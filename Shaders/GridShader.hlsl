cbuffer PassConstants : register(b0)
{
    row_major float4x4 View;
    row_major float4x4 Proj;
    row_major float4x4 ViewProj;
    float3 CamPos;
    float Padding1;
    float4 Time;
    float4 ScreenParams;
};

struct VSInput
{
    float3 Position : POSITION;
    float4 Color : COLOR;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR;
    float3 WorldPos : TEXCOORD0;
};

PSInput VSGridMain(VSInput input)
{
    PSInput output;
    output.WorldPos = input.Position;
    output.Position = mul(float4(input.Position, 1.0f), ViewProj);
    output.Color = input.Color;
    return output;
}

float4 PSGridMain(PSInput input) : SV_TARGET
{
    float dist = length(input.WorldPos.xz - CamPos.xz);
    float fade = saturate(1.0f - dist / 1024.0f);
    float4 color = input.Color;
    color.a *= fade;
    return color;
}