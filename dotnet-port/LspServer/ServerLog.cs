using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;

namespace Nemerle.LanguageServer;

/// <summary>
/// Routes server trace to the LSP <c>window/logMessage</c> notification
/// (LSP 3.17 §window/logMessage) at the appropriate <see cref="MessageType"/>,
/// so vscode-languageclient renders it at its own level instead of forwarding
/// server stderr to the Output Channel as <c>[error]</c> (WP-M1, §6.6 of
/// 29-devenv2-plan.md).  stdout stays JSON-RPC only; stderr is reserved for
/// pre-initialize fatal failures (see <see cref="Fatal"/>).
///
/// Messages emitted before the server facade exists (engine construction, the
/// first project apply that races startup) are buffered and flushed in order on
/// <see cref="Attach"/>.
/// </summary>
internal sealed class ServerLog
{
    private readonly object _gate = new();
    private readonly List<LogMessageParams> _buffered = [];
    private readonly List<ShowMessageParams> _bufferedShow = [];
    private ILanguageServerFacade? _server;
    private bool _traceEnabled;

    /// <summary>Connects the log to the server facade and replays anything that
    /// was buffered before the facade existed.</summary>
    public void Attach(ILanguageServerFacade server)
    {
        LogMessageParams[] pending;
        ShowMessageParams[] pendingShow;
        lock (_gate)
        {
            _server = server;
            pending = [.. _buffered];
            _buffered.Clear();
            pendingShow = [.. _bufferedShow];
            _bufferedShow.Clear();
        }

        foreach (var message in pending)
            server.Window.LogMessage(message);
        foreach (var message in pendingShow)
            server.Window.ShowMessage(message);
    }

    /// <summary>Notable state changes worth surfacing by default.</summary>
    public void Info(string message) => Send(MessageType.Info, message);

    /// <summary>Verbose/measurement trace (query and rebuild timings).</summary>
    public void Log(string message) => Send(MessageType.Log, message);

    /// <summary>
    /// Per-request chatter: one line every time the editor polls hover, signature
    /// help, document highlight or code actions - which is on every caret move and
    /// every mouse rest.  Suppressed unless the client asked for tracing
    /// (<c>nemerle.server.trace</c> other than <c>off</c>).
    ///
    /// <para><b>Why this needs its own level.</b>  <c>MessageType.Log</c> is
    /// already the protocol's lowest, but vscode-languageclient renders it in the
    /// Output channel as <c>[info]</c> all the same, so "quieter" can only mean
    /// "not sent".  Left on, these lines make the Output channel unreadable for
    /// the diagnosis it exists for, and every one of them appends to the Output
    /// editor while the user is typing - which is the leading suspect for word
    /// highlights being dropped when the panel is open (58 追記2).</para>
    ///
    /// <para>The paths that report something <em>unexpected</em> - refusals, empty
    /// answers that should not be empty, engine failures - keep using
    /// <see cref="Log"/> / <see cref="Warning"/>, so a silent failure is still
    /// visible without turning tracing on.</para>
    /// </summary>
    public void Trace(string message)
    {
        if (Volatile.Read(ref _traceEnabled))
            Send(MessageType.Log, message);
    }

    /// <summary>
    /// Turns <see cref="Trace"/> on or off.  Set from the client's
    /// <c>initialize</c> trace value and from <c>$/setTrace</c>, so the
    /// <c>nemerle.server.trace</c> setting takes effect without a restart.
    /// </summary>
    public void SetTraceEnabled(bool enabled) => Volatile.Write(ref _traceEnabled, enabled);

    /// <summary>Recoverable anomalies (unreadable source, unsupported option).</summary>
    public void Warning(string message) => Send(MessageType.Warning, message);

    /// <summary>Recoverable failures (project query/apply failure).</summary>
    public void Error(string message) => Send(MessageType.Error, message);

    /// <summary>
    /// Puts a warning in front of the user (LSP 3.17 <c>window/showMessage</c>), which
    /// vscode-languageclient renders as a notification. Reserved for conditions the user must
    /// act on and would otherwise misdiagnose - currently only a toolchain/language-server
    /// version mismatch (WP-M6, §6.8), whose symptom is an unexplained FileLoadException or
    /// analysis that silently disagrees with `dotnet build`.
    ///
    /// This is why the extension needs no TypeScript for that warning: showMessage is a
    /// protocol-level notification the client already handles, so the server can surface it
    /// directly. Use sparingly - unlike <see cref="Warning"/> (Output Channel), this interrupts.
    /// </summary>
    public void ShowWarning(string message)
    {
        var payload = new ShowMessageParams { Type = MessageType.Warning, Message = message };
        ILanguageServerFacade? server;
        lock (_gate)
        {
            if (_server is null)
            {
                _bufferedShow.Add(payload);
                return;
            }

            server = _server;
        }

        server.Window.ShowMessage(payload);
    }

    /// <summary>
    /// A <see cref="TextWriter"/> that forwards whole lines to <see cref="Log"/>,
    /// used as the engine's own output sink so its writes go to logMessage instead
    /// of stderr.
    /// </summary>
    public TextWriter AsTextWriter() => new LineWriter(this);

    /// <summary>
    /// Pre-initialize fatal path only: the server facade cannot exist yet and
    /// stdout is JSON-RPC framing, so a process that dies before <c>initialize</c>
    /// has nowhere to report but stderr.
    /// </summary>
    public static void Fatal(string message) => Console.Error.WriteLine(message);

    private void Send(MessageType type, string message)
    {
        var payload = new LogMessageParams { Type = type, Message = message };
        ILanguageServerFacade? server;
        lock (_gate)
        {
            if (_server is null)
            {
                _buffered.Add(payload);
                return;
            }

            server = _server;
        }

        server.Window.LogMessage(payload);
    }

    private sealed class LineWriter(ServerLog log) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                    FlushLocked();
                else if (value != '\r')
                    _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            foreach (var c in value)
                Write(c);
        }

        public override void Flush()
        {
            lock (_line)
                FlushLocked();
        }

        private void FlushLocked()
        {
            if (_line.Length == 0)
                return;
            log.Log(_line.ToString());
            _line.Clear();
        }
    }
}
