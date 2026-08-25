using Supprocom.ScaleToolChain;

var target = ScaleGpuTarget.Amd("gfx1201");
var settings = new ScaleCompilationSettings
{
    CompilerPath = "/opt/scale/llvm/bin/nvcc",
    Target = target,
    ExecutionMode = ScaleExecutionMode.Wsl,
    WslDistribution = "Ubuntu-24.04"
};

if (settings.Target != target || settings.ExecutionMode != ScaleExecutionMode.Wsl)
{
    throw new InvalidOperationException("The package consumer could not construct SCALE settings.");
}

Console.WriteLine($"{target.Architecture}:{settings.ExecutionMode}");
