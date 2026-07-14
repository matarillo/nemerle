# 30. WP-M1: IDE/build parity と server ログの地固め 実装ログ

実施日: 2026-07-14

ブランチ: `wip/dotnet-port`

開始 commit: `68837aa56`(`Plan the next .NET 10 Nemerle development environment phase (WP-M)`)

対象: `dotnet-port/29-devenv2-plan.md` の **WP-M1** のみ。hover/completion/definition
(WP-M2〜M4)、incremental rebuild(WP-M5)、`Nemerle.Sdk` NuGet 化(WP-M6)は範囲外。

## 結論

`29-devenv2-plan.md` の WP-M1 を完了した。language features(WP-M2 以降)の前に、
IDE と `dotnet build` の間に残っていた 2 つの parity gap(MSBuild `DefineConstants`
未配線、警告の N コード欠落)を解消し、server の情報 trace を LSP の
`window/logMessage` へ移して正常 session の Output Channel から誤解を招く `[error]`
表示を無くした。受け入れ基準 1〜6 を自動・実測で確認した。

- **DefineConstants → `-define:` 配線**: `Nemerle.Core.targets`(Windows/Linux)と
  in-process `NccCompile` task、`-p:NemerleUseExec=true` の Exec fallback の 3 経路すべてが
  project の `$(DefineConstants)` を ncc に渡す。`#if` 分岐が build と IDE で一致する。
- **engine 適用**: `EngineWorkspaceInputs.FromSnapshot` が snapshot の全 `DefineConstants`
  (MSBuild property ∪ `NemerleAdditionalOptions -define`)を engine に適用し、WP-L3 で
  導入した「DefineConstants 非適用 warning」を撤去した。
- **警告 N コードの構造化**: `ncc\parsing\Utility.n` の 1 行修正(N コードの prefix 付与を
  `RunWarningOccured` の**前**へ移動)で、警告コードが `WarningOccured` イベントから観測
  可能になった。build 経路(`CompilerHost` → `NccCompile`)は `warning N####:` として
  MSBuild のコード欄に出す。IDE 経路(LSP)は診断の `code` field に `N####` を入れ、
  message text からは prefix を取り除く。
- **server ログの `window/logMessage` 化**: MSBuild query 時間・engine rebuild 時間などの
  情報 trace を `MessageType.Log`、回復可能な失敗(project query/apply 失敗)を
  `MessageType.Error` として送る。stderr は initialize 前 fatal 専用に限定した。
- **compiler 1 行修正の回帰ゲート**: Stage1→Stage2→Stage3 を 0 error で再ビルドし、dist/ncc
  と bundled server を再 pack、testsuite 全数で新規 regression が無いことを確認した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301` / 共有フレームワーク `Microsoft.NETCore.App 10.0.9`
- Stage1(net-4.0)ビルドは `C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe`
- Node.js `v22.x` / npm `11.x` / VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- Nemerle assembly version `1.2.0.601`(`git describe` = `v1.2-601-g68837aa56`)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。

## 設計と実装

### 1. DefineConstants の parity(§6.1 の「build parity は機能の前」/ 受け入れ 1・2)

WP-L2/L3 は「現行 `Nemerle.Core.targets` は `DefineConstants` を ncc に渡さない」ことを
既知 gap として warning で可視化していた。WP-M1 で実際に配線した。

- ncc の `-define:` option(`ncc\CompilationOptions.n:403`)は値を `;` で split して
  複数シンボルを定義するので、`$(DefineConstants)` 文字列を **1 つの** `-define:` switch に
  そのまま渡せる。
- `Nemerle.MSBuild.Tasks.NccCompile` に `DefineConstants` パラメーターを追加し、
  `BuildArgs` が非空時に `-define:<DefineConstants>` を付ける。
- `Nemerle.Core.targets`(Windows)/ `msbuild/linux/Nemerle.Core.targets`(Linux)は
  in-process task 呼び出しに `DefineConstants="$(DefineConstants)"` を渡し、Exec fallback には
  条件付きプロパティ `$(NemerleDefineSwitch)` = `-define:"$(DefineConstants)"`(空なら未出力、
  `;` 区切りを shell から守るため quote)を挿入した。
- engine 側: `EngineWorkspaceInputs.FromSnapshot`(ProjectInfo、純関数)の `Defines` を
  `snapshot.Options.AdditionalDefines` から `snapshot.DefineConstants` へ変更。snapshot の
  `DefineConstants` は parser が MSBuild property と `NemerleAdditionalOptions -define` を
  union 済み(`MsBuildJsonParser.cs:39-43`)なので、build が ncc に渡す集合と一致する。
  非適用 gap warning は削除した。

**既知の制約**: `CoreCompile` の増分判定(`Inputs`/`Outputs`)は `DefineConstants` を含まない
(既存の `AdditionalOptions` と同様)。したがって define だけを変えた再ビルドは `-t:Rebuild`
または clean が必要。検証時も clean rebuild で両方向を確認した(下記)。これは WP-M1 で新設した
問題ではなく、targets の増分キー設計の既存特性である。

### 2. 警告 N コードの構造化(§6.6 の diagnostic code / 受け入れ 3)

`ncc\parsing\Utility.n` の `Message.Warning(code, loc, m)` は、コンソール表示用に
`def m = $"N$code: $m"` を作るが、それより**前**に `RunWarningOccured(loc, m)`(生テキスト)を
発火していた。1 行(実質は prefix 付与を 3 行上へ移動)で、prefix 済みテキストで event を
発火するようにした。`report`/`MessageOccured` 経路が受け取るテキストは従来と同一なので、
それ以外の挙動は変わらない。

- **build 経路**: `CompilerHost` は `WarningOccured` を購読しており、修正後は
  `N####: message` を受け取る。`NccCompile.ReportDiagnostic` が先頭 `N####:` を正規表現で
  剥がし、`Log.LogWarning(subcategory, warningCode: "N####", ...)` のコード欄へ入れる。
  実測: `dotnet build` で `nemerle warning N10001: there is no check needed to cast ...`
  のようにコード欄付きで出る。
- **IDE 経路**: engine(`Engine.Init.n:41`)は `MessageOccured` のみを購読し、その経路
  (`report`)は元から `N####:` prefix を含む。したがって IDE の警告コードは **compiler 修正に
  依存せず** 既に message に載っている。ProjectInfo に純関数 `NemerleWarningCode.Extract`
  (先頭 `N####:` を抽出して剥がす)を新設し、`NemerleProject` が診断構築時に code を分離、
  `EngineDiagnostic.Code` に格納。`NemerleTextDocumentSyncHandler` が LSP `Diagnostic.Code`
  (`DiagnosticCode`)へ配線し、payload hash にも含めた。
- `CompilerHost.cs` の「N コードは観測不能」という古いコメントを更新した。

これで build 側は compiler 修正が必須、IDE 側は修正非依存という非対称を明示的に扱う。

### 3. server ログの `window/logMessage` 移行(§6.6 / 受け入れ 5)

vscode-languageclient 10.1.0 の executable transport は server の **stderr** を Output Channel に
`[error]` として転送する(Project Owner 指摘)。正常動作の trace(MSBuild query 時間、rebuild
時間、workspace apply)まで `Console.Error` に出ていたため、正常 session でも `[error]` が並ぶ。

- 新規 `LspServer/ServerLog.cs`: server facade が用意できるまで buffer し、`Attach` で順に
  flush する log sink。`Info`/`Log`/`Warning`/`Error` を `window/logMessage`
  (`MessageType.Info=3`/`Log=4`/`Warning=2`/`Error=1`)として送る。engine の Output
  TextWriter も、行単位で `Log` へ転送する `TextWriter` に置き換えた。stderr は
  `ServerLog.Fatal`(initialize 前 fatal のみ)に限定。
- OmniSharp API(実 assembly 検査で確定、2026-07-14): `ILanguageServerFacade.Window` は
  `IWindowLanguageServer`、`OmniSharp.Extensions.LanguageServer.Protocol.Window.LogMessageExtensions`
  の `LogMessage(IWindowLanguageServer, LogMessageParams)` を使用。
- `Console.Error.WriteLine` を全廃: `NemerleProject`(engine message/rebuild trace/disk 再読込
  失敗/response callback 失敗)、`WorkspaceManager`(apply 失敗)、`NemerleProjectInfoHandler`
  (query 時間 trace/query 失敗/config 失敗)。measurement/trace → `Log`、project query 失敗 →
  `Error`。`Program.cs` は `ServerLog` を生成して各所へ注入・DI 登録し、server 生成後に
  `Attach`。ServerInfo version は `0.2.0` → `0.3.0`。
- raw LSP integration test の計測 assert を stderr grep から `window/logMessage` 受信へ移行し、
  さらに「server が自前 trace を stderr に出していない」ことと「正常 open/change/close で
  Error レベル logMessage が 0」「project query 失敗は Error レベル logMessage で出る」ことを
  assert に追加した(受け入れ 5 の安全な protocol 代替検証)。

### 4. 新規 fixture

- `samples/Defines`(`Defines.nproj` + `defines.n`): `#if CUSTOM_FEATURE` / `#else`(型エラー)。
  `CUSTOM_FEATURE` は既定で `$(DefineConstants)` に追加、`-p:EnableCustom=false` で外せる
  (build 両方向テスト用)。
- `samples/Warnings`(`Warnings.nproj` + `warnings.n`): 冗長な `:>` upcast で警告 `N10001`
  を出す(method body の型付け警告なので build と IDE の両方で出る)。

## 実装ファイル

- 変更(compiler、Stage リビルド対象): `ncc/parsing/Utility.n`(警告コード prefix を
  `RunWarningOccured` の前へ)。**`ncc` 本体の変更はこの 1 箇所のみ**。
- 変更(build/tasks): `dotnet-port/msbuild/Nemerle.Core.targets`、
  `dotnet-port/msbuild/linux/Nemerle.Core.targets`(DefineConstants 配線)、
  `dotnet-port/Nemerle.MSBuild.Tasks/NccCompile.cs`(`DefineConstants` param、`-define:`、
  N コード抽出)、`dotnet-port/Nemerle.Compiler.Hosting/CompilerHost.cs`(コメント更新のみ)。
- 変更(ProjectInfo): `dotnet-port/ProjectInfo/EngineWorkspaceInputs.cs`(DefineConstants 適用、
  gap warning 撤去)、`dotnet-port/ProjectInfo.Test/Program.cs`(engine input テスト更新 +
  `NemerleWarningCode` テスト)。
- 変更(LSP server): `dotnet-port/LspServer/NemerleProject.cs`(`EngineDiagnostic.Code`、
  N コード分離、ServerLog 化)、`NemerleTextDocumentSyncHandler.cs`(`Diagnostic.Code`、hash)、
  `WorkspaceManager.cs`・`NemerleProjectInfoHandler.cs`・`Program.cs`(ServerLog 注入、0.3.0)。
- 変更(integration test): `dotnet-port/LspServer.IntegrationTest/Program.cs`(logMessage 計測 +
  Defines/Warnings シナリオ)、`LspTestClient.cs`(logMessage アクセサ)。
- 新規: `dotnet-port/ProjectInfo/NemerleWarningCode.cs`、`dotnet-port/LspServer/ServerLog.cs`、
  `dotnet-port/samples/Defines/{Defines.nproj,defines.n}`、
  `dotnet-port/samples/Warnings/{Warnings.nproj,warnings.n}`、本ログ。
- `VsIntegration/`(engine 共有ソース)は無変更(`WarningOccured` を購読していないことを
  grep で確認済み。IDE の警告コードは `MessageOccured` 経路で従来から prefix 済み)。

## 検証(受け入れ基準)

### 基準 1: DefineConstants IDE/build parity(両経路・両方向)

`samples/Defines` の clean build:

| 経路 | CUSTOM_FEATURE あり | CUSTOM_FEATURE なし(`-p:EnableCustom=false`) |
|---|---|---|
| in-process `NccCompile` | 0 error(#if 分岐) | `error : expected int, got string`(#else 分岐) |
| `-p:NemerleUseExec=true`(Exec) | 0 error | 1 error(型エラー) |

IDE: raw LSP シナリオ「DefineConstants IDE/build parity」で、loose file(define 無し)は
`#else` の型エラー、`Defines.nproj` 適用後(CUSTOM_FEATURE 適用)は同一 buffer が error-free。
snapshot の `defineConstants` に `CUSTOM_FEATURE` が入ることも確認。

### 基準 2: EngineWorkspaceInputs の unit test

`ProjectInfo.Test`: `EngineWorkspaceInputs.FromSnapshot` が `DefineConstants` 全体
(`NET`/`TRACE` の property 由来 + `FEATURE`/`SECOND` の `-define` 由来)を適用し、gap warning が
生成されないことを固定。`NemerleWarningCode.Extract` の抽出/非抽出/複数行/null も固定。
→ `PASS project info unit tests`。

### 基準 3: 警告 N コードの構造化(build + LSP)

- build: `dotnet build samples/Warnings`(clean)で
  `nemerle warning N10001: there is no check needed to cast string to object`(コード欄付き)、
  加えて `nemerle warning N10003: ...`(未参照 private メンバー)も同様に構造化出力。
- LSP: raw シナリオ「Warning N-code surfaces in the LSP diagnostic code field」で、警告診断の
  `code` が `"N10001"`、`message` に `N10001` prefix が漏れていないことを assert。

### 基準 4: stage2/stage3 0 error + testsuite 回帰

- Stage1(FW4、full rebuild で version 1.2.0.601 に揃える)→ refresh → Stage2 → Stage3 を
  すべて 0 error でビルド。Stage3(Stage2 の self-host)も 4 アセンブリ `-> OK`。
- testsuite(`run-testsuite-core.ps1`、Stage2 ncc に対して全数): **positive 435/469、
  negative 166/167 = 601/636**。既存 baseline(WP-J 時点の 435 / 166)と完全一致し、
  **新規 regression 0**。残 35 失敗はすべて既知の環境・ハーネス制約(C# パーサー未移植、
  Nemerle.Linq/.Unsafe/.WPF 未ビルド、CAS no-op、BCL 書式差など。`18-testsuite-log.md` §6)で、
  genuine なコンパイラーバグは無い。

**版ハザードの実務メモ**: `git describe` 由来の assembly version は、`lib/*.n` を変更していない
Stage1 の増分ビルドでは更新されない(MSBuild は git revision の変化を検知しない)。今回、
HEAD が前回 Stage1 ビルド時の 592 から 601 へ進んでいたため、増分 Stage1 の `Nemerle.dll` が
592 のまま残り、601 の Stage2 `Nemerle.dll` を load できず `FileLoadException` になった。
Stage1 を full rebuild(`/t:Stage1` + 出力ディレクトリ削除)して 601 に揃えて解消した。
compiler ソース変更時は Stage1 の full rebuild が必要(`build-stage2-core.ps1` 冒頭コメントの
既知事項)。

### 基準 5: 正常 session の Output Channel に `[error]` 0

raw LSP integration の各シナリオで、server が自前 trace/診断を **stderr に一切出さない**ことを
assert(`nemerle ...`/`Project query ...` prefix が stderr に出たら失敗)。measurement trace は
`window/logMessage` type 4(Log)で受信、project query 失敗は type 1(Error)で受信、正常
open/change/close 区間では type 1 の logMessage が 0 であることを assert。vscode-languageclient は
type に応じたレベルで表示するため、正常 session の Output Channel に stderr 由来の `[error]` は
出ない。

### 基準 6: WP-L4 の全自動テスト回帰

- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query 4 fixtures)。
- raw LSP integration(repo bin server): 7/7 シナリオ PASS(既存 5 + Defines + Warnings)。
- extension: `npm ci`(0 vuln)/ `check-types` / `lint` / `npm test`(21/21)/
  `test:integration`(trusted 3 + untrusted 1)/ `package`(verify-server: 66 assemblies、
  notices coverage OK → `vscode-nemerle-0.4.0.vsix` 468 files / 4.42 MB)/
  `test:vsix`(隔離 install 1 passing)/ `test-bundled-server.ps1`(VSIX 抽出 server で
  7/7 シナリオ PASS)。
- vulnerability: `npm audit` / `npm audit --omit=dev` = 0。
  `dotnet list package --vulnerable --include-transitive` = LspServer / ProjectInfo /
  ProjectInfo.Test / LspServer.IntegrationTest / Nemerle.MSBuild.Tasks /
  Nemerle.Compiler.Hosting の全てで脆弱 package なし。

### 計測(raw LSP integration の logMessage trace より)

| 対象 | MSBuild query | engine rebuild |
|---|---:|---:|
| HelloCore | 692 ms | 110–210 ms |
| RefDemo App | 916 ms | 156–205 ms |
| PackageReference | 700 ms | 204 ms |
| Sokoban | 1016 ms | 72 ms(collapse 下限) |
| Defines | 681 ms | 158–219 ms |
| Warnings | 670 ms | 218 ms |

## 公式仕様の確認(2026-07-14)

- LSP 3.17 `window/logMessage`(`MessageType` Error=1/Warning=2/Info=3/Log=4、単発ログは
  `$/logTrace` ではなく `window/logMessage` を使う):
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
- vscode-languageserver-node client の logMessage / stderr 表示挙動:
  <https://github.com/microsoft/vscode-languageserver-node/blob/main/client/src/common/client.ts>
- OmniSharp 0.19.9 実 assembly 検査(`IWindowLanguageServer` / `LogMessageExtensions.LogMessage`
  / `LogMessageParams` / `MessageType` / `DiagnosticCode(string)`)。API 欠落なし、0.19.9 継続。
- ncc の `-define:`(値を `;` split して複数定義): `ncc\CompilationOptions.n:403-409`。

## 再現コマンド

```powershell
# 1. compiler 変更を反映(Stage1 full rebuild が必要)
Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj `
  /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
pwsh dotnet-port\refresh-stage1-core.ps1
Remove-Item -Recurse -Force bin\Release\core\Stage2
pwsh dotnet-port\build-stage2-core.ps1
pwsh dotnet-port\pack-tool.ps1
pwsh dotnet-port\vscode-nemerle\pack-server.ps1

# 2. build parity / warning code(clean rebuild で define を効かせる)
dotnet build dotnet-port\samples\Defines\Defines.nproj -t:Rebuild                         # 成功
dotnet build dotnet-port\samples\Defines\Defines.nproj -t:Rebuild -p:EnableCustom=false   # 型エラー
dotnet build dotnet-port\samples\Warnings\Warnings.nproj -t:Rebuild                        # warning N10001

# 3. テスト
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll
pwsh dotnet-port\run-testsuite-core.ps1

Push-Location dotnet-port\vscode-nemerle
npm ci; npm run check-types; npm run lint; npm test
npm run test:integration; npm run package; npm run test:vsix
pwsh .\test-bundled-server.ps1
npm audit; npm audit --omit=dev
Pop-Location
```

前提: `dotnet-port\dist\ncc`(pack-tool.ps1)。raw LSP integration test は fixture の
`dotnet build` を自前で実行する。

## 既知の制約 / 次 WP への境界

1. `CoreCompile` の増分キーに `DefineConstants` / `NemerleAdditionalOptions` が含まれないため、
   これらだけを変えた再ビルドは `-t:Rebuild`/clean が必要(既存特性、WP-M1 新設ではない)。
2. compiler 変更に伴う版ハザード: `git describe` 由来 version は commit 追加で変わる。
   本コミット後に dist/server を再 pack する場合は同一 commit 上で Stage1 full rebuild →
   Stage2 → pack の一連を行うこと(§6.8 の provenance 機械化は WP-M6)。
3. hover/completion/definition(WP-M2〜M4)、incremental rebuild(WP-M5)、
   `Nemerle.Sdk` NuGet 化(WP-M6)は未着手。`EngineRequestBridge`(§6.2)は WP-M2 で新設する。
4. Linux 版 targets の DefineConstants 配線はコードとしては入れたが、実地検証は WP-M6/WP-N の
   WSL 経路のまま(本 WP では Windows で検証)。

前身の実装ログ: `25-vscode-extension-log.md`(WP-L1)/ `26-vscode-project-info-log.md`(WP-L2)/
`27-vscode-project-workspace-log.md`(WP-L3)/ `28-vscode-packaging-log.md`(WP-L4)。
計画: `29-devenv2-plan.md`。
