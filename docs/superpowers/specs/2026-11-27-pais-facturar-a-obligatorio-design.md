# País de «Facturar a» obligatorio en cotizaciones y pedidos

Fecha: 2026-11-27. Estado: **aprobado por el owner en conversación (2026-11-27), incluido el pedido
manual.** Tarjeta:
<https://trello.com/c/PGAmC4DJ>. Frontend: <https://trello.com/c/QolgF4Ma>.

## Por qué

El owner decidió (2026-11-27) que el país del bloque «Facturar a» no se asume: la persona lo elige
y el backend lo exige. Hoy un país vacío cuenta como Colombia en todas partes
(`QuotationPartyDetails.IsColombian`, `SiigoOrderSource.IsColombian`, `BillingPartyFiscalRules`).

Además, `QuotationPartyDetails.NormalizeCountry` **guarda `CO` como `null`**. En la base, una parte
colombiana y una parte sin país son la misma fila, así que hoy no se puede saber cuál «no tiene país».

## Decisiones del owner (todas en la tarjeta)

1. **Consola:** con una fila «Facturar a» con nombre y sin `country`, el guardado responde 422 con el
   campo en `errors`, por FluentValidation. Las vistas previas no lo exigen.
2. **Tienda pública, Shopify y API externa** no fallan por falta de país, y el país no se completa
   solo: el pedido entra sin país y queda pendiente.
3. **Lo ya guardado no se migra.** Para guardar una edición hay que elegir el país.
4. **Conversión de una cotización sin país:** se copia el vacío.
5. **Mientras falte el país** sólo se bloquea la factura en Siigo (faltante en la tanda y en la vista
   previa) y el pedido lo muestra. Pagos y despacho siguen.
6. (2026-11-27, en esta sesión) **`CO` se guarda literal desde este cambio, sólo en la facturación.**
   Lo viejo se interpreta al leer: `null` con `CityId` es Colombia (la regla de carril `EnsureLane`
   sólo admite ids de DIVIPOLA en Colombia); `null` sin ciudad es «pendiente de país». Sólo cuenta la
   fila de facturación con nombre (`OrderBilledParty.SourceFor`); sin ella el país sale de la ficha del
   cliente, como hoy.

## Lo verificado en el código (develop `4cfdd6b87`)

- **Quién escribe la facturación:**
  - Cotización (`CreateQuotation`, `SaveQuotation`) y pedido (`CreateOrder`, `SaveOrderEdits`) mandan
    lo que trae `QuotationPartyRequest.Country`, opcional.
  - La **tienda** siempre manda país: `BillingValidator` lo exige (`SubmitOrderRequest.cs:147`) y
    `CK_order_requests_billing_lane` lo amarra al carril. Hoy su `CO` se guarda como `null`.
  - **Shopify** nunca escribe facturación (`ShopifyOrderWriter.cs:221`, `Billing: null`).
  - La **API externa** (`ExternalQuotationService`) y el mayorista tampoco (`Parties: null`).
- **Una fila de facturación de pedido sin ciudad no se puede guardar:** `OrderParty.IsComplete` exige
  la ciudad, y `Order.EnsureBillingRules` responde `order.billing.party_incomplete`. Con la regla de
  lectura de la decisión 6, un pedido «pendiente de país» sólo puede ser una fila vieja que entró antes
  de esa exigencia. **El estado pendiente es defensivo:** con los escritores de hoy no se genera, y la
  decisión 2 ya se cumple sola (la tienda manda país; Shopify y la API no escriben facturación).
- **Lo que se rompe si la facturación trae `CO` literal**, porque prueban `Country is null`:
  - `OrdersExportProcessor.CityOf` (`:578`): la columna de ciudad de facturación del Excel saldría vacía.
  - `BuyerOrderGateway.DepartmentName` y `CityName` (`IBuyerOrderGateway.cs:401-405`) y la carga de
    nombres (`:315`): el comprador vería su pedido sin departamento ni ciudad.
- **Igualdades:** Shopify sólo compara la parte de envío (`ShopifyShippingFingerprint`,
  `OwnShippingPartyChanged`), que no cambia. Pero `Order.SetBilling` (`Order.cs:935`) y
  `QuotationChangeSummary` (`:432-445`) comparan `ToDetails()`. Una fila vieja (`null`) que se vuelve a
  guardar sin cambios con `CO` contaría como cambio: una entrada en el historial y una versión más.
- **`ExternalOrderInvoicingService.ResolvePartyAsync` (`:313-319`):** una fila con nombre sin país ni
  ciudad toma ciudad **y país de la ficha del cliente**, mezclando a dos personas. Con el pendiente no
  debe completarse.
- `QuotationPartyDto.Country` ya es `string?`, pero `OrderMapping.cs:165` y `QuotationMapping.cs:145`
  lo llenan con `?? "CO"` («siempre viaja»).

## Diseño

### 1. Dominio: el país de la facturación se guarda tal cual

- `QuotationPartyDetails.Normalized(prefix)` recibe si la parte es de facturación
  (`Normalized(prefix, keepColombia: true)`), desde sus dos únicos llamadores: `QuotationParty.Apply`
  y `OrderParty.Apply`, que ya ramifican por rol. Con `keepColombia`, `NormalizeCountry` devuelve
  `CO`; sin él (envío), sigue devolviendo `null`.
- `QuotationParty` y `OrderParty` ganan **`EffectiveCountry`**: `Country`, salvo una fila de facturación
  con `Country` `null` y `CityId`, que da `CO`. Es la regla de lectura de lo viejo, en un solo lugar.
- **`IsCountryPending`** (sólo facturación): `Name` no vacío, `Country` `null` y sin `CityId`.
- `ToDetails()` de una fila de facturación usa `EffectiveCountry`. Así:
  - volver a guardar una fila vieja sin cambios no cuenta como cambio (`SetBilling` y el resumen de
    cambios comparan lo mismo);
  - la conversión de una cotización copia `CO` cuando la fila vieja tenía ciudad, y el vacío cuando no
    (decisión 4).
- Envío: sin cambios.
- Sin migración y sin cambio de esquema: `country` sigue `varchar(2)` nullable en
  `quotation_parties` y `order_parties`.

### 2. Validación: 422 en el guardado de la consola

- Regla nueva **en el guardado, no en la vista previa**, junto a la del documento (se reutiliza la
  bandera `requireBillingIdentification`):
  - Cotización (`QuotationPartiesRequestValidator`, que usan `CreateQuotation` y `SaveQuotation`):
    `Parties.Billing.Country` no vacío cuando la fila de facturación trae nombre.
  - Edición de pedido (`OrderEditsValidator.AddBillingRules`): `Billing.Party.Country` no vacío con
    `HasNamedOwnParty`, el mismo criterio que el documento.
  - Pedido manual (`CreateOrderValidator` / `OrderDraftValidator`): la misma regla. **No está en la
    lista de la tarjeta**, pero es la consola creando una facturación (decisión 1). Se marca para que
    el owner lo confirme.
- Mensaje: `"The country is required when billing to someone else."`, como las reglas vecinas. Llega
  como 422 `validation.failed` con el campo en `errors`.
- Con consumidor final no aplica: la fila propia ya es otro error de dominio
  (`order.billing.final_consumer_conflict`).

### 3. Lectores que pasan a mirar el país efectivo

- `OrdersExportProcessor.CityOf` y `BuyerOrderGateway` (`DepartmentName`, `CityName` y la carga de
  nombres): dejan de probar `Country is null` y deciden con «es Colombia» (`EffectiveCountry` nulo o
  `CO`). El comportamiento con filas viejas no cambia.
- `OrderMapping` / `QuotationMapping`: la facturación viaja con `EffectiveCountry`. **Pendiente viaja
  `null`.** El envío sigue con `?? "CO"`.

### 4. El pedido muestra el pendiente

Para la pantalla, el pendiente es **`country: null` en la parte con rol `Billing`**: no hace falta un
campo nuevo.
- Con esta regla, una facturación con nombre siempre viaja con país salvo que esté pendiente, así que el
  `null` no es ambiguo.
- Se documenta en el DTO (`QuotationPartyDto.Country`): «en la facturación, `null` = país pendiente;
  la pantalla muestra el aviso y no deja guardar sin elegirlo».
- El frontend ya arranca el selector vacío (tarjeta QolgF4Ma); el aviso del pedido es su parte.

### 5. Siigo: el faltante

- Código nuevo **`customer_country_missing`** en `SiigoMissing`. La tarjeta proponía
  `billing_country_missing`; se usa `customer_` porque toda la familia que describe a quien se factura
  ya se llama así (`customer_identification_missing`, `customer_city_missing`). Sin referencia ni
  etiqueta: el dato es del cliente.
- Recorrido:
  - `ExternalInvoicingParty` gana `bool CountryPending`.
  - `ResolvePartyAsync` lo pone con `IsCountryPending` y, en ese caso, **no completa ciudad ni país
    con la ficha del cliente**.
  - `SiigoOrderSource` lo pasa a un campo nuevo de `SiigoBilledPartyInput`.
  - `SiigoInvoiceBuilder.CollectCustomerMissing` agrega `customer_country_missing` **en lugar de** la
    falta de ciudad: sin país no se sabe qué carril de ciudad pedir.
- Sale igual por los tres caminos que ya existen:
  - la vista previa (`SiigoInvoicePreviewDto.Missing`);
  - la emisión (422 `siigo.invoice.not_ready`, `details.missing`);
  - la tanda (`SiigoInvoiceBatchItemDto.Missing`).
- Va en la lista después de la identificación, como la ciudad hoy.
- Pagos, despacho y demás estados no miran el pendiente (decisión 5).

## Cambio de comportamiento a avisar

- La consola recibe 422 al guardar una facturación con nombre sin país, aunque hoy el vacío pase
  como Colombia. El frontend tiene que salir antes o junto (tarjeta QolgF4Ma).
- Una facturación colombiana nueva se guarda con `country = 'CO'`. Los lectores del Excel y del
  comprador se ajustan en este cambio; la API ya devolvía `CO`.

## Pruebas (TDD)

- **Unitarias de dominio** (`OrderPartyTests`, `QuotationPartyTests`):
  - facturación guarda `CO` literal; envío lo sigue guardando `null` (las pruebas actuales,
    `ColombiaIsStoredAsNullWhicheverWayItIsWritten`, usan envío y siguen pasando);
  - `EffectiveCountry` e `IsCountryPending` con los tres casos (`CO`, `null`+ciudad, `null` sin ciudad);
  - volver a guardar una fila vieja sin cambios no cambia la versión.
- **Validadores** (`SaveQuotationValidatorTests`, `CreateQuotationValidatorTests`,
  `SaveOrderEditsValidatorTests`, `CreateOrderValidatorTests`): 422 con el campo; la vista previa lo
  acepta; sin nombre o con consumidor final no aplica.
- **Lectores:** `OrdersExportProcessorTests` (ciudad con `CO` literal), `BuyerOrderGatewayTests`,
  `OrderExternalInvoicingTests` (el pendiente no toma la ficha).
- **Siigo:** `SiigoInvoiceBuilderTests` (el faltante reemplaza al de ciudad), `SiigoOrderSourceTests`
  (el campo viaja).
- **Integración, filtrada por nombre:** un 422 por API en cotización y en edición de pedido.

## Fuera de alcance

- Migrar o rellenar filas viejas (decisión 3).
- La parte de envío.
- Cambiar `IsColombian` para el resto de los módulos (Customers, Store, Buyers).

## Decisiones del owner sobre este spec

- El pedido manual (`CreateOrder`) también exige el país (§2). Aprobado el 2026-11-27.
