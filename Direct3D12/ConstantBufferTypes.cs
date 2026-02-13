using System.Numerics;
using System.Runtime.InteropServices;

namespace FluentDesigner.Direct3D12
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectConstants
    {
        public Matrix4x4 World;
        public Vector4 Color;
        public int TextureIndex;
        public int Padding0, Padding1, Padding2;
        public static int SizeInBytes => 256;
        public static ObjectConstants Default => new ObjectConstants
        {
            World = Matrix4x4.Identity,
            Color = new Vector4(1f, 1f, 1f, 1f),
            TextureIndex = -1,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PassConstants
    {
        public Matrix4x4 View;
        public Matrix4x4 Proj;
        public Matrix4x4 ViewProj;
        public Vector3 CamPos;
        public float Padding0;
        public Vector4 Time;
        public Vector4 ScreenParams;
        public static int SizeInBytes => 256;
        public static PassConstants Default => new PassConstants
        {
            View = Matrix4x4.Identity,
            Proj = Matrix4x4.Identity,
            ViewProj = Matrix4x4.Identity,
            CamPos = Vector3.Zero,
            Time = Vector4.Zero,
            ScreenParams = new Vector4(1920, 1080, 1f / 1920f, 1f / 1080f)
        };
    }
}