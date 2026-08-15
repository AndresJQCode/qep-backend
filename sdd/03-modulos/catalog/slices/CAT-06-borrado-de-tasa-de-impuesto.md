# `CAT-06` — Borrado de tasa de impuesto

> **Estado:** **Complete** — 2026-08-15
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> **Depende de:** `CAT-03` (`Complete`) — creó `TaxRate`; `CAT-04` (`Complete`) — creó la FK
> `FK_products_tax_rates_tax_rate_id` con `RESTRICT`
> **Repos afectados:** `qep-backend`, y el `tax-rate-modal.tsx` de `qep-frontend` deja de apuntar
> a un endpoint inexistente
> **Corre el alcance del gate `CAT-00`:** sí. Ver «Alcance que este slice corre en el gate»

## Objetivo

`DELETE /api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}` — borrar de verdad una tasa de
impuesto, con la única condición que la base ya impone: **que no la esté usando ningún producto**.

## De dónde sale este slice

El owner lo pidió el 2026-08-15. El frontend **ya tenía el botón**: `tax-rate-modal.tsx` llama a
`deleteTaxRate`, que hasta hoy pegaba a una ruta que el backend no expone. Se descubrió al alinear
`CAT-01` y quedó declarado como deuda; este slice la cierra por el lado del backend en vez de
retirar el botón.

## La restricción que manda el diseño, y no es negociable

`catalog.products.tax_rate_id` referencia `catalog.tax_rates(id)` con **`ON DELETE RESTRICT`**,
introducido por la migración `AddProductDetails` de `CAT-04`. **PostgreSQL no va a permitir borrar
una tasa que algún producto esté usando**, y eso es correcto: si lo permitiera, el producto
quedaría apuntando a una fila que no existe, o su impuesto se borraría en cascada sin que nadie lo
pidiera.

Entonces el endpoint no puede prometer «borra siempre». Promete:

| Caso | Respuesta |
|---|---|
| La tasa no existe, o es de otro tenant | **404** `catalog.tax_rate.not_found` |
| La tasa existe y **algún producto la usa** | **422** `catalog.tax_rate.in_use` |
| La tasa existe y nadie la usa | **204**, y la fila desaparece |

**El `422` es la mitad que importa.** Sin él, el caso llega a PostgreSQL, vuelve como violación de
FK y sale **500 `server.unexpected`** — que además, por el hallazgo `C` de `CAT-04`, filtraría el
nombre de la constraint al llamador.

## Cómo se decide «está en uso», y por qué de las dos maneras

**Se comprueba en el handler antes de borrar** —una consulta de conteo— **y además se traduce la
violación de FK en `Infrastructure`.** Las dos, y no es redundancia:

- La comprobación previa da el mensaje correcto y el status correcto en el caso normal.
- La traducción cubre la **carrera**: entre la consulta y el `COMMIT`, otra transacción puede
  crear un producto que use esa tasa. La ventana es chica y existe, y sin la traducción ese caso
  sale como 500.

**Discriminar la misma constraint por dos causas opuestas.** `FK_products_tax_rates_tax_rate_id`
se viola en dos escenarios distintos:

| Quién escribe | Qué significa | Código |
|---|---|---|
| Un `Product` que apunta a una tasa inexistente | La tasa no existe | `catalog.product.tax_rate_not_found` |
| Un `TaxRate` que se borra y sigue referenciado | La tasa está en uso | `catalog.tax_rate.in_use` |

Mismo `SqlState 23503` y mismo nombre de constraint. Se distinguen **por qué entidad estaba
guardando EF**, que es lo que `DbUpdateException.Entries` expone. Devolver un solo código para los
dos casos manda a corregir el problema equivocado — es exactamente la lección de `SDD-CT-06`, pero
un nivel más adentro.

## Fuera de alcance

- **Borrado lógico.** `Deactivate` ya existe y es la operación que conserva historia. Este slice
  es el borrado real, para la tasa que se cargó por error y nunca se usó.
- **Borrar en cascada los productos que la usan.** Nunca: perdería datos que nadie pidió perder.
- **Reasignar los productos a otra tasa antes de borrar.** Es una operación de migración de datos
  con decisiones de negocio propias. Si hace falta, es su propio slice.
- **El frontend.** `tax-rate-modal.tsx` ya llama a `deleteTaxRate`; con este slice deja de pegarle
  a una ruta inexistente. Alinear su manejo del `422` es fila del ledger del otro repo.

## Contrato que se expone

| Método | Ruta | Permiso |
|---|---|---|
| `DELETE` | `/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}` | `catalog.tax_rate.manage` |

**Ningún permiso nuevo.** Borrar es administrar, y `catalog.tax_rate.manage` ya lo cubre — el
mismo criterio con el que `deactivate` no tiene permiso propio.

**Respuesta `204 No Content`.** No hay recurso que devolver: se borró. Es lo que ya hace
`DELETE /files/{id}` en `Storage`.

## Códigos de dominio nuevos

| Invariante | Código |
|---|---|
| La tasa está referenciada por al menos un producto | `catalog.tax_rate.in_use` |

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-06-01` | Dado un `DELETE` de una tasa que **ningún producto usa**, entonces responde **204** y la fila desaparece de la base |
| `CA-CAT-06-02` | **Dado un `DELETE` de una tasa que un producto usa, entonces responde 422 `catalog.tax_rate.in_use`, no 500, y la tasa sigue existiendo** |
| `CA-CAT-06-03` | Dado un `DELETE` de una tasa **de otro tenant**, entonces responde **404** y la tasa del otro tenant **sigue existiendo** |
| `CA-CAT-06-04` | Dado un `DELETE` de un id inexistente, entonces **404** |
| `CA-CAT-06-05` | Dado un `DELETE` sin el permiso `catalog.tax_rate.manage`, entonces **403** |
| `CA-CAT-06-06` | Dado un borrado exitoso, entonces deja **una** entrada de auditoría en el outbox, en la misma transacción |
| `CA-CAT-06-07` | Dada una tasa **inactiva** que nadie usa, entonces se borra igual — desactivar y borrar son operaciones distintas |
| `CA-CAT-06-08` | Dada una violación de FK que llega a la base pese a la comprobación previa, entonces se traduce a `catalog.tax_rate.in_use` y **no** a `catalog.product.tax_rate_not_found` |

`CA-CAT-06-03` es el criterio de aislamiento: un `DELETE` que devuelva 404 pero borre igual sería
la peor forma de esta fuga, porque no deja rastro visible en la respuesta.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | — el comando no tiene reglas de dominio propias; la condición vive en la consulta |
| Arquitectura | `CatalogLayerTests` sin cambios; se corre para verificar que Application sigue sin EF Core |
| Integración | Los 8 CA contra PostgreSQL real, incluido `CA-CAT-06-08` contra la traducción |
| Runtime | Los criterios contra la API local |

**TDD obligatorio, RED antes que GREEN, y el RED tiene que fallar por el motivo correcto.**

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **Borrar una tasa de otro tenant** | `CA-CAT-06-03`, verificando que la fila ajena sigue ahí |
| La violación de FK saliendo como **500** | `CA-CAT-06-02` y `CA-CAT-06-08` |
| Devolver el código de la causa opuesta (`tax_rate_not_found` en vez de `in_use`) | `CA-CAT-06-08`, que afirma sobre el código y no sólo sobre el status |
| Perder productos por un borrado en cascada | No se implementa cascada. La FK es `RESTRICT` y se deja así |

## Alcance que este slice corre en el gate `CAT-00`

El gate ratificó **cinco** operaciones para `tax-rates` y ninguna es `DELETE`. Este slice agrega
la sexta, por pedido del owner del 2026-08-15. **Hay que escribirlo en el gate**, que vive en
`qep-frontend`, igual que se hizo con los cinco campos de `CAT-04` el mismo día.

## Evidencia de cierre

### RED, por el motivo correcto

Las 8 pruebas de integración escritas antes de una sola línea de implementación, contra un
endpoint que no existía:

```txt
Expected: NoContent
Actual:   MethodNotAllowed
Con error! - Con error: 8, Superado: 0, Total: 8
```

### El defecto que apareció en medio, y que no se parece a su causa

Con el endpoint mapeado y el handler escrito, las 8 pasaron de `MethodNotAllowed` a
**`InternalServerError`**. El motivo: **los handlers se registran en el composition root uno por
uno, a mano**, y faltaba la línea de éste. El endpoint resolvía, el dispatcher no encontraba a
quién llamar, y el síntoma era un **500** que no dice nada del problema.

Es el mismo defecto que el `README` documenta para las políticas de permiso —«sin la política,
`RequireAuthorization` no resuelve y el síntoma es 500, no 403»—, y ahora quedó documentado
también para los handlers, en el propio `QepServiceCollectionExtensions`.

### GREEN

```txt
Correctas! - Con error: 0, Superado: 8, Total: 8 - Modules.Catalog.IntegrationTests.dll
```

Integración de `catalog` en `77/77`, unitarias `55/55`, arquitectura `17/17`. **Regresión de toda
la solución: 299 en verde**, con los 5 fallos de `SDD-CT-14` verificados **por nombre**.
`dotnet build` limpio; `dotnet format --verify-no-changes` no reporta ninguno de los archivos
tocados.

### Runtime contra la API local: 8 de 8

```txt
  OK   CA-CAT-06-01 -> 204   en base quedan: 0 filas
  OK   CA-CAT-06-02 -> 422   catalog.tax_rate.in_use / sobrevive: 1
  OK   CA-CAT-06-03 -> 404   la ajena sigue en base: 1 fila(s)
  OK   CA-CAT-06-04 -> 404   id inexistente
  OK   CA-CAT-06-05 -> 403   sobrevive: 1 fila(s)
  OK   CA-CAT-06-06 -> 1   entradas de auditoria nuevas
  OK   CA-CAT-06-07 -> 204   en base quedan: 0 filas
  OK   atomicidad -> 0   el 422 no escribio auditoria
```

**Las aserciones que importan no son los status.** `CA-CAT-06-03` verifica que la tasa del otro
tenant **siga en base** después del 404 —un `DELETE` que responda 404 y borre igual sería la peor
forma de esa fuga—, `CA-CAT-06-02` que la tasa en uso sobreviva, y la última fila que el `422`
**no dejó entrada de auditoría**: la operación fallida y su registro abortan juntos.

### Lo que queda abierto

| Qué | Dónde |
|---|---|
| **Escribir la sexta operación en el gate `CAT-00`** | `qep-frontend`. El gate ratificó cinco y ninguna era `DELETE` |
| **`tax-rate-modal.tsx` no maneja el `422`** | Su botón de eliminar ahora pega a un endpoint real, pero muestra el mensaje genérico cuando la tasa está en uso. Fila del ledger del otro repo |
| **Revisión con lentes ciegos** | No se hizo. Autorrevisión, como `CAT-05` — deuda de método que ya lleva dos slices |
