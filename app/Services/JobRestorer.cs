using SldlWeb.Models;

namespace SldlWeb.Services;

/// <summary>
/// Best-effort rebuild of completed UI jobs from files under the download root.
/// Discovers job folders that contain <c>tracks.csv</c>, <c>input.txt</c>, and/or any
/// nested <c>_index.csv</c> (sockseek writes the index beside playlist/list output,
/// e.g. <c>{jobId}/input/_index.csv</c>, not always at the job root).
/// </summary>
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
        var basePath = string.IsNullOrEmpty(s.DownloadPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "downloads")
            : s.DownloadPath;
        if (!Directory.Exists(basePath)) return jobs;

        foreach (var dir in Directory.GetDirectories(basePath).OrderBy(d => d))
        {
            if (!LooksLikeJobDirectory(dir))
                continue;

            try
            {
                var job = RestoreFromDirectory(dir);
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

    /// <summary>
    /// Job folders may only have nested indexes (no root <c>_index.csv</c> /
    /// <c>tracks.csv</c>), which previously caused restore to skip them entirely.
    /// </summary>
    private static bool LooksLikeJobDirectory(string dir)
    {
        if (File.Exists(Path.Combine(dir, "tracks.csv"))) return true;
        if (File.Exists(Path.Combine(dir, "input.txt"))) return true;
        if (File.Exists(Path.Combine(dir, "input.csv"))) return true;
        if (File.Exists(Path.Combine(dir, "_index.csv"))) return true;
        return Directory.EnumerateFiles(dir, "_index.csv", SearchOption.AllDirectories).Any();
    }

    private DownloadJob? RestoreFromDirectory(string dir)
    {
        var dirName = Path.GetFileName(dir);
        var id = dirName;
        var createdAt = Directory.GetCreationTimeUtc(dir);

        var indexResults = new List<IndexEntry>();
        foreach (var indexFile in Directory.GetFiles(dir, "_index.csv", SearchOption.AllDirectories))
        {
            var indexDir = Path.GetDirectoryName(indexFile)!;
            indexResults.AddRange(ParseIndexEntries(indexFile, indexDir, _logger));
        }

        var tracksCsvPath = Path.Combine(dir, "tracks.csv");
        var inputTxtPath = Path.Combine(dir, "input.txt");
        var inputCsvPath = Path.Combine(dir, "input.csv");

        List<TrackInfo> tracks;
        string input;
        InputType inputType;

        if (File.Exists(tracksCsvPath))
        {
            input = File.ReadAllText(tracksCsvPath).Trim();
            var requestedTracks = CsvHelper.ParseInputCsv(tracksCsvPath);
            tracks = CrossReference(requestedTracks, indexResults);
            inputType = InputTypeDetector.Detect(input);
        }
        else if (File.Exists(inputTxtPath) || File.Exists(inputCsvPath))
        {
            var path = File.Exists(inputTxtPath) ? inputTxtPath : inputCsvPath;
            input = File.ReadAllText(path).Trim();
            inputType = File.Exists(inputCsvPath) && !File.Exists(inputTxtPath)
                ? InputType.CSV
                : InputType.Tracklist;

            // Prefer index rows when present; otherwise show requested lines as Initial.
            if (indexResults.Count > 0)
            {
                tracks = indexResults.Select(e => e.ToTrackInfo()).ToList();
            }
            else if (inputType == InputType.Tracklist)
            {
                tracks = InputTypeDetector.ParseTracklist(input)
                    .Select(t => new TrackInfo { Artist = t.Artist, Title = t.Title, State = "Initial" })
                    .ToList();
            }
            else
            {
                tracks = [];
            }
        }
        else
        {
            input = dirName;
            tracks = indexResults.Select(e => e.ToTrackInfo()).ToList();
            inputType = InputTypeDetector.Detect(input);
        }

        if (tracks.Count == 0 && indexResults.Count == 0 && string.IsNullOrWhiteSpace(input))
            return null;

        var downloaded = tracks.Count(t => t.State is "Done" or "AlreadyExists");
        var failed = tracks.Count(t => t.State == "Failed");
        var status = tracks.Count == 0 ? JobStatus.Completed
            : failed > 0 && downloaded == 0 ? JobStatus.Failed
            : JobStatus.Completed;

        var job = new DownloadJob
        {
            Id = id,
            Input = input,
            InputType = inputType,
            Status = status,
            CreatedAt = createdAt,
            CompletedAt = createdAt,
            DownloadPath = dir,
            Tracks = tracks,
        };

        _logger.LogInformation(
            "Restored job {Id} from {Dir} with {Count} tracks ({Dl} downloaded, {Fl} failed)",
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
        [1] = "Done",
        [2] = "Failed",
        [3] = "AlreadyExists",
        [4] = "Failed",
    };

    private static readonly Dictionary<int, string> _failureMap = new()
    {
        [1] = "InvalidSearchString",
        [2] = "OutOfDownloadRetries",
        [3] = "NoMatchingResults",
        [4] = "AllDownloadsFailed",
        [5] = "Other",
    };

    private static List<IndexEntry> ParseIndexEntries(string indexPath, string baseDir, ILogger logger)
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

            var state = _stateMap.TryGetValue(stateInt, out var mapped) ? mapped : "Initial";
            if (stateInt != 0 && !_stateMap.ContainsKey(stateInt))
                logger.LogWarning("Unknown state index {StateInt} in {IndexPath}; defaulting to Initial", stateInt, indexPath);
            var failureReason = _failureMap.GetValueOrDefault(failureInt);

            string? downloadPath = null;
            string? extension = null;
            if (!string.IsNullOrEmpty(filepath))
            {
                var absPath = Path.GetFullPath(Path.Combine(baseDir, filepath));
                if (File.Exists(absPath))
                {
                    downloadPath = absPath;
                    extension = Path.GetExtension(absPath).TrimStart('.').ToLowerInvariant();
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
            Progress = State is "Done" or "AlreadyExists" ? 100 : 0,
        };
    }
}
