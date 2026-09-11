// -----------------------------------------------------------------------------
//  Reconciles the notes folder with the notes table once per launch.
// -----------------------------------------------------------------------------

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mode;

internal static class NotesSync
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string? Error { get; private set; }

    public static int Moved { get; private set; }

    public static int Deleted { get; private set; }

    public static int Restored { get; private set; }

    public static int Refreshed { get; private set; }

    public static int Imported { get; private set; }

    /// <summary>A row of the notes table: every column, keyed by name.</summary>
    private sealed record Row (string Path, Dictionary<string, string> Values, bool Deleted);

    /// <summary>A file on disk, parsed the way a save parses it.</summary>
    private sealed class Disk
    {
        public Disk (string path, OrderedDictionary<string, string> values)
        {
            Path = path;
            Values = values;
        }

        public string Path { get; }

        public string Name => System.IO.Path.GetFileName (Path);

        public OrderedDictionary<string, string> Values { get; }

        public bool Claimed { get; set; }
    }

    public static void Run ()
    {
        Error = null;
        Moved = Deleted = Restored = Refreshed = Imported = 0;

        if (!Engine.Ready)
        {
            return;
        }

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteTransaction transaction = connection.BeginTransaction ();

            Reconcile (connection, transaction);

            transaction.Commit ();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    public static void MarkDeleted (string path)
    {
        using SqliteConnection connection = Engine.Connect ();

        MarkDeleted (connection, null, path);
    }

    private static void Reconcile (SqliteConnection connection, SqliteTransaction transaction)
    {
        List<Row> rows = ReadRows (connection, transaction);

        HashSet<string> known = new (rows.Select (r => r.Path), StringComparer.OrdinalIgnoreCase);

        List<Disk> strays = new ();

        if (Directory.Exists (UserSettings.NotesPath))
        {
            foreach (string file in Directory.EnumerateFiles (UserSettings.NotesPath, "*", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath (file);

                if (known.Contains (full) || Housekeeping (full))
                {
                    continue;
                }

                if (Read (full) is { } disk)
                {
                    strays.Add (disk);
                }
            }
        }

        List<Row> orphans = new ();

        foreach (Row row in rows)
        {
            if (!File.Exists (row.Path))
            {
                orphans.Add (row);

                continue;
            }

            if (Read (row.Path) is not { } disk)
            {
                continue;
            }

            if (row.Deleted)
            {
                Update (connection, transaction, row.Path, disk, null);
                Restored++;
            }
            else if (Differs (row, disk))
            {
                Update (connection, transaction, row.Path, disk, null);
                Refreshed++;
            }
        }

        HashSet<Row> matched = new ();

        foreach (Row orphan in orphans.OrderBy (o => o.Deleted))
        {
            Disk? match = strays.Where (s => !s.Claimed && SameContent (orphan, s))
                                .OrderByDescending (s => SameName (orphan, s))
                                .FirstOrDefault ();

            if (match is not null)
            {
                Claim (connection, transaction, orphan, match, matched);
            }
        }

        foreach (Row orphan in orphans.Where (o => !o.Deleted && !matched.Contains (o)))
        {
            List<Disk> named = strays.Where (s => !s.Claimed && SameName (orphan, s)).ToList ();

            if (named.Count == 1)
            {
                Claim (connection, transaction, orphan, named [0], matched);
            }
        }

        foreach (Row orphan in orphans.Where (o => !o.Deleted && !matched.Contains (o)))
        {
            MarkDeleted (connection, transaction, orphan.Path);
            Deleted++;
        }

        foreach (Disk stray in strays.Where (s => !s.Claimed))
        {
            Insert (connection, transaction, stray);
            Imported++;
        }
    }

    private static void Claim (SqliteConnection connection, SqliteTransaction transaction, Row orphan, Disk disk, HashSet<Row> matched)
    {
        disk.Claimed = true;
        matched.Add (orphan);

        Update (connection, transaction, orphan.Path, disk, disk.Path);

        if (orphan.Deleted)
        {
            Restored++;
        }
        else
        {
            Moved++;
        }
    }

    private static List<Row> ReadRows (SqliteConnection connection, SqliteTransaction transaction)
    {
        List<Row> rows = new ();
        string[] names = Constants.NotesColumns.Keys.ToArray ();

        using SqliteCommand read = connection.CreateCommand ();
        read.Transaction = transaction;
        read.CommandText = $"SELECT {string.Join (", ", names)} FROM {Constants.NotesTable};";

        using SqliteDataReader reader = read.ExecuteReader ();

        while (reader.Read ())
        {
            Dictionary<string, string> values = new (StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < names.Length; i++)
            {
                values [names [i]] = reader.IsDBNull (i) ? "" : reader.GetValue (i).ToString () ?? "";
            }

            bool deleted = long.TryParse (values ["deleted"], out long flag) && flag != 0;

            rows.Add (new (values ["filepath"], values, deleted));
        }

        return rows;
    }

    private static Disk? Read (string path)
    {
        try
        {
            return new (path, DocumentationBuilder.Parse (File.ReadAllText (path), path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Housekeeping (string path)
    {
        try
        {
            return (File.GetAttributes (path) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool Differs (Row row, Disk disk) =>
        disk.Values.Any (v => !row.Values.TryGetValue (v.Key, out string? stored) || stored != v.Value);

    private static bool SameContent (Row row, Disk disk)
    {
        string stored = Flat (row.Values.GetValueOrDefault ("content", ""));

        return stored.Trim ().Length > 0 && stored == Flat (disk.Values.GetValueOrDefault ("content", ""));
    }

    private static bool SameName (Row row, Disk disk) =>
        string.Equals (Path.GetFileName (row.Path), disk.Name, StringComparison.OrdinalIgnoreCase);

    private static string Flat (string text) => text.Replace ("\r\n", "\n");

    private static void Update (SqliteConnection connection, SqliteTransaction transaction, string oldPath, Disk disk, string? newPath)
    {
        OrderedDictionary<string, string> values = Stamped (disk, created: false);

        using SqliteCommand write = connection.CreateCommand ();
        write.Transaction = transaction;

        write.CommandText = $"UPDATE {Constants.NotesTable} SET deleted = 0, filepath = $newpath, "
                            + string.Join (", ", values.Keys.Select (name => $"{name} = ${name}"))
                            + " WHERE filepath = $oldpath;";

        write.Parameters.AddWithValue ("$oldpath", oldPath);
        write.Parameters.AddWithValue ("$newpath", newPath ?? oldPath);
        Bind (write, values);

        write.ExecuteNonQuery ();
    }

    private static void Insert (SqliteConnection connection, SqliteTransaction transaction, Disk disk)
    {
        OrderedDictionary<string, string> values = Stamped (disk, created: true);

        using SqliteCommand write = connection.CreateCommand ();
        write.Transaction = transaction;

        write.CommandText = $"INSERT INTO {Constants.NotesTable} (filepath, deleted, {string.Join (", ", values.Keys)}) "
                            + $"VALUES ($filepath, 0, {string.Join (", ", values.Keys.Select (name => "$" + name))}) "
                            + "ON CONFLICT (filepath) DO UPDATE SET deleted = 0, "
                            + string.Join (", ", values.Keys.Select (name => $"{name} = excluded.{name}"))
                            + ";";

        write.Parameters.AddWithValue ("$filepath", disk.Path);
        Bind (write, values);

        write.ExecuteNonQuery ();
    }

    private static void MarkDeleted (SqliteConnection connection, SqliteTransaction? transaction, string path)
    {
        using SqliteCommand write = connection.CreateCommand ();
        write.Transaction = transaction;

        write.CommandText = $"UPDATE {Constants.NotesTable} SET deleted = 1 WHERE filepath = $path;";
        write.Parameters.AddWithValue ("$path", path);

        write.ExecuteNonQuery ();
    }

    private static OrderedDictionary<string, string> Stamped (Disk disk, bool created)
    {
        OrderedDictionary<string, string> values = new (disk.Values, StringComparer.OrdinalIgnoreCase);

        values.TryAdd ("modified", Stamp (File.GetLastWriteTimeUtc (disk.Path)));

        if (created)
        {
            values.TryAdd ("created", Stamp (File.GetCreationTimeUtc (disk.Path)));
        }

        return values;
    }

    private static string Stamp (DateTime utc) => utc.ToString (TimeFormat, CultureInfo.InvariantCulture);

    private static void Bind (SqliteCommand command, OrderedDictionary<string, string> values)
    {
        foreach ((string name, string value) in values)
        {
            command.Parameters.AddWithValue ("$" + name, value);
        }
    }
}

