# Nemerle.Sdk.Unofficial

**Unofficial** .NET 10 build of the [Nemerle](https://github.com/rsdn/nemerle) compiler,
packaged as an MSBuild project SDK. This is **not** a release of the Nemerle project; it is
built from a personal port at <https://github.com/matarillo/nemerle>. The Nemerle project's own
packages are `Nemerle`, `Nemerle.Compiler`, etc.

It lets `dotnet build` compile Nemerle (`.n`) sources with no repository checkout: the package
carries the self-hosted `ncc` compiler and an in-process MSBuild task that drives it.

## Usage

A Nemerle project is an ordinary SDK-style project file that must use the **`.nproj`**
extension (see *Limitations*):

```xml
<!-- hello.nproj -->
<Project Sdk="Nemerle.Sdk.Unofficial/__NEMERLE_SDK_VERSION__">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

```n
// Program.n
module Program
{
  Main() : void
  {
    System.Console.WriteLine("Hello from Nemerle on .NET 10!");
  }
}
```

```console
$ dotnet build
$ dotnet run
```

`**/*.n` is globbed into `@(NemerleCompile)` automatically, the same way `Microsoft.NET.Sdk`
globs `**/*.cs`.

Alternatively, pin the version in `global.json` and drop it from the project file:

```json
{ "msbuild-sdks": { "Nemerle.Sdk.Unofficial": "__NEMERLE_SDK_VERSION__" } }
```

```xml
<Project Sdk="Nemerle.Sdk.Unofficial">
```

Project templates are available separately as `Nemerle.Templates.Unofficial`:

```console
$ dotnet new install Nemerle.Templates.Unofficial::__NEMERLE_SDK_VERSION__
$ dotnet new nemerle-console
```

## References

`ProjectReference` and `PackageReference` work as usual. Macro libraries (compile-time only,
passed to `ncc` as `-macros:` rather than `-ref:`) use the SDK's own `OutputItemType`
mechanism:

```xml
<ProjectReference Include="..\MyMacros\MyMacros.nproj"
                  OutputItemType="NemerleMacroReference"
                  ReferenceOutputAssembly="false" />
```

## Useful properties

| Property | Default | Meaning |
|---|---|---|
| `EnableDefaultNemerleCompileItems` | `true` | Glob `**/*.n` into `@(NemerleCompile)`. |
| `NemerleAdditionalOptions` | (empty) | Extra `ncc` switches, verbatim (e.g. `-nowarn:10003`). |
| `NemerleUseExec` | `false` | Run `ncc` out-of-process instead of in-process. |
| `NccLayoutDir` | package's `tools/ncc/` | Point at a different compiler layout. |

`$(DefineConstants)` is passed to `ncc` as `-define:`, and `$(DebugType)`/`$(DebugSymbols)`
control Portable PDB emission, so `#if` and debugging behave as in C# projects.

## Limitations

- **The project file must not be named `.csproj`.** The .NET SDK picks its language targets
  from the project extension, and a `.csproj` would import C#'s `CoreCompile` (csc) after
  this SDK's, so csc would try to compile your Nemerle sources. Use `.nproj`. The SDK raises
  an explicit error if it detects a `.csproj`.
- `GenerateDependencyFile` defaults to `false`. The Nemerle runtime assemblies are copied
  beside your output rather than resolved through the package graph, so they are absent from
  any generated `deps.json` - and a present-but-incomplete `deps.json` would stop the host from
  finding them at run time. Set it back to `true` only if you also arrange for
  `Nemerle.dll` to be listed there.
- This is a preview: it is verified against a local feed and the port's own samples, not
  broadly in the wild.

## Provenance

`tools/ncc/ncc-info.json` records the commit, `git describe`, configuration and pack timestamp
of the compiler inside this package. The Nemerle assembly version derives from the source
generation, so a language server or tooling built from a different commit can fail to load
these assemblies; the version of this package (`1.2.<revision>`) is the compiler's own
assembly revision, which makes such mismatches visible.

## License

BSD-3-Clause. Copyright (c) 2003-2008 The University of Wroclaw. Copyright (c) 2008-2014
Nemerle Project Team. The bundled assemblies (`Nemerle.dll`, `Nemerle.Compiler.dll`,
`Nemerle.Macros.dll`, `Nemerle.CoreEmit.dll`, `ncc.dll`) and the port's own
`Nemerle.Compiler.Hosting.dll` / `Nemerle.MSBuild.Tasks.dll` are all under that license; no
third-party assemblies are redistributed in this package.
