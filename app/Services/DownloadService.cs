using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Sldl.Api;
using SldlWeb.Hubs;
using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// Orchestrates job submission and tracking against the sldl daemon HTTP API.
/// Each app DownloadJob maps to a daemon workflow. Tracks are updated by the
/// SldlEventBridge via UpdateTrackFromDaemon() as live events arrive.
/// </summary>
public class DownloadService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    // app job ID → daemon workflow ID (set after successful submission)
    private readonly ConcurrentDictionary<string, Guid> _jobToWorkflow = new();
    // daemon workflow ID → app job ID (reverse lookup for event bridge)
    private readonly ConcurrentDictionary<Guid, string> _workflowToJob = new();
    private readonly SemaphoreSlim _jobSemaphore = new(1);
    private readonly IHubContext<DownloadHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<DownloadService> _logger;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public DownloadService(
        IHubContext<DownloadHub> hub,
        SettingsService settings,
        ILogger<DownloadService> logger,
        JobRestorer jobRestorer,
        IHttpClientFactory httpClientFactory)
    {
        _hub = hub;
        _settings = settings;
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        foreach (var job in jobRestorer.RestoreAll())
            _jobs.TryAdd(job.Id, job);
    }

    private HttpClient Http => _httpClientFactory.CreateClient("SldlDaemon");

    // ── Public API ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetAvailableProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            var profiles = await Http.GetFromJsonAsync<List<ProfileSummaryDto>>("api/profiles", _json, ct);
            return profiles?.Select(p => p.Name).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch profiles from daemon; returning empty list");
            return [];
        }
    }

    // Synchronous overload kept for backward compatibility with Razor component callsites.
    public IReadOnlyList<string> GetAvailableProfiles()
    {
        try { return GetAvailableProfilesAsync().GetAwaiter().GetResult(); }
        catch { return []; }
    }

    public DownloadJob CreateJob(string input, bool albumMode = false, string? profile = null, List<ExtraArg>? extraArgs = null)
    {
        var s = _settings.Get();
        var merged = (s.DefaultExtraArgs ?? []).Concat(extraArgs ?? [])
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
            DownloadPath = GetDownloadPath(Guid.NewGuid().ToString("N")[..8])
        };
        // Stable job ID already set in DownloadJob ctor; replace DownloadPath using it
        job.DownloadPath = GetDownloadPath(job.Id);

        _jobs.TryAdd(job.Id, job);
        _ = Task.Run(() => ProcessJobAsync(job));
        return job;
    }

    public DownloadJob? GetJob(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<DownloadJob> GetAllJobs() => _jobs.Values.OrderByDescending(j => j.CreatedAt);

    /// <summary>Returns the daemon workflow ID for an app job, or null if not yet submitted.</summary>
    public Guid? GetWorkflowId(string jobId)
        => _jobToWorkflow.TryGetValue(jobId, out var wf) ? wf : null;

    /// <summary>Reverse lookup: daemon workflow ID → app job ID. Used by the event bridge.</summary>
    public string? GetJobIdForWorkflow(Guid workflowId)
        => _workflowToJob.TryGetValue(workflowId, out var id) ? id : null;

    public bool CancelJob(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status is not (JobStatus.Running or JobStatus.Queued)) return false;

        job.Cts.Cancel();
        job.Status = JobStatus.Cancelled;
        _ = _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());

        if (_jobToWorkflow.TryGetValue(id, out var workflowId))
            _ = CancelWorkflowAsync(workflowId);

        return true;
    }

    public bool DeleteJob(string id)
    {
        CancelJob(id);
        if (_jobs.TryRemove(id, out _))
        {
            if (_jobToWorkflow.TryRemove(id, out var wf))
                _workflowToJob.TryRemove(wf, out _);
            return true;
        }
        return false;
    }

    public bool RetryTrack(string jobId, int trackIndex)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (trackIndex < 0 || trackIndex >= job.Tracks.Count) return false;
        var track = job.Tracks[trackIndex];
        if (track.State is not "Failed") return false;

        // Reset UI state and submit a one-track extract job to the daemon
        track.State = "Initial";
        track.Progress = 0;
        track.BytesTransferred = 0;
        track.TotalBytes = 0;
        track.FailureReason = null;
        _ = _hub.Clients.All.SendAsync("TrackStateChanged", job.Id, track.Artist, track.Title, "Initial", (string?)null, (string?)null);

        _ = Task.Run(() => SubmitRetryJobAsync(job, track));
        return true;
    }

    public bool ResumeJob(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status is JobStatus.Running or JobStatus.Queued) return false;

        job.Status = JobStatus.Queued;
        job.Error = null;
        job.CompletedAt = null;
        job.Cts = new CancellationTokenSource();

        foreach (var track in job.Tracks.Where(t => t.State is "Failed" or "Initial"))
        {
            track.State = "Initial";
            track.Progress = 0;
            track.BytesTransferred = 0;
            track.TotalBytes = 0;
            track.FailureReason = null;
        }

        _ = _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
        _ = Task.Run(() => ProcessJobAsync(job));
        return true;
    }

    public IEnumerable<string> GetDownloadedFiles(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || !Directory.Exists(job.DownloadPath)) return [];
        return Directory.GetFiles(job.DownloadPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_index.csv") && !f.EndsWith("tracks.csv") && !f.EndsWith(".incomplete"))
            .Select(f => Path.GetRelativePath(job.DownloadPath, f));
    }

    public string? GetFilePath(string jobId, string filename)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        return Directory.GetFiles(job.DownloadPath, filename, SearchOption.AllDirectories).FirstOrDefault();
    }

    // ── Event bridge integration ────────────────────────────────────────────

    /// <summary>
    /// Called by SldlEventBridge when a song.state-changed event arrives for a tracked workflow.
    /// </summary>
    public void UpdateTrackFromEvent(string jobId, SongStateChangedEventDto payload)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        var track = job.Tracks.FirstOrDefault(t =>
            string.Equals(t.Artist, payload.Query.Artist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Title, payload.Query.Title, StringComparison.OrdinalIgnoreCase));

        if (track is null)
        {
            // New track from daemon — add to job
            track = new TrackInfo
            {
                Artist = payload.Query.Artist ?? "",
                Title = payload.Query.Title ?? "",
                Album = payload.Query.Album ?? "",
            };
            job.Tracks.Add(track);
        }

        var newState = DaemonStateMapper.ToAppState(payload.State);
        track.State = newState;
        track.FailureReason = DaemonStateMapper.ToAppFailureReason(payload.FailureReason);
        if (!string.IsNullOrEmpty(payload.DownloadPath))
            track.DownloadPath = payload.DownloadPath;

        _ = _hub.Clients.All.SendAsync("TrackStateChanged",
            job.Id, track.Artist, track.Title, newState,
            track.FailureReason, track.DownloadPath);

        RecalculateOverallProgress(job);
    }

    /// <summary>
    /// Called by SldlEventBridge when a download.progress event arrives.
    /// </summary>
    public void UpdateTrackProgress(string jobId, Guid daemonJobId, long bytesTransferred, long totalBytes)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        // Progress events carry the daemon job ID but not the track identity.
        // We update whichever track is currently in Downloading state.
        var track = job.Tracks.FirstOrDefault(t => t.State == "Downloading");
        if (track is null) return;

        track.BytesTransferred = bytesTransferred;
        track.TotalBytes = totalBytes;
        track.Progress = totalBytes > 0 ? (double)bytesTransferred / totalBytes * 100 : 0;

        _ = _hub.Clients.All.SendAsync("DownloadProgress",
            job.Id, track.Artist, track.Title,
            track.BytesTransferred, track.TotalBytes, track.Progress);
    }

    /// <summary>
    /// Called by SldlEventBridge when a workflow.upserted state event arrives for a tracked workflow.
    /// Recalculates the app-level JobStatus from the daemon workflow aggregate state.
    /// </summary>
    public void HandleWorkflowUpserted(string jobId, ServerWorkflowState workflowState)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        var newStatus = workflowState switch
        {
            ServerWorkflowState.Completed => JobStatus.Completed,
            ServerWorkflowState.Failed => JobStatus.Failed,
            _ => job.Status // keep current (Active)
        };

        if (newStatus == job.Status) return;
        job.Status = newStatus;
        if (newStatus is JobStatus.Completed or JobStatus.Failed)
            job.CompletedAt = DateTime.UtcNow;

        _ = _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
    }

    // ── Private: job submission ────────────────────────────────────────────

    private async Task ProcessJobAsync(DownloadJob job)
    {
        try { await _jobSemaphore.WaitAsync(job.Cts.Token); }
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

            var workflowId = Guid.NewGuid();
            var s = _settings.Get();
            var options = BuildSubmissionOptions(job, workflowId, s);

            JobSummaryDto? summary;
            if (job.InputType is InputType.Spotify or InputType.YouTube or InputType.Bandcamp
                                 or InputType.CSV or InputType.Tracklist)
            {
                // Extract job: daemon handles parsing/expansion into child jobs
                summary = await SubmitExtractAsync(job.Input, options, job.Cts.Token);
            }
            else if (job.AlbumMode)
            {
                var q = new AlbumQueryDto(Album: job.Input);
                summary = await SubmitAlbumAsync(q, options, job.Cts.Token);
            }
            else
            {
                // Free-text: treat as extract; daemon will attempt artist – title split
                summary = await SubmitExtractAsync(job.Input, options, job.Cts.Token);
            }

            if (summary is null)
                throw new InvalidOperationException("Daemon returned no job summary after submission.");

            // Register workflow mapping so the event bridge can route live events
            _jobToWorkflow[job.Id] = summary.WorkflowId;
            _workflowToJob[summary.WorkflowId] = job.Id;
            _logger.LogInformation("Job {AppId} submitted as daemon workflow {WorkflowId}", job.Id, summary.WorkflowId);

            // The event bridge will update job.Status when the workflow completes.
            // We wait here so the semaphore stays held (sequential policy) until done.
            await WaitForWorkflowAsync(summary.WorkflowId, job);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} submission failed", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.CompletedAt ??= DateTime.UtcNow;
            await _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
            _jobSemaphore.Release();
        }
    }

    private async Task SubmitRetryJobAsync(DownloadJob job, TrackInfo track)
    {
        try
        {
            var s = _settings.Get();
            var workflowId = Guid.NewGuid();
            var options = BuildSubmissionOptions(job, workflowId, s);
            var query = new SongQueryDto(Artist: track.Artist, Title: track.Title, Album: track.Album);
            var summary = await SubmitSongAsync(query, options, CancellationToken.None);
            if (summary is null) return;

            // Add secondary workflow mapping (not replacing primary)
            _workflowToJob.TryAdd(summary.WorkflowId, job.Id);
            _logger.LogInformation("Retry for {Artist} – {Title} submitted as workflow {WorkflowId}",
                track.Artist, track.Title, summary.WorkflowId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry submission failed for {Artist} – {Title}", track.Artist, track.Title);
            track.State = "Failed";
            track.FailureReason = "Other";
            _ = _hub.Clients.All.SendAsync("TrackStateChanged", job.Id, track.Artist, track.Title, "Failed", "Other", (string?)null);
        }
    }

    private async Task WaitForWorkflowAsync(Guid workflowId, DownloadJob job)
    {
        // Poll until the event bridge updates the job status (or cancellation).
        // The event bridge is the source of truth; polling is the fallback.
        const int PollIntervalMs = 2000;
        while (!job.Cts.IsCancellationRequested)
        {
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                return;

            await Task.Delay(PollIntervalMs, job.Cts.Token);

            // Fetch workflow snapshot as fallback if events haven't arrived
            try
            {
                var wf = await Http.GetFromJsonAsync<WorkflowDetailDto>(
                    $"api/workflows/{workflowId}", _json, job.Cts.Token);

                if (wf is null) continue;

                if (wf.Summary.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed)
                {
                    HandleWorkflowUpserted(job.Id, wf.Summary.State);
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Workflow poll failed for {WorkflowId}; retrying", workflowId);
            }
        }
    }

    // ── Private: HTTP helpers ──────────────────────────────────────────────

    private async Task<JobSummaryDto?> SubmitExtractAsync(string input, SubmissionOptionsDto options, CancellationToken ct)
    {
        var body = new SubmitExtractJobRequestDto(input, AutoStartExtractedResult: true, Options: options);
        return await PostJobAsync("api/jobs/extract", body, ct);
    }

    private async Task<JobSummaryDto?> SubmitSongAsync(SongQueryDto query, SubmissionOptionsDto options, CancellationToken ct)
    {
        var body = new SubmitSongJobRequestDto(query, Options: options);
        return await PostJobAsync("api/jobs/downloads/song", body, ct);
    }

    private async Task<JobSummaryDto?> SubmitAlbumAsync(AlbumQueryDto query, SubmissionOptionsDto options, CancellationToken ct)
    {
        var body = new SubmitAlbumJobRequestDto(query, Options: options);
        return await PostJobAsync("api/jobs/downloads/album", body, ct);
    }

    private async Task<JobSummaryDto?> PostJobAsync<T>(string url, T body, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(url, body, _json, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Daemon rejected job submission at {Url}: {Status} {Body}", url, (int)response.StatusCode, err);
            return null;
        }
        return await response.Content.ReadFromJsonAsync<JobSummaryDto>(_json, ct);
    }

    private async Task CancelWorkflowAsync(Guid workflowId)
    {
        try
        {
            using var response = await Http.PostAsync($"api/workflows/{workflowId}/cancel", null);
            _logger.LogInformation("Cancel workflow {WorkflowId}: {Status}", workflowId, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel daemon workflow {WorkflowId}", workflowId);
        }
    }

    private static SubmissionOptionsDto BuildSubmissionOptions(DownloadJob job, Guid workflowId, AppSettings s)
    {
        var profiles = new List<string>();
        if (!string.IsNullOrEmpty(job.Profile))
            profiles.Add(job.Profile);

        return new SubmissionOptionsDto(
            WorkflowId: workflowId,
            OutputParentDir: job.DownloadPath,
            ProfileNames: profiles.Count > 0 ? profiles : null);
    }

    private void RecalculateOverallProgress(DownloadJob job)
    {
        var done = job.Tracks.Count(t => t.State is "Done" or "AlreadyExists");
        var failed = job.Tracks.Count(t => t.State == "Failed");
        _ = _hub.Clients.All.SendAsync("OverallProgress", job.Id, done, failed, job.Tracks.Count);
    }

    private string GetDownloadPath(string jobId)
    {
        var s = _settings.Get();
        var basePath = string.IsNullOrEmpty(s.DownloadPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "downloads")
            : s.DownloadPath;
        var jobPath = Path.Combine(basePath, jobId);
        Directory.CreateDirectory(jobPath);
        return jobPath;
    }
}

