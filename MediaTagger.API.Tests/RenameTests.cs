using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaTagger.API.Tests;

public class StoredPathTests
{
    [Fact]
    public void A_folder_rename_covers_the_folder_and_the_files_inside_it()
    {
        Assert.True(StoredPath.Covers(@"photos\trip", @"photos\trip"));
        Assert.True(StoredPath.Covers(@"photos\trip\a.jpg", @"photos\trip"));
        Assert.False(StoredPath.Covers(@"photos\trip2\a.jpg", @"photos\trip"));
        Assert.Equal(@"photos\holiday\a.jpg", StoredPath.Map(@"photos\trip\a.jpg", @"photos\trip", @"photos\holiday"));
    }

    [Fact]
    public void Full_paths_follow_the_renamed_folder_in_either_case()
    {
        var moved = StoredPath.MapFull(@"E:\Photos\Trip\a.jpg", @"e:\photos\trip", @"E:\Photos\Holiday");
        Assert.Equal(@"E:\Photos\Holiday\a.jpg", moved);
        Assert.Null(StoredPath.MapFull(@"E:\Photos\Trip2", @"E:\Photos\Trip", @"E:\Photos\Holiday"));
    }
}

public class RenameTests
{
    [Fact]
    public void Renaming_a_file_keeps_its_tags_and_remembered_faces()
    {
        using var world = new RenameWorld();
        var folder = world.Dir("album");
        var file = Path.Combine(folder, "a.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(folder, "_tags.json"), """
            { "files": { "a.jpg": { "people": ["Анна"], "groups": [], "albums": [], "place": "Київ", "notes": "" } } }
            """);
        var info = new FileInfo(file);
        world.Faces.Save(file, info.Length, info.LastWriteTimeUtc.Ticks, 1, 8, 8,
            new[] { new DetectedFace(0.1f, 0.1f, 0.2f, 0.2f, 0.9f, new[] { 1f }) });
        world.Exif.GetOrRead(file, info, _ => new ExifData { Camera = "Test" });

        var outcome = world.Rename.Rename(file, "b.jpg", merge: false);

        Assert.Null(outcome.Code);
        Assert.Equal(Path.Combine(folder, "b.jpg"), outcome.Path);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(outcome.Path));
        Assert.Equal(new[] { "Анна" }, world.Tags.ReadTagsFile(folder).Files["b.jpg"].People);
        Assert.False(world.Tags.ReadTagsFile(folder).Files.ContainsKey("a.jpg"));

        var moved = new FileInfo(outcome.Path!);
        var faces = world.Faces.TryGet(outcome.Path!, moved.Length, moved.LastWriteTimeUtc.Ticks);
        Assert.NotNull(faces);
        Assert.Equal(0.1f, faces!.Faces[0].X);
        Assert.Null(world.Faces.TryGet(file, info.Length, info.LastWriteTimeUtc.Ticks));

        var exif = world.Exif.GetOrRead(outcome.Path!, moved, _ => throw new InvalidOperationException("cache miss"));
        Assert.Equal("Test", exif!.Camera);
    }

    [Fact]
    public void Renaming_a_folder_rewrites_nested_tags_paths_and_library_folders()
    {
        using var world = new RenameWorld();
        var root = world.Dir("library");
        var folder = Path.Combine(root, "Trip");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.jpg");
        File.WriteAllBytes(file, new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, "_tags.json"), """
            { "files": { "a.jpg": { "people": ["Олег"], "groups": [], "albums": [], "place": "", "notes": "" } } }
            """);
        var info = new FileInfo(file);
        world.Faces.Save(file, info.Length, info.LastWriteTimeUtc.Ticks, 1, 4, 4,
            new[] { new DetectedFace(0, 0, 1, 1, 1, new[] { 1f }) });
        world.Settings.Save(new AppSettings
        {
            DefaultFolder = folder,
            RecentFolders = new List<string> { folder },
            LibraryFolders = new List<string> { root, folder }
        });

        var outcome = world.Rename.Rename(folder, "Holiday", merge: false);

        var dest = Path.Combine(root, "Holiday");
        Assert.Null(outcome.Code);
        Assert.Equal(dest, outcome.Path);
        Assert.Equal(new[] { "Олег" }, world.Tags.ReadTagsFile(dest).Files["a.jpg"].People);
        Assert.Equal(dest, outcome.Settings!.DefaultFolder);
        Assert.Equal(root, outcome.Settings.LibraryFolders[0]);
        Assert.Equal(dest, outcome.Settings.LibraryFolders[1]);

        var moved = new FileInfo(Path.Combine(dest, "a.jpg"));
        Assert.NotNull(world.Faces.TryGet(moved.FullName, moved.Length, moved.LastWriteTimeUtc.Ticks));
    }

    [Fact]
    public void An_existing_folder_asks_before_merging_and_then_combines_tags()
    {
        using var world = new RenameWorld();
        var root = world.Dir("library");
        var source = Path.Combine(root, "Trip");
        var dest = Path.Combine(root, "Holiday");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(source, "a.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dest, "b.jpg"), new byte[] { 2 });
        File.WriteAllText(Path.Combine(source, "_tags.json"), """
            { "files": { "a.jpg": { "people": ["Анна"], "groups": [], "albums": [], "place": "", "notes": "" } } }
            """);
        File.WriteAllText(Path.Combine(dest, "_tags.json"), """
            { "files": { "b.jpg": { "people": ["Олег"], "groups": [], "albums": [], "place": "", "notes": "" } } }
            """);

        var ask = world.Rename.Rename(source, "Holiday", merge: false);
        Assert.Equal("merge", ask.Code);
        Assert.True(Directory.Exists(source));

        var merged = world.Rename.Rename(source, "Holiday", merge: true);
        Assert.Null(merged.Code);
        Assert.True(merged.Merged);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(dest, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(dest, "b.jpg")));
        var tags = world.Tags.ReadTagsFile(dest);
        Assert.Equal(new[] { "Анна" }, tags.Files["a.jpg"].People);
        Assert.Equal(new[] { "Олег" }, tags.Files["b.jpg"].People);
    }

    [Fact]
    public void A_merge_with_the_same_file_name_changes_nothing()
    {
        using var world = new RenameWorld();
        var root = world.Dir("library");
        var source = Path.Combine(root, "Trip");
        var dest = Path.Combine(root, "Holiday");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(source, "a.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dest, "a.jpg"), new byte[] { 2 });

        var outcome = world.Rename.Rename(source, "Holiday", merge: true);

        Assert.Equal("exists", outcome.Code);
        Assert.Equal("a.jpg", outcome.Name);
        Assert.True(File.Exists(Path.Combine(source, "a.jpg")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(dest, "a.jpg")));
    }

    [Fact]
    public void Moving_a_file_keeps_its_tags_in_the_new_folder()
    {
        using var world = new RenameWorld();
        var root = world.Dir("library");
        var from = Path.Combine(root, "inbox");
        var to = Path.Combine(root, "kept");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        var file = Path.Combine(from, "a.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(from, "_tags.json"), """
            { "files": { "a.jpg": { "people": ["Анна"], "groups": [], "albums": [], "place": "", "notes": "" } } }
            """);
        var info = new FileInfo(file);
        world.Faces.Save(file, info.Length, info.LastWriteTimeUtc.Ticks, 1, 4, 4,
            new[] { new DetectedFace(0, 0, 1, 1, 1, new[] { 1f }) });

        var outcome = world.Rename.MoveInto(file, to, merge: false);

        var dest = Path.Combine(to, "a.jpg");
        Assert.Null(outcome.Code);
        Assert.Equal(dest, outcome.Path);
        Assert.False(File.Exists(file));
        Assert.Equal(new[] { "Анна" }, world.Tags.ReadTagsFile(to).Files["a.jpg"].People);
        Assert.False(world.Tags.ReadTagsFile(from).Files.ContainsKey("a.jpg"));
        var moved = new FileInfo(dest);
        Assert.NotNull(world.Faces.TryGet(dest, moved.Length, moved.LastWriteTimeUtc.Ticks));
    }

    [Fact]
    public void Moving_a_folder_into_itself_is_refused()
    {
        using var world = new RenameWorld();
        var root = world.Dir("library");
        var folder = Path.Combine(root, "Trip");
        var child = Path.Combine(folder, "day");
        Directory.CreateDirectory(child);

        var intoSelf = world.Rename.MoveInto(folder, folder, merge: false);
        var intoChild = world.Rename.MoveInto(folder, child, merge: false);

        Assert.Equal("invalid", intoSelf.Code);
        Assert.Equal("invalid", intoChild.Code);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Moving_onto_an_existing_folder_asks_before_merging()
    {
        using var world = new RenameWorld();
        var root = world.Dir("library");
        var from = Path.Combine(root, "a");
        var into = Path.Combine(root, "b");
        Directory.CreateDirectory(Path.Combine(from, "Trip"));
        Directory.CreateDirectory(Path.Combine(into, "Trip"));
        File.WriteAllBytes(Path.Combine(from, "Trip", "a.jpg"), new byte[] { 1 });

        var ask = world.Rename.MoveInto(Path.Combine(from, "Trip"), into, merge: false);
        Assert.Equal("merge", ask.Code);
        Assert.True(File.Exists(Path.Combine(from, "Trip", "a.jpg")));

        var merged = world.Rename.MoveInto(Path.Combine(from, "Trip"), into, merge: true);
        Assert.Null(merged.Code);
        Assert.True(merged.Merged);
        Assert.True(File.Exists(Path.Combine(into, "Trip", "a.jpg")));
    }

    private sealed class RenameWorld : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mt-rename-" + Guid.NewGuid().ToString("N"));
        public TagsService Tags { get; }
        public FaceStore Faces { get; }
        public ExifCache Exif { get; }
        public SettingsService Settings { get; }
        public MediaRename Rename { get; }

        public RenameWorld()
        {
            Directory.CreateDirectory(_root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaTagger:CacheRoot"] = Path.Combine(_root, "cache"),
                ["MediaTagger:SettingsPath"] = Path.Combine(_root, "settings.json")
            }).Build();
            var locator = new CacheLocator(new TestEnv(_root), config, NullLogger<CacheLocator>.Instance);
            Tags = new TagsService();
            Faces = new FaceStore(locator, NullLogger<FaceStore>.Instance);
            Exif = new ExifCache(locator, NullLogger<ExifCache>.Instance);
            Settings = new SettingsService(config, NullLogger<SettingsService>.Instance);
            var index = new LibraryIndex(Tags, new ExifService(), Exif, new ScanProgress(), NullLogger<LibraryIndex>.Instance);
            Rename = new MediaRename(Tags, Faces, Exif, Settings, new FileSystemService(index));
        }

        public string Dir(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            Faces.Dispose();
            Exif.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* a watcher may still have a file open */ }
        }
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public TestEnv(string root) { ContentRootPath = root; }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MediaTagger";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
