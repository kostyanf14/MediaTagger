using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class FfmpegDirectoryTests
{
    [Fact]
    public void Directory_is_usable_only_when_it_lists_both_tools()
    {
        var both = Directory.CreateTempSubdirectory("mediatagger-ffmpeg-").FullName;
        var ffmpegOnly = Directory.CreateTempSubdirectory("mediatagger-ffmpeg-only-").FullName;
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        File.WriteAllBytes(Path.Combine(both, "ffmpeg" + suffix), []);
        File.WriteAllBytes(Path.Combine(both, "ffprobe" + suffix), []);
        File.WriteAllBytes(Path.Combine(ffmpegOnly, "ffmpeg" + suffix), []);

        Assert.Equal(Path.GetFullPath(both), Path.GetFullPath(ThumbnailService.UsableFfmpegDirectory(both)!));
        Assert.Null(ThumbnailService.UsableFfmpegDirectory(ffmpegOnly));
        Assert.Null(ThumbnailService.UsableFfmpegDirectory(null));
    }
}
