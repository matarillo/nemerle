using System.Diagnostics;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/signatureHelp</c> handler (WP-P1).  Delegates to the shared
/// engine workspace (<see cref="NemerleProject.GetSignatureHelpAsync"/>, which
/// runs both the tip computation and its flattening on the engine's worker
/// thread) and converts the engine-independent
/// <see cref="NemerleMethodTip"/> with the pure
/// <see cref="SignatureHelpMapping"/>.
///
/// <para>The extension needs no manifest change: signature help is a negotiated
/// server capability that vscode-languageclient wires up on its own, and the
/// trigger characters below are registered by the server.</para>
/// </summary>
internal sealed class NemerleSignatureHelpHandler : SignatureHelpHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    /// <summary>
    /// Whether the client understands <c>[start, end)</c> parameter labels.
    /// Negotiated once at registration (LSP 3.17
    /// §textDocument/signatureHelp, <c>labelOffsetSupport</c>).
    /// </summary>
    private bool _labelOffsets;

    public NemerleSignatureHelpHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override async Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var stopwatch = Stopwatch.StartNew();
        var tip = await _project
            .GetSignatureHelpAsync(uri, position.Line, position.Character, token)
            .ConfigureAwait(false);

        var help = SignatureHelpMapping.ToSignatureHelp(tip);
        if (help is null)
        {
            // A silent null is indistinguishable from "the client never asked"
            // in the Output pane, which is the only view a user has of this
            // feature (WP-O5a §設計-3).
            _log.Trace(
                $"nemerle signature help empty at {uri} {position.Line}:{position.Character} " +
                $"({stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
            return null;
        }

        _log.Trace(
            $"nemerle signature help computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms at " +
            $"{uri} {position.Line}:{position.Character} ({help.Signatures.Count} signature(s), " +
            $"active {help.ActiveSignature}/{help.ActiveParameter})");

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(
                help.Signatures.Select(ToSignatureInformation)),
            ActiveSignature = help.ActiveSignature,
            ActiveParameter = help.ActiveParameter,
        };
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability,
        ClientCapabilities clientCapabilities)
    {
        _labelOffsets =
            capability?.SignatureInformation?.ParameterInformation?.LabelOffsetSupport == true;

        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = Selector,
            // '(' opens the popup on a call; ',' both opens it (when the user
            // typed past the opening paren before asking) and advances the
            // active parameter.
            TriggerCharacters = new Container<string>("(", ","),
            RetriggerCharacters = new Container<string>(","),
        };
    }

    private SignatureInformation ToSignatureInformation(NemerleSignatureLabel signature) =>
        new()
        {
            Label = signature.Label,
            Documentation = ToDocumentation(signature.Documentation),
            Parameters = new Container<ParameterInformation>(
                signature.Parameters.Select(ToParameterInformation)),
        };

    private ParameterInformation ToParameterInformation(NemerleSignatureParameterLabel parameter) =>
        new()
        {
            // Offsets are unambiguous; the string form is only a fallback for a
            // client without labelOffsetSupport, where the client has to locate
            // the substring itself (see SignatureHelpMapping for why that is
            // the weaker form).
            Label = _labelOffsets
                ? new ParameterInformationLabel((parameter.Start, parameter.End))
                : new ParameterInformationLabel(parameter.Label),
            Documentation = ToDocumentation(parameter.Documentation),
        };

    /// <summary>
    /// Always plain text: the documentation is an XmlDoc summary that the engine
    /// already reduced to prose, and rendering it as markdown would reinterpret
    /// characters like <c>_</c> or <c>*</c> in identifiers (same call the
    /// completion resolve path makes).
    /// </summary>
    private static StringOrMarkupContent? ToDocumentation(string? documentation) =>
        string.IsNullOrEmpty(documentation)
            ? null
            : new StringOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.PlainText,
                Value = documentation,
            });
}
