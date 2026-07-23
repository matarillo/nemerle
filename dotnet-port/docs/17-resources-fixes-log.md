# 17 — WP-H/WP-G/WP-F: 決定性修正・attributes-01 修正・リソース配線 — 実装ログ

日付: 2026-07-12 / 対象: `wip/dotnet-port` ブランチ、直前の 14-pdb-log.md（WP-E）からの続き。

作業指示は3サブタスク(WP-H 修正、WP-G 修正、WP-F 本体)。この順で実装し、各サブタスク完了ごとに
回帰確認(hello.n/hello2.n 両ランタイム)を行った。最終回帰は末尾にまとめる。

## サブタスク1: WP-H 修正(Nemerle.Macros.dll の決定性、1行)

`16-determinism-diagnosis.md` の診断どおり、`ncc\hierarchy\MacroClassGen.n:749` の

```nemerle
Util.tmpname ($"operator$(x.GetHashCode())");
```

を

```nemerle
Util.tmpname ("operator");
```

に置換(案A、診断レポート推奨)。一意性は `Util.tmpname` 内部の `GetNewId()` 連番が既に保証しており、
`GetHashCode()` は装飾でしかなかった(診断で確認済み)。CoreCLR の文字列ハッシュはプロセスごとに
ランダム化されるため、この値を生成クラス名に埋め込むと毎ビルドで名前が変わり、
`Nemerle.Macros.dll` のメタデータヒープ全体がオフセットずれで非決定的になっていた。

### 受け入れ結果

Stage1 リビルド後、`dotnet-port\build-stage2-core.ps1`(`GitTag=1.2`/`GitRevision=577` を環境変数固定、
16 の診断が指摘したバージョン境界ハザード対策)で stage2 を **2回独立ビルド**し、
`scratchpad\wp-h\difftool`(診断エージェントの残骸、`PeDiff.csproj` に `EnableDefaultCompileItems=false`
を足して同ディレクトリの `HeapDiff` サブプロジェクトとの多重トップレベルステートメント衝突を解消して再利用)
で MVID/PE タイムスタンプをマスクして比較:

| ファイル | 結果 |
|---|---|
| `ncc.exe` | **IDENTICAL (masked)** |
| `Nemerle.dll` | **IDENTICAL (masked)** |
| `Nemerle.Compiler.dll` | **IDENTICAL (masked)** |
| `Nemerle.Macros.dll` | **IDENTICAL (masked)** |

診断時点(16 の記録)では Macros 以外の3本は既に決定的だったが、今回は **4本すべて** が
バイト一致(マスク後)。修正は1行のみで CLR4 パスも無条件で同じコードを通る
(診断の判断どおり、生成名はもともと非本質的な装飾値でありランタイム分岐は不要)。

## サブタスク2: WP-G 修正(attributes-01.n の CustomAttributeBuilder)

`15-attributes01-diagnosis.md` の差分案(§6)をほぼそのまま実装。

### 実装

1. **`dotnet-port\Nemerle.CoreEmit\AttributeBlob.cs`(新規)**: `NeedsWorkaround` が
   コンストラクター引数/名前付きプロパティ/名前付きフィールドの型に「TypeBuilder な enum
   (配列要素含む)」があるかを判定。該当する場合のみ ECMA-335 §II.23.3 の CA blob を手組みし
   (corelib の `EmitType`/`EmitValue`/`EmitString` の忠実な移植。差分は型名解決を
   `AssemblyQualifiedName`(TypeBuilder で `NotSupportedException`)ではなく
   `FullName + ", " + Assembly.FullName` にする点と、`Enum.GetUnderlyingType` の代わりに
   `TypeBuilder.GetEnumUnderlyingType()` を直接呼ぶ点のみ)、
   `RuntimeHelpers.GetUninitializedObject` + `m_con`/`m_constructorArgs`/`m_blob` への
   リフレクション注入で `CustomAttributeBuilder` を鋳造。該当しない場合は従来どおり
   `new CustomAttributeBuilder(...)` を呼ぶだけ(診断の想定どおり)。
2. **`ncc\generation\CoreEmitBridge.n`**: `CreateAttributeBuilder` を追加(リフレクション経由で
   `AttributeBlob.Create` を呼ぶだけ)。
3. **`ncc\hierarchy\CustomAttribute.n` `do_compile`**: `CoreEmitBridge.IsCoreClr` で分岐し、
   core は `CoreEmitBridge.CreateAttributeBuilder(...)`、CLR4 は既存の
   `SR.Emit.CustomAttributeBuilder(...)` をそのまま(一字一句無変更)。

### 受け入れ結果

- **(a) attributes-01.n**: Stage1(net4、`dotnet exec` で .NET 10 上)でコンパイル成功、実行結果は
  期待どおり `attrs 1 5 true` / `[def , foo bar]` / `A` / `Val: A` / `second` / `A` / `B`
  (ファイル末尾の BEGIN-OUTPUT ブロックと完全一致)。**真の自己ホスト stage2 の ncc**
  (Stage1 が .NET 10 上で自分自身を再コンパイルした core フレーバー ncc.exe)でも同様にコンパイル・
  実行成功、同一出力。
- **(b) 派生3形状**(診断の t7/t10/t11 相当): 名前付きフィールド(`MyEnumAttribute(Val=AnEnum.A)`)、
  位置引数(`MyEnumCtorAttribute(AnEnum.B)`)、enum 配列の名前付きフィールド
  (`MyEnumArrAttribute(Vals=array[AnEnum.A,AnEnum.B])`)の3つとも .NET 10 上でコンパイル成功。
  `GetCustomAttributesData()` で `(AnEnum)1` / `Vals = new AnEnum[2]{0,1}` と正しくデコードされる
  ことを確認(blob が ECMA 的に正しいことの独立検証)。
- **(c) CLR4 回帰なし**: Stage1 リビルド後、attributes-01.n をネイティブ CLR4 でコンパイル・実行し、
  .NET 10 版と **バイト単位で同一の標準出力**を確認。

### 落とし穴

`ncc\generation\CoreEmitBridge.n` に `SR.ConstructorInfo`/`SR.PropertyInfo`/`SR.FieldInfo` という
存在しないエイリアスを使ってしまい `unbound type name` エラー(このファイルは
`using System.Reflection;` のみで `SR` エイリアスは無い。`CustomAttribute.n` 側の `SR` エイリアスと
混同した)。素の `ConstructorInfo`/`PropertyInfo`/`FieldInfo` に修正して解消。

## サブタスク3: WP-F 本体(-res / -linkres / /doc:)

### 3a. `/doc:` — 検証のみ、修正不要

`ncc\hierarchy\XmlDump.n` は `System.Xml.XmlDocument` という素の BCL 型のみを使っており、
CoreCLR 分岐は元々存在しない。hello.n と `testsuite\frommcs\test-xml-004.n`
(struct への `<summary>`/Java-style `/** */` コメントを含む)を `-doc:` 付きで
CLR4 / .NET 10 の両方でコンパイルし、出力 XML を diff(埋め込みファイル名の差分を除いて完全一致)。
**「コンパイラーがファイルを書くだけ」という 13-stage2-log の予想どおり、無改造で動作**。
`build-stage2-core.ps1` の rsp に `-doc:` を足さない判断は今後もそのままで良い
(「未検証」から「検証済み・正常」に格上げされたのみで、rsp を変える理由はない)。

### 3b. Win32 `-res`(`-win32-resource`/`-win32res`)

`Manager.Options.UnmanagedResource` の core パスは WP-B 以来ずっと警告して無視するだけだった。
`ManagedPEBuilder` は `nativeResources : ResourceSectionBuilder` を受け取れるので、
RES(Win32 リソースファイル)形式を .rsrc セクションへ変換する
`ResourceSectionBuilder` サブクラスを新規実装した。

#### 実装: `dotnet-port\Nemerle.CoreEmit\Win32Resources.cs`(新規)

- `ResFileResourceSection : ResourceSectionBuilder` — コンストラクターで .RES バイト列をパースし、
  `Serialize(BlobBuilder, SectionLocation)` で Type→Name→Language の3階層リソースディレクトリ
  (`IMAGE_RESOURCE_DIRECTORY`/`_ENTRY`/`_DATA_ENTRY`、文字列テーブル、生データ)を素の PE/COFF
  仕様どおりに手組みする(Roslyn の `CvtResFile` 相当の簡易版。オフセットはツリー構造が
  決定していれば解析的に計算できるため、2パス(サイズ計算→書き込み)方式でバックパッチ不要)。
- `Emitter.Save` に `win32ResourceFile : string` パラメーターを追加し、非 null なら
  `ResFileResourceSection.FromFile(...)` を `ManagedPEBuilder(nativeResources: ...)` に渡す。
- `CoreEmitBridge.Save` / `HierarchyEmitter.SaveAssemblyCore` を配線
  (`Manager.Options.UnmanagedResource` をそのまま通すだけ)。

#### ハマりどころ(RES ヘッダーサイズの仕様誤読)

最初の実装は「HeaderSize は先頭の DataSize/HeaderSize の2つの DWORD を**含まない**」という
誤解で書いた(自作の .res 生成ツールも同じ誤解で書いたため、往復では気づかなかった)。
リポジトリに実在する本物の RES ファイル(`bin\NemerleBinaries-net-4.0-v1.2.547.0\ncc.exe.res`、
Microsoft の rc.exe/cvtres.exe が生成した本物の VERSIONINFO リソース)でクロスチェックしたところ、
signature エントリの `HeaderSize` フィールドの実際の値が 32 であるのに対し、
「DataSize/HeaderSize を除いた残り」は 24 バイトしかなく、**HeaderSize はエントリ先頭
(DataSize フィールド自体)から数える**ことが判明(24+8=32 で一致)。この思い込みのまま
「HeaderSize と実消費バイト数が食い違ったら HeaderSize を信じて位置を補正する」
というフォールバックを書いていたため、位置が8バイト手前にずれ、後続の
Version/Characteristics フィールド(値0)を次エントリの DataSize/HeaderSize として誤読して
`headerSize=0` となり、**補正後の位置が変わらず無限ループ**するバグを作り込んだ
(自作の手組み .res でのみ発現、本物の rc.exe 産 .res は当時のバグ入り実装でも
たまたま正しく解釈できていたため、最初の疎通確認では見逃した)。
本物の .res で作った独立の最小デバッグツール(`scratchpad\wp-f\rsrctest`)でエントリ単位の
ダンプを取って原因を特定し、`headerStart = entryStart`(先頭2 DWORD を含む)に修正。
併せて「補正後オフセットが前進しない/後退する」場合と「DataSize がファイル残量を超える」場合に
`InvalidDataException` を投げる防御を追加(壊れた .res で無限ループする代わりに即座にエラーになる)。

#### 受け入れ結果

- 手組みの `.res`(RT_RCDATA、名前付きリソース "MYNAME"、`scratchpad\wp-f\test.res`)と、
  リポジトリに実在する本物の `.res`(`ncc.exe.res`、RT_VERSION)の両方について、
  .NET 10 上でコンパイルした exe の `.rsrc` セクションを SRM で手動デコードし、
  **元の .res のデータがバイト単位で一致**することを確認(後者は `GetOrAddBlob`
  を介さない生バイト比較で `True` を確認)。
- CLR4 側は無改造。CLR4 でも同じ2つの `.res` を `-win32-resource` でコンパイル・実行して
  回帰なし(手組み `.res` は当初 HeaderSize バグのせいで cvtres.exe に「破損」と
  拒否されたが、これは自作テストファイル側のバグであり、修正後は CLR4 でも通った
  — ncc 側・本 WP の実装側の欠陥ではなかったことの確認にもなった)。
- **自動 Win32 バージョン情報リソース**(`-win32-resource` 未指定時に CLR4 が
  `DefineVersionInfoResource()` で自動生成していたもの)は **実装を見送り、明確な TODO として
  文書化**する判断とした。理由: (1) ユーザーから見える CLI スイッチが無い機能であり、
  省略してもどのテストも失敗しない。(2) VS_VERSIONINFO 構造(`VS_FIXEDFILEINFO` +
  `StringFileInfo`/`VarFileInfo` の入れ子、UTF-16 padding 付き)をアセンブリ属性から
  正しく再現する実装は Win32 -res 変換そのものより手間がかかる一方、
  今回実装した `-win32-resource` を使えば(rc.exe や既存 .res から)同等のバージョン情報を
  ユーザー自身が明示的に埋め込めるため、機能的な代替手段が既にある。
  (3) 工数対効果を考え「壊れているわけではないが unwired」の既存ギャップのまま
  ドキュメント化するに留めた。将来实装する場合は `HierarchyEmitter.add_resources_to_assembly_core`
  にアセンブリ属性(AssemblyVersion/AssemblyTitle/AssemblyCompany 等)から
  `VS_VERSIONINFO` バイト列を組み立てるヘルパーを足し、`ResFileResourceSection` と同様
  `nativeResources` 経由で流すのが自然な実装経路になる。

### 3c. `-linkresource`/`-linkres`

`Manager.Options.LinkedResources` の core パスも警告のみで未実装だった。
`PersistedAssemblyBuilder` に `AddResourceFile` は無いが、`MetadataBuilder`(`GenerateMetadata()`
の戻り値)は `AddAssemblyFile(name, hash, containsMetadata)` と
`AddManifestResource(attrs, name, implementation, offset)` を公開しており、
`implementation` に `AssemblyFileHandle` を渡せばリンクされたリソースの ECMA-335 メタデータ
(File テーブル + ManifestResource テーブル、`Implementation` が embedded の `default` ではなく
File 行を指す)を後付けできることを確認・実装した(`Emitter.Save` に
`linkedResources : string[]`(`"name|絶対パス"` ペア、embeddedResources と同形式)を追加し、
`GenerateMetadata()` 直後に `SHA1` ハッシュ付きで File 行 + ManifestResource 行を追加)。

#### 調査結果: メタデータは正しいが CoreCLR は読み戻せない(ランタイム側の既知の欠落)

Nemerle 非依存の最小 C# 再現(`scratchpad\wp-f\rsrctest`)で検証:

1. `PersistedAssemblyBuilder` で `GenerateMetadata()` 後に `AddAssemblyFile`+`AddManifestResource`
   (`implementation` に File ハンドル)を追加して保存 → `System.Reflection.Metadata` で
   読み返すと **File テーブル・ManifestResource テーブルとも ECMA-335 として正しい行**
   (`Implementation.Kind == AssemblyFile`、ファイル名・SHA1 ハッシュも正しい)。
2. しかし、保存した exe を `Assembly.LoadFrom` してから
   `Assembly.GetManifestResourceNames()` を呼ぶと **リソース名は正しく列挙される**が、
   `Assembly.GetManifestResourceInfo(name)` / `Assembly.GetManifestResourceStream(name)` は
   **どちらも `null` を返す**(例外にもならない)。
3. これは .NET Core がマルチファイルアセンブリ(実体が複数ファイルに分かれたアセンブリ)を
   サポートしなくなったことと整合する既知のランタイム側の欠落であり、
   **ncc 側のメタデータ生成の不備ではない**(メタデータレベルでは csc.exe/link.exe が
   生成する `/linkresource:` の出力と同型であることを確認済み)。

#### 実装した内容と受け入れ結果

上記の理由により「メタデータとしては正しいが、CoreCLR の `System.Reflection` では
読み戻せない」という限定的な状態で実装した(実装コスト自体は WP-G/3b で確立した手法の
延長で小さく、③配管しても実害がないため):

- `Emitter.Save` に `linkedResources` パラメーターを追加、`CoreEmitBridge.Save` /
  `HierarchyEmitter.add_resources_to_assembly_core`(embedded と同じ `"name|絶対パス"` 収集を
  linked 分にも複製、戻り値を `array[string] * array[string]` のタプルに変更)/
  `SaveAssemblyCore` を配線。
- **受け入れ(a) 部分合格**: `-linkres:<file>,<name>` でコンパイルした exe の
  `System.Reflection.Metadata` 経由の読み取りで File+ManifestResource 行が正しく
  (SHA1 ハッシュ付きで)埋め込まれていることを確認、実行(hello.n 本体)も正常。
  **受け入れ(b) 不合格**: 作業指示が求める「`GetManifestResourceInfo` でリンク先ファイル名が
  読めること」は上記の CoreCLR 側の欠落により**達成できない**(ncc 側の追加実装では
  回避不可能 — `System.Reflection` の実装がハードコードで File 実装のリソースを
  解決しないため)。
- CLR4 側は無改造。既存の `AddResourceFile(name, file)` 呼び出しは
  `fileName` 引数にディレクトリ区切りを含められない(.NET の仕様どおり)という
  **本 WP 由来ではない既存の制約**があることも確認した(絶対パスを渡すと
  `ArgumentException: fileName` になるが、ファイル名のみ渡せば正常に動作する。
  この制約は `add_resources_to_assembly_clr4` の元々のコードのままで本 WP は一切変更していない)。

## 最終全体回帰

すべて `bin\Release\net-4.0\Stage1\ncc.exe`(net4 フレーバー、両ランタイムで `dotnet exec`/
ネイティブ実行)と、フルリビルドした `bin\Release\core\Stage2` / `bin\Release\core\Stage3`
(`build-stage2-core.ps1`、`GitTag=1.2`/`GitRevision=577` 固定)で実施。

| 項目 | .NET 10 | CLR4 |
|---|---|---|
| hello.n(-debug なし) | Hello! | Hello! |
| hello2.n(リスト/ラムダ、-debug なし) | `1` / `2, 4, 6` | `1` / `2, 4, 6` |
| hello.n(`-debug`、PDB 生成) | Hello! + 512B PDB | Hello! + 11,776B PDB |
| attributes-01.n | 期待どおりの全7行出力 | 期待どおりの全7行出力(バイト一致) |
| stage2 フルビルド | 0 エラー(4アセンブリ) | — |
| stage3(自己ホスト2周目)フルビルド | 0 エラー | — |
| stage2 の ncc.exe で hello.n 再コンパイル | Hello! | — |
| stage3 の ncc.exe で hello.n 再コンパイル | Hello! | — |
| stage2 の ncc.exe で attributes-01.n コンパイル・実行 | 期待どおりの全7行出力 | — |
| testsuite/positive 先頭60本(アルファベット順、`*-lib.n`除外)を stage2 で `-target:library` コンパイル | **54/60 成功**(13-stage2-log 時点の53/60から attributes-01.n 分+1。残り6件は既知のハーネス制約 — companion lib 不足・System.Web.UI等未参照 — で本 WP 無関係) | — |

## 変更ファイル一覧

- `ncc\hierarchy\MacroClassGen.n` — WP-H、1行。
- `dotnet-port\Nemerle.CoreEmit\AttributeBlob.cs` — 新規、WP-G。
- `ncc\generation\CoreEmitBridge.n` — `CreateAttributeBuilder`(WP-G)、
  `SetAssemblyCustomAttribute`(WP-F 3b の副産物として発見したアセンブリレベル属性バグの回避)、
  `Save` に `win32ResourceFile`/`linkedResources` パラメーター追加(WP-F)。
- `ncc\hierarchy\CustomAttribute.n` — `do_compile` のランタイム分岐(WP-G)。
- `ncc\generation\HierarchyEmitter.n` — `set_assembly_attribute` ディスパッチャー新設+
  6箇所の呼び出し元置換(WP-F で発見したバグの回避、詳細は次項)、
  `add_resources_to_assembly_core` が `-win32-resource`/`-linkres` を配線して
  `array[string]*array[string]` を返すよう変更、`SaveAssemblyCore` の呼び出し更新。
- `dotnet-port\Nemerle.CoreEmit\Emitter.cs` — `Save` に `win32ResourceFile`/
  `linkedResources` パラメーター追加、`SetAssemblyCustomAttribute`/
  `FlushPendingAssemblyAttributes`(アセンブリレベル属性バグ回避)。
- `dotnet-port\Nemerle.CoreEmit\Win32Resources.cs` — 新規、WP-F(`ResFileResourceSection`)。

## 副次的発見: PersistedAssemblyBuilder のアセンブリレベル属性バグ(WP-F 作業中に発見)

attributes-01.n の受け入れテスト中(WP-G 完了直後)に、`[assembly: TestAssembly]`
(このファイル内で定義された `TestAssemblyAttribute` を **アセンブリレベル**に適用する行)を
含む完全な attributes-01.n がコンパイルには成功するのに、**保存した exe のロードが
`System.BadImageFormatException` で失敗する**という新種の不具合を発見した(WP-G の
enum バグとは別物。コンパイルエラーにならないため 13-stage2-log の60本スライスや
WP-G の診断では検出されていなかった)。

Nemerle 非依存の最小 C# 再現(`scratchpad\wp-g-repro`)で確認した根本原因:
`PersistedAssemblyBuilder`(実体は `AssemblyBuilder`)の **アセンブリレベルの
`SetCustomAttribute(CustomAttributeBuilder)`** は、コンストラクターが
「このアセンブリ内でまだ CreateType() 未完了、あるいは完了直後の TypeBuilder」に属する場合に
**保存後のファイルを壊す**(`CreateType()` の前後、両方とも壊れる)。
**同じコンストラクターを使って `TypeBuilder.SetCustomAttribute`(型レベル)を呼んだ場合は
正常に動作する**ため、バグはアセンブリレベルの適用パスに限定される
(GenerateMetadata 内部の `PopulateAssemblyMetadata` がアセンブリレベル属性を
型レベルとは別の(そしてテストが薄い)経路で処理していると推測されるが、
corelib のソースまでは追っていない)。

回避策として、`Nemerle.CoreEmit.Emitter.SetAssemblyCustomAttribute` を新設し、
属性のコンストラクターが `TypeBuilder`(ローカル型)に属する場合のみ
`ConditionalWeakTable` で保留し、`GenerateMetadata()` 完了後(コンストラクターの
`MetadataToken` が確定した後)に `MetadataBuilder.AddCustomAttribute` で
直接メタデータ行を追加する(= アセンブリの Assembly テーブルの唯一の行、固定トークン
`0x20000001`、を親にした CustomAttribute 行を手組みする)方式に変更した。
外部(既にコンパイル済み)のコンストラクターを使うアセンブリレベル属性
(`CompilationRelaxationsAttribute`, `DebuggableAttribute`, `AssemblyConfigurationAttribute`
等、既存の6箇所の呼び出し元すべて)は、これまでどおり
`AssemblyBuilder.SetCustomAttribute` を直接呼ぶ(このパスは元から正常に動作しており、
hello.n 等の全既存テストが検証済み)。`HierarchyEmitter.n` 側の6箇所の
`_assembly_builder.SetCustomAttribute` 呼び出しをすべて新設の `set_assembly_attribute`
ディスパッチャー経由に統一(CLR4 は無条件で従来の直接呼び出し、CoreCLR は
`CoreEmitBridge.SetAssemblyCustomAttribute` 経由)。

**dotnet/runtime への upstream 報告価値がある**(15-attributes01-diagnosis.md で見つけた
`TypeBuilderImpl.UnderlyingSystemType`/`AssemblyQualifiedName` の欠落と並ぶ、
persisted 実装のもう一つの独立したギャップ)。既存 issue の有無は本セッションでは
未調査(時間の都合で見送り)。

### 18-testsuite-log.md の新規バグ疑い2件はこの修正で解消済み

テストハーネス WP(18-testsuite-log.md §6.2(2))が「新規コンパイラーバグ疑い・次 WP の
筆頭候補」として記録した `testsuite\positive\attributes-03.n` / `attributes-assembly.n` の
`BadImageFormatException`(コンパイルはエラーなく成功、生成 exe のロードで失敗)は、
**まさにこのアセンブリレベル属性バグそのもの**であることを確認した。両ファイルとも
`[assembly: ...]` にローカル定義の属性クラス(attributes-03: ジェネリック typeof +
名前付き引数多数 / attributes-assembly: 多重オーバーロード ctor + params)を使っているが、
18 §6.2(2) の推測(「ジェネリック typeof / ctor オーバーロードのパターンが原因」)とは
異なり、**引数の形は無関係で、属性コンストラクターがローカル TypeBuilder に属することだけが
発火条件**(最小再現は引数なしの `[assembly: TestAssembly]` で成立 — 上記 C# repro のとおり。
attributes-01.n が9分割二分探索では拾えなかったのも、blob 破壊がコンパイル時例外を出さず
ロード時に初めて顕在化するため)。

検証結果(本 WP の `SetAssemblyCustomAttribute` 修正適用後、期待出力は各ファイル末尾の
BEGIN-OUTPUT ブロック):

| ファイル | Stage1 on .NET 10 | stage2(self-hosted ncc) | CLR4(回帰確認) |
|---|---|---|---|
| `attributes-03.n` | コンパイル成功・実行出力が期待と完全一致(9行) | 同左 | 同左 |
| `attributes-assembly.n` | コンパイル成功・実行出力が期待と完全一致(True×4) | 同左 | 同左 |

18 が次 WP 候補とした本件の診断・修正作業は不要になった(根本原因特定・修正・3構成での
検証まで本 WP で完了)。

## 残課題・将来課題

- **自動 Win32 バージョン情報リソース**(3b): 未実装、TODO として文書化(判断理由は上記)。
- **`-linkres` の `GetManifestResourceInfo` 読み戻し**(3c): CoreCLR 側の制約により
  ncc 単独では解決不可能。dotnet/runtime 側でマルチファイルアセンブリの
  `GetManifestResourceInfo`/`GetManifestResourceStream` が対応されない限り解消しない。
- **PersistedAssemblyBuilder のアセンブリレベル属性バグ**: upstream 報告候補(未報告)。
  ncc 側は回避済み(attributes-01/-03/-assembly の3テストで検証済み)。
- **stage2/stage3 の非 MVID 差分**: WP-H で **全4アセンブリが解消**したことを確認済み
  (旧課題は解決済みとして記録更新)。
- **`Nemerle.Compiler.Test.exe`/`Nemerle.Test.Framework` の core 移植**: 引き続き
  別 WP のスコープ(本 WP 無関係)。
