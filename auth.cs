// -----------------------------------------------------------------------------
//  Credential storage: one passphrase, a stored verifier, and AES-GCM auth entries.
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     The encryption key derived from a passphrase. Held only for the length of one operation -
///     nothing caches it across actions, so every auth use asks for the passphrase again.
/// </summary>
internal sealed class AuthKey : IDisposable
{
    private readonly byte[] _key;

    internal AuthKey (byte[] key) => _key = key;

    internal ReadOnlySpan<byte> Bytes => _key;

    public void Dispose () => CryptographicOperations.ZeroMemory (_key);
}

/// <summary>One encrypted credential, as stored in credentials.json.</summary>
internal sealed record AuthEntry
{
    [JsonPropertyName ("nonce")] public string Nonce { get; init; } = "";

    [JsonPropertyName ("tag")] public string Tag { get; init; } = "";

    [JsonPropertyName ("ciphertext")] public string Ciphertext { get; init; } = "";

    [JsonPropertyName ("created")] public string Created { get; init; } = "";
}

/// <summary>How the passphrase was stretched. Recorded so a future change can migrate a file.</summary>
internal sealed record AuthKdf
{
    [JsonPropertyName ("algorithm")] public string Algorithm { get; init; } = AuthStore.KdfName;

    [JsonPropertyName ("iterations")] public int Iterations { get; init; } = AuthStore.Iterations;

    [JsonPropertyName ("salt")] public string Salt { get; init; } = "";
}

/// <summary>The whole credentials file.</summary>
internal sealed record AuthFile
{
    [JsonPropertyName ("version")] public int Version { get; init; } = 1;

    [JsonPropertyName ("kdf")] public AuthKdf? Kdf { get; init; }

    [JsonPropertyName ("verifier")] public string Verifier { get; init; } = "";

    [JsonPropertyName ("auths")] public Dictionary<string, AuthEntry> Auths { get; init; } = new (StringComparer.Ordinal);
}

/// <summary>
///     Every stored credential, encrypted under one passphrase.
///
///     The passphrase is stretched once with PBKDF2, then split two ways with HKDF: a verifier
///     that IS written to disk and compared whenever the passphrase is typed, and an encryption
///     key that is NEVER written and only exists while an operation runs. Knowing the verifier
///     does not give you the encryption key.
///
///     Entries are keyed by a namespaced id - "git:github.com", "git:git.lan" - so the store is
///     not git-specific and callers can enumerate or empty all of them at once. The value is an
///     opaque string; what it means belongs to whoever stored it.
///
///     The file lives beside the local application data, deliberately OUTSIDE the synced
///     total-manager folder, so a credential can never be committed and pushed to a remote.
/// </summary>
internal static class AuthStore
{
    public const string KdfName = "PBKDF2-SHA256";

    public const int Iterations = 600_000;

    public const string FileName = "credentials.json";

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private const string VerifyInfo = "ttm-verify";
    private const string EncryptInfo = "ttm-encrypt";

    private static readonly JsonSerializerOptions Json = new () { WriteIndented = true };

    /// <summary>
    ///     %LOCALAPPDATA%\total-manager on Windows, ~/.local/share/total-manager on Linux -
    ///     never the roaming folder that the sync replicates.
    /// </summary>
    public static string FolderPath => Path.Combine (
                                                     Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData),
                                                     UserSettings.FolderName);

    public static string FilePath => Path.Combine (FolderPath, FileName);

    public static string? Error { get; private set; }

    public static bool HasPassphrase => Read () is { Kdf: not null } file && file.Verifier.Length > 0;

    public static int Count => Read ()?.Auths.Count ?? 0;

    /// <summary>The ids that have a credential stored. Needs no passphrase and decrypts nothing.</summary>
    public static string[] Ids () => Read ()?.Auths.Keys.OrderBy (k => k, StringComparer.Ordinal).ToArray () ?? [];

    public static bool Has (string id) => Read ()?.Auths.ContainsKey (id) == true;

    /// <summary>The key for this passphrase, or null when it does not match the stored verifier.</summary>
    public static AuthKey? Unlock (string passphrase)
    {
        Error = null;

        if (Read () is not { Kdf: not null } file || file.Verifier.Length == 0)
        {
            Error = "no passphrase has been set";

            return null;
        }

        byte[] master = Master (passphrase, Convert.FromBase64String (file.Kdf.Salt), file.Kdf.Iterations);

        try
        {
            byte[] verifier = Derive (master, VerifyInfo);

            if (!CryptographicOperations.FixedTimeEquals (verifier, Convert.FromBase64String (file.Verifier)))
            {
                Error = "that passphrase does not match";

                return null;
            }

            return new AuthKey (Derive (master, EncryptInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory (master);
        }
    }

    /// <summary>First run: set a passphrase when none exists.</summary>
    public static bool Create (string passphrase)
    {
        if (HasPassphrase)
        {
            Error = "a passphrase is already set";

            return false;
        }

        return Rebuild (passphrase, new Dictionary<string, string> (StringComparer.Ordinal));
    }

    /// <summary>
    ///     Change the passphrase, keeping every stored credential: the old key decrypts them and
    ///     the new key re-encrypts them. Fails without touching the file if the old one is wrong.
    /// </summary>
    public static bool Change (string oldPassphrase, string newPassphrase)
    {
        using AuthKey? key = Unlock (oldPassphrase);

        if (key is null || Read () is not { } file)
        {
            return false;
        }

        Dictionary<string, string> plain = new (StringComparer.Ordinal);

        foreach ((string id, AuthEntry entry) in file.Auths)
        {
            if (Decrypt (id, entry, key) is not { } value)
            {
                Error = $"could not decrypt {id} - the credentials file may be damaged";

                return false;
            }

            plain [id] = value;
        }

        return Rebuild (newPassphrase, plain);
    }

    /// <summary>
    ///     Forgotten passphrase: set a new one and destroy every stored credential, because
    ///     without the old passphrase no key exists that can read them.
    /// </summary>
    public static bool Reset (string passphrase) =>
        Rebuild (passphrase, new Dictionary<string, string> (StringComparer.Ordinal));

    public static string? Get (string id, AuthKey key) =>
        Read () is { } file && file.Auths.TryGetValue (id, out AuthEntry? entry) ? Decrypt (id, entry, key) : null;

    public static bool Set (string id, string value, AuthKey key)
    {
        if (Read () is not { Kdf: not null } file)
        {
            Error = "no passphrase has been set";

            return false;
        }

        Dictionary<string, AuthEntry> auths = new (file.Auths, StringComparer.Ordinal) { [id] = Encrypt (id, value, key) };

        return Write (file with { Auths = auths });
    }

    public static bool Remove (string id)
    {
        if (Read () is not { } file || !file.Auths.ContainsKey (id))
        {
            return false;
        }

        Dictionary<string, AuthEntry> auths = new (file.Auths, StringComparer.Ordinal);
        auths.Remove (id);

        return Write (file with { Auths = auths });
    }

    /// <summary>Empty every stored credential, keeping the passphrase in place.</summary>
    public static bool ClearAll () =>
        Read () is { } file && Write (file with { Auths = new Dictionary<string, AuthEntry> (StringComparer.Ordinal) });

    private static bool Rebuild (string passphrase, Dictionary<string, string> plain)
    {
        Error = null;

        byte[] salt = RandomNumberGenerator.GetBytes (SaltSize);
        byte[] master = Master (passphrase, salt, Iterations);

        try
        {
            using AuthKey key = new (Derive (master, EncryptInfo));

            Dictionary<string, AuthEntry> auths = new (StringComparer.Ordinal);

            foreach ((string id, string value) in plain)
            {
                auths [id] = Encrypt (id, value, key);
            }

            return Write (
                          new AuthFile
                          {
                              Kdf = new AuthKdf { Salt = Convert.ToBase64String (salt) },
                              Verifier = Convert.ToBase64String (Derive (master, VerifyInfo)),
                              Auths = auths
                          });
        }
        finally
        {
            CryptographicOperations.ZeroMemory (master);
        }
    }

    private static byte[] Master (string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2 (Encoding.UTF8.GetBytes (passphrase), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static byte[] Derive (byte[] master, string info) =>
        HKDF.DeriveKey (HashAlgorithmName.SHA256, master, KeySize, info: Encoding.UTF8.GetBytes (info));

    /// <summary>
    ///     Encrypts one entry, with its id as associated data so the id is authenticated too.
    ///     Without that binding, anyone who can write the file could move the ciphertext of one
    ///     entry under another id - relabelling a github token as belonging to a host they
    ///     control - and the tag would still verify. They cannot forge or read it either way, but
    ///     they could redirect where it gets sent.
    /// </summary>
    private static AuthEntry Encrypt (string id, string value, AuthKey key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes (NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes (value);
        byte[] cipher = new byte [plain.Length];
        byte[] tag = new byte [TagSize];

        using AesGcm gcm = new (key.Bytes, TagSize);

        gcm.Encrypt (nonce, plain, cipher, tag, Encoding.UTF8.GetBytes (id));

        CryptographicOperations.ZeroMemory (plain);

        return new AuthEntry
        {
            Nonce = Convert.ToBase64String (nonce),
            Tag = Convert.ToBase64String (tag),
            Ciphertext = Convert.ToBase64String (cipher),
            Created = DateTime.UtcNow.ToString ("yyyy-MM-dd HH:mm:ss")
        };
    }

    private static string? Decrypt (string id, AuthEntry entry, AuthKey key)
    {
        try
        {
            byte[] cipher = Convert.FromBase64String (entry.Ciphertext);
            byte[] plain = new byte [cipher.Length];

            using AesGcm gcm = new (key.Bytes, TagSize);

            gcm.Decrypt (
                         Convert.FromBase64String (entry.Nonce),
                         cipher,
                         Convert.FromBase64String (entry.Tag),
                         plain,
                         Encoding.UTF8.GetBytes (id));

            string value = Encoding.UTF8.GetString (plain);

            CryptographicOperations.ZeroMemory (plain);

            return value;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            Error = "could not decrypt the stored credential - wrong passphrase, or the file was altered";

            return null;
        }
    }

    private static AuthFile? Read ()
    {
        try
        {
            return File.Exists (FilePath)
                       ? JsonSerializer.Deserialize<AuthFile> (File.ReadAllText (FilePath)) ?? new AuthFile ()
                       : new AuthFile ();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = ex.Message;

            return null;
        }
    }

    private static bool Write (AuthFile file)
    {
        try
        {
            Directory.CreateDirectory (FolderPath);
            File.WriteAllText (FilePath, JsonSerializer.Serialize (file, Json));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;

            return false;
        }
    }
}
