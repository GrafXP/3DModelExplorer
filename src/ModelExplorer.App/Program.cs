using ModelExplorer.Core;
using Windows.Win32;

namespace ModelExplorer.App;

internal static class Program
{
    /// <summary>
    /// Files named on the command line are shown at once: "Open with", a shell file
    /// association, or several files dragged onto the executable. Full paths,
    /// resolved against the directory the app was started in.
    /// </summary>
    private static int Main(string[] args)
    {
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
            {
                continue;
            }

            try
            {
                files.Add(Path.GetFullPath(arg));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        unsafe
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2: crisp on every monitor, at any scale.
            PInvoke.SetProcessDpiAwarenessContext(new Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT((void*)-4));
        }

        return new MainWindow(files, Settings.Load(Settings.DefaultPath)).Run();
    }
}
