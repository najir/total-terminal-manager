// -----------------------------------------------------------------------------
//  The Todo tab: scans the configured folders for TODO markers.
// -----------------------------------------------------------------------------

using System.Text;
using Terminal.Gui.Editor.Highlighting;

internal sealed class TodoTestWindow : Window
{
    private readonly TextField _root;
    private readonly Label _status;
    private readonly TableView _table;

    private List<TodoHit> _hits = new ();

    private CancellationTokenSource? _cts;
    private bool _scanning;
    private bool _scannedOnce;

    public TodoTestWindow (Pos x, Pos y)
    {
        Title = "Todos";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        Label rootLabel = new ()
        {
            Text = "Root:",
            X = 0,
            Y = 0
        };

        _root = new ()
        {
            X = Pos.Right (rootLabel) + 1,
            Y = 0,
            Width = Dim.Fill (24),
            Text = DefaultRoot ()
        };

        Button rescan = new ()
        {
            Text = "_Rescan",
            X = Pos.Right (_root) + 1,
            Y = 0
        };

        Button open = new ()
        {
            Text = "_Open",
            X = Pos.Right (rescan),
            Y = 0
        };

        _status = new ()
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill (),
            Height = 1,
            Text = "Alt+R scans · Enter or double-click a row to open it"
        };

        _table = new ()
        {
            X = 0,
            Y = Pos.Bottom (_status),
            Width = Dim.Fill (),
            Height = Dim.Fill (),
            FullRowSelect = true,
            MultiSelect = false
        };

        TodoTable.ApplyStyle (_table);

        _table.SetSource (TodoTable.BuildSource (_hits));

        _table.Accepted += (_, _) => OpenSelected ();

        rescan.Accepted += (_, _) => _ = ScanAsync ();
        open.Accepted += (_, _) => OpenSelected ();

        Add (rootLabel, _root, rescan, open, _status, _table);

        VisibleChanged += (_, _) =>
        {
            if (!Visible || _scannedOnce)
            {
                return;
            }

            _scannedOnce = true;
            _ = ScanAsync ();
        };
    }

    private static string DefaultRoot ()
    {
        for (DirectoryInfo? d = new (Directory.GetCurrentDirectory ()); d is not null; d = d.Parent)
        {
            if (string.Equals (d.Name, "dev", StringComparison.OrdinalIgnoreCase))
            {
                return d.FullName;
            }
        }

        return Directory.GetCurrentDirectory ();
    }

    private async Task ScanAsync ()
    {
        if (_scanning)
        {
            return;
        }

        string root = _root.Text.Trim ();

        if (!Directory.Exists (root))
        {
            _status.Text = $"not a directory: {root}";

            return;
        }

        _scanning = true;
        _cts?.Cancel ();
        _cts = new ();
        CancellationToken token = _cts.Token;

        _status.Text = $"scanning {root} ...";

        try
        {
            List<TodoHit> hits = await Task.Run (
                                                 () => TodoScanner.SortByFileAge (
                                                                                  TodoScanner.Scan (
                                                                                   root,
                                                                                   token,
                                                                                   (files, found) => Ui (
                                                                                    () => _status.Text =
                                                                                        $"scanning ... {files:N0} files, {found:N0} todos"))),
                                                 token);

            Ui (() =>
            {
                _hits = hits;
                _table.SetSource (TodoTable.BuildSource (hits));
                _table.Refresh ();

                string capped = hits.Count >= TodoScanner.MaxHits ? $" (capped at {TodoScanner.MaxHits:N0})" : "";
                _status.Text = $"{hits.Count:N0} todos under {root}{capped} · Enter or double-click to open";
            });
        }
        catch (OperationCanceledException)
        {
            Ui (() => _status.Text = "scan cancelled");
        }
        catch (Exception ex)
        {
            Ui (() => _status.Text = $"scan failed: {ex.Message}");
        }
        finally
        {
            _scanning = false;
        }
    }

    private void OpenSelected ()
    {
        int row = _table.Value?.SelectedCell.Y ?? -1;

        if (row < 0 || row >= _hits.Count)
        {
            return;
        }

        OpenInEditor (_hits [row]);
    }

    private void OpenInEditor (TodoHit hit)
    {
        string text;
        bool hadBom;

        try
        {
            text = File.ReadAllText (hit.FullPath);
            hadBom = HasUtf8Bom (hit.FullPath);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery (App!, "Cannot open", $"{hit.FullPath}\n\n{ex.Message}", new [] { "_Ok" });

            return;
        }

        Dialog dialog = new ()
        {
            Title = $"{hit.File}:{hit.Line}",
            Width = Dim.Percent (90),
            Height = Dim.Percent (90)
        };

        Editor editor = new ()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill (),
            Height = Dim.Fill (1),
            GutterOptions = GutterOptions.LineNumbers,
            Text = text
        };

        editor.HighlightingDefinition =
            HighlightingManager.Instance.GetDefinitionByExtension (Path.GetExtension (hit.FullPath));

        void PlaceCaret ()
        {
            if (editor.Document is not { } document)
            {
                return;
            }

            int line = Math.Clamp (hit.Line, 1, Math.Max (1, document.LineCount));
            editor.CaretOffset = document.GetOffset (line, hit.Col);
        }

        Button close = new () { Text = "_Close" };

        close.Accepted += (_, _) => App!.RequestStop (dialog);

        dialog.AddButton (close);

        dialog.Add (editor);

        PlaceCaret ();
        dialog.Initialized += (_, _) => PlaceCaret ();

        App!.Run (dialog);
        dialog.Dispose ();
    }

    private static bool HasUtf8Bom (string path)
    {
        try
        {
            using FileStream stream = File.OpenRead (path);
            Span<byte> bom = stackalloc byte[3];

            return stream.Read (bom) == 3 && bom [0] == 0xEF && bom [1] == 0xBB && bom [2] == 0xBF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Ui (Action action)
    {
        IApplication? app = App;

        if (app is null)
        {
            action ();

            return;
        }

        app.Invoke (action);
    }
}

