using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

/// <summary>
/// Names the place of a geotagged photo from the places already written on photos
/// taken nearby. Each place is treated as its own size: a flat is a few dozen metres
/// of GPS jitter, a park is a few hundred, so "nearby" is measured against how spread
/// out that place's own photos are rather than one fixed radius.
/// </summary>
public class PlaceSuggestionService
{
    /// <summary>Phone GPS wanders this much even standing still, so no place is treated as smaller.</summary>
    private const double MinSpreadMeters = 40;
    /// <summary>How far past its own spread a place still reaches.</summary>
    private const double ReachFactor = 3;
    private const double MinReachMeters = 150;
    private const double MaxReachMeters = 1000;
    /// <summary>Share of the nearby evidence the winner needs before it counts as confident.</summary>
    private const double ConfidentShare = 0.75;
    /// <summary>
    /// A confident answer also needs a photo of that place about this close. A few street
    /// photos spread along a kilometre would otherwise claim the whole neighbourhood.
    /// </summary>
    private const double ConfidentDistanceMeters = 100;
    /// <summary>External edits to tag files do not bump the tags version, so a model is never trusted for longer.</summary>
    private static readonly TimeSpan MaxModelAge = TimeSpan.FromMinutes(1);

    private readonly FileSystemService _fs;
    private readonly object _gate = new();
    private Model? _model;
    private long _tagsVersion;

    public PlaceSuggestionService(FileSystemService fs, TagsService tags)
    {
        _fs = fs;
        tags.FolderChanged += _ => Interlocked.Increment(ref _tagsVersion);
    }

    private sealed record Point(double Lat, double Lng);

    private sealed class Place
    {
        public required string Name { get; init; }
        public required List<Point> Points { get; init; }
        /// <summary>Gaussian width of the place, metres.</summary>
        public double Spread { get; set; }
        /// <summary>Beyond this distance from its nearest photo the place is not considered.</summary>
        public double Reach { get; set; }
    }

    private sealed record Model(string Root, long Version, DateTime BuiltAt, List<Place> Places, List<MediaFile> Unplaced);

    /// <summary>Every geotagged photo without a place that has a likely one, grouped by that place.</summary>
    public PlaceSuggestionsResponse GetSuggestions(string root)
    {
        var model = GetModel(root);
        var suggestions = new List<PlaceSuggestion>();

        foreach (var file in model.Unplaced)
        {
            var c = file.Tags.Coordinates!;
            var ranked = Rank(model, new Point(c.Lat, c.Lng));
            if (ranked.Count == 0) continue;

            var best = ranked[0];
            suggestions.Add(new PlaceSuggestion
            {
                FilePath = file.FilePath,
                FileName = file.FileName,
                Type = file.Type,
                Place = best.Place,
                Confidence = best.Confidence,
                Distance = best.Distance,
                Confident = best.Confident,
                Alternatives = ranked.Skip(1).Select(r => r.Place).ToList()
            });
        }

        return new PlaceSuggestionsResponse
        {
            KnownPlaces = model.Places.Count,
            Unplaced = model.Unplaced.Count,
            Places = suggestions
                .GroupBy(s => s.Place, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PlaceSuggestionGroup
                {
                    Place = g.Key,
                    // The ones safe to apply first, then the closest — distance is what a person checks.
                    Items = g.OrderByDescending(s => s.Confident).ThenBy(s => s.Distance).ToList()
                })
                .OrderByDescending(g => g.Items.Count)
                .ToList()
        };
    }

    /// <summary>Likely places for one spot, best first — for a photo being edited, whose pin may be new.</summary>
    public List<PlaceCandidate> GetNearby(string root, double lat, double lng) =>
        Rank(GetModel(root), new Point(lat, lng));

    private static List<PlaceCandidate> Rank(Model model, Point at)
    {
        var scored = new List<PlaceCandidate>();
        var total = 0.0;

        foreach (var place in model.Places)
        {
            var nearest = double.MaxValue;
            var score = 0.0;
            var support = 0;
            foreach (var point in place.Points)
            {
                var d = Distance(at, point);
                if (d < nearest) nearest = d;
                if (d > place.Reach) continue;
                // Every photo of the place close by is a vote, worth less the further away it is.
                score += Math.Exp(-0.5 * (d / place.Spread) * (d / place.Spread));
                support++;
            }
            if (support == 0 || score < 1e-6) continue;

            total += score;
            scored.Add(new PlaceCandidate { Place = place.Name, Confidence = score, Distance = Math.Round(nearest), Support = support });
        }

        foreach (var candidate in scored)
        {
            candidate.Confidence = Math.Round(candidate.Confidence / total, 3);
            candidate.Confident = candidate.Confidence >= ConfidentShare && candidate.Distance <= ConfidentDistanceMeters;
        }
        return scored.OrderByDescending(c => c.Confidence).Take(3).ToList();
    }

    private Model GetModel(string root)
    {
        var version = Interlocked.Read(ref _tagsVersion);
        lock (_gate)
        {
            var model = _model;
            if (model != null && model.Version == version && DateTime.UtcNow - model.BuiltAt < MaxModelAge
                && string.Equals(model.Root, root, StringComparison.OrdinalIgnoreCase))
                return model;

            _model = model = Build(root, version);
            return model;
        }
    }

    private Model Build(string root, long version)
    {
        var points = new Dictionary<string, List<Point>>(StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var unplaced = new List<MediaFile>();

        foreach (var file in _fs.GetMediaFiles(root, recursive: true))
        {
            var c = file.Tags.Coordinates;
            if (c == null || (c.Lat == 0 && c.Lng == 0)) continue;

            var name = file.Tags.Place?.Trim() ?? "";
            if (name.Length == 0) { unplaced.Add(file); continue; }

            if (!points.TryGetValue(name, out var list)) points[name] = list = new List<Point>();
            list.Add(new Point(c.Lat, c.Lng));

            // The same place typed in different case is one place, shown the way it is written most.
            if (!spellings.TryGetValue(name, out var counts)) spellings[name] = counts = new Dictionary<string, int>();
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        var places = points.Select(kv =>
        {
            var place = new Place
            {
                Name = spellings[kv.Key].OrderByDescending(s => s.Value).First().Key,
                Points = kv.Value
            };
            // The middle 80% of its photos describe the place; the rest is GPS that wandered off.
            var centre = new Point(kv.Value.Average(p => p.Lat), kv.Value.Average(p => p.Lng));
            var distances = kv.Value.Select(p => Distance(centre, p)).OrderBy(d => d).ToList();
            var spread = distances[(int)((distances.Count - 1) * 0.8)];
            place.Spread = Math.Max(MinSpreadMeters, spread);
            place.Reach = Math.Clamp(place.Spread * ReachFactor, MinReachMeters, MaxReachMeters);
            return place;
        }).ToList();

        return new Model(root, version, DateTime.UtcNow, places, unplaced);
    }

    /// <summary>Metres between two points. Flat-earth is plenty at the scale of "which café".</summary>
    private static double Distance(Point a, Point b)
    {
        const double metersPerDegree = 111_320;
        var dLat = (a.Lat - b.Lat) * metersPerDegree;
        var dLng = (a.Lng - b.Lng) * metersPerDegree * Math.Cos((a.Lat + b.Lat) * Math.PI / 360);
        return Math.Sqrt(dLat * dLat + dLng * dLng);
    }
}
