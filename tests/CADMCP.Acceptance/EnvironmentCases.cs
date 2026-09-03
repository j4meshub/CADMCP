using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.AutoCAD.Tests;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.Acceptance;
public sealed partial class AcceptanceRun
{
    private static JObject P(double x, double y = 0, double z = 0) => new() { ["x"] = x, ["y"] = y, ["z"] = z };
    private static bool Near(Point3d a, Point3d b) => a.DistanceTo(b) < 1e-7;
    private static string[] Created(JObject r) => r["result"]!["handles"]!.Values<string>().Select(h => h!).ToArray();
    private static JObject Draw(string system, JObject item) => new() { ["coordinateSystem"] = system, ["items"] = new JArray(item) };

    private async Task EnvironmentCases()
    {
        var unitCases = new[] { (UnitsValue.Millimeters, 1d), (UnitsValue.Meters, 1000d), (UnitsValue.Inches, 25.4), (UnitsValue.Undefined, 1d) };
        var frames = new[] { Matrix3d.Identity,
            Matrix3d.Displacement(new Vector3d(100,200,30))*Matrix3d.Rotation(Math.PI/3,Vector3d.ZAxis,Point3d.Origin),
            Matrix3d.Displacement(new Vector3d(100,200,30))*Matrix3d.Rotation(Math.PI/4,Vector3d.XAxis,Point3d.Origin)*Matrix3d.Rotation(Math.PI/3,Vector3d.ZAxis,Point3d.Origin) };
        foreach (var unit in unitCases)
        for (int f=0;f<frames.Length;f++)
        {
            var frame=frames[f]; var mm=unit.Item2;
            Phase("units " + unit.Item1 + " UCS " + f);
            await Cmd(()=> { _doc.Database.Insunits=unit.Item1; _doc.Editor.CurrentUserCoordinateSystem=frame; });
            var baseline=await Handles();
            var tools=new[] { "create_line", "create_circle", "create_polyline", "create_text" };
            var items=new[] {
                new JObject { ["start"]=P(100,200,70), ["end"]=P(1100,200,70) },
                new JObject { ["center"]=P(100,200,70), ["radius"]=50 },
                new JObject { ["vertices"]=new JArray(P(100,200),P(1100,200),P(1100,400)), ["closed"]=true },
                new JObject { ["position"]=P(100,200,70), ["height"]=10, ["width"]=100, ["content"]="UCS ABC" }
            };
            for(int i=0;i<tools.Length;i++)
            {
                var index=i; var response=await Write(tools[i],Draw("ucs",items[i])); var h=Created(response)[0];
                var correct=await Read(tr=> {
                    var e=tr.GetObject(Id(h),OpenMode.ForRead);
                    var expected=new Point3d(100/mm,200/mm,70/mm).TransformBy(frame);
                    var normal=Vector3d.ZAxis.TransformBy(frame);
                    if(index==0) { var l=(Line)e; return Near(l.StartPoint,expected)&&Math.Abs(l.Length-1000/mm)<1e-7; }
                    if(index==1) { var c=(Circle)e; return Near(c.Center,expected)&&Math.Abs(c.Radius-50/mm)<1e-7&&(c.Normal-normal).Length<1e-7; }
                    if(index==2) { var p=(Polyline)e; return p.Closed&&Near(p.GetPoint3dAt(0),new Point3d(100/mm,200/mm,0).TransformBy(frame))&&Near(p.GetPoint3dAt(1),new Point3d(1100/mm,200/mm,0).TransformBy(frame)); }
                    var t=(MText)e; return Near(t.Location,expected)&&Math.Abs(t.TextHeight-10/mm)<1e-7&&(t.Normal-normal).Length<1e-7;
                });
                Observe(correct, unit.Item1+" frame "+f+" "+tools[i]+" native geometry");
                await Success("get_current_document_info",new JObject());
                await Undo(unit.Item1+" frame "+f+" "+tools[i]+" single U");
                Observe((await Handles()).SetEquals(baseline),tools[i]+" single U removes new entity");
                // Do not issue extra U if a legacy tool has an empty undo step.
                await RemoveLeftovers(new[]{h});
            }
            // An explicit WCS request must not inherit even a tilted UCS.
            var wcs=Created(await Write("create_line",Draw("wcs",items[0])))[0];
            Check(await Read(tr=>Near(((Line)tr.GetObject(Id(wcs),OpenMode.ForRead)).StartPoint,new Point3d(100/mm,200/mm,70/mm))),"explicit WCS ignores UCS "+unit.Item1+" "+f);
            await Undo("explicit WCS one U"); Observe((await Handles()).SetEquals(baseline),"explicit WCS create undo"); await RemoveLeftovers(new[]{wcs});
            string source="";
            await Cmd(()=>Transaction((tr,space)=> { var l=new Line(new Point3d(10,20,7).TransformBy(frame),new Point3d(20,20,7).TransformBy(frame));space.AppendEntity(l);tr.AddNewlyCreatedDBObject(l,true);source=l.Handle.ToString(); }));
            var hs=new[]{source}; var before=await LineState(hs); var original=new Point3d(10,20,7).TransformBy(frame);
            var clone=Clone(hs,1000);clone["coordinateSystem"]="ucs";
            var copies=Copies(await Write("clone_entities",clone));
            Check(await Read(tr=>Near(((Line)tr.GetObject(Id(copies[0]),OpenMode.ForRead)).StartPoint,original+new Vector3d(1000/mm,0,0).TransformBy(frame))),"UCS clone vector excludes UCS origin");
            await Undo("UCS clone one U");Check(!(await Handles()).Contains(copies[0]),"UCS clone undo");
            await Write("transform_entities",Move(hs,1000,"ucs"));
            Check(await Read(tr=>Near(((Line)tr.GetObject(Id(source),OpenMode.ForRead)).StartPoint,original+new Vector3d(1000/mm,0,0).TransformBy(frame))),"UCS move vector excludes UCS origin");
            await Undo("UCS move one U");Check(JToken.DeepEquals(before,await LineState(hs)),"UCS move undo exact");
            var rotate=Targets(hs);rotate["coordinateSystem"]="ucs";rotate["operation"]=new JObject{["type"]="rotate",["basePoint"]=P(0),["angle"]=Math.PI/2};
            await Write("transform_entities",rotate);
            Check(await Read(tr=>Near(((Line)tr.GetObject(Id(source),OpenMode.ForRead)).StartPoint,new Point3d(-20,10,7).TransformBy(frame))),"rotate around UCS Z at UCS origin");
            await Undo("UCS rotate one U");Check(JToken.DeepEquals(before,await LineState(hs)),"UCS rotate undo exact");
            var scale=Targets(hs);scale["coordinateSystem"]="ucs";scale["operation"]=new JObject{["type"]="scale",["basePoint"]=P(0),["factor"]=2};
            await Write("transform_entities",scale);
            Check(await Read(tr=>Near(((Line)tr.GetObject(Id(source),OpenMode.ForRead)).StartPoint,new Point3d(20,40,14).TransformBy(frame))),"scale around UCS base point");
            await Undo("UCS scale one U");Check(JToken.DeepEquals(before,await LineState(hs)),"UCS scale undo exact");
            var mirror=Targets(hs);mirror["coordinateSystem"]="ucs";mirror["operation"]=new JObject{["type"]="mirror",["axisStart"]=P(0),["axisEnd"]=P(1000)};
            await Write("transform_entities",mirror);
            Check(await Read(tr=>Near(((Line)tr.GetObject(Id(source),OpenMode.ForRead)).StartPoint,new Point3d(10,-20,7).TransformBy(frame))),"mirror preserves input-frame height");
            await Undo("UCS mirror one U");Check(JToken.DeepEquals(before,await LineState(hs)),"UCS mirror undo exact");
        }
        await Cmd(()=>{_doc.Database.Insunits=UnitsValue.Millimeters;_doc.Editor.CurrentUserCoordinateSystem=Matrix3d.Identity;});
        await DocumentAndSpaceCases();
        await LegacyAndDynamicCases();
    }

    private async Task RemoveLeftovers(string[] hs)
    {
        var existing=await Handles();var remaining=hs.Where(existing.Contains).ToArray();
        if(remaining.Length>0) await Cmd(()=>Transaction((tr,space)=>{foreach(var h in remaining)((Entity)tr.GetObject(Id(h),OpenMode.ForWrite)).Erase();}));
    }

    private async Task DocumentAndSpaceCases()
    {
        Phase("document and active-space identity");
        var a=_doc;var aToken=_token;var request=Identity();request["handles"]=new JArray();
        var info=(await Success("get_current_document_info",new JObject()))["result"]!;
        Check(info.Value<bool>("isNamedDrawing")==false&&info.Value<string>("fileName")=="","new drawing is unnamed, not a fabricated DWG path");
        string h="";
        await Cmd(()=>Transaction((tr,space)=>{var l=new Line(Point3d.Origin,new Point3d(10,0,0));space.AppendEntity(l);tr.AddNewlyCreatedDBObject(l,true);h=l.Handle.ToString();}));
        var old=Move(new[]{h});
        var paper=await Read(tr=> { var names=new List<string>(); foreach(DBDictionaryEntry entry in (DBDictionary)tr.GetObject(_doc.Database.LayoutDictionaryId,OpenMode.ForRead)) { var layout=(Layout)tr.GetObject(entry.Value,OpenMode.ForRead); names.Add(layout.LayoutName); if(!layout.ModelType) { _report["discoveredLayouts"]=new JArray(names);return layout.LayoutName; } } throw new InvalidOperationException("Template has no paper layout"); });
        _report["paperLayoutName"]=paper;
        await Cmd(()=>{LayoutManager.Current.CurrentLayout=paper;RefreshIdentity();});
        Check(_spaceHandle!=old.Value<string>("activeSpaceHandle"),"paper space explicitly activated");
        var s=await Call("transform_entities",old);
        Check(!s.Value<bool>("success")&&s.Value<string>("errorCode")=="active_space_mismatch"&&!s.Value<bool>("committed"),"old model-space target rejected from paper space");
        await Cmd(()=>{LayoutManager.Current.CurrentLayout="Model";RefreshIdentity();});
        _doc=await App(()=>{Ensure();var b=Application.DocumentManager.Add("acadiso.dwt");_createdDocuments.Add(b);Application.DocumentManager.MdiActiveDocument=b;return b;});
        await HostTestDispatch.Ready(_doc);await App(()=>{RefreshIdentity();return true;});
        Check(_token!=aToken,"different active drawing gets distinct token");
        var d=await Call("transform_entities",old);
        Check(!d.Value<bool>("success")&&d.Value<string>("errorCode")=="document_mismatch"&&!d.Value<bool>("committed"),"old drawing request rejected in another active drawing");
        await App(()=>{Application.DocumentManager.MdiActiveDocument=a;return true;});_doc=a;await HostTestDispatch.Ready(_doc);await App(()=>{RefreshIdentity();return true;});
        Check(_token==aToken,"switching back preserves loaded Document token");
        Check(await Read(tr=>Near(((Line)tr.GetObject(Id(h),OpenMode.ForRead)).StartPoint,Point3d.Origin)),"rejected cross-document/space writes leave original geometry unchanged");
    }

    private async Task LegacyAndDynamicCases()
    {
        Phase("legacy consecutive create undo regression");
        var baseline=await Handles();
        var first=Created(await Write("create_line",Draw("wcs",new JObject{["start"]=P(10000),["end"]=P(10010)})));
        var second=Created(await Write("create_circle",Draw("wcs",new JObject{["center"]=P(11000),["radius"]=10})));
        await Success("get_current_document_info",new JObject());
        await Undo("legacy first U");var after1=await Handles();
        Observe(first.All(after1.Contains)&&second.All(h=>!after1.Contains(h)),"legacy consecutive writes U1 removes last only");
        await Undo("legacy second U");
        Observe((await Handles()).SetEquals(baseline),"legacy consecutive writes U2 removes first without empty step",new{first,second,remaining=(await Handles()).Except(baseline).ToArray()});
        await RemoveLeftovers(first.Concat(second).ToArray());

        Phase("Roslyn parameters diagnostics rollback auto/none");
        JObject Code(string body,string mode="auto")=>new(){["code"]=body,["parameters"]=new JObject{["nested"]=new JObject{["value"]=42}},["transactionMode"]=mode};
        const string add="var space=(BlockTableRecord)transaction.GetObject(database.CurrentSpaceId,OpenMode.ForWrite);var l=new Line(new Point3d(12000,0,7),new Point3d(12010,0,7));space.AppendEntity(l);transaction.AddNewlyCreatedDBObject(l,true);";
        var request=Code(add+"return new { handle=l.Handle.ToString(), value=parameters[\"nested\"].Value<int>(\"value\"), initial=context.InitialSelectionHandles.Count }; ");
        request["usings"]=new JArray("System.Text");
        var create=await Write("send_code_to_cad",request);var dynamicHandle=create["result"]!.Value<string>("handle")!;
        Check(create["result"]!.Value<int>("value")==42&&(await Handles()).Contains(dynamicHandle),"Roslyn nested JObject parameters and actual geometry");
        await Undo("dynamic auto single U");Observe(!(await Handles()).Contains(dynamicHandle),"dynamic auto single undo");await RemoveLeftovers(new[]{dynamicHandle});
        baseline=await Handles();
        var failure=await Call("send_code_to_cad",Code(add+"throw new InvalidOperationException(\"intentional transaction rollback\");"));
        Check(!failure.Value<bool>("success")&&failure.Value<string>("errorCode")=="runtime_exception"&&failure.Value<bool>("rolledBack")&&(await Handles()).SetEquals(baseline),"Roslyn runtime exception rolls back appended entity");
        var compilation=await Call("send_code_to_cad",Code("var a = 1;\nreturn missing_identifier;"));
        Check(compilation.Value<string>("errorCode")=="compilation_failed"&&((JArray)compilation["diagnostics"]!).OfType<JObject>().Any(d=>d.Value<string>("id")=="CS0103"&&d.Value<int?>("line")==2&&d.Value<string>("file")=="AI_CODE")&&(await Handles()).SetEquals(baseline),"Roslyn compilation diagnostics and zero entity writes",compilation["diagnostics"]);
        var none=await Success("send_code_to_cad",Code("return new { noTransaction=transaction==null, mode=context.TransactionMode };","none"));
        Check(none["result"]!.Value<bool>("noTransaction")&&!none.Value<bool>("committed")&&!none.Value<bool>("undoGuaranteed"),"none does not impersonate an automatic transaction");
        var owned=await Success("send_code_to_cad",Code("using(var own=database.TransactionManager.StartTransaction()){var space=(BlockTableRecord)own.GetObject(database.CurrentSpaceId,OpenMode.ForWrite);var line=new Line(new Point3d(13000,0,0),new Point3d(13010,0,0));space.AppendEntity(line);own.AddNewlyCreatedDBObject(line,true);var h=line.Handle.ToString();own.Commit();return new {handle=h};}","none"));
        var ownedHandle=owned["result"]!.Value<string>("handle")!;
        Check((await Handles()).Contains(ownedHandle)&&!owned.Value<bool>("undoGuaranteed"),"none can own an explicit write transaction without framework undo claim");await RemoveLeftovers(new[]{ownedHandle});
        // Query/mutate/delete through the same dynamic public tool, then inspect with fixed reads.
        var editSource=Created(await Success("create_line",Draw("wcs",new JObject{["start"]=P(14000),["end"]=P(14010)})))[0];
        var edit=Code("var id=database.GetObjectId(false,new Handle(Convert.ToInt64(parameters.Value<string>(\"handle\"),16)),0);var line=(Line)transaction.GetObject(id,OpenMode.ForWrite);line.EndPoint=new Point3d(14100,0,0);return new {length=line.Length};");edit["parameters"]!["handle"]=editSource;
        var edited=await Success("send_code_to_cad",edit);Check(Math.Abs(edited["result"]!.Value<double>("length")-100)<1e-7,"dynamic query and modification returns structured result");
        var erase=Code("var id=database.GetObjectId(false,new Handle(Convert.ToInt64(parameters.Value<string>(\"handle\"),16)),0);transaction.GetObject(id,OpenMode.ForWrite).Erase();return new {deleted=true};");erase["parameters"]!["handle"]=editSource;
        await Success("send_code_to_cad",erase);Check(!(await Handles()).Contains(editSource),"dynamic erase executes");

        Phase("dynamic consecutive writes undo regression");baseline=await Handles();
        var one=(await Write("send_code_to_cad",Code(add+"return new {handle=l.Handle.ToString()};")))["result"]!.Value<string>("handle")!;
        var two=(await Write("send_code_to_cad",Code(add+"return new {handle=l.Handle.ToString()};")))["result"]!.Value<string>("handle")!;
        await Undo("dynamic consecutive first U");var a=await Handles();Observe(a.Contains(one)&&!a.Contains(two),"dynamic consecutive U1 removes last only");
        await Undo("dynamic consecutive second U");Observe((await Handles()).SetEquals(baseline),"dynamic consecutive U2 removes first without empty step",new{one,two,remaining=(await Handles()).Except(baseline).ToArray()});
        await RemoveLeftovers(new[]{one,two});

        Phase("shared execution slot rejects reentrant call");
        var commands=(Dictionary<string,ICadCommand>)typeof(CommandRegistry).GetField("_commands",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_registry)!;
        var real=commands["say_hello"];commands["say_hello"]=new BusyProbe(_dispatcher);
        try{var busy=await Success("say_hello",new JObject());Check(busy["result"]!.Value<string>("errorCode")=="cad_busy","shared execution slot returns cad_busy without queuing");}finally{commands["say_hello"]=real;}
        await Success("say_hello",new JObject());
        var final=(await Success("get_current_document_info",new JObject()))["result"]!;
        Check(!final.Value<bool>("isNamedDrawing")&&final.Value<string>("fileName")=="","framework tests did not save owned drawing");
    }
    private sealed class BusyProbe : ICadCommand
    {
        private readonly CadDispatcher _dispatcher;public BusyProbe(CadDispatcher dispatcher)=>_dispatcher=dispatcher;
        public string Name=>"say_hello";public CadExecutionKind ExecutionKind=>CadExecutionKind.CommandContext;
        public JObject Execute(CadCommandContext c,JObject p)=>ExecutionResponses.Success(c.CallId,_dispatcher.ExecuteAsync("get_current_document_info",new JObject(),Guid.NewGuid().ToString()).GetAwaiter().GetResult());
    }
}
