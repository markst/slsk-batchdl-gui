using SldlWeb.Services;

namespace SldlWeb.Tests;

public class CsvHelperTests
{
    // --- Escape ---

    [Fact]
    public void Escape_PlainValue_ReturnsUnchanged()
    {
        Assert.Equal("hello", CsvHelper.Escape("hello"));
    }

    [Fact]
    public void Escape_ValueWithComma_WrapsInQuotes()
    {
        Assert.Equal("\"hello, world\"", CsvHelper.Escape("hello, world"));
    }

    [Fact]
    public void Escape_ValueWithQuotes_EscapesAndWraps()
    {
        Assert.Equal("\"say \"\"hello\"\"\"", CsvHelper.Escape("say \"hello\""));
    }

    [Fact]
    public void Escape_ValueWithNewline_WrapsInQuotes()
    {
        Assert.Equal("\"line1\nline2\"", CsvHelper.Escape("line1\nline2"));
    }

    [Fact]
    public void Escape_Apostrophe_NoWrapping()
    {
        Assert.Equal("What's Going On", CsvHelper.Escape("What's Going On"));
    }

    [Fact]
    public void Escape_ParenthesesAndAmpersand_NoWrapping()
    {
        Assert.Equal("Be Free (Emmaculate & Shannon Chambers Mix)",
            CsvHelper.Escape("Be Free (Emmaculate & Shannon Chambers Mix)"));
    }

    // --- ParseLine ---

    [Fact]
    public void ParseLine_SimpleFields()
    {
        var result = CsvHelper.ParseLine("Artist,Title");
        Assert.Equal(new[] { "Artist", "Title" }, result);
    }

    [Fact]
    public void ParseLine_QuotedFields()
    {
        var result = CsvHelper.ParseLine("\"Marvin Gaye\",\"What's Going On\"");
        Assert.Equal(new[] { "Marvin Gaye", "What's Going On" }, result);
    }

    [Fact]
    public void ParseLine_QuotedFieldWithComma()
    {
        var result = CsvHelper.ParseLine("\"Earth, Wind & Fire\",\"September\"");
        Assert.Equal(new[] { "Earth, Wind & Fire", "September" }, result);
    }

    [Fact]
    public void ParseLine_EscapedQuoteInField()
    {
        var result = CsvHelper.ParseLine("\"say \"\"hello\"\"\",world");
        Assert.Equal(new[] { "say \"hello\"", "world" }, result);
    }

    [Fact]
    public void ParseLine_EmptyFields()
    {
        var result = CsvHelper.ParseLine(",title,");
        Assert.Equal(new[] { "", "title", "" }, result);
    }

    // --- Roundtrip: Escape then ParseLine ---

    [Theory]
    [InlineData("Ten City", "Be Free (Emmaculate & Shannon Chambers Mix)")]
    [InlineData("New Musik", "The Planet Doesn't Mind")]
    [InlineData("Earth, Wind & Fire", "September")]
    [InlineData("Marvin Gaye", "What's Going On")]
    public void EscapeThenParse_RoundTrip(string artist, string title)
    {
        var line = $"{CsvHelper.Escape(artist)},{CsvHelper.Escape(title)}";
        var parsed = CsvHelper.ParseLine(line);
        Assert.Equal(artist, parsed[0]);
        Assert.Equal(title, parsed[1]);
    }

    // --- ParseCsvText ---

    [Fact]
    public void ParseCsvText_BasicCsv_ReturnsTracks()
    {
        var csv = "Artist,Title\nTen City,Be Free\nNew Musik,The Planet Doesn't Mind";
        var tracks = CsvHelper.ParseCsvText(csv);
        Assert.Equal(2, tracks.Count);
        Assert.Equal(("Ten City", "Be Free"), tracks[0]);
        Assert.Equal(("New Musik", "The Planet Doesn't Mind"), tracks[1]);
    }

    [Fact]
    public void ParseCsvText_TrackColumnAlias_ReturnsTracks()
    {
        var csv = "Artist,Track\nMarvin Gaye,What's Going On";
        var tracks = CsvHelper.ParseCsvText(csv);
        Assert.Single(tracks);
        Assert.Equal(("Marvin Gaye", "What's Going On"), tracks[0]);
    }

    [Fact]
    public void ParseCsvText_QuotedFieldsWithCommas_ReturnsTracks()
    {
        var csv = "Artist,Title\n\"Earth, Wind & Fire\",September";
        var tracks = CsvHelper.ParseCsvText(csv);
        Assert.Single(tracks);
        Assert.Equal(("Earth, Wind & Fire", "September"), tracks[0]);
    }

    [Fact]
    public void ParseCsvText_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(CsvHelper.ParseCsvText(""));
    }

    [Fact]
    public void ParseCsvText_HeaderOnly_ReturnsEmpty()
    {
        Assert.Empty(CsvHelper.ParseCsvText("Artist,Title"));
    }

    [Fact]
    public void ParseCsvText_SkipsBlankLines_ReturnsTracks()
    {
        var csv = "Artist,Title\nTen City,Be Free\n\nNew Musik,Sanctuary";
        var tracks = CsvHelper.ParseCsvText(csv);
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void ParseCsvText_FullyQuotedCsv_ReturnsTracks()
    {
        var csv = "\"artist\",\"title\",\"status\",\"date\",\"longitude\",\"latitude\"\n\"Nerva\",\"The Scorpion\",\"N/A\",\"2025-02-09T14:01:26.276Z\",\"0.0\",\"0.0\"\n\"Moog Conspiracy\",\"Kamuy\",\"N/A\",\"2025-02-09T11:07:04.891Z\",\"0.0\",\"0.0\"\n\"Sleep D\",\"Hydralite\",\"N/A\",\"2025-02-09T10:55:00.093Z\",\"0.0\",\"0.0\"\n\"Hechizeros Band\",\"El Sonidito\",\"N/A\",\"2025-02-08T06:45:12.714Z\",\"0.0\",\"0.0\"";
        var tracks = CsvHelper.ParseCsvText(csv);
        Assert.Equal(4, tracks.Count);
        Assert.Equal(("Nerva", "The Scorpion"), tracks[0]);
        Assert.Equal(("Moog Conspiracy", "Kamuy"), tracks[1]);
        Assert.Equal(("Sleep D", "Hydralite"), tracks[2]);
        Assert.Equal(("Hechizeros Band", "El Sonidito"), tracks[3]);
    }
}
