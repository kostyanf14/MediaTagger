using Microsoft.AspNetCore.Mvc;
using MediaTagger.API.Models;
using MediaTagger.API.Services;

namespace MediaTagger.API.Controllers;

/// <summary>
/// Place suggestions from nearby photos. Accepting one is an ordinary tag write
/// (PUT /api/media/tags/bulk with a place), so there is nothing to accept here.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PlacesController : ControllerBase
{
    private readonly PlaceSuggestionService _places;

    public PlacesController(PlaceSuggestionService places) => _places = places;

    [HttpGet("suggestions")]
    public ActionResult<PlaceSuggestionsResponse> Suggestions([FromQuery] string[] rootPath)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        return Ok(_places.GetSuggestions(roots));
    }

    [HttpGet("nearby")]
    public ActionResult<List<PlaceCandidate>> Nearby([FromQuery] string[] rootPath, [FromQuery] double lat, [FromQuery] double lng)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        if (Math.Abs(lat) > 90 || Math.Abs(lng) > 180) return BadRequest("Coordinates out of range");
        return Ok(_places.GetNearby(roots, lat, lng));
    }
}
