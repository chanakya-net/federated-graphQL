using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoR.Shared.Auth;

namespace SoR.TokenGenerator;

/// <summary>
/// The whole generator, free of process globals so tests can run it in-process:
/// arguments, environment and both output streams are passed in.
/// </summary>
public static class TokenGeneratorApp
{
    public const string UsersFileEnv = "USERS_FILE";
    public const string TokensOutputEnv = "TOKENS_OUTPUT";
    public const string DefaultAdHocSub = "adhoc";

    /// <summary>
    /// <c>iat</c>/<c>nbf</c> are set this far in the past. Validation allows only 1 minute of clock skew, so a token
    /// minted on the host would otherwise be "not yet valid" in a container whose clock lags (e.g. a VM after sleep).
    /// </summary>
    public static readonly TimeSpan Backdate = TimeSpan.FromMinutes(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        // users.json is edited by hand: tolerate comments and trailing commas, but reject typos and missing fields.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Run(string[] args, IDictionary<string, string?> env, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var cli = CommandLine.Parse(args);
            if (cli.Mode == Mode.Help)
            {
                stdout.Write(CommandLine.UsageText);
                return ExitCodes.Success;
            }

            var key = RequireSigningKey(env);
            // Expiry is relative to real time, not SeedConstants.Epoch, so the demo works whenever it is run.
            var now = DateTimeOffset.UtcNow;
            string Mint(UserSpec u) =>
                DevTokenFactory.Create(key, u.Sub, u.Name, u.TenantId, u.Services, expires: now.AddYears(10), issuedAt: now - Backdate);

            return cli.Mode switch
            {
                Mode.Compose => RunCompose(cli, env, Mint, stderr),
                Mode.User => RunUser(cli, env, Mint, stdout, stderr),
                _ => RunAdHoc(cli, Mint, stdout, stderr),
            };
        }
        catch (CliException e)
        {
            stderr.WriteLine($"TokenGenerator: {e.Message}");
            if (e.ShowUsage)
                stderr.Write(CommandLine.UsageText);
            return e.ExitCode;
        }
    }

    private static int RunCompose(CommandLine cli, IDictionary<string, string?> env, Func<UserSpec, string> mint, TextWriter stderr)
    {
        var usersPath = UsersPath(cli, env);
        var outputPath = cli.OutputPath ?? NonBlank(env, TokensOutputEnv) ?? "tokens.json";

        var entries = new List<TokenEntry>();
        foreach (var user in LoadUsers(usersPath))
        {
            entries.Add(new TokenEntry(user.Sub, user.Name, user.TenantId, user.Services, mint(user)));
            stderr.WriteLine($"minted {Describe(user)}");
        }

        var written = WriteAtomically(outputPath, JsonSerializer.Serialize(entries, WriteOptions) + "\n");
        stderr.WriteLine($"wrote {entries.Count} tokens to {written}");
        return ExitCodes.Success;
    }

    private static int RunUser(CommandLine cli, IDictionary<string, string?> env, Func<UserSpec, string> mint,
        TextWriter stdout, TextWriter stderr)
    {
        var usersPath = UsersPath(cli, env);
        var users = LoadUsers(usersPath);
        var user = users.FirstOrDefault(u => string.Equals(u.Sub, cli.User, StringComparison.Ordinal))
            ?? throw new CliException(ExitCodes.Usage,
                $"no user '{cli.User}' in {usersPath} (known: {string.Join(", ", users.Select(u => u.Sub))})");

        return PrintToken(user, mint, stdout, stderr);
    }

    private static int RunAdHoc(CommandLine cli, Func<UserSpec, string> mint, TextWriter stdout, TextWriter stderr)
    {
        var services = (cli.Services ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unknown = services.Where(s => !IsKnownService(s)).ToArray();
        if (unknown.Length > 0)
            throw new CliException(ExitCodes.Usage,
                $"unknown service(s) {string.Join(", ", unknown)} (allowed: {string.Join(", ", DevAuth.Services.All)})",
                showUsage: true);

        var sub = cli.Sub ?? DefaultAdHocSub;
        return PrintToken(new UserSpec(sub, sub, cli.Tenant!, services), mint, stdout, stderr);
    }

    /// <summary>Stdout gets the token only, so <c>TOKEN=$(... --user bob)</c> captures exactly the JWT.</summary>
    private static int PrintToken(UserSpec user, Func<UserSpec, string> mint, TextWriter stdout, TextWriter stderr)
    {
        stdout.Write(mint(user));
        stdout.Flush();
        // The leading newline ends the token's line when stdout and stderr share a terminal.
        stderr.WriteLine();
        stderr.WriteLine($"minted {Describe(user)}");
        return ExitCodes.Success;
    }

    private static string RequireSigningKey(IDictionary<string, string?> env)
    {
        var key = env.TryGetValue(DevAuth.SigningKeyEnv, out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < DevTokenFactory.MinKeyBytes)
            throw new CliException(ExitCodes.SigningKey,
                $"{DevAuth.SigningKeyEnv} missing or shorter than {DevTokenFactory.MinKeyBytes} bytes");
        return key;
    }

    private static string UsersPath(CommandLine cli, IDictionary<string, string?> env) =>
        cli.UsersPath ?? NonBlank(env, UsersFileEnv) ?? Path.Combine(AppContext.BaseDirectory, "users.json");

    private static string? NonBlank(IDictionary<string, string?> env, string name) =>
        env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <exception cref="CliException">exit code 4 if the file cannot be read or is not a valid user list.</exception>
    public static IReadOnlyList<UserSpec> LoadUsers(string path)
    {
        List<UserSpec>? users;
        try
        {
            users = JsonSerializer.Deserialize<List<UserSpec>>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw UsersFileError(path, e.Message);
        }

        if (users is null || users.Count == 0)
            throw UsersFileError(path, "no users");

        var subs = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UserSpec>(users.Count);
        for (var index = 0; index < users.Count; index++)
        {
            var user = users[index];
            var where = $"user #{index + 1}";
            if (user is null)
                throw UsersFileError(path, $"{where} is null");
            if (string.IsNullOrWhiteSpace(user.Sub) || string.IsNullOrWhiteSpace(user.Name) || string.IsNullOrWhiteSpace(user.TenantId))
                throw UsersFileError(path, $"{where}: sub, name and tenantId must not be empty");
            if (!subs.Add(user.Sub))
                throw UsersFileError(path, $"{where}: duplicate sub '{user.Sub}'");
            var unknown = user.Services.Where(s => !IsKnownService(s)).Select(s => s ?? "null").ToArray();
            if (unknown.Length > 0)
                throw UsersFileError(path,
                    $"{where} ('{user.Sub}'): unknown service(s) {string.Join(", ", unknown)} (allowed: {string.Join(", ", DevAuth.Services.All)})");

            // The token de-duplicates services (DevTokenFactory); keep tokens.json consistent with it.
            result.Add(user with { Services = user.Services.Distinct(StringComparer.Ordinal).ToArray() });
        }

        return result;
    }

    private static bool IsKnownService(string? service) =>
        service is not null && DevAuth.Services.All.Contains(service, StringComparer.Ordinal);

    private static CliException UsersFileError(string path, string detail) =>
        new(ExitCodes.UsersFile, $"users file {path} is unreadable or invalid: {detail}");

    /// <summary>
    /// Writes <c>&lt;path&gt;.tmp</c> next to the target, then renames it over the target, so a reader (the UI's Nginx)
    /// sees either the old file or the new one, never a partial one. Creates the directory if missing.
    /// </summary>
    /// <returns>The absolute output path.</returns>
    private static string WriteAtomically(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var tmpPath = fullPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(tmpPath, content, Utf8NoBom);
            File.Move(tmpPath, fullPath, overwrite: true);
            return fullPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tmpPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original error is the one worth reporting.
            }

            throw new CliException(ExitCodes.OutputNotWritable, $"cannot write {fullPath}: {e.Message}");
        }
    }

    private static string Describe(UserSpec user) =>
        $"{user.Sub} tenant={user.TenantId} services={string.Join(',', user.Services)}";
}
