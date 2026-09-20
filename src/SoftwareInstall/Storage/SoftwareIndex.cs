using SoR.SoftwareInstall.GraphQL;

namespace SoR.SoftwareInstall.Storage;

/// <summary>
/// The reverse index blob <c>_index/software.json</c>: every distinct software (name, version, publisher) seen in any
/// device's install events, with the ids of the devices that have such an event, per tenant. Derived from the device
/// blobs (the seeder writes it from the documents it uploads; a container seeded without it is scanned once at
/// startup), so it is as deterministic as they are.
/// </summary>
public sealed record SoftwareIndexDocument(int SchemaVersion, IReadOnlyList<SoftwareIndexEntry> Products)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>One product version and, per tenant, the sorted ids of the devices with at least one event for it.</summary>
public sealed record SoftwareIndexEntry(
    string Name,
    string Version,
    string Publisher,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DeviceIds);

/// <summary>A product to look devices up by: the exact name and optionally one exact version (null = any).</summary>
public sealed record SoftwareKey(string Name, string? Version);

/// <summary>The index in memory: the catalog in its promised order and the per-tenant device sets.</summary>
public sealed class SoftwareIndex
{
    private readonly ILookup<string, SoftwareIndexEntry> _byName;

    public SoftwareIndex(SoftwareIndexDocument document)
    {
        Entries = [.. document.Products.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Version, VersionComparer.Instance)];
        _byName = Entries.ToLookup(e => e.Name, StringComparer.Ordinal);
    }

    /// <summary>The catalog ordered by name, then by version (numeric segments compare as numbers).</summary>
    public IReadOnlyList<SoftwareIndexEntry> Entries { get; }

    /// <summary>One page of the catalog; <paramref name="search"/> is a case-insensitive substring of the name or the publisher.</summary>
    public IReadOnlyList<Software> Search(string? search, int first, int offset)
    {
        IEnumerable<SoftwareIndexEntry> q = Entries;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(e => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return [.. q.Skip(offset).Take(first).Select(e => new Software(e.Name, e.Version, e.Publisher))];
    }

    /// <summary>The ids of the devices of <paramref name="tenantId"/> with an event for any of <paramref name="keys"/>, sorted, distinct.</summary>
    public IReadOnlyList<string> DevicesFor(string tenantId, IEnumerable<SoftwareKey> keys)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            foreach (var entry in _byName[key.Name])
            {
                if (key.Version is not null && !string.Equals(entry.Version, key.Version, StringComparison.Ordinal)) continue;
                if (entry.DeviceIds.TryGetValue(tenantId, out var devices)) ids.UnionWith(devices);
            }
        }

        return [.. ids];
    }

    /// <summary>Whether an event's software is one of <paramref name="keys"/> (exact name; exact version when the key has one).</summary>
    public static bool Matches(Software software, IReadOnlyCollection<SoftwareKey> keys) =>
        keys.Any(k => string.Equals(k.Name, software.Name, StringComparison.Ordinal)
                      && (k.Version is null || string.Equals(k.Version, software.Version, StringComparison.Ordinal)));
}

/// <summary>Builds the index from device documents. Pure: the same documents give the same bytes.</summary>
public static class SoftwareIndexBuilder
{
    public static SoftwareIndexDocument Build(IEnumerable<DeviceInstallDocument> documents)
    {
        var map = new Dictionary<(string Name, string Version, string Publisher), Dictionary<string, SortedSet<string>>>();
        foreach (var document in documents)
        {
            foreach (var e in document.Events)
            {
                var key = (e.Software.Name, e.Software.Version, e.Software.Publisher);
                if (!map.TryGetValue(key, out var tenants)) map[key] = tenants = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                if (!tenants.TryGetValue(document.TenantId, out var ids)) tenants[document.TenantId] = ids = new SortedSet<string>(StringComparer.Ordinal);
                ids.Add(document.DeviceId);
            }
        }

        var products = map
            .OrderBy(kv => kv.Key.Name, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Version, VersionComparer.Instance)
            .ThenBy(kv => kv.Key.Publisher, StringComparer.Ordinal)
            .Select(kv => new SoftwareIndexEntry(
                kv.Key.Name,
                kv.Key.Version,
                kv.Key.Publisher,
                kv.Value.OrderBy(t => t.Key, StringComparer.Ordinal)
                    .ToDictionary(t => t.Key, t => (IReadOnlyList<string>)[.. t.Value], StringComparer.Ordinal)))
            .ToList();
        return new SoftwareIndexDocument(SoftwareIndexDocument.CurrentSchemaVersion, products);
    }
}

/// <summary>
/// "1.2.10" after "1.2.9": each dot-separated segment compares by its leading number first (none = 0), then by the
/// rest ordinally ("9" before "9-beta"), so the order is total and a pre-release never jumps a numeric neighbour.
/// </summary>
public sealed class VersionComparer : IComparer<string>
{
    public static VersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var a = x.Split('.');
        var b = y.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var (na, ra) = Split(a[i]);
            var (nb, rb) = Split(b[i]);
            var c = na.CompareTo(nb);
            if (c == 0) c = string.CompareOrdinal(ra, rb);
            if (c != 0) return c;
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>"9-beta" -> (9, "-beta"); "beta" -> (0, "beta").</summary>
    private static (long Number, string Suffix) Split(string segment)
    {
        var digits = 0;
        while (digits < segment.Length && char.IsAsciiDigit(segment[digits])) digits++;
        var number = digits == 0 ? 0 : long.Parse(segment.AsSpan(0, Math.Min(digits, 18)), System.Globalization.CultureInfo.InvariantCulture);
        return (number, segment[digits..]);
    }
}
