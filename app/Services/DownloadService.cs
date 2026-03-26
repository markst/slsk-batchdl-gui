using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SldlWeb.Hubs;
using SldlWeb.Models;

namespace SldlWeb.Services;

public class DownloadService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, DownloaderApplication> _runningApps = new();
    private readonly SemaphoreSlim _jobSemaphore = new(1);
    private readonly IHubContext<DownloadHub> _hub;
    private readonly SettingsService _settings;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(IHubContext<DownloadHub> hub, SettingsService settings, ILogger<DownloadService> logger,
        JobRestorer jobRestorer)
    {
        _hub = hub;
        _settings = settings;
        _logger = logger;

        foreach (var job in jobRestorer.RestoreAll())
            _jobs.TryAdd(job.Id, job);
    }

    public DownloadJob CreateJob(string input, List<ExtraArg>? extraArgs = null)
    {
        var s = _settings.Get();
        // Merge defaults with per-job args; per-job args take precedence (last wins for duplicates).
        var merged = (s.DefaultExtraArgs ?? new()).Concat(extraArgs ?? new())
            .GroupBy(a => a.Flag)
            .Select(g => g.Last())
            .ToList();
        var job = new DownloadJob
        {
            Input = input.Trim(),
            InputType = InputTypeDetector.Detect(input),
            DownloadPath = GetDownloadPath(),
            ExtraArgs = merged,
        };
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
            if (_runningApps.TryGetValue(id, out var app))
                app.Cancel();
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

        // Reset track state
        track.State = "Initial";
        track.Progress = 0;
        track.BytesTransferred = 0;
        track.TotalBytes = 0;
        track.FailureReason = null;

        _ = _hub.Clients.All.SendAsync("TrackStateChanged", job.Id, track.Artist, track.Title, "Initial", (string?)null, (string?)null);

        // Write a single-track CSV
        var tempCsv = Path.Combine(Path.GetTempPath(), $"retry_{jobId}_{trackIndex}_{Guid.NewGuid():N}.csv");
        File.WriteAllText(tempCsv, $"Artist,Title\n{CsvHelper.Escape(track.Artist)},{CsvHelper.Escape(track.Title)}");

        _ = Task.Run(() => ProcessRetryAsync(job, track, tempCsv));
        return true;
    }

    private async Task ProcessRetryAsync(DownloadJob job, TrackInfo track, string csvPath)
    {
        try
        {
            await _jobSemaphore.WaitAsync();

            var reporter = new SignalRProgressReporter(_hub, job, skipTrackList: true);

            var args = new List<string> { csvPath, "--path", job.DownloadPath };

            var s = _settings.Get();
            if (!string.IsNullOrEmpty(s.SoulseekUsername)) { args.Add("--user"); args.Add(s.SoulseekUsername); }
            if (!string.IsNullOrEmpty(s.SoulseekPassword)) { args.Add("--pass"); args.Add(s.SoulseekPassword); }
            args.Add("--pref-format"); args.Add(s.PreferredFormat);
            args.Add("--pref-min-bitrate"); args.Add(s.MinBitrate);
            args.Add("--no-listen");
            args.Add("--write-index");

            var config = new Config(args.ToArray());
            config.noProgress = true;
            config.connectTimeout = 30000;

            var app = new DownloaderApplication(config, progressReporter: reporter);
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry failed for job {Id} track {Artist} - {Title}", job.Id, track.Artist, track.Title);
            track.State = "Failed";
            track.FailureReason = "Other";
            _ = _hub.Clients.All.SendAsync("TrackStateChanged", job.Id, track.Artist, track.Title, "Failed", "Other", (string?)null);
        }
        finally
        {
            try { File.Delete(csvPath); } catch { }
            _jobSemaphore.Release();

            // Recalculate overall progress
            var downloaded = job.Tracks.Count(t => t.State is "Downloaded" or "AlreadyExists");
            var failed = job.Tracks.Count(t => t.State == "Failed");
            _ = _hub.Clients.All.SendAsync("OverallProgress", job.Id, downloaded, failed, job.Tracks.Count);
        }
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

        // Reset failed/initial tracks so they show progress again
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
            .Where(f => !f.EndsWith("_index.csv") && !f.EndsWith("_input.csv") && !f.EndsWith(".incomplete"))
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
            // Cancelled while queued — semaphore was never acquired
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            await _hub.Clients.All.SendAsync("JobUpdated", job.Id, job.Status.ToString());

            // Reporter both updates job model and pushes events via SignalR
            var reporter = new SignalRProgressReporter(_hub, job);

            // Build args the same way sldl CLI expects them
            var args = BuildArgs(job);
            _logger.LogInformation("Starting sldl in-process for job {Id} with args: {Args}", job.Id, string.Join(" ", args));

            var config = new Config(args.ToArray());
            config.noProgress = true; // no console progress bars
            config.connectTimeout = 30000; // 30s to handle slow Soulseek server

            var app = new DownloaderApplication(config, progressReporter: reporter);
            _runningApps.TryAdd(job.Id, app);
            try
            {
                // Run sldl in-process. Use a completion source so we can
                // bail out immediately when the job is cancelled — sldl's
                // internal loops don't check our cancellation token.
                var runTask = app.RunAsync();
                var tcs = new TaskCompletionSource();
                using var reg = job.Cts.Token.Register(() => tcs.TrySetResult());

                var completed = await Task.WhenAny(runTask, tcs.Task);
                if (completed == tcs.Task)
                {
                    // Cancellation won — tell sldl to stop too, then give
                    // it a moment to wind down before we move on.
                    app.Cancel();
                    _ = runTask.ContinueWith(_ => { }, TaskScheduler.Default);
                    throw new OperationCanceledException();
                }

                // If RunAsync faulted, observe the exception
                await runTask;
            }
            finally
            {
                _runningApps.TryRemove(job.Id, out _);
            }

            // Determine final status from tracks
            if (job.Cts.IsCancellationRequested)
            {
                job.Status = JobStatus.Cancelled;
            }
            else
            {
                job.Status = job.FailedTracks > 0 && job.DownloadedTracks == 0
                    ? JobStatus.Failed
                    : JobStatus.Completed;
            }
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

    private List<string> BuildArgs(DownloadJob job)
    {
        var args = new List<string>();
        var input = job.Input;

        // CSV or tracklist input: write to temp CSV file
        if (job.InputType == "CSV" || job.InputType == "Tracklist")
        {
            var csvPath = Path.Combine(job.DownloadPath, "_input.csv");
            var csvContent = job.InputType == "Tracklist"
                ? ConvertTracklistToCsv(input)
                : input;
            File.WriteAllText(csvPath, csvContent);
            input = csvPath;
        }

        args.Add(input);
        args.Add("--path"); args.Add(job.DownloadPath);

        var s = _settings.Get();
        if (!string.IsNullOrEmpty(s.SoulseekUsername)) { args.Add("--user"); args.Add(s.SoulseekUsername); }
        if (!string.IsNullOrEmpty(s.SoulseekPassword)) { args.Add("--pass"); args.Add(s.SoulseekPassword); }
        if (!string.IsNullOrEmpty(s.SpotifyClientId)) { args.Add("--spotify-id"); args.Add(s.SpotifyClientId); }
        if (!string.IsNullOrEmpty(s.SpotifyClientSecret)) { args.Add("--spotify-secret"); args.Add(s.SpotifyClientSecret); }
        args.Add("--pref-format"); args.Add(s.PreferredFormat);
        args.Add("--pref-min-bitrate"); args.Add(s.MinBitrate);
        args.Add("--no-listen");
        args.Add("--write-index");
        args.Add("--nc");

        foreach (var extra in job.ExtraArgs)
        {
            if (string.IsNullOrWhiteSpace(extra.Flag)) continue;
            args.Add(extra.Flag);
            if (!string.IsNullOrWhiteSpace(extra.Value))
                args.Add(extra.Value);
        }

        return args;
    }

    private static string ConvertTracklistToCsv(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Artist,Title");
        foreach (var line in lines)
        {
            var parts = InputTypeDetector.SplitTrack(line);
            var artist = parts.Length == 2 ? CsvHelper.Escape(parts[0]) : "";
            var title = parts.Length == 2 ? CsvHelper.Escape(parts[1]) : CsvHelper.Escape(line);
            csv.AppendLine($"{artist},{title}");
        }
        return csv.ToString();
    }

    private string GetDownloadPath()
    {
        var s = _settings.Get();
        var basePath = string.IsNullOrEmpty(s.DownloadPath) ? Path.Combine(Directory.GetCurrentDirectory(), "downloads") : s.DownloadPath;
        var jobPath = Path.Combine(basePath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(jobPath);
        return jobPath;
    }
}
