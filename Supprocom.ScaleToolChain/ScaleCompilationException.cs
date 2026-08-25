namespace Supprocom.ScaleToolChain;

public class ScaleCompilationException : Exception
{
    public const string CleanupFailureDataKey = "Supprocom.ScaleToolChain.CleanupFailure";

    public ScaleCompilationException(
        string message,
        string? sourcePath = null,
        string? outputPath = null,
        int? exitCode = null,
        string standardOutput = "",
        string standardError = "",
        Exception? innerException = null)
        : base(message, innerException)
    {
        SourcePath = sourcePath;
        OutputPath = outputPath;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public string? SourcePath { get; }

    public string? OutputPath { get; }

    public int? ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public Exception? CleanupFailure { get; internal set; }
}

public sealed class ScaleConfigurationException : Exception
{
    public ScaleConfigurationException(string message)
        : base(message)
    {
    }
}

public sealed class ScaleCompilationTimeoutException : ScaleCompilationException
{
    public ScaleCompilationTimeoutException(
        TimeSpan timeout,
        string sourcePath,
        string outputPath,
        string standardOutput,
        string standardError,
        Exception? innerException = null,
        Exception? cleanupFailure = null)
        : base(
            $"The SCALE compiler exceeded the {timeout} timeout.",
            sourcePath,
            outputPath,
            null,
            standardOutput,
            standardError,
            innerException)
    {
        Timeout = timeout;
        CleanupFailure = cleanupFailure;
    }

    public TimeSpan Timeout { get; }
}

public sealed class ScaleProcessCleanupException : Exception
{
    public ScaleProcessCleanupException(
        string message,
        int processGroupId,
        string? wslDistribution,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProcessGroupId = processGroupId;
        WslDistribution = wslDistribution;
    }

    public int ProcessGroupId { get; }

    public string? WslDistribution { get; }
}
