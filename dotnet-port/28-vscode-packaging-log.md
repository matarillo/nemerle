# WP-L4 VS Code packaging と end-to-end test 実装ログ

実施日: 2026-07-14

ブランチ: `wip/dotnet-port`

開始 commit: `84e6c56dd2b87f69f3eecb083d1a17a9ce376554`

## 結論

`24-vscode-development-plan.md` の WP-L4 を完了した。VS Code extension 0.4.0 は
.NET 10 language server を VSIX の `server/` に同梱し、`nemerle.server.path` 未設定の
clean machine 相当環境(隔離 extensions-dir / user-data-dir、fresh user settings)で
bundled server が自動起動して project-aware diagnostics を出すことを、自動テストで実測した。

- `pack-server.ps1` が LspServer を Release build し、`bin\Release\net10.0\` 全体
  (71 files、10.1 MB)を extension の `server/` へ再現可能に staging する。
- extension は既定で `<extension>/server/Nemerle.LanguageServer.dll` を起動し、
  `nemerle.server.path` は開発時 override 専用になった。
- 起動前に `dotnet --list-runtimes` を実行し、dotnet host 不在・
  `Microsoft.NETCore.App 10.x` 不足を launch 前の理解可能なエラーにする。
- `npm run test:vsix` が VSIX を隔離ディレクトリへ install し、Extension Host 内で
  「installed VSIX の extension が bundled server を無設定で起動 → project applied →
  診断表示 → 未保存修正で消える → server process のコマンドラインが VSIX 内 server を
  指す」ことまで assert する。
- `test-bundled-server.ps1` が VSIX を展開し、WP-L3 の raw stdio LSP 統合シナリオ一式
  (HelloCore / RefDemo / PackageReference / Sokoban / buffer-disk-close-stale)を
  **VSIX 抽出物の server だけ**で全 PASS させる(repo の LspServer bin 非依存の実証)。
- `THIRD-PARTY-NOTICES.md` が VSIX 内の全 bundled assembly と npm 依存の license を記録し、
  `npm run verify-server` が「server closure の必須 file」+「全 assembly の notices 掲載」を
  package 前に機械検証する。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301` / Node.js `v22.21.1` / npm `11.7.0`
- VS Code Extension Host `1.128.0`(`@vscode/test-electron 3.0.0` の archive)
- `@vscode/vsce 3.9.2`、OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。
- server 側コード(`LspServer/` 他 .NET projects)は無変更。ServerInfo は 0.2.0 のまま。

## 設計

### packaging 単位 = server bin ディレクトリ全体

VSIX に入れる server は `LspServer\bin\Release\net10.0\` の **ディレクトリ全体**とした。
理由: この出力は既に自己記述的な実行単位である。

- `Nemerle.dll` / `Nemerle.Compiler.dll` / `Nemerle.Macros.dll` は csproj の
  HintPath 参照(`Private=true`)で bin へコピー済み。
- `Nemerle.Compiler.Utils.dll` / `Nemerle.ProjectInfo.dll` は ProjectReference 出力。
- OmniSharp.* / MediatR / Newtonsoft.Json / System.Reactive / Nerdbank.Streams と
  `runtimes\{win,unix}\...`(Microsoft.Win32.Registry 等の RID 資産)、
  `{cs,de,...}\*.resources.dll`(VS Threading/Validation の satellite)は NuGet 出力。
- `Nemerle.LanguageServer.deps.json` は host の依存解決(特に `runtimes\` 資産の
  RID fallback)に必要なので必ず同梱する。
- `Nemerle.LanguageServer.runtimeconfig.json` は手書き(csproj が
  `DisableImplicitFrameworkReferences` + `GenerateRuntimeConfigurationFiles=false` の
  ため SDK は生成しない)。`net10.0` / `rollForward: LatestPatch`。

### Nemerle.CoreEmit.dll は不要(実測で確定)

引き継ぎ事項だった「analysis-only の LSP engine に CoreEmit が要るか」を確定した。

- ソース根拠: `ncc\generation\CoreEmitBridge.n` の `Assembly.LoadFrom("Nemerle.CoreEmit.dll")`
  は `EnsureLoaded()` 経由でのみ実行され、呼び出し元は `CreateBuilder` / `CreateRunBuilder` /
  `Save` / `SetAssemblyCustomAttribute` の emission 経路だけ。実行時判定
  `CoreEmitBridge.IsCoreClr` は `Type.GetType` probe であり assembly load を伴わない
  (`CoreEmitBridge.n:53-54`)。IDE engine の `BuildTypesTree` は emit しない。
- 実測根拠: `LspServer\bin\Release\net10.0\` に CoreEmit は元々存在せず(WP-L3 の全テストも
  この状態で PASS していた)、本 WP で VSIX 抽出 server(CoreEmit 非同梱)に対して
  macro-heavy Sokoban を含む全シナリオが PASS した。macro assembly の compile-time 実行は
  `IntelliSenseModeLibraryReferenceManager` の byte-load 経路で、emission とは無関係。

将来 build 出力に CoreEmit が現れても pack-server.ps1 はディレクトリ全体コピーの原則で
そのまま同梱する(除外リスト方式にしない。取りこぼしより無害な余剰を選ぶ)。

### 既定 = bundled、設定 = override

`resolveServerLaunch(configuredPath, dotnetPath, bundledServerPath)`:

- `nemerle.server.path` 非空 → 従来どおり絶対 path 検証(source=`configured`)。
- 空 → `<extension>/server/Nemerle.LanguageServer.dll`(source=`bundled`)。missing なら
  「reinstall / pack-server.ps1 / nemerle.server.path」を提示する
  `ServerConfigurationError`。
- 非 `.dll`(将来の apphost)分岐は従来のまま。
- どちらを使ったかを Output Channel に info log する。

### dotnet runtime の存在チェック

`.dll` 形の server を起動する直前に `ensureDotNetRuntime(dotnetExecutable)` を実行する。

- `<dotnet> --list-runtimes`(shell なし、15 s timeout)の stdout から
  `Microsoft.NETCore.App <version>` 行を parse する。
- host 不在(ENOENT)→「.NET 10 runtime の install または nemerle.dotnet.path」を促す
  `DotNetRuntimeError`。実行失敗(非 0 exit 等)→ 原因つきの同 error。
- major 10 の runtime が無い → 検出済み version 一覧つきの error
  (bundled runtimeconfig は `rollForward: LatestPatch` なので 10.x が必要)。
- 成功時は採用 runtime version を info log。エラーは既存の configuration error UI
  (dedup + Open Settings/Show Output)で表示し、server は起動しない。
  parser(`parseNetCoreAppRuntimes` / `findRequiredRuntime`)は純関数として unit test した。

### third-party notices と bundling 判断

- 新規 `THIRD-PARTY-NOTICES.md`(VSIX 同梱): first-party Nemerle 群(BSD-3-Clause、
  University of Wroclaw)、bundled .NET assemblies 30 packages(全 MIT、MediatR のみ
  Apache-2.0。OmniSharp 4 packages は nuspec `type="file"` の同梱 LICENSE = MIT)、
  npm production 依存(WP-L1 の表を継承)。license metadata は各 package の
  `.nuspec` / 同梱 LICENSE(ローカル NuGet cache)を 2026-07-14 に確認した。
- `npm run verify-server`(`test/tools/verifyBundledServer.ts`)が package 前に
  (a) server closure の必須 17 files、(b) `server/**` の全 dll(satellite は
  `.resources` を剥がして親 package に対応付け)が notices に載っていることを検証する。
  vsce package の chain(`npm run package`)に組み込んだ。
- JS 側の bundler(esbuild/webpack)は今回も導入しない。公式 docs 上 bundling は
  性能推奨(Web 環境以外では必須でない)であり、plain-tsc + node_modules 同梱は
  per-package LICENSE file がそのまま VSIX に残る(10 files を実測)利点を優先した。
  vsce の警告は「468 files / 186 JavaScript files」の推奨 1 件のみで、機能/安全性の
  警告ではない。bundle 化する場合は notices 生成を build に組み込む必要がある、を
  packaging rule として本節に明記しておく。

### 版ハザードと provenance

Nemerle assembly version は `git describe` 由来のため、server と workspace 側
`dist/ncc` の世代が混在すると `FileLoadException` になり得る。対策:

- pack-server.ps1 が `server/bundle-info.json`(commit / describe / configuration /
  packedAtUtc)を書き、dirty tree では warning を出す。
- README troubleshooting に FileLoadException → 同一 commit で両方を再 pack する手順を記載。
- 完全な機械検証(dist/ncc 側の commit 照合)は dist に provenance が無いため今回は
  していない(残課題)。

### CI 実行可能な command 一式

```powershell
pwsh dotnet-port\pack-tool.ps1                    # dist/ncc(server build の前提)
pwsh dotnet-port\vscode-nemerle\pack-server.ps1   # server build + server/ staging
cd dotnet-port\vscode-nemerle
npm ci
npm run check-types
npm run lint
npm test                                          # unit(packaging 構造検証含む)
npm run test:integration                          # trusted/untrusted Extension Host
npm run package                                   # lint+test+verify-server+vsce package
npm run test:vsix                                 # 隔離 install で bundled server E2E
pwsh .\test-bundled-server.ps1                    # VSIX 抽出 server で raw LSP 全シナリオ
npm audit; npm audit --omit=dev
```

すべて非対話で、失敗は非 0 exit code になる。

## 実装ファイル

- 新規: `vscode-nemerle/pack-server.ps1`(server staging script)、
  `vscode-nemerle/test-bundled-server.ps1`(VSIX 抽出 raw LSP smoke)、
  `vscode-nemerle/THIRD-PARTY-NOTICES.md`、
  `vscode-nemerle/test/tools/verifyBundledServer.ts`(verify-server 本体)、
  `vscode-nemerle/test/runVsix.ts` / `test/suite/vsix.test.ts` /
  `test/vsix-driver/package.json`(clean-machine 統合テスト。driver は manifest-only の
  development extension で、テスト対象は隔離 extensions-dir に install した VSIX)、
  `vscode-nemerle/test/unit/bundledServer.test.ts`。
- 変更: `src/serverLaunch.ts`(bundled 既定 + override、`ensureDotNetRuntime`、
  `DotNetRuntimeError`、launch source)、`src/clientController.ts`(bundled path 注入、
  runtime check、source log、setting 別の error 誘導)、`src/extension.ts`
  (`context.asAbsolutePath('server/...')`)、`package.json`(0.4.0、server.path 説明、
  `verify-server` / `test:vsix` script、package chain)、`.vscodeignore`(開発 script 除外)、
  `.gitignore`(`server/`)、`test/suite/index.ts`(vsix mode)、
  `test/unit/serverLaunch.test.ts` / `test/unit/manifest.test.ts`(WP-L4 assertions)、
  `README.md`(developer preview の install/run/troubleshooting へ全面改稿)。
- compiler 本体(`ncc/`、`lib/`、`macros/`)、`LspServer/` 等の .NET projects、
  `VsIntegration/` は無変更。

## 検証

### VSIX packaging

- `pack-server.ps1`: LspServer Release build 0 warnings / 0 errors、`server/` へ
  71 files(10.1 MB)staging、`bundle-info.json` 記録
  (commit `84e6c56dd...`、pack 時は WP-L4 作業中のため describe は `-dirty`)。
- `npm run package`: lint / unit 21 tests / verify-server PASS →
  `vscode-nemerle-0.4.0.vsix` 生成。**468 files / 4.42 MB**、内訳は
  `node_modules/` 384 files(2.45 MB)+ `server/` 71 files(10.1 MB)+
  extension 本体。VSIX 内 LICENSE files 10 + `THIRD-PARTY-NOTICES.md` を実測。
  vsce の警告は bundling 推奨 1 件のみ。

### 受け入れ基準 1: clean build から VSIX 生成

`npm ci` からの全 chain(check-types / lint / unit / package)を再実行して確認。PASS。

### 受け入れ基準 2: 隔離 install で bundled server が起動(clean-machine 相当)

`npm run test:vsix`(自動):

1. `.vscode-test/user-data-vsix` / `extensions-vsix` を削除して fresh に作る
   (user settings なし = `nemerle.server.path` 未設定)。
2. VS Code 1.128.0 の CLI(`bin\code.cmd`)で
   `--install-extension vscode-nemerle-0.4.0.vsix --extensions-dir ... --user-data-dir ...`。
3. manifest-only の driver extension を `--extensionDevelopmentPath` にして Extension Host を
   起動(repo の開発 extension は load されない)し、mocha suite が以下を assert:
   - `nemerle-project.vscode-nemerle` の `extensionPath` が隔離 extensions-dir 配下
     (= installed VSIX が対象)で、`server/Nemerle.LanguageServer.dll` /
     `server/...runtimeconfig.json` / `THIRD-PARTY-NOTICES.md` が存在する。
   - `nemerle.server.path` の実効値が空のまま server process が起動する。
   - server PID の実コマンドライン(`Get-CimInstance Win32_Process`)が
     **installed VSIX 内の** server DLL を指す(`dotnet exec <extensions-vsix>\...\server\Nemerle.LanguageServer.dll`)。
   - 単一 `.nproj`(test-workspace/Single.nproj)が自動選択され `applied` になる
     (`appliedToEngine=true`)。
   - loose file `Broken.n` に Error diagnostic が出て、未保存修正で消える。

結果: **1 passing (4.2 s)**、Extension Host exit code 0。

### 受け入れ基準 2'/3: VSIX 同梱物だけで動く server + fixture smoke(自動)

`test-bundled-server.ps1`: VSIX を `.vscode-test\vsix-extract` に展開し、
`extension/server/Nemerle.LanguageServer.dll` に対して WP-L3 の raw stdio LSP 統合
シナリオ一式を実行。**全 5 シナリオ PASS**(server は repo の LspServer bin を一切使わない)。

| シナリオ | MSBuild query | engine rebuild |
|---|---:|---:|
| HelloCore apply + loose 回帰 | 703 ms | 250 / 197 / 3 ms |
| RefDemo(ProjectReference) | 928 ms | 207 / 137 ms |
| PackageReference(Newtonsoft.Json) | 707 ms | 218 ms |
| Sokoban(macro-only reference) | 1042 ms | ≧17 ms(collapse 下限) |
| buffer/close/stale/removal/recovery | 736 / 734 ms | 69–214 ms |

Sokoban の PASS は CoreEmit 非同梱 closure で macro が compile-time plugin として
動く実証を兼ねる(設計節参照)。

### 受け入れ基準 4: server build と既存 raw LSP 統合テスト

- `LspServer` Release build(pack-server 内): 0 warnings / 0 errors。
- `LspServer.IntegrationTest` Release build: 0 warnings / 0 errors。
- repo bin の server に対する raw LSP 統合テスト: PASS(WP-L3 スイート全シナリオ)。
- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query 4 fixtures。
  fixture build が HelloCore/RefDemo/PackageReference/Sokoban の `dotnet build` を兼ねる)。

### 受け入れ基準 5: vulnerability audit

- `npm audit` / `npm audit --omit=dev`: **0 vulnerabilities**(npm ci 再構築後)。
- `dotnet list <proj> package --vulnerable --include-transitive`:
  ProjectInfo / ProjectInfo.Test / LspServer / LspServer.IntegrationTest /
  samples\PackageReference の全てで「脆弱なパッケージはありません」。

### 受け入れ基準 6: WP-L1/L2/L3 回帰

- unit tests: **21/21 PASS**(既存 12 + serverLaunch 追加 6 + bundledServer 3)。
- `npm run test:integration`: trusted **3/3**、untrusted **1/1** PASS。
  trusted suite は `nemerle.server.path` に repo bin を設定するので、開発 override 経路が
  bundled 既定と共存することの回帰テストを兼ねる。untrusted は従来どおり server PID が
  生成されないことを確認(runtime check も trust gate の内側なので untrusted では走らない)。

## 公式情報の確認

以下を 2026-07-14 に確認した(要点のみ)。

- vsce packaging / `.vscodeignore`:
  <https://code.visualstudio.com/api/working-with-extensions/publishing-extension>。
  `.vscodeignore` は「package から除外する glob の列挙」(negation 可)。存在する場合の
  file 選択はこれが基準で、bundling は本 page では必須要件ではない。
- bundling guide:
  <https://code.visualstudio.com/api/working-with-extensions/bundling-extension>。
  bundle が必須なのは VS Code for the Web のみで、desktop では性能推奨。
  bundle しない依存 JS を negated glob で残す方法も公式に記載。
- Language Server Extension Guide:
  <https://code.visualstudio.com/api/language-extensions/language-server-extension-guide>。
  client/`server/` ディレクトリ構成が公式サンプルの形。server は任意言語でよい
  (LSP を話せること)と明記され、外部 runtime の同梱可否は extension 作者に委ねられている。
- testing:
  <https://code.visualstudio.com/api/working-with-extensions/testing-extension>。
  `--extensionDevelopmentPath` / `--extensionTestsPath` の意味、`--disable-extensions` を
  渡さない限り installed extensions が有効なこと、CLI の
  `--install-extension <vsix>` による install(spawnSync 例)を確認。
  `--user-data-dir` の隔離は Workspace Trust テストの文脈で公式に言及。
- Workspace Trust guide(変更なし):
  <https://code.visualstudio.com/api/extension-guides/workspace-trust>。
  `untrustedWorkspaces: { supported: 'limited', restrictedConfigurations }` の形は現行どおり。
- bundled NuGet packages の license は各 `.nuspec`(ローカル cache)と同梱 LICENSE file で
  確認(全 MIT、MediatR 8.1.0 のみ Apache-2.0)。詳細は `THIRD-PARTY-NOTICES.md`。
- 依存 package の追加・更新は npm / NuGet とも 0 件。

## 再現コマンド

「設計」節の CI command 一式と同じ。前提は `dotnet-port\dist\ncc`(pack-tool.ps1)のみ。
raw LSP integration test は fixture の `dotnet build` を自前で実行する。

手動で clean-machine 確認をしたい場合(自動テストと同じ内容):

```powershell
code --install-extension vscode-nemerle-0.4.0.vsix --extensions-dir <fresh-dir> --user-data-dir <fresh-dir2>
# その extensions/user-data ディレクトリで VS Code を起動し、.nproj のある信頼済み
# folder を開く。nemerle.server.path を設定しないまま .n を開くと Output Channel に
# "Using the bundled Nemerle language server: ..." と "Found Microsoft.NETCore.App 10.x"
# が出て、status bar が Nemerle: Project Applied になる。
```

## 既知の制約 / 残課題

1. VSIX は Windows 向け developer preview。server は managed IL だが、Linux/macOS の
   VS Code 実地保証・`powershell.exe` を使う test:vsix の PID 検査は Windows 前提
   (計画どおりスコープ外)。
2. compiler toolchain(`dist/ncc` + `Nemerle.Core.targets`)は VSIX に含まれず、
   build/query には repo checkout が要る。`Nemerle.Sdk` NuGet 化は次 WP。
3. bundle-info.json は server 側の commit を記録するが、workspace 側 dist/ncc との
   同一 commit 検証は自動化していない(dist 側に provenance が無いため)。
   また VSIX を commit 前に組むと describe は `-dirty` になる(記録自体は正しい)。
4. JS bundler は未導入(公式上は性能推奨のみ)。導入する場合は third-party notices の
   生成・検証を build に組み込むこと(本 log の packaging rule)。
5. `npm run test:vsix` は `npm run package` 済みの VSIX を要求する(無ければ明示 error)。
6. .NET runtime の自動 install は行わない(計画どおり)。チェックは起動時のみで、
   起動後に runtime が消えるケースは扱わない。
7. hover / completion / definition、multi-root、Marketplace 公開・署名は
   WP-L 完了後の優先順位リストのまま。
