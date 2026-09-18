using SoR.Shared.Seeding;

namespace SoR.DeviceDirectory.Data;

public sealed class DeviceEntity
{
    public required string Id { get; set; }            // "dev-00042"
    public required string TenantId { get; set; }
    public required string Hostname { get; set; }
    public required string Os { get; set; }
    public required string IpAddress { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Straight copy of the canonical catalog entry; no value is invented here.</summary>
    public static DeviceEntity From(SeedDevice d) => new()
    {
        Id = d.Id,
        TenantId = d.TenantId,
        Hostname = d.Hostname,
        Os = d.Os,
        IpAddress = d.IpAddress,
        LastSeenAt = d.LastSeenAt,
    };
}

/// <summary>Completion marker, written last (contracts/seeding.md "Idempotency").</summary>
public sealed class SeedStateEntity
{
    public const string DevicesKey = "devices";

    public required string Key { get; set; }           // "devices"
    public DateTimeOffset CompletedAt { get; set; }    // bookkeeping only: the one wall-clock value
    public int RowCount { get; set; }
}
