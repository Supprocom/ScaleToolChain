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
    AssertEqual("sm_86", ScaleGpuTarget.Nvidia("sm_86").Architecture);
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Amd("sm_86"));
    AssertThrowsSync<ArgumentException>(() => ScaleGpuTarget.Nvidia("gfx1201"));
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
        "--distribution", "Ubuntu-24.04", "--cd", "/mnt/d/Temp Folder", "--", "/usr/bin/env",
        "ALPHA=one", "ZED=two words", "/opt/scale/llvm/bin/nvcc", "--require-scale",
        "--gpu-architecture=gfx1201", "-c", "/mnt/d/Temp Folder/input.cu", "-o", "/mnt/d/Temp Folder/output.o");
    AssertEqual("/mnt/d/Temp Folder/input.cu", ScaleCommandBuilder.WindowsToWslPath(@"D:\Temp Folder\input.cu"));
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

    var outputPath = NewOutputPath("real-gfx1201.o");
    var result = await ScaleCompiler.CompileAsync(new ScaleCompilationRequest
    {
        SourcePath = sourcePath,
        OutputPath = outputPath,
        Settings = new ScaleCompilationSettings
        {
            CompilerPath = compiler,
            Target = ScaleGpuTarget.Amd("gfx1201"),
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
    Console.WriteLine("PASS real WSL SCALE gate");
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
