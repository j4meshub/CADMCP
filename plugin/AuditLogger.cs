using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

public static class AuditLogger
{
    private static readonly object Gate = new();
    public static void Write(string method, string callId, JObject parameters, JObject response)
    {
        try
        {
            Directory.CreateDirectory(RuntimePaths.LogDirectory); Cleanup();
            var settings = SettingsStore.Current;
            var entry = new JObject { ["timestamp"] = DateTimeOffset.Now, ["method"] = method, ["callId"] = callId,
                ["parameters"] = settings.LogSourceAndParameters ? parameters.DeepClone() : new JObject { ["redacted"] = true }, ["response"] = response.DeepClone() };
            lock (Gate) File.AppendAllText(Path.Combine(RuntimePaths.LogDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), entry.ToString(Formatting.None) + Environment.NewLine);
        }
        catch { }
    }
    private static void Cleanup()
    {
        var cutoff = DateTime.Now.AddDays(-SettingsStore.Current.LogRetentionDays);
        foreach (var file in Directory.EnumerateFiles(RuntimePaths.LogDirectory, "*.jsonl").Where(x => File.GetLastWriteTime(x) < cutoff))
            try { File.Delete(file); } catch { }
    }
}
