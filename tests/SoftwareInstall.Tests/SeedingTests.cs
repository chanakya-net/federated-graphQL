using System.Net;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SoR.Shared.Seeding;
using SoR.SoftwareInstall.Seeding;
using SoR.SoftwareInstall.Storage;
using SoR.SoftwareInstall.Tests.Support;
using Xunit.Abstractions;

namespace SoR.SoftwareInstall.Tests;

[Collection(AzuriteCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class SeedingTests(AzuriteFixture azurite, ITestOutputHelper output)
{
    [Fact]
    public async Task Seed_writes_one_blob_per_device_and_marker()
    {
        var container = azurite.Container;

        Assert.Equal(SeedConstants.TenantADeviceCount, await CountAsync(container, "TenantA/"));
        Assert.Equal(SeedConstants.TotalDevices - SeedConstants.TenantADeviceCount, await CountAsync(container, "TenantB/"));
        Assert.Equal(SeedConstants.TotalDevices + 2, await CountAsync(container, prefix: null));   // + the marker + the index
        Assert.True((await container.GetBlobClient(InstallEventsBlobStore.MarkerBlobName).ExistsAsync()).Value);
        var marker = InstallJson.Deserialize<SeedMarker>((await container.GetBlobClient(InstallEventsBlobStore.MarkerBlobName).DownloadContentAsync()).Value.Content);
        Assert.Equal(SeedConstants.TotalDevices, marker.DeviceCount);

        var completed = Assert.Single(azurite.App.Logs.Messages, m => m.Contains("seed completed: 12000 device blobs", StringComparison.Ordinal));
        output.WriteLine(completed);

        // Second start on the same container: the marker is found and nothing is uploaded.
        var markerEtag = (await container.GetBlobClient(InstallEventsBlobStore.MarkerBlobName).GetPropertiesAsync()).Value.ETag;
        var blobEtag = (await container.GetBlobClient("TenantA/dev-00042/installEvents.json").GetPropertiesAsync()).Value.ETag;
        await using (var second = azurite.NewApp())
        {
            await second.WaitUntilHealthyAsync();
            output.WriteLine(Assert.Single(second.Logs.Messages, m => m.Contains("seed already present (12000 device blobs", StringComparison.Ordinal)));
            Assert.DoesNotContain(second.Logs.Messages, m => m.Contains("seeded ", StringComparison.Ordinal));
        }

        Assert.Equal(markerEtag, (await container.GetBlobClient(InstallEventsBlobStore.MarkerBlobName).GetPropertiesAsync()).Value.ETag);
        Assert.Equal(blobEtag, (await container.GetBlobClient("TenantA/dev-00042/installEvents.json").GetPropertiesAsync()).Value.ETag);
        Assert.Equal(SeedConstants.TotalDevices + 2, await CountAsync(container, prefix: null));
    }

    [Fact]
    public async Task Index_blob_equals_the_seed_function_and_is_rebuilt_from_the_blobs_when_missing()
    {
        var container = azurite.Container;
        var index = container.GetBlobClient(InstallEventsBlobStore.IndexBlobName);
        var expected = InstallJson.Serialize(SoftwareIndexBuilder.Build(DeviceCatalog.All().Select(InstallSeedData.BuildDocument))).ToString();
        Assert.Equal(expected, (await index.DownloadContentAsync()).Value.Content.ToString());

        // A container seeded by a version without the index (marker present, index absent): the next start scans the
        // device blobs and writes the same index, without re-seeding. Destructive on the shared container, restored
        // by the app under test itself.
        await index.DeleteAsync();
        try
        {
            await using var second = azurite.NewApp();
            await second.WaitUntilHealthyAsync();
            output.WriteLine(Assert.Single(second.Logs.Messages, m => m.Contains("software index rebuilt from 12000 device blobs", StringComparison.Ordinal)));
            Assert.DoesNotContain(second.Logs.Messages, m => m.Contains("seeded ", StringComparison.Ordinal));
        }
        finally
        {
            if (!(await index.ExistsAsync()).Value) await index.UploadAsync(BinaryData.FromString(expected), overwrite: true);
        }

        Assert.Equal(expected, (await index.DownloadContentAsync()).Value.Content.ToString());
    }

    [Fact]
    public async Task Partial_seed_without_marker_is_redone()
    {
        // Own container, so the shared one is never without its marker.
        const string name = "install-events-partial";
        var container = new BlobContainerClient(azurite.ConnectionString, name);
        await using (var first = azurite.NewApp(name))
        {
            await first.WaitUntilHealthyAsync();
        }

        // Simulate a crash mid-seed: marker missing, some blobs missing, one blob half-written.
        var missing = new[] { 100, 101, 7_100 }.Select(DeviceCatalog.Build).ToList();
        var torn = DeviceCatalog.Build(11_000);
        await container.GetBlobClient(InstallEventsBlobStore.MarkerBlobName).DeleteAsync();
        foreach (var d in missing) await container.GetBlobClient(InstallEventsBlobStore.BlobName(d.TenantId, d.Id)).DeleteAsync();
        await container.GetBlobClient(InstallEventsBlobStore.BlobName(torn.TenantId, torn.Id)).UploadAsync(BinaryData.FromString("{\"schemaVer"), overwrite: true);

        await using var second = azurite.NewApp(name);
        var observed = await second.WaitUntilHealthyAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, observed[0]);   // no marker -> unhealthy until re-seeded
        Assert.Single(second.Logs.Messages, m => m.Contains("seed completed: 12000 device blobs", StringComparison.Ordinal));
        Assert.True((await container.GetBlobClient(InstallEventsBlobStore.MarkerBlobName).ExistsAsync()).Value);
        Assert.Equal(SeedConstants.TenantADeviceCount, await CountAsync(container, "TenantA/"));
        Assert.Equal(SeedConstants.TotalDevices - SeedConstants.TenantADeviceCount, await CountAsync(container, "TenantB/"));
        foreach (var d in missing.Append(torn))
        {
            var content = (await container.GetBlobClient(InstallEventsBlobStore.BlobName(d.TenantId, d.Id)).DownloadContentAsync()).Value.Content;
            Assert.Equal(InstallJson.Serialize(InstallSeedData.BuildDocument(d)).ToString(), content.ToString());
        }

        await container.DeleteAsync();
    }

    [Fact]
    public void Seed_logs_progress_and_finishes_under_300_s()
    {
        // The shared app's cold start on an empty container (AzuriteFixture.InitializeAsync).
        var logs = azurite.App.Logs.Messages;
        var progress = logs.Where(m => m.Contains("seeded ", StringComparison.Ordinal)).ToList();
        Assert.Equal(12, progress.Count);   // every 1 000 blobs
        Assert.Contains(progress, m => m.Contains("seeded 12000/12000 device blobs", StringComparison.Ordinal));

        var completed = Assert.Single(logs, m => m.Contains("seed completed:", StringComparison.Ordinal));
        var ms = long.Parse(CompletedMs().Match(completed).Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"{completed}; cold start to /health 200: {azurite.ColdStartDuration.TotalSeconds:F1} s");
        Assert.Contains("(32 concurrent PUTs)", completed, StringComparison.Ordinal);
        Assert.InRange(ms, 1, 300_000);
    }

    [Fact]
    public async Task Seeded_blobs_equal_the_seed_function()
    {
        var container = azurite.Container;
        foreach (var index in new[] { 0, 1, 42, 6_999, 7_000, 11_999 })
        {
            var device = DeviceCatalog.Build(index);
            var blob = container.GetBlobClient(InstallEventsBlobStore.BlobName(device.TenantId, device.Id));
            var download = (await blob.DownloadContentAsync()).Value;

            Assert.Equal(InstallJson.Serialize(InstallSeedData.BuildDocument(device)).ToString(), download.Content.ToString());
            Assert.Equal("application/json", download.Details.ContentType);
        }
    }

    [Fact]
    public void Health_is_unhealthy_until_seed_completes()
    {
        var observed = azurite.ColdStartHealth;
        output.WriteLine($"/health observed: {string.Join(", ", observed.GroupBy(s => s).Select(g => $"{(int)g.Key} x{g.Count()}"))}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, observed[0]);
        Assert.Equal(HttpStatusCode.OK, observed[^1]);
        Assert.All(observed.SkipLast(1), s => Assert.Equal(HttpStatusCode.ServiceUnavailable, s));
    }

    [Fact]
    public async Task Health_is_unhealthy_without_signing_key()
    {
        await using var app = azurite.NewApp(withSigningKey: false);   // data is seeded; only the key is missing
        var report = await WaitForSeedCheckAsync(app);

        Assert.Equal(HealthStatus.Healthy, report.Entries["seed"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["blob"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["auth-config"].Status);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_unhealthy_when_storage_is_unreachable()
    {
        // Explicit BlobEndpoint on a port nothing listens on: the blob check and the seed both fail.
        var unreachable = Regex.Replace(azurite.ConnectionString, @"BlobEndpoint=[^;]+", "BlobEndpoint=http://127.0.0.1:9/devstoreaccount1");
        await using var app = azurite.NewApp(connectionString: unreachable);
        var health = app.Services.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["blob"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["seed"].Status);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static async Task<HealthReport> WaitForSeedCheckAsync(SoftwareInstallApp app)
    {
        var health = app.Services.GetRequiredService<HealthCheckService>();
        HealthReport report;
        var deadline = DateTime.UtcNow.AddMinutes(1);
        do
        {
            report = await health.CheckHealthAsync();
            if (report.Entries["seed"].Status == HealthStatus.Healthy) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);

        return report;
    }

    internal static async Task<int> CountAsync(BlobContainerClient container, string? prefix)
    {
        var count = 0;
        await foreach (var _ in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix })) count++;
        return count;
    }

    [GeneratedRegex(@"in (?<ms>\d+) ms")]
    private static partial Regex CompletedMs();
}
