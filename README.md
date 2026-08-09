# 3D Model Explorer

A fast, local-first Windows application for indexing, searching, previewing, and inspecting STL and 3MF files.

> **AI-assisted development:** 3D Model Explorer was developed with substantial assistance from generative-AI coding tools. A human maintainer directed the work, reviewed the results, and owns the release decisions. See [AI-Assisted Development](AI_ASSISTED_DEVELOPMENT.md) for the full disclosure.

## Download

Download the latest `3DModelExplorer-*-win-x64.zip` from [GitHub Releases](https://github.com/GrafXP/3DModelExplorer/releases/latest).

1. Extract the entire ZIP into a folder.
2. Run `ModelExplorer.exe`.
3. Add one or more folders containing STL or 3MF files.

The release is self-contained, so a separate .NET installation is not required. The executable is not currently code-signed; Windows SmartScreen may show an unrecognized-app warning on first launch.

## Requirements

- Windows 10 or Windows 11, x64
- A DirectX 11-capable graphics adapter
- Enough memory for the models being viewed; very large meshes are loaded into memory

## Features

- Fast recursive indexing of local folders and network shares
- Binary and ASCII STL support
- 3MF support, including build transforms, components, and unit conversion
- Instant in-memory search with folder, format, size, and sorting controls
- Virtualized list and thumbnail-grid views with a persistent thumbnail cache
- DirectX 11 viewport with orbit, pan, zoom, ViewCube, coordinate axes, and FPS display
- Model dimensions, a toggleable bounding box, and printer build-plate presets
- A freely rotatable cutting plane that creates a real capped surface for closed meshes
- Configurable ambient, main, fill, and rim lighting with an animated rotating light
- Smooth, matte, flat, edge-overlay, and wireframe shading modes
- Multiple model-color presets
- Drag-and-drop, Open in Explorer, and Copy path workflows

All model parsing, indexing, thumbnails, and search data stay on the local computer. The index and thumbnail cache are stored under `%LOCALAPPDATA%\ModelExplorer`.

## Viewport controls

| Action | Control |
|---|---|
| Orbit | Left drag |
| Pan | Middle drag or Shift + left drag |
| Zoom | Mouse wheel or Ctrl + left drag |
| Set orbit pivot | Right-click a model surface |
| Clear orbit pivot | Right-click empty space |
| Fit model | `F` while the viewport is focused |
| Toggle bounding box | `B` |
| Toggle build plate | `P` |
| Toggle cutting plane | `C` |
| Open appearance panel | `A` |
| Open a model directly | `Ctrl+O` |

## Known limitations

- Windows x64 only; there are no macOS or Linux builds.
- STL and 3MF are the only supported model formats in version 1.0.0.
- Folder changes require a manual rescan; live filesystem watching is not implemented yet.
- Cutting an open or non-manifold mesh may leave uncapped contours. The viewport reports when this happens.
- The release is portable and unsigned; there is no installer or automatic updater.

## Building from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
dotnet restore ModelExplorer.slnx
dotnet build ModelExplorer.slnx
dotnet test ModelExplorer.slnx
dotnet run --project src/ModelExplorer.App
```

To create the same self-contained ZIP used for GitHub Releases:

```powershell
./scripts/Publish-Release.ps1 -Version 1.0.0
```

The artifact is written to `artifacts/`.

## Contributing and security

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md) before opening a pull request. Please report security problems according to [SECURITY.md](SECURITY.md), not in a public issue.

## License and acknowledgements

3D Model Explorer is available under the [MIT License](LICENSE). Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
