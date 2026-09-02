using System;
using System.Linq;
using CADMCP.Plugin;

internal static class Program
{
    private static int _tests;
    private static void Main()
    {
        Equal(new[] { "A", "F", "FFFFFFFFFFFFFFFF" }, new[] { "000a", "f", "ffffffffffffffff" }.Select(EntitySelectionRules.NormalizeHandle).ToArray());
        foreach (var value in new[] { "", "0", "0000", " 1", "1 ", "0x10", "-1", "G", "10000000000000000" })
            Reject("invalid_parameters", () => EntitySelectionRules.NormalizeHandle(value));
        Equal(new[] { "A", "B" }, EntitySelectionRules.NormalizeHandles(new[] { "a", "0A", "b" }));
        Reject("invalid_parameters", () => EntitySelectionRules.NormalizeHandles(Array.Empty<string>()));
        Reject("invalid_parameters", () => EntitySelectionRules.NormalizeHandles(Enumerable.Repeat("A", 2001)));
        Equal(Array.Empty<string>(), EntitySelectionRules.NormalizeHandles(Array.Empty<string>(), true));
        var old = new[] { "A", "B" };
        Equal(new[] { "C" }, EntitySelectionRules.Select("replace", old, new[] { "C" }, 1));
        Equal(new[] { "A", "B", "C" }, EntitySelectionRules.Select("add", old, new[] { "B", "C" }, 3));
        Equal(new[] { "A" }, EntitySelectionRules.Select("remove", old, new[] { "B", "D" }, 1));
        Equal(Array.Empty<string>(), EntitySelectionRules.Select("clear", old, Array.Empty<string>(), 0));
        Reject("count_mismatch", () => EntitySelectionRules.Select("add", old, new[] { "C" }, 1));
        Reject("invalid_parameters", () => EntitySelectionRules.Select("clear", old, new[] { "A" }, null));
        Reject("invalid_parameters", () => EntitySelectionRules.Select("invalid", old, new[] { "A" }, null));
        Reject("invalid_parameters", () => EntitySelectionRules.Select("replace", old, new[] { "A" }, -1));
        Equal(new[] { "A", "B" }, old); // Failed calculations cannot mutate the original selection.
        var settings = new CadMcpSettings();
        settings.EnabledTools["get_selected_entities"] = false;
        settings.EnabledTools.Remove("get_entity_details"); settings.EnabledTools.Remove("set_selection");
        settings.EnabledTools["old_tool"] = true;
        settings.Normalize();
        Check(settings.EnabledTools.Count == 12 && !settings.IsEnabled("get_selected_entities"), "preserve preferences");
        Check(settings.IsEnabled("get_entity_details") && settings.IsEnabled("set_selection"), "new tools enabled");
        Check(!settings.EnabledTools.ContainsKey("old_tool") && settings.SchemaVersion == CadMcpVersion.SettingsSchemaVersion, "normalize without schema bump");
        Console.WriteLine($"Passed {_tests} host-independent checks.");
    }
    private static void Equal(string[] expected, string[] actual) => Check(expected.SequenceEqual(actual), "ordered selection mismatch");
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        _tests++;
    }
    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (CadCommandException error) when (error.Code == code) { _tests++; return; }
        throw new Exception("Expected failure: " + code);
    }
}
