# WP-N1 A1: deterministic byte-for-byte comparison of two stage output directories.
#
# Historically this comparison was done ad hoc with throwaway scripts in a scratch
# directory (never checked in) because the emitted assemblies were not byte-stable
# across builds (MVID / PE timestamp / PDB ID were time- or randomness-derived, see
# dotnet-port\16-determinism-diagnosis.md). Now that Nemerle.CoreEmit\Emitter.cs
# derives those IDs deterministically from the emitted content (see ComputeDeterministicId
# in that file), a real, no-mask, full-byte comparison is meaningful and worth keeping
# in the repo instead of re-inventing it each time.
#
# For each file this compares (in order, stopping the individual comparison at the
# first failing check): existence in both directories, file size, then SHA256 of the
# full contents. On a size or hash mismatch, it also reports the offset of the first
# differing byte (found by a straightforward linear scan of both files -- these are
# small compiler-output assemblies, not build artifacts large enough to need anything
# smarter).
#
# Usage (compare a fresh Stage2 build against a fresh Stage3 self-host build):
#   pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage2 -DirB bin\Release\core\Stage3
#
# Usage (reproducibility check: build Stage2 twice into different directories, e.g.
# via build-stage2-core.ps1 -OutDir, and diff the two outputs):
#   pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage2a -DirB bin\Release\core\Stage2b
#
# -IncludePdb additionally compares each file's ".pdb" (e.g. Nemerle.pdb, ncc.pdb) --
# off by default because not every stage build is produced with -debug.
#
# Exit code: 0 when every compared file exists in both directories and matches
# byte-for-byte; 1 if anything is missing or differs (a specific reason is printed
# for each failing file).

param(
    [Parameter(Mandatory = $true)][string]$DirA,
    [Parameter(Mandatory = $true)][string]$DirB,
    [switch]$IncludePdb,
    [string[]]$Files = @("Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "ncc.exe")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $DirA)) { throw "DirA not found: $DirA" }
if (-not (Test-Path $DirB)) { throw "DirB not found: $DirB" }

$compareList = @($Files)
if ($IncludePdb) {
    $compareList += $Files | ForEach-Object { [System.IO.Path]::ChangeExtension($_, ".pdb") }
}

function FindFirstDiffOffset([string]$pathA, [string]$pathB) {
    $bytesA = [System.IO.File]::ReadAllBytes($pathA)
    $bytesB = [System.IO.File]::ReadAllBytes($pathB)
    $len = [Math]::Min($bytesA.Length, $bytesB.Length)
    for ($i = 0; $i -lt $len; $i++) {
        if ($bytesA[$i] -ne $bytesB[$i]) { return $i }
    }
    if ($bytesA.Length -ne $bytesB.Length) { return $len }
    return -1
}

$results = @()
$allOk = $true

foreach ($name in $compareList) {
    $pathA = Join-Path $DirA $name
    $pathB = Join-Path $DirB $name

    $existsA = Test-Path $pathA
    $existsB = Test-Path $pathB

    if (-not $existsA -or -not $existsB) {
        $allOk = $false
        $missing = @()
        if (-not $existsA) { $missing += "A" }
        if (-not $existsB) { $missing += "B" }
        $results += [PSCustomObject]@{
            File   = $name
            Status = "MISSING ($($missing -join ','))"
            SizeA  = $(if ($existsA) { (Get-Item $pathA).Length } else { "-" })
            SizeB  = $(if ($existsB) { (Get-Item $pathB).Length } else { "-" })
            Detail = ""
        }
        continue
    }

    $sizeA = (Get-Item $pathA).Length
    $sizeB = (Get-Item $pathB).Length

    if ($sizeA -ne $sizeB) {
        $allOk = $false
        $offset = FindFirstDiffOffset $pathA $pathB
        $results += [PSCustomObject]@{
            File   = $name
            Status = "SIZE MISMATCH"
            SizeA  = $sizeA
            SizeB  = $sizeB
            Detail = "first diff @ 0x$($offset.ToString('X'))"
        }
        continue
    }

    $hashA = (Get-FileHash -Path $pathA -Algorithm SHA256).Hash
    $hashB = (Get-FileHash -Path $pathB -Algorithm SHA256).Hash

    if ($hashA -ne $hashB) {
        $allOk = $false
        $offset = FindFirstDiffOffset $pathA $pathB
        $results += [PSCustomObject]@{
            File   = $name
            Status = "HASH MISMATCH"
            SizeA  = $sizeA
            SizeB  = $sizeB
            Detail = "first diff @ 0x$($offset.ToString('X')) (sha256 A=$hashA B=$hashB)"
        }
        continue
    }

    $results += [PSCustomObject]@{
        File   = $name
        Status = "OK"
        SizeA  = $sizeA
        SizeB  = $sizeB
        Detail = "sha256=$hashA"
    }
}

$results | Format-Table -Property File, Status, SizeA, SizeB, Detail -AutoSize -Wrap | Out-String -Width 4096 | Write-Host

if ($allOk) {
    Write-Host "All $($compareList.Count) file(s) match byte-for-byte between '$DirA' and '$DirB'."
    exit 0
} else {
    $bad = $results | Where-Object { $_.Status -ne "OK" }
    Write-Host "MISMATCH: $($bad.Count) of $($compareList.Count) file(s) differ or are missing between '$DirA' and '$DirB':"
    foreach ($r in $bad) {
        Write-Host "  - $($r.File): $($r.Status) $($r.Detail)"
    }
    exit 1
}
