using HotChocolate.Execution.Configuration;

namespace SoR.DeviceDirectory.GraphQL;

public static class DeviceDirectorySchema
{
    /// <summary>Source-schema name: the exported settings' <c>"name"</c> and the gateway's client name.</summary>
    public const string Name = "DeviceDirectory";

    /// <summary>The schema definition, shared by <c>Program</c> and the in-process schema tests.</summary>
    public static IRequestExecutorBuilder AddDeviceDirectoryTypes(this IRequestExecutorBuilder builder) =>
        builder
            .AddAuthorization()
            .AddQueryType<Query>()
            .AddType<DeviceType>()
            // HC 16's default, pinned on purpose: each query resolver gets its own DI scope, hence its own pooled
            // DeviceDbContext. The gateway alias-batches lookups (d0: device(..) d1: device(..)); with one
            // request-scoped context those run in parallel and fail with "A second operation was started on
            // this context instance". Test: Aliased_lookups_and_search_in_one_request_succeed.
            .ModifyOptions(o => o.DefaultQueryDependencyInjectionScope = DependencyInjectionScope.Resolver);
}
