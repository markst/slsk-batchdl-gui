using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Soulseek;
using SldlWeb.Models;

namespace SldlWeb.Services;

public class SharingService : BackgroundService
{
    private readonly SettingsService _settings;
    private readonly ILogger<SharingService> _logger;

    private SoulseekClientManager? _clientManager;
    private FileShareService? _fileShareService;
    private readonly SemaphoreSlim _connectLock = new(1);

    public SharingService(SettingsService settings, ILogger<SharingService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _settings.OnSettingsChanged += () => _ = OnSettingsChangedAsync(stoppingToken);

        var s = _settings.Get();
        if (s.EnableSharing)
            await ConnectAndShareAsync(s, stoppingToken);

        // Keep alive — reconnect if disconnected
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            var settings = _settings.Get();
            if (!settings.EnableSharing) continue;
            if (_clientManager != null && _clientManager.IsConnectedAndLoggedIn) continue;

            _logger.LogInformation("Sharing client disconnected, reconnecting...");
            await ConnectAndShareAsync(settings, stoppingToken);
        }
    }

    private async Task OnSettingsChangedAsync(CancellationToken ct)
    {
        try
        {
            var s = _settings.Get();
            if (!s.EnableSharing)
            {
                Disconnect();
                return;
            }

            _fileShareService?.RebuildIndex(s.SharedDirectories);

            if (_clientManager == null || !_clientManager.IsConnectedAndLoggedIn)
                await ConnectAndShareAsync(s, ct);
            else
                await UpdateShareCountsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply sharing settings change");
        }
    }

    private async Task ConnectAndShareAsync(AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.SoulseekUsername) || string.IsNullOrEmpty(settings.SoulseekPassword))
        {
            _logger.LogWarning("Sharing enabled but no Soulseek credentials configured");
            return;
        }

        if (!settings.SharedDirectories.Any(d => System.IO.Directory.Exists(d)))
        {
            _logger.LogWarning("Sharing enabled but no valid shared directories configured");
            return;
        }

        await _connectLock.WaitAsync(ct);
        try
        {
            _fileShareService ??= new FileShareService();
            _fileShareService.RebuildIndex(settings.SharedDirectories);

            var config = BuildConfig(settings);

            Disconnect();
            _clientManager = new SoulseekClientManager(config);
            _clientManager.SetFileShareService(_fileShareService);

            _logger.LogInformation("Connecting sharing client as {User} on port {Port}",
                settings.SoulseekUsername, settings.SharingListenPort);

            await _clientManager.EnsureConnectedAndLoggedInAsync(config, ct);

            var (dirs, files) = _fileShareService.GetShareCounts();
            _logger.LogInformation("Sharing client connected. Sharing {Files} files in {Dirs} directories", files, dirs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to start sharing");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task UpdateShareCountsAsync()
    {
        if (_clientManager?.Client == null || !_clientManager.IsConnectedAndLoggedIn) return;
        if (_fileShareService == null) return;
        var (dirs, files) = _fileShareService.GetShareCounts();
        await _clientManager.Client.SetSharedCountsAsync(dirs, files);
    }

    private void Disconnect()
    {
        if (_clientManager == null) return;
        _clientManager.Disconnect();
        _clientManager = null;
        _logger.LogInformation("Sharing client disconnected");
    }

    private static Config BuildConfig(AppSettings settings)
    {
        var args = new List<string> { "--input", "" };
        args.AddRange(new[] { "--user", settings.SoulseekUsername });
        args.AddRange(new[] { "--pass", settings.SoulseekPassword });
        args.AddRange(new[] { "--listen-port", settings.SharingListenPort.ToString() });
        if (!string.IsNullOrWhiteSpace(settings.UserDescription))
            args.AddRange(new[] { "--user-description", settings.UserDescription });
        var config = new Config(args.ToArray());
        config.enableSharing = true;
        config.shareFolders = settings.SharedDirectories;
        config.connectTimeout = 30000;
        return config;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Disconnect();
        await base.StopAsync(cancellationToken);
    }
}
