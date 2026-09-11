// -----------------------------------------------------------------------------
//  Usage stats read from Claude Code's local session transcripts.
// -----------------------------------------------------------------------------

using System.Drawing;
using System.Text;
using System.Text.Json;

internal sealed class ClaudeStatsTestWindow : Window
{
    private const int WindowDays = ClaudeTranscripts.WindowDays;
    private const int SessionsForTools = 14;
    private const int SessionRows = 30;
    private const int TopTools = 5;

    private readonly Label _headline;
    private readonly Label _totals;
    private readonly Label _graphTitle;
    private readonly SessionsWidget _sessions;
    private readonly LastSessionWidget _lastSession;
    private readonly TableView _tools;
    private readonly GraphView _graph;

    private ClaudeStats _stats = Empty ();
    private bool _loading;
    private bool _loadedOnce;

    private (int Height, long Max) _scaledFor = (-1, -1);

    public ClaudeStatsTestWindow (Pos x, Pos y)
    {
        Title = "Stats";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        _headline = new ()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill (12),
            Height = 1,
            Text = "Claude Code usage"
        };

        Button reload = new ()
        {
            Text = "_Reload",
            X = Pos.AnchorEnd (),
            Y = 0
        };

        _totals = new ()
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill (),
            Height = 1,
            Text = "reading transcripts ..."
        };

        _sessions = new (amount: SessionRows)
        {
            X = 0,
            Y = 2,

            Height = Dim.Percent (40)
        };

        _lastSession = new ()
        {
            X = Pos.Right (_sessions) + 1,
            Y = 2,
            Height = Dim.Percent (40)
        };

        _graphTitle = new ()
        {
            X = 0,
            Y = Pos.Bottom(_sessions),
            Width = Dim.Auto(),
            Height = 1,
            Text = "Tokens by day"
        };

        _graph = new ()
        {
            X = 0,
            Y = Pos.Bottom (_graphTitle),
            Width = Dim.Percent(50),
            Height = Dim.Fill(),
            MarginBottom = 2,
            MarginLeft = 6
        };

        _tools = new ()
        {
            X = Pos.Right(_graph),
            Y = Pos.Bottom(_graphTitle),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
            MultiSelect = false
        };

        Label toolsTitle = new ()
        {
            X = Pos.Left(_tools),
            Y = Pos.Bottom(_sessions),
            Width = Dim.Fill(),
            Height = 1,
            Text = $"Top {TopTools} tools"
        };

        reload.Accepted += (_, _) => _ = LoadAsync ();

        _graph.ViewportChanged += (_, _) => ScaleGraph ();

        Add (_headline, reload, _totals, _sessions, _lastSession, _graphTitle, toolsTitle, _graph, _tools);

        Render ();

        VisibleChanged += (_, _) =>
        {
            if (!Visible || _loadedOnce)
            {
                return;
            }

            _loadedOnce = true;
            _ = LoadAsync ();
        };
    }

    private static ClaudeStats Empty () => ClaudeTranscripts.Empty ();

    private async Task LoadAsync ()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _totals.Text = "reading transcripts ...";

        try
        {
            ClaudeStats stats = await Task.Run (ClaudeTranscripts.Collect);

            Ui (() =>
            {
                _stats = stats;
                Render ();
            });
        }
        catch (Exception ex)
        {
            Ui (() => _totals.Text = $"could not read transcripts: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render ()
    {
        DateTime firstDay = _stats.Days.Length > 0 ? _stats.Days [0] : DateTime.Today;
        DateTime lastDay = _stats.Days.Length > 0 ? _stats.Days [^1] : DateTime.Today;

        _headline.Text = $"Claude Code · last {WindowDays} days ({firstDay:MMM d} – {lastDay:MMM d})";

        _totals.Text = _stats.Sessions.Count == 0
            ? $"no sessions found under {ClaudeTranscripts.Root ()}"
            : $"{_stats.Total:N0} tokens · {_stats.Sessions.Count} sessions · "
              + $"in {Short (_stats.Input)} · out {Short (_stats.Output)} · "
              + $"cache read {Short (_stats.CacheRead)} · cache write {Short (_stats.CacheWrite)}";

        _sessions.Show (_stats.Sessions);

        _lastSession.Show (_stats.Sessions);

        RenderTools ();
        RenderGraph ();
    }

    private void RenderTools ()
    {
        List<ClaudeSession> recent = _stats.Sessions.Take (SessionsForTools).ToList ();
        Dictionary<string, int> counts = recent.Count == _stats.Sessions.Count
            ? _stats.ToolCalls
            : Recount (recent);

        List<(string Name, int Calls)> top = counts
                                             .Select (kv => (kv.Key, kv.Value))
                                             .OrderByDescending (t => t.Value)
                                             .ThenBy (t => t.Key, StringComparer.Ordinal)
                                             .Take (TopTools)
                                             .ToList ();

        int most = top.Count > 0 ? top [0].Calls : 1;

        _tools.SetSource (
                          new EnumerableTableSource<(string Name, int Calls)> (
                                                                               top,
                                                                               new Dictionary<string, Func<(string Name, int Calls), object>>
                                                                               {
                                                                                   ["Tool"] = t => t.Name,
                                                                                   ["Calls"] = t => t.Calls,
                                                                                   [""] = t => new string ('█', Math.Max (1, (int) Math.Round (12.0 * t.Calls / most)))
                                                                               }));
        _tools.Refresh ();
    }

    private static Dictionary<string, int> Recount (List<ClaudeSession> sessions)
    {
        ClaudeStats scratch = new () { TokensByDay = new long[WindowDays], Days = [] };

        foreach (ClaudeSession session in sessions)
        {
            ClaudeTranscripts.ReadSession (session.Path, DateTime.Today.AddDays (-(WindowDays - 1)), scratch);
        }

        return scratch.ToolCalls;
    }

    private void RenderGraph ()
    {
        _graph.Reset ();

        BarSeries series = new ()
        {
            BarEvery = 3f,
            Orientation = Orientation.Vertical,
            DrawLabels = true
        };

        for (int i = 0; i < _stats.Days.Length; i++)
        {
            series.Bars.Add (new BarSeriesBar (
                                               _stats.Days [i].ToString ("dd"),
                                               new GraphCellToRender ((Rune) '█'),
                                               _stats.TokensByDay [i]));
        }

        _graph.Series.Add (series);

        _graph.AxisX.Increment = 0;
        _graph.AxisX.ShowLabelsEvery = 1;
        _graph.AxisX.Text = "";

        _graph.AxisY.ShowLabelsEvery = 1;
        _graph.AxisY.Text = "";
        _graph.AxisY.LabelGetter = v => Short ((long) v.Value);

        _scaledFor = (-1, -1);
        ScaleGraph ();
    }

    private void ScaleGraph ()
    {
        long max = _stats.TokensByDay.Length > 0 ? _stats.TokensByDay.Max () : 0;
        int rows = _graph.Viewport.Height - (int) _graph.MarginBottom - 1;

        if (rows < 1 || _scaledFor == (rows, max))
        {
            return;
        }

        _scaledFor = (rows, max);

        float unitsPerRow = max > 0 ? max / (float) rows : 1f;

        _graph.CellSize = new PointF (1f, unitsPerRow);
        _graph.AxisY.Increment = unitsPerRow * Math.Max (1, rows / 4);
        _graph.SetNeedsDraw ();
    }

    private static string Short (long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:0.0}M",
        >= 1_000 => $"{value / 1_000.0:0.0}K",
        _ => value.ToString ()
    };

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

