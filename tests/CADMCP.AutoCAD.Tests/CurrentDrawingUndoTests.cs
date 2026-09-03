using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.CommandSet;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.AutoCAD.Tests;

// Current explicitly designated test DWG only. Never Add/activate/save a document.
// Tests modify only temporary clones; the supplied source entities remain untouched.
public sealed class CurrentDrawingUndoTests
{
    private static bool _running;
    private Document _doc = null!;
    private ObjectId _space;
    private string _token = "", _spaceHandle = "", _file = "", _phase = "launch";
    private readonly object _eventGate = new();
    private string[] _sources = Array.Empty<string>();
    private CadDispatcher _dispatcher = null!;
    private readonly JObject _report = new() { ["status"] = "scheduled", ["calls"] = new JArray(), ["checks"] = new JArray(), ["events"] = new JArray() };

    public string Start(string token, string spaceHandle, string[] sourceHandles)
    {
        if (_running) throw new InvalidOperationException("Current drawing test already running");
        HostTestDispatch.BindHostThread();
        _doc = Application.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No active test drawing");
        _token = token; _space = _doc.Database.CurrentSpaceId; _spaceHandle = spaceHandle; _sources = sourceHandles;
        Ensure();
        if (_space.Handle.ToString() != spaceHandle || sourceHandles.Length == 0) throw new InvalidOperationException("Test identity/selection mismatch");
        _file = Path.Combine(@"D:\A-CADMyDev\2.CADMCP\CADMCP\artifacts", "SECOND_BATCH_BUGFIX2_CURRENT_" + Guid.NewGuid().ToString("N") + ".json");
        _report["document"] = _doc.Name; _report["documentToken"] = token; _report["sources"] = new JArray(_sources);
        _report["startedAtUtc"] = DateTime.UtcNow.ToString("O");
        _running = true; Save(); _ = Run(); return _file;
    }

    private async Task Run()
    {
        try
        {
            await HostTestDispatch.Ready(_doc); // launcher must finish before any test call
            var registry = await App(() => { Ensure(); var r = new CommandRegistry(); r.Load(); return r; });
            _dispatcher = new CadDispatcher(registry);
            await App(() => { Ensure(); _doc.CommandWillStart += Started; _doc.CommandEnded += Ended; return true; });
            _report["status"] = "running";
            _phase = "safety_gate"; Save();
            var beforeSafety = await State(); Exception? caught = null;
            try { await HostTestDispatch.Command(_doc, () => { Ensure(); throw new InvalidOperationException("intentional current-drawing safety failure"); }); }
            catch (Exception error) { caught = error; }
            Check(caught?.InnerException?.Message == "intentional current-drawing safety failure" && JToken.DeepEquals(beforeSafety, await State()), "native callback exception contained; no database modification");
            var baseline = await Handles(); var original = await Details(_sources);
            await Select(_sources);

            _phase = "two_clone_units"; Save();
            var firstArgs = Clone(_sources, 100000); firstArgs["source"] = new JObject { ["kind"] = "selection" };
            var first = await Write("clone_entities", firstArgs); var firstCopies = Copies(first);
            await Reads(_sources);
            var secondArgs = Clone(_sources, 200000); secondArgs["selectCreated"] = true;
            var second = await Write("clone_entities", secondArgs); var secondCopies = Copies(second);
            Check(second["result"]!.Value<bool>("selectionApplied"), "selectCreated applied");
            await Reads(secondCopies);
            await Undo("two clones / U1");
            var existing = await Handles();
            Check(firstCopies.All(existing.Contains) && !secondCopies.Any(existing.Contains), "first U removes only second clone batch");
            await Undo("two clones / U2");
            Check((await Handles()).SetEquals(baseline), "second U removes first clone batch");
            Check(JToken.DeepEquals(original, await Details(_sources)), "original entities unchanged after clones");
            await Select(_sources);

            _phase = "temporary_copy_transform_units"; Save();
            var fixture = Copies(await Write("clone_entities", Clone(_sources, 100000)));
            var initial = await Details(fixture);
            var states = new List<JArray>();
            for (var i = 0; i < 3; i++)
            {
                states.Add(await Details(fixture));
                await Write("transform_entities", Transform(fixture, JObject.Parse("{type:'move',displacement:{x:100,y:200}}")));
                Check(!JToken.DeepEquals(states[i], await Details(fixture)), "move changes temporary geometry " + i);
                await Reads(fixture);
                await Select(fixture); await Select(Array.Empty<string>()); await Select(_sources);
            }
            for (var i = 2; i >= 0; i--)
            { await Undo("three moves / U" + (3 - i)); Check(JToken.DeepEquals(states[i], await Details(fixture)), "one U restores exact move state " + i); }
            foreach (var op in new[] {
                "{type:'rotate',basePoint:{x:1240000,y:-1260000},angle:0.3}",
                "{type:'scale',basePoint:{x:1240000,y:-1260000},factor:1.1}",
                "{type:'mirror',axisStart:{x:1250000,y:-1300000},axisEnd:{x:1250000,y:-1200000}}" })
            {
                var operation = JObject.Parse(op); _phase = "temporary_" + operation.Value<string>("type"); Save();
                var beforeSetting = await App(() => { Ensure(); return Convert.ToInt32(Application.GetSystemVariable("MIRRTEXT")); });
                await Write("transform_entities", Transform(fixture, operation));
                await Reads(fixture);
                Check(beforeSetting == await App(() => { Ensure(); return Convert.ToInt32(Application.GetSystemVariable("MIRRTEXT")); }), "MIRRTEXT unchanged " + operation.Value<string>("type"));
                await Undo(_phase + " / U");
                Check(JToken.DeepEquals(initial, await Details(fixture)), "one U restores " + operation.Value<string>("type"));
            }
            await Undo("remove temporary clone fixture");
            Check((await Handles()).SetEquals(baseline), "temporary transform copies removed");

            var commands = (Dictionary<string, ICadCommand>)typeof(CommandRegistry).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
            var realClone = commands["clone_entities"];
            _phase = "rollback_and_selection_failure"; Save();
            try
            {
                commands["clone_entities"] = new FailingClone();
                var failed = await Call("clone_entities", Clone(_sources, 100000), false);
                Check(!failed.Value<bool>("committed") && failed.Value<bool>("rolledBack") && (await Handles()).SetEquals(baseline), "injected second-copy failure rolls back whole batch");
                commands["clone_entities"] = new BadSelectionClone();
                var success = await Write("clone_entities", Clone(_sources, 100000));
                Check(((JArray)success["warnings"]!).OfType<JObject>().Any(w => w.Value<string>("code") == "post_commit_selection_failed") && Copies(success).Length == _sources.Length, "selection failure keeps committed handles and undo guarantee");
                await Undo("selection failure clone / U");
                Check((await Handles()).SetEquals(baseline), "one U removes clone despite selection failure");
            }
            finally { commands["clone_entities"] = realClone; }
            await Select(_sources);
            Check(JToken.DeepEquals(original, await Details(_sources)), "original entity details unchanged at end");
            Check((await Call("get_selected_entities", new JObject()))["result"]!["items"]!.Values<JObject>().Select(i => i!.Value<string>("handle")).ToHashSet().SetEquals(_sources), "original selection restored");
            _report["finalState"] = await State(); _report["status"] = "completed";
        }
        catch (Exception error) { _report["status"] = "failed"; _report["error"] = error.ToString(); }
        finally
        {
            try { await App(() => { _doc.CommandWillStart -= Started; _doc.CommandEnded -= Ended; return true; }); }
            catch (Exception error) { _report["cleanupWarning"] = error.ToString(); }
            _report["finishedAtUtc"] = DateTime.UtcNow.ToString("O"); _running = false;
            try { Save(); } catch { AppDomain.CurrentDomain.SetData("CADMCP.CurrentDrawingUndoTests.Result", _report.ToString()); }
        }
    }

    private void Ensure() { HostTestDispatch.EnsureCurrent(_doc); if (DocumentIdentity.GetToken(_doc) != _token || _doc.Database.CurrentSpaceId != _space) throw new InvalidOperationException("Current test identity changed; stopped"); }
    private Task<T> App<T>(Func<T> action) => ExecutionRegressionTests.InApplication(action);
    private JObject Identity() => new() { ["documentToken"] = _token, ["activeSpaceHandle"] = _spaceHandle };
    private JObject Targets(string[] h) { var p = Identity(); p["source"] = new JObject { ["kind"] = "handles", ["handles"] = new JArray(h) }; p["expectedCount"] = h.Length; p["coordinateSystem"] = "wcs"; return p; }
    private JObject Clone(string[] h, double dx) { var p = Targets(h); p["displacement"] = new JObject { ["x"] = dx, ["y"] = 0 }; return p; }
    private JObject Transform(string[] h, JObject op) { var p = Targets(h); p["operation"] = op; return p; }
    private static string[] Copies(JObject r) => r["result"]!["createdHandles"]!.Values<string>().Select(h => h ?? throw new InvalidOperationException("Null copy handle")).ToArray();
    private async Task<JObject> Call(string name, JObject args, bool expectSuccess = true)
    {
        await App(() => { Ensure(); return true; });
        var r = await _dispatcher.ExecuteAsync(name, args, Guid.NewGuid().ToString());
        ((JArray)_report["calls"]!).Add(new JObject { ["phase"] = _phase, ["tool"] = name, ["args"] = args.DeepClone(), ["response"] = r.DeepClone(), ["state"] = await State() }); Save();
        if (r.Value<bool>("success") != expectSuccess) throw new InvalidOperationException(name + ": " + r);
        return r;
    }
    private async Task<JObject> Write(string name, JObject args) { var r = await Call(name, args); Check(r.Value<bool>("committed") && r.Value<bool>("undoGuaranteed"), name + " commit and native undo guarantee"); return r; }
    private async Task<JArray> Details(string[] h) { var p = Identity(); p["handles"] = new JArray(h); return (JArray)(await Call("get_entity_details", p))["result"]!["items"]!.DeepClone(); }
    private async Task Select(string[] h) { var p = Identity(); p["mode"] = h.Length == 0 ? "clear" : "replace"; p["handles"] = new JArray(h); p["expectedCount"] = h.Length; await Call("set_selection", p); }
    private async Task Reads(string[] h)
    {
        var geometry = await Details(h); var state = await State();
        foreach (var name in new[] { "say_hello", "get_current_document_info", "get_selected_entities", "query_entities" }) await Call(name, name == "query_entities" ? new JObject { ["limit"] = 1 } : new JObject());
        var c = Clone(h, 10); c["dryRun"] = true; await Call("clone_entities", c);
        var t = Transform(h, JObject.Parse("{type:'move',displacement:{x:10,y:20}}")); t["dryRun"] = true; await Call("transform_entities", t);
        c["expectedCount"] = h.Length + 1; await Call("clone_entities", c, false);
        Check(JToken.DeepEquals(geometry, await Details(h)) && JToken.DeepEquals(state, await State()), "reads/previews preserve exact geometry and DBMOD");
    }
    private Task<HashSet<string>> Handles() => App(() => { Ensure(); using (_doc.LockDocument(DocumentLockMode.Read, null, null, false)) using (var tr = _doc.Database.TransactionManager.StartOpenCloseTransaction()) return new HashSet<string>(((BlockTableRecord)tr.GetObject(_space, OpenMode.ForRead)).Cast<ObjectId>().Where(id => !id.IsErased).Select(id => id.Handle.ToString())); });
    private Task<JObject> State() => App(() => { Ensure(); return new JObject { ["dbmod"] = Convert.ToInt32(Application.GetSystemVariable("DBMOD")), ["document"] = _doc.Name, ["space"] = _space.Handle.ToString() }; });
    private async Task Undo(string label) { _phase = label; Save(); await App(() => { Ensure(); return true; }); await HostTestDispatch.Undo(_doc); Save(); }
    private void Check(bool value, string name) { ((JArray)_report["checks"]!).Add(new JObject { ["phase"] = _phase, ["test"] = name, ["passed"] = value }); Save(); if (!value) throw new InvalidOperationException("FAILED: " + name); }
    private void Started(object sender, CommandEventArgs e) => Event("start", e);
    private void Ended(object sender, CommandEventArgs e) => Event("end", e);
    private void Event(string kind, CommandEventArgs e) { try { lock (_eventGate) ((JArray)_report["events"]!).Add(new JObject { ["phase"] = _phase, ["kind"] = kind, ["name"] = e.GlobalCommandName, ["thread"] = Environment.CurrentManagedThreadId }); } catch { } }
    private void Save() { lock (_eventGate) { _report["phase"] = _phase; File.WriteAllText(_file, _report.ToString()); } }
    private sealed class FailingClone : AtomicModifyCommand
    { private int _changed; public override string Name => "clone_entities"; protected override bool IsClone => true; protected override void TransformEntity(Entity e, Matrix3d m) { base.TransformEntity(e, m); if (++_changed == 2) throw new InvalidOperationException("Injected second-copy failure"); } }
    private sealed class BadSelectionClone : ICadCommand
    { public string Name => "clone_entities"; public CadExecutionKind ExecutionKind => CadExecutionKind.PreviewableWrite; public JObject Execute(CadCommandContext c, JObject p) { var r = new CloneEntitiesCommand().Execute(c, p); if (r.Value<bool>("committed")) c.RequestSelection(new[] { ObjectId.Null }); return r; } }
}
