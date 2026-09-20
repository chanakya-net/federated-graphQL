using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Tests.Support;

namespace SoR.SoftwareInstall.Tests;

/// <summary>
/// Conformance with contracts/software-install.graphqls, checked on the in-process schema (contracts/README.md):
/// type and field names, argument names, types (nullability included), enum values and required directives.
/// Extra directives in the real schema (@authorize, @cost) are fine.
/// </summary>
public sealed class SchemaTests
{
    [Fact]
    public async Task Schema_matches_contract()
    {
        var contract = Utf8GraphQLParser.Parse(await File.ReadAllTextAsync(Repo.PathOf("contracts", "software-install.graphqls")));
        var actual = await BuildSdlAsync();

        foreach (var expectedType in contract.Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            var type = actual.Definitions.OfType<ObjectTypeDefinitionNode>().SingleOrDefault(t => t.Name.Value == expectedType.Name.Value);
            Assert.True(type is not null, $"type {expectedType.Name.Value} missing");

            // Exactly the contract's fields: nothing missing, nothing extra (Query has deviceById only).
            Assert.Equal(
                expectedType.Fields.Select(f => f.Name.Value).Order(StringComparer.Ordinal),
                type.Fields.Select(f => f.Name.Value).Order(StringComparer.Ordinal));

            foreach (var expectedField in expectedType.Fields)
            {
                var field = type.Fields.Single(f => f.Name.Value == expectedField.Name.Value);
                var at = $"{type.Name.Value}.{field.Name.Value}";
                Assert.True(expectedField.Type.ToString() == field.Type.ToString(), $"{at}: {field.Type} != contract {expectedField.Type}");

                Assert.Equal(expectedField.Arguments.Select(a => a.Name.Value), field.Arguments.Select(a => a.Name.Value));
                foreach (var expectedArg in expectedField.Arguments)
                {
                    var arg = field.Arguments.Single(a => a.Name.Value == expectedArg.Name.Value);
                    Assert.True(expectedArg.Type.ToString() == arg.Type.ToString(), $"{at}({arg.Name.Value}): type {arg.Type} != contract {expectedArg.Type}");
                    Assert.True(expectedArg.DefaultValue?.ToString() == arg.DefaultValue?.ToString(),
                        $"{at}({arg.Name.Value}): default {arg.DefaultValue} != contract {expectedArg.DefaultValue}");
                }

                foreach (var directive in expectedField.Directives)
                {
                    Assert.True(field.Directives.Any(d => d.Name.Value == directive.Name.Value), $"{at}: @{directive.Name.Value} missing");
                }
            }
        }

        foreach (var expectedEnum in contract.Definitions.OfType<EnumTypeDefinitionNode>())
        {
            var actualEnum = actual.Definitions.OfType<EnumTypeDefinitionNode>().SingleOrDefault(e => e.Name.Value == expectedEnum.Name.Value);
            Assert.True(actualEnum is not null, $"enum {expectedEnum.Name.Value} missing");
            Assert.Equal(expectedEnum.Values.Select(v => v.Name.Value), actualEnum.Values.Select(v => v.Name.Value));
        }

        var scalars = actual.Definitions.OfType<ScalarTypeDefinitionNode>().Select(s => s.Name.Value).ToHashSet();
        Assert.All(contract.Definitions.OfType<ScalarTypeDefinitionNode>(), s => Assert.Contains(s.Name.Value, scalars));
    }

    [Fact]
    public async Task InstallEvents_field_is_nullable_list()
    {
        // Guards plan §4.2; do not delete. A denial or outage on a non-null list would propagate to Device (and
        // through the gateway to `device`), wiping the whole timeline instead of one section.
        var installEvents = Type(await BuildSdlAsync(), "Device").Fields.Single(f => f.Name.Value == "installEvents");

        Assert.Equal("[InstallEvent!]", installEvents.Type.ToString());
        Assert.IsType<ListTypeNode>(installEvents.Type);   // the list itself is nullable (not NonNullTypeNode)
        Assert.Equal(["since: DateTime", "until: DateTime"], installEvents.Arguments.Select(a => a.ToString()));
    }

    [Fact]
    public async Task InstallEvents_requires_the_service_policy()
    {
        var installEvents = Type(await BuildSdlAsync(), "Device").Fields.Single(f => f.Name.Value == "installEvents");

        var authorize = Assert.Single(installEvents.Directives, d => d.Name.Value == "authorize");
        Assert.Equal("policy: \"ServiceAccess\"", Assert.Single(authorize.Arguments).ToString());
    }

    [Fact]
    public async Task Software_catalog_and_reverse_lookup_are_nullable_and_guarded()
    {
        var sdl = await BuildSdlAsync();
        var query = Type(sdl, "Query");

        var software = query.Fields.Single(f => f.Name.Value == "software");
        Assert.Equal("[Software!]", software.Type.ToString());   // nullable: a denial must not null all of `data`
        Assert.Equal(["search: String", "first: Int! = 25", "offset: Int! = 0"], software.Arguments.Select(a => a.ToString()));

        // The reverse lookup (software -> devices): its items carry the entity stub `Device!`, completed by the gateway
        // through Device Directory's `device(id)` lookup.
        var find = query.Fields.Single(f => f.Name.Value == "devicesWithSoftware");
        Assert.Equal("SoftwareDeviceMatches", find.Type.ToString());
        Assert.IsNotType<NonNullTypeNode>(find.Type);
        Assert.Equal(["software: [SoftwareKeyInput!]!", "deviceIds: [ID!]", "first: Int! = 25", "offset: Int! = 0"], find.Arguments.Select(a => a.ToString()));
        Assert.Equal(["items: [SoftwareDeviceMatch!]!", "totalCount: Int!", "matches: [SoftwareMatch!]!"], Type(sdl, "SoftwareDeviceMatches").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Equal(["device: Device!", "events: [InstallEvent!]!"], Type(sdl, "SoftwareDeviceMatch").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Equal(["name: String!", "version: String", "deviceIds: [ID!]!"], Type(sdl, "SoftwareMatch").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        var key = sdl.Definitions.OfType<InputObjectTypeDefinitionNode>().Single(t => t.Name.Value == "SoftwareKeyInput");
        Assert.Equal(["name: String!", "version: String"], key.Fields.Select(f => $"{f.Name.Value}: {f.Type}"));

        foreach (var field in new[] { software, find })
        {
            var authorize = Assert.Single(field.Directives, d => d.Name.Value == "authorize");
            Assert.Equal("policy: \"ServiceAccess\"", Assert.Single(authorize.Arguments).ToString());
        }
    }

    [Fact]
    public async Task DeviceById_is_an_internal_lookup()
    {
        var query = Type(await BuildSdlAsync(), "Query");

        Assert.Equal(["deviceById", "software", "devicesWithSoftware"], query.Fields.Select(f => f.Name.Value));
        var lookup = query.Fields[0];
        Assert.Equal("Device", lookup.Type.ToString());
        Assert.Equal("id: ID!", Assert.Single(lookup.Arguments).ToString());
        Assert.Contains(lookup.Directives, d => d.Name.Value == "lookup");
        Assert.Contains(lookup.Directives, d => d.Name.Value == "internal");
        // Behind authentication (type-level @authorize) but NOT behind the service policy.
        Assert.DoesNotContain(lookup.Directives, d => d.Name.Value == "authorize");
        Assert.Contains(query.Directives, d => d.Name.Value == "authorize");
    }

    [Fact]
    public void DeviceById_returns_a_stub_for_any_id()
    {
        // Never null: a null Device would make installEvents null without an error.
        Assert.Equal(new Device("dev-00042"), new Query().GetDeviceById("dev-00042"));
        Assert.Equal(new Device("anything"), new Query().GetDeviceById("anything"));
    }

    [Fact]
    public async Task No_key_directive()
    {
        // Fusion v2 derives the key from the lookup argument (docs/version-facts.md §2).
        var sdl = (await BuildSchemaAsync()).ToString();

        Assert.DoesNotContain("@key", sdl, StringComparison.Ordinal);
    }

    private static ObjectTypeDefinitionNode Type(DocumentNode doc, string name) =>
        doc.Definitions.OfType<ObjectTypeDefinitionNode>().Single(t => t.Name.Value == name);

    private static async Task<DocumentNode> BuildSdlAsync() =>
        Utf8GraphQLParser.Parse((await BuildSchemaAsync()).ToString()!);

    /// <summary>Same schema definition as Program (shared extension), without the host or any backing service.</summary>
    private static async Task<object> BuildSchemaAsync() =>
        await new ServiceCollection()
            .AddGraphQLServer(SoftwareInstallSchema.Name)
            .AddSoftwareInstallTypes()
            .BuildSchemaAsync(SoftwareInstallSchema.Name);
}
