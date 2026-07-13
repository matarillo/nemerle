using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Nemerle.LanguageServer.IntegrationTest;

internal static class Program
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> Main(string[] args)
    {
        var serverDll = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(
                "dotnet-port", "LspServer", "bin", "Release", "net10.0",
                "Nemerle.LanguageServer.dll"));
        var helloProject = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath(Path.Combine(
                "dotnet-port", "samples", "HelloCore", "HelloCore.nproj"));

        if (!File.Exists(serverDll))
        {
            Console.Error.WriteLine($"Server assembly not found: {serverDll}");
            return 2;
        }

        var testDirectory = Path.Combine(Path.GetTempPath(), "nemerle-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var sourcePath = Path.Combine(testDirectory, "Broken.n");
        var sourceUri = new Uri(sourcePath).AbsoluteUri;

        const string brokenSource = """
            module Broken
            {
              Main() : void
              {
                def value : int = "wrong";
              }
            }
            """;
        const string fixedSource = """
            module Broken
            {
              Main() : void
              {
                def value : int = 1;
                System.Console.WriteLine(value);
              }
            }
            """;

        // The disk file deliberately remains broken after didChange.  An empty
        // version-2 diagnostic set therefore proves that the engine compiled
        // the in-memory LSP buffer rather than rereading the file.
        await File.WriteAllTextAsync(sourcePath, brokenSource).ConfigureAwait(false);

        using var process = StartServer(serverDll);
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await SendAsync(process.StandardInput.BaseStream, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    processId = Environment.ProcessId,
                    rootUri = new Uri(testDirectory).AbsoluteUri,
                    capabilities = new { },
                    clientInfo = new { name = "nemerle-lsp-integration-test", version = "1.0" },
                },
            }).ConfigureAwait(false);
            _ = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                static message => HasId(message, 1) && message.TryGetProperty("result", out _),
                MessageTimeout).ConfigureAwait(false);

            await SendNotificationAsync(process, "initialized", new { }).ConfigureAwait(false);
            await SendAsync(process.StandardInput.BaseStream, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "nemerle/projectInfo/load",
                @params = new
                {
                    projectPath = helloProject,
                    configuration = "Debug",
                    platform = "AnyCPU",
                    targetFramework = "net10.0",
                    dotNetExecutable = "dotnet",
                    forceReload = true,
                },
            }).ConfigureAwait(false);
            var projectInfo = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                static message => HasId(message, 2),
                MessageTimeout).ConfigureAwait(false);
            if (projectInfo.TryGetProperty("error", out var projectError))
                throw new InvalidDataException("Project-info custom request failed: " + projectError);
            var projectResult = projectInfo.GetProperty("result");
            if (projectResult.GetProperty("state").GetString() != "loaded" ||
                projectResult.GetProperty("sourceCount").GetInt32() != 1 ||
                projectResult.GetProperty("appliedToEngine").GetBoolean())
            {
                throw new InvalidDataException("Project-info custom request returned an unexpected WP-L2 result.");
            }

            await SendAsync(process.StandardInput.BaseStream, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "nemerle/projectInfo/load",
                @params = new
                {
                    projectPath = Path.Combine(testDirectory, "Missing.nproj"),
                    configuration = "Debug",
                    platform = "AnyCPU",
                    targetFramework = "",
                    dotNetExecutable = "dotnet",
                    forceReload = true,
                },
            }).ConfigureAwait(false);
            var failedProjectInfo = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                static message => HasId(message, 3),
                MessageTimeout).ConfigureAwait(false);
            if (failedProjectInfo.TryGetProperty("error", out var failedProjectError))
                throw new InvalidDataException("Expected a typed project-info result, got JSON-RPC error: " + failedProjectError);
            var failedProjectResult = failedProjectInfo.GetProperty("result");
            if (failedProjectResult.GetProperty("state").GetString() != "error" ||
                failedProjectResult.GetProperty("errorKind").GetString() != "NonZeroExit" ||
                failedProjectResult.GetProperty("appliedToEngine").GetBoolean())
            {
                throw new InvalidDataException("Invalid project did not return the expected recoverable WP-L2 error result.");
            }

            await SendNotificationAsync(process, "textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri = sourceUri,
                    languageId = "nemerle",
                    version = 1,
                    text = brokenSource,
                },
            }).ConfigureAwait(false);

            var errorPublish = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                message => IsDiagnostics(message, sourceUri, 1, requireError: true),
                MessageTimeout).ConfigureAwait(false);
            var error = errorPublish.GetProperty("params").GetProperty("diagnostics")[0];
            var start = error.GetProperty("range").GetProperty("start");
            if (start.GetProperty("line").GetInt32() < 0 ||
                start.GetProperty("character").GetInt32() < 0)
                throw new InvalidDataException("Diagnostic range was not converted to zero-based LSP coordinates.");

            await SendNotificationAsync(process, "textDocument/didChange", new
            {
                textDocument = new { uri = sourceUri, version = 2 },
                contentChanges = new[] { new { text = fixedSource } },
            }).ConfigureAwait(false);
            _ = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                message => IsDiagnostics(message, sourceUri, 2, requireError: false),
                MessageTimeout).ConfigureAwait(false);

            await SendNotificationAsync(process, "textDocument/didClose", new
            {
                textDocument = new { uri = sourceUri },
            }).ConfigureAwait(false);
            _ = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                message => IsClosedDiagnostics(message, sourceUri),
                MessageTimeout).ConfigureAwait(false);

            await SendAsync(process.StandardInput.BaseStream, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "shutdown",
                @params = (object?)null,
            }).ConfigureAwait(false);
            _ = await WaitForMessageAsync(
                process.StandardOutput.BaseStream,
                static message => HasId(message, 4),
                MessageTimeout).ConfigureAwait(false);
            await SendNotificationAsync(process, "exit", null).ConfigureAwait(false);

            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Server exited with code {process.ExitCode}.");

            Console.WriteLine("PASS initialize -> projectInfo(snapshot-only) -> projectInfo(recoverable failure) -> didOpen(error) -> didChange(clear) -> didClose(clear) -> shutdown/exit");
            return 0;
        }
        catch (Exception ex)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            var stderr = await stderrTask.ConfigureAwait(false);
            Console.Error.WriteLine(ex);
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.Error.WriteLine("--- server stderr ---\n" + stderr);
            return 1;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try { Directory.Delete(testDirectory, recursive: true); }
            catch { }
        }
    }

    private static Process StartServer(string serverDll)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(serverDll);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the LSP server.");
    }

    private static Task SendNotificationAsync(Process process, string method, object? parameters) =>
        SendAsync(process.StandardInput.BaseStream, new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        });

    private static async Task SendAsync(Stream stream, object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<JsonElement> WaitForMessageAsync(
        Stream stream,
        Func<JsonElement, bool> predicate,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            using var message = await ReadMessageAsync(stream, cancellation.Token).ConfigureAwait(false);
            if (predicate(message.RootElement))
                return message.RootElement.Clone();
        }
    }

    private static async Task<JsonDocument> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        var header = new List<byte>();
        while (header.Count < 4 ||
               header[^4] != '\r' || header[^3] != '\n' ||
               header[^2] != '\r' || header[^1] != '\n')
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("LSP server closed stdout while a message was expected.");
            header.Add(buffer[0]);
        }

        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var contentLengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var contentLength = int.Parse(contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..].Trim());
        var body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private static bool HasId(JsonElement message, int id) =>
        message.TryGetProperty("id", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.GetInt32() == id;

    private static bool IsDiagnostics(JsonElement message, string uri, int version, bool requireError)
    {
        if (!IsPublishDiagnosticsFor(message, uri, out var parameters) ||
            !parameters.TryGetProperty("version", out var versionElement) ||
            versionElement.GetInt32() != version)
            return false;

        var diagnostics = parameters.GetProperty("diagnostics");
        return requireError
            ? diagnostics.EnumerateArray().Any(diagnostic =>
                diagnostic.TryGetProperty("severity", out var severity) && severity.GetInt32() == 1)
            : diagnostics.GetArrayLength() == 0;
    }

    private static bool IsClosedDiagnostics(JsonElement message, string uri)
    {
        if (!IsPublishDiagnosticsFor(message, uri, out var parameters) ||
            parameters.GetProperty("diagnostics").GetArrayLength() != 0)
            return false;

        return !parameters.TryGetProperty("version", out var version) ||
               version.ValueKind == JsonValueKind.Null;
    }

    private static bool IsPublishDiagnosticsFor(
        JsonElement message,
        string uri,
        out JsonElement parameters)
    {
        parameters = default;
        return message.TryGetProperty("method", out var method) &&
               method.GetString() == "textDocument/publishDiagnostics" &&
               message.TryGetProperty("params", out parameters) &&
               AreEquivalentDocumentUris(parameters.GetProperty("uri").GetString(), uri);
    }

    private static bool AreEquivalentDocumentUris(string? left, string right)
    {
        if (left is null || !Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
            return false;

        if (leftUri.IsFile && rightUri.IsFile)
            return string.Equals(leftUri.LocalPath, rightUri.LocalPath, StringComparison.OrdinalIgnoreCase);

        return leftUri.Equals(rightUri);
    }
}
