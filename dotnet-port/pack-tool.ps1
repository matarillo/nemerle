# WP-I2 (task 1): assembles a self-contained, runnable "dotnet ncc" distribution layout
# out of an already-built core-flavor compiler directory (default:
# bin\Release\core\Stage2, produced by dotnet-port\build-stage2-core.ps1).
#
# Problem this solves: the stage2/stage3 output directory already runs fine via
# `dotnet exec <dir>\ncc.exe ...` (verified in 13-stage2-log.md), but `ncc.exe` only works
# with the `exec` verb by convention -- nothing marks it as runnable via the plain
# `dotnet <dll>` shorthand that most modern tooling/docs expect. (A second problem this used
# to solve -- ncc needing a long, hand-maintained pile of `-use-loaded-corlib`/`-ref:<shared-
# framework path>` switches just to see the standard library -- no longer applies: WP-A2 gave
# ncc its own auto-ref resolution on CoreCLR (LoadCoreStdlibReferences, ncc\passes.n), so a
# bare `dotnet <OutDir>\ncc.dll hello.n` compiles with no response file. See D7 below and
# dotnet-port\DISTRIBUTION.md for the history of the response-file wrapper this replaced.)
#
# What this script produces (-OutDir, default dotnet-port\dist\ncc):
#   - A copy of the compiler directory's *.dll + ncc.exe, PLUS a byte-identical `ncc.dll`
#     copy of `ncc.exe` (same base name "ncc" so the existing `ncc.runtimeconfig.json`
#     covers both) -- this is what makes `dotnet <OutDir>\ncc.dll <args>` work exactly
#     like `dotnet exec <OutDir>\ncc.exe <args>` (verified below: the dotnet muxer only
#     cares about the base-name-matched runtimeconfig.json, not the file extension).
#   - `ncc.cmd`: a thin convenience wrapper (`<OutDir>\ncc.cmd -out:hello.exe hello.n`,
#     works from both cmd.exe and a PowerShell prompt) that runs
#     `dotnet <OutDir>\ncc.dll %*` and, on success, additionally copies this layout's
#     Nemerle*.dll into the CURRENT directory -- because ordinary Nemerle programs (anything
#     using the stdlib beyond compile-time-only macros like `printf`) reference `Nemerle.dll`
#     at RUN time, and .NET's assembly probing only looks beside the entry assembly / in the
#     shared framework (no GAC) -- this is the same "scratch-dir artifact" gap documented in
#     12-selfhost-blockers-log.md / 18-testsuite-log.md section 3b, addressed here for the
#     packaged layout the same way run-testsuite-core.ps1 addresses it for the test suite.
#     (Deliberately a .cmd, not a .ps1: verified empirically that PowerShell's own
#     command/script invocation splits or misparses `-name:value`-shaped arguments --
#     `-out:hello.exe` becomes two tokens `-out` / `hello.exe`, or is rejected outright as
#     ambiguous with PowerShell's common `-OutVariable`/`-OutBuffer` parameters -- no matter
#     how the forwarding script declares its parameters ($args, param(),
#     ValueFromRemainingArguments, `--%` all tried and all broken for THIS shape of argument
#     on a .ps1 target). A .cmd's `%*` is untouched by any of that, from cmd.exe *and* when
#     invoked from a PowerShell prompt alike, since PowerShell's parameter binder only kicks
#     in for script/cmdlet targets, not external programs. See dotnet-port\DISTRIBUTION.md
#     for the full writeup.)
#
#   - `ncc-info.json` (WP-M6): provenance for the packed compiler -- commit, `git describe`,
#     configuration, the Nemerle assembly version consumers bind against, and the pack
#     timestamp. Pairs with the language server's server\bundle-info.json so a mixed-generation
#     install (which surfaces as FileLoadException, since Nemerle assembly versions track the
#     source generation) can be diagnosed mechanically. Also shipped inside the
#     Nemerle.Sdk.Unofficial package.
#
# D7 (WP-N1, rsp legacy cleanup): this script used to also write `ncc.default.rsp` (a wrapper
# response file baking in the hand-built `-no-stdlib -use-loaded-corlib -ref:<...>` reference
# list) plus `gen-default-rsp.ps1` (regenerates that rsp's absolute paths after the layout is
# moved/relocated), and ncc.cmd used to prepend `-from-file:ncc.default.rsp` and re-run the
# generator whenever the baked paths looked stale. All of that existed only to reconstruct, by
# hand, the reference set ncc now resolves on its own since WP-A2 -- so both files and the
# staleness-detection logic were removed as dead complexity. Nothing in this repo (ncc itself,
# Nemerle.Tool\Program.cs, Nemerle.Core.targets) generates or reads either file any more.
#
# Usage:
#   pwsh dotnet-port\pack-tool.ps1                          # default Stage2 -> dotnet-port\dist\ncc
#   pwsh dotnet-port\pack-tool.ps1 -CompilerDir bin\Release\core\Stage3 -OutDir dotnet-port\dist\ncc-stage3
#   pwsh dotnet-port\pack-tool.ps1 -Pack                    # ...and the NuGet packages -> dotnet-port\dist\release
#
# This is the single entry point for producing distributable Nemerle toolchain artifacts
# (29-devenv2-plan.md section 10: "keep pack-tool.ps1 as the one entry point"). -Pack adds the
# MSBuild project SDK package (Nemerle.Sdk.Unofficial) and the `dotnet new` templates
# (Nemerle.Templates.Unofficial), versioned from the compiler this script just packed.
#
# Smoke test (from ANY directory):
#   dotnet <OutDir>\ncc.dll -out:hello.exe hello.n
#   copy <OutDir>\Nemerle*.dll .   (only needed if hello.n uses stdlib beyond printf-style macros)
#   dotnet exec hello.exe
# or simply:
#   <OutDir>\ncc.cmd -out:hello.exe hello.n
#   dotnet exec hello.exe

param(
    [string]$Configuration = "Release",
    [string]$CompilerDir = "",   # source compiler directory (default: bin\<Cfg>\core\Stage2)
    [string]$OutDir = "",        # destination layout directory (default: dotnet-port\dist\ncc)

    # WP-M6. -Pack additionally produces the NuGet packages (Nemerle.Sdk.Unofficial +
    # Nemerle.Templates.Unofficial) from the layout this script just built. Kept behind a switch
    # so the default invocation stays the fast "build me a runnable ncc" loop everything else
    # (pack-server.ps1, the samples, the test fixtures) depends on.
    [switch]$Pack,
    [string]$PackageOutDir = "",        # default: dotnet-port\dist\release
    # Prerelease label appended to the version derived from the compiler itself (see section 5).
    # Bump it when re-packing the same compiler with changed packaging/targets: NuGet caches an
    # (id, version) by content, so reusing a version silently serves stale bits.
    [string]$PackageVersionSuffix = "preview.2"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($CompilerDir -eq "") { $CompilerDir = Join-Path $RepoRoot "bin\$Configuration\core\Stage2" }
if ($OutDir      -eq "") { $OutDir      = Join-Path $PSScriptRoot "dist\ncc" }

if (-not (Test-Path $CompilerDir)) { throw "Compiler directory not found: $CompilerDir (build it first, e.g. dotnet-port\build-stage2-core.ps1)" }
foreach ($required in @("ncc.exe", "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "ncc.runtimeconfig.json")) {
    if (-not (Test-Path (Join-Path $CompilerDir $required))) { throw "Missing '$required' in $CompilerDir -- not a complete core compiler directory" }
}

# WP-N1 (A2): the layout this script packs must itself have been built from HEAD's commit --
# see dotnet-port\assembly-version-check.ps1 / build-stage2-core.ps1's design notes for why a
# stale compiler directory (built from an older commit) causes a ref-def mismatch FileLoadException
# rather than a clean, actionable failure.
. "$PSScriptRoot\assembly-version-check.ps1"
Test-NemerleAssemblyVersionFreshness -NemerleDllPath (Join-Path $CompilerDir "Nemerle.dll") -RepoRoot $RepoRoot -Label "CompilerDir ($CompilerDir)"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---------------------------------------------------------------------------
# 1. Copy the compiler directory as-is (dll/exe/runtimeconfig), then add a
#    byte-identical ncc.dll copy of ncc.exe -- the "dotnet <path>\ncc.dll" entry point.
#    (ncc.runtimeconfig.json's base name is "ncc" regardless of extension, so it already
#    covers ncc.dll too -- no separate runtimeconfig needed for the copy.)
# ---------------------------------------------------------------------------
Get-ChildItem -Path $CompilerDir -Filter "*.dll" | Copy-Item -Destination $OutDir -Force
Get-ChildItem -Path $CompilerDir -Filter "*.exe" | Copy-Item -Destination $OutDir -Force
Copy-Item -Path (Join-Path $CompilerDir "ncc.runtimeconfig.json") -Destination $OutDir -Force
Copy-Item -Path (Join-Path $OutDir "ncc.exe") -Destination (Join-Path $OutDir "ncc.dll") -Force

# ---------------------------------------------------------------------------
# 2. Convenience wrapper: `<OutDir>\ncc.cmd -out:foo.exe foo.n [args...]` runs the
#    compiler directly (no response file -- ncc auto-resolves the standard CoreCLR reference
#    set itself since WP-A2's LoadCoreStdlibReferences, ncc\passes.n), then (best-effort)
#    copies this layout's Nemerle*.dll into the CURRENT directory so the produced program can
#    actually run (see header comment -- run-time stdlib dependency, not a compiler bug). A
#    plain .cmd batch file, NOT a .ps1 -- see the header comment for why a PowerShell wrapper
#    cannot reliably forward ncc's `-name:value` switches.
#
#    D7 (WP-N1): this used to prepend `-from-file:ncc.default.rsp` and regenerate that rsp
#    (via a shipped gen-default-rsp.ps1) whenever the layout had moved. Both existed only to
#    reconstruct, by hand, the reference set ncc now resolves on its own -- see
#    dotnet-port\DISTRIBUTION.md's WP-A2 note for the history. Removed here as dead
#    complexity: nothing in this script or Nemerle.Tool\Program.cs generates or reads either
#    file any more.
# ---------------------------------------------------------------------------
$WrapperPath = Join-Path $OutDir "ncc.cmd"
$wrapperContent = @"
@echo off
rem Convenience wrapper generated by dotnet-port\pack-tool.ps1. Runs this directory's
rem ncc.dll directly (auto-resolves its standard reference set on CoreCLR, no response file
rem needed -- see ncc\passes.n's LoadCoreStdlibReferences), then copies Nemerle*.dll into the
rem current directory on success (see pack-tool.ps1 header for why).
setlocal
set "HERE=%~dp0"
dotnet "%HERE%ncc.dll" %*
set "EXITCODE=%ERRORLEVEL%"
if "%EXITCODE%"=="0" copy /y "%HERE%Nemerle*.dll" . >nul 2>&1
exit /b %EXITCODE%
"@
Set-Content -Path $WrapperPath -Value $wrapperContent -Encoding ascii
Write-Host "Wrote $WrapperPath"

# ---------------------------------------------------------------------------
# 3. WP-A3: build the in-process MSBuild task pieces and drop them into the layout.
#    - Nemerle.Compiler.Hosting.dll goes NEXT TO Nemerle.Compiler.dll/Nemerle.dll (Nemerle.
#      MSBuild.Tasks's NccLoadContext.Load() override resolves same-directory siblings into
#      one AssemblyLoadContext, which the Hosting/ManagerClass subclassing requires -- see
#      dotnet-port\Nemerle.Compiler.Hosting\CompilerHost.cs).
#    - Nemerle.MSBuild.Tasks.dll goes into msbuild-task\, the path
#      dotnet-port\msbuild\Nemerle.Core.targets' <UsingTask AssemblyFile="..."> points at by
#      default (NemerleTaskAssembly = $(NccLayoutDir)msbuild-task\Nemerle.MSBuild.Tasks.dll).
#    Built AFTER the layout above exists: Nemerle.Compiler.Hosting.csproj's <Reference
#    HintPath> entries point at $OutDir, so Nemerle.Compiler.dll/Nemerle.dll must already be
#    there (see that project's header comment for why it can't simply ProjectReference them --
#    ncc\*.n itself is never touched by any C# build).
# ---------------------------------------------------------------------------
$OutDirFull = (Resolve-Path $OutDir).Path
$OutDirWithSlash = if ($OutDirFull.EndsWith('\')) { $OutDirFull } else { "$OutDirFull\" }
$DotnetPortDir = $PSScriptRoot
$HostingProj = Join-Path $DotnetPortDir "Nemerle.Compiler.Hosting\Nemerle.Compiler.Hosting.csproj"
$TasksProj   = Join-Path $DotnetPortDir "Nemerle.MSBuild.Tasks\Nemerle.MSBuild.Tasks.csproj"

Write-Host ""
Write-Host "Building Nemerle.Compiler.Hosting (NccLayoutDir=$OutDirWithSlash) ..."
& dotnet build -c $Configuration $HostingProj "-p:NccLayoutDir=$OutDirWithSlash" -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Nemerle.Compiler.Hosting build failed (exit $LASTEXITCODE)" }
$HostingOutDir = Join-Path $DotnetPortDir "Nemerle.Compiler.Hosting\bin\$Configuration\net10.0"
Copy-Item -Path (Join-Path $HostingOutDir "Nemerle.Compiler.Hosting.dll") -Destination $OutDir -Force
$HostingPdb = Join-Path $HostingOutDir "Nemerle.Compiler.Hosting.pdb"
if (Test-Path $HostingPdb) { Copy-Item -Path $HostingPdb -Destination $OutDir -Force }
Write-Host "Wrote $(Join-Path $OutDir 'Nemerle.Compiler.Hosting.dll')"

Write-Host ""
Write-Host "Building Nemerle.MSBuild.Tasks ..."
& dotnet build -c $Configuration $TasksProj -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Nemerle.MSBuild.Tasks build failed (exit $LASTEXITCODE)" }
$TasksOutDir = Join-Path $DotnetPortDir "Nemerle.MSBuild.Tasks\bin\$Configuration\net10.0"
$TaskDestDir = Join-Path $OutDir "msbuild-task"
New-Item -ItemType Directory -Force -Path $TaskDestDir | Out-Null
Copy-Item -Path (Join-Path $TasksOutDir "Nemerle.MSBuild.Tasks.dll") -Destination $TaskDestDir -Force
$TasksPdb = Join-Path $TasksOutDir "Nemerle.MSBuild.Tasks.pdb"
if (Test-Path $TasksPdb) { Copy-Item -Path $TasksPdb -Destination $TaskDestDir -Force }
Write-Host "Wrote $(Join-Path $TaskDestDir 'Nemerle.MSBuild.Tasks.dll')"

# ---------------------------------------------------------------------------
# 4. WP-M6 (29-devenv2-plan.md section 6.8): provenance.
#    Nemerle assembly versions derive from the source generation, so mixing a language server,
#    a dist layout and a Nemerle.Sdk package built from different commits produces
#    FileLoadException at run time (28-vscode-packaging-log.md). The server has recorded its own
#    server\bundle-info.json since WP-L4; this is the other half, so the two can be compared
#    mechanically instead of by memory. It is written INTO the layout, which means the
#    Nemerle.Sdk.Unofficial package carries it too (that package's csproj requires it).
# ---------------------------------------------------------------------------
Push-Location $RepoRoot
try {
    $commit   = (& git rev-parse HEAD).Trim()
    $describe = (& git describe --long --always --dirty).Trim()
}
finally {
    Pop-Location
}
if ($describe -match '-dirty$') {
    Write-Warning "Working tree is dirty; ncc-info.json records '$describe'."
}

# The identity that actually matters for load compatibility: what a consumer will bind against.
$NemerleAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $OutDirFull "Nemerle.dll")).Version.ToString()

$nccInfo = [ordered]@{
    commit                 = $commit
    describe               = $describe
    configuration          = $Configuration
    nemerleAssemblyVersion = $NemerleAssemblyVersion
    sourceDir              = (Resolve-Path $CompilerDir).Path.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/')
    packedAtUtc            = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
$NccInfoPath = Join-Path $OutDir "ncc-info.json"
$nccInfo | ConvertTo-Json | Set-Content -Path $NccInfoPath -Encoding utf8
Write-Host "Wrote $NccInfoPath (commit $($commit.Substring(0,9)), Nemerle $NemerleAssemblyVersion)"

Write-Host ""
Write-Host "Layout complete -> $OutDir"
Get-ChildItem $OutDir | Format-Table Name, Length
Write-Host ""
Write-Host "Smoke test:"
Write-Host "  dotnet `"$OutDir\ncc.dll`" -out:hello.exe hello.n && dotnet exec hello.exe"
Write-Host "  (or)  `"$WrapperPath`" -out:hello.exe hello.n && dotnet exec hello.exe"

# ---------------------------------------------------------------------------
# 5. WP-M6: the NuGet packages (only with -Pack).
#
#    Version scheme: 1.2.<revision> of the layout's OWN Nemerle.dll (1.2.0.601 -> 1.2.601),
#    plus the prerelease label from -PackageVersionSuffix. This continues the numbering of the
#    author's existing nuget.org packages (Nemerle.Unofficial, Nemerle.Compiler.Unofficial,
#    Nemerle.Macros.Unofficial, Nemerle.Compiler.Utils.Unofficial, all 1.2.547 = the official
#    Nemerle 1.2.547 they were unofficial builds of). The version therefore states which
#    compiler is inside, which is exactly what a toolchain-vs-server mismatch turns on; the
#    prerelease label carries maturity, which the package ID deliberately does not (an ID says
#    who built it -- permanently true -- while "preview" stops being true, and renaming a
#    project SDK ID is a breaking change for every consuming .nproj and global.json).
#
#    Read off the assembly rather than `git describe` because they can legitimately disagree:
#    the compiler is only rebuilt when ncc\*.n changes, so HEAD moves on while dist\ncc keeps
#    the generation it was built from -- and it is the packaged bits, not the checkout, that the
#    version must describe.
# ---------------------------------------------------------------------------
if ($Pack) {
    # dist\release is the release SET, not just a package output directory: the VSIX
    # (dotnet-port\vscode-nemerle's `npm run package`) lands here too, and pack-release.ps1
    # seals the folder with release-info.json. It doubles as a NuGet local feed because NuGet
    # only looks for *.nupkg in a folder source and ignores everything else, so a user can point
    # a NuGet.config straight at the extracted release archive - which is exactly what
    # packaging\README.md tells them to do.
    if ($PackageOutDir -eq "") { $PackageOutDir = Join-Path $PSScriptRoot "dist\release" }
    New-Item -ItemType Directory -Force -Path $PackageOutDir | Out-Null

    $v = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $OutDirFull "Nemerle.dll")).Version
    $PackageVersion = "{0}.{1}.{2}" -f $v.Major, $v.Minor, $v.Revision
    if ($PackageVersionSuffix -ne "") { $PackageVersion = "$PackageVersion-$PackageVersionSuffix" }

    Write-Host ""
    Write-Host "Packing Nemerle.Sdk.Unofficial / Nemerle.Templates.Unofficial $PackageVersion ..."

    # The templates generate projects that pin the SDK version they were packed alongside
    # (<Project Sdk="Nemerle.Sdk.Unofficial/x.y.z">), so the version has to be injected here
    # rather than committed: a hard-coded version in the template sources would silently rot
    # into "generates projects referencing a package that no longer exists" the first time the
    # compiler generation moves. Stage a copy with __NEMERLE_SDK_VERSION__ substituted and pack
    # THAT (Nemerle.Templates.Unofficial.csproj refuses to pack without it).
    $TemplateSrcDir   = Join-Path $PSScriptRoot "packaging\Nemerle.Templates.Unofficial\content"
    $TemplateStageDir = Join-Path $PSScriptRoot "dist\templates"
    if (Test-Path $TemplateStageDir) { Remove-Item -Recurse -Force $TemplateStageDir }
    New-Item -ItemType Directory -Force -Path $TemplateStageDir | Out-Null
    Copy-Item -Path (Join-Path $TemplateSrcDir "*") -Destination $TemplateStageDir -Recurse -Force
    $substituted = 0
    foreach ($file in (Get-ChildItem $TemplateStageDir -Recurse -File)) {
        $text = Get-Content -Raw -Path $file.FullName
        if ($text -match '__NEMERLE_SDK_VERSION__') {
            Set-Content -Path $file.FullName -Value $text.Replace('__NEMERLE_SDK_VERSION__', $PackageVersion) -NoNewline -Encoding utf8
            $substituted++
        }
    }
    if ($substituted -eq 0) { throw "No __NEMERLE_SDK_VERSION__ placeholder found under $TemplateSrcDir -- the template sources and this script have drifted apart." }
    Write-Host "Staged templates -> $TemplateStageDir ($substituted files pinned to $PackageVersion)"

    $TemplateStageWithSlash = (Resolve-Path $TemplateStageDir).Path.TrimEnd('\') + '\'

    $PackageProjects = @(
        (Join-Path $PSScriptRoot "packaging\Nemerle.Sdk.Unofficial\Nemerle.Sdk.Unofficial.csproj"),
        (Join-Path $PSScriptRoot "packaging\Nemerle.Templates.Unofficial\Nemerle.Templates.Unofficial.csproj")
    )

    # NuGet caches an (id, version) in the global packages folder by identity, NOT by content:
    # once 1.2.601-preview.2 has been restored anywhere on this machine, a re-packed
    # 1.2.601-preview.2 is ignored in favour of the extracted copy, and the next build silently
    # tests stale bits. That trap is documented for Nemerle.Ncc.DevTool in DISTRIBUTION.md
    # section 2 with "bump the version on every re-pack" as the workaround, which is untenable
    # for an SDK whose version is meaningful. Evict the exact (id, version) instead, so
    # re-packing the same version during development is honest.
    $GlobalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget\packages" }
    foreach ($id in @("nemerle.sdk.unofficial", "nemerle.templates.unofficial")) {
        $cached = Join-Path $GlobalPackages "$id\$PackageVersion"
        if (Test-Path $cached) {
            Remove-Item -Recurse -Force $cached
            Write-Host "Evicted stale $id/$PackageVersion from the global packages folder"
        }
    }

    foreach ($proj in $PackageProjects) {
        & dotnet pack -c $Configuration $proj -o $PackageOutDir "-p:Version=$PackageVersion" `
            "-p:NccLayoutDir=$OutDirWithSlash" "-p:NemerleTemplateStagingDir=$TemplateStageWithSlash" -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $proj (exit $LASTEXITCODE)" }
    }

    # Ship the install guide alongside the packages. Whoever downloads a GitHub release asset has
    # the .nupkg files and no checkout, so a guide that only exists in the repository is a guide
    # they cannot read; $PackageOutDir is what gets archived, so it has to explain itself.
    Copy-Item -Path (Join-Path $PSScriptRoot "packaging\README.md") -Destination $PackageOutDir -Force
    Write-Host "Wrote $(Join-Path $PackageOutDir 'README.md') (install guide)"

    Write-Host ""
    Write-Host "Packages -> $PackageOutDir"
    Get-ChildItem $PackageOutDir -Filter "*$PackageVersion.nupkg" | Format-Table Name, Length
    Write-Host "Try them from an empty directory outside the repository:"
    Write-Host "  dotnet new install Nemerle.Templates.Unofficial::$PackageVersion --add-source `"$PackageOutDir`""
    Write-Host "  dotnet new nemerle-console"
    Write-Host "  dotnet build   # resolves Nemerle.Sdk.Unofficial from the same feed via NuGet.config"
}
