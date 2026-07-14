using Nemerle.Compiler.Utils.Async;

namespace Nemerle.LanguageServer.Engine;

/// <summary>
/// Turns an IDE-engine <c>Begin*</c> request into an awaitable <see cref="Task"/>.
///
/// A <c>Begin*</c> call (e.g. <c>BeginGetQuickTipInfo</c>) enqueues work on the
/// process-wide <see cref="AsyncWorker"/> and the worker thread finishes it with
/// <c>AsyncRequest.MarkAsCompleted</c>; the bridge starts the request under the
/// caller's engine-serialization lock and then polls for completion off that
/// lock, so a language-feature request never blocks document sync or reload.
///
/// It honors the two ways a request can stop being a valid answer, and fabricates
/// neither (§6.2 of 29-devenv2-plan.md):
/// <list type="bullet">
/// <item>the AsyncWorker force-out policy marks a superseded request
/// <c>Stop = true</c> before completing it, which the bridge reports as
/// <see cref="RequestOutcome.Cancelled"/>; the LSP <see cref="CancellationToken"/>
/// is mapped onto the same <c>Stop</c> flag;</item>
/// <item>a request whose document version advanced before it finished is reported
/// as <see cref="RequestOutcome.Stale"/> (same generation guard the diagnostics
/// path uses).</item>
/// </list>
/// WP-M3 (completion) and WP-M4 (definition/references) reuse this boundary.
/// </summary>
internal sealed class EngineRequestBridge
{
    internal enum RequestOutcome
    {
        /// <summary>The request finished for its document's current version.</summary>
        Completed,

        /// <summary>Cancelled by the LSP token or forced out by a later request.</summary>
        Cancelled,

        /// <summary>The document version advanced before the request finished.</summary>
        Stale,

        /// <summary>The request did not finish within the timeout.</summary>
        TimedOut,
    }

    internal readonly record struct RequestResult<T>(RequestOutcome Outcome, T? Value)
    {
        /// <summary>True only for a fresh, uncancelled completion whose value can be trusted.</summary>
        public bool IsUsable => Outcome == RequestOutcome.Completed;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    private readonly TimeSpan _timeout;

    public EngineRequestBridge(TimeSpan? timeout = null)
    {
        // A generous ceiling so a wedged engine can never hang an LSP request;
        // normal warm requests complete in well under this.
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <param name="start">
    /// Enqueues the <c>Begin*</c> request and returns its handle.  Executes
    /// synchronously (before the first await), so the caller may invoke
    /// <see cref="RunAsync{T}"/> inside its engine lock and await the result
    /// after releasing it.
    /// </param>
    /// <param name="currentVersion">The document's live version, re-read after
    /// completion to drop a stale answer.</param>
    /// <param name="expectedVersion">The document version captured when the
    /// request started.</param>
    /// <param name="extract">Pulls the typed result off the completed request.</param>
    public async Task<RequestResult<T>> RunAsync<T>(
        Func<AsyncRequest> start,
        Func<int> currentVersion,
        int expectedVersion,
        Func<AsyncRequest, T?> extract,
        CancellationToken token)
    {
        var request = start();
        var deadline = DateTime.UtcNow + _timeout;

        while (!request.IsCompleted)
        {
            if (token.IsCancellationRequested)
            {
                request.Stop = true;
                return new RequestResult<T>(RequestOutcome.Cancelled, default);
            }

            if (DateTime.UtcNow >= deadline)
            {
                request.Stop = true;
                return new RequestResult<T>(RequestOutcome.TimedOut, default);
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                request.Stop = true;
                return new RequestResult<T>(RequestOutcome.Cancelled, default);
            }
        }

        // The AsyncWorker sets Stop = true before completing a request that a
        // later same-kind request (or a CloseProject) forced out, so a set flag
        // on a completed request means the answer was superseded, not produced.
        if (request.Stop)
            return new RequestResult<T>(RequestOutcome.Cancelled, default);

        if (currentVersion() != expectedVersion)
            return new RequestResult<T>(RequestOutcome.Stale, default);

        return new RequestResult<T>(RequestOutcome.Completed, extract(request));
    }
}
