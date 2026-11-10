using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

public sealed class RenameOutcome
{
    public string? Path { get; init; }
    public bool Merged { get; init; }
    public AppSettings? Settings { get; init; }
    /// <summary>merge, exists, invalid, or missing. Null when the rename happened.</summary>
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? Destination { get; init; }
}

/// <summary>
/// Renames a photo or a folder and moves every stored reference onto the new path:
/// tag files, the face database, the metadata cache, and the remembered folders.
/// </summary>
public class MediaRename
{
    private const string TagsFileName = "_tags.json";

    private readonly TagsService _tags;
    private readonly FaceStore _faces;
    private readonly ExifCache _exif;
    private readonly SettingsService _settings;
    private readonly FileSystemService _fs;

    public MediaRename(TagsService tags, FaceStore faces, ExifCache exif, SettingsService settings, FileSystemService fs)
    {
        _tags = tags;
        _faces = faces;
        _exif = exif;
        _settings = settings;
        _fs = fs;
    }

    public RenameOutcome Rename(string rawPath, string newName, bool merge)
    {
        newName = newName.Trim();
        if (BadName(newName)) return new RenameOutcome { Code = "invalid" };

        string source;
        try { source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath)); }
        catch { return new RenameOutcome { Code = "invalid" }; }

        var isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source)) return new RenameOutcome { Code = "missing" };

        var parent = Path.GetDirectoryName(source);
        if (string.IsNullOrEmpty(parent)) return new RenameOutcome { Code = "invalid" };

        var currentName = Path.GetFileName(source);
        if (currentName.Equals(newName, StringComparison.Ordinal))
            return new RenameOutcome { Path = source, Settings = _settings.Get() };

        return Relocate(source, Path.Combine(parent, newName), merge, isDir);
    }

    /// <summary>Moves a file or folder into an existing folder, keeping every stored path.</summary>
    public RenameOutcome MoveInto(string rawPath, string rawDestination, bool merge)
    {
        string source, destination;
        try
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath));
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawDestination));
        }
        catch { return new RenameOutcome { Code = "invalid" }; }

        if (!Directory.Exists(destination)) return new RenameOutcome { Code = "missing" };
        var isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source)) return new RenameOutcome { Code = "missing" };

        var parent = Path.GetDirectoryName(source);
        if (string.IsNullOrEmpty(parent)) return new RenameOutcome { Code = "invalid" };
        if (parent.Equals(destination, StringComparison.OrdinalIgnoreCase))
            return new RenameOutcome { Path = source, Settings = _settings.Get() };

        // A folder cannot be moved into itself.
        if (isDir && IsUnder(destination, source)) return new RenameOutcome { Code = "invalid" };

        return Relocate(source, Path.Combine(destination, Path.GetFileName(source)), merge, isDir);
    }

    private RenameOutcome Relocate(string source, string dest, bool merge, bool isDir)
    {
        var parent = Path.GetDirectoryName(source)!;
        var currentName = Path.GetFileName(source);
        var newName = Path.GetFileName(dest);
        var caseOnly = currentName.Equals(newName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dest)!.Equals(parent, StringComparison.OrdinalIgnoreCase);
        var merged = false;

        // shortcut: a case-only change on a case-sensitive disk is treated as the same
        // folder. Two folders that differ only by case cannot both keep their names.
        if (caseOnly)
        {
            MoveWithCaseChange(source, dest, isDir);
        }
        else if (isDir && Directory.Exists(dest))
        {
            if (!merge) return new RenameOutcome { Code = "merge", Destination = dest };
            var clash = FirstClash(source, dest);
            if (clash != null) return new RenameOutcome { Code = "exists", Name = clash };
            MergeTree(source, dest);
            merged = true;
        }
        else if (File.Exists(dest) || Directory.Exists(dest))
        {
            return new RenameOutcome { Code = "exists", Name = newName };
        }
        else if (isDir)
        {
            Directory.Move(source, dest);
        }
        else
        {
            File.Move(source, dest);
        }

        if (!isDir) _tags.MoveFileEntry(source, dest);

        // Thumbnails are named from a hash of the path, so the old ones are left behind
        // and rebuilt the next time the photo is shown.
        _faces.Retarget(source, dest);
        _exif.Retarget(source, dest);
        var settings = _settings.RetargetPaths(source, dest);

        _fs.InvalidateFolder(parent);
        var destParent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destParent)) _fs.InvalidateFolder(destParent);
        _fs.Forget(source);
        if (isDir) _fs.InvalidateFolder(dest);

        return new RenameOutcome { Path = dest, Merged = merged, Settings = settings };
    }

    private void MergeTree(string source, string dest)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
        {
            var name = Path.GetFileName(entry);
            if (name.Equals(TagsFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dest, name);
            if (IsDirectory(entry))
            {
                if (!Directory.Exists(target)) Directory.Move(entry, target);
                else MergeTree(entry, target);
            }
            else File.Move(entry, target);
        }

        _tags.MergeFolderTags(source, dest);
        var tags = Path.Combine(source, TagsFileName);
        if (File.Exists(tags)) File.Delete(tags);
        Directory.Delete(source);
    }

    /// <summary>A file or folder that would be overwritten, relative to the folder being merged. Null when it is safe.</summary>
    private static string? FirstClash(string source, string dest)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Path.GetFileName(source); }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.Equals(TagsFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dest, name);
            if (IsDirectory(entry))
            {
                if (File.Exists(target)) return name;
                if (!Directory.Exists(target)) continue;
                var nested = FirstClash(entry, target);
                if (nested != null) return name + "/" + nested;
            }
            else if (File.Exists(target) || Directory.Exists(target))
            {
                return name;
            }
        }
        return null;
    }

    private static void MoveWithCaseChange(string source, string dest, bool directory)
    {
        var parent = Path.GetDirectoryName(source)!;
        var temp = Path.Combine(parent, ".mt-rename-" + Guid.NewGuid().ToString("N"));
        Move(source, temp, directory);
        try { Move(temp, dest, directory); }
        catch
        {
            try { Move(temp, source, directory); } catch { /* the temp name is still on disk */ }
            throw;
        }
    }

    private static void Move(string source, string dest, bool directory)
    {
        if (directory) Directory.Move(source, dest);
        else File.Move(source, dest);
    }

    private static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectory(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool BadName(string name)
    {
        if (name.Length == 0 || name is "." or "..") return true;
        if (name.Equals(TagsFileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
        if (name.EndsWith('.') || name.EndsWith(' ')) return true;
        return false;
    }
}
