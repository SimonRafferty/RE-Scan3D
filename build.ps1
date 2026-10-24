# Build ScanRE and run its offline tests. No registration, no running SolidWorks needed.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-NoTests] [-OutDir <dir>] [-SwApi 34] [-Bench]
#
# Compiles with the Roslyn csc against the .NET Framework 4.8 runtime assemblies (no .NET SDK, targeting
# pack or NuGet on this machine; DECISIONS D2) and the SolidWorks interop assemblies of the OLDEST supported
# version (-SwApi 34 = SolidWorks 2026). Interop types are EMBEDDED (/link), so no interop DLL ships and the
# add-in runs in later versions without a rebuild; a call newer than 2026 fails here, not on a user's machine.
param([switch]$NoTests, [string]$OutDir, [string]$SwApi = '34', [switch]$Bench)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = if ($OutDir) { $OutDir } else { Join-Path $here 'bin' }
# SolidWorks keeps a registered add-in's DLLs locked: building into the folder it loads from would fail part-way
if (-not $OutDir -and (Get-Process SLDWORKS -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $bin 'ScanRE.AddIn.dll'))) {
    Write-Warning 'SolidWorks is running; if it has loaded ScanRE from bin\ the build will fail. Use -OutDir to build elsewhere.'
}
New-Item -ItemType Directory -Force $bin | Out-Null

# --- tools -------------------------------------------------------------------
$csc = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'Roslyn csc.exe not found (install Visual Studio Build Tools)' }
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$sw = Get-ChildItem "$env:ProgramFiles\SOLIDWORKS Corp" -Directory -ErrorAction SilentlyContinue |
    Where-Object { (Test-Path "$($_.FullName)\SolidWorks.Interop.sldworks.dll") -and
                   ((Get-Item "$($_.FullName)\SolidWorks.Interop.sldworks.dll").VersionInfo.FileVersion -like "$SwApi.*") } |
    Select-Object -First 1
if (-not $sw) { throw "SolidWorks interop assemblies of version $SwApi.x (SolidWorks $([int]$SwApi + 1992)) not found" }
$swdir = $sw.FullName
Write-Host "SolidWorks API: $swdir (interop $((Get-Item "$swdir\SolidWorks.Interop.sldworks.dll").VersionInfo.FileVersion), embedded)"
$swrefs = @('sldworks', 'swconst', 'swpublished') | ForEach-Object { "/link:$swdir\SolidWorks.Interop.$_.dll" }

$fwrefs = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/reference:$fw\$_" }
$common = @('/nologo', '/noconfig', '/nostdlib+', "/reference:$fw\mscorlib.dll", '/langversion:7.3', '/platform:x64',
            '/optimize+', '/debug:pdbonly', '/warnaserror+', '/nowarn:1701,1702') + $fwrefs

function Build([string]$dir, [string]$name, [string]$target, [string[]]$extra) {
    $src = @(Get-ChildItem (Join-Path $here $dir) -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
    $ext = if ($target -eq 'exe') { 'exe' } else { 'dll' }
    $out = Join-Path $bin "$name.$ext"
    & $csc @common "/target:$target" "/out:$out" @extra @src
    if ($LASTEXITCODE -ne 0) { throw "build of $name failed" }
    Write-Host "built $out"
}

$ui = @("/reference:$fw\System.Windows.Forms.dll", "/reference:$fw\System.Drawing.dll", "/reference:$fw\Microsoft.CSharp.dll")
Build 'src\ScanRE.Core' 'ScanRE.Core' 'library' @()
Build 'src\ScanRE.AddIn' 'ScanRE.AddIn' 'library' ($swrefs + $ui + "/reference:$bin\ScanRE.Core.dll")
Build 'tools\ScanRE.Cli' 'ScanRE.Cli' 'exe' @("/reference:$bin\ScanRE.Core.dll")
# the offline test suite is kept in the development repository only
$hasTests = Test-Path (Join-Path $here 'src\ScanRE.Tests')
if ($hasTests) { Build 'src\ScanRE.Tests' 'ScanRE.Tests' 'exe' @("/reference:$bin\ScanRE.Core.dll") }

if ($NoTests -or -not $hasTests) { return }
$args = @((Join-Path $here 'results'))
if ($Bench) { $args += '--bench' }
& (Join-Path $bin 'ScanRE.Tests.exe') @args
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE test(s) failed" }
