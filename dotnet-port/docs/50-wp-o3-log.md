# 50. WP-O3 実装ログ — net10 runtime package の根治

実施日: 2026-07-23

ブランチ: `wip/dotnet-port`

## 結論

`47-wp-o-plan.md` の WP-O3 を完了した。D1(SDK 消費が `GenerateDependencyFile=false` に依存)を
根治した。Nemerle のランタイム閉包を NuGet パッケージ `Nemerle.Runtime.Unofficial` として
package graph に載せ、`Nemerle.Sdk.Unofficial` がこれを暗黙参照するようにした。SDK 消費プロジェクトは
`GenerateDependencyFile` を SDK 既定(`true`)のまま build・run でき、生成される `deps.json` が
ランタイム閉包を正しく列挙する。

- 新パッケージ `Nemerle.Runtime.Unofficial` = `lib/net10.0/{Nemerle,Nemerle.Macros,Nemerle.Compiler}.dll`。
  版は SDK と同一(パック対象コンパイラーの assembly version 由来)。
- `Sdk.props` が `<PackageReference Include="Nemerle.Runtime.Unofficial" ExcludeAssets="compile" />`
  を暗黙で追加する。`ExcludeAssets="compile"` により、これらは **runtime 資産**(copy-local +
  `deps.json`)としてのみ供給され、**コンパイル参照(`-ref:`)には入らない**。ncc は
  `Nemerle.dll` を自前のレイアウトから auto-resolve するため二重 `-ref:` を避け、マクロ支援
  アセンブリの型が消費側の compile scope に入ることも防ぐ。
- `Sdk.props` から `GenerateDependencyFile=false` 既定を撤去。消費側が明示的に `false` にしても、
  ランタイム資産はパッケージの copy-local で出力へ入るため probing で解決でき、動作する。
- checkout 形式(`msbuild/Nemerle.Core.targets` を直接 import する samples / test fixtures)は
  パッケージを持たないので従来どおり `NemerleCopyRuntimeAssemblies` でレイアウトから copy-local し、
  各 `.nproj` が `GenerateDependencyFile=false` を維持する。この二経路は
  `NemerleRuntimeProvidedByPackage` プロパティで分岐する(Sdk.props が `true` を設定)。

WP-O4 への引き継ぎ: 公開する場合の恒久的なパッケージ命名(単一 bundling の
`Nemerle.Runtime.Unofficial` を維持するか、既存 net4x 資産 `Nemerle.Unofficial` /
`Nemerle.Compiler.Unofficial` / `Nemerle.Macros.Unofficial`(1.2.547)へ寄せるか)は WP-O4 の判断。
本 WP の deps.json 機構はこの選択と独立で、local feed で価値が成立している。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301`
- パック対象コンパイラー世代: Nemerle assembly version `1.2.0.635`(version.txt 固定 `1.2.635`)。
- npm / NuGet の依存 package の追加・更新は無い。

## 設計

### deps.json への供給機構

`.NET SDK` の `GenerateDepsFile` タスクは、`deps.json` の runtime 一覧をパッケージ/プロジェクト由来の
resolved ファイル(`@(RuntimeCopyLocalItems)` 等、NuGet ライブラリ項目に紐づくもの)から構成する。
素のファイルパスや `@(UserRuntimeAssembly)` への直接注入はライブラリ項目に紐づかず脱落するため、
`deps.json` にランタイムを載せるには **NuGet ライブラリ項目として供給する**必要がある。よって
runtime を実パッケージ化し、SDK が暗黙参照する構成が唯一機能する経路。

### パッケージ構成

`packaging/Nemerle.Runtime.Unofficial/Nemerle.Runtime.Unofficial.csproj` は
`Nemerle.Sdk.Unofficial` と同じく「dist\ncc レイアウトの既ビルド物を再パッケージするだけ」の
csproj(compile なし)。3 つの dll を `lib/net10.0/` 資産として pack する。lib/ を持つ通常の
lib パッケージなので `SuppressDependenciesWhenPacking` は設定せず、空の net10.0 依存グループを
そのまま出す(NU5128 回避)。版検証・レイアウト検証は SDK csproj と同型の Target。

### 版のピン(pack 時 staging)

`Sdk.props` の runtime PackageReference 版は SDK パッケージ世代と厳密一致が必要(浮動版は別世代を
引く恐れ)。テンプレート / README と同じ staging パターンで、`pack-tool.ps1` が
`msbuild/sdk/Sdk.props` を `dist/sdk/Sdk.props` へ複製し `__NEMERLE_RUNTIME_VERSION__` を
`$PackageVersion` へ置換、SDK csproj に `-p:NemerleStagedSdkPropsFile=` で渡す。SDK csproj は
`Sdk.targets` は verbatim、`Sdk.props` は staged 版を `Sdk/Sdk.props` として pack する。
`Sdk.props` はパッケージ専用(checkout は `Nemerle.Core.targets` を直接 import し `Sdk.props` を
使わない)なので、ソース側にプレースホルダが残っても in-tree ビルドに影響しない。

### smoke-release の D1 ガード

console テンプレートの `Main` は `Console.WriteLine` のみで Nemerle.dll を実行時に使わないため、
閉包が壊れても素の実行では検出できない。`smoke-release.ps1` に 2 点のガードを追加した:
(a) 生成物の `deps.json` が生成され `Nemerle.dll` を列挙すること、(b) `Program.n` を
ランタイムを実際に使うコード(`string.Join(" ", list)`)へ差し替えて再ビルド・実行し、
`GenerateDependencyFile` 既定(`true`)で `deps.json` 経由にランタイムが load されること。
テンプレート自体(ユーザーの第一印象)は変更しない。

## 実装ファイル

- 新規: `packaging/Nemerle.Runtime.Unofficial/Nemerle.Runtime.Unofficial.csproj`、
  `packaging/Nemerle.Runtime.Unofficial/README.md`。
- 変更: `msbuild/sdk/Sdk.props`(runtime PackageReference・`NemerleRuntimeProvidedByPackage=true`・
  `GenerateDependencyFile=false` 既定の撤去)、
  `msbuild/Nemerle.Core.targets`(`NemerleCopyRuntimeAssemblies` を
  `NemerleRuntimeProvidedByPackage != 'true'` で分岐)、
  `packaging/Nemerle.Sdk.Unofficial/Nemerle.Sdk.Unofficial.csproj`(staged Sdk.props を pack、検証追加)、
  `pack-tool.ps1`(runtime package を pack 対象・cache eviction・README staging へ追加、Sdk.props staging)、
  `smoke-release.ps1`(D1 ガード 2 点)、
  `packaging/README.md` / `packaging/Nemerle.Sdk.Unofficial/README.md`(runtime package の記載、
  GenerateDependencyFile 記述の更新)、
  `ProjectInfo.Test/Program.cs`(SDK パッケージ評価テスト `SdkPackageTests` の
  `GenerateDependencyFile` アサートを SDK 既定 `true` へ = deps.json が生成され runtime 閉包が
  Nemerle.Runtime.Unofficial 由来で載る、という WP-O3 の期待値に一致させる)。
- 共有ソース(`ncc/`・`lib/`・`macros/`)と `VsIntegration/` は無変更 = Stage リビルド不要。

## 検証

### パック(`pwsh dotnet-port/pack-tool.ps1 -Pack`)

4 パッケージを `dist/release` へ生成(いずれも `1.2.635-preview.1`):
`Nemerle.Sdk.Unofficial` / `Nemerle.Runtime.Unofficial` / `Nemerle.Templates.Unofficial` /
`Nemerle.Linq.Unofficial`。0 warning。SDK nupkg 内 `Sdk/Sdk.props` は版置換済み
(`Version="1.2.635-preview.1"`、プレースホルダ残存なし)。runtime nupkg は
`lib/net10.0/{Nemerle,Nemerle.Macros,Nemerle.Compiler}.dll` + README を含む。

### 消費 e2e(`pwsh dotnet-port/smoke-release.ps1`)

`<clear />` した local feed のみで install → `dotnet new nemerle-console` → build → run が成立
(`Hello from Nemerle on .NET 10!`)。追加ガード PASS: 生成物 `deps.json` が生成され `Nemerle.dll` を
列挙、ランタイム使用版が `GenerateDependencyFile` 既定(`true`)で run 成立。

### SDK パッケージ評価(`ProjectInfo.Test -- --integration` の `SdkPackageTests`)

`<clear />` local feed から `Nemerle.Sdk.Unofficial` を解決した probe `.nproj` を `dotnet msbuild
-getProperty` で静的評価し、`GenerateDependencyFile` が SDK 既定 `true`(WP-O3 後)であることを含め
PASS。packaged targets とレイアウト構成の検証も同テスト内で継続。`PASS project info unit + sample
integration tests`。

### SDK パッケージ経由の Library / 診断(`npm run test:sdk`)

`<clear />` local feed のみで `<Project Sdk="Nemerle.Sdk.Unofficial/1.2.635-preview.1">` の Library を
restore・build し、VS Code 拡張ホストで project-aware 診断 1/1 PASS。Sdk.props の暗黙
PackageReference が feed から復元されることを LSP 経路でも確認。

### 回帰(checkout 形式 samples)

`HelloCore` / `PackageReference` / `Warnings` / `Defines` は build 成功。`Sokoban`(マクロ消費)は
3 つのランタイム dll を出力へ copy-local(`NemerleCopyRuntimeAssemblies` 継続)して build 成功。
`RefDemo/App`(ProjectReference)は build・run 成功(`Square(7) = 49` / `Sum([1..5]) = 15`)。
= Sdk.props / GenerateDependencyFile の変更は checkout 形式に影響しない。

## 再現コマンド

```powershell
pwsh dotnet-port/build-libs-core.ps1
pwsh dotnet-port/pack-tool.ps1 -Pack
pwsh dotnet-port/smoke-release.ps1
dotnet run -c Release --project dotnet-port/ProjectInfo.Test/Nemerle.ProjectInfo.Test.csproj -- --integration
Push-Location dotnet-port/vscode-nemerle; npm run test:sdk; Pop-Location
# checkout 形式の回帰(dist/ncc 前提):
dotnet build dotnet-port/samples/Sokoban/Sokoban/Sokoban.nproj -c Debug
dotnet build dotnet-port/samples/RefDemo/App/App.nproj -c Debug
```

## 既知の制約 / 残課題

1. 公開時の恒久的なパッケージ命名・既存 net4x 資産(`Nemerle.Unofficial` 等 1.2.547、累計 3,219 DL)
   との関係は WP-O4 の判断。本 WP は local feed で成立し、命名選択と独立。
2. checkout 形式(samples / test fixtures)は引き続き `GenerateDependencyFile=false` +
   `NemerleCopyRuntimeAssemblies`。パッケージを持たない dev 経路であり、D1 の対象外。
3. Library をパックして配布する場合、その `deps.json`/依存に runtime が推移的に伝播する
   (`ProjectReference` 経由では消費 exe の `deps.json` に閉包が入る)ことを RefDemo で確認済み。
   Library 単体の bin にランタイム dll は入らないが、実行主体でないため問題ない。
