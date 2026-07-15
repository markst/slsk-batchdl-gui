using System.Diagnostics;

namespace SldlWeb.Services;

/// <summary>
/// Manages the lifetime of an embedded sockseek daemon process.
/// Launched automatically when the app starts; killed on app exit.
/// Skipped when <c>SldlDaemonUrl</c> points to a non-localhost host
/// (i.e. the user is connecting to a remote daemon they manage themselves).
///
/// Soulseek credentials from <see cref="SettingsService"/> are passed as
/// <c>--user</c>/<c>--pass</c> on daemon start. Call <see cref="RestartAsync"/>
/// after login or credential changes so the running daemon picks them up.
/// </summary>
public sealed class DaemonLauncherService : BackgroundService
{
    private readonly ILogger<DaemonLauncherService> _logger;
    private readonly SettingsService _settings;
    private readonly string? _executablePath;
    private readonly string _daemonIp = "127.0.0.1";
    private readonly int _daemonPort = 5030;
    private Process? _process;
    private volatile bool _intentionalRestart;
    private TaskCompletionSource<(bool Ok, string Message)>? _restartGate;
    private const int MaxRestarts = 5;

    public DaemonLauncherService(
        IConfiguration config,
        SettingsService settings,
        ILogger<DaemonLauncherService> logger)
    {
        _logger = logger;
        _settings = settings;

        var daemonUrl = config["SldlDaemonUrl"] ?? "http://localhost:5030";
        var uri = new Uri(daemonUrl);

        var isLocal = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "127.0.0.1";

        if (!isLocal)
        {
            // Remote daemon — the user manages it themselves; don't launch locally.
            return;
        }

        _daemonIp = "127.0.0.1";
        _daemonPort = uri.Port > 0 ? uri.Port : 5030;

        // Explicit override from config (useful in dev or custom installs)
        var configuredPath = config["SldlExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            _executablePath = configuredPath;
            return;
        }

        var exeName = OperatingSystem.IsWindows() ? "sockseek.exe" : "sockseek";

        // 1. Bundled executable placed next to the app in a "bin/" subdirectory
        //    (populated by electron.manifest.json extraResources for packaged builds).
        var bundled = Path.Combine(AppContext.BaseDirectory, "bin", exeName);
        if (File.Exists(bundled))
        {
            _executablePath = bundled;
            return;
        }

        // 2. Dev mode: sldl submodule built alongside the app.
        //    Walk up from the build output dir to find the workspace root, then
        //    check the CLI project's Debug and Release output directories.
        var submodulePath = FindSubmoduleBinary(exeName);
        if (submodulePath is not null)
        {
            _executablePath = submodulePath;
            return;
        }

        // 3. Fall back to PATH (sockseek installed globally or via Homebrew/scoop/etc.)
        _executablePath = exeName;
    }

    /// <summary>
    /// Kill the current daemon so the supervisor loop restarts it with the
    /// latest username/password from settings. Waits briefly for the new process.
    /// </summary>
    public async Task<(bool Ok, string Message)> RestartAsync(CancellationToken ct = default)
    {
        if (_executablePath is null)
            return (true, "Using a remote daemon — configure Soulseek login on that server.");

        var settings = _settings.Get();
        var hasLogin = !string.IsNullOrWhiteSpace(settings.SoulseekUsername)
            && !string.IsNullOrWhiteSpace(settings.SoulseekPassword);
        if (!hasLogin)
            return (false, "No Soulseek username/password in settings.");

        var gate = new TaskCompletionSource<(bool Ok, string Message)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _restartGate, gate);
        previous?.TrySetCanceled();

        var process = _process;
        if (process is { HasExited: false })
        {
            _logger.LogInformation("Restarting sockseek daemon to apply Soulseek credentials.");
            _intentionalRestart = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _intentionalRestart = false;
                Interlocked.CompareExchange(ref _restartGate, null, gate);
                _logger.LogWarning(ex, "Failed to restart sockseek daemon.");
                return (false, "Could not restart the download daemon.");
            }
        }
        else
        {
            _logger.LogInformation("Waiting for sockseek daemon to start with Soulseek credentials.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await gate.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref _restartGate, null, gate);
            if (_process is { HasExited: false })
                return (true, "Download daemon is running with your Soulseek login.");
            return (false, "Timed out waiting for the download daemon to restart.");
        }
        catch (TaskCanceledException)
        {
            Interlocked.CompareExchange(ref _restartGate, null, gate);
            return (false, "Daemon restart was cancelled.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield immediately so BackgroundService.StartAsync never blocks or faults
        // the host startup sequence (which would prevent the Electron window from opening).
        await Task.Yield();

        if (_executablePath is null)
        {
            _logger.LogInformation("SldlDaemonUrl points to a remote host; skipping daemon auto-launch.");
            return;
        }

        int restarts = 0;
        var lastStarted = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            _process = TryStartProcess();

            if (_process is null)
            {
                var failGate = Interlocked.Exchange(ref _restartGate, null);
                failGate?.TrySetResult((false, "Could not start the download daemon (executable not found)."));

                _logger.LogWarning(
                    "Could not start sockseek daemon (executable not found at '{Path}'). " +
                    "Download functionality will be unavailable.", _executablePath);
                return;
            }

            lastStarted = DateTimeOffset.UtcNow;
            var s = _settings.Get();
            var hasLogin = !string.IsNullOrWhiteSpace(s.SoulseekUsername)
                && !string.IsNullOrWhiteSpace(s.SoulseekPassword);
            _logger.LogInformation(
                "sockseek daemon started (PID {Pid}, soulseek login {LoginState}).",
                _process.Id,
                hasLogin ? "configured" : "missing");

            var startedGate = Interlocked.Exchange(ref _restartGate, null);
            startedGate?.TrySetResult((
                true,
                hasLogin
                    ? "Download daemon restarted with your Soulseek login."
                    : "Download daemon started, but Soulseek login is still missing."));

            try
            {
                await _process.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            var exitCode = _process.ExitCode;
            var wasIntentional = _intentionalRestart;
            _intentionalRestart = false;

            if (wasIntentional)
            {
                _logger.LogInformation("sockseek daemon stopped for credential refresh; restarting.");
                continue;
            }

            // Reset restart counter if the process ran for a while before crashing.
            if ((DateTimeOffset.UtcNow - lastStarted).TotalSeconds > 60)
                restarts = 0;

            restarts++;

            if (restarts > MaxRestarts)
            {
                _logger.LogError(
                    "sockseek daemon exited with code {Code} and has crashed {Max} times. Giving up.",
                    exitCode, MaxRestarts);
                break;
            }

            _logger.LogWarning(
                "sockseek daemon exited with code {Code}. Restarting in 3 s (attempt {N}/{Max})...",
                exitCode, restarts, MaxRestarts);

            await Task.Delay(3_000, stoppingToken).ConfigureAwait(false);
        }
    }

    private static string? FindSubmoduleBinary(string exeName)
    {
        // Walk up from the app build output until we find the workspace root
        // (identified by the presence of a "sldl" subdirectory containing the CLI project).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var cliRoot = Path.Combine(dir.FullName, "sldl", "Sockseek.Cli", "bin");
            if (!Directory.Exists(cliRoot))
                continue;

            // Prefer Release over Debug
            foreach (var config in new[] { "Release", "Debug" })
            {
                var configDir = Path.Combine(cliRoot, config);
                if (!Directory.Exists(configDir))
                    continue;

                foreach (var tfm in Directory.GetDirectories(configDir, "net*", SearchOption.TopDirectoryOnly))
                {
                    var candidate = Path.Combine(tfm, exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    private Process? TryStartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("daemon");
        psi.ArgumentList.Add("--daemon-ip");
        psi.ArgumentList.Add(_daemonIp);
        psi.ArgumentList.Add("--daemon-port");
        psi.ArgumentList.Add(_daemonPort.ToString());

        var s = _settings.Get();
        if (!string.IsNullOrWhiteSpace(s.SoulseekUsername) && !string.IsNullOrWhiteSpace(s.SoulseekPassword))
        {
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add(s.SoulseekUsername);
            psi.ArgumentList.Add("--pass");
            psi.ArgumentList.Add(s.SoulseekPassword);
        }
        else
        {
            _logger.LogWarning(
                "Starting sockseek daemon without Soulseek credentials. " +
                "Log in (or set username/password in Settings) and the daemon will be restarted.");
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start sockseek daemon process.");
            return null;
        }

        if (process is null)
            return null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[sockseek] {Line}", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogWarning("[sockseek] {Line}", e.Data);
        };

        try { process.BeginOutputReadLine(); } catch { /* process may have already exited */ }
        try { process.BeginErrorReadLine(); } catch { /* process may have already exited */ }

        return process;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_process is { HasExited: false })
        {
            _logger.LogInformation("Stopping sockseek daemon (PID {Pid})...", _process.Id);
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping sockseek daemon process.");
            }
        }

        _process?.Dispose();
        _process = null;
    }
}
