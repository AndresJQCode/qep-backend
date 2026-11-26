# Roles personalizados en la pantalla de miembros

Fecha: 2026-11-16. Repos: `qep-backend` (este) y `qep-frontend` (mismo archivo en `docs/superpowers/specs/`).
Sigue a `2026-11-15-roles-asignables-design.md`.

## Intención

El owner quiere asignar roles personalizados del tenant (sus casos: «Verificador de pagos» y «Auxiliar de facturación»)
desde la pantalla de miembros, al invitar y al cambiar los roles de un miembro. La API ya los acepta
(`RoleReferenceValidator` usa `ITenantRoleCatalog`), pero la pantalla no los ofrece: `roles[]` de
`GET …/authorization/catalog` sale de `IRoleCatalog.ListRoles()`, que sólo trae los de sistema.

## Diseño (aprobado por el owner)

- **Backend:** `roles[]` de `/authorization/catalog` sale de `ITenantRoleCatalog.ListRolesAsync(tenantId)` (sistema
  primero, después custom, por clave ordinal: el orden que ya da `TenantRoleCatalog`). Cada rol pasa por
  `RoleAssignability.OfferedPermissions` igual que hoy, así que un custom que no concede nada en el tenant sale con
  `assignable: false` y la pantalla de roles asignables ya sabe tratarlo. `permissions[]` (casillas) y `CatalogVersion`
  no cambian. `RoleCatalogItemResponse` no cambia de forma.
- **Frontend:** la pantalla de miembros ya lee `catalog.roles`, así que no cambia. Crear, editar o borrar un rol
  invalida además `authorizationCatalogQueryKey(tenantId)` (`features/auth/services/tenancy.api.ts`), para que el rol
  aparezca en miembros sin recargar.

## Enfoques considerados

1. **Elegido:** que el catálogo liste los custom. Una sola fuente de «asignable» (`RoleAssignability`).
2. Que la pantalla de miembros lea `/authorization/roles`: se descartó porque obliga a calcular `assignable` en un
   segundo lugar, o en el cliente.

## Pruebas

- Integración (`TenantModulesApiTests`, tenant registrado): un custom creado por `POST …/authorization/roles` con
  `customers.customer.read` aparece en `roles[]` del catálogo con su `displayName` y `assignable: true`; uno con sólo
  `pos.sale.read` aparece con `assignable: false`.
- Frontend: crear un rol invalida la consulta del catálogo.

## Coordinación

Otra sesión trabaja en `fix/roles-editor` (`TenantRoleDefinition.Id`, `use-roles.ts`) y en
`feature/roles-permission-matrix` (campos nuevos en `permissions[]` del mismo endpoint). Esta rama se mergea **después**
de `fix/roles-editor`, y los conflictos se resuelven en la rama.

## Fuera de alcance

- Marcar en pantalla cuál rol es custom.
- Los roles «Verificador de pagos» y «Auxiliar de facturación» los crea el owner en la pantalla de roles; no se siembran.
