// -----------------------------------------------------------------------------
//  The SQLite engine: one database file created at startup and reachable from anywhere.
// -----------------------------------------------------------------------------

using Microsoft.Data.Sqlite;

namespace Sql;

/// <summary>The app's SQLite database. Init once at startup, then Connect from anywhere.</summary>
internal static class Engine
{
    public const string FileName = "total-manager.db";

    private const int TargetSchemaVersion = 7;

    private static readonly string CreateNotes = CreateTable (Constants.NotesTable, Constants.NotesColumns);

    private static readonly string CreateResearch = CreateTable (Constants.ResearchTable, Constants.ResearchColumns);

    private static string CreateTable (string table, OrderedDictionary<string, string> columns) =>
        $"CREATE TABLE IF NOT EXISTS {table} ("
        + string.Join (", ", columns.Select (column => $"{column.Key} {column.Value}"))
        + ");";

    private const string TouchNotes = """
        CREATE TRIGGER notes_touch_modified
        AFTER UPDATE ON notes FOR EACH ROW
        WHEN NEW.modified = OLD.modified
        BEGIN
            UPDATE notes SET modified = datetime('now') WHERE filepath = NEW.filepath;
        END;
        """;

    private const string DropTouchNotes = "DROP TRIGGER IF EXISTS notes_touch_modified;";

    private static readonly string DropNotes = $"DROP TABLE IF EXISTS {Constants.NotesTable};";

    private static readonly string AddDeleted =
        $"ALTER TABLE {Constants.NotesTable} ADD COLUMN deleted {Constants.NotesColumns ["deleted"]};";

    private static readonly string AddCategory =
        $"ALTER TABLE {Constants.ResearchTable} ADD COLUMN category {Constants.ResearchColumns ["category"]};";

    private static string _connectionString = "";

    public static string FilePath => Path.Combine (UserSettings.DatabasePath, FileName);

    public static bool Ready { get; private set; }

    public static string? Error { get; private set; }

    public static int SchemaVersion { get; private set; }

    public static void Init ()
    {
        try
        {
            Directory.CreateDirectory (UserSettings.DatabasePath);

            SqliteConnectionStringBuilder builder = new ()
            {
                DataSource = FilePath,

                Mode = SqliteOpenMode.ReadWriteCreate,

                ForeignKeys = true,

                Pooling = true
            };

            _connectionString = builder.ToString ();

            using SqliteConnection connection = Open ();

            Run (connection, "PRAGMA journal_mode = WAL;");

            Migrate (connection);

            Ready = true;
            Error = null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _connectionString = "";
            Ready = false;
            Error = ex.Message;
        }
    }

    public static SqliteConnection Connect ()
    {
        if (!Ready)
        {
            throw new InvalidOperationException (
                                                 $"The database is not initialized. {Error ?? "Engine.Init () has not been called."}");
        }

        return Open ();
    }

    private static void Migrate (SqliteConnection connection)
    {
        int version = Convert.ToInt32 (Run (connection, "PRAGMA user_version;") ?? 0);

        if (version >= TargetSchemaVersion)
        {
            SchemaVersion = version;

            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction ();

        if (version < 1)
        {
            Execute (connection, transaction, CreateNotes);
        }

        if (version < 2)
        {
            Execute (connection, transaction, DropTouchNotes);
            Execute (connection, transaction, TouchNotes);
        }

        if (version < 3)
        {
            Execute (connection, transaction, DropNotes);
            Execute (connection, transaction, CreateNotes);
            Execute (connection, transaction, DropTouchNotes);
            Execute (connection, transaction, TouchNotes);
        }

        if (version < 4)
        {
            Execute (connection, transaction, DropTouchNotes);
            Execute (connection, transaction, TouchNotes);
        }

        if (version < 5 && !HasColumn (connection, transaction, Constants.NotesTable, "deleted"))
        {
            Execute (connection, transaction, AddDeleted);
        }

        if (version < 6)
        {
            Execute (connection, transaction, CreateResearch);
        }

        if (version < 7 && !HasColumn (connection, transaction, Constants.ResearchTable, "category"))
        {
            Execute (connection, transaction, AddCategory);
        }

        Execute (connection, transaction, $"PRAGMA user_version = {TargetSchemaVersion};");

        transaction.Commit ();

        SchemaVersion = TargetSchemaVersion;
    }

    private static SqliteConnection Open ()
    {
        SqliteConnection connection = new (_connectionString);
        connection.Open ();

        using (SqliteCommand pragma = connection.CreateCommand ())
        {
            pragma.CommandText = "PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery ();
        }

        return connection;
    }

    private static object? Run (SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand ();
        command.CommandText = sql;

        return command.ExecuteScalar ();
    }

    private static void Execute (SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand ();
        command.CommandText = sql;
        command.Transaction = transaction;

        command.ExecuteNonQuery ();
    }

    private static bool HasColumn (SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using SqliteCommand command = connection.CreateCommand ();
        command.CommandText = $"PRAGMA table_info({table});";
        command.Transaction = transaction;

        using SqliteDataReader reader = command.ExecuteReader ();

        while (reader.Read ())
        {
            if (string.Equals (reader.GetString (1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

