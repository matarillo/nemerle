# WP-I1: runs the REAL testsuite harness (Nemerle.Compiler.Test.exe /
# Nemerle.Test.Framework.dll -- snippets\Nemerle.Test\...) against a CoreCLR
# ("core"-flavor) ncc.exe, using the harness's existing `-ncc <exe>` (ExternalNcc)
# and `-runtime <exe> -runtime-params <args>` (RuntimeProcessStartInfoFactory)
# switches (dotnet-port\02-build-flow.md section 8) -- NO harness source changes
# needed. See dotnet-port\18-testsuite-log.md for the full writeup/rationale.
#
# Key facts this script relies on (see 18-testsuite-log.md for verification):
#   - The harness itself stays a CLR4 (.NET Framework) executable -- it runs fine
#     natively on this machine (Framework runtime-only install) and does NOT need
#     `dotnet exec` for itself.
#   - `-runtime <dotnet.exe> -runtime-params exec` makes BOTH steps of ExternalNcc
#     testing go through `dotnet.exe exec ...`: (1) invoking the compiler under
#     test (`-ncc <core ncc.exe>`) and (2) running the compiled *positive* test's
#     output .exe (RuntimeProcessStartInfoFactory is shared between the two).
#   - The compiler-under-test needs `-use-loaded-corlib` + real (non-facade)
#     split-assembly `-ref:`s, exactly like dotnet-port\build-stage2-core.ps1's
#     stage2 build (see 13-stage2-log.md section 1) -- passed here as global
#     `-ref:`/`-p:` options applied to every test file via the harness's own
#     `-reference`/`-parameters` switches.
#   - Per constraints: this script COPIES the compiler-under-test and the harness
#     out of bin\ into a staging directory before use, and never writes into bin\
#     (a concurrent work package regenerates bin\...\Stage2\ during development).
#
# Usage examples:
#   pwsh dotnet-port\run-testsuite-core.ps1
#       (default: test bin\Release\core\Stage2\ncc.exe against the full testsuite)
#   pwsh dotnet-port\run-testsuite-core.ps1 -Suite positive
#   pwsh dotnet-port\run-testsuite-core.ps1 -Compiler <path-to-other-ncc.exe-dir>
#   pwsh dotnet-port\run-testsuite-core.ps1 -StagingDir C:\scratch\nemerle-testsuite

param(
    [string]$Configuration = "Release",
    [string]$CompilerDir   = "",           # dir containing the core-flavor ncc.exe + Nemerle*.dll (default: bin\<Cfg>\core\Stage2)
    [string]$LibsDir       = "",           # dir containing core-built auxiliary libs (Nemerle.Linq.dll, built by
                                           # dotnet-port\build-libs-core.ps1; default: bin\<Cfg>\core\Libs). Staged next to the
                                           # compiler so REFERENCE:-pragma bare-name resolution (CWD probing) and the compiled
                                           # tests' run-time loads both find them -- mirrors NemerleAll.nproj's CompilerTests
                                           # copying $(NBin)\Linq\Nemerle.Linq.dll into $(NBin)\Tests\positive. Skipped with a
                                           # warning if the directory does not exist (the Linq-dependent tests then fail as
                                           # they did before WP-N3).
    [string]$HarnessDir    = "",           # dir containing the prebuilt CLR4 Nemerle.Compiler.Test.exe (default: bin\<Cfg>\net-4.0\TestFramework)
    [string]$StagingDir    = "",           # scratch dir the compiler+harness get copied into (default: %TEMP%\nemerle-core-testsuite)
    [string]$TestSuiteDir  = "",           # default: <repo>\testsuite
    [string]$OutputDir     = "",           # default: <StagingDir>\out
    [ValidateSet("positive", "negative", "both")]
    [string]$Suite = "both",
    [switch]$SkipCopy                      # reuse an existing staging copy as-is (faster iteration)
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($CompilerDir -eq "") { $CompilerDir = Join-Path $RepoRoot "bin\$Configuration\core\Stage2" }
if ($LibsDir     -eq "") { $LibsDir     = Join-Path $RepoRoot "bin\$Configuration\core\Libs" }
if ($HarnessDir  -eq "") { $HarnessDir  = Join-Path $RepoRoot "bin\$Configuration\net-4.0\TestFramework" }
if ($StagingDir  -eq "") { $StagingDir  = Join-Path ([IO.Path]::GetTempPath()) "nemerle-core-testsuite" }
if ($TestSuiteDir -eq "") { $TestSuiteDir = Join-Path $RepoRoot "testsuite" }
if ($OutputDir   -eq "") { $OutputDir   = Join-Path $StagingDir "out" }

if (-not (Test-Path $CompilerDir)) { throw "Compiler dir not found: $CompilerDir" }
if (-not (Test-Path $HarnessDir))  { throw "Harness dir not found: $HarnessDir" }

$StagedCompiler = Join-Path $StagingDir "compiler"
$StagedHarness  = Join-Path $StagingDir "harness"

if (-not $SkipCopy) {
    Write-Host "Staging compiler: $CompilerDir -> $StagedCompiler"
    New-Item -ItemType Directory -Force -Path $StagedCompiler | Out-Null
    Copy-Item -Path (Join-Path $CompilerDir "*") -Destination $StagedCompiler -Recurse -Force

    Write-Host "Staging harness: $HarnessDir -> $StagedHarness"
    New-Item -ItemType Directory -Force -Path $StagedHarness | Out-Null
    Copy-Item -Path (Join-Path $HarnessDir "*") -Destination $StagedHarness -Recurse -Force

    # WP-N3: auxiliary core-built libraries (Nemerle.Linq.dll). Staged into the compiler
    # directory so the existing per-suite "*.dll -> output dir" copy below places them in
    # the harness working directory, where (a) `// REFERENCE: Nemerle.Linq` pragmas
    # resolve by bare-name CWD probing at compile time and (b) the compiled tests find
    # them at run time.
    if (Test-Path $LibsDir) {
        Write-Host "Staging auxiliary libs: $LibsDir -> $StagedCompiler"
        Copy-Item -Path (Join-Path $LibsDir "*.dll") -Destination $StagedCompiler -Force
    } else {
        Write-Warning "LibsDir not found ($LibsDir) -- Nemerle.Linq-dependent tests will fail. Build it with dotnet-port\build-libs-core.ps1."
    }
}

$NccExe = Join-Path $StagedCompiler "ncc.exe"
$HarnessExe = Join-Path $StagedHarness "Nemerle.Compiler.Test.exe"
if (-not (Test-Path $NccExe))     { throw "Staged compiler missing ncc.exe: $NccExe" }
if (-not (Test-Path $HarnessExe)) { throw "Staged harness missing Nemerle.Compiler.Test.exe: $HarnessExe" }

$DotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $DotnetCmd) { throw "dotnet.exe not found on PATH" }
$DotnetExe = $DotnetCmd.Source

# ---------------------------------------------------------------------------
# Same shared-framework/ref-assembly resolution as build-stage2-core.ps1 (see
# dotnet-port\13-stage2-log.md section 1 for why facade refs don't work and
# -use-loaded-corlib + real split assemblies is the working combination).
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
Write-Host "Using dotnet: $DotnetExe"
Write-Host "Using compiler: $NccExe"
Write-Host "Using harness: $HarnessExe"

function FwRef([string]$name) { Join-Path $FW $name }

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
    "System.Data.Common.dll",
    "System.Xml.Serialization.dll",       # needed by testsuite\positive\attributes-01.n (System.Xml.Serialization.XmlElementAttribute etc.)
    "System.Runtime.Serialization.Formatters.dll",
    "System.Net.Primitives.dll",          # System.Net.IPAddress (testsuite\positive\bug-1216.n)
    "System.Net.NameResolution.dll",      # System.Net.IPHostEntry/Dns (testsuite\positive\properties.n)
    "System.Collections.NonGeneric.dll",  # System.Collections.SortedList/Hashtable (testsuite\positive\enumerator.n)
    "System.ObjectModel.dll",             # System.Collections.ObjectModel.KeyedCollection (testsuite\positive\generics.n)
    "System.Linq.Expressions.dll",        # WP-N3: expression trees (Nemerle.Linq consumers: Issue-git-0232/0239/0272-linq-ET, linq-2-ExprTree)
    "System.Linq.Queryable.dll"           # WP-N3: AsQueryable/IQueryable operators (Issue-git-0590-2.n, linq-2-ExprTree.n)
) | ForEach-Object { FwRef $_ }

$GlobalRefs = @("mscorlib", "System") + $CoreRefs + @((Join-Path $StagedCompiler "Nemerle.dll"))

# ---------------------------------------------------------------------------
# Build the harness argument list. `-p` carries whitespace-split ncc switches
# with NO embedded spaces (safe); every path (which may contain spaces, e.g.
# "C:\Program Files\dotnet\...") goes through its own `-ref:` occurrence
# instead, since Main.n's `-p` parser just does value.Split(' ','\t','\n','\r')
# with no quoting.
# ---------------------------------------------------------------------------
# WP-N3: -def:RUNTIME_CORE gives test sources an explicit "running the CoreCLR harness"
# preprocessor symbol. The CLR4 harness (NemerleAll.nproj CompilerTests) defines
# RUNTIME_MS;NET_4_0 and never defines RUNTIME_CORE, so `#if !NET_4_0 && !RUNTIME_CORE`
# shims (external-extension-method-lib.n's pre-3.5 ExtensionAttribute shim) stay exactly
# as before on CLR4 while correctly dropping out here -- CoreCLR's corelib already ships
# the types those shims declared, so compiling them is a redefinition error. NET_4_0's
# mere absence cannot express this (it also means ".NET 2.0/3.5", where the shim is needed).
$CommonArgs = @(
    "-ncc", $NccExe,
    "-r", $DotnetExe,
    "-rp", "exec",
    "-p", "-no-stdlib -use-loaded-corlib -greedy-references:- -nowarn:10003 -def:RUNTIME_CORE"
)
foreach ($r in $GlobalRefs) { $CommonArgs += @("-ref", $r) }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Run-Suite([string]$Name) {
    $suiteDir = Join-Path $TestSuiteDir $Name
    if (-not (Test-Path $suiteDir)) { throw "Suite dir not found: $suiteDir" }
    $outDir = Join-Path $OutputDir $Name
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    Get-ChildItem -Path $outDir -File | Remove-Item -Force -ErrorAction SilentlyContinue

    # Positive tests with BEGIN-OUTPUT get their compiled .exe *executed* right in
    # $outDir (testOutputAssembly's ProcessStartInfoFactory runs `dotnet exec
    # <objectFilePath>`). The compiled exe references Nemerle.dll (stdlib) but the
    # .NET host only probes the exe's own directory (+ shared framework) for
    # dependencies -- it does NOT know about $StagedCompiler. Without a local copy,
    # every test that actually touches the Nemerle standard library at run time
    # (list, printf-style macros, ...) crashes with FileNotFoundException, surfaced
    # by the harness as the opaque "Test finished with exit code -532462766"
    # (0xE0434352, CLR unhandled-exception sentinel). This mirrors the exact
    # "pre-existing scratch-dir artifact" noted in 13-stage2-log.md's CLR4
    # regression check ("once Nemerle.dll is copied beside the exe"). Fix: copy the
    # whole staged compiler directory (Nemerle.dll/.Compiler.dll/.Macros.dll/
    # CoreEmit.dll) beside the test outputs -- mirrors NemerleAll.nproj's
    # CompilerTests target, which copies `$(NBin)\TestFramework\*.*` into
    # `$(NBin)\Tests\positive` for exactly this reason.
    Copy-Item -Path (Join-Path $StagedCompiler "*.dll") -Destination $outDir -Force

    $logPath = Join-Path $OutputDir "$Name.log"
    Write-Host ""
    Write-Host "== Running $Name suite (log: $logPath) =="

    $patterns = @("*.n", "*.nnn", "*.cs") | ForEach-Object { Join-Path $suiteDir $_ }

    # Working directory MUST equal -output's target so bare-name `-r:<companion-lib>`
    # REFERENCE: pragmas (e.g. testsuite\positive\anonymous-classes.n needing
    # anonymous-classes-lib.n's compiled .dll) can be found -- LibraryReferenceManager
    # probes System.Environment.CurrentDirectory, not the harness's -output value
    # (verified empirically; see 18-testsuite-log.md). This matches the original
    # NemerleAll.nproj CompilerTests invocation's WorkingDirectory=...\Tests\positive.
    Push-Location $outDir
    try {
        $allArgs = $CommonArgs + @("-output", ".") + $patterns
        & $HarnessExe @allArgs 2>&1 | Tee-Object -FilePath $logPath | Out-Null
    } finally {
        Pop-Location
    }
    $logPath
}

$results = @{}
if ($Suite -eq "positive" -or $Suite -eq "both") { $results["positive"] = Run-Suite "positive" }
if ($Suite -eq "negative" -or $Suite -eq "both") { $results["negative"] = Run-Suite "negative" }

# ---------------------------------------------------------------------------
# Parse each log for per-test pass/fail/skip and print a summary. The harness
# always emits exactly one line per test matching "<name>: ...<status>" where
# status is one of the four fixed strings below (NccTestOutputWriter.n /
# NccTest.n -- "passed" from GetSuccesOrFailResult, "failed" written eagerly at
# the first error, "not a test"/"unable to load library" from the two
# GetNotRunResult(...) call sites -- these are the ONLY strings ever passed to
# GetNotRunResult in the harness source).
# ---------------------------------------------------------------------------
function Parse-Log([string]$Path) {
    $lines = Get-Content -Path $Path
    $testLineRegex = '^(?<name>\S.*?): .*?(?<status>passed|failed|not a test|unable to load library)\s*$'
    $tests = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in $lines) {
        $m = [regex]::Match($line, $testLineRegex)
        if ($m.Success) {
            if ($null -ne $current) { $tests.Add($current) }
            $current = [PSCustomObject]@{ Name = $m.Groups['name'].Value; Status = $m.Groups['status'].Value; Detail = New-Object System.Collections.Generic.List[string] }
        } elseif ($null -ne $current -and $current.Status -eq 'failed') {
            $current.Detail.Add($line)
        }
    }
    if ($null -ne $current) { $tests.Add($current) }
    $tests
}

foreach ($name in $results.Keys) {
    $log = $results[$name]
    $tests = Parse-Log $log
    $passed = ($tests | Where-Object { $_.Status -eq 'passed' }).Count
    $failed = ($tests | Where-Object { $_.Status -eq 'failed' }).Count
    $notTest = ($tests | Where-Object { $_.Status -eq 'not a test' }).Count
    $noLib = ($tests | Where-Object { $_.Status -eq 'unable to load library' }).Count
    $total = $tests.Count
    Write-Host ""
    Write-Host "=== $name summary ==="
    Write-Host "  total (incl. NO-TEST companion files): $total"
    Write-Host "  passed: $passed"
    Write-Host "  failed: $failed"
    Write-Host "  skipped (not a test / companion lib):  $notTest"
    Write-Host "  skipped (unable to load library):      $noLib"

    $failLog = Join-Path $OutputDir "$name-failures.txt"
    $tests | Where-Object { $_.Status -eq 'failed' } | ForEach-Object {
        "### $($_.Name)"
        $_.Detail
        ""
    } | Set-Content -Path $failLog -Encoding utf8
    Write-Host "  failure detail written to: $failLog"
}
