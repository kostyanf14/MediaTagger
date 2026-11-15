using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class ApiFeaturesTests
{
    [Fact]
    public void Offered_names_are_unique_and_cover_the_negotiated_screens()
    {
        Assert.Equal(ApiFeatures.All.Length, ApiFeatures.All.Distinct().Count());
        Assert.Contains(ApiFeatures.Recaps, ApiFeatures.All);
        Assert.Contains(ApiFeatures.UnnamedFaces, ApiFeatures.All);
        Assert.All(ApiFeatures.All, name => Assert.Matches("^[a-z0-9-]+$", name));
    }
}
