# PZA's Wildlife for Meitou: build the code mod and stage its install folder. Author: PZA. Licence: GPLv3 or later.
#
#   .\build.ps1            build Release, stage out\PZAWildlife\ (PZAWildlife.Meitou.dll, meitou.json, PZAWildlife.mod, lang\)
#   .\build.ps1 -Check     also run tools\check on the staged folder: Meitou's own code-mod checks, the settings, the language files, the rules
#                          (WildCore) case by case, and the three patches applied to the real MeitouClient.dll with real BirdFlocks flying over
#                          a test terrain (a corpse draws them down, a person scatters them). On a copy of the .NET 11 runtime inside the Meitou
#                          release; it never starts the game.
#   .\build.ps1 -Fuzz      also run the fuzz harness (tests\fuzz); -Check runs it too
#   -MeitouDir <dir>       the unpacked Meitou release the mod is built against (default: MeitouDir in mod\local.props, git-ignored; 0.20.1)
#   -RunOn <dir>           run the -Check on ANOTHER Meitou release's runtime and assemblies, with the mod still built against -MeitouDir
#                          (how players run it: a 0.20.1 build on 0.22)
#
# It installs nothing (README "Install"). out\ is git-ignored.
param(
    [switch]$Check,
    [switch]$Fuzz,
    [string]$MeitouDir = "",
    [string]$RunOn = "",
    [string]$KenshiDir = "C:\Program Files (x86)\Steam\steamapps\common\Kenshi"   # the check reads birdtype1.mesh there (read only)
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$localProps = Join-Path $here "local.props"
if ($MeitouDir -eq "" -and (Test-Path $localProps)) { $MeitouDir = ([xml](Get-Content $localProps -Raw)).Project.PropertyGroup.MeitouDir }
if (-not $MeitouDir) { throw "Set -MeitouDir <unpacked Meitou release> or create mod\local.props (see the comment in the csproj)" }
$MeitouDir = [System.IO.Path]::GetFullPath($MeitouDir)
if (-not (Test-Path (Join-Path $MeitouDir "MeitouClient.dll"))) { throw "MeitouClient.dll not found in $MeitouDir" }

& dotnet build (Join-Path $here "PZAWildlife.Meitou.csproj") -c Release -nologo "-p:MeitouDir=$MeitouDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$bin = Get-ChildItem (Join-Path $here "bin\Release") -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$stage = Join-Path $here "out\PZAWildlife"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "lang") | Out-Null
Copy-Item (Join-Path $bin.FullName "PZAWildlife.Meitou.dll") $stage -Force
Copy-Item (Join-Path $here "meitou.json") $stage -Force
Copy-Item (Join-Path $here "lang\meitou.*.txt") (Join-Path $stage "lang") -Force
# the froxlotl: the bake and its colours (TheIvoryDisaster's model and textures, PZA's gait), with its credit
New-Item -ItemType Directory -Force (Join-Path $stage "assets\froxlotl\textures") | Out-Null
Copy-Item (Join-Path $here "assets\froxlotl\froxlotl.pzaf") (Join-Path $stage "assets\froxlotl") -Force
Copy-Item (Join-Path $here "assets\froxlotl\CREDITS.md") (Join-Path $stage "assets\froxlotl") -Force
Copy-Item (Join-Path $here "assets\froxlotl\textures\*.png") (Join-Path $stage "assets\froxlotl\textures") -Force
# the birds' rig: Mesh2Motion's bird skeleton and clips (CC0), baked by tools\birds\bake_bird_rig.py (no game data)
New-Item -ItemType Directory -Force (Join-Path $stage "assets\birdrig") | Out-Null
Copy-Item (Join-Path $here "assets\birdrig\*") (Join-Path $stage "assets\birdrig") -Force
# the Mire Stilt: its two bakes and textures (the user's own Meshy models, Mesh2Motion's CC0 bird rig), with the credit
New-Item -ItemType Directory -Force (Join-Path $stage "assets\stilt\textures") | Out-Null
Copy-Item (Join-Path $here "assets\stilt\*.pzaf") (Join-Path $stage "assets\stilt") -Force
Copy-Item (Join-Path $here "assets\stilt\CREDITS.md") (Join-Path $stage "assets\stilt") -Force
Copy-Item (Join-Path $here "assets\stilt\textures\*.png") (Join-Path $stage "assets\stilt\textures") -Force

# the data mod Meitou lists the folder by: an FCS mod file with no records, author PZA (same layout as PZA's other Meitou mods)
function Write-FcsString([System.IO.BinaryWriter]$w, [string]$s) {
    $b = [System.Text.Encoding]::ASCII.GetBytes($s); $w.Write([int]$b.Length); $w.Write($b)
}
$fs = [System.IO.File]::Create((Join-Path $stage "PZAWildlife.mod"))
$w = New-Object System.IO.BinaryWriter($fs)
try {
    $w.Write([int]16); $w.Write([int]1)
    Write-FcsString $w "PZA"
    Write-FcsString $w "PZA's Wildlife for Meitou (code mod pza.wildlife): birds land at carrion, peck, and fly off when people come near. Holds no records. GPLv3."
    Write-FcsString $w ""; Write-FcsString $w ""
    $w.Write([int]0); $w.Write([int]0)
} finally { $w.Close() }

Write-Host "staged $stage :"
Get-ChildItem $stage -Recurse -File | ForEach-Object { Write-Host ("  {0,-28} {1,8} bytes" -f $_.FullName.Substring($stage.Length + 1), $_.Length) }

if ($Check) {
    $runDir = if ($RunOn -ne "") { [System.IO.Path]::GetFullPath($RunOn) } else { $MeitouDir }
    if (-not (Test-Path (Join-Path $runDir "MeitouClient.dll"))) { throw "MeitouClient.dll not found in $runDir" }
    Write-Host "check: the mod built against $MeitouDir, run on $runDir"
    # one runtime folder per release with EVERY dll of it (2026-11-22: 0.22.2-nightly.20261122.2 loads Silk.NET.Core in MeitouClient's
    # module initializer; a fixed name list broke the PZA checks there), copied once and stamped
    $rt = Join-Path $here ("out\checkrt\" + (Split-Path $runDir -Leaf))
    $stamp = Join-Path $rt "meitou-dir.txt"
    if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $runDir) {
        New-Item -ItemType Directory -Force $rt | Out-Null
        foreach ($p in "*.dll", "*.so") { Copy-Item (Join-Path $runDir $p) $rt -Force -ErrorAction SilentlyContinue }
        Set-Content -Path $stamp -Value $runDir -Encoding ascii
    }
    $stagedDll = Join-Path $stage "PZAWildlife.Meitou.dll"
    & dotnet build (Join-Path $here "tools\check\check.csproj") -c Release -nologo "-p:MeitouDir=$runDir" "-p:ModDll=$stagedDll" -o (Join-Path $here "out\checkbin")
    if ($LASTEXITCODE -ne 0) { throw "check tool build failed ($LASTEXITCODE)" }
    Copy-Item (Join-Path $here "out\checkbin\check.dll") $rt -Force
    Copy-Item $stagedDll $rt -Force
    $cfg = '{ "runtimeOptions": { "tfm": "net11.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "11.0.0" } ] } }'
    Set-Content -Path (Join-Path $rt "check.runtimeconfig.json") -Value $cfg -Encoding ascii
    Push-Location $rt
    try { & dotnet exec --runtimeconfig check.runtimeconfig.json check.dll $stage $KenshiDir } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "check FAILED ($LASTEXITCODE)" }
}

# the fuzz harness (tests\fuzz, PZA's Meitou UI fuzz kit) on the build release: -Fuzz runs it, and so does every -Check (a release also needs
# the mutation check, PZAMeitouUi\fuzz\mutate.py). It never starts the game.
if ($Fuzz -or $Check) {
    $fuzzRunner = Join-Path $here "..\..\PZAMeitouUi\fuzz\run_fuzz.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $fuzzRunner -Repo (Join-Path $here "..") -MeitouDir $MeitouDir
    if ($LASTEXITCODE -ne 0) { throw "fuzz FAILED ($LASTEXITCODE): report in tests\out\fuzz\fuzz-report.txt" }
}
