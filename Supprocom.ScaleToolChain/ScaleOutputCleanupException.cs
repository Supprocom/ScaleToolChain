namespace Supprocom.ScaleToolChain;

public sealed class ScaleOutputCleanupException : Exception
{
    public ScaleOutputCleanupException(
        string message,
        IReadOnlyList<string> outputPaths,
        Exception? innerException = null)
        : base(message, innerException)
    {
        OutputPaths = outputPaths;
    }

    public IReadOnlyList<string> OutputPaths { get; }
}
