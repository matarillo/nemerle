# Nemerle for VS Code (development WP-L2 build)

This development extension provides Nemerle language registration, syntax
highlighting, editing configuration, a stdio client for the repository's .NET 10
language server, and the WP-L2 `.nproj` project-information snapshot.

This development VSIX does **not** contain the language server. Build it first:

```powershell
dotnet build ..\LspServer\Nemerle.LanguageServer.csproj -c Release
```

Then set `nemerle.server.path` to the absolute path of
`..\LspServer\bin\Release\net10.0\Nemerle.LanguageServer.dll`. The extension
launches it as `dotnet exec <path>` without a shell. The workspace must be
trusted; Restricted Mode keeps highlighting and editing support but never starts
the server or MSBuild project queries. `nemerle.dotnet.path` may be `dotnet` or an
absolute path to the executable; the same resolved executable starts the server
and runs project queries.

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

WP-L2 evaluates `.nproj` through `dotnet msbuild`, records project sources,
non-facade assembly references, macro-only references, defines, and selected
options, and exposes counts/warnings in the status bar and Output channel.
Queries are serialized, same-key results are cached, file events are debounced,
and reload is explicit. Errors are recoverable and do not stop the language
server.

The snapshot is deliberately **not applied to the analysis engine yet**.
Diagnostics still operate on open/unsaved loose files and are not project-aware;
snapshot-to-engine workspace integration is WP-L3. The development VSIX also
does not bundle server binaries; that packaging work is WP-L4.
