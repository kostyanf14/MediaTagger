namespace MediaTagger.API.Services;

/// <summary>
/// Tracks how far the current library scan has got, so the UI can show real progress
/// instead of a spinner. The first scan of a folder reads metadata from every file,
/// which takes seconds on a large library; later ones hit the cache and fly past.
/// </summary>
public class ScanProgress
{
    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();

    internal sealed class Job
    {
        public string Folder = "";
        public long Total;
        public long Processed;
    }

    /// <summary>Starts reporting for a scan; dispose the scope when it finishes.</summary>
    public ScanScope Begin(string folder, int total)
    {
        var job = new Job { Folder = folder, Total = total };
        lock (_gate) _jobs.Add(job);
        return new ScanScope(this, job);
    }

    internal void Advance(Job job) => Interlocked.Increment(ref job.Processed);

    internal void End(Job job)
    {
        lock (_gate) _jobs.Remove(job);
    }

    public ScanProgressSnapshot Snapshot()
    {
        lock (_gate)
        {
            // Several scans overlap (the open folder, the whole library, the face pass).
            // They used to share one counter, so a 45-file folder reset the total while
            // the big scan kept incrementing it.
            var job = _jobs.MaxBy(j => j.Total);
            if (job == null) return new ScanProgressSnapshot();
            var processed = Interlocked.Read(ref job.Processed);
            return new ScanProgressSnapshot
            {
                Active = true,
                Processed = Math.Min(processed, job.Total),
                Total = job.Total,
                Folder = job.Folder
            };
        }
    }
}

public sealed class ScanScope : IDisposable
{
    private readonly ScanProgress _owner;
    private readonly ScanProgress.Job _job;
    private bool _ended;

    internal ScanScope(ScanProgress owner, ScanProgress.Job job)
    {
        _owner = owner;
        _job = job;
    }

    public void Advance() => _owner.Advance(_job);

    public void Dispose()
    {
        if (_ended) return;
        _ended = true;
        _owner.End(_job);
    }
}

public class ScanProgressSnapshot
{
    public bool Active { get; set; }
    public long Processed { get; set; }
    public long Total { get; set; }
    public string Folder { get; set; } = "";
}
