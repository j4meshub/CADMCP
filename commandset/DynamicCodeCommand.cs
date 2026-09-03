using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using CADMCP.Plugin;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

public sealed class CadExecutionContext
{
    public CadExecutionContext(Document document, Transaction? transaction, CadUnits units, string callId, string transactionMode,
        IReadOnlyList<ObjectId> initialSelectionObjectIds, IReadOnlyList<string> initialSelectionHandles)
    { Document = document; Database = document.Database; Editor = document.Editor; Transaction = transaction; Units = units; CallId = callId; TransactionMode = transactionMode; InitialSelectionObjectIds = initialSelectionObjectIds; InitialSelectionHandles = initialSelectionHandles; }
    public Document Document { get; }
    public Database Database { get; }
    public Editor Editor { get; }
    public Transaction? Transaction { get; }
    public CadUnits Units { get; }
    public string CallId { get; }
    public string TransactionMode { get; }
    public IReadOnlyList<ObjectId> InitialSelectionObjectIds { get; }
    public IReadOnlyList<string> InitialSelectionHandles { get; }
}

public sealed class SendCodeToCadCommand : ICadCommand
{
    public string Name => "send_code_to_cad";
    public CadExecutionKind ExecutionKind => CadExecutionKind.CommandContext;
    public JObject Execute(CadCommandContext commandContext, JObject parameters)
    {
        var watch = Stopwatch.StartNew(); var mode = parameters.Value<string>("transactionMode") ?? "auto"; if (mode != "auto" && mode != "none") return ExecutionResponses.Failure(commandContext.CallId, "invalid_transaction_mode", new ArgumentException("transactionMode 只能是 auto 或 none"), 0, mode);
        var warnings = new JArray(); var committed = false;
        if (!CompilerRuntimeStatus.IsReady)
            return ExecutionResponses.Failure(commandContext.CallId, "compiler_initialization_failed",
                new InvalidOperationException(CompilerRuntimeStatus.Message + Environment.NewLine + CompilerRuntimeStatus.Details), 0, mode);
        try
        {
            var compiled = Compile(parameters.Value<string>("code") ?? throw new ArgumentException("code 不能为空"), parameters["usings"] as JArray, parameters["references"] as JArray, warnings);
            var count = RuntimeMetrics.IncrementCompilationCount(); if (count >= commandContext.Settings.DynamicAssemblyWarningThreshold) warnings.Add($"当前会话已动态编译 {count} 次，建议在合适时重启 AutoCAD 以释放程序集内存。");
            var namedParameters = parameters["parameters"] as JObject ?? new JObject(); object? raw;
            // Capability first: keep command context, full APIs and auto/none semantics.
            // Never nest ActiveX marks or gate arbitrary C# on strict UNDO conditions.
            warnings.Add(new JObject { ["code"] = "undo_not_guaranteed", ["message"] = "动态代码不提供严格撤销保证；不代表不能撤销。auto 保留框架事务，none 由代码管理事务及副作用。" });
            using (commandContext.Document.LockDocument())
            {
                if (mode == "auto")
                {
                    using (var tr = commandContext.Document.Database.TransactionManager.StartTransaction())
                    {
                        raw = Invoke(compiled, new CadExecutionContext(commandContext.Document, tr, new CadUnits(commandContext.Document.Database, commandContext.Settings), commandContext.CallId, mode, commandContext.InitialSelectionObjectIds, commandContext.InitialSelectionHandles), namedParameters);
                        tr.Commit(); committed = true;
                    }
                }
                else raw = Invoke(compiled, new CadExecutionContext(commandContext.Document, null, new CadUnits(commandContext.Document.Database, commandContext.Settings), commandContext.CallId, mode, commandContext.InitialSelectionObjectIds, commandContext.InitialSelectionHandles), namedParameters);
            }
            watch.Stop(); var serialized = SafeResult(raw, warnings);
            return ExecutionResponses.Success(commandContext.CallId, serialized, watch.ElapsedMilliseconds, mode, committed, false, warnings);
        }
        catch (Exception error) when (committed)
        {
            // A serialization/lock-disposal failure after Commit must not claim rollback
            // or encourage retrying code that has already changed the database.
            warnings.Add(new JObject { ["code"] = "post_commit_warning", ["message"] = "数据库已提交，但动态结果收尾失败；不要自动重试。" + Unwrap(error).Message });
            return ExecutionResponses.Success(commandContext.CallId, null, watch.ElapsedMilliseconds, mode, true, false, warnings);
        }
        catch (CompilationFailure error) { watch.Stop(); return ExecutionResponses.Failure(commandContext.CallId, "compilation_failed", error, watch.ElapsedMilliseconds, mode, false, error.Diagnostics, warnings); }
        catch (DynamicReferenceException error)
        {
            watch.Stop();
            var diagnostics = new JArray { new JObject { ["kind"] = "reference_manifest", ["references"] = error.Manifest } };
            return ExecutionResponses.Failure(commandContext.CallId, error.ErrorCode, error, watch.ElapsedMilliseconds, mode, false, diagnostics, warnings);
        }
        catch (Exception error) when (IsCompilerInfrastructureFailure(error))
        {
            watch.Stop(); var actual = Unwrap(error); CompilerRuntimeStatus.MarkFailed("Roslyn 运行时依赖加载失败，send_code_to_cad 暂不可用", actual);
            return ExecutionResponses.Failure(commandContext.CallId, "compiler_initialization_failed", actual, watch.ElapsedMilliseconds, mode, false, null, warnings);
        }
        catch (Exception error) { watch.Stop(); return ExecutionResponses.Failure(commandContext.CallId, "runtime_exception", Unwrap(error), watch.ElapsedMilliseconds, mode, mode == "auto", null, warnings); }
    }
    private static Type Compile(string body, JArray? usingValues, JArray? referenceValues, JArray warnings)
    {
        var defaults = new[] { "System", "System.Collections.Generic", "System.Linq", "Newtonsoft.Json", "Newtonsoft.Json.Linq", "Autodesk.AutoCAD.ApplicationServices", "Autodesk.AutoCAD.DatabaseServices", "Autodesk.AutoCAD.EditorInput", "Autodesk.AutoCAD.Geometry", "Autodesk.AutoCAD.Colors", "CADMCP.CommandSet" };
        var usings = defaults.Concat(usingValues?.Values<string>() ?? Enumerable.Empty<string>()).Distinct().Select(x => "using " + x + ";");
        var source = string.Join(Environment.NewLine, usings) + @"
namespace CADMCP.Dynamic
{
    public sealed class Script
    {
        public object Execute(CadExecutionContext context, JObject parameters)
        {
            var document = context.Document;
            var database = context.Database;
            var editor = context.Editor;
            var transaction = context.Transaction;
            var units = context.Units;
            var initialSelection = context.InitialSelectionObjectIds;
            var selectedHandles = context.InitialSelectionHandles;
#line 1 ""AI_CODE""
" + body + @"
#line default
        }
    }
}";
        var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var referenceSet = DynamicMetadataReferenceResolver.Resolve(referenceValues);
        foreach (var warning in referenceSet.Warnings) warnings.Add(warning.DeepClone());
        var compilation = CSharpCompilation.Create("CADMCP.Dynamic." + Guid.NewGuid().ToString("N"), new[] { syntax }, referenceSet.MetadataReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using (var stream = new MemoryStream())
        {
            var emit = compilation.Emit(stream); if (!emit.Success) throw new CompilationFailure(emit.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error || x.Severity == DiagnosticSeverity.Warning), referenceSet.Manifest);
            stream.Position = 0; return Assembly.Load(stream.ToArray()).GetType("CADMCP.Dynamic.Script", true)!;
        }
    }
    private static object? Invoke(Type scriptType, CadExecutionContext context, JObject parameters) => scriptType.GetMethod("Execute")!.Invoke(Activator.CreateInstance(scriptType), new object[] { context, parameters });
    private static Exception Unwrap(Exception error) => error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
    private static bool IsCompilerInfrastructureFailure(Exception error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            var text = current.ToString();
            if (text.IndexOf("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("System.Runtime.CompilerServices.Unsafe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("System.Memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("System.Collections.Immutable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("System.Reflection.Metadata", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }
    private static JToken SafeResult(object? value, JArray warnings)
    {
        if (value == null) return JValue.CreateNull();
        if (value is DBObject dbObject) { warnings.Add("返回值是活动 DBObject，已降级为类型和 ToString()，不会将对象带出事务。"); return new JObject { ["type"] = dbObject.GetType().FullName, ["value"] = dbObject.ToString() }; }
        try { return value is JToken token ? token : JToken.FromObject(value); }
        catch (Exception error) { warnings.Add("返回值无法直接序列化，已降级: " + error.Message); return new JObject { ["type"] = value.GetType().FullName, ["value"] = value.ToString() }; }
    }
    private sealed class CompilationFailure : Exception
    {
        public CompilationFailure(IEnumerable<Diagnostic> diagnostics, JArray referenceManifest) : base("C# 动态编译失败")
        {
            Diagnostics = new JArray(diagnostics.Select(x => new JObject { ["kind"] = "compiler", ["id"] = x.Id, ["severity"] = x.Severity.ToString(), ["message"] = x.GetMessage(), ["line"] = x.Location.GetMappedLineSpan().StartLinePosition.Line + 1, ["column"] = x.Location.GetMappedLineSpan().StartLinePosition.Character + 1, ["file"] = x.Location.GetMappedLineSpan().Path }));
            Diagnostics.Add(new JObject { ["kind"] = "reference_manifest", ["references"] = referenceManifest });
        }
        public JArray Diagnostics { get; }
    }
}

public static class RoslynRuntimeProbe
{
    public static string Run()
    {
        const string source = @"using CADMCP.CommandSet;
using Newtonsoft.Json.Linq;
public sealed class CadMcpRoslynProbe
{
    public static object Execute(CadExecutionContext context, JObject parameters)
    {
        return new JObject { [""success""] = true, [""inputCount""] = parameters.Count };
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var referenceSet = DynamicMetadataReferenceResolver.Resolve(null);
        var compilation = CSharpCompilation.Create("CADMCP.RoslynProbe", new[] { tree }, referenceSet.MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using (var stream = new MemoryStream())
        {
            var result = compilation.Emit(stream);
            if (!result.Success)
                throw new InvalidOperationException("Roslyn 自检编译失败: " + string.Join(" | ", result.Diagnostics.Select(x => x.ToString())));
            var probeAssembly = Assembly.Load(stream.ToArray());
            var probeType = probeAssembly.GetType("CadMcpRoslynProbe", true)!;
            var parameters = new JObject { ["probe"] = true };
            object? raw;
            try
            {
                raw = probeType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, new object?[] { null, parameters });
            }
            catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
            if (!(raw is JObject value) || value.Value<bool>("success") != true || value.Value<int>("inputCount") != 1)
                throw new InvalidOperationException("Roslyn 自检程序集已执行，但返回值不符合预期");
        }
        return "Roslyn 4.8 编译、内存加载与反射调用自检通过";
    }
}
