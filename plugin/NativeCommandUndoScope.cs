using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

// No StartUndoMark/EndUndoMark: EXECUTEFUNCTION already supplies the undo unit.
// Created only inside the dispatcher's callback for FixedWrite/formal PreviewableWrite.
internal sealed class NativeCommandUndoScope
{
    private readonly Document _document;
    private readonly ObjectId _space;
    private readonly NativeUndoState _state = new(Environment.CurrentManagedThreadId);

    internal NativeCommandUndoScope(Document document, ObjectId space)
    { _document = document; _space = space; }

    internal void Require(Document document)
    {
        // Check thread/lifetime BEFORE touching any AutoCAD object.
        _state.EnsureActiveThread(Environment.CurrentManagedThreadId);
        var sameDocument = ReferenceEquals(_document, document) &&
            ReferenceEquals(Application.DocumentManager.MdiActiveDocument, _document);
        if (!sameDocument) throw new CadCommandException("document_mismatch", "撤销边界所属文档已改变");
        _state.Claim(Environment.CurrentManagedThreadId, true, _document.Database.CurrentSpaceId == _space,
            Application.DocumentManager.IsApplicationContext, _document.CommandInProgress ?? string.Empty,
            Convert.ToInt32(Application.GetSystemVariable("UNDOCTL")));
    }

    internal void EndCallback() => _state.EndCallback();
    internal void CompleteResponse(JObject? response, Exception? failure) => _state.CompleteResponse(response, failure);
}
