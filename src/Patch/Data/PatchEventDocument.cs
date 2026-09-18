using MongoDB.Bson.Serialization.Attributes;

namespace SoR.Patch.Data;

/// <summary>
/// One patch event in <c>patch_events</c>. Element names are camelCase so the only query path is the index
/// <c>tenantId_1_deviceId_1_occurredAt_-1</c>. Status is stored as its contract name (APPLIED|FAILED|PENDING).
/// </summary>
public sealed class PatchEventDocument
{
    public const string Collection = "patch_events";

    [BsonId] public required string Id { get; set; }                         // "dev-00042-p003"

    [BsonElement("tenantId")] public required string TenantId { get; set; }

    [BsonElement("deviceId")] public required string DeviceId { get; set; }

    [BsonElement("patchId")] public required string PatchId { get; set; }

    [BsonElement("status")] public required string Status { get; set; }

    [BsonElement("occurredAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; set; }
}
