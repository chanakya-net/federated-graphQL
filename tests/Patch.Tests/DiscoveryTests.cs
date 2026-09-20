using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using SoR.Patch.Data;
using SoR.Patch.Seeding;
using SoR.Patch.Tests.Support;
using SoR.Shared.Seeding;

namespace SoR.Patch.Tests;

[Collection(MongoCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DiscoveryTests(MongoFixture mongo)
{
    [Theory]
    [InlineData("items { device { id } }", false)]
    [InlineData("rows: items { ... on PatchDeviceMatch { device { id } ev: events { id } } }", true)]
    public async Task Event_documents_and_catalog_are_read_only_when_events_are_selected(string selection, bool includeEvents)
    {
        var app = await mongo.SeededAppAsync();
        var commands = new ConcurrentQueue<string>();
        var settings = MongoClientSettings.FromConnectionString(mongo.ConnectionString);
        settings.ClusterConfigurator = c => c.Subscribe<CommandStartedEvent>(e => commands.Enqueue(e.CommandName));
        using var client = new MongoClient(settings);
        using var customized = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatchStore>();
            services.AddSingleton<IPatchStore>(new PatchStore(client.GetDatabase(MongoFixture.SharedDatabase), new SeedState()));
        }));
        using var http = customized.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { query = "{ devicesWithPatches(patchIds: [\"" + PatchSeedData.PatchId(7) + "\"], first: 1) { totalCount " + selection + " } }" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Tokens.Alice);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("errors", out _), body);
        Assert.True(document.RootElement.GetProperty("data").GetProperty("devicesWithPatches").GetProperty("totalCount").GetInt32() > 0);
        Assert.Contains("aggregate", commands);
        if (includeEvents) Assert.Contains("find", commands);
        else Assert.DoesNotContain("find", commands);
    }
}
