namespace Supprocom.ScaleToolChain;

public enum ScaleInvocationToolKind
{
    Compiler,
    Utility
}

public enum ScaleTargetArgumentMode
{
    GpuArchitecture,
    OffloadArchitecture,
    CallerSupplied,
    None
}

public sealed record ScaleInvocationRequest
{
    public required string ToolPath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public ScaleInvocationToolKind ToolKind { get; init; } = ScaleInvocationToolKind.Compiler;

    public ScaleGpuTarget? Target { get; init; }

    public ScaleTargetArgumentMode TargetArgumentMode { get; init; } = ScaleTargetArgumentMode.GpuArchitecture;

    public IReadOnlyList<int> PathArgumentIndexes { get; init; } = Array.Empty<int>();

    public IReadOnlyList<string> OutputPaths { get; init; } = Array.Empty<string>();

    public ScaleExecutionMode ExecutionMode { get; init; } = ScaleExecutionMode.Native;

    public string? WslDistribution { get; init; }

    public string WslExecutablePath { get; init; } = "wsl.exe";

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public string? WorkingDirectory { get; init; }

    internal bool AllowPackageEnvironmentOverrides { get; init; }
}
