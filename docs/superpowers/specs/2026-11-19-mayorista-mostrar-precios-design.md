# Lista de precios del mayorista: mostrar u ocultar precios al distribuidor (2026-11-19)

Spec del frontend: `qep-frontend/docs/superpowers/specs/2026-11-19-wholesale-show-prices-design.md`.
Plan: [`2026-11-19-mayorista-mostrar-precios-backend.md`](../plans/2026-11-19-mayorista-mostrar-precios-backend.md).

## Alcance de la primera tanda (owner, 2026-11-19)

Se construye primero un corte reducido; lo demás queda como **Segunda tanda** y no se implementa todavía.

- **Entra:** PriceList.ShowPrices (D-1, D-2, D-3, D-4, D-7), el catálogo del mayorista enmascarado y la **vista previa** de la orden
  enmascarada (POST …/purchases/orders/preview). La vista previa lee el flag **vivo** de la lista del mayorista en el momento de
  la consulta; no hay flag guardado en la orden.
- **Segunda tanda:** D-5 (purchases.purchase_orders.prices_hidden), D-6 (los precios aparecen al aprobar) y todo el bloque de
  detalle, lista, PDF y correo de la orden.
- **Fuga abierta hasta la segunda tanda:** una orden o borrador ya guardado **sigue mostrando sus montos** en el detalle, la lista,
  el PDF y el correo, aunque la lista del mayorista esté oculta. Sólo el catálogo y la vista previa los esconden.

## Problema

Un distribuidor que arma una orden de compra a un mayorista vinculado ve el catálogo con precio base, descuento, precio de
lista y costo (`GET …/purchases/suppliers/{supplierId}/catalog`), la vista previa con subtotales y totales, y el detalle de la
orden. El owner quiere que el mayorista pueda hacer que **un grupo de distribuidores pida sin ver precios** (pedido por
catálogo; el precio se conoce al aprobar).

## Decisiones

- **D-1. Va en la lista de precios, no en un ajuste global** (owner, 2026-11-19). `PriceList.ShowPrices` (`bool`, por defecto
  `true`). Cada distribuidor ya apunta a una lista (`Distributor.PriceListId`), así que la lista es el grupo natural y no hace
  falta un agregado nuevo, ni un permiso nuevo, ni otra pantalla: se edita con el resto de la lista, con
  `wholesale.price_list.manage`, el mismo `If-Match` y la misma auditoría (`wholesale.price_list.updated`, `showPrices` en los
  campos escritos). Sin ajuste global ni excepción por distribuidor en este corte.
- **D-2. `showPrices` es opcional en el cuerpo.** `POST` sin el campo crea la lista con `true`; `PUT` sin el campo **conserva**
  el valor guardado. Así un frontend viejo que edita el nombre de una lista oculta no la vuelve a mostrar.
- **D-3. Ocultar se hace en el servidor.** Con la lista oculta, los montos viajan `null` y la respuesta suma un `showPrices:
  false` a nivel de página u orden. `showPrices` ausente = backend viejo = se muestran. Esconderlos sólo en la UI no es
  seguridad: el JSON los traería.
- **D-4. Wholesale sigue entregando los precios a Compras; Compras enmascara en su borde.** La orden necesita el costo para
  guardarse (D-9 de fase 3: `UnitCost` = lista sin IVA), así que `WholesaleOffers` devuelve precios **y** el flag
  (`WholesaleTerms.ShowPrices`), y los DTOs de Compras los ponen en `null`. El costo queda guardado en la base del distribuidor,
  nunca en una respuesta mientras esté oculto.
- **D-5. [Segunda tanda] La orden congela el flag al guardarse.** `PurchaseOrder.PricesHidden` (columna `prices_hidden`) se fija en el alta y en
  cada edición de una orden vinculada con lo que dijo el catálogo del mayorista en ese momento, igual que la orden congela la
  lista (D-9 de fase 3). Detalle, lista, PDF y correo leen la columna, sin cruzar tenants en cada lectura (y sin depender de
  que la relación siga `Active`). Consecuencia aceptada: si el mayorista cambia el switch, un borrador ya guardado cambia al
  volver a guardarse; una orden enviada conserva el valor con que salió.
- **D-6. [Segunda tanda] Los precios aparecen al aprobar.** Una orden con `PricesHidden` muestra montos cuando ya tiene un paso `Confirmed` en
  su historial (lo escribe el evento de aprobación del mayorista). Antes —borrador, enviada, o anulada sin aprobar— van ocultos.
  **Consecuencia de producto:** el distribuidor pide por cantidades y conoce lo que le van a cobrar cuando el mayorista aprueba;
  desde ahí ve costos y totales confirmados, que necesita para su inventario (D-18 crea productos con ese costo).
- **D-7. Una orden externa nunca oculta.** `PricesHidden` es `false` sin mayorista.

## Contrato

### Lista de precios (mayorista)

`POST /api/v1/tenants/{tenantId}/wholesale/price-lists` y `PUT …/price-lists/{priceListId}` (con `If-Match`) aceptan
`showPrices` opcional:

```json
{ "name": "Catálogo sin precios", "discountPercent": 12, "visibility": "AllActive", "categoryIds": [], "showPrices": false }
```

`GET …/price-lists`, `GET …/price-lists/{id}` y las respuestas de `POST`/`PUT` suman `showPrices` en cada lista
(`PriceListSummaryDto`, `PriceListDetailDto`). Tipo no booleano: 400 del binder, como cualquier cuerpo mal formado.
`POST …/price-lists/preview` («Así lo ve el distribuidor») no cambia: lo ve el mayorista.

### Lado del distribuidor (Compras)

`GET /api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog` con la lista oculta:

```json
{
  "currency": "COP",
  "showPrices": false,
  "items": [
    { "variantId": "…", "code": "CAM-M", "name": "Camiseta – M", "basePrice": null, "discountPercent": null,
      "listPrice": null, "taxRatePercent": 19, "unitCost": null, "productId": "…", "productName": "Camiseta",
      "imageUrl": "https://…" }
  ],
  "total": 1, "page": 1, "pageSize": 50,
  "priceList": { "name": "Catálogo sin precios", "discountPercent": null }
}
```

`POST …/purchases/orders/preview` con proveedor vinculado y lista oculta:

```json
{ "lines": [ { "variantId": "…", "code": "CAM-M", "name": "Camiseta – M", "taxRatePercent": 19, "subtotal": null,
               "listPrice": null } ],
  "totals": null, "currency": "COP", "showPrices": false }
```

`GET …/purchases/orders/{id}` (y las respuestas de `POST`, `PUT` y transiciones, que devuelven el mismo detalle) de una orden con
`PricesHidden` sin aprobar: `showPrices: false`, `totals: null`, `requestedTotal: null`, y en cada línea `unitCost`,
`confirmedUnitCost`, `subtotal` y `listPrice` en `null`. `GET …/purchases/orders`: `total: null` y `showPrices: false` en esa fila.
En todo lo demás —y en toda orden externa o aprobada— `showPrices: true` y los montos como hoy.

### Qué se enmascara

> Primera tanda: sólo las filas «Catálogo del mayorista» y «Vista previa». Las demás filas son **Segunda tanda**.

| Superficie (distribuidor) | Oculto | Se mantiene |
| --- | --- | --- |
| Catálogo del mayorista | `basePrice`, `discountPercent`, `listPrice`, `unitCost`, `priceList.discountPercent` | código, nombres, foto, `taxRatePercent`, `priceList.name`, `total` |
| Vista previa | `lines[].subtotal`, `lines[].listPrice`, `totals` | líneas, tasa, `currency` |
| Detalle de la orden (antes de aprobar) | `lines[].unitCost`, `confirmedUnitCost`, `subtotal`, `listPrice`, `totals`, `requestedTotal` | cantidades, estado, historial, `allowedActions` (guardar y enviar siguen) |
| Lista de órdenes | `total` de la fila | lo demás |
| PDF y correo al proveedor | montos impresos como «—» (se arman desde el detalle: `PurchaseOrderPdfMapper`) | lo demás |

`taxRatePercent` no es un precio y se queda: la pantalla no puede reconstruir el monto con él.

### Lo que este corte nunca toca: los documentos del mayorista

`showPrices = false` oculta precios **sólo en las superficies del distribuidor** (la tabla de arriba, módulo Compras). Los
documentos que el mayorista emite desde su propio tenant —cotización, pedido, PDF, envío por WhatsApp o correo, módulo Cotizaciones—
**siempre llevan precios**: una cotización sin precios no tiene sentido y el mayorista no tiene un pedido distinto del suyo
(`Order` con `Source = Wholesale`, con el precio congelado). Un test de regresión lo fija: con la lista oculta, el PDF del pedido
del mayorista sigue trayendo los montos. Si el mayorista le manda al distribuidor el PDF de su cotización o su pedido, el
distribuidor recibe los precios por ese camino: la lista oculta sólo controla lo que el distribuidor ve **mientras arma su orden**
en QEP, no lo que el mayorista decide enviarle.

### Fugas que quedan abiertas (seguimiento)

0. **Hasta la segunda tanda:** una orden o borrador ya guardado muestra sus montos en detalle, lista, PDF y correo (no hay
   prices_hidden ni regla D-6). La vista previa y el catálogo sí los esconden.
1. **Lo que el distribuidor ya vio.** Un borrador guardado con la lista visible conserva sus montos hasta que se vuelva a
   guardar (D-5). No se reescribe en masa.
2. **El correo y el PDF le llegan al mayorista con «—»** mientras la orden no esté aprobada. No es una fuga (al revés): el
   mayorista tiene los precios en su pedido. Si molesta, se separa el PDF del proveedor del PDF del comprador.
3. **Auditoría y outbox del distribuidor** (`purchases.order.*`) no llevan montos hoy; si un día los llevan, aplicar la regla.
4. **Reportes de Compras.** No existen hoy; uno nuevo que sume `unit_cost` de órdenes vinculadas tiene que aplicar D-6.
5. **Ajuste global u override por distribuidor:** fuera de este corte (owner).

### Compatibilidad

Expand puro: en la primera tanda una columna con valor por defecto (la segunda, prices_hidden, es de la segunda tanda),  (`wholesale.price_lists.show_prices` `true`, `purchases.purchase_orders.prices_hidden`
`false`). Un pod viejo ignora las columnas. Un **frontend viejo** contra este backend ve exactamente lo de hoy mientras ninguna
lista esté oculta; con una lista oculta leería `null` donde espera número. Como el switch sólo existe en el frontend nuevo, el
orden de despliegue es backend y enseguida frontend, y ningún mayorista puede ocultar antes de que el frontend nuevo esté arriba.
