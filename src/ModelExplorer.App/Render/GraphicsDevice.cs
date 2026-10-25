using System.Numerics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace ModelExplorer.App.Render;

/// <summary>Thrown when a Direct3D or DXGI call fails; the message names the call and the HRESULT.</summary>
internal sealed class GraphicsException(string call, int hresult) : Exception($"{call} failed with 0x{hresult:X8}");

internal static class Hr
{
    public static void Check(HRESULT hr, string call)
    {
        if (hr.Failed)
        {
            throw new GraphicsException(call, hr.Value);
        }
    }
}

/// <summary>
/// The Direct3D 11 device, the window's swap chain, and the multisampled target
/// the 3D panes are drawn into.
/// </summary>
/// <remarks>
/// The device is created without a window, on any thread — it is the slowest step
/// of start-up — and the swap chain is attached on the UI thread once the window
/// exists. The device is free-threaded: meshes are uploaded from the loading
/// threads. <see cref="Context"/> belongs to the UI thread.
///
/// The panes draw into a 4× multisampled colour and depth target, which is
/// resolved into the back buffer before the UI is drawn on top. Edges and thin
/// grid lines are most of what a faceted model's silhouette is made of; without
/// antialiasing they crawl whenever the view turns.
/// </remarks>
internal sealed unsafe class GraphicsDevice : IDisposable
{
    private const DXGI_FORMAT ColorFormat = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;

    private IDXGISwapChain1* _swapChain;
    private ID3D11Texture2D* _backTexture;
    private ID3D11RenderTargetView* _backBuffer;
    private ID3D11Texture2D* _sceneTexture;
    private ID3D11RenderTargetView* _sceneTarget;
    private ID3D11DepthStencilView* _sceneDepth;

    private GraphicsDevice()
    {
    }

    public ID3D11Device* Device { get; private set; }

    public ID3D11DeviceContext* Context { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int SampleCount { get; private set; } = 1;

    /// <summary>The software rasterizer stands in: no usable GPU, or a remote session without one.</summary>
    public bool IsSoftware { get; private set; }

    public string AdapterName { get; private set; } = string.Empty;

    public static GraphicsDevice Create()
    {
        var gpu = new GraphicsDevice();
        var level = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, PInvoke.D3D11_SDK_VERSION,
            &device, null, &context);
        if (hr.Failed)
        {
            // No usable GPU or driver: WARP keeps the viewer working, slowly.
            hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, default,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, PInvoke.D3D11_SDK_VERSION,
                &device, null, &context);
            gpu.IsSoftware = true;
        }

        Hr.Check(hr, "D3D11CreateDevice");
        gpu.Device = device;
        gpu.Context = context;

        // Required of every feature level 11 device for these formats; checked
        // anyway so an odd driver degrades to no antialiasing instead of failing.
        uint quality;
        if (device->CheckMultisampleQualityLevels(ColorFormat, 4, &quality).Succeeded && quality > 0)
        {
            gpu.SampleCount = 4;
        }

        gpu.AdapterName = ReadAdapterName(device);
        return gpu;
    }

    /// <summary>Creates the swap chain for the window. UI thread.</summary>
    public void AttachWindow(HWND window, int width, int height)
    {
        IDXGIDevice1* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        try
        {
            var iid = IDXGIDevice1.IID_Guid;
            Hr.Check(Device->QueryInterface(&iid, (void**)&dxgiDevice), "QueryInterface");
            Hr.Check(dxgiDevice->GetAdapter(&adapter), "GetAdapter");
            iid = IDXGIFactory2.IID_Guid;
            Hr.Check(adapter->GetParent(&iid, (void**)&factory), "GetParent");

            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            DXGI_SWAP_CHAIN_DESC1 desc = default;
            desc.Width = (uint)Width;
            desc.Height = (uint)Height;
            desc.Format = ColorFormat;
            desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 2;
            desc.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;
            IDXGISwapChain1* swapChain;
            Hr.Check(factory->CreateSwapChainForHwnd((IUnknown*)Device, window, &desc, null, null, &swapChain), "CreateSwapChainForHwnd");
            _swapChain = swapChain;
            factory->MakeWindowAssociation(window, DXGI_MWA_FLAGS.DXGI_MWA_NO_ALT_ENTER);

            // One frame queued at most: a drag must answer on the next refresh, not
            // three refreshes later.
            dxgiDevice->SetMaximumFrameLatency(1);
            CreateTargets();
        }
        finally
        {
            if (factory != null)
            {
                factory->Release();
            }

            if (adapter != null)
            {
                adapter->Release();
            }

            if (dxgiDevice != null)
            {
                dxgiDevice->Release();
            }
        }
    }

    public bool HasSwapChain => _swapChain != null;

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_swapChain == null || (width == Width && height == Height))
        {
            return;
        }

        Context->OMSetRenderTargets(0, null, null);
        ReleaseTargets();
        Hr.Check(_swapChain->ResizeBuffers(0, (uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0), "ResizeBuffers");
        Width = width;
        Height = height;
        CreateTargets();
    }

    /// <summary>Binds the scene target over the whole window and clears it to the viewport colour.</summary>
    public void BeginScene(Vector3 background)
    {
        BindScene();
        SetViewport(0, 0, Width, Height);
        var color = stackalloc float[4] { background.X, background.Y, background.Z, 1 };
        Context->ClearRenderTargetView(_sceneTarget, color);

        // Reversed depth: 0 is infinitely far.
        Context->ClearDepthStencilView(_sceneDepth, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 0f, 0);
    }

    /// <summary>Binds the scene target and its depth again, after drawing elsewhere.</summary>
    public void BindScene()
    {
        var target = _sceneTarget;
        Context->OMSetRenderTargets(1, &target, _sceneDepth);
    }

    /// <summary>Resolves the multisampled scene into the back buffer and binds that for the UI.</summary>
    public void EndScene()
    {
        Context->OMSetRenderTargets(0, null, null);
        Context->ResolveSubresource((ID3D11Resource*)_backTexture, 0, (ID3D11Resource*)_sceneTexture, 0, ColorFormat);
        var target = _backBuffer;
        Context->OMSetRenderTargets(1, &target, null);
        SetViewport(0, 0, Width, Height);
    }

    /// <summary>Limits drawing to a rectangle: the viewport, which clip space then spans, and the scissor.</summary>
    public void SetViewport(int x, int y, int width, int height)
    {
        var viewport = new D3D11_VIEWPORT { TopLeftX = x, TopLeftY = y, Width = Math.Max(1, width), Height = Math.Max(1, height), MaxDepth = 1 };
        Context->RSSetViewports(1, &viewport);
        var scissor = new RECT { left = x, top = y, right = x + width, bottom = y + height };
        Context->RSSetScissorRects(1, &scissor);
    }

    public void Present()
    {
        var hr = _swapChain->Present(1, 0);
        if (hr == HRESULT.DXGI_ERROR_DEVICE_REMOVED || hr == HRESULT.DXGI_ERROR_DEVICE_RESET)
        {
            Hr.Check(Device->GetDeviceRemovedReason(), "Present (device removed)");
        }
    }

    public void Dispose()
    {
        ReleaseTargets();
        if (_swapChain != null)
        {
            _swapChain->Release();
        }

        if (Context != null)
        {
            Context->ClearState();
            Context->Release();
        }

        if (Device != null)
        {
            Device->Release();
        }

        _swapChain = null;
        Context = null;
        Device = null;
    }

    private void CreateTargets()
    {
        ID3D11Texture2D* back;
        var iid = ID3D11Texture2D.IID_Guid;
        Hr.Check(_swapChain->GetBuffer(0, &iid, (void**)&back), "GetBuffer");
        _backTexture = back;
        ID3D11RenderTargetView* backView;
        Hr.Check(Device->CreateRenderTargetView((ID3D11Resource*)back, null, &backView), "CreateRenderTargetView");
        _backBuffer = backView;

        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = (uint)Width;
        desc.Height = (uint)Height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = ColorFormat;
        desc.SampleDesc.Count = (uint)SampleCount;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
        ID3D11Texture2D* scene;
        Hr.Check(Device->CreateTexture2D(&desc, null, &scene), "CreateTexture2D (scene)");
        _sceneTexture = scene;
        ID3D11RenderTargetView* sceneView;
        Hr.Check(Device->CreateRenderTargetView((ID3D11Resource*)scene, null, &sceneView), "CreateRenderTargetView (scene)");
        _sceneTarget = sceneView;

        desc.Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL;
        ID3D11Texture2D* depth;
        Hr.Check(Device->CreateTexture2D(&desc, null, &depth), "CreateTexture2D (depth)");
        ID3D11DepthStencilView* depthView;
        var hr = Device->CreateDepthStencilView((ID3D11Resource*)depth, null, &depthView);
        depth->Release(); // the view keeps it alive
        Hr.Check(hr, "CreateDepthStencilView");
        _sceneDepth = depthView;
    }

    private void ReleaseTargets()
    {
        if (_sceneDepth != null)
        {
            _sceneDepth->Release();
        }

        if (_sceneTarget != null)
        {
            _sceneTarget->Release();
        }

        if (_sceneTexture != null)
        {
            _sceneTexture->Release();
        }

        if (_backBuffer != null)
        {
            _backBuffer->Release();
        }

        if (_backTexture != null)
        {
            _backTexture->Release();
        }

        _sceneDepth = null;
        _sceneTarget = null;
        _sceneTexture = null;
        _backBuffer = null;
        _backTexture = null;
    }

    private static string ReadAdapterName(ID3D11Device* device)
    {
        IDXGIDevice1* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        try
        {
            var iid = IDXGIDevice1.IID_Guid;
            if (device->QueryInterface(&iid, (void**)&dxgiDevice).Failed || dxgiDevice->GetAdapter(&adapter).Failed)
            {
                return string.Empty;
            }

            DXGI_ADAPTER_DESC desc;
            return adapter->GetDesc(&desc).Succeeded ? desc.Description.ToString() : string.Empty;
        }
        finally
        {
            if (adapter != null)
            {
                adapter->Release();
            }

            if (dxgiDevice != null)
            {
                dxgiDevice->Release();
            }
        }
    }
}
