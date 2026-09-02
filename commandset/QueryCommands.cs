using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

public sealed class SayHelloCommand : ICadCommand
{
    public string Name => "say_hello";
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var message = parameters.Value<string>("message") ?? "你好，我已连接 AutoCAD。"; context.Document.Editor.WriteMessage("\nCADMCP: " + message);
        return ExecutionResponses.Success(context.CallId, new { message, document = context.Document.Name });
    }
}

public sealed class CurrentDocumentInfoCommand : ICadCommand
{
    public string Name => "get_current_document_info";
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var db = context.Document.Database; var units = new CadUnits(db, context.Settings);
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var layer = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead); var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            var count = currentSpace.Cast<ObjectId>().Count();
            return ExecutionResponses.Success(context.CallId, context.WithIdentity(new JObject { ["name"] = context.Document.Name, ["fileName"] = db.Filename, ["isModified"] = Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("DBMOD")) != 0,
                ["activeSpace"] = db.TileMode ? "model" : "paper", ["activeSpaceHandle"] = db.CurrentSpaceId.Handle.ToString(), ["insunits"] = db.Insunits.ToString(), ["externalUnit"] = units.ExternalUnit,
                ["currentLayer"] = layer.Name, ["entityCount"] = count, ["coordinateSystemDefault"] = "ucs" }));
        }
    }
}

public sealed class SelectedEntitiesCommand : ICadCommand
{
    public string Name => "get_selected_entities";
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var ids = context.InitialSelectionObjectIds; var limit = Math.Min(2000, Math.Max(1, parameters.Value<int?>("limit") ?? 200)); var include = parameters.Value<bool?>("includeGeometry") ?? false; var result = new JArray();
        if (ids.Count > 0)
        {
            using (var tr = context.Document.Database.TransactionManager.StartOpenCloseTransaction()) { var units = new CadUnits(context.Document.Database, context.Settings); foreach (var id in ids.Take(limit)) if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity) result.Add(EntitySerialization.Summary(entity, units, include)); }
        }
        return ExecutionResponses.Success(context.CallId, context.WithIdentity(new JObject { ["items"] = result, ["count"] = result.Count, ["truncated"] = ids.Count > limit }));
    }
}

public sealed class QueryEntitiesCommand : ICadCommand
{
    public string Name => "query_entities";
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var db = context.Document.Database; var units = new CadUnits(db, context.Settings); var limit = Math.Min(2000, Math.Max(1, parameters.Value<int?>("limit") ?? 200)); var include = parameters.Value<bool?>("includeGeometry") ?? false;
        var types = Set(parameters["entityTypes"]); var layers = Set(parameters["layers"]); var linetypes = Set(parameters["linetypes"]); var items = new JArray(); var matched = 0;
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (var id in space)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity) || !Matches(entity, types, layers, linetypes, parameters["color"] as JObject)) continue;
                if (!MatchesBox(entity, parameters["boundingBox"] as JObject, units, context.Document.Editor, parameters.Value<string>("coordinateSystem") ?? "ucs")) continue;
                matched++; if (items.Count < limit) items.Add(EntitySerialization.Summary(entity, units, include));
            }
        }
        return ExecutionResponses.Success(context.CallId, context.WithIdentity(new JObject { ["items"] = items, ["count"] = items.Count, ["matched"] = matched, ["limit"] = limit, ["truncated"] = matched > limit, ["activeSpaceOnly"] = true }));
    }
    private static HashSet<string> Set(JToken? token) => new((token?.Values<string>() ?? Enumerable.Empty<string?>()).Where(x => x != null).Select(x => x!), StringComparer.OrdinalIgnoreCase);
    private static bool Matches(Entity entity, HashSet<string> types, HashSet<string> layers, HashSet<string> linetypes, JObject? color)
    {
        if (types.Count > 0 && !types.Contains(entity.GetRXClass().DxfName) && !types.Contains(entity.GetType().Name)) return false;
        if (layers.Count > 0 && !layers.Contains(entity.Layer)) return false; if (linetypes.Count > 0 && !linetypes.Contains(entity.Linetype)) return false;
        if (color == null) return true; var actual = EntitySerialization.ColorJson(entity.Color); return JToken.DeepEquals(actual, color);
    }
    private static bool MatchesBox(Entity entity, JObject? box, CadUnits units, Editor editor, string system)
    {
        if (box == null) return true;
        try
        {
            var minToken = box["min"]!; var maxToken = box["max"]!; var minX = minToken.Value<double>("x"); var minY = minToken.Value<double>("y"); var minZ = minToken.Value<double?>("z") ?? 0; var maxX = maxToken.Value<double>("x"); var maxY = maxToken.Value<double>("y"); var maxZ = maxToken.Value<double?>("z") ?? 0;
            Extents3d? query = null;
            foreach (var x in new[] { minX, maxX }) foreach (var y in new[] { minY, maxY }) foreach (var z in new[] { minZ, maxZ })
            {
                var point = GeometryInput.Point(new JObject { ["x"] = x, ["y"] = y, ["z"] = z }, units, editor, system);
                if (query == null) query = new Extents3d(point, point); else { var value = query.Value; value.AddPoint(point); query = value; }
            }
            var q = query!.Value; var ext = entity.GeometricExtents; return !(ext.MaxPoint.X < q.MinPoint.X || ext.MinPoint.X > q.MaxPoint.X || ext.MaxPoint.Y < q.MinPoint.Y || ext.MinPoint.Y > q.MaxPoint.Y || ext.MaxPoint.Z < q.MinPoint.Z || ext.MinPoint.Z > q.MaxPoint.Z);
        }
        catch { return false; }
    }
}
