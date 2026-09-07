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
        var managedSelection = false;
        JObject? result = null;
        NativeCommandUndoScope? nativeUndo = null;
        try
        {
            var command = _registry.Get(method);
            var route = CommandExecutionPolicy.Resolve(command.ExecutionKind, parameters);
            managedSelection = CommandExecutionPolicy.PreserveInitialSelection(command.ExecutionKind);

            // Fixed reads/validated previews/selection do NOT enter a CAD command. Do not
            // merge this with the write path: command entry and default write locks can
            // consume an UNDO step even when all entities are opened ForRead.
            if (route != CadExecutionKind.CommandContext)
            {
                var applicationCall = await InApplicationContextAsync(() => ExecuteApplicationCall(command, route, parameters, callId));
                result = applicationCall.Response;
                if (applicationCall.RequestedSelection != null && result.Value<bool>("success"))
                {
                    try
                    {
                        var selectionTask = await InApplicationContextAsync(() => SelectionCommandBridge.ApplyAsync(
                            applicationCall.Document, applicationCall.ActiveSpaceId,
                            applicationCall.InitialSelection, applicationCall.RequestedSelection));
                        await selectionTask;
                    }
                    catch (Exception error)
                    {
                        result = ExecutionResponses.Failure(callId,
                            error is CadCommandException business ? business.Code : "selection_failed", error);
                        if (error is SelectionBridgeRestoreException restore)
                            ((JArray)result["warnings"]!).Add(new JObject
                            { ["code"] = "selection_restore_failed", ["message"] = restore.RestoreError.Message });
                    }
                }
                return Finish(callId, result);
            }
            var dispatch = await InApplicationContextAsync(() => CaptureDispatchState(managedSelection));
            var document = dispatch.Document;
            if (document == null) return Finish(callId, Error(callId, "no_active_document", "AutoCAD 没有活动文档。"));
            if (!dispatch.IsAvailable)
                return Finish(callId, Error(callId, "cad_busy", "AutoCAD 当前正在执行命令: " + dispatch.CommandNames));
            selectionDocument = document;
            selectionSpaceId = dispatch.ActiveSpaceId;
            selectionToRestore = dispatch.Selection;

            Exception? failure = null;
            var failureWarning = "post_commit_callback_failed";
            var finalSelection = dispatch.Selection;
            ObjectId[]? requestedSelection = null;

            await Application.DocumentManager.ExecuteInCommandContextAsync(async _ =>
            {
                try
                {
                    EnsureDispatchDocument(document, dispatch.ActiveSpaceId);
                    // Only formal fixed writes receive strict native undo capability.
                    // Reads/previews never get here; dynamic code stays best-effort.
                    if (CommandExecutionPolicy.RequiresNativeUndo(command.ExecutionKind))
                        nativeUndo = new NativeCommandUndoScope(document, dispatch.ActiveSpaceId);
                    if (!managedSelection) ApplySelectionInCommandContext(document, dispatch.Selection.ObjectIds);
                    var context = new CadCommandContext(document, SettingsStore.Current, callId,
                        dispatch.Selection.ObjectIds, dispatch.Selection.Handles, nativeUndo);
                    result = command.Execute(context, parameters);
                    requestedSelection = context.RequestedSelection?.ToArray();
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    // Nothing, including cleanup, may throw through AutoCAD's native callback.
                    try
                    {
                        finalSelection = managedSelection ? dispatch.Selection : CaptureSelection(document);
                        selectionToRestore = finalSelection;
                    }
                    catch (Exception error) { failure ??= error; }
                    finally { nativeUndo?.EndCallback(); }
                }
                await Task.CompletedTask;
            }, null);
            nativeUndo?.CompleteResponse(result, failure);

            if (failure == null && requestedSelection != null && result?.Value<bool>("success") == true)
            {
                try
                {
                    await ApplySelectionViaBridgeAsync(document, dispatch.ActiveSpaceId, requestedSelection);
                    selectionApplied = true;
                    if (result?["result"] is JObject value && value["selectionApplied"] != null) value["selectionApplied"] = true;
                }
                catch (Exception error) { failure = error; failureWarning = "post_commit_selection_failed"; }
            }
            if (failure != null && !SelectionResponsePolicy.PreserveCommitted(result, failure, failureWarning))
                result = ExecutionResponses.Failure(callId, failure is CadCommandException business ? business.Code : "execution_failed", failure);
            if (managedSelection && !selectionApplied)
            {
                // Validation errors must not leave the command-context side effect of clearing preselection.
                // Report restoration failures instead of silently replacing the previous selection with empty.
                try
                {
                    await ApplySelectionViaBridgeAsync(document, dispatch.ActiveSpaceId, dispatch.Selection.ObjectIds);
                }
                catch (Exception error)
                {
                    result ??= Error(callId, "selection_failed", "选择操作失败");
                    ((JArray)result["warnings"]!).Add(new JObject { ["code"] = "selection_restore_failed", ["message"] = error.Message });
                }
                selectionApplied = true; // Restoration was handled explicitly; do not invoke the silent fallback.
            }
            return Finish(callId, result ?? Error(callId, "empty_response", "命令未返回结果"));
        }
        catch (Exception error)
        {
            // Idempotent: only a not-yet-completed native command can lose its guarantee.
            // Preserve committed handles/mappings even if command finalization fails.
            nativeUndo?.CompleteResponse(result, error);
            if (SelectionResponsePolicy.PreserveCommitted(result, error, "post_commit_warning")) return Finish(callId, result!);
            return Finish(callId, ExecutionResponses.Failure(callId,
                error is CadCommandException business ? business.Code : "dispatch_failed", error));
        }
        finally
        {
            if (selectionDocument != null && !selectionApplied)
            {
                try
                {
                    if (ReferenceEquals(Application.DocumentManager.MdiActiveDocument, selectionDocument) && selectionDocument.Database.CurrentSpaceId == selectionSpaceId)
                        await ApplySelectionViaBridgeAsync(selectionDocument, selectionSpaceId, selectionToRestore.ObjectIds);
                }
                catch { }
            }
            _slot.Release();
        }
    }

    private static ApplicationCallResult ExecuteApplicationCall(ICadCommand command, CadExecutionKind route, JObject parameters, string callId)
    {
        // This entire synchronous callback runs on the AutoCAD application thread. No
        // await/Task.Run between identity capture and read validation. Selection changes
        // are returned as intent and later applied by the flagged document-command bridge.
        var state = CaptureDispatchState(true);
        var document = state.Document;
        if (document == null) return new ApplicationCallResult(Error(callId, "no_active_document", "AutoCAD 没有活动文档。"));
        if (!state.IsAvailable) return new ApplicationCallResult(Error(callId, "cad_busy", "AutoCAD 当前正在执行命令: " + state.CommandNames));
        EnsureDispatchDocument(document, state.ActiveSpaceId);
        var context = new CadCommandContext(document, SettingsStore.Current, callId, state.Selection.ObjectIds, state.Selection.Handles);
        JObject result;
        using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            result = command.Execute(context, parameters);
        // A misclassified future command is a developer error, not permission to hide
        // an already-reported commit and encourage the caller to repeat a write.
        if (result.Value<bool>("committed"))
        {
            SelectionResponsePolicy.PreserveCommitted(result,
                new InvalidOperationException("应用上下文命令错误地报告了数据库提交，请检查 ExecutionKind 声明"), "execution_contract_violation");
            return new ApplicationCallResult(result, document, state.ActiveSpaceId, state.Selection.ObjectIds, null);
        }
        EnsureDispatchDocument(document, state.ActiveSpaceId);
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > 7 * 1024 * 1024)
            return new ApplicationCallResult(Error(callId, "result_too_large", "读取结果过大，请缩小范围或关闭几何详情"));

        if (route == CadExecutionKind.ReadOnly)
        {
            // Never restore/set selection for a normal read: that was the old workaround
            // for command-context preselection clearing and can itself affect history.
            if (context.RequestedSelection != null)
                throw new CadCommandException("execution_contract_violation", "只读命令不得请求选择更新");
            return new ApplicationCallResult(result, document, state.ActiveSpaceId, state.Selection.ObjectIds, null);
        }
        return new ApplicationCallResult(result, document, state.ActiveSpaceId, state.Selection.ObjectIds,
            context.RequestedSelection?.ToArray());
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

    private static void ApplySelectionInCommandContext(Document document, ObjectId[] ids)
    {
        try { document.Editor.SetImpliedSelection(ids.Where(IsUsable).ToArray()); }
        catch { document.Editor.SetImpliedSelection(Array.Empty<ObjectId>()); }
    }

    private static async Task ApplySelectionViaBridgeAsync(Document document, ObjectId activeSpaceId, ObjectId[] ids)
    {
        var selectionTask = await InApplicationContextAsync(() =>
        {
            EnsureDispatchDocument(document, activeSpaceId);
            var expected = CaptureSelection(document, true).ObjectIds;
            return SelectionCommandBridge.ApplyAsync(document, activeSpaceId, expected, ids);
        });
        await selectionTask;
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

    private sealed class ApplicationCallResult
    {
        public ApplicationCallResult(JObject response)
        { Response = response; Document = null!; ActiveSpaceId = ObjectId.Null; InitialSelection = Array.Empty<ObjectId>(); RequestedSelection = null; }
        public ApplicationCallResult(JObject response, Document document, ObjectId activeSpaceId,
            ObjectId[] initialSelection, ObjectId[]? requestedSelection)
        { Response = response; Document = document; ActiveSpaceId = activeSpaceId; InitialSelection = initialSelection; RequestedSelection = requestedSelection; }
        public JObject Response { get; }
        public Document Document { get; }
        public ObjectId ActiveSpaceId { get; }
        public ObjectId[] InitialSelection { get; }
        public ObjectId[]? RequestedSelection { get; }
    }
}
