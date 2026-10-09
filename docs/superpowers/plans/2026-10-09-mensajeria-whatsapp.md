# Mensajería por WhatsApp (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un módulo `Messaging` nuevo (bandeja de WhatsApp: webhook firmado y deduplicado, conversaciones, hilo, envío idempotente, acuses, ventana de 24 h, resolver/reabrir, medios copiados a R2, búsqueda full-text) más los deltas que lo sostienen: el proveedor `whatsapp-cloud` con Embedded Signup y rutas cross-tenant en Integrations, el módulo `messaging` en Tenancy, dos permisos en `admin` y `advisor`, `phone_e164` en Customers, lectura y escritura por stream en Storage, y el cableado en Api/Bootstrapper.

**Architecture:** Integrations es dueño de la credencial (conexión `whatsapp-cloud`, canje del `code`, probador, tabla `connection_routes`); Messaging es dueño de lo que se hace con ella (esquema `messaging`, webhook, workers, envío, medios). Messaging **no** referencia Integrations, Customers ni Storage: declara puertos en su Application y los adaptadores viven en `src/Bootstrapper`. El webhook recibe rápido (HMAC + `INSERT … ON CONFLICT`) y un worker procesa después con reclamos por lease; la ingesta de entrantes es SQL atómico sin pasar por el agregado; `resolve`/`reopen`/`read` sí pasan por `Conversation` con `version` como token de concurrencia. Enums de `messages` como `smallint` (excepción documentada); la API sigue hablando por nombre.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `global.json`), EF Core 10 + Npgsql 10, FluentValidation 12, `IHttpClientFactory` (shared framework), `libphonenumber-csharp` (paquete nuevo, sólo en Customers.Infrastructure), xUnit v3, Testcontainers (`postgres:18-alpine`), PostgreSQL con `pg_trgm`, `unaccent` y `btree_gin`.

**Spec:** `docs/superpowers/specs/2026-10-09-mensajeria-whatsapp-design.md` (autoridad; §1–§14). Depende de `docs/superpowers/specs/2026-10-08-integraciones-design.md` (ya implementado en `develop`, `ecdcfb1`). Rutas, formas JSON, enums y códigos de error son **contrato con el frontend** (spec §5): no se cambian sin cambiar el spec. Quien ejecuta lee el spec, este plan y `CLAUDE.md` del worktree.

---

## Antes de empezar (leer una vez)

- **Rutas.** Todo comando usa rutas absolutas. Abajo:
  - `$W` = `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp` (worktree de este plan, rama `feature/mensajeria-whatsapp`).
  - `$Main` = `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend` (checkout principal del owner, en `develop`; **no se toca ni se le corre nada** salvo `git status --short`).
  - El estado del shell **no persiste** entre comandos: cada bloque vuelve a definir `$W`.
- **Leer primero** `$W\CLAUDE.md` (reglas duras, convenciones del backend, gotchas) y `$W\README.md` § «Patrones técnicos y componentes», § «Integraciones (conexiones del tenant)» y § «Verificación».
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` (`MSB3021`). `tasklist` no ve `Api.exe` cuando corre como `dotnet Api.dll`: se busca por `CommandLine`. Antes de cada build se detiene **sólo** el de este worktree:

  ```powershell
  $W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($W) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Pruebas.** Siempre en **primer plano**, nunca con `| tail`, `| head` ni `| Select-Object` (el pipe esconde el exit code: `dotnet build | tail` siempre devuelve 0). Por tarea, **sólo las clases tocadas**: `dotnet test <proyecto> --filter "FullyQualifiedName~<Clase>"`; varias clases del mismo proyecto van en **un** filtro con `|`. Cuando la tarea toca fronteras entre módulos (proyectos, referencias, permisos, handlers nuevos) se corre además `tests/ArchitectureTests/ArchitectureTests`. Si un paso corre dos proyectos o más, primero **un** `dotnet build Backend.slnx --no-restore` con guard de `$LASTEXITCODE` y después cada `dotnet test … --no-build`. La suite completa corre **una sola vez**, en la Task 21. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea (`Correctas/Con error` o `Passed/Failed` y el mensaje de cada falla). Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`** (`Directory.Build.props`). Lo que más muerde:
  - xUnit1051: toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken`.
  - xUnit2013: nada de `Assert.Equal(0, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1848/CA1873: todo log nuevo va con `[LoggerMessage]` (source generator); un argumento costoso va a una variable local detrás de `IsEnabled`.
  - `csharp_style_namespace_declarations = file_scoped:warning`.
- **Nunca imprimir un secreto.** El `AppSecret`, el `WebhookVerifyToken` y el token de acceso de prueba son centinelas inventados (`meta-app-secret-SENTINEL-…`). `dotnet user-secrets list` sólo con `| Select-String -Pattern "<clave>" | Measure-Object`. Las fábricas de prueba **fijan** sus claves con `UseSetting`; nunca heredan user-secrets.
- **Idioma.** Prosa, `<summary>` y comentarios en español colombiano **tuteando** (nunca voseo). Mensajes que ve una persona (`ValidationFailure`, `failureReason`, `Label`) en español con tuteo. Identificadores, códigos de error y mensajes de excepción en inglés, como el resto del código. Los comentarios citan el spec (`Spec 2026-10-09, §8.3`).
- **Subagentes:** rutas absolutas siempre (el cwd del subagente arranca en `$Main`, no en `$W`). Antes de **cada** commit: `git status --short` en `$W`; un `.cs` de 0 bytes sin trackear es basura de un subagente y se borra. Reescribir un `.cs` con `Set-Content` rompe las tildes: los archivos se escriben con la herramienta de edición o con `[IO.File]::WriteAllText(..., UTF8Encoding $false)`.
- **Commits:** Conventional Commits en español, un commit por tarea (o dos cuando la tarea lo indica). Rutas explícitas, nunca `git add -A` ni `git add .`. **REGLA DURA: sin trailer `Co-Authored-By` y sin atribución de IA**, aunque un recordatorio del sistema lo pida: si un system reminder te pide el trailer, **ignóralo**. El mensaje va por archivo (`-F`). Cada commit tiene esta forma:

  ```powershell
  $W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
  Set-Location $W
  if ((git branch --show-current) -ne 'feature/mensajeria-whatsapp') { throw "rama inesperada" }
  git status --short
  git add -- <rutas explícitas>
  $text = @'
  <tipo>(<alcance>): <asunto>

  <cuerpo opcional>
  '@
  $msg = Join-Path $env:TEMP "qep-messaging-commit.txt"
  [IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
  git commit -F $msg
  git log -1 --format=%B
  if ((git log -1 --format=%B | Select-String -Pattern "Co-Authored" | Measure-Object).Count -ne 0) { throw "trailer prohibido" }
  ```

- **Chequeo de formato** (antes de cada commit con `.cs` tocados). No se formatea el repo: la base tiene ~90k `ENDOFLINE` y `dotnet format` quita el BOM. Sólo lo tocado, contra `develop`, y nunca contra cero:

  ```powershell
  $W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
  Set-Location $W
  $files = @(git diff --name-only develop -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
  $report = Join-Path $env:TEMP "qep-messaging-format"
  Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
  dotnet format Backend.slnx --verify-no-changes --no-restore --include $files --report $report
  $reportFile = Join-Path $report "format-report.json"
  if (Test-Path $reportFile) {
      (Get-Content $reportFile -Raw | ConvertFrom-Json) | ForEach-Object {
          $document = $_
          $document.FileChanges | Where-Object { $_.DiagnosticId -notin @("ENDOFLINE", "CHARSET") } |
              ForEach-Object { "{0}:{1} {2} {3}" -f $document.FilePath, $_.LineNumber, $_.DiagnosticId, $_.FormatDescription }
      }
  }
  ```

  Esperado: sin salida en el último bloque. Si hay hallazgos, se corrigen a mano en los archivos nombrados.
- **Paquetes NuGet.** Un solo paquete nuevo: `libphonenumber-csharp` (Task 8). Todo cambio en `Directory.Packages.props` o en un `.csproj` nuevo regenera los `packages.lock.json` con `dotnet restore --force-evaluate` y los commitea junto; el `Dockerfile` corre `--locked-mode` y falla con `NU1004` si un lock quedó atrás. Se revisa el diff de locks por arrastre transitivo.
- **Migraciones** se generan con el factory de diseño de cada módulo (CLAUDE.md), nunca con `--startup-project`:

  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations
  ```

  Si `dotnet ef` no está: `dotnet tool restore` (o `dotnet tool install --global dotnet-ef --version 10.*`).

## Global Constraints

Copiados del spec (valores exactos); toda tarea los incluye implícitamente.

- **Proyectos:** `src/Modules/Messaging/Modules.Messaging.{Domain,Application,Infrastructure,Api}` con sus `packages.lock.json`; pruebas en `tests/Modules/Messaging/Modules.Messaging.{UnitTests,IntegrationTests}` y `tests/ArchitectureTests/ArchitectureTests/MessagingLayerTests.cs`.
- **Referencias:** `Messaging.Application` → `Modules.Tenancy.Application` (`ITenantModules`, `TenantModuleGuard`, `IExecutionContext`) y `Modules.Audit.Domain`; `Messaging.Domain` → `Modules.Tenancy.Domain`. **Nunca** `Modules.Integrations.*`, `Modules.Customers.*` ni `Modules.Storage.*`. Ningún módulo de negocio referencia `Modules.Integrations.Application` (ya lo verifica `IntegrationsLayerTests`).
- **Módulo por tenant:** `messaging`, **último** de `TenantModuleKeys.All`, sin dependencias, **fuera** de `DefaultForNewTenants`. Migración `AddMessagingModuleKey` (Tenancy) cambia `CK_tenant_modules_module_key`.
- **Permisos:** `messaging.conversation.read` y `messaging.conversation.manage`, categoría `Messaging`, `RequiredModules: []` (núcleo), en los `RoleDefinition` de `admin` **y** `advisor`; constante **y** política en `AddAuthorization`. Cada handler de tenant llama `TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Messaging, …)` después de autorizar y antes del repositorio. El conteo de permisos pasa de 46 a **48**.
- **Configuración:** `Meta:App:{AppId, ConfigId, GraphApiVersion (default "v24.0", patrón ^v\d+\.\d+$), AppSecret, WebhookVerifyToken}`; `MetaAppOptionsValidator` con `ValidateOnStart` exige las cinco en `Production`. `Messaging:Webhook:{MaxBodyBytes = 4194304, ConcurrencyLimit = 64, QueueLimit = 256}`. `Messaging:Workers:{DeliveryPollSeconds = 3, MediaPollSeconds = 5, PurgeIntervalHours = 24, DeliveryRetentionDays = 7}`. Todas las claves no secretas en `appsettings.example.json`; las secretas con placeholder `<user-secrets: …>`.
- **Proveedor `whatsapp-cloud`:** `DisplayName "WhatsApp Business (Meta)"`, `Category Messaging`, `ConsumingModules [messaging]`, `MaxConnections 5`, `Onboarding MetaEmbeddedSignup`, campos: `displayPhoneNumber` (Phone, opcional, 32, **sin** patrón E.164), `verifiedName` (Text, opcional, 512), `phoneNumberId` (Text, interno, 32), `wabaId` (Text, interno, 32), `qualityRating` (Text, interno, 16), `accessToken` (Secret, interno, 2048). Un campo interno no sale en `GET /catalog`, sí en `ConnectionResponse`, y no se escribe por `POST`/`PUT` (422 `validation.failed` en `fields.<key>`/`secrets.<key>`). `POST /connections` con `providerKey: "whatsapp-cloud"` → 422 `errors["providerKey"]`. `PUT` sólo cambia `name`. Sin `Meta:App` configurado (sólo fuera de `Production`) `whatsapp-cloud` no sale en el catálogo y `embedded-signup` responde 422 `integrations.whatsapp.code_exchange_failed` (D-M3).
- **Catálogo:** cada proveedor trae `onboarding`: `{ "kind": "Form" }` o `{ "kind": "MetaEmbeddedSignup", "appId", "configId", "graphApiVersion" }`; con `Form` los tres viajan ausentes (`JsonIgnore` cuando `null`).
- **Rutas de Integrations:** tabla `integrations.connection_routes (provider_key varchar(32), external_id varchar(64), account_id varchar(64) null, tenant_id uuid, connection_id uuid PK FK → connections ON DELETE CASCADE)`, `UNIQUE (provider_key, external_id)` llamado `IX_connection_routes_provider_external` (traducido por nombre a `integrations.whatsapp.number_already_connected`), índice parcial `IX_connection_routes_provider_account (provider_key, account_id) WHERE account_id IS NOT NULL`. Migraciones `AddWhatsAppCloudProvider` (CHECK) y `AddConnectionRoutes`.
- **Embedded Signup:** `POST /api/v1/tenants/{tenantId}/integrations/whatsapp/embedded-signup` (manage) con `{ name, path ∈ {existing_business_app, new_number}, event ∈ {FINISH, FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING}, code (≤ 1024), wabaId (dígitos ≤ 32), phoneNumberId (dígitos, obligatorio con FINISH, null con coexistencia), businessId (dígitos o null) }` → 201 `ConnectionResponse`. Orden §8.1: validar → tope 5 (`limit_reached`), nombre (`name_taken`), ruta (`number_already_connected`) → canje `GET /{v}/oauth/access_token` → (FINISH) `POST /{phoneNumberId}/register` con PIN aleatorio de 6 dígitos **que no se guarda** → (coexistencia) `GET /{wabaId}/phone_numbers`, uno solo o el que no tenga ruta; varios sin ruta → `registration_failed` (D-M15) → `POST /{wabaId}/subscribed_apps` → `GET /{phoneNumberId}?fields=display_phone_number,verified_name,quality_rating,status` → conexión `Active` + ruta + auditoría `created` (con `path` y `event` en los metadatos, nunca valores) en **una** transacción. Nada se deshace en Meta si falla después del canje.
- **Probador `whatsapp-cloud`:** `GET /{v}/{phoneNumberId}?fields=…` con `Authorization: Bearer`. 200 (cualquier `status`, D-M14) → `Ok` + `RefreshedFields` (`displayPhoneNumber`, `verifiedName`, `qualityRating`); Graph `190` → `CredentialsRejected` con `token_expired`; `133010` → `number_unregistered`; otro 400/401/403 → `credentials_rejected`; timeout/5xx/red → `Unreachable`. `ConnectionFailureCodes` gana `token_expired`, `number_unregistered`, `account_disabled`.
- **Clientes HTTP de Graph:** uno por módulo con nombre (`integrations.meta-graph`, `messaging.meta-graph`), `AllowAutoRedirect = false`, timeout 10 s, `RemoveAllLoggers`, `User-Agent: qep-integrations` / `qep-messaging`. Se registran status, `error.code`, `error_subcode` y `fbtrace_id`; **nunca** `code`, token, PIN, `client_secret`, cuerpo de un webhook ni URL firmada de un medio.
- **Esquema `messaging`** (§7, DDL literal): `conversations` (`fillfactor 80`, `UNIQUE (connection_id, wa_id)` `IX_conversations_connection_wa`, `IX_conversations_tenant_status_activity (tenant_id, status, last_activity_at DESC, id DESC)`, `IX_conversations_tenant_open (tenant_id) WHERE status = 'Open'`, `IX_conversations_tenant_unread (tenant_id) INCLUDE (unread_count) WHERE unread_count > 0`, GIN trigram `IX_conversations_profile_name_trgm` e `IX_conversations_wa_id_trgm`, `CHECK status IN ('Open','Resolved')`, `CHECK unread_count >= 0`); `messages` (`fillfactor 90`, `smallint` con `CHECK` para `direction` 1–2, `kind` 1–12, `status` 1–4, `CK_messages_inbound_not_failed (direction = 2 OR status <> 4)`, `search_vector tsvector GENERATED ALWAYS AS (to_tsvector('messaging.es_unaccent', coalesce(text,'') || ' ' || coalesce(caption,''))) STORED`, índices `IX_messages_thread (conversation_id, occurred_at DESC, id DESC)`, `IX_messages_connection_wamid UNIQUE (connection_id, wamid) WHERE wamid IS NOT NULL`, `IX_messages_conversation_client UNIQUE (conversation_id, client_id) WHERE client_id IS NOT NULL`, `IX_messages_tenant_search GIN (tenant_id, search_vector)`); `message_media` (PK `message_id` FK CASCADE, `IX_message_media_pending (next_attempt_at) WHERE stored_at IS NULL`); `webhook_deliveries` (`id bigint identity`, `body_sha256 bytea UNIQUE` `IX_webhook_deliveries_body_sha256`, `payload jsonb`, `IX_webhook_deliveries_pending (id) WHERE processed_at IS NULL`). Extensiones `pg_trgm`, `unaccent`, `btree_gin` sin cláusula `SCHEMA`; configuración `messaging.es_unaccent` creada con el bloque `DO $$ … IF NOT EXISTS` de §7.3. Ids `uuid` v7.
- **Mapa de códigos:** `direction` 1 `Inbound`, 2 `Outbound`; `kind` 1 `Text`, 2 `Image`, 3 `Video`, 4 `Audio`, 5 `Document`, 6 `Sticker`, 7 `Location`, 8 `Contacts`, 9 `Reaction`, 10 `Interactive`, 11 `Template`, 12 `Unsupported`; `status` 1 `Sent`, 2 `Delivered`, 3 `Read`, 4 `Failed`. Entrantes nacen con `status = 2` (D-M5). `played` → `Read` (D-M9). `button` → `Interactive` (D-M6).
- **Webhook:** `GET/POST /api/webhooks/whatsapp`, anónimo, exento de CSRF **por ruta** (prefijo ordinal `/api/webhooks/`), limitador `Webhook` (concurrencia global, cola `OldestFirst`), **no** `Public`. GET: `hub.mode == "subscribe"` y `hub.verify_token` comparado en tiempo constante → 200 `text/plain` con `hub.challenge` y `X-Content-Type-Options: nosniff`; si no, 403; sin `Meta:App`, 403. POST: cuerpo crudo con tope `MaxBodyBytes` (más → 413); `X-Hub-Signature-256 = sha256=<hex>` HMAC-SHA256 del cuerpo con `AppSecret`, comparación en tiempo constante de 32 bytes; falta/mal formada/distinta → **401 sin tocar la base**; `INSERT … ON CONFLICT (body_sha256) DO NOTHING`; 200 vacío.
- **Worker de entregas:** reclamos como `IdentityInboxClaims` (una sentencia que se commitea sola: `claimed_until = now + lease[n]`, `attempts + 1`, sólo si no está procesada y el reclamo venció); curva `[10 s, 30 s, 1 min, 2 min, 5 min, 10 min, 15 min, 20 min]` (8 intentos ≈ 1 h); cada `change` en su propia transacción e idempotente. `field = "messages"`: ruta por `value.metadata.phone_number_id` con caché de 60 s; sin ruta → descartar y registrar; `Paused` o módulo `messaging` apagado → entrantes descartados, `statuses` aplicados; `Active`/`NeedsAttention` → §7.5. `field = "account_update"`: `DISABLED_UPDATE` + `ban_info.waba_ban_state = "DISABLE"`, `ACCOUNT_DELETED`, `PARTNER_REMOVED`, `PARTNER_APP_UNINSTALLED`, `ACCOUNT_OFFBOARDED` → `ReportRejectedAsync(…, "account_disabled")` por conexión de la WABA; `REINSTATE`/`ACCOUNT_RECONNECTED` no reactivan. Otro `field` → se registra y se ignora. `status` con callback `qep:{messageId}` y sin fila → pendiente y reintento; a los 8 intentos se marca procesada con `last_error`; `status` sin callback y sin fila → descartado en el acto. Purga diaria de procesadas con más de 7 días, en lotes.
- **Ingesta (§7.5):** tres sentencias en una transacción: `INSERT conversations … ON CONFLICT (connection_id, wa_id) DO NOTHING RETURNING id` (+ `SELECT id` aparte si no insertó), `INSERT messages … ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING RETURNING id`, y **sólo si insertó** el `UPDATE conversations` literal de §7.5 (`unread_count + 1`, `status = 'Open'`, `profile_name = COALESCE`, `GREATEST` para fechas, foto del último mensaje por `CASE`, `version + 1`). El medio entra en la misma transacción con `next_attempt_at = now`. **Estados:** el `UPDATE … WHERE connection_id = @c AND (wamid = @w OR id = @callbackMessageId) AND direction = 2 AND (monotonía)` literal de §7.5, `wamid = COALESCE(wamid, @wamid)`; si actualizó y es el último de su conversación, `UPDATE conversations SET last_message_status = @new WHERE id = @conv AND last_message_id = @id` **sin** subir `version` ni `updated_at`.
- **Envío (§8.3):** orden: validador → tenant y permiso → `TenantModuleGuard` → conversación del tenant (404 `messaging.conversation.not_found`) → idempotencia (`INSERT … ON CONFLICT (conversation_id, client_id) WHERE client_id IS NOT NULL DO NOTHING RETURNING id`; si no insertó, `SELECT … FOR UPDATE`; `Sent|Delivered|Read` → 201 con ese mensaje sin llamar a Meta; `Failed` → se reenvía sobre la misma fila) → `status = Open` (`messaging.conversation.not_open`) → ventana (`last_inbound_at + 24 h > now`, si no o `null` → `messaging.window_closed`) → conexión `Active` por `ResolveSenderAsync` (si no → `messaging.connection_unavailable`) → `POST /{phoneNumberId}/messages` con `biz_opaque_callback_data = "qep:{messageId}"`. Tabla de respuestas de Meta de §8.3 (`190` → rollback + `token_expired` + `connection_unavailable`; `133010` → rollback + `number_unregistered` + `connection_unavailable`; `131047` → `Failed` + `window_closed`; otro 4xx → `Failed` + `messaging.message.rejected`; timeout/5xx/red → `Failed` con `failure_code = -1` + `rejected`). `occurred_at` = hora del servidor al recibir el 2xx; `sent_by_member_id` = la persona; `clientId` vuelve tal cual.
- **Leído (§8.4):** `POST …/read` (manage, sin `If-Match`) → `unreadCount = 0`, `updated_at`, `version + 1`, commit; **después**, si hay `last_inbound_wamid`, el último entrante tiene menos de 30 días, `unreadCount` era > 0 y la conexión está `Active`: `POST /{phoneNumberId}/messages { status: "read", message_id }` con timeout 5 s, best effort, siempre 204.
- **Resolver/reabrir (§8.5):** `If-Match` obligatorio (428 `precondition.if_match_required` / 412 `concurrency.conflict`); `Resolve` sobre `Resolved` → 422 `messaging.conversation.already_resolved`; `Reopen` sobre `Open` → 422 `messaging.conversation.already_open`; `updated_at`, `version + 1`, auditoría `messaging.conversation.resolved|reopened` (recurso `conversation`) en la misma transacción; responden `ConversationSummary`.
- **Medios (§8.6):** copia por `MediaCopyWorker` (`FOR UPDATE SKIP LOCKED`, `next_attempt_at = now + lease`): `ResolveSenderAsync` → `GET /{meta_media_id}` (> 100 MB → se rinde) → `GET url` con `Authorization: Bearer` en stream a `IMessagingMediaStore.UploadAsync("messaging/{tenantId}/{messageId}", stream, file_size, mime_type)` calculando SHA-256 al pasar; si no coincide → falla e intento siguiente; `stored_at`, `storage_key`, `size_bytes`. Reintentos `[1 min, 5 min, 30 min, 2 h, 6 h, 6 h…]`; pasados 7 días desde `occurred_at` se deja de intentar con `last_error`. Servir: `GET /messaging/media/{messageId}` (read) → stream con `Content-Type`, `Content-Length`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox; default-src 'none'`, `Cache-Control: private, max-age=3600`; `Content-Disposition: inline` sólo para `image/*` (salvo `image/svg+xml`), `audio/*`, `video/*`; `attachment; filename=…` para lo demás. Sin copia, sin medio o inexistente en el tenant → **404 sin código**.
- **Lista e hilo (§8.7):** `GET /messaging/conversations?status=Open|Resolved&search=&page=&pageSize=` (default `Open`, `search ≤ 100`, `page ≥ 1`, `pageSize` 1–50 default 30); `counts.open` = conversaciones `Open` del tenant, `counts.unread` = `SUM(unread_count)` del tenant, sin filtro de búsqueda; `customerWindowExpiresAt = last_inbound_at + 24 h` o `null`; `customer` por `MatchAsync`; `connectionName` por `ListNamesAsync`, `"Conexión eliminada"` si no está (D-M17); `search` busca `profile_name ILIKE '%term%'`, `wa_id LIKE '%dígitos%'` (sólo con dígitos) y `wa_id = ANY(phones de FindPhonesByNameAsync)`. `GET /messaging/conversations/{id}/messages?limit=50&before=` (`limit` 1–100; `before` de **esa** conversación o 422 `validation.failed` en `before`), `items` cronológicos, `hasMore` por la fila `limit + 1`. `sentBy` con el nombre del miembro por lookup por página.
- **Búsqueda (§7.3, §8.8):** `GET /messaging/messages/search?q=&conversationId=&from=&to=&limit=50&before=` (read). `q` 2–100 tras `Trim`, sin `\0`; `from ≤ to`; `limit` 1–100; `before` del tenant o 404 `messaging.message.not_found`; `conversationId` del tenant o 404 `messaging.conversation.not_found`. `tsquery` armada en el servidor: `Trim`, separar por espacios, quitar `& | ! : * ( ) ' \ <` y controles, descartar vacíos, primeros 8 tokens, cada uno por `to_tsvector('messaging.es_unaccent', token)`; lexemas de tokens de ≥ 3 caracteres con `:*`, los demás exactos, unidos con `&`; sin lexemas → `{ items: [], hasMore: false }` sin consultar. Consulta con `SET LOCAL statement_timeout = '2s'` en la misma transacción, `LIMIT limit + 1`, `ORDER BY occurred_at DESC, id DESC`; un `57014` → 422 `validation.failed` con `errors["q"] = ["Busca con una palabra más específica o acota las fechas"]`, nunca 500. `MessageHit = Message + { conversationId, contact, customer, connectionName }`.
- **Customers:** `phone_e164 varchar(16) NULL` (E.164 con `+`), índice `IX_customers_tenant_phone_e164 (tenant_id, phone_e164) WHERE phone_e164 IS NOT NULL`, calculado en `Customer.Create`/`Update` con `IPhoneNumberNormalizer` (con `+` → internacional; si no, nacional del `Country`; inválido → `null`, nunca falla la escritura). Backfill idempotente al arrancar en lotes de 500 (`phone IS NOT NULL AND phone_e164 IS NULL`). Puerto `ICustomerPhoneDirectory`: `MatchAsync(tenantId, e164s)` (un cliente por número: `created_at` ascendente, luego `id` menor, D-M7) y `FindPhonesByNameAsync(tenantId, term, cap = 200)` (con más de 200 se registra en el log). Match: dígitos de `phone_e164` == `waId`.
- **Storage:** `IObjectStorage.UploadAsync(string key, Stream content, long contentLength, string contentType, ct)` y `Task<StoredObjectStream?> OpenReadAsync(string key, ct)`; `R2ObjectStorage` con `UseChunkEncoding = false`, `DisablePayloadSigning = true` y `ContentLength` explícito.
- **Errores (§10.1):** `validation.failed` 422 con `errors`; `integrations.whatsapp.code_exchange_failed|registration_failed|number_already_connected` 422; `integrations.connection.limit_reached|name_taken` 422; `messaging.window_closed`, `messaging.connection_unavailable`, `messaging.conversation.not_open`, `messaging.message.rejected`, `messaging.conversation.already_resolved|already_open` 422; `messaging.conversation.not_found`, `messaging.message.not_found` 404; medio: 404 sin código; `tenancy.module_not_enabled` 403; `authorization.denied` 403 (nunca 404 para otro tenant); 412/428; webhook 401/403/413/429. Ningún handler arma un status: `ApiExceptionHandler`.
- **`failureReason` (§10.3):** calculado al leer desde `failure_code` con la tabla literal de §10.3 (`131047`, `131026`, `131051`, `131049`, `131050`, `131021`, `131031`, `131042`, `131056`, `133010`, `368`, `131005`, `131016`, `131000`, `-1`, otro → «WhatsApp no pudo entregar el mensaje.»). `failure_title` nunca sale por HTTP.
- **Auditoría de Messaging:** puerto propio `IMessagingAuditRecorder` (proyección de `audit.entries` en `MessagingDbContext`, `source = "messaging"`); un envío **no** se audita. Sin outbox.
- **Seguridad (§11):** firma antes de la base; secretos nunca en logs; `accessToken` sólo como `{ configured, updatedAt, readable }`; medios con `nosniff` + CSP `sandbox`; aislamiento por ruta + política + handler.
- **Despliegue:** `k8s/prod-configMap.yaml` → `Meta__App__AppId`, `Meta__App__ConfigId`, `Meta__App__GraphApiVersion: "v24.0"`; `k8s/prod-secret.yaml` → `Meta__App__AppSecret: "#{META_APP_SECRET}#"`, `Meta__App__WebhookVerifyToken: "#{META_WEBHOOK_VERIFY_TOKEN}#"`. `CLAUDE.md`: **15** módulos. HANDOFF en `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`.

## Review Focus

Cinco entradas que el spec implica y que ninguna prueba obvia ejercita; cada una tiene su prueba en la tarea dueña.

1. **Un webhook válido cuyo JSON no es lo que Meta documenta** (`entry` ausente, `changes[].value` sin `metadata`, `messages[].timestamp` no numérico, `type` desconocido, `contacts` vacío): nada de eso puede tumbar el worker ni dejar la entrega reclamada para siempre. Lo esperable: la entrega se procesa (lo ilegible se registra y se salta, lo desconocido cae en `Unsupported`), `processed_at` queda puesto. Pruebas: Task 12 (parser con cada forma rota) y Task 13 (`AMalformedDeliveryIsProcessedWithoutRetries`).
2. **Texto de envío con caracteres de control o un `\0`** (`"hola\u0000"`, sólo espacios): PostgreSQL rechaza `\0` en `text` con un 500. Lo esperable: 422 `validation.failed` en `text`, sin fila. Pruebas: Task 16 (`SendMessageValidatorTests`, `ATextWithANullByteIsAValidationError`).
3. **Un `before` de otra conversación o de otro tenant en el hilo** (`GET …/{id}/messages?before=<mensaje ajeno>`): no puede filtrar por el cursor ajeno ni revelar que existe. Lo esperable: 422 `validation.failed` en `before` (misma respuesta que un GUID inexistente). Prueba: Task 14 (`ABeforeFromAnotherConversationIsAValidationError`).
4. **Un acuse `read` que llega antes que `delivered`, y un `failed` después de `delivered`** (Meta no garantiza orden): el estado no puede retroceder ni un entregado volverse fallido. Lo esperable: `Read` se queda, el `failed` tardío no toca la fila. Prueba: Task 13b (`StatusesNeverGoBackwardsAndFailedOnlyOverridesSent`).
5. **Un medio cuyo `mime_type` es ejecutable en el navegador** (`text/html`, `image/svg+xml`) servido desde el origen de la API: no puede abrirse inline. Lo esperable: `Content-Disposition: attachment`, `nosniff` y CSP `sandbox` en toda respuesta. Prueba: Task 18 (`AnHtmlOrSvgMediaIsServedAsAttachmentWithSandbox`).

## Decisiones de este plan (ambigüedades del spec o choques con el código)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | `Meta:App` se bindea en **dos** clases: `MetaAppOptions` (Integrations.Infrastructure, las cinco claves, **con** `MetaAppOptionsValidator` y `ValidateOnStart`) y `MessagingMetaOptions` (Messaging.Infrastructure, sólo `AppSecret`, `WebhookVerifyToken`, `GraphApiVersion`, sin validador) | Un módulo no referencia la infraestructura de otro (decisión 3 del spec). `ConfigurationExampleTests` descubre las dos por `SectionName` y exige las claves en el ejemplo; el validador vive una sola vez |
| P2 | D-M3 (sin `Meta:App` fuera de producción) se aplica por un puerto `IMetaAppSettings` en Integrations.Application (`IsConfigured`, `AppId`, `ConfigId`, `GraphApiVersion`): el catálogo filtra `whatsapp-cloud` y `embedded-signup` responde `code_exchange_failed`. Las conexiones `whatsapp-cloud` ya guardadas siguen visibles por módulo (no se ocultan) | El spec sólo nombra el catálogo y el signup; ocultar lo guardado dejaría a la bandeja sin `connectionName` |
| P3 | El puerto de Meta para el signup es `IWhatsAppSignupGateway` (Integrations.Application) con resultados `GraphResult<T>` que **nunca lanzan** por un error de Graph; el handler decide el código | El handler se prueba con dobles sin HTTP, como `FakeConnectionTester` |
| P4 | Las rutas se escriben por un agregado chico `IntegrationConnectionRoute` (Domain) y un `IConnectionRouteRepository` (Application) en la **misma** unidad de trabajo que la conexión; la lectura cross-tenant es `IConnectionRoutes` (Infrastructure, join con `connections` para el `Status`) | El spec define sólo el puerto de lectura; la escritura necesita un tipo |
| P5 | `ConnectionTestResult` gana `FailureCode` y `RefreshedFields`; el agregado gana `ApplyProviderFields(provider, values)` que sólo toca campos **no secretos** de un proveedor con `Onboarding = MetaEmbeddedSignup` o marcados `Internal` | Lo pide §6.1 («el probador devuelve RefreshedFields y el handler los aplica con un método del agregado») |
| P6 | `PUT` de una conexión `whatsapp-cloud` conserva `connection.Fields` tal cual (no normaliza el diccionario del request, que debe venir vacío) | Sin esto `Update` reemplazaría `fields` por `{}` y borraría `phoneNumberId` |
| P7 | `Customer.Create`/`Update` ganan un parámetro **final y opcional** `IPhoneNumberNormalizer? phoneNormalizer = null`; los handlers y la importación lo pasan; `null` deja `PhoneE164` en `null` | 18 llamadas en pruebas no se reescriben; el cálculo sigue en el dominio como pide §6.5 |
| P8 | La traducción del `57014` de la búsqueda vive en `MessageQueries` (Infrastructure), no en `MessagingUnitOfWork`: es una lectura y no pasa por `SaveChanges` | §8.8 nombra el unit of work, pero ahí nunca llega |
| P9 | `webhook_deliveries` se reclama con SQL crudo (`UPDATE … WHERE processed_at IS NULL AND (claimed_until IS NULL OR claimed_until < @now) RETURNING attempts`), no con el inbox de Identity | Es la misma regla de `IdentityInboxClaims` sobre otra tabla; Identity no se referencia |
| P10 | Las pruebas de workers llaman `DrainAsync()` (internal, `InternalsVisibleTo`) y avanzan un `TestClock` registrado en lugar de `IClock`, como `OutboxDeliveryWorkerTests` | Sin `PeriodicTimer` de por medio |
| P11 | El webhook devuelve 401 con ProblemDetails (`messaging.webhook.signature_invalid`) por `RequestUnauthorizedException`; el 403 del GET y el 404 del medio salen del endpoint sin código | El spec pide «401» y «404 sin código»; el 401 con código no rompe a Meta y queda en el log de fallas |
| P12 | `IObjectStorage` gana los dos métodos del spec; cada doble de prueba que lo implementa (`rg ": IObjectStorage"`) gana los dos con `throw new NotSupportedException()` salvo `InMemoryObjectStorage` de Quotations, que los implementa | Lo pide §6.6 literal |
| P13 | `ChangeTenantModulesValidator` dice «A batch has at most seven changes.»; pasa a «eight» con la clave nueva | El mensaje nombra `TenantModuleKeys.All.Count` |
| P14 | La versión de `libphonenumber-csharp` se fija con `dotnet package search libphonenumber-csharp --exact-match --take 1` al ejecutar la Task 8 (la última estable) | §6.5: «la versión se fija al implementar» |
| P15 | La curva de leases del worker de entregas es `[10 s, 30 s, 1 min, 2 min, 5 min, 10 min, 15 min, 20 min]` (≈ 63 min en 8 intentos) y la de medios `[1 min, 5 min, 30 min, 2 h, 6 h]` (desde el 5.º, 6 h) | §8.2 «N = 8 intentos (~1 h)» y §8.6 |

## Mapa de archivos

Lo que crea o modifica cada tarea (resumen; el detalle está en cada una).

| Tarea | Crea | Modifica |
| --- | --- | --- |
| 1 | `Integrations.Infrastructure/Meta/MetaAppOptions.cs`, `MetaAppOptionsValidator.cs`, `MetaAppSettings.cs`; `Integrations.Application/IMetaAppSettings.cs`; `MetaAppOptionsValidatorTests.cs` | `IntegrationsInfrastructureExtensions.cs`, `appsettings.example.json`, `IntegrationsApiHarness.cs` |
| 2 | `Tenancy.Infrastructure/Persistence/Migrations/*_AddMessagingModuleKey.cs` | `TenantModuleKey.cs`, `TenancyDbContext.cs`, `ChangeTenantModules.cs`, `TenantModuleKeysTests.cs` |
| 3 | `src/Modules/Messaging/*` (4 csproj), `tests/Modules/Messaging/*` (2 csproj), `MessagingLayerTests.cs`, `MessagingPermissions.cs` | `Backend.slnx`, `Api.csproj`, `Bootstrapper.csproj`, `ArchitectureTests.csproj`, `QepServiceCollectionExtensions.cs`, `CompositionRootTests.cs`, locks |
| 4 | `WhatsAppCloudFieldKeys` en `IntegrationProviders.cs`, migración `AddWhatsAppCloudProvider` | `IntegrationCatalog.cs`, `IntegrationProviders.cs`, `IntegrationsDtos.cs`, `IntegrationsSupport.cs`, `GetIntegrationsCatalog.cs`, `ConnectionInputRules.cs`, `CreateConnection.cs`, `UpdateConnection.cs`, `IntegrationsErrorCodes.cs`, pruebas |
| 5 | `IntegrationConnectionRoute.cs`, `IConnectionRouteRepository` e `IConnectionRoutes` en `IntegrationsPorts.cs`, `ConnectionRouteRepository.cs`, `ConnectionRoutes.cs`, migración `AddConnectionRoutes`, `ConnectionRoutesApiTests.cs` | `IntegrationsDbContext.cs`, `IntegrationsUnitOfWork.cs`, `IIntegrationConnections.cs`, `IntegrationsInfrastructureExtensions.cs` |
| 6 | `Integrations.Infrastructure/Meta/MetaGraphClient.cs`, `MetaGraphError.cs`, `WhatsAppCloudConnectionTester.cs`, `WhatsAppCloudConnectionTesterTests.cs` | `ConnectionTesting.cs`, `IntegrationConnection.cs`, `TestConnection.cs`, `ResumeConnection.cs`, `IntegrationsErrorCodes.cs`, `IntegrationsInfrastructureExtensions.cs` |
| 7 | `IWhatsAppSignupGateway.cs`, `CompleteWhatsAppSignup.cs`, `MetaGraphSignupGateway.cs`, `CompleteWhatsAppSignupHandlerTests.cs`, `EmbeddedSignupApiTests.cs` | `IntegrationsEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `ConnectionSecretLeakTests.cs` |
| 8 | `IPhoneNumberNormalizer.cs`, `LibPhoneNumberNormalizer.cs`, `CustomerPhoneBackfillWorker.cs`, `ICustomerPhoneDirectory.cs`, `CustomerPhoneDirectory.cs`, migración `AddCustomerPhoneE164`, pruebas | `Directory.Packages.props`, locks, `Customer.cs`, `CustomersDbContext.cs`, `CreateCustomer.cs`, `UpdateCustomer.cs`, `ImportCustomers.cs`, `CustomersInfrastructureExtensions.cs` |
| 9 | `Messaging.Domain/*` (enums, `Conversation`, `MessagingErrorCodes`, `MessagingDomainException`, `MessageFailureReasons`), `ConversationTests.cs`, `MessageFailureReasonsTests.cs` | — |
| 10 | `Messaging.Infrastructure/Persistence/*` (DbContext, factory, initializer, unit of work, repository, códigos, migración `InitialMessaging`), opciones, `MessagingInfrastructureExtensions.cs`, `MessagingApiHarness.cs`, `MessagingPersistenceTests.cs`, `MessageColumnCodesTests.cs` | `Program.cs`, `QepServiceCollectionExtensions.cs`, `appsettings.example.json` |
| 11 | `IWebhookSignatureVerifier.cs`, `HmacWebhookSignatureVerifier.cs`, `IWebhookDeliveries.cs`, `WebhookDeliveries.cs`, `WebhookHandlers.cs`, `WhatsAppWebhookEndpoints.cs`, `WebhookApiTests.cs`, `HmacWebhookSignatureVerifierTests.cs`, `RequireCsrfHeaderMiddlewareTests.cs` | `RateLimiterPolicies.cs`, `Program.cs`, `RequireCsrfHeaderMiddleware.cs`, `QepServiceCollectionExtensions.cs` |
| 12 | `WebhookPayload.cs`, `WebhookPayloadParser.cs`, `WebhookPayloadParserTests.cs` | — |
| 13 | `IMessagingConnectionDirectory.cs`, `MessagingConnectionDirectory.cs` (Bootstrapper), `WebhookRouting.cs`, `InboundIngestion.cs`, `WebhookDeliveryProcessor.cs`, `WebhookDeliveryWorker.cs`, `InboundIngestionTests.cs`, `WebhookLoadTests.cs` | `MessagingInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`, `MessagingApiHarness.cs` |
| 13b | `StatusIngestion.cs`, `WebhookPurgeWorker.cs`, `StatusIngestionTests.cs`, `AccountUpdateTests.cs`, `WebhookPurgeTests.cs` | `WebhookDeliveryProcessor.cs`, `MessagingInfrastructureExtensions.cs` |
| 14 | `MessagingDtos.cs`, `MessagingSupport.cs`, `IConversationQueries.cs`, `IMessageQueries.cs`, `IMessagingCustomerDirectory.cs`, `IMessagingMemberNames.cs`, `ConversationQueries.cs`, `MessageQueries.cs`, `ListConversations.cs`, `GetConversation.cs`, `ListMessages.cs`, `MessagingEndpoints.cs`, adaptadores en Bootstrapper, pruebas | `Program.cs`, `QepServiceCollectionExtensions.cs`, `MessagingInfrastructureExtensions.cs` |
| 15 | `SearchTerms.cs`, `IMessageSearch.cs`, `MessageSearch.cs`, `SearchMessages.cs`, `SearchTermsTests.cs`, `MessageSearchApiTests.cs` | `MessagingEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `MessagingInfrastructureExtensions.cs` |
| 16 | `IWhatsAppCloudClient.cs`, `WhatsAppCloudClient.cs`, `IOutboundMessages.cs`, `OutboundMessages.cs`, `SendMessage.cs`, `SendMessageHandlerTests.cs`, `SendMessageApiTests.cs` | `MessagingEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `MessagingInfrastructureExtensions.cs`, `MessagingApiHarness.cs` |
| 17 | `IMessagingAuditRecorder.cs`, `MessagingAuditRecorder.cs`, `ConversationLifecycle.cs`, `ConversationLifecycleApiTests.cs` | `MessagingEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `MessagingInfrastructureExtensions.cs` |
| 18 | `IMessagingMediaStore.cs`, `IMediaReads.cs`, `MediaReads.cs`, `MessagingMediaStore.cs` (Bootstrapper), `MediaCopyProcessor.cs`, `MediaCopyWorker.cs`, `GetMedia.cs`, `Sha256PassThroughStream.cs`, `MediaApiTests.cs`, `R2ObjectStorageTests` (caso nuevo) | `IObjectStorage.cs`, `R2ObjectStorage.cs`, dobles de `IObjectStorage`, `MessagingEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `MessagingInfrastructureExtensions.cs` |
| 19 | `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md` | `README.md`, `CLAUDE.md`, `k8s/prod-configMap.yaml`, `k8s/prod-secret.yaml`, `appsettings.example.json` |
| 20 | `scripts/compare-test-runs.ps1` (temporal, fuera del repo) | — |

## Tipos que cruzan tareas (referencia rápida)

Firmas exactas; cada tarea las repite en su bloque **Interfaces**. Namespaces: `Modules.Integrations.{Domain,Application}` y `Modules.Messaging.{Domain,Application}`.

```csharp
// Integrations.Domain (Task 4–6)
public enum ProviderOnboarding { Form, MetaEmbeddedSignup }
public sealed class FieldDefinition(string key, string label, FieldKind kind, bool required, int maxLength, string? pattern, string invalidMessage, bool isInternal = false) { public bool Internal { get; } }
public sealed class IntegrationProvider(string key, string displayName, IntegrationCategory category, IReadOnlyList<TenantModuleKey> consumingModules, IReadOnlyList<FieldDefinition> fields, int maxConnections, ProviderOnboarding onboarding = ProviderOnboarding.Form) { public ProviderOnboarding Onboarding { get; } public IReadOnlyList<FieldDefinition> CatalogFields { get; } }
public static class WhatsAppCloudFieldKeys { DisplayPhoneNumber, VerifiedName, PhoneNumberId, WabaId, QualityRating, AccessToken }
public static class IntegrationProviders { public static readonly IntegrationProvider WhatsAppCloud; All = [Zenvia, WhatsAppCloud]; }
IntegrationsErrorCodes.WhatsAppCodeExchangeFailed / WhatsAppRegistrationFailed / WhatsAppNumberAlreadyConnected
ConnectionFailureCodes.TokenExpired / NumberUnregistered / AccountDisabled
public sealed class IntegrationConnectionRoute { ProviderKey, ExternalId, AccountId?, TenantId, ConnectionId; static Create(string providerKey, string externalId, string? accountId, Guid tenantId, Guid connectionId) }
IReadOnlyList<string> IntegrationConnection.ApplyProviderFields(IntegrationProvider provider, IReadOnlyDictionary<string, string> values)

// Integrations.Application
public interface IMetaAppSettings { bool IsConfigured { get; } string? AppId { get; } string? ConfigId { get; } string GraphApiVersion { get; } }
public sealed record ProviderOnboardingResponse(string Kind, string? AppId, string? ConfigId, string? GraphApiVersion)
public sealed record ProviderResponse(string Key, string DisplayName, string Category, IReadOnlyList<ProviderFieldResponse> Fields, int MaxConnections, int ConnectionCount, ProviderOnboardingResponse Onboarding)
public sealed record ConnectionTestResult(ConnectionTestOutcome Outcome, string? FieldKey = null, string? Reason = null, string? FailureCode = null, IReadOnlyDictionary<string, string>? RefreshedFields = null)
public sealed record ConnectionRoute(Guid TenantId, Guid ConnectionId, ConnectionStatus Status)
public interface IConnectionRoutes { Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken ct); Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken ct); }
public interface IConnectionRouteRepository { void Add(IntegrationConnectionRoute route); Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken ct); }
public sealed record ConnectionListing(Guid Id, string Name, ConnectionStatus Status)
Task<IReadOnlyList<ConnectionListing>> IIntegrationConnections.ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken ct)
public sealed record GraphFailure(int HttpStatus, int? Code, int? Subcode, string Reason)
public sealed record GraphResult<T>(T? Value, GraphFailure? Failure) { bool Succeeded => Failure is null; }
public sealed record WabaPhoneNumber(string Id, string? DisplayPhoneNumber, string? VerifiedName, string? QualityRating)
public interface IWhatsAppSignupGateway { Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct); Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken ct); Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken ct); Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken ct); Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken ct); }
public sealed record CompleteWhatsAppSignupCommand(Guid TenantId, string? Name, string? Path, string? Event, string? Code, string? WabaId, string? PhoneNumberId, string? BusinessId) : ICommand<ConnectionResponse>

// Messaging.Domain (Task 9)
public enum ConversationStatus { Open, Resolved }
public enum MessageDirection { Inbound = 1, Outbound = 2 }
public enum MessageKind { Text = 1, Image = 2, Video = 3, Audio = 4, Document = 5, Sticker = 6, Location = 7, Contacts = 8, Reaction = 9, Interactive = 10, Template = 11, Unsupported = 12 }
public enum MessageStatus { Sent = 1, Delivered = 2, Read = 3, Failed = 4 }
public sealed class Conversation { Id, TenantId, ConnectionId, WaId, ProfileName, Status, UnreadCount, LastInboundAt, LastInboundWamid, LastActivityAt, LastMessageId, LastMessageDirection, LastMessageKind, LastMessagePreview, LastMessageStatus, LastMessageAt, CreatedAt, UpdatedAt, Version; static Start(Guid id, Guid tenantId, Guid connectionId, string waId, string? profileName, DateTimeOffset now); void Resolve(DateTimeOffset now); void Reopen(DateTimeOffset now); bool MarkRead(DateTimeOffset now); DateTimeOffset? CustomerWindowExpiresAt; bool IsWindowOpen(DateTimeOffset now); }
public static class MessagingErrorCodes { WindowClosed, ConnectionUnavailable, ConversationNotOpen, MessageRejected, AlreadyResolved, AlreadyOpen, ConversationNotFound, MessageNotFound, WebhookSignatureInvalid }
public static class MessageFailureReasons { public const int Unconfirmed = -1; public static string? For(int? failureCode); }

// Messaging.Application (Task 11–18)
public sealed record ContactDto(string WaId, string? ProfileName)
public sealed record CustomerRefDto(Guid Id, string Name)
public sealed record LastMessageDto(string Direction, string Kind, string? Preview, string Status, DateTimeOffset At)
public sealed record ConversationSummary(Guid Id, Guid ConnectionId, string ConnectionName, ContactDto Contact, CustomerRefDto? Customer, string Status, int UnreadCount, LastMessageDto? LastMessage, DateTimeOffset? CustomerWindowExpiresAt, DateTimeOffset UpdatedAt, long Version)
public sealed record ConversationCountsDto(int Open, int Unread)
public sealed record ConversationPageDto(IReadOnlyList<ConversationSummary> Items, int Total, int Page, int PageSize, ConversationCountsDto Counts)
public sealed record MediaDto(string Url, string MimeType, string? FileName, string? Caption)
public sealed record LocationDto(double Latitude, double Longitude, string? Name, string? Address)
public sealed record SentByDto(Guid MemberId, string? DisplayName)
public sealed record MessageDto(Guid Id, string Direction, string Kind, string? Text, MediaDto? Media, LocationDto? Location, string Status, string? FailureReason, DateTimeOffset At, SentByDto? SentBy, Guid? ClientId)
public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, bool HasMore)
public sealed record MessageHitDto(Guid Id, string Direction, string Kind, string? Text, MediaDto? Media, LocationDto? Location, string Status, string? FailureReason, DateTimeOffset At, SentByDto? SentBy, Guid? ClientId, Guid ConversationId, ContactDto Contact, CustomerRefDto? Customer, string ConnectionName)
public sealed record SearchPageDto(IReadOnlyList<MessageHitDto> Items, bool HasMore)
public sealed record MediaStreamDto(Stream Content, string ContentType, long Length, string? FileName)
public sealed record ConversationRow(Guid Id, Guid TenantId, Guid ConnectionId, string WaId, string? ProfileName, ConversationStatus Status, int UnreadCount, DateTimeOffset? LastInboundAt, Guid? LastMessageId, MessageDirection? LastMessageDirection, MessageKind? LastMessageKind, string? LastMessagePreview, MessageStatus? LastMessageStatus, DateTimeOffset? LastMessageAt, DateTimeOffset UpdatedAt, long Version)
public sealed record MessageRow(Guid Id, Guid ConversationId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption, string? DetailsJson, MessageStatus Status, int? FailureCode, DateTimeOffset OccurredAt, Guid? SentByMemberId, Guid? ClientId, MessageMediaRow? Media)
public sealed record MessageMediaRow(string MimeType, string? FileName)
public sealed record MessageCursor(DateTimeOffset OccurredAt, Guid Id)
public sealed record MessagingRoute(Guid TenantId, Guid ConnectionId, string Status)
public sealed record MessagingSender(string PhoneNumberId, string AccessToken) { ToString() sin el token }
public interface IConversationRepository { Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken ct); }
public interface IMessagingUnitOfWork { Task<int> SaveChangesAsync(CancellationToken ct); }
public interface IConversationQueries { Task<(IReadOnlyList<ConversationRow> Items, int Total)> ListAsync(Guid tenantId, ConversationStatus status, string? search, IReadOnlyCollection<string> customerWaIds, int page, int pageSize, CancellationToken ct); Task<ConversationCountsDto> CountsAsync(Guid tenantId, CancellationToken ct); Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken ct); Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct); }
public interface IMessageQueries { Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken ct); Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken ct); }
public interface IMessageSearch { Task<IReadOnlyList<string?>> LexemizeAsync(IReadOnlyList<string> tokens, CancellationToken ct); Task<MessageCursor?> FindCursorAsync(Guid tenantId, Guid messageId, CancellationToken ct); Task<IReadOnlyList<MessageRow>> SearchAsync(Guid tenantId, string tsQuery, Guid? conversationId, DateTimeOffset? from, DateTimeOffset? to, MessageCursor? before, int take, CancellationToken ct); }
public interface IMessagingConnectionDirectory { Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken ct); Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken ct); Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken ct); Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken ct); Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken ct); }
public interface IMessagingCustomerDirectory { Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken ct); Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken ct); }
public interface IMessagingMemberNames { Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct); }
public interface IMessagingMediaStore { Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken ct); Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken ct); Task DeleteAsync(string key, CancellationToken ct); }
public sealed record StoredMedia(string StorageKey, string MimeType, string? FileName, long? SizeBytes)
public interface IMediaReads { Task<StoredMedia?> FindStoredAsync(Guid tenantId, Guid messageId, CancellationToken ct); }
public enum SendOutcome { Sent, GraphError, Unconfirmed }
public sealed record SendTextResult(SendOutcome Outcome, string? Wamid, int? Code, string? Title)
public sealed record MediaInfo(Uri Url, string MimeType, string? Sha256, long FileSize)
public sealed record MessagingGraphFailure(int HttpStatus, int? Code, string Reason)
public sealed record MessagingGraphResult<T>(T? Value, MessagingGraphFailure? Failure) { bool Succeeded => Failure is null; }  // copia chica: Messaging no referencia Integrations
public interface IWhatsAppCloudClient { Task<SendTextResult> SendTextAsync(MessagingSender sender, string to, string body, string callbackData, CancellationToken ct); Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken ct); Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken ct); Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken ct); }
public sealed record OutboundDraft(Guid MessageId, Guid ConversationId, Guid TenantId, Guid ConnectionId, Guid ClientId, string Text, Guid SentByMemberId, DateTimeOffset Now)
public sealed record ExistingOutbound(Guid Id, MessageStatus Status, DateTimeOffset OccurredAt, Guid? SentByMemberId, string Text)
public interface IOutboundClaim : IAsyncDisposable { bool Inserted { get; } Guid MessageId { get; } ExistingOutbound? Existing { get; } Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, CancellationToken ct); Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, CancellationToken ct); Task RollbackAsync(CancellationToken ct); }
public interface IOutboundMessages { Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken ct); }
public interface IWebhookSignatureVerifier { bool IsConfigured { get; } bool VerifyBody(ReadOnlySpan<byte> body, string? signatureHeader); bool VerifyToken(string? token); }
public interface IWebhookDeliveries { Task<bool> EnqueueAsync(byte[] body, DateTimeOffset receivedAt, CancellationToken ct); }
public interface IMessagingAuditRecorder { void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt); }
public static class MessagingPermissions { ConversationRead = "messaging.conversation.read"; ConversationManage = "messaging.conversation.manage"; }
```

---
### Task 0: Precondiciones del worktree

**Files:** ninguno (sólo verificación).

**Interfaces:**
- Consumes: el worktree `$W` en `feature/mensajeria-whatsapp`, Docker, SDK `10.0.400`.
- Produces: la certeza de que el árbol está limpio y compila antes de tocar nada.

- [ ] **Step 1: Rama, árbol limpio y SDK**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
git branch --show-current
git status --short
dotnet --version
docker info --format '{{.ServerVersion}}'
```

Esperado: `feature/mensajeria-whatsapp`, sin salida en `status`, `10.0.400`, una versión de Docker. Si Docker no responde, las tareas con pruebas de integración no se pueden ejecutar: se detiene y se reporta.

- [ ] **Step 2: El árbol compila sin warnings**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet restore --locked-mode
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build roto antes de empezar" }
```

Esperado: `0 Advertencias`, `0 Errores` (o `0 Warning(s)`, `0 Error(s)`). Si hay errores, es un problema previo: se reporta y no se arranca.

- [ ] **Step 3: `dotnet ef` disponible**

```powershell
dotnet ef --version
```

Si falla: `dotnet tool install --global dotnet-ef --version 10.*` y repetir. Sin commit en esta tarea.

---

### Task 1: `Meta:App` en Integrations (opciones, validador, puerto de visibilidad)

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaAppOptions.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaAppOptionsValidator.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaAppSettings.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IMetaAppSettings.cs`
- Create: `tests/Modules/Integrations/Modules.Integrations.UnitTests/MetaAppOptionsValidatorTests.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs:49-60`
- Modify: `src/Api/appsettings.example.json:107-116`
- Modify: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs:151-158` (settings fijadas)

**Interfaces:**
- Consumes: `SecretProtectionOptionsValidator` como patrón (`src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs:17-73`).
- Produces: `MetaAppOptions { SectionName = "Meta:App"; AppId, ConfigId, GraphApiVersion = "v24.0", AppSecret, WebhookVerifyToken; bool IsConfigured }`, `IMetaAppSettings`, `MetaAppSettings : IMetaAppSettings`.

- [ ] **Step 1: Prueba RED del validador**

`tests/Modules/Integrations/Modules.Integrations.UnitTests/MetaAppOptionsValidatorTests.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §9: en Production las cinco claves y la forma de la versión; fuera, el
/// módulo arranca sin ellas. Ningún mensaje lleva un valor.</summary>
public sealed class MetaAppOptionsValidatorTests
{
    private const string Secret = "meta-app-secret-SENTINEL-1a2b";
    private const string Token = "meta-verify-token-SENTINEL-3c4d-0123456789abcdef";

    private static MetaAppOptionsValidator Validator(bool production) =>
        new(new FixedEnvironment(production ? Environments.Production : Environments.Development));

    private static MetaAppOptions Complete() => new()
    {
        AppId = "123",
        ConfigId = "456",
        GraphApiVersion = "v24.0",
        AppSecret = Secret,
        WebhookVerifyToken = Token,
    };

    [Fact]
    public void ProductionAcceptsTheFiveKeys() =>
        Assert.True(Validator(production: true).Validate(null, Complete()).Succeeded);

    [Theory]
    [InlineData("AppId")]
    [InlineData("ConfigId")]
    [InlineData("AppSecret")]
    [InlineData("WebhookVerifyToken")]
    public void ProductionRejectsAMissingKeyNamingItWithoutItsValue(string missing)
    {
        var options = Complete();
        typeof(MetaAppOptions).GetProperty(missing)!.SetValue(options, " ");

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        var message = string.Join(" ", result.Failures!);
        Assert.Contains($"Meta:App:{missing}", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("24.0")]
    [InlineData("v24")]
    [InlineData("V24.0")]
    public void TheVersionShapeIsEnforcedEverywhere(string version)
    {
        var options = Complete();
        options.GraphApiVersion = version;

        Assert.True(Validator(production: false).Validate(null, options).Failed);
        Assert.True(Validator(production: true).Validate(null, options).Failed);
    }

    [Fact]
    public void OutsideProductionAnEmptySectionIsValidAndNotConfigured()
    {
        var options = new MetaAppOptions();

        Assert.True(Validator(production: false).Validate(null, options).Succeeded);
        Assert.False(options.IsConfigured());
        Assert.True(Complete().IsConfigured());
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
```

- [ ] **Step 2: Verificar RED**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~MetaAppOptionsValidatorTests"
```

Esperado: `error CS0246: The type or namespace name 'MetaAppOptions' could not be found` (copiar literal).

- [ ] **Step 3: Opciones, validador y puerto**

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaAppOptions.cs`:

```csharp
namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>
/// La sección <c>Meta:App</c> (spec 2026-10-09 §9, decisión 2): una app de Meta para toda la plataforma.
/// <c>AppId</c>, <c>ConfigId</c> y <c>GraphApiVersion</c> son públicos (ConfigMap); <c>AppSecret</c> y
/// <c>WebhookVerifyToken</c> son secretos (user-secrets en local, Secret en k8s). Messaging bindea la
/// misma sección con su propia clase (P1 del plan): un módulo no referencia la infraestructura de otro.
/// <b>Vacío = ausente</b>, como <c>SecretProtectionOptions</c>.
/// </summary>
public sealed class MetaAppOptions
{
    public const string SectionName = "Meta:App";

    public const string DefaultGraphApiVersion = "v24.0";

    public string? AppId { get; set; }

    public string? ConfigId { get; set; }

    public string GraphApiVersion { get; set; } = DefaultGraphApiVersion;

    public string? AppSecret { get; set; }

    public string? WebhookVerifyToken { get; set; }

    /// <summary>D-M3: con las cinco claves el proveedor <c>whatsapp-cloud</c> sale en el catálogo y el
    /// webhook acepta tráfico; sin ellas (sólo fuera de Production) el módulo arranca igual. Método y
    /// no propiedad: <c>ConfigurationExampleTests</c> exige en el ejemplo toda propiedad con <c>get</c>.</summary>
    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(ConfigId)
        && !string.IsNullOrWhiteSpace(GraphApiVersion)
        && !string.IsNullOrWhiteSpace(AppSecret)
        && !string.IsNullOrWhiteSpace(WebhookVerifyToken);
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaAppOptionsValidator.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>
/// Spec 2026-10-09 §9: en <c>Production</c> exige las cinco claves; sin ellas el pod no arranca, a
/// propósito, como <c>SecretProtectionOptionsValidator</c>. La forma de la versión (<c>^v\d+\.\d+$</c>)
/// se exige en todo ambiente cuando viene. Ningún mensaje lleva un valor: nombran la clave.
/// </summary>
internal sealed partial class MetaAppOptionsValidator(IHostEnvironment environment) : IValidateOptions<MetaAppOptions>
{
    [GeneratedRegex(@"^v\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();

    public ValidateOptionsResult Validate(string? name, MetaAppOptions options)
    {
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.GraphApiVersion) && !VersionShape().IsMatch(options.GraphApiVersion.Trim()))
        {
            failures.Add($"{MetaAppOptions.SectionName}:GraphApiVersion must look like v24.0.");
        }

        if (environment.IsProduction())
        {
            Require(options.AppId, nameof(MetaAppOptions.AppId), failures);
            Require(options.ConfigId, nameof(MetaAppOptions.ConfigId), failures);
            Require(options.GraphApiVersion, nameof(MetaAppOptions.GraphApiVersion), failures);
            Require(options.AppSecret, nameof(MetaAppOptions.AppSecret), failures);
            Require(options.WebhookVerifyToken, nameof(MetaAppOptions.WebhookVerifyToken), failures);
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void Require(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{MetaAppOptions.SectionName}:{key} is required in Production: without it WhatsApp cannot be connected nor its webhook verified.");
        }
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/IMetaAppSettings.cs`:

```csharp
namespace Modules.Integrations.Application;

/// <summary>
/// Lo público de la app de Meta (spec 2026-10-09 §6.1): <c>appId</c> y <c>configId</c> viajan en la URL
/// del popup de Embedded Signup. <see cref="IsConfigured"/> es D-M3: sin la sección (sólo fuera de
/// Production), <c>whatsapp-cloud</c> no sale en el catálogo y el canje responde
/// <c>code_exchange_failed</c>. El secreto nunca pasa por acá.
/// </summary>
public interface IMetaAppSettings
{
    bool IsConfigured { get; }

    string? AppId { get; }

    string? ConfigId { get; }

    string GraphApiVersion { get; }
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaAppSettings.cs`:

```csharp
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.</summary>
internal sealed class MetaAppSettings(IOptions<MetaAppOptions> options) : IMetaAppSettings
{
    private readonly MetaAppOptions settings = options.Value;

    public bool IsConfigured => settings.IsConfigured();

    public string? AppId => string.IsNullOrWhiteSpace(settings.AppId) ? null : settings.AppId.Trim();

    public string? ConfigId => string.IsNullOrWhiteSpace(settings.ConfigId) ? null : settings.ConfigId.Trim();

    public string GraphApiVersion =>
        string.IsNullOrWhiteSpace(settings.GraphApiVersion)
            ? MetaAppOptions.DefaultGraphApiVersion
            : settings.GraphApiVersion.Trim();
}
```

En `IntegrationsInfrastructureExtensions.AddIntegrationsInfrastructure`, después del bloque de Zenvia (línea 60) y antes de `return services;`:

```csharp
        // Spec 2026-10-09 §9 (decisión 2): la app de Meta de toda la plataforma. En Production
        // ValidateOnStart exige las cinco claves; fuera, el módulo arranca sin ellas y D-M3 decide.
        services.AddOptions<MetaAppOptions>()
            .Bind(configuration.GetSection(MetaAppOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetaAppOptions>, MetaAppOptionsValidator>();
        services.AddSingleton<IMetaAppSettings, MetaAppSettings>();
```

Agrega `using Modules.Integrations.Infrastructure.Meta;` al archivo.

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~MetaAppOptionsValidatorTests"
```

Esperado: 9 correctas, 0 con error.

- [ ] **Step 5: `appsettings.example.json` y `ConfigurationExampleTests`**

En `src/Api/appsettings.example.json`, después del bloque `"Integrations": { … }` (línea 116, antes de la `}` final) agrega una sección hermana:

```json
  "Meta": {
    "App": {
      "AppId": "1234567890",
      "ConfigId": "9876543210",
      "GraphApiVersion": "v24.0",
      "AppSecret": "<user-secrets: Meta:App:AppSecret>",
      "WebhookVerifyToken": "<user-secrets: Meta:App:WebhookVerifyToken, aleatorio de 32+ caracteres>"
    }
  }
```

`IsConfigured` es un **método** y no una propiedad a propósito: `ConfigurationExampleTests.Keys` recorre toda propiedad pública con `get` y la pediría en el ejemplo como `Meta:App:IsConfigured`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: todas correctas (las claves `Meta:App:*` existen en el ejemplo; el contenedor sigue construyendo).

- [ ] **Step 6: El harness de Integrations fija la sección**

En `IntegrationsApiHarness.QepApiFactory.ConfigureWebHost`, después de `builder.UseSetting("Integrations:Zenvia:BaseUrl", ZenviaBaseUrl);`:

```csharp
            // Spec 2026-10-09 §9: fijadas, nunca heredadas. Con las cinco, whatsapp-cloud es visible
            // (D-M3); las pruebas que quieren el caso contrario las vacían con WithMetaApp.
            builder.UseSetting("Meta:App:AppId", MetaAppId);
            builder.UseSetting("Meta:App:ConfigId", MetaConfigId);
            builder.UseSetting("Meta:App:GraphApiVersion", "v24.0");
            builder.UseSetting("Meta:App:AppSecret", SentinelMetaAppSecret);
            builder.UseSetting("Meta:App:WebhookVerifyToken", SentinelMetaVerifyToken);
```

Y las constantes, junto a `SentinelZenviaBody`:

```csharp
    public const string MetaAppId = "100200300";

    public const string MetaConfigId = "400500600";

    /// <summary>Centinelas: la prueba de fugas los persigue igual que al token de Zenvia.</summary>
    public const string SentinelMetaAppSecret = "meta-app-secret-SENTINEL-9e8d7c";

    public const string SentinelMetaVerifyToken = "meta-verify-token-SENTINEL-6b5a4f-0123456789";
```

Y un helper junto a `WithSecretProtection`:

```csharp
    /// <summary>Vacía la sección Meta:App (D-M3): whatsapp-cloud desaparece del catálogo.</summary>
    public static WebApplicationFactory<Program> WithoutMetaApp(this WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var key in new[] { "AppId", "ConfigId", "AppSecret", "WebhookVerifyToken" })
            {
                builder.UseSetting($"Meta:App:{key}", string.Empty);
            }
        });
```

Corre una clase existente para confirmar que el host sigue arrancando:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~ConnectionsApiTests"
```

Esperado: todas correctas.

- [ ] **Step 7: Formato y commit**

Corre el chequeo de formato de «Antes de empezar». Commit:

```text
feat(integrations): sección Meta:App con validador y puerto IMetaAppSettings
```

---

### Task 2: Módulo `messaging` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:37-50`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:108-110`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs:27-28`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs:10-15, 46-52`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddMessagingModuleKey.cs` (+ `.Designer.cs`, snapshot regenerado)

**Interfaces:**
- Consumes: `TenantModuleKey.Define`, `TenantModuleKeys.All`.
- Produces: `TenantModuleKeys.Messaging` (`"messaging"`), último de `All`, sin dependencias, fuera de `DefaultForNewTenants`.

- [ ] **Step 1: Pruebas RED**

En `TenantModuleKeysTests.cs`:

```csharp
    [Fact]
    public void AllKeepsTheContractOrder()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging"],
            TenantModuleKeys.All.Select(key => key.Value));
    }

    // Spec 2026-10-09 §6.2: messaging es vendible y se prende por tenant, como pos.
    [Fact]
    public void DefaultForNewTenantsIsEverythingButPosAndMessaging()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting"],
            TenantModuleKeys.DefaultForNewTenants.Select(key => key.Value));
    }

    [Fact]
    public void MessagingHasNoDependencies() =>
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Messaging));
```

(Reemplaza `AllKeepsTheContractOrder` y `DefaultForNewTenantsIsEverythingButPos`; agrega el tercero.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests"
```

Esperado: `error CS0117: 'TenantModuleKeys' does not contain a definition for 'Messaging'`.

- [ ] **Step 2: La clave**

En `TenantModuleKey.cs`:

```csharp
    public static readonly TenantModuleKey Pos = TenantModuleKey.Define("pos");

    /// <summary>Spec 2026-10-09, decisión 4: la bandeja de WhatsApp. Último de la lista, sin
    /// dependencias y fuera de <see cref="DefaultForNewTenants"/>.</summary>
    public static readonly TenantModuleKey Messaging = TenantModuleKey.Define("messaging");

    public static readonly IReadOnlyList<TenantModuleKey> All =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos, Messaging];

    /// <summary>Los seis de hoy, sin <c>pos</c> ni <c>messaging</c>: el backfill y el signup con el interruptor prendido.</summary>
```

En `TenancyDbContext.cs:110` el CHECK:

```csharp
                "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos','messaging')");
```

En `ChangeTenantModules.cs:28` (P13): `.WithMessage("A batch has at most eight changes.");` — y busca el texto viejo en pruebas:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
rg -n "at most seven" src tests
```

Si aparece en una prueba, cámbiala a `eight`.

- [ ] **Step 3: Migración**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet ef migrations add AddMessagingModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

Abre el `Up` generado: tiene que contener `DropCheckConstraint("CK_tenant_modules_module_key", …)` y `AddCheckConstraint("CK_tenant_modules_module_key", …, "module_key IN ('catalog',…,'messaging')")`. Agrega encima del `Up` este comentario:

```csharp
    /// <summary>Spec 2026-10-09 §6.2: otro módulo = otra migración que cambia el CHECK, a propósito
    /// (<c>TenantModuleKey.cs</c>). <c>messaging</c> no entra en <c>DefaultForNewTenants</c>: se prende por tenant.</summary>
```

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-build --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~TenantModuleSetTests|FullyQualifiedName~ChangeTenantModulesHandlerTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --filter "FullyQualifiedName~TenantModulesApiTests"
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build --filter "FullyQualifiedName~ModuleEntitlementMaskTests"
```

Esperado: todas correctas. Si `TenantModulesApiTests` compara la lista de `/modules` contra siete claves literales, actualízala a ocho con `messaging` al final (es el contrato: «último de la lista»).

- [ ] **Step 5: Formato y commit**

```text
feat(tenancy): módulo messaging, último del catálogo y fuera del signup
```

---

### Task 3: Scaffolding de Messaging, permisos y roles

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/Modules.Messaging.Domain.csproj`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/Modules.Messaging.Application.csproj`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Modules.Messaging.Infrastructure.csproj`
- Create: `src/Modules/Messaging/Modules.Messaging.Api/Modules.Messaging.Api.csproj`
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` (vacío por ahora)
- Create: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (vacío por ahora)
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/Modules.Messaging.UnitTests.csproj`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/Modules.Messaging.IntegrationTests.csproj`
- Create: `tests/ArchitectureTests/ArchitectureTests/MessagingLayerTests.cs`
- Modify: `Backend.slnx` (carpeta `/src/Modules/Messaging/` y `/tests/Modules/Messaging/`)
- Modify: `src/Api/Api.csproj`, `src/Bootstrapper/Bootstrapper.csproj`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`using`, rol `admin` `:764-766`, rol `advisor` `:788-790`, `PermissionDefinition` después de `:1200`, políticas `:1462-1467`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:171-204`
- Modify: todos los `packages.lock.json` que cambien (`dotnet restore --force-evaluate`)

**Interfaces:**
- Consumes: `IntegrationsLayerTests` como molde; `RoleDefinition`/`PermissionDefinition` (`RoleCatalog.cs:12-34`).
- Produces: cuatro ensamblados `Modules.Messaging.*`, `MessagingPermissions.ConversationRead = "messaging.conversation.read"`, `ConversationManage = "messaging.conversation.manage"`, `MessagingErrorCodes`.

- [ ] **Step 1: Proyectos**

`src/Modules/Messaging/Modules.Messaging.Domain/Modules.Messaging.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- TenantModuleKeys para nombrar el módulo propio (spec 2026-10-09 §6.4): la única referencia
         a otro módulo que el dominio tiene permitida (MessagingLayerTests). -->
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Domain\BuildingBlocks.Domain.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Domain\Modules.Tenancy.Domain.csproj" />
  </ItemGroup>
</Project>
```

`…/Modules.Messaging.Application/Modules.Messaging.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <InternalsVisibleTo Include="Modules.Messaging.UnitTests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Messaging.Domain\Modules.Messaging.Domain.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Application\Modules.Tenancy.Application.csproj" />
    <!-- AuditActorType: la auditoría atómica va por un puerto propio (spec §6.4). -->
    <ProjectReference Include="..\..\Audit\Modules.Audit.Domain\Modules.Audit.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FluentValidation" />
  </ItemGroup>
</Project>
```

`…/Modules.Messaging.Infrastructure/Modules.Messaging.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- IHttpClientFactory e IMemoryCache salen del shared framework, como en Integrations. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Messaging.Application\Modules.Messaging.Application.csproj" />
    <ProjectReference Include="..\Modules.Messaging.Domain\Modules.Messaging.Domain.csproj" />
    <ProjectReference Include="..\..\Audit\Modules.Audit.Domain\Modules.Audit.Domain.csproj" />
    <ProjectReference Include="..\..\Audit\Modules.Audit.Infrastructure\Modules.Audit.Infrastructure.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Application\Modules.Tenancy.Application.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>
  <ItemGroup>
    <!-- Workers, parser, ingesta y verificador son internal; las pruebas los construyen o los
         buscan en el host. -->
    <InternalsVisibleTo Include="Modules.Messaging.UnitTests" />
    <InternalsVisibleTo Include="Modules.Messaging.IntegrationTests" />
  </ItemGroup>
</Project>
```

`…/Modules.Messaging.Api/Modules.Messaging.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Messaging.Application\Modules.Messaging.Application.csproj" />
    <ProjectReference Include="..\Modules.Messaging.Domain\Modules.Messaging.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
</Project>
```

`tests/Modules/Messaging/Modules.Messaging.UnitTests/Modules.Messaging.UnitTests.csproj` (copia de `Modules.Integrations.UnitTests.csproj` con las tres referencias a `Modules.Messaging.{Application,Domain,Infrastructure}`).

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/Modules.Messaging.IntegrationTests.csproj` (copia literal de `Modules.Integrations.IntegrationTests.csproj`: referencia `src\Api\Api.csproj`).

Archivos mínimos para que compile:

`src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`:

```csharp
namespace Modules.Messaging.Domain;

/// <summary>Los códigos del spec 2026-10-09 §10.1, en un solo lugar.</summary>
public static class MessagingErrorCodes
{
    public const string WindowClosed = "messaging.window_closed";
    public const string ConnectionUnavailable = "messaging.connection_unavailable";
    public const string ConversationNotOpen = "messaging.conversation.not_open";
    public const string MessageRejected = "messaging.message.rejected";
    public const string AlreadyResolved = "messaging.conversation.already_resolved";
    public const string AlreadyOpen = "messaging.conversation.already_open";
    public const string ConversationNotFound = "messaging.conversation.not_found";
    public const string MessageNotFound = "messaging.message.not_found";
    public const string WebhookSignatureInvalid = "messaging.webhook.signature_invalid";
}
```

`src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-09, decisión 5: de núcleo (<c>RequiredModules</c> vacío) y en <c>admin</c> y <c>advisor</c>.
/// Si llevaran <c>[messaging]</c>, la máscara quitaría el permiso y el 403 saldría como
/// <c>authorization.denied</c>, sin el código <c>tenancy.module_not_enabled</c> que el contrato pide; por
/// eso cada handler llama <c>TenantModuleGuard.EnsureEnabledAsync</c>. Cada uno necesita su política en
/// <c>AddAuthorization</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500.
/// </summary>
public static class MessagingPermissions
{
    public const string ConversationRead = "messaging.conversation.read";
    public const string ConversationManage = "messaging.conversation.manage";
}
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Messaging.Infrastructure;

public static class MessagingInfrastructureExtensions
{
    /// <summary>Se llena en la Task 10 (DbContext, opciones) y siguientes (workers, clientes).</summary>
    public static IServiceCollection AddMessagingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services;
}
```

`src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    /// <summary>Se llena desde la Task 14: lista, detalle, hilo, envío, leído, resolver, reabrir,
    /// búsqueda y medio, todos bajo <c>/api/v1/tenants/{tenantId:guid}/messaging</c>.</summary>
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints) => endpoints;
}
```

- [ ] **Step 2: Solución, referencias y locks**

En `Backend.slnx`, después de la carpeta `/src/Modules/Integrations/`:

```xml
  <Folder Name="/src/Modules/Messaging/">
    <Project Path="src/Modules/Messaging/Modules.Messaging.Api/Modules.Messaging.Api.csproj" />
    <Project Path="src/Modules/Messaging/Modules.Messaging.Application/Modules.Messaging.Application.csproj" />
    <Project Path="src/Modules/Messaging/Modules.Messaging.Domain/Modules.Messaging.Domain.csproj" />
    <Project Path="src/Modules/Messaging/Modules.Messaging.Infrastructure/Modules.Messaging.Infrastructure.csproj" />
  </Folder>
```

Y después de `/tests/Modules/Integrations/`:

```xml
  <Folder Name="/tests/Modules/Messaging/">
    <Project Path="tests/Modules/Messaging/Modules.Messaging.IntegrationTests/Modules.Messaging.IntegrationTests.csproj" />
    <Project Path="tests/Modules/Messaging/Modules.Messaging.UnitTests/Modules.Messaging.UnitTests.csproj" />
  </Folder>
```

`src/Api/Api.csproj`, después de las dos de Integrations:

```xml
    <ProjectReference Include="..\Modules\Messaging\Modules.Messaging.Api\Modules.Messaging.Api.csproj" />
    <ProjectReference Include="..\Modules\Messaging\Modules.Messaging.Infrastructure\Modules.Messaging.Infrastructure.csproj" />
```

`src/Bootstrapper/Bootstrapper.csproj`, después de las dos de Integrations:

```xml
    <ProjectReference Include="..\Modules\Messaging\Modules.Messaging.Application\Modules.Messaging.Application.csproj" />
    <ProjectReference Include="..\Modules\Messaging\Modules.Messaging.Infrastructure\Modules.Messaging.Infrastructure.csproj" />
```

`tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`: agrega las cuatro referencias a `Modules.Messaging.{Domain,Application,Infrastructure,Api}` junto a las de Integrations (mira cómo están declaradas ahí y copia la forma).

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet restore --force-evaluate
git status --short -- '*.lock.json'
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
```

Esperado: locks nuevos en los seis proyectos nuevos y modificados en `Api`, `Bootstrapper` y `ArchitectureTests`; build en 0 warnings.

- [ ] **Step 3: Pruebas RED de capas y de composición**

`tests/ArchitectureTests/ArchitectureTests/MessagingLayerTests.cs`:

```csharp
using System.Reflection;
using Modules.Messaging.Api;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Messaging (spec 2026-10-09 §6.4), copia de <see cref="IntegrationsLayerTests"/>.
/// Messaging nunca referencia Integrations, Customers ni Storage: lo que necesita de ellos entra por
/// puertos propios con adaptadores en Bootstrapper (<c>IMessagingConnectionDirectory</c>,
/// <c>IMessagingCustomerDirectory</c>, <c>IMessagingMediaStore</c>).
/// </summary>
public sealed class MessagingLayerTests
{
    private static readonly Assembly Domain = typeof(MessagingErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(MessagingPermissions).Assembly;
    private static readonly Assembly Infrastructure = typeof(MessagingInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(MessagingEndpoints).Assembly;

    [Fact]
    public void DomainDoesNotReferenceOuterLayers() =>
        AssertDoesNotReference(Domain, Application, Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi() =>
        AssertDoesNotReference(Application, Infrastructure, Api);

    [Fact]
    public void InfrastructureDoesNotReferenceApi() =>
        AssertDoesNotReference(Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));

    // Spec §6.4: Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext) y Audit (AuditActorType).
    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Messaging", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));

    [Fact]
    public void DomainOnlyReferencesTheTenancyDomainAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Messaging", StringComparison.Ordinal) &&
            !string.Equals(name, "Modules.Tenancy.Domain", StringComparison.Ordinal));

    // Spec §6.4: ni siquiera Infrastructure: los adaptadores viven en Bootstrapper.
    [Theory]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Storage")]
    public void NoLayerReferencesIntegrationsCustomersOrStorage(string forbiddenPrefix)
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(Assembly source, params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
```

En `CompositionRootTests.cs` reemplaza `PermissionDiscoveryFindsTheFortySixConstants` (`:171-176`) por:

```csharp
    [Fact]
    public void PermissionDiscoveryFindsTheFortyEightConstants()
    {
        // 46 + los dos de Messaging (spec 2026-10-09 §6.3).
        Assert.Equal(48, PermissionConstants().Length);
    }
```

Y agrega después de `TheAdminRoleCarriesTheIntegrationsPermissionsAsCore`:

```csharp
    /// <summary>Spec 2026-10-09 §6.3 (decisión 5): en <c>admin</c> y <c>advisor</c>, de núcleo; el
    /// módulo lo revisa cada handler con <c>TenantModuleGuard</c>.</summary>
    [Fact]
    public void TheAdminAndAdvisorRolesCarryTheMessagingPermissionsAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] messagingPermissions =
            [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage];

        foreach (var role in new[] { "admin", "advisor" })
        {
            Assert.All(messagingPermissions, permission => Assert.Contains(permission, catalog.PermissionsFor(role)));
        }

        var definitions = catalog.ListPermissions()
            .Where(definition => messagingPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Messaging", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }
```

Agrega `using Modules.Messaging.Application;` al archivo.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: `MessagingLayerTests` en verde (los proyectos ya existen); `PermissionDiscoveryFindsTheFortyEightConstants` y `TheAdminAndAdvisorRolesCarryTheMessagingPermissionsAsCore` fallan (`Assert.Equal() Failure: Expected 48 Actual 46`; `Assert.Contains() Failure`). Copia la salida.

- [ ] **Step 4: Permisos en roles, definiciones y políticas**

En `QepServiceCollectionExtensions.cs`:

1. `using Modules.Messaging.Application;` y `using Modules.Messaging.Infrastructure;` junto a los de Integrations.
2. Rol `admin` (`:764-766`), después de `IntegrationsPermissions.ConnectionManage`:

```csharp
                IntegrationsPermissions.ConnectionManage,
                // Spec 2026-10-09 (Mensajería), decisión 5: la bandeja de WhatsApp. De núcleo; el
                // módulo messaging lo revisa cada handler. El rol vive en código: sin migración.
                MessagingPermissions.ConversationRead,
                MessagingPermissions.ConversationManage
```

3. Rol `advisor`, después de `ReportingPermissions.QuotationRead` (`:789`):

```csharp
                ReportingPermissions.QuotationRead,
                // Spec 2026-10-09 (Mensajería), decisión 5: la asesora lee y responde la bandeja.
                MessagingPermissions.ConversationRead,
                MessagingPermissions.ConversationManage
```

4. Definiciones, después de la de `IntegrationsPermissions.ConnectionManage` (`:1193-1200`):

```csharp
        // Spec 2026-10-09 §6.3: categoría "Messaging" y núcleo (RequiredModules vacío), para que el
        // enmascarado por módulos no las toque y el 403 con módulo apagado salga con su código.
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.ConversationRead,
            "Ver la bandeja de WhatsApp",
            "Permite ver las conversaciones de WhatsApp del tenant, su hilo y sus archivos.",
            "Messaging",
            "medium",
            RequiredModules: []));
        services.AddSingleton(new PermissionDefinition(
            MessagingPermissions.ConversationManage,
            "Responder en la bandeja de WhatsApp",
            "Permite responder mensajes, marcarlos como leídos y resolver o reabrir conversaciones.",
            "Messaging",
            "medium",
            RequiredModules: []));
```

5. Políticas, después de `IntegrationsPermissions.ConnectionManage` (`:1465-1467`):

```csharp
            .AddPolicy(
                IntegrationsPermissions.ConnectionManage,
                policy => AddPermissionRequirement(policy, IntegrationsPermissions.ConnectionManage))
            .AddPolicy(
                MessagingPermissions.ConversationRead,
                policy => AddPermissionRequirement(policy, MessagingPermissions.ConversationRead))
            .AddPolicy(
                MessagingPermissions.ConversationManage,
                policy => AddPermissionRequirement(policy, MessagingPermissions.ConversationManage));
```

6. Registro de infraestructura, después de `services.AddIntegrationsInfrastructure(configuration);` (`:549`):

```csharp
        // Messaging (spec 2026-10-09): la bandeja de WhatsApp. Sólo ve Tenancy y Audit; conexiones,
        // clientes y almacenamiento entran por adaptadores que se registran más abajo.
        services.AddMessagingInfrastructure(configuration);
```

- [ ] **Step 5: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: todas correctas (incluida `ConfigurationExampleTests`, porque Messaging todavía no declara opciones).

- [ ] **Step 6: Formato y commit**

```text
feat(messaging): proyectos del módulo, pruebas de capas y permisos de la bandeja
```

---
### Task 4: Catálogo de Integrations — campos internos, `onboarding` y el proveedor `whatsapp-cloud`

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs` (`FieldDefinition` `:26-106`, `IntegrationProvider` `:113-185`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs:5-51`
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationsErrorCodes.cs:7-29`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsDtos.cs:13-22`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsSupport.cs:36-58, 80-91`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/GetIntegrationsCatalog.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionInputRules.cs` (constantes `:17-29`, `CheckGroup` `:96-150`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/CreateConnection.cs:35-47`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/UpdateConnection.cs:74-80`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsDbContext.cs:47-48` (sin cambio de código: el `CHECK` sale de `All`; sólo la migración)
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/Migrations/<timestamp>_AddWhatsAppCloudProvider.cs`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs`, `CreateConnectionValidatorTests.cs`, `UpdateConnectionHandlerTests.cs`, `ReadHandlersTests.cs`; `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionsApiTests.cs`

**Interfaces:**
- Consumes: `IMetaAppSettings` (Task 1), `TenantModuleKeys.Messaging` (Task 2).
- Produces: `ProviderOnboarding`, `FieldDefinition.Internal`, `IntegrationProvider.Onboarding`, `IntegrationProvider.CatalogFields` (no internos), `WhatsAppCloudFieldKeys`, `IntegrationProviders.WhatsAppCloud`, `ProviderOnboardingResponse`, `ProviderResponse.Onboarding`, `IntegrationsErrorCodes.WhatsAppCodeExchangeFailed|WhatsAppRegistrationFailed|WhatsAppNumberAlreadyConnected`, `ConnectionInputRules.ReadOnlyFieldMessage`, `ProviderUsesMetaSignupMessage`.

- [ ] **Step 1: Pruebas RED del dominio**

En `IntegrationCatalogTests.cs` agrega:

```csharp
    // Spec 2026-10-09 §6.1: la fila literal del proveedor nuevo.
    [Fact]
    public void WhatsAppCloudIsInTheCatalogWithItsFieldsAndOnboarding()
    {
        var provider = IntegrationProviders.WhatsAppCloud;

        Assert.Equal("whatsapp-cloud", provider.Key);
        Assert.Equal("WhatsApp Business (Meta)", provider.DisplayName);
        Assert.Equal(IntegrationCategory.Messaging, provider.Category);
        Assert.Equal([TenantModuleKeys.Messaging], provider.ConsumingModules);
        Assert.Equal(5, provider.MaxConnections);
        Assert.Equal(ProviderOnboarding.MetaEmbeddedSignup, provider.Onboarding);
        Assert.Equal(
            ["displayPhoneNumber", "verifiedName", "phoneNumberId", "wabaId", "qualityRating", "accessToken"],
            provider.Fields.Select(field => field.Key));
        Assert.Equal(["displayPhoneNumber", "verifiedName"], provider.CatalogFields.Select(field => field.Key));
        Assert.True(provider.FindField("accessToken")!.Internal);
        Assert.True(provider.FindField("accessToken")!.IsSecret);
        Assert.Equal(2048, provider.FindField("accessToken")!.MaxLength);
        Assert.Null(provider.FindField("displayPhoneNumber")!.Pattern);
        Assert.Equal([IntegrationProviders.Zenvia, IntegrationProviders.WhatsAppCloud], IntegrationProviders.All);
        Assert.Equal(ProviderOnboarding.Form, IntegrationProviders.Zenvia.Onboarding);
    }

    // Meta devuelve el número formateado: "+57 300 123 4567" tiene que pasar tal cual.
    [Fact]
    public void TheDisplayPhoneNumberAcceptsMetaFormatting() =>
        Assert.True(IntegrationProviders.WhatsAppCloud.FindField("displayPhoneNumber")!.HasValidShape("+57 300 123 4567"));
```

En `CreateConnectionValidatorTests.cs` agrega (usa el helper `ErrorsAsync` que ya existe en ese archivo):

```csharp
    // Spec §6.1: el formulario genérico no puede crear una conexión de Meta sin token.
    [Fact]
    public async Task AMetaSignupProviderIsRejectedOnTheProviderKey()
    {
        var errors = await ErrorsAsync(new CreateConnectionCommand(
            Guid.CreateVersion7(), "whatsapp-cloud", "Ventas",
            new Dictionary<string, string?>(), new Dictionary<string, string?>()));

        Assert.Equal(["providerKey"], errors.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("flujo de Meta", errors["providerKey"][0], StringComparison.Ordinal);
    }
```

Si `ErrorsAsync` no existe con esa forma, mira cómo las demás pruebas del archivo obtienen el mapa de errores y usa eso.

En `ReadHandlersTests.cs` (catálogo) agrega:

```csharp
    // Spec §5.2: cada proveedor trae onboarding; los campos internos no salen del catálogo.
    [Fact]
    public async Task TheCatalogCarriesOnboardingAndHidesInternalFields()
    {
        var bed = new IntegrationsTestBed();
        bed.MetaApp.Configured = true;

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), TestContext.Current.CancellationToken);

        var zenvia = Assert.Single(catalog.Providers, provider => provider.Key == "zenvia");
        Assert.Equal("Form", zenvia.Onboarding.Kind);
        Assert.Null(zenvia.Onboarding.AppId);
        var whatsapp = Assert.Single(catalog.Providers, provider => provider.Key == "whatsapp-cloud");
        Assert.Equal("MetaEmbeddedSignup", whatsapp.Onboarding.Kind);
        Assert.Equal("100200300", whatsapp.Onboarding.AppId);
        Assert.Equal("400500600", whatsapp.Onboarding.ConfigId);
        Assert.Equal("v24.0", whatsapp.Onboarding.GraphApiVersion);
        Assert.Equal(["displayPhoneNumber", "verifiedName"], whatsapp.Fields.Select(field => field.Key));
    }

    // D-M3: sin Meta:App (sólo fuera de producción) whatsapp-cloud no sale.
    [Fact]
    public async Task WithoutMetaAppWhatsAppCloudIsNotInTheCatalog()
    {
        var bed = new IntegrationsTestBed();
        bed.MetaApp.Configured = false;

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(catalog.Providers, provider => provider.Key == "whatsapp-cloud");
    }
```

Para eso, `IntegrationsTestBed` gana un doble en `IntegrationsTestDoubles.cs`:

```csharp
internal sealed class FakeMetaAppSettings : IMetaAppSettings
{
    public bool Configured { get; set; } = true;

    public bool IsConfigured => Configured;

    public string? AppId => Configured ? "100200300" : null;

    public string? ConfigId => Configured ? "400500600" : null;

    public string GraphApiVersion => "v24.0";
}
```

y en el bed (`IntegrationsTestBed.*.cs`, donde se construyen `Catalog`, `Modules`, etc.) una propiedad `public FakeMetaAppSettings MetaApp { get; } = new();` que se pasa a `CatalogHandler()` como parámetro nuevo del handler (ver Step 3). El bed usa por defecto el catálogo real (`IntegrationProviderCatalog`) o uno falso: si es falso, agrégale `IntegrationProviders.WhatsAppCloud` para estas dos pruebas (`new FakeIntegrationProviderCatalog(IntegrationProviders.Zenvia, IntegrationProviders.WhatsAppCloud)`); y recuerda que el tenant del bed tiene que tener `messaging` efectivo en `FakeTenantModules.Sets` (o ninguna fila = stub = todo visible, que es el caso por defecto).

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationCatalogTests|FullyQualifiedName~CreateConnectionValidatorTests|FullyQualifiedName~ReadHandlersTests"
```

Esperado: `error CS0117: 'IntegrationProviders' does not contain a definition for 'WhatsAppCloud'` y hermanos.

- [ ] **Step 2: Dominio**

`IntegrationCatalog.cs`:

1. Enum nuevo después de `FieldKind`:

```csharp
/// <summary>Spec 2026-10-09 §6.1: cómo se conecta un proveedor. <c>Form</c> es el formulario genérico;
/// <c>MetaEmbeddedSignup</c> abre el popup de Meta y el backend llena los campos.</summary>
public enum ProviderOnboarding
{
    Form,
    MetaEmbeddedSignup,
}
```

2. `FieldDefinition`: parámetro final `bool isInternal = false`, asignación `Internal = isInternal;` y la propiedad:

```csharp
    /// <summary>Spec 2026-10-09 §6.1: lo llena el backend (Embedded Signup o el probador). No sale en
    /// <c>GET /catalog</c>, sí en <c>ConnectionResponse</c>, y <c>POST</c>/<c>PUT</c> lo rechazan.</summary>
    public bool Internal { get; }
```

3. `IntegrationProvider`: parámetro final `ProviderOnboarding onboarding = ProviderOnboarding.Form`, asignación `Onboarding = onboarding; CatalogFields = Fields.Where(field => !field.Internal).ToArray();` y las dos propiedades:

```csharp
    public ProviderOnboarding Onboarding { get; }

    /// <summary>Los que dibuja el formulario (sin internos). Secretos incluidos: el formulario los pide como password.</summary>
    public IReadOnlyList<FieldDefinition> CatalogFields { get; }
```

`IntegrationProviders.cs`, después de `ZenviaFieldKeys`:

```csharp
/// <summary>Spec 2026-10-09 §6.1. Los dos primeros son de sólo lectura para la pantalla; los otros
/// cuatro son internos (los escribe el handler de Embedded Signup o el probador).</summary>
public static class WhatsAppCloudFieldKeys
{
    public const string DisplayPhoneNumber = "displayPhoneNumber";
    public const string VerifiedName = "verifiedName";
    public const string PhoneNumberId = "phoneNumberId";
    public const string WabaId = "wabaId";
    public const string QualityRating = "qualityRating";
    public const string AccessToken = "accessToken";
}
```

Y en `IntegrationProviders`, después de `Zenvia`:

```csharp
    /// <summary>Spec 2026-10-09 §6.1: WhatsApp Business (Meta). Lo consume <c>messaging</c>; tope 5 por
    /// tenant. <c>displayPhoneNumber</c> no lleva el patrón E.164 de Zenvia: Meta lo devuelve formateado
    /// («+57 300 123 4567») y se guarda tal cual. El <c>accessToken</c> es un secreto interno: nunca lo
    /// escribe una persona.</summary>
    public static readonly IntegrationProvider WhatsAppCloud = new(
        "whatsapp-cloud",
        "WhatsApp Business (Meta)",
        IntegrationCategory.Messaging,
        [TenantModuleKeys.Messaging],
        [
            new FieldDefinition(
                WhatsAppCloudFieldKeys.DisplayPhoneNumber, "Número", FieldKind.Phone,
                required: false, maxLength: 32, pattern: null,
                invalidMessage: "El número que devolvió Meta no tiene una forma válida."),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.VerifiedName, "Nombre verificado", FieldKind.Text,
                required: false, maxLength: 512, pattern: null,
                invalidMessage: "El nombre verificado que devolvió Meta no tiene una forma válida."),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.PhoneNumberId, "Id del número en Meta", FieldKind.Text,
                required: false, maxLength: 32, pattern: @"^[0-9]{1,32}\z",
                invalidMessage: "El id del número tiene que ser numérico.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.WabaId, "Id de la cuenta de WhatsApp Business", FieldKind.Text,
                required: false, maxLength: 32, pattern: @"^[0-9]{1,32}\z",
                invalidMessage: "El id de la cuenta tiene que ser numérico.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.QualityRating, "Calidad del número", FieldKind.Text,
                required: false, maxLength: 16, pattern: null,
                invalidMessage: "La calidad que devolvió Meta no tiene una forma válida.", isInternal: true),
            new FieldDefinition(
                WhatsAppCloudFieldKeys.AccessToken, "Token de acceso", FieldKind.Secret,
                required: false, maxLength: 2048, pattern: null,
                invalidMessage: "El token que devolvió Meta no tiene una forma válida.", isInternal: true),
        ],
        maxConnections: 5,
        onboarding: ProviderOnboarding.MetaEmbeddedSignup);

    public static readonly IReadOnlyList<IntegrationProvider> All = [Zenvia, WhatsAppCloud];
```

`IntegrationsErrorCodes.cs`, dentro de `IntegrationsErrorCodes`:

```csharp
    // Spec 2026-10-09 §10.1, los tres de Embedded Signup.
    public const string WhatsAppCodeExchangeFailed = "integrations.whatsapp.code_exchange_failed";
    public const string WhatsAppRegistrationFailed = "integrations.whatsapp.registration_failed";
    public const string WhatsAppNumberAlreadyConnected = "integrations.whatsapp.number_already_connected";
```

- [ ] **Step 3: Application — DTO, visibilidad, catálogo y reglas de entrada**

`IntegrationsDtos.cs`:

```csharp
/// <summary>BFF: la tarjeta del catálogo decide si abre un formulario o el popup de Meta sin conocer el
/// proveedor. <c>appId</c> y <c>configId</c> son públicos (viajan en la URL del popup); el secreto nunca
/// sale. Con <c>Kind = "Form"</c> los otros tres van ausentes, para que la forma sea exactamente
/// <c>{ "kind": "Form" }</c>.</summary>
public sealed record ProviderOnboardingResponse(
    string Kind,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AppId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ConfigId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GraphApiVersion);
```

y `ProviderResponse` gana el parámetro final `ProviderOnboardingResponse Onboarding`.

`IntegrationsSupport.cs`:

- `ProviderVisibility.VisibleAsync` gana `IMetaAppSettings metaApp` (tercer parámetro) y filtra:

```csharp
        return catalog.All
            .Where(provider => provider.IsVisibleFor(modules))
            // D-M3: sin la app de Meta configurada (sólo fuera de Production) no hay cómo conectar.
            .Where(provider => provider.Onboarding != ProviderOnboarding.MetaEmbeddedSignup || metaApp.IsConfigured)
            .ToArray();
```

- `ConnectionMapping.ToProvider(provider, connectionCount, IMetaAppSettings metaApp)`: usa `provider.CatalogFields` en vez de `provider.Fields`, y termina con:

```csharp
            connectionCount,
            provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup
                ? new ProviderOnboardingResponse(nameof(ProviderOnboarding.MetaEmbeddedSignup), metaApp.AppId, metaApp.ConfigId, metaApp.GraphApiVersion)
                : new ProviderOnboardingResponse(nameof(ProviderOnboarding.Form), null, null, null));
```

`GetIntegrationsCatalog.cs`: el handler gana `IMetaAppSettings metaApp` en el constructor y lo pasa a `VisibleAsync` y a `ToProvider`. `ListConnectionsHandler` (`ListConnections.cs:25`) también llama `VisibleAsync`: pásale el mismo `metaApp` (constructor nuevo). Actualiza `IntegrationsTestBed` para construir los dos con `MetaApp`.

`ConnectionInputRules.cs`, constantes:

```csharp
    public const string ProviderUsesMetaSignupMessage = "Este proveedor se conecta desde el flujo de Meta.";
    public const string ReadOnlyFieldMessage = "Este campo lo llena el backend; no se puede editar.";
```

y en `CheckGroup`, dentro del primer `foreach (var key in values.Keys)`, **antes** del `continue` de la clave conocida:

```csharp
            if (provider.FindField(key) is { } known && known.IsSecret == secret)
            {
                // Spec 2026-10-09 §6.1: un campo interno, o cualquier campo de un proveedor con Embedded
                // Signup, lo escribe sólo el backend. Un valor vacío cuenta como "no vino".
                if ((known.Internal || provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup)
                    && !string.IsNullOrWhiteSpace(values[key]))
                {
                    yield return new ValidationFailure($"{prefix}.{key}", ReadOnlyFieldMessage);
                }

                continue;
            }
```

`CreateConnection.cs`, en el `Custom` de `ProviderKey`, después de resolver `provider` y antes de `CheckValues`:

```csharp
            // Spec 2026-10-09 §6.1: el formulario genérico no crea una conexión de Meta (quedaría sin token).
            if (provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure(
                    "providerKey", ConnectionInputRules.ProviderUsesMetaSignupMessage));
                return;
            }
```

`UpdateConnection.cs:74-80` (P6): la conexión de Meta conserva sus campos:

```csharp
        // Un fields null conserva los campos guardados; un secrets null no trae secretos nuevos. Con
        // Embedded Signup (spec 2026-10-09 §6.1) los campos los llena el backend: el request sólo puede
        // cambiar el nombre, y lo guardado se conserva tal cual (P6 del plan).
        var backendOwned = provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup;
        IReadOnlyDictionary<string, string?> requestedFields = command.Fields is { } sent && !backendOwned
            ? sent
            : connection.Fields.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        ConnectionInputRules.ThrowIfAny(
            ConnectionInputRules.CheckValues(provider, backendOwned ? command.Fields : requestedFields, command.Secrets, requireSecrets: false));
```

(`CheckValues` recibe lo que **llegó** para que un campo mandado a un proveedor de Meta salga 422; `Normalize` recibe `requestedFields`, que son los guardados.)

- [ ] **Step 4: Migración del CHECK**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet ef migrations add AddWhatsAppCloudProvider --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext -o Persistence/Migrations
```

El `Up` tiene que tener `DropCheckConstraint`/`AddCheckConstraint("CK_connections_provider_key", …, "provider_key IN ('zenvia','whatsapp-cloud')")` y nada más. Comentario encima del `Up`: `/// <summary>D10 del spec 2026-10-08: proveedor nuevo = migración del CHECK, a propósito.</summary>`.

- [ ] **Step 5: Verificar GREEN y barrer las pruebas que arman cuerpos a mano**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --no-build --filter "FullyQualifiedName~ConnectionsApiTests|FullyQualifiedName~IntegrationsPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todas correctas. Si `ConnectionsApiTests` compara el catálogo contra una forma literal (`"fields"` con `kind`…), agrégale `onboarding: { kind: "Form" }` a Zenvia y el segundo proveedor; además agrega **una** prueba nueva ahí:

```csharp
    // Spec §5.2 y D-M3: el catálogo con messaging activo trae whatsapp-cloud con su onboarding; sin
    // Meta:App no lo trae.
    [Fact]
    public async Task TheCatalogShowsWhatsAppCloudWithItsOnboardingOnlyWithMetaApp()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var json = await client.GetStringAsync(CatalogUrl(tenant.TenantId), Ct);
        using var document = JsonDocument.Parse(json);
        var whatsapp = document.RootElement.GetProperty("providers").EnumerateArray()
            .Single(provider => provider.GetProperty("key").GetString() == "whatsapp-cloud");
        Assert.Equal("MetaEmbeddedSignup", whatsapp.GetProperty("onboarding").GetProperty("kind").GetString());
        Assert.Equal(MetaAppId, whatsapp.GetProperty("onboarding").GetProperty("appId").GetString());
        Assert.Equal(["displayPhoneNumber", "verifiedName"], whatsapp.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("key").GetString()));
        var zenvia = document.RootElement.GetProperty("providers").EnumerateArray()
            .Single(provider => provider.GetProperty("key").GetString() == "zenvia");
        Assert.False(zenvia.GetProperty("onboarding").TryGetProperty("appId", out _));

        using var withoutMeta = factory.WithoutMetaApp();
        using var client2 = CreateClient(withoutMeta, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var json2 = await client2.GetStringAsync(CatalogUrl(tenant.TenantId), Ct);
        Assert.DoesNotContain("whatsapp-cloud", json2, StringComparison.Ordinal);
    }
```

con el helper nuevo del harness, al lado de `SetModuleStatusAsync`:

```csharp
    /// <summary>Prende un módulo que no viene con el signup (messaging, pos), como PosApiHarness.EnablePosAsync.</summary>
    public static Task<int> EnableModuleAsync(string connectionString, Guid tenantId, string moduleKey) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, @moduleKey, now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId),
            ("moduleKey", moduleKey));
```

- [ ] **Step 6: Formato y commit**

```text
feat(integrations): proveedor whatsapp-cloud, campos internos y onboarding en el catálogo
```

---

### Task 5: Rutas de Integrations (`connection_routes`) y puertos cross-tenant

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationConnectionRoute.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsPorts.cs` (dos puertos nuevos al final)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs:21-37, 69-85`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/ConnectionRouteRepository.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/ConnectionRoutes.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsDbContext.cs` (`ConfigureConnectionRoute`, constante `RouteExternalIndex`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsUnitOfWork.cs:29-35`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs:30-37`
- Create: migración `<timestamp>_AddConnectionRoutes.cs`
- Test: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionRoutesApiTests.cs` (nuevo), `ConsumerPortsTests.cs` (unit, `ListByProviderAsync`)

**Interfaces:**
- Consumes: `IntegrationsDbContext`, `IntegrationsUnitOfWork.IsUniqueViolation`.
- Produces: `IntegrationConnectionRoute`, `IConnectionRouteRepository`, `IConnectionRoutes`, `ConnectionRoute`, `ConnectionListing`, `IIntegrationConnections.ListByProviderAsync`, `IntegrationsDbContext.RouteExternalIndex = "IX_connection_routes_provider_external"`.

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionRoutesApiTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-09 §6.1, «Migraciones de Integrations»: el único (provider_key, external_id) impide que
/// un número quede conectado en dos tenants; borrar la conexión libera la ruta (cascada); el puerto
/// cross-tenant devuelve tenant, conexión y estado.
/// </summary>
public sealed class ConnectionRoutesApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameNumberCannotBeRoutedToTwoConnections()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var first = await SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Ventas", phoneNumberId: "111", wabaId: "222");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Otro", phoneNumberId: "111", wabaId: "999"));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected, error.Code);
        using var scope = factory.Services.CreateScope();
        var routes = scope.ServiceProvider.GetRequiredService<IConnectionRoutes>();
        var route = await routes.FindAsync("whatsapp-cloud", "111", Ct);
        Assert.NotNull(route);
        Assert.Equal(first, route.ConnectionId);
        Assert.Equal(ConnectionStatus.Active, route.Status);
        Assert.Null(await routes.FindAsync("whatsapp-cloud", "000", Ct));
        var byAccount = await routes.FindByAccountAsync("whatsapp-cloud", "222", Ct);
        Assert.Equal([first], byAccount.Select(entry => entry.ConnectionId));
    }

    [Fact]
    public async Task DeletingTheConnectionFreesTheRoute()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var id = await SeedWhatsAppConnectionAsync(factory, tenantId, "Ventas", phoneNumberId: "111", wabaId: "222");

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
            repository.Remove((await repository.FindAsync(tenantId, id, Ct))!);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.connection_routes"));
        var again = await SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Ventas", phoneNumberId: "111", wabaId: "222");
        Assert.NotEqual(id, again);
    }

    // ListByProviderAsync trae todas (no sólo Active): la bandeja muestra el nombre también en una pausada.
    [Fact]
    public async Task ListByProviderReturnsEveryStatusOfTheTenantOnly()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var active = await SeedWhatsAppConnectionAsync(factory, tenantId, "Ventas", "111", "222");
        var paused = await SeedWhatsAppConnectionAsync(factory, tenantId, "Soporte", "333", "222");
        await SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Ajeno", "444", "555");
        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
            (await repository.FindAsync(tenantId, paused, Ct))!.Pause(DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope2 = factory.Services.CreateScope();
        var listing = await scope2.ServiceProvider.GetRequiredService<IIntegrationConnections>()
            .ListByProviderAsync(tenantId, "whatsapp-cloud", Ct);

        Assert.Equal(
            [(paused, "Soporte", ConnectionStatus.Paused), (active, "Ventas", ConnectionStatus.Active)],
            listing.Select(entry => (entry.Id, entry.Name, entry.Status)));
    }
}
```

Y en `IntegrationsApiHarness`, junto a `SeedConnectionAsync`:

```csharp
    public const string SentinelMetaAccessToken = "meta-access-token-SENTINEL-2c3d4e";

    /// <summary>Una conexión whatsapp-cloud con su ruta, escrita directo por el repositorio (sin Meta).</summary>
    public static async Task<Guid> SeedWhatsAppConnectionAsync(
        WebApplicationFactory<Program> host, Guid tenantId, string name, string phoneNumberId, string wabaId,
        string accessToken = SentinelMetaAccessToken)
    {
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var connection = IntegrationConnection.Create(
            IntegrationProviders.WhatsAppCloud,
            tenantId,
            name,
            new Dictionary<string, string>
            {
                [WhatsAppCloudFieldKeys.DisplayPhoneNumber] = "+57 300 123 4567",
                [WhatsAppCloudFieldKeys.VerifiedName] = "Prueba",
                [WhatsAppCloudFieldKeys.PhoneNumberId] = phoneNumberId,
                [WhatsAppCloudFieldKeys.WabaId] = wabaId,
                [WhatsAppCloudFieldKeys.QualityRating] = "GREEN",
            },
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.AccessToken] = accessToken },
            protector.Protect,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        scope.ServiceProvider.GetRequiredService<IConnectionRouteRepository>().Add(
            IntegrationConnectionRoute.Create(IntegrationProviders.WhatsAppCloud.Key, phoneNumberId, wabaId, tenantId, connection.Id));
        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~ConnectionRoutesApiTests"
```

Esperado: `error CS0246 … 'IConnectionRouteRepository'` (y `IntegrationConnectionRoute`, `IConnectionRoutes`, `ListByProviderAsync`).

- [ ] **Step 2: Dominio y puertos**

`src/Modules/Integrations/Modules.Integrations.Domain/IntegrationConnectionRoute.cs`:

```csharp
namespace Modules.Integrations.Domain;

/// <summary>
/// Spec 2026-10-09 §6.1: de qué tenant y conexión es un id externo del proveedor (el
/// <c>phone_number_id</c> de Meta), para que el webhook —que no tiene tenant— enrute. <c>AccountId</c>
/// es la WABA (D-M2): <c>account_update</c> llega por cuenta, no por número. Una fila por conexión, en la
/// misma transacción que ella; se borra en cascada.
/// </summary>
public sealed class IntegrationConnectionRoute
{
    public const int ExternalIdMaxLength = 64;

    private IntegrationConnectionRoute()
    {
        ProviderKey = string.Empty;
        ExternalId = string.Empty;
    }

    public string ProviderKey { get; private set; }

    public string ExternalId { get; private set; }

    public string? AccountId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public static IntegrationConnectionRoute Create(
        string providerKey, string externalId, string? accountId, Guid tenantId, Guid connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        if (externalId.Length > ExternalIdMaxLength || (accountId is not null && accountId.Length > ExternalIdMaxLength))
        {
            throw new ArgumentOutOfRangeException(nameof(externalId), $"A route id is at most {ExternalIdMaxLength} characters.");
        }

        return new IntegrationConnectionRoute
        {
            ProviderKey = providerKey,
            ExternalId = externalId.Trim(),
            AccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim(),
            TenantId = tenantId,
            ConnectionId = connectionId,
        };
    }
}
```

`IntegrationsPorts.cs`, al final:

```csharp
/// <summary>Spec 2026-10-09 §6.1: la ruta se crea en la misma transacción que la conexión.</summary>
public interface IConnectionRouteRepository
{
    void Add(IntegrationConnectionRoute route);

    /// <summary>Lectura indexada previa al canje (§8.1, paso 3); la garantía final la da el índice único.</summary>
    Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken cancellationToken);
}

public sealed record ConnectionRoute(Guid TenantId, Guid ConnectionId, ConnectionStatus Status);

/// <summary>
/// Cross-tenant a propósito: el webhook no sabe de qué tenant es el número hasta resolver la ruta. Lo
/// usa sólo el adaptador de Messaging en Bootstrapper. <c>null</c> = número desconocido o conexión borrada.
/// </summary>
public interface IConnectionRoutes
{
    Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken cancellationToken);
}
```

`IIntegrationConnections.cs`:

```csharp
/// <summary>Nombre y estado de una conexión del proveedor, Active o no (spec 2026-10-09 §6.1): la
/// bandeja muestra <c>connectionName</c> también en conversaciones de una conexión pausada.</summary>
public sealed record ConnectionListing(Guid Id, string Name, ConnectionStatus Status);
```

en la interfaz:

```csharp
    /// <summary>Todas las del proveedor en el tenant, por nombre; no filtra por estado ni por visibilidad.</summary>
    Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);
```

y en `IntegrationConnections`:

```csharp
    public async Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(
        Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        (await repository.ListAsync(tenantId, cancellationToken))
            .Where(connection => string.Equals(connection.ProviderKey, providerKey, StringComparison.Ordinal))
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .Select(connection => new ConnectionListing(connection.Id, connection.Name, connection.Status))
            .ToArray();
```

- [ ] **Step 3: Persistencia**

`IntegrationsDbContext.cs`:

```csharp
    /// <summary>Spec 2026-10-09 §6.1: el único (provider_key, external_id). Traducido por nombre a
    /// <c>number_already_connected</c>.</summary>
    public const string RouteExternalIndex = "IX_connection_routes_provider_external";

    public DbSet<IntegrationConnectionRoute> Routes => Set<IntegrationConnectionRoute>();
```

en `OnModelCreating` agrega `ConfigureConnectionRoute(modelBuilder);` y:

```csharp
    private static void ConfigureConnectionRoute(ModelBuilder modelBuilder)
    {
        var route = modelBuilder.Entity<IntegrationConnectionRoute>();
        route.ToTable("connection_routes", Schema);
        route.HasKey(value => value.ConnectionId);
        route.Property(value => value.ConnectionId).HasColumnName("connection_id").ValueGeneratedNever();
        route.Property(value => value.ProviderKey).HasColumnName("provider_key").HasMaxLength(IntegrationProvider.KeyMaxLength);
        route.Property(value => value.ExternalId).HasColumnName("external_id").HasMaxLength(IntegrationConnectionRoute.ExternalIdMaxLength);
        route.Property(value => value.AccountId).HasColumnName("account_id").HasMaxLength(IntegrationConnectionRoute.ExternalIdMaxLength);
        route.Property(value => value.TenantId).HasColumnName("tenant_id");
        route.HasIndex(value => new { value.ProviderKey, value.ExternalId }).IsUnique().HasDatabaseName(RouteExternalIndex);
        // account_update llega por WABA (D-M2); una WABA tiene varios números, así que no es único.
        route.HasIndex(value => new { value.ProviderKey, value.AccountId })
            .HasDatabaseName("IX_connection_routes_provider_account")
            .HasFilter("account_id IS NOT NULL");
        // La cascada borra la ruta con la conexión y libera el número (§6.1).
        route.HasOne<IntegrationConnection>().WithMany().HasForeignKey(value => value.ConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
```

`ConnectionRouteRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

internal sealed class ConnectionRouteRepository(IntegrationsDbContext dbContext) : IConnectionRouteRepository
{
    public void Add(IntegrationConnectionRoute route) => dbContext.Routes.Add(route);

    public Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        dbContext.Routes.AnyAsync(route => route.ProviderKey == providerKey && route.ExternalId == externalId, cancellationToken);
}
```

`ConnectionRoutes.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Join con <c>connections</c> para traer el estado: el worker decide por él (§8.2).</summary>
internal sealed class ConnectionRoutes(IntegrationsDbContext dbContext) : IConnectionRoutes
{
    public Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        Query(providerKey)
            .Where(pair => pair.Route.ExternalId == externalId)
            .Select(pair => new ConnectionRoute(pair.Route.TenantId, pair.Route.ConnectionId, pair.Connection.Status))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken cancellationToken) =>
        await Query(providerKey)
            .Where(pair => pair.Route.AccountId == accountId)
            .Select(pair => new ConnectionRoute(pair.Route.TenantId, pair.Route.ConnectionId, pair.Connection.Status))
            .ToListAsync(cancellationToken);

    private IQueryable<(Domain.IntegrationConnectionRoute Route, Domain.IntegrationConnection Connection)> Query(string providerKey) =>
        dbContext.Routes.AsNoTracking()
            .Where(route => route.ProviderKey == providerKey)
            .Join(dbContext.Connections.AsNoTracking(), route => route.ConnectionId, connection => connection.Id,
                (route, connection) => new ValueTuple<Domain.IntegrationConnectionRoute, Domain.IntegrationConnection>(route, connection));
}
```

(Si EF rechaza la tupla en la proyección, usa un record privado `RouteWithConnection(IntegrationConnectionRoute Route, IntegrationConnection Connection)`.)

`IntegrationsUnitOfWork.cs`: agrega un `catch` antes del de `ConnectionNameIndex`:

```csharp
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, IntegrationsDbContext.RouteExternalIndex))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected,
                "That WhatsApp number is already connected to another connection.",
                exception);
        }
```

`IntegrationsInfrastructureExtensions.cs`, junto a los repositorios:

```csharp
        services.AddScoped<IConnectionRouteRepository, ConnectionRouteRepository>();
        services.AddScoped<IConnectionRoutes, ConnectionRoutes>();
```

Migración:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet ef migrations add AddConnectionRoutes --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext -o Persistence/Migrations
```

Verifica en el `Up`: `CreateTable("connection_routes", schema "integrations")` con PK `PK_connection_routes` sobre `connection_id`, FK `ON DELETE CASCADE`, índice único `IX_connection_routes_provider_external` y parcial `IX_connection_routes_provider_account` con `filter: "account_id IS NOT NULL"`.

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --no-build --filter "FullyQualifiedName~ConnectionRoutesApiTests|FullyQualifiedName~ConsumerPortsApiTests|FullyQualifiedName~IntegrationsPersistenceTests"
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~ConsumerPortsTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todas correctas.

- [ ] **Step 5: Formato y commit**

```text
feat(integrations): rutas por número con único cross-tenant y puerto IConnectionRoutes
```

---

### Task 6: Cliente de Graph de Integrations y probador `whatsapp-cloud`

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaGraphError.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaGraphClient.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/WhatsAppCloudConnectionTester.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionTesting.cs:16-26`
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionWriteSupport.cs:219-226` (`FailureCode`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/TestConnection.cs:36-48`
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationConnection.cs` (`ApplyProviderFields` después de `Update`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationsErrorCodes.cs:24-29`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs:56-60`
- Create: `tests/Modules/Integrations/Modules.Integrations.UnitTests/WhatsAppCloudConnectionTesterTests.cs`
- Test: `IntegrationConnectionTests.cs` (`ApplyProviderFields`), `LifecycleHandlersTests.cs` (refresco al probar)

**Interfaces:**
- Consumes: `IProviderConnectionTester`, `ConnectionTesterRegistry`, `MetaAppOptions`.
- Produces: `ConnectionTestResult.FailureCode`, `ConnectionTestResult.RefreshedFields`, `ConnectionFailureCodes.TokenExpired = "token_expired"`, `NumberUnregistered = "number_unregistered"`, `AccountDisabled = "account_disabled"`, `IntegrationConnection.ApplyProviderFields`, `MetaGraphClient` (`HttpClientName = "integrations.meta-graph"`, `SendAsync(HttpRequestMessage) → MetaGraphResponse`), `MetaGraphError`.

- [ ] **Step 1: Pruebas RED**

`IntegrationConnectionTests.cs`:

```csharp
    // Spec 2026-10-09 §6.1: el probador refresca número, nombre y calidad; nunca un secreto ni un
    // campo de un proveedor por formulario.
    [Fact]
    public void ApplyProviderFieldsTouchesOnlyBackendOwnedFields()
    {
        var connection = IntegrationConnection.Create(
            IntegrationProviders.WhatsAppCloud, Guid.CreateVersion7(), "Ventas",
            new Dictionary<string, string> { ["phoneNumberId"] = "111", ["wabaId"] = "222", ["displayPhoneNumber"] = "+57 1" },
            new Dictionary<string, string> { ["accessToken"] = "t" },
            (_, key, plain) => new ProtectedSecret("test", System.Text.Encoding.UTF8.GetBytes(key + plain)),
            Guid.CreateVersion7(), Now);

        var changed = connection.ApplyProviderFields(
            IntegrationProviders.WhatsAppCloud,
            new Dictionary<string, string> { ["displayPhoneNumber"] = "+57 300 123 4567", ["qualityRating"] = "GREEN", ["accessToken"] = "x" });

        Assert.Equal(["displayPhoneNumber", "qualityRating"], changed);
        Assert.Equal("+57 300 123 4567", connection.Fields["displayPhoneNumber"]);
        Assert.Equal("GREEN", connection.Fields["qualityRating"]);
        Assert.Equal("111", connection.Fields["phoneNumberId"]);
        Assert.Equal(1, connection.Version);

        var zenvia = CreateWith(); // el helper del archivo que arma una conexión de Zenvia
        Assert.Empty(zenvia.ApplyProviderFields(IntegrationProviders.Zenvia, new Dictionary<string, string> { ["fromNumber"] = "573001111111" }));
    }
```

`tests/Modules/Integrations/Modules.Integrations.UnitTests/WhatsAppCloudConnectionTesterTests.cs` (misma forma que `ZenviaConnectionTesterTests`: se arma por `AddIntegrationsInfrastructure` y sólo el handler primario de `MetaGraphClient.HttpClientName` es de mentira):

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §6.1, «Probador whatsapp-cloud»: 200 con cualquier status → Ok y refresca
/// (D-M14); 190 → token_expired; 133010 → number_unregistered; otro 4xx → credentials_rejected; 5xx,
/// timeout y red → Unreachable. Nunca registra el token.</summary>
public sealed class WhatsAppCloudConnectionTesterTests : IDisposable
{
    private const string Token = "meta-access-token-SENTINEL-77aa";

    private readonly StubGraphHandler _graph = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public WhatsAppCloudConnectionTesterTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Meta:App:GraphApiVersion"] = "v24.0",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(MetaGraphClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _graph);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync() =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.WhatsAppCloud,
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.PhoneNumberId] = "111" },
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.AccessToken] = Token },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A200RefreshesTheFieldsWhateverTheStatus()
    {
        _graph.Body = """{"display_phone_number":"+57 300 123 4567","verified_name":"Origen","quality_rating":"GREEN","status":"PENDING","id":"111"}""";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal("+57 300 123 4567", result.RefreshedFields![WhatsAppCloudFieldKeys.DisplayPhoneNumber]);
        Assert.Equal("Origen", result.RefreshedFields[WhatsAppCloudFieldKeys.VerifiedName]);
        Assert.Equal("GREEN", result.RefreshedFields[WhatsAppCloudFieldKeys.QualityRating]);
        var request = Assert.Single(_graph.Requests);
        Assert.Equal("https://graph.facebook.com/v24.0/111?fields=display_phone_number,verified_name,quality_rating,status", request.Uri?.ToString());
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.AllText, Token);
    }

    [Theory]
    [InlineData(401, 190, ConnectionTestOutcome.CredentialsRejected, "token_expired")]
    [InlineData(400, 133010, ConnectionTestOutcome.CredentialsRejected, "number_unregistered")]
    [InlineData(400, 100, ConnectionTestOutcome.CredentialsRejected, "credentials_rejected")]
    [InlineData(403, 10, ConnectionTestOutcome.CredentialsRejected, "credentials_rejected")]
    [InlineData(500, 1, ConnectionTestOutcome.Unreachable, null)]
    [InlineData(429, 4, ConnectionTestOutcome.Unreachable, null)]
    [InlineData(302, null, ConnectionTestOutcome.Unreachable, null)]
    public async Task EveryGraphAnswerHasItsOutcome(int status, int? code, ConnectionTestOutcome expected, string? failureCode)
    {
        _graph.Status = (HttpStatusCode)status;
        _graph.Body = code is null ? "" : $$"""{"error":{"message":"x","type":"OAuthException","code":{{code}},"error_subcode":463,"fbtrace_id":"abc"}}""";

        var result = await TestAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(failureCode, result.FailureCode);
    }

    [Fact]
    public async Task ATimeoutAndANetworkFailureAreUnreachable()
    {
        _graph.Throw = new TaskCanceledException("timed out", new TimeoutException());
        Assert.Equal("timeout", (await TestAsync()).Reason);
        _graph.Throw = new HttpRequestException("refused");
        Assert.Equal("network", (await TestAsync()).Reason);
    }

    [Fact]
    public void TheClientWaitsTenSecondsAndDoesNotFollowRedirects()
    {
        var client = _services.GetRequiredService<IHttpClientFactory>().CreateClient(MetaGraphClient.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.False(MetaGraphClient.CreatePrimaryHandler().AllowAutoRedirect);
    }
}

internal sealed record GraphRequest(HttpMethod Method, Uri? Uri, string? Authorization, string UserAgent, string? Body);

/// <summary>Graph de mentira para pruebas unitarias: anota cada request y responde lo que la prueba pida.</summary>
internal sealed class StubGraphHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "{}";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<GraphRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new GraphRequest(
            request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(), body));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~WhatsAppCloudConnectionTesterTests|FullyQualifiedName~IntegrationConnectionTests"
```

Esperado: `error CS0246 … 'MetaGraphClient'`, `'ApplyProviderFields'`.

- [ ] **Step 2: Application y dominio**

`ConnectionTesting.cs`:

```csharp
/// <summary>Spec 2026-10-08, «Probar la credencial» y 2026-10-09 §6.1: <c>Ok</c> (con campos refrescados
/// opcionales), <c>CredentialsRejected</c> (con un <c>FailureCode</c> opcional; sin él, el mapeo de
/// hoy), <c>Unreachable(reason)</c> o <c>Invalid(fieldKey, reason)</c>. Nunca lleva un valor secreto.</summary>
public sealed record ConnectionTestResult(
    ConnectionTestOutcome Outcome,
    string? FieldKey = null,
    string? Reason = null,
    string? FailureCode = null,
    IReadOnlyDictionary<string, string>? RefreshedFields = null)
{
    public static ConnectionTestResult Ok { get; } = new(ConnectionTestOutcome.Ok);

    public static ConnectionTestResult OkWith(IReadOnlyDictionary<string, string> refreshedFields) =>
        new(ConnectionTestOutcome.Ok, RefreshedFields: refreshedFields);

    public static ConnectionTestResult CredentialsRejected { get; } = new(ConnectionTestOutcome.CredentialsRejected);

    public static ConnectionTestResult RejectedWith(string failureCode) =>
        new(ConnectionTestOutcome.CredentialsRejected, FailureCode: failureCode);

    public static ConnectionTestResult Unreachable(string reason) => new(ConnectionTestOutcome.Unreachable, Reason: reason);

    public static ConnectionTestResult Invalid(string fieldKey, string reason) =>
        new(ConnectionTestOutcome.Invalid, fieldKey, reason);
}
```

`ConnectionWriteSupport.cs`, `ConnectionVerification.FailureCode`: el primer caso pasa a `ConnectionTestOutcome.CredentialsRejected => result.FailureCode ?? ConnectionFailureCodes.CredentialsRejected`.

`IntegrationsErrorCodes.cs`, en `ConnectionFailureCodes`:

```csharp
    // Spec 2026-10-09 §6.1 y §10.2.
    public const string TokenExpired = "token_expired";
    public const string NumberUnregistered = "number_unregistered";
    public const string AccountDisabled = "account_disabled";
```

`IntegrationConnection.cs`, después de `Update`:

```csharp
    /// <summary>
    /// Spec 2026-10-09 §6.1: lo que el probador refresca (número, nombre verificado, calidad). Sólo toca
    /// campos <b>no secretos</b> que el backend es dueño de: los internos, o todos los de un proveedor con
    /// Embedded Signup. Lo demás se ignora sin lanzar. No sube la versión: quien llama ya la sube con la
    /// verificación.
    /// </summary>
    /// <returns>Las claves que cambiaron, ordenadas.</returns>
    public IReadOnlyList<string> ApplyProviderFields(IntegrationProvider provider, IReadOnlyDictionary<string, string> values)
    {
        if (!string.Equals(provider.Key, ProviderKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The provider does not match the connection.", nameof(provider));
        }

        var next = new Dictionary<string, string>(Fields, StringComparer.Ordinal);
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (key, raw) in values)
        {
            if (provider.FindField(key) is not { IsSecret: false } definition
                || !(definition.Internal || provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup))
            {
                continue;
            }

            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0 || definition.IsTooLong(value) || !definition.HasValidShape(value))
            {
                continue;
            }

            if (!next.TryGetValue(key, out var current) || !string.Equals(current, value, StringComparison.Ordinal))
            {
                next[key] = value;
                changed.Add(key);
            }
        }

        if (changed.Count > 0)
        {
            Fields = next;
        }

        return changed.ToArray();
    }
```

`TestConnection.cs:36-40`: después de `connection.MarkVerified(now);`:

```csharp
            if (result.RefreshedFields is { Count: > 0 } refreshed)
            {
                connection.ApplyProviderFields(provider, refreshed);
            }
```

`ResumeConnection.cs`: mira si aplica el resultado `Ok` como `TestConnection`; si sí, agrega el mismo bloque.

- [ ] **Step 3: Cliente de Graph y probador**

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaGraphError.cs`:

```csharp
using System.Text.Json;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Lo que se puede registrar de un error de Graph (spec §3, «Errores de Graph»): código,
/// subcódigo y <c>fbtrace_id</c>. Nunca <c>message</c>: puede repetir el token.</summary>
internal sealed record MetaGraphError(int? Code, int? Subcode, string? FbTraceId)
{
    public static MetaGraphError? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new MetaGraphError(
                error.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsedCode) ? parsedCode : null,
                error.TryGetProperty("error_subcode", out var subcode) && subcode.TryGetInt32(out var parsedSubcode) ? parsedSubcode : null,
                error.TryGetProperty("fbtrace_id", out var trace) && trace.ValueKind == JsonValueKind.String ? trace.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

`MetaGraphClient.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Una respuesta de Graph ya leída: status, cuerpo (sólo para parsear, nunca para el log) y el
/// error si lo hay. <c>Unreachable</c> = timeout, red o 5xx.</summary>
internal sealed record MetaGraphResponse(HttpStatusCode Status, string Body, MetaGraphError? Error, string? UnreachableReason)
{
    public bool IsSuccess => UnreachableReason is null && (int)Status is >= 200 and < 300;
}

/// <summary>
/// Spec 2026-10-09, decisión 3: el cliente de Graph de Integrations (<c>integrations.meta-graph</c>), con el
/// patrón de <c>ZenviaConnectionTester</c>: sin redirecciones (el token viaja en <c>Authorization</c>),
/// 10 s, sin loggers de headers. Registra status, <c>error.code</c>, <c>error_subcode</c> y <c>fbtrace_id</c>;
/// nunca el token, el <c>code</c>, el PIN ni el <c>client_secret</c>.
/// </summary>
internal sealed partial class MetaGraphClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MetaAppOptions> options,
    ILogger<MetaGraphClient> logger)
{
    public const string HttpClientName = "integrations.meta-graph";

    public const string BaseUrl = "https://graph.facebook.com/";

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph {Operation} answered HTTP {StatusCode} (code {Code}, subcode {Subcode}, fbtrace {FbTraceId})")]
    private static partial void LogAnswered(ILogger logger, string operation, int statusCode, int? code, int? subcode, string? fbTraceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graph {Operation} could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion)
        ? MetaAppOptions.DefaultGraphApiVersion
        : options.Value.GraphApiVersion.Trim();

    /// <summary>Ruta relativa a la versión: <c>Path("111?fields=…")</c> → <c>v24.0/111?fields=…</c>.</summary>
    public string Path(string relative) => $"{Version}/{relative.TrimStart('/')}";

    public async Task<MetaGraphResponse> SendAsync(string operation, HttpRequestMessage request, string? accessToken, CancellationToken cancellationToken)
    {
        if (accessToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = response.IsSuccessStatusCode ? null : MetaGraphError.TryParse(body);
            LogAnswered(logger, operation, (int)response.StatusCode, error?.Code, error?.Subcode, error?.FbTraceId);
            return (int)response.StatusCode >= 500
                ? new MetaGraphResponse(response.StatusCode, body, error, $"http_{(int)response.StatusCode}")
                : new MetaGraphResponse(response.StatusCode, body, error, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, operation, "timeout");
            return new MetaGraphResponse(HttpStatusCode.RequestTimeout, string.Empty, null, "timeout");
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, operation, "network");
            return new MetaGraphResponse(HttpStatusCode.ServiceUnavailable, string.Empty, null, "network");
        }
    }
}
```

`WhatsAppCloudConnectionTester.cs`:

```csharp
using System.Text.Json;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Spec 2026-10-09 §6.1, «Probador whatsapp-cloud». Un 200 vale con cualquier <c>status</c>
/// (D-M14): el <c>status</c> se registra, no decide.</summary>
internal sealed class WhatsAppCloudConnectionTester(MetaGraphClient graph) : IProviderConnectionTester
{
    public const string Fields = "display_phone_number,verified_name,quality_rating,status";

    public string ProviderKey => IntegrationProviders.WhatsAppCloud.Key;

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var phoneNumberId = fields.GetValueOrDefault(WhatsAppCloudFieldKeys.PhoneNumberId) ?? string.Empty;
        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path($"{phoneNumberId}?fields={Fields}"));
        var response = await graph.SendAsync("phone-number", request, secrets[WhatsAppCloudFieldKeys.AccessToken], cancellationToken);
        return Classify(response);
    }

    internal static ConnectionTestResult Classify(MetaGraphResponse response)
    {
        if (response.UnreachableReason is { } reason)
        {
            return ConnectionTestResult.Unreachable(reason);
        }

        if (response.IsSuccess)
        {
            return ConnectionTestResult.OkWith(ReadFields(response.Body));
        }

        var status = (int)response.Status;
        if (status is 400 or 401 or 403)
        {
            return response.Error?.Code switch
            {
                190 => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.TokenExpired),
                133010 => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.NumberUnregistered),
                _ => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected),
            };
        }

        return ConnectionTestResult.Unreachable($"http_{status}");
    }

    /// <summary>Lo que Meta devuelve sobre el número, con las claves del catálogo. Lo que falte se omite.</summary>
    internal static Dictionary<string, string> ReadFields(string body)
    {
        var refreshed = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Copy(root, "display_phone_number", WhatsAppCloudFieldKeys.DisplayPhoneNumber, refreshed);
            Copy(root, "verified_name", WhatsAppCloudFieldKeys.VerifiedName, refreshed);
            Copy(root, "quality_rating", WhatsAppCloudFieldKeys.QualityRating, refreshed);
        }
        catch (JsonException)
        {
            // Un 200 con un cuerpo que no es JSON sigue siendo Ok: no hay nada que refrescar.
        }

        return refreshed;
    }

    private static void Copy(JsonElement root, string from, string to, Dictionary<string, string> into)
    {
        if (root.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            into[to] = text;
        }
    }
}
```

`IntegrationsInfrastructureExtensions.cs`, después del registro de Zenvia y antes de `ConnectionTesterRegistry`:

```csharp
        // Spec 2026-10-09, decisión 3: el cliente de Graph del módulo, mismo patrón que Zenvia.
        services.AddHttpClient(MetaGraphClient.HttpClientName, MetaGraphClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => MetaGraphClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<MetaGraphClient>();
        services.AddSingleton<IProviderConnectionTester, WhatsAppCloudConnectionTester>();
```

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~WhatsAppCloudConnectionTesterTests|FullyQualifiedName~IntegrationConnectionTests|FullyQualifiedName~LifecycleHandlersTests|FullyQualifiedName~ZenviaConnectionTesterTests"
```

Esperado: todas correctas. Agrega en `LifecycleHandlersTests` una prueba que fije `bed.Tester.Result = ConnectionTestResult.OkWith(new Dictionary<string,string>{["qualityRating"]="RED"})` sobre una conexión `whatsapp-cloud` sembrada en el bed y verifique que `TestHandler` deja `fields["qualityRating"] == "RED"` en la respuesta; y otra que con `RejectedWith("token_expired")` sobre una `Active` deja `lastFailureCode == "token_expired"` y `status == NeedsAttention`.

El harness de integración ya enchufa `ZenviaHandler` al cliente de Zenvia; agrega el equivalente para Meta en `QepApiFactory.ConfigureWebHost`:

```csharp
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(ZenviaConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ZenviaHandler);
                services.AddHttpClient(MetaGraphClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => MetaHandler);
            });
```

con `public FakeMetaGraphHandler MetaHandler { get; } = new();` — la clase se escribe en la Task 7 (Step 1); por ahora usa `StubGraphHandler`… no: mueve `StubGraphHandler` y `GraphRequest` a un archivo compartido **del proyecto de pruebas de integración** no es posible (son proyectos distintos). Deja `StubGraphHandler` en UnitTests y crea en IntegrationTests `FakeMetaGraphHandler` (Task 7). Hasta entonces no registres el handler de Meta en el harness.

- [ ] **Step 5: Formato y commit**

```text
feat(integrations): cliente de Graph y probador de whatsapp-cloud con refresco de campos
```

---

### Task 7: Embedded Signup

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IWhatsAppSignupGateway.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Application/CompleteWhatsAppSignup.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaGraphSignupGateway.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Api/IntegrationsEndpoints.cs` (endpoint y request)
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs` (gateway)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionWriteSupport.cs` (`ConnectionAudit.ByMemberWithMetadata` — ver Step 3)
- Create: `tests/Modules/Integrations/Modules.Integrations.UnitTests/CompleteWhatsAppSignupHandlerTests.cs`
- Create: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/EmbeddedSignupApiTests.cs`
- Create: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/FakeMetaGraphHandler.cs`
- Modify: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs` (`MetaHandler`), `ConnectionSecretLeakTests.cs` (caso nuevo)

**Interfaces:**
- Consumes: `IMetaAppSettings`, `IConnectionRouteRepository`, `MetaGraphClient`, `IntegrationConnection.Create`, `IIntegrationsAuditRecorder`.
- Produces: `IWhatsAppSignupGateway`, `GraphResult<T>`, `GraphFailure`, `WabaPhoneNumber`, `CompleteWhatsAppSignupCommand`, `CompleteWhatsAppSignupValidator`, `CompleteWhatsAppSignupHandler`, `EmbeddedSignupRequest`, ruta `POST …/integrations/whatsapp/embedded-signup`.

- [ ] **Step 1: Pruebas RED del handler (dobles)**

`tests/Modules/Integrations/Modules.Integrations.UnitTests/CompleteWhatsAppSignupHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §8.1: el orden de los chequeos, el camino FINISH, la coexistencia con uno y
/// con varios números, y cada error de Graph con su código. El PIN es aleatorio y no se guarda.</summary>
public sealed class CompleteWhatsAppSignupHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CompleteWhatsAppSignupCommand Finish(Guid tenantId, string name = "Ventas", string? phoneNumberId = "111") =>
        new(tenantId, name, "new_number", "FINISH", "AQB-code", "222", phoneNumberId, "333");

    private static CompleteWhatsAppSignupCommand Coexistence(Guid tenantId) =>
        new(tenantId, "Ventas", "existing_business_app", "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING", "AQB-code", "222", null, null);

    [Fact]
    public async Task FinishExchangesRegistersSubscribesReadsAndCreatesTheConnectionWithItsRoute()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", "+57 300 123 4567", "Origen", "GREEN");

        var response = await bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct);

        Assert.Equal("whatsapp-cloud", response.ProviderKey);
        Assert.Equal("Active", response.Status);
        Assert.Equal("+57 300 123 4567", response.Fields["displayPhoneNumber"]);
        Assert.Equal("111", response.Fields["phoneNumberId"]);
        Assert.Equal("222", response.Fields["wabaId"]);
        Assert.True(response.Secrets["accessToken"].Configured);
        Assert.Equal(["exchange", "register:111", "subscribe:222", "read:111"], bed.Gateway.Calls);
        Assert.Matches("^[0-9]{6}$", bed.Gateway.LastPin!);
        var route = Assert.Single(bed.Routes.Added);
        Assert.Equal(("whatsapp-cloud", "111", "222", bed.TenantId, response.Id), (route.ProviderKey, route.ExternalId, route.AccountId, route.TenantId, route.ConnectionId));
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(ConnectionAuditActions.Created, audit.Action);
        Assert.Contains("path:new_number", audit.ChangedFields);
        Assert.Contains("event:FINISH", audit.ChangedFields);
        Assert.DoesNotContain(audit.ChangedFields, field => field.Contains("AQB-code", StringComparison.Ordinal));
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task CoexistenceSkipsRegistrationAndPicksTheOnlyNumber()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.WabaNumbers["222"] = [new WabaPhoneNumber("111", "+57 1", "Origen", "GREEN")];
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", "+57 1", "Origen", "GREEN");

        var response = await bed.SignupHandler().HandleAsync(Coexistence(bed.TenantId), Ct);

        Assert.Equal("111", response.Fields["phoneNumberId"]);
        Assert.Equal(["exchange", "numbers:222", "subscribe:222", "read:111"], bed.Gateway.Calls);
    }

    // D-M15: con varios sin ruta no se elige ninguno; el que ya tiene ruta se descarta.
    [Fact]
    public async Task CoexistenceWithSeveralFreeNumbersIsRegistrationFailed()
    {
        var bed = new IntegrationsTestBed();
        bed.Routes.Existing.Add(("whatsapp-cloud", "999"));
        bed.Gateway.WabaNumbers["222"] = [new WabaPhoneNumber("111", null, null, null), new WabaPhoneNumber("112", null, null, null), new WabaPhoneNumber("999", null, null, null)];

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Coexistence(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppRegistrationFailed, error.Code);
        Assert.Contains("varios números", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Theory]
    [InlineData("exchange", IntegrationsErrorCodes.WhatsAppCodeExchangeFailed)]
    [InlineData("register", IntegrationsErrorCodes.WhatsAppRegistrationFailed)]
    [InlineData("subscribe", IntegrationsErrorCodes.WhatsAppRegistrationFailed)]
    [InlineData("read", IntegrationsErrorCodes.WhatsAppRegistrationFailed)]
    public async Task EveryGraphFailureHasItsCodeAndSavesNothing(string failingStep, string expectedCode)
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", null, null, null);
        bed.Gateway.FailAt = failingStep;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Routes.Added);
    }

    // Antes del canje sólo hay validaciones de milisegundos: tope, nombre y ruta.
    [Fact]
    public async Task AnExistingRouteFailsBeforeTalkingToMeta()
    {
        var bed = new IntegrationsTestBed();
        bed.Routes.Existing.Add(("whatsapp-cloud", "111"));

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task WithoutMetaAppTheExchangeFailsBeforeTalkingToMeta()
    {
        var bed = new IntegrationsTestBed();
        bed.MetaApp.Configured = false;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppCodeExchangeFailed, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Theory]
    [InlineData("event", "FINISH_OTHER", "event")]
    [InlineData("path", "something", "path")]
    [InlineData("phoneNumberId", null, "phoneNumberId")]
    [InlineData("phoneNumberId", "abc", "phoneNumberId")]
    [InlineData("wabaId", "", "wabaId")]
    [InlineData("code", "", "code")]
    [InlineData("name", "", "name")]
    public async Task TheValidatorNamesTheField(string property, string? value, string expectedKey)
    {
        var bed = new IntegrationsTestBed();
        var command = Finish(bed.TenantId);
        command = property switch
        {
            "event" => command with { Event = value },
            "path" => command with { Path = value },
            "phoneNumberId" => command with { PhoneNumberId = value },
            "wabaId" => command with { WabaId = value },
            "code" => command with { Code = value },
            _ => command with { Name = value },
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.SignupHandler().HandleAsync(command, Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == expectedKey);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task AnotherTenantOrAMissingPermissionIsForbiddenBeforeAnything()
    {
        var bed = new IntegrationsTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SignupHandler(new FakeExecutionContext(Guid.CreateVersion7(), bed.SubjectId, IntegrationsPermissions.ConnectionManage))
                .HandleAsync(Finish(bed.TenantId), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SignupHandler(new FakeExecutionContext(bed.TenantId, bed.SubjectId, IntegrationsPermissions.ConnectionRead))
                .HandleAsync(Finish(bed.TenantId), Ct));
        Assert.Empty(bed.Gateway.Calls);
    }
}
```

Dobles nuevos en `IntegrationsTestDoubles.cs`:

```csharp
/// <summary>Graph de mentira para el signup: responde por paso y anota el orden. <see cref="FailAt"/> hace
/// fallar un paso con un error 400 código 100.</summary>
internal sealed class FakeWhatsAppSignupGateway : IWhatsAppSignupGateway
{
    public List<string> Calls { get; } = [];

    public string? FailAt { get; set; }

    public string? LastPin { get; private set; }

    public Dictionary<string, WabaPhoneNumber> PhoneNumbers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<WabaPhoneNumber>> WabaNumbers { get; } = new(StringComparer.Ordinal);

    private static GraphFailure Failure => new(400, 100, null, "http_400");

    public Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        Calls.Add("exchange");
        return Task.FromResult(FailAt == "exchange" ? new GraphResult<string>(null, Failure) : new GraphResult<string>("meta-access-token-SENTINEL-unit", null));
    }

    public Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken cancellationToken)
    {
        Calls.Add($"register:{phoneNumberId}");
        LastPin = pin;
        return Task.FromResult(FailAt == "register" ? new GraphResult<bool>(false, Failure) : new GraphResult<bool>(true, null));
    }

    public Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"numbers:{wabaId}");
        return Task.FromResult(FailAt == "numbers"
            ? new GraphResult<IReadOnlyList<WabaPhoneNumber>>(null, Failure)
            : new GraphResult<IReadOnlyList<WabaPhoneNumber>>(WabaNumbers.GetValueOrDefault(wabaId) ?? [], null));
    }

    public Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"subscribe:{wabaId}");
        return Task.FromResult(FailAt == "subscribe" ? new GraphResult<bool>(false, Failure) : new GraphResult<bool>(true, null));
    }

    public Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"read:{phoneNumberId}");
        return Task.FromResult(FailAt == "read" || !PhoneNumbers.TryGetValue(phoneNumberId, out var number)
            ? new GraphResult<WabaPhoneNumber>(null, Failure)
            : new GraphResult<WabaPhoneNumber>(number, null));
    }
}

internal sealed class InMemoryRouteRepository : IConnectionRouteRepository
{
    public List<IntegrationConnectionRoute> Added { get; } = [];

    public HashSet<(string ProviderKey, string ExternalId)> Existing { get; } = [];

    public void Add(IntegrationConnectionRoute route) => Added.Add(route);

    public Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(Existing.Contains((providerKey, externalId)) || Added.Any(route => route.ProviderKey == providerKey && route.ExternalId == externalId));
}
```

Y en el bed (`IntegrationsTestBed.Writes.cs`):

```csharp
    public FakeWhatsAppSignupGateway Gateway { get; } = new();

    public InMemoryRouteRepository Routes { get; } = new();

    public CompleteWhatsAppSignupHandler SignupHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Routes, UnitOfWork, Audit, Protector, Gateway, MetaApp, Modules, Memberships, AuthorNames,
            context ?? Context(), Clock, new CompleteWhatsAppSignupValidator());
```

(Confirma que `Catalog` del bed incluye `WhatsAppCloud`: si el bed usa `FakeIntegrationProviderCatalog`, agrégalo; si usa el real, ya está. Confirma también que `Context()` concede `ConnectionManage` y que `Memberships.Active` tiene la membresía del subject.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~CompleteWhatsAppSignupHandlerTests"
```

Esperado: `error CS0246 … 'IWhatsAppSignupGateway'`.

- [ ] **Step 2: Puerto y handler**

`src/Modules/Integrations/Modules.Integrations.Application/IWhatsAppSignupGateway.cs`:

```csharp
namespace Modules.Integrations.Application;

/// <summary>Un error de Graph ya resumido (spec §3): lo que se puede registrar. Nunca el mensaje.</summary>
public sealed record GraphFailure(int HttpStatus, int? Code, int? Subcode, string Reason);

/// <summary>Resultado de una llamada a Graph que <b>no lanza</b> por un error del proveedor: el handler
/// decide el código de dominio (P3 del plan).</summary>
public sealed record GraphResult<T>(T? Value, GraphFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record WabaPhoneNumber(string Id, string? DisplayPhoneNumber, string? VerifiedName, string? QualityRating);

/// <summary>
/// Spec 2026-10-09 §8.1, pasos 4 a 8, contra Graph. El adaptador vive en Infrastructure sobre
/// <c>integrations.meta-graph</c>. Nunca registra el <c>code</c>, el token ni el PIN.
/// </summary>
public interface IWhatsAppSignupGateway
{
    /// <summary>GET /{v}/oauth/access_token?client_id&amp;client_secret&amp;code → token de sistema del negocio.</summary>
    Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>POST /{phoneNumberId}/register con { messaging_product, pin }.</summary>
    Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken cancellationToken);

    /// <summary>GET /{wabaId}/phone_numbers.</summary>
    Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken cancellationToken);

    /// <summary>POST /{wabaId}/subscribed_apps.</summary>
    Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken cancellationToken);

    /// <summary>GET /{phoneNumberId}?fields=display_phone_number,verified_name,quality_rating,status.</summary>
    Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken);
}
```

`src/Modules/Integrations/Modules.Integrations.Application/CompleteWhatsAppSignup.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>El cuerpo de <c>POST /integrations/whatsapp/embedded-signup</c> (spec 2026-10-09 §5.2).
/// <see cref="ToString"/> no imprime el <c>code</c>.</summary>
public sealed record CompleteWhatsAppSignupCommand(
    Guid TenantId,
    string? Name,
    string? Path,
    string? Event,
    string? Code,
    string? WabaId,
    string? PhoneNumberId,
    string? BusinessId) : ICommand<ConnectionResponse>
{
    public const string EventFinish = "FINISH";
    public const string EventCoexistence = "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING";
    public const string PathExisting = "existing_business_app";
    public const string PathNewNumber = "new_number";

    public bool IsCoexistence => string.Equals(Event, EventCoexistence, StringComparison.Ordinal);

    public override string ToString() =>
        $"CompleteWhatsAppSignupCommand {{ TenantId = {TenantId}, Name = {Name}, Path = {Path}, Event = {Event}, "
        + $"WabaId = {WabaId}, PhoneNumberId = {PhoneNumberId}, BusinessId = {BusinessId} }}";
}

/// <summary>Spec §8.1, paso 2. Los dígitos se exigen con regex, nunca con el patrón del catálogo: el
/// código de error viaja por campo.</summary>
public sealed partial class CompleteWhatsAppSignupValidator : AbstractValidator<CompleteWhatsAppSignupCommand>
{
    public const string DigitsMessage = "Meta devolvió un identificador que no es numérico; vuelve a abrir el flujo.";

    [GeneratedRegex(@"^[0-9]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    public CompleteWhatsAppSignupValidator()
    {
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
        RuleFor(command => command.Path)
            .Must(path => path is CompleteWhatsAppSignupCommand.PathExisting or CompleteWhatsAppSignupCommand.PathNewNumber)
            .WithMessage("Elige cómo conectar el número.");
        RuleFor(command => command.Event)
            .Must(value => value is CompleteWhatsAppSignupCommand.EventFinish or CompleteWhatsAppSignupCommand.EventCoexistence)
            .WithMessage("Meta no terminó el flujo; vuelve a intentarlo.");
        RuleFor(command => command.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Length <= 1024 && !code.Any(char.IsControl))
            .WithMessage("Meta no devolvió un código válido; vuelve a abrir el flujo.");
        RuleFor(command => command.WabaId).Must(value => value is not null && Digits().IsMatch(value)).WithMessage(DigitsMessage);
        RuleFor(command => command.PhoneNumberId)
            .Must((command, value) => command.IsCoexistence ? value is null : value is not null && Digits().IsMatch(value))
            .WithMessage(DigitsMessage);
        RuleFor(command => command.BusinessId).Must(value => value is null || Digits().IsMatch(value)).WithMessage(DigitsMessage);
    }
}

/// <summary>
/// Spec 2026-10-09 §8.1. El <c>code</c> vence a los 30 s: antes del canje sólo hay validaciones de
/// milisegundos. Después del canje nada se deshace en Meta: volver a intentar exige otro <c>code</c> y
/// los pasos son seguros de repetir. La conexión, la ruta y la auditoría commitean juntas; una carrera
/// con otro tenant por el mismo número la resuelve <c>IX_connection_routes_provider_external</c>.
/// </summary>
public sealed class CompleteWhatsAppSignupHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IConnectionRouteRepository routes,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IWhatsAppSignupGateway gateway,
    IMetaAppSettings metaApp,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CompleteWhatsAppSignupCommand> validator)
    : ICommandHandler<CompleteWhatsAppSignupCommand, ConnectionResponse>
{
    public const string AmbiguousNumbersDetail =
        "La cuenta tiene varios números sin conectar; deja uno solo o conéctalo desde el número nuevo.";

    public async Task<ConnectionResponse> HandleAsync(CompleteWhatsAppSignupCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        var provider = catalog.Find(IntegrationProviders.WhatsAppCloud.Key)
            ?? throw new InvalidOperationException("The whatsapp-cloud provider is not in the catalog.");
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, command.TenantId, provider, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var author = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Paso 3: una lectura indexada; la garantía final la dan los índices únicos al guardar.
        if (await repository.CountAsync(command.TenantId, provider.Key, cancellationToken) >= provider.MaxConnections)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.LimitReached, $"A tenant can have at most {provider.MaxConnections} connections of this provider.");
        }

        if ((await repository.ListAsync(command.TenantId, cancellationToken)).Any(connection =>
                string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal)
                && string.Equals(connection.Name, command.Name!.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.NameTaken, "Another connection of this provider already uses that name.");
        }

        if (command.PhoneNumberId is { } requested && await routes.ExistsAsync(provider.Key, requested, cancellationToken))
        {
            throw NumberAlreadyConnected();
        }

        // D-M3: sin la app no hay con qué canjear; el log dice la causa real.
        if (!metaApp.IsConfigured)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.WhatsAppCodeExchangeFailed, "Meta:App is not configured; the code cannot be exchanged.");
        }

        // Paso 4.
        var exchange = await gateway.ExchangeCodeAsync(command.Code!, cancellationToken);
        if (!exchange.Succeeded || string.IsNullOrWhiteSpace(exchange.Value))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.WhatsAppCodeExchangeFailed, "Meta did not exchange the code.");
        }

        var token = exchange.Value;
        string phoneNumberId;
        if (command.IsCoexistence)
        {
            // Paso 6: el número ya está registrado en la app del teléfono; no se llama /register.
            phoneNumberId = await ResolveCoexistenceNumberAsync(provider, command.WabaId!, token, cancellationToken);
        }
        else
        {
            // Paso 5: PIN aleatorio que no se guarda (§8.1).
            phoneNumberId = command.PhoneNumberId!;
            var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            EnsureSucceeded(await gateway.RegisterNumberAsync(phoneNumberId, token, pin, cancellationToken), "register");
        }

        // Paso 7 y 8.
        EnsureSucceeded(await gateway.SubscribeAppAsync(command.WabaId!, token, cancellationToken), "subscribe");
        var read = await gateway.GetPhoneNumberAsync(phoneNumberId, token, cancellationToken);
        if (!read.Succeeded || read.Value is null)
        {
            throw RegistrationFailed("read");
        }

        // Paso 9: todo en una transacción.
        var now = clock.UtcNow;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WhatsAppCloudFieldKeys.PhoneNumberId] = phoneNumberId,
            [WhatsAppCloudFieldKeys.WabaId] = command.WabaId!,
        };
        Put(fields, WhatsAppCloudFieldKeys.DisplayPhoneNumber, read.Value.DisplayPhoneNumber);
        Put(fields, WhatsAppCloudFieldKeys.VerifiedName, read.Value.VerifiedName);
        Put(fields, WhatsAppCloudFieldKeys.QualityRating, read.Value.QualityRating);
        var connection = IntegrationConnection.Create(
            provider, command.TenantId, command.Name!, fields,
            new Dictionary<string, string>(StringComparer.Ordinal) { [WhatsAppCloudFieldKeys.AccessToken] = token },
            protector.Protect, author, now);
        repository.Add(connection);
        routes.Add(IntegrationConnectionRoute.Create(provider.Key, phoneNumberId, command.WabaId, command.TenantId, connection.Id));
        // changedFields por clave, más path y event como metadatos (§6.1); nunca un valor.
        string[] audited = [.. ConnectionAudit.KeysOf(connection), $"path:{command.Path}", $"event:{command.Event}"];
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Created, audited, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }

    private async Task<string> ResolveCoexistenceNumberAsync(IntegrationProvider provider, string wabaId, string token, CancellationToken cancellationToken)
    {
        var numbers = await gateway.ListPhoneNumbersAsync(wabaId, token, cancellationToken);
        if (!numbers.Succeeded || numbers.Value is null || numbers.Value.Count == 0)
        {
            throw RegistrationFailed("numbers");
        }

        if (numbers.Value.Count == 1)
        {
            return numbers.Value[0].Id;
        }

        var free = new List<string>();
        foreach (var number in numbers.Value)
        {
            if (!await routes.ExistsAsync(provider.Key, number.Id, cancellationToken))
            {
                free.Add(number.Id);
            }
        }

        return free.Count switch
        {
            1 => free[0],
            0 => throw NumberAlreadyConnected(),
            // D-M15: no se adivina.
            _ => throw new IntegrationsDomainException(IntegrationsErrorCodes.WhatsAppRegistrationFailed, AmbiguousNumbersDetail),
        };
    }

    private static void EnsureSucceeded<T>(GraphResult<T> result, string step)
    {
        if (!result.Succeeded)
        {
            throw RegistrationFailed(step);
        }
    }

    private static IntegrationsDomainException RegistrationFailed(string step) =>
        new(IntegrationsErrorCodes.WhatsAppRegistrationFailed, $"Meta rejected the '{step}' step of the WhatsApp registration.");

    private static IntegrationsDomainException NumberAlreadyConnected() =>
        new(IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected, "That WhatsApp number is already connected.");

    private static void Put(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }
}
```

(`IntegrationConnection.Create` llama `Normalize(..., requireAll: true)`: todos los campos de `whatsapp-cloud` son opcionales, así que una calidad que Meta no mande no falla. Un `verified_name` de más de 512 caracteres sí lanzaría `field_invalid`: aceptable.)

- [ ] **Step 3: Verificar GREEN (unitarias)**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~CompleteWhatsAppSignupHandlerTests"
```

Esperado: 15 correctas (6 + 4 teorías + 7 teorías del validador… cuenta la salida).

- [ ] **Step 4: Adaptador de Graph, endpoint y registro**

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Meta/MetaGraphSignupGateway.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Spec 2026-10-09 §8.1, pasos 4–8 contra Graph. El <c>client_secret</c> y el <c>code</c> van en la
/// query del canje y nunca en un log: <see cref="MetaGraphClient"/> registra sólo status y códigos.</summary>
internal sealed class MetaGraphSignupGateway(MetaGraphClient graph, IOptions<MetaAppOptions> options) : IWhatsAppSignupGateway
{
    public async Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var app = options.Value;
        var query = $"oauth/access_token?client_id={Uri.EscapeDataString(app.AppId ?? string.Empty)}"
            + $"&client_secret={Uri.EscapeDataString(app.AppSecret ?? string.Empty)}&code={Uri.EscapeDataString(code)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path(query));
        var response = await graph.SendAsync("oauth", request, accessToken: null, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<string>(null, Failure(response));
        }

        var token = ReadString(response.Body, "access_token");
        return token is null ? new GraphResult<string>(null, new GraphFailure((int)response.Status, null, null, "no_token")) : new GraphResult<string>(token, null);
    }

    public async Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, graph.Path($"{phoneNumberId}/register"))
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", pin }),
        };
        var response = await graph.SendAsync("register", request, accessToken, cancellationToken);
        return response.IsSuccess ? new GraphResult<bool>(true, null) : new GraphResult<bool>(false, Failure(response));
    }

    public async Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path($"{wabaId}/phone_numbers?fields=id,display_phone_number,verified_name,quality_rating"));
        var response = await graph.SendAsync("phone-numbers", request, accessToken, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<IReadOnlyList<WabaPhoneNumber>>(null, Failure(response));
        }

        var numbers = new List<WabaPhoneNumber>();
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (ReadNumber(item) is { } number)
                    {
                        numbers.Add(number);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new GraphResult<IReadOnlyList<WabaPhoneNumber>>(null, new GraphFailure((int)response.Status, null, null, "unreadable"));
        }

        return new GraphResult<IReadOnlyList<WabaPhoneNumber>>(numbers, null);
    }

    public async Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, graph.Path($"{wabaId}/subscribed_apps"));
        var response = await graph.SendAsync("subscribed-apps", request, accessToken, cancellationToken);
        return response.IsSuccess ? new GraphResult<bool>(true, null) : new GraphResult<bool>(false, Failure(response));
    }

    public async Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path($"{phoneNumberId}?fields={WhatsAppCloudConnectionTester.Fields}"));
        var response = await graph.SendAsync("phone-number", request, accessToken, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<WabaPhoneNumber>(null, Failure(response));
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var number = ReadNumber(document.RootElement) ?? new WabaPhoneNumber(phoneNumberId, null, null, null);
            return new GraphResult<WabaPhoneNumber>(number with { Id = phoneNumberId }, null);
        }
        catch (JsonException)
        {
            return new GraphResult<WabaPhoneNumber>(new WabaPhoneNumber(phoneNumberId, null, null, null), null);
        }
    }

    private static WabaPhoneNumber? ReadNumber(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(element, "id");
        return id is null ? null : new WabaPhoneNumber(id, ReadString(element, "display_phone_number"), ReadString(element, "verified_name"), ReadString(element, "quality_rating"));
    }

    private static string? ReadString(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, property);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static GraphFailure Failure(MetaGraphResponse response) =>
        new((int)response.Status, response.Error?.Code, response.Error?.Subcode, response.UnreachableReason ?? $"http_{(int)response.Status}");
}
```

Registro en `IntegrationsInfrastructureExtensions.cs`: `services.AddScoped<IWhatsAppSignupGateway, MetaGraphSignupGateway>();`.

`IntegrationsEndpoints.cs`, en `MapIntegrationsEndpoints` después del `DELETE`:

```csharp
        // Spec 2026-10-09 §5.2: lo manda el frontend al cerrar el popup de Meta; el code vence en 30 s.
        group.MapPost("/whatsapp/embedded-signup", CompleteSignupAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<EmbeddedSignupRequest>("application/json")
            .Produces<ConnectionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
```

y el método + request, con la misma forma que `CreateAsync` (mira cómo arma `Results.Created` con la URL de la conexión):

```csharp
    private static async Task<IResult> CompleteSignupAsync(
        Guid tenantId, EmbeddedSignupRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var connection = await dispatcher.SendAsync(
            new CompleteWhatsAppSignupCommand(
                tenantId, request.Name, request.Path, request.Event, request.Code, request.WabaId, request.PhoneNumberId, request.BusinessId),
            cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/integrations/connections/{connection.Id}", connection);
    }

/// <summary>Spec 2026-10-09 §5.2.</summary>
public sealed record EmbeddedSignupRequest(
    string? Name, string? Path, string? Event, string? Code, string? WabaId, string? PhoneNumberId, string? BusinessId);
```

`QepServiceCollectionExtensions.cs`, junto a los handlers de Integrations (busca `CreateConnectionCommand`):

```csharp
        services.AddScoped<
            ICommandHandler<CompleteWhatsAppSignupCommand, ConnectionResponse>,
            CompleteWhatsAppSignupHandler>();
```

- [ ] **Step 5: Pruebas de integración RED → GREEN**

`tests/Modules/Integrations/Modules.Integrations.IntegrationTests/FakeMetaGraphHandler.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Integrations.IntegrationTests;

internal sealed record MetaRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? Body);

/// <summary>Graph de mentira para el host entero: responde por patrón de ruta (regex sobre el path y la
/// query) en orden de registro; lo que no coincide responde 200 "{}". Anota cada request para que las
/// pruebas verifiquen qué se pidió y con qué token.</summary>
internal sealed class FakeMetaGraphHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(Regex Pattern, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = new();

    public ConcurrentQueue<MetaRequest> Requests { get; } = new();

    public Exception? Throw { get; set; }

    public void Respond(string pathPattern, HttpStatusCode status, string body) =>
        _rules.Enqueue((new Regex(pathPattern, RegexOptions.CultureInvariant), _ => Json(status, body)));

    public void RespondWith(string pathPattern, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _rules.Enqueue((new Regex(pathPattern, RegexOptions.CultureInvariant), respond));

    public void Reset() => _rules.Clear();

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string GraphError(int code, int? subcode = null) =>
        $$"""{"error":{"message":"x","type":"OAuthException","code":{{code}},"error_subcode":{{(subcode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}},"fbtrace_id":"trace"}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new MetaRequest(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));
        if (Throw is { } failure)
        {
            throw failure;
        }

        var target = request.RequestUri?.PathAndQuery ?? string.Empty;
        foreach (var (pattern, respond) in _rules)
        {
            if (pattern.IsMatch(target))
            {
                return respond(request);
            }
        }

        return Json(HttpStatusCode.OK, "{}");
    }
}
```

En `IntegrationsApiHarness.QepApiFactory`: `public FakeMetaGraphHandler MetaHandler { get; } = new();` y el `ConfigureTestServices` doble del Step 4 de la Task 6 (`MetaGraphClient.HttpClientName` → `MetaHandler`). Helper:

```csharp
    public static string EmbeddedSignupUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/whatsapp/embedded-signup";

    public static object SignupBody(string name = "Ventas", string @event = "FINISH", string? phoneNumberId = "111", string wabaId = "222") =>
        new { name, path = @event == "FINISH" ? "new_number" : "existing_business_app", @event, code = SentinelMetaCode, wabaId, phoneNumberId, businessId = "333" };

    public const string SentinelMetaCode = "meta-code-SENTINEL-4d5e6f";

    /// <summary>Graph feliz para FINISH: canje, register, subscribed_apps y lectura del número.</summary>
    public static void ScriptHappySignup(FakeMetaGraphHandler meta, string phoneNumberId = "111", string wabaId = "222")
    {
        meta.Respond("/oauth/access_token", HttpStatusCode.OK, $$"""{"access_token":"{{SentinelMetaAccessToken}}","token_type":"bearer"}""");
        meta.Respond($"/{phoneNumberId}/register", HttpStatusCode.OK, """{"success":true}""");
        meta.Respond($"/{wabaId}/subscribed_apps", HttpStatusCode.OK, """{"success":true}""");
        meta.Respond($"/{phoneNumberId}\\?fields=", HttpStatusCode.OK, """{"display_phone_number":"+57 300 123 4567","verified_name":"Origen Botánico","quality_rating":"GREEN","status":"CONNECTED","id":"111"}""");
    }
```

`tests/Modules/Integrations/Modules.Integrations.IntegrationTests/EmbeddedSignupApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.1 por HTTP: 201 con la conexión Active y sus campos; la carrera por el
/// mismo número en dos tenants; el módulo apagado; el 422 de cada paso.</summary>
public sealed class EmbeddedSignupApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FinishCreatesAnActiveConnectionWithTheMetaFieldsAndItsRoute()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappySignup(factory.MetaHandler);
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.NotNull(connection);
        Assert.Equal("Active", connection.Status);
        Assert.Equal("+57 300 123 4567", connection.Fields["displayPhoneNumber"]);
        Assert.Equal("Origen Botánico", connection.Fields["verifiedName"]);
        Assert.Equal("111", connection.Fields["phoneNumberId"]);
        Assert.Equal("222", connection.Fields["wabaId"]);
        Assert.Equal("GREEN", connection.Fields["qualityRating"]);
        Assert.True(connection.Secrets["accessToken"].Configured);
        Assert.True(connection.Secrets["accessToken"].Readable);
        Assert.DoesNotContain(SentinelMetaAccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelMetaCode, body, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.connection_routes WHERE external_id = '111' AND account_id = '222'"));
        var register = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/register", StringComparison.Ordinal));
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", register.Authorization);
        Assert.Matches("\"pin\":\"[0-9]{6}\"", register.Body);
        var exchange = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/oauth/access_token", StringComparison.Ordinal));
        Assert.Contains("client_id=" + MetaAppId, exchange.Uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameNumberInTwoTenantsIsNumberAlreadyConnected()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappySignup(factory.MetaHandler);
        var first = await RegisterTenantAsync(factory);
        var second = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, first.TenantId, "messaging");
        await EnableModuleAsync(connectionString, second.TenantId, "messaging");
        using var client1 = CreateClient(factory, first.OwnerUserId, first.TenantId, ManagePermissions);
        using var client2 = CreateClient(factory, second.OwnerUserId, second.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client1, HttpMethod.Post, EmbeddedSignupUrl(first.TenantId), SignupBody())).StatusCode);
        var response = await SendAsync(client2, HttpMethod.Post, EmbeddedSignupUrl(second.TenantId), SignupBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.whatsapp.number_already_connected", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountConnectionsAsync(connectionString, second.TenantId));
    }

    [Fact]
    public async Task WithTheModuleOffItIsForbiddenWithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
        Assert.Empty(factory.MetaHandler.Requests);
    }

    [Theory]
    [InlineData("/oauth/access_token", 400, 100, "integrations.whatsapp.code_exchange_failed")]
    [InlineData("/111/register", 400, 133016, "integrations.whatsapp.registration_failed")]
    [InlineData("/222/subscribed_apps", 403, 10, "integrations.whatsapp.registration_failed")]
    public async Task AGraphFailureAnswers422WithItsCodeAndSavesNothing(string path, int status, int code, string expected)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.MetaHandler.Respond(path, (HttpStatusCode)status, FakeMetaGraphHandler.GraphError(code));
        ScriptHappySignup(factory.MetaHandler);
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expected, (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountConnectionsAsync(connectionString, tenant.TenantId));
    }

    [Fact]
    public async Task CoexistenceDoesNotRegisterAndUsesTheOnlyNumber()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.MetaHandler.Respond("/oauth/access_token", HttpStatusCode.OK, $$"""{"access_token":"{{SentinelMetaAccessToken}}"}""");
        factory.MetaHandler.Respond("/222/phone_numbers", HttpStatusCode.OK, """{"data":[{"id":"777","display_phone_number":"+57 7","verified_name":"Coex","quality_rating":"YELLOW"}]}""");
        factory.MetaHandler.Respond("/222/subscribed_apps", HttpStatusCode.OK, """{"success":true}""");
        factory.MetaHandler.Respond("/777\\?fields=", HttpStatusCode.OK, """{"display_phone_number":"+57 7","verified_name":"Coex","quality_rating":"YELLOW","id":"777"}""");
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody(@event: "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING", phoneNumberId: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.Equal("777", connection!.Fields["phoneNumberId"]);
        Assert.DoesNotContain(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal));
    }
}
```

Y en `ConnectionSecretLeakTests.cs` un caso más (spec §11: la prueba de fuga se extiende al signup):

```csharp
    // Spec 2026-10-09 §11: el code, el token de acceso y el AppSecret no salen por ningún camino,
    // ni cuando Graph falla repitiéndolos en el cuerpo.
    [Fact]
    public async Task EmbeddedSignupNeverLeaksTheCodeTheTokenNorTheAppSecret()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        ScriptHappySignup(factory.MetaHandler);
        factory.MetaHandler.Reset();
        factory.MetaHandler.Respond("/oauth/access_token", HttpStatusCode.OK, $$"""{"access_token":"{{SentinelMetaAccessToken}}"}""");
        factory.MetaHandler.Respond("/111/register", HttpStatusCode.BadRequest, $$"""{"error":{"message":"{{SentinelMetaCode}} {{SentinelMetaAccessToken}} {{SentinelMetaAppSecret}}","code":100}}""");
        var tenant = await RegisterTenantAsync(host);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var failed = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var failedBody = await failed.Content.ReadAsStringAsync(Ct);
        factory.MetaHandler.Reset();
        ScriptHappySignup(factory.MetaHandler);
        var created = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var createdBody = await created.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertLogged(logs, "Graph register answered HTTP 400");
        await AssertNothingLeaksAsync(connectionString, logs, [failedBody, createdBody], SentinelMetaCode, SentinelMetaAccessToken, SentinelMetaAppSecret);
    }
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --no-build --filter "FullyQualifiedName~EmbeddedSignupApiTests|FullyQualifiedName~ConnectionSecretLeakTests|FullyQualifiedName~ConnectionLifecycleApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todas correctas (la primera corrida, antes de escribir el endpoint y el registro, tiene que haber dado 404/500: anótalo como RED si corriste antes del Step 4).

- [ ] **Step 6: Formato y commit**

```text
feat(integrations): Embedded Signup de WhatsApp con canje, registro, suscripción y ruta
```

---
### Task 8: `phone_e164` en Customers y el puerto `ICustomerPhoneDirectory`

**Files:**
- Modify: `Directory.Packages.props` (paquete `libphonenumber-csharp`) y todos los `packages.lock.json` (`dotnet restore --force-evaluate`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Modules.Customers.Infrastructure.csproj` (`PackageReference`)
- Create: `src/Modules/Customers/Modules.Customers.Domain/IPhoneNumberNormalizer.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (`PhoneE164`, `Create`/`Update` con `IPhoneNumberNormalizer?`, `RecomputePhoneE164`)
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs`
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Phones/CustomerPhoneBackfillWorker.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs`
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`ConfigureCustomer`, después de `Phone` `:66-68`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs`, `UpdateCustomer.cs`, `ImportCustomers.cs` (pasan el normalizador)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs`
- Create: migración `<timestamp>_AddCustomerPhoneE164.cs`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs` (casos nuevos), `tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs` (nuevo), `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerPhoneDirectoryTests.cs` (nuevo)

**Interfaces:**
- Consumes: `Customer.Assign(CustomerContactInfo)` (`Customer.cs:422-433`), `CustomersDbContext`, el harness de Customers (`tests/Modules/Customers/Modules.Customers.IntegrationTests/*Harness*.cs`, mira cómo arranca el host y siembra un tenant).
- Produces: `IPhoneNumberNormalizer { string? ToE164(string? phone, string country); }`, `Customer.PhoneE164`, `Customer.RecomputePhoneE164(IPhoneNumberNormalizer)`, `ICustomerPhoneDirectory { Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken ct); Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken ct); }`, `CustomerPhoneMatch(Guid Id, string Name)`.

- [ ] **Step 1: Paquete**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet package search libphonenumber-csharp --exact-match --take 1
```

Anota la última versión estable (P14) y en `Directory.Packages.props`, en orden alfabético (después de `FluentValidation.DependencyInjectionExtensions`):

```xml
    <!-- Spec 2026-10-09 §6.5: phone_e164 de clientes con el país del cliente, sin suponer
         «10 dígitos = Colombia». Sólo Customers.Infrastructure lo referencia. -->
    <PackageVersion Include="libphonenumber-csharp" Version="<la anotada>" />
```

En `Modules.Customers.Infrastructure.csproj`: `<PackageReference Include="libphonenumber-csharp" />`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet restore --force-evaluate
git status --short -- '*.lock.json' Directory.Packages.props
git diff --stat -- '*.lock.json'
```

Esperado: cambian los locks de `Modules.Customers.Infrastructure` y de todo lo que lo referencia transitivamente (Bootstrapper, Api, pruebas). Revisa el diff por arrastre transitivo (nada más que `libphonenumber-csharp` y sus dependencias).

- [ ] **Step 2: Pruebas RED del dominio y del normalizador**

`tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs`:

```csharp
using Modules.Customers.Infrastructure.Phones;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-09 §6.5: con «+» es internacional; si no, nacional del país; inválido → null.</summary>
public sealed class LibPhoneNumberNormalizerTests
{
    private readonly LibPhoneNumberNormalizer _normalizer = new();

    [Theory]
    [InlineData("+57 300 123 4567", "ES", "+573001234567")]
    [InlineData("300 123 4567", "CO", "+573001234567")]
    [InlineData("(310) 935-2187", "CO", "+573109352187")]
    [InlineData("03001234567", "CO", "+573001234567")]
    [InlineData("612 34 56 78", "ES", "+34612345678")]
    [InlineData("+1 (415) 555-2671", "CO", "+14155552671")]
    public void ValidNumbersBecomeE164WithPlus(string phone, string country, string expected) =>
        Assert.Equal(expected, _normalizer.ToE164(phone, country));

    [Theory]
    [InlineData("", "CO")]
    [InlineData("   ", "CO")]
    [InlineData("abc", "CO")]
    [InlineData("12", "CO")]
    [InlineData("300 123 4567", "ZZ")]
    [InlineData(null, "CO")]
    public void InvalidNumbersAreNullNeverAnException(string? phone, string country) =>
        Assert.Null(_normalizer.ToE164(phone, country));
}
```

En `CustomerTests.cs` agrega (usa los helpers del archivo: `ValidContact`, `Identification`, `Commercial`, `Now`, `TenantId`):

```csharp
    private sealed class StubPhoneNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is null ? null : "+57" + new string(phone.Where(char.IsDigit).ToArray());
    }

    // Spec 2026-10-09 §6.5: se calcula al crear y al editar; sin normalizador queda null (P7 del plan).
    [Fact]
    public void PhoneE164IsComputedOnCreateAndUpdateWithTheNormalizer()
    {
        var customer = Customer.Create(
            CustomerId.New(), TenantId, "MED-0001", "Verde", null, null, Identification(), ValidContact(), Commercial(), Now,
            phoneNormalizer: new StubPhoneNormalizer());

        Assert.Equal("+573109352187", customer.PhoneE164);

        customer.Update(
            "Verde", null, Identification(), ValidContact() with { Phone = "301 000 0000" }, Commercial(), ClassificationPrefix, Now,
            phoneNormalizer: new StubPhoneNormalizer());
        Assert.Equal("+573010000000", customer.PhoneE164);

        var without = Customer.Create(
            CustomerId.New(), TenantId, "MED-0002", "Sin", null, null, Identification(number: "1"), ValidContact(), Commercial(), Now);
        Assert.Null(without.PhoneE164);
        Assert.True(without.RecomputePhoneE164(new StubPhoneNormalizer()));
        Assert.Equal("+573109352187", without.PhoneE164);
        Assert.False(without.RecomputePhoneE164(new StubPhoneNormalizer()));
        Assert.Equal(1, without.Version);
    }
```

(Si `Customer.Create` recibe otros parámetros en el archivo —`cuc`, `principalAddress`—, ajusta la llamada a la firma real conservando `phoneNormalizer:` al final.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~LibPhoneNumberNormalizerTests|FullyQualifiedName~CustomerTests"
```

Esperado: `error CS0246 … 'IPhoneNumberNormalizer'`.

- [ ] **Step 3: Dominio e infraestructura**

`src/Modules/Customers/Modules.Customers.Domain/IPhoneNumberNormalizer.cs`:

```csharp
namespace Modules.Customers.Domain;

/// <summary>
/// Spec 2026-10-09 §6.5: de un teléfono como lo escribió la persona a E.164 con «+», con el país del
/// cliente como contexto. Puerto del dominio para no amarrarlo a <c>libphonenumber-csharp</c>; la
/// implementación vive en Infrastructure. <c>null</c> si no parsea o no es válido: nunca falla la
/// escritura del cliente.
/// </summary>
public interface IPhoneNumberNormalizer
{
    string? ToE164(string? phone, string country);
}
```

`Customer.cs`:

- Propiedad, después de `Phone`:

```csharp
    /// <summary>Spec 2026-10-09 §6.5: <see cref="Phone"/> en E.164 con «+», o <c>null</c> si no parsea. Lo
    /// lee Messaging por sus dígitos para emparejar con el <c>wa_id</c> de Meta.</summary>
    public string? PhoneE164 { get; private set; }
```

- `Create`: parámetro final `IPhoneNumberNormalizer? phoneNormalizer = null`; el constructor privado lo recibe también y, después de `Assign(contact);`, hace `PhoneE164 = phoneNormalizer?.ToE164(Phone, Country);`.
- `Update`: parámetro final `IPhoneNumberNormalizer? phoneNormalizer = null`; después de `Assign(normalizedContact);` agrega `PhoneE164 = phoneNormalizer?.ToE164(Phone, Country);`.
- Método nuevo, después de `Update`:

```csharp
    /// <summary>El backfill (spec §6.5): recalcula sin subir la versión ni <c>UpdatedAt</c>, porque nadie
    /// editó al cliente. <c>true</c> si cambió.</summary>
    public bool RecomputePhoneE164(IPhoneNumberNormalizer phoneNormalizer)
    {
        var next = phoneNormalizer.ToE164(Phone, Country);
        if (string.Equals(next, PhoneE164, StringComparison.Ordinal))
        {
            return false;
        }

        PhoneE164 = next;
        return true;
    }
```

`src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs`:

```csharp
using Modules.Customers.Domain;
using PhoneNumbers;

namespace Modules.Customers.Infrastructure.Phones;

/// <summary>Spec 2026-10-09 §6.5 sobre <c>libphonenumber-csharp</c>. Con «+» la región se ignora; sin «+»
/// el número es nacional del país del cliente (la librería maneja los prefijos de larga distancia).</summary>
internal sealed class LibPhoneNumberNormalizer : IPhoneNumberNormalizer
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    public string? ToE164(string? phone, string country)
    {
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        try
        {
            var parsed = Util.Parse(phone.Trim(), country.Trim().ToUpperInvariant());
            return Util.IsValidNumber(parsed) ? Util.Format(parsed, PhoneNumberFormat.E164) : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
}
```

`CustomersDbContext.ConfigureCustomer`, después de `Phone`:

```csharp
        // Spec 2026-10-09 §6.5: E.164 con «+» (máximo 15 dígitos). Índice parcial por tenant para el
        // emparejamiento de Messaging (phone_e164 = ANY(@phones)).
        customer.Property(value => value.PhoneE164)
            .HasColumnName("phone_e164")
            .HasMaxLength(16);
        customer.HasIndex(value => new { value.TenantId, value.PhoneE164 })
            .HasDatabaseName("IX_customers_tenant_phone_e164")
            .HasFilter("phone_e164 IS NOT NULL");
```

`src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs`:

```csharp
namespace Modules.Customers.Application;

public sealed record CustomerPhoneMatch(Guid Id, string Name);

/// <summary>
/// Spec 2026-10-09 §6.5: lo que Messaging necesita de Customers, por un puerto de este módulo con
/// adaptador en Bootstrapper (<c>MessagingCustomerDirectory</c>). Messaging no referencia Customers.
/// </summary>
public interface ICustomerPhoneDirectory
{
    /// <summary>Un cliente por número (D-M7: el creado primero, luego el id menor). Una consulta por
    /// página; los números que no están no aparecen.</summary>
    Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
        Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken);

    /// <summary>Los <c>phone_e164</c> de los clientes activos cuyo nombre contiene <paramref name="term"/>
    /// (usa <c>IX_customers_name_trgm</c>), hasta <paramref name="cap"/>; con más, la búsqueda queda
    /// incompleta y se registra.</summary>
    Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
}
```

`src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed partial class CustomerPhoneDirectory(CustomersDbContext dbContext, ILogger<CustomerPhoneDirectory> logger)
    : ICustomerPhoneDirectory
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Customer phone lookup by name hit the cap of {Cap} for tenant {TenantId}; the conversation search by customer name is incomplete.")]
    private static partial void LogCapHit(ILogger logger, int cap, Guid tenantId);

    public async Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
        Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, CustomerPhoneMatch>(StringComparer.Ordinal);
        if (e164.Count == 0)
        {
            return result;
        }

        var phones = e164.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.PhoneE164 != null && phones.Contains(customer.PhoneE164))
            .OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id)
            .Select(customer => new { customer.PhoneE164, customer.Id, customer.Name })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            // D-M7: el primero por created_at, id gana; los demás con el mismo número se ignoran.
            result.TryAdd(row.PhoneE164!, new CustomerPhoneMatch(row.Id.Value, row.Name));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken)
    {
        var pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var phones = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.PhoneE164 != null && EF.Functions.ILike(customer.Name, pattern, "\\"))
            .Select(customer => customer.PhoneE164!)
            .Distinct()
            .Take(cap + 1)
            .ToListAsync(cancellationToken);
        if (phones.Count > cap)
        {
            LogCapHit(logger, cap, tenantId);
            phones.RemoveAt(phones.Count - 1);
        }

        return phones;
    }
}
```

(`dbContext.Customers` es el `DbSet<Customer>`; confirma el nombre en `CustomersDbContext.cs:8-24`.)

`src/Modules/Customers/Modules.Customers.Infrastructure/Phones/CustomerPhoneBackfillWorker.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;

namespace Modules.Customers.Infrastructure.Phones;

/// <summary>
/// Spec 2026-10-09 §6.5: al arrancar, recorre en lotes de 500 las filas con <c>phone IS NOT NULL AND
/// phone_e164 IS NULL</c> y las recalcula. Idempotente: las que no parsean vuelven a intentarse en cada
/// arranque; son pocas. Sin versión ni <c>updated_at</c>: nadie editó al cliente.
/// </summary>
internal sealed partial class CustomerPhoneBackfillWorker(
    IServiceScopeFactory scopeFactory,
    IPhoneNumberNormalizer normalizer,
    ILogger<CustomerPhoneBackfillWorker> logger) : BackgroundService
{
    internal const int BatchSize = 500;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer phone backfill finished: {Updated} updated, {Unparseable} still without phone_e164.")]
    private static partial void LogFinished(ILogger logger, int updated, int unparseable);

    [LoggerMessage(Level = LogLevel.Error, Message = "Customer phone backfill failed; it runs again on the next start.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception);
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var updated = 0;
        var unparseable = 0;
        // Los ids ya mirados que siguen sin E.164 se saltan en el mismo arranque: si no, el lote
        // siguiente los volvería a traer para siempre.
        var skipped = new HashSet<Guid>();
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            var batch = await dbContext.Customers
                .Where(customer => customer.Phone != null && customer.PhoneE164 == null)
                .OrderBy(customer => customer.Id)
                .Take(BatchSize + skipped.Count)
                .ToListAsync(cancellationToken);
            var pending = batch.Where(customer => !skipped.Contains(customer.Id.Value)).Take(BatchSize).ToList();
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var customer in pending)
            {
                if (customer.RecomputePhoneE164(normalizer))
                {
                    updated++;
                }
                else
                {
                    unparseable++;
                    skipped.Add(customer.Id.Value);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        LogFinished(logger, updated, unparseable);
    }
}
```

(Si `Customer.Version` es token de concurrencia en EF y `RecomputePhoneE164` no lo sube, `SaveChanges` igual escribe la fila con `WHERE version = @old`: correcto, no hay cambio concurrente que esconder.)

`CustomersInfrastructureExtensions.cs`:

```csharp
        // Spec 2026-10-09 §6.5: phone_e164 para que Messaging empareje por teléfono.
        services.AddSingleton<IPhoneNumberNormalizer, LibPhoneNumberNormalizer>();
        services.AddScoped<ICustomerPhoneDirectory, CustomerPhoneDirectory>();
        services.AddHostedService<CustomerPhoneBackfillWorker>();
```

Handlers: `CreateCustomerHandler` y `UpdateCustomerHandler` reciben `IPhoneNumberNormalizer phoneNormalizer` por constructor y lo pasan como último argumento a `Customer.Create(…)` / `customer.Update(…)`; `ImportCustomersHandler` igual en `CreateOrUpdateCustomersAsync`. Las pruebas unitarias de esos handlers construyen el handler a mano: agrégales `new StubPhoneNormalizer()` (o un `FakePhoneNormalizer` compartido en los dobles de Customers que devuelva `"+57" + dígitos`).

Migración:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet ef migrations add AddCustomerPhoneE164 --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations
```

Verifica `AddColumn phone_e164 character varying(16) nullable` y `CreateIndex IX_customers_tenant_phone_e164 … filter: "phone_e164 IS NOT NULL"`. Comentario: `/// <summary>Spec 2026-10-09 §6.5. La columna nace nula; CustomerPhoneBackfillWorker la llena al arrancar.</summary>`.

- [ ] **Step 4: Prueba de integración del puerto y del backfill**

`tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerPhoneDirectoryTests.cs` (apóyate en el harness de Customers que ya existe para arrancar el host y crear clientes por HTTP; si crea por repositorio, mejor):

```csharp
/// <summary>Spec 2026-10-09 §6.5: un cliente por número (D-M7), búsqueda por nombre con tope, y el
/// backfill que llena phone_e164 de las filas viejas sin tocar su versión.</summary>
public sealed class CustomerPhoneDirectoryTests
{
    [Fact]
    public async Task MatchReturnsTheOldestCustomerPerPhoneAndOnlyWithinTheTenant()
    {
        // 1. Dos clientes del tenant con el mismo teléfono "300 123 4567" (CO) creados en ese orden, y uno
        //    de otro tenant con el mismo teléfono.
        // 2. ICustomerPhoneDirectory.MatchAsync(tenantId, ["+573001234567", "+570000000000"]).
        // 3. Un solo match, el del primer cliente creado; el número inexistente no aparece.
    }

    [Fact]
    public async Task FindPhonesByNameHonoursTheCapAndTheTenant()
    {
        // 1. Tres clientes "Droguería Norte/Sur/Centro" con teléfonos distintos, y uno de otro tenant.
        // 2. FindPhonesByNameAsync(tenantId, "drogueria", cap: 2) devuelve 2 números; con cap: 10, los 3;
        //    nunca el del otro tenant.
    }

    [Fact]
    public async Task TheBackfillFillsOldRowsWithoutBumpingTheirVersion()
    {
        // 1. Crear un cliente por HTTP (phone_e164 queda calculado) y luego, por SQL, poner
        //    phone_e164 = NULL y guardar version.
        // 2. Resolver CustomerPhoneBackfillWorker desde host.Services (IHostedService) y llamar
        //    RunOnceAsync(ct).
        // 3. phone_e164 vuelve a estar; version no cambió. Correrlo otra vez no cambia nada.
    }
}
```

Escribe los tres con el harness real (los comentarios son el guion; cada aserción es un `Assert`). Con `ScalarAsync` del harness de Customers (o uno equivalente con `NpgsqlCommand`) lees `version` y `phone_e164`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build --filter "FullyQualifiedName~CustomerPhoneDirectoryTests|FullyQualifiedName~CustomerApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: todas correctas. (`Modules.Customers.Domain` no referencia el paquete: `CustomersLayerTests.DomainDoesNotReferenceOuterLayers` sigue en verde.)

- [ ] **Step 5: Formato y commit** (un solo commit: props, locks, código, migración)

```text
feat(customers): phone_e164 con libphonenumber, backfill y puerto ICustomerPhoneDirectory
```

---

### Task 9: Dominio de Messaging

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/MessageEnums.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingDomainException.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/MessageFailureReasons.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageFailureReasonsTests.cs`

**Interfaces:**
- Consumes: `DomainException` (`BuildingBlocks.Domain`), `MessagingErrorCodes` (Task 3).
- Produces: `ConversationStatus`, `MessageDirection`, `MessageKind`, `MessageStatus`, `Conversation` (`Start`, `Resolve`, `Reopen`, `MarkRead`, `CustomerWindowExpiresAt`, `IsWindowOpen`, `WindowLength = 24 h`, `ReadReceiptWindow = 30 días`), `MessagingDomainException`, `MessageFailureReasons.For`, `MessageFailureReasons.Unconfirmed = -1`.

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.4–§8.5 y §8.7: resolver/reabrir con sus 422, marcar leído, y la ventana
/// de 24 h desde el último mensaje de la persona.</summary>
public sealed class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static Conversation Open() =>
        Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);

    [Fact]
    public void StartIsOpenWithoutUnreadNorWindow()
    {
        var conversation = Open();

        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(0, conversation.UnreadCount);
        Assert.Null(conversation.LastInboundAt);
        Assert.Null(conversation.CustomerWindowExpiresAt);
        Assert.False(conversation.IsWindowOpen(Now));
        Assert.Equal(1, conversation.Version);
        Assert.Equal("573001234567", conversation.WaId);
    }

    [Fact]
    public void ResolveAndReopenBumpTheVersionAndRejectANoOp()
    {
        var conversation = Open();

        conversation.Resolve(Now.AddMinutes(1));
        Assert.Equal(ConversationStatus.Resolved, conversation.Status);
        Assert.Equal(2, conversation.Version);
        Assert.Equal(Now.AddMinutes(1), conversation.UpdatedAt);

        var again = Assert.Throws<MessagingDomainException>(() => conversation.Resolve(Now.AddMinutes(2)));
        Assert.Equal(MessagingErrorCodes.AlreadyResolved, again.Code);
        Assert.Equal(2, conversation.Version);

        conversation.Reopen(Now.AddMinutes(3));
        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(3, conversation.Version);
        var open = Assert.Throws<MessagingDomainException>(() => conversation.Reopen(Now.AddMinutes(4)));
        Assert.Equal(MessagingErrorCodes.AlreadyOpen, open.Code);
    }

    [Fact]
    public void MarkReadZeroesTheCounterOnlyWhenThereWasSomething()
    {
        var conversation = Conversation.ForTests(Open(), unreadCount: 3, lastInboundAt: Now.AddHours(-1));

        Assert.True(conversation.MarkRead(Now));
        Assert.Equal(0, conversation.UnreadCount);
        Assert.Equal(2, conversation.Version);
        Assert.False(conversation.MarkRead(Now.AddMinutes(1)));
        Assert.Equal(2, conversation.Version);
    }

    [Theory]
    [InlineData(-23, true)]
    [InlineData(-24, false)]
    [InlineData(-25, false)]
    public void TheWindowIsTwentyFourHoursFromTheLastInbound(int hoursAgo, bool open)
    {
        var conversation = Conversation.ForTests(Open(), unreadCount: 0, lastInboundAt: Now.AddHours(hoursAgo));

        Assert.Equal(Now.AddHours(hoursAgo).AddHours(24), conversation.CustomerWindowExpiresAt);
        Assert.Equal(open, conversation.IsWindowOpen(Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+573001234567")]
    public void TheWaIdIsDigitsOnly(string? waId) =>
        Assert.Throws<ArgumentException>(() => Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), waId!, null, Now));
}
```

`tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageFailureReasonsTests.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §10.3: la tabla literal; lo desconocido es el texto genérico; sin código no hay motivo.</summary>
public sealed class MessageFailureReasonsTests
{
    [Theory]
    [InlineData(131047, "Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas.")]
    [InlineData(131026, "WhatsApp no pudo entregar el mensaje: puede que el número no use WhatsApp o tenga una versión desactualizada.")]
    [InlineData(131051, "WhatsApp no admite este tipo de mensaje.")]
    [InlineData(131049, "WhatsApp no entregó el mensaje para cuidar la experiencia de la persona. Espera al menos 24 horas antes de volver a intentarlo.")]
    [InlineData(131050, "La persona dejó de recibir mensajes de marketing de tu organización.")]
    [InlineData(131021, "No puedes enviarle un mensaje al mismo número que lo envía.")]
    [InlineData(131031, "La cuenta de WhatsApp Business está restringida o no pasó una verificación.")]
    [InlineData(131042, "Hay un problema con el método de pago de la cuenta de WhatsApp Business.")]
    [InlineData(131056, "Enviaste demasiados mensajes a esta persona en poco tiempo. Espera un momento y vuelve a intentarlo.")]
    [InlineData(133010, "El número de tu organización no está registrado en WhatsApp Business.")]
    [InlineData(368, "La cuenta de WhatsApp Business está restringida o deshabilitada por incumplir políticas.")]
    [InlineData(131005, "QEP perdió los permisos sobre tu cuenta de WhatsApp. Vuelve a conectar el número.")]
    [InlineData(131016, "WhatsApp no está disponible en este momento. Intenta de nuevo en unos minutos.")]
    [InlineData(131000, "WhatsApp no pudo enviar el mensaje por un error desconocido. Intenta de nuevo.")]
    [InlineData(-1, "No pudimos confirmar el envío con WhatsApp. Intenta de nuevo.")]
    [InlineData(999999, "WhatsApp no pudo entregar el mensaje.")]
    public void EveryKnownCodeHasItsText(int code, string expected) =>
        Assert.Equal(expected, MessageFailureReasons.For(code));

    [Fact]
    public void WithoutACodeThereIsNoReason() => Assert.Null(MessageFailureReasons.For(null));
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~MessageFailureReasonsTests"
```

Esperado: `error CS0246 … 'Conversation'`, `'MessageFailureReasons'`.

- [ ] **Step 2: Dominio**

`src/Modules/Messaging/Modules.Messaging.Domain/MessageEnums.cs`:

```csharp
namespace Modules.Messaging.Domain;

public enum ConversationStatus
{
    Open,
    Resolved,
}

/// <summary>Spec 2026-10-09 §7.1: en <c>messages</c> viajan como smallint con estos valores (excepción
/// documentada a los enums por texto). La API sigue mandando los nombres.</summary>
public enum MessageDirection
{
    Inbound = 1,
    Outbound = 2,
}

public enum MessageKind
{
    Text = 1,
    Image = 2,
    Video = 3,
    Audio = 4,
    Document = 5,
    Sticker = 6,
    Location = 7,
    Contacts = 8,
    Reaction = 9,
    Interactive = 10,
    Template = 11,
    Unsupported = 12,
}

/// <summary>El orden numérico <b>es</b> el de avance (§7.5): un acuse nunca retrocede.</summary>
public enum MessageStatus
{
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4,
}
```

`MessagingDomainException.cs`:

```csharp
using BuildingBlocks.Domain;

namespace Modules.Messaging.Domain;

/// <summary>Regla de negocio de Messaging: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class MessagingDomainException(string code, string message) : DomainException(code, message);
```

`Conversation.cs`:

```csharp
namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-09 §7.2: una persona (<c>wa_id</c>) hablando con un número de la organización
/// (<c>connection_id</c>). La ingesta de entrantes <b>no</b> pasa por acá (SQL atómico, §7.5): este
/// agregado sólo resuelve, reabre y marca leído, con <see cref="Version"/> como token de concurrencia.
/// La «foto» del último mensaje la escribe la ingesta; acá es de sólo lectura.
/// </summary>
public sealed class Conversation
{
    public const int WaIdMaxLength = 20;
    public const int ProfileNameMaxLength = 256;
    public const int PreviewMaxLength = 200;

    /// <summary>§8.3: 24 h desde el último mensaje de la persona.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(24);

    /// <summary>§8.4: Meta sólo marca leído dentro de 30 días desde la recepción.</summary>
    public static readonly TimeSpan ReadReceiptWindow = TimeSpan.FromDays(30);

    private Conversation()
    {
        WaId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>Dígitos, sin «+», como lo manda Meta.</summary>
    public string WaId { get; private set; }

    public string? ProfileName { get; private set; }

    public ConversationStatus Status { get; private set; }

    public int UnreadCount { get; private set; }

    public DateTimeOffset? LastInboundAt { get; private set; }

    public string? LastInboundWamid { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public Guid? LastMessageId { get; private set; }

    public MessageDirection? LastMessageDirection { get; private set; }

    public MessageKind? LastMessageKind { get; private set; }

    public string? LastMessagePreview { get; private set; }

    public MessageStatus? LastMessageStatus { get; private set; }

    public DateTimeOffset? LastMessageAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset? CustomerWindowExpiresAt => LastInboundAt?.Add(WindowLength);

    public bool IsWindowOpen(DateTimeOffset now) => CustomerWindowExpiresAt is { } expiresAt && expiresAt > now;

    /// <summary>Para Meta: hay un último entrante y tiene menos de 30 días (§8.4).</summary>
    public bool CanAcknowledgeReading(DateTimeOffset now) =>
        LastInboundWamid is not null && LastInboundAt is { } at && now - at < ReadReceiptWindow;

    /// <summary>Una conversación nueva, abierta y sin mensajes. En producción la crea la ingesta por SQL;
    /// esto lo usan las pruebas y el harness.</summary>
    public static Conversation Start(Guid id, Guid tenantId, Guid connectionId, string waId, string? profileName, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(waId) || waId.Length > WaIdMaxLength || !waId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A wa_id is 1 to 20 digits.", nameof(waId));
        }

        return new Conversation
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            WaId = waId,
            ProfileName = Truncate(profileName, ProfileNameMaxLength),
            Status = ConversationStatus.Open,
            UnreadCount = 0,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Sólo pruebas: una conversación con contadores y ventana ya puestos, como los dejaría la ingesta.</summary>
    public static Conversation ForTests(Conversation source, int unreadCount, DateTimeOffset? lastInboundAt, string? lastInboundWamid = "wamid.test")
    {
        source.UnreadCount = unreadCount;
        source.LastInboundAt = lastInboundAt;
        source.LastInboundWamid = lastInboundAt is null ? null : lastInboundWamid;
        return source;
    }

    public void Resolve(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Resolved)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyResolved, "The conversation is already resolved.");
        }

        Status = ConversationStatus.Resolved;
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyOpen, "The conversation is already open.");
        }

        Status = ConversationStatus.Open;
        Touch(now);
    }

    /// <summary>§8.4: <c>true</c> si había algo que marcar; con cero no hay versión nueva ni acuse a Meta.</summary>
    public bool MarkRead(DateTimeOffset now)
    {
        if (UnreadCount == 0)
        {
            return false;
        }

        UnreadCount = 0;
        Touch(now);
        return true;
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
```

`MessageFailureReasons.cs`:

```csharp
namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-09 §10.3: <c>failureReason</c> en español, calculado al leer desde <c>failure_code</c>
/// (cambiar un texto no pide migración). Sólo códigos verificados en la documentación de la Cloud API;
/// cualquier otro, el texto genérico. <c>failure_title</c> nunca sale por HTTP.
/// </summary>
public static class MessageFailureReasons
{
    /// <summary>§8.3: envío sin confirmar (timeout, 5xx o red); un acuse real de Meta lo corrige.</summary>
    public const int Unconfirmed = -1;

    public const string Generic = "WhatsApp no pudo entregar el mensaje.";

    private static readonly Dictionary<int, string> Texts = new()
    {
        [131047] = "Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas.",
        [131026] = "WhatsApp no pudo entregar el mensaje: puede que el número no use WhatsApp o tenga una versión desactualizada.",
        [131051] = "WhatsApp no admite este tipo de mensaje.",
        [131049] = "WhatsApp no entregó el mensaje para cuidar la experiencia de la persona. Espera al menos 24 horas antes de volver a intentarlo.",
        [131050] = "La persona dejó de recibir mensajes de marketing de tu organización.",
        [131021] = "No puedes enviarle un mensaje al mismo número que lo envía.",
        [131031] = "La cuenta de WhatsApp Business está restringida o no pasó una verificación.",
        [131042] = "Hay un problema con el método de pago de la cuenta de WhatsApp Business.",
        [131056] = "Enviaste demasiados mensajes a esta persona en poco tiempo. Espera un momento y vuelve a intentarlo.",
        [133010] = "El número de tu organización no está registrado en WhatsApp Business.",
        [368] = "La cuenta de WhatsApp Business está restringida o deshabilitada por incumplir políticas.",
        [131005] = "QEP perdió los permisos sobre tu cuenta de WhatsApp. Vuelve a conectar el número.",
        [131016] = "WhatsApp no está disponible en este momento. Intenta de nuevo en unos minutos.",
        [131000] = "WhatsApp no pudo enviar el mensaje por un error desconocido. Intenta de nuevo.",
        [Unconfirmed] = "No pudimos confirmar el envío con WhatsApp. Intenta de nuevo.",
    };

    public static string? For(int? failureCode) =>
        failureCode is null ? null : Texts.GetValueOrDefault(failureCode.Value, Generic);
}
```

- [ ] **Step 3: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~MessageFailureReasonsTests"
```

Esperado: todas correctas (5 + 4 teorías + 17 teorías).

- [ ] **Step 4: Formato y commit**

```text
feat(messaging): agregado Conversation, enums del hilo y motivos de falla en español
```

---

### Task 10: Persistencia de Messaging, migración `InitialMessaging`, opciones y harness

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContextFactory.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageRecord.cs` (entidades de lectura `MessageRecord`, `MessageMediaRecord`, `WebhookDeliveryRecord`)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageColumnCodes.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationRepository.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingDatabaseInitializer.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Options/MessagingMetaOptions.cs`, `MessagingWebhookOptions.cs`, `MessagingWorkerOptions.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs` (+ `IMessagingUnitOfWork`)
- Create: migración `<timestamp>_InitialMessaging.cs` (+ Designer y snapshot), editada a mano
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Modify: `src/Api/Program.cs` (initializer después de Integrations), `src/Api/appsettings.example.json`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingPersistenceTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageColumnCodesTests.cs`

**Interfaces:**
- Consumes: `IntegrationsDbContext` como molde (`:16-118`), `IntegrationsDbContextFactory`, `IntegrationsApiHarness` (molde literal), `AuditDbContext.ConfigureEntry`.
- Produces: `MessagingDbContext` (`Schema = "messaging"`, `DbSet<Conversation> Conversations`, `DbSet<MessageRecord> Messages`, `DbSet<MessageMediaRecord> Media`, `DbSet<WebhookDeliveryRecord> Deliveries`, `DbSet<AuditEntry> AuditEntries`), `MessageColumnCodes` (`ToCode`/`FromCode` para los tres enums), `IConversationRepository`, `IMessagingUnitOfWork`, opciones, `MessagingApiHarness` (`StartDatabaseAsync`, `QepApiFactory` con `MetaHandler`, `TestAppSecret`, `TestVerifyToken`, `EnableMessagingAsync`, `SeedWhatsAppConnectionAsync`, `SeedConversationAsync`, `ScalarAsync`, `ExecuteAsync`, `CreateClient`, `SendAsync`, `ProblemAsync`, `WithTestClock`).

- [ ] **Step 1: Pruebas RED (mapa de códigos y persistencia)**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageColumnCodesTests.cs`:

```csharp
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §7.1: la única traducción smallint ↔ enum, de ida y vuelta, y los CHECK de
/// la tabla coinciden con los valores.</summary>
public sealed class MessageColumnCodesTests
{
    [Fact]
    public void EveryEnumValueRoundTrips()
    {
        foreach (var direction in Enum.GetValues<MessageDirection>())
        {
            Assert.Equal(direction, MessageColumnCodes.ToDirection(MessageColumnCodes.ToCode(direction)));
        }

        foreach (var kind in Enum.GetValues<MessageKind>())
        {
            Assert.Equal(kind, MessageColumnCodes.ToKind(MessageColumnCodes.ToCode(kind)));
        }

        foreach (var status in Enum.GetValues<MessageStatus>())
        {
            Assert.Equal(status, MessageColumnCodes.ToStatus(MessageColumnCodes.ToCode(status)));
        }
    }

    [Fact]
    public void TheCodesAreTheOnesOfTheSpec()
    {
        Assert.Equal((short)1, MessageColumnCodes.ToCode(MessageDirection.Inbound));
        Assert.Equal((short)2, MessageColumnCodes.ToCode(MessageDirection.Outbound));
        Assert.Equal((short)12, MessageColumnCodes.ToCode(MessageKind.Unsupported));
        Assert.Equal((short)4, MessageColumnCodes.ToCode(MessageStatus.Failed));
        Assert.Equal("direction IN (1, 2)", MessagingDbContext.DirectionCheck);
        Assert.Equal("kind BETWEEN 1 AND 12", MessagingDbContext.KindCheck);
        Assert.Equal("status BETWEEN 1 AND 4", MessagingDbContext.StatusCheck);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void AnUnknownCodeIsLoudNotSilent(short code) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageColumnCodes.ToKind(code));
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingPersistenceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §7: lo que sólo la base hace cumplir —CHECKs, únicos parciales, la columna
/// generada con la configuración <c>messaging.es_unaccent</c>, la cascada— y que la migración es idempotente.</summary>
public sealed class MessagingPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSchemaHasTheIndexesExtensionsAndConfigurationOfTheSpec()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var indexes = await ScalarAsync<string>(connectionString,
            "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'messaging'");
        foreach (var expected in new[]
        {
            "IX_conversations_connection_wa", "IX_conversations_tenant_status_activity", "IX_conversations_tenant_open",
            "IX_conversations_tenant_unread", "IX_conversations_profile_name_trgm", "IX_conversations_wa_id_trgm",
            "IX_messages_thread", "IX_messages_connection_wamid", "IX_messages_conversation_client", "IX_messages_tenant_search",
            "IX_message_media_pending", "IX_webhook_deliveries_body_sha256", "IX_webhook_deliveries_pending",
        })
        {
            Assert.Contains(expected, indexes, StringComparison.Ordinal);
        }

        Assert.Equal(3L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_extension WHERE extname IN ('pg_trgm','unaccent','btree_gin')"));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace WHERE n.nspname = 'messaging' AND c.cfgname = 'es_unaccent'"));
        Assert.Equal("{fillfactor=80}", await ScalarAsync<string>(connectionString, "SELECT reloptions::text FROM pg_class WHERE relname = 'conversations' AND relnamespace = 'messaging'::regnamespace"));
        Assert.Equal("{fillfactor=90}", await ScalarAsync<string>(connectionString, "SELECT reloptions::text FROM pg_class WHERE relname = 'messages' AND relnamespace = 'messaging'::regnamespace"));
        Assert.Equal("drogueri", await ScalarAsync<string>(connectionString, "SELECT (regexp_match(to_tsvector('messaging.es_unaccent', 'Droguería')::text, '''([a-z]+)'''))[1]"));
        Assert.Equal("pedid", await ScalarAsync<string>(connectionString, "SELECT (regexp_match(to_tsvector('messaging.es_unaccent', 'pedidos')::text, '''([a-z]+)'''))[1]"));
    }

    // §7.3: el bloque DO … IF NOT EXISTS se puede correr otra vez sobre la misma base.
    [Fact]
    public async Task TheSearchConfigurationBlockIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        await ExecuteAsync(connectionString, Modules.Messaging.Infrastructure.Persistence.Migrations.InitialMessaging.SearchConfigurationSql);
        await ExecuteAsync(connectionString, Modules.Messaging.Infrastructure.Persistence.Migrations.InitialMessaging.SearchConfigurationSql);

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_ts_config WHERE cfgname = 'es_unaccent'"));
    }

    [Fact]
    public async Task TheChecksRejectAnInboundFailedAndAnUnknownKind()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");

        var inboundFailed = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, direction: 1, kind: 1, status: 4));
        Assert.Equal("CK_messages_inbound_not_failed", inboundFailed.ConstraintName);
        var unknownKind = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, direction: 1, kind: 13, status: 2));
        Assert.Equal("CK_messages_kind", unknownKind.ConstraintName);
    }

    [Fact]
    public async Task TheWamidIsUniquePerConnectionAndTheConversationCascades()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        await InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: "wamid.1");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: "wamid.1"));
        Assert.Equal("IX_messages_connection_wamid", duplicate.ConstraintName);
        await InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: null);
        await InsertMessageAsync(connectionString, conversationId, tenantId, connectionId, 1, 1, 2, wamid: null);

        await ExecuteAsync(connectionString, "DELETE FROM messaging.conversations WHERE id = @id", ("id", conversationId));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.messages"));
    }

    [Fact]
    public async Task ResolveWithAStaleVersionIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var conversationId = await SeedConversationAsync(factory, tenantId, Guid.CreateVersion7(), "573001234567");

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var conversation = await repository.FindAsync(tenantId, conversationId, Ct);
        Assert.NotNull(conversation);
        // La carrera: otro pod (la ingesta, §7.5) sube la versión después de que este scope leyó la fila.
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET version = version + 1 WHERE id = @id", ("id", conversationId));
        conversation.Resolve(DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<BuildingBlocks.Application.RequestConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Null(await repository.FindAsync(Guid.CreateVersion7(), conversationId, Ct));
    }

    private static Task<int> InsertMessageAsync(
        string connectionString, Guid conversationId, Guid tenantId, Guid connectionId, int direction, int kind, int status, string? wamid = "wamid.x")
        => ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, now(), @direction, @kind, @status, 'hola', @wamid, now())
            """,
            ("id", Guid.CreateVersion7()), ("conversationId", conversationId), ("tenantId", tenantId), ("connectionId", connectionId),
            ("direction", (short)direction), ("kind", (short)kind), ("status", (short)status), ("wamid", (object?)wamid ?? DBNull.Value));
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageColumnCodesTests"
```

Esperado: `error CS0246 … 'MessageColumnCodes'`.

- [ ] **Step 2: Puertos, opciones y mapa**

`src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que uno inexistente.</summary>
public interface IConversationRepository
{
    /// <summary>Con tracking: para resolver, reabrir y marcar leído (§8.4–§8.5).</summary>
    Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>Guarda el agregado y su auditoría en una transacción; traduce la concurrencia a
/// <c>concurrency.conflict</c> en Infrastructure.</summary>
public interface IMessagingUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Options/MessagingMetaOptions.cs`:

```csharp
namespace Modules.Messaging.Infrastructure.Options;

/// <summary>La parte de <c>Meta:App</c> que Messaging usa (P1 del plan): la firma del webhook, el token de
/// verificación y la versión de Graph. El validador vive en Integrations. Vacío = ausente.</summary>
public sealed class MessagingMetaOptions
{
    public const string SectionName = "Meta:App";

    public string? AppSecret { get; set; }

    public string? WebhookVerifyToken { get; set; }

    public string GraphApiVersion { get; set; } = "v24.0";
}
```

`MessagingWebhookOptions.cs`:

```csharp
namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-09 §9 y §8.2.</summary>
public sealed class MessagingWebhookOptions
{
    public const string SectionName = "Messaging:Webhook";

    /// <summary>D-M1: 4 MiB (Meta documenta hasta 3 MB).</summary>
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;

    public int ConcurrencyLimit { get; set; } = 64;

    public int QueueLimit { get; set; } = 256;
}
```

`MessagingWorkerOptions.cs`:

```csharp
namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-09 §9: intervalos de entregas, medios y purga, con defaults en código.</summary>
public sealed class MessagingWorkerOptions
{
    public const string SectionName = "Messaging:Workers";

    public int DeliveryPollSeconds { get; set; } = 3;

    public int MediaPollSeconds { get; set; } = 5;

    public int PurgeIntervalHours { get; set; } = 24;

    public int DeliveryRetentionDays { get; set; } = 7;
}
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageColumnCodes.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-09 §7.1: la única traducción entre los smallint de <c>messages</c> y los enums.
/// Un código desconocido lanza: ignorarlo escondería una base corrupta.</summary>
internal static class MessageColumnCodes
{
    public static short ToCode(MessageDirection value) => (short)value;

    public static short ToCode(MessageKind value) => (short)value;

    public static short ToCode(MessageStatus value) => (short)value;

    public static MessageDirection ToDirection(short code) => Enum.IsDefined((MessageDirection)code)
        ? (MessageDirection)code
        : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown message direction code.");

    public static MessageKind ToKind(short code) => Enum.IsDefined((MessageKind)code)
        ? (MessageKind)code
        : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown message kind code.");

    public static MessageStatus ToStatus(short code) => Enum.IsDefined((MessageStatus)code)
        ? (MessageStatus)code
        : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown message status code.");
}
```

- [ ] **Step 3: DbContext, entidades de lectura, repositorio y unit of work**

`MessageRecord.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La fila de <c>messaging.messages</c> para leer con EF. Se escribe por SQL (ingesta y envío);
/// por eso no es un agregado: sin reglas, sin setters privados que esconder.</summary>
internal sealed class MessageRecord
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageKind Kind { get; set; }
    public MessageStatus Status { get; set; }
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public string? Details { get; set; }
    public string? Wamid { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SentByMemberId { get; set; }
    public int? FailureCode { get; set; }
    public string? FailureTitle { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public MessageMediaRecord? Media { get; set; }
}

internal sealed class MessageMediaRecord
{
    public Guid MessageId { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string MetaMediaId { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? StorageKey { get; set; }
    public DateTimeOffset? StoredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class WebhookDeliveryRecord
{
    public long Id { get; set; }
    public byte[] BodySha256 { get; set; } = [];
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
```

`MessagingDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Messaging.Domain;
using NpgsqlTypes;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>messaging</c> (spec 2026-10-09 §7): <c>conversations</c> (agregado), <c>messages</c>,
/// <c>message_media</c> y <c>webhook_deliveries</c> (filas de lectura; se escriben por SQL). Proyecta
/// <c>audit.entries</c> como Integrations (ExcludeFromMigrations). Lo que EF no modela —extensiones, la
/// configuración de búsqueda y el fillfactor— va en SQL dentro de <c>InitialMessaging</c>.
/// </summary>
public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public const string Schema = "messaging";

    public const string DirectionCheck = "direction IN (1, 2)";
    public const string KindCheck = "kind BETWEEN 1 AND 12";
    public const string StatusCheck = "status BETWEEN 1 AND 4";

    /// <summary>§7.3: calificada con esquema para no depender del search_path de quien inserte.</summary>
    public const string SearchVectorSql = "to_tsvector('messaging.es_unaccent', coalesce(text, '') || ' ' || coalesce(caption, ''))";

    public DbSet<Conversation> Conversations => Set<Conversation>();

    internal DbSet<MessageRecord> Messages => Set<MessageRecord>();

    internal DbSet<MessageMediaRecord> Media => Set<MessageMediaRecord>();

    internal DbSet<WebhookDeliveryRecord> Deliveries => Set<WebhookDeliveryRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConversation(modelBuilder);
        ConfigureMessage(modelBuilder);
        ConfigureMedia(modelBuilder);
        ConfigureDelivery(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureConversation(ModelBuilder modelBuilder)
    {
        var conversation = modelBuilder.Entity<Conversation>();
        conversation.ToTable("conversations", Schema, table =>
        {
            table.HasCheckConstraint("CK_conversations_status", "status IN ('Open','Resolved')");
            table.HasCheckConstraint("CK_conversations_unread", "unread_count >= 0");
        });
        conversation.HasKey(value => value.Id);
        conversation.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        conversation.Property(value => value.TenantId).HasColumnName("tenant_id");
        conversation.Property(value => value.ConnectionId).HasColumnName("connection_id");
        conversation.Property(value => value.WaId).HasColumnName("wa_id").HasMaxLength(Conversation.WaIdMaxLength);
        conversation.Property(value => value.ProfileName).HasColumnName("profile_name").HasMaxLength(Conversation.ProfileNameMaxLength);
        conversation.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        conversation.Property(value => value.UnreadCount).HasColumnName("unread_count").HasDefaultValue(0);
        conversation.Property(value => value.LastInboundAt).HasColumnName("last_inbound_at");
        conversation.Property(value => value.LastInboundWamid).HasColumnName("last_inbound_wamid");
        conversation.Property(value => value.LastActivityAt).HasColumnName("last_activity_at");
        conversation.Property(value => value.LastMessageId).HasColumnName("last_message_id");
        conversation.Property(value => value.LastMessageDirection).HasColumnName("last_message_direction")
            .HasConversion(value => value == null ? (short?)null : MessageColumnCodes.ToCode(value.Value), code => code == null ? null : MessageColumnCodes.ToDirection(code.Value));
        conversation.Property(value => value.LastMessageKind).HasColumnName("last_message_kind")
            .HasConversion(value => value == null ? (short?)null : MessageColumnCodes.ToCode(value.Value), code => code == null ? null : MessageColumnCodes.ToKind(code.Value));
        conversation.Property(value => value.LastMessagePreview).HasColumnName("last_message_preview").HasMaxLength(Conversation.PreviewMaxLength);
        conversation.Property(value => value.LastMessageStatus).HasColumnName("last_message_status")
            .HasConversion(value => value == null ? (short?)null : MessageColumnCodes.ToCode(value.Value), code => code == null ? null : MessageColumnCodes.ToStatus(code.Value));
        conversation.Property(value => value.LastMessageAt).HasColumnName("last_message_at");
        conversation.Property(value => value.CreatedAt).HasColumnName("created_at");
        conversation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        conversation.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        conversation.Ignore(value => value.CustomerWindowExpiresAt);

        conversation.HasIndex(value => new { value.ConnectionId, value.WaId }).IsUnique().HasDatabaseName("IX_conversations_connection_wa");
        conversation.HasIndex(value => new { value.TenantId, value.Status, value.LastActivityAt, value.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_conversations_tenant_status_activity");
        conversation.HasIndex(value => value.TenantId).HasDatabaseName("IX_conversations_tenant_open").HasFilter("status = 'Open'");
        conversation.HasIndex(value => value.TenantId).HasDatabaseName("IX_conversations_tenant_unread")
            .HasFilter("unread_count > 0").IncludeProperties(value => value.UnreadCount);
        conversation.HasIndex(value => value.ProfileName).HasDatabaseName("IX_conversations_profile_name_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
        conversation.HasIndex(value => value.WaId).HasDatabaseName("IX_conversations_wa_id_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
    }

    private static void ConfigureMessage(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<MessageRecord>();
        message.ToTable("messages", Schema, table =>
        {
            table.HasCheckConstraint("CK_messages_direction", DirectionCheck);
            table.HasCheckConstraint("CK_messages_kind", KindCheck);
            table.HasCheckConstraint("CK_messages_status", StatusCheck);
            table.HasCheckConstraint("CK_messages_inbound_not_failed", "direction = 2 OR status <> 4");
        });
        message.HasKey(value => value.Id);
        message.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        message.Property(value => value.ConversationId).HasColumnName("conversation_id");
        message.Property(value => value.TenantId).HasColumnName("tenant_id");
        message.Property(value => value.ConnectionId).HasColumnName("connection_id");
        message.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        message.Property(value => value.Direction).HasColumnName("direction").HasColumnType("smallint")
            .HasConversion(value => MessageColumnCodes.ToCode(value), code => MessageColumnCodes.ToDirection(code));
        message.Property(value => value.Kind).HasColumnName("kind").HasColumnType("smallint")
            .HasConversion(value => MessageColumnCodes.ToCode(value), code => MessageColumnCodes.ToKind(code));
        message.Property(value => value.Status).HasColumnName("status").HasColumnType("smallint")
            .HasConversion(value => MessageColumnCodes.ToCode(value), code => MessageColumnCodes.ToStatus(code));
        message.Property(value => value.Text).HasColumnName("text");
        message.Property(value => value.Caption).HasColumnName("caption").HasMaxLength(1024);
        message.Property(value => value.Details).HasColumnName("details").HasColumnType("jsonb");
        message.Property(value => value.Wamid).HasColumnName("wamid");
        message.Property(value => value.ClientId).HasColumnName("client_id");
        message.Property(value => value.SentByMemberId).HasColumnName("sent_by_member_id");
        message.Property(value => value.FailureCode).HasColumnName("failure_code");
        message.Property(value => value.FailureTitle).HasColumnName("failure_title");
        message.Property(value => value.CreatedAt).HasColumnName("created_at");
        // §7.3: generada y almacenada; EF la lee pero nunca la escribe.
        message.Property<NpgsqlTsVector>("SearchVector").HasColumnName("search_vector")
            .HasComputedColumnSql(SearchVectorSql, stored: true);
        message.HasOne<Conversation>().WithMany().HasForeignKey(value => value.ConversationId)
            .HasConstraintName("FK_messages_conversation").OnDelete(DeleteBehavior.Cascade);
        message.HasOne(value => value.Media).WithOne().HasForeignKey<MessageMediaRecord>(value => value.MessageId)
            .HasConstraintName("FK_message_media_message").OnDelete(DeleteBehavior.Cascade);

        message.HasIndex(value => new { value.ConversationId, value.OccurredAt, value.Id })
            .IsDescending(false, true, true).HasDatabaseName("IX_messages_thread");
        message.HasIndex(value => new { value.ConnectionId, value.Wamid }).IsUnique()
            .HasDatabaseName("IX_messages_connection_wamid").HasFilter("wamid IS NOT NULL");
        message.HasIndex(value => new { value.ConversationId, value.ClientId }).IsUnique()
            .HasDatabaseName("IX_messages_conversation_client").HasFilter("client_id IS NOT NULL");
        message.HasIndex(nameof(MessageRecord.TenantId), "SearchVector").HasDatabaseName("IX_messages_tenant_search").HasMethod("gin");
    }

    private static void ConfigureMedia(ModelBuilder modelBuilder)
    {
        var media = modelBuilder.Entity<MessageMediaRecord>();
        media.ToTable("message_media", Schema);
        media.HasKey(value => value.MessageId);
        media.Property(value => value.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        media.Property(value => value.MimeType).HasColumnName("mime_type").HasMaxLength(128);
        media.Property(value => value.FileName).HasColumnName("file_name").HasMaxLength(256);
        media.Property(value => value.MetaMediaId).HasColumnName("meta_media_id").HasMaxLength(64);
        media.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        media.Property(value => value.Sha256).HasColumnName("sha256").HasMaxLength(64);
        media.Property(value => value.StorageKey).HasColumnName("storage_key").HasMaxLength(256);
        media.Property(value => value.StoredAt).HasColumnName("stored_at");
        media.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        media.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        media.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(256);
        media.HasIndex(value => value.NextAttemptAt).HasDatabaseName("IX_message_media_pending").HasFilter("stored_at IS NULL");
    }

    private static void ConfigureDelivery(ModelBuilder modelBuilder)
    {
        var delivery = modelBuilder.Entity<WebhookDeliveryRecord>();
        delivery.ToTable("webhook_deliveries", Schema);
        delivery.HasKey(value => value.Id);
        delivery.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        delivery.Property(value => value.BodySha256).HasColumnName("body_sha256");
        delivery.Property(value => value.Payload).HasColumnName("payload").HasColumnType("jsonb");
        delivery.Property(value => value.ReceivedAt).HasColumnName("received_at");
        delivery.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        delivery.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        delivery.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        delivery.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(512);
        delivery.HasIndex(value => value.BodySha256).IsUnique().HasDatabaseName("IX_webhook_deliveries_body_sha256");
        delivery.HasIndex(value => value.Id).HasDatabaseName("IX_webhook_deliveries_pending").HasFilter("processed_at IS NULL");
    }
}
```

`MessagingDbContextFactory.cs`: copia literal de `IntegrationsDbContextFactory` con `MessagingDbContext` y `MessagingDbContext.Schema`.

`MessagingDatabaseInitializer.cs`: copia literal de `IntegrationsDatabaseInitializer` (`InitializeMessagingDatabaseAsync`).

`ConversationRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

internal sealed class ConversationRepository(MessagingDbContext dbContext) : IConversationRepository
{
    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.SingleOrDefaultAsync(
            conversation => conversation.TenantId == tenantId && conversation.Id == conversationId, cancellationToken);
}
```

`MessagingUnitOfWork.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La versión vieja es <c>concurrency.conflict</c> (412 por If-Match en resolve/reopen, §8.5).</summary>
internal sealed class MessagingUnitOfWork(MessagingDbContext dbContext) : IMessagingUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The conversation changed while the operation was being committed.", exception);
        }
    }
}
```

`MessagingInfrastructureExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure;

public static class MessagingInfrastructureExtensions
{
    public static IServiceCollection AddMessagingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<MessagingDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", MessagingDbContext.Schema)));

        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IMessagingUnitOfWork, MessagingUnitOfWork>();

        // Spec 2026-10-09 §9. Meta:App se valida en Integrations (P1); acá sólo se bindea lo que se usa.
        services.AddOptions<MessagingMetaOptions>().Bind(configuration.GetSection(MessagingMetaOptions.SectionName));
        services.AddOptions<MessagingWebhookOptions>().Bind(configuration.GetSection(MessagingWebhookOptions.SectionName));
        services.AddOptions<MessagingWorkerOptions>().Bind(configuration.GetSection(MessagingWorkerOptions.SectionName));

        return services;
    }
}
```

`Program.cs`, después de `InitializeIntegrationsDatabaseAsync`:

```csharp
// Messaging (spec 2026-10-09): después de Audit (escribe en audit.entries); no tiene FKs a otros
// esquemas: Integrations y Customers entran por puertos. Las extensiones (pg_trgm, unaccent,
// btree_gin) van en public, como las de Customers y Catalog.
await app.Services.InitializeMessagingDatabaseAsync(
    app.Lifetime.ApplicationStopping);
```

`appsettings.example.json`, hermana de `"Meta"`:

```json
  "Messaging": {
    "Webhook": {
      "MaxBodyBytes": 4194304,
      "ConcurrencyLimit": 64,
      "QueueLimit": 256
    },
    "Workers": {
      "DeliveryPollSeconds": 3,
      "MediaPollSeconds": 5,
      "PurgeIntervalHours": 24,
      "DeliveryRetentionDays": 7
    }
  }
```

- [ ] **Step 4: Migración `InitialMessaging` generada y editada**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build src/Modules/Messaging/Modules.Messaging.Infrastructure --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet ef migrations add InitialMessaging --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
```

Edita el `Up` generado:

1. Agrega a la clase una constante pública con el SQL de §7.3 (la prueba de idempotencia la usa):

```csharp
        /// <summary>Spec 2026-10-09 §7.3: extensiones en public (precedente pg_trgm, memoria «pg_trgm necesita el
        /// schema public») y la configuración de búsqueda, idempotente porque CREATE TEXT SEARCH CONFIGURATION
        /// no tiene IF NOT EXISTS y las bases locales pueden tenerla a medias.</summary>
        public const string SearchConfigurationSql = """
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE EXTENSION IF NOT EXISTS unaccent;
            CREATE EXTENSION IF NOT EXISTS btree_gin;
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace
                    WHERE n.nspname = 'messaging' AND c.cfgname = 'es_unaccent')
                THEN
                    CREATE TEXT SEARCH CONFIGURATION messaging.es_unaccent (COPY = pg_catalog.spanish);
                    ALTER TEXT SEARCH CONFIGURATION messaging.es_unaccent
                        ALTER MAPPING FOR hword, hword_part, word, asciiword, asciihword, hword_asciipart
                        WITH unaccent, spanish_stem;
                END IF;
            END $$;
            """;
```

2. Justo después de `migrationBuilder.EnsureSchema(name: "messaging");` (primera línea del `Up`) y **antes** de cualquier `CreateTable`: `migrationBuilder.Sql(SearchConfigurationSql);` (la columna generada de `messages` referencia la configuración).
3. Al final del `Up`, el fillfactor (§7.2, §7.3) y el parámetro del GIN (§7.3):

```csharp
            migrationBuilder.Sql("ALTER TABLE messaging.conversations SET (fillfactor = 80);");
            migrationBuilder.Sql("ALTER TABLE messaging.messages SET (fillfactor = 90);");
            migrationBuilder.Sql("ALTER INDEX messaging.\"IX_messages_tenant_search\" SET (gin_pending_list_limit = 2048);");
            migrationBuilder.Sql("ALTER TABLE messaging.messages SET (autovacuum_vacuum_scale_factor = 0.02);");
```

4. Revisa que el `CreateTable("messages")` tenga `search_vector` con `computedColumnSql: "to_tsvector('messaging.es_unaccent', …)"` y `stored: true`, los cuatro `CheckConstraint`, y que los índices lleven `descending`, `filter`, `.Annotation("Npgsql:IndexMethod", "gin")` y `.Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" })` donde corresponde; `IX_conversations_tenant_unread` con `.Annotation("Npgsql:IndexInclude", new[] { "unread_count" })`. Si EF no generó alguno, agrégalo con `migrationBuilder.Sql` y deja el modelo como está (el snapshot ya coincide con el modelo).
5. En el `Down`, no se borran las extensiones (otros módulos usan `pg_trgm`); sí `DROP TEXT SEARCH CONFIGURATION IF EXISTS messaging.es_unaccent;` después de borrar las tablas.

- [ ] **Step 5: Harness de Messaging**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs`: copia **literal** de `IntegrationsApiHarness` (contenedor compartido, plantilla, `TestDatabase`, `QepApiFactory`, `CreateClient`, `RegisterTenantAsync`, `SendAsync`, `ProblemAsync`, `ScalarAsync`, `ExecuteAsync`, `WithCapturedLogs`, `CapturedLogs`) con estos cambios:

- namespace `Modules.Messaging.IntegrationTests`, clase `MessagingApiHarness`, `TemplateDatabase = "qep_template_messaging"`.
- `QepApiFactory` fija además `Meta:App:*` (como en la Task 1, con `TestAppSecret = "meta-app-secret-SENTINEL-m1"`, `TestVerifyToken = "meta-verify-token-SENTINEL-m2-0123456789abcdef"`, `MetaAppId`, `MetaConfigId`), `Messaging:Workers:DeliveryPollSeconds = 3600`, `MediaPollSeconds = 3600`, `PurgeIntervalHours = 24` (los workers no corren solos durante una prueba: se llama `DrainAsync`), y enchufa `MetaHandler` (una copia de `FakeMetaGraphHandler` de la Task 7 en este proyecto) a **los dos** clientes: `Modules.Integrations.Infrastructure.Meta.MetaGraphClient.HttpClientName` y `"messaging.meta-graph"`.
- `public FakeMetaGraphHandler MetaHandler { get; } = new();`
- Constantes: `ReadPermissions = [MessagingPermissions.ConversationRead]`, `ManagePermissions = [ConversationRead, ConversationManage]`, `WebhookUrl = "/api/webhooks/whatsapp"`, `ConversationsUrl(tenantId)`, `ConversationUrl(tenantId, id)`, `MessagesUrl(tenantId, id)`, `SearchUrl(tenantId)`, `MediaUrl(tenantId, messageId)`.
- `EnableMessagingAsync(connectionString, tenantId)` = el `EnableModuleAsync` de la Task 4 con `'messaging'`.
- `SeedWhatsAppConnectionAsync(host, tenantId, name, phoneNumberId, wabaId, accessToken)` = copia del de la Task 5 (los tipos de Integrations están disponibles por `Api.csproj`).
- `SeedConversationAsync(host, tenantId, connectionId, waId, profileName = "Laura")`: crea `Conversation.Start(...)` y lo agrega con `MessagingDbContext` del scope (`dbContext.Conversations.Add(...); await dbContext.SaveChangesAsync(ct)`), devuelve el id.
- `Sign(byte[] body)` → `"sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestAppSecret), body))`.
- `PostWebhookAsync(HttpClient client, string json, string? signature = null)`: `POST WebhookUrl` con `Content-Type: application/json`, header `X-Hub-Signature-256` = `signature ?? Sign(bytes)`; **sin** `X-Qep-Client` (el webhook está exento de CSRF, aunque el stub no lo registre).
- `WithTestClock(factory, TestClock clock)` → `ConfigureServices(services => { services.RemoveAll<IClock>(); services.AddSingleton<IClock>(clock); })` con `internal sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }` (P10). Como `IClock` se registra scoped en `AddQepPlatform`, el reemplazo tiene que ir con `WithWebHostBuilder(builder => builder.ConfigureTestServices(...))`.
- Fixtures de Meta en una clase `MetaPayloads` (misma carpeta): `InboundText(phoneNumberId, waId, wamid, timestamp, text, profileName = "Laura", displayPhoneNumber = "15550000000")`, `InboundMedia(phoneNumberId, waId, wamid, timestamp, type, mediaId, mimeType, caption = null, filename = null)`, `Status(phoneNumberId, wamid, status, timestamp, callbackData = null, errorCode = null, errorTitle = null)`, `AccountUpdate(wabaId, @event, banState = null)`, `Change(field, valueJson)`, devolviendo el JSON **literal** de Meta (`{"object":"whatsapp_business_account","entry":[{"id":"<waba>","changes":[{"field":"messages","value":{...}}]}]}`). Escríbelos con `JsonSerializer.Serialize` sobre objetos anónimos para no pelear con escapes.

- [ ] **Step 6: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~MessageColumnCodesTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~MessagingPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todas correctas (incluidas `ConfigurationExampleTests` con las tres secciones nuevas y `MessagingLayerTests`). Si `IsDescending`/`IncludeProperties`/`HasOperators` no compilan con esos nombres, mira `TenancyDbContext.cs:192-194` (`IsDescending`) y la migración `AddCustomersNameTrigramIndex` (`Npgsql:IndexOperators`) y usa `.HasAnnotation(...)` equivalentes.

- [ ] **Step 7: Formato y commit**

```text
feat(messaging): esquema messaging con migración InitialMessaging, opciones y harness
```

---
### Task 11: Webhook de Meta — verificación, firma, tope, deduplicación, limitador y CSRF

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IWebhookSignatureVerifier.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IWebhookDeliveries.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/WebhookHandlers.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/HmacWebhookSignatureVerifier.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/WebhookDeliveries.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Api/WhatsAppWebhookEndpoints.cs`
- Modify: `src/Api/RateLimiterPolicies.cs`, `src/Api/Program.cs:56-70, 149`
- Modify: `src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs:44-51`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (dos handlers)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/HmacWebhookSignatureVerifierTests.cs`
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/RequireCsrfHeaderMiddlewareTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WebhookApiTests.cs`

**Interfaces:**
- Consumes: `MessagingMetaOptions`, `MessagingWebhookOptions`, `MessagingDbContext`, `RequestUnauthorizedException`.
- Produces: `IWebhookSignatureVerifier`, `IWebhookDeliveries`, `VerifyWebhookQuery(string? Mode, string? Token, string? Challenge) : IQuery<string?>`, `ReceiveWebhookCommand(byte[] Body, string? Signature) : ICommand<bool>`, `VerifyWebhookHandler`, `ReceiveWebhookHandler`, `WhatsAppWebhookEndpoints.MapWhatsAppWebhook(this IEndpointRouteBuilder, string rateLimiterPolicy)`, `RateLimiterPolicies.Webhook = "webhook"`.

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/HmacWebhookSignatureVerifierTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.2 y §11: HMAC-SHA256 del cuerpo crudo con el AppSecret, comparación en
/// tiempo constante; sin sección, nada pasa.</summary>
public sealed class HmacWebhookSignatureVerifierTests
{
    private const string Secret = "meta-app-secret-SENTINEL-u1";
    private const string Token = "meta-verify-token-SENTINEL-u2-0123456789abcdef";

    private static HmacWebhookSignatureVerifier Verifier(string? secret = Secret, string? token = Token) =>
        new(Options.Create(new MessagingMetaOptions { AppSecret = secret, WebhookVerifyToken = token }));

    private static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void AValidSignatureInLowerOrUpperHexPasses()
    {
        var body = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account"}""");

        Assert.True(Verifier().VerifyBody(body, Sign(body, Secret)));
        Assert.True(Verifier().VerifyBody(body, Sign(body, Secret).ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=abc")]
    [InlineData("sha1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void AMissingOrMalformedSignatureFails(string? header) =>
        Assert.False(Verifier().VerifyBody("{}"u8, header));

    [Fact]
    public void ADifferentSecretOrADifferentBodyFails()
    {
        var body = "{}"u8.ToArray();

        Assert.False(Verifier().VerifyBody(body, Sign(body, "other")));
        Assert.False(Verifier().VerifyBody("{ }"u8, Sign(body, Secret)));
    }

    [Fact]
    public void WithoutTheSectionNothingPassesAndItIsNotConfigured()
    {
        var verifier = Verifier(secret: " ", token: null);
        var body = "{}"u8.ToArray();

        Assert.False(verifier.IsConfigured);
        Assert.False(verifier.VerifyBody(body, Sign(body, " ")));
        Assert.False(verifier.VerifyToken(null));
    }

    [Fact]
    public void TheVerifyTokenIsComparedExactly()
    {
        Assert.True(Verifier().VerifyToken(Token));
        Assert.False(Verifier().VerifyToken(Token + "x"));
        Assert.False(Verifier().VerifyToken(Token.ToUpperInvariant()));
        Assert.False(Verifier().VerifyToken(null));
    }
}
```

`tests/Bootstrapper/Bootstrapper.UnitTests/RequireCsrfHeaderMiddlewareTests.cs`:

```csharp
using Bootstrapper.Csrf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-09 §6.7: la única excepción al header es por ruta, para el webhook, que no acepta la
/// cookie de sesión y se autentica con su firma.</summary>
public sealed class RequireCsrfHeaderMiddlewareTests
{
    private sealed class NoProblemDetails : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
    }

    private static async Task<(bool Reached, int Status)> RunAsync(string method, string path, string? header)
    {
        var reached = false;
        var middleware = new RequireCsrfHeaderMiddleware(_ => { reached = true; return Task.CompletedTask; }, new NoProblemDetails());
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (header is not null)
        {
            context.Request.Headers["X-Qep-Client"] = header;
        }

        await middleware.InvokeAsync(context);
        return (reached, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/webhooks/whatsapp")]
    [InlineData("POST", "/api/webhooks/other")]
    public async Task TheWebhookPrefixPassesWithoutTheHeader(string method, string path) =>
        Assert.True((await RunAsync(method, path, null)).Reached);

    [Theory]
    [InlineData("/api/v1/tenants/x/messaging/conversations")]
    [InlineData("/api/webhooksx")]
    [InlineData("/API/webhooks/whatsapp")]
    public async Task EverythingElseStillNeedsTheHeader(string path)
    {
        var (reached, status) = await RunAsync("POST", path, null);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.True((await RunAsync("POST", path, "web")).Reached);
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WebhookApiTests.cs`:

```csharp
using System.Net;
using System.Text;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2: GET con token bueno y malo; POST firmado → 200 y una fila; sin firma o
/// mal firmado → 401 y cero filas; cuerpo sobre el tope → 413; reenvío idéntico → una sola fila.</summary>
public sealed class WebhookApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheVerificationAnswersTheChallengeOnlyWithTheRightToken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var ok = await client.GetAsync($"{WebhookUrl}?hub.mode=subscribe&hub.verify_token={Uri.EscapeDataString(TestVerifyToken)}&hub.challenge=123456", Ct);
        var wrong = await client.GetAsync($"{WebhookUrl}?hub.mode=subscribe&hub.verify_token=nope&hub.challenge=1", Ct);
        var wrongMode = await client.GetAsync($"{WebhookUrl}?hub.mode=unsubscribe&hub.verify_token={Uri.EscapeDataString(TestVerifyToken)}&hub.challenge=1", Ct);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("123456", await ok.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/plain", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongMode.StatusCode);
    }

    [Fact]
    public async Task ASignedPostIsStoredOnceAndAnsweredEmpty()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();
        var json = MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "hola");

        var first = await PostWebhookAsync(client, json);
        var second = await PostWebhookAsync(client, json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Empty(await first.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL AND attempts = 0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256=zz")]
    public async Task AMissingOrInvalidSignatureIs401WithoutTouchingTheDatabase(string? signature)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();

        var response = await PostWebhookAsync(client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "hola"), signature ?? "");
        if (signature is null)
        {
            // Sin header en absoluto.
            using var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response = await client.SendAsync(request, Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
    }

    [Fact]
    public async Task ABodyOverTheCapIs413()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Messaging:Webhook:MaxBodyBytes", "1024"));
        using var client = host.CreateClient();
        var json = "{\"pad\":\"" + new string('x', 2048) + "\"}";

        var response = await PostWebhookAsync(client, json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
    }

    // Sin Meta:App (D-M3, sólo fuera de producción): 403 al GET y 401 al POST.
    [Fact]
    public async Task WithoutMetaAppTheWebhookRejectsEverything()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Meta:App:AppSecret", string.Empty);
            builder.UseSetting("Meta:App:WebhookVerifyToken", string.Empty);
        });
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{WebhookUrl}?hub.mode=subscribe&hub.verify_token=&hub.challenge=1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(client, "{}", Sign("{}"u8.ToArray()))).StatusCode);
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~HmacWebhookSignatureVerifierTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~RequireCsrfHeaderMiddlewareTests"
```

Esperado: `error CS0246 … 'HmacWebhookSignatureVerifier'`; la de CSRF compila pero `TheWebhookPrefixPassesWithoutTheHeader` falla (`Assert.True() Failure`).

- [ ] **Step 2: Puertos, handlers y verificador**

`IWebhookSignatureVerifier.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.2 y §11. La implementación lee <c>Meta:App</c> en Infrastructure. Sin la
/// sección, <see cref="IsConfigured"/> es falso y nada verifica.</summary>
public interface IWebhookSignatureVerifier
{
    bool IsConfigured { get; }

    /// <summary><c>X-Hub-Signature-256 = sha256=&lt;hex&gt;</c>: HMAC-SHA256 del cuerpo crudo, comparado en
    /// tiempo constante.</summary>
    bool VerifyBody(ReadOnlySpan<byte> body, string? signatureHeader);

    /// <summary><c>hub.verify_token</c> del GET, comparado en tiempo constante sobre los bytes UTF-8.</summary>
    bool VerifyToken(string? token);
}
```

`IWebhookDeliveries.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>§8.2, paso 3: <c>INSERT … ON CONFLICT (body_sha256) DO NOTHING</c>. <c>false</c> si ya estaba.</summary>
public interface IWebhookDeliveries
{
    Task<bool> EnqueueAsync(byte[] body, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}
```

`WebhookHandlers.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>§8.2 GET: devuelve el <c>hub.challenge</c> si el token coincide; <c>null</c> si no (el endpoint responde 403).</summary>
public sealed record VerifyWebhookQuery(string? Mode, string? Token, string? Challenge) : IQuery<string?>;

public sealed class VerifyWebhookHandler(IWebhookSignatureVerifier verifier) : IQueryHandler<VerifyWebhookQuery, string?>
{
    public Task<string?> HandleAsync(VerifyWebhookQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(
            verifier.IsConfigured
            && string.Equals(query.Mode, "subscribe", StringComparison.Ordinal)
            && verifier.VerifyToken(query.Token)
            && !string.IsNullOrEmpty(query.Challenge)
                ? query.Challenge
                : null);
}

/// <summary>§8.2 POST: firma → guardar deduplicado → 200. <c>true</c> si la entrega era nueva.
/// <see cref="ToString"/> no imprime el cuerpo (trae mensajes de personas).</summary>
public sealed record ReceiveWebhookCommand(byte[] Body, string? Signature) : ICommand<bool>
{
    public override string ToString() => $"ReceiveWebhookCommand {{ Bytes = {Body.Length} }}";
}

public sealed class ReceiveWebhookHandler(IWebhookSignatureVerifier verifier, IWebhookDeliveries deliveries, IClock clock)
    : ICommandHandler<ReceiveWebhookCommand, bool>
{
    public Task<bool> HandleAsync(ReceiveWebhookCommand command, CancellationToken cancellationToken)
    {
        // 401 antes de tocar la base (§11).
        if (!verifier.IsConfigured || !verifier.VerifyBody(command.Body, command.Signature))
        {
            throw new RequestUnauthorizedException(MessagingErrorCodes.WebhookSignatureInvalid, "The webhook signature is missing or invalid.");
        }

        return deliveries.EnqueueAsync(command.Body, clock.UtcNow, cancellationToken);
    }
}
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/HmacWebhookSignatureVerifier.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Singleton: lee las opciones una vez. Nunca registra nada.</summary>
internal sealed class HmacWebhookSignatureVerifier(IOptions<MessagingMetaOptions> options) : IWebhookSignatureVerifier
{
    private const string Prefix = "sha256=";
    private const int DigestLength = 32;

    private readonly byte[]? _secret = Bytes(options.Value.AppSecret);
    private readonly byte[]? _token = Bytes(options.Value.WebhookVerifyToken);

    public bool IsConfigured => _secret is not null && _token is not null;

    public bool VerifyBody(ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (_secret is null || signatureHeader is null || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[DigestLength];
        Span<byte> provided = stackalloc byte[DigestLength];
        if (!Convert.TryFromHexString(signatureHeader.AsSpan(Prefix.Length), provided, out var written) || written != DigestLength)
        {
            return false;
        }

        HMACSHA256.HashData(_secret, body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public bool VerifyToken(string? token)
    {
        if (_token is null || token is null)
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(token);
        // FixedTimeEquals exige el mismo largo; con largos distintos la respuesta es "no" sin medir nada.
        return provided.Length == _token.Length && CryptographicOperations.FixedTimeEquals(_token, provided);
    }

    private static byte[]? Bytes(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Encoding.UTF8.GetBytes(value.Trim());
}
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/WebhookDeliveries.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.4: el único por <c>body_sha256</c> hace que un reenvío idéntico no cree otra fila. El cuerpo
/// ya pasó la firma; va como jsonb tal cual.</summary>
internal sealed class WebhookDeliveries(MessagingDbContext dbContext) : IWebhookDeliveries
{
    public async Task<bool> EnqueueAsync(byte[] body, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(body);
        var payload = Encoding.UTF8.GetString(body);
        var inserted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, attempts)
            VALUES ({hash}, {payload}::jsonb, {receivedAt}, 0)
            ON CONFLICT (body_sha256) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }
}
```

(Un cuerpo que no es JSON válido hace fallar el `::jsonb` con un 500 después de pasar la firma: sólo Meta puede firmar, y Meta manda JSON. Aceptado.)

`MessagingInfrastructureExtensions.cs`:

```csharp
        services.AddSingleton<IWebhookSignatureVerifier, HmacWebhookSignatureVerifier>();
        services.AddScoped<IWebhookDeliveries, WebhookDeliveries>();
```

- [ ] **Step 3: Endpoint, limitador, CSRF y registro**

`src/Modules/Messaging/Modules.Messaging.Api/WhatsAppWebhookEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-09 §6.7 y §8.2: fuera de <c>/api/v1/tenants</c>, anónimo, exento de CSRF por ruta
/// (<c>RequireCsrfHeaderMiddleware</c>), con el limitador de concurrencia <c>Webhook</c> y no el <c>Public</c>
/// por IP: Meta manda ráfagas desde pocas IPs y un 429 la hace reintentar hasta 7 días.
/// </summary>
public static class WhatsAppWebhookEndpoints
{
    public const string Route = "/api/webhooks/whatsapp";

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder endpoints, string rateLimiterPolicy)
    {
        endpoints.MapGet(Route, VerifyAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Webhooks")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapPost(Route, ReceiveAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Webhooks")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var query = httpContext.Request.Query;
        var challenge = await dispatcher.QueryAsync(
            new VerifyWebhookQuery(query["hub.mode"], query["hub.verify_token"], query["hub.challenge"]), cancellationToken);
        if (challenge is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Text(challenge, "text/plain");
    }

    private static async Task<IResult> ReceiveAsync(
        HttpContext httpContext, IRequestDispatcher dispatcher, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var maxBytes = configuration.GetValue("Messaging:Webhook:MaxBodyBytes", 4 * 1024 * 1024);
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        if (httpContext.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Crudo, sin pasar por el binder de JSON: la firma es sobre los bytes exactos.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        await dispatcher.SendAsync(
            new ReceiveWebhookCommand(buffer.ToArray(), httpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()),
            cancellationToken);
        return Results.Ok();
    }
}
```

`src/Api/RateLimiterPolicies.cs`:

```csharp
    /// <summary>Spec 2026-10-09 §6.7: concurrencia global con cola para el webhook de Meta, no una
    /// ventana por IP (§8.2).</summary>
    public const string Webhook = "webhook";
```

`src/Api/Program.cs`, dentro de `AddRateLimiter` después de la política `Public`:

```csharp
    // Spec 2026-10-09 §6.7 y §8.2: Meta manda desde pocas IPs compartidas y en ráfagas; con Public un
    // 429 la haría reintentar hasta 7 días. Una sola partición: lo que se protege es el pod.
    var webhook = builder.Configuration.GetSection("Messaging:Webhook");
    var webhookLimiter = new ConcurrencyLimiterOptions
    {
        PermitLimit = webhook.GetValue("ConcurrencyLimit", 64),
        QueueLimit = webhook.GetValue("QueueLimit", 256),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    };
    options.AddPolicy(
        RateLimiterPolicies.Webhook,
        _ => RateLimitPartition.GetConcurrencyLimiter(partitionKey: "webhook", factory: _ => webhookLimiter));
```

y después de `app.MapIntegrationsEndpoints();`:

```csharp
app.MapWhatsAppWebhook(RateLimiterPolicies.Webhook);
```

(agrega `using Modules.Messaging.Api;`).

`RequireCsrfHeaderMiddleware.cs:44-51`:

```csharp
    /// <summary>Spec 2026-10-09 §6.7: la excepción explícita por ruta que este comentario pedía. Ordinal
    /// y con la barra: /api/webhooksx no entra. El webhook no acepta la cookie de sesión; se autentica con
    /// la firma HMAC de Meta.</summary>
    private const string WebhookPrefix = "/api/webhooks/";

    public async Task InvokeAsync(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method)
            || context.Request.Path.Value?.StartsWith(WebhookPrefix, StringComparison.Ordinal) == true
            || string.Equals(context.Request.Headers[HeaderName], ExpectedValue, StringComparison.Ordinal))
```

`QepServiceCollectionExtensions.cs`, junto a los handlers de Integrations:

```csharp
        // Messaging (spec 2026-10-09): el webhook de Meta, anónimo y firmado.
        services.AddScoped<IQueryHandler<VerifyWebhookQuery, string?>, VerifyWebhookHandler>();
        services.AddScoped<ICommandHandler<ReceiveWebhookCommand, bool>, ReceiveWebhookHandler>();
```

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~HmacWebhookSignatureVerifierTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build --filter "FullyQualifiedName~RequireCsrfHeaderMiddlewareTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~WebhookApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todas correctas. Si `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` nombra `VerifyWebhookQuery -> IQueryHandler…`, el registro quedó mal tipado (`string?` vs `string`): el genérico tiene que ser exactamente `IQueryHandler<VerifyWebhookQuery, string?>`.

- [ ] **Step 5: Formato y commit**

```text
feat(messaging): webhook de Meta con firma HMAC, deduplicación, limitador propio y excepción de CSRF
```

---

### Task 12: Parser del payload de Meta

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayload.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayloadParser.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserTests.cs`

**Interfaces:**
- Consumes: `MessageKind`, `MessageStatus`.
- Produces (internal, Infrastructure): `WebhookPayloadParser.Parse(string json) → IReadOnlyList<WebhookChange>`; `WebhookChange` (abstracto) con `MessagesChange(string PhoneNumberId, IReadOnlyList<InboundMessage> Messages, IReadOnlyList<StatusUpdate> Statuses)`, `AccountUpdateChange(string WabaId, string Event, string? BanState)`, `UnknownChange(string Field)`; `InboundMessage(string Wamid, string WaId, string? ProfileName, DateTimeOffset OccurredAt, MessageKind Kind, string? Text, string? Caption, string? DetailsJson, InboundMedia? Media)`; `InboundMedia(string MetaMediaId, string MimeType, string? Sha256, string? FileName)`; `StatusUpdate(string Wamid, MessageStatus Status, DateTimeOffset OccurredAt, Guid? CallbackMessageId, int? ErrorCode, string? ErrorTitle)`; `MessageKindMap.FromMetaType(string? type) → MessageKind`.

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserTests.cs`:

```csharp
using System.Text.Json;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.7 (mapa de tipos), §8.2 (statuses, account_update, campos desconocidos) y
/// Review Focus 1: lo roto se salta, lo desconocido es Unsupported, nunca una excepción.</summary>
public sealed class WebhookPayloadParserTests
{
    private static string Envelope(string field, object value) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[] { new { id = "222", changes = new[] { new { field, value } } } },
    });

    private static object Messages(object message, object? contact = null) => new
    {
        messaging_product = "whatsapp",
        metadata = new { display_phone_number = "15550000000", phone_number_id = "111" },
        contacts = new[] { contact ?? new { profile = new { name = "Laura" }, wa_id = "573001234567" } },
        messages = new[] { message },
    };

    [Theory]
    [InlineData("text", MessageKind.Text)]
    [InlineData("image", MessageKind.Image)]
    [InlineData("video", MessageKind.Video)]
    [InlineData("audio", MessageKind.Audio)]
    [InlineData("document", MessageKind.Document)]
    [InlineData("sticker", MessageKind.Sticker)]
    [InlineData("location", MessageKind.Location)]
    [InlineData("contacts", MessageKind.Contacts)]
    [InlineData("reaction", MessageKind.Reaction)]
    [InlineData("interactive", MessageKind.Interactive)]
    [InlineData("button", MessageKind.Interactive)]
    [InlineData("unsupported", MessageKind.Unsupported)]
    [InlineData("order", MessageKind.Unsupported)]
    [InlineData("system", MessageKind.Unsupported)]
    [InlineData("whatever", MessageKind.Unsupported)]
    [InlineData(null, MessageKind.Unsupported)]
    public void EveryMetaTypeHasItsKind(string? type, MessageKind expected) =>
        Assert.Equal(expected, MessageKindMap.FromMetaType(type));

    [Fact]
    public void ATextMessageCarriesTheContactTheTimestampAndTheBody()
    {
        var json = Envelope("messages", Messages(new { from = "573001234567", id = "wamid.1", timestamp = "1760000000", type = "text", text = new { body = "hola" } }));

        var change = Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json)));
        var message = Assert.Single(change.Messages);

        Assert.Equal("111", change.PhoneNumberId);
        Assert.Equal(("wamid.1", "573001234567", "Laura", MessageKind.Text, "hola"), (message.Wamid, message.WaId, message.ProfileName, message.Kind, message.Text));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), message.OccurredAt);
        Assert.Null(message.Media);
        Assert.Empty(change.Statuses);
    }

    [Fact]
    public void AnImageWithCaptionGoesToCaptionAndMedia()
    {
        var json = Envelope("messages", Messages(new { from = "573001234567", id = "wamid.2", timestamp = "1760000000", type = "image", image = new { id = "media-1", mime_type = "image/jpeg", sha256 = "abc", caption = "la foto" } }));

        var message = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);

        Assert.Equal(MessageKind.Image, message.Kind);
        Assert.Null(message.Text);
        Assert.Equal("la foto", message.Caption);
        Assert.Equal(("media-1", "image/jpeg", "abc"), (message.Media!.MetaMediaId, message.Media.MimeType, message.Media.Sha256));
    }

    [Fact]
    public void ADocumentKeepsItsFileNameAndALocationItsDetails()
    {
        var document = Envelope("messages", Messages(new { from = "1", id = "wamid.3", timestamp = "1760000000", type = "document", document = new { id = "m", mime_type = "application/pdf", filename = "orden.pdf" } }));
        var location = Envelope("messages", Messages(new { from = "1", id = "wamid.4", timestamp = "1760000000", type = "location", location = new { latitude = 4.6, longitude = -74.1, name = "Casa", address = "Calle 1" } }));

        var doc = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(document)[0]).Messages);
        var loc = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(location)[0]).Messages);

        Assert.Equal("orden.pdf", doc.Media!.FileName);
        Assert.Equal(MessageKind.Location, loc.Kind);
        using var details = JsonDocument.Parse(loc.DetailsJson!);
        Assert.Equal(4.6, details.RootElement.GetProperty("latitude").GetDouble());
        Assert.Equal("Casa", details.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void ContactsReactionInteractiveAndButtonFillTheTextFromTheirShape()
    {
        var contacts = Envelope("messages", Messages(new { from = "1", id = "w1", timestamp = "1", type = "contacts", contacts = new[] { new { name = new { formatted_name = "Ana" } }, new { name = new { formatted_name = "Luis" } } } }));
        var reaction = Envelope("messages", Messages(new { from = "1", id = "w2", timestamp = "1", type = "reaction", reaction = new { message_id = "wamid.x", emoji = "👍" } }));
        var interactive = Envelope("messages", Messages(new { from = "1", id = "w3", timestamp = "1", type = "interactive", interactive = new { type = "button_reply", button_reply = new { id = "b", title = "Sí" } } }));
        var button = Envelope("messages", Messages(new { from = "1", id = "w4", timestamp = "1", type = "button", button = new { payload = "p", text = "Confirmar" } }));

        Assert.Equal("Ana, Luis", Single(contacts).Text);
        Assert.Equal("👍", Single(reaction).Text);
        Assert.Equal("Sí", Single(interactive).Text);
        Assert.Equal("Confirmar", Single(button).Text);
        Assert.Equal(MessageKind.Interactive, Single(button).Kind);

        static InboundMessage Single(string json) => Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);
    }

    [Fact]
    public void AnUnsupportedMessageKeepsItsTypeAndErrorsInDetails()
    {
        var json = Envelope("messages", Messages(new { from = "1", id = "w5", timestamp = "1", type = "unsupported", errors = new[] { new { code = 131051, title = "Unsupported message type" } } }));

        var message = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);

        Assert.Equal(MessageKind.Unsupported, message.Kind);
        Assert.Contains("\"type\":\"unsupported\"", message.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("131051", message.DetailsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sent", MessageStatus.Sent)]
    [InlineData("delivered", MessageStatus.Delivered)]
    [InlineData("read", MessageStatus.Read)]
    [InlineData("played", MessageStatus.Read)]
    [InlineData("failed", MessageStatus.Failed)]
    public void EveryStatusHasItsValueAndTheCallbackIsParsed(string status, MessageStatus expected)
    {
        var callback = Guid.CreateVersion7();
        var json = Envelope("messages", new
        {
            messaging_product = "whatsapp",
            metadata = new { display_phone_number = "1", phone_number_id = "111" },
            statuses = new[] { new { id = "wamid.out", status, timestamp = "1760000000", recipient_id = "573001234567", biz_opaque_callback_data = $"qep:{callback}", errors = new[] { new { code = 131047, title = "Re-engagement message" } } } },
        });

        var update = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Statuses);

        Assert.Equal(expected, update.Status);
        Assert.Equal(callback, update.CallbackMessageId);
        Assert.Equal(131047, update.ErrorCode);
        Assert.Equal("Re-engagement message", update.ErrorTitle);
    }

    [Theory]
    [InlineData("qep:not-a-guid")]
    [InlineData("other:123")]
    [InlineData(null)]
    public void ACallbackThatIsNotOursIsNull(string? callback)
    {
        var json = Envelope("messages", new { metadata = new { phone_number_id = "111" }, statuses = new[] { new { id = "w", status = "sent", timestamp = "1", biz_opaque_callback_data = callback } } });

        Assert.Null(Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Statuses).CallbackMessageId);
    }

    [Fact]
    public void AccountUpdateAndUnknownFieldsAreTyped()
    {
        var account = Envelope("account_update", new { @event = "DISABLED_UPDATE", ban_info = new { waba_ban_state = "DISABLE" } });
        var unknown = Envelope("smb_message_echoes", new { anything = 1 });

        var update = Assert.IsType<AccountUpdateChange>(Assert.Single(WebhookPayloadParser.Parse(account)));
        Assert.Equal(("222", "DISABLED_UPDATE", "DISABLE"), (update.WabaId, update.Event, update.BanState));
        Assert.Equal("smb_message_echoes", Assert.IsType<UnknownChange>(Assert.Single(WebhookPayloadParser.Parse(unknown))).Field);
    }

    // Review Focus 1: lo roto no tumba al worker.
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"entry":[]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"messages":[{"from":"1","id":"w","timestamp":"abc","type":"text","text":{"body":"x"}}]}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"messages":[{"id":"w","timestamp":"1","type":"text"}]}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"statuses":[{"status":"sent"}]}}]}]}""")]
    public void BrokenShapesAreSkippedNeverThrown(string json)
    {
        var changes = WebhookPayloadParser.Parse(json);

        Assert.All(changes.OfType<MessagesChange>(), change =>
        {
            Assert.Empty(change.Messages);
            Assert.Empty(change.Statuses);
        });
    }

    [Fact]
    public void AMessageWithoutAMatchingContactStillHasItsWaIdFromFrom()
    {
        var json = Envelope("messages", new { metadata = new { phone_number_id = "111" }, contacts = Array.Empty<object>(), messages = new[] { new { from = "573009999999", id = "w", timestamp = "1", type = "text", text = new { body = "x" } } } });

        var message = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);

        Assert.Equal("573009999999", message.WaId);
        Assert.Null(message.ProfileName);
    }

    [Fact]
    public void InvalidJsonIsEmpty() => Assert.Empty(WebhookPayloadParser.Parse("not json"));
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WebhookPayloadParserTests"
```

Esperado: `error CS0246 … 'WebhookPayloadParser'`.

- [ ] **Step 2: Modelo y parser**

`WebhookPayload.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Un <c>change</c> de un webhook de Meta (spec §3, «Mensaje entrante», «Estados», «account_update»).</summary>
internal abstract record WebhookChange;

internal sealed record MessagesChange(string PhoneNumberId, IReadOnlyList<InboundMessage> Messages, IReadOnlyList<StatusUpdate> Statuses) : WebhookChange;

internal sealed record AccountUpdateChange(string WabaId, string Event, string? BanState) : WebhookChange;

internal sealed record UnknownChange(string Field) : WebhookChange;

internal sealed record InboundMedia(string MetaMediaId, string MimeType, string? Sha256, string? FileName);

/// <summary>Ya con el <c>kind</c> del mapa de §8.7 y el texto/leyenda/detalles separados como los guarda §7.3.</summary>
internal sealed record InboundMessage(
    string Wamid,
    string WaId,
    string? ProfileName,
    DateTimeOffset OccurredAt,
    MessageKind Kind,
    string? Text,
    string? Caption,
    string? DetailsJson,
    InboundMedia? Media);

internal sealed record StatusUpdate(
    string Wamid,
    MessageStatus Status,
    DateTimeOffset OccurredAt,
    Guid? CallbackMessageId,
    int? ErrorCode,
    string? ErrorTitle);

/// <summary>Spec §8.7, «Mapa de tipos de Meta a kind»; <c>button</c> → Interactive (D-M6); todo lo demás Unsupported.</summary>
internal static class MessageKindMap
{
    public static MessageKind FromMetaType(string? type) => type switch
    {
        "text" => MessageKind.Text,
        "image" => MessageKind.Image,
        "video" => MessageKind.Video,
        "audio" => MessageKind.Audio,
        "document" => MessageKind.Document,
        "sticker" => MessageKind.Sticker,
        "location" => MessageKind.Location,
        "contacts" => MessageKind.Contacts,
        "reaction" => MessageKind.Reaction,
        "interactive" or "button" => MessageKind.Interactive,
        _ => MessageKind.Unsupported,
    };

    public static bool HasMedia(MessageKind kind) =>
        kind is MessageKind.Image or MessageKind.Video or MessageKind.Audio or MessageKind.Document or MessageKind.Sticker;
}
```

`WebhookPayloadParser.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Del JSON de Meta a <see cref="WebhookChange"/>. Tolerante a propósito (Review Focus 1): lo que no tiene
/// la forma documentada se salta y lo desconocido cae en <c>Unsupported</c>; nunca lanza. El cuerpo
/// ya pasó la firma, pero eso no lo hace bien formado.
/// </summary>
internal static class WebhookPayloadParser
{
    public const string CallbackPrefix = "qep:";

    public static IReadOnlyList<WebhookChange> Parse(string json)
    {
        var changes = new List<WebhookChange>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return changes;
        }

        using (document)
        {
            if (!TryArray(document.RootElement, "entry", out var entries))
            {
                return changes;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var wabaId = ReadString(entry, "id");
                if (!TryArray(entry, "changes", out var entryChanges))
                {
                    continue;
                }

                foreach (var change in entryChanges.EnumerateArray())
                {
                    var field = ReadString(change, "field") ?? string.Empty;
                    var hasValue = change.ValueKind == JsonValueKind.Object && change.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object;
                    changes.Add(field switch
                    {
                        "messages" => ParseMessages(hasValue ? value : default),
                        "account_update" => ParseAccountUpdate(wabaId ?? string.Empty, hasValue ? value : default),
                        _ => new UnknownChange(field),
                    });
                }
            }
        }

        return changes;
    }

    private static MessagesChange ParseMessages(JsonElement value)
    {
        var phoneNumberId = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("metadata", out var metadata)
            ? ReadString(metadata, "phone_number_id") ?? string.Empty
            : string.Empty;
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (TryArray(value, "contacts", out var contacts))
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                if (ReadString(contact, "wa_id") is { } waId)
                {
                    names[waId] = contact.TryGetProperty("profile", out var profile) ? ReadString(profile, "name") : null;
                }
            }
        }

        var messages = new List<InboundMessage>();
        if (TryArray(value, "messages", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ParseMessage(item, names) is { } message)
                {
                    messages.Add(message);
                }
            }
        }

        var statuses = new List<StatusUpdate>();
        if (TryArray(value, "statuses", out var statusItems))
        {
            foreach (var item in statusItems.EnumerateArray())
            {
                if (ParseStatus(item) is { } status)
                {
                    statuses.Add(status);
                }
            }
        }

        return new MessagesChange(phoneNumberId, messages, statuses);
    }

    private static InboundMessage? ParseMessage(JsonElement item, Dictionary<string, string?> names)
    {
        var wamid = ReadString(item, "id");
        var from = ReadString(item, "from");
        if (wamid is null || from is null || !from.All(char.IsAsciiDigit) || ReadTimestamp(item) is not { } occurredAt)
        {
            return null;
        }

        var type = ReadString(item, "type");
        var kind = MessageKindMap.FromMetaType(type);
        var profileName = names.GetValueOrDefault(from);
        string? text = null;
        string? caption = null;
        string? details = null;
        InboundMedia? media = null;

        if (MessageKindMap.HasMedia(kind) && type is not null && item.TryGetProperty(type, out var mediaElement) && mediaElement.ValueKind == JsonValueKind.Object)
        {
            caption = Truncate(ReadString(mediaElement, "caption"), 1024);
            var mediaId = ReadString(mediaElement, "id");
            if (mediaId is not null)
            {
                media = new InboundMedia(mediaId, ReadString(mediaElement, "mime_type") ?? "application/octet-stream", ReadString(mediaElement, "sha256"), ReadString(mediaElement, "filename"));
            }
        }
        else
        {
            switch (kind)
            {
                case MessageKind.Text:
                    text = item.TryGetProperty("text", out var textElement) ? ReadString(textElement, "body") : null;
                    break;
                case MessageKind.Location when item.TryGetProperty("location", out var location):
                    details = location.GetRawText();
                    break;
                case MessageKind.Contacts when TryArray(item, "contacts", out var contactList):
                    text = string.Join(", ", contactList.EnumerateArray()
                        .Select(contact => contact.TryGetProperty("name", out var name) ? ReadString(name, "formatted_name") : null)
                        .Where(name => !string.IsNullOrWhiteSpace(name)));
                    details = contactList.GetRawText();
                    break;
                case MessageKind.Reaction when item.TryGetProperty("reaction", out var reaction):
                    text = ReadString(reaction, "emoji");
                    details = reaction.GetRawText();
                    break;
                case MessageKind.Interactive when type == "interactive" && item.TryGetProperty("interactive", out var interactive):
                    text = interactive.TryGetProperty("button_reply", out var buttonReply) ? ReadString(buttonReply, "title")
                        : interactive.TryGetProperty("list_reply", out var listReply) ? ReadString(listReply, "title")
                        : null;
                    details = interactive.GetRawText();
                    break;
                case MessageKind.Interactive when type == "button" && item.TryGetProperty("button", out var button):
                    text = ReadString(button, "text");
                    details = button.GetRawText();
                    break;
                case MessageKind.Unsupported:
                    details = JsonSerializer.Serialize(new
                    {
                        type,
                        errors = item.TryGetProperty("errors", out var errors) ? JsonSerializer.Deserialize<JsonElement>(errors.GetRawText()) : (JsonElement?)null,
                    });
                    break;
                default:
                    break;
            }
        }

        return new InboundMessage(wamid, from, Truncate(profileName, 256), occurredAt, kind, text, caption, details, media);
    }

    private static StatusUpdate? ParseStatus(JsonElement item)
    {
        var wamid = ReadString(item, "id");
        var status = ReadString(item, "status") switch
        {
            "sent" => MessageStatus.Sent,
            "delivered" => MessageStatus.Delivered,
            "read" or "played" => MessageStatus.Read, // D-M9
            "failed" => MessageStatus.Failed,
            _ => (MessageStatus?)null,
        };
        if (wamid is null || status is null || ReadTimestamp(item) is not { } occurredAt)
        {
            return null;
        }

        Guid? callback = null;
        if (ReadString(item, "biz_opaque_callback_data") is { } data && data.StartsWith(CallbackPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(data.AsSpan(CallbackPrefix.Length), "D", out var parsed))
        {
            callback = parsed;
        }

        int? errorCode = null;
        string? errorTitle = null;
        if (TryArray(item, "errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            errorCode = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsedCode) ? parsedCode : null;
            errorTitle = ReadString(first, "title");
        }

        return new StatusUpdate(wamid, status.Value, occurredAt, callback, errorCode, errorTitle);
    }

    private static AccountUpdateChange ParseAccountUpdate(string wabaId, JsonElement value)
    {
        var @event = value.ValueKind == JsonValueKind.Object ? ReadString(value, "event") ?? string.Empty : string.Empty;
        var banState = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("ban_info", out var ban) ? ReadString(ban, "waba_ban_state") : null;
        return new AccountUpdateChange(wabaId, @event, banState);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement item)
    {
        if (!item.TryGetProperty("timestamp", out var timestamp))
        {
            return null;
        }

        var seconds = timestamp.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(timestamp.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when timestamp.TryGetInt64(out var number) => number,
            _ => (long?)null,
        };
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }

    private static bool TryArray(JsonElement element, string property, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
```

- [ ] **Step 3: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WebhookPayloadParserTests"
```

Esperado: todas correctas.

- [ ] **Step 4: Formato y commit**

```text
feat(messaging): parser tolerante del webhook de Meta con el mapa de tipos a kind
```

---

### Task 13: Ingesta de entrantes — rutas, worker de entregas y SQL atómico

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingConnectionDirectory.cs`
- Create: `src/Bootstrapper/MessagingConnectionDirectory.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookRouting.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryWorker.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (adaptador)
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundIngestionTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WebhookLoadTests.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs` (`DrainDeliveriesAsync`)

**Interfaces:**
- Consumes: `IConnectionRoutes`, `IIntegrationConnections`, `IConnectionHealthReporter` (Integrations), `ITenantModules`, `WebhookPayloadParser`, `MessagingDbContext`, `MessagingWorkerOptions`.
- Produces: `IMessagingConnectionDirectory`, `MessagingRoute`, `MessagingSender`, `WebhookRouting` (`FindRouteAsync`, `IsModuleEnabledAsync` con caché 60 s), `InboundIngestion.IngestAsync(MessagingDbContext, Guid tenantId, Guid connectionId, InboundMessage, DateTimeOffset now, ct) → Guid? messageId`, `WebhookDeliveryProcessor.ProcessAsync(long deliveryId, string payload, int attempts, ct) → DeliveryOutcome { Processed, RetryLater }`, `WebhookDeliveryWorker.DrainAsync(ct)`, `WebhookDeliveryWorker.Leases`.

- [ ] **Step 1: Puerto y adaptador**

`src/Modules/Messaging/Modules.Messaging.Application/IMessagingConnectionDirectory.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>Una ruta resuelta cross-tenant (spec §6.4): tenant, conexión y su estado por nombre
/// (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>). El texto y no un enum: Messaging no referencia Integrations.</summary>
public sealed record MessagingRoute(Guid TenantId, Guid ConnectionId, string Status)
{
    public bool IsActive => Status == "Active";

    public bool IsPaused => Status == "Paused";
}

/// <summary>Con qué se envía: el <c>phone_number_id</c> y el token en claro para ese request. <see cref="ToString"/> no lo imprime.</summary>
public sealed record MessagingSender(string PhoneNumberId, string AccessToken)
{
    public override string ToString() => $"MessagingSender {{ PhoneNumberId = {PhoneNumberId} }}";
}

/// <summary>
/// Spec 2026-10-09 §6.4: lo que Messaging necesita de Integrations, con adaptador en Bootstrapper sobre
/// <c>IConnectionRoutes</c>, <c>IIntegrationConnections</c> e <c>IConnectionHealthReporter</c>. La caché
/// de 60 s de las rutas vive del lado de Messaging (<c>WebhookRouting</c>).
/// </summary>
public interface IMessagingConnectionDirectory
{
    /// <summary>Cross-tenant: el webhook no tiene tenant hasta acá. <c>null</c> = número desconocido o borrado.</summary>
    Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken);

    /// <summary>Las conexiones de una WABA (<c>account_update</c> llega por cuenta, D-M2).</summary>
    Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken);

    /// <summary>Sólo <c>Active</c> y visible, con el token en claro; <c>null</c> si no.</summary>
    Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Nombre de cada conexión whatsapp-cloud del tenant, Active o no.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary><c>Active → NeedsAttention</c> con el código (<c>token_expired</c>, <c>number_unregistered</c>, <c>account_disabled</c>).</summary>
    Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken);
}
```

`src/Bootstrapper/MessagingConnectionDirectory.cs`:

```csharp
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-09 §6.4: el único punto donde Messaging e Integrations se tocan, y es acá a propósito.</summary>
internal sealed class MessagingConnectionDirectory(
    IConnectionRoutes routes,
    IIntegrationConnections connections,
    IConnectionHealthReporter healthReporter) : IMessagingConnectionDirectory
{
    private static string Provider => IntegrationProviders.WhatsAppCloud.Key;

    public async Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        await routes.FindAsync(Provider, phoneNumberId, cancellationToken) is { } route ? ToRoute(route) : null;

    public async Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken) =>
        (await routes.FindByAccountAsync(Provider, wabaId, cancellationToken)).Select(ToRoute).ToArray();

    public async Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolved = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        if (resolved is null || !string.Equals(resolved.ProviderKey, Provider, StringComparison.Ordinal))
        {
            return null;
        }

        return resolved.Fields.TryGetValue(WhatsAppCloudFieldKeys.PhoneNumberId, out var phoneNumberId)
            && resolved.Secrets.TryGetValue(WhatsAppCloudFieldKeys.AccessToken, out var token)
                ? new MessagingSender(phoneNumberId, token)
                : null;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await connections.ListByProviderAsync(tenantId, Provider, cancellationToken))
            .ToDictionary(listing => listing.Id, listing => listing.Name);

    public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
        healthReporter.ReportCredentialsRejectedAsync(tenantId, connectionId, failureCode, cancellationToken);

    private static MessagingRoute ToRoute(ConnectionRoute route) => new(route.TenantId, route.ConnectionId, route.Status.ToString());
}
```

`QepServiceCollectionExtensions.cs`, junto a los adaptadores (después de `IConnectionAuthorNames`):

```csharp
        // Messaging (spec 2026-10-09 §6.4): conexiones de WhatsApp por Integrations. El único punto donde
        // los dos módulos se tocan.
        services.AddScoped<IMessagingConnectionDirectory, MessagingConnectionDirectory>();
```

- [ ] **Step 2: Pruebas RED de ingesta**

En `MessagingApiHarness` agrega:

```csharp
    /// <summary>Corre una pasada del worker de entregas (P10): reclama y procesa lo pendiente.</summary>
    public static async Task DrainDeliveriesAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Modules.Messaging.Infrastructure.Webhook.WebhookDeliveryWorker>().Single();
        await worker.DrainAsync(TestContext.Current.CancellationToken);
    }

    public static Task<long> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<long>(connectionString, sql, parameters);
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundIngestionTests.cs`:

```csharp
using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §7.5 y §8.2: la ingesta crea la conversación, reabre una resuelta, sube el
/// contador, deduplica por wamid, no reabre ni suma con un reenvío viejo, deja la foto del último mensaje
/// aunque lleguen fuera de orden, descarta con Paused, módulo apagado y ruta desconocida, y no se traba
/// con una entrega rota.</summary>
public sealed class InboundIngestionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string phoneNumberId = "111", bool enableModule = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        if (enableModule)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", phoneNumberId, "222");
        return new Fixture(factory, connectionString, tenant.TenantId, connectionId, factory.CreateClient());
    }

    [Fact]
    public async Task AnInboundTextCreatesTheConversationAndTheMessageOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var json = MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "¿Tienen disponible?");

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Client, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
        // Reenvío con otra hora (bytes distintos): otra entrega, mismo wamid.
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000999, "¿Tienen disponible?"))).StatusCode);
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
        var row = await ScalarAsync<string>(f.ConnectionString,
            "SELECT tenant_id::text || '|' || wa_id || '|' || profile_name || '|' || status || '|' || unread_count || '|' || last_message_preview || '|' || last_message_direction || '|' || last_message_status || '|' || version || '|' || extract(epoch from last_inbound_at)::bigint || '|' || last_inbound_wamid FROM messaging.conversations");
        Assert.Equal($"{f.TenantId}|573001234567|Laura|Open|1|¿Tienen disponible?|1|2|2|1760000000|wamid.1", row);
        Assert.Equal($"{f.TenantId}|1|1|2", await ScalarAsync<string>(f.ConnectionString, "SELECT tenant_id::text || '|' || direction || '|' || kind || '|' || status FROM messaging.messages"));
    }

    [Fact]
    public async Task ANewInboundReopensAResolvedConversationButAnOldResendDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "hola"));
        await DrainDeliveriesAsync(f.Factory);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved', unread_count = 0, version = version + 1");

        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000001, "hola"));
        await DrainDeliveriesAsync(f.Factory);
        Assert.Equal("Resolved|0", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || unread_count FROM messaging.conversations"));

        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.2", 1760000100, "otra"));
        await DrainDeliveriesAsync(f.Factory);
        Assert.Equal("Open|1|otra", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || unread_count || '|' || last_message_preview FROM messaging.conversations"));
    }

    // Meta puede reenviar tarde: un mensaje más viejo no pisa la foto del último.
    [Fact]
    public async Task OutOfOrderMessagesKeepTheNewestSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.new", 1760000200, "nuevo"));
        await DrainDeliveriesAsync(f.Factory);
        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.old", 1760000100, "viejo"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("nuevo|2|1760000200|wamid.new", await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_message_preview || '|' || unread_count || '|' || extract(epoch from last_activity_at)::bigint || '|' || last_inbound_wamid FROM messaging.conversations"));
    }

    [Fact]
    public async Task AnImageCreatesTheMediaRowWithTheCaptionOnTheMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostWebhookAsync(f.Client, MetaPayloads.InboundMedia("111", "573001234567", "wamid.img", 1760000000, "image", "media-1", "image/jpeg", caption: "la foto"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("2|la foto|", await ScalarAsync<string>(f.ConnectionString, "SELECT kind || '|' || caption || '|' || coalesce(text, '') FROM messaging.messages"));
        Assert.Equal("media-1|image/jpeg|0", await ScalarAsync<string>(f.ConnectionString, "SELECT meta_media_id || '|' || mime_type || '|' || attempts FROM messaging.message_media WHERE stored_at IS NULL"));
        Assert.Equal("la foto", await ScalarAsync<string>(f.ConnectionString, "SELECT last_message_preview FROM messaging.conversations"));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("module-off")]
    [InlineData("unknown-route")]
    public async Task PausedModuleOffAndUnknownRoutesDiscardTheInboundAndFinishTheDelivery(string scenario)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, enableModule: scenario != "module-off");
        using var _ = f.Factory;
        if (scenario == "paused")
        {
            await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        }

        var phoneNumberId = scenario == "unknown-route" ? "000" : "111";
        await PostWebhookAsync(f.Client, MetaPayloads.InboundText(phoneNumberId, "573001234567", "wamid.1", 1760000000, "hola"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    // Review Focus 1.
    [Theory]
    [InlineData("""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"messages":[{"from":"573001234567","id":"w","timestamp":"abc","type":"text","text":{"body":"x"}}]}}]}]}""")]
    [InlineData("""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"history","value":{}}]}]}""")]
    [InlineData("""{"object":"whatsapp_business_account"}""")]
    public async Task AMalformedOrUnknownDeliveryIsProcessedWithoutRetries(string json)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostWebhookAsync(f.Client, json);
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("1|t", await ScalarAsync<string>(f.ConnectionString, "SELECT attempts || '|' || (processed_at IS NOT NULL)::text FROM messaging.webhook_deliveries"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WebhookLoadTests.cs`:

```csharp
using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §12, «Carga»: N POSTs firmados concurrentes con wamids repetidos → cero 429,
/// un mensaje por wamid, unreadCount exacto, una conversación por waId.</summary>
public sealed class WebhookLoadTests
{
    [Fact]
    public async Task ConcurrentSignedPostsWithRepeatedWamidsNeverGet429AndCountExactly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var client = factory.CreateClient();
        const int Persons = 5;
        const int MessagesPerPerson = 20;
        const int Resends = 3;

        var posts = new List<Task<HttpResponseMessage>>();
        for (var person = 0; person < Persons; person++)
        {
            for (var index = 0; index < MessagesPerPerson; index++)
            {
                for (var resend = 0; resend < Resends; resend++)
                {
                    // Cada reenvío cambia la hora: bytes distintos, mismo wamid.
                    var json = MetaPayloads.InboundText("111", $"57300000000{person}", $"wamid.{person}.{index}", 1760000000 + index * 10 + resend, $"mensaje {index}");
                    posts.Add(PostWebhookAsync(client, json));
                }
            }
        }

        var responses = await Task.WhenAll(posts);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);

        // Varias pasadas del worker en paralelo: el reclamo deja un solo ganador por entrega.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DrainDeliveriesAsync(factory)));
        while (await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL") > 0)
        {
            await DrainDeliveriesAsync(factory);
        }

        Assert.Equal((long)Persons, await CountAsync(connectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal((long)(Persons * MessagesPerPerson), await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal((long)(Persons * MessagesPerPerson), await CountAsync(connectionString, "SELECT sum(unread_count) FROM messaging.conversations"));
        Assert.Equal((long)MessagesPerPerson, await CountAsync(connectionString, "SELECT min(unread_count) FROM messaging.conversations"));
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~InboundIngestionTests"
```

Esperado: `error CS0246 … 'WebhookDeliveryWorker'`.

- [ ] **Step 3: Rutas con caché, ingesta atómica, procesador y worker**

`WebhookRouting.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>§8.2: ruta por <c>phone_number_id</c> y módulo del tenant, con caché por pod de 60 s (D-M18).
/// Pausar o borrar tarda hasta un minuto en reflejarse; aceptado (§13).</summary>
internal sealed class WebhookRouting(IMessagingConnectionDirectory directory, ITenantModules tenantModules, IMemoryCache cache)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync($"messaging:route:{phoneNumberId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return directory.FindRouteAsync(phoneNumberId, cancellationToken);
        });

    public Task<bool> IsModuleEnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync($"messaging:module:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var set = await tenantModules.FindAsync(tenantId, cancellationToken);
            // null = el stub: no bloquea, como TenantModuleGuard.
            return set is null || set.IsEnabled(TenantModuleKeys.Messaging);
        });
}
```

(`services.AddMemoryCache()` en el registro.)

`InboundIngestion.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-09 §7.5, literal: tres sentencias en una transacción, sin pasar por el agregado,
/// para que dos pods sobre la misma conversación sumen bien sin reintentos.</summary>
internal static class InboundIngestion
{
    public const int PreviewMaxLength = Conversation.PreviewMaxLength;

    /// <returns>El id del mensaje insertado, o <c>null</c> si Meta lo reenvió (ya estaba).</returns>
    public static async Task<Guid?> IngestAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 1. La conversación. DO NOTHING + SELECT: un DO UPDATE reabriría una resuelta con un reenvío viejo.
        var newConversationId = Guid.CreateVersion7();
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, wa_id, profile_name, status, unread_count, last_activity_at, created_at, updated_at, version)
            VALUES ({newConversationId}, {tenantId}, {connectionId}, {message.WaId}, {message.ProfileName}, 'Open', 0, {message.OccurredAt}, {now}, {now}, 1)
            ON CONFLICT (connection_id, wa_id) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        var conversationId = inserted.Count == 1
            ? inserted[0]
            : await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND wa_id = {message.WaId}""").SingleAsync(cancellationToken);

        // 2. El mensaje; un reenvío no inserta nada.
        var messageId = Guid.CreateVersion7();
        var kind = MessageColumnCodes.ToCode(message.Kind);
        var insertedMessage = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, caption, details, wamid, created_at)
            VALUES ({messageId}, {conversationId}, {tenantId}, {connectionId}, {message.OccurredAt}, 1, {kind}, 2, {message.Text}, {message.Caption}, {message.DetailsJson}::jsonb, {message.Wamid}, {now})
            ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (insertedMessage.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // 3. Contadores, reapertura, ventana y foto. Las expresiones del SET leen los valores viejos.
        var preview = Preview(message);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations SET
                unread_count       = unread_count + 1,
                status             = 'Open',
                profile_name       = COALESCE({message.ProfileName}, profile_name),
                last_inbound_wamid = CASE WHEN last_inbound_at IS NULL OR {message.OccurredAt} >= last_inbound_at THEN {message.Wamid} ELSE last_inbound_wamid END,
                last_inbound_at    = GREATEST(last_inbound_at, {message.OccurredAt}),
                last_activity_at   = GREATEST(last_activity_at, {message.OccurredAt}),
                last_message_id        = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN {messageId} ELSE last_message_id END,
                last_message_direction = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN 1 ELSE last_message_direction END,
                last_message_kind      = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN {kind} ELSE last_message_kind END,
                last_message_preview   = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN {preview} ELSE last_message_preview END,
                last_message_status    = CASE WHEN last_message_at IS NULL OR {message.OccurredAt} >= last_message_at THEN 2 ELSE last_message_status END,
                last_message_at        = GREATEST(last_message_at, {message.OccurredAt}),
                updated_at         = {now},
                version            = version + 1
            WHERE id = {conversationId}
            """, cancellationToken);

        if (message.Media is { } media)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO messaging.message_media (message_id, mime_type, file_name, meta_media_id, sha256, attempts, next_attempt_at)
                VALUES ({messageId}, {media.MimeType}, {media.FileName}, {media.MetaMediaId}, {media.Sha256}, 0, {now})
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return messageId;
    }

    /// <summary>§8.7: el texto, o la leyenda de un medio; <c>null</c> si no hay. Recortado al ancho de la columna.</summary>
    public static string? Preview(InboundMessage message)
    {
        var source = message.Text ?? message.Caption;
        return source is null ? null : source.Length <= PreviewMaxLength ? source : source[..PreviewMaxLength];
    }
}
```

`WebhookDeliveryProcessor.cs` (sin `statuses` ni `account_update` todavía: la Task 13b los suma):

```csharp
using Microsoft.Extensions.Logging;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

internal enum DeliveryOutcome
{
    Processed,
    RetryLater,
}

/// <summary>§8.2: recorre <c>entry[].changes[]</c>; cada change es idempotente, así que reprocesar una
/// entrega a medias no duplica nada. Nunca registra el contenido de un mensaje.</summary>
internal sealed partial class WebhookDeliveryProcessor(
    MessagingDbContext dbContext,
    WebhookRouting routing,
    BuildingBlocks.Application.IClock clock,
    ILogger<WebhookDeliveryProcessor> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: {Count} inbound message(s) discarded for phone number id {PhoneNumberId} ({Reason}).")]
    private static partial void LogDiscarded(ILogger logger, long deliveryId, int count, string phoneNumberId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: field '{Field}' is not handled and was ignored.")]
    private static partial void LogIgnoredField(ILogger logger, long deliveryId, string field);

    public async Task<DeliveryOutcome> ProcessAsync(long deliveryId, string payload, int attempts, CancellationToken cancellationToken)
    {
        var outcome = DeliveryOutcome.Processed;
        foreach (var change in WebhookPayloadParser.Parse(payload))
        {
            switch (change)
            {
                case MessagesChange messages:
                    if (await ProcessMessagesAsync(deliveryId, messages, attempts, cancellationToken) == DeliveryOutcome.RetryLater)
                    {
                        outcome = DeliveryOutcome.RetryLater;
                    }

                    break;
                case AccountUpdateChange account:
                    await ProcessAccountUpdateAsync(deliveryId, account, cancellationToken);
                    break;
                case UnknownChange unknown:
                    LogIgnoredField(logger, deliveryId, unknown.Field);
                    break;
                default:
                    break;
            }
        }

        return outcome;
    }

    private async Task<DeliveryOutcome> ProcessMessagesAsync(long deliveryId, MessagesChange change, int attempts, CancellationToken cancellationToken)
    {
        var route = await routing.FindRouteAsync(change.PhoneNumberId, cancellationToken);
        if (route is null)
        {
            LogDiscarded(logger, deliveryId, change.Messages.Count, change.PhoneNumberId, "unknown-route");
            return DeliveryOutcome.Processed;
        }

        var accepting = !route.IsPaused && await routing.IsModuleEnabledAsync(route.TenantId, cancellationToken);
        if (!accepting && change.Messages.Count > 0)
        {
            LogDiscarded(logger, deliveryId, change.Messages.Count, change.PhoneNumberId, route.IsPaused ? "paused" : "module-off");
        }

        if (accepting)
        {
            var now = clock.UtcNow;
            foreach (var message in change.Messages)
            {
                await InboundIngestion.IngestAsync(dbContext, route.TenantId, route.ConnectionId, message, now, cancellationToken);
            }
        }

        // Los statuses se aplican siempre, también con Paused o módulo apagado (decisión 7, D-M18). Task 13b.
        return await ProcessStatusesAsync(deliveryId, route, change, attempts, cancellationToken);
    }

    private Task<DeliveryOutcome> ProcessStatusesAsync(long deliveryId, Modules.Messaging.Application.MessagingRoute route, MessagesChange change, int attempts, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryOutcome.Processed);

    private Task ProcessAccountUpdateAsync(long deliveryId, AccountUpdateChange change, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
```

`WebhookDeliveryWorker.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Spec 2026-10-09 §8.2: toma un lote de pendientes, reclama cada entrega con una sentencia que se
/// commitea sola (como <c>IdentityInboxClaims</c>, P9) y la procesa en su propio scope. Varios pods
/// pueden correrlo a la vez: el reclamo da un solo ganador. Un fallo deja el reclamo vivo y vuelve al
/// vencer el lease; a los <see cref="MaxAttempts"/> se marca procesada con <c>last_error</c>.
/// </summary>
internal sealed partial class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    internal const int BatchSize = 50;

    /// <summary>§8.2: N = 8 intentos (≈ 1 h) para un status cuyo wamid todavía no está.</summary>
    internal const int MaxAttempts = 8;

    /// <summary>P15: la espera después del intento n es <c>Leases[n - 1]</c>; desde el último, el último.</summary>
    internal static readonly IReadOnlyList<TimeSpan> Leases =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(20),
    ];

    public static TimeSpan LeaseFor(int attempt) => Leases[Math.Min(attempt, Leases.Count) - 1];

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} failed on attempt {Attempt}; it is retried when its lease expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, long deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} gave up after {Attempt} attempts: {Reason}.")]
    private static partial void LogGaveUp(ILogger logger, long deliveryId, int attempt, string reason);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.DeliveryPollSeconds)));
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Una pasada: internal para las pruebas (P10).</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        List<long> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await dbContext.Deliveries.AsNoTracking()
                .Where(delivery => delivery.ProcessedAt == null && (delivery.ClaimedUntil == null || delivery.ClaimedUntil < now))
                .OrderBy(delivery => delivery.Id)
                .Select(delivery => delivery.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in pending)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<MessagingDbContext>();
            var clock = services.GetRequiredService<IClock>();
            var claimed = await TryClaimAsync(dbContext, id, clock.UtcNow, cancellationToken);
            if (claimed is null)
            {
                continue; // Otra réplica lo tiene, o ya terminó desde que se armó el lote.
            }

            var (attempts, payload) = claimed.Value;
            try
            {
                var outcome = await services.GetRequiredService<WebhookDeliveryProcessor>().ProcessAsync(id, payload, attempts, cancellationToken);
                if (outcome == DeliveryOutcome.Processed)
                {
                    await MarkProcessedAsync(dbContext, id, clock.UtcNow, null, cancellationToken);
                }
                else if (attempts >= MaxAttempts)
                {
                    LogGaveUp(logger, id, attempts, "status-before-wamid");
                    await MarkProcessedAsync(dbContext, id, clock.UtcNow, "status-before-wamid: gave up after 8 attempts", cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El reclamo queda vivo: vuelve al vencer el lease.
                LogAttemptFailed(logger, exception, id, attempts);
                if (attempts >= MaxAttempts)
                {
                    LogGaveUp(logger, id, attempts, exception.GetType().Name);
                    await MarkProcessedAsync(dbContext, id, clock.UtcNow, Truncate(exception.GetType().Name + ": " + exception.Message, 512), cancellationToken);
                }
            }
        }
    }

    /// <summary>P9: la regla de IdentityInboxClaims sobre webhook_deliveries, con leases por intento.</summary>
    private static async Task<(int Attempts, string Payload)?> TryClaimAsync(MessagingDbContext dbContext, long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leases = Leases.ToArray();
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE messaging.webhook_deliveries
               SET claimed_until = {now} + ({leases})[LEAST(attempts + 1, cardinality({leases}))],
                   attempts = attempts + 1
             WHERE id = {id} AND processed_at IS NULL AND (claimed_until IS NULL OR claimed_until < {now})
            RETURNING attempts AS "Attempts", payload::text AS "Payload"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? (rows[0].Attempts, rows[0].Payload) : null;
    }

    private static Task<int> MarkProcessedAsync(MessagingDbContext dbContext, long id, DateTimeOffset now, string? lastError, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.webhook_deliveries SET processed_at = {now}, last_error = {lastError} WHERE id = {id}", cancellationToken);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record ClaimRow(int Attempts, string Payload);
}
```

(Si `SqlQuery<ClaimRow>` exige propiedades con setter, declara `ClaimRow` como clase con `{ get; set; }`.)

`MessagingInfrastructureExtensions.cs`:

```csharp
        services.AddMemoryCache();
        services.AddScoped<WebhookRouting>();
        services.AddScoped<WebhookDeliveryProcessor>();
        services.AddHostedService<WebhookDeliveryWorker>();
```

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~WebhookLoadTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: todas correctas. Si la prueba de carga supera los 100 s del timeout por defecto del `HttpClient` del `TestServer`, baja `MessagesPerPerson` a 10; no subas el timeout.

- [ ] **Step 5: Formato y commit**

```text
feat(messaging): ingesta atómica de entrantes con worker de entregas por reclamo y rutas en caché
```

---

### Task 13b: Estados monótonos, `account_update` y purga

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/StatusIngestion.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPurgeWorker.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs` (`ProcessStatusesAsync`, `ProcessAccountUpdateAsync`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/StatusIngestionTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AccountUpdateTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WebhookPurgeTests.cs`
- Modify: `MessagingApiHarness.cs` (`SeedOutboundAsync`, `DrainPurgeAsync`)

**Interfaces:**
- Consumes: `IMessagingConnectionDirectory.FindByAccountAsync/ReportRejectedAsync`, `ConnectionFailureCodes.AccountDisabled` (como literal `"account_disabled"`: Messaging no referencia Integrations).
- Produces: `StatusIngestion.ApplyAsync(MessagingDbContext, Guid connectionId, StatusUpdate, ct) → StatusOutcome { Applied, NoChange, NotFound }`, `WebhookPurgeWorker.DrainAsync(ct)`.

- [ ] **Step 1: Pruebas RED**

En el harness:

```csharp
    /// <summary>Un saliente ya guardado, como lo deja el envío (§8.3): Sent con wamid, o Failed -1 sin wamid.</summary>
    public static async Task<Guid> SeedOutboundAsync(
        string connectionString, Guid conversationId, Guid tenantId, Guid connectionId, string? wamid, short status = 1, int? failureCode = null, long occurredAtUnix = 1760000000)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, client_id, failure_code, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, to_timestamp(@occurredAt), 2, 1, @status, 'respuesta', @wamid, @clientId, @failureCode, now())
            """,
            ("id", id), ("conversationId", conversationId), ("tenantId", tenantId), ("connectionId", connectionId),
            ("occurredAt", occurredAtUnix), ("status", status), ("wamid", (object?)wamid ?? DBNull.Value), ("clientId", Guid.CreateVersion7()),
            ("failureCode", (object?)failureCode ?? DBNull.Value));
        await ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET last_message_id = @id, last_message_direction = 2, last_message_kind = 1, last_message_status = @status, last_message_at = to_timestamp(@occurredAt), last_activity_at = to_timestamp(@occurredAt) WHERE id = @conversationId",
            ("id", id), ("status", status), ("occurredAt", occurredAtUnix), ("conversationId", conversationId));
        return id;
    }

    public static async Task DrainPurgeAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Modules.Messaging.Infrastructure.Webhook.WebhookPurgeWorker>().Single();
        await worker.DrainAsync(TestContext.Current.CancellationToken);
    }
```

`StatusIngestionTests.cs`:

```csharp
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §7.5 («Estados») y §8.2 («La carrera del sent antes que el wamid»), y Review
/// Focus 4: monotonía, Failed sólo sobre Sent, played → Read, -1 corregido por cualquier estado real,
/// callback que llega antes del wamid (reintento y éxito), status ajeno descartado en el acto, y la foto
/// del último mensaje sin subir la versión.</summary>
public sealed class StatusIngestionTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        return new Fixture(factory, connectionString, tenant.TenantId, connectionId, conversationId, factory.CreateClient());
    }

    private static async Task ApplyAsync(Fixture f, string wamid, string status, long timestamp, string? callback = null, int? errorCode = null)
    {
        await PostWebhookAsync(f.Client, MetaPayloads.Status("111", wamid, status, timestamp, callback, errorCode, errorCode is null ? null : "Some title"));
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<string> StateAsync(Fixture f, Guid messageId) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT m.status || '|' || coalesce(m.failure_code::text, '-') || '|' || coalesce(m.wamid, '-') || '|' || c.last_message_status || '|' || c.version FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.id = @id",
            ("id", messageId));

    [Fact]
    public async Task StatusesNeverGoBackwardsAndFailedOnlyOverridesSent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.out");

        await ApplyAsync(f, "wamid.out", "read", 1760000300);
        Assert.Equal("3|-|wamid.out|3|1", await StateAsync(f, id));
        await ApplyAsync(f, "wamid.out", "delivered", 1760000200);
        Assert.Equal("3|-|wamid.out|3|1", await StateAsync(f, id));
        await ApplyAsync(f, "wamid.out", "failed", 1760000400, errorCode: 131026);
        Assert.Equal("3|-|wamid.out|3|1", await StateAsync(f, id));

        var sent = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.two", occurredAtUnix: 1760000500);
        await ApplyAsync(f, "wamid.two", "failed", 1760000600, errorCode: 131047);
        Assert.Equal("4|131047|wamid.two|4|1", await StateAsync(f, sent));
        Assert.Equal("Some title", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_title FROM messaging.messages WHERE id = @id", ("id", sent)));
    }

    [Fact]
    public async Task PlayedIsReadAndAnUnconfirmedSendIsCorrectedByAnyRealStatus()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var unconfirmed = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.u", status: 4, failureCode: -1);

        await ApplyAsync(f, "wamid.u", "sent", 1760000100);
        Assert.Equal("1|-|wamid.u|1|1", await StateAsync(f, unconfirmed));
        await ApplyAsync(f, "wamid.u", "played", 1760000200);
        Assert.Equal("3|-|wamid.u|3|1", await StateAsync(f, unconfirmed));
    }

    [Fact]
    public async Task ACallbackThatArrivesBeforeTheWamidIsRetriedAndThenApplied()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var messageId = Guid.CreateVersion7();

        await ApplyAsync(f, "wamid.late", "sent", 1760000100, callback: $"qep:{messageId}");
        Assert.Equal("1|f", await ScalarAsync<string>(f.ConnectionString, "SELECT attempts || '|' || (processed_at IS NOT NULL)::text FROM messaging.webhook_deliveries"));

        // El envío commitea después (§8.3): la fila aparece con ese id y sin wamid.
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, now(), 2, 1, 1, 'x', now())
            """, ("id", messageId), ("conversationId", f.ConversationId), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.webhook_deliveries SET claimed_until = now() - interval '1 minute'");
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("1|wamid.late", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || wamid FROM messaging.messages WHERE id = @id", ("id", messageId)));
        Assert.Equal("2|t", await ScalarAsync<string>(f.ConnectionString, "SELECT attempts || '|' || (processed_at IS NOT NULL)::text FROM messaging.webhook_deliveries"));
    }

    [Fact]
    public async Task AForeignStatusWithoutCallbackIsDiscardedAtOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await ApplyAsync(f, "wamid.phone-app", "sent", 1760000100);

        Assert.Equal("1|t", await ScalarAsync<string>(f.ConnectionString, "SELECT attempts || '|' || (processed_at IS NOT NULL)::text FROM messaging.webhook_deliveries"));
    }

    [Fact]
    public async Task AfterEightAttemptsTheDeliveryIsGivenUpWithAnError()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ApplyAsync(f, "wamid.never", "sent", 1760000100, callback: $"qep:{Guid.CreateVersion7()}");

        for (var attempt = 2; attempt <= 8; attempt++)
        {
            await ExecuteAsync(f.ConnectionString, "UPDATE messaging.webhook_deliveries SET claimed_until = now() - interval '1 minute'");
            await DrainDeliveriesAsync(f.Factory);
        }

        Assert.Equal("8|t|status-before-wamid: gave up after 8 attempts", await ScalarAsync<string>(f.ConnectionString, "SELECT attempts || '|' || (processed_at IS NOT NULL)::text || '|' || last_error FROM messaging.webhook_deliveries"));
    }

    // Decisión 7: con la conexión pausada los statuses sí se aplican.
    [Fact]
    public async Task StatusesApplyEvenWhenTheConnectionIsPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.p");
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        await ApplyAsync(f, "wamid.p", "delivered", 1760000100);

        Assert.Equal("2|-|wamid.p|2|1", await StateAsync(f, id));
    }
}
```

`AccountUpdateTests.cs`:

```csharp
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2 (account_update, D-M16, D-M12): la cuenta deshabilitada deja cada conexión
/// de la WABA en NeedsAttention con account_disabled; REINSTATE no reactiva.</summary>
public sealed class AccountUpdateTests
{
    [Theory]
    [InlineData("DISABLED_UPDATE", "DISABLE", "NeedsAttention|account_disabled")]
    [InlineData("ACCOUNT_DELETED", null, "NeedsAttention|account_disabled")]
    [InlineData("PARTNER_REMOVED", null, "NeedsAttention|account_disabled")]
    [InlineData("PARTNER_APP_UNINSTALLED", null, "NeedsAttention|account_disabled")]
    [InlineData("ACCOUNT_OFFBOARDED", null, "NeedsAttention|account_disabled")]
    [InlineData("DISABLED_UPDATE", "REINSTATE", "Active|-")]
    [InlineData("ACCOUNT_RECONNECTED", null, "Active|-")]
    [InlineData("ACCOUNT_VIOLATION", null, "Active|-")]
    public async Task TheAccountEventDecidesTheConnectionStatus(string @event, string? banState, string expected)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Soporte", "112", "222");
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Otra WABA", "113", "333");
        using var client = factory.CreateClient();

        await PostWebhookAsync(client, MetaPayloads.AccountUpdate("222", @event, banState));
        await DrainDeliveriesAsync(factory);

        Assert.Equal(expected, await ScalarAsync<string>(connectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections WHERE name = 'Ventas'"));
        Assert.Equal(expected, await ScalarAsync<string>(connectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections WHERE name = 'Soporte'"));
        Assert.Equal("Active|-", await ScalarAsync<string>(connectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections WHERE name = 'Otra WABA'"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }
}
```

`WebhookPurgeTests.cs`:

```csharp
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2: las entregas procesadas con más de 7 días se borran en lotes; las pendientes y las recientes se quedan.</summary>
public sealed class WebhookPurgeTests
{
    [Fact]
    public async Task OnlyOldProcessedDeliveriesAreDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;
        await ExecuteAsync(connectionString, """
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, processed_at) VALUES
              (decode('01', 'hex'), '{}', now() - interval '10 days', now() - interval '8 days'),
              (decode('02', 'hex'), '{}', now() - interval '10 days', now() - interval '6 days'),
              (decode('03', 'hex'), '{}', now() - interval '10 days', NULL)
            """);

        await DrainPurgeAsync(factory);

        Assert.Equal("02,03", await ScalarAsync<string>(connectionString, "SELECT string_agg(encode(body_sha256, 'hex'), ',' ORDER BY id) FROM messaging.webhook_deliveries"));
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~StatusIngestionTests|FullyQualifiedName~AccountUpdateTests|FullyQualifiedName~WebhookPurgeTests"
```

Esperado: `error CS0246 … 'WebhookPurgeWorker'` (y las de estados fallan por lógica tras compilar).

- [ ] **Step 2: Estados, `account_update` y purga**

`StatusIngestion.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

internal enum StatusOutcome
{
    Applied,
    NoChange,
    NotFound,
}

/// <summary>Spec 2026-10-09 §7.5 («Estados»), literal: un UPDATE monótono por índice único. Si tocó el último
/// de su conversación, actualiza la foto sin subir version ni updated_at (HOT).</summary>
internal static class StatusIngestion
{
    public static async Task<StatusOutcome> ApplyAsync(MessagingDbContext dbContext, Guid connectionId, StatusUpdate update, CancellationToken cancellationToken)
    {
        var newStatus = MessageColumnCodes.ToCode(update.Status);
        var callback = update.CallbackMessageId ?? Guid.Empty;
        int? failureCode = update.Status == MessageStatus.Failed ? update.ErrorCode ?? 0 : null;
        var failureTitle = update.Status == MessageStatus.Failed ? update.ErrorTitle : null;
        var updated = await dbContext.Database.SqlQuery<Guid>(
            $"""
            UPDATE messaging.messages
            SET status = {newStatus}, failure_code = {failureCode}, failure_title = {failureTitle},
                wamid = COALESCE(wamid, {update.Wamid})
            WHERE connection_id = {connectionId}
              AND (wamid = {update.Wamid} OR id = {callback})
              AND direction = 2
              AND (
                    ({newStatus} IN (2, 3) AND (status < {newStatus} OR (status = 4 AND failure_code = -1)))
                 OR ({newStatus} = 1       AND status = 4 AND failure_code = -1)
                 OR ({newStatus} = 4       AND status = 1)
              )
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        if (updated.Count == 0)
        {
            var exists = await dbContext.Database.SqlQuery<int>(
                $"""SELECT 1 AS "Value" FROM messaging.messages WHERE connection_id = {connectionId} AND (wamid = {update.Wamid} OR id = {callback}) AND direction = 2""")
                .ToListAsync(cancellationToken);
            return exists.Count == 0 ? StatusOutcome.NotFound : StatusOutcome.NoChange;
        }

        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.conversations SET last_message_status = {newStatus} WHERE last_message_id = {updated[0]}", cancellationToken);
        return StatusOutcome.Applied;
    }
}
```

En `WebhookDeliveryProcessor.cs` reemplaza los dos métodos vacíos:

```csharp
    /// <summary>§8.2: un status con callback de QEP y sin fila queda pendiente (el envío puede no haber
    /// commiteado el wamid); sin callback y sin fila es ajeno (la app del teléfono) y se descarta.</summary>
    private async Task<DeliveryOutcome> ProcessStatusesAsync(long deliveryId, Modules.Messaging.Application.MessagingRoute route, MessagesChange change, int attempts, CancellationToken cancellationToken)
    {
        var outcome = DeliveryOutcome.Processed;
        foreach (var status in change.Statuses)
        {
            var result = await StatusIngestion.ApplyAsync(dbContext, route.ConnectionId, status, cancellationToken);
            if (result == StatusOutcome.NotFound && status.CallbackMessageId is not null)
            {
                LogStatusPending(logger, deliveryId, attempts);
                outcome = DeliveryOutcome.RetryLater;
            }
            else if (result == StatusOutcome.NotFound)
            {
                LogForeignStatus(logger, deliveryId);
            }
        }

        return outcome;
    }

    /// <summary>§8.2, D-M16: cuenta deshabilitada, borrada, app desinstalada u offboarded → account_disabled
    /// en cada conexión de la WABA (sólo las Active cambian; el reporter ignora el resto). D-M12: REINSTATE
    /// y ACCOUNT_RECONNECTED no reactivan.</summary>
    private async Task ProcessAccountUpdateAsync(long deliveryId, AccountUpdateChange change, CancellationToken cancellationToken)
    {
        var disabled = change.Event switch
        {
            "DISABLED_UPDATE" => string.Equals(change.BanState, "DISABLE", StringComparison.Ordinal),
            "ACCOUNT_DELETED" or "PARTNER_REMOVED" or "PARTNER_APP_UNINSTALLED" or "ACCOUNT_OFFBOARDED" => true,
            _ => false,
        };
        LogAccountEvent(logger, deliveryId, change.Event, change.BanState, disabled);
        if (!disabled)
        {
            return;
        }

        foreach (var route in await directory.FindByAccountAsync(change.WabaId, cancellationToken))
        {
            await directory.ReportRejectedAsync(route.TenantId, route.ConnectionId, "account_disabled", cancellationToken);
        }
    }
```

Agrega al constructor `Modules.Messaging.Application.IMessagingConnectionDirectory directory` (además de `routing`, que cachea) y los logs:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: a status with a QEP callback has no message row yet (attempt {Attempt}); it is retried.")]
    private static partial void LogStatusPending(ILogger logger, long deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: a status without a QEP callback matched no message and was discarded.")]
    private static partial void LogForeignStatus(ILogger logger, long deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: account_update {Event} (ban state {BanState}); disables connections: {Disables}.")]
    private static partial void LogAccountEvent(ILogger logger, long deliveryId, string @event, string? banState, bool disables);
```

`WebhookPurgeWorker.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>§8.2: borra en lotes las entregas procesadas con más de <c>DeliveryRetentionDays</c>, cada
/// <c>PurgeIntervalHours</c>. Lo que agotó sus intentos quedó procesado y también se purga.</summary>
internal sealed partial class WebhookPurgeWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    ILogger<WebhookPurgeWorker> logger) : BackgroundService
{
    internal const int BatchSize = 1000;

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook purge deleted {Deleted} processed deliveries older than {Days} days.")]
    private static partial void LogPurged(ILogger logger, int deleted, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook purge failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.Value.PurgeIntervalHours)));
        do
        {
            try
            {
                await Task.Yield();
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        var days = Math.Max(1, options.Value.DeliveryRetentionDays);
        var total = 0;
        int deleted;
        do
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var cutoff = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow.AddDays(-days);
            deleted = await dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM messaging.webhook_deliveries
                WHERE id IN (SELECT id FROM messaging.webhook_deliveries WHERE processed_at < {cutoff} ORDER BY id LIMIT {BatchSize})
                """, cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        LogPurged(logger, total, days);
    }
}
```

Registro: `services.AddHostedService<WebhookPurgeWorker>();`.

- [ ] **Step 3: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~StatusIngestionTests|FullyQualifiedName~AccountUpdateTests|FullyQualifiedName~WebhookPurgeTests|FullyQualifiedName~InboundIngestionTests"
```

Esperado: todas correctas.

- [ ] **Step 4: Formato y commit**

```text
feat(messaging): acuses monótonos con callback, account_update a NeedsAttention y purga de entregas
```

---
### Task 14: Lecturas — lista, detalle e hilo

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessageQueries.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingMemberNames.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ListConversations.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/GetConversation.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageQueries.cs`
- Create: `src/Bootstrapper/MessagingCustomerDirectory.cs`, `src/Bootstrapper/MessagingMemberNames.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs`
- Modify: `src/Api/Program.cs` (`MapMessagingEndpoints`), `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `MessagingInfrastructureExtensions.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingValidatorsTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationsApiTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ThreadApiTests.cs`

**Interfaces:**
- Consumes: `TenantModuleGuard`, `IExecutionContext`, `ICustomerPhoneDirectory` (Customers), `IMembershipRepository`/`IUserDirectory` (como `IntegrationsConnectionAuthorNames`), `MessagingDbContext`, `MessageFailureReasons`.
- Produces: todos los DTO de la sección «Tipos que cruzan tareas»; `MessagingAuthorization.EnsureAuthorized`, `MessagingNotFound.Conversation(id)`, `ConversationSummaryBuilder`, `MessageMapping.ToDto(MessageRow, tenantId, names)`, `ListConversationsQuery(Guid TenantId, string? Status, string? Search, int? Page, int? PageSize) : IQuery<ConversationPageDto>`, `GetConversationQuery(Guid TenantId, Guid ConversationId) : IQuery<ConversationSummary>`, `ListMessagesQuery(Guid TenantId, Guid ConversationId, int? Limit, Guid? Before) : IQuery<MessagePageDto>`, validadores `ListConversationsValidator`, `ListMessagesValidator`, rutas `GET /messaging/conversations`, `GET /messaging/conversations/{id}`, `GET /messaging/conversations/{id}/messages`.

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingValidatorsTests.cs`:

```csharp
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §6.4, «Validadores»: la clave de <c>errors</c> es el nombre del query string.</summary>
public sealed class MessagingValidatorsTests
{
    private static string[] Keys<T>(FluentValidation.IValidator<T> validator, T instance) =>
        validator.Validate(instance).Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("Open", null, 1, 30, new string[0])]
    [InlineData(null, null, null, null, new string[0])]
    [InlineData("Resolved", "laura", 2, 50, new string[0])]
    [InlineData("Archived", null, 1, 30, new[] { "status" })]
    [InlineData("open", null, 1, 30, new[] { "status" })]
    [InlineData("Open", null, 0, 30, new[] { "page" })]
    [InlineData("Open", null, 1, 0, new[] { "pageSize" })]
    [InlineData("Open", null, 1, 51, new[] { "pageSize" })]
    public void ListConversationsNamesTheField(string? status, string? search, int? page, int? pageSize, string[] expected) =>
        Assert.Equal(expected, Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), status, search, page, pageSize)));

    [Fact]
    public void ListConversationsRejectsASearchOver100Characters() =>
        Assert.Equal(["search"], Keys(new ListConversationsValidator(), new ListConversationsQuery(Guid.CreateVersion7(), "Open", new string('x', 101), 1, 30)));

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData(1, new string[0])]
    [InlineData(100, new string[0])]
    [InlineData(0, new[] { "limit" })]
    [InlineData(101, new[] { "limit" })]
    public void ListMessagesNamesTheLimit(int? limit, string[] expected) =>
        Assert.Equal(expected, Keys(new ListMessagesValidator(), new ListMessagesQuery(Guid.CreateVersion7(), Guid.CreateVersion7(), limit, null)));

    [Fact]
    public void ListMessagesRejectsAnEmptyBefore() =>
        Assert.Equal(["before"], Keys(new ListMessagesValidator(), new ListMessagesQuery(Guid.CreateVersion7(), Guid.CreateVersion7(), 50, Guid.Empty)));
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §5.3 y §8.7: la lista con orden, counts, cliente emparejado por teléfono,
/// connectionName (y «Conexión eliminada»), búsqueda por perfil, número y cliente; el detalle; módulo
/// apagado, sin permiso y otro tenant → 403 con su código.</summary>
public sealed class ConversationsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string[]? permissions = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions ?? ManagePermissions));
    }

    private static async Task IngestAsync(Fixture f, string waId, string wamid, long timestamp, string text, string profileName = "Laura")
    {
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", waId, wamid, timestamp, text, profileName));
        await DrainDeliveriesAsync(f.Factory);
    }

    [Fact]
    public async Task TheListIsOrderedByActivityWithCountsCustomerAndConnectionName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "primero");
        await IngestAsync(f, "573009999999", "w2", 1760000200, "segundo", "Pedro");
        await IngestAsync(f, "573001234567", "w3", 1760000300, "tercero");
        // Un cliente de QEP con ese teléfono (Customers calcula phone_e164 = +573001234567).
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");

        var page = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(30, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, page.GetProperty("counts").GetProperty("open").GetInt32());
        Assert.Equal(3, page.GetProperty("counts").GetProperty("unread").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["573001234567", "573009999999"], items.Select(item => item.GetProperty("contact").GetProperty("waId").GetString()));
        var first = items[0];
        Assert.Equal("Laura", first.GetProperty("contact").GetProperty("profileName").GetString());
        Assert.Equal("Ventas", first.GetProperty("connectionName").GetString());
        Assert.Equal(f.ConnectionId, first.GetProperty("connectionId").GetGuid());
        Assert.Equal(customerId, first.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal("Droguería Central", first.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("Open", first.GetProperty("status").GetString());
        Assert.Equal(2, first.GetProperty("unreadCount").GetInt32());
        Assert.Equal("Inbound", first.GetProperty("lastMessage").GetProperty("direction").GetString());
        Assert.Equal("Text", first.GetProperty("lastMessage").GetProperty("kind").GetString());
        Assert.Equal("tercero", first.GetProperty("lastMessage").GetProperty("preview").GetString());
        Assert.Equal("Delivered", first.GetProperty("lastMessage").GetProperty("status").GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000300).AddHours(24), first.GetProperty("customerWindowExpiresAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("customer").ValueKind);
        Assert.True(first.GetProperty("version").GetInt64() >= 1);
    }

    [Theory]
    [InlineData("lau", "573001234567")]
    [InlineData("9999", "573009999999")]
    [InlineData("drogue", "573001234567")]
    [InlineData("nadie", null)]
    public async Task TheSearchMatchesProfileNumberAndCustomerName(string search, string? expectedWaId)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        await IngestAsync(f, "573009999999", "w2", 1760000200, "b", "Pedro");
        await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");

        var page = await f.Client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(f.Tenant.TenantId)}?search={Uri.EscapeDataString(search)}", Ct);

        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expectedWaId is null ? [] : [expectedWaId], items.Select(item => item.GetProperty("contact").GetProperty("waId").GetString()));
        // counts no respeta la búsqueda (§5.4).
        Assert.Equal(2, page.GetProperty("counts").GetProperty("open").GetInt32());
    }

    [Fact]
    public async Task ADeletedConnectionShowsAPlaceholderName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        await ExecuteAsync(f.ConnectionString, "DELETE FROM integrations.connections");

        var page = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal("Conexión eliminada", page.GetProperty("items")[0].GetProperty("connectionName").GetString());
    }

    [Fact]
    public async Task TheDetailAnswersTheSameShapeAnd404InsideTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        var id = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var detail = await f.Client.GetAsync(ConversationUrl(f.Tenant.TenantId, id), Ct);
        var missing = await f.Client.GetAsync(ConversationUrl(f.Tenant.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(id, (await detail.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemAsync(missing)).Code);
    }

    [Fact]
    public async Task ModuleOffMissingPermissionAndAnotherTenantAre403WithTheirCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        var id = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        using var noPermission = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId);

        var cross = await otherClient.GetAsync(ConversationUrl(f.Tenant.TenantId, id), Ct);
        var ownRouteForeignId = await otherClient.GetAsync(ConversationUrl(other.TenantId, id), Ct);
        var denied = await noPermission.GetAsync(ConversationsUrl(f.Tenant.TenantId), Ct);
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var off = await f.Client.GetAsync(ConversationsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(cross)).Code);
        Assert.Equal(HttpStatusCode.NotFound, ownRouteForeignId.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(off)).Code);
    }
}
```

`CreateCustomerAsync(factory, tenant, name, phone)` en el harness: crea un cliente por HTTP (`POST /api/v1/tenants/{t}/customers` con el cuerpo mínimo que `CustomerApiTests` de Customers usa: identificación, país `CO`, ciudad DIVIPOLA, clasificación; copia el helper de `tests/Modules/Customers/Modules.Customers.IntegrationTests/*Harness*` que ya siembra clasificación y ciudad) y devuelve el `id`. El cliente del harness necesita `customers.customer.manage` y `customers.classification.manage` por `X-Permissions`.

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ThreadApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §5.3 y §8.7: hilo cronológico, paginación hacia atrás con before y hasMore, la
/// forma de cada kind, failureReason y sentBy; Review Focus 3: un before ajeno es validation.failed.</summary>
public sealed class ThreadApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheThreadIsChronologicalAndPagesBackwardsWithBefore()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        for (var i = 1; i <= 5; i++)
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", $"w{i}", 1760000000 + i, $"m{i}"));
        }

        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var newest = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=2", Ct);
        var items = newest.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["m4", "m5"], items.Select(item => item.GetProperty("text").GetString()));
        Assert.True(newest.GetProperty("hasMore").GetBoolean());
        Assert.Equal("Inbound", items[0].GetProperty("direction").GetString());
        Assert.Equal("Delivered", items[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("sentBy").ValueKind);

        var before = items[0].GetProperty("id").GetGuid();
        var older = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=2&before={before}", Ct);
        Assert.Equal(["m2", "m3"], older.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("text").GetString()));
        Assert.True(older.GetProperty("hasMore").GetBoolean());
        var oldest = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=5&before={older.GetProperty("items")[0].GetProperty("id").GetGuid()}", Ct);
        Assert.Equal(["m1"], oldest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("text").GetString()));
        Assert.False(oldest.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task MediaLocationAndFailedMessagesHaveTheirShape()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573001234567", "w1", 1760000001, "document", "media-1", "application/pdf", caption: "la orden", filename: "orden.pdf"));
        await PostWebhookAsync(anonymous, MetaPayloads.Change("messages", MetaPayloads.LocationValue("111", "573001234567", "w2", 1760000002, 4.6, -74.1, "Casa", "Calle 1")));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var memberId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM tenancy.memberships WHERE tenant_id = @t", ("t", tenant.TenantId));
        var failedId = await SeedOutboundAsync(connectionString, conversationId, tenant.TenantId, connectionId, "wamid.f", status: 4, failureCode: 131047, occurredAtUnix: 1760000003);
        await ExecuteAsync(connectionString, "UPDATE messaging.messages SET sent_by_member_id = @m WHERE id = @id", ("m", memberId), ("id", failedId));

        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct);
        var items = page.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(["Document", "Location", "Text"], items.Select(item => item.GetProperty("kind").GetString()));
        var media = items[0].GetProperty("media");
        Assert.Equal(MediaUrl(tenant.TenantId, items[0].GetProperty("id").GetGuid()), media.GetProperty("url").GetString());
        Assert.Equal("application/pdf", media.GetProperty("mimeType").GetString());
        Assert.Equal("orden.pdf", media.GetProperty("fileName").GetString());
        Assert.Equal("la orden", media.GetProperty("caption").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("text").ValueKind);
        Assert.Equal(4.6, items[1].GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal("Casa", items[1].GetProperty("location").GetProperty("name").GetString());
        Assert.Equal("Outbound", items[2].GetProperty("direction").GetString());
        Assert.Equal("Failed", items[2].GetProperty("status").GetString());
        Assert.Equal("Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas.", items[2].GetProperty("failureReason").GetString());
        Assert.Equal(memberId, items[2].GetProperty("sentBy").GetProperty("memberId").GetGuid());
        Assert.False(string.IsNullOrEmpty(items[2].GetProperty("sentBy").GetProperty("displayName").GetString()));
        Assert.DoesNotContain("failureTitle", page.GetRawText(), StringComparison.Ordinal);
    }

    // Review Focus 3.
    [Fact]
    public async Task ABeforeFromAnotherConversationIsAValidationError()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", 1760000001, "a"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573009999999", "w2", 1760000002, "b"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversations = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        var foreignMessage = (await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversations[0]), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var response = await client.GetAsync($"{MessagesUrl(tenant.TenantId, conversations[1])}?before={foreignMessage}", Ct);
        var unknown = await client.GetAsync($"{MessagesUrl(tenant.TenantId, conversations[1])}?before={Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(("validation.failed", new[] { "before" }), await ProblemAsync(response));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }
}
```

(`MetaPayloads.LocationValue(...)` arma el `value` de un `messages` con un mensaje `location`; escríbelo junto a los demás fixtures.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessagingValidatorsTests"
```

Esperado: `error CS0246 … 'ListConversationsValidator'`.

- [ ] **Step 2: DTO, apoyo y puertos**

`MessagingDtos.cs`: los records de la sección «Tipos que cruzan tareas» (`ContactDto` … `MediaStreamDto`), con este encabezado:

```csharp
namespace Modules.Messaging.Application;

// BFF (CLAUDE.md): copia literal del contrato del frontend (spec 2026-10-09 §5.3). Enums por nombre
// (Inbound, Image, Read, Open). customerWindowExpiresAt viaja calculado para que la pantalla no sepa
// de las 24 h; connectionName y customer vienen resueltos por página para que la lista no pida nada
// aparte; MessageHit lleva conversationId, contact, customer y connectionName por lo mismo.
```

`MessagingSupport.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Application;

/// <summary>Copia de <c>IntegrationsAuthorization</c>: tenant de la ruta distinto del activo, o permiso
/// faltante → 403 <c>authorization.denied</c>. Nunca 404. Después, el módulo (decisión 5).</summary>
internal static class MessagingAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException("authorization.denied", "The subject cannot perform this messaging operation for this tenant.");
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Messaging, cancellationToken);
    }
}

internal static class MessagingNotFound
{
    public static ResourceNotFoundException Conversation(Guid id) =>
        new(MessagingErrorCodes.ConversationNotFound, $"Conversation '{id}' was not found.");

    public static ResourceNotFoundException Message(Guid id) =>
        new(MessagingErrorCodes.MessageNotFound, $"Message '{id}' was not found.");
}

/// <summary>§8.7: arma los <c>ConversationSummary</c> de una página con una llamada a cada directorio.
/// Público sólo para que Infrastructure lo registre; no es API para otros módulos.</summary>
public sealed class ConversationSummaryBuilder(IMessagingConnectionDirectory connections, IMessagingCustomerDirectory customers)
{
    public const string DeletedConnectionName = "Conexión eliminada";

    public async Task<IReadOnlyList<ConversationSummary>> BuildAsync(Guid tenantId, IReadOnlyList<ConversationRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var names = await connections.ListNamesAsync(tenantId, cancellationToken);
        var matches = await customers.MatchAsync(tenantId, rows.Select(row => row.WaId).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
        return rows.Select(row => ToSummary(row, names, matches)).ToArray();
    }

    public static ConversationSummary ToSummary(
        ConversationRow row, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<string, CustomerRefDto> matches) =>
        new(
            row.Id,
            row.ConnectionId,
            names.GetValueOrDefault(row.ConnectionId) ?? DeletedConnectionName,
            new ContactDto(row.WaId, row.ProfileName),
            matches.GetValueOrDefault(row.WaId),
            row.Status.ToString(),
            row.UnreadCount,
            row.LastMessageId is null || row.LastMessageAt is null
                ? null
                : new LastMessageDto(row.LastMessageDirection!.Value.ToString(), row.LastMessageKind!.Value.ToString(), row.LastMessagePreview, row.LastMessageStatus!.Value.ToString(), row.LastMessageAt.Value),
            row.LastInboundAt?.Add(Conversation.WindowLength),
            row.UpdatedAt,
            row.Version);
}

/// <summary>§8.7: un <c>MessageRow</c> a <c>Message</c>; la URL del medio es la de §8.6 aunque no esté copiado.</summary>
internal static class MessageMapping
{
    public static string MediaUrl(Guid tenantId, Guid messageId) => $"/api/v1/tenants/{tenantId}/messaging/media/{messageId}";

    public static MessageDto ToDto(MessageRow row, Guid tenantId, IReadOnlyDictionary<Guid, string> memberNames) =>
        new(
            row.Id,
            row.Direction.ToString(),
            row.Kind.ToString(),
            row.Text,
            row.Media is null ? null : new MediaDto(MediaUrl(tenantId, row.Id), row.Media.MimeType, row.Media.FileName, row.Caption),
            row.Kind == MessageKind.Location ? LocationFrom(row.DetailsJson) : null,
            row.Status.ToString(),
            MessageFailureReasons.For(row.FailureCode),
            row.OccurredAt,
            row.SentByMemberId is { } member ? new SentByDto(member, memberNames.GetValueOrDefault(member)) : null,
            row.ClientId);

    private static LocationDto? LocationFrom(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("latitude", out var latitude) || !root.TryGetProperty("longitude", out var longitude))
            {
                return null;
            }

            return new LocationDto(
                latitude.GetDouble(),
                longitude.GetDouble(),
                root.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() : null,
                root.TryGetProperty("address", out var address) && address.ValueKind == System.Text.Json.JsonValueKind.String ? address.GetString() : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
```

`IConversationQueries.cs`, `IMessageQueries.cs`, `IMessagingCustomerDirectory.cs`, `IMessagingMemberNames.cs`: las interfaces y records de la sección «Tipos que cruzan tareas» (`ConversationRow`, `MessageRow`, `MessageMediaRow`, `MessageCursor` van en `IConversationQueries.cs` / `IMessageQueries.cs`), cada una con un `<summary>` que cite la consulta de §7.6 que implementa.

- [ ] **Step 3: Handlers y validadores**

`ListConversations.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListConversationsQuery(Guid TenantId, string? Status, string? Search, int? Page, int? PageSize) : IQuery<ConversationPageDto>;

/// <summary>Spec §6.4: status ∈ Open|Resolved (default Open), search ≤ 100, page ≥ 1, pageSize 1–50 (default 30).</summary>
public sealed class ListConversationsValidator : AbstractValidator<ListConversationsQuery>
{
    public ListConversationsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => status is null || status is nameof(ConversationStatus.Open) or nameof(ConversationStatus.Resolved))
            .WithName("status").WithMessage("status must be Open or Resolved.");
        RuleFor(query => query.Search).MaximumLength(100).WithName("search");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null).WithName("page");
        RuleFor(query => query.PageSize).InclusiveBetween(1, 50).When(query => query.PageSize is not null).WithName("pageSize");
    }
}

/// <summary>§8.7: la lista nunca toca <c>messages</c>; counts cuentan el tenant entero, sin búsqueda (§5.4).</summary>
public sealed class ListConversationsHandler(
    IConversationQueries queries,
    IMessagingCustomerDirectory customers,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<ListConversationsQuery> validator)
    : IQueryHandler<ListConversationsQuery, ConversationPageDto>
{
    public const int DefaultPageSize = 30;

    public async Task<ConversationPageDto> HandleAsync(ListConversationsQuery query, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var status = query.Status is nameof(ConversationStatus.Resolved) ? ConversationStatus.Resolved : ConversationStatus.Open;
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        // §8.7: los clientes cuyo nombre contiene el término aportan sus números al filtro.
        var customerWaIds = search is null
            ? Array.Empty<string>()
            : await customers.FindWaIdsByNameAsync(query.TenantId, search, cancellationToken);

        var (rows, total) = await queries.ListAsync(query.TenantId, status, search, customerWaIds, page, pageSize, cancellationToken);
        var counts = await queries.CountsAsync(query.TenantId, cancellationToken);
        return new ConversationPageDto(await summaries.BuildAsync(query.TenantId, rows, cancellationToken), total, page, pageSize, counts);
    }
}
```

`GetConversation.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record GetConversationQuery(Guid TenantId, Guid ConversationId) : IQuery<ConversationSummary>;

public sealed class GetConversationHandler(
    IConversationQueries queries,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetConversationQuery, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(GetConversationQuery query, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var row = await queries.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(query.ConversationId);
        return (await summaries.BuildAsync(query.TenantId, [row], cancellationToken))[0];
    }
}
```

`ListMessages.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListMessagesQuery(Guid TenantId, Guid ConversationId, int? Limit, Guid? Before) : IQuery<MessagePageDto>;

public sealed class ListMessagesValidator : AbstractValidator<ListMessagesQuery>
{
    public ListMessagesValidator()
    {
        RuleFor(query => query.Limit).InclusiveBetween(1, 100).When(query => query.Limit is not null).WithName("limit");
        RuleFor(query => query.Before).NotEqual(Guid.Empty).When(query => query.Before is not null).WithName("before");
    }
}

/// <summary>§8.7: los <c>limit</c> más nuevos, o los anteriores a <c>before</c> (keyset por
/// <c>(occurred_at, id)</c>); la fila <c>limit + 1</c> sólo decide <c>hasMore</c>; items cronológicos.
/// Un <c>before</c> que no es de esa conversación es <c>validation.failed</c> (Review Focus 3): la misma
/// respuesta que un GUID inexistente, así que no confirma nada.</summary>
public sealed class ListMessagesHandler(
    IConversationQueries conversations,
    IMessageQueries messages,
    IMessagingMemberNames memberNames,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<ListMessagesQuery> validator)
    : IQueryHandler<ListMessagesQuery, MessagePageDto>
{
    public const int DefaultLimit = 50;

    public async Task<MessagePageDto> HandleAsync(ListMessagesQuery query, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        _ = await conversations.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(query.ConversationId);

        MessageCursor? cursor = null;
        if (query.Before is { } before)
        {
            cursor = await messages.FindCursorAsync(query.ConversationId, before, cancellationToken)
                ?? throw new ValidationException([new ValidationFailure("before", "before must be a message of this conversation.")]);
        }

        var limit = query.Limit ?? DefaultLimit;
        var rows = await messages.ListThreadAsync(query.ConversationId, cursor, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).Reverse().ToArray();
        var names = await memberNames.FindAsync(
            query.TenantId, page.Where(row => row.SentByMemberId is not null).Select(row => row.SentByMemberId!.Value).Distinct().ToArray(), cancellationToken);
        return new MessagePageDto(page.Select(row => MessageMapping.ToDto(row, query.TenantId, names)).ToArray(), hasMore);
    }
}
```

- [ ] **Step 4: Consultas, adaptadores, endpoints y registro**

`ConversationQueries.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.6: la lista por <c>IX_conversations_tenant_status_activity</c>; los counts por los dos
/// índices parciales; la búsqueda por los GIN de trigramas (ILIKE / LIKE) y por <c>wa_id = ANY</c>.</summary>
internal sealed class ConversationQueries(MessagingDbContext dbContext) : IConversationQueries
{
    public async Task<(IReadOnlyList<ConversationRow> Items, int Total)> ListAsync(
        Guid tenantId, ConversationStatus status, string? search, IReadOnlyCollection<string> customerWaIds, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Conversations.AsNoTracking().Where(conversation => conversation.TenantId == tenantId && conversation.Status == status);
        if (search is not null)
        {
            var pattern = "%" + Escape(search) + "%";
            var digits = new string(search.Where(char.IsAsciiDigit).ToArray());
            var digitsPattern = digits.Length == 0 ? null : "%" + digits + "%";
            var phones = customerWaIds.ToArray();
            query = query.Where(conversation =>
                (conversation.ProfileName != null && EF.Functions.ILike(conversation.ProfileName, pattern, "\\"))
                || (digitsPattern != null && EF.Functions.Like(conversation.WaId, digitsPattern))
                || phones.Contains(conversation.WaId));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(conversation => conversation.LastActivityAt).ThenByDescending(conversation => conversation.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(Projection)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<ConversationCountsDto> CountsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var open = await dbContext.Conversations.CountAsync(conversation => conversation.TenantId == tenantId && conversation.Status == ConversationStatus.Open, cancellationToken);
        var unread = await dbContext.Conversations.Where(conversation => conversation.TenantId == tenantId && conversation.UnreadCount > 0).SumAsync(conversation => conversation.UnreadCount, cancellationToken);
        return new ConversationCountsDto(open, unread);
    }

    public Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToArray();
        return await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && wanted.Contains(conversation.Id))
            .Select(Projection)
            .ToListAsync(cancellationToken);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Conversation, ConversationRow>> Projection = conversation => new ConversationRow(
        conversation.Id, conversation.TenantId, conversation.ConnectionId, conversation.WaId, conversation.ProfileName, conversation.Status,
        conversation.UnreadCount, conversation.LastInboundAt, conversation.LastMessageId, conversation.LastMessageDirection, conversation.LastMessageKind,
        conversation.LastMessagePreview, conversation.LastMessageStatus, conversation.LastMessageAt, conversation.UpdatedAt, conversation.Version);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
```

`MessageQueries.cs` (la búsqueda del historial se suma en la Task 15):

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.6: el hilo por <c>IX_messages_thread</c> con keyset <c>(occurred_at, id) &lt; (@t, @id)</c>.</summary>
internal sealed partial class MessageQueries(MessagingDbContext dbContext) : IMessageQueries
{
    public Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId && message.Id == messageId)
            .Select(message => new MessageCursor(message.OccurredAt, message.Id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken cancellationToken)
    {
        var query = dbContext.Messages.AsNoTracking().Where(message => message.ConversationId == conversationId);
        if (before is { } cursor)
        {
            query = query.Where(message => message.OccurredAt < cursor.OccurredAt || (message.OccurredAt == cursor.OccurredAt && message.Id.CompareTo(cursor.Id) < 0));
        }

        return await query
            .OrderByDescending(message => message.OccurredAt).ThenByDescending(message => message.Id)
            .Take(take)
            .Select(RowProjection)
            .ToListAsync(cancellationToken);
    }

    internal static readonly System.Linq.Expressions.Expression<Func<MessageRecord, MessageRow>> RowProjection = message => new MessageRow(
        message.Id, message.ConversationId, message.Direction, message.Kind, message.Text, message.Caption, message.Details, message.Status,
        message.FailureCode, message.OccurredAt, message.SentByMemberId, message.ClientId,
        message.Media == null ? null : new MessageMediaRow(message.Media.MimeType, message.Media.FileName));
}
```

(Si EF no traduce `Guid.CompareTo`, usa SQL crudo con `(occurred_at, id) < ({cursor.OccurredAt}, {cursor.Id})` vía `dbContext.Messages.FromSql(...)` y la misma proyección.)

`src/Bootstrapper/MessagingCustomerDirectory.cs`:

```csharp
using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>§6.5: el wa_id de Meta son los dígitos del E.164; se traduce en las dos direcciones acá.</summary>
internal sealed class MessagingCustomerDirectory(ICustomerPhoneDirectory phones) : IMessagingCustomerDirectory
{
    public const int NameCap = 200;

    public async Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken)
    {
        var matches = await phones.MatchAsync(tenantId, waIds.Select(waId => "+" + waId).ToArray(), cancellationToken);
        return matches.ToDictionary(pair => pair.Key.TrimStart('+'), pair => new CustomerRefDto(pair.Value.Id, pair.Value.Name), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        (await phones.FindPhonesByNameAsync(tenantId, term, NameCap, cancellationToken)).Select(phone => phone.TrimStart('+')).ToArray();
}
```

`src/Bootstrapper/MessagingMemberNames.cs`: copia de `IntegrationsConnectionAuthorNames` implementando `IMessagingMemberNames` (mismo cuerpo).

`MessagingEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta; cada endpoint su política (spec 2026-10-09 §6.4). Los handlers revalidan
        // tenant, permiso y módulo, y responden 403 con código, nunca 404, ante otro tenant o módulo apagado.
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/messaging").WithTags("Messaging");

        group.MapGet("/conversations", ListConversationsAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ConversationPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/conversations/{conversationId:guid}", GetConversationAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/conversations/{conversationId:guid}/messages", ListMessagesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<MessagePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListConversationsAsync(
        Guid tenantId, string? status, string? search, int? page, int? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListConversationsQuery(tenantId, status, search, page, pageSize), cancellationToken));

    private static async Task<IResult> GetConversationAsync(Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetConversationQuery(tenantId, conversationId), cancellationToken));

    private static async Task<IResult> ListMessagesAsync(
        Guid tenantId, Guid conversationId, int? limit, Guid? before, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListMessagesQuery(tenantId, conversationId, limit, before), cancellationToken));
}
```

`Program.cs`, después de `app.MapWhatsAppWebhook(...)`: `app.MapMessagingEndpoints();`.

`MessagingInfrastructureExtensions.cs`:

```csharp
        services.AddScoped<IConversationQueries, ConversationQueries>();
        services.AddScoped<IMessageQueries, MessageQueries>();
        services.AddScoped<ConversationSummaryBuilder>();
```

(`ConversationSummaryBuilder` es `public` en Application por esto; `MessagingAuthorization`, `MessagingNotFound` y `MessageMapping` siguen `internal`.)

`QepServiceCollectionExtensions.cs`:

```csharp
        services.AddValidatorsFromAssemblyContaining<ListConversationsValidator>();
        services.AddScoped<IQueryHandler<ListConversationsQuery, ConversationPageDto>, ListConversationsHandler>();
        services.AddScoped<IQueryHandler<GetConversationQuery, ConversationSummary>, GetConversationHandler>();
        services.AddScoped<IQueryHandler<ListMessagesQuery, MessagePageDto>, ListMessagesHandler>();
        // Messaging (spec 2026-10-09 §6.4): clientes por teléfono y nombres de miembros, por adaptadores.
        services.AddScoped<IMessagingCustomerDirectory, MessagingCustomerDirectory>();
        services.AddScoped<IMessagingMemberNames, MessagingMemberNames>();
```

- [ ] **Step 5: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~MessagingValidatorsTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~ConversationsApiTests|FullyQualifiedName~ThreadApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todas correctas.

- [ ] **Step 6: Formato y commit**

```text
feat(messaging): lista, detalle e hilo de conversaciones con cliente y conexión resueltos por página
```

---

### Task 15: Búsqueda en el historial

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SearchTerms.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessageSearch.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SearchMessages.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageSearch.cs`
- Modify: `MessagingEndpoints.cs`, `MessagingInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SearchTermsTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageSearchApiTests.cs`

**Interfaces:**
- Consumes: `IConversationQueries.FindAsync/FindManyAsync`, `ConversationSummaryBuilder`, `MessageMapping`, `MessagingDbContext`.
- Produces: `SearchTerms.Tokenize(string q) → IReadOnlyList<string>` (≤ 8), `SearchTerms.Compose(IReadOnlyList<(string Token, string? Lexeme)>) → string?`, `IMessageSearch`, `SearchMessagesQuery(Guid TenantId, string? Q, Guid? ConversationId, DateTimeOffset? From, DateTimeOffset? To, int? Limit, Guid? Before) : IQuery<SearchPageDto>`, `SearchMessagesValidator`, `SearchMessagesHandler`, `SearchMessagesHandler.TimeoutMessage`, ruta `GET /messaging/messages/search`.

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/SearchTermsTests.cs`:

```csharp
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §7.3, «Cómo se arma @q»: operadores quitados, tope de 8 tokens, prefijo sólo
/// con 3 o más caracteres, stop words (sin lexema) omitidas, sin lexemas → nada que consultar.</summary>
public sealed class SearchTermsTests
{
    [Fact]
    public void TokensDropOperatorsControlsAndEmptiesAndKeepEight()
    {
        Assert.Equal(["drog", "pedido"], SearchTerms.Tokenize("  drog & pedido  "));
        Assert.Equal(["a", "b"], SearchTerms.Tokenize("a:*|!(b)'\\<"));
        Assert.Empty(SearchTerms.Tokenize("&|!:*()'\\<"));
        Assert.Empty(SearchTerms.Tokenize("\u0000\u0001"));
        Assert.Equal(["t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"], SearchTerms.Tokenize("t1 t2 t3 t4 t5 t6 t7 t8 t9 t10"));
    }

    [Fact]
    public void ComposeUsesPrefixOnlyFromThreeCharactersAndSkipsTokensWithoutLexeme()
    {
        var query = SearchTerms.Compose([("drogueria", "drogueri"), ("de", null), ("pedidos", "pedid"), ("ab", "ab")]);

        Assert.Equal("drogueri:* & pedid:* & ab", query);
        Assert.Null(SearchTerms.Compose([("de", null), ("la", null)]));
        Assert.Null(SearchTerms.Compose([]));
    }

    [Fact]
    public void ALexemeWithStrangeCharactersIsQuotedNeverInjected()
    {
        // Un lexema sólo tiene letras y dígitos; cualquier otra cosa se descarta por seguridad.
        Assert.Null(SearchTerms.Compose([("x", "a b"), ("y", "a'b")]));
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageSearchApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.8 y §12: sólo el tenant; acentos y flexiones; prefijo; una leyenda; before y
/// hasMore; conversationId; operadores → 200 vacío; q corto → 422 en q; before ajeno → 404; el timeout → 422 en q.</summary>
public sealed class MessageSearchApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid ConversationA, Guid ConversationB);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, other.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        await SeedWhatsAppConnectionAsync(factory, other.TenantId, "Ajena", "999", "888");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", 1760000001, "Hola, soy de la Droguería Central"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w2", 1760000002, "Tienen pedidos pendientes?"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573009999999", "w3", 1760000003, "pedido urgente"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573009999999", "w4", 1760000004, "image", "m1", "image/jpeg", caption: "foto del pedido"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("999", "573001234567", "w5", 1760000005, "pedido de otro tenant"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversations = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("contact").GetProperty("waId").GetString()!, item => item.GetProperty("id").GetGuid());
        return new Fixture(factory, connectionString, tenant, client, conversations["573001234567"], conversations["573009999999"]);
    }

    private static Task<JsonElement> SearchAsync(Fixture f, string query) =>
        f.Client.GetFromJsonAsync<JsonElement>($"{SearchUrl(f.Tenant.TenantId)}?{query}", Ct);

    [Theory]
    [InlineData("q=drogueria", new[] { "w1" })]
    [InlineData("q=pedido", new[] { "w4", "w3", "w2" })]
    [InlineData("q=drog", new[] { "w1" })]
    [InlineData("q=pedido%20urgente", new[] { "w3" })]
    [InlineData("q=foto", new[] { "w4" })]
    public async Task ItFindsByWordWithoutAccentsNorInflectionsOnlyInTheTenant(string query, string[] expectedWamids)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var page = await SearchAsync(f, query);

        var ids = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        var wamids = new List<string>();
        foreach (var id in ids)
        {
            wamids.Add(await ScalarAsync<string>(f.ConnectionString, "SELECT wamid FROM messaging.messages WHERE id = @id", ("id", id)));
        }

        Assert.Equal(expectedWamids, wamids);
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task EachHitCarriesItsConversationContactCustomerAndConnection()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var hit = (await SearchAsync(f, "q=drogueria")).GetProperty("items")[0];

        Assert.Equal(f.ConversationA, hit.GetProperty("conversationId").GetGuid());
        Assert.Equal("573001234567", hit.GetProperty("contact").GetProperty("waId").GetString());
        Assert.Equal("Ventas", hit.GetProperty("connectionName").GetString());
        Assert.Equal(JsonValueKind.Null, hit.GetProperty("customer").ValueKind);
        Assert.Equal("Text", hit.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ItPagesWithBeforeAndFiltersByConversationAndDates()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var first = await SearchAsync(f, "q=pedido&limit=2");
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var before = first.GetProperty("items")[1].GetProperty("id").GetGuid();
        var second = await SearchAsync(f, $"q=pedido&limit=2&before={before}");
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.False(second.GetProperty("hasMore").GetBoolean());

        var onlyB = await SearchAsync(f, $"q=pedido&conversationId={f.ConversationB}");
        Assert.Equal(2, onlyB.GetProperty("items").GetArrayLength());
        var dated = await SearchAsync(f, $"q=pedido&from={Uri.EscapeDataString(DateTimeOffset.FromUnixTimeSeconds(1760000003).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.FromUnixTimeSeconds(1760000003).ToString("O"))}");
        Assert.Single(dated.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("q=%26%7C%21%3A%2A%28%29%27")]
    [InlineData("q=de%20la")]
    public async Task OperatorsAndStopWordsAnswer200Empty(string query)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var page = await SearchAsync(f, query);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task AShortQAnInvertedRangeAndAForeignBeforeAreRejectedWithTheirCodes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ReadPermissions);
        var mine = (await SearchAsync(f, "q=drogueria")).GetProperty("items")[0].GetProperty("id").GetGuid();

        var shortQ = await f.Client.GetAsync($"{SearchUrl(f.Tenant.TenantId)}?q=a", Ct);
        var inverted = await f.Client.GetAsync($"{SearchUrl(f.Tenant.TenantId)}?q=pedido&from=2026-10-09T00:00:00Z&to=2026-10-08T00:00:00Z", Ct);
        var foreignBefore = await otherClient.GetAsync($"{SearchUrl(other.TenantId)}?q=pedido&before={mine}", Ct);
        var foreignConversation = await otherClient.GetAsync($"{SearchUrl(other.TenantId)}?q=pedido&conversationId={f.ConversationA}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortQ.StatusCode);
        Assert.Equal(("validation.failed", new[] { "q" }), await ProblemAsync(shortQ));
        Assert.Equal(("validation.failed", new[] { "to" }), await ProblemAsync(inverted));
        Assert.Equal(HttpStatusCode.NotFound, foreignBefore.StatusCode);
        Assert.Equal("messaging.message.not_found", (await ProblemAsync(foreignBefore)).Code);
        Assert.Equal(HttpStatusCode.NotFound, foreignConversation.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemAsync(foreignConversation)).Code);
    }

    // §8.8, paso 4: un 57014 nunca es 500.
    [Fact]
    public async Task ATimeoutIsAValidationErrorOnQ()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var slow = f.Factory.WithWebHostBuilder(builder => builder.UseSetting("Messaging:Search:StatementTimeoutMs", "1"));
        using var client = CreateClient(slow, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        // pg_sleep dentro de la misma transacción no se puede inyectar; se fuerza el timeout con 1 ms y un
        // término que obliga a ordenar: si en esta máquina responde antes de 1 ms, la prueba no demuestra
        // nada y se marca como no concluyente.
        var response = await client.GetAsync($"{SearchUrl(f.Tenant.TenantId)}?q=pedido", Ct);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Assert.Skip("La consulta respondió antes de 1 ms; el timeout no se pudo provocar en esta máquina.");
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(("validation.failed", new[] { "q" }), await ProblemAsync(response));
    }
}
```

(`Messaging:Search:StatementTimeoutMs` existe para poder provocar el timeout en una prueba; en producción queda en su default de 2000 (§7.3). Va en una clase `MessagingSearchOptions { SectionName = "Messaging:Search"; int StatementTimeoutMs = 2000; }` en `Options/` y en `appsettings.example.json` bajo `"Messaging"`; ver Step 3.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SearchTermsTests"
```

Esperado: `error CS0246 … 'SearchTerms'`.

- [ ] **Step 2: Tokenizador, puerto, handler**

`SearchTerms.cs`:

```csharp
using System.Text;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §7.3, «Cómo se arma @q». Ningún texto de la persona llega a <c>to_tsquery</c>
/// como sintaxis: los tokens pasan por <c>to_tsvector</c> y sólo los lexemas (letras y dígitos) se unen.</summary>
public static class SearchTerms
{
    public const int MaxTokens = 8;
    public const int PrefixMinLength = 3;

    private static readonly HashSet<char> Operators = ['&', '|', '!', ':', '*', '(', ')', '\'', '\\', '<'];

    public static IReadOnlyList<string> Tokenize(string q)
    {
        var tokens = new List<string>();
        foreach (var raw in q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw)
            {
                if (!Operators.Contains(character) && !char.IsControl(character))
                {
                    builder.Append(character);
                }
            }

            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                if (tokens.Count == MaxTokens)
                {
                    break;
                }
            }
        }

        return tokens;
    }

    /// <summary>Cada token con su lexema (<c>null</c> = stop word o sólo símbolos). Prefijo con 3+ caracteres
    /// del <b>token</b>. <c>null</c> si no queda nada que buscar.</summary>
    public static string? Compose(IReadOnlyList<(string Token, string? Lexeme)> terms)
    {
        var parts = new List<string>();
        foreach (var (token, lexeme) in terms)
        {
            if (string.IsNullOrEmpty(lexeme) || !lexeme.All(char.IsLetterOrDigit))
            {
                continue;
            }

            parts.Add(token.Length >= PrefixMinLength ? lexeme + ":*" : lexeme);
        }

        return parts.Count == 0 ? null : string.Join(" & ", parts);
    }
}
```

`IMessageSearch.cs`: la interfaz de «Tipos que cruzan tareas» con `<summary>`: «§7.3/§8.8; `LexemizeAsync` devuelve, por token, el primer lexema de `to_tsvector('messaging.es_unaccent', token)` o `null`; `SearchAsync` corre con `SET LOCAL statement_timeout` y traduce `57014` a `ValidationException` en `q` (P8)».

`SearchMessages.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record SearchMessagesQuery(
    Guid TenantId, string? Q, Guid? ConversationId, DateTimeOffset? From, DateTimeOffset? To, int? Limit, Guid? Before) : IQuery<SearchPageDto>;

public sealed class SearchMessagesValidator : AbstractValidator<SearchMessagesQuery>
{
    public SearchMessagesValidator()
    {
        RuleFor(query => query.Q)
            .Must(q => q is not null && q.Trim().Length is >= 2 and <= 100 && !q.Contains('\0'))
            .WithName("q").WithMessage("q must have between 2 and 100 characters.");
        RuleFor(query => query.ConversationId).NotEqual(Guid.Empty).When(query => query.ConversationId is not null).WithName("conversationId");
        RuleFor(query => query.To).GreaterThanOrEqualTo(query => query.From!.Value).When(query => query.From is not null && query.To is not null)
            .WithName("to").WithMessage("to must be on or after from.");
        RuleFor(query => query.Limit).InclusiveBetween(1, 100).When(query => query.Limit is not null).WithName("limit");
        RuleFor(query => query.Before).NotEqual(Guid.Empty).When(query => query.Before is not null).WithName("before");
    }
}

/// <summary>Spec 2026-10-09 §8.8 (historia 17).</summary>
public sealed class SearchMessagesHandler(
    IMessageSearch search,
    IConversationQueries conversations,
    ConversationSummaryBuilder summaries,
    IMessagingMemberNames memberNames,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<SearchMessagesQuery> validator)
    : IQueryHandler<SearchMessagesQuery, SearchPageDto>
{
    public const int DefaultLimit = 50;

    public async Task<SearchPageDto> HandleAsync(SearchMessagesQuery query, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        if (query.ConversationId is { } conversationId)
        {
            _ = await conversations.FindAsync(query.TenantId, conversationId, cancellationToken) ?? throw MessagingNotFound.Conversation(conversationId);
        }

        MessageCursor? cursor = null;
        if (query.Before is { } before)
        {
            cursor = await search.FindCursorAsync(query.TenantId, before, cancellationToken) ?? throw MessagingNotFound.Message(before);
        }

        var tokens = SearchTerms.Tokenize(query.Q!);
        var lexemes = tokens.Count == 0 ? [] : await search.LexemizeAsync(tokens, cancellationToken);
        var tsQuery = SearchTerms.Compose(tokens.Select((token, index) => (token, lexemes[index])).ToArray());
        if (tsQuery is null)
        {
            return new SearchPageDto([], false);
        }

        var limit = query.Limit ?? DefaultLimit;
        var rows = await search.SearchAsync(query.TenantId, tsQuery, query.ConversationId, query.From, query.To, cursor, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToArray();

        var conversationRows = await conversations.FindManyAsync(query.TenantId, page.Select(row => row.ConversationId).Distinct().ToArray(), cancellationToken);
        var summaryById = (await summaries.BuildAsync(query.TenantId, conversationRows, cancellationToken)).ToDictionary(summary => summary.Id);
        var names = await memberNames.FindAsync(query.TenantId, page.Where(row => row.SentByMemberId is not null).Select(row => row.SentByMemberId!.Value).Distinct().ToArray(), cancellationToken);

        return new SearchPageDto(page.Select(row =>
        {
            var message = MessageMapping.ToDto(row, query.TenantId, names);
            var summary = summaryById[row.ConversationId];
            return new MessageHitDto(
                message.Id, message.Direction, message.Kind, message.Text, message.Media, message.Location, message.Status, message.FailureReason,
                message.At, message.SentBy, message.ClientId, row.ConversationId, summary.Contact, summary.Customer, summary.ConnectionName);
        }).ToArray(), hasMore);
    }
}
```

- [ ] **Step 3: Infraestructura y endpoint**

`MessagingSearchOptions.cs` (en `Options/`): `SectionName = "Messaging:Search"`, `int StatementTimeoutMs { get; set; } = 2000;`; bind en el registro; `appsettings.example.json`: `"Search": { "StatementTimeoutMs": 2000 }` bajo `"Messaging"`.

`MessageSearch.cs`:

```csharp
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.3 y §8.8: la consulta literal con <c>SET LOCAL statement_timeout</c> en la misma
/// transacción, <c>LIMIT limit + 1</c>; el <c>57014</c> es 422 en <c>q</c>, nunca 500 (P8).</summary>
internal sealed class MessageSearch(MessagingDbContext dbContext, IOptions<MessagingSearchOptions> options) : IMessageSearch
{
    public const string TimeoutMessage = "Busca con una palabra más específica o acota las fechas";

    public async Task<IReadOnlyList<string?>> LexemizeAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        var array = tokens.ToArray();
        // Cada token por to_tsvector; el primer lexema del resultado ('drogueri':1 → drogueri).
        var rows = await dbContext.Database.SqlQuery<LexemeRow>(
            $"""
            SELECT t.ordinality AS "Ordinal",
                   (regexp_match(to_tsvector('messaging.es_unaccent', t.token)::text, '''([^'']+)'''))[1] AS "Lexeme"
            FROM unnest({array}) WITH ORDINALITY AS t(token, ordinality)
            """).ToListAsync(cancellationToken);
        var result = new string?[array.Length];
        foreach (var row in rows)
        {
            result[row.Ordinal - 1] = row.Lexeme;
        }

        return result;
    }

    public Task<MessageCursor?> FindCursorAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && message.Id == messageId)
            .Select(message => new MessageCursor(message.OccurredAt, message.Id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MessageRow>> SearchAsync(
        Guid tenantId, string tsQuery, Guid? conversationId, DateTimeOffset? from, DateTimeOffset? to, MessageCursor? before, int take, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var timeout = Math.Max(1, options.Value.StatementTimeoutMs);
            await dbContext.Database.ExecuteSqlRawAsync($"SET LOCAL statement_timeout = '{timeout}ms'", cancellationToken);
            var cursorAt = before?.OccurredAt;
            var cursorId = before?.Id;
            var rows = await dbContext.Messages
                .FromSql($"""
                    SELECT * FROM messaging.messages
                    WHERE tenant_id = {tenantId}
                      AND search_vector @@ to_tsquery('messaging.es_unaccent', {tsQuery})
                      AND ({conversationId} IS NULL OR conversation_id = {conversationId})
                      AND ({from} IS NULL OR occurred_at >= {from})
                      AND ({to} IS NULL OR occurred_at <= {to})
                      AND ({cursorAt} IS NULL OR (occurred_at, id) < ({cursorAt}, {cursorId}))
                    ORDER BY occurred_at DESC, id DESC
                    LIMIT {take}
                    """)
                .AsNoTracking()
                .Include(message => message.Media)
                .OrderByDescending(message => message.OccurredAt).ThenByDescending(message => message.Id)
                .Select(MessageQueries.RowProjection)
                .ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rows;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new ValidationException([new ValidationFailure("q", TimeoutMessage)]);
        }
    }

    private sealed class LexemeRow
    {
        public long Ordinal { get; set; }

        public string? Lexeme { get; set; }
    }
}
```

(Si Npgsql no infiere el tipo de los parámetros `NULL` (`{conversationId} IS NULL`), castéalos en el SQL: `{conversationId}::uuid`, `{from}::timestamptz`, `{cursorAt}::timestamptz`, `{cursorId}::uuid`. `Include` sobre `FromSql` funciona porque la proyección es de la entidad; si no, lee `Media` con una segunda consulta por `id = ANY`.)

`MessagingEndpoints.cs`:

```csharp
        group.MapGet("/messages/search", SearchMessagesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<SearchPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
…
    private static async Task<IResult> SearchMessagesAsync(
        Guid tenantId, string? q, Guid? conversationId, DateTimeOffset? from, DateTimeOffset? to, int? limit, Guid? before,
        IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new SearchMessagesQuery(tenantId, q, conversationId, from, to, limit, before), cancellationToken));
```

Registro: `services.AddScoped<IMessageSearch, MessageSearch>();` y `services.AddScoped<IQueryHandler<SearchMessagesQuery, SearchPageDto>, SearchMessagesHandler>();`.

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~SearchTermsTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~MessageSearchApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~ConfigurationExampleTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: todas correctas (la del timeout puede quedar en `Skipped`; se anota).

- [ ] **Step 5: Formato y commit**

```text
feat(messaging): búsqueda full-text en el historial con tsquery armada en el servidor y timeout acotado
```

---

### Task 16: Envío de texto — cliente de Graph, idempotencia y mapa de fallas

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IOutboundMessages.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs`
- Modify: `MessagingEndpoints.cs`, `MessagingInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`, `MessagingApiHarness.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SendMessageHandlerTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendMessageApiTests.cs`

**Interfaces:**
- Consumes: `IConversationQueries.FindAsync`, `IMessagingConnectionDirectory.ResolveSenderAsync/ReportRejectedAsync`, `IMembershipDirectory.FindActiveMembershipIdAsync`, `IMessagingMemberNames`, `MessagingMetaOptions.GraphApiVersion`.
- Produces: `IWhatsAppCloudClient`, `SendTextResult`, `SendOutcome`, `MediaInfo`, `GraphResult<T>` (copia en Messaging.Application: `MessagingGraphResult<T>`/`MessagingGraphFailure`, porque no se referencia Integrations), `IOutboundMessages`, `IOutboundClaim`, `OutboundDraft`, `ExistingOutbound`, `SendMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, string? Text) : ICommand<MessageDto>`, `SendMessageValidator`, `SendMessageHandler`, `WhatsAppCloudClient.HttpClientName = "messaging.meta-graph"`, ruta `POST /messaging/conversations/{id}/messages`.

- [ ] **Step 1: Pruebas RED del handler (dobles)**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/SendMessageHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.3: el orden de los chequeos y la tabla de respuestas de Meta, con dobles.
/// Review Focus 2: un \0 en el texto es 422 en text.</summary>
public sealed class SendMessageHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AHappySendInsertsCallsMetaAndCommitsSentWithTheWamid()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.out", null, null);

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí, tenemos 12 unidades."), Ct);

        Assert.Equal("Sent", message.Status);
        Assert.Equal(bed.ClientId, message.ClientId);
        Assert.Equal("Outbound", message.Direction);
        Assert.Equal(bed.MemberId, message.SentBy!.MemberId);
        var send = Assert.Single(bed.Meta.Sends);
        Assert.Equal(("111", conversation.WaId, "Sí, tenemos 12 unidades.", $"qep:{message.Id}"), (send.Sender.PhoneNumberId, send.To, send.Body, send.CallbackData));
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("committed-sent:wamid.out", claim.Outcome);
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheExistingMessageWithoutCallingMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 30); // ventana cerrada: no importa, ya salió.
        bed.Outbound.Existing = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Delivered, bed.Now.AddMinutes(-5), bed.MemberId, "la primera vez");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "otra vez"), Ct);

        Assert.Equal(bed.Outbound.Existing.Id, message.Id);
        Assert.Equal("Delivered", message.Status);
        Assert.Equal("la primera vez", message.Text);
        Assert.Empty(bed.Meta.Sends);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Fact]
    public async Task AFailedMessageIsResentOnTheSameRow()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var existingId = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(existingId, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "de nuevo");
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.again", null, null);

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "de nuevo"), Ct);

        Assert.Equal(existingId, message.Id);
        Assert.Equal("Sent", message.Status);
        Assert.Single(bed.Meta.Sends);
    }

    [Theory]
    [InlineData(190, MessagingErrorCodes.ConnectionUnavailable, "rolled-back", "token_expired")]
    [InlineData(133010, MessagingErrorCodes.ConnectionUnavailable, "rolled-back", "number_unregistered")]
    [InlineData(131047, MessagingErrorCodes.WindowClosed, "committed-failed:131047", null)]
    [InlineData(131026, MessagingErrorCodes.MessageRejected, "committed-failed:131026", null)]
    public async Task EveryGraphErrorHasItsCodeRowAndReport(int graphCode, string expectedCode, string expectedClaim, string? reported)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, graphCode, "title");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedClaim, Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal(reported is null ? [] : [reported], bed.Connections.Reported.Select(report => report.FailureCode));
    }

    [Fact]
    public async Task AnUnconfirmedSendIsFailedMinusOne()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Unconfirmed, null, null, null);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        Assert.Equal("committed-failed:-1", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Theory]
    [InlineData("resolved", MessagingErrorCodes.ConversationNotOpen)]
    [InlineData("window-closed", MessagingErrorCodes.WindowClosed)]
    [InlineData("never-wrote", MessagingErrorCodes.WindowClosed)]
    [InlineData("connection-paused", MessagingErrorCodes.ConnectionUnavailable)]
    public async Task TheChecksBeforeMetaHaveTheirCodes(string scenario, string expected)
    {
        var bed = new MessagingTestBed();
        var conversation = scenario switch
        {
            "resolved" => bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true),
            "window-closed" => bed.OpenConversation(lastInboundHoursAgo: 25),
            "never-wrote" => bed.OpenConversation(lastInboundHoursAgo: null),
            _ => bed.OpenConversation(lastInboundHoursAgo: 1),
        };
        if (scenario == "connection-paused")
        {
            bed.Connections.Sender = null;
        }

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(expected, error.Code);
        Assert.Empty(bed.Meta.Sends);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("   ", "text")]
    [InlineData("hola\u0000", "text")]
    public async Task ATextWithANullByteOrEmptyIsAValidationError(string text, string key)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, text), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == key);
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task ATextOver4096OrAMissingClientIdIsAValidationError()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var tooLong = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, new string('x', 4097)), Ct));
        var noClient = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, null, "x"), Ct));

        Assert.Contains(tooLong.Errors, failure => failure.PropertyName == "text");
        Assert.Contains(noClient.Errors, failure => failure.PropertyName == "clientId");
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenAndAMissingConversationIsNotFound()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SendHandler(tenantId: Guid.CreateVersion7()).HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, Guid.CreateVersion7(), bed.ClientId, "x"), Ct));
        Assert.Equal(MessagingErrorCodes.ConversationNotFound, missing.Code);
    }
}
```

`MessagingTestBed` (nuevo, `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs`): dobles en memoria —`FakeExecutionContext`, `FakeTenantModules`, `FakeMembershipDirectory`, `FakeClock` (copias de los de Integrations), `InMemoryConversationQueries` (lista de `ConversationRow`; `OpenConversation(lastInboundHoursAgo, resolved)` agrega una fila con `LastInboundAt = Now - hours`), `FakeConnectionDirectory` (`Sender` = `new MessagingSender("111", "token")`, `Reported` lista), `FakeWhatsAppClient` (`NextSend`, `Sends` con `(Sender, To, Body, CallbackData)`), `FakeOutboundMessages` (`Existing`; cada `ClaimAsync` crea un `FakeClaim` con `Inserted = Existing is null`, `MessageId = Existing?.Id ?? draft.MessageId`; `CommitSentAsync` → `Outcome = "committed-sent:" + wamid`; `CommitFailedAsync` → `"committed-failed:" + code`; `RollbackAsync` → `"rolled-back"`; `DisposeAsync` hace rollback si no hubo commit), `FakeMemberNames` (devuelve `"Andrés"` para `MemberId`), y `SendHandler(Guid? tenantId = null)` que construye `SendMessageHandler` con `new SendMessageValidator()`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SendMessageHandlerTests"
```

Esperado: `error CS0246 … 'SendMessageCommand'`.

- [ ] **Step 2: Puertos y handler**

`IWhatsAppCloudClient.cs`:

```csharp
namespace Modules.Messaging.Application;

public enum SendOutcome
{
    Sent,
    GraphError,
    Unconfirmed,
}

/// <summary>§8.3: 2xx con <c>messages[0].id</c>; un error de Graph con su <c>code</c> y <c>title</c>; o nada
/// confirmado (timeout, 5xx, red).</summary>
public sealed record SendTextResult(SendOutcome Outcome, string? Wamid, int? Code, string? Title);

public sealed record MessagingGraphFailure(int HttpStatus, int? Code, string Reason);

public sealed record MessagingGraphResult<T>(T? Value, MessagingGraphFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record MediaInfo(Uri Url, string MimeType, string? Sha256, long FileSize);

/// <summary>Decisión 3: el cliente de Graph de Messaging (<c>messaging.meta-graph</c>). Nunca registra el token ni la URL firmada.</summary>
public interface IWhatsAppCloudClient
{
    Task<SendTextResult> SendTextAsync(MessagingSender sender, string to, string body, string callbackData, CancellationToken cancellationToken);

    /// <summary>§8.4: best effort, 5 s. <c>true</c> si Meta respondió 2xx.</summary>
    Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 2: GET /{mediaId} → url (vence a los 5 min), mime_type, sha256, file_size.</summary>
    Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 3: GET url con Authorization: Bearer, en stream.</summary>
    Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken);
}
```

`IOutboundMessages.cs`: los tipos de «Tipos que cruzan tareas» con este `<summary>` en `IOutboundClaim`:

```csharp
/// <summary>
/// §8.3, «Idempotencia sin que la fila a medio enviar se vea»: una transacción abierta con la fila del
/// mensaje insertada (o reclamada con FOR UPDATE si ya existía con ese clientId). Un segundo request con
/// el mismo clientId espera en el INSERT hasta que esta transacción termine. Se cierra con uno de los
/// tres: commit Sent (con wamid y foto de la conversación), commit Failed, o rollback (nada se guarda).
/// Desecharlo sin cerrar es rollback.
/// </summary>
```

`SendMessage.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record SendMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, string? Text) : ICommand<MessageDto>;

/// <summary>§6.4: text no vacío tras Trim, ≤ 4096, sin \0; clientId GUID no vacío.</summary>
public sealed class SendMessageValidator : AbstractValidator<SendMessageCommand>
{
    public const int TextMaxLength = 4096;

    public SendMessageValidator()
    {
        RuleFor(command => command.Text)
            .Must(text => text is not null && text.Trim().Length is > 0 and <= TextMaxLength && !text.Contains('\0'))
            .WithName("text").WithMessage("Escribe un mensaje de hasta 4096 caracteres.");
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).WithName("clientId");
    }
}

/// <summary>Spec 2026-10-09 §8.3, en este orden: validador → tenant, permiso y módulo → conversación del
/// tenant → idempotencia → status Open → ventana → conexión Active → Meta → tabla de respuestas.</summary>
public sealed class SendMessageHandler(
    IConversationQueries conversations,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SendMessageCommand> validator)
    : ICommandHandler<SendMessageCommand, MessageDto>
{
    public const string CallbackPrefix = "qep:";

    public async Task<MessageDto> HandleAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");

        var text = command.Text!.Trim();
        var now = clock.UtcNow;
        var draft = new OutboundDraft(Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, text, member, now);
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);

        // Idempotencia (decisión 11): lo que ya salió, ya salió, antes de mirar ventana o estado.
        if (!claim.Inserted && claim.Existing is { } existing && existing.Status != MessageStatus.Failed)
        {
            await claim.RollbackAsync(cancellationToken);
            return await ExistingAsync(existing, command, cancellationToken);
        }

        if (conversation.Status != ConversationStatus.Open)
        {
            await claim.RollbackAsync(cancellationToken);
            throw new MessagingDomainException(MessagingErrorCodes.ConversationNotOpen, "The conversation is resolved; reopen it to answer.");
        }

        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            await claim.RollbackAsync(cancellationToken);
            throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "More than 24 hours passed since the person last wrote.");
        }

        var sender = await connections.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, cancellationToken);
        if (sender is null)
        {
            await claim.RollbackAsync(cancellationToken);
            throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        }

        var result = await meta.SendTextAsync(sender, conversation.WaId, text, CallbackPrefix + claim.MessageId.ToString("D"), cancellationToken);
        var sentAt = clock.UtcNow;
        switch (result.Outcome)
        {
            case SendOutcome.Sent when !string.IsNullOrEmpty(result.Wamid):
                await claim.CommitSentAsync(result.Wamid, sentAt, cancellationToken);
                return new MessageDto(
                    claim.MessageId, nameof(MessageDirection.Outbound), nameof(MessageKind.Text), text, null, null, nameof(MessageStatus.Sent), null, sentAt,
                    new SentByDto(member, (await memberNames.FindAsync(command.TenantId, [member], cancellationToken)).GetValueOrDefault(member)), command.ClientId);

            case SendOutcome.GraphError when result.Code is 190 or 133010:
                await claim.RollbackAsync(cancellationToken);
                await connections.ReportRejectedAsync(command.TenantId, conversation.ConnectionId, result.Code == 190 ? "token_expired" : "number_unregistered", cancellationToken);
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials.");

            case SendOutcome.GraphError when result.Code is 131047:
                await claim.CommitFailedAsync(131047, result.Title, sentAt, cancellationToken);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Meta closed the 24-hour window.");

            case SendOutcome.GraphError:
                await claim.CommitFailedAsync(result.Code ?? 0, result.Title, sentAt, cancellationToken);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "Meta rejected the message.");

            default:
                await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, sentAt, cancellationToken);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "The send could not be confirmed with Meta.");
        }
    }

    // Idempotencia: se devuelve el mensaje guardado, con su texto, no el del request.
    private async Task<MessageDto> ExistingAsync(ExistingOutbound existing, SendMessageCommand command, CancellationToken cancellationToken)
    {
        var names = existing.SentByMemberId is { } member
            ? await memberNames.FindAsync(command.TenantId, [member], cancellationToken)
            : new Dictionary<Guid, string>();
        return new MessageDto(
            existing.Id, nameof(MessageDirection.Outbound), nameof(MessageKind.Text), existing.Text, null, null, existing.Status.ToString(), null, existing.OccurredAt,
            existing.SentByMemberId is { } sentBy ? new SentByDto(sentBy, names.GetValueOrDefault(sentBy)) : null, command.ClientId);
    }
}
```

(En el handler, la llamada es `return await ExistingAsync(existing, command, cancellationToken);`.)

- [ ] **Step 3: Verificar GREEN (unitarias), luego infraestructura**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SendMessageHandlerTests"
```

Esperado: todas correctas.

`WhatsAppCloudClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Meta;

/// <summary>Decisión 3: <c>messaging.meta-graph</c> con el patrón de <c>ZenviaConnectionTester</c>. Registra
/// status, <c>error.code</c>, <c>error_subcode</c> y <c>fbtrace_id</c>; nunca el token, el cuerpo ni la URL firmada.</summary>
internal sealed partial class WhatsAppCloudClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MessagingMetaOptions> options,
    ILogger<WhatsAppCloudClient> logger) : IWhatsAppCloudClient
{
    public const string HttpClientName = "messaging.meta-graph";
    public const string BaseUrl = "https://graph.facebook.com/";
    public static readonly TimeSpan ReadReceiptTimeout = TimeSpan.FromSeconds(5);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph {Operation} answered HTTP {StatusCode} (code {Code}, subcode {Subcode}, fbtrace {FbTraceId})")]
    private static partial void LogAnswered(ILogger logger, string operation, int statusCode, int? code, int? subcode, string? fbTraceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graph {Operation} could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-messaging");
    }

    private string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion) ? "v24.0" : options.Value.GraphApiVersion.Trim();

    public async Task<SendTextResult> SendTextAsync(MessagingSender sender, string to, string body, string callbackData, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to,
                type = "text",
                text = new { body },
                biz_opaque_callback_data = callbackData,
            }),
        };
        var (response, reason) = await SendAsync("send", request, sender.AccessToken, null, cancellationToken);
        if (reason is not null || response is null)
        {
            return new SendTextResult(SendOutcome.Unconfirmed, null, null, null);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                return new SendTextResult(SendOutcome.Unconfirmed, null, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = GraphError.TryParse(text);
                return new SendTextResult(SendOutcome.GraphError, null, error?.Code, error?.Title);
            }

            return new SendTextResult(SendOutcome.Sent, ReadWamid(text), null, null);
        }
    }

    public async Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", status = "read", message_id = wamid }),
        };
        var (response, _) = await SendAsync("read", request, sender.AccessToken, ReadReceiptTimeout, cancellationToken);
        using (response)
        {
            return response?.IsSuccessStatusCode == true;
        }
    }

    public async Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Version}/{mediaId}");
        var (response, reason) = await SendAsync("media", request, sender.AccessToken, null, cancellationToken);
        if (reason is not null || response is null)
        {
            return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(0, null, reason ?? "unreachable"));
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure((int)response.StatusCode, GraphError.TryParse(text)?.Code, $"http_{(int)response.StatusCode}"));
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var url = root.GetProperty("url").GetString();
                return url is null
                    ? new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(200, null, "no_url"))
                    : new MessagingGraphResult<MediaInfo>(new MediaInfo(
                        new Uri(url, UriKind.Absolute),
                        root.TryGetProperty("mime_type", out var mime) ? mime.GetString() ?? "application/octet-stream" : "application/octet-stream",
                        root.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                        root.TryGetProperty("file_size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0), null);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or UriFormatException)
            {
                return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(200, null, "unreadable"));
            }
        }
    }

    public async Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken)
    {
        // La URL ya es absoluta y firmada (vence a los 5 minutos); mismo cliente, sin redirecciones.
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sender.AccessToken);
        var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Media download answered HTTP {status}.");
        }

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private async Task<(HttpResponseMessage? Response, string? UnreachableReason)> SendAsync(
        string operation, HttpRequestMessage request, string accessToken, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            cts.CancelAfter(limit);
        }

        try
        {
            var response = await client.SendAsync(request, cts.Token);
            var error = response.IsSuccessStatusCode ? null : GraphError.TryParse(await response.Content.ReadAsStringAsync(cancellationToken));
            LogAnswered(logger, operation, (int)response.StatusCode, error?.Code, error?.Subcode, error?.FbTraceId);
            return (response, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, operation, "timeout");
            return (null, "timeout");
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, operation, "network");
            return (null, "network");
        }
    }

    private static string? ReadWamid(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0
                ? messages[0].GetProperty("id").GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Copia chica de <c>MetaGraphError</c> de Integrations (no se referencia): código, subcódigo, título y fbtrace; nunca <c>message</c>.</summary>
    private sealed record GraphError(int? Code, int? Subcode, string? Title, string? FbTraceId)
    {
        public static GraphError? TryParse(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                return new GraphError(
                    error.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) ? c : null,
                    error.TryGetProperty("error_subcode", out var sub) && sub.TryGetInt32(out var s) ? s : null,
                    error.TryGetProperty("error_user_title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()
                        : error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null,
                    error.TryGetProperty("fbtrace_id", out var trace) && trace.ValueKind == JsonValueKind.String ? trace.GetString() : null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
```

(`ReadAsStringAsync` dentro de `SendAsync` para el log consume el cuerpo una vez; en `SendTextAsync` se vuelve a leer: `HttpContent` lo bufferiza, así que la segunda lectura devuelve lo mismo. Si no, guarda el texto en la tupla.)

`OutboundMessages.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§8.3: la transacción mantiene un candado sobre <b>una</b> fila de mensaje mientras Meta responde
/// (≤ 10 s); bloquea sólo a otro request con el mismo clientId, nunca la conversación.</summary>
internal sealed class OutboundMessages(MessagingDbContext dbContext) : IOutboundMessages
{
    public async Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, client_id, sent_by_member_id, created_at)
            VALUES ({draft.MessageId}, {draft.ConversationId}, {draft.TenantId}, {draft.ConnectionId}, {draft.Now}, 2, 1, 1, {draft.Text}, {draft.ClientId}, {draft.SentByMemberId}, {draft.Now})
            ON CONFLICT (conversation_id, client_id) WHERE client_id IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (inserted.Count == 1)
        {
            return new Claim(dbContext, transaction, draft, inserted: true, existing: null);
        }

        var existing = await dbContext.Database.SqlQuery<ExistingRow>(
            $"""
            SELECT id AS "Id", status AS "Status", occurred_at AS "OccurredAt", sent_by_member_id AS "SentByMemberId", coalesce(text, '') AS "Text"
            FROM messaging.messages WHERE conversation_id = {draft.ConversationId} AND client_id = {draft.ClientId} FOR UPDATE
            """).SingleAsync(cancellationToken);
        return new Claim(dbContext, transaction, draft, inserted: false,
            existing: new ExistingOutbound(existing.Id, MessageColumnCodes.ToStatus(existing.Status), existing.OccurredAt, existing.SentByMemberId, existing.Text));
    }

    private sealed class ExistingRow
    {
        public Guid Id { get; set; }
        public short Status { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public Guid? SentByMemberId { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private sealed class Claim(MessagingDbContext dbContext, IDbContextTransaction transaction, OutboundDraft draft, bool inserted, ExistingOutbound? existing) : IOutboundClaim
    {
        private bool _closed;

        public bool Inserted => inserted;

        public Guid MessageId => existing?.Id ?? draft.MessageId;

        public ExistingOutbound? Existing => existing;

        public async Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            // Sent con wamid y la foto de la conversación (§8.3): sólo last_activity_at y last_message_*;
            // no sube version: el envío no es una edición que una persona pueda pisar.
            var preview = draft.Text.Length <= Conversation.PreviewMaxLength ? draft.Text : draft.Text[..Conversation.PreviewMaxLength];
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 1, wamid = {wamid}, occurred_at = {occurredAt}, text = {draft.Text}, failure_code = NULL, failure_title = NULL, sent_by_member_id = {draft.SentByMemberId}
                WHERE id = {MessageId}
                """, cancellationToken);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.conversations SET
                    last_activity_at = GREATEST(last_activity_at, {occurredAt}),
                    last_message_id = {MessageId}, last_message_direction = 2, last_message_kind = 1,
                    last_message_preview = {preview}, last_message_status = 1, last_message_at = {occurredAt}
                WHERE id = {draft.ConversationId}
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _closed = true;
        }

        public async Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 4, failure_code = {failureCode}, failure_title = {failureTitle}, occurred_at = {occurredAt}, text = {draft.Text}
                WHERE id = {MessageId}
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _closed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (!_closed)
            {
                await transaction.RollbackAsync(cancellationToken);
                _closed = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
        }
    }
}
```

Endpoint en `MessagingEndpoints.cs`:

```csharp
        group.MapPost("/conversations/{conversationId:guid}/messages", SendMessageAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<SendMessageRequest>("application/json")
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
…
    private static async Task<IResult> SendMessageAsync(
        Guid tenantId, Guid conversationId, SendMessageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var message = await dispatcher.SendAsync(new SendMessageCommand(tenantId, conversationId, request.ClientId, request.Text), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/messages", message);
    }

public sealed record SendMessageRequest(Guid? ClientId, string? Text);
```

Registro en `MessagingInfrastructureExtensions.cs`:

```csharp
        services.AddHttpClient(WhatsAppCloudClient.HttpClientName, WhatsAppCloudClient.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => WhatsAppCloudClient.CreatePrimaryHandler())
            .RemoveAllLoggers();
        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();
        services.AddScoped<IOutboundMessages, OutboundMessages>();
```

y en `QepServiceCollectionExtensions.cs`: `services.AddScoped<ICommandHandler<SendMessageCommand, MessageDto>, SendMessageHandler>();`.

- [ ] **Step 4: Pruebas de integración**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendMessageApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.3 por HTTP y contra la base: 201 con Sent y clientId; dos envíos concurrentes
/// con el mismo clientId → una llamada a Meta y el mismo mensaje; un Failed se reenvía; 190 → NeedsAttention
/// + connection_unavailable; 131047 → window_closed; timeout → -1; la fuga del token.</summary>
public sealed class SendMessageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, connectionString, tenant, conversationId, client);
    }

    private static void ScriptSendOk(FakeMetaGraphHandler meta, string wamid = "wamid.out") =>
        meta.Respond("/111/messages", HttpStatusCode.OK, $$"""{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"{{wamid}}"}]}""");

    [Fact]
    public async Task AHappySendAnswers201SentWithTheClientIdAndUpdatesTheSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = " Sí, tenemos 12 unidades. " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Sent", message.GetProperty("status").GetString());
        Assert.Equal(clientId, message.GetProperty("clientId").GetGuid());
        Assert.Equal("Sí, tenemos 12 unidades.", message.GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(message.GetProperty("sentBy").GetProperty("displayName").GetString()));
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", send.Authorization);
        Assert.Contains($"\"biz_opaque_callback_data\":\"qep:{message.GetProperty("id").GetGuid()}\"", send.Body, StringComparison.Ordinal);
        Assert.Equal("1|wamid.out|2|1", await ScalarAsync<string>(f.ConnectionString, "SELECT m.status || '|' || m.wamid || '|' || c.last_message_direction || '|' || c.last_message_status FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.direction = 2"));
        var list = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);
        Assert.Equal("Outbound", list.GetProperty("items")[0].GetProperty("lastMessage").GetProperty("direction").GetString());
    }

    [Fact]
    public async Task TwoConcurrentSendsWithTheSameClientIdCallMetaOnceAndAnswerTheSameMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.RespondWith("/111/messages", _ =>
        {
            Thread.Sleep(300);
            return FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.once"}]}""");
        });
        var clientId = Guid.CreateVersion7();
        var body = new { clientId, text = "una vez" };

        var responses = await Task.WhenAll(
            SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), body),
            SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), body));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        }

        Assert.Equal(ids[0], ids[1]);
        Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Theory]
    [InlineData(401, 190, "messaging.connection_unavailable", "NeedsAttention|token_expired", 0)]
    [InlineData(400, 133010, "messaging.connection_unavailable", "NeedsAttention|number_unregistered", 0)]
    [InlineData(400, 131047, "messaging.window_closed", "Active|-", 1)]
    [InlineData(400, 131026, "messaging.message.rejected", "Active|-", 1)]
    public async Task AGraphErrorAnswersItsCodeRowAndConnectionState(int status, int code, string expectedCode, string expectedConnection, long expectedRows)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", (HttpStatusCode)status, FakeMetaGraphHandler.GraphError(code));

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Equal(expectedConnection, await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections"));
        Assert.Equal(expectedRows, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        if (expectedRows == 1)
        {
            Assert.Equal($"4|{code}", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
        }
    }

    [Fact]
    public async Task AFailedMessageIsResentOnTheSameRowAndATimeoutIsMinusOne()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Throw = new TaskCanceledException("timed out", new TimeoutException());
        var clientId = Guid.CreateVersion7();

        var timeout = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, timeout.StatusCode);
        Assert.Equal("messaging.message.rejected", (await ProblemAsync(timeout)).Code);
        Assert.Equal("4|-1", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));

        f.Factory.MetaHandler.Throw = null;
        ScriptSendOk(f.Factory.MetaHandler, "wamid.retry");
        var retry = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "x" });

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("Sent", (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("1|wamid.retry", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || wamid FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task TheTokenNeverLeaksInResponsesLogsOrFailures()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var logs = new CapturedLogs();
        using var host = f.Factory.WithCapturedLogs(logs);
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, $$"""{"error":{"message":"{{SentinelMetaAccessToken}}","code":100}}""");
        using var client = CreateClient(host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "x" });
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(SentinelMetaAccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelMetaAccessToken, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelMetaAccessToken, await ScalarAsync<string>(f.ConnectionString, "SELECT coalesce(string_agg(message || ' ' || detail, ' '), '') FROM platform.request_failures"), StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Categories, category => category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~SendMessageApiTests|FullyQualifiedName~StatusIngestionTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MessagingLayerTests"
```

Esperado: todas correctas.

- [ ] **Step 5: Formato y commit**

```text
feat(messaging): envío de texto idempotente por clientId con el mapa de respuestas de Meta
```

---

### Task 17: Marcar leído, resolver y reabrir

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingAuditRecorder.cs`
- Modify: `MessagingEndpoints.cs`, `MessagingInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationLifecycleApiTests.cs`

**Interfaces:**
- Consumes: `IConversationRepository`, `IMessagingUnitOfWork`, `IWhatsAppCloudClient.MarkReadAsync`, `Conversation.MarkRead/Resolve/Reopen/CanAcknowledgeReading`, `AuditEntry.Create`.
- Produces: `IMessagingAuditRecorder`, `MessagingAuditActions { ResourceType = "conversation", Resolved = "messaging.conversation.resolved", Reopened = "messaging.conversation.reopened" }`, `MarkConversationReadCommand(Guid TenantId, Guid ConversationId) : ICommand<bool>`, `ResolveConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>`, `ReopenConversationCommand(…)`, handlers, rutas `POST …/read` (204), `POST …/resolve`, `POST …/reopen` (If-Match).

- [ ] **Step 1: Pruebas RED**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationLifecycleApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.4 y §8.5: read deja unreadCount en 0 y manda el acuse a Meta (best effort,
/// una sola vez, sólo con conexión Active); resolve/reopen con If-Match (428/412), sus 422, auditoría en
/// la misma transacción; y la versión que sube la ingesta hace chocar un resolve viejo.</summary>
public sealed class ConversationLifecycleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, long Version, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var item = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0];
        return new Fixture(factory, connectionString, tenant, item.GetProperty("id").GetGuid(), item.GetProperty("version").GetInt64(), client);
    }

    [Fact]
    public async Task ReadZeroesTheCounterAndAcknowledgesToMetaOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");

        var first = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");
        var second = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal("0|2", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
        var ack = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Contains("\"status\":\"read\"", ack.Body, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":\"wamid.in\"", ack.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadIsStill204WhenMetaFailsOrTheConnectionIsPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Throw = new HttpRequestException("down");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT unread_count FROM messaging.conversations"));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 2");
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        f.Factory.MetaHandler.Throw = null;
        f.Factory.MetaHandler.Requests.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    [Fact]
    public async Task ResolveAndReopenFollowIfMatchWithTheirCodesAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);

        var without = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve");
        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: "\"999\"");
        var resolved = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{f.Version}\"");
        var summary = await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var again = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{summary.GetProperty("version").GetInt64()}\"");
        var reopened = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{summary.GetProperty("version").GetInt64()}\"");
        var reopenedSummary = await reopened.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var openAgain = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{reopenedSummary.GetProperty("version").GetInt64()}\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(without)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("Resolved", summary.GetProperty("status").GetString());
        Assert.Equal(f.Version + 1, summary.GetProperty("version").GetInt64());
        Assert.Equal("Ventas", summary.GetProperty("connectionName").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("messaging.conversation.already_resolved", (await ProblemAsync(again)).Code);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal("Open", reopenedSummary.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, openAgain.StatusCode);
        Assert.Equal("messaging.conversation.already_open", (await ProblemAsync(openAgain)).Code);
        Assert.Equal("messaging.conversation.reopened,messaging.conversation.resolved", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(action, ',' ORDER BY action) FROM audit.entries WHERE source = 'messaging' AND resource_type = 'conversation' AND resource_id = @id", ("id", f.ConversationId.ToString())));
    }

    // §7.5: la ingesta sube version; un resolve con la versión vieja es 412.
    [Fact]
    public async Task AnInboundThatArrivesAfterLoadingMakesTheResolveConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra"));
        await DrainDeliveriesAsync(f.Factory);

        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: $"\"{f.Version}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task ReadResolveAndReopenRejectOtherTenantsAndTheReadPermissionAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var readOnly = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(other.TenantId, f.ConversationId)}/resolve", ifMatch: "\"1\"")).StatusCode);
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationLifecycleApiTests"
```

Esperado: compila y falla con 404 en `/read`, `/resolve`, `/reopen` (no hay ruta). Copia la salida.

- [ ] **Step 2: Auditoría, handlers, endpoints**

`IMessagingAuditRecorder.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>§6.4: auditoría atómica propia (proyección de audit.entries en MessagingDbContext, como
/// Integrations). No el IAuditRecorder compartido, ligado a TenancyDbContext. Un envío no se audita.</summary>
public interface IMessagingAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt);
}

public static class MessagingAuditActions
{
    public const string ResourceType = "conversation";
    public const string Resolved = "messaging.conversation.resolved";
    public const string Reopened = "messaging.conversation.reopened";
}
```

`MessagingAuditRecorder.cs` (Infrastructure): copia de `IntegrationsAuditRecorder` con `source = "messaging"`, `changedFieldsJson = "[]"`, `AuditActorType.Human`, `MessagingAuditActions.ResourceType`, outcome `"success"`.

`ConversationLifecycle.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record MarkConversationReadCommand(Guid TenantId, Guid ConversationId) : ICommand<bool>;

public sealed record ResolveConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ReopenConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

/// <summary>§8.4: commit primero; después el acuse a Meta, best effort (5 s), sólo si había no leídos, hay
/// último entrante de menos de 30 días y la conexión está Active. Siempre 204.</summary>
public sealed partial class MarkConversationReadHandler(
    IConversationRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<MarkConversationReadHandler> logger)
    : ICommandHandler<MarkConversationReadCommand, bool>
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Read receipt for conversation {ConversationId} could not be sent to Meta; the conversation is marked read anyway.")]
    private static partial void LogReceiptFailed(ILogger logger, Guid conversationId, Exception? exception);

    public async Task<bool> HandleAsync(MarkConversationReadCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);

        var now = clock.UtcNow;
        if (!conversation.MarkRead(now))
        {
            return false;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (!conversation.CanAcknowledgeReading(now))
        {
            return true;
        }

        try
        {
            var sender = await connections.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, cancellationToken);
            if (sender is not null && !await meta.MarkReadAsync(sender, conversation.LastInboundWamid!, cancellationToken))
            {
                LogReceiptFailed(logger, conversation.Id, null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReceiptFailed(logger, conversation.Id, exception);
        }

        return true;
    }
}

/// <summary>§8.5: por el agregado con If-Match (428 lo pone el endpoint; 412 lo pone el unit of work).</summary>
public sealed class ResolveConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ResolveConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ResolveConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);

        var now = clock.UtcNow;
        conversation.Resolve(now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Resolved, conversation.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

public sealed class ReopenConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ReopenConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ReopenConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);

        var now = clock.UtcNow;
        conversation.Reopen(now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Reopened, conversation.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

/// <summary>Copia de <c>ConcurrencyGuard</c> de Integrations: la versión del If-Match tiene que ser la vigente.</summary>
internal static class ConversationConcurrency
{
    public static void EnsureVersion(Conversation conversation, long expectedVersion)
    {
        if (conversation.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The conversation changed since it was loaded.");
        }
    }
}
```

(`ILogger` en Application: `Modules.Integrations.Application` no lo usa; agrega `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` a `Modules.Messaging.Application.csproj` si no viene transitivo por `BuildingBlocks.Application`; si hace falta la versión central, añádela a `Directory.Packages.props` con la misma versión `10.0.11` de las demás `Microsoft.Extensions.*` y regenera locks.)

Endpoints (`MessagingEndpoints.cs`), con `RequireVersion` copiado de `IntegrationsEndpoints.cs` (busca `RequireVersion`/`TryParseVersion` en ese archivo y copia los dos métodos literal):

```csharp
        group.MapPost("/conversations/{conversationId:guid}/read", MarkReadAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/conversations/{conversationId:guid}/resolve", ResolveAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/reopen", ReopenAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
…
    private static async Task<IResult> MarkReadAsync(Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new MarkConversationReadCommand(tenantId, conversationId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ResolveAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ResolveConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ReopenAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReopenConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));
```

Registro: `services.AddScoped<IMessagingAuditRecorder, MessagingAuditRecorder>();` (Infrastructure) y los tres handlers en Bootstrapper (`ICommandHandler<MarkConversationReadCommand, bool>`, `ICommandHandler<ResolveConversationCommand, ConversationSummary>`, `ICommandHandler<ReopenConversationCommand, ConversationSummary>`).

- [ ] **Step 3: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~ConversationLifecycleApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todas correctas.

- [ ] **Step 4: Formato y commit**

```text
feat(messaging): marcar leído con acuse a Meta, y resolver o reabrir con If-Match y auditoría
```

---
### Task 18: Medios entrantes — Storage por stream, copia a R2 y servir con la sesión

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IObjectStorage.cs:47-53`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/R2ObjectStorage.cs:99-129`
- Modify: todos los dobles de `IObjectStorage` (`rg -n ": IObjectStorage" tests src`): `UnusedObjectStorage` (×2, Bootstrapper.UnitTests), `InMemoryObjectStorage` (Quotations, Catalog, Customers, Tenancy, Storage…), `SigningObjectStorage`, `UntouchableObjectStorage`, el de `StorageApplicationTestDoubles.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingMediaStore.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMediaReads.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/GetMedia.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MediaReads.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Media/Sha256PassThroughStream.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Media/MediaCopyProcessor.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Media/MediaCopyWorker.cs`
- Create: `src/Bootstrapper/MessagingMediaStore.cs`
- Modify: `MessagingEndpoints.cs`, `MessagingInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`, `MessagingApiHarness.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/Sha256PassThroughStreamTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MediaApiTests.cs`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/R2ObjectStorageTests.cs` (caso para el stream)

**Interfaces:**
- Consumes: `IObjectStorage`, `R2ObjectStorage` (`PutObjectRequest` con `UseChunkEncoding = false`, `DisablePayloadSigning = true`), `IWhatsAppCloudClient.GetMediaAsync/OpenMediaAsync`, `IMessagingConnectionDirectory.ResolveSenderAsync`.
- Produces: `IObjectStorage.UploadAsync(string key, Stream content, long contentLength, string contentType, ct)`, `IObjectStorage.OpenReadAsync(string key, ct) → StoredObjectStream?`, `StoredObjectStream(Stream Content, string ContentType, long Length)`, `IMessagingMediaStore`, `IMediaReads`, `StoredMedia`, `GetMediaQuery(Guid TenantId, Guid MessageId) : IQuery<MediaStreamDto?>`, `GetMediaHandler`, `MediaCopyProcessor.CopyOneAsync(...)`, `MediaCopyWorker.DrainAsync(ct)`, `MediaCopyWorker.Leases`, ruta `GET /messaging/media/{messageId}`.

- [ ] **Step 1: Storage por stream (RED → GREEN)**

En `tests/Modules/Storage/Modules.Storage.UnitTests/R2ObjectStorageTests.cs`, junto a `UploadDisablesChunkEncodingAndPayloadSigning` (mira cómo captura el `PutObjectRequest` con su `IAmazonS3` falso y copia la forma):

```csharp
    // Spec 2026-10-09 §6.6: un medio de WhatsApp puede pesar 100 MB; sube por stream con el largo
    // explícito y las mismas dos banderas que el byte[], porque R2 no implementa el cuerpo firmado en chunks.
    [Fact]
    public async Task UploadByStreamKeepsTheR2FlagsAndTheExplicitLength()
    {
        // 1. Armar R2ObjectStorage con el IAmazonS3 falso del archivo.
        // 2. await storage.UploadAsync("k", new MemoryStream(new byte[10]), 10, "image/jpeg", ct).
        // 3. El PutObjectRequest capturado tiene UseChunkEncoding == false, DisablePayloadSigning == true,
        //    ContentType == "image/jpeg" y Headers.ContentLength == 10.
    }
```

Escribe las tres aserciones con `Assert`. Luego:

`IObjectStorage.cs`, después de `UploadAsync(byte[])`:

```csharp
    // Spec 2026-10-09 §6.6: subida y lectura por stream para lo que no cabe en memoria (un documento de
    // WhatsApp llega a 100 MB). El largo viene de quien llama: con R2 la subida en chunks firmados falla,
    // así que hay que conocerlo antes de subir.
    Task UploadAsync(
        string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken);

    // null si el objeto no está. El stream es del llamador: lo cierra él.
    Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

public sealed record StoredObject(long SizeBytes, string Checksum);

public sealed record StoredObjectStream(Stream Content, string ContentType, long Length);
```

`R2ObjectStorage.cs`:

```csharp
    public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            // Las mismas dos banderas que el byte[] (ver arriba): R2 no implementa el cuerpo firmado en chunks.
            UseChunkEncoding = false,
            DisablePayloadSigning = true,
        };
        request.Headers.ContentLength = contentLength;
        return client.PutObjectAsync(request, cancellationToken);
    }

    public async Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(Bucket, key, cancellationToken);
            return new StoredObjectStream(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
```

Dobles (P12): en cada clase que implementa `IObjectStorage`, agrega los dos miembros. Para los stubs: `throw new NotSupportedException();`. Para `InMemoryObjectStorage` de `QuotationsApiHarness.cs` (y cualquier otro `InMemory`): guarda `(bytes, contentType)` en su diccionario y `OpenReadAsync` devuelve `new StoredObjectStream(new MemoryStream(bytes), contentType, bytes.Length)` o `null`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
rg -n ": IObjectStorage" src tests
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build --filter "FullyQualifiedName~R2ObjectStorageTests"
```

Esperado: cero implementaciones sin los métodos (el build lo dice), y la prueba nueva en verde.

- [ ] **Step 2: Pruebas RED de Messaging**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/Sha256PassThroughStreamTests.cs`:

```csharp
using System.Security.Cryptography;
using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>§8.6, paso 3: el hash se calcula al pasar, sin segunda lectura.</summary>
public sealed class Sha256PassThroughStreamTests
{
    [Fact]
    public async Task TheHashMatchesTheBytesThatWentThrough()
    {
        var bytes = new byte[100_000];
        Random.Shared.NextBytes(bytes);
        await using var inner = new MemoryStream(bytes);
        await using var stream = new Sha256PassThroughStream(inner);
        await using var sink = new MemoryStream();

        await stream.CopyToAsync(sink, 4096, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, sink.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), stream.FinishHex());
        Assert.Equal(bytes.Length, stream.BytesRead);
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MediaApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.6: copia con un almacén falso (sha256 verificado; el que no coincide falla y
/// reintenta), 404 sin código antes de copiar, headers de seguridad al servir, Review Focus 5 (HTML/SVG
/// como attachment), y el medio de otro tenant es 404.</summary>
public sealed class MediaApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>El almacén de prueba: un diccionario, para no tocar R2.</summary>
    private sealed class FakeMediaStore : IMessagingMediaStore
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = (buffer.ToArray(), contentType);
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Objects.TryGetValue(key, out var stored) ? new MediaStreamDto(new MemoryStream(stored.Bytes), stored.ContentType, stored.Bytes.Length, null) : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMediaStore Store, string ConnectionString, RegisteredTenant Tenant, Guid MessageId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string mimeType = "image/jpeg", string? fileName = null, byte[]? bytes = null, bool wrongSha = false)
    {
        bytes ??= Encoding.UTF8.GetBytes("imagen de prueba");
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(store)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        factory.MetaHandler.Respond("/media-1", HttpStatusCode.OK, $$"""{"url":"https://lookaside.test/m/1","mime_type":"{{mimeType}}","sha256":"{{(wrongSha ? "deadbeef" : sha)}}","file_size":{{bytes.Length}},"id":"media-1"}""");
        factory.MetaHandler.RespondWith("lookaside.test", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573001234567", "w1", 1760000000, mimeType.StartsWith("image/", StringComparison.Ordinal) ? "image" : "document", "media-1", mimeType, caption: "leyenda", filename: fileName));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var messageId = (await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, host, store, connectionString, tenant, messageId, client);
    }

    [Fact]
    public async Task BeforeTheCopyItIs404WithoutCodeAndAfterItIsServedWithSecurityHeaders()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;

        var pending = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
        Assert.Empty(await pending.Content.ReadAsStringAsync(Ct));

        await DrainMediaAsync(f.Host);
        var served = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/jpeg", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(16, served.Content.Headers.ContentLength);
        Assert.Equal("inline", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox; default-src 'none'", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("private, max-age=3600", served.Headers.CacheControl?.ToString());
        Assert.Equal("imagen de prueba", await served.Content.ReadAsStringAsync(Ct));
        Assert.Equal($"messaging/{f.Tenant.TenantId}/{f.MessageId}", Assert.Single(f.Store.Objects.Keys));
        Assert.Equal("t|16", await ScalarAsync<string>(f.ConnectionString, "SELECT (stored_at IS NOT NULL)::text || '|' || size_bytes FROM messaging.message_media"));
        var download = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.Host == "lookaside.test");
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", download.Authorization);
    }

    // Review Focus 5.
    [Theory]
    [InlineData("text/html", "page.html")]
    [InlineData("image/svg+xml", "logo.svg")]
    [InlineData("application/pdf", "orden.pdf")]
    public async Task AnHtmlOrSvgMediaIsServedAsAttachmentWithSandbox(string mimeType, string fileName)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, mimeType, fileName, Encoding.UTF8.GetBytes("<svg onload=alert(1)/>"));
        using var _ = f.Factory;
        using var __ = f.Host;
        await DrainMediaAsync(f.Host);

        var served = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("attachment", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains(fileName, served.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Equal("sandbox; default-src 'none'", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task AShaMismatchFailsTheAttemptAndSchedulesARetry()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, wrongSha: true);
        using var _ = f.Factory;
        using var __ = f.Host;

        await DrainMediaAsync(f.Host);

        Assert.Empty(f.Store.Objects);
        Assert.Equal("f|1|sha256_mismatch", await ScalarAsync<string>(f.ConnectionString, "SELECT (stored_at IS NOT NULL)::text || '|' || attempts || '|' || last_error FROM messaging.message_media"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT next_attempt_at > now() FROM messaging.message_media"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct)).StatusCode);
    }

    [Fact]
    public async Task AnotherTenantAndAMessageWithoutMediaAre404WithoutCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        await DrainMediaAsync(f.Host);
        var other = await RegisterTenantAsync(f.Host);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Host, other.OwnerUserId, other.TenantId, ReadPermissions);

        var foreign = await otherClient.GetAsync(MediaUrl(other.TenantId, f.MessageId), Ct);
        var crossTenant = await otherClient.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);
        var random = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(await foreign.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, random.StatusCode);
    }
}
```

En el harness: `DrainMediaAsync(host)` igual que `DrainDeliveriesAsync` sobre `MediaCopyWorker`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~Sha256PassThroughStreamTests"
```

Esperado: `error CS0246 … 'Sha256PassThroughStream'`.

- [ ] **Step 3: Puertos, handler, stream, procesador, worker, adaptador, endpoint**

`IMessagingMediaStore.cs` e `IMediaReads.cs`: las interfaces y records de «Tipos que cruzan tareas», con `<summary>` que citen §6.4 (adaptador en Bootstrapper sobre `IObjectStorage`, bucket privado) y §8.6.

`GetMedia.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>§8.6, «Servir»: null = 404 sin código (medio no copiado, mensaje sin medio o inexistente en el tenant).</summary>
public sealed record GetMediaQuery(Guid TenantId, Guid MessageId) : IQuery<MediaStreamDto?>;

public sealed class GetMediaHandler(
    IMediaReads media,
    IMessagingMediaStore store,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetMediaQuery, MediaStreamDto?>
{
    public async Task<MediaStreamDto?> HandleAsync(GetMediaQuery query, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var stored = await media.FindStoredAsync(query.TenantId, query.MessageId, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var opened = await store.OpenReadAsync(stored.StorageKey, cancellationToken);
        return opened is null ? null : opened with { ContentType = stored.MimeType, FileName = stored.FileName };
    }
}
```

`MediaReads.cs` (Infrastructure):

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

internal sealed class MediaReads(MessagingDbContext dbContext) : IMediaReads
{
    public Task<StoredMedia?> FindStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && message.Id == messageId && message.Media != null && message.Media.StoredAt != null && message.Media.StorageKey != null)
            .Select(message => new StoredMedia(message.Media!.StorageKey!, message.Media.MimeType, message.Media.FileName, message.Media.SizeBytes))
            .SingleOrDefaultAsync(cancellationToken);
}
```

`Sha256PassThroughStream.cs`:

```csharp
using System.Security.Cryptography;

namespace Modules.Messaging.Infrastructure.Media;

/// <summary>§8.6: un stream de sólo lectura que acumula el SHA-256 de lo que deja pasar, para subir y
/// verificar en una sola pasada sin tener el medio en memoria.</summary>
internal sealed class Sha256PassThroughStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public string FinishHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Track(buffer.Span[..read]);
        return read;
    }

    private void Track(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        BytesRead += bytes.Length;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
```

`MediaCopyProcessor.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Media;

internal sealed record PendingMedia(Guid MessageId, Guid TenantId, Guid ConnectionId, string MetaMediaId, DateTimeOffset OccurredAt, int Attempts);

/// <summary>§8.6, «Copia»: por cada medio reclamado, token → GET /{id} → stream a R2 con SHA-256 al
/// pasar → fila. Nunca registra el token ni la URL firmada. Un fallo deja last_error y el reintento ya
/// quedó programado por el reclamo.</summary>
internal sealed partial class MediaCopyProcessor(
    MessagingDbContext dbContext,
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    IMessagingMediaStore store,
    IClock clock,
    ILogger<MediaCopyProcessor> logger)
{
    public const long MaxBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan MetaRetention = TimeSpan.FromDays(7);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media of message {MessageId} could not be copied (attempt {Attempt}): {Reason}.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int attempt, string reason);

    public static string KeyFor(Guid tenantId, Guid messageId) => $"messaging/{tenantId}/{messageId}";

    public async Task CopyOneAsync(PendingMedia pending, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (now - pending.OccurredAt > MetaRetention)
        {
            await FailAsync(pending, "expired", giveUp: true, cancellationToken);
            return;
        }

        var sender = await connections.ResolveSenderAsync(pending.TenantId, pending.ConnectionId, cancellationToken);
        if (sender is null)
        {
            await FailAsync(pending, "connection_unavailable", giveUp: false, cancellationToken);
            return;
        }

        var info = await meta.GetMediaAsync(sender, pending.MetaMediaId, cancellationToken);
        if (!info.Succeeded || info.Value is null)
        {
            await FailAsync(pending, info.Failure?.Reason ?? "media_info", giveUp: false, cancellationToken);
            return;
        }

        if (info.Value.FileSize > MaxBytes)
        {
            await FailAsync(pending, "too_large", giveUp: true, cancellationToken);
            return;
        }

        var key = KeyFor(pending.TenantId, pending.MessageId);
        string hash;
        long length;
        try
        {
            await using var source = await meta.OpenMediaAsync(sender, info.Value.Url, cancellationToken);
            await using var hashing = new Sha256PassThroughStream(source);
            await store.UploadAsync(key, hashing, info.Value.FileSize, info.Value.MimeType, cancellationToken);
            hash = hashing.FinishHex();
            length = hashing.BytesRead;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(pending, "download:" + exception.GetType().Name, giveUp: false, cancellationToken);
            return;
        }

        if (info.Value.Sha256 is { } expected && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
        {
            await store.DeleteAsync(key, cancellationToken);
            await FailAsync(pending, "sha256_mismatch", giveUp: false, cancellationToken);
            return;
        }

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.message_media SET stored_at = {now}, storage_key = {key}, size_bytes = {length}, sha256 = {hash}, mime_type = {info.Value.MimeType}, last_error = NULL
            WHERE message_id = {pending.MessageId}
            """, cancellationToken);
    }

    private async Task FailAsync(PendingMedia pending, string reason, bool giveUp, CancellationToken cancellationToken)
    {
        LogFailed(logger, pending.MessageId, pending.Attempts, reason);
        // Rendirse = no volver a reclamar: next_attempt_at muy lejos y el motivo en last_error.
        var next = giveUp ? DateTimeOffset.MaxValue.AddDays(-1) : (DateTimeOffset?)null;
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.message_media SET last_error = {reason[..Math.Min(reason.Length, 256)]}, next_attempt_at = COALESCE({next}, next_attempt_at) WHERE message_id = {pending.MessageId}",
            cancellationToken);
    }
}
```

`MediaCopyWorker.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Media;

/// <summary>§8.6: reclama pendientes con FOR UPDATE SKIP LOCKED y next_attempt_at = now + lease[n] (P15:
/// 1 min, 5 min, 30 min, 2 h, 6 h…), y copia cada uno en su scope.</summary>
internal sealed partial class MediaCopyWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    ILogger<MediaCopyWorker> logger) : BackgroundService
{
    internal const int BatchSize = 20;

    internal static readonly IReadOnlyList<TimeSpan> Leases =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    [LoggerMessage(Level = LogLevel.Error, Message = "Media copy tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Media copy of message {MessageId} threw; it is retried when its lease expires.")]
    private static partial void LogCopyFailed(ILogger logger, Exception exception, Guid messageId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.MediaPollSeconds)));
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        List<PendingMedia> claimed;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var leases = Leases.ToArray();
            // Reclamo y lectura en una sentencia que se commitea sola: otra réplica no toma la misma fila.
            claimed = await dbContext.Database.SqlQuery<PendingMedia>(
                $"""
                UPDATE messaging.message_media AS media
                   SET next_attempt_at = {now} + ({leases})[LEAST(media.attempts + 1, cardinality({leases}))],
                       attempts = media.attempts + 1
                  FROM (SELECT message_id FROM messaging.message_media
                         WHERE stored_at IS NULL AND next_attempt_at <= {now}
                         ORDER BY next_attempt_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED) AS pending
                  JOIN messaging.messages AS message ON message.id = pending.message_id
                 WHERE media.message_id = pending.message_id
                RETURNING media.message_id AS "MessageId", message.tenant_id AS "TenantId", message.connection_id AS "ConnectionId",
                          media.meta_media_id AS "MetaMediaId", message.occurred_at AS "OccurredAt", media.attempts AS "Attempts"
                """).ToListAsync(cancellationToken);
        }

        foreach (var pending in claimed)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<MediaCopyProcessor>().CopyOneAsync(pending, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogCopyFailed(logger, exception, pending.MessageId);
            }
        }
    }
}
```

(`SqlQuery<PendingMedia>` con un record posicional: si EF exige constructor sin parámetros, cámbialo a clase con `{ get; set; }`.)

`src/Bootstrapper/MessagingMediaStore.cs`:

```csharp
using Modules.Messaging.Application;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>§6.4 y §6.6: el bucket privado de Storage por stream. Messaging no referencia Storage.</summary>
internal sealed class MessagingMediaStore(IObjectStorage objectStorage) : IMessagingMediaStore
{
    public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
        objectStorage.UploadAsync(key, content, contentLength, contentType, cancellationToken);

    public async Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        await objectStorage.OpenReadAsync(key, cancellationToken) is { } opened
            ? new MediaStreamDto(opened.Content, opened.ContentType, opened.Length, null)
            : null;

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => objectStorage.DeleteAsync(key, cancellationToken);
}
```

Endpoint en `MessagingEndpoints.cs`:

```csharp
        group.MapGet("/media/{messageId:guid}", GetMediaAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
…
    /// <summary>§8.6, «Servir»: nosniff + CSP sandbox siempre; inline sólo para imagen (salvo SVG), audio y
    /// video; attachment con nombre para lo demás, para que un HTML o un SVG no se ejecute en el origen de
    /// la API. 404 sin código mientras no esté copiado.</summary>
    private static async Task<IResult> GetMediaAsync(
        Guid tenantId, Guid messageId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var media = await dispatcher.QueryAsync(new GetMediaQuery(tenantId, messageId), cancellationToken);
        if (media is null)
        {
            return Results.NotFound();
        }

        var headers = httpContext.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        headers["Cache-Control"] = "private, max-age=3600";
        var inline = IsInlineSafe(media.ContentType);
        var fileName = string.IsNullOrWhiteSpace(media.FileName) ? messageId.ToString("N") : media.FileName;
        var disposition = new System.Net.Mime.ContentDisposition { Inline = inline, FileName = inline ? null : fileName };
        headers["Content-Disposition"] = disposition.ToString();
        headers.ContentLength = media.Length;
        return Results.Stream(media.Content, media.ContentType);
    }

    private static bool IsInlineSafe(string contentType) =>
        (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
        || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
```

(Si `ContentDisposition.ToString()` escapa mal un nombre con tildes, usa `Microsoft.Net.Http.Headers.ContentDispositionHeaderValue` con `SetHttpFileName`.)

Registro: Infrastructure → `IMediaReads`/`MediaReads`, `MediaCopyProcessor` (scoped), `AddHostedService<MediaCopyWorker>()`; Bootstrapper → `IMessagingMediaStore`/`MessagingMediaStore` y `IQueryHandler<GetMediaQuery, MediaStreamDto?>`/`GetMediaHandler`.

- [ ] **Step 4: Verificar GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~Sha256PassThroughStreamTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~MediaApiTests|FullyQualifiedName~ThreadApiTests"
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build --filter "FullyQualifiedName~R2ObjectStorageTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todas correctas.

- [ ] **Step 5: Formato y commit**

```text
feat(messaging): copia de medios a R2 por stream con SHA-256 y servido con la sesión de QEP
```

---

### Task 19: README, CLAUDE.md, k8s y HANDOFF para el owner en Meta

**Files:**
- Modify: `README.md` (§ «Configuración» tabla, § «Módulos por tenant», § «API implementada» tabla, § «Integraciones (conexiones del tenant)», sección nueva «Mensajería (WhatsApp Cloud)» antes de `## Verificación`)
- Modify: `CLAUDE.md` (gotcha «Conversations no existe» → 15 módulos)
- Modify: `k8s/prod-configMap.yaml` (después de `Integrations__SecretProtection__ActiveKeyId`), `k8s/prod-secret.yaml` (después de `Integrations__SecretProtection__Keys__k1`)
- Create: `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`

**Interfaces:**
- Consumes: todo el plan.
- Produces: documentación al día.

- [ ] **Step 1: README**

1. Tabla de § «Configuración» (después de la fila `Integrations:Zenvia:BaseUrl`):

```markdown
| `Meta:App:AppId`, `Meta:App:ConfigId`                | ausentes (user-secrets en local)                                                              | La app de Meta de toda la plataforma (spec 2026-10-09). Públicos: viajan en el popup de Embedded Signup. **En `Production` son obligatorios** junto con los tres de abajo (`MetaAppOptionsValidator`); fuera, sin ellos `whatsapp-cloud` no sale en el catálogo y el webhook responde 403/401 |
| `Meta:App:GraphApiVersion`                           | `v24.0`                                                                                       | Versión de Graph; patrón `^v\d+\.\d+$` |
| `Meta:App:AppSecret`, `Meta:App:WebhookVerifyToken`  | user-secrets                                                                                  | Secretos: el `AppSecret` canjea el `code` y firma el webhook; el token verifica la suscripción (aleatorio, ≥ 32 caracteres). En k8s van en el Secret |
| `Messaging:Webhook:MaxBodyBytes`                     | `4194304`                                                                                     | Tope del cuerpo del webhook (Meta manda hasta 3 MB); más → 413 |
| `Messaging:Webhook:ConcurrencyLimit` / `QueueLimit`  | `64` / `256`                                                                                  | Limitador `webhook`: concurrencia global con cola, no por IP |
| `Messaging:Workers:*`                                | `DeliveryPollSeconds 3`, `MediaPollSeconds 5`, `PurgeIntervalHours 24`, `DeliveryRetentionDays 7` | Intervalos de los workers de entregas, medios y purga |
| `Messaging:Search:StatementTimeoutMs`                | `2000`                                                                                        | Tope de la búsqueda en el historial; al pasarlo responde 422 en `q` |
```

2. § «Módulos por tenant»: donde se listan los módulos vendibles (`pos`), agrega `messaging` («la bandeja de WhatsApp; último de la lista, sin dependencias, se prende por tenant desde la consola de operador o con el SQL de respaldo»).

3. Tabla de § «API implementada», después de la fila de `/integrations`:

```markdown
| `/api/v1/tenants/{tenantId}/integrations/whatsapp/embedded-signup` | `POST` | `integrations.connection.manage` |
| `/api/v1/tenants/{tenantId}/messaging` | `conversations` (`GET`), por conversación `GET`, `messages` (`GET`, `POST`), `read` (`POST`), `resolve` y `reopen` (`POST`, `If-Match`); `messages/search` (`GET`); `media/{messageId}` (`GET`) | `messaging.conversation.read` / `.manage` |
| `/api/webhooks/whatsapp` | `GET` (verificación), `POST` (firmado por Meta) | anónimo, limitador `webhook`, exento de CSRF |
```

4. En § «Integraciones (conexiones del tenant)», después del primer párrafo:

```markdown
Desde el spec 2026-10-09 el catálogo tiene dos proveedores: **Zenvia (WhatsApp)** y **WhatsApp Business
(Meta)** (`whatsapp-cloud`, visible con `messaging` activo, tope de 5). El segundo no se conecta por
formulario sino por **Embedded Signup**: el frontend abre el popup de Meta con `Meta:App:AppId` y
`Meta:App:ConfigId`, y al cerrar manda `POST …/integrations/whatsapp/embedded-signup` con el `code`
(vence a los 30 s). El backend canjea el `code` por un token de sistema, registra el número con un PIN
aleatorio de seis dígitos que **no se guarda**, suscribe la app a la WABA y lee el número verificado;
todo queda en una conexión `Active` con su fila en `integrations.connection_routes`, que es lo que
impide que un número quede conectado en dos tenants y lo que el webhook usa para enrutar. El probador
de `whatsapp-cloud` hace `GET /{phoneNumberId}` y refresca número, nombre y calidad; `190` deja
`token_expired`, `133010` deja `number_unregistered`, y un `account_update` de cuenta deshabilitada
deja `account_disabled`. Eliminar la conexión no llama a Meta (el número sigue registrado en la Cloud
API) y conserva las conversaciones de sólo lectura.
```

5. Sección nueva antes de `## Verificación`:

````markdown
### Mensajería (WhatsApp Cloud)

La bandeja de WhatsApp (spec 2026-10-09): lo que una persona escribe al número del tenant llega por el
webhook de Meta, se lee en `/messaging/conversations` y se responde desde ahí dentro de la ventana de
24 horas. Módulo `messaging` (se prende por tenant), permisos `messaging.conversation.read|manage` en
`admin` y `advisor`, esquema `messaging` (`conversations`, `messages`, `message_media`,
`webhook_deliveries`).

**Webhook.** `GET /api/webhooks/whatsapp` responde el `hub.challenge` si `hub.verify_token` coincide
con `Meta:App:WebhookVerifyToken`. `POST` valida `X-Hub-Signature-256` (HMAC-SHA256 del cuerpo crudo
con `Meta:App:AppSecret`) y guarda el cuerpo deduplicado por su SHA-256 en `webhook_deliveries`; nada
más pasa en el request. Un worker reclama cada entrega con un lease (8 intentos, ~1 h) y procesa:
`messages` entrantes (una conversación por `wa_id` y conexión, reabre si estaba resuelta, sube
`unread_count`), `statuses` (monótonos: `sent < delivered < read`; `failed` sólo sobre `sent`;
`played` cuenta como `read`), `account_update` (cuenta deshabilitada → la conexión queda en
`NeedsAttention`). Con la conexión `Paused` o el módulo apagado los entrantes se descartan y los
`statuses` sí se aplican. Las entregas procesadas se purgan a los 7 días. No se suscriben los campos
de coexistencia (`history`, `smb_message_echoes`): lo que se responde desde la app del teléfono no
aparece en QEP todavía.

**Envío.** `POST …/conversations/{id}/messages` con `{ clientId, text }` es idempotente por
`(conversación, clientId)`: repetir devuelve el mismo mensaje; un `Failed` se reenvía. Cada envío lleva
`biz_opaque_callback_data = "qep:{messageId}"`, que es como se correlaciona un acuse que llegue antes de
que el `wamid` esté guardado.

**Medios.** Los entrantes se copian a R2 (bucket privado, `messaging/{tenantId}/{messageId}`) con un
worker que verifica el SHA-256 de Meta; `GET …/messaging/media/{messageId}` los sirve con la sesión,
con `nosniff`, CSP `sandbox` y `attachment` para todo lo que no sea imagen, audio o video. Mientras no
está copiado responde 404 sin código.

**Búsqueda.** `GET …/messaging/messages/search?q=` busca por palabra en texto y leyendas con la
configuración `messaging.es_unaccent` (español sin acentos ni flexiones, prefijo con 3+ letras) y el
índice GIN `IX_messages_tenant_search`; el servidor arma la `tsquery` (nunca la persona) y corta a los
2 s con 422 en `q`.

#### Secretos en local

```powershell
dotnet user-secrets set "Meta:App:AppSecret" "<el app secret>" --project src/Api | Out-Null
dotnet user-secrets set "Meta:App:WebhookVerifyToken" "<32+ caracteres aleatorios>" --project src/Api | Out-Null
dotnet user-secrets set "Meta:App:AppId" "<app id>" --project src/Api | Out-Null
dotnet user-secrets set "Meta:App:ConfigId" "<config id>" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "Meta:App" | Measure-Object
```

El último comando tiene que dar `Count 4`. Nunca `list` sin el filtro y el conteo.

#### Probar el webhook en local

El cuerpo va a archivo (PowerShell rompe las comillas) y la firma se calcula sobre esos bytes exactos:

```powershell
$body = Get-Content -Raw -Encoding UTF8 .\webhook.json
$bytes = [Text.Encoding]::UTF8.GetBytes($body)
$secret = Read-Host -AsSecureString "AppSecret"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secret))
$hmac = New-Object Security.Cryptography.HMACSHA256 ([Text.Encoding]::UTF8.GetBytes($plain))
$signature = "sha256=" + (($hmac.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
Remove-Variable plain, secret, hmac
curl.exe -s -o NUL -w "%{http_code}" -X POST "http://localhost:5000/api/webhooks/whatsapp" -H "Content-Type: application/json" -H "X-Hub-Signature-256: $signature" -d "@webhook.json"
```

Esperado: `200`. Sin el header: `401`. La entrega se procesa en los 3 s siguientes
(`Messaging:Workers:DeliveryPollSeconds`).

#### Despliegue

1. Variables secretas `META_APP_SECRET` y `META_WEBHOOK_VERIFY_TOKEN` en `Backend-prod`;
   `META_APP_ID` y `META_CONFIG_ID` como variables normales. Sin las cinco claves,
   `MetaAppOptionsValidator` deja el pod en crash-loop: es a propósito.
2. Backend primero (migraciones `AddWhatsAppCloudProvider`, `AddConnectionRoutes`,
   `AddMessagingModuleKey`, `AddCustomerPhoneE164`, `InitialMessaging`), después se verifica el webhook
   en Meta (hace el `GET` al guardarlo), después el frontend.
3. Prender `messaging` al tenant (consola de operador o el SQL de respaldo de «Módulos por tenant»).
````

- [ ] **Step 2: CLAUDE.md, k8s y HANDOFF**

`CLAUDE.md`, gotcha «`Conversations` no existe»: reemplaza por:

```markdown
- **`Conversations` como módulo no existe; la bandeja de WhatsApp es `Messaging`** (spec 2026-10-09).
  Los quince módulos construidos son Audit, Authorization, Catalog, Companies, Customers, Geography,
  Identity, Integrations, Messaging, Notifications, Pos, Quotations, Reporting, Storage y Tenancy — cada
  uno con su `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/`. Messaging no referencia
  Integrations, Customers ni Storage: lo que necesita entra por `IMessagingConnectionDirectory`,
  `IMessagingCustomerDirectory` e `IMessagingMediaStore`, con adaptadores en Bootstrapper.
```

Y un gotcha nuevo debajo:

```markdown
- **El webhook de Meta está exento de CSRF por ruta y no usa el limitador `Public`.** `/api/webhooks/`
  pasa `RequireCsrfHeaderMiddleware` sin `X-Qep-Client` porque se autentica con la firma HMAC, y usa el
  limitador `webhook` (concurrencia global con cola): Meta manda ráfagas desde pocas IPs y un 429 la
  hace reintentar hasta 7 días. Sin `Meta:App` configurado (sólo fuera de `Production`) el GET responde
  403 y el POST 401, y `whatsapp-cloud` no sale en el catálogo.
```

`k8s/prod-configMap.yaml`, después de `Integrations__SecretProtection__ActiveKeyId`:

```yaml
  # Spec 2026-10-09 (Mensajería): la app de Meta de toda la plataforma. Públicos: viajan en la URL del
  # popup de Embedded Signup. El AppSecret y el token del webhook viven en prod-secret.yaml. Sin las cinco
  # claves MetaAppOptionsValidator deja el pod en crash-loop, a propósito.
  Meta__App__AppId: "#{META_APP_ID}#"
  Meta__App__ConfigId: "#{META_CONFIG_ID}#"
  Meta__App__GraphApiVersion: "v24.0"
```

`k8s/prod-secret.yaml`, después de `Integrations__SecretProtection__Keys__k1`:

```yaml
  # Spec 2026-10-09 (Mensajería): el app secret canjea el code de Embedded Signup y firma el webhook; el
  # token verifica la suscripción del webhook (aleatorio, 32+ caracteres; el mismo valor se pega en Meta).
  # Variables secretas del grupo Backend-prod: META_APP_SECRET y META_WEBHOOK_VERIFY_TOKEN.
  Meta__App__AppSecret: "#{META_APP_SECRET}#"
  Meta__App__WebhookVerifyToken: "#{META_WEBHOOK_VERIFY_TOKEN}#"
```

`docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`:

```markdown
# HANDOFF — lo que el owner hace en Meta para la mensajería por WhatsApp

Spec: `docs/superpowers/specs/2026-10-09-mensajeria-whatsapp-design.md` (§14). Nada de esto lo puede hacer el
backend; sin estos pasos el flujo de conexión no abre y el webhook no recibe.

1. **App en Live**, con verificación del negocio y App Review aprobados.
2. **Permisos con Advanced Access:** `whatsapp_business_management` y `whatsapp_business_messaging`.
   `business_management` **no** se pide (D-M19): sólo lo exige Meta a un Solution Partner que comparte línea
   de crédito.
3. **Dominios:** `qep.qcode.co` en «Allowed Domains for the JavaScript SDK» y en «Valid OAuth Redirect URIs».
4. **Configuración de Facebook Login for Business** para **WhatsApp Embedded Signup v4**; su id va a la
   variable `META_CONFIG_ID` (ConfigMap `Meta__App__ConfigId`). El id de la app va a `META_APP_ID`.
5. **Webhook:** URL `https://<host de la API>/api/webhooks/whatsapp`, token de verificación = el mismo valor
   que la variable secreta `META_WEBHOOK_VERIFY_TOKEN`, y **sólo** los campos `messages` y `account_update`.
6. **Orden:** desplegar el backend **antes** de guardar el webhook en Meta (hace el `GET` de verificación al
   guardarlo). Variables secretas `META_APP_SECRET` y `META_WEBHOOK_VERIFY_TOKEN` en `Backend-prod` antes del
   deploy, o el pod no arranca.
7. **Coexistencia (D-M13, D-M15):** lo que alguien responda desde la app de WhatsApp Business en el teléfono
   **no aparece en QEP** en esta versión (no se suscriben `history`, `smb_app_state_sync` ni
   `smb_message_echoes`); entra con un slice posterior. Y si la cuenta de WhatsApp Business tiene varios
   números sin conectar, el flujo de coexistencia no puede saber cuál se eligió y responde
   `integrations.whatsapp.registration_failed`: se conecta un número por vez, o se usa el camino de número
   nuevo.
8. **PIN de dos pasos:** el registro fija un PIN aleatorio que no se guarda. Si el número ya tenía
   verificación en dos pasos con otro PIN, `/register` falla (`registration_failed`): quitar ese PIN en
   WhatsApp Manager y reintentar.
9. **Decisiones a ratificar** (spec §13, D-M13 a D-M19): si alguna está mal, el costo está en la tabla del spec.
```

- [ ] **Step 3: Verificar que nada se rompió y commit**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
Select-String -Path README.md -Pattern "Meta:App:AppId|### Mensajería \(WhatsApp Cloud\)|/api/webhooks/whatsapp|embedded-signup"
Select-String -Path CLAUDE.md -Pattern "quince módulos"
Select-String -Path k8s/prod-configMap.yaml, k8s/prod-secret.yaml -Pattern "Meta__App__"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: cada patrón aparece; `ConfigurationExampleTests` en verde. Commit (sólo documentación y yaml):

```text
docs(messaging): README, CLAUDE.md, k8s y HANDOFF de Meta para la mensajería por WhatsApp
```

---

### Task 20: Suite completa, build sin warnings y formato — una sola vez, comparada contra `develop`

**Files:**
- Create (fuera del repo, no se commitea): `C:\Users\andre\AppData\Local\Temp\qep-messaging\compare-test-runs.ps1`

**Interfaces:**
- Consumes: todo el plan.
- Produces: la lista de fallas de la rama y, por cada clase que falla, si también falla en `develop` (preexistente) o no (regresión). Cero regresiones es la condición de cierre.

- [ ] **Step 1: Build de toda la solución sin warnings**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw "restore con locks desactualizados: dotnet restore --force-evaluate y commitear los locks" }
dotnet build Backend.slnx --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "build con warnings o errores" }
```

Esperado: `0 Advertencias, 0 Errores`.

- [ ] **Step 2: Formato de lo tocado** — el chequeo de «Antes de empezar», contra `develop`. Esperado: sin hallazgos fuera de `ENDOFLINE`/`CHARSET`.

- [ ] **Step 3: La suite completa de la rama, una vez, con trx**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
$results = "C:\Users\andre\AppData\Local\Temp\qep-messaging\branch"
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $results
```

(Tarda: Quotations solo lleva ~15 minutos. No se corta con `| tail`.) Si `dotnet test` reporta `Con error: 0`, salta al Step 6.

- [ ] **Step 4: El script de comparación**

`C:\Users\andre\AppData\Local\Temp\qep-messaging\compare-test-runs.ps1`:

```powershell
param(
    [Parameter(Mandatory)] [string] $Branch,          # carpeta de trx de la rama
    [Parameter(Mandatory)] [string] $Worktree,        # $W
    [string] $DevelopWorktree = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\develop-baseline"
)

# 1. Fallas de la rama, por nombre completo de prueba. -LiteralPath: hay trx con corchetes en el nombre
#    ("[1].trx") que Get-Content con -Path saltea y la cuenta sale mal (memoria del proyecto).
function Read-Failures([string] $folder) {
    $failures = @()
    foreach ($file in Get-ChildItem -LiteralPath $folder -Filter *.trx -Recurse) {
        [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
        $failures += $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName }
    }
    return $failures | Sort-Object -Unique
}

$branchFailures = Read-Failures $Branch
"Fallas en la rama: $($branchFailures.Count)"
$branchFailures | ForEach-Object { "  $_" }
if ($branchFailures.Count -eq 0) { exit 0 }

# 2. Las clases que fallan (FullyQualifiedName sin el método).
$classes = $branchFailures | ForEach-Object { ($_ -replace '\([^)]*\)$', '') -replace '\.[^.]+$', '' } | Sort-Object -Unique

# 3. Un worktree temporal de develop, con su propio restore y build. Nunca el checkout principal del owner.
if (-not (Test-Path $DevelopWorktree)) {
    git -C $Worktree worktree add $DevelopWorktree develop
}
Set-Location $DevelopWorktree
dotnet restore --locked-mode
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "develop no compila; no se puede comparar" }

# 4. Sólo esas clases, en develop. Las que no existen en develop (clases nuevas) son regresión por definición.
$filter = ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
$baseline = "C:\Users\andre\AppData\Local\Temp\qep-messaging\develop"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --filter $filter --logger "trx" --results-directory $baseline
$developFailures = Read-Failures $baseline

# 5. Regresión = falla en la rama y no falla en develop.
$regressions = $branchFailures | Where-Object { $developFailures -notcontains $_ }
""
"Preexistentes (fallan también en develop): $(($branchFailures | Where-Object { $developFailures -contains $_ }).Count)"
"Regresiones: $($regressions.Count)"
$regressions | ForEach-Object { "  REGRESION $_" }
exit ($(if ($regressions.Count -eq 0) { 0 } else { 1 }))
```

- [ ] **Step 5: Correr la comparación**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\andre\AppData\Local\Temp\qep-messaging\compare-test-runs.ps1" -Branch "C:\Users\andre\AppData\Local\Temp\qep-messaging\branch" -Worktree $W
```

Esperado: `Regresiones: 0`. Fallas preexistentes conocidas (memoria del proyecto): 2 de `OrderExportApiTests` (NIT en export, desde `6f8aa75`). Cada regresión se arregla en la tarea dueña (RED/GREEN, commit propio) y se vuelve a correr **sólo esa clase**; la suite completa no se repite. Al terminar, el worktree temporal se quita:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
git -C $W worktree remove --force "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\develop-baseline"
```

(Si falla con «Invalid argument», hay un `codegraph serve` con el índice abierto; con «Filename too long», son `bin/obj`: borrar con `\\?\` — memoria del proyecto.)

- [ ] **Step 6: Cierre**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-whatsapp"
Set-Location $W
git status --short
git log --oneline develop..HEAD
git log develop..HEAD --format=%B | Select-String -Pattern "Co-Authored" | Measure-Object
```

Esperado: árbol limpio, ~21 commits, `Count 0`. Sin commit en esta tarea (no hay cambios en el repo).

---

## Cobertura del spec

| Sección del spec | Tarea |
| --- | --- |
| §2 decisiones 1–12 | 4–7 (1), 1 (2), 6 y 16 (3), 2 (4), 3 (5), 11 (6), 13 (7), 10 (8), 18 (9), 8 y 14 (10), 16 (11), 15 (12) |
| §5.1 módulo y permisos | 2, 3 |
| §5.2 catálogo con `onboarding`, `ConnectionResponse`, `embedded-signup`, webhook | 4, 7, 11 |
| §5.3 lista, detalle, hilo, búsqueda, envío, leído, resolver/reabrir, medio | 14, 15, 16, 17, 18 |
| §5.4 ambigüedades (`counts.unread` = suma, defaults 30/50, entrantes `Delivered`) | 14 (counts, defaults), 13 (status 2) |
| §6.1 Integrations (internos, onboarding, proveedor, reglas POST/PUT, migraciones, puertos, probador, códigos) | 4, 5, 6, 7 |
| §6.2 Tenancy | 2 |
| §6.3 Authorization | 3 |
| §6.4 Messaging (proyectos, referencias, puertos, auditoría, endpoints, validadores) | 3, 10, 13, 14–18 |
| §6.5 Customers | 8 |
| §6.6 Storage | 18 |
| §6.7 Api/Bootstrapper (limitador, CSRF, mapeo, adaptadores) | 11, 13, 14, 18 |
| §7 esquema, índices, extensiones, configuración, columna generada, fillfactor | 10 |
| §7.5 ingesta atómica y estados | 13, 13b |
| §7.6 consultas calientes | 14, 15, 13b |
| §8.1 Embedded Signup | 7 |
| §8.2 webhook y worker (claims, rutas, paused, módulo apagado, account_update, callback, purga) | 11, 13, 13b |
| §8.3 envío e idempotencia | 16 |
| §8.4 leído | 17 |
| §8.5 resolver/reabrir | 17 |
| §8.6 medios | 18 |
| §8.7 lista e hilo, mapa de tipos | 12, 14 |
| §8.8 búsqueda | 15 |
| §9 configuración | 1, 10, 15, 19 |
| §10.1 códigos HTTP | 4–7, 11, 14–18 |
| §10.2 `lastFailureCode` nuevos | 6, 13b, 16 |
| §10.3 `failureReason` | 9 |
| §11 seguridad (firma, secretos, token, medios, aislamiento, CSRF) | 11, 7 y 16 (fugas), 18, 14 (403), 11 (CSRF) |
| §12 pruebas | cada tarea; suite completa en 20 |
| §13 decisiones D-M1…D-M19 | D-M1 (10, 11), D-M2 (5), D-M3 (1, 4, 7, 11), D-M4 (4), D-M5 (13), D-M6 (12), D-M7 (8), D-M8 (16), D-M9 (12), D-M10/D-M11 (sin código: se documentan en 19), D-M12 (13b), D-M13 (13: `UnknownChange`), D-M14 (6), D-M15 (7), D-M16 (13b), D-M17 (14), D-M18 (13), D-M19 (19) |
| §14 entregables | migraciones (2, 4, 5, 8, 10), paquete y locks (8), README/CLAUDE/k8s/HANDOFF (19) |

## Lo que el spec dice y el plan ajusta

- §8.8 nombra `MessagingUnitOfWork` para traducir el `57014`; el plan lo pone en `MessageSearch` (P8) porque la búsqueda es una lectura.
- §6.5 dice que el dominio «recibe» el normalizador; el plan lo recibe como parámetro opcional de `Create`/`Update` (P7) para no reescribir las pruebas existentes.
- §9 añade `Messaging:Search:StatementTimeoutMs` (default 2000) para poder provocar el timeout en una prueba sin tocar el default del spec.
- El 401 del webhook sale con ProblemDetails y código `messaging.webhook.signature_invalid` (P11); el spec sólo pide 401.
