// Constants shared by every shader. Matrices are row-major and multiply row
// vectors from the left, as System.Numerics builds them.

cbuffer Frame : register(b0)
{
    row_major float4x4 viewProjection;
    float4 cameraPosition;     // xyz: the eye, in world space
    float4 ambient;            // rgb: ambient light
    float4 lightDirection[3];  // xyz: the direction each light travels (main, fill, rim)
    float4 lightColor[3];      // rgb: colour times strength; black when the light is off
    float4 view;               // x: distance from the eye to the target, the scale of the scene
};

cbuffer Object : register(b1)
{
    row_major float4x4 world;
    float4 color;          // rgb: surface or line colour; a: opacity
    float4 edgeColor;      // rgb: colour of the edge overlay and the wireframe
    float4 clipPlane;      // model space; the half where dot(xyz, p) + w >= 0 is kept
    float4 material;       // x: specular strength, y: shininess, z: tolerance (mm), w: top of the colour scale (mm)
    uint flags;            // see FLAG_*
    uint primitiveBase;    // triangles before this draw call, for meshes uploaded in chunks
    float2 paneOrigin;     // the pane's top-left corner in pixels (composite)
};

static const uint FLAG_CLIP = 1;     // cut by clipPlane, back faces drawn as the cap
static const uint FLAG_FLAT = 2;     // facet normals from screen derivatives
static const uint FLAG_HEATMAP = 4;  // coloured per triangle by its deviation
static const uint FLAG_UNLIT = 8;    // edgeColor as it is: edges and wireframe
