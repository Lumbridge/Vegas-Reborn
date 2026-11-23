namespace VegasReborn.Core.Update;

/// <summary>Public Ed25519 release-verification key. The private key is never distributed.</summary>
/// <remarks>
/// Identical to <c>src/vegas_reborn/update_key.py</c>. Rotating it requires an intentional
/// trust migration shipped in an already-trusted release.
/// </remarks>
public static class UpdateKey
{
    public const string PublicKeyHex = "ffbf089cf62ebe0034b6dbccbbf6a08885d49e626973e8f1fa79042ea6e5eb4a";
}
