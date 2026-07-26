# 57. WP-P1 実装ログ — signatureHelp(メソッドのシグネチャヒント)

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`

計画は `56-wp-p-plan.md`(WP-P1〜P5)。本書は **WP-P1 のみ**。

## 結論

LSP `textDocument/signatureHelp` を実装した。呼び出しの括弧の中にキャレットがあるとき、
IDE engine の `BeginGetMethodTipInfo` が返す `MethodTipInfo` を LSP の
`SignatureInformation` / `ParameterInformation` / `activeSignature` / `activeParameter` に写像する。

- **共有ソース(ncc / lib / macros / VsIntegration)は無改造** = Stage リビルド不要、
  Nemerle assembly version は `1.2.0.635` のまま。engine 側の `BeginGetMethodTipInfo` /
  `MethodTipInfo` は既に public で core ビルドに含まれており、手を入れる必要はなかった。
- 写像は engine 非依存の純関数 `ProjectInfo/SignatureHelpMapping.cs` + unit test
  (`HoverMarkup` / `CompletionMapping` / `SemanticTokenMapping` と同じ前例)。
- **シグネチャのラベルはサーバー側で組み立てる**。engine は整形済みの署名文字列を持たず、
  部品(名前・戻り型・パラメーターごとの `name : type`)しか出さないため。組み立てるからには
  **`ParameterInformation.label` は必ず `[start, end)` のオフセット**で返す(§設計-1)。
- **engine 呼び出しは AsyncWorker スレッド上で 2 ホップ**行う(§設計-2)。tip の計算は
  `BeginGetMethodTipInfo` が既に worker に載せるが、**`MethodTipInfo` は engine 状態への遅延ビュー**
  であり、その読み出し(XmlDoc)を LSP スレッドから行うと共有可変状態への競合になる。
  そこで平坦化も worker へ投げ返す。
- **`HasTip == false` / overload 0 件は null を返す**(空コンテナではない)。空返答は VS Code が
  空のポップアップを開いたままにする。空返答は必ず `window/logMessage` に出す(§設計-4)。
- **VSIX / 拡張の manifest 変更は不要**(確認済み)。signature help は capability ネゴシエーションで
  vscode-languageclient が自動的に配線し、trigger characters はサーバーが登録する。
  `vscode-nemerle` の TS / `package.json` に signature 関連の記述は 1 件も無い(grep 済み)。
- **版は 0.10.0 のまま据え置き**(`56-*` §3-2。0.10.0 は未公開)。

## 環境

- Windows 11 / PowerShell、.NET SDK `10.0.301`
- engine / server: 既存 `dist/ncc`(`1.2.0.635`)+ `LspServer`(OmniSharp 0.19.9、API は既存の範囲)
- 依存 package の追加は無し(NuGet / npm とも)
- 実 VS Code での確認は **未実施**(`56-*` §3-4 のとおり WP-P 系列の完了時にまとめて 1 回)

## 設計

### 1. ラベルの組み立てとパラメーターのアンカー

**engine は整形済みの署名文字列を持たない。** `MethodTipInfo` が出すのは

| API | 内容 |
|---|---|
| `GetName(i)` | メソッド名。**コンストラクターは宣言型の名前**(`OverloadsMethodTipInfo.GetName`) |
| `GetType(i)` | 戻り型(`IMethod.ReturnType.ToString()`) |
| `GetDescription(i)` | XmlDoc の `<summary>`(無ければ空文字列) |
| `GetParameterCount(i)` | パラメーター数(拡張メソッドなら第 1 引数を除く) |
| `GetParameterInfo(i, p)` | `(name, display, doc)`。`display` は既に `name : type` の Nemerle 記法 |

したがってラベルは本 WP で組む。形は **`Name(p0, p1) : ReturnType`**、各 `pi` は engine の
`display` をそのまま使う = **Nemerle の宣言記法**で、hover が同じメンバーに対して出す形と一致する。
パラメーター 0 個なら `Name() : ReturnType`、戻り型を engine が名前付けできなかった場合は
`" : "` ごと落とす(空の型名を出さない)。

**`ParameterInformation.label` はオフセット形式で返す。** LSP は「シグネチャラベル中の部分文字列」と
「`[start, end)` のオフセット対」の 2 形式を認めるが、**部分文字列形式は肝心なところで曖昧**である:
`Fold(f : int -> int, x : int) : int` のように同じ字面が複数回現れるラベルでは、最初の一致を採る
クライアントは**違う位置を太字にする**。ラベルを自分で組む以上、オフセットは組み立ての最中に
ただで分かるので常にそれを出す。文字列形式は **`labelOffsetSupport` を宣言しないクライアント向けの
フォールバック**としてのみ残した(登録時に 1 回ネゴシエートする)。

**正規化**: 全ての文字列は空白の連続を 1 個の空白に畳んでから使う。XmlDoc の summary は engine の
`XmlDocReader` がテキストノードを連結して前後を trim するだけなので、**元ソースの改行とインデントが
そのまま入っている**。ポップアップは 1 行で読めなければ意味が無く、また複数行ラベルは
パラメーターのオフセットがエディターの描画とずれる原因になる。

### 2. スレッド境界 — worker へ 2 ホップ

`53-wp-o5-log.md` §設計-2 の確定事項(engine は単一スレッド契約、`_engineOperations` は worker と
直列化しない)を守る。

**1 ホップ目(tip の計算)**: `IIdeEngine.BeginGetMethodTipInfo(source, line, col)` は
`AsyncWorker.AddWork` 済みなので、そのまま既存の `EngineRequestBridge` に載る。結果は
`MethodTipInfoAsyncRequest.MethodTipInfo` に載って返るので、**`IIdeProject` コールバックの相関は不要**
(documentHighlight の `SetHighlights` 経路とはここが違う)。engine 座標は 1-origin、LSP は 0-origin の
UTF-16 なので `GetGoto` / `GetHoverAsync` と同じ `+1` 変換を使う。

**2 ホップ目(平坦化)**: 得られた `MethodTipInfo` は **engine 状態への遅延ビュー**である。
`GetDescription` / `GetParameterInfo` は `XmlDocReader.GetInfo` を呼び、その実体は

- module レベルの `mutable _xmlDocCache : Map[string, XmlDocFile]` — ミスのたびに**丸ごと再代入**、
- `XmlDocFile._members` — `.xml` のタイムスタンプが進んでいると**その場で `Load()` して差し替え**

という**同期の無い共有可変状態**である。worker が hover / completion で同じキャッシュを読んでいる
最中に LSP スレッドから読むのは engine 状態に対するデータ競合になる。したがって平坦化は
**`AsyncRequestType.EmptyRequest` の自前 work item(`MethodTipDescriptionRequest`)として worker に
投げ返し**、その中で `MethodTipInfo` → プレーンな record(`NemerleMethodTip`)に落とす。
`ColorizeRequest`(WP-O5a)と同じ理由で `EmptyRequest` を借りる: 共有の `AsyncRequestType` に種別を
足すのは VsIntegration の改変になるため。帰結として **`CloseProject` 以外に押し出されない** =
要求が黙って捨てられることはない。

**代案を採らなかった理由**: (a) LSP スレッドで平坦化する案は上記の競合が残る。既存の
`ResolveCompletionDescription` が同じ形の off-worker 読み出しをしているが、それは先行実装の
既知の穴であって新機能の手本にはしない(§既知の制約)。(b) 1 ホップに畳むために平坦化 work item を
tip 要求と同時に投入して自己再投入で待たせる案は、`Engine-GetMedhodTip.n` が types tree 構築中に
**tip 要求自身を再キューする**ため両者が ping-pong する。逐次 2 ホップの方が読める。

**コスト**: warm の round-trip は実測 **62 ms**(hover の数 ms より遅い)。内訳は worker の
`ThreadProc` が work 1 件ごとに `Thread.Sleep(10)` すること、bridge が 5 ms 間隔でポーリングすること、
そして `GetMethodTip` が `EngineEx.RunCompletionEngine` を回すこと。signature help は 1 文字ごとに
飛ぶ要求ではないので許容と判断した。

### 3. `activeSignature` / `activeParameter` の正規化

- **`activeSignature` は `MethodTipInfo.DefaultMethod`**。ただし engine 側は
  `Project.Type.n` で `List.FindIndex` の**ミス値 -1 をそのまま渡す**ことがあり、
  `OverloadsMethodTipInfo` のガード `when (defaultMethodIndex >= 0 || defaultMethodIndex < _overloads.Count)`
  は `||` なので **-1 を弾かない**(upstream のまま。共有ソースなので直さない)。範囲外の
  `activeSignature` は protocol error なので、**範囲外なら 0 に落とす**。overload 一覧は engine が
  パラメーター数の昇順にソートしているので、0 は「最も単純なアリティ」= 既定として自然。
- **`activeParameter` は `MethodTipInfo.ParameterIndex`**(engine の `calcActiveParam` = そこまでに
  打たれたカンマの数)。**上限クランプはしない**: LSP は範囲外の `activeParameter` を
  「どのパラメーターも強調しない」と規定しており、overload の引数個数を超えて引数を打っている
  状態の描画としてはそれが正しい。負値のみ 0 に補正する(engine は出さないが protocol error なので)。

### 4. null 返答とログ

`HasTip == false`、overload 0 件、bridge が `Cancelled` / `Stale` / `TimedOut` を返した場合はいずれも
**null**(空の `SignatureHelp` ではない)。空コンテナは VS Code が空のポップアップを開いたままにする。

**無言の null 経路は作らない**(WP-O5a の教訓)。Output からは「クライアントが要求していない」のと
区別が付かないため、`ServerLog` に

- `nemerle signature help skipped: <uri> is not an open document`
- `nemerle signature help unavailable at <uri> L:C (engine request <Outcome>)`
- `nemerle signature help unavailable at <uri> L:C (description request <Outcome>)`
- `nemerle signature help empty at <uri> L:C (N ms)`
- `nemerle signature help computed in N ms at <uri> L:C (M signature(s), active S/P)`

を出す。raw LSP シナリオは 4 番目を実際に待って固定している。

### 5. ドキュメントは常に plain text

`SignatureInformation.documentation` / `ParameterInformation.documentation` は
`MarkupKind.PlainText` で返す。中身は engine が既に散文へ落とした XmlDoc であり、markdown として
再解釈すると識別子中の `_` や `*` が装飾に化ける(completion resolve が同じ判断をしている)。

### 6. 登録オプション

- `TriggerCharacters = ["(", ","]` — `(` で開き、`,` は「開いた括弧の後から要求したいとき」に開く。
- `RetriggerCharacters = [","]` — 既に開いているポップアップの `activeParameter` を進める。
- document selector は他のハンドラーと同じ `nemerle`。

## 実装ファイル

新規:

- `dotnet-port/ProjectInfo/SignatureHelpMapping.cs` — engine 非依存の入力 record
  (`NemerleTipParameter` / `NemerleTipSignature` / `NemerleMethodTip`)、出力 record
  (`NemerleSignatureParameterLabel` / `NemerleSignatureLabel` / `NemerleSignatureHelp`)、
  純関数 `ToSignatureHelp`(ラベル組み立て・オフセット計算・空白畳み・index 正規化)。
- `dotnet-port/LspServer/NemerleSignatureHelpHandler.cs` — `SignatureHelpHandlerBase` 実装、
  `labelOffsetSupport` のネゴシエート、trigger / retrigger 登録、ログ。

変更:

- `dotnet-port/LspServer/NemerleProject.cs` — `GetSignatureHelpAsync`、
  `MethodTipDescriptionRequest` / `BeginDescribeMethodTip` / `RunDescribeMethodTip` /
  `DescribeMethodTip`(worker 上の平坦化)。
- `dotnet-port/LspServer/Program.cs` — ハンドラー登録。
- `dotnet-port/ProjectInfo.Test/Program.cs` — `SignatureHelpMappingTests`。
- `dotnet-port/LspServer.IntegrationTest/Program.cs` — raw LSP シナリオ 3 本 + ヘルパー。
- `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs` — `signatureHelp` の client capability
  (`labelOffsetSupport` / `activeParameterSupport` / `contextSupport` / `documentationFormat`)。

## 検証

すべて Windows 11 で実測。共有ソース無改造のため testsuite / stage 比較 / CLR4 スモークは対象外。

| ゲート | 結果 |
|---|---|
| `dotnet build -c Release LspServer` | 成功(0 警告 / 0 エラー) |
| `dotnet build -c Release LspServer.IntegrationTest` | 成功(0 警告 / 0 エラー) |
| raw LSP 統合(`Nemerle.LanguageServer.IntegrationTest`) | **37 シナリオ PASS**(既存 34 本 + 新規 3 本、回帰ゼロ) |
| `dotnet build -c Release ProjectInfo.Test` | 成功(0 警告 / 0 エラー) |
| `ProjectInfo.Test`(unit) | PASS(`SignatureHelpMappingTests` 追加) |
| `ProjectInfo.Test -- --integration` | PASS(sample / SDK package 評価に無回帰) |
| `git diff --check` | クリーン |
| `npm run check-types` / `lint` / `test` | **実行せず**(manifest / TS 無変更。signature 関連の記述が拡張側に 1 件も無いことを grep で確認) |
| `npm run test:integration`(実 VS Code) | **未実施**(WP-P 系列完了時にまとめて 1 回、`56-*` §3-4) |

実測値:

- warm `textDocument/signatureHelp` round-trip: **62 ms**(worker 2 ホップ込み)。
- `StringBuilder("start")` に対して提示された overload: **6 本**。
- `JsonConvert.SerializeObject(42)` に対して **10 本中 10 本が `<summary>` 付き**、
  **パラメーター 25 個中 25 個が `<param>` ドキュメント付き**。

### raw LSP シナリオが固定していること

**1 本目**(`Signature help: overloads, active signature/parameter, label parameter offsets`):
一時ディレクトリの loose file に、同名で引数個数の違う 2 つの `Combine` と引数無しの `Nothing` を置く。

1. overload が **2 本とも**返り、ラベルが `Combine(first : int, second : int) : int` /
   `Combine(first : string, second : string, third : string) : string` **完全一致**
   (= ラベルの組み立て規則そのものを固定している)。
2. `activeSignature` が **int の overload を指す**(engine が呼び出しを解決している)。
3. 第 1 引数上で `activeParameter == 0`、**カンマの後ろに移ると 1 になる**。
4. **全パラメーターのラベルが `[start, end)` の配列**であり、その範囲が
   **自分自身の字面だけを指す**(区切りの `, ` や括弧に食い込まない)ことを、ラベルを実際に
   スライスして検査する。部分文字列形式ではこの検査自体が書けない = オフセットを出す理由。
5. **引数 0 個の呼び出しでも答える**(`Nothing() : void`、パラメーター配列は空)。

**2 本目**(`Signature help: constructor call, and a position outside any call answers null`):

1. `StringBuilder("start")` で **全 overload が型名で始まる**(`.ctor` ではない)。
2. `activeSignature` が **string を取る overload**を指す(6 本の中から解決している)。
3. パラメーターオフセットの検査を外部アセンブリ由来の overload 集合に対しても行う。
4. **呼び出しの外(`def builder` の上)では `null`** が返り、かつ
   **`nemerle signature help empty` が `window/logMessage` に出る**。「静かな null」を作らない
   という設計(§設計-4)を実際に固定している。

**3 本目**(`Signature help: XmlDoc summary and parameter docs from a PackageReference assembly`):
`samples/PackageReference` を project として load し、`JsonConvert.SerializeObject(` を要求する。

1. **`<summary>` が `SignatureInformation.documentation` に届く**(10/10)。
2. **`<param>` が `ParameterInformation.documentation` に届く**(25/25)。
3. どちらも **1 行に正規化**されている(改行が残っていない、空白の連続が残っていない)
   = §設計-1 の正規化を実データで固定。

fixture は既存 samples のみを使い、新しい fixture は追加していない。

## 既知の制約 / 残課題

- **実 VS Code での確認は未実施**。raw LSP でプロトコルの形は固定したが、
  「ポップアップが出て正しいパラメーターが太字になる」ことは実機ゲート
  (`npm run test:integration`)で確認する必要がある。`56-*` §3-4 のとおり WP-P 系列の完了時に
  まとめて 1 回実施する。
- **XmlDoc は外部アセンブリのメンバーにしか出ない。** engine の `XmlDocReader.GetContent` は
  `location.EndLine > 0`(= ソース上の位置を持つメンバー)を弾くので、**同一プロジェクトの
  `///` コメントは signature help に出ない**。これは hover / completion も同じ既存挙動で、
  本 WP では変えていない(engine 改修が要るため §3-3 の停止条件に当たる)。
- **warm 62 ms** は hover(数 ms)より遅い。原因は worker 2 ホップ(§設計-2)と
  `AsyncWorker.ThreadProc` の `Thread.Sleep(10)`。速くするなら engine 側のスリープに触ることになり、
  それは共有ソース改変なので本 WP の範囲外。
- **`ResolveCompletionDescription`(WP-M3)は今も LSP スレッドで XmlDoc を読む。** 本 WP で
  worker 側に寄せたのは signature help の経路だけである。同じ競合の穴が completion resolve に
  残っており、寄せるなら本 WP の `MethodTipDescriptionRequest` と同じ形が使える(小さい後続作業)。
- **signature help はメソッド本体の中でしか出ない。** `Project.Type.n` の `GetMethodTip` は
  `MethodBuilder.BodyLocation.Contains(line, col)` を前提にしている。フィールド初期化子は
  `FieldBuilder.LookupInitializerMethod()` 経由で拾える設計だが**未実測**。属性引数・
  ジェネリック型引数リスト(`[T]`)は engine が `Token.RoundGroup` しか見ないため非対応。
- **名前付き引数 / 可変長引数での `activeParameter` は未検証。** engine の `calcActiveParam` は
  カンマを数えるだけなので、名前付き引数を並べ替えた場合は実際の対応とずれる可能性がある。
- **`LocalFuncMethodTipInfo` / `VariantConstantObjectTipInfo` の description は擬似ドキュメント**
  (それぞれ `"local function"` / `"Variant constructor"` という固定文字列)で、そのまま
  `documentation` に出る。情報量はあるので畳んでいないが、XmlDoc ではない。
  なお `VariantConstantObjectTipInfo.GetParameterInfo` は `assert(false)` だが、
  `GetParameterCount` が 0 を返すので到達しない。
- **`OverloadsMethodTipInfo` の `defaultMethodIndex` ガードは upstream のまま誤り**
  (`||` / `&&`)で、-1 が素通りする。サーバー側で正規化して吸収した(§設計-3)。
  直すなら共有ソース改変になるので触っていない。
- **`activeParameter` の上限は意図的に開けてある**(§設計-3)。引数を打ちすぎた状態では
  どのパラメーターも強調されない。
- **拡張の manifest は無変更**。将来 `editor.parameterHints` 系の既定値を `[nemerle]` に入れたく
  なった場合のみ manifest を触ることになる(サーバー変更は不要)。

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

- **`EngineRequestBridge` + 自前 `EmptyRequest` work item で「worker 上で engine オブジェクトを
  読み出す」型が 2 例目**になった(1 例目は WP-O5a の `ColorizeRequest`)。engine の結果オブジェクトが
  遅延ビューである限り、読み出しも worker に載せるのが既定と考えてよい。
  WP-P2(documentHighlight)/ WP-P3(rename)が `GotoInfo` を扱うときも同じ判断が要る
  (`GetGoto` の worker 経路化は `56-*` §3-1 のとおり WP-P3 の先頭で行う)。
- **raw LSP シナリオは 37 本**になった。以降の WP はこの本数からの無回帰で数えること。
- `LspTestClient` の `initialize` capability に `signatureHelp` を足した。後続の機能も同じ場所に
  capability を足さないとハンドラーが登録されない。
