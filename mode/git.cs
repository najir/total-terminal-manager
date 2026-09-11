// -----------------------------------------------------------------------------
//  The Git tab: push history.
// -----------------------------------------------------------------------------

internal sealed class GitWindow : Window
{
    public GitWindow (Pos x, Pos y)
    {
        Title = "Git";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        GitContributionsWidget contributions = new ();

        Add (
             contributions,
             new GitHistoryWidget (
                                   amount: GitHistoryWidget.DefaultAmount,
                                   y: Pos.Bottom (contributions),
                                   width: Dim.Fill (),
                                   height: Dim.Fill ()));
    }
}

