// -----------------------------------------------------------------------------
//  The Dash tab: the landing page that arranges the dashboard widgets.
// -----------------------------------------------------------------------------

using Terminal.Gui.Input;

namespace Mode;

internal sealed class DashWindow : Window
{
    private const int GitHistoryHeight = 12;

    public DashWindow (Pos x, Pos y)
    {
        Title = "Dash";
        X = x;
        Y = y;
        Height=Dim.Fill();
        Width=Dim.Fill();

        MenuBarItem quit = new(){
            Title = "_Quit",
        };

        Bar bar = new(){
           Orientation = Orientation.Horizontal
        };

        bar.Add(quit);
        bar.Add(new MenuBarItem{Title="testa"});
        bar.Add(new Line());
        bar.Add(new MenuBarItem{Title="test2"});

        Label welcome = new ()
        {
            Text = "Welcome to Terminal.Gui v3!",
            X = Pos.Center (),
            Y = 1
        };

        Button quitButton = new ()
        {
            Text = "abc",
            X = Pos.Center(),
            Y = Pos.Bottom(welcome)
        };
        Button quitButton1 = new ()
        {
            Text = "ab2c",
            X = Pos.Left(quitButton),
            Y = Pos.Bottom (quitButton)
        };

        quit.Accepted += (_, _) => App!.RequestStop ();

        SystemWidget system = new ();
        ProcessesWidget processes = new ();
        NextUpWidget nextUp = new ();

        View meters = Layouts.Horizontal (system, processes, nextUp);
        meters.X = 0;
        meters.Y = 0;

        TodoWidget todo = new (amount: 8, textWidth: 40);

        TasksWidget tasks = new (trashable: false);

        View work = Layouts.Horizontal (todo, tasks);
        work.X = 0;
        work.Y = Pos.Bottom (meters);

        ReportsWidget reports = new ();

        FreshdeskWidget freshdesk = new ();

        NotesWidget notes = new ();

        LinksWidget links = new ();

        View recent = Layouts.Horizontal (reports, notes, freshdesk, links);
        recent.X = 0;
        recent.Y = Pos.Bottom (work);

        HackerNewsWidget news = new (10);
        SessionsWidget stats = new ();
        MailWidget mail = new ();
        TeamsWidget teams = new ();

        View feeds = Layouts.Horizontal (news, stats, mail, teams);
        feeds.X = 0;
        feeds.Y = Pos.Bottom (recent);

        GitContributionsWidget contributions = new ();
        GitHistoryWidget history = new (amount: GitHistoryWidget.DefaultAmount);

        View git = Layouts.Vertical (contributions, history);

        ScriptHistoryWidget scripts = new ();
        ReviewHistoryWidget reviews = new ();
        ProjectsWidget projects = new ();

        View lower = Layouts.Horizontal (git, scripts, reviews, projects);
        lower.X = 0;
        lower.Y = Pos.Bottom (feeds);

        Add (meters, work, recent, feeds, lower);

        VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

        void SizeContent ()
        {
            int needed = Math.Max (Viewport.Height, lower.Frame.Y + lower.Frame.Height);
            System.Drawing.Size wanted = new (Viewport.Width, needed);

            if (GetContentSize () != wanted)
            {
                SetContentSize (wanted);
            }
        }

        ViewportChanged += (_, _) => SizeContent ();
        SubViewsLaidOut += (_, _) => SizeContent ();

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

        void ApplyToggles ()
        {
            teams.Visible = UserSettings.On (UserSettings.ShowTeams);
            mail.Visible = UserSettings.On (UserSettings.ShowMail);
        }

        ApplyToggles ();
        this.OnShown (ApplyToggles);

    }

    protected override bool OnMouseEvent (Mouse mouse) => WheelScrolling.Handle (this, mouse) || base.OnMouseEvent (mouse);
}

