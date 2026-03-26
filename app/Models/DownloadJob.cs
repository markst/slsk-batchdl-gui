namespace SldlWeb.Models;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
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
    public List<TrackInfo> Tracks { get; set; } = new();
    public CancellationTokenSource Cts { get; set; } = new();

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
}
