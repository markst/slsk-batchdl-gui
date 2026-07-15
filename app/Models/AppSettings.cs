namespace SldlWeb.Models;

public class AppSettings
{
    public string SoulseekUsername { get; set; } = "";
    public string SoulseekPassword { get; set; } = "";
    public string DownloadPath { get; set; } = "./downloads";
    public string PreferredFormat { get; set; } = "mp3";
    public string MinBitrate { get; set; } = "200";
    public string SpotifyClientId { get; set; } = BuildDefaults.SpotifyClientId;
    public string SpotifyClientSecret { get; set; } = BuildDefaults.SpotifyClientSecret;
    public List<ExtraArg> DefaultExtraArgs { get; set; } = new();

    public string UserDescription { get; set; } = "";
}
