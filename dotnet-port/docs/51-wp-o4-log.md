# 51. WP-O4 ログ — 配布の器の判断(go/no-go)

実施日: 2026-07-23

ブランチ: `wip/dotnet-port`

## 結論

`47-wp-o-plan.md` の WP-O4 を完了した。配布の器を PO 判断で確定した。

- **nuget.org: no-go**。GitHub Release を配布器として維持する。
- **VS Code Marketplace: no-go**。VSIX も GitHub Release 配布を維持する。
- **公開時の恒久的パッケージ命名は確定不要**(公開しないため)。runtime package は
  単一 `Nemerle.Runtime.Unofficial`(WP-O3)を維持する。
- どちらの器も一方通行のコミット(nuget.org は package ID/版の恒久性、Marketplace は
  publisher identity の継続)を避けられ、位置づけ(保存中心・少し試せる間口・本格採用は非狙い)に
  整合する。
- **本判断は恒久ではない**。状況が変われば別途判断する。PO 指示によりバックログには積まない。
- 公開ワークフロー(release workflow)・packaging・msbuild・共有ソースへの変更は無い =
  Stage リビルド不要。
- WP-O4 の決定(GitHub Release 維持)を実際に行使し、WP-O3 の完全セット
  (`Nemerle.Runtime.Unofficial` を初めて含む)を `release/1.2.635-preview.2` として発行した
  (commit `019a749de`、preview.1 は残置)。方法の結論は下記「GitHub Release の運用」。

## 環境

- Windows 11 / PowerShell
- コード・スクリプト・パッケージ・ワークフローの変更なし(本 WP は判断と文書のみ)。
- npm / NuGet の依存 package の追加・更新は無い。

## 判断材料

### 命名の恒久性は nuget.org を開ける場合のみ発生する

nuget.org の `(パッケージ ID, バージョン)` は不変で、中身を差し替えた再発行はできない。`unlist` は
一覧から隠すだけ(直接 URL / 復元キャッシュからは取得され続ける)で、削除は原則不可。特に SDK の
ID(`Nemerle.Sdk.Unofficial`)は全ユーザーの `.nproj` の `<Project Sdk="…">` と `global.json` に
焼き込まれるため、改名コストが最大になる(出所は恒久的に真だが成熟度はいずれ偽になるため ID には
出所のみ載せ成熟度は prerelease label が担う、という WP-M6 の決定と同根)。GitHub Release のみに
留める限り ID は可逆で(利用者は release set フォルダを差し替えるだけ)、この恒久性問題は発生しない。

### 単一 `Nemerle.Runtime.Unofficial` の妥当性

- 3 dll(`Nemerle.dll` 0.36 MB / `Nemerle.Macros.dll` 0.62 MB / `Nemerle.Compiler.dll` 1.82 MB)は
  1 つのランタイム閉包である。マクロを自分の呼び出し箇所で使うプログラムは実行時に Macros/Compiler も
  束縛しうる(`msbuild/Nemerle.Core.targets:261-262`)ため分けられない。3 つ束ねるのは正しい粒度。
- `ExcludeAssets="compile"`(`msbuild/sdk/Sdk.props:86-88`)が「二重 `-ref:`」と「マクロ支援型の
  compile scope 混入」という唯一の破壊機序を封じている(単一/分割に依らず対処済み)。
- runtime ID は利用者に不可視(手で指定するのは `Nemerle.Sdk.Unofficial` のみ)。単一か 3 分割かは
  消費体験に差を生まない(どちらも同じ 3 dll が出力に入る)。命名選択は主に nuget.org カタログ衛生の
  問題であり、no-go では争点化しない。
- 単一バンドルの唯一の実コストは出力フットプリント(マクロ非使用の素のアプリでも `bin`/`deps.json` に
  実行不要の ~2.4 MB、特に大きい `Compiler.dll` が載る)。これは閉包を運ぶことに内在し 3 分割にしても
  同じ 3 つが出力に入るため分割で解消しない。correctness の問題ではない。

### 既存 net4x 資産との関係

既存の nuget.org 公開物 `Nemerle.Unofficial`(3,219 DL)/ `Nemerle.Compiler.Unofficial` /
`Nemerle.Macros.Unofficial` / `Nemerle.Compiler.Utils.Unofficial`(いずれも 1.2.547、net4x)へ寄せる
場合:

- **net10 のみで同一 ID に出す**と net4x 消費者が `NU1202` でビルド破壊(patch bump の顔をした破壊的
  変更、`35-devenv2-wp-m6-log.md:69-71` に記録済み)。
- **multi-TFM(net40 + net10.0)で同梱**すれば非破壊で系譜を継げるが、毎リリース net4x
  (Windows/CLR4 ブートストラップ依存)ビルドの生産・検証コストが位置づけに不釣り合い。

nuget.org no-go により、この継続性/破壊のトレードオフは発生しない。既存 4 package は 1.2.547 のまま
無傷で残る。

### Marketplace の便益は nuget.org no-go により部分的

- 拡張は SDK(`Nemerle.Sdk.Unofficial`)を自動インストールしない。project-aware 診断も `dotnet build`
  も、GitHub Release の nupkg 一式をローカルフィード登録することが前提(サーバーの MSBuild query が
  `.nproj` の `Sdk` を NuGet で解決できないと loose-file モードに落ちる、`packaging/README.md` §1)。
- LSP サーバーと解析用 Nemerle アセンブリは VSIX に同梱(`server/`、`server/bundle-info.json`)。
  .NET 10 ランタイムは非同梱で起動前に `dotnet --list-runtimes` を検査する(`DISTRIBUTION.md` WP-L4)。
- Marketplace が消せるのは VSIX の手動 install 摩擦のみ。ローカルフィード SDK セットアップの摩擦は
  nuget.org no-go である限り残る。したがって Marketplace の「摩擦なく試せる」便益は部分的で、
  publisher identity の一方通行コミットに見合わない。

## GitHub Release の運用(更新セットの発行)

GitHub Release を配布器として維持する決定のもと、WP-O3 の成果(`Nemerle.Runtime.Unofficial` を
初めて含む完全セット)の発行方法を確定した。

**リリースは provenance 封印された「セット」であり、個別ファイルではない。** runtime パッケージ 1 個
だけを配る単位は存在せず、更新した runtime を出す = セット全体を出し直す。

- **正解 = 新規 `1.2.635-preview.2` を release workflow で発行**。base `1.2.635` は `version.txt`
  ピンで据え置き、サフィックスのみ `preview.1 → preview.2`(WP-N1 規約: 同一 base で中身が変わったら
  `preview.<N+1>`)。CI 緑の commit(`019a749de`)に lightweight・`v` 非開始タグを打ち、
  `build-smoke`(dry-run)→ `build-smoke-release` で発行(リポジトリファイルの変更なし)。
- **不可 (a) 既存リリースに runtime nupkg だけ追加**: 旧セットの SDK は WP-O3 前で runtime を
  参照せず無意味。かつ別コミット産物の混入で `release-info.json` の provenance を破壊
  (`pack-release.ps1` が混在コミット由来のセットの封印を拒否するのはこのため)。
- **不可 (b) 同一版でアセット差し替え**: 版番号の不変性違反(`DISTRIBUTION.md` WP-N1)。NuGet は
  `(id, version)` を内容ごとキャッシュするため、消費側で旧ビットが静かに勝つ。
- **preview.1 は残置**(両者 prerelease で "Latest" にならない)。一度外へ出た番号 `preview.1` は
  削除しても再利用しない(順序逆行・通知済みのため)。

発行結果: `release/1.2.635-preview.2`(prerelease、commit `019a749de`)。アセット = 4 nupkg
(`Nemerle.Sdk` / `Nemerle.Runtime` / `Nemerle.Templates` / `Nemerle.Linq`、いずれも
`1.2.635-preview.2`)+ `vscode-nemerle-0.9.0.vsix` + `README.md` + `release-info.json`。

## 変更ファイル

- 新規: `dotnet-port/docs/51-wp-o4-log.md`(本書)。
- 変更: `dotnet-port/docs/47-wp-o-plan.md`(WP-O4 を完了へ、§8 論点 1/2 の決着を反映)。
- 変更: `dotnet-port/docs/00-PLAN.md`(WP 一覧・作業ログに WP-O4 完了を追記)。
- 共有ソース(`ncc/`・`lib/`・`macros/`)・`VsIntegration/`・release workflow・packaging・msbuild は
  無変更。

## 検証

- コード・スクリプト・パッケージ・ワークフローに変更なし = ビルド/回帰ゲート/Stage リビルド不要
  (`47-wp-o-plan.md` §7 の注、`00-PLAN.md` の判断基準)。
- 判断材料の一次事実をリポジトリ実物で照合:
  - runtime package 構成と `ExcludeAssets="compile"`: `msbuild/sdk/Sdk.props:86-88` / `50-wp-o3-log.md`。
  - 3 dll が閉包で実行時に必要: `msbuild/Nemerle.Core.targets:261-262`。
  - dll サイズ: `dist/ncc`(`Nemerle.dll` 0.36 / `Nemerle.Macros.dll` 0.62 / `Nemerle.Compiler.dll` 1.82 MB)。
  - 既存 net4x package と `NU1202` ハザード: `35-devenv2-wp-m6-log.md:69-71`。
  - VSIX が LSP サーバーを同梱し .NET 10 は非同梱: `DISTRIBUTION.md`(WP-L4 更新節)。
  - ローカルフィード前提の SDK 消費: `packaging/README.md` §1。
- docs 相互参照の整合: `47-wp-o-plan.md` / `00-PLAN.md` の WP-O4 記述が本判断と一致。
- release workflow の発行: `build-smoke`(dry-run、副作用なし)と `build-smoke-release` がいずれも
  green。公開 asset の `release-info.json` の `commit` = タグ commit(`019a749de`)で provenance 整合。
  収録 4 パッケージはすべて `1.2.635-preview.2`、runtime パッケージの `lib/net10.0/` に
  `Nemerle.dll` / `Nemerle.Macros.dll` / `Nemerle.Compiler.dll` を確認。

## 既知の制約 / 残課題

1. 本判断は恒久ではない。位置づけや状況が変われば nuget.org / Marketplace を再判断できる(PO 指示に
   よりバックログには積まない)。
2. 公開する場合に確定が必要な下流の具体値(package ID・publisher・版方針)は、その時点で先行 WP の
   成果物として置く(`47-wp-o-plan.md` §5 WP-O4、[[feedback-plan-dependency-ordering]])。
3. WP-O5(任意 showcase)は未着手のまま。
