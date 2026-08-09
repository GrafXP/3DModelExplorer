# Handoff — Step 6.5 (sorting, bounding box, build plate)

Written 2026-08-09, mid-session, at the point where the build plate landed.
Everything described here is committed and builds clean; `dotnet test` is
132/132 green.

---

## Where things stand

Gate 6 is approved and committed (`5ccb195`). Since then, three interim features
were added on top, out of plan order, at the user's request. `PLAN.md` has a new
**Step 6.5** section describing the first two; **the build plate is not yet
written into `PLAN.md`** — see "Not done" below.

| Feature | State |
|---|---|
| Result sorting | Done, unit tested, verified in the app |
| Viewer bounding box + dimensions | Done, verified in the app |
| Build plate | Code complete and builds; **only partly verified** |

---

## 1. Result sorting — done

Nine options in one dropdown, direction folded into each label rather than split
into a separate ascending/descending toggle:

> Best match · Name (A–Z) · Name (Z–A) · Newest first · Oldest first ·
> Largest first · Smallest first · Format · Folder

`Format` groups STL against 3MF; `Folder` keeps one project's parts adjacent.

**The design point worth preserving.** Ordering never lands on the keystroke
path. [ModelSearchIndex](src/ModelExplorer.Indexing/ModelSearchIndex.cs) builds
**one permutation per sort field**, lazily, once per snapshot, cached in
`_orders` and published with an interlocked write. A query walks that permutation
forwards (or backwards for descending) and keeps the matches — O(n), no sort per
query. `Relevance` and `Name` return a *null* permutation, meaning "the entries
are already in this order", which preserves the existing zero-allocation fast
path for an empty search box.

If you add a sort field, add it to `ModelSortField`, give it a case in
`CompareKeys`, and add a `SortOption` in `LibraryViewModel`. Sort keys are
compared O(n log n) times, so **they must not allocate** — `ExtensionOf` and
`FolderOf` read spans straight off the lowercased haystack for this reason.

Verified at 2.0 ms over the user's real 4,198-model library under "Largest first".

## 2. Bounding box — done

Toggle in the viewport overlay, or `B` while the viewport has focus. Off by
default. Cyan wireframe fitted to the model, with X/Y/Z extents labelled on the
three edges meeting at the corner nearest the default camera. Dimensions also go
to the status bar whether the box is on or not.

One **unit cube** built once and placed by a scale + translate, exactly like the
existing pivot marker — no new vertex buffer per model.

Keybindings are on `Viewport3DX.InputBindings`, not the window: an unmodified
letter binding at window level fires while the search box has focus, so `b`
would toggle the box while you type.

## 3. Build plate — code complete, partly verified

A printer's bed drawn under the model. Toggle in the overlay or `P`. Default is
**Bambu Lab X1C / P1S, 256 × 256 × 250**.

Three layers, all sharing one transform, none hit-testable:
bed surface (`PlateSurface`) → 10 mm grid (`PlateGrid`) → edge + 50 mm lines
(`PlateOutline`). Geometry is built in **plate-local coordinates centred on the
origin at z = 0**, so switching models is a single `TranslateTransform3D` and
switching printers rebuilds the geometry once.

Placed under the model's own footprint, not at the world origin — model files put
geometry wherever the exporter left it, so a plate at z = 0 would as often as not
bury the model or leave it floating.

**Bed sizes came from Bambu Studio's own profiles**, not spec sheets — it is
installed on this PC at `C:\Program Files\Bambu Studio\resources\profiles`.
`printable_area` and `printable_height` were resolved through each profile's
`inherits` chain. This mattered: the X1 Carbon's printable *height* is **250**,
not the 256 on the box. See the remark on [BuildPlates](src/ModelExplorer.App/BuildPlates.cs)
for how to re-derive the list.

Bambu references its own plate mesh (`bbl-3dp-X1.stl`) and logo SVG per printer
model. Those are vendor assets, so the plate here is drawn procedurally instead —
shippable, and no dependency on a Bambu Studio install.

**Fit check.** `PlateFitText` reads "Fits Bambu Lab X1C / P1S" or "Too large for
… — over on Z by 40.0 mm", and the plate edge is drawn by one of two
`LineGeometryModel3D` elements, grey or red. Two elements rather than one with a
rebound colour so both colours stay literals the XAML compiler checks — the DP's
runtime type was never confirmed and a wrong-typed binding fails silently.

**Clip planes.** `UpdateClipPlanes` now derives `_sceneRadius` from the plate
when it is shown. Without this, turning a 256 mm bed on under a 20 mm part shows
a bed with its back half sliced off by the far plane. The near plane still tracks
the model alone — that is what the camera flies into.

### What was verified

Launched against a generated 120 × 80 × 45 mm test box (the generator is in the
session scratchpad, not the repo) and driven through real mouse input:
plate off → clicked the toggle → bed, grid and major lines render, the printer
picker appears in the overlay, and the status bar reads
`Fits Bambu Lab X1C / P1S · 120.0 × 80.0 × 45.0 mm`.

### What was NOT verified — do this first

1. **The red overrun state.** Never exercised. Load something taller than 250 mm
   (or pick "Bambu Lab A1 mini" with the 120 mm test box, which overruns nothing
   — better to pick a genuinely oversized model). Confirm the edge turns red and
   the status text names the right axes and amounts.
2. **The far-plane fix.** Load a *small* model (~10–20 mm) and turn the plate on.
   The far edge of the bed must not be clipped away. This is the specific bug
   `_sceneRadius` exists to prevent and it has not been seen working.
3. **Switching printers with a model loaded.** `OnSelectedBuildPlateChanged`
   rebuilds geometry, recomputes fit and updates clip planes; none of that path
   has been run.
4. **`P` keybinding.** Only the overlay button was clicked.

### Known rough edge

`FrameModel` frames the *model*, not the plate, so turning a 256 mm bed on under
a small part leaves most of the bed outside the viewport until you press `F` or
zoom out. That is arguably correct — framing should not change because a toggle
flipped — but it is worth a decision. Framing the plate when it is visible is a
few lines in `FrameModel` if the user prefers it.

---

## Not done

- **`PLAN.md` has no build-plate entry.** Step 6.5 covers sorting and the
  bounding box only. Extend that section, including a gate along the lines of:
  *load a model you know overruns your printer and confirm the plate edge turns
  red and names the axis.*
- **No unit tests for the plate.** `BuildPlate.Fits` and `BuildPlate.Overruns`
  are pure and trivially testable — worth a small file, in the style of
  `ModelSearchIndexTests`.
- **Nothing is persisted.** Sort choice, both toggles and the selected printer
  all reset each launch. That is Step 8 (settings) by design.

## Suggested next step, unprompted

The genuinely 3D-specific sort keys — triangle count, printed volume, "does it
fit my bed" as a *filter* rather than a per-model readout — need geometry the
index never sees, because a scan deliberately does not parse files.

There is a clean way in: the thumbnail pass already parses every model and
already holds `MeshData.Bounds` and the triangle count. Persisting those two as
tiles are generated would give dimension and complexity sorting for free, and
would make the plate fit check answerable across the whole library rather than
one model at a time. The wrinkle is what to do with rows that have no thumbnail
yet. Best folded into **Step 7**, which is already touching the index schema and
rescan logic.
