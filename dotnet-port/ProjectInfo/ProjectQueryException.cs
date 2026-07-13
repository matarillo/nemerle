namespace Nemerle.ProjectInfo;

public enum ProjectQueryErrorKind
{
    Cancelled,
    Timeout,
    StartFailure,
    NonZeroExit,
    EmptyOutput,
    InvalidJson,
    MissingField,
}

public sealed class ProjectQueryException : Exception
{
    public ProjectQueryException(
        ProjectQueryErrorKind kind,
        string message,
        string? standardError = null,
        string? standardOutput = null,
        int? exitCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StandardError = Truncate(standardError);
        StandardOutput = Truncate(standardOutput);
        ExitCode = exitCode;
    }

    public ProjectQueryErrorKind Kind { get; }
    public string StandardError { get; }
    public string StandardOutput { get; }
    public int? ExitCode { get; }

    private static string Truncate(string? value)
    {
        const int limit = 16 * 1024;
        if (string.IsNullOrEmpty(value) || value.Length <= limit)
            return value ?? string.Empty;
        return value[..limit] + Environment.NewLine + "... output truncated ...";
    }
}
