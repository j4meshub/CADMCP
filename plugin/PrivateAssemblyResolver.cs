using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CADMCP.Plugin;

/// <summary>Loads only the exact CADMCP/Roslyn assemblies declared below and only from Commands.</summary>
public sealed class PrivateAssemblyResolver : IDisposable
{
    private sealed class PrivateAssembly
    {
        public PrivateAssembly(string name, string fileName, string version, string publicKeyToken)
        { Name = name; FileName = fileName; Version = new Version(version); PublicKeyToken = publicKeyToken; }
        public string Name { get; }
        public string FileName { get; }
        public Version Version { get; }
        public string PublicKeyToken { get; }
        public string Key => MakeKey(Name, Version);
    }

    // System.Memory 4.0.1.2 binds to Unsafe 4.0.4.1, while Roslyn 4.8 binds to Unsafe 6.0.0.0.
    // Both strong-named identities are therefore packaged and loaded side by side.
    private static readonly PrivateAssembly[] Manifest =
    {
        new("System.Runtime.CompilerServices.Unsafe", "System.Runtime.CompilerServices.Unsafe.4.0.4.1.dll", "4.0.4.1", "b03f5f7f11d50a3a"),
        new("System.Runtime.CompilerServices.Unsafe", "System.Runtime.CompilerServices.Unsafe.dll", "6.0.0.0", "b03f5f7f11d50a3a"),
        new("System.Buffers", "System.Buffers.dll", "4.0.3.0", "cc7b13ffcd2ddd51"),
        new("System.Memory", "System.Memory.dll", "4.0.1.2", "cc7b13ffcd2ddd51"),
        new("System.Numerics.Vectors", "System.Numerics.Vectors.dll", "4.1.4.0", "b03f5f7f11d50a3a"),
        new("System.Threading.Tasks.Extensions", "System.Threading.Tasks.Extensions.dll", "4.2.0.1", "cc7b13ffcd2ddd51"),
        new("System.Collections.Immutable", "System.Collections.Immutable.dll", "7.0.0.0", "b03f5f7f11d50a3a"),
        new("System.Reflection.Metadata", "System.Reflection.Metadata.dll", "7.0.0.0", "b03f5f7f11d50a3a"),
        new("System.Text.Encoding.CodePages", "System.Text.Encoding.CodePages.dll", "7.0.0.0", "b03f5f7f11d50a3a"),
        new("Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.dll", "4.8.0.0", "31bf3856ad364e35"),
        new("Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis.CSharp.dll", "4.8.0.0", "31bf3856ad364e35")
    };

    private readonly string _privateDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);
    [ThreadStatic] private static HashSet<string>? _resolving;
    private bool _installed;

    public PrivateAssemblyResolver(string privateDirectory) =>
        _privateDirectory = Path.GetFullPath(privateDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public void Install()
    {
        if (_installed) return;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        _installed = true;
    }

    public void Preload()
    {
        if (!_installed) throw new InvalidOperationException("私有程序集解析器尚未安装");
        foreach (var spec in Manifest) LoadValidated(spec);
    }

    public string RunRoslynSelfTest()
    {
        var commandSet = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x =>
            string.Equals(x.GetName().Name, "CADMCP.CommandSet", StringComparison.OrdinalIgnoreCase));
        if (commandSet == null) throw new InvalidOperationException("CADMCP.CommandSet 尚未加载，无法执行 Roslyn 自检");
        var probe = commandSet.GetType("CADMCP.CommandSet.RoslynRuntimeProbe", true)!;
        try { return (string)(probe.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null) ?? "Roslyn 自检通过"); }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }

    private Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        AssemblyName requested;
        try { requested = new AssemblyName(args.Name); }
        catch { return null; }
        if (requested.Name == null || requested.Version == null || !IsPrivateRequester(args.RequestingAssembly)) return null;

        // Assemblies created with Assembly.Load(byte[]) live in the default load context.
        // Their CadExecutionContext/JObject wrapper still references the CommandSet, Plugin,
        // and CADMCP's Newtonsoft 13 assemblies that AutoCAD loaded with LoadFrom. Bridge
        // only those exact identities back to the already loaded instances; never probe or
        // load a second copy, because duplicate type identities would break invocation.
        if (IsDynamicContractAssembly(requested.Name))
            return ResolveLoadedCadMcpContract(requested);

        var spec = Manifest.FirstOrDefault(x => string.Equals(x.Name, requested.Name, StringComparison.OrdinalIgnoreCase) && x.Version == requested.Version);
        if (spec == null || !TokenMatchesRequest(requested, spec)) return null;

        _resolving ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_resolving.Add(spec.Key)) return null;
        try { return LoadValidated(spec); }
        catch (Exception error) { Trace.WriteLine("CADMCP private assembly resolution failed: " + error); return null; }
        finally { _resolving.Remove(spec.Key); }
    }

    private Assembly LoadValidated(PrivateAssembly spec)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(spec.Key, out var cached)) return cached;
            var path = Path.GetFullPath(Path.Combine(_privateDirectory, spec.FileName));
            if (!path.StartsWith(_privateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("私有程序集路径越界: " + path);
            if (!File.Exists(path)) throw new FileNotFoundException("CADMCP 私有依赖缺失", path);

            var actual = AssemblyName.GetAssemblyName(path);
            var token = Token(actual.GetPublicKeyToken());
            if (!string.Equals(actual.Name, spec.Name, StringComparison.OrdinalIgnoreCase) || actual.Version != spec.Version ||
                !string.Equals(token, spec.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException($"CADMCP 私有依赖身份不匹配: 期望 {spec.Name}, {spec.Version}, {spec.PublicKeyToken}; 实际 {actual.FullName}", path);

            var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => IdentityMatches(x.GetName(), actual));
            var assembly = alreadyLoaded ?? Assembly.LoadFrom(path);
            _loaded[spec.Key] = assembly;
            return assembly;
        }
    }

    private static bool IsPrivateRequester(Assembly? requester)
    {
        if (requester == null) return false;
        var name = requester.GetName().Name ?? string.Empty;
        return name.StartsWith("CADMCP.", StringComparison.OrdinalIgnoreCase) || Manifest.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDynamicContractAssembly(string name) =>
        string.Equals(name, "CADMCP.Plugin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "CADMCP.CommandSet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase);

    private static Assembly? ResolveLoadedCadMcpContract(AssemblyName requested)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => IdentityMatches(x.GetName(), requested))
            .ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
            Trace.WriteLine("CADMCP contract resolution rejected duplicate loaded identity: " + requested.FullName);
        return null;
    }

    private static bool TokenMatchesRequest(AssemblyName requested, PrivateAssembly spec)
    {
        var requestedToken = Token(requested.GetPublicKeyToken());
        return string.IsNullOrEmpty(requestedToken) || string.Equals(requestedToken, spec.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentityMatches(AssemblyName left, AssemblyName right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) && left.Version == right.Version &&
        string.Equals(Token(left.GetPublicKeyToken()), Token(right.GetPublicKeyToken()), StringComparison.OrdinalIgnoreCase);

    private static string MakeKey(string name, Version version) => name + "|" + version;
    private static string Token(byte[]? value) => value == null ? string.Empty : string.Concat(value.Select(x => x.ToString("x2")));

    public void Dispose()
    {
        if (!_installed) return;
        AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
        _installed = false;
    }
}
