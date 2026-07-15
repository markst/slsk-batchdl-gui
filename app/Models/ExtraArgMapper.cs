using Sockseek.Api;

namespace SldlWeb.Models;

/// <summary>
/// Maps UI extra CLI-style flags into sockseek <see cref="DownloadSettingsPatchDto"/> operations.
/// </summary>
public static class ExtraArgMapper
{
    public static DownloadSettingsPatchDto? ToDownloadSettingsPatch(IEnumerable<ExtraArg>? extraArgs)
    {
        var ops = ToOperations(extraArgs).ToList();
        return ops.Count == 0 ? null : DownloadSettingsPatchDtoMapper.FromOperations(ops);
    }

    public static IEnumerable<DownloadSettingOperationDto> ToOperations(IEnumerable<ExtraArg>? extraArgs)
    {
        if (extraArgs is null) yield break;

        foreach (var arg in extraArgs)
        {
            var flag = (arg.Flag ?? "").Trim();
            if (flag.Length == 0) continue;

            var value = arg.Value?.Trim() ?? "";
            var op = Map(flag, value);
            if (op is not null)
                yield return op;
        }
    }

    private static DownloadSettingOperationDto? Map(string flag, string value) => flag switch
    {
        "--fast-search" => DownloadSettingsDeltaMapper.Set("Search.FastSearch", true),
        "--desperate" => DownloadSettingsDeltaMapper.Set("Search.DesperateSearch", true),
        "--yt-dlp" => DownloadSettingsDeltaMapper.Set("YtDlp.UseYtdlp", true),
        "--remove-ft" => DownloadSettingsDeltaMapper.Set("Preprocess.RemoveFt", true),
        "--reverse" => DownloadSettingsDeltaMapper.Set("Extraction.Reverse", true),
        "--write-playlist" => DownloadSettingsDeltaMapper.Set("Output.WritePlaylist", true),
        "--artist-maybe-wrong" => DownloadSettingsDeltaMapper.Set("Search.ArtistMaybeWrong", true),
        "--strict-title" => DownloadSettingsDeltaMapper.Set("Search.NecessaryCond.StrictTitle", true),
        "--strict-artist" => DownloadSettingsDeltaMapper.Set("Search.NecessaryCond.StrictArtist", true),
        "--album" => DownloadSettingsDeltaMapper.Set("Extraction.RequestedMode", Sockseek.Core.ExtractionMode.Album),
        "--number" when TryInt(value, out var maxTracks)
            => DownloadSettingsDeltaMapper.Set("Extraction.MaxTracks", maxTracks),
        "--offset" when TryInt(value, out var offset)
            => DownloadSettingsDeltaMapper.Set("Extraction.Offset", offset),
        "--search-timeout" when TryInt(value, out var searchTimeout)
            => DownloadSettingsDeltaMapper.Set("Search.SearchTimeout", searchTimeout),
        "--min-bitrate" when TryInt(value, out var minBitrate)
            => DownloadSettingsDeltaMapper.Set("Search.NecessaryCond.MinBitrate", minBitrate),
        "--max-bitrate" when TryInt(value, out var maxBitrate)
            => DownloadSettingsDeltaMapper.Set("Search.NecessaryCond.MaxBitrate", maxBitrate),
        "--format" when value.Length > 0
            => DownloadSettingsDeltaMapper.Replace(
                "Search.NecessaryCond.Formats",
                value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => f.TrimStart('.').ToLowerInvariant())
                    .ToArray()),
        "--name-format" when value.Length > 0
            => DownloadSettingsDeltaMapper.Set("Output.NameFormat", value),
        _ => null,
    };

    private static bool TryInt(string value, out int parsed)
        => int.TryParse(value, out parsed);
}
