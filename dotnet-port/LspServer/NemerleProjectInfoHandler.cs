using Nemerle.ProjectInfo;
using OmniSharp.Extensions.JsonRpc;
using MediatR;

namespace Nemerle.LanguageServer;

[Method("nemerle/projectInfo/load")]
public sealed record ProjectInfoLoadRequest(
    string ProjectPath,
    string? Configuration,
    string? Platform,
    string? TargetFramework,
    string? DotNetExecutable,
    bool ForceReload) : IRequest<ProjectInfoLoadResult>;

public sealed record ProjectInfoLoadResult(
    string State,
    string? ProjectPath,
    string? ProjectDirectory,
    string? Configuration,
    string? Platform,
    string? TargetFramework,
    int SourceCount,
    int AssemblyReferenceCount,
    int MacroReferenceCount,
    IReadOnlyList<string> DefineConstants,
    IReadOnlyList<string> Warnings,
    string? ErrorKind,
    string? ErrorMessage,
    string? ErrorDetails,
    bool AppliedToEngine);

[Serial]
internal sealed class NemerleProjectInfoHandler
    : IJsonRpcRequestHandler<ProjectInfoLoadRequest, ProjectInfoLoadResult>
{
    private readonly ProjectInfoProvider _provider;

    public NemerleProjectInfoHandler(ProjectInfoProvider provider)
    {
        _provider = provider;
    }

    public async Task<ProjectInfoLoadResult> Handle(
        ProjectInfoLoadRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var key = ProjectQueryKey.Create(
                request.DotNetExecutable ?? "dotnet",
                request.ProjectPath,
                request.Configuration,
                request.Platform,
                request.TargetFramework);
            var snapshot = await _provider.GetSnapshotAsync(
                key,
                request.ForceReload,
                cancellationToken).ConfigureAwait(false);
            return new ProjectInfoLoadResult(
                "loaded",
                snapshot.ProjectPath,
                snapshot.ProjectDirectory,
                snapshot.Configuration,
                snapshot.Platform,
                snapshot.TargetFramework,
                snapshot.SourceFiles.Count,
                snapshot.AssemblyReferences.Count,
                snapshot.MacroReferences.Count,
                snapshot.DefineConstants,
                snapshot.Warnings,
                null,
                null,
                null,
                AppliedToEngine: false);
        }
        catch (ProjectQueryException ex)
        {
            var details = ex.StandardError.Length > 0 ? ex.StandardError : ex.StandardOutput;
            Console.Error.WriteLine($"Project query {ex.Kind}: {ex.Message}");
            if (details.Length > 0)
                Console.Error.WriteLine(details);
            return Error(ex.Kind.ToString(), ex.Message, details);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Project query configuration error: {ex.Message}");
            return Error("Configuration", ex.Message, null);
        }
    }

    private static ProjectInfoLoadResult Error(string kind, string message, string? details) =>
        new(
            "error",
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            kind,
            message,
            details,
            AppliedToEngine: false);
}
