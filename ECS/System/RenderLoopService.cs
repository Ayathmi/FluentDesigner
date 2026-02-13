using FluentDesigner.Direct3D12;
using FluentDesigner.ECS.Components;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using Transform = FluentDesigner.ECS.Components.Transform;

namespace FluentDesigner.ECS.System
{
    public sealed class RenderLoopService : Service, IDisposable
    {
        private const int FrameCount = 2;
        private RenderService _renderService;
        private ConstantBufferService _cbService;
        private InstanceBufferService _instanceService;
        private EcsWorldService _world;
        private GridService _gridService;
        private ID3D12CommandAllocator[] _commandAllocators;
        private ID3D12GraphicsCommandList _commandList;
        private ID3D12Fence _fence;
        private TextureService _textureService;
        private ulong[] _fenceValues;
        private AutoResetEvent _fenceEvent;
        private int _frameIndex;
        private bool _isRunning;
        private DispatcherQueueTimer _renderTimer;
        private readonly Stopwatch _stopwatch = new();
        private float _totalTime;
        private float _deltaTime;
        private CameraMode _cameraMode = CameraMode.Perspective;
        private Vector3 _cameraPosition = new(10, 10, -10f);
        private Vector3 _cameraTarget = Vector3.Zero;
        private Vector3 _cameraUp = Vector3.UnitY;
        private float _orthoWidth = 20f;
        private float _orthoHeight = 15f;
        private float _fovY = MathF.PI / 4f;
        private int _frameCount;
        private float _fpsTimer;
        private float _currentFps;
        private ID3D12DescriptorHeap[] _srvHeapArray;
        private volatile bool _commandListOpen = false;
        private ulong _currentFenceValue = 0;
        private readonly List<RenderBatch> _opaqueEntities = new(2048);
        private readonly List<RenderBatch> _transparentEntities = new(65535);
        private OutlineService _outlineService;
        private readonly List<RenderableEntity> _selectedEntities = new(2048);
        private PlaybackService _playbackService;
        private CameraService _cameraService;
        private bool _isPlaybackMode;

#if DEBUG
        private int _debugLogCounter = 0;
        private const int DebugLogInterval = 60;
#endif

        public bool IsRunning => _isRunning;
        public float CurrentFps => _currentFps;
        public float TotalTime => _totalTime;
        public float DeltaTime => _deltaTime;

        public bool GridVisible
        {
            get => _gridService?.Visibility ?? false;
            set
            {
                if (_gridService != null)
                {
                    _gridService.Visibility = value;
                }
            }
        }

        public float GridSpacing
        {
            get => _gridService?.GridSpacing ?? 10f;
            set
            {
                if (_gridService != null)
                {
                    _gridService.GridSpacing = value;
                }
            }
        }

        public bool IsPlaybackMode
        {
            get => _isPlaybackMode;
            private set => _isPlaybackMode = value;
        }

        public override void Initialize()
        {
            base.Initialize();
            _renderService = GetFramework().GetService<RenderService>();
            _cbService = GetFramework().GetService<ConstantBufferService>();
            _instanceService = GetFramework().GetService<InstanceBufferService>();
            _textureService = GetFramework().GetService<TextureService>();
            _world = GetFramework().GetService<EcsWorldService>();
            _gridService = GetFramework().GetService<GridService>();
            _outlineService = GetFramework().GetService<OutlineService>();
            _cameraService = GetFramework().GetService<CameraService>();
            if (_cameraService != null)
            {
                _cameraService.CameraChanged += SetCamera;
            }
            _playbackService = GetFramework().GetService<PlaybackService>();

            if (_playbackService != null)
            {
                _playbackService.PlayModeEntered += OnPlayModeEntered;
                _playbackService.PlayModeExited += OnPlayModeExited;
                _playbackService.TimeUpdated += OnPlaybackTimeUpdated;
                _playbackService.SeekPreviewRequested += OnSeekPreviewRequested;
            }

            CreateFrameResources();
            _stopwatch.Start();

            _srvHeapArray = new ID3D12DescriptorHeap[1];
        }

        private void CreateFrameResources()
        {
            var device = _renderService.Device;
            _commandAllocators = new ID3D12CommandAllocator[FrameCount];
            _fenceValues = new ulong[FrameCount];

            for (int i = 0; i < FrameCount; i++)
            {
                device.CreateCommandAllocator(CommandListType.Direct, out _commandAllocators[i]).CheckError();
                _fenceValues[i] = 0;
            }

            device.CreateCommandList(0, CommandListType.Direct, _commandAllocators[0], _renderService.PipelineState, out _commandList);
            _commandList.Close();

            device.CreateFence(0, FenceFlags.None, out _fence).CheckError();
            _fenceEvent = new AutoResetEvent(false);
        }

        public void StartRenderLoop(DispatcherQueue dispatcherQueue)
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;

            _renderTimer = dispatcherQueue.CreateTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(1);
            _renderTimer.Tick += OnRenderTick;
            _renderTimer.Start();
        }

        public void StartRenderLoopWithComposition()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;

            CompositionTarget.Rendering += OnCompositionRendering;
        }

        public void StopRenderLoop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;

            _renderTimer?.Stop();
            _renderTimer = null;

            CompositionTarget.Rendering -= OnCompositionRendering; ;

            WaitForGpu();
        }

        private void OnRenderTick(DispatcherQueueTimer sender, object args)
        {
            if (_isRunning)
            {
                RenderFrame();
            }
        }

        private void OnCompositionRendering(object? sender, object e)
        {
            if (_isRunning)
            {
                RenderFrame();
            }
        }

        public void RenderFrame()
        {
            var swapChain = _renderService.SwapChain;
            if (swapChain == null)
            {
                return;
            }

            _renderService.ProcessPendingResize();

            if (!ValidateRenderTargets())
            {
                return;
            }

            UpdateTiming();
            _frameIndex = (int)_renderService.SwapChain.CurrentBackBufferIndex;

            var renderTargets = _renderService.RenderTargets;
            if (renderTargets == null || _renderService.RenderTargets[_frameIndex] == null)
            {
                return;
            }

            if (!IsFrameReady(_frameIndex))
            {
                return;
            }

            WaitForFrame(_frameIndex);
            if (!TryResetCommandList())
            {
                return;
            }

            _gridService?.RebuildIfNeeded();

            if (_isPlaybackMode && _playbackService != null)
            {
                if (_playbackService.State == PlaybackState.Playing || _playbackService.State == PlaybackState.Paused)
                {
                    UpdateNotePositionsFromPlayback();
                }
            }

            UpdateBuffers();
            CollectAndSortEntities();

            UpdateBuffers();
            CollectAndSortEntities();
            if (!RecordCommands())
            {
                CloseCommandListIfOpen();
                return;
            }
            UpdateFpsCounter();
            ExecuteAndPresent();
        }

        private bool IsFrameReady(int frameIndex)
        {
            return _fence.CompletedValue >= _fenceValues[frameIndex];
        }

        private void UpdateTiming()
        {
            var current = (float)_stopwatch.Elapsed.TotalSeconds;
            _deltaTime = current - _totalTime;
            _totalTime = current;
        }

        private void UpdateFpsCounter()
        {
            _frameCount++;
            _fpsTimer += _deltaTime;

            if (_fpsTimer >= 1.0f)
            {
                _currentFps = _frameCount / _fpsTimer;
                _frameCount = 0;
                _fpsTimer = 0.0f;
            }

#if DEBUG
                Debug.WriteLine($"FPS: {_currentFps:F1}");
#endif
        }

        private bool TryResetCommandList()
        {
            try
            {
                _commandAllocators[_frameIndex].Reset();
                _commandList.Reset(_commandAllocators[_frameIndex], null);
                _commandListOpen = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to reset command list: {ex.Message}");
                _commandListOpen = false;
                return false;
            }
        }

        private void CloseCommandListIfOpen()
        {
            if (_commandListOpen)
            {
                try
                {
                    _commandList.Close();
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }
                _commandListOpen = false;
            }
        }

        private void CollectAndSortEntities()
        {
            _opaqueEntities.Clear();
            _transparentEntities.Clear();

            var opaqueByTexture = new Dictionary<int, List<RenderableEntity>>();
            var transparentByTexture = new Dictionary<int, List<RenderableEntity>>();

            var transPool = _world.GetPool<Transform>();
            var rendererPool = _world.GetPool<MeshRenderer>();

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

                int texIndex = renderer.TextureIndex >= 0
                    ? renderer.TextureIndex
                    : _textureService.GetTextureIndexForNoteType(renderer.Type);

                var entity = new RenderableEntity
                {
                    Id = id,
                    Position = new Position { X = trans.Position.X, Y = trans.Position.Y, Z = trans.Position.Z },
                    Rotation = new Rotation { X = trans.Rotation.X, Y = trans.Rotation.Y, Z = trans.Rotation.Z, W = trans.Rotation.W },
                    Scale = new Scale { X = trans.Scale.X, Y = trans.Scale.Y, Z = trans.Scale.Z },
                    Color = renderer.Color,
                    Type = renderer.Type,
                    SortingLayer = renderer.SortingLayer,
                    OrderInLayer = renderer.OrderInLayer,
                    TextureIndex = texIndex
                };

#if DEBUG
                if (renderer.Type == NoteTypeEnum.RotateL || renderer.Type == NoteTypeEnum.RotateR)
                {
                    var world = Matrix4x4.CreateScale(trans.Scale.X, trans.Scale.Y, trans.Scale.Z)
                                * Matrix4x4.CreateFromQuaternion(new Quaternion(
                                    trans.Rotation.X, trans.Rotation.Y,
                                    trans.Rotation.Z, trans.Rotation.W))
                                * Matrix4x4.CreateTranslation(
                                    trans.Position.X, trans.Position.Y, trans.Position.Z);

                    Debug.WriteLine($"[Rotate Entity] Id={id} Type={renderer.Type}");
                    Debug.WriteLine($"  Scale=({trans.Scale.X:F2}, {trans.Scale.Y:F2}, {trans.Scale.Z:F2})");
                    Debug.WriteLine($"  Position=({trans.Position.X:F2}, {trans.Position.Y:F2}, {trans.Position.Z:F2})");
                    Debug.WriteLine($"  TexIndex={texIndex}");
                }
#endif

                if (renderer.Color.W < 1f)
                {
                    if (!transparentByTexture.TryGetValue(texIndex, out var list))
                    {
                        list = new List<RenderableEntity>(64);
                        transparentByTexture[texIndex] = list;
                    }
                    list.Add(entity);
                }
                else
                {
                    if (!opaqueByTexture.TryGetValue(texIndex, out var list))
                    {
                        list = new List<RenderableEntity>(64);
                        opaqueByTexture[texIndex] = list;
                    }
                    list.Add(entity);
                }
            }
            RenderableEntityComparer.Instance.CameraPosition = _cameraPosition;
            _instanceService.BeginFrame(_frameIndex);

            foreach (var kvp in opaqueByTexture)
            {
                int textureIndex = kvp.Key;
                var entities = kvp.Value;
                if (entities.Count == 0)
                {
                    continue;
                }

                entities.Sort(RenderableEntityComparer.Instance);

                int startInstance = _instanceService.GetFrameInstanceCount(_frameIndex);

                foreach (var entity in entities)
                {
                    var world = Matrix4x4.CreateScale(entity.Scale.X, entity.Scale.Y, entity.Scale.Z)
                                * Matrix4x4.CreateFromQuaternion(new Quaternion(
                                    entity.Rotation.X,
                                    entity.Rotation.Y,
                                    entity.Rotation.Z,
                                    entity.Rotation.W))
                                * Matrix4x4.CreateTranslation(
                                    entity.Position.X,
                                    entity.Position.Y,
                                    entity.Position.Z);
                    var ins = new InstanceData
                    {
                        World = world,
                        Color = entity.Color,
                        TextureIndex = textureIndex
                    };
                    _instanceService.AddInstance(_frameIndex, in ins);
                }

                int instanceCount = _instanceService.GetFrameInstanceCount(_frameIndex) - startInstance;

                _opaqueEntities.Add(new RenderBatch
                {
                    TextureIndex = textureIndex,
                    StartInstance = startInstance,
                    InstanceCount = instanceCount
                });
            }

            foreach (var kvp in transparentByTexture)
            {
                int textureIndex = kvp.Key;
                var entities = kvp.Value;
                if (entities.Count == 0)
                {
                    continue;
                }

                entities.Sort(RenderableEntityComparer.Instance);

                int startInstance = _instanceService.GetFrameInstanceCount(_frameIndex);

                foreach (var entity in entities)
                {
                    var world = Matrix4x4.CreateScale(entity.Scale.X, entity.Scale.Y, entity.Scale.Z)
                                * Matrix4x4.CreateFromQuaternion(new Quaternion(
                                    entity.Rotation.X,
                                    entity.Rotation.Y,
                                    entity.Rotation.Z,
                                    entity.Rotation.W))
                                * Matrix4x4.CreateTranslation(
                                    entity.Position.X,
                                    entity.Position.Y,
                                    entity.Position.Z);
                    var ins = new InstanceData
                    {
                        World = world,
                        Color = entity.Color,
                        TextureIndex = textureIndex
                    };
                    _instanceService.AddInstance(_frameIndex, in ins);
                }

                int instanceCount = _instanceService.GetFrameInstanceCount(_frameIndex) - startInstance;

                _transparentEntities.Add(new RenderBatch
                {
                    TextureIndex = textureIndex,
                    StartInstance = startInstance,
                    InstanceCount = instanceCount
                });
            }
        }

#if DEBUG
        private void LogMatrixDebug(string name, Matrix4x4 matrix)
        {
            Debug.WriteLine($"[Matrix] {name}:");
            Debug.WriteLine($"  Row0: ({matrix.M11:F4}, {matrix.M12:F4}, {matrix.M13:F4}, {matrix.M14:F4})");
            Debug.WriteLine($"  Row1: ({matrix.M21:F4}, {matrix.M22:F4}, {matrix.M23:F4}, {matrix.M24:F4})");
            Debug.WriteLine($"  Row2: ({matrix.M31:F4}, {matrix.M32:F4}, {matrix.M33:F4}, {matrix.M34:F4})");
            Debug.WriteLine($"  Row3: ({matrix.M41:F4}, {matrix.M42:F4}, {matrix.M43:F4}, {matrix.M44:F4})");
        }

        private void LogPassConstants(PassConstants pass)
        {
            Debug.WriteLine($"[Pass] Frame={_frameIndex} CamPos=({pass.CamPos.X:F2},{pass.CamPos.Y:F2},{pass.CamPos.Z:F2})");
            LogMatrixDebug("View", pass.View);
            LogMatrixDebug("Proj", pass.Proj);
        }
#endif

        private void UpdateBuffers()
        {
            if (!_cbService.IsInitialized)
            {
                return;
            }

            var swapChain = _renderService.SwapChain;

            int width = swapChain?.SourceSize.Width ?? 1;
            int height = swapChain?.SourceSize.Height ?? 1;

            var ratio = swapChain != null ? (float)swapChain.SourceSize.Width / swapChain.SourceSize.Height : 1f;

            Matrix4x4 view;
            Matrix4x4 proj;
            Vector3 camPos;

            if (_isPlaybackMode && _cameraService != null && _cameraService.IsPlaybackMode)
            {
                view = _cameraService.GetViewMatrix();
                proj = _cameraService.GetProjectionMatrix(ratio);
                camPos = _cameraService.Position;
            }
            else
            {
                view = Matrix4x4.CreateLookAt(_cameraPosition, _cameraTarget, _cameraUp);
                proj = _cameraMode switch
                {
                    CameraMode.Perspective => Matrix4x4.CreatePerspectiveFieldOfView(_fovY, ratio, 0.1f, 1024f),
                    CameraMode.Orthographic => Matrix4x4.CreateOrthographic(_orthoWidth, _orthoHeight, 0.1f, 1024f),
                    _ => Matrix4x4.Identity
                };
                camPos = _cameraPosition;
            }

            var pass = new PassConstants
            {
                View = view,
                Proj = proj,
                ViewProj = view * proj,
                CamPos = camPos,
                Time = new Vector4(_totalTime, _deltaTime, 0, 0),
                ScreenParams = new Vector4(
                    swapChain?.SourceSize.Width ?? 1920,
                    swapChain?.SourceSize.Height ?? 1080,
                    1f / (swapChain?.SourceSize.Width ?? 1920),
                    1f / (swapChain?.SourceSize.Height ?? 1080))
            };

            _cbService.UpdataPassConstant(_frameIndex, in pass);

#if DEBUG
            if (_debugLogCounter++ % DebugLogInterval == 0)
            {
                LogPassConstants(pass);
            }
#endif
        }

        private bool RecordCommands()
        {
            var swapChain = _renderService.SwapChain;
            if (swapChain == null)
            {
                return false;
            }

            var renderTargets = _renderService.RenderTargets;
            if (renderTargets == null || _frameIndex >= renderTargets.Length)
            {
                return false;
            }

            var renderTarget = renderTargets[_frameIndex];
            if (renderTarget == null)
            {
                return false;
            }

            var rtvHandle = _renderService.RtvHeap.GetCPUDescriptorHandleForHeapStart();
            rtvHandle += _frameIndex * (int)_renderService.RtvDescriptorSize;
            var viewport = new Viewport(0, 0, swapChain.SourceSize.Width, swapChain.SourceSize.Height);
            var scissorRect = new Vortice.RawRect(0, 0, swapChain.SourceSize.Width, swapChain.SourceSize.Height);

            _commandList.RSSetViewport(viewport);
            _commandList.RSSetScissorRect(scissorRect);
            _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(renderTarget, ResourceStates.Present, ResourceStates.RenderTarget));
            _commandList.ClearRenderTargetView(rtvHandle, new Color4(0.1f, 0.1f, 0.1f, 1f));
            _commandList.OMSetRenderTargets(rtvHandle, null);
            _commandList.SetGraphicsRootSignature(_renderService.RootSignature);

            if (_renderService.HasDepthBuffer)
            {
                var dsvHandle = _renderService.GetDsvHandle();
                _commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1f, 0);
                _commandList.OMSetRenderTargets(rtvHandle, dsvHandle);
            }
            else
            {
                _commandList.OMSetRenderTargets(rtvHandle, null);
            }

            RenderGrid();

            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            if (_textureService.SrvHeap != null)
            {
                _srvHeapArray[0] = _textureService.SrvHeap;
                _commandList.SetDescriptorHeaps(1, _srvHeapArray);
            }

            _commandList.SetGraphicsRootConstantBufferView(0, _cbService.GetPassCBAddress(_frameIndex));
            _commandList.SetGraphicsRootShaderResourceView(1, _instanceService.GetFrameBufferAddress(_frameIndex));

            var mesh = _renderService.GetMesh(NoteTypeEnum.Click);
            if (mesh != null)
            {
                _commandList.IASetVertexBuffers(0, mesh.VBufferView);
                _commandList.IASetIndexBuffer(mesh.IBufferView);

                if (_renderService.OpaquePipelineState != null && _opaqueEntities.Count > 0)
                {
                    _commandList.SetPipelineState(_renderService.OpaquePipelineState);
                    RenderBatches(_opaqueEntities, mesh);
                }

                if (_renderService.TransparentPipelineState != null && _transparentEntities.Count > 0)
                {
                    _commandList.SetPipelineState(_renderService.TransparentPipelineState);
                    RenderBatches(_transparentEntities, mesh);
                }
            }

            RenderOutlines();

            _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(renderTarget, ResourceStates.RenderTarget, ResourceStates.Present));
            _commandList.Close();
            _commandListOpen = false;

            return true;
        }

        private void RenderBatches(List<RenderBatch> batches, MeshData mesh)
        {
            foreach (var batch in batches)
            {
                if (batch.InstanceCount == 0)
                {
                    continue;
                }

                if (_textureService.TextureCount > batch.TextureIndex && batch.TextureIndex >= 0)
                {
                    _commandList.SetGraphicsRootDescriptorTable(2, _textureService.GetTextureGpuHandle(batch.TextureIndex));
                }
                else if (_textureService.TextureCount > 0)
                {
                    _commandList.SetGraphicsRootDescriptorTable(2, _textureService.GetTextureGpuHandle(0));
                }

                ulong batchBufferAddress = _instanceService.GetFrameBufferAddress(_frameIndex)
                                           + (ulong)(batch.StartInstance * InstanceData.SizeInBytes);
                _commandList.SetGraphicsRootShaderResourceView(1, batchBufferAddress);

                _commandList.DrawIndexedInstanced(
                    (uint)mesh.IndexCount,
                    (uint)batch.InstanceCount,
                    0,
                    0,
                    (uint)batch.StartInstance);
            }
        }

        private void RenderOutlines()
        {
            if (_outlineService == null || !_outlineService.HasSelection)
            {
                return;
            }

            var outlinePso = _outlineService.GetOutlinePso();
            if (outlinePso == null)
            {
                return;
            }

            var outlineItems = new List<(Transform trans, MeshRenderer mesh, Vector4 color, float thickness)>();
            foreach (var item in _outlineService.GetOutlineRenderables())
            {
                outlineItems.Add(item);
            }

            if (outlineItems.Count == 0)
            {
                return;
            }

            _commandList.SetPipelineState(outlinePso);
            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            _commandList.SetGraphicsRootSignature(_renderService.RootSignature);
            _commandList.SetGraphicsRootConstantBufferView(0, _cbService.GetPassCBAddress(_frameIndex));
            _commandList.SetGraphicsRootShaderResourceView(1, _instanceService.GetFrameBufferAddress(_frameIndex));

            if (_textureService.SrvHeap != null)
            {
                _srvHeapArray[0] = _textureService.SrvHeap;
                _commandList.SetDescriptorHeaps(1, _srvHeapArray);
            }

            var mesh = _renderService.GetMesh(NoteTypeEnum.Click);
            if (mesh == null)
            {
                return;
            }

            _commandList.IASetVertexBuffers(0, mesh.VBufferView);
            _commandList.IASetIndexBuffer(mesh.IBufferView);

            var outlinesByTexture = new Dictionary<int, List<(Transform trans, MeshRenderer mesh, Vector4 color, float thickness)>>();

            foreach (var item in outlineItems)
            {
                int texIndex = item.mesh.TextureIndex >= 0
                    ? item.mesh.TextureIndex
                    : _textureService.GetTextureIndexForNoteType(item.mesh.Type);

                if (!outlinesByTexture.TryGetValue(texIndex, out var list))
                {
                    list = new List<(Transform, MeshRenderer, Vector4, float)>();
                    outlinesByTexture[texIndex] = list;
                }
                list.Add(item);
            }
            var outlineBatches = new List<RenderBatch>();

            foreach (var kvp in outlinesByTexture)
            {
                int textureIndex = kvp.Key;
                var items = kvp.Value;

                if (items.Count == 0)
                {
                    continue;
                }

                int startInstance = _instanceService.GetFrameInstanceCount(_frameIndex);

                foreach (var (trans, renderer, outlineColor, thickness) in items)
                {
                    float scale = 1.0f + thickness * 0.03f;
                    var world = Matrix4x4.CreateScale(
                                    trans.Scale.X * scale,
                                    trans.Scale.Y * scale,
                                    trans.Scale.Z * scale)
                                * Matrix4x4.CreateFromQuaternion(new Quaternion(
                                    trans.Rotation.X, trans.Rotation.Y,
                                    trans.Rotation.Z, trans.Rotation.W))
                                * Matrix4x4.CreateTranslation(
                                    trans.Position.X, trans.Position.Y, trans.Position.Z);
                    var ins = new InstanceData
                    {
                        World = world,
                        Color = outlineColor,
                        TextureIndex = textureIndex
                    };

                    _instanceService.AddInstance(_frameIndex, in ins);
                }

                int instanceCount = _instanceService.GetFrameInstanceCount(_frameIndex) - startInstance;

                outlineBatches.Add(new RenderBatch
                {
                    TextureIndex = textureIndex,
                    StartInstance = startInstance,
                    InstanceCount = instanceCount
                });
            }

            foreach (var batch in outlineBatches)
            {
                if (batch.InstanceCount == 0)
                {
                    continue;
                }

                if (_textureService.TextureCount > batch.TextureIndex && batch.TextureIndex >= 0)
                {
                    _commandList.SetGraphicsRootDescriptorTable(2, _textureService.GetTextureGpuHandle(batch.TextureIndex));
                }
                else if (_textureService.TextureCount > 0)
                {
                    _commandList.SetGraphicsRootDescriptorTable(2, _textureService.GetTextureGpuHandle(0));
                }

                ulong batchBufferAddress = _instanceService.GetFrameBufferAddress(_frameIndex)
                                           + (ulong)(batch.StartInstance * InstanceData.SizeInBytes);
                _commandList.SetGraphicsRootShaderResourceView(1, batchBufferAddress);


                _commandList.DrawIndexedInstanced(
                    (uint)mesh.IndexCount,
                    (uint)batch.InstanceCount,
                    0,
                    0,
                    (uint)batch.StartInstance);
            }
        }

        private void RenderGrid()
        {
            if (_gridService == null || !_gridService.Visibility)
            {
                return;
            }

            var gridPso = _gridService.GetPipelineState();
            if (gridPso == null)
            {
                return;
            }

            _commandList.SetPipelineState(gridPso);
            _commandList.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            _commandList.SetGraphicsRootConstantBufferView(0, _cbService.GetPassCBAddress(_frameIndex));

            var (normalView, normalCount) = _gridService.GetNormalLines();
            if (normalCount > 0)
            {
                _commandList.IASetVertexBuffers(0, normalView);
                _commandList.DrawInstanced((uint)normalCount, 1, 0, 0);
            }

            var (majorView, majorCount) = _gridService.GetMajorLines();
            if (majorCount > 0)
            {
                _commandList.IASetVertexBuffers(0, majorView);
                _commandList.DrawInstanced((uint)majorCount, 1, 0, 0);
            }
        }

        private void WaitForFrame(int frameIndex)
        {
            if (_fence.CompletedValue < _fenceValues[frameIndex])
            {
                _fence.SetEventOnCompletion(_fenceValues[frameIndex], _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                _fenceEvent.WaitOne(1000);
            }
        }

        private void WaitForGpu()
        {
            _currentFenceValue++;
            _renderService.CommandQueue.Signal(_fence, _currentFenceValue);

            if (_fence.CompletedValue < _currentFenceValue)
            {
                _fence.SetEventOnCompletion(_currentFenceValue, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
                _fenceEvent.WaitOne(5000);
            }

            for (int i = 0; i < FrameCount; i++)
            {
                _fenceValues[i] = _currentFenceValue;
            }
        }

        private void ExecuteAndPresent()
        {
            _renderService.CommandQueue.ExecuteCommandList(_commandList);
            _renderService.SwapChain.Present(1, PresentFlags.None);

            _currentFenceValue++;
            _renderService.CommandQueue.Signal(_fence, _currentFenceValue);
            _fenceValues[_frameIndex] = _currentFenceValue;
        }

        private bool ValidateRenderTargets()
        {
            var renderTargets = _renderService.RenderTargets;
            if (renderTargets == null)
            {
                return false;
            }

            for (int i = 0; i < FrameCount; i++)
            {
                if (renderTargets[i] == null)
                {
                    return false;
                }
            }
            return true;
        }

        public void SetCameraMode(CameraMode mode) => _cameraMode = mode;

        public void SetCamera(Vector3 position, Vector3 target, Vector3 up)
        {
            _cameraPosition = position;
            _cameraTarget = target;
            _cameraUp = up;
        }

        public void SetOrthographicSize(float width, float height)
        {
            _orthoWidth = width;
            _orthoHeight = height;
        }

        public void SetPerspectiveFov(float fovY) => _fovY = fovY;

        public override void Shutdown()
        {
            _isRunning = false;
            WaitForAllFrames();

            _commandList?.Dispose();
            if (_commandAllocators != null)
            {
                foreach (var a in _commandAllocators)
                {
                    a?.Dispose();
                }
            }

            _fence?.Dispose();
            _fenceEvent?.Dispose();

            if (_playbackService != null)
            {
                _playbackService.PlayModeEntered -= OnPlayModeEntered;
                _playbackService.PlayModeExited -= OnPlayModeExited;
                _playbackService.TimeUpdated -= OnPlaybackTimeUpdated;
                _playbackService.SeekPreviewRequested -= OnSeekPreviewRequested;
            }

            base.Shutdown();
        }

        private void WaitForAllFrames()
        {
            WaitForGpu();
        }

        private void OnPlayModeEntered(object sender, EventArgs e)
        {
            _isPlaybackMode = true;

            _cameraService?.SetDefaultPlaybackCamera();
            _cameraService?.EnterPlaybackMode();

            if (_gridService != null)
            {
                _gridService.Visibility = false;
            }

            if (_outlineService != null)
            {
                _outlineService.ClearCustomOutlines();
            }
        }

        private void OnPlayModeExited(object sender, EventArgs e)
        {
            _isPlaybackMode = false;

            _cameraService?.ExitPlaybackMode();

            if (_gridService != null)
            {
                _gridService.Visibility = true;
            }
        }

        private void UpdateNotePositionsFromPlayback()
        {
            if (_playbackService == null)
            {
                return;
            }

            var transPool = _world.GetPool<Transform>();
            var rendererPool = _world.GetPool<MeshRenderer>();

            foreach (var entityId in transPool.GetEntities())
            {
                if (!rendererPool.Contains(entityId))
                {
                    continue;
                }

                var renderer = rendererPool.Get(entityId);
                if (renderer != null)
                {
                    renderer.Visibility = false;
                }
            }

            foreach (var (note, x, y, z) in _playbackService.GetVisibleNotes())
            {
                if (!transPool.Contains(note.EntityId))
                {
                    continue;
                }

                var trans = transPool.Get(note.EntityId);
                var renderer = rendererPool.Get(note.EntityId);

                if (trans != null)
                {
                    trans.Position = new Position { X = x, Y = y, Z = z };
                }

                if (renderer != null)
                {
                    renderer.Visibility = true;
                }
            }
        }

        private void OnPlaybackTimeUpdated(object sender, float currentTime) { }

        private void OnSeekPreviewRequested(object sender, float time)
        {
            if (_isPlaybackMode)
            {
                UpdateNotePositionsFromPlayback();
                if (_isRunning)
                {
                    RenderFrame();
                }
            }
        }

        public void Dispose() => Shutdown();
    }

    public enum CameraMode
    {
        Perspective,
        Orthographic
    }

    internal struct RenderBatch
    {
        public int TextureIndex;
        public int StartInstance;
        public int InstanceCount;
    }

    internal struct RenderableEntity
    {
        public int Id;
        public Position Position;
        public Rotation Rotation;
        public Scale Scale;
        public Vector4 Color;
        public NoteTypeEnum Type;
        public int SortingLayer;
        public int OrderInLayer;
        public int TextureIndex;
    }

    internal sealed class RenderableEntityComparer : IComparer<RenderableEntity>
    {
        public static readonly RenderableEntityComparer Instance = new RenderableEntityComparer();
        public Vector3 CameraPosition { get; set; } = Vector3.Zero;
        private static readonly Dictionary<NoteTypeEnum, int> TypeRenderOrder = new()
    {
        { NoteTypeEnum.Guiding, 0 },
        { NoteTypeEnum.RotateR, 1 },
        { NoteTypeEnum.RotateL, 2 },
        { NoteTypeEnum.Flick, 3 },
        { NoteTypeEnum.Slide, 4 },
        { NoteTypeEnum.Click, 5 },
        { NoteTypeEnum.Mine, 6 },
        { NoteTypeEnum.Catch, 7 },
        { NoteTypeEnum.Rail, 8 },
        { NoteTypeEnum.Empty, 9 }
    };

        private RenderableEntityComparer() { }

        public int Compare(RenderableEntity x, RenderableEntity y)
        {
            int layerComparison = x.SortingLayer.CompareTo(y.SortingLayer);
            if (layerComparison != 0)
            {
                return layerComparison;
            }

            int orderComparison = x.OrderInLayer.CompareTo(y.OrderInLayer);
            if (orderComparison != 0)
            {
                return orderComparison;
            }

            bool xIsGuiding = x.Type == NoteTypeEnum.Guiding;
            bool yIsGuiding = y.Type == NoteTypeEnum.Guiding;

            if (xIsGuiding && !yIsGuiding) return -1;
            if (!xIsGuiding && yIsGuiding) return 1;

            float xDist = DistanceSquared(x.Position, CameraPosition);
            float yDist = DistanceSquared(y.Position, CameraPosition);

            const float distanceThreshold = 0.0001f;
            float distDiff = xDist - yDist;

            if (Math.Abs(distDiff) > distanceThreshold)
            {
                return distDiff > 0 ? -1 : 1;
            }

            int xTypeOrder = TypeRenderOrder.TryGetValue(x.Type, out var xo) ? xo : 999;
            int yTypeOrder = TypeRenderOrder.TryGetValue(y.Type, out var yo) ? yo : 999;

            return xTypeOrder.CompareTo(yTypeOrder);
        }

        private static float DistanceSquared(Position pos, Vector3 camPos)
        {
            float dx = pos.X - camPos.X;
            float dy = pos.Y - camPos.Y;
            float dz = pos.Z - camPos.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}