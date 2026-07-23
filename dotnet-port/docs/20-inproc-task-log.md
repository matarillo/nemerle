# WP-A3: インプロセス MSBuild タスク — 作業ログ

計画: `dotnet-port\docs\20-inproc-task-plan.md`。本ログは実装中に確定した事実・ハマった点と
解決・検証結果をまとめる。**コンパイラー本体(ncc/、lib/、macros/)は無変更**
(`git status` で確認済み、末尾参照)。

---

## 1. やったこと(成果物)

- `dotnet-port\Nemerle.Compiler.Hosting\`(新規、C#、net10.0)
  - `Nemerle.Compiler.Hosting.csproj`
  - `CompilerHost.cs` — 公開 API `CompilerHost.Compile(string[] args, Action<...> onDiagnostic, Action<string> onOutput) : int`
  - `HostedManager.cs` — `ManagerClass` のサブクラス(`CreateComponentsFactory` を override)
  - `HostedComponentsFactory.cs` — `CompilerComponentsFactory` のサブクラス(`CreateLibraryReferenceManager` を override)
  - `AlcLibraryReferenceManager.cs` — `LibraryReferenceManager` のサブクラス(`assemblyLoad`/`assemblyLoadFrom` を override し ALC 経由でロード)
  - `FuncVoidString.cs` — `Nemerle.Builtins.FunctionVoid<string>` の C# アダプター(`string -> void` 値を C# から作るため)
  - `LineForwardingWriter.cs` — `ManagerClass.InitOutput` 用の行単位フォワーディング `TextWriter`
- `dotnet-port\Nemerle.MSBuild.Tasks\`(新規、C#、net10.0)
  - `Nemerle.MSBuild.Tasks.csproj`(`Microsoft.Build.Utilities.Core` を `ExcludeAssets="runtime"` で参照)
  - `NccCompile.cs` — `Microsoft.Build.Utilities.Task` サブクラス。コンパイル毎に `NccLoadContext` を生成し、
    リフレクションで `CompilerHost.Compile` を呼び出し、診断を `Log.LogError`/`LogWarning`/`LogMessage` に変換
  - `NccLoadContext.cs` — collectible `AssemblyLoadContext`。レイアウトディレクトリ内の `<name>.dll` を最優先でロード、
    無ければ既定解決(CoreLib/System.*/Microsoft.Build.* はこちらへフォールバック)
- `dotnet-port\msbuild\Nemerle.Core.targets` / `dotnet-port\msbuild\linux\Nemerle.Core.targets` の更新
  - `<UsingTask TaskName="Nemerle.MSBuild.Tasks.NccCompile" AssemblyFile="$(NemerleTaskAssembly)" Condition="'$(NemerleUseExec)' != 'true'"/>`
  - `CoreCompile` を二本立てに(`<NccCompile Condition="...!='true'">` / `<Exec Condition="...=='true'">`、
    どちらも `$(NemerleUseExec)` の値で排他)
  - `Inputs` に `$(NemerleTaskAssembly)` を追加
  - `NemerleAdditionalOptions` プロパティを新設(両経路で使える追加 ncc スイッチのエスケープハッチ)
- `dotnet-port\pack-tool.ps1` の更新
  - レイアウト組み立て後に `Nemerle.Compiler.Hosting.csproj` を `-p:NccLayoutDir=<layout>` でビルドし
    `Nemerle.Compiler.Hosting.dll`(+ pdb)をレイアウト直下へコピー
  - 続けて `Nemerle.MSBuild.Tasks.csproj` をビルドし `Nemerle.MSBuild.Tasks.dll`(+ pdb)を `<layout>\msbuild-task\` へコピー

計画からの逸脱は無い(調査済みの想定どおりに実装できた)。ただし実装中に判明した追加の技術的課題
(C# から Nemerle 独自の関数型/リストを扱う具体的な手順、および corlib 参照問題)は計画書には無かった
詳細で、下記2〜3節に記録する。

---

## 2. ハマった点と解決

### 2.1 C# から Nemerle の `list<T>` / `string -> void` を扱う

- `Nemerle.Core.list<T>` は `abstract class`(`Cons`/`Nil` の判別共用体)。C# からは
  `Nemerle.Collections.NList.FromArray(T[] source) : list<T>`(public module、`lib\list.n:791`)
  を使うのが最短。個別に要素を足す必要がある箇所(`CompilationOptions.LibraryPaths` の先頭に
  1件足す等)は `new Nemerle.Core.list<string>.Cons(item, tail)`(`Cons` の public ctor
  `.ctor(T hd, list<T> tl)`)で直接組み立てられる(reflection で実測確認)。
- `string -> void` 型の値(`Getopt.Parse` の `error_fn`、`Getopt.CliOption.NonOption` の `handler`)は
  .NET delegate ではなく `Nemerle.Builtins.FunctionVoid<T>`(abstract class、`apply_void(T)` が
  `public abstract`)のインスタンス。`FuncVoidString : FunctionVoid<string>` で
  `apply_void` を override して `Action<string>` をラップするアダプターを書けば C# から渡せる。
  (`Nemerle.Compiler.Hosting\FuncVoidString.cs`)

### 2.2 C# から `-use-loaded-corlib` でビルドされたアセンブリを参照すると CS0012/CS0433

最も時間を要したハマりどころ。`Nemerle.dll`/`Nemerle.Compiler.dll` は
`ncc\passes.n` の `LoadCoreStdlibReferences`(`-use-loaded-corlib`)でビルドされているため、
その `AssemblyRef` テーブルは **参照アセンブリ(ref-pack)のファサードではなく実行時の実体アセンブリを
直接指す**(reflection で実測: `System.Private.CoreLib, System.Linq, System.Collections,
System.Collections.Specialized, System.Console, System.Text.RegularExpressions,
System.Private.Uri, System.Private.Xml, System.Security.Cryptography,
System.Diagnostics.Process`)。

- 素朴に `<Reference Include="Nemerle.Compiler"><HintPath>...</HintPath></Reference>` だけを
  足すと `CS0012: 型 'Object' は、参照されていないアセンブリに定義されています。
  アセンブリ 'System.Private.CoreLib' に参照を追加する必要があります` になる
  (SDK の暗黙 `FrameworkReference` は ref-pack のファサード `System.Runtime.dll` 等を
  参照に加えるが、そこには literally `System.Private.CoreLib` という名前のアセンブリは無いため)。
- では `System.Private.CoreLib.dll` 等の実体だけを追加参照すると、今度は
  `CS0433: 型 'AssemblyLoadContext' が 'System.Private.CoreLib' と 'System.Runtime.Loader' の
  両方に存在します` になる。理由: .NET のシェアードフレームワークの多くの「分割」アセンブリは
  **実体もただの型フォワーダーで、実装は System.Private.CoreLib に直接入っている**ため、
  暗黙のファサード参照(ref-pack の `System.Runtime.Loader.dll` など、これは type-forward せず
  独自に TypeDef を持つ「参照専用」アセンブリ)と実体参照(`System.Private.CoreLib` へ収束する)が
  同じ型名を二重に定義してしまう。
- **解決**: `<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>` を設定して
  SDK 既定の ref-pack 参照セットを丸ごと外し、代わりに **実行中の .NET SDK のシェアードフレームワーク
  ディレクトリ**(`$(NetCoreRoot)shared\Microsoft.NETCore.App\$(BundledNETCoreAppPackageVersion)\`、
  `NetCoreRoot`/`BundledNETCoreAppPackageVersion` は SDK が既定で公開している MSBuild プロパティ)
  にある `System.*.dll`/`Microsoft.*.dll`/`mscorlib.dll`/`netstandard.dll`/`WindowsBase.dll` を
  **全部**直接参照する(`<Reference Include="$(NetCoreSharedFrameworkDir)System.*.dll;...">`)。
  これは `dotnet-port\build-stage2-core.ps1`/`gen-default-rsp.ps1` が ncc 自身の `-ref:` に対して
  行っている「実体分割アセンブリをそのまま渡す」手法の C# コンパイル版に相当する。
  - ネイティブバイナリ(`coreclr.dll`/`clrjit.dll`/`hostpolicy.dll` 等、`*.dll` の素朴なワイルドカードに
    紛れ込む)は `MSB3246: PE image does not have metadata` 警告になるだけで実害は無いが、
    名前パターンを `System.*`/`Microsoft.*`/`mscorlib`/`netstandard`/`WindowsBase` に絞ることで解消。
  - `<Reference Include="<フルパス>*.dll">` という**ファイルパスのワイルドカード**を `Include` に
    直接書く形が最も簡潔(`Name`+`HintPath` の2段構えや `@(Item)` 経由の transform 方式は
    アイテムメタデータのバッチングが絡んで壊れやすい実測結果があった; そちらは不採用)。
  - `Nemerle.MSBuild.Tasks` 側はこの問題と無縁(Nemerle.*/Hosting への**コンパイル時参照を一切持たず**、
    すべてリフレクション越しに呼ぶ設計そのままで済んだ)。

### 2.3 XML コメント内の `--`

MSBuild の `.csproj`/`.targets` も XML なので `<!-- ... -- ... -->` は
`MSB4025: An XML comment cannot contain '--'` でパース不能になる。ドキュメントコメント中の
「〜だが -- 実際には」のような表現を全部 `;` や `,` に置き換えた
(C# の `///` ドキュメントコメントは同じ制約を受けない — 属性値やコメントでない地の文の
`--` も同様に無害、ハマったのは `<!-- -->` ブロックの中身のみ)。

### 2.4 `ManagerClass` サブクラスのコンストラクタと仮想呼び出しの順序

`ManagerClass(CompilationOptions)` のコンストラクタは内部で `ResetCompilerState` を呼び、そこから
**（override 済みの）`CreateComponentsFactory()` を base コンストラクタの中で呼ぶ**
(`ncc\passes.n:381-404`)。C# の実行順序では base コンストラクタが先に走るため、サブクラス
(`HostedManager`)の**インスタンスフィールドはこの時点でまだ未設定**(古典的な
「コンストラクタ内の仮想呼び出し」問題)。対策として ALC 参照は `[ThreadStatic] static` の
`HostedManager.CurrentAlc` に持たせ、`new HostedManager(...)` を呼ぶ**直前**に
(同じ専用スレッド上で)セットする方式にした(`ManagerClass.Instance` 自体が `[ThreadStatic]`
であることと同じ考え方)。

---

## 3. 要調査ポイントの結論

### 3.1 ComponentsFactory の差し替え可否 → **可能(サブクラス経由)**

- `ManagerClass.CreateComponentsFactory()` は `protected virtual`(reflection で実測: `Family, Virtual`)。
- `CompilerComponentsFactory.CreateLibraryReferenceManager(ManagerClass, list<string>)` は
  `[AbstractFactory]` マクロ生成のため `public virtual`(reflection で実測)。
- `LibraryReferenceManager` の `assemblyLoad(string)`/`assemblyLoad(AssemblyName)`/`assemblyLoadFrom(string)`
  はいずれも `protected virtual`(reflection で実測)、コンストラクタは `public`。
- 3 クラスとも `public`(`sealed` ではない)ので、C# から素直にサブクラス化して差し替えられた。
  実装: `HostedManager : ManagerClass` → `HostedComponentsFactory : CompilerComponentsFactory` →
  `AlcLibraryReferenceManager : LibraryReferenceManager`(3 段のサブクラス連鎖)。
- リスク表の「ComponentsFactory を外から差し替えられない」懸念は**解消**。フォールバック
  (default-ALC ロードのまま出荷)は不要だった。

### 3.2 CoreEmitBridge の cross-ALC 相互作用 → **問題なし(実測)**

- `CoreEmitBridge.EnsureLoaded()`(`ncc\generation\CoreEmitBridge.n:71`)は
  `Assembly.LoadFrom(helperPath)` で `Nemerle.CoreEmit.dll` を**常に既定 ALC**にロードする
  (呼び出し元の `Nemerle.Compiler.dll` がどの ALC にいても関係ない、.NET Core の
  `Assembly.LoadFrom` の既知の仕様)。
- HelloCore(exe 生成)・RefDemo(2 アセンブリ生成、うち 1 つは `ProjectReference` 越しの
  参照ロードも伴う)の両方で実際にコード生成(`AssemblyBuilder.Save` 相当の CoreEmit 呼び出し)が
  成功し、生成された exe/dll が正しく実行できたことを実測した — つまり
  「custom collectible ALC 内の `Nemerle.Compiler.dll`」から「default ALC 内の
  `Nemerle.CoreEmit.dll`」への呼び出しは、両者がやり取りする型(`AssemblyName`、
  `System.Reflection.Emit.AssemblyBuilder`、`MethodInfo`、`bool`、`string[]` 等)が
  すべて CoreLib/共有フレームワーク型なので問題なく動作する。
- `Assembly.LoadFrom` は同一パスの再ロードをキャッシュする(.NET Core の既定動作)ため、
  コンパイル毎に新しい collectible ALC で `Nemerle.Compiler.dll` がロードされ直しても
  `Nemerle.CoreEmit.dll` は初回ロード分が使い回される(RefDemo の 2 回連続コンパイルで実証)。
  CoreEmit.dll 自体はプロセス寿命中ロードされ続けるが、それはビルドツールの一部であり
  ユーザーの参照 dll ではないので、ファイルロック問題(検証4の対象)には影響しない。
- リスク表の「cross-ALC 相互作用が壊れる」懸念は**解消**。default-ALC 実行フラグは不要だった。

---

## 4. 検証結果(受け入れ基準 1〜6)

| # | 内容 | 結果 | 証拠の要点 |
|---|---|---|---|
| 1 | HelloCore: build/実行/増分/`clean`/`-p:DebugType=none` | **PASS** | `dotnet build HelloCore.nproj` 成功→`dotnet exec`で `Hello from HelloCore...` / `2 4 6` 出力。2 回目ビルドで `"CoreCompile" を省略します`(増分スキップ)。`dotnet clean` で bin 空。`-p:DebugType=none` で `.pdb` 無し、既定では `.pdb` 有り。 |
| 2 | RefDemo: build/実行(49/15)、1 プロセス内 2 コンパイル | **PASS** | `dotnet build App.nproj`(ProjectReference 経由で MathLib→App の順に 2 回 `NccCompile` 実行、同一 MSBuild プロセス)成功。`dotnet exec App.dll` → `Square(7) = 49` / `Sum([1..5]) = 15`。ALC 隔離(2 回目のコンパイルが 1 回目の静的状態に汚染されない)を実地確認。 |
| 3 | 構造化診断(エラー/警告)、失敗時 exit code、obj 汚染なし | **PASS** | scratchpad に一時 `.nproj` を作成。型エラー: `Bad.n(7,13,7,33): nemerle error : expected int, got string...`、`exit 1`。警告(未使用ローカル): `Bad.n(7,9,7,10): nemerle warning : a local value x was never used...`、`exit 0`。失敗ビルド後の `obj\` に `.dll`/`.pdb` が残らないこと、再ビルドで `CoreCompile` が(スキップされず)再実行されることを確認。 |
| 4 | ファイルロック/node reuse(`-m -nodeReuse:true`、MathLib 変更→再ビルド) | **PASS** | RefDemo を `-m -nodeReuse:true` でビルド→`Math.n` の `Square` を `x*x+1000` に変更→同条件で再ビルド→`file in use` 等のエラーなし、`Square(7) = 1049` に変化が反映。3 回目(変更なし)ビルドは増分スキップ。テスト後 `Math.n` を元に戻し `49` に復帰することも確認。 |
| 5 | `-p:NemerleUseExec=true` で旧 Exec 経路 | **PASS** | `dotnet build HelloCore.nproj -p:NemerleUseExec=true -v:normal` のログに `CoreCompile:` 配下で `dotnet "...\ncc.dll" -target:exe -debug -out:"..."  "...\hello.n"` という `<Exec>` コマンドラインがそのまま出力され、ビルド・実行とも成功。 |
| 6 | 無回帰(`ncc/`・CLR4 ビルド無変更) | **PASS** | `git status --porcelain ncc/ lib/ macros/` が空。変更/新規ファイルはすべて `dotnet-port\` 配下。testsuite は計画どおり未実行(スコープ外)。 |

---

## 5. 既知の制約・積み残し

- **警告コード(`Nnnnn`)が構造化診断に載らない**(2.1 節/確定事実3のとおり)。
  `ncc\parsing\Utility.n` の `RunWarningOccured` 呼び出し位置がコード付与より前にあるため。
  1 行の修正で直せるが、コンパイラー本体の変更(Stage リビルド)が要るため v1 スコープ外。
  現状は `LogWarning` に `warningCode=null` で出す(MSBuild 上は無コード警告として表示される)。
- **`MessageOccured` イベント経由の info(severity=0)診断**は実装したが、実際にどんな文言が
  流れてくるかは未調査(ncc 内部の `Message.Hint`/デバッグ用途と推測)。実害はない
  (`Importance.Low` で `LogMessage` するのみ)。
- **PackageReference 経由の参照**は `Nemerle.Core.targets` の `@(ReferencePath)` 配線を
  そのまま踏襲しており(WP-I2 で ProjectReference は実証済み)、今回新たに検証はしていない。
- **node reuse の「真の」プロセス再利用**(同一 MSBuild ワーカーノードプロセスがビルドをまたいで
  生き続けること)そのものはこの検証環境(サンドボックス化された Bash ツール越しの逐次コマンド実行)
  では厳密には観測できなかった。ただし機能的な結果(連続ビルドでのファイルロックエラー無し、
  変更の正しい反映)は確認済みで、受け入れ基準4の実質を満たす。
- **Linux 版 targets の実地検証は未実施**(WP-I2 での WSL 実証はそのまま活きるはずだが、今回の
  WP-A3 差分自体は Windows でのみ動作確認)。設計は Windows 版と対称(`AssemblyLoadContext`
  ベースのタスク自体は OS 非依存)。
- **`Nemerle.MSBuild.Tasks` の Microsoft.Build.Utilities.Core バージョン固定**(17.13.9、
  ローカル NuGet キャッシュにあったもの)。`ExcludeAssets="runtime"` のため実行時は
  MSBuild 同梱の実体を使うので、実運用の MSBuild バージョンとの厳密な整合は takenていない
  (API 互換性に依存)。

---

## 6. 参考: `git status` (ncc/lib/macros 無回帰の証跡)

```
$ git status --porcelain ncc/ lib/ macros/
(出力なし)

$ git status --porcelain
 M dotnet-port/msbuild/Nemerle.Core.targets
 M dotnet-port/msbuild/linux/Nemerle.Core.targets
 M dotnet-port/pack-tool.ps1
?? dotnet-port/docs/20-inproc-task-plan.md
?? dotnet-port/docs/20-inproc-task-log.md
?? dotnet-port/Nemerle.Compiler.Hosting/
?? dotnet-port/Nemerle.MSBuild.Tasks/
```

(`dotnet-port/dist/` は `.gitignore` 済みなので pack-tool.ps1 の再生成物は表示されない。
`Nemerle.Compiler.Hosting/bin|obj`・`Nemerle.MSBuild.Tasks/bin|obj` はルート `.gitignore` の
`[Bb]in*/`/`[Oo]bj*/` パターンで既にカバー済み。)
