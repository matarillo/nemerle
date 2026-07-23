# Nemerle.Linq.Unofficial

**Unofficial** .NET 10 build of `Nemerle.Linq` — the [Nemerle](https://github.com/rsdn/nemerle)
LINQ macro library (the `linq` query syntax, the `ToExpression` macro, and implicit
lambda-to-expression-tree conversion). This is **not** a release of the Nemerle project; it is
built from a personal port at <https://github.com/matarillo/nemerle>.

Use it with `Nemerle.Sdk.Unofficial` — prefer the **same version** for both packages (the
assembly version tracks the compiler generation; a `Nemerle.Linq` newer than the toolchain
fails to load at run time).

## Usage

```xml
<!-- app.nproj -->
<Project Sdk="Nemerle.Sdk.Unofficial/__NEMERLE_SDK_VERSION__">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Nemerle.Linq.Unofficial" Version="__NEMERLE_SDK_VERSION__" />
  </ItemGroup>
</Project>
```

```n
// Program.n
using System;
using System.Console;
using System.Linq;
using System.Linq.Expressions;
using Nemerle.Linq;

module Program
{
  Main() : void
  {
    // linq query syntax (macro):
    def xs = [3, 1, 4, 1, 5, 9, 2, 6];
    def evens = linq <# from x in xs where x % 2 == 0 orderby x select x #>;
    WriteLine(string.Join(",", evens));

    // expression trees (macro + implicit conversion):
    def square : Expression[Func[int, int]] = x => x * x;
    WriteLine(square.Compile()(12));
  }
}
```

An ordinary `PackageReference` is all that is needed: the compiler discovers the macros in
referenced assemblies, and the `System.Linq.Expressions` / `System.Linq.Queryable` BCL surface
is part of the port's default reference set.

## Limitations

- Requires the `Nemerle.Sdk.Unofficial` toolchain (same version recommended, see above).
- `IObjectReference`-style binary serialization scenarios and other APIs removed from modern
  .NET are unavailable, as everywhere on .NET 10.
- This is a preview: verified against the port's test suite (`testsuite/positive`
  LINQ tests) and samples, not broadly in the wild.

## License

BSD-3-Clause. Copyright (c) 2003-2008 The University of Wroclaw. Copyright (c) 2008-2014
Nemerle Project Team. The packaged assembly (`Nemerle.Linq.dll`) is built unmodified from the
Nemerle sources except for a portability fix in expression-tree constructor resolution
(documented in the port's `dotnet-port/docs/40-prerelease-wp-n3-log.md`).
