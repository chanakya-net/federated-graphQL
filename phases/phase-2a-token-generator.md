# Phase 2A — Token generator (one-shot Compose service)

**Lane A. Starts after Phase 1. Parallel with every other Stage 2 lane.**
**Effort:** 0.5 day. **Owns:** `src/TokenGenerator/`, `tests/TokenGenerator.Tests/`.

## 1. Purpose

Mint one long-lived HS256 JWT per dummy user and write `tokens.json` for the UI, inside `docker compose up`, with no manual step. Also serve as the ad-hoc token tool for developers and the demo.

## 2. Inputs

- `contracts/tokens.json.md` (output shape, claim contract).
- `src/Shared.Auth` (`DevTokenFactory`, `DevAuth` constants).
- Plan §4.4 (the five users).

## 3. `src/TokenGenerator/users.json` (committed)

```json
[
  { "sub": "alice", "name": "Alice (Tenant A)", "tenantId": "TenantA", "services": ["patch", "vulnerability", "softwareinstall"] },
  { "sub": "bob",   "name": "Bob (Tenant A)",   "tenantId": "TenantA", "services": ["patch", "vulnerability"] },
  { "sub": "carol", "name": "Carol (Tenant A)", "tenantId": "TenantA", "services": ["softwareinstall"] },
  { "sub": "dave",  "name": "Dave (Tenant B)",  "tenantId": "TenantB", "services": ["patch", "vulnerability", "softwareinstall"] },
  { "sub": "erin",  "name": "Erin (Tenant B)",  "tenantId": "TenantB", "services": ["patch"] }
]
```

Set `<Content Include="users.json" CopyToOutputDirectory="PreserveNewest" />` in the csproj so it ships in the image.

## 4. CLI contract

```
TokenGenerator                                   # compose mode: read USERS_FILE, write TOKENS_OUTPUT, print summary, exit 0
TokenGenerator --user bob                        # print bob's token only (stdout, no newline noise), exit 0
TokenGenerator --tenant TenantB --services patch,softwareinstall [--sub adhoc]   # ad-hoc token, exit 0
TokenGenerator --users <path> --output <path>    # explicit paths (override env)
```

Environment: `DEV_JWT_SIGNING_KEY` (required), `USERS_FILE` (default `users.json` next to the binary), `TOKENS_OUTPUT` (default `./tokens.json`).

Exit codes: 0 success; 2 usage error; 3 missing/short signing key; 4 users file unreadable or invalid.

Rules:
- Expiry: `SeedConstants.Epoch + 10 years`? No — expiry must be relative to real time so the demo works whenever it is run: `DateTimeOffset.UtcNow.AddYears(10)`.
- `--user` and ad-hoc modes print **only** the token to stdout so scripts can do `TOKEN=$(docker compose run --rm -T token-generator --user alice)`. All diagnostics go to stderr.
- Compose mode writes the file atomically (write `tokens.json.tmp`, then rename) and creates the output directory if missing.
- Compose mode re-generates on every run (tokens are cheap; the file is not cached).
- Validate `services` values against `DevAuth.Services.All`; unknown value → exit 4 with a clear message.

## 5. Implementation notes

- Plain `args` parsing; no CLI library.
- `System.Text.Json` with `JsonSerializerDefaults.Web`, `WriteIndented = true`.
- Output element order: `sub`, `name`, `tenantId`, `services`, `token` (matches the contract; the UI does not care about order, humans do).
- Print to stderr in compose mode: one line per user `minted <sub> tenant=<tenant> services=<a,b>` and the output path.

Skeleton:

```csharp
using System.Text.Json;
using SoR.Shared.Auth;

var key = Environment.GetEnvironmentVariable(DevAuth.SigningKeyEnv);
if (string.IsNullOrWhiteSpace(key) || System.Text.Encoding.UTF8.GetByteCount(key) < 32)
{ Console.Error.WriteLine($"{DevAuth.SigningKeyEnv} missing or shorter than 32 bytes"); return 3; }

var expires = DateTimeOffset.UtcNow.AddYears(10);
// ... parse args into: mode (Compose | User | AdHoc), usersPath, outputPath, user, tenant, services
// Compose: users = JsonSerializer.Deserialize<List<UserSpec>>(File.ReadAllText(usersPath))
//          entries = users.Select(u => new TokenEntry(u.Sub, u.Name, u.TenantId, u.Services,
//                       DevTokenFactory.Create(key, u.Sub, u.Name, u.TenantId, u.Services, expires)))
//          write atomically; return 0
// User:    find in users; Console.Write(token); return 0 (exit 2 if not found)
// AdHoc:   Console.Write(DevTokenFactory.Create(key, sub ?? "adhoc", sub ?? "adhoc", tenant, services, expires)); return 0
```

## 6. Dockerfile (`src/TokenGenerator/Dockerfile`, context = repo root)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:<ver> AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Shared.Auth/Shared.Auth.csproj src/Shared.Auth/
COPY src/TokenGenerator/TokenGenerator.csproj src/TokenGenerator/
RUN dotnet restore src/TokenGenerator/TokenGenerator.csproj
COPY src/Shared.Auth src/Shared.Auth
COPY src/TokenGenerator src/TokenGenerator
RUN dotnet publish src/TokenGenerator/TokenGenerator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:<ver>-alpine
WORKDIR /app
COPY --from=build /app .
ENV USERS_FILE=/app/users.json TOKENS_OUTPUT=/tokens/tokens.json
ENTRYPOINT ["dotnet", "TokenGenerator.dll"]
```

Runtime image is `runtime`, not `aspnet` (console app). The compose entry (owned by P2C) mounts the `tokens` volume at `/tokens` and sets `restart: "no"`.

## 7. Tests (`tests/TokenGenerator.Tests`, unit only)

Run the generator in-process by extracting the logic into a `TokenGeneratorApp.Run(string[] args, IDictionary<string,string?> env, TextWriter stdout, TextWriter stderr)` method that `Program.Main` calls.

| Test | Assertion |
|---|---|
| `Compose_mode_writes_five_entries` | output file parses to 5 entries in `users.json` order with all five fields |
| `Tokens_validate_with_shared_parameters` | each `token` validates using `Shared.Auth` parameters; `tenantId` and `services` claims match the entry |
| `Services_claim_is_array` | decoded JWT payload has `services` as a JSON array |
| `Expiry_is_at_least_five_years_out` | `exp` ≥ now + 5 y |
| `User_mode_prints_only_token` | stdout equals a three-segment JWT with no trailing newline noise; stderr may have text |
| `Unknown_user_exits_2` | |
| `Missing_key_exits_3` | |
| `Unknown_service_exits_4` | users file with `"services": ["nope"]` |
| `Output_is_atomic` | no `tokens.json.tmp` left behind after success |

## 8. Definition of Done

- [ ] All three CLI modes work locally with `DEV_JWT_SIGNING_KEY` from `.env`.
- [ ] `docker build -f src/TokenGenerator/Dockerfile .` succeeds; `docker run --rm -e DEV_JWT_SIGNING_KEY=... <image> --user alice` prints a token.
- [ ] Tests in §7 pass.
- [ ] `users.json` contains exactly the five users from plan §4.4.
- [ ] README gains a two-line "get a token" snippet.
- [ ] Deviations recorded in `docs/version-facts.md §8`.
