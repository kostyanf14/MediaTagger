using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaTagger.API.Tests;

/// <summary>A real, tiny library on disk, wired the way the API wires it.</summary>
internal sealed class LibraryWorld : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mt-scale-" + Guid.NewGuid().ToString("N"));
    public string Library { get; }
    public TagsService Tags { get; } = new();
    public ExifCache Exif { get; }
    public LibraryIndex Index { get; }
    public FileSystemService Files { get; }

    public LibraryWorld()
    {
        Library = Path.Combine(_root, "library");
        Directory.CreateDirectory(Library);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaTagger:CacheRoot"] = Path.Combine(_root, "cache")
        }).Build();
        var locator = new CacheLocator(new WorldEnv(_root), config, NullLogger<CacheLocator>.Instance);
        Exif = new ExifCache(locator, NullLogger<ExifCache>.Instance);
        Index = new LibraryIndex(Tags, new ExifService(), Exif, new ScanProgress(), NullLogger<LibraryIndex>.Instance);
        Files = new FileSystemService(Index);
    }

    /// <summary>Creates the photos and writes their tags in one folder.</summary>
    public string Folder(string name, IEnumerable<(string File, FileTags Tags)> photos)
    {
        var dir = Path.Combine(Library, name);
        Directory.CreateDirectory(dir);
        var file = new TagsFile();
        foreach (var (fileName, tags) in photos)
        {
            File.WriteAllBytes(Path.Combine(dir, fileName), new byte[] { 1 });
            file.Files[fileName] = tags;
        }
        Tags.WriteTagsFile(dir, file);
        return dir;
    }

    public void Dispose()
    {
        Index.Dispose();
        Exif.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a watcher may still hold a file */ }
    }

    private sealed class WorldEnv : IHostEnvironment
    {
        public WorldEnv(string root) { ContentRootPath = root; }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MediaTagger";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public class PlaceGridTests
{
    // The ranking as it was before the grid: every point of every place, for every question.
    private static List<(string Place, double Confidence, double Distance)> BruteForce(
        List<(string Place, double Lat, double Lng)> placed, double lat, double lng)
    {
        static double Dist(double lat1, double lng1, double lat2, double lng2)
        {
            var dLat = (lat1 - lat2) * 111_320;
            var dLng = (lng1 - lng2) * 111_320 * Math.Cos((lat1 + lat2) * Math.PI / 360);
            return Math.Sqrt(dLat * dLat + dLng * dLng);
        }

        var scored = new List<(string Place, double Score, double Nearest)>();
        foreach (var group in placed.GroupBy(p => p.Place))
        {
            var points = group.ToList();
            var centreLat = points.Average(p => p.Lat);
            var centreLng = points.Average(p => p.Lng);
            var spreads = points.Select(p => Dist(centreLat, centreLng, p.Lat, p.Lng)).OrderBy(d => d).ToList();
            var spread = Math.Max(40, spreads[(int)((spreads.Count - 1) * 0.8)]);
            var reach = Math.Clamp(spread * 3, 150, 1000);

            var nearest = double.MaxValue;
            var score = 0.0;
            var support = 0;
            foreach (var p in points)
            {
                var d = Dist(lat, lng, p.Lat, p.Lng);
                nearest = Math.Min(nearest, d);
                if (d > reach) continue;
                score += Math.Exp(-0.5 * (d / spread) * (d / spread));
                support++;
            }
            if (support == 0 || score < 1e-6) continue;
            scored.Add((group.Key, score, Math.Round(nearest)));
        }

        var total = scored.Sum(s => s.Score);
        return scored.Select(s => (s.Place, Math.Round(s.Score / total, 3), s.Nearest))
            .OrderByDescending(s => s.Item2).Take(3).ToList();
    }

    [Fact]
    public void Looking_only_at_nearby_cells_gives_the_answers_of_checking_every_photo()
    {
        using var world = new LibraryWorld();
        var random = new Random(42);
        var placed = new List<(string Place, double Lat, double Lng)>();
        var centres = new[]
        {
            ("Дім", 50.4501, 30.5234), ("Офіс", 50.4520, 30.5290), ("Парк", 50.4700, 30.5000),
            ("Дача", 50.1000, 30.2000), ("Північ", 70.2000, 25.7000), ("Поряд", 50.4503, 30.5236)
        };
        var photos = new List<(string File, FileTags Tags)>();
        var n = 0;
        foreach (var (name, lat, lng) in centres)
            for (var i = 0; i < 40; i++)
            {
                // Jitter of a few hundred metres, in degrees that mean the same distance at this latitude.
                var jLat = (random.NextDouble() - 0.5) * 0.006;
                var jLng = (random.NextDouble() - 0.5) * 0.006 / Math.Cos(lat * Math.PI / 180);
                placed.Add((name, lat + jLat, lng + jLng));
                photos.Add(($"p{n++}.jpg", new FileTags
                {
                    Place = name, Coordinates = new Coordinates { Lat = lat + jLat, Lng = lng + jLng }
                }));
            }
        world.Folder("all", photos);
        var service = new PlaceSuggestionService(world.Files, world.Tags);
        var roots = new[] { world.Library };

        var checkedQueries = 0;
        for (var q = 0; q < 300; q++)
        {
            var (_, cLat, cLng) = centres[random.Next(centres.Length)];
            var lat = cLat + (random.NextDouble() - 0.5) * 0.03;
            var lng = cLng + (random.NextDouble() - 0.5) * 0.03;

            var expected = BruteForce(placed, lat, lng);
            var actual = service.GetNearby(roots, lat, lng);

            Assert.Equal(expected.Select(e => e.Place), actual.Select(a => a.Place));
            Assert.Equal(expected.Select(e => e.Confidence), actual.Select(a => a.Confidence));
            Assert.Equal(expected.Select(e => e.Distance), actual.Select(a => a.Distance));
            checkedQueries += expected.Count > 0 ? 1 : 0;
        }
        // The questions were not all about empty ground.
        Assert.True(checkedQueries > 100);
    }

    [Fact]
    public void Tags_that_say_nothing_about_places_do_not_make_the_places_stale()
    {
        using var world = new LibraryWorld();
        var coordinates = new Coordinates { Lat = 50.45, Lng = 30.52 };
        var dir = world.Folder("a", new[]
        {
            ("one.jpg", new FileTags { Place = "Дім", Coordinates = coordinates }),
            ("two.jpg", new FileTags { Coordinates = coordinates })
        });
        var service = new PlaceSuggestionService(world.Files, world.Tags);
        var first = service.GetSuggestions(new[] { world.Library });
        Assert.Equal(1, first.Unplaced);

        // The first write to a folder is not known to be harmless. An album afterwards is.
        world.Tags.AddTagsToFiles(new[] { Path.Combine(dir, "one.jpg") }, new BulkTagsRequest { Albums = new() { "Літо" } });
        var again = service.GetSuggestions(new[] { world.Library });
        world.Tags.AddTagsToFiles(new[] { Path.Combine(dir, "one.jpg") }, new BulkTagsRequest { Albums = new() { "Зима" } });
        var third = service.GetSuggestions(new[] { world.Library });
        Assert.Same(again, third);

        // A place does.
        world.Tags.AddTagsToFiles(new[] { Path.Combine(dir, "two.jpg") }, new BulkTagsRequest { Place = "Офіс" });
        Assert.Equal(0, service.GetSuggestions(new[] { world.Library }).Unplaced);
    }
}

public class FaceModelVersionTests
{
    private static TagsFile File(params (string Name, string[] People, string Album)[] files)
    {
        var tags = new TagsFile();
        foreach (var (name, people, album) in files)
            tags.Files[name] = new FileTags { People = people.ToList(), Albums = new() { album } };
        return tags;
    }

    [Fact]
    public void Only_a_change_in_who_is_tagged_changes_what_the_face_model_sees()
    {
        var before = FaceRecognitionService.PeopleHash(File(("a.jpg", new[] { "Анна" }, "Літо")));

        Assert.Equal(before, FaceRecognitionService.PeopleHash(File(("a.jpg", new[] { "Анна" }, "Зима"))));
        Assert.Equal(before, FaceRecognitionService.PeopleHash(File(("A.JPG", new[] { "анна" }, "Зима"))));
        Assert.NotEqual(before, FaceRecognitionService.PeopleHash(File(("a.jpg", new[] { "Олег" }, "Літо"))));
        Assert.NotEqual(before, FaceRecognitionService.PeopleHash(File(("a.jpg", new[] { "Анна", "Олег" }, "Літо"))));
        Assert.NotEqual(before, FaceRecognitionService.PeopleHash(File(("b.jpg", new[] { "Анна" }, "Літо"))));
        // A photo nobody is tagged on is not part of it.
        Assert.Equal(before, FaceRecognitionService.PeopleHash(
            File(("a.jpg", new[] { "Анна" }, "Літо"), ("c.jpg", Array.Empty<string>(), "Літо"))));
    }
}

public class LibraryListingTests
{
    [Fact]
    public void The_whole_library_listing_is_reused_until_something_changes()
    {
        using var world = new LibraryWorld();
        var dir = world.Folder("a", new[] { ("one.jpg", new FileTags()) });

        var first = world.Files.Across(new[] { world.Library });
        var second = world.Files.Across(new[] { world.Library });
        Assert.Same(first, second);

        world.Tags.AddTagsToFiles(new[] { Path.Combine(dir, "one.jpg") }, new BulkTagsRequest { Albums = new() { "Літо" } });
        var third = world.Files.Across(new[] { world.Library });
        Assert.NotSame(first, third);
        Assert.Equal(new[] { "Літо" }, third.Single().Tags.Albums);
    }

    [Fact]
    public void Search_ignores_case_in_the_filters_as_it_does_in_the_query()
    {
        using var world = new LibraryWorld();
        world.Folder("a", new[]
        {
            ("one.jpg", new FileTags { People = new() { "Анна" }, Albums = new() { "Літо" }, Groups = new() { "Сім'я" } }),
            ("two.jpg", new FileTags { People = new() { "Олег" } })
        });

        var found = world.Files.Search(new SearchRequest
        {
            RootPaths = new() { world.Library },
            People = new() { "анна" },
            Albums = new() { "ЛІТО" },
            Groups = new() { "сім'я" },
            Query = "ONE"
        });

        Assert.Equal("one.jpg", found.Single().FileName);
    }
}

public class ThumbnailCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mt-thumbs-" + Guid.NewGuid().ToString("N"));
    private readonly ThumbnailService _thumbnails;
    private readonly string _cache;

    public ThumbnailCacheTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaTagger:CacheRoot"] = Path.Combine(_root, "cache"),
            ["MediaTagger:SettingsPath"] = Path.Combine(_root, "settings.json")
        }).Build();
        var locator = new CacheLocator(new TestHost(_root), config, NullLogger<CacheLocator>.Instance);
        _thumbnails = new ThumbnailService(NullLogger<ThumbnailService>.Instance, locator, config,
            new SettingsService(config, NullLogger<SettingsService>.Instance));
        _cache = Path.Combine(_root, "cache", ".thumbnails");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Photo(string folder, string name = "a.jpg")
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.SteelBlue, 900, 600);
        image.Format = ImageMagick.MagickFormat.Jpeg;
        image.Write(path);
        return path;
    }

    [Fact]
    public async Task A_preview_survives_the_photo_moving_to_another_folder()
    {
        var photo = Photo("2019");
        var first = await _thumbnails.GetThumbnailPath(photo, 480);
        Assert.NotNull(first);
        var before = Directory.GetFiles(_cache, "*.jpg").Length;

        var moved = Path.Combine(_root, "sorted", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(photo, moved);

        var second = await _thumbnails.GetThumbnailPath(moved, 480);

        Assert.Equal(first, second);
        Assert.Equal(before, Directory.GetFiles(_cache, "*.jpg").Length);
    }

    [Fact]
    public async Task A_preview_made_under_the_old_path_based_name_is_kept()
    {
        var photo = Photo("album");
        var current = await _thumbnails.GetThumbnailPath(photo, 480);
        var legacy = _thumbnails.GetLegacyCachePath(photo, 480);
        File.Move(current!, legacy);

        var again = await _thumbnails.GetThumbnailPath(photo, 480);

        Assert.Equal(current, again);
        Assert.True(File.Exists(current));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public async Task An_edited_photo_gets_a_new_preview()
    {
        var photo = Photo("edit");
        var first = await _thumbnails.GetThumbnailPath(photo, 480);

        File.WriteAllBytes(photo, File.ReadAllBytes(photo).Concat(new byte[] { 0 }).ToArray());
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(5));

        var second = await _thumbnails.GetThumbnailPath(photo, 480);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task The_cache_can_be_measured_and_emptied()
    {
        var photo = Photo("measure");
        await _thumbnails.GetThumbnailPath(photo, 480);

        var (bytes, files, limit) = _thumbnails.CacheInfo();
        Assert.True(bytes > 0);
        Assert.Equal(1, files);
        Assert.Equal(10L * 1024 * 1024 * 1024, limit);

        Assert.Equal(1, _thumbnails.ClearCache());
        Assert.Equal(0, _thumbnails.CacheInfo().Files);
    }

    private sealed class TestHost : IHostEnvironment
    {
        public TestHost(string root) { ContentRootPath = root; }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MediaTagger";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
