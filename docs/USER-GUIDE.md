# RE-Scan3D user guide

RE-Scan3D helps you model a part in SOLIDWORKS 2018 (or later) on top of a 3D scan. The scan is temporary
scaffolding: everything RE-Scan3D makes is an ordinary SOLIDWORKS feature (reference planes, axes, sketch points,
splines), and **Remove Mesh Data** removes the scan, so the part opens and rebuilds on any machine without RE-Scan3D.

All commands are on the **RE-Scan3D** tab of the CommandManager (parts only).

## Workflow at a glance

1. **Save the part** (the scan cache is kept next to it).
2. **Import Scan** - load the STL / OBJ / PLY.
3. **Align Scan** - define a plane on the scan (a face or points), mate it to Front, Top or Right, **Save**; repeat per plane.
4. **Add Plane** - reference planes from faces or points; **Fit Axis** - axes from holes and bosses.
5. Open a sketch on a plane, then **Contours** - points, splines or lines and arcs on the scan's outline.
6. Model normally in SOLIDWORKS with the scan visible.
7. **Remove Mesh Data** - the scan goes, the native part stays (anything still depending on the scan is listed first).

## 1. Import Scan

| Option | Meaning |
|---|---|
| Scan file | STL (binary or ASCII), OBJ, PLY (with faces). Point clouds are not supported yet: mesh them in the scanner software. |
| Scan units | The units the scan was exported in (usually millimetres). |
| Remove islands | Deletes loose fragments smaller than this % of the main piece. |
| Display triangles | Size of the copy SOLIDWORKS shows. Picking and fitting always use the full scan, so this only affects how it looks and how fast SOLIDWORKS is. 60 000 is a good start (about 5 s to show; 150 000 about 11 s). |
| Display smoothing | Smooths the displayed copy only; fitting uses the original data. |

The first import of a large scan takes a few seconds (reading + building the spatial index); after that the
cache folder `<Part>.res3d` next to the part makes re-opening near-instant. The scan appears as surface mesh
bodies, in a feature called **Scan - <file>** in a folder named **Scan**. (The display copy is stored in the part
itself - nothing outside the part is needed to open it; Remove Mesh Data deletes it.)

**Display Detail** opens a small window to choose how detailed the scan looks: *Low* (about 30,000 triangles,
quickest to rotate), *Medium* (about 60,000) or *High* (about 150,000, a few seconds to show). Picking, fitting and
contours always use the full scan.

## 2. Align Scan

Do this straight after importing: every part is built from the shared origin, so the scan must sit correctly
relative to Front, Top and Right first. Each standard plane gets one plane from the scan.

**Align Scan** opens one window. For each standard plane:

1. **Define a plane on the scan:**
   - *Click a flat face* - the face is grown from the click and a plane fitted to it. The distance and normal
     tolerances control how far the face may spread (raise the distance for a noisy scan; lower the normal
     tolerance if it spills round a fillet).
   - *Pick 3 or more points* - each click puts a red ball on the scan and a line in the list. From the third
     point the plane is fitted and shown, and it updates with every point. Select a point in the list to turn
     its ball green; right-click it (or press Delete) to remove it.

   The plane shows as orange stripes over the fitted area: where the scan shows through them evenly, the fit
   is good. The window gives the rms and largest distance of the scan from the plane.
2. **Mate it to** Front, Top or Right: *Coincident* (lies on it), *Offset* (parallel, at a distance along the
   standard plane's normal: Front +Z, Top +Y, Right +X), *Angle* (tilted from it by an angle) or *Parallel*
   (direction only). Blue stripes show where the scan plane will end up, and move as you change the offset or
   angle. Tick **Flip** to put the scan on the other side of the plane.
3. **Save** - the scan moves into place and a **Scan-Top**, **Scan-Front** or **Scan-Right** plane is made at the
   *actual* position of the scan's face. **Cancel** closes without saving.

Open Align Scan again for the next standard plane; the saved mates are listed at the top. Select one to see its
plane and to redefine it; right-click to delete it. The first mate is met exactly, the next uses only the
rotation left, and so on - real scans are never perfectly square, so the error ends up on the later mates
rather than the solve failing.

Sketch on the Scan-* planes instead of the base planes where the real part isn't square. They are defined by
numbers only (a hidden "RES3D-Def" sketch with a fixed point and normal line), never by the scan, so they
survive Remove Mesh Data. Changing a mate later moves them with the scan.

## 3. Add Plane

**Add Plane** makes reference planes from the scan, in the same window layout as Align Scan:

1. **Define a plane on the scan** - click a flat face, or pick 3 or more points (live, as in Align Scan).
2. **Position the new plane** (optional) - *Offset* moves it along its normal, *Angle* tilts it about the long
   direction of the fitted area; **Flip** reverses both. Blue stripes show the plane that will be made.
   *Snap square* makes it exactly parallel or perpendicular to an existing plane when it is within the tolerance.
   Give it a *Name*, or leave it empty for "Plane F<n> (rms ... max ...)".
3. **Add** - the plane is created as an ordinary SOLIDWORKS reference plane at exact coordinates (nothing links
   it to the scan). The window stays open for the next plane; **Close** when done.

Planes added are listed at the top: select one to see it, right-click to delete it (it is kept if other features
are built on it).

## 3b. Fit Axis

Click inside a hole or on a boss: a cylinder is grown from the click (within the *distance* and *normal*
tolerances), fitted robustly, and a reference axis is made along its centre line. The window shows the diameter
and how well it fits; the diameter is also in the axis' name, e.g. `Axis F7 D12.700 (rms 0.012)` - rename freely.
The full fit detail is in the feature's Description.

**Snap** (on by default) makes the axis exactly parallel or perpendicular to existing planes, and coaxial with an
existing axis, when it is within the snap tolerance, and reports the adjustment.

## 4. Contours

1. Edit or create a sketch on a plane through the scan (Contours says so if no sketch is open).
2. **Contours**, then **Compute section**. The scan's outline is drawn in your sketch as splines within
   the *Tolerance* (fixed, and construction geometry unless you untick *Outline as construction geometry*). It is
   yours: it stays when you close the window, and Remove Mesh Data leaves it. Computing again in the same sketch replaces it;
   **Remove outline** deletes it.
3. **Click on the outline** - each click snaps to the nearest point of the full-resolution outline:
   - **Point** - each click places a sketch point;
   - **Trace → points** - click a start and an end: points at the chosen spacing along the outline between them;
   - **Trace → spline** - the same, as one spline with the fewest points that stays within the tolerance;
   - **Trace → lines and arcs** - the same, best-fitted with straight lines and circular arcs within the
     tolerance, joined end to end (a round hole becomes a circle).
   Click the same point twice to trace a whole closed outline; **long way round** takes the other side.
   **Fix the result** (on by default) gives what is made a Fixed relation; untick it to dimension and relate it
   yourself. **Undo last** removes the last result.

**Slab** (half-thickness > 0) averages several parallel cuts to reduce scanner noise.

Everything is added without SOLIDWORKS inference, so it never gets relations to the scan.

## 5. Modelling on top

Use SOLIDWORKS as usual. If you sketch near the scan by hand, SOLIDWORKS may add relations to it; **Check
References** lists anything that depends on the scan, with what to fix.

## 6. Remove Mesh Data and Re-attach

**Remove Mesh Data** lists anything still depending on the scan, then deletes the scan feature, its folder and RE-Scan3D's
temporary sketches. Choose whether to delete the cache folder too. Save the part afterwards.

**Re-attach Scan** brings the scan back in exactly the same place (from the cache; the alignment is also stored
in the part's custom properties `RES3D.Alignment.*`). If the part was renamed, point it at the old `.res3d` folder.

You can also insert the scan yourself (Insert > Part, or as an imported mesh): Remove Mesh Data
recognise graphics/mesh bodies from either source.

## Files and settings

- Cache: `<part folder>\<part name>.res3d\` (mesh.bin, bvh.bin, display_*.bin/.stl, state.json).
- Log (timings, errors): `%LOCALAPPDATA%\RES3D\res3d.log`.
- Settings: `HKEY_CURRENT_USER\Software\RES3D`.
- Command line (no SOLIDWORKS needed): `RES3D.Cli.exe info|decimate|fitplane|fitcyl|section <scan> ...`

## Automation

Other tools can drive RE-Scan3D: `ISldWorks.GetAddInObject("RES3D.AddIn")`, then ImportScan, ReattachScan,
FitPlaneAtRay, FitCylinderAtRay, AddMate, ClearMates, SolveAlignment, AcceptAlignment, SectionPlane,
CheckScanReferences, FinishScan, GetStateJson (all lengths in mm, part coordinates; results as JSON).

## When something goes wrong

Every error says why and what to do. Details are in the log. Common ones:

- *The part has not been saved yet* - save it; the cache needs a folder.
- *Nothing on the scan under the cursor* - click directly on the scan surface.
- *Only N triangles matched* - the face is curved or noisy here: raise the distance tolerance, or pick points.
- *The sketch plane does not cross the scan* - choose a plane through the scan, or check the alignment.
- Picking lands in the wrong place - click once on any model geometry: RE-Scan3D calibrates its view mapping from
  SOLIDWORKS' own pick (see Help for the calibration status).
