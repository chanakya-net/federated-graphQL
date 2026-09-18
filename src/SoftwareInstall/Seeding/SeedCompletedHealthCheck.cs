using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SoR.SoftwareInstall.Seeding;

/// <summary>Unhealthy until the completion marker exists (contracts/http-and-env.md: /health = can serve queries).</summary>
public sealed class SeedCompletedHealthCheck(SeedState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Completed
            ? HealthCheckResult.Healthy("seed complete")
            : HealthCheckResult.Unhealthy(state.Error is null ? "seeding in progress" : $"seeding failed: {state.Error}"));
}
