# Soporte — documentos legales del portal público, backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de la sección «Antes de empezar» mandan sobre el proceso del skill** (tres bloques con un solo implementador cada uno, una revisión por bloque).

**Goal:** Que cada tenant escriba en texto enriquecido y versione tres documentos —aviso de privacidad, aviso anónimo y Términos y Condiciones del SAC—; que el portal público `qep-frontend-support` los reciba ya saneados en `GET /form`; que cada radicado guarde la foto de lo que la persona aceptó (también los términos, también en el anónimo, sin IP); y que la consola lo vea en el detalle del ticket.

**Architecture:** Dos tablas insert-only nuevas en `support`: `legal_document_versions` (versión por tenant y tipo) y `legal_acceptances` (una fila por ticket y tipo, con la foto). El HTML se sanea con `HtmlSanitizer` detrás del puerto `ISupportHtmlSanitizer` (Application) implementado en `Modules.Support.Infrastructure`, al publicar y otra vez al servir al portal. Las tablas de la fase 3 (`legal_text_versions`, `consents`) se quedan mapeadas y sin escritor; la migración rellena las versiones nuevas desde la vigente de cada tenant. Las rutas `…/settings/legal-texts` desaparecen (sin consumidor).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, `HtmlSanitizer` (mganss, sobre AngleSharp), xUnit v3, Testcontainers (PostgreSQL).

**Spec:** [`docs/superpowers/specs/2026-11-15-support-portal-legal-documents-design.md`](../specs/2026-11-15-support-portal-legal-documents-design.md), completo, con D-1 a D-7 (§15; el NIT de D-6 sigue pendiente). Reemplaza §9.5 del [spec del módulo](../specs/2026-11-14-support-tickets-pqrs-design.md). Plan previo: [`fase 3`](2026-11-15-support-phase-3-public-form-backend.md) (F15–F17 quedan reemplazadas por este plan).

---

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-portal-legal`, rama `feature/support-portal-legal`, sale de `develop` en `e28ecd0d` (fases 1 a 3, tiempo real y notas mergeadas). Rutas absolutas siempre: un script con `cwd` relativo escribe en el checkout de `develop`, que usan otras sesiones.
- Antes de **cada** commit: `git branch --show-current`. Si dice `main` o `develop`, frena. Commit con `git commit -m "…" -- <rutas>`.
- Conventional commits en inglés, **sin `Co-Authored-By` ni ninguna atribución de IA**, aunque el harness la sugiera.
- PowerShell, sin `&&`. Si `Api.exe` o `dotnet Api.dll` corre, el build falla por archivo bloqueado: avisa, no lo detengas.
- **Presupuesto de pruebas (workspace `CLAUDE.md`, owner 2026-11-12):**
  - TDD: la prueba primero. Si el RED esperado es de compilación (`CS0246`, `CS1061`…), la evidencia es la salida de `dotnet build` del proyecto de pruebas; **no se levantan contenedores para un RED de compilación**. Un RED de comportamiento (la prueba compila y falla) sí se corre, filtrado por nombre.
  - Mientras se itera, integración **filtrada por nombre**: `dotnet test $SI --filter "FullyQualifiedName~<NombreDeLaPrueba>" -- xUnit.MaxParallelThreads=4`. Nunca por clase mientras se itera.
  - Al cerrar cada bloque, **una** corrida de cada clase de prueba tocada en el bloque.
  - Antes de mergear a `develop`: las suites de `$SU`, `$SI`, `$BU` y `$AT`, una a la vez. Toda la solución no hace falta (un solo módulo y su migración; `Program.cs` no cambia).
  - **Nunca dos corridas de integración a la vez.** Antes de una corrida: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. Si hay otra, espera; no la detengas.
  - Los subagentes corren las pruebas **en primer plano**.
  - Las salidas literales de RED y GREEN van al reporte del bloque.
  - Si el owner repite para esta rama la regla de la fase 3 («no se corre ninguna prueba»), RED y GREEN se reducen a `dotnet build` y cada prueba de comportamiento se marca «RED sin evidencia: no se corre».
- Atajos: `$W` = worktree; `$SU`/`$SI` = `$W\tests\Modules\Support\Modules.Support.{UnitTests,IntegrationTests}`; `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`; `$AT` = `$W\tests\ArchitectureTests\ArchitectureTests`; `$INF` = `$W\src\Modules\Support\Modules.Support.Infrastructure`.
- Migración con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add AddSupportLegalDocuments --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  ```
  Revisa el `Up` generado: **no** puede traer `DropTable` ni `DropColumn` (L10). El `Down` se edita a mano (L11).
  El Bloque C agrega otra, después: `dotnet ef migrations add AddSupportTicketClientRequestId` (mismos parámetros).
- `docs/estado.md` **no se toca** en esta rama.

## Global Constraints

- Tipos de documento: `PrivacyNotice`, `AnonymousNotice`, `Terms` en cuerpos, respuestas y base; `privacy-notice`, `anonymous-notice`, `terms` en la ruta.
- **Todo HTML que se guarda o se sirve pasó por `ISupportHtmlSanitizer`**: al publicar (se guarda la salida), al servir `GET /form` (se re-sanea lo leído) y al escribir la foto de una aceptación (se guarda lo que se sirvió). Ningún otro camino escribe `content_html`.
- La lista blanca es la de spec §6.2, sin cambios: `p br strong em u s h2 h3 h4 ul ol li blockquote hr` sin atributos, y `a` sólo con `href` `https:`/`mailto:`/`tel:`, con `rel="noopener noreferrer nofollow"` y `target="_blank"` puestos por el saneador.
- Application no referencia `HtmlSanitizer` ni AngleSharp (`SupportLayerTests`). Domain tampoco.
- `legal_document_versions` y `legal_acceptances` son insert-only: ningún método de dominio edita ni borra, sin FK, y el `Down` no borra las tablas.
- `legal_text_versions` y `consents` **siguen mapeadas** en `SupportDbContext` (quitarlas del modelo haría que EF las borrara) y nada las escribe.
- Un ticket anónimo no deja IP en ninguna fila (`legal_acceptances.visitor_ip` es `NULL` en sus dos filas). Ningún handler del grupo público lee el contexto de ejecución ni la cookie.
- La migración es **aditiva** (regla expand/contract de `CLAUDE.md`): tablas e índices nuevos más un `INSERT … SELECT`. Nada se altera ni se borra.
- El contenido de un documento nunca va a logs, auditoría ni `ticket_events`; la auditoría lleva nombres de campos.
- Mensajes legibles en español tuteando; códigos, rutas y tipos como contrato, en inglés.

## Decisiones del plan

Mías (planner) sobre lo que el spec deja al implementador. D-1 a D-5 del spec están resueltas; de D-6 queda pendiente el NIT (fuera del plan) y de D-7 la confirmación de L28.

| # | Decisión | Por qué |
| --- | --- | --- |
| L1 | Enum `SupportLegalDocumentKind { PrivacyNotice, AnonymousNotice, Terms }` en Domain, guardado por nombre (`HasConversion<string>()`, varchar(32)); `SupportLegalDocumentKinds.TryParseRoute(string, out kind)` traduce el segmento de la ruta. Un segmento desconocido es `404 support.legal_documents.kind_not_found` (`ResourceNotFoundException`). | Una sola tabla de traducción; la ruta no puede usar el nombre del enum porque es kebab-case. |
| L2 | Puerto `ISupportHtmlSanitizer { SanitizedHtml Sanitize(string html); }` con `SanitizedHtml(string Html, bool HasVisibleText)`. `HasVisibleText` = el texto del DOM saneado, con `&nbsp;` y espacios recortados, no vacío. Implementación `HtmlSupportSanitizer` en `$INF/Legal/`, registrada como singleton; arma una instancia de `Ganss.Xss.HtmlSanitizer` **por llamada** (la configuración vive en un método de fábrica). | El saneador expone eventos y estado mutable; no se apuesta a que sea seguro entre hilos. El costo por llamada es despreciable (a lo sumo tres documentos por request). |
| L3 | Configuración de la librería: `AllowedTags`, `AllowedAttributes` (`href`), `AllowedSchemes` (`https`, `mailto`, `tel`) y `UriAttributes` (`href`) se vacían y se cargan con la lista blanca; `AllowedCssProperties`, `AllowedAtRules` y `AllowedClasses` vacíos; `AllowDataAttributes = false`; `KeepChildNodes = true` (desenvuelve `span`, `div`, `b`, `i`, `font`…). Antes de sanear, los elementos `script style template noscript iframe object embed svg math form input img video audio` se quitan **con su contenido** del DOM parseado (AngleSharp), y los comentarios se quitan. `PostProcessNode` fija `rel` y `target` en cada `a`. Sin URL relativas: un `href` sin esquema absoluto se quita. | `KeepChildNodes` dejaría el código de un `<script>` como texto visible; quitar primero esa lista evita basura y mXSS. Fijar `rel`/`target` después hace la salida idempotente: en la segunda pasada se quitan y se vuelven a poner igual. |
| L4 | Paquete: `HtmlSanitizer` con la **última versión estable publicada en nuget.org al implementar** (9.x), fijada exacta en `Directory.Packages.props` (`<PackageVersion Include="HtmlSanitizer" Version="…" />`) y referenciada sólo en `Modules.Support.Infrastructure.csproj`. Antes de fijarla: `dotnet list $INF package --vulnerable --include-transitive` limpio. Después: `dotnet restore --force-evaluate` en `$W` y `dotnet restore --locked-mode` limpio; se commitean todos los `packages.lock.json` que cambien (Infrastructure, `Api`, `Bootstrapper` y los proyectos de prueba que la arrastran). | D-3. El repo restaura con lock files (`RestorePackagesWithLockFile`) y el pipeline usa `--locked-mode`. |
| L5 | Límites: `SupportLegalDocumentVersion.ContentMaxLength = 60_000` (HTML saneado), `RawContentMaxLength = 120_000` (validador, antes de sanear), `AuthorizationQuestionMaxLength = 500` (plano, recortado, sin caracteres de control salvo espacios), `PolicyUrlMaxLength = 500` (reusa la regla https de `SupportLegalTextVersion.IsValidPolicyUrl`, que se mueve a `SupportLegalDocumentVersion.IsValidPolicyUrl`). | Spec §6.4. |
| L6 | Publicar: `PublishLegalDocumentHandler` — `EnsureAsync(SettingsManage)` → validador → membresía activa → `provisioner.EnsureAsync` → vigente del tipo, `If-Match` igual o `412 concurrency.conflict` → sanear → sin texto visible `422 support.legal_documents.content_empty`, más de 60 000 `422 support.legal_documents.content_too_long` → `Create` versión `n+1` → auditoría → `SaveChangesAsync`. `IX_legal_document_versions_tenant_kind_version` se traduce por nombre en `SupportUnitOfWork` a `409 support.legal_documents.version_taken`. Siempre crea versión, aunque el HTML no cambie (como `PublishLegalTexts`). | Mismo orden que `PublishLegalTexts.cs:72-112`; el saneador corre después de validar el tamaño crudo. |
| L7 | Pregunta por defecto: `SupportLegalNotices.DefaultAuthorizationQuestion(string tenantName)` = `«¿Autorizas a {tenantName} para el tratamiento de los datos personales suministrados en este formulario?»`. El portal la recibe ya resuelta; la consola la recibe en `DefaultAuthorizationQuestion`. Nombre del tenant: `SupportPublicResolver` (público) y `ITenantDirectory` (consola, como `GetFormSettings`). | D-1. |
| L8 | Texto fijo: `SupportLegalNotices.Characterization` con el texto exacto del spec §8 (sin «Entidad» cambiado). Viaja en `GET /form` y se copia en `characterization_text` de la fila `PrivacyNotice`. | Spec §8. |
| L9 | Ejemplos (D-4) en `SupportLegalDocumentExamples.For(kind, tenantId)` (versión 0, `IsExample`). Textos exactos en «Textos de ejemplo» al final; el abogado los revisa antes de producción. | El plan fija el texto para que no lo invente cada implementador. |
| L10 | Las entidades `SupportLegalTextVersion` y `SupportConsent` se quedan mapeadas como **sólo lectura**: se borran sus fábricas (`Create`, `Example`, `Record`), sus repositorios (`ISupportLegalTextRepository`, `ISupportConsentRepository` y sus implementaciones) y las traducciones de `IX_legal_text_versions_tenant_version` e `IX_consents_ticket` en `SupportUnitOfWork`. Sus `DbSet` siguen (los usa `SupportUserReferenceProbe`). Se borran `LegalTextsVersionTaken`, `LegalTextsPolicyRequired` y `PublicConsentDuplicate` de `SupportErrorCodes`, y `LegalTextsPublished` y `Resources.LegalTexts` de `SupportAuditActions`. | Spec §5.4: sin escritor. Quitar la entidad del modelo generaría `DropTable`. |
| L11 | `Down` de `AddSupportLegalDocuments`: borra los índices de las dos tablas nuevas y **no** las tablas, como `AddSupportPublicForm`. | Prueba legal (precedente `customers.data_consents`). |
| L12 | **Relleno en SQL dentro de `Up`**, por tenant, desde su `legal_text_versions` de mayor `version`. Conversión de un texto plano: `\r\n` → `\n`; escapar **sólo** `&`, `<`, `>` (en ese orden); partir por `\n` seguido de líneas en blanco (`\n\s*\n`); recortar cada trozo y descartar los vacíos; dentro de un trozo, `\n` → `<br>`; envolver en `<p>…</p>` y concatenar sin separador. `PrivacyNotice` v1 = conversión de `authorization_text` seguida de la de `policy_text` si no es `NULL`; `policy_url` copiada; `authorization_question` `NULL`. `Terms` v1 sólo si `terms_text` no es `NULL`. `id` = `gen_random_uuid()`, `created_at`/`created_by` de la versión de origen. Sin `AnonymousNotice`. | Spec §11. AngleSharp serializa el texto escapando sólo `&`, `<`, `>` (y `&nbsp;`): con esas tres el resultado ya es la forma canónica del saneador, y `BackfilledHtmlIsAlreadyCanonical` lo amarra. Una comilla escapada (`&quot;`) no sería canónica: el spec §11 decía escaparla y se corrige (discrepancia 2). |
| L13 | Foto de la aceptación: `SupportLegalAcceptance.Record(Guid id, Guid tenantId, SupportTicketId ticketId, SupportLegalDocumentVersion version, string servedHtml, string? authorizationQuestion, string? characterizationText, string? visitorIp, DateTimeOffset now)`. Exige versión publicada (≥ 1, no ejemplo) del mismo tenant; `PrivacyNotice` exige IP, pregunta y caracterización; `AnonymousNotice` rechaza IP; `Terms` acepta las dos. `policy_url` se copia de la versión. | Spec §5.2. El anonimato vive en el ticket: que un `Terms` anónimo no lleve IP lo garantiza el handler y lo prueba `AnAnonymousTicketStoresTheNoticeAndTermsWithoutIp`. |
| L14 | `FilePublicTicketHandler`: después de comparar `FormVersion` (`FilePublicTicket.cs:237`), lee las tres vigentes (`ISupportLegalDocumentRepository.CurrentAllAsync(tenantId)`); si falta alguna, `SupportPublicResolver.NotFound()`; compara cada versión que aplica y ante cualquier diferencia `422 support.public.legal_texts_outdated`. Dentro de la transacción, en lugar de `consents.Add`, agrega las dos filas del camino (spec §5.2) con el HTML re-saneado. La auditoría del alta suma `legalAcceptances` a `CreatedFields`. | Todo lo que responde 422 sigue antes de la transacción (no gasta radicado). |
| L15 | `AcceptsTerms` falso: `RuleFor(AcceptsTerms).Equal(true).WithErrorCode(SupportErrorCodes.PublicTermsRequired)`, igual que `AcceptsDataPolicy`. **Por HTTP los dos salen como `422 validation.failed` con `errors.AcceptsTerms` / `errors.AcceptsDataPolicy`**: `ApiExceptionHandler` (`:70-76, :185`) no publica el `ErrorCode` de FluentValidation. Las claves de `errors` llegan en PascalCase (el `PropertyName`). | Así responde hoy `consent_required`; el spec §9-§10 suponía otra cosa y se corrige (discrepancia 1). |
| L16 | Versiones del radicado: con datos, `PrivacyNoticeVersion` y `TermsVersion` obligatorias y `AnonymousNoticeVersion` tiene que ser `null`; anónimo, `AnonymousNoticeVersion` y `TermsVersion` obligatorias y `PrivacyNoticeVersion` `null`. `AcceptsDataPolicy` se ignora en el anónimo, como hoy. | Spec §9. |
| L17 | `GET /form` arma `Legal` re-saneando las tres vigentes; si falta alguna, el mismo `404 support.public.not_found` (`GetPublicForm.cs:28`). | Spec §6.3, §9. |
| L18 | Bloqueadores del formulario: `SupportFormSettings.BlockersAsync` devuelve `privacy_notice_required`, `anonymous_notice_required`, `terms_required` (en ese orden) además de `no_published_types`; `ErrorCodeOf` mapea los tres a `support.form.legal_texts_required`. | Spec §7. |
| L19 | Detalle del ticket: `SupportTicketDtoComposer` lee `ISupportLegalAcceptanceRepository.ListByTicketAsync(tenantId, ticketId)` (sin contenido: proyección de `kind`, `document_version`, `accepted_at`, `visitor_ip`) y arma `LegalAcceptances` en el orden del enum. | Spec §7: el contenido no viaja en cada detalle. |
| L20 | Foto: `GET …/tickets/{ticketId}/legal-acceptances/{kind}` con `SupportAuthorization.EnsureCanViewAsync`; ticket de otro tenant o inexistente `404 support.ticket.not_found`; sin fila `404 support.legal_acceptance.not_found`. No se audita. | Spec §7, §12. |
| L21 | Auditoría de publicar: `SupportAuditActions.LegalDocumentPublished = "support.legal_document.published"`, `Resources.LegalDocument = "legal_document"`, id del recurso = id de la versión, `changedFields` = `["kind", "contentHtml"]` más `authorizationQuestion` y `policyUrl` en `PrivacyNotice`. | Spec §12. |
| L22 | `legal_document_versions.created_by` entra en `SupportUserReferenceProbe` (y en su comentario); `legal_text_versions.created_by` se queda. | Gotcha del repo. |
| L23 | Origen de desarrollo del portal: `http://localhost:4322`. `appsettings.Development.json` cambia `Support:PublicForm:AllowedOrigins` de `["http://localhost:3002"]` a `["http://localhost:4322"]`. El repo del portal fija ese puerto con `--port 4322` y `strictPort` (el storefront usa 4321). | Spec §12: 3002 es la consola y ya no llama al grupo público. Un valor de configuración, sin código. |
| L24 | `appsettings.example.json` documenta `Support:PublicForm:{BaseUrl,Hostname,AllowedOrigins}` y `Support:Workers:{BatchSize,FeedLookbackHours,FollowIntervalSeconds}`. | Pone en verde `ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample`, en rojo desde la fase 3 (`docs/estado.md`, «Defectos conocidos»), y deja documentada la clave que esta rama cambia. |
| L25 | El alias estable de `develop` del portal en Vercel (D-5) **no** entra en código ni en `appsettings*.json`: el proyecto de Vercel todavía no existe. El README dice dónde ponerlo (`Support__PublicForm__AllowedOrigins__N` del backend de desarrollo y CORS del bucket de desarrollo) cuando exista. | No se inventa un origen. |
| L26 | Datos que no se omiten (D-6): `SupportPublicFiledDto` suma `ReceivedAt` (`SupportTicket.ReceivedAt`), `TermBusinessDays` y `TermIsLegal` (la foto del tipo en el ticket: `SupportTicket.TermBusinessDays`, `SupportTicket.cs:41`, y su `TermIsLegal`); `SupportPublicTicketDto` suma `TermBusinessDays` de la misma foto (`LookupPublicTicket.cs:~103`). Nunca del tipo vivo: editar el tipo después no cambia lo que ve el solicitante. **El NIT no entra** (D-6 pendiente: no existe en `Tenant`). | Spec §9.1. |
| L27 | Idempotencia (D-7), calcada de `SubmitOrderRequest.cs:227-232` y `KeyedOrderCommit`: columna `support.tickets.client_request_id uuid NULL`, índice único parcial `IX_tickets_tenant_client_request (tenant_id, client_request_id) WHERE client_request_id IS NOT NULL`, migración aparte `AddSupportTicketClientRequestId` (aditiva, sin relleno). `SupportTicket.CreateFromPublicForm` recibe `Guid? clientRequestId`. Orden del handler: resolver → si hay clave, `ISupportTicketRepository.FindByClientRequestAsync(tenantId, key)` → si existe, respuesta de reintento (L28) **antes** de Turnstile, validador y versiones. Sin ticket: el flujo de siempre. Carrera: dentro de la transacción, **después** de `numbers.NextAsync` (el `UPDATE … RETURNING` de `SupportTicketNumberGenerator` serializa las altas del tenant), se vuelve a buscar la clave; si la confirmó otro, se deshace la transacción (el número vuelve con el rollback) y se responde con el ganador. Respaldo: `SupportUnitOfWork` traduce `IX_tickets_tenant_client_request` **por nombre** a `SupportClientRequestTakenException` (Application) y el handler la atrapa igual. El cuerpo no se compara (como la tienda). `ClientRequestId == Guid.Empty` → `validation.failed`. La clave no se audita ni va a `ticket_events`. | Spec §9.2; el doble chequeo bajo el candado del contador hace la carrera determinista sin depender del 23505. |
| L28 | PIN del reintento (D-7): con `ticket_access.last_sent_channel = 'Email'` y `last_failure_code IS NULL` → `Pin = null`, `PinEmailed = true`, nada se escribe. Si no (anónimo, sin canal, `send_failed`, `send_pending`), `SupportTicketAccess.Reissue(hash nuevo, now)` en una transacción corta y `Pin` = el nuevo, `PinEmailed = false`; no se manda correo ni texto. El reintento no pasa por `SupportOpeningDelivery`. | Spec §15 D-7. |

## Review Focus

1. **XSS por el portal:** publicar `<img src=x onerror=alert(1)>`, `<a href="java&#x09;script:alert(1)">x</a>`, `<svg><script>…`, un `style` y un `on*`, y leer la columna y `GET /form`: nada ejecutable sobrevive, y una fila escrita a mano en SQL con `<script>` sale saneada por `GET /form`. Tarea 1: `SupportHtmlSanitizerTests` completa; Tarea 4: `TheFormResanitizesWhatItServes`.
2. **Idempotencia:** sanear dos veces da los mismos bytes, también para la salida del relleno. Tarea 1: `SanitizingTwiceGivesTheSameBytes`, `BackfilledHtmlIsAlreadyCanonical`.
3. **Anónimo sin IP:** un radicado anónimo deja `AnonymousNotice` y `Terms` con `visitor_ip IS NULL`, ninguna fila `PrivacyNotice`, y nada en `consents`. Tarea 5: `AnAnonymousTicketStoresTheNoticeAndTermsWithoutIp`.
4. **La foto sobrevive:** radicar, publicar otra versión de cada documento, leer la foto por la consola: es el texto viejo. Tarea 5: `TheAcceptanceKeepsTheTextTheRequesterSaw`; Tarea 6: `TheSnapshotIsTheServedText`.
5. **La migración no borra nada:** el `Up` no tiene `DropTable`/`DropColumn`; `legal_text_versions` y `consents` siguen con sus filas; el `Down` deja las tablas nuevas. Tarea 2: `TheMigrationDownKeepsTheLegalDocumentTables`, `TheLegacyTablesAreStillThere`.
6. **Un reintento nunca crea un segundo ticket ni gasta un número:** dos `POST` en paralelo con la misma clave → una fila, los dos `201` con el mismo número, y el siguiente radicado sin clave toma el número siguiente sin hueco. Tarea 9: `TwoConcurrentFilingsWithTheSameKeyCreateOneTicket`, `ARetryWithTheSameClientRequestIdReturnsTheSameTicket`.
7. **El reintento no reenvía ni filtra el PIN:** con el correo ya enviado, `pin` es `null` y no sale otro correo; sin canal, el PIN viejo deja de servir para consultar y el nuevo sí. La clave de otro tenant no encuentra nada. Tarea 9: `ARetryAfterTheEmailWentOutReturnsNoPin`, `ARetryOfAnAnonymousTicketRotatesThePin`, `TheSameKeyInAnotherTenantIsAnotherTicket`.

---

## Contrato para las pantallas (BFF)

### Consola (`qep-frontend`), bajo `/api/v1/tenants/{tenantId:guid}/support`

| Método y ruta | Política / permiso | Respuesta |
| --- | --- | --- |
| `GET /settings/legal-documents` | `support.settings.view` | `200 SupportLegalDocumentsDto` |
| `GET /settings/legal-documents/{kind}/versions` | `support.settings.view` | `200 SupportLegalDocumentHistoryDto` |
| `GET /settings/legal-documents/{kind}/versions/{version:int}` | `support.settings.view` | `200 SupportLegalDocumentDto` |
| `POST /settings/legal-documents/{kind}` (`If-Match: <version vigente>`, `0` si no hay) | `support.settings.manage` | `201 SupportLegalDocumentDto`, con `ETag` |
| `GET /tickets/{ticketId:guid}/legal-acceptances/{kind}` | `support.ticket.view` | `200 SupportLegalAcceptanceSnapshotDto` |
| `GET /tickets/{ticketId:guid}` (existente) | `support.ticket.view` | `SupportTicketDetailDto` + `legalAcceptances` |
| `GET` · `PUT /settings/public-form` (existentes) | sin cambio | `SupportFormSettingsDto` con los bloqueadores nuevos |

`{kind}` ∈ `privacy-notice`, `anonymous-notice`, `terms`. En los cuerpos viaja como `PrivacyNotice`, `AnonymousNotice`, `Terms`.
**Se eliminan** `GET` y `POST /settings/legal-texts` y `SupportLegalTextsDto`.

```csharp
public sealed record SupportLegalDocumentsDto(
    SupportLegalDocumentDto PrivacyNotice, SupportLegalDocumentDto AnonymousNotice, SupportLegalDocumentDto Terms);

// La vigente, una versión vieja o el ejemplo (Version 0, IsExample true, CreatedAt y CreatedBy null).
// AuthorizationQuestion, PolicyUrl y DefaultAuthorizationQuestion son null salvo en PrivacyNotice.
// DefaultAuthorizationQuestion: la pregunta que el portal muestra si AuthorizationQuestion es null.
public sealed record SupportLegalDocumentDto(
    string Kind, int Version, bool IsExample, string ContentHtml,
    string? AuthorizationQuestion, string? PolicyUrl, string? DefaultAuthorizationQuestion,
    DateTimeOffset? CreatedAt, SupportMemberDto? CreatedBy);

public sealed record SupportLegalDocumentHistoryDto(string Kind, IReadOnlyList<SupportLegalDocumentVersionItemDto> Versions); // nueva → vieja; [] sin publicadas
public sealed record SupportLegalDocumentVersionItemDto(int Version, DateTimeOffset CreatedAt, SupportMemberDto CreatedBy);

// AuthorizationQuestion y PolicyUrl sólo con privacy-notice; vacíos o null = sin valor.
public sealed record PublishLegalDocumentRequest(string? ContentHtml, string? AuthorizationQuestion, string? PolicyUrl);

public sealed record SupportLegalAcceptanceDto(string Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp);

public sealed record SupportLegalAcceptanceSnapshotDto(
    string Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp, string ContentHtml,
    string? AuthorizationQuestion, string? PolicyUrl, string? CharacterizationText);

// SupportTicketDetailDto suma al final:
//     IReadOnlyList<SupportLegalAcceptanceDto> LegalAcceptances   // orden PrivacyNotice, AnonymousNotice, Terms; [] si no hay
```

- `ContentHtml` siempre llega **saneado** (lista blanca del spec §6.2). La respuesta del `POST` es lo que quedó guardado: el editor se repinta con ella.
- `SupportLegalAcceptanceDto.AcceptedAt` en `AnonymousNotice` significa «continuó después de verlo». `VisitorIp` es `null` en las dos filas de un anónimo.
- `SupportFormSettingsDto.PublishBlockers` ⊆ `privacy_notice_required`, `anonymous_notice_required`, `terms_required`, `no_published_types` (sale `legal_texts_required`).

Errores de la consola:

| Estado y código | Cuándo |
| --- | --- |
| `422 validation.failed` | `errors.ContentHtml` (vacío o más de 120 000 crudo), `errors.AuthorizationQuestion` (más de 500 o con otro tipo), `errors.PolicyUrl` (no https absoluta ≤ 500, o con otro tipo) |
| `422 support.legal_documents.content_empty` | después de sanear no queda texto visible |
| `422 support.legal_documents.content_too_long` | después de sanear pasa de 60 000 |
| `428 precondition.if_match_required` (el de `RequireVersion` hoy) | `POST` sin `If-Match` |
| `412 concurrency.conflict` | `If-Match` distinto de la versión vigente del tipo |
| `409 support.legal_documents.version_taken` | dos publicaciones simultáneas del mismo tipo |
| `404 support.legal_documents.kind_not_found` | `{kind}` desconocido |
| `404 support.legal_documents.version_not_found` | `…/versions/{version}` que no existe |
| `404 support.ticket.not_found` | foto de un ticket que no es del tenant |
| `404 support.legal_acceptance.not_found` | el ticket no tiene aceptación de ese tipo |
| `422 support.form.legal_texts_required` | `PUT /settings/public-form` publicando con un documento sin versión (sin cambio de código) |
| `403` | sin permiso o tenant ajeno, como el resto del módulo |

### Portal (`qep-frontend-support`), bajo `/api/v1/public/support/{tenantSlug}`

Anónimo, sin credenciales (CORS `support-public`: `GET` y `POST`, sólo `Content-Type`), `Cache-Control: no-store`, cuerpo ≤ 64 KB, limitadores por IP sin cambio. **Toda llamada sale del navegador**, nunca del render del servidor. Rutas sin cambio salvo lo de la tabla: `GET /consult`, geografía, adjuntos y `POST /tickets/resend-pin` siguen como en la fase 3.

| Método y ruta | Cambio |
| --- | --- |
| `GET /form` | `legal` cambia de forma |
| `POST /tickets` | cuerpo: sale `legalTextVersion`; entran `privacyNoticeVersion`, `anonymousNoticeVersion`, `acceptsTerms`, `termsVersion` y `clientRequestId` (opcional, idempotencia). Respuesta: suma `receivedAt`, `termBusinessDays`, `termIsLegal`; `pin` pasa a anulable |
| `POST /tickets/lookup` | respuesta: suma `termBusinessDays` |

```csharp
// SupportPublicFormDto.Legal (los demás campos de SupportPublicFormDto no cambian):
public sealed record SupportPublicLegalDto(
    SupportPublicPrivacyNoticeDto PrivacyNotice,
    SupportPublicLegalDocumentDto AnonymousNotice,
    SupportPublicLegalDocumentDto Terms,
    string CharacterizationNotice);                 // texto plano fijo, igual para todos los tenants

// AuthorizationQuestion ya resuelta: la del tenant o «¿Autorizas a {nombre del tenant} para el tratamiento de los datos
// personales suministrados en este formulario?». PolicyUrl null = el pie no muestra «Política de tratamiento de datos».
public sealed record SupportPublicPrivacyNoticeDto(int Version, string ContentHtml, string AuthorizationQuestion, string? PolicyUrl);
public sealed record SupportPublicLegalDocumentDto(int Version, string ContentHtml);

public sealed record SupportPublicFileRequest(
    Guid? TicketTypeId, string? Subject, string? Description, bool Anonymous,
    SupportPublicRequesterRequest? Requester,
    bool AcceptsDataPolicy, int? PrivacyNoticeVersion,
    int? AnonymousNoticeVersion,
    bool AcceptsTerms, int? TermsVersion,
    int? FormVersion, string? TurnstileToken,
    IReadOnlyList<SupportPublicAttachmentRef>? Attachments = null,
    Guid? ClientRequestId = null);               // opcional; un UUID aleatorio por intento de radicar (no por clic)

// Respuesta de POST /tickets (201), primera vez y reintento. Pin null sólo en un reintento cuyo PIN ya salió por correo
// (L28). TermBusinessDays null = el tipo no tiene término; TermIsLegal decide «plazo legal» u «objetivo».
public sealed record SupportPublicFiledDto(
    string Number, string? Pin, DateTimeOffset ReceivedAt, DateOnly? DueOn, int? TermBusinessDays, bool TermIsLegal,
    string? SuccessMessage, bool PinEmailed, string ConsultUrl);

// Respuesta de POST /tickets/lookup: suma TermBusinessDays a lo de la fase 3.
public sealed record SupportPublicTicketDto(
    string Number, string TypeName, string Status, DateTimeOffset ReceivedAt, DateOnly? DueOn, string? DueState,
    bool TermIsLegal, int? TermBusinessDays, IReadOnlyList<SupportPublicReplyDto> Replies);
```

En JSON: `legal.privacyNotice.{version, contentHtml, authorizationQuestion, policyUrl}`, `legal.anonymousNotice.{version, contentHtml}`, `legal.terms.{version, contentHtml}`, `legal.characterizationNotice`; cuerpo del radicado `{ …, anonymous, requester, acceptsDataPolicy, privacyNoticeVersion, anonymousNoticeVersion, acceptsTerms, termsVersion, formVersion, turnstileToken, attachments, clientRequestId }`; respuesta del radicado `{ number, pin, receivedAt, dueOn, termBusinessDays, termIsLegal, successMessage, pinEmailed, consultUrl }`; respuesta de la consulta `{ number, typeName, status, receivedAt, dueOn, dueState, termIsLegal, termBusinessDays, replies }`.

**El NIT del tenant no está en este contrato** (spec §15, D-6): el tenant no tiene NIT en el modelo y la fuente queda pendiente del owner. Cuando se decida, entra como un campo nuevo de `GET /form` y `GET /consult`.

**Radicado idempotente** (`clientRequestId`, spec §9.2, L26–L28):

- El portal genera un UUID al empezar a radicar y lo manda en **cada** reintento de ese mismo radicado (timeout, red caída, 5xx). Uno nuevo sólo para otro radicado («Radicar otra solicitud»).
- Un reintento con la misma clave en el mismo tenant responde `201` con el **mismo** `number`, `receivedAt`, `dueOn`, `termBusinessDays`, `termIsLegal`, `successMessage` y `consultUrl`, aunque el cuerpo cambie, el token de Turnstile ya esté gastado o el formulario o los documentos hayan cambiado: la clave se mira antes que todo eso. No se crea otro ticket ni se gasta otro número, y el correo de apertura no se vuelve a mandar.
- `pin` en un reintento: `null` con `pinEmailed: true` si el PIN ya salió por correo (el portal dice que se lo enviamos al correo y ofrece la consulta); un PIN **nuevo** con `pinEmailed: false` si no salió por ningún canal (anónimo o correo fallido): el anterior deja de servir y el portal lo muestra como en la primera respuesta.
- Sin `clientRequestId`, cada `POST` es un radicado nuevo, como en la fase 3. El reintento sigue contando para el limitador `SupportPublicSubmit` (5 cada 10 min por IP).

Qué manda cada camino del portal (pasos del tablero aprobado):

| Campo | Con datos (pasos 1 → 2 → 3 → 4) | Anónimo (pasos 1 → 3 → 4) |
| --- | --- | --- |
| `anonymous` | `false` | `true` |
| `requester` | obligatorio | `null` |
| `acceptsDataPolicy` | `true` («Sí acepto» del paso 1) | se ignora |
| `privacyNoticeVersion` | `legal.privacyNotice.version` | `null` |
| `anonymousNoticeVersion` | `null` | `legal.anonymousNotice.version` |
| `acceptsTerms` | `true` («Acepto» del paso 3) | `true` |
| `termsVersion` | `legal.terms.version` | `legal.terms.version` |

- `contentHtml` llega saneado y se pinta como HTML (`dangerouslySetInnerHTML`). El portal no lo vuelve a sanear ni lo transforma.
- `characterizationNotice` va al pie del paso 2 (sólo en el camino con datos).
- `GET /form` responde `404 support.public.not_found` si al tenant le falta cualquiera de los tres documentos (igual que con el formulario sin publicar).

Errores nuevos o cambiados de `POST /tickets`:

| Estado y código | Cuándo | Qué hace el portal |
| --- | --- | --- |
| `422 validation.failed`, `errors.AcceptsDataPolicy` | con datos y `acceptsDataPolicy` falso | no debería pasar: el paso 1 no deja seguir |
| `422 validation.failed`, `errors.AcceptsTerms` | `acceptsTerms` falso | no debería pasar: el paso 3 no deja seguir |
| `422 validation.failed`, `errors.PrivacyNoticeVersion` / `errors.AnonymousNoticeVersion` / `errors.TermsVersion` | falta la que aplica o viene la que no aplica | bug del portal |
| `422 validation.failed`, `errors.ClientRequestId` | `clientRequestId` es el UUID vacío | bug del portal |
| `422 support.public.legal_texts_outdated` | alguna versión que aplica ya no es la vigente | recargar `GET /form`, volver al paso del documento cambiado y pedir de nuevo la aceptación; reiniciar Turnstile |
| `422 support.public.form_outdated`, `captcha_invalid`, `attachment_invalid`, `attachments_disabled`, `404 not_found`, `413`, `429` | sin cambio | como en la fase 3 |

Las claves de `errors` llegan en PascalCase (el `PropertyName` de FluentValidation, como en la fase 3: `Requester.CityId`). Turnstile se gasta antes de validar: después de **cualquier** `422` el portal reinicia el widget (README, fase 3).

## Estructura de archivos

```
src/Modules/Support/
  Domain/         SupportLegalDocumentKind (+SupportLegalDocumentKinds), SupportLegalDocumentVersion, SupportLegalAcceptance,
                  SupportLegalNotices, SupportLegalDocumentExamples                                         (nuevos)
                  SupportLegalTextVersion, SupportConsent (sólo lectura, L10), SupportErrorCodes (+/−)
  Application/    ISupportHtmlSanitizer (+SanitizedHtml), ISupportLegalDocumentRepository, ISupportLegalAcceptanceRepository (nuevos);
                  Settings/ PublishLegalDocument, GetLegalDocuments, GetLegalDocumentHistory, GetLegalDocumentVersion (nuevos),
                  GetLegalTexts + PublishLegalTexts (borrados), GetFormSettings (L18);
                  Tickets/ GetLegalAcceptanceSnapshot (nuevo); SupportTicketDtoComposer (L19);
                  Public/ GetPublicForm (L17), FilePublicTicket (L14–L16, L27–L28), LookupPublicTicket (L26), SupportPublicDtos;
                  ISupportTicketRepository (+FindByClientRequestAsync), SupportClientRequestTakenException (nuevo);
                  SupportDtos, SupportAuditActions; ISupportLegalTextRepository + ISupportConsentRepository (borrados)
  Infrastructure/ Legal/HtmlSupportSanitizer (nuevo); Persistence/ SupportLegalDocumentRepositories (nuevo),
                  SupportPublicFormRepositories (quita los dos repositorios viejos), SupportDbContext, SupportUnitOfWork,
                  Migrations/<ts>_AddSupportLegalDocuments, <ts>_AddSupportTicketClientRequestId; SupportTicketRepository (L27);
                  SupportInfrastructureExtensions; csproj (+HtmlSanitizer); packages.lock.json
  Domain/         SupportTicket (+ClientRequestId, L27)
  Api/            SupportEndpoints (rutas nuevas, quita legal-texts)
src/Bootstrapper/ SupportUserReferenceProbe (L22), QepServiceCollectionExtensions (handlers y validadores a mano)
src/Api/          appsettings.Development.json (L23), appsettings.example.json (L24)
Directory.Packages.props (+HtmlSanitizer); los packages.lock.json que cambien
README.md (§ Formulario público de Soporte), CLAUDE.md (gotchas)
```

---

## Bloque A — Saneador, modelo y consola (Tareas 1–3, un implementador)

### Task 1: Saneador HTML y el paquete

**Files:**
- Create: `Modules.Support.Application/ISupportHtmlSanitizer.cs`; `$INF/Legal/HtmlSupportSanitizer.cs`
- Modify: `Directory.Packages.props`, `$INF/Modules.Support.Infrastructure.csproj`, `SupportInfrastructureExtensions.cs` (singleton), los `packages.lock.json` que cambien
- Test: `$SU\SupportHtmlSanitizerTests.cs` (`$SU` hoy referencia sólo Domain y Application: se le suma `ProjectReference` a `Modules.Support.Infrastructure` y se regenera su `packages.lock.json`), `$AT\SupportLayerTests.cs` (+1)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SanitizedHtml(string Html, bool HasVisibleText);
  public interface ISupportHtmlSanitizer { SanitizedHtml Sanitize(string html); }
  ```

- [ ] **Paso 1: pruebas**
  - `SupportHtmlSanitizerTests` (contra `HtmlSupportSanitizer` real):
    - `KeepsEveryAllowedElement` — un documento con cada elemento de la lista blanca sale igual.
    - `DropsDangerousElementsWithTheirContent` — `script`, `style`, `iframe`, `object`, `embed`, `svg`, `math`, `template`, `noscript`, `form`, `input`, `img`, `video`, `audio`: ni la etiqueta ni su texto.
    - `UnwrapsUnknownElementsKeepingTheirText` — `<span>`, `<div>`, `<b>`, `<i>`, `<font>`.
    - `DropsAttributesOtherThanHref` — `style`, `class`, `id`, `data-x`, `onclick`, `onerror`.
    - `KeepsOnlyHttpsMailtoAndTelLinks` — `[Theory]`: `javascript:`, `JaVaScRiPt:`, `java&#x09;script:`, `" javascript:"`, `\u0001javascript:`, `data:text/html,…`, `vbscript:`, `http://x`, `/relativa`, `#ancla` → sin `href`; `https://x`, `mailto:a@b.co`, `tel:+573001234567` → conservado.
    - `LinksGetRelAndTarget` — `rel="noopener noreferrer nofollow"` y `target="_blank"`, también si el original traía otros.
    - `ResistsMutationXss` — `<noscript><p title="</noscript><img src=x onerror=alert(1)>">`, `<svg><style><img src=x onerror=alert(1)></style></svg>`, `<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>`: la salida no contiene `onerror` ni `<img`.
    - `SanitizingTwiceGivesTheSameBytes` — `[Theory]` sobre las salidas de todas las anteriores.
    - `HasVisibleTextIsFalseForEmptyMarkup` — `<p></p>`, `<p>&nbsp;</p>`, `<br>`, `<script>x</script>` → `false`; `<p>a</p>` → `true`.
    - `BackfilledHtmlIsAlreadyCanonical` — `[Theory]` con la salida esperada de L12 para textos con `&`, `<`, `>`, comillas, `\r\n`, una línea en blanco y dos seguidas: `Sanitize(x).Html == x`.
  - `SupportLayerTests.ApplicationDoesNotReferenceTheHtmlSanitizer` — ni `Ganss.Xss` ni `AngleSharp` en Domain ni Application.
- [ ] **Paso 2: RED** — `dotnet build $SU` falla por `HtmlSupportSanitizer` inexistente (`CS0246`). Sin contenedores.
- [ ] **Paso 3: implementar** — paquete según L4 (versión exacta, `--vulnerable` limpio, `restore --force-evaluate`, `restore --locked-mode`); saneador según L2–L3.
- [ ] **Paso 4: GREEN** — `dotnet test $SU --filter "FullyQualifiedName~SupportHtmlSanitizerTests"`; `dotnet test $AT --filter "FullyQualifiedName~SupportLayerTests"`; `dotnet restore --locked-mode` desde `$W` limpio.
- [ ] **Paso 5: commit** — `feat(support): sanitize legal document html with an allowlist`.

### Task 2: Dominio, persistencia, migración y relleno

**Files:**
- Create: `Modules.Support.Domain/SupportLegalDocumentKind.cs`, `SupportLegalDocumentVersion.cs`, `SupportLegalAcceptance.cs`, `SupportLegalNotices.cs`, `SupportLegalDocumentExamples.cs`; `Modules.Support.Application/ISupportLegalDocumentRepository.cs`, `ISupportLegalAcceptanceRepository.cs`; `$INF/Persistence/SupportLegalDocumentRepositories.cs`; migración `AddSupportLegalDocuments`
- Modify: `SupportDbContext.cs` (dos entidades nuevas), `SupportUnitOfWork.cs` (dos índices nuevos por nombre), `SupportInfrastructureExtensions.cs`, `SupportErrorCodes.cs` (+`LegalDocumentsContentEmpty`, `LegalDocumentsContentTooLong`, `LegalDocumentsKindNotFound`, `LegalDocumentsVersionNotFound`, `LegalDocumentsVersionTaken`, `LegalAcceptanceNotFound`, `PublicTermsRequired`, `PublicLegalAcceptanceDuplicate`)
- Test: `$SU\SupportLegalDocumentVersionTests.cs`, `$SU\SupportLegalAcceptanceTests.cs`, `$SI\SupportLegalDocumentsPersistenceTests.cs`, `$SI\SupportLegalDocumentsBackfillTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum SupportLegalDocumentKind { PrivacyNotice, AnonymousNotice, Terms }
  public static class SupportLegalDocumentKinds {
      public static bool TryParseRoute(string? segment, out SupportLegalDocumentKind kind);   // privacy-notice | anonymous-notice | terms
      public static string ToRoute(SupportLegalDocumentKind kind);
  }
  public sealed class SupportLegalDocumentVersion {                // insert-only
      public const int ContentMaxLength = 60_000, RawContentMaxLength = 120_000, AuthorizationQuestionMaxLength = 500, PolicyUrlMaxLength = 500;
      public static SupportLegalDocumentVersion Create(Guid id, Guid tenantId, SupportLegalDocumentKind kind, int version,
          SanitizedHtml content, string? authorizationQuestion, string? policyUrl, Guid createdBy, DateTimeOffset now);
          // sin texto visible: LegalDocumentsContentEmpty; > ContentMaxLength: LegalDocumentsContentTooLong;
          // pregunta o URL con otro tipo: ArgumentException; URL no https: ArgumentException
      public static bool IsValidPolicyUrl(string? value);         // movida desde SupportLegalTextVersion
      Guid Id; Guid TenantId; SupportLegalDocumentKind Kind; int Version; string ContentHtml; string? AuthorizationQuestion;
      string? PolicyUrl; DateTimeOffset CreatedAt; Guid CreatedBy; bool IsExample;   // IsExample ignorado por EF
  }
  public static class SupportLegalDocumentExamples { public static SupportLegalDocumentVersion For(SupportLegalDocumentKind kind, Guid tenantId); } // versión 0
  public static class SupportLegalNotices {
      public const string Characterization = "Los datos solicitados en este formulario serán tratados para caracterizar a los usuarios de la Entidad y procurar una mejora continua en la atención.";
      public static string DefaultAuthorizationQuestion(string tenantName);
  }
  public sealed class SupportLegalAcceptance { public static SupportLegalAcceptance Record(…); }   // firma en L13
  public interface ISupportLegalDocumentRepository {
      void Add(SupportLegalDocumentVersion version);
      Task<SupportLegalDocumentVersion?> CurrentAsync(Guid tenantId, SupportLegalDocumentKind kind, CancellationToken ct);
      Task<IReadOnlyDictionary<SupportLegalDocumentKind, SupportLegalDocumentVersion>> CurrentAllAsync(Guid tenantId, CancellationToken ct);
      Task<SupportLegalDocumentVersion?> FindAsync(Guid tenantId, SupportLegalDocumentKind kind, int version, CancellationToken ct);
      Task<IReadOnlyList<SupportLegalDocumentVersion>> ListAsync(Guid tenantId, SupportLegalDocumentKind kind, CancellationToken ct); // nueva → vieja
  }
  public sealed record SupportLegalAcceptanceSummary(SupportLegalDocumentKind Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp);
  public interface ISupportLegalAcceptanceRepository {
      void Add(SupportLegalAcceptance acceptance);
      Task<IReadOnlyList<SupportLegalAcceptanceSummary>> ListByTicketAsync(Guid tenantId, SupportTicketId ticketId, CancellationToken ct);
      Task<SupportLegalAcceptance?> FindAsync(Guid tenantId, SupportTicketId ticketId, SupportLegalDocumentKind kind, CancellationToken ct);
  }
  ```
  Tablas y restricciones: spec §5.1 y §5.2 exactas (`CK_legal_document_versions_kind`, `CK_legal_document_versions_version`, `CK_legal_document_versions_privacy_only`, `IX_legal_document_versions_tenant_kind_version` único, `IX_legal_document_versions_created_by`; `CK_legal_acceptances_kind`, `CK_legal_acceptances_ip`, `IX_legal_acceptances_ticket_kind` único). `SupportUnitOfWork`: `IX_legal_document_versions_tenant_kind_version` → `409 LegalDocumentsVersionTaken`; `IX_legal_acceptances_ticket_kind` → `409 PublicLegalAcceptanceDuplicate`.

- [ ] **Paso 1: pruebas**
  - `SupportLegalDocumentVersionTests`: `VersionStartsAtOne`; `EmptyVisibleTextIsContentEmpty`; `MoreThan60000IsContentTooLong`; `QuestionAndUrlOnlyOnThePrivacyNotice`; `ThePolicyUrlMustBeHttps`; `TheQuestionIsTrimmedAndAtMost500`; `EachKindHasAnExampleAtVersionZero` (y su HTML pasa por el saneador sin cambiar: usa el real de la Tarea 1); `TheDefaultQuestionNamesTheTenant`; `RouteSegmentsRoundTrip` (y `"privacy_notice"`, `"PrivacyNotice"`, `""` no parsean).
  - `SupportLegalAcceptanceTests`: `AnExampleOrAnotherTenantsVersionIsRejected`; `APrivacyAcceptanceNeedsIpQuestionAndCharacterization`; `AnAnonymousNoticeAcceptanceRejectsAnIp`; `TermsTakeAnIpOrNone`; `ItKeepsTheServedHtmlAndCopiesThePolicyUrl`.
  - `SupportLegalDocumentsPersistenceTests` (`SharedSupportHost`): `TwoVersionsWithTheSameNumberAndKindAre409`; `TheSameNumberInAnotherKindIsFine`; `ACheckRejectsAQuestionOnTerms` (SQL crudo); `TheIpCheckRejectsAPrivacyRowWithoutIpAndAnAnonymousNoticeWithIp` (SQL crudo); `ASecondAcceptanceOfTheSameKindIs409`; `TheAcceptancesHaveNoForeignKey`; `TheMigrationDownKeepsTheLegalDocumentTables` (lee el `Down` del archivo de migración, como `TheMigrationDownKeepsTheLegalTables`); `TheLegacyTablesAreStillThere` (`legal_text_versions` y `consents` existen y el modelo de EF todavía las mapea).
  - `SupportLegalDocumentsBackfillTests` — base propia por clase (contenedor PostgreSQL propio, no el compartido): `MigrateAsync("AddSupportTicketNotes")` con `IMigrator`, sembrar por SQL `legal_text_versions` de tres tenants (v1 y v2 con y sin `policy_text`/`terms_text`, uno con `&`, `<` y `\r\n\r\n`), `MigrateAsync("AddSupportLegalDocuments")`, y: `TheCurrentLegacyVersionBecomesPrivacyNoticeOne` (HTML esperado de L12 exacto, `policy_url`, `created_by` y `created_at` de la v2); `TermsAreCopiedOnlyWhenTheyExisted`; `NoAnonymousNoticeIsCreated`; `ATenantWithoutLegacyTextsGetsNothing`.
- [ ] **Paso 2: RED** — `dotnet build $SU` y `$SI` fallan por `SupportLegalDocumentVersion`, `SupportLegalAcceptance` inexistentes. Sin contenedores.
- [ ] **Paso 3: implementar** — dominio y repositorios; mapeo en `SupportDbContext` (nombres de columna del spec, `kind` por nombre); `dotnet ef migrations add AddSupportLegalDocuments`; `Up`: revisar que sólo crea, y agregar el `INSERT … SELECT` de L12 con `migrationBuilder.Sql` (una sentencia para `PrivacyNotice` y otra para `Terms`, con `regexp_split_to_table` u otra forma equivalente, **siempre** con el resultado de L12); `Down` a mano según L11.
- [ ] **Paso 4: GREEN** — `dotnet test $SU --filter "FullyQualifiedName~SupportLegalDocument|FullyQualifiedName~SupportLegalAcceptance"`; cada prueba de integración nueva filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(support): model versioned legal documents and per-ticket acceptances`.

### Task 3: Consola — publicar, leer e historial; quitar `legal-texts`

**Files:**
- Create: `Modules.Support.Application/Settings/PublishLegalDocument.cs`, `GetLegalDocuments.cs`, `GetLegalDocumentHistory.cs`, `GetLegalDocumentVersion.cs`
- Modify: `SupportDtos.cs` (DTOs del contrato; quita `SupportLegalTextsDto`), `SupportAuditActions.cs` (L21; quita lo de L10), `Settings/GetFormSettings.cs` (L18), `Settings/UpdateFormSettings.cs` (si usa el repositorio viejo), `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `src/Bootstrapper/SupportUserReferenceProbe.cs` (L22)
- Delete: `Settings/GetLegalTexts.cs`, `Settings/PublishLegalTexts.cs`, `ISupportLegalTextRepository.cs`, `ISupportConsentRepository.cs`, sus implementaciones en `SupportPublicFormRepositories.cs`, las fábricas de `SupportLegalTextVersion` y `SupportConsent` (L10), `$SU\SupportLegalTextVersionTests.cs`
- Test: `$SU\PublishLegalDocumentValidatorTests.cs`, `$SU\FormSettingsValidatorTests.cs` (quita `LegalTextsLimits`), `$SI\SupportLegalDocumentsApiTests.cs`, `$SI\SupportFormSettingsApiTests.cs`, `$SI\SupportPublicFormPersistenceTests.cs`, `$SI\SupportUserReferenceProbeTests.cs`, `$SI\SupportPublicHarness.cs`

**Interfaces:**
- Consumes: Task 1 (`ISupportHtmlSanitizer`), Task 2 (repositorios, dominio).
- Produces: `PublishLegalDocumentCommand(Guid TenantId, string? Kind /* segmento */, string? ContentHtml, string? AuthorizationQuestion, string? PolicyUrl, long ExpectedVersion) : ICommand<SupportLegalDocumentDto>`; `GetLegalDocumentsQuery(Guid TenantId)`; `GetLegalDocumentHistoryQuery(Guid TenantId, string? Kind)`; `GetLegalDocumentVersionQuery(Guid TenantId, string? Kind, int Version)`. Rutas, DTOs y errores: «Contrato para las pantallas», Consola.

- [ ] **Paso 1: pruebas**
  - `PublishLegalDocumentValidatorTests`: `ContentIsRequiredAndAtMost120000Raw`; `TheQuestionIsOnlyForThePrivacyNotice` (con `terms` → error en `AuthorizationQuestion`); `ThePolicyUrlIsOnlyForThePrivacyNoticeAndHttps`; `TheQuestionIsAtMost500`; `TheExpectedVersionIsNotNegative`.
  - `SupportLegalDocumentsApiTests` (`SharedSupportHost`; cada prueba siembra su tenant):
    - `WithoutVersionsTheConsoleGetsTheThreeExamples` — `IsExample`, `Version 0`, `DefaultAuthorizationQuestion` con el nombre del tenant.
    - `PublishingCreatesTheNextVersionPerKind` — publicar `terms` dos veces y `privacy-notice` una: `2` y `1`; `GET` lista las vigentes.
    - `PublishingSetsTheETagAndReturnsTheStoredHtml`.
    - `TheStoredContentIsTheSanitizedOne` — publicar con `<script>` y `onclick`; la columna y la respuesta no los tienen.
    - `ContentWithoutVisibleTextIs422` (`support.legal_documents.content_empty`) y `ContentTooLongAfterSanitizingIs422`.
    - `AStaleIfMatchIs412`; `WithoutIfMatchIs428`; `AnUnknownKindIs404`.
    - `TheHistoryListsVersionsNewestFirst` y `AnOldVersionCanBeRead`; `AMissingVersionIs404`.
    - `AReaderCanSeeButNotPublish` — `CreateReader`: `GET 200`, `POST 403`; otro tenant → `403`.
    - `PublishingWritesAuditWithoutContent` — una entrada `support.legal_document.published`; su `changedFields` no contiene el HTML.
    - `TheOldLegalTextsRoutesAreGone` — `GET`/`POST …/settings/legal-texts` → `404`.
  - `SupportFormSettingsApiTests`: reescribir `WithoutRowsTheFormIsUnpublishedWithDefaultsAndBlockers` (los tres bloqueadores nuevos) y `PublishingWithoutLegalTextsIs422` → `PublishingWithAMissingDocumentIs422` (con dos publicados y el tercero no: `support.form.legal_texts_required` y su bloqueador); borrar `LegalTextsGetReturnsTheExampleUntilPublished`, `PublishingLegalTextsTwiceKeepsBothVersions` y `LegalTextsWithoutPolicyAre422` (cubiertas arriba).
  - `SupportPublicFormPersistenceTests`: conservar `TheConsentsTableHasNoForeignKey` y `TheMigrationDownKeepsTheLegalTables` (esquema); borrar `ASecondConsentForTheSameTicketIs409`, `TwoLegalTextVersionsWithTheSameNumberAre409` y `TheConsentKeepsItsOwnCopyOfTheText` (camino de escritura eliminado; sus equivalentes están en la Tarea 2 y la 5).
  - `SupportUserReferenceProbeTests.ALegalDocumentAuthorIsReferenced`; `AFormOrLegalTextAuthorIsReferenced` siembra el texto viejo por SQL crudo (ya no hay fábrica).
  - `SupportPublicHarness`: siembra las versiones 1 de los tres documentos con el repositorio nuevo (en vez de `SupportLegalTextVersion.Create`); `PublishLegalTextsAsync` → `PublishLegalDocumentAsync(factory, tenantId, kind, version, html)`.
- [ ] **Paso 2: RED** — `dotnet build $SU` y `$SI` fallan por `PublishLegalDocumentCommand` y DTOs inexistentes.
- [ ] **Paso 3: implementar** — handlers según L6–L7, L18, L21; endpoints con `RequireVersion(httpContext, "legal document", allowZero: true)`, `SetETag` y `Results.Created($"/api/v1/tenants/{tenantId}/support/settings/legal-documents/{kind}/versions/{version}", dto)`; registro a mano en `QepServiceCollectionExtensions`; borrar lo de L10; sonda L22.
- [ ] **Paso 4: GREEN** — cada prueba nueva o reescrita filtrada por nombre; `dotnet build` de la solución sin avisos.
- [ ] **Paso 5: commit** — `feat(support): publish the three legal documents from the console`.

**Cierre del Bloque A:** una corrida por clase: `SupportLegalDocumentsApiTests`, `SupportFormSettingsApiTests`, `SupportLegalDocumentsPersistenceTests`, `SupportLegalDocumentsBackfillTests`, `SupportPublicFormPersistenceTests`, `SupportUserReferenceProbeTests` en `$SI`, una a la vez; `SupportHtmlSanitizerTests`, `SupportLegalDocument*`, `SupportLegalAcceptanceTests`, `PublishLegalDocumentValidatorTests`, `FormSettingsValidatorTests` en `$SU`; `SupportLayerTests` en `$AT`.

**Revisión del Bloque A** (un revisor): L1–L12, L18, L21, L22; Review Focus 1, 2 y 5; que ningún camino escriba `content_html` sin el puerto; que el `Up` sólo cree y rellene; que el paquete esté fijado y el lock file al día.

## Bloque B — Portal, radicado, detalle y documentación (Tareas 4–7, un implementador)

### Task 4: `GET /form` con los tres documentos

**Files:**
- Modify: `Public/SupportPublicDtos.cs` (`SupportPublicLegalDto`, `SupportPublicPrivacyNoticeDto`, `SupportPublicLegalDocumentDto`), `Public/GetPublicForm.cs` (L17)
- Test: `$SI\SupportPublicFormApiTests.cs`

- [ ] **Paso 1: pruebas** — en `SupportPublicFormApiTests`: reescribir `TheLegalTextsAreTheCurrentVersion` → `TheFormServesTheThreeCurrentDocuments` (versiones y HTML vigentes de cada uno, después de publicar una v2 de `terms`); `TheCharacterizationNoticeIsTheFixedText`; `TheAuthorizationQuestionIsTheTenantsOrTheDefault` (sin pregunta → la por defecto con el nombre; con pregunta → la escrita); `ThePolicyUrlIsNullWhenNotSet`; `AFormMissingADocumentIs404` (borra por SQL la fila de `AnonymousNotice` sembrada → `404 support.public.not_found`); `TheFormResanitizesWhatItServes` (actualiza por SQL `content_html` de `terms` a `<p>a</p><script>alert(1)</script>` → la respuesta trae `<p>a</p>`).
- [ ] **Paso 2: RED** — `dotnet build $SI` falla por `SupportPublicPrivacyNoticeDto` inexistente.
- [ ] **Paso 3: implementar** — L7, L8, L17.
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(support): serve the three legal documents to the public portal`.

### Task 5: Radicar con las aceptaciones

**Files:**
- Modify: `Public/SupportPublicDtos.cs` (`SupportPublicFileRequest`), `Public/FilePublicTicket.cs` (L14–L16), `SupportErrorCodes.cs` (comentarios de `PublicConsentRequired` y `PublicTermsRequired`: por HTTP salen como `validation.failed`, L15)
- Test: `$SU\FilePublicTicketValidatorTests.cs`, `$SI\SupportPublicFilingApiTests.cs`, `$SI\SupportPublicHarness.cs` (cuerpos)

- [ ] **Paso 1: pruebas**
  - `FilePublicTicketValidatorTests`: reescribir `TheConsentMustBeAccepted` (sigue `["AcceptsDataPolicy"]`) y `TheTypeAndTheVersionsAreRequired` (con las versiones nuevas); nuevas `TheTermsMustBeAcceptedOnBothPaths` (`["AcceptsTerms"]`, y la regla lleva `PublicTermsRequired` como `ErrorCode`); `ANamedBodyNeedsPrivacyAndTermsVersionsAndNoAnonymousNotice`; `AnAnonymousBodyNeedsNoticeAndTermsVersionsAndNoPrivacyNotice`; `AnAnonymousBodyIgnoresAcceptsDataPolicy`.
  - `SupportPublicHarness`: `NaturalBody(...)` y `AnonymousBody(...)` mandan `privacyNoticeVersion`/`anonymousNoticeVersion`, `acceptsTerms = true`, `termsVersion` (parámetros con `1` por defecto); sin `legalTextVersion`.
  - `SupportPublicFilingApiTests`:
    - `ANamedTicketStoresPrivacyAndTermsWithTheVisitorIp` — dos filas, IP del visitante en las dos, `characterization_text` y `authorization_question` (la por defecto) en `PrivacyNotice`, ninguna fila nueva en `consents`.
    - `AnAnonymousTicketStoresTheNoticeAndTermsWithoutIp` (reemplaza `AnAnonymousTicketStoresNoRequesterNoIpAndNoConsent`, conservando sus aserciones sobre el ticket) — Review Focus 3.
    - `TheAcceptanceKeepsTheTextTheRequesterSaw` (reescrita) — radicar, publicar v2 de los tres, leer las filas: HTML y versión 1.
    - `EachOutdatedVersionIs422` — `[Theory]` `privacy`, `anonymous`, `terms` → `support.public.legal_texts_outdated`, y no se gastó radicado (el siguiente radicado sigue el número). Reemplaza `AnOutdatedLegalTextVersionIs422`.
    - `TermsNotAcceptedIs422WithTheField` — `validation.failed`, `errors.AcceptsTerms`.
    - `ARejectedFilingStoresNoAcceptance` — un `422` de cualquier tipo no deja filas en `legal_acceptances`.
- [ ] **Paso 2: RED** — `dotnet build $SU` falla por `FilePublicTicketCommand` sin `TermsVersion`.
- [ ] **Paso 3: implementar** — L13–L16; `FilePublicTicketCommand` y `From` con los campos nuevos.
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre; `dotnet test $SU --filter "FullyQualifiedName~FilePublicTicketValidatorTests"`.
- [ ] **Paso 5: commit** — `feat(support): record privacy, anonymous notice and terms acceptance per ticket`.

### Task 6: Aceptaciones en la consola

**Files:**
- Create: `Modules.Support.Application/Tickets/GetLegalAcceptanceSnapshot.cs`
- Modify: `SupportDtos.cs` (`SupportTicketDetailDto` + `LegalAcceptances`, `SupportLegalAcceptanceDto`, `SupportLegalAcceptanceSnapshotDto`), `SupportTicketDtoComposer` (L19), `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `$SI\SupportLegalAcceptancesApiTests.cs`

- [ ] **Paso 1: pruebas** — `SupportLegalAcceptancesApiTests`: `TheTicketDetailListsTheAcceptances` (radicado con datos → `PrivacyNotice` y `Terms` en ese orden, con IP; anónimo → `AnonymousNotice` y `Terms`, sin IP); `AManualTicketHasNoAcceptances` (`[]`, nunca `null`); `TheSnapshotIsTheServedText` (Review Focus 4: después de publicar v2, la foto sigue en v1); `TheSnapshotOfAMissingKindIs404` (`support.legal_acceptance.not_found`); `AnUnknownKindIs404`; `TheSnapshotIsOnlyForTheTicketsTenant` (otro tenant en la ruta → `403`; ticket de otro tenant con el tenant propio en la ruta → `404 support.ticket.not_found`); `AReaderCanReadTheSnapshot` (`CreateReader` → `200`).
- [ ] **Paso 2: RED** — `dotnet build $SI` falla por `SupportLegalAcceptanceSnapshotDto` inexistente.
- [ ] **Paso 3: implementar** — L19, L20.
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(support): show legal acceptances in the ticket detail`.

### Task 7: Configuración y documentación

**Files:**
- Modify: `src/Api/appsettings.Development.json` (L23), `src/Api/appsettings.example.json` (L24), `README.md` (§ «Formulario público de Soporte (fase 3)» y § Soporte), `CLAUDE.md` (gotchas)

- [ ] **Paso 1: prueba** — `ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample` (`$AT`, existe, en rojo desde la fase 3) es el RED: correrla filtrada y guardar la salida.
- [ ] **Paso 2: implementar**
  - L23 y L24.
  - README: tabla de rutas públicas (`GET /form` con los tres documentos y el texto de caracterización), párrafo «Radicar» (versiones y aceptaciones por camino, `acceptsTerms`, que `consent`/`terms` salen como `validation.failed`), rutas de la consola (sale `legal-texts`, entran `legal-documents` y la foto), «Prueba legal» (tablas nuevas insert-only y las viejas sin escritor), migración `AddSupportLegalDocuments` (aditiva, con relleno), configuración: el origen de desarrollo `http://localhost:4322`, que el portal es `qep-frontend-support` y que el alias estable de `develop` del portal se agrega como `Support__PublicForm__AllowedOrigins__N` del backend de desarrollo y en el CORS de su bucket cuando el proyecto de Vercel exista (L25). Corregir la línea que dice que 3002 es «el puerto de la página en local».
  - `CLAUDE.md`: en el gotcha de `SupportUserReferenceProbe`, sumar `legal_document_versions.created_by`; gotcha nuevo: «**Todo HTML de un documento legal pasa por `ISupportHtmlSanitizer`**: al publicar, al servir `GET /form` y en la foto. Un camino nuevo que escriba `content_html` sin el puerto abre XSS en el portal público. `legal_text_versions` y `consents` siguen mapeadas sin escritor: quitarlas del modelo hace que EF las borre en la siguiente migración.»
- [ ] **Paso 3: GREEN** — `dotnet test $AT --filter "FullyQualifiedName~ConfigurationExampleTests"` en verde.
- [ ] **Paso 4: commit** — `docs(support): document the legal documents and the portal origin`.

**Cierre del Bloque B:** una corrida por clase: `SupportPublicFormApiTests`, `SupportPublicFilingApiTests`, `SupportLegalAcceptancesApiTests`, `SupportPublicSurfaceApiTests` (la forma de `GET /form` cambió) en `$SI`, una a la vez; `FilePublicTicketValidatorTests` en `$SU`; `ConfigurationExampleTests`.

**Revisión del Bloque B** (un revisor): L13–L20, L23–L25; Review Focus 3 y 4; que el anónimo no deje IP en ninguna tabla; que el contrato de las respuestas sea exactamente el de «Contrato para las pantallas».

## Bloque C — Datos que no se omiten y radicado idempotente (Tareas 8–9, un implementador)

Decisiones del owner del 2026-11-15 en la revisión del spec del portal (spec §9.1, §9.2, §15 D-6 y D-7). Va después del Bloque B: toca `FilePublicTicket.cs`, `SupportPublicDtos.cs` y `SupportPublicHarness.cs`, que el Bloque B ya cambió.

### Task 8: Fecha de radicación y término en el radicado y la consulta

**Files:**
- Modify: `Public/SupportPublicDtos.cs` (`SupportPublicFiledDto`, `SupportPublicTicketDto`), `Public/FilePublicTicket.cs` (arma el DTO), `Public/LookupPublicTicket.cs`
- Test: `$SI\SupportPublicFilingApiTests.cs`, `$SI\SupportPublicLookupApiTests.cs`

- [ ] **Paso 1: pruebas**
  - `SupportPublicFilingApiTests.TheFilingResponseCarriesReceivedAtAndTheTerm` — `[Theory]` con un tipo de 15 días hábiles legales y uno sin término: `receivedAt` igual a `support.tickets.received_at` de la fila, `termBusinessDays` `15`/`null`, `termIsLegal` como el tipo, `dueOn` sin cambio.
  - `SupportPublicLookupApiTests.TheTermIsTheTicketsSnapshot` — radicar, cambiar el término del tipo por la consola y consultar: sigue el término con que se radicó.
  - `SupportPublicLookupApiTests.TheConsultCarriesTheTermBusinessDays` — con término y sin término.
- [ ] **Paso 2: RED** — `dotnet build $SI` falla: `SupportPublicFiledDto` no tiene `ReceivedAt` (`CS1061`).
- [ ] **Paso 3: implementar** — L26. `Pin` pasa a `string?` en el DTO (lo usa la Tarea 9; acá siempre lleva valor).
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(support): show the filing date and the term on the public portal`.

### Task 9: `clientRequestId` — radicado idempotente

**Files:**
- Create: `Modules.Support.Application/SupportClientRequestTakenException.cs`; migración `AddSupportTicketClientRequestId`
- Modify: `Modules.Support.Domain/SupportTicket.cs` (`ClientRequestId`, parámetro de `CreateFromPublicForm`), `ISupportTicketRepository` (+`FindByClientRequestAsync(Guid tenantId, Guid clientRequestId, CancellationToken)`, sin tracking) y su implementación, `SupportDbContext.cs` (columna `client_request_id`, índice parcial `IX_tickets_tenant_client_request` con `HasFilter("client_request_id IS NOT NULL")`), `SupportUnitOfWork.cs` (el índice por nombre → `SupportClientRequestTakenException`), `Public/SupportPublicDtos.cs` (`ClientRequestId` en `SupportPublicFileRequest`), `Public/FilePublicTicket.cs` (L27–L28), `README.md` y `CLAUDE.md`
- Test: `$SU\FilePublicTicketValidatorTests.cs`, `$SU\SupportTicketPublicFormTests.cs`, `$SI\SupportPublicIdempotencyApiTests.cs` (nueva), `$SI\SupportPublicFormPersistenceTests.cs`, `$SI\SupportPublicHarness.cs` (cuerpos con `clientRequestId` opcional)

- [ ] **Paso 1: pruebas**
  - `FilePublicTicketValidatorTests.AnEmptyClientRequestIdIsAnError` (`["ClientRequestId"]`) y `ANullClientRequestIdIsFine`.
  - `SupportTicketPublicFormTests.TheTicketKeepsItsClientRequestId`.
  - `SupportPublicFormPersistenceTests.TwoTicketsWithTheSameClientRequestIdInATenantAreRejected` (SQL crudo: viola `IX_tickets_tenant_client_request`) y `ManyTicketsWithoutAClientRequestIdAreFine`.
  - `SupportPublicIdempotencyApiTests` (`SharedSupportHost` con `SupportPublicHarness`; cada prueba siembra su tenant):
    - `ARetryWithTheSameClientRequestIdReturnsTheSameTicket` — dos `POST` iguales → los dos `201`, mismo `number`, `receivedAt`, `dueOn`, `consultUrl`; una sola fila en `support.tickets` con esa clave; `RecordingSupportOpeningEmail` registró un solo correo; el siguiente radicado sin clave toma el número siguiente (sin hueco).
    - `ARetryAfterTheEmailWentOutReturnsNoPin` — con datos: el reintento trae `pin: null`, `pinEmailed: true`; el PIN de la primera respuesta sigue sirviendo en `POST /tickets/lookup`.
    - `ARetryOfAnAnonymousTicketRotatesThePin` — anónimo: el reintento trae un `pin` de 6 dígitos y `pinEmailed: false`; con el PIN viejo la consulta da `422 support.public.lookup_failed` y con el nuevo `200`; ningún correo.
    - `ARetryAfterAFailedEmailRotatesThePin` — `RecordingSupportOpeningEmail` falla en el primer envío: el reintento trae PIN nuevo y no vuelve a intentar el correo.
    - `ARetrySkipsTurnstileAndTheVersionChecks` — el reintento lleva un token que `FakeSupportCaptcha` rechaza, y entre los dos `POST` se publica una versión nueva del formulario y de `terms`: igual `201` con el mismo número.
    - `ARetryWithADifferentBodyStillReturnsTheFirstTicket` — otro asunto y otro tipo: mismo número; el ticket guarda el asunto del primero.
    - `TwoConcurrentFilingsWithTheSameKeyCreateOneTicket` — dos `POST` simultáneos (`Task.WhenAll`) con la misma clave: los dos `201` con el mismo número, una sola fila, un solo par de aceptaciones legales, un solo correo.
    - `TheSameKeyInAnotherTenantIsAnotherTicket` — misma clave en dos tenants: dos tickets.
    - `WithoutAKeyEveryFilingIsNew` — dos `POST` sin clave: dos números.
    - `TheKeyIsNeverInTheTraceNorTheAudit` — `ticket_events` y la auditoría del alta no contienen la clave.
- [ ] **Paso 2: RED** — `dotnet build $SU` falla: `FilePublicTicketCommand` sin `ClientRequestId`.
- [ ] **Paso 3: implementar** — dominio, mapeo y `dotnet ef migrations add AddSupportTicketClientRequestId` (revisar que el `Up` sólo agregue la columna y el índice; `Down` los quita, la columna no es prueba legal); handler según L27 y L28. Documentación:
  - README § «Formulario público de Soporte»: el radicado acepta `clientRequestId`; la tabla de qué trae `pin` en un reintento; que el reintento se mira antes de Turnstile y no compara el cuerpo; la migración `AddSupportTicketClientRequestId`; `receivedAt`, `termBusinessDays` y `termIsLegal` en las respuestas; que el NIT del tenant está pendiente (D-6).
  - `CLAUDE.md`, gotcha nuevo: «**El reintento del radicado público se reconoce por `clientRequestId` antes de Turnstile y bajo el candado del contador** (spec 2026-11-15 §9.2). No muevas la búsqueda después de Turnstile: el token ya se gastó en el primer intento y el reintento fallaría con `captcha_invalid`. Un reintento no reenvía el correo; si el PIN no salió por ningún canal, emite uno nuevo y el viejo deja de servir.»
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre; `dotnet test $SU --filter "FullyQualifiedName~FilePublicTicketValidatorTests|FullyQualifiedName~SupportTicketPublicFormTests"`.
- [ ] **Paso 5: commit** — `feat(support): make public filing idempotent by client request id`.

**Cierre del Bloque C:** una corrida por clase: `SupportPublicIdempotencyApiTests`, `SupportPublicFilingApiTests`, `SupportPublicLookupApiTests`, `SupportPublicFormPersistenceTests` en `$SI`, una a la vez; `FilePublicTicketValidatorTests` y `SupportTicketPublicFormTests` en `$SU`.

**Revisión del Bloque C** (un revisor): L26–L28; Review Focus 6 y 7; que la búsqueda por clave vaya antes de Turnstile y siempre filtrada por tenant; que la migración sea aditiva; que el contrato de `POST /tickets` y `POST /tickets/lookup` sea exactamente el de «Contrato para las pantallas».

**Antes de mergear a `develop`:** suites completas de `$SU`, `$SI`, `$BU`, `$AT`, una a la vez, con `-- xUnit.MaxParallelThreads=4` en las de integración; `dotnet format --verify-no-changes`; `dotnet build` limpio; `dotnet restore --locked-mode` limpio.

**Revisión final** (modelo más capaz, toda la rama contra `develop`): saneamiento (lista blanca, idempotencia, re-saneo al servir), prueba legal (insert-only, sin FK, `Down`, foto exacta), anónimo sin IP, migraciones aditivas con relleno correcto, radicado idempotente sin segundo ticket ni PIN filtrado, arquitectura (`SupportLayerTests`) y dependencia nueva fijada.

## Textos de ejemplo (L9)

Revisados por el abogado antes de producción (D-4). Se guardan ya en forma canónica del saneador (`EachKindHasAnExampleAtVersionZero` lo amarra).

- **`PrivacyNotice`** (el ejemplo actual de `SupportLegalTextVersion.Example`, en dos párrafos; pregunta `null`, URL `null`):
  `<p>Al enviar este formulario autorizas de manera previa, expresa e informada a la empresa para recolectar, almacenar, usar y tratar tus datos personales con el fin de radicar, gestionar y responder tu petición, queja, reclamo, sugerencia o felicitación, y de contactarte sobre ella. Puedes conocer, actualizar, rectificar y suprimir tus datos, y revocar esta autorización, en los términos de la Ley 1581 de 2012 y su política de tratamiento de datos.</p><p>Tratamos tus datos personales sólo para gestionar tu solicitud y cumplir obligaciones legales. Los conservamos mientras dure el trámite y el término que exija la ley. Para ejercer tus derechos como titular, escríbenos por los canales de contacto de la empresa.</p>`
- **`AnonymousNotice`**:
  `<p>Puedes radicar tu solicitud sin darnos tus datos personales: no te pediremos nombre, documento ni datos de contacto.</p><p>Como no tenemos cómo contactarte, la respuesta se publica en la consulta de tu radicado y sólo la ves con el PIN que te damos al final. Guárdalo: si lo pierdes, no se puede recuperar ni reenviar.</p>`
- **`Terms`**:
  `<p>Este sistema recibe peticiones, quejas, reclamos, sugerencias, felicitaciones y solicitudes de soporte. Al radicar recibes un número de radicado y un PIN para consultar el estado de tu solicitud.</p><p>Respondemos dentro de los plazos que fija la ley para cada tipo de solicitud. Si la información que nos das no alcanza para tramitarla, podremos pedirte más datos.</p><p>Usa este canal con respeto y con información veraz. Las solicitudes ofensivas o sin relación con nuestros productos o servicios pueden no tramitarse.</p>`

## Decisiones del owner (resueltas 2026-11-15)

D-1 a D-5 del spec §15, aprobadas tal como se recomendaron: pregunta de autorización editable con una por defecto (L7); enlace a la política opcional; `HtmlSanitizer` (L4); ejemplos para los tres tipos con revisión del abogado (L9); en desarrollo, el alias estable de `develop` del portal, que se configura cuando exista (L25).

Agregadas el 2026-11-15 en la revisión del spec del portal (Bloque C):

- **D-6 «No omitir datos»:** fecha de radicación y término en días hábiles en el radicado y la consulta (L26, Tarea 8). **El NIT del tenant queda pendiente:** `Tenant` no tiene razón social ni NIT; los NIT están en `Modules.Companies` (`Company.TaxId`, `DocumentType`, `Name`), un tenant tiene varias empresas y ninguna es la propia. Recomendación del spec: que la configuración del formulario público elija una empresa del tenant. Hasta que el owner decida, el plan no agrega el campo.
- **D-7 Radicado idempotente por `clientRequestId`** (L27, Tarea 9), calcado del checkout de la tienda. Pendiente de confirmar la regla del PIN en un reintento (L28): `null` si ya salió por correo, uno nuevo si no salió por ningún canal.

## Spec discrepancies (spec contra código)

1. **§9 y §10 dicen que `acceptsDataPolicy` falso responde `422 support.public.consent_required` y que `support.public.terms_required` es un código nuevo por HTTP.** No: `FilePublicTicketValidator` lo marca con `WithErrorCode` y `ApiExceptionHandler` (`:70-76, :185`) responde siempre `validation.failed` con el mapa `errors`, sin el `ErrorCode`. Así responde hoy, y el README de la fase 3 (`support.public.consent_required`) también lo describe mal. El plan sigue al código (L15): `errors.AcceptsDataPolicy` y `errors.AcceptsTerms`. Spec §9 y §10 se corrigen en el mismo commit que este plan; el README, en la Tarea 7.
2. **§11 dice escapar `"` en el relleno.** AngleSharp no lo escapa al serializar texto, así que `&quot;` no sería la forma canónica y re-sanear cambiaría los bytes. El relleno escapa sólo `&`, `<`, `>` (L12). Spec §11 se corrige en el mismo commit que este plan.
3. **§9 nombra los campos de error en camelCase** (`privacyNoticeVersion`). Las claves de `errors` llegan en PascalCase (`PropertyName`), como el resto del módulo. El contrato de este plan usa PascalCase; spec §9 se corrige en el mismo commit.
