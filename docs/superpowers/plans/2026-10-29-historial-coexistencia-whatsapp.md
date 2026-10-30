# WhatsApp coexistence history import — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a tenant connects a number by WhatsApp coexistence, QEP requests the WhatsApp Business app history and contacts from Meta, ingests `history`, `smb_message_echoes` and `smb_app_state_sync` without generating work, and exposes the sync state (and a retry) to the SPA.

**Architecture:** Integrations publishes `integrations.whatsapp-coexistence-connected.v1` to the platform outbox in the same transaction that creates the connection. Messaging consumes it (new inbox, copy of Pipeline's `ClaimedOutboxConsumer`) and creates a `messaging.history_syncs` row in `Pending`; a Messaging worker claims `Pending` rows and calls `POST /{phone_number_id}/smb_app_data` with the connection token (through `IMessagingConnectionDirectory`). The webhook parser learns three fields; their ingestion is a separate path from `InboundIngestion.IngestOnceAsync` (no unread from live, no window, no bot, no realtime, no opt-out). Customers gains a phone-only ensure. Two BFF endpoints in Messaging serve the state and the retry.

**Tech Stack:** .NET 10, EF Core + Npgsql (raw SQL where the module already uses it), xUnit v3, Testcontainers PostgreSQL, Minimal APIs.

**Spec:** `docs/superpowers/specs/2026-10-29-historial-coexistencia-whatsapp-design.md` (authority; §9 retired decisions D-1, D-2, D-8, D-11, D-12 and V-7 are ignored). Frontend (§10) is a separate slice in `qep-frontend`; this plan only fixes the HTTP contract it consumes (Task 12).

## Plan decisions

- **P-1 (spec D-3) — outbox event + `Pending` row + worker.** The coexistence signup writes an outbox event in the same `SaveChangesAsync` as the connection and its route (`IConnectionEventPublisher`, which already writes `platform.outbox_messages` from `IntegrationsDbContext`). Messaging consumes it with an inbox claim and inserts `history_syncs` in `Pending`; a separate worker sends the two requests. Rationale: it is the existing cross-module pattern (Pipeline/Shopify consumers), it needs no new port from Integrations into Messaging, the event commits atomically with the route (so the history can never arrive before the route, spec §3), and a crash at any point leaves either an unprocessed outbox message or a `Pending` row — both are picked up again. A `Pending` row written by Integrations was rejected: the state lives in Messaging (D-4) and a cross-`DbContext` write is not one transaction.
- **P-2 — order of requests:** `smb_app_state_sync` first, then `history` (spec §3 order). Each type has its own state; one failing does not block the other.
- **P-3 — unconfirmed request (timeout, 5xx, network):** the type stays `Pending` with `last_error_code = "unconfirmed"` and is retried when the claim expires. Only a Graph error body moves it to `Failed` (D-9). If Meta rejects the second attempt because the first was in fact accepted, it ends `Failed` with that Graph code — visible to the tenant, never silently lost.
- **P-4 — "created by the import" marker:** new column `conversations.history_managed boolean NOT NULL DEFAULT false`. It is `true` only on a conversation inserted by history ingestion; a live inbound, `Conversation.Resolve` and `Conversation.Reopen` set it to `false`. The open/closed rule (spec §4.1) runs only while it is `true`.
- **P-5 — conversation created by an echo:** legacy row (`user_id NULL`), `Open`, `history_managed = false` (an echo is live traffic, not the import).
- **P-6 — media placeholder:** stored as `Kind = Unsupported` with `details = {"historyMedia":"placeholder"}` and no `message_media` row. If the same `wamid` later arrives with a real media type and id (assumed shape of V-2), ingestion attaches `message_media` and updates `kind`. `MessageDto.MediaState` (Task 11) tells the SPA `Pending` or `Unavailable`.
- **P-7 — contacts that match an existing customer** are not modified, except an `Incomplete` customer whose name is still the phone fallback, which takes the contact's `full_name` (spec §4.3 "crea o actualiza", without clobbering a name a person set). `action = "remove"` is ignored (D-7).
- **P-8 — endpoint permissions:** read reuses the string `integrations.connection.read`, retry reuses `integrations.connection.manage` (spec §5). Messaging cannot reference Integrations, so the strings are copied into a new `MessagingConnectionPermissions` class and tied by a parity test in `Bootstrapper.UnitTests`. Their policies already exist (registered for Integrations), so `AddAuthorization` needs nothing new. The handlers check tenant + permission but **not** the Messaging module guard: the sync belongs to the connection screen, and D-5 processes the history with the module off.

## Global Constraints

- Backend only; no `qep-frontend` changes. `docs/estado.md` is not touched on this branch.
- Layers verified by `tests/ArchitectureTests`: Messaging does not reference Integrations or Customers; Customers does not reference Messaging; adapters live in `src/Bootstrapper`.
- No new configuration keys (spec §6). `Meta:App:*` untouched.
- Status HTTP mapping only through `src/Api/ApiExceptionHandler.cs`; domain errors are codes (`DomainException` → 422).
- Tenant isolation: route carries `{tenantId:guid}`; handler revalidates tenant and permission; another tenant → 403 `authorization.denied`.
- DB error translation in Infrastructure, by index name.
- Every raw `INSERT` into `messaging.conversations` / `messaging.messages` lists the columns the existing ingestion lists (`IngestEchoAsync`, `ConversationOpener.OpenByPhoneAsync`).
- Message dedupe is `ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING` (`IX_messages_connection_wamid`).
- `history`, `smb_message_echoes`, `smb_app_state_sync` never: increment unread from live logic, set `last_inbound_at`, emit `Reopened`, notify realtime, mark marketing opt-out, assign a member, enqueue `messaging.automation_inbox`.
- Code comments and user-facing text in Spanish with tuteo (repo rule); identifiers, codes and enum names in English.
- Never log a token, phone number or message text.
- Commits: conventional, no AI attribution. Check `git branch --show-current` = `feature/historial-coexistencia` before each commit.
- Integration tests on this machine need `$env:Seed__Enabled = "false"`. Stop `Api.exe` / `dotnet Api.dll` before building.
- Test economy: each task runs only its own test classes; the full suite runs once, in Task 13.

## Review Focus

1. **Unconfirmed `smb_app_data` (timeout/5xx):** the person expects the import not to be lost silently → type stays `Pending` with `unconfirmed` and is retried; a later Graph rejection lands in `Failed`. Test: Task 5 `AnUnconfirmedRequestStaysPendingAndIsRetried`.
2. **`threads[].id` not plain digits** (`+57…`, spaces): expected to match the same conversation as the live `wa_id` → normalized to digits; an id that is still not 1–20 digits skips only that thread. Test: Task 6 `AThreadIdIsNormalizedToDigitsAndAnInvalidOneIsSkipped`.
3. **An advisor resolves or reopens an imported conversation, then another chunk arrives:** expected to keep what the advisor did → `Resolve`/`Reopen` clear `history_managed`. Test: Task 9 `AnAdvisorActionStopsTheImportRule`.
4. **The same `wamid` already exists (echo or live) when the history brings it:** expected one message, the existing one untouched. Test: Task 8 `AMessageAlreadyIngestedIsNotDuplicatedOrOverwritten`.
5. **A malformed message inside a valid chunk** (missing timestamp, unknown type): expected the rest of the chunk and its progress to be recorded → the message is skipped (no timestamp) or `Unsupported` (unknown type). Test: Task 6 `AMessageWithoutTimestampIsSkippedAndTheChunkKeepsItsProgress`.

**Unverified fixture fields (spec V-1, V-2):** `history[].metadata.{phase,chunk_order,progress}` location; `history_context.status` values; where `errors[]` sits on a declined `history` (`value.errors` vs `value.history[].errors` — the parser accepts both); the `media_placeholder` object's fields; the shape of the later media webhook (P-6 assumes same `wamid` with a real `type`); whether `history` carries a contact name; `smb_app_state_sync.state_sync[].contact.phone_number` format (with or without `+`). Fixtures in `MetaPayloads.cs` carry a `// V-1: unverified` comment on each of these.

---

### Task 1: Integrations publishes the coexistence event

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionEvents.cs` (new constant)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/CompleteWhatsAppSignup.cs:95-110,225-232`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/CompleteWhatsAppSignupHandlerTests.cs` (use the `RecordingEventPublisher` double the other Integrations tests use)
- Test: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/EmbeddedSignupApiTests.cs`

**Interfaces:**
- Produces: `ConnectionEvents.WhatsAppCoexistenceConnected = "integrations.whatsapp-coexistence-connected.v1"`. Payload is the existing `ConnectionEventPayload` JSON: `{ "tenantId", "connectionId", "providerKey", "occurredAt" }` — `occurredAt` is the connection time (spec §5 "hora de la conexión"). Task 3 reads it by these names.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task ACoexistenceSignupPublishesTheConnectedEventOnce()
// arrange coexistence command (IsCoexistence = true) with FakeWhatsAppSignupGateway returning one number
// assert: publisher.Published == [(ConnectionEvents.WhatsAppCoexistenceConnected, connection.Id, now)]

[Fact] public async Task ARegularSignupPublishesNoEvent()
// assert: publisher.Published is empty

[Fact] public async Task ACoexistenceSignupLeavesTheEventInTheOutbox()   // EmbeddedSignupApiTests
// after POST of the coexistence signup: SELECT count(*) FROM platform.outbox_messages
//   WHERE event_name = 'integrations.whatsapp-coexistence-connected.v1' AND payload->>'connectionId' = @id  == 1
```

- [ ] **Step 2: Run them to verify they fail**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~CompleteWhatsAppSignupHandlerTests"
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~EmbeddedSignupApiTests"
```
Expected: FAIL (`WhatsAppCoexistenceConnected` not defined).

- [ ] **Step 3: Implement**

Add `IConnectionEventPublisher eventPublisher` to `CompleteWhatsAppSignupHandler`'s constructor; when `command.IsCoexistence`, call `eventPublisher.Publish(ConnectionEvents.WhatsAppCoexistenceConnected, connection, now)` after `ConnectionAudit.ByMember(...)` and **before** `unitOfWork.SaveChangesAsync` (same transaction). Update the existing `ConnectionEvents` summary ("Sin consumidores") — this event has one: Messaging.

- [ ] **Step 4: Run the same commands** — Expected: PASS.

- [ ] **Step 5: Commit** — `feat(integrations): publish coexistence-connected event with the connection`

---

### Task 2: Messaging persistence — sync state, inbox, outbox projection, `history_managed`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/HistorySync.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingInbox.cs` (copy of `PipelineInbox.cs`: `MessagingInboxMessage`, `MessagingOutboxRecord`, `MessagingInboxClaims.TryClaimAsync` against `messaging.inbox_messages`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (`ConfigureHistorySync`, `ConfigureInbox`, `ConfigureOutboxProjection` copied from `PipelineDbContext`, conversation property `HistoryManaged`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs:249-270` (`HistoryManaged` property; `Resolve` and `Reopen` set it `false`)
- Create: migration `AddHistorySync` in `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/HistorySyncTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HistorySyncPersistenceTests.cs`

**Interfaces:**
- Produces (Domain):

```csharp
public enum HistorySyncType { SmbAppStateSync, History }               // Meta sync_type: "smb_app_state_sync", "history"
public enum HistorySyncRequestState { Pending, Requested, Failed }
public enum HistorySyncStatus { Pending, Requested, InProgress, Completed, Declined, Failed, Expired }

public sealed class HistorySync
{
    public static readonly TimeSpan RequestWindow = TimeSpan.FromHours(24);
    public const int RequestIdMaxLength = 128;
    public const int ErrorCodeMaxLength = 64;
    public Guid ConnectionId { get; }               // PK
    public Guid TenantId { get; }
    public DateTimeOffset ConnectedAt { get; }
    public DateTimeOffset Deadline => ConnectedAt + RequestWindow;
    public HistorySyncRequestState ContactsState { get; }  public string? ContactsRequestId { get; }  public DateTimeOffset? ContactsRequestedAt { get; }  public string? ContactsErrorCode { get; }
    public HistorySyncRequestState HistoryState { get; }   public string? HistoryRequestId { get; }   public DateTimeOffset? HistoryRequestedAt { get; }   public string? HistoryErrorCode { get; }
    public int? HistoryPhase { get; }  public int? HistoryProgress { get; }   // highest seen
    public DateTimeOffset? DeclinedAt { get; }  public DateTimeOffset? CompletedAt { get; }
    public DateTimeOffset UpdatedAt { get; }  public long Version { get; }

    public static HistorySync Start(Guid tenantId, Guid connectionId, DateTimeOffset connectedAt, DateTimeOffset now);
    public HistorySyncStatus StatusAt(DateTimeOffset now);
    public bool CanRetryAt(DateTimeOffset now);
    public void Retry(DateTimeOffset now);           // throws DomainException(MessagingErrorCodes.HistorySyncRetryNotAllowed) if !CanRetryAt(now)
}
```

`StatusAt` precedence (first match): `Declined` (DeclinedAt set) → `Completed` (CompletedAt set) → `InProgress` (HistoryPhase or HistoryProgress set) → `Expired` (now ≥ Deadline and any type ≠ `Requested`) → `Failed` (any type `Failed`) → `Pending` (any type `Pending`) → `Requested`.
`CanRetryAt` = DeclinedAt is null and now < Deadline and any type ∈ {`Pending`, `Failed`}.
`Retry` moves every `Failed` type to `Pending`, clears its error code, bumps `Version`, sets `UpdatedAt`.
- New error code: `MessagingErrorCodes.HistorySyncRetryNotAllowed = "messaging.history_sync.retry_not_allowed"`, `MessagingErrorCodes.HistorySyncNotFound = "messaging.history_sync.not_found"`.
- Table `messaging.history_syncs`: `connection_id uuid PK`, `tenant_id uuid NOT NULL`, `connected_at`, `contacts_state varchar(16)`, `contacts_request_id varchar(128)`, `contacts_requested_at`, `contacts_error_code varchar(64)`, the same four for `history_*`, `history_phase smallint`, `history_progress smallint`, `declined_at`, `completed_at`, `claimed_until timestamptz NULL`, `attempts int NOT NULL DEFAULT 0`, `created_at`, `updated_at`, `version bigint`. Checks: `CK_history_syncs_contacts_state` / `CK_history_syncs_history_state` `IN ('Pending','Requested','Failed')`, `CK_history_syncs_progress` `history_progress BETWEEN 0 AND 100`. Index `IX_history_syncs_tenant` on `tenant_id`; partial `IX_history_syncs_pending` on `claimed_until` `WHERE contacts_state = 'Pending' OR history_state = 'Pending'`.
- Table `messaging.inbox_messages` (`consumer varchar(100)`, `message_id uuid`, `processed_at`, `claimed_until`, `attempts`; PK `(consumer, message_id)`), as Pipeline's.
- `platform.outbox_messages` mapped read-only with `ExcludeFromMigrations`, as Pipeline's projection.
- Column `messaging.conversations.history_managed boolean NOT NULL DEFAULT false` (map with default + sentinel like `channel` so EF always writes it).

- [ ] **Step 1: Write the failing tests**

```csharp
// HistorySyncTests (unit)
[Fact] public void ANewSyncIsPendingWithTheDeadline24HoursAfterTheConnection()
// Start(t, c, connectedAt: T, now: T) → StatusAt(T) == Pending; Deadline == T + 24h; both states Pending
[Theory] StatusPrecedence(...)   // one row per precedence step above, e.g. Declined beats Completed; Expired beats Failed after the deadline
[Fact] public void RetryMovesFailedToPendingOnlyBeforeTheDeadline()
// Failed history at T+1h → Retry → HistoryState == Pending, HistoryErrorCode == null
// at T+24h → CanRetryAt false; Retry throws DomainException with Code == "messaging.history_sync.retry_not_allowed"
[Fact] public void ResolveAndReopenClearHistoryManaged()   // ConversationTests style, on Conversation

// HistorySyncPersistenceTests (integration, migrated schema)
[Fact] public async Task ASyncRoundTripsWithItsStatesAndVersion()
[Fact] public async Task TheProgressCheckRejectsAValueAbove100()   // raw UPDATE history_progress = 101 → PostgresException 23514
```

- [ ] **Step 2: Run them to verify they fail**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~HistorySyncTests|FullyQualifiedName~ConversationTests"
```
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement the domain, the mappings, then generate the migration**

```powershell
dotnet ef migrations add AddHistorySync --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
```
Check the generated `Up` creates `history_syncs`, `inbox_messages`, adds `history_managed`, and does **not** touch `platform.outbox_messages`.

- [ ] **Step 4: Run tests** — unit command above plus:

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~HistorySyncPersistenceTests|FullyQualifiedName~MessagingPersistenceTests"
```
Expected: PASS.

- [ ] **Step 5: Commit** — `feat(messaging): add history sync state, inbox and history_managed column`

---

### Task 3: Messaging consumes the coexistence event

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/HistorySync/ClaimedOutboxConsumer.cs` (copy of Pipeline's, over `MessagingDbContext` / `MessagingInboxClaims`)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/HistorySync/CoexistenceEventsProcessor.cs`, `CoexistenceEventsWorker.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs` (register processor singleton + `AddHostedService<CoexistenceEventsWorker>()`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CoexistenceEventsTests.cs`; add `DrainCoexistenceEventsAsync(host)` to `MessagingApiHarness.cs` (same shape as `DrainDeliveriesAsync`)

**Interfaces:**
- Consumes: Task 1 event name and payload; Task 2 `HistorySync.Start`, `messaging.inbox_messages`.
- Produces: `CoexistenceEventsProcessor.Consumer = "messaging.coexistence-events"`; `CoexistenceEventsWorker.DrainAsync(CancellationToken)`; poll interval 10 s; retry delays `[5 s, 30 s, 5 min]` (same curve as `QuotationEventsProcessor`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task TheEventCreatesAPendingSyncWithTheConnectionTime()
// insert into platform.outbox_messages the row IntegrationsEventPublisher writes (event_name, payload with tenantId/connectionId/providerKey/occurredAt = T)
// DrainCoexistenceEventsAsync → history_syncs row: tenant_id, connected_at == T, contacts_state = history_state = 'Pending'
[Fact] public async Task ProcessingTheSameEventTwiceKeepsOneRow()
// delete the inbox row and drain again → count(history_syncs) == 1, connected_at unchanged (INSERT … ON CONFLICT (connection_id) DO NOTHING)
[Fact] public async Task AMalformedPayloadIsMarkedProcessedWithoutARow()
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~CoexistenceEventsTests"` → FAIL.

- [ ] **Step 3: Implement** — insert and `MarkProcessedAsync` in the same `SaveChanges`; read the payload by property names `tenantId`, `connectionId`, `occurredAt` (case as written by `IntegrationsEventPublisher`).

- [ ] **Step 4: Run the same command** → PASS.

- [ ] **Step 5: Commit** — `feat(messaging): consume coexistence-connected event into a pending history sync`

---

### Task 4: Graph call `smb_app_data`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs`
- Modify: test doubles implementing `IWhatsAppCloudClient` (`FakeWhatsAppClient` and any other the compiler reports)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientSmbAppDataTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>POST /{phone_number_id}/smb_app_data con { messaging_product: "whatsapp", sync_type }. Devuelve el request_id.</summary>
Task<MessagingGraphResult<string>> RequestSmbAppDataAsync(MessagingSender sender, HistorySyncType type, CancellationToken cancellationToken);
```
`HistorySyncType.SmbAppStateSync` → `"smb_app_state_sync"`, `HistorySyncType.History` → `"history"`. A failure with an HTTP response carries `HttpStatus` and Graph `Code`; no response (timeout, network) → `MessagingGraphFailure(HttpStatus: 0, Code: null, Reason: "unconfirmed")`; a 5xx → `Reason: "unconfirmed"` as well (P-3).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task ItPostsTheSyncTypeAndReturnsTheRequestId()
// handler asserts POST …/{phoneNumberId}/smb_app_data, Bearer token, body JSON {"messaging_product":"whatsapp","sync_type":"history"}
// response 200 {"request_id":"REQ-1"} → Succeeded, Value == "REQ-1"
[Fact] public async Task AGraphErrorKeepsItsCode()          // 400 GraphError(code: 100) → Failure.Code == 100, Reason != "unconfirmed"
[Fact] public async Task ATimeoutOrA5xxIsUnconfirmed()      // → Failure.Reason == "unconfirmed"
[Fact] public async Task A2xxWithoutRequestIdIsUnconfirmed()
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WhatsAppCloudClientSmbAppDataTests"` → FAIL.
- [ ] **Step 3: Implement** with the same request helper the client uses for `BlockUserAsync`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): request smb_app_data from Graph`

---

### Task 5: Request worker

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/HistorySync/HistorySyncRequestProcessor.cs`, `HistorySyncRequestWorker.cs`
- Modify: `MessagingInfrastructureExtensions.cs` (hosted service)
- Modify: `MessagingApiHarness.cs` (`DrainHistorySyncRequestsAsync(host)`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HistorySyncRequestTests.cs`

**Interfaces:**
- Consumes: Task 2 table, Task 3 consumer, Task 4 `RequestSmbAppDataAsync`, `IMessagingConnectionDirectory.ResolveSenderAsync`.
- Produces: `HistorySyncRequestWorker.DrainAsync(CancellationToken)`; poll 10 s; lease `TimeSpan.FromMinutes(2)`; batch 10.

Claim: one statement `UPDATE messaging.history_syncs SET claimed_until = @now + interval '2 minutes', attempts = attempts + 1 WHERE connection_id IN (SELECT connection_id FROM messaging.history_syncs WHERE (contacts_state = 'Pending' OR history_state = 'Pending') AND connected_at + interval '24 hours' > @now AND (claimed_until IS NULL OR claimed_until < @now) AND declined_at IS NULL ORDER BY connected_at LIMIT 10 FOR UPDATE SKIP LOCKED) RETURNING connection_id, tenant_id`. Per row: sender `null` → each `Pending` type keeps `Pending` with error `connection_unavailable`; else request each `Pending` type in P-2 order: success → `Requested` + request id + requested_at; Graph error → `Failed` + `graph:{code}` (or `graph:{httpStatus}` without code); unconfirmed → `Pending` + `unconfirmed`. Save after each type (a crash between the two requests does not repeat the first). Never log the token or the request id body.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task APendingSyncRequestsContactsThenHistory()
// FakeMetaGraphHandler: smb_app_data → 200 {"request_id":"R-C"} then {"request_id":"R-H"}
// Requests: two POSTs to /{phoneNumberId}/smb_app_data, sync_type smb_app_state_sync then history
// row: contacts_state 'Requested', contacts_request_id 'R-C', history_request_id 'R-H'
[Fact] public async Task ACommittedEventIsRequestedEvenIfNothingRanAfterTheCommit()
// spec §8 "sobrevive a una caída": only the outbox row exists (as Task 3 inserts it) → DrainCoexistenceEventsAsync, DrainHistorySyncRequestsAsync → both Requested
[Fact] public async Task AGraphRejectionLeavesTheTypeFailedWithItsCode()      // 400 code 100 → history_state 'Failed', history_error_code 'graph:100'
[Fact] public async Task AnUnconfirmedRequestStaysPendingAndIsRetried()         // Review Focus 1: timeout → 'Pending' + 'unconfirmed'; advance past lease; second drain → 'Requested'
[Fact] public async Task APausedConnectionStaysPendingWithoutCallingGraph()     // no smb_app_data request; error 'connection_unavailable'
[Fact] public async Task AnExpiredSyncIsNeverRequested()                        // connected_at = now - 25h → zero requests
[Fact] public async Task ARequestedTypeIsNeverRequestedAgain()                  // contacts Requested, history Failed→Pending via Retry → only history is posted
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~HistorySyncRequestTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): request coexistence history and contacts after connecting`

---

### Task 6: Parser — `history`, `smb_message_echoes`, `smb_app_state_sync`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayload.cs` (new change records)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookPayloadParser.cs:54-61` (three new arms; extract the content parsing of `ParseMessage` lines ~151-200 into a shared `ParseContent(JsonElement item, string? type)` so live, history and echoes read bodies the same way)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MetaPayloads.cs` (builders `History(...)`, `HistoryDeclined(...)`, `SmbEchoes(...)`, `ContactsSync(...)`, each marked with `// V-1: unverified` on the fields listed in Review Focus)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserCoexistenceTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed record HistoryChange(string PhoneNumberId, IReadOnlyList<HistoryChunk> Chunks, IReadOnlyList<int> ErrorCodes) : WebhookChange;
internal sealed record HistoryChunk(int? Phase, int? ChunkOrder, int? Progress, IReadOnlyList<HistoryThread> Threads, int SkippedMessages);
internal sealed record HistoryThread(string WaId, IReadOnlyList<CoexistenceMessage> Messages);
internal sealed record CoexistenceMessage(
    string Wamid, DateTimeOffset OccurredAt, MessageDirection Direction, MessageKind Kind,
    string? Text, string? Caption, string? DetailsJson, InboundMedia? Media, bool IsMediaPlaceholder);
internal sealed record SmbEchoesChange(string PhoneNumberId, IReadOnlyList<(string WaId, CoexistenceMessage Message)> Echoes) : WebhookChange;
internal sealed record ContactsSyncChange(string PhoneNumberId, IReadOnlyList<SyncedContact> Contacts) : WebhookChange;
internal sealed record SyncedContact(string WaId, string? FullName, string? FirstName, bool Removed);
public const int HistoryDeclinedCode = 2593109;
```
Rules: phone fields → strip everything but digits, keep only 1–20 digits (else skip that thread / echo / contact). History direction: `to` present → `Outbound`, else `Inbound`. Echo direction always `Outbound`; `WaId` = `to`. `type = "media_placeholder"` → `Kind = Unsupported`, `IsMediaPlaceholder = true`, `DetailsJson = {"historyMedia":"placeholder"}`, no media. Missing `id` or timestamp → message skipped and counted in `SkippedMessages`. `ErrorCodes` = every `errors[].code` found in `value.errors` or `value.history[].errors`. `smb_app_state_sync` items with `type` ≠ `"contact"` are skipped; `action = "remove"` → `Removed = true`. Never throws (existing tolerance rule).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void AHistoryChunkCarriesPhaseOrderProgressAndThreads()
// History(phoneNumberId:"PNID", phase:1, chunkOrder:2, progress:40, thread "573001112233": inbound text "hola" ts 1761700000, outbound (to present) text "buenas" ts 1761700060)
// → HistoryChange("PNID", [Chunk(1,2,40,[Thread("573001112233",[Inbound Text "hola" at 2025-10-29T01:06:40Z, Outbound Text "buenas"])],0)], [])
[Fact] public void ADeclinedHistoryCarriesTheErrorCode()                 // ErrorCodes == [2593109]
[Fact] public void AMediaPlaceholderIsUnsupportedWithoutMedia()          // Kind Unsupported, IsMediaPlaceholder, Media null, DetailsJson contains "historyMedia"
[Fact] public void AThreadIdIsNormalizedToDigitsAndAnInvalidOneIsSkipped()   // Review Focus 2: "+57 300 111 2233" → "573001112233"; "abc" thread dropped, other thread kept
[Fact] public void AMessageWithoutTimestampIsSkippedAndTheChunkKeepsItsProgress()   // Review Focus 5: SkippedMessages == 1, Progress == 40
[Fact] public void AnEchoIsOutboundToTheUserPhone()                       // SmbEchoes from "PNID-display" to "573001112233" image with id "MID" → Outbound Image, Media.MetaMediaId "MID"
[Fact] public void ContactsSyncReadsAddAndRemove()                        // add {full_name "Ana Pérez", phone "+573001112233"} → SyncedContact("573001112233","Ana Pérez","Ana",false); remove → Removed true
[Fact] public void TheLiveMessagesParsingIsUnchanged()                    // run the existing WebhookPayloadParserTests fixtures once through the refactor (no new assertion: the existing class must stay green)
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WebhookPayloadParser"` → new tests FAIL, existing pass.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the same command** → PASS (existing `WebhookPayloadParserTests` and `WebhookPayloadParserTemplateTests` included).
- [ ] **Step 5: Commit** — `feat(messaging): parse coexistence history, echoes and contacts webhooks`

---

### Task 7: Customers — ensure by phone without BSUID

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (new factory)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerWhatsAppDirectory.cs`, `CustomerWhatsAppDirectory.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs`
- Modify: `src/Bootstrapper/MessagingCustomerDirectory.cs` and every `IMessagingCustomerDirectory` test double the compiler reports
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWhatsAppDirectoryTests.cs` , `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryEnsureByPhoneTests.cs`

**Interfaces:**
- Produces:

```csharp
// Customers.Domain
public static Customer CreateIncompleteFromPhone(CustomerId id, Guid tenantId, string name, string phoneE164, string? country, DateTimeOffset now, IPhoneNumberNormalizer phoneNormalizer);
// Customers.Application
public sealed record WhatsAppPhoneContact(string PhoneE164, string? Name);
Task<EnsuredCustomer> EnsureByPhoneAsync(Guid tenantId, WhatsAppPhoneContact contact, CancellationToken cancellationToken);
// Messaging.Application
Task<MessagingEnsuredCustomer> EnsureByPhoneAsync(Guid tenantId, string waId, string? name, CancellationToken cancellationToken);  // adapter adds "+"
```
Algorithm: `FindOldestByPhoneE164Async` → found: survivor id (`MergedIntoId ?? Id`), `EnsureOutcome.Existing`; and per P-7, if it is `Incomplete` and its `Name` equals the phone fallback and `contact.Name` is not blank, rename to `contact.Name` (through the domain, `Touch`ed). Not found: `CreateIncompleteFromPhone` with name = `contact.Name` trimmed, else the same fallback `IncompleteCustomerProfile.NameFor` uses for a contact without profile name; country from `IncompleteCustomerProfile.CountryFor` logic on the phone; audit `CustomerAuditActions.CreatedFromMessaging` with actor `Guid.Empty`; `EnsureOutcome.Created`. Never writes `whatsapp_user_id`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task AnUnknownPhoneCreatesAnIncompleteCustomerWithoutBsuid()
// EnsureByPhoneAsync(t, new("+573001112233","Ana Pérez")) → Created; row: completeness Incomplete, phone "+573001112233", whatsapp_user_id NULL, name "Ana Pérez"; one audit CreatedFromMessaging
[Fact] public async Task AKnownPhoneIsNotDuplicated()                        // existing customer with that phone → Existing, same id, count == 1
[Fact] public async Task ALaterLiveMessageAttachesTheBsuidToThePhoneCustomer()
// after EnsureByPhone, EnsureAsync(t, new WhatsAppContact("CO.123", "+573001112233", "Ana", null)) → Linked, same id, whatsapp_user_id "CO.123"
[Fact] public async Task AContactNameReplacesOnlyThePhoneFallbackName()      // P-7: fallback-named incomplete gets "Ana Pérez"; a complete or hand-named customer keeps its name
```

- [ ] **Step 2: Run**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerWhatsAppDirectoryTests"
```
Expected: FAIL.
- [ ] **Step 3: Implement** (Customers, then the Messaging port, then the Bootstrapper adapter).
- [ ] **Step 4: Run** the command above and `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingCustomerDirectoryEnsureByPhoneTests"` → PASS.
- [ ] **Step 5: Commit** — `feat(customers): ensure an incomplete customer by phone without BSUID`

---

### Task 8: History ingestion

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/CoexistenceIngestion.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationOpener.cs:124-143` (make the phone lookup reusable: `internal static Task<Guid?> FindByPhoneAsync(MessagingDbContext, Guid tenantId, Guid connectionId, string waId, CancellationToken)`; `OpenByPhoneAsync` keeps calling it)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/WebhookDeliveryProcessor.cs:76-108` (case `HistoryChange`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HistoryIngestionTests.cs`

**Interfaces:**
- Consumes: Task 2 (`history_syncs`, `history_managed`), Task 6 records, Task 7 `IMessagingCustomerDirectory.EnsureByPhoneAsync`.
- Produces:

```csharp
internal enum CoexistenceSource { History, Echo }
internal static Task<Guid?> IngestThreadAsync(
    MessagingDbContext dbContext, Guid tenantId, Guid connectionId, string waId, IReadOnlyList<CoexistenceMessage> messages,
    Guid? customerId, CoexistenceSource source, DateTimeOffset? connectedAt, DateTimeOffset now, CancellationToken cancellationToken);
// returns the conversation id, or null when every message was a duplicate and nothing changed
internal static Task RecordChunkAsync(MessagingDbContext dbContext, Guid connectionId, HistoryChunk chunk, DateTimeOffset now, CancellationToken cancellationToken);
internal static Task RecordDeclinedAsync(MessagingDbContext dbContext, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);
```
`IngestThreadAsync`, one transaction:
1. Conversation: `FindByPhoneAsync`; none → `INSERT` legacy row (`user_id NULL`, `wa_id`, `channel 'WhatsApp'`, `customer_id`, `unread_count 0`, `last_activity_at` = earliest message time, `created_at`/`updated_at` = now, `version 1`) with `status 'Resolved'`, `history_managed true` for `History`; `status 'Open'`, `history_managed false` for `Echo` (P-5); `ON CONFLICT (connection_id, wa_id) WHERE user_id IS NULL DO NOTHING`, then re-find (race, as `OpenByPhoneAsync`).
2. Messages in `OccurredAt` order: `INSERT` like `IngestEchoAsync` with direction 1/2, status = the code `IngestOnceAsync` writes for an inbound / `1` (Sent) for an outbound, `ON CONFLICT (connection_id, wamid) … DO NOTHING`; media via `InsertMediaAsync` when `Media` is present. On conflict, P-6 late media (Task 11) — leave a hook `AttachLateMediaAsync` call site, no-op until Task 11.
3. Summary: same `UPDATE` as `IngestEchoAsync` (`customer_id = COALESCE`, `last_activity_at = GREATEST`, `last_message_*` only when newer, `version + 1`), using each inserted message's direction/kind. Never touches `last_inbound_at`, `last_inbound_wamid`, `assigned_member_id`, `automation_*`.
4. Rule, only `WHERE history_managed`: `status = 'Open'` iff `last_message_direction = 1` and `last_message_at >= @connectedAt - interval '24 hours'`, else `'Resolved'` (`resolution_reason_id NULL`); `unread_count` = count of inbound messages after the latest outbound one when Open, `0` when Resolved (D-6). With `connectedAt` null (no sync row) → `Resolved`, `0`.
No realtime notify, no `automation_inbox`, no opt-out, no events in the thread.

`RecordChunkAsync`: `history_phase = GREATEST(COALESCE(history_phase,-1), @phase)`, `history_progress = GREATEST(...)`, `completed_at = COALESCE(completed_at, CASE WHEN @progress = 100 THEN @now END)`, `updated_at`, `version + 1`. No row → nothing (logged). `RecordDeclinedAsync`: `declined_at = COALESCE(declined_at, @now)`.

Processor (`HistoryChange`): route by `PhoneNumberId` (`unknown-route` → log + processed); **no** paused/module-off discard (D-5); if `ErrorCodes` contains `2593109` → `RecordDeclinedAsync`; per thread: `EnsureByPhoneAsync(tenant, waId, null)` (D-10; outside the Messaging transaction, as live ingestion does), read `connected_at` of `history_syncs` for the connection, `IngestThreadAsync(..., History, ...)`; then `RecordChunkAsync` per chunk.

- [ ] **Step 1: Write the failing tests** (each posts `MetaPayloads.History(...)` with `PostWebhookAsync`, then `DrainDeliveriesAsync`; sync row seeded with `connected_at = T`)

```csharp
[Fact] public async Task AnImportedThreadIsAClosedLegacyConversationWithoutWork()
// spec §8: conversation user_id NULL, wa_id "573001112233", status 'Resolved', unread_count 0, last_inbound_at NULL, history_managed true;
// messages in occurred_at order; count(automation_inbox for it) == 0; count(messages kind=13 Event) == 0; customer_id set to the phone customer;
// count(assigned_member_id NOT NULL) == 0
[Fact] public async Task LastFromTheContactWithin24HoursOpensWithItsUnread()
// outbound at T-3h, inbound at T-2h and T-1h → status 'Open', unread_count 2
[Fact] public async Task LastFromTheBusinessStaysClosed()
[Fact] public async Task LastFromTheContactOlderThan24HoursStaysClosed()          // inbound at T-25h → Resolved
[Fact] public async Task ChunksOutOfOrderGiveTheSameResult()                     // chunk 2 (recent inbound) before chunk 1 (older outbound) → Open, unread 1
[Fact] public async Task ARepeatedChunkDoesNotDuplicateMessages()                // same body twice (different body hash: add a no-op field) → message count unchanged
[Fact] public async Task AMessageAlreadyIngestedIsNotDuplicatedOrOverwritten()   // Review Focus 4: live/echo message with same wamid and other text → one row, original text
[Fact] public async Task ProgressAndPhaseAdvanceAndOneHundredCompletes()         // 40 then 20 (late) then 100 → history_progress 100, completed_at set; status Completed
[Fact] public async Task ADeclinedHistoryMarksTheSyncDeclined()                  // HistoryDeclined → declined_at set; StatusAt == Declined
[Fact] public async Task APausedConnectionStillImportsTheHistory()               // spec §8 / D-5: connection Paused and module off → messages ingested
[Fact] public async Task TheWindowStaysClosedOnAnOpenImportedConversation()      // GET conversation → customerWindowExpiresAt == null
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~HistoryIngestionTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same command plus `--filter "FullyQualifiedName~InboundIngestionTests|FullyQualifiedName~WebhookDeliveryWorkerTests"` → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): import coexistence history into closed legacy conversations`

---

### Task 9: Live flow takes over an imported conversation

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs:131-229` (the conversation `UPDATE` of `IngestOnceAsync` adds `history_managed = false`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HistoryTakeoverTests.cs`

**Interfaces:**
- Consumes: Task 2 (`Resolve`/`Reopen` already clear it), Task 8 ingestion.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task ALiveMessageWithBsuidAdoptsTheImportedConversation()
// import thread for 573001112233 → then live MetaPayloads inbound with from_user_id "CO.1", wa_id same → same conversation id, user_id "CO.1",
// last_inbound_at set, history_managed false; customer of the conversation gets whatsapp_user_id "CO.1"
[Fact] public async Task AfterALiveMessageTheImportNoLongerChangesIt()
// then a history chunk with a newer outbound → status stays 'Open' (live), unread not recomputed by the rule
[Fact] public async Task AnAdvisorActionStopsTheImportRule()        // Review Focus 3: imported Open → POST resolve → later chunk with recent inbound → stays Resolved
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~HistoryTakeoverTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same command plus `FullyQualifiedName~InboundBsuidTests` → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): live traffic takes imported conversations out of the import rule`

---

### Task 10: Echoes and contacts

**Files:**
- Modify: `WebhookDeliveryProcessor.cs` (cases `SmbEchoesChange`, `ContactsSyncChange`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CoexistenceEchoAndContactsTests.cs`

**Interfaces:**
- Consumes: Task 8 `IngestThreadAsync(..., CoexistenceSource.Echo, ...)`, Task 7 `EnsureByPhoneAsync`.

Echoes: route, no paused/module-off discard (D-5), per echo `EnsureByPhoneAsync(tenant, waId, null)` then `IngestThreadAsync` with one message; group consecutive echoes of the same `WaId` in one call. Contacts: route, per contact with `Removed = false` → `EnsureByPhoneAsync(tenant, waId, FullName ?? FirstName)`; removed → skipped (D-7). Neither notifies realtime.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task AnEchoIsAnOutboundSentMessageThatDoesNotTouchTheWindow()
// existing live conversation with last_inbound_at L → echo → new message direction 2 status 1 (Sent), last_message_direction Outbound, last_inbound_at == L, unread unchanged
[Fact] public async Task AnEchoToAnUnknownPhoneCreatesAnOpenLegacyConversation()   // P-5: status Open, history_managed false, user_id NULL
[Fact] public async Task AContactCreatesAnIncompleteCustomerWithoutBsuid()        // spec §8
[Fact] public async Task ARemovedContactLeavesTheCustomerUntouched()              // D-7: name/version unchanged
[Fact] public async Task EchoesAndContactsAreProcessedWithThePausedConnection()   // D-5
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~CoexistenceEchoAndContactsTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): ingest coexistence echoes and synced contacts`

---

### Task 11: Media placeholders

**Files:**
- Modify: `CoexistenceIngestion.cs` (`AttachLateMediaAsync`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:141-164` (`MessageDto.MediaState`), `MessagingSupport.cs:199-240` (`ToDto`), `IMessageQueries.cs` (`MessageRow` gains `CreatedAt` if missing) and the query that fills it
- Create: `src/Modules/Messaging/Modules.Messaging.Application/HistoryMediaState.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/HistoryMediaStateTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HistoryMediaTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class HistoryMediaState
{
    public const string Pending = "Pending";
    public const string Unavailable = "Unavailable";
    public static readonly TimeSpan MetaMediaHistory = TimeSpan.FromDays(14);   // spec §1: only the last 14 days bring a media id
    public static bool IsPlaceholder(string? detailsJson);                       // {"historyMedia":"placeholder"}
    public static string? For(string? detailsJson, bool hasMedia, DateTimeOffset occurredAt, DateTimeOffset createdAt);
    // null when not a placeholder or media exists; Unavailable when occurredAt < createdAt - 14 days; else Pending
}
// MessageDto, appended last:  string? MediaState = null
```
In `ToDto`: `MediaState = HistoryMediaState.For(...)`; when `MediaState` is not null, `UnsupportedReason = null` (the bubble shows the media state, not "unsupported"). `AttachLateMediaAsync` (P-6, V-2 assumption): on `wamid` conflict with an incoming message that has `Media`, if the stored message is a placeholder and has no `message_media` row → `InsertMediaAsync`, set `kind` to the incoming kind, keep `details` without the marker; the existing `MediaCopyWorker` downloads it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] HistoryMediaStateRules   // (placeholder, hasMedia, occurredAt, createdAt) → expected
//   not placeholder → null; placeholder + media → null; occurred = created - 15d → "Unavailable"; occurred = created - 2d → "Pending"
[Fact] public async Task APlaceholderShowsPendingThenTheLateMediaIsQueued()
// history with media_placeholder wamid W (recent) → GET messages: kind "Unsupported", media null, mediaState "Pending", unsupportedReason null
// then history with W as image id "MID" → message_media row meta_media_id "MID", kind Image; GET → mediaState null
[Fact] public async Task AnOldPlaceholderIsUnavailable()          // occurred 20 days before import → "Unavailable"
```

- [ ] **Step 2: Run** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~HistoryMediaStateTests"` and `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~HistoryMediaTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both plus `FullyQualifiedName~ThreadApiTests` → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): expose pending or unavailable media of imported placeholders`

---

### Task 12: HTTP contract — read state and retry

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingConnectionPermissions.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/HistorySyncQueries.cs` (`GetHistorySyncQuery` + handler, `RetryHistorySyncCommand` + handler, `HistorySyncDto`, mapping)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IHistorySyncRepository.cs`; implementation in `Modules.Messaging.Infrastructure/Persistence/HistorySyncRepository.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Api/HistorySyncEndpoints.cs`; map it where the other Messaging endpoint groups are mapped
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HistorySyncApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingConnectionPermissionsParityTests.cs`

**Interfaces:**
- Consumes: Task 2 domain (`StatusAt`, `CanRetryAt`, `Retry`), error codes.
- Produces (contract for the frontend plan):

```
GET  /api/v1/tenants/{tenantId:guid}/messaging/connections/{connectionId:guid}/history-sync
     policy: integrations.connection.read   → 200 HistorySyncDto
     403 authorization.denied (other tenant / no permission)
     404 messaging.history_sync.not_found   (the connection has no coexistence import: the screen hides the panel)
POST /api/v1/tenants/{tenantId:guid}/messaging/connections/{connectionId:guid}/history-sync/retry   (no body)
     policy: integrations.connection.manage → 200 HistorySyncDto (state after the retry; the request goes out on the worker's next tick)
     403 authorization.denied, 404 messaging.history_sync.not_found,
     422 messaging.history_sync.retry_not_allowed (expired, declined, or nothing Pending/Failed)
```

```csharp
public static class MessagingConnectionPermissions
{
    // Mismo texto que IntegrationsPermissions (P-8); lo amarra MessagingConnectionPermissionsParityTests.
    public const string Read = "integrations.connection.read";
    public const string Manage = "integrations.connection.manage";
}

/// BFF: el estado y el «se puede reintentar» los calcula el servidor, porque dependen del plazo de 24 h de Meta y del
/// orden de precedencia de los estados, que la pantalla no debe conocer.
public sealed record HistorySyncDto(
    Guid ConnectionId,
    string Status,                 // Pending | Requested | InProgress | Completed | Declined | Failed | Expired
    DateTimeOffset ConnectedAt,
    DateTimeOffset Deadline,       // ConnectedAt + 24 h
    HistorySyncRequestDto Contacts,
    HistorySyncRequestDto History,
    int? Phase,                    // 0..2, highest seen; null until the first chunk
    int? Progress,                 // 0..100, highest seen; null until the first chunk
    bool CanRetry,                 // HistorySync.CanRetryAt(now)
    bool MustReconnect);           // Status == Expired, or Failed with now >= Deadline: the only way out is disconnect + Embedded Signup again

public sealed record HistorySyncRequestDto(
    string State,                  // Pending | Requested | Failed
    DateTimeOffset? RequestedAt,
    string? ErrorCode);            // connection_unavailable | unconfirmed | graph:{code} | null
```
Handlers: `executionContext.TenantId == tenantId && HasPermission(...)` else `RequestForbiddenException("authorization.denied", …)`; no module guard (P-8). Repository reads by `(tenant_id, connection_id)`; missing → `ResourceNotFoundException(MessagingErrorCodes.HistorySyncNotFound, …)`. Retry: domain `Retry(now)`, set `claimed_until = NULL`, audit with `IMessagingAuditRecorder` action `messaging.history_sync.retried` (resource = connection id, changed fields = the retried types by name), save in one unit of work; a concurrent update → existing optimistic-concurrency mapping.

No new DTO field is needed for the window: `ConversationSummary.CustomerWindowExpiresAt` is `null` when `last_inbound_at` is `null`, which is every imported conversation (asserted in Task 8 `TheWindowStaysClosedOnAnOpenImportedConversation`). Frontend contract: `customerWindowExpiresAt == null` on a WhatsApp conversation ⇒ closed window, template only (already true for conversations opened by a quotation template). Media: `MessageDto.mediaState` from Task 11.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void PermissionsMatchIntegrations()   // MessagingConnectionPermissions.Read == IntegrationsPermissions.ConnectionRead, Manage == ConnectionManage
[Fact] public async Task TheStateComesComputed()
// seeded row: contacts Requested at T+1m, history Failed "graph:100", connected T = now - 1h →
// 200 {"status":"Failed","deadline":T+24h,"contacts":{"state":"Requested",…},"history":{"state":"Failed","errorCode":"graph:100"},"phase":null,"progress":null,"canRetry":true,"mustReconnect":false}
[Fact] public async Task AnExpiredSyncAsksToReconnect()                  // connected 25 h ago, history Pending → status "Expired", canRetry false, mustReconnect true
[Fact] public async Task RetryMovesFailedToPendingAndAudits()            // 200, history.state "Pending", errorCode null; one audit row messaging.history_sync.retried
[Fact] public async Task RetryAfterTheDeadlineIs422()                    // code messaging.history_sync.retry_not_allowed
[Fact] public async Task AConnectionWithoutImportIs404()                 // code messaging.history_sync.not_found
[Fact] public async Task AnotherTenantIs403()                            // route with tenant B, caller of tenant A → 403 authorization.denied
[Fact] public async Task WithoutConnectionReadIs403Not500()              // X-Permissions without integrations.connection.read → 403 (proves the policy resolves)
[Fact] public async Task RetryNeedsConnectionManage()                    // only integrations.connection.read → POST 403
[Fact] public async Task TheStateIsReadableWithMessagingOff()            // P-8: module off → GET 200
```

- [ ] **Step 2: Run**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingConnectionPermissionsParityTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~HistorySyncApiTests"
```
Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run both** → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): expose coexistence history sync state and retry`

---

### Task 13: Layer tests, README, full suite

**Files:**
- Modify: `README.md` — Messaging section: the three subscribed webhook fields (`history`, `smb_message_echoes`, `smb_app_state_sync`) to enable in the Meta app panel, deployment order of spec §6 (backend before any coexistence connection), the two endpoints, and V-3 (`Messaging:Webhook:MaxBodyBytes` to be checked against a real chunk).
- Modify: `CLAUDE.md` (backend) — one gotcha: «el historial de coexistencia no pasa por `IngestOnceAsync`; `history_managed` decide si la regla de abiertas corre, y `Resolve`/`Reopen`/un entrante en vivo la apagan».

- [ ] **Step 1: Run the layer tests**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~IntegrationsLayerTests|FullyQualifiedName~CustomersLayerTests"
```
Expected: PASS (spec §8).

- [ ] **Step 2: Build and format check**

```powershell
dotnet build Backend.slnx -warnaserror
```
Expected: `0 Error(s)`. (`dotnet format --verify-no-changes` is not a gate here: the CRLF baseline fails it; compare against a stash baseline if run.)

- [ ] **Step 3: Full suite, once**

```powershell
$env:Seed__Enabled = "false"
dotnet test Backend.slnx --logger "trx"
```
Expected: no failures beyond the pre-existing ones (compare by test name against a `git stash` baseline on `develop` if any appear).

- [ ] **Step 4: Commit** — `docs: document coexistence history import`

---

## Not planned (spec points without a task)

- **V-1, V-2, V-3, V-4, V-6:** verifications against real Meta traffic; fixtures and P-3/P-6 are built to tolerate them and are listed in Review Focus.
- **V-5 (`TriageRules.IsPending` with `last_inbound_at` null):** a verification in Pipeline, no code change specified; an imported open conversation will not enter triage, which matches rule 3 (no work in the funnel).
- **§6 Meta panel subscription and rollout order:** operational, documented in Task 13.
- **§10 frontend:** separate slice; its contract is fixed in Task 12.
