using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.CommandSet;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.AutoCAD.Tests;

internal static class ExecutionRegressionTests
{
    // Called only with the dedicated, newly-created test drawing, never a business DWG.
    public static async Task Run(Document doc, CadDispatcher dispatcher, CommandRegistry registry, JObject identity, string[] handles, JArray checks)
    {
        void Check(bool ok, string name)
        { checks.Add(new JObject { ["test"] = name, ["passed"] = ok }); if (!ok) throw new Exception("FAILED: " + name); }
        async Task<JObject> Call(string name, JObject args) => await dispatcher.ExecuteAsync(name, args, Guid.NewGuid().ToString());
        JObject Identity() => (JObject)identity.DeepClone();
        JObject Edit(bool clone, bool preview = false)
        {
            var p = Identity(); p["source"] = new JObject { ["kind"] = "handles", ["handles"] = new JArray(handles) };
            p["expectedCount"] = handles.Length; p["coordinateSystem"] = "wcs"; p["dryRun"] = preview;
            if (clone) p["displacement"] = JObject.Parse("{x:100,y:20}");
            else p["operation"] = JObject.Parse("{type:'move',displacement:{x:10,y:20}}");
            return p;
        }
        JObject Select(string mode)
        {
            var p = Identity(); p["mode"] = mode;
            p["handles"] = mode == "clear" ? new JArray() : new JArray(handles);
            p["expectedCount"] = mode == "clear" || mode == "remove" ? 0 : handles.Length;
            return p;
        }
        var details = Identity(); details["handles"] = new JArray(handles);
        var invalid = Edit(true, true); invalid["expectedCount"] = handles.Length + 1;
        var cases = new[]
        {
            ("say_hello", new JObject(), true), ("get_current_document_info", new JObject(), true),
            ("get_selected_entities", new JObject(), true), ("query_entities", JObject.Parse("{limit:1}"), true),
            ("get_entity_details", details, true), ("clone_entities", Edit(true, true), true),
            ("transform_entities", Edit(false, true), true), ("clone_entities", invalid, false),
            ("set_selection", Select("replace"), true), ("set_selection", Select("add"), true),
            ("set_selection", Select("remove"), true), ("set_selection", Select("clear"), true)
        };
        foreach (var name in CadMcpSettings.ToolNames.Where(n => n != "get_execution_status"))
        {
            var expected = name == "set_selection" ? CadExecutionKind.Selection
                : name == "clone_entities" || name == "transform_entities" ? CadExecutionKind.PreviewableWrite
                : name == "create_line" || name == "create_circle" || name == "create_polyline" || name == "create_text" ? CadExecutionKind.FixedWrite
                : name == "say_hello" || name == "get_current_document_info" || name == "get_selected_entities" || name == "query_entities" || name == "get_entity_details"
                    ? CadExecutionKind.ReadOnly : CadExecutionKind.CommandContext;
            Check(registry.Get(name).ExecutionKind == expected, "execution category: " + name);
        }
        foreach (var entry in cases)
        {
            Check((await Call("set_selection", Select("replace"))).Value<bool>("success"), "fixture selection");
            var before = await Geometry(doc, handles);
            var write = await Call("transform_entities", Edit(false));
            Check(write.Value<bool>("committed") && write.Value<bool>("undoGuaranteed"), "write before " + entry.Item1);
            var moved = await Geometry(doc, handles);
            var response = await Call(entry.Item1, entry.Item2);
            Check(response.Value<bool>("success") == entry.Item3, "read/selection outcome: " + entry.Item1);
            Check(JToken.DeepEquals(moved, await Geometry(doc, handles)), "read/selection leaves geometry: " + entry.Item1);
            if (entry.Item1 != "set_selection")
                Check((await Call("get_selected_entities", new JObject()))["result"]!.Value<int>("count") == handles.Length, "read preserves selection");
            await Undo(doc);
            Check(JToken.DeepEquals(before, await Geometry(doc, handles)), "ONE Undo after " + entry.Item1 + "/" + entry.Item2.ToString(Newtonsoft.Json.Formatting.None));
        }
        // Two edit units, with repeated reads and previews between/after them.
        var initial = await Geometry(doc, handles);
        Check((await Call("transform_entities", Edit(false))).Value<bool>("committed"), "first edit");
        var first = await Geometry(doc, handles);
        for (var i = 0; i < 3; i++) Check((await Call("get_entity_details", details)).Value<bool>("success"), "repeated read");
        Check((await Call("transform_entities", Edit(false))).Value<bool>("committed"), "second edit");
        Check((await Call("clone_entities", Edit(true, true))).Value<bool>("success"), "preview after second edit");
        await Undo(doc); Check(JToken.DeepEquals(first, await Geometry(doc, handles)), "first U restores only second edit");
        await Undo(doc); Check(JToken.DeepEquals(initial, await Geometry(doc, handles)), "second U restores first edit");
        // Three distinct native units must also undo strictly in reverse order.
        var states = new[] { initial, initial, initial };
        for (var i = 0; i < 3; i++)
        {
            states[i] = await Geometry(doc, handles);
            Check((await Call("transform_entities", Edit(false))).Value<bool>("undoGuaranteed"), "three consecutive writes " + i);
            Check((await Call("get_entity_details", details)).Value<bool>("success"), "read after consecutive write");
        }
        for (var i = 2; i >= 0; i--)
        { await Undo(doc); Check(JToken.DeepEquals(states[i], await Geometry(doc, handles)), "reverse one U " + i); }

        // Verify the exact created handles, not just entity counts, for two clone units.
        var baselineHandles = await ExistingHandles(doc);
        var clone1 = await Call("clone_entities", Edit(true));
        Check(clone1.Value<bool>("committed") && clone1.Value<bool>("undoGuaranteed"), "first clone unit");
        var copies1 = clone1["result"]!["createdHandles"]!.Values<string>().Select(h => h ?? throw new Exception("Null clone handle")).ToArray();
        Check((await Call("transform_entities", Edit(false, true))).Value<bool>("success"), "preview between clones");
        var clone2 = await Call("clone_entities", Edit(true));
        Check(clone2.Value<bool>("committed") && clone2.Value<bool>("undoGuaranteed"), "second clone unit");
        var copies2 = clone2["result"]!["createdHandles"]!.Values<string>().Select(h => h ?? throw new Exception("Null clone handle")).ToArray();
        var allCopies = await ExistingHandles(doc);
        Check(copies1.Concat(copies2).All(allCopies.Contains), "both clone batches exist");
        await Undo(doc);
        var remaining = await ExistingHandles(doc);
        Check(copies1.All(remaining.Contains) && !copies2.Any(remaining.Contains) && handles.All(remaining.Contains), "U removes only second clone handles");
        await Undo(doc);
        remaining = await ExistingHandles(doc);
        Check(remaining.SetEquals(baselineHandles) && JToken.DeepEquals(initial, await Geometry(doc, handles)), "second U removes first copies; source geometry unchanged");
        await Mirror(doc, dispatcher, identity, checks);
    }

    private static Task<System.Collections.Generic.HashSet<string>> ExistingHandles(Document doc) => InApplication(() =>
    {
        EnsureCurrent(doc);
        using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            return new System.Collections.Generic.HashSet<string>(((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead))
                .Cast<ObjectId>().Where(id => id.IsValid && !id.IsErased).Select(id => id.Handle.ToString()));
    });

    private static Task<JArray> Geometry(Document doc, string[] handles) => InApplication(() =>
    {
        EnsureCurrent(doc);
        using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            return new JArray(handles.Select(h =>
            {
                var line = (Line)tr.GetObject(doc.Database.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0), OpenMode.ForRead);
                return new JObject { ["handle"] = h, ["start"] = JArray.FromObject(line.StartPoint.ToArray()), ["end"] = JArray.FromObject(line.EndPoint.ToArray()) };
            }));
    });

    private static async Task Mirror(Document doc, CadDispatcher dispatcher, JObject identity, JArray checks)
    {
        var originalMirrtext = await InApplication(() => Application.GetSystemVariable("MIRRTEXT"));
        try
        {
            foreach (var mode in new[] { 0, 1 })
            {
                string? textHandle = null, mtextHandle = null;
                await HostTestDispatch.Command(doc, () =>
                {
                    EnsureCurrent(doc); Application.SetSystemVariable("MIRRTEXT", mode);
                    using (doc.LockDocument())
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                        var text = new DBText { Position = new Point3d(3000, 100, 7), TextString = "ABC123", Height = 10 };
                        text.SetDatabaseDefaults(doc.Database); space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true); textHandle = text.Handle.ToString();
                        var mtext = new MText { Location = new Point3d(3000, 200, 7), Contents = "ABC123", TextHeight = 10, Width = 100 };
                        mtext.SetDatabaseDefaults(doc.Database); space.AppendEntity(mtext); tr.AddNewlyCreatedDBObject(mtext, true); mtextHandle = mtext.Handle.ToString();
                        tr.Commit();
                    }
                });
                if (textHandle == null || mtextHandle == null) throw new Exception("Mirror fixtures not created");
                var args = (JObject)identity.DeepClone();
                args["source"] = new JObject { ["kind"] = "handles", ["handles"] = new JArray(textHandle, mtextHandle) };
                args["expectedCount"] = 2; args["coordinateSystem"] = "wcs";
                args["operation"] = JObject.Parse("{type:'mirror',axisStart:{x:3500,y:0},axisEnd:{x:3500,y:1000}}");
                var result = await dispatcher.ExecuteAsync("transform_entities", args, Guid.NewGuid().ToString());
                var state = await InApplication(() =>
                {
                    using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
                    using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                    {
                        var text = (DBText)tr.GetObject(doc.Database.GetObjectId(false, new Handle(Convert.ToInt64(textHandle, 16)), 0), OpenMode.ForRead);
                        var mtext = (MText)tr.GetObject(doc.Database.GetObjectId(false, new Handle(Convert.ToInt64(mtextHandle, 16)), 0), OpenMode.ForRead);
                        var textForward = !text.IsMirroredInX && !text.IsMirroredInY && text.Normal.Z > 0.999 && Math.Abs(text.Rotation) < 1e-8;
                        var mtextForward = mtext.Normal.Z > 0.999 && mtext.Direction.X > 0.999;
                        return new { textForward, mtextForward, heightPreserved = Math.Abs(text.Position.Z - 7) < 1e-8 && Math.Abs(mtext.Location.Z - 7) < 1e-8,
                            setting = Convert.ToInt32(Application.GetSystemVariable("MIRRTEXT")) };
                    }
                });
                var passed = result.Value<bool>("committed") && state.setting == mode && state.heightPreserved &&
                    (mode == 0 ? state.textForward && state.mtextForward : !state.textForward && !state.mtextForward);
                checks.Add(new JObject { ["test"] = "native text mirror MIRRTEXT=" + mode, ["passed"] = passed, ["state"] = JObject.FromObject(state) });
                if (!passed) throw new Exception("FAILED: text mirror MIRRTEXT=" + mode);
            }
        }
        finally
        {
            await InApplication(() => { EnsureCurrent(doc); Application.SetSystemVariable("MIRRTEXT", originalMirrtext); return true; });
        }
    }

    private static Task Undo(Document doc) => HostTestDispatch.Undo(doc);
    internal static Task<T> InApplication<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == HostTestDispatch.ThreadId && Application.DocumentManager.IsApplicationContext)
        {
            try { return Task.FromResult(action()); }
            catch (Exception error) { return Task.FromException<T>(error); }
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.DocumentManager.ExecuteInApplicationContext(_ =>
        { try { completion.TrySetResult(action()); } catch (Exception error) { completion.TrySetException(error); } }, null);
        return completion.Task;
    }
    private static void EnsureCurrent(Document doc)
    { HostTestDispatch.EnsureCurrent(doc); }
}
