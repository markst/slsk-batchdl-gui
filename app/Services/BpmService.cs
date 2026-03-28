using NAudio.Wave;
using NLayer.NAudioSupport;
using SoundTouch;

namespace SldlWeb.Services;

/// <summary>
/// Analyses an audio file and returns an estimated BPM value using the SoundTouch BpmDetect engine.
/// Supports MP3 (via NLayer), WAV, and AIFF formats.
/// </summary>
public class BpmService
{
    private readonly ILogger<BpmService> _logger;

    private const int ChunkFrames = 4096; // Frames per InputSamples call

    public BpmService(ILogger<BpmService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyses the given audio file and returns the estimated BPM, or null if analysis failed.
    /// </summary>
    public Task<float?> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.Run(() => Analyze(filePath, cancellationToken), cancellationToken);

    private float? Analyze(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = OpenReader(filePath);
            if (reader is null)
            {
                _logger.LogWarning("BPM analysis: unsupported format for {File}", filePath);
                return null;
            }

            ISampleProvider provider = reader.ToSampleProvider();
            int channels = provider.WaveFormat.Channels;
            int sampleRate = provider.WaveFormat.SampleRate;

            var detector = new BpmDetect(channels, sampleRate);

            var buffer = new float[ChunkFrames * channels];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // read is always a multiple of channels for interleaved NAudio providers,
                // but floor the frame count to be safe with any partial trailing samples.
                int frames = read / channels;
                if (frames > 0)
                    detector.InputSamples(buffer.AsSpan(0, frames * channels), frames);
            }

            float bpm = detector.GetBpm();
            return bpm > 0 ? bpm : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BPM analysis failed for {File}", filePath);
            return null;
        }
    }

    private static WaveStream? OpenReader(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".mp3" => new Mp3FileReaderBase(filePath, wf => new Mp3FrameWrapper(wf)),
                ".wav" => new WaveFileReader(filePath),
                ".aiff" or ".aif" => new AiffFileReader(filePath),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
