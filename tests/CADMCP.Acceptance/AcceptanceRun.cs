using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.AutoCAD.Tests;
using CADMCP.CommandSet;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.Acceptance;

// Diagnostic assembly only, never shipped. Each group owns a fresh unsaved drawing.
public sealed partial class AcceptanceRun
{
    private static bool _running;
    private Document _original = null!, _doc = null!;
    private string _token = "", _spaceHandle = "", _path = "", _phase = "start";
    private ObjectId _space;
    private CadDispatcher _dispatcher = null!;
    private CommandRegistry _registry = null!;
    private readonly JObject _report = new() { ["status"] = "scheduled", ["checks"] = new JArray(), ["calls"] = new JArray(), ["events"] = new JArray() };
    private readonly object _events = new();
    private readonly List<Document> _createdDocuments = new();

    public string Start(string expectedOriginalToken, string group)
    {
        if (_running) throw new InvalidOperationException("Acceptance already running");
        HostTestDispatch.BindHostThread();
        _original = Application.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No source drawing");
        if (DocumentIdentity.GetToken(_original) != expectedOriginalToken) throw new InvalidOperationException("Original document mismatch");
        if (group != "bulk" && group != "complex" && group != "environment" && group != "environment_followup" && group != "undo_policy" && group != "undo_conditions") throw new ArgumentException("Unknown group");
        _path = Path.Combine(@"D:\A-CADMyDev\2.CADMCP\CADMCP\artifacts", "ACCEPTANCE_" + group + "_" + Guid.NewGuid().ToString("N") + ".json");
        _report["group"] = group; _report["originalDocument"] = _original.Name; _report["startedAtUtc"] = DateTime.UtcNow.ToString("O");
        _running = true; Save(); _ = Run(group); return _path;
    }

    private async Task Run(string group)
    {
        JObject? originalState = null;
        try
        {
            await HostTestDispatch.Ready(_original);
            originalState = await App(() => DocumentState(_original)); _report["originalBefore"] = originalState;
            _doc = await App(() =>
            {
                HostTestDispatch.EnsureCurrent(_original);
                var fresh = Application.DocumentManager.Add("acadiso.dwt"); _createdDocuments.Add(fresh);
                Application.DocumentManager.MdiActiveDocument = fresh; return fresh;
            });
            await HostTestDispatch.Ready(_doc);
            await App(() => { RefreshIdentity(); _doc.CommandWillStart += Started; _doc.CommandEnded += Ended; _registry = new CommandRegistry(); _registry.Load(); return true; });
            _dispatcher = new CadDispatcher(_registry);
            _report["testDocument"] = await App(() => _doc.Name); _report["testToken"] = _token; _report["status"] = "running";
            Phase("safety and activation");
            var before = await State(); Exception? safe = null;
            try { await Cmd(() => throw new InvalidOperationException("intentional safe callback exception")); } catch (Exception e) { safe = e; }
            Check(safe?.InnerException?.Message == "intentional safe callback exception" && JToken.DeepEquals(before, await State()), "active test drawing and safe callback failure");
            if (group == "bulk") await Bulk();
            else if (group == "complex") await Complex();
            else if (group == "environment") await EnvironmentCases();
            else if (group == "environment_followup") { await DocumentAndSpaceCases(); await LegacyAndDynamicCases(); }
            else if (group == "undo_policy") await UndoPolicyCases();
            else if (group == "undo_conditions") await UndoConditions();
            else throw new InvalidOperationException("Group not present in this staged test assembly");
            _report["status"] = ((JArray)_report["checks"]!).OfType<JObject>().Any(c => c.Value<bool?>("passed") == false) ? "failed" : "completed";
        }
        catch (Exception error) { _report["status"] = "failed"; _report["error"] = error.ToString(); }
        finally
        {
            try
            {
                await App(() =>
                {
                    if (_doc != null) { _doc.CommandWillStart -= Started; _doc.CommandEnded -= Ended; }
                    _report["unsavedTestDocuments"] = new JArray(_createdDocuments.Select(d => d.Name));
                    Application.DocumentManager.MdiActiveDocument = _original; return true;
                });
                await HostTestDispatch.Ready(_original);
                // Switching drawings can clear PICKFIRST even if the tools preserve it.
                // Restore through the real selection path; retain pre-restoration evidence.
                var beforeRestore = await App(() => DocumentState(_original));
                _report["originalBeforeSelectionRestore"] = beforeRestore;
                if (originalState != null && _dispatcher != null && !JToken.DeepEquals(originalState["selection"], beforeRestore["selection"]))
                {
                    var saved = (JArray)originalState["selection"]!;
                    var restore = await _dispatcher.ExecuteAsync("set_selection", new JObject {
                        ["documentToken"] = originalState["token"], ["activeSpaceHandle"] = originalState["space"],
                        ["handles"] = saved.DeepClone(), ["mode"] = saved.Count == 0 ? "clear" : "replace", ["expectedCount"] = saved.Count
                    }, Guid.NewGuid().ToString());
                    _report["originalSelectionRestore"] = restore;
                    if (!restore.Value<bool>("success")) throw new InvalidOperationException("Original selection restore failed: " + restore);
                }
                var after = await App(() => DocumentState(_original)); _report["originalAfter"] = after;
                _report["originalStateUnchanged"] = JToken.DeepEquals(originalState, after);
            }
            catch (Exception error) { _report["restoreError"] = error.ToString(); }
            _report["finishedAtUtc"] = DateTime.UtcNow.ToString("O"); _running = false;
            try { Save(); } catch { AppDomain.CurrentDomain.SetData("CADMCP.Acceptance.Result", _report.ToString()); }
        }
    }
    private void Ensure() { HostTestDispatch.EnsureCurrent(_doc); if (DocumentIdentity.GetToken(_doc) != _token || _doc.Database.CurrentSpaceId != _space) throw new InvalidOperationException("Test document/space changed"); }
    private void RefreshIdentity() { HostTestDispatch.EnsureCurrent(_doc); _token = DocumentIdentity.GetToken(_doc); _space = _doc.Database.CurrentSpaceId; _spaceHandle = _space.Handle.ToString(); }
    private Task<T> App<T>(Func<T> action) => ExecutionRegressionTests.InApplication(action);
    private Task Cmd(Action action) => HostTestDispatch.Command(_doc, () => { Ensure(); action(); });
    private Task<T> Read<T>(Func<Transaction, T> action) => App(() => { Ensure(); using (_doc.LockDocument(DocumentLockMode.Read, null, null, false)) using (var tr = _doc.Database.TransactionManager.StartOpenCloseTransaction()) return action(tr); });
    private void Transaction(Action<Transaction, BlockTableRecord> action) { using (_doc.LockDocument(DocumentLockMode.Write, null, null, false)) using (var tr = _doc.Database.TransactionManager.StartTransaction()) { action(tr, (BlockTableRecord)tr.GetObject(_space, OpenMode.ForWrite)); tr.Commit(); } }
    private ObjectId Id(string handle) => _doc.Database.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
    private static JObject DocumentState(Document doc)
    {
        HostTestDispatch.EnsureCurrent(doc);
        using (doc.LockDocument(DocumentLockMode.Read, null, null, false)) using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var selected = doc.Editor.SelectImplied();
            return new JObject { ["name"] = doc.Name, ["token"] = DocumentIdentity.GetToken(doc), ["space"] = doc.Database.CurrentSpaceId.Handle.ToString(), ["dbmod"] = Convert.ToInt32(Application.GetSystemVariable("DBMOD")), ["units"] = doc.Database.Insunits.ToString(), ["ucs"] = JArray.FromObject(doc.Editor.CurrentUserCoordinateSystem.ToArray()), ["count"] = ((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>().Count(), ["selection"] = selected.Value == null ? new JArray() : new JArray(selected.Value.GetObjectIds().Select(i => i.Handle.ToString())) };
        }
    }
    private Task<JObject> State() => App(() => { Ensure(); return DocumentState(_doc); });
    private Task<HashSet<string>> Handles() => Read(tr => new HashSet<string>(((BlockTableRecord)tr.GetObject(_space, OpenMode.ForRead)).Cast<ObjectId>().Where(i => !i.IsErased).Select(i => i.Handle.ToString())));
    private Task<JArray> LineState(string[] handles) => Read(tr => new JArray(handles.Select(h => { var l = (Line)tr.GetObject(Id(h), OpenMode.ForRead); return new JObject { ["handle"] = h, ["start"] = JArray.FromObject(l.StartPoint.ToArray()), ["end"] = JArray.FromObject(l.EndPoint.ToArray()) }; })));
    private JObject Identity() => new() { ["documentToken"] = _token, ["activeSpaceHandle"] = _spaceHandle };
    private JObject Targets(string[] h) { var p = Identity(); p["source"] = new JObject { ["kind"] = "handles", ["handles"] = new JArray(h) }; p["expectedCount"] = h.Length; p["coordinateSystem"] = "wcs"; return p; }
    private JObject Clone(string[] h, double dx = 10000) { var p = Targets(h); p["displacement"] = new JObject { ["x"] = dx, ["y"] = 0 }; return p; }
    private JObject Move(string[] h, double dx = 10, string system = "wcs") { var p = Targets(h); p["coordinateSystem"] = system; p["operation"] = new JObject { ["type"] = "move", ["displacement"] = new JObject { ["x"] = dx, ["y"] = 0 } }; return p; }
    private static string[] Copies(JObject r) => r["result"]!["createdHandles"]!.Values<string>().Select(h => h ?? throw new InvalidOperationException("Null handle")).ToArray();
    private async Task<JObject> Call(string tool, JObject args)
    {
        await App(() => { Ensure(); return true; }); var watch = Stopwatch.StartNew();
        var response = await _dispatcher.ExecuteAsync(tool, args, Guid.NewGuid().ToString());
        var compact = (JObject)response.DeepClone();
        if (compact["result"] is JObject result && result["items"] is JArray items && items.Count > 20) { result["itemsOmitted"] = items.Count; result["items"] = new JArray(items.Take(2)); }
        var a = (JObject)args.DeepClone(); if (a["source"]?["handles"] is JArray hs && hs.Count > 20) a["source"]!["handles"] = new JArray(hs.Take(2));
        ((JArray)_report["calls"]!).Add(new JObject { ["phase"] = _phase, ["tool"] = tool, ["args"] = a, ["response"] = compact, ["elapsedMs"] = watch.ElapsedMilliseconds, ["state"] = await State() }); Save();
        return response;
    }
    private async Task<JObject> Success(string tool, JObject args) { var r = await Call(tool, args); if (!r.Value<bool>("success")) throw new InvalidOperationException(tool + ": " + r); return r; }
    private async Task<JObject> Write(string tool, JObject args)
    {
        var r = await Success(tool, args);
        var dynamic = tool == "send_code_to_cad";
        Check(r.Value<bool>("committed") && r.Value<bool>("undoGuaranteed") == !dynamic,
            tool + (dynamic ? " committed without a strict undo claim" : " committed with undo guarantee"));
        return r;
    }
    private async Task<JArray> Details(string[] h) { var p = Identity(); p["handles"] = new JArray(h); return (JArray)(await Success("get_entity_details", p))["result"]!["items"]!.DeepClone(); }
    private async Task Select(string[] h, string mode = "replace", int? expected = null) { var p = Identity(); p["mode"] = h.Length == 0 ? "clear" : mode; p["handles"] = new JArray(h); p["expectedCount"] = expected ?? h.Length; await Success("set_selection", p); }
    private async Task Undo(string label) { Phase(label); await App(() => { Ensure(); return true; }); await HostTestDispatch.Undo(_doc); }
    private void Phase(string value) { _phase = value; Save(); }
    private void Check(bool passed, string name, object? detail = null) { ((JArray)_report["checks"]!).Add(new JObject { ["phase"] = _phase, ["test"] = name, ["passed"] = passed, ["detail"] = detail == null ? null : JToken.FromObject(detail) }); Save(); if (!passed) throw new InvalidOperationException("FAILED: " + name); }
    private void Observe(bool passed, string name, object? detail = null) { ((JArray)_report["checks"]!).Add(new JObject { ["phase"] = _phase, ["test"] = name, ["passed"] = passed, ["detail"] = detail == null ? null : JToken.FromObject(detail) }); Save(); }
    private void Started(object sender, CommandEventArgs e) => Event("start", e);
    private void Ended(object sender, CommandEventArgs e) => Event("end", e);
    private void Event(string kind, CommandEventArgs e) { try { lock (_events) ((JArray)_report["events"]!).Add(new JObject { ["phase"] = _phase, ["kind"] = kind, ["name"] = e.GlobalCommandName, ["thread"] = Environment.CurrentManagedThreadId }); } catch { } }
    private void Save() { lock (_events) { _report["phase"] = _phase; File.WriteAllText(_path, _report.ToString()); } }
}
