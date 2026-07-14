# 32. WP-M3: completion 実装ログ

実施日: 2026-07-14

ブランチ: `wip/dotnet-port`

開始 commit: `64c0058e0`(`Implement WP-M2 hover with reusable EngineRequestBridge`)

対象: `dotnet-port/29-devenv2-plan.md` の **WP-M3** のみ。definition/references(WP-M4)、
incremental rebuild(WP-M5)、`Nemerle.Sdk` NuGet 化(WP-M6)は範囲外。

## 結論

`29-devenv2-plan.md` の WP-M3 を完了した。`textDocument/completion`(+ `completionItem/resolve`)
handler を追加し、WP-M2 の `EngineRequestBridge` を **async completion** に再利用した。glyph →
`CompletionItemKind` 変換は純関数 + unit test として固定した(§6.3/§8)。受け入れ基準 1〜7 を
自動・実測で確認した。**compiler / engine(`ncc`・`lib`・`macros`・`VsIntegration`)は無改造**なので
Stage リビルドは不要だった(assembly version は WP-M1/M2 と同一 601)。

- **engine 無改造で async completion**: IDE engine の `IIdeEngine` は同期 `Completion`(内部で
  `WaitOne`)しか公開していないが、`CompletionAsyncRequest` は **public** で、その ctor が engine を
  内部で `(engine :> Engine)` にキャストして work delegate を張る。`AsyncWorker.AddWork` も public。
  そこで LSP server 側で `Engine.BeginCompletion` を **待たずに** 再現する:
  `new CompletionAsyncRequest(_engine, source, line, col)` を作って `AsyncWorker.AddWork` で enqueue し、
  bridge が完了をポーリング検知する。engine 共有ソースへの追加(interface へ `BeginCompletion` 追加等)を
  避けられたので、Stage リビルド/版ハザードが発生しない。
- **EngineRequestBridge の再利用**: WP-M2 の bridge(`RunAsync`)をそのまま使う。completion request の
  `IsForceOutBy` は `CloseProject`/`UpdateCompileUnit`/`BuildTypesTree` を force-out 対象にするため、
  reload 中に投げた completion は AsyncWorker が後勝ちで `Stop=true`+`MarkAsCompleted` する。bridge は
  これを `Cancelled` として扱い結果を捏造しない。document version の照合で stale も落とす。
  LSP `CancellationToken` は `Stop` に写像。hover と同じ `_engineOperations` lock 内で enqueue し、
  `await` は lock 外で行う。
- **glyph → kind 変換の純関数**: `Nemerle.ProjectInfo.CompletionMapping.GlyphToKind(int) :
  NemerleCompletionKind`。engine の `GlyphType`(6 の倍数 + 205/206)を editor 中立な列挙へ写す。
  ProjectInfo は engine/OmniSharp 非依存(`HoverMarkup` と同じ)なので、生の int を受けて unit test で
  固定できる。handler が `NemerleCompletionKind` → LSP `CompletionItemKind` を 1:1 で写す。
  detail/documentation の擬似 markup(`LocalValue.MakeHint` の `<lb/>`、macro hint の `<keyword>`)は
  WP-M2 の `HoverMarkup.ToPlainText` を再利用して除去する。
- **resolve の遅延計算 + 1 世代 cache**: `CompletionElem.Description`(overload 列挙 +
  `XmlDocReader` による XmlDoc summary)は高コストなので completion 応答には含めず、
  `completionItem/resolve` で計算する。completion 結果は直近 1 世代だけ cache し、item の `data` に
  `{gen, index}` を載せる。resolve は gen が一致するときだけ Description を計算する。世代は
  completion のたびに +1、かつ **engine reload のたびにも +1(cache を破棄)** するので、古い types tree の
  member を指す resolve は documentation なしで返る。
- **extension は capability 追従**(TypeScript 無変更)。ServerInfo/extension version を `0.4.0` →
  `0.5.0` に更新。README の機能表を「completion 実装済み」に更新した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301` / 共有フレームワーク `Microsoft.NETCore.App 10.0.x`
- Node.js `v22.21.1` / npm `11.7.0` / VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- Nemerle assembly version `1.2.0.601`(`dist/ncc`。engine 無改造で WP-M1/M2 と同一)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。

## 設計と実装

### 1. async completion の配線(§6.2、受け入れ 6)

completion の計算経路(`Engine-Completion.n` / `Engine.Completion-impl.n`)は次のとおり:
`CompletionAsyncRequest` の work delegate `Engine.Completion(request)` が AsyncWorker スレッドで
`CompletionImpl(source, line, col)` を実行し、`request.CompletionElems` に結果を格納して
`MarkAsCompleted()` する。hover(`GetQuickTipInfo`)と違い completion 経路は types tree 構築中の
再 enqueue を持たない(TODO コメントのまま)が、`IsForceOutBy` が `BuildTypesTree` を force-out 対象に
するため、reload 中の completion は後勝ちで破棄され bridge が `Cancelled` を返す。reload 完了後に
投げた completion は正常に計算される。AsyncWorker は単一スレッドなので completion と rebuild は直列。

`IIdeEngine` interface には `BeginCompletion` が無い(concrete `Engine` の internal メソッドの
public ラッパーだが interface 未公開)。interface へ追加すると VsIntegration engine の Stage リビルドが
必要になり版ハザードを招く。代わりに `BeginCompletion` の中身
(`CompletionAsyncRequest` 生成 + `AsyncWorker.AddWork`)を LSP server 側で再現した。
`CompletionAsyncRequest` は public、その ctor が `(engine :> Engine).Completion` を張るので、
`_engine`(= `EngineFactory.Create` の実体 `Engine`)をそのまま渡せる。engine 共有ソースは無変更。

### 2. glyph → kind 変換(§6.3、受け入れ 5)

`CompletionElem.GlyphType` は int で、値は `Nemerle.Completion2.GlyphType`(`GlyphType.n`)の enum 値
(members が 6 の倍数の VS2010 アイコン列 layout、Snippet=205 / Keyword=206)。
`member.GetGlyphIndex()`(`Utils.n`)は subtype を足さず素の GlyphType を返すので、cache された int を
そのまま列挙へ写せる。`CompletionMapping.GlyphToKind`(ProjectInfo、純関数)で
`NemerleCompletionKind`(editor 中立)へ写し、handler で LSP `CompletionItemKind` へ 1:1 変換する。
非自明な写像:

- `Namespace`(90)= `Operator`(90)と衝突 → `Module`(LSP に Namespace kind 無し)。
- `Snippet`(205)→ `Keyword`。engine は keyword completion に Snippet glyph を使う
  (`StrsToCompletionElems(..., GlyphType.Snippet, "keyword")`)。ambiguous/未 load 型の catch-all でも
  あるが、明示的用途は keyword が支配的。
- `Delegate`/`Variant` → `Class`、`VariantOption`/`EnumValue` → `EnumMember`、`Block`/`Local` →
  `Variable`、`Macro` → `Function`、未知 glyph → `Text`。

unit test(`ProjectInfo.Test`、`CompletionMappingTests`)で 20 種の glyph 値と未知/負値の fallback、
および detail/documentation が `HoverMarkup.ToPlainText` で markup 除去されることを固定。

### 3. completion handler と resolve(受け入れ 1〜4)

- `NemerleProject.GetCompletionAsync(uri, lspLine, lspChar, token)`(新規)は hover と同じ流儀で
  `_engineOperations` lock 下に open document と version を引き当て、bridge で
  `CompletionAsyncRequest` を enqueue、`await` は lock 外。usable なら `_gate` 下で世代を +1、
  `CompletionElem[]` を cache し、`EngineCompletionItem`(kind + label + 軽量 detail + `{gen,index}`)の
  list を返す。detail は engine の `Info` 文字列(例 "keyword")を markup 除去したもの、空なら null。
- `NemerleProject.ResolveCompletionDescription(gen, index)`(新規)は `_gate` 下で gen 一致と index 範囲を
  確認して `CompletionElem` を取り出し、lock 外で `Description`(高コスト)を計算・markup 除去して返す。
  gen 不一致(新しい completion or reload)なら null。Description は immutable な member snapshot と
  XmlDoc を読むだけで、並行 rebuild は snapshot を orphan にするのみなので off-thread 読取りが安全、
  世代 guard が置換後の結果を落とす。
- `NemerleCompletionHandler`(`CompletionHandlerBase`)は `CreateRegistrationOptions` で
  `ResolveProvider=true` / `TriggerCharacters=["."]` を返す。`Handle(CompletionParams)` は
  `GetCompletionAsync` の結果を `CompletionItem`(label / kind / detail / `Data={gen,index}`)へ写し
  `isIncomplete=false` の `CompletionList` を返す。`Handle(CompletionItem)`(resolve)は `Data` から
  gen/index を読み、`ResolveCompletionDescription` の結果を `Documentation`(PlainText `MarkupContent`)に
  載せて返す(既に markup 除去済みなのでソース由来文字列を markdown 解釈しない plaintext)。
- OmniSharp 0.19.9 の実 assembly をリフレクションで確認(2026-07-14): `CompletionHandlerBase`
  (`Handle(CompletionParams):Task<CompletionList>` / `Handle(CompletionItem):Task<CompletionItem>` /
  `CreateRegistrationOptions(CompletionCapability, ClientCapabilities)`)、`CompletionList`
  (`Items`/`IsIncomplete`)、`CompletionItem`(`Label`/`Kind`/`Detail`/`Documentation`/`Data:JToken`)、
  `CompletionItemKind`(1..25)、`CompletionRegistrationOptions`(`ResolveProvider`/`TriggerCharacters`)、
  `StringOrMarkupContent`。API 欠落なし、0.19.9 継続。

## 実装ファイル

- 新規: `dotnet-port/ProjectInfo/CompletionMapping.cs`(純関数 + `NemerleCompletionKind`)、
  `dotnet-port/LspServer/NemerleCompletionHandler.cs`、本ログ。
- 変更(LSP server): `dotnet-port/LspServer/NemerleProject.cs`(`EngineCompletionItem`/
  `EngineCompletionResult` record、`_completionGeneration`/`_completionElems` cache、
  `GetCompletionAsync`/`ResolveCompletionDescription`、`BeginEngineReload` で cache 破棄)、
  `dotnet-port/LspServer/Program.cs`(`WithHandler<NemerleCompletionHandler>`、ServerInfo 0.5.0)。
- 変更(test): `dotnet-port/ProjectInfo.Test/Program.cs`(`CompletionMappingTests`)、
  `dotnet-port/LspServer.IntegrationTest/Program.cs`(completion 5 シナリオ)、
  `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs`(initialize に completion capability を追加)。
- 変更(doc): `dotnet-port/vscode-nemerle/README.md`(completion 実装済み)、
  `dotnet-port/vscode-nemerle/package.json`(version 0.5.0、package:vsix 出力名)、
  `dotnet-port/00-PLAN.md`。
- `ncc/` `lib/` `macros/` `VsIntegration/`(engine 共有ソース)は**無変更**。extension の TypeScript も無変更。

## 検証(受け入れ基準)

raw stdio LSP integration(`LspServer.IntegrationTest`、既存 13 + completion 5 = 18 シナリオ)を
実 server process で実施、全 PASS。bundled server(VSIX 抽出)でも同 18 シナリオ全 PASS。

### 基準 1: `.` 直後の member completion(3 project context)

- RefDemo: 開いた buffer の `Calc.Square(7)` で `Calc.` の直後を completion し、`Square`/`Sum`
  (ProjectReference 先 MathLib の member)が返る。
- PackageReference: `JsonConvert.SerializeObject(42)` で `JsonConvert.` の直後を completion し、
  `SerializeObject`(Newtonsoft.Json)が返る。
- Sokoban: `TreeSearch.A_Star(args)` で `TreeSearch.` の直後を completion し、`A_Star`
  (他 project source 宣言型の member)が返る。
いずれも project 適用 → buffer version-1 解析後に completion。

### 基準 2: global scope completion(keyword + 可視型)

loose-file probe の `_ = match;` 位置(式スコープ)を completion すると list が空でなく、Nemerle
keyword `match` を含み、その item の kind が `CompletionItemKind.Keyword`(14)であることを assert。

### 基準 3: 未保存 buffer の新規宣言 symbol

同 probe を didChange で `def zebraLocal = "hello"; _ = zebraLoc;` に変更(version 2)し、version-2 解析後に
`zebraLoc` 使用位置を completion すると `zebraLocal`(同 buffer 宣言)が返る。

### 基準 4: resolve が overload の Description を返し、resolve 前は heavy documentation なし

`Console.` の member completion で `WriteLine`(多 overload)item を取得。resolve **前** は
`documentation` フィールドが無い(null)ことを assert。`completionItem/resolve` すると `documentation`
が付き、複数 overload を列挙して `WriteLine` を 2 回以上含むこと、生 markup が漏れないことを assert。

### 基準 5: glyph → kind mapping と markup 除去

`ProjectInfo.Test` の `CompletionMappingTests`(20 glyph + fallback + markup 除去)で固定。
integration では全 completion label / resolve documentation に擬似 markup が漏れないことを assert。

### 基準 6: 連続 didChange と completion の交錯で crash / position ずれ例外なし

非構文位置(空行)の completion が object/array/null のいずれかを返す(error でない)ことを固定。
連続 didChange(version 2〜6)の直後に毎回同位置を completion し、object/array/null を必ず返す
(古い buffer への position ずれ例外を投げない)ことを固定。bridge の version 照合と force-out が効いている。

### 基準 7: warm completion 応答時間の計測

probe を open → warm-up completion 1 回の後、同位置に 15 回 completion して round-trip を計測。
**p50 31 ms / p95 61 ms / min 27 ms / max 61 ms(n=15)**。目標 p50 < 500 ms を満たす。

### 回帰ゲート(WP-L2/L3/M1/M2)

- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query。`CompletionMappingTests` 追加)。
- raw LSP integration(repo bin server): 18/18 シナリオ PASS(既存 13 + completion 5)。
- extension: `npm ci`(0 vuln)/ `check-types` / `lint` / `npm test`(21/21)/
  `test:integration`(trusted 3 + untrusted 1)/ `package`(`vscode-nemerle-0.5.0.vsix`)/
  `test:vsix`(隔離 install 1 passing)/ `test-bundled-server.ps1`(VSIX 抽出 server で 18/18 PASS)。
- vulnerability: `npm audit` / `npm audit --omit=dev` = 0。
  `dotnet list package --vulnerable --include-transitive` = LspServer / ProjectInfo /
  ProjectInfo.Test / LspServer.IntegrationTest の全てで脆弱 package なし。
- compiler / engine 無改造のため Stage リビルド・testsuite 再実行は不要
  (assembly version は WP-M1/M2 と同一 601)。pack-server.ps1 で bundled server を再 staging した。

## 公式仕様の確認(2026-07-14)

- LSP 3.17 `textDocument/completion` / `completionItem/resolve`(`CompletionItem.data` の resolve 往復、
  `CompletionItemKind`、`triggerCharacters`、`resolveProvider`、`documentation` = `MarkupContent`):
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
- OmniSharp 0.19.9 実 assembly 検査(`CompletionHandlerBase`/`CompletionList`/`CompletionItem`/
  `CompletionItemKind`/`CompletionRegistrationOptions`/`StringOrMarkupContent`)。API 欠落なし、0.19.9 継続。

## 再現コマンド

```powershell
# server / handler をビルド
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild

# unit + raw LSP integration(completion 含む)
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

1. completion は engine の headless 特性を継承する(WP-K/22-log: 補完個数が BCL 差で VS2010 と
   ずれることがある)。テストは特定 member の存在で固定し、個数には依存しない。
2. resolve の documentation は単一 plaintext(署名 + XmlDoc summary を含む)。markdown での
   署名/本文の分離整形は将来の改善余地。生 tag 非混入とメタ文字 literal を優先した。
3. completion item は現状 `TextEdit` を持たず、client 既定の word 置換に任せる
   (`ComlitionLocation` は取得済みだが未使用)。prefix span を厳密に反映する必要が出たら追加する。
4. 1 世代 cache は engine reload で破棄する仕様。reload 直後の resolve は documentation なしで返る
   (再度 completion を投げれば新世代で解決)。
5. definition/references(WP-M4)、incremental rebuild(WP-M5)、`Nemerle.Sdk`(WP-M6)は未着手。
   WP-M4 は本 WP と同じく bridge を使う(同期 `GetGotoInfo` は `_engineOperations` lock 直実行)。

前身の実装ログ: `30-devenv2-wp-m1-log.md`(WP-M1)/ `31-devenv2-wp-m2-log.md`(WP-M2)。
計画: `29-devenv2-plan.md`。
