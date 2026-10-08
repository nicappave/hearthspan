<#
.SYNOPSIS
    Hearthspan local CI. Run from repo root: .\ci.ps1

    Mirrors docs/development-plan.md Phase 0 step 5:
      1. grep gate - sim/ must never reference Godot.
      2. dotnet build - the whole solution.
      3. dotnet test - Tier-1 suite.
      4. content lint - Hearthspan.ContentLint over content/.
      5. golden-hash comparison - once goldens exist (Phase 1); plus the with-civs settlement-count band (FABLE STOP 4).
      6. balance envelope - outcome-distribution gate, once an envelope exists (Phase 2).
      7. perf budget - p95 turn time against a budget at scale.
      8. steer target - the player's verbs must change the map (FABLE STOP 4).

    Exits non-zero on the first failing gate. Each gate prints PASS/FAIL explicitly -
    per the project's second-channel rule, nothing here should require trusting a tool's
    own silent success.
#>

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
Set-Location $repoRoot

function Fail($gate, $reason) {
    Write-Host "[FAIL] $gate - $reason" -ForegroundColor Red
    exit 1
}

function Pass($gate, $detail) {
    Write-Host "[PASS] $gate - $detail" -ForegroundColor Green
}

# --- Gate 1: sim/ must never reference Godot ---
$gate = "grep-gate (sim/ vs Godot)"
$simCsFiles = Get-ChildItem -Path (Join-Path $repoRoot "sim") -Filter *.cs -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$hits = $simCsFiles | Select-String -Pattern "using Godot" -SimpleMatch
if ($hits) {
    $hits | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    Fail $gate "sim/ references Godot - the simulation must never acquire an engine dependency."
}
Pass $gate "$($simCsFiles.Count) file(s) scanned under sim/, no Godot reference found."

# --- Gate 2: dotnet build ---
$gate = "dotnet build"
dotnet build Hearthspan.sln --nologo | Write-Host
if ($LASTEXITCODE -ne 0) { Fail $gate "build failed (exit $LASTEXITCODE)." }
Pass $gate "Hearthspan.sln built clean."

# --- Gate 3: dotnet test ---
$gate = "dotnet test"
dotnet test Hearthspan.sln --nologo --no-build | Write-Host
if ($LASTEXITCODE -ne 0) { Fail $gate "one or more tests failed (exit $LASTEXITCODE)." }
Pass $gate "Tier-1 test suite green."

# --- Gate 4: content lint ---
$gate = "content lint"
$lintOutput = dotnet run --project sim/Hearthspan.ContentLint/Hearthspan.ContentLint.csproj --no-build 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $lintOutput
    Fail $gate "Hearthspan.ContentLint exited non-zero ($LASTEXITCODE)."
}
try {
    $lintJson = $lintOutput | Select-Object -Last 1 | ConvertFrom-Json
} catch {
    Fail $gate "ContentLint did not emit a single JSON object on stdout as the CLI contract requires."
}
if ($lintJson.status -ne "ok") { Fail $gate "ContentLint reported status '$($lintJson.status)'." }
Pass $gate "$($lintJson.filesFound) content file(s) scanned, status ok."

# --- Gate 5: golden-hash comparison ---
$gate = "golden-hash comparison"
$goldenPath = Join-Path $repoRoot "tests\golden\hash-chain-seed42.json"
if (-not (Test-Path $goldenPath)) {
    Write-Host "[SKIP] $gate - tests/golden/hash-chain-seed42.json not found." -ForegroundColor Yellow
} else {
    $golden = Get-Content $goldenPath -Raw | ConvertFrom-Json
    $harnessDll = Join-Path $repoRoot "sim\Hearthspan.Sim.Harness\bin\Debug\net8.0\Hearthspan.Sim.Harness.dll"
    if (-not (Test-Path $harnessDll)) { Fail $gate "harness dll not found at $harnessDll - did gate 2 build?" }

    $maxTurn = ($golden.checkpoints | ForEach-Object { $_.turn } | Measure-Object -Maximum).Maximum
    $checkpointsArg = ($golden.checkpoints | ForEach-Object { $_.turn }) -join ","
    $hashOutput = dotnet $harnessDll hash --turns $maxTurn --seed $golden.seed --width $golden.width --height $golden.height --checkpoints $checkpointsArg 2>&1
    if ($LASTEXITCODE -ne 0) { Fail $gate "hearthspan hash exited non-zero ($LASTEXITCODE): $hashOutput" }

    try {
        $actual = ($hashOutput | Select-Object -Last 1) | ConvertFrom-Json
    } catch {
        Fail $gate "hearthspan hash did not emit a single JSON object on stdout."
    }

    $mismatches = @()
    foreach ($expected in $golden.checkpoints) {
        $match = $actual.checkpoints | Where-Object { $_.turn -eq $expected.turn }
        if (-not $match) { $mismatches += "turn $($expected.turn): no checkpoint returned"; continue }
        if ($match.hash -ne $expected.hash) {
            $mismatches += "turn $($expected.turn): expected $($expected.hash), got $($match.hash)"
        }
    }
    if ($mismatches.Count -gt 0) {
        $mismatches | ForEach-Object { Write-Host "  $_" }
        Fail $gate "state hash chain diverged from tests/golden/hash-chain-seed42.json - goldens are updated only deliberately, never regenerated to make red go away."
    }
    Pass $gate "hash chain matches golden at turns $checkpointsArg; $($actual.finalSettlementCount) settlements at turn $maxTurn (zero-civ)."

    # FABLE STOP 4: the Phase 1 rider ">= 400 settlements at turn 500" used to be asserted on the
    # zero-civ hash world above. That world has no capitals and so no trade channel, and a spawn
    # threshold selective enough for the player's verbs to matter (SpawnEngine.SpawnThreshold 1.6)
    # leaves it far below 400 by design. The scale claim that actually matters is design decision 6 -
    # 400-600 settlements at game end on the standard map - so it is measured here on the real game:
    # same seed/size as the golden, 4 civs, real content. Band, not floor: too many is as wrong as too few.
    $gateCount = "settlement-count band (with civs)"
    $countOutput = dotnet $harnessDll sim --turns $maxTurn --seed $golden.seed --width $golden.width --height $golden.height --civs 4 --content (Join-Path $repoRoot "content") 2>&1
    if ($LASTEXITCODE -ne 0) { Fail $gateCount "hearthspan sim exited non-zero ($LASTEXITCODE): $countOutput" }
    try {
        $countJson = ($countOutput | Select-Object -Last 1) | ConvertFrom-Json
    } catch {
        Fail $gateCount "hearthspan sim did not emit a single JSON object on stdout."
    }
    if ($countJson.settlementCount -lt 400 -or $countJson.settlementCount -gt 600) {
        Fail $gateCount "design decision 6 requires 400-600 settlements at turn $maxTurn on the standard map with civs, got $($countJson.settlementCount)."
    }
    Pass $gateCount "$($countJson.settlementCount) settlements at turn $maxTurn on $($golden.width)x$($golden.height), 4 civs, real content (band 400-600)."
}


# --- Gate 6: balance envelope ---
$gate = "balance envelope"
$envelopePath = Join-Path $repoRoot "tests\golden\balance-envelope.json"
if (-not (Test-Path $envelopePath)) {
    Write-Host "[SKIP] $gate - tests/golden/balance-envelope.json not found." -ForegroundColor Yellow
} else {
    $envelope = Get-Content $envelopePath -Raw | ConvertFrom-Json
    $harnessDll = Join-Path $repoRoot "sim\Hearthspan.Sim.Harness\bin\Debug\net8.0\Hearthspan.Sim.Harness.dll"
    if (-not (Test-Path $harnessDll)) { Fail $gate "harness dll not found at $harnessDll - did gate 2 build?" }

    # Seeds/turns/dimensions/civCount come from the envelope file itself, not hand-typed here, so the
    # CI gate can never silently drift from what the committed envelope was actually built against.
    # --civs was added to the schema in Wave 0 (Envelope.CivCount) with intent to move this gate off
    # the zero-civ world, but ci.ps1 was never actually updated to pass it and the committed envelope
    # was never regenerated with civs baked in - the gate silently stayed zero-civ for two stages,
    # invisible to any civ/AI/unit mechanics (found running Stage 4's envelope ritual, docs/
    # stage-4-review.md). Fixed here: --civs from the envelope, --content hardcoded to the repo's one
    # content root (same pattern gate 7's perf check already uses).
    $balanceOutput = dotnet $harnessDll balance `
        --seeds $envelope.seeds --turns $envelope.turns --era-turns $envelope.eraTurns `
        --width $envelope.width --height $envelope.height --civs $envelope.civCount `
        --content (Join-Path $repoRoot "content") --envelope $envelopePath 2>&1
    $balanceExit = $LASTEXITCODE

    try {
        $balanceJson = ($balanceOutput | Select-Object -Last 1) | ConvertFrom-Json
    } catch {
        Write-Host $balanceOutput
        Fail $gate "hearthspan balance did not emit a single JSON object on stdout."
    }

    if ($balanceExit -ne 0 -or -not $balanceJson.envelope.pass) {
        $balanceJson.envelope.violations | ForEach-Object {
            Write-Host "  era $($_.era) (turn $($_.turn)): $($_.metric) = $($_.actual), outside [$($_.min), $($_.max)]"
        }
        Fail $gate "$($balanceJson.envelope.violationCount) metric(s) outside the committed envelope - the envelope never moves silently; any legitimate shift needs Nicolas's sign-off with before/after distributions."
    }
    Pass $gate "$($envelope.seeds) seeds x $($envelope.turns) turns x $($envelope.civCount) civs inside the committed envelope, 0 violations."
}

# --- Gate 7: per-turn perf budget ---
# The wave contract's perf gate (docs/phase-5-plan.md §3): p95 turn time <= 100 ms at the wave's
# end-game settlement count. Wave 0's scale is 400 settlements with civs; later waves raise
# --settlements as their own scale grows (Wave 5 takes it to 600).
$gate = "perf budget"
$harnessDll = Join-Path $repoRoot "sim\Hearthspan.Sim.Harness\bin\Debug\net8.0\Hearthspan.Sim.Harness.dll"
if (-not (Test-Path $harnessDll)) { Fail $gate "harness dll not found at $harnessDll - did gate 2 build?" }

$perfOutput = dotnet $harnessDll perf --settlements 400 --civs 8 --content (Join-Path $repoRoot "content") --budget-ms 100 2>&1
$perfExit = $LASTEXITCODE

try {
    $perfJson = ($perfOutput | Select-Object -Last 1) | ConvertFrom-Json
} catch {
    Write-Host $perfOutput
    Fail $gate "hearthspan perf did not emit a single JSON object on stdout."
}

if ($perfExit -ne 0 -or -not $perfJson.withinBudget) {
    Fail $gate "p95 turn time $($perfJson.turnMs.p95) ms exceeds the $($perfJson.budgetMs) ms budget at $($perfJson.actualSettlements) settlements (status: $($perfJson.status))."
}
if (-not $perfJson.reachedScale) {
    # Never pass a scale gate that was never actually tested at that scale.
    Fail $gate "only reached $($perfJson.actualSettlements) of $($perfJson.targetSettlements) settlements - the budget was not tested at the requested scale."
}
Pass $gate "p95 $($perfJson.turnMs.p95) ms (budget $($perfJson.budgetMs) ms) at $($perfJson.actualSettlements) settlements, 8 civs."

# --- Gate 8: steering changes the map ---
# FABLE STOP 4 (docs/archive/plan-v3.md section 7): the player's verbs must visibly change where their web grows.
# Two worlds per seed from the same seed - one idle, one steered by the harness's ScriptedPlayer -
# and the steered civ must own >= 1.5x the settlements while the settled tiles near its capital look
# <= 0.6 Jaccard-similar. Signed numbers at the stop: ownedRatio 4.55, meanSimilarity 0.574
# (docs/review/plan-v3/envelope-before-after.md). --assert makes a miss exit non-zero.
$gate = "steer target"
$steerOutput = dotnet $harnessDll steer --seeds 20 --turns 120 --content (Join-Path $repoRoot "content") --assert 2>&1
$steerExit = $LASTEXITCODE

try {
    $steerJson = ($steerOutput | Select-Object -Last 1) | ConvertFrom-Json
} catch {
    Write-Host $steerOutput
    Fail $gate "hearthspan steer did not emit a single JSON object on stdout."
}

if ($steerExit -ne 0 -or -not $steerJson.meetsTarget) {
    Fail $gate "ownedRatio $($steerJson.ownedRatio) (needs >= $($steerJson.minRatio)), meanSimilarity $($steerJson.meanSimilarity) (needs <= $($steerJson.maxSimilarity)) - steering no longer changes the map enough; the levers are listed in docs/archive/plan-v3.md section 7 (FABLE STOP 4)."
}
Pass $gate "ownedRatio $([math]::Round($steerJson.ownedRatio, 3)) >= $($steerJson.minRatio), meanSimilarity $([math]::Round($steerJson.meanSimilarity, 3)) <= $($steerJson.maxSimilarity) over $($steerJson.seeds) seeds x $($steerJson.turns) turns."

Write-Host ""
Write-Host "All CI gates green." -ForegroundColor Green
exit 0
