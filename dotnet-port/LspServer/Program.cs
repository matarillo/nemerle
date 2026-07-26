using Microsoft.Extensions.DependencyInjection;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nemerle.LanguageServer;

internal static class Program
{
    /// <summary>
    /// True unless the client asked for no tracing.  Compared as text because
    /// OmniSharp models the trace value as its own type, whose equality is not
    /// necessarily value equality.
    /// </summary>
    private static bool IsTracing(object? trace) =>
        trace is not null && !string.Equals(trace.ToString(), "off", StringComparison.OrdinalIgnoreCase);

    public static async Task Main()
    {
        // Trace goes to window/logMessage once the facade is attached below; until
        // then it is buffered.  Only a pre-initialize fatal (before the facade can
        // exist) falls back to stderr (ServerLog.Fatal).
        var log = new ServerLog();
        var serverOptions = ServerOptions.FromEnvironment();
        await using var project = new NemerleProject(log, serverOptions);
        var workspace = new WorkspaceManager(project, log);
        await using var projectInfo = new ProjectInfoProvider();
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .WithServerInfo(new ServerInfo
            {
                Name = "nemerle-language-server",
                Version = "0.10.0",
            })
            .WithServices(services =>
            {
                services.AddSingleton(project);
                services.AddSingleton(workspace);
                services.AddSingleton(projectInfo);
                services.AddSingleton(log);
                services.AddSingleton(serverOptions);
            })
            // Per-request chatter (hover/signature help/highlight/code actions,
            // one line per caret move) is only sent when the client asked for
            // tracing - see ServerLog.Trace.  The client's choice arrives with
            // initialize and can change later through $/setTrace.
            .OnInitialize((_, request, _) =>
            {
                log.SetTraceEnabled(IsTracing(request.Trace));
                return Task.CompletedTask;
            })
            .OnNotification<SetTraceParams>("$/setTrace", parameters =>
            {
                log.SetTraceEnabled(IsTracing(parameters.Value));
            })
            .WithHandler<NemerleTextDocumentSyncHandler>()
            .WithHandler<NemerleProjectInfoHandler>()
            .WithHandler<NemerleHoverHandler>()
            .WithHandler<NemerleCompletionHandler>()
            .WithHandler<NemerleDefinitionHandler>()
            .WithHandler<NemerleReferencesHandler>()
            .WithHandler<NemerleSignatureHelpHandler>()
            .WithHandler<NemerleDocumentHighlightHandler>()
            .WithHandler<NemerleRenameHandler>()
            .WithHandler<NemerlePrepareRenameHandler>()
            .WithHandler<NemerleCodeActionHandler>()
            .WithHandler<NemerleFormattingHandler>()
            .WithHandler<NemerleSemanticTokensHandler>()).ConfigureAwait(false);

        log.Attach(server);
        await server.WaitForExit.ConfigureAwait(false);
    }
}
