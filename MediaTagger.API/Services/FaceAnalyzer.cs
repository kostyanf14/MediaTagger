using FaceAiSharp;
using ImageMagick;
using MediaTagger.API.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MediaTagger.API.Services;

/// <summary>
/// Finds faces in a picture and turns each into an embedding — a vector that lands
/// close to other photos of the same person. Detection is SCRFD, embeddings are
/// ArcFace, both run locally through ONNX Runtime; nothing leaves the machine.
/// </summary>
public class FaceAnalyzer : IDisposable
{
    /// <summary>
    /// Longest edge the picture is decoded at. Big enough that a face across the room
    /// still has some pixels, small enough that decoding stays the cheap part.
    /// </summary>
    private const int AnalysisSize = 1280;
    /// <summary>Faces smaller than this (px, at analysis size) give embeddings too noisy to trust.</summary>
    private const float MinFaceSide = 36;
    private const float MinConfidence = 0.6f;
    private const int MinFrames = 3;

    /// <summary>
    /// How a file was analysed; a higher number saw more of it. A video stored with a
    /// lower method than the chosen mode is analysed again. Cover-frame analysis is 1,
    /// the same as every video stored before modes existed.
    /// </summary>
    public static int MethodFor(bool isVideo, VideoFaceMode mode) => !isVideo ? 1 : mode switch
    {
        VideoFaceMode.Detailed => 3,
        VideoFaceMode.Frames => 2,
        _ => 1
    };

    /// <summary>
    /// Seconds between frames, so someone who shows up halfway is seen too, and a cap on
    /// frames per video; on long videos the frames simply spread further apart.
    /// </summary>
    private static (double Spacing, int MaxFrames) Sampling(VideoFaceMode mode) =>
        mode == VideoFaceMode.Detailed ? (1, 48) : (2, 16);

    /// <summary>
    /// How the work is spread for a given number of CPU threads. Decoding a photo takes
    /// longer than finding its faces, so the threads go to files decoded side by side,
    /// with a copy of the models per four of them to keep up. More threads inside one
    /// model stop paying off after a few, and past the core count they make it slower.
    /// Embeddings stay on the CPU even with the graphics card on, so it needs the same
    /// copies.
    /// </summary>
    public sealed record Plan(int Threads, int Workers, int Copies, int ModelThreads, bool Gpu);

    public static Plan PlanFor(int threads, bool gpu, int processors)
    {
        var n = threads > 0 ? Math.Clamp(threads, 1, processors) : AutoThreads(processors);
        return new Plan(n, Workers: n, Copies: Math.Max(1, n / 4), ModelThreads: Math.Min(n, 2), gpu);
    }

    /// <summary>Leaves most of the machine to whoever is using it while the scan runs.</summary>
    public static int AutoThreads(int processors) => Math.Clamp(processors / 4, 2, 4);

    private readonly ThumbnailService _thumbnails;
    private readonly SettingsService _settings;
    private readonly ILogger<FaceAnalyzer> _logger;

    private readonly object _poolGate = new();
    private FaceModelPool? _pool;
    private (int Copies, int Threads, bool Gpu) _poolKey;

    public FaceAnalyzer(ThumbnailService thumbnails, SettingsService settings, ILogger<FaceAnalyzer> logger)
    {
        _thumbnails = thumbnails;
        _settings = settings;
        _logger = logger;
    }

    public Plan CurrentPlan()
    {
        var settings = _settings.Get();
        return PlanFor(settings.FaceThreads, settings.FaceGpu, Environment.ProcessorCount);
    }

    public bool Available => Pool().Available;

    /// <summary>Whether detection ended up on the graphics card, and why not when it was meant to.</summary>
    public (bool UsingGpu, string? GpuError) Device()
    {
        var pool = Pool();
        return (pool.UsingGpu, pool.GpuError);
    }

    /// <summary>
    /// The models as the settings currently ask for them. A change of settings builds a
    /// new pool on first use; copies of the old one are freed as their pictures finish.
    /// </summary>
    private FaceModelPool Pool()
    {
        var plan = CurrentPlan();
        var key = (plan.Copies, plan.ModelThreads, plan.Gpu);
        lock (_poolGate)
        {
            if (_pool != null && _poolKey == key) return _pool;
            _pool?.Retire();
            _pool = new FaceModelPool(plan.Copies, plan.ModelThreads, plan.Gpu, _logger);
            _poolKey = key;
            return _pool;
        }
    }

    private async Task<FaceModelPool.Lease> RentAsync(CancellationToken cancel)
    {
        // A pool retired between asking for it and borrowing from it hands out nothing.
        while (true)
        {
            var lease = await Pool().RentAsync(cancel);
            if (lease != null) return lease;
        }
    }

    public sealed record Result(int Width, int Height, List<DetectedFace> Faces);

    /// <summary>Faces in the file, or an empty result when it cannot be read at all.</summary>
    public async Task<Result> AnalyzeAsync(string filePath, bool isVideo, VideoFaceMode mode, CancellationToken cancel)
    {
        if (!Available) throw new InvalidOperationException("Face models are not available");
        if (isVideo && mode is VideoFaceMode.Frames or VideoFaceMode.Detailed)
        {
            var video = await AnalyzeVideoAsync(filePath, mode, cancel);
            if (video != null) return video;
        }

        using var image = await LoadAsync(filePath, isVideo, cancel);
        if (image == null) return new Result(0, 0, new());
        return new Result(image.Width, image.Height, await DetectAsync(image, null, cancel));
    }

    /// <summary>
    /// Frames spread evenly over the whole clip, with the faces in them folded into one
    /// per person. Null when no frame could be grabbed, so the caller can fall back to
    /// the cover frame the grid shows.
    /// </summary>
    private async Task<Result?> AnalyzeVideoAsync(string filePath, VideoFaceMode mode, CancellationToken cancel)
    {
        var duration = await _thumbnails.GetVideoDurationAsync(filePath);
        if (duration <= 0) return null;

        var frames = new List<IReadOnlyList<DetectedFace>>();
        int width = 0, height = 0;
        foreach (var seconds in SampleTimes(duration, mode))
        {
            cancel.ThrowIfCancellationRequested();
            var jpeg = await _thumbnails.GrabVideoFrameAsync(filePath, seconds, AnalysisSize, cancel);
            if (jpeg == null) continue;

            Image<Rgb24> image;
            try { image = await Task.Run(() => Decode(jpeg), cancel); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Could not read frame {Seconds}s of {Path}: {Msg}", seconds, filePath, ex.Message);
                continue;
            }
            using (image)
            {
                (width, height) = (image.Width, image.Height);
                frames.Add(await DetectAsync(image, seconds, cancel));
            }
        }

        return frames.Count == 0 ? null : new Result(width, height, VideoFaceTracks.Merge(frames));
    }

    /// <summary>Seconds into a clip of <paramref name="duration"/> to look at, evenly spaced, clear of the very end.</summary>
    public static double[] SampleTimes(double duration, VideoFaceMode mode)
    {
        if (duration <= 0) return new[] { 0.0 };
        var (spacing, maxFrames) = Sampling(mode);
        var count = Math.Clamp((int)Math.Ceiling(duration / spacing), MinFrames, maxFrames);
        // Some containers cannot seek right up to the duration they report.
        var last = Math.Max(0, duration - 0.5);
        return Enumerable.Range(0, count)
            .Select(i => Math.Round(Math.Min(last, (i + 0.5) * duration / count), 3))
            .Distinct()
            .ToArray();
    }

    private async Task<List<DetectedFace>> DetectAsync(Image<Rgb24> image, double? seconds, CancellationToken cancel)
    {
        using var lease = await RentAsync(cancel);
        var models = lease.Models;
        var detected = models.Detector.DetectFaces(image);

        var faces = new List<DetectedFace>();
        foreach (var face in detected)
        {
            cancel.ThrowIfCancellationRequested();
            if ((face.Confidence ?? 0) < MinConfidence || face.Landmarks == null) continue;
            if (Math.Min(face.Box.Width, face.Box.Height) < MinFaceSide) continue;

            float[] embedding;
            using (var aligned = image.Clone())
            {
                models.Embedder.AlignFaceUsingLandmarks(aligned, face.Landmarks);
                embedding = models.Embedder.GenerateEmbedding(aligned);
            }

            // Boxes may poke past the edge; clamp them so crops in the UI stay inside the picture.
            var x = Math.Clamp(face.Box.X / image.Width, 0, 1);
            var y = Math.Clamp(face.Box.Y / image.Height, 0, 1);
            var w = Math.Clamp(face.Box.Right / image.Width, 0, 1) - x;
            var h = Math.Clamp(face.Box.Bottom / image.Height, 0, 1) - y;
            faces.Add(new DetectedFace(x, y, w, h, face.Confidence ?? 0, Normalize(embedding), seconds));
        }

        return faces;
    }

    /// <summary>
    /// Decodes in process when Magick can, which is most photos. Videos, and anything it
    /// refuses, go through the thumbnail service — the same converter the grid relies on.
    /// </summary>
    private async Task<Image<Rgb24>?> LoadAsync(string filePath, bool isVideo, CancellationToken cancel)
    {
        if (!isVideo)
        {
            try { return await Task.Run(() => Decode(filePath, hintJpegSize: true), cancel); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("In-process decode of {Path} failed: {Msg}", filePath, ex.Message);
            }
        }

        var converted = await _thumbnails.GetThumbnailPath(filePath, AnalysisSize, cancel);
        if (converted == null) return null;

        try { return await Task.Run(() => Decode(converted, hintJpegSize: false), cancel); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Could not read converted frame of {Path}: {Msg}", filePath, ex.Message);
            return null;
        }
    }

    private static Image<Rgb24> Decode(string path, bool hintJpegSize)
    {
        var settings = new MagickReadSettings();
        // Lets the JPEG decoder skip detail that the resize below would discard.
        if (hintJpegSize) settings.SetDefine(MagickFormat.Jpeg, "size", $"{AnalysisSize}x{AnalysisSize}");

        using var magick = new MagickImage(path, settings);
        return ToAnalysisImage(magick);
    }

    private static Image<Rgb24> Decode(byte[] jpeg)
    {
        using var magick = new MagickImage(jpeg);
        return ToAnalysisImage(magick);
    }

    private static Image<Rgb24> ToAnalysisImage(MagickImage magick)
    {
        // Face boxes are stored against the upright picture, the same one thumbnails show.
        magick.AutoOrient();
        if (magick.Width > AnalysisSize || magick.Height > AnalysisSize)
            magick.Thumbnail(AnalysisSize, AnalysisSize);
        if (magick.HasAlpha) magick.Alpha(AlphaAction.Remove);

        var pixels = magick.ToByteArray(MagickFormat.Rgb);
        return Image.LoadPixelData<Rgb24>(pixels, (int)magick.Width, (int)magick.Height);
    }

    /// <summary>Unit length, so a plain dot product is the cosine similarity.</summary>
    private static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(System.Numerics.Tensors.TensorPrimitives.Dot<float>(vector, vector));
        if (norm > 0) System.Numerics.Tensors.TensorPrimitives.Divide(vector, norm, vector);
        return vector;
    }

    public void Dispose()
    {
        lock (_poolGate) _pool?.Retire();
        GC.SuppressFinalize(this);
    }
}
