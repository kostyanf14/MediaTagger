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
    }

    public FileTags GetFileTags(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        var tagsFile = ReadTagsFile(folder);
        return tagsFile.Files.TryGetValue(fileName, out var tags) ? tags : new FileTags();
    }

    public void UpdateFileTags(string filePath, FileTags tags)
    {
        var folder = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);
        var tagsFile = ReadTagsFile(folder);
        tagsFile.Files[fileName] = tags;
        WriteTagsFile(folder, tagsFile);
    }

    public AllTagsResponse GetAllTags(string rootPath)
    {
        var people = new HashSet<string>();
        var groups = new HashSet<string>();
        var albums = new HashSet<string>();

        foreach (var dir in GetAllDirectories(rootPath))
        {
            var tagsFile = ReadTagsFile(dir);
            foreach (var tags in tagsFile.Files.Values)
            {
                tags.People.ForEach(p => people.Add(p));
                tags.Groups.ForEach(g => groups.Add(g));
                tags.Albums.ForEach(a => albums.Add(a));
            }
        }

        return new AllTagsResponse
        {
            People = people.OrderBy(x => x).ToList(),
            Groups = groups.OrderBy(x => x).ToList(),
            Albums = albums.OrderBy(x => x).ToList()
        };
    }

    public LibraryStats GetLibraryStats(string rootPath)
    {
        var albums  = new Dictionary<string, (int Count, string? Cover)>();
        var people  = new Dictionary<string, (int Count, string? Cover)>();
        var places  = new Dictionary<string, (int Count, string? Cover)>();
        var groups  = new Dictionary<string, (int Count, string? Cover)>();

        foreach (var dir in GetAllDirectories(rootPath))
        {
            var tagsFile = ReadTagsFile(dir);
            foreach (var (fileName, tags) in tagsFile.Files)
            {
                var fullPath = Path.Combine(dir, fileName);
                if (!File.Exists(fullPath)) continue;

                void Add(Dictionary<string, (int, string?)> dict, string key)
                {
                    if (!dict.TryGetValue(key, out var val)) val = (0, null);
                    dict[key] = (val.Item1 + 1, val.Item2 ?? fullPath);
                }

                tags.Albums.ForEach(a => Add(albums, a));
                tags.People.ForEach(p => Add(people, p));
                tags.Groups.ForEach(g => Add(groups, g));
                if (!string.IsNullOrWhiteSpace(tags.Place)) Add(places, tags.Place);
            }
        }

        return new LibraryStats
        {
            Albums = albums.Select(kv => new AlbumStat  { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderBy(x => x.Name).ToList(),
            People = people.Select(kv => new PersonStat { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderByDescending(x => x.Count).ToList(),
            Places = places.Select(kv => new PlaceStat  { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderByDescending(x => x.Count).ToList(),
            Groups = groups.Select(kv => new GroupStat  { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderBy(x => x.Name).ToList(),
        };
    }

    private IEnumerable<string> GetAllDirectories(string root)
    {
        yield return root;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            yield return dir;
    }
}
