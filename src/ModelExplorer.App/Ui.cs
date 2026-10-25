using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using Hexa.NET.ImGuizmo;
using ModelExplorer.App.Render;
using Windows.Win32.Foundation;

namespace ModelExplorer.App;

/// <summary>
/// Dear ImGui on top of the window's swap chain. Setting it up costs some 80 ms
/// (the native libraries, a font, and the backend compiling its shaders), so it
/// happens in two steps: the constructor does the expensive part on the device
/// thread as soon as the device exists; <see cref="AttachWindow"/> finishes on the
/// UI thread. Between the two nothing else may use ImGui.
/// </summary>
internal sealed unsafe class Ui : IDisposable
{
    private readonly ImGuiContextPtr _context;

    public Ui(GraphicsDevice gpu, double dpiScale, string layoutFile)
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuizmo.SetImGuiContext(_context);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        // ImGui keeps the panel layout in this file and holds on to the pointer,
        // so the string is never freed.
        HasSavedLayout = File.Exists(layoutFile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(layoutFile)!);
            io.IniFilename = (byte*)Marshal.StringToCoTaskMemUTF8(layoutFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io.IniFilename = null;
        }

        io.LogFilename = null;
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (File.Exists(font))
        {
            io.Fonts.AddFontFromFileTTF(font, (float)Math.Round(16 * dpiScale));
        }

        ApplyStyle((float)dpiScale);

        // Device calls only, which Direct3D allows from any thread.
        ImGuiImplD3D11.SetCurrentContext(_context);
        ImGuiImplD3D11.Init(new ID3D11DevicePtr((ID3D11Device*)gpu.Device), new ID3D11DeviceContextPtr((ID3D11DeviceContext*)gpu.Context));
        ImGuiImplD3D11.CreateDeviceObjects();
    }

    /// <summary>True if a panel layout from an earlier run was found; otherwise the window builds the default one.</summary>
    public bool HasSavedLayout { get; }

    public bool WantsMouse => ImGui.GetIO().WantCaptureMouse;

    public bool WantsKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    /// <summary>Connects ImGui to the window's input. UI thread.</summary>
    public void AttachWindow(HWND window)
    {
        ImGui.SetCurrentContext(_context);
        ImGuiImplWin32.SetCurrentContext(_context);
        ImGuiImplWin32.Init(window.Value);
    }

    /// <summary>
    /// For tests: tells ImGui where the mouse is and, with <paramref name="action"/>
    /// 1/2 or 3/4, that the left or right button went down or up.
    /// </summary>
    public void TestMouse(float x, float y, int action)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(x, y);
        if (action is >= 1 and <= 4)
        {
            io.AddMouseButtonEvent(action <= 2 ? 0 : 1, action is 1 or 3);
        }
    }

    /// <summary>Feeds a window message to ImGui. True if ImGui consumed it.</summary>
    public bool HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam) =>
        ImGuiImplWin32.WndProcHandler((nint)window.Value, message, wParam.Value, lParam.Value) != 0;

    /// <summary>
    /// Writes the panel layout now. ImGui writes it by itself only in a frame drawn
    /// seconds after a change, and this app draws no frames while nothing happens.
    /// </summary>
    public void SaveLayout()
    {
        ImGui.SetCurrentContext(_context);
        var file = ImGui.GetIO().IniFilename;
        if (file != null)
        {
            ImGui.SaveIniSettingsToDisk(file);
        }
    }

    public void NewFrame()
    {
        ImGuiImplD3D11.NewFrame();
        ImGuiImplWin32.NewFrame();
        ImGui.NewFrame();
        ImGuizmo.BeginFrame();
    }

    /// <summary>Draws what was built since <see cref="NewFrame"/> into the bound render target.</summary>
    public void Render()
    {
        ImGui.Render();
        ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
    }

    public void Dispose()
    {
        ImGuiImplD3D11.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyContext(_context);
    }

    /// <summary>The dark theme, a little flatter and more compact than ImGui's own.</summary>
    private static void ApplyStyle(float scale)
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.FrameRounding = 3;
        style.PopupRounding = 3;
        style.GrabRounding = 3;
        style.TabRounding = 3;
        style.WindowBorderSize = 0;
        style.FrameBorderSize = 0;
        style.WindowPadding = new Vector2(8, 6);
        style.FramePadding = new Vector2(6, 3);
        style.ItemSpacing = new Vector2(6, 5);
        style.ScaleAllSizes(scale);

        var colors = style.Colors;
        colors[(int)ImGuiCol.WindowBg] = Rgb(0x21, 0x21, 0x21);
        colors[(int)ImGuiCol.ChildBg] = Rgb(0x21, 0x21, 0x21);
        colors[(int)ImGuiCol.PopupBg] = Rgb(0x26, 0x26, 0x26, 0.98f);
        colors[(int)ImGuiCol.MenuBarBg] = Rgb(0x19, 0x19, 0x19);
        colors[(int)ImGuiCol.TitleBg] = Rgb(0x19, 0x19, 0x19);
        colors[(int)ImGuiCol.TitleBgActive] = Rgb(0x19, 0x19, 0x19);
        colors[(int)ImGuiCol.Border] = Rgb(0x2E, 0x2E, 0x2E);
        colors[(int)ImGuiCol.FrameBg] = Rgb(0x2C, 0x2C, 0x2C);
        colors[(int)ImGuiCol.FrameBgHovered] = Rgb(0x36, 0x36, 0x36);
        colors[(int)ImGuiCol.FrameBgActive] = Rgb(0x40, 0x40, 0x40);
        colors[(int)ImGuiCol.Header] = Rgb(0x2A, 0x5A, 0x80);
        colors[(int)ImGuiCol.HeaderHovered] = Rgb(0x30, 0x68, 0x94);
        colors[(int)ImGuiCol.HeaderActive] = Rgb(0x36, 0x74, 0xA6);
        colors[(int)ImGuiCol.Button] = Rgb(0x30, 0x30, 0x30);
        colors[(int)ImGuiCol.ButtonHovered] = Rgb(0x3C, 0x3C, 0x3C);
        colors[(int)ImGuiCol.ButtonActive] = Rgb(0x2A, 0x5A, 0x80);
        colors[(int)ImGuiCol.CheckMark] = Rgb(0x42, 0xA5, 0xF5);
        colors[(int)ImGuiCol.SliderGrab] = Rgb(0x42, 0xA5, 0xF5);
        colors[(int)ImGuiCol.SliderGrabActive] = Rgb(0x64, 0xB5, 0xF6);
        colors[(int)ImGuiCol.Tab] = Rgb(0x19, 0x19, 0x19);
        colors[(int)ImGuiCol.TabHovered] = Rgb(0x30, 0x68, 0x94);
        colors[(int)ImGuiCol.TabSelected] = Rgb(0x21, 0x21, 0x21);
        colors[(int)ImGuiCol.TabDimmed] = Rgb(0x19, 0x19, 0x19);
        colors[(int)ImGuiCol.TabDimmedSelected] = Rgb(0x21, 0x21, 0x21);
        colors[(int)ImGuiCol.DockingPreview] = Rgb(0x42, 0xA5, 0xF5, 0.6f);
        colors[(int)ImGuiCol.TextDisabled] = Rgb(0x9E, 0x9E, 0x9E);
    }

    private static Vector4 Rgb(int red, int green, int blue, float alpha = 1) => new(red / 255f, green / 255f, blue / 255f, alpha);
}
