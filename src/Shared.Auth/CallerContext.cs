using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SoR.Shared.Auth;

/// <summary>The validated caller. Tenant and service access come from JWT claims only, never from arguments.</summary>
public interface ICallerContext
{
    bool IsAuthenticated { get; }

    /// <summary>The <c>sub</c> claim, or empty.</summary>
    string UserId { get; }

    /// <summary>The <c>tenantId</c> claim. Throws <see cref="UnauthorizedAccessException"/> if missing.</summary>
    string TenantId { get; }

    IReadOnlySet<string> Services { get; }

    bool HasService(string serviceName);
}

internal sealed class HttpCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    private ClaimsPrincipal User => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;

    public string UserId => User.FindFirst(DevAuth.SubjectClaim)?.Value ?? string.Empty;

    public string TenantId => User.FindFirst(DevAuth.TenantClaim)?.Value
        ?? throw new UnauthorizedAccessException("tenantId claim missing");

    public IReadOnlySet<string> Services =>
        User.FindAll(DevAuth.ServicesClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

    public bool HasService(string serviceName) => Services.Contains(serviceName);
}
