using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CADMCP.Plugin;

public sealed class CadMcpSettings
{
    public const int CurrentSchemaVersion = 1;
    public static readonly string[] ToolNames = { "say_hello", "get_current_document_info", "get_selected_entities", "query_entities", "send_code_to_cad", "get_execution_status", "create_line", "create_polyline", "create_circle", "create_text" };
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int Port { get; set; } = 8080;
    public string UnitlessDefaultUnit { get; set; } = "Millimeters";
    public int DynamicAssemblyWarningThreshold { get; set; } = 100;
    public Dictionary<string, bool> EnabledTools { get; set; } = ToolNames.ToDictionary(x => x, _ => true, StringComparer.OrdinalIgnoreCase);
    public bool LogSourceAndParameters { get; set; } = true;
    public int LogRetentionDays { get; set; } = 30;
    public bool IsEnabled(string name) => EnabledTools.TryGetValue(name, out var enabled) && enabled;
    public void Normalize()
    {
        Port = Math.Max(1024, Math.Min(65535, Port));
        DynamicAssemblyWarningThreshold = Math.Max(1, DynamicAssemblyWarningThreshold);
        LogRetentionDays = Math.Max(1, LogRetentionDays);
        EnabledTools = ToolNames.ToDictionary(x => x, x => !EnabledTools.TryGetValue(x, out var enabled) || enabled, StringComparer.OrdinalIgnoreCase);
        SchemaVersion = CurrentSchemaVersion;
    }
}

public static class RuntimePaths
{
    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CADMCP");
    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CADMCP");
    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");
    public static string RuntimeDirectory => Path.Combine(LocalDirectory, "runtime");
    public static string SessionFile => Path.Combine(RuntimeDirectory, "session.json");
    public static string PluginDirectory => Path.GetDirectoryName(typeof(RuntimePaths).Assembly.Location)!;
}

public static class SettingsStore
{
    private static readonly object Gate = new();
    private static CadMcpSettings _current = LoadCore();
    public static CadMcpSettings Current { get { lock (Gate) return _current; } }
    public static CadMcpSettings Load() { lock (Gate) return _current = LoadCore(); }
    public static void Save(CadMcpSettings settings)
    {
        settings.Normalize(); Directory.CreateDirectory(RuntimePaths.SettingsDirectory);
        var temp = RuntimePaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(settings, Formatting.Indented));
        if (File.Exists(RuntimePaths.SettingsFile)) File.Replace(temp, RuntimePaths.SettingsFile, null); else File.Move(temp, RuntimePaths.SettingsFile);
        lock (Gate) _current = settings;
    }
    private static CadMcpSettings LoadCore()
    {
        try
        {
            var value = JsonConvert.DeserializeObject<CadMcpSettings>(File.ReadAllText(RuntimePaths.SettingsFile)) ?? new CadMcpSettings();
            value.Normalize(); return value;
        }
        catch { var value = new CadMcpSettings(); value.Normalize(); return value; }
    }
}
