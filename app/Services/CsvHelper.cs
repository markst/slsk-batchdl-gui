using System.Text;
using SldlWeb.Models;

namespace SldlWeb.Services;

public static class CsvHelper
{
    public static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    public static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    public static List<(string Artist, string Title)> ParseCsvText(string text)
    {
        var tracks = new List<(string, string)>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return tracks;

        var header = ParseLine(lines[0]);
        int artistCol = header.FindIndex(h => h.Equals("Artist", StringComparison.OrdinalIgnoreCase));
        int titleCol = header.FindIndex(h => h.Equals("Title", StringComparison.OrdinalIgnoreCase));
        if (titleCol < 0) titleCol = header.FindIndex(h => h.Equals("Track", StringComparison.OrdinalIgnoreCase));
        if (artistCol < 0 || titleCol < 0) return tracks;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = ParseLine(lines[i]);
            var artist = artistCol < fields.Count ? fields[artistCol].Trim() : "";
            var title = titleCol < fields.Count ? fields[titleCol].Trim() : "";
            if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                tracks.Add((artist, title));
        }
        return tracks;
    }

    public static List<(string Artist, string Title)> ParseInputCsv(string path)
    {
        return ParseCsvText(File.ReadAllText(path));
    }
}
