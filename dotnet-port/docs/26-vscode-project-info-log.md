# WP-L2 VS Code project information provider 実装ログ

実施日: 2026-07-13

ブランチ: `wip/dotnet-port`

開始 commit: `ba0197182a295f4bc915f591b7bf7211227cd83c`

## 結論

`24-vscode-development-plan.md` の WP-L2 を完了した。SDK-style `.nproj` を
`dotnet msbuild` で評価し、source、framework facade を除いた assembly reference、
macro-only reference、define と compiler option を、OmniSharp/VS Code に依存しない
immutable snapshot として取得できる。

VS Code extension 0.2.0 には単一 workspace / 単一 project の discovery・selection、
status bar、reload/status command、設定、cache/single-flight、file-event debounce、
workspace trust gate を追加した。query failure は型付きの回復可能な結果として返し、
language server と loose-file diagnostics は継続する。

重要な境界として、snapshot は **まだ analysis engine に適用しない**。
`AppliedToEngine=false` を protocol/UI/test で固定し、project-aware diagnostics は WP-L3、
server 同梱 VSIX は WP-L4 に残した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301`
- MSBuild `18.6.4.27133`
- Node.js `v22.21.1`
- npm `11.7.0`
- VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`

## MSBuild query contract

照会は shell を介さず `ProcessStartInfo.ArgumentList` で次を実行する。

```text
dotnet msbuild <absolute-project.nproj>
  -nologo -verbosity:quiet -target:ResolveReferences
  -getItem:NemerleCompile,ReferencePath,NemerleMacroReference
  -getProperty:MSBuildProjectFullPath,MSBuildProjectDirectory,TargetFramework,Configuration,Platform,DefineConstants,NemerleAdditionalOptions
  -property:Configuration=<configuration>
  -property:Platform=<platform>
  [-property:TargetFramework=<tfm>]
```

`ResolveReferences` 後に item/property を取得するため、ProjectReference output、NuGet assets、
custom macro item を build と同じ MSBuild graph から得られる。成功時 stdout は MSBuild の
`Properties` / `Items` JSON だけを受け入れ、stderr は別 stream で同時に drain する。
`DOTNET_NOLOGO=true` で first-run banner の混入を抑止し、UTF-8 を明示した。

parser は必須 property/item と各 item の `FullPath` を検証し、絶対 path へ正規化・重複除去する。
`ReferencePath` は既存 Windows/Linux `Nemerle.Core.targets` と同じ
`FrameworkReferenceName` が空の item だけを採用する。metadata key が存在しない
ProjectReference/PackageReference output は残り、framework reference pack facade は除外される。
`NemerleMacroReference` は別 collection のままで assembly reference へ混ぜない。

## Option policy

`NemerleAdditionalOptions` は Nemerle Getopt と同じく case-sensitive とし、`-name:value`、
値を次 token に置く形、boolean の bare / `+` / `-` を逐次分類する。空白 split のため、
quoted whitespace を含む値は現段階では安全に再構成できない。

snapshot semantic として初期対応するのは次だけである。

- `-define` / `-d` / `-def`
- `-checked` (`+` / `-` を含む)
- `-indentation-syntax` / `-i`

reference、macro、stdlib/corlib、keyword、root namespace、entry point、target/platform、
response file など semantic-affecting option は生値を保存して warning にする。
warning policy に関わる option も diagnostic warning として保存する。既知の code-generation/output
option は raw token に保存する。未知・malformed option は semantic-affecting とみなし、
黙って破棄しない。

なお現行 `Nemerle.Core.targets` 自体は `DefineConstants` を compiler task へ渡していない。
WP-L2 は評価値を snapshot に記録するが、この既存 build gap の修正はスコープを拡張せず残した。

## 実装

- `ProjectInfo/`: query key/snapshot、strict JSON parser、option classifier、process runner、
  query builder、success cache と same-key single-flight を持つ provider。
- process は全 key で同時一つに制限。timeout/caller cancellation では process tree を kill し、
  `StartFailure` / `NonZeroExit` / `EmptyOutput` / `InvalidJson` / `MissingField` /
  `Timeout` / `Cancelled` を区別する。stdout/stderr detail は各 16 KiB に制限する。
- `LspServer/NemerleProjectInfoHandler.cs`: custom request `nemerle/projectInfo/load`。
  成功/失敗を protocol result にし、常に `AppliedToEngine=false`。失敗時も server を停止しない。
- OmniSharp 0.19.9 は custom method metadata を request DTO から解決する一方、
  request process type は handler descriptor から読む。したがって `[Method]` は request DTO、
  `[Serial]` は handler に付けた。これにより `didOpen` と project query が直列化され、
  `Content Modified` による query 放棄を防いだ。
- extension: `.nproj` を `bin,obj,node_modules,.git,.vs,dist` を除外して探索する。
  0 件は empty、1 件は自動選択、複数は明示 QuickPick。configured/stored path を正規化して扱う。
  `.nproj` create/delete/change は 350 ms debounce、同じ selected project の変更は force reload。
- 設定: `nemerle.dotnet.path`、`nemerle.project`、`nemerle.projectConfiguration`、
  `nemerle.projectPlatform`、`nemerle.projectTargetFramework`。
- command: `Nemerle: Select Project`、`Reload Project`、`Show Project Status`。
- trust 前は server/query とも起動しない。`dotnet.path` は PATH 上の `dotnet` または存在する
  absolute file のみ受け入れ、server と query で同じ解決結果を使う。

## Fixtures と受け入れ結果

- `HelloCore`: source 1、configuration `Debug`、`NET10_0` define を取得。
- `RefDemo/App`: ProjectReference の `MathLib.dll` を assembly reference に取得。
- `samples/PackageReference`: `Newtonsoft.Json` `13.0.4` を restore/build し、resolved
  `Newtonsoft.Json.dll` を取得。framework facade は snapshot に混入しない。
- `Sokoban`: `SokobanMacros.dll` を macro reference にだけ取得し、assembly reference には入らない。
- mixed JSON fixture: path dedupe、facade filter、project/package/macro、option/warning、
  invalid JSON/missing field を deterministic に検証。
- fake executor/loader: start failure、non-zero exit、empty output、invalid JSON、timeout、
  cancellation、cache、same-key single-flight、force reload、全 key の process serialization を検証。
- raw LSP: snapshot 成功、存在しない `.nproj` の `NonZeroExit` 回復可能結果、その後の
  didOpen/error → didChange/clear → didClose/clear → shutdown を一つの実 server で検証。
- Extension Host: trusted workspace で一つの `.nproj` を自動 loadし、Select Project の
  QuickPick を確定、reload/status 表示を行い、
  既存 unsaved diagnostics と restart が継続。untrusted workspace は server process を持たず、
  select/reload command を直接実行しても query を開始しない。
- multiple/zero/one discovery と configured/stored selection は pure unit test で検証。
  interactive QuickPick の見た目は自動クリックせず、command 登録と selection policy を分離して検証した。

## 公式情報の確認

以下を 2026-07-13 に確認した。

- MSBuild build 後 evaluation と JSON output:
  <https://learn.microsoft.com/visualstudio/msbuild/evaluate-items-and-properties>
- MSBuild command-line reference:
  <https://learn.microsoft.com/visualstudio/msbuild/msbuild-command-line-reference>
- `dotnet msbuild`:
  <https://learn.microsoft.com/dotnet/core/tools/dotnet-msbuild>
- .NET CLI environment variables:
  <https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables>
- VS Code API / contribution points / testing / workspace trust:
  <https://code.visualstudio.com/api/references/vscode-api>,
  <https://code.visualstudio.com/api/references/contribution-points>,
  <https://code.visualstudio.com/api/working-with-extensions/testing-extension>,
  <https://code.visualstudio.com/api/extension-guides/workspace-trust>
- LSP 3.17 specification:
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
- OmniSharp 0.19.9 source commit (`ProcessScheduler`, `HandlerTypeDescriptor`):
  <https://github.com/OmniSharp/csharp-language-server-protocol/tree/445179ac1e5774c5e10433cb7066129b7d0cde29/src/JsonRpc>
- Newtonsoft.Json 13.0.4 package:
  <https://www.nuget.org/packages/Newtonsoft.Json/13.0.4>
- NuGet package auditing:
  <https://learn.microsoft.com/nuget/concepts/auditing-packages>

## 再現コマンド

```powershell
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet build -c Release dotnet-port\LspServer\Nemerle.LanguageServer.csproj
dotnet run -c Release --project dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj

Push-Location dotnet-port\vscode-nemerle
npm ci
npm run lint
npm test
npm run test:integration
npm run package
npm audit
npm audit --omit=dev
Pop-Location
```

sample project は `dotnet restore/build`、NuGet/.NET project は vulnerability audit も実行する。

## 最終検証結果

- `ProjectInfo.Test -- --integration`: PASS。unit と HelloCore / RefDemo / PackageReference /
  Sokoban の実 MSBuild query を含む。
- `LspServer` Release build: 0 warnings / 0 errors。
- raw `LspServer.IntegrationTest`: PASS。snapshot-only success、recoverable `NonZeroExit`、
  その後の unsaved diagnostics、shutdown/exit を確認。
- `npm ci`: 456 packages を lockfile から再構築、audit 0 vulnerabilities。
- `npm run check-types` / `npm run lint`: PASS。
- `npm test`: 11/11 PASS。
- `npm run test:integration`: trusted 3/3、untrusted 1/1 PASS。
- `npm run package`: PASS、`vscode-nemerle-0.2.0.vsix` 生成 (server 非同梱)。
- `npm audit` / `npm audit --omit=dev`: どちらも 0 vulnerabilities。
- `dotnet list ... package --vulnerable --include-transitive`: ProjectInfo、ProjectInfo.Test、
  LspServer、LspServer.IntegrationTest、PackageReference の全対象で vulnerable package なし。
- HelloCore、RefDemo/App、PackageReference、Sokoban を `dotnet build`: 全て 0 warnings / 0 errors。

非失敗の注意事項:

- `npm ci` は推移 dev dependency の `whatwg-encoding@3.1.1`、`prebuild-install@7.1.3`、
  `glob@10.5.0` に deprecated warning を出すが、npm audit finding は 0。
- `vsce` は 396 files / JavaScript 186 files のため bundling と `.vscodeignore` 最適化を推奨する。
  正しさや脆弱性の失敗ではなく、server/runtime 同梱と合わせて WP-L4 で扱う。
- VS Code 1.128.0 の test-electron 起動 log に `cached-data` unknown option と in-memory
  application storage の mutex message が出るが、両 Extension Host は exit code 0 で全 test PASS。

## 残課題

1. WP-L3: snapshot → `IIdeProject` adapter、closed source + open buffer override、watch/reload、
   stale diagnostic suppression。ここで初めて diagnostics を project-aware にする。
2. `NemerleAdditionalOptions` の shell-like quoting を必要とする場合は、MSBuild property の
   escaping contract と Nemerle Getopt の response-file semantics を合わせて拡張する。
3. WP-L4: server/runtime/assets を同梱した再現可能な VSIX と clean-machine smoke test。
