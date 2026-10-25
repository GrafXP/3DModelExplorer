// Model surfaces. One vertex stream of positions and one of normals, straight
// from MeshData's arrays. Lit by an ambient term and three directional lights
// (Blinn-Phong), two-sided, since an STL's winding is not to be trusted.
//
// The cut preview needs no extra geometry: the rasterizer clips at the plane, and
// the back faces that become visible through the opening are shaded as if they
// were the cap, so the model looks solid until the real capped mesh replaces it.
//
// The deviation view colours each triangle from a buffer of distances indexed by
// SV_PrimitiveID, so a new tolerance recolours a mesh without touching it.

#include "common.hlsli"

Buffer<float> distances : register(t0);

struct VsIn
{
    float3 position : POSITION;
    float3 normal : NORMAL;
};

struct VsOut
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float clip : SV_ClipDistance0;
};

// What the pixel shaders read of VsOut: everything but the clip distance, which
// the rasterizer has used up.
struct PsIn
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
};

VsOut vs_main(VsIn i)
{
    VsOut o;
    float4 w = mul(float4(i.position, 1), world);
    o.world = w.xyz;
    o.position = mul(w, viewProjection);
    o.normal = i.normal; // models are only ever translated
    o.clip = (flags & FLAG_CLIP) ? dot(clipPlane.xyz, i.position) + clipPlane.w : 1;
    return o;
}

float3 Shade(float3 base, float3 n, float3 p, float specular)
{
    float3 v = normalize(cameraPosition.xyz - p);
    float3 c = ambient.rgb * base;
    [unroll]
    for (int k = 0; k < 3; k++)
    {
        float3 l = -lightDirection[k].xyz;
        float diffuse = saturate(dot(n, l));
        float3 h = normalize(l + v);
        float highlight = diffuse > 0 ? pow(saturate(dot(n, h)), material.y) : 0;
        c += lightColor[k].rgb * (base * diffuse + specular * highlight);
    }
    return c;
}

// Green within tolerance, then yellow through red to the top of the scale.
// The same function as HeatmapScale.Color, which draws the legend.
float3 HeatColor(float d)
{
    const float3 within = float3(0x36, 0xc9, 0x8f) / 255.0;
    const float3 middle = float3(0xf5, 0xcd, 0x4c) / 255.0;
    const float3 worst = float3(0xf0, 0x51, 0x4f) / 255.0;
    if (d <= material.z)
        return within;
    float t = saturate((d - material.z) / max(material.w - material.z, 1e-12));
    return t < 0.5 ? lerp(within, middle, t * 2) : lerp(middle, worst, t * 2 - 1);
}

float3 Lit(PsIn i, bool front, uint primitive)
{
    float3 base = color.rgb;
    float specular = material.x;
    if (flags & FLAG_HEATMAP)
    {
        base = HeatColor(distances[primitiveBase + primitive]);
        specular = 0;
    }

    // Derivatives before any branch: they need every pixel of the quad.
    float3 facet = normalize(cross(ddx(i.world), ddy(i.world)));
    float3 n = normalize(i.normal);
    if (!front && (flags & FLAG_CLIP))
    {
        // Inside the model, seen through the cut: shade it as the cap, which faces
        // away from the kept half.
        n = -normalize(clipPlane.xyz);
    }
    else if (flags & FLAG_FLAT)
    {
        n = dot(facet, cameraPosition.xyz - i.world) < 0 ? -facet : facet;
    }
    else
    {
        n = front ? n : -n;
    }
    return Shade(base, n, i.world, specular);
}

float4 ps_main(PsIn i, uint primitive : SV_PrimitiveID, bool front : SV_IsFrontFace) : SV_Target
{
    if (flags & FLAG_UNLIT)
        return float4(edgeColor.rgb, 1);
    return float4(Lit(i, front, primitive), 1);
}

// Weighted, blended order-independent transparency (McGuire and Bavoil, 2013):
// every surface adds its colour, weighted by opacity and nearness, into one
// target and multiplies its transparency into another; the composite pass
// divides the two out. No sorting, and overlapping meshes blend evenly.
struct OverlayOut
{
    float4 accumulation : SV_Target0;
    float revealage : SV_Target1;
};

OverlayOut ps_overlay(PsIn i, uint primitive : SV_PrimitiveID, bool front : SV_IsFrontFace)
{
    float3 c = Lit(i, front, primitive);
    float a = color.a;
    float z = length(cameraPosition.xyz - i.world) / max(view.x, 1e-6);
    float weight = a * clamp(0.03 / (1e-5 + pow(z * 0.5, 4)), 1e-2, 3e3);
    OverlayOut o;
    o.accumulation = float4(c * a, a) * weight;
    o.revealage = a;
    return o;
}
