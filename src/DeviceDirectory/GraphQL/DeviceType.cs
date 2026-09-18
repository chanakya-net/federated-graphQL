using SoR.DeviceDirectory.Data;

namespace SoR.DeviceDirectory.GraphQL;

/// <summary>Maps <see cref="DeviceEntity"/> to the contract's <c>Device</c>: exactly six fields, nothing else.</summary>
public sealed class DeviceType : ObjectType<DeviceEntity>
{
    protected override void Configure(IObjectTypeDescriptor<DeviceEntity> descriptor)
    {
        descriptor.Name("Device");
        descriptor.Description("A device in the caller's tenant. Owned by Device Directory; domain subgraphs extend it by `id`.");
        descriptor.BindFieldsExplicitly();

        // Plain ID scalar: the value is not re-encoded (no global object identification), "dev-00001" stays as-is.
        descriptor.Field(d => d.Id).Name("id").Type<NonNullType<IdType>>();
        descriptor.Field(d => d.Hostname).Name("hostname").Type<NonNullType<StringType>>();
        descriptor.Field(d => d.Os).Name("os").Type<NonNullType<StringType>>();
        descriptor.Field(d => d.IpAddress).Name("ipAddress").Type<NonNullType<StringType>>();
        descriptor.Field(d => d.LastSeenAt).Name("lastSeenAt").Type<NonNullType<DateTimeType>>();
        descriptor.Field(d => d.TenantId).Name("tenantId").Type<NonNullType<StringType>>();
    }
}
