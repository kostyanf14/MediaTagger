using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class PeopleRenameTests
{
    [Fact]
    public void Rename_rewrites_people_tags_and_folds_duplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-people-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "trip");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, "_tags.json"), """
                { "files": {
                    "a.jpg": { "people": ["анна", "Олег"], "groups": ["анна"], "albums": [], "place": "", "notes": "" },
                    "b.jpg": { "people": ["Марія"], "groups": [], "albums": [], "place": "", "notes": "" }
                } }
                """);
            File.WriteAllText(Path.Combine(nested, "_tags.json"), """
                { "files": { "c.jpg": { "people": ["Анна", "анна"], "groups": [], "albums": [], "place": "", "notes": "" } } }
                """);

            var tags = new TagsService();
            var folders = tags.RenamePeople(new[] { root }, new[] { "Анна", "Олег" }, "Анна К");

            Assert.Equal(2, folders);
            var top = tags.ReadTagsFile(root);
            Assert.Equal(new[] { "Анна К" }, top.Files["a.jpg"].People);
            Assert.Equal(new[] { "анна" }, top.Files["a.jpg"].Groups);
            Assert.Equal(new[] { "Марія" }, top.Files["b.jpg"].People);

            var child = tags.ReadTagsFile(nested);
            Assert.Equal(new[] { "Анна К" }, child.Files["c.jpg"].People);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Rename_also_rewrites_the_photographer()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "_tags.json"), """
                { "files": {
                    "a.jpg": { "people": ["Марія"], "photographer": "олег", "groups": [], "albums": [], "place": "", "notes": "" },
                    "b.jpg": { "people": [], "photographer": "Марія", "groups": [], "albums": [], "place": "", "notes": "" }
                } }
                """);

            var tags = new TagsService();
            var folders = tags.RenamePeople(new[] { root }, new[] { "Олег" }, "Олег П");

            Assert.Equal(1, folders);
            var file = tags.ReadTagsFile(root);
            Assert.Equal("Олег П", file.Files["a.jpg"].Photographer);
            Assert.Equal(new[] { "Марія" }, file.Files["a.jpg"].People);
            Assert.Equal("Марія", file.Files["b.jpg"].Photographer);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Same_spelling_does_not_rewrite_the_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "_tags.json");
        const string original = """
            { "files": { "a.jpg": { "people": ["Анна"], "groups": [], "albums": [], "place": "", "notes": "" } } }
            """;
        try
        {
            File.WriteAllText(path, original);
            var folders = new TagsService().RenamePeople(new[] { root }, new[] { "Анна" }, "Анна");
            Assert.Equal(0, folders);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
