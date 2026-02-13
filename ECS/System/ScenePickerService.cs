using FluentDesigner.ECS.Components;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace FluentDesigner.ECS.System
{
    public sealed class ScenePickerService : Service, IDisposable
    {
        private CameraService _cameraService;
        private EcsWorldService _ecsWorld;
        private HierarchyService _hierarchyService;

        private float _viewportWidth = 1920f;
        private float _viewportHeight = 1080f;
        private float _fovY = MathF.PI / 4f;
        private float _nearZ = 0.1f;
        private float _farZ = 1024f;

        public override void Initialize()
        {
            base.Initialize();
            _cameraService = GetFramework().GetService<CameraService>();
            _ecsWorld = GetFramework().GetService<EcsWorldService>();
            _hierarchyService = GetFramework().GetService<HierarchyService>();
        }

        public void UpdateViewport(float width, float height)
        {
            _viewportWidth = MathF.Max(1f, width);
            _viewportHeight = MathF.Max(1f, height);
        }

        public void UpdateProjection(float fovY, float nearZ = 0.1f, float farZ = 1024f)
        {
            _fovY = fovY;
            _nearZ = nearZ;
            _farZ = farZ;
        }

        public Ray CreateRayFromScreen(float screenX, float screenY)
        {
            float ndcX = (2f * screenX / _viewportWidth) - 1f;
            float ndcY = 1f - (2f * screenY / _viewportHeight);

            var cameraPos = _cameraService.Position;
            var view = _cameraService.GetViewMatrix();
            var aspectRatio = _viewportWidth / _viewportHeight;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(_fovY, aspectRatio, _nearZ, _farZ);

            var viewProj = view * proj;
            if (!Matrix4x4.Invert(viewProj, out var invViewProj))
            {
                return new Ray(cameraPos, Vector3.UnitZ);
            }

            var nearPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), invViewProj);
            var farPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), invViewProj);

            var nearWorld = new Vector3(nearPoint.X, nearPoint.Y, nearPoint.Z) / nearPoint.W;
            var farWorld = new Vector3(farPoint.X, farPoint.Y, farPoint.Z) / farPoint.W;

            var direction = Vector3.Normalize(farWorld - nearWorld);
            return new Ray(nearWorld, direction);
        }

        public int Raycast(float screenX, float screenY)
        {
            var ray = CreateRayFromScreen(screenX, screenY);
            return RaycastEntities(ray);
        }

        private int RaycastEntities(Ray ray)
        {
            var hits = RaycastAllEntities(ray);
            if (hits.Count == 0)
            {
                return -1;
            }

            hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return hits[0].EntityId;
        }

        private List<RaycastHit> RaycastAllEntities(Ray ray)
        {
            var hits = new List<RaycastHit>();
            var transPool = _ecsWorld.GetPool<Transform>();
            var rendererPool = _ecsWorld.GetPool<MeshRenderer>();

            foreach (var id in transPool.GetEntities())
            {
                if (!rendererPool.Contains(id))
                {
                    continue;
                }

                var trans = transPool.Get(id);
                var renderer = rendererPool.Get(id);

                if (trans == null || renderer == null || !renderer.Visibility)
                {
                    continue;
                }

                if (_hierarchyService.NodeMapping.TryGetValue(id, out var node) && node.IsGroup)
                {
                    continue;
                }

                var bounds = CalculateWorldBounds(trans);

                if (RayIntersectsAABB(ray, bounds, out float distance))
                {
                    hits.Add(new RaycastHit
                    {
                        EntityId = id,
                        Distance = distance,
                        Point = ray.Origin + ray.Direction * distance
                    });
                }
            }

            return hits;
        }

        private AABB CalculateWorldBounds(Transform trans)
        {
            var pos = new Vector3(trans.Position.X, trans.Position.Y, trans.Position.Z);
            var scale = new Vector3(trans.Scale.X, trans.Scale.Y, trans.Scale.Z);
            var halfExtents = scale * 0.5f;

            return new AABB
            {
                Min = pos - halfExtents,
                Max = pos + halfExtents
            };
        }

        private static bool RayIntersectsAABB(Ray ray, AABB bounds, out float distance)
        {
            distance = 0f;

            float tMin = float.NegativeInfinity;
            float tMax = float.PositiveInfinity;

            if (MathF.Abs(ray.Direction.X) < float.Epsilon)
            {
                if (ray.Origin.X < bounds.Min.X || ray.Origin.X > bounds.Max.X)
                {
                    return false;
                }
            }
            else
            {
                float invD = 1f / ray.Direction.X;
                float t1 = (bounds.Min.X - ray.Origin.X) * invD;
                float t2 = (bounds.Max.X - ray.Origin.X) * invD;
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                }

                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax)
                {
                    return false;
                }
            }

            if (MathF.Abs(ray.Direction.Y) < float.Epsilon)
            {
                if (ray.Origin.Y < bounds.Min.Y || ray.Origin.Y > bounds.Max.Y)
                {
                    return false;
                }
            }
            else
            {
                float invD = 1f / ray.Direction.Y;
                float t1 = (bounds.Min.Y - ray.Origin.Y) * invD;
                float t2 = (bounds.Max.Y - ray.Origin.Y) * invD;
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                }

                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax)
                {
                    return false;
                }
            }

            if (MathF.Abs(ray.Direction.Z) < float.Epsilon)
            {
                if (ray.Origin.Z < bounds.Min.Z || ray.Origin.Z > bounds.Max.Z)
                {
                    return false;
                }
            }
            else
            {
                float invD = 1f / ray.Direction.Z;
                float t1 = (bounds.Min.Z - ray.Origin.Z) * invD;
                float t2 = (bounds.Max.Z - ray.Origin.Z) * invD;
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                }

                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax)
                {
                    return false;
                }
            }

            distance = tMin >= 0 ? tMin : tMax;
            return distance >= 0;
        }

        public List<int> RectangleSelect(float startX, float startY, float endX, float endY)
        {
            float minX = MathF.Min(startX, endX);
            float maxX = MathF.Max(startX, endX);
            float minY = MathF.Min(startY, endY);
            float maxY = MathF.Max(startY, endY);

            var selectedIds = new List<int>();
            var transPool = _ecsWorld.GetPool<Transform>();
            var rendererPool = _ecsWorld.GetPool<MeshRenderer>();

            var view = _cameraService.GetViewMatrix();
            var aspectRatio = _viewportWidth / _viewportHeight;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(_fovY, aspectRatio, _nearZ, _farZ);
            var viewProj = view * proj;

            foreach (var id in transPool.GetEntities())
            {
                if (!rendererPool.Contains(id))
                {
                    continue;
                }

                var trans = transPool.Get(id);
                var renderer = rendererPool.Get(id);

                if (trans == null || renderer == null || !renderer.Visibility)
                {
                    continue;
                }

                if (_hierarchyService.NodeMapping.TryGetValue(id, out var node) && node.IsGroup)
                {
                    continue;
                }

                var worldPos = new Vector3(trans.Position.X, trans.Position.Y, trans.Position.Z);
                var screenPos = WorldToScreen(worldPos, viewProj);

                if (screenPos.Z > 0 && screenPos.Z < 1 &&
                    screenPos.X >= minX && screenPos.X <= maxX &&
                    screenPos.Y >= minY && screenPos.Y <= maxY)
                {
                    selectedIds.Add(id);
                }
            }

            return selectedIds;
        }

        private Vector3 WorldToScreen(Vector3 worldPos, Matrix4x4 viewProj)
        {
            var clipPos = Vector4.Transform(new Vector4(worldPos, 1f), viewProj);
            if (MathF.Abs(clipPos.W) < float.Epsilon)
            {
                return new Vector3(-1, -1, -1);
            }

            var ndc = new Vector3(clipPos.X, clipPos.Y, clipPos.Z) / clipPos.W;
            float screenX = (ndc.X + 1f) * 0.5f * _viewportWidth;
            float screenY = (1f - ndc.Y) * 0.5f * _viewportHeight;
            float depth = (ndc.Z + 1f) * 0.5f;

            return new Vector3(screenX, screenY, depth);
        }

        public void ClickSelect(float screenX, float screenY, bool additive = false, bool toggle = false)
        {
            int hitId = Raycast(screenX, screenY);

            if (hitId == -1)
            {
                if (!additive && !toggle)
                {
                    ClearAllSelection();
                }

                return;
            }

            if (toggle)
            {
                if (_hierarchyService.SelectedEntitiesId.Contains(hitId))
                {
                    _hierarchyService.RemoveSelect(hitId);
                }
                else
                {
                    _hierarchyService.AddSelect(hitId);
                }
            }
            else if (additive)
            {
                _hierarchyService.AddSelect(hitId);
            }
            else
            {
                var currentSelection = new List<int>(_hierarchyService.SelectedEntitiesId);
                foreach (var id in currentSelection)
                {
                    _hierarchyService.RemoveSelect(id);
                }

                _hierarchyService.AddSelect(hitId);
            }
        }

        public void BoxSelect(float startX, float startY, float endX, float endY, bool additive = false)
        {
            var selectedIds = RectangleSelect(startX, startY, endX, endY);
            if (!additive)
            {
                var currentSelection = new List<int>(_hierarchyService.SelectedEntitiesId);
                foreach (var id in currentSelection)
                {
                    _hierarchyService.RemoveSelect(id);
                }
            }
            foreach (var id in selectedIds)
            {
                _hierarchyService.AddSelect(id);
            }
        }

        private void ClearAllSelection()
        {
            var currentSelection = new List<int>(_hierarchyService.SelectedEntitiesId);
            foreach (var id in currentSelection)
            {
                _hierarchyService.RemoveSelect(id);
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();
        }

        public void Dispose() => Shutdown();
    }

    public readonly struct Ray
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;

        public Ray(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            Direction = direction;
        }
    }

    public struct AABB
    {
        public Vector3 Min;
        public Vector3 Max;
    }

    public struct RaycastHit
    {
        public int EntityId;
        public float Distance;
        public Vector3 Point;
    }
}
