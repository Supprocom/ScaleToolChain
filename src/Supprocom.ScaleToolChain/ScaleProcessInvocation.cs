using System.Collections.ObjectModel;

namespace Supprocom.ScaleToolChain;

internal sealed record ScaleProcessInvocation(
    string ProcessPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

internal static class ScaleCommandBuilder
{
    public static ScaleProcessInvocation Build(
        string compilerPath,
        ScaleGpuTarget target,
        string sourcePath,
        string outputPath,
        string workingDirectory,
        ScaleCompilationSettings settings)
    {
        var compilerArguments = new[]
        {
            "--require-scale",
            $"--gpu-architecture={target.Architecture}",
            "-c",
            sourcePath,
            "-o",
            outputPath
        };

        if (settings.ExecutionMode == ScaleExecutionMode.Native)
        {
            return new ScaleProcessInvocation(
                compilerPath,
                Array.AsReadOnly(compilerArguments),
                workingDirectory,
                new ReadOnlyDictionary<string, string>(
                    settings.Environment
                        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)));
        }

        var wslCompilerPath = ToWslCompilerPath(compilerPath);
        var wslSourcePath = WindowsToWslPath(sourcePath);
        var wslOutputPath = WindowsToWslPath(outputPath);
        var wslWorkingDirectory = WindowsToWslPath(workingDirectory);
        var wslArguments = new List<string>
        {
            "--distribution",
            settings.WslDistribution!,
            "--cd",
            wslWorkingDirectory,
            "--",
            "/usr/bin/env"
        };

        foreach (var pair in settings.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            wslArguments.Add($"{pair.Key}={pair.Value}");
        }

        wslArguments.Add(wslCompilerPath);
        wslArguments.Add("--require-scale");
        wslArguments.Add($"--gpu-architecture={target.Architecture}");
        wslArguments.Add("-c");
        wslArguments.Add(wslSourcePath);
        wslArguments.Add("-o");
        wslArguments.Add(wslOutputPath);

        return new ScaleProcessInvocation(
            settings.WslExecutablePath,
            Array.AsReadOnly(wslArguments.ToArray()),
            workingDirectory,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    internal static string ToWslCompilerPath(string compilerPath)
    {
        if (IsPosixAbsolutePath(compilerPath))
        {
            return compilerPath;
        }

        return WindowsToWslPath(compilerPath);
    }

    internal static string WindowsToWslPath(string path)
    {
        if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
        {
            throw new ScaleConfigurationException($"The path must be an absolute Windows path for WSL execution: {path}");
        }

        var drive = char.ToLowerInvariant(path[0]);
        var remainder = path[3..].Replace('\\', '/');
        return $"/mnt/{drive}/{remainder}";
    }

    private static bool IsPosixAbsolutePath(string path) => path.StartsWith('/');
}
