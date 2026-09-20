using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SoR.Gateway.Transport;

public sealed record SubgraphClientOptions(string Name, Uri Url, TimeSpan Timeout);

public static class SubgraphClientRegistration
{
    public static IReadOnlyList<SubgraphClientOptions> Add(
        IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<string> sourceSchemaNames,
        TimeSpan defaultTimeout)
    {
        var registrations = new List<SubgraphClientOptions>();
        foreach (var name in sourceSchemaNames)
        {
            var configuredUrl = configuration[UrlVariable(name)];
            if (string.IsNullOrWhiteSpace(configuredUrl))
                throw new InvalidOperationException(
                    $"FAR source schema '{name}' has no configured endpoint. Set {UrlVariable(name)} before starting the gateway.");
            var url = AbsoluteHttpUri(configuredUrl, UrlVariable(name));
            var timeoutSeconds = configuration.GetValue(TimeoutVariable(name), defaultTimeout.TotalSeconds);
            if (timeoutSeconds <= 0 || double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds))
                throw new InvalidOperationException($"{TimeoutVariable(name)} must be a positive number of seconds, got {timeoutSeconds}.");
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);

            services.AddHttpClient(name, client =>
                {
                    client.BaseAddress = url;
                    client.Timeout = timeout;
                })
                .AddHttpMessageHandler<ForwardAuthorizationHandler>();
            registrations.Add(new(name, url, timeout));
        }

        return registrations;
    }

    public static string UrlVariable(string name) => $"SUBGRAPH_{name.ToUpperInvariant()}_URL";

    public static string TimeoutVariable(string name) => $"SUBGRAPH_{name.ToUpperInvariant()}_TIMEOUT_SECONDS";

    private static Uri AbsoluteHttpUri(string value, string variable)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"{variable} must be an absolute http(s) URL, got '{value}'.");
        return uri;
    }
}
