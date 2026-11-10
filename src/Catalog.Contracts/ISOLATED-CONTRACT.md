# Isolated catalog contract and in-memory doubles

Status: implemented for independent review. This is not acceptance, not a database adapter, not a host, and not runtime or relay proof.

The Tech Lead accepted separated commands, closed DTOs, server-derived actors, ABSENT versions, the UNCONFIGURED graph default, atomic idempotent replay, safe audit allowlists, and the split between double proof and runtime proof. That acceptance covers this slice only. `TL-CORRECTIONS` overrides the November 8 catalog proposal wherever they differ.

## What this double does

- Twenty catalog families, closed wire names, and separate create, claim-edit, entry-activation, partner-activation, and location-activation commands.
- Actor, tenant, correlation, and idempotency come from the server call. Command bodies that carry actor, owner, admin type, or delegation fields are rejected.
- Fee create is names only: `code`, `display_name`, and optional `description`. `economic_role` and `classification` are unknown fields. No dummy classification is stored.
- Release, ID-document, and currency creates stay unavailable. Other create DTOs stay unavailable because their required fields are not closed.
- Entry version starts at 1. A missing switch is ABSENT, not active and not version 0. Switch inserts are specified on `ScopeVersionTable` and are not used by activation commands.
- The graph defaults to UNCONFIGURED. Every Off writes nothing. Graph-dependent restore writes nothing. A fixture graph can be described in tests and does not authorize a write.
- Claim entry On stays unavailable pending PQ-272 for every actor. The double does not grant it to a Partner Admin and does not assert an SA-only rule.
- An SA partner id is syntax-checked only. It is not a membership or existence check. A Partner Admin cannot retarget the command at another partner.
- Idempotent replay of a committed double result returns the same ids and audit intent. A different body with the same key writes nothing. A rolled-back attempt is not replayed as success.
- Audit intents use an allowlist, `emitted=false`, and `double_recorded_not_durable`. `1.2.0-catalog-unapproved` is rejected and is not emitted. `DURABLY_ACCEPTED` is not claimed.
- Hidden and missing targets share one not-found result. Visibility is decided before a version comparison.

## Proof boundary

`dotnet run --project src/Catalog.Contracts.Checks/Catalog.Contracts.Checks.csproj`

Passing checks show the in-memory double only. They do not show row-level security, SQL locking, outbox atomicity, relay delivery, partner-registry existence, or a deployed write gate.

## Left unresolved

- PQ-272 claim reactivation. The task names this id. The Tech Lead note requires the policy decision and does not itself number it.
- PQ-263.1 directed graph, fan-out, and cycles. No cause engine and no guessed edges.
- PQ-271 private-master location Off preservation.
- PQ-252.SA2 SA private metadata edit stays fail-closed. DEC-195 lets a resolved Super Admin read partner-private non-claim rows.
- PQ-252.SA3 private partner-switch representation.
- PQ-252.SA6 cross-partner private code reuse. Global and one private row may share a code. Further collisions write nothing.
- PQ-252.SA1 claim six-field list. The double enforces that proposed allowlist and does not record owner approval.
- PQ-252.SA10 read audience and actor disclosure. History projections omit the actor user id.
- Create fields for every family except names-only fees, including release, ID document, and currency.
- Idempotency retention, cascade caps, cursor pagination, and reason retention.
- PQ-056 and PQ-057 delivery and local fail-closed audit approval.
- The forms Super Admin audit gap: `open_not_fixed_not_waived`. Source `contract/audit-events.schema.json` and `contract/errors.md` stay frozen.
- Partner registry and location membership. Neither is verified here.
- Independent SSE2 review. This slice does not accept itself.

No live route, database session, migration, UI write, source-schema edit, or runtime graph configuration is included.
