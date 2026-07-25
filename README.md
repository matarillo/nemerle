# Nemerle on .NET 10 — an unofficial port (this fork)

This fork ([matarillo/nemerle](https://github.com/matarillo/nemerle)) ports the self-hosting
Nemerle compiler `ncc` and its toolchain from .NET Framework 4.x to modern, cross-platform
**.NET 10**. Everything below the horizontal rule is the original upstream
[rsdn/nemerle](https://github.com/rsdn/nemerle) README, kept as-is: it describes the classic
.NET Framework / Mono toolchain (nemerle.org installers, Visual Studio 2008–2013 integration),
which this fork leaves intact but does not modernize.

What the port gives you:

- **A self-hosted .NET 10 `ncc`.** The compiler compiles itself on .NET 10; successive
  self-hosted generations are byte-identical (deterministic fixpoint), and the compiler
  testsuite passes with no known genuine compiler bugs (the remaining failures are
  environment and BCL differences, itemized in the docs).
- **The ordinary modern .NET workflow.** SDK-style `.nproj` projects
  (`<Project Sdk="Nemerle.Sdk.Unofficial/<version>">`), `dotnet new` templates, an in-process
  MSBuild compile task with structured diagnostics, `ProjectReference` / `PackageReference`,
  Portable PDB debugging, incremental build and `dotnet clean`.
- **A VS Code extension with a language server**: project-aware diagnostics, hover, completion,
  go-to-definition / find-references, incremental analysis, and semantic highlighting. The
  analysis engine *is* the Nemerle compiler, so the editor understands syntax and keywords that
  macros add dynamically — a keyword introduced by a syntax macro is even colored differently
  from a built-in one, which Roslyn-based tooling cannot offer for a macro language.
- **Windows and Linux.** The packages are pure managed IL; the same set is verified on both
  (Windows 11 in daily use, Ubuntu on clean VMs and CI).

**Status: preview, and a personal port.** It is not affiliated with or supported by the Nemerle
project team; the `.Unofficial` suffix on every package ID keeps that unambiguous. Distribution
is deliberately modest: packages ship as
[GitHub Release assets](https://github.com/matarillo/nemerle/releases) (a local NuGet feed),
not on nuget.org or the VS Code Marketplace. The primary goal of this fork is that the port
stays findable, reproducible and understandable long-term — with enough of an on-ramp that a
curious .NET developer can try it.

## Try it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — no .NET
Framework, no Mono, no clone of this repository.

1. Download all assets of the latest release from the
   [release page](https://github.com/matarillo/nemerle/releases) into a folder you keep,
   e.g. `C:\nemerle-packages` (Windows) or `~/nemerle-packages` (Linux). That folder is a
   NuGet *local feed* — nothing needs to be extracted.
2. Register the feed and install the project templates (replace `<version>` with the package
   version of the release you downloaded, prerelease label included — it is in the file names):

   ```console
   dotnet nuget add source C:\nemerle-packages -n nemerle-local
   dotnet new install Nemerle.Templates.Unofficial@<version> --add-source C:\nemerle-packages
   ```

   (On Linux, use `~/nemerle-packages` in both commands.)
3. Create, build, run:

   ```console
   dotnet new nemerle-console -n MyApp
   cd MyApp
   dotnet build
   dotnet run
   ```

   ```text
   Hello from Nemerle on .NET 10!
   ```

4. Optional — editor support in VS Code:

   ```console
   code --install-extension vscode-nemerle-<version>.vsix
   ```

The `README.md` bundled with each release is the full install guide (feed configuration
options, writing projects by hand, macro libraries, LINQ, troubleshooting); a copy lives in
this repository at [`dotnet-port/packaging/README.md`](dotnet-port/packaging/README.md) with
`__NEMERLE_..._VERSION__` placeholders where the shipped copy shows real versions.

## Known limitations

From a user's point of view, the notable constraints are:

- **Project files must use the `.nproj` extension**, not `.csproj` (the .NET SDK picks its
  compiler from the extension; a `.csproj` would invoke csc on your Nemerle sources).
- **`GenerateDependencyFile` defaults to `false` and should stay that way**: the Nemerle
  runtime assemblies are copied next to your output rather than resolved through the package
  graph, so a generated `deps.json` would not list them.
- **Mono is not a target.** The port targets .NET 10; the classic .NET Framework 4.x build
  (upstream README below) still works but is a separate toolchain.
- **Preview surface.** Verified against the port's own samples and testsuite, not broadly in
  the wild. The macro ecosystem libraries (Nemerle.Peg, Nemerle.Statechart, the C# parser
  plugin, …) are not ported; `Nemerle.Linq` is.
- The classic Visual Studio 2008/2010 integration does not apply to the port; the supported
  editor is VS Code (via the extension above).

## Where things are documented

- [`dotnet-port/docs/00-PLAN.md`](dotnet-port/docs/00-PLAN.md) — the port's master plan, work
  package list and chronological work log. The numbered documents beside it
  (`dotnet-port/docs/NN-*.md`) are the per-work-package plans, implementation logs and
  analyses, and record every design decision and verification.
- [`dotnet-port/DISTRIBUTION.md`](dotnet-port/DISTRIBUTION.md) — the distribution and
  toolchain reference: packages, version/tag contract, build-from-seed bootstrap, CI, and how
  to reproduce a published release.
- [`dotnet-port/packaging/README.md`](dotnet-port/packaging/README.md) — the install guide
  shipped with each release.

---

# What Is It

[![Join the chat at https://gitter.im/rsdn/nemerle](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/rsdn/nemerle?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

Nemerle is a high-level statically-typed programming language for the .NET platform. It offers functional, object-oriented and imperative features. It has a simple C#-like syntax and a powerful meta-programming system.

Features that come from the functional land are variants, pattern matching, type inference and parameter polymorphism (aka generics). The meta-programming system allows great compiler extensibility, embedding domain specific languages, partial evaluation and aspect-oriented programming.

To find out more, please visit: http://nemerle.org/

# Quick sample

## Hello world

Create _hello.n_:
```nemerle
using System.Console;

WriteLine("Hello world")
```
Compile and run
```bat
"C:\Program Files\Nemerle\ncc.exe" hello.n /out:hello.exe
hello.exe
```
Will output
```bat
Hello world
```
# Install

## Windows

  Install latest msi package from http://nemerle.org/

## Linux, Mono

  Download latest binary package from http://nemerle.org and export Nemerle=/path/to/binaries/extracted

# How to build


Clone with all submodules: git clone --recursive git://github.com/rsdn/nemerle.git
If you have a clone already: git pull --recurse-submodules

## Windows

  * For Development:
  
  [Nemerle build process (for Nemerle developers)](https://github.com/rsdn/nemerle/wiki/Nemerle-build-process-(for-Nemerle-developers))

  * For Installer:
  
  Run BuildInstallerFull(fx-version).cmd depending on required .NET version. Installer will be placed in bin/Release/net-(fx-version)/Installer.
  
  _Note: You can also use BuildInstallerFast(fx-version).cmd to build installer without running tests._

  _Note: For building Visual Studio bindings you need VSSDK and administrative rights._

## Linux

  Nemerle can bootstrap itself on Mono.
  
  * Generic line:
  
  xbuild NemerleAll-Mono.nproj /p:TargetFrameworkVersion=v(3.5 or 4.0 or 4.5 or 4.5.1) /p:Configuration=Release(or Debug) /t:Stage4(1 - 4) /tv:4.0(Needed for framework 4.0 and above)   
  
  * Release 3.5:
  
  xbuild NemerleAll-Mono.nproj /p:TargetFrameworkVersion=v3.5 /p:Configuration=Release /t:Stage4  
  
  * Debug 4.0:
  
  xbuild NemerleAll-Mono.nproj /p:TargetFrameworkVersion=v4.0 /p:Configuration=Debug /t:Stage4 /tv:4.0
  

# What about IDE?

  * Visual Studio 2008/2010/2012/2013-preview integration installed by Nemerle installer
  * Nemerle Studio is a free IDE based on Visual Studio Shell (Isolated mode) installed by Nemerle installer if VS Shell was installed
  * Sharp Develop 3.0 addin can be builded manually. See snippets/sharpdevelop/ReadMe.txt 
  * See Vim, Emacs, Kate and other editors syntax support in the 'misc' folder

# Repository structure

  * Nemerle compiler sources (ncc/),
  * Nemerle Documentation (doc/),
  * standard Nemerle library (lib/),
  * standard Nemerle macros (macros/),
  * some examples of Nemerle programs (snippets/),
  * a few useful tools (e.g. synatx highlighting modes) (misc/),
  * binary Nemerle compiler needed to compile itself (boot/, boot-4.0/).
  * Nemerle realted tools (e.g. relector addin) (tools/)
  * Visual Studio 2008 integration (VsIntegration/)

# Contacts

  * [Gitter](https://gitter.im/rsdn/nemerle) - chat for interactive discussions
  * Nemerle forum: http://groups.google.com/group/nemerle-en
  * Nemerle Russian forum: http://rsdn.ru/forum/nemerle/
