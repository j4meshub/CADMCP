using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

// Host-independent path policy: the database filename can become an autosave path.
internal static class DocumentInfoValues
{
    public static JObject Create(string documentName, string databaseFileName, bool isNamed, int dbmod) => new()
    {
        ["name"] = documentName,
        ["fileName"] = isNamed ? documentName : string.Empty,
        ["isNamedDrawing"] = isNamed,
        ["databaseFileName"] = databaseFileName,
        ["dbmod"] = dbmod,
        ["isModified"] = dbmod != 0
    };
}
