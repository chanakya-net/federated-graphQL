using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SoR.Shared.Seeding;

namespace SoR.SoftwareInstall.Storage;

public interface IInstallEventsStore
{
    /// <summary>
    /// The device's document inside <paramref name="tenantId"/>'s prefix, or <c>null</c> when there is none
    /// (no blob, another tenant's device, or an id no seed could have written). Never an error for those cases.
    /// </summary>
    Task<DeviceInstallDocument?> ReadAsync(string tenantId, string deviceId, CancellationToken ct);

    Task WriteAsync(DeviceInstallDocument document, CancellationToken ct);
}

/// <summary>
/// Tenant isolation is structural: the tenant claim selects the blob prefix, so a cross-tenant read looks up
/// <c>{callerTenant}/{otherTenantsDevice}/...</c>, which does not exist. There is no tenant column to forget.
/// </summary>
public sealed class InstallEventsBlobStore(BlobContainerClient container) : IInstallEventsStore
{
    public const string BlobFileName = "installEvents.json";

    public const string MarkerBlobName = "_seed/complete.json";

    /// <summary>The reverse index (<see cref="SoftwareIndexDocument"/>), written before the marker; never a tenant prefix.</summary>
    public const string IndexBlobName = "_index/software.json";

    private static readonly BlobHttpHeaders JsonHeaders = new() { ContentType = "application/json" };

    public BlobContainerClient Container => container;

    /// <summary><c>{tenantId}/{deviceId}/installEvents.json</c>.</summary>
    public static string BlobName(string tenantId, string deviceId) =>
        TryGetBlobName(tenantId, deviceId, out var name)
            ? name
            : throw new ArgumentException($"not a storable tenant/device pair: '{tenantId}' / '{deviceId}'");

    /// <summary>
    /// Only canonical device ids (<c>dev-00000</c>..<c>dev-11999</c>) and plain alphanumeric tenant ids map to a blob.
    /// The id comes from the query; without this check <c>../TenantB/dev-07000</c> would build a blob URL that
    /// <see cref="Uri"/> normalises into another tenant's prefix. No seed ever writes any other name.
    /// </summary>
    public static bool TryGetBlobName(string tenantId, string deviceId, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrEmpty(tenantId) || !tenantId.All(char.IsAsciiLetterOrDigit)) return false;
        if (!DeviceCatalog.TryGetIndex(deviceId, out _)) return false;
        name = $"{tenantId}/{deviceId}/{BlobFileName}";
        return true;
    }

    public async Task<DeviceInstallDocument?> ReadAsync(string tenantId, string deviceId, CancellationToken ct)
    {
        if (!TryGetBlobName(tenantId, deviceId, out var name)) return null;

        BinaryData content;
        try
        {
            var response = await container.GetBlobClient(name).DownloadContentAsync(ct);
            content = response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            // No blob = no events for this tenant/device. Only BlobNotFound: a missing container (ContainerNotFound,
            // also 404) is a broken deployment and must surface as a field error, not as "no events".
            return null;
        }

        var document = InstallJson.Deserialize<DeviceInstallDocument>(content);
        if (document.TenantId != tenantId || document.DeviceId != deviceId)
        {
            throw new InvalidOperationException($"blob {name} holds {document.TenantId}/{document.DeviceId}");
        }

        return document;
    }

    public Task WriteAsync(DeviceInstallDocument document, CancellationToken ct) =>
        UploadAsync(BlobName(document.TenantId, document.DeviceId), document, ct);

    public async Task<SeedMarker?> ReadMarkerAsync(CancellationToken ct)
    {
        try
        {
            var response = await container.GetBlobClient(MarkerBlobName).DownloadContentAsync(ct);
            return InstallJson.Deserialize<SeedMarker>(response.Value.Content);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return null;
        }
    }

    public Task WriteMarkerAsync(SeedMarker marker, CancellationToken ct) => UploadAsync(MarkerBlobName, marker, ct);

    public async Task<SoftwareIndexDocument?> ReadIndexAsync(CancellationToken ct)
    {
        try
        {
            var response = await container.GetBlobClient(IndexBlobName).DownloadContentAsync(ct);
            return InstallJson.Deserialize<SoftwareIndexDocument>(response.Value.Content);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return null;
        }
    }

    public Task WriteIndexAsync(SoftwareIndexDocument index, CancellationToken ct) => UploadAsync(IndexBlobName, index, ct);

    /// <summary>The names of every device blob (<c>{tenant}/{device}/installEvents.json</c>) in the container, in listing order.</summary>
    public async Task<IReadOnlyList<string>> ListDeviceBlobNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(cancellationToken: ct))
        {
            var parts = item.Name.Split('/');
            if (parts.Length == 3 && parts[2] == BlobFileName && TryGetBlobName(parts[0], parts[1], out _)) names.Add(item.Name);
        }

        return names;
    }

    /// <summary>One device blob by its full name, as <see cref="ReadAsync"/> would validate it.</summary>
    public async Task<DeviceInstallDocument> ReadBlobAsync(string name, CancellationToken ct)
    {
        var response = await container.GetBlobClient(name).DownloadContentAsync(ct);
        return InstallJson.Deserialize<DeviceInstallDocument>(response.Value.Content);
    }

    /// <summary>Plain PUT without conditions, i.e. overwrite: re-running a partial seed needs no delete pass.</summary>
    private Task UploadAsync<T>(string name, T value, CancellationToken ct) =>
        container.GetBlobClient(name).UploadAsync(InstallJson.Serialize(value), new BlobUploadOptions { HttpHeaders = JsonHeaders }, ct);
}
