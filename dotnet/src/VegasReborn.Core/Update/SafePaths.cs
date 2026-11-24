using System.Security.Cryptography;

namespace VegasReborn.Core.Update;

/// <summary>Path containment and hashing used by every update step.</summary>
public static class SafePaths
{
    /// <summary>
    /// Equivalent of Python's non-strict <c>Path.resolve()</c> on Windows: the longest existing
    /// prefix is resolved through symlinks, junctions and 8.3 names; the remainder is appended.
    /// </summary>
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var remainder = new Stack<string>();
        var current = full;
        while (true)
        {
            var final = NativeMethods.FinalPath(current);
            if (final is not null)
            {
                return remainder.Aggregate(final, Path.Combine);
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return full;
            }
            remainder.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> under <paramref name="root"/>; it must stay strictly inside it.
    /// </summary>
    /// <exception cref="UpdateException">The path escaped the folder.</exception>
    public static string Beneath(string root, string relative)
    {
        var resolvedRoot = Trim(Resolve(root));
        var target = Trim(Resolve(Path.Combine(root, relative)));
        var prefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar) ? resolvedRoot : resolvedRoot + Path.DirectorySeparatorChar;
        if (target.Length <= prefix.Length || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("An update path escaped the launcher folder");
        }
        return target;
    }

    /// <summary>Case-insensitive comparison of two full Windows paths.</summary>
    public static bool Same(string left, string right) =>
        string.Equals(Trim(Path.GetFullPath(left)), Trim(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    public static string Digest(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        return Convert.ToHexStringLower(SHA256.HashData(source));
    }

    private static string Trim(string path)
    {
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        return path.Length > rootLength ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }
}
