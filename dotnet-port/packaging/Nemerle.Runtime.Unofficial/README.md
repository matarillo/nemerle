# Nemerle.Runtime.Unofficial

The **runtime assemblies** of an **unofficial** .NET 10 port of the
[Nemerle](https://github.com/rsdn/nemerle) compiler: `Nemerle.dll` (the standard library every
compiled Nemerle program binds against) plus `Nemerle.Macros.dll` and `Nemerle.Compiler.dll`
(needed at run time when macros expand into code that calls them).

> **This is not a release of the Nemerle project.** It is a personal port
> (<https://github.com/matarillo/nemerle>). The `.Unofficial` suffix keeps that unambiguous.

## You do not install this directly

`Nemerle.Sdk.Unofficial` references this package for you. When you build a `.nproj` with
`<Project Sdk="Nemerle.Sdk.Unofficial/__NEMERLE_SDK_VERSION__">`, the SDK adds
`Nemerle.Runtime.Unofficial` as an implicit dependency so the compiled program's `deps.json`
lists its runtime closure and the .NET host loads it. The compiler's *compile-time* view of these
assemblies is resolved separately (from the SDK package's bundled `ncc` layout), so this package
contributes runtime assets only.

There is normally no reason to add a `PackageReference` to it by hand.

## License

BSD-3-Clause. Copyright (c) 2003-2008 The University of Wroclaw. Copyright (c) 2008-2014 Nemerle
Project Team.
