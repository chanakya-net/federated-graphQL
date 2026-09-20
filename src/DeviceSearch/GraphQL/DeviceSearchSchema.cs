using HotChocolate.Execution.Configuration;

namespace SoR.DeviceSearch.GraphQL;

public static class DeviceSearchSchema
{
    public const string Name = "DeviceSearch";
    public static IRequestExecutorBuilder AddDeviceSearchTypes(this IRequestExecutorBuilder builder) =>
        builder.AddAuthorization().AddQueryType<Query>();
}
