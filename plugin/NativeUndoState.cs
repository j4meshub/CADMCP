using System;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

// Host-independent lifecycle policy. Only the dispatcher owns creation/completion.
// This is an internal correctness capability, NOT a security sandbox for arbitrary C#.
internal sealed class NativeUndoState
{
    private readonly int _threadId;
    private bool _active = true;
    private bool _claimed;
    private bool _completed;

    internal NativeUndoState(int threadId) => _threadId = threadId;

    internal void EnsureActiveThread(int currentThreadId)
    {
        if (!_active || currentThreadId != _threadId)
            throw new CadCommandException("undo_unavailable", "固定写入必须在本次调度器拥有的同步命令回调中执行");
    }

    internal void Claim(int currentThreadId, bool sameDocument, bool sameSpace,
        bool isApplicationContext, string commandName, int undoControl)
    {
        EnsureActiveThread(currentThreadId);
        if (!sameDocument) throw new CadCommandException("document_mismatch", "撤销边界所属文档已改变");
        if (!sameSpace) throw new CadCommandException("active_space_mismatch", "撤销边界所属活动空间已改变");
        if (isApplicationContext || !string.Equals(commandName, "EXECUTEFUNCTION", StringComparison.OrdinalIgnoreCase))
            throw new CadCommandException("undo_unavailable", "当前不是调度器预期的 AutoCAD 命令边界");
        if ((undoControl & 1) == 0 || (undoControl & 2) != 0 || (undoControl & 8) != 0)
            throw new CadCommandException("undo_unavailable", "需要启用完整 UNDO，且不能处于其他撤销组中");
        _claimed = true;
    }

    internal void EndCallback() => _active = false;

    // Commit is not command completion. Publish the undo guarantee only after await
    // ExecuteInCommandContextAsync returns normally; a later selection failure is separate.
    internal void CompleteResponse(JObject? response, Exception? failure)
    {
        if (_completed) return;
        _completed = true;
        if (!_claimed || response?.Value<bool>("committed") != true) return;
        response["undoGuaranteed"] = !_active && failure == null;
        if (response.Value<bool>("undoGuaranteed")) return;
        var warnings = response["warnings"] as JArray;
        if (warnings == null) { warnings = new JArray(); response["warnings"] = warnings; }
        warnings.Add(new JObject
        {
            ["code"] = "undo_completion_failed",
            ["message"] = "数据库已提交，但命令撤销边界未能确认正常结束；不要重复执行写入。" + (failure?.Message ?? "回调仍在执行")
        });
    }
}
