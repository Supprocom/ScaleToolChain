namespace Supprocom.ScaleToolChain;

public sealed record ScaleCompilationRequest
{
    public required string SourcePath { get; init; }

    public required string OutputPath { get; init; }

    public required ScaleCompilationSettings Settings { get; init; }
}
