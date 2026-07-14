using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Sockseek.Api;
using SldlWeb.Hubs;
using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// Orchestrates job submission and tracking against the sockseek daemon HTTP API.
/// Each app <see cref="DownloadJob"/> maps to a daemon workflow. Tracks are updated by
/// <see cref="SldlEventBridge"/> from live SignalR batches, with HTTP snapshot polling
/// as a fallback when batches are delayed or missing.
///
/// Jobs live in memory only for the current process. <see cref="JobRestorer"/> can
/// best-effort rebuild completed jobs from on-disk <c>tracks.csv</c>/<c>_index.csv</c>,
/// but workflow↔app mappings are not durable. Upstream sockseek is designing daemon-side
/// persistence (see submodule branch <c>persistence</c> /
/// <c>docs/temp/PERSISTENCE-DISCUSSION.md</c>): SQLite projection + startup HTTP snapshots
/// + ordered deltas. When that lands, prefer hydrating the UI from daemon snapshots (and a
/// shared client store like <c>WorkflowClientStore</c>) rather than inventing a parallel
/// app-level job database or replaying Log/activity events for durable state.
/// </summary>
public class DownloadService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    // app job ID → daemon workflow ID (set after successful submission; process-local only)
    private readonly ConcurrentDictionary<string, Guid> _jobToWorkflow = new();
    // daemon workflow ID → app job ID (reverse lookup for event bridge; process-local only)
    private readonly ConcurrentDictionary<Guid, string> _workflowToJob = new();
    private readonly SemaphoreSlim _jobSemaphore = new(1);
    private readonly IHubContext<DownloadHub> _hub;
    private readonly SockseekApiClient _sldl;
    private readonly SettingsService _settings;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(
        IHubContext<DownloadHub> hub,
        SettingsService settings,
        ILogger<DownloadService> logger,
        JobRestorer jobRestorer,
        SockseekApiClient sldl)
    {
        _hub = hub;
        _settings = settings;
        _logger = logger;
        _sldl = sldl;

        // Best-effort UI rebuild from download folders only — does not restore daemon
        // workflow IDs or live/incomplete transfers (see class remarks / persistence branch).
        foreach (var job in jobRestorer.RestoreAll())
            _jobs.TryAdd(job.Id, job);
    }

    // ── Public API ────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetAvailableProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            var profiles = await _sldl.GetProfilesAsync(ct);
            return profiles.Select(p => p.Name).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch profiles from daemon; returning empty list");
            return [];
        }
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
    /// Called by SldlEventBridge when a song.searching event arrives.
    /// Ensures the track exists in the job list immediately (before any state-change events).
    /// </summary>
    public void EnsureTrack(string jobId, SongQueryDto query)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        var exists = job.Tracks.Any(t =>
            string.Equals(t.Title, query.Title, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(query.Artist) || string.IsNullOrEmpty(t.Artist) ||
             string.Equals(t.Artist, query.Artist, StringComparison.OrdinalIgnoreCase)));

        if (!exists)
        {
            job.Tracks.Add(new TrackInfo
            {
                Artist = query.Artist ?? "",
                Title = query.Title ?? "",
                Album = query.Album ?? "",
                State = "Searching"
            });
            RecalculateOverallProgress(job);
            _ = _hub.Clients.All.SendAsync("TrackList", job.Id, job.Tracks.ToList());
        }
    }

    /// <summary>
    /// Called by SldlEventBridge when a song.state-changed event arrives for a tracked workflow.
    /// </summary>
    public void UpdateTrackFromEvent(string jobId, SongStateChangedEventDto payload, bool createIfMissing = true)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        // Match on title; also require artist match only when both sides are non-empty
        // (job.upserted fallback events often carry no artist, which previously caused duplicates).
        var track = job.Tracks.FirstOrDefault(t =>
            string.Equals(t.Title, payload.Query.Title, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(payload.Query.Artist) ||
             string.IsNullOrEmpty(t.Artist) ||
             string.Equals(t.Artist, payload.Query.Artist, StringComparison.OrdinalIgnoreCase)));

        if (track is null)
        {
            if (!createIfMissing) return;
            // New track from daemon — add to job
            track = new TrackInfo
            {
                Artist = payload.Query.Artist ?? "",
                Title = payload.Query.Title ?? "",
                Album = payload.Query.Album ?? "",
            };
            job.Tracks.Add(track);
        }
        else
        {
            // Backfill fields the first event may have omitted
            if (!string.IsNullOrEmpty(payload.Query.Artist) && string.IsNullOrEmpty(track.Artist))
                track.Artist = payload.Query.Artist;
            if (!string.IsNullOrEmpty(payload.Query.Album) && string.IsNullOrEmpty(track.Album))
                track.Album = payload.Query.Album;
        }

        var newState = DaemonStateMapper.ToAppState(payload);
        track.State = newState;
        track.FailureReason = DaemonStateMapper.ToAppFailureReason(payload.FailureReason);
        if (!string.IsNullOrEmpty(payload.FailureMessage))
        {
            track.FailureReason = string.IsNullOrEmpty(track.FailureReason)
                ? payload.FailureMessage
                : $"{track.FailureReason}: {payload.FailureMessage}";
            _logger.LogWarning("[{JobId}] {Track} failed: {Reason}",
                jobId, $"{track.Artist} – {track.Title}", track.FailureReason);
        }
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
    public void HandleWorkflowUpserted(string jobId, ServerWorkflowState workflowState, string? error = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        var newStatus = workflowState switch
        {
            ServerWorkflowState.Completed => JobStatus.Completed,
            ServerWorkflowState.Failed => JobStatus.Failed,
            _ => job.Status // keep current (Active)
        };

        if (newStatus == job.Status && string.IsNullOrEmpty(error)) return;
        job.Status = newStatus;
        if (!string.IsNullOrEmpty(error))
            job.Error = error;
        if (newStatus is JobStatus.Completed or JobStatus.Failed)
            job.CompletedAt = DateTime.UtcNow;

        _ = _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
    }

    /// <summary>Record a daemon-side failure message on the app job (and log).</summary>
    public void ReportJobFailure(string jobId, string message, string? detail = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        job.Error = message;
        if (detail is not null)
            _logger.LogWarning("[{JobId}] {Message}\n{Detail}", jobId, message, detail);
        else
            _logger.LogWarning("[{JobId}] {Message}", jobId, message);

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

            // Register BEFORE submitting so song.searching events are routed correctly
            // even if they fire before the HTTP response returns.
            _jobToWorkflow[job.Id] = workflowId;
            _workflowToJob[workflowId] = job.Id;

            JobSummaryDto summary;
            if (job.InputType is InputType.Spotify or InputType.YouTube or InputType.Bandcamp
                                 or InputType.CSV or InputType.Tracklist)
            {
                // List/CSV extractors expect a filesystem path. Inline pastes are written
                // to a temp file under the job folder before submission.
                var extractInput = MaterializeListInputIfNeeded(job);
                var extractOptions = BuildSubmissionOptions(job, workflowId, s, job.InputType);
                summary = await SubmitExtractAsync(extractInput, ToDaemonInputTypeString(job.InputType), extractOptions, job.Cts.Token);
            }
            else if (job.AlbumMode)
            {
                var q = new AlbumQueryDto(Album: job.Input);
                summary = await SubmitAlbumAsync(q, options, job.Cts.Token);
            }
            else
            {
                // Free-text: treat as extract; daemon will attempt artist – title split
                var extractOptions = BuildSubmissionOptions(job, workflowId, s, job.InputType);
                summary = await SubmitExtractAsync(job.Input, ToDaemonInputTypeString(job.InputType), extractOptions, job.Cts.Token);
            }

            if (summary is null)
                throw new InvalidOperationException("Daemon returned no job summary after submission.");

            // Always register the ID the daemon actually assigned.
            // The daemon may ignore the WorkflowId we provided in SubmissionOptions,
            // so we must use summary.WorkflowId for event routing.
            if (summary.WorkflowId != workflowId)
            {
                _logger.LogDebug("Daemon assigned workflow {Actual} (requested {Requested})",
                    summary.WorkflowId, workflowId);
                // Remove the speculative pre-registration and add the real one
                _workflowToJob.TryRemove(workflowId, out _);
                _workflowToJob[summary.WorkflowId] = job.Id;
                _jobToWorkflow[job.Id] = summary.WorkflowId;
            }

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
        // Live updates should prefer workflowUpdateBatch; this HTTP poll is recovery when
        // batches are late/missing. Persistence plans treat HTTP snapshots the same way
        // (startup + sequence-gap hydrate), eventually via a shared client state store.
        const int PollIntervalMs = 2000;
        while (!job.Cts.IsCancellationRequested)
        {
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                return;

            await Task.Delay(PollIntervalMs, job.Cts.Token);

            // Fetch workflow snapshot as fallback if events haven't arrived
            try
            {
                var wf = await _sldl.GetWorkflowAsync(workflowId, includeAll: true, job.Cts.Token);

                if (wf is null) continue;

                // Keep the UI track list in sync even if SignalR batches are delayed.
                SyncTracksFromWorkflow(job, wf);

                if (wf.Summary.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed)
                {
                    string? error = null;
                    if (wf.Summary.State is ServerWorkflowState.Failed)
                    {
                        var failed = wf.Jobs.FirstOrDefault(j =>
                            j.TerminalOutcome == ServerJobTerminalOutcome.Failed
                            && !string.IsNullOrEmpty(j.FailureMessage));
                        error = failed?.FailureMessage
                            ?? failed?.FailureReason?.ToString()
                            ?? "Workflow failed";
                        _logger.LogWarning("[{JobId}] Workflow failed: {Error}", job.Id, error);
                    }

                    HandleWorkflowUpserted(job.Id, wf.Summary.State, error);
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

    /// <summary>
    /// Mirror song jobs from a workflow HTTP snapshot into the UI track list.
    /// Used as a fallback when SignalR activity events are missing or delayed.
    ///
    /// This is the same recovery idea as the planned client-side
    /// snapshot + ordered-delta model (see sockseek <c>persistence</c> branch): when live
    /// event stream state is incomplete, rehydrate UI rows from an HTTP snapshot rather
    /// than inventing durable app storage. It does not survive process restart — that needs
    /// daemon-side history (or at least a durable app↔workflow mapping) from that work.
    /// </summary>
    private void SyncTracksFromWorkflow(DownloadJob job, WorkflowDetailDto wf)
    {
        var changed = false;
        foreach (var summary in wf.Jobs.Where(j => j.Kind == ServerJobKind.Song))
        {
            var query = QueryFromJobSummary(summary);
            var track = job.Tracks.FirstOrDefault(t =>
                string.Equals(t.Title, query.Title, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(query.Artist) || string.IsNullOrEmpty(t.Artist) ||
                 string.Equals(t.Artist, query.Artist, StringComparison.OrdinalIgnoreCase)));

            if (track is null)
            {
                track = new TrackInfo
                {
                    Artist = query.Artist ?? "",
                    Title = query.Title ?? "",
                    Album = query.Album ?? "",
                };
                job.Tracks.Add(track);
                changed = true;
            }

            var newState = DaemonStateMapper.ToAppState(summary);
            if (!string.Equals(track.State, newState, StringComparison.Ordinal))
            {
                track.State = newState;
                changed = true;
            }

            if (summary.TerminalOutcome == ServerJobTerminalOutcome.Failed
                && !string.IsNullOrEmpty(summary.FailureMessage)
                && track.FailureReason != summary.FailureMessage)
            {
                track.FailureReason = summary.FailureMessage;
                changed = true;
            }
        }

        if (!changed) return;

        RecalculateOverallProgress(job);
        _ = _hub.Clients.All.SendAsync("TrackList", job.Id, job.Tracks.ToList());
    }

    private static SongQueryDto QueryFromJobSummary(JobSummaryDto payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.ItemName) && payload.ItemName.Contains(" - "))
        {
            var parts = payload.ItemName.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                return new SongQueryDto(Artist: parts[0], Title: parts[1]);
        }

        if (!string.IsNullOrWhiteSpace(payload.QueryText))
        {
            var parts = payload.QueryText.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                return new SongQueryDto(Artist: parts[0], Title: parts[1]);
            return new SongQueryDto(Title: payload.QueryText);
        }

        return new SongQueryDto(Title: payload.ItemName);
    }

    // ── Private: HTTP helpers ──────────────────────────────────────────────

    private async Task<JobSummaryDto> SubmitExtractAsync(string input, string? inputType, SubmissionOptionsDto options, CancellationToken ct)
    {
        var body = new SubmitExtractJobRequestDto(input, InputType: inputType, AutoStartExtractedResult: true, Options: options);
        return await _sldl.SubmitExtractJobAsync(body, ct);
    }

    private async Task<JobSummaryDto> SubmitSongAsync(SongQueryDto query, SubmissionOptionsDto options, CancellationToken ct)
    {
        var body = new SubmitSongJobRequestDto(query, Options: options);
        return await _sldl.SubmitSongJobAsync(body, ct);
    }

    private async Task<JobSummaryDto> SubmitAlbumAsync(AlbumQueryDto query, SubmissionOptionsDto options, CancellationToken ct)
    {
        var body = new SubmitAlbumJobRequestDto(query, Options: options);
        return await _sldl.SubmitAlbumJobAsync(body, ct);
    }

    private async Task CancelWorkflowAsync(Guid workflowId)
    {
        try
        {
            var count = await _sldl.CancelWorkflowAsync(workflowId);
            _logger.LogInformation("Cancel workflow {WorkflowId}: {Count} job(s) cancelled", workflowId, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel daemon workflow {WorkflowId}", workflowId);
        }
    }

    private static SubmissionOptionsDto BuildSubmissionOptions(DownloadJob job, Guid workflowId, AppSettings s, InputType? extractInputType = null)
    {
        var profiles = new List<string>();
        if (!string.IsNullOrEmpty(job.Profile))
            profiles.Add(job.Profile);

        DownloadSettingsPatchDto? downloadSettings = null;
        if (extractInputType is not null)
        {
            // v3 defaults string/list extracts to album; keep song-mode when the UI did not request album.
            Sockseek.Core.ExtractionMode? requestedMode = null;
            if (!job.AlbumMode && extractInputType is InputType.Search or InputType.Tracklist)
                requestedMode = Sockseek.Core.ExtractionMode.Song;

            downloadSettings = new DownloadSettingsPatchDto(
                Extraction: new ExtractionSettingsPatchDto(
                    InputType: ToDaemonInputType(extractInputType.Value),
                    RequestedMode: requestedMode));
        }

        return new SubmissionOptionsDto(
            WorkflowId: workflowId,
            OutputParentDir: job.DownloadPath,
            ProfileNames: profiles.Count > 0 ? profiles : null,
            DownloadSettings: downloadSettings);
    }

    private static string ToDaemonInputTypeString(InputType inputType)
        => ToDaemonInputType(inputType).ToString();

    /// <summary>
    /// Sockseek List/CSV extractors read a file path. When the UI detected an
    /// inline paste, materialize it under the job download folder.
    /// List lines are space-separated fields (query, conditions…), so artist/title
    /// queries with spaces must be quoted — see sockseek list-file format.
    /// </summary>
    private string MaterializeListInputIfNeeded(DownloadJob job)
    {
        if (job.InputType is not (InputType.Tracklist or InputType.CSV))
            return job.Input;

        // Already a real path (e.g. restored job or dropped file).
        if (job.Input.IndexOf('\n') < 0
            && job.Input.IndexOf('\r') < 0
            && File.Exists(job.Input))
            return job.Input;

        Directory.CreateDirectory(job.DownloadPath);
        var ext = job.InputType == InputType.CSV ? ".csv" : ".txt";
        var path = Path.Combine(job.DownloadPath, $"input{ext}");

        var content = job.InputType == InputType.Tracklist
            ? FormatTracklistForSockseek(job.Input, job.AlbumMode)
            : job.Input.Replace("\r\n", "\n").Replace('\r', '\n');

        File.WriteAllText(path, content);
        _logger.LogInformation("[{JobId}] Wrote inline {Type} input to {Path}", job.Id, job.InputType, path);
        return path;
    }

    /// <summary>
    /// Convert a freeform "Artist - Title" paste into sockseek list-file lines.
    /// Unquoted spaces would otherwise split the query into fake condition tokens.
    /// </summary>
    internal static string FormatTracklistForSockseek(string input, bool albumMode)
    {
        var prefix = albumMode ? "a:" : "s:";
        var lines = input
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var sb = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = InputTypeDetector.CleanTrackLine(raw);
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Preserve an explicit sockseek mode/conditions line the user already wrote.
            if (line.StartsWith("a:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("s:", StringComparison.OrdinalIgnoreCase)
                || (line.StartsWith('"') && line.EndsWith('"')))
            {
                sb.AppendLine(line);
                continue;
            }

            var escaped = line.Replace('"', '\'');
            sb.Append(prefix).Append('"').Append(escaped).Append('"').AppendLine();
        }

        return sb.ToString();
    }

    private static Sockseek.Core.InputType ToDaemonInputType(InputType inputType)
        => inputType switch
        {
            InputType.Spotify => Sockseek.Core.InputType.Spotify,
            InputType.YouTube => Sockseek.Core.InputType.YouTube,
            InputType.Bandcamp => Sockseek.Core.InputType.Bandcamp,
            InputType.CSV => Sockseek.Core.InputType.CSV,
            InputType.Tracklist => Sockseek.Core.InputType.List,
            _ => Sockseek.Core.InputType.String
        };

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

