using System;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FluentDesigner.Direct3D12
{
    public static class D3D12Pipeline
    {
        public static ID3D12PipelineState CreatePipelineState(
            ID3D12Device device,
            ID3D12RootSignature signature,
            ReadOnlyMemory<byte> vs,
            ReadOnlyMemory<byte> ps)
        {
            var input = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
            };

            var blendDesc = new BlendDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false
            };

            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                LogicOpEnable = false,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                LogicOp = LogicOp.Noop,
                RenderTargetWriteMask = ColorWriteEnable.All
            };

            var pso = new GraphicsPipelineStateDescription
            {
                RootSignature = signature,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(input),
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = blendDesc,
                DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                SampleDescription = new SampleDescription(1, 0)
            };

            return device.CreateGraphicsPipelineState(pso);
        }

        public static ID3D12PipelineState CreateOpaquePipelineState(
            ID3D12Device device,
            ID3D12RootSignature signature,
            ReadOnlyMemory<byte> vs,
            ReadOnlyMemory<byte> ps,
            Format depthFormat = Format.D24_UNorm_S8_UInt)
        {
            var input = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
            };

            var depth = new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.Less,
                StencilEnable = false
            };

            var pso = new GraphicsPipelineStateDescription
            {
                RootSignature = signature,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(input),
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = BlendDescription.Opaque,
                DepthStencilState = depth,
                DepthStencilFormat = depthFormat,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                SampleDescription = new SampleDescription(1, 0)
            };

            return device.CreateGraphicsPipelineState(pso);
        }

        public static ID3D12PipelineState CreateTransparentPipelineState(
            ID3D12Device device,
            ID3D12RootSignature signature,
            ReadOnlyMemory<byte> vs,
            ReadOnlyMemory<byte> ps,
            Format depthFormat = Format.D24_UNorm_S8_UInt)
        {
            var input = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
            };

            var blendDesc = new BlendDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false
            };

            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                LogicOpEnable = false,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                LogicOp = LogicOp.Noop,
                RenderTargetWriteMask = ColorWriteEnable.All
            };

            var depthStencil = new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.Less,
                StencilEnable = false
            };

            var pso = new GraphicsPipelineStateDescription()
            {
                RootSignature = signature,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(input),
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = blendDesc,
                DepthStencilState = depthStencil,
                DepthStencilFormat = depthFormat,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                SampleDescription = new SampleDescription(1, 0)
            };

            return device.CreateGraphicsPipelineState(pso);
        }
    }
}