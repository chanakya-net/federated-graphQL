using SoR.Shared.Seeding;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Seeding;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.Tests;

/// <summary>The reverse index without any storage: how it is built, ordered, searched and queried.</summary>
public sealed class SoftwareIndexTests
{
    private static DeviceInstallDocument Doc(string tenant, string device, params (string Name, string Version)[] software) =>
        new(1, tenant, device, [.. software.Select((s, i) =>
            new StoredInstallEvent($"{device}-i{i:D3}", SeedConstants.Epoch - TimeSpan.FromDays(i), InstallAction.Install, InstallResult.Success, new Software(s.Name, s.Version, "pub")))]);

    [Fact]
    public void Build_groups_device_ids_per_product_and_tenant_sorted()
    {
        var index = SoftwareIndexBuilder.Build(
        [
            Doc("TenantB", "dev-07001", ("Git", "2.40.0")),
            Doc("TenantA", "dev-00002", ("Git", "2.40.0"), ("Git", "2.41.0"), ("7-Zip", "23.1.0")),
            Doc("TenantA", "dev-00001", ("Git", "2.40.0"), ("Git", "2.40.0")),   // twice on one device: one id
        ]);

        Assert.Equal(SoftwareIndexDocument.CurrentSchemaVersion, index.SchemaVersion);
        Assert.Equal(["7-Zip 23.1.0", "Git 2.40.0", "Git 2.41.0"], index.Products.Select(p => $"{p.Name} {p.Version}"));
        var git = index.Products[1];
        Assert.Equal(["TenantA", "TenantB"], git.DeviceIds.Keys);
        Assert.Equal(["dev-00001", "dev-00002"], git.DeviceIds["TenantA"]);
        Assert.Equal(["dev-07001"], git.DeviceIds["TenantB"]);
        Assert.Equal(["TenantA"], index.Products[0].DeviceIds.Keys);
    }

    [Fact]
    public void Build_is_pure_and_round_trips_through_json()
    {
        var documents = Enumerable.Range(0, 200).Select(i => InstallSeedData.BuildDocument(DeviceCatalog.Build(i * 60))).ToList();

        var a = InstallJson.Serialize(SoftwareIndexBuilder.Build(documents)).ToString();
        var b = InstallJson.Serialize(SoftwareIndexBuilder.Build(documents.AsEnumerable().Reverse())).ToString();

        Assert.Equal(a, b);   // input order never matters
        var back = InstallJson.Deserialize<SoftwareIndexDocument>(BinaryData.FromString(a));
        Assert.Equal(a, InstallJson.Serialize(back).ToString());
        Assert.Contains("\"products\":[{\"name\":", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Versions_sort_numerically_by_segment()
    {
        var sorted = new[] { "1.10.0", "1.2.10", "1.2.9", "10.0.0", "2.0.0", "1.2", "1.2.9-beta" }.Order(VersionComparer.Instance).ToList();

        Assert.Equal(["1.2", "1.2.9", "1.2.9-beta", "1.2.10", "1.10.0", "2.0.0", "10.0.0"], sorted);
    }

    [Fact]
    public void Search_pages_by_name_then_version_and_matches_name_or_publisher()
    {
        var index = new SoftwareIndex(SoftwareIndexBuilder.Build(DeviceCatalog.All().Take(600).Select(InstallSeedData.BuildDocument)));

        var all = index.Search(null, first: 100, offset: 0);
        Assert.Equal(100, all.Count);
        Assert.Equal(all.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Version, VersionComparer.Instance).Select(s => $"{s.Name} {s.Version}"), all.Select(s => $"{s.Name} {s.Version}"));
        Assert.Equal(index.Entries.Skip(10).Take(5).Select(e => e.Name), index.Search(null, first: 5, offset: 10).Select(s => s.Name));

        var chrome = index.Search("google", first: 100, offset: 0);
        Assert.NotEmpty(chrome);
        Assert.All(chrome, s => Assert.True(s.Name.Contains("Google", StringComparison.OrdinalIgnoreCase) || s.Publisher.Contains("Google", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(chrome, s => s.Name == "Google Chrome");

        var byPublisher = index.Search("JETBRAINS", first: 100, offset: 0);
        Assert.NotEmpty(byPublisher);
        Assert.All(byPublisher, s => Assert.Contains("JetBrains", s.Publisher, StringComparison.Ordinal));
        Assert.Empty(index.Search("no such product", first: 10, offset: 0));
    }

    [Fact]
    public void DevicesFor_unions_the_keys_within_one_tenant_only()
    {
        var index = new SoftwareIndex(SoftwareIndexBuilder.Build(
        [
            Doc("TenantA", "dev-00003", ("Git", "2.40.0")),
            Doc("TenantA", "dev-00001", ("Git", "2.41.0")),
            Doc("TenantA", "dev-00002", ("7-Zip", "23.1.0")),
            Doc("TenantB", "dev-07000", ("Git", "2.40.0"), ("7-Zip", "23.1.0")),
        ]));

        Assert.Equal(["dev-00001", "dev-00003"], index.DevicesFor("TenantA", [new SoftwareKey("Git", null)]));   // any version, sorted
        Assert.Equal(["dev-00003"], index.DevicesFor("TenantA", [new SoftwareKey("Git", "2.40.0")]));
        Assert.Equal(["dev-00001", "dev-00002", "dev-00003"], index.DevicesFor("TenantA", [new SoftwareKey("Git", null), new SoftwareKey("7-Zip", "23.1.0")]));
        Assert.Equal(["dev-07000"], index.DevicesFor("TenantB", [new SoftwareKey("Git", null)]));
        Assert.Empty(index.DevicesFor("TenantC", [new SoftwareKey("Git", null)]));
        Assert.Empty(index.DevicesFor("TenantA", [new SoftwareKey("git", null)]));   // exact name
        Assert.Empty(index.DevicesFor("TenantA", [new SoftwareKey("Git", "9.9.9")]));

        Assert.True(SoftwareIndex.Matches(new Software("Git", "2.40.0", "p"), [new SoftwareKey("Git", null)]));
        Assert.False(SoftwareIndex.Matches(new Software("Git", "2.40.0", "p"), [new SoftwareKey("Git", "2.41.0")]));
    }
}
