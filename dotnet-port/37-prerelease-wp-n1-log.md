# 37. WP-N1: ビルド再現性と地固め 実装ログ

実施日: 2026-07-16

ブランチ: `wip/dotnet-port`

対象: `36-prerelease-quality-plan.md` の **WP-N1**(A2 版ハザードの機械化 / A1 決定的ビルド完成 /
D2 増分ビルド判定 / D7 rsp レガシー整理)。

## 1. 結論(受け入れ基準との対応)

| 基準(36 §6 WP-N1) | 結果 |
|---|---|
| 1. stage2 の 2 回独立ビルドがマスク無しで 4 アセンブリ完全バイト一致。stage3 も同様(`-debug` 時は PDB も) | **PASS(一部読み替え)**: stage2×2 完全一致、`-EmitDebug`×2 は PDB 含む 8/8 完全一致、stage3==stage4 完全一致。**stage2==stage3 は既知の世代差(16 §3)により原理的に不成立** — §4.2 に読み替えの根拠を記録 |
| 2. 古い Stage1 で `build-stage2-core.ps1` を実行するとビルド前に版不一致エラー+復旧手順で停止 | **PASS**(§3。実際に古かった Stage1 1.2.0.601 vs HEAD 期待 1.2.0.618 の実地 fixture で確認) |
| 3. `DefineConstants` のみ変更した `dotnet build` が再コンパイルを実行し、無変更の再ビルドはスキップ | **PASS**(§5。`samples/Defines` の 6 ステップで確認) |
| 4. provenance(`ncc-info.json` / server 版照合)が決定的 MVID と矛盾しない | **PASS**(§6。provenance-mismatch シナリオ含む bundled server スイート PASS) |
| 5. 回帰: testsuite 全数 regression 0、CLR4 スモーク、raw LSP / bundled server、`npm test` PASS | **PASS**(§7。testsuite 601/636 = ベースライン同数、他すべて PASS) |

## 2. 実装内容

### A1: 決定的ビルド(`dotnet-port\Nemerle.CoreEmit\Emitter.cs`)

- `ComputeDeterministicId`(SHA1 `IncrementalHash` → `BlobContentId.FromHash`)を追加し、
  - `ManagedPEBuilder` に `deterministicIdProvider:` として渡す(MVID / PE COFF タイムスタンプが
    コンテンツハッシュ由来になる。従来は省略により `BlobContentId.GetTimeBasedProvider()` に
    フォールバックしていた — これが 16 §「将来課題」の非決定源)。
  - `PortablePdbBuilder` に `idProvider:` として渡す(PdbId も同様)。
- Roslyn `/deterministic` と同じレシピ(16 §5 将来課題に記載の標準手法)。CLI フラグは追加せず
  既定動作の変更(ncc に `/deterministic` 相当の既存スイッチは無いことを確認済み)。
- **`MacroClassGen.n:749` の演算子名 `GetHashCode()` 問題(16 §2〜4 の「唯一の本質的非決定源」)は
  既に修正済みだった**ことを確認(現行ソースは `Util.tmpname("operator")`。16 §5 案 A-1 が
  過去コミットで実装済み)。今回の変更対象は Emitter.cs のみ。
- `ncc\generation\CoreEmitBridge.n` / `HierarchyEmitter.n` は変更不要(リフレクション経由の
  薄いラッパーで、パラメーターはそのまま転送される)。

### A1: 比較スクリプト(`dotnet-port\compare-stage.ps1` 新規)

従来の stage 比較は scratchpad の使い捨てツール(PeDiff/HeapDiff、リポジトリ未収載)で
MVID/タイムスタンプをマスクして行っていた(13 §、16 §6)。決定化によりマスクが不要になったため、
マスク無し完全バイト比較(存在 → サイズ → SHA256、不一致時は先頭相違オフセット報告)を
チェックイン版として新設。既定対象は 4 アセンブリ、`-IncludePdb` で PDB も比較。

### A2: 版ハザードの機械化(`dotnet-port\assembly-version-check.ps1` 新規 + 3 スクリプト配線)

- `Get-ExpectedNemerleAssemblyVersion`: `GeneratedAssemblyVersion` マクロ
  (`macros\GeneratedAssemblyVersion.n`)と同じ `git describe --tags --long` +
  タグの `[^\d\.]` 除去レシピを PowerShell で再現し、HEAD が要求する版(`{tag}.0.{rev}`)を計算。
  マクロ側が既定値 1.2.0.9999 にフォールバックするケース(git 不在・タグ無し等)は `$null` を
  返して検査スキップ。
- `Test-NemerleAssemblyVersionFreshness`: 実測版(`AssemblyName.GetAssemblyName`)と期待版を比較し、
  不一致なら復旧手順(Stage1 フルリビルド 3 コマンド、30 §再現コマンド由来)つきで throw。
  `-WarnOnly` で警告のみに降格でき、WP-N6(checked-in stage の CI 鮮度検出)から再利用可能な形
  (36 §6 WP-N1 の「流用できる形にする」要件)。
- 配線: `build-stage2-core.ps1`(`-Compiler` 存在チェック直後、rsp 生成・ビルド開始前)/
  `pack-tool.ps1`(`$CompilerDir` 検査直後)/ `pack-release.ps1`(commit 突合の後に
  `ncc-info.json` の `nemerleAssemblyVersion` と期待版の突合を追加)。

### D2: `CoreCompile` 増分キー(`dotnet-port\msbuild\Nemerle.Core.targets`)

MSBuild の Inputs/Outputs はプロパティ値を直接扱えないため、新ターゲット
`_NemerleWriteCoreCompileCache`(`BeforeTargets="CoreCompile"`)が `DefineConstants` /
`NemerleAdditionalOptions` を `$(IntermediateOutputPath)Nemerle.coreCompileInputs.cache` に
書き出し(`WriteLinesToFile` + **`WriteOnlyWhenDifferent="true"`** — 無変更時にタイムスタンプを
更新せず増分スキップを保つ要)、そのファイルを `CoreCompile` の `Inputs` に追加。
`;` は `%3B` にエスケープして 1 プロパティ 1 行を維持。`FileWrites` 登録済み(Clean で削除)。
`CoreCompile` はターゲット 1 つの中で `NccCompile` / `Exec` フォールバックが分岐する構造のため、
両経路に同じ Inputs が効く。SDK パッケージは同一ファイルを同梱するため 1 本の変更で
repo checkout 経路 / SDK 経路の両方に効く(WP-M6 の設計どおり)。

### D7: rsp レガシー整理(`pack-tool.ps1` / `Nemerle.Tool.csproj` / `DISTRIBUTION.md`)

WP-A2 の auto-ref 化(`ncc\passes.n` の `LoadCoreStdlibReferences`)以降、
`ncc.default.rsp` / `gen-default-rsp.ps1` は「auto-ref を `-no-stdlib` で殺して同じ参照集合を
手組みで再現する」だけのレガシーだった。整理内容:

- `pack-tool.ps1`: 両ファイルの生成コードを削除。`ncc.cmd` は `dotnet "%HERE%ncc.dll" %*` を
  直接呼ぶだけに簡略化(rsp 鮮度検知・再生成ロジック削除。成功時の `Nemerle*.dll` カレント
  コピーは維持)。ヘッダーの説明・レイアウト図・Smoke test・完了メッセージを新実態に更新。
- 副次効果: rsp の絶対パス焼き込みに起因していた**再配置(relocation)問題が消滅**
  (レイアウトをコピー/移動しても再生成不要)。
- `Nemerle.Tool\Nemerle.Tool.csproj`: 実装(`Program.cs` は rsp 不使用)と食い違っていた
  stale コメントを修正。
- `DISTRIBUTION.md`: WP-A2 注記の「整理は未実施」を解消済みに更新、レイアウト図・再現手順を
  rsp 無し前提に書き換え、`-from-file:` の説明は歴史的経緯として保持。
- 削除の安全性: MSBuild 経路(`Nemerle.Core.targets`)・SDK パッケージ(元々同梱除外)・
  `Nemerle.Tool` シムのいずれも rsp 非依存であることを事前調査で確認。
  `ProjectInfo.Test` の「nupkg に rsp 系が含まれない」assert は従来どおり有効。

## 3. A2 の実地 fixture(受け入れ基準 2)

着手時点のワークツリーが**まさに版ハザード状態だった**(Stage1 = 1.2.0.601、
HEAD `ec9976dd1` = `v1.2-618` → 期待版 1.2.0.618)。この状態のまま
`pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2a` を実行し、
**rsp 生成・ビルド開始前に**次で停止することを確認(exit 1):

```
Compiler (...\Stage1\ncc.exe) assembly-version mismatch: '...\Stage1\Nemerle.dll' is 1.2.0.601,
but HEAD expects 1.2.0.618 (from 'git describe --tags --long' at ...).
...
Recover with a full Stage1 rebuild (dotnet-port\30-devenv2-wp-m1-log.md:246-254):
  Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
  & "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
  pwsh dotnet-port\refresh-stage1-core.ps1
```

提示された復旧手順をそのまま実行して Stage1 を 1.2.0.618 で再構築
(`refresh-stage1-core.ps1` が決定化修正済み `Nemerle.CoreEmit.dll` を Stage1 に配置)。
以降の `build-stage2-core.ps1` 実行では
`Compiler (...) assembly-version freshness OK (1.2.0.618).` が表示される。

## 4. 決定性の実測(受け入れ基準 1)

### 4.1 provider だけでは足りなかった: MVID の後処理パッチ

最初の実装(`deterministicIdProvider` / `idProvider` の 2 引数追加)での実測は、
**PDB 4 本と全コンテンツ(IL・メタデータ・文字列)はバイト一致**する一方、各アセンブリの
PE がちょうど 20 バイト(COFF `TimeDateStamp` 4B @0x88 + MVID 16B)だけ毎回異なった。
原因: `PersistedAssemblyBuilder.GenerateMetadata` が **MVID をランダム GUID として先に
GUID ヒープへ埋め込む**ため、provider のハッシュ入力自体にランダム値が含まれ、導出される
スタンプも毎回変わる(Roslyn は MVID を予約ブロブ=ゼロのままハッシュして後から書き戻すが、
`PersistedAssemblyBuilder` にはそのフックが無い)。

対策として `Emitter.Save` に post-serialize パッチ(`PatchDeterministicPeStamp`)を追加:
`PEReader`/`MetadataReader` で COFF スタンプと MVID(GUID ヒープ内オフセットは
`GetModuleDefinition().Mvid` から計算し、**パッチ前に実バイトと `GetGuid()` の一致を検証**する
ガードつき)を特定 → 両領域をゼロクリア → イメージ全体の SHA1 から
`BlobContentId.FromHash` で contentId を導出 → `Guid`/`Stamp` を書き戻す。
`deterministicIdProvider` も残置(無害。将来 `PersistedAssemblyBuilder` が MVID 予約に
対応すればそちらが主経路になる)。

### 4.2 実測結果(パッチ後、マスク無し・SHA256 完全一致)

| 比較 | 結果 |
|---|---|
| stage2(Stage2a)vs stage2(Stage2b)— 2 回独立ビルド | **4/4 完全一致**(Nemerle.dll / Nemerle.Compiler.dll / Nemerle.Macros.dll / ncc.exe) |
| 正準 stage2 vs Stage2a(3 回目のビルド) | **4/4 完全一致** |
| stage2 vs stage3 | 3/4 不一致(ncc.exe は一致)— **既知の世代差**(下記) |
| stage3 vs stage4 | **4/4 完全一致**(コアフレーバー同士のフィックスポイント) |
| `-EmitDebug` ×2(同一 OutDir、間で退避) | **8/8 完全一致**(4 アセンブリ + 4 PDB) |

**stage2 ≠ stage3 について**: 36 §6 の受け入れ基準 1 は「stage3 == stage2 も同様」と
書かれていたが、これは既知の**世代差**(16 §3: Stage1 は net4 フレーバー、stage2 は core
フレーバーで、コンパイル中の gensym ID 消費数が異なり生成名の連番が全体にシフトする。
非決定性ではなく世代にのみ依存する決定的な差)により原理的に成立しない。
本 WP では「同一コンパイラーでの再現性(stage2×2、`-EmitDebug`×2)」と
「自己ホストのフィックスポイント(stage3 == stage4 完全バイト一致)」で決定性を検証・達成した。
stage3 を作った stage2 自体が 2 回独立ビルドで一致しているため、チェーン全体が再現可能である。
なお ncc.exe(ソースが小さく gensym シフトの影響を受けない)は世代を跨いでも一致した。

比較は新設の `compare-stage.ps1` によるマスク無し比較(exit 0/1)。証跡ディレクトリ:
`bin\Release\core\{Stage2, Stage2a, Stage2b, Stage3, Stage4, Stage2dbg-run1, Stage2dbg-run2}`。

## 5. D2 の実測(受け入れ基準 3)

`samples/Defines`(`EnableCustom != false` で `CUSTOM_FEATURE` を定義、`defines.n` は
`#if CUSTOM_FEATURE` で 42 / `#else` で意図的な型エラー)にて、obj/bin クリーンから:

| # | コマンド | 結果 |
|---|---|---|
| 1 | `dotnet build Defines.nproj` | 成功(CoreCompile 実行) |
| 2 | 同再実行 | **CoreCompile スキップ**(up-to-date) |
| 3 | `dotnet build -p:EnableCustom=false` | **再コンパイル実行 → 意図的な型エラーで失敗**(`expected int, got string ... System.String is not a subtype of System.Int32`)— 修正前はスキップされ成功扱いになっていた穴 |
| 4 | `dotnet build`(既定に戻す) | 再コンパイル実行 → 成功 |
| 5 | 同再実行 | スキップ |
| 6 | `dotnet clean` | キャッシュファイル(`obj\Debug\net10.0\Nemerle.coreCompileInputs.cache`)も削除 |

## 6. provenance 整合(受け入れ基準 4)

- `pack-tool.ps1 -Pack` を決定化済み Stage2 に対して実行: 版チェック
  `CompilerDir (...) assembly-version freshness OK (1.2.0.618).` の後、
  `ncc-info.json`(commit `ec9976dd1-dirty`、Nemerle 1.2.0.618)と
  `Nemerle.Sdk.Unofficial.1.2.618-preview.2.nupkg` / `Nemerle.Templates.Unofficial.1.2.618-preview.2.nupkg`
  を生成(コンパイラー版が 601 → 618 に進んだため 36 §9 のリスク表どおり再 pack)。
  ※この 618-preview.2 は後に版サフィックス規約の明文化に伴い破棄・再番号付けした — §10 参照。
- `ToolchainProvenance` は AssemblyVersion を json ではなく **Nemerle.dll 自体から読む**設計
  (`ProjectInfo\ToolchainProvenance.cs`)のため、MVID がコンテンツハッシュ由来になっても
  照合ロジックに影響しない。bundled server スイートの provenance-mismatch シナリオが
  PASS していることで実測済み(§7)。
- pack 中の `MSB3246: PE image does not have metadata` 警告は既知・無害
  (20 §、`Nemerle.Compiler.Hosting.csproj` 内に文書化済み)。決定化とは無関係。

## 7. 回帰(受け入れ基準 5)

| 対象 | 結果 |
|---|---|
| testsuite 全数(636) | **PASS 601/636**(positive 435/469 + negative 166/167)— 30 §のベースラインと同数、**新規 regression 0** |
| CLR4 スモーク(Stage1\ncc.exe を `dotnet exec` 無しのネイティブ CLR4 で実行) | **PASS**: hello(`Hello from stage-test!`、exit 0)/ hello2(`1` → `2, 4, 6`、exit 0。Nemerle.dll コピー後) |
| ProjectInfo unit + sample integration(`--integration`) | **PASS** |
| raw LSP integration(29 本 + 計測シナリオ) | **PASS all WP-L3 LSP integration scenarios** |
| bundled server スイート(VSIX 抽出サーバー、provenance シナリオ含む) | **PASS**(`-VsixPath ..\dist\release\vscode-nemerle-0.8.2.vsix` を明示、下記の既知の食い違い参照) |
| `npm ci` / `check-types` / `lint` / `npm test` | **PASS**(unit 22/22、脆弱性 0) |
| `npm run test:integration` / `test:sdk`(Extension Host) | **PASS**(Restricted Mode 1/1、Sdk-based project 1/1) |
| `npm run package` / `test:vsix`(clean-machine install) | **PASS**(`vscode-nemerle-0.8.2.vsix` 468 files / 4.45 MB、install 後にサーバー起動 1/1) |
| `npm audit` / `npm audit --omit=dev` | 0 vulnerabilities |

### incremental 有効/無効の edit-to-diagnostics 実測(Sokoban、raw LSP 計測シナリオ)

| モード | p50 | p95 | n |
|---|---|---|---|
| incremental **ON**(既定) | **330 ms** | **392 ms** | 12 |
| incremental **OFF**(`NEMERLE_INCREMENTAL_UPDATE=0`) | **561 ms** | **613 ms** | 12 |

34 §「基準2」の実測(ON 327/404、OFF 556-590/622-638)と整合し、決定化・再 pack 後も
incremental rebuild の優位が維持されている。

## 8. 再現コマンド

```powershell
# --- A2 fixture(Stage1 が古い場合、ビルド前に停止することの確認)---
pwsh dotnet-port\build-stage2-core.ps1        # Stage1 が HEAD より古ければ復旧手順つきで throw

# --- Stage1 フルリビルド(復旧手順そのもの)---
Remove-Item -Recurse -Force bin\Release\net-4.0\Stage1
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /tv:4.0 /p:TargetFrameworkVersion=v4.0 /p:NTargetName=Rebuild /p:Configuration=Release /t:Stage1
pwsh dotnet-port\refresh-stage1-core.ps1

# --- 決定性(受け入れ基準 1)---
pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2a
pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2b
pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage2a -DirB bin\Release\core\Stage2b   # 4/4 一致
pwsh dotnet-port\build-stage2-core.ps1                                                             # 正準 Stage2
pwsh dotnet-port\build-stage2-core.ps1 -Compiler bin\Release\core\Stage2\ncc.exe -OutDir bin\Release\core\Stage3
pwsh dotnet-port\build-stage2-core.ps1 -Compiler bin\Release\core\Stage3\ncc.exe -OutDir bin\Release\core\Stage4
pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage3 -DirB bin\Release\core\Stage4     # 4/4 一致(フィックスポイント)
# PDB(同一 OutDir で 2 回、間で退避 — PE の CodeView に PDB 絶対パスが入るため)
pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2dbg -EmitDebug
Move-Item bin\Release\core\Stage2dbg bin\Release\core\Stage2dbg-run1
pwsh dotnet-port\build-stage2-core.ps1 -OutDir bin\Release\core\Stage2dbg -EmitDebug
Move-Item bin\Release\core\Stage2dbg bin\Release\core\Stage2dbg-run2
pwsh dotnet-port\compare-stage.ps1 -DirA bin\Release\core\Stage2dbg-run1 -DirB bin\Release\core\Stage2dbg-run2 -IncludePdb  # 8/8 一致
pwsh dotnet-port\build-stage2-core.ps1        # rsp を非 -debug の既定状態へ戻す

# --- D2(samples\Defines)---
dotnet build dotnet-port\samples\Defines\Defines.nproj                        # 成功
dotnet build dotnet-port\samples\Defines\Defines.nproj                        # CoreCompile スキップ
dotnet build dotnet-port\samples\Defines\Defines.nproj -p:EnableCustom=false  # 再コンパイル → 意図的な型エラー
dotnet build dotnet-port\samples\Defines\Defines.nproj                        # 再コンパイル → 成功

# --- 再 pack と回帰一式(33/35 §再現コマンド踏襲)---
pwsh dotnet-port\pack-tool.ps1 -Pack
pwsh dotnet-port\run-testsuite-core.ps1                                       # 601/636
# CLR4 スモーク(dotnet exec を挟まないネイティブ CLR4 実行、13 §5)
bin\Release\net-4.0\Stage1\ncc.exe -out:hello.exe hello.n;  .\hello.exe
bin\Release\net-4.0\Stage1\ncc.exe -out:hello2.exe hello2.n; Copy-Item bin\Release\net-4.0\Stage1\Nemerle.dll .; .\hello2.exe
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release -t:Rebuild
dotnet run -c Release --project dotnet-port\ProjectInfo.Test\Nemerle.ProjectInfo.Test.csproj -- --integration
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll  # incremental 実測もここで出力
pwsh dotnet-port\vscode-nemerle\pack-server.ps1
Push-Location dotnet-port\vscode-nemerle
npm ci; npm run check-types; npm run lint; npm test
npm run test:integration; npm run test:sdk; npm run package; npm run test:vsix
pwsh .\test-bundled-server.ps1 -VsixPath ..\dist\release\vscode-nemerle-0.8.2.vsix -NoBuild
npm audit; npm audit --omit=dev
Pop-Location
```

## 9. 既知の制約 / 次 WP への境界

1. **stage2 ≠ stage3(世代差)**: Stage1(net4 フレーバー)と stage2(core フレーバー)で
   gensym ID 消費数が異なるため、stage2 と stage3 はコンテンツレベルで一致しない(16 §3 の
   既知事実、非決定性ではない)。決定性の保証は「同一コンパイラーの再現性」と
   「stage3 == stage4 フィックスポイント」で担保する。Stage1 を core フレーバー化しない限り
   恒久(36 の WP-N6 が checked-in stage 起点の CI を組む際もこの前提で設計する)。
2. **`test-bundled-server.ps1` の既定 `-VsixPath` が古い**: 既定は拡張ディレクトリ直下だが、
   `npm run package:vsix` は WP-M6 フォローアップ以降 `../dist/release/` へ出力するため、
   既定のままでは "VSIX not found" になる(本 WP 以前からの食い違い)。`-VsixPath` の明示で
   回避(§8)。既定パスの追随は小粒の別修正として残す。
3. **MVID 後処理パッチの前提**: `PatchDeterministicPeStamp` は
   `PersistedAssemblyBuilder.GenerateMetadata` が MVID を予約せずランダム確定させる現仕様への
   対処。将来 dotnet/runtime が MVID 予約(Roslyn 方式)に対応したら `deterministicIdProvider`
   が主経路になり、パッチは削除できる(パッチには誤オフセット書き込みを防ぐ MVID 実バイト
   照合ガードあり)。
4. **`MacroClassGen.n` の演算子名 `GetHashCode()` 除去(16 §5 案 A-1)は本 WP 開始時点で
   実装済みだった**(過去コミットに含まれる)。本 WP の決定化は Emitter.cs のみで完結した。
5. **A2 検査の WP-N6 への流用**: `assembly-version-check.ps1` は dot-source ライブラリで、
   `-WarnOnly` により「CI では警告して続行」の形で checked-in stage1/stage2 の鮮度検出に
   そのまま使える(36 §6 WP-N6 の受け入れ基準が要求する検出の実装部品)。
6. **`dotnet-port\rsp\stage2\*.rsp` の機械的な揺れはコミットしない**: これらは
   `build-stage2-core.ps1` が実行のたびに再生成する生成物で、差分は前回実行の出力先パス
   (Stage2/Stage3)の揺れのみ。本 WP では HEAD の状態のまま維持した。
7. **コンパイラー版 618 への前進**: Stage リビルドにより版が 601 → 618 に進み、
   `1.2.618-preview.2` の Sdk/Templates package を再 pack 済み(36 §9 リスク表の想定どおり。
   この版番号は §10 で規約化に伴い破棄・再番号付け)。
   本コミット自体でさらに HEAD が進むため、次に stage スクリプトを実行すると版チェックが
   Stage1 フルリビルドを要求する — これは A2 の意図された動作である。

前身: 実装計画は `36-prerelease-quality-plan.md` §6 WP-N1。関連ログ: 14(F4)/ 16(決定性診断)/
30(版ハザード実務)/ 33・35(回帰一式の再現コマンド)。

## 10. 追記(コミット a120a931b 後): package 版サフィックスの規約化と再番号付け

§6 の `1.2.618-preview.2` の「`.2`」は自動採番ではなく `pack-tool.ps1 -PackageVersionSuffix` の
ハードコード既定値(601 世代の同一 base 再配布で preview.1 → preview.2 に上げた際の据え置き)
だったため、規約を明文化した:

- **規約**(`DISTRIBUTION.md` の「package 版サフィックスの規約化」注記 + `pack-tool.ps1` の
  パラメーターコメント): base(`1.2.<rev>`、同梱 Nemerle.dll の実 AssemblyVersion 由来)が
  進んだら **`preview.1` にリセット**(スクリプト既定値も `preview.1` へ変更)。同一 base の
  再配布時のみ `preview.<N+1>` を明示。配布済み版番号(GitHub release assets 等)の再発行禁止。
  スクリプトは配布済み状態を知り得ないため同一 base のバンプは操作者責任。

- **618 セットの扱い(批判的検討の結果)**: `1.2.618-preview.2` は未配布(公開済みセットは
  601-preview.2 のまま)なので番号を破棄し規約に従い直すと判断。ただし
  **`1.2.618-preview.1` への再発行は不可能** — WP-N1 のコミットで HEAD が 619 に進んだ時点で、
  A2 チェックが「Stage2(618)は HEAD より古い」として pack を停止する(実測済み。§3 の
  fixture と同じ検査が pack-tool 経路でも機能した証跡でもある)。よって 618 は欠番とし、
  618-preview.2 の 2 nupkg は dist\release と NuGet グローバルキャッシュから削除した。

- **対応**: 本追記のコミット後の rev で復旧手順(Stage1 フルリビルド → stage2)を実行し、
  規約どおり `1.2.<rev>-preview.1` として pack。VSIX(bundle-info)も同一コミットから
  再生成して候補セットの commit 整合を保ち、package 依存テスト
  (`ProjectInfo.Test --integration` / `npm run test:sdk`)を再実行して PASS を確認する。
  §6〜§8 に記録した 618 での検証結果は「当時の実測」としてそのまま残す(内容は同一ソース、
  版文字列のみが異なる)。

この「コミットするたびに成果物が版遅れになる」性質は版スキームの本質(§9-7)であり、
リリース手順としては **「コードを確定コミット → その HEAD で Stage チェーン再構築 → pack →
封緘(pack-release)」の順で行い、pack 後に追加コミットを挟まない**ことが規約の運用形になる。
