# WP-N1 (A2): mechanizes the "compiler binary's Nemerle.dll does not match the version the
# build is about to stamp" hazard that was previously only a header-comment warning in
# build-stage2-core.ps1.
#
# Background: assembly versions come from the `GeneratedAssemblyVersion("$GitTag.0.$GitRevision")`
# macro (macros\AssemblyInfo.n). If a build loads a freshly built Nemerle.dll (same simple name,
# different version) into a compiler process built at another version, the CLR throws a ref-def
# mismatch FileLoadException ("manifest definition does not match") -- see dotnet-port\14-pdb-log.md
# F4 and dotnet-port\30-devenv2-wp-m1-log.md:191-197 for the failure mode.
#
# WP-N7 (case 1) changed where the expected version comes from. It used to be replayed from
# `git describe --tags --long`, which advanced on every commit -- so this check was really
# asking "was this compiler built from HEAD's exact commit?", and any commit at all invalidated
# every stage binary. The version is now pinned by the repo-root version.txt
# (dotnet-port\version-pin.ps1), so the question becomes "was this compiler built within the
# current version.txt span?" -- which is the condition the CoreCLR loader actually enforces.
# Per-commit identity moved to the provenance JSON files (seed/seed-info.json / ncc-info.json),
# which record the commit each binary was built from.
#
# This file is NOT meant to be executed directly -- it is a dot-source-only library of two
# functions, consumed by build-stage2-core.ps1 / build-libs-core.ps1 / pack-tool.ps1 /
# pack-release.ps1 / publish-seed.ps1. Dot-source it like:
#   . "$PSScriptRoot\assembly-version-check.ps1"
#   Test-NemerleAssemblyVersionFreshness -NemerleDllPath <path> -RepoRoot $RepoRoot -Label "Stage1"

. "$PSScriptRoot/version-pin.ps1"

# The assembly version a build at this checkout will stamp: the pinned base from version.txt,
# expressed as the macro expands it ("$GitTag.0.$GitRevision").
function Get-ExpectedNemerleAssemblyVersion {
    param(
        [string]$RepoRoot
    )

    return (Get-NemerleVersionPin -RepoRoot $RepoRoot).AssemblyVersion
}

# Compares a built Nemerle.dll's embedded AssemblyVersion against the pinned version this
# checkout builds at. $Label identifies the artifact in messages (e.g. "Stage1",
# "Compiler (bin\...\ncc.exe)", "CompilerDir (...)").
#
# - If they match, prints a short OK line.
# - If they differ, reports both versions plus the recovery procedure. By default this is a hard
#   failure (throw), matching how build-stage2-core.ps1 / pack-tool.ps1 / pack-release.ps1 already
#   fail fast on other precondition problems. Pass -WarnOnly to downgrade to Write-Warning and
#   continue -- the shape CI needs to *report* a stale checked-in seed without aborting the job
#   doing the reporting.
function Test-NemerleAssemblyVersionFreshness {
    param(
        [string]$NemerleDllPath,
        [string]$RepoRoot,
        [string]$Label = "Stage1",
        [switch]$WarnOnly
    )

    $actual = [System.Reflection.AssemblyName]::GetAssemblyName($NemerleDllPath).Version.ToString()
    $pin = Get-NemerleVersionPin -RepoRoot $RepoRoot
    $expected = $pin.AssemblyVersion

    if ($actual -eq $expected) {
        Write-Host "$Label assembly-version check OK ($actual, pinned by $($pin.Path))."
        return
    }

    $message = @"
$Label assembly-version mismatch: '$NemerleDllPath' is $actual, but this checkout builds at $expected (pinned by $($pin.Path)).
The two are from different version.txt spans. Loading a freshly built assembly of the same simple
name at a different version into this compiler process will fail with a ref-def mismatch
FileLoadException ("manifest definition does not match") -- see dotnet-port\14-pdb-log.md F4 and
dotnet-port\30-devenv2-wp-m1-log.md:191-197.
Two ways this happens, with different fixes:
  - version.txt was bumped but this binary predates the bump: rebuild it for the new version. For
    a seed compiler that means the Windows/CLR4 bump ritual in dotnet-port\44-prerelease-wp-n7-log.md
    section 7.4; for a stage/lib output, just re-run the build script that produces it.
  - This binary belongs to a newer span than version.txt names: version.txt was reverted or edited
    by hand. Restore it to $actual, or rebuild the binary at $expected.
"@

    if ($WarnOnly) {
        Write-Warning $message
    }
    else {
        throw $message
    }
}

# WP-N7 (case 1): the staleness half of the old A2 check, rebuilt on provenance commits.
#
# Pinning deliberately traded away the loader's enforcement: within one version.txt span every
# binary has the same AssemblyVersion, so Test-NemerleAssemblyVersionFreshness above can no
# longer tell "built from HEAD" from "built ten commits ago". That was accepted (44 section 4-1),
# on the condition that the detection move to the provenance JSON files, which record the commit
# each binary was actually built from. This is that check.
#
# What it asks: is $RecordedCommit an ancestor of HEAD? Two outcomes matter --
#   - Not an ancestor: the binary comes from a commit that is not in this checkout's history at
#     all (a different branch, a rewritten history, a dropped commit). Its sources are NOT the
#     ones being built, which is worth stopping for.
#   - An ancestor, but behind: normal and expected. A seed is usually older than HEAD; that is
#     the entire point of pinning. Reported as an informational line with the distance, never a
#     failure.
# Unknown commits (shallow clone, unfetched history) warn and skip rather than fail: CI clones
# and source archives legitimately lack the history, and refusing to build there would punish
# the environment, not the mistake.
function Test-NemerleProvenanceCommit {
    param(
        [string]$RecordedCommit,
        [string]$RepoRoot,
        [string]$Label = "Seed",
        [switch]$WarnOnly
    )

    if ([string]::IsNullOrWhiteSpace($RecordedCommit)) {
        Write-Warning "$Label provenance check skipped: no commit recorded."
        return
    }

    & git -C $RepoRoot cat-file -e "$RecordedCommit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$Label provenance check skipped: commit $RecordedCommit is not present in this checkout (a shallow clone or source archive will not have it). Run 'git fetch --unshallow' if you want this verified."
        return
    }

    & git -C $RepoRoot merge-base --is-ancestor $RecordedCommit HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        $behind = (& git -C $RepoRoot rev-list --count "$RecordedCommit..HEAD").Trim()
        if ($behind -eq "0") {
            Write-Host "$Label provenance OK: built from HEAD ($RecordedCommit)."
        }
        else {
            Write-Host "$Label provenance OK: built from $RecordedCommit, $behind commit(s) behind HEAD (expected -- the version pin makes this buildable)."
        }
        return
    }

    $message = @"
$Label was built from commit $RecordedCommit, which is NOT an ancestor of HEAD in this checkout.
Its sources are not the ones being built here -- it comes from a different branch, a rewritten
history, or a commit that never landed. The assembly-version check cannot catch this: version.txt
pins every binary in a span to the same version, so provenance commits are what distinguish them
(dotnet-port\44-prerelease-wp-n7-log.md section 8.3).
Either check out a history that contains $RecordedCommit, or rebuild the artifact here.
"@

    if ($WarnOnly) {
        Write-Warning $message
    }
    else {
        throw $message
    }
}
