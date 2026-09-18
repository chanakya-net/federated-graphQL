using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SoR.Shared.Auth;

namespace SoR.TokenGenerator.Tests;

public sealed partial class TokenGeneratorAppTests : IDisposable
{
    // Same value as DEV_JWT_SIGNING_KEY in the committed .env (64 ASCII chars).
    private const string Key = "sor-poc-dev-only-signing-key-do-not-use-in-prod-0123456789abcdef";

    private static readonly string[] EntryProperties = ["sub", "name", "tenantId", "services", "token"];

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sor-tokengen-tests-");

    private string OutputPath => Path.Combine(_dir.FullName, "tokens.json");

    public void Dispose() => _dir.Delete(recursive: true);

    // ---- phase-2a §7 ----------------------------------------------------------------------------------------

    [Fact]
    public void Compose_mode_writes_five_entries()
    {
        var (exit, stdout, stderr) = Run(Env());

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(stdout);
        Assert.Contains("minted alice tenant=TenantA services=patch,vulnerability,softwareinstall", stderr);
        Assert.Contains(OutputPath, stderr);

        using var doc = JsonDocument.Parse(File.ReadAllText(OutputPath));
        var entries = doc.RootElement.EnumerateArray().ToArray();
        Assert.Equal(["alice", "bob", "carol", "dave", "erin"], entries.Select(e => e.GetProperty("sub").GetString()));
        var users = TokenGeneratorApp.LoadUsers(DefaultUsersFile);
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(EntryProperties, entries[i].EnumerateObject().Select(p => p.Name));
            Assert.Equal(users[i].Name, entries[i].GetProperty("name").GetString());
            Assert.Equal(users[i].TenantId, entries[i].GetProperty("tenantId").GetString());
            Assert.Equal(users[i].Services, entries[i].GetProperty("services").EnumerateArray().Select(s => s.GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entries[i].GetProperty("token").GetString()));
        }
    }

    [Fact]
    public async Task Tokens_validate_with_shared_parameters()
    {
        var (handler, parameters) = SharedValidation(Key);

        foreach (var entry in ComposeEntries())
        {
            var result = await handler.ValidateTokenAsync(entry.Token, parameters);

            Assert.True(result.IsValid, $"{entry.Sub}: {result.Exception?.Message}");
            var identity = result.ClaimsIdentity;
            Assert.Equal(entry.Sub, identity.FindFirst(DevAuth.SubjectClaim)?.Value);
            Assert.Equal(entry.Name, identity.FindFirst(DevAuth.NameClaim)?.Value);
            Assert.Equal(entry.TenantId, identity.FindFirst(DevAuth.TenantClaim)?.Value);
            Assert.Equal(entry.Services, identity.FindAll(DevAuth.ServicesClaim).Select(c => c.Value));
        }
    }

    [Fact]
    public void Services_claim_is_array()
    {
        foreach (var entry in ComposeEntries())
        {
            using var payload = Payload(entry.Token);
            var services = payload.RootElement.GetProperty(DevAuth.ServicesClaim);

            Assert.Equal(JsonValueKind.Array, services.ValueKind);   // also for erin, who has a single service
            Assert.Equal(entry.Services, services.EnumerateArray().Select(s => s.GetString()));
        }
    }

    [Fact]
    public void Expiry_is_at_least_five_years_out()
    {
        var fiveYears = DateTime.UtcNow.AddYears(5);

        foreach (var entry in ComposeEntries())
        {
            var token = new JsonWebToken(entry.Token);
            Assert.True(token.ValidTo >= fiveYears, $"{entry.Sub}: exp {token.ValidTo:O}");
            Assert.True(token.ValidFrom <= DateTime.UtcNow, $"{entry.Sub}: nbf {token.ValidFrom:O}");
        }
    }

    [Fact]
    public void User_mode_prints_only_token()
    {
        var (exit, stdout, _) = Run(Env(), "--user", "bob");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Matches(JwtShape(), stdout);   // three base64url segments, no whitespace or newline anywhere
        using var payload = Payload(stdout);
        Assert.Equal("bob", payload.RootElement.GetProperty(DevAuth.SubjectClaim).GetString());
        Assert.False(File.Exists(OutputPath));
    }

    [Fact]
    public void Unknown_user_exits_2()
    {
        var (exit, stdout, stderr) = Run(Env(), "--user", "zed");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Empty(stdout);
        Assert.Contains("'zed'", stderr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcde")]   // 31 bytes
    public void Missing_key_exits_3(string? key)
    {
        var (exit, stdout, stderr) = Run(Env(key));
        Assert.Equal(ExitCodes.SigningKey, exit);
        Assert.Empty(stdout);
        Assert.Contains(DevAuth.SigningKeyEnv, stderr);
        Assert.False(File.Exists(OutputPath));

        (exit, stdout, _) = Run(Env(key), "--user", "bob");
        Assert.Equal(ExitCodes.SigningKey, exit);
        Assert.Empty(stdout);
    }

    [Fact]
    public void Unknown_service_exits_4()
    {
        var users = WriteUsers("""[ { "sub": "x", "name": "X", "tenantId": "TenantA", "services": ["nope"] } ]""");

        var (exit, stdout, stderr) = Run(Env(), "--users", users);

        Assert.Equal(ExitCodes.UsersFile, exit);
        Assert.Empty(stdout);
        Assert.Contains("nope", stderr);
        Assert.False(File.Exists(OutputPath));
    }

    [Fact]
    public void Output_is_atomic()
    {
        var (exit, _, _) = Run(Env());

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(File.Exists(OutputPath + ".tmp"));
        Assert.Equal(["tokens.json"], _dir.GetFileSystemInfos().Select(f => f.Name));
    }

    // ---- additional coverage -----------------------------------------------------------------------------------

    [Fact]
    public void Committed_users_json_has_exactly_the_five_plan_users()
    {
        // plan §4.4 / contracts/tokens.json.md
        string[] expected =
        [
            "alice|Alice (Tenant A)|TenantA|patch,vulnerability,softwareinstall",
            "bob|Bob (Tenant A)|TenantA|patch,vulnerability",
            "carol|Carol (Tenant A)|TenantA|softwareinstall",
            "dave|Dave (Tenant B)|TenantB|patch,vulnerability,softwareinstall",
            "erin|Erin (Tenant B)|TenantB|patch",
        ];

        var users = TokenGeneratorApp.LoadUsers(DefaultUsersFile);

        Assert.Equal(expected, users.Select(u => $"{u.Sub}|{u.Name}|{u.TenantId}|{string.Join(',', u.Services)}"));
    }

    [Fact]
    public void Compose_mode_creates_directory_and_replaces_existing_file()
    {
        var output = Path.Combine(_dir.FullName, "nested", "dir", "tokens.json");
        var env = Env();
        env[TokenGeneratorApp.TokensOutputEnv] = output;

        Assert.Equal(ExitCodes.Success, Run(env).Exit);
        File.WriteAllText(output, "stale");
        Assert.Equal(ExitCodes.Success, Run(env).Exit);

        Assert.Equal(5, ReadEntries(output).Count);
    }

    [Fact]
    public void Unwritable_output_exits_1_and_leaves_no_temp_file()
    {
        var env = Env();
        env[TokenGeneratorApp.TokensOutputEnv] = _dir.FullName;   // a directory cannot be replaced by a file

        var (exit, _, stderr) = Run(env);

        Assert.Equal(ExitCodes.OutputNotWritable, exit);
        Assert.Contains("cannot write", stderr);
        Assert.False(File.Exists(_dir.FullName + ".tmp"));
    }

    [Theory]
    [InlineData("TenantB", "patch,softwareinstall", null, "adhoc", new[] { "patch", "softwareinstall" })]
    [InlineData("TenantA", " vulnerability , ", "mallory", "mallory", new[] { "vulnerability" })]
    [InlineData("TenantA", null, "nobody", "nobody", new string[0])]
    public async Task AdHoc_mode_prints_token_with_requested_claims(
        string tenant, string? services, string? sub, string expectedSub, string[] expectedServices)
    {
        List<string> args = ["--tenant", tenant];
        if (services is not null) args.AddRange(["--services", services]);
        if (sub is not null) args.AddRange(["--sub", sub]);

        var (exit, stdout, _) = Run(Env(), [.. args]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Matches(JwtShape(), stdout);
        var (handler, parameters) = SharedValidation(Key);
        var result = await handler.ValidateTokenAsync(stdout, parameters);
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(expectedSub, result.ClaimsIdentity.FindFirst(DevAuth.SubjectClaim)?.Value);
        Assert.Equal(tenant, result.ClaimsIdentity.FindFirst(DevAuth.TenantClaim)?.Value);
        Assert.Equal(expectedServices, result.ClaimsIdentity.FindAll(DevAuth.ServicesClaim).Select(c => c.Value));
        using var payload = Payload(stdout);
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty(DevAuth.ServicesClaim).ValueKind);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("stray")]
    [InlineData("--user")]
    [InlineData("--user --tenant TenantA")]
    [InlineData("--user bob --user alice")]
    [InlineData("--user bob --tenant TenantA")]
    [InlineData("--user bob --output x.json")]
    [InlineData("--tenant TenantA --users x.json")]
    [InlineData("--tenant TenantA --services patch,nope")]
    [InlineData("--services patch")]
    [InlineData("--sub someone")]
    public void Usage_errors_exit_2(string commandLine)
    {
        var (exit, stdout, stderr) = Run(Env(), commandLine.Split(' '));

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Empty(stdout);
        Assert.Contains("usage:", stderr);
        Assert.False(File.Exists(OutputPath));
    }

    [Theory]
    [InlineData(null)]   // file does not exist
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""[ { "sub": "x", "name": "X", "services": ["patch"] } ]""")]                                  // tenantId missing
    [InlineData("""[ { "sub": "x", "name": "X", "tenantId": "TenantA", "services": null } ]""")]
    [InlineData("""[ { "sub": "x", "name": "X", "tenantId": "TenantA", "service": ["patch"] } ]""")]             // typo
    [InlineData("""[ { "sub": " ", "name": "X", "tenantId": "TenantA", "services": [] } ]""")]
    [InlineData("""[ { "sub": "x", "name": "X", "tenantId": "TenantA", "services": ["Patch"] } ]""")]            // case matters
    [InlineData("""[ { "sub": "x", "name": "X", "tenantId": "A", "services": [] }, { "sub": "x", "name": "Y", "tenantId": "B", "services": [] } ]""")]
    public void Invalid_users_file_exits_4(string? content)
    {
        var users = content is null ? Path.Combine(_dir.FullName, "missing.json") : WriteUsers(content);

        var (exit, stdout, stderr) = Run(Env(), "--users", users);

        Assert.Equal(ExitCodes.UsersFile, exit);
        Assert.Empty(stdout);
        Assert.Contains(users, stderr);
        Assert.False(File.Exists(OutputPath));
    }

    [Fact]
    public void Users_file_tolerates_comments_and_trailing_commas()
    {
        var users = WriteUsers("""
            [
              // hand-edited
              { "sub": "x", "name": "X", "tenantId": "TenantA", "services": ["patch", "patch"], },
            ]
            """);

        Assert.Equal(ExitCodes.Success, Run(Env(), "--users", users).Exit);

        var entry = Assert.Single(ReadEntries(OutputPath));
        Assert.Equal(["patch"], entry.Services);   // de-duplicated, like the token's claim
    }

    [Fact]
    public void Environment_paths_beat_defaults_and_arguments_beat_environment()
    {
        var fromEnv = WriteUsers("""[ { "sub": "env", "name": "Env", "tenantId": "TenantA", "services": [] } ]""");
        var fromArgs = WriteUsers("""[ { "sub": "arg", "name": "Arg", "tenantId": "TenantB", "services": [] } ]""");
        var argOutput = Path.Combine(_dir.FullName, "from-args.json");
        var env = Env();
        env[TokenGeneratorApp.UsersFileEnv] = fromEnv;

        Assert.Equal(ExitCodes.Success, Run(env).Exit);
        Assert.Equal("env", Assert.Single(ReadEntries(OutputPath)).Sub);

        Assert.Equal(ExitCodes.Success, Run(env, "--users", fromArgs, "--output", argOutput).Exit);
        Assert.Equal("arg", Assert.Single(ReadEntries(argOutput)).Sub);

        var (exit, stdout, _) = Run(env, "--user", "env");
        Assert.Equal(ExitCodes.Success, exit);
        using var payload = Payload(stdout);
        Assert.Equal("env", payload.RootElement.GetProperty(DevAuth.SubjectClaim).GetString());
    }

    [Fact]
    public void Help_exits_0_without_a_key()
    {
        var (exit, stdout, _) = Run(Env(key: null), "--help");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("usage:", stdout);
    }

    [Fact]
    public void Assembly_name_matches_project_folder()
    {
        // The P2C Dockerfile template runs "<ProjectFolder>.dll", so the assembly name is part of the contract.
        var assembly = Assembly.Load("TokenGenerator");
        Assert.NotNull(assembly.EntryPoint);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>users.json is Content in TokenGenerator.csproj, so it is copied next to the test assembly too.</summary>
    private static string DefaultUsersFile => Path.Combine(AppContext.BaseDirectory, "users.json");

    // \z, not $: in .NET "$" also matches before a trailing newline.
    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\z")]
    private static partial Regex JwtShape();

    private Dictionary<string, string?> Env(string? key = Key) => new(StringComparer.Ordinal)
    {
        [DevAuth.SigningKeyEnv] = key,
        [TokenGeneratorApp.TokensOutputEnv] = OutputPath,
    };

    private static (int Exit, string Stdout, string Stderr) Run(IDictionary<string, string?> env, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = TokenGeneratorApp.Run(args, env, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private List<TokenEntry> ComposeEntries()
    {
        var (exit, _, stderr) = Run(Env());
        Assert.True(exit == ExitCodes.Success, stderr);
        return ReadEntries(OutputPath);
    }

    private static List<TokenEntry> ReadEntries(string path) =>
        JsonSerializer.Deserialize<List<TokenEntry>>(File.ReadAllText(path), JsonSerializerOptions.Web)!;

    private string WriteUsers(string json)
    {
        var path = Path.Combine(_dir.FullName, $"users-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static JsonDocument Payload(string token) => JsonDocument.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]));

    /// <summary>The token handler and parameters a subgraph actually uses (Shared.Auth's AddDevJwtAuthentication).</summary>
    private static (TokenHandler Handler, TokenValidationParameters Parameters) SharedValidation(string key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DevAuth.SigningKeyEnv] = key })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDevJwtAuthentication(config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        return (options.TokenHandlers.Single(), options.TokenValidationParameters);
    }
}
