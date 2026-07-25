# 21. LSP サポートの規模感調査(浅い調査)

日付: 2026-07-13
前提: 意図的に「浅い調査」。ソースは目視サンプリングと grep/LOC 計測のみで、ビルドや動作確認は一切していない。数字は当て推量を含む。

## 問い

Nemerle の LSP(Language Server Protocol)サーバーを作るとして、改修がどのぐらい大掛かりになるか。

- 案 a: `VsIntegration/Nemerle.Compiler.Utils`(VS2010 統合の IDE エンジン)をベースにする
- 案 b: Roslyn をベースにする

## 結論(先に)

**案 a が唯一現実的。案 b は「言語基盤を Roslyn にする」という意味では成立しない**(Roslyn はサードパーティ言語をプラグインできる設計ではなく、実質フロントエンド全書き直し=数人年)。ただし Roslyn 由来の **LSP 配管ライブラリだけ借りて案 a に合流させる**のは合理的。

案 a の規模感: 移植プロジェクトのこれまでの WP と同じ尺度で、
**「Compiler.Utils を .NET 10 でビルドが通るまで」が 1〜2 WP、「最小 LSP(診断・補完・hover・定義ジャンプ)が動くまで」で追加 1〜2 WP** 程度と見積もる。大掛かりだが、コンパイラー本体の改修はほぼ不要(IDE フックは既に本体に入っている)。

---

## 案 a: Nemerle.Compiler.Utils ベース

### 資産の棚卸し

| 資産 | 規模 | 内容 |
|---|---|---|
| `VsIntegration/Nemerle.Compiler.Utils` | 143 ファイル / 約 22,000 行 | IDE エンジン本体(ほぼ Nemerle 製)。`Nemerle.Completion2.Engine` が中核 |
| ncc 本体の IntelliSense フック | 12 ファイルに `IsIntelliSenseMode` 分岐 | Lexer/MainParser/Typer/Typer2/ClassMembers/TypesManager 等。**移植済み stage2 にそのまま残っている** |
| `Nemerle.Compiler.Utils.Tests` + `ConsoleTest` | テスト一式 | **VS なしのヘッドレスで** 補完・QuickTip・GotoInfo 等を回すテストが既にある |
| `Nemerle.VisualStudio`(参考) | 254 ファイル / 約 48,000 行 | VS 固有層。**LSP では移植せず捨てる**。LSP サーバーがこの層の代替になる |

重要な構造的事実:

1. **Engine は `ManagerClass`(コンパイラー本体)のサブクラス**(`Engine-main.n`)。つまり「IDE エンジン=特殊モードのコンパイラー」であり、コンパイラーの .NET 10 移植が完了している現状は、そのまま土台になる。
2. **ホストとの契約が細い**。`EngineFactory.Create(callback : IIdeProject, output, isDefaultEngine)` で生成し、ホスト側は `IIdeProject`(プロジェクト名・参照一覧・`CompilationOptions`・ソース取得・診断/ハイライトの通知コールバック、約 20 メンバー)と `IIdeSource`(テキストバッファー)を実装するだけ。ConsoleTest がこの形でヘッドレス駆動している実績がある。
3. **VS への直接依存はほぼない**。`Microsoft.VisualStudio`/WinForms/Drawing 系の参照は 8 ファイル 13 箇所のみで、内訳はデバッグ用 `AstBrowserForm`(WinForms UI)、CodeDom フォームデザイナー系(`FormCodeDomGenerator` 等)、`Formatter` の 1 箇所。**LSP には全部不要なので除外(または条件コンパイル)できる**。
4. スレッドモデルは自前の `AsyncWorker`(BelowNormal 優先度のワーカースレッド 1 本+要求/応答キュー、純粋な `System.Threading`)。LSP サーバーの「リクエストをシリアライズして 1 本のワーカーで処理」という定石とむしろ相性が良い。

### IIdeEngine → LSP 機能マッピング

`IEngine.n`/`IIdeEngine` に既にあるものと LSP メソッドの対応。**意味解析側の実装はほぼ揃っている**:

| 既存 API | LSP |
|---|---|
| `Completion(source, line, col)` | `textDocument/completion` |
| `BeginGetQuickTipInfo` | `textDocument/hover` |
| `BeginGetMethodTipInfo` | `textDocument/signatureHelp` |
| `GetGotoInfo(…, GotoKind)` / `GetInheritorsGotoInfo` | `definition` / `references` / `implementation` |
| `BeginHighlightUsages` | `documentHighlight` |
| コンパイラーメッセージ(`SetCompilerMessage*` コールバック) | `publishDiagnostics` |
| `ScanLexer` / `ScanTokenColor` | `semanticTokens`(構文彩色)— 実装済み: WP-O5a / `53-wp-o5-log.md` |
| `RegionsHelper` / `RegionInfo` | `foldingRange` |
| `Project.Refactoring.n`(rename 系) | `rename` |
| `BeginFindUnimplementedMembers` / `FindMethodsToOverride` | `codeAction` |
| `XmlDocInfo` | hover/補完のドキュメント表示 |

### 必要な作業(推定)

1. **Compiler.Utils の .NET 10 ビルド**(最大の未知数)
   - 現行は `TargetFrameworkVersion v3.5` の nproj/csproj。stage2 ncc(core)で新規にビルドし直す。
   - WinForms/CodeDom デザイナー系ファイル(上記 8 ファイル前後)を除外するプロジェクト構成を作る。
   - 2008〜2012 年ごろのコードが CoreCLR の厳格化(本移植で多数踏んだ類)にどれだけ引っかかるかが読めない。ただし依存は Nemerle.Compiler/Nemerle.Macros のみで、これらは移植済み。
2. **LSP サーバー本体(新規、小規模)**
   - プロトコル層は既製ライブラリを使う: OmniSharp の `OmniSharp.Extensions.LanguageServer`、または Roslyn/Razor が使う `Microsoft.CommonLanguageServerProtocol.Framework` + `StreamJsonRpc`(→案 b の現実的な使い道はこれ)。
   - `IIdeProject`/`IIdeSource` の LSP 実装(ドキュメント同期 didOpen/didChange をソースバッファーに反映、診断コールバックを publishDiagnostics に変換)。
   - 位置変換: Nemerle `Location` は 1 始まり(行,桁)、LSP は 0 始まり UTF-16 code unit。全境界で変換が要る(地味だが漏れると座標ズレ地獄)。
3. **プロジェクトシステム**
   - `IIdeProject.GetAssemblyReferences()/GetOptions()` に食わせる情報源。.nproj を MSBuild 評価(design-time build)して参照とオプションを取るのが本筋。**WP-A3 の Nemerle.Compiler.Hosting / MSBuild タスクの知見・コードがそのまま効く**。最初は「フォルダー+固定参照(auto-ref と同じ 11 アセンブリ)」の決め打ちで良い。
4. **マクロアセンブリのロード**
   - `IntelliSenseModeLibraryReferenceManager` が既にあり、WP-A3 で実証済みの ALC サブクラス化(3 段 virtual フック)と同じ経路。エディター起動中のマクロ dll 差し替えには collectible ALC が要る(これも WP-A3 で実証済み)。

### リスク

- 22K 行の古いコードのビルドがすんなり通るか(未検証)。ここが膨らむと +1 WP。
- IntelliSense モードは untyped→delayed typing 等コンパイラーの「別経路」を通るため、本移植のテスト(バッチコンパイル 601/636)ではカバーされていない。CoreCLR 固有の新バグを踏む可能性はある。
- 良材料: ヘッドレステスト(ConsoleTest / Utils.Tests)が最初の受け入れ基準としてそのまま使える。

---

## 案 b: Roslyn ベース

### 「Roslyn に Nemerle を載せる」は成立しない

- Roslyn(Microsoft.CodeAnalysis)は **C#/VB 専用**。`LanguageNames` は固定で、Workspace/Document の言語サービス群(パーサー、バインダー、`SemanticModel`)は言語ごとにフルスクラッチ実装が必要。サードパーティ言語を差し込む公開拡張点は存在しない(F# ですら Roslyn 外の独自実装+エディター統合のみ)。
- 従って案 b の字義通りの意味は「Nemerle フロントエンドを Roslyn 流 API(immutable green/red tree、`Compilation`/`SemanticModel`)で再実装」= **新コンパイラーの開発**。現行 ncc の規模は parsing 約 10.8K 行、typing 約 21.5K 行、hierarchy 約 11.1K 行、加えてマクロ機構と標準マクロ約 10.7K 行。
- さらに本質的な非互換がある: **Nemerle のマクロは構文拡張(syntax extension)をパース時に注入する**。Roslyn は文法固定・構文木不変が前提であり、「参照したマクロ dll が文法を変える」モデルと根本的に噛み合わない。マクロ自体が Nemerle 製なのでブートストラップ問題も再発する。
- 見積もり: 数人年オーダー。**本プロジェクトの選択肢としては非現実的**。

### Roslyn の現実的な使い道(案 a への合流)

1. **LSP 配管だけ借りる**: `Microsoft.CommonLanguageServerProtocol.Framework` + `StreamJsonRpc`(Roslyn 自身の LSP サーバーと Razor が使用、NuGet 公開)。プロトコル層の品質を無料で得られる。OmniSharp 版との比較だけすれば良い。
2. 将来、C#/Nemerle 混在ソリューション対応で `MSBuildWorkspace` を参照解決に使う、程度。

---

## 概算比較

| | 案 a(Compiler.Utils) | 案 b(Roslyn) |
|---|---|---|
| 意味解析エンジン | 既存 22K 行を再ビルド(コンパイラー本体は無改造) | 約 43K 行相当+マクロ機構を新規再実装 |
| コンパイラー本体への改修 | ほぼゼロ(フック組込み済み) | 全面書き直し |
| マクロ(構文拡張)対応 | 既存機構がそのまま動く | モデル非互換、解決策なし |
| 新規開発 | LSP サーバー薄皮(数千行)+プロジェクトシステム | すべて |
| 規模感 | 2〜4 WP(週単位) | 数人年 |

## 推奨する進め方(着手する場合)

1. WinForms/CodeDom 系を除いた Compiler.Utils を stage2 ncc でビルド → ConsoleTest 相当をヘッドレスで PASS させる(ここまでで実現可能性はほぼ確定する)。
2. `publishDiagnostics` だけの最小 LSP サーバー(プロトコルは既製ライブラリ)+ VS Code 拡張の素(TextMate 文法だけでも即効性あり)。
3. hover → completion → definition の順に `IIdeEngine` を配線。
