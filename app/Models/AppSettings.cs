namespace SldlWeb.Models;

public class AppSettings
{
    public string SoulseekUsername { get; set; } = "";
    public string SoulseekPassword { get; set; } = "";
    public string DownloadPath { get; set; } = "./downloads";
    public string PreferredFormat { get; set; } = "mp3";
    public string MinBitrate { get; set; } = "200";
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyClientSecret { get; set; } = "";
    public List<ExtraArg> DefaultExtraArgs { get; set; } = new();

    // File sharing
    public bool EnableSharing { get; set; } = false;
    public List<string> SharedDirectories { get; set; } = new();
    public int SharingListenPort { get; set; } = 50000;
    public string UserDescription { get; set; } = "";
}
