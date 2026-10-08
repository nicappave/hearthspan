<#
.SYNOPSIS
    Regenerates the three committed UI text digests under tests/golden/ (FABLE STOP 3).

.DESCRIPTION
    Plan v3 task 3.11 found that none of the dump-ui-*.txt goldens carried a recorded regeneration
    command and had to reconstruct them from git history. This script IS that record. Run it, read
    the resulting `git diff tests/golden/dump-ui-*.txt` line by line, and commit only if every changed
    line traces to a change you made on purpose (goldens are never regenerated to make red go away).

    Launches are headless: a digest may only assert values that are pure functions of sim/UI state
    (CLAUDE.md trap list), so the dummy rasterizer is fine here. A short sleep separates launches
    (docs/plan.md 1.5: rapid-fire godot launches hang).

.PARAMETER GodotPath
    Path to the Godot binary. Defaults to $env:GODOT_BIN, then godot on PATH, then C:\Godot\godot.bat.
#>

param(
    [switch]$NoBuild,
    [string]$GodotPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repoRoot

function Fail($reason) {
    Write-Host (@{ status = "error"; reason = $reason } | ConvertTo-Json -Compress)
    Write-Host "[FAIL] digests - $reason" -ForegroundColor Red
    exit 1
}

if (-not $GodotPath) {
    if ($env:GODOT_BIN) { $GodotPath = $env:GODOT_BIN }
    elseif (Get-Command godot -ErrorAction SilentlyContinue) { $GodotPath = (Get-Command godot).Source }
    elseif (Test-Path "C:\Godot\godot.bat") { $GodotPath = "C:\Godot\godot.bat" }
    else { Fail "could not locate the Godot binary. Pass -GodotPath explicitly." }
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repoRoot "game\Hearthspan.csproj") -nologo -v q | Write-Host
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build game/Hearthspan.csproj failed (exit $LASTEXITCODE)." }
}

$golden = Join-Path $repoRoot "tests\golden"
$digests = @(
    @{ File = "dump-ui-techtree.txt"; Scene = "res://scenes/TechTreeSpike.tscn";
       Flags = @("--node-count", "2000", "--frames", "40") },
    @{ File = "dump-ui-wave0-session.txt"; Scene = "res://scenes/Main.tscn";
       Flags = @("--seed", "1234", "--no-prompts", "--auto-turns", "120") },
    @{ File = "dump-ui-buildview.txt"; Scene = "res://scenes/Main.tscn";
       Flags = @("--seed", "1234", "--no-prompts", "--research", "gathering,toolmaking,agriculture",
                 "--build", "camp-fire,granary,palisade", "--auto-turns", "120", "--show-panel", "buildview") }
)

$i = 0
foreach ($d in $digests) {
    $i++
    $outPath = Join-Path $golden $d.File
    $argList = @("--headless", "--path", "game", $d.Scene, "--") + $d.Flags + @("--dump-ui-out", $outPath)
    Write-Host "[$i/$($digests.Count)] $($d.File): $GodotPath $($argList -join ' ')"
    $before = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }

    $quotedArgs = $argList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $GodotPath
    $psi.Arguments = $quotedArgs -join ' '
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(180000)) {
        $proc.Kill()
        Write-Host "  [WARN] $($d.File) - launch did not exit within 180s (known teardown hang); killed, checking artifact." -ForegroundColor Yellow
    }

    # Second-channel rule: the file must exist and be newer than before this launch.
    if (-not (Test-Path $outPath)) { Fail "$($d.File) was not written." }
    if ((Get-Item $outPath).LastWriteTimeUtc -le $before) { Fail "$($d.File) was not rewritten by this launch." }

    if ($i -lt $digests.Count) { Start-Sleep -Seconds 4 }
}

Write-Host (@{ status = "ok"; files = ($digests | ForEach-Object { $_.File }) } | ConvertTo-Json -Compress)
Write-Host "Now read: git diff tests/golden/dump-ui-*.txt" -ForegroundColor Green
exit 0
