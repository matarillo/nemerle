using Microsoft.Extensions.DependencyInjection;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nemerle.LanguageServer;

internal static class Program
{
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
                Version = "0.7.0",
            })
            .WithServices(services =>
            {
                services.AddSingleton(project);
                services.AddSingleton(workspace);
                services.AddSingleton(projectInfo);
                services.AddSingleton(log);
                services.AddSingleton(serverOptions);
            })
            .WithHandler<NemerleTextDocumentSyncHandler>()
            .WithHandler<NemerleProjectInfoHandler>()
            .WithHandler<NemerleHoverHandler>()
            .WithHandler<NemerleCompletionHandler>()
            .WithHandler<NemerleDefinitionHandler>()
            .WithHandler<NemerleReferencesHandler>()).ConfigureAwait(false);

        log.Attach(server);
        await server.WaitForExit.ConfigureAwait(false);
    }
}
