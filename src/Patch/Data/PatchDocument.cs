using MongoDB.Bson.Serialization.Attributes;

namespace SoR.Patch.Data;

/// <summary>One catalog entry in <c>patches</c>. Severity is stored as its contract name (CRITICAL|HIGH|MEDIUM|LOW).</summary>
public sealed class PatchDocument
{
    public const string Collection = "patches";

    [BsonId] public required string Id { get; set; }                 // "patch-0007"

    [BsonElement("kbId")] public required string KbId { get; set; }  // "KB5000007"

    [BsonElement("title")] public required string Title { get; set; }

    [BsonElement("severity")] public required string Severity { get; set; }

    [BsonElement("vendor")] public required string Vendor { get; set; }

    [BsonElement("releasedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ReleasedAt { get; set; }
}
