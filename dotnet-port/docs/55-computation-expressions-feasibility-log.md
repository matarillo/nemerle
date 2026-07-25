# 55. Nemerle.ComputationExpressions 移植実験ログ

実施日: 2026-07-26

ブランチ: `wip/dotnet-port`(HEAD 951829658)

使用ツールチェーン: `dotnet-port\dist\ncc`(`ncc-info.json`: commit 6efbee6fb / Release /
`nemerleAssemblyVersion` 1.2.0.635 / packed 2026-07-23)

WP 番号なしの単発実験(PO 依頼「`snippets\ComputationExpressions` 以下を dotnet-port できるか
検証して」)。**計画文書ではなく実験記録**であり、`00-PLAN.md` / `47-wp-o-plan.md` への反映は
行っていない。52(Peg)と同じ枠組みで実施した。

## 結論

**移植できる。共有ソース(`snippets\ComputationExpressions\**`)は 1 行も変更していない**
(`git status --porcelain snippets/` が空であることで確認)。上流の 5 プロジェクトすべてが
.NET 10 の core ncc でビルドでき、コンソール 3 プロジェクトは実行時にも正しく動作する。

決定的な証拠は 52 と同じ **CLR4 対照実験**である。同一ソースを legacy .NET Framework
ツールチェーン(`bin\Release\net-4.0\Stage1\ncc.exe`)でもビルドし、実行可能な 3 ドライバー
すべてで**判定行が一致**した。移植による挙動変化は検出されなかった。

| ドライバー | net10 vs CLR4 |
|---|---|
| `CompExprTest`(上流 `Test` そのまま、51 ケース) | 判定行 IDENTICAL(51 OK / 0 Failed、exit 0) |
| `SyncCtxSpike`(SynchronizationContext 経路、自作) | 全行 IDENTICAL(OK、exit 0) |
| `ContSpike`(上流が無効化している継続モナド、自作ドライバー) | 全行 IDENTICAL(1 OK / 1 Failed) |

`ContSpike` の 1 件の Failed は**移植起因ではない**(§4)。

移植側の実在の不具合を 1 件発見した(§5、`NemerleAdditionalOptions` の引用符無視)。
これは ComputationExpressions 固有ではなく、空白を含むパスを `-ref:` に渡す全プロジェクトに
影響する。

## 1. 成果物

`dotnet-port\CompExprFeasibility\` に 7 プロジェクト。52(Peg)/ WP-K(`LspFeasibility`)の
前例に倣い、**`.n` ソースは `snippets\` を相対 `@(NemerleCompile)` で参照するだけでコピーしない**
(legacy `.nproj` と単一ソースを共有し続ける)。

移植対象(上流 5 プロジェクトすべて):

| プロジェクト | 上流 | 行数 | 備考 |
|---|---|---|---|
| `Nemerle.ComputationExpressions` | `ComputationExpressions` | 1883 | ランタイム。auto-ref のみで足りる |
| `Nemerle.ComputationExpressions.Macros` | `ComputationExpressions.Macros` | 1684 | `<NemerleMacroLibrary>true</NemerleMacroLibrary>` |
| `CompExprTest` | `Test` | 2625 | コンソール、無改造で実行可能 |
| `AsyncHttp` | `AsyncHttp` | 271 | WinForms。ビルドのみ検証 |
| `WindowsFormsTest` | `WindowsFormsTest` | 287 | WinForms。ビルドのみ検証 |

`**\*.n` グロブは 5 プロジェクトとも legacy `.nproj` の `<Compile>` 列挙と**過不足なく一致**する
(照合済み)。

切り分け用の自作ドライバー 2 つ:

- `ContSpike/` — 上流 `Test\Main.n` が**コメントアウトしている** `ContTest` を走らせる(§4)。
- `SyncCtxSpike/` — WinForms 2 本しか使っていない
  `SystemExecutionContexts.FromCurrentSynchronizationContext()` 経路を、自前のポンプ型
  `SynchronizationContext` でヘッドレスに再現する(§3)。

`snippets\ComputationExpressions\FSharpAsync` は F# の比較実装であり Nemerle ではないので対象外。

### 1.1 参照の張り方

`Nemerle.ComputationExpressions.Macros` は **`Nemerle.ComputationExpressions` を参照する**。
この点で `Nemerle.Peg.Macros`(参照しない)とは異なり、`Nemerle.Json.Macros` と同型である。
`ComputationExpander.n` の `using ComputationExpressions.Internal;` と
`Builders\AsyncBuilder.n` の `using Nemerle.ComputationExpressions.Async;` があるため、参照を
外すと `referenced namespace ... does not exist` で失敗する(実測)。

ただし **出力アセンブリーのメタデータには `Nemerle.ComputationExpressions` への AssemblyRef が
現れない**(実測、下記)。ランタイム型への言及がすべて quotation 内にあり、消費側の
コンパイル時に解決されるためである。**コンパイル時のみの依存**であり、配布時の版結合は無い。

```
Nemerle.ComputationExpressions.dll       -> System.Private.CoreLib 10.0.0.0, Nemerle 1.2.0.635
Nemerle.ComputationExpressions.Macros.dll-> System.Private.CoreLib 10.0.0.0,
                                            Nemerle.Compiler 1.2.0.635, Nemerle 1.2.0.635
```

消費側は legacy `Test.nproj` の `<MacroProjectReference Private="False">` と同じ形、すなわち
`ProjectReference` + `OutputItemType="NemerleMacroReference"` + `ReferenceOutputAssembly="false"`
(WP-A4 の配線)。実測で `Nemerle.ComputationExpressions.Macros.dll` は消費側の出力ディレクトリー
に現れず、`Nemerle.ComputationExpressions.dll` は現れる。

なお `Nemerle.Compiler.dll` / `Nemerle.Macros.dll` はコンパイラー API を使わない
`Nemerle.ComputationExpressions` の出力にも複写される。これは既存の
`PegFeasibility\JsonDemo` や `samples\Sokoban` でも同じで、本実験で生じた事象ではないため
追及していない。

## 2. テストは無改造で動く

上流 `Test` は **NUnit 等のテストフレームワークに依存していない**。`TestExecuter.n` が
`[TestCase]` 属性のテンプレート文字列と `StringWriter` に捕捉した出力を比較し、`Main.n` が
0 / -1 を返す自己完結のハーネスである。したがって 52 で `Calculator\CalcTestes.n` や
`Json\Nemerle.Json.Tests` が NUnit 依存で未移植になったのとは違い、**そのまま無人実行できる**。

実測: **51 ケース中 51 OK、0 Failed、exit 0**。

### 2.1 比較方法

このテストは判定行のほかに、スレッド ID を含む診断行(`Test2.fn(1213) thread id = 6` など)を
`Console` へ直接書く。これは ThreadPool 上の並列実行の副産物で、**同一バイナリーを 2 回走らせても
一致しない**(実測)。よって net10 と CLR4 の比較は

- 判定行(`Start test:` / `Test: <class> <method> OK|Failed`、56 行)の **完全一致**
- 診断行はスレッド ID を正規化してソートしたうえでの一致

の 2 段で行った。どちらも一致した。判定自体は元々ハーネス内の厳密なテンプレート比較なので、
この扱いで判定の厳しさは落ちていない。

## 3. SynchronizationContext 経路の検証(`SyncCtxSpike`)

`AsyncHttp` と `WindowsFormsTest` は GUI アプリなのでビルドしか確認できない。しかし両者だけが
使う `FromCurrentSynchronizationContext()` — `comp async` の継続を単一の UI スレッドへ
マーシャルする経路 — は、コンソール テストが一切踏んでいない。ここを空白にしないため、
専用スレッドで `Post` を順次実行する最小の `SynchronizationContext` を書き、
`WindowsFormsTest\MainForm.n` と同じ形(`SwitchTo(pool)` で計算 → `SwitchTo(gui)` で
"UI" 更新 → 再帰)を回した。

各 "UI" 更新はポンプ スレッド上にいることを自己申告し、生のスレッド ID は出力しないので
結果は決定的である。

```
compute(1): not-gui / publish(1)=144: gui
compute(2): not-gui / publish(2)=233: gui
compute(3): not-gui / publish(3)=377: gui
SyncCtxSpike OK
```

net10 で 5 回連続 exit 0、CLR4 とも**全行一致**。マーシャリングは移植後も正しい。

## 4. 上流が無効化しているテスト(`ContSpike`)— 移植起因ではない

上流 `Test\Main.n` は `ContTest`(ユーザー定義ビルダーによる継続モナド、`ComputationExpander`
に最も負荷が掛かる形)の行だけをコメントアウトしている。`ContTest.n` / `ContBuilder.n` 自体は
コンパイル対象に残っているため、**コンパイルは通るが一度も実行されない**状態にある。

`ContSpike` でこれを走らせた結果:

- `ContTest.Test1` — **OK**
- `ContTest.Test2` — **Failed**、ただし **net10 と CLR4 で出力が全行一致**

`Test2` は上流の書きかけである。`[TestCase(<# #>)]` の期待テンプレートが**空**のまま、本体は
`while` 版がコメントアウトされて再帰 `loop` 版に置き換わっており、実際には 0〜121 を印字して
`asd n > 20` を返す。期待値が書かれていないだけで、移植とは無関係。

52 §4 の `[Extensible]`(コンパイルは通るが実行時に黙って失敗する)と違い、こちらは
**広告された機能の欠落ではなく、単に未完成のテスト 1 件**である。ライブラリー本体の機能で
壊れているものは見つかっていない。

## 5. 発見: `NemerleAdditionalOptions` は引用符を解さない(移植側の実在の不具合)

**これは ComputationExpressions 固有ではない。** インプロセス `NccCompile` タスクは

```csharp
// dotnet-port\Nemerle.MSBuild.Tasks\NccCompile.cs:175
list.AddRange(AdditionalOptions.Split(
    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
```

と `AdditionalOptions` を素の空白で分割する。引用符は解釈されないため、
`-ref:"C:\Program Files\dotnet\..."` は `-ref:"C:\Program` と `Files\dotnet\..."` に割れ、

```
nemerle error : cannot find assembly `"C:\Program'
```

になる。`<Exec>` フォールバック(`-p:NemerleUseExec=true`)はシェルが引用符を解釈するので
正しく通る。**空白を含むパスを `NemerleAdditionalOptions` で渡す限り、既定の経路は使えない。**

`.NET SDK` の既定インストール先が `C:\Program Files\dotnet` である以上、フレームワーク
アセンブリーを手で `-ref:` する用途はすべてこれに当たる。修正は本実験のスコープ外。回避は
`-p:NemerleUseExec=true`。

なお 52 §3 の `EmitDebugSources` × node reuse クラッシュは、PEG を使わない本実験では**再現しない**。
`CompExprTest` を bin/obj 削除つきで既定設定(node reuse 有効・インプロセス)で 3 回ビルドして
`ok ok ok`。

## 6. WinForms 2 本のビルドに必要だったこと

コンソール 3 本は `.nproj` を書き起こすだけで通る。WinForms 2 本には追加の手当てが要った。
いずれも **WinForms / .NET のアセンブリー配置に由来するもので、ComputationExpressions とは無関係**。

### 6.1 WindowsDesktop 参照は targets が落とす

`Nemerle.Core.targets` は `%(ReferencePath.FrameworkReferenceName)` が非空の項目を `-ref:` から
除外する(ncc が共有フレームワークを自前で auto-ref するため)。これは
`Microsoft.NETCore.App` については正しいが、`Microsoft.WindowsDesktop.App` も同じ条件で落ちる。
ncc 側に WindowsDesktop の auto-ref は無いので、**必要な分を手で `-ref:` し直す**必要がある。

`.nproj` 内の `BeforeTargets="CoreCompile"` ターゲットで `NemerleAdditionalOptions` に足した
(結果として §5 に当たり `-p:NemerleUseExec=true` が必須になる)。

### 6.2 ref pack ではなく shared framework の実体を渡す

`@(ReferencePath)` が指すのは `dotnet\packs\...Ref\` の**参照アセンブリー**だが、これを渡すと

```
internal compiler error : System.IO.FileLoadException: Could not load ...
Microsoft.WindowsDesktop.App.Ref\10.0.9\ref\net10.0\System.Drawing.dll.
The located assembly's manifest definition does not match the assembly reference. (0x80131040)
```

になる。ncc は参照を**実際にロードして**リフレクションで読む
(`LibraryReferenceManager.assemblyLoadFrom`)ので、既定 ALC が既に束縛している同一 ID の
実体アセンブリーと衝突する。**`dotnet\shared\Microsoft.WindowsDesktop.App\<ver>\` の実体**を
渡せば通る。版は SDK が解決した ref pack の `%(TargetingPackVersion)` から取り、
ディレクトリーだけ差し替えている。

### 6.3 `System.Drawing` ではなく `System.Drawing.Common`

WindowsDesktop の `System.Drawing.dll` は型転送のファサードで、単純名が
`Microsoft.NETCore.App` 側の同名ファサードと衝突し、**単独で渡しても** 6.2 と同じ
0x80131040 になる。`Font` / `FontStyle` / `GraphicsUnit` の実体がある
`System.Drawing.Common.dll` を名指しすれば単純名が一意になり通る。
`Point` / `Size` / `SizeF` は `Microsoft.NETCore.App` の `System.Drawing.Primitives.dll`。

### 6.4 `System.Net.Requests`

`AsyncHttp` の `WebRequest` は ncc の auto-ref 集合(`ncc\passes.n:592` の `splitAssemblies`)に
無いので `System.Net.Requests.dll` / `System.Net.Primitives.dll` を明示。
`WebRequest.Create` は .NET で obsolete(N618、下記 §7)だが実在する。

### 6.5 `.resx` は埋め込んでいない

両 `MainForm.Designer.n` とも `ComponentResourceManager` に触れていないため、`.resx` を読む
コードは存在しない。ncc には `-resource:` があるが、`Nemerle.Core.targets` は
`CreateManifestResourceNames` を空実装で潰している(`.nproj` 拡張子の代償として意図的)ので、
`.resx` 経路を通すには `GenerateResource` の配線から要る。feasibility の範囲外として見送った。

**残課題**: WinForms 2 本は**ビルドのみ**の確認で、実行はしていない(GUI のため無人実行不可)。
機能的に等価な経路は §3 でヘッドレスに検証してある。

## 7. 警告

net10 で出る警告は**ランタイム 3 件のみ**(マクロ・テスト・WinForms 2 本は 0 件、
`AsyncHttp` の N618 1 件を除く)。いずれも .NET 側の obsolete 化であり、CLR4 では 0 件になる
= **フレームワーク世代差であって移植の欠陥ではない**。

| 箇所 | 内容 |
|---|---|
| `Async\Exceptions.n:10` | N618 `Exception(SerializationInfo, StreamingContext)`(.NET 9+ で obsolete) |
| `Async\Job.n:27` | N618 `Thread.VolatileWrite` → `Volatile.Write` 推奨 |
| `Async\Job.n:40` | N618 `Thread.VolatileRead` → `Volatile.Read` 推奨 |
| `AsyncHttp\MainForm.n:41` | N618 `WebRequest.Create` → `HttpClient` 推奨 |

`Exceptions.n` は 52 §5 の `GrammarException.n` と同じ形で、`BinaryFormatter` は使っていないため
`Nemerle.Linq` の `serialize.n`(WP-N3 で救済不能と判定)のような詰みではない。
`Thread.VolatileRead/Write` も実在し動作する(51/51 PASS がその証拠)。

`Job.n:32` には `mutable _state : int; // volatile modifier don't work in current release` という
上流のコメントがあり、`Thread.VolatileRead/Write` はその回避策である。将来
`Volatile.Read/Write` に寄せる価値はあるが、共有ソース改変になる。

## 8. 版と署名

`Properties\AssemblyInfo.n` の
`[assembly: GeneratedAssemblyVersion("$GitTag.0.$GitRevision", Defaults(GitTag="1.2", GitRevision="9999"))]`
により、`dotnet build` 単体では `git describe` 由来の値が焼かれる。実測 **1.2.0.669**
(52 §9.4 の案 C と同じ非決定性)。また legacy `.nproj` は `Nemerle.snk` で `SignAssembly` するが、
core 側の出力は **PublicKeyToken 無し**(実測)。

いずれも Peg と完全に同じ条件であり、配布するなら 52 §8〜§9 の議論がそのまま適用できる。
**ComputationExpressions 固有の差は 1 点だけ**: §1.1 のとおりマクロ アセンブリーが
ランタイムへの AssemblyRef を持たないため、52 §9.2 の推奨構成
(`lib/net10.0/` + `macros/` + `build/<id>.props`)が Peg と同様に採れる。ただし Peg と違って
**マクロ側のビルドにはランタイムが要る**(コンパイル時のみ)ので、パッケージ生成の順序は
ランタイム → マクロに固定される。

配布可否そのものは未判断。52 §9.1 と同様に「実利用者が見えていない contrib 相当のツリーに
リリース セットを 1 つ増やすか」の判断であり、本実験の範囲外。

## 9. 再現コマンド

```powershell
$base = "dotnet-port\CompExprFeasibility"

# コンソール 3 本(既定経路で可)
dotnet build "$base\CompExprTest\CompExprTest.nproj" -nr:false
dotnet exec  "$base\CompExprTest\bin\Debug\net10.0\ComputationExpressions.Tests.dll"   # 51 OK / exit 0

dotnet build "$base\SyncCtxSpike\SyncCtxSpike.nproj" -nr:false
dotnet exec  "$base\SyncCtxSpike\bin\Debug\net10.0\SyncCtxSpike.dll"                   # OK / exit 0

dotnet build "$base\ContSpike\ContSpike.nproj" -nr:false
dotnet exec  "$base\ContSpike\bin\Debug\net10.0\ContSpike.dll"                         # 1 OK / 1 Failed(§4)

# WinForms 2 本は -p:NemerleUseExec=true が必須(§5)
dotnet build "$base\AsyncHttp\AsyncHttp.nproj"               -nr:false -p:NemerleUseExec=true
dotnet build "$base\WindowsFormsTest\WindowsFormsTest.nproj" -nr:false -p:NemerleUseExec=true
```

CLR4 対照実験は `bin\Release\net-4.0\Stage1\ncc.exe` に同じソースを渡し、`Nemerle.dll` を
出力先へ copy して実行する(本ログ作成時の作業ディレクトリーは `%TEMP%\ce-clr4`)。

```powershell
$ncc = "bin\Release\net-4.0\Stage1\ncc.exe"
$src = "snippets\ComputationExpressions"
& $ncc -target:library -out:"$wd\Nemerle.ComputationExpressions.dll" `
       (Get-ChildItem -Recurse "$src\ComputationExpressions" -Filter *.n).FullName
& $ncc -target:library -out:"$wd\Nemerle.ComputationExpressions.Macros.dll" `
       -ref:"$wd\Nemerle.ComputationExpressions.dll" -ref:"...\Stage1\Nemerle.Compiler.dll" `
       (Get-ChildItem -Recurse "$src\ComputationExpressions.Macros" -Filter *.n).FullName
& $ncc -target:exe -out:"$wd\ComputationExpressions.Tests.exe" `
       -ref:"$wd\Nemerle.ComputationExpressions.dll" `
       -macros:"$wd\Nemerle.ComputationExpressions.Macros.dll" `
       (Get-ChildItem -Recurse "$src\Test" -Filter *.n).FullName
```

## 10. 残課題(この実験では扱っていない)

- §5 の `NemerleAdditionalOptions` 引用符無視の修正(`NccCompile.cs:175`)。
  ComputationExpressions とは独立した、移植側の一般的な不具合。
- §6.1 の WindowsDesktop 参照。今回は各 `.nproj` に手書きしたが、WinForms/WPF を
  第一級で支援するなら `Nemerle.Core.targets` 側の話になる。
- §6.5 の `.resx` / `EmbeddedResource` 経路。
- WinForms 2 本の実行時検証(GUI のため無人実行不可)。
- §7 の `Thread.VolatileRead/Write` → `Volatile.Read/Write`(共有ソース改変を伴う)。
- 配布の可否と設計(52 §8〜§9 を参照。本実験では §8 の版・署名の実測のみ)。
