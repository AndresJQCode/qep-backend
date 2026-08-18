# `CAT-08` — Reactivación de tasa de impuesto

> **Estado:** **In Progress** — abierto el 2026-08-17
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> **Depende de:** `CAT-03` (`Complete`) — creó `TaxRate.Deactivate` y el invariante
> `EnsureActive`; `CAT-06` (`Complete`) — creó el `DELETE`, que es la mitad de este problema
> **Repos afectados:** `qep-backend` únicamente
> **Corre el alcance del gate `CAT-00`:** sí. Ver «Alcance que este slice corre en el gate»

## Objetivo

`POST /api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}/activate` — devolver una tasa
inactiva al estado activo.

## De dónde sale este slice

**Deuda declarada al cerrar `CAT-07`**, que resolvió exactamente esta asimetría para `Product` y
dejó anotado que `TaxRate` tenía la misma. No es un pedido nuevo: es la otra mitad de un trabajo
que ya se hizo una vez.

## El defecto que esto cierra, y por qué es peor que el de `Product`

`TaxRate.Update` abre con `EnsureActive()` (`TaxRate.cs:77`), que lanza
`catalog.tax_rate.inactive` → **422**. Y no existe ningún método de dominio que ponga `IsActive`
en `true` después de `Create`. Hasta acá, calcado de lo que `CAT-07` encontró en `Product`.

**La diferencia la puso `CAT-06`, sin querer.** `Product` no tiene `DELETE`, así que su única
falta era la reactivación. `TaxRate` **sí** lo tiene — pero es `DELETE` con `ON DELETE RESTRICT`,
así que **no borra una tasa que algún producto use**: responde `422 catalog.tax_rate.in_use`.

Cruzando las dos cosas, una tasa inactiva **que algún producto usa** queda así:

| Operación | Qué responde hoy | Por qué |
|---|---|---|
| `PUT` (editar) | **422** `catalog.tax_rate.inactive` | `Update` abre con `EnsureActive()` |
| `DELETE` (borrar) | **422** `catalog.tax_rate.in_use` | La FK es `RESTRICT` y hay productos apuntándole |
| `POST .../deactivate` | **422** `catalog.tax_rate.already_inactive` | Ya está inactiva |
| Reactivar | **no existe** | Es este slice |

**No queda ninguna salida por la API.** La tasa es inmutable, imborrable y no se puede volver a
usar: sólo un `UPDATE` por SQL la rescata. `CAT-06` cerró la puerta de adelante creyendo que la de
atrás estaba abierta, y no lo estaba.

**Y desactivar una tasa por error es fácil**, porque no duele en el momento: los productos que ya
la usaban **siguen funcionando**. `ProductTaxRateResolver` acepta a propósito una tasa inactiva
—«inactivarla no debe romper los productos que ya la usaban»— así que nada avisa. El problema
aparece después, cuando alguien quiere corregirle el porcentaje.

## El riesgo que parece haber y no hay

`IX_tax_rates_tenant_name` es único sobre `(TenantId, Name)` y **sin filtro parcial**
(`CatalogDbContext.cs:109-111`), así que **desactivar nunca liberó el nombre**: mientras la fila
existe, ningún otro registro puede tomarlo, esté activa o no. Reactivar **no puede colisionar** con
nadie, y por eso `Activate` **no revalida unicidad**.

Es el mismo razonamiento que `CAT-07` hizo sobre `IX_products_tenant_code`, y el mismo que el
hallazgo `B` de `CAT-03` dejó sentado. `CA-CAT-08-09` lo ancla contra el día en que alguien le
agregue un filtro parcial al índice.

## Contrato que se expone

| Método | Ruta | Permiso |
|---|---|---|
| `POST` | `/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}/activate` | `catalog.tax_rate.manage` |

**Ningún permiso nuevo.** Activar es administrar, y `catalog.tax_rate.manage` ya lo cubre — el
mismo criterio con el que `deactivate` y el `DELETE` de `CAT-06` no tienen permiso propio.

**Verbo dedicado, no un booleano en el `PUT`.** Es la decisión que `CAT-03` tomó para
`deactivate` y que `CAT-07` volvió a tomar para `Product`; este slice la espeja por tercera vez.

**Respuesta `200 TaxRateResponse`**, igual que `deactivate`.

| Caso | Respuesta |
|---|---|
| La tasa existe en el tenant y está **inactiva** | **200**, con `isActive: true` |
| La tasa ya está **activa** | **422** `catalog.tax_rate.already_active` |
| La tasa no existe, o es de otro tenant | **404** `catalog.tax_rate.not_found` |
| Falta el permiso `catalog.tax_rate.manage` | **403** |

## Códigos de dominio nuevos

| Invariante | Código |
|---|---|
| La tasa ya está activa | `catalog.tax_rate.already_active` |

Espeja `catalog.tax_rate.already_inactive`, que `Deactivate` ya lanza. El nombre no se elige
libre: se deriva del que existe.

## Evento de auditoría nuevo

| Operación | Evento |
|---|---|
| Activación exitosa | `catalog.tax_rate.activated` |

Por outbox, con `ICatalogAuditPublisher`, en la misma transacción que la escritura — el camino que
ya usa `DeactivateTaxRateHandler`.

## Fuera de alcance

- **Cambiar el comportamiento de `ProductTaxRateResolver`.** Que un producto pueda apuntar a una
  tasa inactiva es una decisión tomada y documentada en el propio resolver; este slice no la toca.
- **Impedir desactivar una tasa en uso.** Sería la otra forma de evitar el atolladero, y es un
  cambio de contrato de `deactivate` que nadie pidió. Reactivar es la salida menos invasiva.
- **Reactivar productos.** Ya es `CAT-07`, `Complete`.
- **El frontend.** Cualquier botón de reactivar es fila del ledger de `qep-frontend`.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-08-01` | Dada una tasa **inactiva**, cuando se hace `POST .../activate`, entonces responde **200** con `isActive: true`, y la fila en base queda en `true` |
| `CA-CAT-08-02` | Dada una tasa **ya activa**, entonces responde **422** `catalog.tax_rate.already_active` — no 200 silencioso ni 500 |
| `CA-CAT-08-03` | Dada una tasa inactiva, cuando se la activa y después se hace `PUT`, entonces el `PUT` responde **200** — el callejón sin salida queda cerrado de punta a punta |
| `CA-CAT-08-04` | Dado un `{taxRateId}` **de otro tenant**, entonces responde **404**, y la tasa ajena **sigue inactiva** en base |
| `CA-CAT-08-05` | Dado un `{taxRateId}` inexistente, entonces **404** |
| `CA-CAT-08-06` | Dado un llamador **sin** `catalog.tax_rate.manage`, entonces **403**, y la tasa **sigue inactiva** |
| `CA-CAT-08-07` | Dada una activación exitosa, entonces deja **una** entrada `catalog.tax_rate.activated` en el outbox, en la misma transacción; y el **422** de `-02` **no** deja ninguna |
| `CA-CAT-08-08` | Dada una activación exitosa, entonces `Version` avanza en 1 y `UpdatedAt` toma la hora del `IClock` |
| `CA-CAT-08-09` | Dada una tasa inactiva de nombre `X`, entonces crear otra con nombre `X` sigue dando **422** `catalog.tax_rate.name_taken`, y activar la primera sigue respondiendo **200** — el nombre nunca se liberó |
| `CA-CAT-08-10` | **Dada una tasa inactiva que un producto usa —la que hoy no se puede editar ni borrar—, entonces el `DELETE` sigue dando 422 `in_use`, la activación responde 200, y después el `PUT` responde 200** |

`CA-CAT-08-10` es **el criterio que justifica este slice** y lo que lo distingue de `CAT-07`:
reproduce el atolladero completo antes de resolverlo. Sin él, esto es sólo «un endpoint más por
simetría». `CA-CAT-08-03` es su versión mínima, sin producto de por medio.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | Invariantes de `TaxRate.Activate`: activa una inactiva, rechaza una ya activa con el código correcto, avanza `Version` y `UpdatedAt` |
| Arquitectura | `CatalogLayerTests` se corre sin cambios |
| Integración | Los 10 CA contra PostgreSQL real |
| Runtime | Los criterios contra la API local, con la auditoría verificada en base |

**TDD obligatorio, RED antes que GREEN, y el RED tiene que fallar por el motivo correcto.** El RED
de integración debe dar el 404 de ruta —endpoint ausente—, no un 500.

**Y la verificación contra el mecanismo ausente**, que `CAT-07` cumplió: quitar `Version++` de
`Activate` tiene que poner roja a la prueba de `CA-CAT-08-08`. Si sigue verde, la prueba no sirve.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **Olvidar registrar el handler en el composition root.** Se registran a mano en `QepServiceCollectionExtensions`; sin la línea el dispatcher no resuelve y el síntoma es **500, no 404** | Cualquiera de los 10 CA. Es el defecto de `CAT-06` y el riesgo número uno de `CAT-07` |
| **Olvidar `Version++`.** Es el token de concurrencia optimista: sin el incremento, dos escrituras que se solapan se pisan en silencio | `CA-CAT-08-08`, verificado saboteando el mecanismo |
| Activar una tasa de otro tenant | `CA-CAT-08-04`, verificando que la fila ajena **siga inactiva** después del 404 |
| Autorizar **después** de leer el repositorio | `CA-CAT-08-06`. `CatalogAuthorization.EnsureAuthorized` va primero en el handler |
| Devolver 200 al activar algo ya activo | `CA-CAT-08-02` |

## Alcance que este slice corre en el gate `CAT-00`

El gate declara **seis** operaciones para `tax-rates` —las cinco originales más el `DELETE` que
agregó `CAT-06`— y ninguna es `activate`. **Este slice agrega la séptima y hay que escribirla en
el gate**, que vive en `qep-frontend`. Es el mismo trámite que ya hicieron `CAT-04`, `CAT-06` y
`CAT-07`; se escribe desde un worktree, sobre `develop`, sin tocar el árbol de trabajo del otro
developer.

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — dominio, Application y Api (2026-08-17)

**RED unitario literal, antes de escribir implementación**, con las cuatro pruebas compilando
contra un método que no existía:

```txt
TaxRateTests.cs(182,17): error CS1061: "TaxRate" no contiene una definición para "Activate"
TaxRateTests.cs(196,21): error CS1061: "TaxRate" no contiene una definición para "Activate"
TaxRateTests.cs(210,17): error CS1061: "TaxRate" no contiene una definición para "Activate"
TaxRateTests.cs(223,17): error CS1061: "TaxRate" no contiene una definición para "Activate"
```

**GREEN unitario:** `Superado: 63, Total: 63` — eran 59 al cerrar `CAT-07`.

**RED de integración**, del tipo que el spec exige —404 de ruta por endpoint ausente, no un 500—,
y **GREEN 10/10** con los diez criterios contra PostgreSQL real.

`ActivateTaxRate.cs` espeja a `DeactivateTaxRate.cs` línea por línea: autorización antes de tocar
el repositorio, `TaxRateNotFound.For` para el 404, y `catalog.tax_rate.activated` por outbox en la
misma transacción. El handler quedó registrado en `QepServiceCollectionExtensions` — el riesgo
número uno.

#### La prueba contra el mecanismo ausente

Se quitó `Version++` de `TaxRate.Activate` y la prueba que lo ancla se puso roja:

```txt
Modules.Catalog.UnitTests.TaxRateTests.ActivateAdvancesTheConcurrencyToken
Assert.Equal() Failure: Values differ
Expected: 3
Actual:   2
```

**Y las otras tres pruebas de `Activate` quedaron verdes**, que es justo lo que demuestra por qué
esa prueba existe: sin ella, perder el token de concurrencia optimista no lo nota nadie. El
sabotaje se restauró y el build volvió a `0 Advertencia(s), 0 Errores`.

#### Regresión: 326 en verde, cero regresión

`Modules.Catalog.IntegrationTests` pasó de `86` a **`96/96`**; unitarias `63/63`; arquitectura
`17/17` **sin cambios**. Los 5 fallos de `SDD-CT-14` verificados **por nombre**:

```txt
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.LogoutRevokesTheSessionCookie
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.MutatingRequestWithoutCsrfHeaderIsRejected
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.RoleDowngradeRemovesPermissionsOnTheNextRequest
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken
Modules.Tenancy.IntegrationTests.RealAuthenticationApiTests.SuspendingMembershipRevokesTheMembersActiveSession
```

### Tramo 2 — runtime contra la API local: 14 de 14 (2026-08-17)

Los diez criterios, con `CA-CAT-08-09` y `CA-CAT-08-10` desdoblados en sus mitades:

```txt
  OK   CA-CAT-08-02  -> 422   already_active
  OK   CA-CAT-08-06  -> 403   sigue inactiva en base: f
  OK   CA-CAT-08-05  -> 404
  OK   CA-CAT-08-04  -> 404   la ajena sigue inactiva: f
  OK   CA-CAT-08-09a -> 422   name_taken
  OK   CA-CAT-08-01  -> 200   isActive:true / base: t
  OK   CA-CAT-08-09b -> t     activar la primera responde 200 y queda activa
  OK   CA-CAT-08-08  -> 3     updated_at > created_at: t
  OK   CA-CAT-08-03  -> 200   porcentaje 21
  OK   CA-CAT-08-07  -> 1     una activación exitosa; el 422 y el 403 no dejaron fila
  OK   CA-CAT-08-10a -> 422   PUT bloqueado: tax_rate.inactive
  OK   CA-CAT-08-10b -> 422   DELETE bloqueado: tax_rate.in_use
  OK   CA-CAT-08-10c -> 200   rescatada
  OK   CA-CAT-08-10d -> 200   y ahora sí se puede editar
```

**`CA-CAT-08-10` es la que vale, y en runtime se ve entera:** el `PUT` rechazado por
`tax_rate.inactive`, el `DELETE` rechazado por `tax_rate.in_use`, y recién después la activación
que la rescata y el `PUT` que ahora sí pasa. Las dos puertas cerradas y la tercera abriéndose, en
orden, contra la API real.

**`CA-CAT-08-04` y `CA-CAT-08-06` se verificaron en base**, no por status: después del 404 y del
403, `SELECT is_active` devuelve `f`.

**`CA-CAT-08-07` por lo que escribió y por lo que no:** una fila `catalog.tax_rate.activated` con
`resourceId` apuntando a la tasa, después de tres intentos de activación sobre ella —el 422, el
403 y el 200—.

#### El mismo fallo de verificador que en `CAT-07`, repetido

`CA-CAT-08-06` reportó `4` en vez de `403`: el `%{http_code}` de `curl.exe` se trunca al capturarlo
dentro de un array de argumentos de PowerShell. **Es exactamente el mismo error que en el runtime
de `CAT-07`**, y volvió a pasar porque se reusó el script. Verificado con `curl.exe` directo:
`activate=403`, y la tasa sigue en `f`. El código nunca estuvo mal; el medidor sí, dos veces.

### Tramos pendientes

| Tramo | Qué falta |
|---|---|
| Gate | La **séptima** operación de `tax-rates` en `CAT-00`, que vive en `qep-frontend` |
| Revisión | Lente ciego, como `CAT-07` |
| Ledger | La entrada de handoff en `sdd/02-plan/plan-maestro.md` |
