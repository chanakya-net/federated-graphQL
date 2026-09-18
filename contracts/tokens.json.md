# `tokens.json` and JWT contract

## `tokens.json`

Written by `token-generator` into a shared volume; served by the UI's Nginx at `/tokens.json`.
It is a JSON **array**. Each element:

```json
{
  "sub": "bob",
  "name": "Bob (Tenant A)",
  "tenantId": "TenantA",
  "services": ["patch", "vulnerability"],
  "token": "eyJhbGciOiJIUzI1NiIs..."
}
```

- Order is the order of `users.json`.
- The UI shows `name`, `tenantId` and `services`, and sends `token` as `Authorization: Bearer <token>`.
- `users.json` (owned by P2A) contains exactly these five users (plan §4.4):

| `sub` | `name` | `tenantId` | `services` |
|---|---|---|---|
| `alice` | Alice (Tenant A) | `TenantA` | `patch`, `vulnerability`, `softwareinstall` |
| `bob` | Bob (Tenant A) | `TenantA` | `patch`, `vulnerability` |
| `carol` | Carol (Tenant A) | `TenantA` | `softwareinstall` |
| `dave` | Dave (Tenant B) | `TenantB` | `patch`, `vulnerability`, `softwareinstall` |
| `erin` | Erin (Tenant B) | `TenantB` | `patch` |

## JWT

Created only by `SoR.Shared.Auth.DevTokenFactory.Create` (TokenGenerator and every test project use it).

| Part | Value |
|---|---|
| Header | `alg = HS256`, `typ = JWT` |
| Key | UTF-8 bytes of `DEV_JWT_SIGNING_KEY` (committed dev key in `.env`, 64 ASCII chars; ≥ 32 bytes required) |
| `iss`, `aud` | `"sor-poc"` |
| `sub` | user id, e.g. `"bob"` |
| `name` | display name |
| `tenantId` | `"TenantA"` or `"TenantB"` (string) |
| `services` | JSON **array** of strings, always an array even with one entry; values from `patch`, `vulnerability`, `softwareinstall`; may be empty |
| `iat`, `nbf`, `exp` | numeric dates; `exp` at least 5 years after `iat` |

Validation (`AddDevJwtAuthentication`): issuer, audience, signature and lifetime are checked, clock skew
1 minute, inbound claim mapping **off** (`sub`, `tenantId`, `services` keep their names). The `services`
array surfaces as one `services` claim per entry, which is what `RequireClaim("services", "<name>")`
matches.
