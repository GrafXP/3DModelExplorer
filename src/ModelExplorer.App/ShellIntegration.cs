using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ModelExplorer.App;

/// <summary>
/// The two Explorer hand-offs the viewer needs. Both are best-effort: they are
/// conveniences, and a failure to reveal a file must never take the app down.
/// </summary>
internal static class ShellIntegration
{
    /// <summary>
    /// Opens Explorer with the file selected, falling back to its folder when the
    /// file itself is gone — which is exactly the case where the user most wants
    /// to go and look.
    /// </summary>
    /// <returns>A status line describing what happened.</returns>
    public static string Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // The comma is part of the /select switch's syntax; the quotes
                // around the path are what make spaces work.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                })?.Dispose();

                return $"Revealed {Path.GetFileName(path)} in Explorer";
            }

            var folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder))
            {
                return $"{Path.GetFileName(path)} no longer exists";
            }

            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            return $"{Path.GetFileName(path)} is gone — opened its folder instead";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Could not open Explorer: {ex.Message}";
        }
    }

    /// <summary>
    /// Copies text to the clipboard, retrying briefly before giving up.
    /// </summary>
    /// <remarks>
    /// The clipboard is a shared resource that exactly one process may hold open
    /// at a time, and clipboard managers hold it constantly. WPF's
    /// <see cref="Clipboard"/> has no retrying overload — that one is WinForms —
    /// so the loop is written out here. A few short retries clear the usual
    /// collision; the alternative is a copy that silently does nothing.
    /// </remarks>
    public static string CopyText(string text, string description)
    {
        const int attempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return $"Copied {description}";
            }
            catch (ExternalException) when (attempt < attempts)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                return "Could not copy — another application is holding the clipboard";
            }
        }
    }
}
