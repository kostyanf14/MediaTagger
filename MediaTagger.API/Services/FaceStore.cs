using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace MediaTagger.API.Services;

/// <summary>One face found in a picture. The box is relative (0..1) to the oriented image.</summary>
public sealed record StoredFace(long Id, float X, float Y, float W, float H, float Score, float[] Embedding);

/// <summary>What analysing one version of a file found. An empty list is a valid answer.</summary>
public sealed record StoredFileFaces(long Size, long Mtime, int Width, int Height, StoredFace[] Faces);

/// <summary>
/// Remembers the faces found in each file, plus what the user has said about them.
/// Finding faces costs a fraction of a second per photo, so — like the EXIF cache —
/// it happens once per version of a file and survives restarts. Everything is
/// mirrored in memory: building suggestions touches every face in the library.
/// </summary>
public class FaceStore : IDisposable
{
    private const string CacheFolderName = ".cache";
    private const string DatabaseFileName = "faces.db";

    private readonly SqliteConnection _connection;
    private readonly ILogger<FaceStore> _logger;
    private readonly object _gate = new();

    private readonly ConcurrentDictionary<(string Volume, string Path), StoredFileFaces> _files = new();
    // Faces the user confirmed as a given person (accepted suggestions).
    private readonly ConcurrentDictionary<long, string> _labels = new();
    // Suggestions the user turned down; never offered again for that face.
    private readonly ConcurrentDictionary<(long FaceId, string Person), byte> _rejections = new();

    private long _version;

    /// <summary>Bumped on every change, so derived data knows when to rebuild.</summary>
    public long Version => Interlocked.Read(ref _version);

    public FaceStore(CacheLocator locator, ILogger<FaceStore> logger)
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

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=NORMAL");
        Execute("""
            CREATE TABLE IF NOT EXISTS files (
                volume  TEXT    NOT NULL,
                path    TEXT    NOT NULL,
                size    INTEGER NOT NULL,
                mtime   INTEGER NOT NULL,
                width   INTEGER NOT NULL,
                height  INTEGER NOT NULL,
                PRIMARY KEY (volume, path)
            )
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS faces (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                volume    TEXT    NOT NULL,
                path      TEXT    NOT NULL,
                x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL,
                score     REAL    NOT NULL,
                embedding BLOB    NOT NULL
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS faces_file ON faces (volume, path)");
        Execute("CREATE TABLE IF NOT EXISTS labels (face_id INTEGER PRIMARY KEY, person TEXT NOT NULL)");
        Execute("""
            CREATE TABLE IF NOT EXISTS rejections (
                face_id INTEGER NOT NULL,
                person  TEXT    NOT NULL,
                PRIMARY KEY (face_id, person)
            )
            """);

        Load();
        _logger.LogInformation("Face store: {Path} ({Files} files, {Faces} faces)",
            path, _files.Count, _files.Values.Sum(f => f.Faces.Length));
    }

    /// <summary>The faces of a file, if it was analysed in exactly this version.</summary>
    public StoredFileFaces? TryGet(string filePath, long size, long mtime)
    {
        return _files.TryGetValue(VolumeKey.For(filePath), out var known) && known.Size == size && known.Mtime == mtime
            ? known
            : null;
    }

    /// <summary>Replaces whatever was known about a file. Old faces take their labels with them.</summary>
    public StoredFileFaces Save(string filePath, long size, long mtime, int width, int height,
        IReadOnlyList<(float X, float Y, float W, float H, float Score, float[] Embedding)> faces)
    {
        var key = VolumeKey.For(filePath);
        var stored = new StoredFace[faces.Count];

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();

            var oldIds = new List<long>();
            using (var select = Command("SELECT id FROM faces WHERE volume = $v AND path = $p", tx))
            {
                select.Parameters.AddWithValue("$v", key.Volume);
                select.Parameters.AddWithValue("$p", key.Path);
                using var reader = select.ExecuteReader();
                while (reader.Read()) oldIds.Add(reader.GetInt64(0));
            }
            foreach (var id in oldIds)
            {
                using var drop = Command(
                    "DELETE FROM labels WHERE face_id = $id; DELETE FROM rejections WHERE face_id = $id; DELETE FROM faces WHERE id = $id", tx);
                drop.Parameters.AddWithValue("$id", id);
                drop.ExecuteNonQuery();
            }

            using (var upsert = Command("""
                INSERT INTO files (volume, path, size, mtime, width, height) VALUES ($v, $p, $size, $mtime, $w, $h)
                ON CONFLICT(volume, path) DO UPDATE SET size = $size, mtime = $mtime, width = $w, height = $h
                """, tx))
            {
                upsert.Parameters.AddWithValue("$v", key.Volume);
                upsert.Parameters.AddWithValue("$p", key.Path);
                upsert.Parameters.AddWithValue("$size", size);
                upsert.Parameters.AddWithValue("$mtime", mtime);
                upsert.Parameters.AddWithValue("$w", width);
                upsert.Parameters.AddWithValue("$h", height);
                upsert.ExecuteNonQuery();
            }

            for (var i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                using var insert = Command("""
                    INSERT INTO faces (volume, path, x, y, w, h, score, embedding)
                    VALUES ($v, $p, $x, $y, $w, $h, $score, $e);
                    SELECT last_insert_rowid();
                    """, tx);
                insert.Parameters.AddWithValue("$v", key.Volume);
                insert.Parameters.AddWithValue("$p", key.Path);
                insert.Parameters.AddWithValue("$x", f.X);
                insert.Parameters.AddWithValue("$y", f.Y);
                insert.Parameters.AddWithValue("$w", f.W);
                insert.Parameters.AddWithValue("$h", f.H);
                insert.Parameters.AddWithValue("$score", f.Score);
                insert.Parameters.AddWithValue("$e", MemoryMarshal.AsBytes(f.Embedding.AsSpan()).ToArray());
                var id = (long)insert.ExecuteScalar()!;
                stored[i] = new StoredFace(id, f.X, f.Y, f.W, f.H, f.Score, f.Embedding);
            }

            tx.Commit();

            foreach (var id in oldIds)
            {
                _labels.TryRemove(id, out _);
                foreach (var rejection in _rejections.Keys.Where(r => r.FaceId == id).ToList())
                    _rejections.TryRemove(rejection, out _);
            }
        }

        var result = new StoredFileFaces(size, mtime, width, height, stored);
        _files[key] = result;
        Interlocked.Increment(ref _version);
        return result;
    }

    public string? GetLabel(long faceId) => _labels.TryGetValue(faceId, out var person) ? person : null;

    public bool IsRejected(long faceId, string person) => _rejections.ContainsKey((faceId, person.ToLowerInvariant()));

    /// <summary>Records that the user confirmed this face as <paramref name="person"/>.</summary>
    public void SetLabel(long faceId, string person)
    {
        lock (_gate)
        {
            using var command = Command("""
                INSERT INTO labels (face_id, person) VALUES ($id, $p)
                ON CONFLICT(face_id) DO UPDATE SET person = $p;
                DELETE FROM rejections WHERE face_id = $id AND person = $lp;
                """);
            command.Parameters.AddWithValue("$id", faceId);
            command.Parameters.AddWithValue("$p", person);
            command.Parameters.AddWithValue("$lp", person.ToLowerInvariant());
            command.ExecuteNonQuery();
        }
        _labels[faceId] = person;
        _rejections.TryRemove((faceId, person.ToLowerInvariant()), out _);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Every confirmed face and rejection under one of <paramref name="from"/> takes the name <paramref name="to"/>.</summary>
    public int RenamePeople(IEnumerable<string> from, string to)
    {
        var sources = new HashSet<string>(
            from.Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        var target = to.Trim();
        if (sources.Count == 0 || target.Length == 0) return 0;
        var targetKey = target.ToLowerInvariant();

        lock (_gate)
        {
            var labelIds = _labels
                .Where(kv => sources.Contains(kv.Value) && !kv.Value.Equals(target, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToHashSet();
            var rejectionMoves = _rejections.Keys
                .Where(k => sources.Contains(k.Person) && k.Person != targetKey)
                .ToList();
            if (labelIds.Count == 0 && rejectionMoves.Count == 0) return 0;

            var labelledAsTarget = new HashSet<long>(labelIds);
            foreach (var (id, person) in _labels)
                if (person.Equals(target, StringComparison.OrdinalIgnoreCase)) labelledAsTarget.Add(id);

            using var tx = _connection.BeginTransaction();
            using (var update = Command("UPDATE labels SET person = $p WHERE face_id = $id", tx))
            using (var drop = Command("DELETE FROM rejections WHERE face_id = $id AND person = $p", tx))
            using (var insert = Command("INSERT OR IGNORE INTO rejections (face_id, person) VALUES ($id, $p)", tx))
            {
                var updateId = update.Parameters.Add("$id", SqliteType.Integer);
                update.Parameters.AddWithValue("$p", target);
                var dropId = drop.Parameters.Add("$id", SqliteType.Integer);
                var dropPerson = drop.Parameters.Add("$p", SqliteType.Text);
                var insertId = insert.Parameters.Add("$id", SqliteType.Integer);
                insert.Parameters.AddWithValue("$p", targetKey);

                foreach (var id in labelIds)
                {
                    updateId.Value = id;
                    update.ExecuteNonQuery();
                    dropId.Value = id;
                    dropPerson.Value = targetKey;
                    drop.ExecuteNonQuery();
                }
                foreach (var (faceId, person) in rejectionMoves)
                {
                    dropId.Value = faceId;
                    dropPerson.Value = person;
                    drop.ExecuteNonQuery();
                    if (labelledAsTarget.Contains(faceId)) continue;
                    insertId.Value = faceId;
                    insert.ExecuteNonQuery();
                }
            }
            tx.Commit();

            foreach (var id in labelIds)
            {
                _labels[id] = target;
                _rejections.TryRemove((id, targetKey), out _);
            }
            foreach (var key in rejectionMoves) _rejections.TryRemove(key, out _);
            foreach (var (faceId, _) in rejectionMoves)
                if (!labelledAsTarget.Contains(faceId)) _rejections[(faceId, targetKey)] = 0;
        }

        Interlocked.Increment(ref _version);
        return 1;
    }

    /// <summary>Records that this face is not <paramref name="person"/>.</summary>
    public void Reject(long faceId, string person)
    {
        var normalized = person.ToLowerInvariant();
        lock (_gate)
        {
            using var command = Command("""
                INSERT OR IGNORE INTO rejections (face_id, person) VALUES ($id, $p);
                DELETE FROM labels WHERE face_id = $id AND lower(person) = $p;
                """);
            command.Parameters.AddWithValue("$id", faceId);
            command.Parameters.AddWithValue("$p", normalized);
            command.ExecuteNonQuery();
        }
        _rejections[(faceId, normalized)] = 0;
        if (_labels.TryGetValue(faceId, out var labelled) && labelled.Equals(person, StringComparison.OrdinalIgnoreCase))
            _labels.TryRemove(faceId, out _);
        Interlocked.Increment(ref _version);
    }

    private void Load()
    {
        var faces = new Dictionary<(string, string), List<StoredFace>>();
        using (var command = Command("SELECT id, volume, path, x, y, w, h, score, embedding FROM faces ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(1), reader.GetString(2));
                var blob = (byte[])reader.GetValue(8);
                var embedding = MemoryMarshal.Cast<byte, float>(blob).ToArray();
                if (!faces.TryGetValue(key, out var list)) faces[key] = list = new List<StoredFace>();
                list.Add(new StoredFace(reader.GetInt64(0), reader.GetFloat(3), reader.GetFloat(4),
                    reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), embedding));
            }
        }

        using (var command = Command("SELECT volume, path, size, mtime, width, height FROM files"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                _files[key] = new StoredFileFaces(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4),
                    reader.GetInt32(5), faces.TryGetValue(key, out var list) ? list.ToArray() : Array.Empty<StoredFace>());
            }
        }

        using (var command = Command("SELECT face_id, person FROM labels"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) _labels[reader.GetInt64(0)] = reader.GetString(1);

        using (var command = Command("SELECT face_id, person FROM rejections"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) _rejections[(reader.GetInt64(0), reader.GetString(1))] = 0;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
