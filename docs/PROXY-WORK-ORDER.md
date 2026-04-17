# OAuth Proxy Work Order

**Target**: Cloudflare Worker at `godmode-auth-proxy.mortenkremmer.workers.dev`

## Issue 1: Reduce Google Login Scopes

### Problem
When a GodMode instance initiates a Google login flow (`purpose=login`), the proxy should only request minimal scopes. Currently Google asks for broader permissions than needed for login.

### Current flow
1. GodMode server redirects browser to proxy: `GET /authorize?provider=google&instance=...&state=...&scope=openid%20email%20profile`
2. Proxy redirects to Google with OAuth scopes
3. Google shows consent screen with requested permissions

### Required change
The GodMode server now passes a `scope` query parameter to `/authorize`. The proxy should:

1. **When `scope` param is present**: Use exactly those scopes in the Google OAuth request. Do not add extra scopes.
2. **When `scope` param is absent** (connector flows): Use the existing default scopes (calendar, gmail, drive, etc.)

Login flow sends: `scope=openid email profile`
Connector flow sends: no `scope` param (use defaults)

### Verification
- Login flow consent screen should only show "See your email address" and "See your personal info" — no calendar/drive/gmail permissions
- Connector flows should continue to request the broad scopes needed for MCP access

---

## Issue 2: Fix OAuth Consent Screen Branding

### Problem
Google's consent screen shows **"godmode-auth-proxy.mortenkremmer.workers.dev is asking for permission"** (or similar worker.dev domain). Users see a Cloudflare Workers domain instead of proper GodMode branding.

### Required change
In the **Google Cloud Console** project that owns the OAuth client ID used by the proxy:

1. Go to **APIs & Services > OAuth consent screen**
2. Set **App name** to "GodMode" (or "GodMode Authentication")
3. Upload an **app logo** (optional but recommended)
4. Set **Application home page** to the production URL
5. Set **Privacy policy** and **Terms of service** URLs if needed for verification
6. Under **Authorized domains**, add the production domain

If the OAuth consent screen is in "Testing" mode, consider submitting for verification so all users see the branded screen rather than an "unverified app" warning.

### Verification
- Google consent screen should show "GodMode" as the app name
- No "worker.dev" domain visible to the user
- Logo appears if configured

---

## Context: Proxy API Contract

For reference, these are the proxy endpoints the GodMode server calls:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/authorize` | GET | Redirect to OAuth provider. Params: `provider`, `instance`, `state`, `profile?`, `scope?` |
| `/callback` | GET | OAuth provider callback (not called by GodMode directly) |
| `/relay/redeem` | POST | Exchange relay token for OAuth tokens. Body: `{ "relayToken": "..." }` |
| `/token/refresh` | POST | Refresh expired token. Body: `{ "provider": "...", "refreshToken": "..." }` |

The `scope` parameter on `/authorize` is the new addition from Issue 1 — the proxy should forward it to the OAuth provider's authorization URL.
