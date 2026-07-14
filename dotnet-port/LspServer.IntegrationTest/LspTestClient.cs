using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Nemerle.LanguageServer.IntegrationTest;

/// <summary>
/// Minimal raw stdio LSP client for the integration scenarios.  Every received
/// message is kept in an in-order history so scenarios can wait for a specific
/// notification after an explicit mark without losing interleaved publishes.
/// </summary>
internal sealed class LspTestClient : IAsyncDisposable
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(120);

    private readonly Process _process;
    private readonly Task<string> _stderrTask;
    private readonly List<JsonElement> _history = [];
    private int _nextRequestId;

    private LspTestClient(Process process)
    {
        _process = process;
        _stderrTask = process.StandardError.ReadToEndAsync();
    }

    /// <summary>The server's <c>initialize</c> result (capabilities etc.).</summary>
    public JsonElement InitializeResult { get; private set; }

    public static async Task<LspTestClient> StartAsync(
        string serverDll,
        string rootDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
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
        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LSP server.");
        var client = new LspTestClient(process);

        var initialize = await client.RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = new Uri(rootDirectory).AbsoluteUri,
            capabilities = new
            {
                textDocument = new
                {
                    // Advertise markdown so the hover handler exercises the
                    // fenced-code-block path (WP-M2).
                    hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                    // Advertise completion with resolve + documentation so the
                    // completion handler exercises the deferred-doc path (WP-M3).
                    completion = new
                    {
                        contextSupport = true,
                        completionItem = new { documentationFormat = new[] { "plaintext", "markdown" } },
                    },
                    // Advertise definition/references so those handlers register
                    // (WP-M4).
                    definition = new { linkSupport = false },
                    references = new { },
                },
            },
            clientInfo = new { name = "nemerle-lsp-integration-test", version = "2.0" },
        }).ConfigureAwait(false);
        if (!initialize.TryGetProperty("result", out var initializeResult))
            throw new InvalidDataException("initialize did not return a result: " + initialize);
        client.InitializeResult = initializeResult.Clone();
        await client.NotifyAsync("initialized", new { }).ConfigureAwait(false);
        return client;
    }

    /// <summary>Marks the current end of the message history.</summary>
    public int Mark()
    {
        return _history.Count;
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters)
    {
        var id = ++_nextRequestId;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }).ConfigureAwait(false);
        return await WaitForAsync(
            message => HasId(message, id),
            Mark(),
            $"response to {method} (id {id})").ConfigureAwait(false);
    }

    public Task NotifyAsync(string method, object? parameters) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters });

    public async Task<JsonElement> WaitForAsync(
        Func<JsonElement, bool> predicate,
        int fromMark,
        string description)
    {
        for (var index = fromMark; index < _history.Count; index++)
        {
            if (predicate(_history[index]))
                return _history[index];
        }

        using var cancellation = new CancellationTokenSource(MessageTimeout);
        while (true)
        {
            JsonElement message;
            try
            {
                message = await ReadMessageAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            if (predicate(message))
                return message;
        }
    }

    /// <summary>
    /// Reads every message that arrives within <paramref name="duration"/> and
    /// fails if any satisfies <paramref name="forbidden"/>.  Also checks the
    /// history from <paramref name="fromMark"/> first.
    /// </summary>
    public async Task AssertQuietAsync(
        Func<JsonElement, bool> forbidden,
        int fromMark,
        TimeSpan duration,
        string description)
    {
        for (var index = fromMark; index < _history.Count; index++)
        {
            if (forbidden(_history[index]))
                throw new InvalidDataException($"Forbidden message ({description}): {_history[index]}");
        }

        using var cancellation = new CancellationTokenSource(duration);
        while (true)
        {
            JsonElement message;
            try
            {
                message = await ReadMessageAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (forbidden(message))
                throw new InvalidDataException($"Forbidden message ({description}): {message}");
        }
    }

    public async Task ShutdownAsync()
    {
        _ = await RequestAsync("shutdown", null).ConfigureAwait(false);
        await NotifyAsync("exit", null).ConfigureAwait(false);
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"Server exited with code {_process.ExitCode}.");
    }

    public async Task<string> DumpServerStderrAsync()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        return await _stderrTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        try { await _stderrTask.ConfigureAwait(false); }
        catch { }
        _process.Dispose();
    }

    private async Task SendAsync(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var stream = _process.StandardInput.BaseStream;
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadMessageAsync(CancellationToken token)
    {
        var stream = _process.StandardOutput.BaseStream;
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
        using var document = JsonDocument.Parse(body);
        var element = document.RootElement.Clone();
        _history.Add(element);
        return element;
    }

    public static bool HasId(JsonElement message, int id) =>
        message.TryGetProperty("id", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.GetInt32() == id;

    /// <summary>
    /// Every <c>window/logMessage</c> notification received so far (from
    /// <paramref name="fromMark"/>), as (type, message).  LSP MessageType:
    /// 1=Error, 2=Warning, 3=Info, 4=Log.
    /// </summary>
    public IReadOnlyList<(int Type, string Message)> LogMessages(int fromMark = 0)
    {
        var result = new List<(int, string)>();
        for (var index = fromMark; index < _history.Count; index++)
        {
            var message = _history[index];
            if (message.TryGetProperty("method", out var method) &&
                method.GetString() == "window/logMessage" &&
                message.TryGetProperty("params", out var p))
            {
                var type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32()
                    : 0;
                var text = p.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                result.Add((type, text));
            }
        }

        return result;
    }

    public static bool IsLogMessageContaining(JsonElement message, string needle, out int type)
    {
        type = 0;
        if (!message.TryGetProperty("method", out var method) ||
            method.GetString() != "window/logMessage" ||
            !message.TryGetProperty("params", out var p))
            return false;
        type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        return p.TryGetProperty("message", out var m) &&
               (m.GetString() ?? "").Contains(needle, StringComparison.Ordinal);
    }
}
