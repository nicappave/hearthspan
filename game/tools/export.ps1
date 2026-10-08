<#
.SYNOPSIS
    Hearthspan Stage 6 task 5  -  scripted `--export-release` round-trip for the Windows Desktop preset.
    Run from anywhere: powershell -File game\tools\export.ps1

.DESCRIPTION
    Builds game/Hearthspan.csproj explicitly first (CLAUDE.md trap list: Hearthspan.sln at the repo
    root does NOT reference game/Hearthspan.csproj, so ci.ps1's gate 2 never verifies this project  - 
    a stale game/ build would silently ship into the export otherwise), then invokes
    `godot --headless --export-release "Windows Desktop" <output>.exe`, then verifies the actual
    output file(s) landed on disk (the second-channel rule  -  a zero exit code alone is not proof).

    Requires:
      - export templates matching 4.5.2.stable.mono installed at
        %APPDATA%\Godot\export_templates\4.5.2.stable.mono\
      - game\export_presets.cfg (committed)
      - game\Hearthspan.sln (committed  -  required by Godot's own C# export pipeline; see that file's
        own presence for why: `godot --export-release` refuses with "no solution file was found" if
        it's missing, discovered while doing this task. This is a second, project-root-adjacent .sln
        purely for Godot's export machinery, distinct from the repo-root Hearthspan.sln that ci.ps1
        builds  -  the two are not interchangeable and both are committed for their own reason.)

.PARAMETER OutputPath
    Where to write the exported .exe. Defaults to <repoRoot>\dist\windows\Hearthspan.exe (gitignored).

.PARAMETER GodotPath
    Path to the Godot binary. Defaults to $env:GODOT_BIN, then `godot`/`godot.bat` on PATH, then the
    pinned CLAUDE.md location C:\Godot\godot.bat.
#>

param(
    [string]$OutputPath,
    [string]$GodotPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repoRoot

function Fail($reason) {
    Write-Host "[FAIL] export - $reason" -ForegroundColor Red
    exit 1
}

function Pass($detail) {
    Write-Host "[PASS] export - $detail" -ForegroundColor Green
}

# --- Resolve the Godot binary ---
if (-not $GodotPath) {
    if ($env:GODOT_BIN) {
        $GodotPath = $env:GODOT_BIN
    } elseif (Get-Command godot -ErrorAction SilentlyContinue) {
        $GodotPath = (Get-Command godot).Source
    } elseif (Test-Path "C:\Godot\godot.bat") {
        $GodotPath = "C:\Godot\godot.bat"
    } else {
        Fail "could not locate the Godot binary (checked `$env:GODOT_BIN`, PATH, C:\Godot\godot.bat). Pass -GodotPath explicitly."
    }
}
Write-Host "Using Godot binary: $GodotPath"

# --- Preconditions ---
if (-not (Test-Path (Join-Path $repoRoot "game\export_presets.cfg"))) {
    Fail "game\export_presets.cfg is missing  -  author it before exporting (Stage 6 task 5)."
}
if (-not (Test-Path (Join-Path $repoRoot "game\Hearthspan.sln"))) {
    Fail "game\Hearthspan.sln is missing  -  Godot's export pipeline requires a solution file next to project.godot (see this script's own doc comment)."
}
$templateDir = Join-Path $env:APPDATA "Godot\export_templates\4.5.2.stable.mono"
if (-not (Test-Path $templateDir)) {
    $msg = "export templates not found at $templateDir. Download the 4.5.2-stable mono .tpz matching " +
        "'godot --version' exactly and extract templates/* into that folder before exporting."
    Fail $msg
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "dist\windows\Hearthspan.exe"
}
$outputDir = Split-Path $OutputPath -Parent
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

# --- Build game/Hearthspan.csproj explicitly (ci.ps1 gate 2 never touches it  -  CLAUDE.md trap list) ---
Write-Host "Building game/Hearthspan.csproj..."
dotnet build (Join-Path $repoRoot "game\Hearthspan.csproj") --nologo | Write-Host
if ($LASTEXITCODE -ne 0) { Fail "dotnet build game/Hearthspan.csproj failed (exit $LASTEXITCODE)." }

# --- Export ---
Write-Host "Running: $GodotPath --headless --path game --export-release `"Windows Desktop`" $OutputPath"
& $GodotPath --headless --path (Join-Path $repoRoot "game") --export-release "Windows Desktop" $OutputPath
$exportExit = $LASTEXITCODE
if ($exportExit -ne 0) { Fail "godot --export-release exited $exportExit." }

# --- Second-channel: verify the actual artifact, not just the exit code ---
if (-not (Test-Path $OutputPath)) {
    Fail "godot reported exit 0 but $OutputPath does not exist on disk."
}
$exeInfo = Get-Item $OutputPath
if ($exeInfo.Length -lt 1MB) {
    Fail "$OutputPath exists but is only $($exeInfo.Length) bytes  -  too small to be a real self-contained .NET export (expect 100MB+)."
}

$consolePath = Join-Path $outputDir ((Get-Item $OutputPath).BaseName + ".console.exe")
$consoleNote = if (Test-Path $consolePath) { "console wrapper present: $consolePath" } else { "WARNING: no console wrapper found at $consolePath (needed to see stdout from --smoke)" }

# --- Bundle content/ beside the exported exe ---
# Found while doing this task: content/ is deliberately OUTSIDE game/ (CLAUDE.md rule 3 - plain JSON
# on disk, never baked into the .pck as an engine-native asset), so Godot's export step never touches
# it at all. GameSession.ResolveContentRoot()'s editor-relative path (res://../content) only resolves
# inside a repo checkout; for a real standalone export, content/ has to travel with the exe as a
# loose sibling folder, which is what this step does and what that fallback path expects.
$contentSrc = Join-Path $repoRoot "content"
$contentDst = Join-Path $outputDir "content"
if (-not (Test-Path $contentSrc)) {
    Fail "content source directory not found at $contentSrc - cannot bundle it into the export."
}
if (Test-Path $contentDst) { Remove-Item -Recurse -Force $contentDst }
Copy-Item -Path $contentSrc -Destination $contentDst -Recurse -Force
$contentFileCount = (Get-ChildItem -Path $contentDst -Recurse -File).Count
if ($contentFileCount -eq 0) {
    Fail "copied content/ to $contentDst but it contains 0 files."
}

Pass "$OutputPath written, $([math]::Round($exeInfo.Length / 1MB, 1)) MB. $consoleNote. content/ bundled beside the exe ($contentFileCount files at $contentDst)."
