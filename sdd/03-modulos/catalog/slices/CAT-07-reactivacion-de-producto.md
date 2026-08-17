# `CAT-07` — Reactivación de producto

> **Estado:** **In Progress** — abierto el 2026-08-15
> **Módulo:** `catalog` — ficha y gate en `qep-frontend/sdd/03-modulos/catalog/`
> **Depende de:** `CAT-02b` (`Complete`) — creó `Product.Deactivate` y el invariante
> `EnsureActive`; `CAT-04` (`Complete`) — creó `ProductDetails` y la FK a `TaxRate`
> **Repos afectados:** `qep-backend` únicamente
> **Corre el alcance del gate `CAT-00`:** sí. Ver «Alcance que este slice corre en el gate»

## Objetivo

`POST /api/v1/tenants/{tenantId}/catalog/products/{productId}/activate` — devolver a un producto
inactivo al estado activo, que hoy es un viaje sin vuelta.

## De dónde sale este slice

El owner lo pidió el 2026-08-15, después de notar que el endpoint de activación no existía.

**No es una funcionalidad nueva: es la mitad faltante de un requisito ya ratificado.** `RF-020`
dice «Administrar productos activos/**inactivos**», y el gate `CAT-00` lo verificó literal
(`04-requisitos/requisitos-funcionales.md`, línea 27). `CAT-02b` implementó una sola dirección.

## El defecto que esto cierra, y es peor que «falta un endpoint»

Un producto inactivo **no es sólo invisible: es inmutable y terminal**.

`Product.Update` abre con `EnsureActive()` (`Product.cs:118`), que lanza
`catalog.product.inactive` → **422**. Y no existe ningún método de dominio que ponga `IsActive`
en `true` después de `Create`. Entonces, desde la API, un producto desactivado:

- no se puede editar — `PUT` responde 422;
- no se puede reactivar — no hay ruta;
- no se puede borrar — `catalog` no expone `DELETE` de producto.

**La única salida es un `UPDATE` por SQL contra la base de producción.** Un click de más en el
frontend es hoy una operación irreversible sobre datos maestros del tenant.

## El riesgo que NO existe, y conviene decirlo antes de que alguien lo invente

**Reactivar no puede chocar con el código de otro producto.**

`IX_products_tenant_code` es único **y no tiene filtro parcial** (`CatalogDbContext.cs:79-81`):
la unicidad de `(tenant_id, code)` alcanza a las filas inactivas igual que a las activas. O sea
que **desactivar nunca liberó el código** — sigue reservado todo el tiempo que el producto está
inactivo, y ningún otro producto pudo haberlo tomado mientras tanto.

Es el mismo hallazgo `B` que el owner cerró en `CAT-03` para `IX_tax_rates_tenant_name`, del otro
lado del mismo comportamiento.

**Consecuencia de diseño:** `Activate` no revalida unicidad, no consulta la base buscando
colisiones y no puede devolver `catalog.product.code_taken`. Una implementación que agregue esa
comprobación estaría defendiéndose de un caso imposible y sumando una consulta por operación.

Lo mismo vale para `TaxRateId`: `CAT-06` sólo permite borrar una tasa que **ningún** producto
referencia, y la FK cuenta las filas inactivas. La tasa de un producto inactivo sigue existiendo.

## Contrato que se expone

| Método | Ruta | Permiso |
|---|---|---|
| `POST` | `/api/v1/tenants/{tenantId}/catalog/products/{productId}/activate` | `catalog.product.manage` |

**Ningún permiso nuevo.** Activar es administrar, y `catalog.product.manage` ya lo cubre — el
mismo criterio con el que `deactivate` no tiene permiso propio.

**Verbo dedicado, no un booleano en el `PUT`.** Es la decisión que `CAT-02b` ya tomó para
`deactivate` y que este slice espeja: un `isActive` editable convertiría el cambio de estado en
un `PUT` cualquiera, sin evento de auditoría propio y sin invariante que lo custodie.

**Respuesta `200 ProductResponse`**, igual que `deactivate`. El llamador recibe el producto con
`isActive` en `true` y no necesita un `GET` de vuelta.

| Caso | Respuesta |
|---|---|
| El producto existe en el tenant y está **inactivo** | **200**, con `isActive: true` |
| El producto ya está **activo** | **422** `catalog.product.already_active` |
| El producto no existe, o es de otro tenant | **404** `catalog.product.not_found` |
| El tenant del llamador no es el de la ruta | **403** |
| Falta el permiso `catalog.product.manage` | **403** |

## Códigos de dominio nuevos

| Invariante | Código |
|---|---|
| El producto ya está activo | `catalog.product.already_active` |

Espeja `catalog.product.already_inactive`, que `Deactivate` ya lanza. El nombre no se elige
libre: se deriva del que existe.

## Evento de auditoría nuevo

| Operación | Evento |
|---|---|
| Activación exitosa | `catalog.product.activated` |

Por outbox, con `ICatalogAuditPublisher`, en la misma transacción que la escritura — el camino
que ya usa `DeactivateProductHandler`.

## Fuera de alcance

- **Reactivar tasas de impuesto.** `TaxRate` tiene la misma asimetría y el mismo callejón sin
  salida. Es su propio slice; queda anotado como deuda al cierre de éste.
- **Borrado real de producto.** `DELETE` de producto no existe y este slice no lo agrega.
  `deactivate`/`activate` es el par que conserva historia.
- **Revalidar `ImageFileId` contra `Storage` al activar.** `CAT-05` valida en la escritura;
  activar no cambia el campo. Si el archivo se borró mientras el producto estaba inactivo, ese
  es el mismo hueco que ya tiene cualquier producto activo, y no lo abre este slice.
- **Que el listado devuelva inactivos.** `GET /products` ya se comporta como se especificó en
  `CAT-02a`; este slice no toca la consulta.
- **El frontend.** Cualquier botón de reactivar es fila del ledger de `qep-frontend`.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-CAT-07-01` | Dado un producto **inactivo**, cuando se hace `POST .../activate`, entonces responde **200** con `isActive: true`, y la fila en base queda en `true` |
| `CA-CAT-07-02` | Dado un producto **ya activo**, entonces responde **422** `catalog.product.already_active` — no 200 silencioso ni 500 |
| `CA-CAT-07-03` | **Dado un producto inactivo, cuando se lo activa y después se hace `PUT`, entonces el `PUT` responde 200 — el callejón sin salida queda cerrado de punta a punta** |
| `CA-CAT-07-04` | Dado un `{productId}` **de otro tenant**, entonces responde **404**, y el producto ajeno **sigue inactivo** en base |
| `CA-CAT-07-05` | Dado un `{productId}` inexistente, entonces **404** |
| `CA-CAT-07-06` | Dado un llamador **sin** `catalog.product.manage`, entonces **403**, y el producto **sigue inactivo** |
| `CA-CAT-07-07` | Dada una activación exitosa, entonces deja **una** entrada `catalog.product.activated` en el outbox, en la misma transacción; y el **422** de `-02` **no** deja ninguna |
| `CA-CAT-07-08` | Dada una activación exitosa, entonces `Version` avanza en 1 y `UpdatedAt` toma la hora del `IClock` |
| `CA-CAT-07-09` | Dado un producto inactivo con código `X`, entonces crear otro con código `X` sigue dando **422** `catalog.product.code_taken`, y activar el primero sigue respondiendo **200** — el código nunca se liberó |

`CA-CAT-07-03` es el criterio que justifica el slice: sin él se puede entregar un endpoint que
responde 200 y deja el producto igual de inservible. `CA-CAT-07-09` ancla la afirmación sobre el
índice; si algún día alguien le agrega un filtro parcial, esta prueba se cae y avisa.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Unitaria | Invariantes de `Product.Activate`: activa uno inactivo, rechaza uno ya activo con el código correcto, avanza `Version` y `UpdatedAt` |
| Arquitectura | `CatalogLayerTests` se corre sin cambios — verifica que `Application` sigue sin EF Core |
| Integración | Los 9 CA contra PostgreSQL real |
| Runtime | Los criterios contra la API local, con la auditoría verificada en base |

**TDD obligatorio, RED antes que GREEN, y el RED tiene que fallar por el motivo correcto.** El
RED de integración debe dar `MethodNotAllowed` o el 404 de ruta —endpoint ausente—, no un 500.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **Olvidar registrar el handler en el composition root.** Se registran a mano, uno por uno, en `QepServiceCollectionExtensions`. Sin la línea, el endpoint resuelve, el dispatcher no encuentra a quién llamar y el síntoma es **500, no 404** | Lo detecta cualquiera de los 9 CA. Es el defecto que apareció en `CAT-06` y quedó documentado ahí mismo |
| **Olvidar `Version++` en `Activate`.** `Version` es token de concurrencia optimista: sin el incremento, dos escrituras que se solapan se pisan en silencio | `CA-CAT-07-08`, que afirma sobre `Version` y no sólo sobre `isActive` |
| Activar un producto de otro tenant | `CA-CAT-07-04`, verificando que la fila ajena **siga inactiva** después del 404 |
| Autorizar **después** de leer el repositorio | `CA-CAT-07-06`. `CatalogAuthorization.EnsureAuthorized` va primero en el handler; la revisión de `CAT-02` ya corrigió ese orden una vez |
| Devolver 200 al activar algo ya activo | `CA-CAT-07-02` |

## Alcance que este slice corre en el gate `CAT-00`

El gate ratificó **cinco** operaciones para `products` (`gate.md:109-113`) y ninguna es
`activate`. **Este slice agrega la sexta y hay que escribirla en el gate**, que vive en
`qep-frontend` — igual que se hizo con los cinco campos de `CAT-04` y con el `DELETE` de
`CAT-06`. No es opcional: sin eso el slice no cumple el DoD, que fue lo que mantuvo abierto a
`CAT-04` hasta el 2026-08-15.

## Evidencia de cierre

Se completa al terminar. Ver `qep-frontend/sdd/00-metodo/definition-of-done.md`.

### Tramo 1 — el dominio: `Product.Activate` (2026-08-16)

**RED literal, antes de escribir una línea de implementación.** Las cuatro unitarias se
escribieron contra un método que no existía, y el proyecto no compiló:

```txt
ProductTests.cs(356,17): error CS1061: "Product" no contiene una definición para "Activate"
ProductTests.cs(370,21): error CS1061: "Product" no contiene una definición para "Activate"
ProductTests.cs(384,17): error CS1061: "Product" no contiene una definición para "Activate"
ProductTests.cs(397,17): error CS1061: "Product" no contiene una definición para "Activate"
```

Un error por prueba, y **por el motivo correcto**: el método ausente, no una aserción que falla.

**Las cuatro pruebas, y qué criterio ancla cada una:**

| Prueba | Criterio | Qué sostiene |
|---|---|---|
| `ActivateTurnsProductActiveAndAdvancesUpdatedAt` | `CA-CAT-07-01` | Vuelve a activo y `UpdatedAt` toma la hora de la operación |
| `ActivateRejectsAnAlreadyActiveProduct` | `CA-CAT-07-02` | `catalog.product.already_active`, derivado de `already_inactive` |
| `ActivateAdvancesTheConcurrencyToken` | `CA-CAT-07-08` | `Version` pasa de 2 a 3. Sin esta, el olvido de `Version++` no lo nota ninguna aserción sobre `IsActive` |
| `ActivateReopensUpdate` | `CA-CAT-07-03` | Activar y **después** editar. Es el criterio que justifica el slice |

**GREEN:** `Correctas! - Con error: 0, Superado: 59, Total: 59` — eran 55 al cerrar `CAT-05`.

**`Activate` no revalida la unicidad del código, y eso es deliberado.** Quedó escrito en el
comentario del método, no sólo en este spec: `IX_products_tenant_code` es único **sin filtro
parcial**, así que desactivar nunca liberó el código y reactivar no puede colisionar. `CA-CAT-07-09`
es la prueba que ancla esa afirmación contra el día en que alguien le agregue un filtro parcial.

**Trampa de entorno que costó el primer intento:** `Api.exe` estaba corriendo (PID 4816) y
bloqueaba el build. Es el gotcha que el `CLAUDE.md` del repo ya documenta; se detuvo el proceso y
el build salió derecho.

### Tramos pendientes

| Tramo | Qué falta |
|---|---|
| Application | `ActivateProductCommand` y su handler, con `CatalogAuthorization.EnsureAuthorized` **antes** del repositorio y el outbox `catalog.product.activated` en la misma transacción |
| Api | `MapPost(".../activate")` con `catalog.product.manage`, **más el registro del handler en `QepServiceCollectionExtensions`** — sin esa línea el síntoma es 500, no 404 |
| Integración | Los 9 CA contra PostgreSQL real |
| Runtime | Los criterios contra la API local, con la auditoría verificada en base |
| Gate | La **sexta** operación de `products` en `CAT-00`, que vive en `qep-frontend`. Sin esto no cumple el DoD |
| Revisión | Pendiente de decidir: `CAT-05` y `CAT-06` se autorrevisaron, y este slice toca frontera de tenant y permisos |
