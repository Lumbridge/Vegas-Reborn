using System.Globalization;
using System.Text.Json;

namespace VegasReborn.Core.Account;

/// <summary>
/// Reads and writes <c>local/companion/account-session.json</c> in the same shape as the
/// Python companion, so existing users stay signed in:
/// <c>{"version":1,"account":{"id","name","claimedName","nameClaimsEnabled","claimRequired","nameChangeAllowed"},"token","savedAt"}</c>.
/// </summary>
public static class AccountSessionStore
{
    private const int MaxFileBytes = 16384;

    /// <summary>Writes a new file; fails with <see cref="IOException"/> if it already exists.</summary>
    public static string Save(string path, AccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var file = new SessionFile(1,
            new SessionFileAccount(session.DiscordId, session.DisplayName, session.ClaimedName,
                session.NameClaimsEnabled, session.ClaimRequired, session.NameChangeAllowed),
            session.Token, Timestamp());
        return WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(file, AccountJsonContext.Default.SessionFile));
    }

    /// <summary>Replaces the saved session through a <c>.new</c> file, as the companion does after sign-in, refresh or claim.</summary>
    public static void Persist(string path, AccountSession session)
    {
        var temporary = path + ".new";
        File.Delete(temporary);
        Save(temporary, session);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Loads a saved session. Throws <see cref="FileNotFoundException"/> when missing and
    /// <see cref="AccountException"/> (<c>invalid_account_session_file</c>) when unusable.</summary>
    public static AccountSession Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("No saved account session.", path);
        }
        if (info.Length > MaxFileBytes)
        {
            throw new AccountException("invalid_account_session_file");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var token = StrictJson.GetString(StrictJson.Get(root, "token"));
            if (root.ValueKind != JsonValueKind.Object || !StrictJson.TryGetInteger(StrictJson.Get(root, "version"), out var version)
                || version != 1 || !AccountRules.IsToken(token))
            {
                throw new AccountException("invalid_account_session_file");
            }
            return DiscordClient.SessionFromAccount(StrictJson.Get(root, "account"), token!);
        }
        catch (AccountException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AccountException("invalid_account_session_file");
        }
    }

    /// <summary>The saved session, or null when there is none or it cannot be used.</summary>
    public static AccountSession? TryLoad(string path)
    {
        try
        {
            return Load(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AccountException)
        {
            return null;
        }
    }

    /// <summary>Signs out locally by removing the saved session.</summary>
    public static void Delete(string path) => File.Delete(path);

    /// <summary>Writes a new private lease file; fails if it already exists.</summary>
    public static string SaveNativeLease(string path, NativeLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var file = new NativeLeaseFile(1, lease.Username, lease.Password, lease.ExpiresIn, lease.DiscordId, lease.DisplayName, Timestamp());
        return WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(file, AccountJsonContext.Default.NativeLeaseFile));
    }

    private static string WriteNew(string path, byte[] json)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(json);
        output.WriteByte((byte)'\n');
        output.Flush(flushToDisk: true);
        return full;
    }

    // Python datetime.isoformat() for an aware UTC time.
    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
}
