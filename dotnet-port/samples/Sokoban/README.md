# Sokoban sample (macro-only project references)

Demonstrates the `dotnet-port` (.NET 10 SDK-style) build of a Nemerle program that
consumes a **separately compiled macro library**, ported from `snippets/sokoban`
(the original hand-written `Makefile` + raw `ncc.exe` invocation).

It exists to validate the `NemerleMacroReference` wiring added to
[`../../msbuild/Nemerle.Core.targets`](../../msbuild/Nemerle.Core.targets) and
[`../../Nemerle.MSBuild.Tasks/NccCompile.cs`](../../Nemerle.MSBuild.Tasks/NccCompile.cs):
a way for one `.nproj` to load another project's *macros* at compile time (ncc's
`-macros:` switch) without turning it into an ordinary `-ref:`.

## Layout

- `SokobanMacros/` — `macros.n` (copied verbatim from `snippets/sokoban/macros.n`),
  built as a `Library`. Defines `macro NextMove(...)` / `macro UseTunnelMacro(...)` /
  `macro UseTunnelMacro2(...)` via quasi-quotation (`<[ ... ]>`), which needs the
  compiler's own API (`Nemerle.Compiler.dll`) at compile time — referenced explicitly
  via `HintPath` into the `dotnet-port/dist/ncc` layout, since it isn't part of ncc's
  auto-resolved shared-framework reference set.
- `Sokoban/` — the solver itself (`sokoban.n`, `main.n`, `treesearch.n`, `splayheap.n`,
  `localsearch.n`, `zestaw1.xml`, all copied verbatim from `snippets/sokoban`), built as
  an `Exe`. References `SokobanMacros.nproj` as a **macro-only** `ProjectReference`:

  ```xml
  <ProjectReference Include="..\SokobanMacros\SokobanMacros.nproj"
                     OutputItemType="NemerleMacroReference"
                     ReferenceOutputAssembly="false" />
  ```

  `ReferenceOutputAssembly="false"` means the SDK's `ResolveProjectReferences` still
  builds `SokobanMacros.nproj` first (build-order dependency preserved) but routes its
  output path only into `@(NemerleMacroReference)`, not `@(ReferencePath)` — so
  `SokobanMacros.dll` is never passed as `-ref:`, never copied next to `Sokoban.dll`, and
  its types never enter `Sokoban`'s scope. Only its macros are loaded (ncc's `-macros:`).
  This mirrors the legacy `.nproj`'s `MacroProjectReference` item
  (`tools/msbuild-task/Nemerle.MSBuild.targets`), but reuses the modern SDK's own
  `ProjectReference`/`OutputItemType` mechanism (the same one Roslyn analyzer project
  references use) instead of inventing a new item type.

`Sokoban.slnx` groups both projects for convenience; `.nproj` isn't a project extension
`dotnet sln add` recognizes, so it was hand-written with an explicit `Type` GUID (the
same Nemerle project-type GUID used by the legacy `.sln` files at the repo root,
`{EDCC3B85-0BAD-11DB-BC1A-00112FDE8B61}`).

## Prerequisite

`dotnet-port/dist/ncc` must exist (`pwsh dotnet-port/pack-tool.ps1`), and
`dotnet-port/dist/ncc/msbuild-task/Nemerle.MSBuild.Tasks.dll` must include the
`MacroReferences` wiring (rebuild with
`dotnet build -c Release dotnet-port/Nemerle.MSBuild.Tasks/Nemerle.MSBuild.Tasks.csproj`
and copy the output into `dotnet-port/dist/ncc/msbuild-task/` if it predates this sample).

## Usage

```
dotnet build dotnet-port/samples/Sokoban/Sokoban.slnx
dotnet exec dotnet-port/samples/Sokoban/Sokoban/bin/Debug/net10.0/Sokoban.dll zestaw1.xml 1 IDFS
```

(Run from `dotnet-port/samples/Sokoban/Sokoban/`, or pass an absolute path to
`zestaw1.xml` — it's copied next to the built exe via `CopyToOutputDirectory`.)

Third argument selects the search method: `IDFS`, `BlindIDFS`, `BFS`, `A*`, `IDA*`,
`RBFS`, or `SA` (simulated annealing). Verified against levels 1–3 with `IDFS`/`BFS`/`SA`
— all solve correctly, and `SokobanMacros.dll` is confirmed absent from the output
directory (proving the macro-only reference isn't copied as a runtime dependency).
