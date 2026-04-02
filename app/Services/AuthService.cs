using Soulseek;
using System.Net.Sockets;

namespace SldlWeb.Services;

public class AuthService
{
    private readonly SettingsService _settings;
    private readonly ILogger<AuthService> _logger;
    // Prevent concurrent login attempts (e.g. auto-login racing with manual submit)
    private readonly SemaphoreSlim _loginSemaphore = new(1);

    public bool IsLoggedIn { get; private set; }

    public event Action? OnAuthStateChanged;

    public AuthService(SettingsService settings, ILogger<AuthService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> LoginAsync(string username, string password)
    {
        await _loginSemaphore.WaitAsync();
        try
        {
            var connectionOptions = new ConnectionOptions(
                connectTimeout: 20000,
                configureSocket: socket =>
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                });

            var clientOptions = new SoulseekClientOptions(
                serverConnectionOptions: connectionOptions,
                enableListener: false);

            using var client = new SoulseekClient(clientOptions);

            try
            {
                await client.ConnectAsync(username, password);
            }
            catch (AddressException ex)
            {
                _logger.LogWarning("Soulseek address error for {User}: {Message}", username, ex.Message);
                return (false, "Could not reach the Soulseek server.");
            }
            catch (SoulseekClientException ex)
            {
                _logger.LogWarning("Soulseek login failed for {User}: {Message}", username, ex.Message);
                return (false, "Invalid username or password.");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Soulseek login timed out for {User}", username);
                return (false, "Connection timed out. Please try again.");
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Soulseek login network error for {User}: {Message}", username, ex.Message);
                return (false, "Network error. Check your internet connection.");
            }

            // Save credentials
            var s = _settings.Get();
            s.SoulseekUsername = username;
            s.SoulseekPassword = password;
            _settings.Update(s);

            IsLoggedIn = true;
            OnAuthStateChanged?.Invoke();

            _logger.LogInformation("Soulseek login successful for {User}", username);
            return (true, null);
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

        var (success, _) = await LoginAsync(s.SoulseekUsername, s.SoulseekPassword);
        return success;
    }

    public void Logout()
    {
        IsLoggedIn = false;
        OnAuthStateChanged?.Invoke();
    }
}
