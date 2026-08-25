namespace Supprocom.ScaleToolChain;

public sealed record ScaleInvocationResult
{
    public required string ToolPath { get; init; }

    public required string ExecutedToolPath { get; init; }

    public required string ProcessPath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required IReadOnlyList<string> ProcessArguments { get; init; }

    public ScaleGpuTarget? Target { get; init; }

    public required ScaleExecutionMode ExecutionMode { get; init; }

    public required int ExitCode { get; init; }

    public required bool Succeeded { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public required IReadOnlyList<string> ProducedOutputPaths { get; init; }

    public required IReadOnlyDictionary<string, string> OutputSha256 { get; init; }

    public Exception? CleanupFailure { get; init; }

    public required TimeSpan Duration { get; init; }
}
