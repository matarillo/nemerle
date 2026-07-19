# WP-N1 (A2): mechanizes the "Stage1/compiler Nemerle.dll is stale relative to HEAD"
# hazard that was previously only a header-comment warning in build-stage2-core.ps1.
#
# Background: assembly versions come from the `GeneratedAssemblyVersion("$GitTag.0.$GitRevision")`
# macro (macros\GeneratedAssemblyVersion.n), which runs `git describe --tags --long` AT COMPILE
# TIME and bakes "{tag digits/dots}.0.{commits-since-tag}" into the built assembly (falling back
# to 1.2.0.9999 if git fails). Stage1 (bin\Release\net-4.0\Stage1) is built incrementally, so its
# embedded version does NOT automatically track HEAD as commits land; if a Stage2 (or later) build
# loads a freshly built Nemerle.dll (same simple name, newer version) into a Stage1 compiler
# process that was built from an older commit, the CLR throws a ref-def mismatch
# FileLoadException ("manifest definition does not match") -- see dotnet-port\14-pdb-log.md F4
# and dotnet-port\30-devenv2-wp-m1-log.md:191-197 for the failure mode.
#
# This file is NOT meant to be executed directly -- it is a dot-source-only library of two
# functions, consumed by build-stage2-core.ps1 / pack-tool.ps1 / pack-release.ps1 (WP-N1) and
# intended for reuse by WP-N6 (CI detection of a stale *checked-in* stage layout, via
# -WarnOnly so CI can report without failing the build outright). Dot-source it like:
#   . "$PSScriptRoot\assembly-version-check.ps1"
#   Test-NemerleAssemblyVersionFreshness -NemerleDllPath <path> -RepoRoot $RepoRoot -Label "Stage1"

# Computes the assembly version HEAD *should* produce, by replaying the same
# `git describe --tags --long` + tag-digit-stripping recipe the GeneratedAssemblyVersion macro
# applies at compile time. Returns $null (meaning "skip the check, nothing to compare against")
# whenever git is unavailable, the repo has no tags yet, or the tag reduces to nothing/"." --
# all cases where the macro itself would fall back to its hard-coded default (1.2.0.9999) rather
# than a real per-commit version, so there is no meaningful "expected version" to enforce.
function Get-ExpectedNemerleAssemblyVersion {
    param(
        [string]$RepoRoot
    )

    # WP-N4 tag contract: --match "v[0-9]*" mirrors the macro exactly -- release/seed tags
    # (deliberately not v-prefixed) must stay invisible to the version computation.
    $describeOutput = & git -C $RepoRoot describe --tags --long --match 'v[0-9]*' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($describeOutput)) { return $null }
    $describeOutput = $describeOutput.Trim()

    # Stricter than the macro's own `(?<tag>.+)-(?<rev>.+)-(?<commit>.+)` (GitRevisionHelper in
    # macros\GeneratedAssemblyVersion.n): anchored, and rev/commit are constrained to their known
    # shapes (digits / "g" + hex) so a tag name that itself contains "-" doesn't get misparsed --
    # the trailing "-<digits>-g<hex>" suffix `git describe --long` always appends is unambiguous.
    if ($describeOutput -notmatch '^(?<tag>.+)-(?<rev>[0-9]+)-(?<commit>g[0-9a-f]+)$') { return $null }
    $tag = $Matches['tag']
    $rev = $Matches['rev']

    # Same stripping rule as GitRevisionHelper.loop: `Regex.Replace(tag, @"[^\d\.]", "")`.
    $tagNumeric = [regex]::Replace($tag, '[^\d\.]', '')
    if ([string]::IsNullOrEmpty($tagNumeric) -or $tagNumeric -eq '.') { return $null }

    return "$tagNumeric.0.$rev"
}

# Compares a built Nemerle.dll's embedded AssemblyVersion against what HEAD currently expects
# (via Get-ExpectedNemerleAssemblyVersion). $Label identifies the artifact in messages (e.g.
# "Stage1", "Compiler (bin\...\ncc.exe)", "CompilerDir (...)").
#
# - If the expected version cannot be determined (git unavailable/no tags/degenerate tag), the
#   check is skipped (Write-Host only) -- there is nothing to compare against.
# - If versions match, prints a short OK line.
# - If they differ, reports both versions plus the known recovery procedure (30-devenv2-wp-m1-log.md
#   :246-254): remove Stage1, rebuild it via the CLR4 msbuild, then refresh-stage1-core.ps1.
#   By default this is a hard failure (throw), matching how build-stage2-core.ps1/pack-tool.ps1/
#   pack-release.ps1 already fail fast on other precondition problems. Pass -WarnOnly to downgrade
#   to Write-Warning and continue -- the shape WP-N6 needs to *report* a stale checked-in stage
#   layout in CI without aborting whatever job is doing the reporting.
function Test-NemerleAssemblyVersionFreshness {
    param(
        [string]$NemerleDllPath,
        [string]$RepoRoot,
        [string]$Label = "Stage1",
        [switch]$WarnOnly
    )

    $actual = [System.Reflection.AssemblyName]::GetAssemblyName($NemerleDllPath).Version.ToString()
    $expected = Get-ExpectedNemerleAssemblyVersion -RepoRoot $RepoRoot

    if ($null -eq $expected) {
        Write-Host "$Label assembly-version freshness check skipped (could not determine an expected version from 'git describe --tags --long' at $RepoRoot -- no tags reachable, or git unavailable)."
        return
    }

    if ($actual -eq $expected) {
        Write-Host "$Label assembly-version freshness OK ($actual)."
        return
    }

    $message = @"
$Label assembly-version mismatch: '$NemerleDllPath' is $actual, but HEAD expects $expected (from 'git describe --tags --long' at $RepoRoot).
This means $Label was built from an older commit than HEAD. Loading a freshly built assembly of
the same simple name (newer version) into this compiler process will fail with a ref-def mismatch
FileLoadException ("manifest definition does not match") -- see dotnet-port\14-pdb-log.md F4 and
dotnet-port\30-devenv2-wp-m1-log.md:191-197.
Recover with a full Stage1 rebuild (dotnet-port\30-devenv2-wp-m1-log.md:246-254):
  Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
  & "`$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
  pwsh dotnet-port/refresh-stage1-core.ps1
"@

    if ($WarnOnly) {
        Write-Warning $message
    }
    else {
        throw $message
    }
}
