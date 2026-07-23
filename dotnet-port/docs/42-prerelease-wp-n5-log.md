# 42. WP-N5: Linux 実地検証とテスト基盤の cross-platform 化 実装ログ

実施日: 2026-07-17

ブランチ: `wip/dotnet-port`

開始 commit: `0687c7302`(`Close WP-N3: seal the 1.2.627-preview.1 release set`)

対象: `dotnet-port/docs/36-prerelease-quality-plan.md` の **WP-N5** のみ。
最小 CI の構築(WP-N6)は範囲外だが、その go/no-go 判断の前提となる検証
(checked-in stage1 起点の Linux ブートストラップ可否)は本 WP の成果物に含まれる。

## 結論

1. **テスト基盤の cross-platform 化(G3)完了**: `test:vsix` の PID→コマンドライン検査を
   platform 分岐化(Linux は `/proc/<pid>/cmdline` の直接読取り = サブプロセス不要、
   Windows は従来の `powershell.exe` + CIM を維持、その他 POSIX は `ps -o command=`)。
   パス比較の `normalize()` は win32 のみ小文字化+`\` 正規化とし、POSIX では
   大文字小文字を保持した verbatim 比較に修正した。
2. **ps1 スクリプト 9 本のパス区切りを cross-platform 化(F5 の前提)**:
   `Join-Path $x "a\b\c"` 形式のバックスラッシュ・リテラル 30 箇所超を `/` に統一
   (pwsh は Windows でも `/` を受容するため Windows の動作は不変)。セパレータ判定
   ロジック 4 箇所(`pack-tool.ps1` の `TrimEnd`/`TrimStart`)を両対応化。
   生成物 `ncc.cmd`(cmd.exe バッチ)と Windows 専用復旧手順の文言は意図的に原文維持。
3. **stage1(net4 フレーバー)の Linux `dotnet exec` 実行は成立**(WSL 実測、§4):
   checked-in 相当の Stage1 バイナリをそのまま Linux の .NET 10 に載せ、
   hello コンパイル→実行、さらに **stage1 → stage2 → Nemerle.Linq → `pack-tool.ps1`
   レイアウト生成まで全工程が Linux 上の `dotnet exec` + pwsh のみで完結**した。
   WP-N6 の CI アーキテクチャ(checked-in stage1 起点)の前提は成立。
4. **Linux 内の決定的ビルドは成立、ただしビルドはチェックアウトパス依存**(§5、本 WP の
   最重要発見): stage2 アセンブリは assert 系メッセージとして**ソースの絶対パスを文字列
   リテラルに埋め込む**ため、決定性は「同一チェックアウトパスなら同一バイト」。
   異なるディレクトリ・異なる OS 間のバイト比較は原理的に成立しない。
   WP-N6 の CI は「固定パス + 同一環境 2 回ビルドの自己一致」で設計する必要がある。
5. **主要テストスイートは Linux で実行でき、全 PASS**(WSL 実測 §4 → 実 VM で追認 §6):
   raw LSP 29 シナリオ、封緘 VSIX 0.9.0 の bundled server 29 シナリオ、
   npm unit 22 本、`samples/Defines` の DefineConstants 配線(G5)。
   前提として **samples の `dotnet restore` が必要**(§5.2 の発見)。
   さらに実 VM では **xvfb 下の実 VS Code で Extension Host スイート 3 種
   (test:vsix / test:integration / test:sdk)が全 PASS** し、封緘 VSIX の
   clean-machine インストール → bundled server 起動 → project-aware diagnostics
   までが無設定で成立した(受け入れ基準 1)。封緘セットのみからの
   `dotnet new nemerle-console` → build → run も成立(§6 phase C)。
6. Windows 側の全回帰ゲート PASS(§3): testsuite 614/636(失敗 22 件は WP-N3 の
   既知セットと完全一致 = 新規 regression 0)、raw lint/unit/integration/vsix、
   世代固定 worktree での stage2+pack 再現。

### 受け入れ基準(36 §6 WP-N5)との対応

| 基準 | 結果 |
|---|---|
| 1. Linux 上で VSIX install から project-aware diagnostics + hover/completion/definition が無設定で動作(clean-machine 相当、自動または記録された手動手順) | **PASS**(§6 phase D: xvfb 下の実 VS Code で `test:vsix` = 封緘 VSIX の隔離インストール→bundled server→診断、`test:integration` = hover/completion/definition/incremental を含む本体スイート。すべて自動、手順は §6 に記録) |
| 2. 主要テストスイート(raw LSP / bundled server / npm test 相当)が Linux で実行でき PASS | **PASS**(§6 phase B: raw LSP 29 / bundled 29 / npm unit 22。前提 = samples restore、§5.2) |
| 3. Linux 固有問題は修正または既知制約として記録 | **PASS**(修正: G3 platform 分岐・ps1 パス区切り。既知制約: パス埋め込み §5.1、restore 前提 §5.2、CLR4 ハーネス §8。engine 改修は発生せず回帰ゲート拡張は不要) |
| 4. Windows 側の全テストに回帰なし | **PASS**(§3: testsuite 614/636 = 既知 22 失敗と完全一致、lint/unit/integration/vsix、世代固定 worktree での stage2+pack 再現) |
| 5. Linux 上で checked-in stage1 相当を `dotnet exec` 実行し、stage2 相当を生成できるかの確認と記録 | **PASS**(§4 WSL + §6 phase B: stage1 実行・stage2 生成とも成立。WP-N6 の前提成立。制約 = パス依存 §5.1 を CI 設計に反映) |

## 1. 検証の基準点: 世代コミット固定(A2 チェックとの整合)

着手時の HEAD(`0687c7302`、封緘コミット)では `git describe` 由来の期待版が 1.2.0.628 に
進んでおり、既存の Stage1/Stage2(1.2.0.627)からのビルドは WP-N1 の A2 版一致チェックが
**設計どおり拒否する**(Windows・Linux 両方で実測。エラーメッセージと復旧手順の提示も
両 OS で機能した — これ自体が A2 チェックの Linux 動作確認になっている)。

本 WP はコンパイラー無改修であり、検証対象は封緘済みリリースセット 1.2.627-preview.1
(`release-info.json` の記録 commit `5245d5345` = HEAD~1)である。よって検証用
checkout(WSL クローン・Windows worktree)は **`5245d5345` に固定**し、その上に本 WP の
スクリプト修正(未コミット)を重ねた。これで Stage1(627)→ stage2(627)→ 封緘 VSIX /
nupkg(627)が全段で整合する。

## 2. 変更ファイル

| ファイル | 変更 |
|---|---|
| `vscode-nemerle/test/suite/vsix.test.ts` | G3: `processCommandLine()` の platform 分岐(linux=`/proc`、win32=CIM、他=`ps`)、`normalize()` の POSIX verbatim 化 |
| `pack-tool.ps1` | パス区切り `/` 統一 + セパレータ判定 4 箇所の両対応化 |
| `pack-release.ps1` / `build-stage2-core.ps1` / `build-libs-core.ps1` / `run-testsuite-core.ps1` / `refresh-stage1-core.ps1` / `assembly-version-check.ps1` | パス区切り `/` 統一(実行時パスのみ。コメント・Windows 専用復旧手順の文言は維持) |
| `vscode-nemerle/pack-server.ps1` / `vscode-nemerle/test-bundled-server.ps1` | 同上 |
| `packaging/README.md` / `vscode-nemerle/README.md` | Linux 手順・検証状態の実測ベース更新(§7) |

`compare-stage.ps1` は変更不要だった(コード中のリテラルパスが無い)。
コンパイラー(`ncc`/`lib`/`macros`)・engine(`VsIntegration`)・LSP server・extension 本体は
**無改造**。よって Stage リビルド・再 pack・provenance 更新は不要で、封緘済み
1.2.627-preview.1 セットはそのまま有効。

## 3. Windows 回帰(全 PASS)

| ゲート | 結果 |
|---|---|
| `npm run lint` / `npm run test:unit` | PASS / 22 本 PASS(TS 変更後) |
| `npm run test:integration`(本体 + Restricted Mode) | PASS |
| `npm run test:vsix`(修正済みテストの win32 経路) | PASS(bundled server 起動をコマンドラインで確認) |
| testsuite 全数(`run-testsuite-core.ps1`、修正版スクリプトで実行) | positive 448/469 + negative 166/167 = **614/636**。失敗 22 件は 40-log 記録の既知セット(C# パーサー 9 / Unsafe 1 / WPF 2 / BCL 面 6 + serialize / CAS 1 / -res 1 / gtk 1)と**完全一致 = 新規 regression 0** |
| 世代固定 worktree での `build-stage2-core.ps1` → `pack-tool.ps1`(修正版) | 成功(修正スクリプトの Windows 動作を実証) |
| CLR4 スモーク | N/A(共有ソース無改修のため §5-2 のゲート発動条件に該当せず) |

## 4. WSL 事前検証(補助、Ubuntu 24.04 / .NET SDK 10.0.109 / ext4)

WSL は計画どおり「補助」— 実 VM 検証(§6)の前に問題を洗い出し、往復を減らすために使った。
クローンは ext4 上(`/mnt` の大文字小文字非依存を避けるため)、`5245d5345` に固定。
Node 22.23.1 / pwsh 7.6.3 は apt(nodesource / packages.microsoft.com)で導入。

| 項目 | 結果 |
|---|---|
| stage1 の `dotnet exec` 実行(受け入れ基準 5 前半) | **成功**: Windows でビルドした `bin/Release/net-4.0/Stage1`(ncc.exe + 3 アセンブリ + CoreEmit + runtimeconfig)をコピーし、`dotnet exec Stage1/ncc.exe -out:hello.exe hello.n` → 生成 exe 実行まで成功 |
| stage1 → stage2 生成(受け入れ基準 5 後半) | **成功**: `pwsh dotnet-port/build-stage2-core.ps1`(修正版)が Linux 上で 4 アセンブリを生成。A2 版チェック・CoreEmit の dotnet build も Linux で機能 |
| Linux 内決定性 | **成功**: 2 回独立ビルド(Stage2 vs Stage2b)が 4 ファイル完全バイト一致 |
| `build-libs-core.ps1`(Nemerle.Linq) | 成功(70,656 bytes) |
| `pack-tool.ps1` レイアウト生成(F5) | 成功(`dist/ncc` 一式 + `ncc-info.json`) |
| `pack-server.ps1` | 成功(server 71 files staged。dirty 警告は想定どおり) |
| raw LSP integration suite | **29 シナリオ全 PASS**(`PASS all WP-L3 LSP integration scenarios`) |
| bundled server suite(封緘 VSIX 0.9.0 を展開) | **29 シナリオ全 PASS** |
| `npm ci` + `npm run test:unit` | 22 本 PASS |
| `samples/Defines`(G5) | 既定ビルド成功、`-p:EnableCustom=false` で植込みエラーが正しく発火(`DefineConstants` → `-define:` 配線が Linux で機能。WP-N1 の D2 増分キーにより define 変更が再コンパイルを正しく誘発) |

## 5. 発見事項

### 5.1 ビルドのチェックアウトパス依存(最重要。WP-N6 への申し送り)

Linux 産と Windows 産の stage2 をバイト比較したところ `ncc.exe` のみ一致し、
3 アセンブリはサイズ不一致だった。切り分けの結果:

- 同一コミット・同一スクリプトでも、**メインツリー / 別パスの worktree / WSL の 3 ビルドが
  3 様のサイズ**になり、その大小が**チェックアウトパスの文字列長の順序と一致**した。
- 実体は assert 系メッセージの文字列リテラルへの**ソース絶対パス埋め込み**
  (実測: Windows 産 `Nemerle.dll` に `F:\dev\nemerle_projects` の UTF-16 出現 54 箇所、
  Linux 産に `/home/wsl/wpn5/nemerle` 106 箇所。例:
  `F:\dev\...\lib\LightList.n` + `_data != null`)。
- パス埋め込みの無い `ncc.exe` は 3 ビルドで完全バイト一致。各環境**内**の 2 回独立ビルドは
  完全一致(WP-N1 の決定性は無傷)。

帰結: **WP-N1 の決定的ビルドは「同一チェックアウトパスなら同一バイト」**であり、
ディレクトリ・OS を跨いだバイト比較は原理的に成立しない。WP-N6 の CI 設計は
(a) runner 上の固定パスで checkout し、(b) バイト一致検証は「同一環境での 2 回ビルドの
自己一致」として行う。Windows 産成果物との突き合わせは版・テスト結果の照合に留める。
恒久対処(assert メッセージのパスを repo 相対に正規化するコンパイラー改修)はバックログ候補。

また、封緘済み 1.2.627-preview.1 の Stage2 と、同一コミットから worktree で再ビルドした
Stage2 も(パス差により)バイト不一致になる。「リリースバイナリの第三者再現」を将来
主張する場合は、この制約(同一パスでの再現に限る)を明記する必要がある。

### 5.2 raw LSP suite は samples の事前 restore に依存(テスト基盤の移植性)

クリーンな checkout で raw LSP / bundled server suite を実行すると、
`DefineConstants IDE/build parity` シナリオが `samples/Defines` の
`project.assets.json` 不在(NETSDK1004)で失敗する。server の MSBuild query
(`-getItem`)は restore を行わないため。Windows ツリーでは過去ビルドの `obj/` が
残っていたため潜在化していた。対処: 手順として
`dotnet-port/samples` 配下の `.nproj` を事前に `dotnet restore` する(§6 の VM 手順・
README に明記)。suite 側での自動 restore はテストの実行時間と副作用を増やすため見送り。

### 5.3 A2 版一致チェックの「封緘コミット直後」状態

封緘コミットは定義上 `git describe` を 1 進めるため、封緘直後の HEAD では既存
Stage1/Stage2 が常に「1 世代古い」と判定される。これは WP-N1 の意図された動作
(37-log §追記に既出)だが、「リリース直後に検証系 WP を行う場合は世代コミットに固定した
checkout を使う」という運用(§1)を本ログで前例化した。

## 6. Linux 実 VM 検証(clean-machine 相当、全 PASS)

環境: 専用 VM `nemerle-ubuntu`、**Ubuntu 26.04 LTS**(x86_64、kernel 7.0)、2 vCPU、
RAM 1.5 GiB → Extension Host テスト前に 7.3 GiB へ増設、ディスク 35 GB。
SSH(鍵認証)でエージェントが全工程を自走した。オーナーの手作業は VM 作成・
SSH 有効化・鍵登録・RAM 増設のみ。

**セットアップ実測**(clean-machine 手順として記録):

- 事前導入済みだったもの: git、.NET SDK **10.0.110**(Ubuntu 26.04 は
  `dotnet-sdk-10.0` をディストリのアーカイブで提供 — Linux ユーザーは
  `sudo apt-get install -y dotnet-sdk-10.0` で足りる)。
- 本検証で導入したもの: Node 22.23.1(nodesource setup_22.x)、
  PowerShell 7.6.3(GitHub release の tar.gz → `/opt/microsoft/powershell/7`、
  ディストリ非依存の方法)、`xvfb` + Electron 系ライブラリ
  (`libnss3 libatk-bridge2.0-0t64 libgtk-3-0t64 libasound2t64 libgbm1 libxkbfile1 libsecret-1-0`)。
  VS Code 本体の手動インストールは**不要**(`@vscode/test-electron` が自動取得)。
  デスクトップ環境も不要(xvfb のみ)。
- repo は git bundle から `~/nemerle` にクローンし世代コミット `5245d5345` に固定
  (`git describe` = `v1.2-627-g5245d5345`)、本 WP のスクリプト修正を patch 適用、
  Windows でビルドした Stage1 と封緘リリースセットを配置。

**phase B(CLI 検証)**: §4 の WSL 結果をすべて実 VM で追認 — stage1 `dotnet exec`
hello 成功 / stage2 生成成功 / **2 回独立ビルド 4 ファイル完全バイト一致** /
Nemerle.Linq / `pack-tool.ps1` レイアウト / `pack-server.ps1` / raw LSP 29 PASS /
封緘 VSIX bundled server 29 PASS / npm unit 22 PASS / Defines(G5)期待どおり。
**F5**: `pack-tool.ps1 -Pack`(出力先は封緘セットを守るため `dist/release-vm` に分離)+
`pack-release.ps1 -AllowDirty`(patch 適用による dirty のため)が Linux の pwsh で成功し、
`release-info.json` の生成まで確認。

**phase C(封緘セットの clean 消費、repo checkout 無し)**: 封緘 nupkg を
`~/nemerle-packages` に置き、`dotnet nuget add source` → `dotnet new install
Nemerle.Templates.Unofficial::1.2.627-preview.1` → `dotnet new nemerle-console` →
`dotnet build` → `dotnet run` = **"Hello from Nemerle on .NET 10!"**。
`nemerle-classlib` もビルド成功。packaging/README.md §1〜2 の手順が Linux で
そのまま成立することを実測。

**phase D(xvfb 下の実 VS Code、受け入れ基準 1)**: RAM 増設(7.3 GiB)後に実施。

| スイート | 結果 |
|---|---|
| `test:vsix`(封緘 VSIX を隔離 extensions-dir にインストール → bundled server 起動 → 診断 → 未保存修正で診断クリア) | **1 passing**(G3 修正の `/proc/<pid>/cmdline` 経路が本番で機能) |
| `test:integration`(本体 + Restricted Mode) | **4 + 1 passing** |
| `test:sdk`(Sdk package 経由プロジェクト) | **1 passing** |

Linux 固有の新規問題は**検出されなかった**(preview.2 で修正済みの hover リビルド
ループのような事象の再発なし)。

## 7. README 更新(実施済み)

- `packaging/README.md`: Requirements の「Both are verified」を実測内容つきの記述へ
  更新(Windows 11 + Ubuntu clean VM)。`NuGet.config` の Linux パス注意(`~` 非展開)、
  `dotnet nuget add source ~/nemerle-packages`(Linux 行)、`dotnet new install` の
  Linux 例を追記。メンテナー節のコマンドを `/` 区切りへ統一し「同コマンドが Linux の
  pwsh で動く(Stage1 ビルドのみ Windows 要)」を明記。
- `vscode-nemerle/README.md`: Requirements を「Windows 11 or Linux(両実測、
  本ログ参照)」へ更新。install 例の古い版リテラル `0.8.2` を `<version>` 表記へ修正
  (WP-N1 の「配布物内文書に版リテラルを書かない」方針への追従)。
- raw LSP suite 実行前の samples `dotnet restore` 前提は本ログ §5.2 と §9 の
  再現コマンドに記録。

## 8. 既知の制約(本 WP で確定・文書化するもの)

- ビルドのチェックアウトパス依存(§5.1)— OS/ディレクトリ跨ぎのバイト比較は不可。
- `run-testsuite-core.ps1` のハーネス(`Nemerle.Compiler.Test.exe`)は CLR4 実行ファイルで
  あり **Linux では実行不可**。WP-N5 の受け入れ基準(raw LSP / bundled server / npm test
  相当)の対象外だが、WP-N6 で「CI の testsuite 実行」を掲げる場合はハーネスの core 移植
  または別経路が必要(申し送り)。
- `ncc.cmd` は Windows 専用の利便ラッパー(仕様)。Linux は `dotnet <layout>/ncc.dll` を
  直接使う(packaging/README.md の手順どおり)。
- 既存 `.github/workflows/build.yml` は CLR4/VS2010 世代のレガシー CI であり、
  dotnet-port は対象外(WP-N6 の材料)。

## 9. 再現コマンド

```powershell
# --- Windows 回帰 ---
cd dotnet-port\vscode-nemerle
npm run lint; npm run test:unit; npm run test:integration; npm run test:vsix
pwsh ..\..\dotnet-port\run-testsuite-core.ps1          # 614/636(既知 22 失敗)

# --- 世代固定 worktree での修正スクリプト検証 ---
git worktree add <dir> 5245d5345
# (修正済み ps1 をコピーし、Stage1 を bin/Release/net-4.0/Stage1 へ配置)
pwsh dotnet-port/build-stage2-core.ps1
pwsh dotnet-port/pack-tool.ps1
```

```bash
# --- Linux(WSL / VM 共通、ext4 上のクローンを 5245d5345 に固定)---
dotnet exec bin/Release/net-4.0/Stage1/ncc.exe -out:hello.exe hello.n   # stage1 直接実行
pwsh dotnet-port/build-stage2-core.ps1        # stage1 -> stage2(Linux 完結)
pwsh dotnet-port/build-libs-core.ps1
pwsh dotnet-port/pack-tool.ps1
pwsh dotnet-port/vscode-nemerle/pack-server.ps1
for p in $(find dotnet-port/samples -name '*.nproj'); do dotnet restore "$p"; done  # §5.2
dotnet run --project dotnet-port/LspServer.IntegrationTest -c Release -- \
  dotnet-port/vscode-nemerle/server/Nemerle.LanguageServer.dll            # 29 PASS
pwsh dotnet-port/vscode-nemerle/test-bundled-server.ps1 -VsixPath <sealed>.vsix -NoBuild
cd dotnet-port/vscode-nemerle && npm ci && npm run test:unit              # 22 PASS
dotnet build dotnet-port/samples/Defines/Defines.nproj                    # OK
dotnet build dotnet-port/samples/Defines/Defines.nproj -p:EnableCustom=false  # 植込みエラー
```
