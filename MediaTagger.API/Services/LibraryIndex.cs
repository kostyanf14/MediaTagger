using System.Collections.Concurrent;
using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

/// <summary>
/// Remembers what each folder contains, so browsing, searching and the map do not
/// walk the disk on every request. A folder is listed once and then served from
/// memory until something in it changes — which a <see cref="FileSystemWatcher"/>
/// and the app's own tag writes report.
/// </summary>
public class LibraryIndex : IDisposable
{
    private const string TagsFileName = "_tags.json";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".heic", ".heif" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm", ".mts", ".m2ts" };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // Reading metadata is mostly waiting on the disk, so a first scan spreads across
    // a few threads — but not so many that a spinning drive spends its time seeking.
    private static readonly int ScanThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private readonly TagsService _tags;
    private readonly ExifService _exif;
    private readonly ExifCache _exifCache;
    private readonly ScanProgress _progress;
    private readonly ILogger<LibraryIndex> _logger;

    /// <summary>One folder's own files and the folders directly below it.</summary>
    private sealed record FolderSnapshot(MediaFile[] Files, List<string> SubFolders);

    private readonly ConcurrentDictionary<string, FolderSnapshot> _folders =
        new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // Bumped on every invalidation. A scan that started before a change must not
    // store what it saw, or the stale listing would outlive the change.
    private long _generation;

    private readonly object _watchGate = new();
    private readonly List<FileSystemWatcher> _watchers = new();

    public LibraryIndex(TagsService tags, ExifService exif, ExifCache exifCache, ScanProgress progress,
        ILogger<LibraryIndex> logger)
    {
        _tags = tags;
        _exif = exif;
        _exifCache = exifCache;
        _progress = progress;
        _logger = logger;

        // Tags are part of what a listing returns, so our own writes count as changes.
        _tags.FolderChanged += Invalidate;
    }

    public static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    public static string GetMediaType(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path)) ? "video" : "image";

    /// <summary>Media files in <paramref name="folderPath"/>, optionally with everything below it.</summary>
    public List<MediaFile> Query(string folderPath, bool recursive)
    {
        var root = Normalize(folderPath);

        // The watcher has to be listening before anything is listed, or a change
        // landing in between would be missed and the listing kept forever.
        var cacheable = EnsureWatched(root);
        var generation = Volatile.Read(ref _generation);

        var visited = new List<FolderVisit>();
        Visit(root, recursive, cacheable, visited);

        var pending = visited.Where(v => v.Snapshot == null).ToList();
        if (pending.Count > 0) Scan(root, pending);

        if (cacheable && pending.Count > 0) Remember(pending, generation);

        var prefixLength = Path.EndsInDirectorySeparator(root) ? root.Length : root.Length + 1;
        var result = new List<MediaFile>(visited.Sum(v => v.Snapshot!.Files.Length));

        foreach (var visit in visited)
        {
            foreach (var file in visit.Snapshot!.Files)
            {
                // Snapshots are shared between requests, and the relative path depends
                // on which folder was asked for — so each answer gets its own copies.
                result.Add(new MediaFile
                {
                    FileName = file.FileName,
                    FilePath = file.FilePath,
                    RelativePath = file.FilePath.Length > prefixLength ? file.FilePath[prefixLength..] : file.FileName,
                    Type = file.Type,
                    Size = file.Size,
                    Modified = file.Modified,
                    Tags = file.Tags,
                    Exif = file.Exif
                });
            }
        }

        return result;
    }

    /// <summary>Forgets one folder's listing; the next request reads it again.</summary>
    public void Invalidate(string folderPath)
    {
        Interlocked.Increment(ref _generation);
        _folders.TryRemove(Normalize(folderPath), out _);
    }

    private sealed class FolderVisit
    {
        public required string Folder { get; init; }
        public FolderSnapshot? Snapshot { get; set; }
        // Filled only for folders that still have to be read.
        public List<FileInfo> Files { get; } = new();
        public List<string> SubFolders { get; } = new();
    }

    private void Visit(string folder, bool recursive, bool cacheable, List<FolderVisit> visited)
    {
        var visit = new FolderVisit { Folder = folder };
        visited.Add(visit);

        List<string> subFolders;
        if (cacheable && _folders.TryGetValue(folder, out var known))
        {
            visit.Snapshot = known;
            subFolders = known.SubFolders;
        }
        else
        {
            List(visit);
            subFolders = visit.SubFolders;
        }

        if (!recursive) return;
        foreach (var subFolder in subFolders) Visit(subFolder, recursive, cacheable, visited);
    }

    private void List(FolderVisit visit)
    {
        try
        {
            // The directory listing already carries each file's size and timestamps,
            // so nothing here costs a separate trip to the disk per file.
            foreach (var entry in new DirectoryInfo(visit.Folder).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                {
                    // Following links is how a scan ends up walking in circles.
                    if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        visit.SubFolders.Add(directory.FullName);
                }
                else if (entry is FileInfo file && IsMediaFile(file.Name))
                {
                    visit.Files.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug("Could not list {Folder}: {Msg}", visit.Folder, ex.Message);
        }

        visit.SubFolders.Sort(StringComparer.OrdinalIgnoreCase);
        visit.Files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    private void Scan(string root, List<FolderVisit> pending)
    {
        var jobs = new List<(FolderVisit Visit, TagsFile Tags, MediaFile[] Built, int Index)>();
        foreach (var visit in pending)
        {
            // Every folder keeps its own _tags.json, so each file is looked up in the
            // folder it actually lives in — not the one being browsed.
            var tags = visit.Files.Count > 0 ? _tags.ReadTagsFile(visit.Folder) : new TagsFile();
            var built = new MediaFile[visit.Files.Count];
            for (var i = 0; i < built.Length; i++) jobs.Add((visit, tags, built, i));
            visit.Snapshot = new FolderSnapshot(built, visit.SubFolders);
        }

        if (jobs.Count == 0) return;

        using var scan = _progress.Begin(root, jobs.Count);
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = ScanThreads }, job =>
        {
            job.Built[job.Index] = Describe(job.Visit.Files[job.Index], job.Tags);
            scan.Advance();
        });
    }

    private MediaFile Describe(FileInfo info, TagsFile tagsFile)
    {
        tagsFile.Files.TryGetValue(info.Name, out var tags);
        tags ??= new FileTags();

        // Parsing metadata dominates a library scan, so it happens once per
        // version of a file and is remembered from then on.
        var exif = _exifCache.GetOrRead(info.FullName, info, _exif.ReadExif);

        // Auto-populate coordinates from EXIF if not set manually
        if (tags.Coordinates == null && exif?.GpsCoordinates != null)
            tags.Coordinates = exif.GpsCoordinates;

        // Same for capture date — this becomes the editable field the UI shows/sorts on.
        if (tags.DateTaken == null && exif?.DateTaken != null)
            tags.DateTaken = exif.DateTaken;

        return new MediaFile
        {
            FileName = info.Name,
            FilePath = info.FullName,
            Type = GetMediaType(info.Name),
            Size = info.Length,
            Modified = info.LastWriteTime,
            Tags = tags,
            Exif = exif
        };
    }

    private void Remember(List<FolderVisit> scanned, long generation)
    {
        if (generation != Volatile.Read(ref _generation)) return;

        foreach (var visit in scanned) _folders[visit.Folder] = visit.Snapshot!;

        // A change may have slipped in between the check and the writes above.
        if (generation != Volatile.Read(ref _generation))
            foreach (var visit in scanned) _folders.TryRemove(visit.Folder, out _);
    }

    /// <summary>
    /// Makes sure changes under <paramref name="root"/> are being reported. Returns
    /// false when they cannot be (some network shares), in which case nothing under
    /// it is remembered and every request reads the disk, as before.
    /// </summary>
    private bool EnsureWatched(string root)
    {
        lock (_watchGate)
        {
            if (_watchers.Any(w => IsUnder(root, w.Path))) return true;

            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Cannot watch {Root} for changes, it will be re-read on every request: {Msg}",
                    root, ex.Message);
                return false;
            }

            // A watcher on a parent covers the ones opened earlier for its subfolders.
            foreach (var covered in _watchers.Where(w => IsUnder(w.Path, root)).ToList())
            {
                _watchers.Remove(covered);
                covered.Dispose();
            }

            _watchers.Add(watcher);
            _logger.LogInformation("Watching {Root} for changes", root);
            return true;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);

        // Writes to anything that is not media or tags cannot change a listing.
        if (e.ChangeType == WatcherChangeTypes.Changed && !IsMediaFile(name) && !IsTagsFile(name)) return;

        InvalidateParent(e.FullPath);
        if (e.ChangeType == WatcherChangeTypes.Deleted) InvalidateTree(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        InvalidateParent(e.FullPath);
        InvalidateParent(e.OldFullPath);
        InvalidateTree(e.OldFullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Usually the event buffer overflowing during a big copy: some changes were
        // dropped, so nothing remembered can be trusted any more.
        _logger.LogWarning("Lost track of file changes, forgetting all listings: {Msg}", e.GetException().Message);
        Interlocked.Increment(ref _generation);
        _folders.Clear();
    }

    private void InvalidateParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Invalidate(parent);
    }

    /// <summary>Forgets a folder that went away, along with everything that was below it.</summary>
    private void InvalidateTree(string path)
    {
        var gone = Normalize(path);
        Interlocked.Increment(ref _generation);
        foreach (var folder in _folders.Keys)
            if (IsUnder(folder, gone)) _folders.TryRemove(folder, out _);
    }

    private static bool IsTagsFile(string name) =>
        name.Equals(TagsFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, PathComparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* keep the raw path */ }
        return Path.TrimEndingDirectorySeparator(path);
    }

    public void Dispose()
    {
        _tags.FolderChanged -= Invalidate;
        lock (_watchGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
        GC.SuppressFinalize(this);
    }
}
