using System.IO.Compression;
using System.Text;

namespace MediaTagger.API.Services;

/// <summary>
/// Years of tagging live in small <c>_tags.json</c> files next to the photos and in the
/// face database. Once a day (and on request) both are zipped into the cache folder,
/// and only the newest few copies are kept.
/// </summary>
public class TagBackupService : BackgroundService
{
    private const string FolderName = "backups";
    private const int KeepCopies = 14;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    // The app is usually opened to work, not to wait: the first copy can come a little later.
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    private readonly CacheLocator _locator;
    private readonly SettingsService _settings;
    private readonly FaceStore _faces;
    private readonly ILogger<TagBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TagBackupService(CacheLocator locator, SettingsService settings, FaceStore faces,
        ILogger<TagBackupService> logger)
    {
        _locator = locator;
        _settings = settings;
        _faces = faces;
        _logger = logger;
    }

    public sealed record Result(string Path, int TagFiles, bool Faces);

    /// <summary>Makes a copy now. Returns null when there is nothing to copy.</summary>
    public async Task<Result?> BackupNowAsync()
    {
        await _gate.WaitAsync();
        try { return await Task.Run(CreateBackup); }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(StartDelay, stopping);
            while (!stopping.IsCancellationRequested)
            {
                if (IsDue())
                {
                    try { await BackupNowAsync(); }
                    catch (Exception ex) { _logger.LogWarning("Tag backup failed: {Msg}", ex.Message); }
                }
                await Task.Delay(TimeSpan.FromHours(1), stopping);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private bool IsDue()
    {
        var newest = Existing().FirstOrDefault();
        return newest == null || DateTime.UtcNow - newest.LastWriteTimeUtc >= Interval;
    }

    private List<FileInfo> Existing()
    {
        var dir = _locator.GetDirectory(FolderName);
        return new DirectoryInfo(dir).EnumerateFiles("tags-*.zip")
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    private Result? CreateBackup()
    {
        var roots = FileSystemService.Collapse(_settings.Get().LibraryFolders);
        if (roots.Length == 0) return null;

        var dir = _locator.GetDirectory(FolderName);
        var name = $"tags-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var finalPath = Path.Combine(dir, name);
        var tempPath = finalPath + ".tmp";
        var tagFiles = 0;
        var faces = false;
        var manifest = new StringBuilder();

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (var i = 0; i < roots.Length; i++)
                {
                    manifest.AppendLine($"root{i}\t{roots[i]}");
                    foreach (var folder in TagsService.FoldersWithTags(roots[i]))
                    {
                        var tags = Path.Combine(folder, "_tags.json");
                        var relative = Path.GetRelativePath(roots[i], folder).Replace('\\', '/');
                        try { zip.CreateEntryFromFile(tags, $"root{i}/{relative}/_tags.json".Replace("/./", "/")); }
                        catch (IOException ex) { _logger.LogDebug("Skipped {Path} in the backup: {Msg}", tags, ex.Message); continue; }
                        tagFiles++;
                    }
                }

                var facesCopy = Path.Combine(dir, "faces-copy-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
                try
                {
                    _faces.BackupTo(facesCopy);
                    zip.CreateEntryFromFile(facesCopy, "faces.db");
                    faces = true;
                }
                catch (Exception ex) { _logger.LogWarning("Face database not included in the backup: {Msg}", ex.Message); }
                finally { try { File.Delete(facesCopy); } catch { /* best effort */ } }

                var entry = zip.CreateEntry("manifest.txt");
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    writer.Write(manifest.ToString());
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }

        foreach (var old in Existing().Skip(KeepCopies))
        {
            try { old.Delete(); } catch { /* best effort */ }
        }

        _logger.LogInformation("Tag backup written: {Path} ({Files} tag files, faces: {Faces})", finalPath, tagFiles, faces);
        return new Result(finalPath, tagFiles, faces);
    }
}
