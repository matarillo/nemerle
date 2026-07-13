# WP-L4: runs the raw stdio LSP integration scenarios (WP-L3 suite:
# HelloCore / RefDemo / PackageReference / Sokoban / buffer-disk-close-stale)
# against the language server EXTRACTED FROM THE VSIX, proving the bundled
# server/ payload runs without the repository's LspServer bin directory.
#
# The test fixtures themselves still live in this repository and are built with
# dist/ncc + Nemerle.Core.targets -- that is the documented developer-preview
# toolchain split: the VSIX bundles the LSP server, not the compiler toolchain.
#
# Usage (after `npm run package`):
#   pwsh dotnet-port\vscode-nemerle\test-bundled-server.ps1
#   pwsh dotnet-port\vscode-nemerle\test-bundled-server.ps1 -VsixPath <file> -NoBuild

param(
    [string]$VsixPath = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$ExtensionDir = $PSScriptRoot
$DotnetPortDir = Split-Path -Parent $ExtensionDir

if ($VsixPath -eq "") {
    $manifest = Get-Content (Join-Path $ExtensionDir "package.json") -Raw | ConvertFrom-Json
    $VsixPath = Join-Path $ExtensionDir "vscode-nemerle-$($manifest.version).vsix"
}
if (-not (Test-Path $VsixPath)) { throw "VSIX not found: $VsixPath (run 'npm run package' first)" }

$IntegrationTestProj = Join-Path $DotnetPortDir "LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj"
$IntegrationTestDll = Join-Path $DotnetPortDir "LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll"
if (-not $NoBuild) {
    & dotnet build $IntegrationTestProj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Integration test build failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path $IntegrationTestDll)) { throw "Integration test not built: $IntegrationTestDll" }

# Expand-Archive needs a .zip extension; a VSIX is a zip archive.
$ExtractRoot = Join-Path $ExtensionDir ".vscode-test\vsix-extract"
if (Test-Path $ExtractRoot) { Remove-Item -Recurse -Force $ExtractRoot }
New-Item -ItemType Directory -Force -Path $ExtractRoot | Out-Null
$ZipCopy = Join-Path $ExtractRoot "vsix.zip"
Copy-Item $VsixPath $ZipCopy
Expand-Archive -Path $ZipCopy -DestinationPath (Join-Path $ExtractRoot "content")

$BundledServer = Join-Path $ExtractRoot "content\extension\server\Nemerle.LanguageServer.dll"
if (-not (Test-Path $BundledServer)) { throw "The VSIX does not contain extension/server/Nemerle.LanguageServer.dll" }

Write-Host "Running the raw LSP integration suite against the extracted bundled server:"
Write-Host "  $BundledServer"
& dotnet exec $IntegrationTestDll $BundledServer
exit $LASTEXITCODE
