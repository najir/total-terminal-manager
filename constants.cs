// -----------------------------------------------------------------------------
//  Values shared across the app.
// -----------------------------------------------------------------------------

/// <summary>Constants shared across the app.</summary>
internal static class Constants
{
    public const string NotesTable = "notes";

    public static readonly OrderedDictionary<string, string> NotesColumns = new (StringComparer.OrdinalIgnoreCase)
    {
        ["filepath"] = "TEXT NOT NULL PRIMARY KEY",
        ["title"] = "TEXT NOT NULL DEFAULT ''",
        ["created"] = "TEXT NOT NULL DEFAULT (datetime('now'))",
        ["modified"] = "TEXT NOT NULL DEFAULT (datetime('now'))",
        ["tags"] = "TEXT NOT NULL DEFAULT ''",
        ["content"] = "TEXT NOT NULL DEFAULT ''",
        ["deleted"] = "INTEGER NOT NULL DEFAULT 0"
    };

    public static readonly string[] NotesReservedColumns = { "filepath", "title", "content", "deleted" };
}

