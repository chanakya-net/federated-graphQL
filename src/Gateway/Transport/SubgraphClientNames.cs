namespace SoR.Gateway.Transport;

/// <summary>
/// Source-schema names (contracts/http-and-env.md). Each one is the composed source schema's name, the
/// name of its <see cref="HttpClient"/>, and the <c>&lt;NAME&gt;</c> in its <c>SUBGRAPH_&lt;NAME&gt;_URL</c>.
/// </summary>
public static class SubgraphClientNames
{
    public const string DeviceDirectory = "DeviceDirectory";
    public const string Patch = "Patch";
    public const string Vulnerability = "Vulnerability";
    public const string SoftwareInstall = "SoftwareInstall";

    public static readonly string[] All = [DeviceDirectory, Patch, Vulnerability, SoftwareInstall];

    /// <summary><c>SUBGRAPH_DEVICEDIRECTORY_URL</c>, <c>SUBGRAPH_PATCH_URL</c>, ... (compose sets them).</summary>
    public static string UrlVariable(string name) => $"SUBGRAPH_{name.ToUpperInvariant()}_URL";
}
