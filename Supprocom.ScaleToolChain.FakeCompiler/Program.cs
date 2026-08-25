using System.Diagnostics;
using System.Globalization;
using System.Text;

var mode = Environment.GetEnvironmentVariable("FAKE_SCALE_MODE") ?? "success";
var arguments = args.ToArray();
var argumentsPath = Environment.GetEnvironmentVariable("FAKE_SCALE_ARGUMENTS_PATH");
if (!string.IsNullOrWhiteSpace(argumentsPath))
{
    await File.WriteAllLinesAsync(argumentsPath, arguments, Encoding.UTF8);
}

if (args.Contains("--child", StringComparer.Ordinal))
{
    var childMarker = Environment.GetEnvironmentVariable("FAKE_SCALE_CHILD_MARKER");
    if (!string.IsNullOrWhiteSpace(childMarker))
    {
        await File.WriteAllTextAsync(childMarker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
    }

    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

Console.WriteLine("fake standard output");
Console.Error.WriteLine("fake standard error");

var outputPaths = FindOutputPaths(args);
if (mode.Equals("fail", StringComparison.Ordinal))
{
    foreach (var outputPath in outputPaths)
    {
        await File.WriteAllTextAsync(outputPath, "partial output", Encoding.UTF8);
    }

    return 7;
}

if (mode.Equals("sleep", StringComparison.Ordinal))
{
    var childMarker = Environment.GetEnvironmentVariable("FAKE_SCALE_CHILD_MARKER");
    var childStartInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    childStartInfo.ArgumentList.Add("--child");
    using var child = Process.Start(childStartInfo) ?? throw new InvalidOperationException("The fake child process did not start.");
    if (!string.IsNullOrWhiteSpace(childMarker))
    {
        await File.WriteAllTextAsync(childMarker, child.Id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
    }

    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

if (mode.Equals("no-output", StringComparison.Ordinal))
{
    return 0;
}

if (outputPaths.Count == 0)
{
    Console.Error.WriteLine("The fake compiler did not receive -o.");
    return 2;
}

foreach (var outputPath in outputPaths)
{
    await File.WriteAllTextAsync(outputPath, "deterministic fake output", Encoding.UTF8);
}
return 0;

static IReadOnlyList<string> FindOutputPaths(IReadOnlyList<string> arguments)
{
    var outputPaths = new List<string>();
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (arguments[index].Equals("-o", StringComparison.Ordinal))
        {
            outputPaths.Add(arguments[index + 1]);
        }
    }

    return outputPaths;
}
