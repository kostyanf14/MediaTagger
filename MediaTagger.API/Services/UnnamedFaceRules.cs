namespace MediaTagger.API.Services;

/// <summary>Which detected faces the “name this face” queue may show.</summary>
public static class UnnamedFaceRules
{
    /// <summary>Photos only. A video face carries the frame time, including a cover at 0.</summary>
    public static bool IsQueuePhoto(double? time, bool labelled, bool skipped)
        => time == null && !labelled && !skipped;

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
