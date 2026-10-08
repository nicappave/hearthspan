<#
.SYNOPSIS
    Hearthspan Plan v3 task 0.4 - download and extract the five Kenney CC0 packs into art/_packs/.

.DESCRIPTION
    Nicolas pre-approved these downloads on 3 September 2026 (all CC0 1.0, no attribution required).
    art/_packs/ is gitignored (task 0.4) - only the specific pieces the manifest tables (Appendix A)
    need get copied out into game/art/<category>/ by hand, per art/CONVENTIONS.md.
    Skips a download if the zip already exists, and skips extraction if the folder already exists.

.PARAMETER PacksOnly
    Comma-separated pack names to fetch (default: all five).
#>

param(
    [string[]]$PacksOnly = @("hexagon-kit", "fantasy-town-kit", "castle-kit", "nature-kit", "survival-kit")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repoRoot

function Fail($reason) {
    Write-Host "[FAIL] fetch-packs - $reason" -ForegroundColor Red
    exit 1
}

# name -> (url, minBytes)
$packs = @{
    "hexagon-kit"      = @{ Url = "https://kenney.nl/media/pages/assets/hexagon-kit/fd3b63101b-1706007730/kenney_hexagon-kit.zip";                        MinBytes = 1MB }
    "fantasy-town-kit" = @{ Url = "https://kenney.nl/media/pages/assets/fantasy-town-kit/efe948d309-1754222374/kenney_fantasy-town-kit_2.0.zip";          MinBytes = 1MB }
    "castle-kit"       = @{ Url = "https://kenney.nl/media/pages/assets/castle-kit/a395102d20-1711543616/kenney_castle-kit.zip";                          MinBytes = 1MB }
    "nature-kit"       = @{ Url = "https://kenney.nl/media/pages/assets/nature-kit/37ac38a37b-1677698939/kenney_nature-kit.zip";                          MinBytes = 1MB }
    "survival-kit"     = @{ Url = "https://kenney.nl/media/pages/assets/survival-kit/4065a8185b-1712149243/kenney_survival-kit.zip";                      MinBytes = 1MB }
}

$packsDir = Join-Path $repoRoot "art\_packs"
New-Item -ItemType Directory -Force -Path $packsDir | Out-Null

foreach ($name in $PacksOnly) {
    if (-not $packs.ContainsKey($name)) { Fail "unknown pack '$name'." }
    $info = $packs[$name]
    $zipPath = Join-Path $packsDir "$name.zip"
    $extractDir = Join-Path $packsDir $name

    if (-not (Test-Path $zipPath)) {
        Write-Host "Downloading $name from $($info.Url) ..."
        try {
            Invoke-WebRequest -Uri $info.Url -OutFile $zipPath -UseBasicParsing
        } catch {
            Fail "download of $name failed from $($info.Url): $($_.Exception.Message). Kenney rotates the hash segment when a pack updates - fetch https://kenney.nl/assets/$name and take the first .zip link, then record the new URL in docs/handover.md."
        }
    } else {
        Write-Host "$name.zip already present, skipping download."
    }

    $zipSize = (Get-Item $zipPath).Length
    if ($zipSize -lt $info.MinBytes) {
        Fail "$zipPath is only $zipSize bytes, expected at least $($info.MinBytes) - download likely failed or returned an error page."
    }

    if (-not (Test-Path $extractDir)) {
        Write-Host "Extracting $name..."
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    } else {
        Write-Host "$name already extracted, skipping."
    }
}

Write-Host "[PASS] fetch-packs - $($PacksOnly.Count) pack(s) present under $packsDir"
exit 0
