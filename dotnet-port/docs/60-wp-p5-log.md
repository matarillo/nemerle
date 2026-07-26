# 60. WP-P5 実装ログ — codeAction(未実装インターフェイスメンバーの生成)

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`

計画は `56-wp-p-plan.md`(WP-P1〜P5)。本書は **WP-P5 のみ**。
**文書番号は作成順**であり WP 番号順ではない(56 §6 の予約を上書き): 57 = P1、58 = P2、
59 = P3、**60 = P5(本書)**、61 = P4(formatting、最後に実施)。順序の理由は 56 §5 のとおり
「確実に価値が出る 4 機能を先に確定させ、no-go を含む formatting の判断を後ろに置く」。

## 結論

LSP `textDocument/codeAction` を実装した。提供するのは **「未実装インターフェイスメンバーの生成」
1 種**。生成されるソースは engine 自身の `InterfaceMemberImplSourceGenerator`(public helper
`Utils.GenerateMemberImplementation` 経由 — legacy VS の "implement members" ダイアログが使うのと
同じ経路)で、この移植で発明した書式ではない。

- **共有ソース(ncc / lib / macros / VsIntegration)は無改造** = Stage リビルド不要、
  Nemerle assembly version は `1.2.0.635` のまま。
- **`56-*` §4 が実装前の実測を要求していた `assert(false)` は、結論として問題にならない**
  (§設計-1)。`Engine-FindUnimplementedMembers.n` は答えを `AsyncWorker.AddResponse` に載せた
  **直後に `assert(false)` を実行**するが、`AsyncWorker.ThreadProc` が例外を握って
  `MarkAsCompleted()` するため、**レスポンスは生き残る**。raw LSP で実測済み。
  したがって **PO 判断(共有ソース改修)の必要は生じなかった**。
- **受け入れ基準は「生成テキストが正しそうに見えること」ではなく「適用したらエラーが消えること」**
  にした(§検証)。この基準のおかげで、**プロパティが accessor メソッドとして生成される**という
  実害のある欠陥を実装中に検出できた(§設計-3)。
- **override 生成は本 WP では出さない**(§設計-4、意図的なスコープ縮小)。engine が返す
  override 候補は plain な class では `System.Object` の virtual 4 個で、それを
  「Override 4 members of Object」という 1 アクションとして全 class 宣言に出すのは害。
  まともにやるなら member ごとのアクション + 専用 fixture が要るので、**未テストで出荷せず先送り**。
- 版は **0.10.0 のまま**(`56-*` §3-2)。拡張の manifest 変更は不要。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9、API は既存の範囲)
- 依存 package の追加は無し(NuGet / npm とも)
- 実 VS Code での確認は **未実施**(`56-*` §3-4 のとおり WP-P 系列の完了時にまとめて 1 回)

## 設計

### 1. コールバック経路と `assert(false)`(実測)

`IIdeEngine.BeginFindUnimplementedMembers(source, line, col)` は worker に載る。engine 側は

```
AsyncWorker.AddResponse(() => _callback.AddUnimplementedMembers(...));
assert(false);
```

を実行する(upstream の残骸)。`AsyncWorker.ThreadProc` は work item の例外を握って
`MarkAsCompleted()` するので(`Async/AsyncWorker.n:117-125`)、**キューに載ったレスポンスは失われない**。
raw LSP シナリオでアクションが実際に出ることを確認した = 机上の読みではなく実測。

**ただしレスポンスは worker ではなく response pump で配送される**。`AddResponse` は
`_responseQueue` に積むだけで、実際に走らせるのは本クラスの `PumpResponsesAsync`
(10 ms ポーリングで `AsyncWorker.DispatchResponses()`)。したがって:

- **コールバックでは engine オブジェクトを一切読まない。** `IGrouping[FixedType.Class, IMember]` を
  **列挙することすら engine 仕事**になる — grouping のキーは `FixedType.Class` で、その等価比較は
  型ソルバーを通る。参照を保存するだけにした。
- 読み取りと生成は **worker 上の 2 ホップ目**(`GenerateMembersRequest`、`EmptyRequest` を借りる)。
  戻すのは plain な文字列だけ。WP-P1 の平坦化と同じ形で、これで worker 経由の読み出しは 4 例目。

**相関の取り方**は WP-P2 の highlight mailbox と同じ構造的手法(gate で in-flight を 1 本に保つ)。
ただし **settle 窓は 750 ms と長め**にした: highlight のコールバックは worker が直接呼ぶのに対し、
こちらは pump の 10 ms ポーリングを挟むので「完了 → 配送」の間隔が原理的に開く。

### 2. 挿入位置

`TypeBuilder.AstParts` から **このファイルにある部分**(partial 対応)の `Location` を取り、
その `EndLine` / `EndColumn` を使う。engine の Location は 1 始まり・終端排他なので、
型本体の閉じ波括弧は `(EndLine, EndColumn - 1)` にある。**その直前に挿入**する。

挿入テキストの組み立ては純関数 `CodeActionMapping`(unit test 付き):

- **再インデント必須**: engine の `SourceGenerator` は `_indentSize = 0` から始まり、その field は
  `protected` なので外から設定できない = 生成物は必ず左寄せで返る。挿入先の行のインデントから
  1 段深いインデントを全行に付ける(空白/タブは挿入先の流儀に合わせる。手がかりが無ければ
  スペース 2 = 本リポジトリの `.n` の流儀)。
- **挿入点の手前が空白だけでなければ改行から始める**(`class Foo { }` のような 1 行宣言)。
- **挿入点の手前にあったテキスト(閉じ波括弧のインデント)を末尾に戻す**ので、波括弧の位置は動かない。
- 生成物末尾の空行は落とす(アクションのたびに閉じ波括弧が下へずれるのを防ぐ)。

### 3. プロパティを accessor として生成しない

**実装中に実測で見つかった欠陥**: `TypeBuilder.UnimplementedMembers` はプロパティを
**accessor メソッド**(`get_Name`)として持つ。素直に生成すると

```
public get_Name() : string{ throw System.NotImplementedException() }
```

となり、**コンパイルは通るがプロパティを実装していない** = アクションが直すと約束したエラーが
残る。accessor を所有プロパティへ引き戻し(インターフェイス側の `IProperty` を
`GetGetter()` / `GetSetter()` の**参照同一性**で探す。`get_` 接頭辞の文字列剥がしではないので、
たまたま名前が accessor 風のメソッドを誤認しない)、プロパティとして 1 回だけ生成する:

```
public Name : string
{
	get{ throw System.NotImplementedException() }
}
```

この欠陥は「適用後にエラーが 0 件」という受け入れ基準でのみ検出できる。生成テキストを
目視・部分文字列で確認するだけの基準なら通ってしまっていた。

### 4. override 生成を出さない判断

`BeginFindMethodsToOverride` 経路は配線としては同じ形で動く(コールバックも届く)。**出さないのは
UX の判断**: `Project.Relocation.n` の `FindMethodsToOverride` は base type の virtual/abstract を
すべて返すので、base を持たない普通の class でも `System.Object` の 4 個
(`Equals` / `GetHashCode` / `ToString` / `Finalize`)が候補になる。実測でも
`Override 4 members of Object` という 1 アクションが出た。これを全 class 宣言の電球に出すのは
ノイズで、有用にするには **member ごとのアクション**にするか picker が要る(legacy VS はダイアログ)。
**専用 fixture 込みで別途やる価値はあるが、未テストのまま出荷しない**ほうを選んだ。
`IIdeProject.AddOverrideMembers` は理由をコメントに書いた no-op として残してある。

### 5. LSP 面

- `CodeActionKind.QuickFix` で登録(型が今まさにコンパイルできない状態を直すので refactor ではなく
  quick fix の電球が妥当)。`ResolveProvider = false` — 各アクションは edit を最初から持つ。
- 対象位置は `CodeActionParams.Range.Start`(キャレット、または選択の先頭)。
- edit の組み立ては **WP-P3 が作った `WorkspaceEditMapping` を経由しない**単一挿入なので、
  ハンドラーが直接 1 件の `TextEdit` を包む。複数インターフェイスがあれば**アクションが複数**になり、
  それぞれが同じ点に挿入する — `WorkspaceEditMapping` が「同一点への挿入は衝突ではない」と
  決めてあるのは、これらを 1 つの edit にまとめたくなった場合のため(59 §設計-5)。
- 空返答も必ずログする(`nemerle code actions: none ...`)。無言の空返答は Output から
  「クライアントが要求していない」と区別できない(WP-O5a §設計-3 以来の方針)。

## 実装ファイル

新規:

- `dotnet-port/ProjectInfo/CodeActionMapping.cs` — `NemerleMemberGeneration` /
  `NemerleInsertionPoint` / `NemerleCodeAction`、`ToCodeAction` / `Reindent` / `IndentFor`。
- `dotnet-port/LspServer/NemerleCodeActionHandler.cs` — `CodeActionHandlerBase` 実装。

変更:

- `dotnet-port/LspServer/NemerleProject.cs` — `GetCodeActionsAsync` / `SuggestMembersAsync` /
  `AwaitMemberDeliveryAsync` / `MemberSuggestionDelivery` / `MemberSuggestion` /
  `GenerateMembersRequest` / `BeginGenerateMembers` / `RunGenerateMembers` /
  `GenerateImplementations` / `FindOwningProperty` / `AppendActions`、
  `AddUnimplementedMembers` の実装(`AddOverrideMembers` は理由付き no-op)。
- `dotnet-port/LspServer/Program.cs` — ハンドラー登録。
- `dotnet-port/LspServer/NemerleRenameHandler.cs` — WP-P3 の `RpcErrorException` 呼び出しに
  残っていた nullable 警告(CS8625)を解消(`InvalidParams` 定数 + `null!` + 理由コメント)。
- `dotnet-port/LspServer.IntegrationTest/{LspTestClient.cs,Program.cs}` — `codeAction` capability、
  シナリオ 2 本とヘルパー(`CodeActionsAsync` / `ApplyEdit`)。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `CodeActionMappingTests`。

## 検証

すべて Windows 11 で実測。共有ソース無改造のため testsuite / stage 比較 / CLR4 スモークは対象外。

| ゲート | 結果 |
|---|---|
| `dotnet build` LspServer / IntegrationTest / ProjectInfo.Test | 成功、**警告 0 / エラー 0**(WP-P3 が残していた CS8625 も解消) |
| raw LSP 統合 | **46 シナリオ PASS**(既存 44 + 新規 2、回帰 0) |
| `ProjectInfo.Test`(unit) | PASS(`CodeActionMappingTests` 追加) |
| `ProjectInfo.Test -- --integration` | PASS |
| `git diff --check` | クリーン |
| npm | 未実行 — 拡張側は無変更(code action は capability ネゴシエーション) |

### raw LSP シナリオが固定していること

**1 本目**(`Code action: implement an interface's missing members, and the error is gone after
applying`)— 本 WP の核心:

1. インターフェイスを実装していない class の宣言位置で **`Implement IWidget (2 members)`** が出る
   (= `assert(false)` を越えてレスポンスが届いている証拠。ここが落ちるなら共有ソース改修の話になる)。
2. 生成テキストが `public Name : string`(**accessor ではなくプロパティ**)/ `public Draw() : void` /
   `NotImplementedException` を含む。
3. **その edit をクライアントと同じ手順で適用し、`didChange` 後の診断が error 0 件**。
   生成物が実際にコンパイルできることをサーバー自身の型付けで確かめている。

**2 本目**(`Code action: nothing to offer where nothing is missing`):
メソッド本体の位置ではアクション 0 件 + `nemerle code actions: none` ログ、
**すでに実装済みの class では `Implement ...` が出ない**こと。

### 実測

- 生成 + 適用まで含めたシナリオ全体で 2.2 秒(engine の初回ビルドを含む)。
- engine が返した override 候補(参考値): 素の class で `System.Object` の 4 member。

## 既知の制約 / 残課題

- **override 生成は未提供**(§設計-4)。配線は動くことを確認済みなので、着手するなら
  「member ごとのアクション + base class を持つ fixture」から。
- **生成されるプロパティ本体のインデントに engine のタブが混じる**(`get{ ... }` の行)。
  engine の `SourceGenerator` の出力そのままで、再インデントは行頭にしか効かない。
  気になるなら `Reindent` にタブ→スペース正規化を足せば済むが、engine の書式を勝手に
  変えないほうを選んだ。
- **1 アクション = 1 インターフェイス**。複数インターフェイスが未実装なら複数のアクションが出る。
  「全部まとめて実装」は用意していない(まとめるなら `WorkspaceEditMapping` で 1 edit に束ねられる)。
- **診断との連動は無し**。`CodeActionParams.context.diagnostics` は見ておらず、キャレット位置の型を
  engine に聞くだけ。したがって electric な「エラー行の電球」ではなく「型宣言のどこかにキャレットを
  置けば出る」挙動になる。
- **partial type は「このファイルにある部分」に挿入する**。別ファイルの部分には入れない。
- **実 VS Code での確認は未実施**(系列完了時の手動ゲート)。raw LSP は「クライアントが適用したら
  こうなる」までを固定するが、電球 UI に出るか自体は実機確認事項。
- **`assert(false)` に依存している**という事実は残る。upstream がここを「例外を投げない」形に
  直すぶんには問題ないが、**レスポンスを積むのをやめる**方向に変えると本機能は静かに死ぬ。
  シナリオ 1 本目がその番人になる。

## 後続 WP への申し送り

- 残るは **WP-P4(formatting)** のみ。`56-*` §4 の評価先行方式(実サンプルへの適用 diff →
  go / 部分 go / no-go の判断を PO へ)をそのまま実施すること。文書番号は **61**。
- **worker 上で engine を読む型はこれで 4 例目**(`ColorizeRequest` / P1 の平坦化 /
  P3 の keyword probe / 本 WP の生成)。`EmptyRequest` を借りる形が定着した。
- **`IIdeProject` コールバック経由の答えは 2 例目**(P2 の `SetHighlights` = worker 直接、
  本 WP の `AddUnimplementedMembers` = response pump 経由)。**配送スレッドが違う**ので、
  新しいコールバックを使うときは engine の該当コードを読んで `AddResponse` かどうかを確かめること。
- raw LSP スイートは **46 シナリオ**になった。
- `LspTestClient` の `initialize` capability に `codeAction`(`codeActionLiteralSupport`)を追加した。
