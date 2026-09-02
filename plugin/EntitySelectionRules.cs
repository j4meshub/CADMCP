using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CADMCP.Plugin;

public sealed class CadCommandException : Exception
{
    public CadCommandException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}

// Pure rules shared by the host commands and host-independent tests.
public static class EntitySelectionRules
{
    public const int MaxHandles = 2000;

    public static string NormalizeHandle(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 16 ||
            !value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) ||
            !ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw new CadCommandException("invalid_parameters", "Handle 必须是非零、至多 16 位的十六进制字符串: " + value);
        return number.ToString("X", CultureInfo.InvariantCulture);
    }

    public static string[] NormalizeHandles(IEnumerable<string> handles, bool allowEmpty = false)
    {
        var values = handles.ToArray();
        if (values.Length > MaxHandles || (!allowEmpty && values.Length == 0))
            throw new CadCommandException("invalid_parameters", "handles 数量必须介于 " + (allowEmpty ? "0" : "1") + " 和 2000 之间");
        return values.Select(NormalizeHandle).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string[] Select(string mode, IEnumerable<string> previous, string[] targets, int? expectedCount)
    {
        var old = previous.Select(NormalizeHandle).Distinct(StringComparer.Ordinal).ToArray();
        IEnumerable<string> selected;
        switch (mode)
        {
            case "replace": selected = targets; break;
            case "add": selected = old.Concat(targets).Distinct(StringComparer.Ordinal); break;
            case "remove": selected = old.Except(targets, StringComparer.Ordinal); break;
            case "clear":
                if (targets.Length != 0) throw new CadCommandException("invalid_parameters", "clear 不接受非空 handles");
                selected = Array.Empty<string>(); break;
            default: throw new CadCommandException("invalid_parameters", "mode 必须是 replace/add/remove/clear");
        }
        var result = selected.ToArray();
        if (result.Length > MaxHandles) throw new CadCommandException("invalid_parameters", "最终选择不能超过 2000 个实体");
        if (expectedCount.HasValue && (expectedCount < 0 || expectedCount > MaxHandles))
            throw new CadCommandException("invalid_parameters", "expectedCount 必须介于 0 和 2000 之间");
        if (expectedCount.HasValue && result.Length != expectedCount.Value)
            throw new CadCommandException("count_mismatch", $"预计最终选择 {expectedCount} 个实体，实际为 {result.Length}");
        return result;
    }
}
