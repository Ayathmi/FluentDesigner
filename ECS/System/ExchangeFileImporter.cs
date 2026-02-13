using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace FluentDesigner.ECS.System
{
    public sealed class ExchangeFileImporter
    {
        private readonly EcsWorldService _ecsWorld;
        private readonly HierarchyService _hierarchyService;
        private readonly ProjectProperties _projectProperties;
        private readonly TextureService _textureService;
        private readonly int _maxParallelism;

        public ExchangeFileImporter(
            EcsWorldService ecsWorld,
            HierarchyService hierarchyService,
            ProjectProperties projectProperties,
            TextureService textureService)
        {
            _ecsWorld = ecsWorld;
            _hierarchyService = hierarchyService;
            _projectProperties = projectProperties;
            _textureService = textureService;
            _maxParallelism = GetOptimalParallelism();
        }

        public static int GetOptimalParallelism()
        {
            int processorCount = Environment.ProcessorCount;
            return processorCount switch
            {
                <= 4 => 2,
                <= 8 => 4,
                _ => 8
            };
        }

        public async UniTask<ImportResult> ImportFromJsonAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var result = new ImportResult();

            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var exchangeData = await UniTask.Run(() =>
                {
                    return JsonConvert.DeserializeObject<ExchangeData>(json, new JsonSerializerSettings
                    {
                        Converters = [new StringEnumConverter()]
                    });
                });

                if (exchangeData == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed To Resolve JSON File. ";
                    return result;
                }

                ClearExistingScene();
                ImportProjectProperties(exchangeData.Project);

                if (exchangeData.Entities != null && exchangeData.Entities.Count > 0)
                {
                    await ImportEntitiesParallelAsync(exchangeData.Entities, result, cancellationToken);
                }
                _hierarchyService.NotifyHierarchyRefresh();
                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Import Cancelled.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public async UniTask<ImportResult> ImportFromJsonStringAsync(
            string json,
            bool skipProjectProperties = false,
            CancellationToken cancellationToken = default)
        {
            var result = new ImportResult();

            try
            {
                var exchangeData = await UniTask.Run(() =>
                {
                    return JsonConvert.DeserializeObject<ExchangeData>(json, new JsonSerializerSettings
                    {
                        Converters = [new StringEnumConverter()]
                    });
                });

                if (exchangeData == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed to parse JSON string.";
                    return result;
                }

                ClearExistingScene();

                if (!skipProjectProperties)
                {
                    ImportProjectProperties(exchangeData.Project);
                }

                if (exchangeData.Entities != null && exchangeData.Entities.Count > 0)
                {
                    await ImportEntitiesParallelAsync(exchangeData.Entities, result, cancellationToken);
                }
                _hierarchyService.NotifyHierarchyRefresh();
                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Import cancelled.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public ImportResult ImportFromJsonStringSync(string json, bool skipProjectProperties = false)
        {
            var result = new ImportResult();

            try
            {
                var exchangeData = JsonConvert.DeserializeObject<ExchangeData>(json, new JsonSerializerSettings
                {
                    Converters = [new StringEnumConverter()]
                });

                if (exchangeData == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed to parse JSON string.";
                    return result;
                }

                ClearExistingScene();

                if (!skipProjectProperties)
                {
                    ImportProjectProperties(exchangeData.Project);
                }

                if (exchangeData.Entities != null && exchangeData.Entities.Count > 0)
                {
                    ImportEntitiesSync(exchangeData.Entities, result);
                }
                _hierarchyService.NotifyHierarchyRefresh();
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private void ClearExistingScene()
        {
            var rootsCopy = new List<int>(_hierarchyService.Roots);
            foreach (var rootId in rootsCopy)
            {
                _hierarchyService.DeleteNode(rootId);
            }
        }

        private void ImportProjectProperties(ProjectData projectData)
        {
            if (projectData == null)
            {
                return;
            }

            _projectProperties.Title = projectData.Title ?? _projectProperties.Title;
            _projectProperties.MusicArtist = projectData.MusicArtist ?? _projectProperties.MusicArtist;
            _projectProperties.ChartAuthor = projectData.ChartAuthor ?? _projectProperties.ChartAuthor;
            _projectProperties.BPM = projectData.BPM ?? _projectProperties.BPM;
            _projectProperties.DifficultyConstant = projectData.DifficultyConstant;
            _projectProperties.Description = projectData.Description ?? _projectProperties.Description;

            if (Enum.TryParse<DifficultyCategory>(projectData.Difficulty, out var difficulty))
            {
                _projectProperties.Difficulty = difficulty;
            }
        }

        private async UniTask ImportEntitiesParallelAsync(List<EntityData> entities, ImportResult result, CancellationToken cancellationToken)
        {
            var flattenedEntities = new List<FlattenedEntity>();
            FlattenEntityTree(entities, flattenedEntities, 0, -1);

            if (flattenedEntities.Count == 0)
            {
                return;
            }

            int importedEntityCount = 0;
            int importedGroupCount = 0;

            var indexedEntities = flattenedEntities.Select((e, idx) => (Entity: e, OriginalIndex: idx)).ToList();

            var entitiesByDepth = indexedEntities
                .GroupBy(e => e.Entity.Depth)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.ToList());

            var createdEntityIds = new ConcurrentDictionary<int, int>();

            foreach (var kvp in entitiesByDepth.OrderBy(k => k.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entitiesAtDepth = kvp.Value;
                var entityIds = _ecsWorld.CreateEntities(entitiesAtDepth.Count);
                var tasks = new List<UniTask>();
                int batchSize = Math.Max(1, entitiesAtDepth.Count / _maxParallelism);

                for (int batchStart = 0; batchStart < entitiesAtDepth.Count; batchStart += batchSize)
                {
                    int start = batchStart;
                    int end = Math.Min(batchStart + batchSize, entitiesAtDepth.Count);

                    tasks.Add(UniTask.Run(() =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            var (flatEntity, originalIndex) = entitiesAtDepth[i];
                            var entityId = entityIds[i];

                            InitializeEntityComponents(entityId, flatEntity.Data);
                            createdEntityIds[originalIndex] = entityId;

                            Interlocked.Increment(ref importedEntityCount);
                            if (flatEntity.Data.IsGroup)
                            {
                                Interlocked.Increment(ref importedGroupCount);
                            }
                        }
                    }));
                }

                await UniTask.WhenAll(tasks);

                for (int i = 0; i < entitiesAtDepth.Count; i++)
                {
                    var (flatEntity, originalIndex) = entitiesAtDepth[i];
                    var entityId = entityIds[i];

                    int parentId = -1;
                    if (flatEntity.ParentFlatIndex >= 0 && createdEntityIds.TryGetValue(flatEntity.ParentFlatIndex, out var mappedParentId))
                    {
                        parentId = mappedParentId;
                    }

                    RegisterEntityToHierarchy(entityId, flatEntity.Data, parentId);
                }
            }

            result.ImportedEntityCount = importedEntityCount;
            result.ImportedGroupCount = importedGroupCount;
        }

        private void ImportEntitiesSync(List<EntityData> entities, ImportResult result)
        {
            var flattenedEntities = new List<FlattenedEntity>();
            FlattenEntityTree(entities, flattenedEntities, 0, -1);

            if (flattenedEntities.Count == 0)
            {
                return;
            }

            int importedEntityCount = 0;
            int importedGroupCount = 0;

            var indexedEntities = flattenedEntities.Select((e, idx) => (Entity: e, OriginalIndex: idx)).ToList();

            var entitiesByDepth = indexedEntities
                .GroupBy(e => e.Entity.Depth)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.ToList());

            var createdEntityIds = new Dictionary<int, int>();

            foreach (var kvp in entitiesByDepth.OrderBy(k => k.Key))
            {
                var entitiesAtDepth = kvp.Value;
                var entityIds = _ecsWorld.CreateEntities(entitiesAtDepth.Count);

                for (int i = 0; i < entitiesAtDepth.Count; i++)
                {
                    var (flatEntity, originalIndex) = entitiesAtDepth[i];
                    var entityId = entityIds[i];

                    InitializeEntityComponents(entityId, flatEntity.Data);
                    createdEntityIds[originalIndex] = entityId;

                    importedEntityCount++;
                    if (flatEntity.Data.IsGroup)
                    {
                        importedGroupCount++;
                    }
                }

                for (int i = 0; i < entitiesAtDepth.Count; i++)
                {
                    var (flatEntity, originalIndex) = entitiesAtDepth[i];
                    var entityId = entityIds[i];

                    int parentId = -1;
                    if (flatEntity.ParentFlatIndex >= 0 && createdEntityIds.TryGetValue(flatEntity.ParentFlatIndex, out var mappedParentId))
                    {
                        parentId = mappedParentId;
                    }

                    RegisterEntityToHierarchy(entityId, flatEntity.Data, parentId);
                }
            }

            result.ImportedEntityCount = importedEntityCount;
            result.ImportedGroupCount = importedGroupCount;
        }

        private void FlattenEntityTree(List<EntityData> entities, List<FlattenedEntity> result, int depth, int parentFlatIndex)
        {
            if (entities == null)
            {
                return;
            }

            foreach (var entity in entities)
            {
                int currentIndex = result.Count;
                result.Add(new FlattenedEntity
                {
                    Data = entity,
                    Depth = depth,
                    ParentFlatIndex = parentFlatIndex
                });

                if (entity.Children != null && entity.Children.Count > 0)
                {
                    FlattenEntityTree(entity.Children, result, depth + 1, currentIndex);
                }
            }
        }

        private void InitializeEntityComponents(int entityId, EntityData entityData)
        {
            var transform = _ecsWorld.AddComponent<Transform>(entityId);
            if (entityData.Transform != null)
            {
                var td = entityData.Transform;
                transform.Position = new Position
                {
                    X = td.Position?.X ?? 0,
                    Y = td.Position?.Y ?? 0,
                    Z = td.Position?.Z ?? 0
                };
                transform.Rotation = new Rotation
                {
                    X = td.Rotation?.X ?? 0,
                    Y = td.Rotation?.Y ?? 0,
                    Z = td.Rotation?.Z ?? 0,
                    W = td.Rotation?.W ?? 1
                };
                transform.Scale = new Scale
                {
                    X = td.Scale?.X ?? 1,
                    Y = td.Scale?.Y ?? 1,
                    Z = td.Scale?.Z ?? 1
                };
                transform.Index = new RailIndex { Index = td.RailIndex };
                transform.Radius = td.Radius > 0 ? td.Radius : 7.5f;
            }
            else
            {
                transform.Position = new Position { X = 0, Y = 0, Z = 0 };
                transform.Rotation = new Rotation { X = 0, Y = 0, Z = 0, W = 1 };
                transform.Scale = HierarchyService.GetDefaultScaleForNoteType(entityData.NoteType);
                transform.Index = new RailIndex { Index = 0 };
            }
            transform.Parents = -1;

            var meshRenderer = _ecsWorld.AddComponent<MeshRenderer>(entityId);
            if (entityData.MeshRenderer != null)
            {
                var md = entityData.MeshRenderer;
                meshRenderer.Type = md.Type;
                meshRenderer.Color = new Vector4(
                    md.Color?.X ?? 1,
                    md.Color?.Y ?? 1,
                    md.Color?.Z ?? 1,
                    md.Color?.W ?? 1);
                meshRenderer.Visibility = md.Visibility;
                meshRenderer.SortingLayer = md.SortingLayer;
                meshRenderer.OrderInLayer = md.OrderInLayer;
            }
            else
            {
                meshRenderer.Type = entityData.NoteType;
                meshRenderer.Color = new Vector4(1, 1, 1, 1);
                meshRenderer.Visibility = true;
            }

            if (_textureService != null)
            {
                meshRenderer.TextureIndex = _textureService.GetTextureIndexForNoteType(meshRenderer.Type);
            }

            if (entityData.Timing != null)
            {
                var timing = _ecsWorld.AddComponent<Timing>(entityId);
                timing.TargetMin = entityData.Timing.TargetMin;
                timing.TargetSecond = entityData.Timing.TargetSecond;
                timing.TargetTick = entityData.Timing.TargetTick;
            }
            else if (entityData.IsGroup)
            {
                _ecsWorld.AddComponent<Timing>(entityId);
            }

            if (entityData.Description != null)
            {
                var description = _ecsWorld.AddComponent<Description>(entityId);
                description.Text = entityData.Description.Text;
            }
            else if (entityData.IsGroup)
            {
                _ecsWorld.AddComponent<Description>(entityId);
            }

            if (entityData.VelocityCurve?.KeyFrames != null && entityData.VelocityCurve.KeyFrames.Count > 0)
            {
                var velocityCurve = _ecsWorld.AddComponent<VelocityCurve>(entityId);
                var frames = new KeyFrame[entityData.VelocityCurve.KeyFrames.Count];

                for (int i = 0; i < entityData.VelocityCurve.KeyFrames.Count; i++)
                {
                    var kf = entityData.VelocityCurve.KeyFrames[i];
                    frames[i] = new KeyFrame
                    {
                        Index = kf.Index,
                        Time = kf.Time,
                        Value = kf.Value,
                        InterpolationType = kf.InterpolationType,
                        ControlPoints = kf.ControlPoints != null
                            ? kf.ControlPoints.Select(cp => new ControlPoint(cp.X, cp.Y)).ToArray()
                            : Array.Empty<ControlPoint>()
                    };
                }

                velocityCurve.Curve = new KeyFrames { Frames = frames };
            }
        }

        private void RegisterEntityToHierarchy(int entityId, EntityData entityData, int parentId)
        {
            var node = new HierarchyNode
            {
                EntityId = entityId,
                Name = entityData.Name ?? $"Entity_{entityId}",
                IsGroup = entityData.IsGroup,
                IsExpanded = true,
                IsVisible = entityData.MeshRenderer?.Visibility ?? true,
                NoteType = entityData.NoteType
            };

            _hierarchyService.NodeMapping[entityId] = node;

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform != null)
            {
                transform.Parents = parentId;

                if (parentId == -1)
                {
                    _hierarchyService.Roots.Add(entityId);
                }
                else
                {
                    var parentTransform = _ecsWorld.GetComponent<Transform>(parentId);
                    parentTransform?.Children.Add(entityId);
                }
            }
        }

        private sealed class FlattenedEntity
        {
            public EntityData Data { get; set; }
            public int Depth { get; set; }
            public int ParentFlatIndex { get; set; }
        }
    }

    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int ImportedEntityCount { get; set; }
        public int ImportedGroupCount { get; set; }
    }
}