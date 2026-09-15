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

    public const string ResearchTable = "research";

    // The creation date is the database's own, in local time, so a row is dated by the write that
    // made it - the Research page stores a name, a url and the category the link belongs to.
    public static readonly OrderedDictionary<string, string> ResearchColumns = new (StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "INTEGER PRIMARY KEY AUTOINCREMENT",
        ["name"] = "TEXT NOT NULL DEFAULT ''",
        ["url"] = "TEXT NOT NULL DEFAULT ''",
        ["category"] = "TEXT NOT NULL DEFAULT 'None'",
        ["created"] = "TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))"
    };
}

