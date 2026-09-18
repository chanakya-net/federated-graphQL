using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SoR.Patch.Data;

/// <summary>
/// <c>ping</c> against the configured database. Registered with a short timeout, so an unreachable server turns
/// <c>/health</c> 503 quickly instead of waiting for the driver's 30 s server-selection timeout.
/// </summary>
public sealed class MongoPingHealthCheck(IMongoDatabase database) : IHealthCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly BsonDocument Ping = new("ping", 1);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(Ping, cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("mongo ping failed", ex);
        }
    }
}
