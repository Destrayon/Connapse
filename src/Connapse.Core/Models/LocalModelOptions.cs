namespace Connapse.Core;

/// <summary>
/// What Compose says about the Ollama it ships, under <c>Connapse:LocalModel</c>. Outside Compose there is no address,
/// and Connapse leaves whatever Ollama it's pointed at alone: only the bundled one is its own to download into.
/// </summary>
public sealed record LocalModelOptions
{
    public const string Section = "Connapse:LocalModel";

    /// <summary>The address Compose gives the bundled Ollama.</summary>
    public const string BundledAddress = "http://ollama:11434";

    public string? Address { get; init; }

    /// <summary><c>on</c> or <c>off</c>, from <c>CONNAPSE_LOCAL_MODEL</c>.</summary>
    public string Switch { get; init; } = "on";

    public bool IsOn => !string.Equals(Switch, "off", StringComparison.OrdinalIgnoreCase);
}
