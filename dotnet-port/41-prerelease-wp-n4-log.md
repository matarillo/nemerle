# 41. WP-N4: 版タグ契約の修正と GitHub Release 配布 実装ログ

対象 WP: 36-prerelease-quality-plan.md §6 WP-N4。

本 log は WP-N4 の**設計・実測証跡**を保持する。36 計画本文は最新の計画に絞るため、
背景・機構の詳細・代替案の検討はここに置く。実装結果は本 log 後半へ追記していく。

## 1. 背景: なぜ現行契約ではリリースタグを打てないか

AssemblyVersion は `GeneratedAssemblyVersion` macro(`macros\GeneratedAssemblyVersion.n`)が
**コンパイル時に `git describe --tags --long` を実行**し、「最も近いタグの数値部 + `.0.` +
タグからのコミット数」で組み立てる。現在 `v1.2` 起点で `1.2.0.<rev>`(実測:
`git describe --tags --long` = `v1.2-633-ge49ba2539` → AssemblyVersion `1.2.0.633`)。

`git describe` の答えは**コミットの祖先関係とリポジトリのタグ集合の両方**に依存する。
したがってリリースタグを mainline のコミットに打つと describe がそのタグを拾い、以後の版が
壊れる:

- タグを打ったコミット以降、describe は `v1.2` ではなく新しいタグからの距離を答える
  → **rev が 0 に戻る**。
- macro の数値抜き出し(`Regex.Replace(tag, @"[^\d\.]", "")`)を通ると、タグ名によっては
  4 成分に収まらない不正な版になる。
- 被害はタグを打ったコミットだけでなく、その**子孫すべて**に及ぶ(describe は祖先方向の
  最近タグを見るため)。

WP-N1 の A2 検査(`assembly-version-check.ps1` の `Get-ExpectedNemerleAssemblyVersion`)は
同じ describe レシピを再計算するので、リリースタグを打つと boot-net10 seed との照合が全段で
throw する。pinned worktree もタグは refs 共有で見えるため同罪。

**再現の観点での含意**: 版はコミットとタグ集合の関数なので、「タグを打つ」行為そのものが
そのコミットの過去のビルド結果を再現不能にする。発行済みリリースを後日再現するには、
リリースタグが版計算に**影響しない**ことが必須。これが本 WP の中心要件。

## 2. 採用案: 案 A(describe に `--match "v[0-9]*"`)

describe を捨てず、**リリースタグ・seed タグを describe から不可視にする**だけの最小改修。

実測(2026-07-17 時点、既存タグ = `v0.0` / `v1.0` / `v1.1b` / `v1.2`):

```
現状:         git describe --tags --long                 = v1.2-633-ge49ba2539
--match 付き: git describe --tags --long --match 'v[0-9]*' = v1.2-633-ge49ba2539   (不変)
```

既存タグはすべて `v[0-9]*` に一致するため、`--match` 追加は既存タグ構成では**機能的 no-op**。
net4 版・net10 版とも版番号・ビルド手順・リリース手順は一切変わらない。将来 `release/` /
`seed/`(v 非開始)を打っても describe はそれらを無視するので、任意のリリースタグを
安全に打てる。

検討して退けた代替:
- **案 B(サイドコミットにタグ)**: macro 無改修だが、タグ checkout からのビルドで版が壊れ、
  Source アーカイブが実ソースと 1 コミットずれる。マージ禁忌の運用地雷。→ 却下。
- **案 C(版ファイルへ全面移行)**: describe を捨て版をファイル固定にする根治案。任意タグ・
  任意コミットビルドを得るが、A2 の自動警報が弱まり NuGet 罠が手動規律頼みになり、検査網の
  再設計が必要。→ WP-N4 では過大。版ピン留めとして **WP-N7** で評価先行 go/no-go で扱う。

## 3. 成果物の設計

### 3.1 タグ契約(成果物 1)

- `GeneratedAssemblyVersion.n` の `GitRevisionHelper` の describe 引数に `--match "v[0-9]*"` を
  追加(`configCommon` / `configCmd` の両経路)。
- `assembly-version-check.ps1` の `Get-ExpectedNemerleAssemblyVersion` の describe 呼び出しに
  同一の `--match` を同期(同ファイルは「macro と same recipe の replay」を契約として明文化
  済み。乖離させない)。
- `macros\` は共有ソースのため §5-2 の CLR4 回帰ゲート(testsuite 全数 + stage2/3 バイト一致 +
  CLR4 スモーク)を適用。§5-5 の VsIntegration 全体 grep も実施し影響確認を記録。
- 代替(git 2.13 未満で `--match` 不可の環境): macro 内で describe 出力を後処理し
  `v` 非開始タグ行を読み飛ばすフィルターで同等を実現。

### 3.2 タグ命名規約(成果物 2)

- リリースタグ: `release/1.2.<rev>-preview.<N>`(実ソースコミットに打つ)。
- seed タグ: `seed/1.2.<rev>`(orphan seed コミットに打つ。preview 概念なし = コンパイラ世代)。
- ともに **v 非開始**(`v*` は upstream rsdn/nemerle の名前空間 = match の保護対象)。
- 接頭辞だけで用途が判別できることを要件とした(`boot/` `dist/` は用途が曖昧なため不採用)。

### 3.3 seed 番地付け(成果物 3、発行済みリリースの後日再現)

目的は「リリース R ↔ seed コミット」の対応を機械可読にし、tip 以外の seed を指名して
ビルドできるようにすること。3 部品:

1. **`seed/` タグ**(永続性): orphan seed コミットを git の GC・履歴書き換えから守る。
   `git tag -l 'seed/*'` で一覧可能。v 非開始なので `--match 'v[0-9]*'` が自動除外。
2. **release-info.json への記録**(追跡性): `pack-release.ps1` に seed コミット hash と
   版一式フィールドを追加。リリース単体から seed へ辿れる。
3. **`build-from-boot.ps1` の seed 指名引数**(再現の実行): 現状の tip 固定(`origin/boot-net10`
   の先端)を解除し、指定 seed(タグ or hash)からビルドできるようにする。

注: 現機構では build-from-boot は世代一致時 in-place、不一致時 pinned worktree。リリースタグ
(= その世代のソース)を checkout した状態は seed 世代と一致するため in-place で成立し、
CLR4 不要・Linux で再現が回る。

### 3.4 preview.N の再現(成果物 4)

- `pack-tool.ps1` は N を計算しない。`-PackageVersionSuffix`(既定 `preview.1`)をそのまま
  版文字列に貼る。N を上げるのは**人**の判断: base(rev)が進めば N=1 にリセット、base 据え置きで
  中身だけ変えて再配布するときだけ N を +1(NuGet の (id, version) キャッシュ罠回避)。
- N はパッケージ版文字列として nupkg のバイト(nuspec / テンプレートの
  `__NEMERLE_SDK_VERSION__` 置換)に埋め込まれるため、**バイト再現には N の復元が必須**。
- 方式: N はリリース時に人が決め、`release/1.2.<rev>-preview.<N>` タグ名に刻んで永続化する。
  再現時はタグ名から版一式(base + N)を読んでパックする経路を用意(ソースファイルに N を
  持たない — N はリリース履歴の状態でありソースの状態ではないため)。

### 3.5 世代更新と初回発行(成果物 5)

- タグ契約の macro 改修は共有ソースなので、Stage1 再ビルド → `refresh-stage1-core.ps1` →
  stage2/libs 再構築 → `publish-boot.ps1` で boot-net10 seed を修正後世代へ refresh。
- その世代で release set を生成し、実ソースコミットに `release/` タグを打ち、
  GitHub Release(**prerelease フラグ付き**)の asset として発行
  (nupkg ×3 + VSIX + README + release-info.json、zip 化の要否は実装時に確定)。
- 契約修正前の世代(例 1.2.627 / 630)には遡ってタグを打てない(macro がコンパイル時に
  除外規則を読むため)。タグ付きリリースは修正後世代が最初になる。

### 3.6 制約の明文化(成果物 6)

`DISTRIBUTION.md` に追記: 「リリース/seed タグは v 非開始」「契約修正前の世代はタグ付け不可」。

## 4. 受け入れ基準(36 §6 と対応)

1. fixture(使い捨て clone): (a) 既存タグのみで修正前後の describe 不変(no-op 証明)、
   (b) `release/` タグを HEAD に打っても AssemblyVersion / A2 期待値 / build-from-boot 全
   チェーン不変。
2. §5-2 回帰ゲート green。
3. release set 封緘、release-info.json の commit・seed hash とタグの整合。
4. 別環境で asset のみから install → `dotnet new` → build → run。
5. **再現**: 使い捨て clone で `seed/` タグ + リリースタグから release set を再ビルドし、
   初回発行物とバイト/版一致(Linux・CLR4 不要)。
6. DISTRIBUTION.md にタグ契約を記録。

## 5. WP-N7 との関係

案 A は describe を Informational 側に残すため、**版ピン留め(WP-N7)を実施しても `--match`
契約は生き続ける**(パッケージ版 rev と provenance の世代情報が describe 由来である限り、
リリースタグを版計算から隠す必要は消えない)。したがって WP-N4 の作業は WP-N7 の go/no-go と
独立に価値を持つ。seed 番地付けも、WP-N7 が seed 機構を作り替えても「orphan 時代に発行済みの
リリースを永続的に再現する」保証として残る。詳細は `44-prerelease-wp-n7-log.md`。

## 6. 実装結果

(着手時に追記)
