# 3D Model Explorer

A fast, local-first Windows application for indexing, searching, previewing, and inspecting STL and 3MF files.

> **AI-assisted development:** 3D Model Explorer was developed with substantial assistance from generative-AI coding tools. A human maintainer directed the work, reviewed the results, and owns the release decisions. See [AI-Assisted Development](AI_ASSISTED_DEVELOPMENT.md) for the full disclosure.

## Download

Download the latest `3DModelExplorer-*-win-x64.zip` from [GitHub Releases](https://github.com/GrafXP/3DModelExplorer/releases/latest).

1. Extract the entire ZIP into a folder.
2. Run `ModelExplorer.exe`.
3. Add one or more folders containing STL or 3MF files.

The release is a native executable with four small native libraries beside it; no .NET installation is required. The executable is not currently code-signed; Windows SmartScreen may show an unrecognized-app warning on first launch.

## Requirements

- Windows 10 or Windows 11, x64
- A DirectX 11-capable graphics adapter (without one, Windows' software rasterizer is used, slowly)
- Enough memory for the models being viewed; very large meshes are loaded into memory

## Features

- Opens in a fraction of a second: a native executable with a Direct3D 11 and Dear ImGui interface, drawing only when something changes
- Fast recursive indexing of local folders and network shares
- Binary and ASCII STL support
- 3MF support, including build transforms, components, and unit conversion
- Instant in-memory search with folder, format, size, and sorting controls
- Thumbnail grid and details list that draw only what is in view, with a persistent thumbnail cache
- Every selected model is shown at once, arranged automatically — no layout to pick
- Recently viewed models stay in memory and the next model in the list is loaded ahead, so stepping through a library is instant
- Linked rotation, pan, zoom and picked pivots across all panes, with optional bounding-box centre alignment
- Overlay mode: all selected models in one pane, each in its own colour, with per-model opacity
- Deviation mode: every model coloured by its surface distance from a chosen reference, with adjustable tolerance
- Model dimensions, a toggleable bounding box, and printer build-plate presets with a fit check
- One freely rotatable cutting plane, shared by all panes, that creates a real capped surface for closed meshes
- Configurable ambient, main, fill, and rim lighting with an animated rotating light
- Smooth, matte, flat, edge-overlay, and wireframe shading modes, and several model colours
- Dockable panels; window, panels and view settings are remembered
- Drag-and-drop, Open in Explorer, and Copy path workflows

All model parsing, indexing, thumbnails, and search data stay on the local computer. The index, thumbnail cache, settings and panel layout are stored under `%LOCALAPPDATA%\ModelExplorer`.

## Controls

| Action | Control |
|---|---|
| Show a model | Click it in the Models panel |
| Show several models side by side | Ctrl+click or Shift+click; Ctrl+A selects every model in the list |
| Step through the models | Arrow keys, Home, End (Shift extends the selection) |
| Orbit | Left drag |
| Pan | Middle drag or Shift + left drag |
| Zoom at the cursor | Mouse wheel or Ctrl + left drag |
| Set orbit pivot | Right-click a model surface |
| Clear orbit pivot | Right-click empty space |
| Fit | `F`, or double-click a pane |
| Look along an axis | Click an end of the axis triad in a pane's corner |
| Make a pane the active one | Click it, or `1`–`9` |
| Overlay / deviation heatmap | `O` / `H` (press again for side by side) |
| Toggle bounding box, build plate, cutting plane | `B`, `P`, `C` |
| Lighting and appearance | `A` |
| Keep the view when models change | `L` |
| Hide or show the panels | `Tab` |
| Open models directly | `Ctrl+O`, or drop files on the window (with Ctrl: add them) |
| Copy path, show in Explorer | `Ctrl+C`, `Ctrl+E` (the active model) |
| Fullscreen | `F11` |
| All keys | `F1` |

## Viewing and comparing models

Whatever is selected in the Models panel is shown. One selected model fills the view. Two or more get a pane each, arranged in the grid that shows them largest: two models side by side in a wide window, four in a 2 × 2 grid, up to sixteen at once. Files opened with `Ctrl+O`, dropped on the window, or named on the command line are shown the same way; dropping a folder adds it to the library.

While the next model loads, the previous one stays on screen, so stepping through a library with the arrow keys never shows an empty view. The models just before and after the selected one are loaded ahead in the background.

**Linked** cameras turn, pan and zoom every pane together, at a common scale, so the same side of each model is always compared. Uncheck it to navigate the panes independently. **Align centers** draws every model with its bounding-box centre at the origin, keeping scale and orientation; it does not rotate or best-fit models. **Keep view** keeps the camera when the selection changes instead of framing the new models.

**Overlay** draws every shown model in one pane, each in its own colour, see-through. The opacity sliders in the toolbar reveal overlaps and differences; the blending is order-independent, so no model hides another by being drawn last.

**Deviation** colours every model by its distance from the active model's surface (blue frame); click another pane to measure against that one instead. With exactly two models, the reference is coloured too, by its distance from the other, so additions and missing regions show from both sides. Green is within the adjustable tolerance (default **0.1 mm**); yellow through red is increasing deviation up to the largest found, on one scale for all panes. Each pane reports its sampled maximum and mean distance and the share of triangles within tolerance. Changing the tolerance recolours instantly; measurements run in the background and are cancelled when leaving the mode.

The comparison samples each triangle at its three vertices and face centre, and the largest sampled distance colours it. The reported maximum is an approximation, not an exact Hausdorff distance; small differences between samples can be missed. Mean distances are sample averages, and triangle percentages are not surface-area percentages. This is an unsigned surface comparison, not a solid subtraction or an inside/outside test. Deviation uses the original meshes; a cutting plane only hides part of them.

The **cutting plane** applies to every pane, placed relative to each model's own centre, so it takes the same section through every variant being compared. Turn it with the rings of the handle in the active pane and move it with its arrow, or use the Cut plane window's axis buttons and offset slider. While the plane moves, the GPU shows a preview; once it is still, each model is replaced by a real capped mesh.

## Known limitations

- Windows x64 only; there are no macOS or Linux builds.
- STL and 3MF are the only supported model formats. STEP and other CAD formats are being researched; see the [plan](PLAN.md).
- Folder changes require a manual rescan; live filesystem watching is not implemented yet.
- Cutting an open or non-manifold mesh may leave uncapped contours. The pane reports when this happens.
- The release is portable and unsigned; there is no installer or automatic updater.

## Building from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
dotnet restore ModelExplorer.slnx
dotnet build ModelExplorer.slnx
dotnet test ModelExplorer.slnx
dotnet run --project src/ModelExplorer.App
```

The compiled shaders are checked in (`src/ModelExplorer.App/Render/Shaders.g.cs`). When the Windows SDK is installed, the build compiles them again whenever a file in `src/ModelExplorer.App/Shaders` changes.

The release is compiled ahead of time to native code, which additionally needs the MSVC linker (Visual Studio or Build Tools with "Desktop development with C++"). To create the same ZIP used for GitHub Releases:

```powershell
./scripts/Publish-Release.ps1 -Version 1.0.0
```

The artifact is written to `artifacts/`.

## Contributing and security

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md) before opening a pull request. Please report security problems according to [SECURITY.md](SECURITY.md), not in a public issue.

## License and acknowledgements

3D Model Explorer is available under the [MIT License](LICENSE). Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
