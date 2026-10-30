using System.Globalization;
using Docker.DotNet.Models;

namespace Connapse.Integration.Tests;

/// <summary>
/// CPU and memory caps applied to every container the integration fixtures start, so a test run
/// leaves the machine usable. Defaults to 2 CPUs and 4 GB each; set
/// <c>CONNAPSE_TEST_CONTAINER_LIMITS</c> to <c>&lt;cpus&gt;,&lt;memory&gt;</c> (for example <c>4,8g</c>)
/// to change both, or to <c>off</c> to start containers uncapped.
/// </summary>
public static class TestContainerLimits
{
    public const string EnvironmentVariable = "CONNAPSE_TEST_CONTAINER_LIMITS";
    public const double DefaultCpus = 2;
    public const long DefaultMemoryBytes = 4L * 1024 * 1024 * 1024;

    private static readonly Lazy<Limits?> Configured =
        new(() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)));

    public sealed record Limits(double Cpus, long MemoryBytes);

    /// <summary>Pass to <c>WithCreateParameterModifier</c>; it adds to any other modifier on the builder.</summary>
    public static void Apply(CreateContainerParameters parameters) => Apply(parameters, Configured.Value);

    internal static void Apply(CreateContainerParameters parameters, Limits? limits)
    {
        if (limits is null)
            return;
        parameters.HostConfig ??= new HostConfig();
        parameters.HostConfig.NanoCPUs = (long)(limits.Cpus * 1_000_000_000);
        parameters.HostConfig.Memory = limits.MemoryBytes;
        // Equal to Memory, so the container gets no swap on top of its cap.
        parameters.HostConfig.MemorySwap = limits.MemoryBytes;
    }

    /// <summary>
    /// Null or blank gives the defaults, <c>off</c> gives no limits. Anything unreadable throws, so a
    /// typo never starts containers uncapped.
    /// </summary>
    internal static Limits? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Limits(DefaultCpus, DefaultMemoryBytes);
        if (value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double cpus)
            && cpus > 0
            && TryParseMemory(parts[1], out long memoryBytes))
            return new Limits(cpus, memoryBytes);

        throw new InvalidOperationException(
            $"{EnvironmentVariable}='{value}' is not '<cpus>,<memory>' (for example '2,4g') or 'off'.");
    }

    // Docker's own memory syntax: a whole number with an optional b, k, m or g suffix, in binary units.
    private static bool TryParseMemory(string value, out long bytes)
    {
        bytes = 0;
        if (value.Length == 0)
            return false;
        long multiplier = char.ToLowerInvariant(value[^1]) switch
        {
            'g' => 1024L * 1024 * 1024,
            'm' => 1024L * 1024,
            'k' => 1024L,
            'b' => 1L,
            _ => 0L,
        };
        string number = multiplier == 0 ? value : value[..^1];
        if (multiplier == 0)
            multiplier = 1;
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long amount)
            || amount <= 0 || amount > long.MaxValue / multiplier)
            return false;
        bytes = amount * multiplier;
        // Docker refuses anything under 6 MB.
        return bytes >= 6L * 1024 * 1024;
    }
}
