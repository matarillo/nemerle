# Builds the full release set (toolchain packages + VSIX) from the checked-in stage1 seed in
# dotnet-port/seed/, without needing .NET Framework / Windows to bootstrap the compiler. This is
# the consuming half of dotnet-port/publish-seed.ps1: that script (Windows only, run by a
# maintainer) refreshes the seed directory from a Windows-built Stage1; this script (Windows or
# Linux) verifies it and drives the existing dotnet-based build chain from it
# (build-stage2-core.ps1 -> build-libs-core.ps1 -> pack-tool.ps1 -> pack-server.ps1 -> the VS Code
# extension's `npm run package` -> pack-release.ps1).
#
# Prerequisites: the .NET 10 SDK, `pwsh` (PowerShell 7+), Node.js 22, and `git`. No .NET
# Framework and no Windows-only tooling anywhere in this script.
#
# WP-N7 (case 1) simplified this script substantially. The seed used to live on the orphan
# `boot-net10` branch, and a build had to happen in a `git worktree` pinned to the seed's own
# commit -- because assembly versions came from `git describe` and advanced every commit, so the
# seed compiler could only build the exact sources it was built from. With the version pinned by
# version.txt (dotnet-port/version-pin.ps1), any commit in the same version.txt span builds with
# the same seed, so:
#   - the seed is an ordinary checked-in directory (dotnet-port/seed/), fetched by a plain clone;
#   - the build always happens in this checkout, against THIS commit's sources -- what you have
#     checked out is what gets built;
#   - there is no orphan branch, no `.boot-build-tree` worktree, and no generation branching.
# See dotnet-port/44-prerelease-wp-n7-log.md sections 7 and 8.
#
# Usage:
#   pwsh dotnet-port/build-from-boot.ps1
#   pwsh dotnet-port/build-from-boot.ps1 -ReleaseTag release/1.2.635-preview.1   # reproduce a release
#   pwsh dotnet-port/build-from-boot.ps1 -PackageVersionSuffix preview.2

param(
    # Reproduce a published release, e.g. release/1.2.635-preview.1. Asserts that this checkout is
    # actually at that tag's commit (reproduction means building the release's own sources, and
    # since WP-N7 that is simply `git checkout <tag>` -- no worktree machinery required), and
    # derives the pack-tool.ps1 version suffix from the tag name.
    [string]$ReleaseTag = "",

    # Forwarded to pack-tool.ps1 -Pack as -PackageVersionSuffix. Normally derived automatically
    # from -ReleaseTag; only pass this directly when NOT using -ReleaseTag.
    [string]$PackageVersionSuffix = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$SeedDir = Join-Path $PSScriptRoot "seed"

# ---------------------------------------------------------------------------
# 1. -ReleaseTag: parse `release/<base>-<suffix>`, derive the package suffix, and require that
#    this checkout is at the tag's commit. Reproducing a release from a different commit would
#    silently produce a set that merely LOOKS like the release.
# ---------------------------------------------------------------------------
if ($ReleaseTag -ne "") {
    if ($ReleaseTag -notmatch '^release/(?<base>\d+\.\d+\.\d+)-(?<suffix>.+)$') {
        throw "-ReleaseTag '$ReleaseTag' does not match the expected 'release/<base>-<suffix>' shape (e.g. release/1.2.635-preview.1)."
    }
    $derivedSuffix = $Matches['suffix']
    $releaseTagBase = $Matches['base']

    # The base version comes from version.txt (the seed pin), NOT from the tag: everything this
    # script builds is stamped with the pinned version. A tag whose base differs would produce
    # packages stamped with the pin while the tag/Release claims another number -- a silent lie.
    # Releasing a NEW base version is a version.txt bump + seed refresh on Windows (44 section 8.6),
    # a different (CLR4) path than this seed-based build; fail here rather than mislabel a set.
    . "$PSScriptRoot/version-pin.ps1"
    $pinBase = (Get-NemerleVersionPin -RepoRoot $RepoRoot).Base
    if ($releaseTagBase -ne $pinBase) {
        throw "-ReleaseTag '$ReleaseTag' has base version $releaseTagBase, but version.txt pins $pinBase, so this build would stamp every package $pinBase -- not $releaseTagBase. Tag the release release/$pinBase-<suffix>. To release base $releaseTagBase you must first bump version.txt to $releaseTagBase and refresh the seed on Windows (dotnet-port/44-prerelease-wp-n7-log.md section 8.6)."
    }

    if ($PackageVersionSuffix -ne "" -and $PackageVersionSuffix -ne $derivedSuffix) {
        throw "-PackageVersionSuffix '$PackageVersionSuffix' disagrees with the suffix derived from -ReleaseTag '$ReleaseTag' ('$derivedSuffix'). Pass just -ReleaseTag, or drop -PackageVersionSuffix."
    }
    $PackageVersionSuffix = $derivedSuffix

    $releaseTagCommit = & git -C $RepoRoot rev-parse --verify --quiet "refs/tags/$ReleaseTag^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "Could not resolve tag '$ReleaseTag' in this checkout. If it was published on GitHub but not fetched here, run:`n  git fetch --tags`nand re-run this script." }
    $releaseTagCommit = $releaseTagCommit.Trim()

    $headCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    if ($headCommit -ne $releaseTagCommit) {
        throw "-ReleaseTag '$ReleaseTag' is at commit $releaseTagCommit, but this checkout is at $headCommit. Reproduction builds the release's own sources -- check it out first:`n  git checkout $ReleaseTag`nand re-run this script."
    }
    Write-Host "Reproducing $ReleaseTag (commit $releaseTagCommit, package suffix $PackageVersionSuffix)"
}

# ---------------------------------------------------------------------------
# 2. pack-release.ps1 refuses to seal a release from a dirty tree, and a dirty tree also means
#    the provenance recorded in the release would describe something nobody can reproduce.
#    Fail here rather than after a full build.
# ---------------------------------------------------------------------------
$status = & git -C $RepoRoot status --porcelain
if ($LASTEXITCODE -ne 0) { throw "'git status --porcelain' failed in $RepoRoot (exit $LASTEXITCODE) -- is this a git checkout?" }
if (-not [string]::IsNullOrWhiteSpace(($status -join ""))) {
    throw "Working tree is dirty. pack-release.ps1 requires a clean tree to seal a release. Commit or stash your changes and re-run -- or, if you have not made any changes of your own, simply re-run once 'git status' is clean."
}

# ---------------------------------------------------------------------------
# 3. Verify the checked-in seed (schema / version.txt span / per-file SHA256 / provenance).
#    The verification itself lives in verify-seed.ps1 so CI can run it without also running
#    this script's release sealing -- see dotnet-port/46-prerelease-wp-n6-log.md.
# ---------------------------------------------------------------------------
& pwsh -NoProfile -File (Join-Path $PSScriptRoot "verify-seed.ps1") -WarnOnly
if ($LASTEXITCODE -ne 0) { throw "Seed verification failed (exit $LASTEXITCODE)" }

# ---------------------------------------------------------------------------
# 4. Build chain, in this checkout. Each step is a child pwsh process (so a failure's own error
#    output is not swallowed by dot-sourcing), and every step's exit code is checked immediately.
# ---------------------------------------------------------------------------
function Invoke-BootStep {
    param(
        [string]$ScriptPath,
        [string[]]$ScriptArgs = @()
    )
    if (-not (Test-Path $ScriptPath)) { throw "Build step script not found: $ScriptPath" }
    Write-Host ""
    Write-Host "== pwsh $ScriptPath $($ScriptArgs -join ' ') =="
    & pwsh -NoProfile -File $ScriptPath @ScriptArgs
    if ($LASTEXITCODE -ne 0) { throw "$ScriptPath failed (exit $LASTEXITCODE)" }
}

$SeedCompiler = Join-Path $SeedDir "ncc.exe"

Invoke-BootStep -ScriptPath (Join-Path $PSScriptRoot "build-stage2-core.ps1") -ScriptArgs @("-Compiler", $SeedCompiler)
Invoke-BootStep -ScriptPath (Join-Path $PSScriptRoot "build-libs-core.ps1")
$packToolArgs = if ($PackageVersionSuffix -ne "") { @("-Pack", "-PackageVersionSuffix", $PackageVersionSuffix) } else { @("-Pack") }
Invoke-BootStep -ScriptPath (Join-Path $PSScriptRoot "pack-tool.ps1") -ScriptArgs $packToolArgs
Invoke-BootStep -ScriptPath (Join-Path $PSScriptRoot "vscode-nemerle/pack-server.ps1")

$VscodeDir = Join-Path $PSScriptRoot "vscode-nemerle"
Write-Host ""
Write-Host "== npm ci / npm run package ($VscodeDir) =="
Push-Location $VscodeDir
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw "'npm ci' failed in $VscodeDir (exit $LASTEXITCODE)" }
    & npm run package
    if ($LASTEXITCODE -ne 0) { throw "'npm run package' failed in $VscodeDir (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

Invoke-BootStep -ScriptPath (Join-Path $PSScriptRoot "pack-release.ps1")

$FinalReleaseDir = Join-Path $PSScriptRoot "dist/release"
Write-Host ""
Write-Host "Release set -> $FinalReleaseDir"
Get-ChildItem -Path $FinalReleaseDir -File | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name)" }
