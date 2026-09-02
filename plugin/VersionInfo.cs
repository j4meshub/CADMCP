// Generated from version.json by scripts/set-version.ps1. Do not edit manually.
using System.Reflection;

namespace CADMCP.Plugin;

public static class CadMcpVersion
{
    public const string ProductVersion = "2.0.0";
    public const int ProtocolVersion = 2;
    public const int SettingsSchemaVersion = 1;

    public static string BuildVersion =>
        typeof(CadMcpVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? ProductVersion;
}
