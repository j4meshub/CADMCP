using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.Plugin;
using CADMCP.CommandSet;
using Newtonsoft.Json.Linq;

namespace CADMCP.Undo.Probes;

public sealed partial class UndoIsolation
{
    private async Task CandidateClone(CommandRegistry registry)
    {
        var row = new JObject { ["scenario"] = "candidate_clone" }; _cases.Add(row);
        var sources = new string[2];
        _phase = "candidate_clone/fixture";
        await Command(() =>
        {
            using (_doc!.LockDocument())
            using (var tr = _doc.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(_space, OpenMode.ForWrite);
                for (int i = 0; i < 2; i++)
                {
                    var l = new Line(new Point3d(-1000200 + i * 10, -1000000, 7), new Point3d(-1000200 + i * 10, -999995, 7));
                    l.SetDatabaseDefaults(_doc.Database); space.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true); sources[i] = l.Handle.ToString();
                }
                tr.Commit();
            }
        });
        row["sourceHandles"] = new JArray(sources);
        var baseline = await SpaceCount(); row["baselineCount"] = baseline;
        var args = await Args(sources[0], 0, 0); args.Remove("operation");
        args["source"]!["handles"] = new JArray(sources); args["expectedCount"] = 2;
        args["displacement"] = JObject.Parse("{x:100,y:20}"); args["selectCreated"] = true;
        var map = (System.Collections.Generic.Dictionary<string, ICadCommand>)typeof(CommandRegistry).GetField("_commands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(registry)!;
        var original = map["clone_entities"]; map["clone_entities"] = new FailingClone();
        JObject fail;
        _phase = "candidate_clone/injected_failure";
        try { fail = await _dispatcher!.ExecuteAsync("clone_entities", args, Guid.NewGuid().ToString()); }
        finally { map["clone_entities"] = original; }
        row["rollbackPassed"] = !fail.Value<bool>("success") && fail.Value<bool>("rolledBack") && await SpaceCount() == baseline;
        row["failureResponse"] = fail;
        if (!row.Value<bool>("rollbackPassed")) throw new Exception("Candidate rollback failed");
        _phase = "candidate_clone/edit1";
        var first = await _dispatcher!.ExecuteAsync("clone_entities", args, Guid.NewGuid().ToString()); row["first"] = first;
        if (!first.Value<bool>("committed")) throw new Exception(first.ToString());
        var firstHandles = first["result"]!["createdHandles"]!.Values<string>().ToArray();
        row["selectionPassed"] = first["result"]!.Value<bool>("selectionApplied");
        var preview = (JObject)args.DeepClone(); preview["dryRun"] = true;
        var pre = await _dispatcher.ExecuteAsync("clone_entities", preview, Guid.NewGuid().ToString());
        row["previewPassed"] = pre.Value<bool>("success") && !pre.Value<bool>("committed") && await SpaceCount() == baseline + 2;
        _phase = "candidate_clone/edit2";
        var second = await _dispatcher.ExecuteAsync("clone_entities", args, Guid.NewGuid().ToString()); row["second"] = second;
        if (!second.Value<bool>("committed")) throw new Exception(second.ToString());
        var secondHandles = second["result"]!["createdHandles"]!.Values<string>().ToArray();
        await _dispatcher.ExecuteAsync("get_current_document_info", new JObject(), Guid.NewGuid().ToString());
        _phase = "candidate_clone/nativeU1"; await NativeUndo();
        row["firstUndoCorrect"] = await SpaceCount() == baseline + 2 && await Existing(firstHandles) == 2 && await Existing(secondHandles) == 0;
        if (!row.Value<bool>("firstUndoCorrect")) throw new Exception("Candidate clone U1 failed");
        _phase = "candidate_clone/nativeU2"; await NativeUndo();
        row["twoUndoPassed"] = await SpaceCount() == baseline && await Existing(firstHandles) == 0 && await Existing(secondHandles) == 0 && await Existing(sources) == 2;
        if (!row.Value<bool>("twoUndoPassed")) throw new Exception("Candidate clone U2 failed");
    }
    private Task<int> SpaceCount() => App(() =>
    {
        Ensure(); using (_doc!.LockDocument(Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Read, null, null, false))
        using (var tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            return ((BlockTableRecord)tr.GetObject(_space, OpenMode.ForRead)).Cast<ObjectId>().Count();
    });
    private Task<int> Existing(string?[] handles) => App(() =>
    {
        Ensure(); return handles.Count(h => { try { var id = Id(h!); return !id.IsErased; } catch { return false; } });
    });
    private sealed class FailingClone : AtomicModifyCommand
    {
        private int _changed;
        public override string Name => "clone_entities";
        protected override bool IsClone => true;
        protected override void TransformEntity(Entity entity, Matrix3d matrix)
        { base.TransformEntity(entity, matrix); if (++_changed == 2) throw new Exception("Candidate injected clone failure"); }
    }
}
