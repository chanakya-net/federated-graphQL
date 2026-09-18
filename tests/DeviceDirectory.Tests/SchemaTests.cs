using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using SoR.DeviceDirectory.GraphQL;
using SoR.DeviceDirectory.Tests.Support;

namespace SoR.DeviceDirectory.Tests;

/// <summary>
/// Conformance with contracts/device-directory.graphqls, checked on the in-process schema (contracts/README.md):
/// type and field names, argument names, types (nullability included), default values and required directives.
/// Extra directives in the real schema (@authorize, @cost) are fine.
/// </summary>
public sealed class SchemaTests
{
    [Fact]
    public async Task Schema_matches_contract()
    {
        var contract = Utf8GraphQLParser.Parse(await File.ReadAllTextAsync(Repo.PathOf("contracts", "device-directory.graphqls")));
        var actual = await BuildSdlAsync();

        foreach (var expectedType in contract.Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            var type = actual.Definitions.OfType<ObjectTypeDefinitionNode>().SingleOrDefault(t => t.Name.Value == expectedType.Name.Value);
            Assert.True(type is not null, $"type {expectedType.Name.Value} missing");

            // Exactly the contract's fields: nothing missing, nothing extra.
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

        var scalars = actual.Definitions.OfType<ScalarTypeDefinitionNode>().Select(s => s.Name.Value).ToHashSet();
        Assert.All(contract.Definitions.OfType<ScalarTypeDefinitionNode>(), s => Assert.Contains(s.Name.Value, scalars));
    }

    [Fact]
    public async Task Device_has_exactly_the_six_contract_fields()
    {
        var device = Type(await BuildSdlAsync(), "Device");

        Assert.Equal(
            ["id: ID!", "hostname: String!", "os: String!", "ipAddress: String!", "lastSeenAt: DateTime!", "tenantId: String!"],
            device.Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
    }

    [Fact]
    public async Task Device_lookup_is_nullable_and_devices_defaults_are_25_and_0()
    {
        var query = Type(await BuildSdlAsync(), "Query");

        var device = query.Fields.Single(f => f.Name.Value == "device");
        Assert.Equal("Device", device.Type.ToString());   // nullable: another tenant's device is null, not an error
        Assert.Equal("id: ID!", Assert.Single(device.Arguments).ToString());
        Assert.Contains(device.Directives, d => d.Name.Value == "lookup");

        var devices = query.Fields.Single(f => f.Name.Value == "devices");
        Assert.Equal("DeviceSearchResult!", devices.Type.ToString());
        Assert.Equal(["search: String", "first: Int! = 25", "offset: Int! = 0"], devices.Arguments.Select(a => a.ToString()));
    }

    [Fact]
    public async Task Every_query_field_requires_authentication()
    {
        var query = Type(await BuildSdlAsync(), "Query");

        Assert.Contains(query.Directives, d => d.Name.Value == "authorize");
    }

    [Fact]
    public async Task No_key_directive_and_no_internal_lookup()
    {
        // Fusion v2 derives the key from the lookup argument (docs/version-facts.md §2); device is public.
        var sdl = (await BuildSchemaAsync()).ToString();

        Assert.DoesNotContain("@key", sdl, StringComparison.Ordinal);
        Assert.DoesNotContain("@internal", sdl, StringComparison.Ordinal);
    }

    private static ObjectTypeDefinitionNode Type(DocumentNode doc, string name) =>
        doc.Definitions.OfType<ObjectTypeDefinitionNode>().Single(t => t.Name.Value == name);

    private static async Task<DocumentNode> BuildSdlAsync() =>
        Utf8GraphQLParser.Parse((await BuildSchemaAsync()).ToString()!);

    /// <summary>Same schema definition as Program (shared extension), without the host or any backing service.</summary>
    internal static async Task<object> BuildSchemaAsync() =>
        await new ServiceCollection()
            .AddGraphQLServer(DeviceDirectorySchema.Name)
            .AddDeviceDirectoryTypes()
            .BuildSchemaAsync(DeviceDirectorySchema.Name);
}
