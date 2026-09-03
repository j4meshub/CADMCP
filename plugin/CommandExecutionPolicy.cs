using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

// Internal host contract, not an MCP input. Read docs/architecture/EXECUTION_CONTEXTS.md
// before adding a category. In particular transactionMode=none NEVER means read-only.
public enum CadExecutionKind
{
    CommandContext = 0,
    ReadOnly,
    Selection,
    PreviewableWrite,
    // Fixed creation: strict native undo, but no public preview contract.
    FixedWrite
}

public static class CommandExecutionPolicy
{
    public static CadExecutionKind Resolve(CadExecutionKind declared, JObject parameters)
    {
        if (declared == CadExecutionKind.ReadOnly || declared == CadExecutionKind.Selection) return declared;
        if (declared != CadExecutionKind.PreviewableWrite) return CadExecutionKind.CommandContext;
        var preview = parameters["dryRun"];
        if (preview != null && preview.Type != JTokenType.Boolean)
            throw new CadCommandException("invalid_parameters", "dryRun 必须是布尔值");
        return preview?.Value<bool>() == true ? CadExecutionKind.ReadOnly : CadExecutionKind.CommandContext;
    }

    public static bool PreserveInitialSelection(CadExecutionKind declared) =>
        declared == CadExecutionKind.Selection || RequiresNativeUndo(declared);

    // Used only after routing into a real command callback. A preview gets no scope.
    // Arbitrary C# must never inherit strict UNDO prerequisites.
    public static bool RequiresNativeUndo(CadExecutionKind declared) =>
        declared == CadExecutionKind.FixedWrite || declared == CadExecutionKind.PreviewableWrite;
}
