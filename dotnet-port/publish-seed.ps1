# Refreshes the checked-in "stage1 seed" in dotnet-port/seed/ -- a Windows-built, net-4.0-flavor
# ncc.exe plus the companion assemblies it loads, all runnable on CoreCLR via `dotnet exec`. This
# lets someone who cloned the repo on Linux (no .NET Framework, so Stage1 itself cannot be built
# there) run the whole dotnet-based build chain from a plain clone. See
# dotnet-port/build-from-boot.ps1, the consuming half of this mechanism.
#
# WP-N7 (case 1) replaced the old orphan-branch mechanism with this one. The seed used to be
# committed to the orphan branch `boot-net10`, because assembly versions came from `git describe`
# and a seed commit on main would advance it -- making the seed one generation stale the instant
# it was committed (the "+1 paradox"). With the version pinned by version.txt
# (dotnet-port/version-pin.ps1), a commit no longer moves the version, so the seed can simply live
# in the tree. See dotnet-port/44-prerelease-wp-n7-log.md sections 7 and 8.
#
# What "seed" means: exactly the 6 files a Stage1 compiler directory needs to run via
# `dotnet exec` (ncc.exe, its runtimeconfig.json, and the 4 assemblies it loads: Nemerle.dll,
# Nemerle.Compiler.dll, Nemerle.Macros.dll, Nemerle.CoreEmit.dll), plus a generated
# seed-info.json recording the pinned version, the commit/describe this seed was built from, and
# a SHA256 of each file (so the consumer can detect a corrupted checkout).
#
# When to run: only when the seed must change -- i.e. after a version.txt bump, or when the
# compiler itself changed enough that the seed should be advanced. Day-to-day commits do NOT
# need a seed refresh, which is the whole point of pinning.
#
# Preconditions checked below (each throws with a recovery hint on failure):
#   - the working tree must be clean (-AllowDirty overrides, but then seed-info.json's provenance
#     describes a tree nobody else can reproduce).
#   - the 6 seed files must already exist in -Stage1Dir (default bin/Release/net-4.0/Stage1) --
#     built by the CLR4 msbuild Stage1 target plus dotnet-port/refresh-stage1-core.ps1.
#   - the seed's own Nemerle.dll must carry version.txt's pinned version. NB: the CLR4 msbuild
#     build does not read version.txt, so export the pin before building Stage1:
#       . dotnet-port/version-pin.ps1 ; Set-NemerleVersionPin -RepoRoot .
#     then run the msbuild Stage1 target in that same shell.
#   - a smoke test (compile and run a trivial hello.n through the seed) must pass.
#
# Usage:
#   pwsh dotnet-port/publish-seed.ps1                 # refresh dotnet-port/seed/ and stage it
#   pwsh dotnet-port/publish-seed.ps1 -AllowDirty     # skip the clean-tree precondition
#
# This script never commits and never pushes -- it leaves the refreshed seed staged for review.

param(
    [string]$Stage1Dir = "",
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$SeedDir = Join-Path $PSScriptRoot "seed"
if ($Stage1Dir -eq "") { $Stage1Dir = Join-Path $RepoRoot "bin/Release/net-4.0/Stage1" }

# ---------------------------------------------------------------------------
# 1. Clean tree -- see header for why.
# ---------------------------------------------------------------------------
# --match 'v[0-9]*' mirrors the macro (macros/GeneratedAssemblyVersion.n): release/seed tags are
# deliberately not v-prefixed and must stay invisible to describe. Since WP-N7 this value is
# provenance only -- it no longer determines the version anything is stamped with.
$describe = (& git -C $RepoRoot describe --tags --long --dirty --match 'v[0-9]*').Trim()
if ($LASTEXITCODE -ne 0) { throw "'git describe --tags --long --dirty --match ''v[0-9]*''' failed in $RepoRoot (exit $LASTEXITCODE) -- is this a git checkout with at least one tag reachable from HEAD?" }
if ($describe -match '-dirty$' -and -not $AllowDirty) {
    throw "Working tree is dirty ($describe). seed-info.json would then record a commit nobody else can reproduce. Commit or stash your changes first, or pass -AllowDirty for a throwaway seed."
}

# ---------------------------------------------------------------------------
# 2. The 6 seed files must already exist.
# ---------------------------------------------------------------------------
$SeedFiles = @("ncc.exe", "ncc.runtimeconfig.json", "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "Nemerle.CoreEmit.dll")
foreach ($name in $SeedFiles) {
    if (-not (Test-Path (Join-Path $Stage1Dir $name))) {
        $hint = if ($name -eq "Nemerle.CoreEmit.dll") { " Run 'pwsh dotnet-port/refresh-stage1-core.ps1' to add it (and the runtimeconfig.json Stage1 needs on CoreCLR)." } else { "" }
        throw "Missing '$name' in $Stage1Dir -- not a complete Stage1 compiler directory.$hint"
    }
}

# ---------------------------------------------------------------------------
# 3. The seed must carry version.txt's pinned version -- a seed at any other version cannot build
#    this tree at all (the CoreCLR loader rejects the mismatch). Hard fail, no -WarnOnly.
# ---------------------------------------------------------------------------
. "$PSScriptRoot/version-pin.ps1"
$pin = Get-NemerleVersionPin -RepoRoot $RepoRoot
. "$PSScriptRoot/assembly-version-check.ps1"
Test-NemerleAssemblyVersionFreshness -NemerleDllPath (Join-Path $Stage1Dir "Nemerle.dll") -RepoRoot $RepoRoot -Label "Stage1 ($Stage1Dir)"

# ---------------------------------------------------------------------------
# 4. Smoke test: compile and run a trivial program through the seed via `dotnet exec`, exactly
#    the way a consumer will use it (build-from-boot.ps1's first build step).
# ---------------------------------------------------------------------------
$SmokeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("nemerle-seed-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $SmokeDir | Out-Null
try {
    $HelloSrc = Join-Path $SmokeDir "hello.n"
    @'
using System.Console;

module HelloSeed
{
  Main() : void
  {
    WriteLine("seed smoke test OK");
  }
}
'@ | Set-Content -Path $HelloSrc -Encoding utf8

    $HelloExe = Join-Path $SmokeDir "hello.exe"
    $NccExe = Join-Path $Stage1Dir "ncc.exe"
    Write-Host "Smoke test: compiling hello.n through $NccExe ..."
    & dotnet exec $NccExe "-out:$HelloExe" $HelloSrc
    if ($LASTEXITCODE -ne 0) { throw "Smoke test FAILED: '$NccExe' could not compile $HelloSrc (exit $LASTEXITCODE)" }

    # Ordinary Nemerle programs reference Nemerle.dll at run time (see pack-tool.ps1's header for
    # the same gap in the packaged layout) -- simplest to copy it next to the smoke-test binary.
    Copy-Item -Path (Join-Path $Stage1Dir "Nemerle.dll") -Destination $SmokeDir -Force

    Write-Host "Smoke test: running hello.exe ..."
    $output = & dotnet exec $HelloExe
    if ($LASTEXITCODE -ne 0) { throw "Smoke test FAILED: hello.exe exited $LASTEXITCODE" }
    if ($output -notmatch 'seed smoke test OK') { throw "Smoke test FAILED: unexpected output from hello.exe: $output" }
    Write-Host "Smoke test OK."
}
finally {
    Remove-Item -Recurse -Force $SmokeDir -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 5. Replace dotnet-port/seed/ with the new files + seed-info.json.
# ---------------------------------------------------------------------------
$commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
$nemerleAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $Stage1Dir "Nemerle.dll")).Version.ToString()

if (Test-Path $SeedDir) { Remove-Item -Recurse -Force $SeedDir }
New-Item -ItemType Directory -Force -Path $SeedDir | Out-Null

$fileHashes = [ordered]@{}
foreach ($name in $SeedFiles) {
    Copy-Item -Path (Join-Path $Stage1Dir $name) -Destination (Join-Path $SeedDir $name) -Force
    $fileHashes[$name] = (Get-FileHash -Path (Join-Path $SeedDir $name) -Algorithm SHA256).Hash.ToLowerInvariant()
}

$seedInfo = [ordered]@{
    schema        = 2
    flavor        = "stage1 (net-4.0 flavor, runs on CoreCLR via dotnet exec)"
    pinnedVersion = $pin.Base
    generation    = [ordered]@{
        commit                 = $commit
        describe               = $describe
        nemerleAssemblyVersion = $nemerleAssemblyVersion
    }
    builtOnUtc    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    files         = $fileHashes
}
$seedInfo | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $SeedDir "seed-info.json") -Encoding utf8

& git -C $RepoRoot add -- $SeedDir
if ($LASTEXITCODE -ne 0) { throw "'git add -- $SeedDir' failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Seed refreshed -> $SeedDir (pinned $($pin.Base), from $($commit.Substring(0, 9)))"
Write-Host "Staged, not committed. Review, then:"
Write-Host "  git commit -m `"Refresh stage1 seed to $($pin.Base) from $($commit.Substring(0, 9))`""
Write-Host ""
Write-Host "The seed stays valid for that commit: pinning means the commit you make now does not"
Write-Host "move the assembly version, so the seed is not stale the moment it lands."
