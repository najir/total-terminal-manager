// -----------------------------------------------------------------------------
//  The Scripts tab: folder box, script list and a live output terminal.
// -----------------------------------------------------------------------------

internal sealed class ScriptsWindow : Window
{
    public ScriptsWindow (Pos x, Pos y)
    {
        Title = "Scripts";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        Label label = new () { Text = "Folder:", X = 0, Y = 0 };

        TextField folder = new ()
        {
            X = Pos.Right (label) + 1,
            Y = 0,
            Width = Dim.Fill (22),
            Text = UserSettings.ScriptsPath
        };

        Button load = new () { Text = "_Load", X = Pos.Right (folder) + 1, Y = 0 };

        Button cancel = new () { Text = "_Cancel", X = Pos.Right (load) + 1, Y = 0 };

        ScriptHistoryWidget history = new (y: Pos.Bottom (label));

        ScriptsWidget scripts = new ()
        {
            X = 0,
            Y = Pos.Bottom (history),
            Width = Dim.Percent (30),
            Height = Dim.Fill ()
        };

        TerminalWidget terminal = new ()
        {
            X = Pos.Right (scripts) + 1,
            Y = Pos.Bottom (history),
            Width = Dim.Fill (),
            Height = Dim.Fill ()
        };

        scripts.Output += terminal.Write;

        load.Accepted += (_, _) => scripts.Show (folder.Text);
        folder.Accepted += (_, _) => scripts.Show (folder.Text);

        cancel.Accepted += (_, _) =>
        {
            scripts.CancelAll ();
            terminal.Clear ();
        };

        Add (label, folder, load, cancel, history, scripts, terminal);
    }
}

