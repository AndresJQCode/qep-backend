# Mensajería: bloquear y desbloquear un contacto — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un asesor bloquee y desbloquee al contacto de una conversación de WhatsApp con la Block Users API de Meta, que el estado quede en la conversación (`blocked` en `ConversationSummary`) y que mientras dure ningún envío de Mensajería —humano ni del chatbot— salga hacia Meta.

**Architecture:** `Conversation` gana `BlockedAt`, `BlockedByMemberId` y `BlockedWaId` con `Block`/`Unblock` (suben `Version`) y dos eventos de hilo nuevos. `IWhatsAppCloudClient` gana `BlockUserAsync`/`UnblockUserAsync` (`POST`/`DELETE …/block_users`). Dos handlers nuevos con el molde de `PauseAutomationHandler` (`ConversationAssignmentSteps`) validan antes de llamar a Meta, clasifican la respuesta con una tabla propia (`ContactBlockResponse`) y guardan con reintento acotado ante `concurrency.conflict`, descartando lo pendiente del tracker (`IMessagingUnitOfWork.DiscardPendingChanges`). `OutboundGuards.AdmitAsync` y `BotOutbound` rechazan una conversación bloqueada; el bot devuelve `BotSendResult.Blocked` → `BotSendOutcome.Blocked`, que `RunEffects` cierra `Interrupted` en silencio.

**Tech Stack:** .NET 10, EF Core + Npgsql, xunit.v3 + Testcontainers.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-10-26-bloquear-contacto-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-10-26-bloquear-contacto-design.md) (§2, §3 y §5 son el alcance de este plan; §4 es el slice de frontend). Fuente externa: [WhatsApp Cloud API — Block Users](https://developers.facebook.com/docs/whatsapp/cloud-api/block-users).

## Cambios respecto al spec (decisión del owner, 2026-10-26)

1. **Código nuevo `messaging.contact.unblock_rejected` (422).** Si Meta responde `131047` al **desbloquear**, no es `block_window_closed` (el spec §3.3 supone que desbloquear no exige ventana): es este código, con un log de advertencia. Si aparece en la prueba manual, es `DECISIÓN-PENDIENTE` para el owner.
2. **No hay sonda con `curl` antes de implementar.** Lo que el spec §3.3 deja «a verificar» se maneja a la defensiva: el estado propio se mira primero (idempotencia sin Meta), y todo código de `failed_users` que no reconocemos —incluidos los de «ya estaba bloqueado» / «no estaba bloqueado», que la doc no publica— es `502 messaging.contact.block_failed` con un log estructurado (código, subcódigo, fbtrace). La verificación contra Meta real es la **prueba manual desde la interfaz** de la Task 9.

## Contrato

**Endpoints** (grupo `/api/v1/tenants/{tenantId}/messaging`, permiso `messaging.conversation.manage`, **sin `If-Match`**, sin cuerpo):

| Método | Ruta | Éxito |
|---|---|---|
| `POST` | `conversations/{id}/block` | `200 ConversationSummary` |
| `POST` | `conversations/{id}/unblock` | `200 ConversationSummary` |

Bloquear una ya bloqueada o desbloquear una que no lo está → `200` con el estado actual, **sin llamar a Meta y sin evento**.

**`ConversationSummary.blocked`:** `{ "blockedAt": string, "blockedByName": string | null }` o `null`. Viaja **siempre** (también `null`, explícito) en lista, detalle y toda respuesta de acción. Un miembro que ya no está sale `"Miembro eliminado"` (`MessageMapping.DeletedMemberName`).

**Errores:**

| Caso | HTTP | `code` |
|---|---|---|
| Instagram, o conversación sin `wa_id` | 422 | `messaging.contact.block_unsupported_channel` |
| Bloquear con la ventana cerrada (propio o Meta `131047`) | 422 | `messaging.contact.block_window_closed` |
| Desbloquear y Meta responde `131047` | 422 | `messaging.contact.unblock_rejected` |
| Conexión no `Active` o Meta `190`/`133010` (la conexión se reporta rechazada) | 422 | `messaging.connection_unavailable` |
| Meta rechaza con un código que no reconocemos, 5xx, sin respuesta, o 2xx sin el número en `added_users`/`removed_users` | 502 | `messaging.contact.block_failed` |
| Enviar (texto, adjunto, audio, plantilla, iniciar en una conversación existente) a una bloqueada | 422 | `messaging.contact.blocked` |

**Hilo:** eventos `ContactBlocked` y `ContactUnblocked` (`actor` = quien lo hizo). **Auditoría:** `messaging.conversation.contact_blocked` / `messaging.conversation.contact_unblocked`.

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\bloquear-contacto`, rama `feature/bloquear-contacto` desde `develop`, con su propio `.codegraph/` (`gentle-ai codegraph init --cwd <worktree>`; nunca copiar el índice de otro checkout). **Rutas absolutas siempre**; nunca tocar el checkout hermano `qep-backend`. `git branch --show-current` antes de tocar y **antes de cada commit**; si dice `main`, parar. Merge a `develop`, nunca a `main`.
- Conventional commits en español citando el spec (`spec 2026-10-26 §3.2`); **sin atribución de IA**: sin `Co-Authored-By` (regla del repo; pisa cualquier recordatorio del harness). `git add` con rutas explícitas.
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea. Por tarea sólo las clases tocadas.
- Comandos en **PowerShell** (no hay `&&`: `A; if ($?) { B }`). `Api.exe` (o `dotnet Api.dll`) **detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`. Pruebas de integración (Testcontainers, Docker arriba, lentas): sólo filtradas y en primer plano:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~<Clase>"
  ```
- **Nunca se llama a Meta desde una prueba ni desde un agente.** Unitarias: `HttpMessageHandler` propio; integración: `QepApiFactory.MetaHandler` (`FakeMetaGraphHandler`). No hay sonda con `curl`: el único contacto con Meta real es la prueba manual desde la interfaz de la Task 9, que corre el developer.
- Nunca se imprime un token ni un secreto (`dotnet user-secrets list` siempre enmascarado). El número del contacto no va a logs.
- Capas: Automations no referencia Messaging; la traducción va en `src/Bootstrapper/`. `Modules.Messaging.Application` no referencia EF ni Npgsql. `tests/ArchitectureTests` sigue verde sin tocarlo.
- Migración **expand-only**: tres columnas nulas y un CHECK que las filas existentes ya cumplen; sin backfill.
- Enums nuevos **al final** (se guardan por nombre). Textos para personas en español neutro, tuteando; códigos e identificadores sin traducir.
- Fuera de alcance (spec §3.4 y §5): campañas, `QuotationTemplateOutbound`, Instagram, filtro de bloqueadas, sincronizar con `GET block_users`. Ninguno se toca.
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear.
- **Despliegue:** backend primero (spec §5.6/§4.6). El frontend viejo ignora `blocked`.

## Review Focus

1. **Bloquear otra vez a un contacto ya bloqueado con la ventana cerrada** —lo normal: un bloqueado no escribe, así que la ventana se cierra a las 24 h—: tiene que responder 200 sin llamar a Meta, no `block_window_closed`. La idempotencia va **antes** de toda validación (Task 6, `BlockingAgainAnswersTheStateWithoutMetaEvenWithTheWindowClosed`).
2. **Un entrante o un webhook de estado sube `version` entre la respuesta de Meta y el guardado:** se reintenta, queda bloqueada, con **un** evento y **una** auditoría, y no se deshace en Meta (Task 6, `AConflictAfterMetaAcceptedIsRetriedAndBlocksOnce`).
3. **`blocked: null` explícito** en lista y detalle: el frontend distingue `null` (backend nuevo) de ausente (backend viejo) para ofrecer el botón (Task 6, `TheListAndDetailCarryBlockedAsAnExplicitNull`).
4. **El contacto cambió de número después del bloqueo:** desbloquear manda `blocked_wa_id`, no el `wa_id` actual (Task 6, `UnblockingSendsTheBlockedNumberAfterANumberChange`).
5. **Bloqueada y resuelta, el asesor intenta escribir o iniciar con plantilla:** `messaging.contact.blocked`, no `not_open`, sin reclamo ni llamada a Meta; mismo orden que el compositor del frontend (§4.3) (Task 7, escenarios `blocked-and-resolved`; `AnExistingBlockedConversationRefusesTheTemplateWithoutMeta`).
6. **Meta contesta algo que no conocemos:** `502 block_failed` con el estado intacto y un log con código, subcódigo y fbtrace —nunca el token ni el número—; y `131047` al desbloquear es `unblock_rejected`, no `block_window_closed` (Task 6, `AnUnrecognizedMetaCodeIs502BlockFailed`, `UnblockingRejectedByMetaIsUnblockRejected`).

---

### Task 1: Dominio — el bloqueo en `Conversation` (spec §3.1, §3.3)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` (propiedades junto a `AutomationResumedAt` `:110`, métodos junto a `PauseAutomation` `:383-438`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs:4-21`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MetaErrorCodes.cs`, `src/Modules/Messaging/Modules.Messaging.Application/OutboundSettlement.cs:58-59`, `src/Modules/Messaging/Modules.Messaging.Domain/MessageFailureReasons.cs:17` (el literal `131047` → la constante)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationBlockTests.cs` (nuevo)

**Interfaces:**
- Produces:
  - `DateTimeOffset? Conversation.BlockedAt`, `Guid? Conversation.BlockedByMemberId`, `string? Conversation.BlockedWaId`, `bool Conversation.IsBlocked` (`BlockedAt is not null`).
  - `void Conversation.EnsureCanBlock(DateTimeOffset now)` — en este orden: `Channel == Instagram` o `WaId is null` → `MessagingDomainException(BlockUnsupportedChannel)`; `!IsWindowOpen(now)` → `BlockWindowClosed`. No toca estado ni versión (como `EnsureCanResolve`).
  - `ConversationEvent? Conversation.Block(Guid memberId, DateTimeOffset now)` — `null` si ya está bloqueada (sin `Touch`); si no: `BlockedAt = now`, `BlockedByMemberId = memberId`, `BlockedWaId = WaId`, `Touch(now)`, devuelve `new ConversationEvent(ContactBlocked, Actor: memberId)`. **No** revisa la ventana (la Task 6 la llama después de que Meta aceptó). `WaId is null` → `InvalidOperationException` (error de programación: `EnsureCanBlock` va antes).
  - `ConversationEvent? Conversation.Unblock(Guid memberId, DateTimeOffset now)` — `null` si no está bloqueada; si no, limpia los tres campos, `Touch(now)`, devuelve `ContactUnblocked` con `Actor: memberId`. (Desvío del spec, que escribe `Unblock(now)`: el evento del hilo necesita el actor, como `PauseAutomation`.)
  - `enum ConversationEventType { …, CustomerChanged, ContactBlocked, ContactUnblocked }`.
  - `MessagingErrorCodes.BlockUnsupportedChannel = "messaging.contact.block_unsupported_channel"`, `BlockWindowClosed = "messaging.contact.block_window_closed"`, `BlockFailed = "messaging.contact.block_failed"`, `UnblockRejected = "messaging.contact.unblock_rejected"`, `ContactBlocked = "messaging.contact.blocked"`.
  - `MetaErrorCodes.CustomerWindowClosed = 131047`.

- [ ] **Step 1: RED.** `ConversationBlockTests` (conversación con `Conversation.StartWithUserId(…, waId: "573001234567", …)` y `ForTests(…, lastInboundAt: now.AddHours(-1))`):
  - `BlockingKeepsStatusAssignmentUnreadAndAutomation` — abierta, asignada, `UnreadCount = 3`, asistente `Active`; `Block(m, now)` → `BlockedAt == now`, `BlockedByMemberId == m`, `BlockedWaId == "573001234567"`, `Version` +1; `Status`, `AssignedMemberId`, `UnreadCount`, `AutomationStatus` iguales; evento `Type == ContactBlocked`, `Actor == m`.
  - `BlockingAResolvedConversationKeepsItResolved`.
  - `BlockingTwiceIsANoOp` — segundo `Block` → `null`, `Version` igual, `BlockedByMemberId` el del primero.
  - `UnblockingClearsTheThreeFields` — `Unblock(m2, later)` → los tres `null`, `Version` +1, evento `ContactUnblocked`, `Actor == m2`; `Unblock` sin bloqueo → `null`, `Version` igual.
  - `EnsureCanBlockRefusesInstagramAndConversationsWithoutWaId` — Instagram → `BlockUnsupportedChannel`; WhatsApp con `waId: null` → `BlockUnsupportedChannel`; `Version` igual.
  - `EnsureCanBlockRefusesAClosedWindow` — `lastInboundAt = now.AddHours(-25)` y `null` → `BlockWindowClosed`.
  (El cambio de número reescribe `wa_id` por SQL, `ContactNumberChange.cs:45`: su prueba es de integración, Task 6.)
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationBlockTests"
  ```
  Expected: FAIL de compilación (`Block`, `EnsureCanBlock`, `ContactBlocked` no existen).
- [ ] **Step 2: GREEN.** Implementar lo de **Interfaces**; `131047` pasa a `MetaErrorCodes.CustomerWindowClosed` en los dos lugares (sin cambiar comportamiento).
- [ ] **Step 3:** el proyecto unitario de Messaging completo (cubre `OutboundSettlement` por `SendMessageHandlerTests`, InlineData `131047`).
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(messaging): estado de bloqueo del contacto en la conversación (spec 2026-10-26 §3.1)`.

### Task 2: Persistencia — columnas, CHECK, `ConversationRow` y descarte del tracker (spec §3.1, §3.2)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:161-225` (`ConfigureConversation`)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<timestamp>_AddConversationBlock.cs` (+ `.Designer.cs`, snapshot)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs:6-37`, `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs:12-18` (`Projection`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs:90-93`, `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingUnitOfWork.cs`, y cada doble de `IMessagingUnitOfWork` en `tests/` (`rg -l ": IMessagingUnitOfWork" tests`; hoy `CampaignTestDoubles.cs:133`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - Columnas `blocked_at timestamptz NULL`, `blocked_by_member_id uuid NULL` (referencia blanda, sin FK, como `automation_paused_by_member_id`), `blocked_wa_id varchar(20) NULL` (`Conversation.WaIdMaxLength`). CHECK `CK_conversations_block`: los tres nulos o los tres no nulos.
  - `ConversationRow(…, DateTimeOffset? AutomationResumedAt = null, DateTimeOffset? BlockedAt = null, Guid? BlockedByMemberId = null)` (al final, con default: los dobles que construyen filas no cambian).
  - `void IMessagingUnitOfWork.DiscardPendingChanges()` → `dbContext.ChangeTracker.Clear()` (precedente: `QuotationsUnitOfWork.cs:74`).

- [ ] **Step 1: RED.** En `MessagingPersistenceTests`:
  - `ABlockRoundTripsAndTheRowCarriesIt` — conversación guardada, `Block(m, now)`, `SaveChangesAsync`; en otro scope `IConversationQueries.FindAsync` → `BlockedAt == now` (precisión de Postgres), `BlockedByMemberId == m`; `SELECT blocked_wa_id` = `"573001234567"`.
  - `AHalfWrittenBlockIsRejectedByTheCheck` — `UPDATE messaging.conversations SET blocked_at = now()` (sin las otras dos) → `PostgresException` `23514` con `ConstraintName == "CK_conversations_block"`.
  - `DiscardPendingChangesDropsWhatTheHandlerTracked` — cargar la conversación, `Block`, `IConversationEvents.Record(…)`, `DiscardPendingChanges()`, `SaveChangesAsync` → 0 filas escritas; la conversación sigue sin bloquear.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~MessagingPersistenceTests"
  ```
  Expected: FAIL de compilación (`DiscardPendingChanges`, `ConversationRow.BlockedAt` no existen).
- [ ] **Step 2: GREEN.** Mapeo (`HasColumnName`, `HasMaxLength`, el CHECK en `ToTable(…)` junto a los demás) y la migración:
  ```powershell
  dotnet ef migrations add AddConversationBlock --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
  ```
  Revisar que el `Up` sólo agregue tres columnas nulas y el CHECK, y el `Down` los quite. `Projection` suma `conversation.BlockedAt, conversation.BlockedByMemberId`.
- [ ] **Step 3:** el mismo filtro, más los dos unitarios que construyen filas.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~MessagingPersistenceTests"
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
  ```
  Expected: PASS, incluida la prueba de migración idempotente que ya existía en `MessagingPersistenceTests`.
- [ ] **Step 4: Commit** `feat(messaging): columnas del bloqueo de contacto y descarte del tracker (spec 2026-10-26 §3.1, §3.2)`.

### Task 3: Cliente de Meta — `BlockUserAsync` / `UnblockUserAsync` (spec §2, §3.3 paso 2)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs` (record nuevo junto a `MessagingGraphResult<T>` `:31-34`; métodos junto a `MarkReadAsync` `:112-113`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs` (junto a `MarkReadAsync` `:377-387`; reutiliza `SendAsync`, `Unreachable`, `FailureOf` `:626-629`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:431` (`FakeWhatsAppClient`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientBlockUsersTests.cs` (nuevo)

**Interfaces:**
- Produces:
  - `sealed record BlockUsersResult(bool Applied, int? FailedCode, int? FailedSubcode = null, string? FbTraceId = null)` — `Applied` = el número vino en `block_users.added_users` (bloquear) o `block_users.removed_users` (desbloquear); `FailedCode` / `FailedSubcode` = `block_users.failed_users[0].errors[0].code` / `.error_subcode` (si viene); `FbTraceId` = el header `x-fb-trace-id` de la respuesta si viene, `null` si no (no se supone que esté). Son para el log de la Task 6; nunca llevan el número.
  - `MessagingGraphFailure` gana `int? Subcode = null, string? FbTraceId = null` **al final y con default** (nadie más cambia); `FailureOf` los llena desde `GraphError` (`Subcode`, `FbTraceId`, que ya se parsean para `LogAnswered` `:43-44`).
  - `Task<MessagingGraphResult<BlockUsersResult>> BlockUserAsync(MessagingSender sender, string waId, CancellationToken cancellationToken)` — `POST {Version}/{sender.PhoneNumberId}/block_users`.
  - `Task<MessagingGraphResult<BlockUsersResult>> UnblockUserAsync(MessagingSender sender, string waId, CancellationToken cancellationToken)` — `DELETE`, mismo cuerpo.
  - Cuerpo: `{"messaging_product":"whatsapp","block_users":[{"user":"+<waId>"}]}` (el `+` lo pone el cliente: `wa_id` se guarda sólo con dígitos, `Conversation.cs:62`). Cliente compartido (`HttpClientName`, 10 s). Sin respuesta → `Unreachable` (status 0). No 2xx → `FailureOf(status, …)`, con `Code = error.code ?? failed_users[0].errors[0].code`. 2xx ilegible → `Failure(status, null, "unreadable")`. Operaciones de log `"block"` / `"unblock"`; el número nunca se registra.
  - `FakeWhatsAppClient.NextBlock` (`MessagingGraphResult<BlockUsersResult>`, default aplicado) y `FakeWhatsAppClient.BlockCalls` (`List<(string Direction, string WaId)>`).

- [ ] **Step 1: RED.** `WhatsAppCloudClientBlockUsersTests`, con un `HttpMessageHandler` propio que anota método, path, `Authorization` y cuerpo (el patrón de `WhatsAppCloudClientTests.CapturingHandler`), `MessagingSender("111", "token")`:
  - `BlockPostsThePlusPrefixedNumberToBlockUsers` — `Method == POST`, path `/v24.0/111/block_users`, `Authorization == "Bearer token"`, cuerpo contiene `"messaging_product":"whatsapp"` y `"block_users":[{"user":"+573001234567"}]`.
  - `UnblockSendsTheSameBodyWithDelete` — `Method == DELETE`, mismo path y cuerpo.
  - `AnAddedUserIsApplied` — 200 `{"block_users":{"added_users":[{"input":"+573001234567","wa_id":"573001234567"}]}}` → `Succeeded`, `Applied == true`, `FailedCode == null`.
  - `ARemovedUserIsApplied` — lo mismo con `removed_users` en `UnblockUserAsync`.
  - `AFailedUserCarriesItsCode` — 200 `{"block_users":{"failed_users":[{"input":"+573001234567","errors":[{"code":139100,"error_subcode":99}]}]}}` (subcódigo de prueba, no de Meta) → `Succeeded`, `Applied == false`, `FailedCode == 139100`, `FailedSubcode == 99`.
  - `ASuccessWithoutTheUserIsNotApplied` — 200 `{}` → `Applied == false`, `FailedCode == null`.
  - `AGraphErrorKeepsStatusCodeSubcodeAndTrace` — 400 `FakeMetaGraphHandler.GraphError(131047)`-shaped, con `error_subcode` y `fbtrace_id` → `Failure.HttpStatus == 400`, `Failure.Code == 131047`, `Failure.Subcode` y `Failure.FbTraceId` los del cuerpo.
  - `AnUnreachableGraphIsStatusZero` — el handler lanza `HttpRequestException` → `Failure.HttpStatus == 0`.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClientBlockUsersTests"
  ```
  Expected: FAIL de compilación (`BlockUserAsync` no existe).
- [ ] **Step 2: GREEN.** Implementar en el cliente y en `FakeWhatsAppClient`.
- [ ] **Step 3:** el filtro y las otras clases del cliente.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClient"
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(messaging): cliente de Block Users de la Cloud API (spec 2026-10-26 §2)`.

### Task 4: Contrato — `blocked` en `ConversationSummary` (spec §3.5)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:29-51`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:87-92` (ids de miembro a resolver), `:115-153` (`ToSummary`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationReadsTests.cs`

**Interfaces:**
- Consumes: `ConversationRow.BlockedAt/BlockedByMemberId` (Task 2).
- Produces: `sealed record ConversationBlockDto(DateTimeOffset BlockedAt, string? BlockedByName)`; `ConversationSummary(…, ConversationAutomationDto? Automation = null, ConversationBlockDto? Blocked = null)`. `BlockedByName`: el nombre resuelto; `MessageMapping.DeletedMemberName` si la membresía ya no está; `null` sólo si `BlockedByMemberId` es `null`.

- [ ] **Step 1: RED.** En `ConversationReadsTests` (con `ConversationSummaryBuilder.ToSummary`, como `APartialLastMessageSnapshotIsNullNotAnError`):
  - `ABlockedRowCarriesWhenAndWhoBlocked` — fila con `BlockedAt = t`, `BlockedByMemberId = m`, `memberNames[m] = "Beatriz"` → `Blocked == new ConversationBlockDto(t, "Beatriz")`.
  - `ABlockerWhoLeftShowsAsDeletedMember` — sin `m` en los nombres → `BlockedByName == "Miembro eliminado"`.
  - `AnUnblockedRowCarriesNull` — `Blocked == null`.
  - `BuildAsyncResolvesTheBlockerInTheSameNamesQuery` — `BuildAsync` con una fila bloqueada por `m` y sin asignado: el doble de `IMessagingMemberNames` recibe `m` en **una** llamada.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationReadsTests"
  ```
  Expected: FAIL de compilación (`ConversationBlockDto` no existe).
- [ ] **Step 2: GREEN.** Implementar lo de **Interfaces**.
- [ ] **Step 3:** el filtro → PASS.
- [ ] **Step 4: Commit** `feat(messaging): blocked en el resumen de la conversación (spec 2026-10-26 §3.5)`.

### Task 5: La tabla de respuestas de Meta al bloquear (spec §3.3)

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ContactBlockResponse.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ConnectionRejection.cs` (extrae `OutboundSettlement.ReportRejectedAsync` `:115-129` sin cambiar comportamiento)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/OutboundSettlement.cs:55` (llama a `ConnectionRejection.ReportAsync`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ContactBlockResponseTests.cs` (nuevo)

**Interfaces:**
- Consumes: `BlockUsersResult`, `MessagingGraphResult<T>`, `MessagingGraphFailure.Subcode/FbTraceId` (Task 3); `MetaErrorCodes.CustomerWindowClosed` (Task 1).
- Produces:
  - `internal enum ContactBlockDirection { Block, Unblock }`, `internal enum ContactBlockVerdict { Applied, WindowClosed, UnblockRejected, ConnectionRejected, Failed }`.
  - `internal sealed record ContactBlockAnswer(ContactBlockVerdict Verdict, int? Code, int? Subcode, string? FbTraceId)`.
  - `internal static ContactBlockAnswer ContactBlockResponse.Classify(MessagingGraphResult<BlockUsersResult> result, ContactBlockDirection direction)`. `Code`/`Subcode`/`FbTraceId` = los de `BlockUsersResult` (2xx) o los de `Failure`. En orden: `Applied` → `Applied`; `Code == CustomerWindowClosed` y `Block` → `WindowClosed`; `Code == CustomerWindowClosed` y `Unblock` → `UnblockRejected`; `MetaErrorCodes.IsRejectedCredentials(Code)` → `ConnectionRejected`; **todo lo demás** (un código que no reconocemos, `null`, status 0, 5xx, 2xx sin el número) → `Failed`.
  - **No** hay constantes para «ya estaba bloqueado» / «no estaba bloqueado»: la doc de Meta no las publica y no se verifican antes (ver «Cambios respecto al spec»). Si Meta y nuestro estado divergen, cae en `Failed` y el log de la Task 6 trae el código para decidir después.
  - `internal static Task ConnectionRejection.ReportAsync(IMessagingConnectionDirectory connections, Guid tenantId, Guid connectionId, int code)` — `ReportRejectedAsync(…, MetaErrorCodes.RejectedReason(code), CancellationToken.None)`; si falla, `MessagingDomainException(ConnectionUnavailable, …, inner)` como hoy.

- [ ] **Step 1: RED.** `ContactBlockResponseTests`:
  - `AnAppliedAnswerIsApplied` — en las dos direcciones.
  - `ClassifyMapsEachCode` (`[Theory]`): `(Block, failed 131047) → WindowClosed`; `(Block, 4xx 131047) → WindowClosed`; `(Unblock, failed 131047) → UnblockRejected`; `(Unblock, 4xx 131047) → UnblockRejected`; `(Block, 190) → ConnectionRejected`; `(Unblock, 133010) → ConnectionRejected`; `139100`, `139101`, `139103`, `130429` → `Failed` en las dos direcciones.
  - `NoAnswerOrA5xxIsFailed` — status 0, 500, y 2xx sin el número.
  - `TheAnswerCarriesCodeSubcodeAndTraceForTheLog` — `failed_users` con subcódigo y un 4xx con `fbtrace_id` → `Code`, `Subcode` y `FbTraceId` pasan tal cual.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ContactBlockResponseTests"
  ```
  Expected: FAIL de compilación (`ContactBlockResponse` no existe).
- [ ] **Step 2: GREEN.** Implementar; extraer `ConnectionRejection`.
- [ ] **Step 3:** el filtro y los envíos que usan la extracción (InlineData `190`/`133010` sin cambiar asserts).
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ContactBlockResponseTests|FullyQualifiedName~SendMessageHandlerTests|FullyQualifiedName~SendMediaMessageHandlerTests|FullyQualifiedName~SendAudioMessageHandlerTests"
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(messaging): tabla de respuestas de Meta al bloquear un contacto (spec 2026-10-26 §3.3)`.

### Task 6: Endpoints `block` / `unblock`, auditoría, reintento y 502 (spec §3.2, §3.3)

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ContactBlock.cs` (comandos y handlers)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAssignment.cs:27-73` (`ConversationAssignmentSteps`: dependencia `IConversationRepository` ya está; suma el método de reintento)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs:37-65`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (junto a `automation/resume` `:213-219` y `:491-495`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:775-776` (registro junto a `PauseAutomationHandler`)
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs:44-50`, `src/Api/ApiExceptionHandler.cs:41-42, 147-174`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ContactBlockApiTests.cs` (nuevo, `[Collection(MessagingLoadGroup.Name)]`, fixture como `ConversationAutomationApiTests.ArrangeAsync` con el entrante `MetaPayloads.Inbound("111", "CO.1", "573001234567", …)`)

**Interfaces:**
- Consumes: Tasks 1 a 5.
- Produces:
  - `sealed record BlockContactCommand(Guid TenantId, Guid ConversationId) : ICommand<ConversationSummary>`; `sealed record UnblockContactCommand(Guid TenantId, Guid ConversationId) : ICommand<ConversationSummary>`; `BlockContactHandler`, `UnblockContactHandler` (dependencias: `ConversationAssignmentSteps`, `IMessagingConnectionDirectory`, `IWhatsAppCloudClient`, `ILogger<…>`).
  - **El estado propio va primero**, antes que toda validación y que Meta (spec §3.3): bloquear una ya bloqueada o desbloquear una que no lo está → 200 con el estado actual, **sin llamar a Meta y sin evento**. Es lo que evita depender de los códigos de «ya bloqueado» / «no estaba» de Meta, que no se verifican.
  - Orden de `BlockContactHandler`: `steps.LoadAsync` (permiso y módulo → 404 → membresía) → **si `IsBlocked`, `steps.CommitAsync(…, [], …)`** (200 sin Meta) → `EnsureCanBlock(now)` → `ResolveSenderAsync` (`null` → 422 `ConnectionUnavailable`) → `BlockUserAsync(sender, WaId, cancellationToken)` → `Classify`: `WindowClosed` → 422 `BlockWindowClosed`; `ConnectionRejected` → `ConnectionRejection.ReportAsync` y 422 `ConnectionUnavailable`; `Failed` → `LogContactBlockFailed` y `UpstreamServiceException(BlockFailed)`; `Applied` → guardar con `CancellationToken.None`. `UnblockContactHandler`: igual, sin `EnsureCanBlock` ni ventana, con `BlockedWaId`, `!IsBlocked` → 200 sin Meta, y además `UnblockRejected` → `LogContactUnblockRejected` y 422 `UnblockRejected` (sigue bloqueada).
  - Logs (`[LoggerMessage]`, el estilo de `WhatsAppCloudClient.LogAnswered` `:43-44`): `LogContactBlockFailed(logger, direction, tenantId, conversationId, code, subcode, fbTraceId)` y `LogContactUnblockRejected(logger, tenantId, conversationId, fbTraceId)`, los dos en Warning, con los campos de `ContactBlockAnswer`. **Nunca** el token ni el número: el cliente de Meta tampoco los registra hoy (`WhatsAppCloudClient.cs:14-16`), así que no hay número que enmascarar; `conversationId` alcanza para encontrarlo.
  - `Task<ConversationSummary> ConversationAssignmentSteps.CommitWithRetryAsync(Guid tenantId, Conversation conversation, Func<Conversation, ConversationEvent?> apply, DateTimeOffset now, CancellationToken cancellationToken)`; `public const int ConflictAttempts = 3`. Aplica, y ante `RequestConcurrencyException`: `DiscardPendingChanges()`, recarga por `IConversationRepository.FindAsync` y vuelve a aplicar; `apply` que devuelve `null` (otro request ganó) termina sin evento. Agotados los intentos, sube el 412 (nunca se deshace en Meta: spec §3.2).
  - `MessagingAuditActions.ContactBlocked = "messaging.conversation.contact_blocked"`, `ContactUnblocked = "messaging.conversation.contact_unblocked"`, y sus brazos en `ForEvent`.
  - `sealed class UpstreamServiceException(string code, string message) : Exception(message)` con `Code` → `ApiExceptionHandler` `(502, "Bad gateway", code)`, registrada como falla de request (como el 503), no como excepción sin manejar.
  - Rutas `POST …/conversations/{conversationId:guid}/block` y `/unblock`: `RequireAuthorization(MessagingPermissions.ConversationManage)`, `Produces<ConversationSummary>()`, `ProducesProblem(403, 404, 422, 502)`; sin `RequireVersion`.

- [ ] **Step 1: RED.** `ContactBlockApiTests` (Meta con `f.Factory.MetaHandler.Respond("/block_users", …)`; el conteo de Meta es `MetaHandler.Requests` con path `block_users`):
  - `BlockingCallsMetaAndMarksTheConversation` — 200; `blocked.blockedAt` no nulo, `blocked.blockedByName` = el nombre del owner; un request `POST …/111/block_users` con `"+573001234567"`; `blocked_wa_id = '573001234567'`; `status`, `assigned_member_id`, `unread_count`, `automation_status` iguales a antes; un evento `ContactBlocked` (`details->>'type'`), una auditoría `messaging.conversation.contact_blocked`; `version` +1.
  - `BlockingAgainAnswersTheStateWithoutMetaEvenWithTheWindowClosed` — bloquear; `UPDATE … SET last_inbound_at = now() - interval '25 hours'`; bloquear → 200 con `blocked` no nulo; un solo request a Meta; eventos y auditorías siguen en 1.
  - `UnblockingSendsTheBlockedNumberAfterANumberChange` — bloquear; `UPDATE … SET wa_id = '573009999999'`; desbloquear → 200, `blocked` `null`; el `DELETE` lleva `"+573001234567"`; evento `ContactUnblocked`; auditoría `contact_unblocked`.
  - `UnblockingIgnoresTheWindow` — bloquear; ventana a 25 h; desbloquear → 200.
  - `UnblockingWhatIsNotBlockedAnswersWithoutMeta` — 200, `blocked` `null`, cero requests, cero eventos.
  - `TheChecksBeforeMetaHaveTheirCodes` (`[Theory]`): `no-wa-id` (entrante con `waId: null`) → 422 `messaging.contact.block_unsupported_channel`; `window-closed` → 422 `messaging.contact.block_window_closed`; `connection-paused` → 422 `messaging.connection_unavailable`. En los tres: cero requests a Meta, `blocked_at` nulo, cero eventos. (Instagram lo cubre la Task 1.)
  - `MetaAnswersHaveTheirCodes` (`[Theory]`): 200 `failed_users` `131047` → 422 `block_window_closed`; 400 `131047` → 422 `block_window_closed`; 400 `130429` → 502; 500 → 502; 400 `190` → 422 `messaging.connection_unavailable` y la conexión queda `NeedsAttention` (el mismo assert que la prueba de `190` de `SendMessageApiTests`). En todos: `blocked_at` nulo, cero eventos, cero auditorías.
  - `AnUnrecognizedMetaCodeIs502BlockFailed` (`[Theory]`, las dos direcciones): bloquear con 200 `failed_users` `139100`; desbloquear (bloqueada antes) con 200 `failed_users` `139100` → **502**, `code == "messaging.contact.block_failed"`; el estado no cambia (`blocked_at` nulo o el de antes), cero eventos y auditorías nuevos. El contrato es el 502 con su código; el log no se aserta.
  - `UnblockingRejectedByMetaIsUnblockRejected` (`[Theory]`): bloquear; desbloquear con 200 `failed_users` `131047` y con 400 `131047` → **422** `messaging.contact.unblock_rejected` (no `block_window_closed`); sigue bloqueada (`blocked_at` igual), cero eventos `ContactUnblocked`, cero auditorías `contact_unblocked`.
  - `AConflictAfterMetaAcceptedIsRetriedAndBlocksOnce` — `MetaHandler.RespondWith("/block_users", _ => { UPDATE … SET version = version + 1; return Json(200, added_users) })` → 200, bloqueada, un evento, una auditoría.
  - `TheListAndDetailCarryBlockedAsAnExplicitNull` — `GET /conversations` y `GET /conversations/{id}`: la propiedad `blocked` existe y su `ValueKind` es `Null`.
  - `WithoutManageItIsForbidden` — cliente con sólo `ConversationRead` → 403 en los dos; cero requests.
  - `AnUnknownConversationIs404` — `messaging.conversation.not_found`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ContactBlockApiTests"
  ```
  Expected: FAIL de compilación (`BlockContactCommand` no existe) y, ya compilando, 404 en las rutas.
- [ ] **Step 2: GREEN.** Implementar lo de **Interfaces** y registrar los dos handlers.
- [ ] **Step 3:** el filtro, más las clases que comparten `ConversationAssignmentSteps` y la auditoría.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ContactBlockApiTests|FullyQualifiedName~ConversationAutomationApiTests|FullyQualifiedName~AssignmentApiTests"
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(messaging): bloquear y desbloquear un contacto desde la conversación (spec 2026-10-26 §3.2, §3.3)`.

### Task 7: Guardas del envío humano (spec §3.4)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/OutboundGuards.cs:74-88`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:126-129` (`OpenConversation(…, bool blocked = false)` → `BlockedAt`/`BlockedByMemberId` en la fila)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SendMessageHandlerTests.cs:386-411`, `SendTemplateMessageHandlerTests.cs`, `StartConversationHandlerTests.cs`

**Interfaces:**
- Consumes: `ConversationRow.BlockedAt` (Task 2), `MessagingErrorCodes.ContactBlocked` (Task 1).
- Produces: `AdmitAsync` (texto, adjuntos, audio) y `AdmitTemplateAsync` (plantillas e iniciar) lanzan `MessagingDomainException(ContactBlocked)` **primero**, antes de `Status` y de la ventana: bloqueada y resuelta, reabrir no alcanza (spec §4.3). El chequeo de «ya salió» (`FindAlreadySentAsync`, paso 5) sigue antes: un reintento de algo ya enviado devuelve lo enviado.

- [ ] **Step 1: RED.**
  - `SendMessageHandlerTests.TheChecksBeforeMetaHaveTheirCodes`: suma `[InlineData("blocked", MessagingErrorCodes.ContactBlocked)]` y `[InlineData("blocked-and-resolved", MessagingErrorCodes.ContactBlocked)]`; los asserts existentes (`Meta.Sends` vacío, sin reclamo) valen igual.
  - `SendTemplateMessageHandlerTests.ATemplateToABlockedContactIsRefusedBeforeTheClaim` — ventana cerrada y bloqueada → `ContactBlocked`, `TemplateSends` vacío, sin reclamo.
  - `StartConversationHandlerTests.AnExistingBlockedConversationRefusesTheTemplateWithoutMeta` — como `ACustomerWhoAlreadyHasAConversationOnThatConnectionGetsTheSendThereWith200` (`:60`), con la existente bloqueada → `ContactBlocked`, `TemplateSends` vacío.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SendMessageHandlerTests|FullyQualifiedName~SendTemplateMessageHandlerTests|FullyQualifiedName~StartConversationHandlerTests"
  ```
  Expected: FAIL en las aserciones (sale `ConversationNotOpen`/`WindowClosed` o se envía).
- [ ] **Step 2: GREEN.** El chequeo en `AdmitAsync`.
- [ ] **Step 3:** `Modules.Messaging.UnitTests` completo → PASS.
- [ ] **Step 4: Commit** `feat(messaging): ningún envío humano sale a un contacto bloqueado (spec 2026-10-26 §3.4)`.

### Task 8: El chatbot no le escribe a un bloqueado (spec §3.4)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BotOutbound.cs:10-24` (`BotSendResult.Blocked`), `:66-74` (la guarda)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:251-263` (`BotSendOutcome.Blocked`)
- Modify: `src/Bootstrapper/AutomationMessenger.cs:50-59`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:30-32` (log nuevo), `:213-235` (`StopAsync`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/BotOutboundTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs:251-275`, `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.cs`

**Interfaces:**
- Consumes: `ConversationRow.BlockedAt` (Task 2).
- Produces:
  - `sealed record BotSendResult.Blocked : BotSendResult` — después de «ya salió» (`FindByClientIdAsync`) y **antes** de la guarda de `NotAutomated`: sin reclamo, sin fila, sin Meta.
  - `sealed record BotSendOutcome.Blocked : BotSendOutcome`; `AutomationMessenger.Map`: `BotSendResult.Blocked → BotSendOutcome.Blocked`.
  - `RunEffects.StopAsync`: `case BotSendOutcome.Blocked` → `LogContactBlocked(logger, tenantId, conversationId, runId)` (Information, sin número) y `run.Close(state, RunStatus.Interrupted, null, now)`; **nunca** `FailAsync`, `HandoffAsync` ni `EndAsync` (`AutomationStatus` no cambia).

- [ ] **Step 1: RED.**
  - `AutomationAdaptersTests.TheMessengerMapsEveryBotSendResult`: suma `(new BotSendResult.Blocked(), new BotSendOutcome.Blocked())`.
  - `RunProcessorTests.ABlockedSendInterruptsTheRunWithoutHandingOff` — `RecordingMessenger.Reply = _ => new BotSendOutcome.Blocked()` → `run.Status == RunStatus.Interrupted`, `FailureCode == null`, el doble de conversaciones sin `HandoffAsync` ni `EndAsync`, sin auditoría `automations.run.failed`.
  - `BotOutboundTests.ABlockedConversationIsNotSentAndLeavesNoRow` — conversación del bot activa y bloqueada por SQL → `IsType<BotSendResult.Blocked>`, cero requests a Meta, cero filas salientes.
  - `EngineEndToEndTests.ABlockBetweenClaimAndSendInterruptsTheRunSilently` — como `AnAdvisorTakingBetweenClaimAndSendInterruptsTheRun` (`:256`), con `f.Meta.BeforeAnswer` que escribe `blocked_at`, `blocked_by_member_id`, `blocked_wa_id` y `version + 1` → un solo envío, `"Interrupted|"`, `automation_status` sigue `Active`, cero eventos `AutomationPaused`, cero auditorías `automations.run.failed`.
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AutomationAdaptersTests"
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessorTests"
  ```
  Expected: FAIL de compilación (`BotSendResult.Blocked`, `BotSendOutcome.Blocked` no existen).
- [ ] **Step 2: GREEN.** Implementar lo de **Interfaces**.
- [ ] **Step 3:** los dos unitarios de arriba y las dos clases de integración.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~BotOutboundTests"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~EngineEndToEndTests"
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(automations): el chatbot no le escribe a un contacto bloqueado (spec 2026-10-26 §3.4)`.

### Task 9: Cierre

**Files:** ninguno nuevo (handoff en el reporte).

- [ ] **Step 1: Suites unitarias, arquitectura y build.**
  ```powershell
  dotnet build Backend.slnx
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests
  ```
  Expected: build sin warnings nuevos; todo PASS.
- [ ] **Step 2: Integración filtrada** (la suite entera tarda decenas de minutos; sólo si el owner la pide):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ContactBlockApiTests|FullyQualifiedName~MessagingPersistenceTests|FullyQualifiedName~BotOutboundTests|FullyQualifiedName~SendMessageApiTests|FullyQualifiedName~ConversationListApiTests|FullyQualifiedName~ThreadEventsApiTests"
  ```
  Expected: PASS.
- [ ] **Step 3: Prueba manual desde la interfaz** (la corre el developer con backend y frontend de la rama; **sin `curl`**, nunca un agente). Con un teléfono personal y el número de WhatsApp del negocio conectado:
  - [ ] (a) Desde el teléfono, escribirle al número del negocio. La conversación aparece en Mensajería.
  - [ ] (b) En QEP, bloquear al contacto: el diálogo avisa que sus mensajes se descartan; al confirmar aparece la insignia «Bloqueado».
  - [ ] (c) Desde el teléfono, mandar otro mensaje: no aparece nada en la bandeja, no sube el contador de no leídos, no suena y el chatbot no contesta.
  - [ ] (d) Al día siguiente, con la ventana de 24 h vencida, desbloquear desde QEP: **tiene que funcionar** (la insignia desaparece). Si sale el mensaje de `messaging.contact.unblock_rejected`, se anota como `DECISIÓN-PENDIENTE` (Meta exige ventana para desbloquear) y se escala al owner; no se mergea hasta que decida.
  - [ ] (e) Desde el teléfono, escribir otra vez: el mensaje entra.
  - [ ] (f) Con el contacto ya bloqueado en Meta pero desbloqueado en QEP (o al revés), repetir la acción y anotar del log `LogContactBlockFailed` el código, subcódigo y fbtrace; con eso se mapean esos códigos a `Applied` en `ContactBlockResponse` en un cambio aparte.
  El resultado de cada paso (ok / qué se vio, con fecha) queda anotado en este plan y en el handoff. Si algún paso falla con `502 block_failed`, se anota el código, subcódigo y fbtrace del log de la Task 6.
- [ ] **Step 4: Handoff** con: el resultado de la prueba manual del Step 3 (o `DECISIÓN-PENDIENTE` si (d) falló), salidas RED/GREEN de cada tarea, commits, y los pendientes del spec §5 (Instagram, cotizaciones por WhatsApp, campañas) más el aviso al plan de frontend de que el hilo trae `ContactBlocked`/`ContactUnblocked` y que existe `messaging.contact.unblock_rejected`.
