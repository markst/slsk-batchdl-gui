using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SldlWeb.Hubs;
using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// Orchestrates job submission and tracking against the sldl daemon HTTP API.
/// 
/// Daemon-first integration: this service submits jobs to the daemon via HTTP,
/// tracks them by workflow ID, and polls/subscribes for updates.
/// </summary>
public class DownloadService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _jobToWorkflowId = new(); // app job ID -> daemon workflow ID
    private readonly SemaphoreSlim _jobSemaphore = new(1); // Sequential job processing
    private readonly IHubContext<DownloadHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<DownloadService> _logger;
    private readonly string _daemonUrl;

    public DownloadService(
        IHubContext<DownloadHub> hub,
        SettingsService settings,
        ILogger<DownloadService> logger,
        JobRestorer jobRestorer,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _hub = hub;
        _settings = settings;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _daemonUrl = config["SldlDaemonUrl"] ?? "http://localhost:5030";

        foreach (var job in jobRestorer.RestoreAll())
            _jobs.TryAdd(job.Id, job);
    }

    /// <summary>
    /// Gets available soulseek profiles from daemon.
    /// TODO: Phase 2 - Implement HTTP call to daemon profiles endpoint.
    /// </summary>
    public IReadOnlyList<string> GetAvailableProfiles()
    {
        _logger.LogWarning("GetAvailableProfiles not yet implemented (Phase 2)");
        return new List<string> { "Default" };
    }

    public DownloadJob CreateJob(string input, bool albumMode = false, string? profile = null, List<ExtraArg>? extraArgs = null)
    {
        var s = _settings.Get();
        // Merge defaults with per-job args; per-job args take precedence
        var merged = (s.DefaultExtraArgs ?? new()).Concat(extraArgs ?? new())
            .GroupBy(a => a.Flag)
            .Select(g => g.Last())
            .ToList();

        var job = new DownloadJob
        {
            Input = input.Trim(),
            InputType = InputTypeDetector.Detect(input),
            ExtraArgs = merged,
            AlbumMode = albumMode,
            Profile = profile,
        };
        job.DownloadPath = GetDownloadPath(job.Id);
        _jobs.TryAdd(job.Id, job);

        _ = Task.Run(() => ProcessJobAsync(job));
        return job;
    }

    public DownloadJob? GetJob(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<DownloadJob> GetAllJobs() => _jobs.Values.OrderByDescending(j => j.CreatedAt);

    public bool CancelJob(string id)
    {
        if (_jobs.TryGetValue(id, out var job) && (job.Status == JobStatus.Running || job.Status == JobStatus.Queued))
        {
            // TODO: Phase 2 - Call daemon cancel endpoint if workflow ID exists
            if (_jobToWorkflowId.TryGetValue(id, out var workflowId))
            {
                _logger.LogInformation("Cancelling daemon workflow {WorkflowId} for app job {JobId}", workflowId, id);
            }

            job.Cts.Cancel();
            job.Status = JobStatus.Cancelled;
            _ = _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
            return true;
        }
        return false;
    }

    public bool DeleteJob(string id)
    {
        CancelJob(id);
        return _jobs.TryRemove(id, out _);
    }

    public bool RetryTrack(string jobId, int trackIndex)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (trackIndex < 0 || trackIndex >= job.Tracks.Count) return false;

        var track = job.Tracks[trackIndex];
        if (track.State is not "Failed") return false;

        _logger.LogWarning("RetryTrack not yet implemented (Phase 2) - job {JobId} track {Index}", jobId, trackIndex);

        // Reset track state temporarily to indicate retry in progress
        track.State = "Initial";
        track.Progress = 0;
        track.BytesTransferred = 0;
        track.TotalBytes = 0;
        track.FailureReason = null;

        _ = _hub.Clients.All.SendAsync("TrackStateChanged", job.Id, track.Artist, track.Title, "Initial", (string?)null, (string?)null);

        return true;
    }

    public bool ResumeJob(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status is JobStatus.Running or JobStatus.Queued) return false;

        // Reset job state for re-processing
        job.Status = JobStatus.Queued;
        job.Error = null;
        job.CompletedAt = null;
        job.Cts = new CancellationTokenSource();

        // Reset failed/initial tracks
        foreach (var track in job.Tracks)
        {
            if (track.State is "Failed" or "Initial")
            {
                track.State = "Initial";
                track.Progress = 0;
                track.BytesTransferred = 0;
                track.TotalBytes = 0;
                track.FailureReason = null;
            }
        }

        _ = _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
        _ = Task.Run(() => ProcessJobAsync(job));
        return true;
    }

    public IEnumerable<string> GetDownloadedFiles(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return [];
        if (!Directory.Exists(job.DownloadPath)) return [];
        return Directory.GetFiles(job.DownloadPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_index.csv") && !f.EndsWith("tracks.csv") && !f.EndsWith(".incomplete"))
            .Select(f => Path.GetRelativePath(job.DownloadPath, f));
    }

    public string? GetFilePath(string jobId, string filename)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        var files = Directory.GetFiles(job.DownloadPath, filename, SearchOption.AllDirectories);
        return files.FirstOrDefault();
    }

    private async Task ProcessJobAsync(DownloadJob job)
    {
        try
        {
            await _jobSemaphore.WaitAsync(job.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            await _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());

            _logger.LogInformation("Processing job {Id} via daemon API (Phase 2 implementation pending)", job.Id);

            // TODO: Phase 2 - Implement daemon submission
            // 1. Convert job parameters to daemon job submission DTO
            // 2. POST to daemon /api/submit endpoint
            // 3. Extract workflow ID from response
            // 4. Store mapping: job.Id -> workflowId
            // 5. Subscribe to /api/events for workflow updates
            // 6. Poll daemon job API for status updates
            // 7. Update TrackInfo and overall progress from daemon state

            // For now, simulate a successful completion after a short delay
            await Task.Delay(100, job.Cts.Token);

            job.Status = JobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} failed", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            await _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
            _jobSemaphore.Release();
        }
    }

    private string GetDownloadPath(string jobId)
    {
        var s = _settings.Get();
        var basePath = string.IsNullOrEmpty(s.DownloadPath) ? Path.Combine(Directory.GetCurrentDirectory(), "downloads") : s.DownloadPath;
        var jobPath = Path.Combine(basePath, jobId);
        Directory.CreateDirectory(jobPath);
        return jobPath;
    }
}
