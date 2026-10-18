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

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("ElectronPolicy");
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapControllers();

// appsettings.json names the address; the literal only covers a missing config file.
if (string.IsNullOrEmpty(app.Configuration["Kestrel:Endpoints:Http:Url"]))
    app.Run("http://localhost:5001");
else
    app.Run();
