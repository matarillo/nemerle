# Nemerle for VS Code (developer preview, WP-L4 build)

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
  server relies on) you need the .NET 10 SDK plus this repository's compiler
  toolchain: `dotnet-port\dist\ncc` (`pack-tool.ps1`) and
  `dotnet-port\msbuild\Nemerle.Core.targets` imported by your `.nproj`.
  A standalone `Nemerle.Sdk` NuGet package is planned as a separate work
  package; until then the developer preview assumes a repository checkout for
  the build toolchain (the language server itself does not need one).

## Install (developer preview)

Build the VSIX from a repository checkout:

```powershell
pwsh dotnet-port\pack-tool.ps1                     # dist/ncc, needed to build the server
pwsh dotnet-port\vscode-nemerle\pack-server.ps1    # builds LspServer, stages server/
cd dotnet-port\vscode-nemerle
npm ci
npm run package                                    # lint + tests + verify-server + vsce package
code --install-extension vscode-nemerle-0.4.0.vsix
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
and reflects unsaved editor buffers. Completion and definition are the next work
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
- **`FileLoadException` mentioning Nemerle assemblies** — the bundled server
  and the workspace's `dist/ncc` toolchain were built from different commits
  (Nemerle assembly versions derive from `git describe`). Rebuild both from
  the same commit; `server/bundle-info.json` records the commit the bundle was
  packed from.
- **Nothing starts** — check that the workspace is trusted and open the
  `Nemerle Language Server` output channel (`Nemerle: Show Output`).

## Development

```powershell
npm ci
npm run check-types
npm run lint
npm test                 # unit tests (includes packaging-structure checks)
npm run test:integration # trusted + untrusted Extension Host tests
npm run package          # VSIX (requires server/ staged by pack-server.ps1)
npm run test:vsix        # installs the VSIX into isolated dirs; bundled server end-to-end
pwsh .\test-bundled-server.ps1  # raw LSP suite against the server extracted from the VSIX
```

`npm run test:integration` uses a development server override
(`dotnet-port\LspServer\bin\Release\net10.0`); `npm run test:vsix` and
`test-bundled-server.ps1` exercise the bundled server exactly as installed.
Third-party license information for everything inside the VSIX is recorded in
`THIRD-PARTY-NOTICES.md` and enforced by `npm run verify-server`.
