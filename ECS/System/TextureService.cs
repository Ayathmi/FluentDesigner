using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using StbImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FluentDesigner.ECS.System
{
    public sealed class TextureData : IDisposable
    {
        public ID3D12Resource Resource { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Format Format { get; set; }
        public int SrvIndex { get; set; } = -1;

        public void Dispose()
        {
            Resource?.Release();
            Resource = null;
        }
    }

    public sealed class TextureService : Service, IDisposable
    {
        private const int MaxTextures = 256;
        private const int SrvHeapSize = 256;

        private RenderService _renderService;
        private ID3D12Device _device;
        private ID3D12CommandQueue _commandQueue;
        private readonly Dictionary<string, int> _textureNameToIndex = new();
        private readonly Dictionary<NoteTypeEnum, int> _noteTextureMap = new();
        private readonly TextureData[] _textures = new TextureData[MaxTextures];
        private int _textureCount;
        private ID3D12DescriptorHeap _srvHeap;
        private uint _srvDescriptorSize;
        private readonly List<ID3D12Resource> _pendingUploads = new();
        private readonly Lock _uploadLock = new();
        private ID3D12CommandAllocator _allocator;
        private ID3D12GraphicsCommandList _commandList;
        private ID3D12Fence _uploadFence;
        private ulong _uploadFenceValue;
        private AutoResetEvent _fenceEvent;

        public ID3D12DescriptorHeap SrvHeap => _srvHeap;
        public int TextureCount => _textureCount;

        public override void Initialize()
        {
            base.Initialize();
            _renderService = GetFramework().GetService<RenderService>();
            if (_renderService?.Device == null)
            {
                throw new InvalidOperationException("RenderService is not initialized.");
            }

            _device = _renderService.Device;
            _commandQueue = _renderService.CommandQueue;

            CreateSrvHeap();
            CreateUploadResources();
            LoadNoteTextures();
        }

        private void LoadNoteTextures()
        {
            var fallbackId = CreateSolidTexture(255, 255, 255, 0, "fallback_tex");

            foreach (NoteTypeEnum type in Enum.GetValues(typeof(NoteTypeEnum)))
            {
                try
                {
                    var id = LoadTextureFromAssembly(type.ToString());
                    _noteTextureMap[type] = id;
                }
                catch (Exception e)
                {
                    _noteTextureMap[type] = _noteTextureMap[NoteTypeEnum.Empty];
                    _noteTextureMap[type] = fallbackId;
                }
            }
        }

        public int GetTextureIndexForNoteType(NoteTypeEnum type)
        {
            return _noteTextureMap.TryGetValue(type, out var index) ? index : 0;
        }

        private void CreateSrvHeap()
        {
            var heap = new DescriptorHeapDescription
            {
                DescriptorCount = SrvHeapSize,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };

            _device.CreateDescriptorHeap(heap, out _srvHeap).CheckError();
            _srvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        }

        private void CreateUploadResources()
        {
            _device.CreateCommandAllocator(CommandListType.Direct, out _allocator).CheckError();
            _device.CreateCommandList(0, CommandListType.Direct, _allocator, null, out _commandList).CheckError();
            _commandList.Close();


            _device.CreateFence(0, FenceFlags.None, out _uploadFence).CheckError();
            _uploadFenceValue = 0;
            _fenceEvent = new AutoResetEvent(false);
        }

        public int LoadTextureFromAssembly(string textureName)
        {
            if (textureName == null || string.IsNullOrEmpty(textureName))
            {
                throw new ArgumentNullException(nameof(textureName));
            }

            if (_textureNameToIndex.TryGetValue(textureName, out int existingIndex))
            {
                return existingIndex;
            }

            if (_textureCount >= MaxTextures)
            {
                throw new InvalidOperationException("Maximum texture limit reached.");
            }

            LoadImage(textureName, out byte[] image, out int width, out int height);
            int index = CreateTextureFromData(image, width, height, textureName);
            return index;
        }

        public int CreateTextureFromData(byte[] rgbaData, int width, int height, string textureName)
        {
            if (_textureNameToIndex.ContainsKey(textureName))
            {
                throw new InvalidOperationException($"Texture with name '{textureName}' already exists.");
            }

            if (_textureCount >= MaxTextures)
            {
                throw new InvalidOperationException("Maximum texture limit reached.");
            }

            var index = _textureCount;
            var format = Format.R8G8B8A8_UNorm;

            var textureDesc = ResourceDescription.Texture2D(
                format,
                (uint)width,
                (uint)height,
                1,
                1
            );

            var defaultHeap = new HeapProperties(HeapType.Default);

            _device.CreateCommittedResource(
                defaultHeap,
                HeapFlags.None,
                textureDesc,
                ResourceStates.CopyDest,
                null,
                out var textureResource
            ).CheckError();

            var uploadBufferSize = GetRequiredIntermediateSize(textureResource, 0, 1);
            var uploadHeap = new HeapProperties(HeapType.Upload);
            var uploadBufferDesc = ResourceDescription.Buffer(uploadBufferSize);

            _device.CreateCommittedResource(
                uploadHeap,
                HeapFlags.None,
                uploadBufferDesc,
                ResourceStates.GenericRead,
                null,
                out var uploadBuffer
            ).CheckError();

            var rowPitch = (uint)(width * 4);
            var alignedRowPitch = (rowPitch + 255) & ~255u;

            unsafe
            {
                void* pData;
                uploadBuffer.Map(0, null, &pData).CheckError();
                byte* dest = (byte*)pData;
                fixed (byte* src = rgbaData)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + y * rowPitch, dest + y * alignedRowPitch, alignedRowPitch, rowPitch);
                    }
                }

                uploadBuffer.Unmap(0, null);
            }

            _allocator.Reset();
            _commandList.Reset(_allocator, null);

            var srcLocation = new TextureCopyLocation(uploadBuffer, new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint
                {
                    Format = format,
                    Width = (uint)width,
                    Height = (uint)height,
                    Depth = 1,
                    RowPitch = alignedRowPitch
                }
            });

            var dstLocation = new TextureCopyLocation(textureResource, 0);
            _commandList.CopyTextureRegion(dstLocation, 0, 0, 0, srcLocation, null);
            _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(textureResource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource));
            _commandList.Close();
            _commandQueue.ExecuteCommandList(_commandList);
            _uploadFenceValue++;
            _commandQueue.Signal(_uploadFence, _uploadFenceValue).CheckError();
            if (_uploadFence.CompletedValue < _uploadFenceValue)
            {
                _uploadFence.SetEventOnCompletion(_uploadFenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
                _fenceEvent.WaitOne();
            }

            uploadBuffer.Dispose();

            var srvDesc = new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0
                }
            };

            var srvHandle = _srvHeap.GetCPUDescriptorHandleForHeapStart();
            srvHandle += index * (int)_srvDescriptorSize;
            _device.CreateShaderResourceView(textureResource, srvDesc, srvHandle);
            _textures[index] = new TextureData
            {
                Resource = textureResource,
                Width = width,
                Height = height,
                Format = format,
                SrvIndex = index
            };

            _textureNameToIndex[textureName] = index;
            _textureCount++;
            return index;
        }

        public async UniTask<int> LoadTextureFromAssemblyAsync(string textureName)
        {
            if (textureName == null)
            {
                throw new ArgumentNullException(nameof(textureName));
            }

            if (_textureNameToIndex.TryGetValue(textureName, out int existingIndex))
            {
                return existingIndex;
            }

            if (_textureCount >= MaxTextures)
            {
                throw new InvalidOperationException("Maximum texture limit reached.");
            }

            var (image, width, height) = await UniTask.Run(() =>
            {
                LoadImage(textureName, out byte[] imgData, out int w, out int h);
                return (imgData, w, h);
            });

            int index = await CreateTextureFromDataAsync(image, width, height, textureName);
            return index;
        }

        public async UniTask<int> CreateTextureFromDataAsync(byte[] rgbaData, int width, int height, string textureName)
        {
            if (_textureNameToIndex.ContainsKey(textureName))
            {
                throw new InvalidOperationException($"Texture with name '{textureName}' already exists.");
            }

            if (_textureCount >= MaxTextures)
            {
                throw new InvalidOperationException("Maximum texture limit reached.");
            }

            var index = _textureCount;
            var format = Format.R8G8B8A8_UNorm;

            var textureDesc = ResourceDescription.Texture2D(
                format,
                (uint)width,
                (uint)height,
                1,
                1
            );

            var defaultHeap = new HeapProperties(HeapType.Default);

            _device.CreateCommittedResource(
                defaultHeap,
                HeapFlags.None,
                textureDesc,
                ResourceStates.CopyDest,
                null,
                out var textureResource
            ).CheckError();

            var uploadBufferSize = GetRequiredIntermediateSize(textureResource, 0, 1);
            var uploadHeap = new HeapProperties(HeapType.Upload);
            var uploadBufferDesc = ResourceDescription.Buffer(uploadBufferSize);

            _device.CreateCommittedResource(
                uploadHeap,
                HeapFlags.None,
                uploadBufferDesc,
                ResourceStates.GenericRead,
                null,
                out var uploadBuffer
            ).CheckError();

            var rowPitch = (uint)(width * 4);
            var alignedRowPitch = (rowPitch + 255) & ~255u;

            unsafe
            {
                void* pData;
                uploadBuffer.Map(0, null, &pData).CheckError();
                byte* dest = (byte*)pData;
                fixed (byte* src = rgbaData)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + y * rowPitch, dest + y * alignedRowPitch, alignedRowPitch, rowPitch);
                    }
                }

                uploadBuffer.Unmap(0, null);
            }

            _allocator.Reset();
            _commandList.Reset(_allocator, null);

            var srcLocation = new TextureCopyLocation(uploadBuffer, new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint
                {
                    Format = format,
                    Width = (uint)width,
                    Height = (uint)height,
                    Depth = 1,
                    RowPitch = alignedRowPitch
                }
            });

            var dstLocation = new TextureCopyLocation(textureResource, 0);
            _commandList.CopyTextureRegion(dstLocation, 0, 0, 0, srcLocation, null);
            _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(textureResource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource));
            _commandList.Close();
            _commandQueue.ExecuteCommandList(_commandList);
            _uploadFenceValue++;
            _commandQueue.Signal(_uploadFence, _uploadFenceValue).CheckError();

            await WaitForFenceAsync(_uploadFenceValue);
            uploadBuffer.Dispose();
            CreateSrvForTexture(index, textureResource, format, width, height, textureName);
            return index;
        }

        public int CreateSolidTexture(byte r, byte g, byte b, byte a, string name)
        {
            byte[] data = [r, g, b, a];
            return CreateTextureFromData(data, 1, 1, name);
        }

        public GpuDescriptorHandle GetTextureGpuHandle(int index)
        {
            if (index < 0 || index >= _textureCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Invalid texture index.");
            }

            var handle = _srvHeap.GetGPUDescriptorHandleForHeapStart();
            handle += index * (int)_srvDescriptorSize;
            return handle;
        }
        public GpuDescriptorHandle GetTextureTableHandle() => _srvHeap.GetGPUDescriptorHandleForHeapStart();

        public int GetTextureIndex(string name) => _textureNameToIndex.TryGetValue(name, out int index) ? index : -1;
        public TextureData GetTexture(int index) => (index >= 0 && index < _textureCount) ? _textures[index] : null;

        private void LoadImage(string name, out byte[] rgbaData, out int width, out int height)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var sourceName = $"{assembly.GetName().Name}.Textures.{name}.png";
            using var stream = assembly.GetManifestResourceStream(sourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"Embedded resource '{sourceName}' not found.");
            }

            DecodeImage(stream, out rgbaData, out width, out height);
        }

        private void DecodeImage(Stream data, out byte[] rgba, out int width, out int height)
        {
            var image = ImageResult.FromStream(data, ColorComponents.RedGreenBlueAlpha);
            rgba = image.Data;
            width = image.Width;
            height = image.Height;
        }

        private ulong GetRequiredIntermediateSize(ID3D12Resource resource, uint firstSubresource, uint subresourceCount)
        {
            var desc = resource.Description;
            _device.GetCopyableFootprints(desc, firstSubresource, subresourceCount, 0, out ulong total);
            return total;
        }

        private async UniTask WaitForFenceAsync(ulong targetValue)
        {
            while (_uploadFence.CompletedValue < targetValue)
            {
                await UniTask.Yield();
            }
        }

        private void CreateSrvForTexture(int index, ID3D12Resource textureResource, Format format, int width, int height, string textureName)
        {
            var srvDesc = new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0
                }
            };

            var srvHandle = _srvHeap.GetCPUDescriptorHandleForHeapStart();
            srvHandle += index * (int)_srvDescriptorSize;
            _device.CreateShaderResourceView(textureResource, srvDesc, srvHandle);

            _textures[index] = new TextureData
            {
                Resource = textureResource,
                Width = width,
                Height = height,
                Format = format,
                SrvIndex = index
            };

            _textureNameToIndex[textureName] = index;
            _textureCount++;
        }

        public override void Shutdown()
        {
            if (_uploadFence != null && _commandQueue != null)
            {
                _uploadFenceValue++;
                _commandQueue.Signal(_uploadFence, _uploadFenceValue).CheckError();
                if (_uploadFence.CompletedValue < _uploadFenceValue)
                {
                    _uploadFence.SetEventOnCompletion(_uploadFenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
                    _fenceEvent.WaitOne();
                }
            }

            for (int i = 0; i < _textureCount; i++)
            {
                _textures[i]?.Dispose();
                _textures[i] = null;
            }
            _textureCount = 0;
            _textureNameToIndex.Clear();

            lock (_uploadLock)
            {
                foreach (var up in _pendingUploads)
                {
                    up?.Dispose();
                }

                _pendingUploads.Clear();
            }

            _commandList?.Dispose();
            _allocator?.Dispose();
            _uploadFence?.Dispose();
            _fenceEvent?.Dispose();
            _srvHeap?.Dispose();

            base.Shutdown();
        }

        public void Dispose() => Shutdown();
    }
}