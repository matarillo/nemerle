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

            # WP-O3 (dotnet-port/docs/50-wp-o3-log.md) D1 guard. The template builds with
            # GenerateDependencyFile at the SDK default (true), which used to be forced off because
            # the Nemerle runtime was absent from any generated deps.json. Prove the root fix two
            # ways so a regression cannot pass silently:
            #   (a) a deps.json IS generated and lists the runtime closure (Nemerle.dll), and
            #   (b) a program that actually EXERCISES the runtime at run time -- not just
            #       System.Console -- loads it through that deps.json and runs.
            # The stock template's Main only calls Console.WriteLine, so on its own it would run
            # even with a broken closure; (b) replaces it with runtime-using code to close that gap.
            $depsJson = Get-ChildItem -Path (Join-Path (Get-Location) "bin") -Recurse -Filter "SmokeApp.deps.json" -File | Select-Object -First 1
            if (-not $depsJson) {
                throw "No SmokeApp.deps.json was generated -- GenerateDependencyFile is not defaulting to true for SDK consumers (WP-O3 regression)."
            }
            $depsText = Get-Content -Raw -Path $depsJson.FullName
            if ($depsText -notmatch 'Nemerle\.dll') {
                throw "SmokeApp.deps.json does not list Nemerle.dll -- the runtime closure is not flowing into deps.json (WP-O3 regression).`n$depsText"
            }

            Set-Content -Path (Join-Path (Get-Location) "Program.n") -Encoding utf8 -Value @(
                'using System;'
                'using System.Console;'
                ''
                'module Program'
                '{'
                '  Main() : void'
                '  {'
                '    def parts = ["Nemerle", "runtime", "OK"];'
                '    WriteLine(string.Join(" ", parts));'
                '  }'
                '}'
            )
            & dotnet build
            if ($LASTEXITCODE -ne 0) { throw "dotnet build (runtime-exercising variant) failed (exit $LASTEXITCODE)" }
            $rtOut = & dotnet run --no-build
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet run (runtime-exercising variant) failed (exit $LASTEXITCODE) -- the Nemerle runtime did not load from deps.json (WP-O3 regression)."
            }
            if (($rtOut -join "`n") -notmatch 'Nemerle runtime OK') {
                throw "runtime-exercising variant printed '$($rtOut -join "`n")', expected 'Nemerle runtime OK'."
            }
        }
        finally { Pop-Location }
    }
    finally { Pop-Location }

    $expected = "Hello from Nemerle on .NET 10!"
    if (($out -join "`n") -notmatch [regex]::Escape($expected)) {
        throw "dotnet run output did not contain '$expected'. Got:`n$($out -join "`n")"
    }
    Write-Host "Smoke test PASS: install -> new -> build -> run -> '$expected'"
    Write-Host "WP-O3 PASS: deps.json lists the Nemerle runtime, and a runtime-using program runs with GenerateDependencyFile default (true)."
}
finally {
    if ($templatesInstalled) { & dotnet new uninstall Nemerle.Templates.Unofficial *> $null }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
