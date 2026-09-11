// -----------------------------------------------------------------------------
//  The Notes tab: editor page and stored-notes list.
// -----------------------------------------------------------------------------

using Microsoft.Data.Sqlite;
using Terminal.Gui.Input;

namespace Mode;

internal static class Notes
{
    public static Action<string>? OpenInEditor { get; private set; }

    public static void Connect (List<View> all, IEnumerable<View> pages)
    {
        if (pages.OfType<EditorPage> ().FirstOrDefault () is not { } editor)
        {
            return;
        }

        OpenInEditor = path =>
                       {
                           Handler.ResetVisibility (all);
                           editor.Visible = true;
                           editor.Open (path);
                           editor.SetFocus ();
                       };
    }

    public static List<View> Pages (Pos x, Pos y) =>
        new () { new EditorPage (x, y), new DocumentsPage (x, y) };

    public static IEnumerable<MenuItem> MenuItems (List<View> all, IEnumerable<View> pages) =>
        pages.Select (
                      page => new MenuItem
                      {
                          Title = page.Title,
                          Action = () =>
                                   {
                                       Handler.ResetVisibility (all);
                                       page.Visible = true;
                                       page.SetFocus ();
                                   }
                      });
}

/// <summary>Where a note gets written: a text editor with Open and Save above it.</summary>
internal sealed class EditorPage : Window
{
    private const string Unsaved = "(unsaved)";

    private const string DefaultType = ".md";

    private static readonly Terminal.Gui.Drawing.Thickness EditorMargin = new (4, 0, 4, 0);

    private static Terminal.Gui.Drawing.Attribute Ink (Terminal.Gui.Drawing.Color background) =>
        new (
             background.IsDarkColor ()
                 ? Terminal.Gui.Drawing.Color.White
                 : Terminal.Gui.Drawing.Color.Black,
             background);

    /// <summary>
    ///     The background the editor draws on: the colour saved from the Background button, or
    ///     the theme's Dialog background - which is what the report and review viewers draw on,
    ///     so the two read alike out of the box.
    /// </summary>
    private static Terminal.Gui.Drawing.Color ChosenBackground ()
    {
        Terminal.Gui.Drawing.Color theme = SchemeManager.GetScheme (Terminal.Gui.Drawing.Schemes.Dialog).Normal.Background;

        return Terminal.Gui.Drawing.Color.TryParse (UserSettings.Get (UserSettings.EditorBackground), out Terminal.Gui.Drawing.Color? chosen)
               && chosen is { } colour
                   ? colour
                   : theme;
    }

    private static string CaretColour =>
        ChosenBackground ().IsDarkColor ()
            ? "rgb:ff/ff/ff"
            : "rgb:00/00/00";

    private readonly Editor _editor;
    private readonly Label _pathLabel;

    // Right end of the button row: when the open file was last written. Wide enough for the
    // longest text it shows, and the path label stops short of it so the two never overlap.
    private const int SavedWidth = 26;

    private readonly Label _savedLabel;

    private string? _path;

    public EditorPage (Pos x, Pos y)
    {
        Title = "Editor";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        Button create = new () { Text = "_New", X = 0, Y = 0 };
        Button open = new () { Text = "_Open", X = Pos.Right (create) + 1, Y = 0 };
        Button save = new () { Text = "_Save", X = Pos.Right (open) + 1, Y = 0 };
        Button saveAs = new () { Text = "Save _As", X = Pos.Right (save) + 1, Y = 0 };
        Button colour = new () { Text = "_Background", X = Pos.Right (saveAs) + 1, Y = 0 };

        _pathLabel = new ()
        {
            Text = Unsaved,
            X = Pos.Right (colour) + 2,
            Y = 0,
            Width = Dim.Fill (SavedWidth + 1),
            Height = 1
        };

        _savedLabel = new ()
        {
            X = Pos.AnchorEnd (SavedWidth),
            Y = 0,
            Width = SavedWidth,
            Height = 1,
            TextAlignment = Alignment.End
        };

        _editor = new ()
        {
            X = 0,
            Y = Pos.Bottom (open),
            Width = Dim.Fill (),
            Height = Dim.Fill (),
            GutterOptions = GutterOptions.LineNumbers
        };

        _editor.Margin!.Thickness = EditorMargin;

        _editor.AddEditingKeys ();
        _editor.WheelLines ();

        ApplyEditorScheme ();
        ThemeManager.ThemeChanged += OnThemeChanged;

        _editor.HasFocusChanged += (_, _) => SetCaretColour (_editor.HasFocus ? CaretColour : null);

        Disposing += (_, _) =>
                     {
                         ThemeManager.ThemeChanged -= OnThemeChanged;

                         SetCaretColour (null);
                     };

        create.Accepted += (_, _) =>
        {
            _editor.Text = "";
            _path = null;
            _pathLabel.Text = Unsaved;
            _savedLabel.Text = "";   // a new file has no save to report yet
        };

        open.Accepted += (_, _) =>
        {
            if (Ask (new OpenDialog { Title = "Open", OpenMode = OpenMode.File, MustExist = true }) is { } chosen)
            {
                Open (chosen);
            }
        };

        save.Accepted += (_, _) => Save ();

        saveAs.Accepted += (_, _) => Save (askPath: true);

        colour.Accepted += (_, _) => PickBackground ();

        AddCommand (Command.Save, () => { Save (); return true; });
        KeyBindings.Add (Key.S.WithCtrl, Command.Save);

        _editor.KeyDown += (_, key) =>
                           {
                               if (key == Key.S.WithCtrl)
                               {
                                   Save ();
                                   key.Handled = true;
                               }
                           };

        Add (create, open, save, saveAs, colour, _pathLabel, _savedLabel, _editor);
    }

    private void Save (bool askPath = false)
    {
        if (askPath || _path is null)
        {
            if (Ask (new SaveDialog { Title = askPath ? "Save As" : "Save" }) is not { } picked)
            {
                return;
            }

            _path = WithDefaultType (picked);
        }

        if (_path is not { } target)
        {
            return;
        }

        try
        {
            File.WriteAllText (target, _editor.Text);
            _pathLabel.Text = target;
            _savedLabel.Text = SavedAt (DateTime.Now);

            DocumentationBuilder.Convert (_editor.Text, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SqliteException or InvalidOperationException)
        {
            MessageBox.ErrorQuery (App!, "Cannot save", target + Environment.NewLine + ex.Message, new [] { "_Ok" });
        }
    }

    private void OnThemeChanged (object? sender, Terminal.Gui.App.EventArgs<string> e) => ApplyEditorScheme ();

    private void ApplyEditorScheme ()
    {
        Terminal.Gui.Drawing.Scheme dialog = SchemeManager.GetScheme (Terminal.Gui.Drawing.Schemes.Dialog);
        Terminal.Gui.Drawing.Color background = ChosenBackground ();

        // The Dialog scheme untouched when nothing is chosen - the exact colours the report and
        // review viewers get. A chosen colour replaces the text role only; selection and the
        // rest keep the theme's own colours so they still stand out against it.
        _editor.SetScheme (
                           background == dialog.Normal.Background
                               ? dialog
                               : new Terminal.Gui.Drawing.Scheme (dialog) { Normal = Ink (background) });

        if (_editor.HasFocus)
        {
            SetCaretColour (CaretColour);
        }
    }

    /// <summary>
    ///     Picks the editor background. OK saves it to the user settings as "#RRGGBB", so the
    ///     next launch gets it back; Reset returns to the theme's Dialog background.
    /// </summary>
    private void PickBackground ()
    {
        Dialog dialog = new () { Title = "Editor background", Width = 60, Height = 12 };

        ColorPicker picker = new ()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill (),
            Height = Dim.Fill (1),
            Value = ChosenBackground ()
        };

        picker.Style.ColorModel = Terminal.Gui.Drawing.ColorModel.RGB;
        picker.Style.ShowTextFields = true;
        picker.Style.ShowColorName = true;
        picker.ApplyStyleChanges ();

        // Reset, Cancel, OK: the last button is the default, so Enter keeps the pick.
        Button reset = new () { Text = "_Reset" };
        Button cancel = new () { Text = "_Cancel" };
        Button ok = new () { Text = "_Ok" };

        reset.Accepted += (_, _) =>
        {
            UserSettings.Set (UserSettings.EditorBackground, "");
            ApplyEditorScheme ();
            App!.RequestStop (dialog);
        };

        cancel.Accepted += (_, _) => App!.RequestStop (dialog);

        ok.Accepted += (_, _) =>
        {
            // Value is nullable on the picker; nothing picked means keep what we have.
            if (picker.Value is not { } picked)
            {
                App!.RequestStop (dialog);

                return;
            }

            UserSettings.Set (UserSettings.EditorBackground, $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}");
            ApplyEditorScheme ();
            App!.RequestStop (dialog);
        };

        dialog.Add (picker);
        dialog.AddButton (reset);
        dialog.AddButton (cancel);
        dialog.AddButton (ok);

        App!.Run (dialog);
        dialog.Dispose ();
    }

    private static void SetCaretColour (string? colour)
    {
        Console.Out.Write (colour is null ? "\u001b]112\u0007" : $"\u001b]12;{colour}\u0007");
        Console.Out.Flush ();
    }

    public void Open (string path)
    {
        try
        {
            _editor.Text = File.ReadAllText (path);
            _pathLabel.Text = _path = path;

            // For a file opened from disk the last save is the file's own last write.
            _savedLabel.Text = SavedAt (File.GetLastWriteTime (path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.ErrorQuery (App!, "Cannot open", path + Environment.NewLine + ex.Message, new [] { "_Ok" });
        }
    }

    private static string SavedAt (DateTime when) => $"saved {when:yyyy-MM-dd HH:mm:ss}";

    private static string WithDefaultType (string path) =>
        Path.GetExtension (path).Length == 0 ? Path.ChangeExtension (path, DefaultType) : path;

    private string? Ask (FileDialog dialog)
    {
        dialog.Path = UserSettings.NotesPath + Path.DirectorySeparatorChar;

        if (dialog is SaveDialog)
        {
            AddNewFolderButton (dialog);
        }

        App!.Run (dialog);

        string? chosen = dialog.Canceled || dialog.Path is not { Length: > 0 } ? null : dialog.Path;

        dialog.Dispose ();

        return chosen;
    }

    private static void AddNewFolderButton (FileDialog dialog)
    {
        if (Row (dialog.Padding!.GetOrCreateView ()) is not { } row
            || row.SubViews.OfType<Button> ().LastOrDefault ()?.X is not PosAlign group)
        {
            return;
        }

        Button folder = new ()
        {
            Text = "New _Folder",
            X = Pos.Align (group.Aligner.Alignment, group.Aligner.AlignmentModes, group.GroupId),
            Y = 1
        };

        folder.Accepting += (_, e) =>
                            {
                                e.Handled = true;

                                NewFolder (dialog);
                            };

        row.Add (folder);

        row.MoveSubViewTowardsStart (folder);
        row.MoveSubViewTowardsStart (folder);

        return;

        static View? Row (View view) =>
            view.SubViews.OfType<Button> ().Any ()
                ? view
                : view.SubViews.Select (Row).FirstOrDefault (found => found is not null);
    }

    private static void NewFolder (FileDialog dialog)
    {
        if (CurrentDirectory (dialog.Path) is not { Length: > 0 } here)
        {
            return;
        }

        try
        {
            System.IO.Abstractions.IFileSystem files = new System.IO.Abstractions.FileSystem ();

            if (dialog.FileOperationsHandler.New (dialog.App!, files, files.DirectoryInfo.New (here)) is { } made)
            {
                dialog.Path = made.FullName + Path.DirectorySeparatorChar;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            MessageBox.ErrorQuery (dialog.App!, "Cannot create folder", here + Environment.NewLine + ex.Message, new [] { "_Ok" });
        }
    }

    private static string? CurrentDirectory (string? path) =>
        path is not { Length: > 0 }
            ? null
            : Directory.Exists (path)
                ? path
                : Path.GetDirectoryName (path);
}

internal static class DocumentationBuilder
{
    public static void Convert (string text, string path) => Store (path, Parse (text, path));

    public static OrderedDictionary<string, string> Parse (string text, string path)
    {
        OrderedDictionary<string, string> values = new (StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = Path.GetFileName (path),
            ["content"] = text
        };

        string trimmed = text.TrimStart ();
        int close = trimmed.StartsWith ("[") ? trimmed.IndexOf ("]") : -1;

        if (close >= 0)
        {
            values ["content"] = trimmed [(close + 1)..];

            foreach (string line in trimmed [1..close].Split ((char) 10))
            {
                int colon = line.IndexOf (":");
                string name = colon > 0 ? line [..colon].Trim () : "";

                if (name.Length > 0
                    && Constants.NotesColumns.ContainsKey (name)
                    && !Constants.NotesReservedColumns.Contains (name, StringComparer.OrdinalIgnoreCase))
                {
                    values [name] = line [(colon + 1)..].Trim ();
                }
            }
        }

        return values;
    }

    private static void Store (string filepath, OrderedDictionary<string, string> values)
    {
        using SqliteConnection connection = Engine.Connect ();
        using SqliteCommand write = connection.CreateCommand ();

        write.CommandText = $"INSERT INTO {Constants.NotesTable} (filepath, deleted, {string.Join (", ", values.Keys)}) "
                            + $"VALUES ($filepath, 0, {string.Join (", ", values.Keys.Select (name => "$" + name))}) "
                            + $"ON CONFLICT (filepath) DO UPDATE SET deleted = 0, "
                            + string.Join (", ", values.Keys.Select (name => $"{name} = excluded.{name}"))
                            + ";";

        write.Parameters.AddWithValue ("$filepath", filepath);

        foreach ((string name, string value) in values)
        {
            write.Parameters.AddWithValue ("$" + name, value);
        }

        write.ExecuteNonQuery ();
    }
}

/// <summary>Every stored note, grouped into one collapsible table per folder it is stored in.</summary>
internal sealed class DocumentsPage : Window
{
    private const string Expanded = ">";

    private const string Collapsed = "|";

    private const string DeleteHeader = "Del";

    private const string DeleteMark = "[del]";

    private const int SectionGap = 1;

    private const int Indent = 1;

    private View? _last;

    private readonly HashSet<string> _expanded = new (StringComparer.OrdinalIgnoreCase);

    private readonly List<(string Folder, Button Title, TableView Table)> _sections = new ();

    public DocumentsPage (Pos x, Pos y)
    {
        Title = "Documents";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

        AddCommand (Command.ScrollDown, () => { ScrollVertical (1); return true; });
        AddCommand (Command.ScrollUp, () => { ScrollVertical (-1); return true; });
        AddCommand (Command.PageDown, () => { ScrollVertical (Viewport.Height); return true; });
        AddCommand (Command.PageUp, () => { ScrollVertical (-Viewport.Height); return true; });
        AddCommand (Command.Start, () => { ScrollVertical (-GetContentSize ().Height); return true; });
        AddCommand (Command.End, () => { ScrollVertical (GetContentSize ().Height); return true; });

        KeyBindings.Add (Key.PageDown, Command.PageDown);
        KeyBindings.Add (Key.PageUp, Command.PageUp);
        KeyBindings.Add (Key.CursorDown.WithCtrl, Command.ScrollDown);
        KeyBindings.Add (Key.CursorUp.WithCtrl, Command.ScrollUp);
        KeyBindings.Add (Key.Home.WithCtrl, Command.Start);
        KeyBindings.Add (Key.End.WithCtrl, Command.End);

        ViewportChanged += (_, _) => SizeContent ();
        SubViewsLaidOut += (_, _) => SizeContent ();

        this.OnShown (Reload);
    }

    protected override bool OnMouseEvent (Mouse mouse) => WheelScrolling.Handle (this, mouse) || base.OnMouseEvent (mouse);

    private void SizeContent ()
    {
        int needed = _last is null
                         ? Viewport.Height
                         : Math.Max (Viewport.Height, _last.Frame.Y + _last.Frame.Height);

        System.Drawing.Size wanted = new (Viewport.Width, needed);

        if (GetContentSize () != wanted)
        {
            SetContentSize (wanted);
        }
    }

    private void Reveal (View view)
    {
        if (!view.HasFocus || Viewport.Height <= 0)
        {
            return;
        }

        int top = view.Frame.Y;
        int bottom = top + Math.Min (view.Frame.Height, Viewport.Height);
        int seen = Viewport.Y + Viewport.Height;

        if (top < Viewport.Y)
        {
            ScrollVertical (top - Viewport.Y);
        }
        else if (bottom > seen)
        {
            ScrollVertical (bottom - seen);
        }
    }

    private void Reload ()
    {
        string? keep = _sections.FirstOrDefault (s => ReferenceEquals (s.Title, Focused)
                                                       || ReferenceEquals (s.Table, Focused)).Folder;

        RemoveAll ();
        _last = null;
        _sections.Clear ();

        List<(string Folder, int Depth, List<NoteRow> Notes)> folders = Folders ();

        if (folders.Count == 0)
        {
            Label nothing = new () { Text = "no notes saved yet", X = 0, Y = 0 };

            Add (nothing);
            _last = nothing;

            return;
        }

        View? previous = null;

        foreach ((string folder, int depth, List<NoteRow> notes) in folders)
        {
            previous = Section (folder, depth, notes, previous);
        }

        _last = previous;

        SetNeedsLayout ();
        Layout ();
        SizeContent ();

        Button? target = _sections.FirstOrDefault (s => string.Equals (s.Folder, keep, StringComparison.OrdinalIgnoreCase)).Title
                         ?? _sections [0].Title;

        target.SetFocus ();
    }

    private View Section (string folder, int depth, List<NoteRow> notes, View? previous)
    {
        int left = depth * Indent;

        Button title = new ()
        {
            X = left,
            Y = previous is null ? 0 : Pos.Bottom (previous) + SectionGap,
            Width = Dim.Auto (),
            Height = 1,
            NoDecorations = true,
            NoPadding = true,
            HotKeySpecifier = new System.Text.Rune (0xffff)
        };

        TableView table = new ()
        {
            X = left,
            Y = Pos.Bottom (title),
            FullRowSelect = true,
            MultiSelect = false
        };

        table.Style.ShowHorizontalHeaderUnderline = true;
        table.Style.ShowVerticalCellLines = true;
        table.Style.ExpandLastColumn = false;

        Dictionary<string, Func<NoteRow, object>> columns = new ()
        {
            ["Document"] = n => n.Title,
            ["When"] = n => n.Modified.ToString ("MM-dd"),
            [DeleteHeader] = _ => DeleteMark
        };

        int deleteColumn = columns.Count - 1;

        table.SetSource (new EnumerableTableSource<NoteRow> (notes, columns));

        int wide = columns.Count + 1;
        int index = 0;

        foreach ((string header, Func<NoteRow, object> cell) in columns)
        {
            int longest = notes.Max (n => cell (n).ToString ()!.Length);

            wide += Math.Min (Math.Max (header.Length, longest), table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        int open = notes.Count + 3;

        bool shown = _expanded.Contains (folder);

        table.Width = wide;
        table.Height = shown ? open : 0;

        table.TabStop = shown ? TabBehavior.TabStop : TabBehavior.NoStop;

        void Retitle () => title.Text = $"{(shown ? Expanded : Collapsed)} {folder}  ({notes.Count})";

        Retitle ();

        title.Accepted += (_, _) =>
        {
            shown = !shown;
            table.Height = shown ? open : 0;
            table.TabStop = shown ? TabBehavior.TabStop : TabBehavior.NoStop;

            if (shown)
            {
                _expanded.Add (folder);
            }
            else
            {
                _expanded.Remove (folder);
            }

            Retitle ();

            SetNeedsLayout ();
            Layout ();
            SizeContent ();
            SetNeedsDraw ();
        };

        title.HasFocusChanged += (_, _) => Reveal (title);
        table.HasFocusChanged += (_, _) => Reveal (table);

        table.MouseEvent += (_, mouse) =>
        {
            if (!mouse.IsSingleClicked || mouse.Position is not { } position)
            {
                return;
            }

            if (table.ScreenToCell (position.X, position.Y) is not { } cell)
            {
                return;
            }

            if (cell.Y < 0 || cell.Y >= notes.Count)
            {
                return;
            }

            if (cell.X == deleteColumn)
            {
                DeleteNote (notes [cell.Y]);
            }
            else
            {
                Open (notes [cell.Y]);
            }

            mouse.Handled = true;
        };

        table.Accepted += (_, _) =>
        {
            int row = table.Value?.SelectedCell.Y ?? -1;

            if (row >= 0 && row < notes.Count)
            {
                Open (notes [row]);
            }
        };

        table.KeyDown += (_, key) =>
        {
            if (key != Key.Delete)
            {
                return;
            }

            int row = table.Value?.SelectedCell.Y ?? -1;

            if (row >= 0 && row < notes.Count)
            {
                DeleteNote (notes [row]);
            }

            key.Handled = true;
        };

        Add (title, table);
        _sections.Add ((folder, title, table));

        return table;
    }

    private static void Open (NoteRow note) => Notes.OpenInEditor?.Invoke (note.Path);

    private void DeleteNote (NoteRow note)
    {
        int? choice = MessageBox.Query (
                                       App!,
                                       "Delete note",
                                       note.Path
                                       + Environment.NewLine
                                       + Environment.NewLine
                                       + "The file is deleted from disk. The database keeps its text, marked deleted.",
                                       "_Delete",
                                       "_Cancel");

        if (choice != 0)
        {
            return;
        }

        try
        {
            if (File.Exists (note.Path))
            {
                File.Delete (note.Path);
            }

            NotesSync.MarkDeleted (note.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SqliteException or InvalidOperationException)
        {
            MessageBox.ErrorQuery (App!, "Cannot delete", note.Path + Environment.NewLine + ex.Message, new [] { "_Ok" });

            return;
        }

        App!.Invoke (Reload);
    }

    private static List<(string Folder, int Depth, List<NoteRow> Notes)> Folders ()
    {
        List<NoteRow> all = new ();

        if (Engine.Ready)
        {
            try
            {
                using SqliteConnection connection = Engine.Connect ();
                using SqliteCommand read = connection.CreateCommand ();

                read.CommandText = $"SELECT filepath, title, modified FROM {Constants.NotesTable} "
                                   + "WHERE deleted = 0 ORDER BY modified DESC;";

                using SqliteDataReader reader = read.ExecuteReader ();

                while (reader.Read ())
                {
                    DateTime.TryParse (reader.GetString (2), out DateTime modified);

                    all.Add (new (reader.GetString (1), modified, reader.GetString (0)));
                }
            }
            catch (SqliteException)
            {
                all = new ();
            }
        }

        return all
               .GroupBy (n => Path.GetDirectoryName (n.Path) ?? "")
               .OrderBy (g => g.Key, StringComparer.OrdinalIgnoreCase)
               .Select (g =>
                        {
                            (string name, int depth) = Describe (g.Key);

                            return (name, depth, g.ToList ());
                        })
               .ToList ();
    }

    private static (string Name, int Depth) Describe (string folder)
    {
        string? above = Path.GetDirectoryName (UserSettings.NotesPath);

        bool inside = above is { Length: > 0 }
                      && folder.Length > above.Length
                      && folder [above.Length] == Path.DirectorySeparatorChar
                      && folder.StartsWith (above, StringComparison.OrdinalIgnoreCase);

        if (!inside)
        {
            return (folder, 0);
        }

        string name = folder [(above!.Length + 1)..];

        return (name, name.Count (c => c == Path.DirectorySeparatorChar));
    }
}

