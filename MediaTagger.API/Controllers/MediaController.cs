using Microsoft.AspNetCore.Mvc;
using MediaTagger.API.Models;
using MediaTagger.API.Services;

namespace MediaTagger.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly FileSystemService _fs;
    private readonly TagsService _tags;

    public MediaController(FileSystemService fs, TagsService tags)
    {
        _fs = fs;
        _tags = tags;
    }

    [HttpGet("files")]
    public ActionResult<List<MediaFile>> GetFiles([FromQuery] string folderPath, [FromQuery] bool recursive = false)
    {
        if (!Directory.Exists(folderPath))
            return BadRequest("Folder not found");
        return Ok(_fs.GetMediaFiles(folderPath, recursive));
    }

    [HttpGet("folder-tree")]
    public ActionResult<FolderNode> GetFolderTree([FromQuery] string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return BadRequest("Folder not found");
        return Ok(_fs.GetFolderTree(rootPath));
    }

    [HttpPut("tags")]
    public ActionResult UpdateTags([FromBody] UpdateTagsRequest request)
    {
        if (!System.IO.File.Exists(request.FilePath))
            return BadRequest("File not found");
        _tags.UpdateFileTags(request.FilePath, request.Tags);
        return Ok();
    }

    [HttpDelete("file")]
    public ActionResult DeleteFile([FromQuery] string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return NotFound("File not found");
        try
        {
            // Remove from _tags.json first
            var folder = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath);
            var tagsFile = _tags.ReadTagsFile(folder);
            if (tagsFile.Files.ContainsKey(fileName))
            {
                tagsFile.Files.Remove(fileName);
                _tags.WriteTagsFile(folder, tagsFile);
            }
            // Move to recycle bin / trash via shell (safer than File.Delete)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                filePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("search")]
    public ActionResult<List<MediaFile>> Search([FromBody] SearchRequest request)
    {
        if (!Directory.Exists(request.RootPath))
            return BadRequest("Root path not found");
        return Ok(_fs.Search(request));
    }

    [HttpGet("all-tags")]
    public ActionResult<AllTagsResponse> GetAllTags([FromQuery] string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return BadRequest("Folder not found");
        return Ok(_tags.GetAllTags(rootPath));
    }

    [HttpGet("thumbnail")]
    public IActionResult GetThumbnail([FromQuery] string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        var ext = Path.GetExtension(filePath).ToLower();

        // Convert HEIC to JPEG on-the-fly for preview
        if (ext == ".heic" || ext == ".heif")
        {
            try
            {
                var converted = ConvertHeicToJpeg(filePath);
                return File(converted, "image/jpeg");
            }
            catch
            {
                return NotFound("HEIC conversion failed");
            }
        }

        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".bmp"            => "image/bmp",
            ".mp4"            => "video/mp4",
            ".mov"            => "video/quicktime",
            ".webm"           => "video/webm",
            _                 => "application/octet-stream"
        };

        return PhysicalFile(filePath, contentType);
    }

    private byte[] ConvertHeicToJpeg(string heicPath)
    {
        // Use ImageMagick if available, otherwise return placeholder
        var tempJpeg = Path.GetTempFileName() + ".jpg";
        try
        {
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "magick",
                    Arguments = $"\"{heicPath}\" \"{tempJpeg}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(5000);
            if (System.IO.File.Exists(tempJpeg))
                return System.IO.File.ReadAllBytes(tempJpeg);
        }
        finally
        {
            if (System.IO.File.Exists(tempJpeg))
                System.IO.File.Delete(tempJpeg);
        }
        throw new Exception("Conversion failed");
    }

    [HttpGet("map-files")]
    public ActionResult<List<MediaFile>> GetFilesWithCoordinates([FromQuery] string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return BadRequest("Folder not found");

        var files = _fs.GetMediaFiles(rootPath, recursive: true)
            .Where(f => f.Tags.Coordinates != null)
            .ToList();

        return Ok(files);
    }

    [HttpGet("library-stats")]
    public ActionResult<LibraryStats> GetLibraryStats([FromQuery] string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return BadRequest("Folder not found");
        return Ok(_tags.GetLibraryStats(rootPath));
    }
}
