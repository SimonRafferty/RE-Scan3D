# RE-Scan3D - scan reverse engineering for SOLIDWORKS

**Download:** [latest release (installer)](https://github.com/SimonRafferty/RE-Scan3D/releases/latest) ·
[v0.2.1](https://github.com/SimonRafferty/RE-Scan3D/releases/tag/v0.2.1)

A SOLIDWORKS 2018+ add-in for modelling parts on top of 3D scans (STL, OBJ, PLY). It works to one rule: **everything it creates is a native SOLIDWORKS feature**, and the scan is temporary
scaffolding that Remove Mesh Data removes, so parts open and rebuild without the add-in.

All commands are on the **RE-Scan3D** tab of the CommandManager:

- **Import Scan** - a lightweight display mesh stored in the part; the full-resolution scan is kept in a cache
  next to the part for picking, fitting and contours. Re-opening the part attaches the scan automatically.
- **Display Detail** - low / medium / high display detail.
- **Align Scan** - define a plane on the scan (click a face, or pick points - fitted live), mate it to Front, Top
  or Right (coincident, offset, angle or parallel, previewed before saving); Scan-Top/Front/Right planes mark the
  real faces.
- **Add Plane** - reference planes from faces or points, optionally offset or tilted, snapped square.
- **Fit Axis** - click a hole or boss for a reference axis along its centre line.
- **Contours** - in a sketch: cut the scan with the sketch plane, then place points, a spline, or best-fit lines
  and arcs on its outline.
- **Remove Mesh Data** - removes the scan (anything still depending on it is listed first); **Re-attach Scan**
  brings it back in the same position.

See [docs/USER-GUIDE.md](docs/USER-GUIDE.md).

## Install

Download `RE-Scan3D-Setup-<version>.exe` from the [releases](https://github.com/SimonRafferty/RE-Scan3D/releases/latest)
and run it (administrator, with SOLIDWORKS closed). One install adds RE-Scan3D to **every SOLIDWORKS 2018 or later
on the PC** - the installer lists the versions it found. Start SOLIDWORKS, open a part: the **RE-Scan3D** tab
appears in the CommandManager. Uninstall from Windows Settings > Apps.

## Compatibility

| | |
|---|---|
| SOLIDWORKS | **2018 and later** (built against the 2018 API, so later versions run the same add-in) |
| Tested | 2018 SP5 and 2025 SP5 (full automated test in SOLIDWORKS). 2026 and later: expected to work, not yet tested |
| Windows | 10 / 11, 64-bit |
| .NET | Framework 4.8 (included with Windows 10 / 11) |
| Scans | STL (binary / ASCII), OBJ, PLY |

Several SOLIDWORKS versions can be installed side by side; RE-Scan3D loads in each of them from 2018 on and stays
inactive in anything older.

## Build from source

```
powershell -ExecutionPolicy Bypass -File build.ps1            # compile
powershell -ExecutionPolicy Bypass -File installer\build.ps1  # installer (needs Inno Setup 6)
```

Needs the Roslyn compiler from Visual Studio (Build Tools) 2019 or 2022 and a SOLIDWORKS installation, ideally 2018 (it compiles
against the oldest SOLIDWORKS installed from 2018 on, so the result runs in that version and later; the
interop types are embedded at compile time; no SOLIDWORKS DLLs are redistributed).

Layout: `src/RES3D.Core` (geometry, no SOLIDWORKS dependency), `src/RES3D.AddIn` (add-in; all SOLIDWORKS
calls in `Sw/`), `tools/RES3D.Cli` (command line), `installer/`, `docs/USER-GUIDE.md`.
