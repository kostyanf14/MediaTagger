using System.Collections.Concurrent;
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

    /// <summary>Side of a grid cell in degrees: about 1.1 km of latitude, as far as any place reaches.</summary>
    private const double CellDegrees = 0.01;
    private const double MetersPerDegree = 111_320;

    private readonly FileSystemService _fs;
    private readonly object _gate = new();
    private Model? _model;
    private long _tagsVersion;

    // What the last write to each folder said about places and coordinates. A write that
    // changes neither - a person, an album, a note - leaves the places as they were.
    private readonly ConcurrentDictionary<string, int> _placesWritten = new(StringComparer.OrdinalIgnoreCase);

    public PlaceSuggestionService(FileSystemService fs, TagsService tags)
    {
        _fs = fs;
        tags.FolderWritten += OnFolderWritten;
    }

    private void OnFolderWritten(string folder, TagsFile written)
    {
        var hash = new HashCode();
        foreach (var (name, tags) in written.Files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(name, StringComparer.OrdinalIgnoreCase);
            hash.Add(tags.Place?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            hash.Add(tags.Coordinates?.Lat);
            hash.Add(tags.Coordinates?.Lng);
        }
        var key = Path.TrimEndingDirectorySeparator(folder);
        var value = hash.ToHashCode();
        if (_placesWritten.TryGetValue(key, out var before) && before == value) return;
        _placesWritten[key] = value;
        Interlocked.Increment(ref _tagsVersion);
    }

    private sealed record Point(double Lat, double Lng);

    /// <summary>One photo of a place, filed under the grid cell it falls in.</summary>
    private readonly record struct Spot(int Place, Point At);

    private sealed class Place
    {
        public required string Name { get; init; }
        public required List<Point> Points { get; init; }
        /// <summary>Gaussian width of the place, metres.</summary>
        public double Spread { get; set; }
        /// <summary>Beyond this distance from its nearest photo the place is not considered.</summary>
        public double Reach { get; set; }
    }

    private sealed class Model
    {
        public required string Root { get; init; }
        public required long Version { get; init; }
        public required DateTime BuiltAt { get; init; }
        public required List<Place> Places { get; init; }
        public required List<MediaFile> Unplaced { get; init; }
        public required Dictionary<long, List<Spot>> Grid { get; init; }
        // Worked out on first use and kept: the answer is the same until the model is.
        public PlaceSuggestionsResponse? Suggestions { get; set; }
    }

    private static long CellKey(int row, int column) => ((long)row << 32) ^ (uint)column;

    private static (int Row, int Column) CellOf(double lat, double lng) =>
        ((int)Math.Floor(lat / CellDegrees), (int)Math.Floor(lng / CellDegrees));

    /// <summary>Every geotagged photo without a place that has a likely one, grouped by that place.</summary>
    public PlaceSuggestionsResponse GetSuggestions(IReadOnlyList<string> roots)
    {
        var model = GetModel(roots);
        lock (_gate) return model.Suggestions ??= BuildSuggestions(model);
    }

    private static PlaceSuggestionsResponse BuildSuggestions(Model model)
    {
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
    public List<PlaceCandidate> GetNearby(IReadOnlyList<string> roots, double lat, double lng) =>
        Rank(GetModel(roots), new Point(lat, lng));

    private static List<PlaceCandidate> Rank(Model model, Point at)
    {
        // No place reaches further than MaxReachMeters, so only the cells that radius touches matter.
        var dLat = MaxReachMeters / MetersPerDegree;
        var dLng = dLat / Math.Max(0.05, Math.Cos(Math.Min(Math.Abs(at.Lat), 85) * Math.PI / 180));
        var (rowFrom, columnFrom) = CellOf(at.Lat - dLat, at.Lng - dLng);
        var (rowTo, columnTo) = CellOf(at.Lat + dLat, at.Lng + dLng);

        var found = new Dictionary<int, (double Nearest, double Score, int Support)>();
        for (var row = rowFrom; row <= rowTo; row++)
            for (var column = columnFrom; column <= columnTo; column++)
            {
                if (!model.Grid.TryGetValue(CellKey(row, column), out var spots)) continue;
                foreach (var spot in spots)
                {
                    var place = model.Places[spot.Place];
                    var d = Distance(at, spot.At);
                    if (d > place.Reach) continue;
                    found.TryGetValue(spot.Place, out var so);
                    var nearest = so.Support == 0 ? d : Math.Min(so.Nearest, d);
                    // Every photo of the place close by is a vote, worth less the further away it is.
                    found[spot.Place] = (nearest, so.Score + Math.Exp(-0.5 * (d / place.Spread) * (d / place.Spread)),
                        so.Support + 1);
                }
            }

        var scored = new List<PlaceCandidate>();
        var total = 0.0;
        // In the order the places were built, so equal scores come out the same every time.
        foreach (var (index, hit) in found.OrderBy(kv => kv.Key))
        {
            if (hit.Support == 0 || hit.Score < 1e-6) continue;
            total += hit.Score;
            scored.Add(new PlaceCandidate
            {
                Place = model.Places[index].Name, Confidence = hit.Score,
                Distance = Math.Round(hit.Nearest), Support = hit.Support
            });
        }

        foreach (var candidate in scored)
        {
            candidate.Confidence = Math.Round(candidate.Confidence / total, 3);
            candidate.Confident = candidate.Confidence >= ConfidentShare && candidate.Distance <= ConfidentDistanceMeters;
        }
        return scored.OrderByDescending(c => c.Confidence).Take(3).ToList();
    }

    private Model GetModel(IReadOnlyList<string> roots)
    {
        var scope = FileSystemService.Scope(roots);
        var version = Interlocked.Read(ref _tagsVersion);
        lock (_gate)
        {
            var model = _model;
            if (model != null && model.Version == version && DateTime.UtcNow - model.BuiltAt < MaxModelAge
                && string.Equals(model.Root, scope, StringComparison.Ordinal))
                return model;

            _model = model = Build(roots, scope, version);
            return model;
        }
    }

    private Model Build(IReadOnlyList<string> roots, string scope, long version)
    {
        var points = new Dictionary<string, List<Point>>(StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var unplaced = new List<MediaFile>();

        foreach (var file in _fs.Across(roots))
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

        var grid = new Dictionary<long, List<Spot>>();
        for (var i = 0; i < places.Count; i++)
            foreach (var point in places[i].Points)
            {
                var (row, column) = CellOf(point.Lat, point.Lng);
                var key = CellKey(row, column);
                if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<Spot>();
                cell.Add(new Spot(i, point));
            }

        return new Model
        {
            Root = scope, Version = version, BuiltAt = DateTime.UtcNow,
            Places = places, Unplaced = unplaced, Grid = grid
        };
    }

    /// <summary>Metres between two points. Flat-earth is plenty at the scale of "which café".</summary>
    private static double Distance(Point a, Point b)
    {
        var dLat = (a.Lat - b.Lat) * MetersPerDegree;
        var dLng = (a.Lng - b.Lng) * MetersPerDegree * Math.Cos((a.Lat + b.Lat) * Math.PI / 360);
        return Math.Sqrt(dLat * dLat + dLng * dLng);
    }
}
