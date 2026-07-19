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
| WP-N | 公開前の品質固めと既知制約の解消(ビルド再現性・engine 品質・Nemerle.Linq/testsuite・Linux 実地・版タグ契約 + GitHub Release・最小 CI) | —(計画後) | 進行中(N1/N2/N3/N5/N4 完了。残り N7 評価 → N6) | 36-prerelease-quality-plan.md / 37-prerelease-wp-n1-log.md / 38-prerelease-wp-n2-log.md / 39-prerelease-wp-n2-log.md / 40-prerelease-wp-n3-log.md / 41-prerelease-wp-n4-log.md / 42-prerelease-wp-n5-log.md / 43-boot-net10-log.md |

## 作業ログ

- 2026-07-11: 計画作成。AL.exe(パブリッシャーポリシー)を SDK 不在時スキップに
  パッチ(Nemerle.nproj / Nemerle.Compiler.nproj / Nemerle.Macros.nproj)。
  ベースラインビルドと分析エージェント3本を並行実行中。
- 2026-07-11: Phase 0 完了(Stage1 ビルド+CLR4 スモークテスト成功)。
  **net4 の ncc.exe が runtimeconfig.json + `dotnet exec` で .NET 10 上で起動することを実証**。
- 2026-07-11: WP-A 完了: `ncc/external/ExternalTypeInfo/ExternalTypeInfo.n` の
  collect_members に一元フィルタを追加し、モダン BCL の ref return / byref-like 構造体を
  含むメンバーを取り込み時にスキップ(CLR4 では無効)。.NET 10 上で hello.n が
  取り込み→型付け→IL 生成まで通り、保存(`AppDomain.DefineDynamicAssembly` 6引数
  オーバーロードの JIT 時 MissingMethodException)でのみ失敗する状態に到達。
  詳細ログ: 10-metadata-import-log.md
  - 知見: CoreCLR では存在しない API の呼び出しは **呼び出し元の JIT 時点** で
    MissingMethodException になる → レガシー専用呼び出し(6引数 DefineDynamicAssembly、
    GetSymWriter、System.Security.Permissions 依存の GetPermissionSets 本体等)は
    「core では決して呼ばれないメソッド」へ隔離が必須。
  - 次の障害(WP-B スコープ): CreateAssembly のデュアルパス化 → ILEmitter の
    FieldToken/MethodToken ハック → AssemblyBuilder.Save 代替。
- 2026-07-12: 1b(ビルドフロー解明)完了 → 02-build-flow.md。要点:
  - コンパイラー差し替えポイントは MSBuild プロパティ `Nemerle`(= コンパイラーの
    ディレクトリ)のみ。Ncc タスク(tools\msbuild-task\MSBuildTask.cs)は
    **外部プロセス** で `ncc.exe /from-file:<rsp>` を起動 → 将来 `dotnet ncc.dll` に差し替え可。
  - 参照は RAR が解決した絶対パスの `/ref:` で渡る。コア4プロジェクトは `-no-stdlib` +
    明示参照 + `/greedy-references:-`。実際のコマンドラインは 02 ドキュメント参照。
  - Stage2 を dotnet 上で回す最小路: レガシー targets を使わず、静的レスポンスファイル
    4本 + `dotnet ncc.dll /from-file:` を依存順に実行。
  - 回帰ゲート: Nemerle.Compiler.Test.exe に `-ncc <exe>` / `-runtime <exe>` スイッチが
    既にあり、外部コンパイラーを試験可能。
- 2026-07-12: 1a(API 棚卸し)完了 → 01-api-inventory.md。要点:
  - 新規 M1 ブロッカーなし(全て emission パッケージ内)。ただし CreateAssembly は
    PermissionSet ローカル宣言だけで JIT 不能 → CAS は分岐でなく **切除** が必要。
    DefineVersionInfoResource() は保存経路で無条件呼び出し(core ではスキップへ)。
  - セルフホストブロッカー: LibraryReference.n:118 GetIsFriend の StrongNameKeyPair
    (InternalsVisibleTo 読み取りで踏む)/ CustomAttribute.n read_keypair・KeyPair /
    ncc\codedom は Nemerle.Compiler.dll 同梱だが未使用 → 除外方針。
  - 好材料: TypeResolve・AssemblyResolve は core で動作。マクロロード(GetType+ctor.Invoke)
    は問題なし。BinaryFormatter/Remoting/レジストリ/Thread.Abort/", mscorlib" 文字列は
    リポジトリに存在せず。lib/・macros/ はほぼクリーン。InternalTypes の起動時型解決は
    .NET 10 で全て成功。
- 2026-07-12: 1c(ランタイム検証)完了 → 03-dotnet-runtime-facts.md(検証済み C# recipe と
  テストプロジェクト scratchpad\wp-c\EmitTests 付き)。全項目 GO。要点:
  - PersistedAssemblyBuilder は AssemblyBuilder の sealed サブクラス。モジュールは1つ、
    DefineDynamicModule(name) のみ。
  - exe 保存 recipe 実証済み(GenerateMetadata → ManagedPEBuilder → dotnet exec で exit 42)。
    **MetadataToken は GenerateMetadata 後にのみ有効**(エントリポイントのトークンは後読み)。
  - **core では CreateType の未作成基底解決に TypeResolve が発火しない**(NotSupportedException)
    → CreateType は基底優先のトポロジカル順が必須。resolve_hack は core では無力。
  - マネージドリソース埋め込み recipe 実証済み。レガシーリソース API は全滅。
  - **.NET 10 は PDB をインボックスでサポート**(DefineDocument/MarkSequencePoint/
    PortablePdbBuilder)→ -debug は後続で対応可能。
  - 公開鍵埋め込み実証済み(snk の CAPI パース、Nemerle.snk token=e080a9c724e2bfcd)。
    CoreCLR は署名を検証しないので public-key-only で十分。
  - トークン API(Get*Token)・MethodRental・AssemblyBuilderAccess.Save は全滅。
    MetadataToken プロパティで置換。persisted の Module.FullyQualifiedName は
    NotImplementedException → ScopeName を使う。
  - coreAssembly=typeof(object).Assembly の場合、出力の AssemblyRef は
    System.Private.CoreLib 10.0 を指す(同一ランタイム上のセルフホストには十分。
    クリーンな TFM ターゲティングは MetadataLoadContext.CoreAssembly で後日)。
- 2026-07-12: WP-B(emission デュアルパス化)完了 → **M1 達成**:
  `dotnet exec bin\Release\net-4.0\Stage1\ncc.exe -out:hello10.exe hello.n` が .NET 10 上で
  成功し、`dotnet exec hello10.exe` が `Hello!` を出力(exit 0)。hello2.n(ジェネリクス +
  Nemerle 標準ライブラリ)・`-target:library`・`-resource:`(埋め込みリソース往復確認済み)も
  .NET 10 上で成功。CLR4 側は無改造の回帰無し(hello/hello2/継承・入れ子・ジェネリック・
  インターフェースの混合階層テスト/-debug/-resource すべて一致)。詳細ログ:
  11-emission-log.md。要点:
  - 新規 C# ヘルパー `dotnet-port\Nemerle.CoreEmit\`(net10.0)に PersistedAssemblyBuilder /
    GenerateMetadata+ManagedPEBuilder の保存 recipe を実装、`ncc\generation\CoreEmitBridge.n`
    (`IsCoreClr` 判定 + リフレクション経由呼び出し)から利用。HierarchyEmitter.n の
    CreateAssembly/SaveAssembly/リソース埋め込みを CLR4 専用メソッドと CoreCLR 専用メソッドに
    完全分離(CAS/PermissionSet も含め、分岐でなく「呼ばれない別メソッドへの隔離」が必須という
    WP-A の知見どおり)。
  - **重要な教訓・回帰の発見**: ILEmitter.n の `GetHackish{Method,Field,Constructor}` で
    `*Builder.GetToken().Token` を無条件で `MetadataToken` に置換したところ、Stage1 は正常に
    ビルドできたが hello2.n(list[int] 経由のジェネリクス)を **CLR4 でコンパイルした exe が
    実行時にクラッシュ**する回帰を作り込んだ(GetToken()とMetadataTokenはSave系
    AssemblyBuilderでは常に同値ではない — 元コードの "HACKS FOR MS.NET BUGS" というコメントは
    伊達ではなかった)。CLR4 側ロジックを一字一句そのまま `...Clr4` メソッドへ退避し、
    CoreCLR だけ MetadataToken を直接使うデュアルパスに修正して解消。**facts ドキュメントが
    「core で安全」と言っている置換でも、CLR4 側で検証なしに一括適用しない**という運用上の
    注意点。
  - CreateType の生成順序(基底優先)は `TypesManager.Iter`/`IterConditionally` が既に
    `iterate_first`(基底型・外側型の推移閉包)を辿ってから処理する実装になっており、
    **無改造で core でも正しく動作**することを階層テストで実証(resolve_hack は core では
    無力だが、既存の順序保証だけで十分だった)。
  - 残課題(次パッケージへ): -debug の PDB 出力(core 側の recipe は 03 文書で検証済みだが
    未配線)、-res(Win32)/-linkres、-keyfile(`GetPublicKeyFromSnk` は実装済みだが
    CustomAttribute.n の read_keypair / AssemblyName.KeyPair 未配線)、
    LibraryReference.n の GetIsFriend(StrongNameKeyPair, self-host で必ず踏む)、
    ncc\codedom の除外方針の実施。
- 2026-07-12: WP-B(emission デュアルパス化 + Nemerle.CoreEmit.dll 補助アセンブリ)を
  sonnet エージェントで実装開始。
- 2026-07-12: WP-C(SELF-HOST ブロッカー3件の解消)完了。詳細ログ: 12-selfhost-blockers-log.md。
  要点:
  - `ncc\external\LibraryReference.n` の `GetIsFriend`(IVT + PublicKey= 突合)から
    `StrongNameKeyPair` を**両ランタイムから完全に除去**。新規 `ncc\misc\SnkUtils.n`(素の
    Nemerle、boot-4.0 でコンパイル可)が .snk の CAPI ブロブを直接パースして公開鍵を導出する
    ため、CoreEmitBridge 経由のランタイム分岐が一切不要(公開鍵の導出だけなら CoreCLR 専用
    API は不要という 03 文書の知見どおり)。
  - `ncc\hierarchy\CustomAttribute.n` の `read_keypair`/`CreateAssemblyName` はデュアルパス化:
    CLR4 は `an.KeyPair = StrongNameKeyPair(...)` で実署名を維持(`set_assembly_key_clr4`)、
    CoreCLR は `SnkUtils` で導出した公開鍵を `AssemblyName.SetPublicKey` + `PublicKey` フラグで
    埋め込むのみ(delay-sign 相当、初回に一度だけ警告)。`an.KeyPair` の getter も core で PNSE
    になるため、「既にキー指定済みか」の判定を getter 参照からローカル bool フラグに変更。
  - **受け入れテストの過程で発見した既存バグ2件**(CLR4 上でも一度も正しく動いたことがない
    デッドコード): (1) `GetIsFriend` の `[asmName, pKey]` マッチ節のガード条件が
    `IsNullOrEmpty` になっており実質反転していた(キー未指定時のみ発火し、その場で空パスの
    `File.Open` に失敗して必ず例外)。(2) `PublicKey=<hex>` を内部でさらに `,` 区切りしていた
    (`=` の誤り)ため2要素パターンに一致し得なかった。両方とも本 WP で修正。IVT+PublicKey=
    を使うテストが testsuite に一つも無かったため気づかれていなかった。
  - **Nemerle 特有の落とし穴を1件発見**: `SnkUtils.n` で `w.Write (0x00002400u)` のような
    無型付きリテラルを `BinaryWriter.Write` に直接渡すと、Nemerle のオーバーロード解決は
    C# と異なり `u` サフィックスを無視して**値が収まる最小の整数型**(この場合 `ushort`)を
    選んでしまい、4バイトのつもりが2バイトしか書かれず鍵ブロブが破損した(CoreCLR の
    `AssemblyLoadContext.LoadFromAssemblyPath` が `SecurityException: Invalid assembly public
    key` で検出)。`: uint` 型注釈付きローカル変数を介することで解消。今後 C# レシピを
    Nemerle に移植する際の一般的な注意点としてログに記録。
  - `ncc\codedom\*.n`(4ファイル、`NemerleCodeProvider`/`NemerleCodeGenerator`/
    `NemerleCodeCompiler`/`NemerleMemberAttributeConverter`)は `Nemerle.Compiler.nproj` の
    `<Compile Include>` から除外(`ncc.build` の NAnt 定義は指示どおり未変更・未使用)。
    ncc/lib/macros のどこからも呼ばれておらず、System.CodeDom/System.Configuration
    (.NET 10 共有フレームワーク外)への依存を断てる。Nemerle.Compiler.dll の公開 API 面が
    縮小するが合意済みのトレードオフ。ビルド後にリフレクションで CodeDom 関連の型が
    一つも残っていないことを確認済み。
  - 受け入れテスト(libA.n/libB.n、`InternalsVisibleTo("libB, PublicKey=<Nemerle.snk の
    公開鍵hex>")` + libA の internal メンバーを libB から参照)を .NET 10 と CLR4 の両方で
    実施し、双方成功。.NET 10: `AssemblyLoadContext` でロードした libA.dll の
    `FullName` に `PublicKeyToken=e080a9c724e2bfcd` を確認、libB から internal メンバー呼び出し
    成功。CLR4: 同じ2段階コンパイルが成功し、`sn.exe -vf libA.dll` が「有効」(= 本物の
    暗号学的署名)と報告、`sn.exe -T` で同じ PublicKeyToken を確認 — CLR4 の実署名パスに
    回帰なし。hello.n/hello2.n は両ランタイムで従来どおり動作(回帰なし)。
  - 既知の残課題(次パッケージへ): -debug の PDB 出力・-linkres・Win32 -res は WP-B からの
    既知ギャップのまま未着手。実際の Nemerle.dll/Nemerle.Compiler.dll/Nemerle.Macros.dll 間の
    真のセルフホスト(本物のソースを CoreCLR 上で再コンパイル)は本 WP のスコープ外
    (合成した libA/libB ペアでのみ検証)。リポジトリの実ビルドは現時点で
    `InternalsVisibleTo(..., PublicKey=...)` を自身の間で使っていない(grep 済み、testsuite
    以外にヒットなし)ため、stage2 オーケストレーションはこのパスが実際の lib/ncc ソースに
    対して検証済みだと仮定しないこと。
- 2026-07-12: WP-D(集大成: stage2 セルフホスト on CoreCLR)完了 → **M2 達成**。詳細ログ:
  13-stage2-log.md。要点:
  - `dotnet-port\build-stage2-core.ps1` 新設: Stage1(net4 バイナリー、`dotnet exec` で
    .NET 10 上で実行)から `dotnet exec <ncc> /from-file:<rsp>` を4回(依存順: Nemerle →
    Nemerle.Compiler → Nemerle.Macros → ncc)呼び出し、真の core フレーバー(AssemblyRef が
    すべて `System.Private.CoreLib`/実体の分割アセンブリ 10.0.0.0、レガシー mscorlib
    参照ゼロ)の `bin\Release\core\Stage2\` を生成。rsp は `dotnet-port\rsp\stage2\*.rsp`
    に実体として出力。同スクリプトに `-Compiler bin\...\Stage2\ncc.exe -OutDir
    bin\...\Stage3` を渡すだけで **stage3(セルフホスト・フィックスポイント)** も生成可能
    (0 エラー、`ncc.exe` は MVID/PE タイムスタンプをマスクすると stage2 と完全バイト一致、
    他3アセンブリはサイズ一致だが内容はまだ非決定性の余地あり→今後の課題)。
  - 参照戦略の核心的知見: 作業指示の (a) .NET 10 共有フレームワークの互換ファサード
    (`mscorlib.dll` 等、型フォワーダーのみ)への `-ref:` は **機能しない**
    (`GetExportedTypes()` が 0 件を返す、`GetForwardedTypes()` は転送先アセンブリ不在で
    例外)。代わりに (b) `-use-loaded-corlib`(裸名 `mscorlib`/`System` を実行中の
    CoreCLR 自身のアセンブリにマップ)+ 共有フレームワーク内の**実体**分割アセンブリ
    (System.Collections/.Specialized、Linq、Console、Diagnostics.Process、
    Private.Uri/.Xml/.Xml.Linq、Diagnostics.TraceSource、Security.Cryptography、
    Data.Common)への明示 `-ref:` の組み合わせのみが動作。
  - **本 WP で発見・修正した真の stage2 専用バグ2件**(CLR4 では一度も踏まれたことがない):
    (1) `ncc\external\Codec.n`: .NET 10 の `GetGenericParameterConstraints()` が
    `where T: struct, Enum` を `[Enum, ValueType]` の**冗長な2要素**として返す
    (.NET Framework は `Enum` のみ)→ `StaticTypeVar` の多重非インターフェース制約
    検査に誤って抵触。既存の `Typer.GetMinimal` による最小集合縮約を流用して修正
    (CLR4 では no-op)。(2) `ncc\typing\Typer-OverloadSelection.n`: 最近の BCL が
    補間文字列ハンドラー対応のために追加した「末尾引数にデフォルト値を持つ新オーバーロード」
    (例: `Debug.Assert(bool, string message = null)`)が実引数数だけでは
    `Assert(bool)` と一意に区別できず「曖昧」エラーに。実際の C# は
    `OverloadResolutionPriorityAttribute` で解決するが ncc の反射ベース解決器はこれを
    見ない → 既存の `UsedDefaultParms` フラグを使い、デフォルト値未使用の候補を優先する
    タイブレークを追加。
  - 加えて、WP-B/WP-C で「CoreCLR では絶対に呼ばれない」よう分離済みだった `*Clr4`
    メソッド群(`HierarchyEmitter.n`/`ILEmitter.n`/`CustomAttribute.n` の
    `CreateAssemblyClr4`/`SaveAssemblyClr4`/`GetHackish*TokenClr4`/
    `Apply*DeclarativeSecurityClr4` 等)は、**実行時に呼ばれない**ことと
    **コンパイル時に型検査できる**ことは別問題だと判明: CoreCLR 上で動く ncc が自分自身の
    ソースをコンパイルする際、これらのメソッド本体もリフレクションで解決される
    (`AssemblyBuilder.Save`/`SetEntryPoint`、`*Builder.GetToken()`、
    `*.AddDeclarativeSecurity`、`SymDocumentType` 等の legacy SymbolStore 型、
    `PermissionSetAttribute`)。`TypeBuilder.n`/`main.n` に既存の `#if NET_4_0` パターン
    を全面適用し、該当メソッド本体・フィールド宣言をガード(else 分岐はデッドコードの
    スタブ)。CLR4 側は一字一句無変更。
  - `macros\Resource.n` の `[Resource]` マクロ(.resx ラッパー生成)は
    `System.Resources.ResXResourceReader` が CoreCLR に存在しないため `#if NET_4_0` で
    ガードし、core では明確なエラーメッセージを出す(コンパイラー自体のビルドは通す)。
  - 受け入れ結果: hello.n/hello2.n が stage2/stage3 双方の ncc.exe で dotnet 上でも
    CLR4 上でも同一出力(回帰なし)。testsuite\positive の先頭60本(手動スライス)を
    stage2 ncc でコンパイルし 53/60 成功、失敗7件のうち6件はハーネス側の制約
    (マルチファイル依存や Nemerle.Compiler.dll 未参照)、1件(`attributes-01.n`)は
    `CustomAttributeBuilder` の `ArgumentException` という genuine な今後の課題として記録。
  - 残課題: `/doc:` 未検証(意図的に外しただけで既知の不具合ではない)、`-debug`/
    `-linkres`/Win32 `-res` は引き続き core 未配線、stage2/stage3 の非 MVID 差分の
    根本原因調査、`attributes-01.n` の CustomAttributeBuilder 例外、
    実 testsuite ハーネス(Nemerle.Compiler.Test.exe)の core 移植。
- 2026-07-12: WP-E(`-debug` の PDB 出力を CoreCLR パスに配線)完了。詳細ログ:
  14-pdb-log.md。要点:
  - `Nemerle.CoreEmit.Emitter.Save` に `emitDebug` を追加し、03 文書 R4 レシピを実装
    (`GenerateMetadata` 3-out → `PortablePdbBuilder` → standalone `<出力名>.pdb` +
    `DebugDirectoryBuilder.AddCodeViewEntry`)。`CoreEmitBridge.Save`/
    `HierarchyEmitter.SaveAssemblyCore` に `Manager.Options.EmitDebug` を配線し、
    `CreateModuleCore` の warn-and-skip を撤去。フロントエンド(TExpr.DebugInfo)と
    ILEmitter の MarkSequencePoint/SetLocalSymInfo は無改造で core でも機能。
  - 発見: (1) レガシー `SymDocumentType`/`SymLanguageType`/`SymLanguageVendor` は
    .NET 10 の `System.Diagnostics.StackTrace.dll` に実在し mscorlib ファサードで転送
    される → NET_4_0 フレーバーの Stage1 バイナリーの既存4引数 DefineDocument 呼び出しは
    core でもそのまま JIT・実行できる(「型検査できないが実行はできる」— WP-D の逆)。
    (2) core の `DefineDocument` は URL で重複排除しない(CLR4 の ISymbolWriter は
    していた)ため、素朴な配線では PDB にメソッド数ぶんの重複 Document 行が入る →
    `TypesManager._core_debug_documents`(モジュール単位キャッシュ、core 専用)を追加。
    (3) 元コードの DefineDocument は昔から language 位置に `SymDocumentType.Text` を
    渡す引数順バグがあり(無害)、core フレーバーの2引数版でも同じ GUID を渡して
    フレーバー間の PDB 出力一致を維持。(4) stage2 スクリプトに既存ハザード:
    アセンブリバージョンが `git describe` のコミット数由来のため、Stage1 が古い
    コミットのままだと stage2 の Nemerle.dll(新バージョン)ロードが ref-def mismatch で
    失敗する(AssemblyInfo.n を touch して Stage1 リビルドで解消。恒久チェックは将来課題)。
  - 受け入れ: 6項目 ALL PASS — .NET 10 で `-debug` コンパイル+PDB 生成(stage1-on-core /
    stage2 双方)、SRM 検証(CodeView GUID 一致・Document・シーケンスポイント・ローカル名)、
    例外スタックトレースにファイル名+行番号、CLR4 -debug は boot-4.0 ベースラインと
    行番号まで完全一致(回帰なし)、非 debug 回帰なし、stage2/stage3 ビルド 0 エラー。
- 2026-07-12: WP-I1(実テストハーネス Nemerle.Compiler.Test.exe の core 対応 + testsuite
  全数実行)完了。詳細ログ: 18-testsuite-log.md。要点:
  - 選んだ経路: ハーネス(net-4.0/CLR4)のソースは**無改造**。既存の `-ncc <exe>`
    (ExternalNcc)+ `-runtime <exe> -runtime-params <args>`(両方に共有される
    `ProcessStartInfoFactory`)を使い、`-r dotnet.exe -rp exec` を渡すだけで
    「コンパイラー起動」も「positive テストの生成 exe 実行」も両方 `dotnet exec` 経由になる
    ことをソース読解で確認・実証。作業指示の(a)(ハーネス自体を core 再コンパイル)は
    不要と判断し着手せず(b)のみで受け入れ基準達成。
  - 新規 `dotnet-port\run-testsuite-core.ps1`: bin\ 非破壊(コンパイラー/ハーネスを
    毎回 `%TEMP%\nemerle-core-testsuite` 相当へコピーしてから使用)、
    `-Compiler`/`-HarnessDir`/`-StagingDir`/`-Suite` 等パラメーター化、
    PASS/FAIL/SKIP 集計と失敗詳細ファイルを自動出力。
  - **重要な発見・修正**: 初回全数実行で positive 216/469 が失敗、うち大半が謎の
    `exit code -532462766`。原因は「コンパイル済み test exe が実行時に隣の
    `Nemerle.dll` を見つけられない」(13-stage2-log.md で既知だった "scratch-dir
    artifact" が全数実行で顕在化)。出力ディレクトリへコンパイラー本体の *.dll を
    コピーする1行で 216→42 失敗に激減。追加で `System.Net.Primitives`/
    `System.Net.NameResolution`/`System.Collections.NonGeneric`/`System.ObjectModel`
    の4参照をグローバル参照に追加して 42→38 失敗まで削減。
  - 最終集計(2回の独立実行で完全再現): positive 431/469、negative 165/167、
    合計 596/636(93.7%)PASS。失敗40件は環境・ハーネス制約35件(6カテゴリー:
    core 未ビルド補助ライブラリ、C# パーサー未移植、.NET 10 に同梱されない BCL 面、
    CAS no-op、-res/-linkres 未サポート、外部ツール欠如)と、BCL 挙動差3種
    (double 書式・例外メッセージ書式・Obsolete 属性追加)+ コンパイラーバグ疑い
    (attributes-01 は既知の再確認、attributes-03/attributes-assembly の
    BadImageFormatException と string-template-3 の内部コンパイラーエラーは新規発見、
    診断のみで修正せず)に分類。
  - **最重要の新規発見**: negative\overloading-01.n が、WP-D(13-stage2-log.md item 2b)
    で導入した `Typer-OverloadSelection.n` の `AintUsingDefaultParms` タイブレークにより
    genuine な既存の regression を起こしていることを発見。**CLR4 の Stage1 ncc.exe でも
    同一に再現**することを確認済み(core 固有ではなく、WP-D 時点で作り込まれ、実ハーネスで
    negative 全数を初めて回すまで気づかれなかった)。次 WP での修正を強く推奨。
- 2026-07-12: WP-H 修正 + WP-G 修正 + WP-F(-res/-linkres//doc:)完了。詳細ログ:
  17-resources-fixes-log.md。要点:
  - **WP-H(決定性)**: `MacroClassGen.n:749` の `Util.tmpname($"operator$(x.GetHashCode())")`
    を `Util.tmpname("operator")` に置換(16 の診断の案A、1行)。stage2 を同一コンパイラーで
    2回独立ビルドし、MVID/PE タイムスタンプのマスク後に **4アセンブリすべて完全バイト一致**
    (16 時点の唯一の非決定源が解消。旧「stage2/stage3 の非 MVID 差分」課題はクローズ)。
  - **WP-G(attributes-01)**: 15 の診断 §6 の差分案どおり実装。
    `Nemerle.CoreEmit\AttributeBlob.cs` 新設(local enum が絡む場合のみ ECMA-335 CA blob を
    手組みして GetUninitializedObject + m_con/m_blob 注入で CustomAttributeBuilder を鋳造、
    それ以外は従来コンストラクター)。`CustomAttribute.n` `do_compile` は IsCoreClr 分岐のみ、
    CLR4 側一字一句無変更。attributes-01.n が Stage1-on-.NET10 / stage2 / CLR4 の3構成すべてで
    BEGIN-OUTPUT と完全一致。派生3形状(名前付き enum・位置引数 enum・enum 配列)も検証済み。
  - **副産物の重要バグ発見・修正**: attributes-01 の受け入れ中に、
    **PersistedAssemblyBuilder のアセンブリレベル SetCustomAttribute が、ローカル定義
    (TypeBuilder)コンストラクターの属性で保存 PE を静かに破壊する**バグを発見
    (コンパイル成功・ロード時 BadImageFormatException。型レベルの SetCustomAttribute は
    同条件で正常 → persisted 実装のアセンブリレベル経路固有。Nemerle 非依存の C# repro で
    確認、upstream 報告候補)。回避策: `Emitter.SetAssemblyCustomAttribute` が該当属性のみ
    保留し GenerateMetadata 後に `MetadataBuilder.AddCustomAttribute`(Assembly トークン
    0x20000001 親)で直接書き込む。`HierarchyEmitter.n` の6箇所を新設 `set_assembly_attribute`
    ディスパッチャー経由に統一(CLR4 は従来どおり直接呼び出し)。
    **WP-I1 が新規バグ疑いとして記録した attributes-03.n / attributes-assembly.n の
    BadImageFormatException(18 §6.2(2))はこのバグそのものであり、本修正で3構成すべて
    BEGIN-OUTPUT 完全一致まで解消済み**(18 の推測と異なり引数の形は無関係、ローカル ctor が
    唯一の発火条件。次 WP での診断作業は不要)。
  - **WP-F 3a(/doc:)**: 検証のみで修正不要と確認。XmlDump.n は素の System.Xml のみ使用、
    hello.n と frommcs/test-xml-004.n の -doc: 出力が CLR4 と .NET 10 で完全一致
    (ファイル名埋め込み差を除く)。
  - **WP-F 3b(Win32 -res)**: `Nemerle.CoreEmit\Win32Resources.cs` 新設 —
    `ResFileResourceSection : ResourceSectionBuilder`(RES→COFF .rsrc 3階層ツリー変換、
    Roslyn CvtResFile 相当の簡易版、2パスでバックパッチ不要)を `ManagedPEBuilder` の
    nativeResources に配線。実在の rc.exe 産 .res(ncc.exe.res、RT_VERSION)と手組み .res
    (RT_RCDATA+名前付き)の両方で、生成 exe の .rsrc から元データが**バイト一致**で
    取り出せることを SRM で検証。CLR4 回帰なし。ハマり: RES の HeaderSize はエントリ先頭
    (DataSize フィールド自体)からの計測(先頭2 DWORD を含む)— 本物の .res との
    クロスチェックで発見、誤読すると壊れた .res で無限ループし得たため防御も追加。
    **自動バージョン情報リソース(CLR4 の DefineVersionInfoResource 相当)は実装見送り・
    TODO 文書化**(CLI スイッチ無し・-win32-resource で代替可能・工数対効果、判断理由は 17)。
  - **WP-F 3c(-linkres)**: `GenerateMetadata` 後に `MetadataBuilder.AddAssemblyFile`(SHA1
    ハッシュ付き)+ `AddManifestResource`(File 実装参照)で実装。メタデータは ECMA-335 として
    正しい(SRM 検証・csc の /linkresource: と同型)が、**CoreCLR の
    Assembly.GetManifestResourceInfo/Stream は File 実装のリソースに null を返す**
    (マルチファイルアセンブリ非サポートのランタイム制約、ncc 側では解決不能 — Nemerle
    非依存 repro で確認)。GetManifestResourceNames の列挙は正常。受け入れ基準(a)は部分合格、
    (b)はランタイム制約により不能と記録。
  - 最終回帰: hello/hello2 両ランタイム(-debug 有無とも)、attributes-01/-03/-assembly、
    stage2/stage3 フルビルド 0 エラー、stage2/stage3 の ncc で hello 再コンパイル、
    testsuite positive 先頭60本 54/60(13 時点の 53/60 から attributes-01 分改善、
    残6件は既知のハーネス制約)— ALL PASS。
- 2026-07-12: WP-I2(配布形態の整備。フェーズ6)完了 → 詳細は
  `dotnet-port\DISTRIBUTION.md`。今回は ncc/lib/macros の .n ソースは一切変更せず、
  新規のパッケージング/スクリプト/サンプルのみを追加(既存 CLR4 ビルドに回帰なし)。
  要点:
  - **タスク1(`dotnet-port\pack-tool.ps1`)完了・完全動作**: `bin\...\core\Stage2`
    から自己完結の実行レイアウト `dotnet-port\dist\ncc\` を組み立てるスクリプトを新設。
    `ncc.exe` と同一バイト列の `ncc.dll` を追加するだけで `dotnet <dir>\ncc.dll <args>`
    が `dotnet exec` と同等に動くことを実証(`ncc.runtimeconfig.json` のベース名が
    拡張子非依存の "ncc" で両方をカバーするため、追加コード不要)。標準参照
    (`-use-loaded-corlib` + 実体分割アセンブリ + 自身の Nemerle.dll)を焼き込んだ
    `ncc.default.rsp`(ncc の `-from-file` は Getopt の `SubstitutionString` で、
    その場で再帰展開してから残りのコマンドラインのパースを続けるため、
    `-from-file:ncc.default.rsp -out:hello.exe hello.n` がそのまま動く)と、
    利便性ラッパー `ncc.cmd` を同梱。hello.n(`printf` のみ、実行時 Nemerle.dll 不要)・
    hello2.n(`Nemerle.Collections` の list/Map・generics、実行時 Nemerle.dll 必要)を
    任意ディレクトリから成功させて実証。
  - **既知の落とし穴**: `.ps1` ラッパーは不可 — PowerShell はスクリプト呼び出しの
    引数トークン化で ncc の `-name:value` 形式スイッチを分割/誤爆させる
    (`param()`・`$args`・`ValueFromRemainingArguments`・`--%` すべて試して全滅、
    `-out:hello.exe` が `-out`/`hello.exe` に割れる、または PowerShell 標準の
    `-OutVariable`/`-OutBuffer` と曖昧一致でエラー)。**`.cmd`(バッチ)の `%*` は
    無傷**(PowerShell から `.cmd` を呼んでも同様に無傷 — パラメーターバインダーは
    外部プログラム呼び出しには介入しないため)。ncc のような colon-value 形式 CLI を
    シェルラッパーで転送する際の一般的な注意点として記録。
  - **タスク2(`dotnet-port\Nemerle.Tool`)完了・PoC 成功**: `dotnet pack
    -p:PackAsTool=true` は常に自プロジェクトがビルドしたアセンブリをエントリポイントに
    固定する(既製の ncc.dll を直接指せない)ため、小さな C# シム
    (`Process.Start` + `ArgumentList` で ncc.dll を re-invoke、シェル再パースを経由
    しないため `.cmd` よりさらに堅牢)を挟み、pack-tool.ps1 のレイアウトを
    tool content として同梱する方式で実現。`PackageId=Nemerle.Ncc.DevTool`、
    `ToolCommandName=nemerle-ncc`。`dotnet pack` → `dotnet tool install --global
    --add-source <local> Nemerle.Ncc.DevTool` → `nemerle-ncc -out:hello.exe hello.n`
    → `dotnet exec hello.exe` を実機で確認済み(hello.n/hello2.n 両方)。
  - **タスク3(`dotnet-port\msbuild\Nemerle.Core.targets`)完了・PoC 成功**:
    `tools\msbuild-task\MSBuildTask.cs` は .NET 10 でビルド不可と再確認(基底クラス
    `Microsoft.Build.Tasks.ManagedCompiler` が現行 `Microsoft.Build.Tasks.Core` に
    存在しない、02-build-flow.md §6 の既存解析どおり、フルポートは別 WP)。
    代わりに軽量な `<Exec>` ベースで `CoreCompile` ターゲット自体を上書きする方式
    (旧 Ncc タスクも同じ拡張点を使っていた)を実装、`@(IntermediateAssembly)` を
    そのまま再利用することで `CopyFilesToOutputDirectory`/
    `GenerateBuildRuntimeConfigurationFiles`/`Clean`/増分ビルド判定を無改造で流用。
    サンプル `dotnet-port\samples\HelloCore\HelloCore.nproj` で `dotnet build` →
    `dotnet exec` 成功、2回目ビルドで `CoreCompile` が正しくスキップされることも確認。
    **発見した制約**: (1) プロジェクト拡張子が `.csproj` だと SDK が暗黙にインポートする
    `Sdk.targets`(→ Microsoft.CSharp.CurrentVersion.targets の csc ベース
    `CoreCompile`)が必ず本文中の `<Import>` より後に評価され、同名ターゲットの
    「最後の定義が勝つ」という MSBuild の規則により csc が勝ってしまう
    (実測: csc の `CS5001` エラーが出て csc が実際に動いていたことを確認)。
    → `.nproj`(非 `.csproj`)拡張子にすることで回避(副作用で
    `CreateManifestResourceNames` 等の言語ターゲット由来の補助ターゲットが
    無くなるため、空スタブで補った)。(2) 既定 true の `ProduceReferenceAssembly`
    (Roslyn `/refout:` 機能)を明示的に false にする必要あり(ncc は参照アセンブリを
    別途出力しないため)。残課題: `@(ReferencePath)` → `-ref:` の配線(複数
    プロジェクト間の参照は未検証)、`dotnet clean` が `bin\` 配下を完全に消さない。
  - 生成物 `dotnet-port\dist\`(pack-tool.ps1/dotnet pack の出力)は `.gitignore` に
    追加して既定で追跡除外。
- 2026-07-12: WP-J(overloading-01 regression 修正 + string-template-3 診断・修正)完了。
  詳細ログ: 19-overload-stringtemplate-log.md。要点:
  - **overloading-01(最優先タスク)**: `Typer-OverloadSelection.n` の
    `AintUsingDefaultParms` タイブレーク(WP-D で追加)が、BCL 由来の `Debug.Assert`
    曖昧性だけでなく `negative/overloading-01.n` の `Bug743(string)`/
    `Bug743(string, _ = "a")` というユーザー由来の意図的な曖昧オーバーロードまで
    無条件に解決してしまっていた既存 regression を修正。調査の結果、実際の C# も
    `Bug743("s")` 相当のコードは曖昧にならず暗黙に解決する(= Nemerle のこの
    negative テストは C# 互換ではなく Nemerle 独自の意図的により厳格なルール)ことを
    実験で確認。`OverloadResolutionPriorityAttribute` を読む案も検討したが
    `string.Split`/`StreamReader` 等 self-host で実際に踏む BCL ケースの一部に
    この属性が付いておらず不十分と判明。最終的に「候補が全員
    外部参照(BCL)メンバーの場合にのみ `AintUsingDefaultParms` タイブレークを
    発動する」ゲート(`IsExternalMember`/`PreferNonDefaultedExternalOverloads`)を
    追加する設計を採用 ── ユーザー宣言オーバーロードには一切影響せず、BCL 由来の
    曖昧性(Debug.Assert 含め、self-host 中に新規発見した `ncc/parsing/Lexer.n` の
    `Split(null)`、`ncc/parsing/Source.n` の `StreamReader` 構築)はすべて解決する。
    受け入れ: negative/overloading-01.n が CLR4 . stage2 の両方で期待どおり
    全7箇所の曖昧エラーを出力(line 111 の Bug743 含む)、かつ stage2/stage3
    フルビルドが 0 エラー(Debug.Assert 系の曖昧性が再発しないことを証明)。
    testsuite 全数実行で negative 166/167 PASS(唯一の失敗は既知の
    notifypropertychanged.n、無関係)、positive は既知の環境制約34件のみ
    (新規 regression なし)。
  - **string-template-3(調査+修正)**: 内部コンパイラーエラー
    `TargetInvocationException`(内側は `InvalidOperationException: Method body
    should not exist.`、`MethodBuilderImpl.GetILGeneratorCore` から)の原因を、
    一時デバッグフックで特定。`macros/string.n` の `StringTemplateGroup` マクロが、
    文字列テンプレートメソッドごとに生成する `<Method>__StImpl` コンパニオンの
    修飾子を組み立てる際、元メソッドが `abstract` の場合その `Abstract` 修飾子を
    そのままコピーしていたが、コンパニオン自身の AST は常に実体を持つ空 `{ }`
    ボディ(= `FunBody.Typed`)であるため「Abstract フラグ + 実ボディ生成要求」
    という矛盾した `MethodBuilder` を生成していた。CLR4 の Reflection.Emit は
    これを黙って許していた(コンパニオンは呼ばれないため実害なし)が、
    PersistedAssemblyBuilder ベースの CoreCLR `MethodBuilderImpl` は
    `GetILGenerator()` 時点で ECMA-335 準拠のより厳格な検証を行い例外を投げる ──
    self-host/emission の問題ではなく `macros/string.n` に元から存在した
    (CoreCLR の厳格化で初めて露呈した)バグ。コンパニオンの修飾子から `Abstract`
    を除去し、代わりに元が `Abstract` の場合は `Virtual` を明示的に補うよう修正
    (派生クラスの override 側コンパニオンが正しく override できるようにするため)。
    受け入れ: string-template-3.n が CLR4/stage2 の両方でコンパイル・実行成功、
    出力が BEGIN-OUTPUT/END-OUTPUT と完全一致。string-template-1/2 に回帰なし。
  - 変更ファイル: `ncc/typing/Typer-OverloadSelection.n`、`macros/string.n`
    (診断用に `ncc/main.n`/`ncc/generation/ILEmitter.n` を一時変更したが最終的に
    元へ復元、両ファイルとも無変更)。
  - stage2/stage3 フルビルド 0 エラー再確認、hello.n/hello2.n 両ランタイム回帰なし、
    testsuite 全数実行で新規 regression なし(positive 434/469, negative 166/167 ──
    18-testsuite-log.md 時点の 431/469, 165/167 から string-template-3 と
    overloading-01 の分だけ改善、失敗の残りは全て既知の環境・ハーネス制約)。
- 2026-07-13: WP-A2(dotnet ネイティブ化「レベル A」= フロントエンド)完了。ncc 自体を
  native 化する方針(corencc 併存は不要)のもと、以下を実施:
  - **参照自動解決・rsp 撤廃**(コミット `56964d879`): `ncc/passes.n` の
    `LoadExternalLibraries` に `CoreEmitBridge.IsCoreClr` 分岐を追加し、新メソッド
    `LoadCoreStdlibReferences()` が CoreCLR 時に `UseLoadedCorlib=true`・
    `GreedyReferences=false` を設定、mscorlib/System(loaded 実体)+ 実行中共有
    フレームワークの split アセンブリ11個 + Nemerle を自動 `-ref`。`dotnet ncc.dll
    hello.n` が `ncc.default.rsp` なしで動くようになった。`-no-stdlib` は従来通り
    エスケープハッチとして残し、`build-stage2-core.ps1` 等は無影響。フォローアップ
    (コミット `041986537`)で `Nemerle.Core.targets` から不要になった
    `NccDefaultRsp` プロパティを削除。
  - **参照配線**(コミット `ee2ae06f3`): `Nemerle.Core.targets` が `@(ReferencePath)`
    を `-ref:` として ncc に渡すよう配線。フレームワーク ref パック facade は
    `%(ReferencePath.FrameworkReferenceName) == 'Microsoft.NETCore.App'` で除外
    (ncc がフレームワーク実体を自動解決するため)。新サンプル
    `dotnet-port/samples/RefDemo`(MathLib→App の ProjectReference)で
    `dotnet build App.nproj` の成功を実証。
  - **`-debug`/PDB + `dotnet clean` 対応**(コミット `0de978022`):
    `DebugType`/`DebugSymbols` を見て `-debug` を ncc に渡す配線を追加。ncc の
    standalone Portable PDB は SDK 期待パスにそのまま一致するため copy/clean は
    共通ターゲットが自動処理。ランタイム dll コピーを
    `AfterTargets=CopyFilesToOutputDirectory` に移し `@(FileWrites)` へ登録したことで
    `dotnet clean` が `bin\` を完全に空にできるようになった(HelloCore/RefDemo で検証)。
  - **`dotnet tool` 更新**(コミット `06d9aa375`): `Nemerle.Tool` シムを rsp フリー化し
    `<Version>` を 0.2.0-poc1 に bump。install→`nemerle-ncc -out:x.exe hello2.n`
    (rsp なし)の compile+run を実証。
  - **Linux 版 targets**(コミット `253d2ecb3`): `dotnet-port/msbuild/linux/
    Nemerle.Core.targets` を新設(差分は NccLayoutDir 既定・`dotnet exec` 起動・
    スラッシュパスの3点のみ)。**WSL で end-to-end 実証**: Windows でビルドした core
    ncc の DLL 群がそのまま Linux 上で `dotnet build`→`dotnet exec` まで成功(managed
    IL は OS 非依存、Windows ネイティブ P/Invoke 無し)。
  - 残項目(b)「インプロセスホスト(MSBuild タスク直呼び)」は独立タスクとして
    分離し WP-A3 で完遂(次項)。詳細は `DISTRIBUTION.md` の更新注記を参照。
- 2026-07-13: WP-A3(in-process MSBuild task)完了。`Nemerle.Compiler.Hosting` と
  `Nemerle.MSBuild.Tasks` により、SDK-style `.nproj` の `dotnet build` が compiler API を
  collectible AssemblyLoadContext 内で直接呼び、構造化 diagnostics を MSBuild へ返す。
  ProjectReference、macro-only reference、PDB、incremental build/clean、Exec fallback まで検証済み。
  詳細は `20-inproc-task-plan.md` / `20-inproc-task-log.md`。
- 2026-07-13: WP-K(LSP feasibility)完了。WinForms/CodeDom 系を除いた IDE engine を
  .NET 10 で headless build/run し、OmniSharp 0.19.9 を使う最小 stdio LSP server を追加。
  open/change/close と publishDiagnostics、未保存 buffer の型診断を実プロセス integration test で
  確認した。詳細は `21-lsp-feasibility.md` / `22-lsp-step1-log.md` / `23-lsp-step2-log.md`。
- 2026-07-13: コンパイラー移植後の次期作業として、VS Code extension と project-aware LSP により
  .NET 10 Nemerle の実用的な編集・build/run loop を作る WP-L を計画。
  原コンパイラー移植計画と成果物/完了条件が異なるため、詳細は
  `24-vscode-development-plan.md` に分離した。
- 2026-07-13: WP-L2(project information provider)完了。`dotnet msbuild` の JSON query から
  source / ProjectReference・PackageReference output / macro-only reference / define・option を
  OmniSharp 非依存 snapshot に正規化し、VS Code の単一 project discovery/selection、status、
  reload、cache/single-flight、debounce、timeout/cancellation、workspace trust gate を実装した。
  HelloCore/RefDemo/Newtonsoft.Json fixture/Sokoban、失敗後の LSP 継続、trusted/untrusted
  Extension Host を実測。snapshot の engine 適用は WP-L3 へ明示的に残した。
  詳細は `26-vscode-project-info-log.md`。
- 2026-07-14: WP-L3(project-aware engine workspace)完了。WP-L2 snapshot を
  `WorkspaceManager` / `IIdeProject` adapter 経由で analysis engine に適用し、
  project 全 source(open buffer が disk-backed text より優先)、resolved assembly
  reference、macro-only reference(assembly reference に混入しない)を使う
  project-aware diagnostics を実装。didClose の disk 復帰、解析済み version 照合による
  stale diagnostics 抑止、`.nproj`/targets/assets の force reload と on-disk `.n`/参照 dll
  の cache-hit 再適用の watch、失敗 reload の回復可能化を、HelloCore/RefDemo/
  PackageReference/Sokoban と一時 project の raw LSP シナリオおよび Extension Host で
  実測した。extension 0.3.0 は適用状態を status に表示する。
  詳細は `27-vscode-project-workspace-log.md`。
- 2026-07-14: WP-L4(packaging と end-to-end test)完了。WP-L はこれで全完了。
  extension 0.4.0 は `pack-server.ps1` が staging する LspServer の Release 出力
  ディレクトリ全体(71 files。CoreEmit は emission 専用のため不要と実測で確定)を
  VSIX の `server/` に同梱し、既定で bundled server を起動(`nemerle.server.path` は
  開発 override)、起動前に `dotnet --list-runtimes` で .NET 10 runtime を検査する。
  隔離 extensions-dir/user-data-dir に VSIX を install して bundled server が無設定で
  project-aware diagnostics を出す clean-machine 相当テスト(`npm run test:vsix`)と、
  VSIX 抽出 server だけで WP-L3 raw LSP 全シナリオを回す `test-bundled-server.ps1` を
  追加。`THIRD-PARTY-NOTICES.md` + `npm run verify-server` で同梱物の license 記録を
  機械検証する。全 build 0 warning、unit 21/21、Extension Host trusted 3/3 +
  untrusted 1/1 + vsix 1/1、npm/NuGet audit 0 件。詳細は `28-vscode-packaging-log.md`。
- 2026-07-14: WP-L 完了を受け、次期フェーズ WP-M(.NET 10 Nemerle 開発環境2)を計画。
  スコープは WP-M1 IDE/build parity(DefineConstants 配線・警告 N コード・server ログの
  `window/logMessage` 化)→ WP-M2 hover → WP-M3 completion → WP-M4 definition/references →
  WP-M5 incremental rebuild(engine 既存の relocation 経路の活用)→ WP-M6 `Nemerle.Sdk`
  NuGet package + `dotnet new` template + toolchain provenance。semantic tokens /
  formatting / multi-root / Linux 実地検証 / Marketplace は WP-M 完了後の優先順位リストへ。
  詳細は `29-devenv2-plan.md`。
- 2026-07-14: WP-M1(IDE/build parity と server ログの地固め)完了。(1) MSBuild
  `DefineConstants` を ncc に `-define:` 配線(`Nemerle.Core.targets` Windows/Linux +
  in-process `NccCompile` + Exec fallback)し、`EngineWorkspaceInputs` が engine にも同集合を
  適用(WP-L3 の gap warning 撤去)。(2) `ncc/parsing/Utility.n` の 1 行修正(N コード prefix を
  `RunWarningOccured` の前へ)で警告コードを構造化: build は `warning N####:`(コード欄)、
  IDE は LSP `Diagnostic.code`。(3) server の情報 trace を `window/logMessage`(Log/Error)へ移し
  stderr を initialize 前 fatal 専用に(`ServerLog`、ServerInfo 0.3.0)。新 fixture
  `samples/Defines`・`samples/Warnings`。Stage1(full rebuild で version 601 に整合)→Stage2→
  Stage3 を 0 error、testsuite 新規 regression 0。raw LSP 7/7、`npm test` 21/21、
  Extension Host trusted 3 + untrusted 1 + vsix 1、`test-bundled-server` 7/7、audit 0 件。
  詳細は `30-devenv2-wp-m1-log.md`。
- 2026-07-14: WP-M2(hover + EngineRequestBridge)完了。(1) `EngineRequestBridge`(§6.2): IDE
  engine の `Begin*` を await 可能な `Task` 化する汎用境界。`_engineOperations` lock 内で enqueue し
  lock 外で完了をポーリング検知、AsyncWorker force-out(`Stop`)と document version を尊重して
  cancel/stale を捏造せず返す。LSP `CancellationToken` を `Stop` に写像、10 s timeout。M3/M4 が再利用。
  (2) 擬似 markup → `MarkupContent` 変換の純関数 `HoverMarkup`(ProjectInfo、unit test 化): `<lb/>`→
  改行、装飾 tag 除去、HtmlMangling 復元、markdown は Nemerle fenced code block で包みメタ文字を
  literal 化、client 非対応時は plaintext。(3) `NemerleHoverHandler`(`textDocument/hover`)+
  `NemerleProject.GetHoverAsync`: `BeginGetQuickTipInfo` 配線、`QuickTipInfo.Location`→0-origin
  UTF-16 range。extension は capability 追従(無変更)、ServerInfo 0.4.0、README 更新。compiler/
  engine 無改造で Stage リビルド不要。raw LSP 13/13(既存 7 + hover 6)、bundled server 13/13、
  `ProjectInfo.Test` unit(`HoverMarkupTests`)+ integration、`npm test` 21/21、Extension Host
  trusted 3 + untrusted 1 + vsix 1、audit 0 件。warm hover **p50 31 ms**(目標 < 300 ms)。
  詳細は `31-devenv2-wp-m2-log.md`。
- 2026-07-14: WP-M3(completion)完了。(1) `NemerleCompletionHandler`
  (`textDocument/completion`、triggerCharacters `.`)+ `completionItem/resolve`
  (`resolveProvider: true`)。engine 無改造のまま、公開 `CompletionAsyncRequest` +
  `AsyncWorker.AddWork` で `BeginCompletion` を再現し WP-M2 の `EngineRequestBridge` を再利用
  (async。reload 中の completion は force-out → cancel、version 照合で stale を捏造しない)。
  (2) glyph → kind 変換の純関数 `CompletionMapping.GlyphToKind`(ProjectInfo、unit test 化)。
  detail/documentation の擬似 markup は `HoverMarkup` を再利用して除去。(3) 高コストな
  Description(overload 列挙・XmlDoc)は resolve 側で遅延計算し、直近 1 世代を cache
  (completion / engine reload で世代を進め、古い世代の resolve は documentation なし)。
  extension は capability 追従(無変更)、ServerInfo/extension 0.5.0、README 更新。compiler/
  engine 無改造で Stage リビルド不要(assembly version 601 のまま)。raw LSP 18/18
  (既存 13 + completion 5)、bundled server 18/18、`ProjectInfo.Test` unit
  (`CompletionMappingTests`)+ integration、`npm test` 21/21、Extension Host
  trusted 3 + untrusted 1 + vsix 1、audit 0 件。warm completion **p50 31 ms / p95 61 ms**
  (目標 < 500 ms)。詳細は `32-devenv2-wp-m3-log.md`。
- 2026-07-14: WP-M4(definition / references)完了。(1) `NemerleDefinitionHandler`
  (`textDocument/definition`)+ `NemerleReferencesHandler`(`textDocument/references`):
  同期 API `GetGotoInfo(source, line, col, GotoKind.Definition|Usages)` を bridge の async 経路
  ではなく `_engineOperations` lock 内で直接実行(§6.2「同期 API は lock 下で直列化」)。engine 無改造。
  (2) `GotoInfo`(fileIndex ベース 1-origin `Location`)→ LSP `Location[]` 変換の純関数
  `GotoMapping`(ProjectInfo、unit test 化): 1→0-origin・UTF-16、URI は `ProjectPathNormalizer`
  共用で drive letter 大文字化、metadata(`FileIndex ≤ 0`)member は除外、`includeDeclaration`
  で宣言エントリを取捨、重複 collapse。(3) 外部 assembly member への definition は空結果 +
  `logMessage`(Info)(生成 source 表示は非ゴール)。references は `context.includeDeclaration` を尊重。
  extension は capability 追従(TypeScript は definition 検証 test のみ追加)、ServerInfo/extension
  0.6.0、README 更新。compiler/engine 無改造で Stage リビルド不要(assembly version 601 のまま)。
  raw LSP 23/23(既存 18 + definition/references 5)、bundled server 23/23、`ProjectInfo.Test` unit
  (`GotoMappingTests`)+ integration、`npm test` 21/21、Extension Host trusted 4(定義 provider の
  `vscode.executeDefinitionProvider` 実クリック相当を追加)+ untrusted 1 + vsix 1、audit 0 件。
  詳細は `33-devenv2-wp-m4-log.md`。
- 2026-07-15: WP-M5(incremental rebuild / relocation + 応答性計測)完了。(1) document sync を
  `TextDocumentSyncKind.Incremental` に切替え、range 付き change → engine の relocation 経路
  (`BeginUpdateCompileUnit`)へ配線。method body 内編集は該当 method のみ再型付け(full rebuild
  不発)、構造変化・relocation 失敗は engine 判定で full types-tree rebuild へ fallback。
  (2) UTF-16(0-origin)→ 1-origin の change → `RelocationRequest` 変換とバッファ range 適用を
  `ProjectInfo.IncrementalSync`(純関数、`IncrementalSyncTests` で unit 化)に固定。
  (3) fallback 駆動は `BeginUpdateCompileUnit` 完了後に `ProcessPendingTypesTreeRequest` を1回呼ぶ
  monitor(relocation 成立時は no-op)。interface 既存 API のみ使用で **engine 無改造**。
  (4) escape hatch `NEMERLE_INCREMENTAL_UPDATE=0` で full sync + 従来 reload に復帰。
  (5) 実測(Sokoban、debounce 込み edit-to-diagnostics):incremental ON p50 **327 ms** /
  OFF p50 556〜590 ms(目標 p50 ≤ 400 ms 達成、full 比 約 45% 短縮)。
  extension は sync 切替 capability 追従で TypeScript src 無変更、ServerInfo/extension 0.7.0、
  README 更新。compiler/engine 無改造で Stage リビルド不要(assembly version 601 のまま)。
  raw LSP 27/27(既存 23 + incremental 4)、bundled server 27/27、`ProjectInfo.Test` unit + integration、
  `npm test` 21/21、Extension Host trusted 4 + untrusted 1 + vsix 1、audit 0 件。
  詳細は `34-devenv2-wp-m5-log.md`。
- 2026-07-15: WP-M6(`Nemerle.Sdk` NuGet package + project template + provenance)完了 =
  **WP-M 完了**。(1) MSBuild project SDK 方式の **`Nemerle.Sdk.Unofficial`** と、別 package の
  **`Nemerle.Templates.Unofficial`**(`nemerle-console` / `nemerle-classlib`)を
  `pack-tool.ps1 -Pack` が生成。**repo checkout 無しで** `dotnet new nemerle-console` →
  `dotnet build` → `dotnet run` が Windows・WSL の両方で成立(local feed のみ、実測)。
  project file は `<Project Sdk="Nemerle.Sdk.Unofficial/1.2.601-preview.1">` + OutputType +
  TargetFramework の 4 行で済む(`**/*.n` は SDK が glob)。
  (2) **targets を 1 本に統合**。§6.7 は「Windows/Linux 2 系統を package 内で条件分岐統合」を
  想定していたが、Linux 版は **importer がゼロ**・既定 layout dir(`msbuild/ncc/`)が不在の
  デッドコードで、差分 3 点はいずれも不要と実測で判明(WSL の repo checkout ビルドが
  Windows 版 targets の `../dist/ncc/` 既定でそのまま成功)。よって条件分岐ではなく**削除**し、
  `msbuild/Nemerle.Core.targets` 1 本を repo(両 OS)と package が共有する
  (package は**バイト同一**で同梱、test で assert)。既存 importer 14 本への影響ゼロ。
  (3) 配布方針を Project Owner と合意: ID は既存 nuget.org 公開物(`Nemerle.Unofficial` 等、
  1.2.547)と同じ「公式名 + `.Unofficial`」規約、version は同規約の `1.2.<Nemerle.dll revision>` +
  prerelease label = `1.2.601-preview.1`(出所は恒久的事実なので ID、成熟度は一時的なので
  prerelease label)。GitHub Packages は public でも PAT 必須のため不採用。公開は local feed 止まり(WP-N)。
  (4) provenance: `dist/ncc/ncc-info.json` を追加し package にも同梱。snapshot に `NccLayoutDir` を
  追加し、server が toolchain と自身の Nemerle.dll 版を比較して不一致時のみ
  **`window/showMessage`(Warning)**。§6.8 は「extension が警告」を想定していたが showMessage は
  protocol 通知なので server から直接出せ、**extension TS src 無変更**と受け入れ基準 6 を両立。
  (5) 検証中に実バグ 2 件を発見・修正: 既存 project の Sdk 形式**変換**時に source が二重に渡る
  (default glob を `Sdk.props` → `Sdk.targets` へ移し `Exclude="@(NemerleCompile)"`)、
  macro library が compiler を参照できない(`<NemerleMacroLibrary>true</NemerleMacroLibrary>` を新設。
  package 内 dll の path はユーザーには書けないため)。
  ServerInfo/extension 0.8.0、compiler/engine 無改造で Stage リビルド不要(assembly version 601)。
  raw LSP 29/29(既存 27 + provenance 2)、bundled server 29/29、`ProjectInfo.Test` unit + integration
  (provenance / package layout / targets byte 同一性 / SDK 評価構造)、`npm test` 22/22、
  Extension Host trusted 4 + untrusted 1 + **sdk 1(新規 `test:sdk`)** + vsix 1、audit 0 件、
  `dotnet list package --vulnerable` 全 7 project clean。詳細は `35-devenv2-wp-m6-log.md`。
- 2026-07-19: **WP-N4 完了**(版タグ契約 + GitHub Release 初回発行)。describe レシピに
  `--match "v[0-9]*"` を全 8 箇所(macro / A2 replay / MSBuild task / 世代比較 2 / provenance 3)へ
  同期し、`release/1.2.<rev>-preview.<N>`(実ソースコミット)・`seed/1.2.<rev>`(orphan seed)の
  v 非開始 lightweight タグ契約を確立。publish-boot が seed タグを付与、pack-release が
  release-info.json に seed 対応(orphan hash / タグ / 世代)を記録、build-from-boot に
  `-Seed` / `-ReleaseTag` / `-PackageVersionSuffix`(発行済みリリースの再現経路、常時 pinned
  worktree)。世代 635(0cab66afe)で Stage1 リビルド → §5-2 回帰ゲート全 green
  (stage2×2 / stage3==stage4 バイト一致、testsuite 614/636 = baseline、CLR4 スモーク)→
  seed refresh(seed/1.2.635)→ **release/1.2.635-preview.1 を GitHub Release(prerelease)
  として発行**。GitHub asset のみからの install→new→build→run、GitHub からの新規 clone +
  `-ReleaseTag` による再現まで検証。発見: boot-4.0 の旧レシピ焼き込み(release タグ可視だと
  Stage1 リビルド不可 — 一時退避運用、根治は WP-N7)ほか。詳細は `41-prerelease-wp-n4-log.md`。
