# 58. WP-P2 実装ログ — documentHighlight(キャレット位置のシンボルの出現箇所)

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`

計画は `56-wp-p-plan.md`(WP-P1〜P5)。本書は **WP-P2 のみ**。

## 結論

LSP `textDocument/documentHighlight` を実装した。キャレット下のシンボルの出現箇所を
**現在のファイル内に限って**返し、宣言を `Write`、使用を `Read` として分類する。

- **経路は計画の第一候補ではなく (a) `BeginHighlightUsages` を採った。** 計画 §4 は
  (b) `GetGotoInfo(..., GotoKind.Usages)` を worker 経路で呼ぶ案を第一候補としていたが、
  **その API は公開インターフェイスに存在しない**(§設計-1)。実測ではなく型の可視性で決まった。
- **共有ソース(ncc / lib / macros / VsIntegration)は無改造** = Stage リビルド不要、
  Nemerle assembly version は `1.2.0.635` のまま。(b) を worker に載せるには `IIdeEngine` へ
  `BeginGetGotoInfo` を公開する共有ソース改修が要り、それは `56-*` §3-3 の停止条件に当たる。
  停止せずに済んだのは、(a) が公開されておりかつ worker 上で走るため。
- **(a) の結果はコールバック(`IIdeProject.SetHighlights`)で返り、しかも
  `request.MarkAsCompleted()` の後に来る**(`Engine-HighlightUsages.n`)。相関は「鍵で照合する」
  のではなく「**同時に 1 本しか飛ばさない**」ことで構造的に取った(§設計-2)。
- **写像は engine 非依存の純関数** `ProjectInfo/GotoMapping.cs` を拡張した
  (`ToDocumentHighlights`)+ unit test。新ファイルは作らず、`ToLocations` と範囲変換を共有している。
- **(a) と (b) が同じ集合を返すことを実測で固定した**。Sokoban の `SMap` は project 全体で
  **55 件**(WP-N2 の実測値)、うち **sokoban.n が 16 件**、**他 4 source に 39 件**。
  documentHighlight は **16 件を過不足なく**返し、references の sokoban.n 部分集合と
  **集合として一致**する(§検証)。つまり「ハイライトされた場所が rename される」は
  WP-P3 が別経路(b)を使っても成り立つ。
- **VSIX / 拡張の manifest 変更は不要**(確認済み)。document highlight は capability
  ネゴシエーションで vscode-languageclient が自動的に配線し、trigger も設定も持たない。
  `vscode-nemerle` 配下に `documentHighlight` / `occurrencesHighlight` 等の記述は **0 件**(grep 済み)。
- **版は 0.10.0 のまま据え置き**(`56-*` §3-2。0.10.0 は未公開)。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9、API は既存の範囲)
- 依存 package の追加は無し(NuGet / npm とも)
- 実 VS Code での確認は **未実施**(`56-*` §3-4 のとおり WP-P 系列の完了時にまとめて 1 回)

## 設計

### 1. 経路の決定 — (b) は「劣る」のではなく「無い」

計画 §4 は 2 経路を挙げ、(b) を第一候補としていた。実装に入って分かった確定事実:

**`IIdeEngine`(`Nemerle.Completion2/Engine/IEngine.n`)が公開しているのは同期版の
`GetGotoInfo` だけで、worker に載る `BeginGetGotoInfo` は公開されていない。** 後者は
`internal partial class Engine`(`Engine-GetGoToInfo.n:23`)のメンバーであり、
LspServer からは型ごと見えない。

同期版を worker 上の work item から呼ぶ案も**不可**である。`GetGotoInfo` の実体は

```
public GetGotoInfo(source, line, col, kind) : array[GotoInfo]
{
  def request = BeginGetGotoInfo(source, line, col, kind);
  _ = request.AsyncWaitHandle.WaitOne();
  request.GotoInfos
}
```

で、worker スレッドから呼べば **worker が自分自身の投入した work を待つデッドロック**になる。
`AsyncWorker` は単一スレッド(`AsyncWorker.n`)なので回避できない。

したがって (b) を「AsyncWorker スレッドで行う」という本 WP の必須条件のもとで実現するには、
`IIdeEngine` に `BeginGetGotoInfo` を追加する共有ソース改修が必要になる。これは
`56-*` §3-3 の停止条件(Stage リビルド → 版バンプ → release セット再生成)であり、
機能単体で決めてよい判断ではない。

一方 **(a) `BeginHighlightUsages` は `IEngine.n:47` で公開されており、engine 側で
`AsyncWorker.AddWork` 済み**(`Engine-HighlightUsages.n:23-28`)。よって

- 共有ソース無改造、
- engine 呼び出しは worker スレッド、

の両方を同時に満たす経路は **(a) だけ**である。計画の「実測で選ぶ」に対する答えは
「実測以前に選択肢が 1 つしか無かった」になる。

**(a) を採ることで失われるものは無い、ということも確認した。** 両者は最終的に同じ
`Project.FindUsages` に入る(`Project.Refactoring.n`)。違いは引数 `onlyThisFile` だけで、
これは**候補位置の字面スキャン**(`findPossibleUsages` が `code.OrdinalIndexOf(name, ...)` で
全 source を舐める部分)を現在のファイルに絞るだけであり、その後 (a) は
`Filter(goto => goto.Location.FileIndex == fileIndex)` を掛ける — サーバー側の
`ToDocumentHighlights` が掛けるのと同じ絞り込みである。現在のファイル内の集合が
両者で一致することは机上ではなく**実測で固定した**(§検証、Sokoban `SMap` 16/16)。
副次的に (a) の方が字面スキャンが 1 ファイル分で済むぶん安い。

### 2. コールバックの相関 — 鍵ではなく「同時に 1 本」で取る

(a) の弱点は計画 §4 が指摘したとおりで、実際にソースで確認した:

```
def highlight = project.HighlightUsages(fileIndex, req.Line, req.Column);
request.MarkAsCompleted();
//AsyncWorker.AddResponse(() => _callback.SetHighlights(request.Source, highlight));
_callback.SetHighlights(request.Source, highlight); // safe
```

- **完了は配達ではない。** `MarkAsCompleted()` が先に走るので、`AsyncRequest.IsCompleted` を
  見て返す待ち方(`EngineRequestBridge` も engine 自身の `WaitOne` も)は答えより先に戻り得る。
- **コールバックは `IIdeSource` しか運ばない。** 要求を識別する鍵が無いので、同一文書に対して
  2 本飛んでいると答えの取り違えが原理的に区別できない。documentHighlight は
  **キャレット移動のたびに飛ぶ**ので、これは理論上の話ではない。
- **キャンセルされた要求もコールバックを撃つ。** engine 側の work は `Stop` を見ない。

そこで**鍵で照合するのをやめ、鍵が不要な状態を作った**:

1. `GetDocumentHighlightsAsync` は往復の全体で `_highlightGate`(`SemaphoreSlim(1,1)`)を保持する。
   = **highlight 要求は常に高々 1 本**。したがって `SetHighlights` が届いた先の受け皿
   (`HighlightDelivery`)は一意に定まる。
2. 受け皿の `Targets` は **配達されるまで null**。engine の正当な答え「出現 0 件」(キャレットが
   シンボル上に無い)と「まだ来ていない」を混同しないため、空配列ではなく null で区別する。
3. **キャンセル/タイムアウトでも要求を捨てずに drain する**(`AwaitHighlightAsync`)。
   捨てて gate を返すと、遅れて来たコールバックが**次のキャレット位置の受け皿に入る**。
   drain することでその窓を閉じる。LSP の `CancellationToken` を待ちの中で見ないのはこのためで、
   呼び出し側は既に答えを捨てると決めているので、待ちは「掃除」であって「作業」ではない。
   worker は単一スレッドなので、drain 中の待ちは**どのみち後ろに積まれていた**時間である。
4. `IsCompleted` を観測した後は **250 ms だけ settle** して配達を待つ。両者は worker 上の
   隣り合う 2 文なので、この窓が埋めるのはスケジューリングのしゃっくりだけである。
   settle しても来なければ「engine が例外を投げた」= 唯一の「完了するが配達しない」経路と判断し、
   空を返す(`AsyncWorker.ThreadProc` は work が投げたときだけ自分で `MarkAsCompleted` する)。
5. 全体の上限は `EngineRequestBridge` と同じ **10 秒**。

**副産物: 平坦化がタダで worker 側に乗る。** `SetHighlights` は **worker スレッドが呼ぶ**ので、
`GotoInfo` → `NemerleGotoTarget` の変換(`FlattenGotoInfos`)をその中で行える。
`GotoInfo.FilePath` は `Location` のプロセス全体で共有される
`SCG.List[string]`(ファイル名表、worker が source 追加のたびに append する)を読むため、
WP-P1 の XmlDoc キャッシュと同じ「同期の無い engine 状態」である。WP-P1 は 2 ホップ目を
足してこれを worker に寄せたが、本 WP はコールバック自体が worker 上なので追加ホップが要らない。

### 3. `UsageType` → `DocumentHighlightKind`

| engine の `UsageType` | LSP | 判断 |
|---|---|---|
| `Definition` | `Write`(3) | LSP に Definition 種別が無い。宣言箇所を `Write` に写すのは主要な LSP サーバーの慣行で、テーマが「別格」に描くのもこれ |
| `GeneratedDefinition` | `Write` | 由来(生成か否か)は宣言かどうかを変えない |
| `Usage` / `GeneratedUsage` | `Read`(2) | |
| `ExternalDefinition` / `ExternalUsage` | **出さない** | 外部アセンブリのメンバーは**このファイルに source を持たない**。`FileIndex` の絞り込みでも落ちるが、意図を明示する位置に判定を置いた |

**`Write` は「代入」ではなく「宣言」を意味する、という点は正直に書いておく。**
engine の `Definition` は *binding occurrence* であって書き込みではない。
`Project.Refactoring.n` の `findLocalValueReferences` は `PExpr.Ref` の `TypedObject` が
`LocalValue` なら `Definition`、`TExpr.LocalRef` なら `Usage` を付ける。つまり
**`mutable` 変数への代入は `Usage` = `Read` として出る**。本物の書き込みを見分けるには
engine の usage 収集が持っていないデータフロー情報が要るので、畳まずに制約として記録する
(§既知の制約)。それでも「宣言だけ別色」は無いよりはるかに有用と判断した。

**実測: `Generated*` / `External*` は現状の engine では 1 件も生成されない。** repo 全体を
grep した結果、これらを付けて `GotoInfo` を作る箇所は無く、参照は
`Nemerle.Completion2/Tests/Heavy.Tests/Runner.n` の表示用 match だけだった。写像は
「将来 engine が出し始めたときに黙って read に化けない」ための防御であり、
**実データでは未検証**である。

**重複の畳み込み**: 同一範囲が 2 回来た場合(partial type、あるいは宣言が自分自身の使用として
再掲される場合)は 1 件に畳み、**`Write` が `Read` に負けない**ようにした。
engine の列挙順に依存して宣言の種別が揺れないようにするため。

### 4. mirror のズレ検出

`NemerleUsageType` は engine の `UsageType` を**名前と値ごと mirror** している
(`CompletionMapping` の `GlyphType`、`SemanticTokenMapping` の `ScanTokenColor` と同じ
トレードオフ: `ProjectInfo` に engine 参照を持ち込まない代わりに mirror のズレを別途検出する)。
`NemerleDocumentHighlightHandler` のコンストラクターで `Enum.GetNames<UsageType>()` と
突き合わせ、不一致を `window/logMessage`(Warning)で報告する。サーバーは
`(NemerleUsageType)(int)info.UsageType` とキャストするので、ズレると存在しない値になる。

### 5. 空返答とログ

出現 0 件・文書が未オープン・要求が `Cancelled` / `Stale` / `TimedOut` の場合は **null**
(空配列ではない)。**無言の null 経路は作らない**(WP-O5a / WP-P1 の教訓)ので、`ServerLog` に

- `nemerle document highlight skipped: <uri> is not an open document`
- `nemerle document highlight unavailable at <uri> L:C (engine request <Outcome>)`
- `nemerle document highlight empty at <uri> L:C (N ms)`
- `nemerle document highlight computed in N ms at <uri> L:C (M occurrence(s), W write / R read)`

を出す。raw LSP シナリオは 3 番目を実際に待って固定している。

## 実装ファイル

新規:

- `dotnet-port/LspServer/NemerleDocumentHighlightHandler.cs` — `DocumentHighlightHandlerBase` 実装、
  登録オプション、`UsageType` mirror のズレ検出、ログ。

変更:

- `dotnet-port/ProjectInfo/GotoMapping.cs` — `NemerleUsageType`(engine enum の mirror)、
  `NemerleDocumentHighlightKind` / `NemerleDocumentHighlight`、純関数 `ToDocumentHighlights`。
  `NemerleGotoTarget` の `bool IsDefinition` を `NemerleUsageType UsageType` に置き換え
  (`IsDefinition` は計算プロパティとして残す)。範囲変換は `ToLocations` と共有(`ToRange`)。
- `dotnet-port/LspServer/NemerleProject.cs` — `GetDocumentHighlightsAsync`、
  `HighlightDelivery` / `AwaitHighlightAsync` / `_highlightGate`、
  `SetHighlights`(空実装だった `IIdeProject` メンバーを実装)、
  `FlattenGotoInfos`(`GetGoto` と共有する平坦化として切り出し)。
- `dotnet-port/LspServer/Program.cs` — ハンドラー登録。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `GotoMappingTests` に documentHighlight の
  assertion を追加(既存の `IsDefinition:` 指定を `NemerleUsageType` に更新)。
- `dotnet-port/LspServer.IntegrationTest/Program.cs` — raw LSP シナリオ 3 本 + ヘルパー。
- `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs` — `documentHighlight` の client capability。

## 検証

すべて Windows 11 で実測。共有ソース無改造のため testsuite / stage 比較 / CLR4 スモークは対象外。

| ゲート | 結果 |
|---|---|
| `dotnet build -c Release LspServer` | 成功(0 警告 / 0 エラー) |
| `dotnet build -c Release LspServer.IntegrationTest` | 成功(0 警告 / 0 エラー) |
| raw LSP 統合(`Nemerle.LanguageServer.IntegrationTest`) | **40 シナリオ PASS**(既存 37 本 + 新規 3 本、回帰ゼロ) |
| `dotnet build -c Release ProjectInfo.Test` | 成功(0 警告 / 0 エラー) |
| `ProjectInfo.Test`(unit) | PASS(`GotoMappingTests` に 21 assertion 追加) |
| `ProjectInfo.Test -- --integration` | PASS(sample / SDK package 評価に無回帰) |
| `git diff --check` | クリーン(exit 0) |
| `npm run check-types` / `lint` / `test` | **実行せず**(manifest / TS 無変更。`vscode-nemerle` 配下に document highlight 関連の記述が 1 件も無いことを grep で確認) |
| `npm run test:integration`(実 VS Code) | **未実施**(WP-P 系列完了時にまとめて 1 回、`56-*` §3-4) |

実測値:

- warm `textDocument/documentHighlight` round-trip: **55 ms**(signature help の 62 ms と同水準。
  worker 1 ホップ + `AsyncWorker.ThreadProc` の `Thread.Sleep(10)` + bridge 相当のポーリング)。
- Sokoban `SMap`: references **55 件**(sokoban.n **16 件** / 他 4 source **39 件**)、
  documentHighlight **16 件**、両者は集合として一致。
- loose file の local `counter`: 宣言 1(`Write`)+ 使用 2(`Read`)。
  module メソッド `Twice`: 宣言 1(`Write`)+ 呼び出し 1(`Read`)。

### raw LSP シナリオが固定していること

**1 本目**(`Document highlight: a local and a method, declaration write vs use reads, and agreement with references`):
一時ディレクトリの loose file に、local `counter`(宣言 1 + 使用 2)と module メソッド
`Twice`(宣言 1 + 呼び出し 1)を置く。

1. **すべての範囲が識別子ちょうどを指す**(行を跨がない、幅が `"counter".Length`)。
2. **宣言が `Write`(3)、使用がすべて `Read`(2)**。
3. **使用起点と宣言起点が同一集合**(kind 込みで集合比較)。
4. **documentHighlight と references が同じ出現位置集合を返す**。両者は engine の
   別の入口(`BeginHighlightUsages` と `GetGotoInfo`)を通るので、これは**トートロジーではなく
   経路間の一致の実測**である。WP-P3 の「ハイライトされた場所が rename される」の根拠。
5. メソッドについても宣言 = `Write` / 呼び出し = `Read` を固定。

**2 本目**(`Document highlight: a cross-source type is highlighted only where it occurs in this file`):
`samples/Sokoban` を project として load し、`sokoban.n` の `SMap` 宣言位置を起点にする。

1. references が **55 件**、うち sokoban.n が **16 件**、**他ファイルに 39 件ある**ことを
   まず確認する(0 件ならこのシナリオは filter を証明できないので明示的に失敗させる)。
2. documentHighlight が **その 16 件と過不足なく一致**する。
   = **他ファイルの 39 件は出ない**(多すぎない)かつ **file filter が同一ファイル内の出現を
   巻き添えにしない**(少なすぎない)。
3. クラス宣言そのものが**唯一の `Write`** である。

**3 本目**(`Document highlight: a non-symbol position answers null and logs it; warm timing`):

1. warm round-trip を計測して出力する(assertion は「0 件ではない」のみ)。
2. **どの宣言にも属さない位置(末尾のコメント行)で `null`** が返り、かつ
   **`nemerle document highlight empty` が `window/logMessage` に出る**。「静かな null」を
   作らないという設計(§設計-5)を実際に固定している。

fixture は既存 samples(Sokoban)と loose file のみを使い、新しい sample は追加していない。

## 既知の制約 / 残課題

- **実 VS Code での確認は未実施**。raw LSP でプロトコルの形は固定したが、
  「キャレットを置くと該当箇所が縁取られる」ことは実機ゲート(`npm run test:integration`)で
  確認する必要がある。`56-*` §3-4 のとおり WP-P 系列の完了時にまとめて 1 回実施する。
- **`Write` は「宣言」であって「代入」ではない**(§設計-3)。`mutable` 変数への書き込みは
  `Read` として出る。engine の usage 収集がデータフローを持たないため、直すには engine 改修が要る
  = `56-*` §3-3 の停止条件。
- **宣言の中だがシンボル上ではない位置は「空」ではない。** engine の `GetActiveDecl` は
  その位置を囲む宣言を返すので、module の `{` や メンバー間の空行にキャレットを置くと
  **module 名がハイライトされる**(実測)。definition / references も同じ挙動であり、
  不合理な答えではないと判断してそのままにした。シナリオ 3 本目の「空」は
  **どの宣言にも属さない位置**で固定している。
- **`Generated*` / `External*` の写像は実データ未検証**(§設計-3)。現在の engine は
  これらを生成しない。
- **highlight 要求は同時に 1 本に直列化される**(§設計-2)。キャンセルされた要求も drain するため、
  キャレットを速く動かすと要求が数珠つなぎになる。warm 55 ms・worker が単一スレッドである
  ことを踏まえれば実害は無いと判断したが、実機で「ハイライトが遅れて追随する」感触が出るなら
  ここが原因である。
- **配達待ちの 250 ms settle には理屈上の穴が残る**(§設計-2)。`MarkAsCompleted()` と
  `SetHighlights` は worker 上の隣り合う 2 文なので、その間に 250 ms 以上プリエンプトされた場合に
  限り「例外で完了した」と誤判定して空を返す。誤った**内容**を返す経路ではない。
- **`GotoKind.UsagesInCurrentFile` の `//!!!`(`GetUsages` と同一実装)は現状維持**。
  `39-*` §7-5 は documentHighlight 実装時にこれを扱うことを想定していたが、
  本 WP の経路(a)はそもそも `GotoKind` を通らないため触っていない。LSP からの消費者は
  依然として無い。
- **`GetGoto`(definition / references)は今も LSP スレッドで同期呼び出しのまま**
  (`56-*` §3-1 の既知のハザード)。本 WP は意図的に触っていない。次項参照。
- **`ResolveCompletionDescription`(WP-M3)も引き続き LSP スレッドで XmlDoc を読む**
  (WP-P1 §既知の制約から持ち越し)。
- **mirror のズレ検出は警告するだけ**で、色落ち/誤分類そのものは止めない
  (`SemanticTokenMapping` の門番と同じ設計)。
- **拡張の manifest は無変更**。`editor.occurrencesHighlight` 等の既定値を `[nemerle]` に
  入れたくなった場合のみ manifest を触ることになる(サーバー変更は不要)。

## 再現コマンド

```powershell
dotnet build -c Release dotnet-port\LspServer\Nemerle.LanguageServer.csproj
dotnet build -c Release dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll
dotnet build -c Release dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj
dotnet exec dotnet-port\ProjectInfo.Test\bin\Release\net10.0\Nemerle.ProjectInfo.Test.dll
dotnet exec dotnet-port\ProjectInfo.Test\bin\Release\net10.0\Nemerle.ProjectInfo.Test.dll --integration
git diff --check
```

## 後続 WP への申し送り

### WP-P3(rename)へ — 計画 §3-1 の前提が崩れている

**`56-*` §3-1 の「WP-P3 の先頭で `GetGoto` を worker 側へ寄せる」は、現在の公開 API では
実行できない。** `IIdeEngine` に `BeginGetGotoInfo` が無く、同期版 `GetGotoInfo` は
worker から呼ぶとデッドロックする(§設計-1)。取り得るのは:

1. **`IIdeEngine` に `BeginGetGotoInfo` を公開する** — 共有ソース改修 = `56-*` §3-3 により
   **PO 判断**(Stage リビルド → 版バンプ → release セット再生成の連鎖)。
   `VsIntegration` 全体を先に grep する手順(`38-*` §5.6 / `39-*` §8)も要る。
   なお `Engine` 側には既に `BeginGetGotoInfo` が実装済みなので、変更はインターフェイスへの
   1 行追加(および legacy VS 統合側の実装確認)で済む見込み。
2. **`GetGoto` を現状の同期・LSP スレッド呼び出しのまま使う** — `53-*` §設計-2 の
   `NullReferenceException` ハザードを rename にも背負う。references が既にそうなっている。
3. **(a) `BeginHighlightUsages` を使う** — **rename には使えない**。single-file 固定なので
   cross-file の usage を取れない。

**この判断を WP-P3 の最初の設計項目として PO へ上げること。** 1 を選ぶ場合、
本 WP の `FlattenGotoInfos` はそのまま worker 側の work item から呼べる形になっている
(`GotoInfo` を受けて `NemerleGotoTarget[]` を返す static)ので、変更点は
「どこから呼ぶか」だけになる。

### WP-P3 が消費する usage コレクションの形

- `NemerleGotoTarget(FilePath, FileIndex, Line, Column, EndLine, EndColumn, UsageType)` —
  座標は **engine の 1-origin / 終端排他**、`FileIndex > 0` が in-workspace source、
  0 以下は metadata / 外部アセンブリ。`UsageType` は engine enum の mirror で、
  `IsDefinition` は計算プロパティ。
- `GotoMapping.ToLocations(targets, includeDeclaration)` が cross-file の LSP location 列
  (references / rename が欲しい形)、`GotoMapping.ToDocumentHighlights(targets, fileIndex)` が
  単一ファイルの view。**両者は同じ `ToRange` を通る**ので、rename の編集範囲と
  ハイライト範囲は同じ規則で決まる。
- **実測済みの前提**: highlight(経路 a)と references(経路 b)は現在のファイルについて
  同一集合(Sokoban `SMap` 16/16)。したがって rename が references の全件を書き換えれば、
  ユーザーがハイライトで見ていた範囲は必ず含まれる。
- **cross-project は依然として非対応**(`39-*` §7-2 / §7-4)。単一 project 内の rename のみ。

### そのほか

- **raw LSP シナリオは 40 本**になった。以降の WP はこの本数からの無回帰で数えること。
- `LspTestClient` の `initialize` capability に `documentHighlight` を足した。後続の機能も
  同じ場所に capability を足さないとハンドラーが登録されない(WP-P1 の申し送りどおり)。
- **`IIdeProject` のコールバック経路を使う型が 1 例目**になった(`SetHighlights`)。
  WP-P5(codeAction)は `AddUnimplementedMembers` / `AddOverrideMembers` という同じ形の
  コールバックを使う予定なので、本 WP の
  「gate で同時 1 本 → 受け皿 → drain して gate を返す」がそのまま雛形になる。
  ただし `56-*` §4 が警告している `Engine-FindUnimplementedMembers.n` の
  `AddResponse` 直後の `assert(false)` は別問題として先に実測すること。
- **engine 結果オブジェクトの読み出しを worker に載せる型は 3 例目**(WP-O5a `ColorizeRequest`、
  WP-P1 `MethodTipDescriptionRequest`、本 WP は `SetHighlights` がそもそも worker 上)。
