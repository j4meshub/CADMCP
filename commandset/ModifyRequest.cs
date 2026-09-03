using System;
using System.Linq;
using CADMCP.Plugin;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

// Parsed and tested without loading AutoCAD. Host validation is still mandatory afterwards.
internal sealed class ModifyRequest
{
    public bool DryRun { get; private set; }
    public bool SelectCreated { get; private set; }
    public string CoordinateSystem { get; private set; } = "ucs";
    public string[] Handles { get; private set; } = Array.Empty<string>();
    public JObject Operation { get; private set; } = new();

    public static ModifyRequest Parse(JObject parameters, string[] initialSelection, bool clone)
    {
        var source = Object(parameters["source"], "source");
        var kind = String(source["kind"], "source.kind");
        Keys(source, kind == "handles" ? new[] { "kind", "handles" } : new[] { "kind" });
        string[] handles;
        if (kind == "selection") handles = EntitySelectionRules.NormalizeHandles(initialSelection);
        else if (kind == "handles" && source["handles"] is JArray array && array.All(x => x.Type == JTokenType.String))
            handles = EntitySelectionRules.NormalizeHandles(array.Values<string>().Select(x => x!));
        else throw Invalid("source 仅支持 handles 或 selection");
        var expected = parameters["expectedCount"];
        if (expected?.Type != JTokenType.Integer || !int.TryParse(expected.ToString(), out var count) || count < 1 || count > EntitySelectionRules.MaxHandles)
            throw Invalid("expectedCount 必须是 1–2000 的整数");
        if (count != handles.Length) throw new CadCommandException("count_mismatch", $"预计 {count} 个顶层实体，实际 {handles.Length} 个");
        var system = parameters["coordinateSystem"] == null ? "ucs" : String(parameters["coordinateSystem"], "coordinateSystem");
        if (system != "ucs" && system != "wcs") throw Invalid("coordinateSystem 只能为 ucs/wcs");
        var operation = clone ? new JObject { ["type"] = "move", ["displacement"] = parameters["displacement"]?.DeepClone() } : Object(parameters["operation"], "operation");
        // Validate shape and finite arithmetic before touching the database.
        TransformMath.Local(operation, x => x);
        return new ModifyRequest { Handles = handles, CoordinateSystem = system, Operation = operation,
            DryRun = Flag(parameters, "dryRun"), SelectCreated = clone && Flag(parameters, "selectCreated") };
    }

    public static CadCommandException Invalid(string message) => new("invalid_parameters", message);
    public static JObject Object(JToken? token, string name) => token as JObject ?? throw Invalid(name + " 必须是对象");
    public static string String(JToken? token, string name) => token?.Type == JTokenType.String ? token.Value<string>()! : throw Invalid(name + " 必须是字符串");
    public static void Keys(JObject value, params string[] allowed)
    {
        if (value.Properties().Any(p => !allowed.Contains(p.Name))) throw Invalid("存在未知或互斥参数");
    }
    public static bool Flag(JObject value, string name)
    {
        if (value[name] == null) return false;
        if (value[name]!.Type != JTokenType.Boolean) throw Invalid(name + " 必须是布尔值");
        return value.Value<bool>(name);
    }
    public static double Number(JToken? token, string name)
    {
        if (token?.Type != JTokenType.Float && token?.Type != JTokenType.Integer) throw Invalid(name + " 必须是有限数值");
        var value = token.Value<double>(); Finite(value); return value;
    }
    public static void Finite(double value)
    {
        if (double.IsInfinity(value) || double.IsNaN(value)) throw Invalid("数值或变换结果超出有限范围");
    }
    public static double[] Point(JToken? token, string name)
    {
        var point = Object(token, name); Keys(point, "x", "y", "z");
        return new[] { Number(point["x"], name + ".x"), Number(point["y"], name + ".y"), point["z"] == null ? 0 : Number(point["z"], name + ".z") };
    }
}
