# 53. WP-O5 実装ログ — semantic tokens(マクロ拡張キーワードの動的彩色)

実施日: 2026-07-25

ブランチ: `wip/dotnet-port`

## 結論

`47-wp-o-plan.md` の **WP-O5a(semantic tokens)を完了**した。LSP
`textDocument/semanticTokens/full` を実装し、エディターの色分けを IDE engine の
コンパイラー由来の colorizer(`ScanLexer` / `ScanTokenColor`)で行う。

**差別化の可視化(本 WP の目的)**: syntax マクロが増やしたキーワードを、素のキーワードとは
**別の semantic token type(`macro`)** で返す。判定は
`line の GlobalEnv.Keywords \ ManagerClass.CoreEnv.Keywords` = 「そのファイルの `using` が
開いた名前空間のマクロによって初めてキーワードになった語」。`using Nemerle.Surround;` を消すと
同じ語が識別子(`variable`)に戻ることを、raw LSP と実 VS Code の両方で固定した。TextMate 文法は
マクロを読み込んだ後にしか存在しない語を原理的に知り得ないので、これは静的文法では出せない色である。

- **共有ソース(ncc / lib / macros / VsIntegration)は無改造** = Stage リビルド不要、
  assembly version は `1.2.0.635` のまま。`ScanLexer` は WP-K の時点で core ビルドに含まれており
  (`LspFeasibility/Nemerle.Compiler.Utils.nproj`)、engine 側に手を入れる必要はなかった。
- 色分類は engine 非依存の純関数 `ProjectInfo/SemanticTokenMapping.cs` + unit test
  (`HoverMarkup` / `CompletionMapping` / `GotoMapping` と同じ前例)。
- **legend は標準 LSP token type のみ**(`keyword` / `macro` / `comment` / `string` / `number` /
  `operator` / `variable` / `type` / `class` / `interface` / `enum` / `struct` / `property` /
  `method` / `event`)なので、既定テーマがそのまま色を持つ。Nemerle 固有の区別は
  **独自 modifier 2 個(`quotation` / `escape`)** に載せ、拡張の manifest で宣言 + テーマ
  fallback(`semanticTokenScopes`)を与えた。
- **`workspace/semanticTokens/refresh`** を types tree 再構築後に送り、**クライアントが一度も
  トークンを要求しないうちは 1s/3s/8s で再送**する(下記 §設計-3)。これが無いと初回描画が
  文法だけの色で固定される競争が 2 種類とも刺さる(いずれも実際に踏んだ)。
- WP-O5b(試遊用サンプル)は PO 主導のため本 WP では触っていない。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`、Node 22、VS Code 1.128.0(`@vscode/test-electron`)
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9、API は既存の範囲)
- 依存 package の追加は無し(NuGet / npm とも)

## 設計

### 1. 色分類(engine → LSP legend)

engine の `ScanTokenColor`(`VsIntegration/.../CodeModel/ScanTokenColor.n`、48 値)を
`ProjectInfo` 側の `NemerleScanTokenColor` に **名前と値ごと mirror** し、`SemanticTokenMapping.Classify`
が `(type, modifiers)` を返す。`CompletionMapping` が `GlyphType` の整数を mirror しているのと同じ
トレードオフ(ProjectInfo に engine 参照を持ち込まない代わりに、mirror のズレを別途検出する)。

主な写像と判断:

| engine の色 | LSP | 判断理由 |
|---|---|---|
| `Keyword` / `QuotationKeyword` | `keyword`、マクロ由来なら `macro` | 本 WP の主目的 |
| `Preprocessor` | `keyword` | `#if` 等は拡張可能構文ではないのでマクロ扱いにしない |
| `Identifier` | `variable` | VS の Identifier 相当。多くのテーマで既定前景色 = 既存の見た目を壊さない |
| `UserType*` | `class` / `interface` / `enum` / `struct`、delegate は `type` | LSP に delegate が無い(`CompletionMapping` と同じ判断) |
| `Field` / `Property` | `property` | LSP に `field` が無い |
| `*StringEx`(escape / `$` splice) | `string` + `escape` | VS が別色にしていた区別を modifier で保つ |
| `Quotation*` | 基底 type + `quotation` | `<[ ]>` の中を面で識別できる |
| `Comment{TODO,BUG,HACK}` | `comment` | 「注記」に相当する標準 modifier が無く、独自語彙を増やすのは本 WP の「小さく閉じた」範囲を超えるため意図的に畳んだ |
| `Text` / whitespace / `HighlightOne,Two` | **トークンを出さない** | 出さない範囲はクライアントの TextMate 文法が色付ける。hover highlight は LSP 経路では発生しない |

mirror のズレ検出: server 側(engine を参照できる)の `NemerleSemanticTokensHandler` の
コンストラクターで `Enum.GetNames<ScanTokenColor>()` と `NemerleScanTokenColor` を突き合わせ、
不一致を `window/logMessage`(Warning)で 1 回報告する。engine は legacy VS 統合と共有のソースなので、
将来 engine 側に色が増えたときに黙って色落ちしないようにするための門番。

### 2. トークン化(`NemerleProject.GetSemanticTokens`)

`GetGotoInfo` と同じ **同期 engine API** なので `_engineOperations` ロック下で直接実行する
(bridge は使わない)。1 行ずつ `ScanLexer.SetLine(line, text, 0, env, typeBuilder)` →
`GetToken(state)` を回し、`ScanState` で複数行構文(ブロックコメント・逐語/再帰文字列・quotation)を
持ち回す = VS の `IScanner` と同じ駆動。

- `env` / `typeBuilder` は `IIdeEngine.GetActiveEnv(fileIndex, line)`。この API は
  **(env, typeBuilder, 有効開始行, 有効終了行)** を返すので、その行スパンをキャッシュ有効期間に
  使い、宣言ツリーの walk を「行ごと」ではなく「宣言ごと」に減らした(VS は行ごとに呼んでいた)。
- `env == null`(types tree 未構築)のときは直前の env を維持し、無ければ `SetLine` が `CoreEnv` に
  fallback する = キーワード・文字列・コメント・数値は正しく、マクロキーワードと user type の区別だけが
  出ない状態。**トークンを返さない**より良いと判断した(§3 の refresh で後から完全になる)。
- engine の列は 1-origin・終端排他。行末を越える終端(`skip_to_end`)や空トークンは行長でクランプし、
  LSP の 0-origin UTF-16 に変換する。行を跨ぐトークンは原理的に発生しない(colorizer が行単位)。
- 例外は握って `window/logMessage`(Warning)+ トークン無しに落とす(色付けの失敗で要求ループを
  壊さない)。`ScanLexer` は `CoreEnv` を前提に assert するので、`RequestOnInitEngine()` と
  `CoreEnv != null` を先に確認する。

### 3. `workspace/semanticTokens/refresh`(重要)

行のキーワード集合は `GlobalEnv` 由来 = **types tree ができて初めてマクロキーワードが分かる**。
クライアントは自分の都合でしか再要求しないので、放置すると不完全な色が次の編集まで固定される。
**競争は 2 種類あり、両方に手当てが要る**。

**(a) 要求 vs ビルド**: `didOpen` 直後の 1 回目の要求が build と競争し、勝てないと core キーワード
だけの色が返る(raw LSP シナリオが実際に赤くなって発覚。1 回目の実行では偶然通っていた = 潜在フレーク)。
対処: `NemerleProject` に `TypesTreeRebuilt` イベントを追加し(`TypesTreeCreated` コールバックで発火)、
semantic tokens ハンドラーが `workspace/semanticTokens/refresh` を送る。クライアントが
`workspace.semanticTokens.refreshSupport` を出していない場合は送らない。再要求はトークン化しか
起こさない(rebuild を誘発しない)ので、ループにはならない。

**(b) 初回要求 vs 解析の完了(こちらが本命。WSL 実機で 3 往復かけて特定)**:
クライアントは **provider を登録した直後に必ず 1 回だけ**要求してくる。ウィンドウ復元直後の
その 1 回は、engine の初期化(`CoreEnv`)も types tree も出来ていない時点に当たる。そして
**クライアントは受け取った答えをキャッシュし、二度と聞き直さない** — `workspace/semanticTokens/
refresh` を 5 回(初回 2 + 再送 3)送っても**再要求は 1 度も来なかった**(実測、§手動テスト)。
結果、初回描画は文法だけの色になり、編集かテーマ切替まで固定される。

**対処(現行の設計)**: **その 1 回に対して不完全な答えを返さない**。`GetSemanticTokens` は
`EngineSemanticTokens(Tokens, FromTypesTree)` を返し、**`FromTypesTree` が false**
(= `GetActiveEnv` が 1 行も env を返さなかった = types tree 未構築で core 環境の色しか出せない)
なら、ハンドラーが **50 ms 間隔・最大 20 秒で解析の完了を待ってから**答える
(`WaitForTheAnalysisAsync`)。待ちは実測で数百 ms(engine の初回ビルド分)、要求の
`CancellationToken` で中断でき、タイムアウトしたら **core 環境の色だけでも返す**(無色よりまし)。

refresh とその再送(1s/3s/8s、`RetryUntilTheClientHasTokensAsync`)は**副次的な保険として残す**
(実トークンを返せた時点で打ち切り)。VS Code は反応しなかったが、仕様上は正しい手段であり、
他のクライアントや将来の版では効く。送信は毎回 `window/logMessage` に理由付きで残す。

**この過程で 2 つの失敗をした。どちらも観測の欠如が原因**:

1. 打ち切り条件を「要求が来たら」にした → provider 登録直後の空返事で即座に打ち切られ、再送が
   1 回も出なかった。打ち切りは「**実トークンを返せたとき**」に限る。
2. `GetSemanticTokens` が null を返す経路が**無言**だった → Output からは「クライアントが要求して
   いない」ようにしか見えず、原因の特定が 1 往復遅れた。空返事・待機・不完全な答えは**必ずログ**する
   (`nemerle semantic tokens unavailable ...` / `... waiting for the analysis at ...` /
   `... (NN tokens, core environment only)`)。

### 4. delta / range を出さない

colorizer は全文パスなので、delta は「毎回新しい document から差分を計算する」だけで利得が無い。
`Full = { Delta = false }` / `Range = false` を登録し、`full` のみを提供する。

### 5. VS Code 拡張側

TS ソースは無変更(capability 追従)。manifest のみ:

- `semanticTokenModifiers`: `quotation` / `escape` の宣言(独自 modifier は宣言しないと
  テーマ・`editor.semanticTokenColorCustomizations` から扱えない)。
- `semanticTokenScopes`: `string.escape` → `constant.character.escape`、`*.quotation` →
  `meta.embedded`。fallback が無いと、規則を持たないテーマでは区別が見えないため。
- `configurationDefaults` で `[nemerle]` の `editor.semanticHighlighting.enabled: true`
  (既定はテーマ任せ。本機能が主眼なので Nemerle ファイルでは明示的に有効化)。
- TextMate 文法は **fallback として維持**(engine が分類しない範囲を色付ける)。

### 6. 版

**VSIX / server の版を 0.9.0 → 0.10.0 に上げた**。0.9.0 は `release/1.2.635-preview.2` で
**公開済み**であり、配布済み版番号の再利用は規約で禁止(WP-N2 クローズで 0.8.2 → 0.9.0 を
上げたのと同じ理由)。変更箇所は `package.json`(version と `package:vsix`)・`package-lock.json`・
`LspServer/Program.cs` の `ServerInfo` の 4 箇所。provenance 警告は Nemerle assembly version のみを
比較するので影響しない。

手動テストの往復で 0.10.1〜0.10.4 のローカル候補を作ったが、**どれも push もリリースもしておらず
PO のローカル WSL に置いただけ**なので、PO 指示で全コミットを squash し **0.10.0 に振り直した**
(規約が禁じているのは「配布済み」番号の再利用であって、未配布のローカル候補には当たらない)。

## 実装ファイル

新規:

- `dotnet-port/ProjectInfo/SemanticTokenMapping.cs` — `NemerleScanTokenColor`(engine 色の mirror)、
  `NemerleSemanticTokenType` / `NemerleSemanticTokenModifier`、legend 配列、純関数 `Classify`。
- `dotnet-port/LspServer/NemerleSemanticTokensHandler.cs` — `SemanticTokensHandlerBase` 実装、
  legend 登録、refresh 送信、mirror ズレの門番。

変更:

- `dotnet-port/LspServer/NemerleProject.cs` — `EngineSemanticToken`、`GetSemanticTokens` /
  `Tokenize` / `AddSemanticToken` / `IsMacroKeyword` / `SplitLines`、`TypesTreeRebuilt` イベント。
- `dotnet-port/LspServer/Program.cs` — ハンドラー登録、`ServerInfo` 版。
- `dotnet-port/vscode-nemerle/package.json` / `package-lock.json` — manifest 貢献 + 版。
- `dotnet-port/vscode-nemerle/README.md`、ルート `README.md` — 機能の説明。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `SemanticTokenMappingTests`。
- `dotnet-port/LspServer.IntegrationTest/{Program.cs,LspTestClient.cs}` — raw LSP シナリオ、
  semantic tokens の client capability(+ `refreshSupport`)、server→client refresh 要求への応答。
- `dotnet-port/vscode-nemerle/test/unit/manifest.test.ts` — manifest 貢献の固定。
- `dotnet-port/vscode-nemerle/test/suite/extension.test.ts` — 実 VS Code での end-to-end。

## 検証

すべて Windows 11 で実測。共有ソース無改造のため testsuite / stage 比較 / CLR4 スモークは対象外。

| ゲート | 結果 |
|---|---|
| `ProjectInfo.Test`(unit) | PASS(`SemanticTokenMappingTests` 追加) |
| `ProjectInfo.Test -- --integration` | PASS(sample / SDK package 評価に無回帰) |
| raw LSP 統合(`Nemerle.LanguageServer.IntegrationTest`) | **34 シナリオ PASS**(新規 5 本を含む) |
| `npm test`(拡張 unit) | 23/23 PASS(manifest テスト 1 本追加) |
| `npm run test:integration`(実 VS Code + 制限モード) | 5 + 1 PASS(新規「semantic tokens で macro キーワードが色付く」を含む) |
| `npm run check-types` / `npm run lint` | クリーン |
| `git diff --check` | クリーン |
| `npm run verify-server`(bundled closure + notices) | OK(66 アセンブリ) |
| `test-bundled-server.ps1`(**VSIX から取り出した server** で raw LSP) | 全シナリオ PASS(sokoban.n 73 ms / 4455 トークン) |
| `npm run test:vsix`(隔離 install → bundled server 起動) | 1 PASS |

新規 raw LSP シナリオ(`Semantic tokens: macro-introduced keyword, quotation/escape modifiers,
dynamic on using removal`)が固定していること:

1. legend の先頭が `keyword` / `macro`、modifier が `quotation` / `escape`(拡張 manifest と同一語彙)。
2. `using` / `module` / `def` = `keyword`、`21` = `number`、`// ...` = `comment`。
3. **`surroundwith` = `macro`**(`using Nemerle.Surround;` があるとき)。
4. `<[` = `operator` + `quotation`、quotation 内の `1` = `number` + `quotation`。
5. `$"value = $value"` が「素の string 実行」+「`escape` modifier の splice 実行」に分かれる。
6. **`using` を消すと同じ `surroundwith` が `variable` になり、`def` は `keyword` のまま**(= 語彙表を
   焼き込んでいない証拠)。
7. types tree 構築後に server が refresh 要求を送る(シナリオはこれを待って決定性を得ている)。

2 本目(`... a keyword from the project's own macro library (macro-only reference)`)は、
**上の 1 本目が stdlib マクロ(`Nemerle.dll` 同梱の `Nemerle.Surround`)しか通っていない**という
穴を塞ぐ。新 fixture `samples/SyntaxMacro`(下記)を **project として load** し、
**利用者が書いた syntax マクロ由来のキーワード `twice` が `macro` になる**こと、`using` を消すと
`variable` に戻ること、同じファイルの `mutable` / `module` は `keyword` のままであることを固定する。
経路が違うのが要点: この語は engine が **macro-only ProjectReference(`NemerleMacroReference` →
`GetMacroAssemblyReferences` → `LoadPluginsFrom`)** で読み込んだ利用者アセンブリ由来であり、
**差別化の主張(「あなたが書いたマクロのキーワードも色が付く」)そのものの経路**である。

3 本目(`... a macro library on its own (quotation bodies, compiler API types)`)は
**マクロ定義ファイル単体**を開く: `<[ ... ]>` の本体に `quotation` modifier が付き、quotation の
外の行には付かないこと、`macro` 宣言キーワード、そして `Nemerle.Compiler.dll` 由来の型 `PExpr` が
**`class` に分類される**ことを固定する(user type の分類はこれまでどのテストも踏んでいなかった)。

4 本目(`Semantic tokens: the very first request (before any analysis) still answers with tokens`)は
**初回描画の要のケース**を固定する: 文書を開いた直後、解析を一切待たずに要求 → 空返事ではなく
**解析完了まで待って**(実測 332 ms)`surroundwith` = `macro` を含む完全なトークンを返す。

5 本目のシナリオ(`Semantic tokens: a silent client is nudged until it asks once, then left alone`)
は startup レースの手当て(§設計-3(b))を固定する: **黙ったままのクライアントには 5 秒の間に
2 回以上 refresh が届き**(実測 3 回 = 初回 + 1s + 3s)、**一度要求したらそれ以降 6 秒間 refresh が
来ない**。テストクライアント側には、履歴を進めるための `DrainAsync`(読むまで受信済みにならない)と
`CountMessages` を追加した。

実測(参考): 654 行の実ソース `samples/Sokoban/Sokoban/sokoban.n` を loose file として開いた場合、
`semanticTokens/full` の warm round-trip **69 ms / 4455 トークン**(全文トークン化 + `GetActiveEnv`)。

### 新 fixture `samples/SyntaxMacro`(PO 質問への対応)

`syntax (...)` を宣言するマクロは **既存の samples に 1 つも無かった**(`SokobanMacros` は名前で
呼ぶ式マクロのみ)。つまり「利用者のマクロがキーワードを増やす」という**差別化の主張そのものが
自動テストで一度も踏まれていなかった**ため、最小の fixture を追加した:

- `SyntaxMacro/SyntaxMacros/`(`NemerleMacroLibrary=true`): `macro Twice(body) syntax ("twice", body)`
  の 1 個だけ。quasi-quotation 1 行。
- `SyntaxMacro/SyntaxDemo/`: それを **macro-only ProjectReference** で参照し `twice` を使う 1 ファイル。
  `dotnet exec` で `count = 2` を出す(= マクロが実際に展開されている)。

`EnsureFixturesBuilt` に `SyntaxDemo.nproj` を追加(ProjectReference 経由で macro dll も建つ)。
CI は `samples/**/*.nproj` を一括 restore し raw LSP スイートを実行するので、**この 2 本は CI で
自動的に回る**。fixture は WP-O5b(試遊サンプル、PO 主導)と目的が重なるので、PO が showcase を
整備する際は差し替え・拡張して構わない(テストが参照しているのは
`SyntaxDemo/Program.n` の `twice` と `SyntaxMacros/macros.n` の quotation)。

実 VS Code 側では `vscode.provideDocumentSemanticTokensLegend` /
`vscode.provideDocumentSemanticTokens` で legend と `macro` / `keyword` の実際の色分類を確認
(= vscode-languageclient 経由の legend 変換まで通っている)。

## 再現コマンド

```powershell
dotnet build -c Release dotnet-port\LspServer\Nemerle.LanguageServer.csproj
dotnet build -c Release dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll
dotnet build -c Release dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj
dotnet exec dotnet-port\ProjectInfo.Test\bin\Release\net10.0\Nemerle.ProjectInfo.Test.dll --integration
cd dotnet-port\vscode-nemerle
npm run check-types; npm run lint; npm test; npm run test:integration
```

## 手動テスト用 VSIX の生成(2026-07-25)

PO の WSL 実機試用のため、**VSIX のみ**を生成した(コミット後・clean tree で実施)。

- `pack-server.ps1` → `vscode-nemerle/server/` に 71 files / 10.2 MB を staging。
  `bundle-info.json` に pack 時のコミットが入り、同梱 `Nemerle.dll` / `Nemerle.Compiler.dll` は
  **1.2.0.635**。
- `npm run lint` / `npm test`(23) / `npm run verify-server`(66 アセンブリ)→
  `vsce package` で **`vscode-nemerle-0.10.0.vsix`(468 files / 4.47 MB)**。
- 出力先の食い違いに注意: `test-bundled-server.ps1` の既定は **拡張ディレクトリ直下**、
  `test/runVsix.ts`(`npm run test:vsix`)と `package:vsix` は **`dist/release/`** を見る。
  今回は両方に同一バイトを置いた(`dist/release/vscode-nemerle-0.9.0.vsix` と
  preview.1 の nupkg はそのまま残置。封緘セットは `dist/release-preview.2/`)。
- **nupkg セットは再生成していない**(コンパイラー無改造で `Nemerle.dll` は 1.2.0.635 のまま =
  公開済み `1.2.635-preview.2` と assembly version が一致するので provenance 警告は出ない。
  39 §11 の「server-only 修正は VSIX だけ再生成で可」の前例どおり)。
- **`pack-release.ps1` による封緘は未実施**(リリース工程は PO 判断)。WSL への導入は
  `code --install-extension <VSIX>`(WSL 側の拡張ホストに入れる)。

## 手動テストで判明した startup レース(WSL、2026-07-25)

**症状**: `code` 起動直後 / `Developer: Reload Window` 直後の**初回描画だけ**マクロキーワードが
色付かない(TextMate だけの色 = `surroundwith` は既定前景色)。**カラーテーマを切り替えるか、
ファイルを 1 文字編集すると正しい色になる**。

**原因**(§設計-3(b)): ウィンドウ復元でエディターが文書を先に復元し、その時点では server が
起動中で provider が居ないためクライアントの初回フェッチが空振りする。起動ビルド後に送る
refresh も、復元文書がアタッチされる前だと落ちる。VS Code は provider 登録や refresh の
取りこぼしを再試行しないので、次のトリガー(編集・テーマ切替・インスペクター)まで固定される。
Output のログがそのまま証拠になっていた: **起動直後に `nemerle semantic tokens computed` が
1 行も無く**(hover は来ている)、最初の 1 行はずっと後の操作時。

**確定した事実**: VS Code(vscode-languageclient 10.1.0)は `workspace/semanticTokens/refresh` を
受けて provider の `onDidChangeSemanticTokens` を発火するところまでは実装されている
(`node_modules/vscode-languageclient/lib/common/semanticTokens.js:104`、provider は
`getAllProviders()` に静的登録分も入る)。それでも**再取得は起きない**。したがって
**「クライアントが必ず 1 回はしてくる要求」に完全な答えを返すことが唯一確実な経路**である。

**対処**: §設計-3(b)。`FromTypesTree` が false の間は最大 20 秒待ってから答える。
raw LSP シナリオ「the very first request (before any analysis) still answers with tokens」で固定
(文書を開いた直後・解析を待たずに要求 → 実測 332 ms 待って `surroundwith` = `macro` を返す)。
**WSL 実機で確認済み**: `Reload Window` 直後、テーマも編集も触らずに初回描画から色が付く。
その時のログは `waiting for the analysis` → `computed in 302 ms (59 tokens)`。

**切り分けの教訓(遠回りした経緯を残す)**:

- 最初は「テーマ依存」と誤診した。組み込みテーマ(Light+ / Light Modern / Quiet Light /
  Dark+ / Dark Modern / 2026-*)は **すべて `semanticHighlighting: true` を持つ**ことを
  実体 JSON で確認済み(`include` で `*_plus` → `*_vs` を継承)。テーマを切り替えると直るのは
  フラグの差ではなく、**切り替え自体が再取得・再適用を起こす**ため。
- `Developer: Inspect Editor Tokens and Scopes` は **provider の結果とテーマ規則を都度計算して
  表示する**ので、「描画に適用されているか」の判定には使えない(macro と正しい色が出ているのに
  画面は黒、という食い違いが起きる)。**描画側の判定は Output の
  `nemerle semantic tokens computed` の有無で行う**。
- `def` / `using` / `module` の色は **TextMate 文法(`keyword.declaration.nemerle`)由来**なので、
  semantic tokens が効いていなくてもキーワードは色付く。「キーワードは色付くのにマクロだけ黒」は
  **semantic tokens 未適用**の典型像。
- 途中で `macro` → `entity.name.function.macro` の scope 写像を入れかけたが、誤診に基づく対症療法
  だったので**採用しなかった**。**既定テーマでの `macro` の見え方(VS Code 既定は preprocessor 系
  スコープ)を変えるかどうかは、色が出るようになった状態で改めて評価する**。
- 試行錯誤のコミットは PO 指示で squash した(どこにも配布していない = 番号も履歴も再利用可)。
  残す価値があるのは上の知見だけで、途中版の経緯ではない。

## 既知の制約 / 残課題

- **release セットの封緘は未実施**。発行する場合は `pack-tool.ps1 -Pack` → `pack-server.ps1` →
  `npm run package` → `pack-release.ps1` の順で、`release-info.json` の extension 版も
  更新される(現在の `dist/release/release-info.json` は 0.9.0 のままの古い staging)。
- **初回要求は解析完了まで待つので数百 ms 遅れる**(実測 302〜332 ms)。色付けが少し遅れて出る
  代わりに、不完全な色をキャッシュされない。20 秒で打ち切り、その場合は core 環境の色だけ返す。
- **refresh 再送(1s/3s/8s)は保険**。VS Code は反応しないことが実測で分かっているので、実質的に
  効いているのは初回要求を待たせる経路。他クライアント向けに残している。
- **極端に遅い環境では 20 秒の上限に当たり得る**。その場合の初回描画は core 環境の色になり、
  編集すると完全な色になる(ログに `core environment only` が出るので判別できる)。
- **自動化できていない範囲**(意図的): (1) **実際の startup レース**(ウィンドウ復元中に provider が
  後から登録される状況)は raw LSP でも Extension Host でも再現できない。raw LSP は「解析前の初回
  要求」で機構だけを固定し、**実機での確認は手動ゲート**として残る。(2) `npm run test:integration`
  (実 VS Code)と `test:vsix` は **CI 対象外**(xvfb 前提、42 §6 の Linux VM 手順)。CI で回るのは
  raw LSP スイート・拡張 unit・ProjectInfo・smoke。(3) **色そのもの**(テーマがどう塗るか)は
  API から観測できないため、テストできるのは token type / modifier までである。
- **既定テーマでの `macro` の見え方は未評価**。VS Code の既定写像は preprocessor 系スコープなので、
  テーマによっては keyword と同色になり得る。変えるなら拡張 manifest の `semanticTokenScopes` に
  `macro` の写像を足す(server 無変更)。
- **TODO / BUG / HACK コメントの特別色は畳んだ**(`comment` に統合)。復活させるなら独自 modifier
  1 個の追加で足りる。
- **delta / range は非提供**(§設計-4)。大規模ファイルで全文パスが重くなった場合の最初の手は
  range 提供、次に行キャッシュ。
- `ScanLexer.GetStringToken` は `Manager.Options.ThrowOnError` を一時的に立てて戻す(upstream の
  実装)。`_engineOperations` ロック下で走るが、AsyncWorker スレッドの build と厳密には競合し得る
  = VS 統合と同じ既存特性であり、本 WP で変えていない。
- マクロ判定の基準は **`CoreEnv`**(常に使える環境)。したがって `when` / `unless` / `foreach` など
  **Nemerle.Core のマクロ由来キーワードは `keyword` 扱い**になる。`LexerBase.BaseKeywords` を基準に
  すればこれらも `macro` になるが、ほぼ全ての制御構文が macro 色になり実用上ノイズなので採らなかった。
- **WP-O5b(試遊用サンプル)は未着手・PO 主導**(`47-wp-o-plan.md` §5 WP-O5b)。
