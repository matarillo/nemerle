# 34. WP-M5: incremental rebuild(relocation)と応答性計測 実装ログ

実施日: 2026-07-15

ブランチ: `wip/dotnet-port`

開始 commit: `2239bd697`(`Implement WP-M4 definition/references via synchronous GetGotoInfo`)

対象: `dotnet-port/docs/29-devenv2-plan.md` の **WP-M5** のみ。`Nemerle.Sdk` NuGet 化(WP-M6)は範囲外。

## 結論

`29-devenv2-plan.md` の WP-M5 を完了した。document sync を `TextDocumentSyncKind.Full` から
`TextDocumentSyncKind.Incremental` に切り替え、range 付き change を engine の relocation 経路
(`BeginUpdateCompileUnit`)に流す配線を追加した。method body 内の編集は該当 method のみ
再型付けされ(full rebuild を経ない)、構造変化・relocation 失敗は engine の判定に従って
自動的に full types-tree rebuild へ fallback する。UTF-16(0-origin)→ engine 1-origin の
change → `RelocationRequest` 変換とバッファ適用は `ProjectInfo` の純関数として固定し
(`HoverMarkup`/`CompletionMapping`/`GotoMapping` の前例)、`ProjectInfo.Test` で unit test 化した。
**compiler / engine(`ncc`・`lib`・`macros`・`VsIntegration`)は無改造**(assembly version は
WP-M1〜M4 と同一 601)。受け入れ基準 1〜7 を自動・実測で確認した。

- **incremental sync + relocation の配線**: `NemerleTextDocumentSyncHandler` は range 付き
  change を editor 中立の `NemerleContentChange` に落とし、`NemerleProject.Change` が「project
  source の単一 range 編集 + project ロード済み」の条件でのみ relocation 経路を選ぶ。
  `InMemoryNemerleSource` にバッファへの range 適用と `RelocationRequestsQueue` への enqueue を
  加え、`_engine.BeginUpdateCompileUnit(source)` を debounce 付きで発行する。engine 内
  `UpdateCompileUnit` が再パース → 構造比較(`IsStructureOfCompileUnitChanged`)し、構造不変なら
  `TryRelocate`(該当 method の `ResetCodeCache` + `AddMethodAtFirstCheckQueue` による method 単位
  再型付け)、構造変化・relocation 失敗なら `RequestOnBuildTypesTree` を立てる。
- **full rebuild fallback の駆動**: `RequestOnBuildTypesTree` は VS の idle loop 前提で
  `IsNeedBuildTypesTree` フラグを立てるだけ(実 rebuild は `OnIdle` の 2s 後 or
  `ProcessPendingTypesTreeRequest`)。LSP server には idle loop が無いため、
  `BeginUpdateCompileUnit` の request 完了を off-lock で待って `ProcessPendingTypesTreeRequest()`
  を1回呼ぶ monitor を新設した。relocation が成立した編集では no-op(`IsNeedBuildTypesTree` false)、
  構造変化・relocation 失敗のときだけ `BeginBuildTypesTree` を起動する。`IsNeedBuildTypesTree` は
  `IIdeEngine` に無い internal プロパティのため、interface に既にある
  `ProcessPendingTypesTreeRequest`(内部でフラグを見て no-op/rebuild を判断)を使うことで
  **engine 無改造**を維持した。
- **escape hatch**: `NEMERLE_INCREMENTAL_UPDATE`(既定 on、`0`/`false`/`off`/`no` で off)を
  起動時に `ServerOptions.FromEnvironment` で読む。off のときは `TextDocumentSyncKind.Full` を
  広告し、従来どおり全編集で `BeginReloadProject` する(§6.4「relocation は engine 最未投入経路 →
  fallback を仕様として持つ」)。§6.4 は `nemerle.server.incrementalUpdate` という**設定名**を
  想定していたが、本 WP の方針(「切替は server capability のみで extension の TypeScript 実装は
  無変更」)を尊重し、拡張の src には手を入れず環境変数を escape hatch の入口とした。ユーザー向け
  設定への昇格(package.json + initializationOptions 配線)は TypeScript 変更を伴うため次期に回す。
- **publish 世代管理は WP-L3 を維持**: 診断は既存の `SetCompilerMessageForCompileUnit`(parse)/
  `SetMethodCompilerMessages`・`ClearMethodCompilerMessages`(method)/ publisher 世代管理を
  そのまま使う。relocation は method 単位の `ClearMethodCompilerMessages` → 再型付け → 
  `SetMethodCompilerMessages` を通るため、method 単位診断がそのまま publish に載る。編集ごとに
  completion cache 世代を bump し、stale な resolve を捨てる規則も維持した。
- **extension は capability 追従**: incremental sync への切替は server capability のみで、
  vscode-languageclient が自動的に range 付き change を送るため TypeScript 実装は無変更
  (test のみ追加なし=既存 Extension Host test が回帰ゲート)。ServerInfo/extension version を
  `0.6.0` → `0.7.0` に更新、README に incremental rebuild と escape hatch を追記した。

## 環境

- Windows 11 / PowerShell
- .NET SDK `10.0.x` / 共有フレームワーク `Microsoft.NETCore.App 10.0.x`
- Node.js `v22.x` / npm `11.x` / VS Code Extension Host `1.128.0`
- OmniSharp.Extensions.LanguageServer.Protocol `0.19.9`(変更なし)
- Nemerle assembly version `1.2.0.601`(`dist/ncc`。engine 無改造で WP-M1〜M4 と同一)
- npm / NuGet の依存 package の追加・更新は無い(lockfile 差分なし)。

## 設計と実装

### 1. change → RelocationRequest 変換とバッファ適用の純関数(§8、受け入れ 全般)

`Nemerle.ProjectInfo.IncrementalSync`(engine/OmniSharp 非依存)に2つの純関数を置いた。

- `ApplyChange(text, NemerleContentChange) : string` — range 付き change を `[Start, End)` の
  UTF-16 span に splice(whole-document change はそのまま置換)。offset 計算は CRLF/CR/LF を
  1 行として数え、行末・文末を越えない clamp(CRLF を割らない)。server のバッファが client の
  文書から乖離しないための土台。
- `ComputeRelocation(NemerleContentChange) : NemerleRelocation` — 0-origin UTF-16 の range から
  engine 1-origin の `Begin`(編集開始 = 旧新共通)/`Old`(旧文書での置換末尾 = LSP range.end)/
  `New`(新文書での挿入末尾 = start + newText の行数と最終行 UTF-16 長)を導く。VS 統合の
  `TextLineChange`(iStart/iOldEnd/iNewEnd、各 +1)を LSP incremental change から再現したもの。

`NemerleRelocation` は `HoverMarkup`/`GotoMapping` と同様に editor/engine 中立で、
`InMemoryNemerleSource` が engine の `RelocationRequest`(`RelocationQueue.AddRelocationRequest`)へ
写す。挿入(Old==Begin)・削除(New==Begin)・置換(Begin≠Old≠New = engine の "isUpdate")・
複数行 newText・CRLF・サロゲートペア(1 文字 2 code unit)を unit test で固定した
(`ProjectInfo.Test` の `IncrementalSyncTests`)。

### 2. didChange の分岐と relocation eligibility(§6.4、受け入れ 1・3・4・6)

`NemerleProject.Change(uri, IReadOnlyList<NemerleContentChange>, version)` は次の条件を**全て**
満たすときだけ relocation 経路を選ぶ:

- `_incrementalEnabled`(escape hatch on)
- 対象が project source(`state.IsProjectSource`)
- project snapshot 適用済み(`_appliedInputs is not null` = types tree 構築済み)
- 単一の range 付き change(`changes.Count == 1 && changes[0].HasRange`)

単一 change に限る理由: engine の `RelocationQueue.GetRelocationRequests` は queue 内の request を
**連番 version**(`f.SourceVersion + 1 == s.SourceVersion`)で merge する。1 didChange = 1 version
なので、複数 change を同一 version で積むと merge の前提が崩れる。VS Code は 1 didChange ごとに
version を +1 するため、単一 change に限れば queue は常に連番で保たれる。上記を満たさない編集
(複数 change・whole-document 置換・loose file・未ロード project・escape hatch off)は
`RelocationRequestsQueue` を drain した上で従来の `BeginReloadProject`(full)へ落とす。

relocation 経路では `source.ApplyRangedChange`(バッファ更新 + `NemerleRelocation` 返却)→
`source.EnqueueRelocation` → debounce(300 ms)後の flush で `BeginUpdateCompileUnit` を発行する。
reload の coalesce 状態(`_pendingIncrementalSources` / `_pendingFullReload`)を新設し、full reload は
常に incremental 更新に優先する(full は全 source を再解析するため個々の relocation を包含する)。

### 3. full rebuild fallback の駆動と engine 無改造(§6.4、受け入れ 3・5)

engine の `UpdateCompileUnit` は AsyncWorker スレッドで走り、構造変化・relocation 失敗時に
`RequestOnBuildTypesTree()`(= `IsNeedBuildTypesTree = true`)を立てるだけで rebuild を起動しない
(VS では `OnIdle` が 2s 後に、または `ProcessPendingTypesTreeRequest` が同期的に起動する)。
LSP server には idle loop が無いため、`FlushPending` が発行した `BeginUpdateCompileUnit` の
`AsyncRequest` 完了を `MonitorUpdateForRebuildAsync`(off-lock、10 ms polling)で待ち、完了後に
`_engine.ProcessPendingTypesTreeRequest()` を1回呼ぶ。これは:

- relocation が成立した編集(method body 内)では `IsNeedBuildTypesTree` false のため **no-op**
  (types tree version 不変)→ full rebuild は走らない(受け入れ 1 を trace で確認)。
- 構造変化・relocation 失敗(`RelocationFailedException` → `RequestOnBuildTypesTree`)のときだけ
  `BeginBuildTypesTree` を起動 → `TypesTreeCreated` → 診断再 publish(受け入れ 3)。

`ProcessPendingTypesTreeRequest` は `IIdeEngine` に既にあり内部でフラグを見て no-op/rebuild を
選ぶため、`IsNeedBuildTypesTree`(engine internal、interface 非公開)にアクセスせずに済み、
**engine 無改造**を維持できた。relocation 失敗経路(受け入れ 5)は構造変化と同じ
`RequestOnBuildTypesTree` → `ProcessPendingTypesTreeRequest` → `BuildTypesTree` の共通 fallback を
通るため、構造変化の test がこの回復機構を実証している(下記「既知の制約」参照)。

### 4. escape hatch と sync kind(§6.4、受け入れ 6)

`ServerOptions.FromEnvironment` が `NEMERLE_INCREMENTAL_UPDATE` を読み、`NemerleProject` と
`NemerleTextDocumentSyncHandler` に注入する。on なら `TextDocumentSyncKind.Incremental` を広告し
上記の relocation 分岐を使う。off なら `TextDocumentSyncKind.Full` を広告し、handler は単一の
whole-document change のみ受理、`NemerleProject` は常に `BeginReloadProject`(WP-M4 以前と同一挙動)。
off でも relocation trace は出ず、診断挙動は従来どおり(受け入れ 6 を実測)。

## 実装ファイル

- 新規: `dotnet-port/ProjectInfo/IncrementalSync.cs`(`NemerleContentChange`/`NemerleRelocation` +
  `ApplyChange`/`ComputeRelocation` 純関数)、`dotnet-port/LspServer/ServerOptions.cs`
  (escape hatch)、本ログ。
- 変更(LSP server): `dotnet-port/LspServer/NemerleProject.cs`(`Change` の relocation 分岐、
  `RequestFullReload`/`RequestIncrementalUpdate`/`FlushPending`/`BumpCompletionGeneration`/
  `MonitorUpdateForRebuildAsync`、ctor に `ServerOptions`)、
  `dotnet-port/LspServer/InMemoryNemerleSource.cs`(`ApplyChanges`/`ApplyRangedChange`/
  `EnqueueRelocation`/`ClearRelocationRequests`)、
  `dotnet-port/LspServer/NemerleTextDocumentSyncHandler.cs`(incremental/full の registration、
  range 付き change → `NemerleContentChange`)、`dotnet-port/LspServer/WorkspaceManager.cs`
  (`ChangeDocument` の signature)、`dotnet-port/LspServer/Program.cs`
  (`ServerOptions` 配線、ServerInfo 0.7.0)。
- 変更(test): `dotnet-port/ProjectInfo.Test/Program.cs`(`IncrementalSyncTests`)、
  `dotnet-port/LspServer.IntegrationTest/Program.cs`(incremental 4 シナリオ + 変更ヘルパー)、
  `dotnet-port/LspServer.IntegrationTest/LspTestClient.cs`(環境変数付き起動と initialize 結果保持)。
- 変更(doc): `dotnet-port/vscode-nemerle/README.md`(incremental rebuild + escape hatch)、
  `dotnet-port/vscode-nemerle/package.json`(version 0.7.0、package:vsix 出力名)、
  `dotnet-port/docs/00-PLAN.md`。
- `ncc/` `lib/` `macros/` `VsIntegration/`(engine 共有ソース)は**無変更**。extension の実装
  TypeScript(`src/`)も**無変更**(sync 切替は capability 追従)。

## 検証(受け入れ基準)

raw stdio LSP integration(`LspServer.IntegrationTest`、既存 23 + incremental 4 = 27 シナリオ)を
実 server process で実施、全 PASS。bundled server(VSIX 抽出)でも同 27 シナリオ全 PASS。

### 基準 1: method body 内編集で full rebuild が走らない + 診断が正しい

temp project(単一 source)を開き、method body 内の初期化子を range 編集で
`= 1;` → `= "x";` と型エラー化。relocation trace(`incremental update (relocation)`)が出ること、
その**後**に `rebuild finished` trace が 2s 現れないこと(= full rebuild 不発)を assert。
診断はエラー導入 → 修正(`= 2;`)で version 追従して error → 0 件へ遷移。
(初期 open の rebuild-finished trace は version-1 publish に遅れて届きうるため、quiet 判定の
起点を relocation trace 直後に置いて、編集起因でない rebuild を誤検知しないようにした。)

### 基準 2: edit-to-diagnostics の incremental/full 前後比較(実測)

Sokoban(5 source + macro)で main.n の method body 内(コメント)を range 編集し、
didChange 送信 → 該当 version の publishDiagnostics 受信までを 12 回計測(warm-up 1 回除外、
debounce 込み):

| 経路 | p50 | p95 |
|---|---|---|
| incremental ON(relocation) | **327 ms** | 404 ms |
| incremental OFF(full reload) | 556〜590 ms | 622〜638 ms |

incremental は full reload 比で **p50 約 45% 短縮**、目標 **p50 ≤ 400 ms(debounce 込み)を達成**。
OFF は同一 fixture を別 server process(`NEMERLE_INCREMENTAL_UPDATE=0`)で計測した。

### 基準 3: 構造変化 → full rebuild fallback + その後の診断/hover/definition

同 temp project で method を1つ range 挿入(構造変化)。version 追従の clean 診断の後に
`rebuild finished` trace が出る(= full rebuild へ fallback)ことを assert。fallback 後、
新 method への hover が内容を返し、definition が location を返すことを確認。

### 基準 4: WP-L3 の buffer/close/stale 相当が incremental 有効で PASS

基準 1 の中で rapid stale sequence(version 4 破損 → version 5 修正を連続送信)を range 編集で
実施。clean version-5 publish の後にエラーが残らないこと(stale 抑止)を assert。加えて既存の
「Buffer override, close revert, stale suppression, source removal, failure recovery」シナリオは
incremental 有効(既定)の server で全 PASS(whole-document change は full 経路へ落ちる)。

### 基準 5: relocation 失敗経路の fallback で診断が壊れない

relocation 失敗(`RelocationFailedException`)は engine 内部で `Completion.Relocate` から投げられ、
`UpdateCompileUnit` が `RequestOnBuildTypesTree` を立てる。これは構造変化と**同一**の
`ProcessPendingTypesTreeRequest` → `BuildTypesTree` fallback を通るため、基準 3 の構造変化 test が
この共通回復機構を実証している。LSP 外からの直接の fault injection は engine 改造を要するため
行わず、共通経路の実証で代替した(下記「既知の制約」1)。

### 基準 6: `NEMERLE_INCREMENTAL_UPDATE=0` で従来動作に戻る

escape hatch off の別 server process を起動し、initialize capability が full sync(change kind 1)を
広告すること、whole-document change で診断が error → 0 件へ遷移すること、relocation trace が
一切出ないことを assert。

### 基準 7: hover/completion/definition が incremental 有効時も全 PASS(回帰ゲート)

incremental 既定 on の下で WP-M2〜M4 の hover/completion/definition/references シナリオ(19 件)が
全 PASS。基準 1 の末尾でも incremental 編集後の hover/definition が正しいことを確認。

### 回帰ゲート(WP-L2/L3/M1〜M4)

- `ProjectInfo.Test -- --integration`: PASS(unit + 実 MSBuild query。`IncrementalSyncTests` 追加)。
- raw LSP integration(repo bin server): 27/27 シナリオ PASS(既存 23 + incremental 4)。
- extension: `npm ci`(0 vuln)/ `check-types` / `lint` / `npm test`(21/21)/
  `test:integration`(trusted 4 + untrusted 1)/ `package`(`vscode-nemerle-0.7.0.vsix`、468 files /
  4.45 MB)/ `test:vsix`(隔離 install 1 passing)/ `test-bundled-server.ps1`(VSIX 抽出 server で
  27/27 PASS)。TypeScript src 無変更のため Extension Host test は追加せず既存が回帰ゲート。
- vulnerability: `npm audit` / `npm audit --omit=dev` = 0。
  `dotnet list package --vulnerable --include-transitive` = LspServer / ProjectInfo /
  ProjectInfo.Test / LspServer.IntegrationTest の全てで脆弱 package なし。
- compiler / engine 無改造のため Stage リビルド・testsuite 再実行は不要
  (assembly version は WP-M1〜M4 と同一 601)。pack-server.ps1 で bundled server を再 staging した。

## 再現コマンド

```powershell
# server / handler をビルド
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild

# unit + raw LSP integration(incremental 含む。edit-to-diagnostics の実測もここで出力)
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll

# escape hatch off の従来動作を単体確認(任意)
$env:NEMERLE_INCREMENTAL_UPDATE = "0"   # server 起動側で設定

# server を再 staging(サーバーコード変更時)
pwsh dotnet-port\vscode-nemerle\pack-server.ps1

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

1. relocation 失敗(`RelocationFailedException`)の直接 fault injection は engine 改造を要するため
   行っていない。失敗経路は構造変化と同じ `RequestOnBuildTypesTree` → `ProcessPendingTypesTreeRequest`
   → `BuildTypesTree` 回復を通り、構造変化 test がこの共通経路を実証している。engine 内経路を強制する
   test は将来 engine 側にテストフックを入れる場合に追加する。
2. relocation 経路は**単一 range change の didChange** に限定している(engine の relocation-queue
   merge が連番 version を要求するため)。複数 change(マルチカーソル編集・大きな貼り付けの一部)は
   安全側に倒して full reload へ落とす。VS Code の通常のタイピングは 1 didChange = 1 change なので
   実用上の増分価値は保たれる。将来、同一 didChange 内の複数 change に連番 sub-version を割り振る
   拡張余地はあるが、queue の version 衝突を避ける設計が要る。
3. escape hatch は環境変数 `NEMERLE_INCREMENTAL_UPDATE`。§6.4 が想定した VS Code 設定
   `nemerle.server.incrementalUpdate` への昇格は extension の TypeScript(initializationOptions
   配線)を要するため、本 WP の「TypeScript 実装は無変更」方針の下では見送った。設定化は
   semantic tokens 等の次期 WP でまとめて行うのが自然。
4. full rebuild fallback は `BeginUpdateCompileUnit` の完了を待ってから
   `ProcessPendingTypesTreeRequest` を呼ぶ monitor で駆動する(off-lock)。構造変化時に relocation
   trace の後 rebuild-finished trace が続くのはこのため。VS の `OnIdle`(2s)には依存していない
   ので、構造変化の反映は idle 待ちなく行われる。
5. `Nemerle.Sdk`(WP-M6)は未着手。WP-M5 で sync 方式を incremental へ差し替えたことで、
   WP-M2〜M4 の integration test(19 件)が今後の基盤変更の回帰ゲートとして機能する。

前身の実装ログ: `30-devenv2-wp-m1-log.md`(WP-M1)/ `31-devenv2-wp-m2-log.md`(WP-M2)/
`32-devenv2-wp-m3-log.md`(WP-M3)/ `33-devenv2-wp-m4-log.md`(WP-M4)。計画: `29-devenv2-plan.md`。
