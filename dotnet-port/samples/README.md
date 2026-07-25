# Nemerle samples

Small projects that build and run with the .NET 10 port of `ncc`. They exist to show what
Nemerle's macros do, and they double as the fixtures the language-server test suite runs
against — so if they build, the port works.

Every project here is an ordinary SDK-style `.nproj`. What makes a macro library a macro
library is one property and one reference form:

```xml
<!-- in the macro library -->
<NemerleMacroLibrary>true</NemerleMacroLibrary>
```

```xml
<!-- in the project that uses it -->
<ProjectReference Include="..\MyMacros\MyMacros.nproj"
                  OutputItemType="NemerleMacroReference"
                  ReferenceOutputAssembly="false" />
```

`NemerleMacroReference` means "load this assembly into the compiler and run it at compile
time", and `ReferenceOutputAssembly="false"` means the macro library is not a runtime
dependency of the result.

## Prerequisites

These projects are built from a repository checkout and use the compiler layout in
`dotnet-port/dist/ncc/`, not a released NuGet package. Populate it once:

```console
pwsh -NoProfile -File dotnet-port/pack-tool.ps1 -Pack
```

If you only want to *use* Nemerle, you do not need this directory at all — follow the
[install guide](../packaging/README.md) instead, which starts from a released package and
`dotnet new`.

## The samples

### HelloCore

The smallest thing that proves the toolchain works: `printf` from `Nemerle.IO`, a list, and
`Map` from `Nemerle.Collections`.

```console
dotnet run --project dotnet-port/samples/HelloCore/HelloCore.nproj
```

### SyntaxMacro — a macro that adds a keyword

The one-page version of Nemerle's central idea. `SyntaxMacros/macros.n` declares:

```nemerle
macro Twice(body : PExpr)
syntax ("twice", body)
{
  <[ { $body; $body } ]>
}
```

`syntax ("twice", body)` does not add a function called `twice` — it adds the **keyword**
`twice` to the grammar of every file that writes `using SyntaxMacro;`. `SyntaxDemo/Program.n`
then writes `twice count += 1;` and prints `count = 2`.

This is also the case the VS Code extension exists for. No static grammar can know the word
`twice`, because it does not exist until the compiler has loaded this assembly and read the
consuming file's `using`. The language server colors it as a macro keyword, distinctly from a
built-in one, because the analysis engine *is* the compiler.

```console
dotnet run --project dotnet-port/samples/SyntaxMacro/SyntaxDemo/SyntaxDemo.nproj
```

### Latin — a keyword is not enough; a whole statement form

`si … tum … aliter …` is an `if`/`else` written in Latin, and `revelare (expr)` prints an
expression together with its own source text:

```nemerle
si (gold >= 50) tum { WriteLine("Alea iacta est!"); }
aliter          { WriteLine("Paupertas non est vitium..."); }

revelare(a + b);        // [ Veritas ] a + b => 42
```

The point over `SyntaxMacro`: a `syntax (...)` clause can interleave several keywords with
several operands, so a macro can introduce an entire statement form rather than a prefix word.
`revelare` additionally shows that a macro sees its argument as a *syntax tree* — it calls
`expr.ToString()` at compile time to recover the text `a + b`, which a function could never do.

```console
dotnet run --project dotnet-port/samples/Latin/LatinDemo/LatinDemo.nproj
```

### SyntaxTree — rewriting the argument's syntax tree

`explain ((x + 2) * (y - 1))` walks the expression tree of its argument and rebuilds it into
code that prints every intermediate step:

```text
x = 3
2 = 2
x + 2 = 5
...
x + 2 * y - 1 = 20
answer = 20
```

Nothing was printed by the macro; the macro *generated* the printing code, recursively, from
the shape of the expression it was handed.

```console
dotnet run --project dotnet-port/samples/SyntaxTree/SyntaxTreeDemo/SyntaxTreeDemo.nproj
```

### CompTimeSolver — the compiler solves a maze while compiling

`Maze/maze.n` defines `SolveMaze(inputFile)`, a macro that **reads a text file and runs a
breadth-first search at compile time**. The path it finds is baked into the assembly as a
constant; the program that runs afterwards does no searching at all.

Because the search happens during compilation, an unsolvable maze is a **compile error**:

```console
dotnet build dotnet-port/samples/CompTimeSolver/Success/Success.nproj   # succeeds
dotnet build dotnet-port/samples/CompTimeSolver/Fail/Fail.nproj         # fails, by design
```

`Fail/fail.txt` is `Success/success.txt` with the start walled off, and the macro answers with
`Message.Error("This maze cannot be solved")` — a diagnostic emitted by user code, reported
exactly like a compiler error, at build time and in the editor:

```text
fail.n(2,11,2,32): nemerle error : This maze cannot be solved
```

(The compiler re-types the failing expression, so that line appears twice.)

### Sokoban — macros in a real program

A Sokoban solver (five source files) with a macro library beside it. Unlike the samples above
it is not a demonstration of one idea; it is ordinary code that happens to use macros for the
parts where they help, and it is the largest thing the language-server suite analyses.

```console
cd dotnet-port/samples/Sokoban/Sokoban
dotnet build Sokoban.nproj
dotnet exec bin/Debug/net10.0/Sokoban.dll zestaw1.xml 1 IDFS
```

The third argument selects the search method (`IDFS`, `BFS`, `A*`, `SA`, …).
[`Sokoban/README.md`](Sokoban/README.md) explains the macro-only `ProjectReference` wiring this
sample was originally written to validate — including the proof that `SokobanMacros.dll` never
reaches the output directory, because macros are a compile-time dependency only.

## Keeping this page honest

Every sample above is built by CI, and `CompTimeSolver/Fail` is checked in the direction it is
supposed to fail — so if this page says something builds, it builds.

```console
pwsh -NoProfile -File dotnet-port/build-samples.ps1
```

The script also refuses to pass if a `.nproj` under `samples/` is not classified, so a new
sample cannot be added without deciding whether it belongs on this page.

## Test fixtures

The remaining directories are not showcases. They exist to pin specific toolchain behaviour and
are kept deliberately minimal:

| Directory | Pins |
|---|---|
| `RefDemo/` | `ProjectReference` between two Nemerle projects (`MathLib` → `App`) |
| `PackageReference/` | a NuGet `PackageReference` resolving into the analysis engine |
| `Defines/` | `DefineConstants` reaching `ncc` as `-define:`, so `dotnet build` and the editor select the same `#if` branch |
| `Warnings/` | a warning with an N-code (`N10001`) surfacing structurally in diagnostics |

`CompTimeSolver/Fail` is listed above as a showcase, but note it is *expected to fail to
build* — do not add it to any "build everything" script.
