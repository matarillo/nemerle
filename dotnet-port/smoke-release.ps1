# Consumer smoke test of a built release set: installs the Templates package from the feed,
# scaffolds a console project, builds it with the packaged compiler, and runs it. This is the
# only check that proves the shipped nupkgs actually install / build / run for a consumer -- the
# unit and integration suites stop at MSBuild evaluation (they never compile or run a project
# against the packaged compiler), so a package that resolves correctly but is missing a runtime
# assembly from its closure would pass them and fail here. See 46-prerelease-wp-n6-log.md.
#
# Prerequisites: the release nupkgs already in the feed (Nemerle.Templates.Unofficial /
# Nemerle.Sdk.Unofficial), the .NET 10 SDK, and `git` is not needed. Windows or Linux.
#
# Usage:
#   pwsh dotnet-port/smoke-release.ps1                                   # feed = dotnet-port/dist/release
#   pwsh dotnet-port/smoke-release.ps1 -Feed <dir> -Version 1.2.635-preview.1

param(
    # Folder feed holding the .nupkg files. Default: the release set pack-tool.ps1 / pack-release.ps1 write.
    [string]$Feed = "",

    # Package version to install/pin. Default: derived from the Templates nupkg in the feed.
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

if ($Feed -eq "") { $Feed = Join-Path $PSScriptRoot "dist/release" }
if (-not (Test-Path $Feed)) { throw "Feed not found: $Feed. Build the release set first (build-from-boot.ps1 or pack-tool.ps1 -Pack)." }
$Feed = (Resolve-Path $Feed).Path

if ($Version -eq "") {
    $tpl = Get-ChildItem -Path $Feed -Filter "Nemerle.Templates.Unofficial.*.nupkg" -File | Select-Object -First 1
    if (-not $tpl) { throw "No Nemerle.Templates.Unofficial.*.nupkg in feed '$Feed'." }
    $Version = $tpl.BaseName.Substring("Nemerle.Templates.Unofficial.".Length)
}
Write-Host "Smoke-testing release set: feed=$Feed, version=$Version"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("nemerle-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

# <clear /> so the build resolves the Nemerle SDK ONLY from the release set: a hermetic check that
# does not depend on nuget.org, and correct for a bare Nemerle console (Microsoft.NET.Sdk ships
# with the installed .NET SDK, not via NuGet, so nothing else needs restoring).
Set-Content -Path (Join-Path $work "NuGet.config") -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nemerle-local" value="$Feed" />
  </packageSources>
</configuration>
"@

# dotnet new install is per-user/global; uninstall in finally so repeated runs stay clean.
$templatesInstalled = $false
try {
    & dotnet new install "Nemerle.Templates.Unofficial::$Version" --add-source $Feed
    if ($LASTEXITCODE -ne 0) { throw "dotnet new install failed (exit $LASTEXITCODE)" }
    $templatesInstalled = $true

    Push-Location $work
    try {
        # The template bakes the release version as its default sdkVersion at pack time, so the
        # scaffolded project pins exactly the version under test -- the same path 41 section 7.7 took.
        & dotnet new nemerle-console -n SmokeApp
        if ($LASTEXITCODE -ne 0) { throw "dotnet new nemerle-console failed (exit $LASTEXITCODE)" }

        Push-Location (Join-Path $work "SmokeApp")
        try {
            & dotnet build
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
            $out = & dotnet run --no-build
            if ($LASTEXITCODE -ne 0) { throw "dotnet run failed (exit $LASTEXITCODE)" }
        }
        finally { Pop-Location }
    }
    finally { Pop-Location }

    $expected = "Hello from Nemerle on .NET 10!"
    if (($out -join "`n") -notmatch [regex]::Escape($expected)) {
        throw "dotnet run output did not contain '$expected'. Got:`n$($out -join "`n")"
    }
    Write-Host "Smoke test PASS: install -> new -> build -> run -> '$expected'"
}
finally {
    if ($templatesInstalled) { & dotnet new uninstall Nemerle.Templates.Unofficial *> $null }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
