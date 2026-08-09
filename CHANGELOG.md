# Changelog

All notable changes to 3D Model Explorer are documented here. The project follows [Semantic Versioning](https://semver.org/).

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
