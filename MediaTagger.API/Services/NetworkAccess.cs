using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MediaTagger.API.Services;

/// <summary>
/// Local use (localhost) stays open. Binding any other address means the API is on a
/// LAN, so it speaks HTTPS with its own certificate and requires one shared key.
/// </summary>
public static class NetworkAccess
{
    public static NetworkGate Prepare(string contentRoot, string? listenUrl, string? cacheRoot, string? accessKey)
    {
        if (string.IsNullOrWhiteSpace(listenUrl))
            listenUrl = "http://localhost:5001";
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
            throw new InvalidOperationException($"MediaTagger:ListenUrl is not an absolute URL with a port: {listenUrl}");

        if (IsLoopbackHost(uri.Host))
            return new NetworkGate(listenUrl, null, null);

        var root = CacheLocator.ResolveRoot(contentRoot, cacheRoot);
        var key = LoadOrCreateKey(root, accessKey);
        var cert = EnsureCertificate(root);
        var listen = $"{Uri.UriSchemeHttps}://{FormatHost(uri)}:{uri.Port}";
        Console.WriteLine("MediaTagger is listening on the network.");
        Console.WriteLine($"  On the PC, set MEDIATAGGER_API_URL={listen.Replace("0.0.0.0", "<nas-ip>", StringComparison.Ordinal).Replace("[::]", "<nas-ip>", StringComparison.Ordinal)}");
        Console.WriteLine($"  Access key: {key}");
        Console.WriteLine($"  A copy of the key is in {Path.Combine(root, "access-key.txt")}");
        return new NetworkGate(listen, key, cert);
    }

    public static bool IsLoopbackHost(string host)
    {
        host = host.Trim().Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Configured key wins. Otherwise the file, or a new one written beside the cache.</summary>
    public static string LoadOrCreateKey(string directory, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "access-key.txt");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0) return existing;
        }

        var key = CreateKey();
        File.WriteAllText(path, key + Environment.NewLine);
        TryRestrict(path);
        return key;
    }

    public static string CreateKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[19];
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var n = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0 && i % 4 == 0) chars[n++] = '-';
            chars[n++] = alphabet[bytes[i] % alphabet.Length];
        }
        return new string(chars);
    }

    public static bool KeysMatch(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(NormalizeKey(expected));
        var b = System.Text.Encoding.UTF8.GetBytes(NormalizeKey(presented));
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static X509Certificate2 EnsureCertificate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "api-cert.pfx");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, CreateCertificate());
            TryRestrict(path);
        }
        // Bytes, not a path, so the file is not locked. Windows TLS (Schannel) cannot use
        // an in-memory key; Linux and macOS can, and a NAS has no certificate store.
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet;
        return new X509Certificate2(File.ReadAllBytes(path), "", flags);
    }

    public static void UseKeyCheck(WebApplication app, string? key)
    {
        if (key is null) return;
        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsOptions(ctx.Request.Method)
                || ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // shortcut: img and video tags cannot send a header, so the key is also
            // accepted on the query string. It can land in a proxy access log.
            var presented = ctx.Request.Headers["X-MediaTagger-Key"].ToString();
            if (presented.Length == 0)
                presented = ctx.Request.Query["access_key"].ToString();
            if (!KeysMatch(key, presented))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"access_key_required\"}");
                return;
            }
            await next();
        });
    }

    private static string NormalizeKey(string key)
    {
        var chars = key.Trim().ToUpperInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private static string FormatHost(Uri uri)
    {
        var host = uri.Host;
        if (host.Contains(':') && !host.StartsWith('[')) return $"[{host}]";
        return host;
    }

    private static byte[] CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=MediaTagger", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("MediaTagger");
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return issued.Export(X509ContentType.Pfx);
    }

    private static void TryRestrict(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* a world-readable key file is still better than an open API */ }
    }
}

public sealed record NetworkGate(string ListenUrl, string? Key, X509Certificate2? Certificate);
