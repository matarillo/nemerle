# WP-N3: builds the auxiliary Nemerle libraries for the core (CoreCLR/.NET 10) flavor,
# using the same static-response-file approach as dotnet-port\build-stage2-core.ps1 --
# `dotnet exec <stage2 ncc.exe> /from-file:<rsp>` per library, no MSBuild.
#
# Currently builds exactly one library:
#   Nemerle.Linq.dll  (Linq\Macro\*.n -- the `linq` syntax macro + ToExpression macro +
#                      their runtime conversion helpers)
#
# Nemerle.Unsafe / Nemerle.WPF are deliberately NOT built here: WP-N3's scope is
# Nemerle.Linq only (dotnet-port\36-prerelease-quality-plan.md WP-N3), the others are
# individual backlog decisions (same doc section 10-6; WPF's System.Xaml/WindowsBase
# dependency makes it a Windows-only, likely-infeasible case).
#
# Design notes (see dotnet-port\40-prerelease-wp-n3-log.md):
#   - The 7 Linq\Macro sources use no CLR4-only BCL surface at all (audited: no
#     System.Windows.Forms / System.Data / System.Xml usage despite Linq.nproj's stale
#     reference list, no AppDomain, no direct Reflection.Emit, no #if branches), so this
#     is a pure re-referencing exercise: same sources, core reference set.
#   - System.Linq.Expressions.dll is referenced explicitly (a real, non-facade split
#     assembly): ToExpressionImpl.n `using`s the namespace, and the macro's generated
#     code (PExpr quotations) resolves against it in CONSUMER compilations.
#   - The output goes to bin\<Cfg>\core\Libs, NOT into the Stage2 compiler directory:
#     Stage2 stays exactly the 4-assembly compiler set that compare-stage.ps1 /
#     pack-tool.ps1's blanket *.dll copy operate on. Consumers opt in:
#     run-testsuite-core.ps1 stages this directory for the testsuite, and
#     pack-tool.ps1 -Pack packages Nemerle.Linq.dll as its own NuGet package
#     (Nemerle.Linq.Unofficial), not inside the Sdk package or dist\ncc.
#   - Nemerle.Linq's AssemblyInfo.n uses the same GeneratedAssemblyVersion machinery as
#     the compiler itself, so the built dll's version is 1.2.0.<revision-of-HEAD> and the
#     usual A2 freshness rules apply (assembly-version-check.ps1): the stage2 compiler
#     must have been built from HEAD's commit, and the Linq dll must be rebuilt whenever
#     the generation advances.
#
# Usage:
#   pwsh dotnet-port\build-libs-core.ps1                 # Stage2 -> bin\Release\core\Libs
#   pwsh dotnet-port\build-libs-core.ps1 -Compiler bin\Release\core\Stage3\ncc.exe -OutDir <dir>

param(
    [string]$Configuration = "Release",
    [string]$Compiler = "",                # ncc.exe to build WITH (default: core Stage2)
    [string]$OutDir = "",                  # default: bin\<Cfg>\core\Libs
    [string]$RspDir = "",                  # default: dotnet-port\rsp\libs
    [switch]$SkipRspGeneration,            # reuse existing .rsp files verbatim
    [switch]$EmitDebug                     # pass -debug (portable PDB) to the compilations
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($Compiler -eq "") { $Compiler = Join-Path $RepoRoot "bin/$Configuration/core/Stage2/ncc.exe" }
if ($OutDir   -eq "") { $OutDir   = Join-Path $RepoRoot "bin/$Configuration/core/Libs" }
if ($RspDir   -eq "") { $RspDir   = Join-Path $PSScriptRoot "rsp/libs" }

if (-not (Test-Path $Compiler)) { throw "Compiler not found: $Compiler (build it first: dotnet-port/build-stage2-core.ps1)" }

# WP-N1 (A2): the compiler must match HEAD's generation, both so it can load the
# freshly-built lib during later use and so the GeneratedAssemblyVersion the Linq dll
# gets stamped with agrees with the compiler's own.
. "$PSScriptRoot/assembly-version-check.ps1"
Test-NemerleAssemblyVersionFreshness -NemerleDllPath (Join-Path (Split-Path $Compiler) "Nemerle.dll") -RepoRoot $RepoRoot -Label "Compiler ($Compiler)"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $RspDir | Out-Null

# Same shared-framework resolution as build-stage2-core.ps1 (facades are useless to
# ncc's reflection importer; real split assemblies + -use-loaded-corlib is the working
# combination -- dotnet-port\13-stage2-log.md section 1).
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

# The stage2 reference set, plus System.Linq.Expressions (real assembly; the CLR4
# project's `System.Core` reference covered it there).
$CoreRefs = @(
    "System.Collections.dll",
    "System.Console.dll",
    "System.Collections.Specialized.dll",
    "System.Linq.dll",
    "System.Linq.Expressions.dll",
    "System.Diagnostics.Process.dll",
    "System.Private.Uri.dll",
    "System.Diagnostics.TraceSource.dll",
    "System.Security.Cryptography.dll",
    "System.Private.Xml.dll",
    "System.Private.Xml.Linq.dll",
    "System.Data.Common.dll"
) | ForEach-Object { FwRef $_ }

$CompilerDir = Split-Path $Compiler
$NemerleDll  = Join-Path $CompilerDir "Nemerle.dll"
$NemerleCompilerDll = Join-Path $CompilerDir "Nemerle.Compiler.dll"
$CompilerKey = Join-Path $RepoRoot "misc/keys/Nemerle.Compiler.snk"   # same key as Linq\Macro\Linq.nproj

function Sources([string[]]$patterns) {
    $patterns | ForEach-Object { Get-ChildItem -Path (Join-Path $RepoRoot $_) -File | Sort-Object Name } |
        ForEach-Object { $_.FullName }
}

# Same file set as Linq\Macro\Linq.nproj's <Compile> items (all 7 files).
$LinqSources = (Sources @("Linq/Macro/*.n")) + @(Join-Path $RepoRoot "Linq/Macro/Properties/AssemblyInfo.n")

Write-Host "Source counts: Nemerle.Linq=$($LinqSources.Count)"

# Same rsp format as build-stage2-core.ps1 (one switch/file per line, quoted paths).
function Write-Rsp {
    param(
        [string]$Path,
        [string]$KeyFile,
        [string]$OutFile,
        [string[]]$Sources,
        [string[]]$Refs
    )
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
    $lines.Add("-target:library")
    foreach ($s in $Sources) { $lines.Add((Q $s)) }
    foreach ($r in $Refs)    { $lines.Add("-ref:$(Q $r)") }
    $lines.Add("-out:$(Q $OutFile)")
    Set-Content -Path $Path -Value $lines -Encoding utf8
}

$LinqRsp = Join-Path $RspDir "Nemerle.Linq.rsp"
$LinqDll = Join-Path $OutDir "Nemerle.Linq.dll"

if (-not $SkipRspGeneration) {
    Write-Rsp -Path $LinqRsp -KeyFile $CompilerKey -OutFile $LinqDll `
        -Sources $LinqSources -Refs (@("mscorlib", "System") + $CoreRefs + @($NemerleDll, $NemerleCompilerDll))
    Write-Host "Wrote rsp files to $RspDir"
}

function Invoke-Ncc([string]$RspFile, [string]$Label) {
    Write-Host "== Building $Label =="
    & dotnet exec $Compiler "/from-file:$RspFile"
    $exit = $LASTEXITCODE
    if ($exit -ne 0) { throw "$Label build FAILED (exit $exit) -- rsp: $RspFile" }
    Write-Host "$Label -> OK"
}

Invoke-Ncc -RspFile $LinqRsp -Label "Nemerle.Linq.dll"

Write-Host ""
Write-Host "Core library build complete -> $OutDir"
Get-ChildItem $OutDir | Format-Table Name, Length
