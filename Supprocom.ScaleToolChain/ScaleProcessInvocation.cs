using System.Collections.ObjectModel;

namespace Supprocom.ScaleToolChain;

internal sealed record ScaleProcessInvocation(
    string ToolPath,
    string ExecutedToolPath,
    IReadOnlyList<string> ToolArguments,
    string ProcessPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? WslDistribution,
    string? WslPidMarker);

internal static class ScaleCommandBuilder
{
    internal const string WslPidMarker = "__SCALE_TOOLCHAIN_PID__:";

    public static ScaleProcessInvocation Build(ScaleInvocationRequest request, string workingDirectory)
    {
        var rawArguments = request.Arguments.ToArray();
        var translatedArguments = request.ExecutionMode == ScaleExecutionMode.Wsl
            ? TranslatePathArguments(rawArguments, request.PathArgumentIndexes)
            : rawArguments;
        var toolArguments = AddControlledArguments(request, translatedArguments);
        var environment = request.Environment
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        if (request.ExecutionMode == ScaleExecutionMode.Native)
        {
            var readOnlyEnvironment = new ReadOnlyDictionary<string, string>(environment);
            var readOnlyArguments = Array.AsReadOnly(toolArguments.ToArray());
            return new ScaleProcessInvocation(
                request.ToolPath,
                request.ToolPath,
                readOnlyArguments,
                request.ToolPath,
                readOnlyArguments,
                workingDirectory,
                readOnlyEnvironment,
                null,
                null);
        }

        var wslToolPath = ToWslToolchainPath(request.ToolPath);
        var wslWorkingDirectory = WindowsToWslPath(workingDirectory);
        var wslArguments = new List<string>
        {
            "--distribution",
            request.WslDistribution!,
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

        foreach (var pair in environment)
        {
            wslArguments.Add($"{pair.Key}={pair.Value}");
        }

        wslArguments.Add(wslToolPath);
        wslArguments.AddRange(toolArguments);

        return new ScaleProcessInvocation(
            request.ToolPath,
            wslToolPath,
            Array.AsReadOnly(toolArguments.ToArray()),
            request.WslExecutablePath,
            Array.AsReadOnly(wslArguments.ToArray()),
            workingDirectory,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)),
            request.WslDistribution,
            WslPidMarker);
    }

    public static ScaleProcessInvocation Build(
        string compilerPath,
        ScaleGpuTarget target,
        string sourcePath,
        string outputPath,
        string workingDirectory,
        ScaleCompilationSettings settings)
    {
        var request = CreateCompilationRequest(compilerPath, target, sourcePath, outputPath, workingDirectory, settings);
        return Build(request, workingDirectory);
    }

    internal static ScaleInvocationRequest CreateCompilationRequest(
        string compilerPath,
        ScaleGpuTarget target,
        string sourcePath,
        string outputPath,
        string workingDirectory,
        ScaleCompilationSettings settings)
    {
        Func<string, string> pathTranslator = settings.ExecutionMode == ScaleExecutionMode.Wsl
            ? ToWslToolchainPath
            : static path => path;
        var rawArguments = BuildCompilerArguments(target, sourcePath, outputPath, settings, pathTranslator);
        IReadOnlyDictionary<string, string> environment = settings.ExecutionMode == ScaleExecutionMode.Wsl
            ? BuildWslEnvironment(settings, target, ToWslCompilerPath(compilerPath))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
            : settings.Environment;
        return new ScaleInvocationRequest
        {
            ToolPath = compilerPath,
            Arguments = rawArguments,
            ToolKind = ScaleInvocationToolKind.Compiler,
            Target = target,
            TargetArgumentMode = ScaleTargetArgumentMode.GpuArchitecture,
            OutputPaths = new[] { outputPath },
            ExecutionMode = settings.ExecutionMode,
            WslDistribution = settings.WslDistribution,
            WslExecutablePath = settings.WslExecutablePath,
            Environment = environment,
            Timeout = settings.Timeout,
            WorkingDirectory = workingDirectory,
            AllowPackageEnvironmentOverrides = true
        };
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

    private static List<string> AddControlledArguments(ScaleInvocationRequest request, string[] arguments)
    {
        if (request.ToolKind == ScaleInvocationToolKind.Utility)
        {
            return arguments.ToList();
        }

        if (arguments.Any(static argument => argument.Equals("--require-scale", StringComparison.Ordinal) ||
            argument.StartsWith("--require-scale=", StringComparison.Ordinal)))
        {
            throw new ScaleConfigurationException("The compiler controls --require-scale and caller arguments must not provide it.");
        }

        var controlled = new List<string>(arguments.Length + 2) { "--require-scale" };
        switch (request.TargetArgumentMode)
        {
            case ScaleTargetArgumentMode.GpuArchitecture:
                RejectCallerTargetArguments(arguments);
                controlled.Add($"--gpu-architecture={request.Target!.Architecture}");
                break;
            case ScaleTargetArgumentMode.OffloadArchitecture:
                RejectCallerTargetArguments(arguments);
                controlled.Add($"--offload-arch={request.Target!.Architecture}");
                break;
            case ScaleTargetArgumentMode.CallerSupplied:
                if (!ContainsTargetArgument(arguments, request.Target!))
                {
                    throw new ScaleConfigurationException("Caller-supplied compiler arguments must contain the explicit target.");
                }
                break;
            case ScaleTargetArgumentMode.None:
                break;
            default:
                throw new ScaleConfigurationException("The target argument mode is not supported.");
        }

        controlled.AddRange(arguments);
        return controlled;
    }

    private static void RejectCallerTargetArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(IsTargetArgument))
        {
            throw new ScaleConfigurationException("The compiler controls the target argument and caller arguments must not provide one.");
        }
    }

    private static bool IsTargetArgument(string argument) =>
        argument.Equals("--gpu-architecture", StringComparison.Ordinal) ||
        argument.StartsWith("--gpu-architecture=", StringComparison.Ordinal) ||
        argument.Equals("--offload-arch", StringComparison.Ordinal) ||
        argument.StartsWith("--offload-arch=", StringComparison.Ordinal) ||
        argument.Equals("-arch", StringComparison.Ordinal) ||
        argument.StartsWith("-arch=", StringComparison.Ordinal);

    private static bool ContainsTargetArgument(string[] arguments, ScaleGpuTarget target)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if ((argument.StartsWith("--gpu-architecture=", StringComparison.Ordinal) ||
                 argument.StartsWith("--offload-arch=", StringComparison.Ordinal) ||
                 argument.StartsWith("-arch=", StringComparison.Ordinal)) &&
                argument[(argument.IndexOf('=') + 1)..].Equals(target.Architecture, StringComparison.Ordinal))
            {
                return true;
            }

            if ((argument.Equals("--gpu-architecture", StringComparison.Ordinal) ||
                 argument.Equals("--offload-arch", StringComparison.Ordinal) ||
                 argument.Equals("-arch", StringComparison.Ordinal)) &&
                 index + 1 < arguments.Length &&
                arguments[index + 1].Equals(target.Architecture, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] TranslatePathArguments(
        IReadOnlyList<string> arguments,
        IReadOnlyList<int> indexes)
    {
        var translated = arguments.ToArray();
        foreach (var index in indexes)
        {
            var value = translated[index];
            translated[index] = value.StartsWith('@')
                ? $"@{ToWslToolchainPath(value[1..])}"
                : ToWslToolchainPath(value);
        }

        return translated;
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

        arguments.Add("-c");
        arguments.Add(pathTranslator(sourcePath));
        arguments.Add("-o");
        arguments.Add(pathTranslator(outputPath));
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
            var existingPath = environment.TryGetValue("PATH", out var path) ? path : "/usr/local/bin:/usr/bin:/bin";
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
