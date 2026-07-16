# 39. WP-N2: engine 品質の解消 実装ログ(N2.1〜N2.4)

実施日: 2026-07-16

ブランチ: `wip/dotnet-port`

対象: `36-prerelease-quality-plan.md` の **WP-N2**(E7 / E8 / 報告事象)。
事前調査・原因分析・実装計画は `38-prerelease-wp-n2-log.md`(以下「38」)。本ログは
38 §6 の実装計画(N2.0〜N2.4)の実装結果と、38 §7 の仮受け入れ基準に対する判定を記録する。

## 1. 結論(38 §7 仮受け入れ基準との対応)

| 基準(38 §7) | 結果 |
|---|---|
| 1. R-H 4件は型の単純名を含む完全な hover を返す | **PASS**(§2 N2.1。4件とも probe の assertion で固定) |
| 2. E7-D は raw hint と LSP 表示の双方で型が確認でき、E7-R matrix の null が合意範囲で解消 | **PASS**(§2, §4。E7-R のうち「project 型注釈」は実は markup 起因で N2.1 が解消 — §2.3 の再分類を参照) |
| 3. R-R の `SMap` と E8 matrix は current project 内で cursor 起点によらず同一集合を返す | **PASS**(§5.3。`SMap` 55 件が両起点で同一集合、matrix fixture 9/9 同一) |
| 4. 別型 `Ping` 3件を回帰させない | **PASS**(§5.3。assertion に昇格して固定) |
| 5. R-P は ncc build / LSP とも parse error 0、診断回復を壊さない | **PASS**(§3 N2.3。probe assertion + raw LSP 29 本 PASS) |
| 6. ProjectReference source 境界の扱いを記録 | **記録済み**(§5.4: current-project スコープに限定し、multi-project workspace は後続の PO 判断へ) |
| 7. raw LSP、ProjectInfo unit、testsuite、stage2/3、CLR4 smoke に新規 regression 0 | **PASS**(§6。全ゲート green、testsuite ベースライン同数) |

## 2. N2.1: R-H / E7-D — hint markup の情報損失修正(LSP-only)

### 2.1 実装

- `dotnet-port\ProjectInfo\HoverMarkup.cs`: 一般 tag 除去(`AnyTag`)の**前**に、self-closing
  `<hint ... value='…' ... />` を `value` 属性のテキストへ展開するステップを追加
  (`SelfClosingHintTag` + `HintValueAttribute` の GeneratedRegex)。
  - single / double quote、属性順(`value` が `key` の前後どちらでも)、任意の空白に対応。
  - `value` 内の HtmlMangling エンティティは展開時に触らず、既存の一括デコードに 1 回だけ
    通す(二重デコードなし)。
  - `value` を持たない/malformed な self-closing hint は従来どおり `AnyTag` で除去
    (「raw markup をクライアントへ漏らさない」不変条件を維持)。
  - paired `<hint>…</hint>` は従来どおり(タグ除去・内容保持)。`ToMarkdown` は
    `ToPlainText` を包むだけなので両経路に同時に効く(38 §5.1 の見立てどおり)。
- `dotnet-port\ProjectInfo.Test\Program.cs`: `HoverMarkupTests()` に 12 assertion を追加
  (quote 種別、属性順、空白、entity の 1 回デコード、valueless/malformed の fallback、
  paired hint 非影響、`ToMarkdown` の fence)。
- `dotnet-port\LspServer.IntegrationTest\Program.cs`(`--wp-n2-probe`): R-H 4件と
  E7-D 2件(local value usage / method call)を観測出力から **assertion** に変更
  (型単純名の包含 + raw markup 非漏出 + relocation / project reload 後の hover 同一性)。

### 2.2 実測(BEFORE → AFTER)

| 位置 | BEFORE | AFTER |
|---|---|---|
| main.n:7:23 `args` | `(function parameter) args : []` | `(function parameter) args : array[string]` |
| splayheap.n:7:39 `SMap` | `NSokoban.` | `NSokoban.SMap` |
| treesearch.n:15:15 `depth` | `(mutable local value) depth : ` | `(mutable local value) depth : int` |
| sokoban.n:109:35 `Hashtable` | `Nemerle.Collections.[, NSokoban.]` | `Nemerle.Collections.Hashtable[string, NSokoban.SMap]` |
| E7-D local value usage | `(local value) localValue :` | `(local value) localValue : int` |
| E7-D method call | `public static .Compute(value : ) :` | `public static E7Probe.Compute(value : int) : int` |

relocation 後・forced project reload 後も main.n の hover は同一文字列(assertion 固定)。

### 2.3 発見: 「project 型注釈 hover null」は E7-R ではなく E7-D だった

38 §4.2 で E7-R に分類していた「project 内型の型注釈 `Box` への hover null」は、N2.1 の
markup 修正だけで `Box` を返すようになった(definition は修正前から 1 件)。つまりこの null は
`FindObject` の解決失敗ではなく、**hint 全体が `<hint value=…/>` のみで構成され、平文化で
空文字列に潰れた結果を上位層が null 扱いしていた**もの(E7-D の極端形)。38 の E7-R matrix は
本ログで次のように再分類する: E7-R として残るのは **static call qualifier**(および外部型の
定義位置なし)系のみ。

## 3. N2.3: R-P — IntelliSense mode の parser parity(コンパイラー共有ソース)

### 3.1 実装

- `ncc\parsing\MainParser.n`: `Parse(lex, tokenHandler)` の実装を
  `Parse(lex, tokenHandler, allowTopLevelExpressions : bool)` へ移し、既存 2 引数版は
  `false` で forwarding する後方互換 overload とした。`allowTopLevelExpressions = true` の
  場合のみ、IntelliSense mode でも `IsTopLevel` 判定に従い top-level 式を ncc と同じ
  synthetic `Main` class へ包む(既定挙動は完全に不変。変更は分岐条件
  `IsIntelliSenseMode || IsTopLevel` → `(IsIntelliSenseMode && !allowTopLevelExpressions) || IsTopLevel` のみ)。
- `VsIntegration\Nemerle.Compiler.Utils\Nemerle.Completion2\Engine\IntegrationDefaultParser.n`:
  LSP engine の parser だけ `MainParser.Parse(lexer, null, true)` で opt-in。
- probe: CompTimeSolver/Success の editor 診断を **0 件 assertion** に変更。

### 3.2 実測

- `success.n`(top-level 式 + コンパイル時マクロ)の LSP 診断: 2 件の parse error → **0 件**。
  `dotnet build Success.nproj` は従来どおり成功(警告 0・エラー 0)。
- raw LSP 全 29 シナリオ PASS(通常ファイル・malformed 入力の診断回復シナリオ含めて回帰なし)。
- boot-4.0 の旧コンパイラー(CLR4)で新 `MainParser.n` がコンパイル可能なこと(bootstrap 制約)は
  Stage1 フルリビルドで機械的に確認(§6)。

## 4. N2.2: E7-R — static qualifier の位置解決(engine 共有ソース)

### 4.1 根因(38 §5.2 の予測の修正)

38 §5.2 は「`FindObject` の分岐不足」を予測したが、実測での根因は **`ExprFinder`** に
あった。`E7Probe.Compute(...)` の walk は、外側の `PExpr.Member`(TypedObject =
`TExpr.StaticRef(E7Probe, Compute, …)` — `FindObject` 既存の StaticRef HACK が処理できる形)
を一旦候補にした後、内側の qualifier `PExpr.Ref("E7Probe")` へ降りる。この node の
TypedObject は、コンパイラーが qualifier を値式として投機的に型付けして棄却した際の
**`TExpr.Error` スタブ**であり、`ExprFinder.GetTypedObject` はこれを「有効な typed object」
として採用してしまう。その結果、外側の有効な候補が上書きされ、`FindObject` の
StaticRef HACK に到達しないまま null になっていた。

### 4.2 実装と実測

- `VsIntegration\...\CodeModel\ExprFinder.n`: `GetTypedObject` で `TExpr.Error` を
  「未型付け(null)」と同一視。これにより walk は実際の解決を持つ最内 node
  (member access)へフォールバックし、既存の `FindObject` StaticRef HACK が発火する。
  `Project.Type.n` 本体は機能変更なし。
- 実測: static qualifier `E7Probe` — hover null → **`E7Probe`**、definition 0 → **1 件**
  (fixture 内の module 宣言)。probe assertion で固定。
- matrix 追加(38 §6 N2.2 の hover / definition 分離): 外部型注釈
  `System.Text.StringBuilder` — hover は **解決**(`System.Text.StringBuilder`)、
  definition は **0 件 = 正**(workspace に source が無い。既存
  `DefinitionExternalEmptyAsync` と同じ意味論)。project 型注釈 `Box`(§2.3 で E7-D へ
  再分類済み)は回帰していないことも assertion 済み。

## 5. N2.4: R-R / E8 — type references の semantic walk(engine 共有ソース)

### 5.1 根因

`Analyser.FindTypeUsages` は parsed `TopDeclaration` tree を `ExprDeclWalker` で歩いていたが、
この走査は field / event / property の型位置や method の戻り値型を**一切訪問しない**。
さらに visitor 内の「`tb is TypeBuilder`(宣言自身)」チェックは `Decl.Type` 起点の走査からは
構造的に到達不能で、**宣言起点ですら 0 件**という実測(38 §4.3)と一致する。加えて
method body は遅延型付けのため、`EnsureCompiled()` を呼ぶまで `BodyParsed` の
node に TypedObject が乗らないという第 2 の欠陥もあった。

### 5.2 実装

- `VsIntegration\...\CodeModel\Static.Analysis.n`: `FindTypeEntriesVisitor` を廃止し、
  新実装 `FindTypeUsagesInTypeBuilder` へ置換。候補 `Decl.Type` ごとに **compiled
  member list**(`TypeBuilder.GetDirectMembers()`)を直接歩き:
  - 宣言自身(partial 部ごとの name location、Definition として)
  - field / event 型、property の戻り値・indexer パラメーター型、method の戻り値・
    パラメーター型(いずれも parsed 型位置 node の TypedObject を目標型と照合)
  - compilable な method body(`EnsureCompiled()` 後に `BodyParsed` を `ExprWalker` で
    walk — local 注釈、generic 型引数位置(`Hashtable.[string, SMap]`)、constructor 呼び出し、
    pattern(`Pattern.HasType` / `Pattern.Application`)、static-member-access qualifier 位置)
  - nested type と variant option へ再帰(variant option 上の field も基底型起点から到達)
  を収集する。visited set で partial / 重複 root を除去。request-time の正しさ優先
  (index / cache なし。38 §5.3 の方針どおり)。
- `GetUsages` / `FindUsages` / `HighlightUsages` のシグネチャは不変。`HighlightUsages` の
  same-file filter(`Location.FileIndex` の後段 filter)も維持。

### 5.3 実測

- Sokoban `SMap`: 0 件 → **55 件**。field / local / parameter / return / generic 引数の
  型注釈、constructor 呼び出し、static-member-access qualifier、宣言を含む。5 source の
  literal 出現の全数と独立照合(56 個目の出現はコメント内で、AST 外として正しく除外)。
  **注釈起点(splayheap.n:7:39)と宣言起点(sokoban.n:107:18)で完全に同一の集合**。
  `includeDeclaration=false` は宣言のみを除いた 54 件。すべて probe assertion で固定。
- 自己完結 matrix fixture(`Widget` / `Container` / `Holder`: field 注釈 / local 注釈 /
  parameter 型 / return 型 / ctor 呼び出し × 別 class): 宣言起点・field 注釈起点の双方で
  9/9 同一集合を assertion。
- positive control: 別型 member `Ping` 3 件(宣言 + 2 usage、両起点)を assertion に昇格 —
  回帰なし。既存の References シナリオ(local 変数)は期待値変更なしで PASS。
- 38 §2.2 の確定事項の再確認: `UsagesInCurrentFile` は `GetUsages` と同一実装に流れる
  `//!!!` 状態のまま(LSP からは未使用。documentHighlight 実装時に E1 バックログで扱う)。

### 5.4 スコープ境界(38 §6 N2.4 のとおり)

- **ProjectReference 先 source の横断は本 WP のスコープ外**。現在の engine workspace は
  単一 project であり、参照先 project の source は探索対象に入らない。multi-project
  workspace(E2)の設計判断として後続へ送る(rename の前提に含めるかは 36 §10-3 の
  バックログ着手時に PO 判断)。
- rename / codeAction 自体は未実装のまま(バックログ E1)。本 WP の references 完全性が
  「rename を安全に出せる水準」に達したかの判定は §7 に記録する。

## 6. 回帰ゲート一式

| 対象 | 結果 |
|---|---|
| testsuite 全数(636) | **PASS 601/636**(positive 435/469 + negative 166/167)— 30/37 §のベースラインと同数、**新規 regression 0** |
| stage2 × 2 独立ビルド(Stage2a vs Stage2b) | **4/4 完全バイト一致**(`compare-stage.ps1`、マスク無し) |
| stage3 vs stage4(自己ホストのフィックスポイント) | **4/4 完全バイト一致**(stage2 ≠ stage3 は既知の世代差 — 37 §4.2) |
| bootstrap 制約(boot-4.0 → Stage1 フルリビルド) | **PASS**(CLR4 msbuild で新 `MainParser.n` を含む全ソースがコンパイル可。1.2.0.621) |
| CLR4 スモーク(Stage1\ncc.exe をネイティブ CLR4 実行) | **PASS**(hello: `Hello from stage-test!` / hello2: `1` → `2, 4, 6`、exit 0) |
| WP-N2 probe(`--wp-n2-probe`、全 assertion) | **PASS**(R-H 4件 / E7-D / E7-R / R-R `SMap` 55件×2起点 / matrix 9件×2起点 / `Ping` 3件×2起点 / R-P 診断0) |
| raw LSP 全 29 シナリオ | **PASS**(最終ソース状態で再実行) |
| ProjectInfo.Test(unit + `--integration`) | **PASS**(HoverMarkup 追加 12 assertion 含む) |
| pack(`pack-tool.ps1 -Pack -PackageVersionSuffix preview.2`) | 成功(freshness OK 1.2.0.621、`1.2.621-preview.2` — 同一 base の再配布のため規約どおり N+1。§7-7 参照) |
| npm スイート(ci / check-types / lint / test / test:integration / test:sdk / package / test:vsix / test-bundled-server) | **PASS**(bundled server は `-VsixPath ..\dist\release\vscode-nemerle-0.8.2.vsix` 明示 — 37 §9-2 の既知の食い違い) |
| `npm audit`(通常 + `--omit=dev`)/ `dotnet` 脆弱 package | 0 vulnerabilities |
| `git diff --check` | **PASS**。CRLF 収載の legacy `VsIntegration` ソースでは追加行の CR が trailing whitespace として flag される(過去に engine の CRLF ファイルを触ったコミットも同様)ため、`.gitattributes` に `VsIntegration/** whitespace=cr-at-eol` を追加して「CR は行終端の一部」を宣言した。実スペースの trailing whitespace は引き続き flag される |

生成物の注意: `dotnet-port\rsp\stage2\*.rsp` の機械的な揺れは HEAD 状態へ復元(37 §9-6 の運用)。
決定性検証の証跡ディレクトリは `bin\Release\core\{Stage2a, Stage2b, Stage3, Stage4}`。

## 7. 既知の制約 / 次 WP への境界

1. **外部型の definition は空結果のまま(正)**: workspace に source が無いため。hover は
   解決する(§4.2)。生成 source 表示(`GenerateCode` 相当)は従来どおり非ゴール(33 §既知の制約 3)。
2. **ProjectReference 先 source の references 横断は未対応**(§5.4)。usage 収集は現在の
   engine workspace(単一 project)内で完結する。multi-project workspace(E2)側の設計判断。
3. **macro 展開でのみ生成される位置**は type references の対象外(parsed tree 上に見えない
   位置)。ただし実測では `foreach (map : SMap in succ)` のような macro 経由の注釈位置も
   parsed tree 上にあり正しく解決された(Sokoban で実欠落は観測されず)。理論上の caveat
   として記録。
4. **rename 前提の判定**: current project 内に限れば、type / member / local の usage 収集は
   両起点で同一・全数(§5.3)であり、**単一 project スコープの rename の前提は満たした**と
   判断する。ProjectReference 横断(制約 2)が解決するまで、cross-project シンボルの
   rename は有効化しない(バックログ E1/E2 の実装時に本判定を引き継ぐ)。
5. **`UsagesInCurrentFile` の `//!!!`(`GetUsages` と同一実装)は現状維持**: LSP からの
   消費者が無く、documentHighlight(E1)実装時に `HighlightUsages`(same-file filter 済み)を
   使う方が実態に合うため、本 WP では触らない。
6. **K-C(WP-K legacy completion 6 FAIL)は本 WP の対象外**(38 §2.3 の判定どおり、
   参照 assembly / runtime 型集合差の問題で E7/E8 と独立。runtime 別期待値化はバックログ)。
7. **コンパイラー版は 621 のまま**(HEAD 未コミットでの Stage 再構築のため版は進まない)。
   本コミット後に stage スクリプトを実行すると A2 版チェックが Stage1 フルリビルドを
   要求する — WP-N1 §9-7 と同じ意図された動作。local の `1.2.621-preview.2` package と
   再生成 VSIX はテスト用候補であり、公開セット(release-info.json = 601 世代)は不変。
   公開時は WP-N1 §10 の規約(確定コミット → Stage 再構築 → pack → 封緘)に従う。

## 8. 共有ソース変更の影響確認(36 §5-5 + sharpdevelop 拡張)

38 §5.6 の方針に従い、変更前に repo 全体を grep して共有ソースの消費者を確認した。

- `MainParser.Parse`: 既存の 1/2 引数呼び出しは `ncc\parsing\Parser.n`(ncc 本体の
  DefaultParser)、`IntegrationDefaultParser.n`(LSP engine)、
  `tools\nemerlish\eval.n`(method-group 代入 `ParsingPipeline = MainParser.Parse` —
  arity 1 のため overload 追加後も一意)、`snippets\VS2010\...\NemerleCodeParser.n`。
  overload 追加のみでシグネチャ・既定挙動とも不変のため全消費者に影響なし。
- `Project.FindUsages` / `FindObject` は private、`GetUsages` / `HighlightUsages` は
  internal(消費者は同一 assembly の `Engine-GetGoToInfo.n` / `Engine-HighlightUsages.n` と
  VS2010 側 `NemerleViewFilter.cs` → `BeginHighlightUsages` 経由)。シグネチャ変更なし。
- `snippets\sharpdevelop\Nemerle.SharpDevelop` は `VsIntegration\...\Nemerle.Compiler.Utils.csproj`
  を ProjectReference するが、直接利用は `BeginGetQuickTipInfo` / `QuickTipInfo` /
  `GotoInfo` 型のみ(grep で `GetUsages` / `FindObjectEverywhere` の使用なし)。
  シグネチャ変更がないため source 互換。挙動変化は「null を返していた位置が結果を返す」
  「取りこぼしていた usage を返す」方向のみ。
- **`snippets\VS2010\` は VsIntegration の共有参照ではなく独立したコピー**(engine 一式が
  別ファイルとして存在)であることを確認した。今回の VsIntegration 側変更はこのコピーには
  及ばない(38 §5.6 の「SharpDevelop binding が同一ソースを共有」は VsIntegration 側の
  記述としては正しいが、snippets\VS2010 は対象外)。

## 9. 再現コマンドと結果

33 / 35 / 37 §再現コマンドを踏襲。engine / コンパイラー共有ソース改修のため全回帰ゲートを実行。

```powershell
# --- Stage チェーン(MainParser 変更の反映。HEAD 未コミットなので版は 621 のまま)---
pwsh dotnet-port\build-stage2-core.ps1                       # 正準 Stage2(freshness OK 1.2.0.621)
dotnet build-server shutdown                                 # dist/ncc の lock 解除(MSBuild ノードが掴む)
pwsh dotnet-port\pack-tool.ps1                               # dist/ncc 更新

# --- bootstrap 制約 + CLR4 スモーク(boot-4.0 で新 MainParser.n がコンパイルできること)---
Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
pwsh dotnet-port\refresh-stage1-core.ps1
bin\Release\net-4.0\Stage1\ncc.exe -out:hello.exe hello.n;  .\hello.exe    # Hello from stage-test!
bin\Release\net-4.0\Stage1\ncc.exe -out:hello2.exe hello2.n; Copy-Item bin\Release\net-4.0\Stage1\Nemerle.dll .; .\hello2.exe  # 1 / 2 4 6

# --- 決定性(WP-N1 の比較スクリプト)---
pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2a
pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2b
pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage2a -DirB bin\Release\core\Stage2b   # 4/4 一致
pwsh dotnet-port\build-stage2-core.ps1 -Compiler bin\Release\core\Stage2\ncc.exe -OutDir bin\Release\core\Stage3
pwsh dotnet-port\build-stage2-core.ps1 -Compiler bin\Release\core\Stage3\ncc.exe -OutDir bin\Release\core\Stage4
pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage3 -DirB bin\Release\core\Stage4     # 4/4 一致
pwsh dotnet-port\build-stage2-core.ps1                       # rsp を正準状態へ戻す

# --- testsuite ---
pwsh dotnet-port\run-testsuite-core.ps1                      # 601/636(ベースライン同数)

# --- LSP / engine / probe ---
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll --wp-n2-probe
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll

# --- pack(同一 base 621 の再配布のため規約どおり preview.2)+ package 依存テスト ---
dotnet build-server shutdown
pwsh dotnet-port\pack-tool.ps1 -Pack -PackageVersionSuffix preview.2
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration

# --- extension 一式 ---
pwsh dotnet-port\vscode-nemerle\pack-server.ps1
Push-Location dotnet-port\vscode-nemerle
npm ci; npm run check-types; npm run lint; npm test
npm run test:integration; npm run test:sdk; npm run package; npm run test:vsix
pwsh .\test-bundled-server.ps1 -VsixPath ..\dist\release\vscode-nemerle-0.8.2.vsix -NoBuild
npm audit; npm audit --omit=dev
Pop-Location
```

## 10. バックログ起票メモ(38 §5.1 メモの持ち越し)

WP-N 完了までに 36 §10 バックログ(E9)へ次の 2 点を明示項目として起票する(38 §5.1 の
仮予定を維持。36 はドラフトのため本ログでは記録のみ):

1. completion `documentation` の Markdown 化(現状 `MarkupKind.PlainText` 固定)。
2. hover Markdown の構造化(engine 擬似 markup の `<keyword>` / `<params>` 等を利用した
   Roslyn 風レイアウト。engine 無改修で `HoverMarkup` 変換層のみで実現可能な見込み)。
