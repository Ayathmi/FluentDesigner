using FluentDesigner.ECS.Components;
using System;

namespace FluentDesigner.ECS.System
{
    public sealed class SceneLayoutService : Service, IDisposable
    {
        public const float DefaultRadius = 7.5f;
        public const float MinRadius = 0.1f;
        public const float MaxRadius = 7.5f;
        private const float UnitsPerSecond = 10f;
        private const int RotateModeIndex = 360;

        private EcsWorldService _ecsWorld;
        private HierarchyService _hierarchyService;

        public override void Initialize()
        {
            base.Initialize();
            _ecsWorld = GetFramework().GetService<EcsWorldService>();
            _hierarchyService = GetFramework().GetService<HierarchyService>();

            _hierarchyService.HierarchyChanged += OnHierarchyChanged;
        }

        private void OnHierarchyChanged(object sender, HierarchyChangedEventArgs e)
        {
            if (e.ChangedType == HierarchyChangedType.Added)
            {
                EnsureTimingComponent(e.EntityId);
                UpdateNoteTransform(e.EntityId);
            }
            else if (e.ChangedType == HierarchyChangedType.PropertyChanged)
            {
                UpdateNoteTransform(e.EntityId);
            }
        }

        private void EnsureTimingComponent(int entityId)
        {
            if (_hierarchyService.NodeMapping.TryGetValue(entityId, out var node) && node.IsGroup)
            {
                return;
            }

            var timing = _ecsWorld.GetComponent<Timing>(entityId);
            if (timing == null)
            {
                _ecsWorld.AddComponent<Timing>(entityId);
            }
        }

        public void UpdateNoteTransform(int entityId)
        {
            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            var timing = _ecsWorld.GetComponent<Timing>(entityId);
            var meshRenderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);

            if (transform == null)
            {
                return;
            }

            float z = 0f;
            if (timing != null)
            {
                float targetSeconds = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;
                z = targetSeconds * UnitsPerSecond;
            }

            int railIndex = transform.Index.Index;

            if (meshRenderer != null)
            {
                bool isRotateType = meshRenderer.Type is NoteTypeEnum.RotateL or NoteTypeEnum.RotateR;

                if (isRotateType && railIndex != RotateModeIndex)
                {
                    transform.Index = new RailIndex { Index = RotateModeIndex };
                    railIndex = RotateModeIndex;
                }
                else if (!isRotateType && railIndex == RotateModeIndex)
                {
                    transform.Index = new RailIndex { Index = 0 };
                    railIndex = 0;
                }
            }

            if (railIndex == RotateModeIndex)
            {
                transform.Position = new Position { X = 0f, Y = 0f, Z = z };
                transform.Rotation = new Rotation { X = 0f, Y = 0f, Z = 0f, W = 1f };
            }
            else
            {
                float radius = Math.Clamp(transform.Radius, MinRadius, MaxRadius);
                float angleRad = railIndex * MathF.PI / 180f;
                float x = radius * MathF.Cos(angleRad);
                float y = radius * MathF.Sin(angleRad);
                transform.Position = new Position { X = x, Y = y, Z = z };
                float halfAngle = angleRad / 2f;
                float sinHalf = MathF.Sin(halfAngle);
                float cosHalf = MathF.Cos(halfAngle);
                transform.Rotation = new Rotation
                {
                    X = 0f,
                    Y = 0f,
                    Z = sinHalf,
                    W = cosHalf
                };
            }

            if (meshRenderer != null)
            {
                bool isRotateType = meshRenderer.Type == NoteTypeEnum.RotateL ||
                                    meshRenderer.Type == NoteTypeEnum.RotateR;

                if (isRotateType && railIndex != RotateModeIndex)
                {
                    transform.Index = new RailIndex { Index = RotateModeIndex };
                    UpdateNoteTransform(entityId);
                }
            }
        }

        public void UpdateAllNotes()
        {
            var transformPool = _ecsWorld.GetPool<Transform>();

            foreach (var entityId in transformPool.GetEntities())
            {
                if (_hierarchyService.NodeMapping.TryGetValue(entityId, out var node) && node.IsGroup)
                {
                    continue;
                }

                UpdateNoteTransform(entityId);
            }
        }

        public void OnTimingUpdated(int entityId)
        {
            UpdateNoteTransform(entityId);
        }

        public void OnRailIndexUpdated(int entityId)
        {
            UpdateNoteTransform(entityId);
        }
        public void OnRadiusUpdated(int entityId)
        {
            UpdateNoteTransform(entityId);
        }

        public void OnNoteTypeChanged(int entityId, NoteTypeEnum newType)
        {
            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null)
            {
                return;
            }

            bool isRotateType = newType == NoteTypeEnum.RotateL || newType == NoteTypeEnum.RotateR;

            if (isRotateType)
            {
                transform.Index = new RailIndex { Index = RotateModeIndex };
            }
            else if (transform.Index.Index == RotateModeIndex)
            {
                transform.Index = new RailIndex { Index = 0 };
            }

            UpdateNoteTransform(entityId);
        }
        public static float SecondsToZ(float seconds) => seconds * UnitsPerSecond;
        public static float ZToSeconds(float z) => z / UnitsPerSecond;

        public static (float x, float y) GetCylinderPosition(int railIndex)
        {
            return GetCylinderPosition(railIndex, DefaultRadius);
        }

        public static (float x, float y) GetCylinderPosition(int railIndex, float radius)
        {
            if (railIndex == RotateModeIndex)
            {
                return (0f, 0f);
            }

            float angleRad = railIndex * MathF.PI / 180f;
            return (radius * MathF.Cos(angleRad), radius * MathF.Sin(angleRad));
        }

        public override void Shutdown()
        {
            if (_hierarchyService != null)
            {
                _hierarchyService.HierarchyChanged -= OnHierarchyChanged;
            }

            base.Shutdown();
        }

        public void Dispose() => Shutdown();
    }
}