namespace SldlWeb.Models;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// A single extra argument passed to slsk-batchdl (e.g. --fast-search or --concurrent-downloads 4).
/// </summary>
public class ExtraArg
{
    public string Flag { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Metadata for a well-known slsk-batchdl argument that can be selected from the UI.
/// </summary>
public class KnownArg
{
    public string Flag { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>flag | number | text</summary>
    public string Type { get; set; } = "flag";
    public string Placeholder { get; set; } = "";

    public static readonly IReadOnlyList<KnownArg> All = new List<KnownArg>
    {
        new() { Flag = "--fast-search",         Label = "Fast Search",                  Type = "flag" },
        new() { Flag = "--album",               Label = "Album Mode",                   Type = "flag" },
        new() { Flag = "--desperate",           Label = "Desperate Mode",               Type = "flag" },
        new() { Flag = "--yt-dlp",              Label = "Use yt-dlp fallback",          Type = "flag" },
        new() { Flag = "--remove-ft",           Label = "Remove 'feat.' from search",   Type = "flag" },
        new() { Flag = "--reverse",             Label = "Reverse Track Order",          Type = "flag" },
        new() { Flag = "--write-playlist",      Label = "Write Playlist (.m3u)",        Type = "flag" },
        new() { Flag = "--artist-maybe-wrong",  Label = "Artist Maybe Wrong",           Type = "flag" },
        new() { Flag = "--strict-title",        Label = "Strict Title Match",           Type = "flag" },
        new() { Flag = "--strict-artist",       Label = "Strict Artist Match",          Type = "flag" },
        new() { Flag = "--concurrent-downloads",Label = "Concurrent Downloads",         Type = "number", Placeholder = "2" },
        new() { Flag = "--number",              Label = "Max Tracks",                   Type = "number", Placeholder = "50" },
        new() { Flag = "--offset",              Label = "Track Offset (skip n)",        Type = "number", Placeholder = "0" },
        new() { Flag = "--search-timeout",      Label = "Search Timeout (ms)",          Type = "number", Placeholder = "6000" },
        new() { Flag = "--min-bitrate",         Label = "Required Min Bitrate (kbps)",  Type = "number", Placeholder = "128" },
        new() { Flag = "--max-bitrate",         Label = "Required Max Bitrate (kbps)",  Type = "number", Placeholder = "2500" },
        new() { Flag = "--format",              Label = "Required Format(s)",           Type = "text",   Placeholder = "mp3,flac" },
        new() { Flag = "--name-format",         Label = "Name Format",                  Type = "text",   Placeholder = "{artist} - {title}" },
    };
}

public class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Input { get; set; } = "";
    public string InputType { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string DownloadPath { get; set; } = "";
    public bool AlbumMode { get; set; }
    public string? Profile { get; set; }
    public List<TrackInfo> Tracks { get; set; } = new();
    public CancellationTokenSource Cts { get; set; } = new();
    public List<ExtraArg> ExtraArgs { get; set; } = new();

    public int TotalTracks => Tracks.Count;
    public int DownloadedTracks => Tracks.Count(t => t.State is "Downloaded" or "AlreadyExists");
    public int FailedTracks => Tracks.Count(t => t.State == "Failed");
}

public class TrackInfo
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public int Length { get; set; }
    public string State { get; set; } = "Initial";
    public string? FailureReason { get; set; }
    public string? DownloadPath { get; set; }
    public double Progress { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public string? Username { get; set; }
    public string? Filename { get; set; }
    public int? BitRate { get; set; }
    public string? Extension { get; set; }
    public float? Bpm { get; set; }
    public string BpmState { get; set; } = "None"; // None | Analyzing | Done | Failed
}
