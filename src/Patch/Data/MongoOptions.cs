using MongoDB.Driver;

namespace SoR.Patch.Data;

/// <summary>Bound from the <c>Mongo</c> section (<c>Mongo__ConnectionString</c>, <c>Mongo__Database</c> in compose).</summary>
public sealed class MongoOptions
{
    public const string Section = "Mongo";

    /// <summary>
    /// Used only when nothing is configured (schema export, a bare <c>dotnet run</c>). Constructing a
    /// <c>MongoClient</c> never connects, so startup and export do not need a running server.
    /// </summary>
    public const string FallbackConnectionString = "mongodb://localhost:27017";

    public const string DefaultDatabase = "patch";

    /// <summary>
    /// The driver's default (30 s) equals Hot Chocolate's execution timeout, so with MongoDB down a query failed as a
    /// whole (HTTP 500) instead of at <c>patchEvents</c>. 3 s fails the field first, inside the gateway's 5 s
    /// subgraph timeout. A <c>serverSelectionTimeoutMS</c> in the connection string wins.
    /// </summary>
    public static readonly TimeSpan DefaultServerSelectionTimeout = TimeSpan.FromSeconds(3);

    public string? ConnectionString { get; set; }

    public string Database { get; set; } = DefaultDatabase;

    public MongoClientSettings ToClientSettings()
    {
        var connectionString = ConnectionString ?? FallbackConnectionString;
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        if (!connectionString.Contains("serverSelectionTimeoutMS=", StringComparison.OrdinalIgnoreCase))
        {
            settings.ServerSelectionTimeout = DefaultServerSelectionTimeout;
        }

        return settings;
    }
}
