using System.Text.Json;
using SldlWeb.Models;

namespace SldlWeb.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private readonly object _lock = new();
    private AppSettings _settings;

    public SettingsService()
    {
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        if (File.Exists(_settingsPath))
        {
            var json = File.ReadAllText(_settingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        else
        {
            _settings = new AppSettings();
            Save();
        }
    }

    public AppSettings Get()
    {
        lock (_lock)
        {
            return new AppSettings
            {
                SoulseekUsername = _settings.SoulseekUsername,
                SoulseekPassword = _settings.SoulseekPassword,
                DownloadPath = _settings.DownloadPath,
                PreferredFormat = _settings.PreferredFormat,
                MinBitrate = _settings.MinBitrate,
                SpotifyClientId = _settings.SpotifyClientId,
                SpotifyClientSecret = _settings.SpotifyClientSecret,
                DefaultExtraArgs = _settings.DefaultExtraArgs.Select(a => new ExtraArg { Flag = a.Flag, Value = a.Value }).ToList(),
                EnableSharing = _settings.EnableSharing,
                SharedDirectories = new List<string>(_settings.SharedDirectories),
                SharingListenPort = _settings.SharingListenPort,
                UserDescription = _settings.UserDescription,
            };
        }
    }

    public event Action? OnSettingsChanged;

    public void Update(AppSettings updated)
    {
        lock (_lock)
        {
            _settings = updated;
            Save();
        }
        OnSettingsChanged?.Invoke();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}
