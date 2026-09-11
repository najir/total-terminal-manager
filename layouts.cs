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

/// <summary>A container that keeps its widgets in one direction and equalises them across the other.</summary>
internal sealed class LayoutView : View
{
    private const string Expanded = ">";

    private const string Shrunk = "|";

    private readonly bool _horizontal;
    private readonly int _gap;

    private readonly Dictionary<View, Dim?> _natural = new ();

    private readonly Dictionary<View, int> _measured = new ();

    private View? _previous;
    private bool _busy;

    public LayoutView (bool horizontal, Dim width, int gap)
    {
        _horizontal = horizontal;
        _gap = gap;

        Width = width;
        Height = Dim.Auto ();

        CanFocus = true;

        this.AddCollapse (Expanded, Shrunk);

        SubViewsLaidOut += (_, _) => Equalise ();
    }

    public void Place (View widget)
    {
        widget.X = _horizontal && _previous is not null ? Pos.Right (_previous) + _gap : 0;
        widget.Y = _horizontal || _previous is null ? 0 : Pos.Bottom (_previous) + _gap;

        _natural [widget] = _horizontal ? widget.Height : widget.Width;

        Add (widget);
        _previous = widget;
    }

    public void Resize ()
    {
        Equalise (remeasure: true);

        this.Remeasure ();

        (SuperView as LayoutView)?.Resize ();
    }

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

        List<View> shown = SubViews.Where (v => _natural.ContainsKey (v) && v.Visible && !v.IsCollapsed ()).ToList ();

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
                    reset |= SetAxis (v, _natural [v]);
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
                foreach (View v in shown.Where (v => Equals (GetAxis (v), _natural [v])))
                {
                    _measured [v] = Axis (v);
                }
            }

            int largest = shown.Max (v => _measured [v]);
            bool changed = false;

            foreach (View v in shown)
            {
                changed |= SetAxis (v, _measured [v] < largest ? Dim.Fill () : _natural [v]);
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

