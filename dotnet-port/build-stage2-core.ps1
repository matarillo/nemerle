# WP-D: builds the core (CoreCLR/.NET-flavor) Nemerle toolchain by invoking a
# dotnet-hosted ncc.exe (by default Stage1, running on .NET 10 via `dotnet exec`)
# against static response files for the 4 core projects, in dependency order:
#   Nemerle.dll -> Nemerle.Compiler.dll -> Nemerle.Macros.dll -> ncc.exe
#
# This is the "smallest dotnet path" described in dotnet-port\02-build-flow.md section 7:
# no MSBuild, no Ncc task -- just `dotnet exec <compiler>\ncc.exe /from-file:<rsp>` per
# project, writing the 4 response files into dotnet-port\rsp\stage2\ (or -RspDir) so they
# can be inspected/reused directly.
#
# Usage (default: use Stage1 to build Stage2):
#   pwsh dotnet-port\build-stage2-core.ps1
#
# Usage (self-host fixpoint: use Stage2 to build Stage3):
#   pwsh dotnet-port\build-stage2-core.ps1 -Compiler bin\Release\core\Stage2\ncc.exe -OutDir bin\Release\core\Stage3
#
# Design notes (see dotnet-port\13-stage2-log.md for the full story):
#   - ncc's LibraryReferenceManager enumerates every /ref:'d assembly's exported types via
#     reflection (Assembly.GetExportedTypes()/GetTypes()) to populate the namespace tree.
#     The .NET 10 shared-framework's *compatibility facade* assemblies (mscorlib.dll,
#     System.dll, System.Core.dll, System.Xml.dll -- pure type-forwarders) report ZERO
#     exported types via this API on CoreCLR (GetForwardedTypes() even throws for missing
#     forward targets like System.Security.Permissions) -- so option (a) from the work
#     order (facade /ref: paths) does not work with ncc's reflection-based importer.
#   - Working combination (option (b), used here): `-use-loaded-corlib` maps the bare
#     names "mscorlib"/"System" (passed as plain -ref: values, not paths) directly to the
#     *running* CoreCLR's own System.Private.CoreLib / System.Text.RegularExpressions
#     assemblies -- these are real, fully-populated assemblies, so GetExportedTypes()
#     works. Everything else the four projects need (Console, Collections, Collections.
#     Specialized, Linq, Diagnostics.Process, Private.Uri, Diagnostics.TraceSource,
#     Security.Cryptography, Private.Xml, Private.Xml.Linq, Data.Common) is referenced by
#     an explicit /ref: path to the *real* (non-facade) split assembly in the shared
#     framework directory, resolved dynamically below via `dotnet --list-runtimes`.
#   - A handful of CLR4-only APIs genuinely do not exist on CoreCLR's own
#     System.Reflection.Emit/System.Security.Permissions/System.Diagnostics.SymbolStore
#     surface (AssemblyBuilder.Save/SetEntryPoint, *Builder.GetToken(), *.
#     AddDeclarativeSecurity, SymDocumentType/SymLanguageType/SymLanguageVendor/
#     SymbolToken/ISymbolWriter, PermissionSetAttribute). These are the *Clr4-suffixed
#     "never invoked on CoreCLR at run time" methods from WP-B/WP-C -- but ncc must still
#     *type-check* their bodies at compile time even though they're runtime-dead on
#     CoreCLR, so they are now also guarded out of compilation entirely with
#     `#if NET_4_0 ... #else <dead-code stub> #endif` (stage2 rsp files below do NOT
#     define NET_4_0). See dotnet-port\13-stage2-log.md for the exact list of methods.
#   - -linkres/Win32 -res are still not supported when the compiler itself runs on
#     CoreCLR (known gap carried over from WP-B/WP-C) -- stage2 rsp files below do not
#     pass -res/-linkres. -debug IS supported on CoreCLR since WP-E (Portable PDB, see
#     dotnet-port\14-pdb-log.md) but is not passed by default here, to keep the stage2
#     output minimal/deterministic-ish -- pass -EmitDebug to this script to opt it back
#     in (e.g. for PDB determinism verification). /doc: is NOT passed: it was dropped
#     opportunistically to keep the rsp files minimal and closer to the ncc.nproj
#     (Release) set; it is not known to be broken, just untested here -- a documented
#     gap, not a finding.
#   - NB (WP-E finding): assembly versions come from `git describe` (commits since the
#     last tag), so the -Compiler's own Nemerle.dll must have been built from the same
#     commit as HEAD -- otherwise loading the freshly built Stage2\Nemerle.dll (newer
#     version, same simple name) into the compiler process fails with a ref-def
#     mismatch FileLoadException. This is now checked automatically (WP-N1,
#     dotnet-port\assembly-version-check.ps1) right after the -Compiler existence check
#     below, and throws with the recovery steps (full Stage1 rebuild) if it is stale.

param(
    [string]$Configuration = "Release",
    [string]$Compiler = "",                # path to the ncc.exe to run the build WITH (default: Stage1)
    [string]$OutDir = "",                  # path to write the built Stage2 assemblies to
    [string]$RspDir = "",                  # where to write the 4 .rsp files
    [switch]$SkipRspGeneration,            # reuse existing .rsp files verbatim (for manual edits/reruns)
    [switch]$EmitDebug                     # pass -debug to all 4 compilations (PDB determinism verification); off by default
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($Compiler -eq "") { $Compiler = Join-Path $RepoRoot "bin\$Configuration\net-4.0\Stage1\ncc.exe" }
if ($OutDir   -eq "") { $OutDir   = Join-Path $RepoRoot "bin\$Configuration\core\Stage2" }
if ($RspDir   -eq "") { $RspDir   = Join-Path $PSScriptRoot "rsp\stage2" }

if (-not (Test-Path $Compiler)) { throw "Compiler not found: $Compiler" }

# WP-N1 (A2): make sure -Compiler's own Nemerle.dll was built from the same commit as HEAD
# before using it to build Stage2 -- without this check, a stale -Compiler fails partway
# through the build with an unexplained ref-def mismatch FileLoadException (see the design
# notes above and dotnet-port\assembly-version-check.ps1) instead of stopping here with the
# recovery steps.
. "$PSScriptRoot\assembly-version-check.ps1"
Test-NemerleAssemblyVersionFreshness -NemerleDllPath (Join-Path (Split-Path $Compiler) "Nemerle.dll") -RepoRoot $RepoRoot -Label "Compiler ($Compiler)"

New-Item -ItemType Directory -Force -Path $OutDir  | Out-Null
New-Item -ItemType Directory -Force -Path $RspDir  | Out-Null

# ---------------------------------------------------------------------------
# Resolve the shared framework directory for the *running* dotnet SDK's
# Microsoft.NETCore.App, so -ref: paths point at real, non-facade split
# assemblies from the exact runtime that will host ncc via `dotnet exec`.
# ---------------------------------------------------------------------------
$runtimes = & dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App (\S+) \[(.+)\]$' }
$netCoreRuntimes = $runtimes | ForEach-Object {
    if ($_ -match '^Microsoft\.NETCore\.App (\S+) \[(.+)\]$') {
        [PSCustomObject]@{ Version = [version]$Matches[1]; Dir = $Matches[2] }
    }
} | Sort-Object Version -Descending
$best = $netCoreRuntimes | Where-Object { $_.Version.Major -eq 10 } | Select-Object -First 1
if ($null -eq $best) { $best = $netCoreRuntimes | Select-Object -First 1 }
if ($null -eq $best) { throw "Could not resolve a Microsoft.NETCore.App shared framework via 'dotnet --list-runtimes'" }
$FW = Join-Path $best.Dir $best.Version.ToString()
Write-Host "Using shared framework: $FW"

function FwRef([string]$name) { Join-Path $FW $name }

# Real (non-facade) split assemblies needed beyond -use-loaded-corlib's mscorlib/System
# mapping -- see the design notes above for why the facade dlls (mscorlib.dll etc.) can't
# be used instead. Each is a plain -ref: path (path form => Assembly.LoadFrom, not the
# bare-name UseLoadedCorlib special case).
$CoreRefs = @(
    "System.Collections.dll",
    "System.Console.dll",
    "System.Collections.Specialized.dll",
    "System.Linq.dll",
    "System.Diagnostics.Process.dll",
    "System.Private.Uri.dll",
    "System.Diagnostics.TraceSource.dll",
    "System.Security.Cryptography.dll",
    "System.Private.Xml.dll",
    "System.Private.Xml.Linq.dll",
    "System.Data.Common.dll"
) | ForEach-Object { FwRef $_ }

$KeysDir = Join-Path $RepoRoot "misc\keys"
$NemerleKey  = Join-Path $KeysDir "Nemerle.snk"
$CompilerKey = Join-Path $KeysDir "Nemerle.Compiler.snk"

# ---------------------------------------------------------------------------
# Source file lists -- same globs as the .nproj files (see dotnet-port\02-build-flow.md
# section 2), enumerated explicitly here. ncc\codedom\*.n is intentionally excluded from
# Nemerle.Compiler.dll's source list, matching Nemerle.Compiler.nproj since WP-C
# (dotnet-port\12-selfhost-blockers-log.md): it pulls in System.CodeDom/System.Configuration,
# neither of which is in the .NET 10 shared framework, and nothing in ncc\/lib\/macros\
# calls into it.
# ---------------------------------------------------------------------------
function Sources([string[]]$patterns) {
    $patterns | ForEach-Object { Get-ChildItem -Path (Join-Path $RepoRoot $_) -File | Sort-Object Name } |
        ForEach-Object { $_.FullName }
}

$NemerleSources = Sources @("lib\*.n")

$CompilerSources = @(
    (Join-Path $RepoRoot "ncc\CompilationOptions.n"),
    (Join-Path $RepoRoot "ncc\passes.n")
) + (Sources @(
        "ncc\parsing\*.n",
        "ncc\completion\*.n",
        "ncc\external\*.n",
        "ncc\external\ExternalMemberInfo\*.n",
        "ncc\external\ExternalTypeInfo\*.n",
        "ncc\generation\*.n",
        "ncc\hierarchy\*.n",
        "ncc\misc\*.n",
        "ncc\optimization\*.n",
        "ncc\typing\*.n"
    ))

$MacrosSources = Sources @("macros\*.n")

$NccSources = @(
    (Join-Path $RepoRoot "ncc\misc\AssemblyInfo.n"),
    (Join-Path $RepoRoot "ncc\main.n")
)

Write-Host "Source counts: Nemerle=$($NemerleSources.Count) Nemerle.Compiler=$($CompilerSources.Count) Nemerle.Macros=$($MacrosSources.Count) ncc=$($NccSources.Count)"

# ---------------------------------------------------------------------------
# rsp writer. One switch/file per line, matching the Ncc MSBuild task's
# AddResponseFileCommandsImpl format (dotnet-port\02-build-flow.md section 3/7).
# ---------------------------------------------------------------------------
function Write-Rsp {
    param(
        [string]$Path,
        [string]$KeyFile,
        [string]$Target,               # "library" or "exe"
        [string]$OutFile,
        [string[]]$Sources,
        [string[]]$Refs
    )
    # ncc's -from-file reader supports "..."/'...' quoting within a line (see
    # ncc\CompilationOptions.n execute_fromfile) -- quote every path-shaped value since
    # the shared framework directory ("C:\Program Files\dotnet\...") contains a space.
    function Q([string]$v) { '"' + $v + '"' }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("-no-color")
    $lines.Add("-optimize")
    $lines.Add("-no-stdlib")
    $lines.Add("-greedy-references:-")
    $lines.Add("-use-loaded-corlib")
    $lines.Add("-define:RUNTIME_MS")
    if ($EmitDebug) { $lines.Add("-debug") }
    $lines.Add("-keyfile:$(Q $KeyFile)")
    $lines.Add("-target:$Target")
    foreach ($s in $Sources) { $lines.Add((Q $s)) }
    foreach ($r in $Refs)    { $lines.Add("-ref:$(Q $r)") }
    $lines.Add("-out:$(Q $OutFile)")
    Set-Content -Path $Path -Value $lines -Encoding utf8
}

$NemerleRsp  = Join-Path $RspDir "Nemerle.rsp"
$CompilerRsp = Join-Path $RspDir "Nemerle.Compiler.rsp"
$MacrosRsp   = Join-Path $RspDir "Nemerle.Macros.rsp"
$NccRsp      = Join-Path $RspDir "ncc.rsp"

$NemerleDll  = Join-Path $OutDir "Nemerle.dll"
$CompilerDll = Join-Path $OutDir "Nemerle.Compiler.dll"
$MacrosDll   = Join-Path $OutDir "Nemerle.Macros.dll"
$NccExe      = Join-Path $OutDir "ncc.exe"

if (-not $SkipRspGeneration) {
    Write-Rsp -Path $NemerleRsp -KeyFile $NemerleKey -Target "library" -OutFile $NemerleDll `
        -Sources $NemerleSources -Refs (@("mscorlib", "System") + $CoreRefs)

    Write-Rsp -Path $CompilerRsp -KeyFile $CompilerKey -Target "library" -OutFile $CompilerDll `
        -Sources $CompilerSources -Refs (@("mscorlib", "System") + $CoreRefs + @($NemerleDll))

    Write-Rsp -Path $MacrosRsp -KeyFile $CompilerKey -Target "library" -OutFile $MacrosDll `
        -Sources $MacrosSources -Refs (@("mscorlib", "System") + $CoreRefs + @($NemerleDll, $CompilerDll))

    Write-Rsp -Path $NccRsp -KeyFile $CompilerKey -Target "exe" -OutFile $NccExe `
        -Sources $NccSources -Refs (@("mscorlib", "System", (FwRef "System.Console.dll"), (FwRef "System.Diagnostics.Process.dll"), (FwRef "System.Private.Uri.dll")) + @($NemerleDll, $CompilerDll))

    Write-Host "Wrote rsp files to $RspDir"
}

# ---------------------------------------------------------------------------
# Build, in dependency order: Nemerle -> Nemerle.Compiler -> Nemerle.Macros -> ncc.
# Each invocation is `dotnet exec <Compiler> /from-file:<rsp>` -- the "smallest dotnet
# path" from dotnet-port\02-build-flow.md section 7. Note: the compiler process (Stage1
# or whichever -Compiler was passed) loads *its own* Nemerle.Macros.dll (next to it) for
# standard macro expansion while compiling these sources -- that is the existing
# bootstrap pattern (see 00-PLAN.md work log / this script's header) and is not
# reconfigured here.
# ---------------------------------------------------------------------------
function Invoke-Ncc([string]$RspFile, [string]$Label) {
    Write-Host "== Building $Label =="
    & dotnet exec $Compiler "/from-file:$RspFile"
    $exit = $LASTEXITCODE
    if ($exit -ne 0) { throw "$Label build FAILED (exit $exit) -- rsp: $RspFile" }
    Write-Host "$Label -> OK"
}

Invoke-Ncc -RspFile $NemerleRsp  -Label "Nemerle.dll"
Invoke-Ncc -RspFile $CompilerRsp -Label "Nemerle.Compiler.dll"
Invoke-Ncc -RspFile $MacrosRsp   -Label "Nemerle.Macros.dll"
Invoke-Ncc -RspFile $NccRsp      -Label "ncc.exe"

# ---------------------------------------------------------------------------
# Make $OutDir a self-contained, runnable "compiler directory": copy the CoreEmit
# helper assembly (see dotnet-port\11-emission-log.md) and write a runtimeconfig.json
# for ncc.exe so `dotnet exec $OutDir\ncc.exe ...` can start directly.
# ---------------------------------------------------------------------------
$CoreEmitSrc = Join-Path $RepoRoot "dotnet-port\Nemerle.CoreEmit\bin\$Configuration\net10.0\Nemerle.CoreEmit.dll"
if (-not (Test-Path $CoreEmitSrc)) {
    Write-Host "Building Nemerle.CoreEmit ($Configuration)..."
    dotnet build -c $Configuration (Join-Path $RepoRoot "dotnet-port\Nemerle.CoreEmit\Nemerle.CoreEmit.csproj") --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "Nemerle.CoreEmit build failed" }
}
Copy-Item -Path $CoreEmitSrc -Destination $OutDir -Force

$RuntimeConfigPath = Join-Path $OutDir "ncc.runtimeconfig.json"
@"
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
    "rollForward": "LatestMinor"
  }
}
"@ | Set-Content -Path $RuntimeConfigPath -Encoding utf8

Write-Host ""
Write-Host "Stage2-style build complete -> $OutDir"
Get-ChildItem $OutDir | Format-Table Name, Length
