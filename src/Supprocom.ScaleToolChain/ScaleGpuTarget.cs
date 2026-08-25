namespace Supprocom.ScaleToolChain;

public enum ScaleGpuVendor
{
    Amd,
    Nvidia
}

public sealed record ScaleGpuTarget
{
    public ScaleGpuTarget(ScaleGpuVendor vendor, string architecture)
    {
        if (!Enum.IsDefined(vendor))
        {
            throw new ArgumentOutOfRangeException(nameof(vendor), vendor, "The GPU vendor is not supported.");
        }

        if (string.IsNullOrWhiteSpace(architecture))
        {
            throw new ArgumentException("The GPU architecture is required.", nameof(architecture));
        }

        if (!IsValidArchitecture(vendor, architecture))
        {
            throw new ArgumentException(
                $"The architecture '{architecture}' does not match the {vendor} target format.",
                nameof(architecture));
        }

        Vendor = vendor;
        Architecture = architecture;
    }

    public ScaleGpuVendor Vendor { get; }

    public string Architecture { get; }

    public static ScaleGpuTarget Amd(string architecture) => new(ScaleGpuVendor.Amd, architecture);

    public static ScaleGpuTarget Nvidia(string architecture) => new(ScaleGpuVendor.Nvidia, architecture);

    public override string ToString() => Architecture;

    private static bool IsValidArchitecture(ScaleGpuVendor vendor, string architecture)
    {
        var prefix = vendor switch
        {
            ScaleGpuVendor.Amd => "gfx",
            ScaleGpuVendor.Nvidia => "sm_",
            _ => throw new ArgumentOutOfRangeException(nameof(vendor), vendor, "The GPU vendor is not supported.")
        };

        if (!architecture.StartsWith(prefix, StringComparison.Ordinal) || architecture.Length == prefix.Length)
        {
            return false;
        }

        return architecture[prefix.Length..].All(static character => character is >= '0' and <= '9');
    }
}
