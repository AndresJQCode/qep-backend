# Roles asignables según los módulos del tenant — plan del backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** que un rol que no concede ningún permiso ofrecido en el tenant (Cajero sin `pos`) no se pueda agregar a un miembro ni a una invitación, y que el catálogo lo diga con `assignable`.

**Architecture:** una función pura en `Modules.Authorization.Application` decide si un rol es asignable. La usan el endpoint del catálogo (`assignable`) y `RoleReferenceValidator`, que pasa a responder tres estados. `UpdateMemberRoles` valida solo los roles agregados; `InviteMember`, todos.

**Tech Stack:** .NET 10, xUnit v3, Testcontainers (integración de Tenancy y Pos).

**Spec:** `docs/superpowers/specs/2026-11-15-roles-asignables-design.md`

## Global Constraints

- Worktree: `qep-backend-worktrees/roles-asignables`, rama `feature/roles-asignables`. Nada en el checkout de `develop` (tiene cambios de otra sesión en archivos de roles).
- **No correr pruebas** (owner, 2026-11-15): se escriben y no se ejecutan. RED = `dotnet build` que falla por compilación, o la prueba escrita contra el comportamiento de hoy. GREEN = `dotnet build` con 0 errores. El owner corre las pruebas al final.
- Código de error: `tenancy.membership.role_unavailable` (422, `TenantDomainException`). Orden de comprobación: `role_unknown` primero, después `role_unavailable`.
- Con el tenant ausente de `tenancy.tenants` (`ITenantModules.FindAsync` → `null`), todo rol es asignable.
- `RoleCatalogItemResponse.Assignable` va al final del record (cambio aditivo).
- Comentarios en español, tuteando. Commits en conventional commits, sin atribución de IA.

## Review Focus

1. Un miembro que ya es `cashier` sin `pos` y al que se le cambia otro rol: el `PUT` reenvía `cashier` y tiene que dar 200. Lo cubre `ARoleTheMemberAlreadyHoldsIsKeptWithoutItsModule` (Task 3).
2. Reinvitación (`existing.Reinvite`) con `cashier` sin `pos`: tiene que dar 422, igual que el alta. Valida todos los roles **antes** de la rama alta/reinvitación. Lo cubre `InvitingWithAnUnavailableRoleIs422` (Task 3), porque la validación corre antes de bifurcar.
3. Un rol personalizado con solo permisos `pos.*`: no asignable. Lo cubre la unitaria `ARoleWithOnlyPosPermissionsIsNotAssignableWithoutPos` (Task 1), y el validador resuelve los permisos con `ITenantRoleCatalog`, no con `IRoleCatalog`.
4. Pruebas de Pos que invitan un cajero sin prender `pos` antes: se pondrían en 422. `InviteCashierAsync` prende `pos` primero (Task 3).
5. Mayúsculas o espacios en la clave del rol: el handler ya hace `Trim()`. La comparación con `membership.Roles` es `StringComparer.Ordinal`, la misma que usa hoy el handler.

---

### Task 1: `RoleAssignability`

**Files:**
- Create: `src/Modules/Authorization/Modules.Authorization.Application/RoleAssignability.cs`
- Test: `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleAssignabilityTests.cs`

**Interfaces:**
- Produces: `public static class RoleAssignability` con
  `public static IReadOnlyCollection<string> OfferedPermissions(IEnumerable<string> rolePermissions, TenantModuleSet? modules, bool isOperatorTenant, ModuleEntitlementMask mask)`
  (los permisos del rol que el tenant ofrece: `modules is null || mask.Allows(p, modules)`, y además `isOperatorTenant || !OperatorPermissionFilter.IsOperatorPermission(p)`)
  y `public static bool IsAssignable(...)` con los mismos parámetros, que devuelve `OfferedPermissions(...).Count > 0`.

- [ ] **Step 1: Escribir las pruebas** (construye la máscara como `ModuleEntitlementMaskTests`, con `PermissionDefinition` de `pos.sale.read` → `[TenantModuleKeys.Pos]`, `tenancy.settings.read` → `[]` y `operator.usage.read` → `[]`):
  - `ARoleWithOnlyPosPermissionsIsNotAssignableWithoutPos`: `IsAssignable(["pos.sale.read"], TenantModuleSet.FromStored([]), false, mask)` es `false`.
  - `ARoleWithOnlyPosPermissionsIsAssignableWithPos`: con `FromStored([TenantModuleKeys.Pos])` es `true`. Si `pos` depende de `catalog` y `companies` para ser efectivo, se incluyen.
  - `AMixedRoleIsAssignableWithoutPos`: `["pos.sale.read", "tenancy.settings.read"]` es `true`, y `OfferedPermissions` es `["tenancy.settings.read"]`.
  - `AnOperatorOnlyRoleIsNotAssignableOutsideTheOperatorTenant`: `["operator.usage.read"]` con `isOperatorTenant: false` es `false`, y con `true` es `true`.
  - `EveryRoleIsAssignableWhenTheTenantIsUnknown`: `["pos.sale.read"]` con `modules: null` es `true`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Authorization/Modules.Authorization.UnitTests` falla con `CS0103: The name 'RoleAssignability' does not exist`.
- [ ] **Step 3: Implementar** `RoleAssignability` en el archivo indicado.
- [ ] **Step 4: GREEN.** El mismo build, con 0 errores.
- [ ] **Step 5: Commit** `feat(authorization): decide whether a role is assignable in a tenant`.

### Task 2: `assignable` en el catálogo

**Files:**
- Modify: `src/Api/AuthorizationCatalogEndpoints.cs:86-149`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs`

**Interfaces:**
- Consumes: `RoleAssignability.OfferedPermissions` (Task 1).
- Produces: `RoleCatalogItemResponse(..., IReadOnlyCollection<string> Permissions, bool Assignable)`.

- [ ] **Step 1: Escribir la prueba** `TheCatalogMarksTheCashierUnassignableWithoutPos`. Registra un tenant real (como `TenantModulesApiTests.RegisterAsync`: el signup no concede `pos`), lee el catálogo con `advisorship.read` y afirma que `cashier` trae `Assignable == false` y `Permissions` vacío, y que `admin` trae `Assignable == true`. Agrega `bool Assignable` a `CatalogRolePayload`.
- [ ] **Step 2: Escribir la prueba** `TheCatalogMarksTheCashierAssignableWithPos`: el mismo tenant con `pos` prendido (el `INSERT … ON CONFLICT` de `PosApiHarness.EnablePosAsync`, copiado como helper privado sobre `factory.ConnectionString`) y `cashier` con `Assignable == true`.
- [ ] **Step 3: RED.** `dotnet build tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests` compila (el payload es de la prueba): queda documentado que la prueba falla contra el código de hoy porque el campo no viaja y se deserializa `false` también en `admin`.
- [ ] **Step 4: Implementar.** En `GetCatalogAsync` reemplaza el filtro `OfferedInRole` de `roles[]` por `RoleAssignability.OfferedPermissions(role.Permissions, modules, isOperator, entitlementMask)`, y pon `Assignable = permisos.Count > 0`. La lista `permissions[]` (casillas) no cambia.
- [ ] **Step 5: GREEN.** `dotnet build` de la solución con 0 errores.
- [ ] **Step 6: Commit** `feat(authorization): mark unassignable roles in the tenant catalog`.

### Task 3: validar al asignar y al invitar

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/IRoleReferenceValidator.cs`
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/RoleReferenceValidator.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberRoles.cs:62-71`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs` (`EnsureKnownRolesAsync`)
- Modify: `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs:325` (`InviteCashierAsync`)
- Modify: los dobles de `IRoleReferenceValidator` en pruebas unitarias, si los hay (`rg "IRoleReferenceValidator" tests`)
- Modify: `CLAUDE.md` (gotcha nuevo)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs`

**Interfaces:**
- Consumes: `RoleAssignability.IsAssignable` (Task 1).
- Produces: en `Modules.Tenancy.Application`,
  `public enum RoleReference { Unknown, Unassignable, Known }` y en `IRoleReferenceValidator`
  `Task<RoleReference> CheckAsync(TenantId tenantId, string role, CancellationToken cancellationToken)`, que reemplaza a `IsKnownRoleAsync`.
  `RoleReferenceValidator(ITenantRoleCatalog roleCatalog, ITenantModules tenantModules, IOperatorTenant operatorTenant, ModuleEntitlementMask mask)`: `ContainsRoleAsync` falso da `Unknown`. Si no, resuelve `PermissionsForAsync(tenantId, [role])`, aplica `IsAssignable` y devuelve `Known` o `Unassignable`.

- [ ] **Step 1: Escribir las pruebas** en `MembershipLifecycleApiTests` (`RegisterTenantWithOwnerAsync` da un tenant sin `pos`):
  - `AddingTheCashierRoleWithoutPosIs422`: invita como advisor y hace `SendRolesAsync(..., ["advisor", "cashier"])`. Espera 422 con `code == "tenancy.membership.role_unavailable"`.
  - `ARoleTheMemberAlreadyHoldsIsKeptWithoutItsModule`: prende `pos`, invita con `["advisor", "cashier"]`, apaga `pos` (`TenantModulesApiTests.DisableAsync`) y hace `SendRolesAsync(..., ["cashier", "billing"])`. Espera 200.
  - `RemovingAnUnavailableRoleIsAllowed`: el mismo arranque, `SendRolesAsync(..., ["advisor"])`, y espera 200.
  - `InvitingWithAnUnavailableRoleIs422`: `InviteAsync` con `["cashier"]` sin `pos`. Espera 422 `role_unavailable`.
  - `AddingTheCashierRoleWithPosIsAllowed`: con `pos` prendido, agregar `cashier` da 200.
- [ ] **Step 2: RED.** Las pruebas compilan y fallan contra el código de hoy, que responde 200 en lugar de 422. Se deja anotado; no se ejecutan.
- [ ] **Step 3: Implementar** el enum, el puerto y `RoleReferenceValidator`. Los registros de DI ya existen (`QepServiceCollectionExtensions.cs:1947`). Verifica que `ModuleEntitlementMask` e `IOperatorTenant` se resuelvan desde un servicio scoped.
- [ ] **Step 4: Implementar en `UpdateMemberRoles`.** Mueve la carga de la membresía antes del bucle de roles. Para cada rol, `Unknown` lanza `role_unknown`. `Unassignable` lanza `role_unavailable` solo si `!membership.Roles.Contains(role, StringComparer.Ordinal)`.
- [ ] **Step 5: Implementar en `InviteMember.EnsureKnownRolesAsync`.** `Unknown` lanza `role_unknown` y `Unassignable` lanza `role_unavailable`, siempre, antes de la rama alta/reinvitación. Corrige el comentario de la línea 56, que dice que «la única validación de rol es que el catálogo lo conozca».
- [ ] **Step 6: `PosApiHarness.InviteCashierAsync`** llama `EnablePosAsync(database, tenant)` antes del `POST` (es idempotente por `ON CONFLICT`). Revisa los otros harness con `rg '"cashier"' tests`; hoy solo lo usa Pos.
- [ ] **Step 7: CLAUDE.md.** Agrega este gotcha: «Un rol que no concede ningún permiso ofrecido en el tenant no se puede agregar ni invitar (`tenancy.membership.role_unavailable`); quien ya lo tiene lo conserva. Una prueba que invite un rol de módulo (`cashier`) prende el módulo antes. La regla vive en `RoleAssignability` y la comparten el catálogo y `RoleReferenceValidator`.»
- [ ] **Step 8: GREEN.** `dotnet build` de la solución con 0 errores.
- [ ] **Step 9: Commit** `feat(tenancy): reject assigning a role that grants nothing in the tenant`.
