namespace SoR.Shared.Seeding;

public static class SeedConstants
{
    public const int TotalDevices = 12_000;

    /// <summary>Indexes 0..6999 belong to <see cref="TenantA"/>, 7000..11999 to <see cref="TenantB"/>.</summary>
    public const int TenantADeviceCount = 7_000;

    public const string TenantA = "TenantA";
    public const string TenantB = "TenantB";

    /// <summary>Every seeded timestamp is relative to this instant, never to "now".</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Domain events fall in [Epoch - EventWindow, Epoch].</summary>
    public static readonly TimeSpan EventWindow = TimeSpan.FromDays(365);
}
