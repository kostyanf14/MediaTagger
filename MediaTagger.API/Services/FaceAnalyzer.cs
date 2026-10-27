using FaceAiSharp;
using ImageMagick;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;
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
    /// <summary>A video is looked at every this many seconds, so someone who shows up halfway is seen too.</summary>
    private const double FrameSpacing = 2;
    private const int MinFrames = 3;
    /// <summary>Caps the work on long videos; the frames then simply spread further apart.</summary>
    private const int MaxFrames = 16;

    /// <summary>
    /// How a file was analysed. Files stored with an older method are analysed again:
    /// videos used to be judged by their cover frame alone, now by frames from end to end.
    /// </summary>
    public static int MethodFor(bool isVideo) => isVideo ? 2 : 1;

    private readonly ThumbnailService _thumbnails;
    private readonly ILogger<FaceAnalyzer> _logger;
    private readonly Lazy<(IFaceDetectorWithLandmarks Detector, IFaceEmbeddingsGenerator Embedder)?> _models;

    // The models are shared: loading a second copy costs more memory than the
    // parallel decoding around them gains back. Decoding is the slow part anyway.
    private readonly object _detectGate = new();
    private readonly object _embedGate = new();

    public FaceAnalyzer(ThumbnailService thumbnails, ILogger<FaceAnalyzer> logger)
    {
        _thumbnails = thumbnails;
        _logger = logger;
        _models = new Lazy<(IFaceDetectorWithLandmarks, IFaceEmbeddingsGenerator)?>(LoadModels);
    }

    public bool Available => _models.Value != null;

    public sealed record Result(int Width, int Height, List<DetectedFace> Faces);

    /// <summary>Faces in the file, or an empty result when it cannot be read at all.</summary>
    public async Task<Result> AnalyzeAsync(string filePath, bool isVideo, CancellationToken cancel)
    {
        var models = _models.Value ?? throw new InvalidOperationException("Face models are not available");
        if (isVideo)
        {
            var video = await AnalyzeVideoAsync(models, filePath, cancel);
            if (video != null) return video;
        }

        using var image = await LoadAsync(filePath, isVideo, cancel);
        if (image == null) return new Result(0, 0, new());
        return new Result(image.Width, image.Height, Detect(models, image, null, cancel));
    }

    /// <summary>
    /// Frames spread evenly over the whole clip, with the faces in them folded into one
    /// per person. Null when no frame could be grabbed, so the caller can fall back to
    /// the cover frame the grid shows.
    /// </summary>
    private async Task<Result?> AnalyzeVideoAsync((IFaceDetectorWithLandmarks Detector, IFaceEmbeddingsGenerator Embedder) models,
        string filePath, CancellationToken cancel)
    {
        var duration = await _thumbnails.GetVideoDurationAsync(filePath);
        if (duration <= 0) return null;

        var frames = new List<IReadOnlyList<DetectedFace>>();
        int width = 0, height = 0;
        foreach (var seconds in SampleTimes(duration))
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
                frames.Add(Detect(models, image, seconds, cancel));
            }
        }

        return frames.Count == 0 ? null : new Result(width, height, VideoFaceTracks.Merge(frames));
    }

    /// <summary>Seconds into a clip of <paramref name="duration"/> to look at, evenly spaced, clear of the very end.</summary>
    public static double[] SampleTimes(double duration)
    {
        if (duration <= 0) return new[] { 0.0 };
        var count = Math.Clamp((int)Math.Ceiling(duration / FrameSpacing), MinFrames, MaxFrames);
        // Some containers cannot seek right up to the duration they report.
        var last = Math.Max(0, duration - 0.5);
        return Enumerable.Range(0, count)
            .Select(i => Math.Round(Math.Min(last, (i + 0.5) * duration / count), 3))
            .Distinct()
            .ToArray();
    }

    private List<DetectedFace> Detect((IFaceDetectorWithLandmarks Detector, IFaceEmbeddingsGenerator Embedder) models,
        Image<Rgb24> image, double? seconds, CancellationToken cancel)
    {
        IReadOnlyCollection<FaceDetectorResult> detected;
        lock (_detectGate) detected = models.Detector.DetectFaces(image);

        var faces = new List<DetectedFace>();
        foreach (var face in detected)
        {
            cancel.ThrowIfCancellationRequested();
            if ((face.Confidence ?? 0) < MinConfidence || face.Landmarks == null) continue;
            if (Math.Min(face.Box.Width, face.Box.Height) < MinFaceSide) continue;

            float[] embedding;
            using (var aligned = image.Clone())
            lock (_embedGate)
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

    private (IFaceDetectorWithLandmarks, IFaceEmbeddingsGenerator)? LoadModels()
    {
        try
        {
            // A couple of threads per model: enough to keep up with decoding, while
            // leaving the rest of the machine to whoever is using it.
            var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
            var detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(options);
            var embedder = FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator(options);
            _logger.LogInformation("Face recognition models loaded");
            return (detector, embedder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Face recognition is unavailable, models failed to load: {Msg}", ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_models.IsValueCreated && _models.Value is { } models)
        {
            (models.Detector as IDisposable)?.Dispose();
            (models.Embedder as IDisposable)?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
