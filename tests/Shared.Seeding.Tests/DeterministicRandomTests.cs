namespace SoR.Shared.Seeding.Tests;

public sealed class DeterministicRandomTests
{
    [Fact]
    public void DeterministicRandom_same_seed_same_sequence()
    {
        Assert.Equal(DeterministicRandom.For(7, "patch").Next(), DeterministicRandom.For(7, "patch").Next());
        Assert.NotEqual(DeterministicRandom.For(7, "patch").Next(), DeterministicRandom.For(7, "vulnerability").Next());

        var a = DeterministicRandom.For(7, "patch");
        var b = DeterministicRandom.For(7, "patch");
        Assert.Equal(Enumerable.Range(0, 100).Select(_ => a.Next()), Enumerable.Range(0, 100).Select(_ => b.Next()));
    }

    [Fact]
    public void StableHash_is_a_fixed_function()
    {
        // Pinned values: if these move, every domain's seeded data moves with them.
        Assert.Equal(23, DeterministicRandom.StableHash(""));
        Assert.Equal(23 * 31 + 'a', DeterministicRandom.StableHash("a"));
        Assert.Equal(DeterministicRandom.StableHash("patch"), DeterministicRandom.StableHash("patch"));
        Assert.NotEqual(DeterministicRandom.StableHash("patch"), DeterministicRandom.StableHash("vulnerability"));
    }

    [Fact]
    public void InstantInWindow_is_inside_window()
    {
        var rng = DeterministicRandom.For(0, "window-test");
        var earliest = SeedConstants.Epoch - SeedConstants.EventWindow;
        for (var i = 0; i < 10_000; i++)
        {
            var t = DeterministicRandom.InstantInWindow(rng);
            Assert.InRange(t, earliest, SeedConstants.Epoch);
        }
    }
}
