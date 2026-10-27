using System.Collections.Concurrent;
using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

/// <summary>
/// Works through the open library in the background, finding faces in every file that
/// has not been analysed yet. A first pass over a large library takes a while, so it
/// is resumable — whatever was done is kept — and later passes only pick up new or
/// changed files.
/// </summary>
public class FaceIndexer : BackgroundService
{
    private readonly FileSystemService _fs;
    private readonly FaceStore _store;
    private readonly FaceAnalyzer _analyzer;
    private readonly SettingsService _settings;
    private readonly ILogger<FaceIndexer> _logger;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private string[]? _requestedRoots;
    // What the last pass covered, so a change of settings can redo it.
    private string[]? _lastRoots;
    private string _scope = "";
    private CancellationTokenSource? _current;

    // The same file asked for by the scan and by the side panel at once is analysed once.
    private readonly ConcurrentDictionary<string, Task<StoredFileFaces?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private string _root = "";
    private bool _active;
    private long _processed;
    private long _total;

    public FaceIndexer(FileSystemService fs, FaceStore store, FaceAnalyzer analyzer, SettingsService settings,
        ILogger<FaceIndexer> logger)
    {
        _fs = fs;
        _store = store;
        _analyzer = analyzer;
        _settings = settings;
        _logger = logger;
        _settings.Changed += (before, after) =>
        {
            if (before.VideoFaces != after.VideoFaces || before.FaceThreads != after.FaceThreads
                || before.FaceGpu != after.FaceGpu)
                Rescan();
        };
    }

    public bool IsActive => Volatile.Read(ref _active);

    public FaceScanStatus Status() => new()
    {
        Available = _analyzer.Available,
        Active = IsActive,
        Root = _root,
        Processed = Interlocked.Read(ref _processed),
        Total = Interlocked.Read(ref _total)
    };

    /// <summary>Starts (or restarts) a pass over every root. One database, one progress bar.</summary>
    public void Start(IReadOnlyList<string> roots)
    {
        var scope = FileSystemService.Scope(roots);
        lock (_gate)
        {
            // Asking again for the library already being scanned changes nothing.
            if (IsActive && _scope == scope) return;
            _requestedRoots = _lastRoots = roots.ToArray();
            _current?.Cancel();
        }
        _wake.Release();
    }

    /// <summary>Starts the last pass over again, even if it is still running, so it picks up changed settings.</summary>
    private void Rescan()
    {
        lock (_gate)
        {
            if (_lastRoots == null) return;
            _requestedRoots = _lastRoots;
            _current?.Cancel();
        }
        _wake.Release();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _requestedRoots = _lastRoots = null;
            _current?.Cancel();
        }
    }

    /// <summary>
    /// Faces in one file, analysing it first if needed. Used for the file the user is
    /// looking at, which should not have to wait for the scan to reach it.
    /// </summary>
    public Task<StoredFileFaces?> EnsureAnalyzedAsync(MediaFile file, CancellationToken cancel = default)
    {
        var mtime = MtimeOf(file);
        var known = _store.TryGet(file.FilePath, file.Size, mtime);
        var mode = _settings.VideoFaces;
        // An answer from a lesser method still beats none when it is not to be redone.
        if ((known != null && IsCurrent(known, file, mode)) || !_analyzer.Available || IsSkipped(file, mode))
            return Task.FromResult(known);

        return _inFlight.GetOrAdd(file.FilePath, _ => AnalyzeAndForget(file, mtime, mode, cancel));
    }

    private async Task<StoredFileFaces?> AnalyzeAndForget(MediaFile file, long mtime, VideoFaceMode mode, CancellationToken cancel)
    {
        var method = FaceAnalyzer.MethodFor(file.Type == "video", mode);
        try
        {
            var result = await _analyzer.AnalyzeAsync(file.FilePath, file.Type == "video", mode, cancel);
            return _store.Save(file.FilePath, file.Size, mtime, method, result.Width, result.Height, result.Faces);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Remembered as "no faces" so a file that cannot be read is not retried on every pass.
            _logger.LogDebug("Face analysis failed for {Path}: {Msg}", file.FilePath, ex.Message);
            return _store.Save(file.FilePath, file.Size, mtime, method, 0, 0, Array.Empty<DetectedFace>());
        }
        finally
        {
            _inFlight.TryRemove(file.FilePath, out _);
        }
    }

    /// <summary>Matches the timestamp form the store keys versions on.</summary>
    public static long MtimeOf(MediaFile file) => file.Modified.ToUniversalTime().Ticks;

    /// <summary>
    /// True when the file was analysed at least as thoroughly as the mode asks. Switching
    /// to a lighter mode keeps the better results instead of redoing them worse.
    /// </summary>
    private static bool IsCurrent(StoredFileFaces known, MediaFile file, VideoFaceMode mode) =>
        known.Method >= FaceAnalyzer.MethodFor(file.Type == "video", mode);

    private static bool IsSkipped(MediaFile file, VideoFaceMode mode) =>
        file.Type == "video" && mode == VideoFaceMode.Off;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            await _wake.WaitAsync(stopping);

            string[]? roots;
            CancellationTokenSource run;
            lock (_gate)
            {
                roots = _requestedRoots;
                if (roots == null) continue;
                _requestedRoots = null;
                _current?.Dispose();
                _current = run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            }

            try { await ScanAsync(roots, run.Token); }
            catch (OperationCanceledException) { /* replaced by a newer request, or stopped */ }
            catch (Exception ex) { _logger.LogWarning("Face scan of {Root} failed: {Msg}", _scope, ex.Message); }
            finally { Volatile.Write(ref _active, false); }
        }
    }

    private async Task ScanAsync(IReadOnlyList<string> roots, CancellationToken cancel)
    {
        if (!_analyzer.Available || roots.Count == 0) return;

        // Listing goes through the library index, which is usually warm already.
        var files = await Task.Run(() => _fs.Across(roots), cancel);
        var mode = _settings.VideoFaces;

        // Photos someone is already tagged on go first — they are what the model
        // learns from, so suggestions get useful long before the scan finishes.
        var pending = files
            .Where(f => !IsSkipped(f, mode))
            .Where(f => _store.TryGet(f.FilePath, f.Size, MtimeOf(f)) is not { } known || !IsCurrent(known, f, mode))
            .OrderByDescending(f => f.Tags.People.Count > 0)
            .ThenBy(f => f.Type == "video")
            .ThenByDescending(f => f.Tags.DateTaken ?? f.Modified)
            .ToList();

        _scope = FileSystemService.Scope(roots);
        _root = _scope;
        Interlocked.Exchange(ref _processed, 0);
        Interlocked.Exchange(ref _total, pending.Count);
        if (pending.Count == 0) return;

        Volatile.Write(ref _active, true);
        _logger.LogInformation("Face scan of {Root}: {Count} files to analyse", _scope, pending.Count);

        // Decoding is the slow step, so the threads the settings allow go to files
        // side by side; the models are shared out among them by the analyzer.
        var workers = _analyzer.CurrentPlan().Workers;
        await Parallel.ForEachAsync(pending,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancel },
            async (file, token) =>
            {
                await EnsureAnalyzedAsync(file, token);
                Interlocked.Increment(ref _processed);
            });

        _logger.LogInformation("Face scan of {Root} finished", _scope);
    }
}

public class FaceScanStatus
{
    /// <summary>False when the models could not be loaded; nothing will ever be found.</summary>
    public bool Available { get; set; }
    public bool Active { get; set; }
    public string Root { get; set; } = "";
    public long Processed { get; set; }
    public long Total { get; set; }
}
