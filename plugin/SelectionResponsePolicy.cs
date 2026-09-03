using System;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

// A post-commit UI failure must never advertise a failed/rolled-back database operation.
internal static class SelectionResponsePolicy
{
    public static bool PreserveCommitted(JObject? response, Exception error, string code)
    {
        if (response?.Value<bool>("committed") != true) return false;
        var warnings = response["warnings"] as JArray;
        if (warnings == null) { warnings = new JArray(); response["warnings"] = warnings; }
        warnings.Add(new JObject { ["code"] = code, ["message"] = error.Message });
        if (response["result"] is JObject result && result["selectionApplied"] != null) result["selectionApplied"] = false;
        return true;
    }
}
