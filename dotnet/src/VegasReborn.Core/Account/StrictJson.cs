using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VegasReborn.Core.Account;

/// <summary>
/// Type-exact JSON checks matching the Python service code: integers are integer
/// literals (never booleans or decimals), a missing key and an explicit null are the
/// same unless stated otherwise.
/// </summary>
internal static class StrictJson
{
    public static bool IsObject(JsonElement? value) => value is { ValueKind: JsonValueKind.Object };

    /// <summary>Like Python's dict.get: null for a missing key or JSON null.</summary>
    public static JsonElement? Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property
            : null;

    public static bool Has(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);

    public static bool IsIntegerLiteral(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Number } number)
        {
            return false;
        }
        var raw = number.GetRawText();
        return raw.IndexOfAny(['.', 'e', 'E']) < 0;
    }

    /// <summary>An integer literal that fits in a long.</summary>
    public static bool TryGetInteger(JsonElement? value, out long result)
    {
        result = 0;
        return IsIntegerLiteral(value) && value!.Value.TryGetInt64(out result);
    }

    public static string? GetString(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    public static bool? GetBool(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>Python's <c>type(x) in (int, float)</c> for a finite number.</summary>
    public static bool TryGetNumber(JsonElement? value, out double result)
    {
        result = 0;
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out result) && double.IsFinite(result);
    }

    /// <summary>Python's str(int) for an integer literal, or the string itself.</summary>
    public static string? IdText(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number } when IsIntegerLiteral(value) =>
            System.Numerics.BigInteger.Parse(value.Value.GetRawText(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    public static int CodePoints(string value) => value.EnumerateRunes().Count();
}

internal sealed record LinkPollRequest(string Secret);

internal sealed record ClaimNameRequest(string Name);

internal sealed record GuestRequest(string InstallId, string Name);

internal sealed record MintRequest(string StatisticsCodec);

internal sealed record EmptyRequest;

internal sealed record SessionFileAccount(
    string Id,
    string Name,
    string? ClaimedName,
    bool NameClaimsEnabled,
    bool ClaimRequired,
    string? NameChangeAllowed,
    // Written only for guests, so a Discord session file keeps the Python companion's shape.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Guest = false);

/// <summary>local/companion/account-session.json, shared with the Python companion.</summary>
internal sealed record SessionFile(int Version, SessionFileAccount Account, string Token, string SavedAt);

internal sealed record NativeLeaseFile(int Version, string Username, string Password, int ExpiresIn, string DiscordId, string DisplayName, string SavedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LinkPollRequest))]
[JsonSerializable(typeof(ClaimNameRequest))]
[JsonSerializable(typeof(GuestRequest))]
[JsonSerializable(typeof(MintRequest))]
[JsonSerializable(typeof(EmptyRequest))]
[JsonSerializable(typeof(SessionFile))]
[JsonSerializable(typeof(NativeLeaseFile))]
internal sealed partial class AccountJsonContext : JsonSerializerContext;
