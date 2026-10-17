# Flete: entrega, transportadora, valor del envío y contraentrega

**Fecha:** 2026-10-17
**Módulos:** Quotations (transportadoras del tenant, IVA del envío, campos de entrega y cobro en `Quotation` y
`Order`, totales, PDF, Excel de pedidos, edición del pedido pendiente), Reporting (resúmenes de pedidos y
cotizaciones), Bootstrapper (lectura de la tasa de impuesto de Catalog, `OrdersReportSource`,
`QuotationsReportSource`). **Frontend:** `qep-frontend`, `features/quotes`, `features/orders`,
`features/tenant-settings`, `features/catalog` (el gestor de tasas de impuesto pasa a Configuración), rutas
nuevas `/settings/carriers` y `/settings/tax-rates`.
**Estado:** aprobado por el owner el 2026-10-17 tras leerlo, con mockups en el lienzo «Entrega y flete del
pedido» (claude.ai, privado). P1 y P2 siguen con su valor por defecto (D-F6, D-F9). Pendiente el plan de
implementación.
**Lo necesita:** `2026-10-15-integracion-shopify-pedidos-design.md` (§11.2): retira `freight_unsupported` y
mapea el envío de Shopify (§10 de este spec). Va **después** de pedidos sin cotización (ya en `develop`) y
**antes** que Shopify.
**Ramas:** `feature/flete` desde `develop` en `qep-backend`; la homónima desde `develop` en `qep-frontend`.

## 1. Contexto y alcance

Hoy una cotización o un pedido sólo saben si el cliente **recoge en tienda** (`IsStorePickup`). No hay
transportadora, no hay valor de envío y no hay forma de decir que el pedido se paga contraentrega. El Excel
de pedidos escribe «Coordinadora» a mano en toda fila que no es recogida. Los asesores cobran el envío por
fuera del documento, y un pedido de Shopify que cobra envío no tiene dónde ponerlo: por eso el spec de
Shopify lo deja en espera (`freight_unsupported`) y no se habilita a un tenant real sin este spec.

**Dentro:**

- Configuración › **Transportadoras**: lista del tenant (nombre, orden, activa, si hace recaudo
  contraentrega, nombres con que llega de Shopify) y el **IVA del envío**, elegido entre las tasas de
  Catálogo.
- En la cotización y el pedido: primero **recoger en tienda o enviar**; si se envía, dirección,
  transportadora, **quién paga el envío** (se cobra en el documento, al cobro, gratis) y el **valor** con IVA
  incluido.
- **Contraentrega** como forma de cobro del pedido: la transportadora recauda al entregar.
- Totales, PDF de la cotización, Excel de pedidos, reportes y eventos con el envío.
- El gestor de **tasas de impuesto** pasa de Catálogo a Configuración (sólo frontend, §9.1).
- Delta al spec de Shopify.

**Fuera** (§12): cotizar el envío en vivo con la transportadora (spec aparte, empieza por Coordinadora),
guías, seguimiento, conciliación automática del recaudo, peso y medidas de producto.

## 2. Hechos verificados en el código

Inventario del 2026-10-17 sobre `develop` (`a748255c`). Q = `src/Modules/Quotations`, App =
`Q/Modules.Quotations.Application`, Dom = `Q/Modules.Quotations.Domain`, Inf =
`Q/Modules.Quotations.Infrastructure`, FE = `qep-frontend/src`.

| # | Hecho | Dónde |
| --- | --- | --- |
| H1 | Una sola fórmula de totales para los dos agregados: `subtotal`, `discount` y `tax` son la suma redondeada de las líneas; con excedente de IVA `tax = 0`; `total = subtotal + tax`; `retention = round(subtotal × 0,025)` si aplica; `NetTotal = total − retention`. 2 decimales, `AwayFromZero`. | `Dom/DocumentTotals.cs:25-43` |
| H2 | `Quotation.RecalculateTotals` y `Order.RecalculateTotals` llaman a `DocumentTotals.Compute` desde `Touch()`; las fábricas del pedido también (`FromQuotation`, `Build`). Los totales se persisten (`subtotal`, `tax_amount`, `total`, `net_total`, …, `numeric(14,2)`). | `Dom/Quotation.cs:1030-1053`; `Dom/Order.cs:271,~390,863-882`; `Inf/Persistence/QuotationsDbContext.cs:101-129,452-458` |
| H3 | El precio de línea es **con IVA**. `VatIncludedLine`: `lineTotal = round(qty × price) − discount`; `tax = round(lineTotal × rate / (100 + rate))`; `subtotal = lineTotal − tax`; en COP redondea a pesos. Lo comparte POS. | `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs:29-76` |
| H4 | Sólo COP discrimina IVA (`ItemizesTaxIn(currency) => currency == "COP"`); fuera de COP la tasa de la línea se fuerza a 0. `AppliesRetention` exige COP; `AppliesVatSurplus` no mira la moneda. | `Dom/QuotationItem.cs:264-267,288-290`; `Dom/OrderItem.cs:162`; `Dom/Quotation.cs:296-306`; `Dom/Order.cs:207-214` |
| H5 | `QuotationParties(…, IsStorePickup, …)`: con recogida la parte de envío se descarta. La cotización la cambia en `UpdateDetails`; el pedido la copia al convertir y **no tiene** cómo cambiar sus partes después (`SaveOrderEditsRequest` = ítems, comprobantes, notas, escala, al detal). | `Dom/QuotationPartyDetails.cs:103-140`; `Dom/Quotation.cs:71,517-549,995`; `Dom/Order.cs:75,130,220-276,890`; `App/OrdersDtos.cs:123-128` |
| H6 | `PaymentMethod` es texto libre de 50 en la cotización y el pedido; ningún formulario del frontend lo edita (el pedido manual manda `null`), la lista de pedidos y el PDF lo muestran. | `Dom/Quotation.cs:26,1079-1090`; `Dom/Order.cs:29,968-979`; FE `features/orders/utils/new-order-draft.ts:181-183`; `quotation.typ:221,229` |
| H7 | Pago del pedido: `OrderPaymentStatus { FullPaymentReceived, PartialPaymentReceived, PaymentPending }`; sin comprobantes sólo se admite `PaymentPending` salvo pedido externo; `RecalculatePaymentStatus` compara los comprobantes con **`NetTotal`**. | `Dom/OrderPaymentStatus.cs`; `Dom/Order.cs:711-723,833-851` |
| H8 | Pedido externo: `ExternalOrderDraft(TenantId, Source, ExternalReference, ClientId, AdvisorMembershipId, PaymentMethod?, Notes?, Parties, BillingAccount, Lines, PaymentStatus, OccurredAt)`. No hay importador de Shopify en código todavía. | `App/IExternalOrderWriter.cs:8-126` |
| H9 | PDF de la cotización: plantilla Typst **en este repo** (`quotation.typ`, recurso embebido) que compila el servicio qcode-pdf; `QuotationPdfDocument` lleva partes, `IsStorePickup`, `PaymentMethod` y totales. No hay PDF de pedido. | `Inf/Pdf/quotation.typ:272-291,376-399`; `App/QuotationPdfDocument.cs:15-71`; `App/QuotationPdfDocumentMapper.cs:26-60,119-121` |
| H10 | Excel de pedidos: una fila por línea, los datos del pedido se repiten; `carrier` = «Recoger en tienda» o la constante «Coordinadora»; `order_total` y `retention_amount` del pedido. Columna nueva = al final del catálogo y al final del arreglo de `RowsFor`; `OrdersExportLayout.Effective` la agrega al final de los layouts guardados con su `DefaultVisible`. | `App/OrdersExportProcessor.cs:33-35,194-283`; `Dom/OrdersExportColumnCatalog.cs:28-114`; `Dom/OrdersExportLayout.cs:218-260` |
| H11 | Reportes: pedidos y cotizaciones suman `Subtotal`, `TaxAmount` y `Total` por moneda; rankings y mensual usan `Total`. `NetTotal` y `RetentionAmount` no se usan. | `src/Bootstrapper/OrdersReportSource.cs:69-82,120-216,318-371`; `src/Bootstrapper/QuotationsReportSource.cs:63-65` |
| H12 | Eventos `sent.v1`, `converted.v1` y `order.created.v1` llevan `total` (= `Total` del documento); Pipeline lo guarda como valor del negocio. | `Inf/Persistence/QuotationLifecycleEventPublisher.cs:17-79`; `src/Modules/Pipeline/Modules.Pipeline.Domain/DealOrder.cs:30,53` |
| H13 | Configuración por tenant en Quotations: `OrdersExportLayout` (`/orders-export-layout`) y `/quotations/settings` (mínimo de compra, tabla `tenant_quotation_settings`), los dos con `tenancy.settings.read` / `tenancy.settings.update`. No hay en Quotations una entidad con CRUD y activar/desactivar; los patrones son `TaxRate` (Catalog) y `ClientClassification` (Customers). | `Api/OrdersExportLayoutEndpoints.cs:22-37`; `Api/QuotationSettingsEndpoints.cs:19-34`; `Inf/Persistence/QuotationsDbContext.cs:791-800` |
| H14 | Quotations lee la tasa de un producto por el puerto `IQuotationProductPricingLookup`, adaptador en Bootstrapper sobre `ITaxRateRepository`. `TaxRate`: `Name`, `Percentage` entero 0–100, `IsActive`; gestor en Catálogo › Tasas de impuesto (`catalog.tax_rate.read` / `.manage`). | `App/IQuotationProductPricingLookup.cs:17-40`; `src/Bootstrapper/QuotationProductPricingLookup.cs:16-80`; `src/Modules/Catalog/Modules.Catalog.Domain/TaxRate.cs:13-106` |
| H15 | Frontend: la entrega del cotizador es `quote-shipping-party.tsx`, donde «Recoger en tienda» es una opción más de la libreta de direcciones; la usan el editor y el pedido manual. Totales en `quote-totals-summary.tsx` (también en el pedido manual), detalle del pedido en `order-shipping-card.tsx` y `order-items-card.tsx`; la tarjeta de pago del pedido manual dice explícitamente que no hay cobro de envío. Configuración: `tenant-settings-page.tsx` con secciones que enlazan a `/settings/<x>`. | FE `features/quotes/components/quote-shipping-party.tsx:26,119-122`; `quote-totals-summary.tsx:67,128`; `features/orders/components/order-shipping-card.tsx:40-57`; `new-order-payment-card.tsx:22-24`; `features/tenant-settings/pages/tenant-settings-page.tsx:130-259` |
| H16 | POS no usa `DocumentTotals` ni tipos de Quotations: sólo `VatIncludedLine`. | `src/Modules/Pos/*/*.csproj`; `Modules.Pos.Domain/PosSale.cs:96-98` |

## 3. Decisiones del owner (2026-10-16 y 2026-10-17)

| # | Pregunta | Elegido |
| --- | --- | --- |
| 1 | ¿Cómo se pregunta la entrega? | **Primero recoger en tienda o enviar.** Recoger no pide dirección. Enviar muestra la lista de transportadoras y después el valor. |
| 2 | ¿De dónde salen las transportadoras? | **Configurables por el tenant** en Configuración › Transportadoras (Coordinadora, Interrapidísimo, Servientrega, Mensajero como ejemplo). |
| 3 | ¿Envío gratis? | **Sí**, como opción explícita. |
| 4 | ¿El cliente que le paga el flete a la transportadora al recibir («flete pago»)? | **Sí**, como opción. En el producto se llama «Al cobro» (§13, D-F1). |
| 5 | ¿Contraentrega (COD)? | **Sí.** Va en el cobro del pedido, no en el envío (ver §5). |
| 6 | ¿El envío lleva IVA? | **Sí.** |
| 7 | ¿Qué tasa? | **Una tasa ya creada en Catálogo**, asignada en Configuración › Transportadoras. |
| 8 | ¿Valor manual o cotizado con la transportadora? | **Manual en este spec.** La cotización en vivo (Coordinadora primero) es un spec propio después; el campo queda listo para que un botón lo llene. |
| 9 | Shopify | El nombre del método de envío identifica la transportadora y el valor viene del pedido. |
| 10 | ¿Dónde vive el gestor de tasas de impuesto? | **En Configuración**, dentro de este spec. Sólo cambia la pantalla: la API y los permisos de Catalog quedan iguales. |
| 11 | ¿Es obligatorio elegir transportadora o envío? | **No.** Ni para enviar la cotización ni para crear o convertir un pedido. |
| 12 | ¿La contraentrega exige transportadora? | **No.** |

**Pendientes del owner** (el spec trae un valor por defecto, marcado en §13):

- **P1. Retención y envío.** ¿La retención en la fuente del 2,5 % se calcula también sobre el envío sin IVA?
  Por defecto **no** (D-F6): sigue siendo sobre los productos, como hoy.
- **P2. Giro del recaudo contraentrega.** ¿Cómo se registra hoy que la transportadora giró el recaudo? Por
  defecto, como cualquier pago: comprobante en el pedido (D-F9).

## 4. Transportadoras y el IVA del envío

### 4.1 Agregado `Carrier`

En `Modules.Quotations` (Dom), tabla `quotations.carriers`:

| Campo | Regla |
| --- | --- |
| `Id`, `TenantId` | |
| `Name` | obligatorio, recortado, ≤ 60; único por tenant sin distinguir mayúsculas ni tildes |
| `ExternalNames` | lista de nombres con que llega de una integración (hoy Shopify), cada uno ≤ 120, sin repetidos dentro de la transportadora, ≤ 20; único por tenant (dos transportadoras no pueden reclamar el mismo nombre) |
| `SupportsCashOnDelivery` | `bool`; si no, la contraentrega no se ofrece con ella |
| `SortOrder` | orden en la lista del cotizador; lo fija la reordenación |
| `IsActive` | las inactivas no se ofrecen; los documentos que ya la tienen la conservan |
| `Version`, `CreatedAt`, `UpdatedAt` | concurrencia optimista como `TaxRate` |

Operaciones: crear, editar (nombre, nombres externos, contraentrega), activar, desactivar, reordenar. **No se
borra** (D-F2): un nombre mal escrito se corrige editando, y los documentos guardan su propia foto del
nombre (§5.1).

La comparación de nombres (unicidad y emparejamiento con Shopify) normaliza: recorta, colapsa espacios,
minúsculas, sin tildes. La normalización vive en Dom (`CarrierName.Normalize`).

**Un solo espacio de nombres por tenant** (D-F16): el nombre y los nombres externos de todas las
transportadoras, **activas e inactivas**, viven en una tabla hija `quotations.carrier_names` (`carrier_id`,
`tenant_id`, `normalized_name`, `kind` = `Name` | `External`) con un único índice
`IX_carrier_names_tenant_name` (`tenant_id`, `normalized_name`). Así el nombre de una no puede ser el nombre
externo de otra (el emparejamiento de Shopify nunca es ambiguo), y el nombre de una inactiva no se reutiliza:
se reactiva. `QuotationsUnitOfWork` traduce ese índice **por nombre** a `quotations.carrier.name_taken`, con
el nombre en conflicto en el detalle.

### 4.2 IVA del envío

`tenant_quotation_settings` gana `freight_tax_rate_id uuid null`. Se fija con una operación propia (no con el
`PUT /quotations/settings` del mínimo de compra, para no obligar a reenviar ese formulario).

- Se valida contra Catalog por un puerto nuevo, `IQuotationTaxRateLookup.FindAsync(tenantId, taxRateId)` →
  `(Id, Name, Percentage, IsActive)?`, adaptador en Bootstrapper sobre `ITaxRateRepository` (mismo patrón que
  H14). Sólo se puede asignar una tasa **activa** del tenant: si no, `quotations.freight_tax_rate.not_found`
  (inexistente u otro tenant: mismo código, nunca distingue) o `quotations.freight_tax_rate.inactive`.
- Se puede quitar (`null`).
- Al **cobrar un envío** en un documento COP (§5.2) se lee la tasa: si no hay, o la tasa se desactivó o borró
  en Catálogo después, `…freight_tax_rate_missing` (422). Los documentos ya guardados no cambian: guardan su
  porcentaje (§5.1).

### 4.3 API (BFF)

Bajo `/api/v1/tenants/{tenantId:guid}`:

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /carriers` | `tenancy.settings.read` | `CarrierSettingsResponse { freightTaxRate: { id, name, percentage, isActive } \| null, carriers: CarrierDto[] }` — **todas**, activas e inactivas, en orden. Una sola llamada pinta la pantalla entera. |
| `POST /carriers` | `tenancy.settings.update` | `CarrierDto` (201). Nace activa, al final del orden. |
| `PUT /carriers/{carrierId:guid}` | `tenancy.settings.update` | `CarrierDto`; `If-Match` como el layout del Excel |
| `POST /carriers/{carrierId:guid}/activate` · `/deactivate` | `tenancy.settings.update` | `CarrierDto` |
| `PUT /carriers/order` | `tenancy.settings.update` | `CarrierSettingsResponse`: la lista entera en el orden nuevo (cuerpo: todos los ids; faltar o sobrar uno es 422 `quotations.carrier.order_mismatch`) |
| `PUT /carriers/freight-tax-rate` | `tenancy.settings.update` | `CarrierSettingsResponse` (cuerpo `{ taxRateId: guid \| null }`) |
| `GET /shipping-options` | `quotations.quotation.read` (D-F3) | `ShippingOptionsResponse { carriers: [{ id, name, supportsCashOnDelivery }] (sólo activas, en orden), freightTaxRate: { id, name, percentage } \| null }` — lo que el cotizador y el pedido necesitan para dibujar la entrega |

`CarrierDto { id, name, externalNames[], supportsCashOnDelivery, sortOrder, isActive, version }`.

Los permisos son los mismos de la otra configuración de Quotations (H13): no hay permiso nuevo. Validador de
FluentValidation para crear y editar (texto libre: nombre y nombres externos). Revalidación de tenant y
permiso en el handler, 403 nunca 404 para un id de otro tenant (regla del repo).

## 5. Entrega y cobro en la cotización y el pedido

### 5.1 Campos nuevos

Los mismos en `Quotation` y en `Order` (tablas `quotations.quotations` y `quotations.orders`):

| Campo | Tipo | Significado |
| --- | --- | --- |
| `IsStorePickup` | existe | recoger en tienda (sí) o enviar (no) |
| `CarrierId` | `uuid null` | la transportadora elegida; **sin FK** a `carriers` (el documento no depende de que la transportadora siga igual) |
| `CarrierName` | `varchar(60) null` | foto del nombre al elegirla; es lo que se muestra, se exporta y se imprime |
| `FreightChargeMode` | `varchar null`, `CHECK` | `Charged` (se cobra en el documento), `OnDelivery` (al cobro: el cliente le paga a la transportadora), `Free` (gratis). `null` = sin envío registrado |
| `FreightAmount` | `numeric(14,2)`, 0 por defecto | el valor **con IVA incluido** que escribió el asesor (o que llegó de Shopify) |
| `FreightTaxPercentage` | `int`, 0 por defecto | foto de la tasa del envío al fijar el valor; 0 fuera de COP |
| `FreightSubtotal` | `numeric(14,2)` persistido | envío sin IVA que suma al total (0 salvo `Charged`) |
| `FreightTaxAmount` | `numeric(14,2)` persistido | IVA del envío que suma al total (0 salvo `Charged` en COP y sin excedente de IVA) |
| `CashOnDelivery` | `bool`, `false` por defecto | el pedido se cobra contraentrega |

Un record de dominio agrupa la entrada: `ShippingDetails(Guid? CarrierId, string? CarrierName,
FreightChargeMode? ChargeMode, decimal FreightAmount, int FreightTaxPercentage, bool CashOnDelivery)`.
`IsStorePickup` **no** se duplica: sigue en `QuotationParties` como hoy (H5) y el agregado valida
`ShippingDetails` contra él.

En la API los campos viajan en un objeto **aparte** de las partes, `ShippingRequest { carrierId,
freightChargeMode, freightAmount, cashOnDelivery }`, no dentro de `QuotationPartiesRequest`: ese record lo
usa también `ExternalOrderDraft.Parties` (`App/IExternalOrderWriter.cs:19`), y el camino externo no debe
recibir envío en este spec (§10).

### 5.2 Reglas (las mismas en los dos agregados)

**Coherencia, siempre** (al crear, guardar o previsualizar; `DomainException` → 422):

- Recogida → sin transportadora, sin modo, valor 0, sin contraentrega
  (`…shipping_not_allowed_on_pickup`). Al pasar de enviar a recoger, el handler limpia esos campos antes de
  llamar al dominio; el dominio sólo valida.
- `FreightAmount` ≥ 0 (`…freight_amount_invalid`); en COP, pesos enteros (como `VatIncludedLine`).
- `Charged` → valor > 0 (`…freight_amount_required`): un envío de $ 0 es `Free`.
- `Free` → valor 0 (el handler lo fuerza; el dominio rechaza otro).
- `OnDelivery` → valor ≥ 0, opcional e informativo: **no suma** al total ni lleva IVA en QEP.
- Transportadora y modo son **independientes y opcionales** (decisión 11): se puede elegir transportadora sin
  registrar envío, o cobrar un envío sin transportadora.
- `CashOnDelivery` tampoco exige transportadora (decisión 12). Sólo exige enviar: con recogida el cliente paga
  en la tienda, y la regla de recogida de arriba ya lo rechaza.

**Al elegir o cambiar la transportadora** (el handler la lee del tenant; no en cada guardado si no cambió):

- Debe existir y estar activa (`…carrier_not_available`; otro tenant → 403 por la doble capa).
- Si hay transportadora, `CashOnDelivery` exige `SupportsCashOnDelivery` de esa transportadora en ese
  momento (`…cash_on_delivery_not_supported`): elegir una que no recauda es una contradicción que se puede
  detectar. Sin transportadora no hay nada que comprobar. Si después la transportadora deja de hacer recaudo, el documento
  conserva lo elegido.
- Un documento que ya tiene una transportadora que se desactivó la conserva al guardarse sin tocarla.

**Al fijar o cambiar el valor de un envío `Charged` en COP:** el handler lee la tasa del tenant (§4.2) y la
pasa al dominio como `FreightTaxPercentage`. Sin tasa → `…freight_tax_rate_missing`.

**Nada de esto es obligatorio** (decisión 11): enviar una cotización, crear un pedido manual y convertir no
exigen transportadora ni envío. Sin ellos el documento queda como hoy (modo nulo, montos 0). No hay
precondición nueva en `Send` ni en la creación del pedido, así que las pruebas y harness que arman cuerpos
sin envío siguen pasando, y no hace falta precargar transportadoras en los tenants.

Prefijos de los códigos: `quotation.quotation.*` en la cotización y `order.order.*` en el pedido, como los
existentes (`payment_method_too_long`). Se documentan en la guía de errores que ya lista los del pedido.

### 5.3 Totales

`DocumentTotals` gana dos campos y una entrada:

```text
Compute(lines, freight: FreightAmounts, appliesVatSurplus, appliesRetention)

FreightAmounts(Subtotal, TaxAmount)      ← sólo con Charged; Zero en los demás casos
  en COP:   VatIncludedLine(qty 1, price = FreightAmount, discount 0, rate = FreightTaxPercentage)
  fuera:    Subtotal = FreightAmount, TaxAmount = 0

subtotal        = Σ líneas                       (sin cambio: sólo productos)
taxAmount       = vatSurplus ? 0 : Σ IVA líneas  (sin cambio: sólo productos)
freightSubtotal = freight.Subtotal
freightTax      = vatSurplus ? 0 : freight.TaxAmount
total           = subtotal + taxAmount + freightSubtotal + freightTax
retention       = appliesRetention ? round(subtotal × 0,025) : 0   (D-F6: sin el envío)
netTotal        = total − retention
taxPercentage   = taxAmount / subtotal × 100     (sin cambio)
```

Se mantiene el significado de `Subtotal` y `TaxAmount` (productos) porque los leen reportes, PDF, Excel y
frontend; el envío va en campos propios y `Total` los incluye. **Un total que no cierra se explica con un
campo:** toda respuesta que lleve `subtotal`, `taxAmount` y `total` lleva también `freightSubtotal` y
`freightTaxAmount` (DTOs de cotización, pedido, lista, reportes).

Consecuencias directas, sin código aparte: `NetTotal` incluye el envío cobrado, así que el estado de pago
(`RecalculatePaymentStatus`, H7) lo exige; `Total` de los eventos lo incluye (§8).

**Con código aparte: el mínimo de compra no cuenta el envío** (D-F7). Hoy se mide sobre `Total`, que ahora
incluiría el envío, así que un envío caro habilitaría el descuento por volumen. Se agrega
`ProductsTotal = Subtotal + TaxAmount` a `DocumentTotals` (y a los dos agregados) y se usa en los cuatro
lugares que hoy leen `Total` para el mínimo: `App/QuotationMinimumPurchase.cs:37`,
`App/QuotationResponseComposer.cs:96` y `App/OrderPricingRecalculation.cs:51,59`, cada uno con su prueba.

**Cambio de moneda** (D-F15). Cambiar la cuenta de cobro puede cambiar la moneda y re-precia las líneas desde
el catálogo (`Dom/Quotation.cs:567-600`); el envío no tiene de dónde re-preciarse. Si la moneda cambia, el
agregado **limpia el valor y el modo** del envío (conserva la transportadora y la contraentrega): si el asesor
quiere cobrar el envío, lo vuelve a escribir; nada lo exige (decisión 11). Un guardado que cambia la moneda y
reenvía el `shipping` de la pantalla no lo restaura: del cuerpo se aplican sólo la transportadora y la
contraentrega (R15). El frontend avisa al cambiar la cuenta.

### 5.4 Edición

- **Cotización:** crear, guardar y `preview` ganan `shipping: ShippingRequest?` junto a `parties` (§5.1).
  Ausente = el envío queda como estaba (un frontend viejo no lo borra). `QuotationChangeSummary` registra en el historial el cambio de transportadora, de modo, de
  valor y de contraentrega.
- **Pedido pendiente:** `SaveOrderEditsRequest` y `PreviewOrderEdits` ganan un bloque opcional `shipping`
  (`carrierId`, `freightChargeMode`, `freightAmount`, `cashOnDelivery`). **No** cambia recoger/enviar ni la
  dirección: el pedido no edita partes hoy (H5) y este spec no lo abre (D-F5). Ausente = sin cambios. Se
  registra en `order_history`. Tras el cambio se recalcula el estado de pago como en las demás ediciones.
  Prender la contraentrega en un pedido con comprobantes o con pago distinto de `PaymentPending` es 422
  `order.order.cash_on_delivery_pending_only` (D-F14): un pedido ya pagado no se cobra en la puerta.
- **Pedido manual:** `CreateOrderRequest` y su `preview` ganan `shipping: ShippingRequest?`.
- **Pedido externo:** `ExternalOrderDraft` no cambia en este spec; un pedido externo nace sin envío (modo
  nulo, montos 0). El campo `Shipping` del borrador externo lo agrega Shopify (§10).
- **Conversión:** `Order.FromQuotation` copia los nueve campos tal cual, incluidos `FreightTaxPercentage` y
  los montos: el pedido nace con los mismos totales que la cotización.

## 6. Contraentrega

Es una forma de **cobrar el pedido**, no de enviarlo: la transportadora le cobra al cliente al entregar y
después gira el recaudo. Puede combinarse con cualquier modo de envío; con «al cobro», el cliente paga en la
puerta el pedido más el flete.

- `CashOnDelivery = true` sólo con envío; con o sin transportadora, pero si la hay, que haga recaudo (§5.2).
- Un pedido contraentrega nace `PaymentPending` sin comprobantes: ya está permitido (H7). El manual y la
  conversión lo **fuerzan**: con `CashOnDelivery`, un `paymentStatus` distinto de `PaymentPending` o algún
  comprobante, aunque el estado sea pendiente, es 422 `order.order.cash_on_delivery_pending_only` (el dinero
  aún no llegó; la misma regla que prenderla en un pedido, R16).
- El giro se registra con un comprobante, como cualquier pago (P2, D-F9); `RecalculatePaymentStatus` lo
  lleva a pagado contra `NetTotal`.
- `PaymentMethod` (texto libre) no se toca: `CashOnDelivery` es el dato estructurado. El PDF y la lista
  muestran «Contraentrega» cuando está prendido.

## 7. Lecturas, PDF, Excel y reportes

**Respuestas.** `QuotationResponse`, `OrderDetailResponse`, la fila de la lista de pedidos y la de
cotizaciones ganan un objeto `shipping` con forma fija, también en recogida:

```json
"shipping": {
  "isStorePickup": false,
  "carrier": { "id": "…", "name": "Coordinadora" },
  "chargeMode": "Charged",
  "freightAmount": 15000,
  "freightTaxPercentage": 19,
  "freightSubtotal": 12605.04,
  "freightTaxAmount": 2394.96,
  "cashOnDelivery": false
}
```

`carrier` es `null` en recogida y en documentos viejos; `chargeMode` viaja con su nombre (el diccionario de
textos lo tiene el frontend). Los totales ganan `freightSubtotal` y `freightTaxAmount` (§5.3). El motivo
(BFF: la pantalla dibuja la tarjeta de entrega y la fila de totales sin recalcular nada) se escribe en el
DTO.

**PDF de la cotización** (`QuotationPdfDocument` y `quotation.typ`):

- Bloque de entrega: «Recoger en tienda» como hoy; si se envía, la dirección y «Transportadora:
  Coordinadora».
- Totales, después del descuento: `Charged` → «Envío (Coordinadora)» con el valor sin IVA y, debajo de la
  fila de IVA de hoy (que sigue siendo `taxAmount`, sólo productos), una fila propia «IVA del envío (19 %)»
  con `freightTaxAmount`; ninguna fila repite el monto de otra;
  `Free` → «Envío (Coordinadora): Gratis»; `OnDelivery` → «Envío (Coordinadora): al cobro, lo paga el
  cliente a la transportadora», sin monto en la suma (con el valor informativo si lo hay).
- «Forma de pago: Contraentrega» cuando `CashOnDelivery`; si no, lo de hoy.
- Las pruebas del PDF verifican el JSON que se manda a qcode-pdf, no el render.

**Excel de pedidos.**

- `carrier` («Transportadora»): `CarrierName`; recogida → «Recoger en tienda»; pedido sin transportadora
  (viejo o de Shopify sin reconocer) → la constante «Coordinadora» de hoy (D-F8), para no cambiar la salida
  de los pedidos que ya existen.
- `coordinadora_city` no cambia (sigue escribiendo el nombre de Coordinadora aunque el pedido vaya por otra;
  §12).
- Columnas nuevas, al final del catálogo, del pedido y repetidas por línea como `order_total`:

| Clave | Encabezado | Contenido | Visible por defecto |
| --- | --- | --- | --- |
| `freight_amount` | `Valor Flete` | `FreightAmount` si `Charged`; 0 en `Free`; vacío en recogida y `OnDelivery` | sí |
| `freight_without_tax` | `Flete sin IVA` | `FreightSubtotal` | no |
| `freight_tax` | `IVA Flete` | `FreightTaxAmount` | no |
| `freight_charge_mode` | `Pago Flete` | `Cobrado` / `Al cobro` / `Gratis` / vacío | no |
| `cash_on_delivery` | `Contraentrega` | `Sí` / `No` | no |

`Valor Flete` visible por defecto (D-F10): `Total facturado` ya lo incluye y sin esa columna el ERP no cuadra
las líneas contra el total.

**Reportes.** `OrdersReportSource` y `QuotationsReportSource` suman además `FreightSubtotal` y
`FreightTaxAmount` en el resumen por moneda, y la fila del listado los lleva. El resumen gana los dos campos
para que `subtotal + taxAmount + freightSubtotal + freightTaxAmount = total` cierre en la pantalla. Los
rankings y el mensual siguen sobre `Total`.

## 8. Eventos

Sin campos nuevos ni versión nueva: `total` ya es `Total`, que ahora incluye el envío cobrado. Pipeline
guarda ese valor en el negocio: un negocio pasa a valer lo facturado con envío. Se deja explícito en
`QuotationEventPayloadsTests` con un caso con envío.

## 9. Frontend (`qep-frontend`)

- **Configuración › Transportadoras** (`/settings/carriers`, sección nueva en `tenant-settings-page.tsx`,
  visible con el módulo de cotizaciones y `tenancy.settings.read`): selector «IVA del envío» con las tasas
  activas (lee `/catalog/tax-rates`; sin `catalog.tax_rate.read` muestra la actual sin poder cambiarla) y un
  enlace a Configuración › Tasas de impuesto, lista con orden por arrastre, nombre, nombres de Shopify,
  contraentrega, activa, editar y «Agregar transportadora». «Recoger en tienda» se explica como opción fija,
  no configurable.
- **Cotizador** (editor y pedido manual): la entrega empieza con dos opciones, «Recoger en tienda» y
  «Enviar». «Recoger en tienda» **sale** de la libreta de direcciones de `quote-shipping-party.tsx` (H15). Con
  «Enviar»: libreta de direcciones, transportadora (`/shipping-options`), «¿Quién paga el envío?» (se cobra
  en el pedido / al cobro / gratis) y el valor (con IVA incluido; habilitado al elegir quién paga, con o
  sin transportadora; en $ 0 y bloqueado con «Gratis»; opcional con «Al cobro»). Todo es opcional: la
  transportadora tiene «Sin transportadora» y quién paga se puede dejar sin elegir. Sin IVA del envío
  configurado, «Se cobra en el pedido» se deshabilita con un aviso que enlaza a Configuración.
- **Cobro:** sección «¿Cómo paga el cliente el pedido?»: «Anticipado» o «Contraentrega» (habilitada con
  «Enviar», con o sin transportadora; deshabilitada si la transportadora elegida no hace recaudo). Con contraentrega, el estado de pago del pedido manual queda fijo en
  pendiente.
- **Totales** (`quote-totals-summary.tsx`, `order-items-card.tsx`): filas «Envío sin IVA · Coordinadora» e
  «IVA del envío (19 %)», o «Envío · Gratis» / «Envío · Al cobro»; con contraentrega o al cobro, un recuadro
  «Al entregar, el cliente paga» con el total en la puerta.
- **Detalle del pedido** (`order-shipping-card.tsx`): transportadora, modo, valor y contraentrega; en
  pendiente, editables desde la edición del pedido.
- **Reportes:** el resumen muestra el envío.
- Los textos de `chargeMode`, de los códigos de error nuevos y de las columnas van al diccionario.
- El mockup de referencia es el lienzo «Entrega y flete del pedido» (privado; el owner lo comparte). Lo que
  fija, por si no se tiene a mano: en el editor, la tarjeta de entrega va donde hoy está la entrega y la
  sección «¿Cómo paga el cliente el pedido?» es una tarjeta propia **inmediatamente debajo**; la columna de
  totales de la derecha gana las filas del envío y, con contraentrega o al cobro, el recuadro «Al entregar,
  el cliente paga» debajo del total (pedido, si es contraentrega; flete al cobro, si lo hay; y su suma). En
  la edición del pedido pendiente, el bloque de envío (transportadora, quién paga, valor, contraentrega) va
  en la tarjeta de entrega, sin recoger/enviar ni dirección.

### 9.1 Tasas de impuesto en Configuración

Las tasas ya no son sólo del catálogo: las usan los productos y el envío. Son configuración fiscal del
tenant, y el selector «IVA del envío» queda a un clic de donde se crean. Sólo se mueve la pantalla:

- Las rutas `catalog/tax-rates/` (`index`, `new`, `$taxRateId`) pasan a `settings/tax-rates/`, con las mismas
  páginas de `features/catalog` (lista, formulario, detalle) y sus pruebas. Las migas de pan y los enlaces
  internos (`tax-rate-table.tsx`, `tax-rate-form-page.tsx`, `tax-rate-detail-page.tsx`) apuntan a la ruta
  nueva; el enlace de `products-list-page` que hoy lleva a `/catalog/tax-rates` también.
- La pestaña «Tasas de impuesto» sale de `catalog-tabs.tsx`.
- `tenant-settings-page.tsx` gana la sección «Tasas de impuesto», visible con `catalog.tax_rate.read` (un
  flag `showTaxRates` como los demás). Nadie gana ni pierde acceso: el permiso es el mismo de hoy.
- Las tres rutas viejas **redirigen** a las nuevas (`beforeLoad` con `redirect`, conservando `$taxRateId`),
  para no romper marcadores ni enlaces guardados.
- Backend: sin cambios. La API sigue en `/catalog/tax-rates` y los permisos siguen siendo
  `catalog.tax_rate.read` / `.manage`. El README que describa dónde está la pantalla se corrige.

## 10. Delta al spec de Shopify

Se aplica al spec de Shopify cuando éste se apruebe; lo implementa Shopify, no este spec.

- **§6.2:** `freight_unsupported` se retira. Entra `freight_tax_rate_missing`: el pedido trae envío con
  valor > 0 y el tenant no tiene IVA del envío → espera hasta configurarlo.
- **§6.2.2, flete:** se suman las `shippingLines` con `isRemoved = false`.
  - Recogida (`code` empieza por `pickup`, V-6) → `IsStorePickup`, sin flete.
  - `discountedPriceSet` = 0 → `Free` (sea por precio 0 o por descuento de envío gratis).
  - Si no → `Charged`, `FreightAmount` = `discountedPriceSet` si `taxesIncluded`; si no,
    `discountedPriceSet × (100 + tasa) / 100` con la tasa del envío del tenant (misma regla que el precio de
    los productos, D-S3). La diferencia con el IVA que calculó Shopify (`taxLines`) se avisa como la del
    total (D-S6), no frena.
  - «Al cobro» no existe en Shopify: nunca se asigna desde la tienda.
- **Transportadora:** el `title` de la línea de envío, normalizado (§4.1), se compara con el nombre y los
  `ExternalNames` de las transportadoras activas. Coincide → `CarrierId` y `CarrierName`. No coincide → el
  pedido entra **sin transportadora** con el aviso `carrier_unrecognized { shippingTitle }` en el espejo; el
  detalle del pedido ofrece elegirla y «reconocer este nombre en los próximos pedidos» (agrega el `title` a
  `ExternalNames`; operación del módulo de pedidos con `quotations.order.manage` + `tenancy.settings.update`
  para recordar el nombre).
- **Contraentrega:** pasarela de pago de contraentrega (el nombre exacto de `gateway` se verifica, V-8) →
  `CashOnDelivery = true`, con o sin transportadora reconocida (es un hecho de la tienda). Si la reconocida
  no hace recaudo según su configuración, entra igual con la marca y el aviso `cash_on_delivery_unmatched`.
- `ExternalOrderDraft` gana `Shipping` (`ShippingDetails` sin validar la transportadora contra el estado
  actual de recaudo, que el importador ya resolvió). Hasta entonces el camino externo no recibe envío
  (§5.4).
- **V-8 (nuevo):** el `gateway` y el `financial_status` que trae un pedido contraentrega de una tienda
  colombiana.

## 11. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias:** `CarrierTests` (nombre, normalización, nombres externos, activar/desactivar); `ProductsTotal` en el mínimo de
compra (cotización y pedido: un envío caro no habilita el descuento); cambio de moneda que limpia el envío;
`DocumentTotalsTests` (envío cobrado en COP con 19 %, gratis, al cobro, USD sin IVA, excedente de IVA,
retención sin el envío, redondeo a pesos); `QuotationTests` y `OrderTests` (coherencia de §5.2 caso por caso,
`Send` sin transportadora, conversión que copia los nueve campos y da los mismos totales, edición del envío
de un pedido pendiente, contraentrega sólo pendiente); `QuotationChangeSummaryTests`;
`OrdersExportProcessorTests` (transportadora real, recogida, pedido viejo → «Coordinadora», las cinco
columnas nuevas); `QuotationPdfDocumentMapperTests`; validadores de transportadoras;
`QuotationsDbContextMappingTests` (tablas y columnas nuevas); `QuotationEventPayloadsTests` con envío.

**Integración (Quotations):** CRUD de transportadoras con 403 de otro tenant, nombre repetido y nombre externo
repetido → 409/422 con el código por **nombre de índice**, reordenar con ids de más o de menos, IVA del envío
(tasa inactiva, de otro tenant, quitarla); `/shipping-options` sólo activas; cotización con envío cobrado
(totales, PDF JSON), gratis, al cobro, recogida que limpia, contraentrega con transportadora sin recaudo
(422), contraentrega sin transportadora (201), contraentrega con recogida (422), cobrar sin IVA configurado
(422), envío cobrado sin
transportadora (201), enviar y convertir sin transportadora ni envío (siguen pasando); conversión con y sin
envío; pedido
manual con envío y contraentrega con estado distinto de pendiente (422); edición del envío de un pedido
pendiente y su estado de pago; Excel columna por columna. **Reporting:** resumen que cierra con envío.
**Pipeline:** valor del negocio con envío.

**Sin barrido de cuerpos crudos:** como nada es obligatorio, las pruebas que arman cuerpos sin `shipping`
no cambian. Sí se revisan las que comparan `total` de un documento contra una suma fija: con envío cobrado
ya no es `subtotal + taxAmount`.

**Migración:** documentos existentes quedan con `carrier_id` nulo, modo nulo, montos 0, `cash_on_delivery`
falso y los mismos `total` y `net_total` (prueba con datos viejos).

**Arquitectura:** sin capas nuevas; `IQuotationTaxRateLookup` vive en Application de Quotations y su
adaptador en Bootstrapper. **Suite por módulo tocado** (Quotations, Reporting, Pipeline, Identity), no la
completa.

## 12. Fuera de alcance

- Cotizar el envío en vivo con la transportadora (Coordinadora primero): spec propio. Necesita peso y
  medidas en Catalog, origen de despacho del tenant y credenciales por tenant en Integrations.
- Guías, seguimiento y conciliación automática del recaudo contraentrega.
- La ciudad en el formato de cada transportadora (`coordinadora_city` sigue siendo de Coordinadora).
- Editar recoger/enviar o la dirección de un pedido ya creado.
- Tasa de IVA del envío distinta por transportadora.
- Mover la API o los permisos de las tasas de impuesto fuera de Catalog (sólo se mueve la pantalla, §9.1).
- POS (H16).

## 13. Decisiones tomadas sin el owner (a ratificar)

| ID | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-F1 | «Flete pago» se llama **«Al cobro»** en el producto (`OnDelivery` en el contrato): en el lenguaje de las transportadoras, «pagado» suele significar que paga el remitente | Un texto del diccionario |
| D-F2 | Una transportadora **no se borra**, se desactiva; los documentos guardan la foto del nombre | Un `DELETE` que sólo borre las nunca usadas |
| D-F3 | `GET /shipping-options` con `quotations.quotation.read`. Si el plan encuentra un rol con `quotations.order.manage` sin ese permiso, se agrega la ruta gemela bajo pedidos con `quotations.order.read` | Una ruta más |
| D-F4 | Transportadora y modo de envío son independientes: se puede cobrar un envío sin transportadora | Exigir transportadora para cobrar envío |
| D-F17 | Contraentrega con transportadora que no hace recaudo se rechaza (422); sin transportadora se acepta. Con recogida, también se rechaza | Aceptarla siempre |
| D-F5 | En un pedido pendiente se edita transportadora, modo, valor y contraentrega, pero no recoger/enviar ni la dirección | Abrir la edición de partes del pedido |
| D-F6 | La **retención no incluye el envío** (P1) | Una línea en `DocumentTotals` y sus pruebas |
| D-F7 | El mínimo de compra no cuenta el envío (`ProductsTotal`, §5.3) | Sumarlo: quitar `ProductsTotal` |
| D-F8 | Un pedido sin transportadora sigue exportando «Coordinadora» en `Transportadora` | Vacío |
| D-F9 | El giro del recaudo contraentrega se registra con un comprobante, como cualquier pago (P2) | Un flujo de liquidación por transportadora |
| D-F10 | De las cinco columnas nuevas del Excel, sólo `Valor Flete` nace visible | `DefaultVisible` |
| D-F11 | Una sola tasa de IVA para el envío, del tenant, no por transportadora | Un campo por transportadora |
| D-F12 | El IVA del envío se muestra en su propia fila y se mantiene fuera de `TaxAmount`, que sigue siendo de productos | Unificar y cambiar reportes |
| D-F13 | Un envío `Charged` exige valor > 0; $ 0 es `Free` | Permitir `Charged` en 0 |
| D-F14 | `CashOnDelivery` fuerza `PaymentPending` sin comprobantes al crear o convertir, y no se prende en un pedido con comprobantes o ya pagado | Dejarlo a criterio del usuario |
| D-F15 | Si la moneda del documento cambia, el valor y el modo del envío se limpian y se vuelven a escribir | Convertir con una tasa de cambio |
| D-F16 | Nombres y nombres externos de transportadoras comparten un solo índice único por tenant, inactivas incluidas | Dos índices |

## 14. Entregables

1. Backend: `Carrier` y su API, IVA del envío, campos y reglas en `Quotation` y `Order`, totales, edición,
   conversión, pedido manual, PDF, Excel, reportes, migración, README (sección de transportadoras) y guía de
   errores.
2. Frontend: Configuración › Transportadoras, tasas de impuesto movidas a Configuración con redirección
   desde Catálogo, entrega y cobro en el cotizador y el pedido manual, totales, detalle y edición del pedido,
   reportes.
3. Delta al spec de Shopify (§10) cuando se apruebe.
4. Plan de implementación (`writing-plans`) tras la lectura de este spec.

## Historial de revisión

- 2026-10-17: decisiones 1–12 tomadas en conversación con el owner (2026-10-16 y 2026-10-17), con mockups en el
  lienzo «Entrega y flete del pedido» y un inventario del código (§2); spec escrito. Pendientes P1 y P2, la
  lectura del owner y el plan.
- 2026-10-17: aprobado por el owner. P1 y P2 quedan con su valor por defecto hasta que diga otra cosa.
- 2026-10-17: implementación del backend (plan `docs/superpowers/plans/2026-10-17-flete-backend.md`, 12 tareas).
  Decisiones del plan: P1 el IVA del envío se calcula como una línea (`VatIncludedLine.Compute(1, monto, 0,
  tasa, moneda)`; $ 15.000 al 19 % da 12.605,04 + 2.394,96); P2 la tasa vive en `IQuotationSettingsStore`
  (`Get/SetFreightTaxRateIdAsync`) y no en `QuotationSettings`, para que el `PUT` del mínimo no la pise; P3 lo que
  el dominio no puede verificar (transportadora activa, recaudo, tasa del tenant) va en `ShippingResolver` y la
  coherencia en el dominio; P4 `shipping` ausente deja el envío, salvo recogida, que lo limpia; P5 el pedido no
  cambia de moneda, así que D-F15 sólo existe en `Quotation`. Resoluciones en curso: R1 la coherencia de recogida
  se valida en `SetShipping` y en las fábricas del pedido, no en `UpdateDetails`; R3 la propiedad calculada se
  llama `ShippingDetails` porque `Shipping` ya es la parte de entrega; R4 prender contraentrega en un pedido se
  rechaza con comprobantes o con pago distinto de `PaymentPending` (manda §5.4); R6, R8 y R10 las mutaciones de
  transportadoras y de la tasa se auditan (`quotations.carrier.*`, `quotations.freight_tax_rate.updated`) y el cambio
  de envío de un pedido como `quotation.order.shipping_changed`; R11 con contraentrega el PDF imprime «Forma de
  pago: Contraentrega» en lugar de la forma de pago escrita; R12 y R13 `OrdersExportLayout.Effective` agrega
  oculta la columna nueva del catálogo cuyo encabezado choca con una visible (la hoja sembrada conserva su fija
  «valor flete», vacía, hasta que el operador la cambie por la del catálogo), y un `PUT` parcial que omite la del
  catálogo y agrega una fija con su encabezado pasa a aceptarse; R15 un guardado que cambia la moneda descarta el
  modo y el valor del `shipping` del cuerpo (D-F15); R16 crear o convertir con contraentrega rechaza los
  comprobantes aunque el pago siga pendiente (D-F14). La plantilla Typst compila con `QuotationTemplateTests`
  (typst 0.15.1), y el renglón «IVA del envío» se oculta cuando vale cero. El frontend va en su propio plan.
