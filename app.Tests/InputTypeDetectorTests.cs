using SldlWeb.Models;
using SldlWeb.Services;

namespace SldlWeb.Tests;

public class InputTypeDetectorTests
{
    // --- Detect ---

    [Theory]
    [InlineData("https://open.spotify.com/playlist/abc123")]
    [InlineData("https://spotify.com/track/xyz")]
    public void Detect_SpotifyUrls_ReturnsSpotify(string input) =>
        Assert.Equal(InputType.Spotify, InputTypeDetector.Detect(input));

    [Theory]
    [InlineData("https://youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc123")]
    public void Detect_YouTubeUrls_ReturnsYouTube(string input) =>
        Assert.Equal(InputType.YouTube, InputTypeDetector.Detect(input));

    [Fact]
    public void Detect_BandcampUrl_ReturnsBandcamp() =>
        Assert.Equal(InputType.Bandcamp, InputTypeDetector.Detect("https://artist.bandcamp.com/album/test"));

    [Fact]
    public void Detect_CsvWithHeader_ReturnsCsv()
    {
        var csv = "Artist,Title\nMarvin Gaye,What's Going On";
        Assert.Equal(InputType.CSV, InputTypeDetector.Detect(csv));
    }

    [Fact]
    public void Detect_CsvHeaderCaseInsensitive_ReturnsCsv()
    {
        var csv = "artist,title\nMarvin Gaye,What's Going On";
        Assert.Equal(InputType.CSV, InputTypeDetector.Detect(csv));
    }

    [Fact]
    public void Detect_SingleTrackWithHyphen_ReturnsTracklist()
    {
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect("Ten City - Be Free (Emmaculate & Shannon Chambers Mix)"));
    }

    [Fact]
    public void Detect_SingleTrackWithEnDash_ReturnsTracklist()
    {
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect("New Musik – The Planet Doesn't Mind"));
    }

    [Fact]
    public void Detect_SingleTrackWithEmDash_ReturnsTracklist()
    {
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect("Artist — Title"));
    }

    [Fact]
    public void Detect_MultiLineTracklist_ReturnsTracklist()
    {
        var input = """
            Ten City - Be Free (Emmaculate & Shannon Chambers Mix)
            New Musik – The Planet Doesn't Mind
            Marvin Gaye - What's Going On
            """;
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect(input));
    }

    [Fact]
    public void Detect_MultiLineMixedSeparators_ReturnsTracklist()
    {
        var input = """
            Ten City - Be Free
            New Musik – The Planet Doesn't Mind
            Artist — Song Title
            Another − Track Name
            """;
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect(input));
    }

    [Fact]
    public void Detect_TrackContainingWordArtist_DoesNotReturnCsv()
    {
        // Track name contains "Artist" but isn't CSV
        Assert.NotEqual(InputType.CSV, InputTypeDetector.Detect("The Artist - My Song, feat. Someone"));
    }

    [Fact]
    public void Detect_TrackListContainingWordTitle_DoesNotReturnCsv()
    {
        var input = """
            Title Fight - Head In The Ceiling Fan
            New Musik – The Planet Doesn't Mind
            """;
        Assert.NotEqual(InputType.CSV, InputTypeDetector.Detect(input));
    }

    [Fact]
    public void Detect_SingleWordSearch_ReturnsSearch()
    {
        Assert.Equal(InputType.Search, InputTypeDetector.Detect("Radiohead"));
    }

    [Fact]
    public void Detect_MultiWordSearch_ReturnsSearch()
    {
        Assert.Equal(InputType.Search, InputTypeDetector.Detect("Radiohead OK Computer"));
    }

    [Fact]
    public void Detect_NumberedTracklist_ReturnsTracklist()
    {
        var input = """
            1. Ten City - Be Free
            2. New Musik – The Planet Doesn't Mind
            3. Marvin Gaye - What's Going On
            """;
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect(input));
    }

    [Fact]
    public void Detect_TimestampedTracklist_ReturnsTracklist()
    {
        var input = """
            [0:00] Ten City - Be Free
            [3:45] New Musik – The Planet Doesn't Mind
            [7:12] Marvin Gaye - What's Going On
            """;
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect(input));
    }

    // --- HasTrackSeparator ---

    [Theory]
    [InlineData("Artist - Title", true)]
    [InlineData("Artist – Title", true)]
    [InlineData("Artist — Title", true)]
    [InlineData("Artist − Title", true)]
    [InlineData("Last, First", true)]
    [InlineData("JustOneWord", false)]
    [InlineData("No separator here", false)]
    public void HasTrackSeparator_DetectsCorrectly(string line, bool expected) =>
        Assert.Equal(expected, InputTypeDetector.HasTrackSeparator(line));

    [Fact]
    public void HasTrackSeparator_HyphenWithoutSpaces_ReturnsFalse()
    {
        // "high-quality" shouldn't match
        Assert.False(InputTypeDetector.HasTrackSeparator("high-quality audio"));
    }

    // --- CleanTrackLine ---

    [Theory]
    [InlineData("1. Artist - Title", "Artist - Title")]
    [InlineData("1) Artist - Title", "Artist - Title")]
    [InlineData("1- Artist - Title", "Artist - Title")]
    [InlineData("  2. Artist - Title", "Artist - Title")]
    [InlineData("[3:45] Artist - Title", "Artist - Title")]
    [InlineData("12. [1:23:45] Artist - Title", "Artist - Title")]
    [InlineData("Artist - Title", "Artist - Title")]
    public void CleanTrackLine_RemovesPrefixes(string input, string expected) =>
        Assert.Equal(expected, InputTypeDetector.CleanTrackLine(input));

    // --- SplitTrack ---

    [Fact]
    public void SplitTrack_HyphenSeparator_SplitsCorrectly()
    {
        var result = InputTypeDetector.SplitTrack("Ten City - Be Free (Emmaculate & Shannon Chambers Mix)");
        Assert.Equal(2, result.Length);
        Assert.Equal("Ten City", result[0]);
        Assert.Equal("Be Free (Emmaculate & Shannon Chambers Mix)", result[1]);
    }

    [Fact]
    public void SplitTrack_EnDashSeparator_SplitsCorrectly()
    {
        var result = InputTypeDetector.SplitTrack("New Musik – The Planet Doesn't Mind");
        Assert.Equal(2, result.Length);
        Assert.Equal("New Musik", result[0]);
        Assert.Equal("The Planet Doesn't Mind", result[1]);
    }

    [Fact]
    public void SplitTrack_EmDashSeparator_SplitsCorrectly()
    {
        var result = InputTypeDetector.SplitTrack("Artist — Song Title");
        Assert.Equal(2, result.Length);
        Assert.Equal("Artist", result[0]);
        Assert.Equal("Song Title", result[1]);
    }

    [Fact]
    public void SplitTrack_NumberPrefix_StrippedBeforeSplit()
    {
        var result = InputTypeDetector.SplitTrack("3. Marvin Gaye - What's Going On");
        Assert.Equal(2, result.Length);
        Assert.Equal("Marvin Gaye", result[0]);
        Assert.Equal("What's Going On", result[1]);
    }

    [Fact]
    public void SplitTrack_TimestampPrefix_StrippedBeforeSplit()
    {
        var result = InputTypeDetector.SplitTrack("[4:20] Artist - Title");
        Assert.Equal(2, result.Length);
        Assert.Equal("Artist", result[0]);
        Assert.Equal("Title", result[1]);
    }

    [Fact]
    public void SplitTrack_MultipleHyphensInTitle_OnlySplitsOnFirst()
    {
        var result = InputTypeDetector.SplitTrack("Artist - Title - Subtitle");
        Assert.Equal(2, result.Length);
        Assert.Equal("Artist", result[0]);
        Assert.Equal("Title - Subtitle", result[1]);
    }

    [Fact]
    public void SplitTrack_NoSeparator_ReturnsSingleElement()
    {
        var result = InputTypeDetector.SplitTrack("Just a title");
        Assert.Single(result);
        Assert.Equal("Just a title", result[0]);
    }

    [Fact]
    public void SplitTrack_CommaSeparatedFallback()
    {
        var result = InputTypeDetector.SplitTrack("Gaye, Marvin");
        Assert.Equal(2, result.Length);
        Assert.Equal("Gaye", result[0]);
        Assert.Equal("Marvin", result[1]);
    }

    // --- Slash separator ---

    [Fact]
    public void SplitTrack_SlashSeparator_SplitsCorrectly()
    {
        var result = InputTypeDetector.SplitTrack("A Man Called Adam / Barefoot In The Head");
        Assert.Equal(2, result.Length);
        Assert.Equal("A Man Called Adam", result[0]);
        Assert.Equal("Barefoot In The Head", result[1]);
    }

    [Fact]
    public void HasTrackSeparator_SlashSeparator_ReturnsTrue()
    {
        Assert.True(InputTypeDetector.HasTrackSeparator("Bocca Juniors / Raise"));
    }

    [Fact]
    public void Detect_SlashSeparatedTracklist_ReturnsTracklist()
    {
        var input = """
            A Man Called Adam / Barefoot In The Head
            Bocca Juniors / Raise
            The Grid / Floatation
            """;
        Assert.Equal(InputType.Tracklist, InputTypeDetector.Detect(input));
    }

    // --- ParseTracklist ---

    [Fact]
    public void ParseTracklist_BasicTracklist_ReturnsCorrectEntries()
    {
        var input = """
            Ten City - Be Free
            New Musik – The Planet Doesn't Mind
            Marvin Gaye - What's Going On
            """;

        var result = InputTypeDetector.ParseTracklist(input);

        Assert.Equal(3, result.Count);
        Assert.Equal("Ten City", result[0].Artist);
        Assert.Equal("Be Free", result[0].Title);
        Assert.Equal("New Musik", result[1].Artist);
        Assert.Equal("The Planet Doesn't Mind", result[1].Title);
        Assert.Equal("Marvin Gaye", result[2].Artist);
        Assert.Equal("What's Going On", result[2].Title);
    }

    [Fact]
    public void ParseTracklist_SlashSeparated_ReturnsCorrectEntries()
    {
        var input = """
            A Man Called Adam / Barefoot In The Head
            Bocca Juniors / Raise
            The Grid / Floatation
            """;

        var result = InputTypeDetector.ParseTracklist(input);

        Assert.Equal(3, result.Count);
        Assert.Equal("A Man Called Adam", result[0].Artist);
        Assert.Equal("Barefoot In The Head", result[0].Title);
        Assert.Equal("Bocca Juniors", result[1].Artist);
        Assert.Equal("Raise", result[1].Title);
    }

    [Fact]
    public void ParseTracklist_PreservesRawLines()
    {
        var input = "1. Ten City - Be Free\n2. Marvin Gaye - What's Going On";

        var result = InputTypeDetector.ParseTracklist(input);

        Assert.Equal(2, result.Count);
        Assert.Equal("1. Ten City - Be Free", result[0].RawLine);
        Assert.Equal("2. Marvin Gaye - What's Going On", result[1].RawLine);
    }

    [Fact]
    public void ParseTracklist_NoSeparator_TitleOnlyWithEmptyArtist()
    {
        var input = "Just a title";
        var result = InputTypeDetector.ParseTracklist(input);

        Assert.Single(result);
        Assert.Equal("", result[0].Artist);
        Assert.Equal("Just a title", result[0].Title);
    }
}
