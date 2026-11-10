namespace MediaTagger.API.Services;

/// <summary>How a stored path moves when a file or folder is renamed.</summary>
public static class StoredPath
{
    /// <summary>Linux paths differ by case; Windows paths do not.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static bool Same(string path, string other) =>
        string.Equals(path, other, Comparison);

    public static bool IsUnder(string path, string root)
    {
        if (Same(path, root)) return true;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    public static bool Covers(string stored, string oldPath)
    {
        if (stored.Equals(oldPath, StringComparison.Ordinal)) return true;
        if (stored.Length <= oldPath.Length) return false;
        if (!stored.StartsWith(oldPath, StringComparison.Ordinal)) return false;
        return stored[oldPath.Length] is '\\' or '/';
    }

    public static string Map(string stored, string oldPath, string newPath)
        => newPath + stored[oldPath.Length..];

    /// <summary>Full path, either separator, either case. Null when <paramref name="path"/> is outside the rename.</summary>
    public static string? MapFull(string path, string oldFull, string newFull)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return null; }
        oldFull = Path.TrimEndingDirectorySeparator(oldFull);
        newFull = Path.TrimEndingDirectorySeparator(newFull);
        if (Same(full, oldFull)) return newFull;
        if (full.Length <= oldFull.Length) return null;
        if (!full.StartsWith(oldFull, Comparison)) return null;
        if (full[oldFull.Length] is not ('\\' or '/')) return null;
        return newFull + full[oldFull.Length..];
    }
}
