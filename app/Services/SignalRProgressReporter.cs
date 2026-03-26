using Microsoft.AspNetCore.SignalR;
using Models;
using Utilities;
using SldlWeb.Hubs;
using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// IProgressReporter implementation that:
/// 1. Updates the in-memory DownloadJob model (for HTTP polling)
/// 2. Pushes events to SignalR clients (for real-time updates)
/// </summary>
public class SignalRProgressReporter : IProgressReporter
{
    private readonly IHubContext<DownloadHub> _hub;
    private readonly DownloadJob _job;
    private DateTime _lastProgressReport = DateTime.MinValue;
    private readonly TimeSpan _progressThrottle = TimeSpan.FromMilliseconds(300);

    public SignalRProgressReporter(IHubContext<DownloadHub> hub, DownloadJob job)
    {
        _hub = hub;
        _job = job;
    }

    private IClientProxy All => _hub.Clients.All;

    public void ReportTrackList(List<Track> tracks, int listIndex = 0)
    {
        var newTracks = tracks.Select(t => new TrackInfo
        {
            Artist = t.Artist,
            Title = t.Title,
            Album = t.Album,
            Length = t.Length,
            State = t.State.ToString(),
        });
        _job.Tracks.AddRange(newTracks);

        _ = All.SendAsync("TrackList", _job.Id, _job.Tracks);
    }

    public void ReportSearchStart(Track track)
    {
        var t = FindTrack(track);
        if (t != null) t.State = "Searching";

        _ = All.SendAsync("SearchStart", _job.Id, track.Artist, track.Title);
    }

    public void ReportSearchResult(Track track, int resultCount, string? chosenUser = null, Soulseek.File? chosenFile = null)
    {
        var t = FindTrack(track);
        if (t != null && resultCount > 0) t.State = "Found";

        _ = All.SendAsync("SearchResult", _job.Id, track.Artist, track.Title, resultCount);
    }

    public void ReportDownloadStart(Track track, string username, Soulseek.File file)
    {
        var t = FindOrAddTrack(track, file.Filename);
        t.State = "Downloading";
        t.Username = username;
        t.Filename = file.Filename;
        t.TotalBytes = file.Size;
        t.Extension = GetExtension(file.Filename);

        _ = All.SendAsync("DownloadStart", _job.Id, track.Artist, track.Title, username,
            file.Filename, file.Size, GetExtension(file.Filename));
    }

    public void ReportDownloadProgress(Track track, long bytesTransferred, long totalBytes)
    {
        var now = DateTime.UtcNow;
        if (now - _lastProgressReport < _progressThrottle) return;
        _lastProgressReport = now;

        var t = FindTrack(track);
        if (t != null)
        {
            t.BytesTransferred = bytesTransferred;
            t.TotalBytes = totalBytes;
            t.Progress = totalBytes > 0 ? Math.Round((double)bytesTransferred / totalBytes * 100, 1) : 0;
        }

        var percent = totalBytes > 0 ? Math.Round((double)bytesTransferred / totalBytes * 100, 1) : 0;
        _ = All.SendAsync("DownloadProgress", _job.Id, track.Artist, track.Title,
            bytesTransferred, totalBytes, percent);
    }

    public void ReportTrackStateChanged(Track track, string? username = null, Soulseek.File? chosenFile = null)
    {
        var t = FindOrAddTrack(track, chosenFile?.Filename);
        t.State = track.State.ToString();
        t.FailureReason = track.FailureReason != Enums.FailureReason.None ? track.FailureReason.ToString() : null;
        t.DownloadPath = !string.IsNullOrEmpty(track.DownloadPath) ? track.DownloadPath : null;
        if (username != null) t.Username = username;
        if (chosenFile != null)
        {
            t.Filename = chosenFile.Filename;
            t.BitRate = chosenFile.BitRate;
            t.Extension = GetExtension(chosenFile.Filename);
        }
        t.Progress = track.State == Enums.TrackState.Downloaded ? 100 : t.Progress;

        _ = All.SendAsync("TrackStateChanged", _job.Id, track.Artist, track.Title,
            track.State.ToString(),
            track.FailureReason != Enums.FailureReason.None ? track.FailureReason.ToString() : null,
            !string.IsNullOrEmpty(track.DownloadPath) ? track.DownloadPath : null);
    }

    public void ReportOverallProgress(int downloaded, int failed, int total)
    {
        _ = All.SendAsync("OverallProgress", _job.Id, downloaded, failed, total);
    }

    public void ReportJobComplete(int downloaded, int failed, int total)
    {
        _ = All.SendAsync("JobComplete", _job.Id, downloaded, failed, total);
    }

    private TrackInfo? FindTrack(Track track, string? filename = null)
    {
        if (filename != null)
        {
            var byFile = _job.Tracks.FirstOrDefault(t =>
                string.Equals(Path.GetFileName(t.Filename ?? ""), Path.GetFileName(filename), StringComparison.OrdinalIgnoreCase));
            if (byFile != null) return byFile;
        }

        return _job.Tracks.FirstOrDefault(t =>
            string.Equals(t.Artist, track.Artist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Title, track.Title, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(t.Filename)); // only match title-based entries not yet bound to a file
    }

    private TrackInfo FindOrAddTrack(Track track, string? filename = null)
    {
        var t = FindTrack(track, filename);
        if (t != null) return t;

        var title = track.Title;
        if (string.IsNullOrEmpty(title) && filename != null)
            title = Path.GetFileNameWithoutExtension(filename);

        t = new TrackInfo
        {
            Artist = track.Artist,
            Title = title,
            Album = track.Album,
            Length = track.Length,
            Filename = filename, // bind immediately so parallel downloads don't collide
            State = "Initial",
        };
        _job.Tracks.Add(t);
        _ = All.SendAsync("TrackList", _job.Id, _job.Tracks);
        return t;
    }

    private static string? GetExtension(string filename)
    {
        var ext = Path.GetExtension(filename);
        return string.IsNullOrEmpty(ext) ? null : ext.TrimStart('.').ToLower();
    }
}
