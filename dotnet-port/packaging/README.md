# Nemerle for .NET 10 — install and use (unofficial)

This directory holds the distributable packages of an **unofficial** .NET 10 port of the
[Nemerle](https://github.com/rsdn/nemerle) compiler, and this page explains how to install and
use them.

> **This is not a release of the Nemerle project.** It is a personal port
> (<https://github.com/matarillo/nemerle>) and the Nemerle team does not support it. The `.Unofficial`
> suffix on every package ID is there to keep that unambiguous. The Nemerle project's own packages
> are `Nemerle`, `Nemerle.Compiler`, and friends.
>
> It is also a **preview**: verified against the port's own samples and a local feed, not broadly
> in the wild.

| Artifact | What it is |
|---|---|
| `Nemerle.Sdk.Unofficial.<version>.nupkg` | An MSBuild project SDK. Carries the `ncc` compiler and the MSBuild task that runs it, so `dotnet build` compiles `.n` sources. This is the only package a project needs. |
| `Nemerle.Templates.Unofficial.<version>.nupkg` | `dotnet new` templates (`nemerle-console`, `nemerle-classlib`). Convenience only — you can write the project file by hand instead. |
| `Nemerle.Linq.Unofficial.<version>.nupkg` | The Nemerle LINQ macro library (`linq <# … #>` query syntax, `ToExpression`, lambda-to-expression-tree conversion). Optional; add as an ordinary `PackageReference`. |
| `vscode-nemerle-<version>.vsix` | VS Code support (optional): highlighting plus project-aware diagnostics, hover, completion, go-to-definition. |
| `release-info.json` | Records the commit every artifact here was built from, and their versions. |

There is no need to clone this repository to use any of them.

> **Why the version numbers differ.** The `.vsix` and the `.nupkg` carry different version
> numbers on purpose: the extension's tracks its editor features, the packages' tracks the
> Nemerle compiler generation inside them, and the two move at different rates (four extension
> releases shipped on one compiler generation). What ties them together is the release they came
> from — see `release-info.json`, and the version check described under *Editor support*. Take
> the whole set from one release rather than mixing.

## Requirements

- The **.NET 10 SDK** (<https://dotnet.microsoft.com/download/dotnet/10.0>).
- Windows or Linux. Both are verified against this release set — Windows 11 in daily use, and
  Ubuntu Linux on a clean VM (install → `dotnet new nemerle-console` → build → run, plus the
  VS Code extension's test suites). The packages contain only managed IL and the same package
  works on both.

## 1. Install

The packages are published as GitHub release assets rather than to nuget.org, so installing them
means pointing NuGet at a folder on your disk. NuGet calls that a *local feed*, and it is an
ordinary directory containing `.nupkg` files — nothing needs to be extracted or unpacked.

### 1.1 Put the packages somewhere

Download the release from the [release page](https://github.com/matarillo/nemerle/releases) and
keep the folder somewhere permanent. Any path works; these are just examples:

```text
C:\nemerle-packages\                     ~/nemerle-packages/
  Nemerle.Sdk.Unofficial.__NEMERLE_SDK_VERSION__.nupkg
  Nemerle.Templates.Unofficial.__NEMERLE_SDK_VERSION__.nupkg
  Nemerle.Linq.Unofficial.__NEMERLE_SDK_VERSION__.nupkg
  vscode-nemerle-__NEMERLE_VSIX_VERSION__.vsix
  README.md
  release-info.json
```

The extra files are harmless: a folder feed is just a directory NuGet scans for `*.nupkg`, and it
ignores everything else. So the folder you extract *is* the feed — no separate step.

### 1.2 Make the feed visible to NuGet

Pick one of these. The project-local file is easier to share with collaborators and to delete
later; the machine-wide source saves repeating it per project.

**Option A — a `NuGet.config` beside your project** (or in any parent directory, e.g. next to
your solution):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nemerle-local" value="C:\nemerle-packages" />
  </packageSources>
</configuration>
```

A relative `value` is resolved against the `NuGet.config`'s own location, so `value="..\packages"`
works and keeps the file portable across machines. On Linux, use the folder's absolute path
(`value="/home/you/nemerle-packages"`) or a relative one — `~` is not expanded inside
`NuGet.config`.

> Do **not** add `<clear />` here unless you mean it. It drops nuget.org, and then any ordinary
> `PackageReference` in your project stops resolving. The example above *adds* the local feed to
> whatever sources you already have.

**Option B — register the feed machine-wide**, once:

```console
dotnet nuget add source C:\nemerle-packages -n nemerle-local     # Windows
dotnet nuget add source ~/nemerle-packages -n nemerle-local      # Linux
```

Undo it later with `dotnet nuget remove source nemerle-local`.

### 1.3 Install the templates (optional)

```console
dotnet new install Nemerle.Templates.Unofficial::__NEMERLE_SDK_VERSION__ --add-source C:\nemerle-packages
```

(On Linux: `--add-source ~/nemerle-packages` — here the shell does expand `~`.)

`--add-source` is only needed for this command; `dotnet new install` does not read the
`NuGet.config` above. Templates are installed for your user account, not per project — remove them
with `dotnet new uninstall Nemerle.Templates.Unofficial`.

## 2. Create a project

### With the templates

```console
dotnet new nemerle-console -n MyApp
cd MyApp
dotnet build
dotnet run
```

```text
Hello from Nemerle on .NET 10!
```

`dotnet new nemerle-classlib` creates a library instead. Both accept `--sdkVersion <version>` to
pin a different SDK version than the one they were packed alongside.

### By hand

The templates save typing, nothing more. A complete Nemerle project is:

```xml
<!-- MyApp.nproj -->
<Project Sdk="Nemerle.Sdk.Unofficial/__NEMERLE_SDK_VERSION__">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

```n
// Program.n
using System.Console;

module Program
{
  Main() : void
  {
    WriteLine("Hello from Nemerle on .NET 10!");
  }
}
```

**The file extension must be `.nproj`, not `.csproj`.** The .NET SDK picks its language targets
from the project's extension, so a `.csproj` imports C#'s compiler and csc tries to compile your
Nemerle sources. The SDK detects this and stops with an explicit error rather than letting csc
produce confusing ones.

`**/*.n` is collected automatically, the way `Microsoft.NET.Sdk` collects `**/*.cs`.

### Pinning the version in `global.json` instead

To keep the version out of every project file, drop it from the `Sdk` attribute and pin it once:

```json
{ "msbuild-sdks": { "Nemerle.Sdk.Unofficial": "__NEMERLE_SDK_VERSION__" } }
```

```xml
<Project Sdk="Nemerle.Sdk.Unofficial">
```

## 3. Using the SDK

### References

`ProjectReference` and `PackageReference` work as they do in C#:

```xml
<ItemGroup>
  <ProjectReference Include="..\MyLib\MyLib.nproj" />
  <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
</ItemGroup>
```

### LINQ (optional package)

`Nemerle.Linq.Unofficial` adds the `linq <# … #>` query syntax, the `ToExpression` macro and
implicit lambda-to-expression-tree conversion. It is an ordinary package reference — use the
same version as the SDK (both track the compiler generation):

```xml
<ItemGroup>
  <PackageReference Include="Nemerle.Linq.Unofficial" Version="__NEMERLE_SDK_VERSION__" />
</ItemGroup>
```

```n
using Nemerle.Linq;

def evens = linq <# from x in [3, 1, 4, 1, 5] where x % 2 == 0 select x #>;
```

### Macro libraries

A project that **defines** macros needs the compiler's own API at compile time. Ask for it with
`NemerleMacroLibrary`; the SDK knows where the compiler is and you do not have to (it lives inside
the resolved package, in a path specific to your machine):

```xml
<!-- MyMacros.nproj -->
<Project Sdk="Nemerle.Sdk.Unofficial/__NEMERLE_SDK_VERSION__">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <NemerleMacroLibrary>true</NemerleMacroLibrary>
  </PropertyGroup>
</Project>
```

A project that **uses** those macros references the macro library as macro-only, so its macros are
expanded at compile time without its types entering scope or its assembly being copied to the
output:

```xml
<ItemGroup>
  <ProjectReference Include="..\MyMacros\MyMacros.nproj"
                    OutputItemType="NemerleMacroReference"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

### Properties

| Property | Default | Meaning |
|---|---|---|
| `EnableDefaultNemerleCompileItems` | `true` | Collect `**/*.n` into `@(NemerleCompile)`. Set `false` to list sources by hand. |
| `NemerleMacroLibrary` | `false` | This project defines macros; reference the compiler API. |
| `NemerleAdditionalOptions` | (empty) | Extra `ncc` switches, passed verbatim (e.g. `-nowarn:10003`). |
| `NemerleUseExec` | `false` | Run `ncc` out-of-process instead of in-process. A fallback; the in-process path gives structured diagnostics. |
| `NccLayoutDir` | the package's `tools/ncc/` | Point at a different compiler layout. |

`DefineConstants` reaches `ncc` as `-define:`, and `DebugType` / `DebugSymbols` control Portable
PDB emission, so `#if` and debugging behave as they do in C# projects.

Sources can also be listed explicitly; explicit items win and the default glob fills in the rest,
so an existing project that lists its sources keeps working as-is when converted to this SDK.

## 4. Editor support (optional)

`vscode-nemerle-<version>.vsix` adds `.n` syntax highlighting plus project-aware diagnostics,
hover, completion, and go-to-definition in VS Code:

```console
code --install-extension vscode-nemerle-__NEMERLE_VSIX_VERSION__.vsix
```

(Or in VS Code: **Extensions** → `...` → *Install from VSIX…*, which needs no `code` on your
PATH.) Then open a **trusted** folder containing your `.nproj`. See the extension's own README
for details.

**Use the VSIX and the packages from the same release.** Nemerle assembly versions track the
compiler generation, so mixing generations can fail at load time. Three things guard this, and
you do not have to do any of them by hand:

- `release-info.json` records the single commit every artifact in this folder was built from
  (the release is refused if they disagree).
- The extension's language server compares its own Nemerle assemblies against the toolchain your
  project builds with, and shows a notification naming both versions if they differ.
- Each half carries its own provenance for inspection: `server/bundle-info.json` inside the
  VSIX, `tools/ncc/ncc-info.json` inside the SDK package.

The extension is not auto-updated from here; installing a newer VSIX replaces it.

## 5. Updating and removing

- **Update**: download the new `.nupkg` into the same folder, bump the version in your project's
  `Sdk` attribute (or `global.json`), and re-install the templates at the new version. Old versions
  can be deleted from the folder once nothing references them.
- **Remove**: `dotnet new uninstall Nemerle.Templates.Unofficial`, delete any `NuGet.config` entry
  or run `dotnet nuget remove source nemerle-local`, and delete the packages folder.

NuGet caches a package by `(id, version)` after first use, so **replacing a `.nupkg` without
changing its version will not take effect** — the extracted copy under `~/.nuget/packages` wins.
Released versions never change, so this only bites if you build packages yourself.

## 6. Troubleshooting

- **`Unable to find SDK 'Nemerle.Sdk.Unofficial'`** — NuGet cannot see the feed, or the version
  does not match a `.nupkg` in it. Check that the `NuGet.config` is beside or above the project
  (not somewhere else), that the path in it points at the folder holding the `.nupkg` files, and
  that the version in the `Sdk` attribute matches the file name exactly, prerelease label included.
- **csc errors (`CS…`) on Nemerle sources, or `error CS5001`** — the project file is named
  `.csproj`. Rename it to `.nproj`.
- **`NU1101` / a `PackageReference` stops resolving after adding the feed** — the `NuGet.config`
  has a `<clear />` that dropped nuget.org. Remove it.
- **`Assets file … project.assets.json not found`** — run `dotnet restore` (or `dotnet build`,
  which restores).
- **`Duplicate @(NemerleCompile) items`** — one source is listed twice, usually by two overlapping
  `ItemGroup`s of your own (an explicit `*.n` next to a named file). The message names the files.
  Listing sources by hand is fine on its own: the default glob yields to whatever the project
  already declared, so it does not double up.
- **`Nemerle toolchain/language server version mismatch`** — the VS Code extension and the SDK
  package come from different releases. Install matching ones.
- **`FileNotFoundException: Nemerle` at run time** — the Nemerle runtime assemblies are copied next
  to your output rather than resolved through the package graph, which requires
  `GenerateDependencyFile` to stay `false` (the SDK's default). If you set it to `true`, the
  generated `deps.json` will not list them and the host will ignore them.

## Known limitations

- `.csproj` is not usable; use `.nproj` (see above).
- `GenerateDependencyFile` defaults to `false` and should stay that way (see above).
- Preview quality: local-feed / GitHub-release distribution only, not published to nuget.org.

## License

BSD-3-Clause. Copyright (c) 2003-2008 The University of Wroclaw. Copyright (c) 2008-2014 Nemerle
Project Team. Each package embeds its own README and license metadata; no third-party assemblies
are redistributed in them.

---

### For maintainers

A release set is produced from a clean checkout by, in order:

```powershell
pwsh dotnet-port/build-libs-core.ps1               # auxiliary libs (Nemerle.Linq) -> bin/Release/core/Libs
pwsh dotnet-port/pack-tool.ps1 -Pack               # toolchain + packages + this page -> dist/release
pwsh dotnet-port/vscode-nemerle/pack-server.ps1    # stages the server from that same toolchain
cd dotnet-port/vscode-nemerle; npm run package     # VSIX -> dist/release
pwsh dotnet-port/pack-release.ps1                  # verifies the set, writes release-info.json
```

The same commands run on Linux under `pwsh` (verified — `dotnet-port/42-prerelease-wp-n5-log.md`);
only building Stage1 itself (the .NET Framework bootstrap) requires Windows.

`dist\release` is then the release: hand it over or zip it as-is. `pack-release.ps1` refuses to
seal a set whose halves were built from different commits, or from a dirty tree — the VSIX and
the packages are built by different tools, so a stale VSIX beside fresh packages is an easy and
otherwise invisible mistake.

The package version is derived from the packaged compiler's own assembly version, so it states
which compiler is inside; the extension keeps its own version (see the note at the top of this
page). `dotnet-port\packaging\<id>\README.md` is the README embedded in each package (what
nuget.org would render); this page is the install guide, and `pack-tool.ps1 -Pack` copies it into
`dist\release` so the archive explains itself. Design and verification:
`dotnet-port\35-devenv2-wp-m6-log.md`; distribution status overall: `dotnet-port\DISTRIBUTION.md`.

#### Building the release set from a clone (Linux or Windows, no .NET Framework required)

Only building Stage1 itself needs Windows; everything after that already runs on `pwsh` on
either OS (see above). The `boot-net10` orphan branch closes that last gap: it carries a
Windows-built Stage1 compiler, kept off main's history so committing it never moves `git
describe` (and therefore never disturbs the assembly versions baked into every build). On a
clone that fetched it — a plain `git clone` does, a `--single-branch` clone or a source archive
does not, in which case run `git fetch origin boot-net10:boot-net10` first — just run:

```powershell
pwsh dotnet-port/build-from-boot.ps1
```

It fetches the seed, verifies it, and drives the same build chain as above from it, building
either in place or in a pinned worktree depending on whether this checkout's generation already
matches the seed's. The output lands at `dotnet-port/dist/release` (in-place case) or
`dotnet-port/dist/release-from-boot` (worktree case); either way the script prints the final path.

#### Refreshing the boot seed

After rebuilding Stage1 on Windows from the commit you want to seed, run
`pwsh dotnet-port/publish-boot.ps1` and then `git push origin boot-net10` to publish it.
