using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

public sealed class CadUnits
{
    private readonly double _millimetersPerDrawingUnit;
    public CadUnits(Database database, CadMcpSettings settings)
    {
        var units = database.Insunits;
        if (units == UnitsValue.Undefined) Enum.TryParse(settings.UnitlessDefaultUnit, true, out units);
        _millimetersPerDrawingUnit = MillimetersPerUnit(units);
    }
    public double FromMillimeters(double value) => value / _millimetersPerDrawingUnit;
    public double ToMillimeters(double value) => value * _millimetersPerDrawingUnit;
    public string ExternalUnit => "Millimeters";
    private static double MillimetersPerUnit(UnitsValue value)
    {
        switch (value)
        {
            case UnitsValue.Inches: return 25.4; case UnitsValue.Feet: return 304.8; case UnitsValue.Miles: return 1609344;
            case UnitsValue.Millimeters: return 1; case UnitsValue.Centimeters: return 10; case UnitsValue.Meters: return 1000;
            case UnitsValue.Kilometers: return 1000000; case UnitsValue.Mils: return 0.0254;
            case UnitsValue.Yards: return 914.4; case UnitsValue.Angstroms: return 0.0000001; case UnitsValue.Nanometers: return 0.000001;
            case UnitsValue.Microns: return 0.001; case UnitsValue.Decimeters: return 100;
            case UnitsValue.Hectometers: return 100000; case UnitsValue.Gigameters: return 1000000000000; case UnitsValue.Astronomical: return 149597870700000;
            case UnitsValue.LightYears: return 9460730472580800000d; case UnitsValue.Parsecs: return 30856775814913673000d;
            default: return 1;
        }
    }
}

internal static class GeometryInput
{
    public static Point3d Point(JToken token, CadUnits units, Editor editor, string system)
    {
        var point = new Point3d(units.FromMillimeters(token.Value<double>("x")), units.FromMillimeters(token.Value<double>("y")), units.FromMillimeters(token.Value<double?>("z") ?? 0));
        return system.Equals("ucs", StringComparison.OrdinalIgnoreCase) ? point.TransformBy(editor.CurrentUserCoordinateSystem) : point;
    }
    public static JObject PointJson(Point3d point, CadUnits units) => new() { ["x"] = units.ToMillimeters(point.X), ["y"] = units.ToMillimeters(point.Y), ["z"] = units.ToMillimeters(point.Z) };
    public static JObject ExtentsJson(Extents3d extents, CadUnits units) => new() { ["min"] = PointJson(extents.MinPoint, units), ["max"] = PointJson(extents.MaxPoint, units), ["coordinateSystem"] = "wcs", ["unit"] = "Millimeters" };
}
