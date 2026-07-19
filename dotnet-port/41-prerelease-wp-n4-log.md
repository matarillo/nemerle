# 41. WP-N4: 版タグ契約の修正と GitHub Release 配布 実装ログ

対象 WP: 36-prerelease-quality-plan.md §6 WP-N4。

本 log は WP-N4 の**設計・実測証跡**を保持する。36 計画本文は最新の計画に絞るため、
背景・機構の詳細・代替案の検討はここに置く。実装結果は本 log 後半へ追記していく。

## 0. 結論(2026-07-19 実施・完了)

**WP-N4 完了。リリースタグを版計算から切り離す契約を確立し、初回の GitHub Release
(prerelease)を発行、発行済みリリースの後日再現まで実証した。**

- **タグ契約**: describe レシピへ `--match "v[0-9]*"` を 3 系統 8 箇所(版計算 3 / 世代比較 2 /
  provenance 3)に同期。`release/1.2.<rev>-preview.<N>`(実ソースコミット)と
  `seed/1.2.<rev>`(orphan seed)は **v 非開始 + lightweight** が契約。既存タグ構成での
  no-op と、タグを打っても AssemblyVersion / A2 / build-from-boot が不変であることを
  fixture で証明(§7.2 / §7.6)。
- **seed 番地付け**: publish-boot が seed タグを自動付与、pack-release が release-info.json に
  リリース ↔ seed 対応を記録、`build-from-boot -ReleaseTag` がタグ名だけから発行物を
  再ビルドする(§3.3–3.4 / §7.6)。
- **初回発行**: 世代 635(`0cab66afe`)で §5-2 回帰ゲート全 green(stage2×2 / stage3==stage4
  マスク無しバイト一致、testsuite **614/636** = baseline 同数、CLR4 スモーク)→ seed refresh
  (`seed/1.2.635`)→ **https://github.com/matarillo/nemerle/releases/tag/release/1.2.635-preview.1**
  に asset 6 点(nupkg ×3 `1.2.635-preview.1` + VSIX 0.9.0 + README + release-info.json)。
  GitHub asset のみからの install → `dotnet new` → build → run を実証(§7.7)。
- **再現**: GitHub からの新規 clone + 同一コマンドで再現ビルドが完走。**版一致は完全**、
  バイト一致は Nemerle 製アセンブリ・静的コンテンツで成立し、残差はパッケージング層の
  ビルドメタデータ 3 種(NuGet psmdcp / packedAtUtc / C# 補助アセンブリの PE ヘッダー)に
  限定されることを zip エントリー単位で確定(§7.8.1)。
- **主要な発見**: boot-4.0(凍結バイナリ)の旧レシピは契約の外に残る — release タグが
  見える checkout での Stage1 フルリビルドは一時退避運用が必要で、根治は WP-N7(§11-1 /
  §12-3)。publish-boot の origin フォールバック欠落(危うく seed 履歴を force-push で
  失うところ)を発見・修正(§11-3)。

受け入れ基準 6 項目の判定は §8、回帰ゲート実測は §9、再現コマンドは §10、発見事項は §11、
確定した制約と次 WP への境界は §12、バックログ起票予定は §13。

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

## 7. 実装結果(実施记録)

### 7.1 実装コミット

**`0cab66afe`(= 世代 `v1.2-635-g0cab66afe`、AssemblyVersion 1.2.0.635 見込み)** に
§6.3 の全変更を 1 コミットで収録。実装体制: メイン(Fable)が macro /
assembly-version-check / GetGitTagRevision / 文書、Sonnet サブエージェントが ps1 5 本
(pack-tool / pack-server / pack-release / publish-boot / build-from-boot)を詳細仕様で
実装し、メインが diff レビュー(設計どおり・`-ReleaseTag` 未指定時の既存動作不変を確認)。

### 7.2 fixture 1a: describe no-op 証明(使い捨て clone、git 2.54.0.windows.1)

| シナリオ | 旧レシピ `--tags --long` | 新レシピ `+ --match 'v[0-9]*'` | provenance `--long --always` | 同 `+ --match` |
|---|---|---|---|---|
| 既存タグのみ(v0.0/v1.0/v1.1b/v1.2) | `v1.2-634-gc200bef6c` | **同一**(no-op 証明) | `c200bef6c` | `c200bef6c` |
| + lightweight `release/1.2.999-preview.1` @HEAD | **壊れる** `release/1.2.999-preview.1-0-g...` | `v1.2-634-g...` 不変 | 不変 | 不変 |
| + annotated `release/1.2.998-preview.1` @HEAD(契約違反シナリオ) | 壊れる | **不変(--match の防御が効く)** | **壊れる**(annotated が映る) | **不変(防御が効く)** |

- 新 `Get-ExpectedNemerleAssemblyVersion`(replay)は release タグ 2 本が存在する clone に
  対して `1.2.0.634` を返した(不変)。annotated 行の実測が、provenance 側にも `--match` を
  同期した §6.2(a) の判断を裏づける。
- **副次的発見**: この環境の素の `git clone` は VS2010 テンプレート等の長パスで
  Windows MAX_PATH に当たり checkout が部分失敗する(describe/refs は無影響)。
  fixture 1b・リリース用 clone は**短い実パス + `core.longpaths=true`** で行う(→ §発見事項)。

### 7.3 環境ブロッカー: CLR4 強名署名の CS1548(解消済み)

Stage1 フルリビルドが `Nemerle.MSBuild.Tasks.csproj` の署名で
`CS1548 ... アクセスが拒否されました` により失敗。単体再現(CLR4 csc + snk のみ)で
プロジェクト非依存を確認し、原因は `C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys` の
ACL が標準の「Everyone: 特殊(書き込み)」ではなく **RX のみ**に矯正されていたこと
(CAPI の強名署名は同フォルダーに一時キーコンテナを作る)。従来のリビルドが通っていたのは
`GetGitTagRevision.cs` 無変更で Tasks プロジェクトの再コンパイル(=署名)が
走らなかったため。本 WP の同ファイル改修で顕在化した。
**PO 承認のうえ**、昇格 icacls で `kenta:(OI)(CI)M` を同フォルダーに付与して恒久解消
(単体署名テスト exit 0 を確認)。

### 7.4 世代更新と回帰ゲート(§5-2)

世代 635(コミット `0cab66afe`)で Stage1 フルリビルド(CLR4 msbuild、0 エラー)+
`refresh-stage1-core.ps1` を実施。Stage1 `Nemerle.dll` = **1.2.0.635**
(boot-4.0 の旧レシピ macro による焼き込みだが、release タグ不在のため新レシピと同値 —
§6.2(c) の理屈どおり)。

| ゲート | 結果 |
|---|---|
| stage2 ×2 独立ビルド(Stage2a/Stage2b) | **4/4 完全バイト一致**(マスク無し、SHA256) |
| stage3 == stage4(自己ホストフィックスポイント) | **4/4 完全バイト一致** |
| `build-libs-core.ps1`(Nemerle.Linq) | ビルド成功(71,168 bytes) |
| CLR4 スモーク(Stage1 ncc.exe ネイティブ実行) | hello: `Hello from stage-test!` / hello2: `1`→`2, 4, 6`、全 exit 0 |
| testsuite 全数 | (下記 7.4.1) |

付随して 2 点: (1) Stage1 の `NTargetName=Rebuild` が `bin\Release\net-4.0\TestFramework`
(CLR4 testsuite ハーネス)を巻き添えで消すことが判明。
`msbuild NemerleAll.nproj /t:TestFramework /p:NCurBin=<Stage1 絶対パス>`(+通常の /tv /p 群)で
再生成できる(NCurBin 未指定だと `$(Nemerle)` が空になり MSB4019)。再生成時の
Nemerle.Diff テストは全 PASS。(2) §5-5 の影響確認: VsIntegration / snippets\VS2010 で
`GeneratedAssemblyVersion` を使うのは両ツリーの `Nemerle.Compiler.Utils\Properties\AssemblyInfo.n`
のみ(マクロ属性経由の間接利用)。`GetRevisionGeneric` の直接呼び出しは macros 内のみ。
`GetGitTagRevision`(MSBuild task)は `misc\packages\wix` と VS2010 csproj のみが利用
(いずれも本ポートでは死んでいるが、レシピ同期のため C# 側も改修済み)。

#### 7.4.1 testsuite 全数

**positive 448/469、negative 166/167 = 614/636 — WP-N3 の baseline と同数、新規 regression 0。**
失敗 22 件の名前を突合し、40 log §の既知セット(C# パーサー 9、Unsafe 1、WPF 2
〔positive notifypropertychanged + negative notifypropertychanged〕、BCL 面 6 + serialize、
security-asm、resource、gtk)と**完全一致**を確認。

### 7.5 seed refresh(publish-boot)と発見: origin フォールバック欠落

`publish-boot.ps1` 実行時、ローカルに `boot-net10` ブランチが無く(前回 WP の後は
remote-tracking `origin/boot-net10` のみ)、スクリプトが**新しい root コミットの orphan
ブランチを作ってしまった**(push すると force-push になり 630 seed 履歴が消える状態)。
`git branch -D` + `git tag -d` で破棄し、`git branch boot-net10 origin/boot-net10` で
ローカルブランチを再建してから再実行して解決:

- boot-net10: `7f3c854e8`(seed 1.2.0.635 from 0cab66afe)→ 親 `d8d5120c9`(630)。
  fast-forward push 可能な形。
- `seed/1.2.635` → 7f3c854e8(lightweight)。加えて旧世代 seed `d8d5120c9` にも
  `seed/1.2.630` を付与(orphan 側タグは describe に無害 — §6.2(c))。
- publish-boot の A2(1.2.0.635 OK)・スモークとも PASS。

**publish-boot のギャップとして記録**: ローカルブランチ不在時は `origin/<branch>` から
ローカルブランチを再建して積むべき。修正はリリース後の follow-up コミットで行う
(リリース対象コミット 0cab66afe には含まれない — 消費側 build-from-boot には無関係の
メンテナー専用経路のため受容)。

### 7.6 fixture 1b + 初回発行 asset 生成(canonical clone)

release タグ `release/1.2.635-preview.1`(lightweight)を実ソースコミット `0cab66afe` に
作成後、canonical path **`C:\Users\kenta\nemerle-release`** に使い捨て clone を作成
(`core.longpaths=true` — §7.2 の MAX_PATH 対策)。実測:

| 測定 | release タグあり | release タグ一時削除 |
|---|---|---|
| 旧レシピ describe | `release/1.2.635-preview.1-0-g0cab66afe`(**壊れていた挙動**) | — |
| 新レシピ describe | `v1.2-635-g0cab66afe` | 同一(**不変**) |
| A2 期待値(replay) | `1.2.0.635` | 同一(**不変**) |

全チェーン検証(受け入れ 1b の残り)は
`pwsh dotnet-port/build-from-boot.ps1 -ReleaseTag release/1.2.635-preview.1` を同 clone で
実行(= **初回発行 asset の生成そのもの**。§6.2(d) の設計により、発行と再現が同一経路・
同一絶対パス・pinned worktree `.boot-build-tree` になる)。結果: **完走**。
`-ReleaseTag` が suffix `preview.1` を導出して seed `seed/1.2.635` と突合し、常時 worktree
経路で release set 6 点(nupkg ×3 `1.2.635-preview.1` + `vscode-nemerle-0.9.0.vsix` +
README.md + release-info.json)を `dist/release-from-boot` に生成。release-info.json は
commit `0cab66afe` / seed `7f3c854e8`(tag `seed/1.2.635`)/ 世代一致を記録(受け入れ 3 充足)。
軽微な後始末問題: チェーン末尾の `git worktree remove` が長パスで exit 255
(警告どまり、成果物に影響なし。手動 `rd /s /q` で除去可)。

### 7.7 発行前検証 → push → GitHub Release 発行 → 受け入れ 4

1. **発行前検証**: 生成 asset のみを feed に、クリーンな一時ディレクトリーで
   `dotnet new install Nemerle.Templates.Unofficial::1.2.635-preview.1 --add-source <feed>` →
   `dotnet new nemerle-console` → NuGet.config(README §1.2 のとおり feed を登録)→
   `dotnet build`(0 エラー)→ `dotnet run` = `Hello from Nemerle on .NET 10!`。
   ※ 最初 NuGet.config を置き忘れて MSB4236(SDK 解決不可)になった — README §1.2 が
   必須手順であることの再確認になった。
2. **push(PO 承認)**: `wip/dotnet-port`(0cab66afe)、`boot-net10`(7f3c854e8、
   fast-forward)、タグ `seed/1.2.630` / `seed/1.2.635` / `release/1.2.635-preview.1`。
3. **GitHub Release 発行**: gh CLI を winget で導入し(オーナーが `gh auth login`)、
   `gh release create release/1.2.635-preview.1 --prerelease` で asset 6 点を添付。
   → https://github.com/matarillo/nemerle/releases/tag/release/1.2.635-preview.1
4. **受け入れ 4**: `gh release download` で asset を別ディレクトリーへ取得(6 点とも
   発行元と SHA256 完全一致 = アップロード完全性も確認)し、その asset **のみ**を使って
   1 と同じ install → new → build → run が成立(`Hello from Nemerle on .NET 10!`、exit 0)。

### 7.8 受け入れ 5: 発行済みリリースの再現

canonical clone を完全削除 → **GitHub から** `https://github.com/matarillo/nemerle.git` を
同一パス `C:\Users\kenta\nemerle-release` へ新規 clone(タグ 3 本・`origin/boot-net10` が
公開状態から揃うことを確認)→ 同一コマンド
`pwsh dotnet-port/build-from-boot.ps1 -ReleaseTag release/1.2.635-preview.1` で再ビルド →
初回発行 asset とバイト比較。結果は §7.8.1。

#### 7.8.1 バイト比較の実測: 版一致は完全、バイト一致はメタデータ 3 種を除き成立

再現ビルドは完走し、**版・構成・release-info.json の同一性フィールド(commit / seed /
nemerleAssemblyVersion)は完全一致**。ファイル単位の SHA256 では README.md のみ一致、
nupkg ×3 / VSIX / release-info.json が不一致だったため、zip 内エントリー単位まで
分解して原因を確定した:

| 偏差 | 実測 | 性質 |
|---|---|---|
| NuGet pack の非決定性 | `.psmdcp`(GUID ファイル名 + 作成時刻)と `_rels/.rels`(その参照)のみ | NuGet ツーリングの構造的制約(Templates / Linq nupkg は**これだけ**が差 = 実コンテンツ完全一致) |
| 自前 provenance の時刻 | `ncc-info.json` / `bundle-info.json` / `release-info.json` の `packedAtUtc` / `createdAtUtc` 1 行のみ | 設計どおりの記録(封緘時刻) |
| C# 製補助アセンブリ | CoreEmit / Hosting / MSBuild.Tasks / LanguageServer / ProjectInfo の dll+pdb。CoreEmit 実測: **同サイズ・72 バイト差・先頭 0x88**(COFF タイムスタンプ / MVID / PDB id 領域) | PE ビルドメタデータのみ(コード実体は同一サイズ・同一内容) |

**Nemerle 製アセンブリ(ncc.exe / Nemerle.dll / Nemerle.Compiler.dll / Nemerle.Macros.dll /
Nemerle.Linq.dll = WP-N1 の決定化対象)と静的コンテンツ(テンプレート・targets・README・
拡張の JS/TS 資産 468 エントリー中 463)はすべてバイト一致**。つまり WP-N1 の決定性は
seed → 再現の全経路で健在で、残差は「コンパイラーの成果物」ではなく「パッケージング層の
ビルドメタデータ」に限定される。

受け入れ 5 の判定: **版一致 = 充足。バイト一致 = 上記 3 種の既知偏差を除き充足**(偏差は
いずれも列挙・原因特定済みで、コード実体の差は無い)。完全なコンテナレベルのバイト一致は
NuGet の psmdcp 非決定性が塞いでおり、本 WP では追わない(→ §13 バックログ 8)。
36 §6 の基準文言「バイト/版一致」に対しては、この偏差リストつきでの成立を PO 報告とする。

## 8. 検証(受け入れ基準との対応)

36 §6 WP-N4 の受け入れ基準 6 項目すべてを充足:

| # | 基準 | 結果 | 証跡 |
|---|---|---|---|
| 1a | 既存タグのみで修正前後の describe 不変(no-op) | **PASS** | §7.2(使い捨て clone、旧=新 `v1.2-634-g...`) |
| 1b | `release/` タグを打っても AssemblyVersion / A2 期待値 / build-from-boot 全チェーン不変 | **PASS** | §7.6(タグ有無で describe / A2 = `1.2.0.635` 不変、全チェーン完走・版正しい) |
| 2 | §5-2 回帰ゲート green | **PASS** | §7.4(stage2×2・stage3==stage4 バイト一致、testsuite 614/636 = baseline、CLR4 スモーク) |
| 3 | release set 封緘、release-info.json の commit・seed hash とタグの整合 | **PASS** | §7.6(commit 0cab66afe / seed 7f3c854e8 / tag seed/1.2.635 / 世代一致) |
| 4 | 別環境で asset のみから install → `dotnet new` → build → run | **PASS** | §7.7(GitHub からダウンロードした asset のみ、SHA256 一致確認込み) |
| 5 | seed 番地 + リリースタグから再ビルドし初回発行物とバイト/版一致 | **PASS(版一致完全、バイト一致は既知偏差 3 種を除く — §7.8.1)** | GitHub からの新規 clone、同一パス・同一コマンド |
| 6 | DISTRIBUTION.md にタグ契約を記録 | **PASS** | DISTRIBUTION.md「版タグ契約(WP-N4)」節 + packaging/README.md For maintainers |

## 9. 回帰ゲート(実測値。§5-2 / §5-5)

- **stage2 ×2 独立ビルド**: 4 アセンブリ(Nemerle.dll / Nemerle.Compiler.dll /
  Nemerle.Macros.dll / ncc.exe)マスク無し SHA256 完全一致。
- **stage3 == stage4**(自己ホストフィックスポイント): 同 4 アセンブリ完全一致。
  (stage2 vs stage3 は既知の世代差により不一致が正しい — 37 log §4.2)
- **testsuite 全数**: positive 448/469 + negative 166/167 = **614/636**。
  失敗 22 件は WP-N3 時点の既知セットと名前まで完全一致(新規 regression 0)。
- **CLR4 スモーク**: Stage1 ncc.exe ネイティブ実行で hello(`Hello from stage-test!`)/
  hello2(`1` → `2, 4, 6`)とも exit 0。
- **VsIntegration / VS2010 grep**(§5-5): §7.4 の (2) — 影響は
  `GeneratedAssemblyVersion` マクロ経由の間接のみ、ソース個別改修は不要。
- LSP / extension 系スイートは本 WP の改修範囲(macro の describe 引数 + ps1 + 文書)に
  server/engine 変更が無いため §5-2 の定義どおり対象外。ただし release set 生成チェーン内で
  `npm run package` の unit テスト(projectDiscovery ほか)は毎回実行され PASS している。

## 10. 再現コマンド

```powershell
# --- 契約の no-op / 不変性確認(任意の checkout で) ---
git describe --tags --long --match 'v[0-9]*'     # = v1.2-<rev>-g<sha>(release/seed タグに影響されない)

# --- 回帰ゲート ---
Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
pwsh dotnet-port/refresh-stage1-core.ps1
pwsh dotnet-port/build-stage2-core.ps1 -OutDir bin/Release/core/Stage2a
pwsh dotnet-port/build-stage2-core.ps1 -OutDir bin/Release/core/Stage2b
pwsh dotnet-port/compare-stage.ps1 -DirA bin/Release/core/Stage2a -DirB bin/Release/core/Stage2b
pwsh dotnet-port/build-stage2-core.ps1
pwsh dotnet-port/build-stage2-core.ps1 -Compiler bin/Release/core/Stage2/ncc.exe -OutDir bin/Release/core/Stage3
pwsh dotnet-port/build-stage2-core.ps1 -Compiler bin/Release/core/Stage3/ncc.exe -OutDir bin/Release/core/Stage4
pwsh dotnet-port/compare-stage.ps1 -DirA bin/Release/core/Stage3 -DirB bin/Release/core/Stage4
pwsh dotnet-port/build-libs-core.ps1
pwsh dotnet-port/run-testsuite-core.ps1          # 期待: positive 448/469, negative 166/167
# testsuite ハーネスが無い場合(Stage1 Rebuild が消す — §11-4):
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:Configuration=Release "/p:NCurBin=$PWD\bin\Release\net-4.0\Stage1" /t:TestFramework

# --- seed refresh + タグ(メンテナー、Windows) ---
pwsh dotnet-port/publish-boot.ps1                # A2 + スモーク + orphan コミット + seed/1.2.<rev> タグ
git push origin boot-net10 seed/1.2.<rev>

# --- リリース発行(canonical path の使い捨て clone で) ---
git tag release/1.2.<rev>-preview.<N> <実ソースコミット>   # lightweight
git push origin release/1.2.<rev>-preview.<N>
git -c core.longpaths=true clone https://github.com/matarillo/nemerle.git C:\Users\kenta\nemerle-release
cd C:\Users\kenta\nemerle-release; git config core.longpaths true
pwsh dotnet-port/build-from-boot.ps1 -ReleaseTag release/1.2.<rev>-preview.<N>
gh release create release/1.2.<rev>-preview.<N> --prerelease --title "..." --notes-file <notes> dotnet-port\dist\release-from-boot\*

# --- 発行済みリリースの再現(受け入れ 5 と同一) ---
#   同じ canonical path へ新規 clone → 同じ -ReleaseTag コマンド。バイト一致は同一絶対パス時のみ。
```

## 11. 発見事項

1. **boot-4.0 の旧レシピ焼き込み(契約の境界)**: AssemblyVersion を刻むのは「ビルドを
   実行するコンパイラーの `Nemerle.Macros.dll`」であり、ソースの macro ではない。
   したがって `--match` 契約が有効なのは **macro バイナリが世代 635 以降のコンパイラー**
   (= 今回の seed 以降の boot-net10 / stage2)だけで、凍結済み boot-4.0 は旧レシピのまま。
   release タグが祖先に付いた checkout で boot-4.0 → Stage1 をフルリビルドすると、旧レシピが
   release タグを拾い不正版(例 `1.2.635..1`)で**大きな音を立てて失敗**する。
   seed/ タグは orphan 上で main の祖先に乗らないため旧レシピにも常に不可視。
   回避は §12-3、根治は WP-N7(版ピン留め)。
2. **annotated タグは pack 系 provenance を汚染し得る**(fixture 1a で実証): `--tags` 無し
   describe は annotated タグだけを見るため、release タグを annotated で作ると
   ncc-info / bundle-info の describe 文字列 = nupkg / VSIX のバイトが変わる。
   → 「lightweight 必須」を契約に格上げ + 全 provenance describe に `--match` の二重防御。
3. **publish-boot の origin フォールバック欠落**: ローカル `boot-net10` ブランチが無いと
   (fresh clone 後の通常形)、既存履歴に繋がらない新 root orphan を作ってしまい、push が
   既発行 seed の破壊(force-push)になるところだった。§7.5 で運用回避し、恒久修正
   (origin/boot-net10 からローカルブランチを再建)を follow-up コミットに収録。
4. **Stage1 の `NTargetName=Rebuild` は testsuite ハーネスを巻き添えにする**:
   `bin\Release\net-4.0\TestFramework` が消え、`/t:TestFramework` は `NCurBin` を明示しないと
   `$(Nemerle)` 未定義で MSB4019 になる。再生成コマンドを §10 に恒久記録。
5. **環境: MachineKeys ACL 矯正による CS1548**(§7.3)。標準 ACL の「Everyone: 特殊書き込み」が
   RX に落ちていると CLR4 強名署名が非昇格で失敗する。PO 承認の昇格 icacls で解消(恒久)。
6. **環境: 素の clone は Windows MAX_PATH に当たる**(VS2010 テンプレート群のパス長)。
   リリース/再現用 clone は「短い実パス + `core.longpaths=true`」を標準とする。

## 12. 新しく持ち込んだ制約 / 次 WP への境界(本 WP で確定・文書化)

1. **タグ契約**(DISTRIBUTION.md に記録): リリースタグ `release/1.2.<rev>-preview.<N>` は
   実ソースコミットへ、seed タグ `seed/1.2.<rev>` は orphan seed コミットへ。
   **v 非開始 + lightweight が必須**。`v*` は upstream の名前空間 = 版計算の予約領域。
2. **契約修正前の世代(1.2.630 以前)にはリリースタグを打てない**(旧 macro バイナリが
   除外規則を知らないため)。タグ付きリリースは 1.2.635 が最初。
3. **boot-4.0 → Stage1 フルリビルドは release タグの一時退避が必要**(発見 1)。
   `git tag -l 'release/*' | ForEach-Object { git tag -d $_ }` → リビルド → `git fetch --tags`。
   失敗しても A2 / ビルドエラーで機械検出される(静かな破損はしない)。WP-N7 が根治。
4. **バイト再現の成立条件**: 同一マシン・同一絶対パス(canonical path、本機では
   `C:\Users\kenta\nemerle-release`)・`-ReleaseTag`(常時 pinned worktree)経路。
   他環境では版・内容一致のみ(WP-N5 §5.1 の assert パス埋め込みが原因。恒久対処は
   バックログ)。
5. **WP-N7 への境界**: `--match` 契約は版ピン留め後も Informational 側で生存(§5)。
   WP-N7 の評価は「boot-4.0 旧レシピ問題(発見 1)の根治」を動機に加えること。
6. **WP-N6 への境界**: release workflow(stretch)は本 WP の
   `build-from-boot -ReleaseTag` をタグ push トリガーで呼ぶだけの形に確定できる。
   seed 検出(A2 流用)・seed/ タグ・release-info.json の seed 記録はそのまま CI の
   部品になる。

## 13. WP 終了後にバックログ起票予定のメモ

1. **release workflow の自動化**(WP-N6 stretch の下敷き): タグ push →
   `build-from-boot -ReleaseTag` → `gh release create`。手動手順は §10 に確立済み。
2. **assert メッセージの絶対パス埋め込みの repo 相対化**(コンパイラー改修):
   成立すればバイト再現のパス条件(§12-4)が消え、公式 asset と第三者再ビルドの
   バイト照合が可能になる。WP-N5 からの継続項目の優先度を再評価。
3. **testsuite ハーネスの保護**: Stage1 Rebuild に巻き込まれない場所への移設、または
   `run-testsuite-core.ps1` にハーネス不在時の再生成案内(§10 のコマンド)を組み込む。
4. **git < 2.7 環境向けの `--match` フォールバック**(macro 内タグ名後処理)。現時点で
   対象環境が無いため未実装(§6.5)。
5. **テンプレート/README の feed 配線改善の検討**: 受け入れ 4 で NuGet.config の置き忘れが
   MSB4236 になることを再確認(README §1.2 は必須手順)。nuget.org 公開(WP-O)で
   自然消滅する項目のため、WP-O の設計に注記として引き継ぐ。
6. **build-from-boot 後始末**: 長パス環境で `git worktree remove` が exit 255(警告どまり)。
   `rd /s /q` フォールバックの追加を検討。
7. **gh CLI の導入を維持**(winget 導入済み・オーナー認証済み)。次回リリースは §10 の
   コマンドがそのまま使える。
8. **パッケージング層のバイト再現の残差**(§7.8.1): (a) NuGet psmdcp の GUID+時刻
   (NuGet/Home#8601 系の既知課題 — pack 後の zip 正規化 post-process で消せる)、
   (b) ncc-info / bundle-info / release-info の `packedAtUtc` を `-ReleaseTag` 再現時は
   タグ/コミット時刻に固定する案、(c) C# 補助アセンブリ 5 本の PE メタデータ差の原因調査
   (72 バイト・ヘッダー領域のみ。csc Deterministic 既定 true とは食い違う挙動 — 要調査)。
   完全なコンテナレベル一致を要求する必要が生じたら(公証・supply chain 検証等)着手。
