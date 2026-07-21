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
   **CLR4/Windows を外せる**。残る CLR4 依存は「boot-4.0 → 最初の stage1」の創世のみ。
   ~~(boot-4.0 = `1.2.0.538`、二度と再実行しない)~~ — **2026-07-20 訂正**: 「二度と
   再実行しない」は前提として過剰だったと判明。boot-4.0 自身も Stage1→Stage2→Stage3 の
   2 世代セルフホスト fixpoint 確認を経て随時更新してよいことを実証済み
   (`45-boot-4.0-refresh-log.md`。boot-4.0 = `1.2.0.636` へ更新)。これは WP-N4 発見 1
   (旧 macro が release タグを誤読するバグ)の根治には有効だが、本 WP-N7 が狙う
   「**継続的な**リリース経路から Windows/CLR4 依存を除く」というゴールの代替にはならない
   (macro など共有ソースに手を入れるたびに、また boot-4.0 refresh で Windows/CLR4 が
   必要になり得るため)。両者は独立した課題として扱う。
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

**結論（2026-07-21、PO 合意）: 部分 GO。**「case 1 = 版ピン留め + orphan/pinned-worktree
廃止 + 同一 version.txt スパン内の Linux/CoreCLR ビルド」を採用する。ただし**版バンプ
（新世代生成 = 工程 X）は Windows/.NET Framework 4.x 限定**とし、リリース経路から Windows を
**完全**排除するゴールは今回は追わない。§4 のトレードオフは受容する。

本節は評価スパイクの実測に基づく。スパイクは全て使い捨て（tracked ソース無改造・実施後クリーン）。

### 7.1 決め手になった実測（版跨ぎ自己ホスト = 旧版コンパイラで新版出力を作れるか）

版ピン留めの核心は「旧 seed(版 A) で新 HEAD を build し、必要なら版 B にバンプする」こと。
版 A ≠ B のとき、自己ホストは途中で「直前に build した版 B の Nemerle.dll を、版 A で走る
seed プロセスに参照ロードする」。このロードが成立するかをランタイム別に実測した:

| ランタイム | 版跨ぎ自己ホスト | 実測 |
|---|---|---|
| **Windows CLR4（.NET FW 4.x）** | **可** | boot ncc v636 → Stage1 を env ピンで v700 生成、フルリビルド完走・0 error（`Framework\v4.0.30319\msbuild.exe`）。CLR4 は 1.2 系を寛容に束ねる（`lib/policy.1.2.*.config` の binding-redirect 相当） |
| **CoreCLR（dotnet）** | **不可** | v636 seed が `build-stage2-core` で Nemerle.dll(v700) を生成後、`Nemerle.Compiler.dll` build 時にその v700 を `LibraryReferenceManager.assemblyLoadFrom`→`LoadFromAssemblyPath` でロードして **FileLoadException "manifest definition does not match" (0x80131040)**。厳格ローダーが版一致を強制 |
| **Mono 6.x（Linux）** | **判定以前に不可** | ncc が SRE(AssemblyBuilder) で emit する設計だが、**Mono の Reflection.Emit が Nemerle の emit でアサーション死（SIGABRT）**。VM Mono 6.14 = `custom-attrs.c:718 'ctor_method'`、WSL Mono 6.12 = `sre-encode.c:290 'count>0'`。hello.n(`module H{Main():void{}}`) すら emit 不可 |

**版ピン留め機構そのものは成立**（実証）: `macros/ExpandEnv.n` の `evaluateVar` は
`getEnvironment() ?? getSpecial()[git describe] ?? getDefault()` の順で解決するため、
**環境変数 `GitTag`/`GitRevision` が git describe を上書きする**。CoreCLR 上の Stage1 ncc で
リポジトリ内（describe=637）に env `GitRevision=700` を与えて版属性付き 1 ファイルを compile
→ 出力 AssemblyVersion=1.2.0.700 を確認（`GitRevision=640` のみだと GitTag は git から 1.2）。
**共有ソース `GeneratedAssemblyVersion.n` は無改造で版固定できる**。

### 7.2 Mono に関する重要な切り分け（dotnet-port の回帰ではない）

上の Mono クラッシュが dotnet-port 固有かを確認するため、**dotnet-port 開始前の upstream
Nemerle（commit b5f3b410cce…、2026-06-20「docs: fix typo」）の旧 boot ncc**（`boot/` と
`boot-4.0/` 両方）を Mono 6.12 で走らせた → **旧 boot ncc も同じく SIGABRT（`sre-encode.c:290`）で
hello を emit 不可**。よって **Mono 6.x の SRE と Nemerle の ncc(新旧問わず) が非互換**であり、
dotnet-port が持ち込んだ回帰ではない（歴史的に Nemerle が動いた Mono 2.x〜4.x 世代から、
Mono 6 で SRE 互換が壊れた）。Mono による Linux バンプは「より古い Mono」か「Mono SRE 側の
修正」を要し、dotnet-port 範囲外の深いバックログ。

Mono 経路で先に踏んだ 2 つの手前の壁（参考、いずれもローダー判定より手前）: (1) `xbuild`
（`UseMSBuild` 空の既定 = `Nemerle.XBuild.Tasks.csproj`）は Ncc タスクを**コンパイル不可**
（旧 `Microsoft.Build.*, Version=2.0.0.0` 参照が modern Mono に無い）。(2) `msbuild`
（`/p:UseMSBuild=1` = `Nemerle.MSBuild.Tasks.csproj`）はタスクを build できるが**ロード不可**
（基底 `ManagedCompiler` が Roslyn の `Microsoft.Build.Tasks.CodeAnalysis` に移動、Mono の
タスクロードパスで解決不可）。根本は Ncc タスクが `ManagedCompiler` を継承していること。
ただしこれらは SRE クラッシュ（本命の壁）より手前で、直接 ncc を叩く隔離実験（7.1 Mono 行）で
バイパスした結果が上記 SRE 死。

### 7.3 採用する設計（case 1 = 部分 GO の中身）

- **版ピン**: チェックイン `version.txt`（例 `1.2.640`）から、リリース経路スクリプト
  （`build-stage2-core.ps1` / `build-libs-core.ps1` 等）が `$env:GitTag`/`$env:GitRevision` を
  設定して ncc を呼ぶ。**共有ソース無改造 → Stage フルリビルド不要 → 実装中の版ハザード無し
  → §5-2 の CLR4 回帰ゲート・§5-5 の VsIntegration grep は原則不要**。素の `msbuild`/`dotnet build`
  はスクリプト非経由なので describe に戻るが、ゴールは「リリース経路スクリプト内でのみピン」で
  十分（PO Q1 合意）。
- **§4-1 の A2 門番の格下げ（受容）**: 版が固定されるとロード時 FileLoadException による世代ズレ
  強制検出が消える。検出は既存 provenance JSON（`boot-info.json`/`ncc-info.json`、既に commit を
  記録）を使い、`assembly-version-check.ps1` を「AssemblyVersion == git describe」→「JSON 記録の
  commit == HEAD（同一 version.txt 世代内の祖先）」の**commit 照合**へ再設計する（新規管理ファイルは
  増やさない）。
- **orphan + pinned-worktree の廃止**: +1 パラドックスが消えるので seed を in-tree に置ける。
  同一 version.txt スパン内は seed(commit で古くても版は同じ)で任意 HEAD を Linux/CoreCLR で
  ビルド可能（`build-from-boot.ps1` の `.boot-build-tree` 世代固定 worktree が不要になる）。
- **バンプ = Windows/.NET FW 限定**: 版跨ぎは CLR4 のみ許容（7.1）。version.txt を上げる時だけ
  Windows で新 seed を作る。日常の release-set 生成（工程 Y）は既に Linux/CoreCLR で完結（WP-N5）。

### 7.4 version.txt 運用と バンプ手順

- **cadence（PO 了承）= (c)+ABI ルール**: 毎コミットでは上げない。version.txt を上げるのは
  **マクロ ABI 互換を壊す変更の区切りのみ**。コミット単位の同一性は provenance JSON の commit で
  持ち、ズレは検査スクリプトで検出（7.3）。「同じ AssemblyVersion で中身違い」を混ぜてよいのは
  同一 ABI 世代内、という規約を明文化する（§4-2）。NuGet base(rev) が自動で動かなくなるので
  同一 base 再配布の `preview.<N+1>` 手動運用の比重が増える（§4-3 受容）。
- **バンプ手順は 2 コミット**（seed はソースでなくバイナリ = ソース commit の後でしか作れない）:
  `commit N` で ABI 変更 + `version.txt` 更新（seed はまだ旧版）→ Windows CLR4 でクリーン・
  フルセルフホストして新版 seed 生成 → `commit N+1` で in-tree seed 差し替え。**ピンにより
  commit N+1 は版を動かさない**ので後追いコミットでも seed は無効化されない（+1 パラドックス解消）。

### 7.5 非ゴール / バックログ

- **Mono による Linux バンプ**: 不成立（7.1/7.2）。Mono 6 SRE 非互換で、dotnet-port の回帰では
  ない。深いバックログ（古い Mono or Mono SRE 修正）。
- **案2（CoreCLR に CLR4 の binding-redirect を移植 = ncc `LibraryReferenceManager` 改修）**:
  Linux 完全自足の唯一の残路だが、**PO 判断で優先度を上げない**（当面は部分的 Windows 依存を
  維持）。ncc 共有ソース改修 = Stage リビルド + CLR4 回帰ゲートの重い WP。バックログ据え置き。

### 7.6 実装状態

§7 までは評価の go/no-go 結論。case 1 の実装は 2026-07-21 に着手した（WP-N6 の前提工程
= 46 §6 Q1 の PO 合意による。N6 は N7 の**判断**ではなく**実装**を前提とする — 36 §8 の
順序記述はこの誤りを含んでいたため修正済み）。

## 8. case 1 実装（第 1 スライス: 版ピンの成立）

### 8.1 変更点

| ファイル | 内容 |
|---|---|
| `version.txt`（リポジトリルート、新規） | ピン値 `1.2.635`。`<GitTag>.<GitRevision>` 形式。バンプ規約（ABI 区切りのみ・2 コミット儀式）をヘッダコメントに明記 |
| `dotnet-port/version-pin.ps1`（新規） | dot-source 専用ライブラリ。`Get-NemerleVersionPin`（読み取り・検証・`AssemblyVersion` 算出）/ `Set-NemerleVersionPin`（`$env:GitTag`/`$env:GitRevision` を export） |
| `dotnet-port/assembly-version-check.ps1` | 期待版の出所を `git describe` の再現から version.txt へ変更。検査の意味が「HEAD の commit で建てられたか」から「同じ version.txt スパンで建てられたか」（= CoreCLR ローダーが実際に強制する条件）へ変わる。エラーメッセージも 2 つの原因（バンプ後の未再建 / version.txt の手編集）に沿って書き直し |
| `dotnet-port/build-stage2-core.ps1`、`build-libs-core.ps1` | ncc 起動前に `Set-NemerleVersionPin` を呼ぶ |

`pack-tool.ps1` / `pack-release.ps1` / `publish-boot.ps1` は**無改造で追随**した
（いずれも `Test-NemerleAssemblyVersionFreshness` / `Get-ExpectedNemerleAssemblyVersion`
経由で期待版を得ているため、出所の差し替えがそのまま伝播する）。共有ソース
（`macros/`）は §7.3 の想定どおり**無改造** — よって Stage フルリビルド不要、
§5-2 の CLR4 回帰ゲート・§5-5 の VsIntegration grep は発生しなかった。

ピン初期値に `1.2.635` を選んだ理由: 公開済み seed（`boot-net10` / `seed/1.2.635`、
generation 635 = commit 0cab66afe）の版そのもの。HEAD（describe 638）との間で
compiler ソース（`lib` `ncc` `macros` `Linq`）は**無変更**（`git diff --name-only`
で確認: 差分は `boot-4.0/` バイナリと docs のみ）であり、§7.4 の ABI 区切り規約に照らして
同一スパン。この選び方により**版ピン導入自体に Windows での seed 再生成が不要**になった。

### 8.2 実測（Windows、seed 635 で HEAD 638 をビルド）

版ピンの核心の主張 —「旧 seed で新 HEAD をビルドできる」— の直接検証。
§7.1 の CoreCLR 行で **FileLoadException で不可**と記録された経路が、ピンにより成立するか。

| 検証 | 結果 |
|---|---|
| seed 635（`bin/boot-net10` へ展開）で HEAD（638）の stage2 をビルド | **PASS**。4 アセンブリ 0 error。§7.1 で版跨ぎが落ちていた `Nemerle.Compiler.dll` 段を通過 |
| stage2 出力の刻印版 | **1.2.0.635**（`Nemerle.dll` / `Nemerle.Compiler.dll` / `Nemerle.Macros.dll` / `ncc.exe` の 4 つとも）。describe が 638 でもピン値で刻まれる |
| ピン付き stage2 の自己ホスト（stage3 生成） | **PASS**（0 error）。ピンで建てたコンパイラ自身がコンパイラとして機能する |
| stage3 vs stage3b（同一 stage2 から 2 回） | **4/4 完全バイト一致** — 決定性は維持 |
| stage2 vs stage3 | 3/4 不一致・`ncc.exe` 一致 → **37 §4.2 の baseline と同一**（net4 フレーバー ⇄ core フレーバーの gensym シフトによる既知の世代差）。ピン由来の回帰ではない |
| `build-libs-core.ps1`（ピン経由） | **PASS**。`Nemerle.Linq.dll` = **1.2.0.635** |

**結論: 版ピン機構は成立**。§7.1 が「CoreCLR では版跨ぎ自己ホスト不可」と記録した壁は、
版を跨がせない（両側を version.txt に固定する）ことで回避できることを実測で確認した。
これで seed は自分の世代のソースに縛られず、同一スパン内の任意 HEAD をビルドできる
= **pinned worktree の存在理由が消えた**。

### 8.3 残り（第 1 スライス時点）

1. **A2 の commit 照合化**（§7.3）→ **§8.5 で実施**。
2. **in-tree seed 化と orphan / pinned worktree の廃止**（§7.3）→ **§8.4 で実施**。
3. **バンプ手順のスクリプト整備**（§7.4）→ **未実施**（§8.6）。
4. 素の `msbuild` / `dotnet build` は describe に戻る（§7.3 で受容済み、PO Q1）。
   `tools/msbuild-task/GetGitTagRevision.cs` は未変更。

## 8.4 case 1 実装（第 2 スライス: in-tree seed 化と orphan 廃止）

**seed の配置場所 = `dotnet-port/seed/`**（2026-07-22 PO 決定）。`bin`/`obj`/`dist`
セグメントを避ける必要がある（log 43 の worktree 失敗と同根: VS Code 拡張の
project discovery がそれらを除外する）ことと、移植固有の成果物を `dotnet-port/` 配下へ
集約する一貫性から。

### 8.4.1 変更点

| ファイル | 内容 |
|---|---|
| `dotnet-port/seed/`（新規、7 ファイル） | 635 seed のバイナリ 6 個 + `seed-info.json`。バイナリは orphan `boot-net10` からハッシュ完全一致で移送 |
| `seed-info.json`（schema 2） | `boot-info.json`（schema 1）から改名・改版。`pinnedVersion` を追加。provenance（`generation.commit` = 0cab66afe）は**実際に建てられたコミットのまま維持**し、移送であることを `migratedFrom` に明記 |
| `dotnet-port/publish-seed.ps1`（`publish-boot.ps1` を置換） | orphan ブランチ・throwaway worktree・`seed/<base>` タグ付けを撤去。`dotnet-port/seed/` を書き換えて `git add` するのみ（コミットは人間が行う）。Stage1 を CLR4 msbuild で建てる際に env ピンが要ることを前提条件のヒントに明記 |
| `dotnet-port/build-from-boot.ps1` | seed ref 解決（`-Branch`/`-Seed`）・`git archive` 展開・世代判定・pinned worktree・`dist/release-from-boot` への回収を**全撤去**。in-tree seed を検証して**常にこのチェックアウトのソースを**ビルドする。`-ReleaseTag` は「このチェックアウトがそのタグのコミットであることを検証 + suffix 導出」へ再定義 |
| `dotnet-port/pack-release.ps1` | seed 記録を in-tree seed から読む。**「seed の世代 commit == リリース commit」検査を撤去**（ピンが不要にした制約そのもの）し、代わりに pinnedVersion == version.txt を検査 |
| `DISTRIBUTION.md` / `packaging/README.md` | タグ契約（seed タグは今後作らない）、seed refresh 手順、リリース再現手順を実態へ更新 |

`build-from-boot.ps1` は**名前を変えていない**（発行済みリリースのドキュメントが
この名前を参照しているため）。行数は 304 → 165 行。

### 8.4.2 実測

| 検証 | 結果 |
|---|---|
| in-tree seed で HEAD の stage2 をビルド | **PASS**（0 error） |
| その出力 vs orphan seed から建てた stage2 | **4/4 完全バイト一致** — 移送でバイナリが変質していないことの証明 |
| dirty tree での `build-from-boot.ps1` | **期待どおり停止**（clean tree 要求のガードが先に効く） |

### 8.4.3 廃止したものと残したもの

- **廃止**: orphan ブランチ `boot-net10` の消費経路、`.boot-build-tree` worktree、
  `build-from-boot.ps1` の `-Branch`/`-Seed`/`-KeepWorktree`、`dist/release-from-boot`、
  publish 時の `seed/<base>` タグ自動付与。
- **残す**: orphan ブランチ `boot-net10` そのものと既存の `seed/1.2.630` / `seed/1.2.635`
  タグは**削除しない**。発行済み 1.2.635-preview.1 の再現手段として履歴に必要で、
  消しても得るものが無い。新規の seed 発行はもう orphan に対して行わない。

### 8.4.4 end-to-end 受け入れ（`build-from-boot.ps1` フルチェーン、Windows）

clean tree で `pwsh dotnet-port/build-from-boot.ps1` を完走 → **release set 生成 PASS**
（Sdk / Templates / Linq 1.2.635-preview.1 + vscode-nemerle-0.9.0.vsix + README +
release-info.json）。

重要なのは封緘されたコミット: **`a4cdc5d67`（HEAD）**であり、seed の世代コミットではない。
旧機構では pinned worktree に退避して seed 世代（0cab66afe）を封緘するしかなかったので、
**「HEAD からリリースセットを作る」こと自体が新しく可能になった**。

`release-info.json` の `seed` フィールドは 3 つの異なるコミットを正しく書き分けている:

| フィールド | 値 | 意味 |
|---|---|---|
| `commit`（トップレベル） | a4cdc5d67 | リリースのソースコミット（= HEAD） |
| `seed.commit` | a4cdc5d67 | seed ディレクトリを最後に変更したコミット |
| `seed.generation.commit` | 0cab66afe | seed バイナリが**実際に建てられた**コミット |

## 8.5 case 1 実装（第 3 スライス: A2 の commit 照合化）

§4-1 で受容した「門番の格下げ」の埋め合わせ。ピンにより同一スパン内は全て同版になるため、
`Test-NemerleAssemblyVersionFreshness` は「HEAD で建てたか」と「10 コミット前に建てたか」を
区別できない。その検出を provenance の commit へ移す。

`assembly-version-check.ps1` に `Test-NemerleProvenanceCommit` を追加。問うのは
**「記録された commit は HEAD の祖先か」**の 1 点:

| 状況 | 挙動 | 理由 |
|---|---|---|
| 祖先で、HEAD より前 | **情報行**（何コミット前かを表示）。失敗ではない | seed が HEAD より古いのは正常。それを可能にするのがピン |
| 祖先で、HEAD と同一 | 情報行 | |
| **祖先でない** | **失敗**（`-WarnOnly` で警告に降格） | 別ブランチ / 書き換えられた履歴 / 到達しないコミット由来。ビルド対象のソースと無関係な成果物 |
| リポジトリに存在しない | 警告してスキップ | shallow clone・source archive は正当に履歴を持たない。環境を罰しない |

`build-from-boot.ps1` の seed 検証直後から `-WarnOnly` で呼ぶ（N6 が要求する
「報告はするが止めない」形）。

実測（4 分岐すべて）: 実 seed（0cab66afe、HEAD の 5 コミット前）→ 情報行 /
orphan root（7f3c854e8、非祖先）→ 警告 / 未知コミット → スキップ警告 /
HEAD 自身 → 「built from HEAD」。

## 8.6 case 1 の残り

**バンプ手順のスクリプト整備（§7.4）のみ未実施**。version.txt を上げる時だけ必要な
Windows/CLR4 限定の儀式で、**日常のビルド・リリース・CI のどれもブロックしない**
（WP-N6 の前提としても不要）。手順自体は `publish-seed.ps1` のヘッダと
`packaging/README.md` に文章として記載済み — 要点は「CLR4 msbuild は version.txt を
読まないので、Stage1 を建てる前に `Set-NemerleVersionPin` で env にピンを流すこと」。

実際に版を上げる時（= マクロ ABI 互換を壊す変更の区切り）に、実バンプと同時に
スクリプト化するのが妥当。未検証のまま儀式スクリプトだけ先に置いても価値が薄いため、
本 log では意図的に未実施として残す。
