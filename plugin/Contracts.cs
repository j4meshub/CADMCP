using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

public interface ICadCommand
{
    string Name { get; }
    // Mandatory classification: new commands cannot silently inherit the read-only path.
    CadExecutionKind ExecutionKind { get; }
    JObject Execute(CadCommandContext context, JObject parameters);
}

public sealed class CadCommandContext
{
    private readonly NativeCommandUndoScope? _nativeUndo;
    public CadCommandContext(Document document, CadMcpSettings settings, string callId,
        IReadOnlyList<ObjectId> initialSelectionObjectIds, IReadOnlyList<string> initialSelectionHandles)
        : this(document, settings, callId, initialSelectionObjectIds, initialSelectionHandles, null) { }
    internal CadCommandContext(Document document, CadMcpSettings settings, string callId,
        IReadOnlyList<ObjectId> initialSelectionObjectIds, IReadOnlyList<string> initialSelectionHandles,
        NativeCommandUndoScope? nativeUndo)
    { Document = document; Settings = settings; CallId = callId; InitialSelectionObjectIds = initialSelectionObjectIds; InitialSelectionHandles = initialSelectionHandles; _nativeUndo = nativeUndo; }
    // The public constructor deliberately grants no fixed-write undo capability.
    // Internal commands must be dispatched, not called from an arbitrary command/worker.
    public void RequireNativeUndoBoundary()
    {
        if (_nativeUndo == null)
            throw new CadCommandException("undo_unavailable", "缺少调度器创建的固定写入撤销边界");
        _nativeUndo.Require(Document);
    }
    public Document Document { get; }
    public CadMcpSettings Settings { get; }
    public string CallId { get; }
    public IReadOnlyList<ObjectId> InitialSelectionObjectIds { get; }
    public IReadOnlyList<string> InitialSelectionHandles { get; }
    public string DocumentToken => DocumentIdentity.GetToken(Document);
    public JObject WithIdentity(JObject value)
    {
        value["documentToken"] = DocumentToken;
        value["activeSpaceHandle"] = Document.Database.CurrentSpaceId.Handle.ToString();
        return value;
    }
    public IReadOnlyList<ObjectId>? RequestedSelection { get; private set; }
    public void RequestSelection(IReadOnlyList<ObjectId> ids) => RequestedSelection = new List<ObjectId>(ids);
}

public static class CompilerRuntimeStatus
{
    private static readonly object Gate = new();
    private static bool _isReady;
    private static string _message = "Roslyn 尚未初始化";
    private static string _details = string.Empty;
    public static bool IsReady { get { lock (Gate) return _isReady; } }
    public static string Message { get { lock (Gate) return _message; } }
    public static string Details { get { lock (Gate) return _details; } }
    public static void MarkReady(string message) { lock (Gate) { _isReady = true; _message = message; _details = string.Empty; } }
    public static void MarkFailed(string message, Exception error) { lock (Gate) { _isReady = false; _message = message; _details = error.ToString(); } }
}

public static class ExecutionResponses
{
    public static JObject Success(string callId, object? result, long durationMs = 0, string? mode = null,
        bool committed = false, bool undoGuaranteed = false, JArray? warnings = null) => new()
    {
        ["callId"] = callId, ["status"] = "completed", ["stage"] = "completed", ["success"] = true,
        ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result), ["diagnostics"] = new JArray(),
        ["warnings"] = warnings ?? new JArray(), ["errorCode"] = null, ["errorType"] = null, ["message"] = null,
        ["stackTrace"] = null, ["durationMs"] = durationMs, ["transactionMode"] = mode,
        ["committed"] = committed, ["rolledBack"] = false, ["undoGuaranteed"] = undoGuaranteed
    };

    public static JObject Failure(string callId, string code, Exception error, long durationMs = 0, string? mode = null,
        bool rolledBack = false, JArray? diagnostics = null, JArray? warnings = null) => new()
    {
        ["callId"] = callId, ["status"] = "failed", ["stage"] = code == "compilation_failed" || code == "compiler_initialization_failed" || code.StartsWith("reference_", StringComparison.Ordinal) || code == "invalid_reference" ? "compile" : "execute",
        ["success"] = false, ["result"] = null, ["diagnostics"] = diagnostics ?? new JArray(), ["warnings"] = warnings ?? new JArray(),
        ["errorCode"] = code, ["errorType"] = error.GetType().FullName, ["message"] = error.Message,
        ["stackTrace"] = error.ToString(), ["durationMs"] = durationMs, ["transactionMode"] = mode,
        ["committed"] = false, ["rolledBack"] = rolledBack, ["undoGuaranteed"] = false
    };
}

public static class ExecutionStatusStore
{
    private static readonly ConcurrentDictionary<string, JObject> Records = new();
    public static void Set(string callId, JObject value) { Records[callId] = value; Trim(); }
    public static JObject Get(string callId) => Records.TryGetValue(callId, out var value)
        ? (JObject)value.DeepClone()
        : new JObject { ["callId"] = callId, ["status"] = "not_found", ["stage"] = "status", ["success"] = false, ["result"] = null,
            ["diagnostics"] = new JArray(), ["warnings"] = new JArray(), ["errorCode"] = "call_not_found", ["errorType"] = null,
            ["message"] = "当前 AutoCAD 会话中没有此调用记录", ["stackTrace"] = null, ["durationMs"] = null, ["transactionMode"] = null,
            ["committed"] = false, ["rolledBack"] = false, ["undoGuaranteed"] = false };
    private static void Trim()
    {
        if (Records.Count <= 500) return;
        foreach (var key in Records.Keys) { Records.TryRemove(key, out _); if (Records.Count <= 400) break; }
    }
}

public static class RuntimeMetrics
{
    private static int _dynamicCompilationCount;
    public static int DynamicCompilationCount => _dynamicCompilationCount;
    public static int IncrementCompilationCount() => System.Threading.Interlocked.Increment(ref _dynamicCompilationCount);
}
