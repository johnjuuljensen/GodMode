# Provisioning Site Implementation

How `ingodmode.xyz` — the browser-facing provisioning site — integrates with Vault to give end users a zero-knowledge credential experience.

This document describes the **target architecture**. The existing Vault API (see [vault.md](./vault.md)) assumes plaintext-in / plaintext-out. The changes called for here make Vault zero-knowledge: it stores ciphertext it cannot decrypt. Implementing this is a planned migration, not the current state.

## Core Idea

The provisioning site does the crypto. Vault is a dumb authenticated blob store. The site runs entirely in the user's browser for any operation that touches plaintext — secrets are encrypted before upload, decrypted after download, and the site's backend only brokers OAuth, launches VMs, and orchestrates the handoff.

```
User Browser                         Vault                  Azure / Target VM
┌──────────────────────┐            ┌─────────────┐        ┌──────────────┐
│  ingodmode.xyz       │            │             │        │              │
│  (static JS + SPA)   │──OAuth────▶│  auth only  │        │              │
│                      │            │             │        │              │
│  Passkey / passphrase│◀──ciphertext blobs──────▶│        │              │
│  ↓                   │            │             │        │              │
│  Derives KEK         │            │  cannot     │        │              │
│  Encrypts/decrypts   │            │  decrypt    │        │              │
│  locally             │            │             │        │              │
│  ↓                   │            └─────────────┘        │              │
│  Seals bundle to VM──────────────────sealed bundle──────▶│  decrypts    │
└──────────────────────┘                                   └──────────────┘
```

Vault never decrypts user data. The provisioning-site backend never sees plaintext or the user's KEK.

## Vault API Changes

The existing `/api/secrets/*` endpoints keep the same shape but change semantics: request/response bytes are **client-ciphertext**, not plaintext. The `valueBase64` field, the raw body, and `fetch` responses all carry AES-GCM-encrypted blobs that only the browser can decrypt.

Vault may still wrap blobs in a server-side HKDF(VMS, user_sub) layer as defense in depth — this is transparent to clients.

Two new endpoints are needed:

### `GET /api/vault/profile`

Returns the authenticated user's vault setup state. The browser calls this on every visit to decide between setup UI and unlock UI.

```json
{
  "initialized": true,
  "salt": "<base64 16-byte salt>",
  "passkeyCredentialIds": ["<base64 cred id>", "..."],
  "wrappedKek": {
    "passkey": "<base64 passkey-PRF-wrapped KEK>",
    "recoveryCode": "<base64 Argon2id-wrapped KEK>"
  },
  "algVersion": 1
}
```

Returns `{ "initialized": false }` if the user has no vault yet.

### `PUT /api/vault/profile`

Stores the vault setup material. Request body mirrors the response shape above (minus `initialized`). Vault treats everything as opaque — it validates lengths and formats but never decrypts.

`algVersion` reserves room for future algorithm changes (e.g., migrating from Argon2id to Argon2d, from AES-GCM to XChaCha20-Poly1305).

## Client-Side Crypto Stack

| Concern | Library | Notes |
|---------|---------|-------|
| AES-GCM encryption | `crypto.subtle` (WebCrypto) | Built-in, fast, constant-time. |
| Argon2id | `argon2-browser` (wasm) | For recovery-code and fallback key derivation. |
| Passkey / PRF | `navigator.credentials` (WebAuthn) | Native browser API. |
| Sealed-box to VM | `libsodium-wrappers` (wasm) | Used in the VM handoff; see [vm-handoff.md](./vm-handoff.md). |
| Random bytes | `crypto.getRandomValues` | Always. Never `Math.random`. |

## Key Hierarchy

```
Passkey (PRF)      Recovery code (128-bit random, shown once)
     │                        │
     │                        ▼
     │                 Argon2id(code, salt)
     │                        │
     ▼                        ▼
passkey_prf_output      recovery_code_key
     │                        │
     ▼                        ▼
     HKDF-Expand                HKDF-Expand
     │                        │
     ▼                        ▼
     KEK ←────── both paths produce the SAME KEK ──────→ KEK

                            │
                            ▼
                  AES-GCM(KEK, per-blob nonce)
                            │
                            ▼
               encrypts individual secret values
```

At setup, the browser generates a random 256-bit **KEK**, then wraps it once under each unlock factor. Both wrapped copies are stored in vault. Unlocking with either factor recovers the same KEK, so secrets never need re-encryption when factors change.

### Argon2id Parameters

For the recovery-code path (fallback unlock):

```
memory:      64 MiB
iterations:  3
parallelism: 1
salt:        16 random bytes, stored per-user
output:      32 bytes
```

These target ~500 ms on a 2024-class laptop. Tuneable; bump `iterations` or `memory` as hardware improves.

## First-Visit Setup Flow

```
1. User arrives at ingodmode.xyz
2. OAuth sign-in (Google / GitHub / …)
3. GET /api/vault/profile  →  { initialized: false }
4. Show "Set up your secure vault" screen
5. Browser:
   a. Generate KEK = 32 random bytes
   b. Generate recovery_code = 16 random bytes (displayed as 24-char base32)
   c. Derive recovery_key = Argon2id(recovery_code, salt)
   d. wrapped_recovery = AES-GCM(recovery_key, KEK)
6. Prompt "Register a passkey" → navigator.credentials.create({ publicKey, extensions: { prf: { eval: { first: ... } } } })
7. If PRF is supported:
      prf_output = passkey PRF result
      passkey_key = HKDF(prf_output)
      wrapped_passkey = AES-GCM(passkey_key, KEK)
8. PUT /api/vault/profile with {
     salt,
     passkeyCredentialIds: [cred.id],
     wrappedKek: { passkey: wrapped_passkey?, recoveryCode: wrapped_recovery }
   }
9. SHOW RECOVERY CODE ONCE, tell user to save it.
   Block "Continue" until user confirms they saved it.
10. Store KEK in memory only. Do not persist it.
```

If the passkey doesn't support PRF (step 7), skip the passkey-wrapped KEK and store only the recovery-code-wrapped KEK. The user will need the recovery code on each unlock. UI should warn them.

## Return-Visit Unlock Flow

```
1. OAuth sign-in
2. GET /api/vault/profile  →  { initialized: true, ... }
3. If passkey-wrapped KEK exists:
      navigator.credentials.get({ publicKey: { allowCredentials, extensions: { prf: ... } } })
      prf_output = passkey PRF result
      KEK = AES-GCM-decrypt(wrapped_passkey, HKDF(prf_output))
4. Else (or "Use recovery code instead"):
      user types recovery code
      recovery_key = Argon2id(code, salt)
      KEK = AES-GCM-decrypt(wrapped_recovery, recovery_key)
5. KEK held in memory for the session. Not persisted.
```

## Storing a Secret

```
plaintext    = user-entered string or binary
nonce        = 12 random bytes
ciphertext   = AES-GCM(KEK, nonce, plaintext)
blob         = [nonce (12B)][tag (16B)][ciphertext]

PUT /api/secrets/{profile}/{secret_name}/raw
Content-Type: application/octet-stream
Body: blob
```

Or via JSON:

```
PUT /api/secrets/{profile}/{secret_name}
{ "valueBase64": "<base64(blob)>", "ttl": "90d" }
```

Vault sees the blob, optionally re-wraps with HKDF(VMS, user_sub), and stores. It has no idea what's inside.

## Fetching and Decrypting

```
POST /api/secrets/check    { profile, secrets: [...] }
    → indicates which secrets are present / missing / expired

POST /api/secrets/fetch    { profile, secrets: [...] }
    → { SECRET_A: "<base64(blob)>", SECRET_B: "<base64(blob)>" }
```

For each returned blob, the browser splits off nonce + tag and AES-GCM-decrypts with KEK. The result is the original plaintext.

## End-to-End Provisioning Walkthrough

### 1. User asks to provision a server
User picks a template (e.g., "GodMode for Mega users"). The template defines the required profile and secret list.

### 2. Unlock vault
If KEK isn't in memory yet, run the unlock flow above.

### 3. Check presence
```js
const check = await vault.post('/api/secrets/check', {
  profile: 'godmode_for_mega_users',
  secrets: ['ANTHROPIC_API_KEY', 'JIRA_TOKEN', 'GITHUB_PAT'],
});
```

### 4. Fill missing secrets
For each `exists: false`:
- Show an entry field (password-masked input).
- On submit: `blob = AesGcmEncrypt(kek, nonce, utf8(value))`.
- `PUT /api/secrets/{profile}/{name}/raw` with the blob as the body.

### 5. Fetch all
```js
const blobs = await vault.post('/api/secrets/fetch', { profile, secrets });
const bundle = {};
for (const [name, b64] of Object.entries(blobs)) {
  const blob = base64Decode(b64);
  const nonce = blob.slice(0, 12);
  const ciphertext = blob.slice(12);
  const plaintext = await subtle.decrypt(
    { name: 'AES-GCM', iv: nonce },
    kek,
    ciphertext
  );
  bundle[name] = new TextDecoder().decode(plaintext);
}
```

### 6. Provision VM
```js
const { fqdn, vmToken } = await site.post('/api/provision', {
  template: 'godmode_for_mega_users',
});
```

The site backend creates the Azure VM with cloud-init containing `vmToken` and `fqdn`, then returns them to the browser. `vmToken` is held in memory only; never logged.

### 7. Poll for VM readiness
```js
let pubkeyResponse;
for (let i = 0; i < 60; i++) {
  try {
    pubkeyResponse = await fetch(`https://${fqdn}/handoff/pubkey`);
    if (pubkeyResponse.ok) break;
  } catch { /* still booting */ }
  await delay(5000);
}
```

### 8. Verify pubkey
```js
const { pubkey, hmac } = await pubkeyResponse.json();
const expected = await hmacSha256(vmToken, base64Decode(pubkey));
if (!constantTimeEqual(expected, base64Decode(hmac))) {
  throw new Error('Pubkey attestation failed — aborting');
}
```

### 9. Seal and ship
```js
const plaintextBundle = JSON.stringify({
  version: 1,
  createdAt: new Date().toISOString(),
  secrets: bundle,
});

const sealed = sodium.crypto_box_seal(
  sodium.from_string(plaintextBundle),
  base64Decode(pubkey)
);

await fetch(`https://${fqdn}/handoff/bundle`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/octet-stream' },
  body: sealed,
});
```

See [vm-handoff.md](./vm-handoff.md) for what happens on the VM side.

### 10. Wipe and redirect
```js
bundle = null;              // best-effort clear
vmToken = null;
pubkey = null;
window.location.href = `https://${fqdn}/`;
```

Note: JavaScript does not guarantee memory zeroization — GC'd strings may linger. This is a known browser limitation and cannot be fully mitigated. Keep plaintext in memory for as short a time as possible and never log it.

## UX Considerations

### "Set up your vault"
Frame it as *"Your secure vault — only you can open it"*, not as a password prompt. Explain in one sentence that you (the operator) cannot recover it. Show the recovery code in large, copyable, downloadable form. Offer a "Download as PDF" option.

### Passkey registration
Use the browser's native passkey UI. Don't build a custom modal. Label it "Touch ID / Windows Hello / security key" so users recognize what's being asked.

### Recovery-code prompts
Only surface the recovery code flow on devices where PRF-capable passkey auth fails or the user explicitly asks for it. Don't make users type 24 characters on every visit.

### Error surfaces
- **"Unlock failed"** — probably wrong recovery code or bitflipped ciphertext. Let them retry or switch factor.
- **"Vault server unavailable"** — network issue, retry with backoff.
- **"Your saved vault is corrupted"** — extremely rare; give them a path to wipe and start over. Tell them this will discard stored secrets.

### Session timeout
KEK expires from memory after N minutes of inactivity (suggest 15). On next action, re-prompt for passkey. Never persist KEK to `localStorage`, `sessionStorage`, IndexedDB, or cookies.

## Security Checklist for the Provisioning Site

- [ ] Enforce a strict Content-Security-Policy. No inline scripts, no third-party CDN-loaded JS.
- [ ] All app JS served with Subresource Integrity hashes.
- [ ] OAuth tokens stored in httpOnly, SameSite=Strict cookies.
- [ ] `vm_token` never logged, never written to storage.
- [ ] Browser JS is audited; no analytics libraries, no ad pixels, no error reporters that can exfiltrate request bodies.
- [ ] Site runs as a static bundle with no server-side rendering of user state. Backend only serves OAuth, provisioning orchestration, and template metadata.
- [ ] TLS 1.3 only, HSTS preload, certificate pinning optional but encouraged for the Vault API.
- [ ] Regular dependency audits; libsodium-js and argon2-browser are the critical dependencies.

## What the Provisioning Site Is NOT

- Not a key recovery service. If a user loses passkey + recovery code, their stored ciphertexts are permanently undecryptable. They re-enter their credentials and we store fresh ciphertexts under a new KEK.
- Not a credential viewer. There is no "show me my saved secrets" UI — secrets are decrypted only on provisioning, only for sealing to a specific VM.
- Not the keeper of `vm_token`. It generates and transmits it but does not persist it.

## Open Questions

- **Multiple passkeys per account.** Users may want to register a second passkey (e.g., phone + laptop). `wrappedKek.passkey` becomes a map `{ credId → wrapped }`. Adding a new passkey requires unlocking with an existing factor, then wrapping KEK under the new one. Deletion is similar but cannot remove the last factor.
- **Shared vault across OAuth providers.** Currently the same person signing in via Google vs GitHub has two separate vaults (different `user_sub`). A "link accounts" feature would require careful design to avoid becoming a re-identification sidechannel. Out of scope for now.
- **Server-side rate limiting of unlock attempts.** Vault should reject more than ~5 `GET /api/vault/profile` per minute per user to slow down scripted attacks against leaked wrapped-KEKs. Not strictly necessary (Argon2id already slows offline attacks) but cheap insurance.
