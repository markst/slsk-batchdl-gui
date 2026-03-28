using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;

namespace SldlWeb.Services;

/// <summary>
/// Analyses an audio file and returns an estimated BPM value using energy-based beat detection.
/// Supports MP3 (via NLayer), WAV, and AIFF formats.
/// </summary>
public class BpmService
{
    private readonly ILogger<BpmService> _logger;

    // Beat detection parameters
    private const int AnalysisSampleRate = 8000;   // Downsample for efficiency
    private const int WindowMs = 50;                // Energy window size in ms
    private const double BeatSensitivity = 1.4;    // Energy spike threshold multiplier
    private const int HistorySize = 43;             // ~2 seconds of history windows
    private const double MinBpm = 60.0;
    private const double MaxBpm = 200.0;

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

            var samples = ReadMonoSamples(reader, cancellationToken);
            if (samples.Count < AnalysisSampleRate) // < 1 second
                return null;

            var bpm = EstimateBpm(samples, AnalysisSampleRate);
            return bpm is null ? null : (float)Math.Round(bpm.Value, 1);
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

    /// <summary>
    /// Reads the audio stream as mono float samples at <see cref="AnalysisSampleRate"/> Hz
    /// using simple decimation (no external resampler required).
    /// </summary>
    private static List<float> ReadMonoSamples(WaveStream reader, CancellationToken cancellationToken)
    {
        // Convert to mono float at the native sample rate first
        ISampleProvider monoProvider = reader.ToSampleProvider();
        if (monoProvider.WaveFormat.Channels > 1)
            monoProvider = new StereoToMonoSampleProvider(monoProvider);

        int nativeSampleRate = monoProvider.WaveFormat.SampleRate;
        // Decimation factor: keep every Nth sample to reach ~AnalysisSampleRate
        int decimation = Math.Max(1, nativeSampleRate / AnalysisSampleRate);

        var result = new List<float>(AnalysisSampleRate * 300);
        var buffer = new float[nativeSampleRate]; // 1 s of native-rate mono samples
        int skipCounter = 0;
        int read;

        while ((read = monoProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int i = 0; i < read; i++)
            {
                if (skipCounter == 0)
                    result.Add(buffer[i]);
                skipCounter = (skipCounter + 1) % decimation;
            }

            // Limit analysis to the first 90 seconds for speed
            if (result.Count >= AnalysisSampleRate * 90)
                break;
        }
        return result;
    }

    /// <summary>
    /// Estimates BPM from mono PCM samples using energy-based beat detection and interval histogram voting.
    /// </summary>
    private static double? EstimateBpm(List<float> samples, int sampleRate)
    {
        int windowSize = sampleRate * WindowMs / 1000;
        if (windowSize < 1) return null;

        // Build per-window energy values
        int numWindows = samples.Count / windowSize;
        var energy = new double[numWindows];
        for (int w = 0; w < numWindows; w++)
        {
            double sum = 0;
            int start = w * windowSize;
            for (int i = start; i < start + windowSize; i++)
                sum += samples[i] * (double)samples[i];
            energy[w] = sum / windowSize;
        }

        // Detect beat onsets: energy > BeatSensitivity * local-average
        var onsetTimes = new List<double>(); // in seconds
        for (int w = HistorySize; w < numWindows; w++)
        {
            double avg = 0;
            for (int h = w - HistorySize; h < w; h++)
                avg += energy[h];
            avg /= HistorySize;

            if (energy[w] > BeatSensitivity * avg)
            {
                double timeSeconds = w * WindowMs / 1000.0;
                // Enforce minimum beat gap of ~200ms (300 BPM max)
                if (onsetTimes.Count == 0 || timeSeconds - onsetTimes[^1] >= 0.2)
                    onsetTimes.Add(timeSeconds);
            }
        }

        if (onsetTimes.Count < 2) return null;

        // Build inter-onset interval (IOI) histogram, quantised to 5ms bins
        const int BinMs = 5;
        var histogram = new Dictionary<int, int>();
        for (int i = 1; i < onsetTimes.Count; i++)
        {
            double intervalSec = onsetTimes[i] - onsetTimes[i - 1];
            double bpm = 60.0 / intervalSec;

            // Allow for multiples/fractions (half-time / double-time normalisation)
            while (bpm < MinBpm) bpm *= 2;
            while (bpm > MaxBpm) bpm /= 2;

            if (bpm < MinBpm || bpm > MaxBpm) continue;

            // Convert back to interval in ms and round to bin
            int bin = (int)Math.Round(60000.0 / bpm / BinMs) * BinMs;
            histogram.TryGetValue(bin, out int count);
            histogram[bin] = count + 1;
        }

        if (histogram.Count == 0) return null;

        // Pick the most-voted interval and convert to BPM
        var bestBin = histogram.OrderByDescending(kv => kv.Value).First().Key;
        double resultBpm = 60000.0 / bestBin;

        if (resultBpm < MinBpm || resultBpm > MaxBpm) return null;
        return resultBpm;
    }
}
