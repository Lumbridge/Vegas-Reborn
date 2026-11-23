using System.Security.Cryptography;
using System.Text;

namespace VegasReborn.Core.Launch;

/// <summary>Atomic replace of companion-owned files (temporary sibling, then rename).</summary>
internal static class PrivateFile
{
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, ReadOnlySpan<byte> data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(data);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    public static void WriteText(string path, string text) => Write(path, Utf8.GetBytes(text));

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Reads text as Python's read_text(encoding="utf-8-sig") with universal newlines.</summary>
    public static string ReadConfigText(string path)
    {
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path));
        if (text.StartsWith('﻿'))
        {
            text = text[1..];
        }
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Python str.splitlines() for the line breaks found in config files.</summary>
    public static IEnumerable<string> Lines(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--;
        }
        return lines.Take(count);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Case-insensitive Windows path containment, including equality.</summary>
    public static bool IsWithin(string path, string parent)
    {
        var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return child.Equals(root, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
