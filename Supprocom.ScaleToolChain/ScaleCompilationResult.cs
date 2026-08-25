namespace Supprocom.ScaleToolChain;

public sealed record ScaleCompilationResult
{
    public required string SourcePath { get; init; }

    public required string OutputPath { get; init; }

    public required ScaleGpuTarget Target { get; init; }

    public required string ProcessPath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required int ExitCode { get; init; }

    public required bool Succeeded { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public required string SourceSha256 { get; init; }

    public string? OutputSha256 { get; init; }

    public required TimeSpan Duration { get; init; }
}
