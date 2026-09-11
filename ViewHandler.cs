// -----------------------------------------------------------------------------
//  View helpers: visibility switching, scrolling, collapsing and TableView tweaks.
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;

internal sealed class Handler{
    static public string ResetVisibility(List<View> windows){
        string result = "Visiblity set for: ";
        int    incr   = 0;
        for(int j=0; j<windows.Count;j++){
            incr++;
            windows[j].Visible = false;
        };

        return $"{result} {incr} views";
    }
}

/// <summary>
///     Terminal.Gui raises <see cref="View.VisibleChanged"/> only on the view whose <see
///     cref="View.Visible"/> actually changed — it does not cascade to SubViews.
/// </summary>
internal static class ViewVisibility
{
    public static void OnShown (this View view, Action onShown)
    {
        List<View> chain = new ();
        bool shown = false;

        void Check (object? sender, EventArgs e)
        {
            bool showing = true;

            for (View? v = view; v is not null && showing; v = v.SuperView)
            {
                showing = v.Visible;
            }

            if (showing == shown)
            {
                return;
            }

            shown = showing;

            if (showing)
            {
                onShown ();
            }
        }

        view.Initialized += (_, _) =>
        {
            for (View? v = view; v is not null; v = v.SuperView)
            {
                chain.Add (v);
                v.VisibleChanged += Check;
            }

            Check (null, EventArgs.Empty);
        };

        view.Disposing += (_, _) =>
        {
            foreach (View v in chain)
            {
                v.VisibleChanged -= Check;
            }

            chain.Clear ();
        };
    }
}

/// <summary>Makes <see cref="Dim.Auto"/> measure again after content arrives late.</summary>
internal static class ViewMeasurement
{
    public static void Remeasure (this View view)
    {
        foreach (View subView in view.SubViews)
        {
            Refresh (subView);
        }

        Refresh (view);

        View target = view.SuperView ?? view;

        target.SetNeedsLayout ();
        target.Layout ();
    }

    private static void Refresh (View view)
    {
        if (view.Width is DimAuto)
        {
            view.Width = Dim.Auto ();
        }

        if (view.Height is DimAuto)
        {
            view.Height = Dim.Auto ();
        }
    }
}

/// <summary>One wheel notch, one scroll distance, for every scrolling view in the app.</summary>
internal static class WheelScrolling
{
    public const int LinesPerNotch = 4;

    public static bool Handle (View view, Mouse mouse)
    {
        if (mouse.Flags.HasFlag (MouseFlags.WheeledDown))
        {
            view.ScrollVertical (LinesPerNotch);

            return true;
        }

        if (mouse.Flags.HasFlag (MouseFlags.WheeledUp))
        {
            view.ScrollVertical (-LinesPerNotch);

            return true;
        }

        return false;
    }
}

/// <summary>A View that actually scrolls: wheel, PageUp/PageDown and Ctrl+arrows.</summary>
internal class ScrollableView : View
{
    public ScrollableView ()
    {
        Width = Dim.Auto ();
        Height = Dim.Auto ();

        CanFocus = true;

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
    }

    protected override bool OnMouseEvent (Mouse mouse) => WheelScrolling.Handle (this, mouse) || base.OnMouseEvent (mouse);
}

/// <summary>Asks the layout holding a view to lay out again, after that view has changed its own size.</summary>
internal static class ViewLayoutRefresh
{
    public static void ResizeLayout (this View view)
    {
        for (View? parent = view.SuperView; parent is not null; parent = parent.SuperView)
        {
            if (parent is LayoutView layout)
            {
                layout.Resize ();

                break;
            }
        }

        View top = view;

        while (top.SuperView is { } above)
        {
            top = above;
        }

        top.SetNeedsLayout ();
        top.SetNeedsDraw ();
    }
}

/// <summary>A one-cell toggle in the top-left of a widget that shrinks it to almost nothing and back again.</summary>
internal static class ViewCollapse
{
    private static readonly HashSet<View> Collapsed = new (ReferenceEqualityComparer.Instance);

    private const string Expanded = "*";

    private const string Shrunk = "+";

    private const int ContentInset = 1;

    public static bool IsCollapsed (this View view) => Collapsed.Contains (view);

    public static void AddCollapse (this View view, string expanded = Expanded, string shrunk = Shrunk)
    {
        Label toggle = new ()
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
            Text = expanded
        };

        toggle.MouseBindings.Add (MouseFlags.LeftButtonClicked, Command.Accept);

        List<View> content = new ();
        Dim? width = null;
        Dim? height = null;
        LineStyle? border = null;

        toggle.Accepted += (_, _) =>
        {
            if (Collapsed.Remove (view))
            {
                foreach (View part in content)
                {
                    view.Add (part);
                }

                content.Clear ();

                view.Remove (toggle);
                view.Add (toggle);

                view.Width = width;
                view.Height = height;
                view.BorderStyle = border;
                toggle.Text = expanded;
            }
            else
            {
                width = view.Width;
                height = view.Height;

                content.AddRange (view.SubViews.Where (v => !ReferenceEquals (v, toggle)));

                foreach (View part in content)
                {
                    view.Remove (part);
                }

                border = view.BorderStyle;
                view.BorderStyle = LineStyle.None;

                view.Width = Dim.Auto ();
                view.Height = Dim.Auto ();
                Collapsed.Add (view);
                toggle.Text = shrunk;
            }

            view.ResizeLayout ();
        };

        view.Initialized += (_, _) => view.Add (toggle);

        bool inset = false;

        view.SubViewsLaidOut += (_, _) =>
        {
            if (inset)
            {
                return;
            }

            inset = true;

            foreach (View part in view.SubViews)
            {
                if (!ReferenceEquals (part, toggle) && part.Frame.X == 0)
                {
                    part.X = ContentInset;
                }
            }
        };

        view.Disposing += (_, _) => Collapsed.Remove (view);
    }
}

/// <summary>Keeps a TableView from coming up with a row already highlighted.</summary>
internal static class ViewTableSelection
{
    private sealed class Unselected
    {
        public bool Armed = true;

        public bool Suppress;
    }

    private static readonly ConditionalWeakTable<TableView, Unselected> Tables = new ();

    public static void SetSource (this TableView table, ITableSource source)
    {
        Unselected state = Register (table);

        table.WheelRows ();

        state.Suppress = true;

        try
        {
            table.Table = source;
        }
        finally
        {
            state.Suppress = false;
        }

        Clear (table, state);
    }

    public static void Refresh (this TableView table)
    {
        table.Update ();

        if (Tables.TryGetValue (table, out Unselected? state))
        {
            Clear (table, state);
        }
    }

    private static Unselected Register (TableView table)
    {
        if (Tables.TryGetValue (table, out Unselected? existing))
        {
            return existing;
        }

        Unselected state = new ();
        Tables.Add (table, state);

        table.ValueChanged += (_, _) =>
                              {
                                  if (!state.Suppress && table.Value is not null)
                                  {
                                      state.Armed = false;
                                  }
                              };

        return state;
    }

    private static void Clear (TableView table, Unselected state)
    {
        if (!state.Armed)
        {
            return;
        }

        state.Suppress = true;

        try
        {
            table.Value = new TableSelection (System.Drawing.Point.Empty);
            table.Value = null;
        }
        finally
        {
            state.Suppress = false;
        }
    }
}

/// <summary>Widens the scroll-wheel step on a TableView from one row to several.</summary>
internal static class ViewTableWheel
{
    public const int DefaultRows = 4;

    public static void WheelRows (this TableView table, int rows = DefaultRows)
    {
        if (rows < 1)
        {
            return;
        }

        table.MouseBindings.ReplaceCommands (
            MouseFlags.WheeledDown,
            Enumerable.Repeat (Command.Down, rows).ToArray ());

        table.MouseBindings.ReplaceCommands (
            MouseFlags.WheeledUp,
            Enumerable.Repeat (Command.Up, rows).ToArray ());
    }

    /// <summary>
    ///     Lines per wheel notch on an <see cref="Editor"/>. The Editor binds the wheel to one
    ///     ScrollDown / ScrollUp; repeating that command per notch is what makes the wheel
    ///     faster. ReplaceCommands, not Add: the binding exists, and Add would throw.
    /// </summary>
    public static void WheelLines (this Editor editor, int lines = DefaultRows)
    {
        if (lines < 1)
        {
            return;
        }

        editor.MouseBindings.ReplaceCommands (
            MouseFlags.WheeledDown,
            Enumerable.Repeat (Command.ScrollDown, lines).ToArray ());

        editor.MouseBindings.ReplaceCommands (
            MouseFlags.WheeledUp,
            Enumerable.Repeat (Command.ScrollUp, lines).ToArray ());
    }
}

