using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using MediaTagger.API.Models;
using MediaTagger.API.Services;
using Microsoft.VisualBasic.FileIO;
using System.Diagnostics;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly FileSystemService _fs;
    private readonly TagsService _tags;
    private readonly ThumbnailService _thumbnails;
    private readonly ScanProgress _progress;

    public MediaController(FileSystemService fs, TagsService tags, ThumbnailService thumbnails, ScanProgress progress)
    {
        _fs = fs;
        _tags = tags;
        _thumbnails = thumbnails;
        _progress = progress;
    }

    [HttpGet("files")]
    public ActionResult<List<MediaFile>> GetFiles([FromQuery] string folderPath, [FromQuery] bool recursive = false)
    {
        if (!Directory.Exists(folderPath)) return BadRequest("Folder not found");
        return Ok(_fs.GetMediaFiles(folderPath, recursive));
    }

    [HttpGet("folder-tree")]
    public ActionResult<FolderNode> GetFolderTree([FromQuery] string rootPath)
    {
        if (!Directory.Exists(rootPath)) return BadRequest("Folder not found");
        return Ok(_fs.GetFolderTree(rootPath));
    }

    [HttpPut("tags")]
    public ActionResult UpdateTags([FromBody] UpdateTagsRequest request)
    {
        if (!System.IO.File.Exists(request.FilePath)) return BadRequest("File not found");
        _tags.UpdateFileTags(request.FilePath, request.Tags);
        return Ok();
    }

    /// <summary>Adds tags to a whole selection without disturbing their other fields.</summary>
    [HttpPut("tags/bulk")]
    public ActionResult<int> BulkAddTags([FromBody] BulkTagsRequest request)
    {
        var existing = request.FilePaths.Where(System.IO.File.Exists).ToList();
        if (existing.Count == 0) return BadRequest("None of the files exist");
        return Ok(_tags.AddTagsToFiles(existing, request));
    }

    /// <summary>How far the running scan has got, for the indexing bar.</summary>
    [HttpGet("scan-progress")]
    public ActionResult<ScanProgressSnapshot> GetScanProgress() => Ok(_progress.Snapshot());

    /// <summary>Lets the client skip a remembered folder whose drive is not plugged in.</summary>
    [HttpGet("folder-exists")]
    public ActionResult<bool> FolderExists([FromQuery] string folderPath)
        => Ok(!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath));

    [HttpDelete("file")]
    public ActionResult DeleteFile([FromQuery] string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return NotFound();
        try
        {
            _tags.RemoveFileTags(filePath);

            SendToTrash(filePath);
            _fs.InvalidateFolder(Path.GetDirectoryName(filePath) ?? "");
            return Ok();
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }

    private static void SendToTrash(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            // The same call a PowerShell one-liner would make, without paying for
            // a PowerShell start-up on every delete.
            FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var escapedPath = filePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = "-e \"tell application \\\"Finder\\\" to delete POSIX file \\\"" + escapedPath + "\\\"\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(10000);
        }
        else
        {
            System.IO.File.Delete(filePath);
        }
    }

    [HttpPost("search")]
    public ActionResult<List<MediaFile>> Search([FromBody] SearchRequest request)
    {
        if (FileSystemService.Collapse(
                request.RootPaths is { Count: > 0 } ? request.RootPaths : new[] { request.RootPath }).Length == 0)
            return BadRequest("Root path not found");
        return Ok(_fs.Search(request));
    }

    [HttpGet("all-tags")]
    public ActionResult<AllTagsResponse> GetAllTags([FromQuery] string[] rootPath)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        return Ok(_fs.GetAllTags(roots));
    }

    /// <summary>
    /// Preview image for a file. <paramref name="maxSize"/> bounds the longest edge —
    /// grids should always pass it so a 20 MB photo is not shipped for a 200 px tile.
    /// Omit it (or pass 0) to get the original, which is what the lightbox wants.
    /// </summary>
    [HttpGet("thumbnail")]
    public async Task<IActionResult> GetThumbnail([FromQuery] string filePath, [FromQuery] int maxSize = 0,
        CancellationToken cancel = default)
    {
        if (!System.IO.File.Exists(filePath)) return NotFound();

        if (maxSize > 0) maxSize = Math.Clamp(maxSize, 64, 2048);

        string? cached;
        try { cached = await _thumbnails.GetThumbnailPath(filePath, maxSize, cancel); }
        // The tile this was for is gone; nobody is waiting on an answer.
        catch (OperationCanceledException) { return new EmptyResult(); }
        if (cached != null) return CacheableFile(cached, "image/jpeg");

        // Nothing cached: fine for JPEG/PNG (the browser decodes the original),
        // but HEIC and video are unreadable without the conversion that just failed.
        if (_thumbnails.NeedsConversion(filePath)) return NotFound("Thumbnail unavailable");

        var ext = Path.GetExtension(filePath).ToLower();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"  => "image/png",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            ".bmp"  => "image/bmp",
            _       => "image/jpeg"
        };
        return CacheableFile(filePath, contentType);
    }

    /// <summary>Serves a file with validators so revisits come back as 304 instead of a re-download.</summary>
    private IActionResult CacheableFile(string path, string contentType)
    {
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return NotFound(); }
        catch { return NotFound(); }

        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=86400";
        return PhysicalFile(path, contentType, lastModified: info.LastWriteTimeUtc, entityTag: etag);
    }

    [HttpGet("stream")]
    public IActionResult Stream([FromQuery] string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return NotFound();

        var ext = Path.GetExtension(filePath).ToLower();
        var contentType = ext switch
        {
            ".mp4"            => "video/mp4",
            ".mov"            => "video/mp4",
            ".webm"           => "video/webm",
            ".avi"            => "video/x-msvideo",
            ".mkv"            => "video/x-matroska",
            ".m4v"            => "video/mp4",
            ".wmv"            => "video/x-ms-wmv",
            ".mts" or ".m2ts" => "video/mp2t",
            _                 => "video/mp4"
        };

        return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
    }

    [HttpGet("map-files")]
    public ActionResult<List<MediaFile>> GetFilesWithCoordinates([FromQuery] string[] rootPath)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        var files = _fs.Across(roots).Where(f => f.Tags.Coordinates != null).ToList();
        return Ok(files);
    }

    [HttpGet("library-stats")]
    public ActionResult<LibraryStats> GetLibraryStats([FromQuery] string[] rootPath)
    {
        var roots = FileSystemService.Collapse(rootPath);
        if (roots.Length == 0) return BadRequest("Folder not found");
        return Ok(_fs.GetLibraryStats(roots));
    }
}
