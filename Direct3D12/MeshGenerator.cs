using FluentDesigner.ECS.Components;
using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FluentDesigner.Direct3D12
{
    public struct Vertex
    {
        public Vector3 Position;
        public Vector2 UV;
        public static int SizeInBytes => 20;
    }

    public class MeshData : IDisposable
    {
        public ID3D12Resource VBuffer;
        public VertexBufferView VBufferView;
        public ID3D12Resource IBuffer;
        public IndexBufferView IBufferView;
        public int IndexCount;

        public void Dispose()
        {
            VBuffer?.Dispose();
            IBuffer?.Dispose();
        }
    }

    public static class MeshGenerator
    {
        public static MeshData CreateNoteMesh(ID3D12Device device, NoteTypeEnum type)
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            const float halfW = 0.5f;
            const float halfH = 0.5f;

            var vertices = new Vertex[]
            {
            new Vertex { Position = new Vector3(-halfW,  halfH, 0f), UV = new Vector2(0f, 0f) },
            new Vertex { Position = new Vector3( halfW,  halfH, 0f), UV = new Vector2(1f, 0f) },
            new Vertex { Position = new Vector3( halfW, -halfH, 0f), UV = new Vector2(1f, 1f) },
            new Vertex { Position = new Vector3(-halfW, -halfH, 0f), UV = new Vector2(0f, 1f) },
            };

            var indices = new ushort[]
            {
            0, 1, 2,
            0, 2, 3
            };

            var meshData = new MeshData();
            meshData.IndexCount = indices.Length;
            var vBufferSize = vertices.Length * Vertex.SizeInBytes;
            var upload = new HeapProperties(HeapType.Upload);
            var res = ResourceDescription.Buffer((ulong)vBufferSize);

            var hr = device.CreateCommittedResource(
                upload,
                HeapFlags.None,
                res,
                ResourceStates.GenericRead,
                null,
                out meshData.VBuffer);

            if (hr.Failure)
            {
                throw new Exception("Failed to create vertex buffer resource: " + hr.Code);
            }

            unsafe
            {
                void* vPtr;
                hr = meshData.VBuffer.Map(0, null, &vPtr);

                if (hr.Failure || vPtr == null)
                {
                    throw new Exception("Failed to map vertex buffer resource.");
                }

                var destSpan = new Span<Vertex>(vPtr, vertices.Length);
                vertices.CopyTo(destSpan);
                meshData.VBuffer.Unmap(0, null);
            }

            meshData.VBufferView = new VertexBufferView(
                meshData.VBuffer.GPUVirtualAddress,
                (uint)vBufferSize,
                (uint)Vertex.SizeInBytes);

            int iBufferSize = indices.Length * sizeof(ushort);
            var iBufferDesc = ResourceDescription.Buffer((ulong)iBufferSize);

            hr = device.CreateCommittedResource(
                upload,
                HeapFlags.None,
                iBufferDesc,
                ResourceStates.GenericRead,
                null,
                out meshData.IBuffer);

            if (hr.Failure)
            {
                throw new Exception($"Failed to create index buffer resource: {hr.Code}");
            }

            unsafe
            {
                void* iPtr;
                hr = meshData.IBuffer.Map(0, null, &iPtr);

                if (iPtr == null || hr.Failure)
                {
                    throw new Exception("Failed to map index buffer resource.");
                }

                var destSpan = new Span<ushort>(iPtr, indices.Length);
                indices.CopyTo(destSpan);
            }

            meshData.IBuffer.Unmap(0, null);

            meshData.IBufferView = new IndexBufferView(
                meshData.IBuffer.GPUVirtualAddress,
                (uint)iBufferSize,
                Format.R16_UInt);

            return meshData;
        }
    }
}