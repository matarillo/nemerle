# 24. WP-L: .NET 10 Nemerle 開発環境(VS Code + project-aware LSP)計画

作成日: 2026-07-13

## 1. この文書を `00-PLAN.md` から分離する理由

`00-PLAN.md` の主目的は、CLR4 上の Nemerle コンパイラーを .NET 10 へ移植し、
セルフホスト、テスト、配布可能なコンパイラーツールチェーンまで到達することである。
その目的は stage2/stage3、`dotnet build` 統合、in-process MSBuild task、最小 LSP server まで
進み、当初の「コンパイラーを先に移植する」段階を越えている。

次に扱う VS Code extension、workspace/project model、LSP lifecycle、editor packaging は、
コンパイラー移植とは異なる成果物、依存関係、受け入れ基準を持つ。詳細を 500 行を越えた
`00-PLAN.md` の作業ログへ追加すると、原計画の完了条件と今後の editor roadmap が混ざり、
どこまでが必須か判断しにくくなる。

したがって、次の方針を採る。

- 本文書を .NET 10 Nemerle 開発環境の実行計画とする。
- `00-PLAN.md` には最新到達点と本文書へのリンクだけを追記する。
- 各 work package の実装結果は、従来どおり番号付き log 文書へ記録する。

## 2. 現在地

すでに利用できるもの:

- .NET 10 上で動く self-hosted ncc と、stage2/stage3 の再現可能なビルド。
- `pack-tool.ps1` が生成する `dotnet-port/dist/ncc` 実行レイアウト。
- SDK-style `.nproj`、`Nemerle.Core.targets`、in-process `NccCompile` task。
- ProjectReference と macro-only `NemerleMacroReference` のビルド経路。
- Portable PDB、構造化 MSBuild diagnostics、`dotnet clean`、Exec fallback。
- headless IDE engine と、open/change/close + publishDiagnostics を実装した
  `net10.0` stdio LSP server。
- 未保存 buffer の型エラーが診断へ反映される raw LSP integration test。
- WP-L1 の VS Code extension shell（syntax/editing、stdio client、trust gate、development VSIX）。
- WP-L2 の MSBuild JSON project-information provider、独立 snapshot、project discovery/selection、
  cache/single-flight/timeout/cancellation、status/reload UI。

進捗更新 (2026-07-13): WP-L1 と WP-L2 は完了。実装・検証結果は
`25-vscode-extension-log.md` と `26-vscode-project-info-log.md` を参照。

進捗更新 (2026-07-14): WP-L3 完了。WP-L2 snapshot は engine workspace に適用され、
project 全 source（open buffer 優先、closed source は disk-backed）と resolved
reference / macro reference を使う project-aware diagnostics が動く。
実装・検証結果は `27-vscode-project-workspace-log.md` を参照。

進捗更新 (2026-07-14): WP-L4 完了。WP-L の全 work package が完了した。
extension 0.4.0 は language server を VSIX の `server/` に同梱し（`pack-server.ps1`）、
既定で bundled server を起動、`dotnet --list-runtimes` による .NET 10 runtime 検査、
隔離 install の clean-machine 相当自動テスト、third-party notices の機械検証を持つ。
実装・検証結果は `28-vscode-packaging-log.md` を参照。

## 3. ゴール

Windows 11、.NET 10 SDK、VS Code がある環境で、repository 内の SDK-style Nemerle project を
次の一連の流れで開発できるようにする。

1. VS Code で `.n` と `.nproj` を含む workspace を開く。
2. Nemerle language server が自動起動する。
3. `.n` に基本的な syntax highlighting と editing support が適用される。
4. project 全 source と解決済み参照を使った diagnostics が Problems/squiggle に表示される。
5. 未保存 buffer を変更すると、その内容を使って diagnostics が更新される。
6. `dotnet build <project.nproj>` が LSP と同じ project inputs を使って成功する。

この WP の「開発できる」は diagnostics と build/run がつながった状態を意味する。
completion、hover、definition、formatting はこの基盤の後に追加する。

## 4. 非ゴール

最初の WP-L には次を含めない。

- Visual Studio 2010 integration の置き換え。
- Marketplace への公開、署名済み正式 release、.NET runtime の自動 install。
- Linux/macOS での VS Code extension 実地保証。
- multi-root workspace と複数 `IIdeEngine` の並列実行。
- semantic tokens、completion、hover、definition、rename、formatting。
- `.nproj` を使わない大規模な loose-file workspace。
- 毎回の full project rebuild を relocation-based incremental analysis へ置き換えること。

ただし、後続機能を妨げない境界と計測点はこの WP で設ける。

## 5. 全体構成

```text
VS Code
  └─ vscode-nemerle extension (TypeScript)
       ├─ language / TextMate grammar / language configuration
       └─ vscode-languageclient
            │ stdio LSP
            ▼
       Nemerle.LanguageServer (.NET 10)
            ├─ WorkspaceManager
            ├─ ProjectInfoProvider
            │    └─ dotnet msbuild <project.nproj>
            │         ResolveReferences + JSON project information
            └─ NemerleProject / IIdeEngine
                 ├─ open documents: editor buffer
                 ├─ closed project documents: disk text
                 ├─ assembly references
                 └─ macro references
```

LSP server の stdout は JSON-RPC framing 専用とし、engine/server log は stderr または
LSP log channel へ送る。VS Code extension は stderr を Output Channel に表示する。

## 6. 主要な設計判断

### 6.1 VS Code extension は薄く保つ

extension は process 起動、configuration、workspace trust、declarative language support に限定する。
Nemerle の解析ロジックを TypeScript 側へ複製しない。diagnostics と後続の language features は
すべて .NET language server が所有する。

### 6.2 project file を独自に XML 解釈しない

`.nproj` は SDK imports、conditions、configuration、ProjectReference、PackageReference を含む。
XML を直接読む実装では MSBuild の評価結果と容易に食い違う。また LSP process 内で
`Microsoft.Build.dll` を直接 load すると、VS Code が選んだ SDK/MSBuild と package version の
整合、AssemblyLoadContext、node reuse を新たに管理する必要がある。

初期実装では外部の `dotnet msbuild` を project model の正とする。MSBuild 17.8 以降の
`-getItem` / `-getProperty` JSON 出力を利用し、`ResolveReferences` 後に次を取得する。

Items:

- `NemerleCompile`
- `ReferencePath` (`FrameworkReferenceName` が空の user/project/package references だけを採用)
- `NemerleMacroReference`

Properties:

- `MSBuildProjectFullPath`
- `MSBuildProjectDirectory`
- `TargetFramework`
- `Configuration`
- `Platform`
- `DefineConstants`
- `NemerleAdditionalOptions`

概念上の呼び出しは次の形とする。実装時に .NET 10 SDK の正確な option spelling と
stdout の JSON-only 条件を integration test で固定する。

```powershell
dotnet msbuild Project.nproj -nologo -verbosity:quiet `
  -target:ResolveReferences `
  -getItem:NemerleCompile,ReferencePath,NemerleMacroReference `
  -getProperty:MSBuildProjectFullPath,MSBuildProjectDirectory,TargetFramework,Configuration,Platform,DefineConstants,NemerleAdditionalOptions
```

project evaluation は server 起動時、`.nproj`/imported targets/assets の変更時、明示 reload 時だけ
実行し、文書編集ごとには実行しない。

### 6.3 最初は一 process・一 project/engine

`AsyncWorker` は process-wide state と response queue を持つ。複数 engine を同時に走らせる前に
隔離方法を確定する必要があるため、最初は次の規則にする。

- workspace 内に `.nproj` が一つなら自動選択する。
- 複数ある場合は、active document に最も近い project を選択するのではなく、設定または
  command で一つを明示選択する。
- 選択変更時は旧 engine を dispose し、単一 engine を再作成する。
- multi-project graph は root project の ProjectReference outputs と compile inputs を通じて扱う。

将来 multi-root を実装する際は、engine process 自体を project ごとに分ける案を優先して比較する。

### 6.4 buffer が disk より常に優先する

project load では全 `NemerleCompile` source を読み込む。document が open なら LSP buffer、closed なら
disk text を使う。didClose 後は source を project から削除せず、project item なら disk text に戻す。
project item ではない loose file だけを didClose 時に削除する。

diagnostics は project source 全体について保持する。通知済み URI と version を追跡し、source 削除、
project reload、didClose の際に古い diagnostics を明示的に空にする。

### 6.5 untrusted workspace では code を実行しない

project evaluation は imported targets を読み、Nemerle macro は compile-time code を実行する。
VS Code workspace trust が無い状態では、server の自動起動、MSBuild project evaluation、macro load、
build command を行わない。extension は restricted mode を表示し、trust 後に明示的に開始する。

## 7. Work packages

### WP-L1: VS Code extension shell

成果物:

- `dotnet-port/vscode-nemerle/`
- `package.json` の language、grammar、configuration、command、configuration contributions。
- `.n` 用の最小 TextMate grammar。
- `language-configuration.json`。
- `vscode-languageclient` による stdio server launcher。
- `nemerle.server.path`、`nemerle.server.trace`、`nemerle.project` 設定。
- `Nemerle: Restart Language Server`、`Nemerle: Show Output` command。
- npm dependency の lockfile と license/notice 確認。

最初の grammar は手書きし、出典不明な legacy/editor grammar をコピーしない。対象は keyword、comment、
string/char、number、operator、type-like identifier、quotation/splice の基本形に絞る。

受け入れ基準:

1. development host と VSIX install の両方で `.n` が Nemerle と認識される。
2. comment toggle、bracket matching、auto-closing、基本 indentation が動く。
3. extension が server を一度だけ起動し、停止/restart できる。
4. `Broken.n` の未保存型エラーが表示され、修正後に消える。
5. stdout に log が混入せず、stderr が Output Channel に現れる。
6. untrusted workspace では server を起動しない。

### WP-L2: project information provider

状態: **完了 (2026-07-13)**。受け入れ基準 1〜5 を実プロセス/fixture で確認済み。
snapshot は WP-L3 まで engine 非適用。詳細は `26-vscode-project-info-log.md`。

成果物:

- MSBuild query process runner と JSON model/parser。
- project discovery/selection service。
- project load/reload command と状態表示。
- source/reference/macro/define を表す OmniSharp 非依存の project snapshot。
- cancellation、timeout、stderr capture、失敗時の user-facing diagnostic。

実装規則:

- `dotnet` executable は extension/server configuration で上書き可能にする。
- query は同時に一つだけ実行し、同じ project/configuration の結果を cache する。
- 連続 file events は debounce する。
- timeout/exit code/invalid JSON を区別して報告する。
- `ReferencePath` の framework reference facade は、build targets と同じ metadata 条件で除外する。
- `NemerleAdditionalOptions` は初期対応する option を明示し、解析できない semantic-affecting option を
  黙って無視せず log/diagnostic に残す。

受け入れ基準:

1. `HelloCore` の source と configuration を取得できる。
2. `RefDemo` の ProjectReference output が assembly reference に入る。
3. 新規 PackageReference fixture の resolved assembly が reference に入る。
4. `Sokoban` または `CompTimeSolver` の macro-only output が macro reference にだけ入り、
   assembly reference には混入しない。
5. project evaluation failure が server crash や無限 restart にならない。

### WP-L3: project-aware engine workspace

状態: **完了 (2026-07-14)**。受け入れ基準 1〜7 を raw LSP / Extension Host の実プロセスで
確認済み。詳細は `27-vscode-project-workspace-log.md`。

成果物:

- `WorkspaceManager` と project snapshot → `IIdeProject` adapter の更新経路。
- closed source の disk-backed text と open buffer override。
- `.n`、`.nproj`、imported targets、NuGet assets の watch/reload。
- project reload と document change の直列化。
- diagnostics generation/version による stale result 抑止。

初期性能方針:

- full document sync は維持する。
- edit は 250–400 ms debounce し、reload を重複実行しない。
- project evaluation と engine rebuild の時間を別々に計測して trace log に出す。
- correctness を保った状態で sample project の edit-to-diagnostics latency を記録し、
  relocation-based incremental update は計測後の独立 WP とする。

受け入れ基準:

1. 同じ project の別 source で宣言した型を解決できる。
2. open buffer が disk content より優先される。
3. didClose した project source は disk content に戻り、project から消えない。
4. ProjectReference/PackageReference/macro reference を使う sample で false unbound diagnostics がない。
5. version N+1 の変更後に version N の diagnostics を通知しない。
6. project から削除した source の diagnostics を空にする。
7. project reload 中の didChange/didClose で deadlock/crash しない。

### WP-L4: packaging と end-to-end test

状態: **完了 (2026-07-14)**。受け入れ基準 1〜5 を自動テストで確認済み
(clean-machine 相当の隔離 VSIX install、VSIX 抽出 server での raw LSP 全シナリオを含む)。
詳細は `28-vscode-packaging-log.md`。

成果物:

- server build output を VSIX の `server/` に配置する再現可能な script。
- extension が bundled server を既定で使い、開発時だけ override path を使う構成。
- VS Code extension integration test fixture。
- CI から実行可能な build/test/package command。
- developer preview の install/run/troubleshooting 文書。

VSIX は language server と必要な managed dependencies を含むが、.NET 10 runtime は含めない。
起動前に `dotnet --list-runtimes` 相当を確認し、不足時は理解可能なエラーを表示する。

初期 developer preview は repository 内の `pack-tool.ps1`/`Nemerle.Core.targets` を前提にする。
repository 外の project を一行の SDK/package reference だけでビルドできる `Nemerle.Sdk` NuGet package は
価値が高いが、targets の import order、compiler layout、versioning を独立に設計すべきなので次 WP とする。

受け入れ基準:

1. clean extension build から VSIX を生成できる。
2. VSIX install 後、repository の追加 checkout なしで bundled LSP server が起動する。
3. `HelloCore`、`RefDemo`、macro sample、PackageReference fixture の automated smoke test が通る。
4. server buildと既存 raw LSP integration test が 0 warning / 0 error で通る。
5. `dotnet list package --vulnerable --include-transitive` と npm audit の結果を記録する。

## 8. テストマトリクス

| 対象 | 確認すること |
|---|---|
| `HelloCore` | 単一 source、build/run、syntax/type diagnostics |
| `LspServer.IntegrationTest` | raw initialize/open/change/close/shutdown、未保存 buffer |
| `RefDemo` | 複数 project、通常 ProjectReference |
| 新規 PackageReference fixture | NuGet restore と resolved ReferencePath |
| `Sokoban` | 複数 source、macro-only ProjectReference |
| `CompTimeSolver` | compile-time macro success/failure diagnostics |
| CRLF + non-BMP fixture | LSP UTF-16 position、Windows path/URI normalization |
| project reload fixture | source add/remove、`.nproj` 編集、stale diagnostics clear |

unit test では少なくとも次を固定する。

- MSBuild JSON の正常/空/欠落 metadata/invalid JSON。
- Windows drive letter と URI の大小文字差。
- CRLF、LF、末尾改行、surrogate pair を含む offset/line/column 変換。
- diagnostics の 1-origin → 0-origin 変換と不正 end range の補正。
- project reload generation と document version の stale 判定。

## 9. 実装順序

1. WP-L1 で current loose-file LSP を実際の VS Code から使えるようにする。
2. WP-L2 で build と同じ MSBuild project information を取得する。
3. WP-L3 で project 全体を engine に載せ、正しい diagnostics を完成させる。
4. WP-L4 で bundled VSIX、automation、developer preview 文書を整える。
5. 実測した latency と engine API をもとに incremental diagnostics を別計画にする。
6. project-aware diagnostics が安定してから hover → completion → definition の順で追加する。

この順序により、L1 で早期に editor/server 接続問題を露出させつつ、completion などを誤った
project context の上へ実装することを避ける。

## 10. リスクと対策

| リスク | 対策 |
|---|---|
| `AsyncWorker` が process-wide | WP-L は単一 engine。multi-project 並列化を行わない |
| MSBuild query が遅い | edit ごとに実行せず cache/debounce、時間を計測 |
| `ResolveReferences` が project buildを伴う | workspace trust 必須、明示 reload、stderr/timeout を可視化 |
| macro が任意 code を実行する | untrusted workspace では server/evaluation/macro load を禁止 |
| project options と LSP options の乖離 | MSBuild evaluation を唯一の正とし、未対応 option を報告 |
| full rebuild の latency | generation で stale 抑止、debounce、計測後に incremental 化 |
| OmniSharp 0.19.9 の停滞 | protocol adapter 境界を維持し、必要機能欠落時に再評価 |
| VSIX と build toolchain の version ずれ | build script で同一 commit の server/layout を package、version を表示 |
| Windows URI/path 差 | file URI normalization と CRLF/drive-case integration test |

## 11. WP-L 完了後の優先順位

1. hover
2. completion
3. definition
4. incremental diagnostics / projectごとの server process
5. semantic tokens
6. formatting
7. `Nemerle.Sdk` NuGet package と project template
8. Linux VS Code/targets の実地検証
9. Marketplace/CI release

機能数より先に project context、diagnostics correctness、配布再現性を完成させる。

## 12. 参照文書

Repository 内:

- `20-inproc-task-plan.md` / `20-inproc-task-log.md`: SDK-style MSBuild の project/reference 経路。
- `21-lsp-feasibility.md`: IDE engine の切り出し範囲と language feature の推奨順序。
- `22-lsp-step1-log.md`: headless Compiler.Utils/ConsoleTest の結果。
- `23-lsp-step2-log.md`: 現在の最小 LSP server、protocol 選定、既知の制約。
- `DISTRIBUTION.md`: compiler layout、dotnet tool、targets、既存 packaging の状態。

外部仕様:

- MSBuild item/property query:
  <https://learn.microsoft.com/visualstudio/msbuild/evaluate-items-and-properties>
- VS Code Language Server Extension Guide:
  <https://code.visualstudio.com/api/language-extensions/language-server-extension-guide>
- VS Code Syntax Highlight Guide:
  <https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide>
- VS Code Workspace Trust:
  <https://code.visualstudio.com/docs/editor/workspace-trust>
