using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

public class FileSystemService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".heic", ".heif" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm", ".mts", ".m2ts" };

    private readonly TagsService _tagsService;
    private readonly ExifService _exifService;

    public FileSystemService(TagsService tagsService, ExifService exifService)
    {
        _tagsService = tagsService;
        _exifService = exifService;
    }

    public bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    public string GetMediaType(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext) ? "video" : "image";
    }

    public List<MediaFile> GetMediaFiles(string folderPath, bool recursive = false)
    {
        if (!Directory.Exists(folderPath))
            return new List<MediaFile>();

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(folderPath, "*", searchOption)
            .Where(IsMediaFile)
            .OrderBy(f => f)
            .ToList();

        var tagsFile = _tagsService.ReadTagsFile(folderPath);
        var result = new List<MediaFile>();

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            var info = new FileInfo(filePath);
            tagsFile.Files.TryGetValue(fileName, out var tags);
            tags ??= new FileTags();

            var exif = _exifService.ReadExif(filePath);

            // Auto-populate coordinates from EXIF if not set manually
            if (tags.Coordinates == null && exif?.GpsCoordinates != null)
                tags.Coordinates = exif.GpsCoordinates;

            result.Add(new MediaFile
            {
                FileName = fileName,
                FilePath = filePath,
                RelativePath = Path.GetRelativePath(folderPath, filePath),
                Type = GetMediaType(filePath),
                Size = info.Length,
                Modified = info.LastWriteTime,
                Tags = tags,
                Exif = exif
            });
        }

        return result;
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
