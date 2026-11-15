using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class RecapTests
{
    private static readonly DateTime Today = new(2026, 11, 15);

    private static RecapBuilder.Shot Shot(int year, int month, int day) =>
        new($"p/{year}{month:00}{day:00}.jpg", "a.jpg", "image", new DateTime(year, month, day, 12, 0, 0));

    [Fact]
    public void Each_photo_lands_in_the_memory_it_belongs_to()
    {
        var groups = RecapBuilder.Build(new[]
        {
            Shot(2020, 11, 15),
            Shot(2024, 11, 15),
            Shot(2026, 11, 15),
            Shot(2026, 11, 10),
            Shot(2026, 10, 2),
            Shot(2025, 11, 3),
            Shot(2025, 11, 14),
            Shot(2025, 11, 15)
        }, Today);

        string[] Ids(string id) => groups.Single(g => g.Id == id).Items.Select(i => i.FilePath).ToArray();

        Assert.Equal(new[] { "p/20251115.jpg", "p/20241115.jpg", "p/20201115.jpg" }, Ids("on-this-day"));
        Assert.Contains("p/20261115.jpg", Ids("last-week"));
        Assert.Contains("p/20261110.jpg", Ids("last-week"));
        Assert.DoesNotContain("p/20241115.jpg", Ids("last-week"));
        Assert.Equal(new[] { "p/20261002.jpg" }, Ids("last-month"));
        Assert.Contains("p/20251103.jpg", Ids("month-last-year"));
        Assert.Equal(new[] { "p/20251114.jpg" }, Ids("a-year-ago"));
        Assert.DoesNotContain("p/20251115.jpg", Ids("a-year-ago"));
    }

    [Fact]
    public void An_empty_memory_is_left_out()
    {
        var groups = RecapBuilder.Build(new[] { Shot(2026, 11, 15) }, Today);
        Assert.DoesNotContain(groups, g => g.Id == "on-this-day");
        Assert.Contains(groups, g => g.Id == "last-week");
    }
}
