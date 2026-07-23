# 48. WP-O1: 入口の現代化(README・入口文書)実装ログ

対象 WP: `47-wp-o-plan.md` §5 WP-O1。実施日: 2026-07-23。ブランチ: `wip/dotnet-port`。

## 結論

WP-O1 を完了した。

- ルート `README.md` の先頭に .NET 10 ポートの入口節を追加した。リポジトリのトップページ
  だけで「これは何か・どう試すか・今どの段階か」が分かる。upstream の README 本文は
  水平線の下に無改造で残る。
- quickstart は公開済み GitHub Release アセット**のみ**を使って Windows / Linux の実機で
  コピペ検証済み(repo clone 不要)。
- PO 指示のスコープ追加として、`dotnet-port/` 直下にあった計画・作業ログ・分析の番号付き
  文書 44 本(`00-PLAN.md`、`01`〜`47-*.md`)を **`dotnet-port/docs/`** へ移設した。

## 文書配置(リファクタリング)

| 場所 | 置くもの |
|---|---|
| `dotnet-port/docs/` | 番号付き計画・実装ログ・分析文書すべて(`00-PLAN.md` を含む) |
| `dotnet-port/DISTRIBUTION.md` | 配布・ツールチェーンの現状リファレンス(スクリプト群の隣に残す) |
| `dotnet-port/packaging/README.md` | リリース同梱のインストールガイド(pack が staging するため従来位置) |

参照の扱い:

- パス修飾された参照(`dotnet-port/NN-*.md` / `dotnet-port\NN-*.md` / `dotnet-port/00-PLAN.md`)は
  リポジトリ全体(スクリプト・workflow・targets・csproj/nproj・ソースコメント・文書)で
  `dotnet-port/docs/...` へ一括書換した(59 ファイル)。
- パス無しの文書名(例: `13-stage2-log.md`)は**文書 ID として存置**。`docs/` 内の文書を指す
  ことを `DISTRIBUTION.md` 冒頭に注記した。
- 共有ソース(`ncc/*.n`、`Nemerle.Compiler.nproj`)への変更は**コメント行のみ**
  (`git diff` で非コメント変更ゼロを確認)。コンパイル出力に影響しないため Stage リビルドは
  不要、回帰ゲートは push CI(HEAD フルビルド)で足りる。

## ルート README の構成

1. **ポート概要**: セルフホスト .NET 10 ncc(決定的 fixpoint・testsuite の現状)、SDK スタイル
   ワークフロー、VS Code 拡張 + LSP(マクロ対応エディター体験の差別化)、Windows/Linux 対応。
2. **Status**: preview であること・個人によるポートで Nemerle チーム非公認であること・
   `.Unofficial` 命名の理由・配布は GitHub Release のみ(nuget.org / Marketplace 非公開)・
   本ポートの第一目的が保存・再現・文書化であること。
3. **Try it**: .NET 10 SDK のみ前提の 4 ステップ(アセット取得 → feed 登録 + templates
   install → `dotnet new nemerle-console` → build → run、任意で VSIX)。
4. **Known limitations**: `.nproj` 必須 / `GenerateDependencyFile=false` / Mono 非対象 /
   preview 面(マクロ生態系ライブラリ未移植、Nemerle.Linq は有り)/ VS2008-2010 統合は
   ポート対象外(エディターは VS Code)。
5. **Where things are documented**: `docs/00-PLAN.md`(全体像)、`DISTRIBUTION.md`
   (配布・再現)、`packaging/README.md`(インストールガイド)。

版表記は 37 §10 の版リテラル禁止規約に従い、`<version>` / `1.2.<rev>-preview.<N>` の
汎用表記のみ(固定版番号は書かない。実版はリリースアセットのファイル名と同梱 README が示す)。

## 検証(受け入れ基準との対応)

quickstart は README の記載どおりのコマンドを、公開済み `release/1.2.635-preview.1` の
アセットだけで実行した(NuGet キャッシュ・CLI home・テンプレート hive は隔離し、
既存環境の混入なし):

| 手順 | Windows 11(SDK 10.0.301) | Linux(WSL Ubuntu、SDK 10.0.110) |
|---|---|---|
| アセット DL → local feed 登録 | PASS | PASS |
| `dotnet new install Nemerle.Templates.Unofficial@<version> --add-source …` | PASS | PASS |
| `dotnet new nemerle-console` → `dotnet build` | PASS(0 warn / 0 err) | PASS(0 warn / 0 err) |
| `dotnet run` | `Hello from Nemerle on .NET 10!` | `Hello from Nemerle on .NET 10!` |
| `code --install-extension vscode-nemerle-<version>.vsix` | PASS(隔離 extensions-dir で install 確認) | —(エディター検証は WP-N5 で実施済み) |

- 受け入れ 1(トップページだけで理解できる): README 構成 §1–§5 で充足。
- 受け入れ 2(コピペで通る): 上表のとおり両 OS で PASS。
- 受け入れ 3(版リテラル禁止): 汎用表記のみ。

## 発見・判断

- **`dotnet new install` の `::` 区切りは .NET 10 CLI で非推奨**(`@` 推奨の warning が出る)。
  ルート README は `@` 表記で書き、`packaging/README.md`(インストールガイド)の該当
  コマンドも `@__NEMERLE_SDK_VERSION__` へ更新した(機能はどちらも成立)。
- リリースアセットの版が唯一の具体値ソースになるよう、README からは release ページと
  同梱 README へ誘導する(プレースホルダー置換機構をルート README に持ち込まない)。

## 既知の制約 / 残課題

1. ルート README の英文入口はリリースのたびに更新する必要はない(版リテラルを持たないため)。
   配布の器を変える判断(WP-O4)をした場合のみ「Try it」の入手手段を書き換える。
2. パス無しの文書名参照(文書 ID)を将来ツールでリンク化する場合は `docs/` 前提で行うこと。
