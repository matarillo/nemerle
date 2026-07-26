# 56. WP-P 計画 — エディター機能第2弾(バックログ E1 の残り)

作成日: 2026-07-26 / ブランチ: `wip/dotnet-port`

**本書はドラフト。PO 合意までは「合意済み」ではなく、`00-PLAN.md` への反映も合意後に行う。**

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

**既存の `GetGoto`(definition / references)は同じハザードを持ったまま**同期呼び出しで残っている
(`NemerleProject.cs:541` 付近、`lock (_engineOperations)` 内で `_engine.GetGotoInfo`)。rename が
同じ経路に載るため、**WP-P3 の先頭でこの経路を worker 側へ寄せる**(§7 の P3-a)。

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
| WP-P3 | rename(+ `GetGoto` の worker 経路化、WorkspaceEdit 基盤) | P2 と実装共有 | 中 |
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
- 文書: 本書 + 実装ログ `57`(P1)/ `58`(P2)/ `59`(P3)/ `60`(P4)/ `61`(P5)。
  完了時に `00-PLAN.md` の WP 表・作業ログと `36-*` §2.2 の E1 記述を更新(**PO 合意後**)。

## 7. 参照文書

- `36-prerelease-quality-plan.md`: §2.2 の E1 定義、§10 バックログ 2/3。
- `39-prerelease-wp-n2-log.md`: §5 references の完全性実測、§7-4 rename 前提の判定、
  §7-5 `UsagesInCurrentFile` の申し送り。
- `53-wp-o5-log.md`: engine のスレッド契約(§設計-2)、bridge の使い方、版の規約、
  raw LSP シナリオと probe の作り方。
- `47-wp-o-plan.md`: §5-3 評価先行方式。
