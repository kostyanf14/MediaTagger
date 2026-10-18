using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MediaTagger.API.Models;
using Microsoft.Data.Sqlite;

namespace MediaTagger.API.Services;

/// <summary>
/// Remembers what <see cref="ExifService"/> found, so a library scan does not re-parse
/// every file. Reading metadata out of 17k files takes seconds; looking it up takes
/// milliseconds, and the answer only changes when the file itself does.
/// </summary>
public class ExifCache : IDisposable
{
    private const string CacheFolderName = ".cache";
    private const string DatabaseFileName = "exif.db";

    private readonly SqliteConnection _connection;
    private readonly ILogger<ExifCache> _logger;
    private readonly object _gate = new();

    // Volume serials never change, so each root only has to be asked once.
    private readonly ConcurrentDictionary<string, string> _volumeIds = new(StringComparer.OrdinalIgnoreCase);

    // The whole table, mirrored. A library scan asks about every file it sees, and a
    // dictionary answers that without a query (or a lock) per file; the database is
    // only there so the answers survive a restart.
    private readonly ConcurrentDictionary<(string Volume, string Path), Entry> _entries = new();

    private readonly SqliteCommand _upsertCommand;

    private readonly record struct Entry(long Size, long Mtime, ExifData? Data);

    private long _hits;
    private long _misses;

    public ExifCache(CacheLocator locator, ILogger<ExifCache> logger)
    {
        _logger = logger;
        var path = locator.GetFilePath(CacheFolderName, DatabaseFileName);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString());
        _connection.Open();

        // WAL keeps readers off the writer's back; NORMAL skips an fsync per commit,
        // which is the difference between a fast scan and a slow one. Losing the tail
        // of the cache after a crash only costs a re-read of those files.
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=NORMAL");
        Execute("""
            CREATE TABLE IF NOT EXISTS exif (
                volume     TEXT    NOT NULL,
                path       TEXT    NOT NULL,
                size       INTEGER NOT NULL,
                mtime      INTEGER NOT NULL,
                present    INTEGER NOT NULL,
                date_taken INTEGER NULL,
                lat        REAL    NULL,
                lng        REAL    NULL,
                camera     TEXT    NULL,
                PRIMARY KEY (volume, path)
            )
            """);

        _upsertCommand = _connection.CreateCommand();
        _upsertCommand.CommandText = """
            INSERT INTO exif (volume, path, size, mtime, present, date_taken, lat, lng, camera)
            VALUES ($v, $p, $size, $mtime, $present, $date, $lat, $lng, $camera)
            ON CONFLICT(volume, path) DO UPDATE SET
                size = $size, mtime = $mtime, present = $present,
                date_taken = $date, lat = $lat, lng = $lng, camera = $camera
            """;
        foreach (var name in new[] { "$v", "$p", "$size", "$mtime", "$present", "$date", "$lat", "$lng", "$camera" })
            _upsertCommand.Parameters.Add(name, SqliteType.Text);

        LoadEntries();
        _logger.LogInformation("EXIF cache: {Path} ({Rows} entries)", path, _entries.Count);
    }

    /// <summary>
    /// Metadata for a file, read from the cache when the file is unchanged and parsed
    /// (then remembered) when it is not.
    /// </summary>
    public ExifData? GetOrRead(string filePath, FileInfo info, Func<string, ExifData?> read)
    {
        var key = BuildKey(filePath);
        var mtime = info.LastWriteTimeUtc.Ticks;

        // A file that changed size or timestamp has to be read again.
        if (_entries.TryGetValue(key, out var cached) && cached.Size == info.Length && cached.Mtime == mtime)
        {
            Interlocked.Increment(ref _hits);
            return cached.Data;
        }

        // Parsing happens outside any lock: it is the slow part, and two threads doing
        // it for the same file is merely wasteful, never wrong.
        var fresh = read(filePath);
        Interlocked.Increment(ref _misses);

        // A remembered "this file has no readable metadata" is worth just as much as
        // a remembered date — it saves the same failed parse next time.
        _entries[key] = new Entry(info.Length, mtime, fresh);

        lock (_gate)
        {
            try { Store(key.Volume, key.Path, info.Length, mtime, fresh); }
            catch (Exception ex) { _logger.LogDebug("Could not cache metadata for {Path}: {Msg}", filePath, ex.Message); }
        }

        return fresh;
    }

    private void LoadEntries()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT volume, path, size, mtime, present, date_taken, lat, lng, camera FROM exif";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ExifData? data = null;
            if (reader.GetInt64(4) != 0)
            {
                data = new ExifData
                {
                    DateTaken = reader.IsDBNull(5) ? null : new DateTime(reader.GetInt64(5), DateTimeKind.Unspecified),
                    GpsCoordinates = reader.IsDBNull(6) || reader.IsDBNull(7)
                        ? null
                        : new Coordinates { Lat = reader.GetDouble(6), Lng = reader.GetDouble(7) },
                    Camera = reader.IsDBNull(8) ? null : reader.GetString(8)
                };
            }
            _entries[(reader.GetString(0), reader.GetString(1))] =
                new Entry(reader.GetInt64(2), reader.GetInt64(3), data);
        }
    }

    private void Store(string volume, string path, long size, long mtime, ExifData? data)
    {
        var p = _upsertCommand.Parameters;
        p["$v"].Value = volume;
        p["$p"].Value = path;
        p["$size"].Value = size;
        p["$mtime"].Value = mtime;
        p["$present"].Value = data != null ? 1 : 0;
        p["$date"].Value = (object?)data?.DateTaken?.Ticks ?? DBNull.Value;
        p["$lat"].Value = (object?)data?.GpsCoordinates?.Lat ?? DBNull.Value;
        p["$lng"].Value = (object?)data?.GpsCoordinates?.Lng ?? DBNull.Value;
        p["$camera"].Value = (object?)data?.Camera ?? DBNull.Value;
        _upsertCommand.ExecuteNonQuery();
    }

    /// <summary>
    /// Splits a path into the volume it lives on and the part below that volume's root.
    /// Keying on the volume's serial rather than its drive letter means an external disk
    /// that comes back as G: instead of E: still finds its own entries.
    /// </summary>
    private (string Volume, string Path) BuildKey(string filePath)
    {
        string full;
        try { full = Path.GetFullPath(filePath); } catch { full = filePath; }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return ("", full.ToLowerInvariant());

        var volume = _volumeIds.GetOrAdd(root, ReadVolumeId);
        var relative = full[root.Length..];
        return (volume, relative.ToLowerInvariant());
    }

    private static string ReadVolumeId(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var label = new StringBuilder(261);
                var fileSystem = new StringBuilder(261);
                if (GetVolumeInformation(root, label, label.Capacity, out var serial,
                        out _, out _, fileSystem, fileSystem.Capacity) && serial != 0)
                    return serial.ToString("X8");
            }
            catch { /* fall through to the path form below */ }
        }

        // Network shares and anything without a serial fall back to the root itself,
        // which is stable enough as long as the share is mounted the same way.
        return root.ToLowerInvariant();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _logger.LogInformation("EXIF cache stopping: {Hits} hits, {Misses} misses", _hits, _misses);
        _upsertCommand.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
