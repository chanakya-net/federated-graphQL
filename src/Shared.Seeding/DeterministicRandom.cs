namespace SoR.Shared.Seeding;

public static class DeterministicRandom
{
    /// <summary>Stable, cross-platform hash. Do NOT use string.GetHashCode (randomised per process).</summary>
    public static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    public static int Seed(int deviceIndex, string domain) => unchecked((deviceIndex * 397) ^ StableHash(domain));

    /// <summary>Seeded System.Random is stable across .NET versions for the same seed.</summary>
    public static Random For(int deviceIndex, string domain) => new(Seed(deviceIndex, domain));

    /// <summary>Uniform instant inside the event window, from a caller-owned Random.</summary>
    public static DateTimeOffset InstantInWindow(Random rng)
    {
        var minutes = (int)SeedConstants.EventWindow.TotalMinutes;
        return SeedConstants.Epoch - TimeSpan.FromMinutes(rng.Next(0, minutes + 1));
    }
}
