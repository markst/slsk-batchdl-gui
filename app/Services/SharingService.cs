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

    private ISoulseekClient? _client;
    private readonly SemaphoreSlim _connectLock = new(1);

    // Soulseek path (backslash) → local path
    private readonly ConcurrentDictionary<string, string> _pathMap = new(StringComparer.OrdinalIgnoreCase);
    // Directory name → list of Soulseek File objects
    private readonly ConcurrentDictionary<string, List<Soulseek.File>> _directoryIndex = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".opus", ".wav", ".aac", ".wma", ".alac", ".ape", ".wv"
    };

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
            if (_client != null && IsConnected) continue;

            _logger.LogInformation("Sharing client disconnected, reconnecting...");
            await ConnectAndShareAsync(settings, stoppingToken);
        }
    }

    private bool IsConnected =>
        _client?.State.HasFlag(SoulseekClientStates.Connected) == true &&
        _client?.State.HasFlag(SoulseekClientStates.LoggedIn) == true;

    private async Task OnSettingsChangedAsync(CancellationToken ct)
    {
        try
        {
            var s = _settings.Get();
            if (!s.EnableSharing)
            {
                await DisconnectAsync();
                return;
            }

            RebuildIndex(s);
            if (!IsConnected)
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
            RebuildIndex(settings);
            await EnsureClientAsync(settings, ct);
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

    private async Task EnsureClientAsync(AppSettings settings, CancellationToken ct)
    {
        if (_client != null)
        {
            try { _client.Disconnect(); } catch { }
            (_client as IDisposable)?.Dispose();
            _client = null;
        }

        var serverConnectionOptions = new ConnectionOptions(
            connectTimeout: 30000,
            configureSocket: socket =>
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
            });

        var transferConnectionOptions = new ConnectionOptions(
            inactivityTimeout: 60000,
            configureSocket: socket =>
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
            });

        var description = string.IsNullOrWhiteSpace(settings.UserDescription) ? "sldl-web" : settings.UserDescription;

        var options = new SoulseekClientOptions(
            serverConnectionOptions: serverConnectionOptions,
            transferConnectionOptions: transferConnectionOptions,
            listenPort: settings.SharingListenPort,
            enableListener: true,
            userInfoResolver: (username, ip) => Task.FromResult(new UserInfo(
                description: description,
                uploadSlots: 1,
                queueLength: 0,
                hasFreeUploadSlot: true)),
            browseResponseResolver: BrowseResponseResolver,
            searchResponseResolver: SearchResponseResolver,
            directoryContentsResolver: DirectoryContentsResolver,
            enqueueDownload: EnqueueDownloadHandler
        );

        _client = new SoulseekClient(options);

        _client.Disconnected += (_, args) =>
        {
            _logger.LogWarning("Sharing client disconnected: {Message}", args.Message);
        };

        _logger.LogInformation("Connecting sharing client as {User} on port {Port}", settings.SoulseekUsername, settings.SharingListenPort);
        await _client.ConnectAsync(settings.SoulseekUsername, settings.SoulseekPassword);
        await UpdateShareCountsAsync();
        _logger.LogInformation("Sharing client connected. Sharing {Files} files in {Dirs} directories",
            _pathMap.Count, _directoryIndex.Count);
    }

    private async Task UpdateShareCountsAsync()
    {
        if (_client == null || !IsConnected) return;
        await _client.SetSharedCountsAsync(_directoryIndex.Count, _pathMap.Count);
    }

    private async Task DisconnectAsync()
    {
        if (_client == null) return;
        try
        {
            _client.Disconnect();
            (_client as IDisposable)?.Dispose();
        }
        catch { }
        _client = null;
        _logger.LogInformation("Sharing client disconnected");
    }

    private void RebuildIndex(AppSettings settings)
    {
        _pathMap.Clear();
        _directoryIndex.Clear();

        foreach (var dir in settings.SharedDirectories)
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            var dirName = new DirectoryInfo(dir).Name;
            IndexDirectory(dir, dirName);
        }

        _logger.LogInformation("Indexed {Files} files in {Dirs} directories for sharing", _pathMap.Count, _directoryIndex.Count);
    }

    private void IndexDirectory(string localRoot, string slskRoot)
    {
        int code = 1;
        foreach (var file in System.IO.Directory.EnumerateFiles(localRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!MusicExtensions.Contains(ext)) continue;

            var relativePath = Path.GetRelativePath(localRoot, file);
            var slskPath = slskRoot + "\\" + relativePath.Replace('/', '\\');
            var slskDir = slskRoot + "\\" + Path.GetRelativePath(localRoot, Path.GetDirectoryName(file)!).Replace('/', '\\');

            _pathMap[slskPath] = file;

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }

            var slskFile = new Soulseek.File(
                code++,
                slskPath,
                size,
                ext,
                attributeList: null
            );

            _directoryIndex.AddOrUpdate(
                slskDir,
                _ => new List<Soulseek.File> { slskFile },
                (_, list) => { list.Add(slskFile); return list; });
        }
    }

    // --- Soulseek Sharing Delegates ---

    private Task<BrowseResponse> BrowseResponseResolver(string username, IPEndPoint endpoint)
    {
        var directories = _directoryIndex.Select(kvp =>
            new Soulseek.Directory(kvp.Key, (IEnumerable<Soulseek.File>)kvp.Value)).ToList();
        return Task.FromResult(new BrowseResponse(directories));
    }

    private Task<SearchResponse?> SearchResponseResolver(string username, int token, SearchQuery query)
    {
        if (_pathMap.IsEmpty)
            return Task.FromResult<SearchResponse?>(null);

        var terms = query.Terms.Select(t => t.ToLowerInvariant()).ToList();
        var exclusions = query.Exclusions.Select(e => e.ToLowerInvariant()).ToList();

        var matches = new List<Soulseek.File>();
        foreach (var (slskPath, localPath) in _pathMap)
        {
            var lower = slskPath.ToLowerInvariant();
            if (terms.All(t => lower.Contains(t)) && !exclusions.Any(e => lower.Contains(e)))
            {
                // Find the File object from the directory index
                var dir = Path.GetDirectoryName(slskPath)?.Replace('/', '\\') ?? "";
                if (_directoryIndex.TryGetValue(dir, out var files))
                {
                    var match = files.FirstOrDefault(f =>
                        string.Equals(f.Filename, slskPath, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        matches.Add(match);
                }
            }
        }

        if (matches.Count == 0)
            return Task.FromResult<SearchResponse?>(null);

        var response = new SearchResponse(
            username: _client?.Username ?? "unknown",
            token: token,
            hasFreeUploadSlot: true,
            uploadSpeed: 0,
            queueLength: 0,
            fileList: matches
        );
        return Task.FromResult<SearchResponse?>(response);
    }

    private Task<IEnumerable<Soulseek.Directory>> DirectoryContentsResolver(string username, IPEndPoint endpoint, int token, string directoryName)
    {
        var normalizedDir = directoryName.TrimEnd('\\');
        if (_directoryIndex.TryGetValue(normalizedDir, out var files))
        {
            IEnumerable<Soulseek.Directory> result = new[] { new Soulseek.Directory(normalizedDir, (IEnumerable<Soulseek.File>)files) };
            return Task.FromResult(result);
        }

        return Task.FromResult(Enumerable.Empty<Soulseek.Directory>());
    }

    private Task EnqueueDownloadHandler(string username, IPEndPoint endpoint, string filename)
    {
        if (!_pathMap.TryGetValue(filename, out var localPath) || !System.IO.File.Exists(localPath))
            throw new DownloadEnqueueException("File not shared");

        _logger.LogInformation("Upload enqueued: {File} to {User}", filename, username);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        await base.StopAsync(cancellationToken);
    }
}
