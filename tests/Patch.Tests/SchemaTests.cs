using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using SoR.Patch.GraphQL;
using SoR.Patch.Tests.Support;
using SoR.Shared.Auth;

namespace SoR.Patch.Tests;

/// <summary>
/// Conformance with contracts/patch.graphqls, checked on the in-process schema (contracts/README.md): type and
/// field names, argument names, types (nullability included), default values, enum values and required
/// directives. Extra directives in the real schema (@authorize, @cost) are fine.
/// </summary>
public sealed class SchemaTests
{
    [Fact]
    public async Task Schema_matches_contract()
    {
        var contract = Utf8GraphQLParser.Parse(await File.ReadAllTextAsync(Repo.PathOf("contracts", "patch.graphqls")));
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
    public async Task PatchEvents_field_is_nullable_list()
    {
        // Guards plan §4.2; do not delete. `patchEvents` must stay a NULLABLE list of non-null PatchEvent:
        // a denial or outage on a non-null field would propagate and null the whole Device (and, through the
        // gateway, the UI's entire timeline). Empty list = no events; null + error = degraded or denied.
        var patchEvents = Type(await BuildSdlAsync(), "Device").Fields.Single(f => f.Name.Value == "patchEvents");

        Assert.Equal("[PatchEvent!]", patchEvents.Type.ToString());
        Assert.IsType<ListTypeNode>(patchEvents.Type);   // not NonNullTypeNode
        Assert.Equal(["since: DateTime", "until: DateTime"], patchEvents.Arguments.Select(a => a.ToString()));
    }

    [Fact]
    public async Task DeviceById_is_an_internal_lookup()
    {
        var deviceById = Type(await BuildSdlAsync(), "Query").Fields.Single(f => f.Name.Value == "deviceById");

        Assert.Equal("Device", deviceById.Type.ToString());   // as in the contract; the resolver never returns null
        Assert.Equal("id: ID!", Assert.Single(deviceById.Arguments).ToString());
        Assert.Contains(deviceById.Directives, d => d.Name.Value == "lookup");
        Assert.Contains(deviceById.Directives, d => d.Name.Value == "internal");
        // Tenant-scoped via the token, NOT behind the service policy (the Query-level @authorize still applies).
        Assert.DoesNotContain(deviceById.Directives, d => d.Name.Value == "authorize");
    }

    [Fact]
    public async Task Patches_is_a_nullable_list_with_search_and_defaults_25_and_0()
    {
        var patches = Type(await BuildSdlAsync(), "Query").Fields.Single(f => f.Name.Value == "patches");

        Assert.Equal("[Patch!]", patches.Type.ToString());   // nullable: a denial must not null all of `data`
        Assert.Equal(["search: String", "first: Int! = 25", "offset: Int! = 0"], patches.Arguments.Select(a => a.ToString()));
    }

    [Fact]
    public async Task DevicesWithPatches_is_nullable_and_returns_device_stubs()
    {
        // The reverse lookup (patches -> devices). Nullable for the same reason as `patches`; its items carry the
        // entity stub `Device!`, which the gateway completes through Device Directory's `device(id)` lookup.
        var sdl = await BuildSdlAsync();
        var field = Type(sdl, "Query").Fields.Single(f => f.Name.Value == "devicesWithPatches");

        Assert.Equal("PatchDeviceMatches", field.Type.ToString());
        Assert.IsNotType<NonNullTypeNode>(field.Type);
        Assert.Equal(["patchIds: [ID!]!", "deviceIds: [ID!]", "first: Int! = 25", "offset: Int! = 0"], field.Arguments.Select(a => a.ToString()));
        Assert.Equal(["items: [PatchDeviceMatch!]!", "totalCount: Int!", "matches: [PatchMatch!]!"], Type(sdl, "PatchDeviceMatches").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Equal(["device: Device!", "events: [PatchEvent!]!"], Type(sdl, "PatchDeviceMatch").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
        Assert.Equal(["patchId: ID!", "deviceIds: [ID!]!"], Type(sdl, "PatchMatch").Fields.Select(f => $"{f.Name.Value}: {f.Type}"));
    }

    [Fact]
    public async Task Guarded_fields_use_the_service_policy_and_Query_requires_authentication()
    {
        var sdl = await BuildSdlAsync();
        var query = Type(sdl, "Query");
        var device = Type(sdl, "Device");

        Assert.Contains(query.Directives, d => d.Name.Value == "authorize");
        foreach (var field in new[]
        {
            query.Fields.Single(f => f.Name.Value == "patches"),
            query.Fields.Single(f => f.Name.Value == "devicesWithPatches"),
            device.Fields.Single(f => f.Name.Value == "patchEvents"),
        })
        {
            var authorize = Assert.Single(field.Directives, d => d.Name.Value == "authorize");
            Assert.Equal($"@authorize(policy: \"{DevAuth.ServiceAccessPolicy}\")", authorize.ToString());
        }
    }

    [Fact]
    public async Task No_key_directive()
    {
        // Fusion v2 derives the key from the lookup argument (docs/version-facts.md §2).
        Assert.DoesNotContain("@key", (await BuildSchemaAsync()).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_enum_strings_are_the_contract_names()
    {
        Assert.Equal(["APPLIED", "FAILED", "PENDING"], Enum.GetValues<PatchStatus>().Select(StoredEnum.ToStored));
        Assert.Equal(["CRITICAL", "HIGH", "MEDIUM", "LOW"], Enum.GetValues<PatchSeverity>().Select(StoredEnum.ToStored));
        Assert.All(Enum.GetValues<PatchStatus>(), s => Assert.Equal(s, StoredEnum.Parse<PatchStatus>(StoredEnum.ToStored(s))));
        Assert.All(Enum.GetValues<PatchSeverity>(), s => Assert.Equal(s, StoredEnum.Parse<PatchSeverity>(StoredEnum.ToStored(s))));
    }

    private static ObjectTypeDefinitionNode Type(DocumentNode doc, string name) =>
        doc.Definitions.OfType<ObjectTypeDefinitionNode>().Single(t => t.Name.Value == name);

    private static async Task<DocumentNode> BuildSdlAsync() =>
        Utf8GraphQLParser.Parse((await BuildSchemaAsync()).ToString()!);

    /// <summary>Same schema definition as Program (shared extension), without the host or any backing service.</summary>
    private static async Task<object> BuildSchemaAsync() =>
        await new ServiceCollection()
            .AddGraphQLServer(PatchSchema.Name)
            .AddPatchTypes()
            .BuildSchemaAsync(PatchSchema.Name);
}
