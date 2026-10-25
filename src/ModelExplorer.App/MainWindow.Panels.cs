using System.Numerics;
using Hexa.NET.ImGui;
using ModelExplorer.Core;
using ModelExplorer.Core.Viewing;

namespace ModelExplorer.App;

// The floating windows over the view: appearance and the cutting plane. And the
// settings that remember all of it between runs.
internal sealed unsafe partial class MainWindow
{
    private void DrawAppearanceWindow()
    {
        var appearance = _workspace.Appearance;
        ImGui.SetNextWindowPos(new Vector2(_viewRect.Right - (12 * Scale), _viewRect.Y + (12 * Scale)), ImGuiCond.FirstUseEver, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.94f);
        if (ImGui.Begin("Lighting and appearance", ref _showAppearance, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking))
        {
            ImGui.SetNextItemWidth(220 * Scale);
            if (ImGui.BeginCombo("Lighting", appearance.Lighting.Name))
            {
                foreach (var preset in LightingPresets.All)
                {
                    if (ImGui.Selectable(preset.Name, ReferenceEquals(preset, appearance.Lighting)))
                    {
                        appearance.Lighting = preset;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.Checkbox("Main", ref appearance.MainEnabled);
            ImGui.SameLine();
            ImGui.Checkbox("Fill", ref appearance.FillEnabled);
            ImGui.SameLine();
            ImGui.Checkbox("Rim", ref appearance.RimEnabled);

            ImGui.Checkbox("Ambient", ref appearance.AmbientEnabled);
            ImGui.SameLine();
            ImGui.BeginDisabled(!appearance.AmbientEnabled);
            ImGui.SetNextItemWidth(140 * Scale);
            ImGui.SliderFloat("##ambient", ref appearance.AmbientIntensity, 0, 1, $"{appearance.AmbientIntensity * 100:0}%%");
            ImGui.EndDisabled();

            ImGui.Checkbox("Rotate main light", ref appearance.RotateMainLight);
            ImGui.SameLine();
            ImGui.BeginDisabled(!appearance.RotateMainLight);
            ImGui.SetNextItemWidth(110 * Scale);
            ImGui.SliderFloat("##speed", ref appearance.RotationSpeed, 10, 120, $"{appearance.RotationSpeed:N0}°/s");
            ImGui.EndDisabled();

            ImGui.Separator();
            ImGui.SetNextItemWidth(220 * Scale);
            if (ImGui.BeginCombo("Shading", appearance.Shading.Name))
            {
                foreach (var shading in ShadingOptions.All)
                {
                    if (ImGui.Selectable(shading.Name, ReferenceEquals(shading, appearance.Shading)))
                    {
                        appearance.Shading = shading;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.TextUnformatted("Colour");
            foreach (var color in ModelColors.All)
            {
                ImGui.SameLine();
                var selected = ReferenceEquals(color, appearance.Color);
                if (selected)
                {
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2 * Scale);
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 1, 1, 0.9f));
                }

                if (ImGui.ColorButton(color.Name, new Vector4(color.Color, 1), ImGuiColorEditFlags.NoDragDrop, new Vector2(ImGui.GetFrameHeight())))
                {
                    appearance.Color = color;
                }

                if (selected)
                {
                    ImGui.PopStyleColor();
                    ImGui.PopStyleVar();
                }
            }
        }

        ImGui.End();
    }

    private void DrawCutWindow()
    {
        var open = true;
        ImGui.SetNextWindowPos(new Vector2(_viewRect.Right - (12 * Scale), _viewRect.Bottom - (12 * Scale)), ImGuiCond.FirstUseEver, new Vector2(1, 1));
        ImGui.SetNextWindowBgAlpha(0.94f);
        if (ImGui.Begin("Cut plane", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking))
        {
            var cut = _workspace.Cut;
            ImGui.TextDisabled("Turn the plane with the rings, move it with the arrow.");
            ImGui.TextUnformatted("Face");
            foreach (var (label, normal) in new[] { ("X", Vector3.UnitX), ("Y", Vector3.UnitY), ("Z", Vector3.UnitZ) })
            {
                ImGui.SameLine();
                if (ImGui.Button(label))
                {
                    _workspace.Cut = CutPlaneSettings.Facing(normal) with { KeepOtherSide = cut.KeepOtherSide };
                }

                ImGui.SetItemTooltip($"Cut across the {label} axis, through the middle");
            }

            ImGui.SameLine();
            if (ImGui.Button("Flip"))
            {
                _workspace.Cut = cut with { KeepOtherSide = !cut.KeepOtherSide };
            }

            ImGui.SetItemTooltip("Keep the other side of the plane");
            ImGui.SameLine();
            if (ImGui.Button("Reset"))
            {
                _workspace.Cut = CutPlaneSettings.Default;
            }

            // The offset's range follows the active model's size along the normal.
            var extent = _workspace.Shown(_workspace.Active)?.Mesh is { } mesh
                ? MathF.Max(Vector3.Dot(Vector3.Abs(cut.Normal), mesh.Bounds.Size) * 0.55f, 0.1f)
                : 100f;
            var offset = cut.Offset;
            ImGui.SetNextItemWidth(240 * Scale);
            if (ImGui.SliderFloat("Offset", ref offset, -extent, extent, "%.2f mm"))
            {
                _workspace.BeginCutDrag();
                _workspace.Cut = cut.WithOffset(offset);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _workspace.EndCutDrag();
            }

            var status = _workspace.CutText(_workspace.Active);
            if (status.Length > 0)
            {
                ImGui.TextDisabled(status);
            }
        }

        ImGui.End();
        if (!open)
        {
            _workspace.CutEnabled = false;
        }
    }

    // ------------------------------------------------------------------ settings

    private void ApplySettings()
    {
        var s = _settings;
        _showLibrary = s.ShowLibrary;
        _showModels = s.ShowModels;
        _gridView = s.GridView;
        _thumbnailSize = Math.Clamp(s.ThumbnailSize / 8 * 8, 88, 256);
        if (s.Sort >= 0 && s.Sort < _library.SortOptions.Count)
        {
            _library.Sort = _library.SortOptions[s.Sort];
        }

        _workspace.SyncCameras = s.SyncCameras;
        _workspace.AlignCenters = s.AlignCenters;
        _workspace.KeepView = s.KeepView;
        _workspace.Mode = Enum.TryParse<CompareMode>(s.CompareMode, out var mode) ? mode : CompareMode.Separate;
        _workspace.Tolerance = s.Tolerance;
        _workspace.ShowBoundingBox = s.ShowBoundingBox;
        _workspace.ShowBuildPlate = s.ShowBuildPlate;
        _workspace.Plate = BuildPlates.All.FirstOrDefault(plate => plate.Name == s.BuildPlate) ?? BuildPlates.Default;

        var appearance = _workspace.Appearance;
        appearance.Lighting = LightingPresets.All.FirstOrDefault(preset => preset.Name == s.Lighting) ?? LightingPresets.Default;
        appearance.Shading = ShadingOptions.All.FirstOrDefault(shading => shading.Name == s.Shading) ?? ShadingOptions.Default;
        appearance.Color = ModelColors.All.FirstOrDefault(color => color.Name == s.ModelColor) ?? ModelColors.Default;
        appearance.AmbientEnabled = s.AmbientEnabled;
        appearance.AmbientIntensity = Math.Clamp(s.AmbientIntensity, 0, 1);
        appearance.MainEnabled = s.MainLight;
        appearance.FillEnabled = s.FillLight;
        appearance.RimEnabled = s.RimLight;
        appearance.RotateMainLight = s.RotateLight;
        appearance.RotationSpeed = Math.Clamp(s.RotationSpeed, 10, 120);
    }

    private void SaveSettings()
    {
        var s = _settings;
        if (!_fullscreen && !_hwnd.IsNull)
        {
            var placement = new Windows.Win32.UI.WindowsAndMessaging.WINDOWPLACEMENT { length = (uint)sizeof(Windows.Win32.UI.WindowsAndMessaging.WINDOWPLACEMENT) };
            if (Windows.Win32.PInvoke.GetWindowPlacement(_hwnd, &placement))
            {
                var r = placement.rcNormalPosition;
                (s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight) = (r.left, r.top, r.right - r.left, r.bottom - r.top);
                s.Maximized = placement.showCmd == Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED;
            }
        }

        s.ShowLibrary = _showLibrary;
        s.ShowModels = _showModels;
        s.GridView = _gridView;
        s.ThumbnailSize = _thumbnailSize;
        s.Sort = Math.Max(0, _library.SortOptions.ToList().IndexOf(_library.Sort));
        s.SyncCameras = _workspace.SyncCameras;
        s.AlignCenters = _workspace.AlignCenters;
        s.KeepView = _workspace.KeepView;
        s.CompareMode = _workspace.Mode.ToString();
        s.Tolerance = _workspace.Tolerance;
        s.ShowBoundingBox = _workspace.ShowBoundingBox;
        s.ShowBuildPlate = _workspace.ShowBuildPlate;
        s.BuildPlate = _workspace.Plate.Name;

        var appearance = _workspace.Appearance;
        s.Lighting = appearance.Lighting.Name;
        s.Shading = appearance.Shading.Name;
        s.ModelColor = appearance.Color.Name;
        s.AmbientEnabled = appearance.AmbientEnabled;
        s.AmbientIntensity = appearance.AmbientIntensity;
        s.MainLight = appearance.MainEnabled;
        s.FillLight = appearance.FillEnabled;
        s.RimLight = appearance.RimEnabled;
        s.RotateLight = appearance.RotateMainLight;
        s.RotationSpeed = appearance.RotationSpeed;
        s.Save(Settings.DefaultPath);
    }
}
