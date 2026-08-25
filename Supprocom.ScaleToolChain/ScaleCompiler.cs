using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Supprocom.ScaleToolChain;

public static class ScaleCompiler
{
    public static async Task<ScaleInvocationResult> InvokeAsync(
        ScaleInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateInvocation(request);
        var invocation = ScaleCommandBuilder.Build(validated.Request, validated.WorkingDirectory);
        return await ExecuteAsync(validated, invocation, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ScaleCompilationResult> CompileAsync(
        ScaleCompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        var validatedCompilation = ValidateCompilation(request);
        var sourceSha256 = await ComputeSha256Async(validatedCompilation.SourcePath, cancellationToken).ConfigureAwait(false);
        var validated = ValidateInvocation(validatedCompilation.InvocationRequest);
        var invocation = ScaleCommandBuilder.Build(validated.Request, validated.WorkingDirectory);
        var result = await ExecuteAsync(validated, invocation, cancellationToken, validatedCompilation.SourcePath).ConfigureAwait(false);
        if (result.Succeeded &&
            result.ProducedOutputPaths.Count == 1 &&
            new FileInfo(result.ProducedOutputPaths[0]).Length == 0)
        {
            var cleanupFailure = DeleteFailedOutputs(result.ProducedOutputPaths);
            var exception = new ScaleCompilationException(
                "The SCALE compiler created an empty output.",
                validatedCompilation.SourcePath,
                validatedCompilation.OutputPath,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError)
            {
                CleanupFailure = cleanupFailure
            };
            throw exception;
        }

        return new ScaleCompilationResult
        {
            SourcePath = validatedCompilation.SourcePath,
            OutputPath = validatedCompilation.OutputPath,
            Target = validatedCompilation.Target,
            ProcessPath = result.ProcessPath,
            Arguments = result.Arguments,
            ProcessArguments = result.ProcessArguments,
            ExitCode = result.ExitCode,
            Succeeded = result.Succeeded,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            SourceSha256 = sourceSha256,
            OutputSha256 = result.OutputSha256.TryGetValue(validatedCompilation.OutputPath, out var outputSha256)
                ? outputSha256
                : null,
            ProducedOutputPaths = result.ProducedOutputPaths,
            CleanupFailure = result.CleanupFailure,
            Duration = result.Duration
        };
    }

    private static async Task<ScaleInvocationResult> ExecuteAsync(
        ValidatedInvocation validated,
        ScaleProcessInvocation invocation,
        CancellationToken cancellationToken,
        string? sourcePath = null)
    {
        var start = Stopwatch.GetTimestamp();
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ProcessPath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var pair in invocation.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var linuxPidSource = invocation.WslPidMarker is null
            ? null
            : new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            if (!process.Start())
            {
                throw new ScaleCompilationException(
                    "The SCALE process did not start.",
                    outputPath: FirstOutput(validated.OutputPaths));
            }
        }
        catch (ScaleCompilationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new ScaleCompilationException(
                $"The SCALE process could not start: {exception.Message}",
                outputPath: FirstOutput(validated.OutputPaths),
                innerException: exception);
        }

        var standardOutputTask = ReadStandardOutputAsync(process.StandardOutput, invocation, linuxPidSource);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(validated.Request.Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var processCleanupFailure = await StopProcessAsync(process, invocation, linuxPidSource).ConfigureAwait(false);
            var diagnostics = await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var outputCleanupFailure = DeleteFailedOutputs(validated.OutputPaths);
            var cleanupFailure = CombineCleanupFailures(processCleanupFailure, outputCleanupFailure);
            throw new ScaleCompilationTimeoutException(
                validated.Request.Timeout,
                sourcePath,
                outputPath: FirstOutput(validated.OutputPaths),
                diagnostics.StandardOutput,
                diagnostics.StandardError,
                exception,
                cleanupFailure);
        }
        catch (OperationCanceledException exception)
        {
            var processCleanupFailure = await StopProcessAsync(process, invocation, linuxPidSource).ConfigureAwait(false);
            var diagnostics = await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var outputCleanupFailure = DeleteFailedOutputs(validated.OutputPaths);
            var cleanupFailure = CombineCleanupFailures(processCleanupFailure, outputCleanupFailure);
            exception.Data[ScaleCompilationException.CancellationStandardOutputDataKey] = diagnostics.StandardOutput;
            exception.Data[ScaleCompilationException.CancellationStandardErrorDataKey] = diagnostics.StandardError;
            if (cleanupFailure is not null)
            {
                exception.Data[ScaleCompilationException.CleanupFailureDataKey] = cleanupFailure;
            }

            throw;
        }

        var completedDiagnostics = await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(start);
        var producedOutputPaths = validated.OutputPaths.Where(File.Exists).ToArray();
        var outputHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outputPath in producedOutputPaths)
        {
            outputHashes[outputPath] = await ComputeSha256Async(outputPath, CancellationToken.None).ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            var outputCleanupFailure = DeleteFailedOutputs(validated.OutputPaths);
            return CreateResult(
                validated,
                invocation,
                process.ExitCode,
                completedDiagnostics,
                duration,
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                outputCleanupFailure);
        }

        var missingOutputPaths = validated.OutputPaths.Where(static path => !File.Exists(path)).ToArray();
        if (missingOutputPaths.Length > 0)
        {
            var outputCleanupFailure = DeleteFailedOutputs(validated.OutputPaths);
            var exception = new ScaleCompilationException(
                $"The SCALE process exited successfully but did not create the declared output '{missingOutputPaths[0]}'.",
                outputPath: missingOutputPaths[0],
                exitCode: process.ExitCode,
                standardOutput: completedDiagnostics.StandardOutput,
                standardError: completedDiagnostics.StandardError)
            {
                CleanupFailure = outputCleanupFailure
            };
            throw exception;
        }

        return CreateResult(
            validated,
            invocation,
            process.ExitCode,
            completedDiagnostics,
            duration,
            producedOutputPaths,
            outputHashes,
            cleanupFailure: null);
    }

    private static ScaleInvocationResult CreateResult(
        ValidatedInvocation validated,
        ScaleProcessInvocation invocation,
        int exitCode,
        Diagnostics diagnostics,
        TimeSpan duration,
        IReadOnlyList<string> producedOutputPaths,
        IReadOnlyDictionary<string, string> outputHashes,
        Exception? cleanupFailure)
    {
        return new ScaleInvocationResult
        {
            ToolPath = invocation.ToolPath,
            ExecutedToolPath = invocation.ExecutedToolPath,
            ProcessPath = invocation.ProcessPath,
            Arguments = invocation.ToolArguments,
            ProcessArguments = invocation.Arguments,
            Target = validated.Request.Target,
            ExecutionMode = validated.Request.ExecutionMode,
            ExitCode = exitCode,
            Succeeded = exitCode == 0,
            StandardOutput = diagnostics.StandardOutput,
            StandardError = diagnostics.StandardError,
            ProducedOutputPaths = producedOutputPaths,
            OutputSha256 = outputHashes,
            CleanupFailure = cleanupFailure,
            Duration = duration
        };
    }

    private static ValidatedCompilation ValidateCompilation(ScaleCompilationRequest request)
    {
        if (request is null)
        {
            throw new ScaleConfigurationException("A compilation request is required.");
        }

        if (request.Settings is null)
        {
            throw new ScaleConfigurationException("Compilation settings are required.");
        }

        if (request.Settings.Target is null)
        {
            throw new ScaleConfigurationException("An explicit SCALE GPU target is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Settings.CompilerPath))
        {
            throw new ScaleConfigurationException("An absolute SCALE compiler path is required.");
        }

        if (!Enum.IsDefined(request.Settings.ExecutionMode))
        {
            throw new ScaleConfigurationException("The SCALE execution mode is not supported.");
        }

        if (request.Settings.Timeout <= TimeSpan.Zero || request.Settings.Timeout > TimeSpan.FromHours(24))
        {
            throw new ScaleConfigurationException("The SCALE timeout must be greater than zero and no longer than 24 hours.");
        }

        if (request.Settings.IncludePaths is null || request.Settings.Definitions is null)
        {
            throw new ScaleConfigurationException("Compiler include paths and definitions cannot be null.");
        }

        ValidateCompilationEnvironment(request.Settings.Environment);

        var sourcePath = RequireAbsolutePath(request.SourcePath, "The CUDA source path must be absolute.");
        var outputPath = RequireAbsolutePath(request.OutputPath, "The output path must be absolute.");
        if (!File.Exists(sourcePath))
        {
            throw new ScaleConfigurationException($"The CUDA source file does not exist: {sourcePath}");
        }

        if (new FileInfo(sourcePath).Length == 0)
        {
            throw new ScaleConfigurationException("The CUDA source file must not be empty.");
        }

        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScaleConfigurationException("The output path must differ from the CUDA source path.");
        }

        EnsureOutputPath(outputPath, request.Settings.ExecutionMode);
        var workingDirectory = request.Settings.WorkingDirectory is null
            ? Path.GetDirectoryName(sourcePath)!
            : RequireAbsolutePath(request.Settings.WorkingDirectory, "The working directory must be absolute.");
        if (!Directory.Exists(workingDirectory))
        {
            throw new ScaleConfigurationException($"The working directory does not exist: {workingDirectory}");
        }

        var invocationRequest = ScaleCommandBuilder.CreateCompilationRequest(
            request.Settings.CompilerPath,
            request.Settings.Target,
            sourcePath,
            outputPath,
            workingDirectory,
            request.Settings);
        return new ValidatedCompilation(sourcePath, outputPath, request.Settings.Target, invocationRequest);
    }

    private static ValidatedInvocation ValidateInvocation(ScaleInvocationRequest request)
    {
        if (request is null)
        {
            throw new ScaleConfigurationException("An invocation request is required.");
        }

        if (!Enum.IsDefined(request.ToolKind) || !Enum.IsDefined(request.TargetArgumentMode) || !Enum.IsDefined(request.ExecutionMode))
        {
            throw new ScaleConfigurationException("The SCALE invocation mode is not supported.");
        }

        if (request.Arguments is null)
        {
            throw new ScaleConfigurationException("The SCALE argument collection cannot be null.");
        }

        var arguments = request.Arguments.ToArray();
        if (arguments.Any(static argument => argument is null || argument.Contains('\0')))
        {
            throw new ScaleConfigurationException("SCALE arguments must not contain null characters.");
        }

        if (request.PathArgumentIndexes is null)
        {
            throw new ScaleConfigurationException("The path argument index collection cannot be null.");
        }

        var indexes = request.PathArgumentIndexes.ToArray();
        if (indexes.Any(index => index < 0 || index >= arguments.Length) || indexes.Distinct().Count() != indexes.Length)
        {
            throw new ScaleConfigurationException("SCALE path argument indexes must identify unique argument positions.");
        }

        if (request.Environment is null)
        {
            throw new ScaleConfigurationException("The SCALE environment collection cannot be null.");
        }

        foreach (var pair in request.Environment)
        {
            if (!IsValidEnvironmentName(pair.Key) || pair.Value is null || pair.Value.Contains('\0') ||
                request.ToolKind == ScaleInvocationToolKind.Compiler &&
                !request.AllowPackageEnvironmentOverrides &&
                IsReservedCompilerEnvironment(pair.Key))
            {
                throw new ScaleConfigurationException($"The SCALE environment entry '{pair.Key}' is not valid.");
            }
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(24))
        {
            throw new ScaleConfigurationException("The SCALE timeout must be greater than zero and no longer than 24 hours.");
        }

        if (request.ToolKind == ScaleInvocationToolKind.Compiler && request.TargetArgumentMode != ScaleTargetArgumentMode.None && request.Target is null)
        {
            throw new ScaleConfigurationException("An explicit SCALE GPU target is required for the selected target argument mode.");
        }

        if (request.ToolKind == ScaleInvocationToolKind.Utility && request.TargetArgumentMode != ScaleTargetArgumentMode.None)
        {
            throw new ScaleConfigurationException("Utility invocations must use the None target argument mode.");
        }

        var toolPath = ValidateToolPath(request);
        var workingDirectory = request.WorkingDirectory is null
            ? RequireAbsolutePath(Environment.CurrentDirectory, "The working directory must be absolute.")
            : RequireAbsolutePath(request.WorkingDirectory, "The working directory must be absolute.");
        if (request.ExecutionMode == ScaleExecutionMode.Wsl && !IsWindowsDriveAbsolutePath(workingDirectory))
        {
            throw new ScaleConfigurationException("WSL execution requires an absolute Windows working directory.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new ScaleConfigurationException($"The working directory does not exist: {workingDirectory}");
        }

        var outputPaths = ValidateOutputPaths(request.OutputPaths, request.ExecutionMode);
        foreach (var index in indexes)
        {
            if (request.ExecutionMode == ScaleExecutionMode.Wsl)
            {
                ValidateWslPathArgument(arguments[index]);
            }
        }

        if (request.ExecutionMode == ScaleExecutionMode.Wsl)
        {
            ValidateWslSettings(request);
        }

        var normalizedRequest = request with
        {
            ToolPath = toolPath,
            Arguments = Array.AsReadOnly(arguments),
            PathArgumentIndexes = Array.AsReadOnly(indexes),
            OutputPaths = outputPaths,
            WorkingDirectory = workingDirectory,
            Environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal)
        };
        return new ValidatedInvocation(normalizedRequest, workingDirectory, outputPaths);
    }

    private static void ValidateCompilationEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        if (environment is null)
        {
            throw new ScaleConfigurationException("The compiler environment cannot be null.");
        }

        foreach (var pair in environment)
        {
            if (!IsValidEnvironmentName(pair.Key) || pair.Value is null || pair.Value.Contains('\0') || IsReservedCompilerEnvironment(pair.Key))
            {
                throw new ScaleConfigurationException($"The compiler environment entry '{pair.Key}' is not valid.");
            }
        }
    }

    private static string ValidateToolPath(ScaleInvocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ToolPath) || request.ToolPath.Contains('\0'))
        {
            throw new ScaleConfigurationException("An absolute SCALE tool path is required.");
        }

        if (request.ExecutionMode == ScaleExecutionMode.Wsl && IsPosixAbsolutePath(request.ToolPath))
        {
            return request.ToolPath;
        }

        var absolutePath = RequireAbsolutePath(request.ToolPath, "The SCALE tool path must be absolute.");
        if (!File.Exists(absolutePath))
        {
            throw new ScaleConfigurationException($"The SCALE tool does not exist: {absolutePath}");
        }

        return absolutePath;
    }

    private static void ValidateWslSettings(ScaleInvocationRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ScaleConfigurationException("WSL execution requires a Windows host.");
        }

        if (string.IsNullOrWhiteSpace(request.WslDistribution) || request.WslDistribution.Contains('\0'))
        {
            throw new ScaleConfigurationException("A WSL distribution is required for WSL execution.");
        }

        if (string.IsNullOrWhiteSpace(request.WslExecutablePath) || request.WslExecutablePath.Contains('\0'))
        {
            throw new ScaleConfigurationException("The WSL executable path is required.");
        }

        if (!string.Equals(request.WslExecutablePath, "wsl.exe", StringComparison.OrdinalIgnoreCase))
        {
            var executablePath = RequireAbsolutePath(request.WslExecutablePath, "The WSL executable path must be absolute.");
            if (!File.Exists(executablePath))
            {
                throw new ScaleConfigurationException($"The WSL executable does not exist: {executablePath}");
            }
        }
    }

    private static ReadOnlyCollection<string> ValidateOutputPaths(
        IReadOnlyList<string> outputPaths,
        ScaleExecutionMode executionMode)
    {
        if (outputPaths is null)
        {
            throw new ScaleConfigurationException("The output path collection cannot be null.");
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var normalized = new List<string>(outputPaths.Count);
        foreach (var outputPath in outputPaths)
        {
            var absolutePath = RequireAbsolutePath(outputPath, "Each output path must be absolute.");
            EnsureOutputPath(absolutePath, executionMode);
            if (File.Exists(absolutePath))
            {
                throw new ScaleConfigurationException($"The output file already exists: {absolutePath}");
            }

            if (normalized.Contains(absolutePath, comparer))
            {
                throw new ScaleConfigurationException($"The output path is listed more than once: {absolutePath}");
            }

            normalized.Add(absolutePath);
        }

        return Array.AsReadOnly(normalized.ToArray());
    }

    private static void EnsureOutputPath(string outputPath, ScaleExecutionMode executionMode)
    {
        if (executionMode == ScaleExecutionMode.Wsl && !IsWindowsDriveAbsolutePath(outputPath))
        {
            throw new ScaleConfigurationException("WSL output paths must be absolute Windows paths.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new ScaleConfigurationException($"The output directory does not exist: {outputDirectory ?? outputPath}");
        }
    }

    private static void ValidateWslPathArgument(string argument)
    {
        var path = argument.StartsWith('@') ? argument[1..] : argument;
        if (!IsPosixAbsolutePath(path) && !IsWindowsDriveAbsolutePath(path))
        {
            throw new ScaleConfigurationException("WSL path arguments must be absolute Windows or POSIX paths.");
        }
    }

    private static bool IsReservedCompilerEnvironment(string name) => name switch
    {
        "CUDA_CXX" or "CUDACXX" or "CUDA_NVCC_EXECUTABLE" or "CUCC" or "NVCC_PREPEND_FLAGS" or "NVCC_APPEND_FLAGS" => true,
        _ => false
    };

    private static bool IsValidEnvironmentName(string name)
    {
        if (string.IsNullOrEmpty(name) || !(name[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
        {
            return false;
        }

        return name[1..].All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    private static string RequireAbsolutePath(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') ||
            (!Path.IsPathFullyQualified(path) && !IsPosixAbsolutePath(path)))
        {
            throw new ScaleConfigurationException(message);
        }

        return IsPosixAbsolutePath(path) ? path : Path.GetFullPath(path);
    }

    private static bool IsWindowsDriveAbsolutePath(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static bool IsPosixAbsolutePath(string path) => path.StartsWith('/');

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ReadStandardOutputAsync(
        StreamReader reader,
        ScaleProcessInvocation invocation,
        TaskCompletionSource<int>? linuxPidSource)
    {
        if (invocation.WslPidMarker is null || linuxPidSource is null)
        {
            return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var firstLine = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        if (firstLine is not null &&
            firstLine.StartsWith(invocation.WslPidMarker, StringComparison.Ordinal) &&
            int.TryParse(
                firstLine[invocation.WslPidMarker.Length..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var linuxPid) &&
            linuxPid > 0)
        {
            linuxPidSource.TrySetResult(linuxPid);
            return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        }

        linuxPidSource.TrySetResult(0);
        var remaining = await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        return firstLine is null ? remaining : $"{firstLine}\n{remaining}";
    }

    private static async Task<Diagnostics> ReadDiagnosticsAsync(
        Task<string> standardOutputTask,
        Task<string> standardErrorTask,
        TimeSpan? timeout = null)
    {
        var allDiagnostics = Task.WhenAll(standardOutputTask, standardErrorTask);
        if (timeout is null)
        {
            await allDiagnostics.ConfigureAwait(false);
        }
        else if (await Task.WhenAny(allDiagnostics, Task.Delay(timeout.Value)).ConfigureAwait(false) != allDiagnostics)
        {
            return new Diagnostics(
                standardOutputTask.IsCompletedSuccessfully ? standardOutputTask.Result : string.Empty,
                standardErrorTask.IsCompletedSuccessfully ? standardErrorTask.Result : string.Empty);
        }

        return new Diagnostics(standardOutputTask.Result, standardErrorTask.Result);
    }

    private static async Task<ScaleProcessCleanupException?> StopProcessAsync(
        Process process,
        ScaleProcessInvocation invocation,
        TaskCompletionSource<int>? linuxPidSource)
    {
        ScaleProcessCleanupException? cleanupFailure = null;
        if (invocation.WslDistribution is not null && linuxPidSource is not null)
        {
            var linuxPid = await WaitForLinuxPidAsync(linuxPidSource).ConfigureAwait(false);
            if (linuxPid > 0)
            {
                await SendWslSignalAsync(invocation, linuxPid, "-TERM").ConfigureAwait(false);
                await WaitForExitBoundedAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var groupState = await ProbeWslProcessGroupAsync(invocation, linuxPid).ConfigureAwait(false);
                if (groupState == WslProcessGroupState.Present)
                {
                    await SendWslSignalAsync(invocation, linuxPid, "-KILL").ConfigureAwait(false);
                    if (!await WaitForWslProcessGroupAbsentAsync(invocation, linuxPid).ConfigureAwait(false))
                    {
                        cleanupFailure = new ScaleProcessCleanupException(
                            "The SCALE Linux process group remained after KILL.",
                            linuxPid,
                            invocation.WslDistribution);
                    }
                }
                else if (groupState == WslProcessGroupState.Unknown)
                {
                    cleanupFailure = new ScaleProcessCleanupException(
                        "The SCALE Linux process group absence could not be confirmed.",
                        linuxPid,
                        invocation.WslDistribution);
                }
            }
            else
            {
                cleanupFailure = new ScaleProcessCleanupException(
                    "The SCALE Linux process group identifier was not received.",
                    0,
                    invocation.WslDistribution);
            }
        }

        if (!process.HasExited)
        {
            KillProcessTree(process);
            await WaitForExitBoundedAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        return cleanupFailure;
    }

    private static async Task<int> WaitForLinuxPidAsync(TaskCompletionSource<int> linuxPidSource)
    {
        var completed = await Task.WhenAny(linuxPidSource.Task, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
        return completed == linuxPidSource.Task ? await linuxPidSource.Task.ConfigureAwait(false) : 0;
    }

    private static Task<WslControlResult> SendWslSignalAsync(
        ScaleProcessInvocation invocation,
        int linuxPid,
        string signal) => RunWslControlAsync(invocation, "/bin/kill", signal, "--", $"-{linuxPid}");

    private static async Task<WslProcessGroupState> ProbeWslProcessGroupAsync(
        ScaleProcessInvocation invocation,
        int linuxPid)
    {
        var result = await RunWslControlAsync(invocation, "/bin/kill", "-0", "--", $"-{linuxPid}").ConfigureAwait(false);
        if (!result.Started || result.TimedOut)
        {
            return WslProcessGroupState.Unknown;
        }

        return result.ExitCode switch
        {
            0 => WslProcessGroupState.Present,
            1 => WslProcessGroupState.Absent,
            _ => WslProcessGroupState.Unknown
        };
    }

    private static async Task<bool> WaitForWslProcessGroupAbsentAsync(
        ScaleProcessInvocation invocation,
        int linuxPid)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.0);
        while (true)
        {
            var state = await ProbeWslProcessGroupAsync(invocation, linuxPid).ConfigureAwait(false);
            if (state == WslProcessGroupState.Absent)
            {
                return true;
            }

            if (state == WslProcessGroupState.Unknown || Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private static async Task<WslControlResult> RunWslControlAsync(
        ScaleProcessInvocation invocation,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ProcessPath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--distribution");
        startInfo.ArgumentList.Add(invocation.WslDistribution!);
        startInfo.ArgumentList.Add("--exec");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var controlProcess = new Process { StartInfo = startInfo };
        try
        {
            if (!controlProcess.Start())
            {
                return new WslControlResult(false, false, -1);
            }

            var standardOutputTask = controlProcess.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardErrorTask = controlProcess.StandardError.ReadToEndAsync(CancellationToken.None);
            if (!await WaitForExitBoundedAsync(controlProcess, TimeSpan.FromSeconds(1)).ConfigureAwait(false))
            {
                KillProcessTree(controlProcess);
                await WaitForExitBoundedAsync(controlProcess, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                return new WslControlResult(true, true, -1);
            }

            await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return new WslControlResult(true, false, controlProcess.ExitCode);
        }
        catch (InvalidOperationException)
        {
            return new WslControlResult(false, false, -1);
        }
        catch (Win32Exception)
        {
            return new WslControlResult(false, false, -1);
        }
    }

    private static async Task<bool> WaitForExitBoundedAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static ScaleOutputCleanupException? DeleteFailedOutputs(IReadOnlyList<string> outputPaths)
    {
        var failedPaths = new List<string>();
        foreach (var outputPath in outputPaths)
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch (IOException)
            {
                failedPaths.Add(outputPath);
            }
            catch (UnauthorizedAccessException)
            {
                failedPaths.Add(outputPath);
            }
        }

        return failedPaths.Count == 0
            ? null
            : new ScaleOutputCleanupException(
                "One or more declared SCALE outputs could not be removed.",
                Array.AsReadOnly(failedPaths.ToArray()));
    }

    private static Exception? CombineCleanupFailures(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return new AggregateException(first, second);
    }

    private static string? FirstOutput(IReadOnlyList<string> outputPaths) =>
        outputPaths.Count == 0 ? null : outputPaths[0];

    private enum WslProcessGroupState
    {
        Present,
        Absent,
        Unknown
    }

    private readonly record struct WslControlResult(bool Started, bool TimedOut, int ExitCode);

    private sealed record ValidatedInvocation(
        ScaleInvocationRequest Request,
        string WorkingDirectory,
        IReadOnlyList<string> OutputPaths);

    private sealed record ValidatedCompilation(
        string SourcePath,
        string OutputPath,
        ScaleGpuTarget Target,
        ScaleInvocationRequest InvocationRequest);

    private sealed record Diagnostics(string StandardOutput, string StandardError);
}
