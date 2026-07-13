# WP-L3 VS Code project-aware engine workspace 実装ログ

実施日: 2026-07-14

ブランチ: `wip/dotnet-port`

開始 commit: `52c9d38faf1b9ae789688843109992679a6ba386`

## 結論

`24-vscode-development-plan.md` の WP-L3 を完了した。WP-L2 の MSBuild snapshot が
LSP server の analysis engine に適用され、diagnostics が project-aware になった。

- selected project の全 `NemerleCompile` source が engine workspace に載る。
  open document は未保存 LSP buffer、closed source は disk-backed text。
- ProjectReference / PackageReference の resolved assembly と macro-only reference が
  engine に渡り、macro-only output は assembly reference に混入しない。
- didClose した project source は disk 内容へ戻り、project から消えない。
- 連続 didChange / reload で古い version の diagnostics が新しい document を上書きしない。
- 失敗した project query / apply は型付きの回復可能な結果のままで、直前の
  engine workspace と server は継続する。
- VS Code extension 0.3.0 は snapshot の engine 適用状態を status bar に表示し、
  `.nproj` / imported targets / NuGet assets / on-disk `.n` / 参照 dll の変更を監視して
  適切な粒度の reload を行う。WP-L2 の「not applied」固定表記は削除した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.301` / MSBuild `18.6.4.27133`
- Node.js `v22.21.1` / npm `11.7.0`
- VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- npm / NuGet の依存 package の追加・更新は無い。

## 設計

### snapshot → engine の経路

```text
nemerle/projectInfo/load (custom LSP request, [Serial])
  → ProjectInfoProvider (WP-L2, 変更なし: cache / single-flight / timeout)
  → EngineWorkspaceInputs.FromSnapshot (純関数, ProjectInfo assembly)
  → WorkspaceManager.ApplySnapshotAsync (closed source を disk から読込)
  → NemerleProject.ApplyProject (workspace 差替え + BeginReloadProject)
```

- `EngineWorkspaceInputs`(`dotnet-port/ProjectInfo/EngineWorkspaceInputs.cs`)は
  snapshot のうち engine に適用する部分を導出する純関数。ProjectInfo assembly に
  置いたのは unit test のため(LspServer は実 shared-framework 参照でビルドされるため
  通常の SDK project から compile 参照できない)。ProjectInfo は引き続き
  OmniSharp にも Nemerle compiler にも依存しない。
- `WorkspaceManager`(`dotnet-port/LspServer/WorkspaceManager.cs`)は snapshot 変換と
  closed source の disk 読込(lock 外の async I/O)だけを担い、engine 変異の直列化は
  `NemerleProject` の `_engineOperations` lock に一元化した。
- `NemerleProject`(`IIdeProject` adapter)が workspace の唯一の状態保持者:
  正規化 path → document state(source / publish URI / open / project-source flag)。
  `IIdeProject.GetSources()` / `GetAssemblyReferences()` / `GetMacroAssemblyReferences()` /
  `GetOptions()` が適用済み inputs を返し、engine の `BeginReloadProject()` →
  `BuildTypesTree` が全 source を再構築する(`Engine-BeginReloadProject.n:33-41`,
  `Engine-BuildTypeTree.n:128-263`)。

### 単一 engine を保つ

計画 6.3 は「選択変更時は旧 engine を dispose し再作成」としていたが、
`BeginReloadProject()` は `PersistentLibraries=false` を設定するため、次の
`BuildTypesTree` が `ResetCompilerState(Options)` から参照・macro・型情報を全て
再構築する。project 切替もこの full reload で表現でき、`AsyncWorker` の
force-out(同一 engine の `BuildTypesTree` 同士は新しい要求が古い要求を停止・破棄)が
連続 reload を collapse する。engine 再作成は process-wide `AsyncWorker` の応答経路を
壊すリスクの方が大きいため、単一 engine instance を維持した。

### buffer / disk / close の規則(計画 6.4 どおり)

- 1 つの正規化 path につき `InMemoryNemerleSource` は 1 instance。
  open は client 版番号で buffer を差替え、close は disk text を読み内部版番号を
  +1 して差替える(project source の場合)。project item でない loose file だけを
  didClose で workspace から削除し `NotifySourceDeleted` する。
- path は `ProjectPathNormalizer.NormalizeFile`(絶対化 + drive letter 大文字化)で
  正規化してから `Location.GetFileIndex` に渡す。`_filesMap` は大文字小文字を
  区別する ordinal dictionary(`ncc/parsing/AST.n:248-267`)なので、URI 由来 path と
  MSBuild 由来 path の case 差で同一 file が二重登録される事故をここで防ぐ。
  重複 path は snapshot 側(`NormalizeDistinct`)と workspace 側(dictionary)の両方で
  collapse される。

### stale diagnostics 抑止

1. **解析済み version の記録**: `SetCompilerMessageForCompileUnit` は
   `compileUnit.SourceVersion == source.CurrentVersion` のときだけ
   `(version, messages)` を保存する(旧実装は version を捨てていた)。
2. **publish 時の一致判定**: 文書の parse entry version が現在の text version と
   一致するときだけ diagnostics を publish し、その version をラベルに使う。
   不一致(= 新しい didChange を engine が未解析)の文書は「変更なし」marker
   (`DocumentDiagnostics.Diagnostics == null`)で通知し、publisher は直前の
   published 状態を保持する。version N+1 の変更後に version N の内容から計算した
   diagnostics が N+1 のラベルで流れる経路は存在しない。
3. **method message の世代検査**: 従来どおり `IntelliSenseModeMethodBuilder.TypesTreeVersion`
   と engine の現在値を照合し、加えて各 build 開始時の `ClearAllCompilerMessages` が
   全 store を消す。
4. **配送順序の直列化**: 応答 pump thread と document/apply thread の両方が
   diagnostics イベントを発火するため、payload 構築と配送を一つの lock で直列化し、
   古い状態から作った payload が新しい payload の後に届くことを防いだ。
5. **publisher の重複抑止と掃除**: URI ごとに最後に publish した (version, payload hash)
   を記録して同一内容の再送を抑止し、workspace から消えた URI(didClose した loose
   file、project から削除された source)には空 diagnostics を一度 publish して消す。
   didClose した project source は「version+1 の空 parse entry」を合成して buffer 由来の
   diagnostics を即時クリアし、次の rebuild が disk-backed の結果を version 無しで
   再 publish する。closed source の publish は LSP `version` field を持たない。

### options の build parity

engine に適用するのは build が実際に ncc へ渡すものだけにした。

- 適用: source、assembly reference、macro reference、
  `NemerleAdditionalOptions` 由来の `-define` / `-checked` / `-indentation-syntax`。
- 非適用: MSBuild の `DefineConstants` property。現行 `Nemerle.Core.targets` は
  これを compiler task に渡していない(WP-L2 記録済みの build gap)ため、IDE だけが
  `NET10_0` 等を定義すると `dotnet build` と診断が食い違う。適用しない代わりに
  `EngineWorkspaceInputs.FromSnapshot` が warning を生成し、status/Output に出す。
  targets 側の gap 修正は WP-L3 のスコープ外のまま。
- 非適用(従来どおり warning): `-ref` / `-macros` / `-root-namespace` 等の
  unsupported semantic option と warning policy option。
- project 未適用(loose-file mode)の既定 `DEBUG;TRACE` define は WP-K 挙動のまま。

### watch / reload(extension 0.3.0)

変更の種類で reload の粒度を分けた。すべて 350 ms debounce、単一 in-flight queue、
force フラグは合流時に OR される。

| 監視対象 | watcher | 動作 |
|---|---|---|
| selected `.nproj` | `**/*.nproj`(既存) | force reload(MSBuild 再照会) |
| imported targets/props | `**/*.{targets,props}` | force reload |
| NuGet assets | `**/obj/project.assets.json` | force reload |
| project source の disk 変更 | `**/*.n`(snapshot の sourceFiles に一致した時のみ) | cache-hit 再適用(disk 再読込) |
| resolved reference / macro dll | 各 path ごとの `RelativePattern(dir, base)` watcher(上限 64) | cache-hit 再適用 |

再適用(forceReload=false)は provider cache に当たるため MSBuild は起動せず、
server 側で closed source の disk text を読み直して `BeginReloadProject` する。
参照 dll は `IntelliSenseModeLibraryReferenceManager` が byte 配列 load + file 時刻
cache(`UpdateAssemblies`)で管理するため、file lock を作らず、更新時刻が変われば
再 load される。`RelativePattern` の base に `Uri.file(dir)` を使うので、workspace
folder 外の resolved reference(NuGet cache 等)も監視できる。

このために custom request の結果へ `sourceFiles` / `assemblyReferences` /
`macroReferences`(絶対 path 配列)と `appliedToEngine` / `applyError` を追加した。

### trust

変更なし。untrusted workspace では extension が server を起動しないため、
MSBuild query も project engine workspace も存在し得ない。command の runtime guard は
WP-L2 のまま(untrusted Extension Host test で server PID が生成されないことを再確認)。

## 実装ファイル

- 変更: `LspServer/NemerleProject.cs`(workspace 化・stale 抑止・reload debounce 300 ms・
  rebuild 時間 trace)、`LspServer/InMemoryNemerleSource.cs`(URI を workspace 管理へ移動)、
  `LspServer/NemerleTextDocumentSyncHandler.cs`(workspace-wide publisher)、
  `LspServer/NemerleProjectInfoHandler.cs`(apply 経路と結果拡張、query 時間 trace)、
  `LspServer/Program.cs`、`ProjectInfo/ProjectSnapshot.cs`(`ProjectPathNormalizer` 公開)、
  `ProjectInfo.Test/Program.cs`(unit test 追加)、
  `LspServer.IntegrationTest/Program.cs`(WP-L3 シナリオ一式へ全面改稿)、
  extension の `src/clientController.ts` / `src/projectController.ts` /
  `src/projectDiscovery.ts` / `package.json`(0.3.0)/ `README.md` / tests。
- 新規: `LspServer/WorkspaceManager.cs`、`ProjectInfo/EngineWorkspaceInputs.cs`、
  `LspServer.IntegrationTest/LspTestClient.cs`。
- compiler 本体(`ncc/`、`lib/`、`macros/`)と `VsIntegration/` は無変更。

## 検証

### raw stdio LSP integration(`LspServer.IntegrationTest`)

fixture(HelloCore / RefDemo / PackageReference / Sokoban)を `dotnet build` してから、
シナリオごとに実 server process を起動して実施。全て PASS。

1. **HelloCore apply + loose 回帰**: snapshot が `appliedToEngine=true` で適用され、
   closed `hello.n` に version 無しの error-free diagnostics が publish される。
   存在しない `.nproj` は `NonZeroExit` の回復可能な結果。その後の
   didOpen(error)→didChange(clear)→didClose(clear) の WP-K/WP-L1 flow が
   HelloCore workspace を適用したまま成立。
2. **RefDemo**: project 適用前に開いた `Program.n` は unbound `Calc` の error になり
   (negative control)、`App.nproj` 適用後に同じ open buffer(version 1)が
   error-free になる。`assemblyReferences` に `MathLib.dll` を確認。
3. **PackageReference**: resolved `Newtonsoft.Json.dll` が適用され、facade
   (`System.Runtime.dll`)は混入しない。`JsonConvert.SerializeObject` を使う未保存
   buffer が error-free。
4. **Sokoban**: `SokobanMacros.dll` が macro reference にだけ入り、closed `sokoban.n`
   (`NextMove` / `UseTunnelMacro` 使用)が error-free = macro が compile-time plugin
   として engine に load された。`main.n` を開くと `MapCollection` / `SMap` /
   `TreeSearch` / `LocalSearch` など他 source 宣言の symbol が解決される。
5. **buffer/disk/close/stale/removal/recovery**(一時 project、restore のみ):
   - 適用直後、disk 上で壊れた closed `broken.n` に version 無しの error。
   - didOpen v1 = error → didChange v2(`TempOther.Helper()` を使う修正)= 空。
     disk file は壊れたままであることを byte 比較で確認(buffer 優先の実証)。
   - v3(壊す)→ 直後に v4(直す): v4 の空 publish 後 3 秒間、この URI への
     error publish が一切ないことを実測(stale 抑止)。
   - didClose: 直ちに version 無しの空 → rebuild 後に disk-backed error が再出現
     (close で disk へ戻り、project からは消えない)。
   - `.nproj` から source を削除して reload: その URI の diagnostics が空になる。
   - 壊れた XML の `.nproj` load は typed error で、直後の loose file diagnostics が
     動く(crash / 無限 restart なし、全シナリオで server は exit code 0 で終了)。

### 計測(上記実行の server trace より)

| 対象 | MSBuild query | engine rebuild |
|---|---:|---:|
| HelloCore(source 1) | 659 ms | 211 / 155 ms |
| RefDemo App(ProjectReference 1) | 835 ms | 125–181 ms |
| PackageReference | 652 ms | 125 ms |
| Sokoban(source 5 + macro) | 960 ms | ≧14 ms(注) |
| 一時 project(source 2) | 617–654 ms | 76–195 ms |

query と rebuild は別々に stderr へ trace される。(注)連続 reload が
`AsyncWorker` の force-out で collapse した場合、rebuild 時間は最後の要求からの
経過になるため下限値である。didChange から空 diagnostics 受信までは
debounce 300 ms 込みで概ね 0.5–1 秒。

### unit tests

- `ProjectInfo.Test`: `EngineWorkspaceInputs.FromSnapshot` の build-parity
  (macro 分離、`-define` のみ適用、`DefineConstants` gap warning の有無)、
  `ProjectPathNormalizer` の drive-letter 大文字化・相対 segment 解決・
  case 違い重複の collapse。既存 parser/error/provider tests は無変更で PASS。
- extension unit: `normalizeComparablePath`(separator / 相対 / Windows case)を追加。
  12/12 PASS。

### VS Code Extension Host(1.128.0)

- trusted 3/3 PASS: 単一 `.nproj` の自動選択が `applied` 状態になり
  `appliedToEngine=true` と `sourceFiles` を API で確認。QuickPick 選択・reload・
  status 表示・Broken.n(loose)の未保存 error→fix→clear、さらに project source
  `Editing.n` の未保存 error → revert+close での clear、restart の PID 交代。
- untrusted 1/1 PASS: server PID が生成されず、command 直接呼び出しでも
  query/workspace が開始されない。

### build / package / audit

- `LspServer` / `ProjectInfo` / 両 test project の Release build: 0 warnings / 0 errors。
- `ProjectInfo.Test -- --integration`: PASS(実 MSBuild query 4 fixtures)。
- `npm ci` → `check-types` / `lint` / `test`(12/12)/ `test:integration`(3+1)/
  `package`: PASS、`vscode-nemerle-0.3.0.vsix`(396 files)生成。
  vsce の bundling 推奨 warning は WP-L2 と同様で WP-L4 で扱う。
- `npm audit` / `npm audit --omit=dev`: 0 vulnerabilities。
- `dotnet list <proj> package --vulnerable --include-transitive`:
  ProjectInfo / ProjectInfo.Test / LspServer / LspServer.IntegrationTest /
  samples\PackageReference の全てで脆弱 package なし。
- HelloCore / RefDemo / PackageReference / Sokoban の `dotnet build`: 成功
  (integration test が毎回実行)。

## 公式情報の確認

以下を 2026-07-14 に確認した。

- LSP 3.17 `textDocument/publishDiagnostics`: `version` field は「diagnostics が
  どの document version に対するものか」(3.15 追加)。project 系言語では close 後も
  diagnostics を保持してよく、空配列で明示的に消す。
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
  (該当節 source: <https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/language/publishDiagnostics.md>)
- VS Code API(`workspace.createFileSystemWatcher`、`RelativePattern` の
  `Uri` base、`FileSystemWatcher` の create/change/delete event):
  <https://code.visualstudio.com/api/references/vscode-api>。
  pinned `@types/vscode 1.125.0` の型定義に対する compile と Extension Host 1.128.0
  での実挙動(workspace 外 dll の監視を含む watcher 生成)で確認。
- Workspace Trust guide(変更なし、WP-L1/L2 の gate を維持):
  <https://code.visualstudio.com/api/extension-guides/workspace-trust>
- OmniSharp 0.19.9: 新規 API は使用していない(`PublishDiagnosticsParams.Version` は
  WP-K から使用済み)。version 固定・source 参照は `26-vscode-project-info-log.md` の
  確認結果から変更なし。
- 依存 package の追加・更新は npm / NuGet とも 0 件(lockfile 差分なし)。

## 再現コマンド

```powershell
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll

Push-Location dotnet-port\vscode-nemerle
npm ci
npm run check-types
npm run lint
npm test
npm run test:integration
npm run package
npm audit
npm audit --omit=dev
Pop-Location
```

前提: `dotnet-port\dist\ncc`(`pack-tool.ps1`)。LSP integration test は fixture の
`dotnet build` を自前で実行する。

## 既知の制約 / 残課題

1. multi-root workspace、複数 project、複数 engine は未対応(計画どおり)。
   `AsyncWorker` は process-wide のまま。
2. MSBuild `DefineConstants` は engine にも build にも渡らない(既存 targets gap)。
   warning で可視化した。targets 修正と `#if` 系 semantic の整合は別作業。
3. `NemerleAdditionalOptions` の `-ref` / `-macros` / `-root-namespace` 等は
   引き続き warning のみで未適用(WP-L2 方針の継続)。
4. 参照 dll の byte-load は unload されないため、依存を何度も再ビルドする長時間
   session では load 済み assembly が蓄積する(headless engine の既知特性)。
5. 全変更が full `BeginReloadProject`(relocation-based incremental は計測後の
   独立 WP)。edit debounce は server 300 ms + extension 350 ms。
6. rebuild 時間 trace は collapse した連続 reload では下限値になる。
7. 参照 watcher は 64 個まで(超過分は warning を出して監視しない)。
8. bundled server / VSIX 一体配布 / clean-machine 検証は WP-L4。
   hover / completion / definition は WP-L 完了後の優先順位リストのまま。
