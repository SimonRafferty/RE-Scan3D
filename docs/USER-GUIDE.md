# ScanRE user guide

ScanRE helps you model a part in SOLIDWORKS 2026 (or later) on top of a 3D scan. The scan is temporary
scaffolding: everything ScanRE makes is an ordinary SOLIDWORKS feature (reference planes, axes, sketch points,
splines), and **Finish** removes the scan, so the part opens and rebuilds on any machine without ScanRE.

All commands are on the **Scan RE** tab of the CommandManager (parts only).

## Workflow at a glance

1. **Save the part** (the scan cache is kept next to it).
2. **Import Scan** - load the STL / OBJ / PLY.
3. **Align Scan** - fit planes on the scan and "mate" them to Front / Top / Right; **Accept**.
4. **Fit Feature** - click faces to make reference planes, click holes or bosses to make axes.
5. Open a sketch on a plane, then **Section & Snap** - click points on the scan's outline, or trace splines.
6. Model normally in SOLIDWORKS with the scan visible.
7. **Check References**, then **Finish** - the scan goes, the native part stays.

## 1. Import Scan

| Option | Meaning |
|---|---|
| Scan file | STL (binary or ASCII), OBJ, PLY (with faces). Point clouds are not supported yet: mesh them in the scanner software. |
| Scan units | The units the scan was exported in (usually millimetres). |
| Remove islands | Deletes loose fragments smaller than this % of the main piece. |
| Display triangles | Size of the copy SOLIDWORKS shows. Picking and fitting always use the full scan, so this only affects how it looks and how fast SOLIDWORKS is. 150 000 is a good start. |
| Display smoothing | Smooths the displayed copy only; fitting uses the original data. |

The first import of a large scan takes a few seconds (reading + building the spatial index); after that the
cache folder `<Part>.scanre` next to the part makes re-opening near-instant. The scan appears as a graphics
body called **Scan - <file>** in a folder named **Scan**.

**Display Detail** cycles the display copy through 30k / 60k / 150k / 400k / 1M triangles.
**Show/Hide Scan** toggles it.

## 2. Align Scan

Do this straight after importing: every part is built from the shared origin, so the scan must sit correctly
relative to Front, Top and Right first.

1. Press **Fit a plane…** and click a flat face of the scan (or pick 3+ points). The fit is recorded as F1, F2...
2. Choose the face, a mate type and a base plane, then **Add**:
   - *Coincident* - the face lies on the base plane;
   - *Distance* - the face is parallel to it, at the given distance (mm, along the base plane's normal);
   - *Parallel* / *Perpendicular* / *Angle* - orientation only.
3. The scan moves at once. Repeat for up to three faces.
4. **Accept alignment**.

The first mate is met exactly, the second uses only the rotation left, the third what remains (datum
priority) - real scans are never perfectly square, so the error ends up on the later mates rather than the
solve failing. **Weighted best fit** spreads the error over all mates instead (set the weights per mate).

Mates can be edited (**Update**), deleted, reordered (**Up/Down**), suppressed or **Flip**ped (face the other
way); the whole set is re-solved every time. **Nudge** adds small manual rotations/moves on top.

**Accept** creates **Scan-Top**, **Scan-Front** and **Scan-Right** (one per base plane you mated): planes at the
*actual* position of the mated faces. Sketch on them instead of the base planes where the real part isn't
square. They are defined by numbers only (a hidden "SRE-Def" sketch with a fixed point and normal line), never
by the scan, so they survive Finish. Changing the alignment later moves them with it; ScanRE warns first,
because geometry you already made won't match the moved scan (nothing fails to rebuild).

## 3. Fit Feature

Click on the scan:

- **Plane (region grow)** - the face is grown from the click while it stays within the *distance* and *normal*
  tolerances, then fitted robustly (outliers ignored). The result reports RMS and maximum deviation - a quick
  answer to "is this face really flat?". Raise the distance tolerance for noisy scans.
- **Plane (3+ points)** - click the points, then **Fit plane to points**.
- **Cylinder** - click a hole or boss: you get a reference axis; the diameter is in its name.

**Snap** (on by default) makes the result exactly parallel / perpendicular / coaxial to existing planes and axes
when it is within the snap tolerance, and reports the adjustment - real parts are usually square, scans are noisy.

Names carry the fit quality, e.g. `Plane F7 lid (rms 0.012 max 0.051)` - rename freely. The full fit detail is in
the feature's Description.

## 4. Section & Snap and Trace

1. Edit or create a sketch on a plane through the scan.
2. **Section & Snap**, then **Compute section**. The scan's cross-section appears (a temporary 3D sketch named
   `_ScanRE Section (temporary)`, deleted automatically).
3. Move the mouse over the sketch: the marker snaps to the nearest point of the full-resolution section.
4. Click mode:
   - **Snap** - each click places a *fixed* sketch point there;
   - **Trace → points** - click a start and an end on the same curve: points at the chosen spacing along it;
   - **Trace → spline** - the same, giving a fixed spline with the fewest points that stays within the tolerance.
     Click the same point twice to trace a whole closed loop; **long way round** takes the other side.
   **Undo last** removes the last result.
5. Join the points into lines and arcs with normal SOLIDWORKS sketch tools.

**Slab** (half-thickness > 0) averages several parallel sections to reduce scanner noise.

Created entities are added without SOLIDWORKS inference, so they never get relations to the scan.

## 5. Modelling on top

Use SOLIDWORKS as usual. If you sketch near the scan by hand, SOLIDWORKS may add relations to it; **Check
References** lists anything that depends on the scan, with what to fix.

## 6. Finish and Re-attach

**Finish** lists anything still depending on the scan, then deletes the scan feature, its folder and ScanRE's
temporary sketches. Choose whether to delete the cache folder too. Save the part afterwards.

**Re-attach Scan** brings the scan back in exactly the same place (from the cache; the alignment is also stored
in the part's custom properties `ScanRE.Alignment.*`). If the part was renamed, point it at the old `.scanre` folder.

You can also insert the scan yourself (Insert > Part, or as an imported mesh): Check References and Finish
recognise graphics/mesh bodies from either source.

## Files and settings

- Cache: `<part folder>\<part name>.scanre\` (mesh.bin, bvh.bin, display_*.bin/.stl, state.json).
- Log (timings, errors): `%LOCALAPPDATA%\ScanRE\scanre.log`.
- Settings: `HKEY_CURRENT_USER\Software\ScanRE`.
- Command line (no SOLIDWORKS needed): `ScanRE.Cli.exe info|decimate|fitplane|fitcyl|section <scan> ...`

## Automation

Other tools can drive ScanRE: `ISldWorks.GetAddInObject("ScanRE.AddIn")`, then ImportScan, ReattachScan,
FitPlaneAtRay, FitCylinderAtRay, AddMate, ClearMates, SolveAlignment, AcceptAlignment, SectionPlane,
CheckScanReferences, FinishScan, GetStateJson (all lengths in mm, part coordinates; results as JSON).

## When something goes wrong

Every error says why and what to do. Details are in the log. Common ones:

- *The part has not been saved yet* - save it; the cache needs a folder.
- *Nothing on the scan under the cursor* - click directly on the scan surface.
- *Only N triangles matched* - the face is curved or noisy here: raise the distance tolerance, or pick points.
- *The sketch plane does not cross the scan* - choose a plane through the scan, or check the alignment.
- Picking lands in the wrong place - click once on any model geometry: ScanRE calibrates its view mapping from
  SOLIDWORKS' own pick (see Help for the calibration status).
