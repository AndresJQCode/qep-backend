# Detalle del pedido del comprador — diseño (backend)

Fecha: 2026-11-10 · Repo: `qep-backend` · Spec hermano: `qep-storefront/docs/superpowers/specs/2026-11-10-detalle-pedido-comprador-design.md`.
Este documento manda el contrato HTTP.
Construye sobre `2026-11-09-pedidos-del-comprador-design.md` (rutas `/buyer/orders`, `BuyerOrderGateway`) y levanta su
no objetivo implícito: el detalle del pedido existía sólo para la confirmación de anulación y no traía partes, totales
ni notas. Depende, sólo del lado del storefront, de `2026-11-10-tienda-mostrar-precios-design.md` (§4.6).

Estado: decisiones del owner de §3 tomadas el 2026-11-10; no quedan preguntas abiertas. Lo marcado **[decisión del
agente]** o **[interpretación del agente sobre la respuesta del owner]** se tomó al escribir este documento: se puede
discutir, pero no bloquea la construcción. §8 lista lo que el owner confirma al leer.

Reglas del workspace que aplican: TDD obligatorio (RED antes que GREEN, con evidencia literal); no se inventan campos,
estados, rutas, permisos ni códigos de error —lo que no existe en el código se marca como nuevo o pendiente—; ante
discrepancia entre este documento y el código, gana el código y se corrige el documento.

## 1. Intención y porqué

El comprador con sesión ya ve «Mis pedidos» (número, fecha, estados, total y la primera línea) y puede anular los
pendientes. No puede ver **qué dejó** al comprar: a quién se factura, a dónde se envía, qué nota escribió y cómo se
compone el total. Para eso tiene que escribirle al asesor. El owner decidió que cada pedido de la lista abra una página
de detalle con esos datos.

`GET /buyer/orders/{orderId}` ya existe y ya filtra por tenant y comprador. Lo que falta es contenido: hoy
`BuyerOrderDetailDto` es el resumen más `lines(name, quantity, unitPrice, lineTotal)`, `cancelledAt` y `canCancel`. Este
trabajo agrega al detalle los totales, las partes de facturación y envío y la nota original del comprador. Sin ruta nueva
ni código de error nuevo. **Sí trae una migración** (§4.8): la nota del checkout hoy se guarda en un campo que el asesor
sobrescribe.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- `BuyerOrderDetailDto` gana, de forma **aditiva**: `subtotal`, `taxAmount`, `freightTotal`, `isStorePickup`,
  `carrierName`, `billedTo`, `billing`, `shipping` y `buyerNotes` (§4.3).
- Los nombres de departamento y ciudad viajan resueltos (texto), no como ids (§4.4).
- Con excedente de IVA, las líneas del detalle van sin IVA para que sumen el total (§4.7).
- `Order` guarda aparte, inmutable, la nota que escribió el comprador en el checkout (§4.8).
- `POST /buyer/orders/{orderId}/cancel` devuelve el mismo detalle ampliado (comparte el DTO).

**No objetivos**

- Ruta nueva, paginación del detalle, historial del pedido, comprobantes de pago, seguimiento de envío, PDF.
- Quitar montos de la respuesta cuando la tienda está en modo catálogo: lo hace el storefront (D6, §4.6).
- Cambiar el listado (`BuyerOrderSummaryDto`) o la regla de anulación (D5 del spec anterior).
- Exponer `Order.Notes` (la nota de trabajo del asesor) o el medio de pago (`Order.PaymentMethod`).
- Exponer el descuento: el detalle no lo muestra (D8) y `discountAmount` no entra al contrato (§4.3).
- Recuperar la nota original de pedidos ya creados (§4.8: no es recuperable).

**Anti-objetivos**

- Que un comprador vea el detalle de un pedido de otro comprador, de otro tenant o uno que puso como invitado. Sigue el
  mismo 404 `order.order.not_found` para los tres casos, sin distinguirlos.
- Que el detalle exponga campos internos: `ClientId`, `AdvisorId`, `BuyerAccountId`, `QuotationId`, `ExternalKey`,
  `ExternalReference`, `CarrierId`, `BillingAccount`, `IsRetail`, `GlobalScaleFloor`, `PartyWithRetention`,
  `PartyVatSurplus`, `CustomerWithRetention`, `CustomerVatSurplus`, `BillingUsesBusinessName`, `BillsToFinalConsumer`
  (crudo), `RetentionAmount`, `NetTotal`, `TaxPercentage`, `DiscountAmount`, `FreightChargeMode`, `FreightTaxPercentage`,
  `CashOnDelivery`, `Notes`, `PaymentMethod`, `ApprovedBy`, `CancelledBy`, `InvoicedBy`, `ConvertedBy`,
  `CancellationReason`, `RitualCollectionSyncId`, `Version`, `PersonType`, ids de departamento, ciudad, parte o línea,
  `ProductCode`, `DiscountPercentage`, `DiscountOrigin`. La prueba de §5 compara el conjunto exacto de claves del JSON.
- Que el comprador vea una nota escrita o editada por el asesor (D9).
- Que `Modules.Buyers.*` referencie `Modules.Quotations.*` o `Modules.Geography.*` (las capas siguen como hoy:
  `BuyersLayerTests`, `QuotationsLayerTests`).
- Que el detalle lea la ficha del cliente (`Customers`) para rellenar una facturación que no es propia (§4.3).

## 3. Decisiones del owner (2026-11-10)

| # | Decisión |
| --- | --- |
| D1 | Cada pedido de «Mis pedidos» abre una página propia `/cuenta/pedidos/{id}` en el storefront. |
| D2 | El detalle muestra encabezado (número, fecha, estado del pedido, estado del pago), productos (nombre y cantidad por línea), facturación (nombre o razón social, tipo y número de documento, correo, teléfono, dirección, ciudad, departamento: lo que el pedido guarde), envío (dirección, ciudad, departamento, transportadora; si es recogida en tienda, se dice en lugar de la dirección) y montos (precio unitario y total por línea, subtotal, impuestos, flete, total; el descuento lo ajusta D8). |
| D3 | «Anular pedido» también está en el detalle cuando `canCancel` (propuesta del agente, aceptada por el owner). |
| D4 | Con `showPrices = false` (spec `2026-11-10-tienda-mostrar-precios-design.md`) el detalle no muestra **ningún** monto: sólo líneas con cantidades, facturación, envío, nota y estados. Los montos no deben llegar al navegador si es factible. |
| D5 | Excedente de IVA («si es exento de IVA, no mostrar IVA 0»): cuando el `taxAmount` del pedido es 0, el detalle **no** muestra la fila «Impuestos». Que las líneas también vayan sin IVA, para que sumen el total, es **interpretación del agente sobre la respuesta del owner** (§4.7). |
| D6 | Modo catálogo («lo que sea más fácil»): el backend sigue mandando los montos en `buyer/orders` (lista y detalle) y el servidor del storefront los quita antes del navegador (§4.6). El spec de precios no cambia. |
| D7 | Facturación no propia: si el pedido se factura a consumidor final, el detalle dice «Facturado a consumidor final». Para distinguirlo, el detalle expone `billedTo` (§4.3). En el caso «ficha del cliente» la sección se oculta: **decisión del agente, a confirmar por el owner al leer**. |
| D8 | Descuento («no mostrar ahorraste $x»): el detalle no muestra «Ahorraste». Que no haya **ninguna** fila de descuento (el subtotal ya lo incluye, así que subtotal + impuestos + envío = total) es **interpretación del agente sobre la respuesta del owner**. |
| D9 | Notas: el comprador ve **sólo** la nota que él escribió en el checkout, nunca una edición del asesor (§4.8). |

## 4. Diseño del backend

### 4.1 Punto de partida verificado en el código

- Ruta: `PublicBuyerEndpoints.cs:163` `group.MapGet("/orders/{orderId:guid}", GetOrderAsync)` y `:169`
  `MapPost("/orders/{orderId:guid}/cancel", …)`, en el grupo `/api/v1/public/stores/{host}/buyer` con
  `StorefrontKeyFilter`, `readPolicy`, `X-Qep-Visitor-Ip`, `X-Qep-Buyer-Session` y `Cache-Control: no-store`
  (`BuyerNoStoreMiddleware`).
- Handlers: `GetBuyerOrderHandler` y `CancelBuyerOrderHandler` (`Modules.Buyers.Application/BuyerOrders.cs`): host →
  sesión → `IBuyerOrders.FindAsync/CancelAsync` → `null` = `ResourceNotFoundException("order.order.not_found")` (404).
- Adaptador: `src/Bootstrapper/BuyerOrdersAdapter.cs` resuelve `IBuyerOrderGateway` en un scope propio y copia los
  records campo a campo (`ToDetail`).
- Gateway: `Modules.Quotations.Application/IBuyerOrderGateway.cs`, `BuyerOrderGateway.FindOwnedAsync` filtra por
  `TenantId` **y** `BuyerAccountId` (repositorio más re-chequeo en memoria). `ToDetail` arma hoy
  `BuyerOrderDetail(Id, Number, CreatedAt, Status, PaymentStatus, Total, Currency, LineCount, FirstLineName, Lines,
  CancelledAt, CanCancel)`; la línea es `BuyerOrderLine(ProductName, Quantity, UnitPrice, Subtotal + TaxAmount)`
  (`:231`). `BuyerOrderLine.Quantity` es `decimal` (`:8`).
- Agregado `Order` (`Modules.Quotations.Domain/Order.cs`):
  - Totales (`RecalculateTotals`, `:1315`, sobre `DocumentTotals.Compute`): `Total = Subtotal + TaxAmount +
    FreightSubtotal + FreightTaxAmount` (`DocumentTotals.cs:48`). `Subtotal` es la base sin IVA **después** del
    descuento (`QuotationItem.cs:93-96`); `TaxAmount` es el IVA contenido de los productos. `DiscountAmount` es
    informativo: ya está restado del subtotal. Con `AppliesVatSurplus` (`:309`), `TaxAmount` y `FreightTaxAmount` son 0
    (`DocumentTotals.cs:45-47`).
  - Línea: `OrderItem.UnitPrice` es precio de lista con IVA, antes del descuento; `UnitPriceWithoutTax`
    (`OrderItem.cs:86`) existe derivado. La cantidad admite fracciones (`Order.UpdateItemQuantity(…, decimal quantity, …)`,
    `:741`).
  - Envío: `IsStorePickup`, `CarrierName`, `FreightSubtotal`, `FreightTaxAmount`.
  - Partes: `Billing` y `Shipping` (`OrderParty`, `Role` `Billing`/`Shipping`): `Name`, `IdentificationType`
    (`PartyIdentificationType?`), `IdentificationNumber`, `PersonType`, `Phone`, `Email`, `Address`, `DepartmentId`,
    `CityId`, `Country` (ISO alfa-2; **`null` es Colombia**, `QuotationPartyDetails.cs:128-143`) y `CityName` (texto, sólo
    fuera de Colombia). En la parte de envío identificación y tipo de persona son siempre `null` (`OrderParty.Apply`).
    Con `IsStorePickup` no hay parte de envío (`AssignParties`, `:1340`).
  - Notas: `Order.Notes` (`:257`, columna `notes text`, `QuotationsDbContext.cs:588`). La sobrescriben
    `UpdateNotes` (`:1173`, desde `SaveOrderEditsHandler`, `SaveOrderEdits.cs:157`, permiso `OrderManage`) y el registro
    de comprobantes de pago (`Notes = NormalizeNotes(notes)`, `:1054`, dentro de `AddPaymentProofs`); en pedidos externos
    `AttachExternalShipping` le agrega líneas (`:1230`). `NormalizeNotes` = recorte, vacío → `null` (`:1450`).
- Qué llena el checkout de la tienda:
  - Storefront: campo `note` (`checkout-schema.ts:263`, máximo 1000) → `note` del cuerpo (`:339`).
  - Backend: `PublicOrderRequestBody.Note` (`SubmitOrderRequest.cs:44`, `NoteMaxLength = 1000`, `:76-77`: «La nota viaja
    a Notes del pedido») → `StoreOrderDraft.Notes` (`:308`) → `Order.CreateFromStore(…, notes, …)` → `Order.Notes`.
  - `StoreOrderGateway.CreateAsync` (`IStoreOrderGateway.cs:165-205`): `Billing` y `Shipping` desde `StoreOrderParty`;
    «Usar los mismos datos» manda la facturación como envío; el `Email` de envío es el de facturación (`:33-35`);
    `PersonType` queda `null`. Siempre `IsStorePickup: false`, `BillsToFinalConsumer: false`,
    `BillingUsesBusinessName: false` y `ShippingDetails.None` (`Order.cs:502`). **Un pedido recién creado desde la
    tienda nunca es recogida en tienda y nunca trae transportadora ni flete.**
- Lo que el asesor cambia después, mientras el pedido está `Pending`: `SetShipping` (`:794`), `SetBilling` (`:629`),
  `UpdateNotes` (`:1173`). No hay método que cambie `IsStorePickup` ni la parte de envío de un pedido de la tienda.
- Nombres de lugares: Quotations.Application ya tiene `IQuotationGeographyLookup` (`FindDepartmentNamesAsync`,
  `FindCityNamesAsync`), adaptador `Bootstrapper/QuotationGeographyLookup.cs` sobre Geography; lo usa
  `OrdersExportProcessor`. Un id que no existe no aparece en el diccionario.
- Tipo de documento al contrato: `PartyFiscalTypesParser.ToWireValue(this PartyIdentificationType)`
  (`PartyFiscalTypes.cs:67`) → `"NIT" | "CC" | "CE" | "PASAPORTE" | "NIF" | "NIE" | "TAX_ID"`.
- Precedencia de facturación: `OrderBilledParty.SourceFor(billsToFinalConsumer, billing)` → enum `BilledPartySource`
  `FinalConsumer | BillingParty | Customer` (`OrderBilledParty.cs`).
- Última migración de Quotations: `20261110023855_AddOrderBuyerAccount`.

### 4.2 Dónde vive el cambio

- **Domain** (`Order.cs`): propiedad nueva `BuyerNotes` y su asignación en `CreateFromStore` (§4.8).
- **Infrastructure** (`QuotationsDbContext.cs`): columna `buyer_notes` y migración (§4.8).
- **Application** (`IBuyerOrderGateway.cs`): `BuyerOrderDetail` gana los campos de §4.3 y dos records nuevos,
  `BuyerOrderBilling` y `BuyerOrderShipping` (nombres al builder). `BuyerOrderGateway` recibe `IQuotationGeographyLookup`
  y su `ToDetail` pasa a `ToDetailAsync` (nombres de departamento y ciudad de las dos partes, una llamada por
  diccionario). Las líneas siguen §4.7. `FindAsync` y `CancelAsync` usan el mismo armado.
- **Buyers.Application** (`BuyerOrders.cs`): `BuyerOrderDetailDto` gana los mismos campos y sus dos records `…Dto`.
- **Bootstrapper** (`BuyerOrdersAdapter.cs`): `ToDetail` copia los campos nuevos.
- Nada cambia en `BuyerOrderSummaryDto` ni en `ListAsync`. Sin ruta ni código de error nuevos.

Ni el gateway ni el handler leen `OnlineStore.ShowPrices` (§4.6).

### 4.3 Contrato HTTP

Rutas, filtros, cabeceras, códigos y status **sin cambios** (spec anterior §4.5):

| Ruta | Respuesta | Errores |
| --- | --- | --- |
| `GET /orders/{orderId:guid}` | `200 BuyerOrderDetailDto` (ampliado) | 401 `store.buyer.session_invalid`; 404 `store.public.not_found` (host); 404 `order.order.not_found` (ajeno, de otro tenant, de invitado o inexistente) |
| `POST /orders/{orderId:guid}/cancel` | `200 BuyerOrderDetailDto` (ampliado) ya anulado | los del detalle; 409 `order.order.not_cancellable_by_buyer` |

`BuyerOrderDetailDto` (JSON, camelCase). Lo marcado **nuevo** no existe hoy:

```jsonc
{
  "id": "…", "number": "PED-2026-0042", "createdAt": "2026-11-10T15:04:05+00:00",
  "status": "Pending", "paymentStatus": "PaymentPending",
  "total": 119000, "currency": "COP", "lineCount": 1, "firstLineName": "Camiseta",
  "lines": [{ "name": "Camiseta", "quantity": 2, "unitPrice": 50000, "lineTotal": 100000 }], // §4.7 con excedente
  "cancelledAt": null, "canCancel": true,

  "subtotal": 84033.61,        // nuevo — Order.Subtotal: base sin IVA, ya con descuento
  "taxAmount": 15966.39,       // nuevo — Order.TaxAmount: IVA de los productos (0 con excedente de IVA)
  "freightTotal": 19000,       // nuevo — Order.FreightSubtotal + Order.FreightTaxAmount
  "isStorePickup": false,      // nuevo — Order.IsStorePickup
  "carrierName": null,         // nuevo — Order.CarrierName (null si nadie la asignó)
  "billedTo": "BillingParty",  // nuevo — BilledPartySource: "FinalConsumer" | "BillingParty" | "Customer"
  "billing": {                 // nuevo — null si billedTo no es "BillingParty"
    "name": "Ana Pérez",
    "identificationType": "CC",            // ToWireValue; null si la parte no lo tiene
    "identificationNumber": "1020304050",  // null si la parte no lo tiene
    "email": "ana@example.com",
    "phone": "+573001112233",
    "address": "Calle 1 # 2-3",
    "country": "CO",                       // ISO alfa-2; el null del dominio sale como "CO"
    "departmentName": "Valle del Cauca",   // Colombia: por DepartmentId; fuera de Colombia: null
    "cityName": "Cali"                     // Colombia: por CityId; fuera: OrderParty.CityName
  },
  "shipping": {                // nuevo — null con isStorePickup o sin parte de envío
    "name": "Ana Pérez", "phone": "+573001112233", "address": "Calle 1 # 2-3",
    "country": "CO", "departmentName": "Valle del Cauca", "cityName": "Cali"
  },
  "buyerNotes": "Entregar en portería"   // nuevo — Order.BuyerNotes (§4.8); null si no escribió nota o es anterior
}
```

Reglas del armado:

- **Identidad: `subtotal + taxAmount + freightTotal == total`**, exacta (`DocumentTotals.cs:48`). Se escribe en el
  comentario del DTO (regla BFF del repo). Se expone `freightTotal` y no `FreightAmount` porque es el valor que cierra el
  total también con excedente de IVA **[decisión del agente]**.
- **Sin `discountAmount` [decisión del agente]:** el descuento ya está dentro del subtotal y el detalle no lo muestra (D8).
  Como el campo es nuevo y nadie lo consumiría, no entra al contrato; agregarlo después es aditivo. `lines[].unitPrice`
  sigue siendo el precio antes del descuento, así que con descuento `quantity × unitPrice ≠ lineTotal`, igual que en el
  carrito.
- `lines[]`: con IVA como hoy, salvo con excedente de IVA (§4.7).
- `billedTo` (D7) es el nombre del enum `BilledPartySource` que devuelve
  `OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing)`, como viajan los demás enums del contrato. No
  expone `BillsToFinalConsumer` crudo ni datos del cliente.
- `billing` sale de `Order.Billing` **sólo si** `billedTo == "BillingParty"`; si no, `null` (`billing != null ⇔ billedTo
  == "BillingParty"`). Con `FinalConsumer` o `Customer` la identidad facturada vive en la ficha del cliente, que el detalle
  no lee. Un pedido de la tienda nace siempre con `BillingParty`; los otros dos casos aparecen sólo si el asesor cambió la
  facturación **[decisión del agente]**.
- `shipping` sale de `Order.Shipping`; `null` con `IsStorePickup` o sin fila. No lleva correo ni identificación: el
  correo de envío del checkout es el de facturación y el dominio anula la identificación de envío **[decisión del
  agente]**.
- `country`: `OrderParty.Country ?? "CO"`. Viaja el código: el storefront ya tiene `countryNameOf` **[decisión del
  agente]**.
- `departmentName`/`cityName`: en Colombia, los diccionarios de `IQuotationGeographyLookup` (id ausente → `null`); fuera,
  `departmentName = null` y `cityName = OrderParty.CityName`.
- `identificationType`/`identificationNumber` pueden ser `null` aunque `billing` exista (el dominio sólo los exige a una
  fila con nombre propio, `EnsureBillingRules`); la tienda siempre los manda.
- `buyerNotes` es `Order.BuyerNotes`, nunca `Order.Notes` (D9, §4.8).
- Texto tal como está guardado; sin enmascarar: el comprador es dueño de esos datos y la sesión ya se verificó.
- Partes, transportadora y flete se muestran **como están hoy**, con los cambios del asesor (§7, R1). La nota no: es la
  original (D9).

**Compatibilidad:** campos aditivos. El storefront de hoy parsea el detalle con `z.object(...)`, que descarta claves
desconocidas: el storefront viejo contra el backend nuevo no cambia, salvo que con excedente de IVA las líneas del
detalle y de la anulación llegan sin IVA (§4.7), que el storefront de hoy sólo usa para la confirmación de anulación. El
storefront nuevo tolera el backend viejo (spec hermano §4.3).

### 4.4 Nombres de departamento y ciudad

El pedido **no guarda nombres** en Colombia, sólo `DepartmentId` y `CityId`. Se resuelven en el backend con
`IQuotationGeographyLookup`, el mismo puerto que usa el Excel de pedidos, para que el storefront no haga dos llamadas más
a `/geography/departments` y `/geography/departments/{id}/cities` ni conozca ids **[decisión del agente]**. Costo: dos
consultas `ListByIdsAsync` por detalle (a lo sumo dos departamentos y dos ciudades), sin caché. El gateway corre en el
scope propio del adaptador; el lookup usa los repositorios de Geography, con su propio `DbContext`, sólo lectura.

### 4.5 Sesión, propiedad y errores

Sin cambios respecto del spec anterior: sesión antes de leer (401); el pedido se busca por `(TenantId, BuyerAccountId,
OrderId)` y se re-chequea en memoria; todo lo demás es el mismo 404 `order.order.not_found`. Un fallo del lookup de
Geography (excepción) se propaga como 500: no se disfraza de nombre `null`, que no se distinguiría de un id inexistente
**[decisión del agente]**.

### 4.6 Modo catálogo: dónde se quitan los montos (D6)

**El backend no quita montos; los quita el servidor del storefront.** Es lo que fija el spec de precios (backend §4.2:
`buyer/orders` «no cambia» y sigue devolviendo montos; O4: ocultar es visual), el owner lo confirmó (D6), y el
consumidor de esta API es sólo el servidor del storefront (`X-Qep-Storefront-Key`, server-to-server): el navegador
nunca habla con el backend. El spec hermano quita los montos campo por campo antes del HTML. Este cambio no lee
`ShowPrices`.

### 4.7 Excedente de IVA: líneas sin IVA (D5) **[interpretación del agente sobre la respuesta del owner]**

Con `Order.AppliesVatSurplus`, el pedido deja `TaxAmount` y el IVA del flete en 0 (`DocumentTotals.cs:45-47`), pero el
mapeo de línea (`IBuyerOrderGateway.cs:231`) manda `unitPrice = item.UnitPrice` y `lineTotal = item.Subtotal +
item.TaxAmount`, los dos con IVA: las líneas no suman al total. El owner pidió no mostrar «IVA 0»; para que lo que se ve
cierre, en esos pedidos las líneas van sin IVA:

- `order.AppliesVatSurplus == true` → `unitPrice = item.UnitPriceWithoutTax` (`OrderItem.cs:86`) y
  `lineTotal = item.Subtotal`.
- `false` → como hoy: `item.UnitPrice` y `item.Subtotal + item.TaxAmount`.
- Resultado: `Σ lines[].lineTotal + freightTotal == total`, salvo el redondeo de `DocumentTotals` sobre la suma
  (centavos; la prueba redondea igual).
- **Alcance:** `lines[]` sólo existe en `BuyerOrderDetailDto`, que comparten `GET /buyer/orders/{id}` y
  `POST /buyer/orders/{id}/cancel`: las dos respuestas cambian. El listado no lleva líneas: no cambia.
- La regla vive en el gateway; el motivo se escribe en el comentario de `BuyerOrderLine`.

### 4.8 Nota del comprador: `Order.BuyerNotes` (D9) — **trae migración**

Verificado en el código (§4.1): la nota del checkout cae en `Order.Notes`, y el asesor la sobrescribe con
`UpdateNotes` (guardar ediciones del pedido) y con el registro de comprobantes de pago (`AddPaymentProofs`, `:1054`). No
existe copia aparte. Exponer `Notes` violaría D9. Lo mínimo **[decisión del agente]**:

- Domain: `public string? BuyerNotes { get; private set; }` en `Order`, asignada **sólo** en `CreateFromStore` con la
  misma normalización que `Notes` (`NormalizeNotes`: recorte, vacío → `null`) y el mismo valor de entrada. Ningún otro
  método la toca: ni `UpdateNotes`, ni `AddPaymentProofs`, ni `AttachExternalShipping`, ni las ediciones. `Notes` sigue
  igual (el asesor sigue partiendo de la nota del comprador).
- Infrastructure: `order.Property(value => value.BuyerNotes).HasColumnName("buyer_notes").HasColumnType("text")`.
- **Migración** de Quotations (nombre al builder, p. ej. `AddOrderBuyerNotes`), sobre
  `20261110023855_AddOrderBuyerAccount`: `ALTER TABLE quotations.orders ADD buyer_notes text NULL`. `Down` borra la
  columna. **Sin backfill: `NULL` para todo pedido existente**, porque la nota original ya no es recuperable (el asesor
  pudo haber editado `notes`) y copiar `notes` podría mostrarle al comprador un texto del asesor. Además los pedidos de la
  tienda que existen hoy son de prueba (D3 del spec anterior).
- Sin tope nuevo: `NoteMaxLength = 1000` ya lo limita en la entrada.
- Contrato: `buyerNotes` sólo en el detalle (§4.3).

## 5. Pruebas (TDD)

Cada ítem va RED antes que GREEN, con la salida literal de las dos corridas en el plan.

Unitarias de dominio (`OrderTests`, Quotations):

- `CreateFromStore` con nota → `BuyerNotes == Notes` normalizada; sin nota o en blanco → `null`.
- `UpdateNotes`, `AddPaymentProofs` con otra nota y las ediciones dejan `BuyerNotes` intacta.
- `CreateManual`, `FromQuotation` y `CreateExternal` dejan `BuyerNotes = null`.

Unitarias de Application (`BuyerOrderGatewayTests`, con un `IQuotationGeographyLookup` falso):

- `Subtotal`, `TaxAmount`, `FreightTotal` salen del pedido y `Subtotal + TaxAmount + FreightTotal == Total`, también con
  flete cobrado (`SetShipping`) y con excedente de IVA.
- Excedente de IVA (§4.7): `TaxAmount == 0`, cada línea con `UnitPrice == item.UnitPriceWithoutTax` y
  `LineTotal == item.Subtotal`, y la suma de líneas más `FreightTotal` igual a `Total` (redondeo de `DocumentTotals`); sin
  excedente, las líneas siguen con IVA. Lo mismo en `CancelAsync`.
- `Billing` copia nombre, `IdentificationType` en su valor de contrato (`"CC"`, `"NIT"`, `"TAX_ID"`), número, correo,
  teléfono y dirección; `Country = "CO"` cuando el dominio guarda `null`.
- Colombia: nombres desde el lookup; id que el lookup no devuelve → `null`. Fuera (`"ES"`): `DepartmentName = null`,
  `CityName = OrderParty.CityName`, sin llamar al lookup con ids vacíos.
- `BilledTo = "BillingParty"` con fila propia; `"FinalConsumer"` y `Billing = null` con `BillsToFinalConsumer`;
  `"Customer"` y `Billing = null` con la fila quitada (`SetBilling(null, …)`).
- `IsStorePickup = true` → `Shipping = null` (pedido armado con `CreateManual`; el gateway no mira el origen).
- `CarrierName` refleja `SetShipping`; `null` en un pedido recién creado desde la tienda.
- `BuyerNotes` sale de `Order.BuyerNotes`: tras `UpdateNotes("texto del asesor")`, el detalle sigue con la nota original.
- `CancelAsync` devuelve el detalle ampliado. Ajeno → `null` (las pruebas existentes siguen en verde).

Integración (`Modules.Store.IntegrationTests`, `PublicBuyerOrdersApiTests`; Testcontainers, un proyecto a la vez,
`-- xUnit.MaxParallelThreads=4`):

- Pedido del checkout real (`BuyerOrdersRig`) con nota y `BuyerAccountId` fijado por SQL: `GET /buyer/orders/{id}` trae
  `billing`/`shipping` iguales a lo enviado, nombres de la geografía sembrada, `isStorePickup: false`,
  `carrierName: null`, `freightTotal: 0`, `billedTo: "BillingParty"`, `buyerNotes` igual a la nota y la identidad de
  totales.
- Después de editar las notas por la consola (`SaveOrderEdits`), `buyerNotes` no cambia.
- **Conjunto exacto de claves** del detalle, de `billing` y de `shipping`: sin `discountAmount`, sin `notes`, sin campos
  internos.
- «Usar los mismos datos» → `shipping` igual a `billing` sin correo ni documento.
- País distinto de `CO` si el rig lo permite; si no, queda en la unitaria.
- 404 de siempre: otra cuenta, otro tenant, inexistente → `order.order.not_found`. `POST …/cancel` responde el detalle
  ampliado.
- Con `showPrices: false` (si el spec de precios ya está mergeado; si no, al rebasar): el detalle **sigue** trayendo
  montos (no-objetivo, D6).

Migración (`Modules.Quotations.IntegrationTests`, `OrdersMigrationTests`): columna `buyer_notes text NULL`; una fila
existente queda en `NULL` al aplicar; la migración aplica sobre `AddOrderBuyerAccount`; un `Order` nuevo con
`BuyerNotes` se lee de vuelta.

`ArchitectureTests`: sin cambios; `BuyersLayerTests` y `QuotationsLayerTests` en verde.

## 6. Despliegue

Backend primero: **trae la migración de Quotations** (`buyer_notes`). El contrato es aditivo; el storefront viejo
descarta las claves nuevas. El storefront nuevo contra este backend viejo muestra el detalle sin facturación, envío,
nota ni desglose (spec hermano §4.3). Sin dependencia de orden con el spec de precios en el backend. Las notas de
pedidos creados antes del despliegue no aparecen (§4.8). `docs/estado.md` se actualiza en `develop` al mergear, no en la
rama.

## 7. Riesgos

- **R1 — «Lo que dejó» vs. «como está».** Facturación, transportadora y flete se muestran con las ediciones del asesor
  (no hay copia inmutable de lo enviado, salvo la nota, §4.8). El comprador puede ver datos que no escribió.
- **R2 — Notas anteriores perdidas.** Los pedidos creados antes de la migración tienen `buyer_notes NULL` aunque el
  comprador haya escrito nota. Aceptado (§4.8).
- **R3 — Recogida en tienda y transportadora.** Hoy ningún pedido de la tienda nace con ellas: estarán casi siempre en
  `false`/`null`. Se exponen porque el owner las pidió y porque el asesor puede asignar transportadora y flete.
- **R4 — Datos personales en la respuesta.** Correo, teléfono, documento, dirección y nota del propio comprador viajan
  al servidor del storefront y a su HTML, con `no-store` en las dos capas.
- **R5 — Líneas sin IVA con excedente (§4.7).** Cambia `lines[]` también en la respuesta de anulación; el storefront de
  hoy no pinta montos de línea, así que no se nota.

## 8. Para confirmar por el owner al leer

No quedan preguntas abiertas. Se confirman al leer:

- D7: ocultar la sección de facturación cuando `billedTo == "Customer"` (decisión del agente).
- D5 y D8: las dos interpretaciones del agente (líneas sin IVA con excedente; ninguna fila de descuento).
- §4.8: columna nueva `buyer_notes` con migración y sin backfill.
