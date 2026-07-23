# 33. WP-M4: definition / references 実装ログ

実施日: 2026-07-14

ブランチ: `wip/dotnet-port`

開始 commit: `361bc64e8`(`Implement WP-M3 completion reusing EngineRequestBridge`)

対象: `dotnet-port/docs/29-devenv2-plan.md` の **WP-M4** のみ。incremental rebuild(WP-M5)、
`Nemerle.Sdk` NuGet 化(WP-M6)は範囲外。

## 結論

`29-devenv2-plan.md` の WP-M4 を完了した。`textDocument/definition` と
`textDocument/references` handler を追加した。hover/completion(WP-M2/M3)の async bridge とは
異なり、definition/references は engine の **同期 API** `GetGotoInfo` を `_engineOperations` lock
**内で直接実行**する(§6.2「同期 API は lock 下で直列化」)。`GotoInfo`(fileIndex ベースの
1-origin `Location`)→ LSP `Location[]` 変換は純関数 + unit test として固定した(§6.3/§8)。
受け入れ基準 1〜6 を自動・実測で確認した。**compiler / engine(`ncc`・`lib`・`macros`・
`VsIntegration`)は無改造**なので Stage リビルドは不要だった(assembly version は WP-M1〜M3 と
同一 601)。

- **同期 API を lock 下で直接実行**: `IIdeEngine.GetGotoInfo(source, line, col, kind)` は内部で
  `BeginGetGotoInfo`(`AsyncWorker.AddWork`)+ `AsyncWaitHandle.WaitOne()` を行う同期 API。
  bridge の async 経路(WP-M2/M3)を通さず、`NemerleProject.GetGoto` が `_engineOperations` lock を
  取ったまま `GetGotoInfo` を呼び、document change / project reload との直列化規則を変えない。
  lock 保持中に block しても deadlock しないことを確認した: engine 側の再型付け/rebuild は
  AsyncWorker スレッドで進み、その IIdeProject callback は `_gate`(≠ `_engineOperations`)しか
  取らず、GetGotoInfo の完了検知は response pump(`DispatchResponses`)に依存しない。
  reload 進行中なら engine が request を再 enqueue して types tree 完成を待ち、その後計算する。
- **GotoInfo → LSP Location 変換の純関数**: `Nemerle.ProjectInfo.GotoMapping.ToLocations`。engine 型
  `GotoInfo` を editor 中立な `NemerleGotoTarget`(FilePath / FileIndex / 1-origin 座標 /
  IsDefinition)へ NemerleProject 側で写し、純関数が `NemerleGotoLocation`(URI + 0-origin UTF-16
  range)へ変換する。ProjectInfo は engine/OmniSharp 非依存(`HoverMarkup`・`CompletionMapping` と
  同じ)なので unit test で固定できる。非自明な規則:
  - **metadata / 外部 assembly member の除外**: `FileIndex ≤ 0`(source table に無い = BCL/NuGet
    member)や、FilePath 空・end 位置なしの target は navigable location にしない。これにより
    BCL/NuGet 型への definition が空結果になり、誤 URI(`obj/` 配下等)を返さない(受け入れ 4)。
  - **1→0-origin / UTF-16**: engine 座標は 1-origin、LSP は 0-origin。列は engine が .NET string を
    UTF-16 code unit で走査するため、`col-1` がそのまま 0-origin UTF-16 になる(hover と同じ)。
  - **URI 正規化**: WP-L3 の `ProjectPathNormalizer.NormalizeFile`(drive letter 大文字・区切り解決)
    を共用してから `new Uri(...).AbsoluteUri`。diagnostics publisher が同一 file に対して出す URI と
    一致する。OmniSharp `DocumentUri` がワイヤ上で正規形(`file:///f%3A/...`)へ再正規化する。
  - **includeDeclaration**: `references` の `context.includeDeclaration=false` のとき、
    `UsageType.Definition` の宣言エントリを落とす。重複 location は collapse。
- **handler**: `NemerleDefinitionHandler`(`DefinitionHandlerBase`、`textDocument/definition`)は
  `LocationOrLocationLinks` を返す。`NemerleReferencesHandler`(`ReferencesHandlerBase`、
  `textDocument/references`)は `LocationContainer` を返し、`ReferenceParams.Context.IncludeDeclaration`
  を尊重する。外部 member への definition は空結果 + `window/logMessage`(Info)。生成 source 表示
  (VS2010 の `GenerateCode` 相当)は非ゴール。
- **extension は capability 追従**(TypeScript は go-to-definition の Extension Host 検証 test のみ追加)。
  ServerInfo/extension version を `0.5.0` → `0.6.0` に更新。README の機能表を
  「definition/references 実装済み」に更新した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.x` / 共有フレームワーク `Microsoft.NETCore.App 10.0.x`
- Node.js `v22.x` / npm `11.x` / VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- Nemerle assembly version `1.2.0.601`(`dist/ncc`。engine 無改造で WP-M1〜M3 と同一)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。

## 設計と実装

### 1. 同期 GetGotoInfo を lock 下で直接実行(§6.2、受け入れ 全般)

goto の計算経路(`Engine-GetGoToInfo.n`)は次のとおり: `BeginGetGotoInfo` が
`GotoInfoAsyncRequest` を `AsyncWorker.AddWork` で enqueue し、AsyncWorker 常駐スレッドが
`GetGotoInfo(request)` を実行する。types tree 構築中(`IsBuildTypesTreeInProgress`)なら request を
再 enqueue、project 未構築なら `BeginBuildTypesTree` を起動してから再 enqueue、準備できたら
`project.GetDefinition(fileIndex, line, col)` / `project.GetUsages(...)` を計算して
`req.GotoInfos` に格納し `MarkAsCompleted()` する。`IIdeEngine.GetGotoInfo` は
`BeginGetGotoInfo` + `AsyncWaitHandle.WaitOne()` の同期ラッパー。

`NemerleProject.GetGoto` は hover/completion と違い bridge を使わず、`_engineOperations` lock を
取ったまま open document を引き当て(`_gate` 下)、`_engine.GetGotoInfo(source, line, col, kind)` を
同期呼び出しする。§6.2「同期 API(GetGotoInfo)は既存の `_engineOperations` lock の下で実行し、
document change / project reload との直列化規則を変えない」に従う。lock 保持中の block でも
deadlock しない理由:

- engine 側の rebuild/再型付けは AsyncWorker スレッドで進み、その IIdeProject callback
  (`GetSource`/`GetOptions`/`Set*CompilerMessages`/`TypesTreeCreated` 等)は `_gate` または
  `_publishOrdering` を取るだけで `_engineOperations` を要求しない。
- GetGotoInfo の完了検知は request 自身の `AsyncWaitHandle` で、response pump
  (`DispatchResponses`)経路に依存しない。
- lock を保持しているため、block 中に新しい reload が enqueue されて goto が force-out される
  こともない(reload enqueue も `_engineOperations` を要求する)。

### 2. GotoInfo → LSP Location 変換(§6.3、受け入れ 4・6)

`GotoInfo` は engine 型(`Nemerle.Completion2`)。ProjectInfo を engine 非依存に保つため、
NemerleProject が `GotoInfo` から primitive(`FilePath`/`FileIndex`/`Line`/`Column`/`EndLine`/
`EndColumn`/`UsageType == Definition`)を抜き出して `NemerleGotoTarget` を作り、純関数
`GotoMapping.ToLocations(targets, includeDeclaration)` が `NemerleGotoLocation`(URI + 0-origin
UTF-16 range)へ変換する。除外規則・1↔0-origin・URI 正規化・includeDeclaration・重複 collapse は
上記「結論」のとおり。

unit test(`ProjectInfo.Test`、`GotoMappingTests`)で固定: 1→0-origin 変換、metadata
(`FileIndex 0`)除外、end 位置なし除外、includeDeclaration の取捨、重複 collapse、
Windows での URI(drive letter 大文字・file スキーム)。

### 3. handler(受け入れ 1・2・3・5)

- `NemerleProject.GetDefinition(uri, line, char)` は `GotoKind.Definition`、
  `GetReferences(uri, line, char, includeDeclaration)` は `GotoKind.Usages` で `GetGoto` を呼ぶ。
  結果は `EngineGotoResult(Locations, ExternalOnly)`。`ExternalOnly` は engine が target を返したが
  navigable location が 0 件(全て metadata)のとき true。
- `NemerleDefinitionHandler` は `LocationOrLocationLinks` を返し、空かつ `ExternalOnly` のとき
  `window/logMessage`(Info)を出す。`NemerleReferencesHandler` は `LocationContainer` を返し、
  `context.includeDeclaration`(既定 true)を尊重する。両者とも `DocumentUri.From(uri)` で
  location URI を包む。
- OmniSharp 0.19.9 の実 assembly をリフレクションで確認(2026-07-14):
  `DefinitionHandlerBase`(`Handle(DefinitionParams, CancellationToken) : Task<LocationOrLocationLinks>` /
  `CreateRegistrationOptions(DefinitionCapability, ClientCapabilities) : DefinitionRegistrationOptions`)、
  `ReferencesHandlerBase`(`Handle(ReferenceParams, …) : Task<LocationContainer>` /
  `CreateRegistrationOptions(ReferenceCapability, …) : ReferenceRegistrationOptions`)、
  `LocationOrLocationLinks`/`LocationContainer`(`IEnumerable` ctor + 空 ctor)、`Location{Uri,Range}`、
  `LocationOrLocationLink`(`Location` からの implicit 変換)、`ReferenceParams.Context.IncludeDeclaration`。
  API 欠落なし、0.19.9 継続。

## 実装ファイル

- 新規: `dotnet-port/ProjectInfo/GotoMapping.cs`(純関数 + `NemerleGotoTarget`/`NemerleGotoLocation`)、
  `dotnet-port/LspServer/NemerleDefinitionHandler.cs`、
  `dotnet-port/LspServer/NemerleReferencesHandler.cs`、本ログ。
- 変更(LSP server): `dotnet-port/LspServer/NemerleProject.cs`(`EngineGotoResult` record、
  `GetDefinition`/`GetReferences`/`GetGoto`)、`dotnet-port/LspServer/Program.cs`
  (`WithHandler<NemerleDefinitionHandler>`/`<NemerleReferencesHandler>`、ServerInfo 0.6.0)。
- 変更(test): `dotnet-port/ProjectInfo.Test/Program.cs`(`GotoMappingTests`)、
  `dotnet-port/LspServer.IntegrationTest/Program.cs`(definition/references 5 シナリオ)、
  `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs`(initialize に definition/references
  capability を追加)、`dotnet-port/vscode-nemerle/test/suite/extension.test.ts`
  (`vscode.executeDefinitionProvider` の Extension Host 検証を追加)。
- 変更(doc): `dotnet-port/vscode-nemerle/README.md`(definition/references 実装済み)、
  `dotnet-port/vscode-nemerle/package.json`(version 0.6.0、package:vsix 出力名)、
  `dotnet-port/docs/00-PLAN.md`。
- `ncc/` `lib/` `macros/` `VsIntegration/`(engine 共有ソース)は**無変更**。extension の実装 TypeScript
  (`src/`)も無変更(handler は capability 追従、test のみ追加)。

## 検証(受け入れ基準)

raw stdio LSP integration(`LspServer.IntegrationTest`、既存 18 + definition/references 5 = 23
シナリオ)を実 server process で実施、全 PASS。bundled server(VSIX 抽出)でも同 23 シナリオ全 PASS。

### 基準 1: cross-source の宣言位置へ definition

Sokoban を適用し main.n を開いて、`MapCollection`(struct、`sokoban.n` 宣言)と
`TreeSearch.A_Star`(method、`treesearch.n` 宣言)への definition が **別 source** の宣言位置を返す
ことを assert。

### 基準 2: 未保存 buffer 内で宣言位置を移動した直後の definition が新位置(buffer 優先)

loose-file probe で local `target` の使用位置に definition すると宣言行を指す。didChange で行を
先頭に挿入して宣言を 1 行下へずらし(version 2、空診断を待つ)、同じ使用位置に definition すると
**新しい**宣言行を指し、pre-edit の宣言行を返さないことを assert。

### 基準 3: references が使用をまたいで返り、includeDeclaration を尊重

local `counter`(宣言 1 + 使用 3)で `includeDeclaration=true` の references が宣言 location を
含み、`includeDeclaration=false` では宣言を落として使用のみ(件数 = true の -1)を返すことを固定。
返る全 location が同一 document を指すことも assert。engine の `GetUsages` は cursor を含む型
宣言をスコープに usage を集める(VS2010 engine 特性、後述「既知の制約」)。

### 基準 4: BCL / NuGet 型への definition が空結果、誤 URI なし

`System.Console.WriteLine` の `WriteLine`(外部 assembly member)への definition が空結果で、
かつ `window/logMessage`(Info、type 3)で「metadata (external assembly)」を 1 件出す。型修飾子
`Console`(解決先なし)への definition も空結果で、誤 URI を返さないことを assert。

### 基準 5: 返る URI が VS Code から開ける(Extension Host)

trusted Extension Host test を 1 件追加。project source `Editing.n` の buffer を probe に差し替え、
`vscode.executeDefinitionProvider`(実クリック相当)を local 使用位置で呼ぶ。返る Location の
`uri.fsPath` が同 source file を指し(drive letter 大小差は無視)、`range.start.line` が宣言行(4)
であることを assert。VS Code が server の file URI を開ける Uri に parse できることの実証。
(空開始内容は既に診断ゼロのため、diagnostics 待ちでなく definition provider を polling する。)

### 基準 6: CRLF + non-BMP で position が正しい

CRLF 改行 + 宣言 `value` の**前**に non-BMP 絵文字(U+1F600、surrogate pair)を置いた buffer で、
使用位置(絵文字より後)に definition すると、返る宣言 range.start が送った 0-origin UTF-16 位置
(絵文字を 2 code unit として数えた値)と一致することを assert。request position と返却 range の
双方で UTF-16 換算を確認する。

### 回帰ゲート(WP-L2/L3/M1/M2/M3)

- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query。`GotoMappingTests` 追加)。
- raw LSP integration(repo bin server): 23/23 シナリオ PASS(既存 18 + definition/references 5)。
- extension: `npm ci`(0 vuln)/ `check-types` / `lint` / `npm test`(21/21)/
  `test:integration`(trusted 4 + untrusted 1)/ `package`(`vscode-nemerle-0.6.0.vsix`、468 files /
  4.44 MB)/ `test:vsix`(隔離 install 1 passing)/ `test-bundled-server.ps1`(VSIX 抽出 server で
  23/23 PASS)。trusted 4 は go-to-definition の `vscode.executeDefinitionProvider` 検証を追加。
- vulnerability: `npm audit` / `npm audit --omit=dev` = 0。
  `dotnet list package --vulnerable --include-transitive` = LspServer / ProjectInfo /
  ProjectInfo.Test / LspServer.IntegrationTest の全てで脆弱 package なし。
- compiler / engine 無改造のため Stage リビルド・testsuite 再実行は不要
  (assembly version は WP-M1〜M3 と同一 601)。pack-server.ps1 で bundled server を再 staging した。

## 公式仕様の確認(2026-07-14)

- LSP 3.17 `textDocument/definition`(結果 `Location | Location[] | LocationLink[] | null`)/
  `textDocument/references`(`ReferenceContext.includeDeclaration`、結果 `Location[] | null`、
  position は 0-origin・UTF-16 code unit):
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
- OmniSharp 0.19.9 実 assembly 検査(`DefinitionHandlerBase`/`ReferencesHandlerBase`/
  `LocationOrLocationLinks`/`LocationContainer`/`Location`/`ReferenceParams`/`ReferenceContext`/
  `DefinitionRegistrationOptions`/`ReferenceRegistrationOptions`/`DocumentUri`)。API 欠落なし、0.19.9 継続。

## 再現コマンド

```powershell
# server / handler をビルド
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild

# unit + raw LSP integration(definition/references 含む)
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll

# server を再 staging(サーバーコード変更時)
pwsh dotnet-port\vscode-nemerle\pack-server.ps1

Push-Location dotnet-port\vscode-nemerle
npm ci; npm run check-types; npm run lint; npm test
npm run test:integration; npm run package; npm run test:vsix
pwsh .\test-bundled-server.ps1
npm audit; npm audit --omit=dev
Pop-Location
```

前提: `dotnet-port\dist\ncc`(pack-tool.ps1)。raw LSP integration test は fixture の
`dotnet build` を自前で実行する。

## 既知の制約 / 次 WP への境界

1. `GetUsages`(references)のスコープは cursor を含む**型宣言**(`FindUsages(inType, …)`)。
   同一型の method 群(partial 型なら複数 file にまたがりうる)内の usage を集める VS2010 engine の
   特性で、別の型で使われた usage は原理的に集めない。テストは includeDeclaration 意味論を単一 buffer で
   固定し、cross-source の断言は definition(cross-source 宣言解決は確実)側で行う。
2. 静的 method 呼び出しの型修飾子(`Calc.Square` の `Calc`)や外部型への型注釈への definition は
   engine が空を返しうる(WP-M2 で記録した hover 解決特性と同根)。テストは確実に解決する対象
   (local・cross-source 宣言型/method・外部 member = 空結果)で固定した。
3. metadata member への definition は空結果 + `logMessage`(Info)のみで、生成 source 表示
   (`GenerateCode` 相当)は非ゴール(§WP-M4 成果物)。engine が外部 member の `GotoInfo` を
   返さない位置(型パス途中など)では log も出ない(空結果は変わらない)。
4. definition/references は現状 range を宣言/使用の name location にする。将来 full symbol range や
   `LocationLink`(originSelectionRange 付き)への拡張余地はあるが、`Location[]` で全要件を満たす。
5. incremental rebuild(WP-M5)、`Nemerle.Sdk`(WP-M6)は未着手。WP-M5 は sync 方式を
   incremental へ差し替え、WP-M2〜M4 の integration test が回帰ゲートになる。

前身の実装ログ: `30-devenv2-wp-m1-log.md`(WP-M1)/ `31-devenv2-wp-m2-log.md`(WP-M2)/
`32-devenv2-wp-m3-log.md`(WP-M3)。計画: `29-devenv2-plan.md`。
