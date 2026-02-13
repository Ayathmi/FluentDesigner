using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vortice.Direct3D12;

namespace FluentDesigner.ECS.System
{
    public struct InstanceData
    {
        public Matrix4x4 World;
        public Vector4 Color;
        public int TextureIndex;
        public int Padding0, Padding1, Padding2;

        public static int SizeInBytes => 96;
    }

    public sealed class InstanceBufferService : Service, IDisposable
    {
        private const int FrameCount = 2;
        private const int DefaultMaxInstances = 4096;

        private ID3D12Device _device;
        private ID3D12Resource _instanceBuffer;
        private IntPtr _mappedData;
        private int _maxInstances;
        private int _currentInstanceCount;

        private readonly int[] _frameInstanceCount = new int[FrameCount];
        private int _currentFrameIndex = -1;
        public ulong GpuAddress;
        public int MaxInstances => _maxInstances;
        public int CurrentInstanceCount => _currentInstanceCount;

        public override void Initialize()
        {
            base.Initialize();

            var renderService = GetFramework().GetService<RenderService>();
            if (renderService?.Device == null)
            {
                throw new InvalidOperationException("RenderService must be initialized first.");
            }

            _device = renderService.Device;
            _maxInstances = DefaultMaxInstances;

            CreateInstanceBuffer();
        }

        private void CreateInstanceBuffer()
        {
            ulong bufferSize = (ulong)(InstanceData.SizeInBytes * _maxInstances * FrameCount);
            var heap = new HeapProperties(HeapType.Upload);
            var buffer = ResourceDescription.Buffer(bufferSize);

            var hr = _device.CreateCommittedResource(
                heap,
                HeapFlags.None,
                buffer,
                ResourceStates.GenericRead,
                null,
                out _instanceBuffer);

            if (hr.Failure)
            {
                throw new Exception("Failed to create instance buffer.");
            }

            unsafe
            {
                void* pData;
                hr = _instanceBuffer.Map(0, null, &pData);
                if (hr.Failure || pData == null)
                {
                    throw new Exception("Failed to map instance buffer.");
                }

                _mappedData = (IntPtr)pData;
            }

            GpuAddress = _instanceBuffer.GPUVirtualAddress;
        }

        public void BeginFrame(int index)
        {
            _currentFrameIndex = index;
            _frameInstanceCount[index] = 0;
        }

        public bool AddInstance(int frameId, in InstanceData data)
        {
            int currentCount = _frameInstanceCount[frameId];
            if (currentCount >= _maxInstances)
            {
                return false;
            }

            int offset = (frameId * _maxInstances + currentCount) * InstanceData.SizeInBytes;
            IntPtr dest = _mappedData + offset;

            unsafe
            {
                Unsafe.CopyBlock((void*)dest, Unsafe.AsPointer(ref Unsafe.AsRef(in data)), (uint)InstanceData.SizeInBytes);
            }

            _frameInstanceCount[frameId] = currentCount + 1;
            return true;
        }

        public ulong GetFrameBufferAddress(int frameId) => GpuAddress + (ulong)(frameId * _maxInstances * InstanceData.SizeInBytes);
        public int GetFrameInstanceCount(int frameId) => _frameInstanceCount[frameId];

        private void DisposeBuffer()
        {
            if (_instanceBuffer != null)
            {
                _instanceBuffer.Unmap(0, null);
                _instanceBuffer.Dispose();
                _instanceBuffer = null;
            }
            _mappedData = IntPtr.Zero;
        }

        public override void Shutdown()
        {
            DisposeBuffer();
            base.Shutdown();
        }

        public void Dispose() => Shutdown();
    }
}