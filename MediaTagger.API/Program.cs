using MediaTagger.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<CacheLocator>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ScanProgress>();
builder.Services.AddSingleton<ExifCache>();
builder.Services.AddSingleton<TagsService>();
builder.Services.AddSingleton<LibraryIndex>();
builder.Services.AddSingleton<FileSystemService>();
builder.Services.AddSingleton<ExifService>();
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddSingleton<FaceStore>();
builder.Services.AddSingleton<FaceAnalyzer>();
builder.Services.AddSingleton<FaceIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FaceIndexer>());
builder.Services.AddSingleton<FaceRecognitionService>();
builder.Services.AddSingleton<PlaceSuggestionService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("ElectronPolicy", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
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
app.MapGet("/health", () => Results.Ok(new { ok = true, auth = gate.Key != null }));
app.MapControllers();
app.Run();
