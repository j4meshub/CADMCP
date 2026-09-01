using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CADMCP.Plugin;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICadCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    public void Load()
    {
        _commands.Clear();
        var path = Path.Combine(RuntimePaths.PluginDirectory, "Commands", "CADMCP.CommandSet.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到内部命令集", path);
        foreach (var type in Assembly.LoadFrom(path).GetTypes().Where(t => !t.IsAbstract && typeof(ICadCommand).IsAssignableFrom(t)))
        {
            if (Activator.CreateInstance(type) is ICadCommand command) _commands[command.Name] = command;
        }
        var missing = CadMcpSettings.ToolNames.Where(x => x != "get_execution_status" && !_commands.ContainsKey(x)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("内部命令缺失: " + string.Join(", ", missing));
    }
    public ICadCommand Get(string name) => _commands.TryGetValue(name, out var command) ? command : throw new KeyNotFoundException("未知工具: " + name);
}
