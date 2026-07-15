using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sockseek.Api;
using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// Background service that subscribes to the sockseek daemon's SignalR event hub and
/// dispatches state-change events to DownloadService so the UI stays in sync.
///
/// The hub lives at {daemonUrl}/api/events. Global events arrive on <c>serverEvent</c>;
/// workflow-scoped updates arrive on <c>workflowUpdateBatch</c> after <c>SubscribeAll</c>.
/// Reconnection uses exponential back-off capped at 30 s.
///
/// Longer-term (sockseek <c>persistence</c> branch): prefer a shared client state store
/// that applies HTTP snapshots + compact ordered deltas, with sequence-gap recovery —
/// see <c>HandleWorkflowBatch</c> and the GUI-EVENT-DELTAS notes on
/// <c>WorkflowUpdateBatchDto</c>.
/// </summary>
public sealed class SldlEventBridge : BackgroundService
{
    private readonly DownloadService _downloadService;
    private readonly IConfiguration _config;
    private readonly ILogger<SldlEventBridge> _logger;

    public SldlEventBridge(
        DownloadService downloadService,
        IConfiguration config,
        ILogger<SldlEventBridge> logger)
    {
        _downloadService = downloadService;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var daemonUrl = _config["SldlDaemonUrl"]?.TrimEnd('/') ?? "http://localhost:5030";
        var hubUrl = $"{daemonUrl}/api/events";

        while (!stoppingToken.IsCancellationRequested)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .AddJsonProtocol(options =>
                {
                    options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                    SockseekApiJson.ConfigureSerializerOptions(options.PayloadSerializerOptions);
                })
                .WithAutomaticReconnect(new ExponentialBackoff())
                .Build();

            connection.On<ServerEventEnvelopeDto>("serverEvent",
                envelope => HandleEvent(ServerEventPayloadConverter.RehydrateEnvelope(envelope)));

            // Sockseek v3 sends workflow-scoped events as batches, not individual serverEvent messages.
            connection.On<WorkflowUpdateBatchDto>("workflowUpdateBatch",
                batch => HandleWorkflowBatch(ServerEventPayloadConverter.RehydrateBatch(batch)));

            connection.Closed += ex =>
            {
                _logger.LogWarning(ex, "Event hub connection closed; will reconnect");
                return Task.CompletedTask;
            };

            connection.Reconnecting += ex =>
            {
                _logger.LogDebug(ex, "Event hub reconnecting…");
                return Task.CompletedTask;
            };

            connection.Reconnected += async id =>
            {
                _logger.LogInformation("Event hub reconnected (connection {Id}); resubscribing", id);
                try { await connection.InvokeAsync("SubscribeAll"); }
                catch (Exception ex) { _logger.LogWarning(ex, "SubscribeAll failed after reconnect"); }
            };

            try
            {
                await connection.StartAsync(stoppingToken);
                _logger.LogInformation("Connected to daemon event hub at {Url}", hubUrl);

                // Subscribe to all workflows so the daemon sends us events.
                // Without this the hub sends nothing (opt-in subscription model).
                await connection.InvokeAsync("SubscribeAll", stoppingToken);
                _logger.LogInformation("Subscribed to all daemon events");

                // Keep running until cancellation or a fatal disconnect
                await AwaitDisconnectAsync(connection, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to daemon event hub at {Url}; retrying in 5 s", hubUrl);
            }
            finally
            {
                try { await connection.DisposeAsync(); } catch { /* best effort */ }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task AwaitDisconnectAsync(HubConnection connection, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex => { tcs.TrySetResult(); return Task.CompletedTask; };
        using var reg = ct.Register(() => tcs.TrySetResult());
        await tcs.Task;
    }

    private void HandleWorkflowBatch(WorkflowUpdateBatchDto batch)
    {
        // Interim client path for sockseek's current summary-heavy WorkflowUpdateBatchDto.
        // Upstream plan (submodule branch `persistence`, docs/temp/PERSISTENCE-DISCUSSION.md
        // and the GUI-EVENT-DELTAS TODO on WorkflowUpdateBatchDto):
        //   - HTTP snapshots for startup / sequence-gap recovery
        //   - SignalR batches with compact ordered patches (not full JobSummaryDto per edge)
        //   - WorkflowClientStore (or equivalent) applies snapshot + deltas for CLI and GUI
        //   - Durable state must not be reconstructed by replaying activity/log events
        // Keep apply order: job upserts → workflow summary → activity → progress.
        try
        {
            _logger.LogDebug(
                "Batch wf={WfId} seq={Seq}: {Jobs} jobs, {Activity} activity, {Progress} progress",
                batch.WorkflowId, batch.Sequence,
                batch.JobUpserts.Count, batch.Activity.Count, batch.Progress.Count);

            foreach (var summary in batch.JobUpserts)
            {
                HandleJobUpserted(new ServerEventEnvelopeDto(
                    Sequence: batch.Sequence,
                    Type: "job.upserted",
                    OccurredAtUtc: batch.OccurredAtUtc,
                    Category: "state",
                    SnapshotInvalidation: true,
                    WorkflowId: batch.WorkflowId,
                    Payload: summary));
            }

            if (batch.Workflow is not null)
            {
                HandleWorkflowUpserted(new ServerEventEnvelopeDto(
                    Sequence: batch.Sequence,
                    Type: "workflow.upserted",
                    OccurredAtUtc: batch.OccurredAtUtc,
                    Category: "state",
                    SnapshotInvalidation: true,
                    WorkflowId: batch.WorkflowId,
                    Payload: batch.Workflow));
            }

            foreach (var envelope in batch.Activity)
                HandleEvent(envelope);

            foreach (var progress in batch.Progress)
            {
                HandleDownloadProgress(new ServerEventEnvelopeDto(
                    Sequence: batch.Sequence,
                    Type: "download.progress",
                    OccurredAtUtc: batch.OccurredAtUtc,
                    Category: "activity",
                    SnapshotInvalidation: false,
                    WorkflowId: batch.WorkflowId,
                    Payload: progress));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling workflowUpdateBatch for {WorkflowId}", batch.WorkflowId);
        }
    }

    private void HandleEvent(ServerEventEnvelopeDto envelope)
    {
        try
        {
            // Temporary: log all events at Info level to diagnose routing issues.
            _logger.LogInformation("Event: {Type}  wf={WfId}", envelope.Type, envelope.WorkflowId);

            switch (envelope.Type)
            {
                case "song.searching":
                    HandleSongSearching(envelope);
                    break;

                case "song.state-changed":
                    HandleSongStateChanged(envelope);
                    break;

                case "download.started":
                    HandleDownloadStarted(envelope);
                    break;

                case "download.progress":
                    HandleDownloadProgress(envelope);
                    break;

                case "workflow.upserted":
                    HandleWorkflowUpserted(envelope);
                    break;

                case "job.upserted":
                    // SnapshotInvalidation=true events; we use the embedded summary
                    // to keep workflow→job mapping consistent but don't change UI state
                    // beyond what song.state-changed events already cover.
                    HandleJobUpserted(envelope);
                    break;

                case "diagnostic.error":
                    HandleDiagnosticError(envelope);
                    break;

                case "extraction.failed":
                    HandleExtractionFailed(envelope);
                    break;

                case "job.message":
                    HandleJobMessage(envelope);
                    break;

                default:
                    _logger.LogDebug("Ignoring daemon event type '{Type}'", envelope.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling daemon event '{Type}'", envelope.Type);
        }
    }

    private void HandleSongSearching(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not SongSearchingEventDto payload) return;

        var workflowId = payload.WorkflowId != Guid.Empty ? payload.WorkflowId
            : envelope.WorkflowId ?? Guid.Empty;
        var jobId = _downloadService.GetJobIdForWorkflow(workflowId);
        if (jobId is null)
        {
            _logger.LogWarning("song.searching: untracked workflow {WorkflowId} (envelope={EnvWf})",
                payload.WorkflowId, envelope.WorkflowId);
            return;
        }

        var track = TrackLabel(payload.Query);
        _logger.LogInformation("[{JobId}] Searching  {Track}", jobId, track);

        _downloadService.EnsureTrack(jobId, payload.Query, payload.JobId);
    }

    private void HandleSongStateChanged(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not SongStateChangedEventDto payload) return;

        var workflowId = payload.WorkflowId;
        // Fall back to envelope-level WorkflowId if payload's is empty
        if (workflowId == Guid.Empty) workflowId = envelope.WorkflowId ?? Guid.Empty;
        var jobId = _downloadService.GetJobIdForWorkflow(workflowId);
        if (jobId is null)
        {
            _logger.LogWarning("song.state-changed: untracked workflow {WorkflowId} (envelope={EnvWf})",
                payload.WorkflowId, envelope.WorkflowId);
            return;
        }

        var track = TrackLabel(payload.Query);
        var appState = DaemonStateMapper.ToAppState(payload);
        switch (appState)
        {
            case "Downloading":
                var user = payload.ChosenCandidate?.Username ?? "?";
                var file = payload.ChosenCandidate is { } c ? System.IO.Path.GetFileName(c.Filename) : null;
                _logger.LogInformation("[{JobId}] Downloading {Track}  ←  {User}/{File}",
                    jobId, track, user, file ?? "");
                break;
            case "Done":
                _logger.LogInformation("[{JobId}] Done       {Track}  →  {Path}",
                    jobId, track, payload.DownloadPath ?? "");
                break;
            case "AlreadyExists":
                _logger.LogInformation("[{JobId}] Exists     {Track}", jobId, track);
                break;
            case "Failed":
                _logger.LogWarning("[{JobId}] Failed     {Track}  ({Reason}){Message}",
                    jobId, track,
                    payload.FailureReason?.ToString() ?? "unknown",
                    string.IsNullOrEmpty(payload.FailureMessage) ? "" : $": {payload.FailureMessage}");
                break;
            case "Skipped":
            case "NotFoundLastTime":
                _logger.LogInformation("[{JobId}] Skipped    {Track}", jobId, track);
                break;
        }

        _downloadService.UpdateTrackFromEvent(jobId, payload);
    }

    private void HandleDownloadStarted(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not DownloadStartedEventDto payload) return;

        var workflowId = payload.WorkflowId != Guid.Empty ? payload.WorkflowId
            : envelope.WorkflowId ?? Guid.Empty;
        var jobId = _downloadService.GetJobIdForWorkflow(workflowId);
        if (jobId is null) return;

        // Bind JobId before progress events so concurrent transfers don't share a row.
        _downloadService.EnsureTrack(jobId, payload.Query, payload.JobId);
    }

    private void HandleDownloadProgress(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not DownloadProgressEventDto payload) return;

        var jobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
        if (jobId is null) return;

        _downloadService.UpdateTrackProgress(jobId, payload.JobId, payload.BytesTransferred, payload.TotalBytes);
    }

    private void HandleWorkflowUpserted(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not WorkflowSummaryDto payload) return;

        var jobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
        if (jobId is null) return;

        if (payload.State is ServerWorkflowState.Completed)
            _logger.LogInformation("[{JobId}] Job completed", jobId);
        else if (payload.State is ServerWorkflowState.Failed)
            _logger.LogWarning("[{JobId}] Job failed (workflow {WorkflowId})", jobId, payload.WorkflowId);

        _downloadService.HandleWorkflowUpserted(jobId, payload.State);
    }

    private void HandleDiagnosticError(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not DiagnosticErrorEventDto payload) return;

        var workflowId = payload.WorkflowId
            ?? payload.Summary?.WorkflowId
            ?? envelope.WorkflowId
            ?? Guid.Empty;
        var jobId = workflowId != Guid.Empty
            ? _downloadService.GetJobIdForWorkflow(workflowId)
            : null;

        _logger.LogWarning(
            "[{JobId}] Diagnostic error ({Type}): {Message}",
            jobId ?? workflowId.ToString(),
            payload.ExceptionType,
            payload.Message);

        if (jobId is not null)
            _downloadService.ReportJobFailure(jobId, payload.Message, payload.Exception);
    }

    private void HandleExtractionFailed(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not ExtractionFailedEventDto payload) return;

        var workflowId = payload.Summary.WorkflowId;
        var jobId = _downloadService.GetJobIdForWorkflow(workflowId);
        _logger.LogWarning(
            "[{JobId}] Extraction failed: {Reason}",
            jobId ?? workflowId.ToString(),
            payload.Reason);

        if (jobId is not null)
            _downloadService.ReportJobFailure(jobId, payload.Reason);
    }

    private void HandleJobMessage(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not JobMessageEventDto payload) return;

        var workflowId = payload.Summary.WorkflowId;
        var jobId = _downloadService.GetJobIdForWorkflow(workflowId) ?? workflowId.ToString();
        var level = payload.Level?.ToLowerInvariant();
        if (level is "error" or "warning" or "warn")
        {
            _logger.LogWarning("[{JobId}] {Source}{Message}",
                jobId,
                string.IsNullOrEmpty(payload.Source) ? "" : $"{payload.Source}: ",
                payload.Message);
        }
        else
        {
            _logger.LogInformation("[{JobId}] {Source}{Message}",
                jobId,
                string.IsNullOrEmpty(payload.Source) ? "" : $"{payload.Source}: ",
                payload.Message);
        }
    }

    private void HandleJobUpserted(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not JobSummaryDto payload) return;

        // Surface extract/root job terminal failures that never become song.state-changed.
        if (DaemonStateMapper.IsTerminal(payload)
            && payload.TerminalOutcome == ServerJobTerminalOutcome.Failed
            && !string.IsNullOrEmpty(payload.FailureMessage))
        {
            var failJobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
            if (failJobId is not null)
            {
                _logger.LogWarning("[{JobId}] {Kind} failed: {Message}",
                    failJobId, payload.Kind, payload.FailureMessage);
                _downloadService.ReportJobFailure(failJobId, payload.FailureMessage, payload.FailureDetail);
            }
        }

        if (payload.Kind is not ServerJobKind.Song)
            return;

        var jobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
        if (jobId is null) return;

        var query = QueryFromJobSummary(payload);
        if (!DaemonStateMapper.IsTerminal(payload))
        {
            // Ensure the track appears as soon as the song job is upserted (searching/running).
            _downloadService.EnsureTrack(jobId, query, payload.JobId);
            // Reflect live activity when we only have job.upserted (no song.state-changed yet).
            if (payload.LifecycleState == ServerJobLifecycleState.Running)
            {
                _downloadService.UpdateTrackFromEvent(jobId, new SongStateChangedEventDto(
                    JobId: payload.JobId,
                    DisplayId: payload.DisplayId,
                    WorkflowId: payload.WorkflowId,
                    Query: query,
                    LifecycleState: payload.LifecycleState,
                    ActivityPhase: payload.ActivityPhase,
                    ActivityUntilUtc: payload.ActivityUntilUtc,
                    TerminalOutcome: payload.TerminalOutcome,
                    SkipReason: payload.SkipReason,
                    FailureReason: payload.FailureReason,
                    DownloadPath: null,
                    ChosenCandidate: null,
                    DiscoveryRawResultCount: payload.DiscoveryRawResultCount,
                    DiscoveryLockedFileCount: payload.DiscoveryLockedFileCount,
                    FailureMessage: payload.FailureMessage,
                    CancellationSource: payload.CancellationSource));
            }
            return;
        }

        var fakeEvent = new SongStateChangedEventDto(
            JobId: payload.JobId,
            DisplayId: payload.DisplayId,
            WorkflowId: payload.WorkflowId,
            Query: query,
            LifecycleState: payload.LifecycleState,
            ActivityPhase: payload.ActivityPhase,
            ActivityUntilUtc: payload.ActivityUntilUtc,
            TerminalOutcome: payload.TerminalOutcome,
            SkipReason: payload.SkipReason,
            FailureReason: payload.FailureReason,
            DownloadPath: null,
            ChosenCandidate: null,
            DiscoveryRawResultCount: payload.DiscoveryRawResultCount,
            DiscoveryLockedFileCount: payload.DiscoveryLockedFileCount,
            FailureMessage: payload.FailureMessage,
            CancellationSource: payload.CancellationSource);

        _downloadService.UpdateTrackFromEvent(jobId, fakeEvent, createIfMissing: true);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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

    private static string TrackLabel(SongQueryDto q)
    {
        if (!string.IsNullOrEmpty(q.Artist) && !string.IsNullOrEmpty(q.Title))
            return $"{q.Artist} – {q.Title}";
        return q.Title ?? q.Artist ?? "(unknown)";
    }

    // ── Reconnect policy ───────────────────────────────────────────────────

    private sealed class ExponentialBackoff : IRetryPolicy
    {
        private static readonly TimeSpan[] _delays =
        [
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var idx = (int)Math.Min(retryContext.PreviousRetryCount, _delays.Length - 1);
            return _delays[idx];
        }
    }
}
