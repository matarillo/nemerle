# Copies the CoreCLR-only emission helper (Nemerle.CoreEmit.dll) next to the Stage1
# ncc.exe, and makes sure ncc.exe has a runtimeconfig.json so `dotnet exec` can start
# it on .NET 10. Run this after every Stage1 rebuild.
#
# Usage: pwsh dotnet-port\refresh-stage1-core.ps1 [-Configuration Release]

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Stage1Dir = Join-Path $RepoRoot "bin/$Configuration/net-4.0/Stage1"
$HelperProj = Join-Path $RepoRoot "dotnet-port/Nemerle.CoreEmit/Nemerle.CoreEmit.csproj"
$HelperBin = Join-Path $RepoRoot "dotnet-port/Nemerle.CoreEmit/bin/$Configuration/net10.0/Nemerle.CoreEmit.dll"

if (-not (Test-Path $Stage1Dir)) {
    throw "Stage1 output dir not found: $Stage1Dir (build Stage1 first)"
}

Write-Host "Building Nemerle.CoreEmit ($Configuration)..."
dotnet build -c $Configuration $HelperProj --nologo -v:quiet
if ($LASTEXITCODE -ne 0) { throw "Nemerle.CoreEmit build failed" }

if (-not (Test-Path $HelperBin)) {
    throw "Built helper dll not found at $HelperBin"
}

Copy-Item -Path $HelperBin -Destination $Stage1Dir -Force
Write-Host "Copied Nemerle.CoreEmit.dll -> $Stage1Dir"

$RuntimeConfigPath = Join-Path $Stage1Dir "ncc.runtimeconfig.json"
if (-not (Test-Path $RuntimeConfigPath)) {
    @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
    "rollForward": "LatestMinor"
  }
}
'@ | Set-Content -Path $RuntimeConfigPath -Encoding utf8
    Write-Host "Wrote $RuntimeConfigPath"
} else {
    Write-Host "$RuntimeConfigPath already exists, leaving as-is"
}

Write-Host "Done."
