namespace SoR.Shared.Seeding;

public sealed record SeedDevice(
    int Index,
    string Id,
    string TenantId,
    string Hostname,
    string Os,
    string IpAddress,
    DateTimeOffset LastSeenAt);
