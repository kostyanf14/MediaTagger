using MediaTagger.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<TagsService>();
builder.Services.AddSingleton<FileSystemService>();
builder.Services.AddSingleton<ExifService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("ElectronPolicy", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("ElectronPolicy");
app.UseStaticFiles();

// Simple health check endpoint for wait-on
app.MapGet("/health", () => Results.Ok("ok"));

app.MapControllers();

app.Run("http://localhost:5001");
