using MediaTagger.API.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace MediaTagger.API.Services;

public class ExifService
{
    public ExifData? ReadExif(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var exif = new ExifData();

            // GPS
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            if (gps != null)
            {
                var location = gps.GetGeoLocation();
                if (location != null)
                {
                    exif.GpsCoordinates = new Coordinates
                    {
                        Lat = location.Latitude,
                        Lng = location.Longitude
                    };
                }
            }

            // Date taken
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                exif.DateTaken = dt;

            // Camera
            var make = ifd0?.GetDescription(ExifDirectoryBase.TagMake);
            var model = ifd0?.GetDescription(ExifDirectoryBase.TagModel);
            if (make != null || model != null)
                exif.Camera = $"{make} {model}".Trim();

            return exif;
        }
        catch
        {
            return null;
        }
    }
}
