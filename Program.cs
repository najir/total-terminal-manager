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
            bar.Add(tabs[i]);
            Add(windows[i]);
        };

        List<View> pages = Settings.Pages(Pos.Left(menuWindow), Pos.Bottom(menuWindow));

        foreach(View page in pages){
            Add(page);
        };

        windows.AddRange(pages);
        bar.Add(new MenuBarItem("_Settings", new PopoverMenu(Settings.MenuItems(windows, pages))));

        List<View> notes = Notes.Pages(Pos.Left(menuWindow), Pos.Bottom(menuWindow));

        foreach(View note in notes){
            Add(note);
        };

        windows.AddRange(notes);
        bar.Add(new MenuBarItem("_Notes", new PopoverMenu(Notes.MenuItems(windows, notes))));

        Notes.Connect(windows, notes);

        Handler.ResetVisibility(windows);
        windows[0].Visible = true;

        Add(menuWindow);
    }
}
