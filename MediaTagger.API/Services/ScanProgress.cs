namespace MediaTagger.API.Services;

/// <summary>
/// Tracks how far the current library scan has got, so the UI can show real progress
/// instead of a spinner. The first scan of a folder reads metadata from every file,
/// which takes seconds on a large library; later ones hit the cache and fly past.
/// </summary>
public class ScanProgress
{
    private long _processed;
    private long _total;
    private int _active;
    private string _folder = "";

    /// <summary>Starts reporting for a scan; dispose the scope when it finishes.</summary>
    public ScanScope Begin(string folder, int total)
    {
        // Last scan wins. Requests overlap rarely, and when they do the newest one is
        // the one the user is waiting on.
        Interlocked.Exchange(ref _processed, 0);
        Interlocked.Exchange(ref _total, total);
        Interlocked.Increment(ref _active);
        _folder = folder;
        return new ScanScope(this);
    }

    internal void Advance() => Interlocked.Increment(ref _processed);

    internal void End() => Interlocked.Decrement(ref _active);

    public ScanProgressSnapshot Snapshot() => new()
    {
        Active = Volatile.Read(ref _active) > 0,
        Processed = Interlocked.Read(ref _processed),
        Total = Interlocked.Read(ref _total),
        Folder = _folder
    };
}

public sealed class ScanScope : IDisposable
{
    private readonly ScanProgress _owner;
    private bool _ended;

    internal ScanScope(ScanProgress owner) => _owner = owner;

    public void Advance() => _owner.Advance();

    public void Dispose()
    {
        if (_ended) return;
        _ended = true;
        _owner.End();
    }
}

public class ScanProgressSnapshot
{
    public bool Active { get; set; }
    public long Processed { get; set; }
    public long Total { get; set; }
    public string Folder { get; set; } = "";
}
