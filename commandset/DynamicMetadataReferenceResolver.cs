using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CADMCP.Plugin;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

internal sealed class DynamicReferenceSet
{
    public DynamicReferenceSet(IReadOnlyList<MetadataReference> metadataReferences, JArray manifest, JArray warnings)
    { MetadataReferences = metadataReferences; Manifest = manifest; Warnings = warnings; }
    public IReadOnlyList<MetadataReference> MetadataReferences { get; }
    public JArray Manifest { get; }
    public JArray Warnings { get; }
}

internal sealed class DynamicReferenceException : Exception
{
    public DynamicReferenceException(string errorCode, string message, JArray? manifest = null) : base(message)
    { ErrorCode = errorCode; Manifest = manifest ?? new JArray(); }
    public string ErrorCode { get; }
    public JArray Manifest { get; }
}

internal static class DynamicMetadataReferenceResolver
{
    private static readonly HashSet<string> CompilerInfrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "System.Buffers", "System.Collections.Immutable",
        "System.Memory", "System.Numerics.Vectors", "System.Reflection.Metadata",
        "System.Runtime.CompilerServices.Unsafe", "System.Text.Encoding.CodePages",
        "System.Threading.Tasks.Extensions"
    };

    public static DynamicReferenceSet Resolve(JArray? requestedReferences)
    {
        var selected = new Dictionary<string, ReferenceCandidate>(StringComparer.OrdinalIgnoreCase);
        var warnings = new JArray();

        // These assemblies define the public wrapper contract. Pinning by runtime type is
        // intentional: it guarantees that JObject is CADMCP's Newtonsoft 13 type, not
        // AutoCAD 2022's Newtonsoft 11 type.
        foreach (var assembly in PinnedAssemblies()) AddPinned(selected, assembly);

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(IsUsable)
            .Where(x => !CompilerInfrastructure.Contains(x.GetName().Name ?? string.Empty))
            .OrderBy(x => x.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.GetName().Version)
            .ThenBy(SafeLocation, StringComparer.OrdinalIgnoreCase)
            .GroupBy(x => x.GetName().Name!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in loaded)
        {
            var candidates = group.GroupBy(x => x.GetName().FullName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToArray();
            if (selected.TryGetValue(group.Key, out var pinned))
            {
                foreach (var ignored in candidates.Where(x => !SameIdentity(x.GetName(), pinned.Identity)))
                    warnings.Add($"已忽略宿主中的冲突程序集 {ignored.GetName().FullName}；动态编译固定使用 {pinned.Identity.FullName}。");
                continue;
            }
            if (candidates.Length == 1) Add(selected, candidates[0], "host");
            else
                warnings.Add($"宿主中同时加载了多个 {group.Key} 版本，因没有安全的默认选择而未加入动态编译引用；需要时请通过 references 显式指定。");
        }

        AddRequested(selected, requestedReferences);

        var ordered = selected.Values.OrderBy(x => x.Identity.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var metadata = ordered.Select(x => MetadataReference.CreateFromFile(x.Path)).ToArray();
        var manifest = new JArray(ordered.Select(x => new JObject
        {
            ["name"] = x.Identity.Name,
            ["version"] = x.Identity.Version?.ToString(),
            ["publicKeyToken"] = Token(x.Identity.GetPublicKeyToken()),
            ["path"] = x.Path,
            ["source"] = x.Source
        }));
        return new DynamicReferenceSet(metadata, manifest, warnings);
    }

    private static IEnumerable<Assembly> PinnedAssemblies()
    {
        return new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(JObject).Assembly,
            typeof(ICadCommand).Assembly,
            typeof(CadExecutionContext).Assembly
        }.GroupBy(x => x.GetName().Name, StringComparer.OrdinalIgnoreCase).Select(x => x.First());
    }

    private static void AddPinned(IDictionary<string, ReferenceCandidate> selected, Assembly assembly)
    {
        if (!IsUsable(assembly)) throw new InvalidOperationException("固定动态编译引用没有可用路径: " + assembly.FullName);
        Add(selected, assembly, "pinned");
    }

    private static void Add(IDictionary<string, ReferenceCandidate> selected, Assembly assembly, string source)
    {
        var identity = assembly.GetName();
        selected[identity.Name!] = new ReferenceCandidate(identity, Path.GetFullPath(assembly.Location), source);
    }

    private static void AddRequested(IDictionary<string, ReferenceCandidate> selected, JArray? requestedReferences)
    {
        if (requestedReferences == null) return;
        foreach (var rawPath in requestedReferences.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!))
        {
            if (!Path.IsPathRooted(rawPath))
                throw new DynamicReferenceException("invalid_reference", "附加引用必须是本地 DLL 绝对路径: " + rawPath, Manifest(selected));
            var path = Path.GetFullPath(rawPath);
            if (!File.Exists(path))
                throw new DynamicReferenceException("reference_not_found", "附加引用文件不存在: " + path, Manifest(selected));

            AssemblyName identity;
            try { identity = AssemblyName.GetAssemblyName(path); }
            catch (Exception error) when (error is BadImageFormatException || error is FileLoadException)
            {
                throw new DynamicReferenceException("invalid_reference", "附加引用不是有效的托管 DLL: " + path, Manifest(selected));
            }

            if (identity.Name == null)
                throw new DynamicReferenceException("invalid_reference", "无法读取附加引用的程序集名称: " + path, Manifest(selected));
            if (selected.TryGetValue(identity.Name, out var existing))
            {
                if (SameIdentity(existing.Identity, identity) && string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                throw new DynamicReferenceException("reference_conflict",
                    $"附加引用 {identity.FullName} 与已经选择的 {existing.Identity.FullName} 冲突。为避免运行时类型不一致，不允许替换固定或已加载程序集。",
                    Manifest(selected));
            }

            try { Assembly.LoadFrom(path); }
            catch (Exception error) { throw new DynamicReferenceException("reference_load_failed", "无法加载附加引用: " + error.Message, Manifest(selected)); }
            selected[identity.Name] = new ReferenceCandidate(identity, path, "requested");
        }
    }

    private static JArray Manifest(IEnumerable<KeyValuePair<string, ReferenceCandidate>> selected) =>
        new(selected.Select(x => new JObject
        {
            ["name"] = x.Value.Identity.Name,
            ["version"] = x.Value.Identity.Version?.ToString(),
            ["path"] = x.Value.Path,
            ["source"] = x.Value.Source
        }));

    private static bool IsUsable(Assembly assembly)
    {
        try { return !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location); }
        catch { return false; }
    }

    private static string SafeLocation(Assembly assembly)
    {
        try { return assembly.Location ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool SameIdentity(AssemblyName left, AssemblyName right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) && left.Version == right.Version &&
        string.Equals(Token(left.GetPublicKeyToken()), Token(right.GetPublicKeyToken()), StringComparison.OrdinalIgnoreCase);

    private static string Token(byte[]? value) => value == null ? string.Empty : string.Concat(value.Select(x => x.ToString("x2")));

    private sealed class ReferenceCandidate
    {
        public ReferenceCandidate(AssemblyName identity, string path, string source)
        { Identity = identity; Path = path; Source = source; }
        public AssemblyName Identity { get; }
        public string Path { get; }
        public string Source { get; }
    }
}
