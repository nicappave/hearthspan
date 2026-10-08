<#
.SYNOPSIS
    Hearthspan Stage 6 task 5  -  the export smoke script. build -> export -> run the exported .exe from
    a clean directory OUTSIDE the repo, with dotnet/Godot deliberately stripped from PATH -> parse the
    --smoke JSON from stdout -> compare two independent runs' state hashes.

    Run from anywhere: powershell -File game\tools\smoke.ps1

.DESCRIPTION
    This is the actual proof the self-contained .NET bundling worked: if the exported binary needed
    `dotnet` (or anything else) on PATH to start its own runtime, stripping PATH before launching it
    would make it fail immediately. It is intentionally scripted rather than wired into ci.ps1  -  too
    slow (a full export is minutes, not seconds) for every-commit CI, matching the plan's own note
    ("too slow for every-commit CI, and that is fine")  -  run it by hand before handing a build to a
    person.

    No external oracle: `hearthspan hash` (Hearthspan.Sim.Harness) constructs its World with
    civCount=0/content=null (the constructor's own defaults), which is a materially different world
    than this game's civCount=4/real-content-bundle world GameSession.RunSmokeAndExit builds  -  making
    the harness accept a matching overload would be a Harness-side change this stage's "zero sim/ diffs"
    policy puts out of scope. The acceptance test actually run here is the plan's own documented
    fallback: two independent runs of the exported exe produce the identical hash, which is itself a
    real determinism proof.

.PARAMETER SkipExport
    Reuse an already-exported build instead of re-running export.ps1 (faster iteration on this script).

.PARAMETER ExportedExePath
    Path to the exported Hearthspan.exe. Defaults to <repoRoot>\dist\windows\Hearthspan.exe.
#>

param(
    [switch]$SkipExport,
    [string]$ExportedExePath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repoRoot

function Fail($reason) {
    Write-Host "[FAIL] smoke - $reason" -ForegroundColor Red
    exit 1
}

function Pass($detail) {
    Write-Host "[PASS] smoke - $detail" -ForegroundColor Green
}

if (-not $ExportedExePath) {
    $ExportedExePath = Join-Path $repoRoot "dist\windows\Hearthspan.exe"
}

if (-not $SkipExport) {
    & (Join-Path $PSScriptRoot "export.ps1")
    if ($LASTEXITCODE -ne 0) { Fail "export.ps1 failed  -  see its own [FAIL] line above." }
}

if (-not (Test-Path $ExportedExePath)) {
    Fail "$ExportedExePath does not exist (run without -SkipExport, or pass -ExportedExePath)."
}

$outputDir = Split-Path $ExportedExePath -Parent
$baseName = (Get-Item $ExportedExePath).BaseName
$consoleExe = Join-Path $outputDir "$baseName.console.exe"
if (-not (Test-Path $consoleExe)) {
    $msg = "$consoleExe (the console-wrapper build) is missing  -  needed to capture --smoke's stdout. " +
        "Check export_presets.cfg's debug/export_console_wrapper is set to 2 (Debug and Release)."
    Fail $msg
}

# --- Copy the exported build to a clean directory OUTSIDE the repo ---
$cleanDir = Join-Path $env:TEMP ("hearthspan-smoke-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $cleanDir | Out-Null
Write-Host "Copying exported build to a clean directory outside the repo: $cleanDir"
Copy-Item -Path (Join-Path $outputDir "*") -Destination $cleanDir -Recurse -Force

$cleanConsoleExe = Join-Path $cleanDir "$baseName.console.exe"
if (-not (Test-Path $cleanConsoleExe)) {
    Fail "copy to $cleanDir did not produce $cleanConsoleExe."
}

# --- A PATH with dotnet and Godot deliberately stripped  -  this is what actually proves the
#     self-contained bundling worked, not just that the exe exists. ---
$strippedPath = ($env:PATH -split ';' | Where-Object {
        $_ -notmatch '(?i)dotnet' -and $_ -notmatch '(?i)godot'
    }) -join ';'
Write-Host "Launching with dotnet/Godot stripped from PATH (stripped PATH has $((($strippedPath -split ';').Count)) entries, original had $((($env:PATH -split ';').Count)))."

function Invoke-Smoke {
    param([string]$Exe, [string]$WorkDir, [string]$Path)
    # NOTE (found while doing this task): reading StandardOutput.ReadToEnd() then
    # StandardError.ReadToEnd() SEQUENTIALLY is the classic .NET Process deadlock - if the child
    # writes enough to stderr to fill the OS pipe buffer before we get around to reading it, the
    # child blocks on the full pipe while we're still blocked synchronously draining stdout, and
    # neither side ever moves again. Reproduced exactly on the exported Hearthspan.console.exe: the
    # process sat at ~0% CPU indefinitely with a tiny working set, never touched again by the OS
    # scheduler - not a real hang in the game/engine, a hang in this script's own process-invocation
    # code. Fixed with the standard pattern: register async output/error handlers via
    # OutputDataReceived/ErrorDataReceived + Begin*ReadLine() BEFORE WaitForExit(), so both streams
    # drain concurrently instead of one blocking the other.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = "--headless -- --smoke"
    $psi.WorkingDirectory = $WorkDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["PATH"] = $Path

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $stdoutBuf = New-Object System.Text.StringBuilder
    $stderrBuf = New-Object System.Text.StringBuilder
    $stdoutHandler = { if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
    $stderrHandler = { if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
    $outSub = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $stdoutHandler -MessageData $stdoutBuf
    $errSub = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action $stderrHandler -MessageData $stderrBuf

    [void]$proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()

    $exited = $proc.WaitForExit(60000)
    if (-not $exited) {
        $proc.Kill()
        $proc.WaitForExit()
    }
    Start-Sleep -Milliseconds 200 # let the last async data-received events land

    Unregister-Event -SourceIdentifier $outSub.Name
    Unregister-Event -SourceIdentifier $errSub.Name
    Remove-Job -Name $outSub.Name -Force -ErrorAction SilentlyContinue
    Remove-Job -Name $errSub.Name -Force -ErrorAction SilentlyContinue

    [PSCustomObject]@{
        ExitCode = $(if ($exited) { $proc.ExitCode } else { -1 })
        TimedOut = -not $exited
        StdOut   = $stdoutBuf.ToString()
        StdErr   = $stderrBuf.ToString()
    }
}

Write-Host "--- Run 1 ---"
$run1 = Invoke-Smoke -Exe $cleanConsoleExe -WorkDir $cleanDir -Path $strippedPath
Write-Host "exit=$($run1.ExitCode)"
Write-Host "--- Run 2 ---"
$run2 = Invoke-Smoke -Exe $cleanConsoleExe -WorkDir $cleanDir -Path $strippedPath
Write-Host "exit=$($run2.ExitCode)"

if ($run1.ExitCode -ne 0) { Fail "run 1 exited $($run1.ExitCode). stdout: $($run1.StdOut) stderr: $($run1.StdErr)" }
if ($run2.ExitCode -ne 0) { Fail "run 2 exited $($run2.ExitCode). stdout: $($run2.StdOut) stderr: $($run2.StdErr)" }

$line1 = ($run1.StdOut -split "`r?`n") | Where-Object { $_ -like "HEARTHSPAN_SMOKE *" } | Select-Object -First 1
$line2 = ($run2.StdOut -split "`r?`n") | Where-Object { $_ -like "HEARTHSPAN_SMOKE *" } | Select-Object -First 1

if (-not $line1) { Fail "run 1 stdout had no HEARTHSPAN_SMOKE line. Full stdout: $($run1.StdOut)" }
if (-not $line2) { Fail "run 2 stdout had no HEARTHSPAN_SMOKE line. Full stdout: $($run2.StdOut)" }

$json1 = $line1.Trim().Substring("HEARTHSPAN_SMOKE ".Length) | ConvertFrom-Json
$json2 = $line2.Trim().Substring("HEARTHSPAN_SMOKE ".Length) | ConvertFrom-Json

Write-Host "Run 1 JSON: $($line1.Trim())"
Write-Host "Run 2 JSON: $($line2.Trim())"

if ($json1.status -ne "ok" -or $json2.status -ne "ok") { Fail "status was not 'ok' in one of the two runs." }
if ($json1.stateHash -ne $json2.stateHash) {
    Fail "stateHash mismatch between two independent runs: $($json1.stateHash) vs $($json2.stateHash)  -  determinism is broken."
}

Pass ("exported exe ran standalone from $cleanDir with dotnet/Godot stripped from PATH, twice, " +
      "identical stateHash $($json1.stateHash) (seed $($json1.seed), turns $($json1.turns), " +
      "settlements $($json1.settlementCount)).")
