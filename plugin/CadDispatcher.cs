using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace CADMCP.Plugin;

public sealed class CadDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly SemaphoreSlim _slot = new(1, 1);

    public CadDispatcher(CommandRegistry registry) => _registry = registry;

    public async Task<JObject> ExecuteAsync(string method, JObject parameters, string callId)
    {
        if (!SettingsStore.Current.IsEnabled(method)) return Disabled(callId);
        if (method == "get_execution_status") return ExecutionStatusStore.Get(parameters.Value<string>("callId") ?? callId);
        if (!_slot.Wait(0)) return Error(callId, "cad_busy", "已有 CAD 调用正在执行");

        var running = new JObject
        {
            ["callId"] = callId, ["status"] = "running", ["stage"] = "dispatch", ["success"] = false,
            ["result"] = null, ["diagnostics"] = new JArray(), ["warnings"] = new JArray(), ["errorCode"] = null,
            ["errorType"] = null, ["message"] = null, ["stackTrace"] = null, ["durationMs"] = null,
            ["transactionMode"] = parameters.Value<string>("transactionMode"), ["committed"] = false,
            ["rolledBack"] = false, ["undoGuaranteed"] = false
        };
        ExecutionStatusStore.Set(callId, running);

        Document? selectionDocument = null;
        var selectionSpaceId = ObjectId.Null;
        var selectionToRestore = SelectionSnapshot.Empty;
        var selectionApplied = false;
        try
        {
            var dispatch = await InApplicationContextAsync(() => CaptureDispatchState(method == "set_selection"));
            var document = dispatch.Document;
            if (document == null) return Finish(callId, Error(callId, "no_active_document", "AutoCAD 没有活动文档。"));
            if (!dispatch.IsAvailable)
                return Finish(callId, Error(callId, "cad_busy", "AutoCAD 当前正在执行命令: " + dispatch.CommandNames));
            selectionDocument = document;
            selectionSpaceId = dispatch.ActiveSpaceId;
            selectionToRestore = dispatch.Selection;

            JObject? result = null;
            Exception? failure = null;
            var finalSelection = dispatch.Selection;
            ObjectId[]? requestedSelection = null;

            await Application.DocumentManager.ExecuteInCommandContextAsync(async _ =>
            {
                try
                {
                    EnsureDispatchDocument(document, dispatch.ActiveSpaceId);
                    if (method != "set_selection") ApplySelection(document, dispatch.Selection.ObjectIds);
                    var context = new CadCommandContext(document, SettingsStore.Current, callId,
                        dispatch.Selection.ObjectIds, dispatch.Selection.Handles);
                    result = _registry.Get(method).Execute(context, parameters);
                    requestedSelection = context.RequestedSelection?.ToArray();
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    finalSelection = CaptureSelection(document);
                    selectionToRestore = method == "set_selection" ? dispatch.Selection : finalSelection;
                }
                await Task.CompletedTask;
            }, null);

            if (failure == null && requestedSelection != null && result?.Value<bool>("success") == true)
            {
                try
                {
                    await InApplicationContextAsync(() =>
                    {
                        EnsureDispatchDocument(document, dispatch.ActiveSpaceId);
                        if (requestedSelection.Any(id => !IsUsable(id)))
                            throw new CadCommandException("entity_not_found", "应用选择前实体已失效");
                        document.Editor.SetImpliedSelection(requestedSelection);
                        var actual = CaptureSelection(document, true);
                        if (actual.ObjectIds.Length != requestedSelection.Length || actual.ObjectIds.Except(requestedSelection).Any())
                            throw new CadCommandException("selection_failed", "CAD 实际选择与请求不一致；尝试恢复原选择");
                        selectionApplied = true;
                        return true;
                    });
                }
                catch (Exception error) { failure = error; }
            }
            if (failure != null) result = ExecutionResponses.Failure(callId, failure is CadCommandException business ? business.Code : "execution_failed", failure);
            if (method == "set_selection" && !selectionApplied)
            {
                // Validation errors must not leave the command-context side effect of clearing preselection.
                // Report restoration failures instead of silently replacing the previous selection with empty.
                try
                {
                    await InApplicationContextAsync(() =>
                    {
                        EnsureDispatchDocument(document, dispatch.ActiveSpaceId);
                        document.Editor.SetImpliedSelection(dispatch.Selection.ObjectIds);
                        var actual = CaptureSelection(document, true);
                        if (actual.ObjectIds.Length != dispatch.Selection.ObjectIds.Length || actual.ObjectIds.Except(dispatch.Selection.ObjectIds).Any())
                            throw new InvalidOperationException("CAD 未恢复完整的原选择集");
                        return true;
                    });
                }
                catch (Exception error)
                {
                    result ??= Error(callId, "selection_failed", "选择操作失败");
                    ((JArray)result["warnings"]!).Add(new JObject { ["code"] = "selection_restore_failed", ["message"] = error.Message });
                }
                selectionApplied = true; // Restoration was handled explicitly; do not invoke the silent fallback.
            }
            if ((method == "get_entity_details" || method == "get_selected_entities" || method == "query_entities") &&
                result != null && Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > 7 * 1024 * 1024)
                result = Error(callId, "result_too_large", "读取结果过大，请缩小范围或关闭几何详情");
            return Finish(callId, result ?? Error(callId, "empty_response", "命令未返回结果"));
        }
        catch (Exception error)
        {
            return Finish(callId, ExecutionResponses.Failure(callId, "dispatch_failed", error));
        }
        finally
        {
            if (selectionDocument != null && !selectionApplied)
            {
                try
                {
                    await InApplicationContextAsync(() =>
                    {
                        if (ReferenceEquals(Application.DocumentManager.MdiActiveDocument, selectionDocument) && selectionDocument.Database.CurrentSpaceId == selectionSpaceId)
                            ApplySelection(selectionDocument, selectionToRestore.ObjectIds);
                        return true;
                    });
                }
                catch { }
            }
            _slot.Release();
        }
    }

    private static void EnsureDispatchDocument(Document document, ObjectId activeSpaceId)
    {
        if (!ReferenceEquals(Application.DocumentManager.MdiActiveDocument, document))
            throw new CadCommandException("document_mismatch", "调度期间活动 DWG 已改变，请重新读取");
        if (document.Database.CurrentSpaceId != activeSpaceId)
            throw new CadCommandException("active_space_mismatch", "调度期间活动空间已改变，请重新读取");
    }

    private static DispatchState CaptureDispatchState(bool strictSelection)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null) return new DispatchState(null, false, string.Empty, SelectionSnapshot.Empty);
        var commands = document.CommandInProgress ?? string.Empty;
        var available = document.Editor.IsQuiescent && string.IsNullOrWhiteSpace(commands);
        return new DispatchState(document, available,
            commands, available ? CaptureSelection(document, strictSelection) : SelectionSnapshot.Empty);
    }

    private static SelectionSnapshot CaptureSelection(Document document, bool strict = false)
    {
        try
        {
            var implied = document.Editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null) return SelectionSnapshot.Empty;
            var ids = implied.Value.GetObjectIds().Where(IsUsable).ToArray();
            return new SelectionSnapshot(ids, ids.Select(id => id.Handle.ToString()).ToArray());
        }
        catch when (!strict) { return SelectionSnapshot.Empty; }
    }

    private static void ApplySelection(Document document, ObjectId[] ids)
    {
        try { document.Editor.SetImpliedSelection(ids.Where(IsUsable).ToArray()); }
        catch { document.Editor.SetImpliedSelection(Array.Empty<ObjectId>()); }
    }

    private static bool IsUsable(ObjectId id)
    {
        try { return !id.IsNull && id.IsValid && !id.IsErased; }
        catch { return false; }
    }

    private static Task<T> InApplicationContextAsync<T>(Func<T> callback)
    {
        if (Application.DocumentManager.IsApplicationContext)
        {
            try { return Task.FromResult(callback()); }
            catch (Exception error) { return Task.FromException<T>(error); }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.DocumentManager.ExecuteInApplicationContext(_ =>
        {
            try { completion.TrySetResult(callback()); }
            catch (Exception error) { completion.TrySetException(error); }
        }, null);
        return completion.Task;
    }

    private static JObject Finish(string id, JObject result) { ExecutionStatusStore.Set(id, result); return result; }
    private static JObject Disabled(string id) => Error(id, "tool_disabled", "此工具已在 CADMCP 设置中禁用");
    private static JObject Error(string id, string code, string message) => new()
    {
        ["callId"] = id, ["status"] = "failed", ["stage"] = "dispatch", ["success"] = false,
        ["result"] = null, ["diagnostics"] = new JArray(), ["warnings"] = new JArray(), ["errorCode"] = code,
        ["errorType"] = null, ["message"] = message, ["stackTrace"] = null, ["durationMs"] = 0,
        ["transactionMode"] = null, ["committed"] = false, ["rolledBack"] = false, ["undoGuaranteed"] = false
    };

    private sealed class DispatchState
    {
        public DispatchState(Document? document, bool isAvailable, string commandNames, SelectionSnapshot selection)
        { Document = document; IsAvailable = isAvailable; CommandNames = commandNames; Selection = selection; ActiveSpaceId = document?.Database.CurrentSpaceId ?? ObjectId.Null; }
        public Document? Document { get; }
        public bool IsAvailable { get; }
        public string CommandNames { get; }
        public SelectionSnapshot Selection { get; }
        public ObjectId ActiveSpaceId { get; }
    }

    private sealed class SelectionSnapshot
    {
        public static readonly SelectionSnapshot Empty = new(Array.Empty<ObjectId>(), Array.Empty<string>());
        public SelectionSnapshot(ObjectId[] objectIds, string[] handles) { ObjectIds = objectIds; Handles = handles; }
        public ObjectId[] ObjectIds { get; }
        public string[] Handles { get; }
    }
}
