# Nemerle for VS Code (development WP-L3 build)

This development extension provides Nemerle language registration, syntax
highlighting, editing configuration, a stdio client for the repository's .NET 10
language server, and the project-aware engine workspace: the `.nproj` MSBuild
snapshot (sources, references, macro references, options) is applied to the
analysis engine, so diagnostics cover the whole selected project.

This development VSIX does **not** contain the language server. Build it first:

```powershell
dotnet build ..\LspServer\Nemerle.LanguageServer.csproj -c Release
```

Then set `nemerle.server.path` to the absolute path of
`..\LspServer\bin\Release\net10.0\Nemerle.LanguageServer.dll`. The extension
launches it as `dotnet exec <path>` without a shell. The workspace must be
trusted; Restricted Mode keeps highlighting and editing support but never starts
the server, MSBuild project queries, or the project engine workspace.
`nemerle.dotnet.path` may be `dotnet` or an absolute path to the executable; the
same resolved executable starts the server and runs project queries.

Project selection is limited to one workspace folder and one `.nproj`. One
project is selected automatically; multiple projects require `Nemerle: Select
Project`. The selection is stored in workspace state and can be overridden by
`nemerle.project`. Project queries use `Debug`, `AnyCPU`, and the project's
default target framework unless `nemerle.projectConfiguration`,
`nemerle.projectPlatform`, or `nemerle.projectTargetFramework` is set.

Commands:

- `Nemerle: Restart Language Server`
- `Nemerle: Show Output`
- `Nemerle: Select Project`
- `Nemerle: Reload Project`
- `Nemerle: Show Project Status`

`nemerle.server.trace` accepts `off`, `messages`, or `verbose`. Protocol traces
also respect the Output channel's log level. Server stderr is written to the
same `Nemerle Language Server` channel; stdout is reserved for LSP framing.

Development commands:

```powershell
npm ci
npm run lint
npm test
npm run test:integration
npm run package
```

The extension evaluates `.nproj` through `dotnet msbuild`, and the language
server loads every project source (unsaved editor buffers override disk
content), resolved non-facade assembly references, and macro-only references
into its engine workspace. Closing a project file reverts it to disk-backed
content without removing it from the project. File watchers trigger the right
level of refresh: `.nproj`, imported `*.targets`/`*.props`, and
`obj/project.assets.json` changes force a new MSBuild query; on-disk `.n` edits
and rebuilt referenced assemblies re-apply the cached snapshot. Queries are
serialized, same-key results are cached, file events are debounced, and errors
are recoverable and do not stop the language server; a failed reload keeps the
previous engine workspace.

The status bar shows whether the snapshot is applied to the engine
(`Nemerle: Project Applied`). Hover/completion/definition are not implemented
yet. The development VSIX also does not bundle server binaries; that packaging
work is WP-L4.
