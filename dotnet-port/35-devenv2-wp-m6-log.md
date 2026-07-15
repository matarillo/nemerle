# 35. WP-M6: Nemerle.Sdk NuGet package + project template + provenance 実装ログ

実施日: 2026-07-15

ブランチ: `wip/dotnet-port`

開始 commit: `5b5e0e4f6`(`Implement WP-M5 incremental rebuild via engine relocation path`)

対象: `dotnet-port/29-devenv2-plan.md` の **WP-M6** のみ。nuget.org 公開・署名・CI release は範囲外(WP-N)。

## 結論

`29-devenv2-plan.md` の WP-M6 を完了した。MSBuild project SDK 方式の
**`Nemerle.Sdk.Unofficial`** と、別 package の **`Nemerle.Templates.Unofficial`** を
`pack-tool.ps1 -Pack` が生成し、**repo checkout 無しで** `dotnet new nemerle-console` →
`dotnet build` → `dotnet run` が Windows・WSL の両方で成立することを実測した。
provenance(`ncc-info.json` ↔ `bundle-info.json`)を追加し、toolchain と language server の
世代混在を server 側の `window/showMessage` 警告として検出できるようにした
(**extension の TypeScript 実装は無変更**)。**compiler / engine(`ncc`・`lib`・`macros`・
`VsIntegration`)は無改造**(assembly version は WP-M1〜M5 と同一 601)。受け入れ基準 1〜7 を
自動・実測で確認した。

計画からの最大の逸脱は **targets の統合方法**である。§6.7 は「Windows/Linux 2 系統の
`Nemerle.Core.targets` を package 内で条件分岐に統合する」としていたが、調査の結果
**Linux 版は importer が1つも存在しないデッドコードだった**ため、条件分岐ではなく
**削除**し、`msbuild/Nemerle.Core.targets` **1 本**を repo(Windows/Linux)と package が
共有する形にした(§1)。結果として実装は単一で、package はそのファイルを**バイト同一**で
同梱する。

## 環境

- Windows 11 / PowerShell、WSL2(`dotnetdev`、Debian 系)
- .NET SDK `10.0.301` / 共有フレームワーク `Microsoft.NETCore.App 10.0.9`(WSL 側 `10.0.109` / `10.0.9`)
- Node.js `v22.x` / npm `11.x` / VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- Nemerle assembly version `1.2.0.601`(`dist/ncc`。engine 無改造で WP-M1〜M5 と同一)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。

## 配布方針(Project Owner と合意、2026-07-15)

package ID と version を実装前に議論して確定した(§6.7「preview 名を使うかを実装時に判断し
log に記録する」への回答)。

| | 決定 | 根拠 |
|---|---|---|
| package ID | `Nemerle.Sdk.Unofficial` / `Nemerle.Templates.Unofficial` | 既存の nuget.org 公開物(`Nemerle.Unofficial` / `Nemerle.Compiler.Unofficial` / `Nemerle.Macros.Unofficial` / `Nemerle.Compiler.Utils.Unofficial`、いずれも 1.2.547)と同じ「公式名 + `.Unofficial`」規約の継続 |
| version | `1.2.601-preview.1` | 既存 4 package の 1.2.547 は**公式 Nemerle 1.2.547 と同番号**だった。同じ規約で `1.2.<Nemerle.dll の revision>`。`dist/ncc/Nemerle.dll` の実 assembly version(1.2.0.601)から導出 |
| 公開先 | local feed のみ(`--add-source`) | nuget.org 公開は WP-N |

裏取りした事実(2026-07-15 に一次情報を確認):

- 公式 `Nemerle` package は owner `hardcase`、最新 1.2.547(2017-12-11 で停止)。
  **`Nemerle` prefix は予約されていない**(青チェック無し)ため、`Nemerle.*` の新規 ID 公開は
  nuget.org の policy 上ブロックされない。先例(matarillo の 4 package、計 12,000 DL 弱)も
  8 年間問題を起こしていない。
- **GitHub Packages は採らない**。NuGet registry は **public package でも匿名取得ができず
  `read:packages` の PAT が必須**(匿名可なのは ghcr.io だけ)。WP-M6 のゴールは
  「repo checkout 無しで試せる」ことなので、checkout 依存を PAT + NuGet.config 依存に
  置き換えるだけで体験は悪化する。nuget.org を避ける実在の理由(prefix 予約・権利者の反対)も無い。
  <https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry>

**「DevPreview」を ID に入れない理由**(当初案から変更): 出所(unofficial)と成熟度(preview)は
性質の違う2軸である。出所は**恒久的に真**なので ID に入れてよいが、成熟度は**いずれ必ず偽になる**。
`Nemerle.Sdk.DevPreview` は安定して普及した瞬間に改名を強制され、しかも project SDK の ID は
全ユーザーの `.nproj` root 要素と `global.json` に焼き込まれるため、**最悪のタイミングで最大の
コスト**を払う。prerelease label は NuGet の一級市民(`--prerelease` 明示が必要、VS は既定で非表示、
SemVer 2.0 で `1.2.601-preview.1 < 1.2.601` と順序が付く)であり、成熟度はそちらが担う。

**WP-N への申し送り**: 既存 `Nemerle.Unofficial`(net4x 消費者が 3,219 DL 分いる ID)に net10.0
ビルドを `1.2.601` として出すと、patch bump の顔をした破壊的変更になる。新 ID
(`Nemerle.Sdk.Unofficial`)は既存消費者ゼロなので無問題。runtime library 側を出す判断は別途。

## 設計と実装

### 1. targets の統合: 条件分岐ではなく削除(§6.7 からの逸脱)

WP-M6 の出発点は「Windows 版 `msbuild/Nemerle.Core.targets` と Linux 版
`msbuild/linux/Nemerle.Core.targets` の 2 本があり、差分は layout dir 既定・起動形
(`dotnet` vs `dotnet exec`)・path 区切りの 3 点」という `DISTRIBUTION.md` の記述だった。
package 用に 3 本目を作れば drift 源が増えるため、当初は「共通実装 1 本 + 薄い shim 3 本」を
設計した(Project Owner と合意)。実装中に調査したところ:

- **`msbuild/linux/Nemerle.Core.targets` を `<Import>` している .nproj / script / test は
  リポジトリ内に1つも無い**(参照は `00-PLAN.md` と過去ログの記述のみ)。
- 既定にしていた layout dir `msbuild/ncc/` は**ディレクトリ自体が存在しない**。
- それでいて 4 commit(253d2ecb3 / 9f9c83d18 / 6ecdf5a4b / 2437d5bfd)にわたり、
  Windows 版の変更が都度手で複製されていた。
- `DISTRIBUTION.md` の「WSL で end-to-end 実証済み」は、samples が import している
  **Windows 版**を経由していたはず(MSBuild は Unix で `\` を正規化する)。

つまり 3 つの差分のうち、**layout dir 既定は Linux 側の勝手な慣習**、**起動形は
`dotnet exec` に統一可能**(`DISTRIBUTION.md` §1 が「`dotnet <path>\ncc.exe` は exec 無しでも
動く」= exec が基準形であることを実証済み)、**path 区切りは `/` で両 OS 可**であり、
条件分岐する必要が無かった。実測でこれを確認した(§検証 基準2)ため、Linux 版を削除し
**1 ファイル構成**にした。

```
msbuild/Nemerle.Core.targets   <- 唯一の実装(パス・名前とも従来どおり = 既存 importer 14 本に影響ゼロ)
msbuild/sdk/Sdk.props          <- package 入口。NccLayoutDir 等を上書きしてから上記を import
msbuild/sdk/Sdk.targets
```

package はこの `Nemerle.Core.targets` を `targets/` に**バイト同一**で同梱する
(`ProjectInfo.Test` の `SdkPackageTests` が byte 同一性を assert)。
「共通ファイル + shim」案より単純で、既存 fixture への影響も小さい。

`NccLayoutDir` の既定 `$(MSBuildThisFileDirectory)../dist/ncc/` は package 内では
`<package>/dist/ncc/` という無意味な path を指すが、`Sdk.props` が先に値を設定するので
発火しない。`Exists()` 条件で package 側を空にする案は却下した: repo で `pack-tool.ps1` を
忘れた場合のエラーが「`dist\ncc` に ncc.dll が無い → pack-tool.ps1 を実行せよ」から
「空文字列に ncc.dll が無い」に劣化するため。**片方の consumer で到達しない既定値のコストは
ゼロだが、最頻出のミスに対するエラー品質の劣化は全新規ユーザーが払う。**

### 2. project SDK 方式(§6.7)

`Sdk/Sdk.props` → `Microsoft.NET.Sdk` の `Sdk.props` を明示 import、
`Sdk/Sdk.targets` → `Microsoft.NET.Sdk` の `Sdk.targets` を明示 import **してから**
`../targets/Nemerle.Core.targets` を import する。MSBuild は同名 target の最後の定義が勝つため、
Nemerle の `CoreCompile` が csc のそれに勝つ。`build/<id>.targets` 規約を使うと NuGet が
自動 inject し、まさに csc が勝つ構図(`DISTRIBUTION.md` タスク3)になるので、package 内の
targets 置き場は**規約外の `targets/`** にしてある(意図的)。

`.csproj` では `Microsoft.NET.Sdk` が C# の言語 targets(と csc の `CoreCompile`)を
import してしまい救えないため、`Sdk.targets` に `NemerleValidateProjectExtension` を置き、
`.csproj` を検出したら**「`.nproj` にリネームせよ」と明示的に error**にする(csc からの
CS5001 という無関係なエラーで悩ませない)。

SDK が肩代わりする既定(すべて project 側で上書き可):

| property | 既定 | 理由 |
|---|---|---|
| `EnableDefaultCompileItems` | `false` | `.n` は C# ではない。SDK の `**/*.cs` glob を止める |
| `UseAppHost` | `false` | ncc の出力は `dotnet <dll>` で直接動く |
| `GenerateDependencyFile` | `false` | **load-bearing**。下記 |
| `EnableDefaultNemerleCompileItems` | `true` | `**/*.n` を glob(新規) |

`GenerateDependencyFile=false` は整理ではなく**必須**である。`Nemerle.dll` 等は
`NemerleCopyRuntimeAssemblies` が出力先へコピーするだけで package graph を通らないため、
deps.json が生成されるとそこに載らず、**deps.json がある場合 host はそれを基準に assembly を
解決して隣接ファイルを無視する**ので実行時に `Nemerle.dll` が見つからなくなる。
deps.json が無ければ host は app ディレクトリを probe して解決する。WP-I2 以降すべての
sample `.nproj` が同じ理由でこれを設定していた。**筋の良い解決**は Nemerle runtime を
本物の `PackageReference` として配り deps.json に正当に載せることだが、net10.0 の runtime
package 公開(= WP-N)が要る。

### 3. `**/*.n` の default glob は Sdk.props ではなく Sdk.targets に置く(バグ修正)

当初 `Sdk.props` に glob を書いた。MSBuild は item(pass 3)を**ドキュメント順**に評価し、
`Sdk.props` は project 本文より**前**にある。したがって:

- glob が本文の `<NemerleCompile Include="...">` より先に走り、**互いを見ないまま加算される**。
- 結果、既存 project を Sdk 形式へ**変換する**(= 受け入れ基準 3 が要求する移行経路)と、
  全 source が 2 回 ncc に渡り `file 'Math.n' occured twice on the list to compile` で失敗する。
  原因ではなく compiler を指すエラーになる。

これは受け入れ基準 3 の検証中に実際に踏んだ。`Microsoft.NET.Sdk` も同じ順序問題を抱えており、
**衝突を検出して error**(NETSDK1022: item を消すか `EnableDefaultCompileItems=false` にせよ)で
答えている。本 SDK は glob を **`Sdk.targets`(本文の後)** に置き `Exclude="@(NemerleCompile)"` を
付けることで、**error より良い**答えにした: 明示 item が見えるので glob がそれに譲り、
手書きの source はそのまま動き、glob が残りを埋める。

残る重複に備え、`Nemerle.Core.targets` に `NemerleCheckForDuplicateCompileItems` を追加した
(full path で比較し、対処法を含む error)。

> **追記(README 執筆時の実測で判明、2026-07-15)**: 当初この項に「`Exclude` は文字列一致なので
> 絶対パス表記の item は拾えない。その保険が重複検出である」と書いたが、**誤りだった**。
> MSBuild の `Exclude` は**正規化したパスで照合する**ため、glob の相対パス結果と本文の
> 絶対パス item も畳み込まれる(実測: 項目数 1)。したがって重複検出が実際に発火するのは
> 「project 自身が重なった ItemGroup で同一ファイルを 2 回挙げた」場合であり、
> こちらは実測で発火とメッセージを確認した。併せて Error の `Text` に書いた
> `@(NemerleCompile)` が **MSBuild に展開されてしまい**、
> `Duplicate Program.n;Program.n items:` と表示される bug も実測で見つけ、`%40` に修正した。

### 4. macro library の compiler 参照(`NemerleMacroLibrary`、新規)

受け入れ基準 3 の検証で 2 つ目の実質的な gap を発見した。macro を**定義する** project は
quasi-quotation のために `Nemerle.Compiler.dll` を compile 時参照する必要があるが
(ncc の auto-ref は BCL のみ)、`samples/Sokoban/SokobanMacros.nproj` はそれを
**repo 相対の HintPath**(`..\..\..\dist\ncc\Nemerle.Compiler.dll`)で書いていた。
package 利用者にとってこの path は **NuGet global packages folder 内の版付きディレクトリ**であり、
project file に portable に書き下すことが**原理的に不可能**である。つまり
**macro library は Sdk package では作れなかった**。

`<NemerleMacroLibrary>true</NemerleMacroLibrary>` を `Nemerle.Core.targets` に追加し、
`$(NccLayoutDir)Nemerle.Compiler.dll` への `Reference`(`Private=false`)を SDK 側が
供給するようにした。repo checkout でも package でも同じ 1 property で済む。
Sdk 形式へ変換した Sokoban で実証済み(§検証 基準3)。

### 5. provenance と版不一致警告(§6.8、受け入れ 6)

- `pack-tool.ps1` が `dist/ncc/ncc-info.json`(commit / describe / configuration /
  **nemerleAssemblyVersion** / sourceDir / packedAtUtc)を書き、package の `tools/ncc/` にも含める。
- `ProjectInfo` の `MsBuildProjectQuery` に **`NccLayoutDir` の `-getProperty` を追加**。
  「その project が実際にどの compiler でビルドされるか」は project / global.json / SDK package /
  コマンドラインのどれもが設定しうる property なので、MSBuild に訊くのが唯一正直な方法。
  snapshot(`NemerleProjectSnapshot.NccLayoutDir`)に載せる。
- `ToolchainProvenance`(純ロジック + unit test)が server 側(`bundle-info.json`)と
  toolchain 側(`ncc-info.json`)を読み、**両方の assembly version が既知で、かつ食い違う場合のみ**
  警告文を返す。**片方でも不明なら黙る**(「証拠が無い」を「不一致」と言わない。
  無条件に鳴る警告は誰にも読まれなくなる)。version は json の自己申告ではなく
  **`Nemerle.dll` から実読**する(不一致の主語は assembly identity そのものだから)。
- `WorkspaceManager` が snapshot 適用時に比較し、一致なら Info ログ、不一致なら
  Warning ログ + **`window/showMessage`(Warning)**。同一 layout に対する再 reload では
  toast を繰り返さない。

**§6.8 は「extension が warning を表示する」と書いていたが、`window/showMessage` は
client が既に処理する protocol 上の通知なので、server から直接出せる**。これにより
指示の「extension TS src は無変更」方針と受け入れ基準 6 を両立し、かつ検証を
Extension Host ではなく**決定的な raw LSP integration test** で行えるようにした。

## 実装ファイル

- 新規: `dotnet-port/msbuild/sdk/Sdk.props`、`dotnet-port/msbuild/sdk/Sdk.targets`、
  `dotnet-port/packaging/Nemerle.Sdk.Unofficial/{Nemerle.Sdk.Unofficial.csproj,README.md}`、
  `dotnet-port/packaging/Nemerle.Templates.Unofficial/{Nemerle.Templates.Unofficial.csproj,README.md,content/**}`、
  `dotnet-port/ProjectInfo/ToolchainProvenance.cs`、
  `dotnet-port/vscode-nemerle/test/runSdk.ts`、`dotnet-port/vscode-nemerle/test/suite/sdk.test.ts`、本ログ。
- 変更: `dotnet-port/msbuild/Nemerle.Core.targets`(統合実装 + `NemerleMacroLibrary` +
  重複 item 検出 + `dotnet exec` 統一)、`dotnet-port/pack-tool.ps1`(`ncc-info.json`、
  `-Pack`/`-PackageOutDir`/`-PackageVersionSuffix`、template staging、NuGet cache eviction)、
  `dotnet-port/ProjectInfo/{MsBuildProjectQuery.cs,MsBuildJsonParser.cs,ProjectSnapshot.cs}`
  (`NccLayoutDir`)、`dotnet-port/LspServer/{ServerLog.cs,WorkspaceManager.cs,Program.cs}`
  (`ShowWarning`、provenance 検査、ServerInfo 0.8.0)。
- 変更(test): `dotnet-port/ProjectInfo.Test/Program.cs`(`ToolchainProvenanceTests` /
  `SdkPackageTests`)、`dotnet-port/LspServer.IntegrationTest/{Program.cs,LspTestClient.cs}`
  (provenance 2 シナリオ、`ShowMessages`)、
  `dotnet-port/vscode-nemerle/test/{suite/index.ts,unit/manifest.test.ts}`。
- 変更(doc/meta): `dotnet-port/DISTRIBUTION.md`、`dotnet-port/00-PLAN.md`、
  `dotnet-port/vscode-nemerle/{package.json,.vscodeignore,README.md}`、`.gitignore`。
- 削除: `dotnet-port/msbuild/linux/Nemerle.Core.targets`(§1)。
- `ncc/` `lib/` `macros/` `VsIntegration/`(engine 共有ソース)は**無変更**。
  extension の実装 TypeScript(`src/`)も**無変更**。

## 検証(受け入れ基準)

### 基準 1: repo 外の空 dir + local feed のみで `dotnet new` → build → run(Windows)

`%TEMP%` 配下の空 dir に `<clear />` + local feed だけの `NuGet.config` を置き:

```
dotnet new install Nemerle.Templates.Unofficial::1.2.601-preview.1 --add-source <feed>
dotnet new nemerle-console -n HelloSdk   -> HelloSdk.nproj (Sdk="Nemerle.Sdk.Unofficial/1.2.601-preview.1")
dotnet build                             -> 成功 (0 警告 0 エラー)
dotnet run                               -> "Hello from Nemerle on .NET 10!"
```

`NccLayoutDir` は `C:\Users\kenta\.nuget\packages\nemerle.sdk.unofficial\1.2.601-preview.1\Sdk\../tools/ncc/`。
出力に `HelloSdk.pdb` と `Nemerle*.dll` が揃う。

### 基準 2: 同一 package が WSL(Linux)で build → 実行

同じ nupkg を WSL(`dotnetdev`)の local feed から install:

```
dotnet new nemerle-console -n HelloWsl / dotnet build -> Build succeeded
dotnet run                                            -> "Hello from Nemerle on .NET 10!"
NccLayoutDir = /home/wsl/.nuget/packages/nemerle.sdk.unofficial/1.2.601-preview.1/Sdk/../tools/ncc/
```

加えて **§1 の根拠**として、統合 targets が **WSL の repo checkout でも**そのまま機能することを
実測した(`dotnet build dotnet-port/samples/HelloCore/HelloCore.nproj` → 成功 →
`Hello from HelloCore (SDK-style dotnet build)!`)。これが Linux 版 targets 削除の実証である。

### 基準 3: ProjectReference / NemerleMacroReference / PackageReference の Sdk 形式

`samples/{RefDemo,Sokoban,PackageReference}` を repo 外へ複製し Sdk 形式へ変換(`<Project Sdk=...>` 化、
`<Import>` と定型 property を削除)して build:

| fixture | 種別 | 結果 |
|---|---|---|
| `RefDemo/App` | ProjectReference | **OK**(`dotnet run` も成功) |
| `Sokoban` | NemerleMacroReference(macro-only) | **OK**(`<NemerleMacroLibrary>true</NemerleMacroLibrary>` 使用。macro 展開後の実行も確認) |
| `PackageReference` | PackageReference(Newtonsoft.Json 13.0.4) | **OK** |

この検証中に §3(重複 source)と §4(macro の compiler 参照)の実バグを発見・修正した。

### 基準 4: VS Code extension が Sdk-based project で project-aware diagnostics を表示

`npm run test:sdk`(新規)。`test/runSdk.ts` が local feed の nupkg から版を読んで
`test-workspace-sdk/` を**生成**し(版と feed path はどちらもコミットできないため)、
restore してから Extension Host を起動する。型エラーを含む `SdkProbe.n` に対し:

- `projectStatus.state === 'applied'` / `appliedToEngine === true` / `sourceFiles = [SdkProbe.n]`
  (= package 内 targets で MSBuild query が成立し snapshot が engine に適用された)
- error diagnostic が出る → 未保存修正で 0 件に戻る

1 passing。project は `<Project Sdk="...">` + `OutputType`/`TargetFramework` の**4 行のみ**
(`<Import>`・item・定型 property 無し)。

### 基準 5: `<Project Sdk="...">` 形式と `global.json` の `msbuild-sdks` 形式の両方

同一 project から Sdk 属性の版を外し `global.json` に
`{ "msbuild-sdks": { "Nemerle.Sdk.Unofficial": "1.2.601-preview.1" } }` を置いて build → 成功。

### 基準 6: 版不一致で warning、同一 commit では非表示

raw LSP integration に 2 シナリオを追加(いずれも PASS):

- **一致**: repo の `dist/ncc` を使う temp project → `nemerle project toolchain: 1.2.0.601 (...) (matches the language server)` が
  Info ログに出て、**`window/showMessage` は 0 件**。
- **不一致**: `<NccLayoutDir>` を、別 assembly を `Nemerle.dll` として置いた偽 layout に向けた temp project →
  `window/showMessage`(type 2 = Warning)が届き、本文が**両方の版**を名指しする。
  (port は一度に 1 世代しか作らないので、版違いの `Nemerle.dll` は他の managed assembly を
  改名して作る。provenance 検査は assembly version を読むだけで、project-info query は
  `-target:ResolveReferences` なので ncc は起動されない。)

unit test(`ToolchainProvenanceTests`)で規則を固定: 同版 → 無警告、異版 → 両方を名指しする警告、
**片方でも不明 → 無警告**、**同版で commit だけ違う → 無警告**(binding は version が支配するため)、
provenance の欠損/不在ディレクトリ → 例外ではなく Unknown。

### 基準 7: clean / incremental / PDB / vulnerable の Sdk 版回帰

- incremental: 2 回目の build で `CoreCompile` が「すべての出力ファイルが入力ファイルに対して最新」でスキップ。
- `dotnet clean`: `bin\Debug\net10.0` が 0 ファイルに(コピーされた `Nemerle*.dll` 含む)。
- PDB: `HelloSdk.pdb` が出力される。
- `dotnet list package --vulnerable --include-transitive`: LspServer / ProjectInfo /
  ProjectInfo.Test / LspServer.IntegrationTest / Nemerle.MSBuild.Tasks /
  Nemerle.Sdk.Unofficial / Nemerle.Templates.Unofficial の**全 7 project で脆弱 package なし**。

### package の中身(`SdkPackageTests` が assert)

```
Sdk/Sdk.props, Sdk/Sdk.targets
targets/Nemerle.Core.targets            <- repo の同名ファイルとバイト同一を assert
tasks/Nemerle.MSBuild.Tasks.dll
tools/ncc/{ncc.dll, ncc.runtimeconfig.json, Nemerle.dll, Nemerle.Compiler.dll,
           Nemerle.Macros.dll, Nemerle.CoreEmit.dll, Nemerle.Compiler.Hosting.dll, ncc-info.json}
```
`Nemerle.Sdk.Unofficial.1.2.601-preview.1.nupkg` = 1.00 MB、
`Nemerle.Templates.Unofficial.1.2.601-preview.1.nupkg` = 5.6 KB。

**除外を明示的に assert** している: `ncc.default.rsp` / `ncc.cmd` / `gen-default-rsp.ps1`
(machine 固有の絶対 path を焼き込む = package を再配置不能にする。MSBuild は in-process 呼び出しで、
ncc は auto-ref なので不要)、`nunit.framework.dll`(pack-tool.ps1 の `*.dll` 一括コピーが
Stage2 出力から拾う迷子)、`ncc.exe`。

evaluation test(§8 が要求する `-getProperty` テスト)で、`Microsoft.NET.Sdk` 由来
(`TargetFramework`)と Nemerle 由来(`NccLayoutDir`、`ProduceReferenceAssembly=false`、
`UseAppHost=false`、`GenerateDependencyFile=false`)の property 共存と、
default glob が source をちょうど 1 回拾うことを固定した。

### 回帰ゲート(WP-L2/L3/M1〜M5)

- repo samples 7 本(`HelloCore` / `RefDemo` / `Sokoban` / `PackageReference` / `Defines` /
  `Warnings` / `CompTimeSolver/Maze`)を `-t:Rebuild` で全 OK。`-p:NemerleUseExec=true`
  fallback(`dotnet exec` 統一後)も OK。
- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query + 新規 provenance / Sdk package)。
- raw LSP integration: **29/29** シナリオ PASS(既存 27 + provenance 2)。
  bundled server(VSIX 抽出)でも 29/29 PASS。
- extension: `npm run check-types` / `lint` / `npm test`(**22/22**、`.vscodeignore` の新規 assert 含む)/
  `test:integration`(trusted 4 + untrusted 1)/ `test:sdk`(1)/ `package`
  (`vscode-nemerle-0.8.0.vsix`、**468 files / 4.45 MB**)/ `test:vsix`(隔離 install 1 passing)/
  `test-bundled-server.ps1`(29/29)。
- `npm audit` / `npm audit --omit=dev` = 0。
- compiler / engine 無改造のため Stage リビルド・testsuite 再実行は不要(assembly version 601 のまま)。

途中で **VSIX に `test-workspace-sdk/`(9 files)が同梱される**不具合を自分で作り込み、
`vsce package` の出力ツリーで検知した。`.vscodeignore` に追加して 477 → **468 files** に戻し、
再発防止に `manifest.test.ts` へ「`test-workspace*` は全て `.vscodeignore` で除外されていること」の
assert を追加した。

## 再現コマンド

```powershell
# 1. toolchain layout + provenance + 2 つの nupkg
pwsh dotnet-port\pack-tool.ps1 -Pack          # -> dotnet-port\dist\ncc, dotnet-port\dist\nupkg
#    版を変えずに再 pack する場合も同じでよい(script が global packages の該当 (id,version) を退避する)

# 2. server / handler
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild

# 3. unit + integration(provenance / Sdk package の assert を含む)
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll

# 4. repo 外の空 dir で developer preview を試す(基準 1)
#    <feed> = <repo>\dotnet-port\dist\nupkg
dotnet new install Nemerle.Templates.Unofficial::1.2.601-preview.1 --add-source <feed>
#    空 dir に <clear/> + <feed> だけの NuGet.config を置いてから:
dotnet new nemerle-console -n HelloSdk; cd HelloSdk; dotnet build; dotnet run
dotnet new uninstall Nemerle.Templates.Unofficial    # 後片付け(dotnet new install はマシン全体)

# 5. server を再 staging(サーバーコード変更時)
pwsh dotnet-port\vscode-nemerle\pack-server.ps1

Push-Location dotnet-port\vscode-nemerle
npm ci; npm run check-types; npm run lint; npm test
npm run test:integration; npm run test:sdk; npm run package; npm run test:vsix
pwsh .\test-bundled-server.ps1
npm audit; npm audit --omit=dev
Pop-Location
```

WSL(基準 2):

```bash
FEED=/mnt/f/.../dotnet-port/dist/nupkg
dotnet new install "Nemerle.Templates.Unofficial::1.2.601-preview.1" --add-source $FEED
# 空 dir に <clear/> + $FEED の NuGet.config を置いてから:
dotnet new nemerle-console -n HelloWsl && cd HelloWsl && dotnet build && dotnet run
```

前提: `dotnet-port\dist\ncc` と `dotnet-port\dist\nupkg`(いずれも `pack-tool.ps1 -Pack`)。
`test:sdk` は nupkg が無いと明示メッセージで失敗する。生成物(`dist/`、`server/`、`.vsix`、
`*.nupkg`、`test-workspace-sdk/`)は gitignore 済みで commit しない。

## 既知の制約 / 次 WP への境界

1. **公開は local feed 止まり**。nuget.org 公開・署名・CI release は WP-N。ID / version は
   確定済み(上記「配布方針」)なので、WP-N は公開工程だけを扱えばよい。
2. **`GenerateDependencyFile=false` に依存している**(§2)。Nemerle runtime が package graph を
   通らないことの裏返しで、筋の良い解決は runtime を本物の `PackageReference` として配ること
   (net10.0 runtime package の公開 = WP-N)。それまで、deps.json を要する構成
   (一部の plugin host 等)は Sdk package では扱えない。
3. **版を据え置いての再 pack**は、`pack-tool.ps1` が global packages folder の該当 (id,version) を
   退避することで正しく動くようにした。ただし**他マシン**に配った同版は更新されない
   (NuGet は (id,version) を内容ごとキャッシュする)。配る版は `-PackageVersionSuffix` を上げること。
4. **`ncc-info.json` の commit は「working tree が dirty なら dirty と記録される」**
   (`pack-server.ps1` の `bundle-info.json` と同じ挙動)。本 WP の実測はすべて dirty tree
   (`5b5e0e4f6-dirty`)で行っており、記録上もそうなっている。commit 後に再 pack すれば
   clean な describe になる。
5. **provenance 検査は assembly version の一致だけを見る**。同版で commit が違う組
   (例: doc だけ変えて再ビルド)は警告しない。binding が支配されるのは version だからで、
   これは意図的(§5)。commit まで一致させたい用途には `ncc-info.json` の `commit` を直接使う。
6. **`test:sdk` は `npm run package` の依存には入れていない**。nupkg を要求するため、
   pack-tool.ps1 -Pack を実行していない環境で `package` が落ちるのは筋が悪い。CI では
   pack → `test:sdk` の順に明示的に並べること。
7. **`dotnet new install` はマシン全体**に効く(project ローカルではない)。検証後は
   `dotnet new uninstall Nemerle.Templates.Unofficial` すること。
8. `.csproj` は使えない(SDK が error にする)。`.nproj` 制約自体は WP-I2 からの既知事項で、
   本 WP はエラーメッセージを改善しただけ。

前身の実装ログ: `30-devenv2-wp-m1-log.md`(WP-M1)/ `31-devenv2-wp-m2-log.md`(WP-M2)/
`32-devenv2-wp-m3-log.md`(WP-M3)/ `33-devenv2-wp-m4-log.md`(WP-M4)/
`34-devenv2-wp-m5-log.md`(WP-M5)。計画: `29-devenv2-plan.md`。配布の現状: `DISTRIBUTION.md`。
