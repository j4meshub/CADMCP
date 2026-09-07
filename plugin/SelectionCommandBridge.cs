using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(CADMCP.Plugin.SelectionCommandBridge))]

namespace CADMCP.Plugin;

// Editor.SetImpliedSelection ends in native acedSSSetFirst. Framework-owned selection
// changes must run as this flagged document command, never directly in application context.
public static class SelectionCommandBridge
{
    internal const string CommandName = "CADMCP_APPLY_SELECTION";
    private static readonly object Gate = new();
    private static PendingSelection? _pending;

    internal static Task ApplyAsync(Document document, ObjectId activeSpaceId,
        ObjectId[] expectedSelection, ObjectId[] requestedSelection)
    {
        if (!AcApp.DocumentManager.IsApplicationContext)
            throw new CadCommandException("selection_context_invalid", "选择桥只能从 AutoCAD 应用上下文排队");
        if (!ReferenceEquals(AcApp.DocumentManager.MdiActiveDocument, document) ||
            document.Database.CurrentSpaceId != activeSpaceId)
            throw new CadCommandException("document_mismatch", "应用选择前活动图纸或空间已改变");
        if (!document.Editor.IsQuiescent || !string.IsNullOrWhiteSpace(document.CommandInProgress))
            throw new CadCommandException("cad_busy", "AutoCAD 当前正在执行命令，无法设置选择");
        if (Convert.ToInt32(AcApp.GetSystemVariable("PICKFIRST")) != 1)
            throw new CadCommandException("selection_unavailable", "PICKFIRST 未开启；CADMCP 不会擅自改变用户设置");

        var current = Capture(document);
        if (!Equivalent(current, expectedSelection))
            throw new CadCommandException("selection_state_changed", "用户选择在请求执行期间已改变，未覆盖当前选择");

        PendingSelection request;
        lock (Gate)
        {
            if (_pending != null)
                throw new CadCommandException("cad_busy", "已有选择桥请求等待 AutoCAD 执行");
            request = new PendingSelection(document, activeSpaceId, current, requestedSelection.ToArray());
            _pending = request;
        }
        try
        {
            // Do not activate a document behind the user's back. The request was just
            // verified as the current, idle document and the command is queued to it.
            request.Subscribe();
            document.SendStringToExecute(CommandName + " ", false, false, false);
            return request.Completion.Task;
        }
        catch
        {
            request.Unsubscribe();
            lock (Gate) if (ReferenceEquals(_pending, request)) _pending = null;
            throw;
        }
    }

    [CommandMethod(CommandName,
        CommandFlags.UsePickSet | CommandFlags.Redraw | CommandFlags.NoUndoMarker |
        CommandFlags.NoHistory | CommandFlags.NoActionRecording | CommandFlags.NoMultiple)]
    public static void ApplyPendingSelection()
    {
        PendingSelection? request;
        lock (Gate) request = _pending;
        if (request == null) return;
        // Never throw through AutoCAD's native command callback. Completion is signaled
        // by CommandEnded/Failed/Cancelled, after the native command has actually ended.
        try { request.Execute(); }
        catch (Exception error) { request.RecordFailure(error); }
    }

    private static ObjectId[] Capture(Document document)
    {
        var implied = document.Editor.SelectImplied();
        return implied.Status == PromptStatus.OK && implied.Value != null
            ? implied.Value.GetObjectIds().Where(IsUsable).ToArray()
            : Array.Empty<ObjectId>();
    }

    private static bool Equivalent(ObjectId[] left, ObjectId[] right) =>
        left.Length == right.Length && !left.Except(right).Any() && !right.Except(left).Any();

    private static bool IsUsable(ObjectId id)
    {
        try { return !id.IsNull && id.IsValid && !id.IsErased; }
        catch { return false; }
    }

    private sealed class PendingSelection
    {
        public PendingSelection(Document document, ObjectId activeSpaceId,
            ObjectId[] expectedSelection, ObjectId[] requestedSelection)
        {
            Document = document;
            ActiveSpaceId = activeSpaceId;
            ExpectedSelection = expectedSelection;
            RequestedSelection = requestedSelection;
        }
        public Document Document { get; }
        public ObjectId ActiveSpaceId { get; }
        public ObjectId[] ExpectedSelection { get; }
        public ObjectId[] RequestedSelection { get; }
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _executed;
        private Exception? _failure;

        public void Subscribe()
        {
            Document.CommandEnded += Ended;
            Document.CommandFailed += Failed;
            Document.CommandCancelled += Failed;
        }

        public void Unsubscribe()
        {
            Document.CommandEnded -= Ended;
            Document.CommandFailed -= Failed;
            Document.CommandCancelled -= Failed;
        }

        public void RecordFailure(Exception error) => _failure = error;

        public void Execute()
        {
            var active = AcApp.DocumentManager.MdiActiveDocument;
            if (!ReferenceEquals(active, Document) || Document.Database.CurrentSpaceId != ActiveSpaceId)
                throw new CadCommandException("document_mismatch", "选择桥执行前活动图纸或空间已改变");
            if (Convert.ToInt32(AcApp.GetSystemVariable("PICKFIRST")) != 1)
                throw new CadCommandException("selection_unavailable", "PICKFIRST 未开启；未改变选择");
            var before = Capture(Document);
            if (!Equivalent(before, ExpectedSelection))
                throw new CadCommandException("selection_state_changed", "用户选择在命令排队期间已改变，未覆盖当前选择");

            ValidateTargets();
            try
            {
                Document.Editor.SetImpliedSelection(RequestedSelection);
                var actual = Capture(Document);
                if (!Equivalent(actual, RequestedSelection))
                    throw new CadCommandException("selection_failed", "CAD 实际选择与请求不一致");
                _executed = true;
            }
            catch (Exception apply)
            {
                // This is reached only for ordinary managed failures. A native access
                // violation is not recoverable; the flagged command context prevents it.
                try
                {
                    Document.Editor.SetImpliedSelection(before);
                    if (!Equivalent(Capture(Document), before))
                        throw new InvalidOperationException("CAD 未恢复完整的原选择集");
                }
                catch (Exception restore)
                {
                    throw new SelectionBridgeRestoreException(apply, restore);
                }
                throw;
            }
        }

        private void Ended(object sender, CommandEventArgs args)
        {
            try
            {
                if (!args.GlobalCommandName.Equals(CommandName, StringComparison.OrdinalIgnoreCase)) return;
                if (_failure != null) Complete(_failure);
                else if (!_executed) Complete(new CadCommandException("selection_failed", "选择桥命令结束但没有应用选择"));
                else Complete(null);
            }
            catch (Exception error) { Complete(error); }
        }

        private void Failed(object sender, CommandEventArgs args)
        {
            try
            {
                if (!args.GlobalCommandName.Equals(CommandName, StringComparison.OrdinalIgnoreCase)) return;
                Complete(_failure ?? new CadCommandException("selection_failed", "选择桥命令失败或被取消"));
            }
            catch (Exception error) { Complete(error); }
        }

        private void Complete(Exception? error)
        {
            Unsubscribe();
            lock (Gate) if (ReferenceEquals(_pending, this)) _pending = null;
            if (error == null) Completion.TrySetResult(true);
            else Completion.TrySetException(error);
        }

        private void ValidateTargets()
        {
            if (RequestedSelection.Length > EntitySelectionRules.MaxHandles ||
                RequestedSelection.Distinct().Count() != RequestedSelection.Length)
                throw new CadCommandException("invalid_parameters", "选择目标必须唯一且不超过 2000 个");
            using (var tr = Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in RequestedSelection)
                {
                    if (!IsUsable(id)) throw new CadCommandException("entity_not_found", "应用选择前实体已失效");
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.OwnerId != ActiveSpaceId)
                        throw new CadCommandException("entity_outside_active_space", "只允许选择当前空间顶层实体");
                    var layer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
                    if (!entity.Visible || layer.IsOff || layer.IsFrozen)
                        throw new CadCommandException("entity_not_selectable", "实体不可见，或图层已关闭/冻结: " + entity.Handle);
                    if (entity is Viewport viewport && viewport.Number == 1)
                        throw new CadCommandException("entity_not_selectable", "不能选择纸空间背景视口: " + entity.Handle);
                }
            }
        }
    }
}

internal sealed class SelectionBridgeRestoreException : Exception
{
    public SelectionBridgeRestoreException(Exception applyError, Exception restoreError)
        : base("选择应用失败: " + applyError.Message + "；原选择恢复也失败: " + restoreError.Message, applyError)
    { ApplyError = applyError; RestoreError = restoreError; }
    public Exception ApplyError { get; }
    public Exception RestoreError { get; }
}
