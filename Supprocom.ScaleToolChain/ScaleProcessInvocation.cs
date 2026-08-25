using System.Collections.ObjectModel;

namespace Supprocom.ScaleToolChain;

internal sealed record ScaleProcessInvocation(
    string ProcessPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? WslDistribution,
    string? WslPidMarker);

internal static class ScaleCommandBuilder
{
    internal const string WslPidMarker = "__SCALE_TOOLCHAIN_PID__:";

    public static ScaleProcessInvocation Build(
        string compilerPath,
        ScaleGpuTarget target,
        string sourcePath,
        string outputPath,
        string workingDirectory,
        ScaleCompilationSettings settings)
    {
        if (settings.ExecutionMode == ScaleExecutionMode.Native)
        {
            var nativeArguments = BuildCompilerArguments(target, sourcePath, outputPath, settings, static path => path);
            return new ScaleProcessInvocation(
                compilerPath,
                Array.AsReadOnly(nativeArguments.ToArray()),
                workingDirectory,
                new ReadOnlyDictionary<string, string>(
                    settings.Environment
                        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)),
                null,
                null);
        }

        var wslCompilerPath = ToWslCompilerPath(compilerPath);
        var wslSourcePath = WindowsToWslPath(sourcePath);
        var wslOutputPath = WindowsToWslPath(outputPath);
        var wslWorkingDirectory = WindowsToWslPath(workingDirectory);
        var wslCompilerArguments = BuildCompilerArguments(
            target,
            wslSourcePath,
            wslOutputPath,
            settings,
            ToWslToolchainPath);
        var wslEnvironment = BuildWslEnvironment(settings, target, wslCompilerPath);
        var wslArguments = new List<string>
        {
            "--distribution",
            settings.WslDistribution!,
            "--cd",
            wslWorkingDirectory,
            "--exec",
            "/usr/bin/setsid",
            "--wait",
            "/bin/sh",
            "-c",
            $"printf '{WslPidMarker}%s\\n' \"$$\"; exec /usr/bin/env \"$@\"",
            "scale-toolchain"
        };

        foreach (var pair in wslEnvironment)
        {
            wslArguments.Add($"{pair.Key}={pair.Value}");
        }

        wslArguments.Add(wslCompilerPath);
        wslArguments.AddRange(wslCompilerArguments);

        return new ScaleProcessInvocation(
            settings.WslExecutablePath,
            Array.AsReadOnly(wslArguments.ToArray()),
            workingDirectory,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)),
            settings.WslDistribution,
            WslPidMarker);
    }

    internal static string ToWslCompilerPath(string compilerPath) => ToWslToolchainPath(compilerPath);

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

    private static List<string> BuildCompilerArguments(
        ScaleGpuTarget target,
        string sourcePath,
        string outputPath,
        ScaleCompilationSettings settings,
        Func<string, string> pathTranslator)
    {
        var arguments = new List<string>();
        if (settings.CudaToolkitPath is not null)
        {
            arguments.Add($"--cuda-path={pathTranslator(settings.CudaToolkitPath)}");
        }

        foreach (var includePath in settings.IncludePaths)
        {
            arguments.Add("-I");
            arguments.Add(pathTranslator(includePath));
        }

        foreach (var definition in settings.Definitions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            arguments.Add("-D");
            arguments.Add($"{definition.Key}={definition.Value}");
        }

        arguments.Add("--require-scale");
        arguments.Add($"--gpu-architecture={target.Architecture}");
        arguments.Add("-c");
        arguments.Add(sourcePath);
        arguments.Add("-o");
        arguments.Add(outputPath);
        return arguments;
    }

    private static KeyValuePair<string, string>[] BuildWslEnvironment(
        ScaleCompilationSettings settings,
        ScaleGpuTarget target,
        string wslCompilerPath)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in settings.Environment)
        {
            environment[pair.Key] = pair.Value;
        }
        if (target.Vendor == ScaleGpuVendor.Nvidia && settings.CudaToolkitPath is not null)
        {
            var toolkitPath = ToWslToolchainPath(settings.CudaToolkitPath);
            var toolkitBin = $"{toolkitPath}/bin";
            var toolkitInclude = $"{toolkitPath}/include";
            var toolkitLib = $"{toolkitPath}/lib64";
            var existingPath = environment.TryGetValue("PATH", out var path)
                ? path
                : "/usr/local/bin:/usr/bin:/bin";
            var existingCpath = environment.TryGetValue("CPATH", out var cpath) ? cpath : string.Empty;
            var existingLibraryPath = environment.TryGetValue("LIBRARY_PATH", out var libraryPath) ? libraryPath : string.Empty;
            var existingLdLibraryPath = environment.TryGetValue("LD_LIBRARY_PATH", out var ldLibraryPath) ? ldLibraryPath : string.Empty;
            environment["CUDA_DIR"] = toolkitPath;
            environment["CUDA_HOME"] = toolkitPath;
            environment["CUDA_PATH"] = toolkitPath;
            environment["CUDA_ROOT"] = toolkitPath;
            environment["CUDA_CXX"] = wslCompilerPath;
            environment["CUDACXX"] = wslCompilerPath;
            environment["CUDA_INC_DIR"] = toolkitInclude;
            environment["CUDA_BIN_PATH"] = "/opt/scale/llvm/bin";
            environment["CUDAARCHS"] = target.Architecture[3..];
            environment["CPATH"] = JoinPathPrefix(toolkitInclude, existingCpath);
            environment["LIBRARY_PATH"] = JoinPathPrefix(toolkitLib, existingLibraryPath);
            environment["LD_LIBRARY_PATH"] = JoinPathPrefix(toolkitLib, existingLdLibraryPath);
            environment["PATH"] = JoinPathPrefix("/opt/scale/llvm/bin", JoinPathPrefix(toolkitBin, existingPath));
            environment["CUDA_NVCC_EXECUTABLE"] = wslCompilerPath;
            environment["NVCC_PREPEND_FLAGS"] = "-require-scale";
            environment["NVCC_APPEND_FLAGS"] = "-require-scale";
        }

        return environment.ToArray();
    }

    private static string JoinPathPrefix(string prefix, string suffix) =>
        string.IsNullOrEmpty(suffix) ? prefix : $"{prefix}:{suffix}";

    private static string ToWslToolchainPath(string path)
    {
        if (IsPosixAbsolutePath(path))
        {
            return path;
        }

        return WindowsToWslPath(path);
    }

    private static bool IsPosixAbsolutePath(string path) => path.StartsWith('/');
}
