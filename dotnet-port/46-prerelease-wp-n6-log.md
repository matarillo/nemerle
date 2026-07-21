# 45. WP-N6: 最小 CI(Linux)設計ログ

対象 WP: 36-prerelease-quality-plan.md §6 WP-N6(任意・go/no-go 判断付き)。

本 log は WP-N6 の設計と go/no-go の判断材料を保持する。
§1–§5 は設計、§6 が PO 判断、§7 が実装結果。

**着手時に判明した計画の欠陥(2026-07-21)**: 36 §8 は N6 と N7 の依存を
「N7 の**判断**が先」と書いていたが、正しくは「N7 の**実装**が先」だった。go の場合に
N6 が乗る seed 機構は N7 の実装が作るものであり、判断だけでは前提が存在しない。
その結果 N7 は「部分 GO、実装は別途」(44 §7.6)で閉じられ、N6 は着手不能な状態で
「次」に並んでいた。36 §8-6/7 を修正済み。

## 1. 前提の現在地

| 前提 | 状態 |
|---|---|
| WP-N4(タグ契約 + seed 番地付け + 初回 Release) | 完了(log 41)。CI の版整合検査が乗る土台 |
| WP-N5(Linux 検証)+ boot-net10 | 完了(log 42・43)。Ubuntu 26.04 実 VM で新規 clone → `build-from-boot.ps1` → release set 生成まで完走 |
| WP-N7(版ピン留め)の go/no-go | **部分 GO(2026-07-21、PO 合意。log 44 §7)。ただし case 1 の実装は未着手**(44 §7.6) |

36 §8-7 は「N7 が go なら版ピン留め後の seed 機構上に構築」と定めており、N7 の結論は
出たが**その seed 機構自体がまだ存在しない**。これが本 WP の最初の分岐になる。

## 2. 中心論点: orphan ベースの CI は push/PR の HEAD を検証できない

`build-from-boot.ps1` は HEAD の世代が seed と異なると pinned worktree 経路を取り、
**seed の世代コミットを checkout してそちらをビルドする**(44 §1-2 で明文化済み)。
CI に当てはめると:

- push/PR で世代は原則毎回進むため、CI は常に pinned worktree 経路に落ちる。
- そこでビルド・テストされるのは **seed 世代のソースの再現**であって、**その push/PR の
  変更内容ではない**。コンパイラ・engine・LspServer への変更は一切コンパイルされない。
- 得られる価値は「seed がまだビルドできること」「seed 鮮度の警告」のみ。push/PR CI の
  本来の目的(HEAD の検証)に対してほぼ空振りになる。

36 §6 の WP-N6 スコープ(「seed が古い場合の警告は A2 流用検査をそのまま使う」)は
N7 判断前に書かれたもので、この空振りを鮮度警告で緩和する前提だった。N7 が部分 GO と
なった今、**版ピン留め(同一 version.txt スパン内なら古い seed で任意 HEAD をビルド可)を
先に実装すれば、CI は HEAD そのものをビルド・テストできる**。構成が根本的に良くなる。

## 3. 構成の選択肢と推奨

| 案 | 内容 | 評価 |
|---|---|---|
| **A(推奨)** | **N7 case 1 の実装を先行**(version.txt、env ピン配線、A2 の commit 照合化、in-tree seed 化と orphan 廃止 = 44 §7.3–7.4)し、その上に CI を構築 | CI が push/PR の HEAD を直接ビルド・テストできる。workflow は「checkout → 環境 → ビルドチェーン → テスト」の素直な形。二度作りが無い |
| B | orphan ベースで即構築 | 今日動く。ただし §2 のとおり HEAD を検証できず、N7 実装後に seed 取得・世代判定まわりを作り直す。実装コストの大半(runner 環境整備・時間計測)は A と共通なので、先行して得るものが薄い |

推奨は **A**。N7 case 1 の実装は 44 §7.6 で「別途実装ステップ」とされており、
本 WP の前提工程(Step 0)として先に実施する。N6 自体のスコープ(workflow 化のみ、
36 §6)は変えない。

## 4. 提案スコープ(案 A 前提)

### Step 0(前提工程 = WP-N7 case 1 実装、log 44 側で管理)

version.txt 導入 / リリース経路スクリプトの env ピン配線 / `assembly-version-check.ps1` の
commit 照合化 / in-tree seed 化と orphan・pinned worktree 廃止 / バンプ手順のスクリプト整備。
実装詳細と結果は log 44 §7.6 以降へ追記する(本 log では扱わない)。

### Step 1: CI workflow(本体)

- 新規 `.github/workflows/` に Linux workflow を追加。トリガー: `wip/dotnet-port` への
  push と pull_request。
- runner: `ubuntu-24.04`(実証済み環境は Ubuntu 26.04 VM / SDK 10.0.110 / pwsh 7.6 /
  Node 22。runner 側は setup-dotnet 10.0.x + setup-node 22 で揃える。pwsh は runner 標準)。
- checkout は `fetch-depth: 0`(git describe / タグ契約 / provenance commit 照合に全履歴と
  タグが必要)。
- 実行チェーン(N7 実装後の in-tree seed 起点、HEAD を直接ビルド):
  seed 検証(commit 照合検査)→ `build-stage2-core.ps1` → `build-libs-core.ps1` →
  `pack-tool.ps1 -Pack` → `pack-server.ps1` → `npm ci` + `npm run test:unit` →
  `LspServer.IntegrationTest`(raw LSP 29)→ `ProjectInfo.Test`。
- **CI 対象外(36 §6 で固定済み)**: testsuite 全数(ハーネスが CLR4 実行ファイル、
  バックログ §10-1)/ boot-4.0 → stage1 再生成と CLR4 スモーク(Windows 専用、
  seed refresh 時の手動回帰ゲートとして維持)。
- (**stretch**)release タグ push 起点の release workflow(`build-from-boot.ps1
  -ReleaseTag` 相当の自動化 + GitHub Release への assets 添付)。未実施でも完了を妨げない。

### 実装時に確認する点

- `ProjectInfo.Test` の Linux 実測は未実施(WP-N5 の Linux 実測リスト(42 §9)に
  含まれていない。unit は問題ない見込み、`--integration` は実 MSBuild query のため要確認)。
- 実行時間。VM(2 vCPU)でフルチェーン完走の実績はあるが所要時間は未記録。受け入れ
  目安 15 分を超える場合は実測を記録してスコープ再判断(36 §6)。NuGet / npm キャッシュの
  導入は超過した場合の第一手。
- 既存 `.github/workflows/build.yml`(upstream 由来、`windows-2016` runner)は GitHub 側で
  runner image が退役済みのため動かない死に workflow。扱い(削除 or 置換)は PO 確認(§6 Q2)。

## 5. 受け入れ基準(36 §6 のまま)

go の場合: push/PR で自動実行され green。実行時間の目安 15 分以内(超える場合は実測を
log に記録しスコープを再判断)。no-go の場合: 判断理由を本 log に記録しバックログ
(§10-1)へ。stretch は未実施でも完了を妨げない。

## 6. PO 判断

1. **Q1(構成)= 案 A で進める(2026-07-21 PO 合意)**。N7 case 1 実装を Step 0 として
   先行させる。これは「WP-N7 の実装に着手する」判断を兼ねる。
2. **Q2(既存 workflow)**: 死んでいる `build.yml`(windows-2016)の扱い(削除 / 新 workflow で
   置換 / 温存)— **PO が別途検討**(2026-07-21)。結論が出るまで温存し、触らない。
   Step 1 の workflow は既存 `build.yml` と独立した新規ファイルとして追加するため、
   この判断は Step 1 の着手をブロックしない。
3. **Q3(stretch)**: release workflow をスコープに含めるか — **PO が別途検討**(2026-07-21)。
   36 §6 のとおり「余裕があれば」の扱いを既定とし、未実施でも本 WP の完了を妨げない。

## 7. 実装結果

### Step 0(WP-N7 case 1)

進行中。実装の詳細・実測は log 44 §8 に記録。本 log には N6 の前提として
成立したかどうかだけを引く。

**第 1 スライス(版ピンの成立)= 達成**(2026-07-21、log 44 §8.2)。
seed 635 で HEAD(638)の stage2 をビルドでき、出力は全て 1.2.0.635 で刻まれた。
自己ホスト・決定性(stage3 == stage3b の 4/4 バイト一致)も維持。
**これで §2 の空振り問題は原理的に解消**した — CI は seed の世代へ退避せず
push/PR の HEAD をそのままビルドできる。

**第 2 スライス(in-tree seed 化と orphan 廃止)= 達成**(2026-07-22、log 44 §8.4)。
seed は `dotnet-port/seed/` にチェックインされ、`build-from-boot.ps1` は常に
このチェックアウトのソースをビルドする。**N6 の Step 1 が待っていたのはここ**で、
これで workflow の中身が確定できる:

- seed の入手 = 通常の clone(orphan ブランチの fetch も worktree も不要)。
- CI のビルド経路 = `build-from-boot.ps1` そのもの。push/PR の HEAD が
  そのままビルド対象になる。
- ただし CI は release set の封緘まで必要ないので、Step 1 では
  `build-from-boot.ps1` を丸ごと呼ぶのではなく、同じチェーンの前半
  (stage2 → libs → pack-tool → pack-server)+ テストに絞る。
  `build-from-boot.ps1` は clean tree を要求する(pack-release の前提)ため、
  CI で丸ごと呼ぶと PR の性質と噛み合わない場面がある。

残り(log 44 §8.3): A2 の commit 照合化 / バンプ手順のスクリプト整備。
前者は N6 の「seed 鮮度警告」の中身なので Step 1 と近接して実施する。

### Step 1(CI workflow)

(未着手)
