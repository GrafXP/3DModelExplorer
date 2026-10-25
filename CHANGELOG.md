# Changelog

All notable changes to 3D Model Explorer are documented here. The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- The interface was rebuilt on a plain Win32 window, Direct3D 11 and Dear ImGui, compiled ahead of time to native code. The window appears in well under 100 ms and the library and first model in about half a second (previously more than a second for an empty window); an idle window uses no CPU.
- The release is one native executable with four native libraries, a 7 MB download instead of 94 MB.
- The selection decides what is shown: every selected model gets a pane, arranged automatically (up to sixteen). There is no layout to choose.
- Panels dock and can be rearranged; the window, panels and view settings are remembered between runs.
- Appearance, bounding box, build plate and the cutting plane apply to every pane. The cutting plane is placed relative to each model's centre, so it takes the same section through each.
- The cutting plane's handle now turns about any axis and moves along the normal, with axis presets and an offset slider beside it.

### Added

- Linked rotation, pan, zoom and picked pivots across all panes, with optional bounding-box centre alignment.
- Overlay mode: every shown model in one pane, in its own colour, with per-model opacity and order-independent transparency.
- Deviation mode: every model coloured by its sampled surface distance from the active model, with adjustable tolerance; with two models, both directions are shown. Changing the tolerance recolours without recomputing.
- Recently viewed models stay in memory, and the models next to the selection are loaded ahead, so stepping through the library is instant. The previous model stays on screen until the next can be drawn.
- Keyboard navigation through the results, a click on the axis triad to look along an axis, and a key overview (F1).
- Ctrl+drop adds files to what is shown; a dropped folder is added to the library.

## [1.0.0] - 2026-08-09

### Added

- Local and network-folder libraries backed by a persistent SQLite index.
- Fast ranked search, sorting, folder filtering, format filtering, and size filtering.
- Virtualized list and thumbnail views with background thumbnail generation and caching.
- Binary/ASCII STL and component-aware 3MF loading.
- DirectX 11 model viewer with configurable navigation and surface-pivot selection.
- Bounding-box dimensions and selectable printer build plates with fit warnings.
- Freely rotatable cutting plane with CPU-generated caps for closed contours.
- Studio, balanced, front, side, and top lighting rigs.
- Ambient-light controls, independently switchable directional lights, and an orbiting light.
- Smooth, matte, flat, edge-overlay, and wireframe modes plus nine model colors.
- Portable, self-contained Windows x64 release packaging.

### Notes

- This first public release is unsigned and may trigger Windows SmartScreen.
- Folder updates are picked up by manually rescanning the library.
- The application was developed with substantial generative-AI assistance under human direction and review.

[1.0.0]: https://github.com/GrafXP/3DModelExplorer/releases/tag/v1.0.0
