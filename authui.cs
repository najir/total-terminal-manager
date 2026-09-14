// -----------------------------------------------------------------------------
//  The dialogs that ask for a passphrase or a credential. Kept apart from auth.cs
//  so the store itself stays free of any UI dependency.
// -----------------------------------------------------------------------------

/// <summary>
///     Asks for the passphrase, and for credentials when none are stored yet.
///
///     Nothing here caches a key: every call that needs one asks again. Stretching the
///     passphrase costs a fraction of a second, which is the point of the iteration count.
/// </summary>
internal static class AuthPrompt
{
    public const int MinimumLength = 8;

    private const int Wide = 68;

    /// <summary>
    ///     A key for the stored credentials, asking for the passphrase and creating one first if
    ///     the user has never set one. Null means the user cancelled.
    /// </summary>
    public static AuthKey? Unlock (View owner)
    {
        if (!AuthStore.HasPassphrase)
        {
            return Create (owner);
        }

        Dialog dialog = new () { Title = "Passphrase", Width = Wide, Height = 9 };

        Label prompt = new ()
        {
            Text = "Enter your passphrase to unlock the stored credentials.",
            X = 1,
            Y = 0,
            Width = Dim.Fill (1)
        };

        Label label = new () { Text = "Passphrase", X = 1, Y = Pos.Bottom (prompt) + 1 };
        TextField field = new () { X = Pos.Right (label) + 2, Y = Pos.Top (label), Width = Dim.Fill (2), Secret = true };

        Label problem = new () { Text = "", X = 1, Y = Pos.Bottom (label) + 1, Width = Dim.Fill (1) };

        AuthKey? key = null;

        Button cancel = new () { Text = "_Cancel" };
        Button unlock = new () { Text = "_Unlock" };

        unlock.Accepting += (_, e) =>
        {
            key = AuthStore.Unlock (field.Text);

            if (key is null)
            {
                // A wrong passphrase is a typo far more often than a forgotten one, so it
                // retries here. Clearing the credentials is only ever reached deliberately,
                // through Forget on the passphrase page.
                e.Handled = true;
                problem.Text = AuthStore.Error ?? "that passphrase does not match";
                field.Text = "";
                field.SetFocus ();
            }
        };

        dialog.Add (prompt, label, field, problem);
        dialog.AddButton (cancel);
        dialog.AddButton (unlock);

        field.SetFocus ();

        owner.App!.Run (dialog);
        dialog.Dispose ();

        return key;
    }

    /// <summary>First run: choose a passphrase, then hand back the key it derives.</summary>
    private static AuthKey? Create (View owner)
    {
        Dialog dialog = new () { Title = "Set a passphrase", Width = Wide, Height = 12 };

        Label prompt = new ()
        {
            Text = "Credentials are encrypted with this passphrase. There is no way to\n"
                   + "recover it - if you lose it, the stored credentials are lost with it.",
            X = 1,
            Y = 0,
            Width = Dim.Fill (1),
            Height = 2
        };

        Label label = new () { Text = "Passphrase", X = 1, Y = Pos.Bottom (prompt) + 1 };
        TextField field = new () { X = Pos.Right (label) + 2, Y = Pos.Top (label), Width = Dim.Fill (2), Secret = true };

        Label againLabel = new () { Text = "Again     ", X = 1, Y = Pos.Bottom (label) };
        TextField again = new () { X = Pos.Right (againLabel) + 2, Y = Pos.Top (againLabel), Width = Dim.Fill (2), Secret = true };

        Label problem = new () { Text = "", X = 1, Y = Pos.Bottom (againLabel) + 1, Width = Dim.Fill (1) };

        AuthKey? key = null;

        Button cancel = new () { Text = "_Cancel" };
        Button set = new () { Text = "_Set passphrase" };

        set.Accepting += (_, e) =>
        {
            if (Rejected (field.Text, again.Text) is { } reason)
            {
                e.Handled = true;
                problem.Text = reason;

                return;
            }

            if (!AuthStore.Create (field.Text))
            {
                e.Handled = true;
                problem.Text = AuthStore.Error ?? "could not write the credentials file";

                return;
            }

            key = AuthStore.Unlock (field.Text);
        };

        dialog.Add (prompt, label, field, againLabel, again, problem);
        dialog.AddButton (cancel);
        dialog.AddButton (set);

        field.SetFocus ();

        owner.App!.Run (dialog);
        dialog.Dispose ();

        return key;
    }

    /// <summary>Why this pair of entries is not usable as a passphrase, or null when it is.</summary>
    private static string? Rejected (string passphrase, string again) =>
        passphrase.Length < MinimumLength ? $"a passphrase needs at least {MinimumLength} characters"
        : passphrase != again ? "the two entries do not match"
        : null;

    /// <summary>
    ///     Asks for the two halves of a credential. Deliberately generic - the caller supplies the
    ///     wording, so the same dialog serves a git token today and something else later.
    /// </summary>
    public static (string User, string Secret)? AskCredential (
        View owner,
        string title,
        string message,
        string userLabel,
        string secretLabel,
        string user = "")
    {
        Dialog dialog = new () { Title = title, Width = Wide, Height = 12 };

        Label prompt = new () { Text = message, X = 1, Y = 0, Width = Dim.Fill (1), Height = 2 };

        Label nameLabel = new () { Text = userLabel, X = 1, Y = Pos.Bottom (prompt) + 1 };
        TextField name = new () { X = 16, Y = Pos.Top (nameLabel), Width = Dim.Fill (2), Text = user };

        Label secretLabelView = new () { Text = secretLabel, X = 1, Y = Pos.Bottom (nameLabel) };
        TextField secret = new () { X = 16, Y = Pos.Top (secretLabelView), Width = Dim.Fill (2), Secret = true };

        Label problem = new () { Text = "", X = 1, Y = Pos.Bottom (secretLabelView) + 1, Width = Dim.Fill (1) };

        (string, string)? result = null;

        Button cancel = new () { Text = "_Cancel" };
        Button save = new () { Text = "_Save" };

        save.Accepting += (_, e) =>
        {
            if (secret.Text.Trim ().Length == 0)
            {
                e.Handled = true;
                problem.Text = secretLabel.Trim ().ToLowerInvariant () + " is required";

                return;
            }

            result = (name.Text.Trim (), secret.Text.Trim ());
        };

        dialog.Add (prompt, nameLabel, name, secretLabelView, secret, problem);
        dialog.AddButton (cancel);
        dialog.AddButton (save);

        (user.Length == 0 ? name : secret).SetFocus ();

        owner.App!.Run (dialog);
        dialog.Dispose ();

        return result;
    }

    /// <summary>
    ///     The passphrase page behind the Preferences button. Changing it with the old passphrase
    ///     re-encrypts every stored credential and keeps them; Forget skips that check and destroys
    ///     them, because without the old passphrase no key exists that could read them.
    /// </summary>
    public static void Manage (View owner)
    {
        if (!AuthStore.HasPassphrase)
        {
            Create (owner)?.Dispose ();

            return;
        }

        Dialog dialog = new () { Title = "Passphrase", Width = Wide, Height = 15 };

        int stored = AuthStore.Count;

        Label prompt = new ()
        {
            Text = $"{stored} credential(s) are encrypted with your current passphrase.\n"
                   + "Changing it keeps them. Forgetting it destroys all of them.",
            X = 1,
            Y = 0,
            Width = Dim.Fill (1),
            Height = 2
        };

        Label oldLabel = new () { Text = "Current", X = 1, Y = Pos.Bottom (prompt) + 1 };
        TextField old = new () { X = 12, Y = Pos.Top (oldLabel), Width = Dim.Fill (2), Secret = true };

        Label newLabel = new () { Text = "New", X = 1, Y = Pos.Bottom (oldLabel) + 1 };
        TextField fresh = new () { X = 12, Y = Pos.Top (newLabel), Width = Dim.Fill (2), Secret = true };

        Label againLabel = new () { Text = "Again", X = 1, Y = Pos.Bottom (newLabel) };
        TextField again = new () { X = 12, Y = Pos.Top (againLabel), Width = Dim.Fill (2), Secret = true };

        Label problem = new () { Text = "", X = 1, Y = Pos.Bottom (againLabel) + 1, Width = Dim.Fill (1) };

        Button forget = new () { Text = "_Forgot it" };
        Button cancel = new () { Text = "_Cancel" };
        Button change = new () { Text = "C_hange" };

        change.Accepting += (_, e) =>
        {
            if (Rejected (fresh.Text, again.Text) is { } reason)
            {
                e.Handled = true;
                problem.Text = reason;

                return;
            }

            if (!AuthStore.Change (old.Text, fresh.Text))
            {
                e.Handled = true;
                problem.Text = AuthStore.Error ?? "could not change the passphrase";
                old.Text = "";
                old.SetFocus ();
            }
        };

        forget.Accepting += (_, e) =>
        {
            if (Rejected (fresh.Text, again.Text) is { } reason)
            {
                e.Handled = true;
                problem.Text = "fill in the new passphrase twice first - " + reason;

                return;
            }

            int? choice = MessageBox.Query (
                                            owner.App!,
                                            "Forget passphrase",
                                            $"This destroys all {stored} stored credential(s)."
                                            + Environment.NewLine
                                            + Environment.NewLine
                                            + "They cannot be recovered without the old passphrase. You will be"
                                            + Environment.NewLine
                                            + "asked for each token again the next time it is needed."
                                            + Environment.NewLine
                                            + Environment.NewLine
                                            + "This cannot be undone.",
                                            new [] { "_Cancel", "_Destroy credentials" });

            if (choice != 1)
            {
                e.Handled = true;

                return;
            }

            if (!AuthStore.Reset (fresh.Text))
            {
                e.Handled = true;
                problem.Text = AuthStore.Error ?? "could not reset the passphrase";
            }
        };

        dialog.Add (prompt, oldLabel, old, newLabel, fresh, againLabel, again, problem);
        dialog.AddButton (forget);
        dialog.AddButton (cancel);
        dialog.AddButton (change);

        old.SetFocus ();

        owner.App!.Run (dialog);
        dialog.Dispose ();
    }
}
