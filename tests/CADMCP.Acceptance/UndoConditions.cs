using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CADMCP.AutoCAD.Tests;
using Newtonsoft.Json.Linq;

namespace CADMCP.Acceptance;
public sealed partial class AcceptanceRun
{
    // Destructive UNDO controls are confined to the fresh, explicitly owned test DWG.
    private async Task UndoConditions()
    {
        var originalControl = await UndoControl();
        Check((originalControl & 1) != 0 && (originalControl & 10) == 0, "fresh test drawing starts with full UNDO");
        var names = new[] { "create_line", "create_circle", "create_polyline", "create_text" };
        var items = new[] {
            new JObject { ["start"] = P(100), ["end"] = P(110) },
            new JObject { ["center"] = P(100), ["radius"] = 10 },
            new JObject { ["vertices"] = new JArray(P(100),P(110),P(110,10)) },
            new JObject { ["position"] = P(100), ["content"] = "UNDO CONTROL", ["height"] = 10 }
        };
        try
        {
            foreach (var condition in new[] { "None", "One", "Begin" })
            {
                Phase("UNDO " + condition);
                await NativeUndoControl(condition == "Begin" ? "_Begin" : "_Control _" + condition);
                var control = await UndoControl();
                Check(condition == "None" ? (control & 1) == 0 : condition == "One" ? (control & 2) != 0 : (control & 8) != 0,
                    "requested native UNDO condition active", new { condition, control });
                var before = await Handles(); var state = await State();
                for (var i = 0; i < names.Length; i++)
                {
                    var r = await Call(names[i], Draw("wcs",items[i]));
                    Check(r.Value<string>("errorCode") == "undo_unavailable" && !r.Value<bool>("committed") && !r.Value<bool>("rolledBack") && (await Handles()).SetEquals(before), names[i] + " refused before write in " + condition);
                    Check(JToken.DeepEquals(state,await State()), "rejected fixed request leaves document state " + names[i]);
                }
                foreach (var mode in new[] { "auto", "none" })
                {
                    var args = new JObject { ["transactionMode"] = mode, ["parameters"] = new JObject(), ["code"] = @"
var before=Convert.ToInt32(Application.GetSystemVariable(""UNDOCTL""));
var own=transaction==null;
var tr=transaction ?? database.TransactionManager.StartTransaction();
string handle;
try {
    var space=(BlockTableRecord)tr.GetObject(database.CurrentSpaceId,OpenMode.ForWrite);
    var line=new Line(new Point3d(300,0,7),new Point3d(310,0,7));
    space.AppendEntity(line);tr.AddNewlyCreatedDBObject(line,true);handle=line.Handle.ToString();
    if(own)tr.Commit();
}finally{if(own)tr.Dispose();}
return new {handle,own,before,after=Convert.ToInt32(Application.GetSystemVariable(""UNDOCTL""))};" };
                    var r = await Success("send_code_to_cad", args);
                    var handle = r["result"]!.Value<string>("handle")!;
                    Check(!r.Value<bool>("undoGuaranteed") && r.Value<bool>("committed") == (mode == "auto") && (await Handles()).Contains(handle), "dynamic " + mode + " actually writes despite " + condition);
                    Check(r["result"]!.Value<bool>("own") == (mode == "none") && r["result"]!.Value<int>("before") == r["result"]!.Value<int>("after") && await UndoControl() == control, "dynamic preserves undo configuration " + condition + "/" + mode);
                    var geometry = await LineState(new[] {handle});
                    Check(geometry[0]!["start"]![2]!.Value<double>() == 7, "dynamic physical geometry in " + condition);
                    await RemoveLeftovers(new[] {handle});
                }
                await RestoreFullUndo();
                Check((await UndoControl() & 11) == 1, "test drawing full undo restored after " + condition);
            }
            Phase("user code may own undo marks and a transaction");
            // C# dynamic requires the runtime binder; it need not already be loaded in CAD.
            // Exercise the public additional-reference contract instead of depending on session load order.
            var binderPath = System.IO.Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "Microsoft.CSharp.dll");
            var ownUndo = await Success("send_code_to_cad",new JObject { ["transactionMode"]="none",["parameters"]=new JObject(),["references"]=new JArray(binderPath),["code"]=@"
dynamic app=Application.AcadApplication;
dynamic active=app.ActiveDocument;
active.StartUndoMark();
try {
    using(var own=database.TransactionManager.StartTransaction()) {
        var space=(BlockTableRecord)own.GetObject(database.CurrentSpaceId,OpenMode.ForWrite);
        var line=new Line(new Point3d(500,0,0),new Point3d(510,0,0));
        space.AppendEntity(line);own.AddNewlyCreatedDBObject(line,true);
        var handle=line.Handle.ToString();own.Commit();return new {handle};
    }
}finally{active.EndUndoMark();}" });
            var ownHandle=ownUndo["result"]!.Value<string>("handle")!;
            Check(!ownUndo.Value<bool>("undoGuaranteed") && (await Handles()).Contains(ownHandle), "user-managed marks and transaction allowed");
            await RemoveLeftovers(new[]{ownHandle});
        }
        finally
        {
            // Only restore a completed command's known state. Never retry an unknown command.
            if (!_undoControlUnknown) await RestoreFullUndo();
            else _report["undoControlCleanupSkipped"] = "Native control result unknown; no retry";
        }
    }
    private bool _undoControlUnknown;
    private Task<int> UndoControl() => App(()=>{Ensure();return Convert.ToInt32(Application.GetSystemVariable("UNDOCTL"));});
    private async Task RestoreFullUndo()
    {
        var control=await UndoControl();
        if((control&8)!=0) await NativeUndoControl("_End");
        control=await UndoControl();
        if((control&1)==0) await NativeUndoControl("_All");
        else if((control&2)!=0) await NativeUndoControl("_Control _All");
    }
    private async Task NativeUndoControl(string arguments)
    {
        var ended=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CommandEventHandler end=(_,e)=>{try{if(e.GlobalCommandName.Equals("UNDO",StringComparison.OrdinalIgnoreCase))ended.TrySetResult(true);}catch(Exception error){ended.TrySetException(error);}};
        CommandEventHandler fail=(_,e)=>{try{ended.TrySetException(new InvalidOperationException("Native control failed/cancelled: "+e.GlobalCommandName));}catch(Exception error){ended.TrySetException(error);}};
        try
        {
            await App(()=>{
                Ensure();
                if(!_createdDocuments.Contains(_doc) || ReferenceEquals(_doc,_original))throw new InvalidOperationException("UNDO controls require an owned temporary drawing");
                if(!_doc.Editor.IsQuiescent || !string.IsNullOrWhiteSpace(_doc.CommandInProgress))throw new InvalidOperationException("CAD busy before native control");
                _doc.CommandEnded+=end;_doc.CommandFailed+=fail;_doc.CommandCancelled+=fail;
                _undoControlUnknown=true;
                _doc.SendStringToExecute("_.UNDO "+arguments+" ",true,false,false);return true;
            });
            if(await Task.WhenAny(ended.Task,Task.Delay(15000))!=ended.Task)throw new TimeoutException("Native UNDO control result unknown; no retry");
            await ended.Task;_undoControlUnknown=false;await HostTestDispatch.Ready(_doc);
        }
        finally {await App(()=>{_doc.CommandEnded-=end;_doc.CommandFailed-=fail;_doc.CommandCancelled-=fail;return true;});}
    }
}
