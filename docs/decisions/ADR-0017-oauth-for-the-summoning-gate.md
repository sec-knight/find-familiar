# ADR-0017: OAuth for the Summoning Gate

- Status: Proposed
- Date: 2026-08-07
- Supersedes: none
- Amends: ADR-0016 (the Summoning Gate), specifically its decision to authenticate with a static
  bearer token and its note that OAuth would be reconsidered given a second client or any need to
  grant partial access
- Related: ADR-0006 (provider-neutral runner bridge), ADR-0016 (interchangeable bodies)

## Context

Sprint 14 opened the gate with a static bearer token: the user generated a secret, pasted it into the
client, and the request filter compared it in fixed time. For a terminal on this machine that remains
the right shape, and it still works.

It is not what a client speaking the MCP authorization specification expects. That specification
(2025-06-18) requires the resource server to publish protected resource metadata naming an
authorization server (RFC 9728), the authorization server to publish its own metadata (RFC 8414), the
client to obtain a token through an OAuth 2.1 authorization code flow with PKCE, and the resource to
validate that the token was issued for *it* as audience (RFC 8707). ChatGPT's custom connector flow
implements the client half of exactly that, obtaining a client identifier either by dynamic client
registration (RFC 7591) or by client ID metadata documents.

So the constraint is not "OAuth is better." It is that one specific body the Familiar is meant to
inhabit speaks a protocol this server did not.

The pressure this creates is toward building an identity system. That would be the wrong answer to
the question actually asked. This deployment has one user, one client, one resource and one scope,
and it is restarted by a systemd unit whenever a build lands.

## Decision

### 1. Find Familiar is its own authorization server, and a very small one

The resource server and the authorization server are the same process. There is no external identity
provider, no user table, no client table and no token table.

Five endpoints, and no sixth:

| Endpoint | Purpose |
|---|---|
| `/.well-known/oauth-protected-resource[/mcp]` | RFC 9728 — names the resource and its authorization server |
| `/.well-known/oauth-authorization-server[/mcp]` | RFC 8414 — names the endpoints and the supported methods |
| `POST /oauth/register` | RFC 7591 — dynamic client registration |
| `GET`/`POST /oauth/authorize` | consent, and the authorization code |
| `POST /oauth/token` | authorization code and refresh token grants |

Both well-known documents are served at the bare path and at the path-inserted form, because clients
in the wild try one, the other, or both, and the document is identical either way.

### 2. The owner authenticates with the credential they already have

The consent screen asks for `FamiliarGateway__Token` and compares it in fixed time over SHA-256
digests — the same primitive, and the same secret, the request filter uses.

This is the decision most likely to be questioned, so the reasoning is worth stating. The alternative
is a user store: a table, a password hash, a reset path, a session cookie, and a login page. All of
it would exist to authenticate one person who already holds a 64-character secret that grants exactly
the access being requested. Adding a second way to prove the same thing would widen the attack
surface without narrowing anything, and would create the failure mode where the password is weaker
than the token it guards.

What this does mean: the consent screen is public, and the token is what stands behind it. It
therefore discloses nothing — no project, no count, no identity beyond the Familiar's configured
name — and its refusal is as silent about the credential as the request filter's is.

### 3. Every artifact is signed, and nothing is stored

Client identifiers, in-flight authorization requests, authorization codes, access tokens and refresh
tokens are all a small JSON payload with an HMAC-SHA256 over it. The key is derived from the gateway
token with HKDF, using the artifact's purpose as the info label: five purposes, five keys, one
secret.

Three properties fall out of this rather than needing to be built:

- **A restart forgets nothing.** There is no pairing to lose when systemd restarts the unit after a
  build, which on this deployment is often.
- **Rotating the gateway token revokes everything.** The signing key is derived from it, so changing
  one line in the secrets file invalidates every token this server ever issued. That is what rotating
  a credential ought to mean, and here it needs no revocation list.
- **An artifact of one kind cannot be spent as another.** A code presented as an access token fails
  at the signature, not at a check somebody had to remember to write.

### 4. What signing cannot do, and the one piece of state that closes it

A signature proves this server issued a value. It cannot prove nobody has spent it. OAuth 2.1
requires an authorization code be redeemable once and requires refresh tokens issued to public
clients be rotated — both statements about history.

`FamiliarOAuthReplayGuard` is an in-memory set of spent identifiers. It is not persisted because
every entry is worthless once the artifact it names expires, which for a code is sixty seconds.

**The cost, stated plainly:** a restart forgets which refresh tokens were already rotated, so a
refresh token captured before a restart could be redeemed once after it. That requires an attacker
who already holds a refresh token — at which point they already hold read access — and closing it
would mean persisting token state for a single-user deployment. Reversed by: a second user, or any
tool that writes.

A code is spent *before* its PKCE verifier is checked, so a failed redemption burns it. A code that
survived a wrong verifier would be a sixty-second window to guess the verifier in.

### 5. Both credentials are accepted, and they are not equals

The static token and an OAuth access token both reach the gate. The static token is the deployment's
own key: it does not expire and it is how a terminal on this machine gets in. An access token is
short-lived, bound to this server's canonical resource URI as its audience, and revoked wholesale
when the static token rotates.

Adding OAuth was meant to add a way in, not replace one.

### 6. What is deliberately not implemented

No `plain` PKCE. No implicit, password, or client credentials grant. No OIDC identity claims. No
client ID metadata document resolution — that would mean this server making outbound HTTPS requests
to a URL a client chose, and dynamic registration already covers the case. No token introspection or
revocation endpoint: with tokens this short-lived and a rotation that revokes everything, neither
would be used.

Each omission is a surface not defended for a capability not used.

### 7. Public exposure stays path-scoped

Sprint 14 exposed `/mcp` alone through a path-scoped Tailscale Funnel. This adds the five OAuth
routes and nothing else, each as its own mount. `/Familiar`, `/api/runner`, `/api/gateway/*`, the
Demiplane and the application root remain unreachable from the public internet — answered by
tailscaled before they reach Kestrel.

The REST adapter over the gateway is deliberately *not* published. It is the surface that makes the
gateway verifiable with `curl` from the tailnet, and it has no reason to be reachable from outside.

## Consequences

- ChatGPT can connect as a standards-compliant MCP client, discovering everything it needs from the
  resource URL alone.
- The user's OAuth session survives restarts and expires on its own; the static token still works for
  local verification.
- One authorization decision is now made by a person reading a consent screen rather than by pasting
  a secret into a vendor's form — the secret no longer leaves this machine to reach ChatGPT.
- This server now has public unauthenticated routes for the first time. They serve fixed protocol
  documents and a consent form, hold no Familiar data, and are tested for it.
- Read-only is unchanged. An OAuth token grants the same four read tools and nothing else.
