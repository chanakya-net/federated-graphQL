namespace SoR.Gateway.Tests.Support;

/// <summary>Files of the repository the tests read: the committed archive, the dev <c>.env</c>, the scripts.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public static string Archive => PathOf("gateway", "gateway.far");

    /// <summary>Value of <paramref name="name"/> from the environment, else the committed <c>.env</c> (compose's precedence).</summary>
    public static string? Env(string name)
    {
        var fromShell = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromShell)) return fromShell;

        foreach (var raw in File.ReadLines(PathOf(".env")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim() == name) return line[(eq + 1)..].Trim();
        }

        return null;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SoR.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException($"SoR.sln not found above {AppContext.BaseDirectory}");
    }
}
