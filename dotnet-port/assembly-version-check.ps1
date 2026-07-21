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
# Per-commit identity moved to the provenance JSON files (boot-info.json / ncc-info.json), which
# record the commit each binary was built from.
#
# This file is NOT meant to be executed directly -- it is a dot-source-only library of two
# functions, consumed by build-stage2-core.ps1 / build-libs-core.ps1 / pack-tool.ps1 /
# pack-release.ps1 / publish-boot.ps1. Dot-source it like:
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
