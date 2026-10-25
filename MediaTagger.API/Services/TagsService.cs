using System.Text.Json;
using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

public class TagsService
{
    private const string TagsFileName = "_tags.json";
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

    public TagsFile ReadTagsFile(string folderPath)
    {
        var tagsPath = Path.Combine(folderPath, TagsFileName);
        if (!File.Exists(tagsPath)) return new TagsFile();
        try
        {
            var json = File.ReadAllText(tagsPath);
            return JsonSerializer.Deserialize<TagsFile>(json, _jsonOptions) ?? new TagsFile();
        }
        catch { return new TagsFile(); }
    }

    public void WriteTagsFile(string folderPath, TagsFile tagsFile)
    {
        var tagsPath = Path.Combine(folderPath, TagsFileName);
        var json = JsonSerializer.Serialize(tagsFile, _jsonOptions);
        File.WriteAllText(tagsPath, json);
        FolderChanged?.Invoke(folderPath);
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
                var tagsFile = ReadTagsFile(folder.Key);

                foreach (var filePath in folder)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!tagsFile.Files.TryGetValue(fileName, out var tags))
                        tagsFile.Files[fileName] = tags = new FileTags();

                    tags.People = Merge(tags.People, request.People);
                    tags.Groups = Merge(tags.Groups, request.Groups);
                    tags.Albums = Merge(tags.Albums, request.Albums);
                    if (!string.IsNullOrWhiteSpace(request.Place))
                        tags.Place = request.Place.Trim();

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
    /// </summary>
    public int RenamePeople(IEnumerable<string> roots, IEnumerable<string> from, string to)
    {
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
                    var tagsFile = ReadTagsFile(folder);
                    var changed = false;
                    foreach (var tags in tagsFile.Files.Values)
                    {
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

    private static IEnumerable<string> FoldersWithTags(string root)
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
                    if ((attr & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
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
            var tagsFile = ReadTagsFile(folder);
            tagsFile.Files[fileName] = tags;
            WriteTagsFile(folder, tagsFile);
        }
    }

    /// <summary>Drops a file's entry, for when the file itself is being deleted.</summary>
    public void RemoveFileTags(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        lock (_writeGate)
        {
            var tagsFile = ReadTagsFile(folder);
            // Nothing recorded for it — do not create a tags file just to say so.
            if (!tagsFile.Files.Remove(fileName)) return;
            WriteTagsFile(folder, tagsFile);
        }
    }
}
