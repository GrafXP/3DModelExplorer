using System.IO;
using System.Windows;

namespace ModelExplorer.App;

/// <summary>
/// Interaction logic for App.xaml.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Model passed on the command line, if any. Supports "open with" and shell
    /// file associations, and makes the viewer scriptable for perf testing.
    /// </summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupFile = e.Args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        base.OnStartup(e);
    }
}
