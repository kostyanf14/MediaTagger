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
    // Decoding is the slow step and mostly waits on the disk and libjpeg, so a few
    // run side by side; the models themselves are shared and take turns.
    private static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    private readonly FileSystemService _fs;
    private readonly FaceStore _store;
    private readonly FaceAnalyzer _analyzer;
    private readonly ILogger<FaceIndexer> _logger;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private string[]? _requestedRoots;
    private string _scope = "";
    private CancellationTokenSource? _current;

    // The same file asked for by the scan and by the side panel at once is analysed once.
    private readonly ConcurrentDictionary<string, Task<StoredFileFaces?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private string _root = "";
    private bool _active;
    private long _processed;
    private long _total;

    public FaceIndexer(FileSystemService fs, FaceStore store, FaceAnalyzer analyzer, ILogger<FaceIndexer> logger)
    {
        _fs = fs;
        _store = store;
        _analyzer = analyzer;
        _logger = logger;
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
            _requestedRoots = roots.ToArray();
            _current?.Cancel();
        }
        _wake.Release();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _requestedRoots = null;
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
        // An answer from an older method still beats none when there is no way to redo it.
        if ((known != null && IsCurrent(known, file)) || !_analyzer.Available)
            return Task.FromResult(known);

        return _inFlight.GetOrAdd(file.FilePath, _ => AnalyzeAndForget(file, mtime, cancel));
    }

    private async Task<StoredFileFaces?> AnalyzeAndForget(MediaFile file, long mtime, CancellationToken cancel)
    {
        try
        {
            var result = await _analyzer.AnalyzeAsync(file.FilePath, file.Type == "video", cancel);
            return _store.Save(file.FilePath, file.Size, mtime, MethodOf(file), result.Width, result.Height, result.Faces);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Remembered as "no faces" so a file that cannot be read is not retried on every pass.
            _logger.LogDebug("Face analysis failed for {Path}: {Msg}", file.FilePath, ex.Message);
            return _store.Save(file.FilePath, file.Size, mtime, MethodOf(file), 0, 0, Array.Empty<DetectedFace>());
        }
        finally
        {
            _inFlight.TryRemove(file.FilePath, out _);
        }
    }

    /// <summary>Matches the timestamp form the store keys versions on.</summary>
    public static long MtimeOf(MediaFile file) => file.Modified.ToUniversalTime().Ticks;

    private static int MethodOf(MediaFile file) => FaceAnalyzer.MethodFor(file.Type == "video");

    /// <summary>False for a file analysed the way this version no longer does it.</summary>
    private static bool IsCurrent(StoredFileFaces known, MediaFile file) => known.Method >= MethodOf(file);

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

        // Photos someone is already tagged on go first — they are what the model
        // learns from, so suggestions get useful long before the scan finishes.
        var pending = files
            .Where(f => _store.TryGet(f.FilePath, f.Size, MtimeOf(f)) is not { } known || !IsCurrent(known, f))
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

        await Parallel.ForEachAsync(pending,
            new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancel },
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
