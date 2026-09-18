using System.Reflection;

namespace SoR.TokenGenerator.Tests;

// Phase 1 placeholder. Lane P2A replaces it.
public sealed class SkeletonTests
{
    [Fact]
    public void Assembly_name_matches_project_folder()
    {
        // The P2C Dockerfile template runs "<ProjectFolder>.dll", so the assembly name is part of the contract.
        var assembly = Assembly.Load("TokenGenerator");
        Assert.NotNull(assembly.EntryPoint);
    }
}
