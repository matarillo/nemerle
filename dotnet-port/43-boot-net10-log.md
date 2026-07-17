# 43. boot-net10: clone からの配布物ビルド(Windows 不要)実装ログ

実施日: 2026-07-17

ブランチ: `wip/dotnet-port`(スクリプト)+ **orphan ブランチ `boot-net10`**(seed)

開始 commit: `2c3a8dd9b`(WP-N5 クローズ)。WP-N5 のフォローアップとして PO 依頼で実施
(WP 番号は付与せず、36 計画の枠外。WP-N6 の CI 検討の先行部品を兼ねる)。

## 目的と結論

**GitHub リポジトリを clone した人(Linux / Windows いずれでも)が、.NET Framework 無しで
配布物一式(`dist/release`)を作成できるようにする。** boot-4.0 と同じ「seed をリポジトリに
置く」発想だが、置き場所を **orphan ブランチ** にしたのが設計の核心。

結論: 実装・検証完了。VM(Ubuntu 26.04)上で、bundle からの新規 clone →
`pwsh dotnet-port/build-from-boot.ps1` の 1 コマンドで release set
(Sdk/Templates/Linq nupkg + VSIX + release-info.json、版 1.2.630-preview.1)が生成され、
生成物だけから `dotnet new nemerle-console` → build → run が成立した。
in-place / pinned-worktree の両経路を検証。

## 1. なぜ orphan ブランチか(設計判断の記録)

アセンブリ版は `GeneratedAssemblyVersion` マクロが**コンパイル時の `git describe --tags --long`**
から焼き込む。ここから 2 つの制約が生じる:

1. **CoreCLR は世代混在ロードを拒否**(A2 チェックの本体、14 F4 / 30 log)。CLR4 と違い、
   古い seed で新しい HEAD をビルドする「世代ジャンプ」ができない。
   → seed は「自分が作られた世代のソース」しかビルドできない。
2. **main へのコミットは何であれ describe を進める**。seed を main にチェックインすると、
   そのコミット自体が世代を進め、seed は置かれた瞬間から常に 1 世代古い
   (「+1 パラドックス」)。さらに利用者が世代コミットへ `git checkout` すると
   seed もスクリプトも巻き戻る。

orphan ブランチ(`git checkout --orphan`、main と共通祖先を持たない独立した歴史)は
両方を同時に解決する: **boot-net10 へのコミットは main の describe を進めない**ので、
「コミット C でビルドした seed を発行 → main の HEAD は C のまま = seed 世代と一致」が成立。
取り出しは checkout ではなく `git archive`(ワーキングツリー非破壊)で行うため、
巻き戻り問題も構造的に起きない。

検討して退けた代替案: (a) main へ in-tree チェックイン(+1 パラドックス・バイナリ churn・
巻き戻り)、(b) GitHub release asset(ネットワーク必須、in-tree ハッシュを置くと +1 再発。
ただし人間向けミラーとしての併設は将来可)。
将来 `GeneratedAssemblyVersion` に版ピン留め(describe ではなくチェックイン済みファイルを
優先)を入れれば「任意の HEAD でビルド」も可能になるが、共有ソース改修 + CLR4 ゲートが
必要なため本件では見送り(WP-N6 検討と抱き合わせのバックログ)。

## 2. 構成

**orphan ブランチ `boot-net10`**(root commit のみ、main と非連結):

```
boot-info.json           マニフェスト(schema 1)
ncc.exe                  stage1 = net-4.0 フレーバー、dotnet exec で CoreCLR 実行
ncc.runtimeconfig.json
Nemerle.dll / Nemerle.Compiler.dll / Nemerle.Macros.dll
Nemerle.CoreEmit.dll     stage1 の CoreCLR emit に必須(refresh-stage1-core.ps1 が配置するもの)
```

boot-info.json は世代(commit / describe / AssemblyVersion)・builtOnUtc・各ファイルの
SHA256 を記録。ハッシュは**完全性**検査用(バイナリと同じブランチに載るため真正性は担保しない)。
ncc32/64.exe・*.xml・policy.*.config は実行に不要のため seed から除外。

**`dotnet-port/publish-boot.ps1`**(発行側、Windows。メンテナー用):
clean tree ゲート → 6 ファイル存在確認 → A2 鮮度チェック(hard fail)→ スモーク
(hello.n を dotnet exec でコンパイル・実行)→ boot-info.json 生成 → 使い捨て worktree 経由で
orphan ブランチへ 1 コミット。**push はしない**(push はオーナー操作)。

**`dotnet-port/build-from-boot.ps1`**(消費側、Windows / Linux):
ref 解決(`boot-net10` → `origin/boot-net10` → fetch 案内付きエラー。clone 直後は
リモート追跡 ref しか無いため後者が通常経路)→ `git archive --format=zip` で
`bin/boot-net10/` へ展開 + SHA256 全照合 → 経路判定:

- **in-place**: HEAD の describe == seed 世代(refresh 直後・世代コミットの clone)。
  dirty tree は拒否(pack-release の clean 要件)。
- **pinned worktree**: 世代不一致(通常の tip clone)。リポジトリルートの
  `.boot-build-tree/`(固定パス — 生成物がチェックアウトパスを埋め込むため、再実行間の
  バイト自己一致にはパス固定が必要。WP-N5 §5.1)に seed の世代コミットを checkout し、
  **worktree 側のスクリプト**でビルド(世代 G のソース・rsp と整合する版を使うため)。
  **`bin/` 配下に置いてはならない**(§4 の発見: 当初 `bin/boot-build-tree` にしたところ、
  VS Code 拡張のプロジェクト探索が bin/obj/dist/node_modules セグメントを含むパスを
  除外するため、worktree 内で走る `npm run package` の unit テスト
  (projectDiscovery fixture)が構造的に失敗した。`.gitignore` に `/.boot-build-tree/` を追加)。

その後は既存チェーンをそのまま実行: `build-stage2-core.ps1 -Compiler <seed>/ncc.exe` →
`build-libs-core.ps1` → `pack-tool.ps1 -Pack` → `pack-server.ps1` → `npm ci` +
`npm run package` → `pack-release.ps1`。**ビルド対象は常に seed と同世代のソース**なので、
A2 チェックも provenance(ncc-info / bundle-info / release-info の commit 照合)も
既存のまま自然に通る(seed 専用の特例なし)。出力: in-place は `dotnet-port/dist/release`、
worktree 時は `dotnet-port/dist/release-from-boot` へ回収して worktree を掃除
(`-KeepWorktree` で保持可)。

**README**: `packaging/README.md` の For maintainers 節に
「Building the release set from a clone」「Refreshing the boot seed」を追記。

## 3. 発行の実測(Windows)

1. 実装コミット `8bdbacbb0`(= 世代 `v1.2-630`)。
2. その HEAD で Stage1 フルリビルド(CLR4 msbuild、0 エラー)+ `refresh-stage1-core.ps1`。
   Windows 側 bin の整合維持のため stage2 / libs も 630 で再構築。
3. `pwsh dotnet-port/publish-boot.ps1` → A2 OK(1.2.0.630)、スモーク OK、
   orphan root-commit **`d8d5120`**(`boot-net10 seed 1.2.0.630 from 8bdbacbb0`)。
   発行後も main の describe は `v1.2-630-g8bdbacbb0` のまま(orphan の分離を実証)。

## 4. 消費の実測(Linux VM、bundle 経由の新規 clone)

WP-N5 と同じ VM(Ubuntu 26.04 / 2 vCPU / 7.3 GiB / dotnet SDK 10.0.110 / pwsh 7.6.3 /
Node 22)。公開前のため GitHub には push せず、`git bundle`(wip/dotnet-port +
boot-net10 + tags)からの clone で実リモートと同じ ref 形状を再現した
(seed は `origin/boot-net10` としてのみ見える — まさに clone 直後の形)。

| 検証 | 結果 |
|---|---|
| 新規 clone(tip = 8bdbacbb0 = seed 世代)→ `build-from-boot.ps1` | **PASS(in-place 経路)**: `Using seed ref: origin/boot-net10` → SHA256 照合 → in-place 判定 → フルチェーン完走。**release set 生成**: Sdk / Templates / Linq 1.2.630-preview.1 + vscode-nemerle-0.9.0.vsix + release-info.json(commit 8bdbacbb0) |
| 生成物の消費(repo 外で `dotnet new install` → `nemerle-console` → build → run) | **PASS**(生成した local feed のみで成立) |
| ダミーコミットで世代をずらして再実行 | **PASS(pinned worktree 経路)**: `.boot-build-tree` に世代コミットを checkout してフルチェーン完走、`dist/release-from-boot` へ回収 |

検証中に修正した問題 2 件(いずれも検証で発見し、成果物側へ反映済み):

1. **worktree の `bin/` 配下配置は不可**(§2 に記載)。初回の worktree 経路は
   `npm run package` 内の projectDiscovery unit テストが除外ルール(`bin` セグメント)に
   かかって失敗した。`.boot-build-tree` へ移動して解消。ユーザーの clone パス自体に
   `bin`/`obj`/`dist` 等のセグメントが含まれる場合も同種の失敗が起きる(既存特性)。
2. `dotnet new install --add-source` は**相対パスを拒否**する("source is not valid")。
   これは検証スクリプト側の誤用で、成果物・README(絶対パス / `~` 前提)は正しい。
   NuGet.config の相対 value は従来どおり有効(config 位置基準で解決)。

なお `build-from-boot.ps1` の Windows 上での end-to-end 実行は個別には実測していない
(チェーンの各段は Windows での常用経路そのもので、スクリプト固有部は OS 非依存の
git/パス操作のみ。publish-boot.ps1 は Windows で実測済み)。

(https clone での煙テストは、オーナーが `wip/dotnet-port` と `boot-net10` を push した後に
別途 1 回実施する — bundle では「GitHub に実際に上がっているか」だけは検証できないため。)

## 5. 運用(メンテナーの refresh 儀式)

リリース封緘や compiler ソース変更後の節目で:

```powershell
# 1. seed にしたいコミットで Stage1 をフルリビルド(従来の A2 復旧手順そのもの)
Remove-Item -Recurse -Force bin/Release/net-4.0/Stage1
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
pwsh dotnet-port/refresh-stage1-core.ps1
# 2. 発行(ローカルコミットのみ)→ レビュー → push
pwsh dotnet-port/publish-boot.ps1
git push origin boot-net10
```

seed の鮮度は publish 時に A2 が強制。**push を忘れても、build-from-boot は古い seed の
世代で正しくビルドする**(worktree 経路)ので、壊れはせず「新しくならない」だけ。

## 6. 既知の制約

- **自作 release set は公式 release assets とバイト一致しない**(チェックアウトパス埋め込み、
  WP-N5 §5.1)。構成・版・動作は同等。スクリプトヘッダーと README に明記済み。
- seed が古い場合、tip の clone は常に worktree 経路(= seed 世代の再現)になる。
  「HEAD そのもの」をビルドしたい場合は Windows での Stage1 リビルドが引き続き必要。
  根治は版ピン留め(§1 末尾、バックログ)。
- `--single-branch` clone / ZIP は boot-net10 を持たない → スクリプトが
  `git fetch origin boot-net10:boot-net10` を案内して停止。
- Linux では testsuite(CLR4 ハーネス)は依然実行不可(WP-N5 §8 のまま)。
  from-boot の検証範囲は publish 時スモーク + pack-release の封緘検査 + 生成物の消費テスト。

## 7. 変更ファイル

- 新規: `dotnet-port/publish-boot.ps1`、`dotnet-port/build-from-boot.ps1`
- 更新: `dotnet-port/packaging/README.md`(For maintainers 節)、`.gitignore`
  (`/.boot-build-tree/`)
- 新規ブランチ: `boot-net10`(orphan、seed 1.2.0.630 from 8bdbacbb0)
- コンパイラー・engine・既存スクリプトは無改修。

実装: Sonnet サブエージェント(スクリプト作成)+ Fable(設計・レビュー・検証)。
