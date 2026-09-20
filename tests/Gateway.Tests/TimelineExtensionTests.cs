using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SoR.Gateway.Tests.Support;
using SoR.Gateway.Timeline;
using SoR.Shared.Auth;

namespace SoR.Gateway.Tests;

/// <summary>A new source exists only in deployment artifacts/configuration, never in gateway production code.</summary>
public sealed class TimelineExtensionTests
{
    [Fact]
    public async Task Additional_composed_source_is_discovered_and_queried_with_forwarded_authorization()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"timeline-extension-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var schema = Path.Combine(directory, "compliance.graphqls");
            await File.WriteAllTextAsync(schema, """
                schema { query: Query }
                type Query { deviceById(id: ID!): Device @lookup @internal }
                type Device { id: ID!, complianceTimeline(since: DateTime, until: DateTime): [TimelineEvent!] }
                type TimelineEvent @shareable {
                  id: ID!
                  occurredAt: DateTime!
                  label: String!
                  title: String!
                  subtitle: String!
                  status: String!
                  severity: String
                  details: [TimelineDetail!]!
                }
                type TimelineDetail @shareable { label: String!, value: String!, mono: Boolean! }
                scalar DateTime
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "compliance-settings.json"), """
                {"name":"Compliance","transports":{"http":{"url":"http://compliance:8080/graphql"}}}
                """);
            var descriptor = Path.Combine(directory, "timeline.json");
            await File.WriteAllTextAsync(descriptor, """
                {"id":"compliance","field":"complianceTimeline","name":"Compliance","icon":"verified",
                 "color":"#7c3aed","history":"compliance","statuses":["PASS"],"contractVersion":1}
                """);
            var archive = Path.Combine(directory, "gateway.far");
            await ComposeAsync(archive, schema);
            var catalogPath = Path.Combine(directory, "timeline-sources.json");
            TimelineCatalogGenerator.Generate(archive, catalogPath, [descriptor], [schema]);
            var deviceDirectory = await FakeSubgraph.DeviceDirectoryAsync();
            var compliance = await FakeSubgraph.RespondingAsync("""
                {"data":{"deviceById":{"complianceTimeline":[{
                  "id":"compliance-1","occurredAt":"2026-09-20T00:00:00Z","label":"CIS",
                  "title":"Disk encryption enabled","subtitle":"Endpoint policy","status":"PASS","severity":null,
                  "details":[{"label":"Policy","value":"CIS 1.2","mono":false}]
                }]}}}
                """);
            var settings = new Dictionary<string, string>
            {
                [DevAuth.SigningKeyEnv] = Tokens.SigningKey,
                [GatewaySettings.ArchiveVariable] = archive,
                [GatewaySettings.CatalogVariable] = catalogPath,
                ["SUBGRAPH_DEVICEDIRECTORY_URL"] = deviceDirectory.Url.ToString(),
                ["SUBGRAPH_COMPLIANCE_URL"] = compliance.Url.ToString(),
                ["SUBGRAPH_PATCH_URL"] = FakeSubgraph.Unreachable.ToString(),
                ["SUBGRAPH_VULNERABILITY_URL"] = FakeSubgraph.Unreachable.ToString(),
                ["SUBGRAPH_SOFTWAREINSTALL_URL"] = FakeSubgraph.Unreachable.ToString(),
                ["SUBGRAPH_DEVICESEARCH_URL"] = FakeSubgraph.Unreachable.ToString(),
            };
            await using var app = new GatewayApp(settings, [deviceDirectory, compliance]);
            using var client = app.CreateClient();
            using var catalogResponse = await client.GetAsync("/timeline-sources");
            Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
            var advertised = Assert.Single(catalog.RootElement.GetProperty("sources").EnumerateArray());
            Assert.Equal("compliance", advertised.GetProperty("id").GetString());
            // Exactly the generic client's strategy: field comes from metadata, selection is domain-independent.
            var field = advertised.GetProperty("field").GetString();
            var response = await app.QueryAsync($$"""
                { device(id: "dev-00001") { id hostname {{field}} {
                    id occurredAt label title subtitle status severity details { label value mono }
                } } }
                """, Tokens.Alice);
            Assert.False(response.HasErrors, response.ToString());
            Assert.Equal("alpha", response.Data.GetProperty("device").GetProperty("hostname").GetString());
            var evt = Assert.Single(response.Data.GetProperty("device").GetProperty(field!).EnumerateArray());
            Assert.Equal("Disk encryption enabled", evt.GetProperty("title").GetString());
            Assert.Equal("CIS 1.2", evt.GetProperty("details")[0].GetProperty("value").GetString());
            Assert.Equal($"Bearer {Tokens.Alice}", Assert.Single(compliance.Requests).Authorization);
            Assert.Equal($"Bearer {Tokens.Alice}", Assert.Single(deviceDirectory.Requests).Authorization);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ComposeAsync(string archive, string additionalSchema)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "nitro", "fusion", "compose" }) psi.ArgumentList.Add(argument);
        var composedSchemas = Directory.GetFiles(Repo.PathOf("schemas"), "*-settings.json")
            .Order(StringComparer.Ordinal)
            .Select(settings => settings[..^"-settings.json".Length] + ".graphqls");
        foreach (var schema in composedSchemas.Append(additionalSchema))
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(schema);
        }
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(archive);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start schema composer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }
}
