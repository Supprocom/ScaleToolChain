using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Supprocom.ScaleToolChain;

var testRoot = Environment.GetEnvironmentVariable("SCALE_TEST_ROOT")
    ?? Path.Combine(Path.GetTempPath(), "Supprocom.ScaleToolChain.Tests");
Directory.CreateDirectory(testRoot);
var fakeCompilerPath = Environment.GetEnvironmentVariable("FAKE_SCALE_COMPILER")
    ?? throw new InvalidOperationException("FAKE_SCALE_COMPILER is required.");
var sourcePath = Path.Combine(testRoot, "input.cu");
await File.WriteAllTextAsync(
    sourcePath,
    "extern \"C\" __global__ void add_one(const int* input, int* output) { output[0] = input[0] + 1; }\n",
    Encoding.UTF8);

var passed = 0;
await RunAsync("target validation", TargetValidationAsync);
await RunAsync("command construction", CommandConstructionAsync);
await RunAsync("successful compilation", SuccessfulCompilationAsync);
await RunAsync("failed compilation diagnostics", FailedCompilationAsync);
await RunAsync("timeout process cleanup", TimeoutCleanupAsync);
await RunAsync("configuration validation", ConfigurationValidationAsync);
await RunOptionalRealWslGateAsync();
await RunOptionalRealWslNvidiaGateAsync();
await RunOptionalRealWslTimeoutAsync();
await RunOptionalRealWslResistantTimeoutAsync();
Console.WriteLine($"Passed {passed} test groups.");

return 0;

async Task RunAsync(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

Task TargetValidationAsync()
{
    AssertEqual("gfx1201", ScaleGpuTarget.Amd("gfx1201").Architecture);
    AssertEqual("gfx90a", ScaleGpuTarget.Amd("gfx90a").Architecture);
    AssertEqual("sm_86", ScaleGpuTarget.Nvidia("sm_86").Architecture);
    AssertEqual("sm_90a", ScaleGpuTarget.Nvidia("sm_90a").Architecture);
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Amd("sm_86"));
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Nvidia("gfx1201"));
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Amd("gfx90-a"));
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Amd("gfxabc"));
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Nvidia("sm_90a/extra"));
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Amd("--gpu-architecture=gfx90a"));
    return Task.CompletedTask;
}

Task CommandConstructionAsync()
{
    var settings = new ScaleCompilationSettings
    {
        CompilerPath = "/opt/scale/llvm/bin/nvcc",
        Target = ScaleGpuTarget.Amd("gfx1201"),
        ExecutionMode = ScaleExecutionMode.Wsl,
        WslDistribution = "Ubuntu-24.04",
        Environment = new Dictionary<string, string>
        {
            ["ZED"] = "two words",
            ["ALPHA"] = "one"
        }
    };
    var invocation = ScaleCommandBuilder.Build(
        settings.CompilerPath,
        settings.Target,
        @"D:\Temp Folder\input.cu",
        @"D:\Temp Folder\output.o",
        @"D:\Temp Folder",
        settings);

    AssertEqual("wsl.exe", invocation.ProcessPath);
    AssertSequence(
        invocation.Arguments,
        "--distribution", "Ubuntu-24.04", "--cd", "/mnt/d/Temp Folder", "--exec", "/usr/bin/setsid", "--wait",
        "/bin/sh", "-c", "printf '__SCALE_TOOLCHAIN_PID__:%s\\n' \"$$\"; exec /usr/bin/env \"$@\"", "scale-toolchain",
        "ALPHA=one", "ZED=two words", "/opt/scale/llvm/bin/nvcc", "--require-scale",
        "--gpu-architecture=gfx1201", "-c", "/mnt/d/Temp Folder/input.cu", "-o", "/mnt/d/Temp Folder/output.o");
    AssertEqual("/mnt/d/Temp Folder/input.cu", ScaleCommandBuilder.WindowsToWslPath(@"D:\Temp Folder\input.cu"));
    AssertEqual(ScaleCommandBuilder.WslPidMarker, invocation.WslPidMarker);

    var nvidiaInvocation = ScaleCommandBuilder.Build(
        settings.CompilerPath,
        ScaleGpuTarget.Nvidia("sm_86"),
        @"D:\Temp Folder\input.cu",
        @"D:\Temp Folder\output.o",
        @"D:\Temp Folder",
        settings);
    AssertEqual("--gpu-architecture=sm_86", nvidiaInvocation.Arguments[15]);

    var configuredSettings = settings with
    {
        CudaToolkitPath = "/usr/local/cuda-12.9",
        IncludePaths = new[] { "/opt/include" },
        Definitions = new Dictionary<string, string>
        {
            ["ZED_DEFINE"] = "two words",
            ["ALPHA_DEFINE"] = "1"
        }
    };
    var configuredInvocation = ScaleCommandBuilder.Build(
        configuredSettings.CompilerPath,
        configuredSettings.Target,
        @"D:\Temp Folder\input.cu",
        @"D:\Temp Folder\output.o",
        @"D:\Temp Folder",
        configuredSettings);
    AssertSequence(
        configuredInvocation.Arguments.Skip(13).ToArray(),
        "/opt/scale/llvm/bin/nvcc", "--cuda-path=/usr/local/cuda-12.9", "-I", "/opt/include",
        "-D", "ALPHA_DEFINE=1", "-D", "ZED_DEFINE=two words", "--require-scale", "--gpu-architecture=gfx1201",
        "-c", "/mnt/d/Temp Folder/input.cu", "-o", "/mnt/d/Temp Folder/output.o");
    return Task.CompletedTask;
}

async Task SuccessfulCompilationAsync()
{
    var outputPath = NewOutputPath("success.o");
    var argumentsPath = Path.Combine(testRoot, "success-arguments.txt");
    var result = await CompileAsync(outputPath, "success", TimeSpan.FromSeconds(10), argumentsPath: argumentsPath);
    Assert(result.Succeeded, "The successful fake compile must succeed.");
    AssertEqual(0, result.ExitCode);
    AssertContains(result.StandardOutput, "fake standard output");
    AssertContains(result.StandardError, "fake standard error");
    Assert(File.Exists(outputPath), "The successful compile must create output.");
    Assert(!string.IsNullOrWhiteSpace(result.OutputSha256), "The successful compile must return an output hash.");
    AssertEqual(fakeCompilerPath, result.ProcessPath);
    AssertSequence(result.Arguments, "--require-scale", "--gpu-architecture=gfx1201", "-c", sourcePath, "-o", outputPath);
    var recordedArguments = await File.ReadAllLinesAsync(argumentsPath, Encoding.UTF8);
    AssertSequence(recordedArguments, result.Arguments.ToArray());
    AssertEqual(Hash(sourcePath), result.SourceSha256);
}

async Task FailedCompilationAsync()
{
    var outputPath = NewOutputPath("failed.o");
    var result = await CompileAsync(outputPath, "fail", TimeSpan.FromSeconds(10));
    Assert(!result.Succeeded, "The failing fake compile must report failure.");
    AssertEqual(7, result.ExitCode);
    AssertContains(result.StandardError, "fake standard error");
    Assert(!File.Exists(outputPath), "A failed compile must remove partial output.");
}

async Task TimeoutCleanupAsync()
{
    var outputPath = NewOutputPath("timeout.o");
    var childMarker = Path.Combine(testRoot, "child-process-id.txt");
    var exception = await AssertThrowsAsync<ScaleCompilationTimeoutException>(
        () => CompileAsync(
            outputPath,
            "sleep",
            TimeSpan.FromMilliseconds(300),
            new Dictionary<string, string> { ["FAKE_SCALE_CHILD_MARKER"] = childMarker }));
    AssertEqual(TimeSpan.FromMilliseconds(300), exception.Timeout);
    Assert(!File.Exists(outputPath), "A timed-out compile must remove output.");
    for (var attempt = 0; attempt < 10 && !File.Exists(childMarker); attempt++)
    {
        await Task.Delay(100);
    }

    if (File.Exists(childMarker) && int.TryParse(await File.ReadAllTextAsync(childMarker, Encoding.UTF8), out var childId))
    {
        Assert(!IsRunning(childId), "The timed-out compiler child process must stop.");
    }
}

Task ConfigurationValidationAsync()
{
    var outputPath = NewOutputPath("config.o");
    AssertThrows<ScaleConfigurationException>(() => ScaleCompiler.CompileAsync(
        new ScaleCompilationRequest
        {
            SourcePath = "relative.cu",
            OutputPath = outputPath,
            Settings = Settings("missing")
        }));
    AssertThrows<ScaleConfigurationException>(() => ScaleCompiler.CompileAsync(
        new ScaleCompilationRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Settings = new ScaleCompilationSettings
            {
                CompilerPath = fakeCompilerPath,
                Target = ScaleGpuTarget.Amd("gfx1201"),
                Environment = new Dictionary<string, string> { ["BAD-NAME"] = "value" }
            }
        }));
    AssertThrows<ScaleConfigurationException>(() => ScaleCompiler.CompileAsync(
        new ScaleCompilationRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Settings = new ScaleCompilationSettings
            {
                CompilerPath = "/opt/scale/llvm/bin/nvcc",
                Target = ScaleGpuTarget.Amd("gfx1201"),
                ExecutionMode = ScaleExecutionMode.Wsl
            }
        }));
    AssertThrows<ScaleConfigurationException>(() => ScaleCompiler.CompileAsync(
        new ScaleCompilationRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Settings = new ScaleCompilationSettings
            {
                CompilerPath = fakeCompilerPath,
                Target = ScaleGpuTarget.Amd("gfx1201"),
                Environment = new Dictionary<string, string> { ["NVCC_PREPEND_FLAGS"] = "-o unsafe.o" }
            }
        }));
    return Task.CompletedTask;
}

async Task RunOptionalRealWslGateAsync()
{
    var distribution = Environment.GetEnvironmentVariable("SCALE_REAL_WSL_DISTRIBUTION");
    var compiler = Environment.GetEnvironmentVariable("SCALE_REAL_WSL_COMPILER");
    if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(distribution) || string.IsNullOrWhiteSpace(compiler))
    {
        Console.WriteLine("SKIP real WSL SCALE gate");
        return;
    }

    await RunRealWslGateAsync(distribution, compiler, "gfx1201");
    await RunRealWslGateAsync(distribution, compiler, "gfx90a");
}

async Task RunRealWslGateAsync(string distribution, string compiler, string architecture)
{
    var outputPath = NewOutputPath($"real-{architecture}.o");
    var result = await ScaleCompiler.CompileAsync(new ScaleCompilationRequest
    {
        SourcePath = sourcePath,
        OutputPath = outputPath,
        Settings = new ScaleCompilationSettings
        {
            CompilerPath = compiler,
            Target = ScaleGpuTarget.Amd(architecture),
            ExecutionMode = ScaleExecutionMode.Wsl,
            WslDistribution = distribution,
            Timeout = TimeSpan.FromMinutes(2),
            Environment = new Dictionary<string, string>
            {
                ["PATH"] = "/opt/scale/bin:/opt/scale/llvm/bin:/usr/local/bin:/usr/bin:/bin"
            }
        }
    });
    Assert(result.Succeeded, "The real WSL SCALE gate must succeed when enabled.");
    Assert(File.Exists(outputPath), "The real WSL SCALE gate must create output.");
    Assert(!string.IsNullOrWhiteSpace(result.OutputSha256), "The real WSL SCALE gate must return an output hash.");
    passed++;
    Console.WriteLine($"PASS real WSL SCALE gate {architecture}");
}

async Task RunOptionalRealWslNvidiaGateAsync()
{
    var distribution = Environment.GetEnvironmentVariable("SCALE_REAL_WSL_DISTRIBUTION");
    var compiler = Environment.GetEnvironmentVariable("SCALE_REAL_WSL_COMPILER");
    if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(distribution) || string.IsNullOrWhiteSpace(compiler))
    {
        Console.WriteLine("SKIP real NVIDIA SCALE gate");
        return;
    }

    var outputPath = NewOutputPath("real-sm_86.o");
    var result = await ScaleCompiler.CompileAsync(new ScaleCompilationRequest
    {
        SourcePath = sourcePath,
        OutputPath = outputPath,
        Settings = new ScaleCompilationSettings
        {
            CompilerPath = compiler,
            Target = ScaleGpuTarget.Nvidia("sm_86"),
            ExecutionMode = ScaleExecutionMode.Wsl,
            WslDistribution = distribution,
            CudaToolkitPath = "/usr/local/cuda-12.9",
            Timeout = TimeSpan.FromMinutes(2),
            Environment = new Dictionary<string, string>
            {
                ["PATH"] = "/usr/local/bin:/usr/bin:/bin"
            }
        }
    });
    Assert(result.Succeeded, "The real NVIDIA SCALE gate must succeed when enabled.");
    Assert(File.Exists(outputPath), "The real NVIDIA SCALE gate must create output.");
    Assert(!string.IsNullOrWhiteSpace(result.OutputSha256), "The real NVIDIA SCALE gate must return an output hash.");
    passed++;
    Console.WriteLine("PASS real NVIDIA SCALE gate sm_86");
}

async Task RunOptionalRealWslTimeoutAsync()
{
    var distribution = Environment.GetEnvironmentVariable("SCALE_REAL_WSL_DISTRIBUTION");
    if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(distribution))
    {
        Console.WriteLine("SKIP real WSL timeout gate");
        return;
    }

    var timeoutScriptPath = Path.Combine(testRoot, "wsl-timeout-compiler.sh");
    var unrelatedScriptPath = Path.Combine(testRoot, "wsl-unrelated-process.sh");
    var compilerPidPath = Path.Combine(testRoot, "wsl-compiler-pid.txt");
    var childPidPath = Path.Combine(testRoot, "wsl-child-pid.txt");
    var unrelatedPidPath = Path.Combine(testRoot, "wsl-unrelated-pid.txt");
    var outputPath = NewOutputPath("wsl-timeout.o");
    await File.WriteAllTextAsync(
        timeoutScriptPath,
        "#!/bin/sh\n" +
        "set -u\n" +
        "output=''\n" +
        "while [ \"$#\" -gt 0 ]; do\n" +
        "  if [ \"$1\" = \"-o\" ]; then output=\"$2\"; shift 2; else shift; fi\n" +
        "done\n" +
        "printf '%s\\n' \"$$\" > \"$SCALE_TEST_PID_FILE\"\n" +
        "(sleep 600) &\n" +
        "child=\"$!\"\n" +
        "printf '%s\\n' \"$child\" > \"$SCALE_CHILD_PID_FILE\"\n" +
        "trap 'exit 143' TERM INT\n" +
        "sleep 3\n" +
        "printf 'late output' > \"$output\"\n" +
        "sleep 600\n",
        new UTF8Encoding(false));
    await File.WriteAllTextAsync(
        unrelatedScriptPath,
        "#!/bin/sh\n" +
        "printf '%s\\n' \"$$\" > \"$SCALE_UNRELATED_PID_FILE\"\n" +
        "sleep 600\n",
        new UTF8Encoding(false));
    var timeoutScriptWslPath = ScaleCommandBuilder.WindowsToWslPath(timeoutScriptPath);
    var unrelatedScriptWslPath = ScaleCommandBuilder.WindowsToWslPath(unrelatedScriptPath);
    var compilerPidWslPath = ScaleCommandBuilder.WindowsToWslPath(compilerPidPath);
    var childPidWslPath = ScaleCommandBuilder.WindowsToWslPath(childPidPath);
    var unrelatedPidWslPath = ScaleCommandBuilder.WindowsToWslPath(unrelatedPidPath);
    await RunWslCommandAsync(distribution, "/bin/chmod", "+x", timeoutScriptWslPath);
    await RunWslCommandAsync(distribution, "/bin/chmod", "+x", unrelatedScriptWslPath);

    using var unrelatedProcess = StartWslProcess(
        distribution,
        "/usr/bin/env",
        $"SCALE_UNRELATED_PID_FILE={unrelatedPidWslPath}",
        unrelatedScriptWslPath);
    await WaitForFileAsync(unrelatedPidPath);
    var unrelatedPid = int.Parse(
        await File.ReadAllTextAsync(unrelatedPidPath, Encoding.UTF8),
        System.Globalization.CultureInfo.InvariantCulture);
    Assert(await IsWslProcessRunningAsync(distribution, unrelatedPid), "The unrelated WSL process must start.");

    var exception = await AssertThrowsAsync<ScaleCompilationTimeoutException>(() => ScaleCompiler.CompileAsync(
        new ScaleCompilationRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Settings = new ScaleCompilationSettings
            {
                CompilerPath = timeoutScriptWslPath,
                Target = ScaleGpuTarget.Amd("gfx1201"),
                ExecutionMode = ScaleExecutionMode.Wsl,
                WslDistribution = distribution,
                Timeout = TimeSpan.FromMilliseconds(500),
                Environment = new Dictionary<string, string>
                {
                    ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
                    ["SCALE_TEST_PID_FILE"] = compilerPidWslPath,
                    ["SCALE_CHILD_PID_FILE"] = childPidWslPath
                }
            }
        }));
    AssertEqual(TimeSpan.FromMilliseconds(500), exception.Timeout);
    Assert(exception.CleanupFailure is null, "The normal WSL cleanup must confirm group absence.");
    await WaitForFileAsync(compilerPidPath);
    await WaitForFileAsync(childPidPath);
    var compilerPid = int.Parse(
        await File.ReadAllTextAsync(compilerPidPath, Encoding.UTF8),
        System.Globalization.CultureInfo.InvariantCulture);
    var childPid = int.Parse(
        await File.ReadAllTextAsync(childPidPath, Encoding.UTF8),
        System.Globalization.CultureInfo.InvariantCulture);
    await Task.Delay(500);
    Assert(!await IsWslProcessRunningAsync(distribution, compilerPid), "The timed-out WSL compiler must stop.");
    Assert(!await IsWslProcessRunningAsync(distribution, childPid), "The timed-out WSL child must stop.");
    Assert(await IsWslProcessRunningAsync(distribution, unrelatedPid), "The unrelated WSL process must remain active.");
    await Task.Delay(3500);
    Assert(!File.Exists(outputPath), "A stopped WSL compiler must not create output later.");
    await RunWslCommandAsync(distribution, "/bin/kill", "-TERM", unrelatedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    passed++;
    Console.WriteLine("PASS real WSL timeout gate");
}

async Task RunOptionalRealWslResistantTimeoutAsync()
{
    var distribution = Environment.GetEnvironmentVariable("SCALE_REAL_WSL_DISTRIBUTION");
    if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(distribution))
    {
        Console.WriteLine("SKIP real WSL TERM-resistant timeout gate");
        return;
    }

    var timeoutScriptPath = Path.Combine(testRoot, "wsl-term-resistant-compiler.sh");
    var unrelatedScriptPath = Path.Combine(testRoot, "wsl-term-resistant-unrelated.sh");
    var compilerPidPath = Path.Combine(testRoot, "wsl-term-resistant-compiler-pid.txt");
    var childPidPath = Path.Combine(testRoot, "wsl-term-resistant-child-pid.txt");
    var unrelatedPidPath = Path.Combine(testRoot, "wsl-term-resistant-unrelated-pid.txt");
    var outputPath = NewOutputPath("wsl-term-resistant-timeout.o");
    await File.WriteAllTextAsync(
        timeoutScriptPath,
        "#!/bin/sh\n" +
        "set -u\n" +
        "output=''\n" +
        "while [ \"$#\" -gt 0 ]; do\n" +
        "  if [ \"$1\" = \"-o\" ]; then output=\"$2\"; shift 2; else shift; fi\n" +
        "done\n" +
        "printf '%s\\n' \"$$\" > \"$SCALE_TEST_PID_FILE\"\n" +
        "(trap '' TERM INT; sleep 600) &\n" +
        "child=\"$!\"\n" +
        "printf '%s\\n' \"$child\" > \"$SCALE_CHILD_PID_FILE\"\n" +
        "trap 'exit 143' TERM INT\n" +
        "sleep 3\n" +
        "printf 'late output' > \"$output\"\n" +
        "sleep 600\n",
        new UTF8Encoding(false));
    await File.WriteAllTextAsync(
        unrelatedScriptPath,
        "#!/bin/sh\n" +
        "printf '%s\\n' \"$$\" > \"$SCALE_UNRELATED_PID_FILE\"\n" +
        "sleep 600\n",
        new UTF8Encoding(false));
    var timeoutScriptWslPath = ScaleCommandBuilder.WindowsToWslPath(timeoutScriptPath);
    var unrelatedScriptWslPath = ScaleCommandBuilder.WindowsToWslPath(unrelatedScriptPath);
    var compilerPidWslPath = ScaleCommandBuilder.WindowsToWslPath(compilerPidPath);
    var childPidWslPath = ScaleCommandBuilder.WindowsToWslPath(childPidPath);
    var unrelatedPidWslPath = ScaleCommandBuilder.WindowsToWslPath(unrelatedPidPath);
    await RunWslCommandAsync(distribution, "/bin/chmod", "+x", timeoutScriptWslPath);
    await RunWslCommandAsync(distribution, "/bin/chmod", "+x", unrelatedScriptWslPath);

    using var unrelatedProcess = StartWslProcess(
        distribution,
        "/usr/bin/env",
        $"SCALE_UNRELATED_PID_FILE={unrelatedPidWslPath}",
        unrelatedScriptWslPath);
    await WaitForFileAsync(unrelatedPidPath);
    var unrelatedPid = int.Parse(
        await File.ReadAllTextAsync(unrelatedPidPath, Encoding.UTF8),
        System.Globalization.CultureInfo.InvariantCulture);
    Assert(await IsWslProcessRunningAsync(distribution, unrelatedPid), "The unrelated WSL process must start.");

    var exception = await AssertThrowsAsync<ScaleCompilationTimeoutException>(() => ScaleCompiler.CompileAsync(
        new ScaleCompilationRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Settings = new ScaleCompilationSettings
            {
                CompilerPath = timeoutScriptWslPath,
                Target = ScaleGpuTarget.Amd("gfx1201"),
                ExecutionMode = ScaleExecutionMode.Wsl,
                WslDistribution = distribution,
                Timeout = TimeSpan.FromMilliseconds(500),
                Environment = new Dictionary<string, string>
                {
                    ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
                    ["SCALE_TEST_PID_FILE"] = compilerPidWslPath,
                    ["SCALE_CHILD_PID_FILE"] = childPidWslPath
                }
            }
        }));
    AssertEqual(TimeSpan.FromMilliseconds(500), exception.Timeout);
    Assert(exception.CleanupFailure is null, "The TERM-resistant WSL cleanup must confirm group absence.");
    await WaitForFileAsync(compilerPidPath);
    await WaitForFileAsync(childPidPath);
    var compilerPid = int.Parse(
        await File.ReadAllTextAsync(compilerPidPath, Encoding.UTF8),
        System.Globalization.CultureInfo.InvariantCulture);
    var childPid = int.Parse(
        await File.ReadAllTextAsync(childPidPath, Encoding.UTF8),
        System.Globalization.CultureInfo.InvariantCulture);
    await Task.Delay(500);
    Assert(!await IsWslProcessRunningAsync(distribution, compilerPid), "The TERM-resistant WSL compiler must stop.");
    Assert(!await IsWslProcessRunningAsync(distribution, childPid), "The TERM-resistant WSL child must stop.");
    Assert(await IsWslProcessRunningAsync(distribution, unrelatedPid), "The unrelated WSL process must remain active.");
    await Task.Delay(3500);
    Assert(!File.Exists(outputPath), "A stopped TERM-resistant compiler must not create output later.");
    await RunWslCommandAsync(distribution, "/bin/kill", "-TERM", unrelatedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    passed++;
    Console.WriteLine("PASS real WSL TERM-resistant timeout gate");
}

async Task WaitForFileAsync(string path)
{
    for (var attempt = 0; attempt < 20 && !File.Exists(path); attempt++)
    {
        await Task.Delay(100);
    }

    Assert(File.Exists(path), $"Expected the process marker file: {path}");
}

Process StartWslProcess(string distribution, params string[] command)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "wsl.exe",
        WorkingDirectory = testRoot,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("--distribution");
    startInfo.ArgumentList.Add(distribution);
    startInfo.ArgumentList.Add("--exec");
    foreach (var argument in command)
    {
        startInfo.ArgumentList.Add(argument);
    }
    return Process.Start(startInfo) ?? throw new InvalidOperationException("The WSL process did not start.");
}

async Task<(int ExitCode, string StandardOutput, string StandardError)> RunWslCommandAsync(
    string distribution,
    params string[] command)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "wsl.exe",
        WorkingDirectory = testRoot,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("--distribution");
    startInfo.ArgumentList.Add(distribution);
    startInfo.ArgumentList.Add("--exec");
    foreach (var argument in command)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The WSL command did not start.");
    var standardOutputTask = process.StandardOutput.ReadToEndAsync();
    var standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await standardOutputTask, await standardErrorTask);
}

async Task<bool> IsWslProcessRunningAsync(string distribution, int processId)
{
    var result = await RunWslCommandAsync(
        distribution,
        "/bin/ps",
        "-p",
        processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-o",
        "stat=");
    var state = result.StandardOutput.Trim();
    return state.Length > 0 && !state.StartsWith('Z');
}

async Task<ScaleCompilationResult> CompileAsync(
    string outputPath,
    string mode,
    TimeSpan timeout,
    IReadOnlyDictionary<string, string>? environment = null,
    string? argumentsPath = null)
{
    var values = new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal)
    {
        ["FAKE_SCALE_MODE"] = mode
    };
    if (argumentsPath is not null)
    {
        values["FAKE_SCALE_ARGUMENTS_PATH"] = argumentsPath;
    }

    return await ScaleCompiler.CompileAsync(new ScaleCompilationRequest
    {
        SourcePath = sourcePath,
        OutputPath = outputPath,
        Settings = new ScaleCompilationSettings
        {
            CompilerPath = fakeCompilerPath,
            Target = ScaleGpuTarget.Amd("gfx1201"),
            Timeout = timeout,
            Environment = values
        }
    });
}

ScaleCompilationSettings Settings(string compiler) => new()
{
    CompilerPath = compiler,
    Target = ScaleGpuTarget.Amd("gfx1201")
};

string NewOutputPath(string name) => Path.Combine(testRoot, $"{Guid.NewGuid():N}-{name}");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static bool IsRunning(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}

static void AssertContains(string value, string expected)
{
    Assert(value.Contains(expected, StringComparison.Ordinal), $"Expected '{value}' to contain '{expected}'.");
}

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    AssertEqual(expected.Length, actual.Count);
    for (var index = 0; index < expected.Length; index++)
    {
        AssertEqual(expected[index], actual[index]);
    }
}

static void AssertThrows<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        action().GetAwaiter().GetResult();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void AssertThrowsSync<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
