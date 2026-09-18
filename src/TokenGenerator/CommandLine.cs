namespace SoR.TokenGenerator;

/// <summary>Process exit codes (phase-2a §4, plus <see cref="OutputNotWritable"/>).</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Compose mode could not write <c>TOKENS_OUTPUT</c> (not covered by the phase doc).</summary>
    public const int OutputNotWritable = 1;

    /// <summary>Bad arguments, or <c>--user</c> names a user that is not in the users file.</summary>
    public const int Usage = 2;

    public const int SigningKey = 3;
    public const int UsersFile = 4;
}

public enum Mode
{
    /// <summary>No mode flag: write one token per user to the output file.</summary>
    Compose,

    /// <summary><c>--user &lt;sub&gt;</c>: print that user's token.</summary>
    User,

    /// <summary><c>--tenant &lt;id&gt;</c>: print a token for an arbitrary tenant / service set.</summary>
    AdHoc,

    Help,
}

/// <summary>A failure that ends the run with <see cref="ExitCode"/>; <see cref="TokenGeneratorApp.Run"/> reports it on stderr.</summary>
public sealed class CliException(int exitCode, string message, bool showUsage = false) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    public bool ShowUsage { get; } = showUsage;
}

/// <summary>Parsed arguments. Plain parsing, no CLI library (phase-2a §5).</summary>
public sealed record CommandLine(
    Mode Mode,
    string? UsersPath = null,
    string? OutputPath = null,
    string? User = null,
    string? Tenant = null,
    string? Services = null,
    string? Sub = null)
{
    public const string UsageText =
        """
        usage:
          TokenGenerator                                    write one token per user to TOKENS_OUTPUT (compose mode)
          TokenGenerator --user <sub>                       print that user's token
          TokenGenerator --tenant <id> [--services a,b] [--sub <sub>]
                                                            print an ad-hoc token (sub defaults to "adhoc")
        options:
          --users <path>    users file; overrides USERS_FILE (compose and --user modes)
          --output <path>   output file; overrides TOKENS_OUTPUT (compose mode only)
          -h, --help        show this help
        --user and --tenant print only the token on stdout (no trailing newline); diagnostics go to stderr.
        environment:
          DEV_JWT_SIGNING_KEY   required, at least 32 bytes
          USERS_FILE            default: users.json next to the binary
          TOKENS_OUTPUT         default: ./tokens.json
        exit codes: 0 ok, 1 output not writable, 2 usage error or unknown user,
                    3 signing key missing or shorter than 32 bytes, 4 users file unreadable or invalid

        """;

    private static readonly string[] ValueOptions = ["--user", "--tenant", "--services", "--sub", "--users", "--output"];

    /// <exception cref="CliException">exit code 2 for any usage error.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
                return new CommandLine(Mode.Help);
            if (!ValueOptions.Contains(arg, StringComparer.Ordinal))
                throw Usage($"unknown argument '{arg}'");

            var value = i + 1 < args.Count ? args[i + 1] : null;
            // --services may be empty (a token with no service access is legal); every other value may not.
            if (value is null || value.StartsWith("--", StringComparison.Ordinal)
                || (arg != "--services" && string.IsNullOrWhiteSpace(value)))
                throw Usage($"{arg} needs a value");
            if (!values.TryAdd(arg, value))
                throw Usage($"{arg} given more than once");
            i++;
        }

        string? Get(string name) => values.GetValueOrDefault(name);
        bool Has(string name) => values.ContainsKey(name);

        if (Has("--user"))
        {
            if (Has("--tenant") || Has("--services") || Has("--sub"))
                throw Usage("--user cannot be combined with --tenant, --services or --sub");
            if (Has("--output"))
                throw Usage("--output applies to compose mode only");
            return new CommandLine(Mode.User, UsersPath: Get("--users"), User: Get("--user"));
        }

        if (Has("--tenant"))
        {
            if (Has("--users") || Has("--output"))
                throw Usage("--users and --output do not apply to ad-hoc tokens (--tenant)");
            return new CommandLine(Mode.AdHoc, Tenant: Get("--tenant"), Services: Get("--services"), Sub: Get("--sub"));
        }

        if (Has("--services") || Has("--sub"))
            throw Usage("--services and --sub need --tenant");

        return new CommandLine(Mode.Compose, UsersPath: Get("--users"), OutputPath: Get("--output"));
    }

    private static CliException Usage(string message) => new(ExitCodes.Usage, message, showUsage: true);
}
