// -----------------------------------------------------------------------------
//  Shared services: environment, auth, settings, caching, AI clients and tools.
// -----------------------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;

using Ttl = Anthropic.Models.Messages.Ttl;
using CacheControlEphemeral = Anthropic.Models.Messages.CacheControlEphemeral;
using OpenAI;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

/// <summary>Secrets loaded from a .env file into the process environment.</summary>
internal static class Env
{
    public const string DefaultFileName = ".env";

    public static int Load (string? fileName = null)
    {
        string? path = Find (fileName ?? DefaultFileName);

        if (path is null)
        {
            return 0;
        }

        int loaded = 0;

        foreach (string raw in File.ReadLines (path))
        {
            string line = raw.Trim ();

            if (line.Length == 0 || line [0] == '#')
            {
                continue;
            }

            if (line.StartsWith ("export ", StringComparison.Ordinal))
            {
                line = line [7..].TrimStart ();
            }

            int equals = line.IndexOf ('=');

            if (equals <= 0)
            {
                continue;
            }

            string key = line [..equals].TrimEnd ();
            string value = line [(equals + 1)..].Trim ();

            if (value.Length >= 2 && (value [0] == '"' || value [0] == '\'') && value [^1] == value [0])
            {
                value = value [1..^1];
            }

            if (value.Length == 0 || Environment.GetEnvironmentVariable (key) is not null)
            {
                continue;
            }

            Environment.SetEnvironmentVariable (key, value);
            loaded++;
        }

        return loaded;
    }

    public static string? Get (string key) => Environment.GetEnvironmentVariable (key);

    public static string Require (string key) =>
        Environment.GetEnvironmentVariable (key)
        ?? throw new InvalidOperationException (
                                               $"{key} is not set. Add it to {DefaultFileName} in the project root, "
                                               + "or set it as an environment variable.");

    private static string? Find (string fileName)
    {
        for (DirectoryInfo? directory = new (AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine (directory.FullName, fileName);

            if (File.Exists (candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Signs the user in to Microsoft Graph and hands out access tokens for them.</summary>
internal static class GraphAuth
{
    private static readonly string[] Scopes = { "Chat.Read", "Mail.Read" };

    private static readonly Lazy<Task<IPublicClientApplication>> Client = new (BuildAsync);

    public static async Task<string> TokenAsync (Action<string> prompt, CancellationToken token = default)
    {
        IPublicClientApplication client = await Client.Value;
        IAccount? account = (await client.GetAccountsAsync ()).FirstOrDefault ();

        if (account is not null)
        {
            try
            {
                return (await client.AcquireTokenSilent (Scopes, account).ExecuteAsync (token)).AccessToken;
            }
            catch (MsalUiRequiredException)
            {
            }
        }

        AuthenticationResult result = await client
                                            .AcquireTokenWithDeviceCode (
                                                                         Scopes,
                                                                         code =>
                                                                         {
                                                                             prompt (code.Message);

                                                                             return Task.CompletedTask;
                                                                         })
                                            .ExecuteAsync (token);

        return result.AccessToken;
    }

    public static async Task SignOutAsync ()
    {
        IPublicClientApplication client = await Client.Value;

        foreach (IAccount account in await client.GetAccountsAsync ())
        {
            await client.RemoveAsync (account);
        }
    }

    private static async Task<IPublicClientApplication> BuildAsync ()
    {
        IPublicClientApplication client =
            PublicClientApplicationBuilder
                .Create (Env.Require ("TEAMS_CLIENT_ID"))
                .WithAuthority ($"https://login.microsoftonline.com/{Env.Require ("TEAMS_TENANT_ID")}")
                .Build ();

        StorageCreationProperties store =
            new StorageCreationPropertiesBuilder ("ttm.msal.cache", MsalCacheHelper.UserRootDirectory).Build ();

        (await MsalCacheHelper.CreateAsync (store)).RegisterCache (client.UserTokenCache);

        return client;
    }
}

/// <summary>Shared wire-format helpers for the API-backed widgets in widget.cs.</summary>
internal static class Wire
{
    public static string? Str (JsonElement element, params string[] path)
    {
        foreach (string name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty (name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString () : null;
    }

    public static IEnumerable<string> Wrap (string message, int width)
    {
        string line = "";

        foreach (string word in message.Split (' '))
        {
            if (line.Length + word.Length + 1 > width && line.Length > 0)
            {
                yield return line;

                line = "";
            }

            line += line.Length == 0 ? word : " " + word;
        }

        yield return line;
    }
}

/// <summary>Optional user settings, read once at startup and applied globally.</summary>
internal static class UserSettings
{
    public const string FolderName = "total-manager";
    public const string FileName = "user-settings.json";
    public const string ScriptsFolderName = "scripts";
    public const string CacheFolderName = "cache";

    public const string DevFolder = @"C:\dev";

    public const string SkillsFolderName = "skills";

    public const string ReportsFolderName = "reports";

    public const string ReviewsFolderName = "reviews";

    public const string NotesFolderName = "notes";

    public const string DailyFolderName = "daily";

    public const string ProjectsFolderName = "projects";

    public const string DatabaseFolderName = "database";

    // Where Download copies the folder before it overwrites it. Git-ignored, so a later
    // clean -fd never removes the safety net it just made.
    public const string BackupFolderName = "_backup";

    public const string EmailProvider = "email-provider";

    public const string ThemeName = "theme";

    public const string AiProvider = "ai-provider";

    public const string AiUrl = "ai-url";

    public const string AiModel = "ai-model";

    public const string AiCompactTokens = "ai-compact-tokens";

    public const string AiTrimTokens = "ai-trim-tokens";

    public const string NextUpModel = "next-up-model";

    public const string FileRoots = "file-roots";

    public const string Links = "links";

    public const string GitProvider = "git-provider";

    public const string GitHub = "github";
    public const string Codeberg = "codeberg";

    public static readonly string[] GitProviders = { GitHub, Codeberg };

    // Data sync (datasync.cs). The remote may be https, http, ssh or a local/UNC path; the
    // username and token are NOT here - they live encrypted in AuthStore, keyed by host.
    public const string SyncRemote = "sync-remote";

    public const string SyncBranch = "sync-branch";

    // Only consulted for an https remote whose certificate does not chain to a trusted root,
    // which is the normal case for a self-hosted git service.
    public const string SyncInsecureTls = "sync-insecure-tls";

    public const string ShowTeams = "show-teams";

    public const string ShowMail = "show-mail";

    // "#RRGGBB" chosen on the editor page, or "" for the theme's own Dialog background.
    public const string EditorBackground = "editor-background";

    private static readonly Dictionary<string, JsonNode?> Defaults = new ()
    {
        [Links] = new JsonObject { ["Codeberg"] = "https://codeberg.org", ["Hacker News"] = "https://news.ycombinator.com" },

        [EmailProvider] = "microsoft",

        [ThemeName] = "Default",

        [ShowTeams] = "true",
        [ShowMail] = "true",

        [EditorBackground] = "",

        [AiProvider] = ChatProvider.Anthropic,
        [AiUrl] = ChatProvider.AnthropicUrl,
        [AiModel] = "claude-opus-5",

        [AiCompactTokens] = 120_000,
        [AiTrimTokens] = 200_000,

        [NextUpModel] = "",

        [GitProvider] = GitHub,

        [SyncRemote] = "",
        [SyncBranch] = "main",
        [SyncInsecureTls] = "false",

        [FileRoots] = new JsonArray (
                                     (JsonNode) @"%USERPROFILE%\.claude",
                                     (JsonNode) $@"%APPDATA%\{FolderName}",
                                     (JsonNode) DevFolder)
    };

    private static Dictionary<string, JsonNode?> _values = Clone (Defaults);

    public static string FolderPath => Path.Combine (
                                                     Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData),
                                                     FolderName);

    public static string FilePath => Path.Combine (FolderPath, FileName);

    public static string ScriptsPath => Path.Combine (FolderPath, ScriptsFolderName);

    public static string CachePath => Path.Combine (FolderPath, CacheFolderName);

    public static string SkillsPath => Path.Combine (FolderPath, SkillsFolderName);

    public static string ReportsPath => Path.Combine (FolderPath, ReportsFolderName);

    public static string ReviewsPath => Path.Combine (FolderPath, ReviewsFolderName);

    public static string NotesPath => Path.Combine (FolderPath, NotesFolderName);

    public static string DailyPath => Path.Combine (NotesPath, DailyFolderName);

    public static string ProjectsPath => Path.Combine (FolderPath, ProjectsFolderName);

    public static string DatabasePath => Path.Combine (FolderPath, DatabaseFolderName);

    public static string BackupPath => Path.Combine (FolderPath, BackupFolderName);

    public static void Load ()
    {
        try
        {
            Directory.CreateDirectory (ScriptsPath);
            Directory.CreateDirectory (CachePath);
            Directory.CreateDirectory (SkillsPath);

            Directory.CreateDirectory (ReportsPath);
            Directory.CreateDirectory (ReviewsPath);

            Directory.CreateDirectory (NotesPath);
            Directory.CreateDirectory (DailyPath);
            Directory.CreateDirectory (ProjectsPath);
            Directory.CreateDirectory (DatabasePath);

            if (File.Exists (FilePath))
            {
                _values = JsonSerializer.Deserialize<Dictionary<string, JsonNode?>> (File.ReadAllText (FilePath))
                          ?? new Dictionary<string, JsonNode?> ();
            }

            foreach ((string key, JsonNode? value) in Defaults)
            {
                _values.TryAdd (key, value?.DeepClone ());
            }

            if (_values.TryGetValue (FileRoots, out JsonNode? roots) && roots is not JsonArray)
            {
                _values [FileRoots] = new JsonArray (
                                                     (roots?.ToString () ?? "")
                                                     .Split (';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                     .Select (r => (JsonNode) r)
                                                     .ToArray ());
            }

            Save ();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _values = Clone (Defaults);
        }
    }

    public static string Get (string key) =>
        _values.TryGetValue (key, out JsonNode? value) && value is JsonValue
            ? value.ToString ()
            : Defaults.GetValueOrDefault (key)?.ToString () ?? "";

    public static int GetInt (string key, int fallback) =>
        int.TryParse (Get (key), out int value) && value > 0 ? value : fallback;

    public static string[] GetArray (string key) =>
        _values.TryGetValue (key, out JsonNode? value) && value is JsonArray array
            ? array.Select (e => e?.ToString () ?? "").Where (e => e.Length > 0).ToArray ()
            : [];

    public static void SetArray (string key, IEnumerable<string> values)
    {
        _values [key] = new JsonArray (values.Where (v => v.Length > 0).Select (v => (JsonNode) v).ToArray ());
        Save ();
    }

    public static Dictionary<string, string> GetTable (string key) =>
        _values.TryGetValue (key, out JsonNode? value) && value is JsonObject table
            ? table.Where (e => e.Value is not null)
                   .ToDictionary (e => e.Key, e => e.Value!.ToString ())
            : new Dictionary<string, string> ();

    public static void SetTable (string key, IEnumerable<KeyValuePair<string, string>> entries)
    {
        JsonObject table = new ();

        foreach ((string name, string value) in entries)
        {
            if (name.Length > 0 && value.Length > 0)
            {
                table [name] = value;
            }
        }

        _values [key] = table;
        Save ();
    }

    public static bool CanLoad (string json, out string error)
    {
        try
        {
            JsonSerializer.Deserialize<Dictionary<string, JsonNode?>> (json);
            error = "";

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;

            return false;
        }
    }

    private static Dictionary<string, JsonNode?> Clone (Dictionary<string, JsonNode?> source) =>
        source.ToDictionary (e => e.Key, e => e.Value?.DeepClone ());

    public static bool On (string key) => Get (key) == "true";

    public static void Set (string key, bool on) => Set (key, on ? "true" : "false");

    public static void Set (string key, string value)
    {
        _values [key] = value;
        Save ();
    }

    private static void Save ()
    {
        File.WriteAllText (FilePath, JsonSerializer.Serialize (_values, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
///     Gmail sign-in. Google issues a long-lived refresh token once; .env keeps it, and this trades
///     it for a short-lived access token per use — so, like GraphAuth, the app itself never writes
///     a credential.
/// </summary>
internal static class GmailAuth
{
    private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (15) };

    public static async Task<string> TokenAsync ()
    {
        using HttpResponseMessage response = await Http.PostAsync (
                                                                   "https://oauth2.googleapis.com/token",
                                                                   new FormUrlEncodedContent (
                                                                    new Dictionary<string, string>
                                                                    {
                                                                        ["client_id"] = Env.Require ("GMAIL_CLIENT_ID"),
                                                                        ["client_secret"] = Env.Require ("GMAIL_CLIENT_SECRET"),
                                                                        ["refresh_token"] = Env.Require ("GMAIL_REFRESH_TOKEN"),
                                                                        ["grant_type"] = "refresh_token"
                                                                    }));

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement> ();

        return Wire.Str (body, "access_token")
               ?? throw new HttpRequestException (
                                                  Wire.Str (body, "error_description")
                                                  ?? Wire.Str (body, "error")
                                                  ?? response.StatusCode.ToString ());
    }
}

/// <summary>A repeating refresh for a view, on the UI loop.</summary>
internal static class AutoRefresh
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds (60);

    public static void RefreshEvery (this View view, TimeSpan interval, Func<Task> refresh)
    {
        object? timer = null;

        view.Initialized += (_, _) => timer = view.App?.AddTimeout (
                                                                    interval,
                                                                    () =>
                                                                    {
                                                                        if (Showing (view))
                                                                        {
                                                                            _ = refresh ();
                                                                        }

                                                                        return true;
                                                                    });

        view.Disposing += (_, _) =>
        {
            if (timer is { } token)
            {
                view.App?.RemoveTimeout (token);
                timer = null;
            }
        };
    }

    internal static bool Showing (View view)
    {
        for (View? v = view; v is not null; v = v.SuperView)
        {
            if (!v.Visible)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Builds an IChatClient for a provider/url/model triple.</summary>
internal static class ChatProvider
{
    public const string Anthropic = "anthropic";

    public const string AnthropicUrl = "https://api.anthropic.com";

    public static bool IsTransient (Exception ex) =>
        ex is not OperationCanceledException
        && (ex.Message.Contains ("overloaded", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains ("rate_limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains ("ServiceUnavailable", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains ("InternalServerError", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains ("\"type\":\"api_error\"", StringComparison.Ordinal));

    public static string Explain (Exception ex) =>
        ex.Message.Contains ("overloaded", StringComparison.OrdinalIgnoreCase)
            ? $"{Anthropic} is at capacity"
            : ex.Message.Contains ("rate_limit", StringComparison.OrdinalIgnoreCase)
                ? "rate limited"
                : IsTransient (ex)
                    ? "the service returned an error"
                    : ex.Message;

    private const int ServerRetries = 3;

    public static readonly string[] Names = { Anthropic, "openai", "ollama", "lmstudio" };

    public static bool HostsTools (string provider) => provider is Anthropic or "openai";

    public static readonly Dictionary<string, string> DefaultUrls = new (StringComparer.Ordinal)
    {
        [Anthropic] = AnthropicUrl,
        ["openai"] = "https://api.openai.com/v1",
        ["ollama"] = "http://localhost:11434/v1",
        ["lmstudio"] = "http://localhost:1234/v1"
    };

    public static string? KeyName (string provider) => provider switch
    {
        Anthropic => "ANTHROPIC_API_KEY",
        "openai" => "OPENAI_API_KEY",
        _ => null
    };

    public static IChatClient Create (string provider, string url, string model, string? key = null)
    {
        string endpoint = url.Length > 0 ? url : DefaultUrls.GetValueOrDefault (provider, "");
        string? resolved = key is { Length: > 0 } ? key : Env.Get (KeyName (provider) ?? "");

        if (provider == Anthropic)
        {
            ClientOptions options = new () { MaxRetries = ServerRetries };

            if (resolved is { Length: > 0 })
            {
                options.ApiKey = resolved;
            }

            return new AnthropicClient (options).AsIChatClient (model);
        }

        if (resolved is not { Length: > 0 } && !IsLocal (endpoint))
        {
            throw new InvalidOperationException (
                                                 $"{endpoint} needs an API key. Type one in the AI tab's Key box, "
                                                 + $"or set {KeyName (provider) ?? "the provider's key"} in .env.");
        }

        OpenAIClient client = new (
                                   new ApiKeyCredential (resolved is { Length: > 0 } ? resolved : "local"),
                                   new OpenAIClientOptions
                                   {
                                       Endpoint = new Uri (endpoint),

                                       RetryPolicy = new ClientRetryPolicy (1),

                                       NetworkTimeout = TimeSpan.FromMinutes (10)
                                   });

        return client.GetChatClient (model).AsIChatClient ();
    }

    private static bool IsLocal (string endpoint) =>
        Uri.TryCreate (endpoint, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
}

/// <summary>
///     Decides what a conversation sends and when it is condensed. Works the same on every backend
///     in <see cref="ChatProvider.Names"/>.
/// </summary>
internal static class ContextPolicy
{
    public static int CompactTokens => UserSettings.GetInt (UserSettings.AiCompactTokens, 120_000);

    public static int TrimTokens =>
        Math.Max (UserSettings.GetInt (UserSettings.AiTrimTokens, 200_000), CompactTokens);

    private const int KeepRecent = 12;

    private const int SummaryTokens = 2_000;

    private const int CharsPerToken = 4;

    private const string SummaryHead = "[Earlier conversation, summarized]";

    public static int Estimate (IReadOnlyList<ChatMessage> history, long? reported)
    {
        if (reported is > 0)
        {
            return (int) Math.Min (reported.Value, int.MaxValue);
        }

        long chars = history.Sum (
                                  m => m.Contents.Sum (
                                                       c => (long) (c switch
                                                       {
                                                           TextContent t => t.Text.Length,
                                                           FunctionResultContent r => r.Result?.ToString ()?.Length ?? 0,
                                                           FunctionCallContent f => f.Arguments?.Sum (a => a.Key.Length + (a.Value?.ToString ()?.Length ?? 0)) ?? 0,
                                                           _ => 0
                                                       })));

        return (int) Math.Min (chars / CharsPerToken, int.MaxValue);
    }

    public static void ApplyCaching (IReadOnlyList<ChatMessage> history)
    {
        foreach (ChatMessage message in history)
        {
            foreach (AIContent content in message.Contents)
            {
                content.WithCacheControl ((CacheControlEphemeral?) null);
            }
        }

        if (history.Count < 2)
        {
            return;
        }

        Mark (history [0], Ttl.Ttl1h);
        Mark (history [^2], Ttl.Ttl5m);
    }

    private static void Mark (ChatMessage message, Ttl ttl)
    {
        if (message.Contents.Count > 0)
        {
            message.Contents [^1].WithCacheControl (ttl);
        }
    }

    public static async Task<string?> CompactAsync (
        List<ChatMessage> history,
        IChatClient client,
        CancellationToken token)
    {
        int cut = FindCut (history);

        if (cut < 1)
        {
            return null;
        }

        string transcript = Render (history.Take (cut));

        ChatResponse response = await client.GetResponseAsync (
                                                               [
                                                                   new ChatMessage (
                                                                                    ChatRole.User,
                                                                                    "Summarise the conversation below so it can replace the original. "
                                                                                    + "Keep decisions, file paths, numbers, and anything the user asked for "
                                                                                    + "that is not finished. Drop pleasantries and superseded detail. "
                                                                                    + "Write it as notes addressed to yourself, not a reply to anyone."
                                                                                    + Environment.NewLine
                                                                                    + Environment.NewLine
                                                                                    + transcript)
                                                               ],

                                                               new ChatOptions { MaxOutputTokens = SummaryTokens },
                                                               token);

        string summary = response.Text.Trim ();

        if (summary.Length == 0)
        {
            return null;
        }

        history.RemoveRange (0, cut);

        history.Insert (0, new ChatMessage (ChatRole.User, $"{SummaryHead}{Environment.NewLine}{summary}"));
        history.Insert (1, new ChatMessage (ChatRole.Assistant, "Noted. Continuing from that summary."));

        return summary;
    }

    private static int FindCut (IReadOnlyList<ChatMessage> history)
    {
        for (int i = history.Count - KeepRecent; i >= 1; i--)
        {
            if (history [i].Role != ChatRole.User)
            {
                continue;
            }

            if (history [i].Contents.Any (c => c is FunctionResultContent or ToolApprovalResponseContent))
            {
                continue;
            }

            if (i == 1 && history [0].Text.StartsWith (SummaryHead, StringComparison.Ordinal))
            {
                return -1;
            }

            return i;
        }

        return -1;
    }

    private static string Render (IEnumerable<ChatMessage> messages) =>
        string.Join (
                     Environment.NewLine,
                     messages.Select (
                                      m => $"{m.Role}: "
                                           + string.Join (
                                                          " ",
                                                          m.Contents.Select (
                                                                             c => c switch
                                                                             {
                                                                                 TextContent t => t.Text,
                                                                                 FunctionCallContent f => $"(called {f.Name})",
                                                                                 FunctionResultContent r => $"(result: {Head (r.Result?.ToString ())})",
                                                                                 _ => ""
                                                                             })
                                                                    .Where (s => s.Length > 0))));

    private const int ResultHead = 1_500;

    private static string Head (string? text) =>
        text is null ? "" :
        text.Length <= ResultHead ? text.ReplaceLineEndings (" ") : text [..ResultHead].ReplaceLineEndings (" ") + "...";
}

/// <summary>A short-lived on-disk cache of API responses, under %APPDATA%\total-manager\cache.</summary>
internal static class ApiCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes (20);

    public static string Identity (params string?[] parts) => Hash (string.Join ("\0", parts));

    public static string? Read (string key, string identity)
    {
        try
        {
            string path = PathFor (key);

            if (!File.Exists (path))
            {
                return null;
            }

            string[] parts = File.ReadAllText (path).Split ('\n', 3);

            if (parts.Length < 3 || parts [0] != identity || !long.TryParse (parts [1], out long stamp))
            {
                return null;
            }

            return DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds (stamp) <= MaxAge ? parts [2] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write (string key, string identity, string body)
    {
        try
        {
            File.WriteAllText (
                               PathFor (key),
                               $"{identity}\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds ()}\n{body}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private static string PathFor (string key) => Path.Combine (UserSettings.CachePath, Hash (key) + ".cache");

    private static string Hash (string text) =>
        Convert.ToHexString (SHA256.HashData (Encoding.UTF8.GetBytes (text))) [..32];
}

/// <summary>
///     A read-only PowerShell command, gated twice: an allow-list here, and the user's own approval
///     before it runs.
/// </summary>
internal static class TerminalTools
{
    private static readonly HashSet<string> Allowed = new (StringComparer.OrdinalIgnoreCase)
    {
        "Get-Content", "Get-ChildItem", "Get-Item", "Select-String", "Test-Path", "Resolve-Path",

        "Select-Object", "Sort-Object", "Where-Object", "Measure-Object", "Group-Object",
        "Compare-Object", "ForEach-Object",

        "Format-List", "Format-Table", "Out-String", "ConvertFrom-Json", "ConvertTo-Json", "ConvertFrom-Csv",

        "Get-Command", "Get-Process", "Get-Date", "Get-Location", "Get-CimInstance", "Get-Variable",
        "Get-Member", "Get-Host", "Measure-Command",

        "gc", "cat", "type", "ls", "dir", "gci", "gi", "sls", "select", "sort", "where", "measure",
        "group", "ft", "fl", "gm", "pwd", "echo", "Write-Output", "head", "tail"
    };

    private static readonly (string Token, string Why) [] Forbidden =
    {
        ("$(", "subexpressions can run anything"),
        ("@(", "subexpressions can run anything"),
        ("`", "backticks escape the parser"),
        ("&", "the call operator runs arbitrary executables"),
        (">", "redirection writes files"),
        ("Invoke-", "Invoke-Expression and friends run arbitrary text"),
        ("iex", "alias for Invoke-Expression"),
        ("Start-", "starts processes"),
        ("New-", "creates things"),
        ("Set-", "writes"),
        ("Remove-", "deletes"),
        ("Clear-", "deletes"),
        ("Out-File", "writes files"),
        ("Add-Content", "writes files"),
        ("Export-", "writes files"),
        ("Stop-", "kills processes")
    };

    private const int TimeoutSeconds = 20;
    private const int MaxOutput = 20_000;

    public static IList<AITool> Tools { get; } =
    [
        new ApprovalRequiredAIFunction (AIFunctionFactory.Create (RunCommand))
    ];

    public static string Instructions () =>
        "The run_command tool executes READ-ONLY PowerShell and requires the user to approve "
        + "each call. Only these commands are permitted, anywhere in the text: "
        + string.Join (", ", Allowed.OrderBy (a => a, StringComparer.OrdinalIgnoreCase))
        + ". There is no git, dotnet, python, or any other program. Nothing that writes, "
        + "deletes, starts a process or reaches the network will be accepted.";

    public static bool IsAllowed (string command, out string reason)
    {
        if (string.IsNullOrWhiteSpace (command))
        {
            reason = "empty command";

            return false;
        }

        foreach ((string token, string why) in Forbidden)
        {
            if (command.Contains (token, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"'{token}' is not allowed - {why}";

                return false;
            }
        }

        foreach (string segment in command.Split (['|', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string head = segment.Trim ().TrimStart ('(', '{').Trim ();

            if (head.Length == 0)
            {
                continue;
            }

            string word = Regex.Match (head, @"^[^\s(){}]+").Value;

            if (word.Length > 0 && !Allowed.Contains (word))
            {
                reason = $"'{word}' is not on the allow-list";

                return false;
            }
        }

        foreach (Match match in Regex.Matches (command, @"(?<![-\w.\/])[A-Za-z][\w]*-[\w-]+(?![\w-])"))
        {
            if (!Allowed.Contains (match.Value))
            {
                reason = $"'{match.Value}' is not on the allow-list";

                return false;
            }
        }

        reason = "";

        return true;
    }

    [Description (
                  "Run a READ-ONLY PowerShell command and return its output. Only file reading, "
                  + "searching and inspection commands are permitted, and the user must approve "
                  + "every call before it runs. No git, dotnet, python or other programs.")]
    private static string RunCommand ([Description ("The PowerShell command to run")] string command)
    {
        if (!IsAllowed (command, out string reason))
        {
            return $"refused: {reason}. Permitted commands: {string.Join (", ", Allowed.OrderBy (a => a))}";
        }

        try
        {
            using Process process = Process.Start (new ProcessStartInfo ("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync ();
            Task<string> stderr = process.StandardError.ReadToEndAsync ();

            if (!process.WaitForExit (TimeSpan.FromSeconds (TimeoutSeconds)))
            {
                process.Kill (true);

                return $"refused: timed out after {TimeoutSeconds}s";
            }

            string output = $"{stdout.Result}{stderr.Result}".Trim ();

            if (output.Length > MaxOutput)
            {
                output = output [..MaxOutput] + $"\n... truncated at {MaxOutput} characters";
            }

            return output.Length == 0 ? "(no output)" : output;
        }
        catch (Exception ex)
        {
            return $"could not run: {ex.Message}";
        }
    }
}

/// <summary>Read-only filesystem access for the AI chat, as two AIFunctions.</summary>
internal sealed class RetryingChatClient (IChatClient inner) : DelegatingChatClient (inner)
{
    private const int MaxAttempts = 3;

    private const int BackoffMs = 1_000;

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync (
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            bool emitted = false;
            bool again = false;

            await using (IAsyncEnumerator<ChatResponseUpdate> stream =
                         base.GetStreamingResponseAsync (messages, options, cancellationToken)
                             .GetAsyncEnumerator (cancellationToken))
            {
                while (true)
                {
                    ChatResponseUpdate? update = null;

                    try
                    {
                        if (await stream.MoveNextAsync ())
                        {
                            update = stream.Current;
                        }
                    }
                    catch (Exception ex) when (!emitted
                                               && attempt < MaxAttempts
                                               && ChatProvider.IsTransient (ex))
                    {
                        again = true;
                    }

                    if (update is null)
                    {
                        break;
                    }

                    emitted = true;

                    yield return update;
                }
            }

            if (!again)
            {
                yield break;
            }

            await Task.Delay (attempt * BackoffMs, cancellationToken);
        }
    }
}

internal static class FileTools
{
    private static readonly string[] DeniedNames =
    {
        "id_rsa", "id_ed25519", "id_ecdsa", ".netrc", ".npmrc", ".pypirc", ".htpasswd",

        ".credentials.json"
    };

    private static readonly string[] DeniedFolders =
    {
        ".ssh", ".aws", ".gnupg", ".azure", ".kube", "secrets", "credentials", ".IdentityService"
    };

    private static readonly string[] DeniedExtensions = { ".key", ".pem", ".pfx", ".p12", ".jks", ".keystore" };

    private const int MaxBytes = 100_000;

    private const int DefaultLines = 2000;

    private const int MaxLines = 5000;

    private const int MaxLineLength = 2_000;

    private const int MaxEntries = 400;

    private const int MaxMatches = 100;

    private const int MaxFound = 200;

    private const int SearchSeconds = 5;

    private const int RegexSeconds = 2;

    private const int BinarySample = 8_192;

    private static readonly Regex CSharpDeclaration = new (
                                                           @"^\s*(?:\[.*\]\s*)?(?:public|internal|private|protected)\s+.*?(?:class|record|struct|interface|enum)\s+\w+"
                                                           + @"|^\s*(?:public|internal|private|protected)\s+(?:static\s+|async\s+|override\s+|sealed\s+|virtual\s+)*[\w<>\[\],?\s]+\s+\w+\s*\(",
                                                           RegexOptions.None,
                                                           TimeSpan.FromSeconds (2));

    private static readonly Regex MarkdownHeading = new (@"^#{1,6}\s+\S", RegexOptions.None, TimeSpan.FromSeconds (2));

    private static readonly Regex PowerShellFunction = new (
                                                            @"^\s*function\s+[\w-]+",
                                                            RegexOptions.IgnoreCase,
                                                            TimeSpan.FromSeconds (2));

    private const int DefaultContext = 120;

    public static IList<AITool> Tools { get; } =
    [
        AIFunctionFactory.Create (ReadFile),
        AIFunctionFactory.Create (ListDirectory),
        AIFunctionFactory.Create (SearchFiles),
        AIFunctionFactory.Create (FindFiles),
        AIFunctionFactory.Create (FileOutline),
        AIFunctionFactory.Create (FindTodos),
        AIFunctionFactory.Create (WriteReport),
        AIFunctionFactory.Create (WriteReview),
        AIFunctionFactory.Create (AppendReview)
    ];

    public static string[] Roots () =>
        UserSettings.GetArray (UserSettings.FileRoots)
                    .Select (Canonical)
                    .Where (r => r.Length > 0)
                    .ToArray ();

    public static string Instructions ()
    {
        string[] roots = Roots ();

        string list = roots.Length > 0
                          ? string.Join (Environment.NewLine, roots.Select (r => "  " + r))
                          : "  (none configured -- add one under Settings > Preferences)";

        return $"""
                You are the assistant inside total-manager, a Terminal.Gui desktop app running
                on Windows.

                You have real file tools, so use them. Never answer a question about what a
                file contains, what a folder holds, or what a script or skill says from memory
                or inference -- call a tool and answer from the result.

                  ReadFile       one file, as numbered lines, paged
                  ListDirectory  one folder, newest first, with modified times
                  FindFiles      locate files by name pattern and age, across subfolders
                  SearchFiles    grep file contents; countOnly when you only need how many
                  FileOutline    a file's types, methods or headings, with line numbers
                  FindTodos      TODO and FIXME markers under a folder, oldest first
                  TokenUsage     token totals from Claude Code's own session transcripts
                  Calculate      arithmetic, exactly
                  Statistics     count, sum, mean, median, min, max, stdev of a list

                On a large file call FileOutline first and then ReadFile with an offset for the
                part you actually need; reading a three thousand line file whole costs about
                twenty-five times as much and stays in the conversation afterwards. Reach for
                SearchFiles or FindFiles before reading a file you are only guessing at, and put every calculation through Calculate or Statistics rather than doing
                it in your head -- arithmetic on real figures is the one thing here you cannot
                do reliably unaided.

                Never read or grep the .jsonl transcripts under the .claude root to work out
                token usage: one line is about 1,800 characters, the usage-bearing lines alone
                come to well over a million tokens, and the same usage repeats across lines so
                summing them double-counts. TokenUsage answers that already deduplicated. When
                a search over machine-written files is unavoidable, keep the default context
                window and pass countOnly when the question is how many rather than which.

                Paths are Windows paths and must be absolute. A relative path resolves against
                the app's own working folder and will almost always be refused. When you do not
                know where something is, call ListDirectory on a root and work down rather than
                guessing at names.

                Readable roots, and nothing outside them can be opened:
                {list}

                This app's folder is {UserSettings.FolderPath}, holding the skills, reports,
                reviews and scripts subfolders and user-settings.json. Claude Code's own data is
                under the .claude root, with per-project session transcripts in its projects
                subfolder.

                Writing is limited to reports and reviews: WriteReport into reports, WriteReview
                into reviews for anything evaluative, and AppendReview to add a section to a review
                that already exists. Nothing can write anywhere else, and there is no way to modify
                a file outside those two folders. A task that spans several turns should append to
                one review rather than rewrite it each time.
                Credentials, key material and .env files are refused wherever they live.

                The time right now is {DateTime.Now:yyyy-MM-dd HH:mm} local ({TimeZoneInfo.Local.StandardName}).
                ListDirectory reports last-modified times in that same local format, so compare
                them against the time above directly rather than assuming today's date.
                """;
    }

    private static string Canonical (string root)
    {
        try
        {
            return Path.GetFullPath (Environment.ExpandEnvironmentVariables (root.Trim ()))
                       .TrimEnd (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    [Description (
                  "Read a text file as numbered lines. Large files are paged: read the footer of the "
                  + "result and call again with a higher offset to continue.")]
    private static string ReadFile (
        [Description ("Path to the file")] string path,
        [Description ("1-based line number to start at. Defaults to the first line.")]
        int offset = 1,
        [Description ("How many lines to return. Defaults to 2000; 0 or less means as many as fit.")]
        int limit = DefaultLines)
    {
        if (!TryResolve (path, directory: false, out string full, out string refusal))
        {
            return refusal;
        }

        if (!File.Exists (full))
        {
            return $"no such file: {full}. Call ListDirectory on the folder to see what is actually there.";
        }

        offset = Math.Max (1, offset);

        limit = limit <= 0 ? MaxLines : Math.Min (limit, MaxLines);

        StringBuilder page = new ();
        int number = 0;
        int shown = 0;
        bool truncatedByBytes = false;

        foreach (string line in File.ReadLines (full))
        {
            number++;

            if (number < offset)
            {
                continue;
            }

            if (shown == limit)
            {
                break;
            }

            string text = line.Length > MaxLineLength
                              ? line [..MaxLineLength] + $"... [+{line.Length - MaxLineLength:N0} chars]"
                              : line;

            if (page.Length + text.Length > MaxBytes)
            {
                truncatedByBytes = true;

                break;
            }

            page.Append (number).Append ('\u2192').Append (text).Append ('\n');
            shown++;
        }

        if (shown == 0)
        {
            return number == 0
                       ? "(empty file)"
                       : $"no lines at offset {offset}; the file has {number:N0} lines";
        }

        int last = offset + shown - 1;

        bool more = shown == limit || truncatedByBytes;

        page.Append ($"\n[lines {offset:N0}-{last:N0}");
        page.Append (truncatedByBytes ? $", stopped at the {MaxBytes:N0} character limit" : "");
        page.Append (more ? $"; call again with offset={last + 1:N0} for more]" : "; end of file]");

        return page.ToString ();
    }

    [Description (
                  "List the files and folders directly inside a directory, newest first, each with its "
                  + "last-modified time in local yyyy-MM-dd HH:mm. Only directories under the configured "
                  + "roots are listable.")]
    private static string ListDirectory ([Description ("Path to the directory")] string path)
    {
        if (!TryResolve (path, directory: true, out string full, out string refusal))
        {
            return refusal;
        }

        if (!Directory.Exists (full))
        {
            return File.Exists (full)
                       ? $"{full} is a file, not a directory. Use ReadFile for it, or "
                         + $"ListDirectory on {Path.GetDirectoryName (full)}."
                       : $"no such directory: {full}";
        }

        List<FileSystemInfo> entries = new DirectoryInfo (full)
                                       .EnumerateFileSystemInfos ()
                                       .Where (e => !Denied (e.FullName))
                                       .OrderByDescending (e => e.LastWriteTime)
                                       .ToList ();

        if (entries.Count == 0)
        {
            return $"{full} is empty";
        }

        StringBuilder listing = new ();

        listing.Append (full)
               .Append (" -- ")
               .Append (entries.Count)
               .Append (entries.Count == 1 ? " entry" : " entries")
               .Append (entries.Count > MaxEntries ? $", newest {MaxEntries} shown" : ", newest first")
               .Append (", times are local\n");

        foreach (FileSystemInfo entry in entries.Take (MaxEntries))
        {
            listing.Append (entry.LastWriteTime.ToString ("yyyy-MM-dd HH:mm"))
                   .Append ("  ")
                   .Append (entry.Name)
                   .Append ((entry.Attributes & FileAttributes.Directory) != 0 ? "/\n" : "\n");
        }

        return listing.ToString ();
    }

    [Description (
                  "Search file contents with a regular expression, like grep. Returns each match with a "
                  + "little text either side, as path:line: text. Prefer this over reading a whole file "
                  + "when looking for something specific, and pass countOnly when the question is only "
                  + "how many.")]
    private static string SearchFiles (
        [Description ("Regular expression, matched against each line")] string pattern,
        [Description ("Folder to search under. Must be inside an allowed root.")] string path,
        [Description ("File name filter, e.g. *.cs. Defaults to every file.")] string filter = "*",
        [Description ("Search subfolders too. Defaults to true.")] bool recursive = true,
        [Description ("Ignore case. Defaults to false.")] bool ignoreCase = false,
        [Description ("Maximum matching lines to return. Defaults to 100.")] int limit = MaxMatches,
        [Description (
                      "Characters of context around each match. Defaults to 120. Pass 0 for the whole "
                      + "line, which on machine-written files such as .jsonl can be thousands of "
                      + "characters per match.")]
        int context = DefaultContext,
        [Description (
                      "Return per-file match counts only, with no matched text. Far cheaper, and it "
                      + "counts every match instead of stopping at the limit.")]
        bool countOnly = false)
    {
        if (!TryResolve (path, directory: true, out string full, out string refusal))
        {
            return refusal;
        }

        if (!Directory.Exists (full))
        {
            return $"no such directory: {full}";
        }

        Regex expression;

        try
        {
            expression = new Regex (
                                    pattern,
                                    ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None,
                                    TimeSpan.FromSeconds (RegexSeconds));
        }
        catch (ArgumentException ex)
        {
            return $"that is not a valid regular expression: {ex.Message}";
        }

        limit = Math.Clamp (limit, 1, MaxMatches);
        context = Math.Clamp (context, 0, MaxLineLength);

        StringBuilder found = new ();
        List<(string File, int Count)> counts = new ();
        Stopwatch clock = Stopwatch.StartNew ();
        int matches = 0;
        int scanned = 0;
        bool stopped = false;

        foreach (string file in Enumerate (full, filter, recursive))
        {
            if (clock.Elapsed.TotalSeconds > SearchSeconds
                || (!countOnly && (matches >= limit || found.Length > MaxBytes)))
            {
                stopped = true;

                break;
            }

            if (IsBinary (file))
            {
                continue;
            }

            scanned++;
            int inFile = 0;

            try
            {
                int number = 0;

                foreach (string line in File.ReadLines (file))
                {
                    number++;
                    Match match = expression.Match (line);

                    if (!match.Success)
                    {
                        continue;
                    }

                    inFile++;
                    matches++;

                    if (countOnly)
                    {
                        continue;
                    }

                    found.Append (file)
                         .Append (':')
                         .Append (number)
                         .Append (": ")
                         .Append (Window (line, match, context))
                         .Append ('\n');

                    if (matches >= limit || found.Length > MaxBytes)
                    {
                        stopped = true;

                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
            }

            if (inFile > 0)
            {
                counts.Add ((file, inFile));
            }
        }

        if (matches == 0)
        {
            return $"no matches for /{pattern}/ in {scanned:N0} file(s) under {full}";
        }

        if (countOnly)
        {
            StringBuilder tally = new ();

            tally.Append ($"{matches:N0} matching line(s) across {counts.Count:N0} of {scanned:N0} file(s) under {full}")
                 .Append (stopped ? ", search stopped early" : "")
                 .Append ('\n');

            foreach ((string file, int count) in counts.OrderByDescending (c => c.Count).Take (limit))
            {
                tally.Append (count.ToString ("N0").PadLeft (9)).Append ("  ").Append (file).Append ('\n');
            }

            return tally.ToString ();
        }

        return $"{matches} matching line(s) in {scanned:N0} file(s) under {full}"
               + (stopped ? ", stopped early -- narrow the pattern, or use countOnly to count them all" : "")
               + $"\n{found}";
    }

    private static string Window (string line, Match match, int context)
    {
        if (context == 0 || line.Length <= context)
        {
            return line.Trim ();
        }

        int half = Math.Max (16, context / 2);
        int start = Math.Max (0, match.Index - half);

        int end = Math.Min (line.Length, match.Index + Math.Min (match.Length, context) + half);

        return (start > 0 ? "..." : "") + line [start..end].Trim () + (end < line.Length ? "..." : "");
    }

    [Description (
                  "Find files by name and age without reading them, newest first. Use it to locate a file "
                  + "before reading it, or to answer what changed recently.")]
    private static string FindFiles (
        [Description ("Folder to search under. Must be inside an allowed root.")] string path,
        [Description ("Name pattern, e.g. *.cs or report-*.md. Defaults to every file.")] string filter = "*",
        [Description ("Only files modified in the last this many days. 0 means any age.")] int withinDays = 0,
        [Description ("Search subfolders too. Defaults to true.")] bool recursive = true,
        [Description ("Maximum files to return. Defaults to 200.")] int limit = MaxFound)
    {
        if (!TryResolve (path, directory: true, out string full, out string refusal))
        {
            return refusal;
        }

        if (!Directory.Exists (full))
        {
            return $"no such directory: {full}";
        }

        limit = Math.Clamp (limit, 1, MaxFound);
        DateTime since = withinDays > 0 ? DateTime.Now.AddDays (-withinDays) : DateTime.MinValue;

        Stopwatch clock = Stopwatch.StartNew ();
        List<FileInfo> files = new ();
        bool stopped = false;

        foreach (string file in Enumerate (full, filter, recursive))
        {
            if (clock.Elapsed.TotalSeconds > SearchSeconds)
            {
                stopped = true;

                break;
            }

            FileInfo info = new (file);

            try
            {
                if (info.LastWriteTime >= since)
                {
                    files.Add (info);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (files.Count == 0)
        {
            return withinDays > 0
                       ? $"no {filter} files under {full} modified in the last {withinDays} day(s)"
                       : $"no {filter} files under {full}";
        }

        StringBuilder listing = new ();

        listing.Append (files.Count)
               .Append (files.Count == 1 ? " file" : " files")
               .Append (withinDays > 0 ? $" modified in the last {withinDays} day(s)" : "")
               .Append (files.Count > limit ? $", newest {limit} shown" : ", newest first")
               .Append (stopped ? ", search stopped early" : "")
               .Append (", times are local\n");

        foreach (FileInfo file in files.OrderByDescending (f => f.LastWriteTime).Take (limit))
        {
            listing.Append (file.LastWriteTime.ToString ("yyyy-MM-dd HH:mm"))
                   .Append ("  ")
                   .Append (file.Length.ToString ("N0").PadLeft (11))
                   .Append ("  ")
                   .Append (file.FullName)
                   .Append ('\n');
        }

        return listing.ToString ();
    }

    private static bool IsBinary (string file)
    {
        try
        {
            using FileStream stream = File.OpenRead (file);
            Span<byte> head = stackalloc byte[BinarySample];

            return head [..stream.Read (head)].IndexOf ((byte) 0) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> Enumerate (string full, string filter, bool recursive)
    {
        EnumerationOptions options = new ()
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        return Directory
               .EnumerateFiles (full, filter.Trim ().Length > 0 ? filter.Trim () : "*", options)
               .Where (f => !Denied (f));
    }

    [Description (
                  "List the shape of a file with line numbers -- types, methods and properties for C#, "
                  + "headings for markdown, functions for PowerShell -- without reading it. On anything "
                  + "large, call this FIRST and then ReadFile with an offset for the part you want: an "
                  + "outline of a 3,000 line file costs about a twenty-fifth of reading it.")]
    private static string FileOutline ([Description ("Path to the file")] string path)
    {
        if (!TryResolve (path, directory: false, out string full, out string refusal))
        {
            return refusal;
        }

        if (!File.Exists (full))
        {
            return $"no such file: {full}";
        }

        Regex? pattern = Path.GetExtension (full).ToLowerInvariant () switch
        {
            ".cs" => CSharpDeclaration,
            ".md" or ".markdown" => MarkdownHeading,
            ".ps1" or ".psm1" => PowerShellFunction,
            _ => null
        };

        if (pattern is null)
        {
            return $"no outline is available for {Path.GetExtension (full)} files. "
                   + "Use SearchFiles to find what you need, or ReadFile to page through it.";
        }

        StringBuilder outline = new ();
        int number = 0;
        int shown = 0;

        try
        {
            foreach (string line in File.ReadLines (full))
            {
                number++;

                if (shown >= MaxEntries || !pattern.IsMatch (line))
                {
                    continue;
                }

                string text = line.Trim ();

                outline.Append (number)
                       .Append ('\u2192')
                       .Append (text.Length > MaxLineLength ? text [..MaxLineLength] + "..." : text)
                       .Append ('\n');

                shown++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return $"could not read {full}: {ex.Message}";
        }

        if (shown == 0)
        {
            return $"{full} has {number:N0} lines and nothing that looks like a declaration or heading.";
        }

        return $"{shown:N0} of {number:N0} lines in {full}"
               + (shown >= MaxEntries ? $", stopped at {MaxEntries}" : "")
               + $"\n{outline}";
    }

    [Description (
                  "Find TODO, FIXME, HACK and similar markers under a folder, oldest file first, so the "
                  + "longest-standing ones come up before today's. Use it to review outstanding work.")]
    private static string FindTodos (
        [Description ("Folder to scan. Must be inside an allowed root.")] string path,
        [Description ("Maximum markers to return. Defaults to 100.")] int limit = MaxMatches)
    {
        if (!TryResolve (path, directory: true, out string full, out string refusal))
        {
            return refusal;
        }

        if (!Directory.Exists (full))
        {
            return $"no such directory: {full}";
        }

        limit = Math.Clamp (limit, 1, MaxMatches);

        List<TodoHit> hits = TodoScanner.SortByFileAge (
                                                        TodoScanner.Scan (full, CancellationToken.None)
                                                                   .Where (h => !Denied (h.FullPath)));

        if (hits.Count == 0)
        {
            return $"no TODO markers under {full}";
        }

        StringBuilder found = new ();

        found.Append (hits.Count)
             .Append (hits.Count == 1 ? " marker under " : " markers under ")
             .Append (full)
             .Append (hits.Count > limit
                          ? $", {limit} least recently modified shown"
                          : ", least recently modified first")
             .Append ('\n');

        foreach (TodoHit hit in hits.Take (limit))
        {
            found.Append (hit.FullPath)
                 .Append (':')
                 .Append (hit.Line)
                 .Append (": ")
                 .Append (hit.Tag)
                 .Append (' ')
                 .Append (hit.Text.Trim ())
                 .Append ('\n');
        }

        return found.ToString ();
    }

    [Description (
                  "Write a report into the reports folder. Reports appear in the Script history widget. "
                  + "Reports and reviews are the only things that can be written anywhere.")]
    private static string WriteReport (
        [Description ("File name only, no folders, e.g. report-2026-09-01.md")] string name,
        [Description ("The full text of the report")] string content) =>
        WriteInto (UserSettings.ReportsPath, "report", name, content);

    [Description (
                  "Write a review into the reviews folder. Reviews appear in the Review history widget "
                  + "on the AI tab. Use this for anything evaluative -- a code review, a self-evaluation, "
                  + "a critique -- and WriteReport for everything else.")]
    private static string WriteReview (
        [Description ("File name only, no folders, e.g. review-2026-09-01.md")] string name,
        [Description ("The full text of the review")] string content) =>
        WriteInto (UserSettings.ReviewsPath, "review", name, content);

    [Description (
                  "Add a section to the END of a review, creating the file if it does not exist. Use "
                  + "this to build one report across several turns: each turn appends only its own "
                  + "part, instead of re-emitting everything already written.")]
    private static string AppendReview (
        [Description ("File name only, no folders, e.g. review-2026-09-01.md")] string name,
        [Description ("The text to add to the end of the file")] string content) =>
        WriteInto (UserSettings.ReviewsPath, "review", name, content, append: true);

    private static string WriteInto (string destination, string what, string name, string content, bool append = false)
    {
        string safe = Path.GetFileName (name.Trim ());

        if (safe.Length == 0)
        {
            return "a file name is required";
        }

        if (content.Length > MaxBytes)
        {
            return $"that {what} is larger than {MaxBytes:N0} characters; refusing to write it";
        }

        string folder = Path.GetFullPath (destination);
        string full = Path.GetFullPath (Path.Combine (folder, safe));

        if (!full.StartsWith (folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return $"a {what} can only be written directly inside {folder}";
        }

        if (Denied (full))
        {
            return $"{safe} is denied by policy";
        }

        if (append && File.Exists (full) && new FileInfo (full).Length + content.Length > MaxBytes)
        {
            return $"appending would take {safe} past {MaxBytes:N0} characters "
                   + $"(it is already {new FileInfo (full).Length:N0})";
        }

        try
        {
            Directory.CreateDirectory (folder);

            if (append)
            {
                File.AppendAllText (full, content);
            }
            else
            {
                File.WriteAllText (full, content);
            }

            return append ? $"appended {content.Length:N0} characters to {full}" : $"wrote {full}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not write {safe}: {ex.Message}";
        }
    }

    private static bool TryResolve (string path, bool directory, out string full, out string refusal)
    {
        refusal = "";

        try
        {
            full = Path.GetFullPath (path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = "";
            refusal = $"\"{path}\" is not a usable path: {ex.Message}";

            return false;
        }

        try
        {
            FileSystemInfo info = directory ? new DirectoryInfo (full) : new FileInfo (full);
            full = Path.GetFullPath (info.ResolveLinkTarget (true)?.FullName ?? full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        string[] roots = Roots ();

        if (roots.Length == 0)
        {
            refusal = "No file roots are configured, so nothing can be read. "
                      + "Add one under Settings > Preferences.";

            return false;
        }

        string candidate = full;

        if (!roots.Any (
                        r => candidate.Equals (r, StringComparison.OrdinalIgnoreCase)
                             || candidate.StartsWith (r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            refusal = $"{full} is outside the allowed roots. Readable roots are: {string.Join ("; ", roots)}. "
                      + "Use an absolute path inside one of them, or call ListDirectory on a root to find the file.";

            return false;
        }

        if (Denied (full))
        {
            refusal = $"{full} is denied by policy. Credentials and key material are never readable, "
                      + "and no path will make them readable.";

            return false;
        }

        return true;
    }

    private static bool Denied (string full)
    {
        string name = Path.GetFileName (full);

        if (name.StartsWith (".env", StringComparison.OrdinalIgnoreCase)
            || DeniedNames.Any (d => name.Equals (d, StringComparison.OrdinalIgnoreCase))
            || DeniedExtensions.Any (e => name.EndsWith (e, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return full
               .Split (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Any (segment => DeniedFolders.Any (d => segment.Equals (d, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Arithmetic the answer can be trusted on.</summary>
internal static class MathTools
{
    public static IList<AITool> Tools { get; } =
    [
        AIFunctionFactory.Create (Calculate),
        AIFunctionFactory.Create (Statistics)
    ];

    [Description (
                  "Evaluate an arithmetic expression exactly. Supports + - * / % ^, parentheses, the "
                  + "functions sqrt abs round floor ceil log log10 exp min max, and the constants pi and e. "
                  + "Write numbers plainly, without thousands separators.")]
    private static string Calculate (
        [Description ("For example: (11400000 - 9800000) / 9800000 * 100")] string expression)
    {
        try
        {
            double value = Evaluate (expression);

            if (double.IsNaN (value))
            {
                return "that is not a number (undefined, such as 0/0)";
            }

            if (double.IsInfinity (value))
            {
                return value > 0 ? "infinity (divided by zero)" : "negative infinity (divided by zero)";
            }

            return value.ToString ("G15", CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            return $"cannot evaluate \"{expression}\": {ex.Message}";
        }
    }

    [Description (
                  "Count, sum, mean, median, smallest, largest and standard deviation of a list of "
                  + "numbers. Use it instead of adding a column up by hand.")]
    private static string Statistics (
        [Description ("The numbers, separated by commas, spaces or newlines")] string numbers)
    {
        List<double> values = new ();
        List<string> unparsed = new ();

        foreach (string part in numbers.Split (
                                               new [] { ',', ' ', '\t', '\n', '\r', ';' },
                                               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse (part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                values.Add (value);
            }
            else
            {
                unparsed.Add (part);
            }
        }

        if (values.Count == 0)
        {
            return "no numbers found in that input";
        }

        double[] sorted = values.Order ().ToArray ();
        double sum = values.Sum ();
        double mean = sum / values.Count;

        double median = sorted.Length % 2 == 1
                            ? sorted [sorted.Length / 2]
                            : (sorted [sorted.Length / 2 - 1] + sorted [sorted.Length / 2]) / 2;

        double deviation = values.Count > 1
                               ? Math.Sqrt (values.Sum (v => (v - mean) * (v - mean)) / (values.Count - 1))
                               : 0;

        StringBuilder result = new ();

        result.Append ($"count   {values.Count:N0}\n")
              .Append ($"sum     {Show (sum)}\n")
              .Append ($"mean    {Show (mean)}\n")
              .Append ($"median  {Show (median)}\n")
              .Append ($"min     {Show (sorted [0])}\n")
              .Append ($"max     {Show (sorted [^1])}\n")
              .Append ($"stdev   {Show (deviation)}");

        if (unparsed.Count > 0)
        {
            result.Append ($"\n\nignored {unparsed.Count} unreadable entr{(unparsed.Count == 1 ? "y" : "ies")}: ")
                  .Append (string.Join (", ", unparsed.Take (10)));
        }

        return result.ToString ();

        static string Show (double value) => value.ToString ("G15", CultureInfo.InvariantCulture);
    }

    private static double Evaluate (string expression)
    {
        int position = 0;
        double value = ParseSum (expression, ref position);

        SkipSpace (expression, ref position);

        if (position < expression.Length)
        {
            throw new FormatException (Unexpected (expression, position));
        }

        return value;
    }

    private static double ParseSum (string s, ref int i)
    {
        double left = ParseProduct (s, ref i);

        while (true)
        {
            SkipSpace (s, ref i);

            if (i >= s.Length || (s [i] != '+' && s [i] != '-'))
            {
                return left;
            }

            char op = s [i++];
            double right = ParseProduct (s, ref i);
            left = op == '+' ? left + right : left - right;
        }
    }

    private static double ParseProduct (string s, ref int i)
    {
        double left = ParseUnary (s, ref i);

        while (true)
        {
            SkipSpace (s, ref i);

            if (i >= s.Length || (s [i] != '*' && s [i] != '/' && s [i] != '%'))
            {
                return left;
            }

            char op = s [i++];
            double right = ParseUnary (s, ref i);

            left = op switch
            {
                '*' => left * right,
                '/' => left / right,
                _ => left % right
            };
        }
    }

    private static double ParseUnary (string s, ref int i)
    {
        SkipSpace (s, ref i);

        if (i < s.Length && (s [i] == '-' || s [i] == '+'))
        {
            char sign = s [i++];
            double value = ParseUnary (s, ref i);

            return sign == '-' ? -value : value;
        }

        return ParsePower (s, ref i);
    }

    private static double ParsePower (string s, ref int i)
    {
        double left = ParseAtom (s, ref i);
        SkipSpace (s, ref i);

        if (i >= s.Length || s [i] != '^')
        {
            return left;
        }

        i++;

        return Math.Pow (left, ParseUnary (s, ref i));
    }

    private static double ParseAtom (string s, ref int i)
    {
        SkipSpace (s, ref i);

        if (i >= s.Length)
        {
            throw new FormatException ("the expression ends where a number was expected");
        }

        if (s [i] == '(')
        {
            i++;
            double value = ParseSum (s, ref i);
            Expect (s, ref i, ')');

            return value;
        }

        if (char.IsAsciiDigit (s [i]) || s [i] == '.')
        {
            int start = i;

            while (i < s.Length && (char.IsAsciiDigit (s [i]) || s [i] == '.' || s [i] == '_'))
            {
                i++;
            }

            if (i < s.Length && (s [i] == 'e' || s [i] == 'E'))
            {
                int mark = i + 1;

                if (mark < s.Length && (s [mark] == '+' || s [mark] == '-'))
                {
                    mark++;
                }

                if (mark < s.Length && char.IsAsciiDigit (s [mark]))
                {
                    i = mark;

                    while (i < s.Length && char.IsAsciiDigit (s [i]))
                    {
                        i++;
                    }
                }
            }

            string literal = s [start..i].Replace ("_", "");

            return double.TryParse (literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                       ? number
                       : throw new FormatException ($"\"{literal}\" is not a number");
        }

        if (char.IsAsciiLetter (s [i]))
        {
            int start = i;

            while (i < s.Length && (char.IsAsciiLetter (s [i]) || char.IsAsciiDigit (s [i])))
            {
                i++;
            }

            string name = s [start..i].ToLowerInvariant ();
            SkipSpace (s, ref i);

            if (i >= s.Length || s [i] != '(')
            {
                return name switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    _ => throw new FormatException ($"unknown name \"{name}\"")
                };
            }

            i++;
            List<double> arguments = new () { ParseSum (s, ref i) };
            SkipSpace (s, ref i);

            while (i < s.Length && s [i] == ',')
            {
                i++;
                arguments.Add (ParseSum (s, ref i));
                SkipSpace (s, ref i);
            }

            Expect (s, ref i, ')');

            return Apply (name, arguments);
        }

        throw new FormatException (Unexpected (s, i));
    }

    private static double Apply (string name, List<double> a) =>
        (name, a.Count) switch
        {
            ("sqrt", 1) => Math.Sqrt (a [0]),
            ("abs", 1) => Math.Abs (a [0]),
            ("round", 1) => Math.Round (a [0], MidpointRounding.AwayFromZero),
            ("round", 2) => Math.Round (a [0], (int) a [1], MidpointRounding.AwayFromZero),
            ("floor", 1) => Math.Floor (a [0]),
            ("ceil", 1) => Math.Ceiling (a [0]),
            ("log", 1) => Math.Log (a [0]),
            ("log", 2) => Math.Log (a [0], a [1]),
            ("log10", 1) => Math.Log10 (a [0]),
            ("exp", 1) => Math.Exp (a [0]),
            ("min", > 0) => a.Min (),
            ("max", > 0) => a.Max (),
            _ => throw new FormatException ($"no function \"{name}\" taking {a.Count} argument(s)")
        };

    private static void Expect (string s, ref int i, char c)
    {
        SkipSpace (s, ref i);

        if (i >= s.Length || s [i] != c)
        {
            throw new FormatException ($"expected '{c}' at position {i}");
        }

        i++;
    }

    private static void SkipSpace (string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace (s [i]))
        {
            i++;
        }
    }

    private static string Unexpected (string s, int i) =>
        $"unexpected '{s [i]}' at position {i}"
        + (s [i] == ',' ? " -- write numbers without thousands separators" : "");
}

/// <summary>Token accounting over Claude Code's own session transcripts.</summary>
internal static class TranscriptTools
{
    public static IList<AITool> Tools { get; } = [AIFunctionFactory.Create (TokenUsage)];

    [Description (
                  "Token usage from Claude Code's session transcripts: totals, a per-session breakdown "
                  + "and tool-call counts. ALWAYS prefer this to reading or searching the .jsonl "
                  + "transcripts yourself -- they cost hundreds of times more to parse by hand, and "
                  + "usage repeats across lines so summing them directly double-counts.")]
    private static string TokenUsage (
        [Description (
                      "Only sessions for this project. Matches either the working directory recorded in "
                      + "the session (C:/dev/ttm) or the transcript folder name (c--dev-ttm), so either "
                      + "form works. Empty means every project.")]
        string project = "",
        [Description ("How many sessions to list, newest first. Defaults to 20.")]
        int limit = 20)
    {
        ClaudeStats stats = ClaudeTranscripts.Collect ();

        string wanted = project.Trim ().Replace (Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        List<ClaudeSession> sessions = stats
                                       .Sessions.Where (
                                                        s => wanted.Length == 0
                                                             || s.Project.Replace (Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                                                                .Contains (wanted, StringComparison.OrdinalIgnoreCase)
                                                             || (Path.GetDirectoryName (s.Path) ?? "")
                                                                .Contains (wanted, StringComparison.OrdinalIgnoreCase))
                                       .OrderByDescending (s => s.Ended)
                                       .ToList ();

        if (sessions.Count == 0)
        {
            return wanted.Length > 0
                       ? $"no sessions found for a project matching \"{wanted}\""
                       : $"no Claude Code sessions found under {ClaudeTranscripts.Root ()}";
        }

        long input = sessions.Sum (s => s.Input);
        long output = sessions.Sum (s => s.Output);
        long cacheRead = sessions.Sum (s => s.CacheRead);
        long cacheWrite = sessions.Sum (s => s.CacheWrite);

        StringBuilder report = new ();

        report.Append (sessions.Count)
              .Append (sessions.Count == 1 ? " session" : " sessions")
              .Append (wanted.Length > 0 ? $" matching \"{wanted}\"" : " across every project")
              .Append ("\n\n")
              .Append ($"input        {input,16:N0}\n")
              .Append ($"output       {output,16:N0}\n")
              .Append ($"cache read   {cacheRead,16:N0}\n")
              .Append ($"cache write  {cacheWrite,16:N0}\n")
              .Append ($"total        {input + output + cacheRead + cacheWrite,16:N0}\n\n")
              .Append ("Cache reads are usually most of that total and are billed well below fresh\n")
              .Append ("input, so price the four lines separately rather than the total.\n\n");

        if (stats.ToolCalls.Count > 0 && wanted.Length == 0)
        {
            report.Append ("tool calls   ")
                  .Append (
                           string.Join (
                                        ", ",
                                        stats.ToolCalls.OrderByDescending (t => t.Value)
                                             .Take (8)
                                             .Select (t => $"{t.Key} {t.Value:N0}")))
                  .Append ("\n\n");
        }

        report.Append ("session   started           ended              msgs  tools")
              .Append ("           input          output      cacheRead     cacheWrite\n");

        foreach (ClaudeSession session in sessions.Take (Math.Max (1, limit)))
        {
            report.Append (session.Id.Length >= 8 ? session.Id [..8] : session.Id.PadRight (8))
                  .Append ("  ")
                  .Append (session.Started.ToString ("yyyy-MM-dd HH:mm"))
                  .Append ("  ")
                  .Append (session.Ended.ToString ("yyyy-MM-dd HH:mm"))
                  .Append ($"  {session.Messages,5}")
                  .Append ($"  {session.ToolCalls,5}")
                  .Append ($"  {session.Input,14:N0}")
                  .Append ($"  {session.Output,14:N0}")
                  .Append ($"  {session.CacheRead,14:N0}")
                  .Append ($"  {session.CacheWrite,14:N0}")
                  .Append ('\n');
        }

        if (sessions.Count > limit)
        {
            report.Append ($"\n[{sessions.Count - limit:N0} older session(s) not listed; totals above cover all {sessions.Count:N0}]\n");
        }

        report.Append ("\nTimes are local. Totals cover every transcript on disk, not a fixed window.\n");

        return report.ToString ();
    }
}

