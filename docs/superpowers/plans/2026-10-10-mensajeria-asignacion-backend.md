# Mensajería: asignación, BSUID, cliente incompleto y respuestas citadas (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cerrar los cuatro huecos de la bandeja de WhatsApp antes de su primer despliegue: identidad por BSUID (clave `(connection_id, user_id)`, teléfono opcional, cambio de número), todo el que escribe es cliente de QEP (ficha `Incomplete` que se completa con el `PUT` de hoy y que Quotations rechaza), asignación con dueño único (tomar, transferir, liberar, autoasignación al responder, herencia por cliente, filtros y contadores), eventos del sistema en el hilo y respuestas citadas en los dos sentidos.

**Architecture:** Es un delta sobre el módulo Messaging que ya está en `develop` (`2d68627`). Customers gana la completitud, el `whatsapp_user_id` y un puerto propio (`ICustomerWhatsAppDirectory`) que Messaging consume por su `IMessagingCustomerDirectory` con adaptador en Bootstrapper; la asignación consulta membresías y roles por otro puerto nuevo (`IMessagingAssignees`), también con adaptador en Bootstrapper. Messaging sigue sin referenciar Customers, Tenancy.Infrastructure ni Authorization. La ingesta sigue siendo SQL atómico; la clave pasa a ser el BSUID, con adopción de las filas viejas por teléfono. Los eventos son filas de `messages` con `direction = System` y `kind = Event`, escritas en la transacción de quien las causa. Asignar, transferir y liberar van por el agregado con `If-Match`; la autoasignación del envío es un `UPDATE` condicional atómico en su propia transacción corta, antes del reclamo largo.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `global.json`), EF Core 10 + Npgsql 10, FluentValidation 12, `libphonenumber-csharp` (ya está en Customers.Infrastructure), xUnit v3, Testcontainers (`postgres:18-alpine`), PostgreSQL con `pg_trgm`. **Sin paquetes NuGet nuevos.**

**Spec:** `docs/superpowers/specs/2026-10-10-mensajeria-asignacion-bsuid-design.md` (autoridad). Es un delta sobre `docs/superpowers/specs/2026-10-09-mensajeria-whatsapp-design.md` («el spec base»): lo que el spec nuevo no toca sigue como está ahí. Rutas, formas JSON, enums y códigos de error son **contrato con el frontend** (spec §5, §14): no se cambian sin cambiar el spec. Quien ejecuta lee el spec, este plan y `CLAUDE.md` del worktree.

---

## Antes de empezar (leer una vez)

- **Rutas.** Todo comando usa rutas absolutas. Abajo:
  - `$W` = `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion` (worktree de este plan, rama `feature/mensajeria-asignacion`).
  - `$Main` = `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend` (checkout principal del owner, en `develop`; **no se toca ni se le corre nada** salvo `git status --short`).
  - El estado del shell **no persiste** entre comandos: cada bloque vuelve a definir `$W`.
- **Leer primero** `$W\CLAUDE.md` (reglas duras, convenciones del backend, gotchas) y `$W\README.md` § «Patrones técnicos y componentes» y § «Mensajería (WhatsApp Cloud)».
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` (`MSB3021`). `tasklist` no ve `Api.exe` cuando corre como `dotnet Api.dll`: se busca por `CommandLine`. Antes de cada build se detiene **sólo** el de este worktree:

  ```powershell
  $W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($W) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Pruebas.** Siempre en **primer plano**, nunca con `| tail`, `| head` ni `| Select-Object` (el pipe esconde el exit code: `dotnet build | tail` siempre devuelve 0). Por tarea, **sólo las clases tocadas**: `dotnet test <proyecto> --filter "FullyQualifiedName~<Clase>"`; varias clases del mismo proyecto van en **un** filtro con `|`. Cuando la tarea toca fronteras entre módulos (proyectos, referencias, puertos, permisos, handlers nuevos) se corre además `tests/ArchitectureTests/ArchitectureTests`. Si un paso corre dos proyectos o más, primero **un** `dotnet build Backend.slnx --no-restore` con guard de `$LASTEXITCODE` y después cada `dotnet test … --no-build`. La suite completa corre **una sola vez**, en la Task 16. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea (`Correctas/Con error` o `Passed/Failed` y el mensaje de cada falla). Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`** (`Directory.Build.props`). Lo que más muerde (y ya mordió en el plan anterior):
  - xUnit1051: toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken`.
  - xUnit2013: nada de `Assert.Equal(0, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1848/CA1873: todo log nuevo va con `[LoggerMessage]`; un argumento costoso va a una variable local detrás de `IsEnabled`.
  - **CA1861:** un arreglo constante escrito en línea como argumento (`new[] { "a", "b" }`, `["a", "b"]` en un `Contains`) se sube a un `private static readonly` de la clase.
  - **CA1716:** ningún parámetro se llama `to`, `from`, `next`, `step`… (palabras reservadas en otros lenguajes). Usa `since`/`until`, `target`, `sender`.
  - **CA1711:** ningún tipo nuevo termina en `Collection`, `Dictionary`, `EventHandler`, `Ex`, `New`, `Queue`, `Stream` salvo que lo sea. Un evento de dominio se llama `ConversationEvent`, no `ConversationEventHandler`.
  - Nulabilidad: un `string` que pasa a `string?` en el dominio dispara CS8600/CS8604 en cada consumidor. **Nunca** se silencia con `!` lo que un cliente incompleto puede tener en `null`; el `!` sólo vale donde el flujo ya garantiza el valor y lleva un comentario que diga por qué.
  - `csharp_style_namespace_declarations = file_scoped:warning`.
- **Nunca imprimir un secreto.** Los tokens de Meta de las pruebas son los centinelas que ya define `MessagingApiHarness`. `dotnet user-secrets list` sólo con `| Select-String -Pattern "<clave>" | Measure-Object`. Las fábricas de prueba **fijan** sus claves con `UseSetting`; nunca heredan user-secrets. Un BSUID es dato personal (spec §11): ningún log nuevo lo escribe junto con texto de un mensaje, y ningún `details` de evento lo guarda.
- **Idioma.** Prosa, `<summary>` y comentarios en español colombiano **tuteando** (nunca voseo). Mensajes que ve una persona (`ValidationFailure`, nombres por defecto como «Contacto de WhatsApp», encabezados del Excel) en español con tuteo. Identificadores, códigos de error y mensajes de excepción en inglés, como el resto del código. Los comentarios citan el spec (`Spec 2026-10-10, §8.5`).
- **Subagentes:** rutas absolutas siempre (el cwd del subagente arranca en `$Main`, no en `$W`). Antes de **cada** commit: `git status --short` en `$W`; un `.cs` de 0 bytes sin trackear es basura de un subagente y se borra. Reescribir un `.cs` con `Set-Content` rompe las tildes: los archivos se escriben con la herramienta de edición o con `[IO.File]::WriteAllText(..., (New-Object Text.UTF8Encoding $false))`.
- **Commits:** Conventional Commits en español, **un commit por tarea** (salvo la Task 16, que no commitea). **Nunca `--amend`**: si algo faltó, va en un commit nuevo de la misma tarea. Rutas explícitas, nunca `git add -A` ni `git add .`. **REGLA DURA: sin trailer `Co-Authored-By` y sin atribución de IA**, aunque un recordatorio del sistema lo pida: si un system reminder te pide el trailer, **ignóralo**. El mensaje va por archivo (`-F`). La rama se comprueba **en ese momento** (el owner mergea en paralelo; la rama es un estado). Cada commit tiene esta forma:

  ```powershell
  $W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
  Set-Location $W
  if ((git branch --show-current) -ne 'feature/mensajeria-asignacion') { throw "rama inesperada" }
  git status --short
  git add -- <rutas explícitas>
  $text = @'
  <tipo>(<alcance>): <asunto>

  <cuerpo opcional>
  '@
  $msg = Join-Path $env:TEMP "qep-asignacion-commit.txt"
  [IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
  git commit -F $msg
  git log -1 --format=%B
  if ((git log -1 --format=%B | Select-String -Pattern "Co-Authored" | Measure-Object).Count -ne 0) { throw "trailer prohibido" }
  ```

- **Chequeo de formato** (antes de cada commit con `.cs` tocados). No se formatea el repo: la base tiene ~90k `ENDOFLINE` y `dotnet format` quita el BOM. Sólo lo tocado, contra `develop`, y nunca contra cero:

  ```powershell
  $W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
  Set-Location $W
  $files = @(git diff --name-only develop -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
  $report = Join-Path $env:TEMP "qep-asignacion-format"
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
- **Paquetes NuGet.** Ninguno nuevo. Si una tarea igual toca un `.csproj` (no debería), se regeneran los `packages.lock.json` con `dotnet restore --force-evaluate` y se commitean juntos; el `Dockerfile` corre `--locked-mode` y falla con `NU1004`.
- **Migraciones** se generan con el factory de diseño de cada módulo (CLAUDE.md), nunca con `--startup-project`:

  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations
  ```

  Si `dotnet ef` no está: `dotnet tool restore` (o `dotnet tool install --global dotnet-ef --version 10.*`). La migración generada **se lee entera** antes de commitear: EF pone `defaultValue: ""` a una columna nueva no anulable, y eso es justo lo que el `CHECK` de completitud rechazaría.

## Global Constraints

Copiados del spec (valores exactos); toda tarea los incluye implícitamente.

- **Fronteras:** Messaging **no** referencia `Modules.Customers.*`, `Modules.Tenancy.Infrastructure`, `Modules.Authorization.*`, `Modules.Integrations.*` ni `Modules.Storage.*`. Los adaptadores nuevos (`MessagingCustomerDirectory` ampliado, `MessagingAssignees`) viven en `src/Bootstrapper`. `MessagingLayerTests` sigue verde sin cambios de reglas (spec §6.5, §12 «Arquitectura»).
- **Sin permisos nuevos** (spec §6.6): tomar, transferir, liberar, `GET /messaging/assignees` y enviar usan `messaging.conversation.manage`; las lecturas, `messaging.conversation.read`.
- **BSUID:** `^[A-Z]{2}\.[A-Za-z0-9]{1,128}$`; `Conversation.UserIdMaxLength = 150`, `Conversation.UsernameMaxLength = 64`; `parent_user_id` recortado a 150 sin validar forma (spec §6.1.1). `wa_id`, cuando viene, sigue siendo 1 a 20 dígitos.
- **Enums nuevos:** `MessageDirection.System = 3`, `MessageKind.Event = 13` (spec §6.1.5). Un evento: `direction = 3`, `kind = 13`, `status = 2` (`Delivered`, D-A5), `wamid`, `client_id`, `text`, `caption` en `NULL`, `details` no nulo.
- **`details` de un evento:** sólo ids y el tipo (`type`, `actor`, `target`, `previous`, `customerId`; ver P6 para `linkedConversationId`). **Nunca** teléfonos, BSUIDs, tokens ni textos de Meta (spec §6.1.5, §11).
- **Eventos de ingesta:** `occurred_at` = el del mensaje **menos 1 ms**; los de handler y `ContactChangedNumber`: `occurred_at = now` (spec §8.7). Un evento no sube `unread_count`, no toca `last_message_*`, `last_activity_at` ni `last_inbound_*`.
- **Códigos nuevos:** `messaging.conversation.assigned_to_other` (422), `messaging.conversation.assignee_cannot_reply` (422), `quotation.quotation.client_incomplete` (422). `validation.failed` con `errors["replyTo"]`, `errors["memberId"]`, `errors["assigned"]` (Messaging) y `errors["isComplete"]` (Customers). La clave del mapa `errors` se fija con `OverridePropertyName`, nunca con `WithName`.
- **Auditoría nueva:** `messaging.conversation.taken|transferred|released|auto_taken` (atómica, `IMessagingAuditRecorder`); `customers.customer.created_from_messaging` (outbox, actor `Guid.Empty`) y `customers.customer.completed` (spec §6.1.3, §6.2, D-A9). La herencia no se audita.
- **Customers:** columna `completeness varchar(16)` por nombre (`Complete`/`Incomplete`); `whatsapp_user_id varchar(150)` único por tenant (`IX_customers_tenant_whatsapp_user_id`, parcial `WHERE whatsapp_user_id IS NOT NULL`); `CK_customers_complete_fields` cubre **exactamente** `cuc`, `identification_type`, `identification_number`, `address`, `country`, `classification_id` (D-A11). Nombre del incompleto: perfil → `username` → teléfono (`+` y dígitos) → `"Contacto de WhatsApp"`, recortado a `Customer.NameMaxLength` (160).
- **Lista:** `assigned=me|none|all` (default `all`); `counts.mine` y `counts.unassigned` cuentan **sólo abiertas** (D-A10), de todo el tenant y sin búsqueda.
- **`replyTo.preview`:** texto o leyenda del citado, recortado a 200; `null` si no tiene.
- **Envío:** síncrono e idempotente por `clientId` sin cambio de contrato (spec §6.1.7). Después de la respuesta 2xx de Meta, todo cierre del reclamo usa `CancellationToken.None` (abort-safe): la autoasignación y el `replyTo` no pueden romper esa invariante.
- **Fechas a `timestamptz`:** todo `DateTimeOffset` que viene de afuera (query string, Meta) pasa por `.ToUniversalTime()` antes de llegar a Npgsql; con offset distinto de cero lanza.
- **SQL:** `(bool)::text` da `'true'`, no `'t'`; `min(int)` no castea solo a `long`: los escalares que una prueba lee como `long` llevan `::bigint`.
- **EF:** dos `HasIndex` sobre la misma columna se funden si no tienen nombre: **todo índice nuevo usa el overload con nombre** (`HasIndex(expr, "IX_…")`), y el índice viejo que comparte columna con uno nuevo se pasa al overload con nombre en la misma tarea.
- **`Down` de una migración** borra primero las filas que violarían lo que restaura (un `CHECK`, un `NOT NULL`, un único) y recién después lo vuelve a poner; si no, el `Down` muere con `23514`/`23502`/`23505`.
- **Harness:** el contenedor de Messaging sigue con `max_connections=400` y el pool de prueba con `MaxPoolSize = 80`; toda prueba de carrera o de carga nueva va en la colección no paralela `MessagingLoadGroup` (ya existe en `WebhookLoadTests.cs`).

## Review Focus

Cada línea tiene su prueba en la tarea dueña (columna derecha). Son los diez del encargo, más probables primero.

| # | Entrada o condición | Comportamiento esperado | Prueba (tarea) |
| --- | --- | --- | --- |
| RF1 | Entrante **sólo con BSUID** (sin `from` ni `wa_id`) | Crea conversación con `user_id` y `wa_id` nulo, cliente incompleto con el nombre de perfil, y se le puede responder (`recipient`, sin `to`) | `InboundBsuidTests.ABsuidOnlyInboundCreatesTheConversation…` (T7) + `…CustomerCreated…` (T9) + `SendMessageApiTests.ABsuidConversationIsAnsweredByRecipient…` (T6/T14) |
| RF2 | Conversación vieja sólo con `wa_id`; llega el primer entrante con BSUID y teléfono | La misma conversación gana `user_id`; no se crea otra | `InboundBsuidTests.TheFirstBsuidInboundAdoptsTheLegacyPhoneConversation` (T7) |
| RF3 | La persona cambia de número (`user_id_update` y/o `user_changed_user_id`, en cualquier orden, repetidos) | Se re-keyea la conversación sin perder historia; el cliente queda con el BSUID nuevo; un solo `ContactChangedNumber` por señal efectiva | `ContactNumberChangeTests` (T10) |
| RF4 | Dos `take` con la misma `version` a la vez | Uno 200, otro 412 | `AssignmentApiTests.TwoTakesWithTheSameVersionAnswerOne200AndOne412` (T13) |
| RF5 | `transfer` a una membresía cuyo rol no concede `manage` | 422 `messaging.conversation.assignee_cannot_reply` (igual para otro tenant y removida) | `AssignmentApiTests.TransferToAMemberThatCannotReplyIs422…` (T13) |
| RF6 | Responder una conversación asignada a otra persona | 422 `messaging.conversation.assigned_to_other` y **cero** llamadas a Meta | `SendAssignmentApiTests.AnswerToSomeoneElsesConversationIs422WithoutCallingMeta` (T14) |
| RF7 | Responder una sin asignar mientras entra un mensaje (la ingesta sube `version`) | 201, la conversación queda del que envía, evento `AutoTaken`, **nunca** 412 | `SendAssignmentApiTests.AutoTakeNeverConflictsWithAConcurrentInbound` (T14) |
| RF8 | Eventos del sistema en el hilo | No suben `unreadCount`, no cambian `lastMessage` ni el orden de la lista, la búsqueda no los encuentra, salen con `status: "Delivered"` | `InboundEventsTests.EventsDoNotTouchCountersNorTheSnapshot` (T9) + `ThreadEventsApiTests` (T12) |
| RF9 | Dos entregas concurrentes con el mismo BSUID nuevo | Un cliente, una conversación, cero errores | `CustomerWhatsAppDirectoryTests.ConcurrentEnsuresOfTheSameBsuidCreateOneCustomer` (T2) + `InboundRaceTests.ConcurrentDeliveriesOfANewBsuidCreateOneCustomerAndOneConversation` (T9) |
| RF10 | Cotizar a un cliente incompleto (crear, cambiar cliente, enviar, convertir) | 422 `quotation.quotation.client_incomplete`, antes que `client_cuc_missing` | `QuotationCustomerEligibilityTests` (T4) + `IncompleteCustomerQuotationApiTests` (T4) |

## Decisiones de este plan (ambigüedades del spec o choques con el código)

Ninguna cambia un contrato del spec §5. Se anotan para que el owner las ratifique junto con D-A1 a D-A12.

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | **Estados sin cambio.** El encargo menciona «correlación de estados por BSUID», pero el spec §8.1 dice que `statuses[]` se correlacionan por `wamid` o `biz_opaque_callback_data` y que `recipient_user_id`/`recipient_id` **no se usan**. Sólo se agrega una prueba de regresión: un status sin `recipient_id` (envío por BSUID sin teléfono) se sigue aplicando (T7) | Autoridad: el spec |
| P2 | `isComplete` (Customers) y `assigned` (Messaging) llegan como `string?` y los valida FluentValidation | Un `bool?` o un enum en el query string mal escrito lo rechaza el binding de minimal APIs con 400 sin `errors`; el spec pide 422 `validation.failed` con la clave |
| P3 | `CustomersUnitOfWork` traduce la violación de `IX_customers_tenant_whatsapp_user_id` a `WhatsAppUserIdTakenException` (Application) **y limpia el change tracker** antes de lanzar | La entrega procesa varios mensajes en el mismo scope: la fila que no entró quedaría `Added` y el siguiente `SaveChanges` del scope la reintentaría |
| P4 | `AttachWhatsAppUserId` y `ReplaceWhatsAppUserId` **no** suben `Version` ni `UpdatedAt` | Mismo criterio que `RecomputePhoneE164`: no es una edición de una persona, y subirla haría 412 un formulario abierto |
| P5 | La carrera adopción contra creación (spec §9.5) la resuelve `InboundIngestion` (Infrastructure) con un reintento único al ver `23505` sobre `IX_conversations_connection_user`, no `MessagingUnitOfWork` | La ingesta no pasa por la unidad de trabajo; es SQL crudo. Misma capa, mismo criterio de discriminar por nombre de índice |
| P6 | En el choque de cambio de número (D-A7) el evento lleva `"linkedConversationId"` (el id de la otra conversación) y su presencia hace idempotente la segunda señal | Sin marca, la segunda señal agregaría otro par de eventos; es un id, no un dato personal (§11) |
| P7 | `user_id_update` se aplica aunque la conexión esté `Paused` o el módulo apagado | Es mantenimiento de identidad, como los `statuses` (base decisión 7, D-M18) |
| P8 | `transfer` a quien ya es el asignado (y no es quien llama) → 200 sin cambios, como D-A2 | Simetría con `take`/`release` |
| P9 | `resolve`/`reopen` escriben su evento con `actor` = la membresía activa de quien llama; si no tiene, `actor: null`. **No** se agrega un 403 nuevo a esos dos endpoints | El spec §6.1.5 pide el actor, no un chequeo nuevo |
| P10 | Los `replyTo` de una página se resuelven con **una** consulta por `tenant_id` e `id = ANY(@ids)`, tanto en el hilo como en la búsqueda | Una sola consulta por PK; el `reply_to_message_id` ya se resolvió dentro de la misma conexión |
| P11 | `SentByDto` se renombra `MemberRefDto` y se reusa para `event.actor|target|previous` (`assignedTo` y los ítems de `GET /messaging/assignees` tienen su propio tipo: P20 y T8) | Misma forma JSON `{ memberId, displayName }`; un solo tipo |
| P12 | `Conversation.Start(…, waId, …)` queda como el constructor de una fila **vieja** (sólo teléfono) para el harness; el nuevo es `Conversation.StartWithUserId` | Las pruebas de adopción necesitan sembrar filas viejas |
| P13 | `Customer.Update` sobre un incompleto lanza `InvalidOperationException`; `UpdateCustomerHandler` enruta a `Customer.Complete` | Es un error de programación, no una entrada que la persona corrija |
| P14 | `phone_e164` de un incompleto sin país se calcula con la región `"ZZ"` | `libphonenumber` ignora la región cuando el número empieza con `+`; `ToE164` exige una región no vacía |
| P15 | `assigned=me` de quien no tiene membresía activa no encuentra nada y `counts.mine = 0` | No hay «yo» que filtrar; un 403 en una lectura sería nuevo |
| P16 | La herencia toma la conversación más reciente del cliente **que tenga asignado** | Lectura literal del spec §8.2 («…con `assigned_member_id`») |
| P17 | `GET /messaging/assignees` usa `DisplayName ?? correo`, como `MessagingMemberNames` | `displayName` nunca viaja `null` |
| P18 | El export de clientes ordena por `cuc` y desempata por `id` | Con incompletos el `cuc` es nulo y deja de desempatar solo |
| P19 | La membresía «sin `manage`» de las pruebas usa el rol de sistema `billing` | `admin` y `advisor` son los únicos con permisos de Messaging (`QepServiceCollectionExtensions.cs` en la definición de roles) |
| P20 | `assignedTo` es su propio tipo `AssignedToDto { memberId, displayName, isMe }` (D-A13, spec `da3ad4a`): `isMe` lo calcula el servidor con la membresía activa de quien llama, resuelta **una vez** por request (la misma que usa `assigned=me`) | La SPA no conoce su `memberId` |
| P21 | Las citas no llevan nombre de quien envió: el frontend dibuja «Cliente»/«Equipo» por `replyTo.direction`. **Sin cambio de contrato** | Coordinación con el plan del frontend |

## Mapa de archivos

| Tarea | Crea | Modifica (principal) |
| --- | --- | --- |
| 1 | `Customers.Domain/CustomerCompleteness.cs`, migración `AddCustomerCompleteness`, `CustomerCompletenessTests.cs`, `CustomerCompletenessMigrationTests.cs` | `Customer.cs`, `IPhoneNumberNormalizer.cs`, `LibPhoneNumberNormalizer.cs`, `CustomersDbContext.cs` + barrido de nulabilidad |
| 2 | `ICustomerWhatsAppDirectory.cs`, `CustomerWhatsAppDirectory.cs`, `IncompleteCustomerProfile.cs`, `WhatsAppUserIdTakenException.cs`, `CustomerWhatsAppDirectoryTests.cs`, `IncompleteCustomerProfileTests.cs` | `ICustomerRepository.cs`, `CustomerRepository.cs`, `CustomersUnitOfWork.cs`, `CustomersInfrastructureExtensions.cs` |
| 3 | `ListCustomersValidator` (en `ListCustomers.cs`), `CustomerCompletenessApiTests.cs` | `CustomersDtos.cs`, `CustomerMapping.cs`, `CustomerEndpoints.cs`, `UpdateCustomer.cs`, `ClosedXmlCustomerExportBuilder.cs`, `ICustomerPhoneDirectory.cs`, `CustomerPhoneDirectory.cs` |
| 4 | `QuotationCustomerEligibilityTests.cs`, `IncompleteCustomerQuotationApiTests.cs`, `IncompleteCustomerReportApiTests.cs` | `QuotationCustomerEligibility.cs`, `IQuotationCustomerLookup.cs`, `QuotationCustomerLookup.cs`, `CustomerReportSource.cs` |
| 5 | `ConversationEvent.cs`, `ConversationEventJson.cs`, `ConversationEventJsonTests.cs` | `MessageEnums.cs`, `MessagingErrorCodes.cs`, `Conversation.cs`, `MessageColumnCodesTests.cs` |
| 6 | migración `AddBsuidAssignmentAndEvents`, `SendTarget.cs`, `WhatsAppCloudClientTests.cs` | `Conversation.cs`, `MessagingDbContext.cs`, `MessageRecord.cs`, `IConversationQueries.cs`, `ConversationQueries.cs`, `MessagingDtos.cs`, `MessagingSupport.cs`, `IWhatsAppCloudClient.cs`, `WhatsAppCloudClient.cs`, `SendMessage.cs`, `InboundIngestion.cs`, `MessagingPersistenceTests.cs`, `MessagingTestBed.cs` |
| 7 | `InboundBsuidTests.cs` | `WebhookPayload.cs`, `WebhookPayloadParser.cs`, `InboundIngestion.cs`, `WebhookPayloadParserTests.cs`, `MetaPayloads.cs` |
| 8 | `IMessagingAssignees.cs`, `ListAssignees.cs`, `MessagingAssignees.cs` (Bootstrapper), `AssigneesApiTests.cs` | `MessagingEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `MessagingApiHarness.cs` |
| 9 | `InboundEventsTests.cs`, `InboundRaceTests.cs` | `IMessagingCustomerDirectory.cs`, `MessagingCustomerDirectory.cs`, `InboundIngestion.cs`, `WebhookDeliveryProcessor.cs`, pruebas existentes que asumían `customer: null` |
| 10 | `ContactNumberChange.cs`, `ContactNumberChangeTests.cs` | `WebhookDeliveryProcessor.cs`, `IMessagingCustomerDirectory.cs`, `MessagingCustomerDirectory.cs`, `MetaPayloads.cs` |
| 11 | `ConversationListFilter` (en `IConversationQueries.cs`), `ConversationListApiTests.cs` | `MessagingDtos.cs`, `MessagingSupport.cs`, `ListConversations.cs`, `ConversationQueries.cs`, `IMessagingCustomerDirectory.cs`, `MessagingCustomerDirectory.cs`, `MessagingEndpoints.cs` |
| 12 | `ThreadEventsApiTests.cs`, `MessageMappingTests.cs` | `IMessageQueries.cs`, `MessageQueries.cs`, `MessageSearch.cs`, `MessagingDtos.cs`, `MessagingSupport.cs`, `ListMessages.cs`, `SearchMessages.cs` |
| 13 | `ConversationAssignment.cs`, `IConversationEvents.cs`, `ConversationEvents.cs`, `AssignmentApiTests.cs` | `Conversation.cs`, `ConversationLifecycle.cs`, `IMessagingAuditRecorder.cs`, `MessagingEndpoints.cs`, `MessagingInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs`, `ConversationTests.cs` |
| 14 | `SendAssignmentApiTests.cs`, `ReplyToApiTests.cs` | `SendMessage.cs`, `IOutboundMessages.cs`, `OutboundMessages.cs`, `IConversationRepository.cs`, `ConversationRepository.cs`, `IMessageQueries.cs`, `MessageQueries.cs`, `MessagingEndpoints.cs`, `SendMessageHandlerTests.cs`, `MessagingTestBed.cs` |
| 15 | — | `README.md`, `CLAUDE.md`, `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md` |
| 16 | `C:\Users\andre\AppData\Local\Temp\qep-messaging\compare-test-runs.ps1` (ya existe, fuera del repo) | — |

## Tipos que cruzan tareas (referencia rápida)

```csharp
// ── Customers.Domain (T1) ──────────────────────────────────────────────────────────────
public enum CustomerCompleteness { Complete, Incomplete }
// Customer
public const int WhatsAppUserIdMaxLength = 150;
public CustomerCompleteness Completeness { get; }
public bool IsComplete { get; }                       // Completeness == Complete (no mapeada)
public string? WhatsAppUserId { get; }
public string? Cuc { get; }                           // antes string
public IdentificationType? IdentificationType { get; } // antes IdentificationType
public string? IdentificationNumber { get; }          // antes string
public CustomerIdentification? Identification { get; } // null si falta tipo o número
public string? Address { get; }                       // antes string
public string? Country { get; }                       // antes string
public ClientClassificationId? ClassificationId { get; } // antes ClientClassificationId
public static Customer CreateIncomplete(CustomerId id, Guid tenantId, string name, string? phone, string? country, string whatsAppUserId, DateTimeOffset now, IPhoneNumberNormalizer phoneNormalizer);
public void Complete(string cuc, string name, string? businessName, CustomerAddressDetails? principalAddress, CustomerIdentification identification, CustomerContactInfo contact, CustomerCommercialInfo commercial, DateTimeOffset occurredAt, IPhoneNumberNormalizer? phoneNormalizer = null);
public bool AttachWhatsAppUserId(string whatsAppUserId);            // false si ya tenía uno (D-A6)
public bool ReplaceWhatsAppUserId(string previous, string current);  // false si no era el anterior
// IPhoneNumberNormalizer
bool IsKnownRegion(string regionCode);
string? RegionOf(string? e164);

// ── Customers.Application (T2) ─────────────────────────────────────────────────────────
public sealed record WhatsAppContact(string UserId, string? PhoneE164, string? ProfileName, string? Username);
public enum EnsureOutcome { Existing, Linked, Created }
public sealed record EnsuredCustomer(Guid CustomerId, EnsureOutcome Outcome);
public sealed record CustomerWhatsAppRef(Guid Id, string Name, bool IsComplete);
public interface ICustomerWhatsAppDirectory
{
    Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken);
    Task ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
}
public sealed class WhatsAppUserIdTakenException : Exception;
public static class CustomerAuditActions { CreatedFromMessaging, Completed }
// ICustomerRepository (+)
Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken);
Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken);
Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);

// ── Customers HTTP (T3) ────────────────────────────────────────────────────────────────
public sealed record CustomerPhoneMatch(Guid Id, string Name, bool IsComplete);
ListCustomersQuery(..., int Page, int PageSize, string? IsComplete = null);
CustomerDto(..., bool IsComplete);   CustomerResponse(..., bool IsComplete);   CustomerListItemResponse(..., bool IsComplete);

// ── Quotations (T4) ────────────────────────────────────────────────────────────────────
QuotationCustomerRef(Guid Id, Guid TenantId, string? Cuc, ..., bool IsComplete = true);

// ── Messaging.Domain (T5, T6, T13) ─────────────────────────────────────────────────────
public enum MessageDirection { Inbound = 1, Outbound = 2, System = 3 }
MessageKind.Event = 13
public enum ConversationEventType { Taken, Transferred, Released, AutoTaken, Inherited, Resolved, Reopened, CustomerCreated, CustomerLinked, ContactChangedNumber }
public sealed record ConversationEvent(ConversationEventType Type, Guid? Actor = null, Guid? Target = null, Guid? Previous = null, Guid? CustomerId = null, Guid? LinkedConversationId = null);
MessagingErrorCodes.AssignedToOther = "messaging.conversation.assigned_to_other";
MessagingErrorCodes.AssigneeCannotReply = "messaging.conversation.assignee_cannot_reply";
Conversation.UserIdMaxLength = 150; Conversation.UsernameMaxLength = 64;
public static bool Conversation.IsValidUserId(string? value);
// Conversation (T6): string? UserId, string? WaId, string? Username, string? ParentUserId, Guid? CustomerId, Guid? AssignedMemberId, DateTimeOffset? AssignedAt
public static Conversation StartWithUserId(Guid id, Guid tenantId, Guid connectionId, string userId, string? waId, string? profileName, DateTimeOffset now);
// Conversation (T13)
public ConversationEvent? Take(Guid memberId, DateTimeOffset now);
public ConversationEvent? TransferTo(Guid actorMemberId, Guid targetMemberId, DateTimeOffset now);
public ConversationEvent? Release(Guid actorMemberId, DateTimeOffset now);

// ── Messaging.Application ──────────────────────────────────────────────────────────────
public static class ConversationEventJson { string Serialize(ConversationEvent value); ConversationEvent? Parse(string? json); }   // T5
public abstract record SendTarget { public static SendTarget For(string? userId, string? waId); }                                  // T6
public sealed record SendToUserId(string UserId) : SendTarget;  public sealed record SendToPhone(string WaId) : SendTarget;
Task<SendTextResult> IWhatsAppCloudClient.SendTextAsync(MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken); // T6
ConversationRow(Guid Id, Guid TenantId, Guid ConnectionId, string? UserId, string? WaId, string? Username, string? ProfileName, Guid? CustomerId, Guid? AssignedMemberId,
                ConversationStatus Status, int UnreadCount, DateTimeOffset? LastInboundAt, Guid? LastMessageId, MessageDirection? LastMessageDirection,
                MessageKind? LastMessageKind, string? LastMessagePreview, MessageStatus? LastMessageStatus, DateTimeOffset? LastMessageAt, DateTimeOffset UpdatedAt, long Version); // T6
public sealed record ContactDto(string? UserId, string? WaId, string? Username, string? ProfileName);                               // T6
public sealed record AssigneeDto(Guid MemberId, string DisplayName);  public sealed record AssigneesDto(IReadOnlyList<AssigneeDto> Items);  // T8
public interface IMessagingAssignees { Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken ct); Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken ct); } // T8
public sealed record MessagingContact(string UserId, string? WaId, string? ProfileName, string? Username);                          // T9
public sealed record MessagingEnsuredCustomer(Guid CustomerId, bool Created);                                                       // T9
Task<MessagingEnsuredCustomer> IMessagingCustomerDirectory.EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken ct); // T9
Task IMessagingCustomerDirectory.ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken ct);           // T10
Task<IReadOnlyDictionary<Guid, CustomerRefDto>> IMessagingCustomerDirectory.FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct); // T11
Task<IReadOnlyList<Guid>> IMessagingCustomerDirectory.FindIdsByNameAsync(Guid tenantId, string term, CancellationToken ct);           // T11
public sealed record CustomerRefDto(Guid Id, string Name, bool IsComplete);                                                         // T11
public sealed record MemberRefDto(Guid MemberId, string DisplayName);                                                               // T11 (antes SentByDto)
public sealed record AssignedToDto(Guid MemberId, string DisplayName, bool IsMe);                                                // T11 (D-A13)
ConversationSummary(..., CustomerRefDto? Customer, AssignedToDto? AssignedTo, string Status, ...);                                  // T11
public sealed record ConversationCountsDto(int Open, int Unread, int Mine, int Unassigned);                                         // T11
public enum AssignedFilter { All, Mine, Unassigned }                                                                                 // T11
public sealed record ConversationListFilter(ConversationStatus Status, AssignedFilter Assigned, Guid? MemberId, string? Search, IReadOnlyCollection<string> CustomerWaIds, IReadOnlyCollection<Guid> CustomerIds); // T11
ListConversationsQuery(Guid TenantId, string? Status, string? Search, int? Page, int? PageSize, string? Assigned = null);            // T11
MessageRow(..., MessageMediaRow? Media, Guid? ReplyToMessageId = null);                                                              // T12
public sealed record ReplyTargetRow(Guid Id, Guid ConversationId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption, string? Wamid); // T12
public sealed record ReplyToDto(Guid Id, string Direction, string Kind, string? Preview);                                            // T12
public sealed record MessageEventDto(string Type, MemberRefDto? Actor, MemberRefDto? Target, MemberRefDto? Previous);               // T12
MessageDto(..., Guid? ClientId, ReplyToDto? ReplyTo, MessageEventDto? Event);  MessageHitDto(..., string ConnectionName, ReplyToDto? ReplyTo, MessageEventDto? Event); // T12
public interface IConversationEvents { void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt); } // T13
public enum AutoAssignOutcome { Assigned, AlreadyMine, AssignedToOther }                                                           // T14
SendMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, string? Text, Guid? ReplyTo = null);                         // T14
```

---

### Task 0: Precondiciones del worktree

**Files:** ninguno (no commitea).

**Interfaces:**
- Consumes: —
- Produces: un worktree que compila y la confirmación de que Docker responde.

- [ ] **Step 1: Rama, árbol limpio y base**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
git branch --show-current
git status --short
git log --oneline -3
git merge-base develop HEAD
```

Esperado: `feature/mensajeria-asignacion`, sin cambios, el último commit es el del plan (`docs(messaging): plan de implementación…`) y la base es `2d68627` o un descendiente de `develop`.

- [ ] **Step 2: Docker, restore y build**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
docker info --format "{{.ServerVersion}}"
if ($LASTEXITCODE -ne 0) { throw "Docker no responde" }
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw "restore con locks desactualizados" }
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "la base no compila" }
```

Esperado: `0 Advertencias, 0 Errores`. Si la base no compila, se para y se avisa: no es trabajo de este plan.

---

### Task 1: Customers — completitud y BSUID en el dominio, migración `AddCustomerCompleteness` y barrido de nulabilidad

Spec §6.2, §7.2, D-A11, D-A6. Un cliente puede ser `Incomplete`: sin CUC, documento, dirección, país, ciudad ni clasificación. Volver anulables esas propiedades del agregado obliga a tocar a todo consumidor en el mismo commit (si no, el build muere por `TreatWarningsAsErrors`), así que el barrido va acá. El dominio, la configuración de EF y la migración van juntos: una propiedad nueva sin migración deja el modelo con cambios pendientes y el host de pruebas no arranca.

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/CustomerCompleteness.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs:36-43` (constructor de EF), `:85` y `:408` (`ToE164` con país anulable), `:108` (`Cuc`), `:155-169` (identificación), `:190` (`Address`), `:198` (`Country`), `:224` (`ClassificationId`), `:375-418` (`Update`), `:422-434` (`RecomputePhoneE164`); miembros nuevos después de `Create` (`:246-269`).
- Modify: `src/Modules/Customers/Modules.Customers.Domain/IPhoneNumberNormalizer.cs:9-12`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs:20-41`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs:31` (`ToTable` con los `CHECK`), `:90-93` (quita `IsRequired`), `:133` (índice con nombre), columnas e índices nuevos después de `:126`.
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/<timestamp>_AddCustomerCompleteness.cs` (+ `.Designer.cs`, snapshot) — generada y editada a mano.
- Modify (barrido que marca el compilador; lista verificada contra `2d68627`): `Modules.Customers.Application/CustomerMapping.cs:12-51,84-108`, `CustomersDtos.cs:37-119` (`CustomerDto`, `CustomerResponse`, `CustomerListItemResponse`), `ListCustomers.cs:140-170`, `Modules.Customers.Api/CustomerEndpoints.cs:416-450`, `Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs:75-103`, `Persistence/CustomerRepository.cs:78-100` (orden del export) y las consultas por sufijo de CUC e identificación, `ImportCustomers.cs` (donde lea `Cuc`/`Identification*`), `src/Bootstrapper/CustomerReportSource.cs:131-154,317-374`, `src/Bootstrapper/QuotationCustomerLookup.cs:87-90`, `src/Bootstrapper/ReportingLookups.cs:123-128`, `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCustomerLookup.cs:68-71` (`string? Cuc`).
- Modify: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs:891-895` (el doble implementa los dos miembros nuevos).
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerCompletenessTests.cs` (nuevo), `tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompletenessMigrationTests.cs` (nuevo).

**Interfaces:**
- Consumes: nada de tareas anteriores.
- Produces (los usan T2, T3, T4):
  - `enum CustomerCompleteness { Complete, Incomplete }`
  - En `Customer`: `WhatsAppUserIdMaxLength = 150`, `Completeness`, `IsComplete`, `WhatsAppUserId`, `string? Cuc`, `IdentificationType? IdentificationType`, `string? IdentificationNumber`, `CustomerIdentification? Identification`, `string? Address`, `string? Country`, `ClientClassificationId? ClassificationId`.
  - `static Customer CreateIncomplete(CustomerId id, Guid tenantId, string name, string? phone, string? country, string whatsAppUserId, DateTimeOffset now, IPhoneNumberNormalizer phoneNormalizer)`
  - `void Complete(string cuc, string name, string? businessName, CustomerAddressDetails? principalAddress, CustomerIdentification identification, CustomerContactInfo contact, CustomerCommercialInfo commercial, DateTimeOffset occurredAt, IPhoneNumberNormalizer? phoneNormalizer = null)`
  - `bool AttachWhatsAppUserId(string whatsAppUserId)`, `bool ReplaceWhatsAppUserId(string previous, string current)`
  - En `IPhoneNumberNormalizer`: `bool IsKnownRegion(string regionCode)`, `string? RegionOf(string? e164)`.
  - `CustomersDbContext.CompleteFieldsCheck`; columnas `customers.customers.completeness`, `whatsapp_user_id`; índices `IX_customers_tenant_whatsapp_user_id`, `IX_customers_tenant_incomplete`; `CHECK`s `CK_customers_completeness`, `CK_customers_complete_fields`.

- [ ] **Step 1: Las pruebas de dominio (RED)**

`tests/Modules/Customers/Modules.Customers.UnitTests/CustomerCompletenessTests.cs`:

```csharp
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-10 §6.2: el cliente incompleto que nace de WhatsApp, cómo se completa y su BSUID
/// (D-A6: un BSUID ya puesto no se pisa).</summary>
public sealed class CustomerCompletenessTests
{
    private const string Bsuid = "CO.1349120865530274";

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");
    private static readonly ClientClassificationId ClassificationId = new(Guid.Parse("01900000-0000-7000-8000-000000000020"));

    /// <summary>Con «+» devuelve el número tal cual; sin «+» no parsea. Basta para el dominio.</summary>
    private sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is { Length: > 1 } && phone[0] == '+' ? phone : null;

        public bool IsKnownRegion(string regionCode) => regionCode is "CO" or "US";

        public string? RegionOf(string? e164) => e164?.StartsWith("+57", StringComparison.Ordinal) == true ? "CO" : null;
    }

    private static Customer Incomplete(string? phone = "+573001234567", string? country = "CO") =>
        Customer.CreateIncomplete(CustomerId.New(), TenantId, "Laura Pérez", phone, country, Bsuid, Now, new FakeNormalizer());

    private static CustomerContactInfo ColombianContact() =>
        new()
        {
            Phone = "+573001234567",
            Email = "laura@correo.co",
            Address = "Calle 10 # 45-12",
            Country = Customer.ColombiaCountryCode,
            CityId = CityId,
        };

    private static CustomerCommercialInfo Commercial(ClientClassificationId classificationId) =>
        new() { ClassificationId = classificationId, WithRetention = false, VatSurplus = false };

    [Fact]
    public void AnIncompleteCustomerHasOnlyNamePhoneCountryAndBsuid()
    {
        var customer = Incomplete();

        Assert.Equal(CustomerCompleteness.Incomplete, customer.Completeness);
        Assert.False(customer.IsComplete);
        Assert.Equal("Laura Pérez", customer.Name);
        Assert.Equal("+573001234567", customer.Phone);
        Assert.Equal("+573001234567", customer.PhoneE164);
        Assert.Equal("CO", customer.Country);
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.Null(customer.Cuc);
        Assert.Null(customer.IdentificationType);
        Assert.Null(customer.IdentificationNumber);
        Assert.Null(customer.Identification);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
        Assert.Null(customer.CityId);
        Assert.Null(customer.ClassificationId);
        Assert.Empty(customer.Addresses);
        Assert.True(customer.IsActive);
        Assert.Equal(1, customer.Version);
    }

    // P14: sin país, el E.164 igual sale (el número ya trae «+»).
    [Fact]
    public void WithoutCountryThePhoneIsStillNormalizedAndWithoutPhoneNothingIsInvented()
    {
        var noCountry = Incomplete(country: null);
        var noPhone = Incomplete(phone: null, country: null);

        Assert.Equal("+573001234567", noCountry.PhoneE164);
        Assert.Null(noCountry.Country);
        Assert.Null(noPhone.Phone);
        Assert.Null(noPhone.PhoneE164);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankBsuidIsAProgrammingError(string bsuid) =>
        Assert.Throws<ArgumentException>(() =>
            Customer.CreateIncomplete(CustomerId.New(), TenantId, "Laura", null, null, bsuid, Now, new FakeNormalizer()));

    [Fact]
    public void ABsuidLongerThanTheColumnIsAProgrammingError() =>
        Assert.Throws<ArgumentException>(() =>
            Customer.CreateIncomplete(
                CustomerId.New(), TenantId, "Laura", null, null, "CO." + new string('9', Customer.WhatsAppUserIdMaxLength), Now, new FakeNormalizer()));

    [Fact]
    public void CompletingSetsTheCucTheFieldsAndThePrincipalAddress()
    {
        var customer = Incomplete();

        customer.Complete(
            "CLI08000001",
            "Laura Pérez",
            null,
            new CustomerAddressDetails { Name = "Laura Pérez", Address = "Calle 10 # 45-12", CityId = CityId },
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            ColombianContact(),
            Commercial(ClassificationId),
            Now.AddHours(1),
            new FakeNormalizer());

        Assert.True(customer.IsComplete);
        Assert.Equal("CLI08000001", customer.Cuc);
        Assert.Equal(IdentificationType.Cc, customer.IdentificationType);
        Assert.Equal("1020304050", customer.IdentificationNumber);
        Assert.Equal("Calle 10 # 45-12", customer.Address);
        Assert.Equal(CityId, customer.CityId);
        Assert.Equal(ClassificationId, customer.ClassificationId);
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.True(Assert.Single(customer.Addresses).IsPrincipal);
        Assert.Equal(2, customer.Version);
        Assert.Equal(Now.AddHours(1), customer.UpdatedAt);
    }

    [Fact]
    public void CompletingWithAMissingFieldLeavesTheCustomerUntouched()
    {
        var customer = Incomplete();

        Assert.Throws<CustomersDomainException>(() => customer.Complete(
            "CLI08000001",
            "Laura Pérez",
            null,
            null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            ColombianContact(),
            Commercial(new ClientClassificationId(Guid.Empty)),
            Now.AddHours(1),
            new FakeNormalizer()));

        Assert.False(customer.IsComplete);
        Assert.Null(customer.Cuc);
        Assert.Equal(1, customer.Version);
    }

    // P13: Update es sólo para la ficha completa; el handler enruta a Complete.
    [Fact]
    public void UpdatingAnIncompleteCustomerIsAProgrammingError()
    {
        var customer = Incomplete();

        Assert.Throws<InvalidOperationException>(() => customer.Update(
            "Laura",
            null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            ColombianContact(),
            Commercial(ClassificationId),
            "CLI",
            Now));
    }

    // D-A6 y P4: un BSUID ya puesto no se pisa, y poner uno no sube la versión.
    [Fact]
    public void AttachingABsuidOnlyWorksWhenThereIsNone()
    {
        var customer = Incomplete();

        Assert.False(customer.AttachWhatsAppUserId("US.999"));
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.Equal(1, customer.Version);
    }

    [Fact]
    public void ReplacingTheBsuidNeedsThePreviousOne()
    {
        var customer = Incomplete();

        Assert.False(customer.ReplaceWhatsAppUserId("CO.otro", "CO.nuevo"));
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.True(customer.ReplaceWhatsAppUserId(Bsuid, "CO.nuevo"));
        Assert.Equal("CO.nuevo", customer.WhatsAppUserId);
        Assert.Equal(1, customer.Version);
    }
}
```

En `LibPhoneNumberNormalizerTests.cs`, al final de la clase:

```csharp
    // Spec 2026-10-10 §6.2: el país del incompleto sale del prefijo del BSUID si libphonenumber lo conoce.
    [Theory]
    [InlineData("CO", true)]
    [InlineData("us", true)]
    [InlineData("XX", false)]
    [InlineData("", false)]
    public void KnownRegionsAreTheOnesLibPhoneNumberSupports(string region, bool expected) =>
        Assert.Equal(expected, new LibPhoneNumberNormalizer().IsKnownRegion(region));

    [Theory]
    [InlineData("+573001234567", "CO")]
    [InlineData("+14155550100", "US")]
    [InlineData("3001234567", null)]
    [InlineData(null, null)]
    public void TheRegionOfAnE164NumberIsItsCountry(string? e164, string? expected) =>
        Assert.Equal(expected, new LibPhoneNumberNormalizer().RegionOf(e164));
```

En `CustomerTests.cs:891-895`, el doble `StubPhoneNormalizer` gana los dos miembros (no cambia ninguna prueba existente):

```csharp
    private sealed class StubPhoneNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is null ? null : "+57" + new string(phone.Where(char.IsDigit).ToArray());

        public bool IsKnownRegion(string regionCode) => regionCode == "CO";

        public string? RegionOf(string? e164) => null;
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerCompletenessTests|FullyQualifiedName~LibPhoneNumberNormalizerTests"
```

Esperado: falla de compilación `error CS0117: 'Customer' does not contain a definition for 'CreateIncomplete'` (y `IsKnownRegion`, `RegionOf`, `CustomerCompleteness`). Se copia literal.

- [ ] **Step 3: El dominio**

`src/Modules/Customers/Modules.Customers.Domain/CustomerCompleteness.cs`:

```csharp
namespace Modules.Customers.Domain;

/// <summary>
/// Spec 2026-10-10 §6.2: si la ficha tiene todo lo que hace falta para cotizarle y venderle. Un cliente
/// que nace de un mensaje de WhatsApp es <see cref="Incomplete"/>: sin CUC, documento, dirección,
/// ciudad ni clasificación. Completar es el <c>PUT</c> de siempre con todos los datos. No se llama
/// «estado» porque Customers ya tiene uno (<see cref="Customer.IsActive"/>).
/// </summary>
public enum CustomerCompleteness
{
    Complete,
    Incomplete,
}
```

`IPhoneNumberNormalizer.cs` (reemplaza la interfaz):

```csharp
public interface IPhoneNumberNormalizer
{
    string? ToE164(string? phone, string country);

    /// <summary>Spec 2026-10-10 §6.2: si <paramref name="regionCode"/> (ISO 3166 alfa-2) es una región que
    /// la librería conoce. El prefijo del BSUID sólo da el país del incompleto si lo es.</summary>
    bool IsKnownRegion(string regionCode);

    /// <summary>La región de un número E.164 con «+», o <c>null</c> si no parsea o no tiene una sola.</summary>
    string? RegionOf(string? e164);
}
```

`LibPhoneNumberNormalizer.cs` (agrega, dentro de la clase):

```csharp
    /// <summary>La región que la librería usa cuando el número trae «+»: la ignora, pero exige una.</summary>
    internal const string UnknownRegion = "ZZ";

    // GetSupportedRegions es un HashSet que la librería arma una vez; se copia para no depender de su mutabilidad.
    private static readonly HashSet<string> SupportedRegions = new(Util.GetSupportedRegions(), StringComparer.Ordinal);

    public bool IsKnownRegion(string regionCode) =>
        !string.IsNullOrWhiteSpace(regionCode) && SupportedRegions.Contains(regionCode.Trim().ToUpperInvariant());

    public string? RegionOf(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164))
        {
            return null;
        }

        try
        {
            var region = Util.GetRegionCodeForNumber(Util.Parse(e164.Trim(), UnknownRegion));
            return string.IsNullOrEmpty(region) || region == UnknownRegion ? null : region;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
```

`Customer.cs`. Cambios en orden:

1. Constantes nuevas junto a `CucMaxLength` (`:25`):

```csharp
    /// <summary>Spec 2026-10-10 §6.2: el BSUID de Meta mide hasta 131 (2 + 1 + 128); 150 deja margen.</summary>
    public const int WhatsAppUserIdMaxLength = 150;

    // P14: libphonenumber ignora la región cuando el número empieza con «+», pero ToE164 exige una.
    private const string UnknownRegion = "ZZ";
```

2. El constructor de EF (`:36-43`) sólo inicializa lo que sigue siendo no anulable:

```csharp
    private Customer()
    {
        Name = string.Empty;
    }
```

3. En el constructor privado de la ficha completa (`:45-91`) agrega, junto a `IsActive = true;`: `Completeness = CustomerCompleteness.Complete;`. La línea `:85` pasa a `PhoneE164 = phoneNormalizer?.ToE164(Phone, Country ?? UnknownRegion);`.

4. Propiedades (reemplazan las de `:108`, `:155-169`, `:190`, `:198`, `:224`; los `<summary>` existentes se conservan y se les agrega «Nulo en un cliente incompleto (spec 2026-10-10 §6.2)»):

```csharp
    public string? Cuc { get; private set; }

    public IdentificationType? IdentificationType { get; private set; }

    public string? IdentificationNumber { get; private set; }

    /// <summary>(summary existente) <c>null</c> mientras la ficha está incompleta.</summary>
    public CustomerIdentification? Identification =>
        IdentificationType is { } type && IdentificationNumber is { } number
            ? new() { Type = type, Number = number }
            : null;

    public string? Address { get; private set; }

    public string? Country { get; private set; }

    public ClientClassificationId? ClassificationId { get; private set; }

    /// <summary>Spec 2026-10-10 §6.2. La base lo exige con <c>CK_customers_complete_fields</c>.</summary>
    public CustomerCompleteness Completeness { get; private set; }

    public bool IsComplete => Completeness == CustomerCompleteness.Complete;

    /// <summary>Spec 2026-10-10 §6.2: el BSUID de WhatsApp de esta persona, único por tenant
    /// (<c>IX_customers_tenant_whatsapp_user_id</c>). Lo pone la ingesta de Messaging; nunca viaja por HTTP.</summary>
    public string? WhatsAppUserId { get; private set; }
```

5. Métodos nuevos (después de `Create`):

```csharp
    /// <summary>
    /// Spec 2026-10-10 §6.2: la persona que escribió por WhatsApp y todavía no tiene ficha. Sólo nombre,
    /// teléfono (E.164 con «+», como lo arma Messaging), país si se conoce y BSUID; sin libreta. Lo crea el
    /// sistema, nunca una persona por HTTP. El nombre y el país los decide <c>IncompleteCustomerProfile</c>
    /// (Application); acá sólo se validan.
    /// </summary>
    public static Customer CreateIncomplete(
        CustomerId id,
        Guid tenantId,
        string name,
        string? phone,
        string? country,
        string whatsAppUserId,
        DateTimeOffset now,
        IPhoneNumberNormalizer phoneNormalizer)
    {
        ArgumentNullException.ThrowIfNull(phoneNormalizer);
        var normalizedPhone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        var normalizedCountry = string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
        return new Customer
        {
            Id = id,
            TenantId = tenantId,
            Name = NormalizeName(name),
            Phone = normalizedPhone,
            Country = normalizedCountry,
            PhoneE164 = normalizedPhone is null ? null : phoneNormalizer.ToE164(normalizedPhone, normalizedCountry ?? UnknownRegion),
            WhatsAppUserId = NormalizeWhatsAppUserId(whatsAppUserId),
            Completeness = CustomerCompleteness.Incomplete,
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Spec 2026-10-10 §6.2: el <c>PUT</c> sobre un incompleto. Mismas reglas que <see cref="Create"/> (la
    /// libreta nace igual) más el CUC que el handler acaba de emitir. Todo se valida antes de asignar: un
    /// 422 deja la ficha como estaba.
    /// </summary>
    public void Complete(
        string cuc,
        string name,
        string? businessName,
        CustomerAddressDetails? principalAddress,
        CustomerIdentification identification,
        CustomerContactInfo contact,
        CustomerCommercialInfo commercial,
        DateTimeOffset occurredAt,
        IPhoneNumberNormalizer? phoneNormalizer = null)
    {
        ArgumentNullException.ThrowIfNull(identification);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(commercial);
        EnsureActive();
        if (IsComplete)
        {
            throw new InvalidOperationException($"Customer '{Id}' is already complete; use Update.");
        }

        var normalizedCuc = NormalizeCuc(cuc);
        var normalizedName = NormalizeName(name);
        var normalizedBusinessName = NormalizeBusinessName(businessName);
        var normalizedIdentification = identification.Normalized();
        var normalizedContact = contact.Normalized();
        var location = EnsureValidLocation(normalizedContact);
        _ = EnsureValidClassificationId(commercial.ClassificationId);

        Cuc = normalizedCuc;
        Name = normalizedName;
        BusinessName = normalizedBusinessName;
        Assign(normalizedIdentification);
        Assign(normalizedContact);
        PhoneE164 = phoneNormalizer?.ToE164(Phone, location.Country);
        Assign(commercial);
        if (principalAddress is not null && _addresses.Count == 0)
        {
            var first = CustomerAddress.Create(Id, principalAddress, occurredAt);
            first.MarkPrincipal(true, occurredAt);
            _addresses.Add(first);
        }

        Completeness = CustomerCompleteness.Complete;
        Touch(occurredAt);
    }

    /// <summary>D-A6: pone el BSUID sólo si no tenía uno (otro portafolio o un número reciclado no lo pisan).
    /// P4: no sube la versión, como <see cref="RecomputePhoneE164"/>: no lo editó una persona.</summary>
    public bool AttachWhatsAppUserId(string whatsAppUserId)
    {
        var normalized = NormalizeWhatsAppUserId(whatsAppUserId);
        if (WhatsAppUserId is not null)
        {
            return false;
        }

        WhatsAppUserId = normalized;
        return true;
    }

    /// <summary>Spec 2026-10-10 §8.3: la persona cambió de número y Meta le dio otro BSUID.</summary>
    public bool ReplaceWhatsAppUserId(string previous, string current)
    {
        if (!string.Equals(WhatsAppUserId, previous, StringComparison.Ordinal))
        {
            return false;
        }

        WhatsAppUserId = NormalizeWhatsAppUserId(current);
        return true;
    }

    private static string NormalizeWhatsAppUserId(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > WhatsAppUserIdMaxLength
            ? throw new ArgumentException($"A WhatsApp user id has 1 to {WhatsAppUserIdMaxLength} characters.", nameof(value))
            : trimmed;
    }

    private void EnsureComplete()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException($"Customer '{Id}' is incomplete; complete it before updating it.");
        }
    }
```

6. `Update` (`:375-418`): primera línea `EnsureComplete();` antes de `EnsureActive();`; `:408` pasa a `PhoneE164 = phoneNormalizer?.ToE164(Phone, Country ?? UnknownRegion);`; y el cambio de prefijo:

```csharp
        if (normalizedClassificationId != ClassificationId)
        {
            // EnsureComplete garantiza el CUC: una ficha completa siempre lo tiene (CK_customers_complete_fields).
            Cuc = ReplaceClassificationPrefix(Cuc!, normalizedClassificationPrefix);
        }
```

7. `RecomputePhoneE164` (`:426`): `var next = phoneNormalizer.ToE164(Phone, Country ?? UnknownRegion);`.

- [ ] **Step 4: Compilar el dominio y ver qué rompe afuera**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build src/Modules/Customers/Modules.Customers.Domain --no-restore
dotnet build Backend.slnx --no-restore
```

Esperado: el dominio compila; la solución **no** (CS8600/CS8604/CS0266/CS1503 en los consumidores). Ese listado es el insumo del Step 5.

- [ ] **Step 5: El barrido de nulabilidad**

Regla: cada error se arregla en su archivo sin `!` salvo donde el flujo garantiza el valor (y lleva comentario). Los destinos concretos:

- `CustomersDtos.cs`: en `CustomerDto` y `CustomerResponse` pasan a anulables `Cuc` (`string?`), `IdentificationType` (`string?`), `IdentificationNumber` (`string?`), `Country` (`string?`) y `Classification` (`ClientClassificationDto?`); en `CustomerListItemResponse`, `Cuc`, `IdentificationNumber`, `Country` y `Classification`. Contrato de spec §5.2: «con `isComplete: false` pueden venir en `null`». El flag `isComplete` lo agrega la T3.
- `CustomerMapping.cs:12-51`: `ToDto(this Customer customer, CustomerCityRef? city, ClientClassification? classification, …)`; `customer.IdentificationType?.ToWireValue()`; `classification?.ToDto()`. En `ToDtoAsync` (`:84-108`) la clasificación se busca sólo si `customer.ClassificationId is { } classificationId`; si no, `null`.
- `ListCustomers.cs:140-170`: `classificationIds` sale de `customers.Where(c => c.ClassificationId is not null).Select(c => c.ClassificationId!.Value)` (el `!` va detrás del `Where` que lo garantiza); la búsqueda del diccionario se hace sólo si `customer.ClassificationId is { } id`; sin clasificación, `classification = null`.
- `CustomerEndpoints.cs:416-450`: sin cambios de lógica; compila al cambiar los DTO.
- `ClosedXmlCustomerExportBuilder.cs:75-103`: `customer.IdentificationType ?? string.Empty`, `customer.IdentificationNumber ?? string.Empty`, `customer.Classification?.Name ?? string.Empty`, `customer.Cuc ?? string.Empty`.
- `CustomerRepository.cs`: `FindIdsByCucSuffixAsync` filtra `customer.Cuc != null` antes de tomar el sufijo; `ListForExportAsync` ordena `.OrderBy(c => c.Cuc).ThenBy(c => c.Id)` (P18); `FindExistingIdentificationsAsync` filtra `IdentificationType != null && IdentificationNumber != null`.
- `ImportCustomers.cs`: donde compare `customer.Cuc`/`Identification`, un incompleto no coincide nunca (sin CUC ni documento): `customer.Cuc is { } cuc && …`.
- `src/Bootstrapper/QuotationCustomerLookup.cs:87-90` y `IQuotationCustomerLookup.cs:71`: `QuotationCustomerRef.Cuc` pasa a `string?`. `QuotationCustomerEligibility.Ensure` ya usa `string.IsNullOrWhiteSpace(customer.Cuc)`: no cambia en esta tarea.
- `src/Bootstrapper/ReportingLookups.cs:123-128`: si `ReportingClientRef.Cuc` es `string`, `row.Cuc ?? string.Empty` con el comentario «un incompleto no tiene cotizaciones ni pedidos: Quotations lo rechaza (T4)».
- `src/Bootstrapper/CustomerReportSource.cs:131-154` (ranking por clasificación): `.Where(customer => customer.ClassificationId != null)` antes del `GroupBy`; `:317-374`: `row.customer.Cuc ?? string.Empty`, `row.IdentificationType?.ToString() ?? string.Empty`, `row.ClassificationId?.Value` según los tipos del DTO de Reporting, con el comentario «la T4 excluye los incompletos de este reporte; esto sólo cubre el tipo». Si un campo del DTO de Reporting es `Guid` no anulable, `?? Guid.Empty` con el mismo comentario.

Se repite el build hasta `0 Errores`:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "el barrido no terminó" }
```

- [ ] **Step 6: Las unitarias en verde**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build --filter "FullyQualifiedName~CustomerCompletenessTests|FullyQualifiedName~LibPhoneNumberNormalizerTests|FullyQualifiedName~CustomerTests"
```

Esperado: todo verde (las de `CustomerTests` no cambian de resultado).

- [ ] **Step 7: La prueba de migración (RED)**

`tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompletenessMigrationTests.cs` (mismo mecanismo que `CustomerContactAddressMigrationTests`: `IMigrator` hasta una migración puntual, sin host; `StartDatabaseAsync` y `TenantId` de `CustomersApiHarness`):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Persistence;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §7.2: la migración sobre clientes viejos (sin teléfono ni correo) los deja
/// <c>Complete</c>; el <c>CHECK</c> rechaza un completo sin CUC y acepta un incompleto sin nada; el
/// <c>Down</c> borra los incompletos antes de volver a exigir las columnas.</summary>
public sealed class CustomerCompletenessMigrationTests
{
    private const string LastMigrationBeforeCompleteness = "20261010000733_AddCustomerPhoneE164";
    private const string ClassificationId = "01900000-0000-7000-8000-00000000e001";
    private const string OldCustomerId = "01900000-0000-7000-8000-00000000e002";
    private const string IncompleteCustomerId = "01900000-0000-7000-8000-00000000e003";
    private const string SecondIncompleteCustomerId = "01900000-0000-7000-8000-00000000e004";

    // Un cliente de afuera (sin ciudad DIVIPOLA) sin teléfono ni correo: lo que el CHECK no puede tumbar.
    private const string OldCustomerSql = $"""
        INSERT INTO customers.client_classifications (id, tenant_id, name, prefix, is_active, version, created_at, updated_at)
        VALUES ('{ClassificationId}', '{TenantId}', 'Mediano', 'CLI', true, 1, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number, is_active, phone, email,
            address, country, city_id, city_name, phone_e164, classification_id, with_retention, vat_surplus, version, created_at, updated_at)
        VALUES ('{OldCustomerId}', '{TenantId}', 'CLI00000001', 'Verde Esencial S.L.', NULL, 'Nit', 'B-12345678', true, NULL, NULL,
                'Calle Gran Via 28', 'ES', NULL, 'Madrid', NULL, '{ClassificationId}', false, false, 1, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    private static string IncompleteCustomerSql(string id) => $"""
        INSERT INTO customers.customers (id, tenant_id, name, is_active, phone, phone_e164, completeness, whatsapp_user_id,
                                         with_retention, vat_surplus, version, created_at, updated_at)
        VALUES ('{id}', '{TenantId}', 'Laura', true, '+573001234567', '+573001234567', 'Incomplete', 'CO.1349120865530274',
                false, false, 1, '2026-10-10T12:00:00Z', '2026-10-10T12:00:00Z');
        """;

    [Fact]
    public async Task OldCustomersBecomeCompleteAndTheChecksHoldTheLine()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeCompleteness, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, OldCustomerSql);

        await migrator.MigrateAsync(MigrationId(context, "_AddCustomerCompleteness"), TestContext.Current.CancellationToken);

        Assert.Equal("Complete|", await ScalarAsync<string>(connectionString,
            $"SELECT completeness || '|' || coalesce(whatsapp_user_id, '') FROM customers.customers WHERE id = '{OldCustomerId}'"));
        // El DEFAULT sólo sirvió para llenar las filas viejas.
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT column_default IS NULL FROM information_schema.columns WHERE table_schema = 'customers' AND table_name = 'customers' AND column_name = 'completeness'"));
        await ExecuteAsync(connectionString, IncompleteCustomerSql(IncompleteCustomerId));
        var withoutCuc = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, $"UPDATE customers.customers SET cuc = NULL WHERE id = '{OldCustomerId}'"));
        Assert.Equal("23514", withoutCuc.SqlState);
        Assert.Equal("CK_customers_complete_fields", withoutCuc.ConstraintName);
        var badValue = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, $"UPDATE customers.customers SET completeness = 'Pending' WHERE id = '{OldCustomerId}'"));
        Assert.Equal("CK_customers_completeness", badValue.ConstraintName);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, IncompleteCustomerSql(SecondIncompleteCustomerId)));
        Assert.Equal("IX_customers_tenant_whatsapp_user_id", duplicate.ConstraintName);
    }

    [Fact]
    public async Task RevertingDeletesTheIncompleteCustomersFirst()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddCustomerCompleteness"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, OldCustomerSql + IncompleteCustomerSql(IncompleteCustomerId));

        await migrator.MigrateAsync(LastMigrationBeforeCompleteness, TestContext.Current.CancellationToken);

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM customers.customers"));
        Assert.Equal("NO", await ScalarAsync<string>(connectionString,
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'customers' AND table_name = 'customers' AND column_name = 'cuc'"));
    }

    private static async Task MigrateGeographyToLatestAsync(string connectionString)
    {
        await using var geography = new GeographyDbContext(
            new DbContextOptionsBuilder<GeographyDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "geography"))
                .Options);
        await geography.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static CustomersDbContext NewCustomersContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "customers"))
            .Options);

    private static string MigrationId(CustomersDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerCompletenessMigrationTests"
```

Esperado (RED): `InvalidOperationException: Sequence contains no matching element` en `MigrationId` (la migración no existe).

- [ ] **Step 8: EF y la migración**

`CustomersDbContext.cs`:

```csharp
    /// <summary>Spec 2026-10-10 §7.2 (D-A11): exactamente las columnas que antes eran NOT NULL. Teléfono, correo y
    /// ciudad siguen exigidos por CustomerWriteRules, como antes.</summary>
    public const string CompleteFieldsCheck =
        "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL "
        + "AND address IS NOT NULL AND country IS NOT NULL AND classification_id IS NOT NULL)";
```

- `:31`: `customer.ToTable("customers", "customers", table => { table.HasCheckConstraint("CK_customers_completeness", "completeness IN ('Complete', 'Incomplete')"); table.HasCheckConstraint("CK_customers_complete_fields", CompleteFieldsCheck); });`
- `:90-93`: se quita `.IsRequired()` de `Country`.
- Después de `:126`:

```csharp
        customer.Property(value => value.Completeness).HasColumnName("completeness").HasConversion<string>().HasMaxLength(16);
        customer.Ignore(value => value.IsComplete);
        customer.Property(value => value.WhatsAppUserId).HasColumnName("whatsapp_user_id").HasMaxLength(Customer.WhatsAppUserIdMaxLength);
        // Spec 2026-10-10 §7.2: la identidad de WhatsApp y el árbitro de la carrera de §9.4. CustomersUnitOfWork la
        // traduce por este nombre (T2).
        customer.HasIndex(value => new { value.TenantId, value.WhatsAppUserId }, "IX_customers_tenant_whatsapp_user_id")
            .IsUnique().HasFilter("whatsapp_user_id IS NOT NULL");
        customer.HasIndex(value => value.TenantId, "IX_customers_tenant_incomplete").HasFilter("completeness = 'Incomplete'");
```

- `:133`: `customer.HasIndex(value => value.TenantId, "IX_customers_tenant");` — **con nombre**: sin él, EF funde este índice con `IX_customers_tenant_incomplete` (misma columna) y el segundo pisa el filtro del primero.

Generar y editar:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet ef migrations add AddCustomerCompleteness --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations
```

En el `Up` generado, a mano:
1. El `AddColumn<string>("completeness", …)` lleva `defaultValue: "Complete"` (EF pone `""`, que el `CHECK` rechaza).
2. Después de los `AlterColumn` y **antes** de los `AddCheckConstraint`: `migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN completeness DROP DEFAULT;");`
3. Confirmar que no aparece ningún `DropIndex`/`CreateIndex` de `IX_customers_tenant` (si aparece, el nombre del Step 8 no quedó bien).

En el `Down`, **primera línea**: `migrationBuilder.Sql("DELETE FROM customers.customers WHERE completeness = 'Incomplete';");` (el `SET NOT NULL` de `cuc`, `identification_type`, … moriría con `23502` sobre un incompleto).

- [ ] **Step 9: GREEN de la migración y de lo que ya existía**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build --filter "FullyQualifiedName~CustomerCompletenessMigrationTests|FullyQualifiedName~CustomerContactAddressMigrationTests|FullyQualifiedName~CustomerApiTests|FullyQualifiedName~CustomerExportApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: todo verde. `CustomerApiTests` y `CustomerExportApiTests` prueban que el barrido no cambió las respuestas de una ficha completa.

- [ ] **Step 10: Formato y commit**

Chequeo de formato de «Antes de empezar». Commit con las rutas tocadas (dominio, normalizador, DbContext, migración + designer + snapshot, archivos del barrido, pruebas):

```
feat(customers): ficha incompleta y BSUID de WhatsApp en el cliente

Customer gana completeness y whatsapp_user_id (spec 2026-10-10 §6.2, §7.2). CUC, documento,
dirección, país y clasificación pasan a anulables; CK_customers_complete_fields los exige en una
ficha completa. Migración AddCustomerCompleteness con Down que borra los incompletos primero.
```

---

### Task 2: Customers — `ICustomerWhatsAppDirectory` (asegurar, reemplazar BSUID, referencias y búsqueda por nombre)

Spec §6.2 («Puerto nuevo para Messaging»), §8.2, §9.4, D-A6, D-A9. Lo que Messaging le pide a Customers por el adaptador de la T9: asegurar el cliente de quien escribe (por BSUID → por teléfono → incompleto nuevo), cambiarle el BSUID, resolver nombres por id y buscar ids por nombre. La carrera la arbitra el índice único y la traduce la unidad de trabajo (P3).

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerWhatsAppDirectory.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerWhatsAppDirectory.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/IncompleteCustomerProfile.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/WhatsAppUserIdTakenException.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerAuditActions.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs:151` (cuatro métodos antes de `Add`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` (implementación)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersUnitOfWork.cs:15-21` (constante), `:57-67` (rama nueva después de la del CUC)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs:45-48` (registro)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/IncompleteCustomerProfileTests.cs` (nuevo), `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWhatsAppDirectoryTests.cs` (nuevo)

**Interfaces:**
- Consumes (T1): `Customer.CreateIncomplete`, `AttachWhatsAppUserId`, `ReplaceWhatsAppUserId`, `Completeness`, `IPhoneNumberNormalizer.IsKnownRegion/RegionOf`, `IX_customers_tenant_whatsapp_user_id`.
- Produces (T3, T9, T10, T11):

```csharp
public sealed record WhatsAppContact(string UserId, string? PhoneE164, string? ProfileName, string? Username);
public enum EnsureOutcome { Existing, Linked, Created }
public sealed record EnsuredCustomer(Guid CustomerId, EnsureOutcome Outcome);
public sealed record CustomerWhatsAppRef(Guid Id, string Name, bool IsComplete);
public interface ICustomerWhatsAppDirectory
{
    Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken);
    /// <returns>true si lo cambió; false si nadie tenía el anterior o el nuevo ya es de otro cliente.</returns>
    Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
}
public static class CustomerAuditActions { public const string CreatedFromMessaging = "customers.customer.created_from_messaging"; public const string Completed = "customers.customer.completed"; }
public static class IncompleteCustomerProfile { public const string FallbackName = "Contacto de WhatsApp"; string NameFor(WhatsAppContact); string? CountryFor(WhatsAppContact, IPhoneNumberNormalizer); }
```

`ReplaceWhatsAppUserIdAsync` devuelve `bool` (el spec dice `Task`): así el adaptador de Bootstrapper, que sí tiene logger, registra el «ya es de otro cliente» que pide §8.3 sin meter logging en Customers.Application.

- [ ] **Step 1: Las unitarias del perfil (RED)**

`tests/Modules/Customers/Modules.Customers.UnitTests/IncompleteCustomerProfileTests.cs`:

```csharp
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-10 §6.2: el nombre del incompleto (perfil → username → teléfono → texto fijo) y su
/// país (prefijo del BSUID si es una región conocida → región del teléfono → nada).</summary>
public sealed class IncompleteCustomerProfileTests
{
    private sealed class RegionsOnlyNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone;

        public bool IsKnownRegion(string regionCode) => regionCode is "CO" or "US";

        public string? RegionOf(string? e164) => e164?.StartsWith("+1", StringComparison.Ordinal) == true ? "US" : null;
    }

    [Theory]
    [InlineData(" Laura Pérez ", "laura.p", "+573001234567", "Laura Pérez")]
    [InlineData(null, "laura.p", "+573001234567", "laura.p")]
    [InlineData("  ", null, "+573001234567", "+573001234567")]
    [InlineData(null, null, null, IncompleteCustomerProfile.FallbackName)]
    public void TheNameFollowsTheOwnersOrder(string? profileName, string? username, string? phone, string expected) =>
        Assert.Equal(expected, IncompleteCustomerProfile.NameFor(new WhatsAppContact("CO.1", phone, profileName, username)));

    [Fact]
    public void ALongProfileNameIsCutToTheColumn() =>
        Assert.Equal(Customer.NameMaxLength, IncompleteCustomerProfile.NameFor(new WhatsAppContact("CO.1", null, new string('a', 300), null)).Length);

    [Theory]
    [InlineData("CO.1349120865530274", "+14155550100", "CO")]
    [InlineData("XX.1349120865530274", "+14155550100", "US")]
    [InlineData("XX.1349120865530274", null, null)]
    public void TheCountryComesFromTheBsuidThenThePhone(string userId, string? phone, string? expected) =>
        Assert.Equal(expected, IncompleteCustomerProfile.CountryFor(new WhatsAppContact(userId, phone, null, null), new RegionsOnlyNormalizer()));
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~IncompleteCustomerProfileTests"
```

Esperado: `error CS0246: The type or namespace name 'WhatsAppContact' could not be found`.

- [ ] **Step 2: Los tipos y el perfil (GREEN de las unitarias)**

`ICustomerWhatsAppDirectory.cs`:

```csharp
namespace Modules.Customers.Application;

/// <summary>Quien escribió por WhatsApp, como lo ve Customers: BSUID, teléfono en E.164 con «+» si Meta lo
/// mandó, nombre de perfil y usuario si los tiene.</summary>
public sealed record WhatsAppContact(string UserId, string? PhoneE164, string? ProfileName, string? Username);

public enum EnsureOutcome
{
    /// <summary>Ya había un cliente con ese BSUID.</summary>
    Existing,

    /// <summary>Se encontró por teléfono (D-M7) y se le puso el BSUID si no tenía otro (D-A6).</summary>
    Linked,

    /// <summary>Se creó un cliente incompleto.</summary>
    Created,
}

public sealed record EnsuredCustomer(Guid CustomerId, EnsureOutcome Outcome);

public sealed record CustomerWhatsAppRef(Guid Id, string Name, bool IsComplete);

/// <summary>
/// Spec 2026-10-10 §6.2: lo que Messaging necesita de Customers para atar una conversación a un cliente, con
/// adaptador en Bootstrapper (<c>MessagingCustomerDirectory</c>). Messaging no referencia Customers.
/// </summary>
public interface ICustomerWhatsAppDirectory
{
    /// <summary>§8.2: por BSUID → por teléfono → incompleto nuevo. Idempotente: un reintento encuentra el
    /// cliente por BSUID. Dos llamadas concurrentes con el mismo BSUID nuevo devuelven el mismo cliente.</summary>
    Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken);

    /// <summary>§8.3: el cliente con <paramref name="previous"/> pasa a <paramref name="current"/>. <c>false</c>
    /// si nadie tenía el anterior o si el nuevo ya es de otro cliente (no se toca). El teléfono no cambia.</summary>
    Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);

    /// <summary>§6.1.2: nombre y completitud de los clientes de una página; los que no están no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>§6.1.6: ids de los clientes cuyo nombre contiene <paramref name="term"/> (usa
    /// <c>IX_customers_name_trgm</c>), hasta <paramref name="cap"/>.</summary>
    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
}
```

`WhatsAppUserIdTakenException.cs`:

```csharp
namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-10 §8.2 y §9.4: otro proceso puso el mismo BSUID primero (<c>IX_customers_tenant_whatsapp_user_id</c>).
/// La lanza <c>CustomersUnitOfWork</c> (P3) y la atrapa <see cref="CustomerWhatsAppDirectory"/>, que relee. Nunca
/// llega a HTTP: ningún endpoint escribe el BSUID.
/// </summary>
public sealed class WhatsAppUserIdTakenException : Exception
{
    public WhatsAppUserIdTakenException()
        : base("Another customer of this tenant already has that WhatsApp user id.")
    {
    }

    public WhatsAppUserIdTakenException(Exception innerException)
        : base("Another customer of this tenant already has that WhatsApp user id.", innerException)
    {
    }

    public WhatsAppUserIdTakenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WhatsAppUserIdTakenException(string message)
        : base(message)
    {
    }
}
```

`CustomerAuditActions.cs`:

```csharp
namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §6.2 (D-A9): <see cref="ICustomersAuditPublisher.Publish"/> no tiene metadatos, así que
/// el origen va en la acción. Las acciones viejas siguen como literales en sus handlers.</summary>
public static class CustomerAuditActions
{
    public const string CreatedFromMessaging = "customers.customer.created_from_messaging";
    public const string Completed = "customers.customer.completed";
}
```

`IncompleteCustomerProfile.cs`:

```csharp
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §6.2: nombre y país de un cliente que nace de WhatsApp. Decisión del owner, en ese orden.</summary>
public static class IncompleteCustomerProfile
{
    public const string FallbackName = "Contacto de WhatsApp";

    public static string NameFor(WhatsAppContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var name = FirstNonBlank(contact.ProfileName, contact.Username, contact.PhoneE164) ?? FallbackName;
        return name.Length <= Customer.NameMaxLength ? name : name[..Customer.NameMaxLength];
    }

    /// <summary>El prefijo ISO del BSUID («CO.…») si la librería conoce esa región; si no, la región del teléfono.</summary>
    public static string? CountryFor(WhatsAppContact contact, IPhoneNumberNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(normalizer);
        if (contact.UserId.Length > 3 && contact.UserId[2] == '.')
        {
            var prefix = contact.UserId[..2].ToUpperInvariant();
            if (normalizer.IsKnownRegion(prefix))
            {
                return prefix;
            }
        }

        return normalizer.RegionOf(contact.PhoneE164);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~IncompleteCustomerProfileTests"
```

Esperado: verde (el compilador todavía no pide `CustomerWhatsAppDirectory`).

- [ ] **Step 3: Las de integración (RED)**

`tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWhatsAppDirectoryTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.2, §8.3 y §9.4 contra la base: crear el incompleto (con auditoría), vincular por
/// teléfono sin pisar otro BSUID (D-A6), la carrera de dos entregas del mismo BSUID (Review Focus RF9), el
/// reemplazo de BSUID y las lecturas por id y por nombre.</summary>
public sealed class CustomerWhatsAppDirectoryTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<T> InScopeAsync<T>(QepApiFactory factory, Func<ICustomerWhatsAppDirectory, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>());
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task ANewBsuidCreatesAnIncompleteCustomerOnceAndAuditsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var contact = new WhatsAppContact("CO.1349120865530274", "+573001234567", "Laura Pérez", "laura.p");

        var first = await InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken));
        var again = await InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken));

        Assert.Equal(EnsureOutcome.Created, first.Outcome);
        Assert.Equal(new EnsuredCustomer(first.CustomerId, EnsureOutcome.Existing), again);
        Assert.Equal("Incomplete|Laura Pérez|CO|+573001234567|CO.1349120865530274", await ScalarAsync(database.GetConnectionString(),
            $"SELECT completeness || '|' || name || '|' || country || '|' || phone_e164 || '|' || whatsapp_user_id FROM customers.customers WHERE id = '{first.CustomerId}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.CreatedFromMessaging}' AND payload->>'actorId' = '{Guid.Empty}'"));
    }

    [Fact]
    public async Task AnExistingPhoneIsLinkedAndKeepsItsFirstBsuid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, city.CityId, classification.Id); // teléfono 310 935 2187 → +573109352187

        var linked = await InScopeAsync(factory, directory => directory.EnsureAsync(
            Tenant, new WhatsAppContact("CO.AAA", "+573109352187", "Otra", null), TestContext.Current.CancellationToken));
        var otherPortfolio = await InScopeAsync(factory, directory => directory.EnsureAsync(
            Tenant, new WhatsAppContact("CO.BBB", "+573109352187", "Otra", null), TestContext.Current.CancellationToken));

        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Linked), linked);
        Assert.Equal(new EnsuredCustomer(existing.Id, EnsureOutcome.Linked), otherPortfolio);
        Assert.Equal("CO.AAA|Complete", await ScalarAsync(database.GetConnectionString(),
            $"SELECT whatsapp_user_id || '|' || completeness FROM customers.customers WHERE id = '{existing.Id}'"));
    }

    // RF9: la carrera la arbitra IX_customers_tenant_whatsapp_user_id; el perdedor relee.
    [Fact]
    public async Task ConcurrentEnsuresOfTheSameBsuidCreateOneCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var contact = new WhatsAppContact("CO.RACE", null, "Laura", null);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            InScopeAsync(factory, directory => directory.EnsureAsync(Tenant, contact, TestContext.Current.CancellationToken))));

        Assert.Single(results.Select(result => result.CustomerId).Distinct());
        Assert.Single(results, result => result.Outcome == EnsureOutcome.Created);
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(), "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.RACE'"));
    }

    [Fact]
    public async Task ReplacingMovesTheBsuidUnlessTheNewOneBelongsToAnotherCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var laura = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.OLD", null, "Laura", null), TestContext.Current.CancellationToken));
        _ = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.TAKEN", null, "Pedro", null), TestContext.Current.CancellationToken));

        Assert.True(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken)));
        Assert.False(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.OLD", "CO.NEW", TestContext.Current.CancellationToken)));
        Assert.False(await InScopeAsync(factory, d => d.ReplaceWhatsAppUserIdAsync(Tenant, "CO.NEW", "CO.TAKEN", TestContext.Current.CancellationToken)));
        Assert.Equal("CO.NEW", await ScalarAsync(database.GetConnectionString(), $"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{laura.CustomerId}'"));
    }

    [Fact]
    public async Task RefsAndIdsByNameReadOnlyThisTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var laura = await InScopeAsync(factory, d => d.EnsureAsync(Tenant, new WhatsAppContact("CO.L", null, "Laura Pérez", null), TestContext.Current.CancellationToken));

        var refs = await InScopeAsync(factory, d => d.FindRefsAsync(Tenant, [laura.CustomerId, Guid.CreateVersion7()], TestContext.Current.CancellationToken));
        var ids = await InScopeAsync(factory, d => d.FindIdsByNameAsync(Tenant, "pére", 200, TestContext.Current.CancellationToken));
        var foreign = await InScopeAsync(factory, d => d.FindIdsByNameAsync(Guid.CreateVersion7(), "pére", 200, TestContext.Current.CancellationToken));

        Assert.Equal(new CustomerWhatsAppRef(laura.CustomerId, "Laura Pérez", false), Assert.Single(refs).Value);
        Assert.Equal(laura.CustomerId, Assert.Single(ids));
        Assert.Empty(foreign);
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerWhatsAppDirectoryTests"
```

Esperado (RED): `InvalidOperationException: No service for type 'Modules.Customers.Application.ICustomerWhatsAppDirectory' has been registered.` (o el error de compilación si el `using` no resuelve todavía; se copia el que salga).

- [ ] **Step 4: Repositorio, unidad de trabajo y el directorio**

`ICustomerRepository.cs`, antes de `void Add(Customer customer);`:

```csharp
    /// <summary>Spec 2026-10-10 §8.2: el cliente con ese BSUID, con tracking (el llamador puede cambiarle el BSUID).</summary>
    Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken);

    /// <summary>§8.2 con la regla de D-M7: el más viejo con ese <c>phone_e164</c>, luego el id menor. Con tracking.</summary>
    Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
```

`CustomerRepository.cs` (al final de la clase; usa el mismo `Include` de direcciones que `FindAsync`):

```csharp
    public Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken) =>
        dbContext.Customers
            .Include(customer => customer.Addresses)
            .SingleOrDefaultAsync(customer => customer.TenantId == tenantId && customer.WhatsAppUserId == whatsAppUserId, cancellationToken);

    public Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
        dbContext.Customers
            .Include(customer => customer.Addresses)
            .Where(customer => customer.TenantId == tenantId && customer.PhoneE164 == phoneE164)
            .OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.Select(id => new CustomerId(id)).ToArray();
        // El id viaja con su conversión: se arma el ref en memoria y no en la proyección SQL.
        var rows = await dbContext.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && wanted.Contains(customer.Id))
            .Select(customer => new { customer.Id, customer.Name, customer.Completeness })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new CustomerWhatsAppRef(row.Id.Value, row.Name, row.Completeness == CustomerCompleteness.Complete)).ToArray();
    }

    public async Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(term);
        var pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var ids = await dbContext.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && EF.Functions.ILike(customer.Name, pattern, "\\"))
            .OrderBy(customer => customer.Id)
            .Select(customer => customer.Id)
            .Take(cap)
            .ToListAsync(cancellationToken);
        return ids.Select(id => id.Value).ToArray();
    }
```

`CustomersUnitOfWork.cs`: constante `private const string WhatsAppUserIdIndex = "IX_customers_tenant_whatsapp_user_id";` y, después de la rama del CUC:

```csharp
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, WhatsAppUserIdIndex))
        {
            // Spec 2026-10-10 §9.4 (P3): dos entregas del mismo BSUID nuevo. CustomerWhatsAppDirectory relee. El tracker
            // se limpia: la fila que no entró quedaría Added y el siguiente SaveChanges del scope (la ingesta procesa
            // varios mensajes por entrega) la volvería a intentar.
            dbContext.ChangeTracker.Clear();
            throw new WhatsAppUserIdTakenException(exception);
        }
```

`CustomerWhatsAppDirectory.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §8.2–§8.3. Corre fuera y antes de la transacción de Messaging (dos DbContext, dos
/// unidades de trabajo): si Messaging falla después, el reintento encuentra el cliente por BSUID.</summary>
public sealed class CustomerWhatsAppDirectory(
    ICustomerRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IPhoneNumberNormalizer phoneNormalizer,
    IClock clock) : ICustomerWhatsAppDirectory
{
    private const string Success = "success";

    public async Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (await repository.FindByWhatsAppUserIdAsync(tenantId, contact.UserId, cancellationToken) is { } known)
        {
            return new EnsuredCustomer(known.Id.Value, EnsureOutcome.Existing);
        }

        if (contact.PhoneE164 is { } phone && await repository.FindOldestByPhoneE164Async(tenantId, phone, cancellationToken) is { } byPhone)
        {
            if (byPhone.AttachWhatsAppUserId(contact.UserId))
            {
                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (WhatsAppUserIdTakenException)
                {
                    return await WinnerAsync(tenantId, contact.UserId, cancellationToken);
                }
            }

            return new EnsuredCustomer(byPhone.Id.Value, EnsureOutcome.Linked);
        }

        var now = clock.UtcNow;
        var customer = Customer.CreateIncomplete(
            CustomerId.New(),
            tenantId,
            IncompleteCustomerProfile.NameFor(contact),
            contact.PhoneE164,
            IncompleteCustomerProfile.CountryFor(contact, phoneNormalizer),
            contact.UserId,
            now,
            phoneNormalizer);
        repository.Add(customer);
        // D-A9: actor vacío, no hay persona detrás de un webhook.
        auditPublisher.Publish(tenantId, Guid.Empty, CustomerAuditActions.CreatedFromMessaging, customer.Id.ToString(), Success, now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (WhatsAppUserIdTakenException)
        {
            return await WinnerAsync(tenantId, contact.UserId, cancellationToken);
        }

        return new EnsuredCustomer(customer.Id.Value, EnsureOutcome.Created);
    }

    public async Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken)
    {
        if (await repository.FindByWhatsAppUserIdAsync(tenantId, current, cancellationToken) is not null)
        {
            return false;
        }

        var customer = await repository.FindByWhatsAppUserIdAsync(tenantId, previous, cancellationToken);
        if (customer is null || !customer.ReplaceWhatsAppUserId(previous, current))
        {
            return false;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (WhatsAppUserIdTakenException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        (await repository.FindWhatsAppRefsAsync(tenantId, ids, cancellationToken)).ToDictionary(item => item.Id);

    public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
        repository.FindIdsByNameAsync(tenantId, term, cap, cancellationToken);

    private async Task<EnsuredCustomer> WinnerAsync(Guid tenantId, string userId, CancellationToken cancellationToken)
    {
        var winner = await repository.FindByWhatsAppUserIdAsync(tenantId, userId, cancellationToken)
            ?? throw new InvalidOperationException("The WhatsApp user id was taken but its customer cannot be read back.");
        return new EnsuredCustomer(winner.Id.Value, EnsureOutcome.Existing);
    }
}
```

`CustomersInfrastructureExtensions.cs`, junto a `ICustomerPhoneDirectory`:

```csharp
        // Spec 2026-10-10 §6.2: el cliente de quien escribe por WhatsApp (asegurar, BSUID, nombres, búsqueda).
        services.AddScoped<ICustomerWhatsAppDirectory, CustomerWhatsAppDirectory>();
```

- [ ] **Step 5: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build --filter "FullyQualifiedName~CustomerWhatsAppDirectoryTests"
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build --filter "FullyQualifiedName~IncompleteCustomerProfileTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde. Si `ConcurrentEnsuresOfTheSameBsuidCreateOneCustomer` falla con `DbUpdateException` en vez de releer, la rama de la unidad de trabajo no está antes de un `catch` más general o el nombre del índice no coincide con el de la migración.

- [ ] **Step 6: Formato y commit**

```
feat(customers): asegurar el cliente de quien escribe por WhatsApp

ICustomerWhatsAppDirectory (spec 2026-10-10 §6.2, §8.2): por BSUID, por teléfono (D-M7, sin pisar
otro BSUID, D-A6) o incompleto nuevo con auditoría created_from_messaging. La carrera la arbitra
IX_customers_tenant_whatsapp_user_id: la unidad de trabajo la traduce y limpia el tracker.
```

---

### Task 3: Customers — `isComplete` en el contrato, filtro, completar con el `PUT` y columna en el export

Spec §5.2, §6.2 («Lista», «Export», `Customer.Complete`), D-A9. El `PUT` de siempre sobre un incompleto emite el CUC como el alta y deja la ficha `Complete`.

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs` (`CustomerDto`, `CustomerResponse`, `CustomerListItemResponse` ganan `bool IsComplete` al final)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerMapping.cs:12-51` (`customer.IsComplete`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ListCustomers.cs:18-27` (query), `:62-94` (handler con validador), validador nuevo en el mismo archivo
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs:25-34` (`SearchAsync` gana `bool? isComplete`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs:101-176` (filtro)
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs:155-186` (query string), `:416-450` (respuestas)
- Modify: `src/Modules/Customers/Modules.Customers.Application/UpdateCustomer.cs:31-125` (rama de completar)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs:24-36,75-103` (columna «Estado de la ficha»)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs:3` y `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs:25-35` (`CustomerPhoneMatch` gana `IsComplete`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompletenessApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: T1 (`Customer.Complete`, `IsComplete`), T2 (`ICustomerWhatsAppDirectory.EnsureAsync` para sembrar incompletos en las pruebas, `CustomerAuditActions.Completed`).
- Produces: `CustomerDto.IsComplete`, `CustomerResponse.IsComplete`, `CustomerListItemResponse.IsComplete`; `ListCustomersQuery(…, int Page, int PageSize, string? IsComplete = null)`; `ListCustomersValidator`; `CustomerPhoneMatch(Guid Id, string Name, bool IsComplete)` (lo lee el adaptador de Messaging en la T11).

- [ ] **Step 1: Las pruebas (RED)**

`tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompletenessApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.2: <c>isComplete</c> en el detalle y la lista, el filtro, completar con el PUT
/// (CUC nuevo y auditoría <c>customers.customer.completed</c>), un PUT incompleto sigue siendo 422, y el export
/// con la columna «Estado de la ficha».</summary>
public sealed class CustomerCompletenessApiTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<Guid> SeedIncompleteAsync(QepApiFactory factory, string userId = "CO.1349120865530274", string name = "Laura Pérez")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        return (await directory.EnsureAsync(Tenant, new WhatsAppContact(userId, "+573001234567", name, null), TestContext.Current.CancellationToken)).CustomerId;
    }

    [Fact]
    public async Task TheDetailAndTheListSayWhetherTheRecordIsCompleteAndTheListFilters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var complete = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var incomplete = await SeedIncompleteAsync(factory);

        var detail = await client.GetFromJsonAsync<JsonElement>($"{CustomersUrl()}/{incomplete}", TestContext.Current.CancellationToken);
        var onlyIncomplete = await ListAsync(client, "?isComplete=false");
        var onlyComplete = await ListAsync(client, "?isComplete=TRUE");
        var all = await ListAsync(client, string.Empty);

        Assert.False(detail.GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("cuc").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("classification").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("identificationType").ValueKind);
        Assert.Equal("Laura Pérez", detail.GetProperty("name").GetString());
        Assert.Equal(incomplete, Assert.Single(onlyIncomplete.Items).Id);
        Assert.Equal(complete.Id, Assert.Single(onlyComplete.Items).Id);
        Assert.True(Assert.Single(onlyComplete.Items).IsComplete);
        Assert.Equal(2, all.Total);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public async Task AnUnknownIsCompleteIsAValidationErrorOnThatKey(string value)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);

        var response = await client.GetAsync($"{CustomersUrl()}?isComplete={value}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["isComplete"], await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task APutWithEverythingCompletesTheRecordWithANewCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var incomplete = await SeedIncompleteAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{incomplete}",
            NewCustomerBody(city.CityId, classification.Id, name: "Laura Pérez", identificationType: "CC", identificationNumber: "1020304050"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("isComplete").GetBoolean());
        Assert.StartsWith(classification.Prefix, body.GetProperty("cuc").GetString(), StringComparison.Ordinal);
        Assert.Single(body.GetProperty("addresses").EnumerateArray());
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var audit = new NpgsqlCommand(
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.Completed}' AND payload->>'resourceId' = '{incomplete}'",
            connection);
        Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        // El BSUID sobrevive a completar la ficha: la conversación sigue atada a este cliente.
        await using var bsuid = new NpgsqlCommand($"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{incomplete}'", connection);
        Assert.Equal("CO.1349120865530274", (string)(await bsuid.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task AnIncompletePutIsStillTheUsualValidationError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var incomplete = await SeedIncompleteAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{incomplete}",
            NewCustomerBody(city.CityId, classification.Id, identificationNumber: string.Empty),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("identificationNumber", await ValidationFieldsAsync(response));
        var detail = await client.GetFromJsonAsync<JsonElement>($"{CustomersUrl()}/{incomplete}", TestContext.Current.CancellationToken);
        Assert.False(detail.GetProperty("isComplete").GetBoolean());
    }
}
```

La prueba del export va en `CustomerExportApiTests` (ya sabe bajar y abrir el `.xlsx`): una prueba nueva `TheExportIncludesIncompleteRecordsWithTheirStatusColumn` que siembra un completo por HTTP y un incompleto con `SeedIncompleteAsync` (copiar el helper), exporta con el mismo flujo de las pruebas existentes de ese archivo y comprueba con ClosedXML que la cabecera de la columna 15 es `Estado de la ficha`, que la fila del incompleto tiene `Incompleta` en esa columna y celdas vacías en documento, CUC y clasificación, y que la del completo dice `Completa`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerCompletenessApiTests|FullyQualifiedName~CustomerExportApiTests"
```

Esperado (RED): `KeyNotFoundException: The given key was not present in the dictionary` sobre `isComplete`, la lista filtrada devuelve los dos, el `PUT` sobre el incompleto responde 500 (`InvalidOperationException` de `Update`, P13), y el export no tiene la columna 15.

- [ ] **Step 2: Contrato, filtro, completar y export**

`CustomersDtos.cs`: `bool IsComplete` como **último** parámetro de `CustomerDto`, `CustomerResponse` y `CustomerListItemResponse`, con `/// <summary>Spec 2026-10-10 §5.2: con <c>false</c>, CUC, documento, correo, dirección, país, ciudad y clasificación pueden venir en <c>null</c>.</summary>`. `CustomerMapping.ToDto` y los dos mapeos de `CustomerEndpoints.cs:416-450` pasan `customer.IsComplete`.

`ListCustomers.cs`:

```csharp
public sealed record ListCustomersQuery(
    Guid TenantId,
    string? Search,
    string? Name,
    string? IdentificationNumber,
    string? Cuc,
    IReadOnlyCollection<Guid>? DepartmentIds,
    IReadOnlyCollection<Guid>? CityIds,
    int Page,
    int PageSize,
    string? IsComplete = null) : IQuery<CustomerPage>;

/// <summary>Spec 2026-10-10 §5.2 (P2): <c>isComplete</c> llega como texto para que un valor raro sea 422 con su
/// clave y no un 400 del binding.</summary>
public sealed class ListCustomersValidator : AbstractValidator<ListCustomersQuery>
{
    public ListCustomersValidator()
    {
        RuleFor(query => query.IsComplete)
            .Must(value => value is null || bool.TryParse(value, out _))
            .OverridePropertyName("isComplete")
            .WithMessage("isComplete debe ser true o false.");
    }
}
```

`ListCustomersHandler` gana `IValidator<ListCustomersQuery> validator` (último parámetro del constructor); después de autorizar: `await validator.ValidateAndThrowAsync(query, cancellationToken);` y `bool? isComplete = query.IsComplete is null ? null : bool.Parse(query.IsComplete);`, que pasa a `repository.SearchAsync(…, cityIds, isComplete, page, pageSize, cancellationToken)`. El validador lo registra solo `AddValidatorsFromAssemblyContaining<CreateCustomerValidator>()` (`QepServiceCollectionExtensions.cs`, misma asamblea). Agrega `using FluentValidation;`.

`ICustomerRepository.SearchAsync` gana `bool? isComplete` después de `cityIds`; en `CustomerRepository.SearchAsync`:

```csharp
        // Spec 2026-10-10 §5.2: el filtro usa IX_customers_tenant_incomplete.
        if (isComplete is { } complete)
        {
            var completeness = complete ? CustomerCompleteness.Complete : CustomerCompleteness.Incomplete;
            query = query.Where(customer => customer.Completeness == completeness);
        }
```

`CustomerEndpoints.cs:155-186`: parámetro nuevo `string? isComplete = null` y `new ListCustomersQuery(…, page, pageSize, isComplete)`.

`UpdateCustomer.cs`: el handler gana `ICucGenerator cucGenerator` (después de `auditPublisher`). Después de resolver clasificación y ciudad (la ciudad se guarda en una variable `city` como en `CreateCustomer.cs:73-78`, en vez de descartarla), antes de `customer.Update(...)`:

```csharp
        if (!customer.IsComplete)
        {
            // Spec 2026-10-10 §6.2: completar emite el CUC exactamente como el alta (CreateCustomer.cs:84-90) y siembra
            // la libreta como Create. Mismas reglas de validación: el validador ya corrió arriba.
            var sequence = await cucGenerator.NextAsync(command.TenantId, cancellationToken);
            var cuc = CucFormatter.Build(classification.Prefix, city?.DepartmentDivipolaCode ?? CucFormatter.ForeignDepartmentCode, sequence);
            customer.Complete(
                cuc,
                command.Name,
                command.BusinessName,
                city is null ? null : new CustomerAddressDetails { Name = command.Name, Address = command.Address ?? string.Empty, CityId = city.CityId, Phone = command.Phone },
                CustomerMapping.ToIdentification(command.IdentificationType, command.IdentificationNumber),
                new CustomerContactInfo
                {
                    Phone = command.Phone,
                    Email = command.Email,
                    Address = command.Address ?? string.Empty,
                    Country = command.Country,
                    CityId = command.CityId,
                    CityName = command.CityName,
                },
                CustomerMapping.ToCommercialInfo(command.ClassificationId, command.WithRetention, command.VatSurplus),
                now,
                phoneNormalizer);
            auditPublisher.Publish(command.TenantId, executionContext.SubjectId, CustomerAuditActions.Completed, customer.Id.ToString(), "success", now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await customer.ToDtoAsync(geographyLookup, classificationRepository, cancellationToken);
        }
```

`ClosedXmlCustomerExportBuilder.cs`: `private const string CompletenessColumn = "Estado de la ficha";` al final de `Columns`; en `WriteRow`: `sheet.Cell(excelRow, 15).Value = customer.IsComplete ? "Completa" : "Incompleta";`. Al final, no en medio: el importador ubica columnas por nombre de cabecera (`:11-14`) y las de más no le molestan.

`ICustomerPhoneDirectory.cs:3`: `public sealed record CustomerPhoneMatch(Guid Id, string Name, bool IsComplete);`. `CustomerPhoneDirectory.cs:29-34`: proyecta `customer.Completeness` y arma `new CustomerPhoneMatch(row.Id.Value, row.Name, row.Completeness == CustomerCompleteness.Complete)`.

- [ ] **Step 3: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build --filter "FullyQualifiedName~CustomerCompletenessApiTests|FullyQualifiedName~CustomerExportApiTests|FullyQualifiedName~CustomerWriteApiTests|FullyQualifiedName~CustomerPhoneDirectoryTests"
```

Esperado: verde. `CustomerWriteApiTests` cubre que el `PUT` sobre un completo no cambió.

- [ ] **Step 4: Formato y commit**

```
feat(customers): isComplete, filtro, completar la ficha con el PUT y columna en el export

Spec 2026-10-10 §5.2: el PUT sobre un incompleto emite el CUC como el alta y audita
customers.customer.completed; isComplete=true|false filtra la lista (422 con otro valor); el
Excel suma «Estado de la ficha».
```

---

### Task 4: Quotations rechaza al incompleto y Reporting no lo cuenta

Spec §5.3, §6.3, §6.4, D-A4. Review Focus RF10.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCustomerLookup.cs:68-96` (`bool IsComplete = true` al final de `QuotationCustomerRef`)
- Modify: `src/Bootstrapper/QuotationCustomerLookup.cs:87-118` (pasa `customer.IsComplete`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationCustomerEligibility.cs:22-34` (chequeo nuevo entre `client_not_found` y `client_cuc_missing`)
- Modify: `src/Bootstrapper/CustomerReportSource.cs:262-268` (`FilterCustomersAsync`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationCustomerEligibilityTests.cs` (nuevo), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/IncompleteCustomerQuotationApiTests.cs` (nuevo), `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/IncompleteCustomerReportApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: T1 (`Customer.IsComplete`, `Completeness`, columna `completeness`).
- Produces: `QuotationCustomerRef.IsComplete`; código `quotation.quotation.client_incomplete`.

- [ ] **Step 1: Unitaria del orden de los chequeos (RED)**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationCustomerEligibilityTests.cs` (`QuotationCustomerEligibility` es `internal`; la unitaria lo ve por `InternalsVisibleTo`, `Modules.Quotations.Application.csproj:7`):

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-10 §6.3 (RF10): un incompleto es <c>client_incomplete</c>, antes que
/// <c>client_cuc_missing</c> (un incompleto tampoco tiene CUC, y la pantalla tiene que decir «completa la ficha»).</summary>
public sealed class QuotationCustomerEligibilityTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();

    private static QuotationCustomerRef Ref(string? cuc, bool isActive = true, bool isComplete = true, Guid? tenantId = null) =>
        new(ClientId, tenantId ?? TenantId, cuc, isActive, "Laura", null, null, WithRetention: false, VatSurplus: false, IsComplete: isComplete);

    [Theory]
    [InlineData(null, true, false, "quotation.quotation.client_incomplete")]
    [InlineData("CLI08000001", false, false, "quotation.quotation.client_incomplete")]
    [InlineData(null, true, true, "quotation.quotation.client_cuc_missing")]
    [InlineData("CLI08000001", false, true, "quotation.quotation.client_inactive")]
    public void TheChecksRunInOrder(string? cuc, bool isActive, bool isComplete, string expectedCode)
    {
        var error = Assert.Throws<QuotationsDomainException>(() => QuotationCustomerEligibility.Ensure(Ref(cuc, isActive, isComplete), TenantId, ClientId));
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void AnotherTenantIsStillNotFoundEvenIfIncomplete()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            QuotationCustomerEligibility.Ensure(Ref(null, isComplete: false, tenantId: Guid.CreateVersion7()), TenantId, ClientId));
        Assert.Equal("quotation.quotation.client_not_found", error.Code);
    }

    [Fact]
    public void ACompleteActiveCustomerWithCucPasses() =>
        QuotationCustomerEligibility.Ensure(Ref("CLI08000001"), TenantId, ClientId);
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationCustomerEligibilityTests"
```

Esperado: `error CS1739: The best overload for 'QuotationCustomerRef' does not have a parameter named 'IsComplete'`.

- [ ] **Step 2: El chequeo (GREEN de la unitaria)**

`IQuotationCustomerLookup.cs`, último parámetro de `QuotationCustomerRef`:

```csharp
    string? IdentificationNumber = null,
    /// <summary>Spec 2026-10-10 §6.3: a un incompleto no se le cotiza ni se le vende. Default <c>true</c> para que
    /// los dobles de prueba viejos sigan describiendo un cliente cotizable.</summary>
    bool IsComplete = true);
```

`QuotationCustomerLookup.cs:116-117`: después de `customer.IdentificationNumber` agrega `, customer.IsComplete`.

`QuotationCustomerEligibility.cs`, entre el `if` de `client_not_found` y el de `client_cuc_missing`:

```csharp
        // Spec 2026-10-10 §6.3 (D-A4): antes que client_cuc_missing. Un incompleto tampoco tiene CUC, y sin este
        // orden la pantalla diría «falta el CUC» en vez de «completa la ficha». Cubre crear, cambiar cliente, enviar y
        // convertir en pedido: los cuatro llamadores de Ensure.
        if (!customer.IsComplete)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.client_incomplete",
                "The client record is incomplete; complete it before quoting or selling.");
        }
```

Y el `<summary>` de la clase suma «o con la ficha incompleta».

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationCustomerEligibilityTests|FullyQualifiedName~CreateQuotationHandlerTests|FullyQualifiedName~SendQuotationHandlerTests"
```

Esperado: verde.

- [ ] **Step 3: Las de integración (RED, después GREEN sin cambio de código)**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/IncompleteCustomerQuotationApiTests.cs`. Por HTTP no se crea un incompleto (spec §11), así que la prueba crea uno completo por la API y lo **vuelve incompleto por SQL** (`CK_customers_complete_fields` deja un incompleto con los campos llenos):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.3 (RF10): crear, cambiar el cliente, enviar y convertir en pedido con un cliente
/// incompleto responden 422 <c>quotation.quotation.client_incomplete</c>.</summary>
public sealed class IncompleteCustomerQuotationApiTests
{
    private static async Task MarkIncompleteAsync(string connectionString, Guid customerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("UPDATE customers.customers SET completeness = 'Incomplete' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", customerId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertIncompleteAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.quotation.client_incomplete", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreatingAndChangingTheClientToAnIncompleteOneIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var complete = await CreateActiveCustomerAsync(client, tenantId);
        var incomplete = await CreateActiveCustomerAsync(client, tenantId);
        await MarkIncompleteAsync(database.GetConnectionString(), incomplete);
        var draft = await CreateQuotationAsync(client, tenantId, complete);

        var create = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId), new CreateQuotationRequest(incomplete, null, null, null, null, null), TestContext.Current.CancellationToken);
        var change = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/client", new ChangeQuotationClientRequest(incomplete), TestContext.Current.CancellationToken);

        await AssertIncompleteAsync(create);
        await AssertIncompleteAsync(change);
    }

    [Fact]
    public async Task SendingAndConvertingForAClientThatBecameIncompleteIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        // Una segunda, en borrador y lista para enviar: lo mismo que arma CreateSentQuotationAsync antes del /send.
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var draft = await CreateQuotationAsync(
            client, tenantId, customerId, billingAccount: new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
        await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{draft.Id}/items", new AddQuotationItemRequest(productId, 1m), TestContext.Current.CancellationToken);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        await MarkIncompleteAsync(database.GetConnectionString(), customerId);

        var send = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/send", new SendQuotationRequest(pdfFileId), TestContext.Current.CancellationToken);
        var convert = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{sent.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, sent.Total)]),
            TestContext.Current.CancellationToken);

        await AssertIncompleteAsync(send);
        await AssertIncompleteAsync(convert);
    }
}
```

`tests/Modules/Reporting/Modules.Reporting.IntegrationTests/IncompleteCustomerReportApiTests.cs` (mismos helpers que `CustomerReportApiTests`; `CreateActiveCustomerAsync` de `ReportingApiHarness` devuelve un objeto con `.Id`):

```csharp
using System.Net.Http.Json;
using Npgsql;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>Spec 2026-10-10 §6.4: el reporte de clientes y su resumen no cuentan incompletos (sin clasificación ni
/// ciudad distorsionarían los cortes).</summary>
public sealed class IncompleteCustomerReportApiTests
{
    [Fact]
    public async Task TheCustomerReportAndItsSummaryIgnoreIncompleteRecords()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var counted = await CreateActiveCustomerAsync(client, tenant.TenantId);
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO customers.customers (id, tenant_id, name, is_active, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
                VALUES (gen_random_uuid(), @tenantId, 'Laura', true, 'Incomplete', 'CO.1', false, false, 1, now(), now())
                """,
                connection);
            insert.Parameters.AddWithValue("tenantId", tenant.TenantId);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var page = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);
        var summary = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(counted.Id, Assert.Single(page.Items).CustomerId);
        Assert.Equal(1, page.Total);
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
    }
}
```

Antes de correr, confirma en `CustomerReportSummaryApiTests` la ruta del resumen y el nombre del campo del total (si no es `total`, usa el que lea esa prueba).

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~IncompleteCustomerQuotationApiTests"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build --filter "FullyQualifiedName~IncompleteCustomerReportApiTests"
```

Esperado: Quotations **verde** (el chequeo ya está) y Reporting **RED** (`Assert.Single` ve dos ítems).

- [ ] **Step 4: El filtro del reporte (GREEN)**

`CustomerReportSource.cs:265-267`:

```csharp
        // Spec 2026-10-10 §6.4: un incompleto no tiene clasificación ni ciudad; contarlo distorsionaría los cortes por esos campos.
        var query = customers.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == criteria.TenantId && customer.Completeness == CustomerCompleteness.Complete);
```

(`using Modules.Customers.Domain;` ya está: el archivo usa `ClientClassificationId`.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build --filter "FullyQualifiedName~IncompleteCustomerReportApiTests|FullyQualifiedName~CustomerReportApiTests|FullyQualifiedName~CustomerReportSummaryApiTests"
```

Esperado: verde.

- [ ] **Step 5: Formato y commit**

```
feat(quotations): no se cotiza ni se vende a un cliente con la ficha incompleta

QuotationCustomerEligibility responde quotation.quotation.client_incomplete antes que
client_cuc_missing (spec 2026-10-10 §6.3) en crear, cambiar cliente, enviar y convertir. El
reporte de clientes de Reporting excluye los incompletos (§6.4).
```

---

### Task 5: Messaging — dominio de eventos y BSUID (sin columnas nuevas)

Spec §6.1.1 (forma del BSUID), §6.1.5 (enums y `details`), §10. Todo lo que no cambia el modelo de EF: los valores de enum nuevos no agregan columnas, y la forma del BSUID es una función pura. Las columnas van en la T6.

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageEnums.cs:11-31`
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs:4-15`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:10-14` (clase `partial`, constantes, `IsValidUserId`)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs` (nuevo), `ConversationTests.cs` (una teoría), `MessageColumnCodesTests.cs:30-45`

**Interfaces:**
- Consumes: —
- Produces (T6–T14):

```csharp
public enum MessageDirection { Inbound = 1, Outbound = 2, System = 3 }
MessageKind.Event = 13
public enum ConversationEventType { Taken, Transferred, Released, AutoTaken, Inherited, Resolved, Reopened, CustomerCreated, CustomerLinked, ContactChangedNumber }
public sealed record ConversationEvent(ConversationEventType Type, Guid? Actor = null, Guid? Target = null, Guid? Previous = null, Guid? CustomerId = null, Guid? LinkedConversationId = null);
MessagingErrorCodes.AssignedToOther, MessagingErrorCodes.AssigneeCannotReply
Conversation.UserIdMaxLength = 150, Conversation.UsernameMaxLength = 64, static bool Conversation.IsValidUserId(string? value)
public static class ConversationEventJson { string Serialize(ConversationEvent value); ConversationEvent? Parse(string? json); }
```

- [ ] **Step 1: Las pruebas (RED)**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs`:

```csharp
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-10 §6.1.5: el <c>details</c> de un evento lleva el tipo por nombre y sólo ids, sin las
/// claves que no aplican; lo que no se entiende se lee como «no es un evento», nunca como un 500.</summary>
public sealed class ConversationEventJsonTests
{
    private static readonly Guid Actor = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Target = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    [Fact]
    public void OnlyTheKeysThatApplyAreWritten() =>
        Assert.Equal(
            $$"""{"type":"Transferred","actor":"{{Actor}}","target":"{{Target}}"}""",
            ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Transferred, Actor: Actor, Target: Target)));

    [Fact]
    public void EveryFieldRoundTrips()
    {
        var value = new ConversationEvent(ConversationEventType.ContactChangedNumber, Actor, Target, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(value, ConversationEventJson.Parse(ConversationEventJson.Serialize(value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"Exploded"}""")]
    [InlineData("""{"actor":"01900000-0000-7000-8000-0000000000a1"}""")]
    public void WhatIsNotAnEventParsesAsNull(string? json) =>
        Assert.Null(ConversationEventJson.Parse(json));

    [Fact]
    public void ABadIdIsDroppedNotFatal() =>
        Assert.Equal(new ConversationEvent(ConversationEventType.Released), ConversationEventJson.Parse("""{"type":"Released","actor":"nope"}"""));
}
```

En `ConversationTests.cs`, al final de la clase:

```csharp
    // Spec 2026-10-10 §6.1.1: ISO alfa-2 en mayúsculas + «.» + 1 a 128 alfanuméricos.
    [Theory]
    [InlineData("CO.1349120865530274", true)]
    [InlineData("US.13491208655302741918", true)]
    [InlineData("US.abcXYZ09", true)]
    [InlineData("co.1349", false)]
    [InlineData("COL.1349", false)]
    [InlineData("CO.", false)]
    [InlineData("CO.13-49", false)]
    [InlineData("573001234567", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABsuidHasTheShapeMetaDocuments(string? value, bool expected) =>
        Assert.Equal(expected, Conversation.IsValidUserId(value));

    [Fact]
    public void ABsuidOfMoreThan128AlphanumericsIsRejected()
    {
        Assert.True(Conversation.IsValidUserId("CO." + new string('9', 128)));
        Assert.False(Conversation.IsValidUserId("CO." + new string('9', 129)));
    }
```

`MessageColumnCodesTests.cs`: en `TheCodesAreTheOnesOfTheSpec` agrega `Assert.Equal((short)3, MessageColumnCodes.ToCode(MessageDirection.System));` y `Assert.Equal((short)13, MessageColumnCodes.ToCode(MessageKind.Event));` (las aserciones de los `CHECK` no cambian todavía: van en la T6). En `AnUnknownCodeIsLoudNotSilent`, `[InlineData(13)]` pasa a `[InlineData(14)]`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationEventJsonTests|FullyQualifiedName~ConversationTests|FullyQualifiedName~MessageColumnCodesTests"
```

Esperado: `error CS0103: The name 'ConversationEventJson' does not exist in the current context` (y `IsValidUserId`, `MessageDirection.System`).

- [ ] **Step 2: La implementación**

`MessageEnums.cs`: `System = 3,` en `MessageDirection` con `/// <summary>Spec 2026-10-10 §6.1.5: un evento del sistema en el hilo; nunca va a WhatsApp.</summary>`, y `Event = 13,` al final de `MessageKind` con el mismo comentario.

`ConversationEvent.cs`:

```csharp
namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-10 §6.1.5: qué pasó en la conversación. Se guarda por nombre en <c>details</c>.</summary>
public enum ConversationEventType
{
    Taken,
    Transferred,
    Released,
    AutoTaken,
    Inherited,
    Resolved,
    Reopened,
    CustomerCreated,
    CustomerLinked,
    ContactChangedNumber,
}

/// <summary>
/// Un evento del historial (spec 2026-10-10 §6.1.5): sólo ids de membresías y del cliente; los nombres se
/// resuelven al leer. <see cref="Actor"/> es <c>null</c> cuando lo hizo el sistema. <see cref="LinkedConversationId"/>
/// sólo lo usa <c>ContactChangedNumber</c> cuando el número nuevo ya tenía su conversación (P6). Nunca teléfonos,
/// BSUIDs ni textos de Meta (§11).
/// </summary>
public sealed record ConversationEvent(
    ConversationEventType Type,
    Guid? Actor = null,
    Guid? Target = null,
    Guid? Previous = null,
    Guid? CustomerId = null,
    Guid? LinkedConversationId = null);
```

`MessagingErrorCodes.cs`:

```csharp
    /// <summary>Spec 2026-10-10 §10: enviar a una conversación asignada a otra membresía.</summary>
    public const string AssignedToOther = "messaging.conversation.assigned_to_other";

    /// <summary>Spec 2026-10-10 §10: transferir a una membresía que no es activa del tenant o no tiene manage.</summary>
    public const string AssigneeCannotReply = "messaging.conversation.assignee_cannot_reply";
```

`Conversation.cs`: `public sealed partial class Conversation`, y junto a las constantes:

```csharp
    /// <summary>Spec 2026-10-10 §6.1.1: el BSUID mide hasta 131 (2 + 1 + 128); 150 deja margen.</summary>
    public const int UserIdMaxLength = 150;

    /// <summary>Meta: hasta 35; la columna deja margen sin costo.</summary>
    public const int UsernameMaxLength = 64;

    /// <summary>§6.1.1: código ISO 3166 alfa-2 + «.» + 1 a 128 alfanuméricos.</summary>
    public static bool IsValidUserId(string? value) => value is not null && UserIdShape().IsMatch(value);

    [GeneratedRegex("^[A-Z]{2}\\.[A-Za-z0-9]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdShape();
```

(con `using System.Text.RegularExpressions;`).

`src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §6.1.5: la única forma de escribir y leer el <c>details</c> de un evento. La ingesta (SQL,
/// Infrastructure) y los handlers (EF) escriben con <see cref="Serialize"/>; la lectura del hilo usa <see cref="Parse"/>.</summary>
public static class ConversationEventJson
{
    public static string Serialize(ConversationEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type.ToString());
            WriteId(writer, "actor", value.Actor);
            WriteId(writer, "target", value.Target);
            WriteId(writer, "previous", value.Previous);
            WriteId(writer, "customerId", value.CustomerId);
            WriteId(writer, "linkedConversationId", value.LinkedConversationId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary><c>null</c> si no es un objeto con un <c>type</c> conocido. Un id ilegible se omite.</summary>
    public static ConversationEvent? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !Enum.TryParse<ConversationEventType>(type.GetString(), ignoreCase: false, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return null;
            }

            return new ConversationEvent(
                parsed, ReadId(root, "actor"), ReadId(root, "target"), ReadId(root, "previous"), ReadId(root, "customerId"), ReadId(root, "linkedConversationId"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteId(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(name, id.ToString("D"));
        }
    }

    private static Guid? ReadId(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var id) ? id : null;
}
```

`Enum.TryParse` acepta números («"3"»): el `Enum.IsDefined` corta los que no existen, y un número que sí existe se acepta — inofensivo, porque sólo `Serialize` escribe.

- [ ] **Step 3: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~ConversationEventJsonTests|FullyQualifiedName~ConversationTests|FullyQualifiedName~MessageColumnCodesTests"
```

Esperado: verde.

- [ ] **Step 4: Formato y commit**

```
feat(messaging): eventos del sistema y forma del BSUID en el dominio

MessageDirection.System y MessageKind.Event, ConversationEvent con su JSON de sólo ids, los dos
códigos de asignación y Conversation.IsValidUserId (spec 2026-10-10 §6.1.1, §6.1.5, §10).
```

---

### Task 6: Messaging — esquema `AddBsuidAssignmentAndEvents`, contacto en el contrato y envío por `recipient`

Spec §6.1.1, §6.1.4, §7.1, §8.5 paso 7. Las columnas nuevas de la conversación y del mensaje, sus índices y `CHECK`s, y los tres arreglos que el `wa_id` anulable obliga a hacer en el mismo commit: la ingesta sigue funcionando por teléfono contra el índice viejo ahora parcial (la clave nueva llega en la T7), `ContactDto` cambia de forma, y el envío elige `recipient` (BSUID) o `to` (fila vieja) — Review Focus RF1, parte del envío.

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:22-98` (propiedades, constructor de EF, `StartWithUserId`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageRecord.cs:7-27` (`ReplyToMessageId`, `ReplyToWamid`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:19-21` (constantes), `:45-91` (conversación), `:93-139` (mensaje)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<timestamp>_AddBsuidAssignmentAndEvents.cs` (+ designer, snapshot)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs:6-22` (`ConversationRow`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs:12-31` (proyección y búsqueda por número con `wa_id` anulable)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:8` (`ContactDto`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:40-71` (emparejar sólo filas con teléfono; contacto nuevo)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SendTarget.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs:27`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs:41-78`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs:100`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs:21-32` (puente: índice viejo parcial)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:36-45,161-179`, `SendMessageHandlerTests.cs` (aserción `send.To`), `ConversationReadsTests.cs:27-30`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs:294-304` (helper nuevo de conversación con BSUID), `MessagingPersistenceTests.cs` (índices y `CHECK`s)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTests.cs` (nuevo), `ConversationTests.cs`, `SendMessageApiTests.cs` (una prueba)

**Interfaces:**
- Consumes (T5): `Conversation.IsValidUserId`, `UserIdMaxLength`, `UsernameMaxLength`, `MessagingDbContext` constantes de `CHECK` a actualizar.
- Produces:
  - `Conversation`: `string? UserId`, `string? WaId`, `string? Username`, `string? ParentUserId`, `Guid? CustomerId`, `Guid? AssignedMemberId`, `DateTimeOffset? AssignedAt`; `static Conversation StartWithUserId(Guid id, Guid tenantId, Guid connectionId, string userId, string? waId, string? profileName, DateTimeOffset now)`. `Start(…, string waId, …)` queda para filas viejas (P12).
  - `MessageRecord.ReplyToMessageId (Guid?)`, `MessageRecord.ReplyToWamid (string?)`.
  - `ConversationRow(Guid Id, Guid TenantId, Guid ConnectionId, string? UserId, string? WaId, string? Username, string? ProfileName, Guid? CustomerId, Guid? AssignedMemberId, ConversationStatus Status, int UnreadCount, DateTimeOffset? LastInboundAt, Guid? LastMessageId, MessageDirection? LastMessageDirection, MessageKind? LastMessageKind, string? LastMessagePreview, MessageStatus? LastMessageStatus, DateTimeOffset? LastMessageAt, DateTimeOffset UpdatedAt, long Version)`
  - `ContactDto(string? UserId, string? WaId, string? Username, string? ProfileName)`
  - `SendTarget` / `SendToUserId(string UserId)` / `SendToPhone(string WaId)`; `SendTarget.For(string? userId, string? waId)`.
  - `IWhatsAppCloudClient.SendTextAsync(MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken)`.
  - `MessagingApiHarness.SeedBsuidConversationAsync(factory, tenantId, connectionId, userId, waId, lastInboundAt)`.

- [ ] **Step 1: Pruebas de dominio y del cliente de Graph (RED)**

`ConversationTests.cs`, al final:

```csharp
    [Fact]
    public void ABsuidConversationMayHaveNoPhone()
    {
        var conversation = Conversation.StartWithUserId(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "CO.1349120865530274", null, "Laura", Now);

        Assert.Equal("CO.1349120865530274", conversation.UserId);
        Assert.Null(conversation.WaId);
        Assert.Null(conversation.AssignedMemberId);
        Assert.Null(conversation.CustomerId);
        Assert.Equal(1, conversation.Version);
    }

    [Theory]
    [InlineData("573001234567", "57300123456X")]
    [InlineData("co.1349", null)]
    public void AStartWithAMalformedIdentityIsRejected(string userId, string? waId) =>
        Assert.Throws<ArgumentException>(() =>
            Conversation.StartWithUserId(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), userId, waId, null, Now));
```

`tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-10 §6.1.4: <c>recipient</c> con BSUID o <c>to</c> con teléfono, nunca los dos (si van los
/// dos, Meta usa <c>to</c>, §3); con cita, <c>context.message_id</c>.</summary>
public sealed class WhatsAppCloudClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"messages":[{"id":"wamid.out"}]}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            WhatsAppCloudClient.ConfigureClient(client);
            return client;
        }
    }

    private static async Task<string> BodyOfAsync(SendTarget target, string? contextWamid)
    {
        using var handler = new CapturingHandler();
        var client = new WhatsAppCloudClient(new SingleClientFactory(handler), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

        var result = await client.SendTextAsync(new MessagingSender("111", "token"), target, "hola", "qep:x", contextWamid, TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        return handler.Body!;
    }

    [Fact]
    public async Task ABsuidGoesAsRecipientAndNeverAsTo()
    {
        var body = await BodyOfAsync(new SendToUserId("CO.1349120865530274"), null);

        Assert.Contains("\"recipient\":\"CO.1349120865530274\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegacyPhoneGoesAsToAndNeverAsRecipient()
    {
        var body = await BodyOfAsync(new SendToPhone("573001234567"), null);

        Assert.Contains("\"to\":\"573001234567\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recipient\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuotedReplyCarriesTheContext() =>
        Assert.Contains("\"context\":{\"message_id\":\"wamid.quoted\"}", await BodyOfAsync(new SendToUserId("CO.1"), "wamid.quoted"), StringComparison.Ordinal);

    [Fact]
    public void TheTargetPrefersTheBsuid()
    {
        Assert.Equal(new SendToUserId("CO.1"), SendTarget.For("CO.1", "573001234567"));
        Assert.Equal(new SendToPhone("573001234567"), SendTarget.For(null, "573001234567"));
        Assert.Throws<InvalidOperationException>(() => SendTarget.For(null, null));
    }
}
```

Si el constructor de `MessagingMetaOptions` exige algo o `ConfigureClient` no es `internal`, ajusta el doble (ambos son `internal` en `WhatsAppCloudClient.cs:30-37`, visibles por `InternalsVisibleTo`, `Modules.Messaging.Infrastructure.csproj:26`).

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~WhatsAppCloudClientTests"
```

Esperado: `error CS0117: 'Conversation' does not contain a definition for 'StartWithUserId'` y `CS0246` por `SendTarget`.

- [ ] **Step 2: Dominio, `SendTarget` y cliente de Graph**

`Conversation.cs`:
- Constructor de EF: queda vacío (`private Conversation() { }`): `WaId` ahora es anulable.
- Propiedades nuevas (después de `ConnectionId`):

```csharp
    /// <summary>Spec 2026-10-10 §6.1.1: el BSUID, la clave de la conversación. <c>null</c> sólo en una fila vieja que
    /// todavía no recibió un entrante con BSUID (§8.1, adopción).</summary>
    public string? UserId { get; private set; }

    /// <summary>Dígitos, sin «+», como lo manda Meta. <c>null</c> cuando Meta no mandó el teléfono (§3).</summary>
    public string? WaId { get; private set; }

    public string? Username { get; private set; }

    /// <summary>Se guarda, no se usa en este slice (§1, fuera de alcance).</summary>
    public string? ParentUserId { get; private set; }

    /// <summary>§6.1.2: lo fija la ingesta; reemplaza el emparejamiento por teléfono al leer (D-A12).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>§6.1.3: referencia blanda a la membresía (sin FK, como <c>sent_by_member_id</c>).</summary>
    public Guid? AssignedMemberId { get; private set; }

    public DateTimeOffset? AssignedAt { get; private set; }
```

- `Start` (`:77-98`) sigue igual salvo el `<summary>`: «P12: una fila **vieja**, sólo teléfono. La usan el harness y las pruebas de adopción; producción crea por SQL con BSUID.»
- Nuevo:

```csharp
    /// <summary>Spec 2026-10-10 §6.1.1: una conversación nueva por BSUID, con teléfono si Meta lo mandó. En producción la
    /// crea la ingesta por SQL; esto lo usan las pruebas y el harness.</summary>
    public static Conversation StartWithUserId(
        Guid id, Guid tenantId, Guid connectionId, string userId, string? waId, string? profileName, DateTimeOffset now)
    {
        if (!IsValidUserId(userId))
        {
            throw new ArgumentException("A user id is a business-scoped user id (CC.alphanumerics).", nameof(userId));
        }

        if (waId is not null && (waId.Length is 0 or > WaIdMaxLength || !waId.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException("A wa_id is 1 to 20 digits.", nameof(waId));
        }

        return new Conversation
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            UserId = userId,
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
```

`src/Modules/Messaging/Modules.Messaging.Application/SendTarget.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-10 §6.1.4: a quién se le manda. Con BSUID va <c>recipient</c>; una fila vieja sin BSUID va con
/// <c>to</c>. Nunca los dos: si llegan los dos, Meta usa <c>to</c> (§3) y el BSUID quedaría ignorado.
/// </summary>
public abstract record SendTarget
{
    public static SendTarget For(string? userId, string? waId) =>
        userId is not null ? new SendToUserId(userId)
        : waId is not null ? new SendToPhone(waId)
        : throw new InvalidOperationException("A conversation has a user id or a wa_id (CK_conversations_identity).");
}

public sealed record SendToUserId(string UserId) : SendTarget;

public sealed record SendToPhone(string WaId) : SendTarget;
```

`IWhatsAppCloudClient.cs:27`:

```csharp
    /// <summary>§8.3 y spec 2026-10-10 §6.1.4: <paramref name="contextWamid"/> = el <c>wamid</c> citado, o <c>null</c>.</summary>
    Task<SendTextResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken);
```

`WhatsAppCloudClient.cs:41-55` (el resto del método no cambia): el cuerpo se arma con `JsonObject` para que la clave ausente no viaje:

```csharp
    public async Task<SendTextResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(target);
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = body },
            ["biz_opaque_callback_data"] = callbackData,
        };
        switch (target)
        {
            case SendToUserId byUserId:
                payload["recipient"] = byUserId.UserId;
                break;
            case SendToPhone byPhone:
                payload["to"] = byPhone.WaId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown send target.");
        }

        if (contextWamid is not null)
        {
            payload["context"] = new JsonObject { ["message_id"] = contextWamid };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // … desde acá, igual que hoy (answer, 5xx, 4xx, ReadWamid).
```

(`using System.Text;` y `using System.Text.Json.Nodes;`.)

`SendMessage.cs:100`: `var result = await meta.SendTextAsync(sender, SendTarget.For(conversation.UserId, conversation.WaId), text, CallbackPrefix + claim.MessageId.ToString("D"), null, CancellationToken.None);` (la cita llega en la T14).

Dobles: `MessagingTestBed.cs:161-179` — `internal sealed record SentText(MessagingSender Sender, SendTarget Target, string Body, string CallbackData, string? ContextWamid);` y `FakeWhatsAppClient.SendTextAsync(MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken)` que anota `new SentText(sender, target, body, callbackData, contextWamid)`. En `SendMessageHandlerTests.AHappySendInsertsCallsMetaAndCommitsSentWithTheWamid`, la tupla compara `send.Target` con `new SendToPhone(conversation.WaId!)` en vez de `send.To` (el `!`: la fila del banco de pruebas siempre tiene teléfono).

- [ ] **Step 3: Persistencia y migración**

`MessageRecord.cs`, después de `SentByMemberId`:

```csharp
    /// <summary>Spec 2026-10-10 §7.1: el mensaje citado, si QEP lo tiene. Sin FK (§7.1).</summary>
    public Guid? ReplyToMessageId { get; set; }

    /// <summary>El <c>wamid</c> citado tal como llegó (o el que se citó al enviar), aunque no se haya resuelto.</summary>
    public string? ReplyToWamid { get; set; }
```

`MessagingDbContext.cs` — constantes (`:19-21`):

```csharp
    public const string DirectionCheck = "direction IN (1, 2, 3)";
    public const string KindCheck = "kind BETWEEN 1 AND 13";
    public const string StatusCheck = "status BETWEEN 1 AND 4";

    /// <summary>Spec 2026-10-10 §7.1: un evento es System y Event a la vez, nunca uno sin el otro.</summary>
    public const string SystemIsEventCheck = "(direction = 3) = (kind = 13)";

    /// <summary>§7.1: un evento no fue a WhatsApp (sin wamid), no tiene clientId, está Delivered (D-A5) y lleva su detalle.</summary>
    public const string EventShapeCheck = "direction <> 3 OR (wamid IS NULL AND client_id IS NULL AND status = 2 AND details IS NOT NULL)";

    public const string IdentityCheck = "user_id IS NOT NULL OR wa_id IS NOT NULL";
    public const string AssignmentCheck = "(assigned_member_id IS NULL) = (assigned_at IS NULL)";
```

`ConfigureConversation` (`:45-91`):
- `ToTable`: suma `table.HasCheckConstraint("CK_conversations_identity", IdentityCheck);` y `table.HasCheckConstraint("CK_conversations_assignment", AssignmentCheck);`.
- Propiedades nuevas:

```csharp
        conversation.Property(value => value.UserId).HasColumnName("user_id").HasMaxLength(Conversation.UserIdMaxLength);
        conversation.Property(value => value.Username).HasColumnName("username").HasMaxLength(Conversation.UsernameMaxLength);
        conversation.Property(value => value.ParentUserId).HasColumnName("parent_user_id").HasMaxLength(Conversation.UserIdMaxLength);
        conversation.Property(value => value.CustomerId).HasColumnName("customer_id");
        conversation.Property(value => value.AssignedMemberId).HasColumnName("assigned_member_id");
        conversation.Property(value => value.AssignedAt).HasColumnName("assigned_at");
```

- Índices: **todos con el overload con nombre**. `IX_conversations_tenant_status_activity` y el nuevo `IX_conversations_tenant_unassigned_status_activity` tienen **las mismas columnas**: sin nombre en `HasIndex`, EF los funde en uno y el segundo pisa el filtro del primero. Reemplaza `:78-81` y agrega:

```csharp
        // Spec 2026-10-10 §7.1: la clave nueva (blanco del ON CONFLICT de la ingesta) y la vieja, sólo entre filas sin BSUID.
        conversation.HasIndex(value => new { value.ConnectionId, value.UserId }, "IX_conversations_connection_user")
            .IsUnique().HasFilter("user_id IS NOT NULL");
        conversation.HasIndex(value => new { value.ConnectionId, value.WaId }, "IX_conversations_connection_wa_legacy")
            .IsUnique().HasFilter("user_id IS NULL");
        conversation.HasIndex(value => new { value.TenantId, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_status_activity")
            .IsDescending(false, false, true, true);
        // Pestaña «Mías» y counts.mine; pestaña «Sin asignar» y counts.unassigned.
        conversation.HasIndex(value => new { value.TenantId, value.AssignedMemberId, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_assignee_status_activity")
            .IsDescending(false, false, false, true, true).HasFilter("assigned_member_id IS NOT NULL");
        conversation.HasIndex(value => new { value.TenantId, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_unassigned_status_activity")
            .IsDescending(false, false, true, true).HasFilter("assigned_member_id IS NULL");
        // §8.2: la herencia del asignado — la conversación más reciente del cliente.
        conversation.HasIndex(value => new { value.TenantId, value.CustomerId, value.LastActivityAt }, "IX_conversations_tenant_customer_activity")
            .IsDescending(false, false, true).HasFilter("customer_id IS NOT NULL");
        conversation.HasIndex(value => value.Username, "IX_conversations_username_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
```

  `IX_conversations_profile_name_trgm` y `IX_conversations_wa_id_trgm` (`:87-90`) pasan también al overload con nombre (`HasIndex(value => value.ProfileName, "IX_conversations_profile_name_trgm")…`), sin cambio físico.

`ConfigureMessage` (`:93-139`): `ToTable` suma `table.HasCheckConstraint("CK_messages_system_is_event", SystemIsEventCheck);` y `table.HasCheckConstraint("CK_messages_event_shape", EventShapeCheck);`; propiedades `message.Property(value => value.ReplyToMessageId).HasColumnName("reply_to_message_id");` y `message.Property(value => value.ReplyToWamid).HasColumnName("reply_to_wamid");`.

Generar:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet ef migrations add AddBsuidAssignmentAndEvents --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
```

Revisar el `Up` generado contra el SQL del spec §7.1: `AlterColumn` de `wa_id` a anulable; seis columnas en `conversations`, dos en `messages`; `DropIndex IX_conversations_connection_wa`; los seis índices nuevos; `DropCheckConstraint`/`AddCheckConstraint` de `CK_messages_direction` y `CK_messages_kind`; los cuatro `CHECK` nuevos. Si aparece un `DropIndex`/`CreateIndex` de `IX_conversations_tenant_status_activity`, `IX_conversations_profile_name_trgm` o `IX_conversations_wa_id_trgm`, algún nombre quedó mal.

En el `Down`, **antes** de lo generado (lo que restaura el `Down` rechazaría estas filas):

```csharp
        // Spec 2026-10-10 §7.1: lo que el esquema viejo no admite. Primero las filas, después las restricciones.
        migrationBuilder.Sql("DELETE FROM messaging.messages WHERE direction = 3;");            // CK_messages_direction / kind
        migrationBuilder.Sql("DELETE FROM messaging.conversations WHERE wa_id IS NULL;");        // wa_id NOT NULL (cascada a messages)
        migrationBuilder.Sql("""
            DELETE FROM messaging.conversations c
            WHERE EXISTS (SELECT 1 FROM messaging.conversations o
                          WHERE o.connection_id = c.connection_id AND o.wa_id = c.wa_id AND o.id < c.id);
            """);                                                                                  // IX_conversations_connection_wa único
```

- [ ] **Step 4: Puentes de lectura, ingesta y harness**

`IConversationQueries.cs:6-22`: `ConversationRow` con la forma de **Interfaces** (orden exacto). `ConversationQueries.cs:12-15`, la proyección en el mismo orden; `:27-30`, la búsqueda:

```csharp
            query = query.Where(conversation =>
                (conversation.ProfileName != null && EF.Functions.ILike(conversation.ProfileName, pattern, "\\"))
                || (digitsPattern != null && conversation.WaId != null && EF.Functions.Like(conversation.WaId, digitsPattern))
                || (conversation.WaId != null && phones.Contains(conversation.WaId)));
```

`MessagingDtos.cs:8`:

```csharp
/// <summary>Spec 2026-10-10 §5.1: <c>userId</c> es <c>null</c> sólo en una conversación vieja sin entrante con BSUID;
/// <c>waId</c> es <c>null</c> cuando Meta no mandó el teléfono. Al menos uno viene (CK_conversations_identity).</summary>
public sealed record ContactDto(string? UserId, string? WaId, string? Username, string? ProfileName);
```

`MessagingSupport.cs:49` y `:63-64` (el `customer` por `customer_id` llega en la T11; acá sigue el emparejamiento por teléfono, sólo para filas con teléfono):

```csharp
        var matches = await customers.MatchAsync(
            tenantId, rows.Where(row => row.WaId is not null).Select(row => row.WaId!).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
```

```csharp
            new ContactDto(row.UserId, row.WaId, row.Username, row.ProfileName),
            row.WaId is { } waId ? matches.GetValueOrDefault(waId) : null,
```

`ConversationReadsTests.cs:27-30` y `MessagingTestBed.cs:38-42`: el `new ConversationRow(...)` con la forma nueva (`UserId: null`, `WaId: "573001234567"`, `Username: null`, `CustomerId: null`, `AssignedMemberId: null` en sus posiciones).

`InboundIngestion.cs` — puente hasta la T7 (el parser todavía exige teléfono, así que `message.WaId` no es nulo acá): el `ON CONFLICT` apunta al índice viejo, ahora parcial, y el `SELECT` se restringe a filas sin BSUID:

```csharp
            ON CONFLICT (connection_id, wa_id) WHERE user_id IS NULL DO NOTHING
```

```csharp
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND wa_id = {message.WaId} AND user_id IS NULL""").SingleAsync(cancellationToken);
```

`MessagingApiHarness.cs`, después de `SeedConversationAsync`:

```csharp
    /// <summary>Spec 2026-10-10: una conversación con BSUID (y teléfono si se pasa), con la ventana abierta si se pasa
    /// <paramref name="lastInboundAt"/>, como la dejaría la ingesta.</summary>
    public static async Task<Guid> SeedBsuidConversationAsync(
        WebApplicationFactory<Program> host, string connectionString, Guid tenantId, Guid connectionId, string userId, string? waId, DateTimeOffset? lastInboundAt = null)
    {
        Guid id;
        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var conversation = Conversation.StartWithUserId(Guid.CreateVersion7(), tenantId, connectionId, userId, waId, "Laura", DateTimeOffset.UtcNow);
            dbContext.Conversations.Add(conversation);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = conversation.Id;
        }

        if (lastInboundAt is { } at)
        {
            await ExecuteAsync(connectionString,
                "UPDATE messaging.conversations SET last_inbound_at = @at, last_inbound_wamid = 'wamid.seed' WHERE id = @id",
                ("at", at.ToUniversalTime()), ("id", id));
        }

        return id;
    }
```

- [ ] **Step 5: Las de integración (RED → GREEN)**

`MessagingPersistenceTests.cs`:
- En `TheSchemaHasTheIndexesExtensionsAndConfigurationOfTheSpec`, la lista esperada cambia `IX_conversations_connection_wa` por `IX_conversations_connection_user`, `IX_conversations_connection_wa_legacy`, `IX_conversations_tenant_assignee_status_activity`, `IX_conversations_tenant_unassigned_status_activity`, `IX_conversations_tenant_customer_activity`, `IX_conversations_username_trgm`, y agrega `Assert.DoesNotContain("IX_conversations_connection_wa,", indexes + ",", StringComparison.Ordinal);`.
- En `TheIndexesHaveTheMethodsOrderIncludeAndPredicatesOfTheSpec`, la fila de `IX_conversations_connection_wa` se reemplaza por:

```csharp
            ("IX_conversations_connection_user", "CREATE UNIQUE INDEX \"IX_conversations_connection_user\" ON messaging.conversations USING btree (connection_id, user_id) WHERE (user_id IS NOT NULL)"),
            ("IX_conversations_connection_wa_legacy", "CREATE UNIQUE INDEX \"IX_conversations_connection_wa_legacy\" ON messaging.conversations USING btree (connection_id, wa_id) WHERE (user_id IS NULL)"),
            ("IX_conversations_tenant_assignee_status_activity", "USING btree (tenant_id, assigned_member_id, status, last_activity_at DESC, id DESC) WHERE (assigned_member_id IS NOT NULL)"),
            ("IX_conversations_tenant_unassigned_status_activity", "USING btree (tenant_id, status, last_activity_at DESC, id DESC) WHERE (assigned_member_id IS NULL)"),
            ("IX_conversations_tenant_customer_activity", "USING btree (tenant_id, customer_id, last_activity_at DESC) WHERE (customer_id IS NOT NULL)"),
            ("IX_conversations_username_trgm", "USING gin (username gin_trgm_ops)"),
```

- En `TheChecksRejectAnInboundFailedAndAnUnknownKind`, el `kind: 13` pasa a `kind: 14` (13 ahora es `Event`).
- Prueba nueva:

```csharp
    // Spec 2026-10-10 §7.1: lo que sólo la base hace cumplir de la identidad, la asignación y los eventos.
    [Fact]
    public async Task TheNewChecksHoldIdentityAssignmentAndEventShape()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var conversationId = await SeedBsuidConversationAsync(factory, connectionString, tenantId, connectionId, "CO.1", null);

        var noIdentity = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET user_id = NULL WHERE id = @id", ("id", conversationId)));
        Assert.Equal("CK_conversations_identity", noIdentity.ConstraintName);
        var halfAssigned = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET assigned_member_id = gen_random_uuid() WHERE id = @id", ("id", conversationId)));
        Assert.Equal("CK_conversations_assignment", halfAssigned.ConstraintName);
        var systemText = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, created_at) VALUES (gen_random_uuid(), @c, @t, @n, now(), 3, 1, 2, now())",
            ("c", conversationId), ("t", tenantId), ("n", connectionId)));
        Assert.Equal("CK_messages_system_is_event", systemText.ConstraintName);
        var eventWithoutDetails = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, created_at) VALUES (gen_random_uuid(), @c, @t, @n, now(), 3, 13, 2, now())",
            ("c", conversationId), ("t", tenantId), ("n", connectionId)));
        Assert.Equal("CK_messages_event_shape", eventWithoutDetails.ConstraintName);
        Assert.Equal(1, await ExecuteAsync(connectionString,
            """INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, details, created_at) VALUES (gen_random_uuid(), @c, @t, @n, now(), 3, 13, 2, '{"type":"Taken"}', now())""",
            ("c", conversationId), ("t", tenantId), ("n", connectionId)));
        // Dos conversaciones con BSUID pueden compartir teléfono (número reciclado); dos viejas no.
        await SeedBsuidConversationAsync(factory, connectionString, tenantId, connectionId, "CO.2", "573001234567");
        await SeedBsuidConversationAsync(factory, connectionString, tenantId, connectionId, "CO.3", "573001234567");
        await SeedConversationAsync(factory, tenantId, connectionId, "573001234567");
        await Assert.ThrowsAnyAsync<Exception>(() => SeedConversationAsync(factory, tenantId, connectionId, "573001234567"));
    }
```

`SendMessageApiTests.cs`, prueba nueva (RF1, envío):

```csharp
    // Spec 2026-10-10 §6.1.4 (RF1): una conversación con BSUID y sin teléfono se responde por recipient.
    [Fact]
    public async Task ABsuidConversationIsAnsweredByRecipientWithoutTo()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedBsuidConversationAsync(factory, connectionString, tenant.TenantId, connectionId, "CO.1349120865530274", null, DateTimeOffset.UtcNow.AddMinutes(-5));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        ScriptSendOk(factory.MetaHandler);

        var response = await SendAsync(client, HttpMethod.Post, MessagesUrl(tenant.TenantId, conversationId), new { clientId = Guid.CreateVersion7(), text = "hola" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var send = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Contains("\"recipient\":\"CO.1349120865530274\"", send.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", send.Body, StringComparison.Ordinal);
    }
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~WhatsAppCloudClientTests|FullyQualifiedName~SendMessageHandlerTests|FullyQualifiedName~ConversationReadsTests|FullyQualifiedName~MessageColumnCodesTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~MessagingPersistenceTests|FullyQualifiedName~SendMessageApiTests|FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~ConversationsApiTests"
```

Esperado: verde. (`MessageColumnCodesTests` ahora también puede aseverar `"direction IN (1, 2, 3)"` y `"kind BETWEEN 1 AND 13"`: actualiza esas dos aserciones.) `InboundIngestionTests` y `ConversationsApiTests` prueban que el puente no rompió nada.

- [ ] **Step 6: Formato y commit**

```
feat(messaging): esquema de BSUID, asignación, eventos y citas; envío por recipient

Migración AddBsuidAssignmentAndEvents (spec 2026-10-10 §7.1) con Down que limpia lo que el esquema
viejo rechaza. ContactDto con userId/username y waId opcional; el envío usa recipient con BSUID y
to sólo en filas viejas (§6.1.4). La ingesta sigue por teléfono contra el índice viejo parcial.
```

---

### Task 7: Parser con BSUID y la ingesta por la clave nueva (adopción de las filas viejas)

Spec §8.1 (parser y sentencia 1), §9.5, P1, P5. Review Focus RF1 (entrante sólo con BSUID) y RF2 (adopción). El parser lee BSUID, teléfono opcional, `username`, `parent_user_id`, la cita (`context.id`) y las dos señales de cambio de número; la ingesta busca por `(connection_id, user_id)`, adopta la fila vieja por teléfono o crea. El procesamiento de las señales de cambio de número va en la T10; acá sólo se parsean. Los fixtures de las pruebas existentes ganan `from_user_id` en un solo lugar (sin él, la regla nueva los saltaría).

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayload.cs:8-26`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayloadParser.cs:20-188` (+ `ParseUserIdUpdate`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs:14-89`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:52-58` (`UserIdUpdateChange` cae en el log de ignorado hasta la T10)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MetaPayloads.cs` (BSUID por defecto, builder general)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserTests.cs:17-23` (helper), `:62-76` (prueba del `from` largo), `:232-262` (dos cuerpos crudos)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendMessageApiTests.cs` (`AHappySend…` ahora ve `recipient`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundBsuidTests.cs` (nuevo), `WebhookPayloadParserTests.cs` (pruebas nuevas)

**Interfaces:**
- Consumes (T5, T6): `Conversation.IsValidUserId`, `UserIdMaxLength`, `UsernameMaxLength`, `WaIdMaxLength`; columnas `user_id`, `username`, `parent_user_id`, `reply_to_wamid`; `IX_conversations_connection_user`, `IX_conversations_connection_wa_legacy`.
- Produces (T9, T10, T12):

```csharp
internal sealed record InboundMessage(
    string Wamid, string UserId, string? WaId, string? ProfileName, string? Username, string? ParentUserId,
    DateTimeOffset OccurredAt, MessageKind Kind, string? Text, string? Caption, string? DetailsJson, InboundMedia? Media, string? QuotedWamid);
internal sealed record UserIdChange(string Previous, string Current, string? WaId);
internal sealed record MessagesChange(string PhoneNumberId, IReadOnlyList<InboundMessage> Messages, IReadOnlyList<StatusUpdate> Statuses, IReadOnlyList<UserIdChange> NumberChanges) : WebhookChange;
internal sealed record UserIdUpdateChange(string WabaId, string? PhoneNumberId, UserIdChange Change) : WebhookChange;
// InboundIngestion (sin cambio de firma en esta tarea)
public static Task<Guid?> IngestAsync(MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken);
// MetaPayloads (pruebas)
public static string UserIdFor(string waId);   // "CO." + waId
public static string Inbound(string phoneNumberId, string userId, string? waId, string wamid, long timestamp, string text, string? profileName = "Laura", string? username = null, string? quotedWamid = null);
```

- [ ] **Step 1: Fixtures con BSUID por defecto**

`MetaPayloads.cs`:
- `public const string DefaultUserIdPrefix = "CO.";` y `public static string UserIdFor(string waId) => DefaultUserIdPrefix + waId;` (un BSUID válido y estable por teléfono: las pruebas viejas siguen teniendo «una conversación por número»).
- El helper privado `Messages(...)` (`:170-180`) recibe además `string userId` y `string? waId`, y antes de serializar agrega al mensaje `from_user_id = userId` (y quita `from` si `waId` es `null`) y arma el contacto `new { profile = new { name = profileName }, user_id = userId, wa_id = waId }` omitiendo `wa_id` si es `null` (con `JsonObject`, no con anónimos, para poder omitir). `InboundText` e `InboundMedia` pasan `UserIdFor(waId)` y `waId`. `LocationValue` agrega `["from_user_id"] = UserIdFor(waId)` al mensaje y `user_id` al contacto.
- Builder general nuevo (lo usan T7–T14):

```csharp
    /// <summary>Spec 2026-10-10 §3: un texto entrante con BSUID (siempre), teléfono si <paramref name="waId"/> no es
    /// <c>null</c> (en <c>from</c> y en el contacto), usuario si lo hay y la cita si <paramref name="quotedWamid"/>.</summary>
    public static string Inbound(
        string phoneNumberId, string userId, string? waId, string wamid, long timestamp, string text,
        string? profileName = "Laura", string? username = null, string? quotedWamid = null)
    {
        var message = new JsonObject
        {
            ["from_user_id"] = userId,
            ["id"] = wamid,
            ["timestamp"] = Seconds(timestamp),
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = text },
        };
        if (waId is not null)
        {
            message["from"] = waId;
        }

        if (quotedWamid is not null)
        {
            message["context"] = new JsonObject { ["id"] = quotedWamid, ["from"] = "15550000000" };
        }

        var profile = new JsonObject();
        if (profileName is not null)
        {
            profile["name"] = profileName;
        }

        if (username is not null)
        {
            profile["username"] = username;
        }

        var contact = new JsonObject { ["profile"] = profile, ["user_id"] = userId };
        if (waId is not null)
        {
            contact["wa_id"] = waId;
        }

        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = Metadata(phoneNumberId, "15550000000"),
            ["contacts"] = new JsonArray(contact),
            ["messages"] = new JsonArray(message),
        };
        return Change("messages", value.ToJsonString());
    }
```

`WebhookPayloadParserTests.cs:17-23`: el helper `Messages(object message, object? contact = null)` convierte el mensaje con `JsonSerializer.SerializeToNode(message)!.AsObject()` y, si no trae `from_user_id`, le agrega `"CO." + from` (o `"CO.1"` si no hay `from`); el contacto por defecto gana `user_id = "CO.573001234567"`. Los dos cuerpos crudos de `:232-262` agregan `from_user_id` a mano. Así las pruebas viejas no cambian de intención.

- [ ] **Step 2: Pruebas del parser (RED)**

Agregar a `WebhookPayloadParserTests.cs`:

```csharp
    private static object BsuidOnly(string userId, object message) => new
    {
        messaging_product = "whatsapp",
        metadata = new { display_phone_number = "15550000000", phone_number_id = "111" },
        contacts = new[] { new { profile = new { name = "Laura", username = "laura.p" }, user_id = userId, parent_user_id = "CO.PARENT" } },
        messages = new[] { message },
    };

    // Spec 2026-10-10 §8.1 (RF1): sin from ni wa_id, el mensaje entra con BSUID y sin teléfono.
    [Fact]
    public void AMessageWithOnlyABsuidEntersWithoutPhone()
    {
        var json = Envelope("messages", BsuidOnly("CO.1349120865530274", new { from_user_id = "CO.1349120865530274", id = "wamid.b", timestamp = "1760000000", type = "text", text = new { body = "hola" } }));

        var message = Assert.Single(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Messages);

        Assert.Equal(("CO.1349120865530274", (string?)null, "Laura", "laura.p", "CO.PARENT"), (message.UserId, message.WaId, message.ProfileName, message.Username, message.ParentUserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("573001234567")]
    [InlineData("co.123")]
    public void AMessageWithoutAValidBsuidIsSkipped(string? fromUserId)
    {
        var json = Envelope("messages", BsuidOnly("CO.1", new { from_user_id = fromUserId, from = "573001234567", id = "wamid.x", timestamp = "1760000000", type = "text", text = new { body = "hola" } }));

        Assert.Empty(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Messages);
    }

    // §8.1: un from inválido se descarta como dato; el mensaje entra igual.
    [Fact]
    public void AnInvalidFromIsDroppedButTheMessageEnters()
    {
        var json = Envelope("messages", BsuidOnly("CO.1", new { from_user_id = "CO.1", from = "57-300", id = "wamid.f", timestamp = "1760000000", type = "text", text = new { body = "hola" } }));

        var message = Assert.Single(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Messages);
        Assert.Null(message.WaId);
    }

    [Fact]
    public void TheQuotedWamidComesFromTheContext()
    {
        var json = Envelope("messages", BsuidOnly("CO.1", new { from_user_id = "CO.1", id = "wamid.r", timestamp = "1760000000", type = "text", text = new { body = "sí" }, context = new { from = "15550000000", id = "wamid.quoted" } }));

        Assert.Equal("wamid.quoted", Assert.Single(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Messages).QuotedWamid);
    }

    // §8.1 y §8.3: user_changed_user_id no es un mensaje Unsupported; el anterior sale de from_user_id o del cuerpo.
    [Theory]
    [InlineData("CO.OLD", "User Laura changed from CO.IGNORED to CO.NEW", "CO.OLD")]
    [InlineData("CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "CO.OLD")]
    public void AUserChangedUserIdSystemMessageIsANumberChange(string fromUserId, string body, string expectedPrevious)
    {
        var json = Envelope("messages", BsuidOnly(fromUserId, new
        {
            from_user_id = fromUserId,
            id = "wamid.sys",
            timestamp = "1760000000",
            type = "system",
            system = new { body, type = "user_changed_user_id", user_id = "CO.NEW", wa_id = "573009999999" },
        }));

        var change = Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json)));

        Assert.Empty(change.Messages);
        Assert.Equal(new UserIdChange(expectedPrevious, "CO.NEW", "573009999999"), Assert.Single(change.NumberChanges));
    }

    [Fact]
    public void AnotherSystemMessageIsStillUnsupported()
    {
        var json = Envelope("messages", BsuidOnly("CO.1", new { from_user_id = "CO.1", id = "wamid.s", timestamp = "1", type = "system", system = new { type = "customer_identity_changed", body = "x" } }));

        Assert.Equal(MessageKind.Unsupported, Assert.Single(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Messages).Kind);
    }

    [Fact]
    public void AUserIdUpdateFieldIsANumberChangeRoutedByPhoneNumberIdOrWaba()
    {
        var withMetadata = Envelope("user_id_update", new { metadata = new { phone_number_id = "111" }, user_id = new { previous = "CO.OLD", current = "CO.NEW" } });
        var withoutMetadata = Envelope("user_id_update", new { user_id = new { previous = "CO.OLD", current = "CO.NEW" } });
        var broken = Envelope("user_id_update", new { user_id = new { previous = "CO.OLD" } });

        Assert.Equal(new UserIdUpdateChange("222", "111", new UserIdChange("CO.OLD", "CO.NEW", null)), Assert.Single(WebhookPayloadParser.Parse(withMetadata)));
        Assert.Equal(new UserIdUpdateChange("222", null, new UserIdChange("CO.OLD", "CO.NEW", null)), Assert.Single(WebhookPayloadParser.Parse(withoutMetadata)));
        Assert.IsType<UnknownChange>(Assert.Single(WebhookPayloadParser.Parse(broken)));
    }

    // P1: un envío por BSUID sin teléfono trae el status sin recipient_id; se correlaciona igual por wamid.
    [Fact]
    public void AStatusWithoutRecipientIdStillParses()
    {
        var json = Envelope("messages", new { metadata = new { phone_number_id = "111" }, statuses = new[] { new { id = "wamid.out", status = "delivered", timestamp = "1760000000", recipient_user_id = "CO.1" } } });

        Assert.Equal("wamid.out", Assert.Single(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Statuses).Wamid);
    }
```

La prueba vieja `AFromLongerThanTheWaIdColumnIsSkipped` (`:62-76`) pasa a `AFromLongerThanTheWaIdColumnIsDroppedButTheMessageEnters`: con `tooLong` el mensaje **entra** con `WaId == null`; con `longest`, `WaId == longest`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WebhookPayloadParserTests"
```

Esperado: `error CS1061: 'InboundMessage' does not contain a definition for 'UserId'` (y `UserIdChange`, `UserIdUpdateChange`).

- [ ] **Step 3: El parser**

`WebhookPayload.cs`: los tipos de **Interfaces**. `MessagesChange` gana `NumberChanges`.

`WebhookPayloadParser.cs`:
- `Parse` (`:52-57`): `"user_id_update" => ParseUserIdUpdate(wabaId ?? string.Empty, value),`.
- `ParseMessages` (`:65-107`): el índice de contactos pasa a `Dictionary<string, ContactProfile>` por `user_id` (sólo contactos con `user_id` válido); `private sealed record ContactProfile(string? Name, string? Username, string? ParentUserId, string? WaId);` con `Truncate(…, Conversation.ProfileNameMaxLength)`, `Truncate(username, Conversation.UsernameMaxLength)`, `Truncate(parent_user_id, Conversation.UserIdMaxLength)` y `wa_id` sólo si son 1 a 20 dígitos. Cada mensaje con `type == "system"` y `system.type == "user_changed_user_id"` va a `numberChanges` por `ParseNumberChange`; el resto, a `ParseMessage`.
- `ParseMessage` (`:109-121`): reemplaza la guarda:

```csharp
        var wamid = ReadString(item, "id");
        var userId = ReadString(item, "from_user_id");
        // Spec 2026-10-10 §8.1: el BSUID es la clave y Meta lo manda siempre. Sin él, el mensaje se salta (tolerancia
        // de base: nunca lanza). El teléfono es un dato opcional: si from no tiene la forma, se descarta y el
        // mensaje entra igual; si falta, se toma el wa_id del contacto.
        if (wamid is null || !Conversation.IsValidUserId(userId) || ReadTimestamp(item) is not { } occurredAt)
        {
            return null;
        }

        contacts.TryGetValue(userId!, out var contact);
        var waId = ValidWaId(ReadString(item, "from")) ?? contact?.WaId;
        var quotedWamid = TryObject(item, "context", out var context) ? ReadString(context, "id") : null;
```

  y el `return` (`:187`): `new InboundMessage(wamid, userId!, waId, contact?.Name, contact?.Username, contact?.ParentUserId, occurredAt, kind, text, caption, details, media, quotedWamid)` (el `!` va detrás de `IsValidUserId`, que descarta el `null`).

- Helpers nuevos:

```csharp
    private static string? ValidWaId(string? value) =>
        value is { Length: > 0 and <= Conversation.WaIdMaxLength } && value.All(char.IsAsciiDigit) ? value : null;

    [GeneratedRegex("changed from (\\S+) to (\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChangedFrom();

    /// <summary>Spec 2026-10-10 §8.3: el nuevo es <c>system.user_id</c>; el anterior, <c>from_user_id</c> si es distinto,
    /// y si no, el del cuerpo («changed from &lt;OLD&gt; to &lt;NEW&gt;»).</summary>
    private static UserIdChange? ParseNumberChange(JsonElement item)
    {
        if (!TryObject(item, "system", out var system) || ReadString(system, "user_id") is not { } current || !Conversation.IsValidUserId(current))
        {
            return null;
        }

        var previous = ReadString(item, "from_user_id");
        if (previous is null || previous == current)
        {
            var match = ChangedFrom().Match(ReadString(system, "body") ?? string.Empty);
            previous = match.Success ? match.Groups[1].Value : null;
        }

        return Conversation.IsValidUserId(previous) && previous != current
            ? new UserIdChange(previous!, current, ValidWaId(ReadString(system, "wa_id")))
            : null;
    }

    /// <summary>§8.3: la forma no está documentada con un ejemplo (riesgo de §13); se lee <c>user_id.{previous,current}</c>
    /// y, si viene, <c>metadata.phone_number_id</c>. Lo que no tenga esa forma cae como campo ignorado, en el log.</summary>
    private static WebhookChange ParseUserIdUpdate(string wabaId, JsonElement value)
    {
        if (!TryObject(value, "user_id", out var ids)
            || ReadString(ids, "previous") is not { } previous || ReadString(ids, "current") is not { } current
            || !Conversation.IsValidUserId(previous) || !Conversation.IsValidUserId(current) || previous == current)
        {
            return new UnknownChange("user_id_update");
        }

        var phoneNumberId = TryObject(value, "metadata", out var metadata) ? ReadString(metadata, "phone_number_id") : null;
        return new UserIdUpdateChange(wabaId, phoneNumberId, new UserIdChange(previous, current, ValidWaId(ReadString(value, "wa_id"))));
    }
```

  La clase pasa a `internal static partial class WebhookPayloadParser` (`using System.Text.RegularExpressions;`).

`WebhookDeliveryProcessor.cs:52-58`: `case UserIdUpdateChange:` → `LogIgnoredField(logger, deliveryId, "user_id_update");` (la T10 lo reemplaza). `MessagesChange.NumberChanges` se ignora hasta la T10.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~WebhookPayloadParserTests"
```

Esperado: el build falla en `InboundIngestion.cs` (`message.WaId` ahora es `string?`): es el insumo del Step 5. Las del parser se corren después del Step 5.

- [ ] **Step 4: Las de integración de la ingesta (RED)**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundBsuidTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.1: la clave es el BSUID (RF1), una fila vieja se adopta por teléfono (RF2), un número
/// reciclado con otro BSUID es otra conversación, y el teléfono o el usuario que llegan después se completan.</summary>
public sealed class InboundBsuidTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, factory.CreateClient());
    }

    private static async Task IngestAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Anonymous, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    [Fact]
    public async Task ABsuidOnlyInboundCreatesTheConversationWithoutPhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", null, "wamid.1", 1760000000, "hola", username: "laura.p"));

        Assert.Equal("CO.1349120865530274||laura.p|Laura|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT user_id || '|' || coalesce(wa_id, '') || '|' || username || '|' || profile_name || '|' || unread_count FROM messaging.conversations"));
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var contact = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), TestContext.Current.CancellationToken))
            .GetProperty("items")[0].GetProperty("contact");
        Assert.Equal("CO.1349120865530274", contact.GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, contact.GetProperty("waId").ValueKind);
        Assert.Equal("laura.p", contact.GetProperty("username").GetString());
    }

    [Fact]
    public async Task TheFirstBsuidInboundAdoptsTheLegacyPhoneConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var legacy = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", "573001234567", "wamid.1", 1760000000, "hola"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", null, "wamid.2", 1760000100, "otra"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal($"{legacy}|CO.1349120865530274|573001234567|2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT id::text || '|' || user_id || '|' || wa_id || '|' || unread_count FROM messaging.conversations"));
    }

    // D-A7 y §7.1: una conversación con BSUID puede repetir teléfono (número reciclado por otra persona).
    [Fact]
    public async Task ARecycledNumberWithAnotherBsuidIsAnotherConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.AAA", "573001234567", "wamid.1", 1760000000, "soy A"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.BBB", "573001234567", "wamid.2", 1760000100, "soy B"));

        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE wa_id = '573001234567'"));
    }

    [Fact]
    public async Task ThePhoneAndUsernameThatArriveLaterAreFilledWithoutLosingThem()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "a", username: "laura.p"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.2", 1760000100, "b", username: null));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", 1760000200, "c", username: null));

        Assert.Equal("573001234567|laura.p|3", await ScalarAsync<string>(f.ConnectionString,
            "SELECT wa_id || '|' || username || '|' || unread_count FROM messaging.conversations"));
    }

    [Fact]
    public async Task AnInboundWithoutBsuidIsSkippedAndTheDeliveryIsDone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var withoutBsuid = MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.1", 1760000000, "hola").Replace("\"from_user_id\":\"CO.1\",", string.Empty, StringComparison.Ordinal);

        await IngestAsync(f, withoutBsuid);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL AND last_error IS NULL"));
    }
}
```

(Si el serializador escribe `from_user_id` en otra posición y el `Replace` no lo encuentra, la prueba lo delata: asserta primero `Assert.Contains("\"from_user_id\":\"CO.1\",", json)`.)

- [ ] **Step 5: La ingesta por BSUID**

`InboundIngestion.cs` — la sentencia 1 se reemplaza, y todo el cuerpo queda dentro de un reintento único (P5):

```csharp
    /// <summary>§9.5 (P5): la adopción de una fila vieja choca con una conversación que otro pod creó con el mismo BSUID
    /// entre el SELECT y el UPDATE. Se reintenta una vez: la segunda vuelta encuentra la nueva por BSUID.</summary>
    private const string ConnectionUserIndex = "IX_conversations_connection_user";

    public static async Task<Guid?> IngestAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await IngestOnceAsync(dbContext, tenantId, connectionId, message, now, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ConnectionUserIndex)
        {
            return await IngestOnceAsync(dbContext, tenantId, connectionId, message, now, cancellationToken);
        }
    }

    private static async Task<Guid?> IngestOnceAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var conversationId = await ResolveConversationAsync(dbContext, tenantId, connectionId, message, now, cancellationToken);
        // Desde acá, el cuerpo actual de IngestAsync (InboundIngestion.cs:34-88: sentencia 2, sentencia 3, medio y commit) se
        // mueve tal cual, usando este conversationId; los cambios de las sentencias 2 y 3 se describen abajo.
    }

    /// <summary>Spec 2026-10-10 §8.1, sentencia 1: por BSUID; si no, adopción de la fila vieja por teléfono; si no, crear.</summary>
    private static async Task<Guid> ResolveConversationAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var known = await dbContext.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
            .ToListAsync(cancellationToken);
        if (known.Count == 1)
        {
            return known[0];
        }

        if (message.WaId is { } waId)
        {
            // 1a. Una conversación vieja (sin BSUID) con ese teléfono pasa a ser la de este BSUID (IX_conversations_connection_wa_legacy).
            var adopted = await dbContext.Database.SqlQuery<Guid>(
                $"""
                UPDATE messaging.conversations SET user_id = {message.UserId}
                WHERE connection_id = {connectionId} AND user_id IS NULL AND wa_id = {waId}
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (adopted.Count == 1)
            {
                return adopted[0];
            }
        }

        // 1b. Crear por la clave nueva. DO NOTHING + SELECT aparte, como antes: un DO UPDATE reabriría una resuelta.
        var newConversationId = Guid.CreateVersion7();
        var inserted = await dbContext.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, username, parent_user_id, profile_name,
                                                 status, unread_count, last_activity_at, created_at, updated_at, version)
            VALUES ({newConversationId}, {tenantId}, {connectionId}, {message.UserId}, {message.WaId}, {message.Username}, {message.ParentUserId},
                    {message.ProfileName}, 'Open', 0, {message.OccurredAt}, {now}, {now}, 1)
            ON CONFLICT (connection_id, user_id) WHERE user_id IS NOT NULL DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        return inserted.Count == 1
            ? inserted[0]
            : await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
                .SingleAsync(cancellationToken);
    }
```

En la sentencia 2 (el `INSERT` del mensaje) se agrega la columna `reply_to_wamid` con `{message.QuotedWamid}` (el `reply_to_message_id` resuelto llega en la T9). En la sentencia 3 (`UPDATE … SET`) se suman, antes de `updated_at`:

```sql
                wa_id              = COALESCE({message.WaId}, wa_id),
                username           = COALESCE({message.Username}, username),
                parent_user_id     = COALESCE({message.ParentUserId}, parent_user_id),
```

(`using Npgsql;`.) El `wa_id` nuevo sobre una fila con BSUID no choca con nada: el único por teléfono es el parcial de las filas viejas.

`SendMessageApiTests.AHappySendAnswers201SentWithTheClientIdAndUpdatesTheSnapshot`: la conversación ahora nace con BSUID (`MetaPayloads.UserIdFor`), así que la aserción `"to":"573001234567"` pasa a `Assert.Contains("\"recipient\":\"CO.573001234567\"", send.Body, StringComparison.Ordinal);` y `Assert.DoesNotContain("\"to\"", …)`.

- [ ] **Step 6: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~WebhookPayloadParserTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~InboundBsuidTests|FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~SendMessageApiTests|FullyQualifiedName~ConversationsApiTests|FullyQualifiedName~ThreadApiTests|FullyQualifiedName~StatusIngestionTests"
```

Esperado: verde. Las clases viejas confirman que el fixture con BSUID por defecto no les cambió la intención.

- [ ] **Step 7: Formato y commit**

```
feat(messaging): la conversación se identifica por BSUID y adopta las filas viejas por teléfono

El parser exige from_user_id, deja el teléfono como dato opcional y lee username, parent_user_id,
context.id y las dos señales de cambio de número (spec 2026-10-10 §8.1). La ingesta busca por
(connection_id, user_id), adopta la conversación vieja por teléfono o crea, con un reintento ante
el choque adopción/creación (§9.5).
```

---

### Task 8: `IMessagingAssignees` y `GET /messaging/assignees`

Spec §6.1.3 (puerto), §5.1 (`GET /messaging/assignees`, D-A1), §11 (aislamiento del `memberId`). Lo usan la herencia (T9), la transferencia (T13) y el selector del frontend.

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAssignees.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ListAssignees.cs`
- Create: `src/Bootstrapper/MessagingAssignees.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:74-88` (ruta nueva)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:540` (handler) y `:597-599` (adaptador)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs` (`AssigneesUrl`, `SeedMemberAsync`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AssigneesApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `IMembershipRepository.FindByIdAsync/ListByTenantAsync` (Tenancy.Application), `IUserDirectory.GetEmailAsync` (Identity.Application), `ITenantRoleCatalog.PermissionsForAsync` (Authorization.Application) — sólo desde Bootstrapper.
- Produces (T9, T13):

```csharp
public sealed record AssigneeDto(Guid MemberId, string DisplayName);
public sealed record AssigneesDto(IReadOnlyList<AssigneeDto> Items);
public interface IMessagingAssignees
{
    Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
public sealed record ListAssigneesQuery(Guid TenantId) : IQuery<AssigneesDto>;
// harness
public static Task<(Guid MembershipId, Guid UserId)> SeedMemberAsync(string connectionString, Guid tenantId, string displayName, string role, string state = "Active");
public static string AssigneesUrl(Guid tenantId);
```

- [ ] **Step 1: Harness y pruebas (RED)**

`MessagingApiHarness.cs`:

```csharp
    public static string AssigneesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/assignees";

    /// <summary>Spec 2026-10-10 §12 («Fixtures»): una membresía del tenant con un rol, escrita directo (sin invitación).
    /// <c>admin</c> y <c>advisor</c> conceden <c>messaging.conversation.manage</c>; <c>billing</c> no (P19).</summary>
    public static async Task<(Guid MembershipId, Guid UserId)> SeedMemberAsync(
        string connectionString, Guid tenantId, string displayName, string role, string state = "Active")
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.memberships (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at, expires_at, version, created_at, updated_at, display_name)
            VALUES (@id, @userId, @tenantId, @state, ARRAY[@role]::text[], 'invitation', now(), now(), now() + interval '3 days', 1, now(), now(), @name)
            """,
            ("id", membershipId), ("userId", userId), ("tenantId", tenantId), ("state", state), ("role", role), ("name", displayName));
        return (membershipId, userId);
    }
```

(Si la tabla tiene otra columna `NOT NULL` sin default, el `INSERT` lo dice con `23502`: se agrega con el valor que use `Membership.Activate` en `Membership.cs:197-230`.)

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AssigneesApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1 (D-A1) y §11: las membresías activas del tenant cuyos roles conceden manage, por
/// nombre; ni las de otro tenant, ni las suspendidas, ni las sin manage.</summary>
public sealed class AssigneesApiTests
{
    [Fact]
    public async Task OnlyActiveMembersThatCanReplyAreListedByName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var (beatriz, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        await SeedMemberAsync(connectionString, tenant.TenantId, "Carlos", "billing");
        await SeedMemberAsync(connectionString, tenant.TenantId, "Diana", "advisor", state: "Suspended");
        await SeedMemberAsync(connectionString, other.TenantId, "Elena", "advisor");
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var body = await client.GetFromJsonAsync<JsonElement>(AssigneesUrl(tenant.TenantId), TestContext.Current.CancellationToken);

        var items = body.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("memberId").GetGuid(), item.GetProperty("displayName").GetString())).ToArray();
        Assert.Contains((beatriz, "Beatriz"), items);
        Assert.Contains(items, item => item.Item1 == owner);
        Assert.Equal(2, items.Length);
        Assert.Equal(items.OrderBy(item => item.Item2, StringComparer.CurrentCultureIgnoreCase), items);
    }

    [Fact]
    public async Task ReadingAssigneesNeedsManage()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync(AssigneesUrl(tenant.TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~AssigneesApiTests"
```

Esperado (RED): `404 Not Found` en el `GetFromJsonAsync` (la ruta no existe) — `HttpRequestException … 404`.

- [ ] **Step 2: Puerto, handler, endpoint y adaptador**

`IMessagingAssignees.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §5.1: un ítem de <c>GET /messaging/assignees</c>. <c>DisplayName</c> nunca es <c>null</c>
/// (P17: nombre de la membresía o, si no tiene, el correo).</summary>
public sealed record AssigneeDto(Guid MemberId, string DisplayName);

public sealed record AssigneesDto(IReadOnlyList<AssigneeDto> Items);

/// <summary>
/// Spec 2026-10-10 §6.1.3: quién puede responder en el tenant. Adaptador en Bootstrapper sobre Tenancy, Identity y
/// Authorization (Messaging no los referencia). Por pedido, sin caché: los permisos se resuelven por request en este
/// repo y un cambio de rol tiene que verse ya.
/// </summary>
public interface IMessagingAssignees
{
    /// <summary>La membresía existe <b>en ese tenant</b>, está activa y sus roles conceden
    /// <c>messaging.conversation.manage</c>. Una de otro tenant responde igual que una inexistente (§11).</summary>
    Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Las que cumplen <see cref="CanReplyAsync"/>, ordenadas por nombre.</summary>
    Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

`ListAssignees.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListAssigneesQuery(Guid TenantId) : IQuery<AssigneesDto>;

/// <summary>Spec 2026-10-10 §5.1 (D-A1): el selector de transferir. Permiso <c>manage</c>: sólo quien puede
/// transferir necesita la lista.</summary>
public sealed class ListAssigneesHandler(IMessagingAssignees assignees, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListAssigneesQuery, AssigneesDto>
{
    public async Task<AssigneesDto> HandleAsync(ListAssigneesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        return new AssigneesDto(await assignees.ListAsync(query.TenantId, cancellationToken));
    }
}
```

`MessagingEndpoints.cs`, antes del `return endpoints;`:

```csharp
        // Spec 2026-10-10 §5.1 (D-A1): a quién se le puede transferir.
        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<AssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
```

y `private static async Task<IResult> ListAssigneesAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) => Results.Ok(await dispatcher.QueryAsync(new ListAssigneesQuery(tenantId), cancellationToken));`.

`src/Bootstrapper/MessagingAssignees.cs`:

```csharp
using Modules.Authorization.Application;
using Modules.Identity.Application;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-10 §6.1.3 y §6.5: membresía del tenant + estado + roles → permisos, sin caché.</summary>
internal sealed class MessagingAssignees(IMembershipRepository memberships, IUserDirectory users, ITenantRoleCatalog roles)
    : IMessagingAssignees
{
    public async Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var membership = await memberships.FindByIdAsync(new MembershipId(memberId), new TenantId(tenantId), cancellationToken);
        return membership is { State: MembershipState.Active } && await GrantsManageAsync(tenantId, membership, cancellationToken);
    }

    public async Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var result = new List<AssigneeDto>();
        foreach (var membership in await memberships.ListByTenantAsync(new TenantId(tenantId), cancellationToken))
        {
            if (membership.State != MembershipState.Active || !await GrantsManageAsync(tenantId, membership, cancellationToken))
            {
                continue;
            }

            // P17: como MessagingMemberNames, el nombre o el correo.
            var name = !string.IsNullOrWhiteSpace(membership.DisplayName)
                ? membership.DisplayName
                : await users.GetEmailAsync(membership.UserId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(new AssigneeDto(membership.Id.Value, name));
            }
        }

        return result.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task<bool> GrantsManageAsync(Guid tenantId, Membership membership, CancellationToken cancellationToken) =>
        (await roles.PermissionsForAsync(tenantId, membership.Roles, cancellationToken)).Contains(MessagingPermissions.ConversationManage, StringComparer.Ordinal);
}
```

`QepServiceCollectionExtensions.cs`: con los handlers de Messaging, `services.AddScoped<IQueryHandler<ListAssigneesQuery, AssigneesDto>, ListAssigneesHandler>();`; con los adaptadores (`:597-599`), `services.AddScoped<IMessagingAssignees, MessagingAssignees>();` con el comentario «Spec 2026-10-10 §6.1.3: quién puede responder, por Tenancy, Identity y Authorization».

- [ ] **Step 3: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~AssigneesApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde; `MessagingLayerTests` sin cambios (el adaptador vive en Bootstrapper).

- [ ] **Step 4: Formato y commit**

```
feat(messaging): quién puede responder y GET /messaging/assignees

IMessagingAssignees (spec 2026-10-10 §6.1.3) con adaptador en Bootstrapper sobre membresías,
roles y correos, sin caché; la lista del selector de transferir (D-A1) sólo trae membresías
activas del tenant con messaging.conversation.manage.
```

---

### Task 9: Ingesta — cliente asegurado, herencia del asignado, eventos del sistema y cita entrante

Spec §8.1 («Antes de la transacción», sentencias 1–3), §8.2, §8.6 («Entrante»), §8.7, D-A3, D-A12. Review Focus RF1 (cliente incompleto), RF8 (los eventos no tocan contadores ni foto) y RF9 (carrera de entregas). Todo el que escribe queda atado a un cliente de QEP; una conversación nueva de un cliente con dueño nace asignada; la reapertura, el cliente creado o vinculado y la herencia dejan su evento 1 ms antes del mensaje.

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs:6-14` (`EnsureAsync` y sus tipos)
- Modify: `src/Bootstrapper/MessagingCustomerDirectory.cs:7-20` (adaptador sobre `ICustomerWhatsAppDirectory`)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundContext.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/ConversationEventRows.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs` (firma con `InboundContext`, sentencias 1b, 2 y 3, eventos)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:16-22` (constructor), `:82-89` (preparación por mensaje)
- Modify (pruebas existentes que asumían `customer: null`): `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationsApiTests.cs:35-97`, `MessageSearchApiTests.cs:91-105`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundEventsTests.cs` (nuevo), `InboundRaceTests.cs` (nuevo)

**Interfaces:**
- Consumes: T2 (`ICustomerWhatsAppDirectory.EnsureAsync`, `WhatsAppContact`, `EnsureOutcome`), T5 (`ConversationEvent`, `ConversationEventJson`), T6 (columnas), T7 (`InboundMessage.UserId/WaId/Username/QuotedWamid`, `ResolveConversationAsync`), T8 (`IMessagingAssignees.CanReplyAsync`, `SeedMemberAsync`).
- Produces (T10, T12, T14):

```csharp
public sealed record MessagingContact(string UserId, string? WaId, string? ProfileName, string? Username);
public sealed record MessagingEnsuredCustomer(Guid CustomerId, bool Created);
Task<MessagingEnsuredCustomer> IMessagingCustomerDirectory.EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken);
internal sealed record InboundContext(Guid? CustomerId, bool CustomerCreated, Guid? InheritedMemberId) { public static InboundContext None { get; } }
internal static class ConversationEventRows
{
    public static Task InsertAsync(MessagingDbContext dbContext, Guid tenantId, Guid connectionId, Guid conversationId,
        ConversationEvent value, DateTimeOffset occurredAt, DateTimeOffset now, CancellationToken cancellationToken);
}
public static Task<Guid?> InboundIngestion.IngestAsync(MessagingDbContext dbContext, Guid tenantId, Guid connectionId, InboundMessage message, InboundContext context, DateTimeOffset now, CancellationToken cancellationToken);
```

- [ ] **Step 1: Las pruebas (RED)**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundEventsTests.cs`:

```csharp
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.1–§8.2, §8.6, §8.7: cliente incompleto o vinculado con su evento antes del mensaje, los
/// eventos no tocan contadores ni foto (RF8), la reapertura sin actor, la herencia del asignado (D-A3) y la cita entrante.</summary>
public sealed class InboundEventsTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, factory.CreateClient());
    }

    private static async Task IngestAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Anonymous, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<string> EventsAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(string_agg(details->>'type', ',' ORDER BY occurred_at, id), '') FROM messaging.messages WHERE direction = 3");

    [Fact]
    public async Task ANewPersonGetsAnIncompleteCustomerAndACustomerCreatedEventJustBeforeTheMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", null, "wamid.1", 1760000000, "hola", profileName: "Laura Pérez"));

        var customerId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations");
        Assert.Equal("Incomplete|Laura Pérez|CO.1349120865530274", await ScalarAsync<string>(f.ConnectionString,
            "SELECT completeness || '|' || name || '|' || whatsapp_user_id FROM customers.customers WHERE id = @id", ("id", customerId)));
        Assert.Equal("CustomerCreated", await EventsAsync(f));
        Assert.Equal(customerId.ToString(), await ScalarAsync<string>(f.ConnectionString, "SELECT details->>'customerId' FROM messaging.messages WHERE direction = 3"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            "SELECT (SELECT occurred_at FROM messaging.messages WHERE direction = 1) - occurred_at = interval '1 millisecond' FROM messaging.messages WHERE direction = 3"));
    }

    [Fact]
    public async Task AnExistingCustomerByPhoneIsLinked()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.1", 1760000000, "hola"));

        Assert.Equal(customerId, await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations"));
        Assert.Equal("CustomerLinked", await EventsAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    // RF8: un evento no sube unread_count, no es la foto, no tiene search_vector y no se repite con un reenvío.
    [Fact]
    public async Task EventsDoNotTouchCountersNorTheSnapshotNorRepeatOnAResend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var json = MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola");

        await IngestAsync(f, json);
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000999, "hola"));

        Assert.Equal("1|1|hola", await ScalarAsync<string>(f.ConnectionString,
            "SELECT unread_count || '|' || last_message_direction || '|' || last_message_preview FROM messaging.conversations"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            "SELECT c.last_message_id = m.id FROM messaging.conversations c JOIN messaging.messages m ON m.conversation_id = c.id AND m.direction = 1"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT search_vector = ''::tsvector AND status = 2 AND wamid IS NULL FROM messaging.messages WHERE direction = 3"));
        Assert.Equal("CustomerCreated", await EventsAsync(f));
    }

    [Fact]
    public async Task AnInboundOnAResolvedConversationReopensItWithAReopenedEventWithoutActor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved'");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", 1760000100, "otra vez"));

        Assert.Equal("CustomerCreated,Reopened", await EventsAsync(f));
        Assert.Equal("Open|", await ScalarAsync<string>(f.ConnectionString,
            "SELECT c.status || '|' || coalesce(m.details->>'actor', '') FROM messaging.conversations c JOIN messaging.messages m ON m.conversation_id = c.id WHERE m.details->>'type' = 'Reopened'"));
    }

    // D-A3: el cliente con conversación asignada en la conexión A escribe por la B → nace asignada, con Inherited.
    [Theory]
    [InlineData("advisor", true)]
    [InlineData("billing", false)]
    public async Task ANewConversationOfACustomerInheritsTheAssigneeOnlyIfTheyCanReply(string role, bool inherits)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "222");
        var (member, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beatriz", role);
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "por ventas"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", member));

        await IngestAsync(f, MetaPayloads.Inbound("333", "CO.1", null, "wamid.2", 1760000100, "por soporte"));

        var assigned = await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(c.assigned_member_id::text, '') FROM messaging.conversations c JOIN messaging.messages m ON m.conversation_id = c.id WHERE m.wamid = 'wamid.2'");
        Assert.Equal(inherits ? member.ToString() : string.Empty, assigned);
        Assert.Equal(inherits ? 1L : 0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'Inherited' AND details->>'target' = @m", ("m", member.ToString())));
        // Mismo cliente en las dos: se encontró por BSUID (Existing), no hay CustomerLinked en la segunda.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(DISTINCT customer_id) FROM messaging.conversations"));
    }

    [Fact]
    public async Task AnInboundQuotingOurOutboundResolvesItAndAnUnknownQuoteKeepsOnlyTheWamid()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var outbound = await SeedOutboundAsync(f.ConnectionString, conversationId, f.Tenant.TenantId, f.ConnectionId, "wamid.ours");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", 1760000100, "sí, ese", quotedWamid: "wamid.ours"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", 1760000200, "y este", quotedWamid: "wamid.desde-el-telefono"));

        Assert.Equal($"{outbound}|wamid.ours", await ScalarAsync<string>(f.ConnectionString,
            "SELECT reply_to_message_id::text || '|' || reply_to_wamid FROM messaging.messages WHERE wamid = 'wamid.2'"));
        Assert.Equal("|wamid.desde-el-telefono", await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(reply_to_message_id::text, '') || '|' || reply_to_wamid FROM messaging.messages WHERE wamid = 'wamid.3'"));
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/InboundRaceTests.cs` (en la colección no paralela):

```csharp
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §9.4 (RF9): varias entregas del mismo BSUID nuevo procesadas a la vez por dos pasadas del
/// worker → un cliente, una conversación, todos los mensajes, cero errores.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class InboundRaceTests
{
    [Fact]
    public async Task ConcurrentDeliveriesOfANewBsuidCreateOneCustomerAndOneConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        const int Deliveries = 8;
        for (var index = 0; index < Deliveries; index++)
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.RACE", null, $"wamid.{index}", 1760000000 + index, $"mensaje {index}"));
        }

        await Task.WhenAll(DrainDeliveriesAsync(factory), DrainDeliveriesAsync(factory), DrainDeliveriesAsync(factory));

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.RACE'"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal((long)Deliveries, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal((long)Deliveries, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL AND last_error IS NULL"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'CustomerCreated'"));
    }
}
```

`CountAsync(connectionString, sql, params (string, object)[])` ya existe en el harness; `ScalarAsync<T>` también.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~InboundEventsTests|FullyQualifiedName~InboundRaceTests"
```

Esperado (RED): `customer_id` nulo (`InvalidCastException`/`NullReferenceException` al leer el `Guid`), cero eventos, sin herencia, `reply_to_message_id` nulo.

- [ ] **Step 2: El puerto y el adaptador**

`IMessagingCustomerDirectory.cs`:

```csharp
/// <summary>Spec 2026-10-10 §8.2: quien escribió, como lo ve Messaging (el <c>waId</c> son los dígitos sin «+»).</summary>
public sealed record MessagingContact(string UserId, string? WaId, string? ProfileName, string? Username);

/// <summary><c>Created</c> decide el evento: <c>CustomerCreated</c> si se creó, si no <c>CustomerLinked</c> (§8.2).</summary>
public sealed record MessagingEnsuredCustomer(Guid CustomerId, bool Created);
```

y en la interfaz:

```csharp
    /// <summary>Spec 2026-10-10 §8.2: el cliente de QEP de esta persona, creándolo incompleto si no existe. Corre antes
    /// y fuera de la transacción de la ingesta; es idempotente por BSUID.</summary>
    Task<MessagingEnsuredCustomer> EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken);
```

`MessagingCustomerDirectory.cs`: el constructor gana `ICustomerWhatsAppDirectory whatsApp`, y:

```csharp
    public async Task<MessagingEnsuredCustomer> EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var ensured = await whatsApp.EnsureAsync(
            tenantId,
            new WhatsAppContact(contact.UserId, contact.WaId is null ? null : "+" + contact.WaId, contact.ProfileName, contact.Username),
            cancellationToken);
        return new MessagingEnsuredCustomer(ensured.CustomerId, ensured.Outcome == EnsureOutcome.Created);
    }
```

- [ ] **Step 3: Eventos por SQL, contexto y la ingesta**

`InboundContext.cs`:

```csharp
namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-10 §8.1, «Antes de la transacción»: lo que la ingesta necesita y se resolvió afuera.
/// <see cref="None"/> = la conversación ya tiene cliente: no se llamó a Customers (camino común, sin costo extra).</summary>
internal sealed record InboundContext(Guid? CustomerId, bool CustomerCreated, Guid? InheritedMemberId)
{
    public static InboundContext None { get; } = new(null, false, null);
}
```

`ConversationEventRows.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-10 §6.1.5 y §8.7: un evento es una fila de messages con direction = 3, kind = 13, status = 2
/// (D-A5), sin wamid, client_id, text ni caption (su search_vector sale vacío solo). Va en la transacción de quien lo
/// causa; nunca toca la conversación.</summary>
internal static class ConversationEventRows
{
    public static Task InsertAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, Guid conversationId,
        ConversationEvent value, DateTimeOffset occurredAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var details = ConversationEventJson.Serialize(value);
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, details, created_at)
            VALUES ({Guid.CreateVersion7()}, {conversationId}, {tenantId}, {connectionId}, {occurredAt.ToUniversalTime()}, 3, 13, 2, {details}::jsonb, {now})
            """,
            cancellationToken);
    }
}
```

`InboundIngestion.cs`:
- `IngestAsync` e `IngestOnceAsync` reciben `InboundContext context` después de `message`.
- `ResolveConversationAsync` devuelve `(Guid Id, bool Created)`; la sentencia 1b inserta además `customer_id`, `assigned_member_id` y `assigned_at`:

```sql
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, username, parent_user_id, profile_name,
                                                 customer_id, assigned_member_id, assigned_at,
                                                 status, unread_count, last_activity_at, created_at, updated_at, version)
            VALUES ({newConversationId}, {tenantId}, {connectionId}, {message.UserId}, {message.WaId}, {message.Username}, {message.ParentUserId},
                    {message.ProfileName}, {context.CustomerId}, {context.InheritedMemberId},
                    CASE WHEN {context.InheritedMemberId}::uuid IS NULL THEN NULL ELSE {now} END,
                    'Open', 0, {message.OccurredAt}, {now}, {now}, 1)
```

  (`Created = inserted.Count == 1`; las ramas «ya existía» y «adoptada» devuelven `Created = false`).
- Sentencia 2: columna `reply_to_message_id` con `(SELECT id FROM messaging.messages WHERE connection_id = {connectionId} AND wamid = {message.QuotedWamid})` (por `IX_messages_connection_wamid`; con `QuotedWamid` nulo da `NULL`).
- Sentencia 3: se envuelve en un CTE que lee y bloquea lo anterior, y devuelve qué había (alias distintos para que `status` y `customer_id` del `SET` no sean ambiguos):

```csharp
        var before = await dbContext.Database.SqlQuery<PreviousState>(
            $"""
            WITH old AS (
                SELECT status AS old_status, customer_id AS old_customer_id
                FROM messaging.conversations WHERE id = {conversationId} FOR UPDATE)
            UPDATE messaging.conversations SET
                unread_count       = unread_count + 1,
                status             = 'Open',
                profile_name       = COALESCE({message.ProfileName}, profile_name),
                wa_id              = COALESCE({message.WaId}, wa_id),
                username           = COALESCE({message.Username}, username),
                parent_user_id     = COALESCE({message.ParentUserId}, parent_user_id),
                customer_id        = COALESCE(customer_id, {context.CustomerId}),
                -- … last_inbound_*, last_activity_at, last_message_*, updated_at y version exactamente como antes …
            FROM old
            WHERE id = {conversationId}
            RETURNING old.old_status AS "OldStatus", old.old_customer_id AS "OldCustomerId"
            """).SingleAsync(cancellationToken);
```

  con `private sealed record PreviousState(string OldStatus, Guid? OldCustomerId);`. Las líneas de `last_inbound_wamid` a `version` son las de hoy (`InboundIngestion.cs:58-68`), sin cambiar una coma.
- Después de la sentencia 3 (y del medio, si lo hay), antes del commit, los eventos (sólo se llega acá si la sentencia 2 insertó: un reenvío no repite eventos):

```csharp
        // Spec 2026-10-10 §8.7: 1 ms antes del mensaje que los causó, para que el hilo (IX_messages_thread) los ponga justo antes.
        var eventAt = message.OccurredAt.AddMilliseconds(-1);
        if (conversation.Created && context.InheritedMemberId is { } heir)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, conversation.Id,
                new ConversationEvent(ConversationEventType.Inherited, Target: heir), eventAt, now, cancellationToken);
        }

        // §8.2: el evento lo decide la transición de customer_id de NULL a un valor. En una conversación recién creada el
        // INSERT ya lo puso, así que «antes» es la creación misma.
        var linkedNow = context.CustomerId is not null && (conversation.Created || before.OldCustomerId is null);
        if (linkedNow)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, conversation.Id,
                new ConversationEvent(context.CustomerCreated ? ConversationEventType.CustomerCreated : ConversationEventType.CustomerLinked, CustomerId: context.CustomerId),
                eventAt, now, cancellationToken);
        }

        if (before.OldStatus == nameof(ConversationStatus.Resolved))
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, conversation.Id,
                new ConversationEvent(ConversationEventType.Reopened), eventAt, now, cancellationToken);
        }
```

`WebhookDeliveryProcessor.cs`: el constructor gana `IMessagingCustomerDirectory customers` y `IMessagingAssignees assignees`; el bucle de `:85-88` pasa a:

```csharp
            foreach (var message in change.Messages)
            {
                var context = await PrepareAsync(route.TenantId, route.ConnectionId, message, cancellationToken);
                await InboundIngestion.IngestAsync(dbContext, route.TenantId, route.ConnectionId, message, context, now, cancellationToken);
            }
```

con:

```csharp
    /// <summary>Spec 2026-10-10 §8.1, «Antes de la transacción»: con conversación y cliente no se llama a Customers. Si
    /// no hay conversación, el asignado a heredar (§8.2, P16): la más reciente del cliente con asignado, si todavía puede
    /// responder.</summary>
    private async Task<InboundContext> PrepareAsync(Guid tenantId, Guid connectionId, InboundMessage message, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Database.SqlQuery<ExistingConversation>(
            $"""SELECT id AS "Id", customer_id AS "CustomerId" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
            .ToListAsync(cancellationToken);
        if (existing is [{ CustomerId: not null }])
        {
            return InboundContext.None;
        }

        var customer = await customers.EnsureAsync(
            tenantId, new MessagingContact(message.UserId, message.WaId, message.ProfileName, message.Username), cancellationToken);
        Guid? heir = null;
        if (existing.Count == 0)
        {
            var candidates = await dbContext.Database.SqlQuery<Guid>(
                $"""
                SELECT assigned_member_id AS "Value" FROM messaging.conversations
                WHERE tenant_id = {tenantId} AND customer_id = {customer.CustomerId} AND assigned_member_id IS NOT NULL
                ORDER BY last_activity_at DESC
                LIMIT 1
                """).ToListAsync(cancellationToken);
            if (candidates is [var candidate] && await assignees.CanReplyAsync(tenantId, candidate, cancellationToken))
            {
                heir = candidate;
            }
        }

        return new InboundContext(customer.CustomerId, customer.Created, heir);
    }

    private sealed record ExistingConversation(Guid Id, Guid? CustomerId);
```

- [ ] **Step 4: Las pruebas viejas que asumían «sin cliente»**

Con esta tarea todo el que escribe tiene cliente; tres aserciones cambian de verdad (no es un parche):
- `ConversationsApiTests.TheListIsOrderedByActivityWithCountsCustomerAndConnectionName`: el `CreateCustomerAsync(… "Droguería Central", "300 123 4567")` se mueve **antes** del primer `IngestAsync` (así la ingesta lo vincula por teléfono), y `Assert.Equal(JsonValueKind.Null, items[1].GetProperty("customer").ValueKind);` pasa a `Assert.Equal("Pedro", items[1].GetProperty("customer").GetProperty("name").GetString());` (Pedro nace incompleto).
- `ConversationsApiTests.TheSearchMatchesProfileNumberAndCustomerName`: el `CreateCustomerAsync` también va antes de los dos `IngestAsync`.
- `MessageSearchApiTests.EachHitCarriesItsConversationContactCustomerAndConnection`: `Assert.Equal(JsonValueKind.Null, hit.GetProperty("customer").ValueKind);` pasa a `Assert.Equal("Laura", hit.GetProperty("customer").GetProperty("name").GetString());`.

- [ ] **Step 5: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~InboundEventsTests|FullyQualifiedName~InboundRaceTests|FullyQualifiedName~InboundBsuidTests|FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~ConversationsApiTests|FullyQualifiedName~MessageSearchApiTests|FullyQualifiedName~ThreadApiTests|FullyQualifiedName~WebhookLoadTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde. Si `ThreadApiTests` cuenta mensajes del hilo y ahora ve el evento `CustomerCreated`, la aserción de cantidad o de `hasMore` se ajusta sumando el evento **y se deja un comentario** «spec 2026-10-10 §8.7: el evento CustomerCreated también es del hilo» (el contrato dice que `hasMore` los cuenta).

- [ ] **Step 6: Formato y commit**

```
feat(messaging): todo el que escribe es cliente, la herencia del asignado y los eventos de la ingesta

Antes de la transacción, IMessagingCustomerDirectory.EnsureAsync ata la conversación a un cliente
(incompleto si no existe) y, en una conversación nueva, busca el asignado a heredar (spec
2026-10-10 §8.1–§8.2). En la transacción: customer_id, la cita entrante resuelta por wamid y los
eventos CustomerCreated/CustomerLinked, Inherited y Reopened 1 ms antes del mensaje (§8.7).
```

---

### Task 10: Cambio de número (`user_id_update` y `user_changed_user_id`)

Spec §8.3, D-A7, P6, P7. Review Focus RF3. Las dos señales se procesan igual y son idempotentes: la segunda no encuentra nada que cambiar.

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/ContactNumberChange.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:38-64` (rutas de `UserIdUpdateChange` y de `MessagesChange.NumberChanges`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs` (`ReplaceUserIdAsync`)
- Modify: `src/Bootstrapper/MessagingCustomerDirectory.cs` (`partial`, logger, `ReplaceUserIdAsync`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MetaPayloads.cs` (`UserIdUpdate`, `UserChangedUserId`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ContactNumberChangeTests.cs` (nuevo)

**Interfaces:**
- Consumes: T2 (`ICustomerWhatsAppDirectory.ReplaceWhatsAppUserIdAsync` → `bool`), T7 (`UserIdChange`, `UserIdUpdateChange`, `MessagesChange.NumberChanges`), T9 (`ConversationEventRows.InsertAsync`).
- Produces:

```csharp
Task IMessagingCustomerDirectory.ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);
internal static class ContactNumberChange
{
    /// <returns>Los ids de las conversaciones tocadas (para pruebas y logs).</returns>
    public static Task<IReadOnlyList<Guid>> ApplyAsync(MessagingDbContext dbContext, Guid tenantId, Guid connectionId, UserIdChange change, DateTimeOffset now, CancellationToken cancellationToken);
}
// MetaPayloads
public static string UserIdUpdate(string previous, string current, string? phoneNumberId = "111", string wabaId = DefaultWabaId);
public static string UserChangedUserId(string phoneNumberId, string fromUserId, string newUserId, string body, string wamid, long timestamp);
```

- [ ] **Step 1: Fixtures y pruebas (RED)**

`MetaPayloads.cs`:

```csharp
    /// <summary>Spec 2026-10-10 §3: la forma de <c>user_id_update</c> no está documentada con un ejemplo; ésta es la que
    /// lee el parser (riesgo de §13).</summary>
    public static string UserIdUpdate(string previous, string current, string? phoneNumberId = "111", string wabaId = DefaultWabaId)
    {
        var value = new JsonObject { ["user_id"] = new JsonObject { ["previous"] = previous, ["current"] = current } };
        if (phoneNumberId is not null)
        {
            value["metadata"] = Metadata(phoneNumberId, "15550000000");
        }

        return Change("user_id_update", value.ToJsonString(), wabaId);
    }

    public static string UserChangedUserId(string phoneNumberId, string fromUserId, string newUserId, string body, string wamid, long timestamp)
    {
        var message = new JsonObject
        {
            ["from_user_id"] = fromUserId,
            ["id"] = wamid,
            ["timestamp"] = Seconds(timestamp),
            ["type"] = "system",
            ["system"] = new JsonObject { ["body"] = body, ["type"] = "user_changed_user_id", ["user_id"] = newUserId },
        };
        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = Metadata(phoneNumberId, "15550000000"),
            ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = "Laura" }, ["user_id"] = newUserId }),
            ["messages"] = new JsonArray(message),
        };
        return Change("messages", value.ToJsonString());
    }
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ContactNumberChangeTests.cs`:

```csharp
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.3 (RF3): las dos señales, en cualquier orden y repetidas, re-keyean la conversación sin
/// perder historia, cambian el BSUID del cliente y dejan un solo ContactChangedNumber; con una conversación ya creada
/// con el BSUID nuevo no se fusionan (D-A7).</summary>
public sealed class ContactNumberChangeTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, factory.CreateClient());
    }

    private static async Task SendAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Anonymous, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<long> ChangeEventsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'ContactChangedNumber'");

    [Fact]
    public async Task AUserIdUpdateReKeysTheConversationAndTheCustomerKeepingTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.NEW", null, "wamid.2", 1760000100, "después"));

        Assert.Equal($"{conversationId}|CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT id::text || '|' || user_id FROM messaging.conversations"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString,
            "SELECT whatsapp_user_id FROM customers.customers WHERE id = (SELECT customer_id FROM messaging.conversations)"));
    }

    [Fact]
    public async Task BothSignalsInAnyOrderAndRepeatedApplyOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));

        await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.OLD", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys", 1760000050));
        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.NEW", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys2", 1760000060));

        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT user_id FROM messaging.conversations"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
        // El mensaje de sistema no es un mensaje del hilo.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
    }

    [Fact]
    public async Task WithoutPhoneNumberIdTheUpdateRoutesByTheWaba()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));

        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW", phoneNumberId: null));

        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT user_id FROM messaging.conversations"));
    }

    // D-A7 y P6: un mensaje con el BSUID nuevo llegó antes que la señal. No se fusionan; las dos llevan el evento, una vez.
    [Fact]
    public async Task AConversationAlreadyCreatedWithTheNewBsuidIsNotMerged()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.NEW", null, "wamid.2", 1760000100, "con el número nuevo"));

        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));

        Assert.Equal("CO.NEW,CO.OLD", await ScalarAsync<string>(f.ConnectionString, "SELECT string_agg(user_id, ',' ORDER BY user_id) FROM messaging.conversations"));
        Assert.Equal(2L, await ChangeEventsAsync(f));
        Assert.Equal(2L, await CountAsync(f.ConnectionString,
            "SELECT count(DISTINCT conversation_id) FROM messaging.messages WHERE details->>'type' = 'ContactChangedNumber' AND details ? 'linkedConversationId'"));
        // El BSUID nuevo ya era de otro cliente (el incompleto del segundo mensaje): el viejo no se toca.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.OLD'"));
    }
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ContactNumberChangeTests"
```

Esperado (RED): `user_id` sigue en `CO.OLD`; cero eventos.

- [ ] **Step 2: Implementación**

`ContactNumberChange.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-10 §8.3, por conexión y en una transacción. Idempotente: si nadie tiene el BSUID anterior, no
/// hay nada que cambiar. Si el nuevo ya tiene conversación (llegó un mensaje antes que la señal), no se fusionan (D-A7):
/// las dos llevan el evento, marcado con la otra (P6) para que una señal repetida no lo duplique.</summary>
internal static class ContactNumberChange
{
    private const string ConnectionUserIndex = "IX_conversations_connection_user";

    public static async Task<IReadOnlyList<Guid>> ApplyAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, UserIdChange change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyOnceAsync(dbContext, tenantId, connectionId, change, now, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ConnectionUserIndex)
        {
            // Un mensaje con el BSUID nuevo creó su conversación entre el SELECT y el UPDATE: la segunda vuelta ve el choque.
            return await ApplyOnceAsync(dbContext, tenantId, connectionId, change, now, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<Guid>> ApplyOnceAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, UserIdChange change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var source = await IdByUserAsync(dbContext, connectionId, change.Previous, cancellationToken);
        if (source is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var target = await IdByUserAsync(dbContext, connectionId, change.Current, cancellationToken);
        if (target is null)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.conversations
                   SET user_id = {change.Current}, wa_id = COALESCE({change.WaId}, wa_id), version = version + 1, updated_at = {now}
                 WHERE id = {source.Value}
                """, cancellationToken);
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, source.Value,
                new ConversationEvent(ConversationEventType.ContactChangedNumber), now, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return [source.Value];
        }

        var already = await dbContext.Database.SqlQuery<int>(
            $"""
            SELECT 1 AS "Value" FROM messaging.messages
            WHERE conversation_id = {target.Value} AND direction = 3
              AND details->>'type' = 'ContactChangedNumber' AND details->>'linkedConversationId' = {source.Value.ToString("D")}
            """).ToListAsync(cancellationToken);
        if (already.Count == 0)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, source.Value,
                new ConversationEvent(ConversationEventType.ContactChangedNumber, LinkedConversationId: target.Value), now, now, cancellationToken);
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, target.Value,
                new ConversationEvent(ConversationEventType.ContactChangedNumber, LinkedConversationId: source.Value), now, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return already.Count == 0 ? [source.Value, target.Value] : [];
    }

    private static async Task<Guid?> IdByUserAsync(MessagingDbContext dbContext, Guid connectionId, string userId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {userId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }
}
```

`IMessagingCustomerDirectory.cs`:

```csharp
    /// <summary>Spec 2026-10-10 §8.3: el cliente con el BSUID anterior pasa al nuevo. Si el nuevo ya es de otro cliente no
    /// se toca (lo registra el adaptador). El teléfono del cliente no cambia: es dato maestro.</summary>
    Task ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);
```

`MessagingCustomerDirectory.cs`: `internal sealed partial class MessagingCustomerDirectory(ICustomerPhoneDirectory phones, ICustomerWhatsAppDirectory whatsApp, ILogger<MessagingCustomerDirectory> logger)`, y:

```csharp
    // §11: sin el BSUID en el log; el tenant basta para buscar la conversación.
    [LoggerMessage(Level = LogLevel.Information, Message = "A WhatsApp number change for tenant {TenantId} was not applied to Customers: no customer had the previous id, or the new one already belongs to another customer.")]
    private static partial void LogNotReplaced(ILogger logger, Guid tenantId);

    public async Task ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken)
    {
        if (!await whatsApp.ReplaceWhatsAppUserIdAsync(tenantId, previous, current, cancellationToken))
        {
            LogNotReplaced(logger, tenantId);
        }
    }
```

`WebhookDeliveryProcessor.cs`:
- `ProcessAsync` (`:43-60`): `case UserIdUpdateChange update: await ProcessUserIdUpdateAsync(deliveryId, update, cancellationToken); break;`
- En `ProcessMessagesAsync`, después de los mensajes y **antes** de los statuses, siempre (P7, aunque esté `Paused` o el módulo apagado):

```csharp
        foreach (var numberChange in change.NumberChanges)
        {
            await ApplyNumberChangeAsync([route], numberChange, cancellationToken);
        }
```

- Nuevos:

```csharp
    /// <summary>Spec 2026-10-10 §8.3: por phone_number_id si viene; si no, por la WABA (el BSUID es por portafolio y
    /// aplica a todas sus conexiones). P7: también con Paused o el módulo apagado.</summary>
    private async Task ProcessUserIdUpdateAsync(long deliveryId, UserIdUpdateChange update, CancellationToken cancellationToken)
    {
        IReadOnlyList<MessagingRoute> routes = update.PhoneNumberId is { } phoneNumberId
            ? (await routing.FindRouteAsync(phoneNumberId, cancellationToken)) is { } route ? [route] : []
            : await directory.FindByAccountAsync(update.WabaId, cancellationToken);
        if (routes.Count == 0)
        {
            LogIgnoredField(logger, deliveryId, "user_id_update");
            return;
        }

        await ApplyNumberChangeAsync(routes, update.Change, cancellationToken);
    }

    private async Task ApplyNumberChangeAsync(IReadOnlyList<MessagingRoute> routes, UserIdChange change, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var route in routes)
        {
            await ContactNumberChange.ApplyAsync(dbContext, route.TenantId, route.ConnectionId, change, now, cancellationToken);
        }

        foreach (var tenantId in routes.Select(route => route.TenantId).Distinct())
        {
            await customers.ReplaceUserIdAsync(tenantId, change.Previous, change.Current, cancellationToken);
        }
    }
```

- [ ] **Step 3: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~ContactNumberChangeTests|FullyQualifiedName~AccountUpdateTests|FullyQualifiedName~InboundBsuidTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde.

- [ ] **Step 4: Formato y commit**

```
feat(messaging): cambio de número por user_id_update y user_changed_user_id

Las dos señales re-keyean la conversación por conexión, dejan un ContactChangedNumber y pasan el
BSUID del cliente (spec 2026-10-10 §8.3); idempotentes y sin fusionar si el número nuevo ya tenía
conversación (D-A7). Se aplican aunque la conexión esté pausada.
```

---

### Task 11: Lectura de la bandeja — cliente por `customer_id`, `assignedTo` con `isMe`, filtro `assigned`, contadores y búsqueda por usuario y cliente

Spec §5.1 (`ConversationSummary`, `GET /conversations`), §6.1.2, §6.1.6, D-A8, D-A10, **D-A13** (`assignedTo.isMe`, spec `da3ad4a`), P15, P20. La membresía de quien llama se resuelve **una vez por request** y la comparten el filtro `assigned=me`, `counts.mine` e `isMe`.

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:10-46` (`CustomerRefDto`, `ConversationSummary`, `ConversationCountsDto`, `SentByDto` → `MemberRefDto`, `AssignedToDto` nuevo)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/CallerMembership.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:36-79` (`ConversationSummaryBuilder`), `:101` (`MemberRefDto`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs` (`FindRefsAsync`, `FindIdsByNameAsync`)
- Modify: `src/Bootstrapper/MessagingCustomerDirectory.cs` (los dos métodos; `MatchAsync` con `IsComplete`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs:25-35` (`ConversationListFilter`, `AssignedFilter`, firmas)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs:17-52`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ListConversations.cs:8-57`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:130-132` (`assigned`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs:31` (`CallerMembership`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:102-123` (`InMemoryConversationQueries`), `ConversationReadsTests.cs:25-35`, `MessagingValidatorsTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationListApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: T2 (`ICustomerWhatsAppDirectory.FindRefsAsync/FindIdsByNameAsync`), T3 (`CustomerPhoneMatch.IsComplete`), T6 (`ConversationRow.CustomerId/AssignedMemberId/Username`), T8 (`SeedMemberAsync`), `IMembershipDirectory.FindActiveMembershipIdAsync` (Tenancy.Application, ya referenciado).
- Produces (T12, T13, T14):

```csharp
public sealed record CustomerRefDto(Guid Id, string Name, bool IsComplete);
public sealed record MemberRefDto(Guid MemberId, string DisplayName);           // antes SentByDto (P11)
public sealed record AssignedToDto(Guid MemberId, string DisplayName, bool IsMe); // D-A13
public sealed record ConversationSummary(Guid Id, Guid ConnectionId, string ConnectionName, ContactDto Contact, CustomerRefDto? Customer,
    AssignedToDto? AssignedTo, string Status, int UnreadCount, LastMessageDto? LastMessage, DateTimeOffset? CustomerWindowExpiresAt, DateTimeOffset UpdatedAt, long Version);
public sealed record ConversationCountsDto(int Open, int Unread, int Mine, int Unassigned);
public sealed class CallerMembership { Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken); }
public enum AssignedFilter { All, Mine, Unassigned }
public sealed record ConversationListFilter(ConversationStatus Status, AssignedFilter Assigned, Guid? MemberId, string? Search,
    IReadOnlyCollection<string> CustomerWaIds, IReadOnlyCollection<Guid> CustomerIds);
Task<(IReadOnlyList<ConversationRow> Items, int Total)> IConversationQueries.ListAsync(Guid tenantId, ConversationListFilter filter, int page, int pageSize, CancellationToken cancellationToken);
Task<ConversationCountsDto> IConversationQueries.CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken);
ListConversationsQuery(Guid TenantId, string? Status, string? Search, int? Page, int? PageSize, string? Assigned = null);
Task<IReadOnlyDictionary<Guid, CustomerRefDto>> IMessagingCustomerDirectory.FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
Task<IReadOnlyList<Guid>> IMessagingCustomerDirectory.FindIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken);
// ConversationSummaryBuilder(IMessagingConnectionDirectory, IMessagingCustomerDirectory, IMessagingMemberNames, CallerMembership)
public static ConversationSummary ConversationSummaryBuilder.ToSummary(ConversationRow row, IReadOnlyDictionary<Guid, string> connectionNames,
    IReadOnlyDictionary<Guid, CustomerRefDto> customersById, IReadOnlyDictionary<string, CustomerRefDto> customersByWaId,
    IReadOnlyDictionary<Guid, string> memberNames, Guid? callerMemberId);
```

- [ ] **Step 1: Las pruebas (RED)**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationListApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1 y §6.1.6: assigned=me|none|all, counts.mine y counts.unassigned sólo de abiertas
/// (D-A10), assignedTo con isMe calculado por el servidor (D-A13), el cliente por customer_id con isComplete (D-A8) y
/// el respaldo por teléfono para lo viejo, y la búsqueda por username y por nombre del cliente.</summary>
public sealed class ConversationListApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid Owner, Guid Beatriz, Guid BeatrizUser);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var (beatriz, beatrizUser) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        return new Fixture(factory, connectionString, tenant, connectionId, await OwnerMembershipIdAsync(connectionString, tenant), beatriz, beatrizUser);
    }

    private static async Task<Guid> IngestAsync(Fixture f, string userId, string wamid, long timestamp, string? username = null)
    {
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", userId, null, wamid, timestamp, "hola", username: username));
        await DrainDeliveriesAsync(f.Factory);
        return await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations WHERE user_id = @u", ("u", userId));
    }

    private static Task AssignAsync(Fixture f, Guid conversationId, Guid? memberId, string status = "Open") =>
        ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = CASE WHEN @m IS NULL THEN NULL ELSE now() END, status = @s WHERE id = @id",
            ("m", (object?)memberId ?? DBNull.Value), ("s", status), ("id", conversationId));

    private static async Task<JsonElement> ListAsync(HttpClient client, Guid tenantId, string query) =>
        await client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(tenantId)}{query}", TestContext.Current.CancellationToken);

    private static Guid[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task TheAssignedFilterAndItsCountsOnlyCountOpenConversations()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var mine = await IngestAsync(f, "CO.A", "w1", 1760000100);
        var hers = await IngestAsync(f, "CO.B", "w2", 1760000200);
        var nobodys = await IngestAsync(f, "CO.C", "w3", 1760000300);
        var nobodysResolved = await IngestAsync(f, "CO.D", "w4", 1760000400);
        var mineResolved = await IngestAsync(f, "CO.E", "w5", 1760000500);
        await AssignAsync(f, mine, f.Owner);
        await AssignAsync(f, hers, f.Beatriz);
        await AssignAsync(f, nobodysResolved, null, "Resolved");
        await AssignAsync(f, mineResolved, f.Owner, "Resolved");
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var me = await ListAsync(client, f.Tenant.TenantId, "?assigned=me");
        var none = await ListAsync(client, f.Tenant.TenantId, "?assigned=none");
        var all = await ListAsync(client, f.Tenant.TenantId, "?assigned=all");
        var resolvedMine = await ListAsync(client, f.Tenant.TenantId, "?status=Resolved&assigned=me");

        Assert.Equal([mine], Ids(me));
        Assert.Equal([nobodys], Ids(none));
        Assert.Equal([nobodys, hers, mine], Ids(all));
        Assert.Equal([mineResolved], Ids(resolvedMine));
        var counts = all.GetProperty("counts");
        Assert.Equal((3, 1, 1), (counts.GetProperty("open").GetInt32(), counts.GetProperty("mine").GetInt32(), counts.GetProperty("unassigned").GetInt32()));
    }

    // D-A13: la SPA no conoce su memberId; isMe lo decide el servidor con la membresía de quien llama.
    [Fact]
    public async Task IsMeIsTrueOnlyForTheAssigneeAndAssignedToIsNullWhenUnassigned()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var hers = await IngestAsync(f, "CO.B", "w1", 1760000100);
        var nobodys = await IngestAsync(f, "CO.C", "w2", 1760000200);
        await AssignAsync(f, hers, f.Beatriz);
        using var owner = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        using var beatriz = CreateClient(f.Factory, f.BeatrizUser, f.Tenant.TenantId, ReadPermissions);

        var seenByOwner = await owner.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, hers), TestContext.Current.CancellationToken);
        var seenByHer = await beatriz.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, hers), TestContext.Current.CancellationToken);
        var herList = await ListAsync(beatriz, f.Tenant.TenantId, string.Empty);
        var unassigned = await owner.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, nobodys), TestContext.Current.CancellationToken);

        Assert.Equal((f.Beatriz, "Beatriz", false), AssignedTo(seenByOwner));
        Assert.Equal((f.Beatriz, "Beatriz", true), AssignedTo(seenByHer));
        Assert.True(herList.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == hers)
            .GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("assignedTo").ValueKind);
    }

    // D-M20: la membresía que ya no está se ve como «Miembro eliminado», nunca null.
    [Fact]
    public async Task AnAssigneeThatNoLongerExistsIsMiembroEliminado()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var conversation = await IngestAsync(f, "CO.A", "w1", 1760000100);
        await AssignAsync(f, conversation, Guid.CreateVersion7());
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var body = await client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, conversation), TestContext.Current.CancellationToken);

        Assert.Equal("Miembro eliminado", body.GetProperty("assignedTo").GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
    }

    [Theory]
    [InlineData("mine")]
    [InlineData("ME")]
    [InlineData("unassigned")]
    public async Task AnUnknownAssignedIsAValidationErrorOnThatKey(string value)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync($"{ConversationsUrl(f.Tenant.TenantId)}?assigned={value}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(("validation.failed", new[] { "assigned" }), await ProblemAsync(response));
    }

    // §6.1.6: la búsqueda encuentra por username y por el nombre del cliente vía customer_id, sin teléfono de por medio.
    [Fact]
    public async Task TheSearchFindsByUsernameAndByTheLinkedCustomersName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var laura = await IngestAsync(f, "CO.L", "w1", 1760000100, username: "laura.p");
        await IngestAsync(f, "CO.P", "w2", 1760000200);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE customers.customers SET name = 'Droguería Central' WHERE id = (SELECT customer_id FROM messaging.conversations WHERE id = @id)", ("id", laura));
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        Assert.Equal([laura], Ids(await ListAsync(client, f.Tenant.TenantId, "?search=aura.p")));
        Assert.Equal([laura], Ids(await ListAsync(client, f.Tenant.TenantId, "?search=drogue")));
    }

    // D-A8 y §6.1.2: isComplete viaja; una conversación vieja sin customer_id se empareja por teléfono como antes.
    [Fact]
    public async Task TheCustomerCarriesIsCompleteAndALegacyConversationFallsBackToThePhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var complete = await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");
        var legacy = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        var fresh = await IngestAsync(f, "CO.N", "w1", 1760000100);
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var legacyBody = await client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, legacy), TestContext.Current.CancellationToken);
        var freshBody = await client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, fresh), TestContext.Current.CancellationToken);

        Assert.Equal((complete, true), (legacyBody.GetProperty("customer").GetProperty("id").GetGuid(), legacyBody.GetProperty("customer").GetProperty("isComplete").GetBoolean()));
        Assert.False(freshBody.GetProperty("customer").GetProperty("isComplete").GetBoolean());
    }

    private static (Guid, string?, bool) AssignedTo(JsonElement conversation)
    {
        var assigned = conversation.GetProperty("assignedTo");
        return (assigned.GetProperty("memberId").GetGuid(), assigned.GetProperty("displayName").GetString(), assigned.GetProperty("isMe").GetBoolean());
    }
}
```

`ProblemAsync` devuelve `(string? Code, string[] ErrorKeys)`; si la igualdad de tuplas con arreglo no compila, compara `Code` y `ErrorKeys` por separado.

En `MessagingValidatorsTests.cs` (unitarias de `ListConversationsValidator`), una teoría: `null`, `"me"`, `"none"`, `"all"` válidos; `"mine"`, `"ME"`, `""` con error en la clave `assigned`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationListApiTests"
```

Esperado (RED): `KeyNotFoundException` sobre `assignedTo`/`mine`/`isComplete`; `assigned=me` devuelve todas.

- [ ] **Step 2: DTOs, `CallerMembership` y puertos**

`MessagingDtos.cs`:

```csharp
/// <summary>Spec 2026-10-10 §5.1 (D-A8): <c>isComplete</c> viaja para que el encabezado del hilo ofrezca «Completar
/// ficha» sin otra llamada.</summary>
public sealed record CustomerRefDto(Guid Id, string Name, bool IsComplete);

/// <summary>Una membresía con nombre: <c>sentBy</c> y los <c>actor</c>/<c>target</c>/<c>previous</c> de un evento (P11).
/// <c>DisplayName</c> nunca es <c>null</c>: la que ya no está viaja como «Miembro eliminado» (D-M20).</summary>
public sealed record MemberRefDto(Guid MemberId, string DisplayName);

/// <summary>Spec 2026-10-10 §5.1 y D-A13. BFF: <c>isMe</c> lo calcula el servidor porque la SPA no conoce su
/// <c>memberId</c> (<c>/auth/me</c> y <c>/authorization/me</c> sólo dan el <c>userId</c>); sin él, la pantalla no sabe
/// si mostrar el compositor o «La tiene X — Tomar».</summary>
public sealed record AssignedToDto(Guid MemberId, string DisplayName, bool IsMe);
```

`ConversationSummary` gana `AssignedToDto? AssignedTo` después de `Customer`. `ConversationCountsDto(int Open, int Unread, int Mine, int Unassigned)` con el `<summary>` «`Mine` y `Unassigned` cuentan sólo abiertas (D-A10): son las pestañas de la cola de trabajo». `SentByDto` se renombra `MemberRefDto` en `MessageDto`, `MessageHitDto` y `MessagingSupport.cs:101`.

`CallerMembership.cs`:

```csharp
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 D-A13 (P20): la membresía activa de quien llama en el tenant, resuelta una vez por request
/// (scoped). La comparten el filtro <c>assigned=me</c>, <c>counts.mine</c> y <c>assignedTo.isMe</c>.</summary>
public sealed class CallerMembership(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
{
    private Guid? _tenantId;
    private Guid? _memberId;

    public async Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantId != tenantId)
        {
            _memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
            _tenantId = tenantId;
        }

        return _memberId;
    }
}
```

Registro en `MessagingInfrastructureExtensions.cs:31`: `services.AddScoped<CallerMembership>();`.

`IMessagingCustomerDirectory.cs` (y la nota del `MatchAsync`: «sólo para conversaciones viejas sin `customer_id`, §6.1.2»):

```csharp
    /// <summary>Spec 2026-10-10 §6.1.2: nombre e isComplete de los clientes de una página, por id.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerRefDto>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>§6.1.6: los clientes cuyo nombre contiene el término (con tope), para <c>customer_id = ANY(@customerIds)</c>.</summary>
    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken);
```

`MessagingCustomerDirectory.cs`: `MatchAsync` arma `new CustomerRefDto(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete)`; y

```csharp
    public async Task<IReadOnlyDictionary<Guid, CustomerRefDto>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? new Dictionary<Guid, CustomerRefDto>()
            : (await whatsApp.FindRefsAsync(tenantId, ids, cancellationToken)).ToDictionary(pair => pair.Key, pair => new CustomerRefDto(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete));

    public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        whatsApp.FindIdsByNameAsync(tenantId, term, NameCap, cancellationToken);
```

- [ ] **Step 3: El armado, la consulta y el handler**

`MessagingSupport.cs` — `ConversationSummaryBuilder`:

```csharp
public sealed class ConversationSummaryBuilder(
    IMessagingConnectionDirectory connections,
    IMessagingCustomerDirectory customers,
    IMessagingMemberNames memberNames,
    CallerMembership caller)
{
    public const string DeletedConnectionName = "Conexión eliminada";

    public async Task<IReadOnlyList<ConversationSummary>> BuildAsync(Guid tenantId, IReadOnlyList<ConversationRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var names = await connections.ListNamesAsync(tenantId, cancellationToken);
        // §6.1.2: el cliente sale de customer_id; el teléfono sólo para las filas viejas que no lo tienen.
        var byId = await customers.FindRefsAsync(
            tenantId, rows.Where(row => row.CustomerId is not null).Select(row => row.CustomerId!.Value).Distinct().ToArray(), cancellationToken);
        var legacyPhones = rows.Where(row => row.CustomerId is null && row.WaId is not null).Select(row => row.WaId!).Distinct(StringComparer.Ordinal).ToArray();
        var byPhone = legacyPhones.Length == 0
            ? new Dictionary<string, CustomerRefDto>()
            : await customers.MatchAsync(tenantId, legacyPhones, cancellationToken);
        var assignees = rows.Where(row => row.AssignedMemberId is not null).Select(row => row.AssignedMemberId!.Value).Distinct().ToArray();
        var assigneeNames = await memberNames.FindAsync(tenantId, assignees, cancellationToken);
        var me = assignees.Length == 0 ? null : await caller.FindAsync(tenantId, cancellationToken);
        return rows.Select(row => ToSummary(row, names, byId, byPhone, assigneeNames, me)).ToArray();
    }

    public static ConversationSummary ToSummary(
        ConversationRow row,
        IReadOnlyDictionary<Guid, string> connectionNames,
        IReadOnlyDictionary<Guid, CustomerRefDto> customersById,
        IReadOnlyDictionary<string, CustomerRefDto> customersByWaId,
        IReadOnlyDictionary<Guid, string> memberNames,
        Guid? callerMemberId)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(connectionNames);
        ArgumentNullException.ThrowIfNull(customersById);
        ArgumentNullException.ThrowIfNull(customersByWaId);
        ArgumentNullException.ThrowIfNull(memberNames);
        var customer = row.CustomerId is { } customerId
            ? customersById.GetValueOrDefault(customerId)
            : row.WaId is { } waId ? customersByWaId.GetValueOrDefault(waId) : null;
        var assignedTo = row.AssignedMemberId is { } assignee
            ? new AssignedToDto(assignee, memberNames.GetValueOrDefault(assignee) ?? MessageMapping.DeletedMemberName, assignee == callerMemberId)
            : null;
        return new(
            row.Id,
            row.ConnectionId,
            connectionNames.GetValueOrDefault(row.ConnectionId) ?? DeletedConnectionName,
            new ContactDto(row.UserId, row.WaId, row.Username, row.ProfileName),
            customer,
            assignedTo,
            row.Status.ToString(),
            row.UnreadCount,
            LastMessageFrom(row),
            row.LastInboundAt?.Add(Conversation.WindowLength),
            row.UpdatedAt,
            row.Version);
    }
```

`IConversationQueries.cs`:

```csharp
/// <summary>Spec 2026-10-10 §5.1: <c>assigned=all|me|none</c>.</summary>
public enum AssignedFilter
{
    All,
    Mine,
    Unassigned,
}

/// <summary>§6.1.6. <c>MemberId</c> = la membresía de quien llama (para <see cref="AssignedFilter.Mine"/>); <c>CustomerIds</c>
/// salen del nombre del cliente; <c>CustomerWaIds</c>, para las conversaciones viejas sin <c>customer_id</c>.</summary>
public sealed record ConversationListFilter(
    ConversationStatus Status,
    AssignedFilter Assigned,
    Guid? MemberId,
    string? Search,
    IReadOnlyCollection<string> CustomerWaIds,
    IReadOnlyCollection<Guid> CustomerIds);
```

y las dos firmas de **Interfaces**.

`ConversationQueries.cs`:

```csharp
    public async Task<(IReadOnlyList<ConversationRow> Items, int Total)> ListAsync(
        Guid tenantId, ConversationListFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = dbContext.Conversations.AsNoTracking().Where(conversation => conversation.TenantId == tenantId && conversation.Status == filter.Status);
        query = filter.Assigned switch
        {
            // P15: sin membresía activa, «mías» no encuentra nada.
            AssignedFilter.Mine => filter.MemberId is { } me
                ? query.Where(conversation => conversation.AssignedMemberId == me)
                : query.Where(_ => false),
            AssignedFilter.Unassigned => query.Where(conversation => conversation.AssignedMemberId == null),
            _ => query,
        };
        if (filter.Search is { } search)
        {
            var pattern = "%" + Escape(search) + "%";
            var digits = ConversationSearchTerms.NumberDigits(search);
            var digitsPattern = digits is null ? null : "%" + digits + "%";
            var phones = filter.CustomerWaIds.ToArray();
            var customerIds = filter.CustomerIds.ToArray();
            query = query.Where(conversation =>
                (conversation.ProfileName != null && EF.Functions.ILike(conversation.ProfileName, pattern, "\\"))
                || (conversation.Username != null && EF.Functions.ILike(conversation.Username, pattern, "\\"))
                || (digitsPattern != null && conversation.WaId != null && EF.Functions.Like(conversation.WaId, digitsPattern))
                || (conversation.CustomerId != null && customerIds.Contains(conversation.CustomerId.Value))
                || (conversation.CustomerId == null && conversation.WaId != null && phones.Contains(conversation.WaId)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(conversation => conversation.LastActivityAt).ThenByDescending(conversation => conversation.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(Projection)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<ConversationCountsDto> CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken)
    {
        var open = await dbContext.Conversations.AsNoTracking()
            .CountAsync(conversation => conversation.TenantId == tenantId && conversation.Status == ConversationStatus.Open, cancellationToken);
        var unread = await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.UnreadCount > 0)
            .SumAsync(conversation => conversation.UnreadCount, cancellationToken);
        // D-A10: sólo abiertas, por los parciales de asignado (IX_conversations_tenant_assignee/unassigned_status_activity).
        var mine = memberId is { } me
            ? await dbContext.Conversations.AsNoTracking().CountAsync(
                conversation => conversation.TenantId == tenantId && conversation.AssignedMemberId == me && conversation.Status == ConversationStatus.Open, cancellationToken)
            : 0;
        var unassigned = await dbContext.Conversations.AsNoTracking().CountAsync(
            conversation => conversation.TenantId == tenantId && conversation.AssignedMemberId == null && conversation.Status == ConversationStatus.Open, cancellationToken);
        return new ConversationCountsDto(open, unread, mine, unassigned);
    }
```

`ListConversations.cs`:

```csharp
public sealed record ListConversationsQuery(Guid TenantId, string? Status, string? Search, int? Page, int? PageSize, string? Assigned = null)
    : IQuery<ConversationPageDto>;
```

En el validador: `RuleFor(query => query.Assigned).Must(value => value is null or "me" or "none" or "all").OverridePropertyName("assigned").WithMessage("assigned debe ser me, none o all.");`. El handler gana `CallerMembership caller` (en lugar de nada: el builder ya lo usa) y:

```csharp
        var assigned = query.Assigned switch { "me" => AssignedFilter.Mine, "none" => AssignedFilter.Unassigned, _ => AssignedFilter.All };
        // P20: la membresía de quien llama, una vez por request; la reusan el filtro, counts.mine e isMe (mismo scoped).
        var me = await caller.FindAsync(query.TenantId, cancellationToken);
        var customerWaIds = search is null ? Array.Empty<string>() : await customers.FindWaIdsByNameAsync(query.TenantId, search, cancellationToken);
        var customerIds = search is null ? Array.Empty<Guid>() : await customers.FindIdsByNameAsync(query.TenantId, search, cancellationToken);
        var filter = new ConversationListFilter(status, assigned, me, search, customerWaIds, customerIds);
        var (rows, total) = await queries.ListAsync(query.TenantId, filter, page, pageSize, cancellationToken);
        var counts = await queries.CountsAsync(query.TenantId, me, cancellationToken);
```

`MessagingEndpoints.cs:130-132`: parámetro `string? assigned` y `new ListConversationsQuery(tenantId, status, search, page, pageSize, assigned)`.

Dobles: `InMemoryConversationQueries` con las firmas nuevas (filtra `Status`, y `Assigned`/`MemberId` sobre `row.AssignedMemberId`); `CountsAsync` calcula los cuatro números. `ConversationReadsTests.APartialLastMessageSnapshotIsNullNotAnError` llama `ToSummary(row, new Dictionary<Guid, string>(), new Dictionary<Guid, CustomerRefDto>(), new Dictionary<string, CustomerRefDto>(), new Dictionary<Guid, string>(), null)`.

- [ ] **Step 4: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~ConversationReadsTests|FullyQualifiedName~MessagingValidatorsTests|FullyQualifiedName~SendMessageHandlerTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~ConversationListApiTests|FullyQualifiedName~ConversationsApiTests|FullyQualifiedName~MessageSearchApiTests|FullyQualifiedName~ConversationLifecycleApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde.

- [ ] **Step 5: Formato y commit**

```
feat(messaging): assignedTo con isMe, filtro assigned, contadores y cliente por customer_id

ConversationSummary gana assignedTo { memberId, displayName, isMe } (spec 2026-10-10 §5.1, D-A13) y
customer.isComplete (D-A8); el cliente sale de customer_id con respaldo por teléfono para lo viejo.
GET /conversations filtra assigned=me|none|all, cuenta mine y unassigned (sólo abiertas) y busca
también por username y por el nombre del cliente vinculado.
```

---

### Task 12: Lectura del hilo y de la búsqueda — eventos y `replyTo`

Spec §5.1 (`Message`: `event`, `replyTo`), §6.1.5, §8.6 («Lectura»), P10, P11, P21. Review Focus RF8 (los eventos salen `Delivered`, `hasMore` los cuenta, la búsqueda no los encuentra).

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessageQueries.cs:7-38` (`MessageRow.ReplyToMessageId`, `ReplyTargetRow`, `FindReplyTargetsAsync`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageQueries.cs:11-38`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:48-82` (`ReplyToDto`, `MessageEventDto`, `MessageDto`, `MessageHitDto`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:81-135` (`MessageMapping`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs:25-58`, `SearchMessages.cs:33-110`, `SendMessage.cs:148-156` (firma nueva del mapeo)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` (doble de `IMessageQueries` si hace falta)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageMappingTests.cs` (nuevo), `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ThreadEventsApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: T5 (`ConversationEventJson.Parse`), T9 (eventos e `reply_to_message_id` en la base), T11 (`MemberRefDto`).
- Produces (T14):

```csharp
public sealed record MessageRow(Guid Id, Guid ConversationId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption,
    string? DetailsJson, MessageStatus Status, int? FailureCode, DateTimeOffset OccurredAt, Guid? SentByMemberId, Guid? ClientId,
    MessageMediaRow? Media, Guid? ReplyToMessageId = null);
public sealed record ReplyTargetRow(Guid Id, Guid ConversationId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption, string? Wamid);
Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> IMessageQueries.FindReplyTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
public sealed record ReplyToDto(Guid Id, string Direction, string Kind, string? Preview);
public sealed record MessageEventDto(string Type, MemberRefDto? Actor, MemberRefDto? Target, MemberRefDto? Previous);
// MessageDto(..., Guid? ClientId, ReplyToDto? ReplyTo, MessageEventDto? Event); MessageHitDto(..., string ConnectionName, ReplyToDto? ReplyTo, MessageEventDto? Event)
internal static MessageDto MessageMapping.ToDto(MessageRow row, Guid tenantId, IReadOnlyDictionary<Guid, string> memberNames, IReadOnlyDictionary<Guid, ReplyTargetRow> replyTargets);
internal static IReadOnlyCollection<Guid> MessageMapping.MemberIdsOf(IEnumerable<MessageRow> rows);   // sentBy + actor/target/previous
internal static ReplyToDto ToReplyTo(ReplyTargetRow target);
```

- [ ] **Step 1: Las unitarias del mapeo (RED)**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageMappingTests.cs`:

```csharp
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-10 §5.1: un evento sale System/Event/Delivered con <c>event</c> lleno y lo demás en null; sus
/// miembros con nombre o «Miembro eliminado»; <c>replyTo.preview</c> es el texto o la leyenda recortado a 200, y
/// <c>replyTo</c> es null si QEP no tiene el citado.</summary>
public sealed class MessageMappingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid Andres = Guid.CreateVersion7();
    private static readonly Guid Gone = Guid.CreateVersion7();
    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string> { [Andres] = "Andrés" };
    private static readonly IReadOnlyDictionary<Guid, ReplyTargetRow> NoTargets = new Dictionary<Guid, ReplyTargetRow>();

    private static MessageRow Row(MessageDirection direction, MessageKind kind, string? text = null, string? details = null, Guid? replyTo = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), direction, kind, text, null, details, MessageStatus.Delivered, null,
            DateTimeOffset.UnixEpoch, null, null, null, replyTo);

    [Fact]
    public void AnEventCarriesItsMembersAndNothingElse()
    {
        var details = ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Transferred, Actor: Andres, Target: Gone));

        var dto = MessageMapping.ToDto(Row(MessageDirection.System, MessageKind.Event, details: details), TenantId, Names, NoTargets);

        Assert.Equal(("System", "Event", "Delivered"), (dto.Direction, dto.Kind, dto.Status));
        Assert.Equal(new MessageEventDto("Transferred", new MemberRefDto(Andres, "Andrés"), new MemberRefDto(Gone, MessageMapping.DeletedMemberName), null), dto.Event);
        Assert.Null(dto.Text);
        Assert.Null(dto.Media);
        Assert.Null(dto.Location);
        Assert.Null(dto.SentBy);
        Assert.Null(dto.ReplyTo);
    }

    [Fact]
    public void AMessageThatIsNotAnEventHasNoEvent() =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Text, "hola"), TenantId, Names, NoTargets).Event);

    [Fact]
    public void TheMemberIdsOfAPageIncludeEventMembers()
    {
        var details = ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Taken, Actor: Andres, Previous: Gone));

        var ids = MessageMapping.MemberIdsOf([Row(MessageDirection.System, MessageKind.Event, details: details)]);

        Assert.Equal(new[] { Andres, Gone }.Order(), ids.Order());
    }

    [Fact]
    public void TheReplyPreviewIsTheTextOrCaptionCutTo200()
    {
        var quoted = Guid.CreateVersion7();
        var targets = new Dictionary<Guid, ReplyTargetRow>
        {
            [quoted] = new(quoted, Guid.CreateVersion7(), MessageDirection.Outbound, MessageKind.Image, null, new string('a', 300), "wamid.q"),
        };

        var dto = MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Text, "sí", replyTo: quoted), TenantId, Names, targets);

        Assert.Equal(new ReplyToDto(quoted, "Outbound", "Image", new string('a', 200)), dto.ReplyTo);
    }

    [Fact]
    public void AReplyToAMessageQepDoesNotHaveIsNull() =>
        Assert.Null(MessageMapping.ToDto(Row(MessageDirection.Inbound, MessageKind.Text, "sí", replyTo: Guid.CreateVersion7()), TenantId, Names, NoTargets).ReplyTo);
}
```

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageMappingTests"
```

Esperado: `error CS0246: The type or namespace name 'ReplyTargetRow' could not be found`.

- [ ] **Step 2: Filas, consulta y mapeo**

`IMessageQueries.cs`: `MessageRow` gana `Guid? ReplyToMessageId = null` al final; nuevos:

```csharp
/// <summary>Spec 2026-10-10 §8.6: lo justo del mensaje citado para la burbuja y para validar un <c>replyTo</c> saliente.</summary>
public sealed record ReplyTargetRow(Guid Id, Guid ConversationId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption, string? Wamid);
```

y en la interfaz:

```csharp
    /// <summary>§8.6 (P10): los citados de una página en una consulta, por tenant y PK. Los que no están no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
```

`MessageQueries.cs`: `RowProjection` agrega `message.ReplyToMessageId` al final (la búsqueda la reusa, `MessageSearch.cs:100`), y:

```csharp
    public async Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ReplyTargetRow>();
        }

        var wanted = ids.ToArray();
        return await dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && wanted.Contains(message.Id))
            .Select(message => new ReplyTargetRow(message.Id, message.ConversationId, message.Direction, message.Kind, message.Text, message.Caption, message.Wamid))
            .ToDictionaryAsync(target => target.Id, cancellationToken);
    }
```

`MessagingDtos.cs`:

```csharp
/// <summary>Spec 2026-10-10 §5.1: lo que la burbuja dibuja del citado. Sin nombre de quien lo envió: la pantalla dice
/// «Cliente» o «Equipo» por <c>direction</c> (P21).</summary>
public sealed record ReplyToDto(Guid Id, string Direction, string Kind, string? Preview);

/// <summary>§5.1 y §6.1.5: <c>actor</c> es null cuando lo hizo el sistema.</summary>
public sealed record MessageEventDto(string Type, MemberRefDto? Actor, MemberRefDto? Target, MemberRefDto? Previous);
```

`MessageDto` y `MessageHitDto` ganan `ReplyToDto? ReplyTo, MessageEventDto? Event` al final.

`MessagingSupport.cs` — `MessageMapping`:

```csharp
    public const int ReplyPreviewMaxLength = 200;

    public static MessageDto ToDto(
        MessageRow row, Guid tenantId, IReadOnlyDictionary<Guid, string> memberNames, IReadOnlyDictionary<Guid, ReplyTargetRow> replyTargets)
    {
        ArgumentNullException.ThrowIfNull(row);
        var conversationEvent = row.Kind == MessageKind.Event ? ConversationEventJson.Parse(row.DetailsJson) : null;
        return new(
            row.Id,
            row.Direction.ToString(),
            row.Kind.ToString(),
            row.Text,
            row.Media is null ? null : new MediaDto(MediaUrl(tenantId, row.Id), row.Media.MimeType, row.Media.FileName, row.Caption),
            row.Kind == MessageKind.Location ? LocationFrom(row.DetailsJson) : null,
            row.Status.ToString(),
            MessageFailureReasons.For(row.FailureCode),
            row.OccurredAt,
            Member(row.SentByMemberId, memberNames),
            row.ClientId,
            row.ReplyToMessageId is { } quoted && replyTargets.TryGetValue(quoted, out var target) ? ToReplyTo(target) : null,
            conversationEvent is null
                ? null
                : new MessageEventDto(
                    conversationEvent.Type.ToString(),
                    Member(conversationEvent.Actor, memberNames),
                    Member(conversationEvent.Target, memberNames),
                    Member(conversationEvent.Previous, memberNames)));
    }

    public static ReplyToDto ToReplyTo(ReplyTargetRow target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = target.Text ?? target.Caption;
        return new ReplyToDto(
            target.Id,
            target.Direction.ToString(),
            target.Kind.ToString(),
            source is null ? null : source.Length <= ReplyPreviewMaxLength ? source : source[..ReplyPreviewMaxLength]);
    }

    /// <summary>Los ids de membresía que una página necesita con nombre: <c>sentBy</c> y los de cada evento.</summary>
    public static IReadOnlyCollection<Guid> MemberIdsOf(IEnumerable<MessageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ids = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (row.SentByMemberId is { } sender)
            {
                ids.Add(sender);
            }

            if (row.Kind == MessageKind.Event && ConversationEventJson.Parse(row.DetailsJson) is { } value)
            {
                foreach (var id in new[] { value.Actor, value.Target, value.Previous })
                {
                    if (id is { } member)
                    {
                        ids.Add(member);
                    }
                }
            }
        }

        return ids;
    }

    private static MemberRefDto? Member(Guid? memberId, IReadOnlyDictionary<Guid, string> memberNames) =>
        memberId is { } id ? new MemberRefDto(id, memberNames.GetValueOrDefault(id) ?? DeletedMemberName) : null;
```

(El `new[] { … }` del bucle es un arreglo de valores variables, no constantes: CA1861 no aplica.)

`ListMessages.cs:55-57`:

```csharp
        var names = await memberNames.FindAsync(query.TenantId, MessageMapping.MemberIdsOf(page), cancellationToken);
        var targets = await messages.FindReplyTargetsAsync(
            query.TenantId, page.Where(row => row.ReplyToMessageId is not null).Select(row => row.ReplyToMessageId!.Value).Distinct().ToArray(), cancellationToken);
        return new MessagePageDto(page.Select(row => MessageMapping.ToDto(row, query.TenantId, names, targets)).ToArray(), hasMore);
```

`SearchMessages.cs`: el handler gana `IMessageQueries messages`; `names` con `MessageMapping.MemberIdsOf(page)`; `targets` igual que arriba; `MessageMapping.ToDto(row, query.TenantId, names, targets)`; el `MessageHitDto` pasa `message.ReplyTo, message.Event` al final.

`SendMessage.cs:148-156` (`ToDtoAsync`): `MessageMapping.ToDto(row, command.TenantId, names, new Dictionary<Guid, ReplyTargetRow>())` (la T14 le pasa el citado).

- [ ] **Step 3: La de integración (RED → GREEN)**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ThreadEventsApiTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1, §8.6, §8.7 (RF8): el evento aparece en el hilo antes del mensaje que lo causó, con su
/// forma; hasMore lo cuenta; la búsqueda del historial no lo encuentra; replyTo en el hilo y en la búsqueda.</summary>
public sealed class ThreadEventsApiTests
{
    [Fact]
    public async Task EventsAndQuotesHaveTheirShapeInTheThreadAndTheSearch()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola pedido"));
        await DrainDeliveriesAsync(factory);
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var outbound = await SeedOutboundAsync(connectionString, conversationId, tenant.TenantId, connectionId, "wamid.ours", occurredAtUnix: 1760000050);
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", 1760000100, "ese pedido", quotedWamid: "wamid.ours"));
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", 1760000200, "y otro pedido", quotedWamid: "wamid.nadie"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var thread = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), TestContext.Current.CancellationToken);
        var oldest = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=4", TestContext.Current.CancellationToken);
        var search = await client.GetFromJsonAsync<JsonElement>($"{SearchUrl(tenant.TenantId)}?q=pedido", TestContext.Current.CancellationToken);

        var items = thread.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        var created = items[0];
        Assert.Equal(("System", "Event", "Delivered", "CustomerCreated"),
            (created.GetProperty("direction").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("status").GetString(),
             created.GetProperty("event").GetProperty("type").GetString()));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("event").GetProperty("actor").ValueKind);
        foreach (var field in new[] { "text", "media", "location", "failureReason", "sentBy", "clientId", "replyTo" })
        {
            Assert.Equal(JsonValueKind.Null, created.GetProperty(field).ValueKind);
        }

        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("event").ValueKind);
        var quoted = items[3].GetProperty("replyTo");
        Assert.Equal((outbound, "Outbound", "Text", "respuesta"),
            (quoted.GetProperty("id").GetGuid(), quoted.GetProperty("direction").GetString(), quoted.GetProperty("kind").GetString(), quoted.GetProperty("preview").GetString()));
        Assert.Equal(JsonValueKind.Null, items[4].GetProperty("replyTo").ValueKind);
        Assert.True(oldest.GetProperty("hasMore").GetBoolean());
        var hits = search.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, hits.Length);
        Assert.All(hits, hit => Assert.Equal("Inbound", hit.GetProperty("direction").GetString()));
        Assert.Equal(outbound, hits.Single(hit => hit.GetProperty("text").GetString() == "ese pedido").GetProperty("replyTo").GetProperty("id").GetGuid());
    }
}
```

(`SeedOutboundAsync` escribe el texto `'respuesta'`, `MessagingApiHarness.cs:434`.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~MessageMappingTests|FullyQualifiedName~SendMessageHandlerTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~ThreadEventsApiTests|FullyQualifiedName~ThreadApiTests|FullyQualifiedName~MessageSearchApiTests|FullyQualifiedName~SendMessageApiTests"
```

Esperado: verde.

- [ ] **Step 4: Formato y commit**

```
feat(messaging): eventos y respuestas citadas en el hilo y en la búsqueda

Message gana event (System/Event, actor/target/previous con nombre) y replyTo (dirección, tipo y
preview de 200), resueltos por página en una consulta (spec 2026-10-10 §5.1, §8.6). SentByDto pasa a
MemberRefDto; la forma JSON no cambia.
```

---

### Task 13: Tomar, transferir y liberar (y el evento de resolver/reabrir)

Spec §5.1 (endpoints nuevos), §6.1.3, §8.4, §9.1–§9.2, D-A2, P8, P9. Review Focus RF4 (carrera de dos `take`) y RF5 (transferir a quien no puede responder). Por el agregado con `If-Match`, como resolver/reabrir; evento y auditoría en la misma transacción.

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:110-136` (`Take`, `TransferTo`, `Release`)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IConversationEvents.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationEvents.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAssignment.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs:10-15` (acciones)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs:65-116` (evento `Resolved`/`Reopened`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:55-72` (tres rutas), `:160-195` (handlers y `TransferConversationRequest`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs:27` (`IConversationEvents`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:536-539` (tres handlers)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`, `MessagingValidatorsTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AssignmentApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: T5 (`ConversationEvent`, `ConversationEventJson`, códigos), T6 (`AssignedMemberId`, `AssignedAt`), T8 (`IMessagingAssignees.CanReplyAsync`, `SeedMemberAsync`), T11 (`CallerMembership`, `ConversationSummaryBuilder`).
- Produces (T14):

```csharp
public ConversationEvent? Conversation.Take(Guid memberId, DateTimeOffset now);
public ConversationEvent? Conversation.TransferTo(Guid actorMemberId, Guid targetMemberId, DateTimeOffset now);
public ConversationEvent? Conversation.Release(Guid actorMemberId, DateTimeOffset now);
public interface IConversationEvents { void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt); }
public sealed record TakeConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;
public sealed record TransferConversationCommand(Guid TenantId, Guid ConversationId, Guid? MemberId, long ExpectedVersion) : ICommand<ConversationSummary>;
public sealed record ReleaseConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;
MessagingAuditActions.Taken|Transferred|Released|AutoTaken
public sealed record TransferConversationRequest(Guid? MemberId);
```

- [ ] **Step 1: Unitarias del agregado y del validador (RED)**

`ConversationTests.cs`:

```csharp
    private static readonly Guid Andres = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Beatriz = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    [Fact]
    public void TakingAssignsBumpsTheVersionAndReturnsTheEventWithThePrevious()
    {
        var conversation = Open();

        var first = conversation.Take(Andres, Now.AddMinutes(1));
        var second = conversation.Take(Beatriz, Now.AddMinutes(2));

        Assert.Equal(new ConversationEvent(ConversationEventType.Taken, Actor: Andres), first);
        Assert.Equal(new ConversationEvent(ConversationEventType.Taken, Actor: Beatriz, Previous: Andres), second);
        Assert.Equal((Beatriz, Now.AddMinutes(2), 3L), (conversation.AssignedMemberId!.Value, conversation.AssignedAt!.Value, conversation.Version));
    }

    // D-A2: tomar la propia o liberar una sin asignar no cambia nada, ni la versión.
    [Fact]
    public void NoOpsReturnNoEventAndKeepTheVersion()
    {
        var conversation = Open();

        Assert.Null(conversation.Release(Andres, Now));
        conversation.Take(Andres, Now);
        Assert.Null(conversation.Take(Andres, Now.AddMinutes(1)));
        Assert.Null(conversation.TransferTo(Beatriz, Andres, Now.AddMinutes(1)));   // P8: ya es de Andrés
        Assert.Equal(2, conversation.Version);
    }

    [Fact]
    public void TransferringToYourselfIsATakeAndTransferringCarriesTargetAndPrevious()
    {
        var conversation = Open();
        conversation.Take(Andres, Now);

        var toSelf = conversation.TransferTo(Beatriz, Beatriz, Now.AddMinutes(1));
        var back = conversation.TransferTo(Beatriz, Andres, Now.AddMinutes(2));

        Assert.Equal(new ConversationEvent(ConversationEventType.Taken, Actor: Beatriz, Previous: Andres), toSelf);
        Assert.Equal(new ConversationEvent(ConversationEventType.Transferred, Actor: Beatriz, Target: Andres, Previous: Beatriz), back);
    }

    [Fact]
    public void ReleasingClearsTheAssignmentAndAResolvedOneCanStillBeTaken()
    {
        var conversation = Open();
        conversation.Resolve(Now);
        conversation.Take(Andres, Now.AddMinutes(1));

        var released = conversation.Release(Beatriz, Now.AddMinutes(2));

        Assert.Equal(new ConversationEvent(ConversationEventType.Released, Actor: Beatriz, Previous: Andres), released);
        Assert.Null(conversation.AssignedMemberId);
        Assert.Null(conversation.AssignedAt);
        Assert.Equal(ConversationStatus.Resolved, conversation.Status);
    }
```

`MessagingValidatorsTests.cs`: `TransferConversationValidator` con `MemberId = null` y `Guid.Empty` → error en la clave `memberId`; con un GUID → válido.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~MessagingValidatorsTests"
```

Esperado: `error CS1061: 'Conversation' does not contain a definition for 'Take'`.

- [ ] **Step 2: El agregado**

`Conversation.cs`, junto a `Resolve`/`Reopen` (el `<summary>` de la clase suma «y asigna (spec 2026-10-10 §6.1.3)»):

```csharp
    /// <summary>Spec 2026-10-10 §6.1.3: quien llama la toma, la tenga quien la tenga. Si ya era suya, nada (D-A2).</summary>
    public ConversationEvent? Take(Guid memberId, DateTimeOffset now)
    {
        if (AssignedMemberId == memberId)
        {
            return null;
        }

        var previous = AssignedMemberId;
        Assign(memberId, now);
        return new ConversationEvent(ConversationEventType.Taken, Actor: memberId, Previous: previous);
    }

    /// <summary>§8.4: transferirse a uno mismo es tomar; transferir a quien ya la tiene, nada (P8).</summary>
    public ConversationEvent? TransferTo(Guid actorMemberId, Guid targetMemberId, DateTimeOffset now)
    {
        if (targetMemberId == actorMemberId)
        {
            return Take(actorMemberId, now);
        }

        if (AssignedMemberId == targetMemberId)
        {
            return null;
        }

        var previous = AssignedMemberId;
        Assign(targetMemberId, now);
        return new ConversationEvent(ConversationEventType.Transferred, Actor: actorMemberId, Target: targetMemberId, Previous: previous);
    }

    /// <summary>§6.1.3: sin asignar ya, nada (D-A2).</summary>
    public ConversationEvent? Release(Guid actorMemberId, DateTimeOffset now)
    {
        if (AssignedMemberId is not { } previous)
        {
            return null;
        }

        AssignedMemberId = null;
        AssignedAt = null;
        Touch(now);
        return new ConversationEvent(ConversationEventType.Released, Actor: actorMemberId, Previous: previous);
    }

    private void Assign(Guid memberId, DateTimeOffset now)
    {
        AssignedMemberId = memberId;
        AssignedAt = now;
        Touch(now);
    }
```

- [ ] **Step 3: La de integración (RED)**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AssignmentApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.4 y §9: take/transfer/release con If-Match (428/412), sus eventos y auditoría, la
/// carrera de dos take (RF4), transferir a quien no puede responder (RF5), resolver no libera.</summary>
public sealed class AssignmentApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid Owner, Guid Beatriz, Guid BeatrizUser, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var (beatriz, beatrizUser) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        return new Fixture(factory, connectionString, tenant, conversationId, await OwnerMembershipIdAsync(connectionString, tenant), beatriz, beatrizUser,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static string ActionUrl(Fixture f, string action) => $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/{action}";

    private static Task<long> VersionAsync(Fixture f) => ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

    private static Task<HttpResponseMessage> ActAsync(Fixture f, HttpClient client, string action, long version, object? body = null) =>
        SendAsync(client, HttpMethod.Post, ActionUrl(f, action), body, $"\"{version}\"");

    private static Task<long> AuditAsync(Fixture f, string action) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = @a", ("a", action));

    [Fact]
    public async Task TakingAnswersTheSummaryAndTakingSomeoneElsesRecordsThePrevious()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var beatriz = CreateClient(f.Factory, f.BeatrizUser, f.Tenant.TenantId, ManagePermissions);

        var mine = await ActAsync(f, f.Client, "take", await VersionAsync(f));
        var hers = await ActAsync(f, beatriz, "take", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var body = await hers.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((f.Beatriz, true), (body.GetProperty("assignedTo").GetProperty("memberId").GetGuid(), body.GetProperty("assignedTo").GetProperty("isMe").GetBoolean()));
        Assert.Equal($"Taken|{f.Beatriz}|{f.Owner}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'type' || '|' || (details->>'actor') || '|' || (details->>'previous') FROM messaging.messages WHERE details->>'type' = 'Taken' AND details ? 'previous'"));
        Assert.Equal(2L, await AuditAsync(f, "messaging.conversation.taken"));
    }

    // RF4 y §9.1: las dos mandan la misma versión; una commitea y la otra recibe 412.
    [Fact]
    public async Task TwoTakesWithTheSameVersionAnswerOne200AndOne412()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var beatriz = CreateClient(f.Factory, f.BeatrizUser, f.Tenant.TenantId, ManagePermissions);
        var version = await VersionAsync(f);

        var responses = await Task.WhenAll(ActAsync(f, f.Client, "take", version), ActAsync(f, beatriz, "take", version));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], responses.Select(response => response.StatusCode).Order());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'Taken'"));
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var missing = await SendAsync(f.Client, HttpMethod.Post, ActionUrl(f, "take"));
        var stale = await ActAsync(f, f.Client, "release", await VersionAsync(f) - 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task TransferringToAnAdvisorAssignsAndRecordsTheEventAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ActAsync(f, f.Client, "transfer", await VersionAsync(f), new { memberId = f.Beatriz });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(f.Beatriz, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal($"{f.Owner}|{f.Beatriz}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT (details->>'actor') || '|' || (details->>'target') FROM messaging.messages WHERE details->>'type' = 'Transferred'"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.transferred"));
    }

    // RF5 y §11: sin manage, de otro tenant o removida responden lo mismo y no confirman nada.
    [Fact]
    public async Task TransferringToAMemberThatCannotReplyIs422WithoutConfirmingWhy()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await RegisterTenantAsync(f.Factory);
        var (billing, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Carlos", "billing");
        var (foreign, _) = await SeedMemberAsync(f.ConnectionString, other.TenantId, "Elena", "advisor");
        var (removed, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Diana", "advisor", state: "Removed");
        var version = await VersionAsync(f);

        foreach (var memberId in new[] { billing, foreign, removed, Guid.CreateVersion7() })
        {
            var response = await ActAsync(f, f.Client, "transfer", version, new { memberId });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("messaging.conversation.assignee_cannot_reply", (await ProblemAsync(response)).Code);
        }

        Assert.Equal(version, await VersionAsync(f));
    }

    [Fact]
    public async Task TransferWithoutMemberIdIsAValidationErrorOnMemberId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ActAsync(f, f.Client, "transfer", await VersionAsync(f), new { memberId = (Guid?)null });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "memberId"), (problem.Code, Assert.Single(problem.ErrorKeys)));
    }

    [Fact]
    public async Task ReleasingClearsTheAssigneeAndReleasingAgainIs200WithoutABump()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "take", await VersionAsync(f));

        var released = await ActAsync(f, f.Client, "release", await VersionAsync(f));
        var version = await VersionAsync(f);
        var again = await ActAsync(f, f.Client, "release", version);

        Assert.Equal(JsonValueKind.Null, (await released.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("assignedTo").ValueKind);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(version, await VersionAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.released"));
    }

    // Spec 2026-10-10 §2, decisión 3: resolver no libera; el evento lleva quién resolvió (P9).
    [Fact]
    public async Task ResolvingKeepsTheAssigneeAndRecordsWhoResolved()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "take", await VersionAsync(f));

        var resolved = await ActAsync(f, f.Client, "resolve", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(f.Owner, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal(f.Owner.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'actor' FROM messaging.messages WHERE details->>'type' = 'Resolved'"));
    }

    [Fact]
    public async Task ACallerWithoutAnActiveMembershipIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var stranger = CreateClient(f.Factory, Guid.CreateVersion7(), f.Tenant.TenantId, ManagePermissions);

        var response = await ActAsync(f, stranger, "take", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(response)).Code);
    }
}
```

`Removed` es el estado de una membresía quitada (`MembershipState.cs:8`).

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~AssignmentApiTests"
```

Esperado (RED): `404 Not Found` en `take`/`transfer`/`release`.

- [ ] **Step 4: Eventos por EF, comandos, handlers y endpoints**

`IConversationEvents.cs`:

```csharp
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §8.7: un evento escrito por un handler; se commitea con el agregado en el mismo
/// <c>SaveChanges</c>. No toca la conversación (ni contadores ni foto).</summary>
public interface IConversationEvents
{
    void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt);
}
```

`src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationEvents.cs`:

```csharp
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La misma fila que <c>ConversationEventRows</c> (ingesta, SQL), por EF para los handlers.</summary>
internal sealed class ConversationEvents(MessagingDbContext dbContext) : IConversationEvents
{
    public void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt) =>
        dbContext.Messages.Add(new MessageRecord
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            OccurredAt = occurredAt.ToUniversalTime(),
            Direction = MessageDirection.System,
            Kind = MessageKind.Event,
            Status = MessageStatus.Delivered,
            Details = ConversationEventJson.Serialize(value),
            CreatedAt = occurredAt.ToUniversalTime(),
        });
}
```

Registro: `services.AddScoped<IConversationEvents, ConversationEvents>();`.

`IMessagingAuditRecorder.cs`:

```csharp
    public const string Taken = "messaging.conversation.taken";
    public const string Transferred = "messaging.conversation.transferred";
    public const string Released = "messaging.conversation.released";
    public const string AutoTaken = "messaging.conversation.auto_taken";
```

`ConversationAssignment.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record TakeConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record TransferConversationCommand(Guid TenantId, Guid ConversationId, Guid? MemberId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ReleaseConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed class TransferConversationValidator : AbstractValidator<TransferConversationCommand>
{
    public TransferConversationValidator() =>
        RuleFor(command => command.MemberId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("memberId")
            .WithMessage("Elige a quién transferir la conversación.");
}

/// <summary>Spec 2026-10-10 §8.4, el orden de los tres: tenant, permiso y módulo → conversación del tenant (404) →
/// membresía activa de quien llama (403) → [transfer: puede responder (422)] → If-Match (412) → agregado → evento y
/// auditoría en la misma transacción → 200 ConversationSummary. Sin cambio (D-A2, P8): 200 sin subir la versión.</summary>
public sealed class ConversationAssignmentSteps(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IConversationEvents events,
    ConversationSummaryBuilder summaries,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
{
    public async Task<(Conversation Conversation, Guid Member)> LoadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(conversationId);
        var member = await caller.FindAsync(tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        return (conversation, member);
    }

    public DateTimeOffset Now => clock.UtcNow;

    public async Task<ConversationSummary> CommitAsync(
        Guid tenantId, Conversation conversation, ConversationEvent? change, string auditAction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (change is not null)
        {
            events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now);
            audit.Record(tenantId, executionContext.SubjectId, auditAction, conversation.Id, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await summaries.BuildAsync(tenantId, [(await queries.FindAsync(tenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

public sealed class TakeConversationHandler(ConversationAssignmentSteps steps) : ICommandHandler<TakeConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(TakeConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        return await steps.CommitAsync(command.TenantId, conversation, conversation.Take(member, now), MessagingAuditActions.Taken, now, cancellationToken);
    }
}

public sealed class TransferConversationHandler(ConversationAssignmentSteps steps, IMessagingAssignees assignees, IValidator<TransferConversationCommand> validator)
    : ICommandHandler<TransferConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(TransferConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var target = command.MemberId!.Value;
        // §11: una membresía de otro tenant responde igual que una inexistente o sin manage; no confirma nada.
        if (!await assignees.CanReplyAsync(command.TenantId, target, cancellationToken))
        {
            throw new MessagingDomainException(MessagingErrorCodes.AssigneeCannotReply, "That member cannot answer conversations in this tenant.");
        }

        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        var change = conversation.TransferTo(member, target, now);
        var action = change?.Type == ConversationEventType.Taken ? MessagingAuditActions.Taken : MessagingAuditActions.Transferred;
        return await steps.CommitAsync(command.TenantId, conversation, change, action, now, cancellationToken);
    }
}

public sealed class ReleaseConversationHandler(ConversationAssignmentSteps steps) : ICommandHandler<ReleaseConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ReleaseConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        return await steps.CommitAsync(command.TenantId, conversation, conversation.Release(member, now), MessagingAuditActions.Released, now, cancellationToken);
    }
}
```

`ConversationLifecycle.cs` — `ResolveConversationHandler` y `ReopenConversationHandler` ganan `IConversationEvents events` y `CallerMembership caller`; después de `conversation.Resolve(now);` (y `Reopen`):

```csharp
        // Spec 2026-10-10 §6.1.5 (P9): el evento lleva quién; sin membresía activa, actor null (sin 403 nuevo).
        var actor = await caller.FindAsync(command.TenantId, cancellationToken);
        events.Record(command.TenantId, conversation.ConnectionId, conversation.Id, new ConversationEvent(ConversationEventType.Resolved, Actor: actor), now);
```

(`ConversationEventType.Reopened` en el de reabrir.)

`MessagingEndpoints.cs`, junto a resolve/reopen:

```csharp
        // Spec 2026-10-10 §5.1 y §8.4: asignación con If-Match (428 sin él, 412 con versión vieja); 200 ConversationSummary.
        group.MapPost("/conversations/{conversationId:guid}/take", TakeAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/transfer", TransferAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<TransferConversationRequest>("application/json")
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/release", ReleaseAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
```

```csharp
    private static async Task<IResult> TakeAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TakeConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> TransferAsync(
        Guid tenantId, Guid conversationId, TransferConversationRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TransferConversationCommand(tenantId, conversationId, request.MemberId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ReleaseAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReleaseConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));
```

y al final del archivo: `/// <summary>Spec 2026-10-10 §5.1.</summary> public sealed record TransferConversationRequest(Guid? MemberId);`.

`QepServiceCollectionExtensions.cs`, con los de resolve/reopen:

```csharp
        // Messaging (spec 2026-10-10 §8.4): tomar, transferir y liberar. Cada handler a mano: sin registro, 500.
        services.AddScoped<ConversationAssignmentSteps>();
        services.AddScoped<ICommandHandler<TakeConversationCommand, ConversationSummary>, TakeConversationHandler>();
        services.AddScoped<ICommandHandler<TransferConversationCommand, ConversationSummary>, TransferConversationHandler>();
        services.AddScoped<ICommandHandler<ReleaseConversationCommand, ConversationSummary>, ReleaseConversationHandler>();
```

(`TransferConversationValidator` lo registra el `AddValidatorsFromAssemblyContaining<ListConversationsValidator>()` existente.)

- [ ] **Step 5: GREEN**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~MessagingValidatorsTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~AssignmentApiTests|FullyQualifiedName~ConversationLifecycleApiTests|FullyQualifiedName~MessagingPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde. Si `TwoTakesWithTheSameVersionAnswerOne200AndOne412` da dos 200, el `If-Match` no llegó al agregado (revisa `ConversationConcurrency.EnsureVersion` antes de `Take`) o `version` no es token de concurrencia en EF (`MessagingDbContext.cs:75`).

- [ ] **Step 6: Formato y commit**

```
feat(messaging): tomar, transferir y liberar una conversación

POST take|transfer|release con If-Match (spec 2026-10-10 §5.1, §8.4): por el agregado, evento y
auditoría en la misma transacción, 200 sin cambios cuando no hay nada que hacer (D-A2). Transferir
exige una membresía activa del tenant con manage (422 assignee_cannot_reply). Resolver y reabrir
dejan su evento con quién lo hizo.
```

---

### Task 14: Enviar con dueño — `assigned_to_other`, autoasignación atómica y respuesta citada

Spec §5.1 (`POST …/messages`), §8.5, §8.6 («Saliente»), §9.3, §9.7. Review Focus RF6 (responder la de otro) y RF7 (autoasignación sin 412 contra la ingesta). Invariantes que no se tocan: idempotencia por `clientId` antes de todo lo demás, un solo candado largo sobre la fila del mensaje, y `CancellationToken.None` en todo cierre del reclamo después de que Meta responde.

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs:8-157`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IOutboundMessages.cs:7-40` (`OutboundDraft`, `ExistingOutbound`, `FindByClientIdAsync`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs:15-113`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs:6-20` (`TryAutoAssignAsync`, `AutoAssignOutcome`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationRepository.cs:7-32`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:146-152,194-195` (`replyTo`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` (dobles y constructor), `SendMessageHandlerTests.cs`, `SendMessageValidatorTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendAssignmentApiTests.cs` (nuevo), `ReplyToApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: T6 (`SendTarget`, `contextWamid`), T9 (`ConversationEventRows.InsertAsync`), T11 (`CallerMembership`), T12 (`ReplyTargetRow`, `IMessageQueries.FindReplyTargetsAsync`, `MessageMapping.ToReplyTo`), T13 (`MessagingAuditActions.AutoTaken`).
- Produces:

```csharp
public sealed record SendMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, string? Text, Guid? ReplyTo = null) : ICommand<MessageDto>;
public sealed record SendMessageRequest(Guid? ClientId, string? Text, Guid? ReplyTo = null);
public sealed record OutboundDraft(Guid MessageId, Guid ConversationId, Guid TenantId, Guid ConnectionId, Guid ClientId, string Text, Guid SentByMemberId,
    DateTimeOffset Now, Guid? ReplyToMessageId = null, string? ReplyToWamid = null);
public sealed record ExistingOutbound(Guid Id, MessageStatus Status, DateTimeOffset OccurredAt, Guid? SentByMemberId, string Text, Guid? ReplyToMessageId = null);
Task<ExistingOutbound?> IOutboundMessages.FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken);
public enum AutoAssignOutcome { Assigned, AlreadyMine, AssignedToOther }
Task<AutoAssignOutcome> IConversationRepository.TryAutoAssignAsync(Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken);
```

- [ ] **Step 1: Unitarias del orden de los chequeos (RED)**

`MessagingTestBed.cs`:
- `OpenConversation(int? lastInboundHoursAgo, bool resolved = false, Guid? assignedTo = null)` pone `AssignedMemberId: assignedTo` en la fila.
- Dobles nuevos:

```csharp
internal sealed class FakeConversationRepository : IConversationRepository
{
    public AutoAssignOutcome NextAutoAssign { get; set; } = AutoAssignOutcome.Assigned;

    public List<Guid> AutoAssigned { get; } = [];

    public Task<AutoAssignOutcome> TryAutoAssignAsync(Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AutoAssigned.Add(conversationId);
        return Task.FromResult(NextAutoAssign);
    }

    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ReadReceiptTarget?> MarkReadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> ExistsAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeMessageQueries : IMessageQueries
{
    public Dictionary<Guid, ReplyTargetRow> Targets { get; } = [];

    public Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ReplyTargetRow>>(Targets.Where(pair => ids.Contains(pair.Key)).ToDictionary());

    public Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
}
```

- `FakeOutboundMessages` gana `public ExistingOutbound? Committed { get; set; }` y `FindByClientIdAsync` que lo devuelve.
- `MessagingTestBed` expone `public FakeConversationRepository Repository { get; } = new();` y `public FakeMessageQueries Messages { get; } = new();`; `SendHandler` arma `new SendMessageHandler(Conversations, Repository, Messages, Outbound, Meta, Connections, new FakeMemberNames(MemberId), membership, TenantModules, executionContext, new FakeClock(Now), new SendMessageValidator())` (orden de la firma nueva, Step 3).

Pruebas nuevas en `SendMessageHandlerTests.cs`:

```csharp
    // RF6: asignada a otra persona → 422 sin reclamo y sin Meta.
    [Fact]
    public async Task AnswerToSomeoneElsesConversationIs422WithoutClaimNorMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Meta.Sends);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    [Fact]
    public async Task AnUnassignedConversationIsTakenBeforeTheClaim()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct);

        Assert.Equal([conversation.Id], bed.Repository.AutoAssigned);
        Assert.Single(bed.Meta.Sends);
    }

    // §9.3: el take ganó entre la lectura y el UPDATE condicional.
    [Fact]
    public async Task LosingTheAutoTakeRaceIs422WithoutMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Repository.NextAutoAssign = AutoAssignOutcome.AssignedToOther;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(bed.Meta.Sends);
    }

    // §8.5 paso 2: lo que ya salió, ya salió, aunque ahora la tenga otra persona o la ventana esté cerrada.
    [Fact]
    public async Task AnAlreadySentClientIdIsReturnedEvenIfSomeoneElseTookTheConversation()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 30, assignedTo: Guid.CreateVersion7());
        bed.Outbound.Committed = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-5), bed.MemberId, "ya salió");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "otra vez"), Ct);

        Assert.Equal((bed.Outbound.Committed.Id, "ya salió"), (message.Id, message.Text));
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task AQuotedReplySendsTheContextAndReturnsTheReplyTo()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        var quoted = Guid.CreateVersion7();
        bed.Messages.Targets[quoted] = new ReplyTargetRow(quoted, conversation.Id, MessageDirection.Inbound, MessageKind.Text, "¿Tienen?", null, "wamid.q");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí", quoted), Ct);

        Assert.Equal("wamid.q", Assert.Single(bed.Meta.Sends).ContextWamid);
        Assert.Equal(new ReplyToDto(quoted, "Inbound", "Text", "¿Tienen?"), message.ReplyTo);
        Assert.Equal((quoted, "wamid.q"), (Assert.Single(bed.Outbound.Claims).Draft.ReplyToMessageId!.Value, bed.Outbound.Claims[0].Draft.ReplyToWamid));
    }

    [Theory]
    [InlineData("other-conversation")]
    [InlineData("no-wamid")]
    [InlineData("reaction")]
    [InlineData("event")]
    [InlineData("unknown")]
    public async Task AReplyToThatCannotBeQuotedIsAValidationErrorOnReplyTo(string shape)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        var quoted = Guid.CreateVersion7();
        if (shape != "unknown")
        {
            bed.Messages.Targets[quoted] = new ReplyTargetRow(
                quoted,
                shape == "other-conversation" ? Guid.CreateVersion7() : conversation.Id,
                shape == "event" ? MessageDirection.System : MessageDirection.Inbound,
                shape switch { "reaction" => MessageKind.Reaction, "event" => MessageKind.Event, _ => MessageKind.Text },
                "x",
                null,
                shape is "no-wamid" or "event" ? null : "wamid.q");
        }

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí", quoted), Ct));

        Assert.Equal("replyTo", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Meta.Sends);
        Assert.Empty(bed.Outbound.Claims);
    }
```

`SendMessageValidatorTests.cs`: `ReplyTo = Guid.Empty` → error en `replyTo`; `null` o un GUID → sin error de `replyTo`.

Las pruebas existentes de `SendMessageHandlerTests` siguen igual (conversación sin asignar → el doble responde `Assigned`), salvo `ARepeatedClientIdReturnsTheExistingMessageWithoutCallingMeta`, que ahora puede probar ambos caminos: deja `Outbound.Existing` (en vuelo) como está y agrega la aserción `Assert.Empty(bed.Repository.AutoAssigned);` si el paso 2 lo resuelve (`Committed`), o la quita si la prueba usa `Existing`.

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SendMessageHandlerTests|FullyQualifiedName~SendMessageValidatorTests"
```

Esperado: `error CS1729: 'SendMessageHandler' does not contain a constructor that takes 12 arguments` (y `AutoAssignOutcome`, `FindByClientIdAsync`).

- [ ] **Step 2: Puertos y persistencia**

`IOutboundMessages.cs`: los dos records con la forma de **Interfaces**, y:

```csharp
    /// <summary>Spec 2026-10-10 §8.5 paso 2: el mensaje ya commiteado con ese clientId, sin candado; <c>null</c> si no hay.</summary>
    Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken);
```

`OutboundMessages.cs`:
- `ClaimAsync`: el `INSERT` agrega `reply_to_message_id, reply_to_wamid` con `{draft.ReplyToMessageId}, {draft.ReplyToWamid}`; el `SELECT … FOR UPDATE` agrega `reply_to_message_id AS "ReplyToMessageId"` y `ExistingRow` la propiedad `public Guid? ReplyToMessageId { get; set; }`; el `ExistingOutbound` la pasa.
- `CommitSentAsync` y `CommitFailedAsync`: `reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}` en el `UPDATE messaging.messages` (reenviar un Failed reescribe el texto y la cita con los del request nuevo, como hoy el texto).
- Nuevo:

```csharp
    public async Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<ExistingRow>(
            $"""
            SELECT id AS "Id", status AS "Status", occurred_at AS "OccurredAt", sent_by_member_id AS "SentByMemberId",
                   coalesce(text, '') AS "Text", reply_to_message_id AS "ReplyToMessageId"
            FROM messaging.messages WHERE conversation_id = {conversationId} AND client_id = {clientId}
            """).ToListAsync(cancellationToken);
        return rows.Count == 1
            ? new ExistingOutbound(rows[0].Id, MessageColumnCodes.ToStatus(rows[0].Status), rows[0].OccurredAt, rows[0].SentByMemberId, rows[0].Text, rows[0].ReplyToMessageId)
            : null;
    }
```

`IConversationRepository.cs`:

```csharp
/// <summary>Spec 2026-10-10 §8.5 paso 4.</summary>
public enum AutoAssignOutcome
{
    /// <summary>Estaba sin asignar y ahora es de quien envía (evento AutoTaken y auditoría ya commiteados).</summary>
    Assigned,

    /// <summary>Ya era de quien envía.</summary>
    AlreadyMine,

    /// <summary>Otra persona la tomó primero (§9.3).</summary>
    AssignedToOther,
}
```

y en la interfaz:

```csharp
    /// <summary>§8.5 y §3 (corrección 7): un UPDATE condicional (<c>assigned_member_id IS NULL</c>) en su propia
    /// transacción corta, antes del reclamo largo del envío: así la ingesta de esta conversación nunca espera a Meta, y
    /// sin token de concurrencia un entrante que subió la versión no lo convierte en 412 (RF7).</summary>
    Task<AutoAssignOutcome> TryAutoAssignAsync(Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken);
```

`ConversationRepository.cs` (constructor gana `IMessagingAuditRecorder audit`):

```csharp
    public async Task<AutoAssignOutcome> TryAutoAssignAsync(
        Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Dos vueltas: si entre el UPDATE y la relectura alguien liberó, la segunda la toma.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var connection = await dbContext.Database.SqlQuery<Guid>(
                $"""
                UPDATE messaging.conversations
                   SET assigned_member_id = {memberId}, assigned_at = {now}, version = version + 1, updated_at = {now}
                 WHERE id = {conversationId} AND tenant_id = {tenantId} AND assigned_member_id IS NULL
                RETURNING connection_id AS "Value"
                """).ToListAsync(cancellationToken);
            if (connection.Count == 1)
            {
                await Webhook.ConversationEventRows.InsertAsync(dbContext, tenantId, connection[0], conversationId,
                    new ConversationEvent(ConversationEventType.AutoTaken, Actor: memberId), now, now, cancellationToken);
                audit.Record(tenantId, actorUserId, MessagingAuditActions.AutoTaken, conversationId, now);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return AutoAssignOutcome.Assigned;
            }

            await transaction.RollbackAsync(cancellationToken);
            var current = await dbContext.Conversations.AsNoTracking()
                .Where(conversation => conversation.Id == conversationId && conversation.TenantId == tenantId)
                .Select(conversation => conversation.AssignedMemberId)
                .SingleAsync(cancellationToken);
            if (current == memberId)
            {
                return AutoAssignOutcome.AlreadyMine;
            }

            if (current is not null)
            {
                return AutoAssignOutcome.AssignedToOther;
            }
        }

        return AutoAssignOutcome.AssignedToOther;
    }
```

(El `SaveChangesAsync` va directo al `DbContext` y no a `IMessagingUnitOfWork`: la entrada de auditoría es lo único pendiente en el tracker, y la transacción es la de esta sentencia.)

- [ ] **Step 3: El handler**

`SendMessage.cs`:
- `SendMessageCommand` con `Guid? ReplyTo = null`; el validador suma `RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");`.
- Constructor: `(IConversationQueries conversations, IConversationRepository repository, IMessageQueries messages, IOutboundMessages outbound, IWhatsAppCloudClient meta, IMessagingConnectionDirectory connections, IMessagingMemberNames memberNames, IMembershipDirectory membershipDirectory, ITenantModules tenantModules, IExecutionContext executionContext, IClock clock, IValidator<SendMessageCommand> validator)`.
- `HandleAsync`, en este orden (el `<summary>` de la clase se actualiza con él):

```csharp
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");

        // Spec 2026-10-10 §8.5 paso 2: lo que ya salió, ya salió, antes de mirar estado, ventana o dueño.
        if (await outbound.FindByClientIdAsync(conversation.Id, command.ClientId!.Value, cancellationToken) is { Status: not MessageStatus.Failed } sent)
        {
            return await ToDtoAsync(command, ExistingRow(conversation.Id, sent, command.ClientId), cancellationToken);
        }

        var now = clock.UtcNow;
        if (conversation.Status != ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConversationNotOpen, "The conversation is resolved; reopen it to answer.");
        }

        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "More than 24 hours passed since the person last wrote.");
        }

        // §8.5 paso 4: sólo el asignado responde; sin asignar, responder es tomar (atómico, fuera del candado largo).
        if (conversation.AssignedMemberId is { } owner ? owner != member
            : await repository.TryAutoAssignAsync(command.TenantId, conversation.Id, member, executionContext.SubjectId, now, cancellationToken) == AutoAssignOutcome.AssignedToOther)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AssignedToOther, "The conversation is assigned to someone else.");
        }

        // §8.5 paso 5: lo citado es de esta conversación, salió por WhatsApp y no es una reacción ni un evento.
        ReplyTargetRow? quoted = null;
        if (command.ReplyTo is { } replyTo)
        {
            var targets = await messages.FindReplyTargetsAsync(command.TenantId, [replyTo], cancellationToken);
            if (!targets.TryGetValue(replyTo, out quoted) || quoted.ConversationId != conversation.Id || quoted.Wamid is null
                || quoted.Kind is MessageKind.Reaction or MessageKind.Event)
            {
                throw new ValidationException([new ValidationFailure("replyTo", "Elige un mensaje de esta conversación que se pueda citar.")]);
            }
        }

        var text = command.Text!.Trim();
        var draft = new OutboundDraft(Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, text, member, now,
            quoted?.Id, quoted?.Wamid);
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);
```

  y desde ahí sigue el cuerpo de hoy (`:67-129`) con tres cambios: el chequeo de status y ventana ya no está después del reclamo (se movió arriba; el `RollbackAsync` de esas dos ramas desaparece); la idempotencia del reclamo (`claim.Existing` no `Failed`) se conserva para dos requests en vuelo con el mismo `clientId` y devuelve `ExistingRow(conversation.Id, existing, command.ClientId)`; y la llamada a Meta es `meta.SendTextAsync(sender, SendTarget.For(conversation.UserId, conversation.WaId), text, CallbackPrefix + claim.MessageId.ToString("D"), quoted?.Wamid, CancellationToken.None)`. El `MessageRow` del `Sent` lleva `ReplyToMessageId: quoted?.Id`. Si Meta rechaza después de la autoasignación, la conversación queda asignada (§8.5: intentar responder es tomar).
- `ToDtoAsync` resuelve el citado guardado:

```csharp
    private async Task<MessageDto> ToDtoAsync(SendMessageCommand command, MessageRow row, CancellationToken cancellationToken)
    {
        var names = row.SentByMemberId is { } member
            ? await memberNames.FindAsync(command.TenantId, [member], cancellationToken)
            : new Dictionary<Guid, string>();
        // §5.1: repetir un clientId devuelve el replyTo guardado, aunque el cuerpo traiga otro.
        var targets = row.ReplyToMessageId is { } quoted
            ? await messages.FindReplyTargetsAsync(command.TenantId, [quoted], cancellationToken)
            : new Dictionary<Guid, ReplyTargetRow>();
        return MessageMapping.ToDto(row, command.TenantId, names, targets);
    }

    private static MessageRow ExistingRow(Guid conversationId, ExistingOutbound existing, Guid? clientId) =>
        new(existing.Id, conversationId, MessageDirection.Outbound, MessageKind.Text, existing.Text, null, null, existing.Status, null,
            existing.OccurredAt, existing.SentByMemberId, clientId, null, existing.ReplyToMessageId);
```

  (Los dos `ToDtoAsync` posteriores al `CommitSentAsync` siguen recibiendo `CancellationToken.None`.)
- `MessagingEndpoints.cs`: `public sealed record SendMessageRequest(Guid? ClientId, string? Text, Guid? ReplyTo = null);` y `new SendMessageCommand(tenantId, conversationId, request.ClientId, request.Text, request.ReplyTo)`.

`using FluentValidation.Results;` en `SendMessage.cs`.

- [ ] **Step 4: Las de integración (RED → GREEN)**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendAssignmentApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.5 y §9.3: responder la de otro es 422 sin Meta (RF6); responder una sin asignar la toma
/// con AutoTaken y auditoría, aunque Meta rechace; la autoasignación nunca da 412 contra la ingesta (RF7); un clientId
/// ya enviado vuelve igual aunque otro la haya tomado.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class SendAssignmentApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid Owner, Guid Beatriz, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var (beatriz, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        return new Fixture(factory, connectionString, tenant, await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations"),
            await OwnerMembershipIdAsync(connectionString, tenant), beatriz, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static void ScriptSendOk(FakeMetaGraphHandler meta) =>
        meta.Respond("/111/messages", HttpStatusCode.OK, """{"messaging_product":"whatsapp","contacts":[{"input":"CO.1","user_id":"CO.1"}],"messages":[{"id":"wamid.out"}]}""");

    private static int SendCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));

    private static Task<HttpResponseMessage> PostAsync(Fixture f, Guid clientId, string text = "hola") =>
        SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text });

    [Fact]
    public async Task AnswerToSomeoneElsesConversationIs422WithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", f.Beatriz));

        var response = await PostAsync(f, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.conversation.assigned_to_other", (await ProblemAsync(response)).Code);
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AnswerToAnUnassignedOneTakesItEvenIfMetaRejects()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));

        var response = await PostAsync(f, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(f.Owner, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal(f.Owner.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'actor' FROM messaging.messages WHERE details->>'type' = 'AutoTaken'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.auto_taken'"));
    }

    // RF7: la autoasignación es un UPDATE condicional sin token de concurrencia; la ingesta sube version en paralelo.
    [Fact]
    public async Task AutoTakeNeverConflictsWithAConcurrentInbound()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        using var anonymous = f.Factory.CreateClient();
        // Una versión vieja en mano (lo que tendría la pantalla) y un entrante que la sube antes de enviar.
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "¿sigues?"));
        await DrainDeliveriesAsync(f.Factory);
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "¿hola?"));

        var results = await Task.WhenAll(PostAsync(f, Guid.CreateVersion7()), DrainThenOkAsync(f));

        Assert.Equal(HttpStatusCode.Created, results[0].StatusCode);
        Assert.Equal(f.Owner, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'AutoTaken'"));
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
    }

    [Fact]
    public async Task ARetryOfASentClientIdAfterSomeoneElseTookItIsTheSameMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();
        var first = await PostAsync(f, clientId, "una vez");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", f.Beatriz));

        var retry = await PostAsync(f, clientId, "otra");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid());
        Assert.Equal(1, SendCalls(f.Factory.MetaHandler));
    }

    private static async Task<HttpResponseMessage> DrainThenOkAsync(Fixture f)
    {
        await DrainDeliveriesAsync(f.Factory);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
```

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ReplyToApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1 y §8.6: replyTo saliente → context.message_id en Meta y replyTo en la respuesta; un
/// replyTo que no se puede citar es 422 en replyTo sin llamar a Meta; repetir el clientId devuelve la cita guardada.</summary>
public sealed class ReplyToApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "¿Tienen?"));
            await DrainDeliveriesAsync(factory);
        }

        factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"messages":[{"id":"wamid.out"}]}""");
        return new Fixture(factory, connectionString, tenant, await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations"), connectionId,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static Task<HttpResponseMessage> ReplyAsync(Fixture f, Guid clientId, Guid replyTo, string text = "Sí") =>
        SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text, replyTo });

    [Fact]
    public async Task AQuotedReplyCarriesTheContextAndAnswersTheReplyTo()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var inbound = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE wamid = 'wamid.in'");
        var clientId = Guid.CreateVersion7();

        var response = await ReplyAsync(f, clientId, inbound);
        var other = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 3");
        var retry = await ReplyAsync(f, clientId, other);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((inbound, "Inbound", "¿Tienen?"), (body.GetProperty("replyTo").GetProperty("id").GetGuid(), body.GetProperty("replyTo").GetProperty("direction").GetString(), body.GetProperty("replyTo").GetProperty("preview").GetString()));
        Assert.Contains("\"context\":{\"message_id\":\"wamid.in\"}", Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Equal($"{inbound}|wamid.in", await ScalarAsync<string>(f.ConnectionString, "SELECT reply_to_message_id::text || '|' || reply_to_wamid FROM messaging.messages WHERE direction = 2"));
        // §5.1: el reintento devuelve la cita guardada aunque el cuerpo traiga otra (y aunque ésa no se pudiera citar).
        Assert.Equal(inbound, (await retry.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("replyTo").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AReplyToThatCannotBeQuotedIs422OnReplyToWithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.2", null);
        var foreign = await SeedOutboundAsync(f.ConnectionString, other, f.Tenant.TenantId, f.ConnectionId, "wamid.otra");
        var withoutWamid = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.Tenant.TenantId, f.ConnectionId, null, status: 4, failureCode: -1);
        var reaction = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, details, created_at) VALUES (@id, @c, @t, @n, now(), 1, 9, 2, '👍', 'wamid.reaction', '{}', now())""",
            ("id", reaction), ("c", f.ConversationId), ("t", f.Tenant.TenantId), ("n", f.ConnectionId));
        var anEvent = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 3 AND conversation_id = @c", ("c", f.ConversationId));

        foreach (var replyTo in new[] { foreign, withoutWamid, reaction, anEvent, Guid.CreateVersion7() })
        {
            var response = await ReplyAsync(f, Guid.CreateVersion7(), replyTo);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await ProblemAsync(response);
            Assert.Equal(("validation.failed", "replyTo"), (problem.Code, Assert.Single(problem.ErrorKeys)));
        }

        Assert.Empty(f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal)));
    }
}
```

(`SeedOutboundAsync` acepta `string? wamid`, `status` y `failureCode`, `MessagingApiHarness.cs:427-443`.)

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
dotnet build Backend.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw "build" }
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --no-build --filter "FullyQualifiedName~SendMessageHandlerTests|FullyQualifiedName~SendMessageValidatorTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --no-build --filter "FullyQualifiedName~SendAssignmentApiTests|FullyQualifiedName~ReplyToApiTests|FullyQualifiedName~SendMessageApiTests|FullyQualifiedName~RequestFailureCaptureTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: verde. `SendMessageApiTests` (las de antes: idempotencia, concurrencia con el mismo `clientId`, 190/131047/timeout, fuga del token) confirman que el orden nuevo no rompió el envío.

- [ ] **Step 5: Formato y commit**

```
feat(messaging): sólo el asignado responde, responder sin dueño es tomar, y respuestas citadas

El envío (spec 2026-10-10 §8.5) devuelve primero lo ya enviado, después rechaza la conversación de
otra persona (422 assigned_to_other) y toma la que no tiene dueño con un UPDATE condicional en su
propia transacción (AutoTaken + auditoría), sin 412 contra la ingesta. replyTo valida el citado y
viaja como context.message_id; repetir el clientId devuelve la cita guardada.
```

---

### Task 15: README, `CLAUDE.md` y HANDOFF

Spec §14 («Backend»), §13 (riesgo de `user_id_update`). Sólo documentación; sin `.cs`.

**Files:**
- Modify: `README.md` § «Mensajería (WhatsApp Cloud)» (`:1877` en adelante) y su § «Probar el webhook en local» (`:1932-1950`)
- Modify: `CLAUDE.md` § «Gotchas verificados» (`:255-262`, junto al de `Conversations`)
- Modify: `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md` (paso 5)

**Interfaces:**
- Consumes: todo lo anterior (rutas, códigos, comportamiento).
- Produces: nada que use el código.

- [ ] **Step 1: README**

En § «Mensajería (WhatsApp Cloud)», una subsección nueva `#### Identidad, clientes y asignación (spec 2026-10-10)` con, en este orden y sin repetir el spec:
- La conversación se identifica por `(connection_id, user_id)` (BSUID); el teléfono (`wa_id`) puede faltar. Las filas viejas sin BSUID se adoptan con el primer entrante que traiga BSUID y teléfono. Se envía con `recipient` (BSUID) y sólo una fila vieja con `to`.
- Todo el que escribe queda atado a un cliente de QEP: si no existe, se crea con la ficha **incompleta** (sin CUC); el `PUT /customers/{id}` con todos los datos la completa y le emite el CUC. Quotations rechaza a un incompleto con `quotation.quotation.client_incomplete`; el reporte de clientes no lo cuenta.
- Asignación: `POST …/take|transfer|release` con `If-Match`; sólo el asignado responde (`messaging.conversation.assigned_to_other`); responder una sin asignar la toma; resolver no libera; una conversación nueva de un cliente con dueño hereda el asignado si todavía puede responder. `GET /messaging/assignees` lista a quién transferir. `assignedTo.isMe` lo calcula el servidor.
- Eventos del sistema (`direction: System`, `kind: Event`) en el hilo; no suben `unreadCount` ni cambian la foto de la lista.
- Respuestas citadas: `replyTo` al enviar (el `id` de **nuestro** mensaje) y `Message.replyTo` al leer.

En § «Probar el webhook en local», el `webhook.json` de ejemplo gana `"from_user_id": "CO.573001234567"` en el mensaje y `"user_id": "CO.573001234567"` en el contacto, con una línea: «Sin `from_user_id` la ingesta salta el mensaje (spec 2026-10-10 §8.1); el teléfono es opcional». Los comandos siguen en PowerShell con `curl.exe` y `--data-binary "@webhook.json"`.

- [ ] **Step 2: `CLAUDE.md`**

Sólo lo que no se deduce leyendo el código (es prescriptivo). Dos viñetas nuevas en «Gotchas verificados»:

- **La clave de una conversación es el BSUID, no el teléfono** (spec 2026-10-10). `wa_id` puede venir `null` y dos conversaciones con BSUID pueden compartir número (reciclado). Un cuerpo de webhook de prueba sin `from_user_id` se descarta en silencio —la entrega queda procesada y no aparece nada—: es el síntoma, no un bug de la ingesta.
- **Un cliente puede ser `Incomplete`** (sin CUC, documento, dirección ni clasificación): lo crea la ingesta de Messaging, nunca un endpoint. Todo código nuevo que lea `Cuc`, `IdentificationType`, `Address`, `Country` o `ClassificationId` de un `Customer` tiene que tratar el `null`; Quotations lo rechaza con `client_incomplete` **antes** que `client_cuc_missing`, y un reporte que agrupe por clasificación o ciudad filtra `Completeness == Complete`.

- [ ] **Step 3: HANDOFF**

`docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`, paso 5: «suscribe **sólo** `messages` y `account_update`» pasa a «suscribe `messages`, `account_update` y, si la app lo ofrece en la lista de campos, `user_id_update` (spec 2026-10-10 §13: la documentación no muestra si es un campo aparte). Después del primer cambio de número real, revisa en el log que llegó y que la conversación siguió siendo la misma; hasta entonces el cambio de número se considera no verificado». Y un paso nuevo al final: «**Desplegar backend y frontend juntos** (spec 2026-10-10 §13): el SPA viejo trata `direction: "System"` como error de contrato y dibuja `+{waId}` aunque venga `null`».

- [ ] **Step 4: Commit**

```
docs(messaging): identidad por BSUID, cliente incompleto y asignación en README, CLAUDE.md y HANDOFF

README «Mensajería» con el delta del spec 2026-10-10 y el webhook de prueba con from_user_id;
CLAUDE.md con los dos gotchas que no se ven en el código; el HANDOFF pide suscribir user_id_update
y desplegar backend y frontend juntos.
```

---

### Task 16: Suite completa, build sin warnings y formato — una sola vez, comparada contra la base

**Files:**
- Reusa (fuera del repo, no se commitea): `C:\Users\andre\AppData\Local\Temp\qep-messaging\compare-test-runs.ps1` (ya existe; compara fallas por nombre y corre en la base sólo las clases que fallaron).

**Interfaces:**
- Consumes: todo el plan.
- Produces: la lista de fallas de la rama y, por cada una, si también falla en la base (preexistente) o no (regresión). Cero regresiones es la condición de cierre.

- [ ] **Step 1: Build de toda la solución sin warnings**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
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
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
$results = "C:\Users\andre\AppData\Local\Temp\qep-messaging\branch"
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $results
```

(Tarda: Quotations solo lleva ~15 minutos. En primer plano, sin `| tail`.) Si reporta `Con error: 0`, salta al Step 6.

- [ ] **Step 4: El worktree de la base, por SHA y desprendido**

`develop` está en el checkout principal del owner (`$Main`): `git worktree add <ruta> develop` falla con «already checked out». La base es el **commit de donde salió la rama**, en un worktree desprendido, bajo el home (CodeGraph):

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
$Baseline = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\develop-baseline"
$base = git -C $W merge-base develop HEAD
if (Test-Path $Baseline) { git -C $W worktree remove --force $Baseline }
git -C $W worktree add --detach $Baseline $base
git -C $Baseline log -1 --oneline
```

Esperado: el SHA de `git merge-base` (`2d68627` o el `develop` del momento en que se ramificó). El script (`compare-test-runs.ps1`) ve que `$Baseline` ya existe y no intenta crearlo con el nombre de la rama.

- [ ] **Step 5: Correr la comparación**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\andre\AppData\Local\Temp\qep-messaging\compare-test-runs.ps1" -Branch "C:\Users\andre\AppData\Local\Temp\qep-messaging\branch" -Worktree $W
```

Esperado: `Regresiones: 0`. Las clases nuevas de este plan que fallen son regresión por definición (no existen en la base). Preexistentes conocidas (memoria del proyecto): 2 de `OrderExportApiTests` (NIT en export, desde `6f8aa75`). Cada regresión se arregla en la tarea dueña (RED/GREEN, commit propio, **nunca** `--amend`) y se vuelve a correr **sólo esa clase**; la suite completa no se repite. Al terminar:

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
git -C $W worktree remove --force "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\develop-baseline"
```

(Si falla con «Invalid argument», hay un `codegraph serve` con el índice abierto; con «Filename too long», son `bin/obj`: borrar con `\\?\` — memoria del proyecto.)

- [ ] **Step 6: Cierre**

```powershell
$W = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\mensajeria-asignacion"
Set-Location $W
git status --short
git log --oneline develop..HEAD
git log develop..HEAD --format=%B | Select-String -Pattern "Co-Authored" | Measure-Object
```

Esperado: árbol limpio, los commits del spec, del plan y de las tareas 1–15, `Count 0`. Sin commit en esta tarea.

---

## Cobertura del spec

| Sección del spec | Tarea |
| --- | --- |
| §2 decisión 1 (BSUID) | 5, 6, 7, 10 |
| §2 decisión 2 (cliente incompleto) | 1, 2, 3, 4, 9 |
| §2 decisión 3 (asignación) | 8, 11, 13, 14, 9 (herencia) |
| §2 decisión 4 (citas) | 6 (columnas, `context`), 7 (parser), 9 (entrante), 12 (lectura), 14 (saliente) |
| §2 decisión 5 (eventos) | 5, 6 (`CHECK`), 9 (ingesta), 12 (lectura), 13 (handlers), 14 (`AutoTaken`) |
| §2 decisión 6 (envío sin cambio de contrato) | 14 (idempotencia primero, `None` después de Meta) |
| §3 correcciones 1–8 | 6 (`username` 64), 4 (código D-A4 y orden), 1 (`completeness`, `CHECK` D-A11), 8 (`/assignees`), 14 (autoasignación fuera del candado), 15 (desplegar juntos) |
| §5.1 Messaging (incluye D-A13 `isMe`) | 6, 8, 11, 12, 13, 14 |
| §5.2 Customers | 1, 3 |
| §5.3 Quotations | 4 |
| §6.1.1–6.1.7 | 5–7, 9, 11–14 |
| §6.2 Customers | 1, 2, 3 |
| §6.3 Quotations · §6.4 Reporting | 4 |
| §6.5 Bootstrapper · §6.6 Authorization | 8, 9, 10, 11 (adaptadores); sin permisos nuevos |
| §7.1 migración Messaging · §7.2 migración Customers | 6 · 1 |
| §8.1 ingesta · §8.2 asegurar cliente · §8.3 cambio de número | 7, 9 · 2, 9 · 10 |
| §8.4 take/transfer/release · §8.5 envío · §8.6 citas · §8.7 eventos | 13 · 14 · 9, 12, 14 · 9, 13, 14 |
| §9 concurrencia 1–7 | 13 (1, 2), 14 (3, 7), 2 y 9 (4), 7 (5), 9 (6) |
| §10 errores · §11 seguridad | 4, 5, 11, 13, 14 · 8, 13 (aislamiento), 5 (details sólo ids), 10 (log sin BSUID) |
| §12 pruebas | cada tarea; arquitectura en 2, 8–14; suite en 16 |
| §13 riesgos · §14 entregables backend | 15 (HANDOFF, despliegue conjunto) · 1–16 |
