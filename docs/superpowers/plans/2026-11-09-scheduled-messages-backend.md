# Scheduled Messages (Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an advisor schedule one message per conversation (text, image, document, video, audio attachment or voice note) for an absolute UTC instant inside the 24 h window, and have a backend worker send it at that time.

**Architecture:** New aggregate `ScheduledMessage` (Messaging Domain) persisted in `messaging.scheduled_messages`; HTTP handlers in Messaging Application reuse `OutboundGuards` for creation and only permission checks for management; a `ScheduledMessageWorker` (Infrastructure, `BackgroundService`, calco of `CampaignDispatchWorker`) claims due rows with `FOR UPDATE SKIP LOCKED` and hands each to `ScheduledMessageExecutor` (Application), which sends through the existing `IOutboundMessages` claim and `OutboundSettlement` with `seen: 0`. A new `messages.retry_disabled` column burns the `client_id` of a discarded failed send.

**Tech Stack:** .NET 10, EF Core + Npgsql (raw SQL for claim/CAS), FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-09-scheduled-messages-backend-design.md` (binding; read it alongside this plan).

## Global Constraints

- Branch `feature/scheduled-messages`; run `git branch --show-current` right before every commit and stop if it differs. Never touch `main`; never edit `docs/estado.md` on this branch.
- Strict TDD: every task shows a literal RED run before GREEN. Conventional commits, no AI attribution, no `Co-Authored-By`.
- `Modules.Messaging.Application` never references EF Core/Npgsql (`MessagingLayerTests.ApplicationDoesNotReferencePersistenceLibraries`). DB error translation lives in `MessagingSaveChanges`, discriminated by index name.
- No HTTP status is built in a handler: 404 `ResourceNotFoundException`, 409 `ResourceConflictException`, 412 `RequestConcurrencyException("concurrency.conflict", …)`, 422 `MessagingDomainException` / `ValidationException` (mapped by `src/Api/ApiExceptionHandler.cs`).
- Tenant isolation: route `{tenantId:guid}`, policy, and handler revalidation via `MessagingAuthorization.EnsureAsync` (403 `authorization.denied`, never 404). Every repository/store method receives `tenantId`.
- New error codes go in `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`, verbatim: `messaging.scheduled.outside_window`, `messaging.scheduled.too_soon`, `messaging.scheduled.already_exists`, `messaging.scheduled.not_found`, `messaging.scheduled.not_editable`, `messaging.scheduled.not_retriable`, `messaging.message.not_retriable`, `messaging.scheduled.unknown_outcome`, `messaging.scheduled.media_missing`.
- Timing constants: `ScheduledMessage.MinimumLead = 1 min`, `ScheduledMessage.WindowSafetyMargin = 2 min`, `ScheduledMessageDispatcher.LeaseLength = 10 min`, `Messaging:Workers:ScheduledPollSeconds` default `5`, worker parallelism `4`.
- `{saludo}` is the only token the backend resolves; length limits count it as 13 characters (`"Buenas tardes"`/`"Buenas noches"`).
- Copy shown to people: Colombian Spanish, tuteo. Identifiers, codes and comments in English (existing comments are Spanish; new ones may follow file style).
- Integration tests: per project, one at a time, `$env:Seed__Enabled = "false"`, `-- xUnit.MaxParallelThreads=4`. Stop `Api.exe` before building.
- Migration via design factory: `dotnet ef migrations add AddScheduledMessages --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`.
- Every new handler and validator registered by hand in `src/Bootstrapper/QepServiceCollectionExtensions.cs` next to `SendMediaMessageHandler` (line ~936); missing registration = 500.

## Review Focus

1. **Two pods, one row.** Two concurrent `DrainAsync` over the same due row must produce exactly one Meta call and one `messages` row (Task 10, `TwoConcurrentDrainsSendOnce`).
2. **Edit racing the claim.** A `PUT`/`cancel` that loses to the worker claim must get 409 `not_editable`, not 412 or 500, because the handler locks the row with `FOR UPDATE` before checking status (Task 7 + Task 10, `AnEditAfterTheClaimIsNotEditable`).
3. **Lease expiry while Meta is still answering.** Recovery must never move a row back to `Scheduled`; with no committed message row it is `Failed unknown_outcome`, and a late CAS from the original worker must not overwrite it (Task 9, `ALateCompletionAfterRecoveryWritesNothing`).
4. **Retry after a lost 201 with a consumed staging.** Same `clientId` + same `uploadId` returns 200 with the stored row, never 422 `upload_not_found` (Task 5/6, test 3b).
5. **Discard burns the clientId everywhere.** After `discard`, text, media, audio and template sends with that `clientId` answer 409 `messaging.message.not_retriable` without calling Meta, including the in-flight path through `TakeInFlightAsync` (Task 3).

---

## File Structure

| File | Responsibility |
| --- | --- |
| Create `src/Modules/Messaging/Modules.Messaging.Domain/ScheduledMessage.cs` | Aggregate, `ScheduledMessageStatus`, transitions, window rules, `ScheduledText` length measure |
| Modify `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` | Nine new codes |
| Create `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessagePorts.cs` | `IScheduledMessageRepository`, `IScheduledMessageLock`, `IScheduledMessageStore`, `ClaimedScheduledMessage` |
| Create `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessages.cs` | Commands, validators, create/get/manage handlers, `ScheduledMessageMapper`, DTOs |
| Create `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessageExecutor.cs` | Sends one claimed row |
| Create `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessageDispatcher.cs` | Tick logic: claim, recovery (D9), purge |
| Create `src/Modules/Messaging/Modules.Messaging.Application/OutboundMediaPreparer.cs` | Extracted `PrepareAsync` from `SendMediaMessageHandler` + `UploadStoredAsync` |
| Modify `SendMediaMessage.cs`, `OutboundGuards.cs`, `IOutboundMessages.cs`, `IMessageQueries.cs`, `MessagingDtos.cs`, `MessagingSupport.cs` (MessageMapping), `IMessagingAuditRecorder.cs` | Extraction, `retry_disabled`, audit |
| Create `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ScheduledMessageRepository.cs` | EF repository + `FOR UPDATE` lock |
| Create `src/Modules/Messaging/Modules.Messaging.Infrastructure/ScheduledMessages/ScheduledMessageStore.cs`, `ScheduledMessageWorker.cs` | Claim/CAS SQL, worker loop |
| Modify `MessagingDbContext.cs`, `MessageRecord.cs`, `OutboundMessages.cs`, `MessageQueries.cs`, `MessagingSaveChanges.cs`, `MessagingAuditRecorder.cs`, `Options/MessagingWorkerOptions.cs`, `MessagingInfrastructureExtensions.cs` | Persistence wiring |
| Create migration `…_AddScheduledMessages.cs` | Table, indexes, CHECKs, `messages.retry_disabled` |
| Create `src/Modules/Messaging/Modules.Messaging.Api/ScheduledMessageEndpoints.cs`; modify `MessagingEndpoints.cs` | Routes |
| Modify `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Handler registration |
| Modify `README.md` (§ Mensajería, line ~3025; config table line 150) | «Mensajes programados» section, `ScheduledPollSeconds` |

Unit tests under `tests/Modules/Messaging/Modules.Messaging.UnitTests/`, integration under `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/`. Reuse `MessagingTestBed`, `FakeOutboundMessages`, `FakeWhatsAppClient`, `FakeInstagramClient`, `InMemoryMediaStore`, `FakeUploadStaging`, `FakeImageCompressor`, `FakeOpusTranscoder`, `FakeTenantClock` (`CampaignDispatchDoubles.cs`), `RecordingAudit` and `FakeUnitOfWork` (`CampaignTestDoubles.cs`).

---

### Task 1: Domain aggregate, window rules and error codes

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/ScheduledMessage.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ScheduledMessageTests.cs`, `ScheduleWindowTests.cs`

**Interfaces:**
- Produces:
  - `enum ScheduledMessageStatus { Scheduled, Sending, Sent, Failed, Cancelled }` (persisted by name).
  - `sealed class ScheduledMessage` with properties mirroring every spec column (`Id, TenantId, ConversationId, ClientId, Kind (MessageKind), IsVoiceNote, Text, Caption, MediaStorageKey, MediaMimeType, MediaFileName, MediaSizeBytes, MediaSha256, ScheduledAt, Status, AuthorMemberId, CreatedByMemberId, CreatedAt, UpdatedByMemberId, UpdatedAt, CancelledByMemberId, CancelledAt, SentMessageId, FailureCode, Attempts, LockedUntil, Version`).
  - `public static readonly TimeSpan MinimumLead = TimeSpan.FromMinutes(1); public static readonly TimeSpan WindowSafetyMargin = TimeSpan.FromMinutes(2);`
  - `static ScheduledMessage CreateText(Guid id, Guid tenantId, Guid conversationId, Guid clientId, string text, DateTimeOffset scheduledAt, Guid memberId, DateTimeOffset now)`
  - `static ScheduledMessage CreateMedia(Guid id, …, MessageKind kind, bool isVoiceNote, string? caption, ScheduledMedia media, DateTimeOffset scheduledAt, Guid memberId, DateTimeOffset now)`; `sealed record ScheduledMedia(string StorageKey, string MimeType, string? FileName, long SizeBytes, string? Sha256Hex)`.
  - `static void EnsureSchedulable(DateTimeOffset scheduledAt, DateTimeOffset? lastInboundAt, DateTimeOffset now)` → throws `MessagingDomainException` `too_soon` / `outside_window`.
  - `static bool IsWindowOpenForSending(DateTimeOffset? lastInboundAt, DateTimeOffset now)` = `lastInboundAt + 24h − WindowSafetyMargin > now`.
  - `ScheduledEditResult Edit(DateTimeOffset? scheduledAt, string? text, bool textProvided, string? caption, bool captionProvided, Guid memberId, DateTimeOffset now)`; `sealed record ScheduledEditResult(IReadOnlyList<string> ChangedFields)` (field names `scheduledAt`, `text`, `caption`).
  - `void Cancel(Guid memberId, DateTimeOffset now)`, `void SendNow(DateTimeOffset now)`, `void Retry(DateTimeOffset now)`, `void Discard(Guid memberId, DateTimeOffset now)`.
  - `bool IsActive` (`Scheduled|Sending|Failed`).
  - `static class ScheduledText { const string GreetingToken = "{saludo}"; const int LongestGreetingLength = 13; static int MeasuredLength(string text); static string ResolveGreeting(string text, string greeting); }`.
  - Error code constants: `ScheduledOutsideWindow`, `ScheduledTooSoon`, `ScheduledAlreadyExists`, `ScheduledNotFound`, `ScheduledNotEditable`, `ScheduledNotRetriable`, `MessageNotRetriable`, `ScheduledUnknownOutcome`, `ScheduledMediaMissing` (values in Global Constraints).

Transition rules (spec «Transiciones»): `Edit`/`Cancel`/`SendNow` only from `Scheduled`, else `ResourceConflictException(ScheduledNotEditable)`; `Retry`/`Discard` only from `Failed`, else `ResourceConflictException(ScheduledNotRetriable)`. Each successful transition increments `Version`, except an `Edit` with empty `ChangedFields`. `Retry` sets `ScheduledAt = now`, `FailureCode = null`, `Status = Scheduled`. `Edit` changes `AuthorMemberId` only when `text` or `caption` changed; `UpdatedByMemberId/UpdatedAt` on any real change. `Cancel`/`Discard` set `CancelledByMemberId/CancelledAt`. `ResourceConflictException` lives in `BuildingBlocks.Application`; if Domain cannot reference it, the transitions return/throw a domain signal and Application maps it — check `Campaign.cs` (`IsEditable`/`IsCancellable` booleans + `CampaignSupport.NotEditable()` in Application) and follow that precedent: expose `CanEdit`/`CanRetry` booleans and throw from Application.

Edit normalization (spec «PUT» + orchestrator contract ruling 2026-11-09): omitted or `null` keeps, **including `scheduledAt`**; `caption` `""`/whitespace → `null`; `text` trimmed; `text`/`caption` on a kind that does not accept them is rejected by the validator (Task 7), not here.

- [ ] **Step 1: Write the failing tests**

```csharp
// ScheduleWindowTests
[Fact] public void FiftyNineSecondsAheadIsTooSoon() // scheduledAt = now + 59 s → code ScheduledTooSoon
[Fact] public void OneMinuteBeforeExpiryIsOutsideWindow() // lastInbound + 24h − 1 min → ScheduledOutsideWindow
[Fact] public void ExactlyTwoMinutesBeforeExpiryPasses() // lastInbound + 24h − 2 min → no throw
[Fact] public void WithoutLastInboundItIsOutsideWindow() // null → ScheduledOutsideWindow
[Fact] public void GreetingTokenCountsAsThirteen() // MeasuredLength("{saludo}, hola") == 13 + 6

// ScheduledMessageTests
[Theory] Cancel/SendNow/Edit from Sending|Sent|Failed|Cancelled → CanEdit false
[Theory] Retry/Discard from Scheduled|Sending|Sent|Cancelled → CanRetry false
[Fact] public void EveryTransitionBumpsVersion() // Version 1 → 2 after SendNow, Retry, Cancel, Discard
[Fact] public void EditingOnlyScheduledAtKeepsTheAuthor() // AuthorMemberId unchanged, ChangedFields == ["scheduledAt"]
[Fact] public void EditingTheTextChangesTheAuthor() // AuthorMemberId == editor, ChangedFields == ["text"]
[Fact] public void AnIdenticalEditChangesNothing() // ChangedFields empty, Version unchanged, UpdatedAt null
[Fact] public void ANullScheduledAtKeepsTheStoredTime() // Edit(scheduledAt: null, text: "x", …) → ScheduledAt unchanged, ChangedFields == ["text"]
[Fact] public void AnEmptyCaptionClearsIt() // caption "  " → Caption null, ChangedFields == ["caption"]
[Fact] public void RetryReschedulesNowAndClearsTheFailure()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ScheduledMessageTests|FullyQualifiedName~ScheduleWindowTests"`
Expected: build error `The type or namespace name 'ScheduledMessage' could not be found`.

- [ ] **Step 3: Implement `ScheduledMessage`, `ScheduledMessageStatus`, `ScheduledMedia`, `ScheduledText` and the nine error codes.**

- [ ] **Step 4: Run tests to verify they pass** (same command). Expected: all PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging/Modules.Messaging.Domain tests/Modules/Messaging/Modules.Messaging.UnitTests/ScheduledMessageTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/ScheduleWindowTests.cs
git commit -m "feat(messaging): add scheduled message aggregate and window rules"
```

---

### Task 2: Persistence — table, `retry_disabled`, migration, repository and index translation

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (add `DbSet<ScheduledMessage> ScheduledMessages`, `ConfigureScheduledMessage`, index-name constants; `message.Property(RetryDisabled)` next to `Imported` at line ~359)
- Modify: `Persistence/MessageRecord.cs` (`public bool RetryDisabled { get; set; }`)
- Modify: `Persistence/MessagingSaveChanges.cs`
- Create: `Persistence/ScheduledMessageRepository.cs`
- Create: `Application/ScheduledMessagePorts.cs` (repository + lock ports only; store port in Task 8)
- Create: migration `Persistence/Migrations/<timestamp>_AddScheduledMessages.cs` (+ Designer, snapshot)
- Modify: `MessagingInfrastructureExtensions.cs` (register repository)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ScheduledMessagePersistenceTests.cs`

**Interfaces:**
- Produces (Application):
  - `public const string ScheduledActiveConversationIndex = "UX_scheduled_messages_active_conversation"; ScheduledConversationClientIndex = "UX_scheduled_messages_conversation_client"; ScheduledDueIndex = "IX_scheduled_messages_due";` on `MessagingDbContext`.
  - `interface IScheduledMessageRepository { Task<ScheduledMessage?> FindByClientIdAsync(Guid tenantId, Guid conversationId, Guid clientId, CancellationToken); Task<ScheduledMessage?> FindActiveAsync(Guid tenantId, Guid conversationId, CancellationToken); /* untracked */ Task<IScheduledMessageLock?> LockAsync(Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken); Task AddAsync(ScheduledMessage message, CancellationToken); /* saves via MessagingSaveChanges */ }`
  - `interface IScheduledMessageLock : IAsyncDisposable { ScheduledMessage Message { get; } Task CommitAsync(CancellationToken); Task CommitDiscardAsync(CancellationToken); }` — opens a transaction, loads the row with `FromSql("SELECT <every mapped column> FROM messaging.scheduled_messages WHERE id = … AND tenant_id = … AND conversation_id = … FOR UPDATE")` (tracked), commits through `MessagingSaveChanges.SaveAsync`; dispose without commit = rollback. Same shape as `IOutboundClaim`. `CommitDiscardAsync` additionally runs, in the same transaction, `UPDATE messaging.messages SET retry_disabled = true WHERE conversation_id = … AND client_id = … AND status = {MessageColumnCodes.ToCode(MessageStatus.Failed)}` (precedent: `CampaignRepository.SaveCancelledAsync`).
- Produces (Infrastructure): `MessagingSaveChanges` maps both `ScheduledActiveConversationIndex` and `ScheduledConversationClientIndex` (23505) to `ResourceConflictException(MessagingErrorCodes.ScheduledAlreadyExists, …)`. The create handler (Task 5) re-reads by `clientId` on that exception to resolve the same-`clientId` race.

Table (spec «Modelo de datos»): every column with the spec types; `status`/`kind` `HasConversion<string>().HasMaxLength(16)`; `version` `IsConcurrencyToken()`; FK `conversation_id` → `conversations`. Indexes and CHECKs verbatim:
- `UX_scheduled_messages_active_conversation` unique `(conversation_id)` filter `status IN ('Scheduled','Sending','Failed')`
- `UX_scheduled_messages_conversation_client` unique `(conversation_id, client_id)`
- `IX_scheduled_messages_due` `(scheduled_at)` filter `status = 'Scheduled'`
- CHECKs: `(kind = 'Text') = (text IS NOT NULL AND media_mime_type IS NULL)` split as spec: `kind <> 'Text' OR (text IS NOT NULL AND media_mime_type IS NULL)`, `kind = 'Text' OR media_mime_type IS NOT NULL`, `NOT is_voice_note OR kind = 'Audio'`, plus `status IN ('Scheduled','Sending','Sent','Failed','Cancelled')`.
- `messages.retry_disabled boolean NOT NULL DEFAULT false` (`HasDefaultValue(false).HasSentinel(false)`, like `imported`).

- [ ] **Step 1: Write the failing tests** (Testcontainers, seed via `MessagingApiHarness.SeedConversationAsync`, insert with `ExecuteAsync`)

```csharp
[Fact] public async Task ASecondActiveRowInTheConversationIsAlreadyExists() // via repository AddAsync → ResourceConflictException.Code == "messaging.scheduled.already_exists"
[Fact] public async Task ACancelledRowDoesNotCountAsActive()
[Fact] public async Task AFailedRowStillCountsAsActive()
[Fact] public async Task TheSameClientIdTwiceIsAlreadyExists()
[Fact] public async Task TheShapeChecksRejectATextWithMedia() // raw INSERT → PostgresException SqlState 23514
[Fact] public async Task RetryDisabledDefaultsToFalseOnExistingMessages() // SeedOutboundAsync then SELECT retry_disabled == false
[Fact] public async Task LockSeesTheRowAndCommitDiscardBurnsTheFailedMessage()
```

- [ ] **Step 2: Run to verify RED**

Run: `$env:Seed__Enabled = "false"; dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ScheduledMessagePersistenceTests" -- xUnit.MaxParallelThreads=4`
Expected: build error (missing `IScheduledMessageRepository`).

- [ ] **Step 3: Implement mapping, repository, lock, `MessagingSaveChanges` branches; generate the migration with the design-factory command; review the generated SQL for the filtered indexes and CHECKs.**

- [ ] **Step 4: Run to verify GREEN** (same command). Expected: 7 PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ScheduledMessagePersistenceTests.cs
git commit -m "feat(messaging): persist scheduled messages and retry_disabled"
```

---

### Task 3: `retry_disabled` in the send path (D8b) and `MessageDto.retryDisabled`

**Files:**
- Modify: `Application/IOutboundMessages.cs` (`ExistingOutbound` gets `bool RetryDisabled = false` as last parameter)
- Modify: `Infrastructure/Persistence/OutboundMessages.cs` (both `SELECT`s add `m.retry_disabled AS "RetryDisabled"`; `ExistingRow.RetryDisabled`; `ToExisting`)
- Modify: `Application/OutboundGuards.cs` (`FindAlreadySentAsync`, `TakeInFlightAsync`)
- Modify: `Application/IMessageQueries.cs` (`MessageRow` + `bool RetryDisabled = false`), `Infrastructure/Persistence/MessageQueries.cs:15` projection, `Application/MessagingDtos.cs` (`MessageDto` + `bool RetryDisabled = false`, with a `<summary>` saying the SPA hides «Reintentar» and an old backend omits it), `Application/MessagingSupport.cs` (`MessageMapping.ToDto`)
- Test: `UnitTests/SendMessageHandlerTests.cs`, `SendMediaMessageHandlerTests.cs`, `SendAudioMessageHandlerTests.cs`; integration `ScheduledMessagePersistenceTests.cs` (one round-trip)

**Interfaces:**
- Produces: `OutboundGuards.FindAlreadySentAsync` throws `ResourceConflictException(MessagingErrorCodes.MessageNotRetriable, …)` when the existing row is `Failed` and `RetryDisabled`; `TakeInFlightAsync` rolls back the claim and throws the same when `claim.Existing` is `Failed` + `RetryDisabled`. Both run before admission/Meta, so they cover `SendMessageHandler`, `SendMediaMessageHandler`, `SendAudioMessageHandler`, `TemplateOutbound` and `BotOutbound`.

- [ ] **Step 1: Write the failing tests**

```csharp
// one per handler test class
[Fact] public async Task ADiscardedFailedClientIdIsNotRetriableAndMetaIsNotCalled()
// bed.Outbound.Existing = new ExistingOutbound(..., MessageStatus.Failed, ..., RetryDisabled: true)
// await Assert.ThrowsAsync<ResourceConflictException>(...) → Code == "messaging.message.not_retriable"; bed.Meta.Sent is empty; no claim
[Fact] public async Task ADiscardedClientIdSeenOnlyInsideTheClaimIsNotRetriable() // Existing null on lookup, claim.Existing Failed+RetryDisabled → same code, claim rolled back
// MessageMapping
[Fact] public void RetryDisabledTravelsInTheDto()
```

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~NotRetriable|FullyQualifiedName~RetryDisabled"`; expected compile error on `RetryDisabled`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — same filter, then the full `SendMessageHandlerTests|SendMediaMessageHandlerTests|SendAudioMessageHandlerTests` classes stay green.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): refuse resending a discarded failed message"` (add touched files explicitly).

---

### Task 4: Extract the media preparation from `SendMediaMessageHandler`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/OutboundMediaPreparer.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SendMediaMessage.cs` (lines 90–93 `Prepared`, 264–320 `PrepareAsync`/`GenericNameOf` move out; `PrefixedHashingStream` stays internal in the same assembly)
- Test: `UnitTests/OutboundMediaPreparerTests.cs`; existing `SendMediaMessageHandlerTests.cs` unchanged

**Interfaces:**
- Produces:
  - `internal sealed class OutboundMediaPreparer(IWhatsAppCloudClient meta, IMessagingMediaStore store, IMessagingImageCompressor compressor)`
  - `internal sealed record PreparedOutboundMedia(string MimeType, string FileName, long SizeBytes, string? Sha256Hex, byte[]? Compressed, MessagingGraphResult<string>? Upload)` (today's `Prepared`).
  - `Task<PreparedOutboundMedia> PrepareAsync(MessagingSender? sender, string sourceKey, long sizeBytes, string fileName, string extension, Func<Task> discardSource, CancellationToken)` — exact behavior of today's private `PrepareAsync` (one read, sniff, compress image, Meta upload only when `sender` is not null; `discardSource` replaces the `DiscardAsync(stagingKey)` calls).
  - `Task<(MessagingGraphResult<string> Upload, string? Sha256Hex)> UploadStoredAsync(MessagingSender sender, string key, long sizeBytes, string mimeType, MessageKind kind, string extension, CancellationToken)` — streams an already-validated stored object to Meta with the generic multipart name (`image.jpg` for images, `GenericNameOf` otherwise); throws `MessagingDomainException(ScheduledMediaMissing)` when `store.OpenReadAsync` returns `null`.
- `SendMediaMessageHandler` builds `new OutboundMediaPreparer(meta, store, compressor)` like it builds `OutboundGuards` (no DI change, so `MessagingTestBed.SendMediaHandler` keeps compiling).

- [ ] **Step 1: Write the failing tests** for the new method only

```csharp
[Fact] public async Task UploadStoredSendsTheObjectWithTheGenericNameAndHashesIt() // meta.Uploads[0].FileName == "document.pdf"; Sha256Hex == hex(SHA256(bytes))
[Fact] public async Task UploadStoredOfAMissingObjectIsMediaMissing()
```

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~OutboundMediaPreparerTests"`; expected missing type.
- [ ] **Step 3: Move the code, add `UploadStoredAsync`.**
- [ ] **Step 4: GREEN** — the new tests **and** `--filter "FullyQualifiedName~SendMediaMessageHandlerTests"` pass with zero edits to that file (`git diff --stat tests/` shows only the new file).
- [ ] **Step 5: Commit** — `git commit -m "refactor(messaging): extract outbound media preparation"`.

---

### Task 5: Create (JSON) and GET handlers, DTO and validator

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessages.cs`
- Test: `UnitTests/CreateScheduledMessageHandlerTests.cs`, `UnitTests/CreateScheduledMessageValidatorTests.cs`, `UnitTests/ScheduledMessageTestDoubles.cs` (`InMemoryScheduledMessageRepository : IScheduledMessageRepository` that can throw `already_exists`)

**Interfaces:**
- Consumes: `IScheduledMessageRepository` (Task 2), `OutboundMediaPreparer.PrepareAsync` (Task 4), `ScheduledMessage.EnsureSchedulable`, `ScheduledText.MeasuredLength` (Task 1), `OutboundGuards.LoadAsync/AdmitAsync` (existing).
- Produces:
  - `sealed record ScheduledMessageDto(Guid Id, Guid ConversationId, Guid ClientId, string Status, DateTimeOffset ScheduledAt, string Kind, bool IsVoiceNote, string? Text, string? Caption, ScheduledMediaDto? Media, MemberRefDto Author, MemberRefDto CreatedBy, MemberRefDto? UpdatedBy, DateTimeOffset? UpdatedAt, string? FailureCode, bool CanRetry, Guid? SentMessageId, long Version)`; `sealed record ScheduledMediaDto(string MimeType, string? FileName, long SizeBytes)`; `sealed record ScheduledMessageEnvelopeDto(ScheduledMessageDto? ScheduledMessage)`; `sealed record ScheduleMessageResult(bool Created, ScheduledMessageDto Body)`.
  - `sealed class ScheduledMessageMapper(IMessagingMemberNames memberNames)` → `Task<ScheduledMessageDto> ToDtoAsync(Guid tenantId, ScheduledMessage message, ConversationRow conversation, DateTimeOffset now, CancellationToken)`; `CanRetry = Status == Failed && ScheduledMessage.IsWindowOpenForSending(conversation.LastInboundAt, now) && conversation.BlockedAt is null`; unknown names → `MessageMapping.DeletedMemberName`. Doc comment states the BFF reason for `canRetry`.
  - `sealed record CreateScheduledMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, DateTimeOffset? ScheduledAt, string? Text, Guid? UploadId, string? FileName, string? Caption) : ICommand<ScheduleMessageResult>`
  - `CreateScheduledMessageValidator`: `clientId` not null/empty; `scheduledAt` not null; exactly one of `text` / `uploadId` (key `text`, «Escribe un mensaje o adjunta un archivo, no los dos.»); with `text`: `SendMessageValidator.IsAcceptableText(text)` and `ScheduledText.MeasuredLength(text.Trim()) <= SendMessageValidator.TextMaxLength` (message «Escribe un mensaje de hasta 4096 caracteres.»); with `uploadId`: the `fileName`/`caption` rules of `SendMediaMessageValidator` (caption measured with `MeasuredLength` ≤ `SendMediaMessageValidator.MaxCaptionLength`, audio without caption). Keys via `OverridePropertyName`.
  - `CreateScheduledMessageHandler : ICommandHandler<CreateScheduledMessageCommand, ScheduleMessageResult>`
  - `sealed record GetScheduledMessageQuery(Guid TenantId, Guid ConversationId) : IQuery<ScheduledMessageEnvelopeDto>`; `GetScheduledMessageHandler` (permission `ConversationRead`, loads the conversation with `MessagingNotFound.Conversation`, returns `FindActiveAsync` or `null`).

Create order (spec «Orden del POST», adapted — see Gap G1): validator (shape) → `MessagingAuthorization.EnsureAsync(…, ConversationManage)` → `FindByClientIdAsync` → if found return `Created=false` with that row in any status → `LoadAsync` → `EnsureSchedulable(scheduledAt, conversation.LastInboundAt, now)` → Instagram text > 1000 measured (422 `validation.failed`, key `text`, the same message as `SendMessageHandler`) → `AdmitAsync(replyTo: null)` → media (below) → `AddAsync`. On `ResourceConflictException` `already_exists`: re-read by `clientId`; if found return it with `Created=false`, else delete the just-written scheduled object (best effort) and rethrow.

Media at create (D6): extension/format checks as `SendMediaMessageHandler` lines 110–122 (`OutboundMediaPolicy.FormatOf`, `IsAllowed(declared, channel)`); `staging.StatAsync(MessagingMediaKeys.Staging(...))` → `null` means re-read by `clientId` (return 200 if found) else `upload_not_found`; size check vs `OutboundMediaPolicy.MaxBytesOf` (`too_large`, delete staging); `preparer.PrepareAsync(sender: null, …)` → image: `store.UploadAsync(scheduledKey, compressed, "image/jpeg")`; other: `staging.PromoteAsync(stagingKey, scheduledKey, staged.ETag)`; then delete staging. New key helper `MessagingMediaKeys.Scheduled(Guid tenantId, Guid scheduledMessageId, string extension)` → `messaging/scheduled/{tenantId}/{scheduledMessageId}{ext}` (images use `.jpg`). Storage failure → `MessagingDomainException(MediaUploadFailed)`. `kind = OutboundMediaPolicy.KindOf(format)`, `isVoiceNote = false`.

- [ ] **Step 1: Write the failing tests** (spec tests 3 and 3b)

```csharp
[Theory] [InlineData(Scheduled)] [InlineData(Failed)] [InlineData(Sent)] [InlineData(Cancelled)]
public async Task AnExistingClientIdReturnsTheRowWithoutStagingAdmissionOrTimeChecks(ScheduledMessageStatus status)
// staging fake throws if StatAsync is called; conversation Resolved; scheduledAt in the past → result.Created == false, Body.Status == status
[Fact] public async Task ABlockedContactCannotSchedule() // "messaging.contact.blocked"
[Fact] public async Task AResolvedConversationCannotSchedule() // "messaging.conversation.not_open"
[Fact] public async Task AnotherAssigneesConversationCannotSchedule() // "messaging.conversation.assigned_to_other"
[Fact] public async Task SchedulingAnUnassignedConversationTakesIt() // bed.Repository auto-assign called with the member
[Fact] public async Task TooSoonAndOutsideWindowAreRejectedBeforeAdmission() // no auto-assign recorded
[Fact] public async Task AMissingStagingIsUploadNotFound()
[Fact] public async Task AnOversizedOrWrongSignatureFileIsRejected() // too_large / unsupported_format
[Fact] public async Task AnImageIsCompressedStoredUnderScheduledAndTheStagingDeleted() // store has "messaging/scheduled/{tenant}/{id}.jpg", staging key gone, Media.MimeType "image/jpeg"
[Fact] public async Task RetryAfterLosingThe201ReturnsTheSameRowNotUploadNotFound() // 3b: second call same clientId+uploadId → Created false, same Id
[Fact] public async Task InstagramRejectsTextOver1000MeasuredWithTheGreeting()
[Fact] public async Task InstagramRejectsAFormatItDoesNotAccept() // .docx → unsupported_format
[Fact] public async Task TheRaceOnTheClientIdIndexReturnsTheWinner() // repository throws already_exists then finds row
[Fact] public async Task GetReturnsOnlyActiveRowsAndCanRetryFollowsTheWindow()
// validator
[Fact] public void TextAndUploadIdTogetherOrNeitherIsInvalid()
[Fact] public void TheGreetingTokenCountsThirteenAgainstTheLimit() // 4091 chars + "{saludo}" (=4099 measured) invalid
```

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~CreateScheduledMessage"`; expected missing types.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — same filter.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): schedule a text or attachment message"`.

---

### Task 6: Create a scheduled voice note (multipart)

**Files:**
- Modify: `Application/ScheduledMessages.cs`
- Test: `UnitTests/CreateScheduledVoiceNoteHandlerTests.cs`

**Interfaces:**
- Produces: `sealed record CreateScheduledVoiceNoteCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, DateTimeOffset? ScheduledAt, byte[]? Audio) : ICommand<ScheduleMessageResult>`; `CreateScheduledVoiceNoteValidator` copies `SendAudioMessageValidator` rules (`clientId`, `file` 1 B–`SendAudioMessageValidator.MaxAudioBytes`, `OggOpusSniffer.IsOggOpus`) plus `scheduledAt` required; `CreateScheduledVoiceNoteHandler`.
- Same order as Task 5. Instagram: transcode with `IOpusTranscoder` exactly as `SendAudioMessageHandler.Transcode` (lines 179–197; extract it to an `internal static` helper `InstagramVoiceNote.Transcode(IOpusTranscoder, byte[])` without changing `SendAudioMessageHandlerTests`), store as `InstagramAudio.WavMimeType`. Stored with `store.UploadAsync(MessagingMediaKeys.Scheduled(..., ".ogg" | ".wav"))`, `kind = Audio`, `isVoiceNote = true`, `fileName = null`, `sha256` of the stored bytes.

- [ ] **Step 1: failing tests**

```csharp
[Fact] public async Task AVoiceNoteIsStoredAsOggUnderScheduled()
[Fact] public async Task InstagramStoresTheVoiceNoteTranscodedToWav() // MediaMimeType == InstagramAudio.WavMimeType
[Fact] public async Task RetryingTheSameClientIdReturnsTheStoredRow() // 3b for multipart: no second UploadAsync
[Fact] public async Task ANonOpusFileIsAFileValidationError() // errors key "file"
```

- [ ] **Step 2: RED**, **Step 3: implement**, **Step 4: GREEN** (`--filter "FullyQualifiedName~CreateScheduledVoiceNote|FullyQualifiedName~SendAudioMessageHandlerTests"`).
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): schedule a voice note"`.

---

### Task 7: Management — edit, cancel, send now, retry, discard, and audit

**Files:**
- Modify: `Application/ScheduledMessages.cs`, `Application/IMessagingAuditRecorder.cs`, `Infrastructure/Persistence/MessagingAuditRecorder.cs`
- Test: `UnitTests/ScheduledMessageManagementTests.cs`, `UnitTests/DiscardScheduledMessageTests.cs`

**Interfaces:**
- Produces:
  - `IMessagingAuditRecorder.RecordScheduledMessage(Guid tenantId, Guid actorId, bool bySystem, string action, Guid scheduledMessageId, DateTimeOffset occurredAt, IReadOnlyCollection<string>? changedFields = null)` (implementation like `RecordTemplate`, actor type `System` when `bySystem`); `MessagingAuditActions.ScheduledMessageResourceType = "scheduled_message"` and `ScheduledMessageCreated = "messaging.scheduled_message.created"`, `…Updated ".updated"`, `…Cancelled ".cancelled"`, `…SendNow ".send_now"`, `…Retried ".retried"`, `…Discarded ".discarded"`, `…Sent ".sent"`, `…Failed ".failed"`. Update `RecordingAudit` test double.
  - Commands (all `long? ExpectedVersion` validated `> 0` with key `expectedVersion`, like `Campaigns.cs:188`): `UpdateScheduledMessageCommand(Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, long? ExpectedVersion, DateTimeOffset? ScheduledAt, string? Text, string? Caption) : ICommand<ScheduledMessageDto>`; `CancelScheduledMessageCommand(…, long? ExpectedVersion) : ICommand<bool>`; `SendScheduledMessageNowCommand : ICommand<ScheduledMessageDto>`; `RetryScheduledMessageCommand : ICommand<ScheduledMessageDto>`; `DiscardScheduledMessageCommand : ICommand<bool>`.
  - Handlers check only `MessagingAuthorization.EnsureAsync(…, ConversationManage)` + active membership (`IMembershipDirectory.FindActiveMembershipIdAsync`, 403 otherwise) + conversation exists (`MessagingNotFound.Conversation`) — **no** assignee check (D3/X3). Then `repository.LockAsync` → `null` = `ResourceNotFoundException(ScheduledNotFound)` → status guard (409) → version (412 `concurrency.conflict`) → transition → audit in the same transaction → `CommitAsync` (`CommitDiscardAsync` for discard) → `notifier.ConversationChangedAsync` after commit (swallow failures like `OutboundSettlement.NotifyConsolidatedAsync`) → for cancel/discard delete the media object best effort.
  - Update validator (`UpdateScheduledMessageValidator`, needs the stored kind, so the kind-dependent rules run in the handler as `ValidationException`): `text` present on a non-`Text` kind → `errors.text`; `caption` present on `Text` or voice note/audio → `errors.caption`; `text` `""`/whitespace on `Text` → `errors.text` with `SendMessageValidator`'s message; measured limits as Task 5. `scheduledAt` is **optional** (`DateTimeOffset?` in command and request; the validator has no `NotNull` rule for it): omitted keeps the stored time. When present, `EnsureSchedulable` runs only if it differs from the stored value (Gap G4). An `Edit` with no `ChangedFields` returns 200 without commit or audit.
  - Retry: `ScheduledMessage.IsWindowOpenForSending(conversation.LastInboundAt, now)` false → `outside_window` (422).

- [ ] **Step 1: failing tests** (spec tests 4 and 5b)

```csharp
[Fact] public async Task AnotherMemberWithPermissionCanEditCancelAndSendNowWithoutBeingTheAssignee()
[Fact] public async Task AStaleExpectedVersionIs412() // RequestConcurrencyException "concurrency.conflict"
[Fact] public async Task EditingASendingRowIsNotEditable() // 409 messaging.scheduled.not_editable, checked before the version
[Fact] public async Task RetryingAScheduledRowIsNotRetriable()
[Fact] public async Task AnOmittedOrNullFieldIsKept()
[Fact] public async Task APutWithoutScheduledAtKeepsTheStoredTime() // command ScheduledAt null, Caption "nuevo" → dto.ScheduledAt == stored; no too_soon even if stored time is < now + 1 min
[Fact] public async Task AnEmptyCaptionClearsIt()
[Fact] public async Task AnEmptyTextIsAValidationErrorOnText()
[Fact] public async Task ACaptionOnAVoiceNoteIsAValidationErrorOnCaption()
[Fact] public async Task AnIdenticalPutDoesNotBumpTheVersionNorAudit()
[Fact] public async Task ChangedFieldsListOnlyWhatReallyChanged() // audit.Entries[0].ChangedFields == ["caption"]
[Fact] public async Task SendNowMovesScheduledAtToNowAndAudits() // ".send_now"
[Fact] public async Task RetryOutsideTheWindowIsOutsideWindow()
[Fact] public async Task AnIdOfAnotherConversationIsNotFound() // messaging.scheduled.not_found
// DiscardScheduledMessageTests
[Fact] public async Task DiscardCancelsTheRowAndBurnsTheFailedMessage() // lock.CommitDiscardAsync called; status Cancelled; audit ".discarded"
[Fact] public async Task DiscardWithoutAMessageRowOnlyCancels()
```

- [ ] **Step 2: RED**, **Step 3: implement**, **Step 4: GREEN** — `--filter "FullyQualifiedName~ScheduledMessageManagementTests|FullyQualifiedName~DiscardScheduledMessageTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): manage a scheduled message"`.

The create handlers (Tasks 5–6) also call `RecordScheduledMessage(..., ScheduledMessageCreated)` before `AddAsync`; add the assertion `AnInsertIsAudited` to `CreateScheduledMessageHandlerTests` in this task.

---

### Task 8: `ScheduledMessageExecutor`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessageExecutor.cs`
- Modify: `Application/ScheduledMessagePorts.cs` (store port)
- Test: `UnitTests/ScheduledMessageExecutorTests.cs`, `UnitTests/ScheduledMessageTestDoubles.cs` (`FakeScheduledMessageStore`)

**Interfaces:**
- Produces:
  - `sealed record ClaimedScheduledMessage(Guid Id, Guid TenantId, Guid ConversationId, Guid ClientId, MessageKind Kind, bool IsVoiceNote, string? Text, string? Caption, string? MediaStorageKey, string? MediaMimeType, string? MediaFileName, long? MediaSizeBytes, string? MediaSha256, Guid AuthorMemberId, int Attempts)`
  - `interface IScheduledMessageStore { Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimDueAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken); Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimExpiredAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken); Task<bool> CompleteSentAsync(ClaimedScheduledMessage message, Guid sentMessageId, DateTimeOffset now, CancellationToken); Task<bool> CompleteFailedAsync(ClaimedScheduledMessage message, string failureCode, DateTimeOffset now, CancellationToken); Task<IReadOnlyList<PurgeableScheduledMedia>> ListPurgeableAsync(int take, CancellationToken); Task ClearMediaKeyAsync(Guid tenantId, Guid id, CancellationToken); }`; `sealed record PurgeableScheduledMedia(Guid TenantId, Guid Id, string StorageKey)`. `Complete*` are CAS on `(id, status = 'Sending', attempts)` and write the `.sent`/`.failed` audit (`bySystem: true`) in the same transaction; `false` = nothing written.
  - `public sealed class ScheduledMessageExecutor(IConversationQueries conversations, IConversationRepository repository, IOutboundMessages outbound, IWhatsAppCloudClient meta, IInstagramClient instagram, IMessagingMediaStore store, IMessagingUploadStaging staging, IMessagingImageCompressor compressor, IMessagingConnectionDirectory connections, IMessagingNotifier notifier, ReadReceipts receipts, IScheduledMessageStore scheduled, ITenantClock tenantClock, IClock clock)` with `Task ExecuteAsync(ClaimedScheduledMessage message, CancellationToken cancellationToken)`.

Algorithm (spec «Ejecución», in order; every write after the claim uses `CancellationToken.None`):
1. `conversations.FindAsync(tenantId, conversationId)`; `null` → return without writing (Gap G6: the FK cascade removes the row with its conversation).
2. `outbound.FindByClientIdAsync`: status not `Failed` → `CompleteSentAsync(existing.Id)` without Meta.
3. `BlockedAt` → `Failed messaging.contact.blocked`; `LastInboundAt + Conversation.WindowLength <= now` → `Failed messaging.window_closed`; sender via `connections.ResolveSenderAsync`/`ResolveInstagramSenderAsync` `null` → `Failed messaging.connection_unavailable`. No claim, no Meta call. No check of `Status`, `AssignedMemberId` or `AutomationStatus` (D4).
4. Greeting: `TimeOfDayGreeting.For(TimeOnly.FromDateTime(calendar.ToLocal(calendar.UtcNow).DateTime))` (same as `CampaignDispatcher.GreetingAsync`), applied with `ScheduledText.ResolveGreeting` to text or caption.
5. Media before the claim: `messageId = existing Failed row id ?? Guid.CreateVersion7()`; `staging.StatAsync(scheduledKey)` `null` → `Failed media_missing`; `staging.PromoteAsync(scheduledKey, MessagingMediaKeys.For(tenant, messageId), eTag)`; WhatsApp: `preparer.UploadStoredAsync(...)` — any exception or unsuccessful upload → delete final key if the message row is new, `Failed messaging.scheduled.media_missing`.
6. `outbound.ClaimAsync(new OutboundDraft(messageId, …, ClientId, Text, SentByMemberId: AuthorMemberId, now, Kind, Media, Caption))`. If `claim.MessageId != messageId`: delete the copy, promote again to the claimed id (WhatsApp upload is id-independent).
7. Send with the same calls as today: `meta.SendTextAsync/SendImageAsync/SendVideoAsync/SendDocumentAsync/SendAudioAsync(voice: IsVoiceNote)` with callback `SendMessageHandler.CallbackPrefix + claim.MessageId`; Instagram: `instagram.SendTextAsync`, or `store.CreateDownloadUrlAsync(finalKey, SendMediaMessageHandler.InstagramDownloadLifetime)` + `instagram.SendAttachmentAsync(…, OutboundMediaPolicy.InstagramAttachmentType(format) | InstagramAudio.AttachmentType)` (caption never travels to Instagram).
8. `new OutboundSettlement(repository, connections, notifier, receipts, clock).SettleAsync(…, seen: 0)`. Return normally → `CompleteSentAsync(claim.MessageId)`; `MessagingDomainException e` → `CompleteFailedAsync(e.Code)` (`message.rejected`, `window_closed`, `connection_unavailable` come from the settlement table); any other exception after the claim → `claim.RollbackAsync`, delete final key if `claim.Inserted`, `CompleteFailedAsync(ScheduledUnknownOutcome)`.
9. After a successful `Complete*`: `notifier.ConversationChangedAsync` (swallowed failure); on `Sent` delete `MediaStorageKey` best effort and `ClearMediaKeyAsync`.

- [ ] **Step 1: failing tests** (spec test 5)

```csharp
[Fact] public async Task AClosedWindowFailsWithoutClaimOrMeta() // store.Failed[0].Code == "messaging.window_closed"; bed.Outbound.Claims empty; bed.Meta.Sent empty
[Fact] public async Task ABlockedContactFails()
[Fact] public async Task APausedConnectionFails() // bed.Connections.Sender = null → "messaging.connection_unavailable"
[Fact] public async Task MetaRejectingLeavesBothFailed() // GraphError 131026 → "messaging.message.rejected"; claim committed Failed
[Theory] [InlineData(6, "Buenos días")] [InlineData(13, "Buenas tardes")]
public async Task TheGreetingUsesTheTenantClock(int localHour, string greeting) // sent text starts with greeting
[Fact] public async Task AResolvedOtherAssigneeAndActiveAssistantStillSendWithoutChangingState() // repository records no assign/reopen; settlement seen == 0
[Fact] public async Task ARetryReusesTheFailedRowAndItsFinalKey() // store key "messaging/{tenant}/{existingId}"
[Fact] public async Task AnAlreadySentClientIdCompletesSentWithoutMeta()
[Fact] public async Task TheCopyToTheFinalKeyHappensBeforeTheClaim()
[Theory] missing object / failing promote / failing Meta upload → "messaging.scheduled.media_missing", no claim
[Fact] public async Task AnExceptionAfterTheClaimIsUnknownOutcomeAndANewRowIsRolledBack()
[Fact] public async Task AnExceptionAfterTheClaimLeavesAReusedFailedRowIntact() // claim not inserted → final key kept
[Fact] public async Task AVoiceNoteGoesWithVoiceTrue()
```

- [ ] **Step 2: RED**, **Step 3: implement**, **Step 4: GREEN** — `--filter "FullyQualifiedName~ScheduledMessageExecutorTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): execute a claimed scheduled message"`.

---

### Task 9: Dispatcher (tick, recovery, purge)

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ScheduledMessageDispatcher.cs`
- Test: `UnitTests/ScheduledMessageRecoveryTests.cs`

**Interfaces:**
- Produces: `public sealed class ScheduledMessageDispatcher(IScheduledMessageStore store, IOutboundMessages outbound, IMessagingMediaStore media, IMessagingNotifier notifier, IClock clock)` with `public static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(10); public const int BatchSize = 20;` and
  - `Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimDueAsync(CancellationToken)` → `store.ClaimDueAsync(BatchSize, now, LeaseLength)`.
  - `Task<int> RecoverExpiredAsync(CancellationToken)` (D9): `store.ClaimExpiredAsync` then per row `outbound.FindByClientIdAsync`: `Sent|Delivered|Read` → `CompleteSentAsync(row.Id)`; `Failed` → `CompleteFailedAsync(MessagingErrorCodes.MessageRejected)`; none → `CompleteFailedAsync(ScheduledUnknownOutcome)`. Never back to `Scheduled`. Notify after each successful completion.
  - `Task<int> PurgeAsync(CancellationToken)`: `ListPurgeableAsync(BatchSize)` → `media.DeleteAsync` (swallow) → `ClearMediaKeyAsync`.
- `ClaimExpiredAsync` re-leases (`locked_until = now + lease`) **without** touching `attempts`, so the original worker's CAS on `(Sending, attempts)` still matches until recovery writes the terminal state.

- [ ] **Step 1: failing tests** (spec test 6)

```csharp
[Fact] public async Task AnExpiredLeaseWithASentMessageBecomesSent()
[Fact] public async Task AnExpiredLeaseWithAFailedMessageBecomesFailedRejected()
[Fact] public async Task AnExpiredLeaseWithoutAMessageIsUnknownOutcomeAndNeverRescheduled() // store.Rescheduled is empty
[Fact] public async Task ALateCompletionAfterRecoveryWritesNothing() // fake CAS returns false on second Complete*; no second notify
[Fact] public async Task PurgeDeletesAndClearsTheKeyEvenIfDeleteFails()
```

- [ ] **Step 2: RED**, **Step 3: implement**, **Step 4: GREEN** — `--filter "FullyQualifiedName~ScheduledMessageRecoveryTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): recover and purge scheduled messages"`.

---

### Task 10: Store SQL, worker, options and DI

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/ScheduledMessages/ScheduledMessageStore.cs`, `ScheduledMessageWorker.cs`
- Modify: `Infrastructure/Options/MessagingWorkerOptions.cs` (`public int ScheduledPollSeconds { get; set; } = 5;`), `MessagingInfrastructureExtensions.cs` (lines ~128–130: `AddScoped<IScheduledMessageStore, ScheduledMessageStore>()`, `AddScoped<ScheduledMessageDispatcher>()`, `AddScoped<ScheduledMessageExecutor>()`, `AddHostedService<ScheduledMessageWorker>()`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs` (`UseSetting("Messaging:Workers:ScheduledPollSeconds", "3600")` next to line 290; `DrainScheduledMessagesAsync(host)` like `DrainCampaignsAsync`)
- Test: `IntegrationTests/ScheduledMessageWorkerTests.cs`

**Interfaces:**
- Consumes: Tasks 8–9.
- Produces: `internal sealed partial class ScheduledMessageWorker(IServiceScopeFactory, IOptions<MessagingWorkerOptions>, TimeProvider, ILogger<ScheduledMessageWorker>) : BackgroundService` with `internal const int Parallelism = 4;` and `internal Task DrainAsync(CancellationToken)`: one scope → `RecoverExpiredAsync` + `ClaimDueAsync`; `Parallel.ForEachAsync` with one scope per row → `ScheduledMessageExecutor.ExecuteAsync` (exceptions logged with `[LoggerMessage]`, never abort the batch, as `CampaignDispatchWorker.SendOneAsync`); `PurgeAsync` once per `PurgeIntervalHours`.
- `ClaimDueAsync` SQL is the spec's `UPDATE … WHERE id IN (SELECT … WHERE status = 'Scheduled' AND scheduled_at <= {now} ORDER BY scheduled_at LIMIT {take} FOR UPDATE SKIP LOCKED) RETURNING …` (bump `version`, `attempts`). `ClaimExpiredAsync`: `status = 'Sending' AND locked_until < {now}` with `FOR UPDATE SKIP LOCKED`, sets only `locked_until`. `Complete*`: one transaction: `UPDATE … SET status, sent_message_id | failure_code, locked_until = NULL, version = version + 1 WHERE id = {id} AND status = 'Sending' AND attempts = {attempts}`; if 1 row, `audit.RecordScheduledMessage(..., bySystem: true)` + `MessagingSaveChanges.SaveAsync`; commit. `ListPurgeableAsync`: `status IN ('Sent','Cancelled') AND media_storage_key IS NOT NULL`.

- [ ] **Step 1: failing tests** (spec test 9; storage via the `FakeBucket` of `SendMediaMessageApiTests.cs` moved to `IntegrationTests/FakeMessagingBucket.cs`, no behavior change)

```csharp
[Fact] public async Task ADueTextIsSentOnceAndTheRowBecomesSent() // MetaHandler.Requests count == 1; scheduled status 'Sent'; sent_message_id = messages.id; audit ".sent" actor System
[Fact] public async Task TwoConcurrentDrainsSendOnce() // Task.WhenAll(DrainAsync, DrainAsync) → one Meta "/messages" request, one messages row
[Fact] public async Task AnEditAfterTheClaimIsNotEditable() // claim via store, then PUT → 409 messaging.scheduled.not_editable
[Fact] public async Task AnEditHoldingTheRowMakesTheWorkerSkipIt() // open LockAsync, DrainAsync → 0 requests; dispose lock
[Fact] public async Task ASentRowDeletesItsScheduledObject()
[Fact] public async Task AnExpiredSendingRowWithoutMessageIsUnknownOutcome() // seed Sending + locked_until past
```

- [ ] **Step 2: RED** — `$env:Seed__Enabled = "false"; dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ScheduledMessageWorkerTests" -- xUnit.MaxParallelThreads=4`; expected missing types.
- [ ] **Step 3: implement.**
- [ ] **Step 4: GREEN** — same command; also `--filter "FullyQualifiedName~SendMediaMessageApiTests"` (moved double).
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): dispatch scheduled messages with a worker"`.

---

### Task 11: HTTP endpoints, registration and API/audit integration tests

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Api/ScheduledMessageEndpoints.cs` (`MapScheduledMessageEndpoints(this RouteGroupBuilder group)`), call it from `MessagingEndpoints.cs` next to `group.MapCampaignEndpoints()` (line ~290)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (8 handlers + `ScheduledMessageMapper`; validators come from `AddValidatorsFromAssemblyContaining`)
- Test: `IntegrationTests/ScheduledMessageApiTests.cs`, `IntegrationTests/ScheduledMessageAuditTests.cs`

**Interfaces:**
- Routes (spec table, under the existing group `/api/v1/tenants/{tenantId:guid}/messaging`): `GET /conversations/{conversationId:guid}/scheduled-message` (`ConversationRead`, 200 envelope); `POST /conversations/{conversationId:guid}/scheduled-messages` (201 / 200 via `ScheduleMessageResult.Created`, as `StartConversationAsync`); `POST …/scheduled-messages/audio` (multipart `clientId`, `scheduledAt`, `file`; body cap `AudioBodyMaxBytes` and the manual form read of `SendAudioMessageAsync`, `.DisableAntiforgery()`, no `.Accepts`); `PUT …/scheduled-messages/{scheduledMessageId:guid}` (200); `POST …/{id}/cancel` (204); `POST …/{id}/send-now` (202 `Results.Accepted(null, dto)`); `POST …/{id}/retry` (202); `POST …/{id}/discard` (204); `GET …/{id}/media` (`ConversationRead`).
- Requests: `ScheduleMessageRequest(Guid? ClientId, DateTimeOffset? ScheduledAt, string? Text, Guid? UploadId, string? FileName, string? Caption)`, `UpdateScheduledMessageRequest(long? ExpectedVersion, DateTimeOffset? ScheduledAt, string? Text, string? Caption)`, `ScheduledMessageVersionRequest(long? ExpectedVersion)`.
- Media: `GetScheduledMessageMediaQuery(Guid TenantId, Guid ConversationId, Guid ScheduledMessageId, string? Range) : IQuery<MediaStreamDto?>`, handler copies `GetMediaHandler`'s range logic over `MediaStorageKey` (`null` row/key → `null` → 404); the endpoint reuses `GetMediaAsync`'s header/range response code (extract it to a private `WriteMediaAsync(HttpContext, MediaStreamDto, Guid id)` in `MessagingEndpoints` and call it from both).

- [ ] **Step 1: failing tests** (spec tests 8 and 10)

```csharp
[Fact] public async Task CreatingATextReturns201WithTheContractShape() // JSON keys exactly as spec «ScheduledMessageDto»
[Fact] public async Task RepeatingTheClientIdReturns200()
[Fact] public async Task ASecondActiveScheduleIs409AlreadyExists()
[Fact] public async Task AnotherTenantIs403() // never 404
[Fact] public async Task AnUnknownIdIs404NotFound() // code messaging.scheduled.not_found
[Fact] public async Task AnUnknownConversationKeeps404ConversationNotFound() // GET, code messaging.conversation.not_found (deploy signal)
[Fact] public async Task SendNowIs202AndTheGetIsEmptyAfterTheWorkerSends()
[Fact] public async Task AVoiceNoteMultipartIs201()
[Fact] public async Task TheMediaRouteServesTheStoredFileWithRanges() // 206 with Range: bytes=0-9
[Fact] public async Task AStaleVersionIs412()
[Fact] public async Task APutWithoutScheduledAtKeepsTheStoredTime() // body { expectedVersion, caption } → 200, scheduledAt unchanged
// ScheduledMessageAuditTests: one row in audit.entries per action
[Fact] public async Task EveryHumanActionLeavesItsEntryWithTheActor() // created, updated(["scheduledAt"]), send_now, cancelled, retried, discarded
[Fact] public async Task TheWorkerLeavesSentAndFailedAsSystem()
```

- [ ] **Step 2: RED** — `$env:Seed__Enabled = "false"; dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ScheduledMessageApiTests|FullyQualifiedName~ScheduledMessageAuditTests" -- xUnit.MaxParallelThreads=4`; expected 404s (routes missing).
- [ ] **Step 3: implement.**
- [ ] **Step 4: GREEN** — same command.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging): expose scheduled message routes"`.

---

### Task 12: README

**Files:** Modify `README.md` (config table line ~150: add `ScheduledPollSeconds 5`; new subsection «Mensajes programados» under § Mensajería (WhatsApp Cloud), line ~3025).

- [ ] **Step 1:** Write the section: routes, statuses, D4 (no state/assignment change, `seen: 0`), D5 `{saludo}` by tenant time, D6 key `messaging/scheduled/` with **no lifecycle rule (or > 2 days)**, D8b `retry_disabled`, D9 no automatic resend, deploy order (backend first).
- [ ] **Step 2:** `git commit -m "docs(messaging): document scheduled messages"` (README only).

---

### Task 13: Final verification

- [ ] **Step 1:** Stop `Api.exe` if running (ask the developer; never kill a process you did not start).
- [ ] **Step 2:** `dotnet build` → `0 Warning(s)`, `0 Error(s)` (TreatWarningsAsErrors).
- [ ] **Step 3:** `dotnet test tests/ArchitectureTests/ArchitectureTests` → all PASS, including `MessagingLayerTests`.
- [ ] **Step 4:** `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests` → all PASS.
- [ ] **Step 5:** `$env:Seed__Enabled = "false"; dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests -- xUnit.MaxParallelThreads=4` → all PASS (includes `QuotationTemplateOutboundTests`, which guards claim/settlement changes).
- [ ] **Step 6:** `git status` clean except intended files; report the literal summary lines of steps 2–5.

---

## Gaps (code vs spec)

- **G1. Create order.** The spec puts the `clientId` lookup before `LoadAsync`. `LoadAsync` is also the tenant/permission/module check, and the repo rule demands 403 before touching the repository. The plan runs shape validation and `MessagingAuthorization.EnsureAsync` first, then the lookup scoped by `(tenantId, conversationId, clientId)`, then `LoadAsync`/admission. Admission, time rules and staging still come after the lookup, as the spec requires.
- **G2. Domain cannot throw HTTP exceptions.** `ResourceConflictException` is in `BuildingBlocks.Application`; `Campaign` exposes `IsEditable`/`IsCancellable` and `CampaignSupport` throws. The plan follows that precedent (`CanEdit`/`CanRetry` + throws in Application).
- **G3. `MessagingAuditTests` does not exist.** No file with that name is in `Modules.Messaging.IntegrationTests`. The plan creates `ScheduledMessageAuditTests.cs`.
- **G4. PUT `scheduledAt`.** The spec table shows `scheduledAt` as required in the PUT body. The orchestrator's contract ruling of 2026-11-09 makes it optional: omitted keeps the stored time. When `scheduledAt` is sent, `too_soon`/`outside_window` apply only if it differs from the stored value; the spec is silent on this, so the owner may revise it. The spec's PUT row should be corrected to match.
- **G5. Retry on a blocked contact.** The error table lists only `outside_window` for retry; `canRetry` is false when blocked, but the endpoint does not reject it. The worker then fails it again with `messaging.contact.blocked`.
- **G6. Conversation missing at execution.** This is not in the spec's failure list. The FK means a deleted conversation removes the row, so the executor returns without writing.
- **G7. `seen: 0` still sends a receipt when `unread_count` is already 0.** `OutboundSettlement.SettleAsync` sends a read receipt whenever `RemainingUnread == 0`. That covers only a thread with nothing unread, so the anti-goal holds. The behavior is the same as `QuotationTemplateOutbound`.
- **G8. Meta upload failure before the claim.** `SendMediaMessageHandler` routes it through `FromUploadFailure` to the settlement table (a 190/133010 code marks the connection `NeedsAttention`). The spec makes it `media_missing` with no message row, so a revoked token does not flag the connection from this path. The plan follows the spec.
- **G9. `ExistingOutbound.FailureCode` is a Meta `int`.** `scheduled_messages.failure_code` is a new `varchar(64)` with QEP codes. No conflict, but the two must not be mixed.
