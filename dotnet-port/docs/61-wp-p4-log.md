# 61. WP-P4 実装ログ — formatting(評価 → go → 実装)

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`

計画は `56-wp-p-plan.md`。本書は **WP-P4 のみ**で、**WP-P 系列の最後**
(57 = P1 / 58 = P2 / 59 = P3 / 60 = P5 / **61 = P4**。番号は作成順 — 56 §6)。

## 結論

`56-*` §4 の**評価先行方式**に従って engine formatter の実出力を測り、**go と判定**して
LSP `textDocument/formatting`(全文整形)を実装した。

**判定の根拠(実測、§評価)**: 合成 1 本 + `samples/` 配下の実ソース **21 本**に対して整形を適用し、

- **エラーを増やしたファイルは 0 本**(整形後のテキストをサーバーに戻して型付けし直して確認)。
- **13/21 は編集 0 件**(既に一貫した整形がされているファイルには触らない)。
- **7 本は整形され**、差分は「ネストの深さを一貫させる」「行末空白を落とす」で、
  実際に読みやすくなる方向だった。
- **1 本(`samples/Sokoban/Sokoban/sokoban.n`)だけ engine formatter 自身が例外を投げる**
  (`FormatterException: Change (210, 1-5) = "..." conflicts with existing change ...`
  = 同一スパンに 2 通りのインデントを出して自分で検出している)。この場合サーバーは
  **編集を 1 件も返さず、Output に理由を出す**。壊さない失敗。

したがって「壊す可能性は測定範囲で 0、効かないファイルが 1 本ある」= **提供して害が無い**と判断した。

- **共有ソース(ncc / lib / macros / VsIntegration)は無改造** = Stage リビルド不要、
  Nemerle assembly version は `1.2.0.635` のまま。**上記の formatter バグも直していない**
  (`56-*` §3-3。engine 側の修正は Stage リビルドの連鎖に入るため PO 判断)。
- 提供するのは **全文整形のみ**。range formatting / on-type formatting は非提供(§設計-4)。
- 版は **0.10.0 のまま**。拡張の manifest 変更は不要。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9)
- 依存 package の追加は無し

## 評価(go/no-go の材料)

`LspServer.IntegrationTest` に **`--formatting-eval` probe**(assertion を持たず出力するだけの
第 3 層。`--wp-n2-probe` / `--macro-sample-probe` と同じ位置づけ)を置いて測った:

```powershell
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll --formatting-eval
```

対象は合成の misindented ファイル 1 本 + `samples/**/*.n` の全 21 本。各ファイルについて
「編集件数 / 変更行数 / 先頭 8 差分 / 整形後の error 件数(整形前との比較)」を出す。

| 結果 | 本数 | 内訳 |
|---|---|---|
| 編集 0 件(無変更) | 13 | `hello.n` / `defines.n` / `warnings.n` / `PackageSample.n` / `RefDemo` 2 本 / `Latin/Program.n` / `SyntaxMacro` 2 本 / `SyntaxTree/Program.n` / `CompTimeSolver` 2 本 ほか |
| 整形された | 7 | `treesearch.n` 196 編集 / `splayheap.n` 124 / `main.n` 21 / `SokobanMacros/macros.n` 9 / `localsearch.n` 8 / `maze.n` 2 / `LatinSyntax/Library.n` 1 / `SyntaxTreeMacros/Library.n` 1 |
| formatter が例外 | 1 | `sokoban.n`(§既知の制約) |
| **整形後にエラーが増えたファイル** | **0** | 整形した全ファイルで before/after とも error 0 |

`56-*` §4 の判定基準への当てはめ:

1. **構文が壊れない** → ✓。整形した 7 本すべてで診断のエラーが 0 のまま。扱えないファイルでは
   そもそも編集を出さない。
2. **既に整形済みのコードはほぼ無変更** → ✓。13/21 が編集 0 件。変更されたものは元が実際に
   不揃い(例: `treesearch.n` は module が 4 桁、その member が 2 桁でネストが逆転していた)。
3. **quotation / syntax マクロ構文を壊さない** → ✓。`SyntaxMacro/macros.n`(quotation 本体)と
   `SyntaxTree` / `Latin` のマクロ定義は 0〜1 編集、エラー増加なし。

実測レイテンシー: 30〜110 ms(654 行の `sokoban.n` で 62 ms、367 行の `treesearch.n` で 109 ms)。

## 設計

### 1. 経路

`Formatter.FormatDocument(engine, source, IndentInfo)` は同期 API なので、他の engine 読み出しと
同じく **worker 上の `EmptyRequest` work item**(`FormatDocumentRequest`)で実行し、
`EngineRequestBridge` で待つ。engine の `Formatter.BeginFormat` は**領域**整形用
(`Format(loc, ...)`)で全文用ではないため使っていない。戻すのは plain record
(`NemerleFormatterResult`)だけ。

`IndentInfo(insertTabs, indentSize, tabSize)` は LSP の `FormattingOptions`
(`tabSize` / `insertSpaces`)から作る。エディター側の設定がそのまま効く。

### 2. 無変更編集を落とす(必須)

engine の formatter は **既に正しいインデントの行についても結果を返す**。そのまま LSP に流すと
「整形しても何も変わらないのにファイルが dirty になる」= format-on-save と最悪に相性が悪い。
純関数 `FormattingMapping.ToTextEdits` が **「そのスパンに既に同じテキストがある結果」を捨てる**。
実測でも、この filter があることで 13 本が「編集 0 件」になっている(filter が無ければ全行が編集)。

### 3. 重なりは集合ごと拒否

LSP は 1 ドキュメントの `TextEdit[]` を**元のテキスト**に対して適用し、重なりを禁じている。
formatter の結果が重なった場合は `WorkspaceEditMapping.Build`(WP-P3 で作ったもの)が conflict を
返し、ハンドラーは **null を返して Output に警告**する。部分適用はしない。

### 4. range / on-type formatting は出さない

- **range**: engine 側に `Formatter.Format(loc, ...)` があるが、**評価していない**。全文と同じ
  品質かは未知(領域境界のインデント基準の扱いが別)。評価してから足す。
- **on-type**: `}` 入力時の即時整形はタイピング体験に直結し、失敗時の害が大きい。非対象。

### 5. 失敗の扱い

formatter は legacy コードで、扱えない形に当たると例外を投げる(実測: `FormatterException`。
ほかに `TokenNotFoundException` の経路もある)。work item で握って **警告ログ + 編集なし**に落とす。
worker ループは壊さない。ユーザーから見ると「Format Document が何も起こさない」で、
理由は Output の `nemerle formatting failed: ...` に出る。

## 実装ファイル

新規:

- `dotnet-port/ProjectInfo/FormattingMapping.cs` — `NemerleFormatterResult`、
  `ToTextEdits`(座標変換 + 無変更 filter + 重なり検査)、`Apply`(クライアントと同じ適用手順。
  評価とテストが使う)。
- `dotnet-port/LspServer/NemerleFormattingHandler.cs` — `DocumentFormattingHandlerBase` 実装。

変更:

- `dotnet-port/LspServer/NemerleProject.cs` — `GetFormattingResultsAsync` /
  `FormatDocumentRequest` / `BeginFormatDocument` / `RunFormatDocument` / `GetDocumentText`。
- `dotnet-port/LspServer/Program.cs` — ハンドラー登録。
- `dotnet-port/LspServer.IntegrationTest/{LspTestClient.cs,Program.cs}` — `formatting` capability、
  シナリオ 3 本、`--formatting-eval` probe。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `FormattingMappingTests`。

## 検証

| ゲート | 結果 |
|---|---|
| `dotnet build` LspServer / IntegrationTest / ProjectInfo.Test | 成功、警告 0 / エラー 0 |
| raw LSP 統合 | **49 シナリオ PASS**(既存 46 + 新規 3、回帰 0) |
| `ProjectInfo.Test`(unit) | PASS(`FormattingMappingTests` 追加) |
| `ProjectInfo.Test -- --integration` | PASS |
| `--formatting-eval` probe | 22 対象を実測(上表) |
| `git diff --check` | クリーン |
| npm | 未実行 — 拡張側は無変更 |

### raw LSP シナリオが固定していること

**1 本目**(`Formatting: a badly indented document is renested and still compiles`):
わざと崩したファイルが 10 編集で整形され、**`Run` = 2 桁 / `def` = 4 桁 / `WriteLine` = 6 桁**と
段階的にネストされること、そして**整形後のテキストを戻すと error 0**であること。

**2 本目**(`Formatting: already-formatted documents come back with no edits`):
`HelloCore/hello.n` と `SyntaxMacro/macros.n`(quotation を含む)が**編集 0 件**で返ること。
= §設計-2 の filter が効いていること(filter が無ければ全行が編集になる)。

**3 本目**(`Formatting: the engine formatter's conflict on sokoban.n yields no edits, not a broken file`):
既知の失敗が**リクエストエラーでも部分適用でもなく「編集 0 件 + `nemerle formatting failed` ログ」**
になること。**この formatter バグが将来直った場合、このシナリオが気付いて赤くなる**
(そのときは成功経路を固定する形へ書き換えること)。

## 既知の制約 / 残課題

- **`samples/Sokoban/Sokoban/sokoban.n` は整形できない**(engine formatter が自分の生成した
  変更同士の衝突を検出して例外)。原因は共有ソース `CodeIndentationStage2` 側にあり、本 WP では
  直していない(`56-*` §3-3)。他の 20 本では起きていないので、頻度は低いが 0 ではない。
- **range formatting / on-type formatting は非提供**(§設計-4)。range は engine API があるので
  評価すれば足せる。
- **整形は再インデントだけ**。改行位置の整形(`CodeLineBreakingStage`)は upstream が
  `Formatter` 内でコメントアウトしており、本 WP でも有効化していない(評価対象外)。
- **タブ/スペースの混在ファイルは未評価**。`samples/` は全てスペース。
- **実 VS Code での確認は未実施**(系列完了時の手動ゲート)。`editor.formatOnSave` との組み合わせも
  未確認 — 編集 0 件が返るので原理的には無害なはずだが、実機で確かめること。
- **評価は `samples/` 21 本という母集団**であることに注意。コンパイラー本体(`ncc/`)のような
  大規模で古いソースは対象にしていない。

## 後続への申し送り

- **WP-P(バックログ E1 の残り 5 機能)はこれで全て完了**: signatureHelp(57)/
  documentHighlight(58)/ rename(59)/ codeAction(60)/ formatting(61)。
  raw LSP スイートは **34 → 49 シナリオ**になった。
- **ただし「WP 完了」と「課題 E1 の完成」は別**である。本書を含む 57〜61 は各 WP が計画どおり
  実施できたかを記録しており、**E1 として何が残ったかは 1 か所にまとまっていない**。
  その集約は **`56-wp-p-plan.md` §8**(意図的な縮小 2 点、E1 の残りではない別項目、
  リリース判断が未了であること)。E1 の状態を知りたいときは各ログではなくそちらを見ること。
- **系列完了時の手動ゲートは実施済み(2026-07-26)**: `npm run check-types` / `lint` /
  `test`(24)/ `test:integration`(実 VS Code 6 + 1 PASS)、および **PO による 5 機能の実機確認
  (WSL + VS Code)**。実機確認で 3 件の欠陥が出て、いずれも修正済み — 拡張 `wordPattern` の
  `u` フラグ欠落(58 追記1)、code action の毎回 750 ms 待ち + Output のノイズ、
  code action が挿入するコードのインデント 2 件(60 追記)。
- `00-PLAN.md` の WP 表・作業ログと `36-prerelease-quality-plan.md` §2.2 / §10 は
  **PO 合意のうえ 2026-07-26 に反映済み**。E1 の残課題の集約は `56-*` §8。
- engine formatter のバグ(`sokoban.n`)は upstream 相当の共有ソース側の課題としてバックログへ。
