using FluentDesigner.Direct3D12;
using System;
using System.Runtime.CompilerServices;
using Vortice.Direct3D12;

namespace FluentDesigner.ECS.System
{
    public sealed class ConstantBufferService : Service, IDisposable
    {
        private const int BufferAlignment = 256;
        private const int FrameCount = 2;
        private const int DefaultMaxObjects = 1024;

        private ID3D12Device _device;
        private ID3D12Resource _uploadBuffer;
        private IntPtr _mappedData;
        private int _objectCBSize;
        private int _passCBSize;
        private int _maxObjects;
        private ulong _bufferSize;

        public ulong GpuAddress;
        public bool IsInitialized => _uploadBuffer != null;
        public int MaxObjects => _maxObjects;

        public override void Initialize()
        {
            base.Initialize();

            var renderService = GetFramework().GetService<RenderService>();
            if (renderService?.Device == null)
            {
                throw new InvalidOperationException("RenderService is not initialized.");
            }

            _device = renderService.Device;
            _maxObjects = DefaultMaxObjects;

            CreateUploadBuffer();
        }

        public void ReInitalize(int maxCount)
        {
            if (maxCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCount), "Max count must be greater than zero.");
            }

            DisposeBuffer();
            _maxObjects = maxCount;
            CreateUploadBuffer();
        }

        private void CreateUploadBuffer()
        {
            _objectCBSize = AlignUp(ObjectConstants.SizeInBytes, BufferAlignment);
            _passCBSize = AlignUp(PassConstants.SizeInBytes, BufferAlignment);
            _bufferSize = (ulong)((_passCBSize + _objectCBSize * _maxObjects) * FrameCount);

            var heap = new HeapProperties(HeapType.Upload);
            var buffer = ResourceDescription.Buffer(_bufferSize);

            var hr = _device.CreateCommittedResource(
                heap,
                HeapFlags.None,
                buffer,
                ResourceStates.GenericRead,
                null,
                out _uploadBuffer);

            if (hr.Failure)
            {
                throw new Exception($"Failed to create constant buffer upload resource. HRESULT: {hr.Code}");
            }

            unsafe
            {
                void* pData;
                hr = _uploadBuffer.Map(0, null, &pData);
                if (hr.Failure)
                {
                    throw new Exception($"Failed to map constant buffer upload resource. HRESULT: {hr.Code}");
                }

                _mappedData = (IntPtr)pData;
            }

            GpuAddress = _uploadBuffer.GPUVirtualAddress;
        }

        public ulong GetPassCBAddress(int index)
        {
            int offset = (_passCBSize + _objectCBSize * _maxObjects) * index;
            return GpuAddress + (ulong)offset;
        }

        public ulong GetObjectCBAddress(int index, int objId)
        {
            if (objId < 0 || objId >= _maxObjects)
            {
                throw new ArgumentOutOfRangeException(nameof(objId), "Object ID is out of range.");
            }

            int fOffset = (_passCBSize + _objectCBSize * _maxObjects) * index;
            int oOffset = _passCBSize + (_objectCBSize * objId);
            return GpuAddress + (ulong)(fOffset + oOffset);
        }

        public void UpdataPassConstant(int frame, in PassConstants data)
        {
            if (!IsInitialized)
            {
                return;
            }

            int frameOffset = (_passCBSize + _objectCBSize * _maxObjects) * frame;
            IntPtr dest = _mappedData + frameOffset;

            unsafe
            {
                Unsafe.CopyBlock((void*)dest, Unsafe.AsPointer(ref Unsafe.AsRef(in data)), (uint)PassConstants.SizeInBytes);
            }
        }

        public void UpdateObjectConstant(int frame, int objId, in ObjectConstants data)
        {
            if (!IsInitialized)
            {
                return;
            }

            if (objId < 0 || objId >= _maxObjects)
            {
                throw new ArgumentOutOfRangeException(nameof(objId), "Object ID is out of range.");
            }

            int frameOffset = (_passCBSize + _objectCBSize * _maxObjects) * frame;
            int objectOffset = _passCBSize + (_objectCBSize * objId);
            IntPtr dest = _mappedData + frameOffset + objectOffset;
            unsafe
            {
                Unsafe.CopyBlock((void*)dest, Unsafe.AsPointer(ref Unsafe.AsRef(in data)), (uint)ObjectConstants.SizeInBytes);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AlignUp(int value, int align) => (value + align - 1) & ~(align - 1);

        private void DisposeBuffer()
        {
            if (_uploadBuffer != null)
            {
                _uploadBuffer.Unmap(0);
                _uploadBuffer.Dispose();
                _uploadBuffer = null;
            }
            _mappedData = IntPtr.Zero;
        }

        public override void Shutdown()
        {
            DisposeBuffer();
            base.Shutdown();
        }

        public void Dispose()
        {
            Shutdown();
        }
    }
}