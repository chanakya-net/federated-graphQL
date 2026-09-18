using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SoR.Shared.Auth;

public sealed record DevAuthState(bool KeyConfigured);

public sealed class DevAuthHealthCheck(DevAuthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.KeyConfigured
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"{DevAuth.SigningKeyEnv} is not set"));
}
