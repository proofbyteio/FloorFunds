# S2a P1 spec review (SSE2)

**Reviewed:** `contract/v1.1-S2a-location-login-config.md` at `4a10792` (feat/location-login-config), md5 `6c9bb033495c72c127dfb68bcda5dada`. Checked in git and in the working tree; they match.
**Scope:** SSE2-owned parts: §8.2 tables, §8.4 login-role grants, §9 readers, §10 semantics, and the §7.1 FYI.
**Method:** read only, with `git show` and `cat`. SSE's files and branch were not changed.
**Sources:**
- LD `/workspace/cachd-ones/login-service-proposal.md` (md5 `5df55754…`, the same as the spec's snapshot).
- SI1 stand-in at `si1-fixes` `3e0b71d`: `src/Login.StandIn/Auth.cs` `LoginSession`, `SessionCap.cs` and `SignIn.cs`.
- `contract/claims.schema.json` and `contract/audit-events.schema.json` at `4a10792`.
- My BUILD-LOG TLD-S2B-01..22 and `docs/plans/S2B-ENTRA-PLAN.md`.

Severity: **High** = blocks sign-off / security gap; **Medium** = fix before S2b merges; **Low** = tidy-up; **Info**.

## 1. Findings

| # | Sev | Where | Finding | Proposed fix |
|---|---|---|---|---|
| F1 | **High** | §9 `IExternalLoginReader`, §8.4 | `FindUserAsync` returns only `Guid?`. The login role has no read of user status: there is no `auth.user` table in §8 and no grant on it. My plan's OnTokenValidated step 9 (refuse a disabled or locked user) can't be done, so a linked Entra user who is disabled could still sign in. The SI1 refresh path also revokes with `user_disabled` (OQ-12). | Return `ExternalLoginMatch(Guid UserId, Guid PartnerId, UserStatus Status, DateTimeOffset? LockedUntil)`, or add `IUserStatusReader.GetAsync(userId, ct)`. Either way, grant `cachd_login` SELECT on the user status columns and state which table holds them. Until then, fail closed: no status means refuse. |
| F2 | **High** | §8.2 `external_login` | Only `user_id` is NOT NULL. A UNIQUE constraint on nullable columns lets duplicate rows exist, because NULLs are distinct. The table has no PK, no `linked_by` (the LD needs it for invite-based linking and `auth.identity.linked`), no FK on `user_id`, and no index on `user_id`. | See §3.1: make every column NOT NULL, add a PK, CHECKs, `linked_by` and an index on `user_id`. |
| F3 | Medium | §8.2 `oidc_replay` | The only key is `key_hash`, with no `kind`, and it covers only "jti or nonce". My plan also needs the auth code and the access-token `uti` (plan step 5). Nothing in the spec forbids `UNLOGGED`, and that would lose rows after a crash and reopen the replay window. | See §4.1: PK `(kind, key_hash)`, a closed `kind` enum, a length CHECK, a logged table, `INSERT … ON CONFLICT DO NOTHING`. |
| F4 | Medium | §8.4 | **SECURITY DEFINER functions:** Postgres grants EXECUTE on functions to PUBLIC by default. "EXECUTE to `cachd_admin_api` only" holds only after an explicit `REVOKE EXECUTE … FROM PUBLIC`, and `cachd_login` would otherwise get `tenant_bound_elsewhere`. | Add `REVOKE ALL ON FUNCTION … FROM PUBLIC` for both functions. Also add `ALTER DEFAULT PRIVILEGES IN SCHEMA auth REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC`. |
| F5 | Medium | §8.4 | `GRANT USAGE ON SCHEMA auth TO cachd_login` (and to `cachd_admin_api`) is not stated, and without it every grant is useless. `audit_outbox` is still OPEN (OQ-S2a-12): if its PK is `bigserial` or identity, both roles need `USAGE` on that sequence. | State the schema USAGE grants. State "no sequences: every auth.* PK is an app-generated uuid". Add the outbox sequence grant once OQ-S2a-12 closes. |
| F6 | Medium | §8.4 | Login-role privileges are wider than needed. `oidc_replay` gets UPDATE but never needs it. `session` gets a whole-row UPDATE, so a bug could rewrite `user_id`, `amr` or `auth_time`. | `oidc_replay`: SELECT (for RETURNING), INSERT, DELETE (cleanup). `session`: SELECT, INSERT, column-level `UPDATE(last_seen_at, revoked_at, revoked_reason, interactive_used_at, handoff_used_at, cage_consumed_at, code_issued)`, DELETE (purge). |
| F7 | Medium | §8.3 | RLS is stated only for the three admin tables. For `oidc_replay` and `session`, the spec should say whether RLS is off. If someone turns it on with only the SELECT `USING (true)` policy, INSERT, UPDATE and DELETE fail. | State: RLS off on `external_login`, `user_location`, `oidc_replay` and `session` (the login service is not partner-scoped, and grants enforce RO/RW). Or add `FOR ALL` policies for `cachd_login`. |
| F8 | Medium | §9 | Error semantics are not stated. `null` means "no row"; a DB error or timeout must **throw**, not return `null`. The caller must not class a DB failure as "Entra down", which would trigger `allow_db_fallback`. | Add: "Readers throw on DB failure and on cancellation (`OperationCanceledException`). A thrown reader error fails closed with no fallback." |
| F9 | Medium | §9 namespace | `Cachd.Login.Abstractions` is an ASSUMPTION. TLD-S2B-04 says `src/Login.Entra` must not depend on the stand-in. | Put the readers in their own small project, `src/Login.Abstractions` (owned by SSE, no other dependencies), and have Login.Entra and the stand-in both reference it. |
| F10 | Medium | §8.2 `user_location` | `IsAssignedAsync` returns a bare bool, and the table has no active or ended state. The LD says "access needs an active assignment" (L316). | Interim: deleting the row is the only way to unassign. Say so, add `created_at` and `created_by`, and add an index on `location_id`. Roles and end-dating wait for LS-OQ-4. |
| F11 | Medium | Session cap base | SI1 measures the absolute cap from `AuthTime` (`SessionCap.Expired`: `now - AuthTime >= cap`), where `AuthTime` is our sign-in time. For Entra, the `auth_time` claim is Entra's time, which can be earlier. Contract 7.4 says "sign-in time + cap". | Measure the cap from `session.created_at` (our sign-in) and keep the Entra `auth_time` only for the ≤5 min hand-off and `max_age` checks. TL to confirm (it doesn't change DB-provider behaviour). |
| F12 | Low | §5.2 `oidc_authority` | `https://host[:port]` only. My fake Entra issuer (port 18093) must therefore serve https with a test certificate, or the config is rejected. I'll do https and not ask for a relaxation. | None needed in the spec. Noted for SSE2's tests. |
| F13 | Low | §9 record | `PrintMembers` keeps `SecretRef` out of `ToString()`, but `System.Text.Json` would still serialise it. `IReadOnlyList<Guid>` makes record equality compare by reference. | Add `[property: JsonIgnore]` on `SecretRef` (or a `SecretRef` wrapper type). Don't rely on equality between configs; compare by `RowVersion` (that's what my options cache does). |
| F14 | Low | §9 `FindUserAsync` | The parameter rules aren't stated. | Keep `Guid tid` and `Guid oid`: parsing makes case canonical and rejects non-GUID input before any query. Compare `issuer` **ordinally and exactly** (no trimming, no case folding, no trailing-slash fix). The caller has already required `iss == $"{oidc_authority}/{tid:D}/v2.0"` (lower-case D format). Store `issuer` exactly in that form. `providerKind` must be `Entra`; `Database` throws `ArgumentOutOfRangeException`. `ct` is required (no default), as drafted. |
| F15 | Low | §8.2 / `provider_kind` | The DB text value and the enum casing aren't mapped. | DB stores `'database'` and `'entra'` in lower case, as in the claims `login_provider` values. Map in the reader. CHECK `IN ('entra')` on `external_login`. |
| F16 | Low | TLD-S2B-21 | The spec adds `row_version` only to `auth.provider.changed`. TLD-S2B-21 says RowVersion goes on events in v1.1. | Ask TL whether `auth.login.*` events for Entra locations carry `config_row_version` in v1.1 (log under V11-ENTRA-FALLBACK). Not blocking. |
| F17 | Info | §9 `GetBySlugAsync` | The normalisation (trim, ASCII lower, regex, `null` without a query) is fine, and the same failure for unknown and malformed slugs is right. `null` covers both "no location" and "no config". That's acceptable because a location can't exist without a config row, and both cases fail closed (TLD-S2B-16). | None. |

## 2. Readers (§9): verdict

The draft is acceptable once **F1, F8, F9 and F14** are applied. Proposed shape:

```csharp
public enum UserStatus { Active, Disabled, Locked }
public sealed record ExternalLoginMatch(Guid UserId, Guid PartnerId, UserStatus Status, DateTimeOffset? LockedUntil);

public interface IExternalLoginReader
{
    // providerKind must be Entra; issuer compared ordinal-exact; tenantId = Entra tid, subject = Entra oid (never sub/email/UPN).
    // null = no link. Throws on DB failure or cancellation (caller fails closed, never falls back).
    Task<ExternalLoginMatch?> FindUserAsync(ProviderKind providerKind, string issuer, Guid tenantId, Guid subject, CancellationToken ct);
}
public interface ILocationAssignmentReader
{
    Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct); // row exists = active (interim)
}
```
The parameter names `tid`/`oid` versus `tenantId`/`subject` follow whichever column names win (§5).

## 3. Draft tables

### 3.1 `auth.external_login` (proposed; LD names, see §5)
```sql
CREATE TABLE auth.external_login (
  external_login_id uuid        PRIMARY KEY,                          -- app-generated
  user_id           uuid        NOT NULL,                             -- FK auth.user(user_id) when that table exists
  provider_kind     text        NOT NULL CHECK (provider_kind IN ('entra')),
  issuer            text        NOT NULL CHECK (issuer ~ '^https://[^/?#]+/[0-9a-f-]{36}/v2\.0$'),
  tenant_id         uuid        NOT NULL,                             -- Entra tid
  subject           uuid        NOT NULL,                             -- Entra oid (NEVER sub, email, UPN)
  linked_at         timestamptz NOT NULL DEFAULT now(),
  linked_by         uuid        NULL,                                 -- admin user id; NULL only for seed/bootstrap
  CONSTRAINT external_login_key UNIQUE (provider_kind, issuer, tenant_id, subject),
  CONSTRAINT external_login_issuer_tid CHECK (position(tenant_id::text in issuer) > 0)
);
CREATE INDEX external_login_user_ix ON auth.external_login (user_id);
```
Changing a location's `oidc_authority` host orphans its links. That fails closed, which is acceptable; documented as intended.

### 3.2 `auth.user_location` (the spec's `location_assignment`)
```sql
CREATE TABLE auth.user_location (
  user_id     uuid        NOT NULL,                                  -- FK auth.user when it exists
  location_id uuid        NOT NULL REFERENCES auth.location ON DELETE CASCADE,
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  uuid        NULL,
  PRIMARY KEY (user_id, location_id)
);
CREATE INDEX user_location_location_ix ON auth.user_location (location_id);
-- roles: deferred to LS-OQ-4 (LD §6.1 option A L316).
```

## 4. Proposed SSE2 tables

### 4.1 `auth.oidc_replay`
```sql
CREATE TABLE auth.oidc_replay (                 -- LOGGED (never UNLOGGED: a crash would reopen the replay window)
  kind       text        NOT NULL CHECK (kind IN ('nonce','code','uti')),
  key_hash   bytea       NOT NULL CHECK (octet_length(key_hash) = 32),  -- SHA-256(kind || 0x00 || raw value); raw never stored
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  CONSTRAINT oidc_replay_pk PRIMARY KEY (kind, key_hash),
  CONSTRAINT oidc_replay_ttl CHECK (expires_at > created_at AND expires_at <= created_at + interval '24 hours')
);
CREATE INDEX oidc_replay_expires_ix ON auth.oidc_replay (expires_at);
```
- **Use:** `INSERT … ON CONFLICT (kind, key_hash) DO NOTHING RETURNING 1`. If no row comes back, it's a replay: refuse and fail closed. If the insert errors, also refuse.
- **What is hashed:**
  - nonce: the ID-token `nonce`.
  - code: the authorization code, before redemption.
  - uti: the Entra token's `uti`.
- **expires_at:** the token's `exp` (or the nonce or code lifetime) + 5 min clock skew.
- **Cleanup:** batched `DELETE FROM auth.oidc_replay WHERE ctid IN (SELECT ctid FROM auth.oidc_replay WHERE expires_at < now() - interval '5 minutes' LIMIT 1000)`, run by the login service on a timer. Rows that have expired but haven't been purged still block, which is harmless.

### 4.2 `auth.session` (follows SI1 `LoginSession` and `SessionCap`)
```sql
CREATE TABLE auth.session (
  session_id          uuid        PRIMARY KEY,                       -- = sid; non-secret id used in audit
  cookie_key_hash     bytea       NOT NULL UNIQUE CHECK (octet_length(cookie_key_hash) = 32), -- SHA-256 of the random cookie secret; raw never stored
  user_id             uuid        NOT NULL,
  partner_id          uuid        NULL,                              -- denormalised for the per-partner cap (SessionCap.PartnerOf)
  location_id         uuid        NULL REFERENCES auth.location,     -- NULL only for /admin sessions
  admin_type          text        NULL CHECK (admin_type IN ('super','partner')),
  session_kind        text        NOT NULL DEFAULT 'normal' CHECK (session_kind IN ('normal','recovery_reenroll')),
  login_provider      text        NOT NULL CHECK (login_provider IN ('database','entra')),
  entra_tenant_id     uuid        NULL,                              -- tid; needed for Entra end-session
  config_row_version  bigint      NULL,                              -- location_login_config.row_version at sign-in (TLD-S2B-21)
  amr                 text[]      NOT NULL CHECK (cardinality(amr) >= 1 AND amr <@ ARRAY['pwd','otp','mfa','hwk','swk','pin','fpt','face','rba','wia','sms','user','kba','mca']::text[]),
  auth_time           timestamptz NOT NULL,                          -- token auth_time (Entra: its auth_time)
  created_at          timestamptz NOT NULL DEFAULT now(),            -- our sign-in; absolute-cap base (F11)
  last_seen_at        timestamptz NOT NULL,                          -- 15-min sliding idle
  interactive         boolean     NOT NULL DEFAULT true,
  via_return          boolean     NOT NULL DEFAULT false,
  code_issued         boolean     NOT NULL DEFAULT false,
  interactive_used_at timestamptz NULL,                              -- A32 one interactive code
  handoff_used_at     timestamptz NULL,                              -- rule 3
  cage_consumed_at    timestamptz NULL,                              -- rule 6
  revoked_at          timestamptz NULL,
  revoked_reason      text        NULL CHECK (revoked_reason ~ '^[a-z_]+$'),
  CONSTRAINT session_revoked_pair  CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL)),
  CONSTRAINT session_scope         CHECK (admin_type IS NOT NULL OR (location_id IS NOT NULL AND partner_id IS NOT NULL)),
  CONSTRAINT session_partner_admin CHECK (admin_type IS DISTINCT FROM 'partner' OR partner_id IS NOT NULL),
  CONSTRAINT session_entra         CHECK (login_provider <> 'entra' OR (location_id IS NOT NULL AND entra_tenant_id IS NOT NULL
                                          AND admin_type IS NULL AND session_kind = 'normal' AND config_row_version IS NOT NULL)),
  CONSTRAINT session_db            CHECK (login_provider <> 'database' OR entra_tenant_id IS NULL),
  CONSTRAINT session_recovery_amr  CHECK (session_kind <> 'recovery_reenroll' OR NOT ('mfa' = ANY (amr)))
);
CREATE INDEX session_user_live_ix ON auth.session (user_id) WHERE revoked_at IS NULL;
CREATE INDEX session_last_seen_ix ON auth.session (last_seen_at);
```
- **Idle and cap:** these are computed, not stored.
  - Idle: `now - last_seen_at > 15 min`.
  - Cap: `now - created_at >= effective cap(partner_id)`. The cap is read at each check (SI1 A62), clamped to (15 min, 24 h] (TLD-SI1-02), with the OQ-40 range still OPEN.
  - There's deliberately no `absolute_expires_at` snapshot until OQ-40 says whether a cap change affects running sessions.
- **What is never stored:** no tokens, no Entra ID or access tokens, no username (SI1 kept one for display; it's PII), and no recovery `PendingSecret` (that stays out of this table).
- **Purge:** delete rows where `revoked_at` or `last_seen_at` is older than 24 h + skew.

**DDL check:** I ran §3–§4 on a throwaway PG 17 cluster (`initdb` under /tmp, deleted afterwards; the shared DB and `ff_sse2` were not touched). Results:
- All tables and indexes are created.
- A repeated `nonce` insert returns 0 rows. The same hash under `code` is accepted.
- Batched cleanup deletes the expired rows.
- `session_entra` rejects an Entra session with no tid.
- The `amr` CHECK rejects `wiaormfa`.
- `external_login_issuer_tid` rejects an issuer and tid that don't match.

## 5. Naming conflicts

**OQ-S2a-10, external_login columns**
- **LD:** §6 L304 has `tenant_id, subject, linked_at, linked_by`.
- **Spec:** §8.2 L426 uses `tid, oid, created_at`, as SSE rendered TL's 7:27 note. I couldn't find the note itself as a file.
- **Recommendation: use the LD names, with the mapping `tenant_id ← tid` and `subject ← oid`.**
- **Rulings:**
  - **TLD-S2B-06** fixes the *key semantics* (link on issuer + tid + oid; never email, UPN or preferred_username). It doesn't rename the columns.
  - An earlier **TL Decision, OQ-23 (LD §16.19.4 L1575, Mon Nov 2 8:11 PM PT)** already names `auth.external_login.tenant_id`.
  - `linked_by` is needed for invite-based linking.
- **Guard against `subject` being misread as `sub`:** add a column comment, keep the type `uuid`, and add a fixture test where `sub` ≠ `oid`.
- If TL meant 7:27 as a deliberate rename, TL should say so and SA should update LD L304.

**OQ-S2a-11, `location_assignment` vs `user_location`**
- **Recommendation: `auth.user_location`**, per LD §6.1 option A L316.
- **Rulings:**
  - Option A is the TL Rec, and LS-OQ-4 is still open, so roles are deferred.
  - I found no TL ruling that renames it. TL's "location-assignment" wording names the *check*, not the table.
- The reader can stay `ILocationAssignmentReader`.

## 6. SSE's §7.1 FYI: impact on sse2/tests-docs

I ran `git grep -E 'approval_id|change_requested|provider\.changed'` over the whole `sse2/tests-docs` tree (tests, fixtures, tools, docs). It found **no matches**, so nothing depends on `approval_id`, `auth.provider.change_requested` or the open `details`.
- `ContractArtifactTests.CS06` checks event names, and neither removed name is in its list.
- `PreFreezeSchemaTests` CAP03/CAP04 build `auth.config.change_denied` with `target.type = config`. That hits the unchanged v1 `oneOf` branch, so it's not affected. SSE's run (285 tests, 0 failures) agrees.

## 7. Sign-off

**Conditional.** I sign off on §8.2, §8.4 (login role) and §9 once F1–F2 (High) are fixed and F3–F11 are accepted or answered. Open for TL: F11 (cap base), F16 (row_version on login events), and confirmation of the two naming recommendations in §5.
