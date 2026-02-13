using Cysharp.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NAudio.Wave;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace FluentDesigner.ECS.System;

public sealed class WaveformGenerator : IDisposable
{
    private const int ReadBufferSize = 65536;
    private readonly int _maxParallelism;
    private bool _isDisposed;

    public event Action<float> ProgressChanged;
    public event Action<WaveformCacheLevel, int, PeakSample[]> SegmentReady;

    public WaveformGenerator()
    {
        _maxParallelism = Math.Max(1, Environment.ProcessorCount - 1);
    }

    public async UniTask<WaveformCache> GenerateFromFileAsync(
        string filePath,
        DispatcherQueue dispatcherQueue,
        CancellationToken token = default)
    {
        using var reader = new AudioFileReader(filePath);
        return await GenerateFromReaderAsync(reader, dispatcherQueue, token);
    }

    public async UniTask<WaveformCache> GenerateFromReaderAsync(
        AudioFileReader reader,
        DispatcherQueue dispatcherQueue,
        CancellationToken token = default)
    {
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);

        var cache = new WaveformCache();
        cache.Initialize(sampleRate, channels, totalSamples);

        await GeneratePeaksAsync(reader, cache, dispatcherQueue, token);

        cache.MarkAsLoaded();
        return cache;
    }

    public async UniTask GenerateRangeAsync(
        AudioFileReader reader,
        WaveformCache cache,
        TimeSpan startTime,
        TimeSpan endTime,
        DispatcherQueue dispatcherQueue,
        CancellationToken token = default)
    {
        if (cache == null || !cache.IsLoaded)
        {
            return;
        }

        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;

        long startSample = (long)(startTime.TotalSeconds * sampleRate * channels);
        long endSample = (long)(endTime.TotalSeconds * sampleRate * channels);

        startSample = Math.Max(0, startSample);
        endSample = Math.Min(cache.TotalSamples, endSample);

        if (startSample >= endSample)
        {
            return;
        }

        reader.Position = startSample * sizeof(float);

        await GeneratePeaksRangeAsync(reader, cache, startSample, endSample, channels, dispatcherQueue, token);
    }

    private async UniTask GeneratePeaksAsync(
        AudioFileReader reader,
        WaveformCache cache,
        DispatcherQueue dispatcherQueue,
        CancellationToken token)
    {
        int channels = reader.WaveFormat.Channels;
        long totalMonoSamples = cache.TotalSamples / channels;

        var levelData = CreateLevelDictionaries();
        var levelSpp = GetAllSamplesPerPeak();

        float[] readBuffer = ArrayPool<float>.Shared.Rent(ReadBufferSize);
        var processingTasks = new ConcurrentBag<UniTask>();
        using var semaphore = new SemaphoreSlim(_maxParallelism);

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
                        ProcessChunk(chunkData, chunkStartSample, chunkCount, channels, levelData, levelSpp);
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
                    float progress = (float)samplesRead / cache.TotalSamples;
                    cache.UpdateProgress(progress);
                    dispatcherQueue?.TryEnqueue(() => ProgressChanged?.Invoke(progress));
                }
            }

            await UniTask.WhenAll(processingTasks);

            foreach (WaveformCacheLevel level in Enum.GetValues<WaveformCacheLevel>())
            {
                int spp = WaveformCache.GetSamplesPerPeak(level);
                var peaks = ConvertToArray(levelData[level], totalMonoSamples, spp);
                cache.SetPeaks(level, peaks);
            }

            dispatcherQueue?.TryEnqueue(() => ProgressChanged?.Invoke(1f));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuffer);
        }
    }

    private async UniTask GeneratePeaksRangeAsync(
        AudioFileReader reader,
        WaveformCache cache,
        long startSample,
        long endSample,
        int channels,
        DispatcherQueue dispatcherQueue,
        CancellationToken token)
    {
        var levelData = CreateLevelDictionaries();
        var levelSpp = GetAllSamplesPerPeak();

        float[] readBuffer = ArrayPool<float>.Shared.Rent(ReadBufferSize);
        var processingTasks = new ConcurrentBag<UniTask>();
        using var semaphore = new SemaphoreSlim(_maxParallelism);

        long samplesRead = startSample;
        long totalToRead = endSample - startSample;

        try
        {
            int read;
            while (samplesRead < endSample &&
                   (read = reader.Read(readBuffer, 0, (int)Math.Min(ReadBufferSize, endSample - samplesRead))) > 0)
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
                        ProcessChunk(chunkData, chunkStartSample, chunkCount, channels, levelData, levelSpp);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(chunkData);
                        semaphore.Release();
                    }
                });

                processingTasks.Add(task);
                samplesRead += read;
            }

            await UniTask.WhenAll(processingTasks);

            long monoStart = startSample / channels;
            long monoEnd = endSample / channels;

            foreach (WaveformCacheLevel level in Enum.GetValues<WaveformCacheLevel>())
            {
                int spp = WaveformCache.GetSamplesPerPeak(level);
                int startPeakIndex = (int)(monoStart / spp);
                int endPeakIndex = (int)Math.Ceiling((double)monoEnd / spp);

                var segmentPeaks = ConvertToArrayRange(levelData[level], startPeakIndex, endPeakIndex);
                cache.MergePeaks(level, startPeakIndex, segmentPeaks);

                dispatcherQueue?.TryEnqueue(() =>
                    SegmentReady?.Invoke(level, startPeakIndex, segmentPeaks));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuffer);
        }
    }

    private void ProcessChunk(
        float[] data,
        long startSample,
        int count,
        int channels,
        Dictionary<WaveformCacheLevel, ConcurrentDictionary<int, PeakSample>> levelData,
        Dictionary<WaveformCacheLevel, int> levelSpp)
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

        foreach (var level in levelData.Keys)
        {
            ProcessLevelPeaks(monoData, monoStartSample, levelData[level], levelSpp[level]);
        }
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
                    Math.Max(existing.Max, sample),
                    Math.Min(existing.Min, sample)));
        }
    }

    private static Dictionary<WaveformCacheLevel, ConcurrentDictionary<int, PeakSample>> CreateLevelDictionaries()
    {
        var dict = new Dictionary<WaveformCacheLevel, ConcurrentDictionary<int, PeakSample>>();
        foreach (WaveformCacheLevel level in Enum.GetValues<WaveformCacheLevel>())
        {
            dict[level] = new ConcurrentDictionary<int, PeakSample>();
        }
        return dict;
    }

    private static Dictionary<WaveformCacheLevel, int> GetAllSamplesPerPeak()
    {
        var dict = new Dictionary<WaveformCacheLevel, int>();
        foreach (WaveformCacheLevel level in Enum.GetValues<WaveformCacheLevel>())
        {
            dict[level] = WaveformCache.GetSamplesPerPeak(level);
        }
        return dict;
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

    private static PeakSample[] ConvertToArrayRange(
        ConcurrentDictionary<int, PeakSample> dict,
        int startIndex,
        int endIndex)
    {
        int count = endIndex - startIndex;
        var result = new PeakSample[count];

        foreach (var kvp in dict)
        {
            int localIndex = kvp.Key - startIndex;
            if (localIndex >= 0 && localIndex < count)
            {
                result[localIndex] = kvp.Value;
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
    }
}