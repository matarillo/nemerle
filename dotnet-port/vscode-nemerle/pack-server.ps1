# WP-L4: stages the .NET 10 Nemerle language server into this extension's server/
# directory so `vsce package` produces a VSIX that runs without a repository checkout.
#
# The packaging unit is the LspServer project's ENTIRE bin\<Configuration>\net10.0\
# output directory (design rule from dotnet-port\docs\24-vscode-development-plan.md §7 and
# 28-vscode-packaging-log.md): it already contains Nemerle.dll / Nemerle.Compiler.dll /
# Nemerle.Macros.dll (HintPath references with Private=true), Nemerle.Compiler.Utils.dll,
# Nemerle.ProjectInfo.dll, the OmniSharp.*/MediatR protocol stack with its runtimes\ and
# satellite-resource subdirectories, Nemerle.LanguageServer.deps.json, and the hand-written
# Nemerle.LanguageServer.runtimeconfig.json (the project disables implicit framework
# references, so the SDK cannot generate one).  Nemerle.CoreEmit.dll is deliberately NOT
# part of the closure: CoreEmitBridge only Assembly.LoadFrom()s it on emission paths
# (CreateBuilder/Save), which the analysis-only LSP engine never executes -- verified
# against macro-heavy fixtures in 28-vscode-packaging-log.md.
#
# The .NET 10 runtime itself is intentionally NOT bundled; the extension checks
# `dotnet --list-runtimes` before launching and reports an actionable error.
#
# Version hazard note: Nemerle assembly versions derive from `git describe`.  Package the
# server from the same commit as the dist/ncc layout the workspace builds with, or mixed
# generations can fail with FileLoadException.  bundle-info.json records the commit for
# troubleshooting.
#
# Usage:
#   pwsh dotnet-port\vscode-nemerle\pack-server.ps1                 # build + stage
#   pwsh dotnet-port\vscode-nemerle\pack-server.ps1 -NoBuild        # stage an existing build

param(
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$ExtensionDir = $PSScriptRoot
$DotnetPortDir = Split-Path -Parent $ExtensionDir
$RepoRoot = Split-Path -Parent $DotnetPortDir
$ServerProject = Join-Path $DotnetPortDir "LspServer/Nemerle.LanguageServer.csproj"
$ServerBinDir = Join-Path $DotnetPortDir "LspServer/bin/$Configuration/net10.0"
$OutDir = Join-Path $ExtensionDir "server"

$NccLayout = Join-Path $DotnetPortDir "dist/ncc"
if (-not (Test-Path (Join-Path $NccLayout "Nemerle.Compiler.dll"))) {
    throw "dist/ncc layout not found ($NccLayout): run dotnet-port/pack-tool.ps1 first (the server build references its Nemerle assemblies)."
}

if (-not $NoBuild) {
    Write-Host "Building $ServerProject ($Configuration) ..."
    & dotnet build $ServerProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Language server build failed (exit $LASTEXITCODE)" }
}

# Core closure sanity check.  The authoritative full check (including third-party
# notice coverage) is `npm run verify-server` (test/tools/verifyBundledServer.ts).
$required = @(
    "Nemerle.LanguageServer.dll",
    "Nemerle.LanguageServer.runtimeconfig.json",
    "Nemerle.LanguageServer.deps.json",
    "Nemerle.dll",
    "Nemerle.Compiler.dll",
    "Nemerle.Macros.dll",
    "Nemerle.Compiler.Utils.dll",
    "Nemerle.ProjectInfo.dll",
    "OmniSharp.Extensions.JsonRpc.dll",
    "OmniSharp.Extensions.LanguageProtocol.dll",
    "OmniSharp.Extensions.LanguageServer.dll",
    "OmniSharp.Extensions.LanguageServer.Shared.dll",
    "MediatR.dll",
    "Newtonsoft.Json.dll",
    "Nerdbank.Streams.dll",
    "System.Reactive.dll"
)
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $ServerBinDir $name))) {
        throw "Missing '$name' in $ServerBinDir -- not a complete language server build output."
    }
}
if (Test-Path (Join-Path $ServerBinDir "Nemerle.CoreEmit.dll")) {
    Write-Host "note: Nemerle.CoreEmit.dll present in the build output; it will be staged although analysis does not need it."
}

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item -Path (Join-Path $ServerBinDir "*") -Destination $OutDir -Recurse -Force

# Record provenance for troubleshooting mixed-generation installs.
Push-Location $RepoRoot
try {
    $commit = (& git rev-parse HEAD).Trim()
    # WP-N4 tag contract: annotated-only describe (no --tags), so release/seed lightweight tags
    # are already invisible here -- --match 'v[0-9]*' is defense in depth, so an accidentally
    # annotated release/seed tag still can't shift this provenance string (= the VSIX's bytes).
    $describe = (& git describe --long --always --dirty --match 'v[0-9]*').Trim()
}
finally {
    Pop-Location
}
if ($describe -match '-dirty$') {
    Write-Warning "Working tree is dirty; bundle-info.json records '$describe'."
}
$bundleInfo = [ordered]@{
    commit        = $commit
    describe      = $describe
    configuration = $Configuration
    sourceDir     = "dotnet-port/LspServer/bin/$Configuration/net10.0"
    packedAtUtc   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
$bundleInfo | ConvertTo-Json | Set-Content -Path (Join-Path $OutDir "bundle-info.json") -Encoding utf8

$files = Get-ChildItem $OutDir -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "Staged $($files.Count) files ($totalMb MB) -> $OutDir"
Write-Host "Next: cd dotnet-port/vscode-nemerle; npm run package"
