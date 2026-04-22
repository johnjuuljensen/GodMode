# Secure VM Handoff

How credentials travel from the user's browser to a freshly provisioned GodMode VM without ever being readable by the provisioning service, by Vault, or by anyone on the network path.

## Goal

The credentials end up in cleartext *inside the VM* (they must — services read them as env vars or files). Everywhere else on the path they are ciphertext that only the target VM can decrypt.

Specifically:
- The provisioning site (ingodmode.xyz) must never see plaintext credentials.
- Vault must never see plaintext credentials.
- Anyone with network access (including cloud providers operating middleboxes) must not see plaintext.
- A VM impersonator must not be able to trick the browser into sending credentials to it.

The browser is the only party (besides the target VM) that sees plaintext. That is unavoidable — the user types them there, or the browser decrypts stored ciphertexts there.

## Cryptographic Primitives

- **X25519** for key agreement (VM keypair).
- **XSalsa20-Poly1305 via libsodium sealed boxes** (`crypto_box_seal` / `crypto_box_seal_open`) for the bundle envelope. Sealed boxes give anonymous-sender authenticated encryption to a known recipient pubkey — exactly what we want.
- **HMAC-SHA256** for pubkey authentication.
- **TLS 1.3** on all legs, with a Let's Encrypt cert on the VM.

HPKE (RFC 9180) is an acceptable substitute for sealed boxes if you prefer IETF-standard primitives; the rest of the design is identical.

## Actors and Trust

| Actor | Holds | Trusted for |
|-------|-------|-------------|
| Browser | user plaintext, vault KEK, `vm_token` | producing the bundle |
| Provisioning site | OAuth session, Azure API creds, `vm_token` (briefly) | launching the VM, brokering the handoff, nothing cryptographic |
| Vault | ciphertext blobs, optional VMS wrapper key | storing opaque blobs |
| Target VM | X25519 keypair, `vm_token`, final plaintext | decrypting the bundle, running services |

The provisioning site sees `vm_token` because it generates it and passes it to both cloud-init and the browser. If the site is compromised, an attacker can MITM the handoff for newly-provisioning VMs (they'd replace the VM's pubkey with their own). That risk is unavoidable in any system where the provisioning page orchestrates the flow; see §Residual Risks. Already-provisioned VMs are unaffected.

## End-to-End Flow

```
Browser                Provisioning Site          Azure                  Target VM              Vault
   │                          │                     │                       │                     │
   │──1. unlock vault─────────▶                     │                       │                     │
   │                          │──2. fetch ciphertext──────────────────────────────────────────────▶
   │◀──────────────── ciphertext blobs ──────────────────────────────────────────────────────────│
   │──3. decrypt locally      │                     │                       │                     │
   │──4. request provision────▶                     │                       │                     │
   │                          │──5. generate vm_token                       │                     │
   │                          │──6. create VM (cloud-init contains vm_token, FQDN)────▶            │
   │◀──7. {fqdn, vm_token}────│                     │                       │                     │
   │                          │                     │                       │──8. generate keypair│
   │                          │                     │                       │──9. start handoff endpoint
   │                          │                     │                       │     on https://{fqdn}/handoff
   │──10. GET https://{fqdn}/handoff/pubkey ────────────────────────────────▶                     │
   │◀──{pubkey, hmac(vm_token, pubkey)}──────────────────────────────────── │                     │
   │──11. verify hmac         │                     │                       │                     │
   │──12. seal bundle to pubkey                     │                       │                     │
   │──13. POST sealed bundle to https://{fqdn}/handoff/bundle ──────────────▶                     │
   │                          │                     │                       │──14. decrypt
   │                          │                     │                       │──15. write secrets
   │                          │                     │                       │──16. wipe keypair
   │                          │                     │                       │──17. disable endpoint
   │◀──{ ok }──────────────────────────────────────────────────────────────│                     │
```

### 1–3. Browser obtains plaintext credentials

See [provisioning-site.md](./provisioning-site.md) for vault unlock and decryption details. At the end of step 3, the browser holds the full plaintext bundle — e.g.:

```json
{
  "ANTHROPIC_API_KEY": "sk-ant-...",
  "JIRA_TOKEN": "...",
  "GITHUB_PAT": "ghp_..."
}
```

### 4–7. Provisioning site creates the VM

The provisioning site:

1. Generates `vm_token = 32 random bytes`.
2. Allocates an FQDN (e.g., `vm-{slug}.godmode.xyz`) and creates a DNS record pointing at the VM's reserved public IP.
3. Starts the Azure VM with a cloud-init user-data payload containing `vm_token` and `fqdn`:

```yaml
#cloud-config
write_files:
  - path: /etc/godmode/handoff.env
    permissions: '0600'
    content: |
      VM_TOKEN={base64-vm-token}
      FQDN={fqdn}
runcmd:
  - /opt/godmode/handoff-bootstrap.sh
```

4. Returns `{ fqdn, vm_token }` to the browser over the site's own TLS session. `vm_token` is **never** stored persistently on the provisioning site — it lives only in the request/response cycle.

### 8–9. VM starts handoff service

`/opt/godmode/handoff-bootstrap.sh` runs as root on first boot:

1. Obtains a Let's Encrypt certificate for `fqdn` (HTTP-01 or DNS-01 challenge).
2. Generates an X25519 keypair in memory (tmpfs, mode 0600, root-only).
3. Starts a minimal HTTPS server on port 443 exposing:
   - `GET /handoff/pubkey` — returns `{ pubkey, hmac }` where `hmac = HMAC-SHA256(vm_token, pubkey)`.
   - `POST /handoff/bundle` — accepts a sealed box, decrypts with the privkey, writes secrets, responds `{ ok: true }`, then self-terminates (see §17).
4. Accepts at most **one** successful handoff, then terminates.

### 10–11. Browser fetches and authenticates the pubkey

The browser connects directly to the VM:

```
GET https://vm-xyz.godmode.xyz/handoff/pubkey
→ 200 OK
  {
    "pubkey":   "<base64 X25519 pubkey, 32 bytes>",
    "hmac":     "<base64 HMAC-SHA256(vm_token, pubkey), 32 bytes>",
    "issuedAt": "2026-04-17T14:22:01Z"
  }
```

The browser verifies:
- TLS cert chains to a trusted CA for `fqdn`. (Prevents ordinary MITM.)
- `hmac == HMAC-SHA256(vm_token, pubkey)`. (Prevents a compromised CA or DNS hijack from substituting a different pubkey — an attacker needs both TLS impersonation *and* `vm_token`, which the provisioning site holds in-memory only.)

If either check fails, the browser aborts the handoff, signals the provisioning site to tear down the VM, and surfaces the failure to the user.

### 12–13. Browser seals and transmits the bundle

```js
// libsodium-js
const plaintextJson = JSON.stringify(bundle);
const sealed = sodium.crypto_box_seal(
  sodium.from_string(plaintextJson),
  pubkey
);

await fetch(`https://${fqdn}/handoff/bundle`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/octet-stream' },
  body: sealed,
});
```

Sealed boxes use an ephemeral sender keypair generated internally; the VM doesn't know who sent the bundle, which is fine — authenticity comes from the HMAC-authenticated pubkey exchange, not the bundle itself. Integrity comes from Poly1305 inside the seal.

### 14–17. VM installs secrets and shuts down the handoff endpoint

On receiving the sealed bundle, the VM:

1. Opens it with `crypto_box_seal_open(sealed, pubkey, privkey)`. If it fails, return 400 and do nothing else (the endpoint remains open for one more attempt, or gives up after N failures — configurable, default 3).
2. Parses the JSON and writes secrets according to the server's layout (env file, systemd credentials, OS keyring, or `.godmode/settings.json` — whatever the server expects).
3. Securely wipes the privkey and the sealed bundle from memory.
4. Deletes `/etc/godmode/handoff.env` (removes `vm_token` from disk).
5. Stops the handoff HTTPS server. The `/handoff/*` endpoints cease to exist. If the VM is ever re-used, the flow cannot be repeated without a fresh cloud-init.
6. Starts the normal GodMode server.

## Bundle Format

The sealed plaintext is a JSON object:

```json
{
  "version":   1,
  "createdAt": "2026-04-17T14:22:05Z",
  "secrets": {
    "ANTHROPIC_API_KEY": "<utf-8 string>",
    "JIRA_TOKEN":        "<utf-8 string>",
    "GITHUB_PAT":        "<utf-8 string>"
  }
}
```

Binary secrets (certificates, keyfiles) go in a separate map with base64 values:

```json
{
  "version": 1,
  "secrets": { "ANTHROPIC_API_KEY": "sk-ant-..." },
  "binarySecrets": { "TLS_CERT": "<base64-bytes>" }
}
```

`version` is reserved for future format changes. `createdAt` aids debugging and lets the VM reject obviously-stale bundles (e.g., > 1 hour old).

## Residual Risks

### Provisioning-site compromise
An attacker with control of ingodmode.xyz at provision time can substitute its own pubkey into the `vm_token` attestation (since they generate `vm_token` and can re-sign a fake pubkey). They cannot affect *existing* VMs or read *stored* ciphertexts (those require the user's vault KEK, which the site never sees). Only in-flight provisions during the compromise window are exposed.

Mitigations:
- Sign provisioning-site JS with Subresource Integrity hashes.
- Consider a hardware-backed signing step: have each user's browser record a long-lived provisioning-pubkey on first use, and require future bundles to be co-signed by the browser's own device key. (Defers to a future revision.)

### Browser compromise
If an attacker controls the user's browser, they have plaintext. No protocol fixes this. Mitigations live in the browser: Content-Security-Policy, no third-party scripts on the provisioning site, SRI.

### `vm_token` exposure
If `vm_token` leaks (e.g., via logs on the provisioning site), a network attacker with TLS MITM capability could substitute a pubkey. Rules:
- `vm_token` is generated in RAM and lives in one HTTP response, one cloud-init payload, and one in-memory browser variable. Never log it.
- Cloud-init is encrypted at rest by Azure; rotate Azure resource-group credentials if you suspect exposure.
- Set a short window (e.g., 10 min) after which the VM discards `vm_token` whether or not handoff succeeded.

### Replay
Sealed boxes are not replay-protected on their own. Replay protection comes from the VM accepting only one handoff and then shutting the endpoint down. A captured sealed box cannot be replayed to the same VM (handoff is closed) or a different VM (wrong pubkey).

### Metadata leakage
The provisioning site learns *which* secrets the user is provisioning (by their names) and to which FQDN. This is acceptable; the credential *values* are the sensitive part.

## Failure Handling

| Failure | Behavior |
|---------|----------|
| Let's Encrypt issuance fails | VM retries N times, then reports back to provisioning site with error. Browser tears down VM, surfaces error. |
| HMAC verification fails in browser | Abort immediately. Tear down VM. Log incident (without `vm_token`). |
| Sealed-box open fails on VM | Return 400. After 3 failures, self-terminate (possible attack). |
| Browser loses connection mid-handoff | User retries from step 4. The old VM is torn down; a fresh `vm_token`/FQDN is issued. |
| VM doesn't shut down handoff endpoint | Monitor alerts. Manually terminate. Treat as compromised, rotate any secrets that went to that VM. |

## Operational Notes

- The handoff HTTPS server should run as a dedicated unprivileged user with CAP_NET_BIND_SERVICE for port 443 — not as root. Root is only needed to write the final secrets to their destination paths, which happens in a short privileged helper invoked post-decrypt.
- `/etc/godmode/handoff.env` must be mode 0600, owned by root. Deleted after handoff.
- The handoff service logs every step (without secret values or tokens) to journald. Logs are retained per normal Azure policy.
- There is no legitimate reason for the handoff endpoint to persist past first boot. If you find it running on an established VM, something went wrong — treat as a security incident.

## What Vault Does During All This

Nothing, after step 2. Vault served ciphertext blobs to the browser and is not involved in the handoff itself. The VM never talks to vault. This is deliberate: it means a compromised vault cannot inject a malicious bundle into a provisioning VM.
