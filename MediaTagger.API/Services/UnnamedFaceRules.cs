namespace MediaTagger.API.Services;

/// <summary>Which detected faces the “name this face” queue may show.</summary>
public static class UnnamedFaceRules
{
    /// <summary>
    /// Detector confidence below this is not treated as a face. The suggestion list uses the same bar.
    /// </summary>
    public const float MinDetectionScore = 0.72f;

    /// <summary>Photos only. A video face carries the frame time, including a cover at 0.</summary>
    public static bool IsQueuePhoto(double? time, bool labelled, bool skipped)
        => time == null && !labelled && !skipped;

    /// <summary>A detected photo face with no name and no proposal. A weak mark is not a face.</summary>
    public static bool IsUnnamed(double? time, float detection, bool labelled, bool proposed, bool skipped)
        => detection >= MinDetectionScore && IsQueuePhoto(time, labelled, skipped) && !proposed;

    /// <summary>Stored paths are the lowercased path below the volume root. An empty root is the volume itself.</summary>
    public static bool UnderRoot(string storedPath, string rootRelative)
    {
        var stored = storedPath.Replace('/', '\\');
        var root = rootRelative.TrimEnd('\\', '/').Replace('/', '\\');
        if (root.Length == 0) return true;
        if (stored.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return stored.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
