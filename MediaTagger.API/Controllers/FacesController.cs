using Microsoft.AspNetCore.Mvc;
using MediaTagger.API.Models;
using MediaTagger.API.Services;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FacesController : ControllerBase
{
    private const int DefaultPerPerson = 120;

    private readonly FileSystemService _fs;
    private readonly TagsService _tags;
    private readonly FaceStore _store;
    private readonly FaceIndexer _indexer;
    private readonly FaceRecognitionService _recognition;
    private readonly ThumbnailService _thumbnails;

    public FacesController(FileSystemService fs, TagsService tags, FaceStore store,
        FaceIndexer indexer, FaceRecognitionService recognition, ThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
        _fs = fs;
        _tags = tags;
        _store = store;
        _indexer = indexer;
        _recognition = recognition;
    }

    /// <summary>Starts finding faces across the library; files already done are skipped.</summary>
    [HttpPost("scan")]
    public ActionResult<FaceScanStatus> Scan([FromQuery] string[] rootPath)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        _indexer.Start(roots);
        return Ok(_indexer.Status());
    }

    [HttpPost("stop")]
    public ActionResult<FaceScanStatus> Stop()
    {
        _indexer.Stop();
        return Ok(_indexer.Status());
    }

    [HttpGet("status")]
    public ActionResult<FaceScanStatus> Status() => Ok(_indexer.Status());

    /// <summary>Photos where a known person seems to appear but is not tagged.</summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult<FaceSuggestionsResponse>> Suggestions([FromQuery] string[] rootPath,
        [FromQuery] int perPerson = DefaultPerPerson)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        return Ok(await _recognition.GetSuggestionsAsync(roots, Math.Clamp(perPerson, 1, 1000)));
    }

    /// <summary>Faces in one file, analysed on the spot if the scan has not got to it yet.</summary>
    [HttpGet("file")]
    public async Task<ActionResult<FileFacesResponse>> FileFaces([FromQuery] string[] rootPath, [FromQuery] string filePath)
    {
        var roots = FileSystemService.Collapse(rootPath);
        var folder = Path.GetDirectoryName(filePath) ?? "";
        if (!System.IO.File.Exists(filePath) || roots.Length == 0) return NotFound();

        var file = _fs.GetMediaFiles(folder)
            .FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (file == null) return NotFound();

        return Ok(await _recognition.GetFileFacesAsync(roots, file));
    }

    /// <summary>A square picture of one face, cut from the file's thumbnail.</summary>
    [HttpGet("crop")]
    public async Task<IActionResult> Crop([FromQuery] string filePath, [FromQuery] float x, [FromQuery] float y,
        [FromQuery] float w, [FromQuery] float h, [FromQuery] int size = 96, CancellationToken cancel = default)
    {
        if (!System.IO.File.Exists(filePath)) return NotFound();
        byte[]? bytes;
        try { bytes = await _thumbnails.GetFaceCropAsync(filePath, x, y, w, h, Math.Clamp(size, 32, 512), cancel); }
        catch (OperationCanceledException) { return new EmptyResult(); }
        if (bytes == null) return NotFound();

        // The query names the file and the box; it only changes when the file does.
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(bytes, "image/jpeg");
    }

    /// <summary>Confirms suggestions: remembers each face as that person, and tags the files.</summary>
    [HttpPost("accept")]
    public ActionResult<int> Accept([FromBody] AcceptFacesRequest request)
    {
        var items = request.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Person) && System.IO.File.Exists(i.FilePath))
            .ToList();
        if (items.Count == 0) return BadRequest("Nothing to accept");

        foreach (var item in items) _store.SetLabel(item.FaceId, item.Person.Trim());

        if (request.ApplyTags)
        {
            foreach (var group in items.GroupBy(i => i.Person.Trim(), StringComparer.OrdinalIgnoreCase))
                _tags.AddTagsToFiles(group.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase),
                    new BulkTagsRequest { People = new List<string> { group.Key } });
        }

        return Ok(items.Count);
    }

    /// <summary>Renames people, or folds several names into one. Tags under the roots and confirmed faces both move.</summary>
    [HttpPost("people")]
    public ActionResult<int> RenamePeople([FromBody] RenamePeopleRequest request)
    {
        var to = request.To?.Trim() ?? "";
        var from = (request.From ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (to.Length == 0 || from.Count == 0) return BadRequest("Name required");
        if (from.All(n => n.Equals(to, StringComparison.Ordinal))) return Ok(0);

        var folders = _tags.RenamePeople(request.RootPaths ?? new List<string>(), from, to);
        _store.RenamePeople(from, to);
        return Ok(folders);
    }

    /// <summary>Turns down suggestions, so the same face is not offered as that person again.</summary>
    [HttpPost("reject")]
    public ActionResult<int> Reject([FromBody] List<FaceDecision> items)
    {
        foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Person)))
            _store.Reject(item.FaceId, item.Person.Trim());
        return Ok(items.Count);
    }
}
