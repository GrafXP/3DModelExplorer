// Resolves the overlay's order-independent transparency over the pane: the
// average colour of every surface at a pixel, weighted as mesh.hlsl ps_overlay
// wrote it, laid over what is behind with the combined opacity.

#include "common.hlsli"

Texture2D<float4> accumulation : register(t0);
Texture2D<float> revealage : register(t1);

// One triangle that covers the viewport.
float4 vs_fullscreen(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 ps_composite(float4 position : SV_Position) : SV_Target
{
    int3 texel = int3(int2(position.xy - paneOrigin), 0);
    float reveal = revealage.Load(texel);
    if (reveal >= 0.9999)
        discard;
    float4 sum = accumulation.Load(texel);
    return float4(sum.rgb / max(sum.a, 1e-5), 1 - reveal);
}
