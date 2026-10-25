# Changelog

All notable changes to 3D Model Explorer are documented here. The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Independent one-, two-, and four-model viewer layouts, multi-file opening, and a library visibility toggle.
- Linked rotation, panning, zoom, fitting, and picked orbit pivots across visible panes.
- A synchronized third pane whose default comparison overlays A/B in two colors with independent opacity sliders and optional bounding-box center alignment.
- An optional bidirectional deviation heatmap with adjustable tolerance, calculated only when explicitly activated.
- Background comparison with cancellation and automatic invalidation/recalculation when source models change.

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
