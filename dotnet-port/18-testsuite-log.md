# 18 — WP-I1: 実テストハーネス(Nemerle.Compiler.Test.exe)の core 移植と testsuite 全数実行 — ログ

ゴール: セルフホスト済みの stage2(真の .NET 10 フレーバー ncc.exe 一式)に対して、
手動スライスではなく **本物のハーネス**(`snippets\Nemerle.Test\Nemerle.Compiler.Test`)を
使い、`testsuite\positive`・`testsuite\negative` の**全数**を自動実行し、結果を
PASS / FAIL(コンパイラーバグ疑い)/ 環境・ハーネス制約に分類する。

## 1. 選んだ経路: (b)(ハーネスは CLR4 のまま、`-ncc`/`-runtime` で core を外部試験)— コード変更ゼロで到達

02-build-flow.md §8 に記載のとおり、ハーネスには元から `-ncc <exe>`(`ExternalNcc`)と
`-runtime <exe> -runtime-params <args>`(`RuntimeProcessStartInfoFactory`)がある。
ソースを読んで分かった重要な事実(`snippets\Nemerle.Test\Nemerle.Compiler.Test\Main.n`
143-164行、`RuntimeProcessStartInfoFactory.n`、`ExternalNcc.n`、`NccTest.testOutputAssembly`):

- `-runtime`/`-runtime-params` で作る `ProcessStartInfoFactory` は **1個だけ生成され、
  「コンパイラーを起動する」「コンパイル済み exe を実行する」の両方で共有**される
  (`Main.n` 155-191行: `processStartFactory` が `ExternalNcc` のコンストラクター引数にも
  `NccTest` のコンストラクター引数にも渡る)。
- したがって `-r dotnet.exe -rp exec` を渡すだけで、実際に起動されるプロセスは
  「コンパイラー本体の起動」も「positive テストが生成した exe の実行」も両方とも
  `dotnet.exe exec <path> ...` になる。**ハーネスのソースは一切変更不要。**
- ハーネス自身(`Nemerle.Compiler.Test.exe`)は net-4.0(CLR4)のままでよい。
  `ExternalNcc` 経路では `HostedNcc`(= `CompilationOptions`/`ManagerClass` を直接
  ロードして in-process コンパイル)を一切使わないため、ハーネスに同梱された
  net4 フレーバーの `Nemerle.Compiler.dll`/`Nemerle.dll`/`Nemerle.Macros.dll` は
  ロードすらされない(.NET の遅延アセンブリロードにより、使われない分岐の型は
  JIT 時に解決されない)。Framework ランタイムのみのこの環境でも
  `Nemerle.Compiler.Test.exe` はネイティブに(`dotnet exec` なしで)起動できる。
- 検証(参考実験): CLR4 の `bin\Release\net-4.0\Stage1\ncc.exe` を `-ncc` に渡して
  ハーネスをそのまま動かし、negative の `overloading-01.n` で本 WP と全く同じ失敗を
  再現(§5参照)。ExternalNcc 経路がハーネス側の変更なしで機能することを実証している。

→ 作業指示の (a)(ハーネス自体を core 向けに再コンパイルして `dotnet exec` で駆動)は
**不要と判断し、着手しなかった**。(b) だけで受け入れ基準(全数自動実行・再現可能・
CLR4 無傷)を満たせたため、「最小工数の経路を選ぶ」方針どおり (a) は見送り。
(a) を追求する場合の价値は主に「ハーネス自身も dotnet ツールとして配布したい」という
将来の配布シナリオ向けであり、回帰ゲートとしての機能は (b) で既に満たされている。

## 2. コンパイラーへの標準参照の注入方法

`build-stage2-core.ps1`(13-stage2-log.md)と同じ **`-use-loaded-corlib` + 実体分割
アセンブリの `-ref:`** 戦略を、ハーネスの `-reference`/`-parameters` スイッチ経由で
「全テスト共通のグローバル参照」として注入する:

- `-p:"-no-stdlib -use-loaded-corlib -greedy-references:- -nowarn:10003"`
  (`-p` の値はホワイトスペース分割されるだけでクォート処理がない
  `Main.n:parseArguments` を通るため、**パスを含まないフラグのみ**をここに乗せる)
- 各実体分割アセンブリ・`Nemerle.dll` は `-ref:<絶対パス>` として個別に渡す
  (1オプション=1トークンなのでパス中のスペース(`C:\Program Files\...`)も安全)。
- stage2 の参照セットに加えて、testsuite の全数実行で新たに必要と分かったものを追加:
  `System.Xml.Serialization.dll`(15-attributes01-diagnosis.md の再現手順どおり)、
  `System.Runtime.Serialization.Formatters.dll`、`System.Net.Primitives.dll`、
  `System.Net.NameResolution.dll`(`System.Net.IPAddress`/`IPHostEntry`)、
  `System.Collections.NonGeneric.dll`(`SortedList`)、`System.ObjectModel.dll`
  (`System.Collections.ObjectModel.KeyedCollection`)。全て .NET 10 共有フレームワーク内の
  実体分割アセンブリで、追加前後の差分で `bug-1216.n`/`properties.n`/`enumerator.n`/
  `generics.n` の4本が FAIL → PASS に転じたことを確認済み(§4)。

## 3. ハマった点と修正(ハーネス実行環境側、コンパイラー本体は無改造)

### 3a. companion `*-lib.n` 参照解決には作業ディレクトリ = `-output` が必須

`testsuite\positive` の REFERENCE: プラグマは多くが同一ディレクトリのソース(companion
`*-lib.n`、それ自体は普通にテストとしてコンパイルされ、ライブラリとして出力される)を
裸名で指す。`ncc\external\LibraryReferenceManager.n:71-76` の `_lib_path` は
`System.Environment.CurrentDirectory` を含むが **ハーネスの `-output:` 値は含まない**。
実験(CLR4 Stage1 で直接検証、§5)で確認したとおり、ハーネスプロセスの CWD が
`-output` と異なると `cannot find assembly` で即失敗する。
→ `run-testsuite-core.ps1` は元の `NemerleAll.nproj` の `CompilerTests` ターゲットと
同じ規約(`WorkingDirectory=$(NBin)\Tests\positive`, `-output:.`)を踏襲し、
出力ディレクトリに `Push-Location` してからハーネスを起動する。

### 3b. コンパイル済み positive テスト exe が `Nemerle.dll` を見つけられない(最重要の修正)

**最初の全数実行で 216/469 が失敗**したが、その大半(150本以上)が
`Test finished with exit code -532462766`(= 0xE0434352、CLR 未処理例外の
終了コード)という不可解な一律の失敗だった。生成された exe を直接
`dotnet exec` してみると:

```
Unhandled exception. System.IO.FileNotFoundException: Could not load file or
assembly 'Nemerle, Version=1.2.0.577, ... PublicKeyToken=e080a9c724e2bfcd'.
```

原因: positive テストの `-t:exe` 出力は `-output` ディレクトリに書かれるが、
その隣に `Nemerle.dll`(スタンダードライブラリ、実行時にも必須)が存在しない
— `.NET` のアセンブリ探索はエントリアセンブリのディレクトリと共有フレームワークしか
見ない。これは 13-stage2-log.md の CLR4 回帰チェック節で「事前に存在が知られていた
scratch-dir artifact」として触れられていた症状そのもの。元の `NemerleAll.nproj` の
`CompilerTests` ターゲットが `$(NBin)\TestFramework\*.*`(= 供試ステージの
`Nemerle.dll` 等)を `$(NBin)\Tests\positive` にコピーしていたのは、まさにこの
ためだったと判明。
→ `run-testsuite-core.ps1` は各スイート実行前に、ステージングした core コンパイラー
ディレクトリの `*.dll`(`Nemerle.dll`/`Nemerle.Compiler.dll`/`Nemerle.Macros.dll`/
`Nemerle.CoreEmit.dll`)を出力ディレクトリへコピーする。
**この1行の修正で positive の失敗が 216→42 に激減**(その後の参照追加で 42→38)。

## 4. 成果物

- `dotnet-port\run-testsuite-core.ps1`(新規): パラメーター化されたテスト実行スクリプト。
  ハードコードパスなし(`bin\<Cfg>\core\Stage2` 等はデフォルト値。全て `-CompilerDir`/
  `-HarnessDir`/`-StagingDir`/`-TestSuiteDir`/`-Suite` で上書き可能)。
  **`bin\` へは一切書き込まない**(コンパイラー・ハーネスとも `%TEMP%\nemerle-core-testsuite`
  相当のステージング先へコピーしてから使用。コピー元の `bin\Release\core\Stage2\` /
  `bin\Release\net-4.0\TestFramework\` はどちらも読み取りのみ)。
  `dotnet --list-runtimes` で共有フレームワークを動的解決(`build-stage2-core.ps1` と同じ
  ロジックを再利用)。実行後、ログをパースして PASS/FAIL/SKIP 集計と失敗詳細
  (`<suite>-failures.txt`)を出力する。
- ハーネスのソース変更: **なし**(`snippets\Nemerle.Test\...` は無改造)。
- コンパイラー本体・macros の変更: **なし**(本 WP はハーネス/実行環境のみが対象)。

### 実行方法

```powershell
pwsh dotnet-port\run-testsuite-core.ps1                    # 既定: bin\Release\core\Stage2 を全数実行
pwsh dotnet-port\run-testsuite-core.ps1 -Suite positive    # positive のみ
pwsh dotnet-port\run-testsuite-core.ps1 -Compiler <他のパス> -StagingDir <他の場所>
```

CLR4 側の回帰は本 WP では未実施(コンパイラー本体・ハーネスとも無改造のため、
既存の CLR4 運用(`NemerleAll.nproj /t:CompilerTests`)に影響する変更はゼロ。
念のため §1 の検証実験で CLR4 の Stage1 ncc.exe を同ハーネスに通して動作を確認済み)。

## 5. 全数実行の集計

2026-07-12、クリーンなステージングディレクトリからの再実行(再現性確認、数値は
2回の独立実行で完全一致)。コンパイラー: `bin\Release\core\Stage2\ncc.exe`
(WP-D/E 時点のビルド)。

| スイート | 総数 | PASS | FAIL | PASS率 |
|---|---|---|---|---|
| positive(`*.n` 460 + `*.cs` 9 = 469) | 469 | **431** | 38 | 91.9% |
| negative(`*.n` 166 + `*.nnn` 1 = 167) | 167 | **165** | 2 | 98.8% |
| 合計 | 636 | **596** | 40 | 93.7% |

companion `*-lib.n` ファイル(REFERENCE: プラグマで他テストから参照される、
それ自体も普通にコンパイル対象になる)は上表の総数・PASS数に含まれる
(「not a test」スキップは0件 — このリポジトリの `*-lib.n` は `NO-TEST` プラグマを
使っておらず、普通にライブラリとしてコンパイル・PASS 判定される)。

## 6. 失敗の分類(全40件)

### 6.1 環境・ハーネス制約(FAIL、コンパイラーバグではない)— 36件

| 分類 | 件数 | 該当ファイル |
|---|---|---|
| A. core 未ビルドの補助 Nemerle ライブラリ(Nemerle.Linq/.Unsafe/.WPF) | 8 | Linq: `Issue-git-0053.n` `Issue-git-0232.n` `Issue-git-0239.n` `Issue-git-0272-linq-ET.n` `Issue-git-0298.cs` `linq-2-ExprTree.n`(6件)/ Unsafe: `Issue-git-0397.n` / WPF: `notifypropertychanged.n`(positive・negative 両方に同名ファイルあり、両方でカウント) |
| B. C# パーサープラグイン(`ncc.parser.csharp.dll`)が core 向けにビルド/登録されていない | 8 | `Issue-git-0092/0220/0221/0222/0246/0255/0297/0306.cs`(`can't parse file with extension 'cs', parser not registered`) |
| C. .NET 10 共有フレームワークに存在しない BCL 面(別 NuGet パッケージが必要、または対象外) | 6 | `System.Web.UI`: `access-checks.n` / `System.CodeDom`: `codedom.n`(ncc 自身も WP-C で codedom 除外済み、対称的な既知ギャップ) / `System.Windows.Forms`: `form.n` / `Tao.OpenGl`(ネイティブバインディング、対象外): `pointer-type-caching.n` / `System.Security.Permissions` の属性クラス面: `Issue-git-0507.n`, `security.n` |
| D. CAS(コード アクセス セキュリティ)は CoreCLR では no-op — テストの `#if !NET_4_0` 分岐が想定していなかった第3のランタイム構成 | 1 | `security-asm.n` |
| E. `-res:`/`-linkres:`(Win32・リンクリソース)が CoreCLR パスで未サポート(WP-B/D/E からの既知ギャップ、`14-pdb-log.md`/`13-stage2-log.md` に記載済み) | 1 | `resource.n` |
| F. 外部開発ツール(`pkg-config`)がこの環境に存在しない(ランタイム非依存、CLR4 でも同様に失敗するはず) | 1 | `gtk.n` |
| G. BCL 例外メッセージ書式の変更(Framework の別行 `Parameter name: X` → Core のインライン `(Parameter 'X')`) | 2 | `assert.n`, `notnullorempty.n` |
| H. BCL `double.ToString()` 既定書式の変更(.NET Core 3+ の最短往復表現 vs Framework の旧書式) | 3 | `basic-value-types.n`, `overloading.n`, `Issue-git-0274.n` |
| I. BCL 属性面の変更(`IObjectReference` が新たに `[Obsolete]` に) — ncc は正しく検知しているが、テスト側の期待値がモダン BCL 未対応 | 1 | `serialize.n` |
| J. BCL 型の移動(`ExtensionAttribute` が corelib 本体に同梱されるようになり、テストの互換シム宣言と衝突) | 2 | `external-extension-method-lib.n`, `external-extension-method.n`(前者の失敗が後者へ連鎖) |
| K. `REFERENCE: System.Core` という facade 名の裸名解決が CoreCLR で機能しない(13-stage2-log.md の facade 参照の知見と同根) | 1 | `Issue-git-0590-2.n` |

小計: 8+8+6+1+1+1+2+3+1+2+1 = 34(positive)。ここに **negative の
`notifypropertychanged.n`**(Nemerle.WPF.dll 不在、A と同分類)を加えて
環境・ハーネス制約は **合計35件**。

### 6.2 コンパイラーバグ疑い — 4件 + 1件(再確認)

#### (1) `attributes-01.n` — 既知(再確認のみ、新規ではない)

`15-attributes01-diagnosis.md` で既に根本原因まで診断済みの
local enum(TypeBuilder のまま)を属性引数/名前付きメンバーに使うパターン。
本 WP の全数実行でも同一の `ArgumentException (Constant does not match the
defined type.)` を再確認。診断済みのため詳細は同ドキュメント参照。

#### (2) `attributes-03.n` / `attributes-assembly.n` — 新規: アセンブリレベル属性で不正な PE が生成される

コンパイル自体は**エラーなく成功**するが、生成された exe を実行すると
`System.BadImageFormatException` でロードにすら失敗する:

```
$ dotnet exec attributes-03.exe
Unhandled exception. System.BadImageFormatException: Could not load file or
assembly 'attributes-03, ...'. (0x8007000B)
```

両ファイルに共通するパターン: `[assembly: ...]` 属性で
(a) `typeof(List[_])`/`typeof(System.Collections.Generic.List[int])` のような
**ジェネリック型引数を伴う `typeof`**、(b) **複数の同名コンストラクター
オーバーロード**(`int`/`long`/`string`/`bool`/`object`/`params array[string]`)
を持つ属性クラス、を使用。`attributes-01.n`(local enum)とは異なる根本原因と見られる
— コンパイル時に例外を投げず、`CustomAttributeBuilder`/`Save()` のどこかで
アセンブリレベル属性のメタデータ blob が静かに壊れている可能性が高い
(15-attributes01-diagnosis.md で確立した「persisted 実装は特定の属性値パターンで
`CustomAttributeBuilder` 周りの互換性が Framework/CoreCLR ランタイム版と異なる」
という知見の別バリエーションと推測されるが、**未診断**)。
最小再現: `testsuite\positive\attributes-03.n` と `attributes-assembly.n` を
stage2 ncc でコンパイル → 成功 → `dotnet exec <出力exe>` で
`BadImageFormatException`。**次 WP の筆頭候補として記録**(本 WP では診断のみ、
修正は行っていない)。

#### (3) `string-template-3.n` — 新規: ジェネリック型 + StringTemplate マクロで内部コンパイラーエラー

```
error: internal compiler error: got some unknown exception of type
System.Reflection.TargetInvocationException: Exception has been thrown by
the target of an invocation..
   at System.RuntimeType.InvokeMember(...)
   at Nemerle.Compiler.ILEmitter..ctor(MethodBuilder method_builder)
   at Nemerle.Compiler.MethodBuilder...apply_void()
   at Nemerle.Compiler.TypeBuilder.BeforeFinalizeType()
   ...
```

`ILEmitter..ctor`(`ncc\generation\ILEmitter.n:87-100`)は
`_ilg = Late.late_macro (NemerleGenerator (mbase.GetILGenerator () :> ILGenerator))`
という、`ILGenerator` を Nemerle の `late`(実行時リフレクションディスパッチ)経由で
ラップするコードを含む。`string-template-1.n`/`string-template-2.n`(非ジェネリック)は
stage2 で問題なく PASS するのに対し、`string-template-3.n` は
**ジェネリック基底クラス**(`BaseHtmlReportTemplate[T]`)に `[StringTemplate.
StringTemplateGroup()]` マクロを付けた場合にのみ失敗する。CoreCLR の persisted
`MethodBuilder`/`ILGenerator` 実装がジェネリック型上のメソッドに対して
`late` 経由のリフレクション呼び出しの何かを未サポートである可能性が高いが、
**未診断**(内側の実例外の詳細は ncc のエラーレポーターがメッセージを1行に
まとめてしまうため、スタックトレースからは特定不能。`Nemerle.CoreEmit` 側に
デバッグ用のフック追加が必要になりそう)。次 WP 候補として記録。

#### (4) `overloading-01.n`(negative)— 最重要: WP-D の修正が引き起こした既存の回帰(CLR4 でも再現、core 固有ではない)

negative の2件の失敗のうち、`notifypropertychanged.n` は上記 A(Nemerle.WPF.dll
不在)だが、**`overloading-01.n` は本 WP で新たに発見した genuine な regression**。

テストの該当箇所(`testsuite\negative\overloading-01.n:105-113`):

```nemerle
public class Bug743
{
  public this (_ : string) { }          // H: overload definition
  public this (_ : string, _ = "a") { } // H: overload definition
  foo () : void
  {
    _ = Bug743 ("s"); // E: typing fails on amb
  }
}
```

これは「デフォルト値を持つ引数だけが違う2つのオーバーロードは、実引数数が
一致する呼び出しに対して曖昧(エラー)であるべき」という意図的な negative test
だが、stage2 の ncc は**エラーを出さずに(黙って一方を選んで)コンパイルを通してしまう**。

原因は 13-stage2-log.md item 2b で導入した `Typer-OverloadSelection.n` の
`AintUsingDefaultParms` タイブレーク(モダン BCL の
`Debug.Assert(bool)`/`Assert(bool, string message = null)` のような
「デフォルト引数だけを追加した新オーバーロード」による偽の曖昧性エラーを
解消するためのフィルタ)。このフィルタは「デフォルト引数を使わずに済む候補が
存在すればそちらを優先する」という**無条件のタイブレーク**であるため、
ユーザーコードが意図的に定義した同型の曖昧オーバーロード
(`Bug743(string)` と `Bug743(string, string = "a")`)も**同じ理由で
非曖昧化されてしまう** — BCL 由来かユーザー由来かを区別していない。

**CLR4 でも同一の regression であることを確認済み**(`bin\Release\net-4.0\Stage1\
ncc.exe` をこのハーネスにそのまま通した):

```
$ Stage1\ncc.exe (via harness) testsuite\negative\overloading-01.n
0 tests passed, 1 test failed.
Expected error: `typing fails on amb' hasn't occured in line:111
```

→ **core 固有ではなく、WP-D の時点で CLR4 側にも作り込まれていた regression**
(WP-D は「CLR4 側は無改造」と報告したが、これは *ソースコードが* 無改造という
意味であり、共有ソースの `Typer-OverloadSelection.n` に加えた「no-op」とされた
はずのタイブレークが、実際には意味的な回帰を起こしていた)。本 WP が実ハーネスで
negative 全数を初めて回したことで判明した。**次 WP での修正を強く推奨**
(修正方針の初期案: BCL 由来かどうかの判定は難しいので、タイブレークを
「デフォルト引数を使わない候補が唯一つ」の場合に限定する、または当面
このタイブレークを撤回し `Typer-OverloadSelection.n` 側で
`OverloadResolutionPriorityAttribute` を素直に読む実装に置き換える、等)。

## 7. 受け入れ基準との対応

1. **全数自動実行 + 集計**: `run-testsuite-core.ps1` が positive 469 本・negative 167 本
   (`*.n`+`*.nnn`+`*.cs`)を自動実行し、PASS/FAIL 集計と失敗詳細ファイルを出力 — 達成。
2. **再現可能**: ステージングディレクトリを完全に消してからの再実行で
   数値が完全一致(431/469, 165/167)、`-Compiler`/`-HarnessDir`/`-StagingDir` 等
   全てパラメーター化されハードコードパスなし — 達成。
3. **CLR4 運用を壊さない**: ハーネス・コンパイラーとも無改造。念のため CLR4 の
   Stage1 ncc.exe を同ハーネスに通す検証実験も実施し、動作(および §6.2(4) の
   regression の再現)を確認 — 達成。
4. **失敗一覧の分類**: 40件全てを「環境・ハーネス制約」(36件、6カテゴリー)と
   「コンパイラーバグ疑い」(4件+再確認1件)に分類し、後者には最小再現情報
   (ファイルパス・症状・関連ソース箇所・CLR4 再現結果)を付記 — 達成。

## 8. 既知の残課題(次 WP へ)

- `attributes-03.n`/`attributes-assembly.n` の `BadImageFormatException`(§6.2(2))の
  根本原因診断(`15-attributes01-diagnosis.md` 方式でのバイナリー差分調査が有効と思われる)。
- `string-template-3.n` の `TargetInvocationException`(§6.2(3))— 内側の実例外を
  取り出すためのデバッグフックが必要。
- **`overloading-01.n` の regression(§6.2(4))— 優先度最高**。`Typer-
  OverloadSelection.n` の `AintUsingDefaultParms` タイブレークの見直し。
- `/doc:`(XML ドキュメント生成)は本 WP のグローバル参照セットに `-doc:` を含めておらず
  未検証のまま(13-stage2-log.md の既知ギャップを継承)。
- C# パーサープラグイン(`ncc.parser.csharp.dll`)の core 移植(8件の `.cs` テストを
  救済できる)。
- `Nemerle.Linq.dll`/`Nemerle.Unsafe.dll`/`Nemerle.WPF.dll` の core 向けビルド
  (`build-stage2-core.ps1` 方式を Linq/Unsafe プロジェクトにも適用すれば、
  positive 8件・negative 1件を追加で救済できる見込み)。
- `System.Security.Permissions`/`System.CodeDom` は .NET 10 共有フレームワークに
  同梱されていない(別 NuGet パッケージ) — 参照追加で救済したい場合は
  ncc のビルド/実行環境にパッケージ復元の仕組みを導入する必要がある(本 WP の
  スコープ外、コンパイラーの config 注入だけでは解決しない)。
