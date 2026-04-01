using System.Text.RegularExpressions;

namespace SldlWeb.Services;

public static class InputTypeDetector
{
    private static readonly string[] TrackSeparators = [" - ", " – ", " — ", " − "];

    private static readonly Regex LinePrefix = new(
        @"^[\s]*(?:\d+[.\-)\s]*)?(?:\[[\d:]+\]\s*)?",
        RegexOptions.Compiled);

    public static string Detect(string input)
    {
        if (input.Contains("spotify.com") || input.Contains("open.spotify")) return "Spotify";
        if (input.Contains("youtube.com") || input.Contains("youtu.be")) return "YouTube";
        if (input.Contains("bandcamp.com")) return "Bandcamp";

        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // CSV: first line must look like a header row with Artist/Title columns
        if (lines.Length >= 2)
        {
            var firstLine = lines[0];
            var headerFields = firstLine.Split(',');
            if (headerFields.Any(f => f.Trim().Equals("Artist", StringComparison.OrdinalIgnoreCase))
                && headerFields.Any(f => f.Trim().Equals("Title", StringComparison.OrdinalIgnoreCase)
                    || f.Trim().Equals("Track", StringComparison.OrdinalIgnoreCase)))
                return "CSV";
        }

        // Tracklist: any line with a track separator counts
        if (lines.Any(l => HasTrackSeparator(l))
            && (lines.Length == 1 || lines.Count(l => HasTrackSeparator(l)) > lines.Length / 2))
            return "Tracklist";

        return "Search";
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
}
