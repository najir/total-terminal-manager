// -----------------------------------------------------------------------------
//  Dashboard widgets: system meters, feeds, tables and panels used across the tabs.
// -----------------------------------------------------------------------------

using Terminal.Gui.Input;
using Microsoft.Data.Sqlite;
using System.Text;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>One TODO marker found on one line of one file.</summary>
internal sealed record TodoHit (
    string Tag,
    string File,
    int Line,
    int Col,
    string Text,
    string FullPath,
    DateTime Modified);

/// <summary>Finds TODO markers under a folder. Pure I/O — touches no views.</summary>
internal static class TodoScanner
{
    private static readonly Regex MarkerPattern = new (@"\b(TODO|FIXME|HACK|XXX|BUG|Todo|todo)\b", RegexOptions.Compiled);

    private static readonly HashSet<string> ScanExtensions = new (StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cpp", ".cs", ".css", ".csproj", ".fs", ".go", ".h", ".hpp", ".html", ".java",
        ".js", ".json", ".jsx", ".md", ".php", ".ps1", ".psm1", ".py", ".rb", ".rs", ".scss",
        ".sh", ".sql", ".ts", ".tsx", ".txt", ".vb", ".xml", ".yaml", ".yml"
    };

    private static readonly HashSet<string> SkipDirectories = new (StringComparer.OrdinalIgnoreCase)
    {
        ".cache", ".git", ".hg", ".history", ".idea", ".mypy_cache", ".next", ".nuxt",
        ".pytest_cache", ".svn", ".venv", ".vs", ".vscode", "__pycache__", "bin", "build",
        "coverage", "dist", "node_modules", "obj", "packages", "site-packages", "target",
        "vendor", "venv"
    };

    public const int MaxHits = 10000;

    private const int MaxFileBytes = 1_000_000;
    private const int MaxLineLength = 100;

    public const int MaxTextLength = 300;

    public static List<TodoHit> Scan (
        string root,
        CancellationToken token,
        Action<int, int>? progress = null,
        int progressEvery = 500,
        int maxHits = MaxHits)
    {
        List<TodoHit> hits = new ();
        int scanned = 0;

        foreach (string path in EnumerateFiles (root, token))
        {
            token.ThrowIfCancellationRequested ();

            if (progress is not null && ++scanned % progressEvery == 0)
            {
                progress (scanned, hits.Count);
            }

            try
            {
                FileInfo info = new (path);

                if (info.Length > MaxFileBytes)
                {
                    continue;
                }

                DateTime modified = info.LastWriteTime;
                int lineNumber = 0;

                foreach (string line in File.ReadLines (path))
                {
                    lineNumber++;

                    if (line.Length > MaxLineLength)
                    {
                        continue;
                    }

                    Match match = MarkerPattern.Match (line);

                    if (!match.Success)
                    {
                        continue;
                    }

                    string text = line.Trim ();

                    hits.Add (new TodoHit (
                                           match.Value,
                                           Path.GetRelativePath (root, path),
                                           lineNumber,
                                           match.Index + 1,
                                           text.Length > MaxTextLength ? text [..MaxTextLength] + "..." : text,
                                           path,
                                           modified));

                    if (hits.Count >= maxHits)
                    {
                        return hits;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return hits;
    }

    public static List<TodoHit> SortByFileAge (IEnumerable<TodoHit> hits) =>
        hits.OrderBy (h => h.Modified)
            .ThenBy (h => h.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy (h => h.Line)
            .ToList ();

    private static IEnumerable<string> EnumerateFiles (string root, CancellationToken token)
    {
        Stack<string> pending = new ();
        pending.Push (root);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested ();

            string dir = pending.Pop ();
            string[] subDirectories;
            string[] files;

            try
            {
                subDirectories = Directory.GetDirectories (dir);
                files = Directory.GetFiles (dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string subDirectory in subDirectories)
            {
                if (!SkipDirectories.Contains (Path.GetFileName (subDirectory)))
                {
                    pending.Push (subDirectory);
                }
            }

            foreach (string file in files)
            {
                if (ScanExtensions.Contains (Path.GetExtension (file)))
                {
                    yield return file;
                }
            }
        }
    }
}

/// <summary>The table rendering: which columns exist and how wide they get.</summary>
internal static class TodoTable
{
    public static EnumerableTableSource<TodoHit> BuildSource (
        IEnumerable<TodoHit> hits,
        int fileTail = 0,
        int textMax = 0) =>
        new (hits, new Dictionary<string, Func<TodoHit, object>>
        {
            ["Tag"] = h => h.Tag,

            ["Modified"] = h => h.Modified.ToString ("MM-dd"),
            ["File"] = h => Tail (h.File, fileTail),
            ["Line"] = h => h.Line,

            ["Text"] = h => Clip (h.Text, textMax)
        });

    private static string Tail (string path, int max) =>
        max > 0 && path.Length > max ? "..." + path [^max..] : path;

    private static string Clip (string text, int max) =>
        max <= 0 || text.Length <= max
            ? text
            : max > 3
                ? text [..(max - 3)] + "..."
                : text [..max];

    public static void ApplyStyle (TableView table)
    {
        table.Style.ShowHorizontalHeaderUnderline = true;
        table.Style.ShowVerticalCellLines = true;
        table.Style.ExpandLastColumn = true;

        table.Style.GetOrCreateColumnStyle (0).MaxWidth = 6;

        table.Style.GetOrCreateColumnStyle (1).MaxWidth = 8;
        table.Style.GetOrCreateColumnStyle (2).MinWidth = 20;
        table.Style.GetOrCreateColumnStyle (2).MaxWidth = 60;
        table.Style.GetOrCreateColumnStyle (3).Alignment = Alignment.End;
        table.Style.GetOrCreateColumnStyle (3).MaxWidth = 7;
        table.Style.GetOrCreateColumnStyle (4).MinWidth = 30;
    }
}

/// <summary>
///     A table of the amount oldest todos under the user's file roots — the file-roots list in
///     user-settings, the same one the AI file tools use.
/// </summary>
internal sealed class TodoWidget : View
{
    public const int DefaultAmount = 15;

    private const int FilePathTail = 30;

    private readonly TableView _table;
    private readonly object _gate = new ();

    private List<TodoHit> _hits = new ();
    private CancellationTokenSource? _cts;
    private bool _started;

    public TodoWidget (
        int amount = DefaultAmount,
        int textWidth = TodoScanner.MaxTextLength,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Amount = Math.Max (0, amount);

        TextWidth = Math.Max (0, textWidth);

        Title = "Todos";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _table = new ()
        {
            X = 0,
            Y = 0,

            Width = Dim.Auto (),
            Height = Dim.Auto (),
            FullRowSelect = true,
            MultiSelect = false
        };

        TodoTable.ApplyStyle (_table);

        Add (_table);

        Show (new ());

        this.OnShown (() =>
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _ = RefreshAsync ();
        });

    }

    public string[] Roots => FileTools.Roots ();

    public int Amount { get; }

    public int TextWidth { get; }

    public IReadOnlyList<TodoHit> Hits => _hits;

    public TableView Table => _table;

    public TodoHit? Selected
    {
        get
        {
            int row = _table.Value?.SelectedCell.Y ?? -1;

            return row >= 0 && row < _hits.Count ? _hits [row] : null;
        }
    }

    public async Task RefreshAsync ()
    {
        string[] roots = Roots.Where (Directory.Exists).ToArray ();

        if (roots.Length == 0)
        {
            Show (new ());

            return;
        }

        CancellationTokenSource cts = new ();
        CancellationTokenSource? previous;

        lock (_gate)
        {
            previous = _cts;
            _cts = cts;
        }

        previous?.Cancel ();
        previous?.Dispose ();

        Show (Placeholder ("...", $"scanning {string.Join ("; ", roots)} ..."));

        try
        {
            List<TodoHit> found = await Task.Run (
                                                  () => TodoScanner
                                                        .SortByFileAge (roots.SelectMany (root => TodoScanner.Scan (root, cts.Token)))
                                                        .Take (Amount)
                                                        .ToList (),
                                                  cts.Token);

            Show (found);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Show (Placeholder ("!", $"scan failed: {ex.Message}"));
        }
    }

    private static List<TodoHit> Placeholder (string tag, string message) =>
        new () { new TodoHit (tag, "", 0, 0, message, "", DateTime.MinValue) };

    private void Show (List<TodoHit> rows)
    {
        Ui (() =>
        {
            _hits = rows.Count == 1 && rows [0].FullPath.Length == 0 ? new () : rows;

            Title = _hits.Count > 0 ? $"Todos ({_hits.Count})" : "Todos";

            ITableSource source = TodoTable.BuildSource (rows, FilePathTail, TextWidth);

            _table.SetSource (source);

            int wide = 0;

            for (int column = 0; column < source.Columns; column++)
            {
                int longest = Enumerable.Range (0, source.Rows)
                                        .Select (row => source [row, column]?.ToString ()?.Length ?? 0)
                                        .DefaultIfEmpty (0)
                                        .Max ();

                wide += Math.Min (
                                  Math.Max (source.ColumnNames [column].Length, longest),
                                  _table.Style.GetOrCreateColumnStyle (column).MaxWidth)
                        + 1;
            }

            _table.Width = Math.Max (1, wide);
            _table.Refresh ();

            this.Remeasure ();
            this.ResizeLayout ();
        });
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

    protected override void Dispose (bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _cts?.Cancel ();
                _cts?.Dispose ();
                _cts = null;
            }
        }

        base.Dispose (disposing);
    }
}

/// <summary>
///     Whatever is in next-up.txt, shown as-is. Re-read every time the tab comes back into view, so
///     editing the file shows up without a restart.
/// </summary>
internal sealed class NextUpWidget : View
{
    public static string FilePath => Path.Combine (UserSettings.FolderPath, "next-up.txt");

    private const string Nothing = "Next up could not be read or does not exist...";

    private readonly Label _text;

    public NextUpWidget (Pos? x = null, Pos? y = null, Dim? width = null, Dim? height = null)
    {
        Title = "Next up";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _text = new () { X = 0, Y = 0, Width = Dim.Auto (), Height = Dim.Auto () };

        Add (_text);

        Reload ();

        this.OnShown (Reload);
    }

    public void Reload ()
    {
        _text.Text = Read ();

        this.Remeasure ();
        this.ResizeLayout ();
    }

    private const int WrapWidth = 120;

    private static string Read ()
    {
        try
        {
            return File.ReadAllText (FilePath).TrimEnd () is { Length: > 0 } content ? Wrap (content) : Nothing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Nothing;
        }
    }

    private static string Wrap (string content) =>
        string.Join (Environment.NewLine,
                     content.Split (["\r\n", "\n"], StringSplitOptions.None)
                            .SelectMany (line => Wire.Wrap (line, WrapWidth)));
}

/// <summary>
///     Live CPU and RAM meters in one panel — a labelled readout and a bar for each, resampled on a
///     timer. 30% wide by default; set X/Y/Width/Height to override.
/// </summary>
internal sealed class SystemWidget : View
{
    public const int RequiredHeight = 4;

    private const int IntervalMs = 1800;
    private const int SlowEvery = 10;
    private const int ReadoutWidth = 10;
    private const int DiskWidth = 14;
    private const int BarWidth = 12;

    private static readonly uint StatusLength = (uint) Marshal.SizeOf<MemoryStatus> ();

    private readonly Label _cpuText;
    private readonly ProgressBar _cpuBar;
    private readonly Label _ramText;
    private readonly ProgressBar _ramBar;
    private readonly Label _diskText;
    private readonly ProgressBar _diskBar;
    private readonly Label _battText;
    private readonly ProgressBar _battBar;

    private readonly DriveInfo? _drive;
    private readonly double _diskTotalGb;

    private long _idle;
    private long _total;

    private int _ticks;
    private object? _timer;

    public SystemWidget (
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Title = "System";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();
        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _cpuText = new () { Y = 0, Width = ReadoutWidth, Height = 1, Text = "CPU   0.0%" };
        _cpuBar = new () { X = ReadoutWidth + 1, Y = 0, Width = BarWidth, Height = 1 };
        _ramText = new () { Y = 1, Width = ReadoutWidth, Height = 1, Text = "RAM   0.0%" };
        _ramBar = new () { X = ReadoutWidth + 1, Y = 1, Width = BarWidth, Height = 1 };

        _diskText = new () { X = Pos.Right (_cpuBar) + 2, Y = 0, Width = DiskWidth, Height = 1, Text = "DISK    n/a" };
        _diskBar = new () { X = Pos.Right (_diskText) + 1, Y = 0, Width = BarWidth, Height = 1 };
        _battText = new () { X = Pos.Left (_diskText), Y = 1, Width = DiskWidth, Height = 1, Text = "BATT    n/a" };
        _battBar = new () { X = Pos.Left (_diskBar), Y = 1, Width = BarWidth, Height = 1 };

        Add (_cpuText, _cpuBar, _ramText, _ramBar, _diskText, _diskBar, _battText, _battBar);

        try
        {
            DriveInfo drive = new (Path.GetPathRoot (AppContext.BaseDirectory) ?? "C:\\");
            _drive = drive.IsReady ? drive : null;
            _diskTotalGb = _drive is null ? 0 : _drive.TotalSize / 1073741824.0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _drive = null;
        }

        this.OnShown (() =>
        {
            if (_timer is not null)
            {
                Tick ();
            }
        });

        Initialized += (_, _) =>
        {
            MemoryStatus ram = new () { Length = StatusLength };

            if (App is not { } app
                || !OperatingSystem.IsWindows ()
                || GetSystemTimes (out _idle, out long kernel, out long user) == 0
                || GlobalMemoryStatusEx (ref ram) == 0)
            {
                _cpuText.Text = "CPU    n/a";
                _ramText.Text = "RAM    n/a";

                return;
            }

            _total = kernel + user;
            Title = $"System · {Environment.ProcessorCount} cores · {ram.TotalPhys / 1073741824.0:0} GB";

            _timer = app.AddTimeout (TimeSpan.FromMilliseconds (IntervalMs), Tick);
        };

        Disposing += (_, _) =>
        {
            if (_timer is { } token)
            {
                App?.RemoveTimeout (token);
                _timer = null;
            }
        };
    }

    private bool Tick ()
    {
        if (!AutoRefresh.Showing (this))
        {
            return true;
        }

        GetSystemTimes (out long idle, out long kernel, out long user);

        MemoryStatus ram = new () { Length = StatusLength };
        GlobalMemoryStatusEx (ref ram);

        long total = kernel + user;
        long span = total - _total;
        float cpu = span > 0 ? 1f - (float) (idle - _idle) / span : 0f;

        _idle = idle;
        _total = total;

        Set (_cpuBar, cpu);
        _cpuText.Text = $"CPU {cpu * 100f,5:0.0}%";
        Set (_ramBar, ram.MemoryLoad / 100f);
        _ramText.Text = $"RAM {ram.MemoryLoad,5:0.0}%";

        if (_ticks++ % SlowEvery == 0)
        {
            if (_drive is not null)
            {
                double usedGb = _diskTotalGb - _drive.TotalFreeSpace / 1073741824.0;

                Set (_diskBar, (float) (usedGb / _diskTotalGb));
                _diskText.Text = $"DISK {usedGb:0}/{_diskTotalGb:0}G";
            }

            GetSystemPowerStatus (out PowerStatus power);

            if (power.BatteryLifePercent <= 100)
            {
                Set (_battBar, power.BatteryLifePercent / 100f);
                _battText.Text = $"BATT {power.BatteryLifePercent,5:0}%";
            }
        }

        return true;
    }

    private static void Set (ProgressBar bar, float fraction)
    {
        float quantised = MathF.Floor (Math.Clamp (fraction, 0f, 1f) * BarWidth) / BarWidth;

        if (bar.Fraction != quantised)
        {
            bar.Fraction = quantised;
        }
    }

    [StructLayout (LayoutKind.Explicit, Size = 64)]
    private struct MemoryStatus
    {
        [FieldOffset (0)] public uint Length;
        [FieldOffset (4)] public uint MemoryLoad;
        [FieldOffset (8)] public ulong TotalPhys;
    }

    [DllImport ("kernel32.dll")]
    private static extern int GetSystemTimes (out long idle, out long kernel, out long user);

    [DllImport ("kernel32.dll")]
    private static extern int GlobalMemoryStatusEx (ref MemoryStatus buffer);

    [StructLayout (LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport ("kernel32.dll")]
    private static extern int GetSystemPowerStatus (out PowerStatus status);
}

/// <summary>
///     The newest Hacker News stories, title and posting time. Enter or double-click opens the
///     story in the default browser. Refetches every time it becomes visible.
/// </summary>
internal sealed class GpuSampler : IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private const uint FormatDouble = 0x00000200;
    private const uint MoreData = 0x800007D2;

    private IntPtr _query;
    private IntPtr _counter;

    public GpuSampler ()
    {
        if (!OperatingSystem.IsWindows ())
        {
            return;
        }

        try
        {
            if (PdhOpenQueryW (null, IntPtr.Zero, out _query) != 0
                || PdhAddEnglishCounterW (_query, CounterPath, IntPtr.Zero, out _counter) != 0)
            {
                Close ();

                return;
            }

            PdhCollectQueryData (_query);
        }
        catch (DllNotFoundException)
        {
            Close ();
        }
        catch (EntryPointNotFoundException)
        {
            Close ();
        }
    }

    public bool Available => _counter != IntPtr.Zero;

    public Dictionary<int, double> Read ()
    {
        Dictionary<int, double> byPid = new ();

        if (!Available || PdhCollectQueryData (_query) != 0)
        {
            return byPid;
        }

        uint size = 0;

        if (PdhGetFormattedCounterArrayW (_counter, FormatDouble, ref size, out _, IntPtr.Zero) != MoreData || size == 0)
        {
            return byPid;
        }

        IntPtr buffer = Marshal.AllocHGlobal ((int) size);

        try
        {
            if (PdhGetFormattedCounterArrayW (_counter, FormatDouble, ref size, out uint count, buffer) != 0)
            {
                return byPid;
            }

            int stride = Marshal.SizeOf<CounterItem> ();

            for (int i = 0; i < count; i++)
            {
                CounterItem item = Marshal.PtrToStructure<CounterItem> (buffer + i * stride);
                string name = Marshal.PtrToStringUni (item.Name) ?? "";

                if (item.Value.Reading <= 0 || !name.StartsWith ("pid_"))
                {
                    continue;
                }

                int cut = name.IndexOf ('_', 4);

                if (cut < 0 || !int.TryParse (name [4..cut], out int pid))
                {
                    continue;
                }

                byPid [pid] = byPid.TryGetValue (pid, out double had) ? had + item.Value.Reading : item.Value.Reading;
            }
        }
        finally
        {
            Marshal.FreeHGlobal (buffer);
        }

        return byPid;
    }

    public void Dispose () => Close ();

    private void Close ()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery (_query);
            _query = IntPtr.Zero;
        }

        _counter = IntPtr.Zero;
    }

    [DllImport ("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW (string? source, IntPtr user, out IntPtr query);

    [DllImport ("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW (IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport ("pdh.dll")]
    private static extern uint PdhCollectQueryData (IntPtr query);

    [DllImport ("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW (IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);

    [DllImport ("pdh.dll")]
    private static extern uint PdhCloseQuery (IntPtr query);

    [StructLayout (LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset (0)] public uint Status;
        [FieldOffset (8)] public double Reading;
    }

    [StructLayout (LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }
}

/// <summary>One process as the table shows it, plus the Weight the rows are ordered by.</summary>
internal sealed record ProcessLoad (string Name, long Ram, double Cpu, double Gpu)
{
    private static readonly double PhysicalRam =
        Math.Max (1, GC.GetGCMemoryInfo ().TotalAvailableMemoryBytes);

    public double Weight => (Cpu + Ram / PhysicalRam * 100) / 2;
}

/// <summary>
///     The heaviest processes right now - name, memory, CPU and GPU - one row each and no header,
///     so a three-row panel costs three rows.
/// </summary>
internal sealed class ProcessesWidget : View
{
    public const int DefaultAmount = 3;

    private const int IntervalMs = 12000;
    private const int NameWidth = 18;

    private readonly TableView _table;
    private readonly int _amount;
    private readonly GpuSampler _gpu = new ();
    private readonly Dictionary<string, Func<ProcessLoad, object>> _columns;

    private Dictionary<int, TimeSpan> _cpuBefore = new ();
    private DateTime _sampledAt = DateTime.UtcNow;

    private object? _timer;
    private bool _sampling;
    private bool _disposed;

    private List<string>? _lastCells;

    public ProcessesWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        _amount = Math.Max (1, amount);

        Title = "Processes";
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

        _table.Style.ShowHeaders = false;
        _table.Style.ShowHorizontalHeaderOverline = false;
        _table.Style.ShowHorizontalHeaderUnderline = false;
        _table.Style.ShowVerticalCellLines = false;
        _table.Style.ExpandLastColumn = false;

        _columns = new ()
        {
            ["Name"] = l => l.Name.Length <= NameWidth ? l.Name : l.Name [.. (NameWidth - 1)] + "…",
            ["Ram"] = l => Memory (l.Ram),
            ["Cpu"] = l => l.Cpu.ToString ("0.0") + "%",
            ["Gpu"] = l => _gpu.Available ? l.Gpu.ToString ("0.0") + "%" : "n/a"
        };

        Add (_table);

        this.OnShown (() =>
        {
            if (_timer is not null)
            {
                Tick ();
            }
        });

        Initialized += (_, _) =>
        {
            Tick ();

            if (App is { } app)
            {
                _timer = app.AddTimeout (TimeSpan.FromMilliseconds (IntervalMs), Tick);
            }
        };

        Disposing += (_, _) =>
        {
            if (_timer is { } token)
            {
                App?.RemoveTimeout (token);
                _timer = null;
            }

            _disposed = true;

            if (!_sampling)
            {
                _gpu.Dispose ();
            }
        };
    }

    private bool Tick ()
    {
        if (!AutoRefresh.Showing (this) || _sampling)
        {
            return true;
        }

        _sampling = true;

        _ = Task.Run (Sample).ContinueWith (
                                            task =>
                                            {
                                                _sampling = false;

                                                if (_disposed)
                                                {
                                                    _gpu.Dispose ();

                                                    return;
                                                }

                                                if (task.IsCompletedSuccessfully)
                                                {
                                                    Render (task.Result);
                                                }
                                            },
                                            CancellationToken.None,
                                            TaskContinuationOptions.None,
                                            new InvokeScheduler (App));

        return true;
    }

    private sealed class InvokeScheduler (IApplication? app) : TaskScheduler
    {
        protected override IEnumerable<Task>? GetScheduledTasks () => null;

        protected override void QueueTask (Task task)
        {
            if (app is null)
            {
                TryExecuteTask (task);
            }
            else
            {
                app.Invoke (() => TryExecuteTask (task));
            }
        }

        protected override bool TryExecuteTaskInline (Task task, bool taskWasPreviouslyQueued) => false;
    }

    private List<ProcessLoad> Sample ()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _sampledAt).TotalSeconds;
        Dictionary<int, double> gpu = _gpu.Read ();
        List<ProcessLoad> loads = new ();
        Dictionary<int, TimeSpan> current = new ();

        foreach (Process process in Process.GetProcesses ())
        {
            try
            {
                TimeSpan cpu = process.TotalProcessorTime;
                long ram = process.WorkingSet64;

                current [process.Id] = cpu;

                double percent = 0;

                if (elapsed > 0 && _cpuBefore.TryGetValue (process.Id, out TimeSpan before))
                {
                    percent = (cpu - before).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100;
                }

                loads.Add (
                           new (
                                process.ProcessName,
                                ram,
                                Math.Clamp (percent, 0, 100),
                                gpu.TryGetValue (process.Id, out double used) ? used : 0));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose ();
            }
        }

        _cpuBefore = current;
        _sampledAt = now;

        return loads.OrderByDescending (l => l.Weight).Take (_amount).ToList ();
    }

    private void Render (List<ProcessLoad> loads)
    {
        List<ProcessLoad> rows = loads.Count > 0 ? loads : [new ("no readings", 0, 0, 0)];

        List<string> cells = rows.SelectMany (r => _columns.Values.Select (cell => cell (r).ToString ()!)).ToList ();

        if (_lastCells is not null && _lastCells.SequenceEqual (cells))
        {
            return;
        }

        _lastCells = cells;

        _table.SetSource (new EnumerableTableSource<ProcessLoad> (rows, _columns));

        int wide = _columns.Count + 1;
        int index = 0;

        foreach ((string _, Func<ProcessLoad, object> cell) in _columns)
        {
            int longest = rows.Max (r => cell (r).ToString ()!.Length);

            wide += Math.Min (longest, _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        bool resized = _table.Width is not DimAbsolute { Size: var w } || w != wide
                       || _table.Height is not DimAbsolute { Size: var h } || h != rows.Count;

        if (resized)
        {
            _table.Width = wide;
            _table.Height = rows.Count;
        }

        _table.Refresh ();

        if (resized)
        {
            this.Remeasure ();
            this.ResizeLayout ();
        }
    }

    private static string Memory (long bytes) =>
        bytes >= 1073741824 ? (bytes / 1073741824.0).ToString ("0.0") + " GB" : (bytes / 1048576.0).ToString ("0") + " MB";
}

internal sealed class HackerNewsWidget : View
{
    private const int TitleWidth = 50;

    private const string Heading = "Hacker News";

    private const string HomePage = "https://news.ycombinator.com";

    private const string Api = "https://hacker-news.firebaseio.com/v0/";

    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (15) };

    private readonly ListView _list;
    private readonly List<string> _urls = new ();

    private bool _loading;

    public HackerNewsWidget (
        int count = 5,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Title = Heading;
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();
        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _list = new () { Width = Dim.Auto (), Height = Dim.Auto () };
        Add (_list);

        _list.Accepted += (_, _) =>
        {
            if (_list.SelectedItem is { } row && row >= 0 && row < _urls.Count)
            {
                Process.Start (new ProcessStartInfo (_urls [row]) { UseShellExecute = true });
            }
        };

        this.OnShown (() => _ = LoadAsync (count));

        this.RefreshEvery (AutoRefresh.DefaultInterval, () => LoadAsync (count));
    }

    private async Task LoadAsync (int count)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;

        ObservableCollection<string> rows = new ();
        List<string> urls = new ();

        try
        {
            int[] ids = (await Http.GetFromJsonAsync<int[]> ($"{Api}newstories.json") ?? [])
                        .Take (count)
                        .ToArray ();

            JsonElement[] items = await Task.WhenAll (ids.Select (FetchAsync));

            foreach (JsonElement item in items)
            {
                if (!TryRead (item, out string row, out string url))
                {
                    continue;
                }

                rows.Add (row);
                urls.Add (url);
            }

            if (rows.Count == 0)
            {
                rows.Add (ids.Length == 0 ? "no stories" : "hacker news unavailable");
                urls.Add (HomePage);
            }
        }
        catch (Exception ex)
        {
            rows.Clear ();
            urls.Clear ();

            rows.Add ($"hacker news unavailable: {Summarise (ex)}");
            urls.Add (HomePage);
        }
        finally
        {
            _loading = false;
        }

        App?.Invoke (() => Show (rows, urls));
    }

    private static async Task<JsonElement> FetchAsync (int id)
    {
        try
        {
            return await Http.GetFromJsonAsync<JsonElement> ($"{Api}item/{id}.json");
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static bool TryRead (JsonElement item, out string row, out string url)
    {
        row = "";
        url = "";

        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string title = item.TryGetProperty ("title", out JsonElement t) ? t.GetString () ?? "" : "";

        DateTime when = DateTimeOffset
                        .FromUnixTimeSeconds (item.TryGetProperty ("time", out JsonElement s) ? s.GetInt64 () : 0)
                        .LocalDateTime;

        row = $"{when:MM-dd}  {(title.Length > TitleWidth ? title [..TitleWidth] + "..." : title)}";

        url = item.TryGetProperty ("url", out JsonElement u) && u.GetString () is { Length: > 0 } link
                  ? link
                  : item.TryGetProperty ("id", out JsonElement i) && i.TryGetInt32 (out int id)
                      ? $"https://news.ycombinator.com/item?id={id}"
                      : HomePage;

        return true;
    }

    private static string Summarise (Exception ex)
    {
        string message = ex.Message.Replace ('\r', ' ').Replace ('\n', ' ').Trim ();

        return message.Length > TitleWidth ? message [..TitleWidth] + "..." : message;
    }

    private void Show (ObservableCollection<string> rows, List<string> urls)
    {
        try
        {
            Title = Heading;

            _list.SelectedItem = null;

            _urls.Clear ();
            _urls.AddRange (urls);
            _list.SetSource (rows);
            Fit (_list, rows);
        }
        catch (Exception ex)
        {
            Title = $"Hacker News (stale: {Summarise (ex)})";
        }
    }

    private static void Fit (ListView list, ICollection<string> rows)
    {
        list.Width = Math.Max (1, rows.Count == 0 ? 1 : rows.Max (r => r.Length));

        list.SuperView?.Remeasure ();

        list.SuperView?.ResizeLayout ();
    }

}

/// <summary>
///     The last few Teams messages across all of your chats — who sent it, what it said, and how
///     long ago. Refetches every time it becomes visible.
/// </summary>
internal sealed class TeamsWidget : View
{
    private const int TextWidth = 60;

    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (15) };

    private readonly ListView _list;

    public TeamsWidget (
        int count = 5,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Title = "Teams";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();
        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _list = new () { Width = Dim.Auto (), Height = Dim.Auto () };
        Add (_list);

        this.OnShown (() => _ = LoadAsync (count));

        this.RefreshEvery (AutoRefresh.DefaultInterval, () => LoadAsync (count));
    }

    private async Task LoadAsync (int count)
    {
        ObservableCollection<string> rows = new ();

        try
        {
            using HttpRequestMessage request = new (
                                                    HttpMethod.Get,
                                                    $"https://graph.microsoft.com/v1.0/me/chats/getAllMessages?$top={count}");

            string token = await GraphAuth.TokenAsync (
                                                       message => App?.Invoke (
                                                        () => _list.SetSource (
                                                         new ObservableCollection<string> (Wire.Wrap (message, TextWidth)))));

            request.Headers.Authorization = new ("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync (request);

            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement> ();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException (Wire.Str (body, "error", "message") ?? response.StatusCode.ToString ());
            }

            foreach (JsonElement message in body.GetProperty ("value").EnumerateArray ())
            {
                string who = Wire.Str (message, "from", "user", "displayName")
                             ?? Wire.Str (message, "from", "application", "displayName")
                             ?? "system";

                string text = Regex.Replace (
                                             WebUtility.HtmlDecode (
                                                                    Regex.Replace (Wire.Str (message, "body", "content") ?? "", "<[^>]+>", " ")),
                                             @"\s+",
                                             " ")
                                   .Trim ();

                TimeSpan age = DateTimeOffset.UtcNow
                               - (DateTimeOffset.TryParse (Wire.Str (message, "createdDateTime"), out DateTimeOffset sent)
                                      ? sent
                                      : DateTimeOffset.UtcNow);

                rows.Add ($"{Ago (age),8}  {who}: {(text.Length > TextWidth ? text [..TextWidth] + "..." : text)}");
            }

            if (rows.Count == 0)
            {
                rows.Add ("no recent messages");
            }
        }
        catch (Exception ex)
        {
            rows.Add ($"teams unavailable: {ex.Message}");
        }

        App?.Invoke (
                     () =>
                     {
                         _list.SetSource (rows);
                         Fit (_list, rows);
                     });
    }

    private static string Ago (TimeSpan age) => age switch
    {
        { TotalSeconds: < 60 } => "just now",
        { TotalMinutes: < 60 } => $"{(int) age.TotalMinutes}m ago",
        { TotalHours: < 24 } => $"{(int) age.TotalHours}h ago",
        { TotalDays: < 7 } => $"{(int) age.TotalDays}d ago",
        _ => $"{(int) (age.TotalDays / 7)}w ago"
    };

    private static void Fit (ListView list, ICollection<string> rows)
    {
        list.Width = Math.Max (1, rows.Count == 0 ? 1 : rows.Max (r => r.Length));

        list.SuperView?.Remeasure ();

        list.SuperView?.ResizeLayout ();
    }

}

/// <summary>Your last few inbox emails — sender and subject, newest first.</summary>
internal sealed class MailWidget : View
{
    private const int TextWidth = 60;

    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (15) };

    private readonly ListView _list;

    public MailWidget (
        int count = 10,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Title = "Inbox";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();
        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _list = new () { Width = Dim.Auto (), Height = Dim.Auto () };
        Add (_list);

        this.OnShown (() => _ = LoadAsync (count));

        this.RefreshEvery (AutoRefresh.DefaultInterval, () => LoadAsync (count));
    }

    private async Task LoadAsync (int count)
    {
        string provider = UserSettings.Get (UserSettings.EmailProvider);
        List<string> rows = new ();

        try
        {
            rows = provider == "gmail" ? await GmailAsync (count) : await GraphAsync (count);

            if (rows.Count == 0)
            {
                rows.Add ("inbox is empty");
            }
        }
        catch (Exception ex)
        {
            rows.Add ($"{provider} mail unavailable: {ex.Message}");
        }

        App?.Invoke (() =>
        {
            Title = $"Inbox ({provider})";
            ObservableCollection<string> source = new (rows);

            _list.SetSource (source);
            Fit (_list, source);
        });
    }

    private async Task<List<string>> GraphAsync (int count)
    {
        using HttpRequestMessage request = new (
                                                HttpMethod.Get,
                                                "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages"
                                                + $"?$top={count}"
                                                + "&$select=subject,from,receivedDateTime"
                                                + "&$orderby=receivedDateTime%20desc");

        request.Headers.Authorization = new (
                                             "Bearer",
                                             await GraphAuth.TokenAsync (
                                                                         message => App?.Invoke (
                                                                          () => _list.SetSource (
                                                                           new ObservableCollection<string> (
                                                                            Wire.Wrap (message, TextWidth))))));

        using HttpResponseMessage response = await Http.SendAsync (request);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement> ();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException (Wire.Str (body, "error", "message") ?? response.StatusCode.ToString ());
        }

        return body.GetProperty ("value")
                   .EnumerateArray ()
                   .Select (
                            mail => Row (
                                         Wire.Str (mail, "from", "emailAddress", "name")
                                         ?? Wire.Str (mail, "from", "emailAddress", "address"),
                                         Wire.Str (mail, "subject")))
                   .ToList ();
    }

    private async Task<List<string>> GmailAsync (int count)
    {
        string token = await GmailAuth.TokenAsync ();

        async Task<JsonElement> Get (string url)
        {
            using HttpRequestMessage request = new (HttpMethod.Get, url);
            request.Headers.Authorization = new ("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync (request);

            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement> ();

            return response.IsSuccessStatusCode
                       ? body
                       : throw new HttpRequestException (
                                                         Wire.Str (body, "error", "message")
                                                         ?? response.StatusCode.ToString ());
        }

        JsonElement list = await Get (
                                      "https://gmail.googleapis.com/gmail/v1/users/me/messages"
                                      + $"?maxResults={count}&labelIds=INBOX");

        if (!list.TryGetProperty ("messages", out JsonElement ids))
        {
            return new List<string> ();
        }

        JsonElement[] mails = await Task.WhenAll (
                                                  ids.EnumerateArray ()
                                                     .Select (
                                                              id => Get (
                                                                         "https://gmail.googleapis.com/gmail/v1/users/me/messages/"
                                                                         + Wire.Str (id, "id")
                                                                         + "?format=metadata"
                                                                         + "&metadataHeaders=Subject&metadataHeaders=From")));

        return mails.Select (mail => Row (Sender (Header (mail, "From")), Header (mail, "Subject"))).ToList ();
    }

    private static string? Header (JsonElement mail, string name) =>
        mail.TryGetProperty ("payload", out JsonElement payload) && payload.TryGetProperty ("headers", out JsonElement headers)
            ? headers.EnumerateArray ()
                     .Where (h => string.Equals (Wire.Str (h, "name"), name, StringComparison.OrdinalIgnoreCase))
                     .Select (h => Wire.Str (h, "value"))
                     .FirstOrDefault ()
            : null;

    private static string? Sender (string? from)
    {
        int bracket = from?.IndexOf ('<') ?? -1;

        return bracket > 0 ? from! [..bracket].Trim ().Trim ('"') : from;
    }

    private static string Row (string? who, string? subject)
    {
        subject ??= "(no subject)";

        return $"{who ?? "unknown"}: {(subject.Length > TextWidth ? subject [..TextWidth] + "..." : subject)}";
    }

    private static void Fit (ListView list, ICollection<string> rows)
    {
        list.Width = Math.Max (1, rows.Count == 0 ? 1 : rows.Max (r => r.Length));

        list.SuperView?.Remeasure ();

        list.SuperView?.ResizeLayout ();
    }

}

/// <summary>One row of FreshdeskWidget: who raised a ticket, and its title.</summary>
internal sealed record FreshdeskTicket (long Id, string Who, string Title);

/// <summary>
///     Your most recent Freshdesk tickets as a two-column table — who raised each one, and its
///     title. Fetches the first time it becomes visible, then again on a timer while it stays on
///     screen.
/// </summary>
internal sealed class FreshdeskWidget : View
{
    public const int DefaultAmount = 5;

    private const int WhoWidth = 20;
    private const int TitleWidth = 60;

    private const string DomainVariable = "FRESHDESK_DOMAIN";
    private const string KeyVariable = "FRESHDESK_API_KEY";

    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (15) };

    private readonly TableView _table;

    private bool _loading;

    private List<FreshdeskTicket> _tickets = new ();

    public FreshdeskWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Amount = Math.Clamp (amount, 1, 100);

        Title = "Freshdesk";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _table = new ()
        {
            X = 0,
            Y = 0,

            Width = Dim.Auto (),
            Height = Dim.Auto (),
            FullRowSelect = true,
            MultiSelect = false
        };

        _table.Style.ShowHorizontalHeaderUnderline = true;
        _table.Style.ShowVerticalCellLines = true;

        _table.Style.GetOrCreateColumnStyle (0).MaxWidth = WhoWidth;
        _table.Style.GetOrCreateColumnStyle (1).MaxWidth = TitleWidth;

        _table.Accepted += (_, _) => Open ();

        Add (_table);

        Show (new List<FreshdeskTicket> { new (0, "", "loading ...") });

        this.OnShown (() => _ = LoadAsync ());

        this.RefreshEvery (AutoRefresh.DefaultInterval, () => LoadAsync ());
    }

    public int Amount { get; }

    public TableView Table => _table;

    private async Task LoadAsync ()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;

        List<FreshdeskTicket> rows;

        try
        {
            rows = await FetchAsync (Amount);

            if (rows.Count == 0)
            {
                rows.Add (new (0, "", "no tickets"));
            }
        }
        catch (Exception ex)
        {
            rows = new List<FreshdeskTicket> { new (0, "!", Cap (Flatten (ex.Message), TitleWidth)) };
        }
        finally
        {
            _loading = false;
        }

        Show (rows);
    }

    private static async Task<List<FreshdeskTicket>> FetchAsync (int amount)
    {
        using HttpRequestMessage request = new (
                                                HttpMethod.Get,
                                                $"https://{Env.Require (DomainVariable)}/api/v2/tickets"
                                                + "?order_by=created_at&order_type=desc"
                                                + "&include=requester"
                                                + $"&per_page={amount}");

        request.Headers.Authorization = new (
                                             "Basic",
                                             Convert.ToBase64String (
                                                                     Encoding.ASCII.GetBytes (
                                                                      $"{Env.Require (KeyVariable)}:X")));

        using HttpResponseMessage response = await Http.SendAsync (request);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement> ();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException (
                                            Wire.Str (body, "message")
                                            ?? Wire.Str (body, "description")
                                            ?? response.StatusCode.ToString ());
        }

        return body.ValueKind == JsonValueKind.Array
                   ? body.EnumerateArray ().Select (Row).ToList ()
                   : throw new HttpRequestException ("unexpected response from Freshdesk");
    }

    private static FreshdeskTicket Row (JsonElement ticket) =>
        new (
             ticket.TryGetProperty ("id", out JsonElement id) && id.TryGetInt64 (out long number)
                 ? number
                 : 0,

             Cap (
                  Wire.Str (ticket, "requester", "name") is { Length: > 0 } name
                      ? name
                      : Wire.Str (ticket, "requester", "email") ?? "unknown",
                  WhoWidth),
             Cap (
                  Wire.Str (ticket, "subject") is { Length: > 0 } subject ? subject : "(no subject)",
                  TitleWidth));

    private static string Cap (string text, int max) =>
        text.Length <= max ? text : max > 3 ? text [..(max - 3)] + "..." : text [..max];

    private static string Flatten (string message) => message.Replace ('\r', ' ').Replace ('\n', ' ').Trim ();

    private void Open ()
    {
        int row = _table.Value?.SelectedCell.Y ?? -1;

        if (row < 0 || row >= _tickets.Count)
        {
            return;
        }

        FreshdeskTicket ticket = _tickets [row];

        if (ticket.Id <= 0)
        {
            return;
        }

        string url = $"https://{Env.Require (DomainVariable)}/a/tickets/{ticket.Id}";

        try
        {
            Process.Start (new ProcessStartInfo (url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery (App!, "Cannot open", $"{url}\n\n{ex.Message}", new [] { "_Ok" });
        }
    }

    private void Show (List<FreshdeskTicket> rows)
    {
        Ui (() =>
        {
            _tickets = rows;

            ITableSource source = new EnumerableTableSource<FreshdeskTicket> (
                                                                              rows,
                                                                              new Dictionary<string, Func<FreshdeskTicket, object>>
                                                                              {
                                                                                  ["Who"] = t => t.Who,
                                                                                  ["Title"] = t => t.Title
                                                                              });

            _table.SetSource (source);

            int wide = 0;

            for (int column = 0; column < source.Columns; column++)
            {
                int longest = Enumerable.Range (0, source.Rows)
                                        .Select (row => source [row, column]?.ToString ()?.Length ?? 0)
                                        .DefaultIfEmpty (0)
                                        .Max ();

                wide += Math.Min (
                                  Math.Max (source.ColumnNames [column].Length, longest),
                                  _table.Style.GetOrCreateColumnStyle (column).MaxWidth)
                        + 1;
            }

            _table.Width = Math.Max (1, wide);
            _table.Refresh ();

            this.Remeasure ();
            this.ResizeLayout ();
        });
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

/// <summary>Everything one pass over the transcripts produces.</summary>
internal sealed class ClaudeStats
{
    public List<ClaudeSession> Sessions { get; } = new ();
    public Dictionary<string, int> ToolCalls { get; } = new (StringComparer.Ordinal);

    public long[] TokensByDay { get; init; } = [];
    public DateTime[] Days { get; init; } = [];

    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }

    public long Total => Input + Output + CacheRead + CacheWrite;
}

/// <summary>
///     Reads Claude Code's own JSONL transcripts under ~/.claude/projects. Pure I/O — touches no
///     views, so callers must run Collect off the UI loop.
/// </summary>
internal static class ClaudeTranscripts
{
    public const int WindowDays = 14;

    public static string Root () =>
        Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static ClaudeStats Empty () => new () { TokensByDay = new long[WindowDays], Days = new DateTime[WindowDays] };

    public static ClaudeStats Collect ()
    {
        DateTime firstDay = DateTime.Today.AddDays (-(WindowDays - 1));

        ClaudeStats stats = new ()
        {
            TokensByDay = new long[WindowDays],
            Days = Enumerable.Range (0, WindowDays).Select (d => firstDay.AddDays (d)).ToArray ()
        };

        string root = Root ();

        if (!Directory.Exists (root))
        {
            return stats;
        }

        foreach (string file in Directory.EnumerateFiles (root, "*.jsonl", SearchOption.AllDirectories))
        {
            ReadSession (file, firstDay, stats);
        }

        stats.Sessions.Sort ((a, b) => b.Ended.CompareTo (a.Ended));

        return stats;
    }

    public static void ReadSession (string file, DateTime firstDay, ClaudeStats stats)
    {
        string? id = null;
        string? project = null;
        DateTime started = DateTime.MaxValue;
        DateTime ended = DateTime.MinValue;
        long tokens = 0;
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        int messages = 0;

        HashSet<string> countedMessages = new (StringComparer.Ordinal);
        HashSet<string> countedTools = new (StringComparer.Ordinal);
        Dictionary<string, int> tools = new (StringComparer.Ordinal);

        IEnumerable<string> lines;

        try
        {
            lines = File.ReadLines (file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonDocument doc;

            try
            {
                doc = JsonDocument.Parse (line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                JsonElement record = doc.RootElement;

                if (record.ValueKind != JsonValueKind.Object
                    || !record.TryGetProperty ("timestamp", out JsonElement ts)
                    || !ts.TryGetDateTimeOffset (out DateTimeOffset when))
                {
                    continue;
                }

                DateTime local = when.ToLocalTime ().DateTime;

                if (local.Date < firstDay)
                {
                    continue;
                }

                id ??= GetString (record, "sessionId");
                project ??= GetString (record, "cwd");

                if (local < started) { started = local; }
                if (local > ended) { ended = local; }

                if (GetString (record, "type") != "assistant"
                    || !record.TryGetProperty ("message", out JsonElement message)
                    || message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (message.TryGetProperty ("content", out JsonElement content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement block in content.EnumerateArray ())
                    {
                        if (block.ValueKind != JsonValueKind.Object
                            || GetString (block, "type") != "tool_use")
                        {
                            continue;
                        }

                        string? toolId = GetString (block, "id");
                        string? name = GetString (block, "name");

                        if (name is null || (toolId is not null && !countedTools.Add (toolId)))
                        {
                            continue;
                        }

                        tools [name] = tools.GetValueOrDefault (name) + 1;
                    }
                }

                string? messageId = GetString (message, "id");

                if (messageId is not null && !countedMessages.Add (messageId))
                {
                    continue;
                }

                if (!message.TryGetProperty ("usage", out JsonElement usage)
                    || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                messages++;

                long msgIn = GetLong (usage, "input_tokens");
                long msgOut = GetLong (usage, "output_tokens");
                long msgRead = GetLong (usage, "cache_read_input_tokens");
                long msgWrite = GetLong (usage, "cache_creation_input_tokens");
                long msgTotal = msgIn + msgOut + msgRead + msgWrite;

                input += msgIn;
                output += msgOut;
                cacheRead += msgRead;
                cacheWrite += msgWrite;
                tokens += msgTotal;

                int day = (local.Date - firstDay).Days;

                if (day >= 0 && day < WindowDays)
                {
                    stats.TokensByDay [day] += msgTotal;
                }
            }
        }

        if (id is null || ended == DateTime.MinValue)
        {
            return;
        }

        stats.Input += input;
        stats.Output += output;
        stats.CacheRead += cacheRead;
        stats.CacheWrite += cacheWrite;

        foreach ((string name, int count) in tools)
        {
            stats.ToolCalls [name] = stats.ToolCalls.GetValueOrDefault (name) + count;
        }

        stats.Sessions.Add (new ClaudeSession (
                                               id,
                                               project ?? "?",
                                               file,
                                               started,
                                               ended,
                                               messages,
                                               tools.Values.Sum (),
                                               tokens,
                                               input,
                                               output,
                                               cacheRead,
                                               cacheWrite));
    }

    private static string? GetString (JsonElement element, string name) =>
        element.TryGetProperty (name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString ()
            : null;

    private static long GetLong (JsonElement element, string name) =>
        element.TryGetProperty (name, out JsonElement value) && value.TryGetInt64 (out long result) ? result : 0;
}

/// <summary>One Claude Code session — one .jsonl file under ~/.claude/projects.</summary>
internal sealed record ClaudeSession (
    string Id,
    string Project,
    string Path,
    DateTime Started,
    DateTime Ended,
    int Messages,
    int ToolCalls,
    long Tokens,
    long Input = 0,
    long Output = 0,
    long CacheRead = 0,
    long CacheWrite = 0);

/// <summary>Per-session token spend: which project, the days it ran, and what it cost.</summary>
internal sealed class SessionsWidget : View
{
    public const int DefaultAmount = 10;

    private const string Heading = "Sessions";

    private readonly TableView _table;

    private bool _fed;

    public SessionsWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Amount = Math.Max (0, amount);

        Title = Heading;
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();
        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _table = new ()
        {
            Width = Dim.Auto (),
            Height = Dim.Auto (),

            CanFocus = false
        };

        _table.Style.ShowHorizontalHeaderUnderline = true;
        _table.Style.ShowVerticalCellLines = true;

        _table.Style.ExpandLastColumn = false;

        _table.Style.GetOrCreateColumnStyle (0).MaxWidth = 20;
        _table.Style.GetOrCreateColumnStyle (2).Alignment = Alignment.End;
        _table.Style.GetOrCreateColumnStyle (3).Alignment = Alignment.End;
        _table.Style.GetOrCreateColumnStyle (4).Alignment = Alignment.End;

        Add (_table);

        Show (Array.Empty<ClaudeSession> ());
        _fed = false;

        this.OnShown (() =>
        {
            if (!_fed)
            {
                _ = LoadAsync ();
            }
        });
    }

    private async Task LoadAsync ()
    {
        ClaudeStats stats = await Task.Run (ClaudeTranscripts.Collect);

        App?.Invoke (() =>
        {
            Show (stats.Sessions);
            _fed = false;
        });
    }

    public int Amount { get; }

    public void Show (IReadOnlyList<ClaudeSession> sessions)
    {
        int total = sessions.Count;

        if (total > Amount)
        {
            sessions = sessions.Take (Amount).ToList ();
        }

        Title = total > sessions.Count ? $"{Heading} ({sessions.Count} of {total})" : $"{Heading} ({total})";
        _fed = true;

        Dictionary<string, Func<ClaudeSession, object>> columns = new ()
        {
            ["Project"] = s => s.Project,
            ["Started"] = s => s.Started.ToString ("MM-dd"),

            ["Msgs"] = s => s.Messages,
            ["Tools"] = s => s.ToolCalls,
            ["Tokens"] = s => s.Tokens.ToString ("N0")
        };

        _table.SetSource (new EnumerableTableSource<ClaudeSession> (sessions, columns));

        int width = columns.Count + 1;
        int index = 0;

        foreach ((string header, Func<ClaudeSession, object> cell) in columns)
        {
            int longest = sessions.Count == 0 ? 0 : sessions.Max (s => cell (s).ToString ()!.Length);

            width += Math.Min (Math.Max (header.Length, longest), _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        _table.Width = width;
        _table.Refresh ();
        this.Remeasure ();
        this.ResizeLayout ();
    }
}

/// <summary>
///     The newest session's token spend, split into the four usage buckets with a bar for each
///     one's share of the session.
/// </summary>
internal sealed class LastSessionWidget : View
{
    private static readonly string[] BucketNames = ["Input", "Output", "Cache read", "Cache write"];

    private const int NameWidth = 12;
    private const int ValueWidth = 13;
    private const int ReadoutWidth = NameWidth + ValueWidth;
    private const int BarWidth = 12;
    private const int LineWidth = ReadoutWidth + 1 + BarWidth;

    private readonly Label _project;
    private readonly Label _when;
    private readonly Label _total;
    private readonly Label _counts;
    private readonly (Label Text, ProgressBar Bar)[] _buckets;

    private bool _fed;

    public LastSessionWidget (
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Title = "Last Session";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _project = new () { Y = 0, Width = LineWidth, Height = 1 };
        _when = new () { Y = 1, Width = LineWidth, Height = 1 };

        _buckets = new (Label Text, ProgressBar Bar)[BucketNames.Length];

        for (int i = 0; i < BucketNames.Length; i++)
        {
            _buckets [i] = (
                new Label { Y = i + 2, Width = ReadoutWidth, Height = 1 },
                new ProgressBar { X = ReadoutWidth + 1, Y = i + 2, Width = BarWidth, Height = 1 });
        }

        _total = new () { Y = BucketNames.Length + 2, Width = LineWidth, Height = 1 };
        _counts = new () { Y = BucketNames.Length + 3, Width = LineWidth, Height = 1 };

        Add (_project, _when, _total, _counts);

        foreach ((Label text, ProgressBar bar) in _buckets)
        {
            Add (text, bar);
        }

        Show (Array.Empty<ClaudeSession> ());
        _fed = false;

        this.OnShown (() =>
        {
            if (!_fed)
            {
                _ = LoadAsync ();
            }
        });
    }

    private async Task LoadAsync ()
    {
        ClaudeStats stats = await Task.Run (ClaudeTranscripts.Collect);

        App?.Invoke (() =>
        {
            Show (stats.Sessions);
            _fed = false;
        });
    }

    public void Show (IReadOnlyList<ClaudeSession> sessions)
    {
        _fed = true;

        ClaudeSession? session = sessions.Count == 0 ? null : sessions.MaxBy (s => s.Ended);

        long[] values = session is null
                            ? [0, 0, 0, 0]
                            : [session.Input, session.Output, session.CacheRead, session.CacheWrite];

        long total = Math.Max (1, session?.Tokens ?? 0);

        for (int i = 0; i < BucketNames.Length; i++)
        {
            _buckets [i].Text.Text = Readout (BucketNames [i], values [i]);
            _buckets [i].Bar.Fraction = (float) values [i] / total;
        }

        if (session is null)
        {
            Title = "Last Session";
            _project.Text = "no sessions in the window";
            _when.Text = string.Empty;
            _total.Text = Readout ("Total", 0);
            _counts.Text = string.Empty;
        }
        else
        {
            Title = $"Last Session ({Short (session.Tokens)})";
            _project.Text = Tail (session.Project, LineWidth);
            _when.Text = $"{session.Started:MMM d HH:mm} - {session.Ended:HH:mm}"
                         + $" · {Elapsed (session.Ended - session.Started)}";
            _total.Text = Readout ("Total", session.Tokens);
            _counts.Text = $"{session.Messages:N0} msgs · {session.ToolCalls:N0} tools";
        }

        this.Remeasure ();
        this.ResizeLayout ();
    }

    private static string Readout (string name, long value) =>
        name.PadRight (NameWidth) + value.ToString ("N0").PadLeft (ValueWidth);

    private static string Short (long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:0.0}M",
        >= 1_000 => $"{value / 1_000.0:0.0}K",
        _ => value.ToString ()
    };

    private static string Elapsed (TimeSpan span)
    {
        int hours = (int) span.TotalHours;

        return hours > 0 ? $"{hours}h {span.Minutes}m" : $"{span.Minutes}m";
    }

    private static string Tail (string text, int width) =>
        text.Length <= width ? text : "..." + text [^(width - 3)..];
}

/// <summary>
///     A button per .md file in the skills folder. Same shape as ScriptsWidget, but a press hands
///     the file path to Picked rather than running anything — the AI tab wires that to its prompt
///     box.
/// </summary>
internal sealed class SkillsWidget : View
{
    private readonly View _list;

    public SkillsWidget (
        string? folder = null,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Folder = folder ?? UserSettings.SkillsPath;

        Title = "Skills";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _list = new ScrollableView { Width = Dim.Auto (), Height = Dim.Auto () };

        Add (_list);

        CanFocus = true;

        this.OnShown (Reload);
    }

    public string Folder { get; private set; }

    public Action<string>? Picked { get; set; }

    public void Show (string folder)
    {
        Folder = folder.Trim ();
        Reload ();
    }

    private void Reload ()
    {
        _list.RemoveAll ();

        string[] skills;

        try
        {
            skills = Directory.GetFiles (Folder, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            skills = [];
        }

        Title = $"Skills ({skills.Length})";

        if (skills.Length == 0)
        {
            _list.Add (new Label { Y = 0, Text = $"no .md files in {Folder}" });
            _list.SetContentSize (null);

            return;
        }

        for (int i = 0; i < skills.Length; i++)
        {
            string path = skills [i];

            Button button = new () { X = 0, Y = i, Text = Path.GetFileNameWithoutExtension (path) };
            button.Accepted += (_, _) => Picked?.Invoke (path);
            _list.Add (button);
        }

        _list.SetContentSize (
                              new (
                                   skills.Max (s => Path.GetFileNameWithoutExtension (s).Length) + 4,
                                   skills.Length));
    }
}

/// <summary>
///     A button per bookmark, from the "links" table in user-settings. Pressing one opens it in the
///     default browser.
/// </summary>
internal sealed class LinksWidget : View
{
    private readonly ScrollableView _list;

    public LinksWidget (Pos? x = null, Pos? y = null, Dim? width = null, Dim? height = null)
    {
        Title = "Links";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _list = new () { Width = Dim.Auto (), Height = Dim.Auto () };

        Add (_list);

        CanFocus = true;

        this.OnShown (Reload);
    }

    public void Reload ()
    {
        _list.RemoveAll ();

        Dictionary<string, string> links = UserSettings.GetTable (UserSettings.Links);

        Title = $"Links ({links.Count})";

        if (links.Count == 0)
        {
            _list.Add (new Label { Y = 0, Text = "no links - add them in Preferences" });
            _list.SetContentSize (null);
            this.Remeasure ();

            return;
        }

        int row = 0;

        foreach ((string name, string url) in links)
        {
            Button button = new () { X = 0, Y = row++, Text = name };

            button.Accepted += (_, _) =>
            {
                try
                {
                    Process.Start (new ProcessStartInfo (url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.ErrorQuery (App!, "Cannot open", $"{url}\n\n{ex.Message}", new [] { "_Ok" });
                }
            };

            _list.Add (button);
        }

        _list.SetContentSize (new (links.Keys.Max (n => n.Length) + 4, links.Count));

        this.Remeasure ();
    }
}

/// <summary>
///     Live output of whatever the Scripts tab is running, a line at a time, the way a console
///     shows it: the invocation, every line as it arrives, then the exit code.
/// </summary>
internal sealed class TerminalWidget : View
{
    private const int FlushMs = 150;

    private const int MaxChars = 200_000;

    private readonly Editor _editor;
    private readonly StringBuilder _pending = new ();
    private readonly object _gate = new ();

    private object? _flush;

    public TerminalWidget (Pos? x = null, Pos? y = null, Dim? width = null, Dim? height = null)
    {
        Title = "Terminal";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Fill ();
        Height = height ?? Dim.Fill ();

        _editor = new () { X = 0, Y = 0, Width = Dim.Fill (), Height = Dim.Fill (), ReadOnly = true, WordWrap = true, Multiline = true };

        Add (_editor);
    }

    public void Write (string line)
    {
        lock (_gate)
        {
            _pending.Append (line).Append (Environment.NewLine);
        }

        if (App is { } app)
        {
            app.Invoke (() => _flush ??= App?.AddTimeout (TimeSpan.FromMilliseconds (FlushMs), Drain));
        }
        else
        {
            Drain ();
        }
    }

    public void Clear ()
    {
        lock (_gate)
        {
            _pending.Clear ();
        }

        _editor.Text = "";
        _editor.CaretOffset = 0;
    }

    private bool Drain ()
    {
        string chunk;

        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                _flush = null;

                return false;
            }

            chunk = _pending.ToString ();
            _pending.Clear ();
        }

        bool follow = _editor.CaretOffset >= _editor.Document!.TextLength;

        if (_editor.Document!.TextLength + chunk.Length > MaxChars)
        {
            int keep = Math.Max (0, MaxChars / 2 - chunk.Length);
            string kept = _editor.Text;

            if (keep == 0 || kept.Length <= keep)
            {
                _editor.Text = keep == 0 ? "" : kept;
            }
            else
            {
                int cut = kept.IndexOf ('\n', kept.Length - keep);
                _editor.Text = cut >= 0 && cut + 1 < kept.Length ? kept [(cut + 1)..] : kept [^keep..];
            }
        }
        _editor.Text += chunk;

        if (follow)
        {
            _editor.CaretOffset = _editor.Document!.TextLength;
        }

        return true;
    }

    protected override void Dispose (bool disposing)
    {
        if (disposing && _flush is { } token)
        {
            App?.RemoveTimeout (token);
            _flush = null;
        }

        base.Dispose (disposing);
    }
}
/// <summary>One button per .ps1 script in folder, in a scrolling list.</summary>
internal sealed class ScriptsWidget : View
{
    private const int SpinnerWidth = 6;

    private readonly View _list;

    private readonly SpinnerView _spinner;

    public event Action<string>? Output;

    private int _running;

    /// <summary>One live run: its process, and whether Cancel has already been through it.</summary>
    private sealed class Run
    {
        public required Process Process { get; init; }

        public volatile bool Cancelled;
    }

    private readonly List<Run> _runs = new ();

    public ScriptsWidget (
        string? folder = null,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        Folder = folder ?? UserSettings.ScriptsPath;

        Title = "Scripts";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _spinner = new ()
        {
            X = 1,
            Y = 0,
            Width = SpinnerWidth,
            Height = 1,
            Style = new SpinnerStyle.BouncingBar (),
            Visible = false,

            TabStop = TabBehavior.NoStop,
            CanFocus = false
        };

        _list = new ScrollableView { Y = Pos.Bottom (_spinner), Width = Dim.Auto (), Height = Dim.Auto () };

        Add (_spinner, _list);

        this.OnShown (Reload);
    }

    public string Folder { get; private set; }

    public void Show (string folder)
    {
        Folder = folder.Trim ();
        Reload ();
    }

    private void Reload ()
    {
        _list.RemoveAll ();

        string[] scripts;

        try
        {
            scripts = Directory.GetFiles (Folder, "*.ps1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            scripts = [];
        }

        Title = $"Scripts ({scripts.Length})";

        if (scripts.Length == 0)
        {
            _list.Add (new Label { Y = 0, Text = $"no .ps1 files in {Folder}" });
            _list.SetContentSize (null);

            return;
        }

        for (int i = 0; i < scripts.Length; i++)
        {
            string path = scripts [i];

            Button button = new () { X = 0, Y = i, Text = Path.GetFileName (path) };
            button.Accepted += (_, _) => _ = RunAsync (path);
            _list.Add (button);
        }

        _list.SetContentSize (new (scripts.Max (s => Path.GetFileName (s).Length) + 4, scripts.Length));
    }

    private async Task RunAsync (string path)
    {
        Running (+1);

        string name = Path.GetFileNameWithoutExtension (path);

        Output?.Invoke ($"> {Path.GetFileName (path)}");

        Run? run = null;

        try
        {
            using Process process = Process.Start (new ProcessStartInfo (
                                                                         "powershell.exe",
                                                                         $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            run = new Run { Process = process };
            _runs.Add (run);

            void Line (string? data)
            {
                if (data is not null && !run.Cancelled)
                {
                    Output?.Invoke ($"[{name}] {data}");
                }
            }

            process.OutputDataReceived += (_, e) => Line (e.Data);
            process.ErrorDataReceived += (_, e) => Line (e.Data);
            process.BeginOutputReadLine ();
            process.BeginErrorReadLine ();

            await process.WaitForExitAsync ();

            if (!run.Cancelled)
            {
                Output?.Invoke ($"[{name}] exit {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            if (run is not { Cancelled: true })
            {
                Output?.Invoke ($"[{name}] could not run: {ex.Message}");
            }
        }

        void Finish ()
        {
            if (run is not null)
            {
                _runs.Remove (run);
            }

            Running (-1);
        }

        if (App is not { } app)
        {
            Finish ();

            return;
        }

        app.Invoke (Finish);
    }

    public void CancelAll ()
    {
        foreach (Run run in _runs)
        {
            run.Cancelled = true;

            try
            {
                run.Process.Kill (entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private void Running (int delta)
    {
        _running = Math.Max (0, _running + delta);

        bool running = _running > 0;

        _spinner.Visible = running;

        _spinner.AutoSpin = running;

        _spinner.SetNeedsDraw ();
        SetNeedsDraw ();
    }
}
/// <summary>
///     A chat box against any backend ChatProvider can build: pick a provider, a base URL and a
///     model, type, get a streamed reply.
/// </summary>
internal sealed class ChatWidget : View
{
    private const int FlushMs = 150;

    private const int SpinnerWidth = 6;

    private const int ResultEcho = 100;

    private const int KeptResults = 3;

    private const int ElideOver = 20_000;

    private const int MaxToolRounds = 10;

    private const int MaxOutputTokens = 16_000;

    private const int MaxAttempts = 2;

    private const int BackoffSeconds = 3;

    private readonly Editor _transcript;
    private readonly TextField _prompt;
    private readonly Button _send;

    private readonly List<ChatMessage> _history = new ();
    private readonly StringBuilder _pending = new ();
    private readonly object _gate = new ();

    private string? _key;

    private readonly SpinnerView _spinner;

    private ChatOptions? _options;

    private IChatClient? _client;
    private CancellationTokenSource? _cts;
    private object? _flush;
    private bool _busy;

    private readonly Dictionary<string, string> _skills = new (StringComparer.OrdinalIgnoreCase);

    private long? _lastInputTokens;

    public ChatWidget (
        string? provider = null,
        string? url = null,
        string? model = null,
        string? key = null,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        CanFocus = true;

        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _transcript = new ()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill (),

            Height = Dim.Fill (3),
            ReadOnly = true,
            WordWrap = true,
            Multiline = true
        };

        _prompt = new () { X = 0, Y = Pos.Bottom (_transcript), Width = Dim.Fill (), Height = 1 };

        _send = new () { Text = "_Send", X = 0, Y = Pos.Bottom (_prompt) };

        _spinner = new ()
        {
            X = Pos.Right (_send) + 1,
            Y = Pos.Top (_send),
            Width = SpinnerWidth,
            Height = 1,
            Style = new SpinnerStyle.BouncingBar (),
            Visible = false,

            TabStop = TabBehavior.NoStop,
            CanFocus = false
        };

        _send.Accepted += (_, _) => _ = SendAsync ();
        _prompt.Accepted += (_, _) => _ = SendAsync ();

        TabStop = TabBehavior.TabGroup;

        _transcript.TabStop = TabBehavior.NoStop;

        Add (_transcript, _prompt, _spinner, _send);

        Use (provider ?? UserSettings.Get (UserSettings.AiProvider),
             url ?? UserSettings.Get (UserSettings.AiUrl),
             model ?? UserSettings.Get (UserSettings.AiModel),
             key);
    }

    public string Provider { get; private set; } = "";

    public string Url { get; private set; } = "";

    public string Model { get; private set; } = "";

    public void AddPrompt (string text)
    {
        _prompt.Text = _prompt.Text.Length == 0 ? text : $"{_prompt.Text} {text}";
        _prompt.SetFocus ();
    }

    public void AddSkill (string path)
    {
        string name = Path.GetFileNameWithoutExtension (path);
        string text;

        try
        {
            text = File.ReadAllText (path);
        }
        catch (Exception ex)
        {
            Write ($"\n[skill] {name} could not be read: {ex.Message}\n");

            return;
        }

        _skills [path] = text;

        Write ($"\n[skill] {name} pinned ({text.Length:N0} characters, sent with every turn)\n");

        AddPrompt ($"Follow the {name} skill.");
    }

    private string SkillInstructions ()
    {
        if (_skills.Count == 0)
        {
            return "";
        }

        return Environment.NewLine
               + Environment.NewLine
               + string.Join (
                              Environment.NewLine + Environment.NewLine,
                              _skills.Select (
                                              s => $"--- skill: {Path.GetFileName (s.Key)} ---"
                                                   + Environment.NewLine
                                                   + s.Value));
    }

    public void Use (string provider, string url, string model, string? key = null)
    {
        Provider = provider.Trim ();
        Url = url.Trim ();
        Model = model.Trim ();

        _key = key?.Trim ();

        _history.Clear ();
        (_client as IDisposable)?.Dispose ();

        try
        {
            _client = ChatProvider.Create (Provider, Url, Model, _key)
                                  .AsBuilder ()
                                  .UseFunctionInvocation (
                                                          configure: c =>
                                                          {
                                                              c.IncludeDetailedErrors = true;

                                                              c.AllowConcurrentInvocation = false;

                                                              c.MaximumIterationsPerRequest = MaxToolRounds;
                                                          })

                                  .Use (next => new RetryingChatClient (next))
                                  .Build ();

            List<AITool> tools = [.. FileTools.Tools, .. MathTools.Tools, .. TranscriptTools.Tools];

            if (ChatProvider.HostsTools (Provider))
            {
                tools.Add (new HostedWebSearchTool ());
            }

            tools.AddRange (TerminalTools.Tools);

            _options = new () { Tools = tools, MaxOutputTokens = MaxOutputTokens };
            Title = $"{Provider} · {Model}";
            string source = _key is { Length: > 0 }
                                ? "key: typed"
                                : Env.Get (ChatProvider.KeyName (Provider) ?? "") is { Length: > 0 }
                                    ? $"key: {ChatProvider.KeyName (Provider)}"
                                    : "key: none";

            Write ($"Ready — {Provider} · {Model}{(Url.Length > 0 ? $" · {Url}" : "")} [{source}]\n\n");
        }
        catch (Exception ex)
        {
            _client = null;
            Title = $"{Provider} · unavailable";
            Write ($"[unavailable] {ex.Message}\n\n");
        }
    }

    private async Task SettleAsync ()
    {
        CloseOpenToolCalls ();

        int tokens = ContextPolicy.Estimate (_history, _lastInputTokens);

        if (tokens <= ContextPolicy.CompactTokens)
        {
            return;
        }

        if (_client is { } client)
        {
            Write ($"\n[compacting the earlier conversation -- {tokens:N0} tokens]\n");

            try
            {
                string? summary = await ContextPolicy.CompactAsync (_history, client, _cts?.Token ?? default);

                Write (summary is null
                           ? "[nothing safe to compact yet]\n"
                           : $"[compacted to {ContextPolicy.Estimate (_history, null):N0} tokens]\n");
            }
            catch (Exception ex)
            {
                Write ($"[compaction failed: {ChatProvider.Explain (ex)}]\n");
            }
        }

        tokens = ContextPolicy.Estimate (_history, null);

        if (tokens > ContextPolicy.TrimTokens)
        {
            TrimHistory ();
            Write ($"[dropped the bodies of stale tool results -- {ContextPolicy.Estimate (_history, null):N0} tokens]\n");
        }

        _lastInputTokens = null;
    }

    private void CloseOpenToolCalls ()
    {
        HashSet<string> answered = new (
                                        _history.SelectMany (m => m.Contents)
                                                .OfType<FunctionResultContent> ()
                                                .Select (r => r.CallId));

        for (int m = _history.Count - 1; m >= 0; m--)
        {
            List<AIContent> answers = _history [m]
                                      .Contents
                                      .OfType<FunctionCallContent> ()
                                      .Where (call => !answered.Contains (call.CallId))
                                      .Select (
                                               call => (AIContent) new FunctionResultContent (
                                                        call.CallId,
                                                        "[not run: the turn ended before this "
                                                        + "call completed. Call it again if you "
                                                        + "still need it.]"))
                                      .ToList ();

            if (answers.Count == 0)
            {
                continue;
            }

            _history.Insert (m + 1, new ChatMessage (ChatRole.Tool, answers));
        }
    }

    private void TrimHistory ()
    {

        StripReasoning ();

        int seen = 0;

        for (int m = _history.Count - 1; m >= 0; m--)
        {
            IList<AIContent> contents = _history [m].Contents;

            for (int c = contents.Count - 1; c >= 0; c--)
            {
                if (contents [c] is not FunctionResultContent result)
                {
                    continue;
                }

                string text = result.Result?.ToString () ?? "";

                if (++seen <= KeptResults || text.Length <= ElideOver)
                {
                    continue;
                }

                contents [c] = new FunctionResultContent (
                                                          result.CallId,
                                                          $"[{text.Length:N0} characters, dropped from the "
                                                          + "transcript to save room. Call the tool again if "
                                                          + "you still need this.]");
            }
        }

    }

    private bool StripReasoning ()
    {
        bool removed = false;

        foreach (ChatMessage message in _history)
        {
            if (message.Contents.Any (c => c is FunctionCallContent))
            {
                continue;
            }

            for (int c = message.Contents.Count - 1; c >= 0; c--)
            {
                if (message.Contents [c] is TextReasoningContent)
                {
                    message.Contents.RemoveAt (c);
                    removed = true;
                }
            }
        }

        _history.RemoveAll (m => m.Contents.Count == 0);

        return removed;
    }

    private void DropToolExchanges ()
    {
        _history.RemoveAll (
                            m => m.Contents.Any (
                                                 c => c is FunctionCallContent
                                                      or FunctionResultContent
                                                      or ToolApprovalRequestContent
                                                      or ToolApprovalResponseContent));

        StripReasoning ();
    }

    private static bool IsReasoningRejection (Exception ex) =>
        ex.Message.Contains ("thinking", StringComparison.OrdinalIgnoreCase)
        && ex.Message.Contains ("cannot be modified", StringComparison.OrdinalIgnoreCase);

    private async Task SendAsync ()
    {
        string prompt = _prompt.Text.Trim ();

        if (_busy || _client is null || prompt.Length == 0)
        {
            return;
        }

        _busy = true;
        _send.Enabled = false;

        _spinner.Visible = true;
        _spinner.AutoSpin = true;

        _spinner.SetNeedsDraw ();
        _prompt.Text = "";
        Write ($"--- you ---\n{prompt}\n\n--- {Model} ---\n");

        int mark = _history.Count;

        CloseOpenToolCalls ();

        List<ChatResponseUpdate> updates = new ();
        _cts = new ();
        _flush = App?.AddTimeout (TimeSpan.FromMilliseconds (FlushMs), Drain);

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                _history.Add (new (ChatRole.User, prompt));
                updates.Clear ();

                try
                {
                    _options!.Instructions = FileTools.Instructions ()
                                             + Environment.NewLine
                                             + Environment.NewLine
                                             + TerminalTools.Instructions ()
                                             + SkillInstructions ();

                    ContextPolicy.ApplyCaching (_history);

                    await foreach (ChatResponseUpdate update in _client.GetStreamingResponseAsync (_history, _options, _cts.Token))
                    {
                        updates.Add (update);

                        foreach (AIContent content in update.Contents)
                        {
                            if (content is FunctionCallContent call)
                            {
                                string args = string.Join (", ", call.Arguments?.Select (a => $"{a.Key}={a.Value}") ?? []);

                                Write ($"\n[tool] {call.Name} ({args})\n");
                            }
                            else if (content is FunctionResultContent result)
                            {
                                string text = (result.Result?.ToString () ?? "").ReplaceLineEndings (" ");

                                Write ($"[    ] {(text.Length > ResultEcho ? text [..ResultEcho] + "..." : text)}\n");
                            }
                        }

                        Write (update.Text);
                    }

                    ChatResponse response = updates.ToChatResponse ();

                    _history.AddRange (response.Messages);

                    while (await AnswerApprovals (response))
                    {
                        updates.Clear ();

                        await foreach (ChatResponseUpdate update in _client.GetStreamingResponseAsync (_history, _options, _cts.Token))
                        {
                            updates.Add (update);
                            Echo (update);
                            Write (update.Text);
                        }

                        response = updates.ToChatResponse ();
                        _history.AddRange (response.Messages);
                    }

                    _lastInputTokens = response.Usage?.InputTokenCount ?? _lastInputTokens;
                    await SettleAsync ();

                    break;
                }
                catch (Exception ex)
                {
                    _history.RemoveRange (mark, _history.Count - mark);

                    if (attempt == 1 && IsReasoningRejection (ex))
                    {
                        if (StripReasoning ())
                        {
                            Write ($"\n[reasoning blocks rejected -- retrying without them]\n");
                        }
                        else
                        {
                            DropToolExchanges ();
                            Write ($"\n[reasoning blocks rejected -- retrying without the earlier tool calls]\n");
                        }

                        continue;
                    }

                    if (attempt < MaxAttempts && ChatProvider.IsTransient (ex))
                    {
                        int wait = attempt * BackoffSeconds;

                        Write ($"\n[{ChatProvider.Explain (ex)} -- retrying in {wait}s]\n");
                        await Task.Delay (TimeSpan.FromSeconds (wait));

                        if (_cts?.IsCancellationRequested != true)
                        {
                            continue;
                        }
                    }

                    Write ($"\n[error] {ChatProvider.Explain (ex)}\n");

                    break;
                }
            }
        }
        finally
        {
            if (_flush is { } token)
            {
                App?.RemoveTimeout (token);
                _flush = null;
            }

            Drain ();
            _cts?.Dispose ();
            _cts = null;
            _busy = false;
            _send.Enabled = true;

            _spinner.AutoSpin = false;
            _spinner.Visible = false;
            Write ("\n");
        }
    }

    private void Echo (ChatResponseUpdate update)
    {
        foreach (AIContent content in update.Contents)
        {
            if (content is FunctionCallContent call)
            {
                string args = string.Join (", ", call.Arguments?.Select (a => $"{a.Key}={a.Value}") ?? []);

                Write ($"{Environment.NewLine}[tool] {call.Name} ({args}){Environment.NewLine}");
            }
            else if (content is FunctionResultContent result)
            {
                string text = (result.Result?.ToString () ?? "").ReplaceLineEndings (" ");

                Write ($"[    ] {(text.Length > ResultEcho ? text [..ResultEcho] + "..." : text)}{Environment.NewLine}");
            }
        }
    }

    private async Task<bool> AnswerApprovals (ChatResponse response)
    {
        List<ToolApprovalRequestContent> asks = response.Messages
                                                        .SelectMany (m => m.Contents)
                                                        .OfType<ToolApprovalRequestContent> ()
                                                        .ToList ();

        if (asks.Count == 0)
        {
            return false;
        }

        List<AIContent> answers = new ();

        foreach (ToolApprovalRequestContent ask in asks)
        {
            string detail = string.Join (
                                         Environment.NewLine,
                                         (ask.ToolCall as FunctionCallContent)?.Arguments?
                                         .Select (a => $"{a.Key}: {a.Value}")
                                         ?? ["(no arguments)"]);

            string name = (ask.ToolCall as FunctionCallContent)?.Name ?? ask.ToolCall.CallId;

            bool approved = await Ask ($"Run {name}?", detail);

            Write ($"{Environment.NewLine}[{(approved ? "approved" : "denied")}] {name}{Environment.NewLine}");
            answers.Add (new ToolApprovalResponseContent (ask.RequestId, approved, ask.ToolCall));
        }

        _history.Add (new ChatMessage (ChatRole.User, answers));

        return true;
    }

    private Task<bool> Ask (string title, string detail)
    {
        TaskCompletionSource<bool> answer = new ();

        if (App is not { } app)
        {
            answer.TrySetResult (false);

            return answer.Task;
        }

        app.Invoke (() => answer.TrySetResult (MessageBox.Query (app, title, detail, "_Run", "_Deny") == 0));

        return answer.Task;
    }

    private void Write (string text)
    {
        lock (_gate)
        {
            _pending.Append (text);
        }

        if (_flush is null)
        {
            if (App is { } app) { app.Invoke (() => Drain ()); } else { Drain (); }
        }
    }

    private bool Drain ()
    {
        string chunk;

        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                return true;
            }

            chunk = _pending.ToString ();
            _pending.Clear ();
        }

        bool follow = _transcript.CaretOffset >= _transcript.Document!.TextLength;

        _transcript.Text += chunk;

        if (follow)
        {
            _transcript.CaretOffset = _transcript.Document!.TextLength;
        }

        return true;
    }
}

/// <summary>One push, however the host reports it.</summary>
internal sealed record GitPush (DateTime When, string Repo, string Branch, int Commits, string Message);

/// <summary>
///     A year of contributions drawn the way the GitHub profile page draws it: one small block per
///     day, weeks as columns, weekdays as rows, colour by intensity.
/// </summary>
internal sealed class GitContributionsWidget : View
{
    public const int Weeks = 53;

    private const int Gutter = 4;
    private const int Header = 1;
    private const int Footer = 1;

    private static readonly (string Colour, char Glyph) [] Levels =
    {
        ("#484f58", '\u2591'),
        ("#0e4429", '\u2591'),
        ("#006d32", '\u2592'),
        ("#26a641", '\u2593'),
        ("#39d353", '\u2588')
    };

    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (20) };

    private readonly Dictionary<DateOnly, int> _counts = new ();

    private DateOnly _start;
    private int _total;
    private int _busiest = 1;
    private string _status = "loading...";
    private string _provider = UserSettings.GitHub;

    public GitContributionsWidget (Pos? x = null, Pos? y = null, Dim? width = null, Dim? height = null)
    {
        Title = "Contributions";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);

        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        Add (new Grid (this));

        this.OnShown (() => _ = LoadAsync ());
    }

    private async Task LoadAsync ()
    {
        _provider = UserSettings.Get (UserSettings.GitProvider);

        bool codeberg = _provider == UserSettings.Codeberg;
        string userKey = codeberg ? "CODEBERG_USER" : "GITHUB_USER";
        string tokenKey = codeberg ? "CODEBERG_TOKEN" : "GITHUB_TOKEN";

        Dictionary<DateOnly, int> counts = new ();
        string? error = null;

        try
        {
            string user = Env.Require (userKey);

            counts = codeberg
                         ? await CodebergAsync (user, Env.Get (tokenKey))
                         : await GitHubAsync (user, Env.Get (tokenKey));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        App?.Invoke (() =>
        {
            _counts.Clear ();

            foreach ((DateOnly day, int count) in counts)
            {
                _counts [day] = count;
            }

            DateOnly today = DateOnly.FromDateTime (DateTime.Now);
            DateOnly earliest = today.AddDays (-((Weeks - 1) * 7));
            _start = earliest.AddDays (-(int) earliest.DayOfWeek);

            _total = _counts.Where (c => c.Key >= _start).Sum (c => c.Value);
            _busiest = Math.Max (1, _counts.Count == 0 ? 1 : _counts.Values.Max ());

            Title = $"Contributions - {_total} on {_provider}";
            _status = error ?? $"{_counts.Count} active days";

            SetNeedsDraw ();
        });
    }

    private async Task<Dictionary<DateOnly, int>> CodebergAsync (string user, string? token)
    {
        JsonElement body = await GetAsync ($"https://codeberg.org/api/v1/users/{user}/heatmap", token);
        Dictionary<DateOnly, int> counts = new ();

        foreach (JsonElement entry in body.EnumerateArray ())
        {
            if (!entry.TryGetProperty ("timestamp", out JsonElement stamp))
            {
                continue;
            }

            DateOnly day = DateOnly.FromDateTime (DateTimeOffset.FromUnixTimeSeconds (stamp.GetInt64 ()).LocalDateTime);
            int count = entry.TryGetProperty ("contributions", out JsonElement c) ? c.GetInt32 () : 0;

            counts [day] = counts.GetValueOrDefault (day) + count;
        }

        return counts;
    }

    private async Task<Dictionary<DateOnly, int>> GitHubAsync (string user, string? token)
    {
        if (string.IsNullOrEmpty (token))
        {
            throw new InvalidOperationException (
                                                 "GitHub needs GITHUB_TOKEN (scope read:user) - its contribution "
                                                 + "calendar is GraphQL only and rejects anonymous requests");
        }

        const string query =
            "query($login:String!){user(login:$login){contributionsCollection{contributionCalendar{"
            + "totalContributions weeks{contributionDays{date contributionCount}}}}}}";

        string cacheKey = $"https://api.github.com/graphql#contributions:{user}";
        string identity = ApiCache.Identity (_provider, user, token);
        string? raw = ApiCache.Read (cacheKey, identity);

        if (raw is null)
        {
            using HttpRequestMessage request = new (HttpMethod.Post, "https://api.github.com/graphql");

            request.Headers.Add ("User-Agent", "ttm");
            request.Headers.Add ("Authorization", $"Bearer {token}");
            request.Content = JsonContent.Create (new { query, variables = new { login = user } });

            using HttpResponseMessage response = await Http.SendAsync (request);

            raw = await response.Content.ReadAsStringAsync ();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException (
                                                Wire.Str (JsonSerializer.Deserialize<JsonElement> (raw), "message")
                                                ?? response.StatusCode.ToString ());
            }

            ApiCache.Write (cacheKey, identity, raw);
        }

        JsonElement body = JsonSerializer.Deserialize<JsonElement> (raw);

        if (body.TryGetProperty ("errors", out JsonElement errors) && errors.GetArrayLength () > 0)
        {
            throw new HttpRequestException (Wire.Str (errors [0], "message") ?? "GraphQL error");
        }

        Dictionary<DateOnly, int> counts = new ();

        JsonElement calendar = body.GetProperty ("data")
                                   .GetProperty ("user")
                                   .GetProperty ("contributionsCollection")
                                   .GetProperty ("contributionCalendar");

        foreach (JsonElement week in calendar.GetProperty ("weeks").EnumerateArray ())
        {
            foreach (JsonElement day in week.GetProperty ("contributionDays").EnumerateArray ())
            {
                if (DateOnly.TryParse (Wire.Str (day, "date"), out DateOnly date))
                {
                    counts [date] = day.GetProperty ("contributionCount").GetInt32 ();
                }
            }
        }

        return counts;
    }

    private async Task<JsonElement> GetAsync (string url, string? token)
    {
        string identity = ApiCache.Identity (_provider, url, token);

        if (ApiCache.Read (url, identity) is { } cached)
        {
            return JsonSerializer.Deserialize<JsonElement> (cached);
        }

        using HttpRequestMessage request = new (HttpMethod.Get, url);

        request.Headers.Add ("User-Agent", "ttm");
        request.Headers.Add ("Accept", "application/json");

        if (!string.IsNullOrEmpty (token))
        {
            request.Headers.Add ("Authorization", $"token {token}");
        }

        using HttpResponseMessage response = await Http.SendAsync (request);

        string raw = await response.Content.ReadAsStringAsync ();
        JsonElement body = JsonSerializer.Deserialize<JsonElement> (raw);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException (Wire.Str (body, "message") ?? response.StatusCode.ToString ());
        }

        ApiCache.Write (url, identity, raw);

        return body;
    }

    private int LevelOf (int count) =>
        count <= 0 ? 0 : Math.Clamp (1 + (int) (3.0 * (count - 1) / Math.Max (1, _busiest - 1)), 1, 4);

    private Terminal.Gui.Drawing.Attribute Ink (int level) =>
        new (new Terminal.Gui.Drawing.Color (Levels [level].Colour), GetScheme ().Normal.Background);

    /// <summary>The grid itself, as a SubView.</summary>
    private sealed class Grid : View
    {
        private readonly GitContributionsWidget _owner;

        public Grid (GitContributionsWidget owner)
        {
            _owner = owner;
            Width = Dim.Auto ();
            Height = Dim.Auto ();

            SetContentSize (new (Gutter + Weeks, Header + 7 + Footer));
        }

        protected override bool OnDrawingContent (Terminal.Gui.ViewBase.DrawContext? context) => _owner.DrawGrid (this);
    }

    private bool DrawGrid (View g)
    {
        Terminal.Gui.Drawing.Attribute normal = GetScheme ().Normal;

        g.SetAttribute (normal);

        int lastMonth = -1;

        for (int week = 0; week < Weeks; week++)
        {
            DateOnly first = _start.AddDays (week * 7);

            if (first.Month != lastMonth && first.Day <= 7 && week < Weeks - 1)
            {
                g.Move (Gutter + week, 0);
                g.AddStr (first.ToString ("MMM"));
                lastMonth = first.Month;
            }
        }

        g.Move (0, Header + 1);
        g.AddStr ("Mon");
        g.Move (0, Header + 3);
        g.AddStr ("Wed");
        g.Move (0, Header + 5);
        g.AddStr ("Fri");

        DateOnly today = DateOnly.FromDateTime (DateTime.Now);

        for (int week = 0; week < Weeks; week++)
        {
            for (int day = 0; day < 7; day++)
            {
                DateOnly date = _start.AddDays (week * 7 + day);

                if (date > today)
                {
                    continue;
                }

                int level = LevelOf (_counts.GetValueOrDefault (date));

                g.SetAttribute (Ink (level));
                g.Move (Gutter + week, Header + day);
                g.AddRune ((System.Text.Rune) Levels [level].Glyph);
            }
        }

        int legendRow = Header + 7;

        g.SetAttribute (normal);
        g.Move (0, legendRow);
        g.AddStr ($"{_status}   Less ");

        for (int level = 0; level < Levels.Length; level++)
        {
            g.SetAttribute (Ink (level));
            g.AddRune ((System.Text.Rune) Levels [level].Glyph);
        }

        g.SetAttribute (normal);
        g.AddStr (" More");

        return true;
    }
}

/// <summary>
///     The most recent pushes you have made, newest first, from GitHub or Codeberg — whichever the
///     git-provider user setting names (Settings &gt; Preferences).
/// </summary>
internal sealed class GitHistoryWidget : View
{
    public const int DefaultAmount = 10;

    private const int MessageWidth = 60;

    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (15) };

    private readonly TableView _table;
    private readonly int _amount;

    private string _provider = UserSettings.GitHub;
    private string _userKey = "GITHUB_USER";
    private string _tokenKey = "GITHUB_TOKEN";

    public GitHistoryWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        _amount = Math.Max (1, amount);

        Title = "Git history";
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

        Add (_table);

        this.OnShown (() => _ = LoadAsync ());
    }

    private async Task LoadAsync ()
    {
        List<GitPush> pushes = new ();
        string? error = null;

        _provider = UserSettings.Get (UserSettings.GitProvider);

        bool codeberg = _provider == UserSettings.Codeberg;
        _userKey = codeberg ? "CODEBERG_USER" : "GITHUB_USER";
        _tokenKey = codeberg ? "CODEBERG_TOKEN" : "GITHUB_TOKEN";

        try
        {
            string user = Env.Require (_userKey);

            pushes = codeberg ? await CodebergAsync (user) : await GitHubAsync (user);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is null && pushes.Count == 0)
        {
            error = Env.Get (_tokenKey) is { Length: > 0 }
                        ? $"no recent pushes for {Env.Get (_userKey)} on {_provider}"
                        : $"no PUBLIC pushes for {Env.Get (_userKey)} on {_provider} - set {_tokenKey} to see private ones";
        }

        App?.Invoke (() =>
        {
            Title = error is null ? $"Git history · {_provider} ({pushes.Count})" : $"Git history · {_provider}";

            List<GitPush> rows = error is null ? pushes : [new (DateTime.Now, "", "", 0, error)];

            Dictionary<string, Func<GitPush, object>> columns = new ()
            {
                ["When"] = p => p.When.ToString ("MM-dd"),
                ["Repo"] = p => p.Repo,
                ["Branch"] = p => p.Branch,
                ["Message"] = p => p.Message
            };

            _table.SetSource (new EnumerableTableSource<GitPush> (rows, columns));

            int wide = columns.Count + 1;
            int index = 0;

            foreach ((string header, Func<GitPush, object> cell) in columns)
            {
                int longest = rows.Count == 0 ? 0 : rows.Max (r => cell (r).ToString ()!.Length);

                wide += Math.Min (Math.Max (header.Length, longest), _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
            }

            _table.Width = wide;

            _table.Height = rows.Count + 3;
            _table.Refresh ();
            this.Remeasure ();
            this.ResizeLayout ();
        });
    }

    private async Task<List<GitPush>> GitHubAsync (string user)
    {
        JsonElement events = await GetAsync ($"https://api.github.com/users/{user}/events?per_page=100");

        List<(DateTime When, string Repo, string Branch, string Range)> found = new ();

        foreach (JsonElement e in events.EnumerateArray ())
        {
            if (e.GetProperty ("type").GetString () != "PushEvent")
            {
                continue;
            }

            JsonElement payload = e.GetProperty ("payload");

            found.Add ((
                        DateTimeOffset.Parse (e.GetProperty ("created_at").GetString () ?? "").LocalDateTime,
                        e.GetProperty ("repo").GetProperty ("name").GetString () ?? "",
                        (payload.GetProperty ("ref").GetString () ?? "").Replace ("refs/heads/", ""),
                        $"{payload.GetProperty ("before").GetString ()}...{payload.GetProperty ("head").GetString ()}"));

            if (found.Count >= _amount)
            {
                break;
            }
        }

        (int Commits, string Message) [] details = await Task.WhenAll (found.Select (CommitsAsync));

        return found.Zip (details, (f, d) => new GitPush (f.When, f.Repo, f.Branch, d.Commits, d.Message)).ToList ();
    }

    private async Task<List<GitPush>> CodebergAsync (string user)
    {
        JsonElement feed = await GetAsync (
                                           $"https://codeberg.org/api/v1/users/{user}/activities/feeds?only-performed-by=true&limit=50");

        List<GitPush> pushes = new ();

        foreach (JsonElement a in feed.EnumerateArray ())
        {
            if (a.GetProperty ("op_type").GetString () != "commit_repo")
            {
                continue;
            }

            int commits = 0;
            string message = "";

            try
            {
                JsonElement content = JsonSerializer.Deserialize<JsonElement> (a.GetProperty ("content").GetString () ?? "{}");
                commits = content.TryGetProperty ("Len", out JsonElement len) ? len.GetInt32 () : 0;

                if (content.TryGetProperty ("Commits", out JsonElement list) && list.GetArrayLength () > 0)
                {
                    message = list [list.GetArrayLength () - 1].GetProperty ("Message").GetString () ?? "";
                }
            }
            catch (JsonException)
            {
            }

            pushes.Add (new (
                             DateTimeOffset.Parse (a.GetProperty ("created").GetString () ?? "").LocalDateTime,
                             a.GetProperty ("repo").GetProperty ("full_name").GetString () ?? "",
                             (a.GetProperty ("ref_name").GetString () ?? "").Replace ("refs/heads/", ""),
                             commits,
                             Subject (message)));

            if (pushes.Count >= _amount)
            {
                break;
            }
        }

        return pushes;
    }

    private async Task<(int Commits, string Message)> CommitsAsync (
        (DateTime When, string Repo, string Branch, string Range) push)
    {
        try
        {
            JsonElement compare = await GetAsync ($"https://api.github.com/repos/{push.Repo}/compare/{push.Range}");
            JsonElement commits = compare.GetProperty ("commits");
            int last = commits.GetArrayLength () - 1;

            return (compare.GetProperty ("total_commits").GetInt32 (),
                    Subject (
                             last < 0
                                 ? ""
                                 : commits [last].GetProperty ("commit").GetProperty ("message").GetString () ?? ""));
        }
        catch (Exception ex) when (ex is HttpRequestException or KeyNotFoundException or InvalidOperationException)
        {
            return (0, "");
        }
    }

    private async Task<JsonElement> GetAsync (string url)
    {
        string identity = ApiCache.Identity (_provider, Env.Get (_userKey), Env.Get (_tokenKey));

        if (ApiCache.Read (url, identity) is { } cached)
        {
            return JsonSerializer.Deserialize<JsonElement> (cached);
        }

        using HttpRequestMessage request = new (HttpMethod.Get, url);

        request.Headers.Add ("User-Agent", "ttm");
        request.Headers.Add ("Accept", "application/json");

        if (Env.Get (_tokenKey) is { Length: > 0 } token)
        {
            request.Headers.Add (
                                 "Authorization",
                                 _provider == UserSettings.Codeberg ? $"token {token}" : $"Bearer {token}");
        }

        using HttpResponseMessage response = await Http.SendAsync (request);

        string raw = await response.Content.ReadAsStringAsync ();
        JsonElement body = JsonSerializer.Deserialize<JsonElement> (raw);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException (
                                            body.ValueKind == JsonValueKind.Object
                                            && body.TryGetProperty ("message", out JsonElement m)
                                                ? m.GetString ()
                                                : response.StatusCode.ToString ());
        }

        ApiCache.Write (url, identity, raw);

        return body;
    }

    private static string Subject (string message)
    {
        string first = message.Split ('\n') [0];

        return first.Length > MessageWidth ? first [..(MessageWidth - 3)] + "..." : first;
    }
}

/// <summary>One output file: a script's report, or an AI evaluation.</summary>
internal sealed record FileRun (string Name, DateTime When, string Path);

/// <summary>
///     The most recent files in a folder, newest first. Enter or a double-click on a row opens that
///     file in a read-only editor.
/// </summary>
internal class FileHistoryWidget : View
{
    public const int DefaultAmount = 5;

    private static readonly string [] MarkdownExtensions = { ".md", ".markdown" };

    private const int MaxRenderedCharacters = 1_000_000;

    private readonly TableView _table;
    private readonly string _folder;
    private readonly string _name;
    private readonly int _amount;

    private List<FileRun> _runs = new ();

    protected FileHistoryWidget (
        string folder,
        string name,
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        _folder = folder;
        _name = name;
        _amount = Math.Max (1, amount);

        Title = name;
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

        _table.Accepted += (_, _) => Open ();

        Add (_table);

        this.OnShown (Reload);
    }

    public IReadOnlyList<FileRun> Runs => _runs;

    public string Folder => _folder;

    public virtual void Reload ()
    {
        try
        {
            _runs = new DirectoryInfo (_folder)
                    .EnumerateFiles ()
                    .OrderByDescending (f => f.LastWriteTime)
                    .Take (_amount)
                    .Select (f => new FileRun (f.Name, f.LastWriteTime, f.FullName))
                    .ToList ();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _runs = new ();
        }

        Title = $"{_name} ({_runs.Count})";

        List<FileRun> rows = _runs.Count > 0 ? _runs : [new ("nothing here yet", DateTime.Now, "")];

        Dictionary<string, Func<FileRun, object>> columns = new ()
        {
            ["File"] = r => r.Name,
            ["When"] = r => r.When.ToString ("MM-dd")
        };

        _table.SetSource (new EnumerableTableSource<FileRun> (rows, columns));

        int wide = columns.Count + 1;
        int index = 0;

        foreach ((string header, Func<FileRun, object> cell) in columns)
        {
            int longest = rows.Max (r => cell (r).ToString ()!.Length);

            wide += Math.Min (Math.Max (header.Length, longest), _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        _table.Width = wide;

        _table.Height = rows.Count + 3;
        _table.Refresh ();
    }

    private void Open ()
    {
        int row = _table.Value?.SelectedCell.Y ?? -1;

        if (row < 0 || row >= _runs.Count)
        {
            return;
        }

        FileRun run = _runs [row];
        string text;

        try
        {
            text = File.ReadAllText (run.Path);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery (App!, "Cannot open", $"{run.Path}\n\n{ex.Message}", new [] { "_Ok" });

            return;
        }

        Dialog dialog = new () { Title = run.Name, Width = Dim.Percent (80), Height = Dim.Percent (80) };
        Button close = new () { Text = "_Close" };

        close.Accepted += (_, _) => App!.RequestStop (dialog);

        dialog.Add (Rendered (run.Path, text) ?? Highlighted (run.Path, text));

        dialog.AddButton (close);

        App!.Run (dialog);
        dialog.Dispose ();
    }
    private static View? Rendered (string path, string text)
    {
        if (text.Length > MaxRenderedCharacters)
        {
            return null;
        }

        return MarkdownExtensions.Contains (Path.GetExtension (path), StringComparer.OrdinalIgnoreCase)
                   ? new ReadableMarkdown
                     {
                         Width = Dim.Fill (),
                         Height = Dim.Fill (1),

                         ShowHeadingPrefix = false,
                         Text = text
                     }
                   : null;
    }

    private static View Highlighted (string path, string text) =>
        new Editor
        {
            Width = Dim.Fill (),
            Height = Dim.Fill (1),
            ReadOnly = true,
            GutterOptions = GutterOptions.LineNumbers,

            HighlightingDefinition = Terminal.Gui.Editor.Highlighting.HighlightingManager
                                                                     .Instance
                                                                     .GetDefinitionByExtension (
                                                                      Path.GetExtension (path)),
            Text = text
        };
}

/// <summary>Recent script outputs, from the reports folder. Used on the Scripts tab.</summary>
internal sealed class ScriptHistoryWidget : FileHistoryWidget
{
    public ScriptHistoryWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
        : base (UserSettings.ReportsPath, "Script history", amount, x, y, width, height)
    {
    }
}

/// <summary>Recent AI evaluations, from the reviews folder. Used on the AI tab.</summary>
internal sealed class ReviewHistoryWidget : FileHistoryWidget
{
    public ReviewHistoryWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
        : base (UserSettings.ReviewsPath, "Review history", amount, x, y, width, height)
    {
    }
}

/// <summary>Project documents, from the projects folder. Used on the Dash tab.</summary>
internal sealed class ProjectsWidget : FileHistoryWidget
{
    public ProjectsWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
        : base (UserSettings.ProjectsPath, "Projects", amount, x, y, width, height)
    {
    }
}

/// <summary>
///     The last few files in the current user's own reports folder — Enter or a double-click opens
///     one, rendered as a formatted document when it is a .md.
/// </summary>
internal sealed class ReportsWidget : FileHistoryWidget
{
    public static string FolderPath =>
        Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.UserProfile), "reports");

    public ReportsWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
        : base (FolderPath, "Reports", amount, x, y, width, height)
    {
        Visible = Directory.Exists (Folder);
    }

    public override void Reload ()
    {
        Visible = Directory.Exists (Folder);

        if (!Visible)
        {
            return;
        }

        base.Reload ();
    }
}

/// <summary>One row of the notes table, as the widget lists it.</summary>
internal sealed record NoteRow (string Title, DateTime Modified, string Path);

/// <summary>
///     The most recently modified documents, straight from the notes table. Enter or a double-click
///     on a row opens that file in the Notes tab editor.
/// </summary>
internal sealed class NotesWidget : View
{
    public const int DefaultAmount = 5;

    private readonly TableView _table;
    private readonly int _amount;

    private List<NoteRow> _rows = new ();

    public NotesWidget (
        int amount = DefaultAmount,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        _amount = Math.Max (1, amount);

        Title = "Notes";
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

        _table.Accepted += (_, _) => Open ();

        Add (_table);

        this.OnShown (Reload);
    }

    public IReadOnlyList<NoteRow> Rows => _rows;

    public void Reload ()
    {
        _rows = new ();

        if (Engine.Ready)
        {
            try
            {
                using SqliteConnection connection = Engine.Connect ();
                using SqliteCommand read = connection.CreateCommand ();

                read.CommandText = $"SELECT filepath, title, modified FROM {Constants.NotesTable} "
                                   + "WHERE deleted = 0 ORDER BY modified DESC LIMIT $amount;";
                read.Parameters.AddWithValue ("$amount", _amount);

                using SqliteDataReader reader = read.ExecuteReader ();

                while (reader.Read ())
                {
                    DateTime.TryParse (reader.GetString (2), out DateTime modified);

                    _rows.Add (new (reader.GetString (1), modified, reader.GetString (0)));
                }
            }
            catch (SqliteException)
            {
                _rows = new ();
            }
        }

        Title = $"Notes ({_rows.Count})";

        List<NoteRow> rows = _rows.Count > 0 ? _rows : [new ("nothing saved yet", DateTime.Now, "")];

        Dictionary<string, Func<NoteRow, object>> columns = new ()
        {
            ["Document"] = r => r.Title,
            ["When"] = r => r.Modified.ToString ("MM-dd")
        };

        _table.SetSource (new EnumerableTableSource<NoteRow> (rows, columns));

        int wide = columns.Count + 1;
        int index = 0;

        foreach ((string header, Func<NoteRow, object> cell) in columns)
        {
            int longest = rows.Max (r => cell (r).ToString ()!.Length);

            wide += Math.Min (Math.Max (header.Length, longest), _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        _table.Width = wide;

        _table.Height = rows.Count + 3;
        _table.Refresh ();
    }

    private void Open ()
    {
        int row = _table.Value?.SelectedCell.Y ?? -1;

        if (row < 0 || row >= _rows.Count)
        {
            return;
        }

        Mode.Notes.OpenInEditor?.Invoke (_rows [row].Path);
    }
}

/// <summary>
///     Terminal.Gui's Markdown view, scrolling at the app's wheel step instead of the one line its
///     own wheel binding moves.
/// </summary>
internal sealed class ReadableMarkdown : Markdown
{
    protected override bool OnMouseEvent (Mouse mouse) => WheelScrolling.Handle (this, mouse) || base.OnMouseEvent (mouse);
}

