# Nemerle for VS Code (developer preview, WP-M6 build)

Language support for Nemerle on .NET 10: `.n` language registration, syntax
highlighting, editing configuration, and project-aware diagnostics from the
bundled .NET 10 language server. The `.nproj` MSBuild snapshot (sources,
references, macro references, options) is applied to the analysis engine, so
diagnostics cover the whole selected project, with unsaved editor buffers
overriding disk content.

## Requirements

- Windows 11 (the preview is validated on Windows; the server is managed IL).
- VS Code 1.125.0 or later.
- The **.NET 10 runtime** (the SDK includes it). The VSIX bundles the language
  server and its managed dependencies under `server/`, but not the .NET
  runtime. Before starting the server the extension runs
  `dotnet --list-runtimes` and reports an actionable error if no
  `Microsoft.NETCore.App 10.x` runtime is found.
- To **build** Nemerle projects (and for the MSBuild project queries the
  server relies on) you need the .NET 10 SDK plus a Nemerle compiler
  toolchain. Either:
  - the **`Nemerle.Sdk.Unofficial` NuGet package** (no repository checkout):

    ```xml
    <Project Sdk="Nemerle.Sdk.Unofficial/1.2.601-preview.1">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
      </PropertyGroup>
    </Project>
    ```

    It is currently published to a local feed only, produced by
    `pwsh dotnet-port\pack-tool.ps1 -Pack` into `dotnet-port\dist\release`;
    point a `NuGet.config` at that directory. `dotnet new install
    Nemerle.Templates.Unofficial::<version>` adds `nemerle-console` /
    `nemerle-classlib` templates. `dotnet-port\packaging\README.md` is the
    install guide (it ships in the release folder too).
  - or a repository checkout: `dotnet-port\dist\ncc` (`pack-tool.ps1`) plus
    `<Import Project="...\dotnet-port\msbuild\Nemerle.Core.targets" />` in your
    `.nproj`. This is the same build logic; the package just ships it.

  The project file must not use the `.csproj` extension either way (the .NET
  SDK would import C#'s compiler targets and try to compile `.n` with csc);
  use `.nproj`.

## Install (developer preview)

Build the VSIX from a repository checkout:

```powershell
pwsh dotnet-port\pack-tool.ps1                     # dist/ncc, needed to build the server
pwsh dotnet-port\vscode-nemerle\pack-server.ps1    # builds LspServer, stages server/
cd dotnet-port\vscode-nemerle
npm ci
npm run package                                    # lint + tests + verify-server + vsce package
code --install-extension vscode-nemerle-0.8.1.vsix
```

Then open a trusted folder containing a `.nproj` project. The server starts
automatically for `.n` files, the single project is selected automatically
(multiple projects: `Nemerle: Select Project`), and the status bar shows
`Nemerle: Project Applied` once diagnostics are project-aware.

## Configuration

- `nemerle.server.path` — leave **empty** to use the bundled server
  (`<extension>/server/Nemerle.LanguageServer.dll`). Set an absolute path to a
  development `Nemerle.LanguageServer.dll` to override it (the extension
  launches DLLs as `dotnet exec <path>` without a shell).
- `nemerle.dotnet.path` — `dotnet` (PATH lookup) or an absolute executable
  path; the same resolved executable starts the server, performs the runtime
  check, and runs `dotnet msbuild` project queries.
- `nemerle.project`, `nemerle.projectConfiguration` (`Debug`),
  `nemerle.projectPlatform` (`AnyCPU`), `nemerle.projectTargetFramework`
  (empty = project default) — project selection and MSBuild query dimensions.
- `nemerle.server.trace` — `off` / `messages` / `verbose` LSP tracing.

Commands: `Nemerle: Restart Language Server`, `Show Output`, `Select Project`,
`Reload Project`, `Show Project Status`.

## Behavior notes

The workspace must be trusted; Restricted Mode keeps highlighting and editing
support but never starts the server, MSBuild project queries, or the project
engine workspace. Server stderr goes to the `Nemerle Language Server` output
channel; stdout is reserved for LSP framing. File watchers pick the right
refresh: `.nproj`, imported `*.targets`/`*.props`, and
`obj/project.assets.json` changes force a new MSBuild query; on-disk `.n`
edits and rebuilt referenced assemblies re-apply the cached snapshot. Errors
are recoverable: a failed reload keeps the previous engine workspace.

**Hover** (`textDocument/hover`) is available: hovering an identifier shows its
type/signature (and documentation when present), converted from the engine's
hints to markdown. It works across all project sources and resolved references,
and reflects unsaved editor buffers.

**Completion** (`textDocument/completion`, triggered on `.` and as you type) is
available: member completion after a receiver resolves against the receiver's
type (including project references, NuGet packages, and types declared in other
project sources), and global-scope completion offers keywords and visible
symbols. Item documentation (overloads and XmlDoc summaries) is filled in on
demand via `completionItem/resolve`. Completion reflects unsaved editor buffers.

**Go to definition** (`textDocument/definition`) and **find all references**
(`textDocument/references`) are available: they resolve locals, parameters,
members, and types to their declaring source — across project sources — and
reflect unsaved editor buffers. Definitions on a BCL/NuGet member return no
location (generated-source display is not provided). References honor
`includeDeclaration`.

**Incremental rebuild** is enabled by default: editing inside a method body
re-types just that method (a relocation) instead of reloading the whole project,
so diagnostics update faster while typing. Edits that change a source's structure
(adding/removing a member, a `using`, or a type) automatically fall back to a
full types-tree rebuild. To restore the previous behavior (a full reload on
every change) set the `NEMERLE_INCREMENTAL_UPDATE` environment variable to `0`
for the server process. Semantic tokens and signature help are the next work
packages.

## Troubleshooting

- **"The .NET host 'dotnet' was not found"** — install the .NET 10 SDK or
  runtime (<https://dotnet.microsoft.com/download/dotnet/10.0>), or point
  `nemerle.dotnet.path` at an absolute `dotnet` executable.
- **"needs the Microsoft.NETCore.App 10.x runtime"** — a dotnet host exists
  but .NET 10 is not installed; the message lists what was found.
- **"The bundled Nemerle language server is missing"** — the VSIX was built
  without `pack-server.ps1` (development tree), or the install is corrupted.
  Re-run `pack-server.ps1` + `npm run package`, reinstall the VSIX, or set
  `nemerle.server.path`.
- **Status bar shows `Nemerle: Project Error`** — run
  `Nemerle: Show Project Status` and check the output channel; MSBuild query
  failures (missing SDK, unrestored NuGet packages, broken `.nproj`) are
  reported there and do not stop the server. Loose-file diagnostics keep
  working.
- **"Nemerle toolchain/language server version mismatch"** — the bundled server
  and the toolchain your project builds with come from different generations
  (Nemerle assembly versions track the source generation, so mixing them can
  also surface as a bare `FileLoadException`). The notification names both
  versions. Rebuild or repack both from the same commit: `server/bundle-info.json`
  records the server's generation and the toolchain's `ncc-info.json` (in
  `dist/ncc`, or `tools/ncc/` inside `Nemerle.Sdk.Unofficial`) records its own.
  The server logs both at startup and on every project load, so the output
  channel shows them even when they agree.
- **Nothing starts** — check that the workspace is trusted and open the
  `Nemerle Language Server` output channel (`Nemerle: Show Output`).

## Development

```powershell
npm ci
npm run check-types
npm run lint
npm test                 # unit tests (includes packaging-structure checks)
npm run test:integration # trusted + untrusted Extension Host tests
npm run test:sdk         # Extension Host against a project built from the Nemerle.Sdk package
                         # (needs `pwsh dotnet-port\pack-tool.ps1 -Pack`; generates test-workspace-sdk/)
npm run package          # VSIX (requires server/ staged by pack-server.ps1)
npm run test:vsix        # installs the VSIX into isolated dirs; bundled server end-to-end
pwsh .\test-bundled-server.ps1  # raw LSP suite against the server extracted from the VSIX
```

`npm run test:integration` uses a development server override
(`dotnet-port\LspServer\bin\Release\net10.0`); `npm run test:vsix` and
`test-bundled-server.ps1` exercise the bundled server exactly as installed.
Third-party license information for everything inside the VSIX is recorded in
`THIRD-PARTY-NOTICES.md` and enforced by `npm run verify-server`.
