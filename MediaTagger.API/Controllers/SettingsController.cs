using Microsoft.AspNetCore.Mvc;
using MediaTagger.API.Models;
using MediaTagger.API.Services;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly TagBackupService _backup;

    public SettingsController(SettingsService settings, TagBackupService backup)
    {
        _settings = settings;
        _backup = backup;
    }

    [HttpGet]
    public ActionResult<AppSettings> Get() => Ok(_settings.Get());

    [HttpPut]
    public ActionResult Put([FromBody] AppSettings settings)
    {
        _settings.Save(settings);
        return Ok();
    }

    /// <summary>Zips every tags file and the face database into the cache folder now.</summary>
    [HttpPost("backup")]
    public async Task<ActionResult<TagBackupService.Result>> Backup()
    {
        var result = await _backup.BackupNowAsync();
        return result == null ? BadRequest("No library folders") : Ok(result);
    }
}
