# Builds the full release set (toolchain packages + VSIX) starting from the `boot-net10`
# orphan branch's stage1 seed, without needing .NET Framework / Windows to bootstrap the
# compiler. This is the consuming half of dotnet-port/publish-boot.ps1: that script (Windows
# only, run by a maintainer) commits a Windows-built Stage1 compiler directory to the orphan
# `boot-net10` branch; this script (Windows or Linux) fetches that seed and drives the existing
# dotnet-based build chain (build-stage2-core.ps1 -> build-libs-core.ps1 -> pack-tool.ps1 ->
# pack-server.ps1 -> the VS Code extension's `npm run package` -> pack-release.ps1) from it.
#
# Prerequisites: the .NET 10 SDK, `pwsh` (PowerShell 7+), Node.js 22, and `git`. No .NET
# Framework and no Windows-only tooling anywhere in this script.
#
# Two build paths, chosen automatically (step 4 below):
#   - IN PLACE, when this checkout's own `git describe --tags --long` already matches the
#     seed's generation: builds directly in this repo checkout. Requires a clean working tree
#     (pack-release.ps1 refuses to seal a release from a dirty one).
#   - PINNED WORKTREE, when the generations differ (the common case right after a fresh clone,
#     since the seed was published from whatever commit last had Stage1 rebuilt on Windows):
#     adds a `git worktree` at bin/boot-build-tree, checked out at the seed's own commit, and
#     builds there instead -- so the sources match the compiler the seed actually is. The
#     worktree path is fixed (not a temp directory) so that repeated runs check out to the same
#     absolute path: generated binaries embed their checkout path (see dotnet-port's WP-N5
#     findings), so a stable path is what makes repeated builds byte-reproduce.
#
# Caveat: because of that same checkout-path embedding, artifacts built by this script do NOT
# byte-match the project's own official GitHub release assets (built from a different absolute
# path). They are a correct, usable release set -- just not a byte-identical rebuild of one.
#
# Usage:
#   pwsh dotnet-port/build-from-boot.ps1                  # boot-net10 (or origin/boot-net10)
#   pwsh dotnet-port/build-from-boot.ps1 -Branch other-seed-branch
#   pwsh dotnet-port/build-from-boot.ps1 -KeepWorktree     # leave bin/boot-build-tree in place
#                                                           # for inspection/debugging

param(
    [string]$Branch = "boot-net10",
    [switch]$KeepWorktree
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# 1. Resolve the seed ref: prefer a local branch, fall back to the remote-tracking one (the
#    shape a plain `git clone` of this repo leaves behind), and give an actionable error if
#    neither exists -- most commonly because this is a --single-branch clone or a source
#    ZIP/tarball, neither of which fetches any branch other than the default one.
# ---------------------------------------------------------------------------
& git -C $RepoRoot rev-parse --verify --quiet $Branch | Out-Null
$ref = if ($LASTEXITCODE -eq 0) { $Branch } else { $null }

if ($null -eq $ref) {
    & git -C $RepoRoot rev-parse --verify --quiet "origin/$Branch" | Out-Null
    if ($LASTEXITCODE -eq 0) { $ref = "origin/$Branch" }
}

if ($null -eq $ref) {
    throw "Could not resolve '$Branch' or 'origin/$Branch' in this checkout. If this is a --single-branch clone (or a source archive/ZIP), the boot seed branch was never fetched. Run:`n  git fetch origin ${Branch}:${Branch}`nand re-run this script."
}
Write-Host "Using seed ref: $ref"

# ---------------------------------------------------------------------------
# 2. Read the seed's generation out of boot-info.json (committed alongside the seed files by
#    publish-boot.ps1), without checking anything out yet.
# ---------------------------------------------------------------------------
$bootInfoText = & git -C $RepoRoot show "${ref}:boot-info.json" 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($bootInfoText)) {
    throw "Could not read boot-info.json from '$ref' -- is this really a boot-net10 seed commit (produced by dotnet-port/publish-boot.ps1)?"
}
$bootInfo = $bootInfoText | ConvertFrom-Json
if ($bootInfo.schema -ne 1) {
    throw "boot-info.json at '$ref' has schema $($bootInfo.schema), but this script only understands schema 1. Use a build-from-boot.ps1 that matches the seed's schema."
}
$G = $bootInfo.generation
Write-Host "Seed generation: commit $($G.commit) ($($G.describe)), Nemerle assembly version $($G.nemerleAssemblyVersion)"

# ---------------------------------------------------------------------------
# 3. Expand the seed into bin/boot-net10/ and verify every file's SHA256 against boot-info.json
#    -- catches a truncated/corrupted fetch before it turns into a confusing build failure.
# ---------------------------------------------------------------------------
$BootDir = Join-Path $RepoRoot "bin/boot-net10"
if (Test-Path $BootDir) { Remove-Item -Recurse -Force $BootDir }
New-Item -ItemType Directory -Force -Path $BootDir | Out-Null

$TmpZip = Join-Path ([System.IO.Path]::GetTempPath()) ("nemerle-boot-seed-" + [guid]::NewGuid().ToString("N") + ".zip")
try {
    & git -C $RepoRoot archive --format=zip -o $TmpZip $ref
    if ($LASTEXITCODE -ne 0) { throw "'git archive --format=zip $ref' failed (exit $LASTEXITCODE)" }
    Expand-Archive -Path $TmpZip -DestinationPath $BootDir -Force
}
finally {
    Remove-Item -Force $TmpZip -ErrorAction SilentlyContinue
}

$fileNames = $bootInfo.files.PSObject.Properties.Name
foreach ($name in $fileNames) {
    $filePath = Join-Path $BootDir $name
    if (-not (Test-Path $filePath)) { throw "Seed is missing '$name' -- declared in boot-info.json but not present after extracting '$ref'." }
    $actualHash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $bootInfo.files.$name
    if ($actualHash -ne $expectedHash) {
        throw "Seed file '$name' does not match the SHA256 recorded in boot-info.json (expected $expectedHash, got $actualHash) -- the fetched seed may be corrupted or truncated. Re-fetch '$Branch' and try again."
    }
}
Write-Host "Verified $($fileNames.Count) seed files against boot-info.json -> $BootDir"

# ---------------------------------------------------------------------------
# 4. Decide the build path: in place if this checkout's generation already matches the seed's,
#    otherwise a pinned worktree checked out at the seed's own commit. See header for why.
# ---------------------------------------------------------------------------
$describeDirty = (& git -C $RepoRoot describe --tags --long --dirty).Trim()
if ($LASTEXITCODE -ne 0) { throw "'git describe --tags --long --dirty' failed in $RepoRoot (exit $LASTEXITCODE) -- is this a git checkout with at least one tag reachable from HEAD?" }
$describeNow = $describeDirty -replace '-dirty$', ''

if ($describeNow -eq $G.describe) {
    if ($describeDirty -match '-dirty$') {
        throw "This checkout's generation matches the seed ($describeNow), but the working tree is dirty. pack-release.ps1 requires a clean tree to seal a release. Commit or stash your changes and re-run -- or, if you have not made any changes of your own, simply re-run this script once 'git status' is clean."
    }
    Write-Host "Generation matches this checkout's HEAD ($describeNow) -- building in place."
    $BuildTree = $RepoRoot
    $usingWorktree = $false
}
else {
    $WorktreeDir = Join-Path $RepoRoot "bin/boot-build-tree"
    if (Test-Path $WorktreeDir) {
        Write-Host "Removing existing worktree at $WorktreeDir ..."
        & git -C $RepoRoot worktree remove --force $WorktreeDir 2>$null
        & git -C $RepoRoot worktree prune 2>$null
        if (Test-Path $WorktreeDir) { Remove-Item -Recurse -Force $WorktreeDir -ErrorAction SilentlyContinue }
    }
    Write-Host "Generation differs (this checkout is $describeNow, seed is $($G.describe)) -- building in a pinned worktree at commit $($G.commit)."
    & git -C $RepoRoot worktree add $WorktreeDir $G.commit
    if ($LASTEXITCODE -ne 0) { throw "'git worktree add $WorktreeDir $($G.commit)' failed (exit $LASTEXITCODE) -- is commit $($G.commit) reachable in this clone? (a shallow clone may need 'git fetch --unshallow'.)" }
    $BuildTree = $WorktreeDir
    $usingWorktree = $true
}

# ---------------------------------------------------------------------------
# 5. Build chain. Each step is a child pwsh process (so a failure's own error output is not
#    swallowed by dot-sourcing), and every step's exit code is checked immediately. When
#    building in a worktree, the scripts invoked are the WORKTREE's own copies -- they must
#    match the seed's generation G, not whatever HEAD looks like in $RepoRoot.
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

$DotnetPortDir = Join-Path $BuildTree "dotnet-port"
$SeedCompiler = Join-Path $BootDir "ncc.exe"

Invoke-BootStep -ScriptPath (Join-Path $DotnetPortDir "build-stage2-core.ps1") -ScriptArgs @("-Compiler", $SeedCompiler)
Invoke-BootStep -ScriptPath (Join-Path $DotnetPortDir "build-libs-core.ps1")
Invoke-BootStep -ScriptPath (Join-Path $DotnetPortDir "pack-tool.ps1") -ScriptArgs @("-Pack")
Invoke-BootStep -ScriptPath (Join-Path $DotnetPortDir "vscode-nemerle/pack-server.ps1")

$VscodeDir = Join-Path $DotnetPortDir "vscode-nemerle"
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

Invoke-BootStep -ScriptPath (Join-Path $DotnetPortDir "pack-release.ps1")

# ---------------------------------------------------------------------------
# 6. Collect the artifacts. In place, dotnet-port/dist/release IS the release, already where a
#    caller would expect it. From a worktree, copy it out to dotnet-port/dist/release-from-boot
#    in the main checkout (the worktree itself is transient), then clean up the worktree unless
#    -KeepWorktree was passed.
# ---------------------------------------------------------------------------
if ($usingWorktree) {
    $ReleaseSrc = Join-Path $BuildTree "dotnet-port/dist/release"
    if (-not (Test-Path $ReleaseSrc)) { throw "Expected release output not found at $ReleaseSrc after pack-release.ps1 succeeded -- this should not happen." }

    $ReleaseDest = Join-Path $RepoRoot "dotnet-port/dist/release-from-boot"
    if (Test-Path $ReleaseDest) { Remove-Item -Recurse -Force $ReleaseDest }
    New-Item -ItemType Directory -Force -Path $ReleaseDest | Out-Null
    Copy-Item -Path (Join-Path $ReleaseSrc "*") -Destination $ReleaseDest -Recurse -Force
    $FinalReleaseDir = $ReleaseDest

    if ($KeepWorktree) {
        Write-Host ""
        Write-Host "Keeping worktree at $BuildTree (-KeepWorktree)."
    }
    else {
        Write-Host ""
        Write-Host "Removing worktree $BuildTree ..."
        & git -C $RepoRoot worktree remove --force $BuildTree
        if ($LASTEXITCODE -ne 0) { Write-Warning "'git worktree remove --force $BuildTree' failed (exit $LASTEXITCODE) -- remove it by hand if needed." }
    }
}
else {
    $FinalReleaseDir = Join-Path $RepoRoot "dotnet-port/dist/release"
}

Write-Host ""
Write-Host "Release set -> $FinalReleaseDir"
Get-ChildItem -Path $FinalReleaseDir -File | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name)" }
