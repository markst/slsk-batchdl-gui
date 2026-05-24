using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
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

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

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
                .WithAutomaticReconnect(new ExponentialBackoff())
                .Build();

            connection.On<SldlEventEnvelope>("serverEvent", envelope => HandleEvent(envelope));

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

            connection.Reconnected += id =>
            {
                _logger.LogInformation("Event hub reconnected (connection {Id})", id);
                return Task.CompletedTask;
            };

            try
            {
                await connection.StartAsync(stoppingToken);
                _logger.LogInformation("Connected to daemon event hub at {Url}", hubUrl);

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

    private void HandleEvent(SldlEventEnvelope envelope)
    {
        try
        {
            switch (envelope.Type)
            {
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

    private void HandleSongStateChanged(SldlEventEnvelope envelope)
    {
        var payload = envelope.Payload.Deserialize<SongStateChangedEventDto>(_json);
        if (payload is null) return;

        var workflowId = payload.WorkflowId;
        var jobId = _downloadService.GetJobIdForWorkflow(workflowId);
        if (jobId is null)
        {
            _logger.LogDebug("Ignoring song.state-changed for untracked workflow {WorkflowId}", workflowId);
            return;
        }

        _downloadService.UpdateTrackFromEvent(jobId, payload);
    }

    private void HandleDownloadProgress(SldlEventEnvelope envelope)
    {
        var payload = envelope.Payload.Deserialize<DownloadProgressEventDto>(_json);
        if (payload is null) return;

        var jobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
        if (jobId is null) return;

        _downloadService.UpdateTrackProgress(jobId, payload.JobId, payload.BytesTransferred, payload.TotalBytes);
    }

    private void HandleWorkflowUpserted(SldlEventEnvelope envelope)
    {
        var payload = envelope.Payload.Deserialize<WorkflowSummaryDto>(_json);
        if (payload is null) return;

        var jobId = _downloadService.GetJobIdForWorkflow(payload.WorkflowId);
        if (jobId is null) return;

        _downloadService.HandleWorkflowUpserted(jobId, payload.State);
    }

    private void HandleJobUpserted(SldlEventEnvelope envelope)
    {
        if (!envelope.SnapshotInvalidation) return;

        var payload = envelope.Payload.Deserialize<JobSummaryDto>(_json);
        if (payload is null) return;

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

            _downloadService.UpdateTrackFromEvent(jobId, fakeEvent);
        }
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
