using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CADMCP.CommandSet;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.Acceptance;

public sealed partial class AcceptanceRun
{
    // Run only after restarting CAD with the matching whole bugfix3 Bundle.
    private async Task UndoPolicyCases()
    {
        var names = new[] { "create_line", "create_circle", "create_polyline", "create_text" };
        foreach (var name in names) Check(_registry.Get(name).ExecutionKind == CadExecutionKind.FixedWrite, "installed fixed-write category " + name);
        Check(_registry.Get("send_code_to_cad").ExecutionKind == CadExecutionKind.CommandContext, "installed dynamic capability-first category");
        JObject Item(int i, double x) => i switch
        {
            0 => new JObject { ["start"] = P(x), ["end"] = P(x + 10) },
            1 => new JObject { ["center"] = P(x), ["radius"] = 10 },
            2 => new JObject { ["vertices"] = new JArray(P(x), P(x + 10), P(x + 10, 20)), ["closed"] = true },
            _ => new JObject { ["position"] = P(x), ["content"] = "UNDO policy ABC", ["height"] = 10, ["width"] = 100 }
        };
        JObject Batch(int i, double x) => new() { ["coordinateSystem"] = "wcs", ["items"] = new JArray(Item(i, x), Item(i, x + 200)) };
        async Task Reads(string[] handles)
        {
            await Select(handles);
            for (var r = 0; r < 2; r++)
            {
                await Success("get_current_document_info", new JObject()); await Details(handles);
                var clone = Clone(handles); clone["dryRun"] = true; await Success("clone_entities", clone);
                var move = Move(handles); move["dryRun"] = true; await Success("transform_entities", move);
            }
        }
        // Both same-tool pairs and mixed pairs; each call contains two entities.
        for (var i = 0; i < names.Length; i++)
        foreach (var j in new[] { i, (i + 1) % names.Length })
        {
            Phase(names[i] + " then " + names[j] + " two native units");
            var baseline = await Handles();
            var first = Created(await Write(names[i], Batch(i, 20000)));
            var firstGeometry = await Details(first); await Reads(first);
            var second = Created(await Write(names[j], Batch(j, 22000))); await Reads(first);
            var selected = (await Success("get_selected_entities", new JObject()))["result"]!["items"]!.Select(x => x.Value<string>("handle")!).ToArray();
            Check(new HashSet<string>(first).SetEquals(selected), "fixed creation retains source preselection");
            await Undo("fixed create pair first U"); var remaining = await Handles();
            Check(first.All(remaining.Contains) && second.All(h => !remaining.Contains(h)), "first U removes only last batch");
            Check(JToken.DeepEquals(firstGeometry, await Details(first)), "first batch geometry intact after U");
            await Undo("fixed create pair second U"); Check((await Handles()).SetEquals(baseline), "second U removes first batch, no empty step");
        }
        Phase("creation resources and partial failure");
        var before = await Handles();
        foreach (var resource in new[] { "layer", "linetype" })
        {
            var bad = Batch(0, 24000); bad["items"]![1]![resource] = "CADMCP_MISSING_" + Guid.NewGuid().ToString("N");
            var r = await Call("create_line", bad);
            Check(r.Value<string>("errorCode") == "missing_resource" && !r.Value<bool>("committed") && (await Handles()).SetEquals(before), "missing " + resource + " whole batch rejected");
        }
        var commands = (Dictionary<string, ICadCommand>)typeof(CommandRegistry).GetField("_commands", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_registry)!;
        var real = commands["create_line"];
        try
        {
            commands["create_line"] = new FailingCreate();
            var r = await Call("create_line", Batch(0, 24000));
            Check(!r.Value<bool>("success") && r.Value<bool>("rolledBack") && (await Handles()).SetEquals(before), "second item exception rolls back first created entity");
        }
        finally { commands["create_line"] = real; }
        var good = Created(await Write("create_line", Batch(0, 24000)));
        await Undo("creation after rollback one U"); Check((await Handles()).SetEquals(before), "creation works after rollback and undoes once");
        Phase("post-commit selection failure retains created handles");
        try
        {
            commands["create_line"] = new InvalidSelectionAfterCreate(real);
            var r = await Write("create_line", Batch(0, 24000));
            var handles = Created(r); var existing = await Handles();
            Check(handles.All(existing.Contains) && ((JArray)r["warnings"]!).OfType<JObject>().Any(w => w.Value<string>("code") == "post_commit_selection_failed"), "selection warning preserves successful actual creation");
        }
        finally { commands["create_line"] = real; }
        await Undo("selection warning creation one U"); Check((await Handles()).SetEquals(before), "warning did not create another undo unit");

        // Reuse all dynamic parameter/exception/none/continuous-U probes with the new
        // conservative response contract. Historical JSON remains unchanged.
        await LegacyAndDynamicCases();
        Phase("dynamic 3D and native command capability");
        var code = new JObject { ["transactionMode"] = "auto", ["parameters"] = new JObject(), ["references"] = new JArray(typeof(AcceptanceRun).Assembly.Location),
            ["code"] = "var space=(BlockTableRecord)transaction.GetObject(database.CurrentSpaceId,OpenMode.ForWrite);using(var box=new Solid3d()){box.CreateBox(10,20,30);space.AppendEntity(box);transaction.AddNewlyCreatedDBObject(box,true);return new {handle=box.Handle.ToString(),kind=box.GetType().Name};}" };
        var solid = await Write("send_code_to_cad", code); var solidHandle = solid["result"]!.Value<string>("handle")!;
        Check(solid["result"]!.Value<string>("kind") == "Solid3d" && (await Handles()).Contains(solidHandle), "dynamic 3D not restricted by fixed-entity whitelist; explicit DLL accepted");
        await RemoveLeftovers(new[] { solidHandle });
        before = await Handles();
        code["code"] = "editor.Command(\"_.CIRCLE\",new Point3d(26000,0,0),5.0);return new {nativeCommand=true};";
        var native = await Write("send_code_to_cad", code); var added = (await Handles()).Except(before).ToArray();
        Check(native["result"]!.Value<bool>("nativeCommand") && added.Length == 1, "dynamic native Editor.Command remains available");
        await RemoveLeftovers(added);
        Phase("dynamic selection effect retained in none");
        var selectedHandle = Created(await Write("create_line", Draw("wcs", Item(0, 28000))))[0];
        code["transactionMode"] = "none"; code["parameters"] = new JObject { ["handle"] = selectedHandle };
        code["code"] = "var id=database.GetObjectId(false,new Handle(Convert.ToInt64(parameters.Value<string>(\"handle\"),16)),0);editor.SetImpliedSelection(new[]{id});return new {noTransaction=transaction==null};";
        var selection = await Success("send_code_to_cad", code);
        var current = (await Success("get_selected_entities", new JObject()))["result"]!;
        Check(selection["result"]!.Value<bool>("noTransaction") && !selection.Value<bool>("undoGuaranteed") && current["items"]!.Any(i => i.Value<string>("handle") == selectedHandle), "none selection survives subsequent fixed reads");
        await Select(Array.Empty<string>()); await RemoveLeftovers(new[] { selectedHandle });
    }

    private sealed class FailingCreate : AtomicCreateCommand
    {
        private int _count;
        public override string Name => "create_line";
        protected override Entity Create(JObject item, CadUnits units, Editor editor, string system)
        { if (++_count == 2) throw new InvalidOperationException("Intentional second item failure"); return new Line(Point3d.Origin, new Point3d(10, 0, 0)); }
    }
    private sealed class InvalidSelectionAfterCreate : ICadCommand
    {
        private readonly ICadCommand _inner;
        public InvalidSelectionAfterCreate(ICadCommand inner) => _inner = inner;
        public string Name => "create_line";
        public CadExecutionKind ExecutionKind => CadExecutionKind.FixedWrite;
        public JObject Execute(CadCommandContext context, JObject args)
        { var response = _inner.Execute(context, args); if (response.Value<bool>("committed")) context.RequestSelection(new[] { ObjectId.Null }); return response; }
    }
}
