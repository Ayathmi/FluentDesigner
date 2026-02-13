using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FluentDesigner.ECS.System;

public class PlaybackCacheParser
{
    private const float PositionSamplePrecision = 0.002f;
    private const float BaseSpeed = 10f;
    private readonly int _maxParallelism;

    public PlaybackCacheParser()
    {
        _maxParallelism = GetOptimalParallelism();
    }

    private static int GetOptimalParallelism()
    {
        int processorCount = Environment.ProcessorCount;
        return processorCount switch
        {
            <= 4 => 2,
            <= 8 => 4,
            _ => 8
        };
    }

    public async UniTask<PlaybackCacheData> ParseFromJsonAsync(string json, CancellationToken cancellationToken)
    {
        return await UniTask.Run(() =>
        {
            return JsonConvert.DeserializeObject<PlaybackCacheData>(json, new JsonSerializerSettings
            {
                Converters = [new StringEnumConverter()]
            });
        });
    }

    public PlaybackCacheData ParseFromJson(string json)
    {
        return JsonConvert.DeserializeObject<PlaybackCacheData>(json, new JsonSerializerSettings
        {
            Converters = [new StringEnumConverter()]
        });
    }

    public async UniTask<List<PlaybackNoteInstance>> PrecomputePositionsAsync(
        PlaybackCacheData cache,
        float multiplier = 1.0f,
        CancellationToken cancellationToken = default)
    {
        if (cache?.Notes == null || cache.Notes.Count == 0)
        {
            return [];
        }

        var insBag = new ConcurrentBag<PlaybackNoteInstance>();
        int size = Math.Max(1, cache.Notes.Count / _maxParallelism);
        var tasks = new List<UniTask>();
        for (int i = 0; i < cache.Notes.Count; i += size)
        {
            int start = i;
            int end = Math.Min(start + size, cache.Notes.Count);
            tasks.Add(UniTask.Run(() =>
            {
                for (int j = start; j < end; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var noteData = cache.Notes[j];
                    var noteInstance = ComputeNoteInstance(noteData, multiplier);
                    insBag.Add(noteInstance);
                }
            }));
        }

        await UniTask.WhenAll(tasks);
        return [.. insBag.OrderBy(n => n.StartTime)];
    }

    public List<PlaybackNoteInstance> PrecomputePositionsSync(
        PlaybackCacheData cacheData,
        float baseSpeedMultiplier = 1.0f)
    {
        if (cacheData?.Notes == null || cacheData.Notes.Count == 0)
        {
            return [];
        }

        var instances = new List<PlaybackNoteInstance>(cacheData.Notes.Count);
        foreach (var noteData in cacheData.Notes)
        {
            instances.Add(ComputeNoteInstance(noteData, baseSpeedMultiplier));
        }

        return [.. instances.OrderBy(n => n.StartTime)];
    }

    private PlaybackNoteInstance ComputeNoteInstance(PlaybackNoteData noteData, float baseSpeedMultiplier)
    {
        var instance = new PlaybackNoteInstance
        {
            EntityId = noteData.EntityId,
            Name = noteData.Name,
            RailIndex = noteData.RailIndex,
            Radius = noteData.Radius,
            NoteType = noteData.NoteType,
            StartTime = noteData.StartTime,
            TargetTime = noteData.TargetTime,
            Rotation = noteData.Rotation,
            PositionTimeline = []
        };

        var (cylinderX, cylinderY) = SceneLayoutService.GetCylinderPosition(noteData.RailIndex, noteData.Radius);

        instance.PositionTimeline = ComputePositionTimeline(
            noteData,
            cylinderX,
            cylinderY,
            baseSpeedMultiplier);

        return instance;
    }

    private List<PositionTimePoint> ComputePositionTimeline(
    PlaybackNoteData noteData,
    float cylinderX,
    float cylinderY,
    float baseSpeedMultiplier)
    {
        var timeline = new List<PositionTimePoint>();

        float startTime = noteData.StartTime;
        float targetTime = noteData.TargetTime;

        if (startTime >= targetTime)
        {
            float z = SceneLayoutService.SecondsToZ(targetTime);
            timeline.Add(new PositionTimePoint
            {
                Time = targetTime,
                X = cylinderX,
                Y = cylinderY,
                Z = z
            });
            return timeline;
        }

        var samples = noteData.VelocitySamples;
        float duration = targetTime - startTime;
        float targetZ = 0f;
        float accumulatedDistance = 0f;

        int sampleCount = (int)MathF.Ceiling(duration / PositionSamplePrecision) + 1;
        var zPositions = new float[sampleCount];
        var times = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = startTime + i * PositionSamplePrecision;
            if (t > targetTime)
            {
                t = targetTime;
            }

            times[i] = t;

            float coefficient = GetVelocityCoefficientAtTime(samples, t, startTime);
            float speed = baseSpeedMultiplier * BaseSpeed * (1f + coefficient);

            if (i > 0)
            {
                float dt = times[i] - times[i - 1];
                accumulatedDistance += speed * dt;
            }
        }


        float startZ = targetZ + accumulatedDistance;
        float currentZ = startZ;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = times[i];

            if (i > 0)
            {
                float coefficient = GetVelocityCoefficientAtTime(samples, t, startTime);
                float speed = baseSpeedMultiplier * BaseSpeed * (1f + coefficient);
                float dt = times[i] - times[i - 1];
                currentZ -= speed * dt;
            }

            timeline.Add(new PositionTimePoint
            {
                Time = t,
                X = cylinderX,
                Y = cylinderY,
                Z = currentZ
            });
        }

        return timeline;
    }

    private static float GetVelocityCoefficientAtTime(DescriptionVelocitySampleData samples, float time, float startTime)
    {
        if (samples?.Samples == null || samples.Samples.Count == 0)
        {
            return 0f;
        }

        float relativeTime = time - startTime;
        if (relativeTime <= 0)
        {
            return samples.Samples[0];
        }

        int index = (int)(relativeTime / samples.Precision);

        if (index >= samples.Samples.Count - 1)
        {
            return samples.Samples[^1];
        }

        float t = (relativeTime - index * samples.Precision) / samples.Precision;
        return samples.Samples[index] * (1f - t) + samples.Samples[index + 1] * t;
    }

    public static (float x, float y, float z) InterpolatePosition(
        List<PositionTimePoint> timeline,
        float currentTime)
    {
        if (timeline == null || timeline.Count == 0)
        {
            return (0, 0, 0);
        }

        if (currentTime <= timeline[0].Time)
        {
            var first = timeline[0];
            return (first.X, first.Y, first.Z);
        }

        if (currentTime >= timeline[^1].Time)
        {
            var last = timeline[^1];
            return (last.X, last.Y, last.Z);
        }

        int left = 0;
        int right = timeline.Count - 1;

        while (left < right - 1)
        {
            int mid = (left + right) / 2;
            if (timeline[mid].Time <= currentTime)
            {
                left = mid;
            }
            else
            {
                right = mid;
            }
        }

        var p1 = timeline[left];
        var p2 = timeline[right];
        float t = (currentTime - p1.Time) / (p2.Time - p1.Time);

        return (
            p1.X + (p2.X - p1.X) * t,
            p1.Y + (p2.Y - p1.Y) * t,
            p1.Z + (p2.Z - p1.Z) * t
        );
    }
}