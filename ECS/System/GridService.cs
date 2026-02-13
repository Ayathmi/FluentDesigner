using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FluentDesigner.ECS.System;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GridVertex
{
    public Vector3 Position;
    public Vector4 Color;

    public static int SizeInBytes => 28;
}

public sealed class GridService : Service, IDisposable
{
    private const int DefaultGridSize = 10;
    private const int MajorLineInterval = 10;
    private const int GridExtent = 32767;

    private RenderService _renderService;
    private ID3D12Device _device;
    private ID3D12PipelineState _gridPipelineState;
    private ID3D12Resource _prevVBuffer;
    private ID3D12Resource _prevMVBuffer;
    private ID3D12Resource _vBuffer;
    private VertexBufferView _vBufferView;
    private int _lineVertexCount;
    private ID3D12Resource _mvBuffer;
    private VertexBufferView _mvBufferView;
    private int _majorLineVertexCount;
    private float _gridSize = DefaultGridSize;
    private bool _needRebuild = true;
    private bool _visible = true;
    private int _pendingDisposeFrames = 0;

    public bool Visibility
    {
        get => _visible;
        set => _visible = value;
    }

    public float GridSpacing
    {
        get => _gridSize;
        set
        {
            float newSpacing = value < 1f ? 1f : value;
            if (Math.Abs(GridSpacing - newSpacing) > 0.001f)
            {
                _gridSize = newSpacing;
                _needRebuild = true;
            }
        }
    }

    public override void Initialize()
    {
        base.Initialize();

        _renderService = GetFramework().GetService<RenderService>();
        if (_renderService?.Device == null)
        {
            throw new InvalidOperationException("RenderService is not available.");
        }

        _device = _renderService.Device;
        CreateGridPipelineState();
        RebuildGridMesh();
    }

    private void CreateGridPipelineState()
    {
        if (_renderService.GridVertexShaderCode.IsEmpty || _renderService.GridPixelShaderCode.IsEmpty)
        {
            throw new InvalidOperationException("Grid shaders are not compiled.");
        }

        var input = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
        };

        var pso = new GraphicsPipelineStateDescription
        {
            RootSignature = _renderService.RootSignature,
            VertexShader = _renderService.GridVertexShaderCode,
            PixelShader = _renderService.GridPixelShaderCode,
            InputLayout = new InputLayoutDescription(input),
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.AlphaBlend,
            DepthStencilState = DepthStencilDescription.None,
            PrimitiveTopologyType = PrimitiveTopologyType.Line,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            SampleDescription = new SampleDescription(1, 0)
        };

        _gridPipelineState = _device.CreateGraphicsPipelineState(pso);
    }

    private void ProcessPendingDispose()
    {
        if (_pendingDisposeFrames > 0)
        {
            _pendingDisposeFrames--;
            if (_pendingDisposeFrames == 0)
            {
                _prevVBuffer?.Dispose();
                _prevVBuffer = null;
                _prevMVBuffer?.Dispose();
                _prevMVBuffer = null;
            }
        }
    }

    public void RebuildIfNeeded()
    {
        ProcessPendingDispose();

        if (_needRebuild)
        {
            RebuildGridMesh();
            _needRebuild = false;
        }
    }

    private void RebuildGridMesh()
    {
        if (_vBuffer != null || _mvBuffer != null)
        {
            _prevVBuffer?.Dispose();
            _prevMVBuffer?.Dispose();

            _prevVBuffer = _vBuffer;
            _prevMVBuffer = _mvBuffer;
            _pendingDisposeFrames = 3;
        }

        var extent = GridExtent;
        var normalLines = new List<GridVertex>();
        var majorLines = new List<GridVertex>();

        var nColor = new Vector4(0.3f, 0.3f, 0.3f, 0.32f);
        var mColor = new Vector4(0.5f, 0.5f, 0.5f, 0.64f);
        var xColor = new Vector4(0.8f, 0.2f, 0.2f, 1f);
        var zColor = new Vector4(0.2f, 0.2f, 0.8f, 1f);

        majorLines.Add(new GridVertex { Position = new Vector3(0f, 0f, -extent), Color = zColor });
        majorLines.Add(new GridVertex { Position = new Vector3(0f, 0f, extent), Color = zColor });
        majorLines.Add(new GridVertex { Position = new Vector3(-extent, 0f, 0f), Color = xColor });
        majorLines.Add(new GridVertex { Position = new Vector3(extent, 0f, 0f), Color = xColor });

        int lineIndex = 1;
        for (float offset = _gridSize; offset <= extent; offset += _gridSize)
        {
            bool isMajor = (lineIndex % MajorLineInterval) == 0;
            Vector4 color = isMajor ? mColor : nColor;
            var lineZPos = new GridVertex[]
            {
            new GridVertex { Position = new Vector3(offset, 0f, -extent), Color = color },
            new GridVertex { Position = new Vector3(offset, 0f, extent), Color = color }
            };
            var lineZNeg = new GridVertex[]
            {
            new GridVertex { Position = new Vector3(-offset, 0f, -extent), Color = color },
            new GridVertex { Position = new Vector3(-offset, 0f, extent), Color = color }
            };
            var lineXPos = new GridVertex[]
            {
            new GridVertex { Position = new Vector3(-extent, 0f, offset), Color = color },
            new GridVertex { Position = new Vector3(extent, 0f, offset), Color = color }
            };
            var lineXNeg = new GridVertex[]
            {
            new GridVertex { Position = new Vector3(-extent, 0f, -offset), Color = color },
            new GridVertex { Position = new Vector3(extent, 0f, -offset), Color = color }
            };

            if (isMajor)
            {
                majorLines.AddRange(lineZPos);
                majorLines.AddRange(lineZNeg);
                majorLines.AddRange(lineXPos);
                majorLines.AddRange(lineXNeg);
            }
            else
            {
                normalLines.AddRange(lineZPos);
                normalLines.AddRange(lineZNeg);
                normalLines.AddRange(lineXPos);
                normalLines.AddRange(lineXNeg);
            }

            lineIndex++;
        }

        if (normalLines.Count > 0)
        {
            CreateVertexBuffer(normalLines.ToArray(), out _vBuffer, out _vBufferView);
            _lineVertexCount = normalLines.Count;
        }

        if (majorLines.Count > 0)
        {
            CreateVertexBuffer(majorLines.ToArray(), out _mvBuffer, out _mvBufferView);
            _majorLineVertexCount = majorLines.Count;
        }
    }

    private void CreateVertexBuffer(GridVertex[] vertices, out ID3D12Resource buffer, out VertexBufferView view)
    {
        int bufferSize = vertices.Length * GridVertex.SizeInBytes;
        var heapProps = new HeapProperties(HeapType.Upload);
        var resourceDesc = ResourceDescription.Buffer((ulong)bufferSize);

        var hr = _device.CreateCommittedResource(
            heapProps,
            HeapFlags.None,
            resourceDesc,
            ResourceStates.GenericRead,
            null,
            out buffer);

        if (hr.Failure)
        {
            throw new Exception($"Failed to create grid vertex buffer: {hr.Code}");
        }

        unsafe
        {
            void* pData;
            hr = buffer.Map(0, null, &pData);
            if (hr.Failure)
            {
                throw new Exception($"Failed to map grid vertex buffer: {hr.Code}");
            }

            byte* dest = (byte*)pData;

            for (int i = 0; i < vertices.Length; i++)
            {
                *(Vector3*)(dest) = vertices[i].Position;
                dest += 12;
                *(Vector4*)(dest) = vertices[i].Color;
                dest += 16;
            }

            buffer.Unmap(0, null);
        }

        view = new VertexBufferView(
            buffer.GPUVirtualAddress,
            (uint)bufferSize,
            (uint)GridVertex.SizeInBytes);
    }

    public ID3D12PipelineState GetPipelineState() => _gridPipelineState;

    public (VertexBufferView view, int count) GetNormalLines() => (_vBufferView, _lineVertexCount);
    public (VertexBufferView view, int count) GetMajorLines() => (_mvBufferView, _majorLineVertexCount);


    private void DisposeBuffers()
    {
        _vBuffer?.Dispose();
        _vBuffer = null;
        _mvBuffer?.Dispose();
        _mvBuffer = null;
        _lineVertexCount = 0;
        _majorLineVertexCount = 0;
    }

    public override void Shutdown()
    {
        _renderService.WaitForGpu();

        DisposeBuffers();
        _gridPipelineState?.Dispose();
        base.Shutdown();
    }

    public void Dispose() => Shutdown();
}