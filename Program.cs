// -----------------------------------------------------------------------------
//  Entry point. Loads .env and user settings, opens the database, applies the theme, then runs MainWindow.
// -----------------------------------------------------------------------------

global using Terminal.Gui.App;
global using Terminal.Gui.Configuration;
global using Terminal.Gui.ViewBase;
global using Terminal.Gui.Views;
global using Terminal.Gui.Editor;

global using Sql;
using Mode;

Env.Load ();

UserSettings.Load ();

Engine.Init ();

NotesSync.Run ();

Theme.Register ();

Theme.Apply ();

ButtonSettings.Current = ButtonSettings.Current with { DefaultShadow = ShadowStyles.None };
ThemeManager.ThemeChanged += (_, _) =>
    ButtonSettings.Current = ButtonSettings.Current with { DefaultShadow = ShadowStyles.None };

Theme.Restore ();

Application.MaximumIterationsPerSecond = 60;

Application
    .Create ()
    .Run<MainWindow> ()
    .Dispose ();

/// <summary>
///     The root view: the banner, the tab bar, and every tab and page. The banner comes from
///     BACKDROP_FILE or BACKDROP in .env (use \n for line breaks), else the default below.
/// </summary>
internal sealed class MainWindow : Window
{
    // Tab bar titles at Compact (responsive plan T9, O4). Settings and Notes keep a hotkey.
    private static readonly Dictionary<string, string> ShortTitles = new ()
    {
        ["Dash"] = "D",
        ["Tasks"] = "Tk",
        ["Todos"] = "Td",
        ["Stats"] = "St",
        ["Scripts"] = "Sc",
        ["AI"] = "AI",
        ["Git"] = "G",
        ["_Settings"] = "_Set",
        ["_Notes"] = "_N"
    };

    private const string DefaultBackdrop = """
        ooooooooooo ooooooooooo oooo     oooo
            888         888      8888o   888
            888         888      88 888o8 88
            888         888      88  888  88
           o888o       o888o    o88o  8  o88o

        """;

    private static string LoadBackdrop ()
    {
        string? file = Env.Get ("BACKDROP_FILE");

        if (!string.IsNullOrWhiteSpace (file))
        {
            try
            {
                return File.ReadAllText (file);
            }
            catch (Exception)
            {
            }
        }

        string? inline = Env.Get ("BACKDROP");

        return string.IsNullOrWhiteSpace (inline)
            ? DefaultBackdrop
            : inline.Replace ("\n", "\n");
    }

    public MainWindow ()
    {
        BorderStyle = Terminal.Gui.Drawing.LineStyle.None;

        Label title = new Label()
        {
            Text = LoadBackdrop (),
            Width = Dim.Auto(),
            Height = Dim.Auto(),
            X = Pos.Center()
        };
        Add(title);

        Bar bar = new(){
           Orientation = Orientation.Horizontal,
           Height = 1
        };

        Window menuWindow = new()
        {
            Title="",
            Width = Dim.Percent(100),
            Height = Dim.Auto(),
            Y = Pos.Bottom(title)
        };

        // Every top-level item on the bar with its full title, and the one shown on Tiny instead.
        List<(MenuBarItem Item, string Full)> barItems = new();
        List<MenuItem> everything = new();

        menuWindow.Add(bar);

        List<View> windows = new List<View>();
        List<MenuBarItem> tabs = new List<MenuBarItem>();

        windows.Add(new Mode.DashWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));
        windows.Add(new TasksWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));
        windows.Add(new TodoTestWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));
        windows.Add(new ClaudeStatsTestWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));
        windows.Add(new ScriptsWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));
        windows.Add(new AiWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));
        windows.Add(new GitWindow(Pos.Left(menuWindow), Pos.Bottom(menuWindow)));

        for(int i=0; i < windows.Count; i++){
            int x = i;
            tabs.Add(new MenuBarItem() {
              Title=windows[i].Title
            });
            tabs[i].Action = () => {Handler.ResetVisibility(windows); windows[x].Visible = true; windows[x].SetFocus();};
            everything.Add(new MenuItem { Title = windows[i].Title, Action = tabs[i].Action });
            barItems.Add((tabs[i], windows[i].Title));
            bar.Add(tabs[i]);
            Add(windows[i]);
        };

        List<View> pages = Settings.Pages(Pos.Left(menuWindow), Pos.Bottom(menuWindow));

        foreach(View page in pages){
            Add(page);
        };

        windows.AddRange(pages);
        MenuBarItem settings = new MenuBarItem("_Settings", new PopoverMenu(Settings.MenuItems(windows, pages)));
        barItems.Add((settings, "_Settings"));
        everything.AddRange(Settings.MenuItems(windows, pages));
        bar.Add(settings);

        List<View> notes = Notes.Pages(Pos.Left(menuWindow), Pos.Bottom(menuWindow));

        foreach(View note in notes){
            Add(note);
        };

        windows.AddRange(notes);
        MenuBarItem notesMenu = new MenuBarItem("_Notes", new PopoverMenu(Notes.MenuItems(windows, notes)));
        barItems.Add((notesMenu, "_Notes"));
        everything.AddRange(Notes.MenuItems(windows, notes));
        bar.Add(notesMenu);

        // Tiny: a single item whose menu reaches every tab and page.
        MenuBarItem menu = new MenuBarItem("_Menu", new PopoverMenu(everything)) { Visible = false };
        bar.Add(menu);

        Notes.Connect(windows, notes);

        Handler.ResetVisibility(windows);
        windows[0].Visible = true;

        Add(menuWindow);

        // Responsive plan T9: shorter tab titles at Compact, one menu on Tiny, no banner below
        // Medium (O5). This host only reads the tier; the Dash tab runs its own host.
        ResponsiveHost.Attach(this, size =>
        {
            foreach ((MenuBarItem item, string full) in barItems)
            {
                item.Visible = size > SizeClass.Tiny;
                item.Title = size >= SizeClass.Medium ? full : ShortTitles.GetValueOrDefault(full, full);
            }

            menu.Visible = size == SizeClass.Tiny;

            // Set Y outright: a hidden label may keep its frame, so Pos.Bottom(title) might not move up.
            title.Visible = size >= SizeClass.Medium;
            menuWindow.Y = title.Visible ? Pos.Bottom(title) : 0;
        }, applyToSubViews: false);

        // Warns when another machine has pushed since this one last synced, then keeps this
        // machine pushing its own changes on the configured interval.
        Initialized += (_, _) =>
        {
            SyncRunner.CheckOnLaunch(this);
            AutoSync.Start(this);
        };
    }
}
