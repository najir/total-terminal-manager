// -----------------------------------------------------------------------------
//  Settings pages and the menu items that switch to them.
// -----------------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Text.Json;

internal static class Settings
{
    public static List<View> Pages (Pos x, Pos y) =>
        new () { new ThemePage (x, y), new PreferencesPage (x, y) };

    public static IEnumerable<MenuItem> MenuItems (List<View> all, IEnumerable<View> pages) =>
        pages.Select (
                      page => new MenuItem
                      {
                          Title = page.Title,
                          Action = () =>
                                   {
                                       Handler.ResetVisibility (all);
                                       page.Visible = true;
                                       page.SetFocus();
                                   }
                      });
}

/// <summary>Pick a theme. Enter on a row applies it to the whole app at once.</summary>
internal sealed class ThemePage : Window
{
    public ThemePage (Pos x, Pos y)
    {
        Title = "Theme";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        ListView list = new () { Width = Dim.Fill (), Height = Dim.Fill () };
        list.SetSource (new ObservableCollection<string> (Theme.Names));

        int current = Theme.Names.ToList ().IndexOf (Theme.Current);

        if (current >= 0)
        {
            list.SelectedItem = current;
        }

        list.Accepted += (_, _) =>
        {
            if (list.SelectedItem is { } row && row >= 0 && row < Theme.Names.Count)
            {
                Theme.Current = Theme.Names [row];
            }
        };

        Add (list);
    }
}

/// <summary>
///     Everything that writes the user-settings file. Each control reads its current value from
///     UserSettings and writes straight back on change — there is no Save button, and nothing to
///     keep in sync.
/// </summary>
internal sealed class PreferencesPage : Window
{
    private static readonly string[] EmailProviders = { "microsoft", "gmail" };

    public PreferencesPage (Pos x, Pos y)
    {
        Title = "Preferences";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        Label emailLabel = new () { Text = "Email provider", X = 0, Y = 0 };

        OptionSelector email = new ()
        {
            X = 0,
            Y = Pos.Bottom (emailLabel),
            Labels = EmailProviders,
            Value = Array.IndexOf (EmailProviders, UserSettings.Get (UserSettings.EmailProvider))
        };

        email.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } row && row >= 0 && row < EmailProviders.Length)
            {
                UserSettings.Set (UserSettings.EmailProvider, EmailProviders [row]);
            }
        };

        Label widgetsLabel = new () { Text = "Dashboard widgets", X = 0, Y = Pos.Bottom (email) + 1 };

        CheckBox teams = Toggle ("_Teams", UserSettings.ShowTeams, 0, Pos.Bottom (widgetsLabel));
        CheckBox mail = Toggle ("_Mail", UserSettings.ShowMail, 0, Pos.Bottom (teams));

        Label aiLabel = new () { Text = "AI default backend", X = 0, Y = Pos.Bottom (mail) + 1 };

        OptionSelector aiProvider = new ()
        {
            X = 0,
            Y = Pos.Bottom (aiLabel),
            Labels = ChatProvider.Names,
            Value = Math.Max (0, Array.IndexOf (ChatProvider.Names, UserSettings.Get (UserSettings.AiProvider)))
        };

        Label aiUrlLabel = new () { Text = "URL", X = 0, Y = Pos.Bottom (aiProvider) };
        TextField aiUrl = new () { X = 8, Y = Pos.Top (aiUrlLabel), Width = 44, Text = UserSettings.Get (UserSettings.AiUrl) };

        Label aiModelLabel = new () { Text = "Model", X = 0, Y = Pos.Bottom (aiUrlLabel) };
        TextField aiModel = new () { X = 8, Y = Pos.Top (aiModelLabel), Width = 44, Text = UserSettings.Get (UserSettings.AiModel) };

        aiProvider.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } row && row >= 0 && row < ChatProvider.Names.Length)
            {
                UserSettings.Set (UserSettings.AiProvider, ChatProvider.Names [row]);
                aiUrl.Text = ChatProvider.DefaultUrls.GetValueOrDefault (ChatProvider.Names [row], "");
                UserSettings.Set (UserSettings.AiUrl, aiUrl.Text);
            }
        };

        aiUrl.TextChanged += (_, _) => UserSettings.Set (UserSettings.AiUrl, aiUrl.Text);
        aiModel.TextChanged += (_, _) => UserSettings.Set (UserSettings.AiModel, aiModel.Text);

        Label nextUpLabel = new () { Text = "Next up (LM Studio)", X = 0, Y = Pos.Bottom (aiModelLabel) + 1 };

        Label nextUpModelLabel = new () { Text = "Model", X = 0, Y = Pos.Bottom (nextUpLabel) };
        TextField nextUpModel = new () { X = 8, Y = Pos.Top (nextUpModelLabel), Width = 44, Text = UserSettings.Get (UserSettings.NextUpModel) };

        nextUpModel.TextChanged += (_, _) => UserSettings.Set (UserSettings.NextUpModel, nextUpModel.Text);

        Label gitLabel = new () { Text = "Git history source", X = 0, Y = Pos.Bottom (nextUpModelLabel) + 1 };

        OptionSelector gitProvider = new ()
        {
            X = 0,
            Y = Pos.Bottom (gitLabel),
            Labels = UserSettings.GitProviders,
            Value = Math.Max (0, Array.IndexOf (UserSettings.GitProviders, UserSettings.Get (UserSettings.GitProvider)))
        };

        gitProvider.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } row && row >= 0 && row < UserSettings.GitProviders.Length)
            {
                UserSettings.Set (UserSettings.GitProvider, UserSettings.GitProviders [row]);
            }
        };

        Label rootsLabel = new () { Text = "AI file access", X = 0, Y = Pos.Bottom (gitProvider) + 1 };

        Label rootsValue = new ()
        {
            X = 0,
            Y = Pos.Bottom (rootsLabel),
            Width = Dim.Fill (),
            Height = 1
        };

        void ShowRoots ()
        {
            string[] roots = FileTools.Roots ();

            rootsValue.Text = roots.Length == 0
                                  ? "no roots - the chat can read nothing (add file-roots below)"
                                  : $"roots: {string.Join ("  ", roots)}";
        }

        ShowRoots ();

        Button editSettings = new () { Text = "_Edit settings file", X = 0, Y = Pos.Bottom (rootsValue) };

        editSettings.Accepted += (_, _) =>
        {
            EditSettingsFile ();
            ShowRoots ();
        };

        Label linksLabel = new () { Text = "Links (name and URL)", X = 0, Y = Pos.Bottom (editSettings) + 1 };

        ListView linkList = new () { X = 0, Y = Pos.Bottom (linksLabel), Width = 60, Height = 5 };

        Label nameLabel = new () { Text = "Name", X = 0, Y = Pos.Bottom (linkList) };
        TextField linkName = new () { X = 8, Y = Pos.Top (nameLabel), Width = 20 };

        Label urlLabel = new () { Text = "URL", X = 0, Y = Pos.Bottom (nameLabel) };
        TextField linkUrl = new () { X = 8, Y = Pos.Top (urlLabel), Width = 44 };

        Button addLink = new () { Text = "_Add", X = Pos.Right (linkUrl) + 1, Y = Pos.Top (urlLabel) };
        Button removeLink = new () { Text = "_Remove", X = Pos.Right (addLink) + 1, Y = Pos.Top (urlLabel) };

        void ShowLinks ()
        {
            Dictionary<string, string> links = UserSettings.GetTable (UserSettings.Links);

            linkList.SetSource (
                                new ObservableCollection<string> (
                                                                  links.Count == 0
                                                                      ? new [] { "(none)" }
                                                                      : links.Select (l => $"{l.Key}  ->  {l.Value}").ToArray ()));
        }

        addLink.Accepted += (_, _) =>
        {
            string name = linkName.Text.Trim ();
            string url = linkUrl.Text.Trim ();

            if (name.Length == 0 || url.Length == 0)
            {
                return;
            }

            Dictionary<string, string> links = UserSettings.GetTable (UserSettings.Links);

            links [name] = url;

            UserSettings.SetTable (UserSettings.Links, links);
            linkName.Text = "";
            linkUrl.Text = "";
            ShowLinks ();
        };

        removeLink.Accepted += (_, _) =>
        {
            Dictionary<string, string> links = UserSettings.GetTable (UserSettings.Links);

            if (linkList.SelectedItem is { } row && row >= 0 && row < links.Count)
            {
                links.Remove (links.Keys.ElementAt (row));
                UserSettings.SetTable (UserSettings.Links, links);
                ShowLinks ();
            }
        };

        ShowLinks ();

        Label path = new ()
        {
            Text = $"Saved to {UserSettings.FilePath}",
            X = 0,
            Y = Pos.Bottom (urlLabel) + 1
        };

        Add (emailLabel, email, widgetsLabel, teams, mail,
             aiLabel, aiProvider, aiUrlLabel, aiUrl, aiModelLabel, aiModel, nextUpLabel, nextUpModelLabel, nextUpModel,
             gitLabel, gitProvider, rootsLabel, rootsValue, editSettings,
             linksLabel, linkList, nameLabel, linkName, urlLabel, linkUrl, addLink, removeLink, path);
    }

    private static CheckBox Toggle (string text, string key, Pos x, Pos y)
    {
        CheckBox box = new ()
        {
            Text = text,
            X = x,
            Y = y,
            Value = UserSettings.On (key) ? CheckState.Checked : CheckState.UnChecked
        };

        box.ValueChanged += (_, e) => UserSettings.Set (key, e.NewValue == CheckState.Checked);

        return box;
    }

    private void EditSettingsFile ()
    {
        string path = UserSettings.FilePath;
        string text;

        try
        {
            text = File.ReadAllText (path);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery (App!, "Cannot open", $"{path}\n\n{ex.Message}", new [] { "_Ok" });

            return;
        }

        Dialog dialog = new () { Title = path, Width = Dim.Percent (85), Height = Dim.Percent (85) };

        Editor editor = new ()
        {
            Width = Dim.Fill (),
            Height = Dim.Fill (1),
            GutterOptions = GutterOptions.LineNumbers,
            Text = text
        };

        editor.AddEditingKeys ();

        Button save = new () { Text = "_Save" };
        Button close = new () { Text = "_Close" };

        save.Accepting += (_, e) =>
        {
            try
            {
                if (!UserSettings.CanLoad (editor.Text, out string problem))
                {
                    e.Handled = true;
                    MessageBox.ErrorQuery (App!, "Not valid settings", $"Nothing was saved.\n\n{problem}", new [] { "_Ok" });

                    return;
                }

                File.WriteAllText (path, editor.Text);

                UserSettings.Load ();

            }
            catch (Exception ex)
            {
                e.Handled = true;
                MessageBox.ErrorQuery (App!, "Cannot save", $"{path}\n\n{ex.Message}", new [] { "_Ok" });
            }
        };

        dialog.AddButton (save);
        dialog.AddButton (close);
        dialog.Add (editor);

        App!.Run (dialog);
        dialog.Dispose ();
    }
}

