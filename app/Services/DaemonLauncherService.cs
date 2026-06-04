using System.Diagnostics;

namespace SldlWeb.Services;

/// <summary>
/// Manages the lifetime of an embedded sldl daemon process.
/// Launched automatically when the app starts; killed on app exit.
/// Skipped when <c>SldlDaemonUrl</c> points to a non-localhost host
/// (i.e. the user is connecting to a remote daemon they manage themselves).
/// </summary>
public sealed class DaemonLauncherService : BackgroundService
{
    private readonly ILogger<DaemonLauncherService> _logger;
    private readonly string? _executablePath;
    private readonly string _daemonIp = "127.0.0.1";
    private readonly int _daemonPort = 5030;
    private Process? _process;
    private const int MaxRestarts = 5;

    public DaemonLauncherService(IConfiguration config, ILogger<DaemonLauncherService> logger)
    {
        _logger = logger;

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

        var exeName = OperatingSystem.IsWindows() ? "sldl.exe" : "sldl";

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

        // 3. Fall back to PATH (sldl installed globally or via Homebrew/scoop/etc.)
        _executablePath = exeName;
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
                _logger.LogWarning(
                    "Could not start sldl daemon (executable not found at '{Path}'). " +
                    "Download functionality will be unavailable.", _executablePath);
                return;
            }

            lastStarted = DateTimeOffset.UtcNow;
            _logger.LogInformation("sldl daemon started (PID {Pid}).", _process.Id);

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

            // Reset restart counter if the process ran for a while before crashing.
            if ((DateTimeOffset.UtcNow - lastStarted).TotalSeconds > 60)
                restarts = 0;

            restarts++;

            if (restarts > MaxRestarts)
            {
                _logger.LogError(
                    "sldl daemon exited with code {Code} and has crashed {Max} times. Giving up.",
                    exitCode, MaxRestarts);
                break;
            }

            _logger.LogWarning(
                "sldl daemon exited with code {Code}. Restarting in 3 s (attempt {N}/{Max})...",
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
            var cliRoot = Path.Combine(dir.FullName, "sldl", "slsk-batchdl.Cli", "bin");
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

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start sldl daemon process.");
            return null;
        }

        if (process is null)
            return null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[sldl] {Line}", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogWarning("[sldl] {Line}", e.Data);
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
            _logger.LogInformation("Stopping sldl daemon (PID {Pid})...", _process.Id);
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping sldl daemon process.");
            }
        }

        _process?.Dispose();
        _process = null;
    }
}
