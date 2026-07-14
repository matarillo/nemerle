namespace Nemerle.LanguageServer;

/// <summary>
/// Server-wide toggles resolved once at startup.  <see cref="IncrementalUpdate"/>
/// is the WP-M5 escape hatch (§6.4 of 29-devenv2-plan.md): on by default, it
/// advertises <c>TextDocumentSyncKind.Incremental</c> and routes project-source
/// edits through the engine's relocation path (<c>BeginUpdateCompileUnit</c>).
/// Setting the environment variable <c>NEMERLE_INCREMENTAL_UPDATE</c> to
/// <c>0</c>/<c>false</c>/<c>off</c> disables it, restoring the pre-WP-M5 behavior
/// (full-document sync + <c>BeginReloadProject</c> on every change).
/// </summary>
internal sealed class ServerOptions
{
    public const string IncrementalUpdateVariable = "NEMERLE_INCREMENTAL_UPDATE";

    public ServerOptions(bool incrementalUpdate) => IncrementalUpdate = incrementalUpdate;

    public bool IncrementalUpdate { get; }

    public static ServerOptions FromEnvironment() =>
        new(ParseEnabled(Environment.GetEnvironmentVariable(IncrementalUpdateVariable)));

    internal static bool ParseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        return value.Trim() switch
        {
            "0" => false,
            _ when string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase) => false,
            _ when string.Equals(value.Trim(), "off", StringComparison.OrdinalIgnoreCase) => false,
            _ when string.Equals(value.Trim(), "no", StringComparison.OrdinalIgnoreCase) => false,
            _ => true,
        };
    }
}
