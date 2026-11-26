# País del distribuidor: de la invitación al tenant — backend

Trabajo cross-repo. Este spec cubre `qep-backend` y se mergea primero. El del frontend es
`qep-frontend/docs/superpowers/specs/2026-11-20-pais-del-distribuidor-hasta-tenant-y-empresa-design.md`
y declara que no necesita cambios.

## Intención

El mayorista elige un país al invitar a un distribuidor. Hoy llega a la fila del distribuidor y al
cliente creado en el tenant del mayorista, y ahí se queda: el tenant propio del distribuidor nace
con el default `CO`. Con este cambio, un distribuidor que acepta con un tenant **nuevo** obtiene un
tenant con el país de la invitación.

## Estado verificado (2026-11-22)

- `Tenant.Country` arranca en `"CO"` (`Tenant.cs`, inicializador de la propiedad) y sólo cambia por
  `UpdateSettings`, que valida con `NormalizeCountryCode` (`tenancy.settings.country.invalid`).
- `Tenant.Create` y `Tenant.EnsureValidRegistration` no reciben país.
- `TenantRegistrationService.RegisterInvitedTenantAsync` crea el tenant con `TenantRegistrationData`, que no tiene país.
- Quien llama: `DistributorInvitationService.ResolveTargetAsync`, rama `NewTenantChoice`. El `Data` lo arma
  `WholesaleInvitationEndpoints` con `RegistrationEndpoints.ToRegistrationData`, sin país.
- `Distributor.Country` está normalizado (mayúsculas, 2 letras) desde `Distributor.Invite`.

## Decisión: dónde viaja el país

**Campo opcional `string? Country = null` al final de `TenantRegistrationData`**, no un parámetro nuevo de
`RegisterInvitedTenantAsync`.

- `EnsureRegistrableAsync` ya recibe `TenantRegistrationData` y corre **antes** de tocar la base: con el país en el
  mismo registro, un país inválido falla ahí (422 `tenancy.settings.country.invalid`) sin dejar nada a medias. Un parámetro
  aparte obligaría a duplicar la validación o a validar tarde.
- No cambia la firma de `RegisterInvitedTenantAsync`: los dobles de `PasswordAuthApiTests` y `DistributorFakes` compilan igual.
- El signup (`RegisterOwnerTenantAsync`, `ToRegistrationData`) no pasa país: `null` significa «el default `CO`».

`DistributorInvitationService` hace `newTenant.Data with { Country = distributor.Country }` antes de `EnsureRegistrableAsync` y de
`RegisterInvitedTenantAsync`. El país **no** sale del cuerpo del accept: `NewTenantChoice` sigue sin leerlo.

## Diseño

1. `Tenant.Create(..., string? country = null)`: `null` conserva `"CO"`; si viene, `NormalizeCountryCode` (trim, mayúsculas, 2 letras)
   o `TenantDomainException("tenancy.settings.country.invalid")`.
2. `Tenant.EnsureValidRegistration(..., string? country = null)` aplica la misma regla, sin construir el tenant.
3. `TenantRegistrationData` suma `Country`; `TenantRegistrationService` lo pasa a `EnsureValidRegistration` y a `Tenant.Create`
   (los dos caminos, el signup con `null`).
4. `DistributorInvitationService`: país del distribuidor guardado en la rama `NewTenantChoice`.
5. `LinkTenantChoice` **no se toca**: no se sobrescribe el país de un tenant vinculado.
6. Idempotencia: el reintento sobre un tenant ya creado sigue devolviendo `false` sin escribir (la rama `existing is not null`
   queda antes de construir nada).

## Pruebas

- Dominio (`TenantTests`): nace con el país dado y normalizado; sin país queda `CO`; país inválido lanza el código.
- Servicio (`TenantRegistrationServiceTests`): el invitado nace con el país del `Data`; el signup sigue en `CO`; el reintento no escribe;
  país inválido en `EnsureRegistrable` falla sin tocar la base.
- Wholesale unit (`DistributorInvitationServiceTests`): el `Data` que llega a `Register`/`EnsureRegistrable` lleva el país del distribuidor;
  el vínculo no llama a `RegisterInvitedTenantAsync`.
- Integración (Wholesale, filtrada por nombre): aceptar con distribuidor `MX` deja `tenancy.tenants.country = 'MX'`; vincular un tenant
  existente en `CO` lo deja en `CO`.

## Fuera de alcance

- Crear la empresa al aceptar; derivar cultura, zona horaria o formato de fecha del país.
- Moneda y formato de número: siguen en `COP` / `CommaDecimal` (el país hoy es informativo).

## Decisión del owner (2026-11-22)

- Sin backfill: los distribuidores ya aceptados conservan `CO`.

## Frontend

Nada: el accept no manda campo nuevo y «Nueva empresa» ya arranca en el país del tenant (verificado en su spec).
