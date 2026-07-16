# 40. WP-N3: 拡張ライブラリ復活 第1弾(Nemerle.Linq)+ testsuite 救済 — 実装ログ

作成日: 2026-07-17。計画: `36-prerelease-quality-plan.md` §6 WP-N3(ドラフト)。
先行 WP: WP-N1(37)= 決定的ビルド・版チェック、WP-N2(38/39)= engine 品質(世代 1.2.0.626)。

## 1. スコープと着手時宣言(受け入れ基準 3 の「内訳確定」)

計画の成果物 4 点 + オーナー要望 1 点:

1. Nemerle.Linq の core ビルド(rsp 方式の横展開)
2. testsuite 分類 A のうち Linq 起因分の救済
3. BCL 差による期待値ずれ(~9 件)の方針決定と実施(CLR4 検証能力の維持が必須)
4. dist/ncc / Sdk package への同梱可否の判断記録
5. **オーナー要望**: `dotnet-port\packaging\Nemerle.Linq.Unofficial` として NuGet パッケージ配布

**着手時の内訳確定**(18-testsuite-log の分類を精査した結果):

- 分類 A「8+1 件」の内訳: **Linq 6 / Unsafe 1(positive `Issue-git-0397.n`)/ WPF 2(positive+negative `notifypropertychanged.n`)**。
- Linq 6 件のうち `Issue-git-0298.cs` は **C# パーサープラグイン未登録(分類 B)と二重ブロック**
  であり本 WP では救済不能 → 実質の Linq 救済対象は **5 件**
  (`Issue-git-0053.n` `Issue-git-0232.n` `Issue-git-0239.n` `Issue-git-0272-linq-ET.n` `linq-2-ExprTree.n`)。
- BCL 差 9 件 = G 例外メッセージ書式 2(`assert.n` `notnullorempty.n`)/ H double 既定書式 3
  (`basic-value-types.n` `overloading.n` `Issue-git-0274.n`)/ I Obsolete 化 1(`serialize.n`)/
  J ExtensionAttribute 衝突 2(`external-extension-method[-lib].n`)/ K System.Core facade 1
  (`Issue-git-0590-2.n`)。
- **宣言目標: 601/636 → 615/636**。ただし調査の結果 `serialize.n` は救済不能と判明し(§4.3)、
  **確定目標 614/636(positive 449… 実測は §6)**へ修正した。

## 2. Nemerle.Linq の core ビルド

### 2.1 事前監査(サブエージェント調査 + 本体確認)

- `Linq\Macro\` の 7 ソースに CoreCLR 非互換 API は**ゼロ**。`Linq.nproj` の
  System.Windows.Forms / System.Data / System.Xml 参照は**死んだ参照**(ソース側の使用箇所なし)。
  `#if` 分岐なし → NET_4_0 ゲート追加不要。
- `Properties\AssemblyInfo.n` は既に `GeneratedAssemblyVersion("$GitTag.0.$GitRevision")` を使用
  → core ビルドの Nemerle.Linq.dll はコンパイラーと同じ世代版(1.2.0.<rev>)を持つ。
  A2 版整合(assembly-version-check)がそのまま適用できる。
- testsuite は `// REFERENCE: Nemerle.Linq`(`-r:`、`MACRO:`/`-m:` は不使用)で裸名参照。
  ハーネス CWD に dll を置けばコンパイル時(CWD probing)・実行時(exe 隣接)とも解決される
  (CLR4 の `NemerleAll.nproj` CompilerTests が `$(NBin)\Linq\Nemerle.Linq.dll` を
  `Tests\positive/negative` へコピーしていたのと同じ構図)。
- 発見(コンパイラー→Linq の逆方向結合): `ncc\typing\Typer.n:1261` がラムダ→
  `Expression[TDelegate]` 暗黙変換で `Nemerle.Linq.ToExpression` を**名前でハードコード合成**する。
  コンパイラーは無改造でよいが、この経路の動作確認をスモークに含めた(§2.3)。

### 2.2 build-libs-core.ps1(新規)

`build-stage2-core.ps1` の rsp 方式を横展開した**別スクリプト**。設計判断:

- **Stage2 ディレクトリへは出力しない**(出力先 `bin\<Cfg>\core\Libs`)。Stage2 は
  「コンパイラー 4 アセンブリ + CoreEmit + runtimeconfig」という不変条件を保ち、
  `compare-stage.ps1`(固定 4 ファイル比較)と `pack-tool.ps1` の blanket `*.dll` copy に
  影響させない。受け入れ基準 4(stage2/3 一致に影響なし)を構造的に満たす。
- コンパイラーは Stage2(製品)、キーは `Linq.nproj` どおり `Nemerle.Compiler.snk`、
  A2 版チェックを実行前に通す。参照 = stage2 と同じ `$CoreRefs` + `System.Linq.Expressions.dll`
  + Stage2 の `Nemerle.dll`/`Nemerle.Compiler.dll`。rsp は `dotnet-port\rsp\libs\`(untracked)。
- Unsafe / WPF は**対象外のまま**(計画どおりバックログ §10-6 で個別判断)。

### 2.3 スモーク(ビルド一発成功)

明示 `ToExpression`(41)/ Typer.n の暗黙変換経路(101)/ `linq <# from .. where .. orderby .. #>`
構文(2,4,6)/ 実行時 `Expression.Compile()` — すべて CoreCLR で compile+run PASS。

## 3. run-testsuite-core.ps1 の拡張

1. **`-LibsDir`**(既定 `bin\<Cfg>\core\Libs`)を staging 時に compiler ディレクトリへ合流
   → 既存の「`*.dll` を出力 dir へコピー」がそのまま Linq を CWD/実行時に供給。
   Libs 不在時は警告して続行(WP-N3 以前の挙動に退化)。
2. **グローバル参照に `System.Linq.Expressions.dll` / `System.Linq.Queryable.dll` を追加**。
   CoreCLR では旧 System.Core 面が 3 分割されている(Enumerable=System.Linq /
   式ツリー=System.Linq.Expressions / IQueryable 演算子=System.Linq.Queryable)。
3. **`-def:RUNTIME_CORE` を新設**(J の救済に使用、§4.4)。CLR4 側(NemerleAll)は
   `RUNTIME_MS;NET_4_0` のままで無改造。

**K(`Issue-git-0590-2.n`)の原因確定**: `REFERENCE: System.Core` の裸名は
`LibraryReferenceManager` の `_lib_path`(System.Object のアセンブリディレクトリ =
共有フレームワーク)経由で **facade に解決自体は成功**していた(`cannot find assembly` は
出ない)。facade は `GetExportedTypes()` が 0 型のため `AsQueryable` が取り込まれず
「no member named AsQueryable」になるだけ。→ 実体 `System.Linq.Queryable.dll` の
グローバル参照追加のみで解決(**テストファイル無改造**。REFERENCE 行の書き換えは
CLR4 側で `System.Linq.Queryable` という名前が解決できず CLR4 を壊すため不可)。

## 4. BCL 差 9 件の方針と実施

**前提(ハーネスの期待値機構、実装確認)**: `NccTestDescription.Parse` は生テキストを
1 行ずつ正規表現で走査するだけで `#if` を解釈しない。同一プラグマが複数あれば無条件で
全部採用(BEGIN/END-OUTPUT は連結、`E:`/`W:` は物理行キーで両方登録 → 片方は必ず
「hasn't occurred」で失敗)。**期待値プラグマの #if 出し分けは原理的に不可能** →
二重化は「コード側の出力正規化」を主軸に、型宣言の排他だけ `#if` + 新シンボルで行う。
実行時出力は行単位 Trim + Ordinal 完全一致(正規表現なし)、診断(`E:`/`W:`)のみ正規表現。

| # | ファイル | 方式 | 内容 |
|---|---|---|---|
| G-1/2 | assert.n / notnullorempty.n | (c) 出力正規化 | `ArgumentException.Message` の ParamName 表示が Framework「\nParameter name: X」/ Core「 (Parameter 'X')」で行数まで変わる → メッセージを位置情報末尾 `": ."` で打ち切り、`Parameter name:` 行は `e.ParamName` から自前で印字。**期待値ブロックは無変更**、ParamName の検証能力は維持 |
| H-1 | basic-value-types.n | (c) G15 正規化 | `%lf`(既定書式)1 箇所を `ToString("G15", Invariant)` へ。Core 3+ の最短往復表現(5.4000005447844055)を Framework 実効既定(G15 = 5.40000054478441)へ固定 |
| H-2 | overloading.n | (c) G15 正規化 | 61.2 を印字する 2 箇所のみ(実測で 183.6 系・タプル行は両ランタイム一致のため不変)。検証対象はオーバーロード解決で、印字書式は無関係 |
| H-3 | Issue-git-0274.n | (c) 手動展開+G15 | `DebugPrint(Cot(42))` 1 行をマクロ展開相当の `WriteLine("Cot(42) ==> " + …G15…)` へ(`DebugPrint` マクロ本体は共有ライブラリのため触らない) |
| I | serialize.n | (c) `#pragma warning disable/restore 618` | `IObjectReference` の Obsolete 化による N618 を variant 宣言の周囲で抑止(CLR4 では警告が出ないので no-op)。**ただし §4.3 のとおり救済には至らず** |
| J-1/2 | external-extension-method[-lib].n | (a) `#if` 拡張 | pre-3.5 互換シムの条件を `#if !NET_4_0 && !RUNTIME_CORE` へ。CoreCLR corelib は ExtensionAttribute を同梱するためシム宣言が redefinition error。NET_4_0 の「未定義」は .NET 2.0/3.5 と区別できないため専用シンボルを新設 |
| K | Issue-git-0590-2.n | (b') 環境側 | §3(テスト無改造) |
| 追加 | Issue-git-0053.n | (c) 表示正規化 | Core 3+ の `Expression.ToString()` が変換ノードを `Convert(x, Type)` と印字(Framework は `Convert(x)`)→ `.Replace(", Int64)", ")")` で Framework 綴りへ正規化(Framework では no-op)。Linq 起因失敗の解消時に顕在化した第 2 のブロッカー |
| 追加 | linq-2-ExprTree.n | (c) G15 正規化 | タプル出力の double(10.799999999999999 vs 10.8)。H と同種 |

### 4.3 serialize.n の再分類(目標 615 → 614)

N618 警告を抑止すると、**隠れていた実行時失敗が顕在化**した: テスト本体が
`BinaryFormatter.Serialize/Deserialize` を使っており、.NET 9+ では BinaryFormatter が
削除済み(常時 `PlatformNotSupportedException`)。18 の分類 I「期待値ずれ」は誤分類で、
実態は**分類 C(BCL 面の恒久的環境制約)**。テスト目的([Serializable] マクロの
round-trip 検証)を偽装なしで core に載せる方法はないため恒久 FAIL として受容。
`#pragma` 修正自体は意味的に正しく CLR4 で PASS を確認済みのため維持(誤誘導だった
警告失敗を、正直な実行時失敗に変える効果もある)。

### 4.4 CLR4 検証能力の維持(受け入れ基準 2)

改変した 8 テスト(assert / notnullorempty / Issue-git-0274 / overloading /
basic-value-types / serialize / external-extension-method-lib + 連鎖先)を CLR4 ハーネス
(`Nemerle.Compiler.Test.exe` + Stage1 ncc ネイティブ、`-def:RUNTIME_MS;NET_4_0`)で実行:
**8/8 PASS**。期待値ブロックは全ファイル無変更(出力正規化はコードが両ランタイムで
同一文字列を出す方向)。`RUNTIME_CORE` は CLR4 側 msbuild に一切追加していない。

## 5. Linq マクロの移植性修正と ncc auto-ref 拡張(共有ソース変更 2 件)

### 5.1 ToExpressionImpl.n: `TExpr.CtorOf` → 実行時 `Type.GetConstructor`(MakeCtorInfo)

`linq-2-ExprTree.n` が internal compiler error で落ちた:
`ILGeneratorImpl.Emit(OpCode, ConstructorInfo)` が call/callvirt/newobj 以外を拒否
(persisted Reflection.Emit の制限、メッセージ "The specified opcode cannot be passed to
EmitCall")。原因は `TExpr.CtorOf` が `ldtoken <ctor>`(ILEmitter.n:870)へ落ちること。
runtime 側 ILGenerator(CLR4 / CoreCLR の in-memory)は ldtoken+ctor を許容しており、
**persisted 実装だけが過剰制限**= A10/A11 系統の upstream 課題(報告候補としてバックログ)。

対応: マクロが生成する式ツリー構築コードから ctor トークンリテラルを排除し、
`typeof(T).GetConstructor(BindingFlags.Instance|Public|NonPublic, null, array[…typeof…], null)`
の実行時解決へ置換(`MakeCtorInfo` / `MakeCtorInfoFromParms` を新設、`Expression.New` の
2 箇所を置換)。**コンパイラー本体は無改造**。ハマり 2 点:

- 関数型の変種名は `FixedType.Fun`(`Fn` ではない)。
- `FixedType.Tuple` レシーバーへの `TypeOfMember` は assertion で死ぬ(FixedType.n:870)
  → タプル ctor は `TupleType.Make(type)` + 要素型リストを直接渡す別経路にした。

意味論: GetConstructor は CLR4/CoreCLR 同一挙動。コストは式ツリー構築時のリフレクション
1 回。**CLR4 検証**: CLR4 側 Nemerle.Linq を Stage1 コンパイラーで再ビルドし
(`MSBuild Linq.nproj /p:Nemerle=…Stage1`)、Linq テスト 6 本を CLR4 ハーネスで **6/6 PASS**。

### 5.2 ncc/passes.n: auto-ref に System.Linq.Expressions / System.Linq.Queryable を追加

**動機(オーナー要望パッケージの成立条件)**: SDK 消費者の `@(ReferencePath)` はフレーム
ワーク facade を除外して ncc に渡す設計(WP-A2)のため、`Expression[Func[…]]` 型は
**ncc の auto-ref(`LoadCoreStdlibReferences`)が唯一の供給経路**。実証: auto-ref のみでは
`referenced namespace 'System.Linq.Expressions' does not exist`。

**検討した代替案**(採否と理由):

| 案 | 判断 | 理由 |
|---|---|---|
| NccCompile タスクに FrameworkReferences param + package の buildTransitive props | 却下 | タスク/targets/props の 3 部品追加に加え、**LSP エンジンに参照が伝わらず build/IDE parity が壊れる**(エディターだけ Expression 型が未解決になる)。Exec fallback にも非対応 |
| package が System.Linq.Expressions の NuGet package に依存 | 却下 | .NET SDK の conflict resolution がプラットフォーム版(facade)を優先し ReferencePath から除去する公算が高い |
| `NemerleAdditionalOptions` に -ref を書かせて文書化 | 却下 | 消費者毎の手作業 + engine は AdditionalOptions の -ref を適用しない既知 gap(warning のみ) |
| **auto-ref リストへ 2 アセンブリ追加** | **採用** | CLI / MSBuild / LSP エンジン(engine も同じ `LoadCoreStdlibReferences` を通る)が一括で解決。CoreCLR 分岐内のみで CLR4 の else 分岐(mscorlib/System/Nemerle/System.Xml)は不変 |

整合性の根拠: 既存 auto-ref リストは「compiler 自身が必要とする $CoreRefs」だが、
System.Linq.dll(= 旧 System.Core の Enumerable 面)を既に含む。今回の 2 つは
**旧 System.Core 面の残り**であり、CLR4 で `-ref:System.Core` 一発で届いていた面が
CoreCLR で届かない(facade が 0 型)ことへの対応として一貫する。passes.n の
「$CoreRefs と同期」コメントは実態(スーパーセット)に合わせて更新した。

**影響範囲**: testsuite は `-no-stdlib`+明示参照のため auto-ref 変更の影響を受けない。
ConsoleTest(engine ベースライン)は 52/58 のまま変動なし。VsIntegration 側の
呼び出し元確認(§5-5 運用)= LoadCoreStdlibReferences は passes.n 内部からのみ呼ばれる
additive 変更で、VsIntegration へのインターフェース影響なし。

この変更により **Stage リビルドを伴う**(§7 の封緘チェーン)。

## 6. 検証結果(コミット前、世代 1.2.0.626 + dirty tree)

| ゲート | 結果 |
|---|---|
| testsuite 全数 | **positive 448/469, negative 166/167 = 614/636**(baseline 601 → **+13**、宣言目標 614 達成、新規 regression 0) |
| 残 22 失敗の内訳 | B C# パーサー 9(`Issue-git-0298.cs` を A から移動)/ A 残 3(Unsafe 1・WPF 2)/ C BCL 面 6(access-checks, codedom, form, pointer-type-caching, Issue-git-0507, security)+ serialize(BinaryFormatter 削除、I から再分類)/ D CAS 1(security-asm)/ E -res 1(resource)/ F pkg-config 1(gtk) |
| CLR4 スポット(改変テスト) | 8/8 PASS(§4.4)+ Linq 6/6 PASS(§5.1、CLR4 Nemerle.Linq 再ビルド込み) |
| ProjectInfo unit tests | PASS |
| raw LSP integration(WP-L3 全シナリオ+WP-N2 probe) | PASS |
| ConsoleTest(engine) | 52/58 = WP-K ベースライン一致 |
| パッケージ試験 pack | 3 nupkg 生成(Sdk / Templates / **Linq** 1.2.626-preview.1) |
| パッケージ消費 e2e(repo 外 + local feed のみ) | `dotnet build` → `dotnet run` PASS: `linq` 構文 / 暗黙式ツリー変換+Compile / IQueryable+**タプル射影**(MakeCtorInfo 経路)全て動作 |
| CLI auto-ref | `dotnet ncc.exe -ref:Nemerle.Linq.dll prog.n`(BCL 明示参照ゼロ)compile+run PASS |

## 7. パッケージング(オーナー要望の検討結果と実装)

**批判的検討の結論: 採用(スコープ限定)**。

- 賛成理由: 既存 `.Unofficial` 命名・版規約(1.2.<rev>-preview.N = 内包コンパイラー世代)・
  pack-tool 単一入口・local feed 検証フローにそのまま乗る。ライブラリの消費は
  `PackageReference` が SDK の設計上の正道(Sdk package 同梱はツールチェーンと
  ライブラリの境界を壊し、全プロジェクトに Linq を強制する)。
- **同梱可否の判断記録(成果物 4)**: dist/ncc・Nemerle.Sdk.Unofficial への同梱は**しない**。
  独立パッケージ `Nemerle.Linq.Unofficial` として配布。nuget.org への公開は WP-O
  (本 WP は local feed / release set まで)。
- 制約: NuGet 依存関係は宣言しない(Nemerle.dll はツールチェーン供給)。**SDK と同一版の
  セット利用を README で明示**(Linq が toolchain より新しい世代だと Nemerle.dll の
  下位版参照で load 失敗する CoreCLR 挙動)。

実装: `packaging\Nemerle.Linq.Unofficial\`(csproj + README、`lib/net10.0` 配置、
README は `__NEMERLE_SDK_VERSION__` 置換方式)。`pack-tool.ps1 -Pack` に統合し、
**Libs の世代一致チェック**(Nemerle.Linq.dll の AssemblyVersion ≠ layout の Nemerle.dll
なら pack 拒否 = 混在世代の機械検出、A2 と同思想)+ NuGet cache eviction + README staging を
追加。インストールガイド(packaging\README.md)にも Linq 節を追加。

## 8. 既知の制約・バックログ起票

- `Issue-git-0298.cs`: C# パーサープラグイン(B1)ブロック。B1 対応時に自動救済見込み。
- `serialize.n`: BinaryFormatter 削除による恒久 FAIL(分類 C へ移動)。
- persisted SRE の `Emit(Ldtoken, ConstructorInfo)` 拒否: **upstream 報告候補**
  (A10/A11 と同リスト、36 §10-4)。ncc の `TExpr.CtorOf` 自体は残存(Nemerle.Linq 以外で
  CtorOf を式ツリー用途に emit するコードが現れれば同じ制限に当たる)。
- エディター(VS Code)での Linq パッケージ実地(hover / diagnostics)は自動ゲート外。
  engine は auto-ref 経由で Expression 型を解決する設計のため動作見込みだが、
  実機確認は手動テスト(WP-N2 と同じ運用)に委ねる。
- `security-asm.n`(分類 D)にも `RUNTIME_CORE` を使えば救済できる可能性があるが、
  CAS no-op の受容(36 §4)を変えないため本 WP では触っていない。

## 9. 封緘(コミット後の Stage 再構築と release set)

WP-N3 本体コミット = **5245d5345**(共有ソース ncc/Linq マクロ変更を含むため、
規約どおり「確定コミット → その HEAD で Stage 再構築 → pack → 封緘」を実施)。

| 工程 | 結果 |
|---|---|
| Stage1 フルリビルド(CLR4、dir 削除 + `/t:Stage1`)+ refresh-stage1-core | OK、**1.2.0.627** |
| CLR4 スモーク(Stage1 ncc ネイティブ hello / hello2 compile+run) | PASS |
| stage2 ×2 独立ビルド(compare-stage、マスク無し) | **4 アセンブリ完全バイト一致** |
| stage3(stage2 産)vs stage4(stage3 産)fixpoint | **4 アセンブリ完全バイト一致** |
| build-libs-core(Nemerle.Linq) | OK、1.2.0.627(コンパイラーと同世代) |
| testsuite 全数 @627 | **614/636(positive 448/469, negative 166/167)** — コミット前(§6)と同一 |
| pack-tool -Pack | Sdk / Templates / **Linq** = **1.2.627-preview.1**、ncc-info = 5245d5345(clean) |
| 消費 e2e 再検証(repo 外 + local feed、627 版) | build/run PASS(linq 構文・式ツリー・タプル射影) |
| pack-server + `npm run package`(lint + unit + verify-server 込み) | VSIX **0.9.0** 再生成(627 server 同梱) |
| test-bundled-server(-VsixPath 明示) | PASS(WP-L3 全シナリオ) |
| `npm run test:sdk` / `npm run test:vsix` | PASS / PASS |
| `npm audit` | **0 vulnerabilities** |
| pack-release | **封緘済み**: dist\release = 3 nupkg(1.2.627-preview.1)+ vscode-nemerle-0.9.0.vsix + README + release-info.json(commit 5245d5345、1.2.0.627) |

運用ノート:

- **VSIX 0.9.0 は同一版番号のまま内容更新**(627 世代 server)。0.9.0 は未公開
  (local セットのみ)のため WP-N1 のローカル候補運用に従い番号を維持した。
  公開時(WP-O)には extension 版 bump 要否を再確認すること。
- WP-N2 の **1.2.626-preview.1 セットは本セットに置換(superseded)**し、dist\release から
  削除した(公開歴なし。なお本 WP の開発中、試験 pack が封緘済み 626 nupkg を同名で
  一時上書きしていた — 最終的に 626 は全て削除済みで実害はないが、封緘済みセットの
  置き場と作業用 pack の出力が同一ディレクトリである構造は将来の注意点)。
- WSL 側は SDK pin を **1.2.627-preview.1** へ、VSIX 0.9.0 再インストール、
  版切替後は `dotnet build-server shutdown`(CoreEmit 衝突の既知回避)が必要。

## 10. 再現コマンド

```powershell
# Nemerle.Linq の core ビルド(要: Stage2)
pwsh dotnet-port\build-libs-core.ps1

# testsuite 全数(Linq staging + RUNTIME_CORE 込み)
pwsh dotnet-port\run-testsuite-core.ps1

# CLR4 側スポット(改変テスト、CLR4 ハーネス)
# <staging>=Stage1 の dll を置いた作業 dir で:
Nemerle.Compiler.Test.exe -ncc bin\Release\net-4.0\Stage1\ncc.exe `
  -p "-nowarn:10003 -def:RUNTIME_MS;NET_4_0" -output . testsuite\positive\<files...>

# CLR4 Nemerle.Linq の再ビルド(マクロ改修の CLR4 検証)
MSBuild.exe Linq\Macro\Linq.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 `
  /p:Configuration=Release /p:Nemerle=<repo>\bin\Release\net-4.0\Stage1 /p:OutputPath=<out>\

# パッケージ(要: build-libs-core 済み)
pwsh dotnet-port\pack-tool.ps1 -Pack
```
