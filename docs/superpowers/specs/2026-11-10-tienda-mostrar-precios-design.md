# Tienda en modo catálogo: ocultar los precios — diseño (backend)

Fecha: 2026-11-10 · Repo: `qep-backend` · Specs hermanos: `qep-frontend/docs/superpowers/specs/2026-11-10-tienda-mostrar-precios-design.md`
y `qep-storefront/docs/superpowers/specs/2026-11-10-tienda-mostrar-precios-design.md`. Este documento manda el contrato HTTP.

Estado: aprobado por el owner el 2026-11-10. Las decisiones de §3.1 son del owner. Las de §3.2 son **decisión del
asistente**: se tomaron al escribir y el owner las tiene que confirmar. §8 lista lo que el código contradice o deja
abierto.

Reglas del workspace que aplican: TDD obligatorio (RED antes que GREEN, con evidencia literal); no se inventan
campos, estados, rutas, permisos ni códigos de error; ante discrepancia entre este documento y el código, gana el
código y se corrige el documento.

## 1. Intención y porqué

Algunos tenants quieren que su tienda pública funcione como **catálogo**: el comprador navega, agrega al carrito y
envía el pedido igual que hoy, pero la tienda **no muestra ningún precio ni monto**. El precio lo conversa el asesor
después.

Lo que cambia es sólo lo que se ve. El pedido se sigue preciando como hoy, con el `PricingMode` de la tienda. El owner
aceptó de forma explícita que los precios sigan visibles en el código fuente de la página y en las respuestas de red:
la API pública los sigue devolviendo.

## 2. Objetivos y no objetivos

**Objetivos**

- Un ajuste nuevo de la tienda, `showPrices`, que se guarda en `PUT /store/settings`, se lee en `GET /store/settings` y
  viaja en `GET /api/v1/public/stores/{host}`.
- Las tiendas existentes siguen mostrando precios sin que nadie haga nada.
- Un frontend viejo que guarda los ajustes durante la ventana de despliegue no recibe un 422 ni apaga el ajuste.

**No objetivos**

- Proteger el dato del precio. Los endpoints públicos de catálogo, ficha, carrito y recibo siguen devolviendo montos.
- Cambiar cómo se precia un pedido (`PublicCartPricing`, `StoreOrderGateway`, `IsRetail`).
- Ocultar precios por producto o por categoría.
- Cambiar la caché de los endpoints públicos.

## 3. Decisiones

### 3.1 Del owner

| # | Decisión |
| --- | --- |
| O1 | Es un ajuste **aparte**, no un tercer valor de `PricingMode`. `PricingMode` (`RetailOnly` \| `QuantityScales`, `Modules.Store.Domain/StorePricingMode.cs:4-8`) decide qué precio se cobra: lo usan `SubmitOrderRequest.cs:281` (preciar el checkout), `SubmitOrderRequest.cs:301` (`IsRetail` del pedido) y `QuotePublicCart.cs:45` (cotizar el carrito). Un valor «sin precios» dejaría el pedido sin regla de precio. |
| O2 | El ajuste vive en la sección «Catálogo» de `/settings/store` («Qué precios ve el cliente y qué categorías destacas.»), debajo del modo de precios. |
| O3 | Con precios ocultos, «Mis pedidos» del comprador en la tienda también oculta el total del pedido. |
| O4 | Ocultar es sólo visual: los precios pueden quedar en el HTML y en las respuestas del backend. |
| O5 | En modo catálogo el backend precia el pedido al enviarlo con el `PricingMode` de la tienda, como hoy: `RetailOnly` → detal, `QuantityScales` → escalas. No se fuerza detal. Quien no quiera escalas en una tienda catálogo elige «Sólo detal». Ni `SubmitOrderRequest` ni `QuotePublicCart` leen `ShowPrices`. |

### 3.2 Del asistente (aprobadas por el owner el 2026-11-10)

| # | Decisión |
| --- | --- |
| A1 | El campo se llama `showPrices` (bool) y su valor por defecto es `true`. |
| A2 | Migración: columna `show_prices boolean NOT NULL DEFAULT true` en `store.stores`. Las tiendas que ya existen quedan mostrando precios. |
| A3 | En `PUT /store/settings`, `ShowPrices` es `bool?` y **ausente (`null`) conserva el valor guardado**. Se aparta a propósito del patrón del resto del cuerpo (§4.3). |
| A4 | Entra a `OnlineStore.Configure`, a `StateKey()` y a `Snapshot()` de la auditoría. Sale en `StoreSettingsDto` y en `PublicStoreDto`. |
| A5 | Sin cambio de caché: un cambio del ajuste puede tardar unos minutos en verse en la tienda (§5). |
| A6 | Orden de despliegue: backend primero; frontend y storefront después, los dos toleran un backend viejo. |

## 4. Diseño

### 4.1 Punto de partida verificado en el código

- Agregado: `Modules.Store.Domain/OnlineStore.cs`. Propiedades en L22-69 (`PricingMode` L51, `AdvisorMembershipId`
  L54), `CreateEmpty` L71-77 (fija `PricingMode = RetailOnly`), `Configure` L83-164 (valida todo antes de asignar,
  compara `StateKey()` antes y después y sólo sube `Version` si cambió), `StateKey()` L167-170.
- Entrada del dominio: `Modules.Store.Domain/StoreSettingsInput.cs` (record de 15 campos, el último
  `AdvisorMembershipId`).
- Contrato del backoffice: `Modules.Store.Application/StoreDtos.cs`. `StoreSettingsRequest` L10-30, todo anulable;
  `StoreSettingsDto` L39-66. El comentario L6-7 fija la regla: «Todo lo que llega es anulable en el binding para que
  un campo ausente sea un 422 de validation.failed con su campo y no un 400 del deserializador».
- Caso de uso: `Modules.Store.Application/UpdateStoreSettings.cs`. Validador L17-94 (listas con `NotNull()`,
  `pricingMode` con `Must(TryParse)`, que rechaza `null`); handler L102-225, que carga la tienda (L149-150) **antes**
  de armar el `StoreSettingsInput` (L174-189); `Snapshot()` L305-330 (sólo nombres de campo salen a auditoría).
- Respuesta del backoffice: `Modules.Store.Application/StoreSettingsComposer.cs`, `ComposeAsync` L16-84.
- Contrato público: `Modules.Store.Application/PublicStoreDtos.cs`, `PublicStoreDto` L51-63 (ya lleva `PricingMode`);
  lo arma `GetPublicStore.cs` L22-35.
- Endpoint público: `Modules.Store.Api/PublicStoreEndpoints.cs`. `MapGet("/", GetStoreAsync)` L44; la respuesta sale
  con `CatalogCacheControl = "public, max-age=60, stale-while-revalidate=300"` (L20, aplicado en L193).
- Persistencia: `Modules.Store.Infrastructure/Persistence/StoreDbContext.cs`, `ConfigureStore` desde L69, tabla
  `store.stores` L72, `pricing_mode` L107-111, `advisor_membership_id` L114. Última migración del módulo:
  `20261110031040_DropStoreOrderRequests`. Ningún `FromSql` crudo lee `store.stores` (no hay `SELECT` que extender).
- Precedente de columna booleana con default en la base: `MessagingDbContext.cs:284`
  (`HasDefaultValue(false).HasSentinel(false)`) y los enums con `HasSentinel((T)(-1))` en `MessagingDbContext.cs:236,269`
  y `CustomersDbContext.cs:212`.

### 4.2 Contrato HTTP

**`GET /api/v1/tenants/{tenantId}/store/settings`** (y la respuesta de `PUT`, `publish` y `unpublish`, que comparten
`StoreSettingsDto`): campo nuevo `showPrices: boolean`, siempre presente.

**`PUT /api/v1/tenants/{tenantId}/store/settings`**: campo nuevo `showPrices: boolean | null`, opcional.

| Valor recibido | Efecto |
| --- | --- |
| `true` / `false` | Se guarda ese valor. |
| ausente o `null` | Se conserva el valor guardado; en una tienda nueva (`Version = 0`), el de `CreateEmpty`: `true`. |

No hay código de error nuevo. Un valor que no sea booleano (`"no"`, `1`) lo rechaza el deserializador antes del
handler, como hoy cualquier `Guid?` o `bool` mal formado del cuerpo; no se agrega regla al validador.

**`GET /api/v1/public/stores/{host}`**: `PublicStoreDto` gana `showPrices: boolean`, siempre presente. Los demás
endpoints públicos (`/products`, `/products/{id}`, `/cart/quote`, `/order-requests`, `buyer/orders`) **no cambian** y
siguen devolviendo montos (O4).

### 4.3 Por qué ausente conserva y no es 422 (A3)

El patrón del cuerpo es «anulable en el binding + regla del validador → 422 `validation.failed`» (`StoreDtos.cs:6-7`;
`pricingMode`, `currencies`, `salesCountries` y `featuredCategoryIds` en `UpdateStoreSettings.cs:51-82`). Aplicado a
`showPrices`, el frontend que esté en producción mientras se despliega el backend —que no conoce el campo— recibiría
un 422 en **cada** guardado de ajustes hasta que se despliegue el suyo. Y si en lugar de 422 se leyera `null` como
`false`, ese mismo guardado apagaría los precios de la tienda sin que nadie lo pidiera.

Por eso `null` significa «no lo toco». Se resuelve en el handler, que ya tiene la tienda cargada al armar la entrada:
`body.ShowPrices ?? store.ShowPrices`. El dominio recibe siempre un `bool`, nunca un `bool?`. El motivo se escribe en
el comentario del campo en `StoreSettingsRequest` (regla BFF del repo: sin la nota, el siguiente lo «arregla» al
patrón general).

Nota: no es la semántica de `AdvisorMembershipId`, que también es `Guid?` pero cuyo `null` **borra** el asesor
(`UpdateStoreSettings.cs:166-189` lo pasa tal cual). `showPrices` es el primer campo del cuerpo con «ausente =
conservar».

### 4.4 Cambios por archivo

- `StoreSettingsInput.cs`: gana `bool ShowPrices`.
- `OnlineStore.cs`: propiedad `public bool ShowPrices { get; private set; }`; `CreateEmpty` la fija en `true`;
  `Configure` la asigna (`ShowPrices = input.ShowPrices;`) junto a `PricingMode`; `StateKey()` la incluye. Sin ella en
  `StateKey()`, un `PUT` que sólo cambia el interruptor compara igual, devuelve `false`, no sube `Version`, no audita
  y **no escribe**: el cambio se pierde en silencio. No es bloqueo de publicación (`PublishBlockers` no cambia).
- `StoreDtos.cs`: `StoreSettingsRequest` gana `bool? ShowPrices` (con el comentario de §4.3); `StoreSettingsDto` gana
  `bool ShowPrices`.
- `UpdateStoreSettings.cs`: el validador no cambia. El handler arma la entrada con `body.ShowPrices ?? store.ShowPrices`.
  `Snapshot()` gana `["showPrices"] = store.ShowPrices.ToString()`, para que la auditoría
  `store.settings.updated` liste `showPrices` entre los campos cambiados.
- `StoreSettingsComposer.cs`: pasa `store.ShowPrices`.
- `PublicStoreDtos.cs` y `GetPublicStore.cs`: `PublicStoreDto` gana `bool ShowPrices`, con un comentario que diga que es
  sólo visual y que los montos siguen viajando en el resto del contrato (O4).
- `StoreDbContext.cs`: `store.Property(value => value.ShowPrices).HasColumnName("show_prices")` con default `true` en
  la base. **Cuidado con el centinela de EF** (§6, B1).
- Migración (nombre al builder, p. ej. `AddStoreShowPrices`), generada con el factory de diseño:
  `ALTER TABLE store.stores ADD show_prices boolean NOT NULL DEFAULT true`. El `Down` borra la columna.

## 5. Caché

`GET /api/v1/public/stores/{host}` responde `public, max-age=60, stale-while-revalidate=300`
(`PublicStoreEndpoints.cs:20,193`), y el storefront guarda la respuesta en su LRU por pod con el TTL de `max-age`
(`qep-storefront/src/lib/storefront-api/client.ts:190-197`). Encender o apagar el ajuste puede tardar unos minutos en
verse en la tienda. Es el mismo comportamiento que hoy tienen el nombre, los colores o el modo de precios. No se
cambia la caché (A5); el frontend lo dice en la ayuda del interruptor (spec hermano).

## 6. Casos borde

- **B1 — El centinela de EF se come el `false` del primer guardado.** Con `HasDefaultValue(true)` y el centinela por
  defecto de `bool` (`false`), EF omite la columna en el `INSERT` cuando el valor es `false` y la base pone `true`. El
  primer `PUT` de una tienda nueva (`existing is null`, `stores.Add`, `UpdateStoreSettings.cs:203-206`) con
  `showPrices: false` quedaría guardado como `true`. El mapeo tiene que evitarlo (centinela explícito, como los
  precedentes de §4.1, o default sólo en la migración); el builder elige y lo amarra la prueba de persistencia de §7.
- **B2 — `PUT` que sólo cambia el interruptor**: escribe, sube `Version`, audita `["showPrices"]`. Un `PUT` idéntico
  sigue sin escribir.
- **B3 — Frontend viejo durante el despliegue**: manda el cuerpo sin `showPrices`; se conserva el valor (§4.3).
- **B4 — Tienda nunca guardada** (`Version = 0`) y `PUT` sin `showPrices`: queda en `true`.
- **B5 — `QuantityScales` con precios ocultos**: el pedido se precia con escalas como siempre; sólo el storefront deja
  de mostrarlas.

## 7. Pruebas (TDD)

Cada ítem va RED antes que GREEN, con la salida literal de las dos corridas en el plan.

Unitarias (`tests/Modules/Store/Modules.Store.UnitTests`):

- `OnlineStoreTests`: `CreateEmpty` deja `ShowPrices = true`; `Configure` con sólo `ShowPrices` cambiado devuelve
  `true` y sube `Version` (la prueba que protege `StateKey()`); `Configure` idéntico con `ShowPrices = false` en las dos
  llamadas devuelve `false`.
- `StoreSettingsHandlerTests`: `showPrices: false` se guarda; `showPrices` ausente conserva `false` guardado y
  conserva `true` guardado; tienda nueva sin `showPrices` queda en `true`; la auditoría lista `showPrices` cuando
  cambia y no lo lista cuando no.
- `UpdateStoreSettingsValidatorTests`: un cuerpo sin `showPrices` sigue siendo válido (no hay 422).

Integración (`tests/Modules/Store/Modules.Store.IntegrationTests`, Testcontainers; un proyecto a la vez,
`-- xUnit.MaxParallelThreads=4`, con `$env:Seed__Enabled = "false"`):

- `StoreSettingsApiTests`: `PUT` con `showPrices: false` → `GET` devuelve `false`; `PUT` sin el campo después → sigue
  `false`; `PUT` con `true` → `true`; el primer `PUT` de una tienda nueva con `false` devuelve `false` al releer (B1).
- `StorePersistenceTests`: la columna `show_prices` existe, es `NOT NULL` con default `true`; una fila insertada antes
  de la migración queda en `true` al aplicarla; un `OnlineStore` nuevo con `ShowPrices = false` se lee de vuelta en
  `false` (B1).
- `PublicCatalogApiTests`: `GET /api/v1/public/stores/{host}` trae `showPrices` (`true` por defecto y `false` después
  de apagarlo); con `false`, `/products` y `/cart/quote` siguen trayendo montos (O4, prueba de no-objetivo).

`ArchitectureTests` sin cambios.

## 8. Contradicciones y pendientes encontrados en el código

- **Semántica de `null`.** Ver §4.3: `showPrices` estrena «ausente = conservar» en un cuerpo donde `null` es 422
  (listas, `pricingMode`) o «borrar» (`advisorMembershipId`, textos). Es intencional, pero el owner lo tiene que ver.

## 9. Despliegue

Backend primero (trae la migración). El frontend y el storefront viejos ignoran el campo nuevo. El frontend nuevo
contra un backend viejo lee `showPrices` ausente como `true` y el backend viejo ignora el campo del `PUT`; el
storefront nuevo contra un backend viejo lo lee como `true` (default del esquema). `docs/estado.md` se actualiza en
`develop` al mergear, no en la rama.

## 10. Frontend y storefront (resumen; detalle en los specs hermanos)

- `qep-frontend`: interruptor «Mostrar precios en la tienda» en la sección «Catálogo» de `/settings/store`, debajo
  del modo de precios; se manda siempre en el `PUT`. Las pantallas de pedidos de la consola no cambian.
- `qep-storefront`: con `showPrices = false` oculta todo monto (tarjetas, ficha, escalas, carrito y totales, total de
  «Mis pedidos»). El selector de moneda se queda visible, porque filtra el catálogo y fija la moneda y la cuenta de
  cobro del pedido. La cotización del carrito se sigue pidiendo para la disponibilidad, pero su BFF
  (`/api/cart/quote`) deja de devolver montos al navegador en modo catálogo. El backend no cambia por eso.
