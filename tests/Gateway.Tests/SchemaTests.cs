using System.IO.Compression;
using System.Net;
using System.Text.Json;
using SoR.Gateway.Tests.Support;

namespace SoR.Gateway.Tests;

/// <summary>The public (composed) schema served from the committed <c>gateway/gateway.far</c>, introspected in Production.</summary>
public sealed class SchemaTests(SchemaTests.Fixture fixture) : IClassFixture<SchemaTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public GatewayApp App { get; } = GatewayApp.Create();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await App.DisposeAsync();
    }

    private static readonly string[] DeviceFields =
        ["id", "hostname", "os", "ipAddress", "lastSeenAt", "tenantId", "patchEvents", "vulnerabilityEvents", "installEvents"];

    [Fact]
    public async Task Gateway_starts_with_committed_archive()
    {
        using var client = fixture.App.CreateClient();
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var fields = await FieldNamesAsync("Query");

        Assert.Contains("device", fields);
        Assert.Contains("devices", fields);
        Assert.DoesNotContain("deviceById", fields);
    }

    [Fact]
    public async Task Internal_lookup_cannot_be_called()
    {
        var response = await fixture.App.QueryAsync("""{ deviceById(id: "dev-00001") { id } }""", Tokens.Alice);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("`deviceById` does not exist", response.Errors.Single().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_has_the_nine_fields()
    {
        var fields = await FieldNamesAsync("Device");

        Assert.Equal(DeviceFields.Order(StringComparer.Ordinal), fields.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("patchEvents", "PatchEvent")]
    [InlineData("vulnerabilityEvents", "VulnerabilityEvent")]
    [InlineData("installEvents", "InstallEvent")]
    public async Task Extension_fields_are_nullable_lists_in_the_composed_schema(string field, string item)
    {
        // Plan §4.2: nullable, so an outage or a denial nulls the field and not the device.
        var type = await FieldTypeAsync("Device", field);

        Assert.Equal("LIST", type.GetProperty("kind").GetString());
        var of = type.GetProperty("ofType");
        Assert.Equal("NON_NULL", of.GetProperty("kind").GetString());
        Assert.Equal(item, of.GetProperty("ofType").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("patches", "Patch")]
    [InlineData("cves", "Cve")]
    public async Task Root_catalogs_are_nullable_lists_in_the_composed_schema(string field, string item)
    {
        var type = await FieldTypeAsync("Query", field);

        Assert.Equal("LIST", type.GetProperty("kind").GetString());
        Assert.Equal(item, type.GetProperty("ofType").GetProperty("ofType").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not a zip")]
    [InlineData("zip without a gateway configuration")]
    public async Task Unusable_archive_fails_startup(string kind)
    {
        // Fusion alone would wait for a usable archive forever; the gateway refuses to start instead.
        var path = Path.Combine(Path.GetTempPath(), $"gateway-{Guid.NewGuid():N}.far");
        if (kind == "not a zip")
        {
            await File.WriteAllBytesAsync(path, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31))]);
        }
        else if (kind == "zip without a gateway configuration")
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            await writer.WriteAsync("not an archive");
        }

        try
        {
            await using var app = GatewayApp.Create(archive: path);

            var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

            Assert.Contains(path, error.ToString(), StringComparison.Ordinal);
            Assert.Contains("scripts/compose-schema.sh", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<IReadOnlyList<string>> FieldNamesAsync(string typeName)
    {
        var response = await fixture.App.QueryAsync($$"""{ __type(name: "{{typeName}}") { fields { name } } }""", Tokens.Alice);
        Assert.False(response.HasErrors, response.ToString());
        return [.. response.Data.GetProperty("__type").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
    }

    private async Task<JsonElement> FieldTypeAsync(string typeName, string fieldName)
    {
        var response = await fixture.App.QueryAsync(
            $$"""{ __type(name: "{{typeName}}") { fields { name type { kind name ofType { kind name ofType { kind name } } } } } }""",
            Tokens.Alice);
        Assert.False(response.HasErrors, response.ToString());
        return response.Data.GetProperty("__type").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == fieldName)
            .GetProperty("type");
    }
}
