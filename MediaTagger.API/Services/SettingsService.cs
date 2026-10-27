using System.Text.Json;
using MediaTagger.API.Models;

namespace MediaTagger.API.Services;

/// <summary>
/// The user's settings file. The API is its only writer, so it is read once and kept;
/// background work (the face scan) asks here instead of reading the file each time.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _gate = new();
    private AppSettings? _current;

    /// <summary>Raised after a save, with the settings before and after it.</summary>
    public event Action<AppSettings, AppSettings>? Changed;

    public SettingsService(IConfiguration config, ILogger<SettingsService> logger)
    {
        _logger = logger;
        var configured = config["MediaTagger:SettingsPath"];
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaTagger", "settings.json")
            : configured;
    }

    public AppSettings Get()
    {
        lock (_gate) return _current ??= Load();
    }

    public VideoFaceMode VideoFaces => Get().VideoFaces;

    public void Save(AppSettings settings)
    {
        AppSettings before;
        lock (_gate)
        {
            before = _current ??= Load();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOpts));
            _current = settings;
        }
        Changed?.Invoke(before, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOpts) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Settings at {Path} could not be read, using defaults: {Msg}", _path, ex.Message);
        }
        return new AppSettings();
    }
}
