# 25. WP-L1: VS Code extension shell 実装記録

日付: 2026-07-13
対象: `dotnet-port/docs/24-vscode-development-plan.md` の WP-L1 だけ。WP-L2 の
`.nproj` / MSBuild project model は実装していない。

## 結論

達成。`dotnet-port/vscode-nemerle/` に development VS Code extension を追加し、
`.n` language registration、手書き TextMate grammar、language configuration、
`vscode-languageclient` による既存 .NET 10 stdio server の起動・停止・restart、
Workspace Trust gate、Output Channel、固定 npm dependency、headless/Extension Host tests、
VSIX packaging を実装した。

VS Code 1.128.0 の Extension Host で、次を自動実測した。

- `.n` が `nemerle` language として開く。
- line/block comment、bracket auto-close、auto-surround、brace indentation が動く。
- disk 上では型エラーのままの `Broken.n` を開くと Error diagnostic が現れ、未保存 buffer
  だけを修正すると diagnostics が空になる。
- Restart 前の server PID が終了してから別 PID が起動し、process が重複しない。
- untrusted workspace でも language support は有効だが、設定済み server が起動せず、
  Restart command を直接呼んでも PID が生成されない。

## 開始状態

開始時の確認結果:

```text
branch: wip/dotnet-port
HEAD:   32a53a5ba6a67f41527b4fa253049533b2630355
status: clean (origin/wip/dotnet-port より ahead 2)
AGENTS.md: repository 内になし
```

## 実装構成

- `package.json`
  - `.n` → `nemerle`、grammar、language configuration を contribution として登録。
  - `Nemerle: Restart Language Server` / `Nemerle: Show Output`。
  - `nemerle.server.path` / `nemerle.server.trace`。
  - `untrustedWorkspaces.supported = limited`。declarative language support は残し、実行だけを
    extension code で gate する。workspace setting の server path は restricted configuration。
- `src/extension.ts`
  - activation/deactivation、commands、trust grant/configuration events を登録。
- `src/clientController.ts`
  - start/restart/trace/dispose を一つの Promise queue で直列化。
  - `maxRestartCount = 0` と initialization failure stop により、自動 restart loop を作らない。
  - `workspace.isTrusted` を activation と command の両方で確認する。
  - `LogOutputChannel` を languageclient の output/trace channel に渡す。languageclient 10.1.0 の
    executable transport は server stderr を同 channel の error log へ転送し、stdout は
    `StreamMessageReader` 専用になる。
  - path error の UI 選択待ちは lifecycle queue の外へ出した。有効 path への設定変更が、
    未応答 error notification によって block されない。
- `src/serverLaunch.ts`
  - 空、相対、missing、directory path を process 起動前に理解可能な error にする。
  - `.dll` は executable=`dotnet`, arguments=`["exec", serverPath]` と分離し、`shell=false`。
  - 将来 bundled native apphost を使えるよう、非 `.dll` は executable 自体として扱う。
    dotnet executable の設定公開は必要になった時にこの境界へ追加できる。
- `syntaxes/nemerle.tmLanguage.json`
  - 外部/legacy grammar をコピーしていない手書き grammar。
- `language-configuration.json`
  - comments、brackets、auto-closing、auto-surrounding、基本 brace indentation。
- `test/`
  - manifest/config/launcher tests、Oniguruma/TextMate tokenization tests。
  - trusted/untrusted VS Code 1.128.0 Extension Host tests。
- `README.md` / `LICENSE`
  - development server path と WP-L2 非対応を明記。

## grammar の根拠と出典

既存 editor grammar は利用していない。repository のコンパイラー source を仕様の根拠にした。

- 固定 keyword と operator character: `ncc/parsing/Lexer.n:1620-1655`
- token dispatch、quotation `<[` / `]>`、recursive string `<#`:
  `ncc/parsing/Lexer.n:588-765`
- numeric prefixes/suffixes: `ncc/parsing/Lexer.n:1144-1383`
- identifier/char/string/verbatim/recursive string: `ncc/parsing/Lexer.n:1393-1591`
- comments/doc comments: `ncc/parsing/Lexer.n:1853-1925`
- code quotation/splice: `ncc/parsing/MainParser.n:736-790`, `3322-3398`
- dollarized string splice: `macros/string.n:681-895`
- 代表的な core macro keyword: `macros/core.n`

static grammar は lexer の fixed keywords に、`if` / `else` / `return` / `foreach` 等の
標準 core macro keyword だけを加えた。macro assembly は任意の syntax keyword を追加できるため、
外部 macro の keyword を固定 language keyword として推測しない。

これらの source header と repository top-level `COPYRIGHT` は University of Wroclaw の
BSD 3-Clause 相当。extension grammar は source の regex/code をコピーせず、字句仕様を読み取って
新規作成した。外部 grammar 資産は 0 件。

## VS Code API / package 選定

2026-07-13 に現行公式文書と npm registry を確認した。

- Language Server Extension Guide:
  <https://code.visualstudio.com/api/language-extensions/language-server-extension-guide>
- Language Configuration Guide:
  <https://code.visualstudio.com/api/language-extensions/language-configuration-guide>
- Syntax Highlight Guide:
  <https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide>
- Workspace Trust extension guide:
  <https://code.visualstudio.com/api/extension-guides/workspace-trust>
- Extension testing:
  <https://code.visualstudio.com/api/working-with-extensions/testing-extension>
- VSIX publishing/package:
  <https://code.visualstudio.com/api/working-with-extensions/publishing-extension>
- official language client repository:
  <https://github.com/microsoft/vscode-languageserver-node>

direct dependency はすべて exact version、transitive dependency は `package-lock.json` で固定した。
TypeScript latest 7.0.2 は `typescript-eslint 8.63.0` の peer range `<6.1.0` 外だったため、
安定版 5.9.3 を選んだ。

```text
runtime: vscode-languageclient 10.1.0
dev: @types/vscode 1.125.0, @types/node 22.20.1, TypeScript 5.9.3
     ESLint 10.7.0, @typescript-eslint 8.63.0
     @vscode/test-electron 3.0.0, @vscode/vsce 3.9.2
     Mocha 11.7.6, vscode-textmate 9.3.2, vscode-oniguruma 2.0.1
```

`nemerle.server.trace` は languageclient の convention `<client-id>.trace.server` と名前が異なるため、
設定値を `Trace.Off/Messages/Verbose` へ明示変換した。

## dependency license / notice

VSIX に入る production tree を `npm ls --omit=dev --all` と各 package.json で確認した。

| package | version | license |
|---|---:|---|
| vscode-languageclient | 10.1.0 | MIT |
| minimatch | 10.2.5 | BlueOak-1.0.0 |
| brace-expansion | 5.0.7 | MIT |
| balanced-match | 4.0.4 | MIT |
| semver | 7.8.5 | ISC |
| vscode-languageserver-protocol | 3.18.2 | MIT |
| vscode-jsonrpc | 9.0.1 | MIT |
| vscode-languageserver-types | 3.18.0 | MIT |
| vscode-languageserver-textdocument | 1.0.13 | MIT |

VSIX archive には extension 自身を含め 10 license files が入ることを確認した。

## 検証環境

```text
Windows 11 x64
Node 22.21.1
npm 11.7.0
VS Code 1.128.0 (test-electron archive; installed editor も 1.128.0)
.NET SDK 10.0.301
```

## 検証結果

### npm clean install / build / lint / unit

```powershell
cd dotnet-port\vscode-nemerle
npm ci
npm run check-types
npm run package
```

結果:

- `npm ci`: 456 packages、audit 0 vulnerabilities。dev test tooling の transitive package に
  deprecated notice 3 件は出るが、audit finding はない。
- production/test TypeScript typecheck: warning 0 / error 0。
- ESLint: warning 0 / error 0。
- unit/headless tests: 8/8 PASS。
  - contribution/configuration structure
  - invalid/missing server path と command/argument separation
  - real Oniguruma で keyword/comment/string/char/number/type/quotation/splice scope を tokenize
- VSIX: `vscode-nemerle-0.1.0.vsix`, 394 files, 681.47 KB。
  `vscode-languageclient` と runtime dependencies を含み、`server/` は含まない。
- `vsce` は 184 JavaScript files のため bundling 推奨 warning を 1 件出す。今回は dependency
  license files をそのまま同梱する plain-tsc layout を優先した。機能/安全性 warning ではない。

### VS Code Extension Host

```powershell
npm run test:integration
```

固定 VS Code 1.128.0 で最終結果:

```text
trusted:   3 passing
untrusted: 1 passing
```

trusted test:

1. `.n` → `nemerle`、line/block comment。
2. bracket auto-close、selection auto-surround、brace indentation。
3. `Broken.n` Error diagnostic → unsaved fix → diagnostics empty。
4. Restart 後 PID が変わり、旧 PID への existence check が false。
5. Show Output command が成功。

untrusted test:

1. `workspace.isTrusted == false`。
2. `.n` language support は有効。
3. 有効な server path を user setting に持っていても PID は undefined。
4. Restart command を直接 invoke しても PID は undefined。

`@vscode/test-electron 3.0.0` は runner 内で Workspace Trust を常に disable するため、untrusted test
だけは同じ downloaded Code.exe を trust-disable flag 無し、独立 user-data/extensions directory で
直接起動した。test source は `test/runUntrusted.ts`。

VS Code の `LogOutputChannel` file には start/stop/restart が一回ずつ順序どおり残り、untrusted
channel には `Workspace is not trusted; ... was not started.` が記録された。current server の正常系は
任意 stderr message を出さないため stderr text 自体は smoke test に現れなかったが、採用した
languageclient 10.1.0 executable transport の stderr → configured LogOutputChannel forwarding path を
package sourceで確認し、同じ channel の file 出力と Show Output を実プロセスで確認した。
diagnostics test が正常に LSP frames を往復したため、server stdout に log が混入していないことも
同時に確認できた。

Extension Host は実行中の同版 VS Code と共存したため `Error mutex already exists` を main log に
出したが、独立 Extension Host は起動し全 tests は exit 0。機能結果への影響はなかった。

### VSIX install smoke

生成 VSIX を workspace 内の隔離 extensions/user-data directory へ install した。

```text
Extension 'vscode-nemerle-0.1.0.vsix' was successfully installed.
nemerle-project.vscode-nemerle@0.1.0
```

archive inspection:

```text
entries=394
vscode-languageclient present=True
bundled server present=False
license files=10
```

### 既存 .NET LSP 回帰

```powershell
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll
```

結果:

```text
build: 0 warnings, 0 errors
PASS initialize -> didOpen(error) -> didChange(clear) -> didClose(clear) -> shutdown/exit
```

### vulnerability audit

```powershell
npm audit
npm audit --omit=dev
dotnet list dotnet-port\LspServer\Nemerle.LanguageServer.csproj package --vulnerable --include-transitive
```

結果:

```text
npm full tree:       0 vulnerabilities
npm production tree: 0 vulnerabilities
.NET LSP: vulnerable package なし
```

Mocha の transitive `diff` / `serialize-javascript` は最初の audit で advisory が出たため、official
VS Code test tooling と同じ patched versions `diff 8.0.4` / `serialize-javascript 7.0.6` を
package overrides と lockfile で固定した。

## 再現可能な開発確認

1. LSP server を Release build する。
2. extension directory で `npm ci`、`npm run build`。
3. VS Code Extension Development Host を開き、trusted workspace の
   `nemerle.server.path` を server DLL の absolute path にする。
4. `test-workspace/Broken.n` を開き、Problems/squiggle を確認する。
5. `"wrong"` を未保存のまま `1` に直し、diagnostic が消えることを確認する。
6. Restart、Show Output、trace setting を確認する。
7. Restricted Mode の別 window では highlighting/editing support が残り、server が起動しないことを
   Output と process list で確認する。

visual color と bracket highlight は stable VS Code API から画面 pixel を assert できないため、
grammar は real TextMate tokenizer、bracket/comment/indent は language configuration と editor command、
squiggle/Problems は `vscode.languages.getDiagnostics` で代替自動検証した。

## 既知の制約 / 次 WP への境界

- development VSIX。server と .NET runtime は bundled されない。bundled packaging は WP-L4。
- server は open-document loose-file mode のまま。`.nproj`、ProjectReference、PackageReference、
  macro reference、project defines は認識しない。WP-L2/WP-L3 の範囲。
- multi-root/project selection、completion、hover、definition、formatting は未実装。
- TextMate grammar は静的な最小 grammar。任意 macro が追加する syntax keyword は動的に色分けしない。
- plain-tsc VSIX は小さいが file 数が 394。将来 bundle する場合は third-party notice の維持も同時に
  packaging rule へ入れる。
