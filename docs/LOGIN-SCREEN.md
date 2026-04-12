# GodMode Login Screen — Reusable Reference

This document describes the login/authentication system used by GodMode. It covers the full flow from server configuration to React UI, and can be adapted for other projects.

## Architecture Overview

```
Browser                    GodMode Server               OAuth Proxy (CF Worker)        Google
  │                            │                              │                          │
  ├─ GET /api/auth/challenge ──►│                              │                          │
  │◄── { method, authenticated }│                              │                          │
  │                            │                              │                          │
  │  (if not authenticated)    │                              │                          │
  ├─ GET /api/oauth/initiate ──►│                              │                          │
  │  ?provider=google          │── GET /authorize ────────────►│                          │
  │  &purpose=login            │   ?provider&instance&state    │── redirect ──────────────►│
  │◄── 302 to proxy ───────────│   &scope=openid+email+profile│                          │
  │                            │                              │◄── callback + code ───────│
  │                            │                              │── exchange code for tokens │
  │                            │                              │── store relay token in KV  │
  │◄── 302 to instance/api/oauth/relay?relay=TOKEN ───────────│                          │
  │                            │                              │                          │
  ├─ GET /api/oauth/relay ─────►│                              │                          │
  │  ?relay&provider&state     │── POST /relay/redeem ────────►│                          │
  │                            │◄── { accessToken, email } ────│                          │
  │                            │── validate email vs allowed   │                          │
  │                            │── set auth cookie             │                          │
  │◄── 302 / (with cookie) ───│                              │                          │
  │                            │                              │                          │
  ├─ (authenticated requests) ─►│                              │                          │
```

## Auth Modes

The server detects its auth mode at startup (first match wins):

| Mode | Condition | Behavior |
|------|-----------|----------|
| `google` | `Authentication:Google:AllowedEmail` is set | OAuth login required. Only the specified email can sign in. |
| `codespace` | `CODESPACES=true` env var | GitHub Codespace token auth (handled by `GodModeAuthenticationHandler`) |
| `apikey` | `Authentication:ApiKey` is set | Bearer token auth (handled by `GodModeAuthenticationHandler`) |
| `none` | No auth config | No authentication. All requests allowed. |

### Configuration (Google mode)

```bash
# Environment variable (recommended for development/codespaces)
Authentication__Google__AllowedEmail=user@example.com

# Or in appsettings.json
{
  "Authentication": {
    "Google": {
      "AllowedEmail": "user@example.com"
    }
  }
}
```

No Google Client ID is needed — the OAuth proxy handles all OAuth credentials.

## Server Endpoints

### `GET /api/auth/challenge`
Returns the auth method and whether the current request is authenticated.

```json
{ "method": "google", "authenticated": false }
```

Used by the React app on startup to decide whether to show the login page.

### `GET /api/oauth/initiate?provider=google&purpose=login`
Redirects the browser to the OAuth proxy's `/authorize` endpoint. Passes:
- `provider`: OAuth provider (google, microsoft, atlassian)
- `instance`: This server's public URL (for redirect back)
- `state`: CSRF token
- `scope`: `openid email profile` for login flows

### `GET /api/oauth/relay?relay=TOKEN&provider=google&state=CSRF`
Callback from the OAuth proxy after successful authentication:
1. Validates CSRF state
2. Redeems relay token for OAuth tokens (via proxy's `/relay/redeem`)
3. Extracts email from proxy response (or fetches from Google userinfo as fallback)
4. Validates email against `AllowedEmail`
5. Creates auth cookie (`GodMode.Auth`) with claims
6. Redirects to `/`

### `POST /api/auth/logout`
Signs out and clears the auth cookie.

## React Components

### `App.tsx` — Auth Gate
```
On mount:
  1. Fetch /api/auth/challenge
  2. If method=google AND not authenticated → render <LoginPage>
  3. Otherwise → render <Shell> (main app)
  4. Check URL for ?error= param and pass to LoginPage
```

### `LoginPage.tsx` — Login UI
A centered card with:
- GodMode logo (SVG layers icon)
- "GodMode" title
- "Sign in to continue" subtitle
- Error message area (access_denied, csrf_mismatch, relay_failed, network)
- "Sign in with Google" button with Google logo

**The button simply redirects to**: `/api/oauth/initiate?provider=google&purpose=login`

No JavaScript SDK, no client-side OAuth. The entire flow is server-driven redirects.

### `Auth.css` — Styles
- `.auth-page`: Full viewport centered flex container
- `.auth-card`: 380px wide glassmorphism card with blur backdrop
- `.auth-google-btn`: Full-width button with Google colors logo
- `.auth-error`: Red error banner
- `.auth-form`, `.auth-input`, `.auth-submit-btn`: For apikey mode (form-based login)

Uses CSS custom properties from the app's theme: `--bg-base`, `--glass`, `--glass-border`, `--accent`, `--text-primary`, `--text-secondary`, `--error`, `--error-soft`, `--shadow-window`, `--radius`.

## Server-Side Auth Setup

### Google mode (`GoogleAuthExtensions.cs`)
- Registers cookie authentication (`GodMode.Auth` cookie, SameSite=Lax, HttpOnly)
- Stores `GoogleAuthOptions` with the allowed email
- 401 on unauthenticated requests (no redirect — React handles the UI)

### Codespace/ApiKey mode (`GodModeAuthenticationHandler.cs`)
- Custom `AuthenticationHandler<AuthenticationSchemeOptions>`
- Codespace: validates GitHub token from `Authorization: Bearer` header
- ApiKey: validates against configured `Authentication:ApiKey` value

## CSRF Protection

The `OAuthCsrfStore` generates a random state token before each OAuth redirect and validates it on the relay callback. Entries expire after 10 minutes. The state also carries metadata (provider, profileId, purpose) to route the callback correctly.

## Adapting for Other Projects

### What to keep
- The challenge/initiate/relay endpoint pattern
- The OAuth proxy integration (no client-side credentials needed)
- Cookie-based session after OAuth
- CSRF state management
- The React auth gate pattern (fetch challenge → render login or app)

### What to change
- `AllowedEmail` → replace with your access control (multiple users, domain check, database lookup)
- Auth cookie name and options
- Login page branding (logo, title, subtitle)
- Error message strings
- Add additional OAuth providers if needed (the proxy supports google, microsoft, atlassian)

### Minimal integration
1. Copy `Auth/` directory (GoogleAuthExtensions, GodModeAuthenticationHandler, OAuthCsrfStore)
2. Copy `Services/OAuthProxyClient.cs` and `Services/OAuthTokenStore.cs`
3. Add the `/api/auth/*` and `/api/oauth/*` endpoints from `Program.cs`
4. Copy `components/Auth/` (LoginPage.tsx, Auth.css) and the auth gate from App.tsx
5. Configure `Authentication:Google:AllowedEmail` and point to the OAuth proxy
