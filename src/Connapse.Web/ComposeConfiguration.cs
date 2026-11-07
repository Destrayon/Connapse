using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Connapse.Web;

/// <summary>What the Compose files hand the app beyond plain environment variables.</summary>
public static class ComposeConfiguration
{
    private const string RerankerProfile = "with-reranker";
    private const string RerankerKey = "Knowledge:Search:Reranker";

    /// <summary>
    /// Reads the passwords the first start generated, one file each in <c>CONNAPSE_SECRETS_DIR</c>, and turns reranking
    /// on when the reranker was switched on in <c>COMPOSE_PROFILES</c>. Both go in before the saved settings are added,
    /// so a choice saved on the Settings page still wins. The files sit just under the environment variables, so a
    /// connection string set there for an outside database still wins too.
    /// </summary>
    public static void AddComposeSetup(this IConfigurationManager configuration)
    {
        if (configuration["CONNAPSE_SECRETS_DIR"] is { Length: > 0 } secrets)
            AddBelowEnvironmentVariables(configuration, secrets);

        bool rerankerOn = configuration["Connapse:Extras"]?
            .Split(',', StringSplitOptions.TrimEntries)
            .Contains(RerankerProfile, StringComparer.OrdinalIgnoreCase) ?? false;

        if (rerankerOn && configuration[RerankerKey] is null)
            configuration.AddInMemoryCollection([new(RerankerKey, "CrossEncoder")]);
    }

    private static void AddBelowEnvironmentVariables(IConfigurationManager configuration, string folder)
    {
        configuration.AddKeyPerFile(folder, optional: true);

        IList<IConfigurationSource> sources = configuration.Sources;
        IConfigurationSource files = sources[^1];
        sources.RemoveAt(sources.Count - 1);

        int position = sources.Count;
        while (position > 0 && sources[position - 1] is not EnvironmentVariablesConfigurationSource)
            position--;

        sources.Insert(position > 0 ? position - 1 : sources.Count, files);
    }
}
