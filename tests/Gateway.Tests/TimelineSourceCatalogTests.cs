using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HotChocolate.Language;
using SoR.Gateway.Timeline;
using SoR.Gateway.Tests.Support;

namespace SoR.Gateway.Tests;

public sealed class TimelineSourceCatalogTests
{
    [Fact]
    public void Source_project_discovery_parses_compact_settings_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"source-projects-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "compliance-settings.json"), "{\"name\":\"Compliance\",\"transports\":{}}");

            var project = Assert.Single(TimelineCatalogGenerator.SourceProjects(directory));

            Assert.Equal("compliance", project.SchemaBaseName);
            Assert.Equal("Compliance", project.ProjectName);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Archive_snapshot_remains_usable_after_the_source_file_is_replaced()
    {
        var archive = Path.Combine(Path.GetTempPath(), $"gateway-{Guid.NewGuid():N}.far");
        File.Copy(Repo.Archive, archive);
        try
        {
            var originalHash = Sha256(archive);
            var snapshot = await GatewayArchive.LoadSnapshotAsync(archive);

            await File.WriteAllTextAsync(archive, "replacement that is not a FAR");

            Assert.Contains("DeviceDirectory", snapshot.SourceSchemaNames);
            Assert.Equal(originalHash, snapshot.SchemaHash);
            Assert.Contains(snapshot.Schema.Definitions, definition => definition is HotChocolate.Language.ObjectTypeDefinitionNode node && node.Name.Value == "Device");
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task Public_endpoint_serves_the_startup_snapshot_without_caching()
    {
        await using var app = GatewayApp.Create();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/timeline-sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(Sha256(Repo.Archive), body.RootElement.GetProperty("schemaHash").GetString());
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("sources").ValueKind);
    }

    [Fact]
    public void Catalog_whose_hash_does_not_match_the_archive_fails_startup()
    {
        var catalog = TempFile("""{"version":1,"schemaHash":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","sources":[]}""");
        try
        {
            using var app = GatewayApp.Create(catalog: catalog);

            var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

            Assert.Contains("does not match", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(catalog, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(catalog);
        }
    }

    [Fact]
    public void Generator_validates_the_declared_field_and_writes_the_paired_hash()
    {
        using var files = CatalogFiles.Valid();

        var catalog = TimelineCatalogGenerator.Generate(files.Archive, files.Output, [files.Descriptor], [files.Schema]);

        Assert.Equal(Sha256(files.Archive), catalog.SchemaHash);
        var source = Assert.Single(catalog.Sources);
        Assert.Equal("audit", source.Id);
        Assert.Equal("auditTimeline", source.Field);
        Assert.Equal(["COMPLETE", "FAILED"], source.Statuses);
        Assert.Equal(JsonSerializer.Serialize(catalog, TimelineSourceCatalog.JsonOptions) + "\n", File.ReadAllText(files.Output));
    }

    [Fact]
    public void Directory_generator_uses_only_schemas_paired_with_composition_settings()
    {
        using var files = CatalogFiles.Valid();
        File.WriteAllText(Path.Combine(files.SchemaDirectory, "orphan.graphqls"), File.ReadAllText(files.Schema));

        var catalog = TimelineCatalogGenerator.GenerateFromDirectories(
            files.Archive,
            files.Output,
            files.SourceRoot,
            files.SchemaDirectory);

        Assert.Equal("auditTimeline", Assert.Single(catalog.Sources).Field);
    }

    [Theory]
    [InlineData("notInArchive")]
    [InlineData("hostname")]
    public void Catalog_field_that_is_absent_or_incompatible_with_the_archive_fails_startup(string field)
    {
        var catalog = TempFile(CatalogJson(Sha256(Repo.Archive), field));
        try
        {
            using var app = GatewayApp.Create(catalog: catalog);

            var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

            Assert.Contains($"Device.{field}", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(catalog);
        }
    }

    [Fact]
    public void Catalog_fails_validation_when_the_archive_common_type_shape_is_incompatible()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var catalog = TempFile(CatalogJson(hash, "auditTimeline"));
        var schema = Utf8GraphQLParser.Parse("""
            scalar DateTime
            type Device { auditTimeline(since: DateTime, until: DateTime): [TimelineEvent!] }
            type TimelineEvent {
              id: ID!
              occurredAt: DateTime!
              label: String!
              title: String!
              subtitle: String!
              status: String!
              severity: String
              details: [TimelineDetail!]!
            }
            type TimelineDetail { label: String!, value: String!, mono: String! }
            """);
        try
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                TimelineSourceCatalogLoader.Load(catalog, hash, "test.far", schema));

            Assert.Contains("TimelineDetail", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(catalog);
        }
    }

    [Fact]
    public void Generator_rejects_a_descriptor_field_missing_from_the_exported_schema()
    {
        using var files = CatalogFiles.Valid(descriptorField: "undeclaredTimeline");

        var error = Assert.Throws<InvalidDataException>(() =>
            TimelineCatalogGenerator.Generate(files.Archive, files.Output, [files.Descriptor], [files.Schema]));

        Assert.Contains("undeclaredTimeline", error.Message, StringComparison.Ordinal);
        Assert.Contains(files.Schema, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_rejects_an_incompatible_timeline_field_shape()
    {
        using var files = CatalogFiles.Valid(fieldType: "[TimelineEvent!]!");

        var error = Assert.Throws<InvalidDataException>(() =>
            TimelineCatalogGenerator.Generate(files.Archive, files.Output, [files.Descriptor], [files.Schema]));

        Assert.Contains("[TimelineEvent!]", error.Message, StringComparison.Ordinal);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string TempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"timeline-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string CatalogJson(string hash, string field) => JsonSerializer.Serialize(new
    {
        version = 1,
        schemaHash = hash,
        sources = new[]
        {
            new
            {
                id = "audit",
                field,
                name = "Audit history",
                icon = "fact_check",
                color = "#345678",
                history = "Audit events",
                statuses = new[] { "COMPLETE", "FAILED" },
                contractVersion = 1,
            },
        },
    });

    private sealed class CatalogFiles : IDisposable
    {
        private CatalogFiles(string directory)
        {
            Directory = directory;
        }

        public string Directory { get; }
        public string Archive => Path.Combine(Directory, "gateway.far");
        public string SourceRoot => Path.Combine(Directory, "src");
        public string Descriptor => Path.Combine(SourceRoot, "Audit", "timeline.json");
        public string SchemaDirectory => Path.Combine(Directory, "schemas");
        public string Schema => Path.Combine(SchemaDirectory, "audit.graphqls");
        public string Output => Path.Combine(Directory, "timeline-sources.json");

        public static CatalogFiles Valid(string descriptorField = "auditTimeline", string fieldType = "[TimelineEvent!]")
        {
            var files = new CatalogFiles(Path.Combine(Path.GetTempPath(), $"timeline-catalog-{Guid.NewGuid():N}"));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(files.Descriptor)!);
            System.IO.Directory.CreateDirectory(files.SchemaDirectory);
            File.WriteAllText(files.Archive, "paired-far-bytes");
            File.WriteAllText(Path.Combine(files.SchemaDirectory, "audit-settings.json"), "{\"name\":\"Audit\"}");
            File.WriteAllText(files.Descriptor, $$"""
                {
                  "id": "audit",
                  "field": "{{descriptorField}}",
                  "name": "Audit history",
                  "icon": "fact_check",
                  "color": "#345678",
                  "history": "Audit events",
                  "statuses": ["COMPLETE", "FAILED"],
                  "contractVersion": 1
                }
                """);
            File.WriteAllText(files.Schema, $$"""
                scalar DateTime
                type Device { id: ID!, auditTimeline(since: DateTime, until: DateTime): {{fieldType}} }
                type TimelineEvent {
                  id: ID!
                  occurredAt: DateTime!
                  label: String!
                  title: String!
                  subtitle: String!
                  status: String!
                  severity: String
                  details: [TimelineDetail!]!
                }
                type TimelineDetail { label: String!, value: String!, mono: Boolean! }
                """);
            return files;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
