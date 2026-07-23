# 15 — WP-G 診断: attributes-01.n の CustomAttributeBuilder ArgumentException 根本原因

作成日: 2026-07-12 / 対象: stage2(真の .NET 10 セルフホスト ncc)における
`testsuite\positive\attributes-01.n` のコンパイル失敗(13-stage2-log.md item 5 の唯一の genuine な失敗)。
**診断のみ・ソース未変更**(修正は別 WP で実施のこと)。

## 結論(要旨)

- 原因は **「同一コンパイル内で定義された enum(= まだ TypeBuilder)を属性の引数/名前付きメンバーに使う」** パターン。
  attributes-01.n では `[MyEnumAttribute(Val = AnEnum.A)]`(AnEnum はこのファイル内で定義)が該当。
- ncc は昔から local enum の属性値を **boxed underlying int** で `CustomAttributeBuilder` に渡す
  (`Literal.AsObject`、ncc\parsing\AST.n:978-983 の TypeBuilder/EnumBuilder 特殊ケース)。
  CLR4 ではこれが通るが、.NET 10 の **persisted 実装(`TypeBuilderImpl`)では検証に通らない**。
- BCL レベルの差の正体: 検証コード自体は両ランタイムでほぼ同一
  (`Type.GetTypeCode(値の型) != Type.GetTypeCode(宣言型)` → `ArgumentException("Constant does not match the defined type.")`)だが、
  **`TypeBuilder.UnderlyingSystemType` の挙動が persisted 実装だけ違う**:
  - .NET Framework 4.8 `TypeBuilder.UnderlyingSystemType` → 未 bake の enum なら **underlying 型 (Int32)** を返す
    (referencesource mscorlib typebuilder.cs:1447-1466)。`Type.GetTypeCodeImpl()` のデフォルト実装は
    `UnderlyingSystemType` に委譲する(referencesource type.cs:241-251)ため
    `GetTypeCode(enum TypeBuilder) == TypeCode.Int32` となり boxed int と一致 → **通過**。
  - .NET 10 CoreCLR の**ランタイム版** `RuntimeTypeBuilder.UnderlyingSystemType` も**同じ特殊ケースを保持**
    (dotnet/runtime src/coreclr/System.Private.CoreLib/.../RuntimeTypeBuilder.cs:951-969)。
  - .NET 10 の **persisted 版 `TypeBuilderImpl`**(System.Reflection.Emit.dll)だけが
    `public override Type UnderlyingSystemType => this;`(TypeBuilderImpl.cs:607)で特殊ケースを欠落
    → `GetTypeCode == TypeCode.Object` → boxed int の `TypeCode.Int32` と不一致 → **throw**。
- **回避不能性**: persisted 側では `CustomAttributeBuilder` にどんな値を渡しても通らないことを実証済み
  (§4)。boxed enum 実体は `Enum.ToObject` がランタイム型必須で作れず、`CreateType()` 後も
  persisted は**同一の TypeBuilderImpl オブジェクトを返す**ため状況が変わらない。
  さらに第2の独立ブロッカーとして、名前付きメンバーの型名エンコードに使われる
  `TypeBuilderImpl.AssemblyQualifiedName` が `NotSupportedException` を投げる(TypeBuilderImpl.cs:602)。
- 修正方針: **CoreCLR パスのみ**、該当ケースで `CustomAttributeBuilder` の検証コンストラクターを迂回し、
  ECMA-335 §II.23.3 の CA blob を Nemerle.CoreEmit(C# ヘルパー)で手組みして
  `CustomAttributeBuilder` インスタンスに注入する(§6 に具体案)。CLR4 パスは一字一句無変更。
- dotnet/runtime への **upstream 報告価値あり**(persisted 実装だけ Framework とも CoreCLR ランタイム版とも
  非互換。既存 issue は見当たらない — 検索結果は §7 末尾)。

## 1. 再現手順

stage2 バイナリー(`bin\Release\core\Stage2\` を scratchpad へコピーしたもの)で:

```
dotnet exec <stage2>\ncc.exe -no-color -use-loaded-corlib -no-stdlib -greedy-references:- ^
  -ref:mscorlib -ref:System ^
  -ref:"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\System.Console.dll" ^
  -ref:"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\System.Xml.Serialization.dll" ^
  -ref:"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.9\System.Private.Xml.dll" ^
  -ref:<stage2>\Nemerle.dll ^
  -target:exe -out:attr01.exe testsuite\positive\attributes-01.n
```

(13-stage2-log の 60 本スライスは `-target:library` かつ Console/Xml 参照なしだったため
`unbound name Console` 等で早期 bail していた。上記参照を足すと本来の例外まで到達する。)

完全なエラー(スタックトレース、`|` は ncc の改行置換):

```
error: internal compiler error: got ArgumentException (Constant does not match the defined type.).
   at System.Reflection.Emit.CustomAttributeBuilder.VerifyTypeAndPassedObjectType(Type type, Type passedType, String paramName)
   at System.Reflection.Emit.CustomAttributeBuilder..ctor(ConstructorInfo con, Object[] constructorArgs,
        PropertyInfo[] namedProperties, Object[] propertyValues, FieldInfo[] namedFields, Object[] fieldValues)
   at Nemerle.Compiler.AttributeCompilerClass.do_compile(GlobalEnv env, TypeBuilder ti, TypeInfo attr, list`1 parms)
   at Nemerle.Compiler.AttributeCompilerClass.CompileAttribute(GlobalEnv env, TypeBuilder ti, PExpr expr)
   at Nemerle.Compiler.AttributesAndModifiers.SaveCustomAttributes(TypeBuilder ti, Function`4 adder)
   at Nemerle.Compiler.MethodBuilder.Compile()
   at Nemerle.Compiler.TypeBuilder.EmitImplementation()
   ... (TypesManager.compile_all_tyinfos → EmitDecls → ManagerClass.Run)
```

## 2. 最小再現ケース(Nemerle)

attributes-01.n(300 行)を 9 分割して二分探索した結果、失敗するのは
`ConverterService` / `MyEnumAttribute` / `AnEnum` の組だけ。最小形:

```nemerle
using System;

public class MyEnumAttribute : Attribute
{
  public Val : AnEnum;          // ← このファイルで定義される enum 型のフィールド
}

public enum AnEnum { | A | B | C }

public class Target
{
  [MyEnumAttribute(Val = AnEnum.A)]   // ← ここで CustomAttributeBuilder..ctor が throw
  public Login(_a : string) : void {}
}
```

派生形も同罪であることを確認(すべて CLR4 では成功 / .NET 10 stage2 では失敗):

| ケース | .NET 10 stage2 | CLR4 Stage1 (native) |
|---|---|---|
| 名前付きフィールド `Val = AnEnum.A`(t7) | ArgumentException (Constant does not match) | OK、実行も `Val: A` |
| **位置引数** `MyEnumCtorAttribute(AnEnum.B)`(t10) | ArgumentException(同上) | OK |
| **enum 配列**の名前付きフィールド `Vals = array[AnEnum.A, AnEnum.B]`(t11) | **NotSupportedException**: `TypeBuilderImpl.get_AssemblyQualifiedName()`(EmitType 内) | OK |

attributes-01.n の他の全パターン(assembly 属性、typeof 引数、`array['A']` char 配列フィールド、
null 引数、多重属性、target 指定 `[method: ...]`、Obsolete、variant/enum メンバーへの属性、
XmlElement)は個別に stage2 で**すべて成功**。失敗は local enum 絡みの 1 パターンのみ。

## 3. ncc 側のコードパス

- `ncc\hierarchy\CustomAttribute.n` `do_compile`(624 行)が
  `SRE.CustomAttributeBuilder(ctor_info, ctor_params, prop_infos, prop_values, field_infos, field_values)`
  を呼ぶ。ここで throw。
- 値の生成元は `compile_expr` → `TExpr.Literal(lit)` → **`lit.AsObject(InternalType)`**
  (`ncc\parsing\AST.n:967-1012`)。`Literal.Enum` のケース:

```nemerle
| Literal.Enum (l, t, _) =>
  def t = t.SystemType;
  if (t is System.Reflection.Emit.EnumBuilder || t is System.Reflection.Emit.TypeBuilder)
    l.AsObject (InternalType)                    // ← local enum: boxed underlying int を返す
  else
    System.Enum.ToObject (t, l.AsObject (InternalType))  // imported enum: 本物の boxed enum
```

  local enum で boxed int しか作れないのは正当(`Enum.ToObject` は TypeBuilder を受け付けない)。
  CLR4 の `CustomAttributeBuilder` はこの表現を前提に受理していた。

## 4. BCL レベルの根本原因(ソース对照)

### 4a. 検証コードは両ランタイムでほぼ同一

.NET Framework 4.8(referencesource mscorlib customattributebuilder.cs:269-274)と
.NET 10(dotnet/runtime src/coreclr/System.Private.CoreLib/.../CustomAttributeBuilder.cs、
`VerifyTypeAndPassedObjectType`:267-277)はどちらも:

```csharp
if (type != typeof(object) && Type.GetTypeCode(passedType) != Type.GetTypeCode(type))
    throw new ArgumentException("Constant does not match the defined type.");
```

### 4b. 差は Type.GetTypeCode(enum TypeBuilder) の結果

`Type.GetTypeCode` のデフォルト実装(両ランタイム共通)は
「`this != UnderlyingSystemType` なら `GetTypeCode(UnderlyingSystemType)` に委譲、さもなくば `TypeCode.Object`」。

| 実装 | `UnderlyingSystemType`(未 bake enum TypeBuilder) | `GetTypeCode` | 検証 |
|---|---|---|---|
| .NET Framework `TypeBuilder`(referencesource typebuilder.cs:1447-1466) | `m_enumUnderlyingType`(= Int32) | **Int32** | 通過 |
| .NET 10 ランタイム版 `RuntimeTypeBuilder`(RuntimeTypeBuilder.cs:951-969) | 同上(特殊ケース保持) | **Int32** | 通過(するはず) |
| .NET 10 **persisted** `TypeBuilderImpl`(TypeBuilderImpl.cs:607) | **`=> this;`(特殊ケース欠落)** | **Object** | **throw** |

つまりこれは「.NET 10 が意図的に厳格化した」のではなく、**PersistedAssemblyBuilder 系の
TypeBuilderImpl が Framework/CoreCLR ランタイム版の enum 特殊ケースを実装していない**という
非互換(おそらく未報告の upstream バグ/ギャップ)。

### 4c. 回避不能性の実証(素の C#、Nemerle 非依存)

scratchpad の `EnumAttrRepro`(PersistedAssemblyBuilder + `DefineType("AnEnum", ..., typeof(Enum))` +
value__ + literal fields、ncc と同じ構築方法)での結果:

| 試行 | 結果 |
|---|---|
| 未 CreateType の enum TypeBuilder フィールド + boxed int | ArgumentException (Constant does not match) |
| `enumTb.CreateType()` **後**に同じ呼び出し | **同じく ArgumentException**(persisted の CreateType は同一オブジェクトを返す: `ReferenceEquals(created, enumTb) == true`) |
| `Enum.ToObject(createdType, 0)` で本物の enum 値を作る | ArgumentException: *Type must be a type provided by the runtime* |
| 名前付きメンバー型が enum 配列(t11 相当) | 検証は通るが `EmitType` → `TypeBuilderImpl.AssemblyQualifiedName` が **NotSupportedException**(TypeBuilderImpl.cs:602) |

→ **CustomAttributeBuilder の公開コンストラクター経由では、宣言型が local enum のとき
いかなる値でも成功し得ない。** 迂回が必須。

### 4d. 迂回の実証(同じ C# repro 内)

blob を手組みして迂回する 2 方式とも成功し、保存後のラウンドトリップで正しくデコードされる:

1. **raw blob**: `MethodBuilder.SetCustomAttribute(ctor, byte[])` に ECMA-335 blob
   (prolog 0x0001 / NumNamed=1 / 0x53 FIELD / 0x55 ENUM / SerString(型名) / SerString("Val") / int32 値)
   を直接渡す → 成功。型名は `AssemblyQualifiedName` が使えないため
   **`tb.FullName + ", " + tb.Assembly.FullName` で手組み**(これで十分)。
2. **forged CustomAttributeBuilder**: 正規の CAB を作って private `m_blob` をリフレクションで
   差し替え → `SetCustomAttribute(cab)` が受理(persisted の内部 `CustomAttributeWrapper` は
   `Ctor`/`Data` を素通しするだけ)→ 成功。

保存した DLL を `AssemblyLoadContext` でロードし `GetCustomAttributesData()` で確認:
`Val = 0 : AnEnum (IsEnum=True)` — blob は well-formed で enum 型も解決される。

## 5. CLR4 との挙動差の確定

- t7(最小再現)を **Stage1 ncc.exe をネイティブ CLR4 で実行**してコンパイル → 成功、
  生成 exe を実行 → `Val: A`(期待どおり)。t10/t11 も CLR4 でコンパイル成功。
- 同一ソース・同一 ncc ロジック(AsObject の boxed int)でランタイムだけ替えると失敗するため、
  ncc 側の回帰ではなく**ランタイム(persisted SRE)の非互換**で確定。
- なお testsuite の `Issue-git-0160.n` には
  `//public enum E { | X | Y }  // we can't use E because SRE 3.5 has bug!` とあり、
  この領域(local enum × 属性 × SRE)は歴史的にランタイム相性が悪い箇所。

## 6. 修正案(実装は別 WP、CLR4 パス無変更の原則に従う)

### 方針

`do_compile` の CoreCLR 側だけを Nemerle.CoreEmit の新ヘルパー経由にする。ヘルパーは
「local enum が絡まなければ従来どおり `new CustomAttributeBuilder(...)`、絡む場合のみ
blob を手組みして CustomAttributeBuilder に注入」する。戻り値は常に
`CustomAttributeBuilder` なので、**HierarchyEmitter.n の 18 箇所の `SetCustomAttribute(attr)`
呼び出しや `AttributeTargets * CustomAttributeBuilder * bool` の配管は一切変更不要**。

### 差分案 1/3 — `dotnet-port\Nemerle.CoreEmit\AttributeBlob.cs`(新規、net10.0 C#)

```csharp
namespace Nemerle.CoreEmit
{
    public static class AttributeBlob
    {
        // 宣言型に「TypeBuilder な enum」(配列要素も含む)が含まれるか
        static bool IsLocalEnum(Type t) =>
            (t.IsEnum && t is System.Reflection.Emit.TypeBuilder)
            || (t.IsArray && IsLocalEnum(t.GetElementType()!));

        public static bool NeedsWorkaround(ConstructorInfo con, PropertyInfo[] props, FieldInfo[] fields) =>
            con.GetParameters().Any(p => IsLocalEnum(p.ParameterType))   // persisted 実装は未 bake でも GetParameters() 可
            || props.Any(p => IsLocalEnum(p.PropertyType))
            || fields.Any(f => IsLocalEnum(f.FieldType));

        public static CustomAttributeBuilder Create(
            ConstructorInfo con, object?[] ctorArgs,
            PropertyInfo[] props, object?[] propVals,
            FieldInfo[] fields, object?[] fieldVals)
        {
            if (!NeedsWorkaround(con, props, fields))
                return new CustomAttributeBuilder(con, ctorArgs, props, propVals, fields, fieldVals);

            byte[] blob = EncodeBlob(con, ctorArgs, props, propVals, fields, fieldVals);

            // 検証コンストラクターを完全に迂回して CAB を鋳造する。
            // (con にパラメーターがある場合、通過できるダミー引数が存在しないため
            //  new CustomAttributeBuilder(con, ...) を先に呼ぶことすらできない)
            var cab = (CustomAttributeBuilder)RuntimeHelpers.GetUninitializedObject(typeof(CustomAttributeBuilder));
            SetPrivate(cab, "m_con", con);
            SetPrivate(cab, "m_constructorArgs", ctorArgs);
            SetPrivate(cab, "m_blob", blob);
            return cab;
            // SetPrivate: typeof(CustomAttributeBuilder).GetField(name, NonPublic|Instance) が
            // null なら corelib レイアウト変更として明確なメッセージ付き例外を投げる。
        }
        // EncodeBlob: ECMA-335 II.23.3。corelib の EmitType/EmitValue/EmitString の忠実な移植で、
        // 相違点は2つだけ:
        //  (1) VerifyTypeAndPassedObjectType 相当の型コード一致検査を行わず、enum 宣言スロットには
        //      boxed underlying 整数(ncc の Literal.AsObject が渡すもの)/boxed enum の両方を受け付け、
        //      Enum.GetUnderlyingType(t)(TypeBuilder のときは t.GetEnumUnderlyingType()。
        //      Nemerle の enum は必ず value__ を定義するので取得可能)のサイズで書く。
        //  (2) enum / typeof 型名の SerString は、t が TypeBuilder のとき
        //      t.FullName + ", " + t.Assembly.FullName(AssemblyQualifiedName は NotSupportedException)、
        //      それ以外は t.AssemblyQualifiedName。
        // 固定引数の順序は con.GetParameters() 順、名前付きは props(0x54)→ fields(0x53)の順で
        // corelib 実装と一致させる。SerString の圧縮長(1/2/4 バイト)も corelib と同じ。
    }
}
```

### 差分案 2/3 — `ncc\generation\CoreEmitBridge.n`(追記)

`EnsureLoaded` に `attr_blob_type = asm.GetType("Nemerle.CoreEmit.AttributeBlob", true)` と
`create_attr_mi = attr_blob_type.GetMethod("Create")` を追加し、公開メソッドを1つ足す:

```nemerle
/** CoreCLR-only: CustomAttributeBuilder that tolerates locally-defined (TypeBuilder) enum
    types in attribute arguments/named members -- the persisted TypeBuilderImpl lacks the
    enum special case in UnderlyingSystemType that both net4 TypeBuilder and CoreCLR's
    RuntimeTypeBuilder have, so the public CustomAttributeBuilder ctor can never accept
    such values (see dotnet-port\docs\15-attributes01-diagnosis.md). */
public CreateAttributeBuilder (ctor : SR.ConstructorInfo, ctorArgs : array [object],
                               props : array [SR.PropertyInfo], propVals : array [object],
                               fields : array [SR.FieldInfo], fieldVals : array [object])
    : System.Reflection.Emit.CustomAttributeBuilder
{
  EnsureLoaded ();
  create_attr_mi.Invoke (null, array [ctor : object, ctorArgs, props, propVals, fields, fieldVals])
    :> System.Reflection.Emit.CustomAttributeBuilder
}
```

### 差分案 3/3 — `ncc\hierarchy\CustomAttribute.n` `do_compile`(620-633 行)

```nemerle
def attrBuilder =
  if (CoreEmitBridge.IsCoreClr)
    CoreEmitBridge.CreateAttributeBuilder(
      ctor_info,
      ctor_params.Reverse().ToArray(),
      prop_infos.Reverse().ToArray(),  prop_values.Reverse().ToArray(),
      field_infos.Reverse().ToArray(), field_values.Reverse().ToArray())
  else
    SR.Emit.CustomAttributeBuilder(         // CLR4: 従来コードそのまま
      ctor_info,
      ctor_params.Reverse().ToArray(),
      prop_infos.Reverse().ToArray(),  prop_values.Reverse().ToArray(),
      field_infos.Reverse().ToArray(), field_values.Reverse().ToArray());
```

CLR4 側は分岐の else にそのまま残す(WP-B/C/D と同じ「呼ばれない側の完全分離」原則。
本メソッドに CLR4 専用 API は無いので `#if NET_4_0` は不要)。
`MakeEmittedAttribute` / `GetInformationalAssemblyAttributes`(ランタイム型のみ使用)と
`create_instance`(security 属性用、CoreCLR では `is_security_attribute` が常に false)は変更不要。

### 検証計画(実装 WP 用)

1. t7/t10/t11(scratchpad に既存)+ attributes-01.n が stage2 でコンパイルできること。
2. attributes-01.n の生成 exe を `dotnet exec` して BEGIN-OUTPUT どおり
   (`attrs 1 5 true` / `[def , foo bar]` / `A` / `Val: A` / `second` / `A` / `B`)。
   特に `Val: A` は blob 内 enum 型名解決のラウンドトリップ検証になる。
3. ILDasm/`GetCustomAttributesData` で CLR4 生成物と blob がバイト一致することの確認(型名文字列の
   組み立て差以外は一致するはず)。
4. CLR4 回帰: Stage1 再ビルド後、attributes-01.n をネイティブ CLR4 でコンパイル・実行し従来どおり。
5. stage2→stage3 フィックスポイントが維持されること。

### 代替案(検討して却下)

- **enum を `ModuleBuilder.DefineEnum`(EnumBuilder)で emit する**: persisted の
  `EnumBuilderImpl` でも AQN 等に同種の制約があり、CreateType 順序制御・型キャッシュへの
  影響が大きい割に確実性がない。却下。
- **属性の配管を `(ConstructorInfo, byte[])` に変える**: `SetCustomAttribute(con, byte[])` は
  全ビルダーにあるので純正 API のみで済むが、`AttributeTargets * CustomAttributeBuilder * bool` を
  受け渡す HierarchyEmitter/TypeBuilder/MethodBuilder ほか多数の署名変更が必要で、
  CLR4 共有コードに触ってしまう。却下(ただし将来 m_blob のフィールド名が変わった場合の
  プラン B としては有効)。
- **upstream 修正待ち**: `TypeBuilderImpl.UnderlyingSystemType` の enum 特殊ケース欠落と
  `AssemblyQualifiedName` の NotSupportedException は dotnet/runtime に報告する価値がある
  (Framework とも CoreCLR ランタイム版 RuntimeTypeBuilder とも非互換であることがソースで示せる。
  既存 issue は見当たらなかった)。ただし修正が .NET 11 以降になるため、ncc 側の迂回は必要。

### リスク

- `m_con`/`m_constructorArgs`/`m_blob` は corelib の private フィールド名(CoreCLR。
  .NET Framework とは無関係、mono は対象外)。バージョンアップで変わる可能性はあるが、
  ヘルパーで検出して明確なエラーにする。変わった場合はプラン B(配管変更)へ。
- `GetUninitializedObject` 経由のため `m_blob` 以外のフィールドが増えた場合は null のままになるが、
  emission 側が読むのは `Ctor`/`Data`(= m_con/m_blob)のみ(CustomAttributeWrapper.cs で確認済み)。

## 7. 影響範囲(同種問題の広がり)

- 壊れる形状は3つ(§2 の表): local enum の (a) 名前付きフィールド/プロパティ、(b) 位置引数、
  (c) enum 配列の名前付きメンバー。※ (b') object 型引数へ local enum を渡すケースは
  CLR4 でも boxed int として emit されていた(tagged int32)ため挙動同一・対象外。
- **testsuite\positive 内では attributes-01.n が唯一の該当**(属性クラスと enum を同時に定義する
  ファイルを全 grep で洗い出し、他の候補 bug-1200 / Issue-git-0160 / Issue-git-0353 /
  external-extension-method-lib / macrolib は imported enum のみ、または enum を属性に使っていない。
  Issue-git-0160 は前掲コメントのとおり意図的に local enum を避けている)。
- 追加サンプリング: 61〜80 本目(bug-1129 〜 bug-1210、alphabetical、*-lib 除外)を stage2 コピーで
  コンパイル → **19/20 成功**。唯一の失敗 bug-1166.n は companion のマクロ lib が必要という
  ハーネス制約(`unbound name testMacro`)で、本件とは無関係。同種の failure は検出されず。
- コンパイラー自身のセルフホスト(stage2/3)はこのパターンを含まないため影響なし(0 エラーで
  ビルド済みの実績どおり)。実世界の Nemerle コード(自前 enum をオプションに使う属性)では
  普通に踏み得るため、stage2 を「属性を多用する実コード」に使う前に修正すべき、という
  13-stage2-log の評価は妥当。

upstream 調査の出典:
- [dotnet/runtime #97015 (PersistedAssemblyBuilder API)](https://github.com/dotnet/runtime/issues/97015)
- [dotnet/runtime #99505 (persisted TypeBuilder の別の NotSupportedException 例)](https://github.com/dotnet/runtime/issues/99505)
- [CustomAttributeBuilder リファレンス](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.customattributebuilder.-ctor?view=net-8.0)
- 該当エラーの既存報告は検索で発見できず(2026-07 時点)。

## 8. 付録: 検証資材(scratchpad、セッション終了で消える)

`C:\Users\kenta\AppData\Local\Temp\claude\f--dev-nemerle-projects-github-nemerle\e2dd9389-8d65-4b73-bb30-6074f4d88a9d\scratchpad\`

- `wp-g\stage2\` — stage2 バイナリーの退避コピー(診断はすべてこちらで実施)
- `wp-g\stage1-clr4\` — Stage1(CLR4)の退避コピー
- `t1_selfx.n` 〜 `t9_variant.n` — attributes-01 の 9 分割(t7 のみ失敗)
- `t7_myenum.n` / `t10_enumctor.n` / `t11_enumarr.n` — 最小再現3形状
- `EnumAttrRepro\` — 素の C# 再現+迂回実証(PersistedAssemblyBuilder、§4c/4d の全出力)
- `CustomAttributeBuilder.cs` / `TypeBuilderImpl.cs` / `RuntimeTypeBuilder.cs` /
  `CustomAttributeWrapper.cs` / `ConstructorBuilderImpl.cs` / `MethodBuilderImpl.cs`(dotnet/runtime main)、
  `CustomAttributeBuilder.net48.cs` / `TypeBuilder.net48.cs` / `Type.net48.cs`(referencesource)
