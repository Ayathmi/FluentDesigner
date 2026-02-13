using FluentDesigner.ECS.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FluentDesigner.ECS.System
{
    public sealed class ExchangeFileExporter
    {
        private readonly EcsWorldService _ecsWorld;
        private readonly HierarchyService _hierarchyService;
        private readonly ProjectProperties _projectProperties;
        private readonly WaveformService _waveformService;
        private readonly CsvMarkerService _csvMarkerService;

        public ExchangeFileExporter(
            EcsWorldService ecsWorld,
            HierarchyService hierarchyService,
            ProjectProperties projectProperties,
            WaveformService waveformService = null,
            CsvMarkerService csvMarkerService = null)
        {
            _ecsWorld = ecsWorld;
            _hierarchyService = hierarchyService;
            _projectProperties = projectProperties;
            _waveformService = waveformService;
            _csvMarkerService = csvMarkerService;
        }

        public async Task<string> ExportToJsonAsync(string filePath)
        {
            var exchangeData = BuildExchangeData();
            var json = JsonConvert.SerializeObject(exchangeData, new JsonSerializerSettings
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
            var exchangeData = BuildExchangeData();
            return JsonConvert.SerializeObject(exchangeData, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                Converters = [new StringEnumConverter()]
            });
        }

        private ExchangeData BuildExchangeData()
        {
            var data = new ExchangeData
            {
                Version = "1.0",
                ExportTime = DateTime.UtcNow,
                Project = new ProjectData
                {
                    Title = _projectProperties.Title,
                    MusicArtist = _projectProperties.MusicArtist,
                    ChartAuthor = _projectProperties.ChartAuthor,
                    BPM = _projectProperties.BPM,
                    DifficultyConstant = _projectProperties.DifficultyConstant,
                    Difficulty = _projectProperties.Difficulty.ToString(),
                    Description = _projectProperties.Description,
                    WavFilePath = _waveformService?.CurrentFilePath,
                    CsvFilePath = _csvMarkerService?.CurrentFilePath
                },
                Entities = []
            };

            foreach (var rootId in _hierarchyService.Roots)
            {
                var entity = BuildEntityData(rootId);
                if (entity != null)
                {
                    data.Entities.Add(entity);
                }
            }

            return data;
        }

        private EntityData BuildEntityData(int entityId)
        {
            if (!_hierarchyService.NodeMapping.TryGetValue(entityId, out var node))
            {
                return null;
            }

            var entityData = new EntityData
            {
                Name = node.Name,
                IsGroup = node.IsGroup,
                NoteType = node.NoteType
            };

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform != null)
            {
                entityData.Transform = new TransformData
                {
                    Position = new Vector3Data(transform.Position.X, transform.Position.Y, transform.Position.Z),
                    Rotation = new Vector4Data(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
                    Scale = new Vector3Data(transform.Scale.X, transform.Scale.Y, transform.Scale.Z),
                    RailIndex = transform.Index.Index,
                    Radius = transform.Radius
                };
            }

            var meshRenderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            if (meshRenderer != null)
            {
                entityData.MeshRenderer = new MeshRendererData
                {
                    Type = meshRenderer.Type,
                    Color = new Vector4Data(meshRenderer.Color.X, meshRenderer.Color.Y, meshRenderer.Color.Z, meshRenderer.Color.W),
                    Visibility = meshRenderer.Visibility,
                    SortingLayer = meshRenderer.SortingLayer,
                    OrderInLayer = meshRenderer.OrderInLayer
                };
            }

            var timing = _ecsWorld.GetComponent<Timing>(entityId);
            if (timing != null)
            {
                entityData.Timing = new TimingData
                {
                    TargetMin = timing.TargetMin,
                    TargetSecond = timing.TargetSecond,
                    TargetTick = timing.TargetTick
                };
            }

            var description = _ecsWorld.GetComponent<Description>(entityId);
            if (description != null && !string.IsNullOrEmpty(description.Text))
            {
                entityData.Description = new DescriptionData
                {
                    Text = description.Text
                };
            }

            var velocityCurve = _ecsWorld.GetComponent<VelocityCurve>(entityId);
            if (velocityCurve != null && velocityCurve.Curve.Frames.Length > 0)
            {
                entityData.VelocityCurve = new VelocityCurveData
                {
                    KeyFrames = []
                };

                foreach (var frame in velocityCurve.Curve.Frames)
                {
                    var frameData = new KeyFrameData
                    {
                        Index = frame.Index,
                        Time = frame.Time,
                        Value = frame.Value,
                        InterpolationType = frame.InterpolationType,
                        ControlPoints = []
                    };

                    if (frame.ControlPoints != null)
                    {
                        foreach (var cp in frame.ControlPoints)
                        {
                            frameData.ControlPoints.Add(new ControlPointData { X = cp.X, Y = cp.Y });
                        }
                    }

                    entityData.VelocityCurve.KeyFrames.Add(frameData);
                }
            }

            if (node.IsGroup && transform?.Children != null)
            {
                entityData.Children = [];
                foreach (var childId in transform.Children)
                {
                    var childEntity = BuildEntityData(childId);
                    if (childEntity != null)
                    {
                        entityData.Children.Add(childEntity);
                    }
                }
            }

            return entityData;
        }
    }

    public sealed class ExchangeData
    {
        public string Version { get; set; }
        public DateTime ExportTime { get; set; }
        public ProjectData Project { get; set; }
        public List<EntityData> Entities { get; set; }
    }

    public sealed class ProjectData
    {
        public string Title { get; set; }
        public string MusicArtist { get; set; }
        public string ChartAuthor { get; set; }
        public string BPM { get; set; }
        public float DifficultyConstant { get; set; }
        public string Difficulty { get; set; }
        public string Description { get; set; }
        public string WavFilePath { get; set; }
        public string CsvFilePath { get; set; }
    }

    public sealed class EntityData
    {
        public string Name { get; set; }
        public bool IsGroup { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public NoteTypeEnum NoteType { get; set; }

        public TransformData Transform { get; set; }
        public MeshRendererData MeshRenderer { get; set; }
        public TimingData Timing { get; set; }
        public DescriptionData Description { get; set; }
        public VelocityCurveData VelocityCurve { get; set; }
        public List<EntityData> Children { get; set; }
    }

    public sealed class TransformData
    {
        public Vector3Data Position { get; set; }
        public Vector4Data Rotation { get; set; }
        public Vector3Data Scale { get; set; }
        public int RailIndex { get; set; }
        public float Radius { get; set; } = 7.5f;
    }

    public sealed class MeshRendererData
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public NoteTypeEnum Type { get; set; }

        public Vector4Data Color { get; set; }
        public bool Visibility { get; set; }
        public int SortingLayer { get; set; }
        public int OrderInLayer { get; set; }
    }

    public sealed class TimingData
    {
        public float TargetMin { get; set; }
        public float TargetSecond { get; set; }
        public float TargetTick { get; set; }
    }

    public sealed class DescriptionData
    {
        public string Text { get; set; }
    }

    public sealed class VelocityCurveData
    {
        public List<KeyFrameData> KeyFrames { get; set; }
    }

    public sealed class KeyFrameData
    {
        public uint Index { get; set; }
        public float Time { get; set; }
        public float Value { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public CurveInterpolationType InterpolationType { get; set; }

        public List<ControlPointData> ControlPoints { get; set; }
    }

    public sealed class ControlPointData
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    public sealed class Vector3Data
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3Data() { }
        public Vector3Data(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public sealed class Vector4Data
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public Vector4Data() { }
        public Vector4Data(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    }
}