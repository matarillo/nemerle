# 59. WP-P3 実装ログ — rename / prepareRename

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`

計画は `56-wp-p-plan.md`(WP-P1〜P5)。本書は **WP-P3 のみ**。
WP-P1(signatureHelp)は `57-wp-p1-log.md`、WP-P2(documentHighlight)は `58-wp-p2-log.md`。

## 結論

LSP `textDocument/rename` と `textDocument/prepareRename` を実装した。編集集合は
**references / documentHighlight と同一の usage 収集**(`GetGotoInfo(..., GotoKind.Usages)`)から作る
ので、「エディターが光らせた場所が書き換わる場所」が構造的に保証される。

- **共有ソース(ncc / lib / macros / VsIntegration)は無改造** = Stage リビルド不要、
  Nemerle assembly version は `1.2.0.635` のまま。
- **`56-*` §3-1 の「WP-P3 の先頭で `GetGoto` を worker 経路へ寄せる」は不要だった**。同節の訂正
  (`459481cf9`)のとおり `GetGotoInfo` は `BeginGetGotoInfo` + `WaitOne()` で、型解決は元から
  worker 上で走る。本 WP は経路を変えていない。
- **拒否できることが機能の一部**(§設計-2)。宣言が engine workspace の外にある symbol、外部
  アセンブリの member、名前でない位置、識別子でない新名、この**ファイルの環境で**キーワードである
  新名、占有テキストが symbol 名と一致しない場合 — いずれも編集を作らずに理由付きで断る。
- **マクロが増やしたキーワードも弾く**。判定はハードコード表ではなく
  `CoreEnv.Keywords ∪ GetActiveEnv(...).Keywords`(WP-O5a と同じ環境)を **worker スレッドで**
  読む。`using Nemerle.Surround;` があるファイルでは `surroundwith` への rename が拒否され、
  その `using` を消すと同じ rename が通ることを raw LSP で固定した。
- **WorkspaceEdit の組み立ては WP-P5(codeAction)と共有する純関数**(`WorkspaceEditMapping`)。
  挿入(空範囲)同士は衝突扱いしない — 生成メンバーの挿入がまさにその形になるため。
- 版は **0.10.0 のまま**(`56-*` §3-2。0.10.0 は未公開)。拡張の manifest 変更は不要(rename は
  capability ネゴシエーションで配線され、`prepareProvider` はサーバーの登録オプションが宣言する)。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9、API は既存の範囲)
- 依存 package の追加は無し(NuGet / npm とも)
- 実 VS Code での確認は **未実施**(`56-*` §3-4 のとおり WP-P 系列の完了時にまとめて 1 回)

## 設計

### 1. 編集集合の出どころ

`NemerleProject.CollectUsages` が `GetGotoInfo(source, line, col, GotoKind.Usages)` を
`_engineOperations` の下で呼び、`FlattenGotoInfos` で `NemerleGotoTarget[]` にする。
**references が使っているのと同じ 1 本の経路**であり、WP-N2 §5.3 が完全性を実測した経路そのもの
(Sokoban `SMap` = 5 source にまたがる 55 usage、宣言起点と注釈起点で同一集合)。
documentHighlight は engine 側の別入口(`BeginHighlightUsages`)を通るが、WP-P2 §検証が
両者の一致を測っている。したがって 3 機能の「対象集合」は実測で結ばれている。

### 2. 拒否規則

すべて純関数 `RenameMapping` にあり、unit test が 1 件ずつ固定している。

| 状況 | 判定 | 理由 |
|---|---|---|
| engine が何も解決しない | `NoSymbol` | rename する対象が無い |
| navigable な location が 1 件も無い | `ExternalSymbol` | 書き換える source が無い |
| **in-workspace な宣言が無い** | `DeclarationOutsideWorkspace` | 使用箇所だけ書き換えて宣言を残す = ビルドを壊す。`39-*` §7-4 の cross-project 禁止を機械的に実装したもの |
| キャレットを含む occurrence がこのファイルに無い | `CaretNotOnSymbol` | 宣言の内側でも識別子から外れた位置では engine は**囲みの型**を返す(WP-P2 §実測)。highlight としては妥当でも rename としては破壊的 |
| 新名が識別子でない | `InvalidNewName` | lexer の規則(下記)に反する |
| 新名がこのファイルの環境でキーワード | `NewNameIsKeyword` | パースできないコードが生成される |
| occurrence の現在テキストが symbol 名と違う | `InconsistentOccurrences` | 収集結果とバッファがずれている。書き換えれば無関係な場所を壊す |

**キャレット判定の細部**: 終端は含む扱いにした(`foo|` = 識別子直後にキャレットを置く操作が
エディターでは普通)。複数の範囲が覆う場合は**最も狭いもの**を採る(識別子 vs 囲みの宣言)。

**`ExternalSymbol` と `DeclarationOutsideWorkspace` の実測上の関係**: BCL member
(`System.Console.WriteLine`)を起点にすると、engine の usages 収集は**このファイル内の呼び出し位置
だけ**を返し、source を持たない宣言 target は返さない。つまり usages 経路からは「BCL」と
「参照プロジェクト由来」を区別できない。**メッセージは両方を名指しする文面にした**
(「このプロジェクトで宣言されていません(参照プロジェクトまたはアセンブリ由来)」)。
`ExternalSymbol` が出るのは navigable な location が 1 件も無いときだけ。

### 3. 新名の検査

- 識別子規則は `ncc/parsing/Lexer.n` の `IsIdBeginning`(letter または `_`)と `get_id` の継続
  (letter / digit / `_` / `'`)を **ProjectInfo 側に mirror** した。ProjectInfo は compiler 参照を
  持たない方針(`CompletionMapping` の `GlyphType`、`SemanticTokenMapping` の `ScanTokenColor` と
  同じトレードオフ)。
- キーワード集合は **worker スレッドで** 読む: `KeywordProbeRequest`(`AsyncRequestType.EmptyRequest`
  を借りる。`ColorizeRequest` / WP-P1 の平坦化と同じ)が `ManagerClass.CoreEnv.Keywords` と
  `GetActiveEnv(fileIndex, line).Field0.Keywords` を `HashSet[string]` にまとめて返す。
  `GetActiveEnv` は宣言ツリーを walk する = engine 状態なので LSP スレッドから触らない。
- **probe が失敗したときは `LexerBase.BaseKeywords`(static、スレッド安全)だけで検査を続ける**。
  これが通してしまうのは「マクロ由来キーワードを名前に選んだ」場合だけで、次のビルドで
  コンパイラーが即座に報告する。engine の一時的な状態を理由に正当な rename を拒むほうが
  害が大きいと判断した。フォールバックは `window/logMessage` に出す。

### 4. 占有テキストの照合

編集を作る前に、**各 occurrence の現在のソーステキストが symbol 名と一致するか**を確認する。
テキストはサーバー自身のバッファ(`InMemoryNemerleSource.GetRegion`、engine 状態ではなく
ドキュメントのテキスト状態なので worker 不要)から読む。プロジェクトの source はエディターが
開いていなくても document として登録されているため、cross-source rename でも全件読める
(Sokoban で 55/55 が読めることを実測)。

**バッファを持たないファイルの occurrence は「null テキスト = 拒否」**にした。見ていないファイルを
書き換える許可にはしない、という側に倒している。usage 収集は AST 由来なので通常この検査は落ちない
— 落ちるとしたらバッファが収集より先に進んだときで、そのときこそ書き換えてはいけない。

### 5. WorkspaceEdit の組み立て(WP-P5 との継ぎ目)

`WorkspaceEditMapping.Build(IEnumerable[NemerleTextEdit])` が URI ごとに束ね、位置順に並べ、

- **完全に同じ範囲・同じ置換テキスト**は 1 件に畳む(engine が同じ location を 2 回報告する
  ケース。partial type が既知 — `GotoMapping.ToLocations` と同じ理由)。
- **同じ範囲で置換テキストが違う**のは矛盾なので conflict(修復しない。どちらを捨てても
  ユーザーが頼んでいない変更になる)。
- **範囲が重なる**のも conflict。LSP は 1 ドキュメントの `TextEdit[]` を**元のテキスト**に対して
  適用する仕様で、重なりを禁じている。
- **接する範囲**(前の終端 == 次の始端)と**同一点への挿入同士**は衝突ではない。後者は
  **WP-P5 が生成メンバーを挿入するときの形**なので、意図的に許可して unit test で固定した。

conflict は握りつぶさず、理由文字列を付けて呼び出し側へ返す(rename は拒否に変換する)。

### 6. ハンドラーが 2 本ある理由

OmniSharp 0.19.9 は `RenameHandlerBase` と `PrepareRenameHandlerBase` を別基底として持つ
(1 クラスで両方は継承できない)。したがって `NemerleRenameHandler` と
`NemerlePrepareRenameHandler` の 2 本にし、`Program.cs` で両方登録した。どちらも
`RenameRegistrationOptions { PrepareProvider = true }` を返す。判断ロジックは共有の
`RenameMapping` にあるので二重化していない。

### 7. 拒否をクライアントへ返す方法(実測)

**`OmniSharp.Extensions.JsonRpc.Server.RequestFailedException` は使えない**: 投げると
クライアントには `"Request Cancelled"` という固定メッセージが届き、**こちらの理由文が消える**
(raw LSP で実測)。`RpcErrorException(-32602, null, reason)`(InvalidParams)に切り替えたところ
理由文がそのまま `error.message` に載った。VS Code はこれを通知として表示する。

`prepareRename` 側は **null を返す**(= エディターは入力ボックスを開かない)。「名前を入れさせて
から失敗する」より前に断るほうが良い。どちらの経路も `window/logMessage` に理由を残す
(Output だけがユーザーから見える診断面 — WP-O5a §設計-3 / WP-P1 §設計-4)。

**サーバーはファイルを書かない**。返すのは `WorkspaceEdit` だけで、適用はクライアントの責任。

## 実装ファイル

新規:

- `dotnet-port/ProjectInfo/WorkspaceEditMapping.cs` — `NemerleTextEdit` / `NemerleDocumentEdits` /
  `NemerleWorkspaceEdit` / `NemerleWorkspaceEditResult`、`Build`(束ね・整列・重複畳み・conflict 検出)。
  **WP-P5 と共有**。
- `dotnet-port/ProjectInfo/RenameMapping.cs` — `NemerleRenameRefusal` / `NemerleRenamePreparation` /
  `NemerleRenameOccurrence`、`Prepare` / `ValidateNewName` / `CheckOccurrences` /
  `ToWorkspaceEdit` / `Describe`。
- `dotnet-port/LspServer/NemerleRenameHandler.cs` — `NemerleRenameHandler` と
  `NemerlePrepareRenameHandler`。

変更:

- `dotnet-port/LspServer/NemerleProject.cs` — `EngineRenameResult`、`CollectUsages` /
  `PrepareRename` / `GetRenameEditsAsync` / `ReadOccurrences` / `ReadRegion` /
  `GetContextKeywordsAsync` / `KeywordProbeRequest` / `BeginProbeKeywords` / `RunProbeKeywords`。
  `FlattenGotoInfos` の doc コメントから、撤回済みの「WP-P3 で worker 化する」記述を除去。
- `dotnet-port/LspServer/Program.cs` — ハンドラー 2 本の登録。
- `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs` — `rename` capability(`prepareSupport`)。
- `dotnet-port/LspServer.IntegrationTest/Program.cs` — シナリオ 4 本と rename ヘルパー。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `RenameMappingTests`。

## 検証

すべて Windows 11 で実測。共有ソース無改造のため testsuite / stage 比較 / CLR4 スモークは対象外。

| ゲート | 結果 |
|---|---|
| `dotnet build` LspServer / IntegrationTest / ProjectInfo.Test | 成功、警告 0 / エラー 0 |
| raw LSP 統合 | **44 シナリオ PASS**(既存 40 + 新規 4、回帰 0) |
| `ProjectInfo.Test`(unit) | PASS(`RenameMappingTests` 追加、40 assertion) |
| `ProjectInfo.Test -- --integration` | PASS |
| `git diff --check` | クリーン |
| npm | 未実行 — 拡張側は無変更(rename は capability ネゴシエーション。`vscode-nemerle` に rename の記述は無し) |

### raw LSP シナリオが固定していること

**1 本目**(`Rename: a local, prepareRename's range, and edits equal to the references set`):
`prepareRename` がキャレット位置の識別子ちょうど(幅 = 名前の長さ)を返すこと、rename が
1 ドキュメント 3 編集を返し、**その集合が同じ位置の references の集合と完全一致**すること、
宣言も含むこと、各編集の範囲幅が識別子幅であること、`nemerle rename computed` がログに出ること、
**宣言起点と使用起点で同じ編集集合**になること。

**2 本目**(`... a cross-source type rewrites every source, ordered and non-overlapping`):
`samples/Sokoban` を project として load し、`SMap` の rename が **5 ドキュメント 55 編集**、
**references の 55 件と集合として完全一致**すること(WP-N2 §5.3 の実測値と一致)、
各ドキュメント内の編集が位置順で重なっていないこと。

**3 本目**(`... refusals (external member, non-symbol position, invalid name, keyword)`):
BCL member で `prepareRename` が null + `nemerle rename not offered` ログ、コメント行でも null、
`1counter`(識別子でない)と `def`(基底キーワード)が理由付きで拒否されること、
**拒否のあとで同じ local が正常に rename できる**(拒否が状態を壊していない)こと。

**4 本目**(`... a macro-introduced keyword is refused, and allowed once its using is gone`):
`using Nemerle.Surround;` があるとき `surroundwith` への rename が「キーワード」として拒否され、
**その `using` を消すと同じ rename が通る**こと。= キーワード表を焼き込んでおらず、
ファイルの環境を実際に読んでいる証拠(WP-O5a の semantic tokens が同じ語で示した差別化と同じ根拠)。

### 実測

- local(3 occurrence)の rename: 1 ドキュメント 3 編集。
- Sokoban `SMap`: **5 ドキュメント 55 編集**、references 55 件と一致。シナリオ全体で 3.5 秒
  (project load + 型付け待ちを含む)。

## 既知の制約 / 残課題

- **cross-project rename は無効のまま**(`39-*` §7-4 の判定を引き継いだ)。参照プロジェクト由来の
  symbol は宣言が engine workspace に無いので拒否される。解除は multi-project workspace(E2)側の
  設計判断。
- **usages 経路では BCL member と参照プロジェクト由来を区別できない**(§設計-2)。メッセージは
  両方を名指しする文面にしてある。区別が必要になったら `GotoKind.Definition` を併用して
  `ExternalOnly` を見る手がある(definition 経路はその情報を持つ)。
- **マクロ展開でのみ生成される位置は対象外**(`39-*` §7-3 の理論上の caveat をそのまま引き継ぐ)。
  parsed tree に現れない位置は usage 収集に入らない。実測では Sokoban で欠落は観測されていない。
- **キーワード probe が失敗したときは基底キーワードのみで検査する**(§設計-3)。マクロ由来
  キーワードへの rename を通し得るが、コンパイラーが即座に報告する範囲の失敗にとどまる。
  ログに `using the base keyword set only` が出るので判別できる。
- **`GetGotoInfo`(同期版)は LSP スレッドをブロックし、キャンセルできない**(`56-*` §3-1 の
  残る性質 1)。rename は元から references と同じ経路なので条件は同じ。非同期化には
  `BeginGetGotoInfo` を `IIdeEngine` に載せる = 共有ソース改修が要る。
- **`GotoInfo` の遅延メンバー(`FilePath`)の読み出しは LSP スレッドで起きる**(同 性質 2)。
  definition / references と同じ既存の形で、本 WP で変えていない。
- **実 VS Code での確認は未実施**。raw LSP は `WorkspaceEdit` の中身までしか固定できず、
  「エディターが実際に 5 ファイルへ適用して保存する」経路は手動ゲートとして残る
  (`56-*` §3-4 のとおり系列完了時)。
- **サーバーはファイルを書かない**ので、テストがサンプルのソースを破壊することはない
  (シナリオは編集を計算して検証するだけ)。
- **`prepareRename` は placeholder を返さない**(range のみ)。VS Code は range のテキストを
  既定値に使うので実用上の差は無い。

## 後続 WP への申し送り

- **WP-P5(codeAction)が使う継ぎ目**: `WorkspaceEditMapping.Build` は
  「`NemerleTextEdit` の列 → 検証済み `WorkspaceEdit`」であり、**挿入(空範囲)を第一級に扱う**。
  同一点への複数挿入は衝突ではないので、未実装メンバーを 1 個ずつ別編集として出してよい。
  conflict は文字列で返るので、そのまま拒否理由に使える。
- **`NemerleProject.CollectUsages` / `ReadRegion`** は codeAction 側でも使える形にしてある
  (前者は usage 収集、後者はバッファからの範囲テキスト読み出し)。
- **worker 上で engine を読む型はこれで 3 例目**(`ColorizeRequest` / WP-P1 の平坦化 /
  本 WP の `KeywordProbeRequest`)。engine 状態に触る読み出しは `EmptyRequest` を借りて
  worker へ、が既定と考えてよい。
- **OmniSharp の拒否の返し方**: `RequestFailedException` はメッセージが潰れる(§設計-7)。
  理由文を届けるなら `RpcErrorException(code, null, message)`。
- raw LSP スイートは **44 シナリオ**になった。以降の WP はこの本数からの無回帰で数えること。
- `LspTestClient` の `initialize` capability に `rename`(`prepareSupport`)を追加した。
