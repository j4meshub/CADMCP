using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CADMCP.CommandSet;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;
using Exception = System.Exception;

namespace CADMCP.AutoCAD.Tests;

// Not shipped in the Bundle. NETLOAD after installing the matching product build.
// The test creates its OWN drawing, never saves it, and does not close any user drawing.
public sealed class SecondBatchHostTests
{
    // Test-only bridge: permits MCP to read evidence after the async Session entry returns.
    public const string ResultKey = "CADMCP.SecondBatchHostTests.Result";
    private static bool _running;
    [CommandMethod("CADMCP_TEST_SECOND_BATCH", CommandFlags.Session)]
    public void Run()
    {
        if (_running) return; _running = true;
        HostTestDispatch.BindHostThread();
        _ = RunSafely();
    }
    private static async Task RunSafely()
    {
        try { await RunTests(); }
        catch (Exception error)
        {
            // Includes failures in result reporting/cleanup. Never an async-void escape.
            AppDomain.CurrentDomain.SetData(ResultKey, new JObject { ["status"] = "failed", ["error"] = error.ToString() }.ToString());
        }
        finally { _running = false; }
    }
    private static async Task RunTests()
    {
        AppDomain.CurrentDomain.SetData(ResultKey, new JObject { ["status"] = "running", ["startedAtUtc"] = DateTime.UtcNow.ToString("O") }.ToString());
        var original = Application.DocumentManager.MdiActiveDocument;
        Document? testDocument = null; var checks = new JArray();
        try
        {
            // Let the launching Session command return before testing the production cad_busy guard.
            if (original == null) throw new Exception("Open a drawing before launching the test");
            await HostTestDispatch.Ready(original);
            testDocument = await ExecutionRegressionTests.InApplication(() =>
            {
                HostTestDispatch.EnsureCurrent(original);
                var created = Application.DocumentManager.Add("acadiso.dwt");
                Application.DocumentManager.MdiActiveDocument = created;
                return created;
            });
            await HostTestDispatch.Ready(testDocument);
            var doc = testDocument;
            var registry = new CommandRegistry(); registry.Load();
            var dispatcher = new CadDispatcher(registry);
            var commands = (Dictionary<string, ICadCommand>)typeof(CommandRegistry).GetField("_commands", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!;
            string[] handles = Array.Empty<string>();
            await InCommand(doc, () => handles = CreateFixture(doc));
            var identity = await ExecutionRegressionTests.InApplication(() => new JObject { ["documentToken"] = DocumentIdentity.GetToken(doc), ["activeSpaceHandle"] = doc.Database.CurrentSpaceId.Handle.ToString() });
            JObject Args(string[] ids, bool clone, bool dryRun = false)
            {
                var p = (JObject)identity.DeepClone(); p["source"] = new JObject { ["kind"] = "handles", ["handles"] = new JArray(ids) };
                p["expectedCount"] = ids.Length; p["coordinateSystem"] = "wcs"; p["dryRun"] = dryRun;
                if (clone) p["displacement"] = new JObject { ["x"] = 100, ["y"] = 20 };
                else p["operation"] = JObject.Parse("{type:'move',displacement:{x:10,y:0}}");
                return p;
            }
            async Task<JObject> Call(string name, JObject p) => await dispatcher.ExecuteAsync(name, p, Guid.NewGuid().ToString());
            void Check(bool ok, string name)
            { checks.Add(new JObject { ["test"] = name, ["passed"] = ok }); if (!ok) throw new Exception("FAILED: " + name); }
            var pair = handles.Take(2).ToArray();
            // Negative safety gates: direct Execute has no framework-owned write scope;
            // a callback exception must return a managed failure, never crash AutoCAD.
            var safetyBefore = await State(doc);
            JObject? direct = null;
            await InCommand(doc, () => direct = new TransformEntitiesCommand().Execute(
                new CadCommandContext(doc, SettingsStore.Current, Guid.NewGuid().ToString(), Array.Empty<ObjectId>(), Array.Empty<string>()), Args(pair, false)));
            Check(direct?.Value<string>("errorCode") == "undo_unavailable" && safetyBefore == await State(doc), "direct fixed edit rejected without modification");
            Exception? safeFailure = null;
            try { await InCommand(doc, () => throw new InvalidOperationException("intentional safety failure")); }
            catch (Exception error) { safeFailure = error; }
            Check(safeFailure?.InnerException?.Message == "intentional safety failure" && safetyBefore == await State(doc), "native callback exception safely contained");
            var baseline = await State(doc);
            var preview = await Call("clone_entities", Args(pair, true, true));
            var afterPreview = await State(doc);
            Check(preview.Value<bool>("success") && !preview.Value<bool>("committed") && baseline == afterPreview, "dryRun does not modify DBMOD/entity count");
            var invalid = Args(pair, true); invalid["expectedCount"] = 1;
            var rejected = await Call("clone_entities", invalid);
            Check(rejected.Value<string>("errorCode") == "count_mismatch" && baseline == await State(doc), "count mismatch zero modification");

            // Inject a deterministic failure AFTER the second transform to exercise the real rollback path.
            var realClone = commands["clone_entities"]; commands["clone_entities"] = new FaultyClone();
            try
            {
                var failure = await Call("clone_entities", Args(pair, true));
                Check(!failure.Value<bool>("success") && failure.Value<bool>("rolledBack") && (await State(doc)).Count == baseline.Count, "mid-clone exception rolls back all copies");
            }
            finally { commands["clone_entities"] = realClone; }

            var cloneArgs = Args(pair, true); cloneArgs["selectCreated"] = true;
            var clone = await Call("clone_entities", cloneArgs);
            Check(clone.Value<bool>("success") && clone.Value<bool>("committed") && clone.Value<bool>("undoGuaranteed"), "clone commit and undo boundary");
            Check(clone["result"]!.Value<bool>("selectionApplied") && ((JArray)clone["result"]!["handleMapping"]!).Count == pair.Length, "mapping and selectCreated");
            var selected = await Call("get_selected_entities", new JObject());
            Check(selected["result"]!.Value<int>("count") == pair.Length, "selection survives dispatcher");
            await HostTestDispatch.Undo(doc);
            Check((await State(doc)).Count == baseline.Count, "one UNDO removes complete clone batch");

            foreach (var op in new[] { "{type:'move',displacement:{x:10,y:20}}", "{type:'rotate',basePoint:{x:0,y:0},angle:1.5707963267948966}", "{type:'scale',basePoint:{x:0,y:0},factor:2}", "{type:'mirror',axisStart:{x:0,y:0},axisEnd:{x:0,y:1}}" })
            {
                var p = Args(pair, false); p["operation"] = JObject.Parse(op);
                var r = await Call("transform_entities", p);
                Check(r.Value<bool>("success") && r.Value<bool>("committed") && r.Value<bool>("undoGuaranteed"), "transform " + p["operation"]!.Value<string>("type"));
                await HostTestDispatch.Undo(doc);
            }
            // Exercise real post-commit selection failure policy with an invalid selection intent.
            commands["clone_entities"] = new InvalidSelectionClone();
            try
            {
                var r = await Call("clone_entities", Args(pair, true));
                Check(r.Value<bool>("success") && r.Value<bool>("committed") && ((JArray)r["warnings"]!).OfType<JObject>().Any(w => w.Value<string>("code") == "post_commit_selection_failed"), "selection failure preserves committed clone and warning");
                Check(((JArray)r["result"]!["createdHandles"]!).Count == 2, "selection failure retains created handles");
                await HostTestDispatch.Undo(doc);
            }
            finally { commands["clone_entities"] = realClone; }
            foreach (var size in new[] { 200, 1000, 2000 })
            {
                var r = await Call("clone_entities", Args(handles.Take(size).ToArray(), true));
                Check(r.Value<bool>("success") && r["result"]!.Value<int>("count") == size, "bulk clone " + size);
                await HostTestDispatch.Undo(doc);
                Check((await State(doc)).Count == baseline.Count, "bulk one UNDO " + size);
            }
            await ExecutionRegressionTests.Run(doc, dispatcher, registry, identity, pair, checks);
        }
        catch (Exception error) { checks.Add(new JObject { ["error"] = error.ToString() }); }
        finally
        {
            var testName = await ExecutionRegressionTests.InApplication(() => testDocument?.Name);
            AppDomain.CurrentDomain.SetData(ResultKey, new JObject
            {
                ["status"] = checks.OfType<JObject>().Any(c => c["error"] != null || c.Value<bool?>("passed") == false) ? "failed" : "completed",
                ["finishedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["testDocument"] = testName,
                ["checks"] = checks.DeepClone()
            }.ToString());
            // Leave the independent test drawing open for inspection; never save or close originals.
            await ExecutionRegressionTests.InApplication(() =>
            {
                (testDocument ?? original)?.Editor.WriteMessage("\nCADMCP second-batch host tests:\n" + checks.ToString() + "\n测试图未保存；检查后可手动关闭。\n");
                return true;
            });
        }
    }
    private static Task InCommand(Document doc, Action action) =>
        HostTestDispatch.Command(doc, () => { using (doc.LockDocument()) action(); });
    private static async Task<(int Count, int Dbmod)> State(Document doc)
    {
        var count = 0; var dbmod = 0;
        await ExecutionRegressionTests.InApplication(() =>
        {
            if (!ReferenceEquals(doc, Application.DocumentManager.MdiActiveDocument)) throw new Exception("Active test drawing changed");
            using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction()) count = ((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>().Count();
            dbmod = Convert.ToInt32(Application.GetSystemVariable("DBMOD"));
            return true;
        });
        return (count, dbmod);
    }
    private static string[] CreateFixture(Document doc)
    {
        var handles = new List<string>(); var db = doc.Database;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            for (var i = 0; i < 2000; i++)
            {
                var line = new Line(new Point3d(i, 0, 7), new Point3d(i, 5, 7)); line.SetDatabaseDefaults(db);
                space.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true); handles.Add(line.Handle.ToString());
            }
            tr.Commit();
        }
        return handles.ToArray();
    }
    private sealed class FaultyClone : AtomicModifyCommand
    {
        private int _changed;
        public override string Name => "clone_entities";
        protected override bool IsClone => true;
        protected override void TransformEntity(Entity entity, Matrix3d matrix)
        { base.TransformEntity(entity, matrix); if (++_changed == 2) throw new Exception("Injected after second entity"); }
    }
    private sealed class InvalidSelectionClone : ICadCommand
    {
        public string Name => "clone_entities";
        public CadExecutionKind ExecutionKind => CadExecutionKind.PreviewableWrite;
        public JObject Execute(CadCommandContext context, JObject parameters)
        { var r = new CloneEntitiesCommand().Execute(context, parameters); if (r.Value<bool>("committed")) context.RequestSelection(new[] { ObjectId.Null }); return r; }
    }
}
