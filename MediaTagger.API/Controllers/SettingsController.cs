using Microsoft.AspNetCore.Mvc;
using MediaTagger.API.Models;
using MediaTagger.API.Services;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;

    public SettingsController(SettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public ActionResult<AppSettings> Get() => Ok(_settings.Get());

    [HttpPut]
    public ActionResult Put([FromBody] AppSettings settings)
    {
        _settings.Save(settings);
        return Ok();
    }
}
