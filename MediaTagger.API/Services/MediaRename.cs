using System.Globalization;
using System.Text.RegularExpressions;
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

public sealed class SortOutcome
{
    public int Moved { get; init; }
    public int Skipped { get; init; }
    /// <summary>How many of <see cref="Moved"/> had no date and went into 0000.</summary>
    public int Undated { get; init; }
    public AppSettings? Settings { get; init; }
    public string? Code { get; init; }
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
    private readonly ExifService _exifReader;
    private readonly SettingsService _settings;
    private readonly FileSystemService _fs;

    public MediaRename(TagsService tags, FaceStore faces, ExifCache exif, ExifService exifReader,
        SettingsService settings, FileSystemService fs)
    {
        _tags = tags;
        _faces = faces;
        _exif = exif;
        _exifReader = exifReader;
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
        if (StoredPath.Same(parent, destination))
            return new RenameOutcome { Path = source, Settings = _settings.Get() };

        // A folder cannot be moved into itself.
        if (isDir && StoredPath.IsUnder(destination, source)) return new RenameOutcome { Code = "invalid" };

        return Relocate(source, Path.Combine(destination, Path.GetFileName(source)), merge, isDir);
    }

    /// <summary>
    /// Puts every photo and video under <paramref name="rawFolder"/> into year/month folders.
    /// The date is the saved capture date, then the file's metadata, then a name like
    /// IMG_20191006_142317, and otherwise the file goes into 0000. A name already in the
    /// destination stays put.
    /// </summary>
    public SortOutcome SortByDate(string rawFolder)
    {
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawFolder)); }
        catch { return new SortOutcome { Code = "invalid" }; }
        if (!Directory.Exists(root)) return new SortOutcome { Code = "missing" };

        var tagsByFolder = new Dictionary<string, TagsFile>(StoredPath.Comparer);
        var planned = new List<(string Source, string Dest, bool Undated)>();
        var reserved = new HashSet<string>(StoredPath.Comparer);
        var skipped = 0;
        foreach (var file in MediaUnder(root))
        {
            var folder = Path.GetDirectoryName(file) ?? "";
            if (!tagsByFolder.TryGetValue(folder, out var tags))
                tagsByFolder[folder] = tags = _tags.ReadTagsFile(folder);
            var date = CaptureDate(file, tags);
            var destDir = date == null
                ? Path.Combine(root, "0000")
                : Path.Combine(root, date.Value.Year.ToString("D4"), date.Value.Month.ToString("D2"));
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            if (StoredPath.Same(Path.GetFullPath(file), Path.GetFullPath(dest)))
                continue;
            if (File.Exists(dest) || !reserved.Add(dest)) { skipped++; continue; }
            planned.Add((file, dest, date == null));
        }

        var done = new List<(string Source, string Dest, bool Undated)>();
        Exception? failure = null;
        foreach (var item in planned)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Dest)!);
                File.Move(item.Source, item.Dest);
                done.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                break;
            }
        }
        if (done.Count > 0)
        {
            var pairs = done.Select(m => (m.Source, m.Dest)).ToList();
            _tags.MoveFileEntries(pairs);
            _faces.RetargetAll(pairs);
            _exif.RetargetAll(pairs);
            foreach (var dir in done.SelectMany(m => new[]
                {
                    Path.GetDirectoryName(m.Source) ?? "",
                    Path.GetDirectoryName(m.Dest) ?? ""
                }).Distinct(StoredPath.Comparer))
                if (dir.Length > 0) _fs.InvalidateFolder(dir);
        }
        if (failure != null) throw failure;

        return new SortOutcome
        {
            Moved = done.Count,
            Skipped = skipped,
            Undated = done.Count(m => m.Undated),
            Settings = _settings.Get()
        };
    }

    /// <summary>The first YYYYMMDD in the name that is a real date, as in IMG_20191006_142317.</summary>
    public static DateTime? DateFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (Match match in FileDate.Matches(name))
        {
            if (DateTime.TryParseExact(match.Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                return date;
        }
        return null;
    }

    private static readonly Regex FileDate = new(@"(?<!\d)(?:19|20)\d{6}(?!\d)", RegexOptions.Compiled);

    private DateTime? CaptureDate(string file, TagsFile tags)
    {
        var name = Path.GetFileName(file);
        var key = tags.Files.Keys.FirstOrDefault(k => k.Equals(name, StoredPath.Comparison));
        if (key != null && tags.Files[key].DateTaken != null) return tags.Files[key].DateTaken;
        var info = new FileInfo(file);
        return _exif.GetOrRead(file, info, _exifReader.ReadExif)?.DateTaken ?? DateFromFileName(info.Name);
    }

    private static List<string> MediaUnder(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                FileAttributes attr;
                try { attr = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if ((attr & FileAttributes.Directory) != 0)
                {
                    if ((attr & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                }
                else if (LibraryIndex.IsMediaFile(entry)) files.Add(entry);
            }
        }
        return files;
    }

    private RenameOutcome Relocate(string source, string dest, bool merge, bool isDir)
    {
        var parent = Path.GetDirectoryName(source)!;
        var currentName = Path.GetFileName(source);
        var newName = Path.GetFileName(dest);
        var caseOnly = currentName.Equals(newName, StringComparison.OrdinalIgnoreCase)
            && StoredPath.Same(Path.GetDirectoryName(dest)!, parent);
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
