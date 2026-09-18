using System.Text.Json;
using System.Text.Json.Serialization;
using SoR.SoftwareInstall.GraphQL;

namespace SoR.SoftwareInstall.Storage;

/// <summary>
/// One blob per device: <c>{tenantId}/{deviceId}/installEvents.json</c>. Events are stored ascending by
/// <see cref="StoredInstallEvent.OccurredAt"/>; the resolver returns them descending.
/// </summary>
public sealed record DeviceInstallDocument(
    int SchemaVersion,
    string TenantId,
    string DeviceId,
    IReadOnlyList<StoredInstallEvent> Events)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>An event as stored. The device id lives on the document, not on each event.</summary>
public sealed record StoredInstallEvent(
    string Id,
    DateTimeOffset OccurredAt,
    InstallAction Action,
    InstallResult Result,
    Software Software);

/// <summary>Written last by the seeder (<c>_seed/complete.json</c>). <c>CompletedAt</c> is bookkeeping, not seed data.</summary>
public sealed record SeedMarker(DateTimeOffset CompletedAt, int DeviceCount);

/// <summary>The one JSON shape for every blob: camelCase, enums as their GraphQL names, ISO 8601 offsets.</summary>
public static class InstallJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // "Install" -> "INSTALL", "Success" -> "SUCCESS": the stored value equals the GraphQL enum value.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false) },
        // A blob missing a field or carrying a null where the model says non-null is corrupt: fail the read
        // loudly (field error) instead of handing HC a null for a non-null GraphQL field.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static BinaryData Serialize<T>(T value) => BinaryData.FromObjectAsJson(value, Options);

    public static T Deserialize<T>(BinaryData content) =>
        content.ToObjectFromJson<T>(Options) ?? throw new JsonException($"blob content is JSON null, expected {typeof(T).Name}");
}
