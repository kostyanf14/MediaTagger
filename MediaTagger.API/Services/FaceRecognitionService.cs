using System.Numerics.Tensors;
using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

/// <summary>
/// Learns what each person looks like from the tags already in the library, then
/// points out photos where they appear but are not tagged.
///
/// Tags say who is in a photo, not which face is whose, so the training data is
/// worked out first: a photo with one face and one tagged person is a sure match;
/// the rest are resolved against those, a round at a time, never giving a face to
/// someone who is not tagged on that photo. People with no sure match at all are
/// found as the face that keeps recurring across the photos they are tagged on.
/// </summary>
public class FaceRecognitionService
{
    /// <summary>Similarity a face needs to be suggested as someone not tagged on the photo.</summary>
    private const float SuggestThreshold = 0.45f;
    /// <summary>Lower bar for picking which face is whose when the tags already narrow it down.</summary>
    private const float AssignThreshold = 0.40f;
    /// <summary>A suggestion must beat the runner-up by this much, or it is a coin toss.</summary>
    private const float MinMargin = 0.04f;
    /// <summary>A sure match that looks like none of the person's other faces is probably a stranger.</summary>
    private const float OutlierThreshold = 0.25f;
    /// <summary>Faces compared per person; spread across time so a child is known at every age.</summary>
    private const int MaxExemplars = 60;
    private const int TopK = 3;
    /// <summary>People known from fewer faces than this are not suggested anywhere — too easy to get wrong.</summary>
    private const int MinExemplarsToSuggest = 3;
    /// <summary>Photos a person must be tagged on before their face is guessed from the tags alone.</summary>
    private const int BootstrapMinPhotos = 3;
    /// <summary>How much more a placed face must resemble someone else before it is taken back.</summary>
    private const float ConflictMargin = 0.1f;
    /// <summary>
    /// Detector confidence below this means a profile, motion blur or a face half out of
    /// frame. Such faces all look alike to the embedding model, so they would only
    /// produce confident nonsense; they are kept in the store but not used.
    /// </summary>
    private const float MinDetectionScore = 0.72f;
    private const int AssignRounds = 3;
    /// <summary>While the scan is adding faces, a model this fresh is good enough.</summary>
    private static readonly TimeSpan StaleAllowance = TimeSpan.FromSeconds(20);

    private readonly FileSystemService _fs;
    private readonly FaceStore _store;
    private readonly FaceIndexer _indexer;
    private readonly TagsService _tags;

    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private Model? _model;
    private long _tagsVersion;

    public FaceRecognitionService(FileSystemService fs, FaceStore store, FaceIndexer indexer, TagsService tags)
    {
        _fs = fs;
        _store = store;
        _indexer = indexer;
        _tags = tags;
        _tags.FolderChanged += _ => Interlocked.Increment(ref _tagsVersion);
    }

    /// <summary>A file with faces: the usable ones, and how many were detected in all.</summary>
    private sealed record Entry(MediaFile File, StoredFace[] Faces, int Detected, int Width, int Height, HashSet<string> People);

    private sealed class Model
    {
        public required string Root { get; init; }
        public required (long Store, long Tags) Version { get; init; }
        public required DateTime BuiltAt { get; init; }
        /// <summary>Canonical spelling for each person, keyed case-insensitively.</summary>
        public required Dictionary<string, string> Names { get; init; }
        public required Dictionary<string, float[][]> Exemplars { get; init; }
        /// <summary>Face → person, for every face the tags (or the user) settled.</summary>
        public required Dictionary<long, string> Assigned { get; init; }
        public required List<FaceSuggestion> Suggestions { get; init; }
        public required List<LearnedPerson> Learned { get; init; }
        public required int FacesTotal { get; init; }
    }

    /// <summary>Suggestions for the whole library, grouped by person, best first.</summary>
    public async Task<FaceSuggestionsResponse> GetSuggestionsAsync(IReadOnlyList<string> roots, int perPerson)
    {
        var model = await GetModelAsync(roots);
        var groups = model.Suggestions
            .GroupBy(s => s.Person, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PersonSuggestions
            {
                Person = g.Key,
                Total = g.Count(),
                Items = g.OrderByDescending(s => s.Score).Take(perPerson).ToList()
            })
            .OrderByDescending(p => p.Total)
            .ToList();

        return new FaceSuggestionsResponse
        {
            People = groups,
            Learned = model.Learned,
            FacesTotal = model.FacesTotal
        };
    }

    /// <summary>Every face in one file, with who it is (or might be).</summary>
    public async Task<FileFacesResponse> GetFileFacesAsync(IReadOnlyList<string> roots, MediaFile file)
    {
        var faces = await _indexer.EnsureAnalyzedAsync(file);
        var response = new FileFacesResponse { Width = faces?.Width ?? 0, Height = faces?.Height ?? 0 };
        if (faces == null || faces.Faces.Length == 0) return response;

        var model = await GetModelAsync(roots);
        var tagged = new HashSet<string>(file.Tags.People, StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Settled faces first, so a suggestion never names someone already placed on another face.
        var results = new List<(StoredFace Face, FileFace Result, string? Best, float Score)>();
        foreach (var face in faces.Faces.Where(IsUsable).OrderByDescending(f => f.W * f.H))
        {
            var result = new FileFace { FaceId = face.Id, X = face.X, Y = face.Y, W = face.W, H = face.H, Time = face.Time };
            var label = _store.GetLabel(face.Id);
            if (label != null && tagged.Contains(label)) result.Person = label;
            else if (model.Assigned.TryGetValue(face.Id, out var assigned) && tagged.Contains(assigned)) result.Person = assigned;

            string? best = null;
            float score = 0;
            if (result.Person == null)
            {
                // A suggestion the user once accepted and then untagged counts as a no.
                (best, score, _) = Rank(model, face.Embedding,
                    p => !_store.IsRejected(face.Id, p) && !p.Equals(label, StringComparison.OrdinalIgnoreCase));
                // A tagged person matched well enough is simply recognised, not suggested.
                if (best != null && tagged.Contains(best) && score >= AssignThreshold && !claimed.Contains(best))
                {
                    result.Person = model.Names[best];
                    best = null;
                }
            }

            if (result.Person != null) claimed.Add(result.Person);
            results.Add((face, result, best, score));
        }

        foreach (var (_, result, best, score) in results)
        {
            if (result.Person == null && best != null && score >= SuggestThreshold
                && !tagged.Contains(best) && claimed.Add(best))
            {
                result.Suggestion = model.Names[best];
                result.Score = score;
            }
            response.Faces.Add(result);
        }

        return response;
    }

    // Faces found, labels and tags all feed the model; any change to them makes it stale.
    private (long Store, long Tags) CurrentVersion => (_store.Version, Interlocked.Read(ref _tagsVersion));

    private async Task<Model> GetModelAsync(IReadOnlyList<string> roots)
    {
        var scope = FileSystemService.Scope(roots);
        var version = CurrentVersion;
        var model = _model;
        if (IsUsable(model, scope, version)) return model!;

        await _buildGate.WaitAsync();
        try
        {
            // Someone else may have rebuilt it while this request waited.
            model = _model;
            version = CurrentVersion;
            if (IsUsable(model, scope, version)) return model!;

            model = await Task.Run(() => Build(roots, scope, version));
            _model = model;
            return model;
        }
        finally { _buildGate.Release(); }
    }

    private bool IsUsable(Model? model, string root, (long Store, long Tags) version)
    {
        if (model == null || !string.Equals(model.Root, root, StringComparison.OrdinalIgnoreCase)) return false;
        if (model.Version == version) return true;
        // Every analysed file changes the version; rebuilding on each would mean
        // rebuilding nonstop for the whole first scan.
        return _indexer.IsActive && DateTime.UtcNow - model.BuiltAt < StaleAllowance;
    }

    private Model Build(IReadOnlyList<string> roots, string scope, (long Store, long Tags) version)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();
        var facesTotal = 0;

        foreach (var file in _fs.Across(roots))
        {
            var faces = _store.TryGet(file.FilePath, file.Size, FaceIndexer.MtimeOf(file));
            var usable = faces?.Faces.Where(IsUsable).ToArray();
            if (faces == null || usable!.Length == 0) continue;

            var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in file.Tags.People)
            {
                var name = person.Trim();
                if (name.Length == 0) continue;
                names.TryAdd(name, name);
                people.Add(name);
            }
            entries.Add(new Entry(file, usable, faces.Faces.Length, faces.Width, faces.Height, people));
            facesTotal += usable.Length;
        }

        var assigned = new Dictionary<long, string>();
        var embeddings = entries.SelectMany(e => e.Faces).ToDictionary(f => f.Id, f => f.Embedding);
        var dates = entries.SelectMany(e => e.Faces.Select(f => (f.Id, Date: e.File.Tags.DateTaken ?? e.File.Modified)))
            .ToDictionary(x => x.Id, x => x.Date);

        // 1. What the user confirmed, as long as the photo is still tagged with that person.
        var confirmed = new HashSet<long>();
        foreach (var entry in entries)
            foreach (var face in entry.Faces)
                if (_store.GetLabel(face.Id) is { } label && entry.People.Contains(label))
                {
                    assigned[face.Id] = names[label];
                    confirmed.Add(face.Id);
                }

        // 2. One face, one tagged person: nothing to work out.
        foreach (var entry in entries)
            // A second, blurry face still makes it unclear which one was meant.
            if (entry.People.Count == 1 && entry.Detected == 1 && entry.Faces.Length == 1
                && !_store.IsRejected(entry.Faces[0].Id, entry.People.First()))
                assigned.TryAdd(entry.Faces[0].Id, names[entry.People.First()]);

        DropOutliers(assigned, confirmed, embeddings);

        // 3. Resolve the rest round by round, each round learning from the one before.
        for (var round = 0; round < AssignRounds; round++)
        {
            var exemplars = BuildExemplars(assigned, embeddings, dates);
            var added = Bootstrap(entries, assigned, exemplars, names);
            added += AssignWithinTags(entries, assigned, BuildExemplars(assigned, embeddings, dates));
            if (added == 0) break;
        }
        DropConflicts(assigned, confirmed, embeddings);

        var model = new Model
        {
            Root = scope,
            Version = version,
            BuiltAt = DateTime.UtcNow,
            Names = names,
            Exemplars = BuildExemplars(assigned, embeddings, dates),
            Assigned = assigned,
            Suggestions = new List<FaceSuggestion>(),
            Learned = Summarize(entries, assigned),
            FacesTotal = facesTotal
        };
        model.Suggestions.AddRange(FindSuggestions(entries, model));
        return model;
    }

    /// <summary>Who the model knows and from how many faces, each with their clearest face as a cover.</summary>
    private static List<LearnedPerson> Summarize(List<Entry> entries, Dictionary<long, string> assigned)
    {
        var learned = new Dictionary<string, LearnedPerson>(StringComparer.OrdinalIgnoreCase);
        var coverQuality = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            foreach (var face in entry.Faces)
            {
                if (!assigned.TryGetValue(face.Id, out var person)) continue;
                if (!learned.TryGetValue(person, out var summary))
                    learned[person] = summary = new LearnedPerson { Name = person };
                summary.Faces++;

                // Big, confidently detected faces make the most recognisable covers.
                var quality = face.Score * face.W * face.H * entry.Width * entry.Height;
                if (coverQuality.TryGetValue(person, out var current) && current >= quality) continue;
                coverQuality[person] = quality;
                summary.Cover = Reference(entry, face);
            }
        return learned.Values.OrderByDescending(p => p.Faces).ToList();
    }

    private static FaceSuggestion Reference(Entry entry, StoredFace face) => new()
    {
        FaceId = face.Id,
        FilePath = entry.File.FilePath,
        FileName = entry.File.FileName,
        Type = entry.File.Type,
        X = face.X, Y = face.Y, W = face.W, H = face.H,
        Time = face.Time,
        Width = entry.Width,
        Height = entry.Height
    };

    /// <summary>
    /// Assumed matches whose face looks like none of that person's other faces were
    /// most likely someone else in shot. Confirmed ones are trusted as they are.
    /// </summary>
    private static void DropOutliers(Dictionary<long, string> assigned, HashSet<long> confirmed,
        Dictionary<long, float[]> embeddings)
    {
        foreach (var group in assigned.GroupBy(a => a.Value).ToList())
        {
            var ids = group.Select(a => a.Key).ToList();
            if (ids.Count < 4) continue;

            foreach (var id in ids)
            {
                if (confirmed.Contains(id)) continue;
                var others = ids.Where(o => o != id).Select(o => embeddings[o]);
                if (TopKMean(embeddings[id], others) < OutlierThreshold) assigned.Remove(id);
            }
        }
    }

    /// <summary>
    /// For people still without a single known face: the face that keeps turning up in
    /// the photos they are tagged on — and not in tagged photos without them — is theirs.
    /// The second half matters: in a group shot tagged with five names, the one face that
    /// is visible usually belongs to someone who is tagged on plenty of other photos too.
    /// </summary>
    private static int Bootstrap(List<Entry> entries, Dictionary<long, string> assigned,
        Dictionary<string, float[][]> exemplars, Dictionary<string, string> names)
    {
        var added = 0;
        var unknown = entries.SelectMany(e => e.People).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !exemplars.ContainsKey(names[p])).ToList();

        foreach (var person in unknown)
        {
            var name = names[person];
            var photos = entries
                .Where(e => e.People.Contains(person))
                .Select(e => e.Faces.Where(f => !assigned.ContainsKey(f.Id)
                        && !LooksLikeSomeoneElse(f.Embedding, exemplars, name)).ToArray())
                .Where(faces => faces.Length > 0)
                .ToList();
            if (photos.Count < BootstrapMinPhotos) continue;
            // Spread the sample over the whole set; a few dozen photos say enough.
            if (photos.Count > 80) photos = photos.Where((_, i) => i % (photos.Count / 80 + 1) == 0).ToList();

            var candidates = new List<(StoredFace Face, int Photo, int Support)>();
            for (var i = 0; i < photos.Count; i++)
                foreach (var candidate in photos[i])
                {
                    var support = 0;
                    for (var j = 0; j < photos.Count; j++)
                        if (j != i && photos[j].Any(f => Similarity(candidate.Embedding, f.Embedding) >= SuggestThreshold))
                            support++;
                    candidates.Add((candidate, i, support));
                }

            // Has to recur in a fair share of the photos, not just turn up twice by chance.
            var needed = Math.Max(BootstrapMinPhotos - 1, (photos.Count - 1) / 3);
            var others = entries.Where(e => e.People.Count > 0 && !e.People.Contains(person)).ToList();
            var chosen = candidates
                .Where(c => c.Support >= needed)
                .OrderByDescending(c => c.Support)
                .Take(3)
                // Tagged photos without this person that show the same face: someone else's face.
                .Where(c => others.Count(e => e.Faces.Any(f => Similarity(c.Face.Embedding, f.Embedding) >= SuggestThreshold)) < c.Support)
                .Select(c => ((StoredFace Face, int Photo)?)(c.Face, c.Photo))
                .FirstOrDefault();
            if (chosen is not { } seed) continue;

            assigned[seed.Face.Id] = name;
            added++;
            for (var i = 0; i < photos.Count; i++)
            {
                // One face per person per photo, and the seed's photo already has its one.
                if (i == seed.Photo) continue;
                var match = photos[i]
                    .Select(f => (Face: f, Score: Similarity(seed.Face.Embedding, f.Embedding)))
                    .OrderByDescending(x => x.Score).First();
                if (match.Score >= SuggestThreshold && assigned.TryAdd(match.Face.Id, name)) added++;
            }
        }

        return added;
    }

    /// <summary>
    /// A face placed as one person that clearly looks more like another known person was
    /// placed wrongly — usually a group shot where the tags did not say who was visible.
    /// </summary>
    private static int DropConflicts(Dictionary<long, string> assigned, HashSet<long> confirmed,
        Dictionary<long, float[]> embeddings)
    {
        var byPerson = assigned.GroupBy(a => a.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Key).ToList(), StringComparer.OrdinalIgnoreCase);
        var wrong = new List<long>();

        foreach (var (id, person) in assigned)
        {
            if (confirmed.Contains(id)) continue;
            var own = TopKMean(embeddings[id], byPerson[person].Where(o => o != id).Select(o => embeddings[o]));
            foreach (var (other, ids) in byPerson)
            {
                if (other.Equals(person, StringComparison.OrdinalIgnoreCase)) continue;
                var score = TopKMean(embeddings[id], ids.Select(o => embeddings[o]));
                if (score >= SuggestThreshold && score > own + ConflictMargin) { wrong.Add(id); break; }
            }
        }

        foreach (var id in wrong) assigned.Remove(id);
        return wrong.Count;
    }

    private static bool LooksLikeSomeoneElse(float[] embedding, Dictionary<string, float[][]> exemplars, string person)
    {
        foreach (var (other, faces) in exemplars)
            if (!other.Equals(person, StringComparison.OrdinalIgnoreCase) && TopKMean(embedding, faces) >= SuggestThreshold)
                return true;
        return false;
    }

    /// <summary>
    /// Pairs the open faces of a photo with the tagged people not yet placed on it,
    /// best match first, one face per person.
    /// </summary>
    private int AssignWithinTags(List<Entry> entries, Dictionary<long, string> assigned,
        Dictionary<string, float[][]> exemplars)
    {
        var added = 0;
        foreach (var entry in entries)
        {
            var placed = new HashSet<string>(entry.Faces
                .Where(f => assigned.ContainsKey(f.Id)).Select(f => assigned[f.Id]), StringComparer.OrdinalIgnoreCase);
            var open = entry.Faces.Where(f => !assigned.ContainsKey(f.Id)).ToList();
            var people = entry.People.Where(p => !placed.Contains(p) && exemplars.ContainsKey(p)).ToList();
            if (open.Count == 0 || people.Count == 0) continue;

            var pairs = new List<(StoredFace Face, string Person, float Score)>();
            foreach (var face in open)
                foreach (var person in people)
                {
                    if (_store.IsRejected(face.Id, person)) continue;
                    var score = TopKMean(face.Embedding, exemplars[person]);
                    if (score >= AssignThreshold) pairs.Add((face, person, score));
                }

            var usedFaces = new HashSet<long>();
            foreach (var (face, person, _) in pairs.OrderByDescending(p => p.Score))
            {
                if (usedFaces.Contains(face.Id) || placed.Contains(person)) continue;
                usedFaces.Add(face.Id);
                placed.Add(person);
                assigned[face.Id] = exemplars.Keys.First(k => k.Equals(person, StringComparison.OrdinalIgnoreCase));
                added++;
            }
        }
        return added;
    }

    private IEnumerable<FaceSuggestion> FindSuggestions(List<Entry> entries, Model model)
    {
        if (model.Exemplars.Count == 0) return Enumerable.Empty<FaceSuggestion>();

        var found = new System.Collections.Concurrent.ConcurrentBag<FaceSuggestion>();
        Parallel.ForEach(entries, entry =>
        {
            // People already on this photo, by tag or by a face that was placed.
            var present = new HashSet<string>(entry.People, StringComparer.OrdinalIgnoreCase);
            var best = new Dictionary<string, FaceSuggestion>(StringComparer.OrdinalIgnoreCase);

            foreach (var face in entry.Faces)
            {
                if (model.Assigned.ContainsKey(face.Id)) continue;

                // A suggestion the user once accepted and then untagged counts as a no.
                var withdrawn = _store.GetLabel(face.Id);
                var (person, score, margin) = Rank(model, face.Embedding,
                    p => !_store.IsRejected(face.Id, p) && !p.Equals(withdrawn, StringComparison.OrdinalIgnoreCase));

                if (person == null || score < SuggestThreshold || margin < MinMargin || present.Contains(person)) continue;
                if (best.TryGetValue(person, out var existing) && existing.Score >= score) continue;

                var suggestion = Reference(entry, face);
                suggestion.Person = model.Names[person];
                suggestion.Score = score;
                best[person] = suggestion;
            }

            foreach (var suggestion in best.Values) found.Add(suggestion);
        });
        return found;
    }

    /// <summary>The most likely person for a face, how sure, and how far ahead of the next one.</summary>
    private static (string? Person, float Score, float Margin) Rank(Model model, float[] embedding, Func<string, bool> allowed)
    {
        string? best = null;
        float first = float.MinValue, second = float.MinValue;
        foreach (var (person, faces) in model.Exemplars)
        {
            // Someone this face was ruled out as does not compete at all, and someone
            // known from a face or two is too uncertain to name.
            if (faces.Length < MinExemplarsToSuggest || !allowed(person)) continue;
            var score = TopKMean(embedding, faces);
            if (score > first) { second = first; first = score; best = person; }
            else if (score > second) second = score;
        }
        if (best == null) return (null, 0, 0);
        return (best, first, second == float.MinValue ? first : first - second);
    }

    /// <summary>
    /// Up to <see cref="MaxExemplars"/> faces per person, evenly spread over the dates
    /// they were taken, so the comparison covers how someone looked over the years.
    /// </summary>
    private static Dictionary<string, float[][]> BuildExemplars(Dictionary<long, string> assigned,
        Dictionary<long, float[]> embeddings, Dictionary<long, DateTime> dates)
    {
        var result = new Dictionary<string, float[][]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in assigned.GroupBy(a => a.Value, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(a => a.Key).OrderBy(id => dates[id]).ToList();
            if (ids.Count > MaxExemplars)
            {
                var step = (double)ids.Count / MaxExemplars;
                ids = Enumerable.Range(0, MaxExemplars).Select(i => ids[(int)(i * step)]).ToList();
            }
            result[group.Key] = ids.Select(id => embeddings[id]).ToArray();
        }
        return result;
    }

    /// <summary>Mean of the best few similarities — robust to one odd photo either way.</summary>
    private static float TopKMean(float[] embedding, IEnumerable<float[]> faces)
    {
        Span<float> top = stackalloc float[TopK];
        top.Fill(float.MinValue);
        var count = 0;
        foreach (var face in faces)
        {
            var score = Similarity(embedding, face);
            count++;
            // Keep the K best, smallest at the end.
            for (var i = 0; i < TopK; i++)
            {
                if (score <= top[i]) continue;
                for (var j = TopK - 1; j > i; j--) top[j] = top[j - 1];
                top[i] = score;
                break;
            }
        }
        if (count == 0) return float.MinValue;

        var used = Math.Min(count, TopK);
        var sum = 0f;
        for (var i = 0; i < used; i++) sum += top[i];
        return sum / used;
    }

    private static bool IsUsable(StoredFace face) => face.Score >= MinDetectionScore;

    private static float Similarity(float[] a, float[] b) => TensorPrimitives.Dot<float>(a, b);
}
