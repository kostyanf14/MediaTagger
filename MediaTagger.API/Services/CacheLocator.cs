namespace MediaTagger.API.Services;

/// <summary>
/// Decides where generated data lives. Everything the app derives from the library —
/// thumbnails, the EXIF cache — goes next to the solution so it is easy to find and
/// safe to delete, and never onto the media drives themselves, which may be read-only,
/// removable, or short on space.
/// </summary>
public class CacheLocator
{
    private readonly string _root;
    private readonly bool _explicit;
    private readonly ILogger<CacheLocator> _logger;

    public CacheLocator(IHostEnvironment env, IConfiguration config, ILogger<CacheLocator> logger)
    {
        _logger = logger;
        var configured = config["MediaTagger:CacheRoot"];
        _explicit = !string.IsNullOrWhiteSpace(configured);
        _root = ResolveRoot(env.ContentRootPath, configured, logger);
        _logger.LogInformation("Cache root: {Path}", _root);
    }

    /// <summary>The folder for generated data. A configured path wins; otherwise the solution, then LocalAppData.</summary>
    public static string ResolveRoot(string contentRootPath, string? configured, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var root = Path.GetFullPath(configured);
            Directory.CreateDirectory(root);
            return root;
        }

        var solutionRoot = Directory.GetParent(contentRootPath)?.FullName ?? contentRootPath;
        if (IsWritable(solutionRoot, out var reason)) return solutionRoot;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaTagger");
        Directory.CreateDirectory(fallback);
        logger?.LogWarning("Cannot use {Preferred} for cache data ({Reason}), falling back to {Fallback}",
            solutionRoot, reason, fallback);
        return fallback;
    }

    /// <summary>Returns (creating if needed) a named folder under the cache root.</summary>
    public string GetDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Returns the full path for a cache file, its folder already created.</summary>
    public string GetFilePath(string folderName, string fileName)
    {
        // A chosen directory is the cache. The dot folder only hides the databases
        // when they would otherwise sit next to the solution.
        if (_explicit && folderName == ".cache")
        {
            var direct = Path.Combine(_root, fileName);
            var nested = Path.Combine(_root, folderName, fileName);
            if (!File.Exists(direct) && File.Exists(nested))
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var from = nested + suffix;
                    if (File.Exists(from)) File.Move(from, direct + suffix);
                }
                var dir = Path.GetDirectoryName(nested)!;
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            return direct;
        }
        return Path.Combine(GetDirectory(folderName), fileName);
    }

    private static bool IsWritable(string directory, out string reason)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // CreateDirectory also succeeds on a read-only location, so prove it.
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
