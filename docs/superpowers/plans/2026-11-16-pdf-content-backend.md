# Contenido configurable del PDF — Implementation Plan (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cada tenant prende, apaga y escribe (con texto enriquecido y variables) el aviso de pago, los términos y el proceso de entrega del PDF de cotizaciones y pedidos.

**Architecture:** El saneador de Soporte pasa a `BuildingBlocks` junto con un lector HTML → árbol de bloques. Quotations guarda tres secciones por tenant (`quotations.pdf_sections`), las valida y las expone por `GET`/`PUT …/quotations/settings/pdf-content`; al generar el PDF arma el árbol, resuelve variables sobre los textos y lo pasa a la plantilla Typst, que lo pinta sin `eval`. El caché del PDF de la cotización se vence por un fingerprint del contenido.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, HtmlSanitizer 11.0.26 (AngleSharp), Typst (`quotation.typ`), xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-16-pdf-content-design.md`

## Global Constraints

- **No se corren pruebas** (owner, 2026-11-15): las pruebas se escriben, pero la verificación de cada paso es `dotnet build` con 0 errores. RED = el build falla por el tipo o miembro que falta (salida literal); GREEN = build en 0 errores. El owner corre las pruebas al final. Decirlo en el brief de cada implementador y revisor.
- Worktree `qep-backend-worktrees/pdf-content`, rama `feature/pdf-content`. Comprobar `git branch --show-current` antes de cada commit. Conventional commits, **sin atribución de IA** (sin `Co-Authored-By`).
- Comandos en PowerShell. Rutas absolutas al worktree en todo script (el cwd vuelve al checkout principal).
- Sintaxis de variables idéntica a Soporte: `{{nombre}}`, espacios opcionales adentro, nombre `[a-z_]+`.
- Límites: `title` ≤ 120; `contentHtml` ≤ 20 000 **después** de sanear.
- Códigos: `422 quotation.pdf_content.unknown_variable`; validación `422 validation.failed` con claves `PaymentNotice.Title`, `Terms.ContentHtml`, etc.
- Auditoría: acción `quotations.pdf_content.updated` (prefijo `quotations.` como `quotations.settings.updated`; el spec decía `quotation.`: gana el código, corregir el spec en el mismo commit de la Tarea 4). Sólo nombres de campo, nunca texto.
- Permisos: `TenancyPermissions.SettingsRead` (GET) y `TenancyPermissions.SettingsUpdate` (PUT), más `TenantModuleGuard` del módulo `Quotations`, como `UpdateQuotationSettingsHandler`.
- Cambiar paquetes de un proyecto exige `dotnet restore --force-evaluate` y commitear los `packages.lock.json` que cambien (CLAUDE.md, gotcha de `NU1004`).
- Comentarios y documentación tutean (español colombiano). Identificadores en inglés.

## Review Focus

1. **Texto con sintaxis de Typst** (`#let x = 1`, `$`, `\`, `[`, `]`, `*`, `_`, `@`, `<x>`) en título o contenido: sale literal en el PDF. → `TheTemplateCompilesWithTypstSyntaxInSectionText` (Tarea 6).
2. **Token partido por marcas o con espacio duro** (`{{em<strong>pre</strong>sa}}`, `{{&nbsp;empresa}}`): 422 `unknown_variable`, no texto literal en el PDF. → `ASplitTokenIsUnknown`, `ANonBreakingSpaceInsideTheBracesIsUnknown` (Tarea 3).
3. **Texto pegado de Word enorme o muy anidado**: el saneador lo rechaza o el límite de 20 000 da 422 con la clave del campo, sin 500. → `ContentLongerThanTheLimitAfterSanitizingIs422` (Tarea 4).
4. **Aviso de pago prendido sin cuenta para el pago**: no sale. → `ThePaymentNoticeIsLeftOutWithoutABillingAccount` (Tarea 5).
5. **HTML sembrado para Origen Botánico que el saneador reescribe**: el texto de la migración es punto fijo del saneador. → `TheOrigenBotanicoSeedIsAFixedPointOfTheSanitizer` (Tarea 4).

---

## Bloque A — Texto enriquecido compartido (Tareas 1–2, un implementador)

### Task 1: El saneador pasa a BuildingBlocks

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Application/RichText/IRichTextSanitizer.cs`
- Create: `src/BuildingBlocks/BuildingBlocks.Infrastructure/RichText/RichTextHtmlSanitizer.cs` (el cuerpo de hoy de `HtmlSupportSanitizer`, movido)
- Modify: `src/BuildingBlocks/BuildingBlocks.Infrastructure/BuildingBlocks.Infrastructure.csproj` (+ `HtmlSanitizer`; + `FluentValidation` si no llega transitivo)
- Modify: `src/Modules/Support/Modules.Support.Infrastructure/Legal/HtmlSupportSanitizer.cs` (envoltorio)
- Modify: `src/Modules/Support/Modules.Support.Infrastructure/Modules.Support.Infrastructure.csproj` (quita `HtmlSanitizer` si queda sin uso directo)
- Modify: los `packages.lock.json` que cambie el restore
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportHtmlSanitizerTests.cs` (sin cambiar lo que afirman)

**Interfaces:**
- Produces:
  - `namespace BuildingBlocks.Application.RichText; public sealed record RichTextSanitized(string Html, bool HasVisibleText);`
  - `public interface IRichTextSanitizer { RichTextSanitized Sanitize(string html, string propertyName); }` — `propertyName` es la clave del `ValidationException` que lanza al rechazar (hoy fija en `"ContentHtml"`).
  - `namespace BuildingBlocks.Infrastructure.RichText; public sealed class RichTextHtmlSanitizer : IRichTextSanitizer` con las mismas constantes (`AllowedTags`, `AllowedSchemes`, `MaxDepth = 100`, `MaxElements = 10_000`, `MaxPasses = 3`).
  - `HtmlSupportSanitizer.Sanitize(html)` = `new RichTextHtmlSanitizer().Sanitize(html, "ContentHtml")` mapeado a `SanitizedHtml`.

- [ ] **Step 1: RED.** Escribir el envoltorio de Soporte contra `RichTextHtmlSanitizer` antes de crearlo. Run: `dotnet build src/Modules/Support/Modules.Support.Infrastructure` → Expected: `error CS0246: … 'RichTextHtmlSanitizer' could not be found`.
- [ ] **Step 2: Mover el núcleo** a `RichTextHtmlSanitizer` (allowlist, pasadas hasta punto fijo, límites, `rel`/`target` de links, `Rejected(propertyName, message)`). Soporte conserva `ISupportHtmlSanitizer`, `SanitizedHtml` y `CachingSupportHtmlSanitizer`.
- [ ] **Step 3: Restore.** Run: `dotnet restore --force-evaluate` en la raíz del worktree → Expected: exit 0; `git status` lista los locks cambiados.
- [ ] **Step 4: GREEN.** Run: `dotnet build` (solución) → Expected: `0 Error(s)`.
- [ ] **Step 5: Commit** — `refactor(building-blocks): share the rich text sanitizer` (props, csproj, locks, código).

### Task 2: Lector HTML → árbol de bloques

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Application/RichText/RichTextBlocks.cs` (records + `MapText`)
- Create: `src/BuildingBlocks/BuildingBlocks.Infrastructure/RichText/RichTextHtmlReader.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/RichTextHtmlReaderTests.cs` (Quotations es el consumidor; no hay proyecto de pruebas de BuildingBlocks)

**Interfaces:**
- Produces (namespace `BuildingBlocks.Application.RichText`; serializan camelCase con `JsonSerializerDefaults.Web`, que es lo que lee la plantilla):
  - `public sealed record RichTextRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, string? Href = null, bool Break = false);`
  - `public sealed record RichTextBlock(string Kind, int Level = 0, bool Ordered = false, IReadOnlyList<RichTextRun>? Runs = null, IReadOnlyList<IReadOnlyList<RichTextBlock>>? Items = null, IReadOnlyList<RichTextBlock>? Children = null);` — `Kind` ∈ `paragraph` | `heading` (`Level` 2–4, `Runs`) | `list` (`Ordered`, `Items`: cada ítem es una lista de bloques) | `quote` (`Children`) | `rule`.
  - `public interface IRichTextReader { IReadOnlyList<RichTextBlock> Read(string sanitizedHtml); }`
  - `public static class RichTextTree { public static IReadOnlyList<RichTextBlock> MapText(IReadOnlyList<RichTextBlock> blocks, Func<string, string> map); }` — aplica `map` a cada `Run.Text` que no es `Break`, recursivo.
  - `public sealed class RichTextHtmlReader : IRichTextReader` (Infrastructure, AngleSharp).

Reglas del lector: texto suelto en un `li` o en `blockquote` va en un `paragraph`; runs contiguos con las mismas marcas se funden en uno (así un token no queda partido por nodos de texto); `<br>` es `new RichTextRun("", Break: true)`; `a[href]` pone `Href` a sus runs; un párrafo vacío (`<p></p>`) se omite.

- [ ] **Step 1: Tests** en `RichTextHtmlReaderTests`:
  - `AParagraphWithMarksBecomesRuns`: `<p>Hola <strong>mundo</strong> <em>y <u>más</u></em></p>` → un `paragraph` con runs `("Hola ")`, `("mundo", Bold)`, `(" ")`, `("y ", Italic)`, `("más", Italic, Underline)`.
  - `ABreakIsARun`: `<p>a<br>b</p>` → runs `a`, `Break`, `b`.
  - `HeadingsKeepTheirLevel`: `<h3>X</h3>` → `heading` `Level` 3.
  - `NestedListsBecomeItems`: `<ul><li>a<ul><li>b</li></ul></li></ul>` → `list` con un ítem `[paragraph a, list[ [paragraph b] ]]`.
  - `ALinkCarriesItsHref`: `<p><a href="https://x.co">x</a></p>` → run `Href = "https://x.co"`.
  - `AdjacentRunsWithTheSameMarksMerge`: `<p><strong>{{em</strong><strong>presa}}</strong></p>` → un run `"{{empresa}}"` Bold.
  - `MapTextReplacesEveryRunText`: `MapText(blocks, t => t.ToUpperInvariant())` cambia los textos y deja `Break` igual.
- [ ] **Step 2: RED.** Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → Expected: `CS0246 'RichTextHtmlReader'`.
- [ ] **Step 3: Implementar** records, `MapText` y `RichTextHtmlReader.Read`.
- [ ] **Step 4: GREEN.** Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → `0 Error(s)`.
- [ ] **Step 5: Commit** — `feat(building-blocks): read sanitized html into blocks`.

## Bloque B — Configuración y API (Tareas 3–4, un implementador)

### Task 3: Dominio — secciones y variables

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/PdfContent.cs` (`PdfSectionKey`, `PdfSection`, `PdfContent`)
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdfVariables.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfVariablesTests.cs`, `PdfContentTests.cs`

**Interfaces:**
- Produces (namespace `Modules.Quotations.Domain`):
  - `public enum PdfSectionKey { PaymentNotice, Terms, DeliveryProcess }` (se guarda por nombre).
  - `public sealed record PdfSection(PdfSectionKey Key, bool Enabled, bool InQuotation, bool InOrder, string Title, string ContentHtml) { public bool PrintsOn(bool isOrder); public static PdfSection Empty(PdfSectionKey key); }` — `Empty`: apagada, `InQuotation`/`InOrder` en `true`, textos vacíos.
  - `public sealed record PdfContent(PdfSection PaymentNotice, PdfSection Terms, PdfSection DeliveryProcess) { public static PdfContent Empty { get; } public static PdfContent Create(PdfSection paymentNotice, PdfSection terms, PdfSection deliveryProcess); public IEnumerable<PdfSection> All { get; } }` — `Create` lanza `QuotationsDomainException("quotation.pdf_content.unknown_variable", …)` si `QuotationPdfVariables.UnknownIn` encuentra algo en algún `Title` o `ContentHtml`.
  - `public sealed record QuotationPdfVariable(string Name, string Label, string Example);`
  - `public sealed record QuotationPdfVariableValues(string? Company, string? CompanyTaxId, string Customer, string Number, string IssuedOn, string? ValidUntil, string Advisor, string Total);` (ya formateados).
  - `public static partial class QuotationPdfVariables { IReadOnlyList<QuotationPdfVariable> All; IReadOnlyList<string> UnknownIn(string? text); string Fill(string text, QuotationPdfVariableValues values); }` — `Fill` reemplaza todo token conocido; un valor `null` es `""`; un token desconocido queda literal. Mismo regex de tokens que `SupportLegalVariables` (copia, no referencia: Quotations no ve Support).

Lista exacta (`name`, `label`, `example`):

| `empresa` | Empresa que factura | Comercial Andina S.A.S. |
| `nit_empresa` | NIT de la empresa | 900123456-7 |
| `cliente` | Cliente | María Pérez |
| `numero` | Número del documento | 1001 |
| `fecha` | Fecha de emisión | 16 de noviembre de 2026 |
| `vigencia` | Vigente hasta | 30 de noviembre de 2026 |
| `asesor` | Asesor | Laura Gómez |
| `total` | Total | $ 1.250.000 |

- [ ] **Step 1: Tests.**
  - `UnknownInFindsEveryTokenOutsideTheList`: `"{{ciudad}} y {{Empresa}} y {{}} y {{ empresa }}"` → `["ciudad", "Empresa", ""]`.
  - `ASplitTokenIsUnknown`: `UnknownIn("{{em<strong>pre</strong>sa}}")` no vacío.
  - `ANonBreakingSpaceInsideTheBracesIsUnknown`: `UnknownIn("{{&nbsp;empresa}}")` no vacío.
  - `FillResolvesEveryVariable`: los ocho tokens con valores conocidos → texto esperado; `{{ empresa }}` con espacios también.
  - `AMissingValueIsEmpty`: `Company = null` → `"Para  ."` desde `"Para {{empresa}}."`.
  - `CreateRejectsAnUnknownVariable`: `PdfContent.Create` con `Terms.Title = "{{ciudad}}"` lanza código `quotation.pdf_content.unknown_variable`.
  - `PrintsOnFollowsTheFlags`: apagada → nunca; prendida sólo `InQuotation` → `PrintsOn(false)` true, `PrintsOn(true)` false.
- [ ] **Step 2: RED.** Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → `CS0246 'PdfContent'`.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN.** Mismo build → `0 Error(s)`.
- [ ] **Step 5: Commit** — `feat(quotations): model the configurable pdf sections`.

### Task 4: Persistencia, migración y API

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/PdfSectionRow.cs`, `PdfContentStore.cs`
- Modify: `…/Persistence/QuotationsDbContext.cs` (`pdf_sections`)
- Create: migración `AddPdfSections` (comando de CLAUDE.md § migraciones)
- Create: `src/Modules/Quotations/Modules.Quotations.Application/PdfContentSettings.cs` (`IPdfContentStore`, DTOs, query, command, validator, handlers)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationSettingsEndpoints.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` (store; `IRichTextSanitizer` → `RichTextHtmlSanitizer` y `IRichTextReader` → `RichTextHtmlReader` como singletons)
- Modify: `docs/superpowers/specs/2026-11-16-pdf-content-design.md` (acción de auditoría `quotations.pdf_content.updated`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PdfContentApiTests.cs`, `PdfContentMigrationTests.cs`

**Interfaces:**
- Consumes: `IRichTextSanitizer` (Tarea 1), `PdfContent`/`PdfSection`/`QuotationPdfVariables` (Tarea 3).
- Produces:
  - `public interface IPdfContentStore { Task<PdfContent> GetAsync(Guid tenantId, CancellationToken ct); Task SaveAsync(Guid tenantId, PdfContent content, CancellationToken ct); }` — sin filas → `PdfContent.Empty`; `SaveAsync` hace upsert de las tres filas.
  - `public sealed record PdfSectionDto(bool Enabled, bool InQuotation, bool InOrder, string Title, string ContentHtml);`
  - `public sealed record PdfContentDto(PdfSectionDto PaymentNotice, PdfSectionDto Terms, PdfSectionDto DeliveryProcess, IReadOnlyList<QuotationPdfVariable> Variables);` (`Variables` último).
  - `GetPdfContentQuery(Guid TenantId) : IQuery<PdfContentDto>`; `UpdatePdfContentCommand(Guid TenantId, PdfSectionDto? PaymentNotice, PdfSectionDto? Terms, PdfSectionDto? DeliveryProcess) : ICommand<PdfContentDto>`.
  - Endpoints `GET`/`PUT /api/v1/tenants/{tenantId:guid}/quotations/settings/pdf-content`; request `UpdatePdfContentRequest(PdfSectionDto? PaymentNotice, PdfSectionDto? Terms, PdfSectionDto? DeliveryProcess)`.

Tabla `quotations.pdf_sections`: `tenant_id uuid`, `section text` (CHECK `IN ('PaymentNotice','Terms','DeliveryProcess')`), `enabled bool`, `in_quotation bool`, `in_order bool`, `title varchar(120)`, `content_html text`, `updated_at timestamptz`; PK `(tenant_id, section)`.

Handler de `PUT` (mismo orden que `UpdateQuotationSettingsHandler`): autorizar `SettingsUpdate` → guard de módulo → validador (secciones no nulas, `Title` ≤ 120) → sanear cada `ContentHtml` con `propertyName` = `"{Section}.ContentHtml"` → por sección: `Html.Length > 20000` o (`Enabled` y `!HasVisibleText`) es `ValidationException` con esa clave → `PdfContent.Create` (variables) → transacción → `SaveAsync` → `PublishWithDetails` con acción `quotations.pdf_content.updated` y `details` = rutas de los campos que cambiaron respecto a lo leído (valor `null`) → responde el DTO guardado.

Migración `AddPdfSections`, además de la tabla: `INSERT INTO quotations.pdf_sections (...) SELECT t.id, … FROM tenancy.tenants t WHERE t.slug = 'origen-botanico' ON CONFLICT DO NOTHING`, dos filas (`PaymentNotice`, `DeliveryProcess`), ambas `enabled`, `in_quotation`, `in_order` en `true`, `updated_at = now()`, con este contenido literal (texto de `quotation.typ` de hoy):

- `PaymentNotice` — título `Ten en cuenta:`; HTML:
  `<p>no aceptamos pagos por corresponsal bancario,<br>ni depósitos en entidades bancarias, ni cajeros.<br>Solo debes pagar el valor de tu pedido, el valor a pagar<br>debe de ser libre de toda comisión bancaria.</p>`
- `DeliveryProcess` — título `Proceso de Alistamiento y Recepción de Pedidos.`; HTML (una sola línea en la migración):
  `<p><strong>Alistamiento y Entrega:</strong><br>El proveedor preparará los pedidos bajo filmación y los entregará a la empresa transportadora dentro de los 8 días hábiles siguientes a la confirmación del pedido, siempre que estos hayan sido solicitados y pagados por el mayorista.</p><p><strong>Recepción de la Mercancía:</strong><br>Al recibir los productos de la transportadora, el mayorista deberá revisar el estado del paquete. No recibir en caso de detectar:</p><ul><li>Cinta contramarcada deteriorada</li><li>Cajas dañadas y/o abiertas</li><li>Cintas, zunchos o cajas diferentes a las del proveedor</li></ul><p>Si decide recibir el paquete a pesar de alguna de estas irregularidades, lo hará bajo su responsabilidad. En este caso, deberá:</p><ul><li>Dejar constancia de la novedad en la guía de recepción de la transportadora.</li><li>Informar de inmediato al proveedor.</li><li>Enviar fotografías como evidencia.</li></ul><p><strong>Verificación del Contenido:</strong><br>Una vez recibido el pedido, el mayorista deberá grabar un video al abrir las cajas o paquetes, evidenciando el estado y la cantidad de los productos.</p><p><strong>Reporte de Novedades:</strong><br>Si detecta daños, faltantes o deterioro en los productos, deberá informar al proveedor en un plazo máximo de 1 día hábil después de recibirlos. El reporte debe incluir:</p><ul><li>Video de la apertura del paquete.</li><li>Fotografías de las cajas.</li><li>Imágenes de los 3 stickers identificativos del proveedor.</li></ul>`

- [ ] **Step 1: Tests de integración** (`PdfContentApiTests`, host compartido salvo las de migración):
  - `ATenantWithoutConfigurationGetsThreeDisabledSections`: `GET` → las tres `enabled: false`, `inQuotation`/`inOrder` `true`, textos vacíos; `variables` con los ocho nombres en el orden de la tabla.
  - `PutReplacesTheThreeSectionsAndGetReturnsThem`.
  - `PutSanitizesTheHtml`: `<p>x<script>alert(1)</script></p>` → se guarda `<p>x</p>`.
  - `AnUnknownVariableIs422AndSavesNothing`: `Terms.ContentHtml = "<p>{{ciudad}}</p>"` → `422`, `code = "quotation.pdf_content.unknown_variable"`; el `GET` sigue como antes.
  - `AnEnabledSectionWithoutTextIs422`: `Terms` prendida con `<p> </p>` → `422 validation.failed`, `errors` contiene `Terms.ContentHtml`.
  - `ContentLongerThanTheLimitAfterSanitizingIs422`: `<p>` + 20 001 letras → clave `Terms.ContentHtml`.
  - `ATitleLongerThan120Is422` → clave `PaymentNotice.Title`.
  - `PutWithoutSettingsUpdateIs403` (sólo `tenancy.settings.read` en `X-Permissions`).
  - `ThePutIsAuditedWithFieldNamesOnly`: el outbox trae `quotations.pdf_content.updated` y ningún valor del texto.
- [ ] **Step 2: Tests de migración** (`PdfContentMigrationTests`, base vacía propia con `StartEmptyDatabaseAsync`, migrar hasta la anterior, sembrar `tenancy.tenants` con `slug = 'origen-botanico'` y otro tenant, migrar a la última):
  - `OrigenBotanicoKeepsItsTwoSections`: dos filas con los títulos y el HTML de arriba; `Terms` sin fila.
  - `OtherTenantsGetNoRows`.
  - `TheOrigenBotanicoSeedIsAFixedPointOfTheSanitizer`: `new RichTextHtmlSanitizer().Sanitize(html, "x").Html == html` para las dos filas.
- [ ] **Step 3: RED.** Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests` → `CS0246 'PdfContentDto'` (o el primer tipo que falte).
- [ ] **Step 4: Implementar** store, DbContext, migración, application, endpoints y registro. Generar la migración con el comando de CLAUDE.md y agregar el `INSERT` a mano en `Up`; el `Down` borra la tabla.
- [ ] **Step 5: GREEN.** Run: `dotnet build` (solución) → `0 Error(s)`.
- [ ] **Step 6: Commit** — `feat(quotations): configure the pdf content per tenant` (incluye la corrección del spec).

## Bloque C — Documento, caché y plantilla (Tareas 5–6, un implementador)

### Task 5: Secciones en el documento y caché

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfSections.cs` (records, builder, valores de variables, fingerprint)
- Modify: `…/Application/QuotationPdfDocument.cs` (parámetro final `QuotationPdfSections? Sections = null`)
- Modify: `…/Application/QuotationPdfProvider.cs`, `…/Application/OrderPdfProvider.cs`
- Modify: `…/Domain/QuotationPdf.cs` (`ContentFingerprint`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfSectionsTests.cs`, `QuotationPdfTests.cs`

**Interfaces:**
- Consumes: `IPdfContentStore` (Tarea 4), `IRichTextReader`/`RichTextTree.MapText` (Tarea 2), `QuotationPdfVariables` (Tarea 3).
- Produces:
  - `public sealed record QuotationPdfSection(string Title, IReadOnlyList<RichTextBlock> Blocks);`
  - `public sealed record QuotationPdfSections(QuotationPdfSection? PaymentNotice, QuotationPdfSection? Terms, QuotationPdfSection? DeliveryProcess);` — viaja como `data.sections.paymentNotice.title` / `.blocks`.
  - `public static class QuotationPdfSectionsBuilder { public static QuotationPdfSections Build(PdfContent content, QuotationPdfDocument document, IRichTextReader reader); public static QuotationPdfVariableValues ValuesOf(QuotationPdfDocument document); public static string Fingerprint(PdfContent content, QuotationPdfKind kind); }`
    - `Build`: una sección es `null` si no `PrintsOn(document.Kind == Order)`; `PaymentNotice` además `null` si `document.BillingAccount is null`. Título con `Fill`; bloques con `MapText(reader.Read(html), t => Fill(t, values))`.
    - `ValuesOf`: `IssuedOn`/`ValidUntil` como «16 de noviembre de 2026» (mismos nombres de mes que `meses` de `quotation.typ`, sin mayúscula); `Total` = `Symbol + " " + monto` con `Decimals`, `ThousandsSeparator` y `DecimalSeparator` del documento, igual que `importe()` de la plantilla.
    - `Fingerprint`: SHA-256 hex en minúsculas sobre, para cada sección que `PrintsOn` ese tipo, `Key|Title|ContentHtml`, unidas con `\n`; sin secciones → hash de `""`.
  - `QuotationPdf`: `string? ContentFingerprint`; `Generate(…, string? numberFormat, string contentFingerprint, DateTimeOffset generatedAt)`; `IsStaleFor(long quotationVersion, Guid? logoFileId, string numberFormat, string contentFingerprint)` suma `|| ContentFingerprint != contentFingerprint` (una fila con `null` queda vencida); `Regenerate(…, string contentFingerprint, …)`.
  - Providers: leen `IPdfContentStore.GetAsync`, calculan el fingerprint antes de decidir el caché (sólo la cotización), y renderizan `mapped with { Sections = QuotationPdfSectionsBuilder.Build(content, mapped, reader) }`.

- [ ] **Step 1: Tests.**
  - `ATenantWithoutConfigurationPrintsNoSection`: `Build(PdfContent.Empty, …)` → las tres `null`.
  - `ASectionOnlyForQuotationsIsLeftOutOfTheOrder` y al revés.
  - `ThePaymentNoticeIsLeftOutWithoutABillingAccount`.
  - `VariablesAreResolvedInTitleAndBlocks`: `{{cliente}}` en el título y `{{numero}}` dentro de `<strong>` → valores del documento.
  - `ValidUntilIsEmptyOnAnOrder` y `CompanyIsEmptyWithoutABillingAccount`.
  - `TheTotalIsFormattedLikeTheTemplate`: COP, `Total = 1250000`, separadores `.`/`,` → `"$ 1.250.000"`; USD con dos decimales y formato `1,234.56` → `Currencies.Get("USD").Symbol + " 1,250.50"`.
  - `TheFingerprintChangesWithTheContentAndNotWithTheOtherKind`: cambiar una sección sólo de pedido no cambia el fingerprint de la cotización.
  - En `QuotationPdfTests`: `ADifferentContentFingerprintIsStale`, `ARowWithoutFingerprintIsStale`, `TheSameFingerprintIsNotStale`.
- [ ] **Step 2: RED.** Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → `CS0246 'QuotationPdfSectionsBuilder'`.
- [ ] **Step 3: Implementar**, con la columna `quotations.quotation_pdfs.content_fingerprint` (`text NULL`) mapeada en `QuotationsDbContext` y su migración `AddQuotationPdfContentFingerprint` (aditiva). Actualizar `StubPdfRenderer` del harness de integración si su firma lo exige.
- [ ] **Step 4: GREEN.** Run: `dotnet build` → `0 Error(s)`.
- [ ] **Step 5: Commit** — `feat(quotations): print the configured sections and expire cached pdfs`.

### Task 6: La plantilla pinta las secciones

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs`

**Interfaces:**
- Consumes: `data.sections` (Tarea 5): `paymentNotice | terms | deliveryProcess`, cada uno `none` o `{ title, blocks }`; bloques y runs con los campos camelCase de la Tarea 2.

Cambios en `quotation.typ`:
- Quitar el aviso fijo (`#if data.billingAccount != none [ … Ten en cuenta … ]`) y la página fija del proceso (desde `#pagebreak()`), con sus comentarios.
- Funciones nuevas: `#let runs(lista)` (cada run: `Break` → `linebreak()`; si no, `[#run.text]` envuelto en `strong`/`emph`/`underline`/`strike` según las marcas y en `link(run.href)` si trae `href`) y `#let bloques(lista)` (recursiva: `paragraph` → `par(runs(..))`; `heading` → `text(weight: "bold", size: 10pt | 9.5pt | 9pt)` según `level` 2/3/4, **no** `heading()`; `list` → `list(..)` o `enum(..)` con `bloques(item)` por ítem; `quote` → `pad(left: 8pt, text(fill: apagado, bloques(children)))`; `rule` → `line(length: 100%, stroke: 0.4pt + filete)`). Nunca `eval`.
- Aviso de pago: donde estaba el fijo, `#if data.sections.paymentNotice != none [ … ]` con `text(size: 7.5pt, fill: naranja)`, el título en negrita y luego `bloques(...)`.
- Términos: después del bloque de totales, título 10pt negrita y `bloques(...)` a 8.5pt.
- Proceso: `#if data.sections.deliveryProcess != none [ #pagebreak() … ]`, título 11pt negrita, `bloques(...)`.
- `data.sections` puede ser `none` (documento sin secciones): guardar cada acceso con `data.sections != none`.

- [ ] **Step 1: Tests.** Agregar `Sections` al fixture `Complete()` (las tres, con párrafo, título, lista anidada, cita, regla y link) y dejar `Minimal()` sin secciones.
  - `TheTemplateCompilesWithAllSections` → `AssertCompilesAsync(Complete())`.
  - `TheTemplateCompilesWithoutSections` → `AssertCompilesAsync(Minimal())`.
  - `TheTemplateCompilesWithTypstSyntaxInSectionText`: un run con `"#let x = 1 $a$ \\ [b] *c* _d_ @e <f>"` compila.
  - `TheTemplateNeverEvaluatesData`: `StripComments(source)` no contiene `eval(`.
  - `TheFixedTextsAreGone`: el source no contiene `corresponsal bancario` ni `Alistamiento y Entrega`.
  - `EveryFieldTheTemplateReadsExistsInThePayload` sigue verde con el `Complete()` nuevo (sumar a `Bindings` las variables nuevas que desreferencien `data`, si las hay).
- [ ] **Step 2: RED.** Sin cambios de tipos, el RED de esta tarea es la prueba de `TheFixedTextsAreGone` contra la plantilla actual; como no se corren pruebas, la evidencia es `rg -n "corresponsal bancario" src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ` → una coincidencia antes del cambio, ninguna después.
- [ ] **Step 3: Implementar** la plantilla.
- [ ] **Step 4: GREEN.** Run: `dotnet build` → `0 Error(s)`; el `rg` anterior → sin coincidencias. Si `typst` está instalado (`FindTypst()`), compilar a mano el fixture es opcional; no correr la suite.
- [ ] **Step 5: Commit** — `feat(quotations): render the configured sections in the pdf template`.

## Cierre

- [ ] Revisión fuerte de toda la rama (una sola, al final), con el brief «no corras pruebas, sólo build».
- [ ] README: sección del PDF de cotizaciones (qué es configurable, la migración de Origen Botánico, que el pedido no cachea). CLAUDE.md: gotcha de que `quotation.typ` nunca hace `eval` de datos del tenant, y que el saneador vive en `BuildingBlocks` y lo comparten Soporte y Quotations.
- [ ] Avisar al owner que las pruebas de Quotations, Support y ArchitectureTests están escritas y sin correr. `docs/estado.md` se actualiza en `develop` al mergear, no en esta rama.
