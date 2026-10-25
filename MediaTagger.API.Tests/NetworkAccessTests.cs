using MediaTagger.API.Services;
using Xunit;

namespace MediaTagger.API.Tests;

public class NetworkAccessTests
{
    [Fact]
    public void Loopback_is_open_and_any_other_host_is_not()
    {
        Assert.True(NetworkAccess.IsLoopbackHost("localhost"));
        Assert.True(NetworkAccess.IsLoopbackHost("127.0.0.1"));
        Assert.True(NetworkAccess.IsLoopbackHost("[::1]"));
        Assert.False(NetworkAccess.IsLoopbackHost("0.0.0.0"));
        Assert.False(NetworkAccess.IsLoopbackHost("192.168.1.20"));
    }

    [Fact]
    public void Key_matches_with_or_without_dashes_and_rejects_a_different_one()
    {
        const string key = "ABCD-EFGH-JKLM-NPQR";
        Assert.True(NetworkAccess.KeysMatch(key, "abcdefghjklmnpqr"));
        Assert.True(NetworkAccess.KeysMatch(key, "  abcd-efgh-jklm-npqr\n"));
        Assert.False(NetworkAccess.KeysMatch(key, "ABCD-EFGH-JKLM-NPQS"));
        Assert.False(NetworkAccess.KeysMatch(key, ""));
    }

    [Fact]
    public void Missing_key_is_created_once_and_a_configured_key_is_not_written()
    {
        var dir = Directory.CreateTempSubdirectory("mediatagger-key-").FullName;
        var first = NetworkAccess.LoadOrCreateKey(dir, null);
        var second = NetworkAccess.LoadOrCreateKey(dir, null);
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "access-key.txt")).Trim());

        var configured = NetworkAccess.LoadOrCreateKey(dir, "ZZZZ-ZZZZ-ZZZZ-ZZZZ");
        Assert.Equal("ZZZZ-ZZZZ-ZZZZ-ZZZZ", configured);
        Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "access-key.txt")).Trim());
    }

    [Fact]
    public void Network_bind_is_https_with_a_key_and_localhost_is_unchanged()
    {
        var dir = Directory.CreateTempSubdirectory("mediatagger-net-").FullName;
        var local = NetworkAccess.Prepare(dir, "http://localhost:5001", dir, null);
        Assert.Equal("http://localhost:5001", local.ListenUrl);
        Assert.Null(local.Key);
        Assert.Null(local.Certificate);

        var nas = NetworkAccess.Prepare(dir, "http://0.0.0.0:5001", dir, "ABCD-EFGH-JKLM-NPQR");
        Assert.Equal("https://0.0.0.0:5001", nas.ListenUrl);
        Assert.Equal("ABCD-EFGH-JKLM-NPQR", nas.Key);
        Assert.NotNull(nas.Certificate);
        Assert.True(nas.Certificate!.HasPrivateKey);

        using var again = NetworkAccess.EnsureCertificate(dir);
        Assert.Equal(nas.Certificate.Thumbprint, again.Thumbprint);
        nas.Certificate.Dispose();
    }
}
