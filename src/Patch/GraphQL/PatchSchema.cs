using HotChocolate.Execution.Configuration;

namespace SoR.Patch.GraphQL;

public static class PatchSchema
{
    /// <summary>Source-schema name: the exported settings' <c>"name"</c> and the gateway's client name.</summary>
    public const string Name = "Patch";

    /// <summary>The schema definition, shared by <c>Program</c> and the in-process schema tests.</summary>
    public static IRequestExecutorBuilder AddPatchTypes(this IRequestExecutorBuilder builder) =>
        builder
            .AddAuthorization()
            .AddQueryType<Query>()
            .AddTypeExtension<DeviceExtensions>();
}
