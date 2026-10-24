using MediaTagger.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<CacheLocator>();
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

// Allow large video files to be streamed
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = null;
});

// Bind address. appsettings and MediaTagger__ListenUrl override this; localhost
// only accepts clients on the same machine, so a NAS uses http://0.0.0.0:5001.
var listenUrl = builder.Configuration["MediaTagger:ListenUrl"];
if (string.IsNullOrWhiteSpace(listenUrl))
    listenUrl = "http://localhost:5001";
builder.WebHost.UseUrls(listenUrl);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("ElectronPolicy");
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapControllers();
app.Run();
