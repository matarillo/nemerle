# 44. WP-N7: 版ピン留めと boot-net10 orphan 廃止 設計・評価ログ

対象 WP: 36-prerelease-quality-plan.md §6 WP-N7。

本 log は WP-N7 の**設計根拠・評価の出発点・トレードオフ**を保持する。WP-N7 は
**評価(本 log の設計節)先行 → go/no-go** で扱い、go の場合のみ実装へ進む。
本節は評価の下敷きであり、go/no-go の判断と実装結果は後半へ追記する。

## 1. 問題意識: boot-net10 orphan は苦し紛れの一時手段

現在、net4 (.NET Framework) 無しで配布物をビルドするための stage1 seed は orphan ブランチ
`boot-net10` に置かれ、`build-from-boot.ps1` が消費する(log 43)。この構造は 2 つの厄介を
同時にこなすための苦肉の策であり、どちらの厄介も**「AssemblyVersion が describe 由来で
毎コミット進む」**ことに起因する:

1. **seed の置き場**: seed バイナリを main にチェックインすると、そのコミット自体が describe を
   +1 進め、seed は置かれた瞬間から常に 1 世代古い(+1 パラドックス)。→ describe に見えない
   orphan へ隔離。
2. **世代固定の代償 = pinned worktree**: CoreCLR は世代混在ロードを拒否する(A2 ハザードの
   本体)ため、seed は自分の世代のソースしかビルドできない。HEAD が進んでいると
   `build-from-boot.ps1` は `.boot-build-tree` に**seed の世代コミットを checkout してそちらを
   ビルド**する。得られるのは「seed 世代の再現」であって HEAD の成果物ではない。

つまり orphan + pinned worktree は「版ピン留めをやらなかったことの代償」。log 43 §1 末尾も
「`GeneratedAssemblyVersion` に版ピン留めを入れれば任意 HEAD でビルド可能になる」と出口を
予告している。

## 2. 採用する設計の骨子: AssemblyVersion 固定 + Informational でズレ検出

- **AssemblyVersion**(`major.minor.build.revision`)を describe 由来ではなく
  **チェックイン済みの固定値**(`version.txt` 等)から与える。これは CoreCLR のロード束ね
  (型同一性)に使われる版。固定にすると世代ジャンプ(古い seed で新しいソースをビルド)が
  CoreCLR でも成立する。
- **AssemblyInformationalVersion** に `major.minor.build.revision-<commit8>` のように
  コミットハッシュを載せる。これは**ロードに一切使われないラベル**なので、ハッシュを入れても
  束ねを壊さない。
- **世代ズレ検出は Informational のハッシュ照合へ移す**。version.txt を上げ忘れても
  ハッシュは毎コミット変わるため、seed と HEAD のズレを検出できる。

技術的裏付け(実測): A2 検査・pack はいずれも `AssemblyName.GetAssemblyName(...).Version`
(= AssemblyVersion)を読む(`assembly-version-check.ps1` / `pack-tool.ps1`)。CoreCLR のロード
束ねも `AssemblyName.Version` のみを見て InformationalVersion を無視する。したがって
「束ねには AssemblyVersion、検出には Informational」の役割分担はクリーンに成立する。

参考: CLR4 世界はこれに近いことを binding redirect で実現していた
(`lib/policy.1.2.Nemerle.config.template`: `oldVersion="1.2.0.0-${ver}" newVersion="${ver}"` =
1.2 系のどの版要求も現物へ束ねる)。版ピン留めは、CLR4 がやっていた寛容な束ねを CoreCLR 上で
明示的に再現するものと言える。

## 3. 得られるもの

1. **seed が任意コミットをビルド可能**になる → **boot-net10 orphan と pinned worktree を
   廃止**できる。seed 置き場は in-tree チェックイン等の素直な形へ移せる(+1 パラドックスが
   消えるため main への配置が初めて自然になる)。
2. **seed 前進(seed refresh)の CoreCLR 化**: 版固定で世代ジャンプが可能になれば、seed から
   次の seed を CoreCLR で前進させられる。→ リリース経路(seed 作成〜release set 発行)から
   **CLR4/Windows を外せる**。残る CLR4 依存は「最初の boot-4.0 → 最初の stage1」という
   凍結済みの創世のみ(boot-4.0 = `1.2.0.538`、二度と再実行しない)。
3. 結果として、CI が Windows runner 無しでリリース世代の前進まで完結できる道が開く
   (WP-N6 の CI アーキテクチャの前提が変わる)。

### リリース経路の工程分解(N7 の狙いの背景)

- 工程 X「seed を作る/世代を進める」= boot-4.0 → Stage1 → publish-boot。**現状 CLR4/Windows 必須**。
- 工程 Y「seed から release set を作って発行」= build-from-boot(seed 経路)→ pack → Release。
  **現状でも CoreCLR/Linux で可能**(log 43 で実証)。

現状、CI(Linux)は「seed が既にある世代」の工程 Y だけ担える。新しいコンパイラ世代を出すには
先に工程 X を Windows で行う必要がある。**N7 の核心は工程 X を CoreCLR 化して、この Windows
依存を外すこと**。

## 4. トレードオフ(受容可否を評価 log で確定させる)

1. **A2 の門番が格下げ**: 現状は世代ズレが「ロード時の FileLoadException」で強制検出される。
   版固定後は AssemblyVersion が一致してしまうため、ズレ検出は Informational ハッシュ照合
   (= 検査スクリプトの警告/停止)に依存する。**ランタイムによる強制 → 人+テストの規律**への
   移行を受容できるか。
2. **世代跨ぎ束ねの安全性**: version.txt を稀にしか上げないと、「同じ AssemblyVersion を名乗る
   中身の違うコンパイラ」が並ぶ。マクロ ABI が非互換に変わった版どうしを混ぜてもランタイムは
   止めない。「version.txt をいつ上げるか」(例: マクロ ABI が変わる区切り)の運用規約が
   成否の鍵。現状の describe 方式はこの判断を「毎コミット上げる」で機械化して肩代わりしていた。
3. **NuGet 罠が手動運用へ**: base(rev)が自動で動かなくなるため、内部テストの反復で
   base 据え置き再配布が常態化し、`preview.N` の手動 +1 規律の比重が増える(WP-N4 の preview.N
   運用がそのまま重くなる)。

## 5. 評価 log で確定させること(go/no-go の判断材料)

- 版ピン留め後の A2 検査を「AssemblyVersion 一致」→「Informational ハッシュ照合」へ再設計する
  具体案と、失う安全性(§4-1)の受容可否。
- boot-net10 orphan 廃止後の seed 置き場(in-tree / release asset / その他)の再設計。
- **工程 X(seed 前進)の CoreCLR 化が実際に成立するか**の検証(seed から次 seed を CoreCLR で
  作れるか)。
- NuGet 罠への手動 suffix 運用ルールと「version.txt をいつ上げるか」の運用規約。
- 影響範囲: `macros\GeneratedAssemblyVersion.n`(共有ソース)、`assembly-version-check.ps1`、
  `pack-tool.ps1` / `pack-release.ps1` の版取得、`boot-info.json` 世代照合、
  `publish-boot.ps1` / `build-from-boot.ps1`、DISTRIBUTION.md / packaging README。

## 6. WP-N4 との関係・前提

- 前提: **WP-N4 完了後**。タグ契約(`--match`)は describe を Informational 側に
  残すため N7 をやっても生き続ける(41 §5)。
- 共有ソース(`macros\`)改修のため §5-2 の CLR4 回帰ゲート + §5-5 の VsIntegration grep を適用。
- 順序上、N7 の go/no-go 結論が WP-N6 の seed 機構(orphan 継続か新機構か)を決めるため、
  N6 より先に判断する(36 §8)。

## 7. go/no-go 判断・実装結果

(評価完了時に追記)
