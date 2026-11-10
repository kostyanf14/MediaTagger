using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class MediaTypeTests
{
    [Fact]
    public void Three_gp_is_a_video()
    {
        Assert.True(LibraryIndex.IsMediaFile("clip.3gp"));
        Assert.True(LibraryIndex.IsMediaFile("CLIP.3GP"));
        Assert.Equal("video", LibraryIndex.GetMediaType("phone.3gp"));
    }
}
