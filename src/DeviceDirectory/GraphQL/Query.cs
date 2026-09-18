using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;
using SoR.DeviceDirectory.Data;
using SoR.Shared.Auth;

namespace SoR.DeviceDirectory.GraphQL;

/// <summary>
/// Every field requires a valid token (AUTH_NOT_AUTHENTICATED otherwise); no service check. The tenant comes
/// from the token only. <see cref="DeviceDbContext"/> is safe to inject because query resolvers run in their
/// own DI scope (<see cref="DeviceDirectorySchema"/>), so parallel root fields never share a context.
/// </summary>
[Authorize]
public sealed class Query
{
    public const int DefaultFirst = 25;
    public const int MaxFirst = 100;

    private const string LikeEscape = "\\";

    [Lookup]
    [GraphQLDescription("The device with this id in the caller's tenant. Null (never an error) if it does not exist there.")]
    public async Task<DeviceEntity?> GetDevice(
        [ID] string id,
        [Service] ICallerContext caller,
        [Service] DeviceDbContext db,
        CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        // Another tenant's device -> null, no error: tenant isolation leaks nothing.
        return await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
    }

    [GraphQLDescription(
        "Devices in the caller's tenant. `search` matches id, hostname or os (case-insensitive substring). " +
        "`first` is clamped to 1..100. Ordered by hostname, then id.")]
    public async Task<DeviceSearchResult> GetDevices(
        string? search,
        [Service] ICallerContext caller,
        [Service] DeviceDbContext db,
        CancellationToken ct,
        int first = DefaultFirst,
        int offset = 0)
    {
        first = Math.Clamp(first, 1, MaxFirst);
        offset = Math.Max(0, offset);
        var tenantId = caller.TenantId;

        var q = db.Devices.AsNoTracking().Where(d => d.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Substring match: the caller's text is literal, so LIKE wildcards in it are escaped.
            var p = $"%{EscapeLike(search.Trim())}%";
            q = q.Where(d => EF.Functions.ILike(d.Hostname, p, LikeEscape)
                          || EF.Functions.ILike(d.Id, p, LikeEscape)
                          || EF.Functions.ILike(d.Os, p, LikeEscape));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(d => d.Hostname).ThenBy(d => d.Id).Skip(offset).Take(first).ToListAsync(ct);
        return new DeviceSearchResult(items, total);
    }

    internal static string EscapeLike(string value) =>
        value.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
             .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
             .Replace("_", LikeEscape + "_", StringComparison.Ordinal);
}
