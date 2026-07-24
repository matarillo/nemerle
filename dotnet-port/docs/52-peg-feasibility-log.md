# 52. Nemerle.Peg / Nemerle.Peg.Macros 移植実験ログ

実施日: 2026-07-24

ブランチ: `wip/dotnet-port`

WP 番号なしの単発実験(PO 依頼「`snippets\peg-parser\Nemerle.Peg.Macros` と
`snippets\peg-parser\Nemerle.Peg` を port できるか実験して」)。**計画文書ではなく実験記録**であり、
`00-PLAN.md` / `47-wp-o-plan.md` への反映は行っていない。

## 結論

**移植できる。共有ソース(`snippets\peg-parser\**`)は 1 行も変更していない。** `.nproj` を
dotnet-port 形式で書き起こすだけで、両アセンブリとも .NET 10 の core ncc でビルドでき、
生成されたパーサーは .NET 10 上で正しく動作する。

決定的な証拠は **CLR4 対照実験**である。同一ソースを legacy .NET Framework ツールチェーン
(`bin\Release\net-4.0\Stage1\ncc.exe`)でもビルドし、5 本のドライバーすべてで**実行時出力が
バイト単位で一致**した。移植による挙動変化は検出されなかった。

| ドライバー | net10 vs CLR4 |
|---|---|
| SimpleSpike(自作の最小文法) | IDENTICAL |
| EmitDebugSrcSpike(`Options = EmitDebugSources` 単離) | IDENTICAL |
| CalcSpike(`snippets\...\Calculator\CalcParser.n`) | IDENTICAL |
| JsonDemo(`snippets\...\Json` 3 プロジェクト) | IDENTICAL |
| JSParserDemo(`snippets\...\JSParser`、最大文法) | IDENTICAL |

観測された不具合 2 件は、いずれも**移植起因ではない**(下記 §3・§4)。

## 1. 成果物

`dotnet-port/PegFeasibility/` に 8 プロジェクト。WP-K(`LspFeasibility`)の前例に倣い、
**`.n` ソースは `snippets\` を相対 `@(NemerleCompile)` で参照するだけでコピーしない**
(legacy `.nproj` と単一ソースを共有し続ける)。

移植対象:

- `Nemerle.Peg/` — ランタイム側。参照は ncc の auto-ref のみで足りる(標準ライブラリしか使わない)。
- `Nemerle.Peg.Macros/` — マクロ側。`<NemerleMacroLibrary>true</NemerleMacroLibrary>` で
  `Nemerle.Compiler.dll` を得る(Sokoban サンプルと同型)。
  **`Nemerle.Peg` は参照しない**: ランタイム型(`NToken` / `Located` / `SourceSnapshot`)への言及は
  すべて quotation 内か `LookupTypeInfo` 文字列で、消費側のコンパイル時に解決されるため。

消費側(移植可能性の実証):

- `SimpleSpike/`、`EmitDebugSrcSpike/` — 切り分け用に自作した最小文法。
- `CalcSpike/` — `Calculator\CalcParser.n` をそのまま使用(`Main.n` のみ新規)。
- `Nemerle.Json/` + `Nemerle.Json.Macros/` + `JsonDemo/` — `Json` の 3 プロジェクトを移植。
  `Nemerle.Json.Macros` は「マクロライブラリでありながら別の Nemerle プロジェクトの通常の
  消費者でもある」形で、参照の張り方の 2 つ目の形を確認できる。
- `JSParser/` + `JSParserDemo/` — 435 行の文法 + 281 行 AST。マクロの rule compiler / FSM /
  optimizer への負荷試験。**警告 0 で 5 秒**でビルドされた。

`JsonDemo` は 9/9 PASS(入れ子・リテラル・エスケープ・`\u` シーケンス・コメント・空コンテナー・
不正入力の拒否)。印字は正規形を返すため、入力テキストとの直接比較ではなく
**parse → print → re-parse → print の不動点比較**で判定している。

## 2. ビルド時の注意

`Options = EmitDebugSources` を使う文法(`CalcParser.n` / `JsonParser.n` / `JSParser.n` は
いずれも使用)は、**常駐 MSBuild ノード(node reuse)と組み合わせるとインプロセス
`NccCompile` タスクが不安定になる**。§3 参照。回避は以下のいずれか:

- `-nr:false`(node reuse 無効) — 実測 5/5 安定
- `-p:NemerleUseExec=true`(WP-A3 の `<Exec>` フォールバック) — 実測 5/5 安定

本実験のビルドはすべてこのいずれかを付けて行った。

## 3. 発見 1: `EmitDebugSources` × MSBuild node reuse でインプロセスタスクが落ちる

**これは移植側(dotnet-port)の実在の不具合**だが、PEG 固有ではなく `EmitDebugSources` 固有。

`Options = EmitDebugSources` は `TypeBuilder.DefineWithSource`
(`ncc/hierarchy/TypeBuilder.n:593`)経由で `TypesManager.GenerateFakeSourceCode`
(`ncc/hierarchy/TypesManager.n:467`)を呼び、生成コードを
`obj\<Cfg>\<tfm>\_N_GeneratedSource_<name>.n` に書き出したうえ **ReadOnly 属性を立てる**
(`SaveGeneratedSourceFile`、同 397-424 行)。

実測(各 5 回、毎回 bin/obj 削除):

| 条件 | 結果 |
|---|---|
| `EmitDebugSrcSpike`(EmitDebugSources、自明な文法)インプロセス・node reuse 既定 | `ok ok CRASH ok CRASH` |
| `SimpleSpike`(同一文法、`Options = None`)インプロセス・node reuse 既定 | `ok ok ok ok ok` |
| `EmitDebugSrcSpike` インプロセス・`-nr:false` | `ok ok ok ok ok` |
| `EmitDebugSrcSpike` `<Exec>` 経路 | `ok ok ok ok ok` |
| 既存サンプル `Sokoban` / `RefDemo`(対照) | 各 `ok ok ok ok ok` |

つまり **`EmitDebugSources` + node reuse** の組み合わせでのみ発症する。症状は 2 通り:
MSBuild 子ノードの突然死(`MSB4166`)と、常駐ノードが `Nemerle.Peg.dll` を掴んだままの
コピー失敗(`MSB3027`/`MSB3021`、ロック元は `.NET Host`)。`-t:Rebuild` を無清掃で繰り返すと
`ok CRASH ok CRASH ok` と交互になり、**同一ノード内 2 回目のコンパイルで落ちる**挙動と整合する。

根本原因の特定までは行っていない(この実験のスコープ外)。ReadOnly ファイルと
collectible ALC の相互作用が疑わしいが未確認。既知の関連事象として 39 §11 の
「CoreEmit × 常駐 MSBuild 衝突」がある。

## 4. 発見 2: 拡張規則(`[Extensible]` / `is expr`)は上流で未完成

`CalcSpike`(`Calculator\CalcParser.n` の `CalcParser` クラス)は **compile は通るが実行時に
すべての入力で `pos=-1`**(パース即失敗)を返す。

**移植起因ではない。** CLR4 対照実験で**同一の出力**が得られた。さらに:

- 両ツールチェーンが**同一の警告**を出す —
  `__GENERATED_PEG__RULE__expr__(pos : int, text : string, result : ref int)` の
  `pos` と `text` が未使用(N168)。拡張規則の本体が生成されていないことを示す。
- `snippets\peg-parser\TODO.txt` の項目 5「Create parser extention tecnology」が未完了のまま。
- `ExtensionRuleBase.n` / `IGrammar.n` は `Nemerle.Peg.Macros` のどこからも参照されていない
  (旧設計の残骸)。
- `[Extensible(a)]` が指す曖昧性ハンドラー `a` は `CalcParser` に定義すら存在しない。

同様に `JSParserDemo` の 11 ケース中 4 件(`if/else`・`for`・`while`・オブジェクトリテラル)が
パース不能だが、これも CLR4 と完全一致で、当該 JS 文法の上流での未完成部分である。

拡張規則を使わない文法(`SimpleSpike` / `Json` / `JSParser` の大半)は問題なく動作する。

## 5. その他の観測

- 唯一の新規警告は `GrammarException.n:10` の N618
  (`Exception(SerializationInfo, StreamingContext)` が .NET 9+ で obsolete)。
  コンストラクターは実在するのでビルドは通る。`BinaryFormatter` は使っていないため
  `Nemerle.Linq` の `serialize.n`(WP-N3 で救済不能と判定)のような詰みではない。
- `PegGrammarOptions.EmitDebugSources` は module の可変状態で、いったん真になると
  同一コンパイル内の後続の文法すべてに波及する(リセットされない)。上流の設計上の癖。

## 6. 再現コマンド

```powershell
$base = "dotnet-port\PegFeasibility"
dotnet build "$base\JsonDemo\JsonDemo.nproj"         -p:NemerleUseExec=true -nr:false
dotnet exec  "$base\JsonDemo\bin\Debug\net10.0\JsonDemo.dll"

dotnet build "$base\JSParserDemo\JSParserDemo.nproj" -p:NemerleUseExec=true -nr:false
dotnet exec  "$base\JSParserDemo\bin\Debug\net10.0\JSParserDemo.dll"
```

CLR4 対照実験は `bin\Release\net-4.0\Stage1\ncc.exe` に同じソースを渡し、
`Nemerle.dll` を出力先へ copy して実行する(本ログ作成時の作業ディレクトリーは削除済み)。

## 7. 残課題(この実験では扱っていない)

- §3 の `EmitDebugSources` × node reuse クラッシュの根本原因特定と修正。
- `Calculator\CalcTestes.n` と `Json\Nemerle.Json.Tests`(NUnit 依存)は未移植。
- `snippets\peg-parser` の残り(`Doc` / `FSMTest` / `JSParser.GUI`)は未着手。
  `JSParser.GUI` は WinForms 依存。
- 配布そのものの可否と設計は §8〜§10 に整理した(いずれも未決)。

## 8. 配布設計の前提

以降 3 節は**実装ではなく判断材料**。PO 合意前で結論は確定していない。

### 8.1 Peg と Linq は独立している

`Nemerle.Peg` を配布するために `Nemerle.Linq` のビルド方法を変える必要はなく、逆も成り立たない。
両者が共有しているのは版ピン機構(`version-pin.ps1`)と梱包経路(`pack-tool.ps1`)だけで、
どちらも**片方にだけ適用できる**。したがって §9 と §10 は任意の順序で、片方だけでも着手できる。

共通するのは §8.2 の版モデルという語彙だけで、実装上の結合はない。

### 8.2 版が強制するもの / 自由なもの

| | 内容 | 選択の余地 |
|---|---|---|
| **参照する版** | ビルドに使ったコンパイラーの `Nemerle.dll` / `Nemerle.Compiler.dll` がそのまま焼き込まれる | **なし** |
| **自身に刻まれる版** | 出力アセンブリーの `AssemblyVersion` | **あり** |

自身の版に課される要求は 2 点だけである。

1. **決定的であること**(`git describe` 由来は不可 — コミットごとに動き再現性が壊れる)
2. 配布済みの版を別内容で再発行しないこと

**「ツールチェーンの版と等しいこと」はこの 2 点からは導けない。** 固定値でも両方を満たせる。

版が**担っていないもの**: 世代ズレの検出。WP-N7 の版ピン以降、古いコンパイラーでビルドしても
`version.txt` の値が刻まれるため、版は陳腐化を検出しない。検出は provenance JSON の commit 照合
(`assembly-version-check.ps1` の `Test-NemerleProvenanceCommit`)が担う。

版が**実際に担っているもの**: 互換性の signalling。`Nemerle.Compiler.dll` を参照するアセンブリーは
そのツールチェーン世代でしか動かない(別世代では macro 展開時に `LoadFrom` が失敗する)。
ただし signalling の本来の担い手は**版番号ではなく NuGet 依存**である。

## 9. Nemerle.Peg / Nemerle.Peg.Macros を配布する場合

### 9.1 配布可否そのものの判断材料

版やビルド方法より先に決めるべき事項。否なら §9.2 以降は不要になる。

- **§4 の拡張規則の未完成が最大の懸念。** `[Extensible]` / `is expr` は広告された機能でありながら、
  コンパイルは通り実行時に黙って `pos=-1` を返す。配布するなら (a) 制限として明記するか、
  (b) `[Extensible]` を使った時点でコンパイル エラーにするか。(b) は共有ソース改変になるため
  dotnet-port の「共有ソース無改造」原則との兼ね合いを要判断。
- `snippets\` は WIP ツリー(`TODO.txt` 項目 5 が未完了、JSParser の 4 構文も未完成)。
  `Nemerle.Linq` が本流 `Linq\` にあり classic 配布物にも含まれていたのに対し、Peg は contrib 相当。
- 保守コスト: リリース セットが 1 つ増えると pack / 版 / provenance / CI / README 版リテラル置換の
  すべてに乗る。実利用者が見えていない段階で払うかどうか。

### 9.2 パッケージ構成

2 アセンブリーを 1 パッケージに収め、消費者は `<PackageReference>` 1 行だけで使える。
どちらの構成でも消費者側の記述は変わらないが、マクロ アセンブリーの扱いが異なる。

| 構成 | マクロ アセンブリーの配置 | ncc への渡り方 | 型がスコープに入る | 出力にコピー |
|---|---|---|---|---|
| **推奨** | `macros/` + `build/<id>.props` | `-macros:` | **しない** | **しない** |
| 簡易 | `lib/net10.0/` | `-ref:` | する | する |

ncc の `-ref:` はマクロも登録する(`external\LibraryReference.n` の `LoadContents` が
`LoadTypesFrom` と `LoadMacrosFrom` の両方を呼ぶ)ため、簡易構成でも `[PegGrammar]` は機能する。
`-macros:` は「マクロだけ登録し、型はスコープに入れない」引き算のオプション
(`CompilationOptions.n` の `-macros` ヘルプ)。

推奨構成の内訳:

- `lib/net10.0/Nemerle.Peg.dll` — ランタイム。通常の参照資産。
- `macros/Nemerle.Peg.Macros.dll` — 非標準フォルダーなので NuGet は参照資産にしない(pack 時に
  NU5100 が出るが意図どおり)。
- `build/<id>.props` — 消費者の `obj\*.nuget.g.props` に自動 import され、次を追加する。

  ```xml
  <ItemGroup>
    <NemerleMacroReference Include="$(MSBuildThisFileDirectory)../macros/Nemerle.Peg.Macros.dll" />
  </ItemGroup>
  ```

  `@(NemerleMacroReference)` は WP-A4 で追加された既存の配線(`Nemerle.Core.targets` が
  `NccCompile` の `MacroReferences` と `<Exec>` フォールバックの両方で `-macros:` に展開する)。
  **targets / タスク / ncc のいずれにも変更は不要。**

推奨構成を実測で確認した結果 — ncc コマンドラインが
`-ref:...\lib\net10.0\Nemerle.Peg.dll` + `-macros:...\macros\Nemerle.Peg.Macros.dll` になり、
消費者のビルド・実行が成功し、`Nemerle.Peg.NameRef`(Macros にのみ存在する型)の参照は
`unbound name` エラーになり、出力ディレクトリーに `Nemerle.Peg.Macros.dll` は現れない。

`Nemerle.Peg.Macros` は `Nemerle.Peg` を参照しない(実測)ため、2 つのアセンブリーの間に
版結合は存在しない。

なお `Nemerle.Linq` は 1 つの dll がマクロとランタイムを兼ねるため分離の余地がなく、
`lib/net10.0` 配置(簡易構成に相当)以外の選択肢がない。分離できるのは Peg 固有の利点である。

### 9.3 ビルド方法の選択

| 選択肢 | 評価 |
|---|---|
| `build-libs-core.ps1` に rsp を追加 | 既存に揃うが、Peg には rsp の複雑さの理由が無い |
| `.nproj` + `dotnet build` | §1 で実証済み。障壁は版ピンのみ |

実測のとおり **Peg は auto-ref だけでビルドでき、`-no-stdlib` も `$CoreRefs` 12 個の明示 `-ref:` も
不要**である。rsp にしても中身はほぼ空(`-target:library` / sources / `-out`)になり、
Linq の rsp が抱える複雑さは Peg には存在しない。

推奨は **`.nproj` を真実の源とし、リリース経路のスクリプトが env 版ピンを撒いてから
`dotnet build` を呼ぶ**形。版ピン / A2 / provenance のゲートは 1 箇所に残り、IDE 支援・増分ビルド・
PDB・`dotnet clean` が付く。

署名は legacy `.nproj` が `Nemerle.snk` で `SignAssembly` している。core では公開鍵のみ埋め込み
(WP-C)になるが、targets に第一級の配線が無く `NemerleAdditionalOptions` 経由になる。

### 9.4 版の考え方

| 案 | 内容 | 評価 |
|---|---|---|
| **A. lockstep** | `version.txt` にピン(1.2.0.635) | 既存機構をそのまま使える。Peg 無変更でも版が動く |
| **B. 固定版** | `AssemblyVersion` を固定値 + パッケージ版でセマンティクスを表現 | **NuGet 依存とセットでのみ成立**(下記) |
| **C. ピン無し** | `git describe` 任せ | **却下**(非決定的、実測 1.2.0.658) |

**案 B を単独で採ってはいけない。** `Nemerle.Peg.Macros` は `Nemerle.Compiler.dll` を参照するため
ツールチェーン世代に強く結合しており(§8.2)、固定版にすると「動かない組み合わせを版が警告しない」
状態になる。案 B は `Nemerle.Runtime.Unofficial` への NuGet 依存で制約を表明することとセットで
初めて成立する。それが無いなら案 A のほうが実務的である。

案 A の代償は 2 つ。Peg が無変更でも版が動きセマンティック バージョニングができないこと。
そして `version.txt` の bump 規約(**マクロ ABI 破壊時のみ**)との衝突 — Peg の API が壊れても
コンパイラーの ABI が無事なら `version.txt` は動かず、**中身の違う `Nemerle.Peg.dll` が同一
`AssemblyVersion` で 2 度配布されうる**(パッケージ版は `preview.<N+1>` で逃げられるが
`AssemblyVersion` では残る)。

案 B を採る場合、`Properties\AssemblyInfo.n` を `@(NemerleCompile)` から除外して dotnet-port 側に
差し替えを置けば、**共有ソース無改造のまま**実現できる(WP-K の `CodeDomStubs.n` と同じ手口)。

Peg は**未公開**なので、どちらを選んでも切り替えコストは発生しない。この点で §10.3 の Linq とは
条件が異なる。

## 10. Nemerle.Linq — 見直せる点

### 10.1 現状

- **ビルド**: `build-libs-core.ps1` が `dotnet exec <stage2 ncc> /from-file:<rsp>`。MSBuild 不使用。
- **版**: `version-pin.ps1` で 1.2.0.635 に固定。`pack-tool.ps1:298-301` が梱包時に世代一致を強制。
- **パッケージ**: `Nemerle.Linq.Unofficial`、`lib/net10.0`、NuGet 依存なし。

### 10.2 ビルド方法

rsp の `-no-stdlib` / `-greedy-references:-` / `$CoreRefs` 12 個は、WP-N3 が ncc の auto-ref
(`ncc\passes.n` の `LoadCoreStdlibReferences`)に `System.Linq.Expressions.dll` +
`System.Linq.Queryable.dll` を追加したことで**大部分が冗長**になっている。
両リストは現在まったく同じ 12 個である。

実測: `.nproj` + `dotnet build` が **auto-ref だけで成功し、`linq` 構文と式ツリーの両方が動作**した。

見直す利点:

- **ドッグフーディング** — 現状ツールチェーン自身のライブラリーは targets 経路を一度も通っていない。
- `$CoreRefs` と `LoadCoreStdlibReferences` の**二重管理の解消**(WP-N3 は両方を編集する必要があった)。
- **IDE 支援** — `.nproj` が無いため `Linq\Macro\*.n` の編集では VS Code 拡張の
  project-aware diagnostics が効かない。
- 増分ビルド・PDB・`dotnet clean` が targets の既存配線で付く。

障壁は**版ピンのみ**(`dotnet build` 単体では `$env:GitTag/GitRevision` が撒かれず
`git describe` 由来になる)。現実的な形は §9.3 と同じく、**スクリプトを入口に残したまま中身を
`dotnet build` 呼び出しに替える**こと。ゲートは 1 箇所に残る。

なお `-keyfile` / `-optimize` は targets に第一級の配線が無く `NemerleAdditionalOptions` 経由になる。
Debug 構成では `-debug` と `-optimize` が同居して N10010 警告が出るため、構成ごとの扱いを詰める必要がある。

### 10.3 版の考え方

現在の lockstep が実質的に担っているのは**互換性の signalling** である(`Nemerle.Compiler.dll` を
参照するため世代結合は本物)。したがって版体系そのものは妥当だが、次の 2 点は見直す価値がある。

- **最も見直す価値があるのは NuGet 依存の不在。** パッケージが依存を持たないため、
  ツールチェーン世代の制約が**版番号の慣習だけに乗っている**(README の「same version recommended」)。
  本来は `Nemerle.Runtime.Unofficial` への依存で機械的に表明すべきで、そうすれば
  版の一致は運用規約ではなく NuGet が保証する。
- **`pack-tool.ps1` の世代チェックの意味を取り違えないこと。** 版ピン以降、これが実際に捕まえるのは
  「`version.txt` の bump 後に `Libs` を再ビルドし忘れた」場合だけである。同一スパン内の陳腐化
  (古いコンパイラーでビルドした `Nemerle.Linq.dll`)は素通りし、そちらは provenance の
  commit 照合が担当する。

NuGet 依存を入れれば案 B(固定版)も選択肢に入るが、**`Nemerle.Linq` は既に 1.2.635-preview.1 系で
公開済み**であり、版体系の変更は配布済み番号との整合を要する。未公開の Peg(§9.4)と違って
切り替えコストが高く、積極的な理由が無い限り lockstep 維持が妥当である。
