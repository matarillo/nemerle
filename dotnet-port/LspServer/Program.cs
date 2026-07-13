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
        await using var project = new NemerleProject(Console.Error);
        var workspace = new WorkspaceManager(project);
        await using var projectInfo = new ProjectInfoProvider();
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .WithServerInfo(new ServerInfo
            {
                Name = "nemerle-language-server",
                Version = "0.2.0",
            })
            .WithServices(services =>
            {
                services.AddSingleton(project);
                services.AddSingleton(workspace);
                services.AddSingleton(projectInfo);
            })
            .WithHandler<NemerleTextDocumentSyncHandler>()
            .WithHandler<NemerleProjectInfoHandler>()).ConfigureAwait(false);

        await server.WaitForExit.ConfigureAwait(false);
    }
}
