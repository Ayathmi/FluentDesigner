using System;
using System.Runtime.CompilerServices;

namespace FluentDesigner.ECS.System;

public sealed class WaveformCache : IDisposable
{
    private const int OverviewSamplesPerPeak = 1024;
    private const int StandardSamplesPerPeak = 256;
    private const int DetailedSamplesPerPeak = 64;
    private const int FineSamplesPerPeak = 16;

    public const int SegmentSize = 256;

    private PeakSample[] _overviewPeaks;
    private PeakSample[] _standardPeaks;
    private PeakSample[] _detailedPeaks;
    private PeakSample[] _finePeaks;

    private bool[] _overviewDirty;
    private bool[] _standardDirty;
    private bool[] _detailedDirty;
    private bool[] _fineDirty;

    private bool _isDisposed;

    public int SampleRate { get; private set; }
    public int ChannelCount { get; private set; }
    public long TotalSamples { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsLoaded { get; private set; }

    public float GenerationProgress { get; private set; }

    public static int GetSamplesPerPeak(WaveformCacheLevel level) => level switch
    {
        WaveformCacheLevel.Overview => OverviewSamplesPerPeak,
        WaveformCacheLevel.Standard => StandardSamplesPerPeak,
        WaveformCacheLevel.Detailed => DetailedSamplesPerPeak,
        WaveformCacheLevel.Fine => FineSamplesPerPeak,
        _ => StandardSamplesPerPeak
    };

    public void Initialize(int rate, int channel, long total)
    {
        SampleRate = rate;
        ChannelCount = channel;
        TotalSamples = total;
        Duration = TimeSpan.FromSeconds((double)total / rate / channel);

        long monoSamples = total / channel;

        int overviewCount = (int)Math.Ceiling((double)monoSamples / OverviewSamplesPerPeak);
        int standardCount = (int)Math.Ceiling((double)monoSamples / StandardSamplesPerPeak);
        int detailedCount = (int)Math.Ceiling((double)monoSamples / DetailedSamplesPerPeak);
        int fineCount = (int)Math.Ceiling((double)monoSamples / FineSamplesPerPeak);

        _overviewPeaks = new PeakSample[overviewCount];
        _standardPeaks = new PeakSample[standardCount];
        _detailedPeaks = new PeakSample[detailedCount];
        _finePeaks = new PeakSample[fineCount];

        _overviewDirty = new bool[(overviewCount + SegmentSize - 1) / SegmentSize];
        _standardDirty = new bool[(standardCount + SegmentSize - 1) / SegmentSize];
        _detailedDirty = new bool[(detailedCount + SegmentSize - 1) / SegmentSize];
        _fineDirty = new bool[(fineCount + SegmentSize - 1) / SegmentSize];

        IsLoaded = false;
        GenerationProgress = 0f;
    }

    public void SetPeaks(WaveformCacheLevel level, PeakSample[] peaks)
    {
        switch (level)
        {
            case WaveformCacheLevel.Overview:
                _overviewPeaks = peaks;
                break;
            case WaveformCacheLevel.Standard:
                _standardPeaks = peaks;
                break;
            case WaveformCacheLevel.Detailed:
                _detailedPeaks = peaks;
                break;
            case WaveformCacheLevel.Fine:
                _finePeaks = peaks;
                break;
        }
    }
    public void MarkAsLoaded()
    {
        IsLoaded = true;
        GenerationProgress = 1f;
    }

    public void UpdateProgress(float progress)
    {
        GenerationProgress = Math.Clamp(progress, 0f, 1f);
    }

    public ReadOnlySpan<PeakSample> GetPeaks(WaveformCacheLevel level) => level switch
    {
        WaveformCacheLevel.Overview => _overviewPeaks,
        WaveformCacheLevel.Standard => _standardPeaks,
        WaveformCacheLevel.Detailed => _detailedPeaks,
        WaveformCacheLevel.Fine => _finePeaks,
        _ => _standardPeaks
    };

    public void MergePeaks(WaveformCacheLevel level, int startIndex, PeakSample[] newPeaks)
    {
        var existingPeaks = GetPeaksArray(level);
        var dirtyFlags = GetDirtyFlags(level);

        if (existingPeaks == null || newPeaks == null)
        {
            return;
        }

        for (int i = 0; i < newPeaks.Length; i++)
        {
            int targetIndex = startIndex + i;
            if (targetIndex >= 0 && targetIndex < existingPeaks.Length)
            {
                var existing = existingPeaks[targetIndex];
                var incoming = newPeaks[i];
                existingPeaks[targetIndex] = new PeakSample(
                    Math.Max(existing.Max, incoming.Max),
                    Math.Min(existing.Min, incoming.Min));
                int segmentIndex = targetIndex / SegmentSize;
                if (segmentIndex < dirtyFlags.Length)
                {
                    dirtyFlags[segmentIndex] = true;
                }
            }
        }
    }

    public bool HasDirtySegments(WaveformCacheLevel level, TimeSpan startTime, TimeSpan endTime)
    {
        var dirtyFlags = GetDirtyFlags(level);
        if (dirtyFlags == null || Duration.TotalSeconds <= 0)
        {
            return false;
        }

        int samplesPerPeak = GetSamplesPerPeak(level);
        double peaksPerSecond = (double)SampleRate / samplesPerPeak;

        int startPeak = (int)(startTime.TotalSeconds * peaksPerSecond);
        int endPeak = (int)Math.Ceiling(endTime.TotalSeconds * peaksPerSecond);

        int startSegment = startPeak / SegmentSize;
        int endSegment = (endPeak + SegmentSize - 1) / SegmentSize;

        for (int i = startSegment; i < endSegment && i < dirtyFlags.Length; i++)
        {
            if (dirtyFlags[i])
            {
                return true;
            }
        }

        return false;
    }

    public void ClearDirtyFlags(WaveformCacheLevel level, int startSegment, int endSegment)
    {
        var dirtyFlags = GetDirtyFlags(level);
        if (dirtyFlags == null)
        {
            return;
        }

        for (int i = startSegment; i < endSegment && i < dirtyFlags.Length; i++)
        {
            dirtyFlags[i] = false;
        }
    }

    public void ClearAllDirtyFlags()
    {
        ClearArray(_overviewDirty);
        ClearArray(_standardDirty);
        ClearArray(_detailedDirty);
        ClearArray(_fineDirty);
    }

    private static void ClearArray(bool[] array)
    {
        if (array != null)
        {
            Array.Clear(array);
        }
    }

    private PeakSample[] GetPeaksArray(WaveformCacheLevel level) => level switch
    {
        WaveformCacheLevel.Overview => _overviewPeaks,
        WaveformCacheLevel.Standard => _standardPeaks,
        WaveformCacheLevel.Detailed => _detailedPeaks,
        WaveformCacheLevel.Fine => _finePeaks,
        _ => _standardPeaks
    };

    private bool[] GetDirtyFlags(WaveformCacheLevel level) => level switch
    {
        WaveformCacheLevel.Overview => _overviewDirty,
        WaveformCacheLevel.Standard => _standardDirty,
        WaveformCacheLevel.Detailed => _detailedDirty,
        WaveformCacheLevel.Fine => _fineDirty,
        _ => _standardDirty
    };

    public ReadOnlySpan<PeakSample> GetPeaksInRange(
        WaveformCacheLevel level,
        TimeSpan startTime,
        TimeSpan endTime)
    {
        var peaks = GetPeaks(level);
        if (peaks.IsEmpty || Duration.TotalSeconds <= 0)
        {
            return ReadOnlySpan<PeakSample>.Empty;
        }

        int samplesPerPeak = GetSamplesPerPeak(level);
        double samplesPerSecond = SampleRate;
        double peaksPerSecond = samplesPerSecond / samplesPerPeak;

        int startIndex = Math.Max(0, (int)(startTime.TotalSeconds * peaksPerSecond));
        int endIndex = Math.Min(peaks.Length, (int)Math.Ceiling(endTime.TotalSeconds * peaksPerSecond));

        if (startIndex >= endIndex || startIndex >= peaks.Length)
        {
            return ReadOnlySpan<PeakSample>.Empty;
        }

        return peaks.Slice(startIndex, endIndex - startIndex);
    }

    public static WaveformCacheLevel GetOptimalLevel(double pixelsPerSecond, int sampleRate)
    {
        double samplesPerPixel = sampleRate / pixelsPerSecond;

        return samplesPerPixel switch
        {
            >= 768 => WaveformCacheLevel.Overview,
            >= 256 => WaveformCacheLevel.Standard,
            >= 32 => WaveformCacheLevel.Detailed,
            _ => WaveformCacheLevel.Fine
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _overviewPeaks = null;
        _standardPeaks = null;
        _detailedPeaks = null;
        _finePeaks = null;

        _overviewDirty = null;
        _standardDirty = null;
        _detailedDirty = null;
        _fineDirty = null;

        _isDisposed = true;
    }
}

public enum WaveformCacheLevel
{
    Overview = 0,
    Standard = 1,
    Detailed = 2,
    Fine = 3
}

public readonly struct PeakSample
{
    public readonly float Max;
    public readonly float Min;

    public PeakSample(float max, float min)
    {
        Min = min;
        Max = max;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PeakSample FromRange(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return default;
        }

        float max = float.MaxValue, min = float.MinValue;

        foreach (var sample in samples)
        {
            if (sample < min)
            {
                min = sample;
            }

            if (sample > max)
            {
                max = sample;
            }
        }

        return new PeakSample(max, min);
    }
}