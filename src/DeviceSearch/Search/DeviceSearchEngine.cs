using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Transport;
using SoR.DeviceSearch.Providers;
using SoR.Shared.Auth;

namespace SoR.DeviceSearch.Search;

/// <summary>Request-local set evaluation. AND groups are unioned, then the final ID set is paged.</summary>
public sealed class DeviceSearchEngine(IDomainSearchClient domain, ICallerContext caller, SearchProviderRegistry registry)
{
    public const int MaxFilters = 20;
    public const int MaxDiscoveredIds = 100_000;
    public const int MaxConcurrency = 4;
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(25);

    public async Task<IReadOnlyList<SearchCatalogItem>?> CatalogAsync(string category, string? search, int first, CancellationToken ct)
    {
        var provider = registry.Resolve(category);
        if (!caller.IsAuthenticated) throw Error("AUTH_NOT_AUTHENTICATED", "Authentication is required.");
        _ = caller.TenantId;
        if (!caller.HasService(provider.RequiredPermission)) throw Error("AUTH_NOT_AUTHORIZED", $"Device search requires access to {provider.RequiredPermission}.");
        if (first is < 1 or > 100 || search is not null && (search.Length > 512 || search.Any(char.IsControl)))
            throw Error("BAD_USER_INPUT", "Catalog first must be between 1 and 100; search must have at most 512 characters without control characters.");
        return await domain.CatalogAsync(category, search?.Trim(), first, ct);
    }

    public async Task<FindDevicesResult> FindAsync(IReadOnlyList<DeviceSearchFilterInput> filters, int first, int offset, CancellationToken ct)
    {
        filters = ValidateAndNormalize(filters, first, offset);
        if (!caller.IsAuthenticated) throw Error("AUTH_NOT_AUTHENTICATED", "Authentication is required.");
        _ = caller.TenantId;
        // Preflight every required permission, even when another predicate has no matches.
        foreach (var category in filters.Select(f => f.Category).Distinct(StringComparer.Ordinal))
            if (!caller.HasService(registry.Resolve(category).RequiredPermission)) throw Error("AUTH_NOT_AUTHORIZED", $"Device search requires access to {category}.");
        if (filters.Count == 0) return new([], 0, false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SearchTimeout);
        try
        {
            var selections = filters.Select(f => f with { Connector = "and" }).Distinct().ToArray();
            var sets = new IReadOnlySet<string>[selections.Length];
            var discovered = 0;
            await Parallel.ForAsync(0, selections.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = timeout.Token,
            }, async (i, token) =>
            {
                sets[i] = await domain.DiscoverAsync(selections[i], token);
                if (Interlocked.Add(ref discovered, sets[i].Count) > MaxDiscoveredIds)
                    throw Error("SEARCH_LIMIT_EXCEEDED", $"Search exceeds {MaxDiscoveredIds} discovered device IDs. Narrow the filters.");
            });
            var byFilter = selections.Select((f, i) => (f, sets[i])).ToDictionary(x => x.f, x => x.Item2);
            var combined = new HashSet<string>(StringComparer.Ordinal);
            var group = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < filters.Count; i++)
            {
                var filter = filters[i];
                var ids = byFilter[filter with { Connector = "and" }];
                if (i == 0 || filter.Connector == "or")
                {
                    combined.UnionWith(group);
                    group = new(ids, StringComparer.Ordinal);
                }
                else group.IntersectWith(ids);
            }
            combined.UnionWith(group);
            var page = combined.Order(StringComparer.Ordinal).Skip(offset).Take(first).ToArray();
            if (page.Length == 0) return new([], combined.Count, false);
            var details = new IReadOnlyList<DeviceSearchItem>[selections.Length];
            await Parallel.ForAsync(0, selections.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = timeout.Token,
            }, async (i, token) =>
            {
                var candidates = page.Where(sets[i].Contains).ToArray();
                details[i] = candidates.Length == 0 ? [] : await domain.DetailsAsync(selections[i], candidates, token);
            });
            var events = details.SelectMany(x => x).ToLookup(x => x.Device.Id, StringComparer.Ordinal);
            var items = page.Select(id => new DeviceSearchItem(new Device(id), events[id]
                .SelectMany(x => x.Events)
                .DistinctBy(e => (e.Source, e.Id))
                .OrderByDescending(e => e.OccurredAt)
                .ThenBy(e => e.Source, StringComparer.Ordinal)
                .ThenBy(e => e.Id, StringComparer.Ordinal).ToArray())).ToArray();
            return new(items, combined.Count, (long)offset + page.Length < combined.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Error("SEARCH_TIMEOUT", "Device search exceeded its time limit. Narrow the filters or retry.");
        }
    }

    private IReadOnlyList<DeviceSearchFilterInput> ValidateAndNormalize(IReadOnlyList<DeviceSearchFilterInput> filters, int first, int offset)
    {
        if (filters.Count > MaxFilters || first is < 1 or > 100 || offset < 0)
            throw Error("BAD_USER_INPUT", "Use at most 20 filters, first between 1 and 100, and a nonnegative offset.");
        return filters.Select(f =>
        {
            if (f is null || f.Connector is not ("and" or "or") || string.IsNullOrWhiteSpace(f.Category))
                throw Error("BAD_USER_INPUT", "Each filter needs a valid category, key, and lowercase and/or connector.");
            return f with { Key = registry.Resolve(f.Category).NormalizeKey(f.Key) };
        }).ToArray();
    }

    internal static GraphQLException Error(string code, string message) =>
        new(ErrorBuilder.New().SetCode(code).SetMessage(message).Build());
}
