// -----------------------------------------------------------------------------
//  The AI tab: a backend picker across the top, a chat box below.
// -----------------------------------------------------------------------------

using System.Drawing;
internal sealed class AiWindow : Window
{
    private const int MinContentWidth = 145;

    private const int SkillsHeight = 8;

    public AiWindow (Pos x, Pos y)
    {
        Title = "AI";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        Label providerLabel = new () { Text = "Provider:", X = 0, Y = 0 };

        OptionSelector provider = new ()
        {
            X = Pos.Right (providerLabel) + 1,
            Y = 0,
            Labels = ChatProvider.Names,
            Value = Math.Max (0, Array.IndexOf (ChatProvider.Names, UserSettings.Get (UserSettings.AiProvider)))
        };

        Label urlLabel = new () { Text = "URL:", X = Pos.Right (provider) + 2, Y = 0 };
        TextField url = new () { X = Pos.Right (urlLabel) + 1, Y = 0, Width = 34, Text = UserSettings.Get (UserSettings.AiUrl) };

        Label modelLabel = new () { Text = "Model:", X = Pos.Right (url) + 2, Y = 0 };
        TextField model = new () { X = Pos.Right (modelLabel) + 1, Y = 0, Width = 26, Text = UserSettings.Get (UserSettings.AiModel) };

        Label keyLabel = new () { Text = "Key:", X = Pos.Right (model) + 2, Y = 0 };

        TextField key = new () { X = Pos.Right (keyLabel) + 1, Y = 0, Width = 24, Secret = true };

        Button apply = new () { Text = "_Apply", X = Pos.Right (key) + 1, Y = 0 };

        ChatWidget chat = new (
                               y: Pos.Bottom (provider) + 1,
                               width: Dim.Percent (50),
                               height: Dim.Fill (SkillsHeight));

        provider.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } row && row >= 0 && row < ChatProvider.Names.Length)
            {
                url.Text = ChatProvider.DefaultUrls.GetValueOrDefault (ChatProvider.Names [row], "");
            }
        };

        void Apply ()
        {
            int row = provider.Value ?? 0;

            chat.Use (
                      ChatProvider.Names [Math.Clamp (row, 0, ChatProvider.Names.Length - 1)],
                      url.Text,
                      model.Text,
                      key.Text);
            chat.SetFocus ();
        }

        apply.Accepted += (_, _) => Apply ();
        model.Accepted += (_, _) => Apply ();
        key.Accepted += (_, _) => Apply ();

        SkillsWidget skills = new ()
        {
            X = Pos.Left (chat),
            Y = Pos.Bottom (chat),
            Width = Dim.Width (chat),
            Height = SkillsHeight
        };

        skills.Picked += path => chat.AddSkill (path);

        ReviewHistoryWidget reviews = new (x: Pos.Right (chat) + 1, y: Pos.Top (chat));

        Add (providerLabel, provider, urlLabel, url, modelLabel, model, keyLabel, key, apply, chat, skills, reviews);

        HorizontalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

        ViewportChanged += (_, _) =>
        {
            Size wanted = new (Math.Max (MinContentWidth, Viewport.Width), Viewport.Height);

            if (GetContentSize () != wanted)
            {
                SetContentSize (wanted);
            }
        };
    }
}

