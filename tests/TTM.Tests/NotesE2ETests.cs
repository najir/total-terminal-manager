// -----------------------------------------------------------------------------
//  End-to-end test of the note pipeline: a note is created and saved, then comes
//  back through the "open from document viewer" path.
//
//  This is a pipeline-level E2E (no Terminal.Gui window): it drives the SAME code
//  the UI drives -
//      EditorPage.Save()          ->  File.WriteAllText + DocumentationBuilder.Convert
//      startup NotesSync.Run()    ->  file/folder <-> notes table reconciliation
//      DocumentsPage.Folders()    ->  "SELECT ... FROM notes WHERE deleted = 0"
//      Notes.OpenInEditor(path)   ->  File.ReadAllText
//  so the whole note -> SQLite -> list -> reopen round trip is exercised without
//  the modal Save dialog, which is the only part that needs a real terminal.
//
//  It runs against a throwaway data root (TTM_DATA), never the live vault.
// -----------------------------------------------------------------------------

using Microsoft.Data.Sqlite;
using Mode;
using Sql;
using Xunit;

namespace TTM.Tests;

public sealed class NotesE2ETests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _notesPath;

    public NotesE2ETests ()
    {
        // Isolate the whole app data root into a throwaway directory.
        _dataRoot = Path.Combine (Path.GetTempPath (), "ttm-tests", Guid.NewGuid ().ToString ("N"));

        Environment.SetEnvironmentVariable (UserSettings.DataRootVariable, _dataRoot);

        // The same bootstrap Program.cs runs, just pointed at the temp root.
        UserSettings.Load ();
        Engine.Init ();

        _notesPath = UserSettings.NotesPath;
    }

    public void Dispose ()
    {
        // Engine.Connect() pools connections, so the database file stays open until the pools
        // are released; clear them first or the temp folder cannot be removed.
        SqliteConnection.ClearAllPools ();

        try
        {
            Directory.Delete (_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - the throwaway folder may need a moment after the DB is released.
        }
    }

    /// <summary>
    ///     A note is created and saved, appears on the document viewer, and can be opened
    ///     back up with its content intact.
    /// </summary>
    [Fact]
    public void Create_Save_And_Reopen_From_Document_Viewer ()
    {
        const string body = "[tags: e2e]\n# E2E note\n\nWritten by the end-to-end test.";

        string fileName = $"e2e-{Guid.NewGuid ():N}.md";
        string path = Path.Combine (_notesPath, fileName);

        // 1. Create + save: exactly what EditorPage.Save() does - write the file, then
        //    record the note in the database (parsing its [key: value] front-matter).
        File.WriteAllText (path, body);
        DocumentationBuilder.Convert (body, path);

        // 2. Simulate the startup reconcile; the just-saved row must survive unchanged.
        NotesSync.Run ();

        // 3. "Open from document viewer": DocumentsPage lists non-deleted rows, newest
        //    first. It must include our note, titled by its file name.
        List<(string FilePath, string Title, string Modified)> listed = ListNotes ();
        Assert.Single (listed, n => string.Equals (n.FilePath, path, StringComparison.OrdinalIgnoreCase));

        (string filePath, string title, _) = listed.First (n => string.Equals (n.FilePath, path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal (fileName, title);

        // 4. The database row carries the parsed note: the body minus its front-matter,
        //    and the front-matter tag - not deleted.
        (string content, string tags, bool deleted) = ReadRow (path);
        Assert.False (deleted);
        Assert.Equal ("e2e", tags);
        Assert.Equal ("\n# E2E note\n\nWritten by the end-to-end test.", content);

        // 5. Open it back up: what Notes.OpenInEditor ends up reading for the editor.
        string reopened = File.ReadAllText (path);
        Assert.Equal (body, reopened);
    }

    /// <summary>The same SELECT the document viewer runs to build its list.</summary>
    private static List<(string FilePath, string Title, string Modified)> ListNotes ()
    {
        List<(string, string, string)> rows = new ();

        using SqliteConnection connection = Engine.Connect ();
        using SqliteCommand read = connection.CreateCommand ();
        read.CommandText = $"SELECT filepath, title, modified FROM {Constants.NotesTable} "
                           + "WHERE deleted = 0 ORDER BY modified DESC;";

        using SqliteDataReader reader = read.ExecuteReader ();

        while (reader.Read ())
        {
            rows.Add ((reader.GetString (0), reader.GetString (1), reader.GetString (2)));
        }

        return rows;
    }

    private static (string Content, string Tags, bool Deleted) ReadRow (string filePath)
    {
        using SqliteConnection connection = Engine.Connect ();
        using SqliteCommand read = connection.CreateCommand ();
        read.CommandText = $"SELECT content, tags, deleted FROM {Constants.NotesTable} WHERE filepath = $filepath;";
        read.Parameters.AddWithValue ("$filepath", filePath);

        using SqliteDataReader reader = read.ExecuteReader ();

        if (!reader.Read ())
        {
            return ("", "", true);
        }

        return (reader.GetString (0), reader.GetString (1), reader.GetInt64 (2) != 0);
    }
}