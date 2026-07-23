# 23. WP-K: LSP feasibility step 2 — 最小 .NET 10 LSP サーバー

日付: 2026-07-13
前提: `dotnet-port/docs/21-lsp-feasibility.md` の「推奨する進め方」ステップ5、
および `dotnet-port/docs/22-lsp-step1-log.md` で動作確認した IDE エンジン。

## 結論

達成。`dotnet-port/LspServer` に stdio で動く `net10.0` LSP サーバーを追加した。
`initialize` / `shutdown` / `exit` に加え、full document sync の
`textDocument/didOpen` / `didChange` / `didClose` を受け、既存の
`Nemerle.Completion2.Engine` が生成した構文・型診断を
`textDocument/publishDiagnostics` で返す。

ディスクとは独立した `IIdeSource` 実装を使うため、未保存のエディターバッファを直接検査する。
統合テストでは、ディスク上に型エラーを残したまま `didChange` でメモリー上の内容だけを修正し、
version 2 の診断が空になるところまで確認した。

## プロトコル部品の選定

今回は `OmniSharp.Extensions.LanguageServer 0.19.9` を固定して使う。
プロトコル依存は handler とホストに閉じ込め、Nemerle エンジン側の adapter は
OmniSharp 型に依存させていない。

比較には次の三案を含めた。

1. `OmniSharp.Extensions.LanguageServer 0.19.9`
   - lifecycle、DI、handler registration、capability negotiation、client facade、
     protocol model が一式揃う。
   - hover、completion、definition などを今後増やす際の自前コードが最も少ない。
   - 一方、最終 release は 2023-09-21 と古い。version を固定し、境界を隔離する。
2. `Microsoft.VisualStudio.LanguageServer.Protocol 17.2.8` の型定義 +
   `StreamJsonRpc 2.24.92`
   - `net10.0` で restore / build / run と `DidOpenTextDocumentParams` の JSON 往復を実測済み。
   - DTO は Newtonsoft.Json 属性を持つので、既定の System.Text.Json では wire 名が
     PascalCase になる。組み合わせるなら StreamJsonRpc の `JsonMessageFormatter` が必要。
   - DTO package は 2022-05-31 以降更新されず、inlay hint、inline value、notebook、
     pull diagnostics、call/type hierarchy など後発機能の型が不足する。
   - StreamJsonRpc は transport であり LSP framework ではないため、lifecycle、capability、
     registration、document sync、diagnostics facade を自前所有することになる。
   - 技術的には成立するが、「多くの機能へ拡張する」という今回の前提では採用しない。
3. `Microsoft.CommonLanguageServerProtocol.Framework`
   - Roslyn 由来の prerelease/source package で、単独では LSP DTO を提供しない。
     現段階の最小サーバーで採用するには不安定な面積が大きいため採用しない。

OmniSharp の更新停止が問題になる、必要な新しい LSP 型が欠ける、または独自の小さな
LSP kernel を所有する方針へ変わった時点で、DTO + StreamJsonRpc 案を再評価する。

## 実装構成

- `LspServer/Program.cs`: stdin/stdout host と OmniSharp lifecycle。
- `LspServer/NemerleTextDocumentSyncHandler.cs`: full sync の文書 handler、診断の LSP 変換・通知。
- `LspServer/NemerleProject.cs`: `IIdeProject` adapter、engine の直列操作、応答 queue の常時 pump、
  診断の世代管理と集約。
- `LspServer/InMemoryNemerleSource.cs`: LSP buffer を保持する `IIdeSource`。Nemerle の
  1-origin line/column と LSP の 0-origin UTF-16 position の境界を担当。
- `LspServer/Nemerle.LanguageServer.runtimeconfig.json`: Compiler.Utils と同様に real runtime
  assembly を compile reference にする都合で SDK の implicit framework reference を外しているため、
  framework-dependent な .NET 10 host 設定を明示する。
- `LspServer.IntegrationTest`: package や test framework に依存しない raw LSP client。

`AsyncWorker` の engine callback は通常の `Task` callback ではなく response queue に積まれる。
サーバーは約 10 ms 間隔で `AsyncWorker.DispatchResponses()` を継続実行し、ここから
parse / top-level / method-body diagnostics を受け取る。engine の reload と source mutation は
単一 lock で直列化する。

現 engine の空 relocation queue では method body の増分再検査を確実に起動できないため、
この最小版は open/change/close ごとに `BeginReloadProject()` で open documents 全体を再構築する。
遅いが、syntax と method-body type diagnostics の正しさを優先した。

## ビルド・実行・検証

前提として `dotnet-port/dist/ncc` に stage2 compiler layout があること。

```powershell
dotnet build dotnet-port\LspServer\Nemerle.LanguageServer.csproj -c Release
dotnet exec dotnet-port\LspServer\bin\Release\net10.0\Nemerle.LanguageServer.dll
```

stdio は LSP framing 専用なので、engine log は stderr に出す。

統合テスト:

```powershell
dotnet build dotnet-port\LspServer.IntegrationTest\Nemerle.LanguageServer.IntegrationTest.csproj -c Release
dotnet exec dotnet-port\LspServer.IntegrationTest\bin\Release\net10.0\Nemerle.LanguageServer.IntegrationTest.dll
```

最終結果:

```text
ビルド: 0 warnings, 0 errors
PASS initialize -> didOpen(error) -> didChange(clear) -> didClose(clear) -> shutdown/exit
dotnet list package --vulnerable --include-transitive: 該当なし
```

統合テストが確認する内容:

1. initialize response と full text sync capability。
2. didOpen(version 1) 後に severity=Error の診断が通知される。
3. 診断 range が非負の 0-origin LSP 座標へ変換される。
4. didChange(version 2) の未保存 buffer を再検査し、空の診断が通知される。
5. didClose で version 無しの空診断が通知される。
6. shutdown / exit で正常終了する。

## 現段階の制約と次の拡張点

- project は open documents のみ。workspace scan、`.nproj` 読み込み、design-time reference 解決は未実装。
- assembly/macro references は空で、compiler の core automatic references のみ。
- change は full sync のみ。毎回 project 全体を rebuild するため、大規模 workspace 向けではない。
- diagnostics の code / codeDescription / relatedInformation / tags はまだ付けない。
- VS Code extension や process launcher は未実装。現成果物は editor 非依存の stdio server。
- hover/completion/definition などは、同じ `NemerleProject` と engine を使う独立 handler として追加する。
- 複数 workspace や複数 engine instance を導入する前に、`AsyncWorker` の process-wide 状態を
  明示的に隔離または直列化する必要がある。
