using System.Collections.Concurrent;
using System.Globalization;
using ImageMagick;
using Xabe.FFmpeg;

namespace MediaTagger.API.Services;

public class ThumbnailService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm", ".mts", ".m2ts" };

    private static readonly HashSet<string> HeicExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".heic", ".heif" };

    /// <summary>Marks an in-progress conversion; the .jpg tail tells FFmpeg what to write.</summary>
    private const string TempSuffix = ".part.jpg";
    /// <summary>Matches every in-progress file, including the two-pass intermediate.</summary>
    private const string TempGlob = "*.part*.jpg";
    /// <summary>Cache folder name, created next to the solution root.</summary>
    private const string CacheFolderName = ".thumbnails";
    /// <summary>How much of an FFmpeg error to keep when logging it.</summary>
    private const int ErrorTailLength = 400;
    /// <summary>
    /// The size grids ask for. Anything smaller is cut from this one instead of from
    /// the original, which for a video or a HEIC is the expensive part all over again.
    /// </summary>
    private const int BaseSize = 480;
    /// <summary>The cache is trimmed back under this on startup, oldest entries first.</summary>
    private const long CacheLimitBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _cacheDir;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly bool _ffmpegAvailable;

    // One conversion at a time per cache entry. A gallery mounting fires a burst of
    // requests and the same file can be asked for from several panels at once —
    // without this they all shell out to FFmpeg and clobber each other's output.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _entryLocks = new();

    // Hard cap on conversions running at once, in-process or FFmpeg. A folder of
    // HEIC files would otherwise start one per tile and starve the machine, which is
    // what makes thumbnails fail intermittently under load.
    private readonly SemaphoreSlim _workSlots = new(Math.Max(2, Environment.ProcessorCount / 2));

    public ThumbnailService(ILogger<ThumbnailService> logger, CacheLocator locator)
    {
        _logger = logger;
        _cacheDir = locator.GetDirectory(CacheFolderName);
        _logger.LogInformation("Thumbnail cache: {Path}", _cacheDir);
        CleanupStaleTempFiles();
        _ = Task.Run(TrimCache);

        // The slots above already spread work across cores; letting each conversion
        // fan out as well only makes them fight.
        ResourceLimits.Thread = 1;

        var ffmpegDir = FindFfmpegDirectory();
        if (!string.IsNullOrEmpty(ffmpegDir))
        {
            // Always pin the directory explicitly — leaving it unset makes Xabe resolve
            // the executable relative to the working directory, which is not where
            // FFmpeg lives when the API is launched by Electron.
            FFmpeg.SetExecutablesPath(ffmpegDir);
            _ffmpegAvailable = true;
            _logger.LogInformation("FFmpeg found at: {Path}", ffmpegDir);
        }
        else
        {
            _ffmpegAvailable = false;
            _logger.LogWarning("FFmpeg not found. Video thumbnails will not work. Install FFmpeg and add to PATH.");
        }
    }

    /// <summary>True for formats no browser can decode — these are useless without a conversion.</summary>
    public bool NeedsConversion(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return HeicExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    /// <summary>
    /// Path to a cached JPEG for <paramref name="filePath"/>, or null when none is needed
    /// or none could be produced. <paramref name="maxSize"/> bounds the longest edge;
    /// pass 0 for a full-resolution conversion.
    /// </summary>
    public async Task<string?> GetThumbnailPath(string filePath, int maxSize = 0, CancellationToken cancel = default)
    {
        var ext = Path.GetExtension(filePath);
        var isVideo = VideoExtensions.Contains(ext);
        var isHeic = HeicExtensions.Contains(ext);

        // Browser-readable images only need a cache entry when we are downscaling them.
        if (!isVideo && !isHeic && maxSize <= 0) return null;
        if (isVideo && !_ffmpegAvailable) return null;

        var cachePath = GetCachePath(filePath, maxSize);
        if (IsCacheValid(cachePath, filePath)) return cachePath;

        // Small previews are cut from the grid-sized one, which the gallery has
        // usually made already and which is a few kilobytes to decode.
        string? basePath = null;
        if (maxSize > 0 && maxSize < BaseSize)
            basePath = await GetThumbnailPath(filePath, BaseSize, cancel);

        // A tile that scrolled away while waiting gives up its place here, so the
        // ones still on screen are not queued behind it.
        var gate = _entryLocks.GetOrAdd(cachePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel);
        try
        {
            // Someone else may have produced it while we waited on the gate.
            if (IsCacheValid(cachePath, filePath)) return cachePath;

            // Keep the .jpg tail: FFmpeg picks the output format from the extension.
            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix;
            try
            {
                await _workSlots.WaitAsync(cancel);
                try
                {
                    if (basePath != null) await Task.Run(() => RenderInProcess(basePath, tempPath, maxSize));
                    else if (isVideo) await ExtractVideoFrameAsync(filePath, tempPath, maxSize);
                    else await RenderImageAsync(filePath, tempPath, maxSize, isHeic);
                }
                finally { _workSlots.Release(); }

                // Caching an empty result would break the thumbnail permanently,
                // since the cache entry is newer than the source.
                if (!HasContent(tempPath))
                {
                    _logger.LogWarning("Thumbnail produced no output for {Path}", filePath);
                    return null;
                }

                // Publish atomically so a parallel request never reads a half-written JPEG.
                File.Move(tempPath, cachePath, overwrite: true);
                return cachePath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Thumbnail failed for {Path}: {Msg}", filePath, Summarize(ex));
                return null;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        finally
        {
            gate.Release();
            // Drop the gate once nobody holds it, so the dictionary does not grow with the
            // library. A racing caller may briefly get a fresh gate and convert twice —
            // harmless, because publishing is atomic.
            if (gate.CurrentCount == 1)
                _entryLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(cachePath, gate));
        }
    }

    private string GetCachePath(string filePath, int maxSize)
    {
        var normalized = filePath;
        try { normalized = Path.GetFullPath(filePath); } catch { /* keep the raw path */ }
        if (OperatingSystem.IsWindows()) normalized = normalized.ToLowerInvariant();

        var cacheKey = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized + "|" + maxSize)));
        return Path.Combine(_cacheDir, cacheKey + ".jpg");
    }

    /// <summary>
    /// FFmpeg attaches its whole version banner and input dump to every error. Keep the
    /// tail, which is where the actual reason is, so one failure is not a screen of log.
    /// </summary>
    private static string Summarize(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= ErrorTailLength ? message : "..." + message[^ErrorTailLength..];
    }

    /// <summary>FFmpeg leaves a zero-byte file behind when it bails out mid-run.</summary>
    private static bool HasContent(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    private static bool IsCacheValid(string cachePath, string sourcePath)
    {
        var cached = new FileInfo(cachePath);
        return cached.Exists
            && cached.Length > 0
            && cached.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(sourcePath);
    }

    /// <summary>Fits the image inside maxSize x maxSize without upscaling or changing the aspect ratio.</summary>
    private static string ScaleFilter(int maxSize) =>
        $"scale=w='min({maxSize},iw)':h='min({maxSize},ih)':force_original_aspect_ratio=decrease:flags=lanczos";

    /// <summary>
    /// Stills are decoded in this process: starting FFmpeg costs more than the
    /// conversion itself for a typical photo. FFmpeg stays as the fallback for
    /// anything the decoder refuses.
    /// </summary>
    private async Task RenderImageAsync(string source, string outJpeg, int maxSize, bool isHeic)
    {
        try
        {
            await Task.Run(() => RenderInProcess(source, outJpeg, maxSize));
            if (HasContent(outJpeg)) return;
        }
        catch (Exception ex) when (_ffmpegAvailable)
        {
            _logger.LogDebug("In-process conversion of {Path} failed, falling back to FFmpeg: {Msg}",
                source, Summarize(ex));
        }

        if (!_ffmpegAvailable) return;
        TryDelete(outJpeg);
        await ConvertImageAsync(source, outJpeg, maxSize, isHeic);
    }

    private static void RenderInProcess(string source, string outJpeg, int maxSize)
    {
        var settings = new MagickReadSettings();
        // Lets the JPEG decoder skip detail the thumbnail would throw away anyway.
        // It stops at the last power-of-two step still at least this big, so the
        // resize below always has something to work with.
        if (maxSize > 0)
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxSize}x{maxSize}");

        using var image = new MagickImage(source, settings);

        // Phones record rotation as a flag; bake it in before the flag is stripped.
        image.AutoOrient();

        if (maxSize > 0 && (image.Width > maxSize || image.Height > maxSize))
            image.Thumbnail((uint)maxSize, (uint)maxSize);

        if (image.HasAlpha)
        {
            // JPEG has no transparency; without a backdrop it comes out black.
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }

        image.Strip();
        image.Quality = maxSize > 0 ? 82u : 92u;
        image.Format = MagickFormat.Jpeg;
        image.Write(outJpeg);
    }

    /// <summary>FFmpeg converts HEIC → JPEG natively, no libheif needed.</summary>
    private async Task ConvertImageAsync(string source, string outJpeg, int maxSize, bool isHeic)
    {
        // An iPhone HEIC stores the picture as a grid of tiles, which FFmpeg exposes as
        // a stream group. Scaling that group inside one filtergraph does the whole job
        // in a single process; files without a grid fail here and take the path below.
        if (isHeic && maxSize > 0)
        {
            try
            {
                await RunGridConversionAsync(source, outJpeg, maxSize);
                if (HasContent(outJpeg)) return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Single-pass conversion of {Path} failed: {Msg}", source, Summarize(ex));
            }
            TryDelete(outJpeg);
        }

        try
        {
            await RunImageConversionAsync(source, outJpeg, maxSize);
        }
        catch (Exception ex) when (maxSize > 0)
        {
            _logger.LogDebug("Scaled conversion of {Path} failed, retrying in two passes: {Msg}",
                source, Summarize(ex));
        }

        if (maxSize <= 0 || HasContent(outJpeg)) return;

        // The complex filtergraph that stitches a tile grid refuses to run alongside
        // the simple -vf scaler. Decode the grid first, then scale the result.
        var stitched = Path.ChangeExtension(outJpeg, null) + ".full.jpg";
        try
        {
            await RunImageConversionAsync(source, stitched, 0);
            if (!HasContent(stitched)) return;
            await RunImageConversionAsync(stitched, outJpeg, maxSize);
        }
        finally
        {
            TryDelete(stitched);
        }
    }

    private static async Task RunGridConversionAsync(string source, string outJpeg, int maxSize)
    {
        var conversion = FFmpeg.Conversions.New()
            .AddParameter($"-i \"{source}\"")
            .AddParameter($"-filter_complex \"[0:g:0]{ScaleFilter(maxSize)}[out]\"")
            .AddParameter("-map \"[out]\"")
            .AddParameter("-frames:v 1")
            .AddParameter("-q:v 4")
            .SetOutput(outJpeg)
            .SetOverwriteOutput(true);

        await conversion.Start();
    }

    private static async Task RunImageConversionAsync(string source, string outJpeg, int maxSize)
    {
        var conversion = FFmpeg.Conversions.New()
            .AddParameter($"-i \"{source}\"")
            .AddParameter("-frames:v 1");

        if (maxSize > 0)
            conversion.AddParameter($"-vf \"{ScaleFilter(maxSize)}\"");

        conversion
            .AddParameter(maxSize > 0 ? "-q:v 4" : "-q:v 2")
            .SetOutput(outJpeg)
            .SetOverwriteOutput(true);

        await conversion.Start();
    }

    private async Task ExtractVideoFrameAsync(string videoPath, string outJpeg, int maxSize)
    {
        var seekSeconds = await GetSeekPositionAsync(videoPath);
        await SnapshotAsync(videoPath, outJpeg, seekSeconds, maxSize);

        // Some containers report a duration they cannot actually seek to; the first
        // frame is a fine fallback and always exists.
        if (seekSeconds > 0 && !HasContent(outJpeg))
            await SnapshotAsync(videoPath, outJpeg, 0, maxSize);
    }

    private static async Task SnapshotAsync(string videoPath, string outJpeg, double seekSeconds, int maxSize)
    {
        var conversion = FFmpeg.Conversions.New();

        // -ss before -i is the fast seek: FFmpeg jumps instead of decoding up to the mark.
        if (seekSeconds > 0)
            conversion.AddParameter($"-ss {seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");

        conversion
            .AddParameter($"-i \"{videoPath}\"")
            .AddParameter("-frames:v 1");

        if (maxSize > 0)
            conversion.AddParameter($"-vf \"{ScaleFilter(maxSize)}\"");

        conversion
            .AddParameter("-q:v 4")
            .SetOutput(outJpeg)
            .SetOverwriteOutput(true);

        await conversion.Start();
    }

    private async Task<double> GetSeekPositionAsync(string videoPath)
    {
        try
        {
            var mediaInfo = await FFmpeg.GetMediaInfo(videoPath);
            var duration = mediaInfo.Duration.TotalSeconds;
            if (duration <= 0) return 0;
            // 10% in, but never past the last half second of the clip.
            return Math.Clamp(duration * 0.1, 0, Math.Max(0, duration - 0.5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not probe {Path}, seeking to first frame: {Msg}", videoPath, ex.Message);
            return 0;
        }
    }

    /// <summary>Removes half-written files left behind by a crash or a killed process.</summary>
    private void CleanupStaleTempFiles()
    {
        try
        {
            foreach (var leftover in Directory.EnumerateFiles(_cacheDir, TempGlob))
                TryDelete(leftover);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Thumbnail cache cleanup skipped: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Keeps the cache from growing without bound. Entries are cheap to make again,
    /// so the ones untouched for longest simply go once the folder is over its limit.
    /// </summary>
    private void TrimCache()
    {
        try
        {
            var entries = new DirectoryInfo(_cacheDir).EnumerateFiles("*.jpg").ToList();
            var total = entries.Sum(e => e.Length);
            if (total <= CacheLimitBytes) return;

            // Trim well below the limit so this does not run again on every start.
            var target = CacheLimitBytes * 8 / 10;
            var removed = 0;
            foreach (var entry in entries.OrderBy(e => e.LastWriteTimeUtc))
            {
                if (total <= target) break;
                total -= entry.Length;
                TryDelete(entry.FullName);
                removed++;
            }
            _logger.LogInformation("Thumbnail cache trimmed: {Count} oldest entries removed", removed);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Thumbnail cache trim skipped: {Msg}", ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string FindFfmpegDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin"),
            @"C:\ffmpeg\bin",
            "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"
        };
        foreach (var dir in candidates)
        {
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(exe)) return dir;
        }
        return ResolveDirectoryFromPath("ffmpeg");
    }

    /// <summary>Asks the OS where a command lives and returns its directory.</summary>
    private static string ResolveDirectoryFromPath(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000) || proc.ExitCode != 0) return "";

            var firstHit = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);

            return firstHit == null ? "" : Path.GetDirectoryName(firstHit) ?? "";
        }
        catch { return ""; }
    }
}
