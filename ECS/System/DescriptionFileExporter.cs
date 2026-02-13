using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentDesigner.ECS.System
{
    public sealed class DescriptionFileExporter
    {
        private const float SamplePrecision = 0.01f;

        private readonly EcsWorldService _ecsWorld;
        private readonly HierarchyService _hierarchyService;
        private readonly ProjectProperties _projectProperties;
        private readonly int _maxParallelism;

        public DescriptionFileExporter(
            EcsWorldService ecsWorld,
            HierarchyService hierarchyService,
            ProjectProperties projectProperties)
        {
            _ecsWorld = ecsWorld;
            _hierarchyService = hierarchyService;
            _projectProperties = projectProperties;
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

        public async Task<string> ExportToJsonAsync(string filePath)
        {
            var descriptionData = BuildDescriptionData();
            var json = JsonConvert.SerializeObject(descriptionData, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                Converters = [new StringEnumConverter()]
            });

            await File.WriteAllTextAsync(filePath, json);
            return json;
        }

        public string ExportToJson()
        {
            var descriptionData = BuildDescriptionData();
            return JsonConvert.SerializeObject(descriptionData, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                Converters = [new StringEnumConverter()]
            });
        }

        public async UniTask<PlaybackCacheData> ExportToJsonAsync(CancellationToken cancellationToken)
        {
            var cacheData = new PlaybackCacheData
            {
                Version = "1.0",
                ExportTime = DateTime.UtcNow,
                Notes = []
            };

            var entities = new List<(int id, HierarchyNode node)>();
            foreach (var kvp in _hierarchyService.NodeMapping)
            {
                if (!kvp.Value.IsGroup)
                {
                    entities.Add((kvp.Key, kvp.Value));
                }
            }

            if (entities.Count == 0)
            {
                return cacheData;
            }

            var dataBag = new ConcurrentBag<PlaybackNoteData>();
            int batchSize = Math.Max(1, entities.Count / _maxParallelism);
            var tasks = new List<UniTask>();

            for (int i = 0; i < entities.Count; i += batchSize)
            {
                int start = i;
                int end = Math.Min(i + batchSize, entities.Count);

                tasks.Add(UniTask.Run(() =>
                {
                    for (int j = 0; j < end; j++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var (entityId, node) = entities[j];
                        var data = BuildPlaybackNoteData(entityId, node);
                        if (data != null)
                        {
                            dataBag.Add(data);
                        }
                    }
                }));
            }

            await UniTask.WhenAll(tasks);

            cacheData.Notes = [.. dataBag.OrderBy(n => n.StartTime)];
            cacheData.TotalTime = cacheData.Notes.Count > 0 ? cacheData.Notes.Max(n => n.TargetTime) : 0f;
            return cacheData;
        }

        public PlaybackCacheData ExportPlaybackCacheSync()
        {
            var cacheData = new PlaybackCacheData
            {
                Version = "1.0",
                ExportTime = DateTime.UtcNow,
                Notes = []
            };

            foreach (var kvp in _hierarchyService.NodeMapping)
            {
                if (kvp.Value.IsGroup)
                {
                    continue;
                }

                var noteData = BuildPlaybackNoteData(kvp.Key, kvp.Value);
                if (noteData != null)
                {
                    cacheData.Notes.Add(noteData);
                }
            }

            cacheData.Notes = [.. cacheData.Notes.OrderBy(n => n.StartTime)];
            cacheData.TotalTime = cacheData.Notes.Count > 0
                ? cacheData.Notes.Max(n => n.TargetTime)
                : 0f;

            return cacheData;
        }

        private PlaybackNoteData BuildPlaybackNoteData(int id, HierarchyNode node)
        {
            var trans = _ecsWorld.GetComponent<Transform>(id);
            var timing = _ecsWorld.GetComponent<Timing>(id);
            var curve = _ecsWorld.GetComponent<VelocityCurve>(id);
            var meshRenderer = _ecsWorld.GetComponent<MeshRenderer>(id);

            if (timing == null || (timing.TargetMin == 0 && timing.TargetSecond == 0 && timing.TargetTick == 0))
            {
                return null;
            }

            float target = ConvertTimingToSeconds(timing.TargetMin, timing.TargetSecond, timing.TargetTick);
            float start;

            bool hasValidCurve = curve?.Curve.Frames.Length > 0;

            if (hasValidCurve)
            {
                start = curve.Curve.Frames[0].Time;
            }
            else
            {
                start = Math.Max(0f, target - 5f);
            }

            var note = new PlaybackNoteData
            {
                EntityId = id,
                Name = node.Name,
                RailIndex = trans?.Index.Index ?? 0,
                Radius = trans?.Radius ?? 7.5f,
                StartTime = start,
                TargetTime = target,
                NoteType = meshRenderer?.Type ?? NoteTypeEnum.Click,
                Rotation = trans != null
                    ? new DescriptionRotationData
                    {
                        X = trans.Rotation.X,
                        Y = trans.Rotation.Y,
                        Z = trans.Rotation.Z,
                        W = trans.Rotation.W
                    } : null
            };

            if (hasValidCurve)
            {
                note.VelocitySamples = SampleVelocityCurve(curve, start, target);
            }
            else
            {
                note.VelocitySamples = GenerateDefaultVelocitySamples(start, target);
            }

            return note;
        }

        private DescriptionVelocitySampleData GenerateDefaultVelocitySamples(float startTime, float endTime)
        {
            if (startTime >= endTime)
            {
                return new DescriptionVelocitySampleData
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    Precision = SamplePrecision,
                    Samples = [0f]
                };
            }

            float duration = endTime - startTime;
            int sampleCount = (int)MathF.Ceiling(duration / SamplePrecision) + 1;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = 0f;
            }

            return new DescriptionVelocitySampleData
            {
                StartTime = startTime,
                EndTime = endTime,
                Precision = SamplePrecision,
                Samples = [.. samples]
            };
        }

        private DescriptionVelocitySampleData SampleVelocityCurve(VelocityCurve curve, float start, float end)
        {
            if (start >= end)
            {
                return new DescriptionVelocitySampleData
                {
                    StartTime = start,
                    EndTime = end,
                    Precision = SamplePrecision,
                    Samples = [curve.Evaluate(start)]
                };
            }

            float duration = end - start;
            int sampleCount = (int)MathF.Ceiling(duration / SamplePrecision) + 1;
            var samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = start + i * SamplePrecision;
                if (t > end)
                {
                    t = end;
                }

                float normalizedTime = (t - start) / duration;
                samples[i] = curve.Evaluate(normalizedTime);
            }

            return new DescriptionVelocitySampleData()
            {
                StartTime = start,
                EndTime = end,
                Precision = SamplePrecision,
                Samples = [.. samples]
            };
        }

        private DescriptionFileData BuildDescriptionData()
        {
            var data = new DescriptionFileData
            {
                Version = "1.0",
                ExportTime = DateTime.UtcNow,
                Project = new DescriptionProjectData
                {
                    Title = _projectProperties.Title,
                    MusicArtist = _projectProperties.MusicArtist,
                    ChartAuthor = _projectProperties.ChartAuthor,
                    BPM = _projectProperties.BPM,
                    DifficultyConstant = _projectProperties.DifficultyConstant,
                    Difficulty = _projectProperties.Difficulty.ToString()
                },
                Entities = []
            };

            foreach (var rootId in _hierarchyService.Roots)
            {
                var entity = BuildEntityDescriptionData(rootId);
                if (entity != null)
                {
                    data.Entities.Add(entity);
                }
            }

            return data;
        }

        private DescriptionEntityData BuildEntityDescriptionData(int entityId)
        {
            if (!_hierarchyService.NodeMapping.TryGetValue(entityId, out var node))
            {
                return null;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);

            if (node.IsGroup)
            {
                return BuildGroupData(entityId, node, transform);
            }

            return BuildNoteData(entityId, node, transform);
        }

        private DescriptionEntityData BuildGroupData(int entityId, HierarchyNode node, Transform transform)
        {
            var description = _ecsWorld.GetComponent<Description>(entityId);

            var groupData = new DescriptionEntityData
            {
                Id = entityId,
                IsGroup = true,
                Name = node.Name,
                Description = description?.Text,
                ParentId = transform?.Parents ?? -1,
                Children = []
            };

            if (transform?.Children != null)
            {
                foreach (var childId in transform.Children)
                {
                    var childEntity = BuildEntityDescriptionData(childId);
                    if (childEntity != null)
                    {
                        groupData.Children.Add(childEntity);
                    }
                }
            }

            return groupData;
        }

        private DescriptionEntityData BuildNoteData(int entityId, HierarchyNode node, Transform transform)
        {
            var meshRenderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            var timing = _ecsWorld.GetComponent<Timing>(entityId);
            var velocityCurve = _ecsWorld.GetComponent<VelocityCurve>(entityId);

            float targetTime = 0f;
            if (timing != null)
            {
                targetTime = ConvertTimingToSeconds(timing.TargetMin, timing.TargetSecond, timing.TargetTick);
            }

            var noteData = new DescriptionEntityData
            {
                Id = entityId,
                IsGroup = false,
                Name = node.Name,
                RailIndex = transform?.Index.Index ?? 0,
                Rotation = transform != null
                    ? new DescriptionRotationData
                    {
                        X = transform.Rotation.X,
                        Y = transform.Rotation.Y,
                        Z = transform.Rotation.Z,
                        W = transform.Rotation.W
                    }
                    : null,
                ParentId = transform?.Parents ?? -1,
                NoteType = meshRenderer?.Type ?? NoteTypeEnum.Click
            };

            if (velocityCurve != null && timing != null && velocityCurve.Curve.Frames.Length > 0)
            {
                noteData.VelocitySamples = SampleVelocityCurve(velocityCurve, timing);
            }
            else if (timing != null)
            {
                float startTime = Math.Max(0f, targetTime - 5f);
                noteData.VelocitySamples = GenerateDefaultVelocitySamples(startTime, targetTime);
            }

            return noteData;
        }

        private DescriptionVelocitySampleData SampleVelocityCurve(VelocityCurve curve, Timing timing)
        {
            float birthTime = curve.Curve.Frames.FirstOrDefault().Time;
            float targetTime = ConvertTimingToSeconds(timing.TargetMin, timing.TargetSecond, timing.TargetTick);

            if (birthTime >= targetTime && curve.Curve.Frames.Length > 0)
            {
                birthTime = curve.Curve.Frames[0].Time;
                targetTime = curve.Curve.Frames[^1].Time;
            }

            if (birthTime >= targetTime)
            {
                return new DescriptionVelocitySampleData
                {
                    StartTime = birthTime,
                    EndTime = targetTime,
                    Precision = SamplePrecision,
                    Samples = [curve.Evaluate(birthTime)]
                };
            }

            var samples = new List<float>();
            float duration = targetTime - birthTime;
            int sampleCount = (int)MathF.Ceiling(duration / SamplePrecision) + 1;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = birthTime + i * SamplePrecision;
                if (t > targetTime)
                {
                    t = targetTime;
                }

                float normalizedTime = (t - birthTime) / duration;
                samples.Add(curve.Evaluate(normalizedTime));
            }

            return new DescriptionVelocitySampleData
            {
                StartTime = birthTime,
                EndTime = targetTime,
                Precision = SamplePrecision,
                Samples = samples
            };
        }

        private static float ConvertTimingToSeconds(float min, float second, float tick)
        {
            return min * 60f + second + tick / 1000f;
        }
    }
    public sealed class DescriptionFileData
    {
        public string Version { get; set; }
        public DateTime ExportTime { get; set; }
        public DescriptionProjectData Project { get; set; }
        public List<DescriptionEntityData> Entities { get; set; }
    }

    public sealed class DescriptionProjectData
    {
        public string Title { get; set; }
        public string MusicArtist { get; set; }
        public string ChartAuthor { get; set; }
        public string BPM { get; set; }
        public float DifficultyConstant { get; set; }
        public string Difficulty { get; set; }
    }

    public sealed class DescriptionEntityData
    {
        public int Id { get; set; }
        public bool IsGroup { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int? RailIndex { get; set; }
        public float? Radius { get; set; }
        public DescriptionRotationData Rotation { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public NoteTypeEnum? NoteType { get; set; }
        public DescriptionVelocitySampleData VelocitySamples { get; set; }
        public int ParentId { get; set; }
        public List<DescriptionEntityData> Children { get; set; }
    }

    public sealed class DescriptionRotationData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }
    }

    public sealed class DescriptionVelocitySampleData
    {
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public float Precision { get; set; }
        public List<float> Samples { get; set; }
    }
}