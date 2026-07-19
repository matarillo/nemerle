# Publishes a "stage1 seed" -- a Windows-built, net-4.0-flavor ncc.exe plus the companion
# assemblies it loads, all runnable on CoreCLR via `dotnet exec` -- to the orphan `boot-net10`
# branch. This lets someone who cloned the repo on Linux (no .NET Framework, so Stage1 itself
# cannot be built there) fetch the seed and run the rest of the existing dotnet-based build
# chain from it. See dotnet-port/build-from-boot.ps1, the consuming half of this mechanism.
#
# Why an orphan branch: a commit on `boot-net10` does NOT touch main's history, so it does not
# move `git describe --tags --long` -- and GeneratedAssemblyVersion.n (macros/GeneratedAssembly
# Version.n) bakes that describe output into every Nemerle assembly at compile time (see
# dotnet-port/assembly-version-check.ps1's header for the full mechanism). A seed commit that
# advanced describe would silently invalidate the very version contract this script exists to
# preserve, so `boot-net10` is deliberately disconnected from main's commit graph.
#
# What "seed" means: exactly the 6 files a Stage1 compiler directory needs to run via
# `dotnet exec` (ncc.exe, its runtimeconfig.json, and the 4 assemblies it loads: Nemerle.dll,
# Nemerle.Compiler.dll, Nemerle.Macros.dll, Nemerle.CoreEmit.dll), plus a generated
# boot-info.json recording the commit/describe/assembly-version this seed was built from and a
# SHA256 of each file (so the consumer can detect a corrupted or truncated fetch).
#
# Preconditions checked below (each throws with a recovery hint on failure):
#   - main's working tree must be clean (`git describe --tags --long --dirty` must not end in
#     "-dirty") -- -AllowDirty overrides this, but then boot-info.json's provenance describes a
#     tree nobody else can reproduce.
#   - the 6 seed files must already exist in -Stage1Dir (default bin/Release/net-4.0/Stage1) --
#     built by the CLR4 msbuild Stage1 target plus dotnet-port/refresh-stage1-core.ps1 (that
#     script adds Nemerle.CoreEmit.dll and the runtimeconfig.json Stage1 needs on its own).
#   - the seed's own Nemerle.dll must be fresh relative to HEAD (the A2 check from
#     dotnet-port/assembly-version-check.ps1) -- a stale seed would reproduce, one hop later at
#     the consumer's site, exactly the ref-def mismatch FileLoadException that check exists to
#     catch early.
#   - a smoke test (compile and run a trivial hello.n through the seed) must pass.
#
# Usage:
#   pwsh dotnet-port/publish-boot.ps1                # seed -> boot-net10 (local commit only)
#   pwsh dotnet-port/publish-boot.ps1 -AllowDirty     # skip the clean-tree precondition
#
# This script never pushes. Inspect the new commit on the orphan branch, then push it yourself:
#   git push origin boot-net10

param(
    [string]$Stage1Dir = "",
    [string]$Branch = "boot-net10",
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($Stage1Dir -eq "") { $Stage1Dir = Join-Path $RepoRoot "bin/Release/net-4.0/Stage1" }

# ---------------------------------------------------------------------------
# 1. main must be clean -- see header for why.
# ---------------------------------------------------------------------------
# Same recipe as the macro (macros\GeneratedAssemblyVersion.n) / assembly-version-check.ps1 --
# --match 'v[0-9]*' keeps this seed's own release/seed tags (if any are reachable) invisible.
$describe = (& git -C $RepoRoot describe --tags --long --dirty --match 'v[0-9]*').Trim()
if ($LASTEXITCODE -ne 0) { throw "'git describe --tags --long --dirty --match ''v[0-9]*''' failed in $RepoRoot (exit $LASTEXITCODE) -- is this a git checkout with at least one tag reachable from HEAD?" }
if ($describe -match '-dirty$' -and -not $AllowDirty) {
    throw "Working tree is dirty ($describe). The seed's boot-info.json would then record a commit nobody else can reproduce. Commit or stash your changes first, or pass -AllowDirty for a throwaway seed."
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
# 3. A2 freshness check (hard fail, no -WarnOnly): the seed must match HEAD's generation.
# ---------------------------------------------------------------------------
. "$PSScriptRoot/assembly-version-check.ps1"
Test-NemerleAssemblyVersionFreshness -NemerleDllPath (Join-Path $Stage1Dir "Nemerle.dll") -RepoRoot $RepoRoot -Label "Stage1 ($Stage1Dir)"

# ---------------------------------------------------------------------------
# 4. Smoke test: compile and run a trivial program through the seed via `dotnet exec`,
#    exactly the way a consumer will use it (build-from-boot.ps1 step 5's first build step).
# ---------------------------------------------------------------------------
$SmokeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("nemerle-boot-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $SmokeDir | Out-Null
try {
    $HelloSrc = Join-Path $SmokeDir "hello.n"
    @'
using System.Console;

module HelloBoot
{
  Main() : void
  {
    WriteLine("boot-net10 smoke test OK");
  }
}
'@ | Set-Content -Path $HelloSrc -Encoding utf8

    $HelloExe = Join-Path $SmokeDir "hello.exe"
    $NccExe = Join-Path $Stage1Dir "ncc.exe"
    Write-Host "Smoke test: compiling hello.n through $NccExe ..."
    & dotnet exec $NccExe "-out:$HelloExe" $HelloSrc
    if ($LASTEXITCODE -ne 0) { throw "Smoke test FAILED: '$NccExe' could not compile $HelloSrc (exit $LASTEXITCODE)" }

    # Ordinary Nemerle programs reference Nemerle.dll at run time (see pack-tool.ps1's header
    # for the same gap in the packaged layout) -- ncc.cmd handles this for consumers, but here
    # it is simplest to just copy it alongside the smoke-test binary.
    Copy-Item -Path (Join-Path $Stage1Dir "Nemerle.dll") -Destination $SmokeDir -Force

    Write-Host "Smoke test: running hello.exe ..."
    $output = & dotnet exec $HelloExe
    if ($LASTEXITCODE -ne 0) { throw "Smoke test FAILED: hello.exe exited $LASTEXITCODE" }
    if ($output -notmatch 'boot-net10 smoke test OK') { throw "Smoke test FAILED: unexpected output from hello.exe: $output" }
    Write-Host "Smoke test OK."
}
finally {
    Remove-Item -Recurse -Force $SmokeDir -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 5. boot-info.json: the generation this seed was built from, plus a SHA256 of every seed file
#    so a consumer can verify the fetched seed byte-for-byte (build-from-boot.ps1 step 3).
# ---------------------------------------------------------------------------
$commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
$nemerleAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $Stage1Dir "Nemerle.dll")).Version.ToString()

$fileHashes = [ordered]@{}
foreach ($name in $SeedFiles) {
    $fileHashes[$name] = (Get-FileHash -Path (Join-Path $Stage1Dir $name) -Algorithm SHA256).Hash.ToLowerInvariant()
}

$bootInfo = [ordered]@{
    schema     = 1
    flavor     = "stage1 (net-4.0 flavor, runs on CoreCLR via dotnet exec)"
    generation = [ordered]@{
        commit                 = $commit
        describe               = $describe
        nemerleAssemblyVersion = $nemerleAssemblyVersion
    }
    builtOnUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    files      = $fileHashes
}

# ---------------------------------------------------------------------------
# 6. Stage the seed files + boot-info.json, then commit them to the orphan branch via a
#    throwaway worktree -- this never touches main's own working tree or index.
# ---------------------------------------------------------------------------
$StageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("nemerle-boot-stage-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
try {
    foreach ($name in $SeedFiles) {
        Copy-Item -Path (Join-Path $Stage1Dir $name) -Destination (Join-Path $StageDir $name) -Force
    }
    $bootInfo | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $StageDir "boot-info.json") -Encoding utf8

    $WorktreeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("nemerle-boot-worktree-" + [guid]::NewGuid().ToString("N"))

    & git -C $RepoRoot rev-parse --verify --quiet "refs/heads/$Branch" | Out-Null
    $branchExists = ($LASTEXITCODE -eq 0)

    # WP-N4 follow-up: a checkout that only has the remote-tracking ref (the shape any fresh
    # clone leaves behind) must NOT fall into the create-a-new-orphan-root path below -- that
    # would silently start a second, disconnected history whose push force-overwrites (and
    # thus discards) every previously published seed commit. Recreate the local branch from
    # origin's instead, so the new seed commit lands on top of the published history.
    if (-not $branchExists) {
        & git -C $RepoRoot rev-parse --verify --quiet "refs/remotes/origin/$Branch" | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Local branch '$Branch' is missing but 'origin/$Branch' exists; creating the local branch from it."
            & git -C $RepoRoot branch $Branch "origin/$Branch"
            if ($LASTEXITCODE -ne 0) { throw "'git branch $Branch origin/$Branch' failed (exit $LASTEXITCODE)" }
            $branchExists = $true
        }
    }

    try {
        if ($branchExists) {
            Write-Host "Branch '$Branch' already exists; checking it out into a worktree ..."
            & git -C $RepoRoot worktree add $WorktreeDir $Branch
            if ($LASTEXITCODE -ne 0) { throw "'git worktree add $WorktreeDir $Branch' failed (exit $LASTEXITCODE) -- is '$Branch' already checked out somewhere else (e.g. accidentally on main)?" }
            Get-ChildItem -Path $WorktreeDir -Force | Where-Object { $_.Name -ne ".git" } | Remove-Item -Recurse -Force
        }
        else {
            Write-Host "Branch '$Branch' does not exist yet; creating it as an orphan branch ..."
            & git -C $RepoRoot worktree add --detach $WorktreeDir
            if ($LASTEXITCODE -ne 0) { throw "'git worktree add --detach $WorktreeDir' failed (exit $LASTEXITCODE)" }
            & git -C $WorktreeDir checkout --orphan $Branch
            if ($LASTEXITCODE -ne 0) { throw "'git checkout --orphan $Branch' failed in $WorktreeDir (exit $LASTEXITCODE)" }
            # Empties the working tree/index inherited from the detached checkout. If there is
            # nothing to remove (rare), `git rm` exits non-zero with "pathspec did not match any
            # files" -- harmless here, so the failure is swallowed rather than checked.
            & git -C $WorktreeDir rm -rf --quiet . 2>$null
        }

        Copy-Item -Path (Join-Path $StageDir "*") -Destination $WorktreeDir -Force
        & git -C $WorktreeDir add -A
        if ($LASTEXITCODE -ne 0) { throw "'git add -A' failed in $WorktreeDir (exit $LASTEXITCODE)" }

        $shortCommit = $commit.Substring(0, 9)
        $commitMessage = "boot-net10 seed $nemerleAssemblyVersion from $shortCommit"
        & git -C $WorktreeDir commit -m $commitMessage
        if ($LASTEXITCODE -ne 0) { throw "'git commit' failed in $WorktreeDir (exit $LASTEXITCODE)" }
        $seedCommitSha = (& git -C $WorktreeDir rev-parse HEAD).Trim()

        Write-Host ""
        Write-Host "Committed to '$Branch': $commitMessage"
    }
    finally {
        & git -C $RepoRoot worktree remove --force $WorktreeDir 2>$null
        & git -C $RepoRoot worktree prune 2>$null
        if (Test-Path $WorktreeDir) { Remove-Item -Recurse -Force $WorktreeDir -ErrorAction SilentlyContinue }
    }
}
finally {
    Remove-Item -Recurse -Force $StageDir -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 7. WP-N4: tag the new seed commit `seed/<base>` so build-from-boot.ps1's -Seed/-ReleaseTag and
#    pack-release.ps1's release<->seed record can name this generation directly instead of only
#    "whatever boot-net10's tip happens to be". Lightweight (no -a/-m) is part of the tag
#    contract: an annotated tag here could otherwise be picked up by the annotated-only
#    `git describe` provenance calls in pack-tool.ps1/pack-server.ps1/pack-release.ps1.
# ---------------------------------------------------------------------------
$seedTagName = $null
if ($nemerleAssemblyVersion -match '^(\d+)\.(\d+)\.0\.(\d+)$') {
    $seedBase = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
    $candidateTagName = "seed/$seedBase"
    $seedTagRef = "refs/tags/$candidateTagName"

    $existingSeedTagCommit = & git -C $RepoRoot rev-parse --verify --quiet $seedTagRef 2>$null
    if ($LASTEXITCODE -eq 0) {
        $existingSeedTagCommit = $existingSeedTagCommit.Trim()
        if ($existingSeedTagCommit -eq $seedCommitSha) {
            Write-Host "'$candidateTagName' already tagged at $seedCommitSha."
            $seedTagName = $candidateTagName
        }
        else {
            throw "'$candidateTagName' already exists but points at $existingSeedTagCommit, not the new seed commit $seedCommitSha. This looks like a re-publish of generation $seedBase with a different seed commit. Remove the stale tag by hand first if that is really what you want, then re-run: git tag -d $candidateTagName"
        }
    }
    else {
        & git -C $RepoRoot tag $candidateTagName $seedCommitSha
        if ($LASTEXITCODE -ne 0) { throw "'git tag $candidateTagName $seedCommitSha' failed (exit $LASTEXITCODE)" }
        Write-Host "Tagged '$candidateTagName' -> $seedCommitSha"
        $seedTagName = $candidateTagName
    }
}
else {
    Write-Warning "Could not parse a base version ('X.Y.0.Z') out of Nemerle assembly version '$nemerleAssemblyVersion' -- skipping the seed/<base> tag."
}

Write-Host ""
Write-Host "Not pushed. Review the new commit on '$Branch', then:"
if ($seedTagName) {
    Write-Host "  git push origin $Branch $seedTagName"
}
else {
    Write-Host "  git push origin $Branch"
}
