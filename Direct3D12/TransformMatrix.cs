using FluentDesigner.ECS.Components;
using System.Numerics;

namespace FluentDesigner.Direct3D12
{
    public static class TransformMatrix
    {
        public static Matrix4x4 GetWorldMatrix(Transform trans)
        {
            var scale = Matrix4x4.CreateScale(trans.Scale.X, trans.Scale.Y, trans.Scale.Z);
            var rot = Matrix4x4.CreateFromQuaternion(new Quaternion(trans.Rotation.X, trans.Rotation.Y, trans.Rotation.Z, trans.Rotation.W));
            var translation = Matrix4x4.CreateTranslation(trans.Position.X, trans.Position.Y, trans.Position.Z);

            return scale * rot * translation;
        }

        public static Matrix4x4 GetOrthographihcProjection(float width, float height, float nearZ = 0.1f, float farZ = 128f)
        {
            return Matrix4x4.CreateOrthographic(width, height, nearZ, farZ);
        }

        public static Matrix4x4 CreatePerspectiveProjection(float fovY, float ratio, float nearZ = 0.1f, float farZ = 128f)
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(fovY, ratio, nearZ, farZ);
        }

        public static Matrix4x4 CreateLookAtMatrix(Vector3 eye, Vector3 target, Vector3 up)
        {
            return Matrix4x4.CreateLookAt(eye, target, up);
        }
    }
}