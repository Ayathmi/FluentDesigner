using Cysharp.Threading.Tasks;
using NAudio.Wave;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace FluentDesigner.ECS.System;
public sealed class WaveformService : Service, IDisposable
{
    private const int ReadBufferSize = 65536;
    private const int ParallelChunkSize = 8192;

    private WaveformCache _cache;
    private string _currentFilePath;
    private CancellationTokenSource _cts;
    private readonly object _lock = new();
    private WaveformGenerator _generator;
    private AudioFileReader _cachedReader;

    public WaveformCache Cache => _cache;
    public bool IsLoading { get; private set; }
    public string CurrentFilePath => _currentFilePath;

    public event EventHandler<float> ProgressChanged;
    public event EventHandler<WaveformCache> WaveformLoaded;
    public event EventHandler<string> LoadFailed;
    public event EventHandler WaveformCleared;
    public event EventHandler<(WaveformCacheLevel level, int startIndex, PeakSample[] peaks)> SegmentGenerated;

    public override void Initialize()
    {
        base.Initialize();
        _cache = new WaveformCache();
        _generator = new WaveformGenerator();
        _generator.ProgressChanged += OnGeneratorProgress;
        _generator.SegmentReady += OnSegmentReady;
    }

    private void OnGeneratorProgress(float progress)
    {
        ReportProgress(progress);
    }

    private void OnSegmentReady(WaveformCacheLevel level, int startIndex, PeakSample[] peaks)
    {
        SegmentGenerated?.Invoke(this, (level, startIndex, peaks));
    }

    public override void Shutdown()
    {
        CancelCurrentOperation();
        _cache?.Dispose();
        base.Shutdown();
    }

    public async UniTask GenerateAllCacheLevelsAsync(
    AudioFileReader reader,
    DispatcherQueue dispatcherQueue,
    CancellationToken token,
    string filePath)
    {
        _currentFilePath = filePath;
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        _cache.Initialize(sampleRate, channels, totalSamples);
        long totalMonoSamples = _cache.TotalSamples / channels;

        var finePeaks = new ConcurrentDictionary<int, PeakSample>();
        var detailedPeaks = new ConcurrentDictionary<int, PeakSample>();
        var standardPeaks = new ConcurrentDictionary<int, PeakSample>();
        var overviewPeaks = new ConcurrentDictionary<int, PeakSample>();

        int fineSpp = WaveformCache.GetSamplesPerPeak(WaveformCacheLevel.Fine);
        int detailedSpp = WaveformCache.GetSamplesPerPeak(WaveformCacheLevel.Detailed);
        int standardSpp = WaveformCache.GetSamplesPerPeak(WaveformCacheLevel.Standard);
        int overviewSpp = WaveformCache.GetSamplesPerPeak(WaveformCacheLevel.Overview);

        float[] readBuffer = ArrayPool<float>.Shared.Rent(ReadBufferSize);
        var processingTasks = new ConcurrentBag<UniTask>();

        int maxParallelism = Math.Max(1, Environment.ProcessorCount - 1);
        using var semaphore = new SemaphoreSlim(maxParallelism);

        long samplesRead = 0;
        long lastProgressSample = 0;
        const long progressInterval = 100000;

        try
        {
            int read;
            while ((read = reader.Read(readBuffer, 0, ReadBufferSize)) > 0)
            {
                token.ThrowIfCancellationRequested();

                float[] chunkData = ArrayPool<float>.Shared.Rent(read);
                Array.Copy(readBuffer, chunkData, read);

                long chunkStartSample = samplesRead;
                int chunkCount = read;

                await semaphore.WaitAsync(token);

                var task = UniTask.Run(() =>
                {
                    try
                    {
                        ProcessChunkParallel(
                            chunkData, chunkStartSample, chunkCount, channels,
                            finePeaks, detailedPeaks, standardPeaks, overviewPeaks,
                            fineSpp, detailedSpp, standardSpp, overviewSpp);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(chunkData);
                        semaphore.Release();
                    }
                });

                processingTasks.Add(task);
                samplesRead += read;

                if (samplesRead - lastProgressSample >= progressInterval)
                {
                    lastProgressSample = samplesRead;
                    float progress = (float)samplesRead / _cache.TotalSamples;
                    _cache.UpdateProgress(progress);

                    dispatcherQueue.TryEnqueue(() => ReportProgress(progress));
                }
            }

            await UniTask.WhenAll(processingTasks);

            _cache.SetPeaks(WaveformCacheLevel.Fine, ConvertToArray(finePeaks, totalMonoSamples, fineSpp));
            _cache.SetPeaks(WaveformCacheLevel.Detailed, ConvertToArray(detailedPeaks, totalMonoSamples, detailedSpp));
            _cache.SetPeaks(WaveformCacheLevel.Standard, ConvertToArray(standardPeaks, totalMonoSamples, standardSpp));
            _cache.SetPeaks(WaveformCacheLevel.Overview, ConvertToArray(overviewPeaks, totalMonoSamples, overviewSpp));

            _cache.MarkAsLoaded();

            dispatcherQueue.TryEnqueue(() =>
            {
                ReportProgress(1f);
                WaveformLoaded?.Invoke(this, _cache);
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuffer);
        }
    }

    private static void ProcessChunkParallel(
        float[] data, long startSample, int count, int channels,
        ConcurrentDictionary<int, PeakSample> finePeaks,
        ConcurrentDictionary<int, PeakSample> detailedPeaks,
        ConcurrentDictionary<int, PeakSample> standardPeaks,
        ConcurrentDictionary<int, PeakSample> overviewPeaks,
        int fineSpp, int detailedSpp, int standardSpp, int overviewSpp)
    {
        int monoCount = count / channels;
        Span<float> monoBuffer = monoCount <= 4096
            ? stackalloc float[monoCount]
            : new float[monoCount];

        int monoIndex = 0;
        for (int i = 0; i < count && monoIndex < monoBuffer.Length; i += channels)
        {
            float sum = 0f;
            int validChannels = 0;
            for (int c = 0; c < channels && i + c < count; c++)
            {
                sum += data[i + c];
                validChannels++;
            }
            monoBuffer[monoIndex++] = validChannels > 0 ? sum / validChannels : 0f;
        }

        var monoData = monoBuffer.Slice(0, monoIndex);
        long monoStartSample = startSample / channels;

        ProcessLevelPeaks(monoData, monoStartSample, finePeaks, fineSpp);
        ProcessLevelPeaks(monoData, monoStartSample, detailedPeaks, detailedSpp);
        ProcessLevelPeaks(monoData, monoStartSample, standardPeaks, standardSpp);
        ProcessLevelPeaks(monoData, monoStartSample, overviewPeaks, overviewSpp);
    }

    private static void ProcessLevelPeaks(
        ReadOnlySpan<float> monoData,
        long monoStartSample,
        ConcurrentDictionary<int, PeakSample> peaks,
        int samplesPerPeak)
    {
        for (int i = 0; i < monoData.Length; i++)
        {
            long globalSampleIndex = monoStartSample + i;
            int peakIndex = (int)(globalSampleIndex / samplesPerPeak);

            float sample = monoData[i];
            peaks.AddOrUpdate(
                peakIndex,
                new PeakSample(sample, sample),
                (_, existing) => new PeakSample(
                    Math.Min(existing.Min, sample),
                    Math.Max(existing.Max, sample)));
        }
    }

    private static PeakSample[] ConvertToArray(
        ConcurrentDictionary<int, PeakSample> dict,
        long totalMonoSamples,
        int samplesPerPeak)
    {
        int count = (int)Math.Ceiling((double)totalMonoSamples / samplesPerPeak);
        var result = new PeakSample[count];

        foreach (var kvp in dict)
        {
            if (kvp.Key >= 0 && kvp.Key < count)
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private void ReportProgress(float progress)
    {
        ProgressChanged?.Invoke(this, progress);
    }

    public void CancelCurrentOperation()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void ClearWaveform()
    {
        CancelCurrentOperation();
        _cache?.Dispose();
        _cache = new WaveformCache();
        _currentFilePath = null;
        WaveformCleared?.Invoke(this, EventArgs.Empty);
    }

    public bool NeedsRedraw(TimeSpan startTime, TimeSpan endTime, double pixelsPerSecond)
    {
        if (_cache == null || !_cache.IsLoaded)
        {
            return false;
        }

        var level = WaveformCache.GetOptimalLevel(pixelsPerSecond, _cache.SampleRate);
        return _cache.HasDirtySegments(level, startTime, endTime);
    }

    public void Dispose()
    {
        CancelCurrentOperation();
        _cache?.Dispose();
        _generator?.Dispose();
        _cachedReader?.Dispose();
        base.Shutdown();
    }
}