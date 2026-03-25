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
}
