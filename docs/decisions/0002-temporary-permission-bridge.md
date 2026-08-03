# 0002 — Temporary permission bridge for external tokens

> **Status:** Superseded (permission derivation now governed by the Authorization
> capability; identity resolution and membership validation retained)
> **Date:** 2026-07-05
> **Related:** `0001-development-auth-stub.md`, ADR 0015, ADR 0016, ADR 0017

## Update (2026-07-05)

Step 3 below (the hardcoded role→permission mapping in
`ExternalClaimsTransformation`) has been **replaced** by the Authorization
capability (`docs/03-capabilities/authorization/README.md`,
`docs/11-capability-deep-dives/capability-contracts.md`). Permissions are now
resolved by `IAuthorizationService.ResolvePermissionsAsync` from a code-versioned,
module-owned role catalog (deny-by-default). Steps 1–2 (provider `sub` → user id,
active-membership validation) remain as designed. Still deferred to a later
Authorization slice: `DirectGrant`, contextual `Policy` (OD-0016), custom/DB roles,
global platform roles, and grant-change cache invalidation.

The original context is kept below for history.

## Context

The API is a resource server: it validates an external provider (Google) JWT and
does not issue its own token (implementation-baseline). The provider token carries
`sub` (provider subject), `email`, `email_verified` — but no internal user id, no
active tenant, and no permissions.

Tenant-aware endpoints need an internal `IExecutionContext` with:

- `SubjectId` — the internal QEP user id;
- `TenantId` — the active tenant;
- `HasPermission(...)` — the caller's permissions in that tenant.

The Authorization capability (the real permission model: catalog, role→permission
mapping, policies) is **not built yet** and several of its decisions are still open.

## Decision

`ExternalClaimsTransformation` (Bootstrapper) enriches an externally-authenticated
principal on each request:

1. **Identity resolution (permanent).** Resolves the provider `sub` to the internal
   user id via the Identity provider-link (`IProviderIdentityResolver`) and adds a
   `qep_sub` claim. `HttpExecutionContext.SubjectId` reads `qep_sub` for external
   tokens (and `sub` for the dev stub).
2. **Active tenant (permanent).** Reads the `X-Tenant-Id` header as an active-tenant
   *signal* and validates it against a live **active membership**
   (`IMembershipDirectory`). Only then are `tenant_id` and permission claims added.
   A tenant with no active membership yields no access.
3. **Permission derivation (TEMPORARY — this document's debt).** Coarsely maps
   membership role references to permissions:
   - owner/admin → `tenancy.settings.read` + `tenancy.settings.update` +
     `tenancy.membership.invite`;
   - any other active member → `tenancy.settings.read`.

The development stub principal is left untouched (identified by its
`Development` authentication type).

## Why this is temporary

Step 3 is a stopgap so the post-login application is navigable under real Google
tokens before the Authorization capability exists. It is **not** the permission
model: it hardcodes a two-tier mapping and a fixed permission set.

## Exit criteria

Replace step 3 when the Authorization capability defines:

- the permission catalog and role definitions;
- the authoritative role→permission resolution (per tenant context);
- how membership role references bind to Authorization roles (OD-0102 direction).

Steps 1 and 2 (identity resolution and active-membership validation) are expected
to remain.
