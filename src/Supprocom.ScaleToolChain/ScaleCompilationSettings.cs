namespace Supprocom.ScaleToolChain;

public enum ScaleExecutionMode
{
    Native,
    Wsl
}

public sealed record ScaleCompilationSettings
{
    public required string CompilerPath { get; init; }

    public required ScaleGpuTarget Target { get; init; }

    public ScaleExecutionMode ExecutionMode { get; init; } = ScaleExecutionMode.Native;

    public string? WslDistribution { get; init; }

    public string WslExecutablePath { get; init; } = "wsl.exe";

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public string? WorkingDirectory { get; init; }
}
