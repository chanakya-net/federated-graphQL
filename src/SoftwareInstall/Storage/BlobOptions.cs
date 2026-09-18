using Azure.Core;
using Azure.Storage.Blobs;

namespace SoR.SoftwareInstall.Storage;

/// <summary>Configuration section <c>Blob</c>: <c>Blob__ConnectionString</c>, <c>Blob__Container</c> (contracts/http-and-env.md).</summary>
public sealed class BlobOptions
{
    public const string Section = "Blob";

    public const string DefaultContainer = "install-events";

    /// <summary>
    /// Used only when <c>Blob__ConnectionString</c> is not set (schema export, a local <c>dotnet run</c> against Azurite
    /// on the host). The well-known Azurite dev account with an explicit <c>BlobEndpoint</c>, the same form compose
    /// uses; never <c>UseDevelopmentStorage=true</c>. Constructing a client from it does not connect.
    /// </summary>
    public const string LocalAzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    public string? ConnectionString { get; set; }

    public string Container { get; set; } = DefaultContainer;

    /// <summary>
    /// The container client for these options. Parses the connection string only; nothing is sent until the first
    /// operation, so <c>schema export</c> works with no storage running.
    /// </summary>
    public BlobContainerClient CreateContainerClient()
    {
        var connectionString = string.IsNullOrWhiteSpace(ConnectionString) ? LocalAzuriteConnectionString : ConnectionString;
        var options = new BlobClientOptions
        {
            Retry =
            {
                Mode = RetryMode.Exponential,
                MaxRetries = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                MaxDelay = TimeSpan.FromSeconds(2),
                // SDK default is 100 s per try. A hung Azurite should fail the read (the gateway gives up after 5 s
                // anyway) and the seed attempt, not park a request for minutes.
                NetworkTimeout = TimeSpan.FromSeconds(20),
            },
        };
        return new BlobServiceClient(connectionString, options).GetBlobContainerClient(Container);
    }
}
