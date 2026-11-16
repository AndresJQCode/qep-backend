# Roles personalizados en miembros — plan (backend y frontend)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** que `roles[]` de `/authorization/catalog` traiga los roles personalizados del tenant, y que la pantalla de roles
recargue el catálogo al crear, editar o borrar uno.

**Spec:** `docs/superpowers/specs/2026-11-16-roles-custom-en-miembros-design.md`

## Global Constraints

- Worktrees `qep-*-worktrees/roles-custom-miembros`, rama `feature/roles-custom-miembros`.
- No correr pruebas (owner): RED = build o prueba escrita contra el comportamiento de hoy; GREEN = build sin errores.
- Merge sólo después de `fix/roles-editor` y con aprobación del owner.

## Review Focus

1. Un tenant con un custom cuya clave choca con la de un rol de sistema: `TenantRoleCatalog` ya deja una sola definición por clave.
2. El tenant operador: `operator.*` sólo se ofrece ahí (lo resuelve `RoleAssignability`, sin cambio).
3. Tenant del stub sin fila en `tenancy.tenants`: `ListRolesAsync` funciona igual (sólo los de sistema si no hay custom).

---

### Task 1 (backend): el catálogo lista los custom

**Files:** `src/Api/AuthorizationCatalogEndpoints.cs` (`GetCatalogAsync`), prueba en
`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`.

- [ ] Prueba `TheCatalogListsTheTenantCustomRoles`: tenant registrado; `POST …/authorization/roles` con
  `{ key: "payment-verifier", displayName: "Verificador de pagos", permissions: ["customers.customer.read"] }` y otro
  `{ key: "pos-only", displayName: "Solo caja", permissions: ["pos.sale.read"] }` (stub con
  `advisorship.read,advisorship.roles.manage`); el catálogo trae `payment-verifier` con `DisplayName == "Verificador de pagos"`
  y `Assignable == true`, y `pos-only` con `Assignable == false`. Suma `DisplayName` a `CatalogRoleDto`.
- [ ] RED: compila; hoy falla porque `roles[]` no trae custom.
- [ ] `GetCatalogAsync` inyecta `ITenantRoleCatalog` y mapea `await tenantRoleCatalog.ListRolesAsync(tenantId, ct)` en lugar de
  `roleCatalog.ListRoles()`; mismos campos (`TenantRoleDefinition` tiene `Role`, `DisplayName`, `Description`, `Category`,
  `RiskLevel`, `Permissions`).
- [ ] GREEN: `dotnet build Backend.slnx` 0 errores. Commit `feat(authorization): list the tenant custom roles in the catalog`.

### Task 2 (frontend): recargar el catálogo al cambiar un rol

**Files:** `src/features/roles/use-roles.ts` (`reload`), prueba en `src/features/roles/use-roles.test.tsx` si existe en
`develop`, si no nueva.

- [ ] Prueba: tras crear un rol, `queryClient.invalidateQueries` recibe `authorizationCatalogQueryKey(tenantId)`.
- [ ] RED: `tsc -b` compila; hoy no se invalida.
- [ ] `reload` invalida también `authorizationCatalogQueryKey(tenantId!)`.
- [ ] GREEN: `bunx tsc -b` 0 y lint limpio. Commit `feat(roles): refresh the authorization catalog when roles change`.
