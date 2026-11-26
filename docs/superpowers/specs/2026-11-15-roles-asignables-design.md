# Roles asignables según los módulos del tenant — backend

Fecha: 2026-11-15. Repos: `qep-backend` (este) y `qep-frontend` (spec hermano con el mismo nombre).

## Intención

En un tenant sin el módulo `pos`, el editor de roles de un miembro ofrece «Cajero» y la API lo acepta.
El rol no concede nada —`ModuleEntitlementMask` le niega todo `pos.*`— y solo confunde. El owner quiere
que un rol que no concede nada en el tenant no se pueda asignar. Cajero sin POS es el caso de hoy, pero la
regla es general.

## Regla

Un rol es **asignable** en un tenant si concede al menos un permiso que el tenant ofrece, es decir, si
queda algo en la lista de permisos del rol después de aplicar el mismo filtro que ya usa
`/authorization/catalog` (`Offered` + `OperatorPermissionFilter`, `AuthorizationCatalogEndpoints.cs:104-110`).

- Vale para los roles de sistema y para los personalizados (`ITenantRoleCatalog`).
- Tenant que no está en `tenancy.tenants` (solo pasa con el stub de desarrollo): todo es asignable, igual
  que hoy el catálogo (`modules is null`).

## Comportamiento

| Dónde | Hoy | Después |
|---|---|---|
| `GET …/authorization/catalog` | lista todos los roles, con los permisos filtrados | igual, y cada rol lleva `assignable: bool` |
| `PUT …/memberships/{id}/roles` (`UpdateMemberRoles`) | solo exige que el rol exista | además, cada rol **agregado** (pedido y no presente en `membership.Roles`) tiene que ser asignable |
| `POST` de invitación (`InviteMember`, alta y reinvitación) | solo exige que el rol exista | todos los roles pedidos tienen que ser asignables |

- Rechazo: `TenantDomainException("tenancy.membership.role_unavailable")` → 422, con el rol en el mensaje.
  El orden de las comprobaciones es: rol desconocido (`role_unknown`) y después no asignable.
- Quien ya tiene un rol no asignable lo conserva: guardar sin tocarlo vuelve a mandarlo y pasa. Quitarlo
  también pasa.
- No se toca nada guardado: ninguna migración y ningún barrido de membresías. Si el módulo vuelve, el rol
  vuelve a conceder lo que concedía.

## Enfoques considerados

1. **Elegido:** la regla vive en Authorization y la consultan los handlers de Tenancy por el puerto que ya
   existe. `IRoleReferenceValidator` pasa de `IsKnownRoleAsync` a una respuesta de tres estados
   (`Known | Unassignable | Unknown`). El catálogo y la validación usan la misma función, así que no pueden
   discrepar.
2. Validar en `Membership.ChangeRoles`: se descartó porque el dominio de Tenancy no conoce los permisos ni
   la máscara.
3. Solo en el frontend: se descartó porque la API seguiría aceptando el rol.

## Diseño

- Una función pura en `Modules.Authorization.Application` (por ejemplo, `RoleAssignability`) recibe los
  permisos del rol, el `TenantModuleSet?` y si el tenant es el operador, y responde si es asignable.
  `AuthorizationCatalogEndpoints.GetCatalogAsync` la usa para `assignable` y `RoleReferenceValidator` para
  la validación. Su comportamiento lo cubren pruebas unitarias.
- `RoleReferenceValidator` resuelve los permisos del rol con `ITenantRoleCatalog` y los módulos con
  `ITenantModules`.
- `RoleCatalogItemResponse` suma `bool Assignable` al final. El cambio es aditivo, así que un frontend viejo
  lo ignora.
- La invitación valida todos los roles, también en la reinvitación: volver a invitar es conceder de nuevo.
  **Decisión mía:** una invitación creada con el módulo prendido y aceptada con el módulo apagado se acepta
  igual, porque el rol ya se había concedido al invitar.

## Pruebas (TDD)

- Unitarias de `RoleAssignability`: un rol solo de `pos.*` sin `pos` no es asignable y con `pos` sí; un rol
  mixto es asignable; un rol solo de `operator.*` fuera del tenant operador no es asignable; con el tenant
  desconocido todo es asignable.
- Integración (Tenancy):
  - el catálogo de un tenant sin `pos` trae `cashier` con `assignable: false`;
  - el `PUT` que agrega `cashier` responde 422 `tenancy.membership.role_unavailable`;
  - el `PUT` de un miembro que ya es `cashier` y lo conserva junto a otro rol responde 200;
  - la invitación con `cashier` responde 422;
  - con `pos` prendido (`PosApiHarness.EnablePosAsync` o la fila de `tenancy.tenant_modules`) el `PUT`
    responde 200.

## Fuera de alcance

- Que `/authorization/catalog` ofrezca los roles personalizados (hoy lista solo los de sistema, desde
  `IRoleCatalog`). Va en un spec aparte, inmediatamente después.
- `/authorization/roles` y la página de roles no cambian.

## Pendiente para quien construye

- El nombre exacto de la función y del enum de tres estados.
- La forma de los mensajes de excepción.
