# Build the ScanRE installer: compile into installer\build\app (never bin\, which SolidWorks may hold
# locked), add the user guide, then run Inno Setup. Output: installer\Output\ScanRE-Setup-<version>.exe
#
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$app = Join-Path $here 'build\app'
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
New-Item -ItemType Directory -Force $app | Out-Null

$buildArgs = @('-OutDir', $app)
if ($SkipTests) { $buildArgs += '-NoTests' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
# the tests/benchmarks stay out of the installer
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $app 'ScanRE.Tests.*')
Copy-Item (Join-Path $root 'docs\USER-GUIDE.md') $app
$html = Join-Path $root 'docs\USER-GUIDE.html'
if (Test-Path $html) { Copy-Item $html $app }

# version from the add-in's AssemblyVersion
$m = Select-String -Path (Join-Path $root 'src\ScanRE.AddIn\AddIn.cs') -Pattern 'AssemblyVersion\("(\d+\.\d+\.\d+)' | Select-Object -First 1
$version = $m.Matches[0].Groups[1].Value

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found: install it from jrsoftware.org' }
Push-Location $here
try { & $iscc "/DAppVersion=$version" 'ScanRE.iss'; if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' } }
finally { Pop-Location }
Write-Host "installer: $(Join-Path $here "Output\ScanRE-Setup-$version.exe")"
