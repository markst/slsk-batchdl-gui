using SldlWeb.Models;

namespace SldlWeb.Services;

public class JobRestorer
{
    private readonly SettingsService _settings;
    private readonly ILogger<JobRestorer> _logger;

    public JobRestorer(SettingsService settings, ILogger<JobRestorer> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public List<DownloadJob> RestoreAll()
    {
        var jobs = new List<DownloadJob>();
        var s = _settings.Get();
        var basePath = string.IsNullOrEmpty(s.DownloadPath) ? Path.Combine(Directory.GetCurrentDirectory(), "downloads") : s.DownloadPath;
        if (!Directory.Exists(basePath)) return jobs;

        foreach (var dir in Directory.GetDirectories(basePath).OrderBy(d => d))
        {
            var inputPath = Path.Combine(dir, "_input.csv");
            var rootIndexPath = Path.Combine(dir, "_index.csv");
            if (!File.Exists(inputPath) && !File.Exists(rootIndexPath)) continue;

            try
            {
                var job = RestoreFromDirectory(dir, inputPath, rootIndexPath);
                if (job is not null)
                    jobs.Add(job);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore job from {Dir}", dir);
            }
        }

        return jobs;
    }

    private DownloadJob? RestoreFromDirectory(string dir, string inputPath, string rootIndexPath)
    {
        var dirName = Path.GetFileName(dir);
        var id = dirName;

        DateTime createdAt = Directory.GetCreationTimeUtc(dir);

        var indexResults = new List<IndexEntry>();
        foreach (var indexFile in Directory.GetFiles(dir, "_index.csv", SearchOption.AllDirectories))
        {
            var indexDir = Path.GetDirectoryName(indexFile)!;
            indexResults.AddRange(ParseIndexEntries(indexFile, indexDir));
        }

        List<TrackInfo> tracks;
        string input;

        if (File.Exists(inputPath))
        {
            input = File.ReadAllText(inputPath).Trim();
            var requestedTracks = CsvHelper.ParseInputCsv(inputPath);
            tracks = CrossReference(requestedTracks, indexResults);
        }
        else
        {
            input = dirName;
            tracks = indexResults.Select(e => e.ToTrackInfo()).ToList();
        }

        var downloaded = tracks.Count(t => t.State is "Downloaded" or "AlreadyExists");
        var failed = tracks.Count(t => t.State == "Failed");
        var status = tracks.Count == 0 ? JobStatus.Completed
            : failed > 0 && downloaded == 0 ? JobStatus.Failed
            : JobStatus.Completed;

        var job = new DownloadJob
        {
            Id = id,
            Input = input,
            InputType = InputTypeDetector.Detect(input),
            Status = status,
            CreatedAt = createdAt,
            CompletedAt = createdAt,
            DownloadPath = dir,
            Tracks = tracks,
        };

        _logger.LogInformation("Restored job {Id} from {Dir} with {Count} tracks ({Dl} downloaded, {Fl} failed)",
            id, dirName, tracks.Count, downloaded, failed);

        return job;
    }

    private static List<TrackInfo> CrossReference(
        List<(string Artist, string Title)> requested,
        List<IndexEntry> results)
    {
        var tracks = new List<TrackInfo>();

        foreach (var (artist, title) in requested)
        {
            var match = results.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r.Title) &&
                string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(r.Artist) ||
                 r.Artist.Contains(artist, StringComparison.OrdinalIgnoreCase) ||
                 artist.Contains(r.Artist, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                var info = match.ToTrackInfo();
                if (!string.IsNullOrEmpty(artist)) info.Artist = artist;
                if (!string.IsNullOrEmpty(title)) info.Title = title;
                tracks.Add(info);
                results.Remove(match);
            }
            else
            {
                tracks.Add(new TrackInfo
                {
                    Artist = artist,
                    Title = title,
                    State = "Initial",
                });
            }
        }

        return tracks;
    }

    private static readonly Dictionary<int, string> _stateMap = new()
    {
        [1] = "Downloaded",
        [2] = "Failed",
        [3] = "AlreadyExists",
        [4] = "Failed",
    };

    private static readonly Dictionary<int, string> _failureMap = new()
    {
        [1] = "InvalidSearchString",
        [2] = "OutOfDownloadRetries",
        [3] = "NoSuitableFileFound",
        [4] = "AllDownloadsFailed",
        [5] = "Other",
    };

    private static List<IndexEntry> ParseIndexEntries(string indexPath, string baseDir)
    {
        var entries = new List<IndexEntry>();
        var lines = File.ReadAllLines(indexPath);
        if (lines.Length < 2) return entries;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = CsvHelper.ParseLine(lines[i]);
            if (fields.Count < 8) continue;

            var filepath = fields[0];
            var artist = fields[1];
            var album = fields[2];
            var title = fields[3];
            _ = int.TryParse(fields[4], out var length);
            _ = int.TryParse(fields[6], out var stateInt);
            _ = int.TryParse(fields[7], out var failureInt);

            var state = _stateMap.GetValueOrDefault(stateInt, "Initial");
            var failureReason = _failureMap.GetValueOrDefault(failureInt);

            string? downloadPath = null;
            string? extension = null;
            if (!string.IsNullOrEmpty(filepath))
            {
                var absPath = Path.GetFullPath(Path.Combine(baseDir, filepath));
                if (File.Exists(absPath))
                {
                    downloadPath = absPath;
                    extension = Path.GetExtension(absPath).TrimStart('.').ToLower();
                }
            }

            entries.Add(new IndexEntry(filepath, artist, album, title, length,
                state, failureReason, downloadPath, extension));
        }
        return entries;
    }

    private record IndexEntry(
        string Filepath, string Artist, string Album, string Title,
        int Length, string State, string? FailureReason,
        string? DownloadPath, string? Extension)
    {
        public TrackInfo ToTrackInfo() => new()
        {
            Artist = Artist,
            Title = Title,
            Album = Album,
            Length = Length,
            State = State,
            FailureReason = FailureReason,
            DownloadPath = DownloadPath,
            Extension = Extension,
            Progress = State is "Downloaded" or "AlreadyExists" ? 100 : 0,
        };
    }
}
