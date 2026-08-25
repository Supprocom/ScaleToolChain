using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Supprocom.ScaleToolChain;

public static class ScaleCompiler
{
    public static async Task<ScaleCompilationResult> CompileAsync(
        ScaleCompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(request);
        var sourceSha256 = await ComputeSha256Async(validated.SourcePath, cancellationToken).ConfigureAwait(false);
        var invocation = ScaleCommandBuilder.Build(
            validated.CompilerPath,
            validated.Target,
            validated.SourcePath,
            validated.OutputPath,
            validated.WorkingDirectory,
            validated.Settings);
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
                    "The SCALE compiler process did not start.",
                    validated.SourcePath,
                    validated.OutputPath);
            }
        }
        catch (ScaleCompilationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new ScaleCompilationException(
                $"The SCALE compiler process could not start: {exception.Message}",
                validated.SourcePath,
                validated.OutputPath,
                innerException: exception);
        }

        var standardOutputTask = ReadStandardOutputAsync(process.StandardOutput, invocation, linuxPidSource);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(validated.Settings.Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var cleanupFailure = await StopProcessAsync(process, invocation, linuxPidSource).ConfigureAwait(false);
            var diagnostics = await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            DeleteFailedOutput(validated.OutputPath);
            throw new ScaleCompilationTimeoutException(
                validated.Settings.Timeout,
                validated.SourcePath,
                validated.OutputPath,
                diagnostics.StandardOutput,
                diagnostics.StandardError,
                exception,
                cleanupFailure);
        }
        catch (OperationCanceledException exception)
        {
            var cleanupFailure = await StopProcessAsync(process, invocation, linuxPidSource).ConfigureAwait(false);
            await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            DeleteFailedOutput(validated.OutputPath);
            if (cleanupFailure is not null)
            {
                exception.Data[ScaleCompilationException.CleanupFailureDataKey] = cleanupFailure;
            }

            throw;
        }

        var completedDiagnostics = await ReadDiagnosticsAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(start);
        var result = new ScaleCompilationResult
        {
            SourcePath = validated.SourcePath,
            OutputPath = validated.OutputPath,
            Target = validated.Target,
            ProcessPath = invocation.ProcessPath,
            Arguments = invocation.Arguments,
            ExitCode = process.ExitCode,
            Succeeded = process.ExitCode == 0,
            StandardOutput = completedDiagnostics.StandardOutput,
            StandardError = completedDiagnostics.StandardError,
            SourceSha256 = sourceSha256,
            Duration = duration
        };

        if (process.ExitCode != 0)
        {
            DeleteFailedOutput(validated.OutputPath);
            return result;
        }

        if (!File.Exists(validated.OutputPath))
        {
            throw new ScaleCompilationException(
                "The SCALE compiler exited successfully but did not create the requested output.",
                validated.SourcePath,
                validated.OutputPath,
                process.ExitCode,
                completedDiagnostics.StandardOutput,
                completedDiagnostics.StandardError);
        }

        var outputInfo = new FileInfo(validated.OutputPath);
        if (outputInfo.Length == 0)
        {
            DeleteFailedOutput(validated.OutputPath);
            throw new ScaleCompilationException(
                "The SCALE compiler created an empty output.",
                validated.SourcePath,
                validated.OutputPath,
                process.ExitCode,
                completedDiagnostics.StandardOutput,
                completedDiagnostics.StandardError);
        }

        return result with { OutputSha256 = await ComputeSha256Async(validated.OutputPath, cancellationToken).ConfigureAwait(false) };
    }

    private static ValidatedRequest Validate(ScaleCompilationRequest request)
    {
        if (request is null)
        {
            throw new ScaleConfigurationException("A compilation request is required.");
        }

        if (request.Settings is null)
        {
            throw new ScaleConfigurationException("Compilation settings are required.");
        }

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new ScaleConfigurationException("A CUDA source path is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new ScaleConfigurationException("An output path is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Settings.CompilerPath))
        {
            throw new ScaleConfigurationException("An absolute SCALE compiler path is required.");
        }

        if (request.Settings.CompilerPath.Contains('\0'))
        {
            throw new ScaleConfigurationException("The SCALE compiler path must not contain a null character.");
        }

        if (request.Settings.Target is null)
        {
            throw new ScaleConfigurationException("An explicit SCALE GPU target is required.");
        }

        if (!Enum.IsDefined(request.Settings.ExecutionMode))
        {
            throw new ScaleConfigurationException("The SCALE execution mode is not supported.");
        }

        if (request.Settings.Timeout <= TimeSpan.Zero || request.Settings.Timeout > TimeSpan.FromHours(24))
        {
            throw new ScaleConfigurationException("The compiler timeout must be greater than zero and no longer than 24 hours.");
        }

        ValidateEnvironment(request.Settings.Environment);
        var cudaToolkitPath = ValidateToolchainPath(
            request.Settings.CudaToolkitPath,
            request.Settings.ExecutionMode,
            "CUDA toolkit path");
        var includePaths = ValidateIncludePaths(request.Settings.IncludePaths, request.Settings.ExecutionMode);
        ValidateDefinitions(request.Settings.Definitions);

        var sourcePath = RequireAbsolutePath(request.SourcePath, "The CUDA source path must be absolute.");
        var outputPath = RequireAbsolutePath(request.OutputPath, "The output path must be absolute.");
        if (request.Settings.ExecutionMode == ScaleExecutionMode.Wsl &&
            (!IsWindowsDriveAbsolutePath(sourcePath) || !IsWindowsDriveAbsolutePath(outputPath)))
        {
            throw new ScaleConfigurationException("WSL execution requires absolute Windows source and output paths.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new ScaleConfigurationException($"The CUDA source file does not exist: {sourcePath}");
        }

        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length == 0)
        {
            throw new ScaleConfigurationException("The CUDA source file must not be empty.");
        }

        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScaleConfigurationException("The output path must differ from the CUDA source path.");
        }

        if (File.Exists(outputPath))
        {
            throw new ScaleConfigurationException($"The output file already exists: {outputPath}");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new ScaleConfigurationException($"The output directory does not exist: {outputDirectory ?? outputPath}");
        }

        var workingDirectory = request.Settings.WorkingDirectory is null
            ? Path.GetDirectoryName(sourcePath)!
            : RequireAbsolutePath(request.Settings.WorkingDirectory, "The working directory must be absolute.");
        if (request.Settings.ExecutionMode == ScaleExecutionMode.Wsl && !IsWindowsDriveAbsolutePath(workingDirectory))
        {
            throw new ScaleConfigurationException("WSL execution requires an absolute Windows working directory.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new ScaleConfigurationException($"The working directory does not exist: {workingDirectory}");
        }

        var compilerPath = ValidateCompilerPath(request.Settings);
        var settings = request.Settings with
        {
            CompilerPath = compilerPath,
            WorkingDirectory = workingDirectory,
            CudaToolkitPath = cudaToolkitPath,
            IncludePaths = includePaths
        };

        return new ValidatedRequest(sourcePath, outputPath, compilerPath, request.Settings.Target, workingDirectory, settings);
    }

    private static string ValidateCompilerPath(ScaleCompilationSettings settings)
    {
        if (settings.ExecutionMode == ScaleExecutionMode.Wsl)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new ScaleConfigurationException("WSL execution requires a Windows host.");
            }

            if (string.IsNullOrWhiteSpace(settings.WslDistribution))
            {
                throw new ScaleConfigurationException("A WSL distribution is required for WSL execution.");
            }

            if (settings.WslDistribution.Contains('\0'))
            {
                throw new ScaleConfigurationException("The WSL distribution must not contain a null character.");
            }

            if (string.IsNullOrWhiteSpace(settings.WslExecutablePath))
            {
                throw new ScaleConfigurationException("The WSL executable path is required.");
            }

            if (settings.WslExecutablePath.Contains('\0'))
            {
                throw new ScaleConfigurationException("The WSL executable path must not contain a null character.");
            }

            if (!string.Equals(settings.WslExecutablePath, "wsl.exe", StringComparison.OrdinalIgnoreCase))
            {
                var wslExecutablePath = RequireAbsolutePath(
                    settings.WslExecutablePath,
                    "The WSL executable path must be absolute when it is not wsl.exe.");
                if (!File.Exists(wslExecutablePath))
                {
                    throw new ScaleConfigurationException($"The WSL executable does not exist: {wslExecutablePath}");
                }
            }

            if (ScaleCommandBuilder.ToWslCompilerPath(settings.CompilerPath) == settings.CompilerPath)
            {
                return settings.CompilerPath;
            }

            var windowsCompilerPath = RequireAbsolutePath(
                settings.CompilerPath,
                "The SCALE compiler path must be absolute.");
            if (!IsWindowsDriveAbsolutePath(windowsCompilerPath))
            {
                throw new ScaleConfigurationException("The WSL compiler path must be an absolute POSIX or Windows path.");
            }

            if (!File.Exists(windowsCompilerPath))
            {
                throw new ScaleConfigurationException($"The SCALE compiler does not exist: {windowsCompilerPath}");
            }

            return windowsCompilerPath;
        }

        var compilerPath = RequireAbsolutePath(settings.CompilerPath, "The SCALE compiler path must be absolute.");
        if (!File.Exists(compilerPath))
        {
            throw new ScaleConfigurationException($"The SCALE compiler does not exist: {compilerPath}");
        }

        return compilerPath;
    }

    private static void ValidateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        if (environment is null)
        {
            throw new ScaleConfigurationException("The compiler environment cannot be null.");
        }

        foreach (var pair in environment)
        {
            if (!IsValidEnvironmentName(pair.Key) || IsReservedCompilerEnvironment(pair.Key) || pair.Value is null || pair.Value.Contains('\0'))
            {
                throw new ScaleConfigurationException($"The compiler environment entry '{pair.Key}' is not valid.");
            }
        }
    }

    private static bool IsReservedCompilerEnvironment(string name) => name switch
    {
        "CUDA_CXX" or "CUDACXX" or "CUDA_NVCC_EXECUTABLE" or "CUCC" or "NVCC_PREPEND_FLAGS" or "NVCC_APPEND_FLAGS" => true,
        _ => false
    };

    private static string? ValidateToolchainPath(
        string? path,
        ScaleExecutionMode executionMode,
        string settingName)
    {
        if (path is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new ScaleConfigurationException($"The {settingName} must be an absolute path.");
        }

        if (executionMode == ScaleExecutionMode.Wsl && IsPosixAbsolutePath(path))
        {
            return path;
        }

        var absolutePath = RequireAbsolutePath(path, $"The {settingName} must be an absolute path.");
        if (executionMode == ScaleExecutionMode.Wsl && !IsWindowsDriveAbsolutePath(absolutePath))
        {
            throw new ScaleConfigurationException($"The {settingName} must be an absolute POSIX or Windows path for WSL execution.");
        }

        if (!Directory.Exists(absolutePath))
        {
            throw new ScaleConfigurationException($"The {settingName} does not exist: {absolutePath}");
        }

        return absolutePath;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> ValidateIncludePaths(
        IReadOnlyList<string> includePaths,
        ScaleExecutionMode executionMode)
    {
        if (includePaths is null)
        {
            throw new ScaleConfigurationException("The include path collection cannot be null.");
        }

        var normalized = new List<string>(includePaths.Count);
        foreach (var includePath in includePaths)
        {
            normalized.Add(ValidateToolchainPath(includePath, executionMode, "include path")!);
        }

        return Array.AsReadOnly(normalized.ToArray());
    }

    private static void ValidateDefinitions(IReadOnlyDictionary<string, string> definitions)
    {
        if (definitions is null)
        {
            throw new ScaleConfigurationException("The definition collection cannot be null.");
        }

        foreach (var definition in definitions)
        {
            if (!IsValidEnvironmentName(definition.Key) || definition.Value is null || definition.Value.Contains('\0'))
            {
                throw new ScaleConfigurationException($"The definition '{definition.Key}' is not valid.");
            }
        }
    }

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
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            throw new ScaleConfigurationException(message);
        }

        return Path.GetFullPath(path);
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
        var completed = await Task.WhenAny(
            linuxPidSource.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
        return completed == linuxPidSource.Task ? await linuxPidSource.Task.ConfigureAwait(false) : 0;
    }

    private static async Task<WslControlResult> SendWslSignalAsync(
        ScaleProcessInvocation invocation,
        int linuxPid,
        string signal)
    {
        return await RunWslControlAsync(
            invocation,
            "/bin/kill",
            signal,
            "--",
            $"-{linuxPid}").ConfigureAwait(false);
    }

    private static async Task<WslProcessGroupState> ProbeWslProcessGroupAsync(
        ScaleProcessInvocation invocation,
        int linuxPid)
    {
        var result = await RunWslControlAsync(
            invocation,
            "/bin/kill",
            "-0",
            "--",
            $"-{linuxPid}").ConfigureAwait(false);
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

    private enum WslProcessGroupState
    {
        Present,
        Absent,
        Unknown
    }

    private readonly record struct WslControlResult(bool Started, bool TimedOut, int ExitCode);

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

    private static void DeleteFailedOutput(string outputPath)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ValidatedRequest(
        string SourcePath,
        string OutputPath,
        string CompilerPath,
        ScaleGpuTarget Target,
        string WorkingDirectory,
        ScaleCompilationSettings Settings);

    private sealed record Diagnostics(string StandardOutput, string StandardError);
}
