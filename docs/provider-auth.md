# Provider authentication

`andy-cli` needs a credential for every hosted LLM provider it talks to. This document covers
where that credential can come from, how it is stored on each operating system, how to drive it
from automation, and how to rotate or recover it.

Implemented for [issue #284](https://github.com/rivoli-ai/andy-cli/issues/284).

## Commands

The same four verbs exist in every mode. On the command line:

```
andy-cli auth list                  # providers, credential status, supported login methods
andy-cli auth login <provider>      # sign in (masked prompt)
andy-cli auth status [provider]     # where each credential comes from, fully redacted
andy-cli auth logout <provider>     # remove the stored credential
```

Inside the interactive TUI the same commands are available as `/auth list`, `/auth login
<provider>`, `/auth status [provider]`, and `/auth logout <provider>`. `/auth login` opens a
modal that masks the typed value; the value never reaches the prompt line, the prompt history,
the transcript, or a saved session.

`auth login` accepts `--method api-key` (the default), `--method oauth`, or `--method
device-code`. `auth list` shows which methods each provider actually supports.

## Source precedence

A credential is resolved in this order, and the first match wins:

| Priority | Source | Persisted? | Notes |
| --- | --- | --- | --- |
| 1 | Environment variable (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, ...) | Never | Read-only. `auth login` will never copy an environment value into the credential store, and `auth logout` cannot remove one. |
| 2 | OS credential store | Yes | Written by `auth login`, removed by `auth logout`. |
| 3 | `Llm:Providers:<id>:ApiKey` in `appsettings.json` | Yes (in your config file) | Only used when neither of the above supplies a value. Prefer `${ENV_VAR}` placeholders over literals. |
| 4 | Nothing | - | The provider is reported as "not configured". |

Two consequences worth remembering:

- If a variable such as `OPENAI_API_KEY` is exported, it keeps winning even after you run
  `auth login openai`. `auth login` warns when this is the case, and `auth logout` reminds you
  which variable is still set. Unset the variable to fall through to the stored credential.
- A stored credential makes a provider "available" everywhere environment detection is used:
  startup provider auto-detection, `/model`, and the ACP model catalog all agree, because
  interactive, headless, and ACP mode resolve credentials through the same code path.

## Operating-system behaviour

| Platform | Backend | How the secret is handled |
| --- | --- | --- |
| macOS | Login Keychain, via `/usr/bin/security` | The value is piped over stdin (`add-generic-password ... -w` with no argument). It never appears in process arguments. Going through `security` for both reads and writes keeps the keychain ACL stable, so macOS does not re-prompt after an SDK update. |
| Windows | Windows Credential Manager (generic credentials), via `advapi32` | The blob is passed as an in-process buffer and zeroed before it is freed. Stored with `CRED_PERSIST_LOCAL_MACHINE`, so it does not roam. Entries appear as `andy-cli:provider:<id>`. |
| Linux | freedesktop Secret Service, via libsecret's `secret-tool` | The value is piped over stdin. Requires both `secret-tool` and a D-Bus session bus (`DBUS_SESSION_BUS_ADDRESS`). |

Each provider has exactly one entry, keyed `provider:<id>` under the service name `andy-cli`.
Keeping the API key, the OAuth access token, and the OAuth refresh token in a single record is
what makes `auth logout` atomic: one delete removes all of them, with no window in which a
refresh token outlives the access token.

### Machines with no credential service

Headless servers, minimal containers, and bare SSH sessions frequently have no credential
service at all. In that situation `andy-cli` **fails loudly rather than writing a plaintext
file**. `auth login` refuses before it prompts for a secret and prints guidance listing three
options:

1. install a credential service (on Linux, a Secret Service provider plus a session bus);
2. supply the credential through an environment variable injected by your secret manager -
   always the preferred option for unattended machines;
3. explicitly opt in to the file fallback.

Reading still degrades quietly: a machine with no credential service starts normally and simply
reports every provider as configured-by-environment or not configured.

### The file fallback (opt-in only)

```
ANDY_CREDENTIAL_STORE=file andy-cli auth login openai
```

This writes `~/.andy/credentials.json` with mode `0600` inside a `0700` directory. **The
contents are base64-encoded, not encrypted.** Anyone who can read the file - including `root` -
can read the credential. Every login through this backend prints a warning saying so. Use it
only when you have accepted that trade-off.

### `ANDY_CREDENTIAL_STORE`

| Value | Backend |
| --- | --- |
| unset / `auto` | The platform default; fails closed when none is usable. |
| `keychain`, `macos` | macOS Keychain |
| `wincred`, `windows` | Windows Credential Manager |
| `secretservice`, `libsecret`, `linux` | Linux Secret Service |
| `file` | Plaintext file fallback (see above) |
| `memory` | Process-local, discarded on exit. For sandboxes and tests. |
| `none` | Storage disabled; environment variables only. |

## OAuth

Providers can offer an OAuth login in addition to (or instead of) an API key. Two flows are
supported:

- **`--method oauth`** - authorization code with a local callback. The callback listener binds
  to `127.0.0.1` only, uses PKCE (S256) by default, and validates the `state` parameter in
  constant time before it accepts a code. A mismatched or missing `state`, a provider-reported
  error, a timeout (five minutes by default), or `Ctrl+C` all abort the login without storing
  anything.
- **`--method device-code`** - RFC 8628 device authorization, for machines with no usable
  browser. The user code is displayed; the device code is treated as a secret and never shown.

Expiring access tokens are renewed automatically during credential resolution, using the refresh
token from the same store, and the renewed record is written back to the store. If a refresh
fails, the previous token is kept and a non-secret note explains what happened; run
`auth login <provider>` again to recover.

### Enabling OAuth for a provider

OAuth endpoints are data, not code. Create `~/.andy/provider-auth.json`:

```json
{
  "providers": {
    "openai": {
      "oauth": {
        "clientId": "your-registered-client-id",
        "authorizationEndpoint": "https://provider.example/oauth/authorize",
        "tokenEndpoint": "https://provider.example/oauth/token",
        "deviceAuthorizationEndpoint": "https://provider.example/oauth/device",
        "scopes": ["api"],
        "usePkce": true,
        "callbackPort": 0,
        "callbackPath": "/andy-cli/callback"
      }
    }
  }
}
```

`callbackPort: 0` picks a free ephemeral port. Set a fixed port when the provider requires a
pre-registered redirect URI. `deviceAuthorizationEndpoint` is what enables `--method
device-code`. A malformed or incomplete entry is ignored, and the provider keeps its API-key
login.

This file must not contain client secrets; andy-cli only uses public-client flows (PKCE).

## Automation

Environment variables remain the recommended path for CI and unattended runs: they are the
highest-priority source, they are never persisted, and they need no credential service.

```
export ANTHROPIC_API_KEY="$(your-secret-manager read anthropic)"
andy-cli run --headless --config ./run.json
```

If you do need to seed a credential store non-interactively, pipe the value into `auth login` -
never pass it as an argument, because process arguments are readable by other users and are
recorded in shell history:

```
printf '%s' "$API_KEY" | andy-cli auth login openai
```

`andy-cli auth login` rejects any option that looks like it carries a credential value.

Headless runs can also point at a specific variable with `model.api_key_ref: "env:MY_VAR"` in
the run config (see `docs/headless-runtime.md`).

## Rotation

1. Obtain the new key from the provider.
2. Run `andy-cli auth login <provider>` again. The login replaces the existing record in place;
   there is no need to log out first.
3. Verify with `andy-cli auth status <provider>` - it shows the source and account label, never
   the value.
4. Revoke the old key at the provider.

For environment-variable deployments, rotate the value in your secret manager and restart the
process; nothing is cached on disk.

For OAuth, rotation is automatic while a refresh token is valid. When the refresh token itself
is revoked, resolution reports a failed refresh; run `auth login <provider>` to re-authorize.

## Recovery

| Symptom | What to do |
| --- | --- |
| `auth status` says "not configured" although you logged in | An environment variable may be unset while the store is on a different backend. Check `auth list`, which names the active backend, and confirm `ANDY_CREDENTIAL_STORE` is not pinned to `none` or `memory`. |
| macOS prompts for keychain access repeatedly | Allow the `security` helper once; if the prompt persists, run `auth logout <provider>` then `auth login <provider>` to recreate the item with a fresh ACL. |
| Linux: "No freedesktop Secret Service is reachable" | Install and start a Secret Service provider, ensure `DBUS_SESSION_BUS_ADDRESS` is exported in the session, or fall back to environment variables. |
| A token refresh keeps failing | The refresh token was revoked or expired. Run `auth login <provider>` again. |
| The file fallback store is corrupt | Delete `~/.andy/credentials.json` and log in again. No other andy-cli state depends on it. |
| You want to remove everything | `andy-cli auth logout <provider>` for each provider listed by `andy-cli auth list`, plus unset any provider environment variables in your shell profile. |

## What is never recorded

Credentials never appear in effective configuration output, saved sessions, transcripts, logs,
telemetry, process arguments, or exception messages. Status and listing output is fully
redacted: no prefix, no suffix, and no digest of a secret is ever displayed - only `****` plus
non-secret metadata (source, account label, expiry).

## Testing

Deterministic tests run against an in-memory credential store and never touch the developer's
real keychain. The tests that exercise the real OS credential service are opt-in:

```
ANDY_AUTH_REAL_STORE_TESTS=1 dotnet test --filter FullyQualifiedName~RealCredentialStoreTests
```
