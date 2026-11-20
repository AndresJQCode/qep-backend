# Lista de precios del mayorista: mostrar u ocultar precios al distribuidor (2026-11-19)

Spec del frontend: `qep-frontend/docs/superpowers/specs/2026-11-19-wholesale-show-prices-design.md`.
Plan: [`2026-11-19-mayorista-mostrar-precios-backend.md`](../plans/2026-11-19-mayorista-mostrar-precios-backend.md).

## Estado de este documento

- **Primera tanda (hecha, en la rama):** `PriceList.ShowPrices` + migración, contrato `POST`/`PUT` de la lista, catálogo del
  mayorista enmascarado y **vista previa** enmascarada con el flag vivo. Sigue valiendo tal cual (D-1, D-2, D-3, D-7).
- **Segunda tanda (esta revisión, decisión del owner 2026-11-19):** la orden vinculada de una lista oculta **nace sin precio** y
  recibe los precios **sólo** con la aprobación del mayorista. **Reemplaza** D-4, D-5 y D-6 de la revisión anterior y todo el
  enfoque «`prices_hidden` + enmascarar después»: no hay columna `prices_hidden` y no hay nada que enmascarar en detalle, lista,
  PDF ni correo, porque el monto no existe en la base del distribuidor.
- **No se mergea la primera tanda sola:** con ella un borrador guardado con la lista oculta guarda su costo y lo muestra. La rama
  se mergea entera, con la segunda tanda.

## Problema

Un distribuidor que arma una orden de compra a un mayorista vinculado ve precio base, descuento, precio de lista y costo en el
catálogo, subtotales y totales en la vista previa y en el detalle. El owner quiere que el mayorista pueda hacer que **un grupo de
distribuidores pida sin ver precios** (pedido por catálogo; el precio se conoce al aprobar).

## Decisiones

- **D-1. Va en la lista de precios, no en un ajuste global** (owner). `PriceList.ShowPrices` (`bool`, por defecto `true`); se
  edita con el resto de la lista, con `wholesale.price_list.manage`, el mismo `If-Match` y la misma auditoría
  (`wholesale.price_list.updated`, `showPrices` en los campos escritos). Sin ajuste global ni excepción por distribuidor. *(Vigente.)*
- **D-2. `showPrices` es opcional en el cuerpo.** `POST` sin el campo → `true`; `PUT` sin el campo **conserva** el valor. *(Vigente.)*
- **D-3. Ocultar se hace en el servidor.** El JSON nunca trae el monto; `showPrices: false` lo dice explícito; `showPrices` ausente
  = backend viejo = se muestran. *(Vigente.)*
- ~~D-4. Wholesale entrega los precios y Compras enmascara en su borde; el costo queda guardado.~~ **Reemplazada por D-8.** Lo que
  sigue valiendo de D-4: Wholesale entrega precio **y** flag (`WholesaleTerms.ShowPrices`) y es Compras quien decide.
- ~~D-5. `PurchaseOrder.PricesHidden` congela el flag.~~ **Reemplazada por D-9 y D-11.**
- ~~D-6. Los montos aparecen al aprobar, enmascarando hasta un paso `Confirmed`.~~ **Reemplazada por D-10.**
- **D-7. Una orden externa nunca queda sin precio.** Exige costo (422 `Lines[i].UnitCost`, como hoy). *(Vigente.)*
- **D-8. La orden vinculada de una lista oculta se guarda sin montos.** En el alta, la edición y el envío, con el flag en `false`,
  cada línea se guarda con `unit_cost`, `list_base_price`, `list_discount_percent` y `list_unit_price` en `NULL`; la orden no tiene
  totales. El precio que el mayorista conoce (su pedido, `Order` con `Source = Wholesale`) **no se copia de vuelta** al enviar.
  El dominio es el único punto que descarta los montos: `PurchaseOrder.Create`, `Replace` y `SendToWholesaler` reciben
  `bool withPrices` y, en `false`, arman las líneas sin montos (y lanzan `ArgumentException` si la orden es externa: es un error
  de programación, como el modo mezclado de hoy).
- **D-9. Cada escritura usa el flag vivo de ese momento; nada se congela.** Alta y edición: el de `IWholesaleCatalog.PriceAsync`
  (ya existe en `PurchaseOrderLineBuild.ShowPrices`). Envío: el de los términos con que el intake crea el pedido
  (`WholesaleIntakeResult.ShowPrices`, nuevo). Como el envío **reemplaza** las líneas por las del pedido (D-8 de fase 3), el envío
  decide en última instancia. Después del envío ninguna escritura del distribuidor toca montos: sólo la aprobación.
- **D-10. Los precios llegan con la aprobación y son los confirmados.** El evento de aprobación ya trae las líneas finales; el
  reconcile (`ApplyWholesalerApproval`) escribe, como hoy, `confirmed_quantity` y `confirmed_unit_cost`. **`unit_cost` no se
  rellena**: el distribuidor nunca pidió a un precio, y fabricarlo haría que una orden sin precio «cambiara de precio» al aprobar.
  Desde ahí detalle, lista, PDF y correo muestran costos y totales confirmados, y D-18 (crear productos con el costo confirmado)
  funciona como hoy.
- **D-11. «Sin precio» se deriva, no se guarda.** `PurchaseOrder.HasPrices => _lines.All(line => line.Subtotal is not null)`, en
  el dominio. Equivale a: orden vinculada con alguna línea sin `unit_cost` que no tenga todavía `confirmed_unit_cost` (una línea
  rechazada, cantidad confirmada 0, cuenta como con precio: subtotal 0). Una externa siempre es `true` (D-7). El DTO lo expone como
  `showPrices` (mismo nombre que catálogo y vista previa).
- **D-12. Una línea sin precio pedido se compara sólo por cantidad.** `Status` es `Unchanged` si la cantidad confirmada es la
  pedida y (`UnitCost` es nulo **o** el costo confirmado es el pedido); `Changed` si la cantidad difiere; `Rejected` con 0; `Added`
  igual que hoy. Así `confirmedWithChanges` y el banner de cambios hablan de lo que el distribuidor pidió (cantidades), no de un
  precio que nunca vio.
- **D-13. Donde no hay monto se imprime «—».** PDF y correo se arman desde el detalle (`PurchaseOrderPdfMapper`); un monto `null`
  sale como «—» en costo, subtotal y totales. La plantilla Typst y la del correo reciben texto: no cambian.
- **D-14. El reintento del envío que encuentra el pedido ya creado (D-8 de fase 3) usa el flag vivo, y sin lista cierra.**
  `WholesaleOffers.OffersForOrderAsync` devuelve además `ShowPrices` de la lista del distribuidor; sin relación o sin lista,
  `false`: sin información la orden queda sin precio y los recibe al aprobar, nunca un precio que no se debía ver.

## Modelo de datos

| Columna (`purchases.purchase_order_lines`) | Hoy | Con este cambio |
| --- | --- | --- |
| `unit_cost` `numeric(14,2)` | `NOT NULL` | **nullable** (migración `AllowPricelessPurchaseOrderLines`: un `AlterColumn` a `nullable: true`, nada más) |
| `list_base_price`, `list_discount_percent`, `list_unit_price` | nullable | sin cambio (ya admiten `NULL`) |
| `confirmed_quantity`, `confirmed_unit_cost` | nullable | sin cambio |

Sin columna nueva, sin backfill: las filas existentes tienen costo y siguen igual. `wholesale.price_lists.show_prices` ya está
(primera tanda). El `Down` de la migración falla si hay filas con `NULL`: es la señal correcta (ver Compatibilidad).

Aritmética del dominio (`PurchaseOrderLine`, `PurchaseOrder`; `PurchaseOrderMath` no cambia):

- `UnitCost` → `decimal?`. `Confirm(decimal quantity, decimal? unitCost)`.
- `RequestedSubtotal` → `decimal?`: `UnitCost is { } c ? Subtotal(Quantity ?? 0, c) : null`.
- `Subtotal` → `decimal?`: con `ConfirmedQuantity` = 0, `0`; con otra cantidad confirmada, `Subtotal(q, ConfirmedUnitCost ??
  UnitCost)` si ese costo existe, si no `null`; sin confirmar, `RequestedSubtotal`. (Para las filas de hoy da lo mismo que antes.)
- `Tax`, `RequestedTax` → `decimal?`, nulos si su subtotal lo es.
- `Totals()` y `RequestedTotals()` → `PurchaseOrderTotals?`: `null` si alguna línea no tiene el subtotal correspondiente.
- `ApplyWholesalerApproval`: una línea que el mayorista quitó se confirma con `(0, UnitCost)`, que en una orden sin precio es
  `(0, null)`.
- `HasPrices` (D-11).

## Comportamiento por operación (lista oculta)

| Operación | Qué hace |
| --- | --- |
| Alta (`POST /orders`) | Borrador con cantidades y sin montos. Responde el detalle con `showPrices: false`. |
| Edición (`PUT /orders/{id}`) | Rearma las líneas con el flag vivo (D-9). |
| Vista previa | Sin cambio (primera tanda): calcula en memoria y omite montos con el flag vivo. |
| Envío (`POST …/send`) | El intake crea el pedido en el mayorista **a su precio de lista** (como hoy) y la orden adopta sus líneas **sin montos**. Correo y respuesta con «—»/`null`. |
| Reenvío del correo | Mismo documento: «—» antes de aprobar, montos confirmados después. |
| Cancelar (distribuidor o evento del mayorista) | No toca montos. Una orden cancelada antes de aprobar queda sin precio para siempre. |
| Aprobación (evento) | Escribe cantidades y costos confirmados (D-10); la orden pasa a tener precio. |
| Recibir | Como hoy (ya tiene precio). |

### Casos borde (decididos)

1. **El mayorista cambia el switch entre el borrador y el envío.** Manda el envío (D-9): borrador visible + lista oculta al
   enviar → la orden enviada queda sin montos; borrador oculto + lista visible al enviar → la orden enviada adopta los montos del
   pedido, como hoy.
2. **Borrador guardado visible, después la lista se oculta y el distribuidor edita.** El siguiente guardado deja las líneas sin
   montos. Lo que ya vio en pantalla no se puede des-ver; la base deja de tenerlo.
3. **Borrador guardado oculto, después la lista se muestra.** No cambia solo: el siguiente guardado (o el envío) lo pone con
   precio. No hay reescritura en masa ni trabajo en segundo plano.
4. **Una orden ya enviada, aprobada o recibida nunca se vuelve a quedar sin precio**, ni gana precio por un cambio del switch:
   sólo un borrador se reescribe (`EnsureDraft`), y después del envío la única escritura de montos es la aprobación.
5. **Reintento de envío con el pedido ya creado:** flag vivo; sin lista o sin relación, sin precio (D-14).
6. **La aprobación trae líneas agregadas** (`Added`): llegan con su costo confirmado, como hoy. **Rechazadas**: subtotal 0.
   **Cambiadas**: sólo por cantidad (D-12).
7. **Moneda distinta en la aprobación** (M2 de fase 3): se ignora como hoy; la orden sigue sin precio.

## Contrato

### Lista de precios (mayorista) — sin cambio desde la primera tanda

`POST /api/v1/tenants/{tenantId}/wholesale/price-lists` y `PUT …/price-lists/{priceListId}` (con `If-Match`) aceptan
`showPrices` opcional; `GET` y las respuestas lo traen en `PriceListSummaryDto` y `PriceListDetailDto`.

```json
{ "name": "Catálogo sin precios", "discountPercent": 12, "visibility": "AllActive", "categoryIds": [], "showPrices": false }
```

### Catálogo y vista previa (distribuidor) — sin cambio desde la primera tanda

`GET …/purchases/suppliers/{supplierId}/catalog`: `basePrice`, `discountPercent`, `listPrice`, `unitCost` y
`priceList.discountPercent` en `null`, `showPrices: false`. `POST …/purchases/orders/preview`: `lines[].subtotal`,
`lines[].listPrice` y `totals` en `null`, `showPrices: false`.

### Detalle (`GET …/purchases/orders/{id}`, y las respuestas de `POST`, `PUT`, `send`, `cancel`, `receive`)

Campos que pasan a ser anulables: `lines[].unitCost`, `lines[].subtotal`, `totals`, `requestedTotal`. Campo nuevo al final:
`showPrices` (`order.HasPrices`). Orden enviada sin precio:

```json
{
  "id": "…", "number": "OC-0007", "status": "Sent", "confirmedWithChanges": false, "version": 3,
  "supplier": { "id": "…", "name": "Textiles Andinos", "isActive": true, "isLinked": true },
  "currency": "COP", "notes": null, "createdBy": { "memberId": "…", "name": "Ana" }, "createdAt": "2026-11-19T15:02:00Z",
  "lines": [
    { "id": "…", "variantId": null, "code": "CAM-M", "name": "Camiseta – M", "quantity": 120, "confirmedQuantity": null,
      "unitCost": null, "confirmedUnitCost": null, "taxRatePercent": 19, "lineStatus": "Pending", "subtotal": null,
      "supplierVariantId": "…", "listPrice": null, "createdInCatalog": false }
  ],
  "totals": null, "requestedTotal": null,
  "history": [ { "status": "Draft", "at": "…", "actorName": "Ana" }, { "status": "Sent", "at": "…", "actorName": "Ana" } ],
  "allowedActions": ["cancel", "resend_email"], "mode": "Linked", "linked": { "orderNumber": "PED-0042" },
  "changes": { "changedLines": 0, "addedLines": 0, "rejectedLines": 0, "createdProducts": 0 },
  "email": { "status": "Sent", "to": "pedidos@textiles.co", "at": "…", "recipient": "pedidos@textiles.co" },
  "showPrices": false
}
```

La misma orden después de la aprobación (el mayorista confirmó 100):

```json
{
  "status": "Confirmed", "confirmedWithChanges": true,
  "lines": [
    { "code": "CAM-M", "quantity": 120, "confirmedQuantity": 100, "unitCost": null, "confirmedUnitCost": 10000,
      "taxRatePercent": 19, "lineStatus": "Changed", "subtotal": 1000000, "listPrice": null, "createdInCatalog": true }
  ],
  "totals": { "subtotal": 1000000, "tax": 190000, "total": 1190000 }, "requestedTotal": null,
  "changes": { "changedLines": 1, "addedLines": 0, "rejectedLines": 0, "createdProducts": 1 },
  "showPrices": true
}
```

`requestedTotal` queda `null` para siempre en una orden que nació sin precio: no hubo total pedido. Toda orden externa, y toda
vinculada de una lista visible, responde como hoy con `showPrices: true`.

### Lista (`GET …/purchases/orders`)

`items[].total` pasa a `decimal?` y cada fila suma `showPrices`:

```json
{ "id": "…", "number": "OC-0007", "status": "Sent", "confirmedWithChanges": false,
  "supplier": { "id": "…", "name": "Textiles Andinos" }, "currency": "COP", "total": null,
  "createdAt": "…", "lineCount": 3, "isLinked": true, "mode": "Linked", "showPrices": false }
```

### PDF (`GET …/orders/{id}/pdf`) y correo al proveedor

Mismo documento (`PurchaseOrderPdfMapper` desde el detalle). Sin precio: costo, subtotal, «Subtotal», «IVA» y «Total» como «—»;
código, nombre, cantidad y tasa como hoy. El correo de una orden sin precio sale sin la tabla de montos (decisión del owner, abajo); el PDF adjunto sí lleva «—».

### Lo que este cambio nunca toca: los documentos del mayorista

Cotización, pedido (`Order` con `Source = Wholesale`, con su precio congelado), PDF, envío por WhatsApp o correo del mayorista
**siempre llevan precios**. El intake sigue creando el pedido al precio de lista. Una prueba de regresión ya lo fija (primera
tanda): con la lista oculta, el PDF del pedido del mayorista trae montos. Si el mayorista le manda su documento al distribuidor,
éste ve los precios por ese camino: es decisión del mayorista.

## Seguimiento (no se construye)

1. **El correo y el PDF le llegan al mayorista con «—»** mientras la orden no esté aprobada. No es fuga: el mayorista tiene los
   precios en su pedido.
2. **Auditoría y outbox del distribuidor** (`purchases.order.*`) no llevan montos hoy; si un día los llevan, que salgan de la orden
   (sin precio = sin monto).
3. **Reportes de Compras:** no existen; uno nuevo que sume costos tiene que tratar `unit_cost` nulo como «sin precio», no como 0.
4. **Ajuste global u override por distribuidor:** fuera de este corte (owner).

## Compatibilidad y despliegue

- **Migración:** `AlterColumn` de `unit_cost` a nullable. Un pod viejo escribe siempre valor; **leer** una fila con `NULL` lo haría
  fallar al materializar. Esas filas sólo existen después de que un mayorista oculte una lista, y el switch sólo está en el
  frontend nuevo: el orden es **backend (con migración) y enseguida frontend**, en el mismo despliegue de la rama entera.
- **Rollback:** sólo hacia adelante una vez que alguna lista se ocultó: el backend viejo no puede leer órdenes sin precio y el
  `Down` falla con filas `NULL`. Antes de ocultar la primera lista, el rollback es libre.
- **Frontend viejo** contra este backend: idéntico a hoy mientras ninguna lista esté oculta; con una oculta leería `null` donde
  espera número (detalle en blanco). Por eso el frontend nuevo sale enseguida y es null-safe en detalle y lista.

## Decisión del owner sobre el correo (2026-11-20)

1. **Correo al proveedor al enviar una orden sin precio: sale sin la tabla de montos.** Conserva código, producto y cantidad; no
   lleva las columnas «Costo» y «Subtotal» ni las líneas de Subtotal, IVA y Total, y tampoco una grilla de «—». El PDF adjunto
   sí imprime «—» (D-13). Implementación: `PurchaseOrderEmailInput.ShowsAmounts` (por defecto `true`), que el sender toma de
   `PurchaseOrderPdfDocument.ShowsAmounts` (no viaja a la plantilla Typst) y el mapper deriva de `detail.showPrices`.
