# Formato regional del tenant en PDF, correos y WhatsApp — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el PDF de cotización y de pedido, los correos de exportación lista, el WhatsApp de Zenvia, el historial
de cambios (filas nuevas) y las fechas de texto del Excel de clientes y de cotizaciones usen el formato regional del
tenant, con el comportamiento colombiano de hoy como respaldo; y que la hoja del ERP (Excel de pedidos) quede fija.

**Architecture:** Un helper estático en `Modules.Tenancy.Application` (`RegionalNumberFormat`, `RegionalDateFormat`)
resuelve separadores y respaldo D-R5. Quotations lee `ITenantDirectory.GetRegionalSettingsAsync` en los dos providers
de PDF y en los dos handlers de envío; el PDF recibe los separadores en el payload y la plantilla Typst los usa; el caché
de la cotización recuerda con qué formato imprimió (columna nueva). Notifications arma un `EmailRegionalFormat`
(primitivos, en Application) desde los workers de Infrastructure, que son los que ven Tenancy. El historial recibe un
`HistoryFormat` obligatorio que cada handler que escribe lee una vez; los Excel de texto usan
`RegionalDateFormat.WithTime`. `OrdersExportProcessor` no se toca.

**Tech Stack:** .NET 10, EF Core + Npgsql, Typst (`quotation.typ`), xunit.v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-23-formato-regional-pdf-y-correos-design.md` (D-R1..D-R8, finales; no se
reabren. D-R6..D-R8 son la ampliación del owner del 2026-10-25, §5 y §6 del spec).

## Desvíos respecto del spec

Verificado contra `develop` en `68c1ca7f` (el spec, contra `baf9aba2`; 96 commits en medio).

1. **Hay un cuarto correo con fecha y conteo:** `SupplierExportEmailTemplate` (`Render`, `:18-24`; vencimiento `:30-31`;
   conteo `:32`) y su `SupplierExportDeliveryWorker` (`:38-45`), mergeados después del spec. Entra con el mismo
   tratamiento que los otros tres (D-R2 habla de «los correos»), con sus pruebas en `SupplierExportEmailTemplateTests`
   (`:22`, `:77-83`).
2. **`ConfigureQuotationPdf` se movió:** hoy es `QuotationsDbContext.cs:386-413` (el spec dice `390-406`).
3. **DP-1 sigue fuera de alcance y pendiente.** No existe un sender de WhatsApp por Meta para cotizaciones:
   `IWhatsAppSender` sólo lo implementa `ZenviaWhatsAppSender`, `WhatsAppChannel` es una conexión de Zenvia
   (`IWhatsAppChannelDirectory.cs:9-18`) y `docs/estado.md` registra el plan 3 del envío por canal como «sin escribir».
   Los ejemplos de la plantilla de Meta (`QuotationTemplateProvisioning.cs:38-44`, `"2.450.000 COP"`) no se tocan.
4. **Las pruebas de integración de Notifications registran el tenant con `dateFormat = "yyyy-MM-dd"`**
   (`NotificationsDeliveryHarness.cs:127`). Con este cambio esos correos de prueba pasan a imprimir la fecha ISO. Ninguna
   afirmación de integración mira el cuerpo hoy (revisado), así que nada se rompe; la Tarea 5 lo aprovecha.
5. **Cambio de comportamiento que el spec implica pero no dice en voz alta:** el `rowCount` del correo de cotizaciones
   hoy sale sin agrupar (`1234 pedidos`); con el respaldo colombiano sale `1.234 pedidos`. Es D-R2 aplicado; queda
   afirmado en la Tarea 5.

6. **Ampliación (D-R6..D-R8), verificada contra `68c1ca7f`:**
   - `PaymentProofsChanged` (`OrderChangeSummary.cs:57-76`) imprime conteos y **queda igual**: son conteos de un solo
     guardado, y con `"N0"` el texto sería idéntico (spec §5).
   - Proveedores y productos no tienen fechas de texto en celdas, sólo en el nombre del archivo: nada que cambiar.
   - Capas: `Modules.Customers.Application` ya referencia `Modules.Tenancy.Application` (`.csproj:10`) y
     `CustomersLayerTests.cs:66-74` lo admite; Quotations ya lo ve. Ningún módulo necesita una referencia nueva, así que
     `tests/ArchitectureTests` sigue sin tocarse. El builder de clientes (Infrastructure) recibe el patrón ya resuelto
     como `string`, así que tampoco depende de quién lo resuelva.
   - Los harnesses de Quotations (`QuotationsApiHarness.cs:215`) y Customers (`CustomersApiHarness.cs:72`) registran el
     tenant con `yyyy-MM-dd`: `CustomerExportApiTests.cs:105-106` y las fechas de historial de integración siguen
     verdes sin cambio, y por lo mismo **no** prueban el cambio. Las pruebas nuevas ponen otro formato a propósito.
   - `OrderExportApiTests` está entero en verde desde el 2026-10-25 (Quotations integración 496/496 sobre `3472f01d`):
     las dos del NIT de `6f8aa75` ya pasan. Una roja ahí es regresión, no falla previa.

Las demás referencias del spec coinciden con el código actual (mapper `:26-64`/`:75-119`, `QuotationPdf.cs:51-80`,
`QuotationPdfProvider.cs:37,41,61-76`, `OrderPdfProvider.cs:24-48`, `quotation.typ:58-119`,
`ZenviaWhatsAppSender.cs:30-36,86-88,149`, `IWhatsAppSender.cs:31-39,46-53`, `SendQuotation.cs:122-131`,
`SendOrder.cs:112-121`, plantillas y workers de Notifications, `DeliveryContext` `:234-239`).

### Decisiones del plan (huecos del spec, sin reabrir D-R*)

- `RegionalNumberFormat.Resolve(string?)` devuelve el formato **canónico** ya con el respaldo: es lo que el provider
  compara en `IsStaleFor` y lo que guarda como «el que de verdad imprimió» (spec §2, Caché).
- D-R5 dice «un valor que no se reconoce» también para `DateFormat`, y los valores permitidos son un `HashSet` privado de
  `Tenant` (`Tenant.cs:8-13`). Se sube a `TenantDateFormats` (público, en `Modules.Tenancy.Domain`, gemelo de
  `TenantNumberFormats`) y `RegionalDateFormat.Pattern(string?)` lo reutiliza.
- El parámetro nuevo del mapper es opcional (`string? numberFormat = null` → respaldo), como los campos con default del
  payload: los llamadores de prueba existentes no cambian.
- `EmailRegionalFormat` trae `Expiry(DateTimeOffset)` y `Count(int)` para que las cuatro plantillas no repitan la regla.
- Los cuatro workers leen el formato con un helper protegido de `OutboxDeliveryWorker`, sobre una fábrica interna
  `EmailRegionalFormats` que se prueba sin base.
- Historial: `HistoryFormat` es parámetro **obligatorio** de los resúmenes que imprimen número o fecha, para que el
  compilador marque cada llamador. `QuotationEditsOutcome` devuelve las dos fotos del encabezado en vez del texto: así
  los dos cálculos previos que comparten `ApplyAsync` no leen Tenancy para un resumen que descartan.

## Global Constraints

- Rama: quien ejecuta crea `feature/formato-regional` desde `develop`. Antes de **cada** commit,
  `git branch --show-current` en ese momento (la rama cambia durante la sesión); nunca commitear en `main` ni en
  `develop` directo.
- Conventional commits en español citando el spec (`(spec 2026-10-23 D-R3)`); **sin atribución de IA**: ninguna línea
  `Co-Authored-By`, aunque el harness la pida.
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea.
- Comandos en PowerShell. Pruebas de integración en esta máquina:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/<Modulo>/Modules.<Modulo>.IntegrationTests --filter "FullyQualifiedName~<Clase>"
  ```
  Por tarea, sólo las clases tocadas; la corrida combinada de los proyectos afectados va una sola vez, en la Tarea 9.
  Nada de `Api.exe` (ni `dotnet Api.dll`) corriendo mientras tanto. Las corridas, en primer plano.
- Capas intactas: `Modules.Notifications.Application` no gana referencias (sólo `System.Globalization`); Quotations sólo
  ve `Modules.Tenancy*` (`QuotationsLayerTests.cs:68-77`). `tests/ArchitectureTests` no se toca.
- D-R1: en PDF y WhatsApp la fecha sigue larga y en español (`fecha()`, `meses`, `"d 'de' MMMM 'de' yyyy"` con `es-CO`).
- D-R2: correos con `DateFormat + " HH:mm"` en `InvariantCulture`, en la zona del tenant; conteos con `"N0"`.
- D-R4: símbolo y decimales siguen de `Currencies.Get`.
- D-R5: sin fila o valor desconocido → `dd/MM/yyyy` y `1.234,56` (`TenantNumberFormats.CommaDecimal`).
- Sin rediseño: ningún texto, layout ni plantilla de Zenvia cambia más allá de separadores y fechas de correo.
- Textos y comentarios para personas en español colombiano, tuteando; identificadores y nombres de prueba en inglés.
- `docs/estado.md` no se toca en la rama (se actualiza en `develop` al mergear).

## Review Focus

1. **El despliegue no regenera todos los PDF de golpe:** una fila de `quotation_pdfs` anterior (`number_format` nulo) de
   un tenant en `1.234,56` sigue vigente; con `1,234.56` sí se regenera (Tarea 3,
   `QuotationPdfNumberFormatMigrationTests.ALegacyRowKeepsItsCacheForACommaDecimalTenant` y
   `QuotationPdfTests.ALegacyPdfWithoutFormatCountsAsCommaDecimal`).
2. **El mensaje y el documento dicen el mismo número:** al enviar una cotización de un tenant en `1,234.56`, el PDF que
   sale y el total del WhatsApp usan los mismos separadores (Tarea 4,
   `SendQuotationHandlerTests.TheMessageAndTheDocumentUseTheSameNumberFormat`).
3. **Tenant sin fila regional o con un valor sembrado a mano** (D-R5) en los tres canales: separadores colombianos en el
   PDF, total colombiano en WhatsApp, `dd/MM/yyyy` en el correo (Tareas 3, 4 y 5: `WithoutRegionalSettings…`,
   `AnUnknownNumberFormatFallsBackToColombian`, `AnUnknownDateFormatFallsBackToDayFirst`).
4. **`MM/dd/yyyy` con otra cultura de proceso:** la `/` sigue siendo `/` aunque el host corra en `de-DE` (Tarea 5,
   `EmailRegionalFormatTests.TheSlashStaysASlashUnderAnotherProcessCulture`).
5. **Montos negativos y cantidades fraccionarias en `1,234.56`:** la retención (`"-" + moneda`) y una cantidad `12.5`
   compilan y conservan el signo antes del primer grupo (Tarea 2, `TheTemplateCompilesWithADotDecimalDocument` sobre
   `Complete()`, que trae retención y descuentos, y `TheAmountHelpersUseTheTenantSeparators`).
6. **La hoja del ERP no se mueve** aunque el tenant pase a `MM/dd/yyyy` y `1,234.56`: `Fecha Pedido` sigue
   `yyyy-MM-dd` y los valores siguen siendo celdas numéricas (Tarea 8,
   `OrderExportApiTests.TheErpSheetIgnoresTheTenantRegionalFormat`, con su chequeo de mutación).
7. **Cantidad fraccionaria con miles en el historial:** `1234.5` sale `1.234,5` / `1,234.5`, `0.5` sale `0,5` (no `,5`)
   y `3.00` sale `3` (Tarea 7, `HistoryFormatTests.NumbersKeepUpToTwoDecimalsWithGrouping`).

---

### Task 1: Helper regional en Tenancy (D-R5)

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantDateFormats.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs:8-13` (el `HashSet` usa las constantes)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/RegionalNumberFormat.cs`, `RegionalDateFormat.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/RegionalNumberFormatTests.cs`, `RegionalDateFormatTests.cs` (nuevos)

**Interfaces:**
- Produces: `TenantDateFormats { const string IsoDate = "yyyy-MM-dd"; const string DayMonthYear = "dd/MM/yyyy"; const string MonthDayYear = "MM/dd/yyyy"; static bool IsKnown(string? value) }`.
- Produces: `RegionalNumberFormat.Resolve(string? numberFormat) : string` (`CommaDecimal` o `DotDecimal`; otro valor o `null` → `CommaDecimal`, comparando tras `Trim()`); `Separators(string? numberFormat) : (string Thousands, string Decimal)`; `ToNumberFormatInfo(string? numberFormat) : NumberFormatInfo` (clon de sólo lectura de `InvariantCulture.NumberFormat` con `NumberGroupSeparator`, `NumberDecimalSeparator`, `NumberGroupSizes = [3]`).
- Produces: `RegionalDateFormat.Pattern(string? dateFormat) : string` (el valor si `TenantDateFormats.IsKnown`, si no `DayMonthYear`) y `RegionalDateFormat.WithTime(string? dateFormat) : string` (`Pattern(...) + " HH:mm"`, para los Excel de la Tarea 8).

- [ ] **Step 1: RED.** `RegionalNumberFormatTests`:
  - `[Theory] SeparatorsFollowTheTenantFormat` — `("1.234,56", ".", ",")`, `("1,234.56", ",", ".")`, `(null, ".", ",")`, `("x", ".", ",")`, `(" 1,234.56 ", ",", ".")`.
  - `ResolveFallsBackToCommaDecimal` — `Resolve(null)` y `Resolve("x")` son `TenantNumberFormats.CommaDecimal`; `Resolve("1,234.56")` es `DotDecimal`.
  - `ToNumberFormatInfoFormatsWithTheTenantSeparators` — `2450.5m.ToString("N2", ToNumberFormatInfo("1,234.56")) == "2,450.50"`, `2450000m.ToString("N0", ToNumberFormatInfo(null)) == "2.450.000"`, y `IsReadOnly`.
  `RegionalDateFormatTests.PatternKeepsKnownFormatsAndFallsBackToDayFirst` — los tres conocidos salen iguales; `null`, `""` y `"dd.MM.yy"` salen `"dd/MM/yyyy"`; `WithTime("MM/dd/yyyy") == "MM/dd/yyyy HH:mm"` y `WithTime(null) == "dd/MM/yyyy HH:mm"`.

  ```powershell
  dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~RegionalNumberFormatTests|FullyQualifiedName~RegionalDateFormatTests"
  ```
  Esperado: no compila (`CS0103`: `RegionalNumberFormat`/`RegionalDateFormat` no existen).
- [ ] **Step 2: GREEN.** Crear los tres tipos; `Tenant.AllowedDateFormats` se arma con las constantes de
  `TenantDateFormats` (mismo `HashSet`, mismo mensaje de error). `RegionalNumberFormat` reutiliza las constantes de
  `TenantNumberFormats`, no repite literales.
- [ ] **Step 3:** Correr el filtro anterior más `FullyQualifiedName~TenantTests`. Todo verde.
- [ ] **Step 4: Commit** `feat(tenancy): formato regional resuelto con el respaldo colombiano (spec 2026-10-23 D-R5)`.

### Task 2: El PDF lleva los separadores del tenant (payload, mapper y plantilla)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs:84-87` (dos campos al final)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs:26-64`, `:75-119`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:72-119`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs`, `QuotationPdfDocumentMapperTests.cs`, `OrderPdfDocumentMapperTests.cs`

**Interfaces:**
- Consumes: `RegionalNumberFormat.Separators` (Tarea 1).
- Produces: `QuotationPdfDocument(..., QuotationPdfKind Kind = Quotation, string ThousandsSeparator = ".", string DecimalSeparator = ",")` → `data.thousandsSeparator`, `data.decimalSeparator`.
- Produces: `QuotationPdfDocumentMapper.From(QuotationResponse quotation, TenantCalendar calendar, QuotationPdfLogo? logo, string? numberFormat = null)` y `FromOrder(OrderDetailResponse order, TenantCalendar calendar, QuotationPdfLogo? logo, string? numberFormat = null)`.

- [ ] **Step 1: RED.**
  - `QuotationPdfDocumentMapperTests`: `[Theory] MapsTheSeparatorsOfTheTenantNumberFormat` con `("1.234,56", ".", ",")`, `("1,234.56", ",", ".")`, `(null, ".", ",")`, `("x", ".", ",")`, llamando `From(<respuesta del archivo>, <calendario del archivo>, logo: null, numberFormat)` y afirmando `(document.ThousandsSeparator, document.DecimalSeparator)`. Mismo theory en `OrderPdfDocumentMapperTests` con `FromOrder`. `TheIssueDateIsTheTenantsLocalDate` y `ValidUntil` no cambian (D-R1).
  - `QuotationTemplateTests`:
    - `ThePayloadCarriesTheSeparators` — `CapturedRequestAsync(Complete() with { ThousandsSeparator = ",", DecimalSeparator = "." })` → `data.thousandsSeparator == ","`, `data.decimalSeparator == "."`; con `Minimal()` sin tocar → `"."` y `","`.
    - `TheAmountHelpersUseTheTenantSeparators` (fuente) — contiene `partes.join(data.thousandsSeparator)` y `data.decimalSeparator`; **no** contiene `partes.join(".")`, `"," +`, `",0" +` ni `porcentaje(`; sigue conteniendo `fecha(data.issuedOn)` y `#let meses`.
    - `TheTemplateCompilesWithADotDecimalDocument` → `AssertCompilesAsync(Complete() with { ThousandsSeparator = ",", DecimalSeparator = "." })`, y `TheTemplateCompilesWithADotDecimalMinimalDocument` igual sobre `Minimal()`.

  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationTemplateTests|FullyQualifiedName~QuotationPdfDocumentMapperTests|FullyQualifiedName~OrderPdfDocumentMapperTests"
  ```
  Esperado: no compila (`CS0117`: `QuotationPdfDocument` no tiene `ThousandsSeparator`).
- [ ] **Step 2: GREEN (C#).** Los dos campos con default al final del record, con un `<summary>` que diga que el
  símbolo y los decimales siguen de `Currencies` (D-R4) y esto sólo decide los separadores. El mapper llena ambos con
  `RegionalNumberFormat.Separators(numberFormat)`; `IssuedOn` y `ValidUntil` no cambian.
- [ ] **Step 3: GREEN (plantilla).** En `quotation.typ`: `miles()` une con `data.thousandsSeparator` (`:82`);
  `importe()` (`:94`) y `cantidad()` (`:109`, `:111`, `:113`) usan `data.decimalSeparator` (el `",0"` de `:111` pasa a
  `data.decimalSeparator + "0"`); se borra `porcentaje()` (`:117-119`), que no tiene llamadas. `fecha()` y `meses` no se
  tocan. El comentario de `miles()` dice de dónde salen los separadores.
- [ ] **Step 4:** Correr el filtro anterior. Verde, incluida `EveryFieldTheTemplateReadsExistsInThePayload` sin cambios
  y las de compilación (`typst` está instalado en esta máquina: no deben salir como `Skipped`).
- [ ] **Step 5: Commit** `feat(quotations): el PDF imprime los separadores del tenant (spec 2026-10-23 D-R1, D-R4)`.

### Task 3: El caché recuerda el formato y los providers lo leen del tenant (D-R3)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdf.cs:20-80`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:401` (mapeo de `number_format`)
- Create: migración `AddQuotationPdfNumberFormat` (+ snapshot)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs:20-79`, `OrderPdfProvider.cs:24-47`
- Modify (dobles): `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubTenantDirectory` nuevo)
- Test: `QuotationPdfTests.cs`, `ExportQuotationPdfHandlerTests.cs` (`NewHandler` `:170-196`), `SendOrderHandlerTests.cs` (`:361-379`), `SendQuotationHandlerTests.cs` (`:473-494`, sólo el constructor del provider)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfNumberFormatMigrationTests.cs` (nuevo)

**Interfaces:**
- Consumes: `RegionalNumberFormat.Resolve` (Tarea 1); `QuotationPdfDocumentMapper.From/FromOrder(..., numberFormat)` (Tarea 2).
- Produces: `QuotationPdf.NumberFormat : string?`; `QuotationPdf.Generate(QuotationId quotationId, Guid tenantId, string storageKey, long quotationVersion, Guid? logoFileId, string? numberFormat, DateTimeOffset generatedAt)`; `Regenerate(string storageKey, long quotationVersion, Guid? logoFileId, string numberFormat, DateTimeOffset generatedAt)`; `IsStaleFor(long quotationVersion, Guid? logoFileId, string numberFormat)`.
- Produces: `QuotationPdfProvider(IQuotationRepository, IQuotationResponseComposer, IQuotationPdfRenderer, IQuotationPdfStorage, ITenantClock, IQuotationLogoLookup, ITenantDirectory tenantDirectory)` y `OrderPdfProvider(IQuotationPdfRenderer, IQuotationPdfStorage, ITenantClock, IQuotationLogoLookup, ITenantDirectory tenantDirectory)`.
- Produces (pruebas): `StubTenantDirectory : ITenantDirectory` con `TenantRegionalSettings? Regional { get; set; }` (default `("America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal)`), `static StubTenantDirectory WithNumberFormat(string numberFormat)`; los demás miembros devuelven `null`.

- [ ] **Step 1: RED (dominio).** `QuotationPdfTests`: las llamadas existentes pasan `TenantNumberFormats.CommaDecimal` en
  la posición nueva, y se agregan:
  - `APdfPrintedWithAnotherNumberFormatIsStale` — generado con `CommaDecimal`, `IsStaleFor(7, null, DotDecimal)` es `true`.
  - `APdfPrintedWithTheSameNumberFormatIsNotStale`.
  - `ALegacyPdfWithoutFormatCountsAsCommaDecimal` — `Generate(..., numberFormat: null, ...)`: `IsStaleFor(7, null, TenantNumberFormats.CommaDecimal)` es `false` y con `DotDecimal` es `true`. Usa la constante de Tenancy a propósito: es lo que ata el literal del dominio.
  - `RegeneratingRecordsTheNewNumberFormat` — tras `Regenerate(..., DotDecimal, ...)`, `NumberFormat == DotDecimal` y no está vencido para `DotDecimal`.

  Esperado: no compila (`CS1501`: `IsStaleFor` no tiene sobrecarga de 3 argumentos).
- [ ] **Step 2: GREEN (dominio).** `NumberFormat` (`string?`, privado set) con su `<summary>`; `IsStaleFor` suma
  `|| (NumberFormat ?? LegacyNumberFormat) != numberFormat`, con `private const string LegacyNumberFormat = "1.234,56"` y
  un comentario que la ata a `TenantNumberFormats.CommaDecimal` (el dominio de Quotations no referencia Tenancy) y a la
  prueba del paso 1.
- [ ] **Step 3: RED (migración).** `QuotationPdfNumberFormatMigrationTests.ALegacyRowKeepsItsCacheForACommaDecimalTenant`,
  con el patrón de `FreightMigrationTests` (`NewContext`, `IMigrator`, `MigrationId`): migrar Quotations hasta la
  migración **inmediatamente anterior** a `_AddQuotationPdfNumberFormat` (la anterior en `GetMigrations()`, no un nombre
  fijo); sembrar con `session_replication_role = replica` una fila de `quotations.quotation_pdfs` (`quotation_id`,
  `tenant_id`, `storage_key`, `quotation_version = 4`, `logo_file_id NULL`, `generated_at`); migrar a la última
  (`MigrateAsync(null)`). Afirmar: `number_format IS NULL`, `storage_key` y `quotation_version` intactos, y leyendo la
  fila con `context.Set<QuotationPdf>()`: `IsStaleFor(4, null, TenantNumberFormats.CommaDecimal)` es `false` y con
  `DotDecimal` es `true`.

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationPdfNumberFormatMigrationTests"
  ```
  Esperado: falla con `InvalidOperationException: Sequence contains no matching element` (la migración no existe).
- [ ] **Step 4: GREEN (persistencia).** En `ConfigureQuotationPdf`, tras `logo_file_id` (`:401`):
  `pdf.Property(value => value.NumberFormat).HasColumnName("number_format").HasMaxLength(16);`. Sin `Api.exe` corriendo:

  ```powershell
  dotnet ef migrations add AddQuotationPdfNumberFormat --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
  ```
  Revisar que el `Up` sea sólo `AddColumn<string>("number_format", "quotations", "quotation_pdfs", "character varying(16)", maxLength: 16, nullable: true)`, sin backfill, y su `Down` el `DropColumn`.
- [ ] **Step 5: RED (providers).** `ExportQuotationPdfHandlerTests` — `NewHandler(StubTenantDirectory? tenantDirectory = null)` (no cambia la tupla):
  - `ChangingTheTenantNumberFormatRegenerates` — tras el primer export, `directory.Regional = directory.Regional! with { NumberFormat = TenantNumberFormats.DotDecimal }`; segundo export → `renderer.Calls == 2`, `renderer.Last!.ThousandsSeparator == ","`, `repository.Pdf!.NumberFormat == DotDecimal`.
  - `ExportingTwiceWithTheSameNumberFormatDoesNotRegenerate` — con `WithNumberFormat(DotDecimal)`, dos exports → `renderer.Calls == 1`.
  - `WithoutRegionalSettingsTheDocumentUsesColombianSeparators` — `Regional = null` → `(".", ",")` en `renderer.Last` y `repository.Pdf!.NumberFormat == CommaDecimal`.
  `SendOrderHandlerTests` — `NewHarness` acepta `StubTenantDirectory? tenantDirectory = null` y lo pasa al `OrderPdfProvider`; `TheOrderDocumentUsesTheTenantSeparators` → con `DotDecimal`, `renderer.Last` trae `(",", ".")`. En `SendQuotationHandlerTests.NewHandler` sólo se pasa `new StubTenantDirectory()` al provider (la Tarea 4 lo usa también en el handler).

  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPdfTests|FullyQualifiedName~ExportQuotationPdfHandlerTests|FullyQualifiedName~SendOrderHandlerTests|FullyQualifiedName~SendQuotationHandlerTests"
  ```
  Esperado: no compila (`CS1729`: los providers no tienen constructor de 7 y 5 argumentos).
- [ ] **Step 6: GREEN (providers).** `QuotationPdfProvider`: leer
  `tenantDirectory.GetRegionalSettingsAsync(new TenantId(quotation.TenantId), ...)` junto al logo (`:37`, comentario
  «una lectura de Tenancy más por export»), `var numberFormat = RegionalNumberFormat.Resolve(regional?.NumberFormat)`;
  pasarlo a `IsStaleFor` (`:41`), al mapper (`:62`), a `Generate` (`:69-70`) y a `Regenerate` (`:75`). `OrderPdfProvider`:
  la misma lectura y `FromOrder(detail, calendar, pdfLogo, regional?.NumberFormat)` (`:45`).
- [ ] **Step 7:** Correr los dos filtros (unitario e integración de este paso). Verde.
- [ ] **Step 8: Commit** `feat(quotations): el PDF guardado se regenera si cambia el formato numérico del tenant (spec 2026-10-23 D-R3)`.

### Task 4: WhatsApp de Zenvia con los separadores del tenant

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSender.cs:21-53`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs:28-44`, `:122-131`; `SendOrder.cs:32-47`, `:112-121`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs:28-36`, `:84-88`, `:149`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ZenviaWhatsAppSenderTests.cs`, `SendQuotationHandlerTests.cs`, `SendOrderHandlerTests.cs`

**Interfaces:**
- Consumes: `RegionalNumberFormat.ToNumberFormatInfo` (Tarea 1); `StubTenantDirectory` (Tarea 3).
- Produces: `WhatsAppQuotationMessage(..., string DocumentUrl, string? NumberFormat = null)` y `WhatsAppOrderMessage(..., string DocumentUrl, string? NumberFormat = null)` — crudo, como el resto del contrato.
- Produces: `SendQuotationHandler(..., IWhatsAppChannelDirectory whatsAppChannels, ITenantDirectory tenantDirectory, IMembershipDirectory membershipDirectory, ...)` y `SendOrderHandler(..., IWhatsAppChannelDirectory whatsAppChannels, ITenantDirectory tenantDirectory, IMembershipDirectory membershipDirectory, ...)`.

- [ ] **Step 1: RED.** `ZenviaWhatsAppSenderTests` (`:56`, `:67`, `:80`, `:217` siguen igual, sin formato):
  - `SendFormatsTheTotalWithTheTenantSeparators` — `Message with { Total = 2450.5m, Currency = "EUR", NumberFormat = TenantNumberFormats.DotDecimal }` → `"€ 2,450.50"`; `Message with { NumberFormat = DotDecimal }` → `"$ 2,450,000"`.
  - `SendOrderFormatsTheTotalWithTheTenantSeparators` — `OrderMessage with { NumberFormat = DotDecimal }` → `"$ 2,450,000"`.
  - `TheValidityStaysLongWithAnyNumberFormat` — con `DotDecimal`, `valid_until == "30 de septiembre de 2026"` (D-R1).
  - `AnUnknownNumberFormatFallsBackToColombian` — `NumberFormat = "x"`, EUR 2450.5 → `"€ 2.450,50"`.
  `SendQuotationHandlerTests` (el `NewHandler` pasa el mismo `StubTenantDirectory` al provider y al handler):
  - `TheMessageAndTheDocumentUseTheSameNumberFormat` — con `DotDecimal`: `sender.Sent!.NumberFormat == DotDecimal` y `renderer.Last!.ThousandsSeparator == ","`.
  - `WithoutRegionalSettingsTheMessageHasNoNumberFormat` — `Regional = null` → `sender.Sent!.NumberFormat` es `null`.
  `SendOrderHandlerTests.TheOrderMessageCarriesTheTenantNumberFormat` → `sender.SentOrder!.NumberFormat == DotDecimal`.

  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ZenviaWhatsAppSenderTests|FullyQualifiedName~SendQuotationHandlerTests|FullyQualifiedName~SendOrderHandlerTests"
  ```
  Esperado: no compila (`CS0117`: `WhatsAppQuotationMessage` no tiene `NumberFormat`).
- [ ] **Step 2: GREEN.** El campo nuevo al final de los dos records; el párrafo del `<summary>` (`:27-29`) agrega que
  el formato numérico del tenant también viaja crudo. Los handlers leen
  `GetRegionalSettingsAsync(new TenantId(command.TenantId), ...)` justo antes de `stage = QuotationSendStage.WhatsApp` y
  pasan `NumberFormat: regional?.NumberFormat`. En el sender: `FormatTotal(decimal total, string currency, string? numberFormat)`
  con `RegionalNumberFormat.ToNumberFormatInfo(numberFormat)`; `Colombia` (`:30`) queda **sólo** para `valid_until`, y su
  comentario (`:28-29`) y el de `:84-85` lo dicen.
- [ ] **Step 3:** Correr el filtro anterior. Verde.
- [ ] **Step 4: Commit** `feat(quotations): el total del WhatsApp usa los separadores del tenant (spec 2026-10-23 D-R1, D-R4)`.

### Task 5: Correos de exportación con fecha y conteos del tenant (D-R2)

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/EmailRegionalFormat.cs`
- Modify: `CustomerExportEmailTemplate.cs:18-32`, `ProductExportEmailTemplate.cs:18-31`, `QuotationsExportReadyEmailTemplate.cs:15-31`, `SupplierExportEmailTemplate.cs:18-32` (todas en `Modules.Notifications.Application`)
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/EmailRegionalFormats.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs` (helper junto a `ResolveRecipientAsync`, `:175-189`)
- Modify: `CustomerExportDeliveryWorker.cs:35-46`, `ProductExportDeliveryWorker.cs:35-44`, `QuotationsExportReadyDeliveryWorker.cs:35-44`, `SupplierExportDeliveryWorker.cs:35-46`
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/EmailRegionalFormatTests.cs`, `EmailRegionalFormatsTests.cs` (nuevos); `CustomerExportEmailTemplateTests.cs`, `ProductExportEmailTemplateTests.cs`, `QuotationsExportEmailTemplateTests.cs`, `SupplierExportEmailTemplateTests.cs`
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs`, `NotificationsDeliveryHarness.cs`

**Interfaces:**
- Consumes: `RegionalDateFormat.Pattern`, `RegionalNumberFormat.ToNumberFormatInfo` (Tarea 1), sólo desde Infrastructure.
- Produces: `public sealed record EmailRegionalFormat(TimeZoneInfo TimeZone, string DateFormat, NumberFormatInfo Numbers)` con `static EmailRegionalFormat Colombian(TimeZoneInfo timeZone)` (`"dd/MM/yyyy"`, `.`/`,`, grupos de 3, sin ver Tenancy), `string Expiry(DateTimeOffset instant)` (`ConvertTime` a `TimeZone`, `DateFormat + " HH:mm"`, `InvariantCulture`) y `string Count(int value)` (`"N0"` con `Numbers`).
- Produces: `Render(..., DateTimeOffset expiresAt, EmailRegionalFormat format)` en las cuatro plantillas (reemplaza `TimeZoneInfo timeZone`).
- Produces: `internal static class EmailRegionalFormats { static EmailRegionalFormat For(TimeZoneInfo timeZone, TenantRegionalSettings? settings) }` (sin fila → `Colombian(timeZone)`) y en `OutboxDeliveryWorker`: `protected static async Task<EmailRegionalFormat> ResolveEmailFormatAsync(DeliveryContext context, Guid tenantId, CancellationToken stoppingToken)` (calendario por `TenantClock`, que sigue fallando con un tenant desconocido; formato por `TenantDirectory.GetRegionalSettingsAsync`).

- [ ] **Step 1: RED (unitarias).**
  - `EmailRegionalFormatTests`: `ColombianIsDayFirstWithDotGrouping` — `Colombian(Bogota).Expiry(2027-01-01T04:00Z) == "31/12/2026 23:00"`, `Count(1234) == "1.234"`; `ColombianMatchesTheTenancyFallback` — `DateFormat == RegionalDateFormat.Pattern(null)` y los dos separadores iguales a `RegionalNumberFormat.ToNumberFormatInfo(null)` (amarra la copia: Application no ve Tenancy); `TheSlashStaysASlashUnderAnotherProcessCulture` — con `CultureInfo.CurrentCulture = de-DE` (restaurada en `finally`), `MM/dd/yyyy` → `"12/31/2026 23:00"`.
  - `EmailRegionalFormatsTests`: `UsesTheTenantDateAndNumberFormat` (`MM/dd/yyyy`, `1,234.56`), `WithoutARegionalRowIsColombian` (`null`), `AnUnknownDateFormatFallsBackToDayFirst` (`"dd.MM.yy"`), `AnUnknownNumberFormatFallsBackToColombian` (`"x"`), `KeepsTheCalendarTimeZone`.
  - En las cuatro clases de plantillas: un campo `Colombian = EmailRegionalFormat.Colombian(Bogota)` reemplaza a `Bogota` en las llamadas existentes, que siguen afirmando `31/12/2026 23:00`, `13/09/2026 10:30` y `42 pedidos`. Nuevas en cada una: `RenderUsesTheTenantDateAndNumberFormat` — con `new EmailRegionalFormat(Bogota, "MM/dd/yyyy", RegionalNumberFormat.ToNumberFormatInfo("1,234.56"))` y conteo `1234`, los dos cuerpos traen `12/31/2026 23:00` (en cotizaciones, `09/13/2026 10:30`) y `1,234 clientes` / `productos` / `pedidos` / `proveedores`; `RenderUsesTheIsoDateFormat` → `2026-12-31 23:00`. En `QuotationsExportEmailTemplateTests`, además, `ReadyGroupsTheRowCountForAColombianTenant` → `1.234 pedidos` (desvío 5). El singular («1 cliente») no cambia.

  ```powershell
  dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~EmailRegionalFormat|FullyQualifiedName~ExportEmailTemplateTests"
  ```
  Esperado: no compila (`CS0246`: `EmailRegionalFormat` no existe).
- [ ] **Step 2: GREEN.** Record y fábrica; las cuatro plantillas usan `format.Expiry(expiresAt)` y `format.Count(n)`
  (el comentario del vencimiento cita además D-R2 y por qué `InvariantCulture`); los cuatro workers cambian
  `TenantClock.GetAsync` + `calendar.TimeZone` por `var format = await ResolveEmailFormatAsync(context, export.TenantId, stoppingToken)`
  en el mismo lugar (fuera del envío: una falla deja el reclamo vivo, como hoy). `InvitationEmailTemplate` y
  `QuotationsExportFailedEmailTemplate` no cambian.
- [ ] **Step 3:** Correr el filtro anterior más `FullyQualifiedName~NotificationsLayerTests` en
  `tests/ArchitectureTests/ArchitectureTests`. Verde.
- [ ] **Step 4: RED (integración).** `NotificationsDeliveryHarness.SetTenantNumberFormatAsync(string connectionString, Guid tenantId, string numberFormat)`
  (`UPDATE tenancy.tenants SET number_format = @format WHERE id = @id`, Npgsql como `InsertOutboxAsync`).
  `PayloadFor` gana `int count = 3, DateTimeOffset? expiresAt = null` (los existentes no cambian). Nueva
  `[Theory] EachExportEmailUsesTheTenantRegionalFormat` sobre `customers.export-ready.v1`, `catalog.product-export-ready.v1`,
  `quotations.export-ready.v1` y `suppliers.export-ready.v1`: tenant registrado por el harness (ya en `yyyy-MM-dd`,
  `America/Bogota`), `SetTenantNumberFormatAsync(..., "1,234.56")`, evento con `count: 1234` y
  `expiresAt: 2030-01-15T15:30:00Z`; esperar `Sent` y afirmar que el `TextBody` del mensaje para ese correo contiene
  `2030-01-15 10:30` y `1,234 `.

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~DeliveryWorkersCharacterizationTests"
  ```
  Escríbela y córrela **antes** del Step 2 (junto con el RED del Step 1): esperado `Assert.Contains() Failure`, con
  `15/01/2030 10:30` y `1234 ` en el cuerpo. Después del Step 2, verde la clase entera.
- [ ] **Step 5: Commit** `feat(notifications): los correos de exportación usan la fecha y los números del tenant (spec 2026-10-23 D-R2)`.

### Task 6: Quotations de punta a punta con el tenant en `1,234.56`

**Files:**
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (helper nuevo junto a los de siembra)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestDoubles.cs:23-50`
- Test: `QuotationPdfExportApiTests.cs`, `OrderSendApiTests.cs`

**Interfaces:**
- Consumes: todo lo anterior, por DI real (`ITenantDirectory` ya está registrado por Tenancy).
- Produces (pruebas): `QuotationsApiHarness.SetTenantRegionalFormatAsync(QepApiFactory factory, Guid tenantId, string? dateFormat = null, string? numberFormat = null)` (SQL sobre `tenancy.tenants` —`date_format`, `number_format`; sólo las que vienen— con el `QuotationsDbContext` del scope, como `DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync`; la Tarea 8 lo reutiliza); `RecordingWhatsAppSender.LastNumberFormat : string?` (lo guardan los dos `Send…Async`; `Sent` no cambia de forma).

- [ ] **Step 1: RED.**
  - `QuotationPdfExportApiTests.TheDocumentUsesTheTenantSeparatorsAndRegeneratesWhenTheyChange` — exportar: `PdfRenderer.LastDocument` trae `(".", ",")`; `SetTenantRegionalFormatAsync(..., numberFormat: "1,234.56")`; exportar: `GeneratedAt` distinto del primero y `(",", ".")`; exportar otra vez: `GeneratedAt` igual al segundo (no regenera).
  - `OrderSendApiTests.TheOrderDocumentAndMessageUseTheTenantSeparators` — formato en `1,234.56` antes de enviar: `LastDocument` trae `(",", ".")` y `factory.WhatsAppSender.LastNumberFormat == "1,234.56"`.

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationPdfExportApiTests|FullyQualifiedName~OrderSendApiTests"
  ```
  Esperado: no compila (`CS0103`/`CS1061`: faltan `SetTenantRegionalFormatAsync` y `LastNumberFormat`). Con el código de
  las tareas 2-4 ya hecho, el comportamiento pasa en cuanto existen los helpers: es una prueba de punta a punta, y el
  reporte lo dice en vez de fabricar un RED de comportamiento.
- [ ] **Step 2: GREEN.** Los dos helpers. Correr el filtro: verde, incluidas las existentes de las dos clases.
- [ ] **Step 3: Commit** `test(quotations): PDF y WhatsApp con el formato del tenant de punta a punta (spec 2026-10-23 D-R3)`.

### Task 7: Historial de cambios con el formato del tenant (D-R6)

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/HistoryFormat.cs`
- Modify: `QuotationChangeSummary.cs:36-41` (`ItemAdded`, `ItemQuantityChanged`), `:89-91` (`GlobalScaleFloorChanged`), `:117` (`HeaderChanged`, con `:128` y `:157`), `:234-263` (`AppendFreight`, `:255`), `:299-307` (se van los privados `Date` y `Number`)
- Modify: `OrderChangeSummary.cs:17-42` (delegados y `ShippingChanged`); `PaymentProofsChanged` no cambia (desvío 6)
- Modify: `QuotationEditsApplication.cs:9-12` (`QuotationEditsOutcome`), `:101-102`; `QuotationItemEditHistory.cs:15-48`
- Modify (handlers, `ITenantDirectory tenantDirectory` antes de `IExecutionContext executionContext`): `AddQuotationItem.cs:28`, `:85`; `UpdateQuotationItem.cs:30`, `:93`; `BatchUpdateQuotationItems.cs:54`, `:157`; `SaveQuotation.cs:54`, `:97-118`; `CreateQuotation.cs:54`, `:116-118`; `AddOrderItems.cs:42`, `:81`; `SaveOrderEdits.cs:46`, `:177`, `:210`, `:294-330`; `RecognizeOrderCarrier.cs:44`, `:111` (todos en `src/Modules/Quotations/Modules.Quotations.Application/`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/HistoryFormatTests.cs` (nuevo); `QuotationChangeSummaryTests.cs`, `OrderChangeSummaryTests.cs`, `QuotationRetailTests.cs:217`; dobles en `CreateQuotationHandlerTests.cs:204-235`, `PreviewNewQuotationHandlerTests.cs:168`, `RecognizeOrderCarrierHandlerTests.cs:166-196`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderShippingApiTests.cs:242`

**Interfaces:**
- Consumes: `RegionalDateFormat.Pattern`, `RegionalNumberFormat.ToNumberFormatInfo` (Tarea 1); `StubTenantDirectory` (Tarea 3).
- Produces: `public sealed record HistoryFormat(string DateFormat, NumberFormatInfo Numbers)` con `static HistoryFormat For(TenantRegionalSettings? settings)`, `static HistoryFormat Colombian { get; }` (`For(null)`), `static Task<HistoryFormat> ReadAsync(ITenantDirectory directory, Guid tenantId, CancellationToken cancellationToken)`, `string Date(DateOnly value)` (`DateFormat`, `InvariantCulture`) y `string Number(decimal value)` (`"#,##0.##"` con `Numbers`).
- Produces: `ItemAdded(string productName, decimal quantity, HistoryFormat format)`, `ItemQuantityChanged(string productName, decimal from, decimal to, HistoryFormat format)`, `GlobalScaleFloorChanged(int? floor, HistoryFormat format)`, `HeaderChanged(QuotationHeaderSnapshot before, QuotationHeaderSnapshot after, HistoryFormat format)`, `internal AppendFreight(List<string> changes, ShippingDetails before, ShippingDetails after, HistoryFormat format)`; en `OrderChangeSummary` los mismos más `ShippingChanged(ShippingDetails before, ShippingDetails after, HistoryFormat format)`.
- Produces: `QuotationEditsOutcome(QuotationHeaderSnapshot HeaderBefore, QuotationHeaderSnapshot HeaderAfter, IReadOnlyList<QuotationItemEdit> ItemEdits)`; `QuotationItemEditHistory.RecordAsync(..., IReadOnlyList<QuotationItemEdit> edits, HistoryFormat format, MemberId updatedBy, ...)`.

- [ ] **Step 1: RED.**
  - `HistoryFormatTests`: `NumbersKeepUpToTwoDecimalsWithGrouping` — con `For(null)`: `1500m` → `"1.500"`, `1.5m` → `"1,5"`, `1234.5m` → `"1.234,5"`, `0.5m` → `"0,5"`, `3.00m` → `"3"`, `2.456m` → `"2,46"`; con `DotDecimal`: `"1,500"`, `"1.5"`, `"1,234.5"`. `DatesFollowTheTenantFormat` — `2026-12-31` sale `"31/12/2026"` (`null` y `"x"`), `"12/31/2026"` y `"2026-12-31"`.
  - `QuotationChangeSummaryTests`: un campo `Colombian = HistoryFormat.Colombian` en las 15 llamadas a `HeaderChanged`; cambian `:145` → `"escala desde 1.000 unidades"`, `:228` → `"valor del envío (15.000)"`, `:234` → `"(18.000)"`. Nuevas: `TheValidityIsPrintedWithTheTenantDateFormat` (`MM/dd/yyyy` → `"Editó vigencia (12/31/2026)."`) y `QuantitiesUseTheTenantSeparators` (`ItemAdded("Tornillo", 1500m, dot)` → `"Agregó Tornillo x1,500."`; `ItemQuantityChanged("Tornillo", 1.5m, 2m, Colombian)` → `"Cambió la cantidad de Tornillo: 1,5 → 2."`).
  - `OrderChangeSummaryTests`: cuatro llamadas a `ShippingChanged` con `Colombian`; `:17` y `:23` pasan a `15.000` y `18.000`.
  - `QuotationRetailTests:217`: la llamada lleva `HistoryFormat.Colombian`.
  - Handlers: `CreateQuotationHandlerTests.Handler` y `PreviewNewQuotationHandlerTests.CreateHandler` pasan un `StubTenantDirectory`; nueva `CreateQuotationHandlerTests.TheLineHistoryUsesTheTenantSeparators` (tenant en `DotDecimal`, una línea de `1500` → la fila dice `x1,500`). `RecognizeOrderCarrierHandlerTests.NewHandler` lo mismo, con `TheShippingHistoryUsesTheTenantSeparators` (flete `15000` → `valor del envío (15,000)` en `DotDecimal`).
  - `OrderShippingApiTests:242` → `"valor del envío (15.000)"` (el tenant del harness es `1.234,56`).

  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~HistoryFormatTests|FullyQualifiedName~ChangeSummaryTests|FullyQualifiedName~QuotationRetailTests|FullyQualifiedName~CreateQuotationHandlerTests|FullyQualifiedName~PreviewNewQuotationHandlerTests|FullyQualifiedName~RecognizeOrderCarrierHandlerTests"
  ```
  Esperado: no compila (`CS0246`: `HistoryFormat` no existe).
- [ ] **Step 2: GREEN.** `HistoryFormat` y las firmas de arriba; `Date` y `Number` privados se borran (el comentario de
  «sin ceros a la derecha» se muda a `HistoryFormat.Number`, con D-R6). `QuotationEditsApplication.ApplyAsync` devuelve
  `HeaderBefore`/`HeaderAfter` (la foto «después» en el mismo punto que hoy arma el resumen, `:101`), y
  `SaveQuotationHandler` llama `HeaderChanged(outcome.HeaderBefore, outcome.HeaderAfter, format)` donde hoy lee
  `outcome.HeaderSummary` (`:97`). Cada uno de los ocho handlers lee
  `var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken)` **una vez**, antes de
  escribir la primera fila, y lo pasa a cada resumen. `PreviewQuotation`, `QuotationDraftBuilder` y `PreviewOrderEdits`
  no cambian.
- [ ] **Step 3:** Correr el filtro anterior y, en integración, `OrderShippingApiTests`, `GlobalScaleDiscountApiTests`,
  `QuotationShippingApiTests` y `RetailQuotationApiTests` (las que leen `Details`). Verde.
- [ ] **Step 4: Commit** `feat(quotations): el historial nuevo usa la fecha y los números del tenant (spec 2026-10-23 D-R6)`.

### Task 8: Fechas de texto del Excel con el formato del tenant; la hoja del ERP queda fija (D-R7, D-R8)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerExportBuilder.cs:12-18`; `ExportCustomers.cs:31-41`, `:76-79`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs:44-47`, `:60`, `:119-123` (y la firma del armado de la fila)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs:12-18`, `:51`, `:74`, `:106-112`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs` (`NewProcessor` `:234-247`, `:39`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerExportApiTests.cs`, `CustomersApiHarness.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` (nueva, junto a `:637`)

**Interfaces:**
- Consumes: `RegionalDateFormat.WithTime` (Tarea 1); `StubTenantDirectory` (Tarea 3); `SetTenantRegionalFormatAsync` (Tarea 6).
- Produces: `ICustomerExportBuilder.Build(IReadOnlyList<CustomerDto> customers, TenantCalendar calendar, string dateTimePattern, CancellationToken cancellationToken)`; `ExportCustomersHandler(..., ITenantDirectory tenantDirectory, IExecutionContext executionContext, ITenantClock tenantClock)`; `QuotationsExportProcessor(..., ITenantClock tenantClock, ITenantDirectory tenantDirectory)`.
- Produces (pruebas): `CustomersApiHarness.SetTenantDateFormatAsync(QepApiFactory factory, string dateFormat, string tenantId = TenantId)` (`UPDATE tenancy.tenants SET date_format = …` con el `TenancyDbContext` del scope, como `SeedTenantAsync`).

- [ ] **Step 1: RED.**
  - `QuotationsExportProcessorTests`: `NewProcessor` acepta `StubTenantDirectory? tenantDirectory = null` y, por defecto, uno en `yyyy-MM-dd` (así `:39` sigue afirmando `2026-09-12 10:30`). Nuevas: `CreatedAtUsesTheTenantDateFormat` (`dd/MM/yyyy` → `"12/09/2026 10:30"`) y `WithoutRegionalSettingsCreatedAtIsDayFirst` (`Regional = null` → `"12/09/2026 10:30"`). El total no cambia: en la misma prueba, la última celda sigue siendo `ExportCell.OfNumber(...)` (`Text` nulo, `Number` con el valor), con cualquier formato (D-R7).
  - `CustomerExportApiTests.ExportWritesDatesInTheTenantsDateFormat` — patrón de `ExportWritesDatesAndTheFileNameInTheTenantsLocalTime` (`:82-107`) con `SetTenantDateFormatAsync(factory, "MM/dd/yyyy")` después de `SeedTenantAsync`: celdas `(2, 15)` y `(2, 16)` → `"12/31/2026 23:00"`; el nombre sigue `clientes-20261231-230000.xlsx`.
  - `OrderExportApiTests.TheErpSheetIgnoresTheTenantRegionalFormat` — como `TheSeededLayoutProducesTheErpImportSheet` (`:637`), pero con `SetTenantRegionalFormatAsync(factory, tenantId, "MM/dd/yyyy", "1,234.56")` antes de exportar: `row[4]`, `row[9]` y `row[26]` cumplen `^\d{4}-\d{2}-\d{2}$`; `row[36]` es `10000` con `CultureInfo.InvariantCulture` y `sheet.NumericCells[1][36]` es `true`.

  ```powershell
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsExportProcessorTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerExportApiTests"
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderExportApiTests"
  ```
  Esperado: la unitaria no compila (`CS1739`: `NewProcessor` sin `tenantDirectory`); la de clientes falla con
  `Assert.Equal() Failure` (`2026-12-31 23:00`). La del ERP es de fijación y **pasa** desde el primer intento: para ver
  que muerde, cambia a mano `"yyyy-MM-dd"` por `"dd/MM/yyyy"` en `OrdersExportProcessor.cs:227`, córrela (roja), revierte
  y pega las dos salidas en el reporte.
- [ ] **Step 2: GREEN.** `ExportCustomersHandler` lee `GetRegionalSettingsAsync` junto al calendario (`:76`) y pasa
  `RegionalDateFormat.WithTime(settings?.DateFormat)`; el builder usa ese patrón en `InvariantCulture` en `:122-123`, y el
  comentario de `:119-121` suma D-R7. `QuotationsExportProcessor` lee una vez por job junto a `:51` y `ToCells` recibe el
  patrón. `OrdersExportProcessor` no se toca: un comentario en `:227` cita D-R8 para que nadie lo «arregle».
- [ ] **Step 3:** Correr los tres filtros. Todo en verde.
- [ ] **Step 4: Commit** `feat(exports): las fechas de texto del Excel usan el formato del tenant; la hoja del ERP queda fija (spec 2026-10-23 D-R7, D-R8)`.

### Task 9: Gotcha, corrida combinada y cierre del plan

**Files:**
- Modify: `CLAUDE.md` (sección «Gotchas verificados», dos entradas)
- Modify: `docs/superpowers/plans/2026-10-25-formato-regional-pdf-y-correos.md` (checkboxes y desvíos encontrados al ejecutar)

- [ ] **Step 1: Gotcha.** Dos entradas, tuteando, con esto y nada más. Primera: el respaldo colombiano de D-R5 vive en **tres
  copias** que se cambian juntas porque los módulos no se ven — `RegionalNumberFormat`/`RegionalDateFormat`
  (`Modules.Tenancy.Application`), `EmailRegionalFormat.Colombian` (`Modules.Notifications.Application`, que no ve
  Tenancy) y `QuotationPdf.LegacyNumberFormat` (dominio de Quotations, el formato con el que se supone generada una fila
  de `quotation_pdfs` con `number_format` nulo). Cambiar `TenantNumberFormats.CommaDecimal` sin la del dominio compila y
  regenera en silencio todos los PDF guardados de los tenants colombianos en el siguiente export. Las amarran
  `EmailRegionalFormatTests.ColombianMatchesTheTenancyFallback` y `QuotationPdfTests.ALegacyPdfWithoutFormatCountsAsCommaDecimal`.
  Segunda, corta: `OrdersExportProcessor` **no** lee el formato del tenant a propósito (D-R8: lo importa el
  ERP); «unificarlo» con el Excel de cotizaciones compila, pasa sus pruebas unitarias y rompe la importación en
  silencio. La amarra `OrderExportApiTests.TheErpSheetIgnoresTheTenantRegionalFormat`. Y que el historial es texto
  congelado: un tenant que cambia de formato ve las filas viejas con el anterior, y eso no es un bug. El README no cambia.
- [ ] **Step 2: Corrida combinada** (una sola vez; sin `Api.exe`):

  ```powershell
  dotnet build
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
  dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests
  dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests
  dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
  dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerExportApiTests"
  dotnet test tests/ArchitectureTests/ArchitectureTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~QuotationsReportSummaryApiTests"
  ```
  La última es humo: su harness envía cotizaciones por el handler real, que ahora pide `ITenantDirectory`. Esperado:
  todo verde salvo fallas previas conocidas, comparadas **por nombre** contra `develop` (`git stash` si hace falta).
- [ ] **Step 3:** Marcar los checkboxes de este plan y anotar al final cualquier desvío encontrado al ejecutar.
- [ ] **Step 4: Commit** `docs(formato-regional): gotchas del respaldo colombiano y de la hoja del ERP (spec 2026-10-23 D-R5, D-R8)`.

## Pendiente fuera de este plan

- **DP-1 — WhatsApp por Meta:** sin sender (desvío 3). Cuando exista el plan 3 del envío por canal, decidir si aplica
  `RegionalNumberFormat` y el símbolo de `Currencies`, y si el ejemplo `"2.450.000 COP"` de
  `QuotationTemplateProvisioning` se alinea.
