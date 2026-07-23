# 14 — WP-E: `-debug`(PDB 出力)の CoreCLR 配線 — 反復ログ

ゴール: CLR4 では ISymbolWriter 系 API で PDB を出していた ncc の `-debug` を、
CoreCLR(.NET 10)パスでも機能させる(WP-B 以降 warn-and-skip で無効化されていた)。
03-dotnet-runtime-facts.md の R4 レシピ(`GenerateMetadata` 3-out オーバーロード +
`PortablePdbBuilder` + `DebugDirectoryBuilder`、standalone Portable PDB)を実配線する。

## 実装アーキテクチャ

配線は3層(WP-B で確立したパターンの延長):

1. **Nemerle.CoreEmit.Emitter.Save**(net10 C#)— `emitDebug : bool` パラメーターを追加。
   true のとき `GenerateMetadata(out il, out fieldData, out pdbMetadata)`(3-out)で
   PDB メタデータを受け取り、`PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(),
   entryPointHandle)` を Serialize → `<出力名>.pdb` を出力の隣に書き、
   `DebugDirectoryBuilder.AddCodeViewEntry(pdbのフルパス, pdbId, FormatVersion)` を
   `ManagedPEBuilder(debugDirectoryBuilder:)` に渡す。false のときは従来どおり 2-out
   (非 debug 出力はバイト単位で従来と同一経路)。
2. **CoreEmitBridge.Save** — `emitDebug` を追加してリフレクション引数配列に積むだけ。
3. **HierarchyEmitter.SaveAssemblyCore** — `Manager.Options.EmitDebug` を渡す。
   **CreateModuleCore** — warn-and-skip を撤去(コメントだけ更新)。CoreCLR の
   persisted ModuleBuilder は `emitSymbolInfo` フラグ相当が不要で、DefineDocument /
   MarkSequencePoint / SetLocalSymInfo を常時受け付ける。`_debug_emit`(legacy
   ISymbolWriter、CLR4 で SetUserEntryPoint にのみ使用)は core では null のまま。

フロントエンド(Typer の TExpr.DebugInfo 生成)と ILEmitter の MarkSequencePoint /
SetLocalSymInfo 呼び出しはランタイム非依存で、**そのまま無改造で core でも機能した**
(作業指示の見立てどおり)。

## 発見した事実(予想外のもの含む)

### F1. レガシー SymbolStore の GUID ホルダー型は .NET 10 にまだ「存在する」

`SymDocumentType`/`SymLanguageType`/`SymLanguageVendor` は
**`System.Diagnostics.StackTrace.dll`**(共有フレームワーク内)に実在し、mscorlib
ファサードから型転送されている(`Type.GetType("System.Diagnostics.SymbolStore.
SymDocumentType, mscorlib")` が .NET 10 で成功)。4引数の
`ModuleBuilder.DefineDocument(url, Guid, Guid, Guid)` も core に存在する。
→ **NET_4_0 でコンパイルされた Stage1 バイナリーの既存 CLR4 呼び出しは、CoreCLR 上でも
そのまま JIT・実行できる**(実測で確認)。WP-D の `#if NET_4_0` ガードが必要だったのは
あくまで「stage2 の参照セットで*型検査*できない」(System.Diagnostics.StackTrace.dll は
stage2 rsp に含まれない)からで、実行時解決とは別問題 — 13-stage2-log.md の
「実行されないことと型検査できることは別」の逆パターン(型検査できないが実行はできる)。

### F2. 元コードの DefineDocument は引数順が昔から間違っている(無害・踏襲)

`ILEmitter.DefineDebugDocument` は `DefineDocument(url, SymDocumentType.Text,
SymLanguageType.ILAssembly, SymLanguageVendor.Microsoft)` と呼ぶが、API の引数順は
`(url, language, languageVendor, documentType)`。つまり **language の位置に
SymDocumentType.Text(5a869d0b-6611-11d3-bd2a-0000f80849bd)を渡している**。歴代の
Nemerle PDB はすべて言語 GUID = Text で出ており、デバッガー実害はない。core フレーバー
(`#else`)の 2引数 `DefineDocument(url, language)` でも**同じ GUID を渡して挙動を踏襲**
(stage1-on-core と stage2+ の PDB 出力を同一に保つため。「正しい」ILAssembly GUID に
直すのは全フレーバー一斉にやるべき将来課題)。

### F3. core の DefineDocument は URL で重複排除しない → モジュール単位キャッシュが必須

CLR4 の ISymbolWriter.DefineDocument は同一 URL を内部で重複排除するが、CoreCLR の
persisted ModuleBuilder.DefineDocument は**呼ぶたびに新しい Document 行を追加する**。
ILEmitter はメソッドごとに1インスタンス生成され `DefineDebugDocument` のメモ化は
インスタンススコープなので、素朴に配線すると **PDB にメソッド数ぶんの重複 Document 行**
が入る(thrower.n の3メソッドで3行になるのを実測)。大きいアセンブリでは PDB 肥大化と
デバッガー混乱のもと。→ `TypesManager._core_debug_documents : Hashtable[int(fileIndex),
ISymbolDocumentWriter]`(core 専用、CLR4 では常に空)を追加し、`DefineDebugDocumentCore`
がモジュール単位で重複排除。修正後は Document 1 行を確認。

### F4. stage2 ビルドの「バージョン境界」ハザード(WP-E 無関係の既存問題)

stage2 再実行時、`Nemerle.Compiler.dll` のビルドが
`FileLoadException: manifest definition does not match (0x80131040)` で失敗した。
原因: アセンブリバージョンは `GeneratedAssemblyVersion("$GitTag.0.$GitRevision")` で
**`git describe --tags --long` のコミット数**から生成される。Stage1 の Nemerle.dll は
インクリメンタルビルドで再コンパイルされず旧コミット時点の 1.2.0.**576** のまま、stage2 が
新規生成する Nemerle.dll は 1.2.0.**577**。コンパイラープロセス(Stage1)には既に
Nemerle 576 がロード済みで、`-ref:` の Stage2\Nemerle.dll(577)を
`AssemblyLoadContext.LoadFromAssemblyPath` すると単純名衝突・上位バージョンで
ref-def mismatch になる。WP-D は「Stage1 とその時の stage2 が同一コミットでビルドされて
いた」ため偶然一致して通っていた。対処: `lib/macros/ncc の AssemblyInfo.n` を touch して
Stage1 をフルリビルド(577 に揃える)。**恒久対策(将来課題): build-stage2-core.ps1 に
Stage1 と HEAD のバージョン一致チェックを入れる**か、バージョンを固定する。

### F5. 例外スタックトレースの行帰属は CLR4 と CoreCLR で微妙に違う(回帰ではない)

同じ thrower.n を -debug でコンパイルすると、throw 地点のフレームが CoreCLR では
`line 8`(`when` の条件)、.NET Framework では `line 10` と報告される。boot-4.0 の
ncc(WP-E 以前)で作った CLR4 バイナリーも同じ line 10 で、**コンパイラー側の
シーケンスポイントは両ランタイムの PDB で完全一致**(SRM ダンプで確認)。差は各
ランタイムの例外 IL オフセット→シーケンスポイント帰属の実装差。

## 変更ファイル

- `dotnet-port\Nemerle.CoreEmit\Emitter.cs` — `Save` に `emitDebug` 追加、R4 レシピ実装
  (3-out GenerateMetadata / PortablePdbBuilder / standalone .pdb / AddCodeViewEntry)。
- `ncc\generation\CoreEmitBridge.n` — `Save` に `emitDebug` パラメーター追加。
- `ncc\generation\HierarchyEmitter.n` — `CreateModuleCore` の warn-and-skip 撤去、
  `SaveAssemblyCore` が `Manager.Options.EmitDebug` を渡す、
  `TypesManager._core_debug_documents` フィールド追加(F3)。
- `ncc\generation\ILEmitter.n` — `DefineDebugDocument` をランタイムディスパッチ化:
  `DefineDebugDocumentClr4`(旧本体を一字一句そのまま退避、`#if NET_4_0` ガード)+
  `DefineDebugDocumentCore`(モジュール単位キャッシュ。NET_4_0 フレーバー = Stage1 では
  従来の4引数 DefineDocument(F1 により core でも動く)、core フレーバーでは2引数
  DefineDocument + Text GUID(F2))。

CLR4 専用コードパスは一字一句無変更(DefineDebugDocument の旧本体は verbatim 移設のみ)。

## 受け入れテスト結果(6項目)

テストソース: hello.n / hello2.n(リスト+ラムダ+stdlib)/ thrower.n(例外+ローカル変数)。
scratchpad\wp-e\tests\ に配置。検証ツール: scratchpad\wp-e\PdbVerify(net10、SRM で
CodeView GUID 突合・Document・シーケンスポイント・ローカル名をダンプ)。

1. **PASS** — `dotnet exec Stage1\ncc.exe -debug -out:X.exe *.n` が .NET 10 上で成功し、
   出力の隣に standalone Portable PDB(hello: 520B、thrower: 700B)が生成される。
   stage2 の ncc でも同様(throwerS2.pdb)。
2. **PASS** — PdbVerify(`MetadataReaderProvider.FromPortablePdbStream`)で確認:
   PE の CodeView GUID と PDB ID が一致、Document = thrower.n のフルパス(1行のみ)、
   シーケンスポイント(Boom: 6,7,8,hidden,10,11 行 / Main: 14〜18 行、ラムダの
   apply にも 3,21-3,26)、ローカル名 `y`(SetLocalSymInfo 経由)。
3. **PASS** — thrower.n を -debug でコンパイル → `dotnet exec` 実行で
   `at Thrower.Boom(Int32 x) in ...\thrower.n:line 8` / `at Thrower.Main() in
   ...\thrower.n:line 16` とファイル名+行番号が出る(stage1-on-core / stage2 両方)。
4. **PASS** — CLR4 ネイティブ Stage1\ncc.exe で -debug: hello4.exe+hello4.pdb(11776B、
   Windows PDB)生成・実行 OK、thrower4.exe のスタックトレースに thrower.n:line 10 /
   line 16(boot-4.0 コンパイラーのベースラインと完全一致 = 回帰なし)。
5. **PASS** — -debug なし: hello.n / hello2.n が CLR4・.NET 10 の両方で従来どおり
   コンパイル・実行、余計な .pdb は生成されない(非 debug 経路は 2-out のまま)。
6. **PASS** — `build-stage2-core.ps1` 成功(4アセンブリ 0 エラー、F4 対処後)。
   さらに stage3(`-Compiler Stage2\ncc.exe`)も 0 エラーで成功。

## 残課題

- **埋め込み PDB オプション**(`AddEmbeddedPortablePdbEntry`): 未配線。ncc に
  `-debug:embedded` 相当のスイッチ自体がないので、必要になったら CLI から。
- **Document チェックサム / SourceLink**: SRE の DefineDocument はチェックサム情報を
  受け取れないため、Roslyn の PDB にある Document ハッシュは入らない(デバッガーの
  ソース一致検証が緩くなるだけで機能はする)。
- **`-compile-to-memory` + `-debug` on core**(RunAndCollect ビルダーでの
  MarkSequencePoint): 未検証(テストハーネス未移植のため、WP-B から継続)。
- **F4 のバージョン境界ハザード**: build-stage2-core.ps1 での事前チェック未実装。
- 言語 GUID の是正(F2)と、stage2/stage3 の非 MVID 差分・`attributes-01.n` などの
  既知課題は WP-D から変わらず。
