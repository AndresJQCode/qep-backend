# Formato regional del tenant en PDF, correos y WhatsApp

Fecha: 2026-10-23. Estado: aprobado en conversación por el owner (2026-10-23), sin empezar — pendiente. Sólo
`qep-backend`: el frontend ya aplica el formato del tenant (`RegionalFormatBoundary`, `src/lib/regional-format.ts`). Sin
plan escrito. Verificado contra `develop` en `baf9aba2`.

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

**Objetivo:** que esos tres canales usen el formato regional del tenant, con el comportamiento colombiano de hoy como
respaldo cuando falta.

## Decisiones

| ID | Decisión | Origen |
| --- | --- | --- |
| D-R1 | En el **PDF** y en el **WhatsApp** la fecha sigue **larga** («6 de septiembre de 2026»); ahí sólo se aplican los separadores numéricos del tenant (`NumberFormat`). Una fecha larga en un documento comercial no se puede leer mal; `MM/dd` contra `dd/MM` sí. Los nombres de los meses siguen en español: i18n queda fuera. | owner |
| D-R2 | Los **correos** usan el `DateFormat` del tenant más `" HH:mm"` (24 h), en la zona horaria del tenant, y la agrupación de `NumberFormat` para los conteos. | owner |
| D-R3 | Un **PDF guardado** se regenera en el siguiente pedido cuando el `NumberFormat` del tenant es distinto del que usó al generarse: se persiste el formato con el que se generó cada PDF (columna nueva, migración chica) y entra en `IsStaleFor`, igual que hoy el id del logo. Una fila vieja con `null` cuenta como generada con `"1.234,56"` (lo que la plantilla hace hoy), para que no se regeneren todas de golpe. | owner |
| D-R4 | El **símbolo y los decimales** de la moneda siguen saliendo de `Currencies.Get` (`Currencies.cs:46`; spec multi-moneda D8: el símbolo es fijo por moneda). El formato del tenant sólo decide los separadores. | owner (ratifica D8) |
| D-R5 | **Sin datos regionales** (el tenant no tiene fila, o un valor que no se reconoce) rige el comportamiento colombiano de hoy: `dd/MM/yyyy` y `1.234,56`. | owner |

## Qué se construye

### 1. Un solo helper, en `Modules.Tenancy.Application`

`RegionalNumberFormat` (static, nuevo) en `Modules.Tenancy.Application`, junto a `Currencies` y `TenantCalendar`:

- `Separators(string? numberFormat)` → `(string Thousands, string Decimal)`: `("." , ",")` para `"1.234,56"`,
  `(",", ".")` para `"1,234.56"`, y los colombianos (D-R5) para `null` o cualquier otro valor.
- `ToNumberFormatInfo(string? numberFormat)` → un `NumberFormatInfo` de sólo lectura sobre `InvariantCulture` con
  `NumberGroupSeparator`, `NumberDecimalSeparator` y `NumberGroupSizes = [3]` de esos separadores.

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

## Restricciones

- Sin rediseño: ningún texto, layout ni plantilla de Zenvia cambia más allá de separadores y fechas de correo.
- La plantilla de Zenvia aprobada no cambia: la variable `total` es la misma, sólo su contenido.
- Ningún default silencioso nuevo distinto de D-R5. `ITenantClock` sigue sin default a UTC (`ITenantClock.cs:8-10`).
- La regla de capas no se toca: ni `Modules.Notifications.Application` gana referencias, ni Quotations ve otro módulo que
  Tenancy.

## Fuera de alcance

- i18n: nombres de meses u otros textos en otro idioma.
- Las exportaciones a Excel y el texto del historial de cambios: usan `yyyy-MM-dd` legible por máquina a propósito
  (nombre del archivo, `ExportJobSupport.cs:37`; historial, `QuotationChangeSummary.cs:300`).
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
