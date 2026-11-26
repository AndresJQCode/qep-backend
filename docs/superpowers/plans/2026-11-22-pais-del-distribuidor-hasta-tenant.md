# País del distribuidor hasta el tenant — plan de implementación

> **Para quien ejecute:** 2 bloques, un implementador por bloque, un commit por tarea, una revisión al final (regla del owner, 2026-11-12).

**Meta:** que el tenant de un distribuidor que acepta con tenant nuevo nazca con `distributor.Country`.

**Spec:** [`../specs/2026-11-22-pais-del-distribuidor-hasta-tenant-design.md`](../specs/2026-11-22-pais-del-distribuidor-hasta-tenant-design.md)

## Restricciones

- TDD: RED antes que GREEN con salida literal. El RED de un fallo de compilación es la salida del build, sin contenedores.
- Pruebas filtradas por nombre; clase completa una vez por bloque; nunca dos integraciones a la vez (revisar `testhost`/`dotnet`).
- Rama `feature/pais-distribuidor-tenant`. Sin atribución de IA. `docs/estado.md` sólo en `develop`, al mergear.

## Bloque 1 — Tenancy

### Task 1: dominio

- [ ] RED en `TenantTests`: `CreateWithACountryNormalizesIt` (`" mx "` → `MX`), `CreateWithoutCountryKeepsColombia`, `CreateWithAnInvalidCountryThrows` (`tenancy.settings.country.invalid`), `EnsureValidRegistrationRejectsAnInvalidCountry`.
- [ ] GREEN: `Tenant.Create` y `EnsureValidRegistration` con `string? country = null`; el constructor privado asigna `Country`.
- [ ] Commit `feat(tenancy): create a tenant with an optional validated country`.

### Task 2: servicio

- [ ] RED en `TenantRegistrationServiceTests`: invitado con `Data { Country = "MX" }` nace `MX`; signup nace `CO`; reintento devuelve `false` sin escribir; `EnsureRegistrableAsync` con país inválido lanza sin consultar el slug.
- [ ] GREEN: `TenantRegistrationData(..., string? Country = null)`; el servicio lo pasa en `EnsureRegistrableAsync`, `RegisterOwnerTenantAsync` y `RegisterInvitedTenantAsync`.
- [ ] Commit `feat(tenancy): carry an optional country in the registration data`.

## Bloque 2 — Wholesale

### Task 3: aceptación

- [ ] RED en `DistributorInvitationServiceTests`: el `Data` revisado y el registrado llevan el país del distribuidor (`MX`), aunque el `Data` del choice traiga otro; el vínculo no registra tenant.
- [ ] GREEN: `ResolveTargetAsync` usa `newTenant.Data with { Country = distributor.Country }` en las dos llamadas.
- [ ] Commit `feat(wholesale): create the invited tenant with the distributor country`.

### Task 4: integración y regresión

- [ ] RED/GREEN en Wholesale.IntegrationTests (filtrado por nombre): aceptar con distribuidor `MX` → `tenancy.tenants.country = 'MX'`; vincular tenant existente `CO` → sigue `CO`. Regresión de signup normal en `CO` con una prueba de Tenancy existente o nueva.
- [ ] Commit `test(wholesale): the accepted tenant is born with the invited country`.

## Cierre

Suite de `Modules.Tenancy.UnitTests` y `Modules.Wholesale.UnitTests` completas; integración de Wholesale una vez; arquitectura (`ArchitectureTests`). Revisión fuerte de la rama; merge a `develop`; `docs/estado.md` (fila + registro) en `develop`.
