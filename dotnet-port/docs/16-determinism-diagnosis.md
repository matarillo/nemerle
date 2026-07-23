# 16 — WP-H: stage2/stage3 バイナリー差分(非 MVID 差分)の根本原因診断

日付: 2026-07-12 / 対象: `bin\Release\core\Stage2\` 一式(scratchpad へ退避したコピーで検証)
性質: **診断のみ**(ソース修正なし)。使用ツール・中間生成物はすべて scratchpad
(`...\scratchpad\wp-h\`)に生成。リポジトリへの書き込みは本ドキュメントのみ。

## TL;DR(結論)

1. **stage3 で真のフィックスポイントに到達している**: stage3 → stage4 で
   `ncc.exe` / `Nemerle.dll` / `Nemerle.Compiler.dll` は(MVID/PE タイムスタンプの
   マスク後)**バイト単位で完全一致**。13-stage2-log.md で観測された stage2 vs stage3 の
   大きな差分(Nemerle.dll 224KB 等)は「初回だけの世代差」であり、非決定性ではない。
2. **唯一の本質的非決定性は `Nemerle.Macros.dll` に残り、原因はただ 1 箇所**:
   `ncc\hierarchy\MacroClassGen.n:749` の
   `Util.tmpname($"operator$(x.GetHashCode())")`。
   演算子マクロ(`+`, `%%`, `<->` など、型名に使えない文字を含む名前のマクロ)の
   生成クラス名に `string.GetHashCode()` を埋め込んでおり、CoreCLR では文字列ハッシュが
   **プロセスごとにランダム化**(Marvin ハッシュ、無効化スイッチなし)されるため、
   ビルドのたびに `_N_operator<乱数>_<id>Macro` の `<乱数>` 部分が変わる。
3. Macros の巨大に見える差分(#US 4 万箇所など)は、この **約 20 個のクラス名の
   桁数変動によるヒープ内オフセットのずれの伝播**で、項目レベルでは
   #US 1311 項目・#Blob(該当以外)・メソッド名などすべて完全一致。
4. 修正は 1 行で済む(後述の案 A)。ランタイム側スイッチでの回避は**不可能**
   (CoreCLR の文字列ハッシュランダム化は常時有効)。

## 1. 実験マトリクス

すべて scratchpad 内で実施(`bin\` は読み取りのみ)。ビルドは
`dotnet-port\build-stage2-core.ps1 -Compiler <前段の ncc.exe> -OutDir <scratchpad>\stageN`。
比較は自作ツール PeDiff / HeapDiff(`System.Reflection.Metadata`、
COFF タイムスタンプ + Module MVID の GUID をマスク後に比較。ツールとログは
`scratchpad\wp-h\difftool\`、`scratchpad\wp-h\heapdiff-*.txt`)。

| 世代 | ビルドに使ったコンパイラー | 目的 |
|---|---|---|
| stage2 | (退避コピー; 元は Stage1) | 基準 |
| stage3a | stage2 | 世代差の測定 |
| stage3b | stage2(2回目) | **同一コンパイラー再現性**(プロセス内非決定性の検出) |
| stage4 | stage3a | **収束性**(フィックスポイント判定) |
| stage5 | stage4 | 追加確認(※後述の汚染あり) |

### 結果(マスク後の差分バイト数)

| 比較 | ncc.exe | Nemerle.dll | Nemerle.Compiler.dll | Nemerle.Macros.dll |
|---|---|---|---|---|
| stage3a vs stage3b(同一コンパイラー2回) | **一致** | **一致** | **一致** | 差分 7,962 B / 532 箇所 |
| stage2 vs stage3a(世代差) | 一致 | 224,095 B | 35,970 B | 129,896 B |
| stage3a vs stage4(収束性) | **一致** | **一致** | **一致** | 110,307 B |
| stage4 vs stage5 | 一致 | 一致 | (汚染・無効) | 既知パターンと同一 |

読み方:

- **同一コンパイラーで 2 回ビルドすると、Macros 以外の 3 アセンブリは完全に決定的**。
  ncc は内部で Hashtable を多用するが、少なくともこの 3 アセンブリの emission 経路には
  ハッシュ順序依存の出力順は(経験的に)存在しない。
- **stage3a vs stage4 の 3 アセンブリ完全一致 = フィックスポイント到達の証明**。
  stage2 vs stage3 の差分は 1 世代で消える種類のもの(§3)。
- **Macros だけは何世代進めても、同一コンパイラーで 2 回ビルドしても差分が出続ける**
  = プロセス内非決定性。

※ stage4 vs stage5 の Nemerle.Compiler.dll 差分(サイズも相違)は、並行作業中の
実装エージェントが stage4 と stage5 のビルドの間に `ncc/generation/*` を編集したことによる
**ソース変更由来の汚染**(stage5 側に stage4 側の `debug symbols are not yet supported...`
文字列が無い等、`git status` で `HierarchyEmitter.n`/`ILEmitter.n`/`CoreEmitBridge.n`/
`Emitter.cs` の変更を確認)。収束性の判定には stage3a vs stage4(汚染前に完結)を用いる。
ncc.exe / Nemerle.dll は stage4 vs stage5 でも一致しており、編集ファイルが
Nemerle.Compiler.dll のソースのみだったことと整合する。

## 2. Nemerle.Macros.dll の差分の正体(メタデータレベル)

stage3a vs stage3b(同一コンパイラー2回、純粋なプロセス内非決定性)の内訳:

```
7068 bytes / 303 ranges : stream #Blob
 709 bytes /  45 ranges : stream #Strings
 163 bytes / 162 ranges : table CustomAttribute (623 rows)   ← 値ハンドルのオフセットずれ
  22 bytes /  22 ranges : table TypeDef (525 rows)           ← 名前ハンドルのオフセットずれ
```

HeapDiff による項目レベルの突き合わせで、**実体差はすべて次の 1 パターンのみ**と確定:

- **TypeDef 名**: `_N_operator_1719609747_8190Macro` ↔ `_N_operator2107149480_8190Macro`
  のような、`operator` 直後の数値だけが異なる生成クラス名(負のハッシュは `-` が `_` に
  エスケープされる)。**末尾の `_8190` 等の連番(`Util.tmpname` の `GetNewId()`)は
  全ビルドで完全一致** — 非決定的なのはハッシュ部分だけ。
- **#Blob / CustomAttribute**: 差分行はすべてアセンブリレベルの
  `ContainsMacroAttribute` の値 blob(`Nemerle.Core._N_operator..._8190Macro` という
  登録クラス名文字列を含む)。
- それ以外の見かけ上の差分(#US・各テーブルの 1 バイト差分の群れ)は、上記クラス名の
  **文字列長の変動**(ハッシュの桁数・負号の有無)で #Strings / #Blob ヒープ内の後続
  項目のオフセットが 1〜2 バイトずれることの伝播。世代間比較(stage3a vs stage4)では
  #US に 46,939 バイト / 40,637 箇所の差分が出るが、**#US の 1311 項目は項目単位では
  100% 一致**(HeapDiff で確認)。メソッド名・フィールド名の差分は 0 件。

## 3. stage2 vs stage3 の大差分(収束する側)のメカニズム

stage2 vs stage3a の Nemerle.dll(224KB 差)を項目レベルで見ると、差分はほぼ全て
`_N_<種別>_<番号>` 形式の**コンパイラー生成名の番号ずれ**:

```
A(stage2): _N_ps_31200, _N__N_lambda__25586__25600, _N_closureOf_describe_15800, ...
B(stage3): _N_enumerator_26300, _N_op_RightShift_closure_18600, _N_options_32700, ...
```

`Util.tmpname` / gensym の番号は `ManagerClass.GetNewId()` のグローバル連番であり、
コンパイル中に**コンパイラー自身がどれだけ ID を消費するか**に依存する。stage2 を
ビルドしたのは Stage1(net4 フレーバー: `NET_4_0` 定義でビルドされ、net4 フレーバーの
Nemerle.Macros.dll をロードする)、stage3 をビルドしたのは stage2(core フレーバー)で、
両者は同じソースの正しいコンパイラーだが内部の ID 消費数が微妙に異なる。そのため
生成名の番号が全体的にシフトし、#Strings/#US/MemberRef/MethodDef が広範囲に差分化する。
**これはコンパイラー世代にのみ依存する決定的な差**であり、core フレーバー同士になった
stage3 以降は完全一致する(stage3a vs stage4 で証明済み)。#US も項目レベルでは一致。

副次的な発見(実害あり): 世代差にはもう 1 つの入り口がある。
`GeneratedAssemblyVersion("$GitTag.0.$GitRevision")` マクロ(`lib\AssemblyInfo.n:41` /
`macros\AssemblyInfo.n:37`)は毎ビルド `git describe --tags --long` を実行するため、
**リポジトリに 1 コミット積まれるとアセンブリバージョンが変わり**(1.2.0.576 →
1.2.0.577)、stage2(576 参照)の Nemerle.Compiler.dll が新しくビルドした Nemerle.dll
(577)をロードできず `FileLoadException`(manifest definition does not match)で
ビルド自体が失敗する。本診断では `ExpandEnvHelper.Expand` が**環境変数を最優先**する
仕様(`macros\ExpandEnv.n:148`: `getEnvironment() ?? getSpecial() ?? getDefault()`)を
利用し、`GitTag=1.2` / `GitRevision=576` を設定して全ステージを同一バージョンで固定した。
再現ビルドの手順としては必須(build-stage2-core.ps1 への組み込みを推奨)。

## 4. 原因コード

`ncc\hierarchy\MacroClassGen.n:739-752`:

```nemerle
private convert_to_valid_type_name (x : string) : string {
  ...
  if (invalid)
    Util.tmpname ($"operator$(x.GetHashCode())");   // ← 行 749: ここが唯一の非決定源
  else
    build.ToString ()
}
```

- `x` はマクロ名(演算子マクロなら `+` や `%%` などの記号列)。`:` 以外の記号を含むと
  `invalid` になり、この行に到達する。lib/macros の標準マクロ群では約 20 個が該当
  (`Nemerle.Core` / `Nemerle.Extensions` の演算子マクロ)。
- `Util.tmpname(kind)`(`ncc\parsing\Utility.n:73`)は `_N_<kind>_<GetNewId()>` を返す。
  `GetNewId()` は決定的(全ビルドで一致することを確認済み)。**一意性は既に GetNewId が
  保証しており、`GetHashCode()` は名前の一意性に寄与していない**(ほぼ装飾)。
- CoreCLR の `string.GetHashCode()` はプロセスごとにシード付き(Marvin)。
  .NET Framework の `UseRandomizedStringHashAlgorithm`(opt-in)と逆で、**常時有効・
  無効化する構成スイッチや環境変数は存在しない**。net4 上では安定だったためこの問題は
  CLR4 ビルドでは顕在化しなかった。
- リポジトリ全体を確認したが、コンパイル時に GetHashCode を「名前」へ埋め込む箇所は
  この 1 箇所のみ(他の `GetHashCode` ヒットはデバッグコメント、実行時ハッシュ実装の
  生成、DecisionTreeBuilder の内部比較などで、出力バイトに乗る名前とは無関係)。
  経験的にも、同一コンパイラー 2 回ビルドで Macros 以外が完全一致したことが
  「他に非決定源がない」ことの強い証拠になっている。

## 5. 修正方針の提案(実装は本 WP の範囲外)

### 案 A(推奨): `convert_to_valid_type_name` から `GetHashCode()` を排除

最小修正は行 749 を次のいずれかに置き換える:

1. `Util.tmpname("operator")` — ハッシュを単純に削除。一意性は `GetNewId()` が保証済み。
   最も簡単で、生成名も短くなる。
2. 決定的な安定ハッシュ(FNV-1a 32bit を 5 行程度で実装、または各文字を `_uXXXX` に
   エスケープした文字列)に置換 — 「名前から元の演算子がわかる」性質を残したい場合。

リスク評価:

- `ContainsMacroAttribute` に記録されるこのクラス名は**同一アセンブリ内の自己登録専用**
  (マクロローダーが属性からクラス名を読み取って同アセンブリから `GetType` する)。
  外部アセンブリがこの生成名を参照することは原理的に不可能(名前自体が毎ビルド変わって
  いたのだから)。後方互換リスクは無い。
- CLR4 ビルド(boot / Stage1)でも名前が変わるため、「CLR4 出力のバイト不変」を厳守する
  なら `#if !NET_4_0` または `if (CoreEmitBridge.IsCoreClr)` で分岐する手もあるが、
  名前は元々 net4 でもプロセスアーキテクチャ等で変わり得る装飾値であり、無条件置換で
  問題ないと判断する(分岐はソースを汚すだけ)。
- **ブートストラップ上の注意**: この修正は「修正を含むコンパイラーでビルドした世代から」
  効く。修正入りソースを stage2 でビルド → stage3'(この Macros はまだ非決定的名を
  持ち得る)→ stage3' で stage4' をビルド、stage4' vs stage5' で 4 アセンブリ完全一致、
  という 2 世代の確認手順になる。

### 案 B(不可能): ランタイム側でハッシュランダム化を無効化

.NET Framework では `<UseRandomizedStringHashAlgorithm>` が opt-in だったが、CoreCLR では
文字列ハッシュのランダム化が常時有効で、`DOTNET_` 環境変数・runtimeconfig・AppContext
スイッチのいずれにも無効化手段が**存在しない**。この経路は選択肢にならない。

### 将来課題(本件の範囲外だが決定的ビルドの完成に必要)

- **MVID / PE タイムスタンプ**: 現状はマスクして比較している。Roslyn の `/deterministic`
  同様、出力内容のハッシュから MVID を導出し、タイムスタンプを固定値(または内容ハッシュ)
  にすれば、マスクなしの完全バイト一致が得られる。emission が
  `PersistedAssemblyBuilder` + `Nemerle.CoreEmit`(PEBuilder)経由になった今、
  `BlobContentId.FromHash` を `ManagedPEBuilder.Serialize` 後に適用する標準レシピが
  そのまま使える(CoreEmit 側の小改修)。
- **AssemblyVersion の git 依存**(§3): `GitTag`/`GitRevision` 環境変数の固定を
  build-stage2-core.ps1 に組み込む(または rsp に `-define` で渡す方式へ変更)。

## 6. 検証に使った成果物(scratchpad、セッション終了で消える)

- `wp-h\stage2\`(退避コピー), `wp-h\stage3a\`, `wp-h\stage3b\`, `wp-h\stage4\`, `wp-h\stage5\`
- `wp-h\difftool\`(PeDiff: マスク付きバイト diff + PE セクション/メタデータテーブル/
  ヒープ/IL 本体へのオフセット対応付け; HeapDiff: #Strings/#US/#Blob/CustomAttribute の
  項目単位突き合わせ)
- `wp-h\heapdiff-3a-3b-macros.txt`, `wp-h\heapdiff-3a-4-macros.txt`,
  `wp-h\heapdiff-2-3a-nemerle.txt`, `wp-h\heapdiff-4-5-compiler.txt`(汚染確認用)

なお、途中で `git worktree`(旧コミット比較用)を一時作成したが、診断には不要と判断して
削除済み(`git worktree list` はメインツリーのみ、`git status` はクリーン
※実装エージェントの編集 4 ファイルを除く)。
