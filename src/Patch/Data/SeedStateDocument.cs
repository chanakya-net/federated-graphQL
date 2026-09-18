using MongoDB.Bson.Serialization.Attributes;

namespace SoR.Patch.Data;

/// <summary>Completion marker in <c>seed_state</c>, written last (contracts/seeding.md).</summary>
public sealed class SeedStateDocument
{
    public const string Collection = "seed_state";
    public const string PatchKey = "patch";

    [BsonId] public required string Key { get; set; }

    /// <summary>Bookkeeping only (when the seed finished); never part of the seeded data.</summary>
    [BsonElement("completedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CompletedAt { get; set; }

    /// <summary>Number of patch events written.</summary>
    [BsonElement("count")] public long Count { get; set; }
}
