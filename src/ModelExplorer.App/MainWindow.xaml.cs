using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ModelExplorer.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow : Window
{
    // Win10 20H1 and later. Earlier builds used 19 for the same attribute.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Opts the non-client area into dark mode. WPF does not do this on its own,
    /// so without it the window keeps a light title bar over a dark client area.
    /// Applied during SourceInitialized — before the window is shown — so there
    /// is no visible light-to-dark flash on startup.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            // Pre-20H1 attribute id. Failure here is cosmetic only, so it is ignored.
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }
}
