# 49. WP-O2: 保存・再現性の確定(アーカイブ story)実装ログ

対象 WP: `47-wp-o-plan.md` §5 WP-O2。実施日: 2026-07-23。ブランチ: `wip/dotnet-port`。

## 結論

WP-O2 を完了した。

- 公開済み `release/1.2.635-preview.1` は、**GitHub リポジトリ(全履歴 + タグ)と
  .NET 10 SDK / pwsh / Node 22 だけ**で、使い捨ての Linux clone から release set を
  再ビルドできることを再実証した(Windows・.NET Framework・CLR4 いずれも不要)。
- 再現物は初回発行物と **版・provenance・アーカイブのエントリー構成が完全一致**。
  バイト一致の範囲と非一致の原因は §4 のとおり確定し、`DISTRIBUTION.md` /
  `packaging/README.md` に保存目線の照合基準として明文化した。
- provenance 連鎖(release-info / ncc-info / bundle-info / seed boot-info)は
  すべて同一コミットを指し整合(§5)。
- 発見 2 件(§6): (1) **再現はタグ時点のスクリプトで走る**ことの明文化(旧世代タグには
  release workflow を適用できない)。(2) **Linux パック時のテンプレート placeholder
  置換漏れ**(`pack-tool.ps1` を修正済み)。

## 1. 保存の全体像(何があれば数年後に再現できるか)

| 必要なもの | 役割 |
|---|---|
| この git リポジトリの全履歴(リリースタグ `release/1.2.<rev>-preview.<N>`、seed タグ `seed/1.2.<rev>`、orphan ブランチ `boot-net10`、WP-N7 以降はチェックイン seed `dotnet-port/seed/`) | ソース・ビルド機構・ブートストラップ seed のすべて |
| GitHub Release の公開 asset(nupkg ×3・VSIX・README.md・release-info.json) | 照合対象。**利用だけなら asset のみで完結**(`packaging/README.md` のインストールガイド) |
| .NET 10 SDK・pwsh(7 系)・Node 22 | ビルド・パック環境(OS は Windows / Linux いずれも可) |

再現コマンド(新規 clone だけで完結):

```console
git clone https://github.com/matarillo/nemerle.git && cd nemerle
git checkout release/1.2.<rev>-preview.<N>
pwsh dotnet-port/build-from-boot.ps1 -ReleaseTag release/1.2.<rev>-preview.<N>
```

## 2. 再現の再実証(使い捨て Linux clone)

環境: WSL Ubuntu / .NET SDK 10.0.110 / pwsh 7.6.3 / Node v22.23.1 / git 2.43.0。
リポジトリ外の新規ディレクトリへ GitHub から https clone し、上記コマンドを
`release/1.2.635-preview.1` に対して実行。

- タグのコミット `0cab66afe` は WP-N7(in-tree seed)移行**前**なので、タグ時点の
  `build-from-boot.ps1` が seed タグ `seed/1.2.635`(= orphan `boot-net10` の先端
  `7f3c854e8`)を解決し、pinned worktree 経由でフルチェーンを完走した。
- 結果: `dist/release-from-boot` に 6 asset(nupkg ×3 = `1.2.635-preview.1`、
  `vscode-nemerle-0.9.0.vsix`、README.md、release-info.json)。封緘 commit
  `0cab66afe`、`nemerleAssemblyVersion 1.2.0.635` — 初回発行物と同一。
- 再現セットからの消費スモーク(公開 asset を使わず、rebuilt feed だけで
  install → `dotnet new nemerle-console` → build → run)も PASS。生成 `.nproj` は
  `Sdk="Nemerle.Sdk.Unofficial/1.2.635-preview.1"` を正しく pin。

## 3. 公開 asset との照合結果

`release-info.json` は `createdAtUtc` 以外の全フィールドが一致(commit・seed 対応・
版・asset 一覧)。各アーカイブはエントリー集合が完全一致(NuGet の psmdcp は
ファイル名自体がランダム GUID のため名前のみ相違)。エントリー内容の SHA256 分類:

| Artifact | entries | バイト一致 | 改行差のみ | 実差分 |
|---|---:|---:|---:|---:|
| Nemerle.Sdk.Unofficial.nupkg | 17 | 1(**`tools/ncc/ncc.dll`**) | 7 | 8 |
| Nemerle.Templates.Unofficial.nupkg | 11 | 0 | 7 | 3 |
| Nemerle.Linq.Unofficial.nupkg | 6 | 0 | 3 | 2 |
| vscode-nemerle-0.9.0.vsix | 468 | **451** | 7 | 10 |

実差分の内訳(すべて既知の原因に帰着):

- **Nemerle 製アセンブリ**(Nemerle.dll / Nemerle.Compiler.dll / Nemerle.Macros.dll /
  Nemerle.Linq.dll / Nemerle.Compiler.Utils.dll): assert メッセージのチェックアウト
  絶対パス埋め込み(WP-N5 §5.1)。バイト一致は「同一絶対パスの clone」でのみ成立。
- **C# 製補助アセンブリ + pdb**(CoreEmit / Hosting / MSBuild.Tasks / LanguageServer /
  ProjectInfo): PE タイムスタンプ / MVID がビルドごとに変わる(既知)。
- **provenance JSON**(ncc-info.json / bundle-info.json): `packedAtUtc` のみ相違、
  `commit` は一致。
- `_rels/.rels`: psmdcp のランダムファイル名への参照。`deps.json`: ビルド環境差。
- `template.json` ×2: §6 の発見 2(修正済み)。
- 特筆: **`ncc.dll`(コンパイラー本体)は OS・チェックアウトパスを跨いでバイト一致**
  (Windows でパックした公開物 vs Linux 再ビルド)。決定的ビルド(WP-N1)の成果が
  コンパイラー本体では環境非依存に成立している。

照合基準(`DISTRIBUTION.md` / `packaging/README.md` に明文化): **版 + provenance commit +
エントリー構成の一致**が再現の判定。テキストは改行正規化後に比較。コンテナー
(nupkg/vsix)全体のハッシュ比較は無意味(psmdcp・packedAtUtc・PE メタデータ)。

## 4. provenance 連鎖の検証

| 記録 | commit |
|---|---|
| git タグ `release/1.2.635-preview.1`(GitHub 上) | `0cab66afe` |
| 公開 asset `release-info.json` の `commit` / `seed.generation.commit` | `0cab66afe` |
| 公開 Sdk nupkg 内 `tools/ncc/ncc-info.json` | `0cab66afe` |
| 公開 VSIX 内 `extension/server/bundle-info.json` | `0cab66afe` |
| seed タグ `seed/1.2.635` の `boot-info.json` の `generation.commit` | `0cab66afe`(seed 自体は orphan コミット `7f3c854e8`、GitHub に残置) |

現行機構側も `verify-seed.ps1` PASS(チェックイン seed = 同世代 `0cab66afe`、
version.txt ピン 1.2.635、全ファイル SHA256 一致)。

## 5. 再現経路の世代差(明文化)

- **再現は checkout したタグに含まれるスクリプトで走る**。各リリースは自分の世代の
  seed 機構ごと保存されている(preview.1 = orphan seed 世代、WP-N7 以降のタグ =
  チェックイン seed)。orphan ブランチ・seed タグは履歴として GitHub に残置されて
  いるため、旧世代タグも新規 clone だけで再現できる(§2 で実証)。
- **release workflow の `build-smoke`(dry run)が使えるのは、タグのコミットが
  `dotnet-port/smoke-release.ps1` を含む世代(WP-N6 以降)のみ**。preview.1 には
  適用できず、ローカルの `build-from-boot.ps1 -ReleaseTag` が正となる。
  `packaging/README.md` の再現節に反映済み。

## 6. 発見と修正: Linux パックのテンプレート placeholder 漏れ

再現セットの `template.json`(2 本)だけが公開物と実差分になり、`sdkVersion` の
`defaultValue` / `replaces` に `__NEMERLE_SDK_VERSION__` が未置換で残っていた。

- **原因**: Linux の pwsh は dot 名エントリー(`.template.config/`)を hidden 扱いし、
  `pack-tool.ps1` のテンプレート staging の `Get-ChildItem -Recurse -File` が列挙しない
  (実測: 既定列挙 4 files / `-Force` 6 files)。`.nproj` 側は置換されるため
  「置換 0 件なら throw」のゲートを素通りしていた。
- **影響**: Linux でパックした Templates package でも生成プロジェクトは正しい版を pin
  する(`.nproj` は置換済み)ため、CI・スモークは検出できない。実害は
  `dotnet new nemerle-console --sdkVersion <別版>` が**無言で no-op** になることのみ。
  公開済み preview.1 は Windows パックのため無影響(置換済みを確認)。
- **修正**: staging の列挙に `-Force` を追加し、置換件数ゲートを「> 0」から
  **「== 4」の厳密一致**(template.json ×2 + .nproj ×2)へ強化(再発時に停止する)。
  修正した staging ロジック単体を Windows / Linux(WSL)双方で 4/4 置換・残 0 を確認。
  フルチェーンは push CI(`pack-tool.ps1 -Pack` + `smoke-release.ps1`)が検証する。

## 7. 受け入れ基準との対応

1. 別環境(Linux・CLR4 不要)で公開済み asset と seed だけからリリースを再現し、
   初回発行物と一致(§2–§3。一致水準は §3 の照合基準どおり)→ **PASS**。
2. 再現・保存手順の文書化(`DISTRIBUTION.md` 再現節 + 保存に必要なもの、
   `packaging/README.md` "Reproducing a published release")→ **完了**。

## 既知の制約 / 残課題

1. Nemerle 製アセンブリのバイト一致は同一絶対パスの clone に限定(WP-N5 既知)。
   恒久対処(assert パスの相対化 = コンパイラー改修)はバックログのまま。
2. テキストエントリーの改行はパックした OS 側の checkout に従う(実害なし。
   照合時は改行正規化で内容一致を確認する)。
3. `--sdkVersion` no-op(§6)は次回以降の Linux パックで解消。公開済みセットの
   再発行は不要。
