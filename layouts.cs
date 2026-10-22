// -----------------------------------------------------------------------------
//  Containers that group widgets in one direction and equalise them across the other.
// -----------------------------------------------------------------------------

internal static class Layouts
{
    public static LayoutView Horizontal (params View[] widgets) => Build (widgets, true, Dim.Auto (), HorizontalGap);

    public static LayoutView Vertical (params View[] widgets) => Build (widgets, false, Dim.Auto (), VerticalGap);

    public static LayoutView Large (params View[] widgets) => Build (widgets, true, Dim.Fill (), HorizontalGap);

    private const int HorizontalGap = 1;
    private const int VerticalGap = 0;

    private static LayoutView Build (View[] widgets, bool horizontal, Dim width, int gap)
    {
        LayoutView container = new (horizontal, width, gap);

        foreach (View widget in widgets)
        {
            container.Place (widget);
        }

        return container;
    }
}

/// <summary>
///     A container that keeps its widgets in one direction and equalises them across the other.
///     <see cref="Reflow"/> lets a horizontal container stack its widgets when space runs out.
/// </summary>
internal sealed class LayoutView : View
{
    private const string Expanded = ">";

    private const string Shrunk = "|";

    private readonly bool _preferHorizontal;
    private readonly Dim _width;
    private readonly int _gap;

    private bool _horizontal;

    // Children in the order they were placed, so they can be re-placed after a reflow.
    private readonly List<View> _order = new ();

    // Each child's own size, captured at Place() so either axis can be restored.
    private readonly Dictionary<View, Dim> _naturalWidth = new ();
    private readonly Dictionary<View, Dim> _naturalHeight = new ();

    // Each child's width while laid out side by side; used to tell whether the row still fits.
    private readonly Dictionary<View, int> _sideBySideWidth = new ();

    private readonly Dictionary<View, int> _measured = new ();

    private bool _busy;

    public LayoutView (bool horizontal, Dim width, int gap)
    {
        _preferHorizontal = horizontal;
        _horizontal = horizontal;
        _width = width;
        _gap = gap;

        Width = width;
        Height = Dim.Auto ();

        CanFocus = true;

        this.AddCollapse (Expanded, Shrunk);

        SubViewsLaidOut += (_, _) => Equalise ();
    }

    public void Place (View widget)
    {
        View? previous = _order.LastOrDefault ();

        widget.X = _horizontal && previous is not null ? Pos.Right (previous) + _gap : 0;
        widget.Y = _horizontal || previous is null ? 0 : Pos.Bottom (previous) + _gap;

        _naturalWidth [widget] = widget.Width;
        _naturalHeight [widget] = widget.Height;

        Add (widget);
        _order.Add (widget);
    }

    public void Resize ()
    {
        Equalise (remeasure: true);

        this.Remeasure ();

        (SuperView as LayoutView)?.Resize ();
    }

    /// <summary>
    ///     Picks the direction for the given tier and width: a horizontal container stacks when
    ///     the tier is Compact or smaller, or when its widgets no longer fit side by side.
    ///     Nested containers are reflowed against the width they get. Returns true if anything moved.
    /// </summary>
    public bool Reflow (SizeClass size, int available)
    {
        if (_horizontal)
        {
            foreach (View v in _order.Where (v => v.Visible))
            {
                _sideBySideWidth [v] = v.Frame.Width;
            }
        }

        bool horizontal = _preferHorizontal && size > SizeClass.Compact && SideBySideWidth () <= available;
        bool changed = horizontal != _horizontal;

        if (changed)
        {
            _horizontal = horizontal;

            foreach (View v in _order)
            {
                v.Width = _naturalWidth [v];
                v.Height = _naturalHeight [v];
            }

            Width = _horizontal ? _width : Dim.Fill ();
        }

        // Re-place every time: visibility may have changed even if the direction did not.
        Arrange ();
        _measured.Clear ();
        Equalise (remeasure: true);

        int inner = available - (Frame.Width - Viewport.Width);

        foreach (LayoutView child in _order.OfType<LayoutView> ())
        {
            changed |= child.Reflow (size, _horizontal ? child.Frame.Width : inner);
        }

        return changed;
    }

    private int SideBySideWidth ()
    {
        List<View> shown = _order.Where (v => v.Visible).ToList ();

        return shown.Sum (v => _sideBySideWidth.GetValueOrDefault (v, v.Frame.Width)) + _gap * Math.Max (0, shown.Count - 1);
    }

    // Chains the visible children one after another; hidden ones are skipped so they leave no gap.
    private void Arrange ()
    {
        View? previous = null;

        foreach (View v in _order.Where (v => v.Visible))
        {
            v.X = _horizontal && previous is not null ? Pos.Right (previous) + _gap : 0;
            v.Y = _horizontal || previous is null ? 0 : Pos.Bottom (previous) + _gap;

            previous = v;
        }
    }

    // A row stacked by Reflow fills the width; otherwise the child keeps its own size.
    private Dim? Natural (View v) =>
        _horizontal ? _naturalHeight [v]
        : _preferHorizontal ? Dim.Fill ()
        : _naturalWidth [v];

    private int Axis (View v) => _horizontal ? v.Frame.Height : v.Frame.Width;

    private Dim? GetAxis (View v) => _horizontal ? v.Height : v.Width;

    private bool SetAxis (View v, Dim? dim)
    {
        if (Equals (GetAxis (v), dim))
        {
            return false;
        }

        if (_horizontal)
        {
            v.Height = dim;
        }
        else
        {
            v.Width = dim;
        }

        return true;
    }

    private void Equalise (bool remeasure = false)
    {
        if (_busy)
        {
            return;
        }

        List<View> shown = SubViews.Where (v => _naturalWidth.ContainsKey (v) && v.Visible && !v.IsCollapsed ()).ToList ();

        if (shown.Count == 0)
        {
            return;
        }

        _busy = true;

        try
        {
            if (remeasure || shown.Any (v => !_measured.ContainsKey (v)))
            {
                bool reset = false;

                foreach (View v in shown)
                {
                    reset |= SetAxis (v, Natural (v));
                }

                if (reset)
                {
                    Layout ();
                }

                foreach (View v in shown)
                {
                    _measured [v] = Axis (v);
                }
            }
            else
            {
                foreach (View v in shown.Where (v => Equals (GetAxis (v), Natural (v))))
                {
                    _measured [v] = Axis (v);
                }
            }

            int largest = shown.Max (v => _measured [v]);
            bool changed = false;

            foreach (View v in shown)
            {
                changed |= SetAxis (v, _measured [v] < largest ? Dim.Fill () : Natural (v));
            }

            if (changed)
            {
                Layout ();
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
