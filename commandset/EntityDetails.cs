using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

// One shared serializer for selected/query/detail tools. All DBObjects stay inside the caller's transaction.
internal static class EntityDetails
{
    public static void Warn(JArray warnings, string code, string message) => warnings.Add(new JObject { ["code"] = code, ["message"] = message });
    private static JObject Vector(Vector3d v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
    private static void CheckSize(int count)
    {
        if (count > 20000) throw new CadCommandException("result_too_large", "单实体顶点/属性数量超过 20000，请关闭该项详情或用动态代码按需读取");
    }

    public static JToken Geometry(Entity entity, CadUnits units, Transaction tr, JArray warnings)
    {
        if (entity is Line line) return new JObject { ["start"] = GeometryInput.PointJson(line.StartPoint, units), ["end"] = GeometryInput.PointJson(line.EndPoint, units) };
        if (entity is Arc arc) return new JObject
        {
            ["center"] = GeometryInput.PointJson(arc.Center, units), ["radius"] = units.ToMillimeters(arc.Radius),
            ["start"] = GeometryInput.PointJson(arc.StartPoint, units), ["end"] = GeometryInput.PointJson(arc.EndPoint, units),
            ["startAngle"] = arc.StartAngle, ["endAngle"] = arc.EndAngle, ["angleUnit"] = "radians", ["angleCoordinateSystem"] = "ocs", ["normalWcs"] = Vector(arc.Normal)
        };
        if (entity is Circle circle) return new JObject { ["center"] = GeometryInput.PointJson(circle.Center, units), ["radius"] = units.ToMillimeters(circle.Radius), ["normalWcs"] = Vector(circle.Normal) };
        if (entity is Polyline polyline)
        {
            CheckSize(polyline.NumberOfVertices); var vertices = new JArray();
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var p = GeometryInput.PointJson(polyline.GetPoint3dAt(i), units); p["bulge"] = polyline.GetBulgeAt(i);
                p["startWidth"] = units.ToMillimeters(polyline.GetStartWidthAt(i)); p["endWidth"] = units.ToMillimeters(polyline.GetEndWidthAt(i)); vertices.Add(p);
            }
            return new JObject { ["verticesWcs"] = vertices, ["closed"] = polyline.Closed, ["normalWcs"] = Vector(polyline.Normal) };
        }
        if (entity is Solid solid)
        {
            var vertices = new JArray(); for (short i = 0; i < 4; i++) vertices.Add(GeometryInput.PointJson(solid.GetPointAt(i), units));
            return new JObject { ["verticesWcs"] = vertices, ["normalWcs"] = Vector(solid.Normal) };
        }
        if (entity is DBText text) return TextGeometry(text, units);
        if (entity is MText mtext) return MTextGeometry(mtext, units);
        if (entity is BlockReference block)
        {
            var record = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
            var effective = block.IsDynamicBlock ? (BlockTableRecord)tr.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead) : record;
            var matrix = block.BlockTransform.ToArray();
            foreach (var index in new[] { 3, 7, 11 }) matrix[index] = units.ToMillimeters(matrix[index]);
            return new JObject
            {
                ["position"] = GeometryInput.PointJson(block.Position, units), ["blockTableRecordHandle"] = block.BlockTableRecord.Handle.ToString(),
                ["name"] = record.Name, ["effectiveName"] = effective.Name, ["isDynamicBlock"] = block.IsDynamicBlock, ["isXref"] = record.IsFromExternalReference,
                ["rotation"] = block.Rotation, ["angleUnit"] = "radians", ["angleCoordinateSystem"] = "ocs", ["normalWcs"] = Vector(block.Normal),
                ["scaleFactors"] = new JObject { ["x"] = block.ScaleFactors.X, ["y"] = block.ScaleFactors.Y, ["z"] = block.ScaleFactors.Z },
                ["blockTransform"] = new JObject { ["values"] = new JArray(matrix), ["layout"] = "rowMajor4x4", ["mapping"] = "blockLocalMillimetersToWcsMillimeters" }
            };
        }
        if (entity is Leader leader)
        {
            CheckSize(leader.NumVertices); var vertices = new JArray();
            for (var i = 0; i < leader.NumVertices; i++) vertices.Add(GeometryInput.PointJson(leader.VertexAt(i), units));
            return new JObject
            {
                ["verticesWcs"] = vertices, ["hasArrowHead"] = leader.HasArrowHead,
                ["arrowTipWcs"] = leader.HasArrowHead && leader.NumVertices > 0 ? GeometryInput.PointJson(leader.VertexAt(0), units) : null,
                ["annotationHandle"] = leader.Annotation.IsNull ? null : leader.Annotation.Handle.ToString(), ["normalWcs"] = Vector(leader.Normal)
            };
        }
        if (entity is MLeader multi) return MLeaderGeometry(multi, units, warnings);
        if (entity is Dimension dimension) return DimensionGeometry(dimension, units);
        return JValue.CreateNull();
    }

    private static JObject TextGeometry(DBText text, CadUnits units) => new()
    {
        ["content"] = text.TextString, ["plainText"] = text.TextString, ["position"] = GeometryInput.PointJson(text.Position, units),
        ["alignmentPoint"] = GeometryInput.PointJson(text.AlignmentPoint, units), ["height"] = units.ToMillimeters(text.Height),
        ["rotation"] = text.Rotation, ["angleUnit"] = "radians", ["angleCoordinateSystem"] = "ocs", ["normalWcs"] = Vector(text.Normal),
        ["horizontalMode"] = text.HorizontalMode.ToString(), ["verticalMode"] = text.VerticalMode.ToString(),
        ["widthFactor"] = text.WidthFactor, ["oblique"] = text.Oblique, ["isMirroredInX"] = text.IsMirroredInX, ["isMirroredInY"] = text.IsMirroredInY
    };

    private static JObject MTextGeometry(MText text, CadUnits units) => new()
    {
        ["content"] = text.Contents, ["plainText"] = text.Text, ["position"] = GeometryInput.PointJson(text.Location, units),
        ["height"] = units.ToMillimeters(text.TextHeight), ["width"] = units.ToMillimeters(text.Width), ["rotation"] = text.Rotation,
        ["angleUnit"] = "radians", ["rotationReference"] = "AutoCADMTextRotation", ["directionWcs"] = Vector(text.Direction),
        ["normalWcs"] = Vector(text.Normal), ["attachment"] = text.Attachment.ToString()
    };

    public static JArray Attributes(BlockReference block, CadUnits units, Transaction tr)
    {
        var result = new JArray();
        // Read references only, never synthesize selectable handles for constant attribute definitions.
        foreach (ObjectId id in block.AttributeCollection)
        {
            CheckSize(result.Count + 1);
            if (!(tr.GetObject(id, OpenMode.ForRead, false) is AttributeReference attribute)) continue;
            var item = TextGeometry(attribute, units);
            item["handle"] = attribute.Handle.ToString(); item["blockHandle"] = block.Handle.ToString();
            item["tag"] = attribute.Tag; item["text"] = attribute.TextString; item["invisible"] = attribute.Invisible;
            item["isConstant"] = attribute.IsConstant; item["isMTextAttribute"] = attribute.IsMTextAttribute;
            if (attribute.IsMTextAttribute) using (var mtext = attribute.MTextAttribute) item["mtext"] = MTextGeometry(mtext, units);
            result.Add(item);
        }
        return result;
    }

    public static JObject Style(Entity entity, Transaction tr)
    {
        var value = new JObject { ["lineWeight"] = entity.LineWeight.ToString(), ["linetypeScale"] = entity.LinetypeScale };
        var styleId = entity is DBText text ? text.TextStyleId : entity is MText mtext ? mtext.TextStyleId : ObjectId.Null;
        if (!styleId.IsNull)
        {
            var textStyle = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
            value["textStyle"] = new JObject { ["handle"] = styleId.Handle.ToString(), ["name"] = textStyle.Name, ["fileName"] = textStyle.FileName, ["bigFontFileName"] = textStyle.BigFontFileName };
        }
        var dimStyleId = entity is Dimension dimension ? dimension.DimensionStyle : entity is Leader leader ? leader.DimensionStyle : ObjectId.Null;
        if (!dimStyleId.IsNull)
        {
            var dimStyle = (DimStyleTableRecord)tr.GetObject(dimStyleId, OpenMode.ForRead);
            value["dimensionStyle"] = new JObject { ["handle"] = dimStyleId.Handle.ToString(), ["name"] = dimStyle.Name };
        }
        return value;
    }

    private static JObject MLeaderGeometry(MLeader multi, CadUnits units, JArray warnings)
    {
        var leaders = new JArray(); var totalVertices = 0;
        foreach (int leaderIndex in multi.GetLeaderIndexes())
        {
            var lines = new JArray();
            foreach (int lineIndex in multi.GetLeaderLineIndexes(leaderIndex))
            {
                var vertices = new JArray(); var count = multi.VerticesCount(lineIndex); totalVertices += count; CheckSize(totalVertices);
                for (var i = 0; i < count; i++) vertices.Add(GeometryInput.PointJson(multi.GetVertex(lineIndex, i), units));
                lines.Add(new JObject { ["lineIndex"] = lineIndex, ["verticesWcs"] = vertices, ["headPointWcs"] = count > 0 ? GeometryInput.PointJson(multi.GetFirstVertex(lineIndex), units) : null });
            }
            leaders.Add(new JObject { ["leaderIndex"] = leaderIndex, ["lines"] = lines });
        }
        var value = new JObject { ["contentType"] = multi.ContentType.ToString(), ["leaders"] = leaders };
        if (multi.ContentType == ContentType.MTextContent) using (var text = multi.MText) value["content"] = MTextGeometry(text, units);
        else if (multi.ContentType == ContentType.BlockContent) Warn(warnings, "unsupported_mleader_content", "已返回引线结构；块内容与块属性尚未展开");
        return value;
    }

    private static JToken DimensionGeometry(Dimension dimension, CadUnits units)
    {
        var value = new JObject { ["dimensionType"] = dimension.GetType().Name, ["textOverride"] = dimension.DimensionText,
            ["textPosition"] = GeometryInput.PointJson(dimension.TextPosition, units), ["normalWcs"] = Vector(dimension.Normal) };
        if (dimension is RadialDimension radial)
        {
            value["center"] = GeometryInput.PointJson(radial.Center, units); value["chordPoint"] = GeometryInput.PointJson(radial.ChordPoint, units);
        }
        else if (dimension is DiametricDimension diameter)
        {
            value["chordPoint"] = GeometryInput.PointJson(diameter.ChordPoint, units); value["farChordPoint"] = GeometryInput.PointJson(diameter.FarChordPoint, units);
        }
        else if (dimension is AlignedDimension aligned)
        {
            value["xLine1Point"] = GeometryInput.PointJson(aligned.XLine1Point, units); value["xLine2Point"] = GeometryInput.PointJson(aligned.XLine2Point, units); value["dimLinePoint"] = GeometryInput.PointJson(aligned.DimLinePoint, units);
        }
        else if (dimension is RotatedDimension rotated)
        {
            value["xLine1Point"] = GeometryInput.PointJson(rotated.XLine1Point, units); value["xLine2Point"] = GeometryInput.PointJson(rotated.XLine2Point, units); value["dimLinePoint"] = GeometryInput.PointJson(rotated.DimLinePoint, units);
            value["rotation"] = rotated.Rotation; value["angleUnit"] = "radians"; value["angleCoordinateSystem"] = "ocs";
        }
        else return JValue.CreateNull();
        // Actual geometric length, not evaluated/rendered text (DIMLFAC and overrides can differ).
        value["measurement"] = units.ToMillimeters(dimension.Measurement); value["measurementUnit"] = "Millimeters";
        return value;
    }
}
