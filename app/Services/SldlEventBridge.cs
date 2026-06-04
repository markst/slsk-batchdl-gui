using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sldl.Api;
using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// Background service that subscribes to the sldl daemon's SignalR event hub and
/// dispatches state-change events to DownloadService so the UI stays in sync.
///
/// The hub lives at {daemonUrl}/api/events and broadcasts on the "serverEvent" method.
/// Reconnection uses exponential back-off capped at 30 s.
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
                    SldlApiJson.ConfigureSerializerOptions(options.PayloadSerializerOptions);
                })
                .WithAutomaticReconnect(new ExponentialBackoff())
                .Build();

            connection.On<ServerEventEnvelopeDto>("serverEvent",
                envelope => HandleEvent(ServerEventPayloadConverter.RehydrateEnvelope(envelope)));

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

        _downloadService.EnsureTrack(jobId, payload.Query);
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
        switch (payload.State)
        {
            case ServerJobState.Downloading:
                var user = payload.ChosenCandidate?.Username ?? "?";
                var file = payload.ChosenCandidate is { } c ? System.IO.Path.GetFileName(c.Filename) : null;
                _logger.LogInformation("[{JobId}] Downloading {Track}  ←  {User}/{File}",
                    jobId, track, user, file ?? "");
                break;
            case ServerJobState.Done:
                _logger.LogInformation("[{JobId}] Done       {Track}  →  {Path}",
                    jobId, track, payload.DownloadPath ?? "");
                break;
            case ServerJobState.AlreadyExists:
                _logger.LogInformation("[{JobId}] Exists     {Track}", jobId, track);
                break;
            case ServerJobState.Failed:
                _logger.LogWarning("[{JobId}] Failed     {Track}  ({Reason})",
                    jobId, track, payload.FailureReason?.ToString() ?? "unknown");
                break;
            case ServerJobState.Skipped:
                _logger.LogInformation("[{JobId}] Skipped    {Track}", jobId, track);
                break;
        }

        _downloadService.UpdateTrackFromEvent(jobId, payload);
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
            _logger.LogWarning("[{JobId}] Job failed", jobId);

        _downloadService.HandleWorkflowUpserted(jobId, payload.State);
    }

    private void HandleJobUpserted(ServerEventEnvelopeDto envelope)
    {
        if (!envelope.SnapshotInvalidation) return;

        if (envelope.Payload is not JobSummaryDto payload) return;

        // If this is a song job in a terminal state and we don't have a state-changed event yet,
        // treat it as a state update so nothing gets stuck.
        if (payload.Kind is ServerJobKind.Song && DaemonStateMapper.IsTerminal(payload.State))
        {
            var jobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
            if (jobId is null) return;

            var fakeEvent = new SongStateChangedEventDto(
                JobId: payload.JobId,
                DisplayId: payload.DisplayId,
                WorkflowId: payload.WorkflowId,
                Query: new SongQueryDto(Title: payload.ItemName),
                State: payload.State,
                FailureReason: payload.FailureReason,
                DownloadPath: null,
                ChosenCandidate: null,
                DiscoveryResultCount: payload.DiscoveryResultCount,
                DiscoveryLockedFileCount: payload.DiscoveryLockedFileCount,
                FailureMessage: payload.FailureMessage);

            _downloadService.UpdateTrackFromEvent(jobId, fakeEvent, createIfMissing: false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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
