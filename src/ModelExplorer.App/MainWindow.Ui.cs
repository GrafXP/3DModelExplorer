using System.Numerics;
using Hexa.NET.ImGui;
using ModelExplorer.App.Shell;
using ModelExplorer.Core.Viewing;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ModelExplorer.App;

// What a frame draws besides the panes: the dock space with the two panels, the
// menu, the view's toolbar, the status bar and the floating windows.
internal sealed unsafe partial class MainWindow
{
    private const ImGuiWindowFlags PinnedWindow = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;

    private bool _layoutChecked;
    private bool _resetLayout;
    private bool _showAppearance;
    private bool _showKeys;
    private bool _showAbout;

    private void DrawUi()
    {
        var viewport = ImGui.GetMainViewport();
        DrawMenuBar();
        DrawStatusBar(viewport);

        // Panels dock around a central area that stays empty: that is where the panes are.
        var dock = ImGui.GetID("dock");
        if (!_layoutChecked || _resetLayout)
        {
            if (!_ui!.HasSavedLayout || _resetLayout)
            {
                BuildDefaultLayout(dock, viewport.WorkSize);
            }

            _layoutChecked = true;
            _resetLayout = false;
        }

        ImGui.DockSpaceOverViewport(dock, viewport, ImGuiDockNodeFlags.PassthruCentralNode);
        if (_showLibrary)
        {
            ImGui.SetNextWindowSize(new Vector2(260, 600) * Scale, ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Library", ref _showLibrary))
            {
                DrawLibraryPanel();
            }

            ImGui.End();
        }

        if (_showModels)
        {
            ImGui.SetNextWindowSize(new Vector2(380, 600) * Scale, ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Models", ref _showModels))
            {
                DrawModelsPanel();
            }

            ImGui.End();
        }

        var central = ImGuiP.DockBuilderGetCentralNode(dock);
        var area = central.IsNull
            ? new PaneRect(0, 0, (int)viewport.WorkSize.X, (int)viewport.WorkSize.Y)
            : new PaneRect((int)central.Pos.X, (int)central.Pos.Y, (int)central.Size.X, (int)central.Size.Y);
        var toolbar = DrawViewToolbar(area);
        LayoutPanes(area with { Y = area.Y + toolbar, Height = Math.Max(1, area.Height - toolbar) });
        DrawPaneOverlays();

        if (_showAppearance)
        {
            DrawAppearanceWindow();
        }

        if (_workspace.CutEnabled)
        {
            DrawCutWindow();
        }

        if (_showKeys)
        {
            DrawKeys();
        }

        if (_showAbout)
        {
            DrawAbout();
        }
    }

    /// <summary>The first-run layout: library on the left, the models beside it, the panes in the rest.</summary>
    private static void BuildDefaultLayout(uint dock, Vector2 size)
    {
        const ImGuiDockNodeFlags dockSpace = (ImGuiDockNodeFlags)(1 << 10); // ImGuiDockNodeFlags_DockSpace, an internal flag
        ImGuiP.DockBuilderRemoveNode(dock);
        ImGuiP.DockBuilderAddNode(dock, dockSpace);
        ImGuiP.DockBuilderSetNodeSize(dock, size);
        uint library, rest, models, centre;
        ImGuiP.DockBuilderSplitNode(dock, ImGuiDir.Left, 0.17f, &library, &rest);
        ImGuiP.DockBuilderSplitNode(rest, ImGuiDir.Left, 0.30f, &models, &centre);
        ImGuiP.DockBuilderDockWindow("Library", library);
        ImGuiP.DockBuilderDockWindow("Models", models);
        ImGuiP.DockBuilderFinish(dock);
    }

    // ------------------------------------------------------------------ menu

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return;
        }

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open models…", "Ctrl+O"))
            {
                _deferred = OpenWithDialog;
            }

            if (ImGui.MenuItem("Add library folder…", string.Empty, false, _library.IsReady && !_library.IsScanning))
            {
                _deferred = AddFolders;
            }

            if (ImGui.MenuItem("Rescan library", string.Empty, false, _library.IsReady && !_library.IsScanning))
            {
                _ = _library.RescanAsync();
            }

            if (ImGui.MenuItem("Clear thumbnail cache", string.Empty, false, _thumbs is not null))
            {
                ClearThumbnails();
            }

            ImGui.Separator();
            var active = _workspace.Requested(_workspace.Active);
            if (ImGui.MenuItem("Copy path", "Ctrl+C", false, active is not null) && active is not null)
            {
                CopyPath(active.Path);
            }

            if (ImGui.MenuItem("Show in Explorer", "Ctrl+E", false, active is not null) && active is not null)
            {
                Notice(ShellActions.Reveal(active.Path));
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Exit", "Alt+F4"))
            {
                PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Library panel", string.Empty, ref _showLibrary);
            ImGui.MenuItem("Models panel", string.Empty, ref _showModels);
            if (ImGui.MenuItem("Reset panel layout"))
            {
                _showLibrary = _showModels = true;
                _resetLayout = true;
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Fit", "F"))
            {
                Fit();
            }

            MenuToggle("Bounding box", "B", _workspace.ShowBoundingBox, value => _workspace.ShowBoundingBox = value);
            MenuToggle("Build plate", "P", _workspace.ShowBuildPlate, value => _workspace.ShowBuildPlate = value);
            MenuToggle("Cut plane", "C", _workspace.CutEnabled, value => _workspace.CutEnabled = value);
            ImGui.MenuItem("Lighting and appearance", "A", ref _showAppearance);
            ImGui.Separator();
            MenuToggle("Linked cameras", string.Empty, _workspace.SyncCameras, value => _workspace.SyncCameras = value);
            MenuToggle("Align centers", string.Empty, _workspace.AlignCenters, value => _workspace.AlignCenters = value);
            MenuToggle("Keep the view when models change", "L", _workspace.KeepView, value => _workspace.KeepView = value);
            ImGui.Separator();
            if (ImGui.MenuItem("Fullscreen", "F11", _fullscreen))
            {
                _deferred = ToggleFullscreen;
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Compare"))
        {
            ModeItem("Side by side", string.Empty, CompareMode.Separate);
            ModeItem("Overlay", "O", CompareMode.Overlay);
            ModeItem("Deviation heatmap", "H", CompareMode.Deviation);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help"))
        {
            ImGui.MenuItem("Keys and mouse", "F1", ref _showKeys);
            ImGui.MenuItem("About", string.Empty, ref _showAbout);
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();

        void ModeItem(string label, string shortcut, CompareMode mode)
        {
            if (ImGui.MenuItem(label, shortcut, _workspace.Mode == mode))
            {
                _workspace.Mode = mode;
            }
        }
    }

    private static void MenuToggle(string label, string shortcut, bool value, Action<bool> set)
    {
        if (ImGui.MenuItem(label, shortcut, value))
        {
            set(!value);
        }
    }

    // ------------------------------------------------------------------ toolbar

    /// <summary>The bar over the panes: fitting, overlays, how the models are compared. Returns its height.</summary>
    private int DrawViewToolbar(PaneRect area)
    {
        ImGui.SetNextWindowPos(new Vector2(area.X, area.Y));
        ImGui.SetNextWindowSize(new Vector2(area.Width, 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0x19 / 255f, 0x19 / 255f, 0x19 / 255f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 4) * Scale);
        var height = 0f;
        if (ImGui.Begin("##toolbar", PinnedWindow | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (ImGui.Button("Fit"))
            {
                Fit();
            }

            ImGui.SetItemTooltip("Frame every model (F, or double-click a pane)");
            ImGui.SameLine();
            Toggle("Box", _workspace.ShowBoundingBox, "Bounding box and dimensions (B)", value => _workspace.ShowBoundingBox = value);
            ImGui.SameLine();
            Toggle("Plate", _workspace.ShowBuildPlate, "A printer's build plate under each model (P)", value => _workspace.ShowBuildPlate = value);
            ImGui.SameLine();
            Toggle("Cut", _workspace.CutEnabled, "Cut every model with one freely rotatable plane (C)", value => _workspace.CutEnabled = value);
            ImGui.SameLine();
            Toggle("Look", _showAppearance, "Lighting, shading and model colour (A)", value => _showAppearance = value);

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ModeButton("Side by side", CompareMode.Separate, "One pane per model");
            ImGui.SameLine();
            ModeButton("Overlay", CompareMode.Overlay, "All models in one pane, each in its own colour (O)");
            ImGui.SameLine();
            ModeButton("Deviation", CompareMode.Deviation, "Colour each model by its distance from the active one (H)");

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            var linked = _workspace.SyncCameras;
            if (ImGui.Checkbox("Linked", ref linked))
            {
                _workspace.SyncCameras = linked;
            }

            ImGui.SetItemTooltip("Turn, pan and zoom every pane together");
            ImGui.SameLine();
            var align = _workspace.AlignCenters;
            if (ImGui.Checkbox("Align centers", ref align))
            {
                _workspace.AlignCenters = align;
            }

            ImGui.SetItemTooltip("Put the bounding-box centres on top of each other. Scale and orientation are kept.");
            ImGui.SameLine();
            var keep = _workspace.KeepView;
            if (ImGui.Checkbox("Keep view", ref keep))
            {
                _workspace.KeepView = keep;
            }

            ImGui.SetItemTooltip("Keep the camera when the models change, instead of framing the new ones (L)");

            if (_workspace.ShowBuildPlate)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(230 * Scale);
                if (ImGui.BeginCombo("##plate", _workspace.Plate.Label))
                {
                    foreach (var plate in BuildPlates.All)
                    {
                        if (ImGui.Selectable(plate.Label, ReferenceEquals(plate, _workspace.Plate)))
                        {
                            _workspace.Plate = plate;
                        }
                    }

                    ImGui.EndCombo();
                }
            }

            if (_workspace.Mode == CompareMode.Overlay && _workspace.Count > 0)
            {
                DrawOverlayControls();
            }
            else if (_workspace.Mode == CompareMode.Deviation)
            {
                DrawDeviationControls();
            }

            height = ImGui.GetWindowHeight();
        }

        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        return (int)MathF.Ceiling(height);

        void ModeButton(string label, CompareMode mode, string tooltip) =>
            Toggle(label, _workspace.Mode == mode, tooltip, _ => _workspace.Mode = mode);
    }

    private void Toggle(string label, bool value, string tooltip, Action<bool> set)
    {
        if (value)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        }

        var clicked = ImGui.Button(label);
        if (value)
        {
            ImGui.PopStyleColor();
        }

        ImGui.SetItemTooltip(tooltip);
        if (clicked)
        {
            set(!value);
        }
    }

    /// <summary>One opacity slider per model, in its overlay colour.</summary>
    private void DrawOverlayControls()
    {
        var first = true;
        for (var i = 0; i < _workspace.Count; i++)
        {
            if (_workspace.Requested(i) is not { } model)
            {
                continue;
            }

            if (!first)
            {
                ImGui.SameLine(0, 14 * Scale);
            }

            first = false;
            ImGui.ColorButton($"##swatch{i}", new Vector4(Workspace.OverlayColor(i), 1), ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90 * Scale);
            var opacity = _workspace.Opacity[i];
            if (ImGui.SliderFloat($"##opacity{i}", ref opacity, 0, 1, $"{opacity * 100:0}%%"))
            {
                _workspace.Opacity[i] = opacity;
            }

            ImGui.SetItemTooltip($"Opacity of {model.Name}");
        }
    }

    /// <summary>The tolerance, the colour scale it sets, and how far the measurement has got.</summary>
    private void DrawDeviationControls()
    {
        if (_workspace.Count < 2)
        {
            ImGui.TextDisabled("Select two or more models to measure how far their surfaces are apart.");
            return;
        }

        ImGui.TextUnformatted("Tolerance");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * Scale);
        var tolerance = _workspace.Tolerance;
        if (ImGui.InputFloat("##tolerance", ref tolerance, 0.01f, 0.1f, "%.3f mm"))
        {
            _workspace.Tolerance = tolerance;
        }

        ImGui.SetItemTooltip("Distances within this are green");
        ImGui.SameLine();

        // The scale itself: green, then yellow through red to the largest deviation.
        var maximum = _workspace.DeviationMaximum;
        ImGui.TextUnformatted($"≤ {_workspace.Tolerance:G4} mm");
        ImGui.SameLine();
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var size = new Vector2(140 * Scale, ImGui.GetFrameHeight() * 0.6f);
        var top = at with { Y = at.Y + ((ImGui.GetFrameHeight() - size.Y) / 2) };
        uint within = ImGui.GetColorU32(new Vector4(HeatmapScale.Within, 1));
        uint middle = ImGui.GetColorU32(new Vector4(HeatmapScale.Middle, 1));
        uint worst = ImGui.GetColorU32(new Vector4(HeatmapScale.Worst, 1));
        var half = size.X / 2;
        draw.AddRectFilledMultiColor(top, top + new Vector2(half, size.Y), within, middle, middle, within);
        draw.AddRectFilledMultiColor(top + new Vector2(half, 0), top + size, middle, worst, worst, middle);
        ImGui.Dummy(new Vector2(size.X, ImGui.GetFrameHeight()));
        ImGui.SameLine();
        ImGui.TextUnformatted($"{maximum:G4} mm");
        ImGui.SameLine();
        ImGui.TextDisabled(_workspace.IsComputingDeviation
            ? "Measuring surface distances…"
            : "Measured against the active model (blue frame). Click a pane to make it the reference.");
        if (_workspace.IsComputingDeviation)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
            {
                _workspace.Mode = CompareMode.Separate;
            }
        }
    }

    // ------------------------------------------------------------------ status bar

    private void DrawStatusBar(ImGuiViewportPtr viewport)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 2) * Scale);
        var height = ImGui.GetFrameHeight();
        if (ImGuiP.BeginViewportSideBar("##status"u8, viewport, ImGuiDir.Down, height, PinnedWindow | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.AlignTextToFramePadding();
            var notice = Environment.TickCount64 < _noticeUntil ? _notice : null;
            if (notice is not null)
            {
                ImGui.TextUnformatted(notice);
            }
            else if (_workspace.Requested(_workspace.Active) is { } model)
            {
                ImGui.TextUnformatted(model.Path);
                if (model.IsLoaded && model.Mesh is { } mesh)
                {
                    ImGui.SameLine();
                    var upload = model.Gpu is Render.GpuMesh gpuMesh ? $"  ·  upload {gpuMesh.UploadTime.TotalMilliseconds:N0} ms" : string.Empty;
                    ImGui.TextDisabled($"·  {mesh.TriangleCount:N0} triangles  ·  parse {model.ParseTime.TotalMilliseconds:N0} ms{upload}");
                }
            }
            else
            {
                ImGui.TextDisabled("Ready");
            }

            // Scan progress and frame time on the right, out of the way of the path.
            var right = _library.ScanStatus;
            var frame = $"{_lastFrameMs:0.0} ms";
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var rightWidth = (right.Length > 0 ? ImGui.CalcTextSize(right).X + spacing : 0) + ImGui.CalcTextSize(frame).X + (8 * Scale);
            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - rightWidth));
            if (right.Length > 0)
            {
                ImGui.TextUnformatted(right);
                ImGui.SameLine();
            }

            ImGui.TextDisabled(frame);
            ImGui.SetItemTooltip(_gpu is null ? "No graphics device" : $"CPU time of the last frame · {_gpu.AdapterName}{(_gpu.IsSoftware ? " (software)" : string.Empty)}");
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    // ------------------------------------------------------------------ help

    private void DrawKeys()
    {
        ImGui.SetNextWindowPos(new Vector2(_viewRect.X + (_viewRect.Width / 2f), _viewRect.Y + (_viewRect.Height / 2f)), ImGuiCond.Appearing, new Vector2(0.5f));
        if (ImGui.Begin("Keys and mouse", ref _showKeys, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings))
        {
            (string Keys, string Action)[] rows =
            [
                ("Click, Ctrl+click, Shift+click", "Select models in the list: every selected model is shown"),
                ("Arrow keys, Home, End", "Step through the models (Shift extends the selection)"),
                ("Ctrl+A", "Select every model in the list"),
                ("Left drag", "Turn"),
                ("Middle drag, Shift+left drag", "Pan"),
                ("Wheel, Ctrl+left drag", "Zoom at the cursor"),
                ("Right-click a surface", "Turn about that point (right-click empty space to clear)"),
                ("Double-click, F", "Fit"),
                ("Click an axis end", "Look along that axis"),
                ("1 – 9", "Make that pane the active one"),
                ("O, H", "Overlay, deviation heatmap (again: side by side)"),
                ("B, P, C, A", "Bounding box, build plate, cut plane, appearance"),
                ("L", "Keep the view when the models change"),
                ("Tab", "Hide or show the panels"),
                ("Ctrl+O", "Open models; drop files to open them (with Ctrl: add them)"),
                ("Ctrl+C, Ctrl+E", "Copy the active model's path, show it in Explorer"),
                ("F11, Esc", "Fullscreen; back"),
            ];
            if (ImGui.BeginTable("keys", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                foreach (var (keys, action) in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(keys);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextDisabled(action);
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    private void DrawAbout()
    {
        ImGui.SetNextWindowPos(new Vector2(_viewRect.X + (_viewRect.Width / 2f), _viewRect.Y + (_viewRect.Height / 2f)), ImGuiCond.Appearing, new Vector2(0.5f));
        if (ImGui.Begin("About", ref _showAbout, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings))
        {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            ImGui.TextUnformatted($"3D Model Explorer {version?.ToString(3)}");
            ImGui.TextDisabled("A fast, local-first viewer and library for STL and 3MF files.");
            ImGui.Spacing();
            ImGui.TextDisabled($"Graphics: {_gpu?.AdapterName}{(_gpu?.IsSoftware == true ? " (software)" : string.Empty)}");
            ImGui.TextDisabled($"Models in memory: {_cache.Count} · {_cache.Bytes / 1048576.0:N0} MB");
        }

        ImGui.End();
    }

    // ------------------------------------------------------------------ actions

    private void Fit()
    {
        if (!_workspace.SyncCameras && _workspace.Mode != CompareMode.Overlay)
        {
            _workspace.FitPane(_workspace.Active);
        }
        else
        {
            _workspace.FitAll();
        }
    }

    private void OpenWithDialog()
    {
        var patterns = string.Join(';', _loaders.SupportedExtensions.Select(extension => $"*{extension}"));
        ShellActions.PickAsync(_hwnd, folders: false, ("3D models", patterns), picked =>
        {
            if (picked.Count > 0)
            {
                _dispatcher!.Post(_ => OpenFiles(picked, add: false), null);
            }
        });
    }

    /// <summary>
    /// Shows files that were opened, dropped or named on the command line, replacing
    /// what is shown or, with <paramref name="add"/>, joining it. A dropped folder is
    /// added to the library instead.
    /// </summary>
    private void OpenFiles(IReadOnlyList<string> paths, bool add)
    {
        var folders = paths.Where(Directory.Exists).ToList();
        if (folders.Count > 0)
        {
            if (_library.IsReady)
            {
                _ = _library.AddRootsAsync(folders);
            }
            else
            {
                Notice("The index is still opening; drop the folder again in a moment.");
            }
        }

        var files = paths.Where(path => File.Exists(path) && _loaders.IsSupported(path)).Select(Path.GetFullPath).ToList();
        if (files.Count == 0)
        {
            if (folders.Count == 0)
            {
                Notice("Only STL and 3MF files can be opened.");
            }

            return;
        }

        // Files from outside the selection: the list's highlight no longer explains the view.
        _selection.Clear();
        _shownSelection = _selection.Version;
        if (add)
        {
            _workspace.Add(files);
        }
        else
        {
            _workspace.Show(files);
        }

        if (_workspace.Overflow > 0)
        {
            Notice($"Up to {Workspace.MaxModels} models are shown at once; {_workspace.Overflow} were not opened.");
        }

        UpdateTitle();
        Invalidate();
    }

    private void ClearThumbnails()
    {
        if (_thumbs is null)
        {
            return;
        }

        UiTasks.Then(_thumbs.ClearCacheAsync(), removed =>
            _library.ReportStatus(removed == 1 ? "Cleared 1 cached thumbnail" : $"Cleared {removed:N0} cached thumbnails"));
    }

    private void OnKeyDown(VIRTUAL_KEY key, bool shift, bool control)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                if (_showKeys || _showAbout)
                {
                    _showKeys = _showAbout = false;
                }
                else if (_fullscreen)
                {
                    ToggleFullscreen();
                }
                else if (_workspace.Mode != CompareMode.Separate)
                {
                    _workspace.Mode = CompareMode.Separate;
                }

                break;
            case VIRTUAL_KEY.VK_F1:
                _showKeys = !_showKeys;
                break;
            case VIRTUAL_KEY.VK_F11:
                ToggleFullscreen();
                break;
            case VIRTUAL_KEY.VK_TAB:
                var show = !(_showLibrary || _showModels);
                _showLibrary = _showModels = show;
                break;
            case VIRTUAL_KEY.VK_O when control:
                OpenWithDialog();
                break;
            case VIRTUAL_KEY.VK_O:
                _workspace.Mode = _workspace.Mode == CompareMode.Overlay ? CompareMode.Separate : CompareMode.Overlay;
                break;
            case VIRTUAL_KEY.VK_H:
                _workspace.Mode = _workspace.Mode == CompareMode.Deviation ? CompareMode.Separate : CompareMode.Deviation;
                break;
            case VIRTUAL_KEY.VK_F:
                Fit();
                break;
            case VIRTUAL_KEY.VK_B:
                _workspace.ShowBoundingBox = !_workspace.ShowBoundingBox;
                break;
            case VIRTUAL_KEY.VK_P:
                _workspace.ShowBuildPlate = !_workspace.ShowBuildPlate;
                break;
            case VIRTUAL_KEY.VK_C when control:
                if (_workspace.Requested(_workspace.Active) is { } copied)
                {
                    CopyPath(copied.Path);
                }

                break;
            case VIRTUAL_KEY.VK_C:
                _workspace.CutEnabled = !_workspace.CutEnabled;
                break;
            case VIRTUAL_KEY.VK_E when control:
                if (_workspace.Requested(_workspace.Active) is { } revealed)
                {
                    Notice(ShellActions.Reveal(revealed.Path));
                }

                break;
            case VIRTUAL_KEY.VK_A when control:
                _selection.SelectAll();
                ApplySelection();
                break;
            case VIRTUAL_KEY.VK_A:
                _showAppearance = !_showAppearance;
                break;
            case VIRTUAL_KEY.VK_L:
                _workspace.KeepView = !_workspace.KeepView;
                Notice(_workspace.KeepView ? "The view is kept when the models change" : "New models are framed when they appear");
                break;
            case VIRTUAL_KEY.VK_DOWN:
                Step(_gridView && _showModels ? _gridColumns : 1, shift);
                break;
            case VIRTUAL_KEY.VK_UP:
                Step(_gridView && _showModels ? -_gridColumns : -1, shift);
                break;
            case VIRTUAL_KEY.VK_RIGHT:
            case VIRTUAL_KEY.VK_NEXT when !_gridView:
                Step(1, shift);
                break;
            case VIRTUAL_KEY.VK_LEFT:
            case VIRTUAL_KEY.VK_PRIOR when !_gridView:
                Step(-1, shift);
                break;
            case VIRTUAL_KEY.VK_HOME:
                Step(-_library.Models.Count, shift);
                break;
            case VIRTUAL_KEY.VK_END:
                Step(_library.Models.Count, shift);
                break;
            case >= VIRTUAL_KEY.VK_1 and <= VIRTUAL_KEY.VK_9:
                var pane = key - VIRTUAL_KEY.VK_1;
                if (pane < _workspace.Count)
                {
                    _workspace.Active = pane;
                    UpdateTitle();
                }

                break;
            default:
                return;
        }

        Invalidate();
    }
}
