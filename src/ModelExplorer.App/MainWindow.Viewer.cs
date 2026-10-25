using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using ModelExplorer.App.Render;
using ModelExplorer.Core.Models;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Geometry;
using Windows.Win32;

namespace ModelExplorer.App;

// The panes: where they go, what is drawn in them, how the mouse moves their
// cameras, and the labels, axes and handles drawn over them.
internal sealed unsafe partial class MainWindow
{
    private enum MouseButton { Left, Middle, Right }

    private enum DragKind { None, Orbit, Pan, Zoom }

    private const uint ColorActive = 0xFFF59621;     // #2196F5, the accent blue (ABGR)
    private const uint ColorPaneBorder = 0xFF2E2E2E;
    private const uint ColorText = 0xFFE6E6E6;
    private const uint ColorTextDim = 0xFF9E9E9E;
    private const uint ColorPlate = 0xB0141414;
    private const uint ColorError = 0xFF5252E0;
    private const uint ColorDimension = 0xFFE1D04D;
    private const uint ColorPivot = 0xFF268CFF;

    private PaneRect _viewRect;
    private PaneRect[] _panes = [];
    private DragKind _drag;
    private MouseButton _dragButton;
    private int _dragPane = -1;
    private int _mouseX, _mouseY, _downX, _downY;
    private bool _rightDown;
    private bool _gizmoActive;
    private bool _gizmoWasUsing;
    private readonly List<(int Pane, Vector2 Center, float Radius, Vector3 Direction)> _axisTargets = [];

    private bool IsDragging => _drag != DragKind.None || _rightDown;

    private float Scale => (float)_dpiScale;

    /// <summary>Divides the view into panes for this frame and lets the workspace frame new models in them.</summary>
    private void LayoutPanes(PaneRect view)
    {
        _viewRect = view;
        _panes = PaneLayout.Arrange(_workspace.PaneCount, view, (int)MathF.Round(3 * Scale));
        _workspace.Layout(_panes);
    }

    private int PaneAt(int x, int y)
    {
        for (var i = 0; i < _panes.Length; i++)
        {
            if (_panes[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ rendering

    private void RenderPanes()
    {
        var renderer = _renderer!;
        var appearance = _workspace.Appearance;
        for (var p = 0; p < _panes.Length; p++)
        {
            ref var camera = ref _workspace.Camera(p);
            if (!camera.IsSet)
            {
                continue;
            }

            if (_workspace.Mode == CompareMode.Overlay)
            {
                RenderOverlayPane(renderer, _panes[p], camera, appearance);
                continue;
            }

            if (_workspace.Shown(p) is not { IsLoaded: true, Gpu: GpuMesh gpuMesh, Mesh: { } mesh } model)
            {
                continue;
            }

            var offset = _workspace.Offset(model);
            var cut = _workspace.CutResult(p);
            var deviation = _workspace.Deviation(p);
            var drawnBounds = DrawnBounds(model, cut);
            renderer.BeginPane(_panes[p], camera, appearance, SceneBoundsWithPlate(drawnBounds));

            if (deviation is not null || cut is not { Gpu: GpuMesh })
            {
                // The deviation colours belong to the original triangles, so a cut
                // model in the deviation view keeps the clipped original.
                renderer.DrawModel(gpuMesh, offset, appearance,
                    clip: _workspace.CutEnabled ? _workspace.CutPlane(model) : null,
                    distances: deviation?.Gpu as GpuDistances,
                    tolerance: _workspace.Tolerance,
                    maximum: _workspace.DeviationMaximum);
            }
            else
            {
                renderer.DrawModel((GpuMesh)cut.Gpu!, offset, appearance);
            }

            if (_workspace.ShowBuildPlate)
            {
                renderer.DrawPlate(_workspace.Plate, drawnBounds, _workspace.Plate.Fits(drawnBounds.Size));
            }

            if (_workspace.ShowBoundingBox)
            {
                renderer.DrawBox(drawnBounds);
            }

            if (_workspace.CutEnabled)
            {
                renderer.DrawCutPlane(CutPlaneTransform(model), CutPlaneSide(mesh.Bounds));
            }
        }
    }

    private void RenderOverlayPane(SceneRenderer renderer, PaneRect pane, in OrbitCamera camera, Appearance appearance)
    {
        var draws = new List<ModelDraw>();
        var bounds = BoundingBox.Empty;
        foreach (var (index, model) in _workspace.LoadedModels())
        {
            if (model.Gpu is GpuMesh mesh)
            {
                draws.Add(new ModelDraw(mesh, _workspace.Offset(model), Workspace.OverlayColor(index), _workspace.Opacity[index],
                    _workspace.CutEnabled ? _workspace.CutPlane(model) : null));
                bounds = bounds.Union(_workspace.Bounds(model));
            }
        }

        renderer.BeginPane(pane, camera, appearance, SceneBoundsWithPlate(bounds));
        if (_workspace.ShowBuildPlate && !bounds.IsEmpty)
        {
            renderer.DrawPlate(_workspace.Plate, bounds, _workspace.Plate.Fits(bounds.Size));
        }

        renderer.DrawOverlay([.. draws], appearance);
        if (_workspace.ShowBoundingBox)
        {
            renderer.DrawBox(bounds);
        }

        if (_workspace.CutEnabled && _workspace.Shown(_workspace.Active) is { IsLoaded: true, Mesh: { } active } activeModel)
        {
            renderer.DrawCutPlane(CutPlaneTransform(activeModel), CutPlaneSide(active.Bounds));
        }
    }

    /// <summary>The model's bounds where it is drawn: the cut mesh's once there is one.</summary>
    private BoundingBox DrawnBounds(LoadedModel model, CutView? cut)
    {
        if (cut is null || cut.Mesh.Bounds.IsEmpty)
        {
            return _workspace.Bounds(model);
        }

        var offset = _workspace.Offset(model);
        return new BoundingBox(cut.Mesh.Bounds.Min + offset, cut.Mesh.Bounds.Max + offset);
    }

    /// <summary>What the near plane has to make room for: the model, and the plate under it when shown.</summary>
    private BoundingBox SceneBoundsWithPlate(BoundingBox bounds)
    {
        if (!_workspace.ShowBuildPlate || bounds.IsEmpty)
        {
            return bounds;
        }

        var plate = _workspace.Plate;
        var center = bounds.Center;
        var half = new Vector3(plate.Width * 0.5f, plate.Depth * 0.5f, 0);
        return bounds.Union(new Vector3(center.X, center.Y, bounds.Min.Z) - half).Union(new Vector3(center.X, center.Y, bounds.Min.Z) + half);
    }

    private Matrix4x4 CutPlaneTransform(LoadedModel model) =>
        _workspace.Cut.Transform(model.Mesh!.Bounds) * Matrix4x4.CreateTranslation(_workspace.Offset(model));

    private static float CutPlaneSide(BoundingBox bounds) => MathF.Max(bounds.Size.Length() * 1.15f, 0.1f);

    // ------------------------------------------------------------------ mouse

    private void OnMouseDown(MouseButton button, int x, int y, bool shift, bool control)
    {
        _mouseX = _downX = x;
        _mouseY = _downY = y;
        var pane = PaneAt(x, y);
        if (pane < 0 || IsDragging)
        {
            return;
        }

        if (_workspace.Mode != CompareMode.Overlay && pane != _workspace.Active)
        {
            _workspace.Active = pane;
            UpdateTitle();
        }

        if (button == MouseButton.Left && AxisTargetAt(pane, x, y) is { } direction)
        {
            ref var camera = ref _workspace.Camera(pane);
            camera.Pivot = null;
            camera.SetDirection(-direction);
            Invalidate();
            return;
        }

        _dragPane = pane;
        _dragButton = button;
        switch (button)
        {
            case MouseButton.Left:
                _drag = control ? DragKind.Zoom : shift ? DragKind.Pan : DragKind.Orbit;
                break;
            case MouseButton.Middle:
                _drag = DragKind.Pan;
                break;
            default:
                _rightDown = true;
                break;
        }

        // ImGui's backend has usually taken the capture already, on the same
        // message; taking it again would only make Windows report a change.
        if (PInvoke.GetCapture() != _hwnd)
        {
            PInvoke.SetCapture(_hwnd);
        }

        Invalidate();
    }

    /// <param name="buttons">
    /// The MK_* flags of the message: which buttons are down now. A drag whose
    /// button was let go somewhere this window never heard of ends here. Null when
    /// not known (the test hook).
    /// </param>
    private void OnMouseMove(int x, int y, nuint? buttons = null)
    {
        var dx = x - _mouseX;
        var dy = y - _mouseY;
        _mouseX = x;
        _mouseY = y;
        if (_drag == DragKind.None || _dragPane < 0 || _dragPane >= _panes.Length)
        {
            return;
        }

        var held = _dragButton == MouseButton.Middle ? 0x0010u : 0x0001u; // MK_MBUTTON, MK_LBUTTON
        if (buttons is { } down && (down & held) == 0)
        {
            EndDrag();
            if (PInvoke.GetCapture() == _hwnd)
            {
                PInvoke.ReleaseCapture();
            }

            return;
        }

        var pane = _panes[_dragPane];
        ref var camera = ref _workspace.Camera(_dragPane);
        switch (_drag)
        {
            case DragKind.Orbit:
                camera.Orbit(dx, dy);
                break;
            case DragKind.Pan:
                camera.Pan(dx, dy, pane.Height);
                break;
            case DragKind.Zoom:
                // Towards the point the drag started on, as the wheel zooms at the cursor.
                var (ndcX, ndcY) = Ndc(pane, _downX, _downY);
                camera.Zoom(MathF.Exp(dy * 0.006f), ndcX, ndcY, pane.Aspect);
                break;
        }

        Invalidate();
    }

    private void OnMouseUp(MouseButton button, int x, int y)
    {
        var wasRight = _rightDown && button == MouseButton.Right;
        var pane = _dragPane;
        if (button == MouseButton.Right)
        {
            _rightDown = false;
        }
        else
        {
            _drag = DragKind.None;
        }

        if (!IsDragging)
        {
            _dragPane = -1;
            PInvoke.ReleaseCapture();
        }

        // A right click that did not move picks the point to turn about.
        if (wasRight && pane >= 0 && Math.Abs(x - _downX) <= 4 && Math.Abs(y - _downY) <= 4)
        {
            PickPivot(pane, x, y);
        }

        Invalidate();
    }

    private void EndDrag()
    {
        _drag = DragKind.None;
        _rightDown = false;
        _dragPane = -1;
    }

    private void OnMouseWheel(int delta, int x, int y)
    {
        var p = PaneAt(x, y);
        if (p < 0)
        {
            return;
        }

        var pane = _panes[p];
        var (ndcX, ndcY) = Ndc(pane, x, y);
        _workspace.Camera(p).Zoom(MathF.Pow(1 / 1.2f, delta / (float)PInvoke.WHEEL_DELTA), ndcX, ndcY, pane.Aspect);
        Invalidate();
    }

    private void OnDoubleClick(int x, int y)
    {
        var pane = PaneAt(x, y);
        if (pane >= 0)
        {
            _workspace.FitPane(pane);
            Invalidate();
        }
    }

    private static (float X, float Y) Ndc(PaneRect pane, int x, int y) =>
        (((x - pane.X + 0.5f) / pane.Width * 2) - 1, 1 - ((y - pane.Y + 0.5f) / pane.Height * 2));

    /// <summary>
    /// Makes the surface point under the cursor the centre the view turns about;
    /// empty space clears it. Without it, orbiting a large print always swings
    /// around its middle, which is useless when inspecting one corner of it.
    /// </summary>
    private void PickPivot(int pane, int x, int y)
    {
        var rect = _panes[pane];
        ref var camera = ref _workspace.Camera(pane);
        var (ndcX, ndcY) = Ndc(rect, x, y);
        var (origin, direction) = camera.Ray(ndcX, ndcY, rect.Aspect);

        float? nearest = null;
        IEnumerable<(int Index, LoadedModel Model)> candidates = _workspace.Mode == CompareMode.Overlay
            ? _workspace.LoadedModels()
            : _workspace.Shown(pane) is { IsLoaded: true, Mesh: not null } shown ? [(pane, shown)] : [];
        foreach (var (index, model) in candidates)
        {
            // The ray in the model's own coordinates; what a cut removed is not hit.
            var local = origin - _workspace.Offset(model);
            var cut = _workspace.Mode == CompareMode.Overlay ? null : _workspace.CutResult(index);
            var hit = cut is not null
                ? MeshPicking.Raycast(cut.Mesh, local, direction)
                : MeshPicking.Raycast(model.Mesh!, local, direction, _workspace.CutEnabled && _workspace.Mode != CompareMode.Overlay ? _workspace.CutPlane(model) : null);
            if (hit is { } t && (nearest is null || t < nearest))
            {
                nearest = t;
            }
        }

        if (nearest is { } distance)
        {
            var point = origin + (direction * distance);
            _workspace.SetPivot(pane, point);
            Notice($"Rotation centre set to {point.X:N1}, {point.Y:N1}, {point.Z:N1} mm — right-click empty space to clear");
        }
        else if (camera.Pivot is not null)
        {
            _workspace.SetPivot(pane, null);
            Notice("Rotation centre reset to the view's centre");
        }
    }

    private Vector3? AxisTargetAt(int pane, int x, int y)
    {
        foreach (var target in _axisTargets)
        {
            if (target.Pane == pane && Vector2.Distance(target.Center, new Vector2(x, y)) <= target.Radius)
            {
                return target.Direction;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ overlays

    /// <summary>Everything drawn over the panes: frames, labels, measurements, the axes, the pivot and the plane's handle.</summary>
    private void DrawPaneOverlays()
    {
        var draw = ImGui.GetBackgroundDrawList();
        _axisTargets.Clear();
        if (_workspace.Count == 0)
        {
            CenteredText(draw, _viewRect, "Select models in the library to view them\n\nCtrl+click or Shift+click to compare several\nCtrl+O or drop STL / 3MF files to open them directly", ColorTextDim);
            return;
        }

        for (var p = 0; p < _panes.Length; p++)
        {
            var rect = _panes[p];
            var p0 = new Vector2(rect.X, rect.Y);
            var p1 = new Vector2(rect.Right, rect.Bottom);
            draw.PushClipRect(p0, p1, true);
            if (_panes.Length > 1)
            {
                draw.AddRect(p0, p1, p == _workspace.Active ? ColorActive : ColorPaneBorder, 0, ImDrawFlags.None, p == _workspace.Active ? 2 * Scale : Scale);
            }

            ref var camera = ref _workspace.Camera(p);
            if (_workspace.Mode == CompareMode.Overlay)
            {
                DrawOverlayLegend(draw, rect);
            }
            else
            {
                DrawPaneLabel(draw, rect, p);
                if (_workspace.ShowBoundingBox && _workspace.Shown(p) is { IsLoaded: true } model && camera.IsSet)
                {
                    DrawDimensions(draw, rect, camera, DrawnBounds(model, _workspace.CutResult(p)));
                }
            }

            if (camera.IsSet)
            {
                if (camera.Pivot is { } pivot && camera.Project(pivot, rect.Width, rect.Height) is { } at)
                {
                    var center = p0 + at;
                    draw.AddCircleFilled(center, 5 * Scale, 0xFF000000);
                    draw.AddCircleFilled(center, 3.5f * Scale, ColorPivot);
                }

                DrawAxes(draw, rect, p, camera);
            }

            draw.PopClipRect();
        }

        DrawCutGizmo();
    }

    private void DrawPaneLabel(ImDrawListPtr draw, PaneRect rect, int pane)
    {
        var requested = _workspace.Requested(pane);
        var shown = _workspace.Shown(pane);
        if (requested is null)
        {
            return;
        }

        var lines = new List<(string Text, uint Color)> { (requested.Name, ColorText) };
        if (shown is not null && !ReferenceEquals(shown, requested))
        {
            // Still showing the previous model while the next one loads.
            lines[0] = ($"Loading {requested.Name}…", ColorText);
            lines.Add((shown.Name, ColorTextDim));
        }

        var model = shown ?? requested;
        if (model.IsLoaded && model.Mesh is { } mesh)
        {
            var cut = _workspace.CutResult(pane);
            var size = (cut?.Mesh.Bounds ?? mesh.Bounds).Size;
            var triangles = cut?.Mesh.TriangleCount ?? mesh.TriangleCount;
            lines.Add(($"{size.X:N1} × {size.Y:N1} × {size.Z:N1} mm · {triangles:N0} triangles{(cut is null ? string.Empty : " after cut")}", ColorTextDim));
            if (_workspace.ShowBuildPlate)
            {
                var plate = _workspace.Plate;
                lines.Add(plate.Fits(size)
                    ? ($"Fits {plate.Name}", ColorTextDim)
                    : ($"Too large for {plate.Name} — over on {string.Join(", ", plate.Overruns(size))}", ColorError));
            }

            if (_workspace.CutText(pane) is { Length: > 0 } cutText)
            {
                lines.Add((cutText, ColorTextDim));
            }

            if (_workspace.DeviationText(pane) is { Length: > 0 } deviationText)
            {
                lines.Add((deviationText, ColorText));
            }
        }

        Plate(draw, new Vector2(rect.X, rect.Y) + new Vector2(8 * Scale), lines);

        if (shown is null && requested.State == ModelState.Loading)
        {
            CenteredText(draw, rect, $"Loading {requested.Name}…", ColorTextDim);
        }
        else if (model.State == ModelState.Failed)
        {
            CenteredText(draw, rect, $"Could not open {model.Name}\n\n{model.Error}\n\nCtrl+C copies its path, Ctrl+E shows it in Explorer", ColorError);
        }
        else if (model.IsLoaded && model.Mesh is { TriangleCount: 0 })
        {
            CenteredText(draw, rect, $"{model.Name} contains no geometry", ColorTextDim);
        }
        else if (model.IsLoaded && model.Gpu is null && _gpuError is null)
        {
            CenteredText(draw, rect, "Not enough video memory to display this model", ColorError);
        }
    }

    private void DrawOverlayLegend(ImDrawListPtr draw, PaneRect rect)
    {
        var lines = new List<(string, uint)>();
        foreach (var (index, model) in _workspace.LoadedModels())
        {
            var color = Workspace.OverlayColor(index);
            lines.Add(($"■ {model.Name}", ImGui.GetColorU32(new Vector4(color, 1))));
        }

        for (var i = 0; i < _workspace.Count; i++)
        {
            if (_workspace.Requested(i) is { State: ModelState.Loading } loading)
            {
                lines.Add(($"Loading {loading.Name}…", ColorTextDim));
            }
            else if (_workspace.Requested(i) is { State: ModelState.Failed } failed)
            {
                lines.Add(($"Could not open {failed.Name}: {failed.Error}", ColorError));
            }
        }

        Plate(draw, new Vector2(rect.X, rect.Y) + new Vector2(8 * Scale), lines);
    }

    /// <summary>
    /// The model's three extents, at the edges meeting at the corner nearest the
    /// default view. Fixed size on screen, so they stay legible at any zoom.
    /// </summary>
    private void DrawDimensions(ImDrawListPtr draw, PaneRect rect, in OrbitCamera camera, BoundingBox bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var size = bounds.Size;
        var center = bounds.Center;
        var min = bounds.Min;
        var gap = MathF.Max(bounds.MaxExtent * 0.03f, 0.01f);
        Label(new Vector3(center.X, min.Y - gap, min.Z - gap), $"X {size.X:N1}", camera);
        Label(new Vector3(min.X - gap, center.Y, min.Z - gap), $"Y {size.Y:N1}", camera);
        Label(new Vector3(min.X - gap, min.Y - gap, center.Z), $"Z {size.Z:N1}", camera);

        void Label(Vector3 world, string text, in OrbitCamera view)
        {
            if (view.Project(world, rect.Width, rect.Height) is not { } at)
            {
                return;
            }

            var textSize = ImGui.CalcTextSize(text);
            var position = new Vector2(rect.X, rect.Y) + at - (textSize / 2);
            draw.AddRectFilled(position - new Vector2(3 * Scale, 1 * Scale), position + textSize + new Vector2(3 * Scale, 1 * Scale), ColorPlate, 3 * Scale);
            draw.AddText(position, ColorDimension, text);
        }
    }

    /// <summary>
    /// The world axes in the pane's lower left corner, turning with the view. A
    /// click on an axis end looks along it — the quick way to a front, side or top view.
    /// </summary>
    private void DrawAxes(ImDrawListPtr draw, PaneRect rect, int pane, in OrbitCamera camera)
    {
        var length = 26 * Scale;
        var origin = new Vector2(rect.X + (16 * Scale) + length, rect.Bottom - (16 * Scale) - length);
        (Vector3 Axis, uint Color, string Name)[] axes =
        [
            (Vector3.UnitX, 0xFF4F51F0, "X"),
            (Vector3.UnitY, 0xFF8FC936, "Y"),
            (Vector3.UnitZ, 0xFFF5A242, "Z"),
        ];

        // Far ends first, so the nearer axes are drawn over them.
        var right = camera.Right;
        var up = camera.Up;
        var forward = camera.Forward;
        foreach (var (axis, color, name) in axes.OrderByDescending(a => Vector3.Dot(a.Axis, forward)))
        {
            var end = origin + (new Vector2(Vector3.Dot(axis, right), -Vector3.Dot(axis, up)) * length);
            var back = origin - (new Vector2(Vector3.Dot(axis, right), -Vector3.Dot(axis, up)) * length * 0.55f);
            draw.AddLine(origin, end, color, 2 * Scale);
            draw.AddCircle(back, 4.5f * Scale, color, 0, 1.2f * Scale);
            draw.AddCircleFilled(end, 8 * Scale, color);
            var textSize = ImGui.CalcTextSize(name);
            draw.AddText(end - (textSize / 2), 0xFF101010, name);
            _axisTargets.Add((pane, end, 9 * Scale, axis));
            _axisTargets.Add((pane, back, 6 * Scale, -axis));
        }
    }

    /// <summary>
    /// The manipulator for the cutting plane, in the active pane: rings turn the
    /// plane, the arrow moves it along its normal. While it is dragged, the panes
    /// show the GPU preview; the real cut follows once it is let go.
    /// </summary>
    private void DrawCutGizmo()
    {
        // In the overlay the one pane carries the active model's handle.
        var pane = _workspace.Mode == CompareMode.Overlay ? 0 : _workspace.Active;
        if (!_workspace.CutEnabled || pane < 0 || pane >= _panes.Length || _workspace.Shown(_workspace.Active) is not { IsLoaded: true, Mesh: { } mesh } model)
        {
            if (_gizmoWasUsing)
            {
                _workspace.EndCutDrag();
            }

            _gizmoActive = _gizmoWasUsing = false;
            return;
        }

        var rect = _panes[pane];
        ref var camera = ref _workspace.Camera(pane);
        if (!camera.IsSet)
        {
            return;
        }

        ImGuizmo.SetDrawlist(ImGui.GetBackgroundDrawList());
        ImGuizmo.SetRect(rect.X, rect.Y, rect.Width, rect.Height);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetGizmoSizeClipSpace(0.16f);
        var view = camera.View;
        var radius = MathF.Max(mesh.Bounds.Size.Length() * 0.5f, 1e-3f);
        var near = camera.NearPlane(_workspace.Bounds(model).Center, radius);

        // ImGuizmo expects a conventional projection, not the renderer's reversed one.
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(OrbitCamera.FieldOfView, rect.Aspect, near, near * 1e5f);
        var transform = CutPlaneTransform(model);
        ImGuizmo.Manipulate((float*)&view, (float*)&projection, ImGuizmoOperation.Rotate | ImGuizmoOperation.TranslateZ, ImGuizmoMode.Local, (float*)&transform);

        var using_ = ImGuizmo.IsUsing();
        if (using_)
        {
            if (!_gizmoWasUsing)
            {
                _workspace.BeginCutDrag();
            }

            _workspace.Cut = _workspace.Cut.WithTransform(transform * Matrix4x4.CreateTranslation(-_workspace.Offset(model)), mesh.Bounds);
        }
        else if (_gizmoWasUsing)
        {
            _workspace.EndCutDrag();
        }

        _gizmoWasUsing = using_;
        _gizmoActive = using_ || ImGuizmo.IsOver();
    }

    /// <summary>Lines of text on a dark plate, one colour per line.</summary>
    private void Plate(ImDrawListPtr draw, Vector2 at, IReadOnlyList<(string Text, uint Color)> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var lineHeight = ImGui.GetTextLineHeight();
        var width = lines.Max(line => ImGui.CalcTextSize(line.Text).X);
        var padding = new Vector2(6 * Scale, 3 * Scale);
        draw.AddRectFilled(at, at + new Vector2(width, lines.Count * lineHeight) + (padding * 2), ColorPlate, 3 * Scale);
        for (var i = 0; i < lines.Count; i++)
        {
            draw.AddText(at + padding + new Vector2(0, i * lineHeight), lines[i].Color, lines[i].Text);
        }
    }

    private void CenteredText(ImDrawListPtr draw, PaneRect rect, string text, uint color)
    {
        var lines = text.Split('\n');
        var lineHeight = ImGui.GetTextLineHeight();
        var top = rect.Y + ((rect.Height - (lines.Length * lineHeight)) / 2);
        for (var i = 0; i < lines.Length; i++)
        {
            var size = ImGui.CalcTextSize(lines[i]);
            draw.AddText(new Vector2(MathF.Floor(rect.X + ((rect.Width - size.X) / 2)), MathF.Floor(top + (i * lineHeight))), color, lines[i]);
        }
    }
}
