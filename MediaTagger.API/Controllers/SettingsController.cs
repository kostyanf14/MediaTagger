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
    private readonly ThumbnailService _thumbnails;

    public SettingsController(SettingsService settings, TagBackupService backup, ThumbnailService thumbnails)
    {
        _settings = settings;
        _backup = backup;
        _thumbnails = thumbnails;
    }

    /// <summary>How much the preview cache holds and the most it may.</summary>
    [HttpGet("thumbnail-cache")]
    public ActionResult<object> ThumbnailCache()
    {
        var (bytes, files, limit) = _thumbnails.CacheInfo();
        return Ok(new { bytes, files, limitBytes = limit });
    }

    /// <summary>Empties the preview cache; previews are made again as they are shown.</summary>
    [HttpDelete("thumbnail-cache")]
    public ActionResult<int> ClearThumbnailCache() => Ok(_thumbnails.ClearCache());

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
