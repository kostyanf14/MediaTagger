using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class TagsSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-tags-" + Guid.NewGuid().ToString("N"));
    private readonly TagsService _tags = new();

    public TagsSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string TagsPath => Path.Combine(_dir, "_tags.json");

    private string Photo(string name = "a.jpg")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    [Fact]
    public void A_corrupt_tags_file_refuses_the_edit_and_stays_as_it_was()
    {
        const string broken = "{ \"files\": { \"a.jpg\": { \"people\": [\"Анна\"";
        File.WriteAllText(TagsPath, broken);

        Assert.Throws<TagsFileUnreadableException>(() => _tags.UpdateFileTags(Photo(), new FileTags()));
        Assert.Throws<TagsFileUnreadableException>(() =>
            _tags.AddTagsToFiles(new[] { Photo("b.jpg") }, new BulkTagsRequest { People = new() { "Олег" } }));
        Assert.Throws<TagsFileUnreadableException>(() => _tags.RemoveFileTags(Photo("c.jpg")));

        Assert.Equal(broken, File.ReadAllText(TagsPath));
    }

    [Fact]
    public void An_empty_tags_file_is_treated_as_unreadable_not_as_no_tags()
    {
        File.WriteAllText(TagsPath, "");
        Assert.Throws<TagsFileUnreadableException>(() => _tags.UpdateFileTags(Photo(), new FileTags()));
    }

    [Fact]
    public void A_missing_tags_file_is_simply_created()
    {
        _tags.UpdateFileTags(Photo(), new FileTags { People = new() { "Анна" } });
        Assert.Equal(new[] { "Анна" }, _tags.ReadTagsFile(_dir).Files["a.jpg"].People);
    }

    [Fact]
    public void A_write_leaves_no_temp_file_and_keeps_the_previous_version_as_a_backup()
    {
        var photo = Photo();
        _tags.UpdateFileTags(photo, new FileTags { People = new() { "Ann" } });
        _tags.UpdateFileTags(photo, new FileTags { People = new() { "Oleg" } });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        var backup = Path.Combine(_dir, "_tags.json.bak");
        Assert.True(File.Exists(backup));
        Assert.Contains("Ann", File.ReadAllText(backup));
        Assert.Contains("Oleg", File.ReadAllText(TagsPath));
    }

    [Fact]
    public void Simultaneous_bulk_adds_keep_every_entry()
    {
        var photos = Enumerable.Range(0, 20).Select(i => Photo($"p{i}.jpg")).ToList();

        Parallel.ForEach(photos, p =>
            _tags.AddTagsToFiles(new[] { p }, new BulkTagsRequest { Albums = new() { "Літо" } }));

        var file = _tags.ReadTagsFile(_dir);
        Assert.Equal(20, file.Files.Count);
        Assert.All(file.Files.Values, t => Assert.Equal(new[] { "Літо" }, t.Albums));
    }

    [Fact]
    public void A_failed_destination_read_leaves_the_source_entries_in_place()
    {
        var source = Path.Combine(_dir, "from");
        var dest = Path.Combine(_dir, "to");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(source, "_tags.json"),
            """{ "files": { "a.jpg": { "people": ["Анна"], "groups": [], "albums": [], "place": "", "notes": "" } } }""");
        File.WriteAllText(Path.Combine(dest, "_tags.json"), "{ nope");

        Assert.Throws<TagsFileUnreadableException>(() =>
            _tags.MoveFileEntries(new[] { (Path.Combine(source, "a.jpg"), Path.Combine(dest, "a.jpg")) }));

        Assert.Equal(new[] { "Анна" }, _tags.ReadTagsFile(source).Files["a.jpg"].People);
    }

    [Fact]
    public void Moved_entries_arrive_in_the_destination_and_leave_the_source()
    {
        var source = Path.Combine(_dir, "from");
        var dest = Path.Combine(_dir, "to");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(source, "_tags.json"),
            """{ "files": { "a.jpg": { "people": ["Анна"], "groups": [], "albums": [], "place": "", "notes": "" } } }""");

        _tags.MoveFileEntries(new[] { (Path.Combine(source, "a.jpg"), Path.Combine(dest, "b.jpg")) });

        Assert.Equal(new[] { "Анна" }, _tags.ReadTagsFile(dest).Files["b.jpg"].People);
        Assert.Empty(_tags.ReadTagsFile(source).Files);
    }

    [Fact]
    public void A_deleted_files_tags_are_kept_aside_and_come_back_with_the_file()
    {
        var photo = Photo();
        _tags.UpdateFileTags(photo, new FileTags { People = new() { "Анна" }, Notes = "літо" });

        _tags.RemoveFileTags(photo);
        var afterDelete = _tags.ReadTagsFile(_dir);
        Assert.Empty(afterDelete.Files);
        Assert.NotNull(afterDelete.Removed);

        _tags.RestoreRemoved(_dir, new[] { "a.jpg" });

        var restored = _tags.ReadTagsFile(_dir);
        Assert.Equal(new[] { "Анна" }, restored.Files["a.jpg"].People);
        Assert.Equal("літо", restored.Files["a.jpg"].Notes);
        Assert.Null(restored.Removed);
    }

    [Fact]
    public void Tags_kept_for_a_deleted_file_are_dropped_after_ninety_days()
    {
        var photo = Photo();
        _tags.UpdateFileTags(photo, new FileTags { People = new() { "Анна" } });
        _tags.RemoveFileTags(photo);

        var file = _tags.ReadTagsFile(_dir);
        file.Removed!["a.jpg"].At = DateTime.UtcNow.AddDays(-91);
        _tags.WriteTagsFile(_dir, file);

        Assert.Null(_tags.ReadTagsFile(_dir).Removed);
    }

    [Fact]
    public void Renaming_people_skips_an_unreadable_folder_and_still_does_the_rest()
    {
        var good = Path.Combine(_dir, "good");
        var bad = Path.Combine(_dir, "bad");
        Directory.CreateDirectory(good);
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(good, "_tags.json"),
            """{ "files": { "a.jpg": { "people": ["Ann"], "groups": [], "albums": [], "place": "", "notes": "" } } }""");
        File.WriteAllText(Path.Combine(bad, "_tags.json"), "{ broken");

        var changed = _tags.RenamePeople(new[] { _dir }, new[] { "Ann" }, "Анна", out var skipped);

        Assert.Equal(1, changed);
        Assert.Equal(1, skipped);
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(bad, "_tags.json")));
        Assert.Equal(new[] { "Анна" }, _tags.ReadTagsFile(good).Files["a.jpg"].People);
    }

    [Fact]
    public void System_folders_are_not_part_of_a_library()
    {
        Assert.True(LibraryIndex.IsSkippedFolder("@eaDir"));
        Assert.True(LibraryIndex.IsSkippedFolder("#recycle"));
        Assert.True(LibraryIndex.IsSkippedFolder(".thumbnails"));
        Assert.True(LibraryIndex.IsSkippedFolder("$RECYCLE.BIN"));
        Assert.False(LibraryIndex.IsSkippedFolder("2019"));
        Assert.False(LibraryIndex.IsSkippedFolder("Відпустка"));
    }

    [Fact]
    public void AtomicFile_replaces_content_without_leaving_stray_files()
    {
        var path = Path.Combine(_dir, "x.json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir, "x.json*"));
    }
}
