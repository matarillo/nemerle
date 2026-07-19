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

## 6. 仮実装計画(2026-07-19 着手時。以降の節が実装結果)

### 6.1 着手時実測で確定した前提

- HEAD `c200bef6c` = `v1.2-634-gc200bef6c`(世代 634)。実装コミット後の世代は 635 見込み。
- 既存タグ `v0.0` / `v1.0` / `v1.1b` / `v1.2` は **4 本とも lightweight**(`git cat-file -t` = commit)。
- `git describe --tags --long --match 'v[0-9]*'` = `v1.2-634-gc200bef6c`(この環境の git で
  no-op を再実証。§2 の 633 時点の実測と同じ)。
- pack 系 provenance の `git describe --long --always --dirty`(`--tags` 無し = annotated 限定)は
  annotated タグが 1 本も無いため**常に `--always` fallback = bare commit hash**(実測 `c200bef6c`)。
- `git describe` 呼び出し箇所の全数調査(grep)の結果、レシピは 3 系統 8 箇所:
  1. **版計算**: `macros\GeneratedAssemblyVersion.n`(configCommon / configCmd)、
     `dotnet-port\assembly-version-check.ps1`(replay)、`tools\msbuild-task\GetGitTagRevision.cs`
     (同一レシピの C# 複製。利用者は `misc\packages\wix\nemerle.wixproj` と
     `snippets\VS2010\Nemerle.VisualStudio.csproj` のみ = 本ポートでは死んでいるが同期する)。
  2. **世代比較**: `publish-boot.ps1`(boot-info.json の generation.describe)、
     `build-from-boot.ps1`(in-place / worktree 判定)。macro と同じ `--tags --long` レシピ。
  3. **provenance 記録**: `pack-tool.ps1`(ncc-info.json)、`vscode-nemerle\pack-server.ps1`
     (bundle-info.json)、`pack-release.ps1`(release-info.json + dirty 検査)。
  - 対象外: `tools\TCSetBuildNumber.cmd`(TeamCity 遺物、死んでいる。無改修)。

### 6.2 設計判断(着手時に追加・確定したもの)

**(a) `--match` は 3 系統 8 箇所すべてに同期する。** 版計算・世代比較は契約の本体。
provenance 記録(annotated 限定)は現状 no-op だが、release タグをうっかり annotated で
作った場合に ncc-info / bundle-info の describe 文字列が変わり nupkg / VSIX の**バイトが
変わる**(= 受け入れ基準 5 の再現を壊す)ため、防御として同期する。

**(b) release/・seed/ タグは lightweight で作る**(タグ契約に追加)。annotated 限定 describe
への不可視性を二重に保証する。GitHub Release は lightweight タグで問題なく機能する。

**(c) 発見: boot-4.0 の旧 macro バイナリは契約の外に残る(→ §発見事項)。**
`--match` はソース修正であり、Stage1 を生成する boot-4.0(凍結済みバイナリ)の
`GeneratedAssemblyVersion` は旧レシピのまま。release タグが祖先に付いたコミットで
boot-4.0 → Stage1 のフルリビルドを行うと、旧レシピが release タグを拾い、タグ名の
数字抜き出し(`1.2.635..1` のような不正版)で**ビルドが大声で失敗する**。
seed/ タグは orphan コミット上で main の祖先に乗らないため旧レシピにも不可視(無害)。
対処: 運用回避(Stage1 リビルド前にローカル release タグを一時削除)を文書化。根治は
WP-N7 の版ピン留め。本 WP 自身の手順は「Stage1 リビルド → 最後にタグ」なので影響しない。

**(d) 初回発行物は「再現と同一の経路・同一の絶対パス」で生成する。**
WP-N5 §5.1 のチェックアウトパス埋め込みにより、バイト一致はビルドパスが一致する場合に
しか成立しない。そこで初回発行 asset 自体を、canonical path に置いた使い捨て clone 上の
`build-from-boot.ps1 -ReleaseTag release/1.2.<rev>-preview.<N>` で生成する。再現(受け入れ 5)は
後日、同じ path への新規 clone + 同じコマンドで、初回と同一条件になる。
`-ReleaseTag` 指定時は tip の世代が一致していても**常に pinned worktree 経路**
(`<cloneRoot>\.boot-build-tree`)を使う — in-place だと clone root 直下ビルドになり、
後日の再現(tip が進んで worktree 経路)とパスがずれてバイト一致が壊れるため。
バイト一致の保証は「同一マシン・同一 canonical path」に限る(他環境では版・内容一致のみ)。

**(e) preview.N の転送。** `build-from-boot.ps1` に `-PackageVersionSuffix` を追加し
`pack-tool.ps1` へ転送する。`-ReleaseTag` はタグ名から base(`1.2.<rev>`)と suffix
(`preview.<N>`)を導出し、seed 既定を `seed/<base>` に、suffix を pack-tool へ渡す。
整合検証: release タグの指すコミット == seed の generation.commit、および base ==
seed の nemerleAssemblyVersion(`1.2.0.<rev>` → `1.2.<rev>`)。

**(f) release-info.json の seed 記録。** `pack-release.ps1` が `boot-net10` →
`origin/boot-net10` の順で seed ref を解決し、その boot-info.json の generation.commit が
リリースコミットと一致する場合に `seed = { commit(orphan コミット hash), tag(指している
seed/ タグ名、あれば), generation }` を記録する。一致しない・解決できない場合は警告して
null 記録(Windows Stage1 直ビルドの throwaway 封緘を塞がない)。

### 6.3 変更ファイル(仮)

| ファイル | 変更 |
|---|---|
| `macros\GeneratedAssemblyVersion.n` | describe 2 経路に `--match v[0-9]*` |
| `dotnet-port\assembly-version-check.ps1` | replay に `--match` 同期 |
| `tools\msbuild-task\GetGitTagRevision.cs` | 同上(C# 複製の同期) |
| `dotnet-port\publish-boot.ps1` | `--match` + seed コミットへ `seed/1.2.<rev>` lightweight タグ付与 |
| `dotnet-port\build-from-boot.ps1` | `--match` + `-Seed` / `-ReleaseTag` / `-PackageVersionSuffix` |
| `dotnet-port\pack-tool.ps1` | provenance describe に `--match` |
| `dotnet-port\vscode-nemerle\pack-server.ps1` | 同上 |
| `dotnet-port\pack-release.ps1` | 同上 + release-info.json へ seed 記録 |
| `dotnet-port\DISTRIBUTION.md` | タグ契約の節を追加 |
| `dotnet-port\packaging\README.md` | For maintainers にタグ付け・GitHub Release 手順 |

### 6.4 手順(実行順)

1. コード変更一式を 1 コミット(= リリース対象の実ソースコミット、世代 G)。
2. fixture 1a: 使い捨て clone で修正前後の describe 不変(git レベル no-op 証明)。
3. Stage1 フルリビルド(CLR4 msbuild)+ `refresh-stage1-core.ps1`。
4. §5-2 回帰ゲート: stage2 ×2 独立ビルド + stage3 のマスク無しバイト一致、
   `build-libs-core.ps1`、testsuite 全数、CLR4 hello/hello2 スモーク。
   §5-5: VsIntegration / VS2010 grep の影響確認を記録。
5. `publish-boot.ps1` で seed refresh + `seed/1.2.<G>` タグ。
6. fixture 1b: 使い捨て clone(bundle 経由、新 seed 入り)で `release/` タグを HEAD に打ち、
   AssemblyVersion / A2 期待値 / `build-from-boot.ps1` 全チェーンの不変性を確認。
7. 実ソースコミットに `release/1.2.<G>-preview.1`(lightweight)を打ち、canonical path の
   clone で `build-from-boot -ReleaseTag` により初回発行 asset を生成。
8. push(オーナー確認)→ `gh release create --prerelease` で asset 発行。
9. 受け入れ 4: 別環境相当(clean な一時ディレクトリー + asset のみ)で
   install → `dotnet new` → build → run。
10. 受け入れ 5: 同 canonical path への新規 clone(GitHub から)+
    `build-from-boot -ReleaseTag` で再現し、初回発行物とバイト比較。
11. DISTRIBUTION.md / packaging README / 本 log の文書化。

### 6.5 リスクと逃げ道(仮計画時点)

- **git バージョン**: `--tags` と `--match` の併用が lightweight タグに正しく効くのは
  git 2.7 以降(単一 `--match` 自体は太古から)。実行環境(Windows / Ubuntu 26.04)は
  いずれも遥かに新しく、実測でも確認済み。2.13 未満向けの後処理フォールバック(§3.1)は
  実装しない(必要になった時点でバックログ)。
- **`-ReleaseTag` の worktree 強制**が既存の in-place 利用(引数なし)に影響しないこと —
  引数なしの挙動は完全に従来どおり残す。
- Stage リビルドで世代が 627/630 から進むため、Windows 側 bin の stage2 / libs / dist の
  再構築と provenance 整合が必要(WP-N1 の A2 検査が混在を機械検出する)。
