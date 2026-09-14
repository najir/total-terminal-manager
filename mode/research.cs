// -----------------------------------------------------------------------------
//  Research: the research table, the links widget and the Notes page that feeds it.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Terminal.Gui.Input;

namespace Mode;

/// <summary>One research link, as stored in the research table.</summary>
internal sealed record ResearchLink (long Id, string Name, string Url, DateTime Created);

/// <summary>
///     The research table: the name, url and creation date of every link stored from the Research
///     page. The database owns the creation stamp, so a row is dated by the write that made it.
/// </summary>
internal static class ResearchStore
{
    public static string? Error { get; private set; }

    /// <summary>The newest links first, at most <paramref name="amount"/> of them.</summary>
    public static List<ResearchLink> Load (int amount)
    {
        Error = null;

        List<ResearchLink> links = new ();

        if (!Engine.Ready || amount <= 0)
        {
            return links;
        }

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand read = connection.CreateCommand ();

            read.CommandText = $"SELECT id, name, url, created FROM {Constants.ResearchTable} "
                               + "ORDER BY created DESC, id DESC LIMIT $amount;";

            read.Parameters.AddWithValue ("$amount", amount);

            using SqliteDataReader reader = read.ExecuteReader ();

            while (reader.Read ())
            {
                links.Add (
                           new (
                                reader.GetInt64 (0),
                                reader.GetString (1),
                                reader.GetString (2),
                                Stamp (reader.GetString (3))));
            }

            return links;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Error = ex.Message;

            return new ();
        }
    }

    public static bool Add (string name, string url)
    {
        Error = null;

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand write = connection.CreateCommand ();

            write.CommandText = $"INSERT INTO {Constants.ResearchTable} (name, url) VALUES ($name, $url);";

            write.Parameters.AddWithValue ("$name", name);
            write.Parameters.AddWithValue ("$url", url);

            write.ExecuteNonQuery ();

            return true;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Error = ex.Message;

            return false;
        }
    }

    public static bool Delete (long id)
    {
        Error = null;

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand write = connection.CreateCommand ();

            write.CommandText = $"DELETE FROM {Constants.ResearchTable} WHERE id = $id;";

            write.Parameters.AddWithValue ("$id", id);

            write.ExecuteNonQuery ();

            return true;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Error = ex.Message;

            return false;
        }
    }

    /// <summary>
    ///     A url the shell can open. Writing one down as "codeberg.org" is normal, and without a
    ///     scheme the default browser is never reached, so one is assumed.
    /// </summary>
    public static string Canonical (string url) =>
        url.Contains ("://", StringComparison.Ordinal) ? url : "https://" + url;

    private static DateTime Stamp (string stored) =>
        DateTime.TryParse (stored, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime when)
            ? when
            : default;
}

/// <summary>
///     The stored research links as a table: name, url, the day it was stored - and, when trashable,
///     a delete cell on the right of every row. Amount caps how many rows are shown; double-clicking
///     a name or a url opens the link in the default browser, and Delete, like the delete cell,
///     removes the row from the research table.
/// </summary>
internal sealed class ResearchWidget : View
{
    public const int DefaultAmount = 100;

    private const string Nothing = "no research saved yet";

    private const string TrashCell = "[del]";

    private const int NameWidth = 40;
    private const int UrlWidth = 80;

    private const int NameColumn = 0;
    private const int UrlColumn = 1;
    private const int TrashColumn = 3;

    private const string TrashHeader = " ";

    private readonly TableView _table;
    private readonly bool _trashable;

    private List<ResearchLink> _links = new ();

    public ResearchWidget (
        int amount = DefaultAmount,
        bool trashable = true,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Amount = Math.Max (0, amount);

        _trashable = trashable;

        Title = "Research";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _table = new ()
        {
            Width = 1,
            Height = 1,
            FullRowSelect = true,
            MultiSelect = false
        };

        _table.Style.ShowHorizontalHeaderUnderline = true;
        _table.Style.ShowVerticalCellLines = true;
        _table.Style.ExpandLastColumn = false;

        _table.MouseEvent += OnTableMouse;
        _table.KeyDown += OnTableKey;

        Add (_table);

        this.OnShown (Reload);
    }

    public int Amount { get; }

    public IReadOnlyList<ResearchLink> Links => _links;

    public void Reload ()
    {
        _links = ResearchStore.Load (Amount);
        Render ();
    }

    private void OnTableMouse (object? sender, Mouse mouse)
    {
        if (mouse.Position is not { } position)
        {
            return;
        }

        if (_table.ScreenToCell (position.X, position.Y) is not { } cell)
        {
            return;
        }

        if (cell.Y < 0 || cell.Y >= _links.Count)
        {
            return;
        }

        if (mouse.IsDoubleClicked && cell.X is NameColumn or UrlColumn)
        {
            Open (_links [cell.Y]);

            mouse.Handled = true;
        }
        else if (mouse.IsSingleClicked && _trashable && cell.X == TrashColumn)
        {
            Delete (cell.Y);

            mouse.Handled = true;
        }
    }

    private void OnTableKey (object? sender, Key key)
    {
        if (!_trashable || key != Key.Delete)
        {
            return;
        }

        int row = _table.Value?.SelectedCell.Y ?? -1;

        if (row >= 0 && row < _links.Count)
        {
            Delete (row);
        }

        key.Handled = true;
    }

    private void Open (ResearchLink link)
    {
        if (link.Url.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start (new ProcessStartInfo (link.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery (App!, "Cannot open", $"{link.Url}\n\n{ex.Message}", new [] { "_Ok" });
        }
    }

    private void Delete (int row)
    {
        ResearchLink link = _links [row];

        int? choice = MessageBox.Query (
                                        App!,
                                        "Delete research",
                                        $"Delete '{link.Name}'?"
                                        + Environment.NewLine
                                        + link.Url
                                        + Environment.NewLine
                                        + Environment.NewLine
                                        + "The row is removed from the research table. This cannot be undone.",
                                        new [] { "_Delete", "_Cancel" });

        if (choice != 0)
        {
            return;
        }

        if (!ResearchStore.Delete (link.Id))
        {
            MessageBox.ErrorQuery (
                                   App!,
                                   "Cannot delete",
                                   link.Url + Environment.NewLine + (ResearchStore.Error ?? ""),
                                   new [] { "_Ok" });

            return;
        }

        Reload ();
    }

    private void Render ()
    {
        Title = $"Research ({_links.Count})";

        bool empty = _links.Count == 0;

        List<ResearchLink> rows = empty ? new () { new ResearchLink (0, Nothing, "", default) } : _links;

        Dictionary<string, Func<ResearchLink, object>> columns = new ()
        {
            ["Name"] = l => Shorten (l.Name, NameWidth),
            ["Url"] = l => Shorten (l.Url, UrlWidth),
            ["Created"] = l => Created (l.Created)
        };

        if (_trashable)
        {
            columns [TrashHeader] = _ => empty ? "" : TrashCell;
        }

        _table.SetSource (new EnumerableTableSource<ResearchLink> (rows, columns));

        _table.Style.GetOrCreateColumnStyle (NameColumn).MaxWidth = NameWidth;
        _table.Style.GetOrCreateColumnStyle (UrlColumn).MaxWidth = UrlWidth;

        int wide = columns.Count + 1;
        int index = 0;

        foreach ((string header, Func<ResearchLink, object> cell) in columns)
        {
            int longest = rows.Max (r => cell (r).ToString ()!.Length);

            wide += Math.Min (Math.Max (header.Length, longest), _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        _table.Width = wide;

        _table.Height = rows.Count + 3;
        _table.Refresh ();

        this.Remeasure ();
        this.ResizeLayout ();
    }

    private static string Shorten (string text, int width) =>
        text.Length <= width ? text : text [.. (width - 1)] + "\u2026";

    private static string Created (DateTime created) => created == default ? "" : created.ToString ("dd-MM");
}

/// <summary>
///     The Research page: a name and a url to store, and below them the links already stored -
///     trashable here, where there is room to manage the list.
/// </summary>
internal sealed class ResearchPage : Window
{
    private const int LabelGap = 1;

    private const int GroupGap = 2;

    public ResearchPage (Pos x, Pos y)
    {
        Title = "Research";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        View entry = new ()
        {
            Title = "New research",
            BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded,
            X = 0,
            Y = 0,
            Width = Dim.Auto (),
            Height = Dim.Auto (),
            CanFocus = true
        };

        Label nameLabel = new () { Text = "Name", X = 0, Y = 0 };
        TextField name = new () { X = Pos.Right (nameLabel) + LabelGap, Y = 0, Width = 30 };

        Label urlLabel = new () { Text = "Url", X = Pos.Right (name) + GroupGap, Y = 0 };
        TextField url = new () { X = Pos.Right (urlLabel) + LabelGap, Y = 0, Width = 44 };

        Button add = new () { Text = "_Add link", X = Pos.Right (url) + GroupGap, Y = 0 };

        Label problem = new () { Text = "", X = 0, Y = Pos.Bottom (nameLabel), Width = Dim.Auto () };

        entry.Add (nameLabel, name, urlLabel, url, add, problem);

        ResearchWidget research = new () { X = 0, Y = Pos.Bottom (entry) };

        void AddLink ()
        {
            string linkName = name.Text.Trim ();
            string linkUrl = url.Text.Trim ();

            if (linkName.Length == 0)
            {
                problem.Text = "name is required";

                return;
            }

            if (linkUrl.Length == 0)
            {
                problem.Text = "url is required";

                return;
            }

            if (!ResearchStore.Add (linkName, ResearchStore.Canonical (linkUrl)))
            {
                problem.Text = ResearchStore.Error ?? "could not write to the research table";

                return;
            }

            name.Text = "";
            url.Text = "";
            problem.Text = "";

            research.Reload ();
        }

        add.Accepted += (_, _) => AddLink ();
        name.Accepted += (_, _) => AddLink ();
        url.Accepted += (_, _) => AddLink ();

        Add (entry, research);
    }
}
