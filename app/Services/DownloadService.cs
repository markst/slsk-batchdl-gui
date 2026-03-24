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
    private readonly IConfiguration _config;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(IHubContext<DownloadHub> hub, IConfiguration config, ILogger<DownloadService> logger)
    {
        _hub = hub;
        _config = config;
        _logger = logger;
    }

    public IReadOnlyList<string> GetAvailableProfiles() => Config.GetAvailableProfiles();

    public DownloadJob CreateJob(string input, bool albumMode = false, string? profile = null)
    {
        var job = new DownloadJob
        {
            Input = input.Trim(),
            InputType = DetectInputType(input),
            DownloadPath = GetDownloadPath(),
            AlbumMode = albumMode,
            Profile = profile,
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

        if (!string.IsNullOrEmpty(input))
            args.Add(input);
        args.Add("--path"); args.Add(job.DownloadPath);

        var username = _config["Sldl:Username"];
        var password = _config["Sldl:Password"];
        if (!string.IsNullOrEmpty(username)) { args.Add("--user"); args.Add(username); }
        if (!string.IsNullOrEmpty(password)) { args.Add("--pass"); args.Add(password); }

        var spotifyId = _config["Spotify:ClientId"];
        var spotifySecret = _config["Spotify:ClientSecret"];
        if (!string.IsNullOrEmpty(spotifyId)) { args.Add("--spotify-id"); args.Add(spotifyId); }
        if (!string.IsNullOrEmpty(spotifySecret)) { args.Add("--spotify-secret"); args.Add(spotifySecret); }

        var format = _config["Sldl:PreferredFormat"] ?? "mp3";
        var minBitrate = _config["Sldl:MinBitrate"] ?? "200";
        args.Add("--pref-format"); args.Add(format);
        args.Add("--pref-min-bitrate"); args.Add(minBitrate);
        if (job.AlbumMode)
            args.Add("--album");

        var mockFilesDir = _config["Sldl:MockFilesDir"];
        if (!string.IsNullOrEmpty(mockFilesDir))
        {
            args.Add("--mock-files-dir"); args.Add(mockFilesDir);
            if (_config.GetValue<bool>("Sldl:MockFilesNoReadTags"))
                args.Add("--mock-files-no-read-tags");
        }

        if (!string.IsNullOrEmpty(job.Profile))
        { args.Add("--profile"); args.Add(job.Profile); }

        args.Add("--no-listen");
        args.Add("--write-index");

        return args;
    }

    private static string DetectInputType(string input)
    {
        if (input.Contains("spotify.com") || input.Contains("open.spotify")) return "Spotify";
        if (input.Contains("youtube.com") || input.Contains("youtu.be")) return "YouTube";
        if (input.Contains("bandcamp.com")) return "Bandcamp";
        if (input.Contains(',') && (input.Contains("Artist") || input.Contains("Title") || input.Split('\n').Length > 1))
            return "CSV";

        // Multi-line text with " - " separators → tracklist
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1 && lines.Count(l => l.Contains(" - ")) > lines.Length / 2)
            return "Tracklist";

        return "Search";
    }

    private static string ConvertTracklistToCsv(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Artist,Title");
        foreach (var line in lines)
        {
            var parts = line.Split(" - ", 2, StringSplitOptions.TrimEntries);
            var artist = parts.Length == 2 ? CsvEscape(parts[0]) : "";
            var title = parts.Length == 2 ? CsvEscape(parts[1]) : CsvEscape(line);
            csv.AppendLine($"{artist},{title}");
        }
        return csv.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\""; 
        return value;
    }

    private string GetDownloadPath()
    {
        var basePath = _config["Sldl:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        var jobPath = Path.Combine(basePath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(jobPath);
        return jobPath;
    }
}
