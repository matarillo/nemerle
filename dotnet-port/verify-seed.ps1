# Verifies the checked-in stage1 seed in dotnet-port/seed/ against seed-info.json.
#
# Three questions, in order:
#   1. Is the metadata a schema this tooling understands?
#   2. Does the seed belong to THIS checkout's version.txt span?  A seed can only build sources
#      whose pinned version matches its own (44 section 7.1) -- mismatch means the version was
#      bumped without refreshing the seed, and every later error would be a confusing symptom
#      of that one fact.
#   3. Does every seed file match its recorded SHA256?  Catches a corrupted checkout before it
#      turns into an inscrutable compiler crash.
# Plus an informational provenance check: the seed being OLDER than HEAD is normal and is what
# pinning is for, but a seed built from a commit this checkout's history does not contain is not.
#
# Extracted from build-from-boot.ps1 for WP-N6: CI runs the build chain WITHOUT the release
# sealing that build-from-boot.ps1 performs (pack-release.ps1 requires a clean tree, which does
# not fit a PR build), but it still has to verify the seed first.  build-from-boot.ps1 now calls
# this script rather than carrying its own copy.  See dotnet-port/46-prerelease-wp-n6-log.md.
#
# Usage:
#   pwsh dotnet-port/verify-seed.ps1              # provenance mismatch is an error
#   pwsh dotnet-port/verify-seed.ps1 -WarnOnly    # provenance mismatch is a warning (CI, from-boot)

param(
    # Downgrade a non-ancestor provenance commit from an error to a warning.  Used by callers
    # that want to REPORT seed/source divergence without refusing to build (WP-N6 asked CI for
    # exactly this shape: surface it, do not gate on it).
    [switch]$WarnOnly
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$SeedDir = Join-Path $PSScriptRoot "seed"

$SeedInfoPath = Join-Path $SeedDir "seed-info.json"
if (-not (Test-Path $SeedInfoPath)) {
    throw "Seed metadata not found: $SeedInfoPath. The checked-in seed lives in dotnet-port/seed/ (WP-N7); if this is an old checkout that still used the boot-net10 orphan branch, use that commit's own build-from-boot.ps1."
}
$seedInfo = Get-Content -Raw -Path $SeedInfoPath | ConvertFrom-Json
if ($seedInfo.schema -ne 2) {
    throw "seed-info.json has schema $($seedInfo.schema), but this script only understands schema 2. Use a verify-seed.ps1 that matches the seed's schema."
}

. "$PSScriptRoot/version-pin.ps1"
$pin = Get-NemerleVersionPin -RepoRoot $RepoRoot
if ($seedInfo.pinnedVersion -ne $pin.Base) {
    throw "Seed is pinned to $($seedInfo.pinnedVersion) but this checkout's version.txt says $($pin.Base). They must match: a seed can only build sources in its own version.txt span (dotnet-port/44-prerelease-wp-n7-log.md section 7.1). If version.txt was just bumped, the seed has to be refreshed on Windows (section 7.4)."
}

$G = $seedInfo.generation
Write-Host "Seed: pinned $($seedInfo.pinnedVersion), built from commit $($G.commit) ($($G.describe)), Nemerle assembly version $($G.nemerleAssemblyVersion)"

. "$PSScriptRoot/assembly-version-check.ps1"
Test-NemerleProvenanceCommit -RecordedCommit $G.commit -RepoRoot $RepoRoot -Label "Seed" -WarnOnly:$WarnOnly

$fileNames = $seedInfo.files.PSObject.Properties.Name
foreach ($name in $fileNames) {
    $filePath = Join-Path $SeedDir $name
    if (-not (Test-Path $filePath)) { throw "Seed is missing '$name' -- declared in seed-info.json but not present in $SeedDir." }
    $actualHash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $seedInfo.files.$name
    if ($actualHash -ne $expectedHash) {
        throw "Seed file '$name' does not match the SHA256 recorded in seed-info.json (expected $expectedHash, got $actualHash). Restore it with 'git checkout -- dotnet-port/seed'."
    }
}
Write-Host "Verified $($fileNames.Count) seed files against seed-info.json -> $SeedDir"
