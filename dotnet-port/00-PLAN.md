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
