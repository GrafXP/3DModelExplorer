using System.Numerics;
using System.Runtime.InteropServices;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Geometry;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace ModelExplorer.App.Render;

/// <summary>Constants for every draw of a pane. Matches cbuffer Frame in common.hlsli.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProjection;
    public Vector4 CameraPosition;
    public Vector4 Ambient;
    public Vector4 MainDirection, FillDirection, RimDirection;
    public Vector4 MainColor, FillColor, RimColor;
    public Vector4 View;
}

/// <summary>Constants for one draw. Matches cbuffer Object in common.hlsli.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ObjectConstants
{
    public Matrix4x4 World;
    public Vector4 Color;
    public Vector4 EdgeColor;
    public Vector4 ClipPlane;
    public Vector4 Material;
    public uint Flags;
    public uint PrimitiveBase;
    public Vector2 PaneOrigin;
}

/// <summary>A model to draw: its buffers, where it is moved to, how it is coloured, and the plane cutting it.</summary>
internal readonly record struct ModelDraw(GpuMesh Mesh, Vector3 Offset, Vector3 Color, float Opacity, Plane? Clip = null);

/// <summary>
/// Draws the panes: models, overlays and comparison views. UI thread only.
/// </summary>
internal sealed unsafe class SceneRenderer : IDisposable
{
    public static readonly Vector3 Background = new(0x1A / 255f, 0x1A / 255f, 0x1A / 255f);

    private const uint FlagClip = 1, FlagFlat = 2, FlagHeatmap = 4, FlagUnlit = 8;

    /// <summary>D3D11_COLOR_WRITE_ENABLE_ALL: red, green, blue and alpha.</summary>
    private const byte WriteAll = 0x0F;

    private static readonly Vector4 CutColor = new(1f, 0.59f, 0.24f, 1f);
    private static readonly Vector4 BoxColor = new(0x4D / 255f, 0xD0 / 255f, 0xE1 / 255f, 1f);
    private static readonly Vector4 PlateSurfaceColor = new(0.15f, 0.16f, 0.19f, 1f);
    private static readonly Vector4 PlateGridColor = new(0x3C / 255f, 0x42 / 255f, 0x4B / 255f, 1f);
    private static readonly Vector4 PlateFitColor = new(0x6E / 255f, 0x7A / 255f, 0x88 / 255f, 1f);
    private static readonly Vector4 PlateOverrunColor = new(0xE0 / 255f, 0x52 / 255f, 0x52 / 255f, 1f);

    private readonly GraphicsDevice _gpu;
    private ID3D11VertexShader* _meshVs, _lineVs, _compositeVs;
    private ID3D11PixelShader* _meshPs, _overlayPs, _linePs, _compositePs;
    private ID3D11InputLayout* _meshLayout, _lineLayout;
    private ID3D11Buffer* _frameBuffer, _objectBuffer;
    private ID3D11RasterizerState* _solid, _wireframe, _edges;
    private ID3D11DepthStencilState* _depthWrite, _depthRead, _depthOff;
    private ID3D11BlendState* _alphaBlend, _oitBlend;

    private readonly LineGeometry _box, _planeSurface, _planeOutline;
    private LineGeometry? _plateSurface, _plateGrid, _plateOutline;
    private BuildPlate? _plate;

    private ID3D11Texture2D* _accumTexture, _revealTexture;
    private ID3D11RenderTargetView* _accumTarget, _revealTarget;
    private ID3D11ShaderResourceView* _accumView, _revealView;
    private int _oitWidth, _oitHeight;

    private FrameConstants _frame;
    private PaneRect _pane;

    public SceneRenderer(GraphicsDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _meshVs = CreateVertexShader(device, Shaders.MeshVs);
        _lineVs = CreateVertexShader(device, Shaders.LineVs);
        _compositeVs = CreateVertexShader(device, Shaders.CompositeVs);
        _meshPs = CreatePixelShader(device, Shaders.MeshPs);
        _overlayPs = CreatePixelShader(device, Shaders.MeshOverlayPs);
        _linePs = CreatePixelShader(device, Shaders.LinePs);
        _compositePs = CreatePixelShader(device, Shaders.CompositePs);
        _meshLayout = CreateLayout(device, Shaders.MeshVs, twoStreams: true);
        _lineLayout = CreateLayout(device, Shaders.LineVs, twoStreams: false);
        _frameBuffer = CreateConstantBuffer(device, sizeof(FrameConstants));
        _objectBuffer = CreateConstantBuffer(device, sizeof(ObjectConstants));

        // Nothing is culled: an STL's winding cannot be trusted, and the cut
        // preview draws the back faces as the cap.
        _solid = CreateRasterizer(device, D3D11_FILL_MODE.D3D11_FILL_SOLID, 0, 0);
        _wireframe = CreateRasterizer(device, D3D11_FILL_MODE.D3D11_FILL_WIREFRAME, 0, 0);

        // Edges are pulled towards the eye (reversed depth: larger is nearer), so
        // they win against the surface they lie on.
        _edges = CreateRasterizer(device, D3D11_FILL_MODE.D3D11_FILL_WIREFRAME, 64, 1.5f);

        _depthWrite = CreateDepth(device, enable: true, write: true);
        _depthRead = CreateDepth(device, enable: true, write: false);
        _depthOff = CreateDepth(device, enable: false, write: false);
        _alphaBlend = CreateAlphaBlend(device);
        _oitBlend = CreateOitBlend(device);

        _box = LineGeometry.Create(device, UnitBox(), D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_LINELIST);
        _planeSurface = LineGeometry.Create(device,
            [new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0)],
            D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        _planeOutline = LineGeometry.Create(device,
        [
            new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0),
            new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0), new(-0.5f, -0.5f, 0),
            new(-0.5f, 0, 0), new(0.5f, 0, 0), new(0, -0.5f, 0), new(0, 0.5f, 0),
        ], D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_LINELIST);
    }

    /// <summary>
    /// Starts drawing a pane: sets its rectangle and the camera, the light and the
    /// near plane, which is fitted to what the pane shows.
    /// </summary>
    public void BeginPane(PaneRect pane, in OrbitCamera camera, Appearance appearance, BoundingBox scene)
    {
        _pane = pane;
        _gpu.SetViewport(pane.X, pane.Y, pane.Width, pane.Height);
        var center = scene.IsEmpty ? camera.Target : scene.Center;
        var radius = scene.IsEmpty ? camera.Distance : MathF.Max(scene.Size.Length() * 0.5f, 1e-3f);
        var near = camera.NearPlane(center, radius);
        var lighting = appearance.Lighting;
        _frame = new FrameConstants
        {
            ViewProjection = camera.View * OrbitCamera.Projection(pane.Aspect, near),
            CameraPosition = new Vector4(camera.Position, 1),
            Ambient = new Vector4(appearance.AmbientColor, 0),
            MainDirection = new Vector4(appearance.MainDirection, 0),
            FillDirection = new Vector4(lighting.Fill.Direction, 0),
            RimDirection = new Vector4(lighting.Rim.Direction, 0),
            MainColor = new Vector4(appearance.MainEnabled ? lighting.Main.Color : Vector3.Zero, 0),
            FillColor = new Vector4(appearance.FillEnabled ? lighting.Fill.Color : Vector3.Zero, 0),
            RimColor = new Vector4(appearance.RimEnabled ? lighting.Rim.Color : Vector3.Zero, 0),
            View = new Vector4(camera.Distance, 0, 0, 0),
        };
        Upload(_frameBuffer, _frame);
    }

    /// <summary>
    /// Draws a model as the appearance settings say: solid, with edges, or as a
    /// wireframe; optionally cut by <paramref name="clip"/> (the GPU preview) or
    /// coloured by <paramref name="distances"/>.
    /// </summary>
    public void DrawModel(GpuMesh mesh, Vector3 offset, Appearance appearance, Plane? clip = null,
        GpuDistances? distances = null, float tolerance = 0, float maximum = 0)
    {
        var shading = appearance.Shading;
        var color = appearance.Color;
        var constants = new ObjectConstants
        {
            World = Matrix4x4.CreateTranslation(offset),
            Color = new Vector4(color.Color, 1),
            EdgeColor = new Vector4(color.EdgeColor, 1),
            ClipPlane = clip is { } plane ? new Vector4(plane.Normal, plane.D) : Vector4.Zero,
            Material = new Vector4(shading.SpecularStrength, shading.Shininess, tolerance, maximum),
            Flags = (clip is null ? 0 : FlagClip) | (shading.FlatShading ? FlagFlat : 0) | (distances is null ? 0 : FlagHeatmap),
        };

        var context = _gpu.Context;
        BindMeshPipeline(_meshPs);
        SetBlend(null);
        context->OMSetDepthStencilState(_depthWrite, 0);
        var view = distances is null ? null : distances.View;
        context->PSSetShaderResources(0, 1, &view);

        if (shading.Wireframe)
        {
            constants.Flags |= FlagUnlit;
            context->RSSetState(_wireframe);
            DrawChunks(mesh, constants);
        }
        else
        {
            context->RSSetState(_solid);
            DrawChunks(mesh, constants);
            if (shading.RenderEdges)
            {
                constants.Flags |= FlagUnlit;
                context->RSSetState(_edges);
                DrawChunks(mesh, constants);
            }
        }

        view = null;
        context->PSSetShaderResources(0, 1, &view);
    }

    /// <summary>
    /// Draws several models see-through over each other, each in its own colour,
    /// blended without sorting, then lays the result over the pane.
    /// </summary>
    public void DrawOverlay(ReadOnlySpan<ModelDraw> models, Appearance appearance)
    {
        if (models.IsEmpty)
        {
            return;
        }

        EnsureOitTargets(_pane.Width, _pane.Height);
        var context = _gpu.Context;
        var clearAccumulation = stackalloc float[4] { 0, 0, 0, 0 };
        var clearRevealage = stackalloc float[4] { 1, 0, 0, 0 };
        context->ClearRenderTargetView(_accumTarget, clearAccumulation);
        context->ClearRenderTargetView(_revealTarget, clearRevealage);
        var targets = stackalloc ID3D11RenderTargetView*[2] { _accumTarget, _revealTarget };
        context->OMSetRenderTargets(2, targets, null);
        _gpu.SetViewport(0, 0, _pane.Width, _pane.Height);

        BindMeshPipeline(_overlayPs);
        SetBlend(_oitBlend);
        context->OMSetDepthStencilState(_depthOff, 0);
        context->RSSetState(_solid);
        var shading = appearance.Shading;
        foreach (var model in models)
        {
            DrawChunks(model.Mesh, new ObjectConstants
            {
                World = Matrix4x4.CreateTranslation(model.Offset),
                Color = new Vector4(model.Color, Math.Clamp(model.Opacity, 0, 1)),
                ClipPlane = model.Clip is { } plane ? new Vector4(plane.Normal, plane.D) : Vector4.Zero,
                Material = new Vector4(MathF.Max(shading.SpecularStrength, 0.12f), MathF.Max(shading.Shininess, 12), 0, 0),
                Flags = (shading.FlatShading ? FlagFlat : 0) | (model.Clip is null ? 0 : FlagClip),
            });
        }

        // Back into the pane, over whatever was drawn there before.
        _gpu.BindScene();
        _gpu.SetViewport(_pane.X, _pane.Y, _pane.Width, _pane.Height);
        Upload(_objectBuffer, new ObjectConstants { PaneOrigin = new Vector2(_pane.X, _pane.Y) });
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(_compositeVs, null, 0);
        context->PSSetShader(_compositePs, null, 0);
        var buffers = stackalloc ID3D11Buffer*[2] { _frameBuffer, _objectBuffer };
        context->PSSetConstantBuffers(0, 2, buffers);
        var views = stackalloc ID3D11ShaderResourceView*[2] { _accumView, _revealView };
        context->PSSetShaderResources(0, 2, views);
        SetBlend(_alphaBlend);
        context->OMSetDepthStencilState(_depthOff, 0);
        context->Draw(3, 0);
        views[0] = views[1] = null;
        context->PSSetShaderResources(0, 2, views);
    }

    /// <summary>The wireframe box around <paramref name="bounds"/>, in world space.</summary>
    public void DrawBox(BoundingBox bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        // A model flat along one axis would otherwise get a singular scale.
        var size = Vector3.Max(bounds.Size, new Vector3(1e-4f));
        DrawFlat(_box, Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(bounds.Center), BoxColor, _depthWrite, null);
    }

    /// <summary>
    /// The printer's bed under a model: centred beneath its footprint, at the
    /// height of its lowest point, which is what a slicer shows after import.
    /// </summary>
    public void DrawPlate(BuildPlate plate, BoundingBox bounds, bool fits)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        EnsurePlate(plate);
        var center = bounds.Center;
        var world = Matrix4x4.CreateTranslation(center.X, center.Y, bounds.Min.Z);
        DrawFlat(_plateSurface!, world, PlateSurfaceColor, _depthWrite, null);
        DrawFlat(_plateGrid!, world, PlateGridColor, _depthWrite, null);
        DrawFlat(_plateOutline!, world, fits ? PlateFitColor : PlateOverrunColor, _depthWrite, null);
    }

    /// <summary>The cutting plane: a translucent square with an outline, <paramref name="side"/> across.</summary>
    public void DrawCutPlane(Matrix4x4 transform, float side)
    {
        var world = Matrix4x4.CreateScale(side, side, 1) * transform;
        DrawFlat(_planeSurface, world, CutColor with { W = 0.12f }, _depthRead, _alphaBlend);
        DrawFlat(_planeOutline, world, CutColor, _depthWrite, null);
    }

    public void Dispose()
    {
        ReleaseOitTargets();
        _box.Dispose();
        _planeSurface.Dispose();
        _planeOutline.Dispose();
        _plateSurface?.Dispose();
        _plateGrid?.Dispose();
        _plateOutline?.Dispose();
        Release(ref _alphaBlend);
        Release(ref _oitBlend);
        Release(ref _depthWrite);
        Release(ref _depthRead);
        Release(ref _depthOff);
        Release(ref _solid);
        Release(ref _wireframe);
        Release(ref _edges);
        Release(ref _frameBuffer);
        Release(ref _objectBuffer);
        Release(ref _meshLayout);
        Release(ref _lineLayout);
        Release(ref _meshVs);
        Release(ref _lineVs);
        Release(ref _compositeVs);
        Release(ref _meshPs);
        Release(ref _overlayPs);
        Release(ref _linePs);
        Release(ref _compositePs);
    }

    private void BindMeshPipeline(ID3D11PixelShader* pixelShader)
    {
        var context = _gpu.Context;
        context->IASetInputLayout(_meshLayout);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(_meshVs, null, 0);
        context->PSSetShader(pixelShader, null, 0);
        var buffers = stackalloc ID3D11Buffer*[2] { _frameBuffer, _objectBuffer };
        context->VSSetConstantBuffers(0, 2, buffers);
        context->PSSetConstantBuffers(0, 2, buffers);
    }

    private void DrawChunks(GpuMesh mesh, ObjectConstants constants)
    {
        if (mesh.IsDisposed)
        {
            return;
        }

        var context = _gpu.Context;
        var strides = stackalloc uint[2] { 12, 12 };
        var offsets = stackalloc uint[2] { 0, 0 };
        var streams = stackalloc ID3D11Buffer*[2];
        if (mesh.IsIndexed)
        {
            context->IASetIndexBuffer(mesh.Indices, DXGI_FORMAT.DXGI_FORMAT_R32_UINT, 0);
        }

        foreach (var chunk in mesh.Chunks)
        {
            constants.PrimitiveBase = (uint)chunk.FirstTriangle;
            Upload(_objectBuffer, constants);
            streams[0] = chunk.Positions;
            streams[1] = chunk.Normals;
            context->IASetVertexBuffers(0, 2, streams, strides, offsets);
            if (mesh.IsIndexed)
            {
                context->DrawIndexed((uint)mesh.IndexCount, 0, 0);
            }
            else
            {
                context->Draw((uint)chunk.VertexCount, 0);
            }
        }
    }

    private void DrawFlat(LineGeometry geometry, Matrix4x4 world, Vector4 color, ID3D11DepthStencilState* depth, ID3D11BlendState* blend)
    {
        var context = _gpu.Context;
        Upload(_objectBuffer, new ObjectConstants { World = world, Color = color });
        context->IASetInputLayout(_lineLayout);
        context->IASetPrimitiveTopology(geometry.Topology);
        context->VSSetShader(_lineVs, null, 0);
        context->PSSetShader(_linePs, null, 0);
        var buffers = stackalloc ID3D11Buffer*[2] { _frameBuffer, _objectBuffer };
        context->VSSetConstantBuffers(0, 2, buffers);
        context->PSSetConstantBuffers(0, 2, buffers);
        context->RSSetState(_solid);
        context->OMSetDepthStencilState(depth, 0);
        SetBlend(blend);
        var stride = 12u;
        var offset = 0u;
        var vertices = geometry.Buffer;
        context->IASetVertexBuffers(0, 1, &vertices, &stride, &offset);
        context->Draw((uint)geometry.Count, 0);
    }

    /// <summary>
    /// Sets a blend state (null: none). The blend factor goes along always: the
    /// binding checks it is four values, and none of the states here reads it.
    /// </summary>
    private void SetBlend(ID3D11BlendState* state)
    {
        ReadOnlySpan<float> factor = [1, 1, 1, 1];
        _gpu.Context->OMSetBlendState(state, factor, 0xFFFFFFFF);
    }

    private void Upload<T>(ID3D11Buffer* buffer, in T value) where T : unmanaged
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Hr.Check(_gpu.Context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(T*)mapped.pData = value;
        _gpu.Context->Unmap((ID3D11Resource*)buffer, 0);
    }

    /// <summary>
    /// The bed surface, a 10 mm grid and an outline with the 50 mm lines, in
    /// plate-local coordinates centred on the origin, so placing it is one translate.
    /// </summary>
    private void EnsurePlate(BuildPlate plate)
    {
        if (ReferenceEquals(plate, _plate))
        {
            return;
        }

        _plateSurface?.Dispose();
        _plateGrid?.Dispose();
        _plateOutline?.Dispose();
        var hw = plate.Width * 0.5f;
        var hd = plate.Depth * 0.5f;

        // Just below the lines: coplanar, the two would z-fight.
        const float Drop = -0.05f;
        _plateSurface = LineGeometry.Create(_gpu.Device,
            [new(-hw, -hd, Drop), new(hw, -hd, Drop), new(hw, hd, Drop), new(-hw, -hd, Drop), new(hw, hd, Drop), new(-hw, hd, Drop)],
            D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

        var grid = new List<Vector3>();
        var outline = new List<Vector3>
        {
            new(-hw, -hd, 0), new(hw, -hd, 0), new(hw, -hd, 0), new(hw, hd, 0),
            new(hw, hd, 0), new(-hw, hd, 0), new(-hw, hd, 0), new(-hw, -hd, 0),
        };

        // Counted rather than accumulated: adding a float step repeatedly drifts
        // enough over 350 mm to put the last line visibly off the grid.
        for (var i = 1; i * 10f < plate.Width; i++)
        {
            var x = (i * 10f) - hw;
            (i % 5 == 0 ? outline : grid).AddRange([new(x, -hd, 0), new(x, hd, 0)]);
        }

        for (var i = 1; i * 10f < plate.Depth; i++)
        {
            var y = (i * 10f) - hd;
            (i % 5 == 0 ? outline : grid).AddRange([new(-hw, y, 0), new(hw, y, 0)]);
        }

        _plateGrid = LineGeometry.Create(_gpu.Device, [.. grid], D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_LINELIST);
        _plateOutline = LineGeometry.Create(_gpu.Device, [.. outline], D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_LINELIST);
        _plate = plate;
    }

    private void EnsureOitTargets(int width, int height)
    {
        if (_accumTarget != null && width <= _oitWidth && height <= _oitHeight)
        {
            return;
        }

        ReleaseOitTargets();
        _oitWidth = Math.Max(width, 64);
        _oitHeight = Math.Max(height, 64);
        CreateTarget(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, out _accumTexture, out _accumTarget, out _accumView);
        CreateTarget(DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT, out _revealTexture, out _revealTarget, out _revealView);

        void CreateTarget(DXGI_FORMAT format, out ID3D11Texture2D* texture, out ID3D11RenderTargetView* target, out ID3D11ShaderResourceView* view)
        {
            D3D11_TEXTURE2D_DESC desc = default;
            desc.Width = (uint)_oitWidth;
            desc.Height = (uint)_oitHeight;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = format;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
            ID3D11Texture2D* t;
            Hr.Check(_gpu.Device->CreateTexture2D(&desc, null, &t), "CreateTexture2D (overlay)");
            ID3D11RenderTargetView* rtv;
            Hr.Check(_gpu.Device->CreateRenderTargetView((ID3D11Resource*)t, null, &rtv), "CreateRenderTargetView (overlay)");
            ID3D11ShaderResourceView* srv;
            Hr.Check(_gpu.Device->CreateShaderResourceView((ID3D11Resource*)t, null, &srv), "CreateShaderResourceView (overlay)");
            texture = t;
            target = rtv;
            view = srv;
        }
    }

    private void ReleaseOitTargets()
    {
        Release(ref _accumView);
        Release(ref _revealView);
        Release(ref _accumTarget);
        Release(ref _revealTarget);
        Release(ref _accumTexture);
        Release(ref _revealTexture);
        _oitWidth = _oitHeight = 0;
    }

    private static Vector3[] UnitBox()
    {
        var corners = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            corners[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
        }

        int[] edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        return [.. edges.Select(i => corners[i])];
    }

    private static void Release<T>(ref T* resource) where T : unmanaged
    {
        if (resource != null)
        {
            ((Windows.Win32.System.Com.IUnknown*)resource)->Release();
            resource = null;
        }
    }

    private static ID3D11VertexShader* CreateVertexShader(ID3D11Device* device, ReadOnlySpan<byte> code)
    {
        ID3D11VertexShader* shader;
        fixed (byte* bytes = code)
        {
            Hr.Check(device->CreateVertexShader(bytes, (nuint)code.Length, null, &shader), "CreateVertexShader");
        }

        return shader;
    }

    private static ID3D11PixelShader* CreatePixelShader(ID3D11Device* device, ReadOnlySpan<byte> code)
    {
        ID3D11PixelShader* shader;
        fixed (byte* bytes = code)
        {
            Hr.Check(device->CreatePixelShader(bytes, (nuint)code.Length, null, &shader), "CreatePixelShader");
        }

        return shader;
    }

    private static ID3D11InputLayout* CreateLayout(ID3D11Device* device, ReadOnlySpan<byte> code, bool twoStreams)
    {
        var elements = stackalloc D3D11_INPUT_ELEMENT_DESC[2];
        fixed (byte* position = "POSITION"u8)
        fixed (byte* normal = "NORMAL"u8)
        fixed (byte* bytes = code)
        {
            elements[0] = new D3D11_INPUT_ELEMENT_DESC
            {
                SemanticName = new Windows.Win32.Foundation.PCSTR(position),
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                InputSlot = 0,
                InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA,
            };
            elements[1] = new D3D11_INPUT_ELEMENT_DESC
            {
                SemanticName = new Windows.Win32.Foundation.PCSTR(normal),
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                InputSlot = 1,
                InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA,
            };
            ID3D11InputLayout* layout;
            Hr.Check(device->CreateInputLayout(elements, twoStreams ? 2u : 1u, bytes, (nuint)code.Length, &layout), "CreateInputLayout");
            return layout;
        }
    }

    private static ID3D11Buffer* CreateConstantBuffer(ID3D11Device* device, int size)
    {
        D3D11_BUFFER_DESC desc = default;
        desc.ByteWidth = (uint)size;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE;
        ID3D11Buffer* buffer;
        Hr.Check(device->CreateBuffer(&desc, null, &buffer), "CreateBuffer (constants)");
        return buffer;
    }

    private static ID3D11RasterizerState* CreateRasterizer(ID3D11Device* device, D3D11_FILL_MODE fill, int depthBias, float slopeBias)
    {
        D3D11_RASTERIZER_DESC desc = default;
        desc.FillMode = fill;
        desc.CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE;
        desc.DepthClipEnable = true;
        desc.ScissorEnable = true;
        desc.MultisampleEnable = true;
        desc.DepthBias = depthBias;
        desc.SlopeScaledDepthBias = slopeBias;
        ID3D11RasterizerState* state;
        Hr.Check(device->CreateRasterizerState(&desc, &state), "CreateRasterizerState");
        return state;
    }

    private static ID3D11DepthStencilState* CreateDepth(ID3D11Device* device, bool enable, bool write)
    {
        D3D11_DEPTH_STENCIL_DESC desc = default;
        desc.DepthEnable = enable;
        desc.DepthWriteMask = write ? D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL : D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO;
        desc.DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_GREATER_EQUAL; // reversed depth
        ID3D11DepthStencilState* state;
        Hr.Check(device->CreateDepthStencilState(&desc, &state), "CreateDepthStencilState");
        return state;
    }

    private static ID3D11BlendState* CreateAlphaBlend(ID3D11Device* device)
    {
        D3D11_BLEND_DESC desc = default;
        ref var target = ref desc.RenderTarget._0;
        target.BlendEnable = true;
        target.SrcBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA;
        target.DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
        target.BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        target.SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE;
        target.DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
        target.BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        target.RenderTargetWriteMask = WriteAll;
        ID3D11BlendState* state;
        Hr.Check(device->CreateBlendState(&desc, &state), "CreateBlendState");
        return state;
    }

    /// <summary>Accumulation adds up; revealage multiplies by one minus each surface's opacity.</summary>
    private static ID3D11BlendState* CreateOitBlend(ID3D11Device* device)
    {
        D3D11_BLEND_DESC desc = default;
        desc.IndependentBlendEnable = true;
        ref var accumulation = ref desc.RenderTarget._0;
        accumulation.BlendEnable = true;
        accumulation.SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE;
        accumulation.DestBlend = D3D11_BLEND.D3D11_BLEND_ONE;
        accumulation.BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        accumulation.SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE;
        accumulation.DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE;
        accumulation.BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        accumulation.RenderTargetWriteMask = WriteAll;
        ref var revealage = ref desc.RenderTarget._1;
        revealage.BlendEnable = true;
        revealage.SrcBlend = D3D11_BLEND.D3D11_BLEND_ZERO;
        revealage.DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR;
        revealage.BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        revealage.SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ZERO;
        revealage.DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
        revealage.BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        revealage.RenderTargetWriteMask = WriteAll;
        ID3D11BlendState* state;
        Hr.Check(device->CreateBlendState(&desc, &state), "CreateBlendState (overlay)");
        return state;
    }
}

/// <summary>A small, immutable run of positions: lines or flat triangles.</summary>
internal sealed unsafe class LineGeometry : IDisposable
{
    private ID3D11Buffer* _buffer;

    private LineGeometry(ID3D11Buffer* buffer, int count, D3D_PRIMITIVE_TOPOLOGY topology)
    {
        _buffer = buffer;
        Count = count;
        Topology = topology;
    }

    public ID3D11Buffer* Buffer => _buffer;

    public int Count { get; }

    public D3D_PRIMITIVE_TOPOLOGY Topology { get; }

    public static LineGeometry Create(ID3D11Device* device, ReadOnlySpan<Vector3> vertices, D3D_PRIMITIVE_TOPOLOGY topology)
    {
        if (vertices.IsEmpty)
        {
            vertices = new Vector3[2];
        }

        return new LineGeometry(
            GpuMesh.CreateBuffer(device, MemoryMarshal.AsBytes(vertices), D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER),
            vertices.Length,
            topology);
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            _buffer->Release();
            _buffer = null;
        }
    }
}
