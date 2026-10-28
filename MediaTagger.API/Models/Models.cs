using System.Text.Json.Serialization;

namespace MediaTagger.API.Models;

public class AppSettings
{
    public string DefaultFolder { get; set; } = "";
    public List<string> RecentFolders { get; set; } = new();
    /// <summary>Photo folders indexed together. Faces, places and the library counts share one database.</summary>
    public List<string> LibraryFolders { get; set; } = new();
    /// <summary>How the face scan looks at videos.</summary>
    public VideoFaceMode VideoFaces { get; set; } = VideoFaceMode.Frames;
    /// <summary>CPU threads the face scan may use; 0 picks a share that leaves the machine usable.</summary>
    public int FaceThreads { get; set; }
    /// <summary>Find faces on the graphics card (DirectML, Windows only); falls back to the CPU.</summary>
    public bool FaceGpu { get; set; }
}

/// <summary>How much of a video face analysis looks at; each step costs more time per video.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VideoFaceMode>))]
public enum VideoFaceMode
{
    /// <summary>Videos are skipped; whatever was found in them before is kept.</summary>
    Off,
    /// <summary>Only the cover frame the grid shows.</summary>
    Cover,
    /// <summary>A frame every couple of seconds across the whole clip.</summary>
    Frames,
    /// <summary>A frame every second, for more angles of each person; slower.</summary>
    Detailed
}

public class MediaFile
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Type { get; set; } = "image"; // image | video
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public FileTags Tags { get; set; } = new();
    public ExifData? Exif { get; set; }
}

public class FileTags
{
    public List<string> People { get; set; } = new();
    /// <summary>
    /// Who took the photo: there, but not in the frame. Kept apart from <see cref="People"/>
    /// so face recognition never looks for their face in it.
    /// </summary>
    public string Photographer { get; set; } = "";
    public string Place { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public List<string> Albums { get; set; } = new();
    public Coordinates? Coordinates { get; set; }
    public string Notes { get; set; } = "";
    // Editable capture date. Auto-populated from EXIF the first time a file is read
    // (see FileSystemService.GetMediaFiles) so the UI always has a value to show and
    // the user can override it — e.g. when EXIF is missing or wrong.
    public DateTime? DateTaken { get; set; }
}

public class Coordinates
{
    public double Lat { get; set; }
    public double Lng { get; set; }
}

public class ExifData
{
    public Coordinates? GpsCoordinates { get; set; }
    public DateTime? DateTaken { get; set; }
    public string? Camera { get; set; }
}

public class TagsFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, FileTags> Files { get; set; } = new();
}

public class FolderNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<FolderNode> Children { get; set; } = new();
}

public class UpdateTagsRequest
{
    public string FilePath { get; set; } = "";
    public FileTags Tags { get; set; } = new();
}

/// <summary>
/// Adds tags to many files at once. Only the listed values are touched — each file
/// keeps its own notes, coordinates and capture date, which a plain overwrite would
/// flatten across the whole selection.
/// </summary>
public class BulkTagsRequest
{
    public List<string> FilePaths { get; set; } = new();
    public List<string> People { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<string> Albums { get; set; } = new();
    /// <summary>Applied only when non-empty; blank leaves each file's own place alone.</summary>
    public string? Place { get; set; }
    /// <summary>Like <see cref="Place"/>: blank leaves each file's own photographer alone.</summary>
    public string? Photographer { get; set; }
}

public class SearchRequest
{
    public string? Query { get; set; }
    public List<string>? People { get; set; }
    public string? Place { get; set; }
    public List<string>? Albums { get; set; }
    public List<string>? Groups { get; set; }
    public string RootPath { get; set; } = "";
    /// <summary>When set, the search covers each of these instead of <see cref="RootPath"/> alone.</summary>
    public List<string> RootPaths { get; set; } = new();
}

public class AllTagsResponse
{
    public List<string> People { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<string> Albums { get; set; } = new();
    public List<string> Places { get; set; } = new();
}

public class LibraryStats
{
    public List<AlbumStat> Albums { get; set; } = new();
    public List<PersonStat> People { get; set; } = new();
    public List<PlaceStat> Places { get; set; } = new();
    public List<GroupStat> Groups { get; set; } = new();
}

public class AlbumStat  { public string Name { get; set; } = ""; public int Count { get; set; } public string? CoverPath { get; set; } }
public class PersonStat { public string Name { get; set; } = ""; public int Count { get; set; } public string? CoverPath { get; set; } }
public class PlaceStat  { public string Name { get; set; } = ""; public int Count { get; set; } public string? CoverPath { get; set; } }
public class GroupStat  { public string Name { get; set; } = ""; public int Count { get; set; } public string? CoverPath { get; set; } }

/// <summary>A face in a specific file, with enough to crop it out of that file's thumbnail.</summary>
public class FaceSuggestion
{
    public long FaceId { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Type { get; set; } = "image";
    /// <summary>Box relative (0..1) to the upright picture.</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    /// <summary>For a video, the frame (seconds in) the box is on; null for a photo.</summary>
    public double? Time { get; set; }
    /// <summary>Pixel size the box was measured on; the UI needs the aspect ratio to crop square.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Who it is probably, and how similar (cosine, roughly 0.45 likely … 0.8 certain).</summary>
    public string Person { get; set; } = "";
    public float Score { get; set; }
}

public class PersonSuggestions
{
    public string Person { get; set; } = "";
    /// <summary>All suggestions for this person; <see cref="Items"/> may hold only the best of them.</summary>
    public int Total { get; set; }
    public List<FaceSuggestion> Items { get; set; } = new();
}

public class LearnedPerson
{
    public string Name { get; set; } = "";
    /// <summary>Faces the model has placed as this person.</summary>
    public int Faces { get; set; }
    public FaceSuggestion? Cover { get; set; }
}

public class FaceSuggestionsResponse
{
    public List<PersonSuggestions> People { get; set; } = new();
    public List<LearnedPerson> Learned { get; set; } = new();
    public int FacesTotal { get; set; }
}

public class FileFace
{
    public long FaceId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    /// <summary>For a video, the frame (seconds in) the box is on; null for a photo.</summary>
    public double? Time { get; set; }
    /// <summary>Who this is, when the tags (or the user) settled it.</summary>
    public string? Person { get; set; }
    /// <summary>Who this might be — someone not tagged on the file yet.</summary>
    public string? Suggestion { get; set; }
    public float Score { get; set; }
}

public class FaceDeviceInfo
{
    public int ProcessorCount { get; set; }
    /// <summary>Threads used when the setting is left on automatic.</summary>
    public int AutoThreads { get; set; }
    /// <summary>Threads in use now.</summary>
    public int Threads { get; set; }
    /// <summary>False where there is no DirectML (anything but Windows).</summary>
    public bool GpuSupported { get; set; }
    public bool UsingGpu { get; set; }
    /// <summary>Why the graphics card was asked for but not used.</summary>
    public string? GpuError { get; set; }
}

public class FileFacesResponse
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<FileFace> Faces { get; set; } = new();
}

public class FaceDecision
{
    public long FaceId { get; set; }
    public string FilePath { get; set; } = "";
    public string Person { get; set; } = "";
}

/// <summary>Every name in <see cref="From"/> becomes <see cref="To"/>, in tags and on confirmed faces.</summary>
public class RenamePeopleRequest
{
    public List<string> RootPaths { get; set; } = new();
    public List<string> From { get; set; } = new();
    public string To { get; set; } = "";
}

public class AcceptFacesRequest
{
    public List<FaceDecision> Items { get; set; } = new();
    /// <summary>
    /// Also add the person to each file's tags. The side panel writes tags itself and
    /// passes false; the suggestions view relies on this.
    /// </summary>
    public bool ApplyTags { get; set; } = true;
}

public class PlaceCandidate
{
    public string Place { get; set; } = "";
    /// <summary>Share (0..1) of the nearby evidence that points at this place.</summary>
    public double Confidence { get; set; }
    /// <summary>Metres to the closest photo already tagged with this place.</summary>
    public double Distance { get; set; }
    /// <summary>How many of its photos are close enough to count.</summary>
    public int Support { get; set; }
    /// <summary>Clear winner with a photo of it right there — safe to apply without looking.</summary>
    public bool Confident { get; set; }
}

public class PlaceSuggestion
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Type { get; set; } = "image";
    public string Place { get; set; } = "";
    public double Confidence { get; set; }
    public double Distance { get; set; }
    public bool Confident { get; set; }
    /// <summary>Runners-up, for spots where several places sit on top of each other.</summary>
    public List<string> Alternatives { get; set; } = new();
}

public class PlaceSuggestionGroup
{
    public string Place { get; set; } = "";
    public List<PlaceSuggestion> Items { get; set; } = new();
}

public class PlaceSuggestionsResponse
{
    public List<PlaceSuggestionGroup> Places { get; set; } = new();
    /// <summary>Places with at least one geotagged photo — what suggestions are made from.</summary>
    public int KnownPlaces { get; set; }
    /// <summary>Geotagged photos with no place yet, whether or not anything is near them.</summary>
    public int Unplaced { get; set; }
}
