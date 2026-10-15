# Pedidos sin cotización: el pedido con datos propios

**Fecha:** 2026-10-15
**Módulos:** Quotations (dominio `Order`, tablas nuevas, conversión, pedido manual, lecturas, Excel, eventos),
Pipeline (consumidor de los eventos de pedido, foto `deal_orders`), Reporting (reporte de pedidos leyendo
del pedido), Bootstrapper (`OrdersReportSource`, adaptadores). **Frontend:** `qep-frontend`,
`features/orders`, `features/quotes`, `features/reports`, guía `docs/integracion-cotizaciones-y-pedidos.md`.
**Estado:** aprobado por el owner el 2026-10-15 tras leerlo. Pendiente de implementar; plan en
`docs/superpowers/plans/2026-10-15-pedidos-sin-cotizacion-backend.md`.
**Lo necesita:** `2026-10-15-integracion-shopify-pedidos-design.md` (§11.1), que asume este modelo. Va
**antes** que el spec de flete y que Shopify.
**Ramas:** `feature/pedidos-sin-cotizacion` desde `develop` en `qep-backend`; la homónima desde `develop` en
`qep-frontend`.

## 1. Contexto y alcance

Hoy un `Order` es un apéndice de su `Quotation`: tiene número, estado, pago, comprobantes y quién hizo qué,
pero **no** tiene cliente, asesor, líneas, partes, cuenta de cobro ni totales. Todo eso lo lee de la
cotización en 25 puntos del backend, en las 47 columnas del Excel de pedidos, en el reporte de pedidos, en
los eventos que mueven el embudo y en la mitad de la pantalla de pedidos. Editar un pedido pendiente edita,
en realidad, la cotización.

El owner quiere **separar cotizaciones de pedidos**: que pueda existir un pedido sin cotización, que una
persona pueda crear uno desde Pedidos, que las integraciones (Shopify primero) creen pedidos sin inventar
una cotización fantasma, y que el pedido lleve **datos propios en todos los casos**, también cuando nace de
una cotización.

**Dentro:**

- `Order` con cabecera, partes, líneas y totales propios; `QuotationId` opcional; `Source` (§4).
- Conversión como **copia independiente**: la cotización queda congelada; editar el pedido no la toca (§4.4).
- Pedido manual desde la pantalla de pedidos, con las mismas reglas de precio que el cotizador (§5).
- Lista, detalle, edición, Excel y reporte de pedidos leyendo del pedido (§6).
- Eventos de pedido y su efecto en el embudo (§7).
- Migración de los pedidos existentes copiando lo de su cotización (§9).
- Frontend de pedidos leyendo del pedido y editor de pedido nuevo (§8).

**Fuera** (§12): mover pedidos a un módulo propio, PDF o envío del pedido, flete, pedidos de Shopify.

## 2. Hechos verificados en el código

Inventario del 2026-10-15 sobre `develop` (`b11919eb`). Q = `src/Modules/Quotations`, App =
`Q/Modules.Quotations.Application`, Dom = `Q/Modules.Quotations.Domain`, Inf =
`Q/Modules.Quotations.Infrastructure`.

| # | Hecho | Dónde |
| --- | --- | --- |
| H1 | `Order` sólo enlaza con la cotización por `QuotationId`; sus campos propios son número, estado, pago, notas, convertido/aprobado/anulado/facturado por y cuándo, `RitualCollectionSyncId`, fechas, versión y comprobantes. `RecalculatePaymentStatus(decimal amountDue)` recibe el monto porque el agregado no tiene total. `Order.Create` sólo se llama desde la conversión. | `Dom/Order.cs:3-15,65-68,118,334`; `App/ConvertQuotationToOrder.cs:148` |
| H2 | Tabla `quotations.orders` con esas columnas; `IX_orders_quotation` **único** (el 1:1); FK a `quotations` con `Restrict`. `order_payment_proofs` cuelga del pedido y no toca la cotización. | `Inf/Persistence/QuotationsDbContext.cs:399-532` |
| H3 | `OrderRepository` devuelve `OrderWithQuotation`; `SearchAsync`, `AnyForExportAsync` y `ListForExportAsync` hacen join a `quotations`; los filtros de cliente, CUC y asesor se aplican sobre `Quotations`; los ítems y partes para el Excel se leen de `QuotationItems` y `QuotationParties` por `QuotationId`. | `Inf/Persistence/OrderRepository.cs:24-30,45-62,72-172,179-244,279-324`; `App/IOrderRepository.cs:10,139,150` |
| H4 | La lista de pedidos toma `QuotationNumber`, `ClientId`, `ClientName`, `AdvisorId`, `AdvisorName`, `PaymentMethod`, `Currency` y `Total` de la cotización. El detalle es `OrderDetailDto(OrderDto, QuotationDto)` y sin cotización lanza `OrderNotFound`. | `App/ListOrders.cs:37-62`; `App/OrderListing.cs:59-89`; `App/OrderMapping.cs:31-46`; `App/GetOrderById.cs:24,46-48`; `App/OrdersDtos.cs:170-208` |
| H5 | Editar un pedido pendiente edita la cotización: `AddOrderItems`, `SaveOrderEdits`, `PreviewOrderEdits` y `OrderItemEdits` cargan la cotización, usan `quotation.Currency` e `IsRetail`, llaman `AddItemAfterConversion`, `UpdateItemQuantityAfterConversion`, `RemoveItemAfterConversion`, `SetIsRetailAfterConversion`, `SetGlobalScaleFloorAfterConversion`, escriben historial de la cotización y recalculan el pago con `quotation.NetTotal`. `RemoveOrderPaymentProof` también. | `App/AddOrderItems.cs:61-108`; `App/SaveOrderEdits.cs:70-330`; `App/PreviewOrderEdits.cs:63-130`; `App/OrderItemEdits.cs:38-98`; `App/RemoveOrderPaymentProof.cs:41-54`; `Dom/Quotation.cs:406-417,439,467,584,606` |
| H6 | Dos handlers de ítems de cotización tienen un caso especial: si la cotización está `Converted`, buscan el pedido y exigen que esté `Pending`. `EnsureEditable` bloquea `Converted` en el resto. | `App/UpdateQuotationItem.cs:47-60`; `App/RemoveQuotationItem.cs:36-50`; `Dom/Quotation.cs:1057-1065` |
| H7 | Aprobar, anular, facturar, revertir facturación y adjuntar comprobantes son operaciones sólo del pedido. El estado de pago al adjuntar lo manda el cliente, derivado en el frontend contra `quotation.netTotal`. | `App/ApproveOrder.cs:40-59`; `App/CancelOrder.cs:42-63`; `App/InvoiceOrder.cs:39-59`; `App/RevertOrderInvoicing.cs:36-57`; `App/AddOrderPaymentProofs.cs:79-215,130` |
| H8 | El Excel de pedidos construye una fila por `QuotationItem` y lee de la cotización la empresa y cuenta de cobro, las líneas, las partes, el cliente, el asesor, `notes` (**de la cotización**, no del pedido), `order_total`, `retention_amount` y `carrier`; del pedido sólo número, fecha y comprobantes. | `App/OrdersExportProcessor.cs:186-366,303,307,361,366`; `Dom/OrdersExportColumnCatalog.cs:30-110` |
| H9 | `OrdersReportSource` hace join a `quotations` y toma asesor, cliente, moneda y totales de la cotización; `OrdersReportItemDto` lleva `QuotationId` y `QuotationNumber` obligatorios. | `src/Bootstrapper/OrdersReportSource.cs:72-250,303-353`; `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs:15-31` |
| H10 | Eventos: `quotations.quotation.converted.v1` (datos de la cotización más `orderId`, `orderNumber`) y `quotations.order.cancelled.v1` (`tenantId`, `orderId`, `quotationId`); la correlación es siempre el id de la cotización. Pipeline busca su foto `DealQuotation` **por `quotationId`**, guarda `OrderId` y `OrderCancelledAt`, gana el negocio con `converted` y lo reabre con `order.cancelled`. No lee las tablas de pedidos. | `Inf/Persistence/QuotationLifecycleEventPublisher.cs:26-62`; `src/Modules/Pipeline/Modules.Pipeline.Application/QuotationEvents.cs:8-12,45-52,100,161`; `Modules.Pipeline.Domain/DealQuotation.cs:35-37`; `Modules.Pipeline.Infrastructure/Messaging/QuotationEventPayloads.cs:26,53` |
| H11 | `Quotation.ConvertToOrder` exige Draft/Sent, líneas, `ValidUntil`, cuenta de cobro, partes completas y documento de facturación; congela la foto de productos y pasa a `Converted`. Las conversiones anteriores al 2026-09-13 dejaron la cotización en `Sent`. | `Dom/Quotation.cs:853-869,882-955,789-799`; `Dom/QuotationStatus.cs:7-14` |
| H12 | La lista de cotizaciones muestra `orderId` y `orderStatus` por fila (`FindByQuotationIdsAsync`). | `App/ListQuotations.cs:172-184`; `App/QuotationMapping.cs:75,88-89`; `App/QuotationsDtos.cs:530-532` |
| H13 | Frontend: `OrderDetail { order, quotation }`; `order-items-card`, `order-shipping-card`, `order-summary-cards`, `order-detail-page`, `order-edit-page` y `order-edit-draft` leen `quotation.*`; `AddOrderItems` devuelve `{ order, quotation }`; `created-order-summary` y `order-confirmation-summary` muestran el pedido creado desde la cotización. | `qep-frontend/src/features/orders/types/order-detail.ts:12-15`; `components/order-items-card.tsx:51-229`; `order-shipping-card.tsx:38-68`; `order-summary-cards.tsx:95-250`; `pages/order-edit-page.tsx:222-513`; `utils/order-edit-draft.ts:44-469`; `features/quotes/components/created-order-summary.tsx:38-343` |
| H14 | Otros lectores de `quotations.orders`: `QuotationUserReferenceProbe` (membresías referenciadas), `ExportLoadSeeder` (siembra con `quotation_id`), Identity `OrphanUserCleanupTests.SeedOrderAsync` (llama `Order.Create`). Messaging y Pos no referencian pedidos. | `Inf/Persistence/QuotationUserReferenceProbe.cs:49-58`; `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:365-401` |
| H15 | Pruebas que tocan pedidos: unas 22 de 42 de integración y 25 de 68 unitarias en Quotations, más Reporting (harness `ConvertToOrderAsync`), Identity y Pipeline. `OrderWithQuotation` se construye en siete archivos de pruebas unitarias. `OrdersMigrationTests` existe. | `tests/Modules/Quotations/**`; inventario §7 |
| H16 | `modelo-datos-cotizaciones.md`, citado 14 veces en el código como fuente del modelo 1:1, **no existe** en el repo. | `grep -rn "modelo-datos-cotizaciones" src` |

## 3. Decisiones del owner (2026-10-15)

| # | Pregunta | Elegido |
| --- | --- | --- |
| 1 | ¿Qué significa separar cotizaciones de pedidos? | **Pedido con datos propios para todos.** Debe poder existir un pedido sin cotización. |
| 2 | ¿Quién crea un pedido sin cotización? | **También una persona desde Pedidos**, además de las integraciones. |
| 3 | Al convertir, ¿cómo se relacionan los dos documentos? | **Copia independiente.** La cotización queda `Converted` y congelada; editar el pedido toca sólo el pedido. |
| 4 | ¿Qué hace el embudo con un pedido sin cotización? | **Es una venta:** gana el negocio abierto del cliente o crea uno ya ganado; anularlo lo reabre. |
| 5 | Enfoque | **A: datos propios dentro de `Modules.Quotations`.** Extraer un módulo `Orders` queda fuera (§12). |

## 4. Dominio: `Order` con datos propios

### 4.1 Cabecera

Conserva todo lo de H1 y gana:

| Campo | Qué | De dónde sale al convertir |
| --- | --- | --- |
| `ClientId` | cliente (referencia blanda a Customers) | `Quotation.ClientId` |
| `AdvisorId` | membresía asesora | `Quotation.AdvisorId` |
| `Currency` | ISO 4217 de todo el pedido | `Quotation.Currency` |
| `BillingAccount` | copia de empresa, banco, cuenta y moneda (`QuotationBillingAccount` se renombra a `BillingAccountSnapshot` y lo comparten los dos agregados) | `Quotation.BillingAccount` |
| `PaymentMethod` | texto, mismo tope | `Quotation.PaymentMethod` |
| `IsRetail`, `GlobalScaleFloor` | reglas de precio que hoy edita `SaveOrderEdits` sobre la cotización | de la cotización |
| `BillsToFinalConsumer`, `BillingUsesBusinessName`, `IsStorePickup` | banderas de facturación y entrega | de la cotización |
| `CustomerWithRetention`, `CustomerVatSurplus` | foto del perfil tributario del cliente al crear | de la cotización |
| `Subtotal`, `DiscountAmount`, `TaxAmount`, `TaxPercentage`, `Total`, `RetentionAmount`, `NetTotal` | totales propios, recalculados por el agregado | se recalculan de las líneas copiadas y tienen que coincidir con los de la cotización (prueba) |
| `Source` | `Quotation` · `Manual` · `Shopify` (la lista crece con cada integración) | `Quotation` |
| `QuotationId` | **opcional** | la cotización |
| `ExternalReference` | hasta 64, null | null |

`RecalculatePaymentStatus()` deja de recibir el monto: usa `NetTotal`. `AppliesRetention` y `AppliesVatSurplus`
se calculan como en la cotización (facturar a consumidor final anula el excedente).

### 4.2 Partes: `order_parties`

Misma forma que `quotation_parties` (`QuotationPartyDetails` se renombra a `PartyDetails` y se comparte):
rol `Billing` o `Shipping`, nombre, documento, teléfono, email, dirección, departamento y ciudad, con la misma
regla de «los seis campos o ninguno» al guardar y «completa» para crear el pedido.

### 4.3 Líneas: `order_items`

Misma forma que `quotation_items`: producto, foto de código y nombre, cantidad, precio unitario **con IVA**,
porcentaje de descuento y su origen, tasa de IVA, posición. El cálculo de línea (`Subtotal`, `DiscountAmount`,
`TaxAmount`, `DiscountedUnitPriceWithoutTax`, `UnitPriceWithoutTax`, redondeo) se extrae de `QuotationItem` a
`LineAmounts` (Domain, estático) y lo usan los dos agregados: una sola implementación para que un cambio de
redondeo no deje a pedidos y cotizaciones sumando distinto. `RecalculateTotals` de `Order` es el mismo
algoritmo que el de `Quotation` (`QuotationTotals` extraído igual).

### 4.4 Fábricas y conversión

- `Order.FromQuotation(id, orderNumber, quotation, paymentStatus, notes, convertedBy, proofs, now)`: copia
  cabecera, partes y líneas (foto de producto incluida), `Source = Quotation`, `QuotationId = quotation.Id`.
  Precondición: `quotation.EnsureConvertibleToOrder()` ya pasó (la llama el handler).
- `Order.CreateManual(id, orderNumber, header, parties, lines, paymentStatus, notes, createdBy, proofs, now)`:
  `Source = Manual`, sin cotización. Valida lo mismo que `EnsureConvertibleToOrder` exige hoy a la
  cotización, trasladado al pedido: al menos una línea, cuenta de cobro, partes completas, documento de
  facturación cuando se factura a una parte propia, perfil tributario coherente.
- `Order.CreateExternal(…)`: igual que `CreateManual` pero con `Source` dado y `ExternalReference`; es la
  **única** fábrica que no exige cliente completo (la elegibilidad del cliente vive en Application, §5.2).
- `ConvertToOrder` en `Quotation` queda como está: `EnsureConvertibleToOrder`, foto de productos, `Converted`.
  Después, la cotización es **sólo lectura**: se eliminan `AddItemAfterConversion`,
  `UpdateItemQuantityAfterConversion`, `RemoveItemAfterConversion`, `SetIsRetailAfterConversion`,
  `SetGlobalScaleFloorAfterConversion` y `CaptureProductSnapshotsAfterConversion`, y el caso especial de
  `UpdateQuotationItem` y `RemoveQuotationItem` (H6): una `Converted` responde `quotation.quotation.status_not_editable`.
- Las conversiones anteriores al 2026-09-13 que dejaron la cotización en `Sent` (H11) se corrigen en la
  migración (§9): toda cotización con pedido pasa a `Converted`. El comentario de `AddItemAfterConversion`
  que lo justificaba se va con el método.

### 4.5 Edición del pedido pendiente

`AddItems`, `UpdateItemQuantity`, `RemoveItem` (no el último), `SetIsRetail`, `SetGlobalScaleFloor`,
`UpdateParties`, `UpdateNotes`: todos sobre el pedido, todos exigen `Pending`, todos recalculan totales y
`RecalculatePaymentStatus()`. Los descuentos de grupo y escalas se resuelven en Application como en el
cotizador (`QuotationPricingRecalculation` se generaliza a `PricingRecalculation` sobre una interfaz común de
líneas).

### 4.6 Historial del pedido

`order_history` con el molde de `quotation_history`: `Created` (con el origen: «desde COT-…», «manual»,
«Shopify #1001»), `ItemsChanged`, `PartiesChanged`, `Approved`, `Cancelled`, `Invoiced`, `InvoicingReverted`,
`PaymentProofsChanged`. El historial de la cotización registra sólo `Converted` con el número del pedido; lo
que pase después es del pedido.

## 5. Pedido manual

### 5.1 Contrato

`POST /api/v1/tenants/{tenantId}/orders`, permiso `quotations.order.manage`, módulo `orders`:

```json
{
  "clientId": "…",
  "paymentMethod": "Transferencia",
  "notes": "…",
  "isRetail": false,
  "globalScaleFloor": null,
  "parties": { "billing": null, "shipping": { … }, "billingUsesBusinessName": false, "isStorePickup": false, "billsToFinalConsumer": false },
  "billingAccount": { "companyId": "…", "bankName": "…", "accountNumber": "…", "currency": "COP" },
  "items": [ { "productId": "…", "quantity": 2 } ],
  "paymentStatus": "PaymentPending",
  "paymentProofs": []
}
```

Las líneas llegan con producto y cantidad; precio, descuento por escala, descuentos de grupo y tasa de IVA
los resuelve el handler **igual que el cotizador** (`IQuotationProductPricingLookup`, resolver de escala,
descuentos de grupo). El validador de FluentValidation da el campo; el dominio, el código. Responde `201`
con el pedido completo (§6.2). `POST …/orders/preview` con el mismo cuerpo devuelve el pedido calculado sin
guardar (sin número ni id), para que el editor pinte totales antes de confirmar.

### 5.2 Reglas

- Cliente: `OrderCustomerEligibility`, copia de `QuotationCustomerEligibility` con códigos
  `order.order.client_not_found`, `client_incomplete`, `client_cuc_missing`, `client_inactive`. El pedido
  manual **exige cliente completo con CUC**, como la cotización. Sólo `CreateExternal` (integraciones) lo
  relaja: su servicio pasa `allowIncomplete: true` y ningún handler HTTP lo hace (prueba).
- Numeración: el consecutivo de pedidos del tenant (`IOrderNumberGenerator`, formato por tenant), igual que al
  convertir. No consume número de cotización.
- Asesor: la membresía activa de quien crea (`QuotationAdvisorResolver`), como en la cotización.
- Sin PDF ni envío: lo que el cliente recibe sigue siendo la cotización. Un pedido manual es un registro
  interno.
- Auditoría `order.order.created` con el id del pedido; evento `quotations.order.created.v1` (§7).

## 6. Lecturas, edición, Excel y reportes

### 6.1 Lista

`GET /orders` y el Excel filtran cliente, CUC y asesor contra **columnas del pedido**; `OrderWithQuotation`
desaparece del repositorio (`SearchAsync`, `AnyForExportAsync`, `ListForExportAsync` devuelven `Order`).
`OrderListItemDto` conserva todos los campos de hoy (por BFF: la tabla no cambia) y gana `source` y
`externalReference`; `quotationId` y `quotationNumber` pasan a **opcionales**.

### 6.2 Detalle

`GET /orders/{id}` y `GET /quotations/{id}/order` devuelven `OrderDetailResponse` nuevo: cabecera del pedido,
`client { id, name, cuc, identification, businessName }`, `advisor { id, name }`, `billingAccount` con
`companyName` y `companyTaxId`, `parties[]`, `items[]` con producto resuelto (nombre, código, imagen como
hoy el compositor de cotizaciones), totales, `history[]`, `paymentProofs[]`, y `sourceQuotation { id,
quotationNumber }` cuando `QuotationId` no es nulo. **Deja de viajar `quotation`.** El compositor
`IQuotationResponseComposer` se generaliza para resolver cliente, asesor, productos y empresa de cualquiera
de los dos documentos.

### 6.3 Edición

`POST /orders/{id}/preview`, `PUT /orders/{id}` (`If-Match`) y `POST /orders/{id}/items` operan sobre el
pedido (§4.5) y devuelven `OrderDetailResponse`. `isRetail` y `globalScaleFloor` del cuerpo son campos del
pedido. `RemoveOrderPaymentProof` recalcula con `NetTotal` del pedido.

### 6.4 Excel de pedidos

Las 47 columnas conservan clave, encabezado, orden y el layout por tenant. Cambia la fuente:

| Columnas | Antes | Ahora |
| --- | --- | --- |
| `company`, `bank`, `account`, `bank_account`, `company_tax_id`, `payment_method_N` | `Quotation.BillingAccount` | `Order.BillingAccount` |
| `product_code`, `quantity`, `unit_price`, `unit_price_without_tax`, `tax`, `tax_rate`, `discount` | `QuotationItem` | `OrderItem` |
| `city`, `address`, `phone`, `email`, `coordinadora_city` | parte de envío de la cotización o cliente de la cotización | parte de envío del pedido o cliente del pedido |
| `customer_name`, `customer_identification` | banderas y partes de la cotización | las del pedido, misma precedencia |
| `document`, `advisor_code` | `Quotation.ClientId` / `AdvisorId` | `Order.ClientId` / `AdvisorId` |
| `notes` (Observaciones) | **`Quotation.Notes`** | **`Order.Notes`**: al convertir, las notas de la cotización se copian al pedido, así que un pedido convertido exporta lo mismo que hoy; el cambio sólo se nota si alguien edita las notas del pedido después, que es lo esperado |
| `order_total`, `retention_amount`, `carrier` | cotización | pedido |
| `order_number`, `order_date`, comprobantes | pedido | pedido |

Una fila por `OrderItem`. Un pedido sin cotización exporta igual. `OrdersExportColumnCatalog` documenta la
fuente nueva en el comentario de `notes`.

### 6.5 Reporte de pedidos

`OrdersReportSource` deja el join: asesor, cliente, moneda, subtotal, IVA y total salen de `orders`;
`FilterQuotations` pasa a filtrar pedidos. `OrdersReportItemDto.QuotationId` y `QuotationNumber` pasan a
opcionales (`Guid?`, `string?`); el frontend de reportes los pinta vacíos. `source` se agrega al DTO para que
el reporte pueda mostrarlo. Los reportes de cotizaciones no cambian.

## 7. Eventos y embudo

**Nuevos en `QuotationLifecycleEventPublisher`** (contrato con Pipeline, camelCase, se cambian en los dos
lados y en `QuotationEventPayloadsTests`, CLAUDE.md):

- `quotations.order.created.v1`: `tenantId`, `orderId`, `orderNumber`, `clientId`, `advisorMemberId`,
  `total`, `currency`, `source`, `quotationId` (null si no hay), `occurredAt`. Correlación: el id del pedido.
  Se publica en las tres fábricas.
- `quotations.order.cancelled.v1` gana `source` y `quotationId` pasa a opcional. Correlación: id del pedido.
- `quotations.quotation.converted.v1` no cambia: Pipeline sigue ganando el negocio por la cotización.

**Pipeline** (`QuotationEventApplier` gana dos métodos y una foto):

- `pipeline.deal_orders`: `tenant_id`, `order_id`, `order_number`, `client_id`, `total`, `currency`,
  `source`, `deal_id` (null), `cancelled_at` (null), fechas. Paralela a `deal_quotations`.
- `ApplyOrderCreatedAsync`: con `quotationId` no nulo → no hace nada (el `converted` ya actuó). Sin
  cotización → foto en `deal_orders`; negocio abierto del cliente: uno → se gana con `DealActor.ForSystem`
  y el monto del pedido pasa a ser el del negocio si no tiene principal; varios → la foto queda suelta y el
  asesor elige desde el negocio (como D27 del spec de embudo con cotizaciones); ninguno → se crea uno a
  nombre de `advisorMemberId`, `DealSource.Order` (valor nuevo), y se gana. Idempotente por `order_id`.
- `ApplyOrderCancelledAsync`: con cotización → lo de hoy; sin cotización → marca `cancelled_at` en la foto y,
  si el negocio está ganado por ese pedido, lo reabre a la última etapa abierta, como hoy con la cotización.
- `DealDetail` lista los pedidos del negocio junto a las cotizaciones (`orders[]`), para que el tablero
  muestre la venta de la tienda o el pedido manual.

## 8. Frontend (`qep-frontend`, su developer)

| Dónde | Qué |
| --- | --- |
| `features/orders/types` | `OrderDetail` pasa a ser el pedido completo (§6.2); `OrderListItemDto` con `quotationId`/`quotationNumber` opcionales y `source`; `order-edit-draft` sobre `order.items`, `order.isRetail`, `order.globalScaleFloor`. |
| `features/orders/components` | `order-items-card`, `order-shipping-card`, `order-summary-cards`, `order-table`, `order-detail-page`, `order-edit-page`, `save-order-edits-dialog`, `add-order-payment-proofs-dialog`: leen del pedido; «Cotización de origen» sólo si `sourceQuotation` viene. Insignia de `source`. |
| `features/orders` nuevo | «Nuevo pedido» en la lista (`quotations.order.manage`): editor que reutiliza del cotizador el selector de cliente, las partes, la cuenta de cobro, el editor de líneas y el resumen de totales (con `POST /orders/preview`), más estado de pago y comprobantes; confirma con `POST /orders` y navega al detalle. |
| `features/quotes` | `created-order-summary` y `order-confirmation-summary` leen del `OrderDetailResponse` que devuelve la conversión; `quote-convert-page` no pide la cotización después de convertir. Una cotización `Converted` se muestra sólo lectura sin la excepción de edición. |
| `features/reports` | `quotationNumber` vacío cuando no hay; columna «Origen» si el reporte lo trae. |
| `docs/integracion-cotizaciones-y-pedidos.md` | Se reescribe: forma del pedido, pedido manual, rutas, códigos. |

Textos en español con tuteo.

## 9. Migración de datos

Una migración de EF de Quotations, en una transacción:

1. Columnas nuevas en `quotations.orders` **nulas**; tablas `order_items`, `order_parties`, `order_history`.
2. `UPDATE orders SET … FROM quotations` para la cabecera; `INSERT INTO order_items … SELECT … FROM
   quotation_items` e igual para partes, con ids nuevos y `order_id`; `source = 'Quotation'`; `notes` del
   pedido conserva las suyas (no se pisan con las de la cotización: **el Excel pierde** las notas de la
   cotización en pedidos viejos cuyo pedido no tenía notas, D-O3).
3. Totales: se copian de la cotización, no se recalculan en SQL. Una prueba de integración recalcula con el
   dominio sobre los datos migrados y exige igualdad.
4. **Guardas** antes de volver las columnas `NOT NULL`: `SELECT count(*)` de pedidos sin líneas, sin cuenta
   de cobro o sin cotización; si alguno es mayor que cero, `RAISE EXCEPTION` con los ids. Un pedido así no
   pudo crearse por la API; si existe, se mira a mano antes de migrar.
5. `quotations.status = 'Converted'` para toda cotización con pedido (H11).
6. `IX_orders_quotation` pasa a único **parcial** (`WHERE quotation_id IS NOT NULL`); FK con `Restrict` se
   conserva; índices `(tenant_id, client_id)`, `(tenant_id, advisor_id)`, `(tenant_id, source)`.
7. Irreversible por diseño: el `Down` sólo borra las tablas y columnas nuevas; la cotización no perdió nada.

`OrdersMigrationTests` siembra con el esquema viejo (SQL crudo, como `InsertLegacyOrderAsync`), migra y
compara fila a fila. `ExportLoadSeeder` pasa a sembrar las tablas nuevas.

## 10. Despliegue

- **Backend y frontend en la misma ventana**, backend primero: `GET /orders/{id}` cambia de forma y el
  frontend viejo lo leería mal. No hay bandera de transición: duplicar la respuesta por un sprint costaría más
  que coordinar la ventana.
- La migración corre al arrancar; con las guardas de §9.4 un dato inconsistente deja el pod sin arrancar con
  el motivo en el log, a propósito. Antes de desplegar, correr las tres consultas de las guardas contra
  producción.
- Rollback: sólo backend y frontend juntos; las columnas nuevas quedan y el código viejo las ignora.
- README: la sección de cotizaciones y pedidos describe el modelo nuevo y el pedido manual; CLAUDE.md gana el
  gotcha de los eventos de pedido como contrato con Pipeline.

## 11. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias:** `OrderTests` (tres fábricas, validaciones, líneas, totales con los mismos casos de
`QuotationTests`, edición pendiente, `RecalculatePaymentStatus` desde `NetTotal`, historial);
`LineAmountsTests` y `TotalsTests` compartidos (los dos agregados dan lo mismo); `QuotationTests` sin
`…AfterConversion`; `OrdersExportProcessorTests` (90 casos) contra `Order`; `ListOrdersHandlerTests`,
`ExportOrdersHandlerTests`, `OrderInvoicingHandlerTests`, `ApproveOrderHandlerTests`,
`RemoveOrderPaymentProofHandlerTests`, `ListQuotationsHandlerTests` sin `OrderWithQuotation`;
`QuotationsDbContextMappingTests` con las tablas nuevas; `CreateOrderValidatorTests`; Pipeline:
`QuotationEventApplierTests` con `order.created` (con y sin cotización, uno, varios y ningún negocio abierto)
y `order.cancelled` sin cotización; `QuotationEventPayloadsTests`.

**Integración (Quotations):** pedido manual (alta, `preview`, cliente incompleto 422, sin CUC 422, sin cuenta
422, partes incompletas 422, escalas y descuentos de grupo iguales al cotizador); conversión que copia
(totales iguales a la cotización) y congela (editar la cotización después → 422); edición del pedido sin
tocar la cotización; `GET /orders/{id}` sin `quotation` y con `sourceQuotation`; lista y filtros por cliente,
CUC y asesor; Excel de un pedido con y sin cotización, columna por columna; `notes` del pedido;
`order.created` y `order.cancelled` en el outbox; migración con datos viejos (`OrdersMigrationTests`) y con
un pedido inconsistente (falla con el id). Reporting: pedidos sin cotización en listado, resumen y mensual.
Pipeline: `deal_orders` y el negocio ganado/reabierto. Identity: `SeedOrderAsync` con la fábrica nueva.

**Barrido previo:** los 22 archivos de integración y 25 unitarios de Quotations que arman pedidos o
`OrderWithQuotation` (H15), el harness de Reporting y el de Identity, **antes** de correr nada: un cuerpo
viejo falla con 422 donde se esperaba 201 y esconde la causa.

**Arquitectura:** sin cambios de capas. `ConfigurationExampleTests` sin claves nuevas.

**Suite por módulo tocado** (Quotations, Reporting, Pipeline, Identity), no la completa.

## 12. Fuera de alcance

- Mover pedidos a `Modules.Orders` (enfoque B). Con los datos separados, es un refactor mecánico futuro.
- PDF, envío por correo o WhatsApp del pedido.
- Flete (spec aparte, siguiente).
- Pedidos de Shopify (spec aparte, después del flete).
- Editar un pedido `Approved` o `Invoiced` (hoy tampoco).
- Cotizar desde un pedido (pedido → cotización).
- Backfill de `deal_orders` para pedidos viejos: nacieron de cotizaciones, y su negocio ya existe.

## 13. Decisiones tomadas sin el owner (a ratificar)

| ID | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-O1 | El código sigue en `Modules.Quotations`; las tablas nuevas, en el esquema `quotations` | Enfoque B |
| D-O2 | `Source` como texto con `CHECK` (`Quotation`, `Manual`, `Shopify`); cada integración agrega su valor | Un enum abierto |
| D-O3 | La migración **no pisa** `Order.Notes` con las de la cotización; el Excel de un pedido viejo sin notas propias exporta `Observaciones` vacía donde antes salían las de la cotización | Copiar las notas de la cotización cuando el pedido no tiene |
| D-O4 | Un pedido manual exige cliente completo con CUC, como la cotización; sólo las integraciones admiten incompleto | Relajarlo también para el manual |
| D-O5 | El pedido manual nace `Pending`, nunca directamente `Approved` | Un campo en el cuerpo |
| D-O6 | Permiso `quotations.order.manage` para crear; sin permiso nuevo | `quotations.order.create` con sus cuatro piezas |
| D-O7 | `GET /quotations/{id}/order` se mantiene por compatibilidad con la misma respuesta nueva | Retirarlo |
| D-O8 | Con varios negocios abiertos del cliente, un pedido sin cotización deja la foto suelta (como las cotizaciones); no crea un negocio más | Elegir el más reciente |
| D-O9 | `DealSource.Order` nuevo para negocios creados por un pedido sin cotización | Reutilizar `Quotation` |
| D-O10 | Toda cotización con pedido pasa a `Converted` en la migración (cierra la deuda del 2026-09-13) | Dejarlas en `Sent` |
| D-O11 | Despliegue coordinado sin bandera de compatibilidad | Duplicar `quotation` en la respuesta por un sprint |
| D-O12 | `order_history` propio desde el día uno; el historial previo a la migración queda en la cotización y el pedido migrado nace con un solo evento `Created` «desde COT-…» | Copiar entradas |

## 14. Entregables

1. Backend: dominio y tablas, fábricas, conversión, edición, pedido manual, lecturas, Excel, reporte,
   eventos, Pipeline, migración, README y CLAUDE.md.
2. Frontend: pedidos leyendo del pedido, editor de pedido nuevo, conversión, reportes, guía.
3. Plan de implementación (`writing-plans`) tras la lectura de este spec.
4. Delta al spec de Shopify (§11.1 y §6.2.3) con los nombres reales.

## Historial de revisión

- 2026-10-15: decisiones 1–5 tomadas en conversación con el owner, con un inventario previo del
  acoplamiento (§2); secciones aprobadas una por una; spec escrito. Pendiente la lectura del owner y el plan.
