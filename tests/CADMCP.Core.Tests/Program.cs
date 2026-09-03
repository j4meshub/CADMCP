using System;
using System.Linq;
using CADMCP.Plugin;
using CADMCP.CommandSet;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int _tests;
    private static void Main()
    {
        Equal(new[] { "A", "F", "FFFFFFFFFFFFFFFF" }, new[] { "000a", "f", "ffffffffffffffff" }.Select(EntitySelectionRules.NormalizeHandle).ToArray());
        foreach (var value in new[] { "", "0", "0000", " 1", "1 ", "0x10", "-1", "G", "10000000000000000" })
            Reject("invalid_parameters", () => EntitySelectionRules.NormalizeHandle(value));
        Equal(new[] { "A", "B" }, EntitySelectionRules.NormalizeHandles(new[] { "a", "0A", "b" }));
        Reject("invalid_parameters", () => EntitySelectionRules.NormalizeHandles(Array.Empty<string>()));
        Reject("invalid_parameters", () => EntitySelectionRules.NormalizeHandles(Enumerable.Repeat("A", 2001)));
        Equal(Array.Empty<string>(), EntitySelectionRules.NormalizeHandles(Array.Empty<string>(), true));
        var old = new[] { "A", "B" };
        Equal(new[] { "C" }, EntitySelectionRules.Select("replace", old, new[] { "C" }, 1));
        Equal(new[] { "A", "B", "C" }, EntitySelectionRules.Select("add", old, new[] { "B", "C" }, 3));
        Equal(new[] { "A" }, EntitySelectionRules.Select("remove", old, new[] { "B", "D" }, 1));
        Equal(Array.Empty<string>(), EntitySelectionRules.Select("clear", old, Array.Empty<string>(), 0));
        Reject("count_mismatch", () => EntitySelectionRules.Select("add", old, new[] { "C" }, 1));
        Reject("invalid_parameters", () => EntitySelectionRules.Select("clear", old, new[] { "A" }, null));
        Reject("invalid_parameters", () => EntitySelectionRules.Select("invalid", old, new[] { "A" }, null));
        Reject("invalid_parameters", () => EntitySelectionRules.Select("replace", old, new[] { "A" }, -1));
        Equal(new[] { "A", "B" }, old); // Failed calculations cannot mutate the original selection.
        var settings = new CadMcpSettings();
        settings.EnabledTools["get_selected_entities"] = false;
        settings.EnabledTools.Remove("get_entity_details"); settings.EnabledTools.Remove("set_selection");
        settings.EnabledTools["old_tool"] = true;
        settings.Normalize();
        settings.EnabledTools.Remove("clone_entities"); settings.EnabledTools.Remove("transform_entities"); settings.Normalize();
        Check(settings.EnabledTools.Count == 14 && !settings.IsEnabled("get_selected_entities"), "preserve preferences");
        Check(settings.IsEnabled("get_entity_details") && settings.IsEnabled("set_selection"), "new tools enabled");
        Check(!settings.EnabledTools.ContainsKey("old_tool") && settings.SchemaVersion == CadMcpVersion.SettingsSchemaVersion, "normalize without schema bump");
        Check(settings.IsEnabled("clone_entities") && settings.IsEnabled("transform_entities"), "second batch enabled");
        SecondBatch();
        ExecutionRoutes();
        NativeUndoLifecycle();
        Console.WriteLine($"Passed {_tests} host-independent checks.");
    }
    private static void NativeUndoLifecycle()
    {
        void Claim(NativeUndoState state, int control = 5) => state.Claim(7, true, true, false, "EXECUTEFUNCTION", control);
        JObject Committed() => JObject.Parse("{success:true,committed:true,rolledBack:false,undoGuaranteed:false,result:{createdHandles:['F'],selectionApplied:true},warnings:[]}");
        foreach (var control in new[] { 0, 2, 3, 8, 9, 13 })
            Reject("undo_unavailable", () => Claim(new NativeUndoState(7), control));
        foreach (var control in new[] { 1, 5, 37, 53 })
        {
            var valid = new NativeUndoState(7); Claim(valid, control);
            var response = Committed();
            Check(!response.Value<bool>("undoGuaranteed"), "commit alone is not completion");
            valid.EndCallback(); valid.CompleteResponse(response, null);
            Check(response.Value<bool>("undoGuaranteed"), "native command completes undo guarantee");
            Reject("undo_unavailable", () => Claim(valid));
            SelectionResponsePolicy.PreserveCommitted(response, new Exception("selection failed"), "post_commit_selection_failed");
            valid.CompleteResponse(response, new Exception("later failure"));
            Check(response.Value<bool>("undoGuaranteed") && response.Value<bool>("committed"), "post-command UI failure does not revoke native undo");
        }
        Reject("undo_unavailable", () => new NativeUndoState(7).EnsureActiveThread(8));
        Reject("document_mismatch", () => new NativeUndoState(7).Claim(7, false, true, false, "EXECUTEFUNCTION", 5));
        Reject("active_space_mismatch", () => new NativeUndoState(7).Claim(7, true, false, false, "EXECUTEFUNCTION", 5));
        Reject("undo_unavailable", () => new NativeUndoState(7).Claim(7, true, true, true, "EXECUTEFUNCTION", 5));
        foreach (var name in new[] { "", "LINE", "EXECUTEFUNCTION LINE" })
            Reject("undo_unavailable", () => new NativeUndoState(7).Claim(7, true, true, false, name, 5));
        var expired = new NativeUndoState(7); expired.EndCallback();
        Reject("undo_unavailable", () => Claim(expired));
        var failed = new NativeUndoState(7); Claim(failed); failed.EndCallback();
        var committed = Committed(); failed.CompleteResponse(committed, new Exception("native completion failed"));
        Check(!committed.Value<bool>("undoGuaranteed") && committed.Value<bool>("committed") && committed.Value<bool>("success") && !committed.Value<bool>("rolledBack"), "completion failure preserves actual commit");
        Check(committed["result"]!["createdHandles"]![0]!.Value<string>() == "F" && committed["warnings"]![0]!.Value<string>("code") == "undo_completion_failed", "completion warning retains mapping");
        var unclaimed = new NativeUndoState(7); unclaimed.EndCallback(); var noAuthority = Committed();
        unclaimed.CompleteResponse(noAuthority, null);
        Check(!noAuthority.Value<bool>("undoGuaranteed"), "no claim cannot gain guarantee");
        var rolledBack = new NativeUndoState(7); Claim(rolledBack); rolledBack.EndCallback();
        var failure = JObject.Parse("{success:false,committed:false,rolledBack:true,undoGuaranteed:false,warnings:[]}");
        rolledBack.CompleteResponse(failure, null);
        Check(!failure.Value<bool>("undoGuaranteed") && failure.Value<bool>("rolledBack"), "failed transaction never gets undo guarantee");
        var active = new NativeUndoState(7); Claim(active); var premature = Committed();
        active.CompleteResponse(premature, null);
        Check(!premature.Value<bool>("undoGuaranteed"), "active callback cannot finalize guarantee");
    }
    private static void ExecutionRoutes()
    {
        Check(CommandExecutionPolicy.Resolve(CadExecutionKind.ReadOnly, new JObject()) == CadExecutionKind.ReadOnly, "fixed reads application context");
        Check(CommandExecutionPolicy.Resolve(CadExecutionKind.Selection, new JObject()) == CadExecutionKind.Selection, "selection separate from writes");
        foreach (var args in new[] { new JObject(), JObject.Parse("{transactionMode:'none'}"), JObject.Parse("{dryRun:true,transactionMode:'none'}") })
            Check(CommandExecutionPolicy.Resolve(CadExecutionKind.CommandContext, args) == CadExecutionKind.CommandContext, "dynamic none/dryRun cannot opt into readonly");
        Check(CommandExecutionPolicy.Resolve((CadExecutionKind)999, JObject.Parse("{dryRun:true}")) == CadExecutionKind.CommandContext, "unknown category defaults to command context");
        Check(CommandExecutionPolicy.Resolve(CadExecutionKind.PreviewableWrite, new JObject()) == CadExecutionKind.CommandContext, "previewable writes default to execute");
        Check(CommandExecutionPolicy.Resolve(CadExecutionKind.PreviewableWrite, JObject.Parse("{dryRun:false}")) == CadExecutionKind.CommandContext, "explicit execution");
        Check(CommandExecutionPolicy.Resolve(CadExecutionKind.PreviewableWrite, JObject.Parse("{dryRun:true}")) == CadExecutionKind.ReadOnly, "validated preview readonly");
        foreach (var args in new[] { "{dryRun:'true'}", "{dryRun:1}", "{dryRun:null}", "{dryRun:[]}", "{dryRun:{}}" })
            Reject("invalid_parameters", () => CommandExecutionPolicy.Resolve(CadExecutionKind.PreviewableWrite, JObject.Parse(args)));
        Check(CommandExecutionPolicy.PreserveInitialSelection(CadExecutionKind.Selection), "selection intent managed");
        Check(CommandExecutionPolicy.PreserveInitialSelection(CadExecutionKind.PreviewableWrite), "fixed edits preserve initial selection");
        Check(!CommandExecutionPolicy.PreserveInitialSelection(CadExecutionKind.CommandContext), "dynamic code keeps its selection effects");
        Check(CommandExecutionPolicy.PreserveInitialSelection(CadExecutionKind.FixedWrite), "fixed creation preserves selection");
        foreach (var args in new[] { "{}", "{dryRun:true}", "{dryRun:'true'}", "{transactionMode:'none'}" })
            Check(CommandExecutionPolicy.Resolve(CadExecutionKind.FixedWrite, JObject.Parse(args)) == CadExecutionKind.CommandContext, "creation cannot acquire an undeclared preview route");
        Check(CommandExecutionPolicy.RequiresNativeUndo(CadExecutionKind.FixedWrite), "creation receives strict native undo");
        Check(CommandExecutionPolicy.RequiresNativeUndo(CadExecutionKind.PreviewableWrite), "formal edits receive strict native undo");
        foreach (var kind in new[] { CadExecutionKind.CommandContext, CadExecutionKind.ReadOnly, CadExecutionKind.Selection, (CadExecutionKind)999 })
            Check(!CommandExecutionPolicy.RequiresNativeUndo(kind), "dynamic/other paths do not get strict UNDO prerequisites");
    }
    private static void SecondBatch()
    {
        var info = DocumentInfoValues.Create(@"C:\drawings\A.dwg", @"C:\Temp\A.sv$", true, 21);
        Check(info.Value<string>("fileName") == @"C:\drawings\A.dwg" && info.Value<string>("databaseFileName")!.EndsWith(".sv$"), "autosave path isolation");
        info = DocumentInfoValues.Create("Drawing1.dwg", "Drawing1.dwg", false, 0);
        Check(info.Value<string>("fileName") == "" && !info.Value<bool>("isNamedDrawing") && !info.Value<bool>("isModified"), "untitled drawing");
        Check(DocumentInfoValues.Create("B.dwg", "A.sv$", true, 1).Value<string>("fileName") == "B.dwg", "save as no cache");
        var args = JObject.Parse("{source:{kind:'handles',handles:['a','00A','B']}, expectedCount:2, displacement:{x:10,y:20}}");
        var parsed = ModifyRequest.Parse(args, Array.Empty<string>(), true);
        Equal(new[]{"A","B"}, parsed.Handles); Check(!parsed.DryRun && !parsed.SelectCreated && parsed.CoordinateSystem == "ucs", "write defaults");
        args["source"] = JObject.Parse("{kind:'selection'}");
        Equal(new[]{"C","D"}, ModifyRequest.Parse(args, new[]{"c","D"}, true).Handles);
        Reject("invalid_parameters", () => ModifyRequest.Parse(args, Array.Empty<string>(), true));
        Reject("invalid_parameters", () => ModifyRequest.Parse(args, Enumerable.Repeat("A",2001).ToArray(), true));
        Reject("count_mismatch", () => ModifyRequest.Parse(args, new[]{"A"}, true));
        args.Remove("expectedCount"); Reject("invalid_parameters", () => ModifyRequest.Parse(args, new[]{"A"}, true));
        args["expectedCount"] = 1; args["source"] = JObject.Parse("{kind:'selection', handles:['A']}");
        Reject("invalid_parameters", () => ModifyRequest.Parse(args, new[]{"A"}, true));
        args["source"] = JObject.Parse("{kind:'query'}"); Reject("invalid_parameters", () => ModifyRequest.Parse(args, new[]{"A"}, true));

        double[] Matrix(string json, double mmPerUnit=1) => TransformMath.Local(JObject.Parse(json), x => x/mmPerUnit);
        Near(new[]{11d,22d,33d}, TransformMath.Point(Matrix("{type:'move',displacement:{x:10,y:20,z:30}}"),new[]{1d,2d,3d}));
        foreach(var factor in new[]{1d,1000d,25.4}) Near(new[]{1000/factor,0d,0d},TransformMath.Point(Matrix("{type:'move',displacement:{x:1000,y:0}}",factor),new double[3]));
        Near(new[]{10d,21d,3d},TransformMath.Point(Matrix("{type:'rotate',basePoint:{x:10,y:20,z:3},angle:1.5707963267948966}"),new[]{11d,20d,3d}));
        Near(new[]{12d,24d,9d},TransformMath.Point(Matrix("{type:'scale',basePoint:{x:10,y:20,z:3},factor:2}"),new[]{11d,22d,6d}));
        Near(new[]{-3d,4d,7d},TransformMath.Point(Matrix("{type:'mirror',axisStart:{x:0,y:0},axisEnd:{x:0,y:1}}"),new[]{3d,4d,7d}));
        Near(new[]{4d,3d,7d},TransformMath.Point(Matrix("{type:'mirror',axisStart:{x:0,y:0},axisEnd:{x:1,y:1}}"),new[]{3d,4d,7d}));
        var frame=new double[]{0,-1,0,100, 1,0,0,200, 0,0,1,300, 0,0,0,1};
        var inverse=new double[]{0,1,0,-200, -1,0,0,100, 0,0,1,-300, 0,0,0,1};
        Near(new[]{1d,12d,3d},TransformMath.Point(TransformMath.InFrame(Matrix("{type:'move',displacement:{x:10,y:0}}"),frame,inverse),new[]{1d,2d,3d}));
        var tilt=new double[]{1,0,0,0, 0,0,-1,0, 0,1,0,0, 0,0,0,1};
        var tiltInverse=new double[]{1,0,0,0, 0,0,1,0, 0,-1,0,0, 0,0,0,1};
        Near(new[]{0d,0d,2d},TransformMath.Point(TransformMath.InFrame(Matrix("{type:'rotate',basePoint:{x:0,y:0},angle:1.5707963267948966}"),tilt,tiltInverse),new[]{2d,0d,0d}));
        foreach(var invalid in new[]{"{type:'scale',basePoint:{x:0,y:0},factor:0}","{type:'scale',basePoint:{x:0,y:0},factor:-1}","{type:'mirror',axisStart:{x:0,y:0},axisEnd:{x:0,y:0}}","{type:'mirror',axisStart:{x:0,y:0},axisEnd:{x:1,y:0,z:1}}","{type:'rotate',basePoint:{x:0,y:0}}","{type:'move',displacement:{x:0,y:0},angle:1}"})
            Reject("invalid_parameters",()=>Matrix(invalid));
        Reject("invalid_parameters",()=>ModifyRequest.Number(new JValue(double.NaN),"x"));
        Reject("invalid_parameters",()=>ModifyRequest.Number(new JValue(double.PositiveInfinity),"x"));
        Reject("invalid_parameters",()=>Matrix("{type:'scale',basePoint:{x:1e308,y:0},factor:1e308}"));
        var response=JObject.Parse("{success:true,committed:true,rolledBack:false,result:{createdHandles:['F'],selectionApplied:false},warnings:[]}");
        Check(SelectionResponsePolicy.PreserveCommitted(response,new Exception("selection error"),"post_commit_selection_failed"),"preserve committed");
        Check(response.Value<bool>("success") && response.Value<bool>("committed") && !response.Value<bool>("rolledBack") && response["result"]!["createdHandles"]![0]!.Value<string>()=="F" && ((JArray)response["warnings"]!).Count==1,"selection failure keeps handles");
        Check(!SelectionResponsePolicy.PreserveCommitted(new JObject{["committed"]=false},new Exception(),"x"),"uncommitted failure not suppressed");
    }
    private static void Near(double[] expected, double[] actual) => Check(expected.Length==actual.Length && expected.Zip(actual,(a,b)=>Math.Abs(a-b)<1e-8).All(x=>x),"matrix mismatch");
    private static void Equal(string[] expected, string[] actual) => Check(expected.SequenceEqual(actual), "ordered selection mismatch");
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        _tests++;
    }
    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (CadCommandException error) when (error.Code == code) { _tests++; return; }
        throw new Exception("Expected failure: " + code);
    }
}
