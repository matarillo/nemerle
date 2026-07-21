# WP-N7 (case 1): reads the pinned assembly version base from the repo-root version.txt and
# exports it to ncc as the GitTag / GitRevision environment variables.
#
# Background: assembly versions come from the `GeneratedAssemblyVersion("$GitTag.0.$GitRevision")`
# macro (macros\AssemblyInfo.n). ExpandEnv resolves those two variables in the order
# environment -> `git describe --tags --long` -> hard-coded default (macros\ExpandEnv.n
# evaluateVar), so setting the environment variables overrides the describe recipe WITHOUT
# touching any shared source -- which is what makes this pin free of the usual Stage-rebuild
# hazard (dotnet-port\44-prerelease-wp-n7-log.md section 7.3).
#
# Consequence, and the whole point: AssemblyVersion no longer advances per commit, so a seed
# compiler can build any commit within the same version.txt span. Without the pin, CoreCLR's
# loader rejects the version-mismatched Nemerle.dll a cross-generation build produces
# ("manifest definition does not match", 44 section 7.1) -- the reason the boot seed previously
# had to build its own generation's sources in a pinned worktree.
#
# Scope of the pin: only builds that go through these scripts. A plain `msbuild` / `dotnet build`
# does not dot-source this file and therefore still gets describe-derived versions. That is the
# accepted boundary (44 section 7.3, PO Q1) -- the contract is "the release path is pinned".
#
# This file is NOT meant to be executed directly -- it is a dot-source-only library, used like:
#   . "$PSScriptRoot\version-pin.ps1"
#   Set-NemerleVersionPin -RepoRoot $RepoRoot

# Reads and validates version.txt, returning its parts. Throws when the file is missing or
# malformed: in the pinned world there is no meaningful fallback -- silently reverting to
# `git describe` would reintroduce exactly the per-commit drift the pin exists to remove, and
# would do so invisibly, stamping a wrong version into shipped assemblies.
function Get-NemerleVersionPin {
    param(
        [string]$RepoRoot
    )

    $path = Join-Path $RepoRoot "version.txt"
    if (-not (Test-Path $path)) {
        throw "Pinned version file not found: $path. The dotnet-port release-path scripts require it (WP-N7 case 1); it holds the '<GitTag>.<GitRevision>' base, e.g. '1.2.635'."
    }

    # First line that is neither blank nor a '#' comment -- version.txt carries an explanatory
    # header for whoever opens it looking for the bump procedure.
    $value = Get-Content -Path $path |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne "" -and -not $_.StartsWith("#") } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Pinned version file '$path' contains no version line (only blanks/comments). Expected a single '<GitTag>.<GitRevision>' line, e.g. '1.2.635'."
    }

    # <tag>.<revision>, where the tag part mirrors what the macro's tag-stripping rule
    # (Regex.Replace(tag, '[^\d\.]', '')) would leave of an upstream v-tag: digits and dots only.
    if ($value -notmatch '^(?<tag>\d+(\.\d+)*)\.(?<rev>\d+)$') {
        throw "Pinned version '$value' in '$path' is malformed. Expected '<GitTag>.<GitRevision>' with digits and dots only, e.g. '1.2.635'."
    }

    [PSCustomObject]@{
        Path            = $path
        Base            = $value                                  # 1.2.635
        GitTag          = $Matches['tag']                         # 1.2
        GitRevision     = $Matches['rev']                         # 635
        AssemblyVersion = "$($Matches['tag']).0.$($Matches['rev'])"  # 1.2.0.635 -- what ncc stamps
    }
}

# Exports the pin into the current process's environment, so every ncc child process started
# afterwards inherits it. Returns the pin object so callers can report/compare against it.
function Set-NemerleVersionPin {
    param(
        [string]$RepoRoot,
        [switch]$Quiet
    )

    $pin = Get-NemerleVersionPin -RepoRoot $RepoRoot
    $env:GitTag      = $pin.GitTag
    $env:GitRevision = $pin.GitRevision

    if (-not $Quiet) {
        Write-Host "Version pin: $($pin.AssemblyVersion) (GitTag=$($pin.GitTag) GitRevision=$($pin.GitRevision), from $($pin.Path))"
    }
    return $pin
}
