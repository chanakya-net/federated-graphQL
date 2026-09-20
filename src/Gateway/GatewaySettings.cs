namespace SoR.Gateway;

/// <summary>Gateway environment variables besides the subgraph URLs (contracts/http-and-env.md).</summary>
public static class GatewaySettings
{
    public const string TimeoutVariable = "SUBGRAPH_TIMEOUT_SECONDS";
    public const int DefaultTimeoutSeconds = 5;

    /// <summary>Path of the composed archive. Default: <c>gateway.far</c> next to the assembly.</summary>
    public const string ArchiveVariable = "GATEWAY_ARCHIVE";

    /// <summary>Path of the catalog paired with the archive. Default: <c>timeline-sources.json</c> beside it.</summary>
    public const string CatalogVariable = "TIMELINE_SOURCES_CATALOG";
}
