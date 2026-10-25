using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsController(IConfiguration config)
    {
        var configured = config["MediaTagger:SettingsPath"];
        _settingsPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaTagger", "settings.json")
            : configured;
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists(_settingsPath))
            {
                var json = System.IO.File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    private void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        System.IO.File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    [HttpGet]
    public ActionResult<AppSettings> Get() => Ok(LoadSettings());

    [HttpPut]
    public ActionResult Put([FromBody] AppSettings settings)
    {
        SaveSettings(settings);
        return Ok();
    }
}

public class AppSettings
{
    public string DefaultFolder { get; set; } = "";
    public List<string> RecentFolders { get; set; } = new();
    /// <summary>Photo folders indexed together. Faces, places and the library counts share one database.</summary>
    public List<string> LibraryFolders { get; set; } = new();
}
