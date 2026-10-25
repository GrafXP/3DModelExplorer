using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ModelExplorer.App.Render;
using ModelExplorer.App.Shell;
using ModelExplorer.App.Thumbnails;
using ModelExplorer.Core;
using ModelExplorer.Core.Library;
using ModelExplorer.Core.Models;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Geometry;
using ModelExplorer.Indexing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ModelExplorer.App;

/// <summary>
/// The application window.
/// </summary>
/// <remarks>
/// <para>Start-up runs three things at once: this thread creates the window, a
/// second creates the Direct3D device and prepares the UI (the slowest step), and
/// the index is opened on the thread pool. Models named on the command line start
/// loading before the window exists.</para>
/// <para>Nothing is drawn unless something changed. Every change invalidates the
/// window, and WM_PAINT — which Windows delivers once the input queue is empty —
/// renders one frame. An idle window costs no CPU at all.</para>
/// </remarks>
internal sealed unsafe partial class MainWindow
{
    private const uint WM_APP_RUN = PInvoke.WM_APP + 1;
    private const uint WM_APP_DEVICE = PInvoke.WM_APP + 2;
    private const uint WM_APP_UI = PInvoke.WM_APP + 3;

    /// <summary>
    /// Sent by UI tests: the mouse is at this client position (x in the low word of
    /// lParam, y in the high word) and wParam says what it does: 0 moves, 1/2 left
    /// button down/up, 3/4 right button down/up. Posted mouse messages do not do: with
    /// the real mouse elsewhere, ImGui follows the real one.
    /// </summary>
    private const uint WM_APP_TEST_MOUSE = PInvoke.WM_APP + 20;
    private const nuint NoticeTimerId = 1;

    private static MainWindow? s_instance;

    /// <summary>Set once an unexpected error has been reported; from then on the window only closes.</summary>
    private static bool s_failed;

    /// <summary>Set once the window is being destroyed and its resources released.</summary>
    private static bool s_closed;

    private readonly Settings _settings;
    private readonly List<string> _startupFiles;
    private readonly GeometryLoaderRegistry _loaders = GeometryLoaderRegistry.CreateDefault();
    private readonly ModelCache _cache;
    private readonly Workspace _workspace;
    private readonly ModelLibrary _library;
    private readonly ResultSelection _selection = new();
    private readonly ManualResetEventSlim _deviceReady = new();
    private UiDispatcher? _dispatcher;

    private HWND _hwnd;
    private nint _hwndForWorkers;
    private int _clientWidth, _clientHeight;
    private double _dpiScale = 1;
    private bool _presented;
    private bool _fullscreen;
    private WINDOWPLACEMENT _savedPlacement;
    private nint _savedStyle;

    private GraphicsDevice? _gpu;
    private GraphicsDevice? _pendingGpu;

    /// <summary>The device as soon as it exists, for the loading threads; set once, never cleared.</summary>
    private volatile GraphicsDevice? _device;
    private SceneRenderer? _renderer;
    private SceneRenderer? _pendingRenderer;
    private Ui? _ui;
    private Ui? _pendingUi;
    private ThumbnailService? _thumbs;
    private string? _gpuError;

    private int _extraFrames;
    private long _lastFrame;
    private double _lastFrameMs;
    private string _notice = string.Empty;
    private long _noticeUntil;

    public MainWindow(IReadOnlyList<string> files, Settings settings)
    {
        _settings = settings;
        _startupFiles = [.. files];
        _cache = new ModelCache(_loaders.Load, PrepareMesh, ModelCache.DefaultBudget, Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
        _workspace = new Workspace(_cache, PrepareMesh, PrepareDistances);
        _library = new ModelLibrary(_loaders.SupportedExtensions, ModelIndexStore.DefaultPath);
        ApplySettings();
    }

    private string LayoutFile => Path.Combine(Path.GetDirectoryName(Settings.DefaultPath)!, "layout.ini");

    public int Run()
    {
        s_instance = this;
        _dispatcher = new UiDispatcher(() => Post(WM_APP_RUN));
        SynchronizationContext.SetSynchronizationContext(_dispatcher);

        PlanWindow(out var x, out var y, out var width, out var height, out var maximized);
        StartDeviceThread();
        CreateWindow(x, y, width, height);
        PInvoke.ShowWindow(_hwnd, maximized ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED : SHOW_WINDOW_CMD.SW_SHOW);

        _ = _library.InitializeAsync();
        if (_startupFiles.Count > 0)
        {
            OpenFiles(_startupFiles, add: false);
        }

        // Whatever the device thread finished before there was a window to tell.
        OnDeviceReady();
        OnUiReady();

        MSG message;
        while (PInvoke.GetMessage(&message, default, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }

        return 0;
    }

    // ------------------------------------------------------------------ window

    /// <summary>Where the window goes: where it was last time if that is still on a screen, otherwise centred on the screen under the mouse.</summary>
    private void PlanWindow(out int x, out int y, out int width, out int height, out bool maximized)
    {
        _dpiScale = PInvoke.GetDpiForSystem() / 96.0;
        var saved = new RECT
        {
            left = _settings.WindowX,
            top = _settings.WindowY,
            right = _settings.WindowX + _settings.WindowWidth,
            bottom = _settings.WindowY + _settings.WindowHeight,
        };
        if (_settings.WindowWidth >= 400 && _settings.WindowHeight >= 300 &&
            !PInvoke.MonitorFromRect(&saved, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull)
        {
            (x, y, width, height) = (saved.left, saved.top, saved.right - saved.left, saved.bottom - saved.top);
            maximized = _settings.Maximized;
            return;
        }

        System.Drawing.Point cursor;
        PInvoke.GetCursorPos(&cursor);
        var monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(monitor, &info);
        var work = info.rcWork;
        width = (int)((work.right - work.left) * 0.85);
        height = (int)((work.bottom - work.top) * 0.88);
        x = work.left + ((work.right - work.left - width) / 2);
        y = work.top + ((work.bottom - work.top - height) / 2);
        maximized = false;
    }

    private void CreateWindow(int x, int y, int width, int height)
    {
        var instance = new HINSTANCE(PInvoke.GetModuleHandle((char*)null).Value);
        fixed (char* className = "ModelExplorer.Window")
        fixed (char* title = "3D Model Explorer")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_DBLCLKS,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW),

                // The viewport colour, so the window never flashes white before
                // Direct3D has drawn into it.
                hbrBackground = PInvoke.CreateSolidBrush(new COLORREF(0x001A1A1A)),
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(&windowClass);
            _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_ACCEPTFILES, className, title, WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                x, y, width, height, default, default, instance, null);
        }

        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException("The window could not be created.");
        }

        var dark = 1;
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
        RECT client;
        PInvoke.GetClientRect(_hwnd, &client);
        _clientWidth = client.right;
        _clientHeight = client.bottom;
        _dpiScale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
        Interlocked.Exchange(ref _hwndForWorkers, (nint)_hwnd.Value);
    }

    /// <summary>Wakes the UI thread from any thread. A no-op before the window exists; Run catches up.</summary>
    private void Post(uint message)
    {
        var window = Interlocked.CompareExchange(ref _hwndForWorkers, 0, 0);
        if (window != 0)
        {
            PInvoke.PostMessage(new HWND((void*)window), message, default, default);
        }
    }

    /// <summary>
    /// Asks for a frame because something changed. Two more follow: ImGui lays out a
    /// window that appears or changes size over the next frames, and without them it
    /// would stay unfinished until the next input.
    /// </summary>
    private void Invalidate()
    {
        if (_hwnd.IsNull)
        {
            return;
        }

        _extraFrames = Math.Max(_extraFrames, 2);
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    private void Notice(string text)
    {
        _notice = text;
        _noticeUntil = Environment.TickCount64 + 4000;
        PInvoke.SetTimer(_hwnd, NoticeTimerId, 4100, null);
        Invalidate();
    }

    private void UpdateTitle()
    {
        var active = _workspace.Requested(_workspace.Active);
        var title = _workspace.Count switch
        {
            0 => "3D Model Explorer",
            1 => $"{active?.Name} - 3D Model Explorer",
            var n => $"{active?.Name} and {n - 1} more - 3D Model Explorer",
        };
        fixed (char* text = title)
        {
            PInvoke.SetWindowText(_hwnd, text);
        }
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            PInvoke.GetWindowPlacement(_hwnd, &placement);
            _savedPlacement = placement;
            _savedStyle = PInvoke.GetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            PInvoke.GetMonitorInfo(PInvoke.MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), &info);
            PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, _savedStyle & ~(nint)WINDOW_STYLE.WS_OVERLAPPEDWINDOW);
            var r = info.rcMonitor;
            PInvoke.SetWindowPos(_hwnd, default, r.left, r.top, r.right - r.left, r.bottom - r.top,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }
        else
        {
            PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, _savedStyle);
            var placement = _savedPlacement;
            PInvoke.SetWindowPlacement(_hwnd, &placement);
            PInvoke.SetWindowPos(_hwnd, default, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }

        _fullscreen = !_fullscreen;
    }

    // ------------------------------------------------------------------ GPU

    private void StartDeviceThread()
    {
        var layout = LayoutFile;
        var dpi = _dpiScale;
        new Thread(() =>
        {
            GraphicsDevice gpu;
            try
            {
                gpu = GraphicsDevice.Create();
                Interlocked.Exchange(ref _pendingRenderer, new SceneRenderer(gpu));
                _device = gpu;
                Interlocked.Exchange(ref _pendingGpu, gpu);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                Interlocked.Exchange(ref _gpuError, ex.Message);
                return;
            }
            finally
            {
                _deviceReady.Set();
                Post(WM_APP_DEVICE);
            }

            // The UI is prepared here too, while the UI thread is still busy with
            // the window and the first loads.
            try
            {
                Interlocked.Exchange(ref _pendingUi, new Ui(gpu, dpi, layout));
                Post(WM_APP_UI);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                Interlocked.Exchange(ref _gpuError, $"The user interface could not start: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "Device" }.Start();
    }

    private void OnDeviceReady()
    {
        var gpu = Interlocked.Exchange(ref _pendingGpu, null);
        if (gpu is null || _gpu is not null || _hwnd.IsNull)
        {
            if (gpu is not null)
            {
                _pendingGpu = gpu; // the window is not there yet: Run takes it
            }

            if (_gpuError is { } error && _ui is null)
            {
                PInvoke.MessageBox(_hwnd, $"3D Model Explorer needs a Direct3D 11 graphics device.\n\n{error}", "3D Model Explorer",
                    MESSAGEBOX_STYLE.MB_ICONERROR | MESSAGEBOX_STYLE.MB_OK);
                PInvoke.PostQuitMessage(1);
            }

            return;
        }

        _gpu = gpu;
        _renderer = Interlocked.Exchange(ref _pendingRenderer, null);
        _gpu.AttachWindow(_hwnd, _clientWidth, _clientHeight);
        _thumbs = new ThumbnailService(_loaders, new ThumbnailCache(ThumbnailCache.DefaultDirectory), () => Post(WM_APP_RUN));
        Invalidate();
    }

    private void OnUiReady()
    {
        if (_hwnd.IsNull || _gpu is null)
        {
            return;
        }

        var ui = Interlocked.Exchange(ref _pendingUi, null);
        if (ui is null || _ui is not null)
        {
            return;
        }

        ui.AttachWindow(_hwnd);
        _ui = ui;
        Invalidate();
    }

    /// <summary>
    /// Uploads a parsed mesh, on the loading thread. The first loads can finish
    /// before the device exists; they wait for it here rather than on the UI thread.
    /// </summary>
    private IDisposable? PrepareMesh(MeshData mesh, CancellationToken token)
    {
        _deviceReady.Wait(token);
        return _device is { } gpu ? GpuMesh.Create(gpu.Device, mesh, token) : null;
    }

    private IDisposable? PrepareDistances(float[] distances)
    {
        _deviceReady.Wait();
        return _device is { } gpu && distances.Length > 0 ? GpuDistances.Create(gpu.Device, distances) : null;
    }

    // ------------------------------------------------------------------ frames

    private void OnPaint()
    {
        if (_gpu is null || !_gpu.HasSwapChain || _ui is null || _renderer is null)
        {
            // Nothing to draw with yet: let the class brush paint the background.
            PAINTSTRUCT paint;
            PInvoke.BeginPaint(_hwnd, &paint);
            PInvoke.EndPaint(_hwnd, &paint);
            return;
        }

        // Validate first: drawing may invalidate again to ask for a follow-up frame.
        PInvoke.ValidateRect(_hwnd, (RECT*)null);
        RenderFrame();
    }

    private void RenderFrame()
    {
        var gpu = _gpu!;
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastFrame == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastFrame, now);
        _lastFrame = now;
        _workspace.Appearance.Advance(elapsed);
        if (_thumbs?.Upload() == true)
        {
            _extraFrames = Math.Max(_extraFrames, 1);
        }

        _ui!.NewFrame();
        DrawUi();

        gpu.BeginScene(SceneRenderer.Background);
        RenderPanes();
        gpu.EndScene();
        _ui.Render();
        _lastFrameMs = Stopwatch.GetElapsedTime(now).TotalMilliseconds;
        gpu.Present();
        _presented = true;

        if (_deferred is { } action)
        {
            // What the UI asked for that must not happen in the middle of a frame:
            // dialogs, window changes.
            _deferred = null;
            action();
        }

        if (_workspace.Appearance.RotateMainLight)
        {
            PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
        }
        else if (_extraFrames > 0)
        {
            _extraFrames--;
            PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
        }
    }

    private Action? _deferred;

    // ------------------------------------------------------------------ messages

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        var window = s_instance;
        if (window is null || window._hwnd.IsNull || s_failed || s_closed)
        {
            // After a failure the message box below pumps messages; a paint that
            // failed once would fail again, inside it, for ever.
            if (s_failed && message == PInvoke.WM_PAINT)
            {
                PInvoke.ValidateRect(hwnd, (RECT*)null);
                return default;
            }

            return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }

        try
        {
            return window.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception must not unwind into the window manager.
            s_failed = true;
            CrashLog.Write(ex);
            PInvoke.MessageBox(hwnd, $"3D Model Explorer ran into an unexpected problem and has to close.\n\n{ex.Message}\n\nDetails are in crash.log under %LOCALAPPDATA%\\ModelExplorer.",
                "3D Model Explorer", MESSAGEBOX_STYLE.MB_ICONERROR | MESSAGEBOX_STYLE.MB_OK);
            PInvoke.PostQuitMessage(1);
            return default;
        }
    }

    private static int LowWord(nint value) => (short)(value & 0xFFFF);

    private static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);

    private static bool IsMouseMessage(uint message) => message is >= PInvoke.WM_MOUSEFIRST and <= PInvoke.WM_MOUSELAST;

    private static bool IsKeyMessage(uint message) => message is >= PInvoke.WM_KEYFIRST and <= PInvoke.WM_KEYLAST;

    private LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (_ui is not null)
        {
            if (_ui.HandleMessage(hwnd, message, wParam, lParam))
            {
                return new LRESULT(1);
            }

            var mouse = IsMouseMessage(message);
            var key = IsKeyMessage(message);
            if (mouse || key || message == PInvoke.WM_MOUSELEAVE)
            {
                Invalidate();
            }

            // Input over a panel, or typed into one, belongs to the panel. A drag
            // that started in a pane keeps going wherever the mouse goes.
            if (mouse && !IsDragging && (_ui.WantsMouse || _gizmoActive) && message != PInvoke.WM_LBUTTONUP &&
                message != PInvoke.WM_MBUTTONUP && message != PInvoke.WM_RBUTTONUP)
            {
                return default;
            }

            if (key && _ui.WantsKeyboard)
            {
                return default;
            }
        }

        switch (message)
        {
            case WM_APP_RUN:
                // Continuations to run, or finished thumbnails for the next frame to take in.
                _dispatcher!.Drain();
                Invalidate();
                return default;
            case WM_APP_DEVICE:
                OnDeviceReady();
                OnUiReady();
                return default;
            case WM_APP_UI:
                OnUiReady();
                return default;
            case WM_APP_TEST_MOUSE:
            {
                int x = LowWord(lParam.Value), y = HighWord(lParam.Value), action = (int)wParam.Value;
                _ui?.TestMouse(x, y, action);
                var overPanel = _ui is { WantsMouse: true } && !IsDragging;
                switch (action)
                {
                    case 1 or 3 when !overPanel:
                        OnMouseDown(action == 1 ? MouseButton.Left : MouseButton.Right, x, y, false, false);
                        break;
                    case 2 or 4:
                        OnMouseUp(action == 2 ? MouseButton.Left : MouseButton.Right, x, y);
                        break;
                    case 0:
                        OnMouseMove(x, y);
                        break;
                }

                Invalidate();
                return default;
            }

            case PInvoke.WM_PAINT:
                OnPaint();
                return default;
            case PInvoke.WM_ERASEBKGND:
                // Once Direct3D draws the window, erasing it first would only flicker.
                return _presented ? new LRESULT(1) : PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
            case PInvoke.WM_SIZE:
            {
                var width = LowWord(lParam.Value) & 0xFFFF;
                var height = HighWord(lParam.Value) & 0xFFFF;
                if (width > 0 && height > 0)
                {
                    _clientWidth = width;
                    _clientHeight = height;
                    _gpu?.Resize(width, height);
                    Invalidate();
                }

                return default;
            }

            case PInvoke.WM_GETMINMAXINFO:
            {
                var info = (MINMAXINFO*)lParam.Value;
                info->ptMinTrackSize = new System.Drawing.Point((int)(900 * _dpiScale), (int)(520 * _dpiScale));
                return default;
            }

            case PInvoke.WM_DPICHANGED:
            {
                _dpiScale = (wParam.Value & 0xFFFF) / 96.0;
                var suggested = (RECT*)lParam.Value;
                PInvoke.SetWindowPos(hwnd, default, suggested->left, suggested->top, suggested->right - suggested->left,
                    suggested->bottom - suggested->top, SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                return default;
            }

            case PInvoke.WM_TIMER:
                if (wParam.Value == NoticeTimerId)
                {
                    PInvoke.KillTimer(hwnd, NoticeTimerId);
                    Invalidate();
                }

                return default;

            case PInvoke.WM_MOUSEMOVE:
                OnMouseMove(LowWord(lParam.Value), HighWord(lParam.Value));
                return default;
            case PInvoke.WM_LBUTTONDOWN:
                OnMouseDown(MouseButton.Left, LowWord(lParam.Value), HighWord(lParam.Value), (wParam.Value & 0x0004) != 0, (wParam.Value & 0x0008) != 0);
                return default;
            case PInvoke.WM_MBUTTONDOWN:
                OnMouseDown(MouseButton.Middle, LowWord(lParam.Value), HighWord(lParam.Value), false, false);
                return default;
            case PInvoke.WM_RBUTTONDOWN:
                OnMouseDown(MouseButton.Right, LowWord(lParam.Value), HighWord(lParam.Value), false, false);
                return default;
            case PInvoke.WM_LBUTTONUP:
            case PInvoke.WM_MBUTTONUP:
            case PInvoke.WM_RBUTTONUP:
                OnMouseUp(message == PInvoke.WM_RBUTTONUP ? MouseButton.Right : message == PInvoke.WM_MBUTTONUP ? MouseButton.Middle : MouseButton.Left,
                    LowWord(lParam.Value), HighWord(lParam.Value));
                return default;
            case PInvoke.WM_LBUTTONDBLCLK:
                OnDoubleClick(LowWord(lParam.Value), HighWord(lParam.Value));
                return default;
            case PInvoke.WM_MOUSEWHEEL:
            {
                var point = new System.Drawing.Point(LowWord(lParam.Value), HighWord(lParam.Value));
                PInvoke.ScreenToClient(hwnd, &point);
                OnMouseWheel(HighWord((nint)wParam.Value), point.X, point.Y);
                return default;
            }

            case PInvoke.WM_CAPTURECHANGED:
                EndDrag();
                return default;

            case PInvoke.WM_KEYDOWN:
            case PInvoke.WM_SYSKEYDOWN when wParam.Value == (nuint)VIRTUAL_KEY.VK_F10:
                OnKeyDown((VIRTUAL_KEY)(ushort)wParam.Value,
                    shift: PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) < 0,
                    control: PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) < 0);
                return default;

            case PInvoke.WM_DROPFILES:
                OnDropFiles(new HDROP((void*)wParam.Value));
                return default;

            case PInvoke.WM_CLOSE:
                SaveSettings();
                _ui?.SaveLayout();
                PInvoke.DestroyWindow(hwnd);
                return default;
            case PInvoke.WM_DESTROY:
                Shutdown();
                PInvoke.PostQuitMessage(0);
                return default;
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void OnDropFiles(HDROP drop)
    {
        var count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)count);
        var buffer = stackalloc char[1024];
        for (uint i = 0; i < count; i++)
        {
            var length = PInvoke.DragQueryFile(drop, i, buffer, 1024);
            if (length > 0)
            {
                files.Add(new string(buffer, 0, (int)length));
            }
        }

        PInvoke.DragFinish(drop);

        // With Ctrl held, what is dropped joins what is shown. Asked directly: during
        // a drag from another program this window receives no keyboard input.
        OpenFiles(files, add: PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_CONTROL) < 0);
    }

    /// <summary>
    /// Releases everything, in dependency order: what holds GPU resources before the
    /// device. Messages that still arrive afterwards (WM_NCDESTROY) go straight to
    /// DefWindowProc; the UI they would otherwise reach is gone.
    /// </summary>
    private void Shutdown()
    {
        s_closed = true;
        _workspace.Dispose();
        _cache.Dispose();
        _library.Dispose();
        _thumbs?.Dispose();
        _renderer?.Dispose();
        _ui?.Dispose();

        // The device itself is left to the process's end, a moment away: a loading
        // thread that has not noticed its cancellation yet may still be creating
        // buffers on it, and must not find it released underneath.
        _thumbs = null;
        _renderer = null;
        _ui = null;
        _gpu = null;
    }
}
