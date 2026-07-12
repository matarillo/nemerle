# WP-I2 (task 1): assembles a self-contained, runnable "dotnet ncc" distribution layout
# out of an already-built core-flavor compiler directory (default:
# bin\Release\core\Stage2, produced by dotnet-port\build-stage2-core.ps1).
#
# Problem this solves: the stage2/stage3 output directory already runs fine via
# `dotnet exec <dir>\ncc.exe ...` (verified in 13-stage2-log.md), but (a) `ncc.exe` only
# works with the `exec` verb by convention -- nothing marks it as runnable via the plain
# `dotnet <dll>` shorthand that most modern tooling/docs expect, and (b) every invocation
# needs a long, hand-maintained pile of `-use-loaded-corlib`/`-ref:<shared-framework path>`
# switches (see build-stage2-core.ps1) just to see the standard library -- there is no
# "default install" behavior a user could rely on the way `csc.dll`/`Nemerle.dll` would
# ship with implicit references.
#
# What this script produces (-OutDir, default dotnet-port\dist\ncc):
#   - A copy of the compiler directory's *.dll + ncc.exe, PLUS a byte-identical `ncc.dll`
#     copy of `ncc.exe` (same base name "ncc" so the existing `ncc.runtimeconfig.json`
#     covers both) -- this is what makes `dotnet <OutDir>\ncc.dll <args>` work exactly
#     like `dotnet exec <OutDir>\ncc.exe <args>` (verified below: the dotnet muxer only
#     cares about the base-name-matched runtimeconfig.json, not the file extension).
#   - `ncc.default.rsp`: a wrapper response file with the standard reference set baked in
#     (`-no-stdlib -use-loaded-corlib -greedy-references:-` + `-ref:` to the real,
#     non-facade shared-framework split assemblies and to this layout's own Nemerle.dll --
#     the exact "option (b)" reference recipe from 13-stage2-log.md/build-stage2-core.ps1,
#     just aimed at compiling ordinary user programs instead of the compiler's own
#     sources). These are ABSOLUTE paths specific to this machine/location (ncc resolves
#     -ref: against the current directory, so relative paths won't do); gen-default-rsp.ps1
#     -- shipped alongside -- recomputes them from the layout's own location + the installed
#     runtime, and ncc.cmd runs it automatically whenever the folder has been copied/moved,
#     so the distribution is relocatable. Combine it with `-from-file:` at the FRONT of any
#     invocation -- ncc's
#     `-from-file` is a Getopt `SubstitutionString` option (ncc\CompilationOptions.n /
#     lib\getopt.n): it recursively parses the response file's contents in place and then
#     resumes parsing the rest of the original command line, so
#     `-from-file:ncc.default.rsp -out:hello.exe hello.n` behaves exactly like typing the
#     rsp file's switches followed by `-out:hello.exe hello.n` -- verified below.
#   - `ncc.cmd`: a thin convenience wrapper (`<OutDir>\ncc.cmd -out:hello.exe hello.n`,
#     works from both cmd.exe and a PowerShell prompt) that runs
#     `dotnet <OutDir>\ncc.dll -from-file:<OutDir>\ncc.default.rsp %*` and, on success,
#     additionally copies this layout's Nemerle*.dll into the CURRENT directory --
#     because ordinary Nemerle programs (anything using the stdlib beyond compile-time-
#     only macros like `printf`) reference `Nemerle.dll` at RUN time, and .NET's assembly
#     probing only looks beside the entry assembly / in the shared framework (no GAC) --
#     this is the same "scratch-dir artifact" gap documented in 12-selfhost-blockers-log.md
#     / 18-testsuite-log.md section 3b, addressed here for the packaged layout the same way
#     run-testsuite-core.ps1 addresses it for the test suite. (Deliberately a .cmd, not a
#     .ps1: verified empirically that PowerShell's own command/script invocation splits or
#     misparses `-name:value`-shaped arguments -- `-out:hello.exe` becomes two tokens
#     `-out` / `hello.exe`, or is rejected outright as ambiguous with PowerShell's common
#     `-OutVariable`/`-OutBuffer` parameters -- no matter how the forwarding script
#     declares its parameters ($args, param(), ValueFromRemainingArguments, `--%` all
#     tried and all broken for THIS shape of argument on a .ps1 target). A .cmd's `%*` is
#     untouched by any of that, from cmd.exe *and* when invoked from a PowerShell prompt
#     alike, since PowerShell's parameter binder only kicks in for script/cmdlet targets,
#     not external programs. See dotnet-port\DISTRIBUTION.md for the full writeup.)
#
# Usage:
#   pwsh dotnet-port\pack-tool.ps1                          # default Stage2 -> dotnet-port\dist\ncc
#   pwsh dotnet-port\pack-tool.ps1 -CompilerDir bin\Release\core\Stage3 -OutDir dotnet-port\dist\ncc-stage3
#
# Smoke test (from ANY directory):
#   dotnet <OutDir>\ncc.dll -from-file:<OutDir>\ncc.default.rsp -out:hello.exe hello.n
#   copy <OutDir>\Nemerle*.dll .   (only needed if hello.n uses stdlib beyond printf-style macros)
#   dotnet exec hello.exe
# or simply:
#   <OutDir>\ncc.cmd -out:hello.exe hello.n
#   dotnet exec hello.exe

param(
    [string]$Configuration = "Release",
    [string]$CompilerDir = "",   # source compiler directory (default: bin\<Cfg>\core\Stage2)
    [string]$OutDir = ""         # destination layout directory (default: dotnet-port\dist\ncc)
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($CompilerDir -eq "") { $CompilerDir = Join-Path $RepoRoot "bin\$Configuration\core\Stage2" }
if ($OutDir      -eq "") { $OutDir      = Join-Path $PSScriptRoot "dist\ncc" }

if (-not (Test-Path $CompilerDir)) { throw "Compiler directory not found: $CompilerDir (build it first, e.g. dotnet-port\build-stage2-core.ps1)" }
foreach ($required in @("ncc.exe", "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "ncc.runtimeconfig.json")) {
    if (-not (Test-Path (Join-Path $CompilerDir $required))) { throw "Missing '$required' in $CompilerDir -- not a complete core compiler directory" }
}

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
# 2. Ship a self-contained, RELOCATABLE reference-set generator and run it once.
#    ncc.default.rsp must use ABSOLUTE -ref: paths (ncc resolves -ref: against the current
#    working directory, which is unknown at compile time, so relative paths won't do), but
#    baking THIS machine's paths in at pack time makes the layout non-portable: both the
#    shared-framework path (versioned, e.g. ...\Microsoft.NETCore.App\10.0.9\) and this
#    layout's own Nemerle.dll path would be wrong on any other machine or install location.
#    So instead we write gen-default-rsp.ps1 INTO the layout; it recomputes every absolute
#    path from its own directory (Nemerle.dll) + the target machine's installed runtime (the
#    BCL split assemblies), and ncc.cmd runs it whenever the layout has moved (see section 3).
#    We invoke it once here so the freshly-packed layout is immediately usable on this machine.
# ---------------------------------------------------------------------------
$GenPath = Join-Path $OutDir "gen-default-rsp.ps1"
$genScript = @'
# Regenerates ncc.default.rsp for THIS layout, on the CURRENT machine. Relocatable: every
# -ref: path is resolved at run time from this script's own directory (for Nemerle.dll) and
# the installed shared framework (for the BCL split assemblies), so the distribution works
# wherever it is copied. Re-run this (or just use ncc.cmd, which runs it automatically after
# a move) if you relocate the folder or upgrade the .NET runtime.
$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$netCore =
  foreach ($line in (& dotnet --list-runtimes)) {
    if ($line -match '^Microsoft\.NETCore\.App (\S+) \[(.+)\]$') {
      [PSCustomObject]@{ Version = [version]$Matches[1]; Dir = $Matches[2] }
    }
  }
$netCore = $netCore | Sort-Object Version -Descending
$best = $netCore | Where-Object { $_.Version.Major -eq 10 } | Select-Object -First 1
if (-not $best) { $best = $netCore | Select-Object -First 1 }
if (-not $best) { throw "No Microsoft.NETCore.App runtime found via 'dotnet --list-runtimes'" }
$FW = Join-Path $best.Dir $best.Version.ToString()
# Real, non-facade split assemblies (13-stage2-log.md section 1: non-empty GetExportedTypes()
# via Assembly.LoadFrom) covering the common BCL surface ordinary Nemerle programs touch.
$CoreRefs = @(
  "System.Collections.dll", "System.Console.dll", "System.Collections.Specialized.dll",
  "System.Linq.dll", "System.Diagnostics.Process.dll", "System.Private.Uri.dll",
  "System.Diagnostics.TraceSource.dll", "System.Security.Cryptography.dll",
  "System.Private.Xml.dll", "System.Private.Xml.Linq.dll", "System.Data.Common.dll"
)
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("-no-color")
$lines.Add("-no-stdlib")
$lines.Add("-greedy-references:-")
$lines.Add("-use-loaded-corlib")
$lines.Add("-ref:mscorlib")
$lines.Add("-ref:System")
foreach ($r in $CoreRefs) { $lines.Add('-ref:"' + (Join-Path $FW $r) + '"') }
$lines.Add('-ref:"' + (Join-Path $Here 'Nemerle.dll') + '"')
Set-Content -Path (Join-Path $Here 'ncc.default.rsp') -Value $lines -Encoding utf8
'@
Set-Content -Path $GenPath -Value $genScript -Encoding utf8
Write-Host "Wrote $GenPath"

# Produce the initial ncc.default.rsp for immediate use on THIS machine.
& $GenPath
$RspPath = Join-Path $OutDir "ncc.default.rsp"
Write-Host "Wrote $RspPath"

# ---------------------------------------------------------------------------
# 3. Convenience wrapper: `<OutDir>\ncc.cmd -out:foo.exe foo.n [args...]` runs the
#    compiler with the default rsp pre-pended, then (best-effort) copies this layout's
#    Nemerle*.dll into the CURRENT directory so the produced program can actually run
#    (see header comment -- run-time stdlib dependency, not a compiler bug). A plain
#    .cmd batch file, NOT a .ps1 -- see the header comment for why a PowerShell wrapper
#    cannot reliably forward ncc's `-name:value` switches.
# ---------------------------------------------------------------------------
$WrapperPath = Join-Path $OutDir "ncc.cmd"
$wrapperContent = @"
@echo off
rem Convenience wrapper generated by dotnet-port\pack-tool.ps1. Runs this directory's
rem ncc.dll with the standard reference set (ncc.default.rsp) pre-pended, then copies
rem Nemerle*.dll into the current directory on success (see pack-tool.ps1 header for why).
rem ncc.default.rsp holds ABSOLUTE -ref: paths, so if this folder was copied/moved (or was
rem never packed on this machine) they are stale; we regenerate it via gen-default-rsp.ps1
rem whenever its baked Nemerle.dll path no longer points at THIS folder.
setlocal
set "HERE=%~dp0"
where pwsh >nul 2>&1 && (set "PS=pwsh") || (set "PS=powershell")
set "NEED_GEN="
if not exist "%HERE%ncc.default.rsp" set "NEED_GEN=1"
if not defined NEED_GEN findstr /C:"%HERE%Nemerle.dll" "%HERE%ncc.default.rsp" >nul 2>&1 || set "NEED_GEN=1"
if defined NEED_GEN "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%HERE%gen-default-rsp.ps1" >nul || echo [ncc] warning: failed to regenerate ncc.default.rsp 1>&2
dotnet "%HERE%ncc.dll" "-from-file:%HERE%ncc.default.rsp" %*
set "EXITCODE=%ERRORLEVEL%"
if "%EXITCODE%"=="0" copy /y "%HERE%Nemerle*.dll" . >nul 2>&1
exit /b %EXITCODE%
"@
Set-Content -Path $WrapperPath -Value $wrapperContent -Encoding ascii
Write-Host "Wrote $WrapperPath"

Write-Host ""
Write-Host "Layout complete -> $OutDir"
Get-ChildItem $OutDir | Format-Table Name, Length
Write-Host ""
Write-Host "Smoke test:"
Write-Host "  dotnet `"$OutDir\ncc.dll`" -from-file:`"$RspPath`" -out:hello.exe hello.n && dotnet exec hello.exe"
Write-Host "  (or)  `"$WrapperPath`" -out:hello.exe hello.n && dotnet exec hello.exe"
