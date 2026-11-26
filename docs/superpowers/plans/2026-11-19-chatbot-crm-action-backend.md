# Chatbot «Acción de CRM» block — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `crmAction` flow node (add tag, remove tag, move deal) that the chatbot engine executes against Customers and Pipeline as the system, with simulator, validation, editor data and audit.

**Architecture:** Same shape as `http` / `dataConsent`: the engine emits an effect and stops `Running`; `RunEffects` calls a port (`IAutomationCrm`) in `Modules.Automations.Application`; an adapter in `src/Bootstrapper/` resolves the conversation's customer and calls two new system-actor services (Customers: one-tag add/remove; Pipeline: move the conversation's open deal). The result comes back as `EngineInput.CrmResult` and the walk leaves by `ok` / `skipped` / `error`.

**Tech Stack:** .NET 10, xunit.v3, EF Core/Npgsql (Infrastructure only), Testcontainers (integration only).

**Spec:** `docs/superpowers/specs/2026-11-19-chatbot-crm-action-design.md` (pair: frontend repo, same file name). Read it first.

## Global Constraints

- Flow `schema` stays `1`; every change is additive (deploy backend before frontend).
- Node kind string `"crmAction"`; operations `"addTag"`, `"removeTag"`, `"moveDeal"`; outputs `ok`, `skipped`, `error`.
- Issue codes `crm.tag_missing`, `crm.stage_missing`, `crm.module_off` (errors, block publishing).
- `Modules.Automations.*` references no other module except Tenancy and no EF Core/Npgsql in Application; CRM access only through a port in Automations and an adapter in `src/Bootstrapper/`.
- Audit of Customers and Pipeline carries source `"chatbot"` and the system as actor, never a member. Automations' own audit: `bySystem: true`, resource = the run.
- The simulator never calls the port.
- TDD with literal RED evidence. Presupuesto de pruebas (workspace CLAUDE.md): RED of a compile failure is the build output, no containers; integration filtered by test name while iterating; the touched unit/integration/architecture projects once before merging; never two integration runs at once (`Get-Process testhost, dotnet -ErrorAction SilentlyContinue` first, never stop what you did not launch); foreground runs.
- Commits: conventional, no AI attribution, one per task (`git commit -o <paths>`).

## Review Focus

1. A tag deleted after the flow was published → runtime `error` / `tag_missing`, never an exception that fails the run.
2. Customer already at `CustomerTagAssignment.MaxPerCustomer` (20) → `error` / `tag_limit`; the add is not applied.
3. The Incomplete customer the bot creates from a conversation can be tagged (the reason `Customer.Update` was not reused).
4. A unit that died while `Running` is re-requested by `PendingInputAsync` and the second execution answers `ok` / `already_tagged` (idempotent), writing no second audit row.
5. Two open deals, or an open deal of another customer from this conversation → `skipped`, never a guess, never another customer's deal.

---

## File map

- Domain (`src/Modules/Automations/Modules.Automations.Domain`): `Flow/FlowNodeKinds.cs`, `Flow/FlowDefinition.cs`, `Flow/FlowJson.cs`, `Flow/FlowValidator.cs`, `Flow/FlowIssue.cs` (codes + `FlowValidationContext`), new `Flow/CrmResultCodes.cs`, `Engine/Effect.cs`, `Engine/EngineInput.cs`, `Engine/FlowEngine.cs`.
- Application (`…Automations.Application`): `AutomationsPorts.cs` (ports, audit actions), `Engine/RunEffects.cs`, `Simulations.cs`, `Templates.cs`, `ChatbotEditorBuilder.cs`, `AutomationsDtos.cs`.
- Customers: `Customers.Application/ICustomersAuditPublisher.cs` + `Customers.Infrastructure/Persistence/CustomersAuditPublisher.cs`, new `Customers.Application/ChatbotCustomerTags.cs`, `CustomersInfrastructureExtensions.cs`.
- Pipeline: `Pipeline.Application/PipelinePorts.cs` (`IDealRepository`, `IPipelineAuditRecorder`), `Pipeline.Infrastructure/Persistence/PipelineRepositories.cs`, `PipelineAuditRecorder.cs`, new `Pipeline.Application/ChatbotDealMover.cs`, its DI extension.
- Bootstrapper (`src/Bootstrapper`): new `AutomationCrm.cs`, new `AutomationsCrmDirectory.cs`, new `ConversationCustomerResolver.cs`, edit `AutomationDataConsents.cs`, `QepServiceCollectionExtensions.cs` (~line 1770).
- Tests: `tests/Modules/Automations/Modules.Automations.UnitTests/*`, `…IntegrationTests/*`, `tests/ArchitectureTests/ArchitectureTests/AutomationsLayerTests.cs`, `CompositionRootTests.cs`, Customers and Pipeline test projects.

**Execution blocks (one implementer each, one review each):** Block A = Tasks 1–4 (pure code, no containers). Block B = Tasks 5–7 (Customers + Pipeline entry points). Block C = Tasks 8–11 (wiring, editor, end to end).

---

## Block A — flow contract, engine, simulator, templates

### Task 1: Node kind, JSON, validator

**Files:**
- Create: `…Domain/Flow/CrmResultCodes.cs`
- Modify: `FlowNodeKinds.cs` (const line ~15, `All` 23-26, `OutputsOf` 33-48, `FlowOutputs` 81-105), `FlowDefinition.cs`, `FlowJson.cs` (write ~241-305, read ~370-389), `FlowIssue.cs` (codes 41-155; `FlowValidationContext` 22-25), `FlowValidator.cs` (`OutputLabels` 44-65, `CheckNode` 175, `WrittenVariables` ~745, `KnownWrittenInOrder` ~774)
- Test: `FlowJsonTests.cs` (add the node to `EveryKindJson`; the test asserts `FlowNodeKinds.All.Count` equals the kinds present), `FlowValidatorTests.cs` (new `FlowCrmActionTests.cs` is fine)

**Interfaces:**
- Produces:
  - `FlowNodeKinds.CrmAction = "crmAction"`; `FlowNodeKinds.OutputsOf(CrmActionNode)` → `[FlowOutputs.Ok, FlowOutputs.Skipped, FlowOutputs.Error]`; add `FlowOutputs.Skipped = "skipped"` and its Spanish label in `OutputLabels` («Sin nada que hacer»).
  - `public sealed record CrmActionNode(string Id, string Title, IReadOnlyDictionary<string,string?> Outputs, string Operation, Guid? TagId, Guid? StageId, string SaveTo) : FlowNode(Id, FlowNodeKinds.CrmAction, Title, Outputs);`
  - `public static class CrmOperations { AddTag="addTag"; RemoveTag="removeTag"; MoveDeal="moveDeal"; All; }`
  - `public static class CrmResultCodes` with consts `Added="added", AlreadyTagged="already_tagged", Removed="removed", NotTagged="not_tagged", Moved="moved", AlreadyInStage="already_in_stage", NoOpenDeal="no_open_deal", SeveralOpenDeals="several_open_deals", NoCustomer="no_customer", CustomerUnavailable="customer_unavailable", TagLimit="tag_limit", DealConflict="deal_conflict", StageNotOpen="stage_not_open", ModuleOff="module_off", TagMissing="tag_missing", StageMissing="stage_missing", Failed="failed"` and `static string OutputOf(string code)` → `ok` for added/already_tagged/removed/not_tagged/moved/already_in_stage, `skipped` for no_open_deal/several_open_deals/no_customer, `error` otherwise. `static string SuccessCodeOf(string operation)` → `added`/`removed`/`moved` (used by the simulator). (`tag_missing`/`stage_missing` at runtime are a builder call beyond the spec list: a tag or stage deleted after publishing.)
  - `FlowIssueCodes`: `CrmTagMissing="crm.tag_missing"`, `CrmStageMissing="crm.stage_missing"`, `CrmModuleOff="crm.module_off"`.
  - `FlowValidationContext` gains trailing optionals `bool CustomersEnabled = false, bool PipelineEnabled = false, IReadOnlySet<Guid>? CrmTagIds = null, IReadOnlySet<Guid>? CrmOpenStageIds = null` (null set = empty).
- Consumes: `JsonFields.RequiredOneOf`, `OptionalGuid`, `Text`, helpers `CheckName`, `Error(...)`.

- [ ] **Step 1: RED tests.** In `FlowCrmActionTests.cs` (helpers as in `FlowValidatorTests`): `RoundTripsEveryOperation` (parse/write a node of each operation; the JSON in the spec is the shape: `operation`, `tagId`, `stageId`, `saveTo`, `outputs{ok,skipped,error}`); `NormalizerClearsTheFieldsOfOtherOperations` (an `addTag` node with a `stageId` serializes `stageId` null; `moveDeal` with a `tagId` serializes `tagId` null); `TagMissingWhenEmptyOrNotInCatalog` (two cases → one `crm.tag_missing` error each); `StageMissingWhenEmptyNotOpenOrUnknown`; `ModuleOffWhenCustomersOffForTagOps` and `…PipelineOffForMoveDeal` (`crm.module_off`, and no `tag_missing` noise when the module is off); `SaveToIsWrittenVariable` (`{crm.result}` used by a later message raises no `variable.never_set` warning); `InvalidSaveToNameIsAnError` (same rule as `CheckName`); `UnconnectedSkippedAndErrorAreWarningsNotErrors` (same severity as `http` secondary exits). Add the node to `EveryKindJson` in `FlowJsonTests`.
- [ ] **Step 2: Run** `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowCrmActionTests|FullyQualifiedName~FlowJsonTests"` → Expected: build FAIL (`CrmActionNode`, `CrmOperations` not defined). Paste the build output as RED.
- [ ] **Step 3: Implement** the interfaces above. Normalizer: clearing of the non-chosen id happens in `FlowNormalizer` (add a `CrmActionNode` case; default `_ => node` otherwise). Validator `case CrmActionNode`: module check first, then catalog check, `CheckName(saveTo)`, add `{saveTo}.result` to `WrittenVariables`/`KnownWrittenInOrder`.
- [ ] **Step 4: Run** the same filter → PASS; then the whole `Modules.Automations.UnitTests` project once (the `_ => throw` defaults must not fire anywhere).
- [ ] **Step 5: Commit** `feat(automations): crmAction node in the flow contract and validator`.

### Task 2: Engine

**Files:** Modify `Engine/Effect.cs`, `Engine/EngineInput.cs`, `Engine/FlowEngine.cs` (`Step` dispatch 28-35, `OnHttpResult` 125-139 as the model, `Walk.Enter` ~519-568). Test: new `FlowEngineCrmActionTests.cs` modeled on `FlowEngineDataConsentTests`.

**Interfaces:**
- Produces: `Effect.CrmAction(string NodeId, CrmActionNode Node) : Effect(NodeId)`; `EngineInput.CrmResult(string NodeId, string Code)`.
- Behavior: `Enter` emits the effect and `Stop(node, RunStatus.Running, TraceOutcomes.Pending)`. `OnCrmResult`: if not pending at that node → `Unchanged`; else write `{saveTo}.result = Code` into the run variables and `walk.Leave(node, CrmResultCodes.OutputOf(Code))`.

- [ ] **Step 1: RED tests:** `EnteringEmitsTheEffectAndStopsRunning`; `OkCodeLeavesByOk` (`added`), `SkippedCodeLeavesBySkipped` (`no_open_deal`), `ErrorCodeLeavesByError` (`tag_limit`); `ResultIsWrittenToSaveToDotResult`; `ResultForAnotherNodeIsIgnored` (state unchanged); `UnknownCodeLeavesByError` (`"weird"` → `error`, code still stored).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~FlowEngineCrmActionTests"` → Expected: build FAIL (`Effect.CrmAction` undefined).
- [ ] **Step 3: Implement** as above; copy how `AiAgentNode` writes `saveTo` variables for the variable write.
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(automations): engine step for the crmAction block`.

### Task 3: Simulator

**Files:** Modify `Simulations.cs` (case at ~66 and the effect switch; `lines.System(nodeId, text)`). Test: new `SimulationCrmActionTests.cs` modeled on `SimulationAgentBlockTests` (`new SimulationRunner(http, businessHours, media, clock)`, `runner.RunAsync(flow, Settings, Initial(), new EngineInput.Start(), Context(), Ct)`).

- Behavior: `Effect.CrmAction` counts for the re-entry limit, never touches a port, emits one system line `Simulación: …` (text per operation: «se etiquetaría al cliente con «<tag>»», «se quitaría la etiqueta «<tag>» al cliente», «se movería el negocio a la etapa «<stage>»» — the node only has ids, so the line uses the generic wording «se etiquetaría al cliente», «se quitaría la etiqueta al cliente», «se movería el negocio de la conversación»; names are a frontend concern), feeds `EngineInput.CrmResult(nodeId, CrmResultCodes.SuccessCodeOf(operation))`.

- [ ] **Step 1: RED:** `NeverCallsAnyCrmPort` (runner constructed without a CRM dependency: the test itself proves it; assert the transcript has exactly one system line for the node and the walk reaches the node connected to `ok`); `WritesTheSuccessCodeForEachOperation` (`added`, `removed`, `moved` in `{saveTo}.result`, then a `condition` on it picks the branch); `CountsForTheReentryLimit` (a loop of crm blocks fails with the same loop-detected reason as `http`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SimulationCrmActionTests"` → Expected: FAIL (`InvalidOperationException: Efecto del motor desconocido` or build error). Paste output.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(automations): simulate the crmAction block without writing`.

### Task 4: Templates

**Files:** Modify `Templates.cs` (`Node()` switch 89-129, `WithoutConnections` ~272). Test: `TemplateParametersTests.cs`, `SystemTemplatesTests.cs` neighbors.

- Behavior: `CrmActionNode` is carried through `TemplateParameters.Apply` (title gets `Text(...)`); «guardar como plantilla» clears `TagId` and `StageId` like `ConnectionId` (tenant-specific ids). Duplicate in the same tenant keeps them (`Chatbots.cs` needs no change: `Remap`/`DropForeignMembers` default `_ => node`).

- [ ] **Step 1: RED:** `ApplyCarriesACrmActionNode` (today throws `ArgumentOutOfRangeException`); `SavingAsTemplateClearsTagAndStageIds`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TemplateParametersTests|FullyQualifiedName~SystemTemplatesTests"` → FAIL as above.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS, then the full `Modules.Automations.UnitTests` project once (end of Block A).
- [ ] **Step 5: Commit** `feat(automations): carry crmAction through templates`.

---

## Block B — Customers and Pipeline system entry points

### Task 5: Audit source `chatbot`

**Files:** Modify `ICustomersAuditPublisher.cs` (`PublishBySystem` 52-57), `CustomersAuditPublisher.cs` (42-48; `source ?? "customers"` at line 70), `IPipelineAuditRecorder` (PipelinePorts.cs 103-106) and `PipelineAuditRecorder.cs` (const `Source = "pipeline"`). Test: existing audit-publisher tests in the Customers and Pipeline test projects.

**Interfaces:**
- Produces: `PublishBySystem(Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt, string? source = null)`; `IPipelineAuditRecorder.Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt, string? source = null)`; consts `AuditSources.Chatbot = "chatbot"` in each module (`CustomerAuditSources`, `PipelineAuditSources`).

- [ ] **Step 1: RED:** a Customers test `PublishBySystemWithSourceStoresIt` (entry has `Source == "chatbot"`, actor type `System`, and the default call still stores `"customers"`); a Pipeline test `RecordWithSourceStoresIt` (and default stays `"pipeline"`).
- [ ] **Step 2: Run** the two filtered tests → build FAIL (no `source` parameter).
- [ ] **Step 3: Implement** (optional trailing parameter, existing callers untouched). Add `CustomerAuditActions.TagAdded/TagRemoved`? **No:** reuse `CustomerAuditActions.TagsChanged` (one-tag change is a change of the set); Pipeline reuses `PipelineAuditActions.DealMoved`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(audit): chatbot source for customers and pipeline system audit`.

### Task 6: Customers — chatbot tag service

**Files:** Create `Customers.Application/ChatbotCustomerTags.cs`; modify `CustomersInfrastructureExtensions.cs` (DI next to line 71). Test: Customers integration tests (Testcontainers) new `ChatbotCustomerTagsTests.cs`.

**Interfaces:**
- Produces:
  - `public enum ChatbotTagOutcome { Added, AlreadyTagged, Removed, NotTagged, CustomerMissing, TagMissing, LimitReached }`
  - `public interface IChatbotCustomerTags { Task<ChatbotTagOutcome> AddAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken ct); Task<ChatbotTagOutcome> RemoveAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken ct); }`
- Behavior: no `IExecutionContext`, no permission check (like `ICustomerDataConsents`); `AssignedByUserId = Guid.Empty`; load the customer (`EnsureNotMerged`; missing → `CustomerMissing`), the tag via `ICustomerTagRepository.ListByIdsAsync` (tenant-scoped; absent → `TagMissing`), the current assignments; add: present → `AlreadyTagged` with **no save and no audit**; at `CustomerTagAssignment.MaxPerCustomer` → `LimitReached`; else `CustomerTagAssignment.Create(...)`, `tags.AddAssignment`, one `PublishBySystem(..., CustomerAuditActions.TagsChanged, customerId, "success", now, source: "chatbot")`, `SaveChangesAsync` inside the same transaction pattern as `CustomerDataConsents` (`BeginTransactionAsync`, `LockCustomerAsync`). Remove mirrors it (`NotTagged`, no audit).

- [ ] **Step 1: RED tests** (integration, filter by name): `AddsATagToAnIncompleteCustomer` (Review Focus 3: seed the customer the way the bot's `EnsureAsync` creates it), `AddingTwiceIsAlreadyTaggedAndAuditsOnce`, `AtTheLimitReturnsLimitReached`, `TagOfAnotherTenantIsTagMissing`, `RemoveAbsentIsNotTagged`, `RemoveRemovesAndAudits` (audit row: source `chatbot`, actor type `System`).
- [ ] **Step 2: Run** `dotnet test <Customers integration project> --filter "FullyQualifiedName~ChatbotCustomerTagsTests"` → build FAIL (`IChatbotCustomerTags` undefined): RED is the build output, no containers.
- [ ] **Step 3: Implement** `ChatbotCustomerTags` (internal sealed class) and register it.
- [ ] **Step 4: Run** the filter (check `Get-Process testhost, dotnet` first) → PASS.
- [ ] **Step 5: Commit** `feat(customers): chatbot tag add and remove as the system`.

### Task 7: Pipeline — chatbot deal mover

**Files:** Create `Pipeline.Application/ChatbotDealMover.cs`; modify `IDealRepository` (PipelinePorts.cs 13-42) + `PipelineRepositories.cs` (next to `HasOpenDealFromConversationAsync`, 41-43) + DI. Test: Pipeline integration tests new `ChatbotDealMoverTests.cs`.

**Interfaces:**
- Produces:
  - `IDealRepository.FindOpenFromConversationAsync(Guid tenantId, Guid conversationId, CancellationToken ct) : Task<Deal?>` (tracked; `ClosedAt == null && OriginConversationId == conversationId`).
  - `public enum ChatbotDealMoveOutcome { Moved, AlreadyInStage, NoOpenDeal, SeveralOpenDeals, StageNotOpen, StageMissing, ModuleOff, Conflict }`
  - `public interface IChatbotDealMover { Task<ChatbotDealMoveOutcome> MoveAsync(Guid tenantId, Guid? customerId, Guid conversationId, Guid stageId, CancellationToken ct); }`
- Behavior (spec «Which deal»): strict module check (`tenantModules.FindAsync` null or Pipeline off → `ModuleOff`); pipeline via `IPipelineProvisioner.EnsureAsync`; survivor of merges via `IPipelineCustomerDirectory.ResolveSurvivorAsync` when `customerId` is not null; deal = the open deal from this conversation **only if** its customer equals the conversation's customer (or `customerId` is null → still the conversation's own deal); else `ListOpenByCustomerAsync(customerId)` with exactly one → it, zero → `NoOpenDeal`, many → `SeveralOpenDeals`; target stage missing from pipeline → `StageMissing`, kind not Open → `StageNotOpen`; `deal.MoveTo(pipeline, stageId, DealActor.ForSystem, now)` false → `AlreadyInStage`; else audit `Record(..., bySystem: true, PipelineAuditActions.DealMoved, ..., source: "chatbot")` and `IPipelineUnitOfWork.SaveChangesAsync`. On the `concurrency.conflict` exception: clear tracking, re-read, retry **once**; second failure → `Conflict`.

- [ ] **Step 1: RED tests:** `MovesTheConversationDeal`, `SameStageIsAlreadyInStageAndWritesNoAudit`, `TwoOpenDealsAndNoneFromTheConversationIsSeveralOpenDeals` and `DealFromTheConversationOfAnotherCustomerIsNotMoved` (Review Focus 5), `OneOpenDealOfTheCustomerIsMovedWhenNoneFromTheConversation`, `ClosedStageIsStageNotOpen`, `UnknownStageIsStageMissing`, `ModuleOffIsModuleOff`, `ConflictRetriesOnceThenSucceeds` and `TwoConflictsAreConflict` (fake `IPipelineUnitOfWork` or a concurrent update inside the test), audit row source `chatbot` actor `System`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ChatbotDealMoverTests"` → build FAIL. RED is the build output.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter → PASS; then the Customers and Pipeline test projects once each (end of Block B, one run at a time).
- [ ] **Step 5: Commit** `feat(pipeline): chatbot deal mover as the system`.

---

## Block C — port, adapter, RunEffects, editor, end to end

### Task 8: Port, resolver and adapter

**Files:** Modify `AutomationsPorts.cs` (port + outcome + audit action consts near 145-177 and 546/550), `src/Bootstrapper/AutomationDataConsents.cs` (extract `CustomerOfAsync` 105-140), `QepServiceCollectionExtensions.cs` (~1770); create `src/Bootstrapper/ConversationCustomerResolver.cs`, `src/Bootstrapper/AutomationCrm.cs`. Test: integration `AutomationCrmAdapterTests.cs` modeled on `AutomationDataConsentsAdapterTests` (`ArrangeAsync`, raw SQL seeds).

**Interfaces:**
- Produces:
  - `public sealed record CrmActionContext(Guid TenantId, Guid ConversationId);`
  - `public interface IAutomationCrm { Task<string> AddTagAsync(CrmActionContext ctx, Guid tagId, CancellationToken ct); Task<string> RemoveTagAsync(CrmActionContext ctx, Guid tagId, CancellationToken ct); Task<string> MoveDealAsync(CrmActionContext ctx, Guid stageId, CancellationToken ct); }` — each returns a `CrmResultCodes` string and **never throws a domain error** (a `DomainException` → `failed`; transient/cancellation exceptions propagate as `MustPropagate` already defines).
  - `internal abstract record ConversationCustomer { NoUser; Unavailable; Linked(Guid CustomerId) }` produced by `ConversationCustomerResolver.ResolveAsync(ConversationRow row, ct)` (same logic as today's `CustomerOfAsync`, but `Unavailable` ≠ `NoUser`); `AutomationDataConsents` is refactored to call it and map both non-linked cases to its current `null` — its existing tests must stay green untouched.
- Adapter behavior: tags — module check (Customers off → `module_off`), resolve customer (`NoUser` → `no_customer`, `Unavailable` → `customer_unavailable`), call `IChatbotCustomerTags`, map `ChatbotTagOutcome` → code (`Added→added`, `AlreadyTagged→already_tagged`, `Removed→removed`, `NotTagged→not_tagged`, `TagMissing→tag_missing`, `LimitReached→tag_limit`, `CustomerMissing→customer_unavailable`). moveDeal — never creates a customer: use `row.CustomerId` as is (null allowed), call `IChatbotDealMover`, map outcome (`Moved→moved`, `AlreadyInStage→already_in_stage`, `NoOpenDeal→no_open_deal`, `SeveralOpenDeals→several_open_deals`, `StageNotOpen→stage_not_open`, `StageMissing→stage_missing`, `ModuleOff→module_off`, `Conflict→deal_conflict`). Conversation not found → `failed`.

- [ ] **Step 1: RED tests** (integration): `AddTagCreatesAndLinksTheCustomerWhenMissing`; `NoUserIsNoCustomer`; `UnavailableCustomerIsCustomerUnavailable` (force `EnsureAsync` to fail); `TagMappingCoversEveryOutcome` (theory over the outcome table); `MoveDealNeverCreatesACustomer` (customer count unchanged, code `no_open_deal`); `AModuleOffIsModuleOff`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AutomationCrmAdapterTests"` → build FAIL. RED is the build output.
- [ ] **Step 3: Implement** and register `IAutomationCrm`, `ConversationCustomerResolver` next to line 1770.
- [ ] **Step 4: Run** the new filter and `--filter "FullyQualifiedName~AutomationDataConsentsAdapterTests"` (refactor guard) → PASS, one run at a time.
- [ ] **Step 5: Commit** `feat(bootstrapper): CRM port and adapter for the chatbot engine`.

### Task 9: RunEffects wiring and Automations audit

**Files:** Modify `Engine/RunEffects.cs` (ctor 33-42, re-entry case 122-137, `PendingInputAsync` 236-257, `ExternalAsync` 427-436), `AutomationsPorts.cs` (`AutomationsAuditActions`: `CrmActionApplied = "automations.run.crm_action_applied"`, `CrmActionSkipped = "automations.run.crm_action_skipped"`, `CrmActionFailed = "automations.run.crm_action_failed"`). Test: new `RunProcessorTests.CrmAction.cs` (partial of the existing class, with a fake `IAutomationCrm`).

- Behavior: `Effect.CrmAction` joins the `CheckBusinessHours or CallHttp or CheckDataConsent` case (re-entry counted); `ExternalAsync` calls the port by operation and returns `EngineInput.CrmResult(nodeId, code)`; any non-propagating exception → code `failed` (P2-R33a: nothing escapes the unit); one `Audit(run, action, now)` per execution (`added/removed/moved/already_*` → Applied only when something changed, no row for the idempotent no-ops; `skipped` codes → Skipped; `error` codes → Failed). The node id, operation and result code are in the run's step trace; the audit row carries the run id (the recorder has no detail field — a builder call, mirrored in the frontend spec's run-detail screens). `PendingInputAsync` re-requests the effect for a `Running` run.

- [ ] **Step 1: RED:** `ExecutesTheOperationAndLeavesByTheMappedOutput` (one test per output), `ARunDiedWhileRunningIsReRequestedAndTheSecondAnswerIsAlreadyTagged` (Review Focus 4: fake port returns `added` then `already_tagged`; one audit row), `ATransientPortExceptionPropagatesWithoutAdvancing` and `AnyOtherExceptionBecomesFailedOutputError`, `CountsForTheReentryLimit`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RunProcessorTests"` → build FAIL (ctor/port).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(automations): run the crmAction effect through the CRM port`.

### Task 10: Editor data and validation context

**Files:** Modify `AutomationsPorts.cs` (new `IAutomationsCrmDirectory`), `ChatbotEditorBuilder.cs` (`AiOfAsync`-style read 138-153; `Validate` 237-246; DTO build 63-79), `AutomationsDtos.cs` (70-90), `src/Modules/…` none; create `src/Bootstrapper/AutomationsCrmDirectory.cs` (model: `AutomationsAgentDirectory`). Test: `ChatbotEditorBuilderTests.cs`, integration `ChatbotsApiTests.CrmAction.cs`.

**Interfaces:**
- Produces: `IAutomationsCrmDirectory.ReadAsync(Guid tenantId, CancellationToken ct) : Task<CrmCatalog>` with `public sealed record CrmCatalog(bool CustomersEnabled, bool PipelineEnabled, IReadOnlyList<CrmTagRef> Tags, IReadOnlyList<CrmStageRef> OpenStages)` (`CrmTagRef(Guid Id, string Name)`, `CrmStageRef(Guid Id, string Name)`, stages in pipeline order, open kind only, empty when the module is off); `ChatbotEditorDto` gains `ChatbotEditorCrmDto Crm` = `{ tags:[{id,name}], stages:[{id,name}], customersEnabled, pipelineEnabled }` as the last positional field. Tags via the existing `ICustomerTagLookup`/tag repository, stages via the provisioner's pipeline; both read inside the adapter.
- Behavior: the catalog is read only when the flow has a `crmAction` node **or** the editor is opened (palette needs it: always read; it is two small queries), and feeds both the DTO and `FlowValidationContext` (`CustomersEnabled`, `PipelineEnabled`, `CrmTagIds`, `CrmOpenStageIds`).

- [ ] **Step 1: RED:** unit `EditorBuilderFeedsTheValidatorWithTheCatalog` (a flow with a tag id absent from the catalog → `crm.tag_missing` in the editor issues; present → none); `ModuleOffGivesEmptyListsAndFlagsFalse`; integration `EditorResponseCarriesTagsAndOpenStagesInOrder` (stages in pipeline order, closed ones absent) and `ImportedFlowFromAnotherTenantFailsPublishWithTagMissing` (Review Focus: tenant isolation).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ChatbotEditorBuilderTests"` → build FAIL.
- [ ] **Step 3: Implement** (register the directory next to `AutomationsAgentDirectory`, line 1768).
- [ ] **Step 4: Run** the unit filter, then the integration filter by name → PASS.
- [ ] **Step 5: Commit** `feat(automations): CRM catalog in the chatbot editor response`.

### Task 11: Architecture, composition root and end to end

**Files:** Modify `AutomationsLayerTests.cs` (add `[InlineData("Modules.Pipeline")]` to `NoLayerReferencesMessagingIntegrationsCustomersOrIdentity`), `CompositionRootTests.cs` (resolve `IAutomationCrm`, `IAutomationsCrmDirectory`, `IChatbotCustomerTags`, `IChatbotDealMover`); create `EngineEndToEndTests.CrmAction.cs` (partial of `EngineEndToEndTests`, model `EngineEndToEndTests.DataConsent.cs`).

- [ ] **Step 1: RED:** the Pipeline theory case (should already pass if layering is respected: write it first, it is the guard); end-to-end `TaggingFlowTagsTheCustomerAndLeavesByOk` (inbound → `crmAction addTag` → message by `ok`; `SELECT count(*) FROM customers.customer_tag_assignments`, the customer audit row has source `chatbot`, the Automations audit row exists), `MoveDealMovesTheDealFromTheConversation`, `DeletedTagAfterPublishLeavesByError` (Review Focus 1), `LimitLeavesByErrorWithTagLimit` (Review Focus 2), `SeveralOpenDealsLeavesBySkipped`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EngineEndToEndTests"` narrowed to the new test names, one at a time → FAIL until Tasks 1-10 are merged in the branch (they are): expected FAIL reasons are assertion failures only if something is miswired; paste the first output either way.
- [ ] **Step 3: Fix wiring** the tests expose (DI, mapping).
- [ ] **Step 4: Run** `AutomationsLayerTests`, `CompositionRootTests` filtered, the new end-to-end tests; then, once and after checking `Get-Process testhost, dotnet`, the Automations integration project with `-- xUnit.MaxParallelThreads=4`, and the Customers and Pipeline projects touched. Not the whole solution (Bootstrapper DI is touched, but the change is additive: the owner decides on a full run).
- [ ] **Step 5: Commit** `test(automations): crmAction architecture guards and end to end`.

---

## Self-review

- Spec coverage: node + outputs + `saveTo` (T1-2), validation codes (T1, T10), execution + port + adapter + system services (T6-9), customer resolution rules (T8), deal resolution rules (T7), idempotency (T6, T7, T9), audit sources and Automations audit (T5, T9), editor DTO (T10), simulator (T3), duplicate/template/import (T4, T10), architecture (T11).
- Builder calls beyond the spec, to flag in the final review: runtime codes `tag_missing` / `stage_missing`; generic simulator wording (names are frontend); Automations audit has no detail field, so node/operation/code live in the run trace; `CustomerTagAssignment.AssignedByUserId = Guid.Empty` for the system (verify no FK/constraint rejects it in Task 6's first test).
- Open owner decisions are not implemented: customer custom fields, customer advisor, «Actualizar dato».
