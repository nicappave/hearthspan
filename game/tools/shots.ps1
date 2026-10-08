<#
.SYNOPSIS
    Hearthspan Plan v3 task 0.2 - the standard shot set (docs/plan.md section 2.1), one command.

.DESCRIPTION
    Launches godot windowed-offscreen (--position -4000,-4000) once per row of the shots table so
    every launch uses the real renderer, never --headless (CLAUDE.md trap list: --headless returns a
    null Image and a fake small viewport size). Sleeps between launches per docs/plan.md section 1.5
    (a tight loop of rapid godot launches hangs the machine after about a dozen).

.PARAMETER OutDir
    Where the thirteen PNGs land. Defaults to tests/golden/shots/current (repo-relative).

.PARAMETER Seed
    Seed passed to every launch. Defaults to 42 (docs/plan.md 2.1).

.PARAMETER NoBuild
    Skip the dotnet build game/Hearthspan.csproj step (use when the build is already current).

.PARAMETER GodotPath
    Path to the Godot binary. Defaults to $env:GODOT_BIN, then godot/godot.bat on PATH, then the
    pinned CLAUDE.md location C:\Godot\godot.bat.
#>

param(
    [string]$OutDir = "tests/golden/shots/current",
    [int]$Seed = 42,
    [switch]$NoBuild,
    [string]$GodotPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repoRoot

function Fail($reason) {
    $payload = @{ status = "error"; missing = $script:missing } | ConvertTo-Json -Compress
    Write-Host $payload
    Write-Host "[FAIL] shots - $reason" -ForegroundColor Red
    exit 1
}

# --- Resolve the Godot binary (same order as export.ps1) ---
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

# --- Build game/Hearthspan.csproj (ci.ps1 gate 2 never touches it - CLAUDE.md trap list) ---
if (-not $NoBuild) {
    Write-Host "Building game/Hearthspan.csproj..."
    dotnet build (Join-Path $repoRoot "game\Hearthspan.csproj") -nologo -v q | Write-Host
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build game/Hearthspan.csproj failed (exit $LASTEXITCODE)." }
}

$absOutDir = Join-Path $repoRoot $OutDir
New-Item -ItemType Directory -Force -Path $absOutDir | Out-Null

# --- The standard shot set (docs/plan.md section 2.1) ---
$shots = @(
    @{ File = "01-open.png";      Flags = @("--frames", "40") },
    @{ File = "02-close.png";     Flags = @("--no-prompts", "--camera", "close-on-capital") },
    @{ File = "03-t60-mid.png";   Flags = @("--no-prompts", "--research", "gathering,toolmaking,agriculture", "--auto-turns", "60", "--select-settlement", "1", "--camera", "mid-region") },
    @{ File = "04-t120-far.png";  Flags = @("--no-prompts", "--auto-turns", "120", "--camera", "max-zoom-out") },
    @{ File = "05-tech.png";      Flags = @("--no-prompts", "--research", "irrigation", "--show-panel", "techtree") },
    @{ File = "06-build.png";     Flags = @("--no-prompts", "--auto-turns", "30", "--show-panel", "buildview") },
    @{ File = "07-t120-close.png"; Flags = @("--no-prompts", "--research", "gathering,toolmaking,agriculture", "--build", "camp-fire,granary,palisade", "--auto-turns", "120", "--camera", "close-on-capital") },
    @{ File = "08-overlay.png"; Flags = @("--no-prompts", "--research", "gathering", "--auto-turns", "60", "--overlay", "growth", "--camera", "mid-region") },
    @{ File = "09-t200-largest.png"; Flags = @("--no-prompts", "--auto-turns", "200", "--camera", "largest-settlement") },
    # Plan v5 M3: the map interactions - a unit's route, founding sites, a moment card.
    @{ File = "10-unit-route.png"; Flags = @("--no-prompts", "--research", "fire", "--do", "select-unit:2;hover-ruin", "--camera", "close-on-capital") },
    @{ File = "11-found-mode.png"; Flags = @("--no-prompts", "--research", "fire", "--found-mode", "--camera", "close-on-capital") },
    @{ File = "12-moment.png"; Flags = @("--research", "fire", "--do", "dest-ruin:2", "--camera", "close-on-capital") },
    # Plan v5: the main menu over its live scenery (generated on a worker thread, so give it frames).
    @{ File = "13-menu.png"; Scene = "res://scenes/MainMenu.tscn"; Flags = @("--frames", "240") }
)

$script:missing = @()
$results = @()
$i = 0
foreach ($shot in $shots) {
    $i++
    $outPath = Join-Path $absOutDir $shot.File
    if (Test-Path $outPath) { Remove-Item -Force $outPath }

    $scene = if ($shot.Scene) { $shot.Scene } else { "res://scenes/Main.tscn" }
    $seedArgs = if ($shot.Scene) { @() } else { @("--seed", "$Seed") }
    $argList = @("--position", "-4000,-4000", "--path", "game", $scene, "--") `
        + $seedArgs + $shot.Flags + @("--shot-out", $outPath)

    Write-Host "[$i/$($shots.Count)] $($shot.File): $GodotPath $($argList -join ' ')"

    $quotedArgs = $argList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $GodotPath
    $psi.Arguments = $quotedArgs -join ' '
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
    $exited = $proc.WaitForExit(120000)
    if (-not $exited) {
        $proc.Kill()
        # Known hang (docs/plan.md 1.5): the process can sit alive well after it already wrote the
        # PNG and called AppRunner's own quit path - the hang is in process teardown, not capture.
        # Killing it and moving on is correct; whether this shot actually succeeded is decided below
        # by checking the artifact on disk (the second-channel rule), not by this exit code.
        Write-Host "  [WARN] $($shot.File) - launch did not exit within 120s (known hang, docs/plan.md 1.5); killed, checking artifact." -ForegroundColor Yellow
    }

    if ($i -lt $shots.Count) { Start-Sleep -Seconds 4 }
}

foreach ($shot in $shots) {
    $outPath = Join-Path $absOutDir $shot.File
    if (-not (Test-Path $outPath)) {
        $script:missing += $shot.File
        continue
    }
    $size = (Get-Item $outPath).Length
    if ($size -le 20KB) {
        $script:missing += $shot.File
        continue
    }
    $results += @{ file = $shot.File; bytes = $size }
}

if ($script:missing.Count -gt 0) {
    Fail "missing or undersized: $($script:missing -join ', ')"
}

$payload = @{ status = "ok"; outDir = $absOutDir; shots = $results } | ConvertTo-Json -Compress
Write-Host $payload
exit 0
