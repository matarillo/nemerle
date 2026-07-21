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

## 6. 判断

1. **Q1(構成)= 案 A**。N7 case 1 実装を Step 0 として先行させる。
2. **Q2(既存 workflow)= `build.yml` 削除**。`windows-2016` runner は退役済みで動かず、
   かつ `on: pull_request` により全 PR に必ず落ちるチェックが 1 本付くため。
3. **Q3(stretch)= 含める。マニュアルトリガーで発行まで自動化する**。
   `workflow_dispatch`(入力 `release_tag` + `stage`)。**2 段**を選べる:
   `build-smoke`(ビルド + 消費者スモークで停止する dry run)と
   `build-smoke-release`(続けて **GitHub Release へ発行**)。発行はスモーク通過を必須ゲートとし、
   `stage` でガードする。トリガーをタグ push でなくマニュアルにするのは、タグを push した勢いで
   意図せず発行されないようにするため。
4. **Q4(CI トリガー)= `wip/dotnet-port` への push / PR で自動起動。ただし `**/*.md` のみの
   変更は除外**(`paths-ignore`)。手動起動(`workflow_dispatch`)も可。ドキュメントだけの
   コミットでツールチェーン全体を再ビルドしないため。

## 7. 実装結果

### Step 0(WP-N7 case 1)= N6 に必要な範囲で完了

版ピンの成立 → in-tree seed 化と orphan 廃止 → A2 の commit 照合化まで達成
(log 44 §8.2 / §8.4 / §8.5)。これにより CI は seed の世代へ退避せず push/PR の HEAD を
そのままビルドでき(§2 の空振り解消)、seed が古い場合の警告は
`Test-NemerleProvenanceCommit -WarnOnly` が担う。残る「バンプ手順のスクリプト整備」
(log 44 §8.6)は Windows/CLR4 限定で N6 をブロックしない。

### Step 1(CI workflow)

#### 7.1 変更ファイル

| ファイル | 内容 |
|---|---|
| `dotnet-port/verify-seed.ps1`(新規) | seed の schema / version.txt スパン / 全ファイル SHA256 / provenance コミット照合。`-WarnOnly` で provenance 不一致を警告へ降格。CI は release 封緘を行わず `build-from-boot.ps1` を丸ごと呼べないため、seed 検証をここへ抽出 |
| `dotnet-port/smoke-release.ps1`(新規) | 消費者スモーク。feed からテンプレートを install → `nemerle-console` を生成 → build → run し `Hello from Nemerle on .NET 10!` を確認。梱包物が実際に install/build/run できるかを見る唯一の検査(他の suite は MSBuild 評価で止まる)。CI・release workflow・手元で共用 |
| `dotnet-port/build-from-boot.ps1` | §3 のインライン seed 検証を `verify-seed.ps1` の子プロセス呼び出しへ置換(振る舞いは同一) |
| `.github/workflows/dotnet-port-ci.yml`(新規) | push/PR CI 本体 |
| `.github/workflows/dotnet-port-release-build.yml`(新規) | Q3 の release workflow(build → smoke → publish) |
| `.github/workflows/build.yml` | 削除(§6 Q2) |

#### 7.2 CI workflow(`dotnet-port-ci.yml`)= as-built のパイプライン

**目的**: push/PR の HEAD をチェックイン seed からビルドし、消費者レベルまで検証する。
版ピン(Step 0)により seed の世代へ退避せず HEAD そのものがビルド対象になる(§2 解消)。

**トリガー**(§6 Q4): `wip/dotnet-port` への push と、同ブランチ宛の pull_request。
`paths-ignore: **/*.md`(ドキュメントのみの変更は起動しない)。加えて `workflow_dispatch`
で手動起動可。`concurrency` で同一 ref の先行 run をキャンセル。`permissions: contents: read`。

**ジョブ**: 単一ジョブ・`runs-on: ubuntu-24.04`・`timeout-minutes: 45`・
`defaults.run.shell: pwsh`。ツールチェーン: .NET SDK 10.0.x(`setup-dotnet@v4`)/
Node 22(`setup-node@v4`、npm キャッシュ)/ pwsh は runner 標準。

**ステップ(実行順)**:

| # | 内容 | 要点 |
|---|---|---|
| 1 | `actions/checkout@v4`(`fetch-depth: 0`) | version-pin の describe フォールバック・provenance の祖先判定・`ncc-info.json` の describe 記録が全履歴とタグを要る |
| 2 | `setup-dotnet@v4` 10.0.x / `setup-node@v4` 22 | node は npm キャッシュ(`package-lock.json` キー) |
| 3 | `verify-seed.ps1 -WarnOnly` | seed の schema / 版スパン / SHA256 / provenance。古い seed は情報行、非祖先は警告(止めない) |
| 4 | `build-stage2-core.ps1 -Compiler dotnet-port/seed/ncc.exe` | seed が **この HEAD** の stage2 をビルド |
| 5 | `build-libs-core.ps1` | Nemerle.Linq |
| 6 | `pack-tool.ps1 -Pack` | `dist/ncc` レイアウト + 3 nupkg(feed) |
| 7 | `vscode-nemerle/pack-server.ps1` | LSP サーバをビルド・staging |
| 8 | samples 全 `.nproj` の `dotnet restore` | 42 §5.2。clean checkout では `-getItem` が restore せず NETSDK1004 で落ちるため明示 restore |
| 9 | `npm ci` + `npm run test:unit` | 拡張の unit |
| 10 | `LspServer.IntegrationTest`(`-t:Rebuild` → `dotnet exec`) | raw LSP(実測の最大コスト) |
| 11 | `ProjectInfo.Test -- --integration` | unit + Sdk パッケージ評価 |
| 12 | `smoke-release.ps1` | 消費者スモーク。install → new → build → run。上の suite が MSBuild 評価で止まる穴を埋める唯一の検査 |

**CI 対象外(意図的)**: testsuite 全数 / boot-4.0 → stage1 再生成と CLR4 スモーク(以上
36 §6。CLR4 ハーネス・Windows 専用のため手動回帰ゲートとして維持)/ バイト決定性比較
(フルビルド 2 回を要し実行時間が倍。決定性は seed refresh・リリース儀式が担保)。

#### 7.3 release workflow(`dotnet-port-release-build.yml`)

`workflow_dispatch` のみ。入力は `release_tag`(push 済みタグ。正規表現で検証、`env:` 経由で
渡す)と `stage`(choice)。`preview.<N>` の `<N>` は `release_tag` の suffix
(`release/1.2.635-preview.2` → N=2)であり、専用入力は設けない。

- **`stage = build-smoke`(既定)**: tag を checkout → `build-from-boot.ps1 -ReleaseTag` →
  `smoke-release.ps1` → `dist/release/` を artifact 保存で**停止**。リリース固有経路
  (pack-release 封緘・VSIX・release-info.json)を副作用なしで空試験する dry run。
- **`stage = build-smoke-release`**: 上に続けて **GitHub Release へ発行**。タグに Release が
  無ければ `gh release create`、有れば `gh release upload --clobber`。タグ形が `-<suffix>` 必須
  ゆえ常に `--prerelease`(= Latest に付かない)。

発行ステップは `if: stage == build-smoke-release` でガードし、スモーク通過後にのみ到達する。
`permissions: contents: write`(build-smoke ではトークンを使わない)。

#### 7.4 検証結果

**push CI(`dotnet-port-ci.yml`)= green**。`wip/dotnet-port` への push で自動実行され、
全ステップ成功(run 29876764811、`ubuntu-24.04`)。**総実行時間 3 分 54 秒**(受け入れ目安
15 分に対し十分内。キャッシュ導入は不要)。ステップ別(主要):stage2 38s / raw LSP 104s
(最大)/ その他は各 4–11s。`ProjectInfo.Test --integration` = 5s PASS、
`smoke-release.ps1` = 7s PASS — §4 で未実測だった 2 点はいずれも Linux で成立。

初回 Linux 実行で 1 件の移植性欠陥が露呈し修正済み: `ProjectInfo.Test` の `ParserTests` が
ケース違い重複ソースの dedup を「常に 1」と決め打ちしていた(Windows 前提)。
`ProjectPathNormalizer` は設計どおり Windows のみ case-fold・Linux/macOS は case-sensitive
なので、正しい数は Linux で 2。プラットフォーム対応の期待値へ修正
(`PathNormalizerTests` と同じ分岐方針)。

ローカル(Windows)で確認済み: `verify-seed.ps1` 正常系/異常系、`smoke-release.ps1` フル走行、
`ProjectInfo.Test`(unit)、workflow 2 本の YAML パース。

**未実測**: release workflow の発行経路(`gh release create/upload`)。build-smoke 段は
副作用が無いが、いずれも現行 HEAD にタグを付けて起動する必要があり、実発行は
publish 操作を伴うため PO 主導で行う。

#### 7.5 申し送り

- release workflow が発行を担うようになったため、`DISTRIBUTION.md` / `packaging/README.md` の
  リリース手順を「タグ push 後にこの workflow を起動して発行する」形へ更新すること。**未実施**。
- release workflow の end-to-end 初回起動(build-smoke → build-smoke-release)を PO が実施し、
  結果をここへ追記する。
