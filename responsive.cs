// -----------------------------------------------------------------------------
//  Responsive layout core: size tiers, per-view responsive tags and the scaling hook.
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using Terminal.Gui.Input;

/// <summary>Width tiers, smallest first, so tiers can be compared with <c>&lt;</c> and <c>&gt;</c>.</summary>
internal enum SizeClass { Tiny, Compact, Medium, Wide }

internal static class Breakpoints
{
    // Default column thresholds: a tier applies below its number, Wide is everything above.
    public const int DefaultTiny = 60;
    public const int DefaultCompact = 80;
    public const int DefaultMedium = 140;

    // How far past a threshold the width must move before the tier changes.
    // Stops flicker while a drag-resize hovers on a breakpoint.
    public const int Hysteresis = 2;

    // The thresholds in use: the "layout-breakpoints" setting, else the defaults.
    public static int Tiny => Threshold ("tiny", DefaultTiny);
    public static int Compact => Threshold ("compact", DefaultCompact);
    public static int Medium => Threshold ("medium", DefaultMedium);

    /// <summary>Raised when the layout-mode setting changes, so open pages can re-apply at once.</summary>
    public static event Action? OverrideChanged;

    /// <summary>The tier forced by the layout-mode setting, or null for auto.</summary>
    public static SizeClass? Override =>
        UserSettings.Get (UserSettings.LayoutMode) switch
        {
            "wide" => SizeClass.Wide,
            "medium" => SizeClass.Medium,
            "compact" => SizeClass.Compact,
            "tiny" => SizeClass.Tiny,
            _ => null
        };

    public static void SetMode (string mode)
    {
        UserSettings.Set (UserSettings.LayoutMode, mode);
        OverrideChanged?.Invoke ();
    }

    public static SizeClass Classify (int columns, SizeClass? previous = null)
    {
        SizeClass size = Raw (columns);

        if (previous is not SizeClass last || size == last)
        {
            return size;
        }

        // Growing: only step up once the width is clear of the threshold.
        if (size > last)
        {
            SizeClass settled = Raw (columns - Hysteresis);

            return settled > last ? settled : last;
        }

        // Shrinking: only step down once the width is clear of the threshold.
        SizeClass shrunk = Raw (columns + Hysteresis);

        return shrunk < last ? shrunk : last;
    }

    private static SizeClass Raw (int columns)
    {
        if (columns < Tiny)
        {
            return SizeClass.Tiny;
        }

        if (columns < Compact)
        {
            return SizeClass.Compact;
        }

        return columns < Medium ? SizeClass.Medium : SizeClass.Wide;
    }

    private static int Threshold (string name, int fallback) =>
        int.TryParse (UserSettings.GetTable (UserSettings.LayoutBreakpoints).GetValueOrDefault (name), out int columns) && columns > 0
            ? columns
            : fallback;
}

/// <summary>How a view behaves as space runs out. Every field is optional.</summary>
/// <param name="HideBelow">Hide the view when the tier is smaller than this.</param>
/// <param name="MinWidth">Narrowest width the view should be squeezed to.</param>
/// <param name="PageTitle">Title shown in the pager header when this view is a page.</param>
internal sealed record ResponsiveTag (SizeClass? HideBelow = null, int? MinWidth = null, string? PageTitle = null);

internal static class ResponsiveTags
{
    private static readonly ConditionalWeakTable<View, ResponsiveTag> Tags = new ();

    /// <summary>Tags a view with responsive behaviour. Returns the view so it can be chained.</summary>
    public static T Responsive<T> (this T view, SizeClass? hideBelow = null, int? minWidth = null, string? pageTitle = null) where T : View
    {
        Tags.AddOrUpdate (view, new ResponsiveTag (hideBelow, minWidth, pageTitle));

        return view;
    }

    public static ResponsiveTag? GetResponsive (this View view) => Tags.TryGetValue (view, out ResponsiveTag? tag) ? tag : null;
}

/// <summary>Implemented by widgets that can shrink their own content.</summary>
internal interface IResponsive
{
    // Must be idempotent and cheap; called on every tier or width change.
    // availableWidth is the inside of the widget's border when it fills the page width.
    void ApplySize (SizeClass size, int availableWidth);
}

/// <summary>Width arithmetic shared by the <see cref="IResponsive"/> widgets.</summary>
internal static class ResponsiveWidth
{
    /// <summary>Widgets keep their full-size look above Compact and only shrink at Compact and Tiny.</summary>
    public static bool Scaled (SizeClass size) => size <= SizeClass.Compact;

    /// <summary>What is left after <paramref name="reserved"/> columns, kept between min and max.</summary>
    public static int Fit (int available, int reserved, int min, int max) =>
        Math.Clamp (available - reserved, Math.Min (min, max), max);

    /// <summary>Cuts text to max columns, ending in "..." when there is room for it.</summary>
    public static string Cap (string text, int max) =>
        text.Length <= max ? text : max > 3 ? text [..(max - 3)] + "..." : text [..Math.Max (0, max)];
}

/// <summary>
///     One display line of a table whose long column wraps. A TableView row is always one line,
///     so a wrapped item becomes several rows: the first (<see cref="First"/>) carries the item's
///     other cells, the rest leave them blank. <see cref="Index"/> is the item's position in the
///     widget's own list, for mapping a selected row back to its item.
/// </summary>
internal sealed record WrapRow<T> (T Item, int Index, string Text, bool First);

internal static class TableWrap
{
    /// <summary>
    ///     Expands items into display rows, wrapping <paramref name="text"/> at width columns. With
    ///     <paramref name="gap"/>, a blank row separates one item from the next, so wrapped items
    ///     read as blocks. A gap row belongs to the item above it, so selecting it still maps to an item.
    /// </summary>
    public static List<WrapRow<T>> Expand<T> (IReadOnlyList<T> items, Func<T, string> text, int width, bool gap = true)
    {
        List<WrapRow<T>> rows = new ();

        for (int i = 0; i < items.Count; i++)
        {
            if (gap && i > 0)
            {
                rows.Add (new (items [i - 1], i - 1, "", false));
            }

            bool first = true;

            foreach (string line in Lines (text (items [i]), width))
            {
                rows.Add (new (items [i], i, line, first));
                first = false;
            }
        }

        return rows;
    }

    /// <summary>The item index for a display row, or -1.</summary>
    public static int ItemAt<T> (IReadOnlyList<WrapRow<T>> rows, int row) => row >= 0 && row < rows.Count ? rows [row].Index : -1;

    // Word wrap that also splits words longer than the width (paths, URLs), so no line overflows.
    private static IEnumerable<string> Lines (string text, int width)
    {
        width = Math.Max (1, width);
        string line = "";
        bool any = false;

        foreach (string word in text.Split (' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string rest = word;

            while (rest.Length > 0)
            {
                int room = line.Length == 0 ? width : width - line.Length - 1;

                if (rest.Length <= room)
                {
                    line += line.Length == 0 ? rest : " " + rest;
                    rest = "";
                }
                else if (line.Length > 0 && rest.Length <= width)
                {
                    any = true;
                    yield return line;
                    line = "";
                }
                else
                {
                    // Too long for any line: fill this one and carry the remainder.
                    int take = Math.Max (1, room);

                    if (line.Length > 0 && room <= 0)
                    {
                        any = true;
                        yield return line;
                        line = "";
                        continue;
                    }

                    line += (line.Length == 0 ? "" : " ") + rest [..take];
                    rest = rest [take..];
                    any = true;
                    yield return line;
                    line = "";
                }
            }
        }

        // The last part line, or one empty line for empty text, but no blank tail after a split.
        if (line.Length > 0 || !any)
        {
            yield return line;
        }
    }
}

/// <summary>
///     Hides tagged views when the tier drops below their <see cref="ResponsiveTag.HideBelow"/>,
///     and brings back only the views it hid itself. A view the user switched off stays off.
/// </summary>
internal static class ResponsiveVisibility
{
    private static readonly HashSet<View> HiddenBySize = new (ReferenceEqualityComparer.Instance);

    private static readonly HashSet<View> HiddenByUser = new (ReferenceEqualityComparer.Instance);

    /// <summary>Records the user's own on/off choice for a view and applies it.</summary>
    public static void SetUserHidden (View view, bool hidden)
    {
        if (hidden)
        {
            HiddenByUser.Add (view);
        }
        else
        {
            HiddenByUser.Remove (view);
        }

        Show (view, !hidden && !HiddenBySize.Contains (view));
    }

    public static void Apply (View root, SizeClass size)
    {
        foreach (View subView in root.SubViews)
        {
            if (subView.GetResponsive ()?.HideBelow is SizeClass hideBelow)
            {
                if (size < hideBelow)
                {
                    HiddenBySize.Add (subView);
                }
                else
                {
                    HiddenBySize.Remove (subView);
                }

                Show (subView, !HiddenBySize.Contains (subView) && !HiddenByUser.Contains (subView));
            }

            Apply (subView, size);
        }
    }

    private static void Show (View view, bool visible)
    {
        if (view.Visible == visible)
        {
            return;
        }

        view.Visible = visible;

        // Let the enclosing LayoutView re-equalise around the change.
        view.ResizeLayout ();
    }
}

/// <summary>
///     Watches a page's width and the layout-mode setting, and re-applies the responsive layout
///     when the tier or width changes: visibility, then widget scaling, then row reflow.
/// </summary>
internal sealed class ResponsiveHost
{
    // Resize drags fire many viewport changes; wait this long for them to settle.
    private const int CoalesceMs = 50;

    private readonly View _host;
    private readonly Action<SizeClass>? _onChanged;
    private readonly bool _applyToSubViews;

    private SizeClass? _size;
    private int _width = -1;
    private object? _pending;
    private bool _forced;
    private bool _busy;

    private ResponsiveHost (View host, Action<SizeClass>? onChanged, bool applyToSubViews)
    {
        _host = host;
        _onChanged = onChanged;
        _applyToSubViews = applyToSubViews;
    }

    public SizeClass Size => _size ?? SizeClass.Wide;

    /// <param name="applyToSubViews">
    ///     False for a host that only wants the tier (the main window's tab bar): it must not hide,
    ///     scale or reflow views that belong to a page with its own host.
    /// </param>
    public static ResponsiveHost Attach (View host, Action<SizeClass>? onChanged = null, bool applyToSubViews = true)
    {
        ResponsiveHost responsive = new (host, onChanged, applyToSubViews);

        void OnOverride () => responsive.Schedule (force: true);

        host.ViewportChanged += (_, _) => responsive.Schedule (force: false);
        host.OnShown (() => responsive.Schedule (force: true));
        Breakpoints.OverrideChanged += OnOverride;

        host.Disposing += (_, _) =>
        {
            Breakpoints.OverrideChanged -= OnOverride;
            responsive.Cancel ();
        };

        return responsive;
    }

    private void Schedule (bool force)
    {
        _forced |= force;

        // Scrolling also raises ViewportChanged; only a width change matters.
        if (!_forced && _host.Viewport.Width == _width)
        {
            return;
        }

        if (_pending is not null || _host.App is not { } app)
        {
            return;
        }

        _pending = app.AddTimeout (TimeSpan.FromMilliseconds (CoalesceMs), () =>
        {
            _pending = null;
            Update ();

            return false;
        });
    }

    private void Cancel ()
    {
        if (_pending is { } token)
        {
            _host.App?.RemoveTimeout (token);
            _pending = null;
        }
    }

    private void Update ()
    {
        if (_busy)
        {
            return;
        }

        int width = _host.Viewport.Width;

        if (width <= 0)
        {
            return;
        }

        SizeClass size = Breakpoints.Override ?? Breakpoints.Classify (width, _size);
        bool forced = _forced;
        _forced = false;

        if (!forced && size == _size && width == _width)
        {
            return;
        }

        _busy = true;

        try
        {
            _size = size;
            _width = width;

            if (_applyToSubViews)
            {
                ResponsiveVisibility.Apply (_host, size);
                ApplySizes (_host, size, width);

                foreach (LayoutView row in _host.SubViews.OfType<LayoutView> ())
                {
                    row.Reflow (size, width);
                }
            }

            _onChanged?.Invoke (size);
            _host.SetNeedsLayout ();
        }
        finally
        {
            _busy = false;
        }
    }

    // Each widget is told the width it would have when filling the page: the host width minus
    // the borders of every container around it and its own border. Its current frame is no use
    // here, because Dim.Auto widgets are as wide as their content.
    private static void ApplySizes (View root, SizeClass size, int width)
    {
        foreach (View subView in root.SubViews)
        {
            int inner = width - (subView.Frame.Width - subView.Viewport.Width);

            if (subView is IResponsive responsive && subView.Visible)
            {
                responsive.ApplySize (size, Math.Max (1, inner));
            }

            ApplySizes (subView, size, inner);
        }
    }
}

/// <summary>
///     Tiny-tier paging: shows one page (a Dash row) at a time under a one-line header
///     <c>&lt; Title i/n &gt;</c>. Ctrl+PageUp / Ctrl+PageDown or clicking an arrow moves between pages.
///     Disabling it puts every page back where it was.
/// </summary>
internal sealed class ResponsivePager
{
    private readonly View _host;
    private readonly IReadOnlyList<View> _pages;
    private readonly View _header;
    private readonly Label _title;

    // Each page's original Y (the Pos.Bottom chain), restored when paging ends.
    private readonly Dictionary<View, Pos> _savedY = new (ReferenceEqualityComparer.Instance);

    private int _index;

    public ResponsivePager (View host, IReadOnlyList<View> pages)
    {
        _host = host;
        _pages = pages;

        Label previous = new () { X = 0, Y = 0, Width = 1, Height = 1, Text = "<" };
        _title = new () { X = 2, Y = 0, Width = Dim.Auto (), Height = 1 };
        Label next = new () { X = Pos.Right (_title) + 1, Y = 0, Width = 1, Height = 1, Text = ">" };

        previous.MouseBindings.Add (MouseFlags.LeftButtonClicked, Command.Accept);
        next.MouseBindings.Add (MouseFlags.LeftButtonClicked, Command.Accept);
        previous.Accepted += (_, _) => Move (-1);
        next.Accepted += (_, _) => Move (1);

        _header = new () { X = 0, Y = 0, Width = Dim.Fill (), Height = 1, Visible = false };
        _header.Add (previous, _title, next);
        host.Add (_header);

        // A key handler rather than a command binding: PageUp/PageDown and Ctrl+Home/End are
        // already the host's scroll commands, and these keys only mean something while paging.
        host.KeyDown += (_, key) =>
        {
            if (!Enabled)
            {
                return;
            }

            if (key == Key.PageDown.WithCtrl)
            {
                key.Handled = Move (1);
            }
            else if (key == Key.PageUp.WithCtrl)
            {
                key.Handled = Move (-1);
            }
        };
    }

    public bool Enabled { get; private set; }

    public View Current => _pages [_index];

    /// <summary>Raised after the shown page changes or paging is switched on or off.</summary>
    public event Action? PageChanged;

    public void SetEnabled (bool enabled)
    {
        if (enabled == Enabled || _pages.Count == 0)
        {
            return;
        }

        Enabled = enabled;

        if (enabled)
        {
            foreach (View page in _pages)
            {
                _savedY [page] = page.Y;
            }

            ShowCurrent ();
        }
        else
        {
            foreach (View page in _pages)
            {
                page.Y = _savedY [page];
                page.Visible = true;
            }

            _savedY.Clear ();
        }

        _header.Visible = enabled;
        _host.SetNeedsLayout ();
        PageChanged?.Invoke ();
    }

    private bool Move (int step)
    {
        if (!Enabled)
        {
            return false;
        }

        _index = (_index + step + _pages.Count) % _pages.Count;
        ShowCurrent ();
        _host.SetNeedsLayout ();
        PageChanged?.Invoke ();

        return true;
    }

    private void ShowCurrent ()
    {
        bool focusLost = false;

        for (int i = 0; i < _pages.Count; i++)
        {
            View page = _pages [i];
            bool shown = i == _index;

            focusLost |= !shown && page.HasFocus;
            page.Visible = shown;
        }

        Current.Y = 1;
        _title.Text = $"{Current.GetResponsive ()?.PageTitle ?? $"Page {_index + 1}"} {_index + 1}/{_pages.Count}";

        // Focus inside a page that just went away would leave the keyboard nowhere.
        if (focusLost)
        {
            Current.SetFocus ();
        }
    }
}
