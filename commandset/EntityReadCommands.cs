using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using CADMCP.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

internal static class EntityTargetResolver
{
    public static void ValidateContext(CadCommandContext context, JObject parameters, params string[] allowed)
    {
        var names = new HashSet<string>(allowed.Concat(new[] { "documentToken", "activeSpaceHandle", "callId" }), StringComparer.Ordinal);
        if (parameters.Properties().Any(p => !names.Contains(p.Name)))
            throw new CadCommandException("invalid_parameters", "存在未知参数");
        if (parameters["documentToken"]?.Type != JTokenType.String || !Guid.TryParse(parameters.Value<string>("documentToken"), out var token))
            throw new CadCommandException("invalid_parameters", "documentToken 必须来自最近的文档/选择/查询结果");
        if (token != Guid.Parse(context.DocumentToken))
            throw new CadCommandException("document_mismatch", "DWG 已切换或重新打开，请重新读取句柄与文档标识");
        if (parameters["activeSpaceHandle"]?.Type != JTokenType.String)
            throw new CadCommandException("invalid_parameters", "activeSpaceHandle 为必填十六进制句柄");
        if (EntitySelectionRules.NormalizeHandle(parameters.Value<string>("activeSpaceHandle")!) != context.Document.Database.CurrentSpaceId.Handle.ToString())
            throw new CadCommandException("active_space_mismatch", "活动空间已改变，请重新读取");
    }

    public static string[] Handles(JObject parameters, bool allowEmpty)
    {
        var value = parameters["handles"];
        if (value == null && allowEmpty) return Array.Empty<string>();
        if (!(value is JArray array) || array.Any(v => v.Type != JTokenType.String))
            throw new CadCommandException("invalid_parameters", "handles 必须是十六进制字符串数组");
        return EntitySelectionRules.NormalizeHandles(array.Values<string>().Select(x => x!), allowEmpty);
    }

    public static bool Flag(JObject parameters, string name, bool fallback)
    {
        if (parameters[name] == null) return fallback;
        if (parameters[name]!.Type != JTokenType.Boolean) throw new CadCommandException("invalid_parameters", name + " 必须是布尔值");
        return parameters.Value<bool>(name);
    }

    public static Entity[] Resolve(Database db, Transaction tr, string[] handles, bool forSelection = false)
    {
        var entities = new List<Entity>();
        foreach (var handle in handles)
        {
            ObjectId id;
            try { id = db.GetObjectId(false, new Handle(unchecked((long)ulong.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture))), 0); }
            catch { throw new CadCommandException("entity_not_found", "不存在的实体: " + handle); }
            if (id.IsNull || !id.IsValid || id.IsErased) throw new CadCommandException("entity_not_found", "实体不存在或已删除: " + handle);
            if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                throw new CadCommandException("wrong_entity_type", "句柄不是图形实体: " + handle);
            if (entity.OwnerId != db.CurrentSpaceId)
                throw new CadCommandException("entity_outside_active_space", "只支持当前空间顶层实体；块属性请通过父块读取: " + handle);
            if (forSelection)
            {
                var layer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
                if (!entity.Visible || layer.IsOff || layer.IsFrozen)
                    throw new CadCommandException("entity_not_selectable", "实体不可见，或图层已关闭/冻结: " + handle);
            }
            entities.Add(entity);
        }
        return entities.ToArray();
    }
}

public sealed class GetEntityDetailsCommand : ICadCommand
{
    public string Name => "get_entity_details";
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var watch = Stopwatch.StartNew();
        EntityTargetResolver.ValidateContext(context, parameters, "handles", "includeGeometry", "includeAttributes", "includeStyle");
        var handles = EntityTargetResolver.Handles(parameters, false);
        var geometry = EntityTargetResolver.Flag(parameters, "includeGeometry", true);
        var attributes = EntityTargetResolver.Flag(parameters, "includeAttributes", true);
        var style = EntityTargetResolver.Flag(parameters, "includeStyle", true);
        var items = new JArray();
        // Leave ample room for the execution and JSON-RPC envelopes within the 8 MiB frame.
        var bytes = 0;
        using (context.Document.LockDocument())
        using (var tr = context.Document.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var entities = EntityTargetResolver.Resolve(context.Document.Database, tr, handles);
            var units = new CadUnits(context.Document.Database, context.Settings);
            foreach (var entity in entities)
            {
                var item = EntitySerialization.Details(entity, units, tr, geometry, attributes, style);
                bytes += Encoding.UTF8.GetByteCount(item.ToString(Formatting.None));
                if (bytes > 7 * 1024 * 1024) throw new CadCommandException("result_too_large", "详情超过响应容量，请拆分句柄或关闭非必需详情");
                items.Add(item);
            }
        }
        return ExecutionResponses.Success(context.CallId, context.WithIdentity(new JObject
        {
            ["items"] = items, ["count"] = items.Count, ["truncated"] = false,
            ["coordinateSystem"] = "wcs", ["unit"] = "Millimeters"
        }), watch.ElapsedMilliseconds);
    }
}

public sealed class SetSelectionCommand : ICadCommand
{
    public string Name => "set_selection";
    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var watch = Stopwatch.StartNew();
        EntityTargetResolver.ValidateContext(context, parameters, "handles", "mode", "expectedCount");
        if (parameters["mode"] != null && parameters["mode"]!.Type != JTokenType.String)
            throw new CadCommandException("invalid_parameters", "mode 必须是字符串");
        var mode = parameters.Value<string>("mode") ?? "replace";
        var targets = EntityTargetResolver.Handles(parameters, mode == "clear");
        int? expected = null;
        if (parameters["expectedCount"] != null)
        {
            if (parameters["expectedCount"]!.Type != JTokenType.Integer ||
                !int.TryParse(parameters["expectedCount"]!.ToString(), out var value))
                throw new CadCommandException("invalid_parameters", "expectedCount 必须是整数");
            expected = value;
        }
        var previous = context.InitialSelectionHandles.ToArray();
        var selected = EntitySelectionRules.Select(mode, previous, targets, expected);
        using (context.Document.LockDocument())
        using (var tr = context.Document.Database.TransactionManager.StartOpenCloseTransaction())
        {
            // Validate even remove targets that are not currently selected.
            EntityTargetResolver.Resolve(context.Document.Database, tr, targets, mode != "remove");
            var final = EntityTargetResolver.Resolve(context.Document.Database, tr, selected, true);
            context.RequestSelection(final.Select(e => e.ObjectId).ToArray());
        }
        // The dispatcher applies and verifies this intent in application context before reporting success.
        return ExecutionResponses.Success(context.CallId, context.WithIdentity(new JObject
        {
            ["mode"] = mode, ["previousHandles"] = new JArray(previous),
            ["selectedHandles"] = new JArray(selected), ["count"] = selected.Length
        }), watch.ElapsedMilliseconds);
    }
}
