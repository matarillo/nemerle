# 53. WP-O5a 実装ログ — semantic tokens(マクロ拡張キーワードの動的彩色)

WP-O5b(試遊用サンプルと README 導線)は `54-wp-o5b-log.md`。

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
- **colorize は engine の AsyncWorker スレッドで実行する**(§設計-2)。engine は単一スレッド契約
  なので、これを守らないと本体型付けが `NullReferenceException` で落ちる。
- **初回要求には完全な答えを返す**(§設計-3)。クライアントは provider 登録直後に 1 回だけ要求し、
  受け取った答えをキャッシュして聞き直さないため、不完全な色を返すと次の編集まで固定される。
- WP-O5b(試遊用サンプル)は PO 主導のため本 WP では触っていない。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`、Node 22、VS Code 1.128.0(`@vscode/test-electron`)
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9、API は既存の範囲)
- 依存 package の追加は無し(NuGet / npm とも)
- 実機確認は WSL(Ubuntu / VS Code Remote)でも実施

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

### 2. トークン化とスレッド境界

**colorize は engine の AsyncWorker スレッドで実行する。** `NemerleProject.GetSemanticTokensAsync`
が `ColorizeRequest`(`AsyncRequest` 派生)を `AsyncWorker.AddWork` で投入し、hover / completion /
definition / references と同じ `EngineRequestBridge` で待つ。トークン化本体(`Tokenize`)は
worker スレッド上で走る。

**この境界が必須である理由**: engine は単一スレッド契約で、`AsyncWorker.CheckCurrentThreadIsTheAsyncWorker()`
が約 10 箇所で assert している。`_engineOperations` ロックは `NemerleProject` 自身の帳簿を直列化する
だけで、**リロードと本体型付けが走る worker スレッドとは直列化しない**。一方 colorizer は
`ScanLexer.GetIdentifierColor` が**全識別子について** `GlobalEnv.LookupType` を呼び、参照アセンブリの
`LibraryReference.ExternalTypeInfo` の遅延生成を強制する。そのコンストラクターは再帰を断つために
**`direct_supertypes` を代入する前に `this` を共有キャッシュへ公開する**
(`ncc/external/ExternalTypeInfo/ExternalTypeInfo.n:62` と `:76`、`// first cache ourself to avoid loops`)。
worker 以外のスレッドから colorize すると、worker が同じ型を引いたときに半完成のインスタンスを掴み、
`SuperClass()` で null の `direct_supertypes` を参照して `NullReferenceException` になる。

この NRE は本体型付けの中で起きるため `IntelliSenseModeMethodBuilder` の catch が拾い、
**メソッド本体の開き波括弧に固定でエラーを表示**し、`_bodyTyped` を null のまま残す。結果として
**そのメソッド内の hover がファイルを編集するまで永久に動かなくなる**。タイミング依存であり、
リロードが参照アセンブリを再リフレクトしている間に窓が最も広くなる。

代替案を採らなかった理由: ロックで守る案は、共有状態がコンパイラーのグローバル型キャッシュなので
`NemerleProject` から囲えない。`LookupType` を呼ばずキーワードだけ着色する案は user type の色を失い、
かつ `GetActiveEnv` の宣言ツリー走査が worker 外に残る。`ExternalTypeInfo` の自己公開を後ろへずらす
案は、そこにある単一スレッド前提の再帰ガードそのものを壊す。

`ColorizeRequest` の種別は **`AsyncRequestType.EmptyRequest`** を使う。専用の種別を足すと
legacy VS 統合と共有する `AsyncRequestType` の改変になるため、engine 自身が同じ用途で使っている
既存の名前を借りる(`Engine-BuildTypeTree.n` が
`AsyncRequest(AsyncRequestType.EmptyRequest, this, null, emptyWork)` を投入している)。
`AsyncRequest.IsForceOutBy` 上の帰結として、`EmptyRequest` は `CloseProject` 以外に押し出されない
= **要求が黙って捨てられることはない**。重複要求の抑制は呼び出し側で行う(§設計-3)。

トークン化の中身:

- 1 行ずつ `ScanLexer.SetLine(line, text, 0, env, typeBuilder)` → `GetToken(state)` を回し、
  `ScanState` で複数行構文(ブロックコメント・逐語/再帰文字列・quotation)を持ち回す
  = VS の `IScanner` と同じ駆動。
- `env` / `typeBuilder` は `IIdeEngine.GetActiveEnv(fileIndex, line)`。この API は
  **(env, typeBuilder, 有効開始行, 有効終了行)** を返すので、その行スパンをキャッシュ有効期間に
  使い、宣言ツリーの walk を「行ごと」ではなく「宣言ごと」に減らした(VS は行ごとに呼んでいた)。
- `env == null`(types tree 未構築)のときは直前の env を維持し、無ければ `SetLine` が `CoreEnv` に
  fallback する = キーワード・文字列・コメント・数値は正しく、マクロキーワードと user type の区別だけが
  出ない状態。**トークンを返さない**より良いと判断した(§3 で後から完全になる)。
- engine の列は 1-origin・終端排他。行末を越える終端(`skip_to_end`)や空トークンは行長でクランプし、
  LSP の 0-origin UTF-16 に変換する。行を跨ぐトークンは原理的に発生しない(colorizer が行単位)。
- 例外は握って `window/logMessage`(Warning)+ トークン無しに落とす(色付けの失敗で worker ループを
  壊さない)。`ScanLexer` は `CoreEnv` を前提に assert するので、`RequestOnInitEngine()` と
  `CoreEnv != null` を先に確認する。成功経路では work item 自身が `MarkAsCompleted()` する
  (`AsyncWorker.ThreadProc` が完了させるのは例外時だけ)。
- 文書の版が進んだ答えは `EngineRequestBridge` が `Stale` として落とし、ハンドラーは null として扱う。

### 3. 初回描画を完全な色にする

行のキーワード集合は `GlobalEnv` 由来 = **types tree ができて初めてマクロキーワードが分かる**。
**クライアントは provider を登録した直後に 1 回だけ要求し、受け取った答えをキャッシュして
二度と聞き直さない**(下記「クライアント挙動の確定事実」)。したがって**その 1 回に対して
不完全な答えを返さないこと**が唯一確実な経路である。

**対処**: `GetSemanticTokensAsync` は `EngineSemanticTokens(Tokens, FromTypesTree)` を返す。
`FromTypesTree` が false(= `GetActiveEnv` が 1 行も env を返さなかった = types tree 未構築で
core 環境の色しか出せない)なら、ハンドラーは **types tree の再構築シグナルを待ってから**
問い直す(`WaitForTheAnalysisAsync`、上限 20 秒)。待ちは実測で数百 ms(engine の初回ビルド分)、
要求の `CancellationToken` で中断でき、タイムアウトしたら **core 環境の色だけでも返す**(無色よりまし)。

**待ち方がポーリングではなくシグナルである理由**: colorize は worker のキューに載る(§設計-2)ので、
一定間隔で問い直すと**全文パスが待っている当のビルドの後ろに積み上がり、ビルド自体を遅くする**。
`NemerleProject.TypesTreeRebuilt` を `TaskCompletionSource` で受け、**要求を出す前に**次のシグナルを
捕まえてから待つ(取りこぼし防止)。再問い合わせの前にも捕まえ直す。

**refresh とその再送**(`workspace/semanticTokens/refresh`、1s/3s/8s、
`RetryUntilTheClientHasTokensAsync`)は**副次的な保険**として残す。VS Code は反応しないが、仕様上は
正しい手段であり他のクライアントでは効く。クライアントが `workspace.semanticTokens.refreshSupport` を
出していない場合は送らない。再要求はトークン化しか起こさない(rebuild を誘発しない)のでループには
ならない。

**打ち切り条件は「実トークンを返せたとき」に限る。** 「要求が来たら」にすると provider 登録直後の
空返事で即座に打ち切られ、再送が 1 回も出ない。

**空返事・待機・不完全な答えは必ずログする。** 無言の null 経路があると、Output からは
「クライアントが要求していない」ようにしか見えず切り分けができない
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

**VSIX / server の版は 0.10.0**(0.9.0 から繰り上げ)。0.9.0 は `release/1.2.635-preview.2` で
**公開済み**であり、配布済み版番号の再利用は規約で禁止(WP-N2 クローズで 0.8.2 → 0.9.0 を
上げたのと同じ理由)。版が入るのは `package.json`(version と `package:vsix`)・
`package-lock.json`・`LspServer/Program.cs` の `ServerInfo` の 4 箇所。provenance 警告は
Nemerle assembly version のみを比較するので影響しない。

## 実装ファイル

新規:

- `dotnet-port/ProjectInfo/SemanticTokenMapping.cs` — `NemerleScanTokenColor`(engine 色の mirror)、
  `NemerleSemanticTokenType` / `NemerleSemanticTokenModifier`、legend 配列、純関数 `Classify`。
- `dotnet-port/LspServer/NemerleSemanticTokensHandler.cs` — `SemanticTokensHandlerBase` 実装、
  legend 登録、types tree シグナル待ち、refresh 送信、mirror ズレの門番。

変更:

- `dotnet-port/LspServer/NemerleProject.cs` — `EngineSemanticToken` / `EngineSemanticTokens`、
  `GetSemanticTokensAsync` / `ColorizeRequest` / `BeginColorize` / `RunColorize` / `Tokenize` /
  `AddSemanticToken` / `IsMacroKeyword` / `SplitLines`、`TypesTreeRebuilt` イベント、
  `TypesTreeCreated` のリビルド所要時間計測。
- `dotnet-port/LspServer/Program.cs` — ハンドラー登録、`ServerInfo` 版。
- `dotnet-port/vscode-nemerle/package.json` / `package-lock.json` — manifest 貢献 + 版。
- `dotnet-port/vscode-nemerle/README.md`、ルート `README.md` — 機能の説明。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `SemanticTokenMappingTests`。
- `dotnet-port/LspServer.IntegrationTest/{Program.cs,LspTestClient.cs}` — raw LSP シナリオ、
  semantic tokens の client capability(+ `refreshSupport`)、server→client refresh 要求への応答、
  観測用 probe(§検証)。
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
| WSL 実機(`Developer: Reload Window` × 5〜6) | 初回描画から彩色、診断・hover とも正常 |

### raw LSP シナリオが固定していること

**1 本目**(`Semantic tokens: macro-introduced keyword, quotation/escape modifiers, dynamic on
using removal`):

1. legend の先頭が `keyword` / `macro`、modifier が `quotation` / `escape`(拡張 manifest と同一語彙)。
2. `using` / `module` / `def` = `keyword`、`21` = `number`、`// ...` = `comment`。
3. **`surroundwith` = `macro`**(`using Nemerle.Surround;` があるとき)。
4. `<[` = `operator` + `quotation`、quotation 内の `1` = `number` + `quotation`。
5. `$"value = $value"` が「素の string 実行」+「`escape` modifier の splice 実行」に分かれる。
6. **`using` を消すと同じ `surroundwith` が `variable` になり、`def` は `keyword` のまま**(= 語彙表を
   焼き込んでいない証拠)。
7. types tree 構築後に server が refresh 要求を送る(シナリオはこれを待って決定性を得ている)。

**2 本目**(`... a keyword from the project's own macro library (macro-only reference)`)は、
1 本目が stdlib マクロ(`Nemerle.dll` 同梱の `Nemerle.Surround`)しか通らない穴を塞ぐ。
fixture `samples/SyntaxMacro` を **project として load** し、**利用者が書いた syntax マクロ由来の
キーワード `twice` が `macro` になる**こと、`using` を消すと `variable` に戻ること、同じファイルの
`mutable` / `module` は `keyword` のままであることを固定する。経路が違うのが要点: この語は engine が
**macro-only ProjectReference(`NemerleMacroReference` → `GetMacroAssemblyReferences` →
`LoadPluginsFrom`)** で読み込んだ利用者アセンブリ由来であり、**差別化の主張(「あなたが書いた
マクロのキーワードも色が付く」)そのものの経路**である。

**3 本目**(`... a macro library on its own (quotation bodies, compiler API types)`)は
**マクロ定義ファイル単体**を開く: `<[ ... ]>` の本体に `quotation` modifier が付き、quotation の
外の行には付かないこと、`macro` 宣言キーワード、そして `Nemerle.Compiler.dll` 由来の型 `PExpr` が
**`class` に分類される**ことを固定する(user type の分類はこれまでどのテストも踏んでいなかった)。

**4 本目**(`... the very first request (before any analysis) still answers with tokens`)は
**初回描画の要のケース**: 文書を開いた直後、解析を一切待たずに要求 → 空返事ではなく
**解析完了まで待って**(実測 332 ms)`surroundwith` = `macro` を含む完全なトークンを返す。

**5 本目**(`... a silent client is nudged until it asks once, then left alone`)は refresh 再送を
固定する: **黙ったままのクライアントには 5 秒の間に 2 回以上 refresh が届き**(実測 3 回 =
初回 + 1s + 3s)、**一度要求したらそれ以降 6 秒間 refresh が来ない**。

### fixture `samples/SyntaxMacro`

`syntax (...)` を宣言するマクロは既存の samples に 1 つも無かった(`SokobanMacros` は名前で
呼ぶ式マクロのみ)。つまり「利用者のマクロがキーワードを増やす」という差別化の主張そのものが
自動テストで踏まれていなかったため、最小の fixture を置いている:

- `SyntaxMacro/SyntaxMacros/`(`NemerleMacroLibrary=true`): `macro Twice(body) syntax ("twice", body)`
  の 1 個だけ。quasi-quotation 1 行。
- `SyntaxMacro/SyntaxDemo/`: それを **macro-only ProjectReference** で参照し `twice` を使う 1 ファイル。
  `dotnet exec` で `count = 2` を出す(= マクロが実際に展開されている)。

`EnsureFixturesBuilt` に `SyntaxDemo.nproj` が入っており、raw LSP スイートがこれを建てるので
CI でも回る(CI の `samples/**/*.nproj` 一括処理は restore であって build ではない —
`54-wp-o5b-log.md` §3)。この fixture は WP-O5b の showcase と目的が重なるため、差し替え・拡張
して構わない(テストが参照しているのは `SyntaxDemo/Program.n` の `twice` と
`SyntaxMacros/macros.n` の quotation)。

### 観測用 probe(`--macro-sample-probe`)

`LspServer.IntegrationTest` に、**assertion を持たず出力するだけ**の probe を置いている
(`--wp-n2-probe` と同じ第 3 層。本スイートには含まれず、明示指定した時だけ動く)。
スレッド境界(§設計-2)に関わる不具合は本スイートの順序では踏めないため、実エディターの
到着順を人工的に作るためのもの。

```powershell
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll `
  --macro-sample-probe [--open-first | --race | --hover-storm | --churn]
```

| オプション | 作る状況 |
|---|---|
| (無指定) | プロジェクト load → `didOpen` → semanticTokens → hover(基準線) |
| `--open-first` | `didOpen` を load より先に出す(loose file 解析 → 後からプロジェクト適用) |
| `--race` | `didOpen` 直後に semanticTokens を出し、MSBuild クエリと types tree build に重ねる。refresh の度に再要求 |
| `--hover-storm` | 全行全列に hover を投げ続けながら load と semanticTokens を並走させる |
| `--churn` | 開いたままプロジェクトを 3 回リロードし、兄弟プロジェクトへ切替→復帰 |

対象は `samples/Latin`(複数キーワード + ブロック引数の syntax マクロ)、`samples/SyntaxTree`
(式ツリーを再帰展開する syntax マクロ)、`samples/SyntaxMacro`(対照群)。各回の診断・トークン数・
hover 結果・`window/logMessage` を印字する。

テストクライアント側の道具: `DrainAsync`(読むまで履歴が進まないので、待つだけの区間で必要)、
`CountMessages`、`Messages`(区間内の受信メッセージ列)、`SendRequestAsync`(応答を待たずに id を
返す = 他のトラフィックと重ねられる)。

### 実測

- 654 行の実ソース `samples/Sokoban/Sokoban/sokoban.n` を loose file として開いた場合、
  `semanticTokens/full` の warm round-trip **69 ms / 4455 トークン**(全文トークン化 + `GetActiveEnv`)。
- 小規模ファイルの warm 再要求は **13〜15 ms**。
- ビルドと並走した場合の最悪値は **285〜290 ms**(worker のキューでビルドの後ろに並ぶため)。
- 初回要求の待ち時間は **302〜332 ms**(engine の初回ビルド分)。

実 VS Code 側では `vscode.provideDocumentSemanticTokensLegend` /
`vscode.provideDocumentSemanticTokens` で legend と `macro` / `keyword` の実際の色分類を確認
(= vscode-languageclient 経由の legend 変換まで通っている)。

## クライアント挙動の確定事実

実測で確定した、設計判断の根拠になっている事実。

- **VS Code は `workspace/semanticTokens/refresh` を受けても再取得しない。** vscode-languageclient
  10.1.0 は refresh を受けて provider の `onDidChangeSemanticTokens` を発火するところまでは実装して
  いる(`node_modules/vscode-languageclient/lib/common/semanticTokens.js:104`、provider は
  `getAllProviders()` に静的登録分も入る)。それでも再要求は来ない — 5 回(初回 2 + 再送 3)送って
  0 回。したがって**必ず 1 回はしてくる要求に完全な答えを返す**しかない(§設計-3)。
- **描画に適用されているかは Output の `nemerle semantic tokens computed` の有無で判定する。**
  `Developer: Inspect Editor Tokens and Scopes` は provider の結果とテーマ規則を都度計算して表示
  するため、「`macro` と正しい色が出ているのに画面は黒」という食い違いが起きる。
- **組み込みテーマは全て `semanticHighlighting: true` を持つ**(Light+ / Light Modern / Quiet Light /
  Dark+ / Dark Modern / 2026-*。実体 JSON で確認、`include` で `*_plus` → `*_vs` を継承)。
  テーマを切り替えると色が直るのはフラグの差ではなく、**切り替え自体が再取得・再適用を起こす**ため。
- **`def` / `using` / `module` は TextMate 文法(`keyword.declaration.nemerle`)でも色が付く。**
  したがって「キーワードは色付くのにマクロだけ黒」は **semantic tokens 未適用**の典型像である。

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

## 手動テスト用 VSIX / server の生成

**VSIX**(clean tree で実施すること。`bundle-info.json` に pack 時のコミットが入る):

- `pack-server.ps1` → `vscode-nemerle/server/` に 71 files / 10.2 MB を staging。同梱の
  `Nemerle.dll` / `Nemerle.Compiler.dll` は **1.2.0.635**。
- `npm run lint` / `npm test`(23) / `npm run verify-server`(66 アセンブリ)→
  `vsce package` で **`vscode-nemerle-0.10.0.vsix`(468 files / 4.47 MB)**。
- 出力先の食い違いに注意: `test-bundled-server.ps1` の既定は **拡張ディレクトリ直下**、
  `test/runVsix.ts`(`npm run test:vsix`)と `package:vsix` は **`dist/release/`** を見る。
  両方に同一バイトを置くこと(`dist/release/vscode-nemerle-0.9.0.vsix` と preview.1 の nupkg は
  残置。封緘セットは `dist/release-preview.2/`)。
- **nupkg セットの再生成は不要**(コンパイラー無改造で `Nemerle.dll` は 1.2.0.635 のまま =
  公開済み `1.2.635-preview.2` と assembly version が一致するので provenance 警告は出ない。
  39 §11 の「server-only 修正は VSIX だけ再生成で可」の前例どおり)。
- WSL への導入は `code --install-extension <VSIX>`(WSL 側の拡張ホストに入れる)。

**server だけ差し替える場合**(VSIX を作らず開発版を試す): `pack-server.ps1` の出力
`vscode-nemerle/server/` を WSL 側の任意のパスへコピーし、`"nemerle.server.path"` に
`.../Nemerle.LanguageServer.dll` を絶対パスで設定する。Output の 1 行目が
`Using the configured development Nemerle language server:` になれば効いている。

## 既知の制約 / 残課題

- **短時間に複数のフルリロードが走る**(未対処)。エディターの watcher(`**/*.n`、
  `**/*.{targets,props}`、参照 DLL)と `didOpen` がそれぞれ `RequestFullReload(immediate: true)`
  を呼ぶため、プロジェクトを開いた直後に engine rebuild が 2〜数本連続する。各リロードは
  `Options.PersistentLibraries = false`(`Engine-BeginReloadProject.n:38`)で参照アセンブリを
  毎回リフレクトし直すので無駄が大きい。**減らすなら発行側(`RequestFullReload` の呼び出し元)で
  重複要求を落とすこと。** engine は「後発の `BuildTypesTree` が実行中のものを force-out する」
  設計(`AsyncRequest.IsForceOutBy`)なので、**実行中のリロードを待って次を流す方式は誤り**
  (リロードがクライアントの解析完了判定より後ろにずれ、補完が空を返す)。
- **リビルド所要時間ログは消費型**(`TypesTreeCreated`)。`_reloadStartedTimestamp` は開始を 1 つしか
  持てないため、**対応する開始を持たない完了は所要時間を出さない**(`nemerle engine rebuild finished`
  のみ)。連続リロードの本数を数えるときはこの形で見ること。
- **release セットの封緘は未実施**。発行する場合は `pack-tool.ps1 -Pack` → `pack-server.ps1` →
  `npm run package` → `pack-release.ps1` の順で、`release-info.json` の extension 版も
  更新される(現在の `dist/release/release-info.json` は 0.9.0 のままの古い staging)。
- **初回要求は解析完了まで待つので数百 ms 遅れる**(実測 302〜332 ms)。色付けが少し遅れて出る
  代わりに、不完全な色をキャッシュされない。20 秒で打ち切り、その場合は core 環境の色だけ返す。
- **極端に遅い環境では 20 秒の上限に当たり得る**。その場合の初回描画は core 環境の色になり、
  編集すると完全な色になる(ログに `core environment only` が出るので判別できる)。
- **colorize が worker のキューに載るぶん、ビルドと並走したときのレイテンシーが上がる**
  (実測 285〜290 ms)。hover / completion は元から同じ経路なので条件は同じ。
- **自動化できていない範囲**(意図的): (1) **実際の startup レース**(ウィンドウ復元中に provider が
  後から登録される状況)は raw LSP でも Extension Host でも再現できない。raw LSP は「解析前の初回
  要求」で機構だけを固定し、**実機での確認は手動ゲート**として残る。(2) **スレッド競合そのもの**は
  タイミング依存で、Windows では probe の 4 パターンいずれでも踏めなかった(WSL では踏めた)。
  probe は状況を作るだけで、検出を保証しない。(3) `npm run test:integration`(実 VS Code)と
  `test:vsix` は **CI 対象外**(xvfb 前提、42 §6 の Linux VM 手順)。CI で回るのは raw LSP スイート・
  拡張 unit・ProjectInfo・smoke。(4) **色そのもの**(テーマがどう塗るか)は API から観測できないため、
  テストできるのは token type / modifier までである。
- **既定テーマでの `macro` の見え方は未評価**。VS Code の既定写像は preprocessor 系スコープなので、
  テーマによっては keyword と同色になり得る。変えるなら拡張 manifest の `semanticTokenScopes` に
  `macro` の写像を足す(server 無変更)。
- **TODO / BUG / HACK コメントの特別色は畳んだ**(`comment` に統合)。復活させるなら独自 modifier
  1 個の追加で足りる。
- **delta / range は非提供**(§設計-4)。大規模ファイルで全文パスが重くなった場合の最初の手は
  range 提供、次に行キャッシュ。
- `ScanLexer.GetStringToken` は `Manager.Options.ThrowOnError` を一時的に立てて戻す(upstream の
  実装)。colorize が worker スレッドで走るようになったため build とは直列化されるが、engine 状態を
  一時的に書き換える性質自体は VS 統合と共通で、本 WP で変えていない。
- マクロ判定の基準は **`CoreEnv`**(常に使える環境)。したがって `when` / `unless` / `foreach` など
  **Nemerle.Core のマクロ由来キーワードは `keyword` 扱い**になる。`LexerBase.BaseKeywords` を基準に
  すればこれらも `macro` になるが、ほぼ全ての制御構文が macro 色になり実用上ノイズなので採らなかった。
- **WP-O5b(試遊用サンプル)は未着手・PO 主導**(`47-wp-o-plan.md` §5 WP-O5b)。
