using System.Text.RegularExpressions;
using SldlWeb.Models;

namespace SldlWeb.Services;

public static class InputTypeDetector
{
    private static readonly string[] TrackSeparators = [" - ", " – ", " — ", " − ", " / "];

    private static readonly Regex LinePrefix = new(
        @"^[\s]*(?:\d+[.\-)\s]*)?(?:\[[\d:]+\]\s*)?",
        RegexOptions.Compiled);

    public static InputType Detect(string input)
    {
        if (input.Contains("spotify.com") || input.Contains("open.spotify")) return InputType.Spotify;
        if (input.Contains("youtube.com") || input.Contains("youtu.be")) return InputType.YouTube;
        if (input.Contains("bandcamp.com")) return InputType.Bandcamp;

        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // CSV: first line must look like a header row with Artist/Title columns
        if (lines.Length >= 2)
        {
            var headerFields = CsvHelper.ParseLine(lines[0]);
            if (headerFields.Any(f => f.Trim().Equals("Artist", StringComparison.OrdinalIgnoreCase))
                && headerFields.Any(f => f.Trim().Equals("Title", StringComparison.OrdinalIgnoreCase)
                    || f.Trim().Equals("Track", StringComparison.OrdinalIgnoreCase)))
                return InputType.CSV;
        }

        // Tracklist: any line with a track separator counts
        if (lines.Any(l => HasTrackSeparator(l))
            && (lines.Length == 1 || lines.Count(l => HasTrackSeparator(l)) > lines.Length / 2))
            return InputType.Tracklist;

        return InputType.Search;
    }

    public static string CleanTrackLine(string line)
    {
        return LinePrefix.Replace(line, "").Trim();
    }

    public static bool HasTrackSeparator(string line)
    {
        var clean = CleanTrackLine(line);
        return TrackSeparators.Any(s => clean.Contains(s)) || clean.Contains(", ");
    }

    public static string[] SplitTrack(string line)
    {
        var clean = CleanTrackLine(line);
        foreach (var sep in TrackSeparators)
        {
            if (clean.Contains(sep))
                return clean.Split(sep, 2, StringSplitOptions.TrimEntries);
        }
        if (clean.Contains(", "))
        {
            var idx = clean.IndexOf(", ", StringComparison.Ordinal);
            return [clean[..idx].Trim(), clean[(idx + 2)..].Trim()];
        }
        return [clean];
    }

    /// <summary>
    /// Parses a freeform tracklist into a list of (rawLine, artist, title) tuples.
    /// </summary>
    public static List<(string RawLine, string Artist, string Title)> ParseTracklist(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<(string RawLine, string Artist, string Title)>();
        foreach (var line in lines)
        {
            var parts = SplitTrack(line);
            var artist = parts.Length == 2 ? parts[0] : "";
            var title = parts.Length == 2 ? parts[1] : line.Trim();
            result.Add((line, artist, title));
        }
        return result;
    }
}
