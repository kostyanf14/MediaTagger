using MediaTagger.API.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace MediaTagger.API.Services;

public class ExifService
{
    // QuickTime counts seconds from 1904. A file whose creation atom was never
    // filled in reports exactly that epoch, and taking it at face value would park
    // the clip decades before every real photo in the library.
    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

            exif.DateTaken = ReadCaptureDate(directories);

            // Camera
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
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

    /// <summary>
    /// Capture date, most trustworthy source first. DateTimeOriginal is the moment the
    /// shutter fired and lives in the Exif <b>SubIFD</b> — IFD0 only carries the file's
    /// own Date/Time, which editors rewrite on save.
    /// </summary>
    private static DateTime? ReadCaptureDate(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd != null)
        {
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
                return original;
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                return digitized;
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var fileDateTime))
            return fileDateTime;

        // Video has no EXIF; the date sits in QuickTime atoms instead. The metadata
        // header records local wall-clock time, which is what the camera displayed.
        var quickTimeMeta = directories.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();
        if (quickTimeMeta != null &&
            quickTimeMeta.TryGetDateTime(QuickTimeMetadataHeaderDirectory.TagCreationDate, out var localCreated)
            && IsSet(localCreated.ToUniversalTime()))
            return localCreated;

        // The movie header is defined as UTC, so it needs converting before it can be
        // compared against the local timestamps above.
        var movieHeader = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (movieHeader != null &&
            movieHeader.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var utcCreated))
        {
            var utc = DateTime.SpecifyKind(utcCreated, DateTimeKind.Utc);
            if (IsSet(utc)) return utc.ToLocalTime();
        }

        return null;
    }

    /// <summary>Rejects the QuickTime "never written" epoch, and anything at or before it.</summary>
    private static bool IsSet(DateTime utc) => utc - QuickTimeEpoch > TimeSpan.FromDays(1);
}
