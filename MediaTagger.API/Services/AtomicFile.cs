using System.Text;

namespace MediaTagger.API.Services;

/// <summary>
/// Writes a file so a crash, a full disk or a dropped network share halfway through
/// never leaves a truncated file behind: the new text goes to a temp file next to the
/// target and replaces it in one step.
/// </summary>
public static class AtomicFile
{
    private const string TempSuffix = ".tmp";

    public static void WriteAllText(string path, string text, bool keepBackup = false)
    {
        var temp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}{TempSuffix}";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            var backup = path + ".bak";
            try
            {
                if (keepBackup) File.Replace(temp, path, backup);
                else File.Replace(temp, path, null);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // Some network shares cannot do an atomic replace; a copy and a move
                // still keep the old version around until the new one is complete.
                if (keepBackup) File.Copy(path, backup, overwrite: true);
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
