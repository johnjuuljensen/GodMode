# GodMode.Vault

A headless, encrypted credentials vault for provisioning GodMode servers. No UI — pure API, Docker-deployable.

## Problem

GodMode servers are transient. They receive secrets (API keys, tokens, PATs) at provision time and hold them for their lifetime. The question is: where do those secrets live between server lifetimes, and how does a non-technical user supply them?

Vault solves this by giving each user an encrypted, persistent store tied to their Google or GitHub identity. The provisioning system checks Vault for required secrets, and if any are missing, directs the user to enter them before proceeding.

## Architecture

```
Provisioning System          GodMode Vault              Storage
┌──────────────────┐        ┌──────────────────┐       ┌─────────────┐
│                  │──check──▶                  │──r/w──▶ Persistent  │
│  Sends profile   │        │  OAuth verify     │       │ volume      │
│  name + schema   │◀─ready──│  HKDF encrypt    │       │ {sub}/      │
│                  │        │  File store       │       │  {profile}/ │
│  Has its own UI  │──fetch──▶                  │       │   {secret}  │
│  for secret entry│        └──────────────────┘       └─────────────┘
└──────────────────┘
```

Key points:
- **Vault has no UI.** The provisioning system (or any client) owns the user experience.
- **Vault authenticates every request** via Google or GitHub OAuth. A bare `user_sub` is not sufficient.
- **Secrets are encrypted at rest** using AES-256-GCM with per-user keys derived via HKDF from a vault master secret + user identity.
- **Storage is files on a persistent volume**, organized as `{user_sub}/{profile}/{secret_name}`.

## Provisioning Flow

This is the intended end-to-end flow for provisioning a GodMode server:

### 1. User initiates provisioning

The user opens the provisioning system (a web app, CLI, or CI pipeline) and requests a GodMode server — e.g., "GodMode for normal Mega users."

### 2. Provisioning system checks Vault

The provisioner knows which secrets are needed for this server type. It sends the profile name and schema to Vault:

```http
POST /api/secrets/check
Authorization: <user's OAuth cookie/token>

{
  "profile": "godmode_for_normal_mega_users",
  "secrets": ["ANTHROPIC_API_KEY", "JIRA_TOKEN", "GITHUB_PAT"]
}
```

Vault checks if all named secrets exist (and are not expired) for the authenticated user under that profile.

### 3a. All secrets present — fetch and provision

If `ready: true`, the provisioner fetches the actual values:

```http
POST /api/secrets/fetch
Authorization: <user's OAuth cookie/token>

{
  "profile": "godmode_for_normal_mega_users",
  "secrets": ["ANTHROPIC_API_KEY", "JIRA_TOKEN", "GITHUB_PAT"]
}
```

Response (200):
```json
{
  "ANTHROPIC_API_KEY": "c2stYW50Li4u",
  "JIRA_TOKEN": "dG9rZW4u",
  "GITHUB_PAT": "Z2hwXy4u"
}
```

Values are base64-encoded. The provisioner decodes them and injects as environment variables into the server (Docker `-e`, Codespace secrets, Azure container env, etc.).

### 3b. Secrets missing — redirect to entry UI

If `ready: false`, the response includes which secrets are missing:

```json
{
  "profile": "godmode_for_normal_mega_users",
  "ready": false,
  "secrets": [
    { "name": "ANTHROPIC_API_KEY", "exists": true, "expired": false, "expiresAt": null },
    { "name": "JIRA_TOKEN", "exists": false, "expired": false, "expiresAt": null },
    { "name": "GITHUB_PAT", "exists": false, "expired": false, "expiresAt": null }
  ]
}
```

The provisioning system shows its own secrets-entry form for the missing items. When the user fills them in, the provisioner stores each one:

```http
PUT /api/secrets/godmode_for_normal_mega_users/JIRA_TOKEN
Authorization: <user's OAuth cookie/token>

{ "valueBase64": "dG9rZW4u", "ttl": "90d" }
```

Then retries the check/fetch cycle.

### 4. Server runs autonomously

Once provisioned, the GodMode server has all secrets in its environment. It operates autonomously — no dependency on Vault or the client. When the server eventually dies, the next provisioning cycle repeats from step 2 (secrets are already in Vault, so it's typically instant).

## Setup

### Generate a master secret

```bash
# Option 1: OpenSSL
openssl rand -base64 32

# Option 2: The vault's built-in utility (requires auth)
curl -s https://vault.example.com/admin/generate-secret
```

### Configuration

All config can be set via environment variables (recommended for Docker) or `appsettings.json`.

| Setting | Env Var | Description |
|---------|---------|-------------|
| `Vault:MasterSecret` | `VAULT_MASTER_SECRET` | Base64-encoded 32-byte encryption key. **Required.** |
| `Vault:StoragePath` | `VAULT_STORAGE_PATH` | Path to persistent secret storage. Default: `~/.godmode-vault/secrets` |
| `Auth:Google:ClientId` | `Auth__Google__ClientId` | Google OAuth client ID |
| `Auth:Google:ClientSecret` | `Auth__Google__ClientSecret` | Google OAuth client secret |
| `Auth:GitHub:ClientId` | `Auth__GitHub__ClientId` | GitHub OAuth app client ID |
| `Auth:GitHub:ClientSecret` | `Auth__GitHub__ClientSecret` | GitHub OAuth app client secret |

At least one OAuth provider must be configured. Both can be enabled simultaneously.

### Docker

```bash
docker run -d \
  --name godmode-vault \
  -v vault-data:/data/secrets \
  -e VAULT_MASTER_SECRET=$(openssl rand -base64 32) \
  -e Auth__Google__ClientId=your-client-id \
  -e Auth__Google__ClientSecret=your-client-secret \
  -p 8080:8080 \
  godmode-vault
```

The `/data/secrets` volume must be persistent — it holds the encrypted secrets across container restarts.

### OAuth callback URLs

When registering OAuth apps, configure these callback URLs:

- **Google**: `https://vault.example.com/auth/callback/google`
- **GitHub**: `https://vault.example.com/auth/callback/github`

## API Reference

All endpoints under `/api/secrets` require authentication (Google or GitHub OAuth session).

### Check profile completeness

```
POST /api/secrets/check
```

Request body:
```json
{
  "profile": "profile_name",
  "secrets": ["SECRET_A", "SECRET_B"]
}
```

The profile and schema are provided by the caller (the provisioning system), not stored in Vault. Vault simply checks whether the named secrets exist for the authenticated user.

Returns `{ ready, profile, secrets: [{ name, exists, expired, expiresAt }] }`.

### Fetch profile secrets

```
POST /api/secrets/fetch
```

Same request body as check. Returns 200 with `{ "SECRET_A": "<base64>", ... }` if all present, or 409 with the check result if any are missing.

### Store a secret (JSON)

```
PUT /api/secrets/{profile}/{secretName}
```

```json
{
  "valueBase64": "<base64-encoded value>",
  "ttl": "90d"
}
```

TTL is optional. Supported formats: `90d` (days), `24h` (hours), `30m` (minutes). Blank values are rejected — existing secrets are never overwritten with empty content.

### Store a secret (raw binary)

```
PUT /api/secrets/{profile}/{secretName}/raw?ttl=90d
Content-Type: application/octet-stream

<raw bytes>
```

For binary secrets like certificates or key files.

### Get a secret

```
GET /api/secrets/{profile}/{secretName}
```

Returns the raw decrypted bytes with `Content-Type: application/octet-stream`. Returns 404 if missing or expired.

### Get secret metadata

```
GET /api/secrets/{profile}/{secretName}/meta
```

Returns `{ name, createdAt, ttl, isExpired, expiresAt }`.

### Delete a secret

```
DELETE /api/secrets/{profile}/{secretName}
```

### List profiles

```
GET /api/secrets/profiles
```

Returns profile names that have any stored secrets for the authenticated user.

### List secrets in a profile

```
GET /api/secrets/{profile}
```

Returns secret names stored under the given profile.

## Naming Rules

Profile names and secret names must match `[a-zA-Z0-9_-]+`. Names like `ANTHROPIC_API_KEY`, `my-profile`, `jira_token` are valid. Names containing `.`, `/`, `\`, spaces, or `..` are rejected with 400.

## Auth Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /auth/login/google` | Initiate Google OAuth login |
| `GET /auth/login/github` | Initiate GitHub OAuth login |
| `GET /auth/logout` | Clear session |
| `GET /auth/me` | Current user info (`{ authenticated, provider, name, sub }`) |
| `GET /health` | Health check |

## Encryption

Secrets are encrypted at rest using envelope encryption:

1. A **Vault Master Secret** (VMS) is set once at deployment via environment variable
2. Per-user keys are derived: `KEK = HKDF-SHA256(VMS, user_sub, "godmode-vault")`
3. Each secret is encrypted with AES-256-GCM using the user's KEK and a random 12-byte nonce
4. Stored wire format: `[nonce 12B][auth tag 16B][ciphertext]`

The user sub is prefixed with the auth provider (`google:123...`, `github:456...`) to prevent cross-provider collisions. This means the same person authenticating via Google and GitHub would have separate encryption keys and separate secret storage.

## Storage Layout

```
/data/secrets/
  google_104578293847561234567/       # user sub (sanitized)
    godmode_for_normal_mega_users/    # profile
      ANTHROPIC_API_KEY               # encrypted binary
      ANTHROPIC_API_KEY.meta.json     # { name, createdAt, ttl }
      JIRA_TOKEN
      JIRA_TOKEN.meta.json
    personal_projects/
      GITHUB_PAT
      GITHUB_PAT.meta.json
  github_12345678/
    ...
```

## TTL

Secrets can have an optional time-to-live. When a secret's TTL has elapsed (calculated from `createdAt`), it is treated as missing — check returns `exists: false, expired: true`, and get returns 404. The encrypted file remains on disk until explicitly deleted or overwritten.

Storing a new value for the same secret name replaces it and resets the creation time.
