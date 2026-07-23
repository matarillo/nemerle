# 19 — WP-J: overloading-01 回帰の修正 + string-template-3 の診断・修正 — ログ

ゴール: 18-testsuite-log.md §6.2(4) で発見された `negative/overloading-01.n` の regression
(WP-D の `Typer-OverloadSelection.n` タイブレークが引き起こした)を、WP-D が解いた
`Debug.Assert` 曖昧性を再発させずに修正する。あわせて §6.2(3) の `string-template-3.n`
内部コンパイラーエラーを再現・原因特定し、可能なら修正する。

## 1. タスク1: overloading-01 の修正

### 1.1 問題の再確認

`ncc/typing/Typer-OverloadSelection.n` の `GetBestOverloads` は、`IsBetterOverload` で
絞り込んだ後になお複数候補が残る場合、`AintUsingDefaultParms`(`!o.UsedDefaultParms`)で
「デフォルト引数を1つも使わなかった候補」を無条件に優先していた(WP-D で追加)。
これは `Debug.Assert(bool)` / `Assert(bool, string message = null)` のような、モダン BCL が
補間文字列ハンドラー対応のために増やした「末尾デフォルト引数だけが違う新オーバーロード」を
解決するためのものだったが、`testsuite/negative/overloading-01.n:105-113` の
`Bug743(string)` / `Bug743(string, _ = "a")` という**ユーザー定義の同型オーバーロード**も
無条件に解決してしまい、期待される曖昧エラーが出なくなっていた(CLR4 でも再現する
既存 regression)。

### 1.2 設計調査: 実際の C# は何をしているか

反射で確認したところ、`System.Diagnostics.Debug.Assert(bool)` には
`[System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(-1)]` が付与されており、
`Assert(bool, string message = null)`(新しい方)には付いていない(暗黙の優先度 0)。
実際の C# はこの属性で `Assert(true)` を一意に解決している。

しかし、**同じ属性が `string.Split(char[])` / `Split(string, StringSplitOptions = default)` や
`StreamReader` の各コンストラクターには一切付いていない**ことも確認した。にもかかわらず、
C# で `"...".Split(null)` や `new StreamReader(stream)`(defaultありオーバーロードと衝突する形)を
実際にコンパイルすると **一切曖昧にならず正常にコンパイルできる**(実験で確認)。
つまり実際の C# の Better Function Member アルゴリズムには
「デフォルト引数を埋めていない候補を優先する」という一般規則が(属性と独立に)存在する。

さらに踏み込んで、**`Bug743("s")` と全く同じ形(1引数版 / 2引数目にデフォルト値を持つ版)を
素の C# で書いて試したところ、これも曖昧にならず 1 引数版がコンパイラーに黙って選ばれる**
ことを実験で確認した。つまり **Nemerle のこの negative テストが検証しているのは
「C# を忠実に再現する」ことではなく、Nemerle 自身が意図的に持たせている
より厳格な独自ルール**である(ユーザーが自分で書いた「デフォルト引数だけが違う2つの
オーバーロード」は明示的な曖昧エラーにする、という設計判断)。

このため、「C# の Better Function Member を素直に模倣する」(=デフォルト引数の有無で
常にタイブレークする)というアプローチは、BCL のケースだけでなく Nemerle 独自の
Bug743 テストケースまで解決してしまい、そもそも WP-D が持ち込んだ regression と
同じ問題を再発させる。`OverloadResolutionPriorityAttribute` を読む案も検討したが、
Split/StreamReader のような実際に self-host で踏んだケースを救えないため不十分。

### 1.3 採用した設計: 「外部参照メンバーのみ」ゲート

実際に self-host で踏んだ全ケース(`Debug.Assert`、`ncc/parsing/Lexer.n` の
`"...".Split(null)`、`ncc/parsing/Source.n` の `StreamReader` 構築)に共通するのは、
**候補がすべて外部参照(BCL/参照アセンブリ)のメンバーである**ことである。
一方、negative テストが意図的に曖昧を要求するケース(Bug743 を含む全件)は、
**候補がすべて現在コンパイル中のソースで宣言されたメンバー**である。

そこで、WP-D のタイブレーク(`AintUsingDefaultParms`)自体は維持しつつ、
「候補集合の**全員**が外部参照メンバー(`LibraryReference.ExternalMemberInfo` 派生)の
場合にのみ発動する」というゲートを追加した(`ncc/typing/Typer-OverloadSelection.n`):

```nemerle
static IsExternalMember (o : OverloadPossibility) : bool
{
  o.Member is LibraryReference.ExternalMemberInfo
}

static AintUsingDefaultParms (o : OverloadPossibility) : bool
{
  ! o.UsedDefaultParms
}

static PreferNonDefaultedExternalOverloads (overloads : list [OverloadPossibility]) : list [OverloadPossibility]
{
  if (overloads.ForAll (IsExternalMember))
    overloads.FilterIfExists (AintUsingDefaultParms)
  else
    overloads
}
```

`GetBestOverloads` 内の呼び出しを `res2.FilterIfExists(AintUsingDefaultParms)` から
`PreferNonDefaultedExternalOverloads(res2)` に置換。

この設計は:
- BCL 由来の `Debug.Assert`/`Split`/`StreamReader` 系の曖昧性を、実際に self-host で
  必要とされる形のまま解決する(WP-D が解いていた問題を再発させない)。
- ユーザー(現在コンパイル中のソース)が宣言したオーバーロードには一切発動しない
  ため、Bug743 を含む negative テストの全期待どおりの曖昧エラーを維持する。
- `ConstVariantAndType`/`NotExpectVoidInSequence` など他の negative ケースにも
  影響しない(いずれもユーザー宣言メンバーのみが絡む)。

### 1.4 受け入れ結果

- **negative/overloading-01.n**: stage2(CoreCLR)・CLR4 Stage1 の両方で、
  line 111(Bug743 の呼び出し)を含む全 7 箇所の期待エラーが完全に一致して出力される
  ことを確認(diff なし)。
- **stage2/stage3 フルビルド**: 本修正後、`build-stage2-core.ps1` が
  Nemerle.dll/Nemerle.Compiler.dll/Nemerle.Macros.dll/ncc.exe を **0 エラー**で
  ビルドすることを確認(= `lib/nstring.n` の `Debug.Assert(true)` および
  `ncc/typing/Typer-OverloadSelection.n` 自身の `System.Diagnostics.Debug.Assert(...)` の
  両方が正しく解決されている)。stage3(stage2 によるセルフホスト再構築)も同様に
  0 エラーで完了し、hello2.n が正しく動作することを確認。
- **testsuite 全数**: `run-testsuite-core.ps1` で positive 469 本・negative 167 本を
  再実行。negative は 167/167 中 166 PASS(唯一の失敗は既知の
  `notifypropertychanged.n`、Nemerle.WPF.dll 未ビルドという環境制約で
  `overloading-01.n` とは無関係)— **overloading-01.n は PASS** に転じた
  (18-log 時点の 165/167 → 166/167)。positive は 469 本中 434 PASS、
  失敗 35 件はすべて 18-testsuite-log.md §6.1 の環境・ハーネス制約カテゴリー
  (34件)+ string-template-3.n(本ログ §2 参照、修正後は解消 — 後述)の
  既知集合と完全一致し、新規の regression は0件。
- **CLR4 回帰確認**: Stage1(net-4.0)を本修正後のソースで再ビルドし、0 エラー。
  `hello.n`/`hello2.n`/`overloading-01.n`/`Debug.Assert(true)` すべて .NET 10 版と
  同一の挙動(バイト単位ではなく診断・出力内容が一致)を確認。

## 2. タスク2: string-template-3.n の診断・修正

### 2.1 再現

`testsuite/positive/string-template-3.n` を stage2 ncc でコンパイルすると、内部コンパイラー
エラーになる:

```
error: internal compiler error: got some unknown exception of type
System.Reflection.TargetInvocationException: Exception has been thrown by
the target of an invocation..
   at System.RuntimeType.InvokeMember(...)
   at Nemerle.Compiler.ILEmitter..ctor(MethodBuilder method_builder)
   ...
```

`ncc/main.n` の `bomb()`(ICE 報告関数)は `e.Message`/`e.StackTrace` しか出力せず、
`TargetInvocationException.InnerException`(本当の例外)を握りつぶしていたため、
一時的に `bomb()` に InnerException チェーン全体を出力するデバッグフックを追加して
再ビルド・再現した結果、真の内側の例外が判明した:

```
--- INNER: System.InvalidOperationException: Method body should not exist. ---
   at System.Reflection.Emit.MethodBuilderImpl.GetILGeneratorCore(Int32 size)
```

さらに `ncc/generation/ILEmitter.n` の `ILEmitter` コンストラクターにも一時デバッグ出力を
追加し、どのメンバーでクラッシュしているかを特定した:

```
WP-J DEBUG ILEmitter..ctor: method_builder=method
  ReportTemplate.BaseHtmlReportTemplate.PrintBody__StImpl(_data : list[T]) : void
  mbase.Attributes=Public, Virtual, HideBySig, Abstract
  DeclaringType=Type: BaseHtmlReportTemplate`1
```

(両デバッグフックは診断専用であり、根本原因特定後に `ncc/main.n`/
`ncc/generation/ILEmitter.n` とも元の状態へ復元済み。最終的な修正は
`macros/string.n` の1箇所のみ。)

### 2.2 根本原因

`[StringTemplate.StringTemplateGroup()]` マクロ(`macros/string.n`
`StringTemplateGroupBeforeTypedMembers`)は、文字列テンプレートで実装された各メソッド
(`method`)ごとに、実際の文字列組み立てロジックを実行する `<MethodName>__StImpl`
という**コンパニオンメソッド**を生成する(`macros/string.n:145-161`)。

このコンパニオンの元となる `members2` フィルターは `testAbstract(m) || ...`
(`macros/string.n:138,141`)という条件で、**abstract メソッドも明示的に対象に含む**
——つまり `BaseHtmlReportTemplate[T].PrintBody`(abstract)にも
`PrintBody__StImpl` が生成される。

修正前のコードは、このコンパニオンの修飾子を

```nemerle
def newMods = AttributesAndModifiers(Public | (attrs & (Virtual | Override | Abstract)), ...)
```

のように、**元メソッドの `Abstract` 修飾子をそのままコピー**していた。しかし
コンパニオンメソッド自身の AST(`newMethodAst`、`macros/string.n:153-157`)は
常に実体のある(空の)ブロック `{ }` を持つ ── つまり Nemerle の型付け上は
`FunBody.Parsed` → `FunBody.Typed`(実装ボディあり)になる一方、CLR メタデータ上は
`Abstract` フラグが立った `MethodBuilder` が生成される、という**矛盾した状態**になる。

`beforeBodyTypingHandler`(`macros/string.n:268-270`)は
`!(mb.Attributes %&& Abstract)` という条件で abstract な元メソッドには
登録されない(正しい)ため、abstract 元メソッドのコンパニオンは実際には
本文が書き換えられることもなく、常に元の空 `{ }` のまま実ボディ生成
(`ILEmitter` 経由の `GetILGenerator()` 呼び出し)を試みる。

.NET Framework(CLR4)の `System.Reflection.Emit.MethodBuilder.GetILGenerator()`
はこの矛盾(Abstract フラグ + 実際のボディ生成要求)を寛容に許してきた
(生成された IL は元々誰にも呼ばれないため実害が表面化しなかった)。
CoreCLR の新しい `System.Reflection.Emit.MethodBuilderImpl`(PersistedAssemblyBuilder
経由、`03-dotnet-runtime-facts.md`/`11-emission-log.md` で採用が確定した実装)は
ECMA-335 によりファイルに近い形で検証しており、`Abstract` フラグが立った
`MethodBuilder` に対する `GetILGenerator()` 呼び出しを
`InvalidOperationException("Method body should not exist.")` で即座に拒否する。

つまりこれは **emission や self-host の問題ではなく、`macros/string.n` の
`StringTemplateGroup` マクロに元から存在した(CLR4 では無害に見えていた)バグ**であり、
CoreCLR のより厳格な Reflection.Emit 実装がそれを露呈させたケースである。

### 2.3 修正

`macros/string.n` の該当箇所を、コンパニオンメソッドには **`Abstract` を一切コピーせず**、
その代わり元メソッドが `Abstract` だった場合は `Virtual` を明示的に補う形に変更した
(単純に `Abstract` ビットを落とすだけだと、元メソッドが `Abstract` のみで `Virtual`
ビットを併せ持たない Nemerle 側の修飾子表現のため、派生クラス側のコンパニオンが
「override 対象に virtual が無い」という新たな別のエラーになったため、
`Virtual` への読み替えが必要だった):

```nemerle
def companionAttrs = (attrs & (Virtual | Override)) %| (if (attrs %&& Abstract) Virtual else None);
def newMods         = AttributesAndModifiers(Public | companionAttrs, mods.GetCustomAttributes());
```

これにより、abstract な元メソッドのコンパニオンは「(実際には何もしない)具象の
virtual メソッド」になり、派生クラスの concrete override 側のコンパニオンが
正しくこれを override できる状態になる。

### 2.4 受け入れ結果

- `string-template-3.n` が stage2(CoreCLR)・CLR4 Stage1 の両方でコンパイル成功、
  生成された exe を実行した結果が testsuite の `BEGIN-OUTPUT`/`END-OUTPUT` と
  完全一致(HTML テーブル3行、DOCTYPE 含め diff なし)。
- `string-template-1.n`/`string-template-2.n`(abstract 基底クラスを含まない既存の
  StringTemplate テスト)は本修正の前後で退行なし(コンパニオンの `Abstract` 対象
  フィルターに一切該当しないため無変更のコードパス)。
- testsuite 全数実行で `string-template-3.n` は失敗リストから消え、positive の
  失敗件数は 35 件から 34 件(環境・ハーネス制約カテゴリーのみ)に減少。

## 3. 変更ファイル一覧

- `ncc/typing/Typer-OverloadSelection.n` — `AintUsingDefaultParms` タイブレークに
  「候補が全員外部参照メンバーの場合のみ発動」ゲート(`IsExternalMember`/
  `PreferNonDefaultedExternalOverloads`)を追加。`GetBestOverloads` の呼び出し元を
  差し替え。
- `macros/string.n` — `StringTemplateGroupBeforeInheritance` 内、`__StImpl`
  コンパニオンメソッドの修飾子生成から `Abstract` を除去し、元メソッドが `Abstract`
  だった場合は `Virtual` を明示的に付与するよう修正。

(診断のために一時的に変更してから元に戻したファイル: `ncc/main.n`
[ICE 報告時の InnerException ダンプ]、`ncc/generation/ILEmitter.n`
[コンストラクター引数のデバッグ出力] — 最終状態は無変更。)

## 4. 残課題

- なし(本 WP のスコープ内タスクは両方とも修正完了)。ただし
  `PreferNonDefaultedExternalOverloads` の「全員外部参照」ゲートは、
  「一部が外部参照・一部がユーザー宣言」という混在ケース(例: 拡張メソッドが
  BCL メソッドと衝突するケース)には未対応(発動しない = 現状維持)。
  testsuite にこのパターンの既存テストは無く、実害は確認されていないため
  本 WP では対応を見送った。将来 self-host やテストで踏んだ場合に再検討。
