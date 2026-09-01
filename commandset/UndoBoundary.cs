using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CADMCP.CommandSet;

internal sealed class UndoBoundary : IDisposable
{
    private dynamic? _acadDocument;
    public UndoBoundary(Document document)
    {
        try { dynamic acadApplication = AcApp.AcadApplication; _acadDocument = acadApplication.ActiveDocument; _acadDocument.StartUndoMark(); IsGuaranteed = true; }
        catch { _acadDocument = null; IsGuaranteed = false; }
    }
    public bool IsGuaranteed { get; }
    public void Dispose()
    {
        if (_acadDocument == null) return;
        try { _acadDocument.EndUndoMark(); } catch { }
        _acadDocument = null;
    }
}
