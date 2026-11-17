# Soporte (PQRSF) — Fase 3 «Formulario público», backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un solicitante radique un ticket en `soporte.lulocrm.com/{tenantSlug}` —con su documento o anónimo—, reciba un radicado y una clave de consulta (PIN), y consulte después el estado y la respuesta con radicado más PIN; que el tenant configure el formulario y sus textos legales versionados; que cada aceptación quede como prueba legal insert-only; y que la información del ticket con el PIN salga por el canal del ticket (correo, o texto en el hilo de WhatsApp dentro de la ventana de 24 h).

**Architecture:** Un grupo anónimo nuevo, `/api/v1/public/support/{tenantSlug}`, en `Modules.Support.Api`, que el navegador llama directo (no hay BFF como en la tienda): política CORS propia **sin credenciales** aplicada sólo a ese grupo, exención de CSRF por prefijo, `Cache-Control: no-store` antes del limitador, cuerpo con tope de 64 KB, limitadores propios y Turnstile. El tenant se resuelve por `Tenant.Slug` con un puerto nuevo de Tenancy; todo fallo (slug desconocido, tenant inactivo, módulo apagado, formulario sin publicar) es el mismo 404. Support sigue sin referenciar Messaging, Customers, Geography, Store ni Notifications: búsqueda por documento, geografía, captcha, correo de apertura y el texto por el hilo entran por puertos con adaptador en `src/Bootstrapper/`. La plantilla «Apertura de Soporte» **no** se construye (owner, 2026-11-15: el PIN por WhatsApp sólo va a quien escribió por WhatsApp, dentro de la ventana).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql.

**Spec:** [`docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md`](../specs/2026-11-14-support-tickets-pqrs-design.md), §9 completo, §8.5 (fila «Apertura de Soporte»), §10 (CORS, slugs, pruebas) y §12 fase 3. Planes anteriores: [`fase 1`](2026-11-14-support-phase-1-core-backend.md), [`fase 2`](2026-11-15-support-phase-2-messaging-backend.md) (E1 movía la plantilla de apertura a esta fase; el owner la descartó el 2026-11-15, F24).

---

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-public-form`, rama `feature/support-public-form`, sale de `develop` en `6233f645` (fases 1 y 2 mergeadas). Rutas absolutas siempre: un script con `cwd` relativo escribe en el checkout de `develop`.
- Antes de **cada** commit: `git branch --show-current`. Si dice `main` o `develop`, frena.
- Conventional commits en inglés, `git commit -m "…" -- <rutas>`, **sin `Co-Authored-By` ni atribución de IA**, aunque el harness la sugiera.
- PowerShell, sin `&&`. Si `Api.exe` o `dotnet Api.dll` corre, el build falla por archivo bloqueado: avisa, no lo detengas.
- **No se corre ninguna prueba** (owner, 2026-11-15): ni unitarias ni de integración, ni filtradas. TDD en el orden: la prueba primero; RED = `dotnet build` del proyecto de pruebas fallando por compilación (`CS0246`, `CS1061`…); GREEN = `dotnet build` de la solución sin errores ni avisos. Una prueba que compila desde el principio se marca «RED sin evidencia: no se corre». Las dos salidas van al reporte del bloque.
- Atajos: `$W` = worktree; `$SU`/`$SI` = `$W\tests\Modules\Support\Modules.Support.{UnitTests,IntegrationTests}`; `$MU`/`$MI` = los de Messaging; `$TU`/`$TI` = los de Tenancy; `$NU` = `…\Modules.Notifications.UnitTests`; `$AT` = `$W\tests\ArchitectureTests\ArchitectureTests`; `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`.
- Migraciones con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add AddSupportPublicForm --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  ```
- `docs/estado.md` **no se toca** en esta rama.

## Global Constraints

- Support no referencia `Modules.Messaging.*`, `Customers.*`, `Geography.*`, `Store.*` ni `Notifications.*`; `SupportLayerTests` no se afloja. Sí referencia `Modules.Tenancy.Application` (ya lo hace).
- Rutas públicas: prefijo exacto `/api/v1/public/support/{tenantSlug}`; `AllowAnonymous`; ningún handler del grupo lee `IExecutionContext` ni la cookie de sesión.
- Toda respuesta del grupo, también 4xx, 413 y 429, lleva `Cache-Control: no-store` (middleware antes de `UseRateLimiter`, `Program.cs:232-233`).
- La política CORS `spa` no cambia. La nueva es otra política con nombre, sin `AllowCredentials`, con orígenes exactos de configuración.
- Tipos de documento: sólo `DocumentTypesByCountry` (`BuildingBlocks.Domain/Identification`). Nada de copiar la tabla.
- Prueba legal insert-only: `support.legal_text_versions` y `support.consents` no tienen método de dominio que edite ni borre, `consents` no tiene FK y el `Down` de la migración **no** las borra.
- PIN: 6 dígitos, `RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture)` (precedente `CompleteWhatsAppSignup.cs:196`); se guarda sólo su hash; nunca se loguea, ni en auditoría ni en `ticket_events`.
- Un ticket anónimo no guarda `customer_id`, foto del solicitante, documento, IP ni consentimiento.
- Mensajes al solicitante en español tuteando; códigos, rutas, enums y claves de plantilla en inglés o como contrato (`radicado`, `pin`…).

## Decisiones del plan

Mías (planner) sobre lo que el spec deja abierto. Las marcadas **(owner)** van en la lista de decisiones abiertas al final y el plan las asume como aquí dicen hasta que el owner responda.

| # | Decisión | Por qué |
| --- | --- | --- |
| F1 | **Slugs reservados** en Tenancy: `TenantSlugReservations.Reserved` (`Modules.Tenancy.Domain`), código `tenancy.slug.reserved`, verificado en `Tenant.Create` y `Tenant.EnsureValidRegistration`, **no** en el constructor de lectura. Lista: `api`, `app`, `assets`, `static`, `public`, `consultar`, `calificar`, `admin`, `www`, `soporte`, `support`, `pqrsf`, `qep`, `login`, `cuenta`, `health`, `status`, `robots`, `sitemap`. **(owner: aceptada por omisión, 2026-11-15)** | §9.2: la lista sale de las rutas fijas del host; `calificar` reserva la página de la encuesta de la fase 4. Un tenant existente con slug reservado sigue cargando (se corrige a mano). |
| F2 | Tenant por slug: puerto nuevo `ITenantSlugDirectory` en `Modules.Tenancy.Application`, no un método más en `ITenantDirectory`. | Sumar un método a `ITenantDirectory` rompe cada doble que lo implementa en las pruebas. |
| F3 | Resolución pública en `SupportPublicResolver`: slug normalizado → tenant `Active` → módulo `support` prendido → formulario publicado. Cualquier fallo: `404 support.public.not_found`, idéntico, `no-store`. | §9.1 y el precedente `StorefrontResolver` (fail closed, indistinguible). |
| F4 | CORS: política `support-public` en `CorsServiceCollectionExtensions`, orígenes de `Support:PublicForm:AllowedOrigins` (validados como `CorsSettingsValidator`: https exacto, `http://localhost[:port]` sólo en Development), métodos `GET, POST`, header `Content-Type`, **sin credenciales**; el grupo la pide con `.RequireCors("support-public")`. `UseQepCors` registra el middleware si hay orígenes en cualquiera de las dos. | §10. El metadata del endpoint gana sobre la política por defecto del middleware, así que `spa` no se extiende al grupo ni `support-public` a la consola. |
| F5 | CSRF: excepción por prefijo `SupportPublicPaths.Prefix = "/api/v1/public/support/"` (Bootstrapper, literal duplicado del grupo y amarrado por prueba), con la garantía probada de que el grupo ignora la cookie. | Una página en otro sitio no puede mandar `X-Qep-Client` sin preflight; la excepción es la vía del precedente de la tienda. |
| F6 | Limitadores por IP (`RemoteIpAddress`, ya corregida por `X-Real-IP`): `SupportPublicRead` 60/min (configuración y geografía), `SupportPublicSubmit` 5 cada 10 min (radicar y reenviar PIN) y `SupportPublicLookup` 10/min (consultar). **(owner: aceptados por omisión)** | §9.4 pide uno propio y más estricto que `Public`; valores del implementador. |
| F7 | PIN y bloqueo en tabla aparte, `support.ticket_access` (`ticket_id` PK, `tenant_id`, `pin_hash`, `failed_attempts`, `locked_until`, `pin_issued_at`, `last_sent_at`, `last_sent_channel`, `last_failure_code`). Cinco fallos seguidos bloquean 15 min; un acierto pone el contador en 0. Un reenvío **no** desbloquea. | Si viviera en `tickets`, cada intento fallido subiría `version` y le daría un 412 al asesor. |
| F8 | Hash del PIN con PBKDF2 (`Microsoft.AspNetCore.Identity.PasswordHasher<object>`, como `AspNetCoreBuyerPasswordHasher`) detrás del puerto `ISupportPinHasher`; un radicado inexistente verifica contra un hash señuelo para igualar el tiempo. | SHA-256 de 6 dígitos se invierte con un millón de intentos. PBKDF2 no pide un secreto nuevo en el pipeline. |
| F9 | Consulta: `POST …/tickets/lookup` con `{ number, pin, turnstileToken }` (el PIN no va en la URL ni en logs). Radicado desconocido, PIN errado o bloqueado responden igual: `422 support.public.lookup_failed`. | Anti-enumeración (§2 anti-objetivo). |
| F10 | «Reenviar PIN»: `POST …/tickets/resend-pin` con `{ number, turnstileToken }`, siempre `202` vacío. Genera PIN nuevo y lo manda al destino registrado: formulario no anónimo → correo; conversación → texto por su hilo si la ventana está abierta (cerrada: no sale, `window_closed`); manual → su canal de respuesta si es `Email` o tiene conversación. Anónimo, inexistente o sin destino: no hace nada. Enfriamiento: no reenvía si `last_sent_at` tiene menos de 10 min. | §9.4. |
| F11 | Autor de los envíos de apertura por Mensajería (la fila del mensaje exige autor): el miembro que creó el ticket; si ya no está activo, el responsable; si tampoco, el primer gestor de `ISupportMemberDirectory.ListManagersAsync`. Sin ninguno, no se envía (`last_failure_code = no_author`). | Precedente `BillingTemplateSend.SentByMemberId` («la fila exige un autor»). |
| F12 | Canal nuevo `SupportReplyChannel.Portal` (y `SupportReplyDelivery.Portal`): la respuesta se guarda `Sent` sin llamar afuera y sólo se ve en la consulta. Un ticket anónimo sólo puede responderse por `Portal`, y `Portal` sólo existe para anónimos. | §9.4: «la respuesta se publica, no se envía». Sin esto un ticket anónimo no tiene respuesta posible. |
| F13 | La consulta muestra número, tipo, estado, radicación, vencimiento y estado del plazo, y **todas** las respuestas `Sent` (texto, fecha, canal), de cualquier canal. Nada más del expediente. El PDF es de la fase 4. | §9.4. |
| F14 | Formulario versionado insert-only: `support.form_versions` (`id`, `tenant_id`, `version` único por tenant, `is_published`, `show_document`, `show_address`, `show_landline`, `business_name_required`, `accent_color`, `intro_text`, `success_message`, `created_at`, `created_by`). Guardar crea la versión `n+1` con `If-Match: n`; el ticket guarda `form_version_id`. Sin fila: formulario sin publicar con los valores por defecto (todo visible, razón social obligatoria). | §9.3 «Versionado». |
| F15 | Publicar exige una versión de textos legales; si no existe, `422 support.form.legal_texts_required`. | §9.5: el formulario siempre puede recoger datos (el no anónimo los pide). |
| F16 | Textos legales: `support.legal_text_versions` (`id`, `tenant_id`, `version`, `authorization_text` 1–20 000, `policy_text` ≤ 20 000, `policy_url` https ≤ 500, `terms_text` ≤ 20 000, `created_at`, `created_by`), con política por texto **o** URL (al menos una). `GET` sin versión devuelve el texto de ejemplo con `isExample: true`, que no se guarda. | §9.5. |
| F17 | Consentimiento: `support.consents` (`id`, `tenant_id`, `ticket_id`, `legal_text_version_id`, `legal_text_version`, `accepted_at`, `authorization_text`, `policy_text`, `policy_url`, `terms_text`, `visitor_ip` varchar(45)), sin FK, una fila por ticket no anónimo, en la misma transacción que el ticket. El cuerpo exige `acceptsDataPolicy: true` y `legalTextVersion` igual a la vigente; si cambió, `422 support.public.legal_texts_outdated` (la pantalla recarga). | §9.5: la prueba es lo que la persona vio, no lo que hay hoy. |
| F18 | Campos según el documento en **una** clase de dominio, `SupportFormFields`, que usan la configuración pública (para dibujar) y el validador (para exigir). Variantes: `Natural` (doc distinto de NIT), `Nit`, `NoDocument` (par oculto) y `Anonymous`. | §9.3 «Dónde vive la regla». |
| F19 | País: ISO alfa-2 validado con `ISupportCountries.IsKnown` (adaptador sobre el normalizador de teléfonos, como `IStoreCountries`); la pantalla dibuja los nombres. Ciudad: con país `CO`, `cityId` de Geography obligatorio (endpoints públicos del grupo); otro país, `cityName` libre de 1 a 100. **(owner: aprobada 2026-11-15)** | §9.3 pide Geography, pero Geography sólo tiene Colombia y no hay catálogo de países. |
| F20 | Celular obligatorio normalizado a E.164 con el país del solicitante por `ISupportPhoneNormalizer` (adaptador sobre el normalizador de Store). Fijo opcional, texto ≤ 32. | `TicketRequester.MobilePhoneMaxLength = 16` ya es E.164. |
| F21 | Vínculo con el cliente: `ISupportCustomerLookup.FindIdsByDocumentAsync(tenantId, type, number)` con `CustomerIdentificationSql.NormalizedNumber` más el tipo, incluye `Incomplete`, excluye fusionados. Uno: se vincula con `HoldCustomerReferenceAsync`. Cero o varios: sin cliente. Con `customers` apagado no se busca. El detalle del ticket suma `CustomerCandidates` cuando no tiene cliente y tiene documento. | §9.3. |
| F22 | Ticket del formulario: `SupportTicket.CreateFromPublicForm`, `Intake = PublicForm`, sin actor (evento `Created` con actor `null`, auditoría `PublishSystem`), `ReplyChannel = Email` (no anónimo) o `Portal` (anónimo), sin responsable. `TicketRequester` gana `CityId` y `CityName`; el ticket gana `IsAnonymous` y `FormVersionId`. Con NIT y razón social vacía, `DisplayName()` es `NIT <número>`. | §9.3, §9.4. |
| F23 | Correo de apertura (formulario no anónimo, manual por correo, reenvío): puerto `ISupportOpeningEmail` con la plantilla `SupportOpeningEmailTemplate` (`support.ticket-opened.v1`) de Notifications. Se manda **después** del commit; si falla, el ticket queda creado, el PIN igual se muestra en pantalla y la falla queda en `ticket_access.last_failure_code`. | §9.4. El radicado no puede depender del correo. |
| F24 | Plantilla «Apertura de Soporte»: **no se construye** (owner, 2026-11-15). El PIN por WhatsApp sólo va a tickets que nacen de una conversación, con la ventana de 24 h abierta: texto libre. Se evita además el riesgo de recategorización a `AUTHENTICATION`. | Respuesta del owner a P-2. |
| F25 | Ticket desde una conversación (fase 2): después del commit manda la información con el PIN por ese hilo: texto si la ventana está abierta (Mensajería, `ISupportReplyChannels.SendThreadAsync`); si cerró, nada (`last_failure_code = window_closed`). El ticket ya está creado pase lo que pase. | §9.4. |
| F26 | Ticket manual: con canal `Email`, correo de apertura después del commit. Con canal `WhatsAppTemplate` o `Thread` sin conversación **no** sale nada (owner, 2026-11-15: se deja así). | El envío a un celular nuevo pide elegir conexión (E14 de la fase 2): no cabe en el alta. |
| F27 | Adjuntos del formulario público: **entran** (owner, 2026-11-15), Bloque D, F30–F37. | Respuesta del owner a P-4. |
| F28 | Captcha: `ISupportCaptcha` con adaptador `SupportCaptcha` sobre `ICaptchaVerifier` de Store (como `BuyerCaptcha.cs:10`), `expectedHostname` = `Support:PublicForm:Hostname` (por defecto `soporte.lulocrm.com`). Usa el widget y el secreto de Turnstile de la tienda (`Store:Turnstile:*`); el hostname se exige sólo en `Production`. **(owner: reusar, 2026-11-15)** | §9.4. Un widget de Turnstile admite varios hostnames con un solo secreto. |
| F29 | Enlace de consulta: `{Support:PublicForm:BaseUrl}/{slug}/consultar?radicado=SOP-000127` (por defecto `https://soporte.lulocrm.com`). Nunca lleva el PIN. | §9.1. |

## Review Focus

1. **Enumeración por la consulta:** radicado inexistente, PIN errado y radicado bloqueado deben ser indistinguibles en status, código y cuerpo. Tarea 7: `AnUnknownNumberAWrongPinAndALockedTicketAnswerTheSame`.
2. **Fuerza bruta del PIN:** tras cinco fallos, el PIN **correcto** también falla durante 15 min, y otro ticket del mismo tenant sigue consultable. Tarea 7: `FiveWrongPinsLockTheTicketEvenForTheRightPin` y `ALockedTicketDoesNotLockAnother`.
3. **La cookie de sesión y CORS:** una petición del grupo con la cookie de sesión de un asesor y sin `X-Qep-Client` debe pasar sin autenticar a nadie; el preflight desde `soporte.lulocrm.com` recibe `Access-Control-Allow-Origin` **sin** `Allow-Credentials`, y ese mismo origen no recibe nada en una ruta de la consola. Tarea 4: `ThePublicGroupIgnoresTheSessionCookieWithRealAuth`, `ThePublicPreflightAllowsTheSupportOriginWithoutCredentials`, `TheSupportOriginGetsNoCorsOnTenantRoutes`.
4. **«Reenviar PIN» nunca usa un destino del visitante** y responde igual exista o no el radicado, sea anónimo o esté en enfriamiento. Tarea 7: `ResendPinAnswersTheSameAndOnlyWritesToTheRegisteredEmail`.
5. **El consentimiento sobrevive a la edición del texto:** radicar, publicar una versión nueva de los textos y leer la fila: guarda el texto viejo. Un anónimo no deja fila ni IP. Tarea 6: `TheConsentKeepsTheTextTheRequesterAccepted` y `AnAnonymousTicketStoresNoRequesterNoIpAndNoConsent`.

---

## Contrato para las pantallas (BFF)

**Públicas**, bajo `/api/v1/public/support/{tenantSlug}`, anónimas, `no-store`:

| Método y ruta | Limitador | Respuesta |
| --- | --- | --- |
| `GET /form` | `SupportPublicRead` | `SupportPublicFormDto` |
| `GET /geography/departments` | `SupportPublicRead` | `SupportPublicGeoListDto` |
| `GET /geography/departments/{departmentId:guid}/cities` | `SupportPublicRead` | `SupportPublicGeoListDto` (404 `support.public.department_not_found`) |
| `POST /tickets` | `SupportPublicSubmit` | `201 SupportPublicFiledDto` |
| `POST /tickets/lookup` | `SupportPublicLookup` | `SupportPublicTicketDto` |
| `POST /tickets/resend-pin` | `SupportPublicSubmit` | `202` vacío |

- `SupportPublicFormDto(string TenantName, string? LogoUrl, string? AccentColor, string? IntroText, string DefaultCountry, IReadOnlyList<SupportPublicTypeDto> Types, SupportPublicFieldsDto Fields, IReadOnlyDictionary<string, IReadOnlyList<string>> DocumentTypesByCountry, SupportPublicLegalDto Legal, int FormVersion)`. `DocumentTypesByCountry` trae `CO`, `ES` y `*` (el resto) desde `DocumentTypesByCountry.AllowedFor`: la pantalla no conoce la tabla.
- `SupportPublicTypeDto(Guid Id, string Name, int? TermBusinessDays)`. Sólo activos y publicados, en orden.
- `SupportPublicFieldsDto(bool ShowDocument, IReadOnlyList<SupportPublicFieldDto> Natural, IReadOnlyList<SupportPublicFieldDto> Nit, IReadOnlyList<SupportPublicFieldDto> NoDocument)`; `SupportPublicFieldDto(string Key, bool Required)`. Claves: `documentType`, `documentNumber`, `firstNames`, `lastNames`, `businessName`, `address`, `landlinePhone`, `mobilePhone`, `country`, `city`, `email`. Un campo oculto no viene en la lista.
- `SupportPublicLegalDto(int Version, string AuthorizationText, string? PolicyText, string? PolicyUrl, string? TermsText)`.
- `SupportPublicFileRequest(Guid? TicketTypeId, string? Subject, string? Description, bool Anonymous, SupportPublicRequesterRequest? Requester, bool AcceptsDataPolicy, int? LegalTextVersion, int? FormVersion, string? TurnstileToken)`; `SupportPublicRequesterRequest(string? DocumentType, string? DocumentNumber, string? FirstNames, string? LastNames, string? BusinessName, string? Address, string? LandlinePhone, string? MobilePhone, string? Country, Guid? CityId, string? CityName, string? Email)`.
- `SupportPublicFiledDto(string Number, string Pin, DateOnly? DueOn, string? SuccessMessage, bool PinEmailed, string ConsultUrl)`. `Pin` sólo viaja aquí, una vez.
- `SupportPublicLookupRequest(string? Number, string? Pin, string? TurnstileToken)`; `SupportPublicResendPinRequest(string? Number, string? TurnstileToken)`.
- `SupportPublicTicketDto(string Number, string TypeName, string Status, DateTimeOffset ReceivedAt, DateOnly? DueOn, string? DueState, bool TermIsLegal, IReadOnlyList<SupportPublicReplyDto> Replies)`; `SupportPublicReplyDto(string Text, DateTimeOffset SentAt, string Channel)`.

**Consola**, bajo `/api/v1/tenants/{tenantId:guid}/support`:

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /settings/public-form` | `SettingsView` | `SupportFormSettingsDto` |
| `PUT /settings/public-form` (`If-Match: <version>`) | `SettingsManage` | `SupportFormSettingsDto` |
| `GET /settings/legal-texts` | `SettingsView` | `SupportLegalTextsDto` |
| `POST /settings/legal-texts` (`If-Match: <version>`, `0` si no hay) | `SettingsManage` | `201 SupportLegalTextsDto` |

- `SupportFormSettingsDto(int Version, bool IsPublished, bool ShowDocument, bool ShowAddress, bool ShowLandline, bool BusinessNameRequired, string? AccentColor, string? IntroText, string? SuccessMessage, string PublicUrl, IReadOnlyList<string> PublishBlockers)`. `PublishBlockers` ∈ `legal_texts_required`, `no_published_types`: la pantalla explica por qué no se puede publicar sin adivinar.
- `SupportLegalTextsDto(int Version, bool IsExample, string AuthorizationText, string? PolicyText, string? PolicyUrl, string? TermsText, DateTimeOffset? CreatedAt, SupportMemberDto? CreatedBy)`.
- `SupportTicketDetailDto` suma `bool IsAnonymous`, `IReadOnlyList<SupportCustomerCandidateDto> CustomerCandidates` (`SupportCustomerCandidateDto(Guid Id, string Name, bool IsActive)`) y `SupportConsultAccessDto? ConsultAccess(DateTimeOffset? LastSentAt, string? Channel, string? FailureCode)`. `SupportReplyOptionsDto` suma `SupportChannelOptionDto Portal`.

## Estructura de archivos

```
src/Modules/Tenancy/Modules.Tenancy.Domain/TenantSlugReservations.cs            (nuevo)
src/Modules/Tenancy/Modules.Tenancy.Application/ITenantSlugDirectory.cs         (nuevo) + Infrastructure/Persistence/TenantSlugDirectory.cs
src/Modules/Support/
  Domain/        SupportTicket (+CreateFromPublicForm, IsAnonymous, FormVersionId), TicketRequester (+CityId, CityName),
                 SupportReplyChannel/Delivery (+Portal), SupportTicketAccess, SupportPin, SupportFormVersion, SupportFormFields,
                 SupportLegalTextVersion, SupportConsent, SupportErrorCodes (+)
  Application/   Public/ (SupportPublicResolver, GetPublicForm, ListPublicGeography, FilePublicTicket, LookupPublicTicket,
                 ResendPublicPin, SupportPublicDtos), Settings/ (GetFormSettings, UpdateFormSettings, GetLegalTexts, PublishLegalTexts),
                 Opening/ (SupportOpeningSender), ports: ISupportCaptcha, ISupportCountries, ISupportPhoneNormalizer,
                 ISupportGeography, ISupportOpeningEmail, ISupportPinHasher, ISupportTicketAccessRepository,
                 ISupportFormRepository, ISupportLegalTextRepository, ISupportConsentRepository, ISupportPublicOptions
  Infrastructure/ repositorios nuevos, Pbkdf2SupportPinHasher, migración AddSupportPublicForm
  Api/           PublicSupportEndpoints.cs (nuevo), SupportEndpoints.cs (+settings)
src/Modules/Storage/    FileOwnerType.SupportTicket, IStorageSystemUploads (+núcleo común con CompleteUpload)
src/Modules/Notifications/Modules.Notifications.Application/SupportOpeningEmailTemplate.cs
src/Bootstrapper/ SupportPublicPaths, SupportCaptcha, SupportCountries, SupportPhoneNormalizer, SupportGeography,
                 SupportOpeningEmailSender, SupportPublicOptions binding, RequireCsrfHeaderMiddleware (+), BuyerNoStoreMiddleware (+),
                 Cors (+support-public), SupportCustomerLookup (+FindIdsByDocumentAsync), SupportAttachmentStorage, SupportAttachmentReferenceProbe
src/Api/         Program.cs (limitadores, MapPublicSupportEndpoints), RateLimiterPolicies.cs (+3), appsettings.json (Support:PublicForm)
```

---

## Bloque A — Slugs y modelo del formulario (Tareas 1–3, un implementador)

### Task 1: Slugs reservados de tenant

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantSlugReservations.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` (`Create` :128, `EnsureValidRegistration` :144)
- Test: `$TU\TenantSlugReservationsTests.cs`, `$TU\TenantTests.cs`, `$TI\RegistrationApiTests.cs`

**Interfaces:**
- Produces: `public static class TenantSlugReservations { public static IReadOnlySet<string> Reserved { get; } public static bool IsReserved(string normalizedSlug); }`; código `TenantErrorCodes.SlugReserved = "tenancy.slug.reserved"` (o la constante equivalente donde vivan los códigos de Tenancy).

- [ ] **Paso 1: pruebas**
  - `TenantSlugReservationsTests.TheListIsTheSupportHostRoutes` — `Reserved` es exactamente la lista de F1 (19 palabras, ordinal).
  - `TenantTests.CreateRejectsAReservedSlug` — `Tenant.Create(..., "consultar", ...)` lanza `TenantDomainException` con `tenancy.slug.reserved`; `"Consultar "` también (normaliza antes).
  - `TenantTests.EnsureValidRegistrationRejectsAReservedSlug` — mismo código.
  - `RegistrationApiTests.RegisterTenantRejectsAReservedSlug` — `POST register-tenant` con `slug = "api"` → `422`, `code = tenancy.slug.reserved`, sin tenant creado.
- [ ] **Paso 2: RED** — `dotnet build $TU` falla por `TenantSlugReservations` inexistente.
- [ ] **Paso 3: implementar** — verificar después de `ValidateSlug` y antes de devolver. El constructor que usa EF no cambia.
- [ ] **Paso 4: GREEN** — `dotnet build` de la solución del repo (la que nombra el README § Verificación) limpio.
- [ ] **Paso 5: commit** — `feat(tenancy): reserve the support host routes as tenant slugs`. Agregar al README (§ Soporte o Tenancy) la consulta previa al despliegue: `SELECT id, slug FROM tenancy.tenants WHERE slug IN (…lista…);`.

### Task 2: Dominio y persistencia del formulario público

**Files:**
- Modify: `Modules.Support.Domain/SupportTicket.cs`, `TicketRequester.cs`, `SupportReplyChannel.cs`, `SupportReplyDelivery.cs`, `SupportErrorCodes.cs`
- Create: `Modules.Support.Domain/SupportTicketAccess.cs`, `SupportPin.cs`, `SupportFormVersion.cs`, `SupportFormFields.cs`, `SupportLegalTextVersion.cs`, `SupportConsent.cs`
- Modify: `Modules.Support.Infrastructure/Persistence/SupportDbContext.cs`, `SupportUnitOfWork.cs`; migración `AddSupportPublicForm`
- Create: repositorios `SupportTicketAccessRepository`, `SupportFormRepository`, `SupportLegalTextRepository`, `SupportConsentRepository`, `Pbkdf2SupportPinHasher`
- Test: `$SU\SupportTicketPublicFormTests.cs`, `$SU\SupportFormFieldsTests.cs`, `$SU\SupportTicketAccessTests.cs`, `$SU\SupportLegalTextVersionTests.cs`, `$SI\SupportPublicFormPersistenceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static SupportTicket CreateFromPublicForm(SupportTicketId id, Guid tenantId, long number, SupportTicketType type,
      string subject, string description, TicketRequester? requester, Guid? customerId, Guid formVersionId,
      DateTimeOffset now, SupportClockContext clock);              // requester null ⇒ IsAnonymous, ReplyChannel Portal
  public sealed record TicketRequester(..., string CountryCode, string? Address, Guid? CityId, string? CityName);  // dos campos al final
  public static class SupportPin { public static string Generate(); }          // "D6"
  public interface ISupportPinHasher { string Hash(string pin); bool Verify(string? hash, string pin); }   // hash null ⇒ señuelo
  public sealed class SupportTicketAccess {
      public static SupportTicketAccess Issue(SupportTicketId ticketId, Guid tenantId, string pinHash, DateTimeOffset now);
      public bool IsLocked(DateTimeOffset now);
      public void RecordFailure(DateTimeOffset now);                // 5.º fallo ⇒ LockedUntil = now + 15 min
      public void RecordSuccess();
      public void Reissue(string pinHash, DateTimeOffset now);      // no toca el bloqueo
      public bool CanResend(DateTimeOffset now);                    // LastSentAt null o ≥ 10 min
      public void RecordDelivery(string channel, string? failureCode, DateTimeOffset now);
      public const int MaxFailures = 5; public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
  }
  public enum SupportFormVariant { Natural, Nit, NoDocument, Anonymous }
  public sealed record SupportFormField(string Key, bool Required);
  public static class SupportFormFields {
      public static IReadOnlyList<SupportFormField> For(SupportFormVariant variant, SupportFormVersion form);
      public static SupportFormVariant VariantOf(bool anonymous, bool showDocument, string? documentType);
  }
  public sealed class SupportFormVersion {   // insert-only
      public static SupportFormVersion Create(Guid id, Guid tenantId, int version, bool isPublished, bool showDocument,
          bool showAddress, bool showLandline, bool businessNameRequired, string? accentColor, string? introText,
          string? successMessage, Guid createdBy, DateTimeOffset now);
      public static SupportFormVersion Default(Guid tenantId);      // versión 0, sin publicar, no se guarda
  }
  public sealed class SupportLegalTextVersion {   // insert-only
      public static SupportLegalTextVersion Create(Guid id, Guid tenantId, int version, string authorizationText,
          string? policyText, string? policyUrl, string? termsText, Guid createdBy, DateTimeOffset now);
      public static SupportLegalTextVersion Example(Guid tenantId); // versión 0, IsExample
  }
  public sealed class SupportConsent {   // sin mutadores
      public static SupportConsent Record(Guid id, Guid tenantId, SupportTicketId ticketId, SupportLegalTextVersion text,
          string visitorIp, DateTimeOffset now);
  }
  ```
  Códigos nuevos: `support.public.not_found`, `support.public.lookup_failed`, `support.public.captcha_invalid`, `support.public.legal_texts_outdated`, `support.public.form_outdated`, `support.public.consent_required`, `support.public.department_not_found`, `support.public.body_too_large`, `support.public.body_invalid`, `support.form.legal_texts_required`, `support.form.no_published_types`, `support.form.version_taken`, `support.legal_texts.version_taken`, `support.legal_texts.policy_required`, `support.reply.portal_only` (anónimo con otro canal), `support.reply.portal_unavailable` (no anónimo con `Portal`).
  Repositorios (Application): `ISupportTicketAccessRepository { FindAsync(Guid tenantId, SupportTicketId, ct); FindByNumberAsync(Guid tenantId, long number, ct) → (SupportTicket, SupportTicketAccess)?; Add }`, `ISupportFormRepository { CurrentAsync(Guid tenantId, ct) → SupportFormVersion?; FindAsync(Guid id, ct); Add }`, `ISupportLegalTextRepository { CurrentAsync; FindAsync; Add }`, `ISupportConsentRepository { Add }` (sin lectura pública: la fila se lee por SQL en las pruebas).

- [ ] **Paso 1: pruebas**
  - `SupportTicketPublicFormTests`: `AnAnonymousTicketHasNoRequesterNoCustomerAndRepliesByPortal`; `ANamedTicketRepliesByEmail`; `ItHasNoActorAndNoAssignee` (evento `Created` con `ActorMemberId == null`); `ItKeepsTheFormVersion`; `ANitWithoutBusinessNameShowsAsNitAndNumber` (`DisplayName() == "NIT 900123456"`); `ManualAndConversationStillRejectAMissingRequester` (el guard de :183 sigue para esos dos).
  - `SupportFormFieldsTests`: `NaturalAsksNamesNotBusinessName`; `NitSwapsNamesForBusinessNameWithTheTenantRequirement` (con `businessNameRequired: false` ⇒ `businessName` opcional); `HiddenAddressAndLandlineAreNotListed`; `NoDocumentAsksNamesMobileCountryCityAndEmail` (§9.3: exactamente esos seis, obligatorios); `AnonymousAsksNothing`; `TheFixedFieldsAreAlwaysRequired` (celular, país, ciudad, correo, nombres en `Natural`).
  - `SupportTicketAccessTests`: `TheFifthFailureLocksForFifteenMinutes`; `ASuccessResetsTheFailures`; `ReissueKeepsTheLock`; `CanResendOnlyAfterTenMinutes`.
  - `SupportPinTests.ItIsSixDigits` (1 000 generaciones, todas `^\d{6}$`).
  - `SupportLegalTextVersionTests`: `ItNeedsAuthorizationAndAPolicyTextOrUrl` (`support.legal_texts.policy_required`, súmalo a los códigos); `APolicyUrlMustBeHttps`.
  - `SupportPublicFormPersistenceTests` (host propio, migrado): `TheConsentsTableHasNoForeignKey` (`information_schema.table_constraints` sin `FOREIGN KEY` en `support.consents`); `TheMigrationDownKeepsTheLegalTables` (base vacía, `MigrateAsync` hasta `AddSupportPublicForm`, luego `MigrateAsync` a la migración anterior; `information_schema.tables` sigue mostrando `support.consents` y `support.legal_text_versions`); `ARequesterWithCityRoundTrips`; `TheReplyChannelCheckAcceptsPortal`; `TwoFormVersionsWithTheSameNumberAre409` (índice `IX_form_versions_tenant_version` → `support.form.version_taken`, traducido por nombre en `SupportUnitOfWork`; ídem `IX_legal_text_versions_tenant_version`).
- [ ] **Paso 2: RED** — `dotnet build $SU` y `dotnet build $SI` fallan por los tipos nuevos.
- [ ] **Paso 3: implementar** — columnas nuevas en `tickets`: `is_anonymous bool not null default false`, `form_version_id uuid null`, `requester_city_id uuid null`, `requester_city_name varchar(100) null`. Reescribir el CHECK de `reply_channel` (y el de la entrega en `ticket_replies`) con `Portal`. Tablas `ticket_access`, `form_versions`, `legal_text_versions`, `consents` (F7, F14, F16, F17). En el `Down`, comentario: «`consents` y `legal_text_versions` son prueba legal: este Down no las borra (precedente `customers.data_consents`)», y no emitir su `DropTable`. `Pbkdf2SupportPinHasher` en Infrastructure; su señuelo es un hash fijo calculado una vez (campo estático perezoso).
- [ ] **Paso 4: GREEN** — build de la solución limpio.
- [ ] **Paso 5: commit** — `feat(support): model the public form, consult access and legal consents`.

### Task 3: Configuración del formulario y textos legales en la consola

**Files:**
- Create: `Modules.Support.Application/Settings/GetFormSettings.cs`, `UpdateFormSettings.cs`, `GetLegalTexts.cs`, `PublishLegalTexts.cs`; `ISupportPublicOptions.cs`
- Modify: `Modules.Support.Api/SupportEndpoints.cs`, `SupportDtos.cs`, `SupportAuditActions.cs`
- Test: `$SU\FormSettingsValidatorTests.cs`, `$SI\SupportFormSettingsApiTests.cs`

**Interfaces:**
- Consumes: Task 2 (`SupportFormVersion`, `SupportLegalTextVersion`, repositorios).
- Produces: `public interface ISupportPublicOptions { string BaseUrl { get; } string Hostname { get; } }` (enlace `PublicUrl` = `{BaseUrl}/{slug}`); `UpdateFormSettingsRequest(bool? IsPublished, bool? ShowDocument, bool? ShowAddress, bool? ShowLandline, bool? BusinessNameRequired, string? AccentColor, string? IntroText, string? SuccessMessage)`; `PublishLegalTextsRequest(string? AuthorizationText, string? PolicyText, string? PolicyUrl, string? TermsText)`; auditoría `support.form.updated`, `support.legal_texts.published`.

- [ ] **Paso 1: pruebas**
  - `FormSettingsValidatorTests`: `AccentColorIsHashAndSixHex`; `IntroIsAtMost1000AndSuccessAtMost500`; `LegalTextsLimits` (20 000; URL ≤ 500).
  - `SupportFormSettingsApiTests` (host compartido; sólo su tenant): `WithoutRowsTheFormIsUnpublishedWithDefaultsAndBlockers` (`PublishBlockers` contiene `legal_texts_required`); `PublishingWithoutLegalTextsIs422`; `SavingCreatesTheNextVersionAndKeepsThePrevious` (dos `PUT`, dos filas); `AStaleIfMatchIs412` y `WithoutIfMatchIs428`; `LegalTextsGetReturnsTheExampleUntilPublished` (`isExample: true`); `PublishingLegalTextsTwiceKeepsBothVersions`; `AReaderCanSeeButNotSave` (`TicketView` sin `SettingsManage` → `GET` 200, `PUT` 403); `SavingWritesAudit` (`CountAuditEntriesAsync`).
- [ ] **Paso 2: RED** — build de `$SI` falla.
- [ ] **Paso 3: implementar** — el `If-Match` es el número de versión vigente (0 sin filas). Si la versión `n+1` ya existe por carrera, el índice da 409 traducido (Task 2). `PublishBlockers` también incluye `no_published_types` cuando no hay tipo activo y publicado.
- [ ] **Paso 4: GREEN** — build limpio.
- [ ] **Paso 5: commit** — `feat(support): configure the public form and version the legal texts`.

**Revisión del Bloque A** (un revisor): F1, F7, F8, F14–F18, F22 y que ningún método edite o borre `legal_text_versions` ni `consents`.

## Bloque B — API pública (Tareas 4–7, un implementador)

### Task 4: Fontanería del grupo público

**Files:**
- Create: `src/Bootstrapper/SupportPublicPaths.cs`, `src/Bootstrapper/SupportCaptcha.cs`, `SupportCountries.cs`, `SupportPhoneNormalizer.cs`, `SupportGeography.cs`; `Modules.Tenancy.Application/ITenantSlugDirectory.cs` + `Modules.Tenancy.Infrastructure/Persistence/TenantSlugDirectory.cs`; `Modules.Support.Application/Public/SupportPublicResolver.cs`; `Modules.Support.Api/PublicSupportEndpoints.cs` (grupo vacío más `GET /form` stub que devuelve 404 hasta Task 5)
- Modify: `RequireCsrfHeaderMiddleware.cs` (:58-69), `BuyerNoStoreMiddleware.cs` (:16-30), `Cors/CorsServiceCollectionExtensions.cs`, `src/Api/RateLimiterPolicies.cs`, `src/Api/Program.cs` (limitadores tras :190, `MapPublicSupportEndpoints` junto a :339), `src/Api/RequestFailureCapture.cs:43-48`, `src/Api/ExternalRateLimitRejection.cs:20-21`, `appsettings.json`, `appsettings.Development.json`, `k8s/prod-configMap.yaml`, `QepServiceCollectionExtensions.cs`
- Test: `$BU\RequireCsrfHeaderMiddlewareTests.cs`, `$BU\SupportPublicPathsTests.cs`, `$BU\CorsSettingsValidatorTests.cs` (o uno nuevo para la sección de Support), `$SI\SupportPublicSurfaceApiTests.cs`, `$TI\TenantSlugDirectoryTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface ITenantSlugDirectory { Task<TenantId?> FindIdBySlugAsync(string normalizedSlug, CancellationToken ct); }
  public interface ISupportCaptcha { Task<bool> VerifyAsync(string token, string visitorIp, CancellationToken ct); }  // hostname de ISupportPublicOptions
  public interface ISupportCountries { bool IsKnown(string countryCode); }
  public interface ISupportPhoneNormalizer { string? ToE164(string raw, string countryCode); }
  public interface ISupportGeography { Task<IReadOnlyList<SupportGeoItem>> DepartmentsAsync(CancellationToken ct);
      Task<IReadOnlyList<SupportGeoItem>?> CitiesAsync(Guid departmentId, CancellationToken ct);
      Task<SupportGeoItem?> FindCityAsync(Guid cityId, CancellationToken ct); }
  public sealed record SupportGeoItem(Guid Id, string Name);
  public sealed record SupportPublicTenant(Guid TenantId, string Slug, string Name, string Country, SupportFormVersion Form);
  public sealed class SupportPublicResolver { Task<SupportPublicTenant> ResolveAsync(string? slug, CancellationToken ct); } // 404 support.public.not_found
  public static IEndpointRouteBuilder MapPublicSupportEndpoints(this IEndpointRouteBuilder e, string readPolicy, string submitPolicy, string lookupPolicy);
  RateLimiterPolicies.SupportPublicRead = "support-public-read"; SupportPublicSubmit = "support-public-submit"; SupportPublicLookup = "support-public-lookup";
  ```
  Configuración `Support:PublicForm { BaseUrl, Hostname, AllowedOrigins[] }` con validador `ValidateOnStart` (orígenes como `CorsSettingsValidator`; en `Production`, `AllowedOrigins` no vacío y sin `#{…}#` sin resolver). Producción: `Support__PublicForm__AllowedOrigins__0 = https://soporte.lulocrm.com` en el ConfigMap.

- [ ] **Paso 1: pruebas**
  - `RequireCsrfHeaderMiddlewareTests.ThePublicSupportPrefixSkipsTheCsrfHeader`; ampliar `ANearMissPrefixStillNeedsTheCsrfHeader` con `/api/v1/public/supportx/a` y `/api/v1/public/support` (sin barra).
  - `SupportPublicPathsTests.ThePrefixMatchesTheEndpointGroup` — el literal de Bootstrapper empieza el patrón del grupo (lee el `RoutePattern` del endpoint registrado, vía `EndpointDataSource` de un host mínimo o una constante pública del `.Api` que la prueba sí ve).
  - `TenantSlugDirectoryTests.FindsByNormalizedSlugAndNullForUnknown`.
  - `SupportPublicSurfaceApiTests` (host propio con `WithRealAuthentication` copiado al harness de Support y `Support:PublicForm:AllowedOrigins:0 = https://soporte.example.com`, `Cors:AllowedOrigins:0 = https://console.example.com`):
    - `UnknownSlugInactiveTenantModuleOffAndUnpublishedAreTheSame404` (cuatro casos, mismo status, código y cuerpo).
    - `EveryPublicResponseIsNoStoreEvenOn404And413And429`.
    - `ThePublicGroupIgnoresTheSessionCookieWithRealAuth` (Review Focus 3).
    - `ThePublicPreflightAllowsTheSupportOriginWithoutCredentials` y `TheSupportOriginGetsNoCorsOnTenantRoutes` (Review Focus 3); `TheConsoleOriginGetsNoCorsOnThePublicGroup`.
    - `ABodyOver64KbIs413` (código `support.public.body_too_large`); `AMalformedJsonBodyIs400`.
    - `TheLookupLimiterIs429OnTheEleventhAndBucketsArePerIp` (dos IPs por `X-Real-IP` con `ForwardedHeaders:KnownNetworks` del test, precedente `ForwardedHeadersApiTests`).
- [ ] **Paso 2: RED** — builds de `$BU`, `$SI`, `$TI` fallan.
- [ ] **Paso 3: implementar** — grupo: `MapGroup("/api/v1/public/support/{tenantSlug}").WithTags("Support public").AllowAnonymous().RequireCors("support-public").WithMetadata(new RequestSizeLimitAttribute(65_536)).RequireRateLimiting(readPolicy)`. Lector de cuerpo acotado privado en `PublicSupportEndpoints` copiado del de Buyers (`PublicBuyerEndpoints.cs:407`), con los códigos `support.public.*`. `BuyerNoStoreMiddleware` suma `|| SupportPublicPaths.IsSupportPublic(path)` con comentario. Los limitadores particionan por `RemoteIpAddress ?? "unknown"`.
- [ ] **Paso 4: GREEN** — build limpio.
- [ ] **Paso 5: commit** — `feat(support): open the anonymous public support route group`.

### Task 5: Configuración pública y geografía

**Files:**
- Create: `Modules.Support.Application/Public/GetPublicForm.cs`, `ListPublicGeography.cs`, `SupportPublicDtos.cs`; puerto `ISupportTenantBranding` (`Task<string?> LogoUrlAsync(Guid tenantId, ct)`) con adaptador `src/Bootstrapper/SupportTenantBranding.cs` (calco de `StoreTenantLookup`: `GetLogoFileIdAsync` → `IFileResourceRepository` con chequeo de tenant → `ITenantLogoStorage.GetUrl`)
- Modify: `PublicSupportEndpoints.cs`
- Test: `$SI\SupportPublicFormApiTests.cs`

**Interfaces:**
- Consumes: Task 4 (`SupportPublicResolver`, `ISupportGeography`), Task 2 (`SupportFormFields`), Task 3 (versión vigente).
- Produces: los DTO públicos del Contrato.

- [ ] **Paso 1: pruebas** — `TheFormListsOnlyActivePublishedTypesInOrder`; `TheFieldsFollowTheFormVersion` (oculta dirección ⇒ no aparece en ninguna variante); `TheDocumentTypesComeFromTheSharedTable` (`CO` = `NIT, CC, CE, PASAPORTE`; `*` = `TAX_ID, PASAPORTE`); `TheLegalTextsAreTheCurrentVersion`; `TheDefaultCountryIsTheTenantCountry`; `CitiesOfAnUnknownDepartmentAre404`; `GeographyIsAlso404ForAnUnpublishedForm`.
- [ ] **Paso 2: RED** — build de `$SI` falla.
- [ ] **Paso 3: implementar.**
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): serve the public form configuration`.

### Task 6: Radicar desde el formulario

**Files:**
- Create: `Modules.Support.Application/Public/FilePublicTicket.cs` (comando, validador, handler), `ISupportOpeningEmail.cs`; `Modules.Notifications.Application/SupportOpeningEmailTemplate.cs`; `src/Bootstrapper/SupportOpeningEmailSender.cs`
- Modify: `ISupportCustomerLookup.cs` + `src/Bootstrapper/SupportCustomerLookup.cs` (`FindIdsByDocumentAsync`); `GetTicket` / `SupportTicketDtoComposer` (`IsAnonymous`, `CustomerCandidates`, `ConsultAccess`)
- Test: `$SU\FilePublicTicketValidatorTests.cs`, `$NU\SupportOpeningEmailTemplateTests.cs`, `$SI\SupportPublicFilingApiTests.cs`, `$SI\SupportBootstrapperAdaptersTests.cs` (+1)

**Interfaces:**
- Consumes: Tasks 2, 4, 5.
- Produces:
  ```csharp
  Task<IReadOnlyList<Guid>> FindIdsByDocumentAsync(Guid tenantId, string documentType, string documentNumber, CancellationToken ct); // en ISupportCustomerLookup
  public sealed record SupportOpeningEmail(string ToAddress, string TenantName, string TicketNumber, string TypeName,
      DateOnly? DueOn, string ConsultUrl, string Pin);
  public interface ISupportOpeningEmail { Task<SupportChannelSendResult> SendAsync(SupportOpeningEmail email, CancellationToken ct); }
  ```

- [ ] **Paso 1: pruebas**
  - `FilePublicTicketValidatorTests` (mapa `errors` con las claves del DTO): `ANaturalRequesterNeedsNamesMobileCountryCityAndEmail`; `ANitRequesterNeedsBusinessNameOnlyWhenTheFormSaysSo`; `TheDocumentTypeMustBeAllowedForTheRequesterCountry` (`NIF` con `CO` → error en `Requester.DocumentType`); `ColombiaNeedsACityIdOtherCountriesACityName`; `AnAnonymousBodyMayNotCarryARequester`; `SubjectAndDescriptionLimits` (200 / 5 000); `TheConsentMustBeAccepted` (no anónimo).
  - `SupportOpeningEmailTemplateTests`: `ItCarriesTheNumberThePinAndTheLink`; `ItHtmlEncodesTheTenantName`; `WithoutTermItSaysSinFechaLimite`; `TheTextTutea`.
  - `SupportPublicFilingApiTests` (host propio por clase con `FakeSupportCaptcha` y `RecordingSupportOpeningEmail`):
    - `FilingReturnsTheNumberAndASixDigitPinOnceAndStoresOnlyTheHash` (en BD `ticket_access.pin_hash` ≠ PIN y `Verify` acierta).
    - `ANamedTicketEmailsThePinToTheRequester` (`PinEmailed: true`).
    - `AFailedEmailStillFilesTheTicket` (`PinEmailed: false`, `last_failure_code = send_failed`).
    - `AnAnonymousTicketStoresNoRequesterNoIpAndNoConsent` (Review Focus 5).
    - `TheConsentKeepsTheTextTheRequesterAccepted` (Review Focus 5).
    - `AnOutdatedLegalTextVersionIs422` y `AnOutdatedFormVersionIs422` (`support.public.form_outdated`).
    - `ABadCaptchaIs422AndFilesNothing`.
    - `AnUnpublishedOrInactiveTypeIs422`.
    - `OneMatchingCustomerIsLinkedAndTwoAreLeftAsCandidates` (el detalle de la consola trae `CustomerCandidates`).
    - `WithCustomersOffNoLookupHappens`.
    - `TheTicketIsPublicFormWithoutActorAndAuditedAsSystem`.
    - `ThePinNeverReachesLogsAuditOrEvents` (logger en memoria a `Trace`, `audit.entries`, `ticket_events.details`: ninguno contiene el PIN).
  - `SupportBootstrapperAdaptersTests.FindIdsByDocumentUsesTheNormalizedNumberAndSkipsMerged` (contra el esquema migrado: `900.123.456` casa con `900123456`; un fusionado no sale; un `Incomplete` sí).
- [ ] **Paso 2: RED** — builds de `$SU`, `$NU`, `$SI` fallan.
- [ ] **Paso 3: implementar** — flujo del handler: resolver → captcha (`VisitorIp` = `RemoteIpAddress`) → validar versiones de formulario y textos → transacción → `EnsureAsync` del aprovisionador → número → buscar cliente (si `customers` prendido y hay documento) y `HoldCustomerReferenceAsync` si hay uno → `CreateFromPublicForm` → `SupportTicketAccess.Issue` → `SupportConsent.Record` (no anónimo) → auditoría `PublishSystem` → commit → correo de apertura (no anónimo) → `RecordDelivery` en un segundo guardado de `ticket_access` (sin tocar el ticket). El adaptador del correo calca `SupportReplyEmailSender` (excepción ⇒ `Failed(send_failed)`, log sólo con el tipo).
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): file tickets from the public form`.

### Task 7: Consulta, reenvío del PIN y respuesta por la página

**Files:**
- Create: `Modules.Support.Application/Public/LookupPublicTicket.cs`, `ResendPublicPin.cs`; `Modules.Support.Application/Opening/SupportOpeningSender.cs` (el reparto por canal de F10; en este bloque sólo correo; el hilo lo suma Task 8)
- Modify: `Replies/SendTicketReply.cs` (`RouteAsync` :228: `Portal`), `Replies/GetReplyOptions.cs` (`Portal`), `SupportDtos.cs`
- Test: `$SI\SupportPublicLookupApiTests.cs`, `$SI\SupportTicketRepliesApiTests.cs` (+3)

**Interfaces:**
- Consumes: Tasks 2, 4, 6 (`ISupportOpeningEmail`).
- Produces: `public sealed class SupportOpeningSender { Task<SupportOpeningOutcome> SendAsync(SupportTicket ticket, string pin, string consultUrl, CancellationToken ct); }`; `public sealed record SupportOpeningOutcome(string Channel, string? FailureCode, bool Sent);`. Canal registrado en `last_sent_channel`: `Email`, `Thread`, `None`.

- [ ] **Paso 1: pruebas**
  - `SupportPublicLookupApiTests` (host propio por clase, reloj movible como `ClockedSupportHost`):
    - `TheRightPinShowsStatusDueAndOnlySentReplies` (una `Failed` y una `Pending` no salen).
    - `AnUnknownNumberAWrongPinAndALockedTicketAnswerTheSame` (Review Focus 1).
    - `FiveWrongPinsLockTheTicketEvenForTheRightPin` y que a los 15 min vuelve a abrir (Review Focus 2).
    - `ALockedTicketDoesNotLockAnother` (Review Focus 2).
    - `TheNumberAcceptsWithAndWithoutPrefix` (`SOP-000127` y `127`, por `SupportTicketNumber.TryParse`).
    - `ATicketOfAnotherTenantIsNotFoundUnderThisSlug`.
    - `ResendPinAnswersTheSameAndOnlyWritesToTheRegisteredEmail` (Review Focus 4: inexistente, anónimo, en enfriamiento y válido responden `202` vacío; sólo el válido manda un correo, al correo de la foto; el PIN viejo deja de servir).
    - `ResendPinWithinTenMinutesSendsNothing`.
    - `ABadCaptchaOnLookupIs422CaptchaInvalid`.
  - `SupportTicketRepliesApiTests`: `AnAnonymousTicketRepliesOnlyByPortalAndTheReplyIsSentAtOnce`; `PortalIsRejectedOnANamedTicket` (`support.reply.portal_unavailable`); `ReplyOptionsOfAnAnonymousTicketOfferOnlyPortal`.
- [ ] **Paso 2: RED** — build de `$SI` falla.
- [ ] **Paso 3: implementar** — la consulta: captcha → resolver → `FindByNumberAsync`; si no hay, `Verify(null, pin)` (señuelo) y `lookup_failed`; si bloqueado, `lookup_failed` sin verificar; si falla, `RecordFailure` y guardar; si acierta, `RecordSuccess` y devolver. El guardado de `ticket_access` no toca `tickets.version`. `resolve: true` con `Portal` resuelve igual que un `Sent`.
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): consult a ticket by number and pin`.

**Revisión del Bloque B** (un revisor): Review Focus 1–5, F3–F13, F17, F19–F23, y que el grupo no lea nunca `IExecutionContext`.

## Bloque C — Apertura por el canal del ticket (Tarea 8, un implementador)

### Task 8: Información del ticket con el PIN por su canal

**Files:**
- Modify: `Opening/SupportOpeningSender.cs` (hilo, autor F11), `Conversations/CreateTicketFromConversation.cs` (PIN y apertura tras el commit), `Tickets/CreateTicket.cs` (PIN siempre; correo de apertura con canal `Email`), `ResendPublicPin.cs` (correo e hilo)
- Test: `$SI\SupportOpeningApiTests.cs`, `$SU\SupportOpeningSenderTests.cs`

**Interfaces:**
- Consumes: Task 7 (`SupportOpeningSender`), `ISupportReplyChannels.SendThreadAsync` (fase 2). Messaging **no** cambia.

- [ ] **Paso 1: pruebas**
  - `SupportOpeningSenderTests` (dobles): `InsideTheWindowItSendsTextWithTheNumberAndPin`; `OutsideTheWindowItSendsNothingAndRecordsWindowClosed`; `TheAuthorIsTheCreatorThenTheAssigneeThenAManager` (F11); `WithoutAnyAuthorItRecordsNoAuthor`; `AnAnonymousTicketSendsNothing`; `TheTextTuteaAndCarriesTheLinkWithoutThePin` (el enlace nunca lleva el PIN, F29).
  - `SupportOpeningApiTests`: `ATicketFromAConversationSendsItsInformationWithThePin` (doble de canales registra un envío con el PIN); `AFailedOpeningStillCreatesTheTicket` (el `201` llega y `ConsultAccess.FailureCode` lo cuenta); `AManualTicketByEmailEmailsTheOpening`; `AManualTicketByWhatsAppSendsNothing` (F26); `ResendPinOfAConversationTicketGoesToItsThread`; `TheCreationResponseVersionIsStillValidAfterTheOpening` (un `PUT` de estado con la `version` del `201` no da 412: la apertura no sube la versión del ticket).
- [ ] **Paso 2: RED** — builds de `$SU`, `$SI` fallan.
- [ ] **Paso 3: implementar** — texto en ventana (tuteando): `Registramos tu caso {número} ({tipo}). Fecha límite de respuesta: {fecha}. Consulta su estado en {enlace} con la clave {pin}.`; sin término, `sin fecha límite`; la fecha en el formato regional del tenant (`ITenantDirectory.GetRegionalSettingsAsync`). `ClientId` determinista por ticket y emisión del PIN (`pin_issued_at`), para que un reintento no duplique.
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): send the ticket information with its pin through the ticket channel`.

**Revisión del Bloque C** (un revisor): F11, F25, F26, F29.

## Bloque D — Adjuntos del formulario público (Tareas 9–10, un implementador)

Decisiones del bloque (planner, sobre la respuesta del owner a P-4 «implementar»):

| # | Decisión | Por qué |
| --- | --- | --- |
| F30 | Los adjuntos van por el pipeline de **Storage** (`FileResource`, staging, `FileContentInspector`, `IFileScanner`, promoción), no por el staging de Messaging. Tipo de dueño nuevo `FileOwnerType.SupportTicket` (al final; varchar por nombre, sin migración de Storage). | Es el único pipeline con inspección de contenido, antivirus, cuarentena y limpieza de staging. |
| F31 | Puerto `IStorageSystemUploads` en `Modules.Storage.Application`, sin `IExecutionContext`. `CompleteUploadHandler` se parte: la autorización del miembro queda en el handler y el resto pasa a un núcleo común (`UploadCompletion`) que usan los dos. Support lo ve por su puerto `ISupportAttachmentStorage` con adaptador en Bootstrapper. | Un handler anónimo no puede pasar `StorageAuthorization`; duplicar la verificación de contenido divergiría. |
| F32 | La sesión la ata un secreto: `ownerId` es un `Guid.NewGuid()` (v4) que sólo recibe quien abrió la sesión y viaja como `uploadKey`. Completar y radicar exigen el par `(fileId, uploadKey)` del mismo tenant; un par que no casa responde igual que un id inexistente (`422 support.public.attachment_invalid`). | Los ids de Storage son v7 (adivinables por tiempo); el secreto aparte impide reclamar la subida de otro. |
| F33 | Rutas públicas nuevas: `POST /attachments` (`{ name, mimeType, sizeBytes }` → `{ fileId, uploadKey, uploadUrl, headers }`) y `POST /attachments/{fileId:guid}/complete` (`{ uploadKey }` → `{ status }`: `available` o `rejected`). Sin captcha (el token de Turnstile es de un solo uso y se gasta al radicar); limitador nuevo `SupportPublicUpload` 20 cada 10 min por IP. | La pantalla avisa del archivo rechazado antes de radicar. |
| F34 | Límites en `SupportPublicAttachmentRules` (Support.Domain): máximo 5 archivos por ticket, 10 MB cada uno, tipos de `FileUploadPolicy` (pdf, doc, docx, xls, xlsx, jpg, jpeg, png, webp), nombre ≤ 260. `SupportAttachmentParityTests` (`$BU`) amarra los tipos con `FileUploadPolicy`. | 25 MB de Storage es mucho para un formulario anónimo. |
| F35 | `support.ticket_attachments` (`id`, `tenant_id`, `ticket_id` FK con cascada, `file_id`, `file_name`, `mime_type`, `size_bytes`, `created_at`), único `IX_ticket_attachments_file` sobre `file_id`. Radicar con `attachments: [{ fileId, uploadKey }]` exige cada archivo `Available`, de dueño `SupportTicket` con ese `uploadKey`; se inserta en la misma transacción del ticket. Un anónimo también puede adjuntar. | El índice único es lo que impide que dos radicaciones reclamen el mismo archivo; se traduce por nombre a `support.public.attachment_invalid`, no con consulta previa. |
| F36 | `IFileReferenceProbe` de Support (Bootstrapper) consulta `support.ticket_attachments.file_id`, para que la limpieza de staging no borre un adjunto reclamado. Lo no reclamado lo purga `StagingCleanupProcessor` a las 24 h. | Precedente de los comprobantes de pago. |
| F37 | Consola: `SupportTicketDetailDto` suma `IReadOnlyList<SupportAttachmentDto> Attachments` (`SupportAttachmentDto(Guid Id, string FileName, string MimeType, long SizeBytes)`) y `GET …/tickets/{id}/attachments/{attachmentId}/download` (`SupportPolicies.TicketView`, revalidado en el handler) responde `{ url }` firmada de 5 min. | El asesor de Soporte no necesariamente tiene `storage.file.read`. |

### Task 9: Subida sin miembro en Storage

**Files:**
- Modify: `Modules.Storage.Domain/FileResourceEnums.cs` (`SupportTicket` al final), `FileOwnerModuleGuard` (`SupportTicket` → módulo `support`), `CompleteUpload.cs` (parte el núcleo)
- Create: `Modules.Storage.Application/IStorageSystemUploads.cs`, `StorageSystemUploads.cs`, `UploadCompletion.cs`
- Test: unitarias e integración de Storage (`StorageSystemUploadsTests`, `StorageSystemUploadsApiTests`)

**Interfaces:**
- Produces:
  ```csharp
  public interface IStorageSystemUploads {
      Task<SystemUploadSession> OpenAsync(Guid tenantId, FileOwnerType ownerType, Guid ownerId, string name, string mimeType, long sizeBytes, long maxSizeBytes, CancellationToken ct);
      Task<SystemUploadResult> CompleteAsync(Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken ct);
      Task<SystemFileInfo?> FindAvailableAsync(Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken ct);
      Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken ct);
  }
  public sealed record SystemUploadSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);
  public enum SystemUploadOutcome { Available, Rejected, NotFound, ObjectMissing }
  public sealed record SystemUploadResult(SystemUploadOutcome Outcome);
  public sealed record SystemFileInfo(Guid FileId, string Name, string MimeType, long SizeBytes);
  ```
- [ ] **Paso 1: pruebas** — `OpenValidatesTheDeclarationWithTheGivenMax`; `CompleteWithAnotherOwnerIdIsNotFound`; `CompleteOfAMismatchedContentIsRejected`; `CompleteOfACleanFilePromotesIt`; `FindAvailableIgnoresPendingAndQuarantined`; `TheMemberCompletionStillRequiresThePermission`.
- [ ] **Paso 2: RED.**
- [ ] **Paso 3: implementar** — mismo orden de verificaciones del handler (stat, tamaño, magic bytes, antivirus, variantes, promoción, auditoría con actor sistema).
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(storage): add system upload sessions without a member`.

### Task 10: Adjuntos en el formulario y en la consola

**Files:**
- Create: `Support.Domain/SupportTicketAttachment.cs`, `SupportPublicAttachmentRules.cs`; `Support.Application/Public/OpenPublicAttachment.cs`, `CompletePublicAttachment.cs`, `ISupportAttachmentStorage.cs`; `Tickets/GetTicketAttachmentDownload.cs`; `src/Bootstrapper/SupportAttachmentStorage.cs`, `SupportAttachmentReferenceProbe.cs`; migración `AddSupportTicketAttachments`
- Modify: `FilePublicTicket.cs` (+`attachments`), `PublicSupportEndpoints.cs`, `SupportEndpoints.cs`, `SupportTicketDetailDto`, `RateLimiterPolicies.cs` + `Program.cs` (`SupportPublicUpload`), `SupportDbContext.cs`, `SupportUnitOfWork` (traducción de `IX_ticket_attachments_file`)
- Test: `$SU\SupportPublicAttachmentRulesTests.cs`, `$SI\SupportPublicAttachmentsApiTests.cs`, `$BU\SupportAttachmentParityTests.cs`

- [ ] **Paso 1: pruebas** — `AFiledTicketKeepsItsAttachments`; `AnAttachmentWithAnotherUploadKeyIsRejectedLikeAnUnknownOne`; `TheSameFileCannotBeClaimedByTwoTickets`; `APendingOrQuarantinedFileIsRejected`; `SixFilesAreRejected`; `AFileOverTenMegabytesIsRejectedAtOpen`; `AnAnonymousTicketCanAttach`; `TheDownloadNeedsTicketViewAndAnotherTenantGets403`; `TheReferenceProbeKeepsAClaimedFile`; `TheUploadRoutesAreNoStoreAndRateLimited`; paridad de tipos con `FileUploadPolicy`.
- [ ] **Paso 2: RED.**
- [ ] **Paso 3: implementar** (F30–F37).
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): accept attachments on the public form`. Agregar al README § Soporte: rutas públicas (con adjuntos), configuración `Support:PublicForm`, slugs reservados y su consulta previa, límites de adjuntos. Agregar a `CLAUDE.md` del repo, en «Gotchas verificados»: el grupo público no se prueba con el stub de auth; `consents`/`legal_text_versions` son prueba legal (sin FK, sin `Down`); el PIN nunca sale a logs, auditoría ni eventos; `uploadKey` es el único vínculo entre la subida anónima y quien la abrió.

**Revisión del Bloque D** (un revisor): F30–F37, que ningún camino anónimo debilite `StorageAuthorization`, y que el `CompleteUploadHandler` de la consola conserve su permiso.

**Revisión final** (modelo más capaz, toda la rama contra `develop`): arquitectura (`SupportLayerTests`, ningún `Modules.Messaging.*` ni `Modules.Storage.*` en Support), seguridad del grupo público (CORS, CSRF, cookie, enumeración, PIN) y prueba legal.

## Lo que esta fase deja preparado

- `SupportReplyChannel.Portal` y la página de consulta: la fase 4 cuelga ahí el PDF de la respuesta y la calificación (`calificar` ya está reservado).
- `SupportOpeningSender` como único camino del PIN (correo o texto en ventana): la prórroga y los avisos de vencimiento de la fase 4 pueden reusar su reparto por canal.

## Decisiones del owner (2026-11-15)

- **P-1 Turnstile:** reusar el widget y el secreto de la tienda (F28).
- **P-2 Plantilla:** no se construye; el PIN por WhatsApp va como texto dentro de la ventana (F24, F25).
- **P-3 Ticket manual por WhatsApp sin conversación:** se deja así (F26).
- **P-4 Adjuntos:** entran (Bloque D, F30–F37).
- **P-5 Slugs y limitadores:** sin respuesta; quedan los del plan (F1, F6).
- **P-6 País y ciudad fuera de Colombia:** aprobada (F19).

Pendientes de la fase 2 que **no** se resuelven aquí y no chocan con esta fase: FR-I2, cobertura de R9, P3–P5 y vocabulario de `failureCode`. Toca el vocabulario de `failureCode` sólo en que `ticket_access.last_failure_code` reusa los códigos de `SupportChannelReasons` más `no_author` y `send_failed`; si el owner cambia el vocabulario, cambia también aquí.

## Spec discrepancies (spec contra código)

1. §9.3 pide país y ciudad «del módulo Geography»: Geography sólo tiene departamentos y ciudades de Colombia y no hay catálogo de países (`GeographyEndpoints.cs:20-25`). Ver F19.
2. §3 cita `Tenant.cs:365` `ValidateSlug` como «3 a 63 minúsculas»: también acepta guiones al inicio, al final y dobles (no hay regex). Esta fase no lo endurece.
3. §9.4 da como precedente de bloqueo «cinco intentos por radicado»: el repo no tiene ningún contador de intentos (sólo limitadores por IP). `ticket_access` es el primero.
