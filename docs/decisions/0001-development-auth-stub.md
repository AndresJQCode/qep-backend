# ADR 0001 — Development authentication is a stub (production blocker)

- Status: Accepted (temporary) — **must be replaced before any non-dev deployment**
- Date: 2026-07-05
- Scope: qep-backend (+ qep-frontend guard gap)
- Severity: HIGH — security risk if it reaches a shared/staging/production environment

## Context

The Tenant Settings vertical slice (Iteración 15) was built before Identity
(OIDC, baseline order step #3) and Authorization (policy evaluator, step #4).
To validate the full chain — route → endpoint → authorization → command →
domain → PostgreSQL → audit → outbox → E2E — without blocking on real identity,
authentication is served by a **stub**:

- `src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs`
  reads the `X-Subject-Id` and `X-Tenant-Id` request headers, trusts them
  blindly, and **always grants both `tenancy.settings.read` and
  `tenancy.settings.update`**.
- The frontend injects those headers automatically in DEV mode
  (`qep-frontend/src/lib/api.ts`), so there is no login screen.

The backend authorization plumbing is real (`RequireAuthorization(permission)`
on every endpoint, per-tenant guard `executionContext.TenantId != tenantId`);
only the identity/permission source is fake.

## Decision

Ship the slice with the development auth stub, gated to the Development
environment, and record it here as an explicit production blocker rather than
silent debt.

## Risks if this reaches production

- **Total authentication bypass**: anyone can impersonate any subject/tenant by
  setting two headers. No token, no signature, no expiry.
- **Privilege escalation**: permissions are self-asserted. The handler derives
  them from the caller-controlled `X-Permissions` header (defaulting to all
  tenancy.settings permissions when absent), so a caller grants itself whatever
  it wants. This is only acceptable because it is gated to Development.
- **No real tenant isolation at the identity layer**: the per-tenant guard
  trusts the `X-Tenant-Id` header, which is attacker-controlled under the stub.

## Required before production (definition of done to close this ADR)

1. Replace `DevelopmentAuthenticationHandler` with real OIDC authentication
   (baseline step #3) and a policy/permission evaluator (step #4). Roles must
   produce granular permissions (read without update must be possible).
2. Restrict any header-based dev auth to `IsDevelopment()` only, and fail closed
   in every other environment (no fallback that grants access).
3. Add the frontend route guard the baseline requires: the loader for
   `/tenants/$tenantId/settings` must enforce `tenancy.settings.read`
   (`qep-frontend/src/routes/tenants.$tenantId.settings.tsx` currently only calls
   `ensureQueryData`; enforcement is server-side 403 only). **Still open.**
4. ~~Add integration tests for acceptance #2 (member with read but not update →
   GET 200, PATCH 403).~~ **Done** — permissions are now derived from the
   `X-Permissions` header so a read-only subject is expressible, and
   `MemberWithReadButWithoutUpdateCanReadButNotUpdate` covers it. The header
   source remains a stub; item 1 still supersedes it.

## Consequences

- This ADR is a gate: CI/release checklists should block promotion beyond
  Development while the stub handler is the active authentication scheme.
- Superseding ADR expected when Identity/Authorization lands.
