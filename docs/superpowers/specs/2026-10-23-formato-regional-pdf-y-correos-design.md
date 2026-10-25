# Formato regional del tenant en PDF, correos y WhatsApp

Fecha: 2026-10-23. Estado: aprobado en conversación por el owner (2026-10-23), sin empezar — pendiente. Ampliado el
2026-10-25 con el historial de cambios y las fechas de texto del Excel (D-R6..D-R8, owner). Sólo `qep-backend`: el
frontend ya aplica el formato del tenant (`RegionalFormatBoundary`, `src/lib/regional-format.ts`). Plan:
[`2026-10-25-formato-regional-pdf-y-correos.md`](../plans/2026-10-25-formato-regional-pdf-y-correos.md). Verificado contra
`develop` en `baf9aba2`; la ampliación, contra `68c1ca7f`.

## Por qué

El tenant ya guarda su formato regional —`DateFormat`, `NumberFormat`, `TimeZone` y `DefaultCurrency` en `Tenant`
(`Tenant.cs:54-60`)— y la SPA lo respeta. Lo que el backend le manda a una persona no:

- **El PDF** de cotización y de pedido agrupa miles con `.` y marca decimales con `,` a mano, en la plantilla Typst
  (`quotation.typ:72-82`, `:87-96`, `:102-115`). Un tenant con `1,234.56` ve en la pantalla `$ 2,450.50` y en el PDF
  `$ 2.450,50`.
- **Los correos** de exportación lista imprimen el vencimiento con `dd/MM/yyyy HH:mm` fijo
  (`CustomerExportEmailTemplate.cs:30-31`, `ProductExportEmailTemplate.cs:29-30`,
  `QuotationsExportReadyEmailTemplate.cs:27-28`) y los conteos sin agrupar.
- **El WhatsApp** (Zenvia) arma el total con `CultureInfo("es-CO")` (`ZenviaWhatsAppSender.cs:30-36`), sin mirar el
  tenant.

- **El historial de cambios** de cotización y pedido congela en `quotation_history.details` / `order_history.details`
  la vigencia como `yyyy-MM-dd` (`QuotationChangeSummary.cs:299-300`), las cantidades y el valor del envío con
  `"0.##"` invariante, sin agrupar (`:304-307`), y la escala del descuento global interpolada (`:90`, `:157`).
- **Los Excel** de clientes y de cotizaciones escriben sus fechas de texto con `yyyy-MM-dd HH:mm` fijo
  (`ClosedXmlCustomerExportBuilder.cs:122-123`, `QuotationsExportProcessor.cs:112`).

**Objetivo:** que esos cinco lugares usen el formato regional del tenant, con el comportamiento colombiano de hoy como
respaldo cuando falta. El Excel de pedidos que importa el ERP **no** cambia (D-R8).

**Dos lugares donde un cambio de formato tiene consecuencias fuera de la pantalla:** el historial (el texto se congela al
escribirse y no se reescribe nunca: un tenant que cambia de formato ve las filas viejas con el anterior) y la hoja del
ERP (la lee un sistema, no una persona: por eso queda fija).

## Decisiones

| ID | Decisión | Origen |
| --- | --- | --- |
| D-R1 | En el **PDF** y en el **WhatsApp** la fecha sigue **larga** («6 de septiembre de 2026»); ahí sólo se aplican los separadores numéricos del tenant (`NumberFormat`). Una fecha larga en un documento comercial no se puede leer mal; `MM/dd` contra `dd/MM` sí. Los nombres de los meses siguen en español: i18n queda fuera. | owner |
| D-R2 | Los **correos** usan el `DateFormat` del tenant más `" HH:mm"` (24 h), en la zona horaria del tenant, y la agrupación de `NumberFormat` para los conteos. | owner |
| D-R3 | Un **PDF guardado** se regenera en el siguiente pedido cuando el `NumberFormat` del tenant es distinto del que usó al generarse: se persiste el formato con el que se generó cada PDF (columna nueva, migración chica) y entra en `IsStaleFor`, igual que hoy el id del logo. Una fila vieja con `null` cuenta como generada con `"1.234,56"` (lo que la plantilla hace hoy), para que no se regeneren todas de golpe. | owner |
| D-R4 | El **símbolo y los decimales** de la moneda siguen saliendo de `Currencies.Get` (`Currencies.cs:46`; spec multi-moneda D8: el símbolo es fijo por moneda). El formato del tenant sólo decide los separadores. | owner (ratifica D8) |
| D-R5 | **Sin datos regionales** (el tenant no tiene fila, o un valor que no se reconoce) rige el comportamiento colombiano de hoy: `dd/MM/yyyy` y `1.234,56`. | owner |
| D-R6 | El **historial de cambios** de cotización y de pedido (`QuotationChangeSummary`, `OrderChangeSummary`) imprime las fechas con el `DateFormat` del tenant, sólo fecha (la vigencia es `DateOnly`), y todo número con los separadores del tenant: cantidades, valor del envío y escala del descuento global, con el patrón `"#,##0.##"` (hasta dos decimales, sin ceros a la derecha, **con** agrupación: `1500` → `1.500` y `1.5` → `1,5` en `1.234,56`). Sólo filas **nuevas**: las existentes son texto guardado y no se reescriben (sin migración ni backfill). Rige D-R5. | owner, 2026-10-25 |
| D-R7 | En los **Excel**, las celdas de fecha **de texto** usan el `DateFormat` del tenant más `" HH:mm"`, en su zona horaria (misma regla que D-R2): `Creado`/`Actualizado` de clientes y `CreatedAt` de cotizaciones. Las celdas **numéricas no se tocan**: son celdas tipadas con formato numérico de Excel, y Excel las muestra con la configuración regional de quien abre el archivo. Los nombres de archivo no cambian. | owner, 2026-10-25 |
| D-R8 | El **Excel de pedidos** (`OrdersExportProcessor`, el layout homologado que importa el ERP) queda exactamente como hoy: `Fecha Pedido` en `yyyy-MM-dd` (`:227`), las fechas de pago (`:435-436`) y cada celda. Lo lee un ERP, y un cambio de formato le puede romper la importación en silencio. Una prueba lo fija con un tenant en `MM/dd/yyyy` y `1,234.56`. | owner, 2026-10-25 |

## Qué se construye

### 1. Un solo helper, en `Modules.Tenancy.Application`

`RegionalNumberFormat` (static, nuevo) en `Modules.Tenancy.Application`, junto a `Currencies` y `TenantCalendar`:

- `Separators(string? numberFormat)` → `(string Thousands, string Decimal)`: `("." , ",")` para `"1.234,56"`,
  `(",", ".")` para `"1,234.56"`, y los colombianos (D-R5) para `null` o cualquier otro valor.
- `ToNumberFormatInfo(string? numberFormat)` → un `NumberFormatInfo` de sólo lectura sobre `InvariantCulture` con
  `NumberGroupSeparator`, `NumberDecimalSeparator` y `NumberGroupSizes = [3]` de esos separadores.

Junto a él, `RegionalDateFormat` (ampliación 2026-10-25, la usan el historial y los Excel): `Pattern(string? dateFormat)`
devuelve el formato si es uno de los permitidos y `dd/MM/yyyy` si no (D-R5), y `WithTime(string?)` le suma `" HH:mm"`.
Los formatos permitidos salen del `HashSet` privado de `Tenant` (`Tenant.cs:8-13`) a `TenantDateFormats`, público en
`Modules.Tenancy.Domain`, gemelo de `TenantNumberFormats`.

Los dos valores aceptados son los de `TenantNumberFormats` (`TenantNumberFormats.cs:9-10`, en `Modules.Tenancy.Domain`),
que este helper reutiliza en vez de repetir los literales.

**Por qué ahí:** es el único lugar que ven todos los consumidores sin romper `tests/ArchitectureTests`.
`Modules.Quotations.Application` referencia `Modules.Tenancy.Application` (`Modules.Quotations.Application.csproj:11`) y
su regla sólo admite `Modules.Tenancy*` entre los módulos de negocio (`QuotationsLayerTests.cs:68-77`);
`Modules.Notifications.Infrastructure` también lo referencia (`Modules.Notifications.Infrastructure.csproj:13`).
`Modules.Notifications.Application` **no** (sólo su Domain, `Modules.Notifications.Application.csproj:4`) y así se queda:
sus plantillas reciben primitivos (§3).

### 2. PDF de cotización y de pedido (Quotations)

**Payload.** `QuotationPdfDocument` (`QuotationPdfDocument.cs:15-87`) suma dos campos con default, para que ningún
llamador ni prueba existente cambie de forma:

```csharp
string ThousandsSeparator = ".",
string DecimalSeparator = ","
```

Viajan como `data.thousandsSeparator` y `data.decimalSeparator` (`JsonSerializerDefaults.Web`,
`QCodePdfRenderer.cs:26`).

**Mapper.** `QuotationPdfDocumentMapper.From` (`QuotationPdfDocumentMapper.cs:26-64`) y `FromOrder` (`:75-119`)
reciben además el `NumberFormat` del tenant y llenan los dos campos con `RegionalNumberFormat.Separators`. `IssuedOn` y
`ValidUntil` no cambian: siguen como `DateOnly` y la plantilla los imprime largos (D-R1).

**Plantilla** (`quotation.typ`):

- `miles()` (`:72-82`) une con `data.thousandsSeparator` en vez de `"."` (`:82`).
- `importe()` (`:87-96`) y `cantidad()` (`:102-115`) usan `data.decimalSeparator` en vez de `","` (`:94`, `:109`,
  `:111`, `:113`).
- `porcentaje()` (`:117-119`) no tiene ninguna llamada en la plantilla: se borra en vez de adaptarla.
- `fecha()` (`:66-69`) y `meses` (`:58-61`) no cambian (D-R1).

**De dónde sale el formato.** `QuotationPdfProvider` (`QuotationPdfProvider.cs:20-80`) y `OrderPdfProvider`
(`OrderPdfProvider.cs:24-48`) inyectan `ITenantDirectory` y leen `GetRegionalSettingsAsync`
(`ITenantDirectory.cs:35`), que devuelve `TenantRegionalSettings(TimeZone, DateFormat, DefaultCurrency, NumberFormat)`
(`:39-43`). Es una lectura de Tenancy más por export, del mismo tamaño que la del logo (`QuotationPdfProvider.cs:37`).
Usar el puerto directo, sin uno propio en Quotations, es lo que la regla de capas ya permite; el del logo existe porque
también baja bytes de Storage.

**Caché (D-R3).** Sólo la cotización tiene caché; el PDF del pedido se genera en cada envío
(`OrderPdfProvider.cs:17-23`), así que no necesita nada más.

- `QuotationPdf` (`QuotationPdf.cs:14-81`) suma `string? NumberFormat`, el formato con el que se generó. `Generate` y
  `Regenerate` (`:55-62`, `:73-80`) lo reciben.
- `IsStaleFor(long quotationVersion, Guid? logoFileId, string numberFormat)` (hoy `:70-71`) agrega
  `|| (NumberFormat ?? LegacyNumberFormat) != numberFormat`, con `LegacyNumberFormat = "1.234,56"` como constante privada
  del dominio y un comentario que la ata a `TenantNumberFormats.CommaDecimal`. El dominio de Quotations no referencia
  Tenancy: el literal va acá, comentado.
- El provider (`QuotationPdfProvider.cs:41`) pasa el formato vigente, ya resuelto con el respaldo de D-R5, y registra el
  que de verdad imprimió (`:66-76`).
- **Migración** en `QuotationsDbContext` (`quotations.quotation_pdfs`, configuración en `QuotationsDbContext.cs:390-406`):
  columna `number_format varchar(16) null`, sin backfill. Es aditiva: el código anterior la ignora.

### 3. Correos de exportación (Notifications)

**Contrato de las plantillas.** Record nuevo en `Modules.Notifications.Application`:

```csharp
public sealed record EmailRegionalFormat(TimeZoneInfo TimeZone, string DateFormat, NumberFormatInfo Numbers)
```

con un `Colombian(TimeZoneInfo timeZone)` que devuelve `dd/MM/yyyy` y `1.234,56` (D-R5). `CustomerExportEmailTemplate`,
`ProductExportEmailTemplate` y `QuotationsExportReadyEmailTemplate` reemplazan su parámetro `TimeZoneInfo timeZone`
(`CustomerExportEmailTemplate.cs:24`, `ProductExportEmailTemplate.cs:24`, `QuotationsExportReadyEmailTemplate.cs:22`)
por `EmailRegionalFormat format`, y:

- el vencimiento se imprime con `format.DateFormat + " HH:mm"` en `InvariantCulture`, en la zona de `format.TimeZone`
  (D-R2). `InvariantCulture` es a propósito: en un patrón .NET `/` es el separador de fecha de la cultura, e invariante
  lo deja en `/`;
- los conteos se imprimen con `ToString("N0", format.Numbers)`: `rowCount` (`QuotationsExportReadyEmailTemplate.cs:31`,
  hoy `InvariantCulture` sin agrupar) y también `customerCount` y `productCount`
  (`CustomerExportEmailTemplate.cs:32`, `ProductExportEmailTemplate.cs:31`, hoy interpolados con la cultura del
  proceso). El singular («1 cliente») no cambia.

**Quién lo arma.** Los workers de Infrastructure, que ya tienen `ITenantDirectory` y `ITenantClock` en su
`DeliveryContext` (`OutboxDeliveryWorker.cs:234-239`): `CustomerExportDeliveryWorker.cs:38-45`,
`ProductExportDeliveryWorker.cs:36-43` y `QuotationsExportReadyDeliveryWorker.cs:36-43` leen además
`GetRegionalSettingsAsync` y construyen el record con `calendar.TimeZone`, el `DateFormat` del tenant y
`RegionalNumberFormat.ToNumberFormatInfo`. Sin fila, `EmailRegionalFormat.Colombian(calendar.TimeZone)`.

`InvitationEmailTemplate` y `QuotationsExportFailedEmailTemplate` no imprimen fechas ni números: no cambian.

**Cuarto correo (verificado 2026-10-25):** `SupplierExportEmailTemplate` y `SupplierExportDeliveryWorker` llegaron después
de este spec, con el mismo vencimiento `dd/MM/yyyy HH:mm` y el mismo conteo. Reciben el mismo tratamiento.

### 4. WhatsApp (Zenvia)

- `WhatsAppQuotationMessage` y `WhatsAppOrderMessage` (`IWhatsAppSender.cs:31-39`, `:46-53`) suman
  `string? NumberFormat = null`, crudo como el resto del contrato: el formato sigue siendo del adaptador
  (`IWhatsAppSender.cs:27-29`).
- `SendQuotationHandler` (`SendQuotation.cs:122-131`) y `SendOrderHandler` (`SendOrder.cs:112-120`) lo llenan con
  `ITenantDirectory.GetRegionalSettingsAsync`.
- `ZenviaWhatsAppSender.FormatTotal` (`ZenviaWhatsAppSender.cs:32-36`) usa
  `RegionalNumberFormat.ToNumberFormatInfo(message.NumberFormat)` en vez de `es-CO`: `$ 2,450.50` para `1,234.56`. Sirve
  a la cotización (`:86`) y al pedido (`:149`).
- `valid_until` (`:87-88`) **sigue** con `"d 'de' MMMM 'de' yyyy"` y `es-CO` (D-R1): el campo `Colombia` (`:30`) queda,
  sólo para los nombres de los meses.

### 5. Historial de cambios (D-R6)

**Qué imprime un número o una fecha hoy** (todo en `QuotationChangeSummary`; `OrderChangeSummary` delega en él):

| Texto | Método | Hoy |
| --- | --- | --- |
| «Agregó X x3.» | `ItemAdded` (`:36-37`) | `Number` → `"0.##"` invariante |
| «Cambió la cantidad de X: 3 → 5.» | `ItemQuantityChanged` (`:39-41`) | ídem |
| «valor del envío (15000)» | `AppendFreight` (`:255`) | ídem |
| «vigencia (2026-12-31)» | `HeaderChanged` (`:128`) | `Date` → `yyyy-MM-dd` (`:299-300`) |
| «escala desde 1000 unidades» | `GlobalScaleFloorChanged` (`:89-91`), `HeaderChanged` (`:157`) | `int` interpolado |

`PaymentProofsChanged` (`OrderChangeSummary.cs:57-76`) también imprime números —cuántos comprobantes se agregaron,
corrigieron o retiraron en un guardado— y **no cambia**: son conteos de un solo guardado, muy por debajo de 1.000, y
con `"N0"` el texto sería idéntico. Llevarle el formato a `RemoveOrderPaymentProofHandler`, que siempre escribe `1`,
sería una lectura de Tenancy para nada.

**Contrato.** Record nuevo en `Modules.Quotations.Application`:

```csharp
public sealed record HistoryFormat(string DateFormat, NumberFormatInfo Numbers)
```

con `For(TenantRegionalSettings? settings)` (usa `RegionalDateFormat.Pattern` y `RegionalNumberFormat.ToNumberFormatInfo`:
D-R5 sale solo), `Colombian`, `Date(DateOnly)` y `Number(decimal)` (`"#,##0.##"`). Los métodos de los resúmenes que
imprimen un número o una fecha (`ItemAdded`, `ItemQuantityChanged`, `HeaderChanged`, `GlobalScaleFloorChanged`,
`AppendFreight` y sus pares de `OrderChangeSummary`) reciben un `HistoryFormat` **obligatorio**: así el compilador
encuentra cada llamador y ninguno queda con el formato colombiano por olvido.

**Quién lo lee.** Una lectura de `ITenantDirectory.GetRegionalSettingsAsync` por handler que escribe historial con
número o fecha —ocho—: `AddQuotationItemHandler`, `UpdateQuotationItemHandler`, `BatchUpdateQuotationItemsHandler`,
`SaveQuotationHandler`, `CreateQuotationHandler` (sus líneas pasan por `QuotationItemEditHistory.RecordAsync`),
`AddOrderItemsHandler`, `SaveOrderEditsHandler` y `RecognizeOrderCarrierHandler`. `QuotationEditsApplication.ApplyAsync`
lo comparten el guardado y dos cálculos previos (`PreviewQuotation`, `QuotationDraftBuilder`) que no escriben historial:
en vez de pasarle el formato, `QuotationEditsOutcome` devuelve las dos fotos del encabezado y el resumen lo arma
`SaveQuotationHandler`. Los previews no ganan ninguna lectura.

### 6. Excel con fechas de texto (D-R7 y D-R8)

- **Clientes:** `ICustomerExportBuilder.Build` (`ICustomerExportBuilder.cs:15-18`) recibe además el patrón ya resuelto
  (`RegionalDateFormat.WithTime(settings?.DateFormat)`), que `ExportCustomersHandler` (`ExportCustomers.cs:76-79`) lee con
  `ITenantDirectory`. `Modules.Customers.Application` ya referencia `Modules.Tenancy.Application`
  (`Modules.Customers.Application.csproj:10`, y `CustomersLayerTests.cs:66-74` lo permite), así que la regla de capas
  no cambia. `Creado` y `Actualizado` (`ClosedXmlCustomerExportBuilder.cs:122-123`) usan ese patrón en
  `InvariantCulture`; el comentario de `:119-121` (texto y no fecha de Excel) sigue valiendo.
- **Cotizaciones:** `QuotationsExportProcessor` (`:12-18`) inyecta `ITenantDirectory`, lee una vez por job junto al
  calendario (`:51`) y `ToCells` (`:106-112`) usa el patrón.
- **Proveedores y productos:** no tienen fechas de texto en celdas (sólo el nombre del archivo,
  `ClosedXmlSupplierExportBuilder.cs:81`, `ExportProducts.cs:232`): nada que cambiar.
- **Celdas numéricas:** no se tocan en ningún export. Son celdas tipadas con formato de Excel (`#,##0`, `#,##0.00`), y
  Excel las localiza para quien abre el archivo; escribirlas como texto con los separadores del tenant rompería sumas y
  filtros.
- **Pedidos (D-R8):** `OrdersExportProcessor` no se toca.

## Restricciones

- Sin rediseño: ningún texto, layout ni plantilla de Zenvia cambia más allá de separadores, fechas de correo, fechas y
  números del historial nuevo y fechas de texto del Excel.
- La plantilla de Zenvia aprobada no cambia: la variable `total` es la misma, sólo su contenido.
- Ningún default silencioso nuevo distinto de D-R5. `ITenantClock` sigue sin default a UTC (`ITenantClock.cs:8-10`).
- La regla de capas no se toca: ni `Modules.Notifications.Application` gana referencias, ni Quotations ve otro módulo que
  Tenancy.

## Fuera de alcance

- i18n: nombres de meses u otros textos en otro idioma.
- El Excel de pedidos que importa el ERP (D-R8) y los nombres de archivo de todos los exports (`ExportJobSupport.cs:37`,
  `ClosedXmlCustomerExportBuilder.cs:76`, `ClosedXmlSupplierExportBuilder.cs:81`, `ExportProducts.cs:232`): legibles por
  máquina a propósito.
- Reescribir filas de historial ya guardadas (D-R6).
- Las celdas numéricas de los Excel (D-R7).
- Impuestos por moneda, `PosLimits` por moneda y la columna de moneda en la exportación de pedidos al ERP.
- Las variables de texto del constructor de chatbots (`RunVariables.FormatText`, `RunVariables.cs:70-77`): imprimen en
  invariante valores que llegan de una API externa, no importes que el backend conozca con su moneda.
- El frontend: ya aplica el formato del tenant.

## Pruebas (TDD: cada una en RED antes del cambio que la pone verde)

**Tenancy.Application** — `RegionalNumberFormatTests` (nuevo): separadores de `"1.234,56"`, de `"1,234.56"`, y los
colombianos para `null` y para `"x"`; `ToNumberFormatInfo("1,234.56")` formatea `2450.5m` con `N2` como `2,450.50`.

**Notifications.UnitTests** (hoy afirman `31/12/2026 23:00` y `13/09/2026 10:30`):

- `CustomerExportEmailTemplateTests` (`:22`, `:77`), `ProductExportEmailTemplateTests` (`:12`) y
  `QuotationsExportEmailTemplateTests` (`:21`): con `MM/dd/yyyy` y `1,234.56`, el vencimiento sale `12/31/2026 23:00`
  y un conteo de `1234` sale `1,234`; con `yyyy-MM-dd`, `2026-12-31 23:00`. Las afirmaciones existentes pasan a usar
  `EmailRegionalFormat.Colombian(...)` y siguen pidiendo `31/12/2026 23:00`.
- Una prueba por worker (o por el armado del record) para el respaldo D-R5 sin fila regional.

**Quotations.UnitTests:**

- `ZenviaWhatsAppSenderTests`: con `NumberFormat = "1,234.56"`, `€ 2,450.50` y `$ 2,450,000` en cotización y pedido
  (junto a `:56`, `:67` y `:217`, que hoy afirman `€ 2.450,50` y `$ 2.450.000` y siguen así sin formato); la vigencia
  sigue `30 de septiembre de 2026` con cualquier formato (`:80`).
- `QuotationPdfDocumentMapperTests` y `OrderPdfDocumentMapperTests`: `ThousandsSeparator`/`DecimalSeparator` para cada
  formato y para el respaldo.
- `QuotationTemplateTests`: `EveryFieldTheTemplateReadsExistsInThePayload` (`:43`) cubre los dos campos nuevos sin
  cambios; una prueba de fuente nueva afirma que `miles()` une con `data.thousandsSeparator` y que no queda `"," +`
  ni `join(".")` en los helpers de importe; `TheTemplatePrintsTheIssueDateFromItsLocalDate` (`:124-129`, que busca
  `fecha(data.issuedOn)`) sigue verde, y eso es lo que prueba D-R1. Las de compilación (`:74`, `:83`, `:95`) suman un
  documento con `1,234.56`.
- `QuotationPdfTests` (hoy `IsStaleFor` en `:25-93`): vencido cuando cambia el formato; no vencido con `null` guardado y
  `"1.234,56"` vigente; vencido con `null` guardado y `"1,234.56"` vigente.
- `ExportQuotationPdfHandlerTests` (junto a `ChangingTheTenantLogoRegenerates`, `:85`): cambiar el `NumberFormat`
  regenera; exportar dos veces con el mismo no.

**Quotations.IntegrationTests:**

- Prueba de migración (patrón de `FreightMigrationTests.cs`): una fila de `quotation_pdfs` anterior queda con
  `number_format` nulo y no se regenera con el tenant en `1.234,56`.
- `QuotationPdfExportApiTests` (patrón de `:77`) y `OrderSendApiTests` (`:54`): con el tenant en `1,234.56`,
  `StubPdfRenderer.LastDocument` (`QuotationsApiHarness.cs:892-901`) trae `(",", ".")`; en la cotización, cambiar el
  formato y volver a exportar llama otra vez al renderer.

**Historial (D-R6), Quotations.UnitTests:** `HistoryFormatTests` (nuevo): `1500` → `1.500` y `1.5` → `1,5` en
`1.234,56`; `1,500` y `1.5` en `1,234.56`; `3` → `3`; la vigencia `2026-12-31` sale `31/12/2026`, `12/31/2026` o
`2026-12-31` según el formato, y sin fila, `31/12/2026`. En `QuotationChangeSummaryTests` y `OrderChangeSummaryTests`,
las afirmaciones de hoy pasan a `HistoryFormat.Colombian` y cambian donde había un número de cuatro cifras o más:
`valor del envío (15.000)`, `(18.000)`, `escala desde 1.000 unidades`. En los handlers, una prueba que fija que el
texto nuevo sale con el formato del tenant (`CreateQuotationHandlerTests`, `RecognizeOrderCarrierHandlerTests`).

**Excel (D-R7, D-R8):** `QuotationsExportProcessorTests`: `CreatedAt` con `dd/MM/yyyy` sale `12/09/2026 10:30`, y con
`yyyy-MM-dd` sigue `2026-09-12 10:30`. `CustomerExportApiTests`: con el tenant en `MM/dd/yyyy`, `Creado` y
`Actualizado` salen `12/31/2026 23:00`. `OrderExportApiTests`: con el tenant en `MM/dd/yyyy` y `1,234.56`, la hoja del
ERP trae `Fecha Pedido` en `yyyy-MM-dd` y los valores numéricos como número, igual que hoy.

## Archivos

| Archivo | Líneas | Qué |
| --- | --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` | 8-12, 54-60 | Formatos de fecha permitidos y propiedades regionales |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantNumberFormats.cs` | 9-10 | `"1.234,56"` y `"1,234.56"` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs` | 35, 39-43 | `GetRegionalSettingsAsync`, `TenantRegionalSettings` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantClock.cs` | 12-15 | Calendario del tenant |
| `src/Modules/Tenancy/Modules.Tenancy.Application/TenantCalendar.cs` | 27 | `ToLocal` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/Currencies.cs` | 9, 46 | Símbolo y decimales por moneda |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs` | 26-64, 75-119 | `From` y `FromOrder` |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs` | 15-87 | Payload del PDF |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs` | 37, 41, 61-76 | Caché y regeneración |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderPdfProvider.cs` | 17-48 | PDF del pedido, sin caché |
| `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdf.cs` | 51, 55-80 | `LogoFileId`, `IsStaleFor`, `Regenerate` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` | 390-406 | Mapeo de `quotation_pdfs` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ` | 58-61, 66-69, 72-82, 87-96, 102-115, 117-119 | `meses`, `fecha`, `miles`, `importe`, `cantidad`, `porcentaje` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/QCodePdfRenderer.cs` | 26, 43 | Serialización de `data` |
| `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSender.cs` | 27-39, 46-53 | Mensajes de cotización y pedido |
| `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs` | 122-131 | Arma el mensaje de la cotización |
| `src/Modules/Quotations/Modules.Quotations.Application/SendOrder.cs` | 112-120 | Arma el mensaje del pedido |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs` | 30-36, 86-88, 149 | `es-CO`, total y vigencia |
| `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` | 144, 161 | `POST …/send`, `POST …/pdf` |
| `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` | 114 | `POST /orders/{id}/send` |
| `src/Modules/Notifications/Modules.Notifications.Application/CustomerExportEmailTemplate.cs` | 24, 30-32 | Vencimiento y conteo |
| `src/Modules/Notifications/Modules.Notifications.Application/ProductExportEmailTemplate.cs` | 24, 29-31 | Vencimiento y conteo |
| `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs` | 22, 27-31 | Vencimiento y `rowCount` |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs` | 234-239 | `DeliveryContext` |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs` | 38-45 | Llama a la plantilla |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/ProductExportDeliveryWorker.cs` | 36-43 | Llama a la plantilla |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs` | 36-43 | Llama a la plantilla |
| `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs` | 38-43 | Ejemplos de la plantilla de Meta (DP-1) |
| `src/Modules/Notifications/Modules.Notifications.Application/SupplierExportEmailTemplate.cs` | 24, 30-32 | Cuarto correo (2026-10-25) |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationChangeSummary.cs` | 36-41, 89-91, 128, 157, 255, 299-307 | Números y fechas del historial (D-R6) |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderChangeSummary.cs` | 17-42, 57-76 | Delegados del pedido; comprobantes sin cambio |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationEditsApplication.cs` | 9-12, 101-102 | `QuotationEditsOutcome` y el resumen del encabezado |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationItemEditHistory.cs` | 15-48 | Historial de líneas compartido |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs` | 12-18, 51, 106-112 | `CreatedAt` de texto (D-R7) |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` | 227, 435-436 | Hoja del ERP, sin cambio (D-R8) |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerExportBuilder.cs` | 15-18 | Contrato del builder |
| `src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs` | 31-41, 76-79 | Lee el calendario y arma el archivo |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs` | 44-47, 119-123 | `Creado`, `Actualizado` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` | 637 | `TheSeededLayoutProducesTheErpImportSheet` |
| `tests/ArchitectureTests/ArchitectureTests/QuotationsLayerTests.cs` | 68-77 | Quotations sólo ve `Modules.Tenancy*` |
| `tests/ArchitectureTests/ArchitectureTests/NotificationsLayerTests.cs` | 21-27 | Application no ve Infrastructure |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/CustomerExportEmailTemplateTests.cs` | 22, 77-83 | `31/12/2026 23:00` |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/ProductExportEmailTemplateTests.cs` | 12-26 | `31/12/2026 23:00` |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs` | 21-33 | `42 pedidos`, `13/09/2026 10:30` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ZenviaWhatsAppSenderTests.cs` | 56-90, 217 | `€ 2.450,50`, `$ 2.450.000`, vigencia |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs` | 38-56 | Moneda y fecha de emisión |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPdfDocumentMapperTests.cs` | 22-46 | Mapper del pedido |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs` | 43, 74-95, 124-129 | Campos, compilación y `fecha(data.issuedOn)` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfTests.cs` | 25-93 | `IsStaleFor` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs` | 38-143 | Regeneración por versión y logo |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` | 892-901 | `StubPdfRenderer.LastDocument` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs` | 77-79 | Documento enviado al renderer |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderSendApiTests.cs` | 54-57 | Documento del pedido |

## Decisiones pendientes

- **DECISIÓN-PENDIENTE DP-1 — WhatsApp por Meta.** El envío de la cotización por la conexión de Meta (plan 3 del spec
  [2026-10-21 envío por canal](2026-10-21-envio-de-cotizaciones-por-canal-del-tenant-design.md)) no existe todavía: hoy
  `WhatsAppChannel` es sólo Zenvia (`IWhatsAppChannelDirectory.cs:9-14`). Los ejemplos con los que se aprovisiona la
  plantilla de Meta ponen el código de moneda **después** del monto, `"2.450.000 COP"`, y la vigencia larga
  (`QuotationTemplateProvisioning.cs:42-43`, afirmados en `QuotationTemplateDefinitionTests.cs:36`), distinto del
  `$ 2.450.000` de Zenvia. Falta decidir si el sender de Meta aplica este spec (separadores del tenant, símbolo de
  `Currencies`) y si el ejemplo de la plantilla se alinea.
