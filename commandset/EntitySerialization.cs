using System;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

internal static class EntitySerialization
{
    public static JObject Summary(Entity entity, CadUnits units, bool geometry)
    {
        var value = new JObject { ["handle"] = entity.Handle.ToString(), ["objectId"] = entity.ObjectId.OldIdPtr.ToInt64().ToString("X"), ["type"] = entity.GetRXClass().DxfName,
            ["layer"] = entity.Layer, ["color"] = ColorJson(entity.Color), ["linetype"] = entity.Linetype, ["visible"] = entity.Visible };
        try { value["boundingBoxWcs"] = GeometryInput.ExtentsJson(entity.GeometricExtents, units); } catch { value["boundingBoxWcs"] = null; }
        if (geometry) value["geometry"] = Geometry(entity, units); return value;
    }
    public static JObject ColorJson(Color color)
    {
        if (color.IsByLayer) return new JObject { ["mode"] = "byLayer" };
        if (color.ColorMethod == ColorMethod.ByColor) return new JObject { ["mode"] = "rgb", ["red"] = color.Red, ["green"] = color.Green, ["blue"] = color.Blue };
        return new JObject { ["mode"] = "aci", ["index"] = color.ColorIndex };
    }
    private static JToken Geometry(Entity entity, CadUnits units)
    {
        if (entity is Line line) return new JObject { ["start"] = GeometryInput.PointJson(line.StartPoint, units), ["end"] = GeometryInput.PointJson(line.EndPoint, units) };
        if (entity is Circle circle) return new JObject { ["center"] = GeometryInput.PointJson(circle.Center, units), ["radius"] = units.ToMillimeters(circle.Radius) };
        if (entity is Polyline polyline)
        {
            var vertices = new JArray(); for (var i = 0; i < polyline.NumberOfVertices; i++) { var point = polyline.GetPoint3dAt(i); vertices.Add(new JObject { ["x"] = units.ToMillimeters(point.X), ["y"] = units.ToMillimeters(point.Y), ["z"] = units.ToMillimeters(point.Z), ["bulge"] = polyline.GetBulgeAt(i) }); }
            return new JObject { ["verticesWcs"] = vertices, ["closed"] = polyline.Closed };
        }
        if (entity is MText text) return new JObject { ["content"] = text.Contents, ["position"] = GeometryInput.PointJson(text.Location, units), ["height"] = units.ToMillimeters(text.TextHeight), ["width"] = units.ToMillimeters(text.Width), ["rotation"] = text.Rotation };
        if (entity is BlockReference block) return new JObject { ["position"] = GeometryInput.PointJson(block.Position, units), ["blockTableRecordHandle"] = block.BlockTableRecord.Handle.ToString(), ["isXref"] = IsXref(block) };
        return JValue.CreateNull();
    }
    private static bool IsXref(BlockReference block)
    {
        try { using (var record = (BlockTableRecord)block.BlockTableRecord.GetObject(OpenMode.ForRead)) return record.IsFromExternalReference; } catch { return false; }
    }
}
