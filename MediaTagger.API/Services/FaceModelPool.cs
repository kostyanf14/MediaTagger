using System.Collections.Concurrent;
using FaceAiSharp;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace MediaTagger.API.Services;

/// <summary>
/// Copies of the face models, lent out one picture at a time. A copy runs one picture
/// at a time, so more copies let more files be analysed side by side. Copies are made
/// as they are first needed; settings that change how they are made retire the pool,
/// and each copy is freed once whoever borrowed it is done.
/// </summary>
public sealed class FaceModelPool
{
    public sealed class Models : IDisposable
    {
        public required IFaceDetectorWithLandmarks Detector { get; init; }
        public required IFaceEmbeddingsGenerator Embedder { get; init; }

        public void Dispose()
        {
            (Detector as IDisposable)?.Dispose();
            (Embedder as IDisposable)?.Dispose();
        }
    }

    /// <summary>A borrowed copy; disposing it gives it back.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly FaceModelPool _pool;
        private Models? _models;

        internal Lease(FaceModelPool pool, Models models) { _pool = pool; _models = models; }

        public Models Models => _models ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            var models = Interlocked.Exchange(ref _models, null);
            if (models != null) _pool.Return(models);
        }
    }

    private readonly ConcurrentBag<Models> _idle = new();
    private readonly SemaphoreSlim _free;
    private readonly object _gate = new();
    private readonly int _modelThreads;
    private readonly ILogger _logger;
    private int _created;
    private int _size;
    private bool _retired;

    public bool Available { get; }
    /// <summary>True when detection runs on the graphics card; false when asked for it but it failed, too.</summary>
    public bool UsingGpu { get; }
    /// <summary>Why the graphics card could not be used, when it was asked for.</summary>
    public string? GpuError { get; }

    public FaceModelPool(int copies, int modelThreads, bool gpu, ILogger logger)
    {
        _modelThreads = modelThreads;
        _logger = logger;

        // The first copy is made straight away: it tells whether the models load at
        // all, and whether the graphics card takes them.
        Models? first = null;
        if (gpu)
        {
            try
            {
                first = Create(gpu: true);
                UsingGpu = true;
            }
            catch (Exception ex)
            {
                GpuError = ex.Message;
                _logger.LogWarning("Face models could not use the graphics card, falling back to the CPU: {Msg}", ex.Message);
            }
        }
        if (first == null)
        {
            try { first = Create(gpu: false); }
            catch (Exception ex)
            {
                _logger.LogWarning("Face recognition is unavailable, models failed to load: {Msg}", ex.Message);
            }
        }

        _size = first == null ? 0 : Math.Max(1, copies);
        _free = new SemaphoreSlim(_size);
        if (first == null) return;

        _idle.Add(first);
        _created = 1;
        Available = true;
        _logger.LogInformation("Face recognition models loaded, detection on the {Device}, up to {Copies} copies with {Threads} threads each",
            UsingGpu ? "graphics card" : "CPU", _size, modelThreads);
    }

    /// <summary>A copy to use for one picture, or null when this pool was retired meanwhile.</summary>
    public async Task<Lease?> RentAsync(CancellationToken cancel)
    {
        if (!Available) throw new InvalidOperationException("Face models are not available");

        while (true)
        {
            await _free.WaitAsync(cancel);
            bool create;
            lock (_gate)
            {
                if (_retired)
                {
                    _free.Release();
                    return null;
                }
                if (_idle.TryTake(out var idle)) return new Lease(this, idle);
                // Every copy made so far is out, and there is room for one more.
                create = _created < _size;
                if (create) _created++;
            }

            if (create)
            {
                try { return new Lease(this, Create(UsingGpu)); }
                catch (Exception ex)
                {
                    // Out of memory, most likely. Keep the copies there are; this
                    // permit stays taken, so the pool simply stays that size.
                    _logger.LogWarning("Could not load another copy of the face models, staying at {Count}: {Msg}", _created - 1, ex.Message);
                    lock (_gate) { _created--; _size = _created; }
                }
            }
            else
            {
                // A copy is on its way back; the permit we hold belongs to it.
                _free.Release();
                await Task.Yield();
            }
        }
    }

    /// <summary>No more lending; idle copies are freed now, the rest as they come back.</summary>
    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
            while (_idle.TryTake(out var models)) models.Dispose();
        }
    }

    private void Return(Models models)
    {
        lock (_gate)
        {
            if (_retired) models.Dispose();
            else _idle.Add(models);
        }
        _free.Release();
    }

    /// <summary>
    /// Only detection goes to the graphics card. The embedding model is quantised to
    /// int8, and DirectML rounds those operations differently: the same face comes out
    /// as much as 0.16 apart from its CPU embedding. Embeddings must stay comparable
    /// with every face already stored, so they are always made on the CPU. Detection
    /// gives the same result either way.
    /// </summary>
    private Models Create(bool gpu)
    {
        using var cpu = new SessionOptions { IntraOpNumThreads = _modelThreads, InterOpNumThreads = 1 };
        using var card = new SessionOptions();
        if (gpu)
        {
            // DirectML runs the graph itself; ONNX Runtime's memory planning and
            // parallel execution get in its way.
            card.EnableMemoryPattern = false;
            card.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            card.AppendExecutionProvider_DML(0);
        }

        var detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(gpu ? card : cpu);
        try
        {
            return new Models { Detector = detector, Embedder = FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator(cpu) };
        }
        catch
        {
            (detector as IDisposable)?.Dispose();
            throw;
        }
    }
}
