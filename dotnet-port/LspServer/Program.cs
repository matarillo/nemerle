using Microsoft.Extensions.DependencyInjection;
using Nemerle.LanguageServer.Engine;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nemerle.LanguageServer;

internal static class Program
{
    public static async Task Main()
    {
        await using var project = new NemerleProject(Console.Error);
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .WithServerInfo(new ServerInfo
            {
                Name = "nemerle-language-server",
                Version = "0.1.0",
            })
            .WithServices(services => services.AddSingleton(project))
            .WithHandler<NemerleTextDocumentSyncHandler>()).ConfigureAwait(false);

        await server.WaitForExit.ConfigureAwait(false);
    }
}
