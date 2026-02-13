using FluentDesigner.Direct3D12;
using FluentDesigner.ECS.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;
using WinRT;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace FluentDesigner.ECS.System
{
    public sealed class RenderService : Service, IDisposable
    {
        private const int FrameCount = 2;
        private const Format DepthFormat = Format.D24_UNorm_S8_UInt;

        public ID3D12Device Device { get; private set; }
        public ID3D12CommandQueue CommandQueue { get; private set; }
        public IDXGISwapChain3 SwapChain { get; private set; }
        public ID3D12DescriptorHeap RtvHeap { get; private set; }
        public ID3D12DescriptorHeap DsvHeap { get; private set; }
        public ID3D12Resource[] RenderTargets { get; private set; }
        public ID3D12Resource DepthStencilBuffer { get; private set; }
        public uint RtvDescriptorSize { get; private set; }
        public uint DsvDescriptorSize { get; private set; }
        public ID3D12RootSignature RootSignature { get; private set; }
        public ReadOnlyMemory<byte> VertexShaderCode { get; private set; }
        public ReadOnlyMemory<byte> PixelShaderCode { get; private set; }
        public ID3D12PipelineState PipelineState { get; private set; }
        public ID3D12PipelineState OpaquePipelineState { get; private set; }
        public ID3D12PipelineState TransparentPipelineState { get; private set; }
        public IDXGIFactory4 Factory { get; private set; }
        public ReadOnlyMemory<byte> GridVertexShaderCode { get; private set; }
        public ReadOnlyMemory<byte> GridPixelShaderCode { get; private set; }

        public bool HasDepthBuffer => DepthStencilBuffer != null;

        private Dictionary<NoteTypeEnum, MeshData> _noteMeshes;
        private ID3D12Fence _fence;
        private long _fenceValue;
        private AutoResetEvent _fenceEvent;
        private int _width, _height;
        private SwapChainPanel _panel;
        private bool _debugLayerEnabled = false;
        private readonly Lock _resizeLock = new();
        private volatile bool _resizePending = false;
        private int _pendingWidth, _pendingHeight;

        public override void Initialize()
        {
            base.Initialize();
            InitD3D12Device();
            CreateRootSignature();
            CompileShaders();
            CreatePSO();
            CreateMeshes();
        }

        private void InitD3D12Device()
        {
            _debugLayerEnabled = false;

#if DEBUG
            try
            {
                if (D3D12GetDebugInterface<ID3D12Debug>(out var debug).Success)
                {
                    debug?.EnableDebugLayer();
                    debug?.Dispose();
                    _debugLayerEnabled = true;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("D3D12 Debug Layer not available: " + e.Message);
            }
#endif

            CreateDXGIFactory2<IDXGIFactory4>(_debugLayerEnabled, out var factory).CheckError();
            Factory = factory;

            if (D3D12CreateDevice<ID3D12Device>(null, Vortice.Direct3D.FeatureLevel.Level_11_0, out var device).Failure)
            {
                factory.EnumWarpAdapter<IDXGIAdapter>(out var warp);
                D3D12CreateDevice(warp, Vortice.Direct3D.FeatureLevel.Level_11_0, out device);
            }

            Device = device;

            var queueDesc = new CommandQueueDescription
            {
                Type = CommandListType.Direct,
                Flags = CommandQueueFlags.None
            };
            Device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc, out var queue);
            CommandQueue = queue;

            var rtvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = FrameCount,
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None
            };
            Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(rtvHeapDesc, out var rtvHeap);
            RtvHeap = rtvHeap;
            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            var dsvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = 1,
                Type = DescriptorHeapType.DepthStencilView,
                Flags = DescriptorHeapFlags.None
            };
            Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(dsvHeapDesc, out var dsvHeap);
            DsvHeap = dsvHeap;
            DsvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);

            Device.CreateFence<ID3D12Fence>(0, FenceFlags.None, out _fence).CheckError();
            _fenceValue = 1;
            _fenceEvent = new AutoResetEvent(false);
        }

        private void CreateRootSignature()
        {
            var srvRange = new DescriptorRange1(
                DescriptorRangeType.ShaderResourceView,
                numDescriptors: 1,
                baseShaderRegister: 1,
                registerSpace: 0);

            var root = new RootParameter1[3];
            root[0] = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            root[1] = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex);
            root[2] = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);

            var samplers = new StaticSamplerDescription[]
            {
                new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                {
                    Filter = Filter.Anisotropic,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    MaxAnisotropy = 16
                }
            };

            var rootDesc = new VersionedRootSignatureDescription(new RootSignatureDescription1(
                    RootSignatureFlags.AllowInputAssemblerInputLayout,
                    root,
                    samplers));

            D3D12SerializeVersionedRootSignature(rootDesc, out var blob);
            Device.CreateRootSignature(0, blob, out var signature);
            RootSignature = signature;
            blob.Dispose();
        }

        private void CompileShaders()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string source = "FluentDesigner.Shaders.Shader.hlsl";
            string code;
            using (var stream = assembly.GetManifestResourceStream(source))
            {
                if (stream == null)
                {
                    throw new Exception($"Embedded resource not found: {source}.");
                }

                using (var reader = new StreamReader(stream))
                {
                    code = reader.ReadToEnd();
                }
            }

            Compiler.Compile(
                code,
                "VSMain",
                "Shader.hlsl",
                "vs_5_0",
                out var vsBlob,
                out var vsError);

            if (vsError != null)
            {
                string error = vsError.ToString();
                throw new Exception($"Vertex Shader Compile Failed: {error}.");
            }
            VertexShaderCode = vsBlob.AsBytes();

            Compiler.Compile(
                code,
                "PSMain",
                "Shader.hlsl",
                "ps_5_0",
                out var psBlob,
                out var psError);

            if (psError != null)
            {
                string error = psError.ToString();
                throw new Exception($"Vertex Shader Compile Failed: {error}.");
            }
            PixelShaderCode = psBlob.AsBytes();

            var assemblyGrid = Assembly.GetExecutingAssembly();
            string sourceGrid = "FluentDesigner.Shaders.GridShader.hlsl";
            string codeGrid;
            using (var stream = assemblyGrid.GetManifestResourceStream(sourceGrid))
            {
                if (stream == null)
                {
                    throw new Exception($"Embedded resource not found : {sourceGrid}");
                }

                using (var reader = new StreamReader(stream))
                {
                    codeGrid = reader.ReadToEnd();
                }
            }

            Compiler.Compile(
                codeGrid,
                "VSGridMain",
                "GridShader.hlsl",
                "vs_5_0",
                out var vsGridBlob,
                out var vsGridError);
            GridVertexShaderCode = vsGridBlob.AsBytes();

            Compiler.Compile(
                codeGrid,
                "PSGridMain",
                "GridShader.hlsl",
                "ps_5_0",
                out var psGridBlob,
                out var psGridError);
            GridPixelShaderCode = psGridBlob.AsBytes();
        }

        private void CreateDepthStencilBuffer()
        {
            DepthStencilBuffer?.Dispose();
            DepthStencilBuffer = null;

            if (_width <= 0 || _height <= 0)
            {
                return;
            }

            var depthDesc = ResourceDescription.Texture2D(
                DepthFormat,
                (uint)_width,
                (uint)_height,
                1, 1, 1, 0,
                ResourceFlags.AllowDepthStencil);

            var clear = new ClearValue
            {
                Format = DepthFormat,
                DepthStencil = new DepthStencilValue
                {
                    Depth = 1f,
                    Stencil = 0
                }
            };

            var heap = new HeapProperties(HeapType.Default);

            var hr = Device.CreateCommittedResource(
                heap,
                HeapFlags.None,
                depthDesc,
                ResourceStates.DepthWrite,
                clear,
                out var depthBuffer);

            if (hr.Failure)
            {
                throw new OperationCanceledException($"Failed to create DepthStencilBuffer: {hr}");
            }

            DepthStencilBuffer = depthBuffer;

            var dsvDesc = new DepthStencilViewDescription
            {
                Format = DepthFormat,
                ViewDimension = DepthStencilViewDimension.Texture2D,
                Texture2D = new Texture2DDepthStencilView { MipSlice = 0 }
            };

            var handle = DsvHeap.GetCPUDescriptorHandleForHeapStart();
            Device.CreateDepthStencilView(DepthStencilBuffer, dsvDesc, handle);
        }

        private void CreateRenderTargetViews()
        {
            RenderTargets = new ID3D12Resource[FrameCount];
            var handle = RtvHeap.GetCPUDescriptorHandleForHeapStart();

            for (int i = 0; i < FrameCount; i++)
            {
                SwapChain.GetBuffer<ID3D12Resource>((uint)i, out var renderTarget).CheckError();
                RenderTargets[i] = renderTarget;
                Device.CreateRenderTargetView(renderTarget, null, handle);
                handle += (int)RtvDescriptorSize;
            }
        }

        private void UpdateSize()
        {
            if (_panel == null)
            {
                return;
            }

            _width = (int)(_panel.ActualWidth);
            _height = (int)(_panel.ActualHeight);
            _width = Math.Max(_width, 1);
            _height = Math.Max(_height, 1);
        }

        private void OnPanelSizeChanged(object s, SizeChangedEventArgs e)
        {
            if (SwapChain == null)
            {
                TryCreateSwapChain();
            }
            else
            {
                RequestResize();
            }
        }

        private void OnCompositionScaleChanged(SwapChainPanel s, object e)
        {
            if (SwapChain != null)
            {
                RequestResize();
            }
        }

        private void CreatePSO()
        {
            if (VertexShaderCode.IsEmpty || PixelShaderCode.IsEmpty)
            {
                throw new InvalidOperationException("Shader must be compiled.");
            }

            PipelineState = D3D12Pipeline.CreatePipelineState(Device, RootSignature, VertexShaderCode, PixelShaderCode);
            OpaquePipelineState = D3D12Pipeline.CreateOpaquePipelineState(Device, RootSignature, VertexShaderCode, PixelShaderCode);
            TransparentPipelineState = D3D12Pipeline.CreateTransparentPipelineState(Device, RootSignature, VertexShaderCode, PixelShaderCode);
        }

        private void CreateMeshes()
        {
            _noteMeshes = new();
            foreach (NoteTypeEnum type in Enum.GetValues(typeof(NoteTypeEnum)))
            {
                if (type == NoteTypeEnum.Empty)
                {
                    if (_noteMeshes.TryGetValue(NoteTypeEnum.Click, out var clickMesh))
                    {
                        _noteMeshes[NoteTypeEnum.Empty] = clickMesh;
                    }

                    continue;
                }

                var mesh = MeshGenerator.CreateNoteMesh(Device, type);
                _noteMeshes[type] = mesh;
            }

            if (!_noteMeshes.ContainsKey(NoteTypeEnum.Empty) && _noteMeshes.Count > 0)
            {
                _noteMeshes[NoteTypeEnum.Empty] = _noteMeshes.Values.GetEnumerator().Current;
            }
        }

        private void RequestResize()
        {
            lock (_resizeLock)
            {
                UpdateSize();
                _pendingWidth = _width;
                _pendingHeight = _height;
                _resizePending = true;
            }
        }

        public void Resize()
        {
            if (SwapChain == null)
            {
                return;
            }

            WaitForPreviousFrame();

            for (int i = 0; i < FrameCount; i++)
            {
                RenderTargets[i]?.Dispose();
                RenderTargets[i] = null;
            }

            UpdateSize();
            SwapChain.ResizeBuffers(FrameCount, (uint)_width, (uint)_height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
            CreateRenderTargetViews();
        }

        public void SetSwapChainTarget(SwapChainPanel panel)
        {
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));
            _panel.SizeChanged += OnPanelSizeChanged;
            _panel.CompositionScaleChanged += OnCompositionScaleChanged;
            UpdateSize();

            TryCreateSwapChain();
        }

        public void ProcessPendingResize()
        {
            if (!_resizePending)
            {
                return;
            }

            lock (_resizeLock)
            {
                if (!_resizePending)
                {
                    return;
                }

                _width = _pendingWidth;
                _height = _pendingHeight;
                _resizePending = false;

                if (SwapChain == null || _width <= 1 || _height <= 1)
                {
                    return;
                }

                ResizeInternal();
            }
        }

        private void ResizeInternal()
        {
            WaitForGpu();

            for (int i = 0; i < FrameCount; i++)
            {
                RenderTargets[i]?.Dispose();
                RenderTargets[i] = null;
            }

            var hr = SwapChain.ResizeBuffers(
                FrameCount,
                (uint)_width,
                (uint)_height,
                Format.R8G8B8A8_UNorm,
                SwapChainFlags.None);

            if (hr.Failure)
            {
                Debug.WriteLine($"ResizeBuffers failed: {hr}");
                return;
            }

            CreateRenderTargetViews();
            CreateDepthStencilBuffer();
        }

        private void TryCreateSwapChain()
        {
            if (SwapChain != null)
            {
                return;
            }

            UpdateSize();

            if (_width <= 1 || _height <= 1)
            {
                Debug.WriteLine($"Waiting for valid panel size. Current: {_width}x{_height}");
                return;
            }

            CreateSwapChainInternal();
        }

        private void CreateSwapChainInternal()
        {
            /*
            Debug.WriteLine($"Creating SwapChain: {_width}x{_height}");
            */

            var swapChainDesc = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.R8G8B8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = FrameCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore
            };

            using var swapchain1 = Factory.CreateSwapChainForComposition(CommandQueue, swapChainDesc);
            SwapChain = swapchain1.QueryInterface<IDXGISwapChain3>();

            var nativePanel = _panel.As<ISwapChainPanelNative>();
            nativePanel.SetSwapChain(SwapChain.NativePointer);

            CreateRenderTargetViews();
            CreateDepthStencilBuffer();
        }

        public CpuDescriptorHandle GetDsvHandle() => DsvHeap.GetCPUDescriptorHandleForHeapStart();

        public MeshData GetMesh(NoteTypeEnum type)
        {
            if (_noteMeshes.TryGetValue(type, out var mesh))
            {
                return mesh;
            }

            if (_noteMeshes.TryGetValue(NoteTypeEnum.Click, out var fallback))
            {
                return fallback;
            }

            return null;
        }

        public override void Shutdown()
        {
            WaitForPreviousFrame();

            if (_panel != null)
            {
                _panel.SizeChanged -= OnPanelSizeChanged;
                _panel.CompositionScaleChanged -= OnCompositionScaleChanged;
            }

            WaitForGpu();


            if (_noteMeshes != null)
            {
                var disposed = new HashSet<MeshData>();
                foreach (var mesh in _noteMeshes.Values)
                {
                    if (mesh != null && !disposed.Contains(mesh))
                    {
                        mesh.Dispose();
                        disposed.Add(mesh);
                    }
                }

                _noteMeshes.Clear();
            }
            PipelineState?.Dispose();
            RootSignature?.Dispose();
            if (RenderTargets != null)
            {
                foreach (var rt in RenderTargets)
                {
                    rt.Dispose();
                }
            }

            SwapChain?.Dispose();
            _fence?.Dispose();
            CommandQueue?.Dispose();
            Device.Dispose();
            _fenceEvent.Dispose();
        }

        public void Dispose()
        {
            Shutdown();
        }

        public void WaitForGpu()
        {
            if (CommandQueue == null || _fence == null)
            {
                return;
            }

            ulong fenceValue = (ulong)Interlocked.Increment(ref _fenceValue);
            CommandQueue.Signal(_fence, fenceValue);

            if (_fence.CompletedValue < fenceValue)
            {
                _fence.SetEventOnCompletion(fenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                _fenceEvent.WaitOne(5000);
            }
        }

        private void WaitForPreviousFrame()
        {
            ulong currentFenceValue = (ulong)_fenceValue;
            CommandQueue.Signal(_fence, currentFenceValue);
            _fenceValue++;

            if (_fence.CompletedValue < currentFenceValue)
            {
                _fence.SetEventOnCompletion(currentFenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                _fenceEvent.WaitOne();
            }
        }

        [ComImport]
        [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISwapChainPanelNative
        {
            void SetSwapChain(IntPtr swapChain);
        }
    }
}
