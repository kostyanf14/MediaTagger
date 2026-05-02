namespace MediaTagger.API.Models;

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
    public string Place { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public List<string> Albums { get; set; } = new();
    public Coordinates? Coordinates { get; set; }
    public string Notes { get; set; } = "";
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

public class SearchRequest
{
    public string? Query { get; set; }
    public List<string>? People { get; set; }
    public string? Place { get; set; }
    public List<string>? Albums { get; set; }
    public List<string>? Groups { get; set; }
    public string RootPath { get; set; } = "";
}

public class AllTagsResponse
{
    public List<string> People { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<string> Albums { get; set; } = new();
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
