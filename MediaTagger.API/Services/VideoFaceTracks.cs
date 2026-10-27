using System.Numerics.Tensors;

namespace MediaTagger.API.Services;

/// <summary>
/// Turns the faces seen in many frames of one video into one face per person. The rest
/// of face recognition treats a file as "these people are in it", so the same person
/// seen in twenty frames has to come out as one face, not twenty.
/// </summary>
public static class VideoFaceTracks
{
    /// <summary>
    /// Two sightings at least this alike are the same person. A face turned away or
    /// looking down can score this low against its own frontal view; different people
    /// in one clip stay around 0.3 at most, and two faces on screen together are kept
    /// apart regardless.
    /// </summary>
    public const float SameFaceThreshold = 0.40f;
    /// <summary>
    /// Sightings detected this confidently are sharp and frontal enough to describe the
    /// person; blurrier ones still join a track but do not shape its embedding.
    /// </summary>
    public const float ClearScore = 0.72f;

    /// <summary>One face per person across all <paramref name="frames"/>, each frame's faces in one list.</summary>
    public static List<DetectedFace> Merge(IEnumerable<IReadOnlyList<DetectedFace>> frames)
    {
        var tracks = new List<List<DetectedFace>>();
        foreach (var frame in frames)
        {
            // Two faces in one frame are two people: each track takes at most one face
            // per frame, and each face continues at most one track.
            var pairs = new List<(int Face, int Track, float Score)>();
            for (var f = 0; f < frame.Count; f++)
                for (var t = 0; t < tracks.Count; t++)
                {
                    // The closest sighting rather than the average, so a person turning
                    // their head over the clip still links up step by step.
                    var score = tracks[t].Max(seen => Similarity(seen.Embedding, frame[f].Embedding));
                    if (score >= SameFaceThreshold) pairs.Add((f, t, score));
                }

            var usedFaces = new HashSet<int>();
            var usedTracks = new HashSet<int>();
            foreach (var (f, t, _) in pairs.OrderByDescending(p => p.Score))
            {
                if (usedFaces.Contains(f) || usedTracks.Contains(t)) continue;
                usedFaces.Add(f);
                usedTracks.Add(t);
                tracks[t].Add(frame[f]);
            }
            for (var f = 0; f < frame.Count; f++)
                if (!usedFaces.Contains(f)) tracks.Add(new List<DetectedFace> { frame[f] });
        }

        JoinSplitTracks(tracks);
        return tracks.Select(Summarize).ToList();
    }

    /// <summary>
    /// Frame by frame, a person can start a second track before enough of them is seen to
    /// link it to the first. Averaged over all their sightings the two do match; joined
    /// here, most alike first, as long as they were never on screen together.
    /// </summary>
    private static void JoinSplitTracks(List<List<DetectedFace>> tracks)
    {
        while (true)
        {
            var embeddings = tracks.Select(t => Summarize(t).Embedding).ToList();
            (int A, int B, float Score)? best = null;
            for (var a = 0; a < tracks.Count; a++)
                for (var b = a + 1; b < tracks.Count; b++)
                {
                    var score = Similarity(embeddings[a], embeddings[b]);
                    if (score < SameFaceThreshold || (best != null && score <= best.Value.Score)) continue;
                    if (tracks[a].Any(x => tracks[b].Any(y => x.Time == y.Time))) continue;
                    best = (a, b, score);
                }

            if (best is not { } join) return;
            tracks[join.A].AddRange(tracks[join.B]);
            tracks.RemoveAt(join.B);
        }
    }

    /// <summary>
    /// The person's biggest clear sighting stands for the track — that is the box the UI
    /// crops — while the embedding is the average of every clear one, which recognises
    /// someone better than any single frame does.
    /// </summary>
    private static DetectedFace Summarize(List<DetectedFace> sightings)
    {
        var clear = sightings.Where(s => s.Score >= ClearScore).ToList();
        if (clear.Count == 0) clear = sightings;

        var best = clear.MaxBy(s => s.W * s.H)!;
        var embedding = new float[best.Embedding.Length];
        foreach (var sighting in clear) TensorPrimitives.Add(embedding, sighting.Embedding, embedding);
        var norm = MathF.Sqrt(TensorPrimitives.Dot<float>(embedding, embedding));
        if (norm > 0) TensorPrimitives.Divide(embedding, norm, embedding);

        return best with { Embedding = embedding };
    }

    private static float Similarity(float[] a, float[] b) => TensorPrimitives.Dot<float>(a, b);
}
