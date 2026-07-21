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
> 関する記述はこの2コミットで解消済み。`pack-tool.ps1` の `ncc.default.rsp`/`gen-default-rsp.ps1`
> 生成と `ncc.cmd` の rsp 前置/自動再生成ロジックは **WP-N1(D7)で整理済み** — auto-ref 化により
> レガシーになっていたこれらを削除し、`ncc.cmd` は `dotnet ncc.dll %*` を直接呼ぶだけの単純な
> ラッパーになった(下記「1. `dotnet ncc` 配布レイアウト」節を参照)。

> **更新 (2026-07-16, WP-N1 — package 版サフィックスの規約化)**: NuGet package の版は
> `1.2.<rev>-preview.<N>`。base の `1.2.<rev>` は同梱される `Nemerle.dll` の実 AssemblyVersion
> (`git describe` 由来の revision)から `pack-tool.ps1` が自動導出する。サフィックス
> `preview.<N>`(`-PackageVersionSuffix`)の規約は次のとおり:
>
> 1. **base version が進んだら `preview.1` にリセット**(スクリプトの既定値。Stage リビルド後の
>    通常の pack は引数不要)。SemVer 2.0 は base 側を優先して順序付けるため
>    `1.2.618-preview.1 > 1.2.601-preview.2` となり、リセットしても順序・キャッシュは壊れない。
> 2. **同一 base version のまま中身を変えて配り直すときだけ `preview.<N+1>` を明示的に渡す**
>    (例: 1.2.601-preview.1 → Linux hover 修正の再 pack で 1.2.601-preview.2)。NuGet は
>    `(id, version)` を内容ごとにキャッシュするため、配布済み版番号の再利用は消費側で
>    古いビットが静かに使われ続ける事故になる。
> 3. **一度マシンの外へ出た版番号(GitHub release assets・共有 feed)は再発行しない**。
>    同一 base 内でサフィックスを戻すことも不可(順序が逆行する)。スクリプトは配布済み状態を
>    知り得ないため、同一 base の再配布バンプは操作者の責任 — 公開済み release assets を
>    確認してから pack すること。
>
> なお `preview` ラベル自体は成熟度の表現(package ID には意図的に載せない — ID は恒久、
> 成熟度は変わる)。WP-O の nuget.org 公開時にサフィックスを外した `1.2.<rev>` 安定版が終着点。
> VSIX(`vscode-nemerle-<semver>`)は別体系(拡張の独自 semver)で、この規約の対象外。
>
> **配布物内の文書に版番号のリテラルを書かない**(同 WP-N1 フォローアップ): package README
> (`packaging\*\README.md`)・インストールガイド(`packaging\README.md` → `dist\release\README.md`)・
> `dotnet new` テンプレートは、版を `__NEMERLE_SDK_VERSION__`(インストールガイドの VSIX 例のみ
> `__NEMERLE_VSIX_VERSION__`)と書き、`pack-tool.ps1 -Pack` が pack 時に実版へ置換した staging 済み
> コピーを同梱する(プレースホルダーが見つからないと pack が停止するドリフト検知つき。csproj 側も
> staged パス未指定の直 pack を拒否する)。コメント等の説明用途では固定版ではなく `<version>` 表記を
> 使う(`Sdk.props` / 各 README / csproj ヘッダーで適用済み)。固定版の手書きは
> 「1.2.601-preview.2 が 1.2.618 以降の配布物に残る」形で実際に腐った経緯による。

> **更新 (WP-A3 — インプロセス MSBuild タスク)**: 「タスク3」の `<Exec dotnet ncc.dll ...>` は
> 既定で**インプロセスタスク `NccCompile`**(`dotnet-port\Nemerle.MSBuild.Tasks`)に置き換わった。
> `dotnet-port\Nemerle.Compiler.Hosting` が `Nemerle.Compiler.dll` の `ManagerClass` API を
> C# から強い型付けで呼び出すブリッジで、コンパイル毎に collectible `AssemblyLoadContext` へ
> ロードされる(静的状態隔離・参照 dll のファイルロック解放が目的)。診断はテキストではなく
> `Log.LogError`/`LogWarning`(file/line/col 付き構造化)で報告される。
> `-p:NemerleUseExec=true` で従来の `<Exec>` 経路にフォールバック可能。詳細・検証結果は
> `dotnet-port\20-inproc-task-plan.md` / `dotnet-port\20-inproc-task-log.md` を参照。
> `pack-tool.ps1` は既定でこの2プロジェクトもビルドし、レイアウトへ配置するようになった。

> **更新 (2026-07-13, WP-L2 — VS Code project information)**: `dotnet msbuild` の
> `-getItem` / `-getProperty` JSON query を使い、`.nproj` の source、ProjectReference、
> PackageReference、macro-only reference、define/options を取得する独立 snapshot provider を追加した。
> `samples\PackageReference` の Newtonsoft.Json 13.0.4 resolved assembly まで実測済み。
> VS Code extension 0.2.0 はこの snapshot の選択・reload・状態表示を行うが、WP-L3 前なので
> diagnostics engine へはまだ適用しない。また development VSIX は server binaries を同梱せず、
> 配布一体化は WP-L4 のまま。詳細は `24-vscode-development-plan.md` /
> `26-vscode-project-info-log.md`。

> **更新 (2026-07-14, WP-L3 — project-aware engine workspace)**: 上記 snapshot は
> LSP server の analysis engine に適用されるようになった。project 全 source
> (未保存 buffer が disk より優先)、resolved reference、macro-only reference を使う
> project-aware diagnostics が VS Code extension 0.3.0 で動く。development VSIX が
> server を同梱しない点は変わらず WP-L4。詳細は `27-vscode-project-workspace-log.md`。

> **更新 (2026-07-15, WP-M6 — Nemerle.Sdk NuGet 化)**: 本文書の中心的な前提
> 「toolchain は repo checkout(`dist/ncc` + `Nemerle.Core.targets` の `<Import>`)が要る」は
> **もう当てはまらない**。`pwsh dotnet-port\pack-tool.ps1 -Pack` が **MSBuild project SDK
> パッケージ `Nemerle.Sdk.Unofficial`** と **`dotnet new` テンプレート
> `Nemerle.Templates.Unofficial`** を生成し、repo checkout 無しで
> `dotnet new nemerle-console` → `dotnet build` → `dotnet run` が成立する
> (Windows / WSL 実測)。project は `<Project Sdk="Nemerle.Sdk.Unofficial/1.2.601-preview.2">`
> の1行だけで済み、`<Import>`・`@(NemerleCompile)`・定型 property は不要
> (`**/*.n` は SDK が glob する)。また **Windows/Linux 2 本あった targets は 1 本に統合**され、
> package はその同一ファイルを同梱する(下記「3. SDK スタイル MSBuild 統合」の
> `msbuild/linux/` に関する記述は無効。あの派生は importer ゼロのデッドコードだった)。
> 併せて `dist/ncc/ncc-info.json`(provenance)を追加し、server 側 `bundle-info.json` と
> 突き合わせて世代混在を検出する。公開は local feed 止まり(nuget.org 公開は WP-N)。
> 詳細は `35-devenv2-wp-m6-log.md`。

> **更新 (2026-07-14, WP-L4 — bundled VSIX packaging)**: VS Code extension 0.4.0 は
> `dotnet-port\vscode-nemerle\pack-server.ps1` が staging する LspServer Release 出力
> ディレクトリ全体を VSIX の `server/` に同梱し、既定で bundled server を起動する
> (`nemerle.server.path` は開発 override)。.NET 10 runtime は同梱せず、起動前に
> `dotnet --list-runtimes` で検査する。VSIX は server を配布するが、user project の
> build / MSBuild query には引き続きこの文書の `dist/ncc`(pack-tool.ps1)+
> `Nemerle.Core.targets` が必要(`Nemerle.Sdk` NuGet 化は次 WP)。同一 commit の
> server/dist を組み合わせること(Nemerle assembly version は git describe 由来。
> `server/bundle-info.json` に pack 時 commit を記録)。詳細は
> `28-vscode-packaging-log.md`。

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
  ncc.cmd                    -- 利便性ラッパー(後述)。`dotnet ncc.dll %*` を素で呼ぶだけ
```

> **更新 (WP-N1, D7 — rsp レガシー整理)**: 以前はここに `ncc.default.rsp`(標準参照セットを
> 焼き込んだ手組みのラッパー応答ファイル)と `gen-default-rsp.ps1`(レイアウトの再配置後に
> その絶対パスを再計算するスクリプト)も含まれ、`ncc.cmd` は前者を `-from-file:` で前置し、
> 移動を検知すると後者を自動再実行していた。WP-A2 の auto-ref 化(`ncc\passes.n` の
> `LoadCoreStdlibReferences`)により ncc 自身が標準参照セットを解決するようになったため、
> この3点はすべて**不要になり削除した**(`pack-tool.ps1` はもう生成しない)。再配置に関する
> 懸念(絶対パスがマシン/設置場所固有になる問題)もこれで解消している — `ncc.cmd` は
> `dotnet "%HERE%ncc.dll" %*` を呼ぶだけなので、レイアウトごとコピー/移動しても
> 追加の再生成なしにそのまま動く。

### 検証済みの事実

- **`dotnet <path>\ncc.exe` は `exec` なしでもそのまま動く**(`dotnet exec` と同じ)。
  さらに **同じファイルを `ncc.dll` にコピーしただけでも動く**
  (`ncc.runtimeconfig.json` のベース名が拡張子非依存の "ncc" で両方に効くため)。
  つまり現状の stage2/stage3 出力は追加コード変更なしで「`dotnet <dll>`」規約に
  すでに適合していた — 必要だったのは新しい実装ではなく、レイアウト整理と
  デフォルト参照のラップだった。
- **(歴史的経緯、現在は使っていない)** `-from-file:` は Getopt の `SubstitutionString`
  (`lib\getopt.n` / `ncc\CompilationOptions.n`)であり、**その場でファイル内容を再帰的に
  パースしてから残りのコマンドラインのパースを続ける**(`parse_opts` の再帰呼び出しの後に
  外側の `parse_opts(rest)` が必ず実行される、`lib\getopt.n:265-288` で確認)。よって
  `-from-file:ncc.default.rsp -out:hello.exe hello.n` は「rsp の内容 +
  `-out:hello.exe hello.n`」と等価に振る舞う ── これが撤去済みの「ラッパー rsp」方式
  (`ncc.default.rsp` / `gen-default-rsp.ps1`、上記 WP-N1/D7 の更新を参照)の土台だった。
  auto-ref 化後の現在は `-from-file:` を使わずとも標準参照セットが解決されるため、
  この仕組み自体は使われていない。

### 再現手順

```powershell
pwsh dotnet-port\pack-tool.ps1                # 既定: Stage2 -> dotnet-port\dist\ncc
# 任意のディレクトリで(auto-ref のため rsp 不要):
dotnet <repo>\dotnet-port\dist\ncc\ncc.dll -out:hello.exe hello.n
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
  (ncc が実体を自動解決するため)。**PackageReference も WP-L2 の
  `samples\PackageReference` (Newtonsoft.Json 13.0.4) で restore/build と
  `ReferencePath` resolved assembly の両方を実測済み**。
- ~~`dotnet clean` の bin\ 削除、`-debug`/PDB 配線、マルチプロジェクトビルド~~ →
  **完了 (0de978022)**。clean/PDB 配線済み、複数プロジェクト(MathLib→App)も RefDemo で実証。
- ~~**Linux/Unix 対応**: `dotnet-port\msbuild\linux\Nemerle.Core.targets` を追加 (253d2ecb3)。
  NccLayoutDir 既定 `../ncc/`・`dotnet exec ncc.dll` 起動・スラッシュパスのみが Windows 版との差分~~
  → **WP-M6 で撤去**。この派生を `<Import>` している .nproj / script / test は**1つも無く**、
  既定にしていた layout dir `msbuild/ncc/` は存在すらしなかった。WSL の実証は samples が import
  している **Windows 版**を経由しており(MSBuild は Unix で `\` を正規化する)、
  `../dist/ncc/` 既定は Linux でそのまま機能していた。3つの差分は条件分岐ではなく**削除**で解消し
  (layout dir は property、`dotnet exec` は全 OS 共通、パスは `/` に統一)、
  `msbuild\Nemerle.Core.targets` 1 本を Windows・Linux・NuGet package が共有する。
  **WSL で再実証済み**: Windows でビルドした core ncc の DLL 群がそのまま Linux で動作し、
  `dotnet build HelloCore.nproj` → 実行まで成功(PDB・ランタイム dll コピー含む)。生の CLI
  (`dotnet exec ncc.dll hello.n`)は生成 exe 隣の `Nemerle.dll` 不足で実行時エラー(OS 非依存の
  既知 gap)だが、MSBuild 統合は自動コピーで解消。Linux で repo checkout を使う場合の layout は
  Windows と同じ `dotnet-port/dist/ncc/`(`msbuild/ncc/` ではない)。
- `.nproj` 拡張子を使う制約そのものは実用上大きな障害ではない(Visual Studio 等の IDE 統合まで
  考えるなら別途「Nemerle 言語 SDK」相当が必要になるが、CLI ビルドの範囲では `.nproj` で十分機能する)。

---

## まとめ表

| 項目 | 状態 | 再現性 |
|---|---|---|
| `dotnet <layout>\ncc.dll` 配布レイアウト | **完全動作** | `pack-tool.ps1` で再現可、hello.n/hello2.n 実証済み |
| `dotnet tool` 化 | **PoC 成功**(C# シム経由) | install→実行の手順を記載、実証済み |
| SDK スタイル MSBuild 統合 | **動作**(`.nproj` 拡張子。参照/PDB/clean/マルチプロジェクト/Linux 対応済み) | Windows・WSL で `dotnet build`→実行を実証 |
| **`Nemerle.Sdk.Unofficial` NuGet package(WP-M6)** | **動作**(repo checkout 不要。project SDK 方式) | `pack-tool.ps1 -Pack` → local feed。repo 外空 dir と WSL で `dotnet new`→build→run を実証(`35-devenv2-wp-m6-log.md`) |
| **`Nemerle.Templates.Unofficial`(WP-M6)** | **動作**(`nemerle-console` / `nemerle-classlib`) | 同上 |
| **`Nemerle.Linq.Unofficial`(WP-N3)** | **動作**(`linq` 構文 / `ToExpression` / 暗黙の式ツリー変換。通常の `PackageReference` で消費) | `build-libs-core.ps1` → `pack-tool.ps1 -Pack`。世代不一致の Libs は pack 前チェックで拒否(`40-prerelease-wp-n3-log.md`) |
| **provenance(WP-M6)** | **動作**(`ncc-info.json` ↔ `bundle-info.json`) | 版不一致で server が `window/showMessage` 警告、同一版では無警告(LSP integration 2 シナリオ) |
| インプロセス MSBuild タスク(WP-A3) | **動作**(構造化診断、ALC 隔離、Exec フォールバック付き) | HelloCore/RefDemo/一時診断プロジェクトで実証(`20-inproc-task-log.md`) |
| 既存 CLR4 ビルド(`NemerleAll.nproj`/`build-stage2-core.ps1`) | **無改造・無回帰** | 新規ファイルのみ追加(`.gitignore` のみ既存ファイルに軽微な追記) |

## 変更・追加ファイル一覧

- 新規: `dotnet-port\pack-tool.ps1`
- 新規: `dotnet-port\Nemerle.Tool\Nemerle.Tool.csproj`, `Program.cs`(0.2.0-poc1 で rsp フリー化)
- 新規: `dotnet-port\msbuild\Nemerle.Core.targets`(参照配線/PDB/clean/インプロセスタスク追加)
- 新規: `dotnet-port\msbuild\linux\Nemerle.Core.targets`(Linux 版, 253d2ecb3。インプロセスタスク追加)
- 新規: `dotnet-port\Nemerle.Compiler.Hosting\`(WP-A3、`ManagerClass` API の C# ブリッジ)
- 新規: `dotnet-port\Nemerle.MSBuild.Tasks\`(WP-A3、`NccCompile` インプロセスタスク)
- 新規: `dotnet-port\20-inproc-task-plan.md` / `dotnet-port\20-inproc-task-log.md`(WP-A3 計画/ログ)
- 新規: `dotnet-port\samples\HelloCore\HelloCore.nproj`, `hello.n`
- 新規: `dotnet-port\samples\RefDemo\`(MathLib ライブラリ → App exe の ProjectReference 例)
- 新規: `dotnet-port\DISTRIBUTION.md`(本ドキュメント)
- 新規(WP-N6): `dotnet-port\verify-seed.ps1`(seed 検証を `build-from-boot.ps1` から抽出。
  schema / version.txt スパン / 全ファイル SHA256 / provenance コミット照合。`-WarnOnly` あり)、
  `dotnet-port\smoke-release.ps1`(消費者スモーク: feed から install → `nemerle-console`
  生成 → build → run)、`.github\workflows\dotnet-port-ci.yml`(push/PR CI)、
  `.github\workflows\dotnet-port-release-build.yml`(手動 release workflow)。
  削除: `.github\workflows\build.yml`(upstream 由来・`windows-2016` 退役で死亡)
- コンパイラー側: `ncc\passes.n`(CoreCLR デフォルト参照の自動解決 `LoadCoreStdlibReferences`, 56964d879)。
  **WP-A3 ではコンパイラー側は無変更**。
- 既存改変(最小限): `.gitignore`(`dotnet-port/dist/` と `*.nupkg` を追加、
  生成物を誤って追跡しないため)、`dotnet-port\00-PLAN.md`(作業ログ追記)
- 生成物(既定では git 追跡対象外): `dotnet-port\dist\ncc\`(pack-tool.ps1 の
  出力、`Nemerle.Compiler.Hosting.dll`/`msbuild-task\Nemerle.MSBuild.Tasks.dll` を含む)、
  `dotnet-port\dist\nupkg\`(この節の `Nemerle.Tool` PoC の `dotnet pack -o` 出力)。
  **WP-M6 以降のリリース成果物は `dotnet-port\dist\release\`**(`pack-tool.ps1 -Pack` の
  nupkg + `packaging\README.md` の写し、`npm run package` の VSIX、`pack-release.ps1` の
  `release-info.json`)。このフォルダーがそのままリリースの実体で、NuGet の local feed としても
  機能する

## 推奨される次ステップ

1. ~~`Nemerle.Core.targets` に `@(ReferencePath)` を `-ref:` として渡す配線~~
   → **完了 (ee2ae06f3)**。`samples\RefDemo` で ProjectReference を実証。
   **PackageReference も WP-L2 で完了**。`samples\PackageReference` の
   Newtonsoft.Json 13.0.4 について build と project-information snapshot を実測した。
2. ~~インプロセス化: MSBuild の `<Exec>` による ncc.dll 別プロセス起動を、
   `Nemerle.Compiler.dll` の API 直呼びへ置き換える~~ →
   **完了 (WP-A3、`dotnet-port\20-inproc-task-plan.md` / `20-inproc-task-log.md`)**。
   `dotnet-port\Nemerle.MSBuild.Tasks`(`NccCompile` タスク)+
   `dotnet-port\Nemerle.Compiler.Hosting`(`ManagerClass` API ブリッジ)で実現。
   コンパイル毎の collectible `AssemblyLoadContext` 隔離、`Log.LogError`/`LogWarning` による
   構造化診断(テキストパース不要)、`-p:NemerleUseExec=true` フォールバックまで実装・検証済み。
   `dotnet tool` シム(`Nemerle.Tool`)側のインプロセス化は同じ Hosting ブリッジで後日可能
   (スコープ外のまま)。**live squiggle(LSP)は引き続き別課題**。
3. ~~`dotnet clean` が `bin\` 配下を消せるよう `@(FileWrites)` 登録~~
   → **完了 (0de978022)**。ランタイム dll コピーを `AfterTargets=CopyFilesToOutputDirectory`
   (IncrementalClean が @(FileWrites) を確定する前)へ移し `DestinationFiles` を @(FileWrites)
   に登録。**`-debug`/PDB 配線も同コミットで完了**(ncc の Portable PDB が SDK 期待パスに一致
   するため copy/clean は共通ターゲットが自動処理)。HelloCore/RefDemo で `dotnet clean` 後の
   bin 空・.pdb 生成コピーを実測。
4. 実配布を見据えるなら、`Nemerle.Tool` の nupkg メタデータ(README・ライセンス・アイコン)
   整備。**CI 化は WP-N6 で完了**(下記「CI」節。push/PR で `pack-tool.ps1 -Pack` まで含む
   フルチェーンが Linux で自動実行され、release workflow が発行まで担う)。**Linux 用 ncc
   レイアウト生成スクリプト**(`pack-tool.ps1` の Linux 版: `ncc.exe` 除外・`ncc.default.rsp`
   不要・`msbuild/ncc/` 配置)は未整備のまま。

## 版タグ契約(WP-N4、2026-07-19)

アセンブリ版は `GeneratedAssemblyVersion` マクロ(`macros\GeneratedAssemblyVersion.n`)が
コンパイル時に決める。**WP-N7(case 1)以降、リリース経路のスクリプトはリポジトリルートの
`version.txt` の固定値を `$env:GitTag`/`$env:GitRevision` として渡す**
(`dotnet-port\version-pin.ps1`)。ExpandEnv は環境変数を describe より優先するため、
スクリプト経由のビルドは**コミットに関係なく version.txt の版**で刻まれる。
スクリプトを経由しない素の `msbuild` / `dotnet build` は従来どおり
`git describe --tags --long --match "v[0-9]*"` に戻る(受容済みの境界)。
`--match` により **v 開始タグだけ**が版計算に入る。したがって:

- **リリースタグは `release/1.2.<rev>-preview.<N>`**(実ソースコミットに打つ)。
  **v 非開始が必須**(`v*` は upstream rsdn/nemerle の名前空間 = 版計算の予約領域)。
- **seed タグ `seed/1.2.<rev>` は WP-N7 以降は作らない**: seed は `dotnet-port\seed\` に
  チェックインされ、リリースタグのコミット自身がその seed を含むため、seed を別途
  番地付けする必要がない。既存の `seed/1.2.630` / `seed/1.2.635` は orphan 時代の
  履歴として残す(タグ契約の v 非開始規則は引き続き適用)。
- **lightweight タグで作る**(`git tag <name> <sha>`、`-a`/`-m` を付けない)。
  pack 系スクリプト(`pack-tool.ps1` / `pack-server.ps1` / `pack-release.ps1`)の provenance
  describe は annotated 限定 + `--match` で防御済みだが、lightweight 運用が第一の契約。
- 同一レシピの複製(`dotnet-port\assembly-version-check.ps1` /
  `tools\msbuild-task\GetGitTagRevision.cs`)は macro と**乖離させない**こと。
  ただし `assembly-version-check.ps1` は WP-N7 で describe 再現をやめ version.txt を読む
  ようになった(検査の意味も「HEAD の commit か」→「同じ version.txt スパンか」へ変更)。
- git 2.7 以上が必要(`--tags` + `--match` の lightweight タグへの適用)。

**契約修正前の世代(1.2.630 以前)にはタグを打てない**: macro はコンパイル時にレシピを
読むため、旧世代のコンパイラー/seed は release タグを版計算から除外できない。

**boot-4.0 は世代 636(コミット `78c7024b0` 相当のセルフホスト成果物)へ更新済み**
(2026-07-20、`45-boot-4.0-refresh-log.md`)。`--match` 契約を知る世代の macro が
boot-4.0 自身に焼き込まれたため、release タグが祖先に付いたコミットで boot-4.0 → Stage1
のフルリビルドを行っても正しく `v1.2-<rev>-g<sha>` を計算する(旧世代(538)で必要だった
「release タグの一時退避」運用は不要になった)。boot-4.0 は今後も同様の手順(Stage1→Stage2→
Stage3 の 2 世代セルフホストで fixpoint を確認してから採用)で随時更新してよい
(45-log 参照。定期更新の義務はない — 問題が顕在化した時のスポット更新で足りる)。

なお、この更新は**発行済みリリースの再現性に影響しない**: `build-from-boot.ps1` /
`publish-seed.ps1` が消費するのはチェックイン済み seed(`dotnet-port\seed\`、WP-N7 以前は
orphan ブランチ `boot-net10`)のみで、boot-4.0 には一切依存しない(43-boot-net10-log.md §2)。
boot-4.0 の更新だけを理由に再リリースする必要はない。

根治(describe 依存そのものを断つ版ピン留め)は WP-N7 で**部分 GO・実装済み**
(`44-prerelease-wp-n7-log.md` §7–§8)。version.txt によるピンで seed は自分の世代に
縛られなくなり、orphan ブランチと pinned worktree は廃止された。

**発行済みリリースの再現**: `release-info.json` の `seed` フィールド(seed ディレクトリを
最後に変更したコミット・pinnedVersion・seed の世代)がリリース ↔ seed の対応を機械可読に
保持する。再現はリリースタグを**チェックアウトしてから**:

```powershell
git checkout release/1.2.<rev>-preview.<N>
pwsh dotnet-port/build-from-boot.ps1 -ReleaseTag release/1.2.<rev>-preview.<N>
```

`-ReleaseTag` はタグ名から版 suffix を導出し、**このチェックアウトが本当にそのタグの
コミットか**を検証する(不一致なら停止)。seed はそのコミットに含まれているので、
worktree もブランチ解決も不要になった。再現の一致水準(41 log §7.8.1 の実測):

- **版・構成・provenance の同一性フィールドは完全一致**。
- **Nemerle 製アセンブリ(ncc.exe / Nemerle*.dll / Nemerle.Linq.dll)と静的コンテンツは
  バイト一致**(同一マシン・同一絶対パスの clone が条件 — WP-N5 §5.1 のチェックアウト
  パス埋め込みのため。他環境では版・内容一致のみ)。
- nupkg / VSIX の**ファイル全体のハッシュは一致しない**: NuGet psmdcp(GUID+時刻)、
  provenance の `packedAtUtc`、C# 製補助アセンブリの PE メタデータ(タイムスタンプ/MVID)が
  ビルドごとに変わるため。照合は「版 + zip 内エントリー単位の内容ハッシュ」で行うこと。

詳細な設計と実測は `dotnet-port\41-prerelease-wp-n4-log.md`。

## CI(WP-N6、Linux)

GitHub Actions ワークフロー 2 本。設計と実測は `dotnet-port\46-prerelease-wp-n6-log.md`。

**`.github\workflows\dotnet-port-ci.yml`(push/PR CI)**: `wip/dotnet-port` への push と
同ブランチ宛 PR で自動実行(`**/*.md` のみの変更は除外。`workflow_dispatch` で手動起動も可)。
`ubuntu-24.04` 単一ジョブで、チェックイン seed から **その HEAD** をビルドして検証する:
`verify-seed.ps1` → `build-stage2-core.ps1`(seed の ncc で stage2)→ `build-libs-core.ps1`
→ `pack-tool.ps1 -Pack` → `pack-server.ps1` → samples restore → npm unit → raw LSP
integration → `ProjectInfo.Test --integration` → `smoke-release.ps1`(消費者スモーク)。
初回実測 3 分 54 秒(受け入れ目安 15 分内)。

**`.github\workflows\dotnet-port-release-build.yml`(手動 release workflow)**:
`workflow_dispatch` のみ。入力 `release_tag`(push 済みタグ)+ `stage`。
`stage=build-smoke` はビルド + `smoke-release.ps1` で停止する dry run、
`stage=build-smoke-release` は続けて GitHub Release へ発行(スモーク通過が必須ゲート、
常に `--prerelease`)。手順は `packaging\README.md` の「Tagging and publishing a release」。

**CI が担保しない範囲(手動ゲートとして残す)**:

- **testsuite 全数**(636): ハーネス `Nemerle.Compiler.Test.exe` が CLR4 実行ファイルで
  Linux 不可。seed refresh 時に Windows で回す(バックログ: core 移植)。
- **boot-4.0 → Stage1 再生成と CLR4 スモーク**: Windows/CLR4 専用。boot-4.0 更新や
  seed refresh 時の手動回帰ゲート(`45-boot-4.0-refresh-log.md`)。
- **バイト決定性比較**: 意味のある検査はフルビルド 2 回を要し実行時間が倍。決定性は
  seed refresh 儀式とリリース再現(上節)が担保する。

これらは CI の穴ではなく**意図的な線引き**(36 §6)。CI は push/PR の HEAD が Linux で
ビルド・自己ホスト・消費できることを保証し、CLR4 世代管理は Windows 側の儀式に残す。
