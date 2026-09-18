using System.Diagnostics;
using System.Text.Json;
using SoR.DeviceDirectory.Tests.Support;
using SoR.Shared.Auth;

namespace SoR.DeviceDirectory.Tests;

/// <summary>
/// Runs the built app as a separate process: <c>DeviceDirectory.dll schema export --output &lt;tmp&gt;</c>, with no
/// connection string and no signing key in its environment (contracts/http-and-env.md).
/// </summary>
public sealed class ExportFixture : IAsyncLifetime
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"devdir-export-{Guid.NewGuid():N}");

    public string SchemaFile => Path.Combine(Directory, "device-directory.graphqls");

    public string SettingsFile => Path.Combine(Directory, "device-directory-settings.json");

    public int ExitCode { get; private set; }

    public string Output { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "DeviceDirectory.dll"), "schema", "export", "--output", SchemaFile })
        {
            psi.ArgumentList.Add(arg);
        }

        // Nothing to connect to and no key: both must be absent, not just empty.
        foreach (var name in psi.Environment.Keys.ToList())
        {
            if (name.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) || name == DevAuth.SigningKeyEnv)
            {
                psi.Environment.Remove(name);
            }
        }

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Production";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(cts.Token);
        ExitCode = process.ExitCode;
        Output = await stdout + await stderr;
    }

    public Task DisposeAsync()
    {
        System.IO.Directory.Delete(Directory, recursive: true);
        return Task.CompletedTask;
    }
}

public sealed class ExportTests(ExportFixture export) : IClassFixture<ExportFixture>
{
    [Fact]
    public void Export_runs_without_database()
    {
        Assert.True(export.ExitCode == 0, $"exit {export.ExitCode}: {export.Output}");
        var sdl = File.ReadAllText(export.SchemaFile);
        Assert.Contains("type Device ", sdl, StringComparison.Ordinal);
        Assert.Contains("device(id: ID!): Device @lookup", sdl, StringComparison.Ordinal);
        Assert.Contains("devices(search: String, first: Int! = 25, offset: Int! = 0): DeviceSearchResult!", sdl, StringComparison.Ordinal);
    }

    [Fact]
    public void Exported_settings_name_the_source_schema()
    {
        Assert.True(export.ExitCode == 0, export.Output);
        using var settings = JsonDocument.Parse(File.ReadAllText(export.SettingsFile));
        Assert.Equal("DeviceDirectory", settings.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Committed_schema_is_the_current_export()
    {
        // schemas/device-directory.graphqls feeds composition; a stale file would compose an old schema.
        Assert.True(export.ExitCode == 0, export.Output);
        var committed = File.ReadAllText(Repo.PathOf("schemas", "device-directory.graphqls"));
        Assert.Equal(File.ReadAllText(export.SchemaFile), committed);
    }

    [Fact]
    public void Committed_settings_use_the_compose_url()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Repo.PathOf("schemas", "device-directory-settings.json")));
        var http = settings.RootElement.GetProperty("transports").GetProperty("http");
        Assert.Equal("DeviceDirectory", settings.RootElement.GetProperty("name").GetString());
        Assert.Equal("http://device-directory:8080/graphql", http.GetProperty("url").GetString());
        Assert.Equal("propagate", http.GetProperty("capabilities").GetProperty("onError").GetString());
    }
}
