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
    private readonly ILogger<CacheLocator> _logger;

    public CacheLocator(IHostEnvironment env, ILogger<CacheLocator> logger)
    {
        _logger = logger;
        _root = ResolveRoot(env);
        _logger.LogInformation("Cache root: {Path}", _root);
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
        => Path.Combine(GetDirectory(folderName), fileName);

    private string ResolveRoot(IHostEnvironment env)
    {
        // The API project sits one level below the solution, which is where these
        // folders belong — beside the code rather than buried in AppData.
        var solutionRoot = Directory.GetParent(env.ContentRootPath)?.FullName ?? env.ContentRootPath;

        if (IsWritable(solutionRoot, out var reason)) return solutionRoot;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaTagger");
        Directory.CreateDirectory(fallback);
        _logger.LogWarning("Cannot use {Preferred} for cache data ({Reason}), falling back to {Fallback}",
            solutionRoot, reason, fallback);
        return fallback;
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
