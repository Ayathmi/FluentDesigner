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
    float4 OutlineColor : COLOR0;
    int TexIndex : TEXINDEX;
};

static const float ALPHA_THRESHOLD = 0.04f;
static const float OUTLINE_WIDTH_UV = 0.2f;
static const float OUTLINE_WIDTH_TEX = 2.0f;

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

PSInput VSOutlineMain(VSInput input)
{
    PSInput result;
    InstanceData inst = LoadInstance(input.InstanceID);
    float4 worldPos = mul(float4(input.Position, 0.96f), inst.World);
    
    result.Position = mul(worldPos, gViewProj);
    result.UV = input.UV;
    result.OutlineColor = inst.Color;
    result.TexIndex = inst.TextureIndex;
    
    return result;
}

float4 PSOutlineMain(PSInput input) : SV_TARGET
{
    float4 texColor = gTexture.Sample(gSampler, input.UV);
    float currentAlpha = texColor.a;

    if (currentAlpha < 0.01f)
        discard;

    float4 sample1 = gTexture.Sample(gSampler, float2(0.0f, 0.0f));
    float4 sample2 = gTexture.Sample(gSampler, float2(1.0f, 1.0f));
    float4 sample3 = gTexture.Sample(gSampler, float2(0.5f, 0.5f));
    
    bool isSolidTexture = (abs(sample1.a - sample2.a) < 0.001f) &&
                          (abs(sample2.a - sample3.a) < 0.001f) &&
                          (sample1.a > 0.99f);
    
    bool isEdge = false;
    
    if (isSolidTexture)
    {
        float edgeThreshold = OUTLINE_WIDTH_UV;
        
        isEdge = (input.UV.x < edgeThreshold) ||
                 (input.UV.x > (1.0f - edgeThreshold)) ||
                 (input.UV.y < edgeThreshold) ||
                 (input.UV.y > (1.0f - edgeThreshold));
    }
    else
    {
        float2 texelSize = float2(1.0f / 256.0f, 1.0f / 256.0f) * OUTLINE_WIDTH_TEX;
        
        float2 offsets[8] =
        {
            float2(-1, 0), float2(1, 0),
            float2(0, -1), float2(0, 1),
            float2(-1, -1), float2(1, -1),
            float2(-1, 1), float2(1, 1)
        };
        
        [unroll]
        for (int i = 0; i < 8; i++)
        {
            float2 sampleUV = input.UV + offsets[i] * texelSize;

            if (sampleUV.x < 0.0f || sampleUV.x > 1.0f ||
                sampleUV.y < 0.0f || sampleUV.y > 1.0f)
            {
                isEdge = true;
                break;
            }

            float neighborAlpha = gTexture.Sample(gSampler, sampleUV).a;

            if (neighborAlpha < ALPHA_THRESHOLD)
            {
                isEdge = true;
                break;
            }
        }
    }

    if (isEdge)
        return input.OutlineColor;

    discard;
    return float4(0, 0, 0, 0);
}