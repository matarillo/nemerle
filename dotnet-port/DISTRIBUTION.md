# 配布形態の整備 (WP-I2) — 現状まとめ

対象: ncc.exe(.NET 10 でセルフホスト済み、`bin\Release\core\Stage2`/`Stage3`)を
「現代的で配布可能な形」にどこまで近づけられるか。深いランタイム移植ではなく、
パッケージング/ツールチェーン整備が主眼。前提ドキュメント: `00-PLAN.md`(全体計画)、
`02-build-flow.md`(MSBuild 統合の仕組み)、`13-stage2-log.md`(stage2 の参照戦略)。

本ドキュメントは3つの成果物の到達点・再現手順・既知の制約をまとめる。

- タスク1: `dotnet ncc` 配布レイアウト(`pack-tool.ps1`) — **完全に動作**
- タスク2: `dotnet tool` 化 PoC(`Nemerle.Tool`) — **PoC 成功**(制約付き)
- タスク3: SDK スタイル MSBuild 統合 PoC(`msbuild\Nemerle.Core.targets`) — **PoC 成功**(制約付き)

> **更新 (2026-07-13, WP-A2 — dotnet ネイティブ化レベル A)**: 本ドキュメント中の
> 「毎回長い `-ref:` 列 / `ncc.default.rsp` が必要」という前提は**もう当てはまらない**。
> `ncc\passes.n` に CoreCLR 用のデフォルト参照自動解決(`LoadCoreStdlibReferences`)を実装し、
> `dotnet ncc.dll hello.n` が rsp なしで動くようになった(コミット 56964d879)。
> さらに `Nemerle.Core.targets` は (a) `-from-file:ncc.default.rsp` を撤廃、(b) `@(ReferencePath)`
> を `-ref:` として配線(フレームワーク ref パック facade は `%(FrameworkReferenceName)` で除外)
> したので、`ProjectReference`/`PackageReference` を持つ `.nproj` も `dotnet build` できる
> (コミット ee2ae06f3、`samples\RefDemo` で実証)。以下の「制約」節のうち rsp 依存・参照未配線に
> 関する記述はこの2コミットで解消済み。`pack-tool.ps1` の rsp/cmd/gen-default-rsp 生成は
> now 任意(レガシー)で、整理は未実施。

---

## 1. `dotnet ncc` 配布レイアウト — `dotnet-port\pack-tool.ps1`

### やったこと

`build-stage2-core.ps1` が作る `bin\<Cfg>\core\Stage2\`(または `Stage3\`)は、
すでに `dotnet exec <dir>\ncc.exe ...` で動作する(13-stage2-log.md)。しかし:

1. `ncc.exe` は「`dotnet exec` でしか動かない」という体裁で、`dotnet <dll>` という
   現代的な素の呼び出し規約に乗っていない(実際には拡張子は無関係で動くことを検証済み
   — 後述)。
2. 標準ライブラリを見せるためだけに、`-use-loaded-corlib` + 共有フレームワークの
   実体分割アセンブリ群への `-ref:` という長い手組みの引数列(`build-stage2-core.ps1`
   参照)を毎回書く必要があり、「デフォルトで動く」体験がない。

`pack-tool.ps1` は既存の core コンパイラーディレクトリ(既定 `Stage2`)から、
実行可能な自己完結レイアウトを組み立てる:

```
dotnet-port\dist\ncc\
  ncc.exe / ncc.dll          -- 同一バイト列。ncc.runtimeconfig.json のベース名は
                                 "ncc" なので拡張子に関係なく両方をカバーする
  ncc.runtimeconfig.json
  Nemerle.dll / Nemerle.Compiler.dll / Nemerle.Macros.dll / Nemerle.CoreEmit.dll
  ncc.default.rsp            -- 標準参照セット(-no-stdlib -use-loaded-corlib
                                 -greedy-references:- + 実体分割アセンブリ + 自身の
                                 Nemerle.dll への -ref:)。**絶対パスはマシン/設置場所固有**
                                 (下記「再配置(relocation)」参照)
  gen-default-rsp.ps1        -- ncc.default.rsp を現在の設置場所・ランタイムから再生成
  ncc.cmd                    -- 利便性ラッパー(後述)。移動を検知して rsp を自動再生成
```

> **再配置(relocation)について**: `ncc.default.rsp` の `-ref:` は絶対パスである必要がある
> (ncc は `-ref:` の相対パスをカレントディレクトリ基準で解決するため、相対パスでは
> 任意の場所から使えない)。そのため、このレイアウトを別マシン・別ディレクトリへコピーすると、
> pack 時に焼き込まれた (a) 共有フレームワークのパス(`...\Microsoft.NETCore.App\10.0.9\` の
> ようにバージョン番号込み)と (b) 自身の `Nemerle.dll` のパスが両方とも無効になる。
> これを避けるため、同梱の `gen-default-rsp.ps1` が **自分の設置場所** と **その場の
> インストール済みランタイム** から全絶対パスを再計算する。`ncc.cmd` は rsp 内の
> `Nemerle.dll` パスが現在地を指していない(=移動された)ときにこれを自動実行するので、
> **`ncc.cmd` 経由なら配布物はそのまま再配置可能**。`dotnet <dir>\ncc.dll -from-file:...`
> の直接呼び出しパスを使う場合や、.NET ランタイムをアップグレードした場合は、
> `gen-default-rsp.ps1` を一度手動実行して rsp を更新すること。

### 検証済みの事実

- **`dotnet <path>\ncc.exe` は `exec` なしでもそのまま動く**(`dotnet exec` と同じ)。
  さらに **同じファイルを `ncc.dll` にコピーしただけでも動く**
  (`ncc.runtimeconfig.json` のベース名が拡張子非依存の "ncc" で両方に効くため)。
  つまり現状の stage2/stage3 出力は追加コード変更なしで「`dotnet <dll>`」規約に
  すでに適合していた — 必要だったのは新しい実装ではなく、レイアウト整理と
  デフォルト参照のラップだった。
- `-from-file:` は Getopt の `SubstitutionString`(`lib\getopt.n` / `ncc\CompilationOptions.n`)
  であり、**その場でファイル内容を再帰的にパースしてから残りのコマンドラインの
  パースを続ける**(`parse_opts` の再帰呼び出しの後に外側の `parse_opts(rest)` が
  必ず実行される、`lib\getopt.n:265-288` で確認)。よって
  `-from-file:ncc.default.rsp -out:hello.exe hello.n` は
  「rsp の内容 + `-out:hello.exe hello.n`」と等価に振る舞う。これが
  「ラッパー rsp」方式の土台。

### 再現手順

```powershell
pwsh dotnet-port\pack-tool.ps1                # 既定: Stage2 -> dotnet-port\dist\ncc
# 任意のディレクトリで:
dotnet <repo>\dotnet-port\dist\ncc\ncc.dll -from-file:<repo>\dotnet-port\dist\ncc\ncc.default.rsp -out:hello.exe hello.n
dotnet exec hello.exe
# または利便性ラッパー:
<repo>\dotnet-port\dist\ncc\ncc.cmd -out:hello.exe hello.n
dotnet exec hello.exe
```

hello.n(`printf` のみ)・hello2.n(`Nemerle.Collections` の `list`/`Map` 使用、
generics を含む)の両方で、レイアウトから離れた任意のディレクトリからのコンパイル・
実行を確認済み(後者は実行時に `Nemerle.dll` が必要 — 下記参照)。

### `ncc.cmd`(`.ps1` ではなく `.cmd` にした理由 — ハマった点)

最初は `ncc.ps1` を書いたが、**PowerShell はスクリプト/コマンドレット呼び出しの
引数トークン化で `-name:value` 形式を特別扱いする**ことが実測で判明した:

- `param()` の有無や `$args`/`ValueFromRemainingArguments` のどれを使っても、
  `-out:hello.exe` は `-out` と `hello.exe` の**2トークンに分割される**、あるいは
  PowerShell 標準の `-OutVariable`/`-OutBuffer` と**曖昧一致でエラーになる**
  (`Parameter cannot be processed because the parameter name 'out' is ambiguous`)。
- `--%`(stop-parsing トークン)は **`.ps1` スクリプト呼び出しには効かない**
  (ネイティブコマンド専用)。
- 一方、**`.cmd`(バッチファイル)は `%*` で完全にリテラルなまま引数を転送する**、
  かつ **PowerShell プロンプトから `.cmd` を呼んでも同じく無傷**(PowerShell の
  パラメーターバインダーは外部プログラム呼び出しには介入しないため)。

→ ncc の CLI が `-name:value` を多用する以上、フォワーディング用ラッパーは
`.ps1` ではなく `.cmd` にすべき、という結論(`pack-tool.ps1` のヘッダーコメントに
詳細実証込みで記録)。

### 実行時に `Nemerle.dll` が必要な場合(既知の制約、コンパイラーのバグではない)

`printf` のような一部のライブラリ機能はコンパイル時マクロとして完全にインライン
展開されるため、生成された exe は実行時に `Nemerle.dll` を一切参照しない
(実証済み: 生成 exe のバイト列に `"Nemerle"` という文字列が一つも現れない)。
しかし `list`/`Map` など通常のスタドライブラリ呼び出しを使うプログラムは、
実行時に `Nemerle.dll` を解決できないと `FileNotFoundException` になる
(.NET のアセンブリプローブはエントリアセンブリの隣と共有フレームワークしか
見ない。GAC は無い)。これは 12/18 ログで既知の "scratch-dir artifact" と同根。

`ncc.cmd` はコンパイルが成功したら**常に**(`-out:` の値を解析せず、シンプルに)
カレントディレクトリへ `Nemerle*.dll` をコピーする — 「出力先 = カレント
ディレクトリ」という一般的なケースをカバーする実用的なヒューリスティックであり、
`-out:` が別ディレクトリを指す場合は手動コピーが必要(`dotnet-port\dist\ncc\
Nemerle*.dll` を出力先へコピーするだけ)。

---

## 2. `dotnet tool` 化 — PoC 成功(`dotnet-port\Nemerle.Tool`)

### 結論

**実現可能。ただし ncc 本体を直接 `PackAsTool` の対象にはできず、C# シムを
1つ挟む必要がある。**

`dotnet pack -p:PackAsTool=true` は、**そのプロジェクト自身がビルドした
アセンブリ**を必ずエントリポイントにする(SDK が自動生成する
`DotnetToolSettings.xml` の `EntryPoint` は常に `$(AssemblyName).dll` 固定)。
既製の(Nemerle でビルド済みの)`ncc.dll` を直接 `PackAsTool` することはできない。

そこで `dotnet-port\Nemerle.Tool\Nemerle.Tool.csproj`(SDK スタイル、コンパイル
不要な既製バイナリの再パッケージ用)を新設:

- 実体は**小さな C# シム**(`Program.cs`)。`AppContext.BaseDirectory` から
  同梱された `ncc.dll` を見つけ、`Process.Start` で
  `dotnet <dir>\ncc.dll <転送引数>` を起動する。
  **`ProcessStartInfo.ArgumentList` で引数を渡すため、シェル/PowerShell 側の
  再トークン化を一切経由しない**(1.節の `.cmd` と同じ問題を回避する、より
  堅牢な方式)。
  > **更新 (0.2.0-poc1)**: auto-ref 化(`ncc\passes.n` の `LoadCoreStdlibReferences`)により
  > **シムは `-from-file:ncc.default.rsp` を前置しなくなった**。素の `ncc.dll hello.n` が
  > rsp なしでコンパイルできるため。`ncc.default.rsp` は .nupkg にも同梱しない。
- `dotnet-port\pack-tool.ps1` が組み立てた `dotnet-port\dist\ncc\` の
  `Nemerle*.dll`/`ncc.dll`/`ncc.runtimeconfig.json` を
  `<None Pack="true" PackagePath="tools\net10.0\any\">` として **.nupkg の
  tool content として同梱**(コンパイルはしない、ただの再パッケージ)。
- `PackageId=Nemerle.Ncc.DevTool`、`ToolCommandName=nemerle-ncc`
  (実運用の名前を占有しないよう、明確に "DevTool"/PoC 名にした)。

### 再現手順(実際に実行して確認済み)

```powershell
# 1. レイアウトと nupkg を作る
pwsh dotnet-port\pack-tool.ps1
dotnet pack -c Release dotnet-port\Nemerle.Tool\Nemerle.Tool.csproj -o dotnet-port\dist\nupkg

# 2. ローカルフィードからグローバルツールとしてインストール
#    (再パックのたびに csproj の <Version> を上げること: NuGet は (id,version) を
#     内容ごとキャッシュするため、同版で内容だけ差し替えると古いビットが使われる)
dotnet tool install --global --add-source dotnet-port\dist\nupkg Nemerle.Ncc.DevTool --version 0.2.0-poc1

# 3. 任意のディレクトリから使う
nemerle-ncc -out:hello.exe hello.n
dotnet exec hello.exe
nemerle-ncc -out:hello2.exe hello2.n     # list/Map 使用、generics
dotnet exec hello2.exe

# 後片付け
dotnet tool uninstall --global Nemerle.Ncc.DevTool
```

`dotnet pack` は警告なし(`NU5100`/`NU5128`/`NU5118` は既知の理由で意図的に
抑制 — 「lib/ref の外にアセンブリがある」「対象 TFM に lib/ref が無い」という、
ここでは正しい構成についての定型警告)で成功し、install → 実行 →
(hello.n・hello2.n とも)成功を確認済み。シムがコンパイル成功後に
`Nemerle*.dll` をカレントディレクトリへコピーする点も 1節の `.cmd` と同じ
ヒューリスティック。

### 既知の制約・今後

- 現状は **ローカル `--add-source` フィード限定の PoC**。nuget.org 等への
  実配布には、バージョニング方針(ncc 自身のバージョンと nupkg バージョンの
  対応)、ライセンス/著作権メタデータ、README、複数 RID/TFM 対応、CI
  でのビルド・パック自動化が必要 — 本 WP のスコープ外。
- シムは `dotnet` を子プロセスとして起動する(`Process.Start("dotnet", ...)`)
  ため、起動オーバーヘッドが `dotnet <ncc.dll>` 直接呼び出しの2倍近くになる
  (プロセス2段分)。将来的にはシム内で `Nemerle.Compiler.dll` の
  コンパイラー API を直接呼び出す(プロセス起動なし)方式に置き換えれば
  解消できるが、そのための Nemerle.Compiler.dll の公開 API 呼び出し規約の
  精査は未実施。
- ツールの一括アンインストール後の再インストールで PackageId 大文字小文字の
  差异により警告が出ることがある(NuGet 側の既知の挙動、実害なし)。

---

## 3. SDK スタイル MSBuild 統合 PoC — `dotnet-port\msbuild\Nemerle.Core.targets`

### 結論

**PoC 成功。`dotnet build` でサンプルプロジェクトが実際にビルド・実行できる
ところまで到達。** ただし SDK 側の言語ターゲット選択の都合で、プロジェクト
拡張子は `.csproj` ではなく `.nproj`(や `.csproj` 以外の任意の拡張子)を
使う必要がある、という制約が判明した。

### `tools\msbuild-task\MSBuildTask.cs` は .NET 10 でビルドできるか

**できない**(新規ビルドでの再確認はせず、既存の `02-build-flow.md` §6 の
解析結果を踏襲 — 十分に確度が高い既知の事実のため)。`Ncc : ManagedCompiler`
の基底クラス `Microsoft.Build.Tasks.ManagedCompiler` は最新の
`Microsoft.Build.Tasks.Core`(dotnet SDK 同梱)には存在しない
(`Microsoft.Build.Tasks.v4.0`/`Utilities.v4.0` 時代の型)。また
`CommandLineBuilder` の非 public メンバーへのリフレクション
(`GetCommandLine`/`AppendTextWithQuoting`)、`Microsoft.Win32.Registry` 依存も
移植の追加障害になる。**フルポートは別ワークパッケージ**であり、今回は
「ToolTask を書き直す」のではなく「`<Exec>` ベースで CoreCompile を上書きする」
という軽量な代替を PoC 実装した。

### 実装(`Nemerle.Core.targets`)

- 通常の SDK スタイルプロジェクト(`<Project Sdk="Microsoft.NET.Sdk">`)から
  `<Import Project="...\Nemerle.Core.targets" />` を追加するだけで使える。
- 独自アイテム型 `@(NemerleCompile)`(`Compile` ではない — C# のデフォルト
  glob と衝突させないため)を入力に、**`CoreCompile` ターゲットを丸ごと
  上書き**(csc/fsc/vbc など「本物のコンパイラー」が使うのと全く同じ
  拡張ポイント。旧 `Nemerle.MSBuild.targets` の Ncc タスクも同じ
  `CoreCompile` を上書きしていた、02-build-flow.md §3 参照)。中身は
  `<Exec Command="dotnet ncc.dll -target:... $(NemerleDebugSwitch) -out:@(IntermediateAssembly) @(参照->-ref:) @(NemerleCompile)" />`
  (auto-ref により **`-from-file:ncc.default.rsp` は不要**。`@(ReferencePath)` を `-ref:` に配線、
  `-debug` は DebugType/DebugSymbols に応じて付与)。
  `@(IntermediateAssembly)` は SDK の共通ターゲットが csc 用に計算済みの
  パスをそのまま再利用するため、以降の `CopyFilesToOutputDirectory`・
  `GenerateBuildRuntimeConfigurationFiles`・`Clean`・増分ビルドの
  最新判定(`Inputs`/`Outputs`)がすべて無改造で機能する。
- ビルド中(`AfterTargets="CopyFilesToOutputDirectory"`)に `Nemerle*.dll` を出力ディレクトリへ
  コピーし `@(FileWrites)` に登録(実行時スタドライブラリ依存への対処 + `dotnet clean` 対応)。

### ハマった点(重要な制約として残る)

1. **`.csproj` 拡張子だと csc が勝つ**。SDK スタイルプロジェクトは
   `Sdk="Microsoft.NET.Sdk"` により暗黙に「本体の前に `Sdk.props`、
   本体の後ろに `Sdk.targets`」がインポートされる。`.csproj` の場合
   `Sdk.targets` が `Microsoft.CSharp.CurrentVersion.targets`
   (csc ベースの `CoreCompile` を定義)を**必ず**インポートし、それは
   プロジェクト本文中の(=より前にある)`<Import Project="Nemerle.Core.targets">`
   より**後**に評価される。MSBuild は「同名ターゲットは最後の定義が勝つ」
   ため、`.csproj` では常に csc の `CoreCompile` が勝ってしまう
   (実測: `EnableDefaultCompileItems=false` にしても
   `error CS5001: プログラムは、エントリ ポイントに適切な静的 'Main' メソッドを
   含んでいません` が csc から出る=csc が実際に動いてしまっている証拠)。
   → **回避策: プロジェクト拡張子を `.nproj`(や他の非 `.csproj`)にする**。
   この場合 SDK は言語別ターゲットを一切インポートしないため、
   `Nemerle.Core.targets` の `CoreCompile` 定義がそのまま最終定義になる。
   副作用として `CreateManifestResourceNames` のような C#/VB 言語ターゲットが
   本来定義する補助ターゲットが無くなる(`Nemerle.Core.targets` 側で
   空スタブとして補っている — 現状これ1つだけ必要だった)。
2. **`ProduceReferenceAssembly`(既定 true)を切る必要がある**。Roslyn の
   `/refout:` 機能(`obj\...\refint\` への参照アセンブリ出力)に ncc は
   対応しないため、既定のままだと `指定されたファイル "...\refint\....dll"
   は存在しません` で失敗する。`Nemerle.Core.targets` 側で
   `ProduceReferenceAssembly=false` を既定にして解消。

### 再現手順

```powershell
pwsh dotnet-port\pack-tool.ps1     # dotnet-port\dist\ncc が必要
dotnet build dotnet-port\samples\HelloCore\HelloCore.nproj
dotnet exec dotnet-port\samples\HelloCore\bin\Debug\net10.0\HelloCore.dll
```

`hello.n`(`printf` + `Nemerle.Collections` の `list`/`Map`、generics)で
ビルド成功・実行成功・**増分ビルド(2回目は `CoreCompile` をスキップ)**
まで確認済み。~~`dotnet clean` が `bin\` 配下を消さない~~ 問題は **0de978022 で解消**
(ランタイム dll コピーを `@(FileWrites)` に登録)— `dotnet clean` 後に bin が空になることを実測。

### 既知の制約・今後

- ~~参照アセンブリ(`ProjectReference`/`PackageReference`)の依存解決~~ →
  **ProjectReference は実装・実証済み (ee2ae06f3, `samples\RefDemo`)**。`@(ReferencePath)` を
  `-ref:` に配線し、フレームワーク ref パック facade は `%(FrameworkReferenceName)` で除外
  (ncc が実体を自動解決するため)。PackageReference は同経路だが未実測。
- ~~`dotnet clean` の bin\ 削除、`-debug`/PDB 配線、マルチプロジェクトビルド~~ →
  **完了 (0de978022)**。clean/PDB 配線済み、複数プロジェクト(MathLib→App)も RefDemo で実証。
- **Linux/Unix 対応**: `dotnet-port\msbuild\linux\Nemerle.Core.targets` を追加 (253d2ecb3)。
  NccLayoutDir 既定 `../ncc/`・`dotnet exec ncc.dll` 起動・スラッシュパスのみが Windows 版との差分。
  **WSL で end-to-end 実証済み**: Windows でビルドした core ncc の DLL 群がそのまま Linux で動作し、
  `dotnet build HelloCore.nproj` → 実行まで成功(PDB・ランタイム dll コピー含む)。生の CLI
  (`dotnet exec ncc.dll hello.n`)は生成 exe 隣の `Nemerle.dll` 不足で実行時エラー(OS 非依存の
  既知 gap)だが、MSBuild 統合は自動コピーで解消。
- `.nproj` 拡張子を使う制約そのものは実用上大きな障害ではない(Visual Studio 等の IDE 統合まで
  考えるなら別途「Nemerle 言語 SDK」相当が必要になるが、CLI ビルドの範囲では `.nproj` で十分機能する)。

---

## まとめ表

| 項目 | 状態 | 再現性 |
|---|---|---|
| `dotnet <layout>\ncc.dll` 配布レイアウト | **完全動作** | `pack-tool.ps1` で再現可、hello.n/hello2.n 実証済み |
| `dotnet tool` 化 | **PoC 成功**(C# シム経由) | install→実行の手順を記載、実証済み |
| SDK スタイル MSBuild 統合 | **動作**(`.nproj` 拡張子。参照/PDB/clean/マルチプロジェクト/Linux 対応済み) | Windows・WSL で `dotnet build`→実行を実証 |
| 既存 CLR4 ビルド(`NemerleAll.nproj`/`build-stage2-core.ps1`) | **無改造・無回帰** | 新規ファイルのみ追加(`.gitignore` のみ既存ファイルに軽微な追記) |

## 変更・追加ファイル一覧

- 新規: `dotnet-port\pack-tool.ps1`
- 新規: `dotnet-port\Nemerle.Tool\Nemerle.Tool.csproj`, `Program.cs`(0.2.0-poc1 で rsp フリー化)
- 新規: `dotnet-port\msbuild\Nemerle.Core.targets`(参照配線/PDB/clean 追加)
- 新規: `dotnet-port\msbuild\linux\Nemerle.Core.targets`(Linux 版, 253d2ecb3)
- 新規: `dotnet-port\samples\HelloCore\HelloCore.nproj`, `hello.n`
- 新規: `dotnet-port\samples\RefDemo\`(MathLib ライブラリ → App exe の ProjectReference 例)
- 新規: `dotnet-port\DISTRIBUTION.md`(本ドキュメント)
- コンパイラー側: `ncc\passes.n`(CoreCLR デフォルト参照の自動解決 `LoadCoreStdlibReferences`, 56964d879)
- 既存改変(最小限): `.gitignore`(`dotnet-port/dist/` と `*.nupkg` を追加、
  生成物を誤って追跡しないため)、`dotnet-port\00-PLAN.md`(作業ログ追記)
- 生成物(既定では git 追跡対象外): `dotnet-port\dist\ncc\`(pack-tool.ps1 の
  出力)、`dotnet-port\dist\nupkg\`(`dotnet pack` の出力)

## 推奨される次ステップ

1. ~~`Nemerle.Core.targets` に `@(ReferencePath)` を `-ref:` として渡す配線~~
   → **完了 (ee2ae06f3)**。`samples\RefDemo` で ProjectReference を実証。
   PackageReference は同じ `@(ReferencePath)` 経路だが未実測。
2. **(現在の検討ポイント — 着手是非を判断中)** インプロセス化: `dotnet tool` シムの
   `Process.Start` / MSBuild の `<Exec>` による **ncc.dll 別プロセス起動を、
   `Nemerle.Compiler.dll` の API 直呼び**(`ManagerClass`/`CompilationOptions`/`Message`)
   へ置き換える。狙いは (a) 2段プロセス起動の解消、(b) **診断の構造化取得(IDE のエラー
   一覧向け)**。リスク: ncc は「1プロセス1コンパイル」前提で `Instance` シングルトン等
   プロセス全体の静的状態を持ち、MSBuild ノード再利用と衝突しうる(→ コンパイル毎に
   collectible AssemblyLoadContext で隔離する案)。診断のテキストパース方式は脆く
   (ncc 形式 `path:sl:sc:el:ec: sev: Nnnn: msg`、Windows のドライブレターのコロンで
   naive split 破綻)、代替は **ncc に構造化診断出力を足す**方向。**live squiggle は
   build-time 診断とは別物で LSP が別途必要**な点にも注意。
3. ~~`dotnet clean` が `bin\` 配下を消せるよう `@(FileWrites)` 登録~~
   → **完了 (0de978022)**。ランタイム dll コピーを `AfterTargets=CopyFilesToOutputDirectory`
   (IncrementalClean が @(FileWrites) を確定する前)へ移し `DestinationFiles` を @(FileWrites)
   に登録。**`-debug`/PDB 配線も同コミットで完了**(ncc の Portable PDB が SDK 期待パスに一致
   するため copy/clean は共通ターゲットが自動処理)。HelloCore/RefDemo で `dotnet clean` 後の
   bin 空・.pdb 生成コピーを実測。
4. 実配布を見据えるなら、`Nemerle.Tool` の nupkg メタデータ(README・ライセンス・アイコン)
   整備と CI での `pack-tool.ps1` → `dotnet pack` 自動化。加えて **Linux 用 ncc レイアウト
   生成スクリプト**(`pack-tool.ps1` の Linux 版: `ncc.exe` 除外・`ncc.default.rsp` 不要・
   `msbuild/ncc/` 配置)も未整備。
