// -----------------------------------------------------------------------------
//  Data sync: replicates the total-manager folder through a git remote.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Mode;

/// <summary>How a remote is reached, which decides whether a credential is needed at all.</summary>
internal enum RemoteKind
{
    Unset,
    Https,
    Http,
    Ssh,
    Local
}

/// <summary>The two halves of an http credential, as stored under one AuthStore id.</summary>
internal sealed record GitCredential
{
    [JsonPropertyName ("user")] public string User { get; init; } = "";

    [JsonPropertyName ("token")] public string Token { get; init; } = "";

    public string ToJson () => JsonSerializer.Serialize (this);

    // A record would otherwise print every property, so any stray interpolation of one of these
    // would put the token on screen or in a log.
    public override string ToString () => $"GitCredential {{ User = {User}, Token = <redacted> }}";

    public static GitCredential? FromJson (string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GitCredential> (json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The credential embedded in a pasted URL, if there is one. A single value with no colon
    ///     is taken as the token: that is the token-as-username form GitHub accepts, and GitAsync
    ///     supplies the placeholder username for it.
    /// </summary>
    public static GitCredential? InUrl (string raw)
    {
        if (!Uri.TryCreate (raw.Trim (), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length == 0)
        {
            return null;
        }

        string info = Uri.UnescapeDataString (uri.UserInfo);
        int split = info.IndexOf (':');

        return split < 0
                   ? new GitCredential { Token = info }
                   : new GitCredential { User = info [..split], Token = info [(split + 1)..] };
    }
}

/// <summary>
///     Credentials lifted out of a pasted URL, waiting for a passphrase to encrypt them with.
///
///     Preferences cannot write to the store on its own: that needs the passphrase, and asking for
///     it on every keystroke would be intolerable. So a pasted token is held here, in memory only,
///     and the next sync - which asks for the passphrase anyway - commits it to the store. It is
///     never written to disk in this form, and it does not survive a restart.
/// </summary>
internal static class PendingCredential
{
    private static readonly Dictionary<string, GitCredential> Waiting = new (StringComparer.Ordinal);

    public static void Capture (string authId, GitCredential credential)
    {
        lock (Waiting)
        {
            Waiting [authId] = credential;
        }
    }

    /// <summary>Hands over the captured credential and forgets it, so it is used exactly once.</summary>
    public static GitCredential? Take (string authId)
    {
        lock (Waiting)
        {
            if (!Waiting.Remove (authId, out GitCredential? credential))
            {
                return null;
            }

            return credential;
        }
    }

    public static bool Holds (string authId)
    {
        lock (Waiting)
        {
            return Waiting.ContainsKey (authId);
        }
    }
}

/// <summary>
///     A parsed sync remote. Only the two http forms authenticate: ssh rides whatever keys and
///     agent the user already has, and a local or UNC path has no transport to authenticate to.
/// </summary>
internal sealed record SyncRemote (RemoteKind Kind, string Url, string Scheme, string Host)
{
    /// <summary>
    ///     True when a user:password was removed from the pasted URL. Embedding the token in the
    ///     URL is the form most git documentation shows, and it is the one form that must never be
    ///     kept: the settings file it would be saved in is itself inside the synced folder, so the
    ///     token would be pushed to the remote in plaintext on the next Update.
    /// </summary>
    public bool Stripped { get; init; }

    public bool NeedsAuth => Kind is RemoteKind.Https or RemoteKind.Http;

    /// <summary>The config scope, so a header is only ever sent to the host it belongs to.</summary>
    public string Origin => $"{Scheme}://{Host}/";

    /// <summary>The AuthStore id for this host - namespaced, so the store is not git-only.</summary>
    public string AuthId => "git:" + Host;

    public string Describe () => Kind switch
                                 {
                                     RemoteKind.Unset => "no remote set",
                                     RemoteKind.Local => $"{Url} (local path, no sign-in)",
                                     RemoteKind.Ssh => $"{Url} (ssh, uses your existing keys)",
                                     RemoteKind.Http => $"{Url} (plain http - the token is sent unencrypted)",
                                     _ => Url
                                 };

    public static SyncRemote Parse (string raw)
    {
        string url = raw.Trim ();

        if (url.Length == 0)
        {
            return new (RemoteKind.Unset, "", "", "");
        }

        // git@host:owner/repo.git - the scp-like form, which is not a URI at all.
        if (!url.Contains ("://", StringComparison.Ordinal) && url.Contains ('@') && url.Contains (':'))
        {
            string after = url [(url.IndexOf ('@') + 1)..];

            return new (RemoteKind.Ssh, url, "ssh", after [..after.IndexOf (':')]);
        }

        // A leading slash is an absolute posix path, which Uri would not read as one on Windows.
        if (url [0] == '/')
        {
            return new (RemoteKind.Local, url, "", "");
        }

        if (!Uri.TryCreate (url, UriKind.Absolute, out Uri? uri))
        {
            return new (RemoteKind.Local, url, "", "");
        }

        // A windows path and a UNC share both parse as file, which is the local case.
        return uri.Scheme switch
               {
                   "https" => Http (RemoteKind.Https, uri, "https"),
                   "http" => Http (RemoteKind.Http, uri, "http"),
                   "ssh" => new (RemoteKind.Ssh, url, "ssh", uri.Host),
                   _ => new (RemoteKind.Local, url, "", "")
               };
    }

    /// <summary>
    ///     An http remote with any embedded credential removed, so what gets saved, logged, shown
    ///     in the confirm dialog and handed to git carries no secret. The token belongs in the
    ///     encrypted store, which is outside the synced folder.
    /// </summary>
    private static SyncRemote Http (RemoteKind kind, Uri uri, string scheme)
    {
        if (uri.UserInfo.Length == 0)
        {
            return new (kind, uri.OriginalString, scheme, uri.Host);
        }

        UriBuilder clean = new (uri) { UserName = "", Password = "" };

        return new (kind, clean.Uri.ToString (), scheme, uri.Host) { Stripped = true };
    }
}

/// <summary>
///     Replicates the total-manager folder through a git remote, in one direction at a time.
///
///     Neither direction merges. Download makes the folder match the remote and Update makes the
///     remote match the folder, so whichever button you press wins outright. That is the whole
///     design: a note is a file, and a file has one current version.
///
///     The credential never reaches argv or .git/config. It goes in as a scoped http header
///     through the child environment, so it is not visible in a process list and not left behind
///     in the repository afterwards.
/// </summary>
internal static class DataSync
{
    /// <summary>Lines that must be in .gitignore for the sync to behave.</summary>
    private static readonly string[] Ignored =
    {
        UserSettings.CacheFolderName + "/",
        UserSettings.BackupFolderName + "/",
        "*.db-wal",
        "*.db-shm"
    };

    // A vault synced between Windows and Linux must never have its line endings rewritten in
    // transit, or every note looks modified on the other machine.
    private const string Attributes = "* -text\n";

    public static string? Error { get; private set; }

    public static SyncRemote Remote () => SyncRemote.Parse (UserSettings.Get (UserSettings.SyncRemote));

    public static string Branch ()
    {
        string branch = UserSettings.Get (UserSettings.SyncBranch).Trim ();

        return branch.Length == 0 ? "main" : branch;
    }

    /// <summary>
    ///     Makes the folder a git repository if it is not one yet, and asserts the settings the
    ///     sync depends on. Safe to run before every operation.
    /// </summary>
    public static async Task<bool> PrepareAsync (SyncRemote remote, Action<string> log)
    {
        Error = null;

        if (remote.Kind == RemoteKind.Unset)
        {
            Error = "no sync remote is set - add one in Settings > User";

            return false;
        }

        // Every git call runs with this as its working directory, so it has to exist before the
        // first one - otherwise a missing folder reports itself as a missing git.
        Directory.CreateDirectory (UserSettings.FolderPath);

        if (await GitAsync (["--version"], remote, null, log) is not (0, _))
        {
            Error = "git is not installed, or not on PATH";

            return false;
        }

        if (!Directory.Exists (Path.Combine (UserSettings.FolderPath, ".git"))
            && await GitAsync (["init", "-b", Branch ()], remote, null, log) is not (0, _))
        {
            Error = "could not make the folder a git repository";

            return false;
        }

        WriteIgnore (log);

        // Repo-local only: the sync never touches the global git config.
        await GitAsync (["config", "core.autocrlf", "false"], remote, null, log);

        return true;
    }

    /// <summary>
    ///     Adds the lines the sync needs to .gitignore without disturbing anything already there,
    ///     and pins the line-ending rule in .gitattributes.
    /// </summary>
    private static void WriteIgnore (Action<string> log)
    {
        try
        {
            string ignore = Path.Combine (UserSettings.FolderPath, ".gitignore");

            List<string> lines = File.Exists (ignore)
                                     ? File.ReadAllLines (ignore).ToList ()
                                     : new List<string> ();

            string[] missing = Ignored.Where (i => !lines.Any (l => l.Trim () == i)).ToArray ();

            if (missing.Length > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add ("");
                }

                lines.Add ("# ttm data sync");
                lines.AddRange (missing);

                File.WriteAllText (ignore, string.Join ("\n", lines) + "\n");
                log ($"[sync] .gitignore += {string.Join (" ", missing)}");
            }

            string attributes = Path.Combine (UserSettings.FolderPath, ".gitattributes");

            if (!File.Exists (attributes) || !File.ReadAllText (attributes).Contains ("* -text", StringComparison.Ordinal))
            {
                File.WriteAllText (attributes, Attributes);
                log ("[sync] .gitattributes pinned to * -text");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log ("[sync] could not write .gitignore: " + ex.Message);
        }
    }

    /// <summary>
    ///     Blanks any user:password@host that reached a line bound for the screen or the
    ///     transcript. Parse already strips it from the remote, so this is the second line of
    ///     defence for whatever git chooses to echo back.
    /// </summary>
    private static string Redact (string line) => UserInfo.Replace (line, "://<redacted>@");

    private static readonly Regex UserInfo = new (@"://[^/@\s]+@", RegexOptions.Compiled, TimeSpan.FromSeconds (1));

    /// <summary>
    ///     Runs one git command in the total-manager folder.
    ///
    ///     The credential is injected through GIT_CONFIG_COUNT rather than the remote URL, so it
    ///     never lands in argv and never gets written into .git/config. The header is scoped to
    ///     the remote origin, so git will not send it anywhere else.
    /// </summary>
    private static async Task<(int Code, string Output)> GitAsync (
        string[] arguments,
        SyncRemote remote,
        GitCredential? credential,
        Action<string> log)
    {
        ProcessStartInfo info = new ("git")
        {
            WorkingDirectory = UserSettings.FolderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Never block on a credential prompt - fail and report instead.
        info.Environment ["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment ["GIT_ASKPASS"] = "echo";

        List<(string Key, string Value)> config = new () { ("credential.helper", "") };

        if (remote.NeedsAuth && credential is not null)
        {
            string user = credential.User.Length == 0 ? "x-access-token" : credential.User;
            string basic = Convert.ToBase64String (Encoding.UTF8.GetBytes (user + ":" + credential.Token));

            config.Add (($"http.{remote.Origin}.extraHeader", "Authorization: Basic " + basic));

            if (UserSettings.On (UserSettings.SyncInsecureTls))
            {
                config.Add (($"http.{remote.Origin}.sslVerify", "false"));
            }
        }

        info.Environment ["GIT_CONFIG_COUNT"] = config.Count.ToString ();

        for (int i = 0; i < config.Count; i++)
        {
            info.Environment [$"GIT_CONFIG_KEY_{i}"] = config [i].Key;
            info.Environment [$"GIT_CONFIG_VALUE_{i}"] = config [i].Value;
        }

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add (argument);
        }

        log ("> git " + Redact (string.Join (" ", arguments)));

        StringBuilder output = new ();

        try
        {
            using Process process = Process.Start (info)!;

            void Line (string? data)
            {
                if (data is not null)
                {
                    output.AppendLine (data);
                    log ("  " + Redact (data));
                }
            }

            process.OutputDataReceived += (_, e) => Line (e.Data);
            process.ErrorDataReceived += (_, e) => Line (e.Data);
            process.BeginOutputReadLine ();
            process.BeginErrorReadLine ();

            await process.WaitForExitAsync ();

            return (process.ExitCode, output.ToString ());
        }
        catch (Exception ex)
        {
            log ("  could not run git: " + ex.Message);

            return (-1, ex.Message);
        }
    }

    /// <summary>
    ///     Makes the folder match the remote, discarding local changes. A copy of the folder is
    ///     taken first, because the reset and the clean together are not reversible.
    /// </summary>
    public static async Task<bool> DownloadAsync (SyncRemote remote, GitCredential? credential, Action<string> log)
    {
        if (!await PrepareAsync (remote, log))
        {
            return false;
        }

        // Pooled handles hold the database file open, and it is about to be replaced underneath
        // the running app.
        SqliteConnection.ClearAllPools ();

        if (Backup (log) is null)
        {
            Error = "could not take a backup, so nothing was overwritten";

            return false;
        }

        if ((await GitAsync (["fetch", remote.Url, Branch ()], remote, credential, log)).Code != 0)
        {
            Error = $"could not fetch {Branch ()} from the remote";

            return false;
        }

        if ((await GitAsync (["reset", "--hard", "FETCH_HEAD"], remote, credential, log)).Code != 0)
        {
            Error = "fetched, but could not reset the folder to it";

            return false;
        }

        // Removes local files the remote does not have. Honours .gitignore, so the cache and the
        // backup just taken both survive.
        await GitAsync (["clean", "-fd"], remote, credential, log);

        DropStaleSidecars (log);
        Reopen (log);

        return true;
    }

    /// <summary>Makes the remote match the folder, discarding whatever was there.</summary>
    public static async Task<bool> UpdateAsync (SyncRemote remote, GitCredential? credential, Action<string> log)
    {
        if (!await PrepareAsync (remote, log))
        {
            return false;
        }

        Checkpoint (log);

        await GitAsync (["add", "-A"], remote, credential, log);

        // A commit with nothing staged exits non-zero, which is success here.
        (int code, string output) = await GitAsync (
                                                   ["-c", "user.name=ttm", "-c", "user.email=ttm@localhost",
                                                    "commit", "-m", $"ttm sync {DateTime.Now:yyyy-MM-dd HH:mm:ss} from {Environment.MachineName}"],
                                                   remote,
                                                   credential,
                                                   log);

        bool nothing = output.Contains ("nothing to commit", StringComparison.OrdinalIgnoreCase)
                       || output.Contains ("nothing added to commit", StringComparison.OrdinalIgnoreCase);

        if (code != 0 && !nothing)
        {
            Error = "could not commit the folder";

            return false;
        }

        if (nothing)
        {
            log ("[sync] nothing changed locally - pushing the current commit anyway");
        }

        if ((await GitAsync (["push", "--force", remote.Url, $"HEAD:{Branch ()}"], remote, credential, log)).Code != 0)
        {
            Error = $"could not push to {Branch ()} on the remote";

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Copies the folder aside before a Download overwrites it. Skips the cache, the git
    ///     directory and earlier backups, so the copy is the user data and nothing else.
    /// </summary>
    public static string? Backup (Action<string> log)
    {
        string target = Path.Combine (UserSettings.BackupPath, DateTime.Now.ToString ("yyyy-MM-dd-HHmmss"));

        string[] skip =
        {
            Path.Combine (UserSettings.FolderPath, ".git"),
            UserSettings.CachePath,
            UserSettings.BackupPath
        };

        try
        {
            Directory.CreateDirectory (target);

            int files = 0;

            foreach (string source in Directory.EnumerateFiles (UserSettings.FolderPath, "*", SearchOption.AllDirectories))
            {
                if (skip.Any (s => source.StartsWith (s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string relative = Path.GetRelativePath (UserSettings.FolderPath, source);
                string destination = Path.Combine (target, relative);

                Directory.CreateDirectory (Path.GetDirectoryName (destination)!);
                File.Copy (source, destination, overwrite: true);
                files++;
            }

            log ($"[sync] backed up {files} file(s) to {target}");

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log ("[sync] backup failed: " + ex.Message);
            Error = ex.Message;

            return null;
        }
    }

    /// <summary>
    ///     Folds the write-ahead log back into the database file, so what gets committed is the
    ///     whole database rather than a snapshot missing its most recent writes.
    /// </summary>
    private static void Checkpoint (Action<string> log)
    {
        if (!Engine.Ready)
        {
            return;
        }

        try
        {
            using SqliteConnection connection = Engine.Connect ();
            using SqliteCommand command = connection.CreateCommand ();

            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery ();

            log ("[sync] database checkpointed");
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            log ("[sync] could not checkpoint the database: " + ex.Message);
        }
    }

    /// <summary>
    ///     Deletes the write-ahead log left over from the database that was just replaced. It
    ///     describes the old file, and sqlite would refuse or misread the new one beside it.
    /// </summary>
    private static void DropStaleSidecars (Action<string> log)
    {
        foreach (string suffix in new [] { "-wal", "-shm" })
        {
            string path = Engine.FilePath + suffix;

            try
            {
                if (File.Exists (path))
                {
                    File.Delete (path);
                    log ("[sync] removed stale " + Path.GetFileName (path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log ("[sync] could not remove " + Path.GetFileName (path) + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    ///     Reopens what the Download replaced. Settings come first so the rest of the reload acts
    ///     on the configuration that just arrived.
    ///
    ///     Reloading UserSettings is not optional: it keeps its values in memory and rewrites the
    ///     whole file on every Set, so without this the next preference change would write the
    ///     pre-download settings straight back over the ones just fetched.
    /// </summary>
    private static void Reopen (Action<string> log)
    {
        UserSettings.Load ();
        log ("[sync] settings reloaded");

        Engine.Init ();

        log (Engine.Ready
                 ? $"[sync] database reopened, schema {Engine.SchemaVersion}"
                 : "[sync] database did not reopen: " + (Engine.Error ?? "unknown error"));

        NotesSync.Run ();

        log (NotesSync.Error is { } problem
                 ? "[sync] notes reconcile reported: " + problem
                 : $"[sync] notes reconciled - {NotesSync.Imported} imported, {NotesSync.Refreshed} refreshed, "
                   + $"{NotesSync.Restored} restored, {NotesSync.Deleted} marked deleted");
    }

    /// <summary>The stored credential for this remote, or null when none is stored yet.</summary>
    public static GitCredential? Credential (SyncRemote remote, AuthKey key) =>
        AuthStore.Get (remote.AuthId, key) is { } json ? GitCredential.FromJson (json) : null;

    /// <summary>True when git refused the credential, rather than failing for some other reason.</summary>
    public static bool Unauthorized (string output) =>
        output.Contains ("Authentication failed", StringComparison.OrdinalIgnoreCase)
        || output.Contains ("could not read Username", StringComparison.OrdinalIgnoreCase)
        || output.Contains ("Invalid username or token", StringComparison.OrdinalIgnoreCase)
        || output.Contains ("Permission denied", StringComparison.OrdinalIgnoreCase)
        // Anchored to the status phrasing git actually prints, so a hash or a byte count that
        // merely contains 401 does not send the user off to re-enter a working token.
        || Status.IsMatch (output);

    private static readonly Regex Status = new (
                                                @"(?:error|fatal|HTTP)[^\n]*\b(?:401|403)\b",
                                                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                                                TimeSpan.FromSeconds (1));
}

/// <summary>
///     Drives one sync from the button press to the result.
///
///     Credentials are collected here rather than on the User page, so the first sync on a
///     new machine walks the user through it: set a passphrase, enter a token, confirm, go. The
///     destructive confirm is last, immediately before anything is overwritten.
/// </summary>
internal static class SyncRunner
{
    public static void Download (View owner) => Run (owner, download: true);

    public static void Update (View owner) => Run (owner, download: false);

    private static void Run (View owner, bool download)
    {
        SyncRemote remote = DataSync.Remote ();

        if (remote.Kind == RemoteKind.Unset)
        {
            MessageBox.ErrorQuery (
                                   owner.App!,
                                   "No remote",
                                   "Set a repository URL in Settings > User first.",
                                   new [] { "_Ok" });

            return;
        }

        AuthKey? key = null;

        try
        {
            GitCredential? credential = null;

            // ssh rides the keys the user already has, and a local path has nothing to sign in
            // to, so neither asks for a passphrase at all.
            if (remote.NeedsAuth)
            {
                key = AuthPrompt.Unlock (owner);

                if (key is null)
                {
                    return;
                }

                credential = DataSync.Credential (remote, key);

                // A token pasted into the repo URL is committed to the store here, where the
                // passphrase is finally available - so it is never asked for twice. A freshly
                // pasted one wins over whatever was stored, since supplying a new token is how
                // you replace an expired one.
                if (PendingCredential.Take (remote.AuthId) is { } pasted)
                {
                    if (!AuthStore.Set (remote.AuthId, pasted.ToJson (), key))
                    {
                        MessageBox.ErrorQuery (
                                               owner.App!,
                                               "Cannot store",
                                               AuthStore.Error ?? "could not write the credentials file",
                                               new [] { "_Ok" });

                        return;
                    }

                    credential = pasted;
                }

                if (credential is null)
                {
                    if (Ask (owner, remote, key, "") is not { } entered)
                    {
                        return;
                    }

                    credential = entered;
                }
            }

            if (!Confirm (owner, remote, download))
            {
                return;
            }

            (bool ok, string output) = Progress (owner, remote, credential, download);

            // A rejected token is worth one more go, with a chance to replace it in place.
            if (!ok && key is not null && remote.NeedsAuth && DataSync.Unauthorized (output))
            {
                if (Retry (owner, remote) && Ask (owner, remote, key, credential?.User ?? "") is { } replaced)
                {
                    (ok, output) = Progress (owner, remote, replaced, download);
                }
            }

            Report (owner, download, ok);
        }
        finally
        {
            key?.Dispose ();
        }
    }

    /// <summary>
    ///     Asks for the credential for this host and stores it. This is the only place a token is
    ///     entered - there is no field for it on the User page.
    /// </summary>
    private static GitCredential? Ask (View owner, SyncRemote remote, AuthKey key, string user)
    {
        string warning = remote.Kind == RemoteKind.Http
                             ? "This remote is plain http, so the token is sent unencrypted."
                             : "Use an access token, not your account password.";

        if (AuthPrompt.AskCredential (
                                      owner,
                                      "Sign in to " + remote.Host,
                                      $"{warning}\nIt is encrypted with your passphrase and kept out of the synced folder.",
                                      "Username",
                                      "Token",
                                      user) is not { } entered)
        {
            return null;
        }

        GitCredential credential = new () { User = entered.User, Token = entered.Secret };

        if (!AuthStore.Set (remote.AuthId, credential.ToJson (), key))
        {
            MessageBox.ErrorQuery (
                                   owner.App!,
                                   "Cannot store",
                                   AuthStore.Error ?? "could not write the credentials file",
                                   new [] { "_Ok" });

            return null;
        }

        return credential;
    }

    /// <summary>The gate immediately before anything is destroyed. Affirmative button last.</summary>
    private static bool Confirm (View owner, SyncRemote remote, bool download)
    {
        string what = download
                          ? $"Everything in {UserSettings.FolderPath}"
                          + Environment.NewLine
                          + "is replaced by the remote copy. Local changes the remote does not"
                          + Environment.NewLine
                          + "have are DISCARDED."
                          + Environment.NewLine
                          + Environment.NewLine
                          + $@"A copy is saved to {UserSettings.BackupFolderName}\<timestamp>\ first."
                          : "The remote branch is force-pushed to match this folder."
                          + Environment.NewLine
                          + "Commits on the remote that this machine does not have"
                          + Environment.NewLine
                          + "are LOST.";

        return MessageBox.Query (
                                 owner.App!,
                                 download ? "Overwrite local data" : "Overwrite the remote",
                                 $"Remote: {remote.Describe ()}"
                                 + Environment.NewLine
                                 + $"Branch: {DataSync.Branch ()}"
                                 + Environment.NewLine
                                 + Environment.NewLine
                                 + what
                                 + Environment.NewLine
                                 + Environment.NewLine
                                 + "This cannot be undone.",
                                 new [] { "_Cancel", download ? "_Overwrite local" : "_Overwrite remote" })
               == 1;
    }

    private static bool Retry (View owner, SyncRemote remote) =>
        MessageBox.Query (
                          owner.App!,
                          "Sign-in refused",
                          $"{remote.Host} rejected the stored token."
                          + Environment.NewLine
                          + Environment.NewLine
                          + "It may have expired or been revoked.",
                          new [] { "_Cancel", "_Enter a new token" })
        == 1;

    /// <summary>
    ///     Runs the operation behind a live log, so a stall is visible as the git command it is
    ///     stuck on rather than a frozen window. Close only becomes available once it has finished.
    /// </summary>
    private static (bool Ok, string Output) Progress (View owner, SyncRemote remote, GitCredential? credential, bool download)
    {
        Dialog dialog = new ()
        {
            Title = download ? "Download" : "Update",
            Width = Dim.Percent (85),
            Height = Dim.Percent (80)
        };

        TerminalWidget terminal = new () { Width = Dim.Fill (), Height = Dim.Fill (1) };

        Button close = new () { Text = "_Close", Enabled = false };

        StringBuilder transcript = new ();
        bool ok = false;

        void Write (string line)
        {
            lock (transcript)
            {
                transcript.AppendLine (line);
            }

            terminal.Write (line);
        }

        dialog.Initialized += (_, _) => _ = RunAsync ();

        async Task RunAsync ()
        {
            bool result = download
                              ? await DataSync.DownloadAsync (remote, credential, Write)
                              : await DataSync.UpdateAsync (remote, credential, Write);

            owner.App?.Invoke (() =>
                               {
                                   ok = result;

                                   Write ("");
                                   Write (result
                                              ? download ? "Download finished." : "Update finished."
                                              : "FAILED: " + (DataSync.Error ?? "see the output above"));

                                   close.Enabled = true;
                                   close.SetFocus ();
                               });
        }

        dialog.Add (terminal);
        dialog.AddButton (close);

        owner.App!.Run (dialog);
        dialog.Dispose ();

        return (ok, transcript.ToString ());
    }

    private static void Report (View owner, bool download, bool ok)
    {
        if (!ok)
        {
            return;
        }

        MessageBox.Query (
                          owner.App!,
                          download ? "Download finished" : "Update finished",
                          download
                              ? "The folder now matches the remote."
                              + Environment.NewLine
                              + Environment.NewLine
                              + "Tabs that are already open still show what was loaded before the"
                              + Environment.NewLine
                              + "download. Restart ttm to see everything."
                              : "The remote now matches this folder.",
                          new [] { "_Ok" });
    }
}
