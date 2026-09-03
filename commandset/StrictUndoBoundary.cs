using CADMCP.Plugin;

namespace CADMCP.CommandSet;

// A guard, not an extra undo group. The dispatcher owns the native command's unit.
// Fixed create/edit commands only. Arbitrary dynamic code does not require this guard.
internal static class StrictUndoBoundary
{
    public static void Require(CadCommandContext context) => context.RequireNativeUndoBoundary();
}
