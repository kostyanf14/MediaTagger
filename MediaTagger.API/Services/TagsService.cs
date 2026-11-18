using System.Text.Json;
using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

/// <summary>
/// A folder's tags file exists but cannot be read. Writing over it would throw away
/// every tag in the folder, so edits are refused until it is fixed or restored from
/// the <c>.bak</c> copy.
/// </summary>
public sealed class TagsFileUnreadableException : IOException
{
    public string Path { get; }

    public TagsFileUnreadableException(string path, Exception? inner = null)
        : base($"The tags file {path} cannot be read", inner)
    {
        Path = path;
    }
}

public class TagsService
{
    private const string TagsFileName = "_tags.json";
    /// <summary>How long the tags of a deleted file are kept, in case it comes back from the recycle bin.</summary>
    private static readonly TimeSpan RemovedKeepFor = TimeSpan.FromDays(90);

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Every edit is read-modify-write on a whole folder's file. Two saves landing
    // together would otherwise each write back their own copy and one would vanish.
    private readonly object _writeGate = new();

    /// <summary>Raised with the folder path whenever its tags file is rewritten.</summary>
    public event Action<string>? FolderChanged;

    /// <summary>Raised with the folder and the parsed file after every write, so listeners can tell what changed.</summary>
    public event Action<string, TagsFile>? FolderWritten;

    /// <summary>
    /// The folder's tags for display. A missing or unreadable file reads as empty — which
    /// is fine for showing a folder, and exactly why edits must not start from it.
    /// </summary>
    public TagsFile ReadTagsFile(string folderPath) =>
        TryReadTagsFile(folderPath, out var file) ? file : new TagsFile();

    /// <summary>False when the file exists but cannot be parsed; a missing file is a valid, empty result.</summary>
    public bool TryReadTagsFile(string folderPath, out TagsFile file)
    {
        var tagsPath = Path.Combine(folderPath, TagsFileName);
        file = new TagsFile();
        if (!File.Exists(tagsPath)) return true;
        try
        {
            var json = File.ReadAllText(tagsPath);
            var parsed = JsonSerializer.Deserialize<TagsFile>(json, _jsonOptions);
            if (parsed == null) return false;
            file = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The folder's tags as a starting point for an edit; throws when they cannot be trusted.</summary>
    public TagsFile ReadForWrite(string folderPath)
    {
        if (TryReadTagsFile(folderPath, out var file)) return file;
        throw new TagsFileUnreadableException(Path.Combine(folderPath, TagsFileName));
    }

    public void WriteTagsFile(string folderPath, TagsFile tagsFile)
    {
        PruneRemoved(tagsFile);
        var tagsPath = Path.Combine(folderPath, TagsFileName);
        var json = JsonSerializer.Serialize(tagsFile, _jsonOptions);
        AtomicFile.WriteAllText(tagsPath, json, keepBackup: true);
        FolderChanged?.Invoke(folderPath);
        FolderWritten?.Invoke(folderPath, tagsFile);
    }

    private static void PruneRemoved(TagsFile tagsFile)
    {
        if (tagsFile.Removed == null) return;
        var cutoff = DateTime.UtcNow - RemovedKeepFor;
        foreach (var key in tagsFile.Removed.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            tagsFile.Removed.Remove(key);
        if (tagsFile.Removed.Count == 0) tagsFile.Removed = null;
    }

    public FileTags GetFileTags(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        var tagsFile = ReadTagsFile(folder);
        return tagsFile.Files.TryGetValue(fileName, out var tags) ? tags : new FileTags();
    }

    /// <summary>
    /// Merges the given tags into every listed file, returning how many were touched.
    /// Files are grouped by folder so each _tags.json is read and written once.
    /// </summary>
    public int AddTagsToFiles(IEnumerable<string> filePaths, BulkTagsRequest request)
    {
        var updated = 0;

        foreach (var folder in filePaths.GroupBy(p => Path.GetDirectoryName(p) ?? ""))
        {
            lock (_writeGate)
            {
                var tagsFile = ReadForWrite(folder.Key);

                foreach (var filePath in folder)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!tagsFile.Files.TryGetValue(fileName, out var tags))
                        tagsFile.Files[fileName] = tags = TakeRemoved(tagsFile, fileName) ?? new FileTags();

                    tags.People = Merge(tags.People, request.People);
                    tags.Groups = Merge(tags.Groups, request.Groups);
                    tags.Albums = Merge(tags.Albums, request.Albums);
                    if (!string.IsNullOrWhiteSpace(request.Place))
                        tags.Place = request.Place.Trim();
                    if (!string.IsNullOrWhiteSpace(request.Photographer))
                        tags.Photographer = request.Photographer.Trim();

                    updated++;
                }

                WriteTagsFile(folder.Key, tagsFile);
            }
        }

        return updated;
    }

    /// <summary>Appends what is new, keeping existing order and ignoring case-only repeats.</summary>
    private static List<string> Merge(List<string> existing, IEnumerable<string> additions)
    {
        var result = new List<string>(existing);
        foreach (var addition in additions)
        {
            var value = addition.Trim();
            if (value.Length == 0) continue;
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
        }
        return result;
    }

    /// <summary>
    /// Rewrites people tags under <paramref name="roots"/> so every name in <paramref name="from"/>
    /// becomes <paramref name="to"/>. One file keeps a single copy of the new name.
    /// A folder whose tags file cannot be read is left alone; it is counted in <paramref name="skipped"/>.
    /// </summary>
    public int RenamePeople(IEnumerable<string> roots, IEnumerable<string> from, string to) =>
        RenamePeople(roots, from, to, out _);

    public int RenamePeople(IEnumerable<string> roots, IEnumerable<string> from, string to, out int skipped)
    {
        skipped = 0;
        var sources = new HashSet<string>(
            from.Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        var target = to.Trim();
        if (sources.Count == 0 || target.Length == 0) return 0;

        var folders = 0;
        foreach (var root in FileSystemService.Collapse(roots))
        {
            foreach (var folder in FoldersWithTags(root))
            {
                lock (_writeGate)
                {
                    if (!TryReadTagsFile(folder, out var tagsFile)) { skipped++; continue; }
                    var changed = false;
                    foreach (var tags in tagsFile.Files.Values)
                    {
                        // The photographer is a person too, just not in the frame.
                        if (tags.Photographer is { } photographer && sources.Contains(photographer.Trim())
                            && photographer != target)
                        {
                            tags.Photographer = target;
                            changed = true;
                        }
                        if (tags.People == null || ReplacePeople(tags.People, sources, target) is not { } next) continue;
                        tags.People = next;
                        changed = true;
                    }
                    if (!changed) continue;
                    WriteTagsFile(folder, tagsFile);
                    folders++;
                }
            }
        }
        return folders;
    }

    /// <summary>The rewritten list, or null when this file does not use any of the old names.</summary>
    internal static List<string>? ReplacePeople(List<string> people, HashSet<string> sources, string target)
    {
        var hit = false;
        foreach (var person in people)
            if (sources.Contains(person)) { hit = true; break; }
        if (!hit) return null;

        var result = new List<string>(people.Count);
        var placed = false;
        foreach (var person in people)
        {
            if (sources.Contains(person) || person.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                if (placed) continue;
                result.Add(target);
                placed = true;
            }
            else result.Add(person);
        }
        return result.SequenceEqual(people) ? null : result;
    }

    internal static IEnumerable<string> FoldersWithTags(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var entry in entries)
            {
                FileAttributes attr;
                try { attr = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

                if ((attr & FileAttributes.Directory) != 0)
                {
                    if ((attr & FileAttributes.ReparsePoint) == 0 && !LibraryIndex.IsSkippedFolder(Path.GetFileName(entry)))
                        pending.Push(entry);
                }
                else if (Path.GetFileName(entry).Equals(TagsFileName, StringComparison.OrdinalIgnoreCase))
                    yield return dir;
            }
        }
    }

    public void UpdateFileTags(string filePath, FileTags tags)
    {
        var folder = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        lock (_writeGate)
        {
            var tagsFile = ReadForWrite(folder);
            tagsFile.Files[fileName] = tags;
            tagsFile.Removed?.Remove(fileName);
            WriteTagsFile(folder, tagsFile);
        }
    }

    /// <summary>Keeps a file's tags with it when the file is renamed. The key is the file name.</summary>
    public void MoveFileEntry(string oldPath, string newPath) =>
        MoveFileEntries(new[] { (oldPath, newPath) });

    /// <summary>
    /// Moves many tag entries, reading each folder's file once. Every folder is read
    /// before anything is written, and the folders receiving entries are written before
    /// the ones losing them — a failure in between leaves an entry in two places rather than in none.
    /// </summary>
    public void MoveFileEntries(IReadOnlyList<(string OldPath, string NewPath)> moves)
    {
        if (moves.Count == 0) return;
        lock (_writeGate)
        {
            var loaded = new Dictionary<string, TagsFile>(StoredPath.Comparer);
            TagsFile Load(string dir)
            {
                if (!loaded.TryGetValue(dir, out var file)) loaded[dir] = file = ReadForWrite(dir);
                return file;
            }

            var sources = new HashSet<string>(StoredPath.Comparer);
            var destinations = new HashSet<string>(StoredPath.Comparer);
            var pending = new List<(string Dir, string Name, FileTags Tags)>();

            foreach (var group in moves.GroupBy(m => Path.GetDirectoryName(m.OldPath) ?? "", StoredPath.Comparer))
            {
                var source = Load(group.Key);
                foreach (var (oldPath, newPath) in group)
                {
                    var oldName = Path.GetFileName(oldPath);
                    var key = source.Files.Keys.FirstOrDefault(k => k.Equals(oldName, StoredPath.Comparison));
                    if (key == null) continue;
                    var tags = source.Files[key];
                    source.Files.Remove(key);
                    sources.Add(group.Key);
                    var newDir = Path.GetDirectoryName(newPath) ?? "";
                    var newName = Path.GetFileName(newPath);
                    if (StoredPath.Same(group.Key, newDir))
                    {
                        source.Files[newName] = tags;
                        continue;
                    }
                    pending.Add((newDir, newName, tags));
                }
            }

            foreach (var (dir, name, tags) in pending)
            {
                var dest = Load(dir);
                dest.Files[name] = tags;
                dest.Removed?.Remove(name);
                destinations.Add(dir);
            }

            // Folders that only receive come first; the rest (which also lose entries) follow.
            foreach (var dir in destinations.Where(d => !sources.Contains(d)))
                WriteTagsFile(dir, loaded[dir]);
            foreach (var dir in sources)
                WriteTagsFile(dir, loaded[dir]);
        }
    }

    /// <summary>
    /// Copies tags for files that moved into <paramref name="destDir"/>.
    /// A name already tagged there is left alone: that file was not overwritten.
    /// </summary>
    public void MergeFolderTags(string sourceDir, string destDir)
    {
        lock (_writeGate)
        {
            // An unreadable source has nothing to give; the caller keeps the file.
            var source = ReadTagsFile(sourceDir);
            if (source.Files.Count == 0) return;
            var dest = ReadForWrite(destDir);
            var changed = false;
            foreach (var (name, tags) in source.Files)
            {
                if (dest.Files.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                dest.Files[name] = tags;
                dest.Removed?.Remove(name);
                changed = true;
            }
            if (changed) WriteTagsFile(destDir, dest);
        }
    }

    /// <summary>
    /// Takes a deleted file's entry out of the active tags, keeping it for a while so
    /// the file can come back from the recycle bin with its tags.
    /// </summary>
    public void RemoveFileTags(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        lock (_writeGate)
        {
            var tagsFile = ReadForWrite(folder);
            // Nothing recorded for it — do not create a tags file just to say so.
            if (!tagsFile.Files.Remove(fileName, out var tags)) return;
            (tagsFile.Removed ??= new())[fileName] = new RemovedTags { Tags = tags, At = DateTime.UtcNow };
            WriteTagsFile(folder, tagsFile);
        }
    }

    /// <summary>
    /// Puts back the tags of files that were deleted and have reappeared under the same
    /// name. Called by the library listing when it meets a file with no entry of its own.
    /// </summary>
    public void RestoreRemoved(string folder, IEnumerable<string> names)
    {
        lock (_writeGate)
        {
            if (!TryReadTagsFile(folder, out var tagsFile) || tagsFile.Removed == null) return;
            var changed = false;
            foreach (var name in names)
            {
                if (tagsFile.Files.ContainsKey(name)) continue;
                if (TakeRemoved(tagsFile, name) is not { } tags) continue;
                tagsFile.Files[name] = tags;
                changed = true;
            }
            if (changed) WriteTagsFile(folder, tagsFile);
        }
    }

    /// <summary>The kept tags of a deleted file with this name, removing them from the keep list.</summary>
    internal static FileTags? TakeRemoved(TagsFile tagsFile, string fileName)
    {
        if (tagsFile.Removed == null) return null;
        var key = tagsFile.Removed.Keys.FirstOrDefault(k => k.Equals(fileName, StoredPath.Comparison));
        if (key == null) return null;
        var tags = tagsFile.Removed[key].Tags;
        tagsFile.Removed.Remove(key);
        if (tagsFile.Removed.Count == 0) tagsFile.Removed = null;
        return tags;
    }

    /// <summary>The kept tags of a deleted file, without taking them.</summary>
    internal static FileTags? PeekRemoved(TagsFile tagsFile, string fileName)
    {
        if (tagsFile.Removed == null) return null;
        foreach (var (key, value) in tagsFile.Removed)
            if (key.Equals(fileName, StoredPath.Comparison)) return value.Tags;
        return null;
    }
}
