# 56. WP-P 計画 — エディター機能第2弾(バックログ E1 の残り)

作成日: 2026-07-26 / ブランチ: `wip/dotnet-port`

**§1〜§7 は着手前に書いた計画で、当時はドラフトだった。§8 は実施後(2026-07-26)の記録で、
PO 合意のうえ `00-PLAN.md` と `36-prerelease-quality-plan.md` へ反映済み。**
計画と実績が食い違う箇所は §3-1 と §6 に日付入りの訂正を入れてある。

## 1. 位置づけ

`36-prerelease-quality-plan.md` §2.2 の課題 **E1「LSP 未実装機能」** のうち、
WP-O5a で切り出した semantic tokens を除いた残り 5 機能を実装する。

| 機能 | LSP method | E1 内の状態 |
|---|---|---|
| semantic tokens | `textDocument/semanticTokens/full` | **WP-O5a で実装済み**(`53-wp-o5-log.md`) |
| signatureHelp | `textDocument/signatureHelp` | 本 WP |
| documentHighlight | `textDocument/documentHighlight` | 本 WP |
| formatting | `textDocument/formatting`(+ `rangeFormatting`) | 本 WP(評価先行) |
| rename | `textDocument/rename` + `prepareRename` | 本 WP |
| codeAction | `textDocument/codeAction` | 本 WP |

根拠は既に記録済み: `36-*` §10 バックログ 2 番(signatureHelp / documentHighlight / formatting)と
3 番(rename / codeAction。「WP-N2 の E8 解消を前提に WorkspaceEdit 基盤を共有して
rename → codeAction の順」)。本 WP はそのバックログ項目をそのまま実施するもので、新規発明ではない。

**engine API は 5 機能すべて実在し、core ビルドに既に含まれている**
(`LspFeasibility/Nemerle.Compiler.Utils.nproj` の `NemerleCompile` 一覧で確認):

| 機能 | engine 側の入口 | 可視性 |
|---|---|---|
| signatureHelp | `IIdeEngine.BeginGetMethodTipInfo` → `MethodTipInfoAsyncRequest.MethodTipInfo` | public |
| documentHighlight | `IIdeEngine.BeginHighlightUsages` → `IIdeProject.SetHighlights` コールバック / 代替: `GetGotoInfo(..., Usages)` + 同一ファイル filter | public |
| formatting | `CodeFormatting.Formatter.BeginFormat` / `FormatDocument` → `List[FormatterResult]` | public |
| rename | `GetGotoInfo(..., GotoKind.Usages)`(WP-N2 で完全性を実測済み) | public |
| codeAction | `IIdeEngine.BeginFindUnimplementedMembers` / `BeginFindMethodsToOverride` → `IIdeProject.AddUnimplementedMembers` / `AddOverrideMembers` コールバック、生成は `InterfaceMemberImplSourceGenerator` | public(ただし §4 の注意) |

## 2. ゴール / 非ゴール

**ゴール**

1. 上表 5 機能が VS Code 拡張から実用水準で動く(= 実機で使える、raw LSP で固定されている)。
2. **共有ソース(`ncc` / `lib` / `macros` / `VsIntegration`)無改造** = Stage リビルド不要、
   Nemerle assembly version は `1.2.0.635` のまま。WP-O5a と同じ制約で通す。
3. 各機能が「engine の単一スレッド契約」を守る(§3-1)。
4. 既存 34 本の raw LSP シナリオに回帰ゼロ。

**非ゴール**

- multi-root / 複数プロジェクト(E2)。**cross-project シンボルの rename は有効化しない**
  (`39-*` §7-4 の申し送りをそのまま引き継ぐ)。
- 外部アセンブリ member の rename(source が無いので原理的に不可 — 拒否して理由を返す)。
- codeAction のうち「診断からの自動修正」全般(§4 の 2 種に限定)。
- 新規 NuGet / npm 依存の追加、VSIX の公開。

## 3. 全 WP に共通する制約(WP-O5a の教訓)

### 3-1. engine 呼び出しは AsyncWorker スレッドで行う

`53-wp-o5-log.md` §設計-2 の確定事項: `_engineOperations` ロックは `NemerleProject` 自身の帳簿を
直列化するだけで、**リロードと本体型付けが走る worker スレッドとは直列化しない**。worker 外から
型解決を伴う engine API を呼ぶと、`ExternalTypeInfo` の半完成インスタンスを掴んで本体型付けが
`NullReferenceException` になり、**そのメソッドの hover が編集まで永久に死ぬ**。

したがって新機能はすべて `ColorizeRequest` の前例(`AsyncRequestType.EmptyRequest` +
`AsyncWorker.AddWork` + `EngineRequestBridge`)か、engine 自身の `Begin*`(worker 投入済み)を使う。

**【2026-07-26 訂正】** 初版は「既存の `GetGoto`(definition / references)は同じハザードを
持ったまま同期呼び出しで残っているので、WP-P3 の先頭で worker 側へ寄せる」と書いていたが、
**これは誤りだったので取り消す**(WP-P2 実施中に判明、engine ソースで確認済み)。

`IIdeEngine.GetGotoInfo` の実体は `Engine-GetGoToInfo.n:31` の

```
public GetGotoInfo(source, line, col, kind) : array[GotoInfo]
{
  def request = BeginGetGotoInfo(source, line, col, kind);  // AsyncWorker.AddWork 済み
  _ = request.AsyncWaitHandle.WaitOne();
  request.GotoInfos
}
```

= **同期版は「worker へ投げて呼び出し元スレッドが待つ」だけ**であり、**型解決そのものは
既に worker スレッドで走っている**。したがって WP-O5a の NRE ハザード(worker 外で型解決を
走らせる)には該当しない。**WP-P3 に「経路の worker 化」は不要**。

残る性質は 2 点で、いずれも別問題:

1. 同期版は LSP スレッドを worker のキューが捌けるまでブロックし、**キャンセルできない**
   (`WaitOne()` に timeout も CancellationToken も無い)。
2. 返ってきた `GotoInfo` の遅延メンバー(`FilePath` = コンパイラーのファイル名表の読み出し)を
   LSP スレッドで読むのは WP-P1 / WP-P2 が避けた形と同じ。フラット化は worker 側で済ませるのが望ましい。

なお **worker 上で `GetGotoInfo`(同期版)を呼ぶことはできない**: 自分自身の完了を単一の worker
スレッド上で待つことになり自己デッドロックする。`BeginGetGotoInfo`(非同期版)は
`internal partial class Engine` のメンバーで `IIdeEngine` に無いため、server からは見えない
= **非同期経路を使うには interface への追加 = 共有ソース改修**(§3-3 により PO 判断)。

### 3-2. 版

**VSIX / server の版は 0.10.0 のまま据え置く。** 0.10.0 は WP-O5a で繰り上げたが**未公開**
(公開済みは `release/1.2.635-preview.2` の 0.9.0)であり、規約が禁じるのは**配布済み版番号の再利用**
なので、未公開の 0.10.0 に機能を足すのは規約内。公開時に改めて判断する。

### 3-3. 共有ソースを触りたくなったら止める

engine 側の改修が必要と判断した時点で**実装を止めて PO に報告**する(Stage リビルド → 版バンプ →
release セット再生成という別コストの連鎖に入るため、機能単体で決めてよい判断ではない)。
触る場合は先に `VsIntegration` 全体を grep して legacy VS 統合側の消費者を確認する
(`38-*` §5.6 / `39-*` §8 の手順)。

### 3-4. 各 WP の完了ゲート

| ゲート | 対象 |
|---|---|
| `ProjectInfo.Test`(unit)+ `--integration` | 純関数マッピングを足した WP |
| raw LSP 統合(`Nemerle.LanguageServer.IntegrationTest`) | **全 WP 必須**(新規シナリオ + 既存 34 本の無回帰) |
| `npm run check-types` / `lint` / `test` | manifest / TS を触った WP |
| `npm run test:integration`(実 VS Code) | WP 完了時にまとめて 1 回 |
| `git diff --check` | 全 WP(`VsIntegration` の CRLF は `.gitattributes` 済み) |

## 4. 各機能の設計方針(実装 WP への入力)

### signatureHelp(WP-P1)

`BeginGetMethodTipInfo` は `MethodTipInfoAsyncRequest.MethodTipInfo` に結果を載せて返すので、
hover / completion と同じ `EngineRequestBridge` にそのまま載る(コールバック経路なし)。
`MethodTipInfo` は `GetCount` / `GetName` / `GetType` / `GetDescription` /
`GetParameterCount` / `GetParameterInfo(index, parameter) : (name, display, doc)` /
`ParameterIndex` / `DefaultMethod` を持つ = LSP の `SignatureInformation` /
`ParameterInformation` / `activeSignature` / `activeParameter` に素直に写像できる。
写像は engine 非依存の純関数(`ProjectInfo/SignatureHelpMapping.cs`)+ unit test に置く
(`HoverMarkup` / `CompletionMapping` / `SemanticTokenMapping` と同じ前例)。
`triggerCharacters` は `(` と `,`、`retriggerCharacters` は `,`。
`HasTip == false` は「シグネチャ無し」= null を返す。

### documentHighlight(WP-P2)

2 経路あり、**実測で選ぶ**:

- (a) `BeginHighlightUsages`: engine 側で same-file filter 済み(`Project.Refactoring.n:174`)。
  ただし結果は `IIdeProject.SetHighlights` コールバックで返り、**`request.MarkAsCompleted()` が
  コールバックより先に呼ばれている**(`Engine-HighlightUsages.n`)ため、bridge の完了と結果到着が
  競合する。使うなら `NemerleProject.SetHighlights`(現在は空実装)で受けて相関させる仕掛けが要る。
- (b) `GetGotoInfo(..., GotoKind.Usages)` を worker 経路で呼び、`FileIndex` で自ファイルに絞る。
  WP-N2 が完全性を実測した経路そのもので、rename と実装を共有できる。

**(b) を第一候補**とする(rename と同じ収集結果を使うので「ハイライトされた場所が rename される」
が保証される)。`GotoInfo.UsageType` から LSP の `DocumentHighlightKind`(Definition / Write / Read)
へ写像する。(a) を選ぶ場合はコールバック相関の設計を log に残すこと。

### formatting(WP-P4 — 評価先行)

`Formatter.FormatDocument` / `Format(loc, ...)` は `List[FormatterResult]`(置換範囲 + 置換文字列)を
返すので LSP `TextEdit[]` へは機械的に写る。**問題は品質**: 実体は VS2010 時代の
`CodeIndentationStage2` のみ(`CodeLineBreakingStage` は Formatter 内でコメントアウト済み)で、
実際の出力を誰も評価していない。

したがって **`47-wp-o-plan.md` §5-3 の「評価先行方式」を適用**する:

1. 既存 samples の実ソース(`Sokoban/sokoban.n` 654 行、`Latin`、`SyntaxTree`、マクロ定義ファイル)に
   `FormatDocument` をかけ、**入力と出力の diff を採取**する。
2. 判定基準: (i) 構文が壊れない(整形後も診断が増えない)、(ii) 既に整形済みのコードが
   ほぼ無変更、(iii) quotation / syntax マクロ構文を破壊しない。
3. **go**: 全文 + range を提供。**部分 go**: range のみ、または既定 off の設定で提供。
   **no-go**: 実装せず、採取した diff を証跡として log に残しバックログへ戻す(この判断は PO へ上げる)。

### rename(WP-P3)

`prepareRename` で「rename 可能か」を先に答える(不可なら VS Code は入力ボックスを出さない):

- 対象が current project 内の source に宣言を持つ → 可(識別子範囲を返す)。
- 外部アセンブリ member(`GetGoto` の `ExternalOnly`)→ 不可 + 理由。
- **ProjectReference 先を含む可能性のあるシンボル → 不可**(`39-*` §7-4)。判定方法は
  「宣言位置が current project の source に無い」= 上の 2 条件で機械的に落ちる。

`rename` は `GotoKind.Usages`(宣言込み)の全 location を `WorkspaceEdit.Changes` に変換する。
**新しい名前の妥当性検査**(識別子として合法か、キーワード衝突。マクロ由来キーワードは
WP-O5a の `IsMacroKeyword` の判定を再利用できる)を入れ、不正なら `ResponseError` を返す。
WorkspaceEdit の組み立ては純関数(`ProjectInfo/WorkspaceEditMapping.cs`)+ unit test に置き、
WP-P5 と共有する。

### codeAction(WP-P5)

範囲を 2 種に限定する(engine が既に持っている生成器を使えるもの):

1. **未実装インターフェイスメンバーの生成**(`BeginFindUnimplementedMembers` +
   `InterfaceMemberImplSourceGenerator`)。
2. **override 可能メンバーの生成**(`BeginFindMethodsToOverride`)。

**注意(実装前に必ず実測すること)**: `Engine-FindUnimplementedMembers.n` は成功枝で
`AsyncWorker.AddResponse(...)` の**直後に `assert(false)` を実行している**(upstream の残骸)。
`AsyncWorker.ThreadProc` は work の例外を握って `MarkAsCompleted()` するため
(`Async/AsyncWorker.n:117-125`)、**コールバックはレスポンスキューに載った後に例外が飛ぶ =
結果は受け取れる**はずだが、これは机上の読みである。**最初にこの 1 点を実測で確定**し、
- 受け取れる → コールバック経路で実装(engine 無改造)。
- 受け取れない → **共有ソース改修が必要 = §3-3 により停止して PO 判断**。
  その場合の代替として「診断ベースの軽量 quick fix のみ」に縮退する案を併記して上げる。

## 5. WP 分割と順序

| WP | 内容 | 依存 | リスク |
|---|---|---|---|
| WP-P1 | signatureHelp | — | 低 |
| WP-P2 | documentHighlight | — | 低 |
| WP-P3 | rename(WorkspaceEdit 基盤) | P2 と実装共有 | 中 |
| WP-P4 | formatting(評価 → go/no-go → 実装) | — | **高**(no-go あり) |
| WP-P5 | codeAction | P3(WorkspaceEdit 基盤) | 中〜高(§4 の assert) |

**順序を「E1 の列挙順」から変えている理由**: (1) rename と codeAction は WorkspaceEdit 基盤を
共有するので連続させる(36 §10-3 の指示どおり)。(2) formatting だけが no-go を含む評価案件なので、
確実に価値が出る 4 機能を先に確定させ、formatting の判断を後ろに置く。

**並行実行はしない。** 全 WP が `LspServer/Program.cs`(ハンドラー登録)・`NemerleProject.cs`・
`LspServer.IntegrationTest/Program.cs`(シナリオ)という同じファイルに触るため、同一 working tree での
並行編集は競合する。formatting の**評価だけ**は読み取り主体なので P1 と並行してよい。

## 6. 成果物

- 実装: `dotnet-port/LspServer/Nemerle*Handler.cs`(新規 5 本前後)、`NemerleProject.cs`、
  `Program.cs`、`ProjectInfo/*Mapping.cs`(純関数 + unit test)。
- テスト: raw LSP シナリオ(各機能 2〜4 本)、`ProjectInfo.Test` の unit、拡張 unit / 実 VS Code。
- 文書: 本書 + 実装ログ。**番号は作成順**(WP 番号順ではない): `57`(P1)/ `58`(P2)/
  `59`(P3)/ `60`(**P5 codeAction**)/ `61`(**P4 formatting**)。§5 の順序どおり formatting を
  最後に回したため、P4 と P5 の番号が入れ替わっている。
  完了時に `00-PLAN.md` の WP 表・作業ログと `36-*` §2.2 の E1 記述を更新(**PO 合意後**)。

## 7. 参照文書

- `36-prerelease-quality-plan.md`: §2.2 の E1 定義、§10 バックログ 2/3。
- `39-prerelease-wp-n2-log.md`: §5 references の完全性実測、§7-4 rename 前提の判定、
  §7-5 `UsagesInCurrentFile` の申し送り。
- `53-wp-o5-log.md`: engine のスレッド契約(§設計-2)、bridge の使い方、版の規約、
  raw LSP シナリオと probe の作り方。
- `47-wp-o-plan.md`: §5-3 評価先行方式。

## 8. E1 としての到達点と残課題(2026-07-26 追記、実施後)

**本節は計画ではなく実施後の記録**である。5 機能の実装は 57〜61 の各ログにあり、それぞれに
「既知の制約 / 残課題」節がある。本節はそれらを**課題 E1 の視点で集約した索引**であって、
詳細を複製しない(複製すると必ずずれる)。1 項目 1 行 + 詳細のありかを示す。

### 到達点

WP-P1〜P5 を完了し、E1 が列挙した 5 機能はすべて実装された。共有ソースは全 WP で無改造
(Stage リビルド不要、Nemerle assembly version は `1.2.0.635` のまま)。raw LSP スイートは
34 → **49 シナリオ**。5 機能とも PO の実機確認(WSL + VS Code)を通過している。

| 機能 | 状態 | ログ |
|---|---|---|
| signatureHelp | 実装 | 57 |
| documentHighlight | 実装 | 58 |
| rename / prepareRename | 実装 | 59 |
| codeAction | 実装(**未実装インターフェイスメンバーの生成のみ**) | 60 |
| formatting | 実装(**全文整形のみ**) | 61 |

### E1 の未完部分(意図的な縮小、2 点)

**この 2 点だけが「E1 として実装しきっていない部分」**である。どちらも理由を記録した上での
スコープ縮小で、機能そのものは動いている。

1. **codeAction の override メンバー生成が無い**。配線は動くことを確認済みだが、engine が返す
   候補は base を持たない class では `System.Object` の virtual 4 個で、それを 1 アクションとして
   全 class 宣言の電球に出すのは害。member ごとのアクション + 専用 fixture が要るため、
   **未テストで出荷しない**判断で先送り(詳細 60 §設計-4)。
2. **formatting は全文のみ**で、range formatting / on-type formatting は非提供。range は engine 側に
   API があるが**未評価**、on-type は失敗時の害が大きいため対象外(詳細 61 §設計-4)。

### E1 の残りではないが、WP-P で判明・確定したもの

E1 の未完部分と混同しないよう分けて記録する。いずれも `36-*` §2.2 / §10 に独立項目として立てた。

- **engine formatter が `samples/Sokoban/Sokoban/sokoban.n` で例外を投げる**(自分が生成した変更
  同士の衝突を検出)。サーバーは編集ゼロ + ログで安全に失敗する。**共有ソース側の欠陥**なので
  E1 とは別項目(36 の **B7**、詳細 61 §既知の制約)。
- **ハイライトがまれに一瞬で消える**(リロード直後、Output パネル表示時)。サーバーは実測で除外
  済み、**クライアント側で原因未確定**。優先度を下げて記録のみ(36 の **E12**、詳細 58 追記2)。
- **cross-project rename は無効のまま**。これは E1 の未完ではなく **E2(multi-root / 複数 project)
  の帰結**であり、`39-*` §7-4 の判定をそのまま引き継いでいる。E2 の着手時に一緒に解決する。
- **拡張の `wordPattern` が `u` フラグ無しで壊れていた**(WP-L 期からの潜在欠陥)。WP-P2 の実機確認で
  発覚し**修正済み**。documentHighlight だけでなく単語選択・`Ctrl+D`・単語単位移動も直った
  (詳細 58 追記1)。残課題ではないが、E1 の実機評価が拾った副産物として記録する。

### リリース判断(2026-07-27 決着)

**PO 判断 = GO(条件付き)。** 条件は「**リリースノートに 0.10.0 でできるようになったことと
既知の挙動を一覧にまとめて書けるなら公開してよい**」。すなわち上記の残課題は**残したまま出す**が、
**利用者が事前に知っていられる形にすること**が引き換えである。この条件を満たす形で
**`release/1.2.635-preview.3` を発行した**(commit `745e8148b`、VSIX / server 0.10.0、
nupkg `1.2.635-preview.3`)。

- 発行は**一式**。コンパイラー側は preview.2 から無変更(`1.2.0.635` のまま)だが、部分リリースの
  器は無く(workflow はタグからフルセットを構築し、`pack-release.ps1` は nupkg か VSIX が欠けると
  throw する)、公開済みリリースは不変に保つ規約なので N=3 で再発行した。
- **条件を満たす作業で 1 件の不備が出た**: 同梱 README 2 本(拡張 README = VSIX の説明ページ、
  `packaging/README.md` = リリース asset)が**0.9.0 の機能記述のまま**で、拡張 README は
  「signature help は次の WP」で終わっていた。リリースノートに書ける状態にする過程で気づき、
  6 機能の説明と制約、E12 の回避策を両方へ入れた(commit `745e8148b`)。
  **リリースノートは 1 回きりだが README は成果物の中に残る**ので、同じ内容の恒久版はそちら。
- 上記「E1 の未完部分 2 点」と「E1 の残りではない項目」は、**リリースノートの
  *Known behavior* 節に利用者向けの言葉で列挙済み**。本節の分類(E1 / B7 / E12 / E2)は
  開発側の整理なので、ノートには持ち込んでいない。
- 発行手順・照合結果は `00-PLAN.md` の 2026-07-27 の項。

→ https://github.com/matarillo/nemerle/releases/tag/release/1.2.635-preview.3
