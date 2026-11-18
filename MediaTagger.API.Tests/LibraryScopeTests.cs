using MediaTagger.API.Controllers;
using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaTagger.API.Tests;

public class LibraryScopeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mt-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _photos;
    private readonly string[] _roots;

    public LibraryScopeTests()
    {
        _photos = Path.Combine(_base, "Photos");
        Directory.CreateDirectory(_photos);
        _roots = new[] { _photos };
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_root_and_anything_inside_it_is_allowed()
    {
        Assert.True(LibraryScope.Allows(_photos, _roots));
        Assert.True(LibraryScope.Allows(_photos + Path.DirectorySeparatorChar, _roots));
        Assert.True(LibraryScope.Allows(Path.Combine(_photos, "2019", "a.jpg"), _roots));
    }

    [Fact]
    public void A_dot_dot_escape_is_refused()
    {
        Assert.False(LibraryScope.Allows(Path.Combine(_photos, "..", "secret.txt"), _roots));
        Assert.False(LibraryScope.Allows(Path.Combine(_photos, "2019", "..", "..", "secret.txt"), _roots));
        // Still inside after resolving, so still fine.
        Assert.True(LibraryScope.Allows(Path.Combine(_photos, "2019", "..", "a.jpg"), _roots));
    }

    [Fact]
    public void A_sibling_that_only_shares_a_prefix_is_refused()
    {
        Assert.False(LibraryScope.Allows(_photos + "2", _roots));
        Assert.False(LibraryScope.Allows(Path.Combine(_base, "Photos2", "a.jpg"), _roots));
    }

    [Fact]
    public void Another_location_is_refused_and_an_empty_value_is_left_to_the_endpoint()
    {
        Assert.False(LibraryScope.Allows(Path.GetTempPath(), _roots));
        Assert.True(LibraryScope.Allows("", _roots));
        Assert.True(LibraryScope.Allows(null, _roots));
    }

    [Fact]
    public void Device_paths_are_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(LibraryScope.Allows(@"\\?\C:\Windows\win.ini", _roots));
        Assert.False(LibraryScope.Allows(@"\\.\PhysicalDrive0", _roots));
    }

    [Fact]
    public void Case_is_ignored_where_the_file_system_ignores_it()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.True(LibraryScope.Allows(_photos.ToUpperInvariant(), _roots));
    }

    // The filter looks at every argument of an action, so these are the shapes the real endpoints use.
    private string? Refused(params (string Name, object? Value)[] arguments)
    {
        var settings = new SettingsService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaTagger:SettingsPath"] = Path.Combine(_base, "settings.json")
            }).Build(),
            NullLogger<SettingsService>.Instance);
        settings.Save(new AppSettings { LibraryFolders = _roots.ToList() });
        var filter = new LibraryScopeFilter(new LibraryScope(settings), NullLogger<LibraryScopeFilter>.Instance);

        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(),
            arguments.ToDictionary(a => a.Name, a => a.Value), new object());
        filter.OnActionExecuting(context);
        return context.Result is ObjectResult { StatusCode: 403 } ? "refused" : null;
    }

    [Fact]
    public void Query_values_named_like_paths_are_checked()
    {
        Assert.Null(Refused(("filePath", Path.Combine(_photos, "a.jpg"))));
        Assert.Equal("refused", Refused(("filePath", Path.Combine(_base, "other", "a.jpg"))));
        Assert.Equal("refused", Refused(("rootPath", new[] { _photos, _base })));
        Assert.Equal("refused", Refused(("folderPath", _base)));
        // Anything that is not a path is not looked at.
        Assert.Null(Refused(("query", Path.GetTempPath())));
    }

    [Fact]
    public void Request_bodies_are_checked_down_to_their_lists()
    {
        var inside = Path.Combine(_photos, "a.jpg");
        var outside = Path.Combine(_base, "x.jpg");

        Assert.Null(Refused(("request", new UpdateTagsRequest { FilePath = inside })));
        Assert.Equal("refused", Refused(("request", new UpdateTagsRequest { FilePath = outside })));
        Assert.Equal("refused", Refused(("request", new BulkTagsRequest { FilePaths = new() { inside, outside } })));
        Assert.Equal("refused", Refused(("request", new MoveRequest { Path = inside, Destination = outside })));
        Assert.Equal("refused", Refused(("request", new SearchRequest { RootPath = _photos, RootPaths = new() { outside } })));
        Assert.Equal("refused", Refused(("items", new List<FaceDecision> { new() { FilePath = outside } })));
        Assert.Equal("refused", Refused(("filePaths", new List<string> { inside, outside })));
        Assert.Null(Refused(("filePaths", new List<string> { inside })));
    }
}

public class ClientOriginTests
{
    [Fact]
    public void By_default_only_the_apps_own_window_and_the_dev_server_may_call()
    {
        var origins = NetworkAccess.ClientOrigins(null);
        Assert.Contains("app://mediatagger", origins);
        Assert.Contains("http://localhost:3000", origins);
        Assert.DoesNotContain("*", origins);
        Assert.DoesNotContain("null", origins);
    }

    [Fact]
    public void A_configured_list_replaces_the_defaults()
    {
        var origins = NetworkAccess.ClientOrigins("http://localhost:4000/, app://other ;");
        Assert.Equal(new[] { "http://localhost:4000", "app://other" }, origins);
    }
}
