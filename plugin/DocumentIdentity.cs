using System;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;

namespace CADMCP.Plugin;

public static class DocumentIdentity
{
    private sealed class Identity { public readonly string Token = Guid.NewGuid().ToString(); }
    private static readonly ConditionalWeakTable<Document, Identity> Identities = new();
    // Stable for the loaded Document, not a filename or a cross-session DWG identifier.
    public static string GetToken(Document document) => Identities.GetValue(document, _ => new Identity()).Token;
}
