# 54. WP-O5b 実装ログ — 試遊用サンプルと README 導線

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`

## 結論

`47-wp-o-plan.md` §5 の **WP-O5b(試遊用サンプル)の受け入れ基準を構造として充足**した。
基準は「core でビルド・実行でき、README から辿れる」の 2 点である。

WP-O5b は「PO 自身が色々試しながら随時取り込むため、本計画では成果物を先に固定しない」と
定義されている。したがって本ログは**サンプル集合を確定するもの**ではなく、
**基準を満たす枠組みを作り、以後の追加がその枠組みから外れられないようにした記録**である。

- **README から辿れる**: `dotnet-port/samples/README.md` を新設し、ルート `README.md` から
  1 項目のリンクで辿れるようにした。それまでルート README は `samples/` を 1 か所も
  参照しておらず、**基準は既存サンプルについても未達だった**。
- **core でビルド・実行できる**: `dotnet-port/build-samples.ps1` を CI に組み込み、README が
  載せるサンプルのビルドを保証する。**以後サンプルを足しても基準は自動的に維持される**
  (未分類の `.nproj` があるとゲートが落ちる、§3)。
- syntax マクロのサンプルを 2 本追加(Latin / SyntaxTree)。重複していた
  `samples/CompTimeSolver/SolveMaze` を削除し、`CompTimeSolver` の失敗時診断を 1 本にした。
- 共有ソース(ncc / lib / macros / VsIntegration)は無改造。

## 1. `samples/` の分類

`samples/` は 1 つのカテゴリーではない。19 個の `.nproj` は 3 種類が混在しており、
**この分類を決めない限り「README に載せるか」「CI で建てるか」のどちらにも答えが出ない**。

| 種類 | 対象 | README | ビルド保証 |
|---|---|---|---|
| showcase | HelloCore, SyntaxMacro, Latin, SyntaxTree, CompTimeSolver, Sokoban | 載せる | 要る |
| 正常系フィクスチャー | RefDemo, PackageReference, Defines, Warnings | 載せない | テスト都合のみ |
| 失敗するフィクスチャー | CompTimeSolver/Fail | **載せる** | **失敗することを保証** |

`CompTimeSolver/Fail` を showcase に含めるのが判断の要点である。コンパイル時計算で
「迷路が解けたらビルド成功・解けなければビルド失敗」という対を見せることが、この
サンプルの主張そのものだからである。**失敗する側を単に対象外にすると、ビルドが通るように
なる回帰を検出できず、README の主張が黙って偽になる。**

正常系フィクスチャーを README に載せないのは、showcase としての面白みが無く、
ツールチェーンの挙動(`ProjectReference` / `PackageReference` / `DefineConstants` /
警告 N コード)を固定するためのものだからである。`samples/README.md` の末尾に
1 行ずつの表として存在だけを説明し、ディレクトリーに説明の無いフォルダーが残らないようにした。

## 2. 追加したサンプル

既存の `samples/SyntaxMacro` は「キーワード 1 個の後置マクロ」(`twice PExpr`)しか
カバーしていなかった。syntax マクロの形はもっと広いので、未踏の 2 形を足した。

**Latin** — `si (cond) tum { ... } aliter { ... }` と `revelare (expr)`。

- `syntax (...)` 節は複数のキーワードと複数の被演算子を交互に並べられるので、
  マクロは前置の 1 語ではなく**文の形そのもの**を導入できる。
- `revelare` は引数を**構文木として**受け取ることを示す。コンパイル時に `expr.ToString()` で
  ソース断片 `a + b` を復元して出力に埋め込む — 関数にはできない。

**SyntaxTree** — `explain ((x + 2) * (y - 1))`。引数の式ツリーを再帰的に walk し、
各部分式の評価を出力するコードへ**書き換える**。マクロは何も出力していない。

どちらもマクロライブラリーを macro-only ProjectReference(`NemerleMacroReference` +
`ReferenceOutputAssembly="false"`)で参照する構成で、`samples/SyntaxMacro` と同型である。

## 3. ビルド保証(`build-samples.ps1`)

**`EnsureFixturesBuilt`(`LspServer.IntegrationTest/Program.cs`)には足さない。**
あのリストが答えているのは「raw LSP スイートが MSBuild `-getItem` で評価するために、
参照アセンブリがディスク上に実体として要る」というテスト前提であって、「README で読者に
約束した」ではない。両方を混ぜると、次に触る人には各エントリーがどちらの理由で入って
いるのか判別できなくなる。よって**意味を 1 つに限定した別ゲート**を新設した。

設計上の要点は 3 つ。

**(a) `CompTimeSolver/Fail` は除外せず、失敗方向で検査する。** exit code が非ゼロであることと、
`This maze cannot be solved` が出ることを両方確認する。§1 で述べたとおり、単に対象外にすると
回帰が素通りする。

**(b) 全 `.nproj` の分類を強制する。** `samples/` 配下の `.nproj` は
`$MustBuild` / `$MustFail` / `$Transitive`(依存として建つマクロライブラリー)/ `$Fixtures` の
いずれかに属していなければならず、どれにも無いとゲートが落ちる。逆に、存在しない
プロジェクトがリストに残っていても落ちる。**新しいサンプルは分類するまで CI が通らない**ので、
「README に載せたのにゲートが無い」「ゲートはあるのに README に無い」のどちらも起きない。
これが「以後の追加でも基準が維持される」ことの根拠である。

**(c) 実行結果の照合は入れない。** Sokoban は引数が要る上に実行時間を印字するので非決定的で、
出力を固定するとゲートが脆くなる。ビルドの成否までを保証範囲とする。

CI では「Pack toolchain」「Restore sample projects」の後、拡張 unit テストの前に実行する
(`dotnet-port-ci.yml`)。`dist/ncc` が必要なので pack より前には置けない。

**CI が従来サンプルを build していなかった点の訂正**: CI の `samples/**/*.nproj` 一括処理は
`dotnet restore` であって build ではない(clean checkout に `project.assets.json` が無いと
サーバーの MSBuild 評価が NETSDK1004 で落ちるための前処理)。実際にコンパイルされていたのは
`EnsureFixturesBuilt` の 6 本だけで、**Latin / SyntaxTree / CompTimeSolver / Defines は
一度も建てられていなかった**。`53-wp-o5-log.md` にあった「CI は samples を一括 restore するので
この 2 本も CI で自動的に走る」という記述は、結論(SyntaxMacro は走る)は正しいが理由が
restore になっていて誤解を招くため、あわせて直した。

## 4. `CompTimeSolver` の整理

**重複の削除**: `SolveMaze/` は `Success/` と `Fail/` の内容を 1 ディレクトリーにまとめた
重複だった(`success.n` / `fail.n` / `success.txt` / `fail.txt` の 4 ファイルともバイト一致)。
同じ目的のプロジェクトが 2 か所にあり、しかも片方は成功する `.nproj` と失敗する `.nproj` が
同居していて分かりにくいので削除した。マクロ本体は `Maze/` にあるため `Success/` と `Fail/` は
影響を受けない。参照元はテスト・スクリプト・ワークフローのいずれにも無い
(`36` / `38` の `SolveMaze` はマクロ名への言及であってディレクトリーではない)。

**失敗時診断の修正**: 解けない迷路のとき、`Loop()` が `Message.Error` を呼んだ後もそのまま
続行して空のキューに `Take()` していた。投げた `InvalidOperationException` がマクロの外へ
出るため、本来の診断に 2 本の余計なエラーが付いていた。

```
This maze cannot be solved
InvalidOperationException has occurred when expanding macro 'SolveMaze'
the meaning of `SolveMaze' does not allow this operation
```

キューが空なら `return` し、未解決のまま抜けた場合に `Message.Error` + 型の付いた式を返す
(既存の `"S is not found"` 分岐と同じ形)。これで診断は 1 種類になる。

**残る挙動**: 同じ行が 2 回出る。コンパイラーが失敗した式を型付けし直すためで、サンプル側では
消せない(`Success` は 1 回のみ)。`samples/README.md` にその旨を明記した。

## 5. `samples/README.md` の構成

showcase を「マクロで何ができるか」の難度順に並べた。

| 節 | 見せるもの |
|---|---|
| HelloCore | ツールチェーンが動くことの最小確認 |
| SyntaxMacro | `syntax (...)` がキーワードを増やす。エディター支援が存在する理由でもある |
| Latin | 複数キーワードによる文の追加、引数を構文木として見る |
| SyntaxTree | 引数の構文木を再帰的に書き換える |
| CompTimeSolver | コンパイル時に幅優先探索。解けない迷路はコンパイルエラー |
| Sokoban | 実際のプログラムの中でマクロを使う |

冒頭にマクロライブラリーの作り方(`NemerleMacroLibrary` と `NemerleMacroReference` +
`ReferenceOutputAssembly="false"`)を置き、前提(`pack-tool.ps1 -Pack` で `dist/ncc` を作る)と、
「使うだけなら `packaging/README.md` から始めればよい」という誘導を書いた。

`Sokoban` には既に詳細な README(macro-only ProjectReference の配線の検証記録)があるので、
重複を書かずにリンクした。

言語はルート `README.md` と `packaging/README.md` に合わせて**英語**とした。`docs/` の日本語
ログとは読者が違う(前者は利用者、後者は移植作業の記録)。

ルート `README.md` への追加は「Where things are documented」の先頭 1 項目のみ。

## 6. WP-O5a の実機不具合の修正(本 WP 期間中)

サンプル整備の過程で、エディターで syntax マクロを使うプロジェクトを開くと semantic tokens の
計算直後にメソッド本体の開き波括弧へ `Exception:Object reference not set to an instance of
an object.` が出て、以後そのメソッド内の hover が効かなくなる不具合を PO が WSL で踏んだ。

原因は WP-O5a の colorize が engine を worker スレッド外から叩いていたことで、修正と設計上の
経緯は `53-wp-o5-log.md` §設計-2 に記載した。本 WP の成果物ではないが、**Latin / SyntaxTree の
サンプルがその再現材料になった**(観測用 probe `--macro-sample-probe` の対象)。

## 検証

| ゲート | 結果 |
|---|---|
| `build-samples.ps1`(新規) | PASS。showcase 6 本 build、`CompTimeSolver/Fail` は期待どおり失敗 |
| 分類漏れの検出 | 未分類の `.nproj` を置いて実行 → exit 1 で落ちることを確認、その後削除 |
| README 掲載コマンド | 全て実行して出力を確認(HelloCore / SyntaxMacro / Latin / SyntaxTree / CompTimeSolver の成功・失敗 / Sokoban `zestaw1.xml 1 IDFS`) |
| 相対リンク | `packaging/README.md`、`Sokoban/README.md`、`pack-tool.ps1`、ルートからの `samples/README.md` の実在を確認 |
| CI(GitHub Actions) | 全 job green。新ステップ `Samples advertised in the README build` が success |
| raw LSP 統合 | 34 シナリオ PASS(サンプル追加・削除による回帰なし) |

## 再現コマンド

```powershell
pwsh -NoProfile -File dotnet-port\pack-tool.ps1 -Pack
pwsh -NoProfile -File dotnet-port\build-samples.ps1
```

## 既知の制約 / 残課題

- **`CompTimeSolver/Fail` の診断が 2 回出る**(§4)。コンパイラー側の再型付けによるもので、
  サンプルでは解消できない。直すならコンパイラーの診断重複抑止の話になる。
- **実行結果は保証していない**(§3(c))。README は各サンプルの出力例を載せているので、
  マクロの展開結果が変わって出力だけずれた場合は検出できない。引数不要な 4 本
  (HelloCore / SyntaxMacro / Latin / SyntaxTree)に限って特徴的な 1 行を照合する形へ
  広げることは可能。
- **WP-O5b は開いた項目のまま**。基準を満たす枠組みができただけで、PO が今後サンプルを
  追加するのは想定内である。追加時は `build-samples.ps1` の分類と `samples/README.md` の
  節を足すこと(前者は忘れると CI が落ちるので、後者だけが人間の注意に依存する)。
- **`dotnet-port/PegFeasibility/` は showcase に含めていない**。マクロの説得力という点では
  リポジトリ内で最も強い成果物(PEG パーサージェネレーターがマクロライブラリとして動く)だが、
  (1) `47-wp-o-plan.md` §9 がマクロ生態系の移植を明示的に非ゴールとしている、
  (2) `52-peg-feasibility-log.md` 自身が「計画文書ではなく実験記録」と宣言している、
  (3) 8 プロジェクト規模で「小さく閉じた」を超える、(4) `.n` ソースを `snippets\` から相対参照
  しており自己完結でない、(5) showcase として出すと「Nemerle.Peg はサポート対象」と読まれ、
  52 が主張していない約束になる、という 5 点による。README から辿らせたい場合は
  「サンプル」ではなく「移植実験」という別見出しにし、非サポートを明示するのが筋である。
  非ゴールを取り下げるなら独立した判断として起こすこと。
