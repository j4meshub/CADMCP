using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.CommandSet;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.Acceptance;
public sealed partial class AcceptanceRun
{
    private async Task Bulk()
    {
        var handles = new List<string>();
        Phase("create 2001 temporary source lines");
        await Cmd(() => Transaction((tr, space) => { for (var i = 0; i < 2001; i++) { var l = new Line(new Point3d(i * 2, 0, 7), new Point3d(i * 2, 10, 7)); l.SetDatabaseDefaults(_doc.Database); space.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true); handles.Add(l.Handle.ToString()); } }));
        _report["fixtureHandles"] = new JArray(handles); var baseline = await Handles();
        var allBefore = await LineState(handles.ToArray());
        foreach (var count in new[] { 200, 1000, 2000 })
        {
            Phase("bulk " + count + " preview"); var targets = handles.Take(count).ToArray();
            await Select(targets); var before = await State();
            var preview = Clone(targets); preview["dryRun"] = true;
            var p = await Success("clone_entities", preview);
            Check(!p.Value<bool>("committed") && JToken.DeepEquals(before, await State()) && (await Handles()).SetEquals(baseline), count + " preview no state/handle modification");
            var clone = await Write("clone_entities", Clone(targets)); var copies = Copies(clone);
            Check(copies.Length == count && copies.Distinct().Count() == count && ((JArray)clone["result"]!["handleMapping"]!).Count == count, count + " clone unique complete mapping");
            var copyGeometry = await LineState(copies);
            Check(copyGeometry.OfType<JObject>().Select((l,i) => Math.Abs(l["start"]![0]!.Value<double>() - (i*2 + 10000)) < 1e-8 && l["start"]![2]!.Value<double>() == 7).All(x=>x), count + " copy displacement and height exact");
            await Success("get_current_document_info", new JObject()); await Details(targets);
            await Undo(count + " clone one U"); Check((await Handles()).SetEquals(baseline), count + " all copied handles removed by one U");
            var original = await LineState(targets);
            await Write("transform_entities", Move(targets));
            var moved = await LineState(targets);
            Check(moved.OfType<JObject>().Select((l,i)=>Math.Abs(l["start"]![0]!.Value<double>()-(i*2+10))<1e-8).All(x=>x), count + " actual move geometry");
            var dry = Move(targets); dry["dryRun"] = true; await Success("transform_entities", dry);
            await Undo(count + " move one U"); Check(JToken.DeepEquals(original, await LineState(targets)), count + " exact geometry restored by one U");
            var commands = (Dictionary<string,ICadCommand>)typeof(CommandRegistry).GetField("_commands", BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_registry)!;
            foreach (var name in new[] { "clone_entities", "transform_entities" })
            {
                var real = commands[name]; commands[name] = new MidFailure(name, count/2);
                try { var failure = await Call(name, name == "clone_entities" ? Clone(targets) : Move(targets)); Check(!failure.Value<bool>("success") && failure.Value<bool>("rolledBack") && (await Handles()).SetEquals(baseline) && JToken.DeepEquals(allBefore, await LineState(handles.ToArray())), count + " halfway failure rolls back " + name); }
                finally { commands[name] = real; }
            }
        }
        Phase("full selection over limit rejection");
        await Cmd(() => _doc.Editor.SetImpliedSelection(handles.Select(Id).ToArray()));
        var over = Clone(handles.Take(2000).ToArray()); over["source"] = new JObject { ["kind"] = "selection" }; over["expectedCount"] = 2000;
        var rejected = await Call("clone_entities", over);
        Check(rejected.Value<string>("errorCode") == "invalid_parameters" && !rejected.Value<bool>("committed") && (await Handles()).SetEquals(baseline), "2001 actual selected entities rejected, not truncated");
        Check(JToken.DeepEquals(allBefore, await LineState(handles.ToArray())), "all bulk source geometry unchanged at end");
        await Select(Array.Empty<string>());
    }
    private sealed class MidFailure : AtomicModifyCommand
    {
        private readonly string _name; private readonly int _at; private int _count;
        public MidFailure(string name, int at) { _name=name;_at=at; }
        public override string Name => _name; protected override bool IsClone => _name=="clone_entities";
        protected override void TransformEntity(Entity e, Matrix3d m) { base.TransformEntity(e,m); if(++_count==_at) throw new InvalidOperationException("Injected halfway failure at " + _at); }
    }
}
