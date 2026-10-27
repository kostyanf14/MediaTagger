using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaTagger.API.Tests;

public class SettingsTests
{
    private static SettingsService Open(string path) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MediaTagger:SettingsPath"] = path })
            .Build(),
        NullLogger<SettingsService>.Instance);

    [Fact]
    public void Video_mode_is_saved_by_name_and_reported_when_it_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), "mt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // A file from before the setting existed: whole-clip analysis is the default.
            File.WriteAllText(path, """{ "defaultFolder": "E:\\Photos", "recentFolders": [] }""");
            var settings = Open(path);
            Assert.Equal(VideoFaceMode.Frames, settings.VideoFaces);

            (VideoFaceMode Before, VideoFaceMode After)? change = null;
            settings.Changed += (before, after) => change = (before.VideoFaces, after.VideoFaces);
            var updated = settings.Get();
            settings.Save(new AppSettings
            {
                DefaultFolder = updated.DefaultFolder,
                RecentFolders = updated.RecentFolders,
                VideoFaces = VideoFaceMode.Detailed
            });

            Assert.Equal((VideoFaceMode.Frames, VideoFaceMode.Detailed), change);
            Assert.Contains("\"videoFaces\": \"Detailed\"", File.ReadAllText(path));
            Assert.Equal(VideoFaceMode.Detailed, Open(path).VideoFaces);
            Assert.Equal("E:\\Photos", Open(path).Get().DefaultFolder);
        }
        finally { File.Delete(path); }
    }
}
