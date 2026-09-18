namespace SoR.Shared.Seeding;

/// <summary>
/// The canonical device set every subgraph seeds. Id and tenant are pure functions of the index;
/// Bogus only decorates (hostname, OS, IP, last seen), seeded per index so order never matters.
/// </summary>
public static class DeviceCatalog
{
    private const string Prefix = "dev-";

    private static readonly string[] OsCatalog =
    [
        "Windows 11 23H2", "Windows 10 22H2", "Windows Server 2022",
        "Ubuntu 22.04", "Ubuntu 24.04", "RHEL 9", "macOS 15",
    ];

    public static string DeviceId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SeedConstants.TotalDevices);
        return $"{Prefix}{index:D5}";
    }

    public static bool TryGetIndex(string? deviceId, out int index)
    {
        index = -1;
        if (deviceId is null || !deviceId.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var digits = deviceId.AsSpan(Prefix.Length);
        if (digits.Length != 5) return false;   // only the canonical "dev-00042" form, so the mapping is 1:1
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i)) return false;
        if (i is < 0 or >= SeedConstants.TotalDevices) return false;
        index = i;
        return true;
    }

    public static string TenantOf(int index) =>
        index < SeedConstants.TenantADeviceCount ? SeedConstants.TenantA : SeedConstants.TenantB;

    /// <summary>Deterministic per index. Nothing federation depends on comes from Bogus.</summary>
    public static SeedDevice Build(int index)
    {
        var id = DeviceId(index);

        // One Randomizer per index, shared by the only two datasets used. Same draws, in the same order,
        // as `new Faker("en") { Random = new Randomizer(index) }`, without constructing every Faker
        // dataset per device (about 25x faster; golden-devices.json pins the output).
        var random = new Bogus.Randomizer(index);
        var hacker = new Bogus.DataSets.Hacker("en") { Random = random };
        var internet = new Bogus.DataSets.Internet("en") { Random = random };

        var os = OsCatalog[random.Int(0, OsCatalog.Length - 1)];
        var host = $"{hacker.Noun().ToLowerInvariant().Replace(' ', '-')}-{random.AlphaNumeric(4).ToLowerInvariant()}-{index % 1000:D3}";
        var lastSeen = SeedConstants.Epoch - TimeSpan.FromMinutes(random.Int(1, 30 * 24 * 60));
        return new SeedDevice(index, id, TenantOf(index), host, os, internet.Ip(), lastSeen);
    }

    public static IEnumerable<SeedDevice> All() =>
        Enumerable.Range(0, SeedConstants.TotalDevices).Select(Build);

    public static IEnumerable<SeedDevice> ForTenant(string tenantId) =>
        All().Where(d => d.TenantId == tenantId);
}
