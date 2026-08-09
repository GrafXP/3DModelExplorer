# 3D Model Explorer — MVP Implementation Plan

A fast WPF viewer + indexer for 3D printing models.

**Target:** .NET 10 (SDK 10.0.203 / WindowsDesktop 10.0.7 confirmed installed), WPF, dark theme, x64.
**MVP formats:** STL (binary + ASCII), 3MF.
**Out of MVP:** STEP — deliberately excluded. The geometry layer is built around `IGeometryLoader` so STEP can be added later as a self-contained loader with no changes elsewhere.
**Scale target:** 100k+ models across multiple roots, including network shares.

---

## Architecture

```
ModelExplorer.sln
├─ src/ModelExplorer.Geometry     net10.0          STL/3MF parsers, IGeometryLoader, MeshData
├─ src/ModelExplorer.Indexing     net10.0          scanner, SQLite store, search index, thumb cache
├─ src/ModelExplorer.App          net10.0-windows  WPF + MVVM (CommunityToolkit.Mvvm)
└─ tests/ModelExplorer.Tests      net10.0          xunit
```

### Packages

| Package | Version | Why |
|---|---|---|
| `HelixToolkit.Wpf.SharpDX` | 3.1.2 | DX11 viewport. Ships `net8.0-windows7.0` — loads fine on `net10.0-windows`. |
| `MaterialDesignThemes` + `MaterialDesignColors` | 5.3.2 | Dark theme and control set. Already proven on `net10.0-windows` in the adjacent `TradingAgent` project. |
| `Microsoft.Data.Sqlite` | 10.0.10 | Index persistence. Bundled `e_sqlite3` has FTS5 + trigram. |
| `CommunityToolkit.Mvvm` | 8.4.2 | Source-generated observables/commands. Already a Helix transitive dep. |
| `VirtualizingWrapPanel` | 2.5.4 | Virtualized thumbnail grid with container recycling. |
| `xunit` + `FluentAssertions` | latest | Tests. |

Parsers are hand-written — no Assimp. A dedicated binary-STL reader beats a general importer by a wide margin and removes a native dependency.

### Key design decisions

**Search is in-memory, SQLite is only persistence.** On load, the DB is projected into flat arrays (`string[] haystack` of lowercased `name\0relativePath`, parallel `int[] ids`). Queries run as a parallel vectorized `Span<char>.IndexOf` scan with AND-of-terms semantics and rank ordering (exact name > name prefix > name contains > path contains). At 100k entries that is ~4 MB of RAM and sub-millisecond queries — no query planner, no index maintenance, no surprises. SQLite FTS5 stays available for later advanced queries (tags, notes).

**Enumeration reads metadata from the directory walk itself.** A custom `FileSystemEnumerable<FileRecord>` transform pulls name, length and mtime straight off `FileSystemEntry` — no second `stat` per file. `RecurseSubdirectories = true`, `IgnoreInaccessible = true`, skip `System` and `ReparsePoint` attributes.

**Change detection is (size, mtimeTicks).** No full-file hashing during scan. A content key (xxHash64 of first 64 KB + size) is computed lazily, only when a thumbnail is generated, and doubles as the thumbnail cache key and duplicate detector.

**Network roots are isolated.** Each root gets its own scan pipeline with independent degree-of-parallelism (local = `ProcessorCount`, network = 2) so a slow NAS can never stall local indexing or the UI.

**Writes are batched.** WAL mode, `synchronous=NORMAL`, 10k-row transactions fed through a `System.Threading.Channels` producer/consumer.

**Thumbnails are files, not BLOBs.** `%LocalAppData%\ModelExplorer\thumbs\{hash[0..2]}\{hash}.png`, 256px, keyed by content so a model filed under three folders renders once. Rendered by a software rasterizer on background worker threads. Priority is request order, newest first, which in a virtualized grid *is* the viewport: rows ask as they scroll in and cancel as they recycle out.

### Performance budget (the actual acceptance criteria)

| Metric | Budget |
|---|---|
| Cold start → interactive window | < 800 ms |
| Index load (100k) → searchable | < 500 ms |
| Keystroke → filtered results | < 16 ms |
| Parse 2M-triangle binary STL | < 500 ms |
| Select → first frame of that model | < 600 ms |
| Grid scroll during thumb generation | ≥ 50 fps |
| Idle RSS at 100k indexed | < 400 MB |

---

## Steps

Each step ends at a gate you test by hand. Nothing proceeds until the gate passes.

### Step 0 — Skeleton and dark shell

Solution and four projects. Dark theme via `MaterialDesignThemes` (`BundledTheme BaseTheme="Dark"` + `MaterialDesign3.Defaults.xaml`), matching the setup already running on .NET 10 in the adjacent `TradingAgent` project, plus an app-level brush dictionary for surfaces the Material palette doesn't cover. Dark title bar via `DwmSetWindowAttribute`, applied in `OnSourceInitialized` so there's no light-to-dark flash. Three-pane layout: roots sidebar, results area, viewer — with draggable splitters and a status bar. No functionality behind it.

> **Gate:** `dotnet run` opens a dark window. Title bar is dark, no white flash on startup, splitters drag, layout survives resize and a DPI change (drag to a second monitor).

### Step 1 — STL parser + viewer

The riskiest piece, so it goes first. Binary/ASCII detection by content (`length == 84 + 50n`), binary path reads via pooled buffers + `Unsafe.ReadUnaligned` over the 50-byte records, normals recomputed (STL-supplied normals are frequently zero or wrong). `Viewport3DX` host, File→Open, orbit/pan/zoom, fit-to-bounds, ViewCube, ground grid. Status bar shows triangle count and parse time.

> **Gate:** Open a handful of your own STLs including the largest one you own. Orbiting stays smooth. Status bar shows the parse time — a 2M-triangle file must be under 500 ms and hold ≥ 55 fps while you rotate it. This also proves SharpDX works on .NET 10; if it doesn't, we find out here and not in step 6.

### Step 2 — 3MF parser

`ZipArchive` → resolve the model part through `_rels/.rels` → streaming `XmlReader` over `<vertices>`/`<triangles>`. Resolve `<build><item>` transforms and recursive `<components>`, honour the `unit` attribute. Same viewer path as STL, dispatched through `IGeometryLoader` by sniffed content.

> **Gate:** Open 3MFs from your slicer, including multi-part project files. Every part on the plate appears, at the right position, orientation and scale — compare side by side against the slicer.

### Step 3 — Scanner and index

Library roots UI (add/remove folders). Fast recursive enumeration, batched SQLite writes, live progress with a genuinely instant Cancel. Results shown in a plain virtualized list: name, folder, size, date. No thumbnails yet.

> **Gate:** Add your real model folders including one network share. The UI stays fully responsive throughout and the status bar reports files/sec. Cancel stops immediately. Close and reopen the app — the full list is there instantly, with no rescan.

### Step 4 — Instant search

In-memory index built at load. Debounced (~60 ms), cancellable, parallel, ranked. Query time and result count displayed. Side filters: extension, size range, folder subtree.

> **Gate:** Type into the search box against the full index. Results keep pace with your typing — no caret lag, no stutter, no flicker of stale results. Displayed query time stays under 10 ms.

### Step 5 — Selection wiring

Selecting a row loads it into the viewer asynchronously, with cancellation so fast keyboard scrubbing doesn't queue up a backlog of loads. Loading indicator, explicit error state for corrupt files, "Open in Explorer", "Copy path".

> **Gate:** Hold arrow-down through a few hundred models. The viewer keeps up, never freezes, and never shows a model that isn't the selected one. Watch Task Manager across the run — memory returns to baseline, nothing climbs.

### Step 6 — Thumbnails and grid view

Software rasterizer on background workers, disk cache, newest-request-first queue, placeholder → fade-in. `VirtualizingWrapPanel` grid with a size slider and a list/grid toggle.

HelixToolkit 3.1.2 turns out to have no windowless offscreen render path — `RenderToBitmapStream` captures a live `Viewport3DX`, so "offscreen DX" would have meant a hidden window on a second STA thread, or raw SharpDX with its own shaders. A 256px tile is 65k pixels and the cost is dominated by parsing the file, so the GPU buys nothing here. A CPU z-buffer rasterizer instead: no second device competing with the viewport, no STA thread or device-loss handling, works with no GPU at all, runs on as many workers as we like, and is deterministic enough to unit test. It mirrors the viewer's camera and lighting, so a tile looks like what clicking it gives you.

> **Gate:** Clear the thumbnail cache and scroll fast through 10k+ models. Scrolling stays smooth while thumbnails fill in, and the ones under your cursor render before off-screen ones. Restart the app — thumbnails appear instantly from cache.

### Step 6.5 — Result ordering, bounding box and build plate

Interim work, slotted in after gate 6 passed.

**Sorting.** Best match, name, date, size, format and folder, each with its
direction folded into the option ("Newest first", not "Date modified" plus an
arrow somewhere else). Ordering never touches the keystroke path: `ModelSearchIndex`
builds one permutation per field, lazily and once per snapshot, and a query walks
it — forwards or backwards — keeping the matches. Sorting 100k results per
keystroke would have blown the 16 ms budget on its own.

The sort and filter drop-downs moved to the top of the sidebar. Docked at the
bottom, a nine-item list opened past the bottom edge of the window and spilled
onto the desktop.

**Bounding box.** A toggleable wireframe box around the model with its X/Y/Z
extents labelled on the three edges meeting at the corner nearest the camera, and
the dimensions in the status bar whether the box is shown or not — the number a
print is most often checked against. One unit cube placed by a scale and a
translate, like the pivot marker, rather than new line geometry per model. Toggle
in a viewport overlay or `B` while the viewport has focus.

**Build plate.** A toggleable printer bed under the model, centred beneath its
actual bounds so models retain the position stored by their exporter. The default
is the Bambu Lab X1C / P1S profile (256 × 256 × 250 mm), with a deliberately short
printer picker whose usable volumes come from slicer profiles. The surface, 10 mm
grid and 50 mm outline share one transform; changing models only translates them,
while changing printer rebuilds the plate-local geometry once. The status bar
reports whether the model fits and lists every exceeded axis, worst first; the
outline turns red on an overrun. Toggle in the viewport overlay or `P` while the
viewport has focus. When visible, the plate contributes to the far clip plane so
a large bed is not sliced away behind a small model.

> **Gate:** Sort a full library by each field and confirm the order and that the
> results keep pace with typing. Toggle the box on a model whose bounds you know
> — the labels must match what your slicer reports. Load a model you know exceeds
> the selected printer and confirm the plate outline turns red and the status bar
> names the right axes and amounts. Switch printers with that model loaded, then
> load a 10–20 mm model and confirm the far edge of the bed remains visible. `P`
> must toggle the plate only while the viewport has focus.

### Step 7 — Incremental rescan and live updates

Diff by (size, mtime) — unchanged files are never reparsed. `FileSystemWatcher` per local root with debounce and event coalescing; timed polling fallback for network roots. Deletions removed from index, thumbnails invalidated on change.

> **Gate:** Add, rename and delete a model in a watched folder — the list reflects each within a couple of seconds with no manual rescan. Then rescan a 100k library with nothing changed: it finishes in seconds and regenerates zero thumbnails.

### Step 8 — Polish and ship

Settings (roots, thumbnail size, cache location), keyboard shortcuts, remembered window and pane state, first-run experience, empty and error states, single-instance, `win-x64` publish profile with ReadyToRun.

> **Gate:** Full walkthrough under a clean Windows user profile: launch, add roots, index, search, view, close, reopen. No crashes, no lost state.

---

## Risks

| Risk | Mitigation |
|---|---|
| SharpDX 4.2 (unmaintained) on .NET 10 | Proven in step 1, before anything depends on it. Fallback: `HelixToolkit.Wpf` Viewport3D at reduced perf. |
| Theme library lock-in | Custom surface brushes are declared in `App.xaml` separately from the Material palette, so restyling doesn't mean touching every view. |
| No GPU / RDP session | DX11 WARP fallback — verify during step 1. |
| Network share latency | Separate pipeline and DOP per root; all I/O off the UI thread. |
| ~~Thumbnail worker contending with the viewer for the GPU~~ | Retired in step 6: thumbnails are rasterized on the CPU, so nothing but the viewport ever touches the device. |

## Post-MVP

STEP support (`Ab4d.OpenCascade` for OCCT tessellation — needs ~200 MB of native OCCT binaries and a license file, which is exactly why it's not in the MVP), tags and collections, duplicate detection via content hash, measurement tools, cross-section, print-time estimate, G-code preview, OBJ/PLY.

## Setup note

The directory is not a git repo yet — worth `git init` before step 0 so each gate is a commit you can fall back to.
