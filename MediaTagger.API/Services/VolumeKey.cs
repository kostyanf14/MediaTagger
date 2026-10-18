using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaTagger.API.Services;

/// <summary>
/// Splits a path into the volume it lives on and the part below that volume's root.
/// Keying on the volume's serial rather than its drive letter means an external disk
/// that comes back as G: instead of E: still finds its own entries in every cache.
/// </summary>
public static class VolumeKey
{
    // Volume serials never change, so each root only has to be asked once.
    private static readonly ConcurrentDictionary<string, string> VolumeIds = new(StringComparer.OrdinalIgnoreCase);

    public static (string Volume, string Path) For(string filePath)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(filePath); } catch { full = filePath; }

        var root = System.IO.Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return ("", full.ToLowerInvariant());

        var volume = VolumeIds.GetOrAdd(root, ReadVolumeId);
        var relative = full[root.Length..];
        return (volume, relative.ToLowerInvariant());
    }

    private static string ReadVolumeId(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var label = new StringBuilder(261);
                var fileSystem = new StringBuilder(261);
                if (GetVolumeInformation(root, label, label.Capacity, out var serial,
                        out _, out _, fileSystem, fileSystem.Capacity) && serial != 0)
                    return serial.ToString("X8");
            }
            catch { /* fall through to the path form below */ }
        }

        // Network shares and anything without a serial fall back to the root itself,
        // which is stable enough as long as the share is mounted the same way.
        return root.ToLowerInvariant();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}
