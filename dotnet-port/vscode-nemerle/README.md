# Nemerle for VS Code (development shell)

This WP-L1 extension provides Nemerle language registration, syntax highlighting,
editing configuration, and a stdio client for the repository's .NET 10 language
server.

This development VSIX does **not** contain the language server. Build it first:

```powershell
dotnet build ..\LspServer\Nemerle.LanguageServer.csproj -c Release
```

Then set `nemerle.server.path` to the absolute path of
`..\LspServer\bin\Release\net10.0\Nemerle.LanguageServer.dll`. The extension
launches it as `dotnet exec <path>` without a shell. The workspace must be
trusted; Restricted Mode keeps highlighting and editing support but never starts
the server.

Commands:

- `Nemerle: Restart Language Server`
- `Nemerle: Show Output`

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

The current server intentionally operates on open/unsaved loose files only. It
does not yet evaluate `.nproj`, ProjectReference, PackageReference, or macro
references; that project-aware work belongs to WP-L2 and later work packages.
