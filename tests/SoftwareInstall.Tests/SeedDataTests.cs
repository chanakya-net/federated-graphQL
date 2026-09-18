using SoR.Shared.Seeding;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Seeding;

namespace SoR.SoftwareInstall.Tests;

public sealed class SeedDataTests
{
    [Fact]
    public void SeedData_is_deterministic_and_sorted()
    {
        var first = InstallSeedData.BuildEventsFor(DeviceCatalog.Build(42));
        var second = InstallSeedData.BuildEventsFor(DeviceCatalog.Build(42));

        Assert.Equal(first, second);   // records: value equality, Software included
        Assert.InRange(first.Count, InstallSeedData.MinEvents, InstallSeedData.MaxEvents);
        Assert.Equal(first.OrderBy(e => e.OccurredAt), first);   // ascending in blob order
    }

    [Fact]
    public void SeedData_is_independent_of_generation_order()
    {
        // The catalog is built lazily once; a device's events depend on its index only.
        var direct = InstallSeedData.BuildDocument(DeviceCatalog.Build(11_999));
        _ = InstallSeedData.BuildDocument(DeviceCatalog.Build(0));
        var again = InstallSeedData.BuildDocument(DeviceCatalog.Build(11_999));

        Assert.Equal(direct.Events, again.Events);
        Assert.Equal((1, SeedConstants.TenantB, "dev-11999"), (again.SchemaVersion, again.TenantId, again.DeviceId));
    }

    [Fact]
    public void Events_have_contract_ids_catalog_software_and_timestamps_in_window()
    {
        var device = DeviceCatalog.Build(42);
        var events = InstallSeedData.BuildEventsFor(device);
        var catalog = InstallSeedData.Catalog;

        Assert.Equal(
            Enumerable.Range(0, events.Count).Select(n => $"dev-00042-i{n:D3}").Order(StringComparer.Ordinal),
            events.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.All(events, e =>
        {
            Assert.InRange(e.OccurredAt, SeedConstants.Epoch - SeedConstants.EventWindow, SeedConstants.Epoch);
            Assert.Contains(catalog, p => p.Name == e.Software.Name && p.Publisher == e.Software.Publisher && p.Versions.Contains(e.Software.Version));
        });
    }

    [Fact]
    public void Every_device_has_3_to_20_events_and_the_mix_follows_the_weights()
    {
        var all = DeviceCatalog.All().Select(InstallSeedData.BuildEventsFor).ToList();

        Assert.All(all, events => Assert.InRange(events.Count, 3, 20));
        var flat = all.SelectMany(e => e).ToList();
        double Share(Func<Storage.StoredInstallEvent, bool> p) => flat.Count(p) / (double)flat.Count;
        Assert.InRange(Share(e => e.Action == InstallAction.Install), 0.53, 0.57);
        Assert.InRange(Share(e => e.Action == InstallAction.Upgrade), 0.28, 0.32);
        Assert.InRange(Share(e => e.Action == InstallAction.Uninstall), 0.13, 0.17);
        Assert.InRange(Share(e => e.Result == InstallResult.Success), 0.90, 0.94);
    }

    [Fact]
    public void Catalog_has_150_products_with_3_to_6_increasing_versions()
    {
        var catalog = InstallSeedData.BuildCatalog();

        Assert.Equal(InstallSeedData.CatalogSize, catalog.Count);
        Assert.Equal(catalog.Count, catalog.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("7-Zip", catalog[0].Name);
        Assert.Equal("Igor Pavlov", catalog[0].Publisher);
        Assert.All(catalog, p =>
        {
            Assert.InRange(p.Versions.Count, 3, 6);
            var parsed = p.Versions.Select(Version.Parse).ToList();
            Assert.All(parsed, v => Assert.Equal(3, v.ToString().Split('.').Length));
            Assert.All(parsed.Zip(parsed.Skip(1)), pair => Assert.True(pair.First < pair.Second, $"{p.Name}: {pair.First} !< {pair.Second}"));
        });

        // Deterministic: a second build is identical.
        Assert.Equal(catalog.Select(Describe), InstallSeedData.BuildCatalog().Select(Describe));
        static string Describe(CatalogProduct p) => $"{p.Name}|{p.Publisher}|{string.Join(',', p.Versions)}";
    }
}
