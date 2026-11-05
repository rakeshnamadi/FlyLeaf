namespace Flyleaf.Core;

/// <summary>Produces the canonical form of a path used as the lookup key everywhere in Flyleaf.</summary>
public static class PathNormalizer
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>Converts a path to an absolute, lowercase, backslash-separated key with no trailing separator.</summary>
    /// <param name="path">The path to normalise.</param>
    /// <returns>The normalised key. Drive roots keep their backslash, as in <c>c:\</c>.</returns>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var unprefixed = path.Replace('/', '\\');
        if (unprefixed.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            unprefixed = @"\\" + unprefixed[ExtendedUncPrefix.Length..];
        }
        else if (unprefixed.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            unprefixed = unprefixed[ExtendedPrefix.Length..];
        }

        var full = Path.GetFullPath(unprefixed);
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length)
        {
            full = full.TrimEnd('\\');
        }

        return full.ToLowerInvariant();
    }
}
