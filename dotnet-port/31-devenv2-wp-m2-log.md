# 31. WP-M2: hover(+ EngineRequestBridge)実装ログ

実施日: 2026-07-14

ブランチ: `wip/dotnet-port`

開始 commit: `2437d5bfd`(`Implement WP-M1 IDE/build parity and window/logMessage server logging`)

対象: `dotnet-port/29-devenv2-plan.md` の **WP-M2** のみ。completion(WP-M3)、
definition/references(WP-M4)、incremental rebuild(WP-M5)、`Nemerle.Sdk` NuGet 化
(WP-M6)は範囲外。

## 結論

`29-devenv2-plan.md` の WP-M2 を完了した。LSP handler と IDE engine の間に
`EngineRequestBridge`(§6.2)を新設し、それを使う `textDocument/hover` handler を追加した。
擬似 markup → `MarkupContent` 変換は純関数 + unit test として固定した(§6.3)。受け入れ基準
1〜6 を自動・実測で確認した。**compiler / engine(`ncc`・`lib`・`macros`・`VsIntegration`)は
無改造**なので Stage リビルドは不要だった(§既知の制約 の想定どおり)。

- **EngineRequestBridge**: `Begin*`(`BeginGetQuickTipInfo` 等)を `TaskCompletionSource` 相当の
  await 可能な `Task` 化する汎用境界。engine 直列化 lock(`_engineOperations`)の中で request を
  enqueue し、lock を離してから完了をポーリングで検知する。AsyncWorker の force-out
  (`Stop` フラグ)と request 実行時の document version を尊重し、破棄された要求は
  `Cancelled` / stale は `Stale` として報告して結果を捏造しない。LSP `CancellationToken` は
  同じ `Stop` に写像する。WP-M3/M4 がこの境界を再利用する。
- **markup 変換の純関数**: `Nemerle.ProjectInfo.HoverMarkup`。`<lb/>` → 改行、他の擬似 tag
  (`<keyword>`/`<b>`/`<hint>`/`<params>`/`<pname>`/`<ptype>`/`<code>`/`<pre>` …)は除去、
  HtmlMangling(`&amp;`/`&lt;`/`&gt;`)を復元。markdown 出力は変換後テキスト全体を
  Nemerle fenced code block で包み、markdown メタ文字とソース由来文字列を literal にする
  (fence 長は埋め込まれた backtick 連の長さ + 1 で伸ばす)。client が markdown を
  advertise しなければ plaintext に落とす。ProjectInfo assembly に置いたので
  `ProjectInfo.Test` から unit test できる(`NemerleWarningCode` と同じ方式)。
- **HoverHandler**: `NemerleHoverHandler : HoverHandlerBase`。`NemerleProject.GetHoverAsync`
  経由で bridge を呼び、`QuickTipInfo.Text` を `HoverMarkup` で変換、`QuickTipInfo.Location` を
  0-origin UTF-16 の LSP range へ変換。負荷計測(warm hover 応答時間)を `window/logMessage`
  (Log)で出す。
- **extension は capability 追従**(コード無変更)。ServerInfo version を `0.3.0` → `0.4.0` に更新。
  README の機能表を「hover 実装済み」に更新した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301` / 共有フレームワーク `Microsoft.NETCore.App 10.0.9`
- Node.js `v22.x` / npm `11.x` / VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- Nemerle assembly version `1.2.0.601`(`git describe` = `v1.2-601-g2437d5bfd`。engine 無改造)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。

## 設計と実装

### 1. EngineRequestBridge(§6.2、受け入れ 5)

hover の計算経路(`Engine-GetTokenInfo.n`)は次のとおり:
`BeginGetQuickTipInfo` が `QuickTipInfoAsyncRequest` を `AsyncWorker.AddWork` で enqueue し、
AsyncWorker 常駐スレッドが `GetQuickTipInfo` を実行する。types tree 構築中
(`IsBuildTypesTreeInProgress`)なら request を再 enqueue して待ち、project 未構築なら
`BeginBuildTypesTree` を起動してから再 enqueue、準備できたら
`project.GetQuickTipInfo(fileIndex, line, col)` を計算して `req.QuickTipInfo` に格納し
`MarkAsCompleted()` する。この経路は **response queue を使わず** request の完了フラグ
(`IsCompleted`)で結果を返す。

bridge(`LspServer/EngineRequestBridge.cs`)は engine を変異させないので、次の役割だけを持つ:

- `start()`(= `BeginGetQuickTipInfo`)を **同期実行**して request を得る。呼び出し側が
  `_engineOperations` lock の中で `RunAsync` を呼べば、enqueue が document 変更 / reload と
  直列化される(最初の `await` までが同期実行される C# async の性質を利用)。
- lock を離した後、`IsCompleted` を 5 ms 間隔でポーリングして完了を待つ。ポーリングにしたのは、
  `AsyncRequest.MarkAsCompleted` が `ManualResetEvent` を **set 直後に Close** するため
  (`AsyncRequest.n:55-63`)、`RegisterWaitForSingleObject` 等の handle ベース待機が
  set/close の race を踏むから。既存 response pump も 10 ms ポーリングで、方式として同型。
- LSP `CancellationToken` が cancel されたら request の `Stop` を true にして `Cancelled` を返す。
- 完了時に request の `Stop` が true なら、AsyncWorker の force-out
  (`GetNextRequest`/`AddWork` が後勝ち request で `Stop=true`+`MarkAsCompleted`)で
  破棄されたということなので `Cancelled` を返す(結果を読まない)。
- 完了時に document version が start 時から進んでいたら `Stale` を返す(diagnostics の世代管理と
  同じ考え方)。
- 過負荷でハングしないよう 10 s の timeout を持ち、超過時は `TimedOut` を返す
  (`RequestCancelled` / null 相当)。

`Completed` 以外はすべて呼び出し側で null hover にする。実測 warm p50 31 ms に対し timeout は
十分な安全弁で、project reload と競争させても deadlock せず null/結果/cancel のいずれかを返す
(integration test で固定)。

補足: `GetQuickTipInfo`(hover)は AsyncWorker の force-out 対象が `CloseProject` のみ
(`AsyncRequest.IsForceOutBy`)で、`BuildTypesTree`(reload)では force-out されない。reload 中は
engine 側が request を再 enqueue して新しい types tree の完成を待つため、reload と hover が
交錯しても結果か null(version 進行時)を返せる。

### 2. markup → MarkupContent 変換(§6.3、受け入れ 3)

`QuickTipInfo.Text` は VS2010 向けの擬似 markup を含む(`HintHelper.n` / `QuickTipInfo.n`)。
LSP には返さない前提で `HoverMarkup`(ProjectInfo、純関数)で変換する。

- `<lb/>`(`<lb />` 表記揺れ含む、case-insensitive)→ 改行。**tag 除去より先に**行う
  (汎用 tag 除去 `<[^<>]*>` が `<lb/>` も食うため)。
- 残る全 tag(`<keyword>`/`<b>`/`<hint value='…'>`/`<code>`/`<pre>`/`<params>`/`<pname>`/
  `<ptype>` …)を除去し、可視テキストのみ残す。この時点でソース由来の `<`/`>` はまだ
  `&lt;`/`&gt;` にエスケープされているので、解析対象コードの本物の角括弧を誤って食わない。
- HtmlMangling(`&`→`&amp;`, `>`→`&gt;`, `<`→`&lt;`)を逆変換。`&amp;` を**最後に**復元して
  `&amp;lt;` が `&lt;` へ正しく round-trip するようにする。
- **markdown**: 変換後テキスト全体を ```` ```nemerle … ``` ```` で包む。fence 内では markdown
  メタ文字が literal になるので、シグネチャがコードとして表示され、ソース由来文字列が markdown
  として解釈される経路が無い。fence 長は埋め込み backtick 連 + 1(最小 3)で、早期クローズを防ぐ。
- client の `hover.contentFormat` に markdown が無ければ plaintext(fence 無し)。

unit test(`ProjectInfo.Test`、`HoverMarkupTests`)で固定: `<lb/>`→改行、装飾 tag 除去、
`<hint>` の value 属性を落として内側テキストのみ残す、エスケープ復元(`&amp;lt;`→`&lt;` 含む)、
生 tag 非残存、可視内容の保持、null/空/空白入力、markdown の fenced code block 化、メタ文字の
literal 化、backtick 連での fence 伸長。

### 3. HoverHandler と NemerleProject.GetHoverAsync(受け入れ 1・2・4・6)

- `NemerleProject.GetHoverAsync(uri, lspLine, lspCharacter, token)`(新規)は、`_engineOperations`
  lock 下で open document を引き当てて version を記録し、`_bridge.RunAsync(() =>
  BeginGetQuickTipInfo(source, line, col), …)` を **lock 内で開始**、`await` は lock 外で行う。
  engine 座標は 1-origin、LSP character は UTF-16 code unit オフセットで、source の .NET 文字列
  インデックス(UTF-16)と一致する。結果が usable かつ `Text` 非空なら `EngineHover`
  (raw text + engine 座標 range)を返す。それ以外は null。
- `NemerleHoverHandler`(`HoverHandlerBase`)は `CreateRegistrationOptions` で
  `HoverCapability.ContentFormat` に markdown が含まれるかを一度だけ判定し、`Handle` で
  `HoverMarkup.ToMarkdown`/`ToPlainText` を選ぶ。range は 0-origin UTF-16 へ変換(diagnostics と
  同じクランプ規則)。warm hover の compute 時間を `window/logMessage`(Log)で出す。
- OmniSharp 0.19.9 の実 assembly をリフレクションで確認(2026-07-14): `HoverHandlerBase`
  (`Handle(HoverParams, CancellationToken) : Task<Hover>` と
  `CreateRegistrationOptions(HoverCapability, ClientCapabilities)`)、`Hover.Contents`
  (`MarkedStringsOrMarkupContent`、`MarkupContent` ctor 有り)、`MarkupContent{Kind,Value}`、
  `MarkupKind{PlainText,Markdown}`、`HoverCapability.ContentFormat`(`Container<MarkupKind>`、
  `IEnumerable`)、`Position{Line,Character}`(int)。API 欠落なし、0.19.9 継続。

## 実装ファイル

- 新規: `dotnet-port/ProjectInfo/HoverMarkup.cs`(純関数)、
  `dotnet-port/LspServer/EngineRequestBridge.cs`、
  `dotnet-port/LspServer/NemerleHoverHandler.cs`、本ログ。
- 変更(LSP server): `dotnet-port/LspServer/NemerleProject.cs`(`EngineHover`/`EngineHoverRange`
  record、`_bridge`、`GetHoverAsync`)、`dotnet-port/LspServer/Program.cs`
  (`WithHandler<NemerleHoverHandler>`、ServerInfo 0.4.0)。
- 変更(test): `dotnet-port/ProjectInfo.Test/Program.cs`(`HoverMarkupTests`)、
  `dotnet-port/LspServer.IntegrationTest/Program.cs`(hover 6 シナリオ)、
  `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs`(initialize に hover.contentFormat
  markdown を追加)。
- 変更(doc): `dotnet-port/vscode-nemerle/README.md`(hover 実装済みへ更新)。
- `ncc/` `lib/` `macros/` `VsIntegration/`(engine 共有ソース)は**無変更**(hover は既存 engine
  API のみ使用)。extension の TypeScript も無変更。

## 検証(受け入れ基準)

raw stdio LSP integration(`LspServer.IntegrationTest`、既存 7 + hover 6 = 13 シナリオ)を実 server
process で実施、全 PASS。bundled server(VSIX 抽出)でも同 13 シナリオ全 PASS。

### 基準 1: 他 source / ProjectReference 先の型・method に hover

- Sokoban: `main.n` の `TreeSearch.A_Star`(他 project source 宣言)に hover して非 null。
- RefDemo: `Program.n` の `Calc.Square`(ProjectReference 先 MathLib)に hover して非 null。
  いずれも project 適用 → open buffer が error-free になった後に hover。

### 基準 2: 未保存 buffer 変更後の hover が新しい buffer を反映

`def alpha = 123;` → hover が `alpha` を返す。didChange で
`def bravo = "s";`(rename + 型変更、同 length で位置固定)に変更 → version-2 の空診断を待って
同位置を hover すると `bravo` を返し、stale な `alpha` を返さない。**注**: headless engine は
local value hint の型欄が空になることが多い(WP-K で記録した BCL/推論差の特性)ため、確実に
差が出る識別子名で「未保存 buffer が使われた」ことを固定した(型自体は補完個数と同じ既知特性)。

### 基準 3: 5 種の期待 text を固定、擬似 markup 非漏洩

自己完結の loose-file probe で、local value(`localValue`)、parameter(`parameter`)、
method call(`Compute`)、property(`box.Amount`)、type name(`class Box` 宣言名)の 5 種に
hover し、それぞれ期待識別子を含むことと、`<lb/>`/`<keyword`/`<hint`/`<b>`/`<params`/`<pname`/
`<ptype`/`<code`/`<pre` が結果に一切漏れないことを assert。markdown fenced code block
(```` ```nemerle ````)であることも assert。

補足(engine の hover 解決特性、log に記録): 静的 method 呼び出しの型修飾子(`Calc.Square` の
`Calc`)や型注釈(`def x : Box` の `Box`)への hover は engine が null を返す
(`Project.Type.n` の `FindObject` は typed tree ベースで、静的**フィールド**参照
`TExpr.StaticRef` の修飾子だけを HACK で型に解決する)。安定して型 hover が得られるのは
型宣言名・静的フィールド修飾子・`tv is TypeVar` 経路。テストは型宣言名を使った。

### 基準 4: CRLF + non-BMP で range が 0-origin UTF-16

CRLF 改行 + 同一行のコメント内 non-BMP 絵文字(U+1F600、UTF-16 surrogate pair)の後ろの
`value` 使用位置に hover し、返る range.start が送った 0-origin UTF-16 位置(絵文字を 2 code unit、
CRLF を 1 改行として数えた値)と一致することを assert。

### 基準 5: 非識別子は null、reload と競争して deadlock しない

空行への hover は null(`ValueKind.Null`)。連続 didChange(version 2〜6)の直後に毎回 hover を
投げ、object か null の応答を必ず返す(hang しない)ことを固定。bridge の force-out/version 照合と
timeout が効いている。

### 基準 6: warm hover 応答時間の計測

probe を open → warm-up hover 1 回の後、同位置に 15 回 hover して round-trip を計測。
**p50 31 ms / p95 77–86 ms / min 27–30 ms / max 77–86 ms(n=15)**。目標 p50 < 300 ms を満たす。
server 側 compute 時間も `window/logMessage`(Log)で出しており、extension の Output Channel と
integration test の双方から観測できる。

### 回帰ゲート(WP-L2/L3/M1)

- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query 4 fixtures。`HoverMarkupTests`
  追加)。
- raw LSP integration(repo bin server): 13/13 シナリオ PASS(既存 7 + hover 6)。
- extension: `npm ci`(0 vuln)/ `check-types` / `lint` / `npm test`(21/21)/
  `test:integration`(trusted 3 + untrusted 1)/ `package`(`vscode-nemerle-0.4.0.vsix`
  468 files / 4.43 MB)/ `test:vsix`(隔離 install 1 passing)/
  `test-bundled-server.ps1`(VSIX 抽出 server で 13/13 シナリオ PASS)。
- vulnerability: `npm audit` / `npm audit --omit=dev` = 0。
  `dotnet list package --vulnerable --include-transitive` = LspServer / ProjectInfo /
  ProjectInfo.Test / LspServer.IntegrationTest の全てで脆弱 package なし。
- compiler / engine 無改造のため Stage リビルド・testsuite 再実行は不要
  (assembly version は WP-M1 と同一 601)。pack-server.ps1 で bundled server を再 staging した。

## 公式仕様の確認(2026-07-14)

- LSP 3.17 `textDocument/hover`(`Hover.contents` = `MarkupContent | MarkedString`、
  `MarkupContent{kind,value}`、`MarkupKind` = `markdown`/`plaintext`、
  `HoverClientCapabilities.contentFormat`、position は 0-origin・UTF-16 code unit):
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
- OmniSharp 0.19.9 実 assembly 検査(`HoverHandlerBase`/`Hover`/`MarkupContent`/`MarkupKind`/
  `HoverCapability.ContentFormat`/`HoverParams`/`Position`)。API 欠落なし、0.19.9 継続。

## 再現コマンド

```powershell
# server / bridge / handler をビルド
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild

# unit + raw LSP integration(hover 含む)
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

1. hover は静的表示のみ。`<hint>` の対話的展開・`GetHintContent`(遅延 delegate)は VS2010 UI
   機能なので使わない(§6.3 どおり)。
2. headless engine は local value / method の hint で **型欄が空**になることがある(WP-K で
   記録した BCL / 推論差の特性。補完個数のずれと同根)。名前・修飾子・宣言は正しく出る。
3. 型 hover は宣言名・静的フィールド修飾子・`tv is TypeVar` 経路で得られる。静的 method 呼び出しの
   型修飾子や型注釈への hover は engine が null(基準 3 の補足)。completion(WP-M3)は別 API
   (`Completion`)なので影響しない。
4. markdown hover は hint 全体を単一 fenced code block にする(ドキュメント本文も code block 内に
   入る)。より豊かな整形(署名を code、doc を段落に分離)は将来の改善余地。生 tag 非混入と
   メタ文字 literal を優先した。
5. `EngineRequestBridge` は完了検知をポーリング(5 ms)で行う(`MarkAsCompleted` の handle
   set/close race を避けるため)。応答時間への影響は p50 31 ms で許容内。
6. completion(WP-M3)、definition/references(WP-M4)、incremental rebuild(WP-M5)、
   `Nemerle.Sdk`(WP-M6)は未着手。M3/M4 は本 WP の `EngineRequestBridge`(async は M3、
   同期 `GetGotoInfo` は `_engineOperations` lock 直実行)を再利用する。

前身の実装ログ: `27-vscode-project-workspace-log.md`(WP-L3)/ `28-vscode-packaging-log.md`
(WP-L4)/ `30-devenv2-wp-m1-log.md`(WP-M1)。計画: `29-devenv2-plan.md`。
