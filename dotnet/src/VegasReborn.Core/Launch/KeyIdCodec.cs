namespace VegasReborn.Core.Launch;

/// <summary>
/// Vegas 2 stores its CD key as [CDKEY] KEYID: RC4 with the key "Tigu" over a 24-byte,
/// NUL-padded key buffer, written as lower-case hex (RainbowSixVegas2_SADS writer
/// 0x113e9ed7, reader 0x113ea54d). The decrypted text is the key the client uses in its
/// ticket proofs. A realm's nativeLicenseId is already in this form: it decodes to the
/// master's native test key, and Vegas 1 stores the same value as KEYID.
/// </summary>
public static class KeyIdCodec
{
    public const int BufferSize = 24;
    private static readonly byte[] Rc4Key = "Tigu"u8.ToArray();

    public static string Encode(string cdKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(cdKey);
        if (cdKey.Length >= BufferSize || cdKey.Any(c => c is < '!' or > '~'))
        {
            throw new ArgumentException($"The CD key must be 1-{BufferSize - 1} printable ASCII characters", nameof(cdKey));
        }
        var buffer = new byte[BufferSize];
        for (var i = 0; i < cdKey.Length; i++)
        {
            buffer[i] = (byte)cdKey[i];
        }
        return Convert.ToHexStringLower(Rc4(Rc4Key, buffer));
    }

    public static string Decode(string keyId)
    {
        var plain = Rc4(Rc4Key, Convert.FromHexString(keyId));
        var end = Array.IndexOf(plain, (byte)0);
        return System.Text.Encoding.ASCII.GetString(plain, 0, end < 0 ? plain.Length : end);
    }

    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        var state = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xff;
            (state[i], state[j]) = (state[j], state[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xff;
            j = (j + state[i]) & 0xff;
            (state[i], state[j]) = (state[j], state[i]);
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 0xff]);
        }
        return output;
    }
}
