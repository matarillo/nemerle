# Nemerle コンパイラー (ncc.exe) の dotnet 移植計画

作成日: 2026-07-11 / 対象リポジトリ: rsdn/nemerle (master)

## ゴール

セルフホストされた Nemerle コンパイラー `ncc.exe`(.NET Framework 4.0 / CLR4 用)を
モダン .NET(dotnet)上で動作させ、dotnet 用アセンブリを出力できるようにする。

**ターゲットランタイムは .NET 10 を選定**(理由は後述)。
MSBuild タスク / Visual Studio 統合は本計画のスコープ外(ncc.exe が先)。

## 環境の前提

- Windows 11、dotnet SDK 8.0 / 9.0 / 10.0 インストール済み
- .NET Framework 4.8 は **ランタイムのみ**(SDK / Targeting Pack / Windows SDK なし
  → AL.exe, gacutil, peverify, 参照アセンブリは使えない。ビルドは GAC フォールバックで可)
- ブートストラップバイナリー: `boot-4.0\`(ncc.exe, Nemerle.dll, Nemerle.Compiler.dll, Nemerle.Macros.dll)
- 既知の良好なリリースバイナリー: `bin\NemerleBinaries-net-4.0-v1.2.547.0\`

## アーキテクチャ上の核心的問題

ncc は Roslyn のようなメタデータベースのコンパイラーではなく、
**実行中のランタイムに寄生する** 設計:

1. **コード生成**: System.Reflection.Emit(SRE)でその場に TypeBuilder/ILGenerator で
   アセンブリを構築し、`AssemblyBuilder.Save()` でディスクに保存する
   (`ncc/generation/HierarchyEmitter.n`, `ILEmitter.n`)。
   → 出力アセンブリの参照(mscorlib 等)は **ncc プロセスが動いているランタイム** に従う。
2. **参照読み取り**: 参照アセンブリを `Assembly.LoadFrom` 等で実プロセスにロードし、
   リフレクション(System.Type)でメタデータを読む(`ncc/external/`)。
3. **マクロ**: マクロアセンブリをコンパイラープロセスにロードして **実行** する
   (メタデータ読み取りだけでは済まない)。
4. **セルフホスト**: コンパイラー自身が Nemerle で書かれており、ncc でしかコンパイルできない。

この結果、「net4 の ncc で net10 用アセンブリを出す」ことは原理的にできない。
**コンパイラーを dotnet 上で動かすこと自体が、dotnet 用出力を得る手段** になる。

## ターゲット選定: .NET 10(.NET 8 ではなく)

- .NET 9 で `PersistedAssemblyBuilder`(System.Reflection.Emit の保存可能版)が復活。
  `AssemblyBuilder` のサブクラスなので、ModuleBuilder/TypeBuilder/ILGenerator を使う
  既存バックエンドが **ほぼ無改造で流用できる**。
- .NET 10 では PDB(シーケンスポイント)サポートも追加されている(要検証 → 03 ドキュメント)。
- .NET 8 のみを対象にすると Mono.Cecil / System.Reflection.Metadata への
  バックエンド全面書き換えが必要になり、工数が桁違いに増える。

## ブートストラップ計画(段階方式)

```
[boot-4.0 ncc.exe]  (CLR4 / .NET Framework 4.8 上で実行)
      │  現行ソース + 移植パッチ をコンパイル
      ▼
[stage1: ncc.exe + Nemerle.dll + Nemerle.Compiler.dll + Nemerle.Macros.dll]
      │  net4 フレーバーのアセンブリ(mscorlib 参照)だが、
      │  emission 層はデュアルパス(CLR4: 従来 Save / CoreCLR: PersistedAssemblyBuilder)
      │
      │  runtimeconfig.json を与えて dotnet 10 上で実行
      │  (net4 アセンブリは mscorlib ファサードの型転送で CoreCLR 上でも動く)
      ▼
[stage1 on dotnet 10]  ← ここで初めて「dotnet 上で動く Nemerle コンパイラー」が誕生
      │  同じソースを再コンパイル(参照解決先が CoreCLR になる)
      ▼
[stage2: core フレーバーの ncc + ライブラリ群]  (真の dotnet アセンブリ)
      │  自分自身をもう一度コンパイル
      ▼
[stage3] == stage2 とバイナリー一致(フィックスポイント)を確認 + testsuite 実行
```

### フェーズ分割

| フェーズ | 内容 | 検証方法 |
|---|---|---|
| 0 | boot-4.0 でのベースラインビルドを現代の環境で通す(AL.exe スキップ等) | `DevBuildQuickNccOnly` 成功 |
| 1 | 分析: Framework 専用 API 棚卸し / ビルドフロー解明 / ランタイム事実確認 | dotnet-port/01〜03 ドキュメント |
| 2 | 本計画の確定 | このドキュメント |
| 3 | emission 層のデュアルパス化パッチ(下記)を boot ncc でコンパイル → stage1 | stage1 が CLR4 上で従来どおり動く |
| 4 | stage1 を dotnet 10 で起動し hello.n をコンパイル・実行 | **最初のマイルストーン (M1)** |
| 5 | stage1(on dotnet)でライブラリ+コンパイラーを再コンパイル → stage2、フィックスポイント確認 | stage3 一致 + testsuite |
| 6 | 仕上げ: PDB、リソース、署名、`dotnet tool` 化、SDK スタイルの新ビルド | 配布可能なツールチェーン |

### フェーズ3の技術方針(デュアルパス emission)

パッチは boot-4.0 の ncc(= net4 コンパイラー)でコンパイルできる必要があるため、
**PersistedAssemblyBuilder を型として直接参照できない**(net4 に存在しない)。
→ 生成と保存の分岐点だけをリフレクション(late-binding)で書く:

- 分岐点は少ない:
  - `AppDomain.CurrentDomain.DefineDynamicAssembly(..., Save, dir)`
    → CoreCLR では `new PersistedAssemblyBuilder(name, coreAssembly)` を
      `Type.GetType("System.Reflection.Emit.PersistedAssemblyBuilder, System.Reflection.Emit")`
      経由で生成(戻り値は AssemblyBuilder として扱えるので以降は共通コード)
  - `_assembly_builder.Save(...)` → リフレクションで `Save(string)` を呼ぶ
  - `SetEntryPoint` / `DefineVersionInfoResource` / `AddResourceFile` / `GetSymWriter`
    → CoreCLR パスでは初期は無効化(M1 では不要)、後続フェーズで
      `GenerateMetadata` + `PEBuilder` の recipe に置換(exe エントリポイントは M1 で必要
      → PEBuilder recipe を C# 補助アセンブリ(net10 でビルドした Nemerle.Compiler.Sre.dll 等)
      に切り出し、CoreCLR 上でのみリフレクションでロードする案が有力)
- ランタイム判定: `System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription`
  または `Type.GetType("System.Reflection.Emit.PersistedAssemblyBuilder, ...") != null`

### 既知のリスク / 未確定事項(フェーズ1で確定させる)

- net4 アセンブリ(stage1)が dotnet 10 で本当に起動するか(mscorlib ファサード転送の網羅性)
- PersistedAssemblyBuilder で exe のエントリポイント/Win32 リソースをどう出すか
- `ncc/external` の Assembly.LoadFrom が CoreCLR の ALC でそのまま動くか
- マクロ(net4 ビルドの Nemerle.Macros.dll)を CoreCLR にロードして実行できるか
- 強名署名(Nemerle.snk): CoreCLR は署名生成不可 → 公開鍵のみ埋める(delay-sign 相当)か署名廃止
- multi-module / x86 固有(ncc32/ncc64)は廃止方向

## 作業パッケージ一覧

WP(Work Package)は作業単位。完了時に下の「作業ログ」へ「WP-X 完了」と記録する。
「フェーズ」列は上記「ブートストラップ計画」のフェーズ分割表(フェーズ0〜6)との対応。
WP-A2・WP-A3・WP-K・WP-L はブートストラップ計画(フェーズ0〜6完了 = WP-I2 で配布可能な
ツールチェーンが揃った時点)達成後に発生した、計画のスコープ外の作業パッケージ。

> **命名注意**: WP-A2/WP-A3 の「A」は WP-A(フェーズ3、ExternalTypeInfo修正)の続きではない。
> 「dotnet ネイティブ化 レベル A(= フロントエンド)」という別系列の通し番号で、
> WP-A2(rsp撤廃・参照配線・PDB/clean・dotnet tool更新・Linux版等)の残項目のうち
> インプロセスホスト化が独立タスクとして切り出されたものが WP-A3。内容的には無関係だが
> 接頭辞が衝突しており紛らわしい(以後の類似作業は別系列名を検討すること)。

| WP | 内容 | フェーズ | 状態 | 詳細ログ |
|---|---|---|---|---|
| WP-A | `ExternalTypeInfo` のメンバー取り込みフィルタ(モダン BCL のref return/byref-like構造体をスキップ) | 3 | 完了(2026-07-11) | 10-metadata-import-log.md |
| WP-B | emission 層のデュアルパス化(CLR4 / CoreCLR) | 3 → 4(**M1 達成**) | 完了(2026-07-12) | 11-emission-log.md |
| WP-C | SELF-HOST ブロッカー3件の解消(StrongNameKeyPair除去、codedom除外等) | 5(準備) | 完了(2026-07-12) | 12-selfhost-blockers-log.md |
| WP-D | stage2 セルフホスト on CoreCLR | 5(**M2 達成**) | 完了(2026-07-12) | 13-stage2-log.md |
| WP-E | `-debug` の PDB 出力を CoreCLR パスに配線 | 6 | 完了(2026-07-12) | 14-pdb-log.md |
| WP-F | `-res`(Win32)/`-linkres`/`/doc:` の CoreCLR 対応 | 6 | 完了(2026-07-12) | 17-resources-fixes-log.md |
| WP-G | attributes-01(CustomAttributeBuilder)の修正 | 6 | 完了(2026-07-12) | 17-resources-fixes-log.md |
| WP-H | stage2/stage3 の非決定性(MacroClassGen のtmpname)修正 | 6 | 完了(2026-07-12) | 17-resources-fixes-log.md |
| WP-I1 | Nemerle.Compiler.Test.exe の core 対応 + testsuite 全数実行 | 5(検証) | 完了(2026-07-12) | 18-testsuite-log.md |
| WP-I2 | 配布形態の整備(pack-tool.ps1 / Nemerle.Tool / MSBuild targets) | 6 | 完了(2026-07-12) | DISTRIBUTION.md |
| WP-J | overloading-01 regression 修正 + string-template-3 診断・修正 | 5(検証中に発見した回帰の修正) | 完了(2026-07-12) | 19-overload-stringtemplate-log.md |
| WP-A2 | dotnet ネイティブ化「レベル A」: 参照自動解決・rsp撤廃(`LoadCoreStdlibReferences`)、`@(ReferencePath)`→`-ref:`配線、`-debug`/PDB・`dotnet clean`対応、dotnet tool rspフリー化、Linux版targets | —(計画後) | 完了(2026-07-13) | DISTRIBUTION.md |
| WP-A3 | インプロセス MSBuild タスク(`Nemerle.Compiler.Hosting` / `Nemerle.MSBuild.Tasks`)— レベル A の残項目を分離・完遂 | —(計画後) | 完了(2026-07-13) | 20-inproc-task-plan.md / 20-inproc-task-log.md |
| WP-K | LSP feasibility(headless IDE engine + 最小 stdio LSP server) | —(計画後) | 完了(2026-07-13) | 21-lsp-feasibility.md / 22-lsp-step1-log.md / 23-lsp-step2-log.md |
| WP-L | VS Code extension + project-aware LSP(コンパイラー移植後の次期作業) | —(計画後) | 完了(WP-L1〜L4、2026-07-14) | 24-vscode-development-plan.md / 25-vscode-extension-log.md / 26-vscode-project-info-log.md / 27-vscode-project-workspace-log.md / 28-vscode-packaging-log.md |
| WP-M | 開発環境2: language features(hover/completion/definition)+ incremental rebuild + Nemerle.Sdk NuGet 化 | —(計画後) | **WP-M1〜M6 完了(2026-07-15)= WP-M 完了** | 29-devenv2-plan.md / 30-devenv2-wp-m1-log.md / 31-devenv2-wp-m2-log.md / 32-devenv2-wp-m3-log.md / 33-devenv2-wp-m4-log.md / 34-devenv2-wp-m5-log.md / 35-devenv2-wp-m6-log.md |
| WP-N | 公開前の品質固めと既知制約の解消(ビルド再現性・engine 品質・Nemerle.Linq/testsuite・Linux 実地・版タグ契約 + GitHub Release・版ピン留め・最小 CI) | —(計画後) | **完了(2026-07-22)**(N1〜N5 完了、N7 = 部分 GO で case 1 実装済み、N6 = CI/release workflow 稼働) | 36-prerelease-quality-plan.md / 37-prerelease-wp-n1-log.md / 38-prerelease-wp-n2-log.md / 39-prerelease-wp-n2-log.md / 40-prerelease-wp-n3-log.md / 41-prerelease-wp-n4-log.md / 42-prerelease-wp-n5-log.md / 43-boot-net10-log.md / 44-prerelease-wp-n7-log.md / 45-boot-4.0-refresh-log.md / 46-prerelease-wp-n6-log.md |
| WP-O | 保存・配布フェーズ(入口の現代化・保存/再現性の確定・runtime package 根治・配布の器の判断・showcase) | —(計画後) | **完了(2026-07-26)**(O1〜O4 完了 2026-07-23、O5a = semantic tokens 完了 2026-07-25、O5b = 試遊サンプルの受け入れ基準を充足 2026-07-26。以後のサンプル追加は CI ゲートが基準を維持) | 47-wp-o-plan.md / 48-preservation-wp-o1-log.md / 49-preservation-wp-o2-log.md / 50-wp-o3-log.md / 51-wp-o4-log.md / 53-wp-o5-log.md / 54-wp-o5b-log.md |

## 作業ログ

詳細ログを持つ項目は「何が済んだか」と参照先のみを記す。経緯・実測値・設計判断は
参照先の文書にある。

- 2026-07-11: 計画作成。AL.exe(パブリッシャーポリシー)を SDK 不在時スキップに
  パッチ(Nemerle.nproj / Nemerle.Compiler.nproj / Nemerle.Macros.nproj)。
  ベースラインビルドと分析エージェント3本を並行実行中。
- 2026-07-11: Phase 0 完了(Stage1 ビルド+CLR4 スモークテスト成功)。
  **net4 の ncc.exe が runtimeconfig.json + `dotnet exec` で .NET 10 上で起動することを実証**。
- 2026-07-11: WP-A(メタデータ取り込み)完了。モダン BCL の ref return / byref-like 構造体を
  含むメンバーを取り込み時にスキップ。.NET 10 上で hello.n が IL 生成まで到達し、保存のみ
  失敗する状態に到達。詳細ログ: `10-metadata-import-log.md`。
- 2026-07-12: 1b(ビルドフロー解明)完了 → `02-build-flow.md`。コンパイラー差し替え点は
  MSBuild プロパティ `Nemerle` のみで、Ncc タスクは ncc.exe を外部プロセス起動している。
- 2026-07-12: 1a(API 棚卸し)完了 → `01-api-inventory.md`。新規 M1 ブロッカーなし(すべて
  emission パッケージ内)。セルフホストブロッカーは StrongNameKeyPair 系に限られる。
- 2026-07-12: 1c(ランタイム検証)完了 → `03-dotnet-runtime-facts.md`。全項目 GO。
  PersistedAssemblyBuilder による保存・PDB・公開鍵埋め込みの recipe を実証済み。
- 2026-07-12: WP-B(emission デュアルパス化)完了 → **M1 達成**。.NET 10 上の ncc が hello.n を
  コンパイルし、生成した exe が動作。CLR4 側は無改造・回帰なし。新規 C# ヘルパー
  `dotnet-port\Nemerle.CoreEmit\` を追加。詳細ログ: `11-emission-log.md`。
- 2026-07-12: WP-C(セルフホストブロッカー3件の解消)完了。`StrongNameKeyPair` を両ランタイムから
  除去し、鍵処理をデュアルパス化、`ncc\codedom` をビルド対象から除外。
  詳細ログ: `12-selfhost-blockers-log.md`。
- 2026-07-12: WP-D(stage2 セルフホスト on CoreCLR)完了 → **M2 達成**。
  `build-stage2-core.ps1` が真の core フレーバー(AssemblyRef にレガシー mscorlib 参照ゼロ)の
  Stage2 を生成し、Stage3 フィックスポイントも取得。詳細ログ: `13-stage2-log.md`。
- 2026-07-12: WP-E(`-debug` の PDB 出力を CoreCLR パスに配線)完了。詳細ログ: `14-pdb-log.md`。
- 2026-07-12: WP-I1(実テストハーネスの core 対応 + testsuite 全数実行)完了。
  positive 431/469・negative 165/167(合計 596/636)。失敗 40 件の内訳は環境・ハーネス制約 35 件と
  BCL 挙動差・コンパイラーバグ疑い。詳細ログ: `18-testsuite-log.md`。
- 2026-07-12: WP-H(決定性)+ WP-G(attributes-01)+ WP-F(`-res` / `-linkres` / `/doc:`)完了。
  stage2 の独立 2 回ビルドが 4 アセンブリすべて完全バイト一致。`-linkres` はマルチファイル
  アセンブリ非サポートというランタイム制約により部分達成。詳細ログ: `17-resources-fixes-log.md`。
- 2026-07-12: WP-I2(配布形態の整備)完了 → `dotnet-port\DISTRIBUTION.md`。`pack-tool.ps1` による
  自己完結の実行レイアウト、`dotnet tool` 形式、`.nproj` + `Nemerle.Core.targets` の PoC。
  ncc/lib/macros の `.n` ソースは無変更。
- 2026-07-12: WP-J(overloading-01 regression 修正 + string-template-3 の診断・修正)完了。
  いずれも CLR4 でも再現する既存バグで、CoreCLR の厳格化により露呈した。
  詳細ログ: `19-overload-stringtemplate-log.md`。
- 2026-07-13: WP-A2(dotnet ネイティブ化「レベル A」= フロントエンド)完了。参照自動解決による
  rsp 撤廃(`56964d879`、フォローアップ `041986537`)、`@(ReferencePath)` → `-ref:` 配線
  (`ee2ae06f3`)、`-debug`/PDB + `dotnet clean` 対応(`0de978022`)、`dotnet tool` 更新
  (`06d9aa375`)、Linux 版 targets(`253d2ecb3`、WSL で end-to-end 実証)。
  詳細は `DISTRIBUTION.md` の更新注記。
- 2026-07-13: WP-A3(in-process MSBuild task)完了。SDK-style `.nproj` の `dotnet build` が
  compiler API を collectible AssemblyLoadContext 内で直接呼び、構造化 diagnostics を返す。
  詳細は `20-inproc-task-plan.md` / `20-inproc-task-log.md`。
- 2026-07-13: WP-K(LSP feasibility)完了。IDE engine を .NET 10 で headless build/run し、
  OmniSharp 0.19.9 を使う最小 stdio LSP server を追加。
  詳細は `21-lsp-feasibility.md` / `22-lsp-step1-log.md` / `23-lsp-step2-log.md`。
- 2026-07-13: コンパイラー移植後の次期作業として、VS Code extension と project-aware LSP により
  .NET 10 Nemerle の実用的な編集・build/run loop を作る WP-L を計画。
  原コンパイラー移植計画と成果物/完了条件が異なるため、詳細は
  `24-vscode-development-plan.md` に分離した。
- 2026-07-13: WP-L2(project information provider)完了。`dotnet msbuild` の JSON query を
  OmniSharp 非依存 snapshot に正規化し、VS Code 側の project discovery/selection・status・reload を
  実装。snapshot の engine 適用は WP-L3 へ明示的に残した。
  詳細は `26-vscode-project-info-log.md`。
- 2026-07-14: WP-L3(project-aware engine workspace)完了。WP-L2 snapshot を `IIdeProject` adapter
  経由で analysis engine に適用し、project-aware diagnostics を実装。
  詳細は `27-vscode-project-workspace-log.md`。
- 2026-07-14: WP-L4(packaging と end-to-end test)完了 = **WP-L 全完了**。extension 0.4.0 が
  LspServer の Release 出力を VSIX に同梱し、既定で bundled server を起動する。
  詳細は `28-vscode-packaging-log.md`。
- 2026-07-14: WP-L 完了を受け、次期フェーズ WP-M(.NET 10 Nemerle 開発環境2)を計画。
  スコープは WP-M1 IDE/build parity → WP-M2 hover → WP-M3 completion → WP-M4
  definition/references → WP-M5 incremental rebuild → WP-M6 `Nemerle.Sdk` NuGet package +
  `dotnet new` template + toolchain provenance。semantic tokens / formatting / multi-root /
  Linux 実地検証 / Marketplace は WP-M 完了後の優先順位リストへ。詳細は `29-devenv2-plan.md`。
- 2026-07-14: WP-M1(IDE/build parity と server ログの地固め)完了。`DefineConstants` の
  `-define:` 配線、警告 N コードの構造化、server ログの `window/logMessage` 化。
  新 fixture `samples/Defines`・`samples/Warnings`。詳細は `30-devenv2-wp-m1-log.md`。
- 2026-07-14: WP-M2(hover + `EngineRequestBridge`)完了。engine の `Begin*` を await 可能にする
  汎用境界を作り、以後の M3/M4 が再利用する。engine 無改造で Stage リビルド不要。
  詳細は `31-devenv2-wp-m2-log.md`。
- 2026-07-14: WP-M3(completion)完了。`completionItem/resolve` による遅延 documentation 込み。
  engine 無改造で Stage リビルド不要。詳細は `32-devenv2-wp-m3-log.md`。
- 2026-07-14: WP-M4(definition / references)完了。外部 assembly member は空結果 + Info ログ。
  engine 無改造で Stage リビルド不要。詳細は `33-devenv2-wp-m4-log.md`。
- 2026-07-15: WP-M5(incremental rebuild / relocation)完了。document sync を Incremental に切替え、
  method body 内編集は該当 method のみ再型付け。escape hatch `NEMERLE_INCREMENTAL_UPDATE=0` あり。
  engine 無改造で Stage リビルド不要。詳細は `34-devenv2-wp-m5-log.md`。
- 2026-07-15: WP-M6(`Nemerle.Sdk` NuGet package + project template + provenance)完了 =
  **WP-M 完了**。repo checkout 無しで `dotnet new nemerle-console` → `dotnet build` → `dotnet run` が
  Windows・WSL 双方で成立。配布規約を PO と合意(ID は「公式名 + `.Unofficial`」、版は
  `1.2.<Nemerle.dll revision>` + prerelease label)。GitHub Packages は PAT 必須のため不採用、
  公開は local feed 止まり。詳細は `35-devenv2-wp-m6-log.md`。
- 2026-07-19: **WP-N4 完了**(版タグ契約 + GitHub Release 初回発行)。
  `release/1.2.<rev>-preview.<N>` / `seed/1.2.<rev>` のタグ契約を確立し、
  **release/1.2.635-preview.1 を GitHub Release(prerelease)として発行**。
  GitHub asset のみからの install→new→build→run と、clone からの再現を検証。
  詳細は `41-prerelease-wp-n4-log.md`。
- 2026-07-20: **boot-4.0 を世代 538→636 へ更新**(WP 番号なし、PO 依頼の単発保守)。
  発行済みリリースの再現性は boot-net10 seed のみに依存するため無影響、再リリース不要と判断。
  詳細は `45-boot-4.0-refresh-log.md`。
- 2026-07-21: **WP-N7 評価完了(部分 GO、PO 合意)**。版跨ぎ自己ホストの実測により
  「case 1 = version.txt による版ピン留め + orphan/pinned-worktree 廃止」を採用、ただし
  版バンプ(新世代生成)は Windows/.NET FW 4.x 限定のまま(Mono は SRE 非互換で不可、
  CoreCLR は厳格ローダーで版跨ぎ不可)。詳細は `44-prerelease-wp-n7-log.md` §7。
- 2026-07-22: **WP-N7 case 1 実装 + WP-N6(最小 Linux CI)完了 = WP-N 全完了**。
  版ピンにより seed が任意 HEAD をビルド可能になり、seed を `dotnet-port/seed/` にチェックイン。
  push/PR CI と手動 release workflow を追加。
  詳細は `44-prerelease-wp-n7-log.md` §8 / `46-prerelease-wp-n6-log.md`。
- 2026-07-22: 次フェーズ **WP-O(保存・配布)** を計画(`47-wp-o-plan.md`)。位置づけは
  保存・再現・文書化を第一に「少し試せる」間口を用意する。O1(入口)/ O2(保存・再現)/
  O3(runtime package 根治)/ O4(配布の器の go/no-go)/ O5(任意 showcase)。
- 2026-07-23: **WP-O1(入口の現代化)完了**。ルート `README.md` に .NET 10 ポートの入口節を追加し、
  公開済み GitHub Release アセットのみで quickstart を Windows / Linux(WSL)実機検証。あわせて
  PO 指示のスコープ追加として、`dotnet-port/` 直下の計画・作業ログ文書(本書含む 44 本)を
  `dotnet-port/docs/` へ移設した。詳細は `48-preservation-wp-o1-log.md`。
- 2026-07-23: **WP-O2(保存・再現性の確定)完了**。公開済み `release/1.2.635-preview.1` の
  provenance 連鎖を検証し、使い捨て clone からリリースタグを再ビルドして初回発行物との一致を
  再実証。詳細は `49-preservation-wp-o2-log.md`。
- 2026-07-23: **WP-O3(net10 runtime package の根治)完了**。Nemerle ランタイム閉包を新パッケージ
  `Nemerle.Runtime.Unofficial` 化し、`GenerateDependencyFile` 既定を SDK 既定(true)へ戻した。
  共有ソース無変更。詳細は `50-wp-o3-log.md`。
- 2026-07-23: **WP-O4(配布の器の判断)完了**。nuget.org = **no-go** / VS Code Marketplace =
  **no-go** で確定(GitHub Release を配布器として維持)。両器の一方通行コミットを避け、位置づけに
  整合させた判断。恒久ではなく状況が変われば再判断(バックログには積まない)。
  詳細は `51-wp-o4-log.md`。
- 2026-07-23: **リリース `release/1.2.635-preview.2` 発行**。WP-O3 の完全セット
  (`Nemerle.Runtime.Unofficial` を初めて含む 4 nupkg + VSIX 0.9.0 + README + release-info.json)を
  GitHub prerelease として発行(commit `019a749de`、provenance 一致)。preview.1 の D1(deps.json が
  runtime 閉包を列挙しない)を解消した版。手作業でアセットを触らず release workflow の
  `build-smoke`(dry-run)→ `build-smoke-release` で発行し、いずれも green。preview.1 は再現可能な
  履歴として残置(両者 prerelease、"Latest" にはならない。番号 preview.1 は再利用しない)。
  併せて発行前に CI 赤を解消: `ProjectInfo.Test` の `SdkPackageTests` が `GenerateDependencyFile` の
  旧挙動(false)をアサートしていた陳腐化を SDK 既定(true)へ修正(commit `019a749de`、テスト専用)。
- 2026-07-25: **WP-O5(showcase)を実施決定(PO 指示)**。順序は (a) semantic tokens 先行 →
  (b) 試遊用サンプル。(a) は既存バックログの筆頭項目(`29-devenv2-plan.md` §11-1 /
  `36-prerelease-quality-plan.md` の課題 **E1** の一部)の切り出し。E1 の残り(signatureHelp /
  documentHighlight / formatting / rename / codeAction)は引き続き非ゴール。
  詳細は `47-wp-o-plan.md` §5 WP-O5。
- 2026-07-25: **WP-O5a(semantic tokens)完了**。`textDocument/semanticTokens/full` を実装し、
  **syntax マクロが増やしたキーワードを `macro` type で返す**(TextMate 文法では原理的に出せない
  差別化の可視化)。**共有ソース無改造 = Stage リビルド不要**(`1.2.0.635` のまま)。
  **公開済み 0.9.0 の再利用を避けるため VSIX/server 版を 0.10.0 へ bump**。
  詳細は `53-wp-o5-log.md`。
- 2026-07-26: **WP-O5b(試遊用サンプル)の受け入れ基準を構造として充足**。`samples/README.md` を
  新設してルート `README.md` から辿れるようにし、syntax マクロのサンプル 2 本(Latin / SyntaxTree)を
  追加、重複していた `CompTimeSolver/SolveMaze` を削除。`build-samples.ps1` を CI に組み込み、
  README が載せるサンプルのビルドと `CompTimeSolver/Fail` の失敗を保証する。同スクリプトが
  `samples/` 配下の全 `.nproj` の分類を強制するため、以後サンプルを足しても基準は維持される。
  併せて WP-O5a の実機不具合(colorize が engine の worker スレッド外で走り、本体型付けが
  NRE になる)を修正。詳細は `54-wp-o5b-log.md`(WP-O5a の修正は `53-wp-o5-log.md`)。
