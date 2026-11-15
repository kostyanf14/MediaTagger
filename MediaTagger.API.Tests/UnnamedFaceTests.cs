using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class UnnamedFaceTests
{
    [Fact]
    public void The_queue_is_unnamed_photos_only()
    {
        Assert.True(UnnamedFaceRules.IsQueuePhoto(null, labelled: false, skipped: false));
        Assert.False(UnnamedFaceRules.IsQueuePhoto(0, labelled: false, skipped: false));
        Assert.False(UnnamedFaceRules.IsQueuePhoto(2.5, labelled: false, skipped: false));
        Assert.False(UnnamedFaceRules.IsQueuePhoto(null, labelled: true, skipped: false));
        Assert.False(UnnamedFaceRules.IsQueuePhoto(null, labelled: false, skipped: true));
    }

    [Fact]
    public void A_face_has_to_sit_inside_the_library_folder()
    {
        Assert.True(UnnamedFaceRules.UnderRoot(@"photos\trip\a.jpg", "photos"));
        Assert.False(UnnamedFaceRules.UnderRoot(@"photos-old\a.jpg", "photos"));
        Assert.True(UnnamedFaceRules.UnderRoot(@"photos\a.jpg", ""));
    }
}
