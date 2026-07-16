# 38. WP-N2: engine 品質の事前調査ログ(E7 / E8 / 報告事象)

実施日: 2026-07-16

ブランチ: `wip/dotnet-port`

対象: `36-prerelease-quality-plan.md` の **WP-N2**。本ログは実測の証跡(§3〜§4)、
原因分析(§5)、仮実装計画(§6〜§7)を記録する。修正実装はまだ行っていない。
`37-*.md` は WP-N1 用に予約する。本文は現時点の理解のみを記載し、調査の経緯
(時系列)は §9 に簡潔にまとめる。

## 1. 結論: 事象の分類

E7 / E8 と報告事象は、症状が似ていても原因の異なる群に分かれる。同じ表示症状でも
群を合流させず、fixture と受け入れ判定は群ごとに独立して持つ(§6 N2.0)。

| ID | 項目 | 実測した問題 | 原因(詳細は §5) |
|---|---|---|---|
| R-H | Sokoban hover 4件(報告事象) | 型の単純名だけが消える | LSP の hint markup 変換での情報損失(§5.1) |
| R-R | Sokoban `SMap` references(報告事象) | 宣言・型注釈のどちらを起点にしても 0 件 | 型 usage walker の annotation 未対応(§5.3) |
| R-P | CompTimeSolver/Success(報告事象) | ncc build は成功、LSP のみ top-level 式に parse error 2件 | IntelliSense mode の parser 分岐(§5.4) |
| E7-D | hover の型欄空欄 | local / method の raw hint は解決済みだが LSP 表示で型名が消える | R-H と同じ markup 変換(§5.1) |
| E7-R | hover / definition の null | static qualifier / 型注釈の位置から symbol を解決できない | `Project.FindObject` の位置解決不足(§5.2) |
| E8 | references 完全性 | symbol kind / 構文位置ごとに探索能力が不均一(別型 member は取得できる) | legacy `GetUsages` が stub 状態の混成実装(§5.3) |
| K-C | WP-K completion 6 FAIL | .NET Framework 2.0 前提の候補件数・順序と .NET 10 実値が異なる | 参照 assembly / runtime 型集合の差。上記いずれとも独立(§2.3) |

## 2. 定義と証跡境界

### 2.1 E7 は 2 層に分ける

E7(hover / definition の型欠落。31/33 の log に記録)は、実態として次の 2 層を含む。
本ログでは両者を混同しない。

1. **E7-D(display / 型欄空欄系)**: local value / method の hover は名前や宣言を
   解決できているが、型欄が空に見える。応答は null ではない。
2. **E7-R(resolution / 応答 null 系)**: static method 呼び出しの型修飾子、型注釈等の
   位置を `Project.FindObject` が解決できず、hover / definition が null または空になる。

definition に「型欄」はないため、definition 側の E7 は常に E7-R(位置から symbol /
source location を解決できるか)の問題である。

### 2.2 E8 の実態

コード上、`GetActiveDecl` は cursor を含む `Decl.Type` を取り `FindUsages(inType, ...)` に
渡すが、この `inType` は主に **cursor 位置の declaration object を同定する起点**であり、
member / type の候補探索は `EngineEx.GetSources()` の全 source を走査する(別型の候補も
typed tree で検証する)。実測でも別型 member usage は全件取得できた(§4.3)。

E8 の問題は探索範囲の型スコープ制約ではなく、legacy `GetUsages` が stub 状態の混成実装で
(`Project.Refactoring.n` 自身が `get usages behaviour, this is a stub yet` と記す)、
**symbol kind / 構文位置ごとに semantic coverage が不均一**なことである(§5.3)。
この定義は「別型 member は取れるが `SMap` 型 references が 0 件」という実測(§4.3)と
矛盾しない。`UsagesInCurrentFile` は `//!!!` 付きで通常の `GetUsages` と同じ実装へ流れる。

### 2.3 K-C: WP-K completion 6 FAIL の位置づけ

`22-lsp-step1-log.md` の 58 件中 6 FAIL(`Complete_in_return_type_*`、
`Complete_Complete_expr`、`Complete_enum`、`Complete_qualidend`)は、すべて
`CompletionElems` の特定 index の名前または配列長の assert であり、fixture の期待値は
.NET Framework 2.0 時代の `mscorlib` / `System` / `System.Data` / `System.Drawing` /
`System.Windows.Forms` を前提にする。原因は 2 つに分かれる。

| 分類 | 原因 | 影響 | 対処 |
|---|---|---|---|
| K-C1 | `System.Data` / Drawing / WinForms 等を headless host がロードしない | 旧 fixture より候補が減る、順序が変わる | Core 対象なら runtime 別期待値へ更新。desktop parity が要件なら参照を明示追加 |
| K-C2 | .NET 10 split BCL で `mscorlib` / `System` 相当の namespace / type 集合が変化 | 候補が増減し index / 件数が変わる | 件数・順序への依存を避け、必要 symbol の包含を検証。実 project の解決済み参照を正とする |

K-C が実証するのは **参照 assembly / runtime の型 universe 差による global completion
候補集合の差**であり、E7-D(hover の型欄空欄)の証拠ではない。raw engine の
`QuickTips` test は `QuickTipInfo.Text` に期待部分文字列(symbol 名等)があることを検査して
PASS しており、local / method の**型欄そのものは assert していない** — つまり WP-K は
E7-D を実証も反証もしていない。K-C1/K-C2 は completion の参照 universe を整える問題で、
`HoverMarkup` を直しても変化せず、hint-value 平文化は completion 候補集合を変えない。
両者の対処は独立である。

## 3. 確認環境と方法

- Windows 11 / PowerShell
- .NET 10 Release build の `LspServer/bin/Release/net10.0/Nemerle.LanguageServer.dll`
- raw stdio LSP。VS Code UI を挟まず server の JSON 応答を採取
- project は `nemerle/projectInfo/load(forceReload=true)`、文書は `didOpen` 後の
  `publishDiagnostics` を待ってから hover / definition / references を要求
- 再現コードは `LspServer.IntegrationTest` の `--wp-n2-probe` に保存

比較対照として `samples/CompTimeSolver/Success/Success.nproj` を通常の MSBuild/ncc 経路でも
ビルドした。

## 4. 実測結果

### 4.1 R-H: Sokoban hover / definition 4 件

`samples/Sokoban/Sokoban/Sokoban.nproj` の初回解析が診断0で完了した後に測定した。位置は1-origin。

| 位置 | 対象 | hover 実測 | definition 実測 |
|---|---|---|---|
| `main.n:7:23` | parameter `args` | `(function parameter) args : []` | 同じ宣言1件 |
| `splayheap.n:7:39` | field type `SMap` | `NSokoban.` | `sokoban.n:107:18` 1件 |
| `treesearch.n:15:15` | inferred local `depth` | `(mutable local value) depth : ` + `defined in BFS(...)` | 同じ宣言1件 |
| `sokoban.n:109:35` | `Hashtable` | `Nemerle.Collections.[, NSokoban.]` | 0件 |

前3件は symbol / declaration を解決できている。`Hashtable` の definition 0件は外部 assembly 型に
workspace source location がないためで、hover の単純名欠落とは別である。

`args` は初回 full rebuild、実際の incremental relocation、forced project reload の3状態で同じ
`args : []` を返した。stale version / reload timing の一時的問題ではない。

### 4.2 E7 の正準ケース

自己完結 fixture で E7-D と E7-R を分けて測定した。

| 位置の種類 | hover | definition | 判定 |
|---|---|---|---|
| local value usage | `(local value) localValue :` | 1件 | 解決済み。型名だけ表示変換で消失 |
| method call | `public static .Compute(value : ) :` | 1件 | method / parameter / return の型名が表示変換で消失 |
| static method type qualifier `E7Probe` | null | 0件 | E7-R を再現 |
| project 内型の型注釈 `Box` | null | 1件 | hover は E7-R、definition は解決可能 |

「型注釈への hover / definition は常に両方 null」と一括りにはできない。型注釈 hover は
null だが、project 内型の definition は成功した。外部型注釈の空結果(33 の log)とは
source location の有無も分けて matrix 化する必要がある。

### 4.3 R-R と E8 の正準ケース

Sokoban の `SMap`:

| 起点 | references(`includeDeclaration=true`) |
|---|---|
| `splayheap.n:7:39` の field type `SMap` | 0件 |
| `sokoban.n:107:18` の `public class SMap` 宣言 | 0件 |

別型 member fixture:

```nemerle
class Shared { public Ping() : int { 1 } }
class First  { public Run(value : Shared) : int { value.Ping() } }
class Second { public Run(value : Shared) : int { value.Ping() } }
```

`Ping` 宣言起点・`First` 内 usage 起点の双方が、宣言 + `First` + `Second` の3件を返した。
従って R-R は「別型が探索外」ではなく、type symbol / annotation の walker coverage 不足である。

### 4.4 R-P: CompTimeSolver/Success

通常の `dotnet build Success.nproj` は警告0・エラー0で成功した。raw LSP は2行目
`WriteLine(SolveMaze("success.txt"));` に次の2診断を返した。

1. character 0–9: ``parse error near identifier `WriteLine': expecting type declaration``
2. character 9–35: ``parse error near `(...)' group: unexpected token after type declaration ...``

macro の戻り値型を調べる前の parse 段階で失敗しており、macro 解決とは無関係である。

## 5. 原因と修正境界

### 5.1 R-H / E7-D: hint markup の情報損失

`HintHelper` → `SubHintForType.TypeVarToString` → `QuickTipInfo.Text` の経路では、型の単純名を
`<hint value='型名' key='…' />` に入れる。旧 Visual Studio UI は `value` を inline 表示するが、
`HoverMarkup.ToPlainText` は self-closing tag 全体を削除する。名前空間と `[]` / `[,]` だけ残る
実測(§4.1〜4.2)はこの変換と一対一に対応する。R-H の4件と E7-D は同一原因である。

修正案は、一般 tag 除去の前に self-closing `<hint>` の `value` を可視文字列へ展開すること。
`ProjectInfo` の純関数と unit test、Sokoban raw LSP assertion で閉じられ、Stage リビルドは不要。

なお、BCL / metadata import 差が別の hover で本当に型解決を失敗させる可能性までは否定しない。
その場合は raw `QuickTipInfo.Text` の時点で型が `?` / 欠落する、対象 assembly/type が workspace に
存在しない、diagnostic や completion にも同じ symbol 欠落が出る等の独立した証拠が必要である。
対処も reference snapshot / `ExternalTypeInfo.collect_members` 等の metadata importer 側となり、
hint markup 修正とは別になる。

**メモ(MarkupKind の Markdown 化 — 本 WP のスコープ外)**: hover は WP-M2 実装済みで、
クライアントが `hover.contentFormat` に markdown を広告した場合は
`MarkupKind.Markdown` + `HoverMarkup.ToMarkdown` を返す(VS Code は該当。ただし現在の
Markdown 形は平文全体を ```` ```nemerle ```` フェンスで包むだけ)。一方 completion の
`documentation` は `MarkupKind.PlainText` 固定(`NemerleCompletionHandler.cs`)。
「completion documentation の Markdown 化」と「hover Markdown 表現の高度化」は
N2.1 では扱わない。高度化の素材はすでにある: engine の擬似 markup は
`<keyword>` / `<b>` / `<params>` / `<pname>` / `<ptype>` / `<hint>` の構造を持つ
(現在の `ToPlainText` はこれらを一括除去している)ため、シグネチャ部のみ code fence、
doc コメントは地の文、パラメーターはリスト化する Roslyn 風の構造化 hover が、
engine 無改修・LSP 変換層(`HoverMarkup`)のみで実現できる見込み。
仮の予定: **WP-N 完了までに、(a) completion documentation の Markdown 化と
(b) hover Markdown の構造化の 2 点を、36 §10 のバックログ
(E9「completion/hover 表示の細部」の明示項目)として起票する**。N2.1 の hint-value 平文化は `ToPlainText` 修正であり、
`ToMarkdown` はその結果をフェンスするだけなので、両経路に同時に効く(競合しない)。

### 5.2 E7-R: `FindObject` の位置解決不足

`Project.GetTypeQuickTip` は `FindObject(typeDecl, fileIndex, line, col)` の結果が null なら null を返す。
static call qualifier や一部型注釈は typed expression / parsed type から対象 `TypeVar` / `TypeInfo` へ
写す分岐が不足する。hover が null の場合は平文化する payload 自体が無いため、
hint-value 展開(§5.1)はこの層へ到達せず、E7-R は直らない。

修正には、位置別 fixture を先に作り、`FindObject` / `FindObjectEverywhere` の `checkType` と
static-call qualifier 処理を拡張する必要がある。project 型 / 外部型 / ProjectReference 型では
definition の source location 可否が異なるため、hover と definition の期待を別々に持つ。
engine 共有ソース変更となる見込みで、Stage リビルドが必要。

### 5.3 R-R / E8: usage collector の coverage 不足

legacy `GetUsages` の探索能力は symbol kind ごとに不均一である。

- local / parameter は現在の method body を個別 walker で探索する。
- member は全 source を text scan して候補化し typed tree で identity を検証する
  (別型の usage も取得できる。§4.3)。
- type は全 source の候補 `Decl` を `FindTypeEntriesVisitor` で歩くが、認識するのは主に
  function parameter type、constructor、`TypeBuilder` 宣言で、一般の field / local / return /
  property type annotation を網羅しない。R-R(`SMap` 0件)の直接原因。
- `UsagesInCurrentFile` は `//!!!` 付きで通常の `GetUsages` と同じ実装へ流れる。

修正案は request-time の current-project semantic walk を正しさ優先で実装すること。symbol identity
を一度確定し、全 `Decl.Type`、signature、typed method body、pattern、constructor、macro expansion
を歩く。計測で必要になった場合だけ generation-aware index を導入する。

現在の engine workspace に入らない ProjectReference 先 source は探索できない。この境界は E8 の
collector bug とは分け、multi-project workspace のスコープ判断として扱う。

### 5.4 R-P: IntelliSense mode の parser 分岐

`MainParser.Parse` は概ね `Manager.IsIntelliSenseMode || IsTopLevel(topstream)` の場合に
`ParseTopLevel` を使う。LSP engine は IntelliSense mode なので top-level 式も型宣言として parse
しようとするが、ncc は synthetic class + `Main` に包む。E7 / E8 とは無関係な parser parity 問題である。

既存挙動を既定値として残した opt-in option / overload を `MainParser.Parse` に追加し、
`IntegrationDefaultParser` だけ有効にする案が安全。共有 parser / engine 変更なので Stage
リビルドが必要。

### 5.5 改修と解消範囲の対応

| 改修 | R-H | E7-D | E7-R null | R-R `SMap` | E8 完全性 | R-P |
|---|---|---|---|---|---|---|
| `<hint value>` 平文化 | 解消見込み | markup 起因分は解消 | 直らない | 直らない | 直らない | 直らない |
| `FindObject` 位置解決拡張 | 無関係 | raw 型未確定なら対象 | 対象 | 起点解決には寄与しうる | 一部 | 無関係 |
| project-wide semantic usage walk | 無関係 | 無関係 | 無関係 | 対象 | 対象 | 無関係 |
| IntelliSense parser parity opt-in | 無関係 | 無関係 | 無関係 | 無関係 | 無関係 | 解消見込み |

hint-value 平文化だけでは E7-R が残るため、E7 は N2.1(markup)と N2.2(FindObject)の
2 track に分けて実装する(§6)。また表の4改修はいずれも K-C1/K-C2 の completion 候補差を
直すものではない。K-C は E7 の完了条件に混ぜず、legacy WP-K test を保守する場合だけ
「runtime別期待値または必要symbol包含assertへの変更」として独立に扱う。

### 5.6 既存 .NET Framework 4 / Mono ベースのコードへの影響

4 改修が触れるソースと、CLR4 / Mono フレーバーへの波及は次のとおり。

| 改修 | 変更するソース | CLR4 / Mono ビルドへの波及 |
|---|---|---|
| N2.1 hint-value 平文化 | `dotnet-port/ProjectInfo/HoverMarkup.cs`(.NET 10 LSP 専用 C#) | なし。ncc / engine / VS2010 のどのビルドにも含まれない |
| N2.2 `FindObject` 拡張 | engine 共有ソース(`VsIntegration/Nemerle.Compiler.Utils` の `Project.Type.n` / `CompilerUnit.n` / `Project.Refactoring.n`) | VS2010 integration と SharpDevelop binding(`snippets/sharpdevelop`)が同一ソースを共有。ncc 本体 / boot-4.0 には含まれない |
| N2.3 parser parity opt-in | コンパイラー共有ソース(`ncc/parsing/MainParser.n`) | boot-4.0 → stage1(CLR4)の ncc 本体に入る。upstream 系譜では Mono でもビルドされてきたソース |
| N2.4 semantic references | engine 共有ソース(N2.2 と同じ、中心は `Project.Refactoring.n`) | N2.2 と同じ |

- **N2.1**: CLR4 / Mono コードに一切触れないため、リスクなし。
- **N2.2 / N2.4(engine 共有ソース)**: VS2010 本体はデッド・移植対象外(36 §2.3)だが
  ソースは共有のままなので、VS2010 用に CLR4 でビルドした場合の IntelliSense /
  references 挙動が変わり得る。さらに repo 内の消費者は VS2010 だけではない:
  `snippets/sharpdevelop/Nemerle.SharpDevelop`(OSS IDE SharpDevelop 向け binding)が
  `Nemerle.Compiler.Utils.csproj` を ProjectReference し、`Nemerle.Completion2` API を
  CodeCompletion 一式で直接利用している。これらのビルドは回帰ゲートに含まれない
  (CLR4 スモークは ncc の hello/hello2 のみで engine を通らない)ため機械的検証はなく、
  影響確認の grep は VsIntegration に加えて `snippets/sharpdevelop` も対象に含める
  (36 §5-5 の運用をこの範囲へ拡張)。挙動面では、
  `FindObject` 拡張は「null を返していた位置で結果を返す」方向、`GetUsages` の walk 追加は
  「取りこぼしていた usage を返す」方向の変更であり、既存で解決できていた位置・集合を
  変えないことを positive control(§6 N2.4、別型 `Ping` 3件等)で担保する。
- **N2.3(`MainParser`)**: CLR4 / Mono のビルド対象に入る唯一の変更。既定値を現行の
  `IsIntelliSenseMode || IsTopLevel` 判定(`MainParser.n:375`)のまま残す opt-in
  (既存 entry point は無変更で overload を追加)とするため、opt-in を呼ばない
  ncc / VS2010 / Mono の parse 挙動は不変。ソースレベルでは boot-4.0 の旧コンパイラーで
  コンパイルできる言語機能に限定する必要がある(bootstrap 制約)。検証は testsuite 全数 +
  stage2/3 バイト一致 + CLR4 スモークの既存ゲートで機械的に閉じる。
- **Mono 全般**: 本移植は Mono 非対象(36 §4 G1)で Mono 上の検証は行わない。ただし
  Mono との接点は N2.3(ncc 本体)に限らない: engine 共有ソースも SharpDevelop binding
  経由で VS 以外の IDE 環境から消費されてきた経緯があり、その系譜(SharpDevelop /
  派生の Mono ベース環境)で利用されていた可能性を排除できないため、
  「Mono のビルド対象に入り得るのは N2.3 のみ」とはいえない。N2.2 / N2.4 の
  engine 変更も、public API シグネチャと既存挙動の互換(既存で解決できていた結果を
  変えない)に注意する。N2.3 は既定挙動を変えない opt-in のため、ソース互換
  (使用する言語機能・API)以外の新規リスクは想定しない。

## 6. 仮実装計画

### N2.0: fixture / control を6群に分離

1. K-C control: global completion の参照 assembly / runtime 型集合差。E7 fixture にしない。
2. R-H: Sokoban hover 4件。
3. E7-D: local / parameter / method の raw hint と LSP 表示。
4. E7-R: static qualifier / 型注釈 × hover / definition × project / external / reference 型。
5. R-R/E8: type / method / property / local / parameter × 同一型 / 別型 / 別file。
6. R-P: Success の ncc/LSP parser parity。

同じ表示になっただけで群を合流させず、engine raw result、LSP 変換後、location 集合を別 assertion にする。

### N2.1: R-H と E7-D の markup 修正(LSP-only、低リスク)

- `HoverMarkup` に self-closing hint の `value` 展開を追加する。
- single/double quote、属性順、escape、malformed tag、paired hint の unit test を追加する。
- Sokoban 4件と local / method の完全な型表示を raw LSP で固定する。
- この完了をもって E7 全体を close しない。

### N2.2: E7-R の engine 解決拡張(中〜高リスク)

- `FindObject` の位置別失敗表を確定する。
- static call qualifier と parsed type annotation から semantic type へ写す処理を追加する。
- hover と definition の期待を分離し、external 型の definition 空結果は source 不在として維持する。
- Stage2/3、testsuite、CLR4 smoke を回す。

### N2.3: R-P parser parity(中リスク)

- `MainParser.Parse` に後方互換な opt-in を追加し engine parser だけ有効化する。
- Success 診断0、通常 build、malformed top-level、incremental/full reload を固定する。
- parser 共有変更の全回帰ゲートを実行する。

### N2.4: R-R / E8 semantic references(高リスク)

- 現在すでに成功する別型 member と local references を positive control にする。
- type annotation を最初の欠落修正対象にし、signature / body / pattern / macro へ matrix を広げる。
- current-project semantic walk の集合完全性と `includeDeclaration` を固定する。
- ProjectReference source 横断は multi-project workspace として PO 判断を分離する。
- rename-safe と言える水準に達しない場合は rename を有効化しない。

## 7. 仮受け入れ基準

1. R-H 4件は型の単純名を含む完全な hover を返す。
2. E7-D は engine raw hint と LSP 表示の双方で型が確認でき、E7-R matrix の null が合意範囲で解消する。
3. R-R の `SMap` と E8 matrix は current project 内で cursor 起点によらず同一集合を返す。
4. 既に成功する別型 `Ping` 3件を回帰させない。
5. R-P は ncc build / LSP とも parse error 0になり、通常の未完成入力の診断回復を壊さない。
6. ProjectReference source 境界を rename 前提へ含めるか、明示的に後続へ送るかを記録する。
7. raw LSP、ProjectInfo unit、testsuite、stage2/3、CLR4 smoke に新規 regression 0。

## 8. 再現コマンドと結果

```powershell
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll --wp-n2-probe
dotnet build dotnet-port\samples\CompTimeSolver\Success\Success.nproj -c Debug
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll
```

- probe 追加後の Release rebuild: 成功(既知 warning 6、error 0)
- probe: PASS、12.8秒。R-H、E7正準4位置、R-R、別型 `Ping`、R-P を実測
- `Success.nproj`: 成功(警告0、エラー0)
- 通常 raw LSP integration: 全シナリオ PASS(probe 拡張は `--wp-n2-probe` 分岐内だけで
  通常シナリオを変更しない)

## 9. 調査の経緯(時系列)

1. `36` §6 WP-N2 の事前計画とトラック 3 の報告ケース(Sokoban hover 4件 /
   CompTimeSolver parse error)を出発点に、raw LSP probe(`--wp-n2-probe`)と
   自己完結 fixture で実測した。
2. 実測の結果、事前計画が前提としていた旧記録の 2 点を本ログの定義で置き換えた:
   (a) 31 の「E7 の型欄空欄は WP-K の補完個数のずれと同根」という因果推定
   (WP-K が実証したのは参照型集合差のみ — §2.3)、
   (b) 33 の「references は cursor 所属型だけを探索」という一般化
   (別型 member は実測で全件取得できた — §2.2, §4.3)。
3. 調査初期の仮計画は `HoverMarkup` 修正・references 再設計・parser parity の 3 本立て
   だったが、markup 修正では E7-R の null 系が残ることが分かり、§6 のとおり
   N2.1(markup)と N2.2(`FindObject`)に分割した。
4. 報告時の仮説「success.n のエラーは SolveMaze マクロの戻り値型が不明のため」は、
   parse 段階で失敗している実測(§4.4)により棄却した。
