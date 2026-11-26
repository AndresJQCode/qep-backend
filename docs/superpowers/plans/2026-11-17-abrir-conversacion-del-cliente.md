# Open a customer's conversation without sending — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `POST /api/v1/tenants/{tenantId}/messaging/conversations/open` finds or creates the customer's WhatsApp conversation on one connection, reopens it if resolved and assigns it to the caller if unassigned, without sending anything.

**Architecture:** One command + handler in `Modules.Messaging.Application`, next to `StartConversation.cs`, reusing `IConversationOpener.OpenAsync`, `Conversation.Reopen`, `Conversation.Take` and `ConversationAssignmentSteps.CommitAsync` (events, audit, notifier, summary). One endpoint in `MessagingEndpoints.cs`.

**Tech Stack:** .NET 10, FluentValidation, EF Core (Infrastructure only), xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-abrir-conversacion-del-cliente-design.md`

## Global Constraints

- Work in a worktree `qep-backend-worktrees/abrir-conversacion` on branch `feature/abrir-conversacion` from `develop`; merge goes to `develop`, never `main`.
- TDD: RED before GREEN, literal evidence of both. A RED that fails to compile is evidenced by the build output, no containers.
- Integration tests filtered by test name while iterating (`--filter "FullyQualifiedName~<Name>"`); never two integration runs at once (`Get-Process testhost, dotnet` first).
- `Modules.Messaging.Application` must not reference EF Core/Npgsql (`ArchitectureTests`).
- Permission `messaging.conversation.manage`, module `messaging`. No `If-Match`.
- Existing codes reused verbatim: `messaging.customer.not_found` (404), `messaging.customer.unreachable` (422), `messaging.connection_unavailable` (422), `messaging.channel_unsupported` (422). New: `messaging.conversation.connection_required` (422).
- Response `{ conversation: ConversationSummary, created: bool }`: 201 when created, 200 when found.
- Conventional commits, no AI attribution. `docs/estado.md` only on `develop` at merge.

## Review Focus

1. Conversation **assigned to another advisor** and `Resolved`: reopened, assignment untouched → test in Task 1.
2. **Instagram** `connectionId` passed explicitly: 422 before touching the customer → test in Task 1.
3. Several Active connections but only **one is WhatsApp** (others Instagram): counts as one → test in Task 1.
4. Taking an unassigned conversation that a **bot is attending** pauses the bot (`Take` does it) — accepted, pinned by a test in Task 1 so it is visible.
5. Validation failure / unreachable customer leaves **no row** behind → integration test in Task 2.

---

### Task 1: Command, handler and new error code

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/OpenCustomerConversation.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` (add `ConnectionRequired`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/OpenCustomerConversationHandlerTests.cs` (use `MessagingTestBed`; extend its fakes if the opener or `ListActiveAsync` are missing)

**Interfaces:**
- Produces:
  - `public sealed record OpenCustomerConversationCommand(Guid TenantId, Guid? CustomerId, Guid? ConnectionId) : ICommand<OpenCustomerConversationResult>`
  - `public sealed record OpenedCustomerConversationDto(ConversationSummary Conversation, bool Created)`
  - `public sealed record OpenCustomerConversationResult(OpenedCustomerConversationDto Body, bool Created)`
  - `OpenCustomerConversationValidator` (`customerId` NotNull, NotEqual Guid.Empty, property name `customerId`)
  - `MessagingErrorCodes.ConnectionRequired = "messaging.conversation.connection_required"`

- [ ] **Step 1: Write the failing tests** (names are the assertions):
  - `WithoutConnectionAndOneActiveWhatsAppItOpensThereAndAssignsTheCaller` → `Created == true`, summary `AssignedTo.MemberId == caller`, opener called with that connection.
  - `WithoutConnectionAndSeveralActiveWhatsAppItAnswersConnectionRequired` → `MessagingDomainException` code `messaging.conversation.connection_required`, opener never called, even when the customer already has a conversation on one of them.
  - `WithoutConnectionAndNoActiveWhatsAppItAnswersConnectionUnavailable` → `messaging.connection_unavailable`.
  - `OneWhatsAppPlusInstagramCountsAsOne` → opens on the WhatsApp one.
  - `AnInstagramConnectionIsRefused` → `messaging.channel_unsupported`, customer directory not called.
  - `AnInactiveOrMissingCustomerIs404` / `ACustomerWithoutPhoneOrBsuidIs422Unreachable`.
  - `AnExistingUnassignedConversationIsTakenByTheCaller` → `Created == false`, `Taken` event + audit recorded once.
  - `AnExistingResolvedConversationIsReopenedThenTaken` → status `Open`, events `Reopened` then `Taken` in that order.
  - `AConversationAssignedToSomeoneElseIsLeftAssigned` (also when `Resolved`: reopened, assignee unchanged).
  - `AConversationAlreadyMineAndOpenRecordsNothing` → no event, no audit, no version bump.
  - `TakingAnUnassignedConversationWithABotPausesIt` → same pause event `Take` emits elsewhere.

- [ ] **Step 2: Run, verify RED** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests` fails: `OpenCustomerConversationCommand` not defined.

- [ ] **Step 3: Implement `OpenCustomerConversationHandler.HandleAsync(OpenCustomerConversationCommand, CancellationToken) : Task<OpenCustomerConversationResult>`**
  Order (spec §Rules): validate → authorize + caller membership (as `ConversationAssignmentSteps.LoadAsync`) → if `ConnectionId` given: channel check (`connections.FindChannelAsync`), then `ResolveSenderAsync` (null → `ConnectionUnavailable`); if not given: `ListActiveAsync` filtered to `Channel == "WhatsApp"`, 0 → `ConnectionUnavailable`, >1 → `ConnectionRequired` → customer checks (same as `StartConversationHandler` lines 93-102) → `opener.OpenAsync(tenant, connection, contact, assignedMemberId: member, actorUserId: SubjectId, now)` (a created row comes assigned with its `Taken`) → load with `repository.FindAsync` → if `Status == Resolved`: `Reopen(now)` + `ConversationEvent(Reopened, Actor: member)` → if `AssignedMemberId is null`: `Take(member, now)` → `steps.CommitAsync(tenant, conversation, changes, now)` (it records events + audit, saves once, notifies). Customer checks may go before the connection checks only if Instagram still answers `channel_unsupported` first (Review Focus 2).
  Note: `CommitAsync` audits with `MessagingAuditActions.ForEvent(type)`; confirm `Reopened` maps to `MessagingAuditActions.Reopened`.

- [ ] **Step 4: Run, verify GREEN** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~OpenCustomerConversation"` → all pass.

- [ ] **Step 5: Commit** — `feat(messaging): open a customer's conversation without sending`

### Task 2: Endpoint and integration tests

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (route next to `MapPost("/conversations", …)`; handler next to `StartConversationAsync` ~line 422)
- Create: request record `OpenCustomerConversationRequest(Guid? CustomerId, Guid? ConnectionId)` wherever `StartConversationRequest` lives
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/OpenCustomerConversationApiTests.cs` (copy the setup of `StartConversationApiTests.cs`)

**Interfaces:**
- Consumes: Task 1 command/result.
- Produces: `POST …/messaging/conversations/open` → `Results.Created((string?)null, body)` when `Created`, else `Results.Ok(body)`; `.RequireAuthorization(MessagingPermissions.ConversationManage)`, `Produces<OpenedCustomerConversationDto>(201)`, `Produces<OpenedCustomerConversationDto>()`, problems 403/404/422.

- [ ] **Step 1: Write the failing tests:**
  - `OpensAnEmptyConversationAssignedToTheCallerWith201` → 201, `conversation.assignedTo.isMe == true`, `GET /conversations?status=Open` lists it, `GET …/messages` is empty.
  - `ASecondOpenReturnsTheSameConversationWith200` → same id, 200.
  - `AResolvedConversationComesBackOpen` (resolve it first via `/resolve`).
  - `SeveralWhatsAppConnectionsAnswer422ConnectionRequired` → body `code == "messaging.conversation.connection_required"`.
  - `AnUnreachableCustomerLeavesNoConversation` → 422 and the conversation count does not change.
  - `WithoutManagePermissionIs403`.
  - `TheInboundOfThatNumberLandsInTheOpenedConversation` → after open, ingest an inbound for the customer's phone (as `StartConversationApiTests.TheBsuidThatArrivesLaterIsAdopted…`) → same conversation id, no duplicate.
- [ ] **Step 2: Run, verify RED** — build fails or 404 on the route.
- [ ] **Step 3: Implement the endpoint** (as above).
- [ ] **Step 4: Run GREEN** — `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~OpenCustomerConversationApiTests" -- xUnit.MaxParallelThreads=4`. Then the architecture tests: `dotnet test tests/ArchitectureTests`.
- [ ] **Step 5: Commit** — `feat(messaging): expose POST conversations/open`

Before merging to `develop`: Messaging unit + integration test projects in full, one at a time; update `docs/estado.md` on `develop`.
