# Verifies the promise that dotnet-port\samples\README.md makes: the samples it walks a reader
# through actually build with this checkout's compiler.
#
# Why this is NOT EnsureFixturesBuilt (LspServer.IntegrationTest\Program.cs):
#   That list answers a test prerequisite -- "the raw LSP suite evaluates these projects with
#   MSBuild -getItem, which does not compile ProjectReferences, so MathLib.dll / SokobanMacros.dll
#   / restored packages must already exist on disk". Membership there is decided by what the
#   suite needs, not by what the README advertises. Overloading it with "and also these, because
#   we promised a reader they work" would make each entry's reason unrecoverable for the next
#   person. Hence a separate gate with a single, stated meaning.
#
# Why an expected-to-fail entry instead of an exclusion:
#   CompTimeSolver\Fail exists to demonstrate that an unsolvable maze is a *compile error* -- the
#   macro runs a breadth-first search at compile time and calls Message.Error when it finds no
#   path. Merely skipping it would let a regression that makes it compile go unnoticed, and the
#   README's central claim about that sample would silently become false. So it is checked in the
#   direction it is supposed to fail, including the message.
#
# Why every .nproj must be classified:
#   The lists below have to cover samples\**\*.nproj exhaustively. A new sample directory
#   therefore fails this script until someone says which kind it is, instead of quietly being
#   advertised without a gate (or gated without being advertised).
#
# Usage:
#   pwsh -NoProfile -File dotnet-port\build-samples.ps1
#   pwsh -NoProfile -File dotnet-port\build-samples.ps1 -Configuration Release

param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$DotnetPortDir = $PSScriptRoot
$SamplesDir = Join-Path $DotnetPortDir "samples"

# Entry points a reader is told to build or run. Their macro libraries come along as
# ProjectReferences, so they are not listed separately here (see $Transitive).
$MustBuild = @(
    "HelloCore/HelloCore.nproj"
    "SyntaxMacro/SyntaxDemo/SyntaxDemo.nproj"
    "Latin/LatinDemo/LatinDemo.nproj"
    "SyntaxTree/SyntaxTreeDemo/SyntaxTreeDemo.nproj"
    "CompTimeSolver/Success/Success.nproj"
    "Sokoban/Sokoban/Sokoban.nproj"
)

# Must fail, and for the stated reason.
$MustFail = @{
    "CompTimeSolver/Fail/Fail.nproj" = "This maze cannot be solved"
}

# Built as a dependency of something in $MustBuild; a direct build would be redundant.
$Transitive = @(
    "SyntaxMacro/SyntaxMacros/SyntaxMacros.nproj"
    "Latin/LatinSyntax/LatinSyntax.nproj"
    "SyntaxTree/SyntaxTreeMacros/SyntaxTreeMacros.nproj"
    "CompTimeSolver/Maze/Maze.nproj"
    "Sokoban/SokobanMacros/SokobanMacros.nproj"
)

# Test fixtures: not in the README, so nothing is promised about them here. The raw LSP suite
# is what exercises these.
$Fixtures = @(
    "RefDemo/App/App.nproj"
    "RefDemo/MathLib/MathLib.nproj"
    "PackageReference/PackageReference.nproj"
    "Defines/Defines.nproj"
    "Warnings/Warnings.nproj"
)

function Get-RelativeSamplePath([string]$FullPath) {
    $relative = [System.IO.Path]::GetRelativePath($SamplesDir, $FullPath)
    return $relative -replace '\\', '/'
}

# --- classification completeness -------------------------------------------------------------

$known = @($MustBuild) + @($MustFail.Keys) + @($Transitive) + @($Fixtures)
$onDisk = Get-ChildItem -Path $SamplesDir -Recurse -Filter *.nproj |
    ForEach-Object { Get-RelativeSamplePath $_.FullName }

$unclassified = $onDisk | Where-Object { $known -notcontains $_ }
if ($unclassified.Count -gt 0) {
    throw ("These sample projects are not classified in build-samples.ps1. Add each to " +
           "`$MustBuild (advertised in samples/README.md), `$MustFail, `$Transitive or " +
           "`$Fixtures:`n  " + ($unclassified -join "`n  "))
}

$missing = $known | Where-Object { $onDisk -notcontains $_ }
if ($missing.Count -gt 0) {
    throw ("build-samples.ps1 lists sample projects that do not exist:`n  " + ($missing -join "`n  "))
}

Write-Host "Classified $($onDisk.Count) sample projects: $($MustBuild.Count) advertised, " +
           "$($MustFail.Count) expected-to-fail, $($Transitive.Count) transitive, $($Fixtures.Count) fixtures."

# --- the advertised samples must build --------------------------------------------------------

foreach ($relative in $MustBuild) {
    $project = Join-Path $SamplesDir $relative
    Write-Host "Building $relative ..."
    dotnet build $project -c $Configuration --nologo -v:quiet -t:Rebuild
    if ($LASTEXITCODE -ne 0) {
        throw "samples/README.md advertises $relative, but it failed to build."
    }
}

# --- and the counter-example must not ---------------------------------------------------------

foreach ($relative in $MustFail.Keys) {
    $project = Join-Path $SamplesDir $relative
    $expected = $MustFail[$relative]
    Write-Host "Building $relative (expected to fail) ..."
    $output = dotnet build $project -c $Configuration --nologo -v:quiet -t:Rebuild 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        throw ("$relative is supposed to fail to compile -- it demonstrates that an unsolvable " +
               "maze is a compile error -- but it built successfully.")
    }
    if ($output -notmatch [regex]::Escape($expected)) {
        throw ("$relative failed to build as expected, but not for the advertised reason: " +
               "'$expected' was not reported.`n$output")
    }
}

Write-Host "All samples advertised in samples/README.md build; the counter-example fails as documented."
