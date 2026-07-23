# 45. boot-4.0 更新(世代 538→636)実装ログ

対象: 36-prerelease-quality-plan.md の枠外(WP 番号なし、PO 依頼による単発の保守作業)。
WP-N7(`44-prerelease-wp-n7-log.md`)とは**独立**。WP-N7 の go/no-go 判断を待たずに実施。

## 0. 結論(2026-07-20 実施・完了)

**boot-4.0 を世代 538(`1.2.0.538`)から世代 636(`1.2.0.636`、コミット `78c7024b0` 相当)へ
更新した。** 既存の Stage1/Stage2/Stage3 セルフホスト機構(`NemerleAll.nproj`)と ildasm ベースの
IL fixpoint 検証で候補バイナリを検証し、採用した。これにより WP-N4 発見 1(41-log §11-1、
release タグ祖先下での boot-4.0→Stage1 フルリビルドが「release タグの一時退避」を要求していた
問題)が根治し、DISTRIBUTION.md の回避運用を削除した。**再リリースは不要**(理由は §4)。

## 1. 動機

41-log(WP-N4)は「AssemblyVersion を刻むのはビルドを実行しているコンパイラーの
`Nemerle.Macros.dll` であり、ソースの macro ではない」ことを発見した。boot-4.0 は Stage1 を
生成するコンパイラーそのものであり、538 世代のまま凍結されていたため、`--match "v[0-9]*"`
タグ契約(WP-N4 で macro ソースに追加済み)を**知らない**。release タグが祖先に付いた
コミットで boot-4.0 → Stage1 のフルリビルドを行うと、旧レシピが release タグを版として誤読し
`invalid format of version attribute` で失敗する。回避策(release タグの一時削除→リビルド→
`git fetch --tags` で復元)が DISTRIBUTION.md に運用として残っていた。

44-log(WP-N7 評価)はこの問題の根治を「describe 依存を断つ版ピン留め」という大きな設計変更に
求めていたが、これは過剰である可能性が高いと判断した: boot-4.0 自身を現行世代へ更新すれば、
boot-4.0 の macro が `--match` 契約を知ることになり、この特定のバグは解消する。
`git log --oneline -- boot-4.0` を見ると `Update boot.` / `New boot.` という更新コミットが
アップストリーム時代(直近 2017-03-10 `851a186de`)に繰り返し行われており、boot を
定期的に作り直すこと自体はこのリポジトリの本来の運用だった。`NemerleAll.nproj` にも
Stage1〜Stage4 の多段セルフホストと `Validate`(IL diff)ターゲットが既に用意されている。
「凍結」はこの .NET 移植プロジェクトが WP-N7 評価時に置いた前提であり、どこにも
批判的に検討された記録がなかった。

## 2. 手順と検証

### 2.1 候補バイナリの生成(2 世代セルフホスト)

既存の `NemerleAll.nproj` の Stage1→Stage2→Stage3 依存チェーン(すべて net4.0/CLR4、
boot-4.0 が Stage1 を、Stage1 が Stage2 を、Stage2 が Stage3 を生成)をそのまま利用。

```powershell
git tag -d release/1.2.635-preview.1   # ローカルの release タグを一時削除(旧 boot-4.0 が拾うため)
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj `
    /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage3
git fetch --tags                        # push 済みなので復元
```

結果: 0 エラーで Stage1/Stage2/Stage3 とも完走。Nemerle.dll = `1.2.0.636`(3 世代とも同一。
HEAD が WP-N4 close-out コミット `78c7024b0` へ進んでいたため 636)。

### 2.2 Stage2 vs Stage3 の fixpoint 検証(ildasm IL diff、マスク付き)

`NemerleAll.nproj` の `Validate` ターゲット(ildasm + タイムスタンプ行マスク + diff)と
同じ手法を手動で実施(`MSBuild.Community.Tasks` への依存を避けるため PowerShell で再実装)。
対象 6 ファイル(Nemerle.dll / Nemerle.Compiler.dll / Nemerle.Macros.dll / ncc.exe / ncc32.exe /
ncc64.exe)。

**結果: 6/6 ファイルとも IL diff 0 行(fixpoint 到達)。** Stage2(候補 boot)が現行ソースを
自己再生成しても意味的に同一の出力を返すことを確認。これは CoreCLR 側で既に確立している
「stage3==stage4」fixpoint 検証(37-log)と同じ発想を CLR4/net4.0 側に適用したもの。

参考: Stage1(旧 boot 538 が生成)と Stage2(候補 boot が生成)の `Nemerle.Macros.dll` を
同様に diff すると、`ContainsMacroAttribute` に埋め込まれた内部 ID(gensym カウンター由来の
数値)のみが異なり、それ以外は一致していた。これは 16-determinism-diagnosis.md で既知の
「ブートストラップ世代間の gensym ID 消費差」(コンパイラー生成物の恒久的な性質、正誤とは
無関係)と整合する。Nemerle.dll(322048 バイト)・Nemerle.Compiler.dll(1777664 バイト)は
Stage1/2/3 全ステージでサイズが完全一致していた。

### 2.3 CLR4 ネイティブスモーク + PEVerify(候補 boot = Stage2 を実行系として)

37-log §7.4 と同じ hello.n / hello2.n を用いて、`dotnet exec` を挟まないネイティブ CLR4
実行で確認:

```
bin\Release\net-4.0\Stage2\ncc.exe -out:hello.exe hello.n   && .\hello.exe    # Hello from stage-test!, exit 0
bin\Release\net-4.0\Stage2\ncc.exe -out:hello2.exe hello2.n && .\hello2.exe   # 1 / 2, 4, 6, exit 0
```

PEVerify(NETFX 4.8.1 Tools)を Stage2 の 6 ファイル全てに実行し、全て exit 0(構造的破損なし)。

### 2.4 修正の実証(release タグ祖先下での Stage1 リビルド)

候補バイナリ(Stage2 の 6 ファイル)を作業ツリー上で `boot-4.0/` に上書きし、
`release/1.2.635-preview.1` タグを**削除せずに残したまま**、Stage1 をフルリビルド:

```
Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
msbuild NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
```

**結果: 0 エラーで完走、Nemerle.dll = `1.2.0.636`(正しい版、タグの誤読なし)。**
旧 boot-4.0(538)では同じ状況(release タグが祖先に存在)で `invalid format of version
attribute` により失敗していた(§1)。これで DISTRIBUTION.md の回避運用(release タグの
一時削除)が不要になったことを実証した。

## 3. 採用した検証の範囲についての判断

以下を実施した: Stage2==Stage3 の IL fixpoint(§2.2)、CLR4 ネイティブスモーク 2 本 +
PEVerify(§2.3)、修正の直接実証(§2.4)。

**以下は実施しなかった**: `NemerleAll.nproj` の `CompilerTests` ターゲット(レガシー
testsuite 全数、positive 469 + negative 167)。理由:

1. この作業は**共有ソースの変更を伴わない**(macros\ 等は WP-N4 で既に修正済みで無変更)。
   boot-4.0 の更新は「同じ現行ソースをもう 1 世代分セルフホストする」だけであり、
   コンパイラーの振る舞い自体を新規に検証する必要は薄い。
2. `CompilerTests` は Linq/Unsafe/WPF/C# パーサープラグインのビルドを前提とし、この
   .NET 移植期間中に一度も通した実績がない(testsuite の実績はすべて dotnet-port 側の
   `run-testsuite-core.ps1`、CoreCLR フレーバー)。範囲・所要時間ともに本作業の重みに対して
   過大と判断した。
3. Stage2==Stage3 の fixpoint(§2.2)は、自己ホスト系プロジェクトで広く使われる
   「自分自身を正しく再生成できるか」の強い間接証拠であり、WP-H(16-log)がまさにこの
   種の fixpoint 不一致からバグを発見した実績がある。fixpoint が綺麗に成立したことは
   低くないシグナル。

**残存リスク**として記録: 今回の 2 世代セルフホストが「意味的に不変(fixpoint)」であることは
確認したが、「機能的にバグが無い」ことを testsuite 全数で確認してはいない。次にこの boot-4.0
を実際のリリース seed 更新(`publish-boot.ps1`)に使う際は、通常どおり §5-2 相当の回帰ゲート
(dotnet-port 側 testsuite・stage2/3 core 比較)が走るため、そこで最終的にカバーされる。

## 4. 再リリースは不要と判断した根拠

- `publish-boot.ps1` / `build-from-boot.ps1` は orphan ブランチ `boot-net10` の seed のみを
  消費し、**boot-4.0 を一切参照しない**(43-boot-net10-log.md §2、grep で確認)。したがって
  既に発行済みのリリース(1.2.635-preview.1)の再現性は boot-4.0 の状態と無関係。
- 平常時(release タグが祖先に存在しない状態)では、旧レシピ(538)と新レシピ(636、
  `--match` 契約入り)は同一の describe 結果を返す(41-log §7.4 で既に実測済み)。つまり
  boot-4.0 の更新は、通常ビルドで生成される版文字列を一切変えない。
- 影響が及ぶのは「今後 macro 等の共有ソースを変更して boot-4.0 を再度フルリビルドする」
  場面と、「release タグが祖先に存在する状態で Stage1 をリビルドする」場面(= 本更新の
  動機そのもの)だけであり、どちらも配布済み成果物には波及しない。
- 次回 `publish-boot.ps1`(boot-net10 seed の前進)を実行する際は、その Stage1 リビルドが
  新しい boot-4.0 を経由することになる。これは「今回のための再リリース」ではなく、次回の
  通常手順として自然に反映される。

## 5. コミットした変更

- `boot-4.0/{Nemerle.dll,Nemerle.Compiler.dll,Nemerle.Macros.dll,ncc.exe,ncc32.exe,ncc64.exe}`:
  538→636 世代のバイナリへ置換(§2.1〜2.2 で検証した Stage2 の出力をそのまま採用)。
- `DISTRIBUTION.md`: 「boot-4.0 制約」節から回避運用を削除し、更新済みである旨と
  再リリース不要の理由を記載。
- `41-prerelease-wp-n4-log.md` §12-3: 解消済みである旨を追記(取り消し線 + 注記、履歴は残す)。
- `44-prerelease-wp-n7-log.md` §3-2: 「boot-4.0 は二度と再実行しない」という前提が過剰
  だったことを訂正。WP-N7 のゴール(継続的なリリース経路の Windows レス化)とは独立の
  課題であることを明記。

## 6. WP-N7 との関係(再確認)

本作業は WP-N4 発見 1(旧 macro が release タグを誤読するバグ)を根治したが、WP-N7 が
狙う「リリース経路(seed 前進を含む)を継続的に Windows/CLR4 レスにする」というアーキテクチャ
上のゴールの代替にはならない。macros\ など共有ソースに今後手を入れるたびに、また同様の
boot-4.0 更新(= Windows/CLR4 での Stage1〜Stage3 ビルド)が必要になり得るため、
「boot-4.0 を都度作り直せばよい」という運用に倒すことは WP-N7 が排除したい依存を形を変えて
残すことになる。WP-N7 は引き続き独立の評価対象とする。
