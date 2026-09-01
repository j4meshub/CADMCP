using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;

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
        var selectionToRestore = SelectionSnapshot.Empty;
        try
        {
            var dispatch = await InApplicationContextAsync(CaptureDispatchState);
            var document = dispatch.Document;
            if (document == null) return Finish(callId, Error(callId, "no_active_document", "AutoCAD 没有活动文档。"));
            selectionDocument = document;
            selectionToRestore = dispatch.Selection;
            if (!dispatch.IsAvailable)
                return Finish(callId, Error(callId, "cad_busy", "AutoCAD 当前正在执行命令: " + dispatch.CommandNames));

            JObject? result = null;
            Exception? failure = null;
            var finalSelection = dispatch.Selection;

            await Application.DocumentManager.ExecuteInCommandContextAsync(async _ =>
            {
                try
                {
                    ApplySelection(document, dispatch.Selection.ObjectIds);
                    var context = new CadCommandContext(document, SettingsStore.Current, callId,
                        dispatch.Selection.ObjectIds, dispatch.Selection.Handles);
                    result = _registry.Get(method).Execute(context, parameters);
                }
                catch (Exception error) { failure = error; }
                finally { finalSelection = CaptureSelection(document); selectionToRestore = finalSelection; }
                await Task.CompletedTask;
            }, null);

            if (failure != null) result = ExecutionResponses.Failure(callId, "execution_failed", failure);
            return Finish(callId, result ?? Error(callId, "empty_response", "命令未返回结果"));
        }
        catch (Exception error)
        {
            return Finish(callId, ExecutionResponses.Failure(callId, "dispatch_failed", error));
        }
        finally
        {
            if (selectionDocument != null)
            {
                try
                {
                    await InApplicationContextAsync(() =>
                    {
                        if (ReferenceEquals(Application.DocumentManager.MdiActiveDocument, selectionDocument))
                            ApplySelection(selectionDocument, selectionToRestore.ObjectIds);
                        return true;
                    });
                }
                catch { }
            }
            _slot.Release();
        }
    }

    private static DispatchState CaptureDispatchState()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null) return new DispatchState(null, false, string.Empty, SelectionSnapshot.Empty);
        var commands = document.CommandInProgress ?? string.Empty;
        return new DispatchState(document, document.Editor.IsQuiescent && string.IsNullOrWhiteSpace(commands),
            commands, CaptureSelection(document));
    }

    private static SelectionSnapshot CaptureSelection(Document document)
    {
        try
        {
            var implied = document.Editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null) return SelectionSnapshot.Empty;
            var ids = implied.Value.GetObjectIds().Where(IsUsable).ToArray();
            return new SelectionSnapshot(ids, ids.Select(id => id.Handle.ToString()).ToArray());
        }
        catch { return SelectionSnapshot.Empty; }
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
        { Document = document; IsAvailable = isAvailable; CommandNames = commandNames; Selection = selection; }
        public Document? Document { get; }
        public bool IsAvailable { get; }
        public string CommandNames { get; }
        public SelectionSnapshot Selection { get; }
    }

    private sealed class SelectionSnapshot
    {
        public static readonly SelectionSnapshot Empty = new(Array.Empty<ObjectId>(), Array.Empty<string>());
        public SelectionSnapshot(ObjectId[] objectIds, string[] handles) { ObjectIds = objectIds; Handles = handles; }
        public ObjectId[] ObjectIds { get; }
        public string[] Handles { get; }
    }
}
