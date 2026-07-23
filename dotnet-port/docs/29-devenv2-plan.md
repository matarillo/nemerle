# 29. WP-M: .NET 10 Nemerle 開発環境2(language features + toolchain 配布)計画

作成日: 2026-07-14

## 1. この文書を `00-PLAN.md` / `24-vscode-development-plan.md` から分離する理由

`24-vscode-development-plan.md`(WP-L)は「VS Code で project-aware diagnostics と
build/run がつながった状態」を完了条件とし、WP-L1〜L4 で達成された。extension 0.4.0 は
language server を VSIX に同梱し、clean machine 相当の隔離 install で project-aware
diagnostics まで自動テストで実証済みである(`28-vscode-packaging-log.md`)。

次に扱うのは、その基盤の上に載せる language features(hover、completion、definition)、
編集応答性(incremental rebuild)、そして compiler toolchain の repo checkout 依存を断つ
`Nemerle.Sdk` NuGet 化である。これらは WP-L と異なる成果物・受け入れ基準・リスクを持ち、
24 番の文書に追記すると WP-L の完了条件と混ざる。したがって WP-L と同じ方式を採る。

- 本文書を WP-M(.NET 10 Nemerle 開発環境の第2期)の実行計画とする。
- `00-PLAN.md` には最新到達点と本文書へのリンクだけを追記する。
- 各 work package の実装結果は、従来どおり番号付き log 文書(`30-*.md` 以降)へ記録する。

## 2. 現在地

すでに利用できるもの(詳細は各 log):

- .NET 10 self-hosted ncc、stage2/stage3、testsuite 596/636、`dist/ncc` レイアウト、
  SDK-style `.nproj` + `Nemerle.Core.targets` + in-process `NccCompile` task。
- VS Code extension 0.4.0 + Nemerle.LanguageServer(ServerInfo 0.2.0、OmniSharp 0.19.9、
  VS Code 1.128.0 で検証)。bundled VSIX、`dotnet --list-runtimes` 検査、
  clean-machine 相当自動テスト、third-party notices 機械検証。
- project-aware diagnostics: MSBuild JSON query(0.6〜1.0 s)→ snapshot →
  engine workspace(rebuild 100〜250 ms、fixtures 実測)。open buffer 優先、
  stale 抑止、watch/reload。edit-to-diagnostics は debounce 込みで 0.5〜1 s
  (`27-vscode-project-workspace-log.md`)。

language features の材料(裏取り済みの事実):

- IDE engine には LSP の主要機能に対応する API が既存
  (`VsIntegration/Nemerle.Compiler.Utils/Nemerle.Completion2/Engine/IEngine.n`):
  `BeginGetQuickTipInfo`(hover)、`Completion`(completion)、
  `GetGotoInfo(source, line, col, GotoKind.Definition|Usages)`(definition/references)、
  `BeginGetMethodTipInfo`(signatureHelp)、`BeginHighlightUsages`(documentHighlight)、
  `ScanLexer`(semantic tokens 相当)。`22-lsp-step1-log.md` の headless ConsoleTest が
  QuickTip / Completion / GotoInfo / Highlight の計算ロジックを 58 件中 52 PASS で実証済み
  (残 6 件は BCL 差の期待値ずれで機能欠陥ではない)。
- hover/completion の text は VS2010 向けの擬似 markup
  (`<keyword>`、`<lb/>`、`<hint>`、`HtmlMangling` による `&amp;` 等のエスケープ。
  `Hints/HintHelper.n`、`CodeModel/QuickTipInfo.n`)。LSP `MarkupContent` への変換層が必要。
- 増分再解析の実体は engine に既存: `Engine-UpdateCompileUnit.n` の `TryRelocate` は
  編集が method body 内に収まる場合、該当 method のみ再型付けする
  (`RelocationRequestsQueue` + `AddMethodAtFirstCheckQueue`)。現 LSP server は
  `InMemoryNemerleSource.RelocationRequestsQueue` を常に空にしているため未活用で、
  didChange ごとに full `BeginReloadProject` している(`23-lsp-step2-log.md` の既知制約)。
- OmniSharp.Extensions.LanguageServer 0.19.9 の実 assembly をリフレクションで検査し、
  hover / completion(+resolve)/ definition / signatureHelp /
  semanticTokens(full/range/delta)/ documentFormatting /
  pull diagnostics(`DocumentDiagnosticHandlerBase` / `WorkspaceDiagnosticHandlerBase`)/
  `window/logMessage` の handler・DTO が全て存在することを確認した(2026-07-14)。
  本計画の全機能は 0.19.9 のまま実装できる。

既知の負債(WP-L の log に記録済み。本計画での扱いは §4 / §7):

1. server の情報レベル trace が全て stderr(`Console.Error`)に出るため、
   vscode-languageclient 10.1.0 が Output Channel に **[error] として表示**する
   (Project Owner 指摘。`25-vscode-extension-log.md` の stderr 転送仕様)。
2. `Nemerle.Core.targets` が MSBuild `DefineConstants` を ncc に渡さない
   (IDE/build parity gap。engine 側も非適用 + warning、`27-log` 設計節)。
3. in-process task の警告に N コードが付かない(`CompilerHost.cs:78-82` に原因と
   1 行修正先 `ncc\parsing\Utility.n` を記録済み。Stage リビルドが必要)。
4. toolchain(`dist/ncc` + targets)が repo checkout 依存(`DISTRIBUTION.md`、
   `Nemerle.Sdk` NuGet 化は次 WP と明記済み)。
5. dist/ncc に provenance が無く、bundled server との同一 commit 照合が自動化できない
   (`28-log` 残課題 3)。
6. `AsyncWorker` が process-wide(`module` = static 単一 thread + 共有 queue)。
7. 編集ごとの full `BeginReloadProject`(上記のとおり)。
8. `NemerleAdditionalOptions` の `-ref`/`-macros`/`-root-namespace` 等は warning のみ。
9. byte-load 参照 assembly が unload されない、参照 watcher 上限 64。
10. Linux: build 経路は WSL 実証済みだが、VS Code extension 実地・Linux 用 layout script・
    `test:vsix` の PID 検査(powershell.exe)は Windows 前提。

## 3. ゴール

Windows 11、.NET 10 SDK、VS Code がある環境で、次の状態に到達する。

1. `.n` の編集中に hover(型・シグネチャ・ドキュメント)、completion(`.` トリガー含む)、
   definition / references が project context(全 source + 解決済み参照 + macro)を
   使って動く。
2. method body 内の編集が full project rebuild を経ずに診断へ反映され、
   edit-to-diagnostics の実測が改善する(数値は WP-M5 の受け入れ基準)。
3. 正常時の Output Channel から誤解を招く [error] 表示が消え、server のログが
   LSP の `window/logMessage` レベル(Info/Log/Warning/Error)で分類される。
4. IDE と `dotnet build` が同じ define / 診断コードを持つ(DefineConstants gap と
   N コード欠落の解消)。
5. repo checkout なしで、`dotnet new` template + `Nemerle.Sdk` NuGet package(local feed)
   だけで Nemerle project を作成・build・実行でき、VS Code extension がその project で
   project-aware diagnostics を表示する。

## 4. 非ゴール

WP-M には次を含めない(番号は §2 の負債リストとの対応):

- **semantic tokens、formatting、signatureHelp、documentHighlight、rename、codeAction**。
  engine API は存在する(`ScanLexer`、`Formatter`、`BeginGetMethodTipInfo`、
  `BeginHighlightUsages`、`Project.Refactoring.n`)が、TextMate 静的彩色が既にあるため
  増分価値が相対的に低く、hover/completion/definition の基盤(WP-M2 の bridge)を
  流用して次期に実装する方が安全。§11 の優先順位リストへ。
- **multi-root workspace / 複数 project・複数 engine の並列実行**(負債 6)。
  `AsyncWorker` は process-wide のまま単一 engine を維持する。将来 multi-root を
  実装する際は「project ごとの server process 分離」を engine 隔離より優先して比較する
  (`24-plan` §6.3 の方針を維持)。WP-M では、この将来分離を妨げないよう
  server 側の per-project 状態を `NemerleProject` に閉じたまま保つ、という境界だけ守る。
- **pull diagnostics への移行**。0.19.9 に型はある(§2)が、push
  (`publishDiagnostics`)で全要件を満たせており、移行は挙動リスクだけが増える。
- **OmniSharp 0.19.9 の置き換え**(§6.5 で再評価条件のみ更新)。
- **`NemerleAdditionalOptions` の `-ref`/`-macros`/`-root-namespace` 等の適用**(負債 8)。
  これらは MSBuild の Reference / NemerleMacroReference / RootNamespace 相当で表現でき、
  fixture にも使用例が無い。warning による可視化を継続する。
- **byte-load 参照 assembly の unload と参照 watcher 上限 64 の拡張**(負債 9)。
  発生条件(依存の頻繁な再ビルド、65 個以上の参照)が現 fixture / 想定規模で遠く、
  `Nemerle: Restart Language Server` で回復できる。既知の特性として README に残す。
- **Linux/macOS の VS Code extension 実地保証、Marketplace 公開、署名、CI release、
  .NET runtime 自動 install**(負債 10)。Linux は WP-M6 で「Sdk package が OS 非依存で
  build できる」ことまで確認し(WSL)、editor 実地検証と release 工程は WP-N(§11)。
- **Visual Studio 2010 integration の置き換え**。

## 5. 全体構成

```text
VS Code
  └─ vscode-nemerle extension (TypeScript)
       └─ vscode-languageclient (stdio)
            ▼
       Nemerle.LanguageServer (.NET 10, OmniSharp 0.19.9)
            ├─ document sync (didOpen/didChange/didClose)     … 既存
            ├─ publishDiagnostics + 世代管理                   … 既存
            ├─ nemerle/projectInfo/load → WorkspaceManager     … 既存
            ├─ window/logMessage(Info/Log/Warning/Error)     … WP-M1
            ├─ hover / completion / definition handlers        … WP-M2〜M4
            │    └─ EngineRequestBridge(Begin* → Task、cancel)
            └─ NemerleProject / IIdeEngine(単一 engine)
                 ├─ BeginReloadProject(project/構造変更時)
                 └─ BeginUpdateCompileUnit + relocation         … WP-M5
                      (method-body 編集の増分再型付け)

dotnet build(repo checkout 不要化 … WP-M6)
  └─ .nproj: <Project Sdk="Nemerle.Sdk/x.y.z"> または global.json msbuild-sdks
       └─ Nemerle.Sdk.nupkg
            ├─ Sdk/Sdk.props / Sdk/Sdk.targets(Microsoft.NET.Sdk を明示 import)
            ├─ tasks/(NccCompile + Hosting)
            └─ tools/ncc/(compiler layout + provenance)
       └─ Nemerle.Templates.nupkg(dotnet new、別 package)
```

## 6. 主要な設計判断

### 6.1 機能(hover 等)を配布整備(Nemerle.Sdk)より先行させる

両論:

- **配布先行**の利点: repo checkout 依存(負債 4)が消え、repo 外の利用者が
  developer preview を試せる。provenance / versioning(負債 5)も同時に解決しやすい。
  extension は既に VSIX 単体で配布可能なので、toolchain だけが最後の障壁である。
- **機能先行**の利点: (a) WP-L3/L4 で検証したての engine workspace の直上に積むため、
  「後続機能を誤った基盤の上に作らない」原則に適合する(Sdk 化は editor 機能と直交で、
  後回しにしても機能側の基盤を汚さない)。(b) 現時点の実利用者は repository 保有者のみで、
  外部配布の需要より編集体験の不足(hover が無い)が日々のコストになっている。
  (c) hover/completion が無い状態で外部配布しても魅力が薄く、配布の効果が出ない。
  (d) engine API は headless 実証済み(§2)でリスクが小さく、早期に価値が出る。

**推奨: 機能先行**(WP-M2〜M5 → WP-M6)。`24-plan` §11 の優先順位とも一致する。
ただし WP-M6 は M2〜M5 と独立しているため、外部配布の必要が生じた時点で
前倒しできる(§9)。また build parity の修正(負債 2, 3)だけは、hover/completion の
表示内容(define で変わる型付け結果、診断コード)に直接影響するため、
機能実装の**前**に WP-M1 で済ませる。

### 6.2 AsyncWorker は温存し、request 単位の bridge を新設する

`AsyncWorker` の再設計(instance 化)は multi-root 前提の大工事であり非ゴール。
代わりに LSP handler と engine の間に `EngineRequestBridge` を置く:

- `Begin*`(`QuickTipInfoAsyncRequest` 等)を `TaskCompletionSource` で `Task` 化し、
  既存の response pump(`DispatchResponses` 常時実行)から完了を検知する。
- LSP の `CancellationToken` を `AsyncRequest.Stop` に写像する。AsyncWorker の
  force-out(同種 request の後勝ち破棄)で `MarkAsCompleted` された request は
  「キャンセル済み」として LSP に `RequestCancelled` / null を返し、結果を捏造しない。
- 同期 API(`GetGotoInfo`)は既存の `_engineOperations` lock(WP-L3 で一元化済み)の
  下で実行し、document change / project reload との直列化規則を変えない。
- request 実行時点の document version を記録し、応答時に version が進んでいたら
  結果を破棄して null を返す(diagnostics の世代管理と同じ考え方)。

この bridge を WP-M2(hover)で作り、M3/M4(completion/definition)は同じ境界を
再利用する。これが「hover を最初にやる」実装順の根拠でもある。

### 6.3 hover/completion text は変換層で Markdown 化し、engine の markup を漏らさない

`QuickTipInfo.Text` / `CompletionElem` の文字列は擬似 markup
(`<keyword>`、`<lb/>`、`<hint>`、HTML エスケープ)を含む(§2)。LSP には
`MarkupContent(markdown)` を返し、変換規則を純関数 + unit test で固定する:

- `<lb/>` → 改行、`<keyword>` 等の装飾 tag → 除去(または code span)、
  `&amp;`/`&lt;`/`&gt;` → 復元、シグネチャ部は fenced code block。
- Markdown メタ文字はエスケープし、ソース由来文字列を生で埋め込まない。
- `GetHintContent`(遅延展開 delegate)と `<hint>` の対話的展開は VS2010 の
  UI 機能なので使わない(hover は静的表示)。
- client の `contentFormat` に markdown が無ければ plaintext へ落とす。

### 6.4 document sync は WP-M4 まで full sync を維持し、WP-M5 で incremental に切り替える

relocation(増分)には「どの範囲がどう変わったか」が必要で、LSP の
incremental sync(`TextDocumentSyncKind.Incremental` の range 付き change)が
それをそのまま供給する。一方 hover/completion/definition は sync 方式に依存しない
(現在 text と position だけ使う)。したがって:

- M2〜M4 は full sync のまま実装し、handler は sync 方式を知らない境界にする。
- M5 で incremental sync + `RelocationRequest` 投入 + `BeginUpdateCompileUnit` を導入し、
  構造変更時は engine 自身の判定(`isNeedRebuildTypesTree`)で full rebuild に落とす。
- 切替は server capability の変更だけで extension 側の対応は不要(languageclient が追従)。
- `nemerle.server.incrementalUpdate`(既定 on、名称は実装時に確定)で従来の
  full reload 動作へ戻せる escape hatch を置く。relocation は engine で最も
  実戦投入されていない経路のため、fallback を仕様として持つ。

### 6.5 OmniSharp 0.19.9 を継続し、再評価条件を更新する

2026-07-14 時点の確認: リポジトリは archive されておらず、2026-06 に
LSP 3.18 機能のコミットと v0.19.10 tag(prerelease、NuGet 未公開)がある。
NuGet の最新安定版は 0.19.9(2023-09-21)のまま。0.19.9 は本計画の全機能
(pull diagnostics 型を含む)を持つことを実 assembly 検査で確認済み(§2)。

- 判断: **0.19.9 継続**。本計画に必要な API 欠落は無く、置き換えは純リスク。
- 再評価トリガー(23-log の条件を更新): (a) 必要な LSP 型の欠落が実際に出た、
  (b) セキュリティ問題が出た、(c) NuGet 安定版が止まったまま GitHub 活動も停止した。
  代替候補の現状: `Microsoft.CommonLanguageServerProtocol.Framework` は
  prerelease 1 版のみで Roslyn/Razor 外の採用実績なし。
  `Microsoft.VisualStudio.LanguageServer.Protocol` DTO は 17.2.8(2022-05)で停止。
  activeな軽量代替として `EmmyLua.LanguageServer.Framework`(0.9.x、LSP 3.18、
  zero-dependency)が育っているが小規模・1.0 未満。protocol adapter 境界
  (handler とホストに閉じ込め、engine adapter は OmniSharp 非依存)は維持する。

### 6.6 server のログは `window/logMessage` に移し、stderr は起動失敗専用にする

現状、正常動作の trace(MSBuild query 時間、rebuild 時間、workspace apply)も
`Console.Error` に出るため、languageclient 10.1.0 の executable transport が
Output Channel へ **[error]** として転送する(Project Owner 指摘の紛らわしさ)。
LSP 3.17 の正規の経路に合わせる:

- 情報・計測 trace → `window/logMessage`(`MessageType.Info` / `Log`)。
  languageclient は type に応じて info/log として表示する
  (vscode-languageserver-node `client.ts` の `LogMessageNotification` 処理で確認済み)。
- 回復可能な異常(project query 失敗等)→ `MessageType.Warning` / `Error`。
- `$/logTrace` は使わない。仕様上 protocol trace(`$/setTrace` 制御)専用であり、
  単発のログには `window/logMessage` を使えという注記が仕様にある。
- stderr に残すのは「initialize 前に死ぬ」経路(起動直後の fatal)のみ。
  stdout が JSON-RPC 専用である規則は不変。
- raw LSP integration test は stderr 依存の計測 assert を `logMessage` 受信に置き換える。

### 6.7 Nemerle.Sdk は MSBuild project SDK 方式(build/ 規約ではなく)で作る

公式資料(2026-07-14 確認、§12)に基づく:

- package ルートに `Sdk/Sdk.props` + `Sdk/Sdk.targets` を置く project SDK 方式を採る。
  `build/<id>.targets` 規約は「既存 SDK のビルド構造に追記する」仕組みであり、
  `CoreCompile` を丸ごと差し替える Nemerle には向かない(`.csproj` で csc が勝つ問題
  = `DISTRIBUTION.md` タスク3 と同型の理由)。
- `Sdk.props`/`Sdk.targets` から `Microsoft.NET.Sdk` を明示 import する
  (`Microsoft.Build.NoTargets` 等、microsoft/MSBuildSdks の公式実装パターン)。
  プロジェクトは `<Project Sdk="Nemerle.Sdk/x.y.z">` または `global.json` の
  `msbuild-sdks` でバージョン固定できる(NuGet SDK resolver 公式仕様)。
- task assembly(NccCompile + Hosting)と compiler layout(dist/ncc 相当)は
  package 内の独自フォルダー(`tasks/`、`tools/ncc/`)に置き、
  `UsingTask AssemblyFile="$(MSBuildThisFileDirectory)..."` の相対参照で配線する
  (`AssemblyFile` の相対 path は宣言ファイル基準、公式リファレンス)。
  全て managed .NET assembly なので `dotnet build` の MSBuild(.NET)で実行できる。
- Windows/Linux 2 系統の `Nemerle.Core.targets` は package 内で条件分岐に統合する
  (差分は layout dir 既定・起動形・path 区切りの 3 点のみ、`DISTRIBUTION.md`)。
- `dotnet new` template は公式作法(`PackageType=Template`、`content/`、
  `IncludeBuildOutput=false`)に従い **別 package**(`Nemerle.Templates`)にする。
  SDK package と template package は要求構造が異なる。
- 公開範囲は local feed(`--add-source`)+ 社内的な検証まで。nuget.org 公開は
  WP-N(release 工程)。package ID は既存 PoC(`Nemerle.Ncc.DevTool`)と同様に
  実運用名を占有しない preview 名を使うかを実装時に判断し、log に記録する。

### 6.8 toolchain のバージョン整合を provenance で機械検証可能にする

Nemerle assembly version は `git describe` 由来のため、server / toolchain の世代混在は
`FileLoadException` を生む(`28-log`)。WP-M6 で:

- `pack-tool.ps1` が `dist/ncc/ncc-info.json`(commit / describe / configuration /
  packedAtUtc)を書き、`Nemerle.Sdk` package にも同じ provenance を含める。
- server は起動時に自分の `bundle-info.json` を `window/logMessage`(Info)で出す。
  extension は project の resolved `Nemerle.dll`(snapshot の assembly reference)の
  file version と server 側 version が食い違う場合に warning を表示する。
- compiler 修正(WP-M1 の 1 行)後の Stage リビルド → dist 再 pack → server 再 pack の
  一連が同一 commit で行われたことを、この provenance で確認できるようにする。

## 7. Work packages

### WP-M1: IDE/build parity と server ログの地固め

成果物:

- `Nemerle.Core.targets`(Windows/Linux)と `NccCompile.BuildArgs` が
  `DefineConstants` を `-define:` として ncc に渡す配線。Exec fallback 経路も同様。
- `EngineWorkspaceInputs.FromSnapshot` の DefineConstants 非適用 warning を撤去し、
  snapshot の `DefineConstants` を engine に適用する(build と同一の値を使う)。
- `ncc\parsing\Utility.n` の 1 行修正で警告 N コードを `WarningOccured` イベントから
  観測可能にし、`CompilerHost` → `NccCompile` が `Log.LogWarning` の code 引数に
  `N####` を渡す。LSP diagnostics の `code` field にも配線する。
- server の情報 trace を `window/logMessage`(Info/Log)へ移行(§6.6)。
  stderr は initialize 前 fatal のみ。integration test の計測 assert を移行。
- compiler 変更に伴う Stage1/Stage2 リビルドと dist/server の再 pack、回帰確認。

受け入れ基準:

1. `#if` で分岐する新 fixture(`DefineConstants` に custom define を持つ)が
   `dotnet build`(inproc / `-p:NemerleUseExec=true` の両経路)と IDE diagnostics で
   同じ結果になる。IDE 側は define の有無で diagnostics が変わることを両方向で確認する。
2. `EngineWorkspaceInputs` の unit test が「DefineConstants 適用 + gap warning 消滅」を固定する。
3. 警告を含む fixture の `dotnet build` バイナリログ/コンソールで
   `warning N####:` がコード欄付き構造化警告として出る。LSP publishDiagnostics の
   該当 diagnostic に `code: "N####"` が入る。
4. stage2/stage3 が 0 error でビルドでき、testsuite 全数で新規 regression なし
   (compiler 1 行修正の回帰ゲート)。
5. 正常 session(起動 → project apply → didChange → didClose)で extension の
   Output Channel に `[error]` 行が 0。project query 失敗の fixture では引き続き
   error レベルで表示される。
6. WP-L4 の全自動テスト(unit / integration / test:vsix / test-bundled-server)が PASS。

### WP-M2: hover(+ EngineRequestBridge)

成果物:

- `EngineRequestBridge`(§6.2): `Begin*` → `Task` 化、cancellation、force-out 処理、
  document version 照合。
- markup → `MarkupContent` 変換の純関数(§6.3)+ unit test。
- `HoverHandler`(`textDocument/hover`): `BeginGetQuickTipInfo` 配線、
  `QuickTipInfo.Location` → LSP range 変換。
- extension は変更不要(capability 追従)。README の機能表更新。

受け入れ基準:

1. Sokoban で他 source(closed)宣言の型・method に hover するとシグネチャ text が返る。
   RefDemo で ProjectReference 先の型に hover できる。
2. 未保存 buffer の変更(型を変える編集)後の hover が新しい型を反映する。
3. local value / パラメーター / method / property / 型名の 5 種の期待 text を
   raw LSP integration test で固定する。擬似 markup(`<lb/>` 等)が結果に漏れない。
4. CRLF + non-BMP fixture で hover の range が 0-origin UTF-16 で正しい。
5. 識別子でない位置は null。project reload 直後・最中の hover が deadlock せず
   null / 結果 / cancel のいずれかを返す(integration test で reload と競争させる)。
6. warm 状態(rebuild 完了後)の hover 応答時間を計測し log に記録する
   (目標 p50 < 300 ms。未達なら原因分析を log に残し、基準自体は「計測と記録」)。

### WP-M3: completion

成果物:

- `CompletionHandler`(`textDocument/completion`、triggerCharacters: `"."`)+
  `completionItem/resolve`(`resolveProvider: true`)。
- `Engine.Completion` → `CompletionAsyncRequest` の bridge 配線。
- `CompletionElem`(GlyphType / DisplayName / Info / Overloads)→ `CompletionItem`
  (kind / detail / documentation)の mapping 純関数 + unit test。
  Description(XmlDoc 参照を含む)は高コストなので resolve 側で遅延計算する。
- 直近の completion 結果 1 世代の cache(resolve の `data` index 参照先)。

受け入れ基準:

1. `.` 直後の member completion が receiver の型に応じた member 一覧を返す:
   RefDemo(ProjectReference の `Calc`)、PackageReference(`JsonConvert`)、
   Sokoban(他 source 宣言型)で raw LSP integration test。
2. global scope completion(keyword + 可視型)が空にならず、Nemerle keyword を含む。
3. 未保存 buffer で新たに宣言した symbol が同 buffer の completion に出る。
4. resolve が Overloads 持ち item の Description(複数 overload の列挙)を返し、
   resolve 前の応答には重い documentation が含まれない。
5. glyph → `CompletionItemKind` mapping と markup 除去の unit test。
6. 連続 didChange(高速タイピング相当)と completion の交錯で crash / 古い buffer への
   position ずれ例外が出ない(bridge の version 照合が働く)。
7. warm 状態の completion 応答時間を計測し log に記録する(目標 p50 < 500 ms)。

### WP-M4: definition / references

成果物:

- `DefinitionHandler`(`textDocument/definition`)と `ReferencesHandler`
  (`textDocument/references`): `GetGotoInfo(…, GotoKind.Definition | Usages)` 配線。
- `GotoInfo`(fileIndex ベースの 1-origin `Location`)→ LSP `Location[]` 変換
  (URI 正規化は WP-L3 の `ProjectPathNormalizer` を共用)。
- metadata(外部 assembly)member への definition は空結果 + `logMessage`(Info)。
  生成 source 表示(VS2010 の `GenerateCode` 相当)は非ゴール。

受け入れ基準:

1. Sokoban の `main.n` から `MapCollection` 等、closed source の宣言位置へ definition が返る。
2. 未保存 buffer 内で宣言位置を移動した直後の definition が新位置を指す(buffer 優先)。
3. references が定義 source と使用 source をまたいで全 usage を返す
   (includeDeclaration の扱いを test で固定)。
4. BCL / NuGet 型への definition が空結果で、crash や誤 URI(obj/ 配下等)を返さない。
5. 返る URI が drive letter 大文字・`file:///f%3A/...` 形式で VS Code から開ける
   (Extension Host test で実クリック相当の `vscode.executeDefinitionProvider` を検証)。
6. CRLF + non-BMP fixture で position が正しい。

### WP-M5: incremental rebuild(relocation)と応答性計測

成果物:

- `TextDocumentSyncKind.Incremental` への切替と、range 付き change →
  `RelocationRequest(Begin, Old, New)` 変換(UTF-16 → 1-origin 変換の unit test)。
- didChange 経路の分岐: project source の編集は `BeginUpdateCompileUnit`
  (engine 内で relocation → method 単位再型付け or 構造変化検知で
  `BuildTypesTree`)、project/構造イベントは従来どおり `BeginReloadProject`。
- method 単位診断(`ClearMethodCompilerMessages` / method message 世代)の
  publish 統合(WP-L3 の publisher 世代管理を維持)。
- `nemerle.server.incrementalUpdate` escape hatch(§6.4)。
- 計測: fixture ごとの edit-to-diagnostics(didChange 送信 → publish 受信)を
  incremental 有効/無効で比較し log に記録。

受け入れ基準:

1. method body 内の編集で `BuildTypesTree`(full rebuild)が走らないことを
   trace(logMessage)で確認し、かつ diagnostics が正しい(エラー導入 → 解消)。
2. Sokoban 相当 fixture の edit-to-diagnostics p50 が full reload 方式より短縮され、
   実測値(前後比較)を log に記録する(目標: debounce 込み p50 ≦ 400 ms)。
3. 構造変更(method 追加/削除、using 追加、型追加)が full rebuild に fallback し、
   その後の diagnostics / hover / definition が正しい。
4. WP-L3 の buffer/close/stale/removal/recovery シナリオ一式が incremental 有効で全 PASS。
5. relocation 失敗経路(`RelocationFailedException` → full rebuild)で diagnostics が
   壊れない(経路を強制する test または fault injection)。
6. `nemerle.server.incrementalUpdate=false` で従来動作(全 test PASS)に戻る。
7. hover / completion / definition が incremental 有効時も WP-M2〜M4 の
   integration test を全 PASS する(基盤変更の回帰ゲート)。

### WP-M6: Nemerle.Sdk NuGet package + project template + provenance

成果物:

- `Nemerle.Sdk` package(§6.7): `Sdk/Sdk.props` + `Sdk/Sdk.targets`
  (Microsoft.NET.Sdk 明示 import、Windows/Linux 統合 targets)、`tasks/`、
  `tools/ncc/`(compiler layout + provenance)。pack script は `pack-tool.ps1` を拡張。
- `Nemerle.Templates` package(別 package、`dotnet new nemerle-console` 等)。
- provenance(§6.8): `dist/ncc/ncc-info.json`、package 内 provenance、
  server bundle-info との不一致 warning(extension)。
- README / DISTRIBUTION.md の更新(repo checkout 不要の手順)。
- package の license/notices(bundled Nemerle 群 = BSD-3-Clause 等)と
  `dotnet list package --vulnerable` / pack 時検証。

受け入れ基準:

1. repo 外の空 directory で、local feed だけを使い
   `dotnet new nemerle-console` → `dotnet build` → `dotnet run` が
   repo checkout なしで成功する(Windows)。
2. 同じ project が WSL(Linux)で `dotnet build` → 実行まで成功する(同一 package)。
3. ProjectReference / `NemerleMacroReference` / PackageReference を使う fixture 相当が
   Sdk 参照形式で build できる(既存 samples を Sdk 形式へ複製または変換して検証)。
4. VS Code extension がその Sdk-based project で project-aware diagnostics を表示する
   (MSBuild query が package 内 targets で成立することの実証)。
5. `<Project Sdk="Nemerle.Sdk/x.y.z">` 形式と `global.json` の `msbuild-sdks` 形式の
   両方で build できる。
6. toolchain と server の版不一致を再現する fixture で extension が warning を表示し、
   同一 commit の組では表示しない。
7. `dotnet clean` / incremental build / PDB / `dotnet list package --vulnerable` が
   既存 targets と同水準で機能する(既存受け入れ項目の Sdk 版回帰)。

## 8. テストマトリクス

| 対象 | 確認すること |
|---|---|
| defines fixture(新規) | `#if` 分岐の build/IDE parity、`-define` 配線(WP-M1) |
| warning fixture(新規) | `warning N####` の MSBuild 構造化出力と LSP `code`(WP-M1) |
| `HelloCore` | hover/completion/definition の最小ケース、ログ移行後の [error] 0 行 |
| `RefDemo` | ProjectReference 先の型への hover/completion/definition |
| `samples/PackageReference` | NuGet 型への completion、metadata definition の空結果 |
| `Sokoban` | 複数 source 横断の hover/definition/references、macro 環境での安定性 |
| CRLF + non-BMP fixture | hover range / completion position / definition の UTF-16 変換 |
| 一時 project(WP-L3 方式) | reload と feature request の競争、incremental の stale/fallback |
| Sdk-based project(新規) | repo 外 build/run、extension 経由の diagnostics(WP-M6) |
| WSL | Sdk package の Linux build(WP-M6) |

unit test では少なくとも次を固定する。

- 擬似 markup → Markdown 変換(tag 除去、エスケープ復元、meta 文字エスケープ)。
- glyph → `CompletionItemKind`、`GotoInfo` → `Location` 変換(URI 大小文字、1↔0-origin)。
- incremental change(range + newText)→ `RelocationRequest` 変換
  (CRLF、複数 change、surrogate pair)。
- bridge の version 照合と force-out → cancel の写像。
- N コード抽出(`Utility.n` 修正後のイベント payload)。
- Sdk.props/targets の import 構造(評価だけの MSBuild test: `-getProperty` で
  Microsoft.NET.Sdk 由来 property と Nemerle 由来 property の共存を確認)。

## 9. 実装順序

1. **WP-M1**(parity + ログ)。define と診断コードは hover/completion の表示内容と
   diagnostics の同一性に影響するため、機能より先に正す(誤った基盤の回避)。
   ログ移行は以降の全 WP の計測手段(logMessage ベースの trace)でもある。
2. **WP-M2**(hover)。最小の language feature で `EngineRequestBridge` を確立する。
3. **WP-M3**(completion)。bridge を再利用し、最も複雑な mapping を単独 WP で扱う。
4. **WP-M4**(definition/references)。同期 API の直列化規則を bridge に追加する。
5. **WP-M5**(incremental)。機能が出揃った後に基盤(sync 方式)を差し替えることで、
   M2〜M4 の integration test が incremental 化の回帰ゲートとして機能する。
   逆順(M5 先行)にすると、実戦投入されていない relocation の上に全機能を
   積むことになり、原則に反する。
6. **WP-M6**(Sdk + template + provenance)。M1〜M5 と独立なので、外部配布の
   必要が生じた場合はどの時点にも前倒しできる(§6.1)。

## 10. リスクと対策

| リスク | 対策 |
|---|---|
| compiler 1 行修正(N コード)の波及 | WP-M1 で testsuite 全数を回帰ゲートにし、provenance(§6.8)で server/dist の世代混在を検出する |
| DefineConstants 適用で既存 fixture の診断が変わる | parity fixture で build 側と突き合わせ、既存 fixture の期待値変化は log に記録する |
| relocation が実戦未投入(最大の技術リスク) | WP-M5 を最後に置き M2〜M4 の test を回帰ゲート化、escape hatch 設定、失敗時 full rebuild fallback を仕様化 |
| AsyncWorker force-out による request 取りこぼし | bridge が force-out を cancel として扱い、結果を捏造しない(§6.2)。integration test で reload と競争させる |
| completion の応答が全 rebuild と衝突して遅い | warm/cold を分けて計測・記録し、閾値未達は WP-M5 の優先度根拠にする |
| 擬似 markup の変換漏れ(表示崩れ) | 変換を純関数化して unit test で固定、integration test で生 tag 非混入を assert |
| OmniSharp 0.19.9 の停滞 | 全機能の API 実在を確認済み(§2)。adapter 境界維持と再評価条件の更新(§6.5) |
| Sdk package の import 順序 / csc CoreCompile 競合 | project SDK 方式 + Microsoft.NET.Sdk 明示 import(公式パターン §6.7)、評価 unit test(§8) |
| Sdk 化で pack script が複雑化し再現性を失う | pack-tool.ps1 の単一入口を維持し、CI command 一覧(28-log 方式)を log に記録する |
| logMessage 移行で既存 test / トラブルシュートが壊れる | stderr → logMessage の移行を WP-M1 で一括実施し、README の troubleshooting を同時更新する |

## 11. WP-M 完了後の優先順位

1. semantic tokens(`ScanLexer` ベース。macro が拡張する keyword の動的彩色が
   TextMate では原理的に不可能なため、価値は明確)
2. signatureHelp(`BeginGetMethodTipInfo`、bridge 流用で小さい)
3. documentHighlight(`GetGotoInfo(UsagesInCurrentFile)` 流用)
4. formatting(`Formatter` の検証コストが大きいため単独 WP)
5. Linux 実地検証 + Marketplace/CI release(WP-N: 署名、Linux 用 test 経路の
   cross-platform 化、`test:vsix` PID 検査の powershell.exe 依存解消を含む)
6. multi-root / 複数 project(project ごとの server process 分離を優先比較)
7. rename / codeAction(`Project.Refactoring.n` / `BeginFindUnimplementedMembers`)
8. pull diagnostics への移行検討(client 側の制御が必要になった場合のみ)

## 12. 参照文書

Repository 内:

- `24-vscode-development-plan.md`: WP-L 計画(§11 の優先順位リストが本計画の出発点)。
- `25-vscode-extension-log.md` / `26-vscode-project-info-log.md` /
  `27-vscode-project-workspace-log.md` / `28-vscode-packaging-log.md`: WP-L1〜L4 の実装結果。
- `21-lsp-feasibility.md` / `22-lsp-step1-log.md` / `23-lsp-step2-log.md`:
  engine API の実証と OmniSharp 採用理由。
- `20-inproc-task-plan.md` / `20-inproc-task-log.md`: NccCompile / Hosting(WP-M1 の対象)。
- `DISTRIBUTION.md`: toolchain 配布の現状(WP-M6 の出発点)。

外部仕様(いずれも 2026-07-14 に一次資料を確認):

- LSP 3.17 specification(hover / completion / definition / semanticTokens /
  formatting / pull diagnostics / `window/logMessage` / `$/logTrace` / workDoneProgress):
  <https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/>
- vscode-languageserver-node client の logMessage / trace 表示挙動(実装ソース):
  <https://github.com/microsoft/vscode-languageserver-node/blob/main/client/src/common/client.ts>
- MSBuild project SDK の参照と解決(`Sdk` 属性、`global.json` の `msbuild-sdks`):
  <https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-use-project-sdk>
- .NET project SDK overview(加算的 SDK の公式例):
  <https://learn.microsoft.com/en-us/dotnet/core/project-sdk/overview>
- NuGet の props/targets 規約(build/ 系と SDK 方式の違い):
  <https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets>
- UsingTask リファレンス(`AssemblyFile` 相対 path、Runtime):
  <https://learn.microsoft.com/en-us/visualstudio/msbuild/usingtask-element-msbuild>
- PackageType(MSBuildSdk / Template):
  <https://learn.microsoft.com/en-us/nuget/create-packages/set-package-type>
- dotnet new custom templates:
  <https://learn.microsoft.com/en-us/dotnet/core/tools/custom-templates>
- microsoft/MSBuildSdks(NoTargets / Traversal、custom SDK の公式実装例):
  <https://github.com/microsoft/MSBuildSdks>
- OmniSharp/csharp-language-server-protocol(現状確認: NuGet 安定版 0.19.9、
  2026-06 に 3.18 コミットと v0.19.10 prerelease tag):
  <https://github.com/OmniSharp/csharp-language-server-protocol>
