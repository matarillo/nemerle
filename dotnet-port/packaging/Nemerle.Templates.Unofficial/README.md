# Nemerle.Templates.Unofficial

`dotnet new` templates for [Nemerle](https://github.com/rsdn/nemerle) projects targeting
.NET 10.

**Unofficial**: these are not a release of the Nemerle project. They come from a personal port
at <https://github.com/matarillo/nemerle>, and the projects they generate build with the
`Nemerle.Sdk.Unofficial` MSBuild project SDK from the same source.

## Install

```console
$ dotnet new install Nemerle.Templates.Unofficial::1.2.601-preview.2
```

## Templates

| Short name | What it creates |
|---|---|
| `nemerle-console` | A console application (`Program.n`, `OutputType=Exe`). |
| `nemerle-classlib` | A class library (`Library.n`, `OutputType=Library`). |

```console
$ dotnet new nemerle-console -n MyApp
$ cd MyApp
$ dotnet build
$ dotnet run
```

Options: `--sdkVersion <version>` pins a different `Nemerle.Sdk.Unofficial` version in the
generated project; `--framework net10.0` selects the target framework.

## Requirements

`dotnet build` on a generated project resolves `Nemerle.Sdk.Unofficial` through the NuGet
MSBuild SDK resolver, so **a feed serving that package must be visible from the project
directory**. While the SDK is only published to a local feed, that means a `NuGet.config`
beside the project (or above it):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nemerle-local" value="/path/to/nupkg" />
  </packageSources>
</configuration>
```

The generated project pins the SDK version in its `Sdk` attribute. To pin it centrally
instead, drop the version from the attribute and use `global.json`:

```json
{ "msbuild-sdks": { "Nemerle.Sdk.Unofficial": "1.2.601-preview.2" } }
```

## License

BSD-3-Clause. Copyright (c) 2003-2008 The University of Wroclaw. Copyright (c) 2008-2014
Nemerle Project Team.
