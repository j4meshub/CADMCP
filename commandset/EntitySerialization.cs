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
        value["objectIdScope"] = "runtimeDiagnosticOnly";
        value["coordinateSystem"] = "wcs"; value["unit"] = "Millimeters";
        if (geometry) AddDetails(value, entity, units, entity.Database.TransactionManager.TopTransaction, true, true, false);
        return value;
    }
    public static JObject Details(Entity entity, CadUnits units, Transaction tr, bool geometry, bool attributes, bool style)
    {
        var value = Summary(entity, units, false);
        AddDetails(value, entity, units, tr, geometry, attributes, style);
        return value;
    }
    private static void AddDetails(JObject value, Entity entity, CadUnits units, Transaction tr, bool geometry, bool attributes, bool style)
    {
        var warnings = new JArray(); value["warnings"] = warnings;
        if (value["boundingBoxWcs"]?.Type == JTokenType.Null) EntityDetails.Warn(warnings, "geometry_unavailable", "包围盒不可用");
        if (geometry)
        {
            try
            {
                value["geometry"] = EntityDetails.Geometry(entity, units, tr, warnings);
                if (value["geometry"]!.Type == JTokenType.Null) EntityDetails.Warn(warnings, "unsupported_entity_type", "未实现此实体的几何详情: " + entity.GetType().Name);
            }
            catch (CadCommandException) { throw; }
            catch (Exception error) { value["geometry"] = null; EntityDetails.Warn(warnings, "geometry_unavailable", error.Message); }
        }
        if (attributes && entity is BlockReference block)
        {
            value["attributeScope"] = "referencesOnly";
            try { value["attributes"] = EntityDetails.Attributes(block, units, tr); }
            catch (CadCommandException) { throw; }
            catch (Exception error) { value["attributes"] = null; EntityDetails.Warn(warnings, "attributes_unavailable", error.Message); }
        }
        if (style)
        {
            try { value["style"] = EntityDetails.Style(entity, tr); }
            catch (Exception error) { value["style"] = null; EntityDetails.Warn(warnings, "style_unavailable", error.Message); }
        }
    }
    public static JObject ColorJson(Color color)
    {
        if (color.IsByLayer) return new JObject { ["mode"] = "byLayer" };
        if (color.ColorMethod == ColorMethod.ByColor) return new JObject { ["mode"] = "rgb", ["red"] = color.Red, ["green"] = color.Green, ["blue"] = color.Blue };
        return new JObject { ["mode"] = "aci", ["index"] = color.ColorIndex };
    }
}
