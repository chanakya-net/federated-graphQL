namespace SoR.DeviceDirectory.Tests.Support;

/// <summary>Files of the repository the tests read: contract SDL, committed export, the dev <c>.env</c>.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Value of <paramref name="name"/> in the committed <c>.env</c> (dev-only values, plan §4.4).</summary>
    public static string Env(string name)
    {
        foreach (var raw in File.ReadLines(PathOf(".env")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim() == name) return line[(eq + 1)..].Trim();
        }

        throw new InvalidOperationException($"{name} is not defined in {PathOf(".env")}");
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
