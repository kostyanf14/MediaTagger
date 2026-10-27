using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaTagger.API.Tests;

public class VideoFacesTests
{
    private const int Dimensions = 8;

    /// <summary>A unit vector mostly along <paramref name="axis"/>, nudged by <paramref name="jitter"/> along the next one.</summary>
    private static float[] Person(int axis, float jitter = 0)
    {
        var v = new float[Dimensions];
        v[axis] = 1;
        v[(axis + 1) % Dimensions] = jitter;
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }

    private static DetectedFace Face(float[] embedding, double time, float size = 0.1f, float score = 0.9f) =>
        new(0.1f, 0.1f, size, size, score, embedding, time);

    [Fact]
    public void Same_person_across_frames_becomes_one_face()
    {
        var frames = new List<IReadOnlyList<DetectedFace>>
        {
            new[] { Face(Person(0), 1) },
            new[] { Face(Person(0, 0.2f), 3, size: 0.2f), Face(Person(4), 3) },
            new[] { Face(Person(0, 0.1f), 5), Face(Person(4, 0.1f), 5) },
        };

        var merged = VideoFaceTracks.Merge(frames);

        Assert.Equal(2, merged.Count);
        // The biggest sighting is the one the UI crops, frame and all.
        var first = merged.Single(f => f.Embedding[0] > 0.9f);
        Assert.Equal(3, first.Time);
        Assert.Equal(0.2f, first.W);
        // The embedding stays unit length, so dot products remain cosine similarities.
        Assert.Equal(1f, MathF.Sqrt(first.Embedding.Sum(x => x * x)), 3);
    }

    [Fact]
    public void Two_faces_in_one_frame_are_never_merged()
    {
        // Twins, as far as the model can tell — but both are on screen at once.
        var frames = new List<IReadOnlyList<DetectedFace>>
        {
            new[] { Face(Person(0), 1), Face(Person(0, 0.05f), 1) },
            new[] { Face(Person(0), 2) },
        };

        Assert.Equal(2, VideoFaceTracks.Merge(frames).Count);
    }

    [Fact]
    public void A_person_split_early_is_joined_once_their_average_matches()
    {
        // Frame 2 alone is too far from frame 1's sighting to link; on average they agree.
        var a = Person(0);
        var b = new float[Dimensions]; b[0] = 0.3f; b[1] = 0.954f;
        var c = new float[Dimensions]; c[0] = 0.8f; c[1] = 0.6f;
        var frames = new List<IReadOnlyList<DetectedFace>>
        {
            new[] { Face(a, 1) },
            new[] { Face(b, 2), Face(Person(4), 2) },
            new[] { Face(c, 3), Face(Person(4), 3) },
        };

        Assert.Equal(2, VideoFaceTracks.Merge(frames).Count);
    }

    [Fact]
    public void Blurry_sightings_do_not_shape_the_embedding()
    {
        var frames = new List<IReadOnlyList<DetectedFace>>
        {
            new[] { Face(Person(0), 1, size: 0.1f) },
            new[] { Face(Person(0, 0.8f), 2, size: 0.3f, score: 0.62f) },
        };

        var face = Assert.Single(VideoFaceTracks.Merge(frames));
        Assert.Equal(1, face.Time);
        Assert.Equal(Person(0), face.Embedding);
    }

    [Fact]
    public void Frames_cover_the_whole_clip()
    {
        var times = FaceAnalyzer.SampleTimes(60, VideoFaceMode.Frames);
        Assert.Equal(16, times.Length);
        Assert.True(times[0] < 3);
        Assert.True(times[^1] > 55 && times[^1] <= 59.5);

        Assert.Equal(10, FaceAnalyzer.SampleTimes(20, VideoFaceMode.Frames).Length);
        Assert.Equal(20, FaceAnalyzer.SampleTimes(20, VideoFaceMode.Detailed).Length);
        Assert.Equal(48, FaceAnalyzer.SampleTimes(600, VideoFaceMode.Detailed).Length);
        Assert.Equal(3, FaceAnalyzer.SampleTimes(1.5, VideoFaceMode.Frames).Length);
        Assert.Equal(new[] { 0.0 }, FaceAnalyzer.SampleTimes(0, VideoFaceMode.Frames));
    }

    [Fact]
    public void Reanalysis_keeps_what_the_user_said_about_a_face()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-faces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "clip.mp4");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["MediaTagger:CacheRoot"] = root })
                .Build();
            var locator = new CacheLocator(new HostingEnvironment { ContentRootPath = root }, config,
                NullLogger<CacheLocator>.Instance);

            using (var store = new FaceStore(locator, NullLogger<FaceStore>.Instance))
            {
                var before = store.Save(file, 10, 1, 1, 640, 480,
                    new[] { new DetectedFace(0, 0, 0.1f, 0.1f, 0.9f, Person(0)), new DetectedFace(0.5f, 0, 0.1f, 0.1f, 0.9f, Person(4)) });
                store.SetLabel(before.Faces[0].Id, "Анна");
                store.Reject(before.Faces[1].Id, "Анна");

                var after = store.Save(file, 10, 1, 2, 640, 480,
                    new[] { Face(Person(4, 0.1f), 7), Face(Person(0, 0.1f), 2), Face(Person(2), 9) });

                Assert.Equal(2, store.TryGet(file, 10, 1)!.Method);
                Assert.Equal("Анна", store.GetLabel(after.Faces[1].Id));
                Assert.True(store.IsRejected(after.Faces[0].Id, "Анна"));
                Assert.Null(store.GetLabel(after.Faces[2].Id));
                Assert.Null(store.GetLabel(before.Faces[0].Id));
            }

            // And it all survives a restart, frame times included.
            using (var reopened = new FaceStore(locator, NullLogger<FaceStore>.Instance))
            {
                var faces = reopened.TryGet(file, 10, 1)!.Faces;
                Assert.Equal(new double?[] { 7, 2, 9 }, faces.Select(f => f.Time));
                Assert.Equal("Анна", reopened.GetLabel(faces[1].Id));
                Assert.True(reopened.IsRejected(faces[0].Id, "Анна"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
