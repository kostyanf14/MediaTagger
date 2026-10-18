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
