using MediaTagger.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<MediaTagger.API.Controllers.ApiExceptionFilter>();
    options.Filters.Add<MediaTagger.API.Controllers.LibraryScopeFilter>();
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<CacheLocator>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<LibraryScope>();
builder.Services.AddSingleton<ScanProgress>();
builder.Services.AddSingleton<ExifCache>();
builder.Services.AddSingleton<TagsService>();
builder.Services.AddSingleton<LibraryIndex>();
builder.Services.AddSingleton<FileSystemService>();
builder.Services.AddSingleton<MediaRename>();
builder.Services.AddSingleton<ExifService>();
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddSingleton<FaceStore>();
builder.Services.AddSingleton<FaceAnalyzer>();
builder.Services.AddSingleton<FaceIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FaceIndexer>());
builder.Services.AddSingleton<FaceRecognitionService>();
builder.Services.AddSingleton<PlaceSuggestionService>();
builder.Services.AddSingleton<TagBackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TagBackupService>());

// Only the app's own window may call the API from a browser context: a page the user
// happens to have open elsewhere must not be able to talk to it.
var clientOrigins = NetworkAccess.ClientOrigins(builder.Configuration["MediaTagger:ClientOrigins"]);
builder.Services.AddCors(options =>
{
    options.AddPolicy("ElectronPolicy", policy =>
        policy.WithOrigins(clientOrigins).AllowAnyHeader().AllowAnyMethod());
});

// Localhost stays open. Any other listen address is the NAS case: HTTPS plus one key.
var gate = NetworkAccess.Prepare(
    builder.Environment.ContentRootPath,
    builder.Configuration["MediaTagger:ListenUrl"],
    builder.Configuration["MediaTagger:CacheRoot"],
    builder.Configuration["MediaTagger:AccessKey"]);

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = null;
    if (gate.Certificate != null)
        opts.ConfigureHttpsDefaults(https => https.ServerCertificate = gate.Certificate);
});
builder.WebHost.UseUrls(gate.ListenUrl);

// A name that is not ours in the Host header is how a web page rebinds its own address
// onto this machine. On localhost only localhost names are served; on a network address
// the HTTPS certificate and the key already stand in the way.
var allowedHosts = builder.Configuration["AllowedHosts"];
if (gate.Key is null && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Trim() == "*"))
    builder.Configuration["AllowedHosts"] = NetworkAccess.LoopbackHosts;

var app = builder.Build();

// Swagger is a map of every route. Keep it for the local app; the NAS does not serve it.
if (gate.Key is null)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("ElectronPolicy");
NetworkAccess.UseKeyCheck(app, gate.Key);
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { ok = true, auth = gate.Key != null, features = ApiFeatures.All }));
app.MapControllers();
app.Run();
