# RE-Scan3D (ScanRE) - scan reverse engineering for SOLIDWORKS

A SOLIDWORKS 2026+ add-in for modelling parts on top of 3D scans (STL, OBJ, PLY). It works to one rule: **everything it creates is a native SOLIDWORKS feature**, and the scan is temporary
scaffolding that Finish removes, so parts open and rebuild without the add-in.

- Import a scan as a lightweight graphics body (decimated for display; the full-resolution scan is kept by the add-in).
- Align it by "mating" fitted scan planes to Front / Top / Right; get Scan-Top/Front/Right planes at the real faces.
- Click a face to fit a reference plane (region growing, robust fit, RMS/max reported) or a hole/boss for an axis.
- In a sketch: section the scan by the sketch plane, snap points to the outline, trace points or splines.
- Check what still depends on the scan; Finish; Re-attach later in the same position.

## Install

Run `ScanRE-Setup-<version>.exe` (administrator, with SOLIDWORKS closed). Start SOLIDWORKS, open a part: the
**Scan RE** tab appears in the CommandManager. Uninstall from Windows Settings > Apps.

Requirements: Windows 10/11 x64, SOLIDWORKS 2026 or later, .NET Framework 4.8 (included with Windows).

## Build from source

```
powershell -ExecutionPolicy Bypass -File build.ps1            # compile + offline tests
powershell -ExecutionPolicy Bypass -File build.ps1 -Bench     # + load-time benchmarks
powershell -ExecutionPolicy Bypass -File installer\build.ps1  # installer (needs Inno Setup 6)
```

Needs the Roslyn compiler from Visual Studio (Build Tools) 2019 or 2022 and a SOLIDWORKS 2026 installation (the
interop types are embedded at compile time; no SOLIDWORKS DLLs are redistributed).

Layout: `src/ScanRE.Core` (geometry, no SOLIDWORKS dependency), `src/ScanRE.AddIn` (add-in; all SOLIDWORKS
calls in `Sw/`), `tools/ScanRE.Cli` (command line), `installer/`, `docs/USER-GUIDE.md`.
