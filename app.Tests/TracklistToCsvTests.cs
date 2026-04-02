using SldlWeb.Models;
using SldlWeb.Services;

namespace SldlWeb.Tests;

public class TracklistToCsvTests
{
    /// <summary>
    /// Integration test: tracklist input detection + CSV conversion roundtrip.
    /// Simulates what DownloadService.ConvertTracklistToCsv does.
    /// </summary>
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

    [Fact]
    public void ConvertTracklist_BasicTracklist_ProducesValidCsv()
    {
        var input = """
            Ten City - Be Free (Emmaculate & Shannon Chambers Mix)
            New Musik – The Planet Doesn't Mind
            Marvin Gaye - What's Going On
            """;

        var csv = ConvertTracklistToCsv(input);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Artist,Title", lines[0]);
        Assert.Equal(4, lines.Length); // header + 3 tracks

        // Verify each row parses back correctly
        var row1 = CsvHelper.ParseLine(lines[1]);
        Assert.Equal("Ten City", row1[0]);
        Assert.Equal("Be Free (Emmaculate & Shannon Chambers Mix)", row1[1]);

        var row2 = CsvHelper.ParseLine(lines[2]);
        Assert.Equal("New Musik", row2[0]);
        Assert.Equal("The Planet Doesn't Mind", row2[1]);

        var row3 = CsvHelper.ParseLine(lines[3]);
        Assert.Equal("Marvin Gaye", row3[0]);
        Assert.Equal("What's Going On", row3[1]);
    }

    [Fact]
    public void ConvertTracklist_NumberedLines_ProducesValidCsv()
    {
        var input = """
            1. Ten City - Be Free
            2. New Musik – The Planet Doesn't Mind
            3. Marvin Gaye - What's Going On
            """;

        var csv = ConvertTracklistToCsv(input);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var row1 = CsvHelper.ParseLine(lines[1]);
        Assert.Equal("Ten City", row1[0]);
        Assert.Equal("Be Free", row1[1]);
    }

    [Fact]
    public void ConvertTracklist_TimestampedLines_ProducesValidCsv()
    {
        var input = """
            [0:00] Ten City - Be Free
            [3:45] New Musik – The Planet Doesn't Mind
            """;

        var csv = ConvertTracklistToCsv(input);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var row1 = CsvHelper.ParseLine(lines[1]);
        Assert.Equal("Ten City", row1[0]);
        Assert.Equal("Be Free", row1[1]);
    }

    [Fact]
    public void ConvertTracklist_SingleTrack_ProducesValidCsv()
    {
        var input = "Ten City - Be Free (Emmaculate & Shannon Chambers Mix)";

        var csv = ConvertTracklistToCsv(input);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length); // header + 1 track
        var row = CsvHelper.ParseLine(lines[1]);
        Assert.Equal("Ten City", row[0]);
        Assert.Equal("Be Free (Emmaculate & Shannon Chambers Mix)", row[1]);
    }

    [Fact]
    public void ConvertTracklist_CommaInArtistName_ProducesValidCsv()
    {
        var input = """
            Earth, Wind & Fire - September
            """;

        // "Earth, Wind & Fire" has a comma separator, but ` - ` should match first
        var csv = ConvertTracklistToCsv(input);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var row = CsvHelper.ParseLine(lines[1]);
        Assert.Equal("Earth, Wind & Fire", row[0]);
        Assert.Equal("September", row[1]);
    }

    [Fact]
    public void DetectAndConvert_EndToEnd_TracklistIsProcessedCorrectly()
    {
        var input = """
            Ten City - Be Free (Emmaculate & Shannon Chambers Mix)
            New Musik – The Planet Doesn't Mind
            Marvin Gaye - What's Going On
            """;

        // Detect should classify as Tracklist
        var inputType = InputTypeDetector.Detect(input);
        Assert.Equal(InputType.Tracklist, inputType);

        // Convert should produce valid CSV
        var csv = ConvertTracklistToCsv(input);
        Assert.StartsWith("Artist,Title", csv);

        // All tracks should be parseable
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < lines.Length; i++)
        {
            var fields = CsvHelper.ParseLine(lines[i]);
            Assert.Equal(2, fields.Count);
            Assert.False(string.IsNullOrWhiteSpace(fields[0]), $"Artist empty on line {i}");
            Assert.False(string.IsNullOrWhiteSpace(fields[1]), $"Title empty on line {i}");
        }
    }

    [Fact]
    public void DetectAndConvert_SingleTrack_ClassifiesAndConverts()
    {
        var input = "Ten City - Be Free (Emmaculate & Shannon Chambers Mix)";

        var inputType = InputTypeDetector.Detect(input);
        Assert.Equal(InputType.Tracklist, inputType);
    }

    [Fact]
    public void ConvertTracklist_SlashSeparated_ProducesValidCsv()
    {
        var input = """
            A Man Called Adam / Barefoot In The Head
            Bocca Juniors / Raise
            The Grid / Floatation
            """;

        var csv = ConvertTracklistToCsv(input);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Artist,Title", lines[0]);
        Assert.Equal(4, lines.Length); // header + 3 tracks

        var row1 = CsvHelper.ParseLine(lines[1]);
        Assert.Equal("A Man Called Adam", row1[0]);
        Assert.Equal("Barefoot In The Head", row1[1]);

        var row2 = CsvHelper.ParseLine(lines[2]);
        Assert.Equal("Bocca Juniors", row2[0]);
        Assert.Equal("Raise", row2[1]);
    }
}
