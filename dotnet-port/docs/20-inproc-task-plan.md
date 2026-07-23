# WP-A3: インプロセス MSBuild タスク — 実装計画

目的: `Nemerle.Core.targets` の `<Exec dotnet ncc.dll ...>` を、`Nemerle.Compiler.dll` の API を
直接呼ぶ MSBuild タスク `NccCompile` に置き換える。狙いは (a) コンパイル毎のプロセス起動の解消、
(b) **構造化診断**(file/line/col/severity を MSBuild の Log.LogError/LogWarning へ → IDE の
エラー一覧・Problems ペインに正しく出る)。live squiggle(LSP)はスコープ外。

前提知識: `DISTRIBUTION.md`(現行 targets の到達点)、`02-build-flow.md`(MSBuild 統合)、
メモリの WP-A2 設計メモ。**v1 はコンパイラー本体(ncc/*.n)を一切変更しない**
(変更すると Stage1→Stage2 フルリビルド=版ハザード対応が必要になるため)。

---

## 調査済みの確定事実(実装の土台)

1. **インプロセスホスティングの前例**: `ncc\codedom\NemerleCodeCompiler.n` の
   `CompileAssemblyFromFileBatch`(104〜190 行)。パターン:
   `CompilationOptions()` → `ManagerClass(cOptions)` → `man.ErrorOccured += ...` /
   `man.WarningOccured += ...` → `cOptions.GetCommonOptions()` + NonOption ハンドラーで
   `Getopt.Parse(Message.Error, opts, argsList)`(この overload は Environment.Exit しない)
   → `cOptions.Sources = sources`(`FileSource(path, cOptions.Warnings)`)→
   `man.InitOutput(writer)`、`cOptions.ProgressBar = false`、`cOptions.IgnoreConfusion = true`
   → 大スタックスレッド(64bit で 40MB)上で `man.Run()`、例外
   (FileNotFound/Recovery/MatchFailure/ICE/AssertionException/AssemblyFindException/Exception)
   を捕捉して Message.Error に変換。※codedom 自体は core ビルドから除外されている(WP-C)が
   パターンはそのまま有効。`ncc\main.n` も同型(そちらは Environment.Exit する点だけ違う)。
2. **診断イベント**: `ncc\passes.n:104-106` — `ErrorOccured` / `WarningOccured` /
   `MessageOccured : MessageEventHandler`(= `(Location, string)`)。`Location` は
   `File / Line / Column / EndLine / EndColumn` を持つ。`Location.Default` の場合は
   `LocationStack.Top()` へフォールバック(codedom 112-121 行と同じ処理を入れる)。
3. **警告コードの制約**: `ncc\parsing\Utility.n:197-216` — `RunWarningOccured(loc, m)` は
   `N$code` プレフィックス付与**前**に発火するため、イベント購読者は警告コードを得られない。
   → v1 は警告をコード無しで LogWarning する(subcategory="nemerle")。イベント発火位置の
   1 行修正はコンパイラー変更なので将来の別 WP(他の変更と抱き合わせで Stage リビルド時に)。
4. **参照ロードとファイルロック**: `ncc\external\LibraryReferenceManager.n` は参照を
   `SR.Assembly.LoadFrom(path)`(224-229 行、CoreCLR では **default ALC** に入る)でロードする。
   インプロセスだと MSBuild 常駐ノード(node reuse)に ProjectReference の出力 dll が
   ロックされ、リビルドが `file in use` で壊れる恐れがある。ただし:
   - `assemblyLoad(name)` / `assemblyLoadFrom(path)` は **protected virtual**(224-229 行)。
   - 生成は `ManagerClass.ComponentsFactory.CreateLibraryReferenceManager(this, ...)` 経由
     (`ncc\passes.n:540-541`)。
   → ホスティング側(C#)で `LibraryReferenceManager` をサブクラス化し、ロードを
   **自分の(collectible)ALC** へルーティングする。`ComponentsFactory` の差し替え可否
   (public/protected、mutable か)は要確認 — ダメなら `ManagerClass` サブクラスから
   protected フィールドを設定する等、無改造で通る道を探す(VS 統合も昔サブクラス化していた)。
5. **auto-ref はインプロセスでも成立**: `LoadCoreStdlibReferences`(`ncc\passes.n:573-606`)は
   `typeof(object).Assembly.Location`(=MSBuild プロセスでも同じ共有フレームワーク)と
   「コンパイラーアセンブリの隣の Nemerle.dll」(ALC でパスロードすれば `Assembly.Location`
   は生きる)で解決するので、そのまま動くはず。
6. **CoreEmitBridge**: `ncc\generation\CoreEmitBridge.n:71` が `Assembly.LoadFrom(helperPath)` で
   Nemerle.CoreEmit.dll を **default ALC** にロードする。compiler⇔CoreEmit の相互作用は
   リフレクション+System/SRE 型(CoreLib=全 ALC 共有)なので cross-ALC でも動く見込みだが、
   **要実測**。問題が出たらホスティング ALC 側で CoreEmit を先回りロードして
   `Assembly.LoadFrom` のパスキャッシュに頼らない等の対策を検討(コンパイラー無改造の範囲で)。

---

## 成果物(新規/変更ファイル)

### 1. `dotnet-port\Nemerle.Compiler.Hosting\`(新規、C#、net10.0)

ブリッジアセンブリ。**collectible ALC の中に**(Nemerle.Compiler.dll と同じ ALC で)ロードされ、
コンパイラーを強い型付けで呼ぶ。タスクとの境界は CoreLib 型のみ(string/int/bool/Action<...>)
なので ALC 越しでも型同一性問題が起きない。

- `Nemerle.Compiler.Hosting.csproj`: `<Reference>` HintPath で
  `dotnet-port\dist\ncc\Nemerle.Compiler.dll` と `Nemerle.dll` を参照
  (`Private=false`、レイアウトは pack-tool.ps1 で事前生成)。
- `public static class CompilerHost`:
  ```csharp
  // 戻り値: エラー数(0 = 成功)。決して Environment.Exit しない。
  public static int Compile(
      string[] args,                    // ncc の CLI 引数と同一(-target: -out: -ref: ソース...)
      // (file, line, col, endLine, endCol, severity 0=info/1=warn/2=error, message)
      Action<string,int,int,int,int,int,string> onDiagnostic,
      Action<string> onOutput)          // InitOutput 行単位転送
  ```
- 実装は確定事実 1 のパターンを踏襲。追加で:
  - `Options.LibraryPaths` にレイアウトディレクトリ(自 assembly の場所)を追加(main.n:163 と同じ)。
  - `ColorMessages = false`(ANSI エスケープが MSBuild ログを汚すため)。
  - 大スタックスレッド: main.n:140-147 と同様(64bit なら 160MB 予約)。
  - `Message.MaybeBailout` は呼ばない/BailOutException は捕捉(codedom と同じ、
    失敗はエラー数で判定)。
  - **ALC セーフな参照ロード**(確定事実 4): `LibraryReferenceManager` サブクラスで
    `assemblyLoadFrom(path)` を `AssemblyLoadContext.GetLoadContext(typeof(CompilerHost).Assembly)
    .LoadFromAssemblyPath(...)` に差し替え。`assemblyLoad(AssemblyName)` も同 ALC の
    `LoadFromAssemblyName` 相当へ。差し替え注入方法(ComponentsFactory)は要調査・実装。

### 2. `dotnet-port\Nemerle.MSBuild.Tasks\`(新規、C#、net10.0)

MSBuild タスクアセンブリ。MSBuild が自分の task-ALC にロードする。Nemerle.* への
コンパイル時参照は**持たない**(リフレクションのみ)。

- `Nemerle.MSBuild.Tasks.csproj`: `Microsoft.Build.Utilities.Core` PackageReference
  (`ExcludeAssets="runtime"` — 実行時は MSBuild 同梱のものを使う)。
- `public class NccCompile : Microsoft.Build.Utilities.Task`
  - パラメーター: `Sources`(ITaskItem[], Required)、`References`(ITaskItem[])、
    `OutputAssembly`(string, Required)、`TargetType`(string, 既定 "library")、
    `EmitDebug`(bool)、`AdditionalOptions`(string、空白区切り、任意)、
    `NccLayoutDir`(string, Required)。
  - 動作: コンパイル毎に
    1. collectible な custom ALC(`NccLoadContext : AssemblyLoadContext`)を生成。
       `Load(name)` はまず `NccLayoutDir` 内の `<name>.dll` を `LoadFromAssemblyPath`、
       無ければ null(=default へフォールバック、CoreLib/System.* はこちら)。
       **Nemerle.* と Nemerle.Compiler.Hosting は必ずレイアウトから解決**すること。
    2. ALC で `Nemerle.Compiler.Hosting.dll` をロード → `CompilerHost.Compile` を
       リフレクションで 1 回だけ取得・呼び出し。argv はタスクパラメーターから組み立て
       (`-target:` `-debug` `-out:` `-ref:` × N、AdditionalOptions、ソースパス列)。
       **プロセス起動もコマンドライン文字列化も無いので引用符問題が消える。**
    3. 診断デリゲート → `Log.LogError(subcategory:"nemerle", code:null, ..., file, line, col,
       endLine, endCol, message)` / `LogWarning` / `LogMessage(Importance.Low)`。
       file が空/Location.Default 由来はプロジェクトファイル位置で LogError。
    4. 終了後 `alc.Unload()` + `GC.Collect()`×2 + `WaitForPendingFinalizers`(ベストエフォート。
       アンロード完了は保証しなくてよいが、参照 dll のロック解放をテストで確認する)。
  - 戻り値: `!Log.HasLoggedErrors && errorCount == 0`。

### 3. `dotnet-port\msbuild\Nemerle.Core.targets`(Windows/Linux 両方を更新)

- `<UsingTask TaskName="Nemerle.MSBuild.Tasks.NccCompile"
  AssemblyFile="$(NemerleTaskAssembly)" Condition="'$(NemerleUseExec)' != 'true'"/>`。
  `NemerleTaskAssembly` 既定 = `$(NccLayoutDir)msbuild-task\Nemerle.MSBuild.Tasks.dll`。
- `CoreCompile` 内を二本立てに:
  - 既定: `<NccCompile Sources="@(NemerleCompile)" References="@(_NemerleUserReference)"
    OutputAssembly="%(IntermediateAssembly.FullPath)" TargetType="$(NemerleTargetType)"
    EmitDebug="..." NccLayoutDir="$(NccLayoutDir)" />`
  - `$(NemerleUseExec) == 'true'`: 現行の `<Exec>`(そのまま残す=エスケープハッチ)。
- `Inputs` に `$(NemerleTaskAssembly)` を追加。既存の PDB/clean/参照配線/
  NemerleCopyRuntimeAssemblies はそのまま再利用(変更不要のはず)。
- Linux 版は Windows 版との差分方針(パス区切りのみ)を維持。タスク方式なら
  `dotnet exec` 起動差分も消えるので、差分はさらに縮むはず。

### 4. `dotnet-port\pack-tool.ps1`(更新)

- `dotnet build -c Release` で Hosting と Tasks をビルドし、
  `dist\ncc\` に `Nemerle.Compiler.Hosting.dll`(Nemerle.Compiler.dll の隣、ALC 解決のため)、
  `dist\ncc\msbuild-task\Nemerle.MSBuild.Tasks.dll` を配置するステップを追加。
- 注意: Hosting のビルドは dist レイアウトの存在に依存する(参照 HintPath)ので、
  「レイアウト組み立て → Hosting/Tasks ビルド → コピー」の順にする。

### 5. ドキュメント: `dotnet-port\docs\20-inproc-task-log.md`(新規、作業ログ)+ `DISTRIBUTION.md` 追記

---

## 検証項目(受け入れ基準)

1. **HelloCore**: `dotnet build dotnet-port\samples\HelloCore\HelloCore.nproj` 成功 →
   `dotnet exec ...\HelloCore.dll` 実行 OK。増分ビルド(2 回目は CoreCompile スキップ)、
   `dotnet clean` で bin 空、`-p:DebugType=none` で pdb 無し — 全て現行と同じに保つ。
2. **RefDemo**: `dotnet build ...\App.nproj` 成功・実行 OK(49 / 15)。
   ※1 プロセス内で 2 コンパイル(MathLib→App)が走るケースがあり、**ALC 隔離
   (静的状態リセット)の実地テスト**を兼ねる。
3. **構造化診断**: 故意の構文エラー/型エラー/警告を含む一時 .n で
   `dotnet build` → MSBuild 出力に `path(line,col,endline,endcol): error : msg` 形式で出る
   (= Log.LogError 経由の証拠)。ビルドは失敗コードで終了。obj に壊れた出力が残って
   増分ビルドを騙さないこと(失敗時は IntermediateAssembly が無い/古い状態で、
   次回リビルドされる)。
4. **ファイルロック/node reuse**: node reuse を有効にして(例: `dotnet build -m` または
   `dotnet msbuild /nr:true /m`)RefDemo をビルド → `MathLib\Calc.n` を変更 → 再ビルド。
   `The process cannot access the file` 等が出ず、変更が App に反映されること。
   これが通らない場合は LoadFromStream(バイト読み=ロック無し)への切り替えを検討。
5. **Exec フォールバック**: `-p:NemerleUseExec=true` で従来経路が生きていること。
6. **無回帰**: コンパイラー本体・CLR4 ビルドは無変更(git status で ncc/ 配下に diff が
   無いこと)。testsuite 再実行は不要。

## リスクと逃げ道

| リスク | 対策 |
|---|---|
| ComponentsFactory を外から差し替えられない | ManagerClass サブクラス(protected アクセス)経由。それも不可なら v1 は default-ALC ロードのまま出荷し、検証 4 を「既知の制約」に落とす(Exec フォールバック常用可) |
| CoreEmitBridge の cross-ALC 相互作用が壊れる | 実測して問題があれば、compile を default ALC で行う(隔離を諦める)フラグを暫定投入。恒久対応は将来 WP |
| ALC unload が遅延しロックが残る | 参照ロードを LoadFromStream 化(パスキャッシュ注意) |
| ncc が Console に直接書く箇所 | ProgressBar=false + InitOutput で大半カバー。残りは実測で確認(Console.SetOut のプロセスグローバル差し替えは MSBuild では不可) |

## スコープ外(明記)

- 警告 N コードのイベント露出(コンパイラー 1 行修正 + Stage リビルド)— 将来 WP。
- LSP / live squiggle。
- Nemerle.Tool(dotnet tool シム)のインプロセス化 — 同じ Hosting ブリッジで後日可能。
