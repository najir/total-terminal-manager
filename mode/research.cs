// -----------------------------------------------------------------------------
//  Research: the research table, the links widget and the Notes page that feeds it.
// -----------------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Terminal.Gui.Input;

namespace Mode;

/// <summary>One research link, as stored in the research table.</summary>
internal sealed record ResearchLink (long Id, string Name, string Url, string Category, DateTime Created);

/// <summary>
///     Which stored links an export should take: a category, text that has to appear in the name or
///     the url, a range of creation dates and a cap on how many links are taken. Every part is
///     optional - a blank category, blank text, no dates and a cap of zero take everything.
/// </summary>
internal sealed record ResearchFilter (
    string Category = "",
    string Text = "",
    DateTime? From = null,
    DateTime? To = null,
    int Amount = 0);

/// <summary>
///     The research table: the name, url, category and creation date of every link stored from the
///     Research page. The database owns the creation stamp, so a row is dated by the write that made it.
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

            read.CommandText = $"SELECT id, name, url, category, created FROM {Constants.ResearchTable} "
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
                                reader.GetString (3),
                                Stamp (reader.GetString (4))));
            }

            return links;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Error = ex.Message;

            return new ();
        }
    }

    /// <summary>A link with no category of its own belongs to <see cref="NoCategory"/>.</summary>
    public const string NoCategory = "None";

    public static bool Add (string name, string url, string category = NoCategory)
    {
        Error = null;

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand write = connection.CreateCommand ();

            write.CommandText = $"INSERT INTO {Constants.ResearchTable} (name, url, category) "
                                + "VALUES ($name, $url, $category);";

            write.Parameters.AddWithValue ("$name", name);
            write.Parameters.AddWithValue ("$url", url);
            write.Parameters.AddWithValue ("$category", category.Length == 0 ? NoCategory : category);

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

    /// <summary>The categories actually in use, so a filter only ever offers real choices.</summary>
    public static List<string> Categories ()
    {
        Error = null;

        List<string> categories = new ();

        if (!Engine.Ready)
        {
            return categories;
        }

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand read = connection.CreateCommand ();

            read.CommandText = $"SELECT DISTINCT category FROM {Constants.ResearchTable} "
                               + "WHERE category <> '' ORDER BY category COLLATE NOCASE;";

            using SqliteDataReader reader = read.ExecuteReader ();

            while (reader.Read ())
            {
                categories.Add (reader.GetString (0));
            }

            return categories;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Error = ex.Message;

            return new ();
        }
    }

    /// <summary>
    ///     The links the filter matches, newest first. Only the parts of the filter that are set
    ///     narrow the query, so the default filter returns the whole table.
    /// </summary>
    public static List<ResearchLink> Load (ResearchFilter filter)
    {
        Error = null;

        List<ResearchLink> links = new ();

        if (!Engine.Ready)
        {
            return links;
        }

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand read = connection.CreateCommand ();

            List<string> where = new ();

            if (filter.Category.Length > 0)
            {
                where.Add ("category = $category");
                read.Parameters.AddWithValue ("$category", filter.Category);
            }

            if (filter.Text.Length > 0)
            {
                where.Add (@"(name LIKE $text ESCAPE '\' COLLATE NOCASE "
                           + @"OR url LIKE $text ESCAPE '\' COLLATE NOCASE)");

                read.Parameters.AddWithValue ("$text", "%" + Escape (filter.Text) + "%");
            }

            if (filter.From is { } from)
            {
                where.Add ("created >= $from");
                read.Parameters.AddWithValue ("$from", from.Date.ToString ("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (filter.To is { } to)
            {
                // The stored stamp carries a time, so the range ends at the start of the next day
                // and the whole of the chosen day is inside it.
                where.Add ("created < $to");

                read.Parameters.AddWithValue (
                                              "$to",
                                              to.Date.AddDays (1).ToString ("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            read.CommandText = $"SELECT id, name, url, category, created FROM {Constants.ResearchTable} "
                               + (where.Count == 0 ? "" : "WHERE " + string.Join (" AND ", where) + " ")
                               + "ORDER BY created DESC, id DESC"
                               + (filter.Amount > 0 ? " LIMIT $amount" : "")
                               + ";";

            if (filter.Amount > 0)
            {
                read.Parameters.AddWithValue ("$amount", filter.Amount);
            }

            using SqliteDataReader reader = read.ExecuteReader ();

            while (reader.Read ())
            {
                links.Add (
                           new (
                                reader.GetInt64 (0),
                                reader.GetString (1),
                                reader.GetString (2),
                                reader.GetString (3),
                                Stamp (reader.GetString (4))));
            }

            return links;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Error = ex.Message;

            return new ();
        }
    }

    /// <summary>
    ///     Writes the urls of <paramref name="links"/> into a new text file in
    ///     <paramref name="folder"/>, one url to a line and nothing else, so the file can be handed
    ///     on as it is. The name carries the moment of the export, so nothing is ever overwritten.
    /// </summary>
    public static bool Export (IReadOnlyList<ResearchLink> links, string folder, out string file)
    {
        Error = null;

        file = Path.Combine (folder, $"research-links-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        try
        {
            File.WriteAllLines (file, links.Select (link => link.Url));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            Error = ex.Message;

            return false;
        }
    }

    /// <summary>
    ///     Neutralises the wildcards LIKE reads, so searching for "a_b" finds "a_b" and not "axb".
    /// </summary>
    private static string Escape (string text) =>
        text.Replace (@"\", @"\\").Replace ("%", @"\%").Replace ("_", @"\_");

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
///     The stored research links as a table: name, url, the category when categorized, the day it was
///     stored - and, when trashable, a delete cell on the right of every row. Amount caps how many
///     rows are shown; double-clicking
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

    private const string TrashHeader = " ";

    private readonly TableView _table;
    private readonly bool _trashable;
    private readonly bool _categorized;

    private List<ResearchLink> _links = new ();

    public ResearchWidget (
        int amount = DefaultAmount,
        bool trashable = true,
        bool categorized = true,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Amount = Math.Max (0, amount);

        _trashable = trashable;
        _categorized = categorized;

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

    /// <summary>Whether the category column is shown.</summary>
    public bool Categorized => _categorized;

    /// <summary>The delete cell's column, which sits after the optional category column.</summary>
    private int TrashColumn => _categorized ? 4 : 3;

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

        List<ResearchLink> rows =
            empty ? new () { new ResearchLink (0, Nothing, "", ResearchStore.NoCategory, default) } : _links;

        Dictionary<string, Func<ResearchLink, object>> columns = new ()
        {
            ["Name"] = l => Shorten (l.Name, NameWidth),
            ["Url"] = l => Shorten (l.Url, UrlWidth)
        };

        if (_categorized)
        {
            columns ["Category"] = l => empty ? "" : l.Category;
        }

        columns ["Created"] = l => Created (l.Created);

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

    private const string AnyCategory = "(any)";

    private const string DateFormat = "yyyy-MM-dd";

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

        Label categoryLabel = new () { Text = "Category", X = Pos.Right (url) + GroupGap, Y = 0 };
        TextField category = new () { X = Pos.Right (categoryLabel) + LabelGap, Y = 0, Width = 20 };

        Button add = new () { Text = "_Add link", X = Pos.Right (category) + GroupGap, Y = 0 };

        Button export = new () { Text = "_Export", X = Pos.Right (add) + GroupGap, Y = 0 };

        Label problem = new () { Text = "", X = 0, Y = Pos.Bottom (nameLabel), Width = Dim.Auto () };

        entry.Add (nameLabel, name, urlLabel, url, categoryLabel, category, add, export, problem);

        ResearchWidget research = new () { X = 0, Y = Pos.Bottom (entry) };

        void AddLink ()
        {
            string linkName = name.Text.Trim ();
            string linkUrl = url.Text.Trim ();
            string linkCategory = category.Text.Trim ();

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

            if (!ResearchStore.Add (linkName, ResearchStore.Canonical (linkUrl), linkCategory))
            {
                problem.Text = ResearchStore.Error ?? "could not write to the research table";

                return;
            }

            name.Text = "";
            url.Text = "";
            category.Text = "";
            problem.Text = "";

            research.Reload ();
        }

        export.Accepted += (_, _) => Export ();

        add.Accepted += (_, _) => AddLink ();
        name.Accepted += (_, _) => AddLink ();
        url.Accepted += (_, _) => AddLink ();
        category.Accepted += (_, _) => AddLink ();

        Add (entry, research);
    }

    /// <summary>
    ///     Narrow the stored links down, choose a folder, and write the urls that are left into a
    ///     text file there - one url to a line, nothing else, ready to hand on.
    /// </summary>
    private void Export ()
    {
        Dialog dialog = new () { Title = "Export research", Width = 62, Height = 14 };

        Label categoryLabel = new () { Text = "Category", X = 1, Y = 0 };

        DropDownList category = new ()
        {
            X = Pos.Right (categoryLabel) + LabelGap,
            Y = 0,
            Width = 24,
            ReadOnly = true,
            Text = AnyCategory
        };

        ObservableCollection<string> categories = new () { AnyCategory };

        foreach (string stored in ResearchStore.Categories ())
        {
            categories.Add (stored);
        }

        category.Source = new ListWrapper<string> (categories);

        Label textLabel = new () { Text = "Search", X = 1, Y = Pos.Bottom (categoryLabel) };
        TextField text = new () { X = Pos.Left (category), Y = Pos.Top (textLabel), Width = 36 };

        Label fromLabel = new () { Text = "From", X = 1, Y = Pos.Bottom (textLabel) };
        TextField from = new () { X = Pos.Left (category), Y = Pos.Top (fromLabel), Width = 12 };

        Label toLabel = new () { Text = "To", X = Pos.Right (from) + GroupGap, Y = Pos.Top (fromLabel) };
        TextField to = new () { X = Pos.Right (toLabel) + LabelGap, Y = Pos.Top (fromLabel), Width = 12 };

        Label amountLabel = new () { Text = "Max", X = 1, Y = Pos.Bottom (fromLabel) };
        TextField amount = new () { X = Pos.Left (category), Y = Pos.Top (amountLabel), Width = 12 };

        Label help = new ()
        {
            Text = $"dates are {DateFormat}; blank takes everything",
            X = 1,
            Y = Pos.Bottom (amountLabel) + 1
        };

        Label matches = new () { Text = "", X = 1, Y = Pos.Bottom (help), Width = Dim.Fill (1) };

        // The count under the fields and the export itself read the same filter, so what is counted
        // is exactly what gets written.
        List<ResearchLink>? Chosen ()
        {
            if (!Filter (
                         category.Text,
                         text.Text,
                         from.Text,
                         to.Text,
                         amount.Text,
                         out ResearchFilter filter,
                         out string problem))
            {
                matches.Text = problem;

                return null;
            }

            List<ResearchLink> found = ResearchStore.Load (filter);

            if (ResearchStore.Error is { } failed)
            {
                matches.Text = failed;

                return null;
            }

            matches.Text = found.Count == 1 ? "1 link matches" : $"{found.Count} links match";

            return found;
        }

        void Count () => Chosen ();

        category.ValueChanged += (_, _) => Count ();
        text.TextChanged += (_, _) => Count ();
        from.TextChanged += (_, _) => Count ();
        to.TextChanged += (_, _) => Count ();
        amount.TextChanged += (_, _) => Count ();

        Count ();

        // Cancel is added first, so Enter is Export.
        Button cancel = new () { Text = "_Cancel" };
        Button write = new () { Text = "_Export" };

        cancel.Accepted += (_, _) => App!.RequestStop (dialog);

        // Pressing any button of a Dialog ends it, which would tear this one down before the folder
        // picker could open - so Export handles its own accept and closes the dialog itself.
        write.Accepting += (_, e) =>
        {
            e.Handled = true;

            // A filter nothing matches, a bad date or an abandoned folder pick all leave the dialog
            // open, so the filter can be corrected instead of started again.
            if (Chosen () is not { Count: > 0 } links || Folder () is not { } folder)
            {
                return;
            }

            if (!ResearchStore.Export (links, folder, out string file))
            {
                MessageBox.ErrorQuery (
                                       App!,
                                       "Cannot export",
                                       folder + Environment.NewLine + (ResearchStore.Error ?? ""),
                                       new [] { "_Ok" });

                return;
            }

            MessageBox.Query (
                              App!,
                              "Exported",
                              $"{links.Count} link{(links.Count == 1 ? "" : "s")} written to"
                              + Environment.NewLine
                              + file,
                              new [] { "_Ok" });

            App!.RequestStop (dialog);
        };

        dialog.Add (
                    categoryLabel,
                    category,
                    textLabel,
                    text,
                    fromLabel,
                    from,
                    toLabel,
                    to,
                    amountLabel,
                    amount,
                    help,
                    matches);

        dialog.AddButton (cancel);
        dialog.AddButton (write);

        App!.Run (dialog);
        dialog.Dispose ();
    }

    /// <summary>The folder to write into, or null when the pick was abandoned.</summary>
    private string? Folder ()
    {
        OpenDialog picker = new ()
        {
            Title = "Export to folder",
            OpenMode = OpenMode.Directory,
            MustExist = true,
            AllowsMultipleSelection = false,
            Path = UserSettings.NotesPath + Path.DirectorySeparatorChar
        };

        App!.Run (picker);

        string? chosen = picker.Canceled || picker.Path is not { Length: > 0 } ? null : picker.Path;

        picker.Dispose ();

        if (chosen is null)
        {
            return null;
        }

        return Directory.Exists (chosen) ? chosen : Path.GetDirectoryName (chosen);
    }

    /// <summary>
    ///     Reads the fields of the dialog into a filter. A date that is not a date, or a cap that is
    ///     not a number, is reported rather than quietly ignored.
    /// </summary>
    private static bool Filter (
        string category,
        string text,
        string from,
        string to,
        string amount,
        out ResearchFilter filter,
        out string problem)
    {
        filter = new ();
        problem = "";

        if (!Day (from, out DateTime? start))
        {
            problem = $"from is not a date ({DateFormat})";

            return false;
        }

        if (!Day (to, out DateTime? end))
        {
            problem = $"to is not a date ({DateFormat})";

            return false;
        }

        if (start is { } began && end is { } ended && ended < began)
        {
            problem = "to is before from";

            return false;
        }

        string capped = amount.Trim ();
        int most = 0;

        if (capped.Length > 0 && (!int.TryParse (capped, out most) || most < 0))
        {
            problem = "max is not a whole number";

            return false;
        }

        string picked = category.Trim ();

        filter = new (picked == AnyCategory ? "" : picked, text.Trim (), start, end, most);

        return true;
    }

    /// <summary>A blank date is no bound at all; anything else has to be a real date.</summary>
    private static bool Day (string typed, out DateTime? day)
    {
        day = null;

        string written = typed.Trim ();

        if (written.Length == 0)
        {
            return true;
        }

        if (!DateTime.TryParseExact (
                                     written,
                                     DateFormat,
                                     CultureInfo.InvariantCulture,
                                     DateTimeStyles.None,
                                     out DateTime read))
        {
            return false;
        }

        day = read;

        return true;
    }
}
