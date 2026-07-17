# Seals dotnet-port\dist\release into a coherent, hand-off-able release set.
#
# It does NOT build anything: pack-tool.ps1 (toolchain + packages) and the extension's
# `npm run package` (VSIX) already write into that folder. This is the last step, and its job is
# to answer the one question a downloader cannot answer for themselves -- "do these artifacts
# belong together?" -- and then record the answer in release-info.json.
#
#   pwsh dotnet-port\pack-tool.ps1 -Pack               # dist\ncc + packages + guide -> dist\release
#   pwsh dotnet-port\vscode-nemerle\pack-server.ps1    # stages server\ (from the same dist\ncc)
#   cd dotnet-port\vscode-nemerle; npm run package     # VSIX -> dist\release
#   pwsh dotnet-port\pack-release.ps1                  # verify + release-info.json
#
# Why the check is worth a script: the VSIX and the .nupkg are built by different tools from
# different directories, so it is entirely possible -- and has happened during WP-M6 -- to end up
# with a VSIX packed from an older commit sitting next to freshly packed packages. Both halves
# already record their own provenance (server\bundle-info.json inside the VSIX, tools\ncc\
# ncc-info.json inside the SDK package), so the mismatch is detectable; nothing was reading them
# together until now. Reading them from INSIDE the artifacts (rather than from the working tree
# they were supposedly built from) is the point: it is the shipped bits that have to agree.
#
# Version note: the VSIX version (extension feature generation) and the package version
# (compiler generation, 1.2.<rev>-preview.N) are deliberately NOT the same number -- they move at
# different rates, and the VS Code Marketplace does not accept semver prerelease tags anyway
# (35-devenv2-wp-m6-log.md). The release, not the artifact, is what has one version; that is what
# the GitHub release tag is for, and release-info.json is the machine-readable form of it.

param(
    [string]$ReleaseDir = "",   # default: dotnet-port\dist\release
    # Seal a release built from a dirty working tree. Off by default: the provenance recorded in
    # the artifacts would say "-dirty", which is unreproducible for whoever receives them.
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($ReleaseDir -eq "") { $ReleaseDir = Join-Path $PSScriptRoot "dist/release" }
if (-not (Test-Path $ReleaseDir)) {
    throw "Release directory not found: $ReleaseDir (run `pwsh dotnet-port/pack-tool.ps1 -Pack` first)"
}
$ReleaseDir = (Resolve-Path $ReleaseDir).Path

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Reads one text entry out of a zip-shaped artifact (.nupkg / .vsix are both zips).
function Read-ArchiveEntry {
    param([string]$ArchivePath, [string]$EntryName)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

Push-Location $RepoRoot
try {
    $commit   = (& git rev-parse HEAD).Trim()
    $describe = (& git describe --long --always --dirty).Trim()
}
finally {
    Pop-Location
}
if ($describe -match '-dirty$' -and -not $AllowDirty) {
    throw "Working tree is dirty ($describe). The artifacts' own provenance will record '-dirty', which nobody can reproduce. Commit first, rebuild the release, and re-run; or pass -AllowDirty for a throwaway build."
}

# ---------------------------------------------------------------------------
# 1. The packages, and the compiler generation they carry.
# ---------------------------------------------------------------------------
$packageFiles = Get-ChildItem $ReleaseDir -Filter "*.nupkg" -File
if ($packageFiles.Count -eq 0) { throw "No .nupkg in $ReleaseDir -- run `pwsh dotnet-port/pack-tool.ps1 -Pack` first." }

$packages = @()
$toolchainInfo = $null
foreach ($file in $packageFiles) {
    # "Nemerle.Sdk.Unofficial.1.2.601-preview.2.nupkg" -> id + version. The version starts at the
    # first dot-separated part that begins with a digit.
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($stem -notmatch '^(?<id>.+?)\.(?<version>\d+\..*)$') { throw "Cannot parse an id/version out of '$($file.Name)'." }
    $packages += [ordered]@{ id = $Matches['id']; version = $Matches['version']; file = $file.Name }

    $info = Read-ArchiveEntry -ArchivePath $file.FullName -EntryName "tools/ncc/ncc-info.json"
    if ($info) { $toolchainInfo = $info | ConvertFrom-Json }
}
if (-not $toolchainInfo) { throw "No package in $ReleaseDir carries tools/ncc/ncc-info.json -- the SDK package is missing or was packed before WP-M6 added provenance." }

# ---------------------------------------------------------------------------
# 2. The VSIX, and the generation its bundled language server carries.
# ---------------------------------------------------------------------------
$manifestPath = Join-Path $PSScriptRoot "vscode-nemerle/package.json"
$extensionVersion = (Get-Content -Raw $manifestPath | ConvertFrom-Json).version
$vsixName = "vscode-nemerle-$extensionVersion.vsix"
$vsixPath = Join-Path $ReleaseDir $vsixName
if (-not (Test-Path $vsixPath)) {
    throw "$vsixName not found in $ReleaseDir. Build it with: pwsh dotnet-port/vscode-nemerle/pack-server.ps1; cd dotnet-port/vscode-nemerle; npm run package"
}
$bundleInfoText = Read-ArchiveEntry -ArchivePath $vsixPath -EntryName "extension/server/bundle-info.json"
if (-not $bundleInfoText) { throw "$vsixName does not contain extension/server/bundle-info.json -- it was packaged without pack-server.ps1 staging the server." }
$bundleInfo = $bundleInfoText | ConvertFrom-Json

# ---------------------------------------------------------------------------
# 3. The check this script exists for.
#
#    Compare commits, not assembly versions. The server's own runtime check already compares
#    assembly versions (ToolchainProvenance), and that is the right test THERE: it decides
#    whether the bits can load each other. Here the question is different and stricter -- "were
#    these two artifacts built from the same source?" -- because a release is a claim about
#    provenance. Two builds from different commits can share an assembly version whenever the
#    compiler was not rebuilt in between (WP-M2..M6 all carried 1.2.0.601), so the assembly
#    version cannot detect a stale VSIX, and it is exactly a stale VSIX that this catches.
# ---------------------------------------------------------------------------
if ($bundleInfo.commit -ne $toolchainInfo.commit) {
    throw @"
Release halted: the VSIX and the packages were built from different commits.
  $vsixName        server packed from $($bundleInfo.commit) ($($bundleInfo.describe))
  packages         toolchain packed from $($toolchainInfo.commit) ($($toolchainInfo.describe))
Rebuild both from the current commit ($commit) and re-run:
  pwsh dotnet-port/pack-tool.ps1 -Pack
  pwsh dotnet-port/vscode-nemerle/pack-server.ps1
  cd dotnet-port/vscode-nemerle; npm run package
"@
}
if ($bundleInfo.commit -ne $commit) {
    throw "Release halted: both halves were built from $($bundleInfo.commit), but HEAD is $commit. Rebuild the release from HEAD (see pack-release.ps1's header for the order), or check out that commit."
}

# ---------------------------------------------------------------------------
# 3b. WP-N1 (A2): the packaged toolchain's own Nemerle assembly version must match what HEAD
#     (the commit just verified above) expects. This is a narrower, mechanized restatement of
#     the same hazard build-stage2-core.ps1/pack-tool.ps1 now guard against earlier in the
#     pipeline (dotnet-port\assembly-version-check.ps1) -- catching it here too means a release
#     built from a correct commit but with a stale (not-rebuilt) compiler inside the package
#     still gets flagged, instead of only being caught by a load failure at a consumer's site.
#     This script does not build anything, so on mismatch it reports and halts the same way as
#     the commit checks above rather than offering a rebuild command mid-script.
# ---------------------------------------------------------------------------
. "$PSScriptRoot/assembly-version-check.ps1"
$expectedNemerleAssemblyVersion = Get-ExpectedNemerleAssemblyVersion -RepoRoot $RepoRoot
if ($null -eq $expectedNemerleAssemblyVersion) {
    Write-Warning "Could not determine an expected Nemerle assembly version from 'git describe --tags --long' at $RepoRoot -- skipping the packaged-toolchain freshness check."
}
elseif ($toolchainInfo.nemerleAssemblyVersion -ne $expectedNemerleAssemblyVersion) {
    throw @"
Release halted: the packaged toolchain's Nemerle assembly version does not match what HEAD ($commit) expects.
  packages         nemerleAssemblyVersion $($toolchainInfo.nemerleAssemblyVersion) (from ncc-info.json, commit $($toolchainInfo.commit))
  HEAD expects     $expectedNemerleAssemblyVersion (from 'git describe --tags --long')
This means the packaged compiler was built before the current commit's assembly-version-affecting
history, even though its recorded commit matched. Rebuild the toolchain from HEAD and re-pack:
  pwsh dotnet-port/build-stage2-core.ps1
  pwsh dotnet-port/pack-tool.ps1 -Pack
"@
}

# ---------------------------------------------------------------------------
# 4. Record it.
# ---------------------------------------------------------------------------
$releaseInfo = [ordered]@{
    commit                 = $commit
    describe               = $describe
    createdAtUtc           = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    # The identity the two halves must share at run time; the server warns if a project's
    # toolchain disagrees with it (29-devenv2-plan.md section 6.8).
    nemerleAssemblyVersion = $toolchainInfo.nemerleAssemblyVersion
    extension              = [ordered]@{
        file    = $vsixName
        version = $extensionVersion
    }
    packages               = $packages
    guide                  = "README.md"
}
$releaseInfoPath = Join-Path $ReleaseDir "release-info.json"
$releaseInfo | ConvertTo-Json -Depth 5 | Set-Content -Path $releaseInfoPath -Encoding utf8

Write-Host ""
Write-Host "Release sealed -> $ReleaseDir"
Write-Host "  commit                 $commit ($describe)"
Write-Host "  nemerleAssemblyVersion $($toolchainInfo.nemerleAssemblyVersion)"
Write-Host ""
Get-ChildItem $ReleaseDir -File | Format-Table Name, @{ Name = "KB"; Expression = { [math]::Round($_.Length / 1KB, 1) } }
Write-Host "Hand this folder over as-is (or zip it): the packages double as a NuGet local feed,"
Write-Host "README.md is the recipient's install guide, and release-info.json ties the set to a commit."
