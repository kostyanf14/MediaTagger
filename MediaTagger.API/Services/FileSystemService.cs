using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

public class FileSystemService
{
    private readonly LibraryIndex _index;

    public FileSystemService(LibraryIndex index)
    {
        _index = index;
    }

    public bool IsMediaFile(string path) => LibraryIndex.IsMediaFile(path);

    public string GetMediaType(string path) => LibraryIndex.GetMediaType(path);

    public List<MediaFile> GetMediaFiles(string folderPath, bool recursive = false)
    {
        if (!Directory.Exists(folderPath))
            return new List<MediaFile>();

        return _index.Query(folderPath, recursive);
    }

    /// <summary>Call after changing a folder's contents directly, e.g. deleting a file.</summary>
    public void InvalidateFolder(string folderPath) => _index.Invalidate(folderPath);

    public AllTagsResponse GetAllTags(string rootPath)
    {
        var people = new HashSet<string>();
        var groups = new HashSet<string>();
        var albums = new HashSet<string>();
        // Place is a single value per file rather than a list, but the suggestions
        // it feeds work the same way.
        var places = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in GetMediaFiles(rootPath, recursive: true))
        {
            var tags = file.Tags;
            tags.People.ForEach(p => people.Add(p));
            tags.Groups.ForEach(g => groups.Add(g));
            tags.Albums.ForEach(a => albums.Add(a));
            if (!string.IsNullOrWhiteSpace(tags.Place)) places.Add(tags.Place.Trim());
        }

        return new AllTagsResponse
        {
            People = people.OrderBy(x => x).ToList(),
            Groups = groups.OrderBy(x => x).ToList(),
            Albums = albums.OrderBy(x => x).ToList(),
            Places = places.OrderBy(x => x).ToList()
        };
    }

    public LibraryStats GetLibraryStats(string rootPath)
    {
        var albums  = new Dictionary<string, (int Count, string? Cover)>();
        var people  = new Dictionary<string, (int Count, string? Cover)>();
        var places  = new Dictionary<string, (int Count, string? Cover)>();
        var groups  = new Dictionary<string, (int Count, string? Cover)>();

        foreach (var file in GetMediaFiles(rootPath, recursive: true))
        {
            var tags = file.Tags;

            void Add(Dictionary<string, (int, string?)> dict, string key)
            {
                if (!dict.TryGetValue(key, out var val)) val = (0, null);
                dict[key] = (val.Item1 + 1, val.Item2 ?? file.FilePath);
            }

            tags.Albums.ForEach(a => Add(albums, a));
            tags.People.ForEach(p => Add(people, p));
            tags.Groups.ForEach(g => Add(groups, g));
            if (!string.IsNullOrWhiteSpace(tags.Place)) Add(places, tags.Place);
        }

        return new LibraryStats
        {
            Albums = albums.Select(kv => new AlbumStat  { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderBy(x => x.Name).ToList(),
            People = people.Select(kv => new PersonStat { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderByDescending(x => x.Count).ToList(),
            Places = places.Select(kv => new PlaceStat  { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderByDescending(x => x.Count).ToList(),
            Groups = groups.Select(kv => new GroupStat  { Name = kv.Key, Count = kv.Value.Count, CoverPath = kv.Value.Cover }).OrderBy(x => x.Name).ToList(),
        };
    }

    public FolderNode GetFolderTree(string rootPath, int depth = 3)
    {
        var node = new FolderNode
        {
            Name = Path.GetFileName(rootPath) ?? rootPath,
            Path = rootPath
        };

        if (depth <= 0 || !Directory.Exists(rootPath))
            return node;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(rootPath)
                         .Where(d => !Path.GetFileName(d).StartsWith('.')))
            {
                node.Children.Add(GetFolderTree(dir, depth - 1));
            }
        }
        catch (UnauthorizedAccessException) { }

        return node;
    }

    public List<MediaFile> Search(SearchRequest request)
    {
        var allFiles = GetMediaFiles(request.RootPath, recursive: true);

        return allFiles.Where(f =>
        {
            var t = f.Tags;

            if (!string.IsNullOrWhiteSpace(request.Query))
            {
                var q = request.Query.ToLower();
                var match = t.People.Any(p => p.ToLower().Contains(q))
                    || t.Place.ToLower().Contains(q)
                    || t.Groups.Any(g => g.ToLower().Contains(q))
                    || t.Albums.Any(a => a.ToLower().Contains(q))
                    || f.FileName.ToLower().Contains(q);
                if (!match) return false;
            }

            if (request.People?.Any() == true)
                if (!request.People.All(p => t.People.Contains(p))) return false;

            if (!string.IsNullOrWhiteSpace(request.Place))
                if (!t.Place.Contains(request.Place, StringComparison.OrdinalIgnoreCase)) return false;

            if (request.Albums?.Any() == true)
                if (!request.Albums.Any(a => t.Albums.Contains(a))) return false;

            if (request.Groups?.Any() == true)
                if (!request.Groups.Any(g => t.Groups.Contains(g))) return false;

            return true;
        }).ToList();
    }
}
