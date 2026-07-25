# 47. WP-O 計画

**状態: WP-O1 / WP-O2 / WP-O3 / WP-O4 は PO 指示により実施・完了(2026-07-23。ログ:
`48-preservation-wp-o1-log.md` / `49-preservation-wp-o2-log.md` / `50-wp-o3-log.md` /
`51-wp-o4-log.md`)。WP-O4 は nuget.org / Marketplace とも no-go(GitHub Release 維持、単一
`Nemerle.Runtime.Unofficial` 維持)で確定。§8 の論点 1/2/3 はすべて決着(下記)。
**WP-O5 は PO 指示により実施すると決定(2026-07-25)**: 順序は **(a) semantic tokens を先に入れ、
(b) その後に試遊用サンプルを入れる**。(b) は **PO 自身が色々試しながら随時取り込む**ため、
本計画の作業対象は (a) に限る。**(a) は完了(2026-07-25、ログ `53-wp-o5-log.md`)**、(b) は PO 主導で
継続。`00-PLAN.md` の WP 表・作業ログへは
O1/O2/O3/O4/O5 の合意時に反映済み。

WP-O1 には PO 指示によるスコープ追加が 1 点ある: `dotnet-port/` 直下に置かれていた
計画・作業ログ文書(`00-PLAN.md`・番号付き `NN-*.md`)を `dotnet-port/docs/` へ移設する
リファクタリング(§5 WP-O1 成果物に追記)。

作成日: 2026-07-22

---

## 1. 本書の位置づけ

WP-N(公開前の品質固め)は完了し、.NET 10 セルフホストの ncc・SDK スタイルの
ツールチェーン・VS Code 拡張・Linux CI・GitHub Release への発行経路まで揃った(§2)。

WP-O はその次のフェーズである。過去文書は WP-O を「公開フェーズ」(nuget.org・
Marketplace・署名)と仮置きしてきたが、本計画は **公開を目的化せず**、本ポートの
位置づけ(§3)から出発して価値のある作業だけを WP-O に置く。

---

## 2. 現在地(WP-N 完了時点)

到達しているもの:

- **セルフホストする .NET 10 ネイティブ ncc**。stage2/stage3 が決定的にバイト一致、
  testsuite 614/636(コンパイラーバグ 0、残りは環境・BCL 差)。
- **SDK スタイルのツールチェーン**: `.nproj` + in-process `NccCompile` +
  `Nemerle.Sdk.Unofficial` / `Nemerle.Templates.Unofficial` / `Nemerle.Linq.Unofficial`
  (local feed / GitHub Release asset、repo checkout 不要)。
- **VS Code 拡張 0.9.0 + LSP server**: project-aware diagnostics・hover・completion・
  definition/references・incremental rebuild・provenance 版不一致警告。
- **配布・再現の経路**: 版ピン(`version.txt`)+ in-tree seed + `build-from-boot.ps1`、
  GitHub Release(prerelease、`release/1.2.635-preview.1`)への発行を使い捨てタグで
  end-to-end 実証済み。Linux CI(push/PR)+ 手動 release workflow。
- Linux 実地(VM + WSL、xvfb 下の実 VS Code)で build・エディター・テスト一式 PASS。

含意: **動くツールチェーンを世に出すための技術的前提はほぼ揃っている**。残るのは主に
入口(外部から見える文書)の不在と、成果を長く保存・再現できる形に固めることである。

本ポートの差別化要素: **マクロ対応のエディター体験**(マクロで動的に増える構文/
キーワードを理解する diagnostics・hover。Roslyn ベースのツールでは原理的に出せない)。

---

## 3. 位置づけとゴール

### 3.1 本ポートの位置づけ

本ポートの位置づけは主に **保存・再現・文書化** である。完成した .NET 10 移植が、
将来にわたって見つかり・再現でき・理解できる状態で残ることを第一とする。
加えて、興味を持った .NET 開発者が **少し試せる** 程度の間口を用意する。
本格的な開発への採用獲得は本計画の狙いとしない。

上流(`rsdn/nemerle`)への還元は追わない。上流は 2020 年を最後にコミットが止まっており、
還元先として機能しない前提で扱う。

### 3.2 ゴール

1. 外部から本ポートの存在・試し方・現状が分かる **入口** が整う。
2. 発行済みリリースが、将来にわたり **再現・保存** できる形で文書化・検証されている。
3. SDK 消費の実装上のわだかまり(deps.json 依存、§4)が解消され、GitHub Release 経由の
   利用者を含め製品品質が上がる。
4. 配布の器(GitHub Release のみか、nuget.org / Marketplace を足すか)が、位置づけに
   照らして判断・確定される。

### 3.3 スコープの考え方

- **配布は目的でなく手段**。GitHub Release による配布・再現は既に成立している(§2)。
  nuget.org / Marketplace は「試せる間口」を広げる手段だが、publisher identity や
  パッケージ ID/版の恒久性という一方通行のコミットを伴う。位置づけ(保存中心・
  本格採用は非狙い)に照らし、これらは **必要性を判断してから** 開ける(§5 WP-O4)。
- **マクロ生態系(Peg / Statechart 等)の移植は本計画のスコープ外**。これは本格採用を
  狙う場合の要件であり、本計画はそれを狙わない。試遊の魅力づけには小さな showcase で足りる
  (任意、§5 WP-O5)。

---

## 4. 未消化の技術的わだかまり(配布の器に依存しない)

- **D1 / F2**: SDK 消費が `GenerateDependencyFile=false` に依存している(net10 runtime
  package 未公開のため deps.json を正しく出せない)。これは GitHub Release 経由の利用者にも
  影響する製品品質の課題で、公開先を選ぶ以前に解ける。実装(パッケージ生成 + local 消費)と
  公開(nuget.org へ出す)は分離できる。→ WP-O3。
- 既存の net4x NuGet パッケージ(累計 3,219 DL)が存在する。公開に踏み込む場合、命名・版・
  互換の方針はこの既存資産との関係で決める(→ WP-O4)。踏み込まない場合も、README で
  現行 `*.Unofficial` パッケージの位置づけを明記する(→ WP-O1)。

---

## 5. Work packages

依存は「判断」ではなく「実装」で切る([[feedback-plan-dependency-ordering]])。
一方通行のコミット(WP-O4 の公開)を伴う工程には、その前提となる具体値(命名・版方針)を
先行 WP の成果物として置く。

### WP-O1: 入口の現代化(README・入口文書)— 完了(2026-07-23、ログ 48)

現状、ルート `README.md` は upstream のまま(nemerle.org の msi、VS2008/2010、Mono/xbuild)で、
.NET 10 ポートに触れていない。外部の第一印象を作る入口を整える。

成果物(案):

- (PO 指示によるスコープ追加)`dotnet-port/` 直下の計画・作業ログ文書を
  `dotnet-port/docs/` へ移設し、リポジトリ内の参照を追随させる。

- ルート `README.md` に **.NET 10 ポートの節**を追加(既存 upstream 記述は残す)。
  「少し試すには」= `dotnet new` → build → run のクイックスタート、VS Code 拡張、対応 OS、
  現状(preview・`*.Unofficial` 命名の理由・個人によるポートであること)を率直に記す。
  GitHub Release への導線。
- `dotnet-port/`(00-PLAN.md が全体像、DISTRIBUTION.md / packaging/README.md が配布・再現)への案内。
- 既知の制約の要約(CLR4 との関係、Mono 非対象、`.nproj` 必須 等)を利用者目線で 1 節。

受け入れ基準(案):

1. リポジトリのトップページだけを見た .NET 開発者が「これは何か・どう試すか・今どの段階か」を
   理解できる。
2. クイックスタート手順が実機(Windows / Linux)でそのまま通る(コピペ検証)。
3. 版番号・パッケージ名は版リテラル禁止規約(37 §10)に沿う(プレースホルダー or 汎用表記)。

可逆性: 高。リスク: 低。位置づけに関わらず先行して価値が出る。

### WP-O2: 保存・再現性の確定(アーカイブ story)— 完了(2026-07-23、ログ 49)

発行済みリリースを将来にわたり再現・保存できる形に固め、その手順を検証・文書化する。
機構(版ピン・in-tree seed・provenance・`build-from-boot.ps1`)は既にある(§2)ので、
本 WP は「数年後に取り出して再現する」導線が **自己完結** していることの確認と明文化。

成果物(案):

- 発行済み release set + seed + 再現手順(`build-from-boot.ps1` / `verify-seed.ps1` /
  `smoke-release.ps1`)が、リポジトリと GitHub Release だけで完結することの確認。
- 使い捨て clone で「seed 番地 + リリースタグ → release set 再ビルド → 初回発行物と版/バイト一致」を
  再実証し、手順を `DISTRIBUTION.md` / `packaging/README.md` に保存目線で整理。
- provenance(`ncc-info.json` / `release-info.json` / `bundle-info.json`)が指す commit と
  タグの整合を確認。

受け入れ基準(案):

1. 別環境(Linux・CLR4 不要)で、公開済み asset と seed だけからリリースが再現でき、
   初回発行物と一致する。
2. 再現・保存手順が文書化され、外部の人が追える。

可逆性: 高(文書・検証中心)。リスク: 低。

### WP-O3: net10 runtime package の根治(公開先に依存しない品質改善)— 完了(2026-07-23、ログ 50)

`GenerateDependencyFile=false` 依存(D1)を解消し、GitHub Release 経由の利用者を含む
全消費者の製品品質を上げた。公開(nuget.org)とは独立に、local feed で価値を確認できる。

確定した機構(`50-wp-o3-log.md`): deps.json への供給はパッケージ/プロジェクト由来の NuGet
ライブラリ項目でしか成立しないため、runtime を実パッケージ `Nemerle.Runtime.Unofficial`
(`lib/net10.0/{Nemerle,Nemerle.Macros,Nemerle.Compiler}.dll`)化し、`Sdk.props` が
`ExcludeAssets="compile"` の暗黙 PackageReference で参照する。compile 参照(`-ref:`)には入れず
runtime + deps.json にのみ載せることで、ncc の自前解決との二重参照とマクロ支援型の compile scope
混入を避ける。`GenerateDependencyFile=false` 既定を撤去し SDK 既定(true)へ。checkout 形式は
`NemerleRuntimeProvidedByPackage` で従来の copy-local 経路を維持。版は pack 時 staging で SDK 世代に
ピン。既存 net4x パッケージとの版/命名関係は WP-O4 の方針に従う(公開しない場合も local 実装は成立)。

受け入れ基準の結果:

1. ✓ SDK 消費プロジェクトが `GenerateDependencyFile` 既定(true)で build・run 成立
   (smoke-release: install → new → build → run、deps.json がランタイム閉包を列挙)。
2. ✓ nuget.org へ出さず local feed(`<clear />`)のみで確認(smoke-release / test:sdk)。
3. ✓ 回帰: samples(HelloCore/PackageReference/Warnings/Defines/Sokoban/RefDemo)green、
   test:sdk 1/1 PASS。共有ソース無変更 = Stage リビルド不要。

可逆性: 中(パッケージは local に留められる)。リスク: 中(deps.json / 依存解決の未知)。

### WP-O4: 配布の器の判断(go/no-go)— 完了(2026-07-23、ログ 51)

GitHub Release による配布・再現は既に成立している(§2)。本 WP は、位置づけ(保存中心・
少し試せる間口・本格採用は非狙い)に照らして、nuget.org / VS Code Marketplace を足すかを判断した。

**確定した判断(`51-wp-o4-log.md`)**: nuget.org = **no-go**、VS Code Marketplace = **no-go**。
両器とも GitHub Release 配布を維持する。公開時の恒久命名は確定不要(公開しないため)で、runtime
package は単一 `Nemerle.Runtime.Unofficial`(WP-O3)を維持。理由: nuget.org は package ID/版、
Marketplace は publisher identity という一方通行のコミットを伴い、位置づけに不釣り合い。Marketplace
の便益(VSIX の手動 install 摩擦の解消)は、SDK がローカルフィード前提である以上部分的にしかならない
(nuget.org no-go のため)。**本判断は恒久ではなく**、状況が変われば別途判断する(PO 指示により
バックログには積まない)。公開ワークフロー・packaging・msbuild・共有ソースへの変更はなし。

判断材料:

- **GitHub Release のみで足りるか**。preview / ニッチ段階の配布器としては十分機能している。
- **VS Code Marketplace**: 差別化(マクロ対応エディター)の試用チャネル。「少し試す」導線としては
  最も自然だが、publisher identity の継続コミットを伴う。
- **nuget.org**: `dotnet tool` / `PackageReference` の利便。パッケージ ID / 版の恒久性
  (`*.Unofficial` 命名・1.2.x 系)を固定する一方通行。既存 net4x 資産(3,219 DL)との関係。
- **署名**: 器を開ける場合のみ、必要範囲を identity 決定の一部として。

成果物(案):

- 器ごとの go/no-go と、その理由の log 記録。
- go の器がある場合の下流の具体値(package ID・publisher・版方針)を確定し、
  `pack` / release workflow が参照できる形にする(これが実装として次工程を解錠する)。
- go の器について、既存 release workflow を publish 対応へ拡張(手動 dispatch、prerelease、
  版リテラル禁止規約準拠)。初回は 1 パッケージで dry-run → 本 push。

受け入れ基準(案):

1. 各器の採否と理由が log にある。
2. go とした器で、別環境から入手 → `dotnet new` / 拡張 install → build → run が通り、
   版・命名・provenance が確定値と一致する。取り下げ可能な範囲の手順を log 化。
3. no-go の器はバックログへ。

可逆性: 公開部分は **低(一方通行)** のため go/no-go を器ごとに。リスク: 中(identity/命名の恒久性)。

### WP-O5: 試遊を心地よくする小さな showcase — 実施決定(2026-07-25、PO 指示)

「少し試せる間口」を魅力的にするための、**小さく閉じた**要素。生態系の大規模移植は行わない。
PO 指示により **2 段構成**とし、順序を固定する。

#### WP-O5a: semantic tokens(先行、本計画の作業対象)— 完了(2026-07-25、ログ 53)

マクロ拡張キーワードの動的彩色。**差別化(マクロ対応エディター)を直接可視化する小機能**で、
TextMate 文法では原理的に出せない色(`using` で開いた名前空間の syntax マクロが増やした
キーワード)をエディターに出す。

位置づけの再確認: 本項目は既存バックログの筆頭項目であり、根拠も既に記録済み —
`29-devenv2-plan.md` §11-1(「`ScanLexer` ベース。macro が拡張する keyword の動的彩色が
TextMate では原理的に不可能なため、価値は明確」= WP-M 完了後の優先順位 1 位)、
`36-prerelease-quality-plan.md` §2.2 の課題 **E1** の一部 / 同 §10-2。したがって本 WP は
新規発明ではなく、E1 のうち **semantic tokens だけ**を切り出して実施するもの。E1 の残り
(signatureHelp / documentHighlight / formatting / rename / codeAction)は §9 の非ゴールに留める。

成果物(実装済み。詳細は `53-wp-o5-log.md`):

- LSP `textDocument/semanticTokens/full`: engine の `ScanLexer`(`ScanTokenColor`)を全文に走らせ、
  LSP semantic token の type / modifier に写す。**マクロ由来キーワードは
  `env.Keywords \ CoreEnv.Keywords` で判定**して `macro` type で返す。
- 色分類は engine 非依存の純関数 + unit test(`ProjectInfo/SemanticTokenMapping.cs`。
  `HoverMarkup` / `CompletionMapping` / `GotoMapping` の前例に従う)。engine 色 enum の mirror
  ズレは server 起動時に警告する。
- legend は **標準 LSP token type のみ**で足りた(既定テーマがそのまま色を持つ)ので、拡張側の
  宣言は Nemerle 固有の **modifier 2 個(`quotation` / `escape`)** と `semanticTokenScopes` の
  テーマ fallback、`[nemerle]` での semantic highlighting 有効化のみ。TextMate 文法は fallback
  として維持(engine が分類しない範囲を色付ける)。
- 追加で必要になったもの: **`workspace/semanticTokens/refresh`**。行のキーワード集合は types tree
  由来なので、`didOpen` 直後の初回要求が build と競争して負けるとマクロキーワードが色付かないまま
  固定される。rebuild 完了時に再要求を促して解決した。
- raw LSP 統合シナリオ + 実 VS Code(Extension Host)シナリオで legend とマクロキーワードの type を固定。

受け入れ基準の結果:

1. ✓ マクロで増えたキーワードが `macro`、素のキーワードは `keyword` で返る。`using` を消すと
   同じ語が `variable`(識別子)に戻ることをテストで固定 = 動的であることの証拠。
2. ✓ 文字列(`$` splice を `escape` modifier で分離)・コメント・型・quotation(基底 type +
   `quotation` modifier)が色付き、複数行構文は `ScanState` の持ち回しで行単位トークンに分割される。
3. ✓ 共有ソース無改造 = Stage リビルド不要(`1.2.0.635` のまま)。回帰ゲートすべて green
   (raw LSP 30 / 実 VS Code 5+1 / 拡張 unit 23 / ProjectInfo unit+integration)。

#### WP-O5b: 試遊用サンプル(後続、PO 主導)

マクロが「効いている」ことが一目で分かる小さな sample の追加・整理(既存 samples の延長)。
**PO 自身が色々試しながら随時取り込む**ため、本計画では成果物を先に固定しない。
受け入れ基準は従来どおり「core でビルド・実行でき、README から辿れる」。

可逆性: 高。リスク: 低〜中。位置づけ上の必須ではなく、間口の質を上げる項目。
規模が「小さく閉じた」範囲を超えると判断したら、そこで止めてバックログへ回す。

---

## 6. 実装順序

1. **WP-O1(入口)** — 先行。外部からの発見性の前提。
2. **WP-O2(保存・再現)** と **WP-O3(runtime package 根治)** を並行 — いずれも配布の器に
   依存せず価値が出る。
3. **WP-O4(器の判断)** — 保存中心の位置づけに照らして採否を決める。go の器があれば
   下流の具体値を固定してから publish。
4. **WP-O5(showcase)** — 間口の質を上げる。**O5a(semantic tokens)を先に実装し、その後に
   O5b(試遊サンプル、PO 主導)**。

---

## 7. テストマトリクス(案)

| 対象 | 確認すること |
|---|---|
| README クイックスタート | Windows/Linux でコピペ手順が通る(WP-O1) |
| 保存・再現 | 公開 asset + seed だけからリリース再現、初回発行物と一致(WP-O2) |
| runtime package 消費 | `GenerateDependencyFile` 既定で deps.json 正常・build/run(WP-O3) |
| 器入手 e2e(go の器のみ) | 別環境で install → build/run、版・provenance 一致(WP-O4) |
| semantic tokens | ✓ マクロ拡張キーワードが別 type、既存の色が非回帰、raw LSP + 実 VS Code で固定(WP-O5a) |
| 試遊 showcase | core build・run/表示、README から辿れる(WP-O5b、PO 主導) |
| 既存回帰ゲート | testsuite 全数 / stage2·3 一致 / CLR4 スモーク / raw LSP / bundled server / CI(共有ソース改修時のみ) |

注: WP-O1/O2/O4 は主に文書・判断・スクリプトで、共有ソース(ncc / engine)を触らない見込み
= Stage リビルド不要。O3/O5 で共有ソースに及ぶ場合は §5-2 相当の回帰ゲート + 版ハザード手順
(Stage1 フルリビルド)を適用する。

---

## 8. PO が決めるべき論点

1. ~~**配布の器(WP-O4)**: GitHub Release のみで足りるか。足りないなら Marketplace / nuget.org の
   どちらを足すか~~ → **決着(2026-07-23、ログ 51)**: 両器とも **no-go**、GitHub Release のみを維持。
   恒久ではなく状況次第で再判断。
2. ~~**パッケージ命名・版方針**: `*.Unofficial` + 1.2.x 継続でよいか(器を開ける場合)~~ →
   **決着(同上)**: 公開しないため恒久命名の確定は不要。runtime package は単一
   `Nemerle.Runtime.Unofficial` を維持(利用者不可視・機能的実害なし・GitHub Release では ID 可逆)。
3. ~~**試遊 showcase(WP-O5)** と **semantic tokens** を入れるか~~ → **決着(2026-07-25、PO 指示)**:
   **両方入れる**。順序は semantic tokens(O5a)先行 → 試遊サンプル(O5b)。O5b は PO 自身が
   試しながら随時取り込むため、実装 WP としては O5a のみを扱う(§5 WP-O5)。

---

## 9. 非ゴールとバックログ

WP-O の非ゴール:

- 本格採用を狙うエディター機能(rename / codeAction / formatting / signatureHelp、E1 の残り / E2)。
  **semantic tokens のみ E1 から切り出して WP-O5a で実施**(間口の質と差別化の可視化のため)。
- マクロ生態系の移植(Peg 125 files / Statechart 342 files / csharp-parser 150 files 等)。
  本格採用を狙う場合の要件であり本計画のスコープ外。着手する場合は評価先行(36 §5-3)の独立スコープ。
- nemish REPL 等 tools 移植、`[Resource]` マクロ core 対応(B2)。
- 上流(rsdn/nemerle)への還元(上流は停止中)。
- Nemerle.Unsafe / Nemerle.WPF の core ビルド(WPF は Windows 専用 + System.Xaml 依存)。
- upstream バグ報告(A10/A11)、SourceLink / embedded PDB(A5)。
- 版バンプのスクリプト化(44 §8.6、Windows/CLR4 限定)。macOS 実地、multi-root。

方針: 未移植ライブラリに着手する場合は評価先行(ストレート移植 vs 再設計を log で PO 合意して
から実装、36 §5-3)。共有ソース改修時は VsIntegration 全体 + snippets/sharpdevelop を grep
([[feedback-shared-source-impact]])。

---

## 10. 参照文書

- `00-PLAN.md`: 原計画・WP 一覧・作業ログ。
- `36-prerelease-quality-plan.md`: WP-N 計画。§2.2 の課題 ID(D1/F1/F2/F3 等)と §10 バックログ。
- `DISTRIBUTION.md` / `packaging/README.md`: 配布・再現の現状(WP-O1/O2/O4 の出発点)。
- `46-prerelease-wp-n6-log.md`: CI / release workflow / 版ピン / in-tree seed の現況。
- `21-lsp-feasibility.md`: エディター差別化(マクロ対応)の根拠。engine API 対応表の
  `ScanLexer` / `ScanTokenColor` → `semanticTokens` 行が WP-O5a の出発点。
- `29-devenv2-plan.md`: WP-M 計画。**§11-1 が semantic tokens をバックログ筆頭に置いた根拠**
  (WP-O5a はこれを実施する)。§6.2/§6.3 の bridge / handler 設計は O5a でも踏襲。
- `README.md`(ルート): 現行の入口(upstream のまま、WP-O1 の改修対象)。
