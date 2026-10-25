using System.Numerics;
using Hexa.NET.ImGui;
using ModelExplorer.App.Render;
using ModelExplorer.App.Shell;
using ModelExplorer.App.Thumbnails;
using ModelExplorer.Core.Library;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Indexing;

namespace ModelExplorer.App;

// The library panel (roots, folder tree, sorting and filters) and the models panel
// (search, and the results as a thumbnail grid or a list). Both draw only what is
// in view, however large the library.
internal sealed unsafe partial class MainWindow
{
    private const uint ColorSelected = 0xFF805A2A, ColorHover = 0x30FFFFFF, ColorFocus = 0xFFF59621, ColorPlaceholder = 0xFF2C2A2A, ColorFailed = 0xFF30305A;

    private bool _showLibrary = true;
    private bool _showModels = true;
    private bool _gridView = true;
    private int _thumbnailSize = 144;
    private int _gridColumns = 1;
    private int _seenResults = -1;
    private int _shownSelection;
    private int _lastStep = 1;
    private bool _scrollToFocus;
    private string _searchBuffer = string.Empty;
    private ModelFile? _menuFile;

    // ------------------------------------------------------------------ library panel

    private void DrawLibraryPanel()
    {
        var library = _library;
        ImGui.BeginDisabled(!library.IsReady || library.IsScanning);
        if (ImGui.Button("Add folder…"))
        {
            _deferred = AddFolders;
        }

        ImGui.SameLine();
        if (ImGui.Button("Rescan"))
        {
            _ = library.RescanAsync();
        }

        ImGui.EndDisabled();
        if (library.IsScanning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel scan"))
            {
                library.CancelScan();
            }
        }

        if (library.SelectedFolder is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Show all"))
            {
                library.ClearFolderFilter();
            }

            ImGui.SetItemTooltip("Show every folder again");
        }

        ImGui.Spacing();
        Combo("Sort", library.SortOptions, library.Sort, option => option.Label, value => library.Sort = value);
        Combo("Format", library.ExtensionFilters, library.ExtensionFilter, option => option.Label, value => library.ExtensionFilter = value);
        Combo("Size", library.SizeFilters, library.SizeFilter, option => option.Label, value => library.SizeFilter = value);
        ImGui.Separator();

        var footer = ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginChild("tree", new Vector2(0, -footer)))
        {
            if (!library.HasRoots)
            {
                ImGui.TextDisabled(library.IsReady ? "No library folders yet. Use Add folder…" : "Opening the index…");
            }

            foreach (var root in library.FolderTree)
            {
                DrawFolderNode(root);
            }
        }

        ImGui.EndChild();
        ImGui.TextDisabled(library.ModelCountText);
    }

    private void DrawFolderNode(FolderNode node)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (node.Children.Count == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        }

        if (node.SameSubtreeAs(_library.SelectedFolder))
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        ImGui.SetNextItemOpen(node.IsExpanded);
        var label = node.IsRoot
            ? $"{node.Name}  ({node.CountText}{(node.IsIncomplete ? ", not fully scanned" : string.Empty)})###{node.FullPath}"
            : $"{node.Name}###{node.FullPath}";
        var open = ImGui.TreeNodeEx(label, flags);
        if (node.Children.Count > 0)
        {
            node.IsExpanded = open;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
        {
            _library.SelectedFolder = node;
        }

        ImGui.SetItemTooltip(node.FullPath);
        if (node.IsRoot && ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("Open in Explorer"))
            {
                Notice(ShellActions.Reveal(node.FullPath));
            }

            ImGui.BeginDisabled(_library.IsScanning);
            if (ImGui.MenuItem("Remove from library") && node.Root is { } root)
            {
                _ = _library.RemoveRootAsync(root.Id);
            }

            ImGui.EndDisabled();
            ImGui.EndPopup();
        }

        if (open && node.Children.Count > 0)
        {
            foreach (var child in node.Children)
            {
                DrawFolderNode(child);
            }

            ImGui.TreePop();
        }
    }

    private void AddFolders()
    {
        ShellActions.PickAsync(_hwnd, folders: true, filter: null, picked =>
        {
            if (picked.Count > 0)
            {
                _dispatcher!.Post(_ => _ = _library.AddRootsAsync(picked), null);
            }
        });
    }

    // ------------------------------------------------------------------ models panel

    private void DrawModelsPanel()
    {
        SyncResults();
        var library = _library;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Search models…  (name or folder)", ref _searchBuffer, 256))
        {
            library.SearchText = _searchBuffer;
        }

        ImGui.TextDisabled(library.ResultStatus);
        ImGui.SameLine();
        var right = ImGui.GetContentRegionAvail().X;
        var toggleWidth = ImGui.CalcTextSize("List").X + (ImGui.GetStyle().FramePadding.X * 2);
        var sliderWidth = 110 * Scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, right - toggleWidth - (_gridView ? sliderWidth + ImGui.GetStyle().ItemSpacing.X : 0)));
        if (_gridView)
        {
            ImGui.SetNextItemWidth(sliderWidth);
            var step = _thumbnailSize / 8;
            if (ImGui.SliderInt("##size", ref step, 11, ThumbnailService.RenderSize / 8, string.Empty))
            {
                _thumbnailSize = step * 8;
            }

            ImGui.SetItemTooltip("Thumbnail size");
            ImGui.SameLine();
        }

        if (ImGui.Button(_gridView ? "List" : "Grid"))
        {
            _gridView = !_gridView;
            _scrollToFocus = true;
        }

        var models = library.Models;
        if (models.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(library.IsReady ? library.EmptyModelsText : "Opening the index…");
            return;
        }

        if (_gridView)
        {
            DrawGrid(models);
        }
        else
        {
            DrawList(models);
        }
    }

    /// <summary>
    /// Takes in a new result list. Files that are still in it stay selected; the
    /// view is left alone, so refining a search never blanks what is on screen.
    /// </summary>
    private void SyncResults()
    {
        if (_seenResults == _library.ResultsVersion)
        {
            return;
        }

        _seenResults = _library.ResultsVersion;
        _selection.Reset(_library.Models);
        _shownSelection = _selection.Version;
    }

    /// <summary>A click, a key or a menu changed the selection: show what is selected now.</summary>
    private void ApplySelection()
    {
        if (_shownSelection == _selection.Version)
        {
            return;
        }

        _shownSelection = _selection.Version;
        var selected = _selection.Selected;
        if (selected.Count == 0)
        {
            return;
        }

        // Stepping through one model at a time: the next ones in the direction of
        // travel, and the previous one, are loaded ahead so the step shows at once.
        var prefetch = new List<string>();
        if (selected.Count == 1 && _selection.Focus >= 0)
        {
            var models = _library.Models;
            foreach (var delta in new[] { _lastStep, 2 * _lastStep, -_lastStep })
            {
                var index = _selection.Focus + delta;
                if (index >= 0 && index < models.Count)
                {
                    prefetch.Add(models[index].FullPath);
                }
            }
        }

        _workspace.Show([.. selected.Select(file => file.FullPath)], prefetch);
        if (_workspace.Overflow > 0)
        {
            Notice($"{selected.Count:N0} models are selected; the first {Workspace.MaxModels} are shown.");
        }

        UpdateTitle();
    }

    private void Click(int index, bool control, bool shift)
    {
        if (_selection.Focus >= 0 && index != _selection.Focus)
        {
            _lastStep = index > _selection.Focus ? 1 : -1;
        }

        _selection.Click(index, control, shift);
        ApplySelection();
    }

    /// <summary>Moves through the results with the keyboard, extending the selection with Shift.</summary>
    private void Step(int delta, bool extend)
    {
        if (_library.Models.Count == 0)
        {
            return;
        }

        _lastStep = Math.Sign(delta);
        _selection.Move(delta, extend);
        _scrollToFocus = true;
        ApplySelection();
    }

    private void DrawGrid(IReadOnlyList<ModelFile> models)
    {
        if (_thumbs is null || _gpu is null)
        {
            return;
        }

        var thumb = Math.Min(ThumbnailService.RenderSize, (int)MathF.Round(_thumbnailSize * Scale));
        _thumbs.SetCellSize(thumb, _gpu);
        if (!ImGui.BeginChild("grid", ImGui.GetContentRegionAvail()))
        {
            ImGui.EndChild();
            return;
        }

        var pad = MathF.Round(5 * Scale);
        var label = (ImGui.GetTextLineHeight() * 2) + MathF.Round(4 * Scale);
        var width = ImGui.GetContentRegionAvail().X;
        var cellWidth = thumb + (2 * pad);
        var cellHeight = thumb + label + (2 * pad);
        var columns = Math.Max(1, (int)(width / cellWidth));
        cellWidth = MathF.Floor(width / columns);
        var rows = (models.Count + columns - 1) / columns;
        _gridColumns = columns;

        var viewHeight = ImGui.GetWindowHeight();
        var scrollY = ImGui.GetScrollY();
        if (_scrollToFocus && _selection.Focus >= 0)
        {
            _scrollToFocus = false;
            var top = _selection.Focus / columns * cellHeight;
            if (top < scrollY)
            {
                ImGui.SetScrollY(scrollY = top);
            }
            else if (top + cellHeight > scrollY + viewHeight)
            {
                ImGui.SetScrollY(scrollY = top + cellHeight - viewHeight);
            }
        }

        var origin = ImGui.GetCursorScreenPos();
        var firstRow = Math.Max(0, (int)(scrollY / cellHeight));
        var lastRow = Math.Min(rows - 1, (int)((scrollY + viewHeight) / cellHeight));
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsWindowHovered();
        var mouse = ImGui.GetMousePos();
        var io = ImGui.GetIO();

        _thumbs.BeginFrame();
        var order = 0;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = (row * columns) + column;
                if (index >= models.Count)
                {
                    break;
                }

                var file = models[index];
                var p0 = new Vector2(MathF.Floor(origin.X + (column * cellWidth)), MathF.Floor(origin.Y + (row * cellHeight)));
                var p1 = p0 + new Vector2(cellWidth, cellHeight);
                var over = hovered && mouse.X >= p0.X && mouse.X < p1.X && mouse.Y >= p0.Y && mouse.Y < p1.Y;
                if (_selection.IsSelected(file))
                {
                    draw.AddRectFilled(p0 + Vector2.One, p1 - Vector2.One, ColorSelected, 4 * Scale);
                }
                else if (over)
                {
                    draw.AddRectFilled(p0 + Vector2.One, p1 - Vector2.One, ColorHover, 4 * Scale);
                }

                var box = new Vector2(MathF.Floor(p0.X + ((cellWidth - thumb) / 2)), p0.Y + pad);
                if (_thumbs.TryGet(file, 1000 + order++, out var slot, out var failed))
                {
                    var at = new Vector2(MathF.Floor(box.X + ((thumb - slot.Width) / 2f)), MathF.Floor(box.Y + ((thumb - slot.Height) / 2f)));
                    draw.AddImage(new ImTextureRef(null, new ImTextureID(slot.Texture)), at, at + new Vector2(slot.Width, slot.Height), slot.Uv0, slot.Uv1);
                }
                else
                {
                    draw.AddRectFilled(box + new Vector2(thumb * 0.3f), box + new Vector2(thumb * 0.7f), failed ? ColorFailed : ColorPlaceholder, 4 * Scale);
                }

                if (index == _selection.Focus)
                {
                    draw.AddRect(p0 + Vector2.One, p1 - Vector2.One, ColorFocus, 4 * Scale);
                }

                DrawCellName(draw, file.Name, p0, cellWidth, pad, thumb);
                if (over)
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        Click(index, io.KeyCtrl, io.KeyShift);
                    }
                    else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        OpenModelMenu(index);
                    }

                    ImGui.SetTooltip(file.FullPath);
                }
            }
        }

        // About to scroll in: two screens ahead and one behind, after everything visible.
        var visibleRows = lastRow - firstRow + 1;
        for (var row = lastRow + 1; row <= Math.Min(rows - 1, lastRow + (2 * visibleRows)); row++)
        {
            for (var column = 0; column < columns && (row * columns) + column < models.Count; column++)
            {
                _thumbs.TryGet(models[(row * columns) + column], 5000 + order++, out _, out _);
            }
        }

        for (var row = firstRow - 1; row >= Math.Max(0, firstRow - visibleRows); row--)
        {
            for (var column = 0; column < columns && (row * columns) + column < models.Count; column++)
            {
                _thumbs.TryGet(models[(row * columns) + column], 5000 + order++, out _, out _);
            }
        }

        _thumbs.EndFrame();
        ImGui.Dummy(new Vector2(1, rows * cellHeight));
        DrawModelMenu();
        ImGui.EndChild();
    }

    /// <summary>The name under a tile: up to two lines, centred, cut off at the cell's edge.</summary>
    private static void DrawCellName(ImDrawListPtr draw, string name, Vector2 cell, float cellWidth, float pad, int thumb)
    {
        var room = cellWidth - (2 * pad);
        var lineHeight = ImGui.GetTextLineHeight();
        var top = cell.Y + pad + thumb + 2;
        draw.PushClipRect(new Vector2(cell.X + pad, top), new Vector2(cell.X + cellWidth - pad, top + (2 * lineHeight)), true);
        var first = name;
        var second = string.Empty;
        if (ImGui.CalcTextSize(name).X > room)
        {
            // Break where the first line is full; the second is cut off if it has to be.
            var split = name.Length;
            while (split > 1 && ImGui.CalcTextSize(name[..split]).X > room)
            {
                split--;
            }

            first = name[..split];
            second = name[split..];
        }

        foreach (var (text, line) in new[] { (first, 0), (second, 1) })
        {
            if (text.Length == 0)
            {
                continue;
            }

            var width = ImGui.CalcTextSize(text).X;
            draw.AddText(new Vector2(MathF.Floor(cell.X + pad + MathF.Max(0, (room - width) / 2)), top + (line * lineHeight)), ColorText, text);
        }

        draw.PopClipRect();
    }


    private void DrawList(IReadOnlyList<ModelFile> models)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Hideable;
        if (!ImGui.BeginTable("models", 4, flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Size");
        ImGui.TableSetupColumn("Modified");
        ImGui.TableSetupColumn("Folder", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableHeadersRow();

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        if (_scrollToFocus && _selection.Focus >= 0)
        {
            _scrollToFocus = false;
            float top = _selection.Focus * rowHeight, scrollY = ImGui.GetScrollY(), viewHeight = ImGui.GetWindowHeight() - (2 * rowHeight);
            if (top < scrollY)
            {
                ImGui.SetScrollY(top);
            }
            else if (top + rowHeight > scrollY + viewHeight)
            {
                ImGui.SetScrollY(top + rowHeight - viewHeight);
            }
        }

        var io = ImGui.GetIO();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(models.Count, rowHeight);
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var file = models[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(index);
                if (ImGui.Selectable(file.Name, _selection.IsSelected(file), ImGuiSelectableFlags.SpanAllColumns))
                {
                    Click(index, io.KeyCtrl, io.KeyShift);
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    OpenModelMenu(index);
                }

                ImGui.SetItemTooltip(file.FullPath);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(FormatSize(file.Size));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(file.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                ImGui.TableSetColumnIndex(3);
                ImGui.TextDisabled(file.Folder);
                ImGui.PopID();
            }
        }

        clipper.End();
        clipper.Destroy();
        DrawModelMenu();
        ImGui.EndTable();
    }

    private void OpenModelMenu(int index)
    {
        _selection.Ensure(index);
        ApplySelection();
        _menuFile = _library.Models[index];
        ImGui.OpenPopup("model");
    }

    private void DrawModelMenu()
    {
        if (!ImGui.BeginPopup("model"))
        {
            return;
        }

        if (_menuFile is { } file)
        {
            ImGui.TextDisabled(file.Name);
            ImGui.Separator();
            if (ImGui.MenuItem("Show only this model"))
            {
                var index = IndexOf(file);
                if (index >= 0)
                {
                    Click(index, control: false, shift: false);
                }
            }

            if (ImGui.MenuItem("Open in Explorer", "Ctrl+E"))
            {
                Notice(ShellActions.Reveal(file.FullPath));
            }

            if (ImGui.MenuItem("Copy path", "Ctrl+C"))
            {
                CopyPath(file.FullPath);
            }
        }

        ImGui.EndPopup();
    }

    private int IndexOf(ModelFile file)
    {
        var models = _library.Models;
        for (var i = 0; i < models.Count; i++)
        {
            if (ReferenceEquals(models[i], file))
            {
                return i;
            }
        }

        return -1;
    }

    private void CopyPath(string path) =>
        Notice(ShellActions.CopyText(_hwnd, path) ? $"Copied the path of {Path.GetFileName(path)}" : "Could not copy — another application is holding the clipboard");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:N1} MB",
        _ => $"{bytes / 1073741824.0:N2} GB",
    };

    private static void Combo<T>(string label, IReadOnlyList<T> options, T selected, Func<T, string> name, Action<T> select)
    {
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize("Format").X - ImGui.GetStyle().ItemInnerSpacing.X - 2);
        if (ImGui.BeginCombo(label, name(selected)))
        {
            foreach (var option in options)
            {
                if (ImGui.Selectable(name(option), EqualityComparer<T>.Default.Equals(option, selected)))
                {
                    select(option);
                }
            }

            ImGui.EndCombo();
        }
    }
}
