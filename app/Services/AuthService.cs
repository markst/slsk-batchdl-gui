using Soulseek;
using System.Net.Sockets;

namespace SldlWeb.Services;

public class AuthService
{
    private readonly SettingsService _settings;
    private readonly DaemonLauncherService _daemon;
    private readonly ILogger<AuthService> _logger;
    // Prevent concurrent login attempts (e.g. auto-login racing with manual submit)
    private readonly SemaphoreSlim _loginSemaphore = new(1);

    public bool IsLoggedIn { get; private set; }

    public event Action? OnAuthStateChanged;

    public AuthService(
        SettingsService settings,
        DaemonLauncherService daemon,
        ILogger<AuthService> logger)
    {
        _settings = settings;
        _daemon = daemon;
        _logger = logger;
    }

    /// <summary>
    /// Validates credentials with Soulseek, persists them, then restarts the
    /// local download daemon so it runs with <c>--user</c>/<c>--pass</c>.
    /// </summary>
    public async Task<(bool Success, string? Error, bool DaemonOk, string? DaemonMessage)> LoginAsync(
        string username,
        string password)
    {
        await _loginSemaphore.WaitAsync();
        try
        {
            // Soulseek.NET 10+ requires a unique client minor version (license condition).
            // Distinct from Sockseek's daemon identity (800850000).
            using var client = new SoulseekClient(800861000);

            try
            {
                await client.ConnectAsync(username, password);
            }
            catch (AddressException ex)
            {
                _logger.LogWarning("Soulseek address error for {User}: {Message}", username, ex.Message);
                return (false, "Could not reach the Soulseek server.", false, null);
            }
            catch (SoulseekClientException ex)
            {
                _logger.LogWarning("Soulseek login failed for {User}: {Message}", username, ex.Message);
                return (false, "Invalid username or password.", false, null);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Soulseek login timed out for {User}", username);
                return (false, "Connection timed out. Please try again.", false, null);
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Soulseek login network error for {User}: {Message}", username, ex.Message);
                return (false, "Network error. Check your internet connection.", false, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected Soulseek login error for {User}", username);
                return (false, "Unexpected error. Please try again.", false, null);
            }

            // Save credentials for the app and for the next daemon launch.
            var s = _settings.Get();
            s.SoulseekUsername = username;
            s.SoulseekPassword = password;
            _settings.Update(s);

            IsLoggedIn = true;
            OnAuthStateChanged?.Invoke();

            // Web login only validated against Soulseek; the daemon needs --user/--pass
            // (it may have started before credentials were available).
            var (daemonOk, daemonMessage) = await _daemon.RestartAsync();

            _logger.LogInformation(
                "Soulseek login successful for {User}; daemon: {DaemonOk} ({DaemonMessage})",
                username, daemonOk, daemonMessage);

            return (true, null, daemonOk, daemonMessage);
        }
        finally
        {
            _loginSemaphore.Release();
        }
    }

    public async Task<bool> TryAutoLoginAsync()
    {
        var s = _settings.Get();
        if (string.IsNullOrEmpty(s.SoulseekUsername) || string.IsNullOrEmpty(s.SoulseekPassword))
            return false;

        var (success, _, _, _) = await LoginAsync(s.SoulseekUsername, s.SoulseekPassword);
        return success;
    }

    public void Logout()
    {
        IsLoggedIn = false;
        OnAuthStateChanged?.Invoke();
    }
}
