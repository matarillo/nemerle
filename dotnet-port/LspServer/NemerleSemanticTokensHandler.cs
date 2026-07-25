using System.Diagnostics;
using Nemerle.Completion2;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/semanticTokens/full</c> handler (WP-O5a).  Runs the IDE
/// engine's colorizer (<c>ScanLexer</c>) over the whole open document through
/// <see cref="NemerleProject.GetSemanticTokens"/> and pushes the result into the
/// LSP legend built from <see cref="SemanticTokenMapping"/>.
///
/// <para>The point of the feature: keywords that a syntax macro added to the
/// file's environment are reported as <c>macro</c> instead of <c>keyword</c>, so
/// the editor colors syntax that only exists after the compiler loaded the macros
/// named by the file's <c>using</c>s — something the TextMate grammar cannot do.
/// Spans the colorizer has nothing to say about emit no token at all, which lets
/// the grammar keep coloring them.</para>
///
/// <para>Delta and range requests are not advertised: the colorizer is a
/// whole-document pass, so a delta would be computed from a fresh document and
/// carry no benefit.</para>
/// </summary>
internal sealed class NemerleSemanticTokensHandler : SemanticTokensHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private static readonly SemanticTokensLegend TokensLegend = new()
    {
        TokenTypes = Array.ConvertAll(SemanticTokenMapping.TokenTypes, name => new SemanticTokenType(name)),
        TokenModifiers = Array.ConvertAll(
            SemanticTokenMapping.TokenModifiers,
            name => new SemanticTokenModifier(name)),
    };

    /// <summary>
    /// Delays of the extra refresh requests sent while the client still has not
    /// asked for tokens once (see <see cref="RequestRefresh"/>).
    /// </summary>
    private static readonly TimeSpan[] StartupRefreshRetries =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8),
    ];

    /// <summary>How long a request waits for the engine's analysis, and how often it looks.</summary>
    private static readonly TimeSpan AnalysisWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AnalysisPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ILanguageServerFacade _server;
    private readonly NemerleProject _project;
    private readonly ServerLog _log;
    private bool _refreshSupported;
    private int _tokensRequested;
    private int _retryRunning;

    public NemerleSemanticTokensHandler(ILanguageServerFacade server, NemerleProject project, ServerLog log)
    {
        _server = server;
        _project = project;
        _log = log;
        WarnOnColorMirrorDrift(log);
        _project.TypesTreeRebuilt += RequestRefresh;
    }

    /// <summary>
    /// A document's keyword set comes from its <c>GlobalEnv</c>, which only exists
    /// once the engine built the types tree.  The first request after
    /// <c>didOpen</c> usually races that build and gets core-keyword coloring
    /// (correct, but without the macro keywords), and a client re-requests only on
    /// its own triggers - so ask it to (LSP <c>workspace/semanticTokens/refresh</c>).
    ///
    /// <para>On a window reload there is a second, worse race: the editor restores
    /// its documents while this server is still starting, so its first (and only)
    /// attempt to fetch tokens finds no provider, and the refresh that follows the
    /// startup build can arrive before the client attached the restored document -
    /// where it is dropped rather than queued.  The result is a first paint with
    /// grammar coloring only, frozen until the user edits the file or switches
    /// color theme (observed on WSL, 53-wp-o5-log.md).  Retry the refresh a few
    /// times until the client asks for tokens once; each request costs the server
    /// well under a millisecond, and the retries stop as soon as one arrives.</para>
    /// </summary>
    private void RequestRefresh()
    {
        if (!_refreshSupported)
            return;

        SendRefresh("types tree rebuilt");
        StartRefreshRetries();
    }

    /// <summary>
    /// Arms the retry sequence unless it is already running.  Called both after a
    /// types-tree build and after answering a request with "nothing yet", because
    /// either one can be the event that races the client.
    /// </summary>
    private void StartRefreshRetries()
    {
        if (!_refreshSupported || Volatile.Read(ref _tokensRequested) != 0)
            return;
        if (Interlocked.Exchange(ref _retryRunning, 1) != 0)
            return;

        _ = RetryUntilTheClientHasTokensAsync();
    }

    private async Task RetryUntilTheClientHasTokensAsync()
    {
        try
        {
            for (var attempt = 0; attempt < StartupRefreshRetries.Length; attempt++)
            {
                await Task.Delay(StartupRefreshRetries[attempt]).ConfigureAwait(false);
                if (Volatile.Read(ref _tokensRequested) != 0)
                    return;

                SendRefresh($"retry {attempt + 1}/{StartupRefreshRetries.Length}, the client has no tokens yet");
            }
        }
        finally
        {
            Volatile.Write(ref _retryRunning, 0);
        }
    }

    private void SendRefresh(string reason)
    {
        try
        {
            _server.Workspace.SendSemanticTokensRefresh(new SemanticTokensRefreshParams());
            _log.Log($"nemerle semantic tokens refresh requested ({reason})");
        }
        catch (Exception ex)
        {
            _log.Warning($"nemerle semantic tokens refresh request failed: {ex.Message}");
        }
    }

    protected override async Task Tokenize(
        SemanticTokensBuilder builder,
        ITextDocumentIdentifierParams identifier,
        CancellationToken token)
    {
        var uri = identifier.TextDocument.Uri.ToString();

        var stopwatch = Stopwatch.StartNew();
        var result = _project.GetSemanticTokens(uri);
        if (result is not { FromTypesTree: true })
            result = await WaitForTheAnalysisAsync(uri, result, token).ConfigureAwait(false);
        if (result is null)
        {
            _log.Log($"nemerle semantic tokens unavailable at {uri}");
            StartRefreshRetries();
            return;
        }

        // A complete answer: the client has tokens, so the refresh retries can
        // stop.  An incomplete one (the wait timed out) still gets sent - core
        // keywords beat no coloring - but the nudging continues.
        if (result.FromTypesTree)
            Volatile.Write(ref _tokensRequested, 1);
        else
            StartRefreshRetries();

        var tokens = result.Tokens;
        foreach (var semanticToken in tokens)
        {
            token.ThrowIfCancellationRequested();
            builder.Push(
                semanticToken.Line,
                semanticToken.Character,
                semanticToken.Length,
                SemanticTokenMapping.TypeName(semanticToken.Type)!,
                SemanticTokenMapping.ModifierNames(semanticToken.Modifiers));
        }

        _log.Log(
            $"nemerle semantic tokens computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms " +
            $"({tokens.Count} tokens{(result.FromTypesTree ? "" : ", core environment only")}) at {uri}");
    }

    /// <summary>
    /// Waits (bounded) for the engine to finish analyzing the document, then
    /// colorizes it.  Returns <paramref name="incomplete"/> if the wait ran out.
    ///
    /// <para>This is what makes the first paint correct.  The client asks exactly
    /// once at startup - right after it registers the provider, which is before
    /// the engine initialized and built its types tree - and it caches whatever it
    /// gets.  Measured on WSL: neither an empty nor a core-environment-only answer
    /// is ever re-queried, not on <c>workspace/semanticTokens/refresh</c> (three
    /// retries, no reaction) and not on anything short of an edit or a color-theme
    /// switch.  So the one request that does arrive has to be answered with the
    /// real thing, even if that means holding it for a moment; the wait is a
    /// fraction of a second in practice (the engine's first build), bounded, and
    /// cancelled with the request.</para>
    /// </summary>
    private async Task<EngineSemanticTokens?> WaitForTheAnalysisAsync(
        string uri,
        EngineSemanticTokens? incomplete,
        CancellationToken token)
    {
        if (!_project.IsDocumentOpen(uri))
            return incomplete;

        _log.Log($"nemerle semantic tokens waiting for the analysis at {uri}");
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < AnalysisWait)
        {
            try
            {
                await Task.Delay(AnalysisPollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return incomplete;
            }

            var result = _project.GetSemanticTokens(uri);
            if (result is { FromTypesTree: true })
                return result;

            incomplete ??= result;
        }

        _log.Warning(
            $"nemerle semantic tokens gave up waiting for the analysis after {waited.Elapsed.TotalSeconds:F0} s at {uri}");
        return incomplete;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SemanticTokensDocument(TokensLegend));

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities)
    {
        var workspaceSemanticTokens = clientCapabilities?.Workspace?.SemanticTokens;
        _refreshSupported = workspaceSemanticTokens is { IsSupported: true } supported &&
                            supported.Value.RefreshSupport;

        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = Selector,
            Legend = TokensLegend,
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = false,
        };
    }

    /// <summary>
    /// <see cref="NemerleScanTokenColor"/> mirrors the engine's
    /// <c>ScanTokenColor</c> so the classification can live in the engine-free
    /// ProjectInfo assembly (the same trade-off <see cref="CompletionMapping"/>
    /// makes for the glyph enum).  The engine is a shared source that also serves
    /// the legacy VS integration, so check the two still agree and say so once if
    /// they drifted: an unmirrored color silently loses its coloring otherwise.
    /// </summary>
    private static void WarnOnColorMirrorDrift(ServerLog log)
    {
        foreach (var name in Enum.GetNames<ScanTokenColor>())
        {
            var engineValue = (int)Enum.Parse<ScanTokenColor>(name);
            if (!Enum.TryParse<NemerleScanTokenColor>(name, out var mirrored) ||
                (int)mirrored != engineValue)
            {
                log.Warning(
                    $"nemerle semantic tokens: engine color '{name}' ({engineValue}) is not mirrored by " +
                    "NemerleScanTokenColor; tokens with that color will not be colored.");
            }
        }
    }
}
