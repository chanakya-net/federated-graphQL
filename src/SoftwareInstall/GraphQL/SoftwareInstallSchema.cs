using HotChocolate.Execution.Configuration;

namespace SoR.SoftwareInstall.GraphQL;

public static class SoftwareInstallSchema
{
    /// <summary>Source-schema name: the exported settings' <c>"name"</c> and the gateway's client name.</summary>
    public const string Name = "SoftwareInstall";

    /// <summary>The schema definition, shared by <c>Program</c> and the in-process schema tests.</summary>
    public static IRequestExecutorBuilder AddSoftwareInstallTypes(this IRequestExecutorBuilder builder) =>
        builder
            .AddAuthorization()
            .AddQueryType<Query>()
            .AddTypeExtension<DeviceExtensions>();
}
