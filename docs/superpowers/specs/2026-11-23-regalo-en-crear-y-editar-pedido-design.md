# Regalo al crear y editar un pedido — backend

**Fecha:** 2026-11-23 · **Estado:** borrador revisado contra el código (2026-11-23), con decisiones del owner pendientes ·
**Repo:** `qep-backend`. Spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-23-regalo-en-crear-y-editar-pedido-design.md`.
Extiende `2026-11-21-siigo-regalo-design.md` (misma carpeta). Plan: `docs/superpowers/plans/2026-11-23-regalo-en-crear-y-editar-pedido-backend.md`.

Pedido del owner, 2026-11-23: el detalle del pedido no edita el pedido, así que «Agregar regalo» sale de ahí. El regalo se marca
**en la lista de productos al crear y al editar**. **Se despliega primero el backend.**

## Lo que ya existe (verificado en `develop`, 2026-11-23)

- `OrderItem.IsGift` (columna `is_gift`, migración `20261121225312_AddOrderItemIsGift`). `OrderItem.Create` y `UpdateQuantity`
  fuerzan el descuento del regalo a `GiftDiscountPercentage = 100`; `ApplyDiscount` no lo baja.
- `Order.AddGift` (rechaza `Source == Wholesale` con `order.gift.wholesale_not_supported`, exige `IsEditable`, congela costo si el
  pedido ya está pagado) y `Order.AddItemCore(..., isGift)`, que rechaza la misma **(variante, regalo)** dos veces
  (`order.item.duplicate_product`). `Order.RemoveItem` sólo protege la última línea **pagada** (`order.item.last_item_required`).
- `AddOrderGift` (`POST …/orders/{id}/gifts`) y `RemoveOrderGift` (`DELETE …/orders/{id}/gifts/{itemId}`), con `OrderManage`,
  sin `If-Match`. Precian con `isRetail: true` (sin escalas) al precio de lista del catálogo. Historial `ItemsChanged` con
  `OrderChangeSummary.GiftAdded`/`GiftRemoved`; auditoría `quotation.order.gift_added`/`gift_removed`.
- `OrderItemEdits.ApplyAsync` (la lista deseada del `PUT` y de `POST …/orders/{id}/preview`) **sólo indexa líneas pagadas**: los
  regalos quedan intactos. La prueba `OrderGiftHandlerTests.PutItemsWithTheSameVariantKeepsTheGift` lo fija.
- `OrderPricingRecalculation` excluye los regalos de escalas y mínimo de compra; `ApplyGroupDiscounts` los salta;
  `ReplaceExternalLines`/`SameItems` (Shopify) los conservan; `OrderResponseComposer` y `OrderMapping` devuelven `isGift`; Siigo
  (`SiigoInvoiceBuilder`) y `ExternalOrderInvoicingService` ya facturan el regalo como obsequio.
- La base **no** tiene índice único por variante (`IX_order_items_variant` es normal): **no hace falta migración**.

## Hallazgos que cambian el borrador anterior

1. **Compatibilidad: un `PUT` que trate la lista como estado deseado de los regalos borra regalos.** Con el backend desplegado
   primero, el frontend actual manda sólo líneas pagadas (`paidLines`). Si la ausencia de regalos en la lista los quitara, la
   primera edición de un pedido con regalos los borraría sin aviso. El contrato tiene que distinguir «no toques los regalos» de
   «estos son los regalos». Ver D-1.
2. **Crear no pasa por `OrderItemEdits`.** `POST /orders` y `POST /orders/preview` van por `OrderDraftBuilder.PriceLinesAsync` →
   `Order.CreateManual`/`Order.Preview` → `Order.Build` → `AddItemCore` sin `isGift`, y `Order.CandidateLines` no se usa ahí.
   `OrderLineInput` no tiene `IsGift`.
3. **Hay dos cálculos previos**, no uno: `POST /orders/preview` (crear, `PreviewOrder`) y `POST /orders/{id}/preview` (editar,
   `PreviewOrderEdits`). Los dos tienen que aceptar regalos.
4. **El duplicado no llega como `order.item.duplicate_product`.** `OrderDraftValidator` y `OrderEditsValidator` lo rechazan antes,
   con 422 `validation.failed` y la clave `Items` en `errors`. El código de dominio sólo sale si un validador no lo ve.
5. **`unitPrice` en el alta se ignora hoy, no es 422.** `PriceLinesAsync` no lo lee. Con la forma de D-1 (A) el regalo no tiene
   campo de precio, así que la regla del borrador («`unitPrice` en regalo → 422») sobra.
6. **El historial no tiene «tipo propio» para el regalo**: es `OrderHistoryEventType.ItemsChanged` con el texto
   `GiftAdded`/`GiftRemoved`. Falta un texto para el cambio de cantidad de un regalo (hoy no existe: quitar y volver a agregar).
7. **Shopify y la tienda sí admiten regalos puestos en QEP**: `AddGift` sólo bloquea `Wholesale`. «Shopify sin regalos» quiere
   decir que la plataforma no los manda, no que el pedido no pueda tenerlos.
8. **Mínimo un producto pagado** ya lo exigen los validadores (`Items` no vacío) en crear y editar, y el dominio al quitar. Con
   D-1 (A) un pedido sólo de regalos no se puede armar: cierra la pregunta 2 del borrador.

## Diseño (asume D-1 = A, la recomendación)

### Contrato HTTP

- `CreateOrderRequest` (y por herencia `PreviewOrderRequest`) y `SaveOrderEditsRequest` (y por herencia
  `PreviewOrderEditsRequest`) ganan una propiedad **`Gifts`** (JSON `gifts`): lista de `OrderGiftLineRequest(Guid VariantId, decimal
  Quantity)`. Es una propiedad `init` del record base, no un parámetro posicional, para que los dos records derivados la hereden sin
  tocar sus constructores (el mismo recurso que `SaveOrderEditsRequest.OperationCityId`).
- **Crear** (`POST /orders`, `POST /orders/preview`): `gifts` ausente o `null` = sin regalos.
- **Editar** (`PUT /orders/{id}`, `POST /orders/{id}/preview`): `gifts` **ausente o `null` conserva** los regalos guardados (un
  frontend viejo no los borra, como `shipping`, `billing` y las retenciones). Presente —también `[]`— es la lista **entera** de
  regalos deseados, por variante: agrega, cambia cantidad y quita.
- `items` no cambia: sigue siendo la lista de líneas **pagadas**, por variante. La misma variante puede estar en `items` y en
  `gifts` a la vez (una pagada y una de regalo).
- Un regalo no lleva precio ni descuento: se precia al precio de lista del catálogo con descuento fijo del 100 %, sin escalas ni
  mínimo, y el IVA lo asume la empresa (spec 2026-11-21, sin cambios).

### Validación (422 `validation.failed`)

- `gifts[i].variantId` no vacío, `gifts[i].quantity > 0`.
- Una variante una sola vez dentro de `gifts` (clave `Gifts` en `errors`, mensaje «La misma variante no puede ir más de una vez
  como regalo.»). La regla de `items` no cambia.
- `items` sigue exigiendo al menos una línea: un pedido sólo de regalos es 422.

### Reglas de dominio y aplicación

- **Crear:** `OrderLineInput` gana `bool IsGift = false`; `Order.Build` lo pasa a `AddItemCore(..., isGift: line.IsGift)`.
  `OrderDraftBuilder` precia los regalos aparte (`PriceGiftLinesAsync`, con `isRetail: true` como `AddOrderGift`) y los agrega
  después de las pagadas. `EnsureCreatable` sigue exigiendo al menos una línea (la validación ya exige una pagada). Un pedido
  manual nunca es mayorista, así que crear no se cruza con `wholesale_not_supported`.
- **Editar:** clase nueva `OrderGiftEdits.ApplyAsync`, que corre **después** de `OrderItemEdits.ApplyAsync` en `SaveOrderEdits` y en
  `PreviewOrderEdits`, sólo si `Gifts` no es `null`. Concilia contra `order.Items.Where(IsGift)` por `VariantId`:
  - nueva → `order.AddGift(...)` (precio de lista, `isRetail: true`, costo cargado con `OrderLineCostsLoader` si el pedido está
    pagado). En un pedido mayorista `AddGift` responde `order.gift.wholesale_not_supported`.
  - misma variante y otra cantidad → `order.UpdateItemQuantity(item.Id, qty, item.DiscountPercentage,
    item.ProductTaxPercentage ?? item.TaxPercentage, …)`: no lee el catálogo ni reprecia (el precio de lista queda el guardado;
    el descuento sigue en 100 %).
  - misma variante y misma cantidad → no se toca.
  - ausente de la lista → `order.RemoveItem(item.Id, …)` (quitar un regalo nunca dispara `last_item_required`).
- `OrderItemEdit` gana `bool IsGift = false`. `SaveOrderEdits` suma las ediciones de regalo a las de líneas, así que «nada cambió»
  (sin versión ni historial) las cuenta, y `CaptureProductSnapshots` corre también por un regalo agregado.
- Un pedido no editable sigue rechazando todo (`order.order.not_editable`, antes de tocar nada). El `If-Match` del `PUT` cubre
  también los regalos: con la edición deja de haber un camino de regalos sin control de concurrencia.

### Historial y auditoría (`ItemsChanged`, como hoy)

| Edición de regalo | Texto | Auditoría |
| --- | --- | --- |
| Agregado | `OrderChangeSummary.GiftAdded` («Agregó de regalo X x2.») | `quotation.order.gift_added` (existe) |
| Cantidad | **nuevo** `OrderChangeSummary.GiftQuantityChanged` («Cambió la cantidad del regalo X: 1 → 2.») | **nuevo** `quotation.order.gift_updated` |
| Quitado | `OrderChangeSummary.GiftRemoved` («Quitó el regalo X.»), con el nombre de la foto de la línea | `quotation.order.gift_removed` (existe) |

El alta de un pedido con regalos deja sólo «Creado a mano desde Pedidos.», como hoy con las líneas pagadas.

## Qué no cambia

- `POST /quotations/{id}/order` (conversión), cotizaciones, POS, tienda pública, mayorista y la sincronización de Shopify: ninguno
  manda regalos. El regalo nace al crear a mano o al editar.
- Escalas, piso global, mínimo de compra, retenciones, IVA del pedido, Siigo, costos e inventario: ya tratan el regalo (spec
  2026-11-21). El total y el neto no cambian por un regalo.
- `AddOrderGift`/`RemoveOrderGift` y sus rutas se conservan mientras el frontend las use (D-2).

## Pruebas (TDD, RED primero)

Unitarias (`Modules.Quotations.UnitTests`): validadores (duplicado en `gifts`, cantidad 0, variante vacía, sólo regalos);
`Order.CreateManual` con una pagada y un regalo de la misma variante; `OrderDraftBuilder` precia el regalo a lista con 100 %;
`OrderGiftEdits` (agrega, cambia cantidad sin repreciar, quita, deja igual, `null` no toca, mayorista 422, pedido pagado congela
costo); `SaveOrderEdits` escribe el historial y la auditoría de la tabla. La prueba existente
`PutItemsWithTheSameVariantKeepsTheGift` **sigue en verde sin cambios** (es la garantía de compatibilidad). Integración
(`Modules.Quotations.IntegrationTests`, filtrada por nombre): una por ruta (`POST /orders`, `POST /orders/preview`, `PUT`, `POST
/orders/{id}/preview`).

## Decisiones pendientes del owner

1. **D-1. Forma del contrato.** **A (recomendada):** lista aparte `gifts`; en la edición, ausente conserva. **B (el borrador):**
   `isGift` por línea en `items` y la lista entera como estado deseado; para no borrar regalos con el frontend viejo exige además
   una bandera de petición (p. ej. `giftsIncluded`) o desplegar los dos repos a la vez. Recomiendo A: sigue la convención del
   `PUT` («ausente conserva»), no toca la clave de `items` ni sus validadores, y deja en verde la prueba de compatibilidad.
2. **D-2. `POST/DELETE …/gifts`.** Recomiendo borrarlos (ruta, comando, validador, request y sus pruebas) en un cambio aparte
   cuando el frontend nuevo esté en `main`: después no hay quien las llame (no son API pública) y duplican la regla.
3. **D-3. Pedido sólo de regalos.** Recomiendo confirmar lo que ya hace el código: al menos una línea pagada (validadores y
   `RemoveItem`). Un regalo sin venta no tiene factura que lo lleve.
