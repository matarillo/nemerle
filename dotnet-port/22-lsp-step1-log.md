# 22. WP-K: LSP feasibility step 1 — Compiler.Utils core build + headless ConsoleTest

日付: 2026-07-13
前提: dotnet-port/21-lsp-feasibility.md の「推奨する進め方」ステップ1
「WinForms/CodeDom 系を除いた Compiler.Utils を stage2 ncc でビルド → ConsoleTest 相当を
ヘッドレスで PASS させる」を実施。ここまでで LSP 実現性はほぼ確定する、というのが21番の見立て。

## 結論

**達成**。VsIntegration/Nemerle.Compiler.Utils(IDE エンジン本体)を WinForms/CodeDom 系
14ファイルを除外して dist/ncc(core, .NET 10)でビルドし、ConsoleTest 相当のヘッドレス
ドライバーで実行 → **Engine のブートストラップ(EngineFactory.Create→BeginReloadProject→
IsProjectAvailable)が成功し、58件中52件 PASS**。残り6件は全て「補完候補の個数/順序が
.NET Framework 2.0 と .NET 10 の BCL 差でずれる」という環境差起因のアサーション不一致で、
コンパイラー/エンジンのクラッシュや機能欠落ではない。21番の見立てどおり、
**LSP サーバー化の実現可能性はほぼ確定**。

## 成果物

- `dotnet-port/LspFeasibility/Nemerle.Compiler.Utils/Nemerle.Compiler.Utils.nproj`:
  VsIntegration の .n ファイルをそのまま参照(コピーしない、単一ソース)する net10.0 ライブラリ。
  WinForms/CodeDom 系14ファイルを除外。dist/ncc の Nemerle.Compiler.dll/Nemerle.Macros.dll を
  HintPath 参照(Sokoban サンプルと同じパターン)。
- `dotnet-port/LspFeasibility/Nemerle.Compiler.Utils.Tests/Nemerle.Compiler.Utils.Tests.nproj`:
  上記への ProjectReference + ExternalDependences/nunit.framework.dll(NUnit 2.x、.NET
  Framework 用)を HintPath 参照。ビルド・実行とも無改造で通る(mscorlib ファサード転送と
  同じ理屈で CoreCLR 上でも読み込める)。
- `dotnet-port/LspFeasibility/ConsoleTest/`: legacy `VsIntegration/ConsoleTest/Program.cs`
  相当のヘッドレスドライバー(Nemerle で新規実装)。legacy 版は最初の Assert 失敗で
  プロセスごと落ちる作りだったため、今回は各 [Test] メソッドをリフレクションで個別に
  try/catch して PASS/FAIL を独立集計するよう改善。legacy が呼んでいなかった
  GoToInfoTest_001〜004 と Complete_in_base_type_1〜3 も追加で回した(QuickTip_StackOverflow
  だけは legacy 同様に除外: 本物のスタックオーバーフローはプロセスを道連れにする
  catch 不能な CLR 障害なので、try/catch ランナーでは意味がない)。

## 除外した WinForms/CodeDom 系14ファイル

`AstBrowserForm.n`、`Nemerle.Completion2/CompiledUnitAstBrowser.n`(デバッグ用 AST ツリー
Form)とその唯一の呼び出し元 `Nemerle.Completion2/CodeModel/Project.Debug.n`(その呼び出し元
自体は `Project.Relocation.n` 側で既にコメントアウト済み=到達不能コードだった)。
`CodeDom/*.n` 7ファイル(CodeDomHelper/FormChanges/FormCodeDomGenerator/FormCodeDomParser/
NemerleCodeDomProvider/NemerleCodeParser/NemerleCodeParserBase.n: VS Forms デザイナーの
CodeDom 往復変換機能一式)。それを呼ぶ `Engine-CreateCodeCompileUnit.n`/
`Engine-MergeCodeCompileUnit.n` と対応する `*AsyncRequest.n` 2ファイル。

**唯一の分離が必要だった箇所**: `CodeDom/NemerleCodeParser.n` 内に `IntegrationDefaultParser`
クラス(IParser の素朴な実装、Engine.Init.n の `CreateParser()` が使う**コア機能**)が
同居していた。CodeDom 専用ではなかったため、新規ファイル
`Nemerle.Completion2/Engine/IntegrationDefaultParser.n` に分離(legacy .csproj にも追加
=legacy ビルドは無影響)。

**`IEngine.n` は無改造**。当初は `IIdeEngine` から `CreateCodeCompileUnit`/
`MergeCodeCompileUnit`/`BeginMergeCodeCompileUnit` の3メンバーを削除する案だったが、
`VsIntegration/Nemerle.VisualStudio/Project/NemerleFileNodeCodeDomProvider.cs`(VS2010 拡張の
Forms デザイナー統合)が `IIdeEngine` 型経由でこの3メンバーを呼んでいる
(`projectInfo.Engine.CreateCodeCompileUnit(...)` 等、`Engine` プロパティの型が `IIdeEngine`)
ため、インターフェースから削除すると legacy VS ビルドを壊すことが判明し撤回。
代わりに: (a) 3メンバーのシグネチャが要求する型
(`CreateCodeCompileUnitAsyncRequest`/`FormChanges`/`MergeCodeCompileUnitAsyncRequest`、
定義ファイルはそれぞれ `Async/AsyncRequest/CreateCodeCompileUnitAsyncRequest.n`・
`CodeDom/FormChanges.n`・`Async/AsyncRequest/MergeCodeCompileUnitAsyncRequest.n`)は
`System.CodeDom` のみに依存し WinForms 依存はゼロだったため、この3ファイルは core ビルドにも
含めた(`System.CodeDom` PackageReference を追加、ncc の CoreCLR 自動参照セットには無いため)。
(b) 実装本体(`Engine-CreateCodeCompileUnit.n`/`Engine-MergeCodeCompileUnit.n`、内部で
`FormCodeDomParser`=WinForms 依存クラスターを生成する)は引き続き除外し、代わりに新規
`dotnet-port/LspFeasibility/Nemerle.Compiler.Utils/CodeDomStubs.n`(VsIntegration の**外**、
dotnet-port 側)で `Engine` の3メソッドを `NotSupportedException` を投げるだけの実装として
追加し、`IIdeEngine` を核ビルドでも完全に実装させた(このテストスイートは実際どちらも
呼ばない)。`Formatter.n` の未使用 `using System.Windows.Forms;` も削除(実際に Forms 型を
使っている箇所はゼロだった)。

## legacy VS2010 ビルドへの影響

VsIntegration 配下は legacy(.NET Framework 3.5/VS2010)ビルドと本 WP の core ビルドが
**同じソースファイルを共有**しているため、変更のたびに legacy 側を壊していないか確認が必要。
最終的に VsIntegration 側に残った差分は全て legacy 非破壊であることを確認済み:

- `CodeDom/NemerleCodeParser.n` の `IntegrationDefaultParser` 分離 + `.csproj` への追加:
  legacy 側もこの新ファイルを取り込むよう `.csproj` を更新済み(上記)。
- `IntelliSenseModeLibraryReferenceManager.n` の `Exists` 完全修飾: legacy(.NET Framework)
  には `Path.Exists` 自体が存在しない(.NET 7+ 追加)ため、そもそも曖昧さが無く、
  `File.Exists` へ完全修飾しても意味的に同一。
  同ファイル・`XmlDocReader.n` の `RuntimeEnvironment.GetRuntimeDirectory()` →
  `Path.GetDirectoryName(typeof(object).Assembly.Location)` 置換も、.NET Framework では
  `typeof(object).Assembly`(mscorlib.dll)の所在ディレクトリ = CLR ランタイムディレクトリ
  なので返り値は同一。
- `EngineCallbackStub.n` の `LoadWithPartialName` null 対応: legacy では
  `LoadWithPartialName` は実際に動作する(GAC 部分名検索)ため、通常ケース
  (`asm != null`)は分岐が変わらず従来どおり。従来コードが NullReferenceException で
  即死していた null ケースのみ動作が変わる(=元々 legacy でも潜在バグだった箇所の修正)。
- `Formatter.n` の未使用 using 削除: 参照ゼロのため無影響。

## CoreCLR 移植で踏んだバグ(全て VsIntegration 側、ncc 本体は無改造で完了)

1. **`System.IO.File.Exists` と `System.IO.Path.Exists` の曖昧参照**
   (`IntelliSenseModeLibraryReferenceManager.n`、3箇所): `using System.IO.File;` と
   `using System.IO.Path;` を両方 open している状態で、.NET 7+ が `Path.Exists(string?)`
   を新規追加したため、裸の `Exists(path)` 呼び出しが二重定義で曖昧に。
   `System.IO.File.Exists(path)` に完全修飾して解消。
2. **`MessageBox.Show`**(`Formatter.n`、フォーマット例外ハンドラー内のデバッグ表示):
   WinForms 除外につき `Trace.WriteLine` に置換。
3. **`Assembly.LoadWithPartialName` が CoreCLR で常時 null**(`EngineCallbackStub.n` ctor):
   .NET Framework の GAC 部分名検索は CoreCLR ではドキュメント上の no-op(常に null)。
   null なら `Assembly.Load(name)` にフォールバックし、それも失敗する名前(このテスト
   フィクスチャの `System.Data`/`System.Drawing`/`System.Windows.Forms` 参照名 — ヘッドレス
   host にデスクトップ参照は無い)は例外にせず読み飛ばすよう変更。**この変更が
   補完個数の差(後述)の一因**。
4. **`System.Runtime.InteropServices.RuntimeEnvironment` が解決できない**
   (`IntelliSenseModeLibraryReferenceManager.n` の static field 初期化子 / `XmlDocReader.n`):
   ncc の CoreCLR 自動参照解決(`LoadCoreStdlibReferences`, WP-A2)が対象にしている
   split BCL 一式に `System.Runtime.InteropServices.RuntimeInformation.dll` は入っていない。
   もともと legacy GAC/フレームワークディレクトリー探索用のハックだった箇所なので、
   `System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)`
   (`LoadCoreStdlibReferences` 自身が `frameworkDir` を求めるのと同じ手法)に置換して解消。
   **これが今回唯一の「本物のクラッシュ」の原因**(下記参照)。

## 診断ログ: `cannot reflect 'System.Array'` (AssertionException) の原因

`ConsoleTest` 初回実行時、`Test1.Init()` → `EngineFactory.Create` → `Engine..ctor` →
`InitDefaulteEngine()` → `ManagerClass.LoadExternalLibraries()` の中で
`Nemerle.Core.AssertionException: cannot reflect 'System.Array'`(`ncc/external/
InternalTypes.n:164`)が飛んで即死していた。

`ManagerClass.LoadExternalLibraries` 自体は ncc CLI と Engine で完全に同一のメソッド
(CoreCLR 分岐込みで自動参照解決も共通)だが、`ncc/external/LibraryReferenceManager.n:513`
の `LoadTypesFrom` は `assembly.GetExportedTypes()` の失敗を**握りつぶして
`Message.Error(...)` を呼ぶだけ**(re-throw しない)。加えて IDE モードのメッセージ経路
(`Engine.CompilerMessages.n` の `AddCompilerMessage`)は
`AsyncWorker.IsRunSynchronously || AsyncWorker.IsCurrentThreadTheAsyncWorker` でない限り
即 return する no-op で、`InitDefaulteEngine()` はメインスレッド・同期呼び出しなので
**そのエラーメッセージ自体が完全に消える**。結果、mscorlib/System 相当の型登録が
失敗しているのに、後続の `SystemTypeCache.Init()`(`Reflect("System.Array")` 呼び出し元)
まで実行が進んでしまい、そこで初めて(無関係に見える場所で)assertion が飛ぶ、という
「本当の原因から数ステップ離れた場所で落ちる」構造的に追いにくいバグだった。

一時的に `ncc/external/LibraryReferenceManager.n` の該当 catch に
`Console.Error.WriteLine` を仕込んで Stage1→Stage2 をフルリビルドし、原因を
`IntelliSenseModeLibraryReferenceManager` の static field 初期化子(3番目の項目)の
`RuntimeEnvironment.GetRuntimeDirectory()` に特定(コンパイル時 unbound name として
表面化もした)。**修正後は診断コードが一度も発火しなかった**ため、上記4番の1行修正のみで
解消したことを確認済み。診断コード自体は revert し、ncc 本体は無改造の状態に戻して
Stage1/Stage2 を再ビルドしてから最終検証している(`git diff --ignore-all-space
ncc/external/LibraryReferenceManager.n` が空、意味的な差分ゼロを確認)。

## 58件中6件の FAIL は環境差(genuine なバグではない)

```
FAIL Complete_in_return_type_1  : Expected "Microsoft", got "System"    (index 3 の中身が違う)
FAIL Complete_in_return_type_3  : Expected 1, got 2
FAIL Complete_in_return_type_4  : Expected 4, got 6
FAIL Complete_Complete_expr     : Expected 20, got 18
FAIL Complete_enum              : Expected 32, got 34
FAIL Complete_qualidend         : Expected 2, got 3
```

全て「グローバルスコープでの補完候補の個数・順序」に関するアサーション。このテスト
フィクスチャ(`Nemerle.Compiler.Utils.Tests/Tests.Init.n`)は本来
`["mscorlib", "System", "System.Data", "System.Drawing", "System.Windows.Forms"]`
という .NET Framework 2.0 時代の4アセンブリ構成を前提に手で調整された期待値を持つ。
今回のヘッドレス host は(a) WinForms を意図的に除外しているため
System.Data/Drawing/WinForms は読み込まれず、(b) 読み込まれる mscorlib/System 自体も
.NET 10 の分割 BCL(実体は `System.Private.CoreLib`/`System.Text.RegularExpressions` he)
であり名前空間・型の集合が全く別物 — なので個数・並び順がずれるのは当然で、
コンパイラーやエンジンの欠陥ではない。GoTo/QuickTip/Completion(個数に依存しない形の)/
Hint/SourceTextManager/FindByLocation 系はすべて PASS しており、**機能そのもの
(補完・QuickTip・GotoInfo・ハイライトの計算ロジック)は正しく動いている**ことが
確認できた。

## 手順メモ(再現用)

```
dotnet build dotnet-port\LspFeasibility\Nemerle.Compiler.Utils\Nemerle.Compiler.Utils.nproj -c Release
dotnet build dotnet-port\LspFeasibility\Nemerle.Compiler.Utils.Tests\Nemerle.Compiler.Utils.Tests.nproj -c Release
dotnet build dotnet-port\LspFeasibility\ConsoleTest\ConsoleTest.nproj -c Release
dotnet exec dotnet-port\LspFeasibility\ConsoleTest\bin\Release\net10.0\ConsoleTest.dll
```

前提: `dotnet-port\dist\ncc\nunit.framework.dll` が要る(`ExternalDependences\
nunit.framework.dll` を手動コピー — `NccLoadContext`(WP-A3)の ALC 解決は
`assemblyName.Name + ".dll"` を **layoutDir 直下**でしか探さないため、
`Nemerle.Compiler.Utils.Tests.dll` の依存として nunit.framework が要る場面では
layoutDir=dist/ncc 直下に無いと `Can't load types from '...Tests...'.` で失敗する。
pack-tool.ps1 は現状これを配置しないので、**dist/ncc を作り直すたびに手動コピーが必要**
=今後の TODO)。

## 残作業・今後の TODO

- pack-tool.ps1 に nunit.framework.dll コピーを組み込む(or この WP 専用の配置スクリプトを
  作る)。dist/ は gitignore 対象なので今回のコピーはリポジトリには残っていない。
- 6件の FAIL は「.NET 10 BCL 前提の期待値に書き換える」か「テストフィクスチャの
  参照アセンブリ構成を意図的に無視する」かの方針判断が要る(本 WP のスコープ外、
  次段階=最小 LSP サーバー実装で `IIdeProject` を実プロジェクトの参照に置き換えれば
  この問題自体が別の形になるため、今のうちに深追いしない判断)。
- `VsIntegration/Nemerle.Compiler.Utils/Nemerle.Completion2/CompilerConcreteDefinitions/
  IntelliSenseModeLibraryReferenceManager.n` は元ファイルが CRLF/LF 混在で、今回の編集で
  一部行が LF に正規化されてしまい `git diff` にノイズがある(機能無害、内容は
  `git diff --ignore-all-space` で確認済み)。
- 21番の見積もり通り、次段階(最小 LSP: publishDiagnostics のみ)に進める状態。
