cbuffer PassCB : register(b0)
{
    row_major float4x4 gView;
    row_major float4x4 gProj;
    row_major float4x4 gViewProj;
    float3 gCameraPos;
    float gPadding0;
    float4 gTime;
    float4 gScreenSize;
}

struct InstanceData
{
    row_major float4x4 World;
    float4 Color;
    int TextureIndex;
    int Padding0, Padding1, Padding2;
};

ByteAddressBuffer gInstanceBuffer : register(t0);

Texture2D gTexture : register(t1);
SamplerState gSampler : register(s0);

struct VSInput
{
    float3 Position : POSITION;
    float2 UV : TEXCOORD0;
    uint InstanceID : SV_InstanceID;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
    float4 Color : COLOR;
    int TexIndex : TEXINDEX;
};

InstanceData LoadInstance(uint instanceId)
{
    uint byteOffset = instanceId * 96;
    
    InstanceData inst;
    inst.World[0] = asfloat(gInstanceBuffer.Load4(byteOffset + 0));
    inst.World[1] = asfloat(gInstanceBuffer.Load4(byteOffset + 16));
    inst.World[2] = asfloat(gInstanceBuffer.Load4(byteOffset + 32));
    inst.World[3] = asfloat(gInstanceBuffer.Load4(byteOffset + 48));

    inst.Color = asfloat(gInstanceBuffer.Load4(byteOffset + 64));

    uint4 extra = gInstanceBuffer.Load4(byteOffset + 80);
    inst.TextureIndex = asint(extra.x);
    
    return inst;
}

PSInput VSMain(VSInput input)
{
    PSInput result;
    InstanceData inst = LoadInstance(input.InstanceID);
    float4 worldPos = mul(float4(input.Position, 1.0f), inst.World);
    
    result.Position = mul(worldPos, gViewProj);
    result.UV = input.UV;
    result.Color = inst.Color;
    result.TexIndex = inst.TextureIndex;
    
    return result;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    float4 baseColor = input.Color;
    if (input.TexIndex >= 0)
    {
        float4 texColor = gTexture.Sample(gSampler, input.UV);
        baseColor *= texColor;
    }

    clip(baseColor.a - 0.01f);
    
    return baseColor;
}