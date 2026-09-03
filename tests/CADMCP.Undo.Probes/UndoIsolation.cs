using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.Plugin;
using CADMCP.CommandSet;
using Newtonsoft.Json.Linq;
using Exception = System.Exception;

namespace CADMCP.Undo.Probes;

// Diagnostic only. NOT shipped in Bundle. Never alters the service registry or user DWGs.
// Deliberate variants are experiments, not alternative production command implementations.
public sealed partial class UndoIsolation
{
    public const string ResultKey = "CADMCP.UndoIsolation.Result";
    private static bool _running;
    private Document? _doc;
    private string _phase = "setup";
    private readonly JArray _events = new();
    private readonly JArray _cases = new();
    private CadDispatcher? _dispatcher;
    private static bool _safetyPassed;
    private int _hostThread;
    private string? _expectedToken;
    private ObjectId _space;
    private string? _outputFile;

    // New entry: one explicitly selected scenario, current user-designated test DWG only.
    // Old Run remains quarantined. No Add/activation, async void, or automatic retries.
    public string RunCurrent(string documentToken, string scenario)
    {
        if (_running) throw new Exception("Diagnostic already running");
        if (scenario != "safety_mismatch" && !_safetyPassed) throw new Exception("Run safety_mismatch first");
        if (!(new[] { "safety_mismatch", "candidate_clone", "production_dispatcher_no_reads", "production_dispatcher_reads", "production_direct_context",
            "minimal_command_only", "minimal_lock_no_mark", "minimal_mark_no_lock", "minimal_lock_mark_close_inside",
            "minimal_lock_mark_close_outside", "minimal_lock_preflight_mark_close_inside", "minimal_lock_preflight_mark_close_outside",
            "minimal_mark_before_lock" }).Contains(scenario)) throw new Exception("Unknown diagnostic scenario");
        _hostThread = Thread.CurrentThread.ManagedThreadId;
        _doc = Application.DocumentManager.MdiActiveDocument;
        _expectedToken = documentToken; _space = _doc.Database.CurrentSpaceId;
        Ensure();
        _outputFile = Path.Combine(@"D:\A-CADMyDev\2.CADMCP\CADMCP\artifacts", "UNDO_PROBE_" + scenario + "_" + Guid.NewGuid().ToString("N") + ".json");
        _running = true;
        Publish("scheduled");
        _ = RunCurrentAsync(scenario); // all exceptions consumed in this Task
        return _outputFile;
    }

    private async Task RunCurrentAsync(string scenario)
    {
        try
        {
            await Idle();
            await App(() =>
            {
                Ensure();
                if (!_doc!.Editor.IsQuiescent || !string.IsNullOrWhiteSpace(_doc.CommandInProgress)) throw new Exception("CAD busy; stop");
                _doc.CommandWillStart += Started; _doc.CommandEnded += Ended; _doc.CommandCancelled += Cancelled; _doc.CommandFailed += Failed;
                return true;
            });
            Publish("running");
            if (scenario == "safety_mismatch")
            {
                var before = await App(() => Convert.ToInt32(Application.GetSystemVariable("DBMOD")));
                Exception? caught = null;
                try { await Command(() => Ensure("intentional-wrong-document-token")); }
                catch (Exception error) { caught = error; }
                var after = await App(() => Convert.ToInt32(Application.GetSystemVariable("DBMOD")));
                _safetyPassed = caught?.InnerException?.Message == "Diagnostic document identity mismatch" && before == after;
                _cases.Add(new JObject { ["scenario"] = scenario, ["passed"] = _safetyPassed, ["beforeDbmod"] = before, ["afterDbmod"] = after, ["caught"] = caught?.ToString() });
                if (!_safetyPassed) throw new Exception("Safety gate failed");
            }
            else
            {
                var registry = new CommandRegistry(); registry.Load();
#if UNDO_CANDIDATE
                // Only this newly-created diagnostic registry uses candidate commands.
                // The running TCP service registry and installed files remain untouched.
                var map = (System.Collections.Generic.Dictionary<string, ICadCommand>)typeof(CommandRegistry)
                    .GetField("_commands", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(registry)!;
                map["transform_entities"] = new TransformEntitiesCommand();
                map["clone_entities"] = new CloneEntitiesCommand();
#endif
                _dispatcher = new CadDispatcher(registry);
#if UNDO_CANDIDATE
                if (scenario == "candidate_clone") await CandidateClone(registry);
                else await Scenario(scenario);
#else
                await Scenario(scenario);
#endif
            }
            Publish("completed");
        }
        catch (Exception error) { Publish("failed", error.ToString()); }
        finally
        {
            try { await App(() => { if (_doc != null) { _doc.CommandWillStart -= Started; _doc.CommandEnded -= Ended; _doc.CommandCancelled -= Cancelled; _doc.CommandFailed -= Failed; } return true; }); }
            catch (Exception error) { Publish("failed", "Diagnostic cleanup: " + error); }
            _running = false;
        }
    }

    public async void Run()
    {
        // Quarantined after host fatal error during first launch on 2026-09-03.
        // Keep the original experiment below for forensic review, but NEVER rerun it
        // until the scheduling/thread-affinity failure has been diagnosed separately.
        if (IsQuarantined()) { Publish("quarantined", "First launch caused a host fatal error; execution disabled."); return; }
        if (_running) return;
        _running = true;
        Publish("running");
        try
        {
            await Idle(); // launcher must release its command/lock before creating test DWG
            await App(() =>
            {
                _doc = Application.DocumentManager.Add("acadiso.dwt");
                _doc.CommandWillStart += Started;
                _doc.CommandEnded += Ended;
                _doc.CommandCancelled += Cancelled;
                _doc.CommandFailed += Failed;
                return true;
            });
            // A new document is not necessarily active until application-context work
            // has returned. Never assume Add completed activation in the same callback.
            await Idle();
            await App(() => { Ensure(); return true; });
            var registry = new CommandRegistry(); registry.Load(); _dispatcher = new CadDispatcher(registry);
            foreach (var scenario in new[] {
                "production_dispatcher_no_reads", "production_dispatcher_reads", "production_direct_context",
                "minimal_command_only", "minimal_lock_no_mark", "minimal_mark_no_lock",
                "minimal_lock_mark_close_inside", "minimal_lock_mark_close_outside",
                "minimal_lock_preflight_mark_close_inside", "minimal_lock_preflight_mark_close_outside",
                "minimal_mark_before_lock"
            })
            {
                await Scenario(scenario);
                Publish("running");
            }
            Publish("completed");
        }
        catch (Exception error) { Publish("failed", error.ToString()); }
        finally
        {
            await App(() =>
            {
                if (_doc != null) { _doc.CommandWillStart -= Started; _doc.CommandEnded -= Ended; _doc.CommandCancelled -= Cancelled; _doc.CommandFailed -= Failed; }
                return true;
            });
            _running = false;
        }
    }

    private static bool IsQuarantined() => true;

    private void Publish(string status, string? error = null)
    {
        try
        {
            string json;
            lock (_events) json = new JObject { ["status"] = status, ["error"] = error, ["phase"] = _phase, ["assembly"] = typeof(UndoIsolation).Assembly.GetName().Name,
                ["documentToken"] = _expectedToken, ["hostThread"] = _hostThread, ["cases"] = _cases.DeepClone(), ["events"] = _events.DeepClone() }.ToString();
            AppDomain.CurrentDomain.SetData(ResultKey, json);
            if (_outputFile != null) File.WriteAllText(_outputFile, json);
        }
        catch { /* Diagnostics must never throw from a callback or terminal catch. */ }
    }
    private void Started(object sender, CommandEventArgs e) => Log("start", e);
    private void Ended(object sender, CommandEventArgs e) => Log("end", e);
    private void Cancelled(object sender, CommandEventArgs e) => Log("cancel", e);
    private void Failed(object sender, CommandEventArgs e) => Log("fail", e);
    private void Log(string kind, CommandEventArgs e)
    {
        try { lock (_events) _events.Add(new JObject { ["phase"] = _phase, ["kind"] = kind, ["command"] = e.GlobalCommandName,
            ["thread"] = Thread.CurrentThread.ManagedThreadId, ["utc"] = DateTime.UtcNow.ToString("O") }); }
        catch { }
    }

    private async Task Scenario(string scenario)
    {
        var row = new JObject { ["scenario"] = scenario }; _cases.Add(row);
        _phase = scenario + "/fixture";
        string handle = "";
        await Command(() =>
        {
            using (_doc!.LockDocument())
            using (var tr = _doc.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(_doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                var line = new Line(new Point3d(-1000000, -1000000, 7), new Point3d(-1000000, -999995, 7));
                line.SetDatabaseDefaults(_doc.Database); space.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);
                handle = line.Handle.ToString(); tr.Commit();
            }
        });
        var initial = await Geometry(handle); row["initial"] = initial;
        Publish("running");
        _phase = scenario + "/edit1"; await Edit(scenario, handle, 100, 0);
        var first = await Geometry(handle); row["first"] = first;
        Publish("running");
        if (scenario.EndsWith("_reads") && !scenario.EndsWith("_no_reads")) await Reads(handle);
        _phase = scenario + "/edit2"; await Edit(scenario, handle, 0, 200);
        row["second"] = await Geometry(handle);
        Publish("running");
        if (scenario.EndsWith("_reads") && !scenario.EndsWith("_no_reads")) await Reads(handle);
        var undos = new JArray(); row["undos"] = undos;
        // Bound diagnostic U count, stop immediately upon reaching the fixture baseline.
        // This never changes the production promise into a multi-U workaround.
        for (var i = 1; i <= 4; i++)
        {
            _phase = scenario + "/nativeU" + i;
            await NativeUndo();
            var state = await Geometry(handle);
            undos.Add(new JObject { ["index"] = i, ["geometry"] = state });
            Publish("running");
            if (i == 1) row["firstUndoCorrect"] = JToken.DeepEquals(first, state);
            if (JToken.DeepEquals(initial, state)) { row["undosToBaseline"] = i; break; }
        }
        if (row["undosToBaseline"] == null) throw new Exception("Baseline not reached within diagnostic bound: " + scenario);
        row["twoUndoPassed"] = row.Value<bool>("firstUndoCorrect") && row.Value<int>("undosToBaseline") == 2;
    }

    private async Task Reads(string handle)
    {
        _phase += "/reads";
        var identity = await Args(handle, 0, 0);
        var details = new JObject { ["documentToken"] = identity["documentToken"], ["activeSpaceHandle"] = identity["activeSpaceHandle"], ["handles"] = new JArray(handle) };
        foreach (var name in new[] { "get_current_document_info", "get_entity_details" })
        {
            var r = await _dispatcher!.ExecuteAsync(name, name == "get_entity_details" ? details : new JObject(), Guid.NewGuid().ToString());
            if (!r.Value<bool>("success")) throw new Exception(r.ToString());
        }
        identity["dryRun"] = true;
        var preview = await _dispatcher!.ExecuteAsync("transform_entities", identity, Guid.NewGuid().ToString());
        if (!preview.Value<bool>("success")) throw new Exception(preview.ToString());
    }

    private async Task Edit(string scenario, string handle, double x, double y)
    {
        var args = await Args(handle, x, y);
        if (scenario.StartsWith("production_dispatcher"))
        {
            var r = await _dispatcher!.ExecuteAsync("transform_entities", args, Guid.NewGuid().ToString());
            if (!r.Value<bool>("committed")) throw new Exception(r.ToString());
            return;
        }
        await Command(() =>
        {
            if (scenario == "production_direct_context")
            {
                var context = new CadCommandContext(_doc!, SettingsStore.Current, Guid.NewGuid().ToString(), Array.Empty<ObjectId>(), Array.Empty<string>());
                var r = new TransformEntitiesCommand().Execute(context, args);
                if (!r.Value<bool>("committed")) throw new Exception(r.ToString());
                return;
            }
            bool useLock = scenario != "minimal_command_only" && scenario != "minimal_mark_no_lock";
            bool mark = scenario != "minimal_command_only" && scenario != "minimal_lock_no_mark";
            bool beforeLock = scenario == "minimal_mark_before_lock";
            bool closeOutside = scenario.EndsWith("outside") || beforeLock;
            dynamic acad = Application.AcadApplication;
            dynamic comDoc = acad.ActiveDocument;
            DocumentLock? docLock = null; bool opened = false;
            try
            {
                if (mark && beforeLock) { comDoc.StartUndoMark(); opened = true; }
                if (useLock) docLock = _doc!.LockDocument(DocumentLockMode.Write, null, null, false);
                if (scenario.Contains("preflight"))
                    using (var read = _doc!.Database.TransactionManager.StartOpenCloseTransaction())
                        read.GetObject(Id(handle), OpenMode.ForRead);
                if (mark && !opened) { comDoc.StartUndoMark(); opened = true; }
                using (var tr = _doc!.Database.TransactionManager.StartTransaction())
                {
                    var line = (Line)tr.GetObject(Id(handle), OpenMode.ForWrite);
                    line.TransformBy(Matrix3d.Displacement(new Vector3d(x, y, 0))); tr.Commit();
                }
                if (opened && !closeOutside) { comDoc.EndUndoMark(); opened = false; }
            }
            finally
            {
                try { docLock?.Dispose(); }
                finally { if (opened) comDoc.EndUndoMark(); }
            }
        });
    }

    private Task<JObject> Args(string h, double x, double y) => App(() =>
    {
        Ensure();
        return new JObject { ["documentToken"] = DocumentIdentity.GetToken(_doc!), ["activeSpaceHandle"] = _doc!.Database.CurrentSpaceId.Handle.ToString(),
            ["source"] = new JObject { ["kind"] = "handles", ["handles"] = new JArray(h) }, ["expectedCount"] = 1, ["coordinateSystem"] = "wcs",
            ["operation"] = new JObject { ["type"] = "move", ["displacement"] = new JObject { ["x"] = x, ["y"] = y, ["z"] = 0 } } };
    });
    private ObjectId Id(string h) => _doc!.Database.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0);
    private Task<JObject> Geometry(string h) => App(() =>
    {
        Ensure();
        using (_doc!.LockDocument(DocumentLockMode.Read, null, null, false))
        using (var tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var l = (Line)tr.GetObject(Id(h), OpenMode.ForRead);
            return new JObject { ["handle"] = h, ["start"] = JArray.FromObject(l.StartPoint.ToArray()), ["end"] = JArray.FromObject(l.EndPoint.ToArray()) };
        }
    });
    private async Task Command(Action action)
    {
        Exception? callbackError = null;
        var pending = await App(() =>
        {
            Ensure();
            return Application.DocumentManager.ExecuteInCommandContextAsync(_ =>
            {
                // No exception may escape this native-owned callback. A try/catch
                // around awaiting the returned Task does NOT catch that failure safely.
                try { Ensure(); action(); }
                catch (Exception error) { callbackError = error; }
                return Task.CompletedTask;
            }, null);
        });
        await pending;
        if (callbackError != null) throw new Exception("Diagnostic command callback failed safely; stopped without retry", callbackError);
    }
    private async Task NativeUndo()
    {
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CommandEventHandler end = (_, e) => { try { if (e.GlobalCommandName.Equals("U", StringComparison.OrdinalIgnoreCase)) ended.TrySetResult(true); } catch (Exception error) { ended.TrySetException(error); } };
        CommandEventHandler fail = (_, e) => { try { ended.TrySetException(new Exception("Native U failed/cancelled: " + e.GlobalCommandName)); } catch (Exception error) { ended.TrySetException(error); } };
        await App(() =>
        {
            Ensure();
            _doc!.CommandEnded += end; _doc.CommandFailed += fail; _doc.CommandCancelled += fail;
            _doc.SendStringToExecute("_.U ", true, false, false);
            return true;
        });
        try
        {
            if (await Task.WhenAny(ended.Task, Task.Delay(15000)) != ended.Task) throw new Exception("No native U completion event; stopped without retry");
            await ended.Task;
        }
        finally { await App(() => { _doc!.CommandEnded -= end; _doc.CommandFailed -= fail; _doc.CommandCancelled -= fail; return true; }); }
        await Idle();
    }
    private void Ensure(string? overrideToken = null)
    {
        if (_hostThread != 0 && Thread.CurrentThread.ManagedThreadId != _hostThread) throw new Exception("Diagnostic attempted CAD access off host thread");
        if (_doc == null || !ReferenceEquals(_doc, Application.DocumentManager.MdiActiveDocument)) throw new Exception("Independent test document changed; stopped");
        if (_expectedToken != null && (DocumentIdentity.GetToken(_doc) != (overrideToken ?? _expectedToken) || _doc.Database.CurrentSpaceId != _space)) throw new Exception("Diagnostic document identity mismatch");
    }
    private Task<T> App<T>(Func<T> action)
    {
        if (Thread.CurrentThread.ManagedThreadId == _hostThread && Application.DocumentManager.IsApplicationContext) return Task.FromResult(action());
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.DocumentManager.ExecuteInApplicationContext(_ => { try { if (Thread.CurrentThread.ManagedThreadId != _hostThread) throw new Exception("Wrong callback thread"); tcs.TrySetResult(action()); } catch (Exception e) { tcs.TrySetException(e); } }, null);
        return tcs.Task;
    }
    private async Task Idle()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await App(() => { EventHandler? h = null; h = (_, __) => { try { Application.Idle -= h; tcs.TrySetResult(true); } catch (Exception error) { tcs.TrySetException(error); } }; Application.Idle += h; return true; });
        await tcs.Task;
    }
}
