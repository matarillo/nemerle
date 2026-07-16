# 36. WP-N: 公開前の品質固めと既知制約の解消 計画(ドラフト)

作成日: 2026-07-16

> **状態: ドラフト — Product Owner 未合意。**
> 2026-07-16 の PO ヒアリング(公開時期・CLR4 保全・ライブラリ復活範囲・新規提案の扱い)の
> 回答を反映しているが、それは PO の現時点の考え方を聞き取ったものであり、
> 本計画(バックログ §10 を含む)はこのドラフトとともに見直し・合意される。
> 合意されるまで `00-PLAN.md` / `29-devenv2-plan.md` へのリンク追記・WP 表更新は行わない。

## 1. この文書の位置づけと命名整理

WP-M(29-devenv2-plan.md)は WP-M1〜M6 で完了し、フォローアップとして
インストールガイド(`packaging/README.md`)、リリース集約(`pack-release.ps1` +
`release-info.json`)、Linux hover 起因リビルドループ修正(package `1.2.601-preview.2`)、
バージョン文字列整理(VSIX 0.8.2)まで済んでいる(`35-devenv2-wp-m6-log.md`)。

本文書はその次のフェーズ **WP-N「公開前の品質固めと既知制約の解消」** の実行計画である。
従来と同じ方式を採る: `00-PLAN.md` にはリンクと最新到達点のみ追記し(**合意後**)、
各 work package の実装結果は番号付き log 文書(`37-*.md` 以降)へ記録する。

**命名整理**: 過去文書(28/29 の §4・§11 等)は「WP-N = release 工程
(Marketplace/nuget.org 公開、署名、CI)」と呼んでいたが、これは当時の AI エージェントの
仮説的バックログであり Product Owner と合意したものではなかった。本ドラフトは
**公開工程を品質固めの後**に置く方針を採るため、公開フェーズを **WP-O** に繰り下げ、
本フェーズが WP-N を名乗る。
過去文書内の「WP-N(release)」への言及はすべて WP-O と読み替える。

## 2. 現在地と残課題の棚卸し

### 2.1 到達点

- .NET 10 self-hosted ncc(stage2/stage3、testsuite 601/636、残 35 失敗はすべて
  環境・ハーネス・BCL 差でコンパイラーバグ 0)。
- SDK-style `.nproj` + in-process `NccCompile` + `Nemerle.Sdk.Unofficial` /
  `Nemerle.Templates.Unofficial`(local feed / GitHub release assets、repo checkout 不要)。
- VS Code extension 0.8.2 + LSP server: project-aware diagnostics、hover、completion、
  definition/references、incremental rebuild(relocation)、provenance 版不一致警告。
- Windows 実地 + WSL の build 実証。Linux はエディター実地未保証。

### 2.2 未解決課題の棚卸し(全ログ横断、2026-07-16 時点)

本計画の策定にあたり、10〜35 の全ログと DISTRIBUTION.md から OPEN 項目を棚卸しした。
ID は本文書内の参照用。影響度は [高/中/低]。

**A. コンパイラーバックエンド(emission)**

| ID | 課題 | 出典 | 影響 |
|---|---|---|---|
| A1 | 決定的ビルド未完成(MVID/PE タイムスタンプは比較時マスクのみ。`BlobContentId.FromHash` で根治可) | 16 §5 | 中 |
| A2 | AssemblyVersion の git describe 依存による版境界ハザード(stage スクリプトに版一致チェック未実装、手動 touch 頼み) | 14 F4, 30 | 高 |
| A3 | `-linkres` の読み戻し不可(CoreCLR がマルチファイルアセンブリ非サポート。ncc 側で解決不能) | 17 §3c | 恒久 |
| A4 | 自動 Win32 バージョン情報リソース未実装(`-win32-resource` で代替可) | 17 §3b | 低 |
| A5 | embedded PDB / Document チェックサム / SourceLink 未配線 | 14 | 低 |
| A6 | PDB の言語 GUID 引数順バグ(全フレーバー共通の歴史的バグ、実害なし) | 14 F2 | 低 |
| A7 | `-compile-to-memory` + `-debug` on core 未検証 | 11, 14 | 中 |
| A8/A9 | CAS no-op・実 RSA 署名なし(CoreCLR 仕様) | 11, 12 | 恒久 |
| A10/A11 | dotnet/runtime のバグ 2 件(PersistedAssemblyBuilder のアセンブリレベル属性 PE 破損 / TypeBuilderImpl 非互換)。ncc は回避済みだが upstream 未報告 | 15, 17 | 低 |

**B. フロントエンド**: B1 C# パーサープラグイン未移植(testsuite 8 件)[中] /
B2 `[Resource]` マクロ core で hard-error(最小 resx パーサーで代替可)[低] /
B3 codedom 除外・B4 マルチモジュール廃止[恒久] / B5 overload tie-break の外部+ユーザー混在ケース未対応[低] /
B6 マクロ定義本体内 hover 不可(ncc 改修 + Stage リビルド要)[中]。

**C. testsuite 残 35 失敗の内訳**: 補助ライブラリ未ビルド 8+1 件(Nemerle.Linq/.Unsafe/.WPF)/
C# パーサー未登録 8 件 / 共有フレームワーク外 BCL 面 6 件 / BCL 差の期待値ずれ ~9 件
(double 書式・例外メッセージ・Obsolete 等)/ その他環境 3 件。
完全ハーネス(Nemerle.Compiler.Test.exe)の core 移植は未実施(外部 `-ncc`/`-runtime` 経路で代替中)。

**D. MSBuild/SDK**: D1 `GenerateDependencyFile=false` 依存(根治には net10 runtime package
公開 = WP-O)[高] / D2 `CoreCompile` 増分キーに DefineConstants 等が入らない[中] /
D3 `.nproj` 必須[恒久] / D5 版据え置き再パックの非伝播 / D6 Nemerle.Tool の 2 段起動 /
D7 auto-ref 化後のレガシー rsp 整理未実施[以上、低]。

**E. LSP/VS Code**: E1 未実装機能(semantic tokens / signatureHelp / documentHighlight /
formatting / rename / codeAction — engine API は実在)[中] / E2 multi-root 非対応[中] /
**E7 hover の engine 特性の穴・E8 references が型宣言スコープ限定**[中 → WP-N2 で解消] /
E3 incremental escape hatch が環境変数のみ / E4 relocation は単一 range change 限定 /
E5 relocation 失敗経路の fault injection 未テスト / E9 completion/hover 表示の細部 /
E10 ConsoleTest 期待値ずれ 6 件 / E11 nunit.framework.dll 手動コピー[以上、低]。

さらに **PO 報告(2026-07-16)**: hover で一部の主要 BCL 型が表示されない、
プロジェクトで定義した型も表示されないことがある。**事象自体がまだ正確に捉えられて
いない**ため、挙動確認から始める。E7/E8 の対応で解決する可能性も、別原因の可能性も
ある(→ WP-N2 トラック 3)。その後、具体的な再現ケース(`samples/Sokoban` の
hover 型名欠落 4 件、`samples/CompTimeSolver/Success` の parse error 1 件)が
報告された — 一覧は §6 WP-N2 トラック 3 に記録。

**F. リリース工程**: F1 nuget.org/Marketplace/署名/CI 未実施[高・WP-O] /
F2 net10 runtime package 未公開(D1 の前提。既存 net4x パッケージ消費者との版方針判断要)[高・WP-O] /
F4 provenance 完全自動化 / F5 Linux layout 手順[低]。

**G. クロスプラットフォーム**: G1 Mono 非対象[恒久・方針] /
G2 Linux/macOS エディター実地未保証[中] / G3 `test:vsix` の powershell.exe 依存[低] /
G5 Linux の DefineConstants 配線実地未検証[低]。

### 2.3 未移植サブシステム(批判的分析の材料)

- 生存しているが未ビルド: `Linq\`(Nemerle.Linq)、`snippets\peg-parser`(Nemerle.Peg、125 files)、
  `snippets\ComputationExpressions`、`snippets\Nemerle.Statechart`(342 files)、
  WPF/Unsafe/Xml/Async 等、`snippets\csharp-parser`(150 files)。
- tools: nemish REPL + Nemerle.Evaluation、cs2n、ndp、nemerle-unit、contracts — すべて未移植。
- デッド(移植対象外): `VsIntegration` の VS2010 本体(≈620 files)、NAnt 系、旧 sln/cmd 群。
- ルート README は .NET 10 ポートに未言及。CI 無し(全工程が手動 PowerShell)。

## 3. ゴール

WP-O(公開)に耐える品質へ到達する。具体的には:

1. stage2/stage3 がマスク無しの完全バイト一致で再現し、版境界ハザードが機械的に検出される。
2. hover / definition の型欠落(E7)、usages 探索の不完全さ(E8)、および報告事象
   (挙動確認から)が系統的に調査され、修正可能なものは修正、そうでないものは
   既知制約として文書化される。E8 については将来の rename / codeAction の前提
   (usage 収集の完全性)を満たすかどうかが確定する。
3. Nemerle.Linq が core でビルドされ、testsuite の Linq 起因の失敗と BCL 差の
   期待値ずれが解消する(目標 PASS 数は分類 A の内訳確定後に log で宣言)。
4. 看板ライブラリ(Nemerle.Peg / ComputationExpressions)の移植方針が評価・合意され、
   go 判断されたものは core で動作する(本項は優先度最下位。評価結果によっては
   WP-N からドロップしてバックログへ回す)。
5. Linux での VS Code extension 実地検証が完了し、テスト基盤が cross-platform 化される。

## 4. 非ゴールと恒久制約の受容

**非ゴール**(バックログへ。§10 参照):

- nuget.org / Marketplace 公開、署名、net10 runtime package(→ WP-O)。
- エディター機能第2弾(semantic tokens / signatureHelp / documentHighlight / formatting /
  rename / codeAction)、multi-root(E1/E2)。
- C# パーサープラグイン(B1)、nemish REPL、cs2n 等 tools の移植。
- README・入口ドキュメントの現代化(WP-O と同時が自然)。
- upstream 報告(A10/A11)、SourceLink / embedded PDB(A5)。

**恒久制約として受容する(直さない)提案**: 「既知の制約」として文書化を維持する
(受容の是非も本ドラフトの合意対象)。

- A3 `-linkres` 読み戻し不可(CoreCLR のランタイム制約)。
- A8/A9 CAS no-op・実 RSA 署名なし(CoreCLR は署名検証しない)。
- B3 codedom 除外、B4 マルチモジュール廃止。
- D3 `.nproj` 必須(`.csproj` では SDK の csc CoreCompile が後勝ちする)。
- G1 Mono 非対象(本移植は CLR4 無改造 + .NET 10 のみ)。

## 5. 方針(2026-07-16 PO ヒアリング回答の反映 — 計画全体は未合意)

1. **公開は品質固めの後**。本計画(WP-N)完了後に WP-O(公開)へ進む。
2. **CLR4 挙動の保全**: testsuite 全数 + stage2/stage3 + CLR4 スモークを回帰ゲートとして、
   共有ソース(ncc / engine)の改修に踏み込む従来 WP と同じ運用を**当面継続**する。
   ただしこれは**恒久決定ではない**。保全コストが便益を上回った時点で縮小を再検討する
   (見直しトリガー例: boot-4.0 ブートストラップ以外に CLR4 実行者がいなくなった、
   CLR4 検証が特定 WP の主要コストになった)。
3. **未移植ライブラリは評価ステップを先行**: ストレート移植か .NET 10 向け再設計かを
   ライブラリごとに評価し、log で PO 合意を得てから実装する。
4. **CI 化・README 現代化・upstream 報告・SourceLink は明示的バックログ**
   (優先度は認めるが本フェーズには入れない。最小 CI のみ WP-N6 として go/no-go 付きで置く)。
5. engine(`VsIntegration/Nemerle.Compiler.Utils`)の共有ソースを変更する際は、
   変更前に VsIntegration 全体を grep して VS2010 側の利用箇所への影響を確認する(既定の運用)。

## 6. Work packages

### WP-N1: ビルド再現性と地固め

成果物:

- **A2 版ハザードの機械化**: `build-stage2-core.ps1` が実行前に Stage1 コンパイラの
  `Nemerle.dll` AssemblyVersion とソースツリーの `GeneratedAssemblyVersion`
  (`git describe` 由来)の一致を検査し、不一致時は復旧手順(Stage1 フルリビルド)を
  提示して停止する。`pack-tool.ps1` / `pack-release.ps1` にも同等の整合チェックを通す。
  同じ検査を、WP-N6 でリポジトリにチェックインする stage1(または stage2)成果物が
  現在のソースツリーに対して古くなっていないかの検出にも流用できる形にする。
- **A1 決定的ビルド完成**: `Nemerle.CoreEmit.Emitter.Save` で `ManagedPEBuilder.Serialize`
  後に `BlobContentId.FromHash` を適用し、MVID / PE タイムスタンプをコンテンツハッシュ由来に
  する(PDB の PdbId も同様)。stage2/stage3 比較スクリプトのマスク処理を撤去し、
  完全バイト一致比較へ切替。
- **D2**: `CoreCompile` の増分ビルド判定に `DefineConstants` / `NemerleAdditionalOptions` の
  変更を反映(プロパティのハッシュを中間ファイルへ書き出し `Inputs` に加える方式)。
- **D7**: auto-ref 化(WP-A2)で不要になった `ncc.default.rsp` / `gen-default-rsp` 系の
  レガシー整理と `DISTRIBUTION.md` の記述更新。

受け入れ基準:

1. 同一ソースからの stage2 の 2 回独立ビルドが、マスク無しで 4 アセンブリ完全バイト一致。
   stage3 == stage2 も同様(`-debug` 有効時は PDB も一致、不一致なら非決定源を log に記録)。
2. Stage1 が古い版のまま `build-stage2-core.ps1` を実行すると、ビルド開始前に
   版不一致エラーと復旧手順が表示される(fixture で再現)。
3. `DefineConstants` のみ変更した `dotnet build` が再コンパイルを実行し
   (`samples/Defines` で確認)、無変更の再ビルドは従来どおりスキップされる。
4. provenance(`ncc-info.json` / server 版照合)が決定的 MVID と矛盾しない。
5. 回帰: testsuite 全数で新規 regression 0、CLR4 hello/hello2 スモーク、
   raw LSP / bundled server スイート、`npm test` PASS(toolchain 再 pack 後)。

リスク: **低**。CoreEmit・PowerShell・targets のみで CLR4 実行パス無影響。
注意点は (a) `pack-tool.ps1` の「ncc.exe と ncc.dll 同一バイト」前提と決定化の整合、
(b) stage 比較スクリプトの更新漏れ、(c) 増分キー方式が既存の増分スキップ動作を壊さないこと。

### WP-N2: engine 品質の解消(3 トラック: E7 / E8 / 報告事象)

**本フェーズの最優先事項**。WP-N1 より後に置くのは順序の都合
(§8: 版ハザード検査と決定的ビルドを、本 WP の Stage リビルド前に用意する)であり、
優先度は本 WP が最も高い。工数が競合する場合は WP-N3 以降を縮小して本 WP を優先する。

事前調査の実測結果・原因分析・仮実装計画は `38-prerelease-wp-n2-log.md` に記録する
(本節は事前計画のまま維持し、調査で得た知見の反映は 38 側で行う)。

対象を次の 3 トラックに分けて扱う。

**トラック 1: hover / definition の型欠落(E7)**

記録済みの既知特性: headless engine で local value / method の型欄が空になることがある、
静的 method 呼び出しの型修飾子や型注釈への hover / definition が null になる等
(31/33 の log)。再現マトリクス —
{BCL 型 / プロジェクト定義型 / 参照プロジェクト型 / マクロ生成型} ×
{型名・変数・メンバー・パラメーター・型注釈上の位置} ×
{初回 full rebuild 後 / incremental rebuild(relocation)後 / project reload 直後} —
で欠落パターンを網羅・分類し、completion への波及も確認する。原因の有力候補は
engine の `Project.FindObject` / `GetActiveDecl` 特性(B6 と同根の VS2010 由来挙動)と、
WP-A の `ExternalTypeInfo.collect_members` フィルタ(モダン BCL の ref return /
byref-like メンバーの取り込みスキップ)の型情報表示への波及。

**トラック 2: usages 探索の不完全さ(E8)**

references の usage 収集が「カーソルを含む型宣言スコープ」に限定される engine 特性。
references の結果が少なく出るだけでなく、**将来の rename / codeAction(WorkspaceEdit で
実コードを書き換える機能)の前提を塞いでいる**(取りこぼしのある rename は静かに
コードを壊すため出せない)。再現マトリクス —
{同一型内 / 同一ファイル別型 / 別ファイル / 別プロジェクト(ProjectReference)} ×
{型 / メソッド / プロパティ / local / パラメーター} — で取りこぼし境界を正確に測る。
本計画策定時の調査で `Engine-GetGoToInfo.n` の `UsagesInCurrentFile` が `Usages` と
同一実装に流れている(`//!!!` コメント付き)ことも確認しており、この探索系は
元々未完成の可能性がある — 実装状態の確定も調査対象。E8 の `GetUsages` も
`GetActiveDecl` 起点(`| Type => ... | _ => null` の構造)であり、トラック 1 と
切り分け作業を共有できる。

**トラック 3: 報告事象の挙動確認(原因未特定)**

2026-07-16 の報告: 「hover で一部の主要 BCL 型が表示されない、プロジェクトで定義した
型も表示されないことがある」。ただし**発生している事象がまだ正確に捉えられていない**
前提で扱う — 「hover」という報告語を出発点にしつつ、**まず挙動の確認から**始める:
再現手順の採取、発生時の server log(`window/logMessage` trace)、engine / workspace の
状態、発生頻度・タイミングの記録。確認の結果、トラック 1(E7)またはトラック 2(E8)の
対応で解決する可能性もあれば、別原因の可能性もある(候補: LSP bridge の version 照合 /
force-out による正当な null との混同、relocation 後の型ツリー不整合、project reload
タイミング、`collect_members` フィルタ)。切り分け後、該当トラックへ合流させるか、
独立した修正項目として扱うかを確定する。

**報告ケース(2026-07-16、具体的な再現箇所つき)**

`samples/Sokoban/Sokoban` — hover の型名欠落 4 件:

| ファイル | 行 | hover 対象 | 表示 | 欠落しているもの |
|---|---|---|---|---|
| main.n | 7 | `Main (args : array[string])` の `args` | `(function parameter) args : []` | `string`(配列要素型) |
| splayheap.n | 7 | `elem : SMap;` の `SMap` | `NSokoban.` | `SMap`(単純名。名前空間修飾のみ残る) |
| treesearch.n | 15 | `mutable depth = 0;` の `depth` | `(mutable local value) depth : `(型欄が空、`defined in BFS(...)` は表示) | 推論された local の型 |
| sokoban.n | 109 | `Hashtable [string, SMap]` の `Hashtable` | `Nemerle.Collections.[, NSokoban.]` | `Hashtable`・`string`・`SMap` の単純名すべて。※109 行目は `public class SMap`(107 行目)の内部 |

観察される共通パターン: 名前空間修飾(`NSokoban.` / `Nemerle.Collections.`)や
記号(`[]`、`[, ]`)は表示されるが、型の**単純名**が欠落する。ユーザー定義型(`SMap`)・
BCL 型(`string`)・Nemerle ライブラリ型(`Hashtable`)のいずれでも発生。
treesearch.n の件は E7 の既知特性(local value の型欄が空)と同型に見えるため、
E7 への合流候補。

`samples/CompTimeSolver/Success/success.n` — hover ではなくエディター診断 1 件:

- 2 行目 `WriteLine(SolveMaze("success.txt"));` に
  ``parse error near identifier `WriteLine': expecting type declaration`` が報告される。
  報告者の仮説: SolveMaze マクロの戻り値の型が不明のため(トップレベル式プログラム +
  コンパイル時マクロの組み合わせ)。挙動確認時に ncc 本体でのビルド成否と比較する
  (LSP 経路のみで出るのか、ncc でも出るのか)。

進め方(調査 → 中間判断 → 修正の三段):

1. **調査**: トラック 1/2 は再現マトリクスの採取・分類、トラック 3 は挙動確認と
   再現手順の確立を並行して進める。
2. **中間判断点**: 分類と原因が出た時点で「修正するスコープ」を PO と合意してから
   改修に入る(engine の設計特性が原因の場合、完全解消は大改修になり得るため)。
   E8 は「rename を安全に出せる水準の usage 完全性」を到達目標の基準として提示する。
   トラック 3 はここで合流先(E7/E8)または独立項目としての扱いを確定する。
3. **修正**: 合意したスコープを実装し、fixture / raw LSP テストで固定する。

成果物:

- トラック 1/2 の再現マトリクスの fixture 化と raw LSP integration test への固定。
- トラック 3 の挙動確認記録(再現手順・発生条件)と切り分け結果。
- 欠落・取りこぼしの分類・原因・修正/受容の判断を記録した log(`38-prerelease-wp-n2-log.md`。
  `37-*.md` は WP-N1 用に予約)。
- 修正(engine / ncc / LSP server いずれか)と、修正しないパターンの README への既知制約記載。
- ncc/engine 改修時: Stage1→2→3 リビルド、dist/server 再 pack、provenance 整合。

受け入れ基準:

1. トラック 1(E7): 既知の欠落パターンが再現 fixture 化され、原因が log で特定されている。
2. トラック 2(E8): 取りこぼし境界が fixture で定量化され、原因が log で特定されている。
   `UsagesInCurrentFile` と `Usages` の実装重複(`//!!!`)の実態も確定させる。
3. トラック 3: 報告事象が正確に再現・記述され(何が・どの操作で・どの状態で起きるか)、
   E7/E8 との関係(いずれかに包含されるか、別原因か)が確定している。
   別原因の場合は修正または既知制約としての文書化まで行う。
4. 修正可能と判定したパターンは修正され raw LSP テストで固定される。修正しない
   パターンは理由と回避策が文書化される(「欠落ゼロ」は要求しない)。ただし E8 は
   中間判断点で合意した到達水準(rename 前提を満たすか、明示的に満たさないと決めるか)を
   log に記録する。
5. 回帰ゲート: testsuite 全数 + stage2/3 完全一致(WP-N1 の決定性で検証が強化される)+
   既存 raw LSP 29 本 + bundled server + Extension Host + CLR4 スモーク全 PASS。
6. engine 共有ソース変更時は VsIntegration 全体 grep の影響確認記録を log に残す(§5-5)。

リスク: **中〜高**。ncc のメタデータ取り込みや engine 本体の改修に及ぶ可能性が高く、
その場合 Stage リビルドを伴う(= コンパイラー版が 601 から進み、パッケージ再 pack が必要)。
原因が VS2010 由来の engine 設計特性だった場合は「どこまで直すか」の判断が難所で、
中間判断点で PO 合意を取ることで沼化を防ぐ。トラック 3 は事象の正確な把握から始まる
ため見積り不確実性が最も大きいが、挙動確認自体は小さい作業であり、E7/E8 に合流すれば
追加コストはほぼ消える。トラック 1/2 は同じ `GetActiveDecl` / `FindObject` 系を通る
ため、切り分け作業の大部分を共有できる(別 WP に分けるより合計コストが下がる見込み)。

### WP-N3: 拡張ライブラリ復活 第1弾(Nemerle.Linq)+ testsuite 救済

本 WP は **Nemerle.Linq のみ**を対象とする。Nemerle.Unsafe / Nemerle.WPF は
後続バックログ(§10)で個別判断する。

成果物:

- `build-stage2-core.ps1` の rsp 方式を `Linq\`(Nemerle.Linq)へ横展開する core ビルド
  (スクリプト拡張または追加スクリプト)。
- `run-testsuite-core.ps1` が Nemerle.Linq を staging へ含め、testsuite 分類 A
  (8+1 件)のうち **Linq 起因分**を救済(Linq / Unsafe / WPF の内訳は着手時に確定して
  log に記録する)。
- BCL 差による期待値ずれ(~9 件)の方針決定と実施。推奨: **ランタイム別期待値の二重化**
  (CLR4 での検証能力を失う一方的な書き換えはしない)。
- dist/ncc / Sdk package への同梱可否の判断記録(同梱は WP-O の公開判断と整合させる)。

受け入れ基準:

1. Nemerle.Linq が core でビルドされ、testsuite 分類 A の Linq 起因テストが PASS。
2. 期待値ずれの方針が決定・実施され、CLR4 側の検証能力が維持されている。
3. testsuite 全数で新規 regression 0。分類 A の内訳確定にもとづく目標 PASS 数を
   着手時に log で宣言し、達成値を記録する。
4. 既存 4 アセンブリの stage2/stage3 完全一致に影響なし。

リスク: **中**。ライブラリソースが CoreCLR 非互換 API を含む場合は NET_4_0 ゲート追加
= 共有ソース改修になり CLR4 回帰検証が必要。Nemerle.Linq は System.Linq.Expressions の
挙動差(式ツリー生成)が未知。スコープを Linq に絞ったことで、System.Xaml 依存で
不成立リスクが最も高かった WPF を切り離せている(WPF/Unsafe はバックログで個別判断)。

### WP-N4: 看板ライブラリの評価と移植(Nemerle.Peg / ComputationExpressions)

**本計画の中で最も優先度が低い WP**。他 WP と工数が競合する
場合は縮小・後回しとし、状況によっては **WP-N からドロップしてバックログへ回す**
可能性がある(ドロップ判断は評価ステップの結果を材料に PO が行う)。

PO 方針(§5-3)により**評価ステップ先行の二段構え**:

1. **評価**: ライブラリごとに (a) 依存 API 監査(CoreCLR 非互換の有無)、
   (b) ストレート移植 vs .NET 10 向け再設計の比較と推奨、(c) 工数見積り、
   (d) テスト・サンプルの現況を調べ、評価レポートを log に書き **PO の go/no-go 合意**を得る。
2. **実装**(go のもののみ): core ビルド + 既存テスト/サンプルの .NET 10 動作 +
   `.Unofficial` 規約での NuGet パッケージング準備(公開自体は WP-O)。

受け入れ基準:

1. Nemerle.Peg / ComputationExpressions の評価レポートと PO の go/no-go 判断が log に記録される。
2. go としたライブラリは core でビルドされ、代表サンプル(Peg なら snippets 内の
   パーサーサンプル等)が .NET 10 で動作する。
3. nupkg が local feed から Sdk プロジェクトの `PackageReference` で consume できる。
4. 既存パイプライン(stage2/3、testsuite、LSP)に回帰なし。

リスク: **中〜大**。規模が大きく(Peg 125 files)、マクロ API の CoreCLR 互換性が未知の
ため工数が不確実。ただしコンパイラー本体無改修で閉じる見込みのため失敗しても他 WP に
波及せず、評価ステップで早期に打ち切れる。Statechart(342 files)は今回スコープ外
(評価結果次第で次期に提案)。

### WP-N5: Linux 実地検証とテスト基盤の cross-platform 化

成果物:

- Linux(実 VM または実機。WSL は補助)での VS Code + VSIX 実地検証の記録:
  clean-machine 相当 install → Sdk project の project-aware diagnostics / hover /
  completion / definition / incremental。
- `test:vsix` の PID 検査の powershell.exe 依存解消(Node API 化等)と、
  raw LSP / bundled server テストの Linux 実行経路(G3)。
- `pack-tool.ps1` / `pack-release.ps1` の pwsh(Linux)動作確認または Linux 手順整備(F5)。
- Linux での `DefineConstants` 配線確認(`samples/Defines`、G5)。
- `packaging/README.md` / extension README の Linux 手順の実測ベース更新。
- **stage1(net4 フレーバー、mscorlib 互換ファサード経由で CoreCLR 上に載る)が
  Linux の .NET 10 ランタイム上で `dotnet exec` 実行できるかの検証**。既存の WSL 実証
  (`build-stage2-core.ps1` の core フレーバー = stage2 の DLL 群を Linux へ持ち込んで
  `dotnet exec`)とは別の確認対象で、これまで未検証。成立すれば stage1→stage2→stage3 の
  ブートストラップ全体を Linux 上で `dotnet exec` のみで完結できることになり、
  WP-N6 の CI アーキテクチャの前提になる。

受け入れ基準:

1. Linux 上で VSIX install から project-aware diagnostics + hover/completion/definition が
   無設定で動作する(clean-machine 相当、自動または記録された手動手順)。
2. 主要テストスイート(raw LSP / bundled server / npm test 相当)が Linux で実行でき、PASS する。
3. 発見した Linux 固有問題は修正または既知制約として記録される(engine 改修に及ぶ場合は
   WP-N2 と同じ回帰ゲートを適用)。
4. Windows 側の全テストに回帰なし。
5. Linux 上で checked-in stage1(§6 WP-N6)を `dotnet exec` 実行し、そこから
   `dotnet exec <ncc> /from-file:<rsp>` で stage2 相当を生成できるかを確認し、
   結果(可否と、不可の場合の原因)を log に記録する。

リスク: **中**。preview.2 で修正した hover リビルドループ(path 大小文字)のような
Linux 固有問題が engine 側から再び出る可能性がある。それを公開前に洗い出すことが
本 WP の目的そのものなので、「問題が見つかること」は失敗ではない。stage1 の
Linux 実行が不成立だった場合、WP-N6 は「checked-in stage2(core フレーバー)から
始める」構成に縮小する(stage1→stage2 の再現だけを諦め、CI は stage2 以降のみ担当)。
macOS は今回も対象外(バックログ)。

### WP-N6(任意・go/no-go 判断付き): 最小 CI(Linux ベース、checked-in stage1 起点)

位置づけ: バックログ寄りだが、最小限の Linux ベース CI に限って本フェーズに任意で置く。
作業ボリュームの不確実性が懸念のため、**WP-N5 完了時点で go/no-go を PO と判断**する。

**アーキテクチャ**: `boot-4.0\` と同じパターンで、**現時点の stage1 成果物
(net4 フレーバー、`Nemerle.dll`/`Nemerle.Compiler.dll`/`Nemerle.Macros.dll`/`ncc.exe`)を
リポジトリにチェックインする**。CI(Linux 含む)は boot-4.0 → stage1 の生成ステップ
(csc・GAC 上の .NET Framework v4.0 参照アセンブリ等、Windows/CLR4 専用ツールが必要)を
毎回実行せず、チェックイン済み stage1 から `dotnet exec` のみで stage2 → stage3・
testsuite・LspServer/ProjectInfo テストまで到達する。boot-4.0 → stage1 の再生成は
ncc/lib/macros ソースが変わった際に**手動または別スケジュールで Windows 上で行い**、
更新した stage1 を再チェックインする(WP-N1 の A2 版一致チェックを、チェックイン済み
stage1 がソースツリーに対して古すぎないかの検出にも流用する)。
本アーキテクチャの前提(stage1 の Linux 上 `dotnet exec` 実行可否)は WP-N5 で検証する。

スコープ(go の場合、最小に固定):

- GitHub Actions の Linux runner で、チェックイン済み stage1 から
  `dotnet exec <ncc> /from-file:<rsp>` を実行して stage2(+ 可能なら stage3)を生成し、
  testsuite と LspServer / ProjectInfo テスト一式を実行する workflow。
- boot-4.0 → stage1 の生成自体と CLR4 スモークは CI の対象に**含めない**
  (Windows/CLR4 専用ツールが必要なため。stage1 更新時の手動実行として引き続き
  回帰ゲートを維持する)。
- WP-N5 で stage1 の Linux 実行が不成立と判明した場合は、起点を
  checked-in stage2(core フレーバー)に繰り下げ、CI は stage2→stage3 とテストのみを
  担当する(stage1→stage2 の再現は CI スコープ外のまま手動回帰ゲートに残す)。

受け入れ基準(go の場合): push/PR で自動実行され green。実行時間の目安 15 分以内。
チェックイン済み stage1(または stage2)がソースツリーに対して古い場合、workflow が
検出して警告する。no-go の場合: 判断理由を log に記録しバックログ(§10-1)へ。

リスク: **中**。CI 環境での .NET 10 SDK / VS Code headless / テスト依存の整備量が
読みにくい。go/no-go 判断点とスコープ固定で計画倒れ・肥大化を防ぐ。
checked-in stage1(または stage2)の鮮度管理(WP-N2 等でソースを変更した後に更新を
忘れる)が新たな運用負荷になるため、A2 の版一致チェックによる自動検出を必須とする。

## 7. テストマトリクス

| 対象 | 確認すること |
|---|---|
| stage2 × 2 独立ビルド + stage3 | マスク無し完全バイト一致(WP-N1) |
| 版不一致 fixture(古い Stage1) | 事前チェックがビルド前に停止させる(WP-N1) |
| `samples/Defines` | DefineConstants 変更での再コンパイル、Linux での配線(WP-N1/N5) |
| hover/definition 再現マトリクス fixture(新規) | BCL/プロジェクト/参照/マクロ型 × 位置 × rebuild 状態の欠落分類(WP-N2 トラック 1) |
| usages 再現マトリクス fixture(新規) | 同一型内/別型/別ファイル/別プロジェクト × シンボル種別の取りこぼし境界(WP-N2 トラック 2) |
| 報告事象の挙動確認(新規) | 再現手順・発生条件の確立と E7/E8 への合流可否の切り分け(WP-N2 トラック 3) |
| 既存 raw LSP 29 + bundled server + Extension Host | engine/コンパイラー改修の回帰ゲート(WP-N2 以降共通) |
| testsuite 全数(636) | 分類 A の Linq 起因分の救済、期待値二重化、新規 regression 0(WP-N3) |
| Peg / ComputationExpressions のサンプル | go 判断後の .NET 10 動作(WP-N4、実施した場合) |
| Linux clean-machine 相当 | VSIX install → 全 language features(WP-N5) |
| CLR4 hello/hello2 + boot ビルド | CLR4 スモーク(共有ソース改修時、全 WP 共通) |

## 8. 実装順序

1. **WP-N1**(再現性)。最小工数で以降の全 WP の回帰検証を強化する
   (バイト一致が使えると「改修の無影響」を機械的に示せる)。版ハザード解消は
   WP-N2 で Stage リビルドが発生する前に済ませておく必要がある。
   ※着手順が先なだけで、**フェーズの優先度は WP-N2 が最上位**。
2. **WP-N2**(engine 品質: E7 / E8 / 報告事象の 3 トラック)。実際の利用で体感されている
   品質問題であり、E8 は将来の rename / codeAction の前提でもある。Stage リビルドを
   伴う可能性が最も高いため、パッケージ再 pack の連鎖をライブラリ WP より先に済ませる。
   工数が競合する場合は WP-N3 以降を縮小してでも本 WP を完遂する。
3. **WP-N3**(Nemerle.Linq + testsuite)。WP-N2 で確定したコンパイラー世代の上で
   ライブラリを積む。
4. **WP-N4**(Peg 等の評価→移植)。**優先度は本計画で最下位**。評価は WP-N3 と
   並行可能(読み取り調査のみのため)だが、実装は他 WP と競合したら後回し、
   場合によっては WP-N からドロップ(§6 WP-N4)。
5. **WP-N5**(Linux 実地)。機能・ライブラリが出揃った状態で実地検証する方が
   検証範囲が広く、手戻りがない。
6. **WP-N6**(最小 CI)。WP-N5 の cross-platform 化が前提。go/no-go を判断。

## 9. リスクと対策

| リスク | 対策 |
|---|---|
| 決定化(MVID 置換)が provenance / pack の前提と衝突 | WP-N1 受け入れ基準に整合確認を内蔵。stage 比較・pack スクリプトを同一 WP で更新 |
| hover 欠落・usages 取りこぼし(E7/E8)の原因が engine の設計特性で大改修になる | WP-N2 に中間判断点(調査完了時に修正スコープを PO 合意)を設定。E8 は「rename 前提を満たす水準」を判断基準として提示 |
| 共有ソース改修による CLR4 回帰 | testsuite 全数 + stage2/3 バイト一致 + CLR4 スモークの回帰ゲート(§5-2)。VsIntegration 全体 grep(§5-5) |
| Stage リビルドで版が進み package 再 pack が必要になる | WP-N1 の版一致チェックで混在を機械検出。provenance(ncc-info.json)で照合 |
| Nemerle.Linq の式ツリー生成に CoreCLR 挙動差 | NET_4_0 ゲートで分岐し CLR4 回帰ゲートで検証。不成立なら理由を log に記録して撤退 |
| Peg 等の工数爆発 | 評価ステップ先行 + go/no-go(§5-3)+ WP-N からのドロップ選択肢(優先度最下位)。コンパイラー本体無改修で隔離 |
| Linux 固有の engine 問題の再発 | WP-N5 は「洗い出しが目的」と位置づけ、修正は WP-N2 と同じ回帰ゲートで実施 |
| 最小 CI の作業量が想定超過 | WP-N6 は任意 + スコープ固定 + go/no-go。no-go でもバックログに残す |
| 期待値二重化で testsuite の保守が複雑化 | 二重化はランタイム差のある ~9 件に限定し、方式を log で固定 |

## 10. バックログ案(WP-N 完了後の優先順位)

**このリストも未合意である。** 2026-07-16 のヒアリングで示された PO の現時点の考え方を
反映した案であり、本ドラフトとともに見直し・合意する。
WP-O(公開フェーズ)を最優先とし、その後は以下:

**WP-O: 公開フェーズ**(本計画完了後): nuget.org 公開(F1)、
**net10 版 runtime package**(F2 — deps.json 問題 D1 の根治。既存 net4x パッケージ
利用者 3,219 DL との版方針判断が必要)、VS Code Marketplace 公開・署名(F3)、
入口ドキュメント現代化(ルート README → dotnet-port / リリースページ接続)。

バックログ(優先度順):

1. 最小 CI の本格化(WP-N6 を no-go とした場合はここが起点)。
2. エディター機能第2弾: semantic tokens(マクロ拡張キーワードの動的彩色は TextMate では
   原理的に不可能で価値明確)/ signatureHelp / documentHighlight / formatting(E1)。
3. rename / codeAction(E1 の残り): WP-N2 の E8 解消を前提に、WorkspaceEdit 基盤を
   共有して rename → codeAction(未実装メンバー生成)の順で実装。
   engine 品質の残りは B6 マクロ定義本体内 hover(WP-N2 の調査結果次第で優先度を再評価)。
4. upstream 貢献: dotnet/runtime へ A10/A11 の報告、rsdn/nemerle への還元検討。
5. SourceLink / embedded PDB(A5)、`-compile-to-memory` + `-debug` 検証(A7)。
6. Nemerle.Unsafe / Nemerle.WPF の core ビルド(testsuite 分類 A の残り。
   WPF は System.Xaml / WindowsBase 依存で不成立リスク大・Windows 専用である点を
   踏まえ個別判断)。
7. C# パーサープラグイン(B1、testsuite 8 件)、nemish REPL / Nemerle.Evaluation(A7 依存)。
8. `[Resource]` マクロの core 対応(B2、最小 resx パーサー)。
9. multi-root / 複数 project(E2。project ごとの server process 分離を優先比較)。
10. その他小粒: A4 / A6 / B5 / D5 / D6 / E3 / E4 / E5 / E9 / E10 / E11 / F4、
    Statechart 等の追加ライブラリ評価、macOS 実地検証。
    WP-N4 を WP-N からドロップした場合の Peg / ComputationExpressions もここが受け皿。

## 11. 参照文書

- `00-PLAN.md`: 原計画と WP 一覧・作業ログ。
- `29-devenv2-plan.md`: WP-M 計画(§11 の優先順位リストは仮説。本計画 §10 が合意版)。
- `35-devenv2-wp-m6-log.md`: WP-M6 実装結果とリリース後フォローアップ(preview.2 / 0.8.2)。
- `38-prerelease-wp-n2-log.md`: WP-N2 事前調査(実測証跡・原因分析・仮実装計画。`37-*.md` は WP-N1 用に予約)。
- `30〜34-*.md`: WP-M1〜M5 実装結果(hover/completion/definition/incremental の現況)。
- `18-testsuite-log.md`: testsuite 失敗分類(WP-N3 の出発点)。
- `16-determinism-diagnosis.md` / `14-pdb-log.md`: 決定性・版ハザードの診断(WP-N1 の出発点)。
- `10-metadata-import-log.md`: `ExternalTypeInfo.collect_members` フィルタ(WP-N2 の調査候補)。
- `DISTRIBUTION.md` / `packaging/README.md`: 配布の現状(WP-O の出発点)。
