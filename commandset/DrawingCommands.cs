using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

public abstract class AtomicCreateCommand : ICadCommand
{
    public abstract string Name { get; }
    public CadExecutionKind ExecutionKind => CadExecutionKind.FixedWrite;
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var watch = Stopwatch.StartNew();
        JObject? response = null; var committed = false; var transactionStarted = false;
        try
        {
            // Validate the dispatcher's capability before touching CAD. This is not
            // another undo group; EXECUTEFUNCTION already owns the native unit.
            StrictUndoBoundary.Require(context);
            var db = context.Document.Database; var units = new CadUnits(db, context.Settings);
            var system = parameters.Value<string>("coordinateSystem") ?? "ucs";
            var handles = new JArray(); var created = new JArray();
            using (context.Document.LockDocument(Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Write, null, null, false))
            using (var tr = db.TransactionManager.StartTransaction())
            {
                transactionStarted = true;
                var items = parameters["items"] as JArray ?? throw new ArgumentException("items 必须是数组");
                if (items.Count == 0) throw new ArgumentException("items 不能为空");
                ValidateResources(items, tr, db);
                StrictUndoBoundary.Require(context);
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (JObject item in items)
                {
                    using (var entity = Create(item, units, context.Document.Editor, system))
                    {
                        entity.SetDatabaseDefaults(db); Apply(entity, item, tr, db);
                        space.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true);
                        handles.Add(entity.Handle.ToString()); created.Add(EntitySerialization.Summary(entity, units, false));
                    }
                }
                // Materialize the response before committing. No live DBObject escapes.
                response = ExecutionResponses.Success(context.CallId, new JObject { ["handles"] = handles, ["items"] = created, ["count"] = handles.Count, ["inputCoordinateSystem"] = system, ["unit"] = "Millimeters" }, watch.ElapsedMilliseconds, "auto", true, false);
                if (Encoding.UTF8.GetByteCount(response.ToString(Formatting.None)) > 7 * 1024 * 1024)
                    throw new CadCommandException("result_too_large", "创建结果过大，请减少每批数量");
                tr.Commit(); committed = true;
            }
            response!["durationMs"] = watch.ElapsedMilliseconds;
            // Only the dispatcher, after native command completion, sets undoGuaranteed.
            return response;
        }
        catch (Exception error)
        {
            if (committed && response != null)
            {
                ((JArray)response["warnings"]!).Add(new JObject { ["code"] = "post_commit_warning", ["message"] = error.Message });
                return response;
            }
            return ExecutionResponses.Failure(context.CallId,
                error is CadCommandException business ? business.Code : error is KeyNotFoundException ? "missing_resource" : "invalid_parameters",
                error, watch.ElapsedMilliseconds, "auto", transactionStarted);
        }
    }
    protected abstract Entity Create(JObject item, CadUnits units, Autodesk.AutoCAD.EditorInput.Editor editor, string system);
    private static void ValidateResources(JArray items, Transaction tr, Database db)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead); var linetypes = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        foreach (JObject item in items) { var layer = item.Value<string>("layer"); if (layer != null && !layers.Has(layer)) throw new KeyNotFoundException("图层不存在: " + layer); var linetype = item.Value<string>("linetype"); if (linetype != null && !linetypes.Has(linetype)) throw new KeyNotFoundException("线型不存在: " + linetype); }
    }
    private static void Apply(Entity entity, JObject item, Transaction tr, Database db)
    {
        var layer = item.Value<string>("layer"); if (layer != null) entity.Layer = layer; var linetype = item.Value<string>("linetype"); if (linetype != null) entity.Linetype = linetype;
        if (item["color"] is JObject color) { var mode = color.Value<string>("mode"); if (mode == "byLayer") entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256); else if (mode == "aci") entity.Color = Color.FromColorIndex(ColorMethod.ByAci, color.Value<short>("index")); else if (mode == "rgb") entity.Color = Color.FromRgb(color.Value<byte>("red"), color.Value<byte>("green"), color.Value<byte>("blue")); }
    }
}

public sealed class CreateLineCommand : AtomicCreateCommand
{
    public override string Name => "create_line";
    protected override Entity Create(JObject item, CadUnits units, Autodesk.AutoCAD.EditorInput.Editor editor, string system) => new Line(GeometryInput.Point(item["start"]!, units, editor, system), GeometryInput.Point(item["end"]!, units, editor, system));
}
public sealed class CreateCircleCommand : AtomicCreateCommand
{
    public override string Name => "create_circle";
    protected override Entity Create(JObject item, CadUnits units, Autodesk.AutoCAD.EditorInput.Editor editor, string system)
    {
        var center = new Point3d(units.FromMillimeters(item["center"]!.Value<double>("x")), units.FromMillimeters(item["center"]!.Value<double>("y")), units.FromMillimeters(item["center"]!.Value<double?>("z") ?? 0));
        var circle = new Circle(center, Vector3d.ZAxis, units.FromMillimeters(item.Value<double>("radius"))); if (system.Equals("ucs", StringComparison.OrdinalIgnoreCase)) circle.TransformBy(editor.CurrentUserCoordinateSystem); return circle;
    }
}
public sealed class CreatePolylineCommand : AtomicCreateCommand
{
    public override string Name => "create_polyline";
    protected override Entity Create(JObject item, CadUnits units, Autodesk.AutoCAD.EditorInput.Editor editor, string system)
    {
        var vertices = item["vertices"] as JArray ?? throw new ArgumentException("vertices 必须是数组"); if (vertices.Count < 2) throw new ArgumentException("多段线至少需要两个顶点"); var polyline = new Polyline(vertices.Count);
        for (var i = 0; i < vertices.Count; i++) { var p = vertices[i]!; polyline.AddVertexAt(i, new Point2d(units.FromMillimeters(p.Value<double>("x")), units.FromMillimeters(p.Value<double>("y"))), p.Value<double?>("bulge") ?? 0, 0, 0); }
        polyline.Closed = item.Value<bool?>("closed") ?? false; if (system.Equals("ucs", StringComparison.OrdinalIgnoreCase)) polyline.TransformBy(editor.CurrentUserCoordinateSystem); return polyline;
    }
}
public sealed class CreateTextCommand : AtomicCreateCommand
{
    public override string Name => "create_text";
    protected override Entity Create(JObject item, CadUnits units, Autodesk.AutoCAD.EditorInput.Editor editor, string system)
    {
        var position = item["position"]!; var text = new MText { Contents = item.Value<string>("content") ?? "", Location = new Point3d(units.FromMillimeters(position.Value<double>("x")), units.FromMillimeters(position.Value<double>("y")), units.FromMillimeters(position.Value<double?>("z") ?? 0)), TextHeight = units.FromMillimeters(item.Value<double>("height")), Width = units.FromMillimeters(item.Value<double?>("width") ?? 0), Rotation = item.Value<double?>("rotation") ?? 0 };
        text.Attachment = Attachment(item.Value<string>("attachment") ?? "topLeft"); if (system.Equals("ucs", StringComparison.OrdinalIgnoreCase)) text.TransformBy(editor.CurrentUserCoordinateSystem); return text;
    }
    private static AttachmentPoint Attachment(string value) { AttachmentPoint result; return Enum.TryParse(value.Replace("top", "Top").Replace("middle", "Middle").Replace("bottom", "Bottom").Replace("Left", "Left").Replace("Center", "Center").Replace("Right", "Right"), true, out result) ? result : AttachmentPoint.TopLeft; }
}
