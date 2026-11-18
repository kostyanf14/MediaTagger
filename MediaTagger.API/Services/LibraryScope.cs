namespace MediaTagger.API.Services;

/// <summary>
/// The folders the API will touch: the ones in the user's library. Every endpoint that
/// takes a path is checked against this, so a request cannot read, move or delete
/// anything outside the library however it spells the path.
/// </summary>
public class LibraryScope
{
    private readonly SettingsService _settings;

    public LibraryScope(SettingsService settings) => _settings = settings;

    /// <summary>The library folders, including the default one.</summary>
    public IReadOnlyList<string> Roots()
    {
        var settings = _settings.Get();
        return FileSystemService.Collapse(settings.LibraryFolders.Append(settings.DefaultFolder));
    }

    public bool Allows(string? path) => Allows(path, Roots());

    /// <summary>True when <paramref name="path"/>, resolved, is a library folder or inside one.</summary>
    public static bool Allows(string? path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Device paths (\\?\, \\.\) skip the normalisation the checks below rely on.
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;

        foreach (var root in roots)
            if (StoredPath.IsUnder(full, Path.TrimEndingDirectorySeparator(root))) return true;
        return false;
    }
}
