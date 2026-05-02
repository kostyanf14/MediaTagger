using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MediaTagger", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private AppSettings LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists(SettingsPath))
            {
                var json = System.IO.File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    private void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        System.IO.File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
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
}
