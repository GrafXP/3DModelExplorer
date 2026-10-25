using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace ModelExplorer.App.Shell;

/// <summary>
/// What the app asks of Windows besides its window: file and folder pickers, the
/// clipboard, and Explorer. All of it best-effort: a failure here is a status
/// line, never a crash.
/// </summary>
internal static unsafe class ShellActions
{
    /// <summary>
    /// Shows the system's Open dialog on a thread of its own and calls
    /// <paramref name="done"/> there with what was picked (empty if cancelled).
    /// The shell's dialogs need a single-threaded COM apartment; on its own thread
    /// the dialog is still modal to the window, which keeps painting meanwhile.
    /// </summary>
    /// <param name="filter">Name and patterns of the types offered first, e.g. ("3D models", "*.stl;*.3mf").</param>
    public static void PickAsync(HWND owner, bool folders, (string Name, string Patterns)? filter, Action<List<string>> done)
    {
        var thread = new Thread(() =>
        {
            List<string> picked;
            try
            {
                picked = Pick(owner, folders, filter);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                picked = [];
            }

            done(picked);
        })
        { IsBackground = true, Name = "Open dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Opens Explorer with the file selected, or its folder when the file is gone.</summary>
    public static string Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // The comma is part of the /select switch's syntax; the quotes make spaces work.
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                return $"Revealed {Path.GetFileName(path)} in Explorer";
            }

            var folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder))
            {
                return $"{Path.GetFileName(path)} no longer exists";
            }

            using var __ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return $"{Path.GetFileName(path)} is gone — opened its folder instead";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Could not open Explorer: {ex.Message}";
        }
    }

    /// <summary>
    /// Puts text on the clipboard, retrying briefly: exactly one process may hold
    /// it open, and clipboard managers hold it often.
    /// </summary>
    public static bool CopyText(HWND owner, string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryCopyText(owner, text))
            {
                return true;
            }

            Thread.Sleep(30);
        }

        return false;
    }

    private static bool TryCopyText(HWND owner, string text)
    {
        var size = (nuint)(text.Length + 1) * sizeof(char);
        var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, size);
        if (memory.IsNull)
        {
            return false;
        }

        var target = (char*)PInvoke.GlobalLock(memory);
        if (target == null)
        {
            PInvoke.GlobalFree(memory);
            return false;
        }

        text.AsSpan().CopyTo(new Span<char>(target, text.Length));
        target[text.Length] = '\0';
        PInvoke.GlobalUnlock(memory);
        if (!PInvoke.OpenClipboard(owner))
        {
            PInvoke.GlobalFree(memory);
            return false;
        }

        PInvoke.EmptyClipboard();
        var ok = !PInvoke.SetClipboardData(13 /* CF_UNICODETEXT */, new HANDLE(memory.Value)).IsNull;
        if (!ok)
        {
            PInvoke.GlobalFree(memory);
        }

        PInvoke.CloseClipboard();
        return ok;
    }

    /// <summary>The Open dialog for files or folders, several at once. Call on an STA thread.</summary>
    private static List<string> Pick(HWND owner, bool folders, (string Name, string Patterns)? filter)
    {
        var result = new List<string>();
        PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
        var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"); // CLSID_FileOpenDialog
        var iid = IFileOpenDialog.IID_Guid;
        IFileOpenDialog* dialog = null;
        IShellItemArray* items = null;
        var pins = new List<GCHandle>();
        try
        {
            if (PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&dialog).Failed)
            {
                return result;
            }

            FILEOPENDIALOGOPTIONS options;
            dialog->GetOptions(&options);
            options |= FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT;
            options |= folders ? FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS : FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST;
            dialog->SetOptions(options);

            if (!folders && filter is { } types)
            {
                // The dialog reads the strings while it is shown: pinned until it closes.
                var specs = stackalloc COMDLG_FILTERSPEC[2];
                specs[0] = new COMDLG_FILTERSPEC { pszName = Pin(types.Name), pszSpec = Pin(types.Patterns) };
                specs[1] = new COMDLG_FILTERSPEC { pszName = Pin("All files"), pszSpec = Pin("*.*") };
                dialog->SetFileTypes(2, specs);
            }

            if (dialog->Show(owner).Failed || dialog->GetResults(&items).Failed)
            {
                return result; // cancelled
            }

            uint count;
            items->GetCount(&count);
            for (uint i = 0; i < count; i++)
            {
                IShellItem* item;
                if (items->GetItemAt(i, &item).Failed)
                {
                    continue;
                }

                PWSTR name;
                if (item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &name).Succeeded)
                {
                    result.Add(new string(name.Value));
                    PInvoke.CoTaskMemFree(name.Value);
                }

                item->Release();
            }

            return result;
        }
        finally
        {
            if (items != null)
            {
                items->Release();
            }

            if (dialog != null)
            {
                dialog->Release();
            }

            foreach (var pin in pins)
            {
                pin.Free();
            }
        }

        char* Pin(string text)
        {
            var handle = GCHandle.Alloc(text, GCHandleType.Pinned);
            pins.Add(handle);
            return (char*)handle.AddrOfPinnedObject();
        }
    }
}

/// <summary>Writes unexpected exceptions beside the index, so a crash leaves a trace.</summary>
internal static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelExplorer");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:O} {exception}\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
