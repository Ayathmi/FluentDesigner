using FluentDesigner.ECS.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FluentDesigner.ECS.System
{
    public static class OutlineColors
    {
        public static readonly Vector4 Selection = new Vector4(1f, 0.5f, 0f, 1f);
    }

    public struct OutlineRenderItem
    {
        public int Id;
        public Vector4 Color;
        public float Thickness;
    }

    public sealed class OutlineService : Service, IDisposable
    {
        private RenderService _renderService;
        private HierarchyService _hierarchyService;
        private EcsWorldService _ecsWorld;
        private ID3D12PipelineState _outlinePso;
        private ReadOnlyMemory<byte> _outlineVertexShader;
        private ReadOnlyMemory<byte> _outlinePixelShader;

        private readonly List<int> _seletedEntitiesIds = new();
        private readonly Dictionary<int, OutlineRenderItem> _customOutlines = new();
        private Vector4 _selectColor = OutlineColors.Selection;
        private float _thickness = 0.01f;
        public IReadOnlyList<int> SeletedEntitiedIds => _seletedEntitiesIds;
        public bool HasSelection => _seletedEntitiesIds.Count > 0;

        public Vector4 SelectionColor
        {
            get => _selectColor;
            set => _selectColor = value;
        }

        public float Thickness
        {
            get => _thickness;
            set => _thickness = value;
        }

        public override void Initialize()
        {
            base.Initialize();
            _renderService = GetFramework().GetService<RenderService>();
            _hierarchyService = GetFramework().GetService<HierarchyService>();
            _ecsWorld = GetFramework().GetService<EcsWorldService>();
            _hierarchyService.SelectedEntitiesChanged += OnSelectionChanged;
            CompileShaders();
            CreateOutlinePso();
        }

        private void OnSelectionChanged(object s, List<int> selectedIds)
        {
            _seletedEntitiesIds.Clear();
            _seletedEntitiesIds.AddRange(selectedIds);
        }

        public void SetOutline(int id, Vector4 Color, float thick)
        {
            _customOutlines[id] = new OutlineRenderItem
            {
                Id = id,
                Color = Color,
                Thickness = thick
            };
        }

        public void RemoveOutline(int id)
        {
            _customOutlines.Remove(id);
        }

        public void ClearCustomOutlines()
        {
            _customOutlines.Clear();
        }

        private void CompileShaders()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var source = "FluentDesigner.Shaders.OutlineShader.hlsl";
            using var stream = assembly.GetManifestResourceStream(source);
            if (stream == null)
            {
                throw new FileNotFoundException("Outline Shaders not found.");
            }

            using var reader = new StreamReader(stream);
            var code = reader.ReadToEnd();

            Compiler.Compile(code,
                "VSOutlineMain",
                "OutlineShader.hlsl",
                "vs_5_0",
                out var vsBlob,
                out var vsError);
            if (vsError != null)
            {
                var errorMessage = vsError.AsString();
                throw new Exception($"Vertex Shader Compilation Error: {errorMessage}");
            }
            _outlineVertexShader = vsBlob.AsBytes();

            Compiler.Compile(code,
                "PSOutlineMain",
                "OutlineShader.hlsl",
                "ps_5_0",
                out var psBlob,
                out var psError);
            if (psError != null)
            {
                var errorMessage = psError.AsString();
                throw new Exception($"Vertex Shader Compilation Error: {errorMessage}");
            }
            _outlinePixelShader = psBlob.AsBytes();
        }

        public IEnumerable<OutlineRenderItem> GetAllOutlineItems()
        {
            foreach (var id in _seletedEntitiesIds)
            {
                if (_customOutlines.ContainsKey(id))
                {
                    continue;
                }

                yield return new OutlineRenderItem
                {
                    Id = id,
                    Color = _selectColor,
                    Thickness = _thickness
                };
            }
            foreach (var item in _customOutlines.Values)
            {
                yield return item;
            }
        }

        private void CreateOutlinePso()
        {
            var device = _renderService.Device;
            var rootSignature = _renderService.RootSignature;

            var input = new InputElementDescription[]
            {
                new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            };

            var blend = new BlendDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false
            };

            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            };

            var depth = new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.LessEqual,
                StencilEnable = false
            };

            var desc = new GraphicsPipelineStateDescription
            {
                RootSignature = rootSignature,
                VertexShader = _outlineVertexShader,
                PixelShader = _outlinePixelShader,
                InputLayout = new InputLayoutDescription(input),
                BlendState = blend,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = depth,
                DepthStencilFormat = Format.D24_UNorm_S8_UInt,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                SampleDescription = new SampleDescription(1, 0)
            };

            _outlinePso = device.CreateGraphicsPipelineState(desc);
        }

        public ID3D12PipelineState GetOutlinePso() => _outlinePso;

        public IEnumerable<(Transform trans, MeshRenderer mesh, Vector4 color, float thickness)> GetOutlineRenderables()
        {
            foreach (var item in GetAllOutlineItems())
            {
                var transform = _ecsWorld.GetComponent<Transform>(item.Id);
                var renderer = _ecsWorld.GetComponent<MeshRenderer>(item.Id);

                if (transform != null && renderer != null && renderer.Visibility)
                {
                    yield return (transform, renderer, item.Color, item.Thickness);
                }
            }
        }

        public IEnumerable<(Transform trans, MeshRenderer mesh)> GetSelectedRenderables()
        {
            foreach (var id in _seletedEntitiesIds)
            {
                var transform = _ecsWorld.GetComponent<Transform>(id);
                var renderer = _ecsWorld.GetComponent<MeshRenderer>(id);

                if (transform != null && renderer != null && renderer.Visibility)
                {
                    yield return (transform, renderer);
                }
            }
        }

        public override void Shutdown()
        {
            if (_hierarchyService != null)
            {
                _hierarchyService.SelectedEntitiesChanged -= OnSelectionChanged;
            }

            _outlinePso?.Dispose();
            base.Shutdown();
        }

        public void Dispose() => Shutdown();
    }
}
