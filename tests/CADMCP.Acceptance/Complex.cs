using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace CADMCP.Acceptance;
public sealed partial class AcceptanceRun
{
    private async Task Complex()
    {
        var fixtures = new List<string>(); var negative = new Dictionary<string,string>();
        Phase("complex fixtures");
        await Cmd(() => Transaction((tr, space) =>
        {
            var db = _doc.Database;
            T Add<T>(T e) where T:Entity { e.SetDatabaseDefaults(db); space.AppendEntity(e); tr.AddNewlyCreatedDBObject(e,true); fixtures.Add(e.Handle.ToString()); return e; }
            Add(new Line(new Point3d(0,0,7),new Point3d(100,10,7)));
            Add(new Circle(new Point3d(150,0,7),Vector3d.ZAxis,20));
            Add(new Arc(new Point3d(200,0,7),Vector3d.ZAxis,30,0.2,2.2));
            var pl = new Polyline(); pl.AddVertexAt(0,new Point2d(250,0),0.5,0,0); pl.AddVertexAt(1,new Point2d(300,0),0,0,0); pl.AddVertexAt(2,new Point2d(300,50),0,0,0); pl.Closed=true; pl.Elevation=7; Add(pl);
            Add(new Solid(new Point3d(350,0,7),new Point3d(400,0,7),new Point3d(350,50,7),new Point3d(400,50,7)));
            Add(new DBPoint(new Point3d(450,0,7)));
            Add(new DBText { Position=new Point3d(0,100,7), TextString="TEXT ABC123", Height=10 });
            Add(new MText { Location=new Point3d(150,100,7), Contents="MTEXT ABC123\\Psecond line",TextHeight=10,Width=100 });
            var leader = new Leader(); leader.SetDatabaseDefaults(db); leader.AppendVertex(new Point3d(300,100,7)); leader.AppendVertex(new Point3d(350,140,7)); leader.AppendVertex(new Point3d(400,140,7)); Add(leader);
            var ml = new MLeader(); ml.SetDatabaseDefaults(db); ml.ContentType=ContentType.MTextContent;
            using(var text = new MText { Contents="MLEADER ABC123",Location=new Point3d(600,150,7),TextHeight=10,Width=150 }) ml.MText=text;
            ml.TextLocation=new Point3d(600,150,7); var line=ml.AddLeaderLine(new Point3d(500,100,7)); ml.AddLastVertex(line,new Point3d(580,150,7)); Add(ml);
            var dims = new Dimension[] {
                new AlignedDimension(new Point3d(0,200,7),new Point3d(100,200,7),new Point3d(50,240,7),"",db.Dimstyle),
                new RotatedDimension(0.2,new Point3d(150,200,7),new Point3d(250,220,7),new Point3d(200,260,7),"",db.Dimstyle),
                new RadialDimension(new Point3d(350,200,7),new Point3d(380,200,7),20,"",db.Dimstyle),
                new DiametricDimension(new Point3d(450,200,7),new Point3d(510,200,7),20,"",db.Dimstyle) };
            foreach(var d in dims) { Add(d); d.RecomputeDimensionBlock(true); }
            var hatch = Add(new Hatch()); hatch.Elevation=7; hatch.SetHatchPattern(HatchPatternType.PreDefined,"SOLID"); hatch.Associative=false;
            hatch.AppendLoop(HatchLoopTypes.External,new Point2dCollection(new[]{new Point2d(600,200),new Point2d(650,200),new Point2d(650,250),new Point2d(600,250)}),new DoubleCollection(new[]{0d,0,0,0})); hatch.EvaluateHatch(true);
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
            var def=new BlockTableRecord { Name="CADMCP_ACCEPTANCE_ATTR_"+Guid.NewGuid().ToString("N") }; bt.Add(def);tr.AddNewlyCreatedDBObject(def,true);
            var circle=new Circle(Point3d.Origin,Vector3d.ZAxis,30); circle.SetDatabaseDefaults(db); def.AppendEntity(circle);tr.AddNewlyCreatedDBObject(circle,true);
            var definitions=new List<AttributeDefinition>();
            for(int i=0;i<2;i++) { var a=new AttributeDefinition { Position=new Point3d(0,i*15,0),TextString="attribute "+i,Tag=i==0?"VISIBLE":"HIDDEN",Prompt="test",Height=10,Invisible=i==1 }; a.SetDatabaseDefaults(db);def.AppendEntity(a);tr.AddNewlyCreatedDBObject(a,true);definitions.Add(a); }
            var block=Add(new BlockReference(new Point3d(800,200,7),def.ObjectId){Rotation=0.2,ScaleFactors=new Scale3d(1.5)});
            foreach(var a in definitions) { var ar=new AttributeReference();ar.SetAttributeFromBlock(a,block.BlockTransform);ar.TextString=a.TextString;block.AttributeCollection.AppendAttribute(ar);tr.AddNewlyCreatedDBObject(ar,true); }
            var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForWrite);
            foreach(var kind in new[]{"locked","off","frozen"}) { var layer=new LayerTableRecord{Name="CADMCP_TEST_"+kind};layers.Add(layer);tr.AddNewlyCreatedDBObject(layer,true);var e=new Line(new Point3d(0,-100,0),new Point3d(20,-100,0));e.SetDatabaseDefaults(db);e.LayerId=layer.ObjectId;space.AppendEntity(e);tr.AddNewlyCreatedDBObject(e,true);negative[e.Handle.ToString()]=kind=="locked"?"layer_locked":"entity_not_selectable";layer.IsLocked=kind=="locked";layer.IsOff=kind=="off";layer.IsFrozen=kind=="frozen"; }
            var annot=new MText{Contents="annotative",Location=new Point3d(0,-200,0),TextHeight=10};annot.SetDatabaseDefaults(db);space.AppendEntity(annot);tr.AddNewlyCreatedDBObject(annot,true);annot.Annotative=AnnotativeStates.True;negative[annot.Handle.ToString()]="unsupported_entity_type";
            var invisible=new Line(new Point3d(0,-250,0),new Point3d(20,-250,0));invisible.SetDatabaseDefaults(db);space.AppendEntity(invisible);tr.AddNewlyCreatedDBObject(invisible,true);invisible.Visible=false;negative[invisible.Handle.ToString()]="entity_not_selectable";
            var solid3d=new Solid3d();solid3d.SetDatabaseDefaults(db);solid3d.CreateBox(10,10,10);space.AppendEntity(solid3d);tr.AddNewlyCreatedDBObject(solid3d,true);negative[solid3d.Handle.ToString()]="unsupported_entity_type";
        }));
        _report["syntheticFixtures"] = new JArray(fixtures); _report["negativeFixtures"]=JObject.FromObject(negative);
        var synthetic=fixtures.ToArray(); var baseline=await Handles(); var original=await Details(synthetic);
        Check(original.Count==16,"16 supported synthetic types/details readable");
        var clone=await Write("clone_entities",Clone(synthetic,2000)); var copies=Copies(clone);
        Check(copies.Length==synthetic.Length,"complex clone complete top-level mapping");
        var copyDetails=await Details(copies); Check(copyDetails.Count==synthetic.Length,"complex copied details readable");
        await VerifyBlockCopies(synthetic,copies,2000);
        await Undo("complex clone one U"); Check((await Handles()).SetEquals(baseline)&&JToken.DeepEquals(original,await Details(synthetic)),"complex clone undo preserves all sources");
        await Write("transform_entities",Move(synthetic)); await Details(synthetic);
        await Undo("complex move one U"); Check(JToken.DeepEquals(original,await Details(synthetic)),"complex move/undo exact details");

        Phase("conservative target rejection");
        foreach(var invalid in negative) { var before=await State(); var r=await Call("clone_entities",Clone(new[]{synthetic[0],invalid.Key})); Check(r.Value<string>("errorCode")==invalid.Value&&!r.Value<bool>("committed")&&(await Handles()).SetEquals(baseline)&&JToken.DeepEquals(original,await Details(synthetic)),"mixed invalid target rejected atomically "+invalid.Value); }

        Phase("import original dynamic blocks into isolated test drawing");
        var imported=new List<string>();
        await Cmd(() =>
        {
            using(_original.LockDocument(DocumentLockMode.Read,null,null,false))
            Transaction((tr,space)=>
            {
                var ids=new ObjectIdCollection(new[]{"2AF3FD","2AF433"}.Select(h=>_original.Database.GetObjectId(false,new Handle(Convert.ToInt64(h,16)),0)).ToArray());
                using(var map=new IdMapping())
                { _original.Database.WblockCloneObjects(ids,_space,map,DuplicateRecordCloning.Ignore,false);foreach(ObjectId id in ids){var target=map[id].Value;var b=(BlockReference)tr.GetObject(target,OpenMode.ForWrite);b.TransformBy(Matrix3d.Displacement(new Point3d(3000+imported.Count*3000,1000,7)-b.Position));imported.Add(b.Handle.ToString());} }
            });
        });
        _report["importedDynamicHandles"]=new JArray(imported);
        var dyn=await BlockState(imported.ToArray()); _report["dynamicBeforeClone"]=dyn;
        Check(dyn.OfType<JObject>().All(b=>b.Value<bool>("dynamic")) && dyn[0]![("attributes")] is JArray attrs && attrs.Count==15,"real dynamic frame retains 15 attributes and dynamic state after import");
        var dynamicOriginal=await Details(imported.ToArray()); var dynamicBaseline=await Handles();
        var dynamicClone=await Write("clone_entities",Clone(imported.ToArray(),10000)); var dynamicCopies=Copies(dynamicClone);
        await VerifyBlockCopies(imported.ToArray(),dynamicCopies,10000);
        await Undo("dynamic blocks clone one U");Check((await Handles()).SetEquals(dynamicBaseline)&&JToken.DeepEquals(dynamicOriginal,await Details(imported.ToArray())),"dynamic clone undo/source details unchanged");
        await Write("transform_entities",Move(imported.ToArray(),50)); await Undo("dynamic blocks move one U");Check(JToken.DeepEquals(dyn,await BlockState(imported.ToArray())),"dynamic block positions/attributes/properties restored");

        Phase("native text mirror setting 0 and 1"); var oldSetting=await App(()=>Convert.ToInt32(Application.GetSystemVariable("MIRRTEXT")));
        try
        {
            foreach(var mode in new[]{0,1})
            {
                await Cmd(()=>Application.SetSystemVariable("MIRRTEXT",mode));
                var textHandles=new[]{synthetic[6],synthetic[7]};var before=await Details(textHandles);
                var mirror=Targets(textHandles);mirror["operation"]=JObject.Parse("{type:'mirror',axisStart:{x:1000,y:0},axisEnd:{x:1000,y:1000}}");
                await Write("transform_entities",mirror);
                var state=await Read(tr=>{var t=(DBText)tr.GetObject(Id(textHandles[0]),OpenMode.ForRead);var m=(MText)tr.GetObject(Id(textHandles[1]),OpenMode.ForRead);return new {mode=Convert.ToInt32(Application.GetSystemVariable("MIRRTEXT")),textForward=!t.IsMirroredInX&&!t.IsMirroredInY&&t.Normal.Z>0.999&&Math.Abs(t.Rotation)<1e-8,mtextForward=m.Normal.Z>0.999&&m.Direction.X>0.999,height=Math.Abs(t.Position.Z-7)<1e-8&&Math.Abs(m.Location.Z-7)<1e-8};});
                Observe(state.mode==mode&&state.height&&(mode==0?state.textForward&&state.mtextForward:!state.textForward&&!state.mtextForward),"TEXT/MTEXT native mirror MIRRTEXT="+mode,state);
                await Undo("text mirror mode "+mode+" one U");Check(JToken.DeepEquals(before,await Details(textHandles)),"text mirror undo "+mode);
            }
        }
        finally { await Cmd(()=>Application.SetSystemVariable("MIRRTEXT",oldSetting)); }
    }
    private Task<JArray> BlockState(string[] handles)=>Read(tr=>new JArray(handles.Select(h=>
    {
        if(!(tr.GetObject(Id(h),OpenMode.ForRead) is BlockReference b))return null;
        var attrs=new JArray(b.AttributeCollection.Cast<ObjectId>().Select(id=>{var a=(AttributeReference)tr.GetObject(id,OpenMode.ForRead);return new JObject{["tag"]=a.Tag,["text"]=a.TextString,["invisible"]=a.Invisible,["position"]=JArray.FromObject(a.Position.ToArray())};}));
        var props=new JArray();if(b.IsDynamicBlock)foreach(DynamicBlockReferenceProperty p in b.DynamicBlockReferencePropertyCollection)props.Add(new JObject{["name"]=p.PropertyName,["value"]=JToken.FromObject(p.Value)});
        return new JObject{["handle"]=h,["definition"]=b.BlockTableRecord.Handle.ToString(),["dynamic"]=b.IsDynamicBlock,["position"]=JArray.FromObject(b.Position.ToArray()),["attributes"]=attrs,["properties"]=props};
    }).Where(x=>x!=null)));
    private async Task VerifyBlockCopies(string[] source,string[] copies,double dx)
    {
        var a=await BlockState(source);var b=await BlockState(copies); Check(a.Count==b.Count,"block copy count");
        for(int i=0;i<a.Count;i++)
        {
            var x=a[i]!;var y=b[i]!;
            Check(x.Value<string>("definition")==y.Value<string>("definition")&&x.Value<bool>("dynamic")==y.Value<bool>("dynamic")&&JToken.DeepEquals(x["properties"],y["properties"]),"block definition/dynamic parameters preserved "+i);
            var aa=(JArray)x["attributes"]!;var bb=(JArray)y["attributes"]!;Check(aa.Count==bb.Count,"block attribute count "+i);
            for(int j=0;j<aa.Count;j++)Check(aa[j]!.Value<string>("tag")==bb[j]!.Value<string>("tag")&&aa[j]!.Value<string>("text")==bb[j]!.Value<string>("text")&&aa[j]!.Value<bool>("invisible")==bb[j]!.Value<bool>("invisible")&&Math.Abs(bb[j]!["position"]![0]!.Value<double>()-aa[j]!["position"]![0]!.Value<double>()-dx)<1e-6&&Math.Abs(bb[j]!["position"]![1]!.Value<double>()-aa[j]!["position"]![1]!.Value<double>())<1e-6,"attribute content and single displacement "+i+"/"+j);
        }
    }
}
