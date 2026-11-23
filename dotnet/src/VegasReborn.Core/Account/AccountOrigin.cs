namespace VegasReborn.Core.Account;

/// <summary>A fixed HTTPS origin: no credentials, path, query or fragment.</summary>
public sealed record AccountOrigin(string Host, int Port)
{
    public Uri BaseAddress => new($"https://{(Host.Contains(':') ? "[" + Host + "]" : Host)}:{Port}/");

    /// <summary>Returns null when the value is not an acceptable HTTPS origin.</summary>
    public static AccountOrigin? TryParse(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is <= 0 or > 65535)
        {
            return null;
        }
        // Uri normalises the path; reject anything after the authority in the original text.
        var authority = value.IndexOf("://", StringComparison.Ordinal);
        if (authority < 0)
        {
            return null;
        }
        var afterScheme = value[(authority + 3)..];
        var slash = afterScheme.IndexOfAny(['/', '?', '#']);
        if (slash >= 0 && afterScheme[slash..] != "/")
        {
            return null;
        }
        return new AccountOrigin(uri.IdnHost.ToLowerInvariant(), uri.Port);
    }

    public static AccountOrigin Parse(string value, string errorCode) =>
        TryParse(value) ?? throw new AccountException(errorCode);
}
