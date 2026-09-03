using System;
using Autodesk.AutoCAD.ApplicationServices;
using CADMCP.Plugin;

namespace CADMCP.CommandSet;

// Experiment only: same modification source compiled against a native-command boundary.
// Does NOT turn off undo, clear history or suppress the outer command's undo marker.
internal sealed class StrictUndoBoundary : IDisposable
{
    // The production source now requires dispatcher ownership. Kept build-compatible
    // for historical diagnostics; rebuilt binaries are NOT the original comparison.
    public static void Require(CadCommandContext context) => context.RequireNativeUndoBoundary();
    public Exception? CloseError => null;
    public StrictUndoBoundary(Document document)
    {
        if (!ReferenceEquals(Application.DocumentManager.MdiActiveDocument, document))
            throw new CadCommandException("document_mismatch", "活动文档已改变");
        if (Application.DocumentManager.IsApplicationContext ||
            !string.Equals(document.CommandInProgress, "EXECUTEFUNCTION", StringComparison.OrdinalIgnoreCase))
            throw new CadCommandException("undo_unavailable", "候选实验仅允许已确认的宿主命令边界");
        var control = Convert.ToInt32(Application.GetSystemVariable("UNDOCTL"));
        if ((control & 1) == 0 || (control & 2) != 0 || (control & 8) != 0)
            throw new CadCommandException("undo_unavailable", "需要完整 UNDO 且不能嵌入其他撤销组");
    }
    public void Dispose() { }
}
