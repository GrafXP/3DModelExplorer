// Flat-coloured geometry: the bounding box, the build plate and its grid, the
// cutting plane and its outline. Positions only; colour and opacity per draw.

#include "common.hlsli"

float4 vs_main(float3 position : POSITION) : SV_Position
{
    return mul(mul(float4(position, 1), world), viewProjection);
}

float4 ps_main() : SV_Target
{
    return color;
}
