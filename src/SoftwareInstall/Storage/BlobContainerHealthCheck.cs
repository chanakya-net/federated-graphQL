using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SoR.SoftwareInstall.Storage;

/// <summary>Healthy when the storage account answers and the configured container exists.</summary>
public sealed class BlobContainerHealthCheck(BlobContainerClient container) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Exceptions (storage unreachable) are turned into Unhealthy by the health check service.
        var exists = await container.ExistsAsync(cancellationToken);
        return exists.Value
            ? HealthCheckResult.Healthy($"container {container.Name} exists")
            : HealthCheckResult.Unhealthy($"container {container.Name} does not exist");
    }
}
