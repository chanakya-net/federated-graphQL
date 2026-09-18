using SoR.Patch.Data;

namespace SoR.Patch.Tests;

/// <summary>
/// Data-layer helpers without a database: inclusive <c>since</c>/<c>until</c> bounds survive MongoDB's millisecond
/// precision, and the client fails server selection fast.
/// </summary>
public sealed class PatchStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Whole_milliseconds_are_unchanged_and_UTC()
    {
        var plusTwo = new DateTimeOffset(2026, 3, 1, 14, 0, 0, TimeSpan.FromHours(2));   // same instant as Noon

        Assert.Equal(Noon.UtcDateTime, PatchStore.CeilingToMillisecond(plusTwo));
        Assert.Equal(Noon.UtcDateTime, PatchStore.FloorToMillisecond(plusTwo));
        Assert.Equal(DateTimeKind.Utc, PatchStore.CeilingToMillisecond(plusTwo).Kind);
        Assert.Equal(DateTimeKind.Utc, PatchStore.FloorToMillisecond(plusTwo).Kind);
    }

    [Fact]
    public void Sub_millisecond_since_rounds_up_and_until_rounds_down()
    {
        var value = Noon.AddTicks(4);   // 12:00:00.0000004

        Assert.Equal(Noon.UtcDateTime.AddMilliseconds(1), PatchStore.CeilingToMillisecond(value));
        Assert.Equal(Noon.UtcDateTime, PatchStore.FloorToMillisecond(value));
    }

    [Fact]
    public void Ceiling_at_the_end_of_time_does_not_overflow()
    {
        Assert.Equal(DateTime.MaxValue.Ticks, PatchStore.CeilingToMillisecond(DateTimeOffset.MaxValue).Ticks);
    }

    [Fact]
    public void Server_selection_fails_fast_unless_the_connection_string_says_otherwise()
    {
        var compose = new MongoOptions { ConnectionString = "mongodb://mongo:27017" }.ToClientSettings();
        Assert.Equal(TimeSpan.FromSeconds(3), compose.ServerSelectionTimeout);
        Assert.Equal("mongo", compose.Server.Host);

        var unset = new MongoOptions().ToClientSettings();   // schema export / bare dotnet run
        Assert.Equal("localhost", unset.Server.Host);
        Assert.Equal(MongoOptions.DefaultDatabase, new MongoOptions().Database);

        var explicitTimeout = new MongoOptions { ConnectionString = "mongodb://mongo:27017/?serverSelectionTimeoutMS=500" }.ToClientSettings();
        Assert.Equal(TimeSpan.FromMilliseconds(500), explicitTimeout.ServerSelectionTimeout);
    }
}
