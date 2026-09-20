namespace SoR.Gateway;

/// <summary>Gateway environment variables besides the subgraph URLs (contracts/http-and-env.md).</summary>
public static class GatewaySettings
{
    public const string TimeoutVariable = "SUBGRAPH_TIMEOUT_SECONDS";
    public const int DefaultTimeoutSeconds = 5;

    // Search coordinates several bounded domain requests before returning its final page.
    public const string SearchTimeoutVariable = "DEVICE_SEARCH_TIMEOUT_SECONDS";
    public const int DefaultSearchTimeoutSeconds = 30;

    /// <summary>Path of the composed archive. Default: <c>gateway.far</c> next to the assembly.</summary>
    public const string ArchiveVariable = "GATEWAY_ARCHIVE";
}
