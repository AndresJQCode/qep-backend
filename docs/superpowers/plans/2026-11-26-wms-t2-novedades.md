# WMS-T2 · Incidents, short lines and verification counts — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (or subagent-driven-development). **The rules in the
> `qep` workspace `CLAUDE.md` override the skill:** exactly **three blocks, one implementer per block, run one after the other**
> (Block B starts only after Block A is committed, Block C after Block B). One review per block, and the strong review of the
> whole branch once at the end. Every task keeps its own commit. Steps use checkbox (`- [ ]`) syntax.

**Goal:** The unified incident (`Incident`, spec §8) with its closed catalog, history and evidence; the short declared on a task
line (`short`, line `Short`); the `Count` task type (blind verification count) generated in the same transaction as the incident
that asks for it and that, when it finds a difference, proposes an `Adjustment` with reason `CountVariance`; and the `SyncConflict`
incident recorded when a line action arrives with a lost lease, even though the action itself is rejected. Before any of that, the
operator line actions get one runner with one «task closed» hook (asked by the T1 review).

**Architecture:** `Incident` lives in the domain area `Control` (next to `Adjustment`): it depends only on `Inventory` and
`Warehousing` and knows tasks by `Guid`, so `WmsLayerTests` does not change. Tasks do not know incidents: the coordination (create
the incident, generate the `Count`, react when a `Count` closes, propose the adjustment) lives in `Application`, always in the
transaction of the fact that originates it (spec §6.4, §8.4 rule 1). The count never writes to the ledger: the movement is made by
the approved adjustment through the existing `PostAdjustmentHandler`. Every operator line action (`confirm`, `short`, `count`) runs
through `OperatorLineRunner`; every path that can close a task (that runner and `SupervisorTaskRunner`) calls the same
`TaskClosedNotifier`.

**Tech Stack:** .NET 10, minimal APIs, EF Core 10 + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3, Testcontainers.PostgreSql.

**Spec:** [`docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md`](../specs/2026-11-26-wms-tareas-y-operaciones-design.md)
§6.1–§6.12 (with the T1 review changes of §13.1), §7.9, §8, §12, §13 (DA-38 to DA-51, **pending the owner**: if the owner changes
one before a block starts, the owner wins). **Previous plans:** [`2026-11-26-wms-t1-motor-de-tareas.md`](2026-11-26-wms-t1-motor-de-tareas.md)
and [`2026-11-26-wms-t1-correcciones-revision.md`](2026-11-26-wms-t1-correcciones-revision.md) (what T1 looks like after its review).

**Out of scope:** receiving, putaway, picking, packing and dispatch incident types (they come with their cut: the enum grows like
`WarehouseTaskType`); resolutions that quarantine, return or write off (`ReturnToSupplier`, `WriteOff`, `Relocate`, `Count`,
`Adjust`); `TASK_ABANDONED` and `NeedsRecovery` (T6); cycle counting and manual `Count` by the supervisor; comments on an incident;
frontend (W2) and app (A3); `docs/estado.md` (updated on `develop` at merge time).

---

## Owner authorization required before merge

`CLAUDE.md` (2026-11-25): a contract or architecture change needs the owner's explicit authorization **before the merge**, each one
on its own (approving one does not approve the others; a green test is not an authorization). This branch carries:

| # | Change | Kind | Where |
| --- | --- | --- | --- |
| 1 | `POST /tasks/{id}/lines/{lineId}/short` (IK, `wms.document.read` + type permission) | New route | Task A4, B2 (adds `photoFileIds`) |
| 2 | `POST /tasks/{id}/lines/{lineId}/count` (IK, `wms.document.read` + `wms.count.perform`) — DA-40 | New route | Task C4 |
| 3 | `POST /incidents` (IK), `GET /incidents`, `GET /incidents/{id}`, `POST /incidents/{id}/review`, `POST /incidents/{id}/resolve` (SK), `POST /incidents/{id}/reject` | New routes | Tasks B3, B4 |
| 4 | Error codes `wms.incident.not_found` (404), `wms.incident.self_resolution`, `wms.incident.resolution_not_allowed`, `wms.incident.type_not_allowed`, `wms.incident.evidence_invalid`, `wms.incident.invalid` (422) | New codes | A3, B1, B2 |
| 5 | Existing codes in new places: `wms.task.invalid_transition` (`confirm`/`short` on a `Count`, `count` on a `Move`), `wms.serial.required` and `wms.lot.required` at `count`, `wms.lot.not_found` at `short`/`POST /incidents`, `wms.task.lease_lost` from `short`/`count` (with `resumable`) | Contract | A4, C4 |
| 6 | DTO fields: `TaskLineDto.quantityCounted`, `TaskBoardDto.openIncidents`; new enum names on the wire: type `Count`, line status `Short`, source type `Incident`, event kind `LineShort` | Contract (additive) | A2, B3, C1 |
| 7 | `CK_task_lines_status` gains `Short` (A); `CK_tasks_type` gains `Count`, `CK_task_lines_quantity` relaxed to `quantity_planned >= 0` plus `quantity_counted`, new column `task_lines.quantity_counted` (C); new tables `incidents`, `incident_evidence`, `incident_events` with their CHECKs (A). `CK_tasks_status` **does not change** (T2 adds no task status) | Schema (expand-only; see «Rollout») | A3, C1 |
| 8 | Permissions: **none new** (`wms.count.perform` and `wms.discrepancy.resolve` already exist with shape, policy and `admin`). `wms.discrepancy.resolve` is reused for incidents (spec §8.5) | — | — |
| 9 | Architecture tests: **none expected to change** (`WmsLayerTests`, `MultiReplicaInventory`, `CompositionRootTests`). If one asks for a change, stop and report it as a deviation for the owner | Architecture | every block |
| 10 | DA-38 to DA-51 and every «agent decision» at the end of this plan | Behavior | — |
| 11 | Error code `wms.adjustment.count_stale` (422) from `approve` and `post` of a `CountVariance` adjustment (owner decision 2026-11-27, H1) | New code | final review |
| 12 | Migration `AddIncidentsTenantReportedIndex` (index only, expand-only) | Schema | final review |

### Rollout (expand/contract) — decided in this plan, pending the owner

- Both migrations are **expand-only** (new tables, a nullable column, CHECKs that only admit more values). A pod with today's code
  (T1) keeps writing valid rows against the new schema.
- **The reverse is not safe and cannot be fixed retroactively:** T1 code materializes `type`, `status`, `source_type`, `line_status`
  and `kind` with `HasConversion<string>()`, so a T1 pod that loads a `Count` task, a `Short` line or a `LineShort` event throws
  (the T1 sweeper would fail its whole batch every pass; the T1 board would answer 500 while an open `Count` exists).
- **Deploy order:** deploy T2 as one release (migrations run under the startup lock before the new pod serves). With today's
  `replicas: 1`, `RollingUpdate`, `maxSurge: 1`, `maxUnavailable: 0`, the old pod overlaps the new one only until the new one is
  Ready; `Count`/`Short` rows exist only after an operator uses `short` on the new pod, and a `Count` lease lasts 30 minutes, so
  the old sweeper cannot meet one. The residual risk is a few seconds of 500 on the old pod's board.
- **No rollback to pre-T2 code once a `Count` task or a `Short` line exists:** forward-fix only. The README section says so.
- **From T2 on, the sweeper and the board tolerate values they do not know** (Task C6): `LockExpiredAsync`, the board counts and
  the board lists filter by the enum names this build knows, so the T3+ rollouts do not have this problem.

---

## Before starting

- **Worktree (work only here):**
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-t2`, branch
  `feature/wms-t2-incidents` (from `develop` `71d7787d5`, which already has T1 and its review fixes, merge `809b028ed`). Every script
  starts with `Set-Location "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-t2"`.
  Never write to the main checkout (`qep-backend`) or another repo.
- **PowerShell only:** no `&&` (`A; if ($?) { B }`), `$env:VAR = "value"` on its own line, `Get-Content` instead of `cat`.
- **Never rewrite a `.cs` with `Set-Content`/`Out-File`** (breaks accents, adds a BOM): use the edit tool.
- **Build lock:** an `Api.exe` (or a debugger) that you did not launch is not yours: do not stop it, tell the owner. Build only the
  test project, or add `--artifacts-path .\artifacts` (inside the worktree, git-ignored) to every `dotnet build`/`dotnet test`.
- **Integration runs:** Docker up, and in the same session:

  ```powershell
  $env:Seed__Enabled = "false"
  Get-Process testhost, dotnet -ErrorAction SilentlyContinue
  ```

  If a `testhost` or a `dotnet test` from another session is running, **wait** and check again; never stop it. **Known exception:**
  `testhost` PID `133940` belongs to another session and is hung: ignore it (do not wait for it, do not stop it). Never two
  integration runs at once. Tests run **in the foreground**.
- **RED of a compile failure does not start containers:** the evidence is the build output (`dotnet build <test project>`), copied
  literally into the task notes. While iterating, integration runs are filtered by test name
  (`--filter "FullyQualifiedName~<Test>"`); the touched classes run once at the end of each block.
- **Migrations** (from the worktree root, design-time factory, never `--startup-project`):

  ```powershell
  dotnet ef migrations add <Name> --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
  ```

  The last migration on the branch today is `20261127040643_AddTaskEventRecordedAt`; T2 adds `AddIncidentsAndShortLines` (Block A)
  and `AddCountTasks` (Block C), in that order.
- **Before every commit:** `git branch --show-current` must print `feature/wms-t2-incidents`; if not, stop. Stage explicit paths
  only (`git add <path> ...`), never `git add -A`. Conventional commits in English. **Every commit ends with this trailer** (blank
  line before it; this repo's rule overrides the global "no attribution" rule):

  ```
  Session: Pendientes del WMS
  Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
  ```

  **Commit with a message file**, written with the edit tool (PowerShell 5.1 breaks `git commit -F -` with a here-string):

  ```powershell
  # $env:TEMP\wms-t2-commit.txt contains: subject line, blank line, the two trailer lines
  git commit -F "$env:TEMP\wms-t2-commit.txt"
  ```

  Do not push.
- Code comments and texts for people in Spanish (Colombian, tú); identifiers in English. Open `.claude/rules/wms.md` before the
  first edit (it loads on its own when you read a WMS file) and `.claude/rules/sql-entre-modulos.md` before Task C6.

## Existing pieces this plan uses (verified at `71d7787d5`)

| Piece | Where | Use |
| --- | --- | --- |
| `WarehouseTask` (`CreateMove`, `ConfirmLine(memberId, leaseToken, deviceId, lineId, quantity, now, occurredAt, memberIsBusy)`, `CheckLease`, `CancelLine`, `Cancel`, private `CloseIfFinished(by, now, recordedAt, cancelReason)`, `MarkCancelled`, `Renew`, `ClearClaim`, `Record(kind, memberId, at, …, recordedAt)`) | `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs` | `ShortLine`, `CreateCount`, `RecordCount` follow `ConfirmLine` |
| `ConfirmLine` validation order: lease (`Lost` → `InvalidOperationException`), device, line `Open` (`TaskTransitionException(status, "confirm-line")`), quantity, `LedgerEntryRules.EnsureOccurredAtInWindow`, B2 floor `scannedAt < (ReleasedAt ?? CreatedAt) − LedgerEntryRules.ClockToleranceMinutes` → `WmsErrorCodes.MovementOccurredAtOutOfRange`, then resume (`LeaseResumed` at `scannedAt`, `recordedAt: now`) | `WarehouseTask.cs:365-434` | Extracted into a shared private `PrepareLineWrite` (Task A2) |
| `WarehouseTaskLine` (`Create` with quantity `> 0`, internal `Confirm`, `Cancel`, `Remaining`) | `Modules.Wms.Domain/Tasks/WarehouseTaskLine.cs` | `MarkShort`, `CreateCount`, `RecordCount` |
| Enums persisted by name: `WarehouseTaskType { Move }`, `WarehouseTaskStatus`, `TaskLineStatus { Open, Done, Cancelled }`, `TaskSourceType { Manual }`, `TaskEventKind` (causal order; the history sorts in memory by `RecordedAt`, then `Kind`, then `Id`) | `Modules.Wms.Domain/Tasks/` | `Count`, `Short`, `Incident`, `LineShort` |
| `TaskTypePolicy` (`DefaultPriority`, `LeaseDuration`, `AllowsDestinationOverride`, `MovementReasonOf`; unknown type → `ArgumentOutOfRangeException`) | `Modules.Wms.Domain/Tasks/TaskTypePolicy.cs` | `Count` arm, `ConfirmsByCount` |
| `TaskTypePermissions.For` / `AllowedFor` | `Modules.Wms.Application/Tasks/TaskTypePermissions.cs` | `Count` → `WmsPermissions.CountPerform` |
| `ConfirmTaskLineHandler` (authorize `DocumentRead` → validate → `scope.Key` → `caller.RequireAsync` → tx → `LockAsync(TaskMemberLocks.For(member))` → `tasks.LockAsync` (404) → `GetAsync` → `guard.EnsureCanPerformAsync` → `OperatorTaskGuard.EnsureLease` → line (404) → destination/lot → `busy` (DA-32) → `ConfirmLine` → one `ledger.PostAsync` → `composer.ComposeAsync(task, member, withEvents: false)` → `receipts.Record` → `SaveChangesAsync` → `metrics.RecordConfirmDuration`) | `Modules.Wms.Application/Tasks/ConfirmTaskLine.cs` | Becomes the first client of `OperatorLineRunner` (Task A1) |
| `SupervisorTaskRunner.RunAsync(tenantId, taskId, version, auditAction, mutate, fields, ct, precondition)` — used by `CancelTaskHandler` and `CancelTaskLineHandler`, the two supervisor actions that close a task | `Modules.Wms.Application/Tasks/SupervisorTaskActions.cs` | Calls `TaskClosedNotifier` (Task A1) |
| `OperatorTaskGuard` (`EnsureCanPerformAsync`, static `EnsureLease` → `TaskLeaseLostException(resumable: false)` on `Lost`, `EnsureActiveLease`), `TaskMemberLocks.For`, `TaskDeviceRules.MustBeADeviceId` | `Modules.Wms.Application/Tasks/OperatorTaskGuard.cs` | Same guard |
| `TaskLeaseLostException(bool resumable) : ResourceConflictException, IHasProblemExtensions` (`resumable` in the 409 extensions) | `Modules.Wms.Application/Tasks/TaskLeaseLostException.cs` | The `SyncConflict` 409 |
| `IdempotencyKeyFilter`: tenant check → key → existing receipt replays 200 → otherwise runs the handler; on an exception, replays only if a receipt appeared, else rethrows | `Modules.Wms.Api/IdempotencyKeyFilter.cs` | The `SyncConflict` 409 reaches the client untouched; `resolve` uses `.UseServerIdempotencyKey("incident", "resolve")` |
| `IWmsUnitOfWork` (`BeginTransactionAsync`, `FlushAsync`, `TryLockDocumentAsync`, blocking `LockAsync(string key)`, `SaveChangesAsync` = save **and commit** the open transaction) | `Modules.Wms.Application/WmsPorts.cs`, `Infrastructure/Persistence/WmsUnitOfWork.cs` (unique index → `RequestConcurrencyException` by index name) | Commit the `SyncConflict` before the 409 |
| `IWmsAuditRecorder`, `WmsAuditActions` (`Resources.*`, `Adjustment*`, `Task*`) | `WmsPorts.cs:41-98` | `Resources.Incident`, `IncidentReviewed/Resolved/Rejected` |
| `WmsNotFound` (`Task`, `TaskLine`, `Adjustment`, `Warehouse`, `Location`…), `WmsVersions.Ensure`, `WmsCaller` (`FindAsync`, `RequireAsync` → membership id) | `Modules.Wms.Application/WmsSupport.cs` | `WmsNotFound.Incident` |
| `Adjustment.Propose(tenantId, number, owner, warehouse, reason, note, occurredAt, lines, proposedBy, now)`, `Approve(by, now)`, `Reject(by, note, now)`, `MarkPosted(batchId, now)`; `AdjustmentLine.Create`; `AdjustmentReason.CountVariance`; `IAdjustmentRepository.NextNumberAsync` (`adj-seq:` lock) | `Modules.Wms.Domain/Control/`, `Modules.Wms.Application/Control/IAdjustmentRepository.cs` | The count with a difference proposes the adjustment |
| `ApproveAdjustmentHandler`, `PostAdjustmentHandler` (SK, `MarkPosted`), `RejectAdjustmentHandler`, `ProposeAdjustmentHandler` (`ProposedBy`/`ApprovedBy` are `executionContext.SubjectId`, **not** memberships) | `Modules.Wms.Application/Control/` | Separation of duties (compare **memberships** via `WmsCaller`) and `CountVariance` closing |
| `InvalidTransitionException` (`wms.document.invalid_transition`, `{ from, action }`) | `Modules.Wms.Domain/Control/InvalidTransitionException.cs` | `Incident` transitions |
| `IStockReadQueries` (raw SQL over balances) | `Modules.Wms.Application/Inventory/IStockReadQueries.cs`, `Infrastructure/Persistence/StockReadQueries.cs` | `FindOnHandAsync` |
| `IVariantProfileRepository.FindAsync` → `VariantLogisticsProfile.Tracking` (`TrackingPolicy.None/Lot/Serial`) | `WmsPorts.cs:151`, `Domain/Warehousing/VariantLogisticsProfile.cs` | Lot required to count; no `Count` for serials |
| `ILotRepository.GetManyAsync` + the rule in `CreateMoveTaskHandler.EnsureLotsAsync` (404 `wms.lot.not_found` unless tenant, owner and variant match) | `Modules.Wms.Application/Tasks/CreateMoveTask.cs:194-210` | Lot scanned on `short` without a ledger call, lot of `POST /incidents` |
| `IWarehouseTaskRepository` (`LockAsync`, `GetAsync`, `NextNumberAsync` with `task-seq:`, `LockNextCandidateAsync` with the DA-33 filter, `LockExpiredAsync`, `CountByStatusAsync`, `CountOpenByTypeAsync`, `ListInProgressAsync`, `ListAssignedOpenAsync`, `FindInProgressOfMemberAsync`) | `Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs`, `Infrastructure/Persistence/WarehouseTaskRepository.cs` | `FindOpenCountAsync`, DA-45, forward compatibility |
| `TaskLeaseSweeper.RunOnceAsync(now)` (one transaction per batch: `LockExpiredAsync` → `GetAsync` → `ExpireLease`; one throwing row rolls back the whole batch) | `Modules.Wms.Infrastructure/Workers/TaskLeaseSweeper.cs` | Forward compatibility (Task C6) |
| `GetTaskBoardHandler`, `TaskBoardDto(WarehouseId, ByStatus, ByType, Operators, StaleAssignments)` | `Modules.Wms.Application/Tasks/GetTaskBoard.cs`, `TaskDtos.cs:134` | `OpenIncidents` |
| `WarehouseTaskDtoComposer.ComposeAsync(task, viewerMemberId, withEvents, ct)`, `TaskLineDto(… QuantityPlanned, QuantityConfirmed, QuantityRemaining, LineStatus, BaseUom)` | `Modules.Wms.Application/Tasks/` | `QuantityCounted` |
| `TaskEndpoints` (`OperatorAction<TRequest, TResponse>(group, route, handler)` = `DocumentRead` policy; `.RequireIdempotencyKey()` on confirm) | `Modules.Wms.Api/TaskEndpoints.cs:82-100` | `short`, `count` |
| `CatalogVariantUsageChecker` (UNION over quotations, pos, ledger, open adjustment lines, lots, open task lines) | `src/Bootstrapper/CatalogVariantUsageChecker.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs`, `StockProbeTests.TheRealCheckerSeesAnOpenTaskLineAgainstTheMigratedSchema` | Adds open incidents (DA-51) |
| `WmsUserReferenceProbe` (movements, tasks, task_events, adjustments) | `Modules.Wms.Infrastructure/Persistence/WmsUserReferenceProbe.cs`, `WmsUserReferenceProbeTests` | Adds `incidents`, `incident_events` |
| `FileOwnerType.WmsEvidence` (= 12, module `wms`), `FileResource.CreatePendingUpload(id, tenantId, ownerId, ownerType, …)`, `IFileResourceRepository.ListByIdsAsync`, `IStorageUnitOfWork`; template `QuotationFileLookup.FindManyAsync` (tenant filter in memory after `ListByIdsAsync`) | `Modules.Storage.*`, `src/Bootstrapper/QuotationFileLookup.cs`, `src/Bootstrapper/WmsExportStorage.cs` | `WmsEvidenceLookup` |
| `WmsPersistenceTests.TheMigrationCreatesTheEightTablesWithTheirUniqueIndexes` (excludes the task tables by name) | `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsPersistenceTests.cs:46-62` | Also exclude the incident tables |
| Unit doubles `TaskWorld` (`Context()`, `Authorization`, `Caller()`, `UnitOfWork()`, `Repository()`, `ReceiptStore()`, `Warehouses()`, `Ledger()`, `Metrics`, `Clock()`, `Composer()`, `Log`, `Posted`, `Receipts`, `AddTask`, `Line`, `Member`, `Device`, `Tenant`, `Start`, `Now`) | `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/TaskWorld*.cs` | Runner, notifier, short domain |
| Integration support: `OpenAsync`, `ArrangeAsync` (`LedgerSetup`: `Manager`, `WarehouseId`, `OwnerId`, `BinA` = `A-01-02`, `VariantId`), `CreateClient(factory, subject, tenant, params permissions)`, `AllPermissions`, `OtherTenantId`/`OtherSubjectId`, `CodeOfAsync`, `BodyAsync`, `ScalarAsync`, `ExecuteAsync`, `CountAuditEntriesAsync`, `CreateBinAsync`, `SeedCallerMembershipAsync`; `WmsLedgerSeeding` (`StockAsync`, `OnHandAsync`, `SetTrackingAsync`, `CreateLotAsync`); `TaskApiSupport` (`TasksUrl`, `Line`, `Body`, `CreateAsync`, `ActionRawAsync`, `ActionAsync`, `GetAsync`, `ResumableOfAsync`, `ReadAsync`, `SeedOperatorAsync`, `OperatorSubject`, `SecondOperatorSubject`); `TaskTestSupport` (`NewMove`, `Line`, `CreateDestinationAsync`, `SaveAsync`, `ReloadAsync`, `History`) | `tests/Modules/Wms/Modules.Wms.IntegrationTests/` | Every integration test. Each WMS integration test opens **its own** database (`OpenAsync`): no shared host, so a test may alter the schema |
| Patterns to copy: `TaskOperatorApiTests` (`ArrangeFloorAsync`, `NextAsync`, `ConfirmRawAsync` with `Idempotency-Key`), `TaskTenantIsolationApiTests.AnotherTenantGets403OnEveryTaskRoute` (route table, every response `403 authorization.denied`), `TaskLeaseSweeperTests.SweeperOf(factory)` | `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/` | New API tests |

## Global constraints

- **The incident is created in the same transaction as the fact that originates it**, with the same idempotency key (spec §8.4
  rule 1). So is the `Count` it generates. If something does not save, nothing saves — **except `SyncConflict`**, which exists
  precisely because the fact is rejected (DA-48): its transaction holds only the incident and is committed before the 409.
- **A short is never adjusted on the floor** (DA-18): `short` moves only what was found; the difference is proposed by the count as an
  `Adjustment` and applied by whoever has `wms.adjustment.approve`.
- **Lock order** (extends T1's): operator advisory (`task-member:{id}`) → the operator's task row (`FOR UPDATE`) → count-key advisory
  (`count-key:{tenant}:{owner}:{location}:{variant}:{lot|none}:{status}`) → the open `Count` row (`FOR UPDATE`, only when linking to
  it) → numberings (`incident-seq:`, `task-seq:`, `adj-seq:`) → balances (inside the ledger). **The `count` action never takes the
  count-key lock** (it already holds the `Count` row; taking the key after the row would invert the order of `short`). No path takes
  a numbering and then a task.
- **A `Count` is blind:** no DTO the operator receives carries the expected quantity. A `Count` line has `quantityPlanned = 0`; the
  expected quantity is computed on the server at count time and never stored (DA-39).
- Supervisor actions on an incident: `version` (412) and audit `wms.incident.*`. Operator and system facts: `incident_events` only,
  no audit (DA-50, same criterion as DA-27).
- 403 always for another tenant; `wms.incident.not_found` (404) only inside the tenant.
- Enums by name in the API and as text in the database; quantities `numeric(18,6)` in base unit.
- `occurredAt` of every line action (`confirm`, `short`, `count`) obeys the same window and the same floor (release − 5 min).

## File map

| File | Block | What |
| --- | --- | --- |
| `src/Modules/Wms/Modules.Wms.Application/Tasks/OperatorLineRunner.cs`, `TaskClosedHooks.cs` | A | Runner and the single «task closed» hook |
| `src/Modules/Wms/Modules.Wms.Application/Tasks/ConfirmTaskLine.cs`, `SupervisorTaskActions.cs` | A | Use the runner / the notifier |
| `src/Modules/Wms/Modules.Wms.Domain/Tasks/TaskLineStatus.cs`, `TaskEventKind.cs`, `WarehouseTask.cs`, `WarehouseTaskLine.cs` | A, C | `Short`, `LineShort`, `ShortLine`; then `Count` |
| `src/Modules/Wms/Modules.Wms.Domain/Control/Incident*.cs`, `WmsErrorCodes.cs` | A, B, C | The incident and its catalog |
| `src/Modules/Wms/Modules.Wms.Application/Control/IIncidentRepository.cs`, `Incidents/*.cs` | A, B, C | Port, recorder, handlers, DTOs |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/IncidentModel.cs`, `IncidentRepository.cs`, `WmsDbContext.cs`, `WmsUnitOfWork.cs`, `WarehouseTaskModel.cs`, `WarehouseTaskRepository.cs`, `StockReadQueries.cs`, `WmsUserReferenceProbe.cs`, `WmsInfrastructureExtensions.cs` | A, C | Persistence |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddIncidentsAndShortLines*.cs`, `<ts>_AddCountTasks*.cs` | A, C | Generated migrations |
| `src/Modules/Wms/Modules.Wms.Application/Tasks/ShortTaskLine.cs`, `CountTaskLine.cs`, `CountVerificationHook.cs`, `TaskDtos.cs`, `WarehouseTaskDtoComposer.cs`, `GetTaskBoard.cs` | A, B, C | Use cases |
| `src/Modules/Wms/Modules.Wms.Application/Control/ApproveAdjustment.cs`, `PostAdjustment.cs`, `RejectAdjustment.cs` | C | `CountVariance` closing and separation of duties |
| `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`, `IncidentEndpoints.cs`, `WmsEndpoints.cs` | A, B, C | Routes |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `WmsEvidenceLookup.cs`, `CatalogVariantUsageChecker.cs` | A, B, C | Registrations, adapter, usage |
| `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/*`, `Control/Incident*Tests.cs` | A, B, C | Unit |
| `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskShortApiTests.cs`, `TaskSyncConflictApiTests.cs`, `TaskCountApiTests.cs`, `TaskForwardCompatibilityTests.cs`; `Incidents/IncidentPersistenceTests.cs`, `IncidentApiTests.cs`, `IncidentTenantIsolationApiTests.cs`, `CountVarianceAdjustmentTests.cs`, `IncidentTestSupport.cs` | A, B, C | Integration |
| `README.md` (§ Módulo Wms), `.claude/rules/wms.md`, `.claude/rules/sql-entre-modulos.md` | C | Contract and rules |

---

# Block A — operator line runner, short and `SyncConflict` (tasks A1 to A5, one implementer)

Order: A1 → A2 → A3 → A4 → A5. A1 is a **pure refactor**: every existing test stays green with only the construction of the
handlers changing in the test helpers.

### Task A1: `OperatorLineRunner` and the single «task closed» hook (pure refactor)

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/OperatorLineRunner.cs`, `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskClosedHooks.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/ConfirmTaskLine.cs`, `src/Modules/Wms/Modules.Wms.Application/Tasks/SupervisorTaskActions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (scoped `OperatorLineRunner`, `TaskClosedNotifier`; no hook yet)
- Modify (construction only): `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/OperatorTaskHandlersTests.cs` (`ConfirmAsync` helper), `SupervisorTaskActionsTests.cs` (runner construction), `TaskWorld*.cs` if a helper is needed
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/TaskClosedNotifierTests.cs`

Shape:

```csharp
// TaskClosedHooks.cs
/// <summary>Una tarea pasó a Done o Cancelled en esta transacción. ActorMemberId es la membresía que la cerró; OperationKey, la
/// clave de la acción del operario (null en las del supervisor).</summary>
public sealed record TaskClosed(WarehouseTask Task, Guid ActorMemberId, string? OperationKey, DateTimeOffset OccurredAt, DateTimeOffset Now);

/// <summary>Lo que otro proceso hace cuando una tarea se cierra (T2: el Count de verificación). Corre dentro de la transacción del
/// cierre, antes de guardar: si falla, no se guarda nada.</summary>
public interface ITaskClosedHook
{
    Task OnClosedAsync(TaskClosed closed, CancellationToken cancellationToken);
}

/// <summary>El único punto por el que pasa el cierre de una tarea. Lo llaman los dos caminos que pueden cerrarla: OperatorLineRunner
/// (confirmar, faltante, conteo) y SupervisorTaskRunner (cancelar la tarea o su última línea abierta).</summary>
public sealed class TaskClosedNotifier(IEnumerable<ITaskClosedHook> hooks)
{
    public async Task NotifyIfClosedAsync(WarehouseTaskStatus before, TaskClosed closed, CancellationToken cancellationToken);
    // no-op unless before is open and closed.Task.Status is Done or Cancelled; then each hook, in registration order
}

// OperatorLineRunner.cs
public sealed record OperatorLineRequest(
    Guid TenantId, Guid TaskId, Guid LineId, Guid LeaseToken, string DeviceId, DateTimeOffset OccurredAt, string Action, object RawCommand);

public sealed record OperatorLineContext(
    WarehouseTask Task, WarehouseTaskLine Line, Guid MemberId, LeaseCheck Lease, bool MemberIsBusy, string OperationKey, DateTimeOffset Now);

/// <summary>El molde de toda acción del operario sobre una línea (confirmar, faltante, conteo): clave IK → membresía → transacción →
/// lock del operario → lock de la tarea (404) → carga → permiso del tipo → lease → línea (404) → DA-32 → la acción → cierre →
/// componer → recibo → guardar. Una sola llamada al ledger por transacción, dentro de la acción.</summary>
public sealed class OperatorLineRunner(
    IExecutionContext executionContext, WmsAuthorization authorization, WmsCaller caller, IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope, IOperationReceipts receipts, IWarehouseTaskRepository tasks, OperatorTaskGuard guard,
    IWmsConfirmMetrics metrics, IClock clock, WarehouseTaskDtoComposer composer, TaskClosedNotifier closed)
{
    public Task AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken);   // DocumentRead, before the validator
    public Task<TaskDto> RunAsync(
        OperatorLineRequest request, Func<OperatorLineContext, CancellationToken, Task> apply, CancellationToken cancellationToken);
}
```

`RunAsync`, in this exact order (it is today's `ConfirmTaskLineHandler.HandleAsync` minus the confirm-specific part):
`scope.Key ?? throw new InvalidOperationException(…)` → `caller.RequireAsync` → `Stopwatch` → `await using` transaction →
`LockAsync(TaskMemberLocks.For(member))` → `tasks.LockAsync` (404 `WmsNotFound.Task`) → `GetAsync` (404) →
`guard.EnsureCanPerformAsync` → `OperatorTaskGuard.EnsureLease` (Task A5 replaces this line) → `task.FindLine` (404
`WmsNotFound.TaskLine`) → `busy = lease == Resumable && FindInProgressOfMemberAsync(...) is not null` → `var before = task.Status`
→ `await apply(context, ct)` → `closed.NotifyIfClosedAsync(before, new TaskClosed(task, member, key, request.OccurredAt, now), ct)`
→ `composer.ComposeAsync(task, member, withEvents: false, ct)` → `receipts.Record(dto)` → `SaveChangesAsync` →
`metrics.RecordConfirmDuration(...)`. `ConfirmTaskLineHandler` keeps its validator, `ResolveDestinationAsync`, the lot check, the
`ConfirmLine` call and the single `LedgerEntry`/`PostAsync` **inside** `apply`; its constructor becomes `(OperatorLineRunner runner,
IWarehouseRepository warehouses, IInventoryLedger ledger, IClock clock, IValidator<ConfirmTaskLineCommand> validator)`.

`SupervisorTaskRunner.RunAsync` takes `TaskClosedNotifier` in its constructor, captures `task.Status` before `mutate` and calls
`NotifyIfClosedAsync(before, new TaskClosed(task, actor, null, now, now), ct)` after it and before auditing. `Cancel`, `CancelLine` and
`ConfirmLine` keep closing the task in the domain; the application reacts in one place.

- [ ] **RED.** `TaskClosedNotifierTests` (with a `SpyHook : ITaskClosedHook` that appends to `world.Log` and stores the `TaskClosed`):
  - `ConfirmingTheLastQuantityNotifiesTheHookOnceBeforeTheReceipt`: confirm 5 of 5 through the handler → `world.Log` is
    `["begin", "lock task-member:…", "lock-task", "load-task", "ledger", "hook", "receipt", "save"]`; the `TaskClosed` has the
    member, the key `"key-1"` and `OccurredAt = command.OccurredAt`.
  - `APartialConfirmationDoesNotNotify` (confirm 2 of 5 → no `"hook"`).
  - `CancellingTheTaskNotifiesOnce` and `CancellingTheLastOpenLineNotifiesOnce` (through `CancelTaskHandler`/`CancelTaskLineHandler`;
    `OperationKey` null, actor = the supervisor's membership).
  - `CancellingALineThatLeavesOthersOpenDoesNotNotify`.
  - `AFailingHookSavesNothing` (hook throws → exception propagates, no `"receipt"`, no `"save"`).
- [ ] Build: `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests` fails (`ITaskClosedHook`, `TaskClosedNotifier`,
  `OperatorLineRunner` do not exist). Copy the output.
- [ ] **GREEN.** Implement as above. Update the two test helpers so they build the handlers through the runner. No assertion of an
  existing test changes; if one has to, stop: it is not a pure refactor.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~WmsLayerTests"
  ```

  Then, once (integration, the refactored paths):

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskOperatorApiTests|FullyQualifiedName~TaskSupervisorActionsApiTests" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `refactor(wms): run operator line actions through one runner with a task-closed hook`

### Task A2: Short on a line (`ShortLine`) and a close that counts `Short`

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/TaskLineStatus.cs` (+ `Short` between `Done` and `Cancelled`),
  `TaskEventKind.cs` (+ `LineShort` right after `LineConfirmed`: the history sorts by the enum inside a timestamp)
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs`, `WarehouseTaskLine.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskShortTests.cs`

API:

```csharp
// WarehouseTaskLine
internal void MarkShort();   // Status = Short, keeps QuantityConfirmed; no more confirmations

// WarehouseTask
/// <summary>De lo que falta de la línea sólo había <paramref name="quantityFound"/> (puede ser cero). Lo encontrado se confirma
/// como en ConfirmLine; la línea queda Short y la tarea se cierra si era la última abierta. Mismo lease, misma reanudación, misma
/// regla de DA-32 y misma ventana y piso de occurredAt que ConfirmLine.</summary>
public WarehouseTaskLine ShortLine(
    Guid memberId, Guid leaseToken, string deviceId, Guid lineId, decimal quantityFound,
    DateTimeOffset now, DateTimeOffset? occurredAt = null, bool memberIsBusy = false);
```

- [ ] **RED.** `WarehouseTaskShortTests` (fixtures like `WarehouseTaskLeaseTests`: a `Move` with one line of 5, claimed):
  - `ShortingWithTwoFoundConfirmsTwoAndClosesTheLineAsShort`: line `Short`, `QuantityConfirmed = 2`, events `LineConfirmed` (2) and
    `LineShort` (quantity 3, what was missing), task `Done` with `CompletedAt` and `Completed`.
  - `ShortingWithNothingFoundClosesTheTaskAsDone`: `quantityFound: 0` → no `LineConfirmed`, `LineShort` with 5, task `Done` (§6.3:
    `Done` if any line ended `Done` or `Short`).
  - `AShortLineWithAnotherCancelledStillClosesDone` (two lines: cancel one, short the other → `Done`).
  - `ShortingAfterAPartialConfirmationOnlyCountsTheRest` (confirm 1, short with 1 found → confirmed 2, `LineShort` 3).
  - `FindingEverythingIsNotAShort` (`quantityFound == Remaining` → `WmsDomainException` `TaskLineInvalid`).
  - `FindingMoreThanRemainingIs422` (`TaskQuantityExceedsRemaining`); `ANegativeFoundIs422LineInvalid`.
  - `ShortingAClosedLineIsAnInvalidTransition` (`TaskTransitionException`, action `short-line`) and
    `ConfirmingAShortLineIsAnInvalidTransition` (action `confirm-line`).
  - `AResumableLeaseResumesBeforeShorting` (`LeaseResumed` before `LineShort`); `ABusyMemberShortsWithoutResuming` (DA-32);
    `ALostLeaseThrowsInvalidOperation`.
  - `ShortEventsCarryTheScanTime` (`occurredAt` in `LineConfirmed`, `LineShort`, `Completed`; `RecordedAt = now`).
  - `AScanOutsideTheLedgerWindowIs422` and `AScanMoreThanFiveMinutesBeforeTheReleaseIs422` (both
    `MovementOccurredAtOutOfRange`); `AScanFourMinutesBeforeTheReleaseIsAccepted`.
- [ ] Build fails (`ShortLine`, `TaskLineStatus.Short` do not exist). Copy the output.
- [ ] **GREEN.** Extract from `ConfirmLine` a private `PrepareLineWrite(memberId, leaseToken, deviceId, lineId, action, now,
  occurredAt) → (WarehouseTaskLine line, LeaseCheck lease, string device, DateTimeOffset scannedAt)` (lease `Lost` →
  `InvalidOperationException`, `RequiredDevice`, `LineOf`, line `Open` else `TaskTransitionException(line.Status, action)`, window,
  floor) and a private `ApplyLease(lease, memberIsBusy, memberId, device, scannedAt, now)` (resume + `Renew` or `LastActivityAt`).
  `ConfirmLine` keeps its behavior (T1 tests stay green). `ShortLine`: `PrepareLineWrite(…, "short-line", …)` → `0 ≤ quantityFound <
  line.Remaining` → `ApplyLease` → if `> 0` `line.Confirm` and `LineConfirmed` → `var missing = line.Remaining` → `line.MarkShort()`
  → `Version++` → `LineShort` with `missing` → `CloseIfFinished(memberId, scannedAt, now)`. `CloseIfFinished`: the `Done` condition
  becomes `line.Status is TaskLineStatus.Done or TaskLineStatus.Short`, and its summary says so.
- [ ] Run: `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"` (T1 tests
  included).
- [ ] Commit: `feat(wms): let an operator declare a short line`

### Task A3: The incident core: vocabulary, `Report`, catalog and persistence

`Short` and `SyncConflict` persist an incident, so the aggregate, its table and its numbering land here; Block B adds the
lifecycle (review, resolve, reject), the evidence lookup and the routes. The vocabularies are complete from this task so the
incident CHECKs are written once.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Control/IncidentType.cs`, `IncidentStatus.cs`, `IncidentResolution.cs`,
  `IncidentEventKind.cs`, `IncidentEvidenceKind.cs`, `IncidentCatalog.cs`, `IncidentSubject.cs`, `IncidentEvidence.cs`,
  `IncidentEvent.cs`, `Incident.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs` (block «Novedades (spec 2026-11-26 §8)»: `IncidentTypeNotAllowed`,
  `IncidentInvalid`, `IncidentEvidenceInvalid`)
- Create: `src/Modules/Wms/Modules.Wms.Application/Control/IIncidentRepository.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/IncidentModel.cs`, `IncidentRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs` (`CK_task_lines_status`), `WmsDbContext.cs`,
  `WmsUnitOfWork.cs`, `WmsUserReferenceProbe.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/WmsInfrastructureExtensions.cs`
- Create (generated): `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddIncidentsAndShortLines.cs` (+ Designer, snapshot)
- Modify: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsPersistenceTests.cs` (exclude `incidents`, `incident_evidence`, `incident_events`)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Control/IncidentCatalogTests.cs`, `IncidentReportTests.cs`;
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/Incidents/IncidentPersistenceTests.cs`, `Tasks/WarehouseTaskPersistenceTests.cs`
  (new case), `WmsUserReferenceProbeTests.cs` (new case)

Vocabulary (DA-42; every enum «se persiste por nombre; no se renombra»):

```csharp
public enum IncidentType { ShortMove, LocationMismatch, UnreadableLabel, CountVariance, SyncConflict }
public enum IncidentStatus { Open, InReview, Resolved, Rejected }
public enum IncidentResolution { Accept, AcceptIntoStock }
public enum IncidentEventKind { Reported, InReview, Resolved, Rejected, VerificationLinked, VerificationCancelled }
public enum IncidentEvidenceKind { Photo, Scan }

public static class IncidentCatalog
{
    public static IReadOnlySet<IncidentResolution> ResolutionsOf(IncidentType type); // ShortMove, LocationMismatch, UnreadableLabel → {Accept}; SyncConflict → {AcceptIntoStock}; CountVariance → {} (closed by its adjustment)
    public static bool CanBeRejected(IncidentType type);                              // SyncConflict only (it put nothing in the ledger)
    public static bool RequestsVerification(IncidentType type);                        // ShortMove, LocationMismatch
    public static IReadOnlySet<IncidentType> DeclarableOnShort(string taskType);      // "Move" → {ShortMove, LocationMismatch}; anything else → {}
    public static IReadOnlySet<IncidentType> DeclarableStandalone { get; }             // {UnreadableLabel, LocationMismatch} (DA-49)
}
```

`DeclarableOnShort` takes the task type **by name** (`string`): `Control` cannot see `Tasks` (`DomainAreasOnlyDependDownwards`). The
handler passes `task.Type.ToString()`.

Aggregate (`{ get; private set; }`, private ctor, `Version++` per transition, like `Adjustment`):

```csharp
public sealed record IncidentSubject(Guid? SourceTaskId, Guid? SourceTaskLineId, LocationId? Location, Guid? VariantId,
    LotId? LotId, StockStatus? StockStatus, decimal? ExpectedQty, decimal? ActualQty);

public sealed class Incident
{
    public const int NoteMaxLength = 500;
    public const int RawCodeMaxLength = 200;
    public const int MaxEvidence = 5;
    // Id, TenantId, WarehouseId, OwnerId, Number, Type, Status, SourceTaskId?, SourceTaskLineId?, VerificationTaskId?, LocationId?,
    // VariantId?, LotId?, StockStatus?, ExpectedQty?, ActualQty?, RawCode?, RawPayload? (jsonb text), Note?, ReportedBy (membership;
    // Guid.Empty = system), DeviceId?, ReportedAt, OperationKey?, Resolution?, ResolvedBy?, ResolvedAt?, ResolutionNote?, BatchId?,
    // AdjustmentId?, ParentIncidentId?, Version; Evidence, Events (append-only)

    public static Incident Report(Guid tenantId, string number, WarehouseId warehouse, OwnerId owner, IncidentType type,
        IncidentSubject subject, string? note, string? rawCode, string? rawPayload, IReadOnlyList<IncidentEvidence> evidence,
        Guid reportedBy, string? deviceId, string? operationKey, DateTimeOffset reportedAt, DateTimeOffset now);
}
```

Port (this task; B and C add methods):

```csharp
public interface IIncidentRepository
{
    void Add(Incident incident);
    /// <summary>Con evidencia y eventos, sin rastreo: la ficha.</summary>
    Task<Incident?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    /// <summary>Si esa Idempotency-Key ya dejó una novedad (deduplicar SyncConflict). Se llama bajo el lock del operario.</summary>
    Task<bool> ExistsWithOperationKeyAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken);
    /// <summary>NOV-{yyyyMMdd}-{NNNN} por tenant y día, con lock bloqueante «incident-seq:{tenant}»; exige la transacción.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
```

- [ ] **RED (unit).** `IncidentCatalogTests`: one test per catalog row, plus `EveryTypeHasAnEntry` (walks
  `Enum.GetValues<IncidentType>()` and calls the five members: a new type without a row turns red).
  `IncidentReportTests`: `ReportingIsOpenWithVersionOneAndAReportedEvent` (number, subject, `ReportedAt` = the scan time, one
  `Reported` event with `memberId = reportedBy` or null for `Guid.Empty`); `TheNoteAndTheRawCodeAreTrimmedAndBounded`
  (`IncidentInvalid` above 500 / 200); `NegativeQuantitiesAreInvalid`; `MoreThanFiveEvidenceFilesIsEvidenceInvalid`
  (`WmsErrorCodes.IncidentEvidenceInvalid` is defined in this task so the test is final; B2 adds the tenant lookup).
- [ ] **RED (integration).** `IncidentPersistenceTests` (`OpenAsync` + `ArrangeAsync`, save through the port + `IWmsUnitOfWork`):
  - `AnIncidentRoundTripsWithEvidenceAndEvents` (every column, `raw_payload` as JSON).
  - `TheOperationKeyIsUniquePerTenant` (same key twice in one tenant → the second save throws `RequestConcurrencyException`
    translated from `IX_incidents_operation_key`; in another tenant it saves).
  - `NumbersAreSequentialPerTenantAndDay` (`NOV-yyyyMMdd-0001`, `-0002`).
  - `TheDatabaseRejectsAnUnknownTypeStatusOrResolution` (`UPDATE … SET type = 'Nope'` → `23514` `CK_incidents_type`; same for
    status and resolution).
  - `DeletingALocationUsedByAnIncidentIs422HasStock` (`FK_incidents_location` in `LocationReferenceForeignKeys`).
  `WarehouseTaskPersistenceTests.AShortLineRoundTrips` (a `Move` whose line ends `Short`, plus its `LineShort` event).
  `WmsUserReferenceProbeTests.AMemberWhoOnlyReportedAnIncidentRetainsTheUser`.
- [ ] Build fails (types missing). Copy the output.
- [ ] **GREEN.**
  - `Report`: trims, validates lengths (`IncidentInvalid`), non-negative quantities, `evidence.Count <= MaxEvidence`
    (`IncidentEvidenceInvalid`), `Id = Guid.CreateVersion7()`, `Status = Open`, `Version = 1`, `Reported` event at `reportedAt`
    (`IncidentEvent.Record(tenantId, incidentId, kind, Guid? memberId, string? detail, DateTimeOffset at, DateTimeOffset recordedAt)`,
    `recordedAt = now`, like task events). `IncidentEvidence.Create(Guid fileId, IncidentEvidenceKind kind, string? rawValue)`.
  - `WarehouseTaskModel`: `CK_task_lines_status` → `line_status IN ('Open','Done','Short','Cancelled')`. Nothing else in tasks.
  - `IncidentModel.Configure(ModelBuilder)` (internal static, called from `WmsDbContext.OnModelCreating` after
    `WarehouseTaskModel.Configure`): tables `incidents`, `incident_evidence`, `incident_events`, snake_case columns; enums
    `HasConversion<string>()` `HasMaxLength(32)`; `raw_payload` `jsonb`; `version` `bigint` concurrency token; quantities
    `HasPrecision(18, 6)`; FKs `FK_incidents_warehouse`, `FK_incidents_owner`, `FK_incidents_location`, `FK_incidents_lot`,
    `FK_incidents_parent` (RESTRICT), `FK_incident_evidence_incident`, `FK_incident_events_incident` (cascade). No FK to `tasks`,
    `adjustments` or Storage files (linked by `Guid`, like `tasks.source_id`). Indexes: `IX_incidents_tenant_number` unique;
    `IX_incidents_operation_key` unique `(tenant_id, operation_key) WHERE operation_key IS NOT NULL`; `IX_incidents_open`
    `(tenant_id, warehouse_id, status) WHERE status IN ('Open','InReview')`; `IX_incidents_verification` `(verification_task_id) WHERE
    verification_task_id IS NOT NULL`; `IX_incidents_adjustment` `(adjustment_id) WHERE adjustment_id IS NOT NULL`;
    `IX_incidents_variant_open` `(tenant_id, variant_id) WHERE status IN ('Open','InReview') AND variant_id IS NOT NULL` (DA-51
    query); `IX_incident_events_incident` `(incident_id, recorded_at)`. CHECKs: `CK_incidents_type`, `CK_incidents_status`,
    `CK_incidents_resolution` (`resolution IS NULL OR resolution IN ('Accept','AcceptIntoStock')`), `CK_incidents_resolved`
    (`status <> 'Resolved' OR (resolution IS NOT NULL AND resolved_at IS NOT NULL)`), `CK_incidents_quantities` (non-negative),
    `CK_incident_evidence_kind`. **No CHECK on `incident_events.kind`** (like `task_events.kind`): the event vocabulary grows without
    a migration.
  - `WmsDbContext`: `IncidentNumberIndex`, `IncidentOperationKeyIndex`; `FK_incidents_location` into `LocationReferenceForeignKeys`,
    `FK_incidents_owner` into `OwnerReferenceForeignKeys`. `WmsUnitOfWork.PersistAsync`: both indexes →
    `RequestConcurrencyException("concurrency.conflict", …)` (last resort: the numbering lock and the operator lock serialize first).
  - `IncidentRepository` (template `AdjustmentRepository.NextNumberAsync`, prefix `NOV-`, lock `"incident-seq:" + tenantId`);
    register `IIncidentRepository` in `AddWmsInfrastructure`.
  - `WmsUserReferenceProbe`: add `incidents.reported_by`, `incidents.resolved_by`, `incident_events.member_id`.
  - Generate `AddIncidentsAndShortLines`. Review the `Up`: the `CK_task_lines_status` drop/add and the three tables only. The `Down`
    EF generates goes back to T1's CHECK: leave a comment that **going down with `Short` lines fails with `23514`, on purpose**.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Control.Incident"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~IncidentPersistenceTests|FullyQualifiedName~AShortLineRoundTrips|FullyQualifiedName~AMemberWhoOnlyReportedAnIncidentRetainsTheUser|FullyQualifiedName~TheMigrationCreatesTheEightTablesWithTheirUniqueIndexes" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `feat(wms): persist the incident core and short task lines`

### Task A4: `short` on a line, with its `ShortMove`/`LocationMismatch` incident in the same transaction

`short` creates the incident but **not yet** the verification `Count`: that type is born in Block C (Task C3 adds it to this same
path with its own RED).

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Incidents/TaskIncidentRecorder.cs`, `src/Modules/Wms/Modules.Wms.Application/Tasks/ShortTaskLine.cs`
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskLotRules.cs` (extract `EnsureLotsAsync` from `CreateMoveTask.cs` into a
  shared `EnsureLotAsync(ILotRepository, tenantId, ownerId, variantId, lotId, ct)`; `CreateMoveTaskHandler` uses it)
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskDtos.cs` (`ShortTaskLineRequest`), `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`,
  `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskShortApiTests.cs`; `Incidents/IncidentTestSupport.cs` (helpers below)

```csharp
/// <summary>Lo que el operario escaneó al no encontrar todo. QuantityFound va al destino planeado; para otro destino se confirma
/// antes con motivo y se declara el faltante con 0 (DA-41). IncidentType es el nombre del tipo: ShortMove o LocationMismatch.</summary>
public sealed record ShortTaskLineRequest(Guid LeaseToken, decimal QuantityFound, Guid? LotId, string? IncidentType, string? Note,
    DateTimeOffset? OccurredAt, string? DeviceId) : IIdempotentRequest;

/// <summary>Arma la novedad que nace de una línea de tarea. No guarda: comparte la transacción del handler.</summary>
public sealed class TaskIncidentRecorder(IIncidentRepository incidents)
{
    public Task<Incident> ReportFromLineAsync(WarehouseTask task, WarehouseTaskLine line, IncidentType type, decimal expected,
        decimal actual, LotId? lot, string? note, Guid member, string deviceId, string operationKey, DateTimeOffset reportedAt,
        DateTimeOffset now, CancellationToken cancellationToken);
}
```

`ShortTaskLineHandler(OperatorLineRunner runner, TaskIncidentRecorder recorder, IInventoryLedger ledger, ILotRepository lots,
IValidator<ShortTaskLineCommand> validator)`: `runner.AuthorizeAsync` → validator (`deviceId`, `0 <= quantityFound`, `incidentType`
parses to `IncidentType`, note ≤ 500, `occurredAt` not null; the domain still decides `< remaining`) → `runner.RunAsync(request with
Action = "short", RawCommand = command, apply)`, where `apply`:

1. `IncidentCatalog.DeclarableOnShort(task.Type.ToString())` lacks the type → 422 `wms.incident.type_not_allowed`.
2. Lot: `line.LotId` fixed and `command.LotId` different → `wms.task.lot_mismatch`; effective `line.LotId ?? command.LotId`; a lot
   that came from the command goes through `TaskLotRules.EnsureLotAsync` (404 `wms.lot.not_found`): with `quantityFound = 0` there is
   no ledger call to catch a foreign lot, and the incident row has an FK to `lots` that does not look at the tenant.
3. `var expected = line.Remaining` → `task.ShortLine(member, …, quantityFound, now, occurredAt, ctx.MemberIsBusy)`.
4. `recorder.ReportFromLineAsync(task, line, type, expected, quantityFound, lot, note, member, deviceId, key, occurredAt, now)` —
   subject `(task.Id, line.Id, line.FromLocationId, line.VariantId, lot, line.StockStatus, expected, quantityFound)`; numbering
   **before** the ledger (lock order).
5. If `quantityFound > 0`: **one** `LedgerEntry` `(owner, from, variant, lot, status) → (owner, line.ToLocationId, …)`,
   `TaskTypePolicy.MovementReasonOf(task.Type)`, document `("Task", task.Id, line.Id)`, actor the membership with its device →
   `ledger.PostAsync`. If the ledger fails (`wms.stock.insufficient`, `wms.lot.required`…), the whole transaction rolls back: no
   `Short` line, no incident.

Route: `OperatorAction<ShortTaskLineRequest, TaskDto>(group, "/tasks/{id:guid}/lines/{lineId:guid}/short", ShortAsync).RequireIdempotencyKey()`.

`IncidentTestSupport` (integration): `IncidentsOfTaskAsync(database, taskId)` → rows `(id, type, status, expected_qty, actual_qty,
reported_by, operation_key, verification_task_id, lot_id)` via `ScalarAsync`/Npgsql; `ShortRawAsync(client, taskId, lineId, body,
key)`; `ShortBody(token, found, type = "ShortMove", lotId = null, occurredAt = null)`.

- [ ] **RED.** `TaskShortApiTests` (floor of `TaskOperatorApiTests`: stock 10 in `A-01-02`, a `Move` of 5 to `B-01-01`, claimed
  with `next` by an operator with `DocumentRead` + `PutawayPerform`):
  - `ShortWithTwoFoundMovesTwoAndClosesTheTask`: 200; `OnHandAsync(B-01-01) = 2`, `A-01-02 = 8`; line `Short`, task `Done`; one
    `ShortMove` incident `Open` with `expected_qty = 5`, `actual_qty = 2`, `source_task_id`, `reported_by` = the operator,
    `operation_key` = the key, `reported_at` = the scan time.
  - `ShortWithNothingFoundMovesNothing` (`quantityFound: 0`: no movement with the task as document; incident `actual_qty = 0`).
  - `ShortWithALotMovesThatLot`: variant set to `Lot` (`SetTrackingAsync`) with lot L1 stocked; line without lot; `lotId = L1`,
    found 2 → movement and incident carry L1. `AShortWithAnotherLotThanTheLineIs422LotMismatch`.
  - `AShortWithNothingFoundOnALotVariantNeedsNoLot` (found 0, no lot → 200, incident `lot_id` null).
  - `AForeignLotIs404` (a lot of another variant, found 0 → 404 `wms.lot.not_found`, nothing saved).
  - `RepeatingTheShortReturnsTheSavedResponse` (same key → 200 identical body, one incident, one movement).
  - `ATypeNotDeclarableOnAMoveIs422` (`UnreadableLabel` → `wms.incident.type_not_allowed`); `AnUnknownTypeIs422ValidationFailed`.
  - `FindingEverythingIs422LineInvalid`.
  - `AScanMoreThanFiveMinutesBeforeTheReleaseIs422` (`wms.movement.occurred_at_out_of_range`, nothing saved).
  - `ShortMoreThanTheStockRollsBackEverything` (stock 1, found 2 → 422 `wms.stock.insufficient`; no incident; line still `Open`).
  - `ShortWithoutThePutawayPermissionIs403`; `ShortWithoutTheIdempotencyKeyIs428`.
  - `ALateShortOfTheSameOperatorResumesTheTask` (lease expired + swept, same token → 200, `LeaseResumed` then `LineShort`).
- [ ] Build fails (`ShortTaskLineRequest`, handler, route missing). Copy the output.
- [ ] **GREEN.** Implement and register `ShortTaskLineHandler`, its validator and `TaskIncidentRecorder` (scoped).
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskShortApiTests" -- xUnit.MaxParallelThreads=4
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
  ```

- [ ] Commit: `feat(wms): declare a short line with its incident`

### Task A5: `SyncConflict` when a line action arrives with a lost lease

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/OperatorLineRunner.cs` (the lease step), `src/Modules/Wms/Modules.Wms.Application/Incidents/TaskIncidentRecorder.cs`
  (`RecordSyncConflictAsync`)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskSyncConflictApiTests.cs`; `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/TaskClosedNotifierTests.cs` (one case)

```csharp
// TaskIncidentRecorder
/// <summary>La acción que llegó con un lease perdido, tal cual llegó (DA-48), sin el leaseToken. Si esa Idempotency-Key ya dejó su
/// novedad no agrega otra. No guarda: el runner confirma la transacción con sólo esto y después responde 409.</summary>
public Task RecordSyncConflictAsync(WarehouseTask task, WarehouseTaskLine? line, Guid member, string deviceId, string operationKey,
    string action, object rawCommand, decimal? quantity, LotId? lot, DateTimeOffset occurredAt, DateTimeOffset now,
    CancellationToken cancellationToken);
```

In `OperatorLineRunner.RunAsync` the lease step becomes:

```csharp
// Spec §6.7 y DA-48: la acción rechazada queda como novedad aunque el hecho no se aplique. La transacción sólo tiene la novedad
// (nada más se tocó), así que guardarla y lanzar después no deja nada a medias. El filtro IK no encuentra recibo y devuelve este
// 409 tal cual; un reintento con la misma clave no duplica la novedad (operation_key, bajo el lock del operario).
var lease = task.CheckLease(member, request.LeaseToken);
if (lease == LeaseCheck.Lost)
{
    await recorder.RecordSyncConflictAsync(task, task.FindLine(request.LineId), member, request.DeviceId, key, request.Action,
        request.RawCommand, request.Quantity, request.LotId, request.OccurredAt, now, cancellationToken);
    await unitOfWork.SaveChangesAsync(cancellationToken);
    throw new TaskLeaseLostException(resumable: false);
}
```

(`OperatorLineRequest` gains `decimal? Quantity` and `Guid? LotId`; the runner takes `TaskIncidentRecorder`.) Rules:
- The permission guard (`EnsureCanPerformAsync`) stays **before**: without the type permission it is 403 and nothing is recorded. An
  unknown task is 404 and nothing is recorded.
- `ExistsWithOperationKeyAsync(tenant, key)` true → add nothing (dedup by `Idempotency-Key`).
- Subject from the line when it belongs to the task (`FromLocationId`, variant, `line.LotId ?? lot`, status, `actualQty =
  quantity`); a line id that is not the task's → no line subject (the task exists; the physical fact is what matters).
- `raw_payload` = the command serialized with `JsonSerializerOptions.Web` into a `JsonObject`, `"action"` added, `"leaseToken"`
  removed (another claim's token must not be readable in the inbox).
- `reported_at` = `occurredAt` when it is inside the ledger window `[now − 30 d, now + 5 min]`, else the server `now` (the raw value
  stays in `raw_payload`).
- `resumable` is always `false` here: a `Resumable` lease never reaches this branch (the action resumes the task).

- [ ] **RED.** `TaskSyncConflictApiTests`:
  - `AConfirmationAfterReassignmentLeavesASyncConflict`: operator A claims; the supervisor reassigns to B (`assign` with reason);
    A confirms → 409 `wms.task.lease_lost` with `resumable = false` (`ResumableOfAsync`); one `SyncConflict` `Open`, `reported_by` =
    A, `source_task_line_id`, `actual_qty`, `operation_key` = the key, `raw_payload` with `quantity` and `action = "confirm"` and
    **without** `leaseToken`; the task version, its lines and the stock did not change; **no** `operation_receipts` row for the key.
  - `RetryingTheSameKeyDoesNotDuplicateTheConflictAndStill409s` (twice → 409 with `resumable = false` both times, one incident).
  - `ADifferentKeyLeavesASecondConflict`.
  - `TheSameKeyWithAnotherBodyKeepsTheFirstPayload` (no receipt, so no `wms.idempotency_key_reused`: 409 lease lost, one incident with
    the first body).
  - `AShortWithALostLeaseAlsoLeavesAConflict` (`action = "short"`, `actual_qty = quantityFound`, no `ShortMove` incident).
  - `AResumableLateConfirmationLeavesNoConflict` (lease expired + swept, same token → 200 and no `SyncConflict`).
  - `AConfirmationOnAHeldTaskLeavesAConflict` (DA-35: held then published → 409 `resumable = false` + `SyncConflict`).
  - `WithoutTheTypePermissionItIs403AndLeavesNothing`; `AnUnknownTaskIs404AndLeavesNothing`.
  - `AnAncientScanIsReportedAtServerTime` (`occurredAt` 40 days ago → 409, `reported_at` ≈ now, raw value in `raw_payload`).
  Unit (`TaskClosedNotifierTests`): `ALostLeaseCommitsOnlyTheConflictAndNeverNotifies` (`world.Log` ends `…"load-task", "save"`,
  no `"ledger"`, no `"hook"`, no `"receipt"`; the exception is `TaskLeaseLostException` with `Resumable == false`).
- [ ] Build: if the new signatures do not exist yet the build fails (copy it); once it compiles, the RED is the filtered run of
  `AConfirmationAfterReassignmentLeavesASyncConflict` failing on the missing incident (a behavior failure: containers are fine).
- [ ] **GREEN.** Implement.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskSyncConflictApiTests" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `feat(wms): keep a sync conflict when a line action lost its lease`

**Block A close (once, foreground, after checking `Get-Process testhost`):**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MultiReplicaInventoryTests"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks|FullyQualifiedName~Modules.Wms.IntegrationTests.Incidents|FullyQualifiedName~WmsPersistenceTests|FullyQualifiedName~WmsUserReferenceProbeTests|FullyQualifiedName~WmsUnitOfWorkTests" -- xUnit.MaxParallelThreads=4
```

`WmsLayerTests` must stay green **without changes** (if it asks for one, stop: `Incident` must not reference `Tasks`). Block review
(one read-only reviewer): the runner preserves T1's lock and log order, the hook runs once per close, §6.3 close with `Short`, DA-48
transaction and dedup, numbering before balances.

---

# Block B — incident lifecycle, evidence, routes and audit (tasks B1 to B4, one implementer)

### Task B1: Review, resolve, reject and separation of duties in the domain

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Control/Incident.cs`, `WmsErrorCodes.cs` (`IncidentNotFound`, `IncidentSelfResolution`,
  `IncidentResolutionNotAllowed`)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Control/IncidentLifecycleTests.cs`

```csharp
public void Review(Guid by, DateTimeOffset now);                                       // Open → InReview
public void Resolve(IncidentResolution resolution, Guid by, string? note, Guid? batchId, DateTimeOffset now); // Open|InReview → Resolved
public void Reject(Guid by, string note, DateTimeOffset now);                           // Open|InReview → Rejected; only if CanBeRejected
public void EnsureNotSelfResolution(IncidentResolution resolution, Guid by);           // DA-17/DA-47: AcceptIntoStock by the reporter → 422
```

- [ ] **RED.** `IncidentLifecycleTests`: `ReviewMovesOpenToInReviewAndBumpsTheVersion` (event `InReview`);
  `ReviewingTwiceIsAnInvalidTransition` (`InvalidTransitionException`, `from = "InReview"`, action `review`);
  `ResolvingWithAResolutionTheTypeDoesNotAllowIs422` (`ShortMove` + `AcceptIntoStock`; `CountVariance` + `Accept` →
  `IncidentResolutionNotAllowed`); `ResolvingIsTerminal` (a second `Resolve` or `Reject` → invalid transition);
  `OnlyAnIncidentThatMovedNothingCanBeRejected` (`SyncConflict` yes; `ShortMove` → `IncidentResolutionNotAllowed`);
  `RejectingNeedsANoteOfAtMostFiveHundred` (`IncidentInvalid`); `TheReporterCannotAcceptIntoStockTheirOwnConflict`
  (`IncidentSelfResolution`) while `Accept` by the reporter passes; `ResolvingRecordsWhoWhenAndTheBatch`.
- [ ] Build fails. Copy the output.
- [ ] **GREEN.** Each transition: status check → mutate → `Version++` → event (`recordedAt = now`). `Resolve` refuses `CountVariance`
  always (it is closed by its adjustment, Task C5).
- [ ] Run: `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Control.Incident"`.
- [ ] Commit: `feat(wms): review, resolve and reject incidents in the domain`

### Task B2: Evidence (`WmsEvidence` files of the tenant, at most 5)

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Incidents/IWmsEvidenceLookup.cs`; `src/Bootstrapper/WmsEvidenceLookup.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Incidents/TaskIncidentRecorder.cs` (resolve evidence), `src/Modules/Wms/Modules.Wms.Application/Tasks/ShortTaskLine.cs`,
  `TaskDtos.cs` (`ShortTaskLineRequest` gains `IReadOnlyList<Guid>? PhotoFileIds`), `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskShortApiTests.cs` (new cases); `Incidents/IncidentTestSupport.cs`
  (`SeedEvidenceFileAsync(factory, tenantId, ownerType = FileOwnerType.WmsEvidence)` with `FileResource.CreatePendingUpload` +
  `IFileResourceRepository` + `IStorageUnitOfWork`, left pending)

```csharp
public interface IWmsEvidenceLookup
{
    /// <summary>Los ids que son archivos WmsEvidence del tenant, en cualquier estado (pueden estar subiendo todavía, DA-49). Los de
    /// otro tenant o de otro tipo de dueño no vienen, como uno que no existe.</summary>
    Task<IReadOnlySet<Guid>> FindEvidenceAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
```

`WmsEvidenceLookup(IFileResourceRepository repository)`: `ListByIdsAsync` and filter `resource.TenantId == tenantId &&
resource.OwnerType == FileOwnerType.WmsEvidence` (copy of `QuotationFileLookup.FindManyAsync`, without looking at the status).
Registered next to `WmsMemberDirectory`. The recorder: ids are de-duplicated first; more than `Incident.MaxEvidence` distinct ids →
422 `wms.incident.evidence_invalid`; any id missing from the lookup → the same code; each becomes `IncidentEvidence.Create(id, Photo,
null)`. The validator of `short` caps the list length at 5 only through the domain (no FluentValidation rule: it is not free text).

- [ ] **RED.** `TaskShortApiTests`: `AShortKeepsItsPhotosEvenWhileUploading` (two pending `WmsEvidence` files → incident with two
  evidence rows); `APhotoOfAnotherTenantIs422EvidenceInvalid` (file seeded for `OtherTenantId`; nothing saved);
  `AFileThatIsNotEvidenceIs422EvidenceInvalid` (owner type `WmsExport`); `AnUnknownPhotoIs422EvidenceInvalid`;
  `SixPhotosAre422EvidenceInvalid`; `TheSamePhotoTwiceCountsOnce`.
- [ ] Build fails (`PhotoFileIds` missing). Copy the output.
- [ ] **GREEN.** Implement and register.
- [ ] Run: `dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskShortApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] Commit: `feat(wms): attach tenant evidence files to incidents`

### Task B3: Report and read incidents (`POST /incidents`, `GET /incidents`, `GET /incidents/{id}`) and the board count

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Incidents/IncidentDtos.cs` (DTOs, `IncidentDtoComposer`), `ReportIncident.cs`,
  `ListIncidents.cs`, `GetIncident.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Control/IIncidentRepository.cs` (`GetAsync` tracked with evidence, `ListAsync`,
  `CountOpenAsync`), `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/IncidentRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsSupport.cs` (`WmsNotFound.Incident`), `WmsPorts.cs` (`Resources.Incident = "wms.incident"`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/GetTaskBoard.cs`, `TaskDtos.cs` (`TaskBoardDto` gains `int OpenIncidents`, last)
- Create: `src/Modules/Wms/Modules.Wms.Api/IncidentEndpoints.cs`; Modify: `src/Modules/Wms/Modules.Wms.Api/WmsEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Incidents/IncidentApiTests.cs`, `Tasks/TaskSupervisorActionsApiTests.cs`
  (`TheBoardCountsOpenIncidents`)

Contract (spec §8.5, names by enum, DA-42):

```
POST /incidents                 IK  [wms.document.read + the permission of some task type, in the handler, DA-49]
     { warehouseId, type, locationId?, variantId?, lotId?, quantity?, rawCode?, note, photoFileIds?, sourceTaskId?, occurredAt, deviceId } → 201 IncidentDto
GET  /incidents?warehouseId&status&type&page&pageSize   [wms.document.read] → { items: IncidentSummaryDto[], page, pageSize, totalCount }
GET  /incidents/{id}            [wms.document.read] → IncidentDto
```

```
IncidentSummaryDto { id, number, type, status, warehouseId, ownerId, locationId, locationCode, variantId, variantCode, productName,
                     expectedQty, actualQty, reportedBy, reportedByName, reportedAt, sourceTaskId, sourceTaskNumber,
                     verificationTaskId, adjustmentId, version }
IncidentDto        { ...IncidentSummaryDto, lotId, lotCode, stockStatus, rawCode, rawPayload, note, deviceId, resolution, resolvedBy,
                     resolvedByName, resolvedAt, resolutionNote, batchId, parentIncidentId, allowedResolutions, canBeRejected,
                     evidence: [{ fileId, kind, rawValue }], events: [{ kind, memberId, memberName, detail, occurredAt }] }
TaskBoardDto       { ...T1, openIncidents }
```

BFF notes for the DTO comments: `allowedResolutions` and `canBeRejected` come from `IncidentCatalog` **and the incident** (the screen
does not know the catalog; without this it would paint buttons that answer 422). `AcceptIntoStock` is offered only when
`actualQty > 0` and the source line has a destination (a `SyncConflict` from `count` or from a `short` with 0 found has nothing to
apply: only `reject`). Member names, location codes, variant code/product name and task number are resolved on the server
(`IWmsMemberDirectory.FindNamesAsync`, `IWarehouseRepository.FindLocationCodesAsync`, `IWmsCatalogReader`, the task repository), like
`WarehouseTaskDtoComposer`. `reportedByName`/`resolvedByName` of `Guid.Empty` is «Sistema». `rawCode`, `rawPayload`, evidence and
events travel only in `IncidentDto`. Events in `recorded_at` order, then kind, then id.

`ReportIncidentHandler`: `DocumentRead` (with warehouse) → `TaskTypePermissions.AllowedFor(ctx).Count == 0` → 403 → validator (type
parses and is in `IncidentCatalog.DeclarableStandalone`, else 422 `wms.incident.type_not_allowed`; note required 1..500; rawCode ≤
200; `quantity >= 0`; `occurredAt` not null; `deviceId`) → owner Self (`provisioner.EnsureAsync` **before** the transaction, like
`ProposeAdjustmentHandler`) → transaction → warehouse (404) and location of that warehouse (404 `wms.location.not_found`) → variant
via `IWmsCatalogReader.GetVariantsAsync` (unknown → 422 `wms.profile.variant_unknown`) → lot via `TaskLotRules.EnsureLotAsync` →
`sourceTaskId` must be a task of the tenant and warehouse (404 `wms.task.not_found`) → evidence (B2 rule) →
`incidents.NextNumberAsync` → `Incident.Report(reportedBy: member, operationKey: key, reportedAt: occurredAt)` → receipt → save.
Its occurredAt follows the ledger window (`LedgerEntryRules.EnsureOccurredAtInWindow` → `wms.movement.occurred_at_out_of_range`). A
`LocationMismatch` with a variant gets its `Count` in Task C3; until then it stays without verification.

`GetTaskBoardHandler`: `OpenIncidents = incidents.CountOpenAsync(tenant, warehouse)` (`Open` + `InReview`).

Routes in `IncidentEndpoints` (template `AdjustmentEndpoints`/`TaskEndpoints`): `POST /incidents` → 201 with `Location`,
`.RequireIdempotencyKey()`.

- [ ] **RED.** `IncidentApiTests`:
  - `ReportingAnUnreadableLabelReturns201Open` (operator with `PutawayPerform`; evidence of the tenant; `Location` header);
    `RepeatingTheReportReturnsTheSavedResponseWith200`.
  - `ReportingWithoutAnyTaskPermissionIs403` (only `DocumentRead`); `ReportingAShortMoveStandaloneIs422TypeNotAllowed`;
    `ReportingWithoutANoteIs422ValidationFailed`; `ALocationOfAnotherWarehouseIs404`; `AnUnknownVariantIs422`.
  - `ReportingWithAPhotoOfAnotherTenantIs422EvidenceInvalid`; `ReportingWithSixPhotosIs422EvidenceInvalid`.
  - `ListingFiltersByWarehouseStatusAndTypeNewestFirst`; `GettingIncludesEvidenceEventsAllowedResolutionsAndNames` (a
    `SyncConflict` from Task A5 shows `allowedResolutions = ["AcceptIntoStock"]`, `canBeRejected = true`; one from a `short` with 0
    found shows `[]`).
  - `AnUnknownIncidentIs404`.
  `TaskSupervisorActionsApiTests.TheBoardCountsOpenIncidents` (one `Open`, one `InReview`, one `Rejected` → 2).
- [ ] Build fails. Copy the output.
- [ ] **GREEN.** Implement and register the three handlers, the validator and the composer.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~IncidentApiTests|FullyQualifiedName~TheBoardCountsOpenIncidents" -- xUnit.MaxParallelThreads=4
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
  ```

- [ ] Commit: `feat(wms): report and read incidents`

### Task B4: `review`, `resolve` (with `AcceptIntoStock` of a `SyncConflict`), `reject`, audit and tenant isolation

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Incidents/ReviewIncident.cs`, `ResolveIncident.cs`, `RejectIncident.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` (`IncidentReviewed = "wms.incident.reviewed"`, `IncidentResolved`,
  `IncidentRejected`), `src/Modules/Wms/Modules.Wms.Api/IncidentEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Incidents/IncidentApiTests.cs` (new cases), `Incidents/IncidentTenantIsolationApiTests.cs`

```
POST /incidents/{id}/review       [wms.discrepancy.resolve]  { version } → IncidentDto
POST /incidents/{id}/resolve  SK  [wms.discrepancy.resolve]  { version, resolution, note? } → { batchId?, createdTaskIds }
POST /incidents/{id}/reject       [wms.discrepancy.resolve]  { version, note } → IncidentDto
```

- `ReviewIncidentHandler`, `RejectIncidentHandler`: template `ApproveAdjustmentHandler` — authorize → load tracked (404) → authorize
  with owner and warehouse → `WmsVersions.Ensure` (412) → domain with the membership (`WmsCaller.RequireAsync`) → audit
  (`executionContext.SubjectId`, `Resources.Incident`, fields `ownerId`, `number`, `type`, and `note` for reject) → compose → save.
- `ResolveIncidentHandler` (SK `incident:{id}:resolve:{version}`, `ResolveIncidentRequest(long Version, string? Resolution, string?
  Note) : IVersionedRequest`): like the above, plus `incident.EnsureNotSelfResolution(resolution, member)`; for `SyncConflict` +
  `AcceptIntoStock`: `await using` transaction, `tasks.GetAsync(sourceTaskId)` to read the line destination (no task lock: the task is
  not touched, DA-48); missing line, no destination or `actualQty` not `> 0` → 422 `wms.incident.resolution_not_allowed`; one
  `LedgerEntry` `(owner, location, variant, lot, status) → (owner, line.ToLocationId, …)` by `actualQty`, reason `InternalMove`,
  document `("Incident", incident.Id, sourceTaskLineId)`, actor the supervisor's membership, key = the SK → `ledger.PostAsync` →
  `incident.Resolve(…, posting.BatchId, now)` → receipt → save. `Accept` resolves without movement. Response
  `IncidentResolutionDto(Guid? BatchId, IReadOnlyList<Guid> CreatedTaskIds)` (always empty list in T2).

- [ ] **RED.** `IncidentApiTests`: `ReviewThenAcceptRoundTrips` (version climbs; audit rows `wms.incident.reviewed` and
  `wms.incident.resolved` via `CountAuditEntriesAsync`; `resolved_by` the supervisor's membership); `AStaleVersionIs412`;
  `ResolvingWithANotAllowedResolutionIs422`; `RejectingAShortMoveIs422ResolutionNotAllowed`; `RejectingASyncConflictWithANote`
  (audit `wms.incident.rejected`); `AcceptingASyncConflictIntoStockMovesTheStock` (supervisor ≠ reporter; one movement with
  `document_type = 'Incident'`, `batch_id` set, task untouched); `TheReporterCannotAcceptIntoStockTheirOwnConflict` (422
  `wms.incident.self_resolution`; the reporter is given `DiscrepancyResolve` for the test); `AConflictFromACountCannotBeAcceptedIntoStock`
  is written in C4 — here `AConflictWithNothingFoundCannotBeAcceptedIntoStock` (from a `short` with 0 → 422);
  `RepeatingTheResolveReturnsTheSavedResponse` (SK: one movement); `ResolvingWithoutDiscrepancyResolveIs403`;
  `OperatorActionsLeaveNoAudit` (the `short` and the `SyncConflict` of Block A: zero `wms.incident.*` rows).
  `IncidentTenantIsolationApiTests.AnotherTenantGets403OnEveryIncidentRoute` (copy of the task test: `POST /incidents`, `GET` list,
  `GET {id}`, `review`, `resolve`, `reject`, plus `POST /tasks/{id}/lines/{lineId}/short`; each with an `Idempotency-Key`; every
  response `403 authorization.denied`, never 404).
- [ ] Build fails. Copy the output.
- [ ] **GREEN.** Implement and register.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~IncidentApiTests|FullyQualifiedName~IncidentTenantIsolationApiTests" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `feat(wms): review, resolve and reject incidents behind wms.discrepancy.resolve`

**Block B close (once, foreground, after checking `Get-Process testhost`):**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Control"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Incidents|FullyQualifiedName~TaskShortApiTests|FullyQualifiedName~TaskSyncConflictApiTests|FullyQualifiedName~TaskSupervisorActionsApiTests|FullyQualifiedName~AdjustmentApiTests" -- xUnit.MaxParallelThreads=4
```

Block review: §8.3 transitions, §8.4 rules 3–6 against the tests, BFF fields, the SK of `resolve`, the 403/404 split, DA-49 evidence.

---

# Block C — verification count, `CountVariance` → adjustment, catalog usage and final verification (tasks C1 to C7, one implementer)

### Task C1: The `Count` type: vocabulary, policy, domain and migration `AddCountTasks`

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTaskType.cs` (+ `Count`), `TaskSourceType.cs` (+ `Incident`),
  `TaskTypePolicy.cs`, `WarehouseTask.cs`, `WarehouseTaskLine.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskTypePermissions.cs`, `TaskDtos.cs` (`TaskLineDto` gains `decimal?
  QuantityCounted`, last), `WarehouseTaskDtoComposer.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs`
- Create (generated): `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddCountTasks.cs` (+ Designer, snapshot)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/TaskTypePolicyTests.cs`, `WarehouseTaskCountTests.cs`, `TaskRoutesTests.cs` or a new
  `TaskTypePermissionsTests.cs`; `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/WarehouseTaskPersistenceTests.cs` (new cases)

API:

```csharp
// TaskTypePolicy: Count → DefaultPriority 3, LeaseDuration 30 min, AllowsDestinationOverride false,
// MovementReasonOf → ArgumentOutOfRangeException("A count never moves stock."), and:
public static bool ConfirmsByCount(WarehouseTaskType type);   // Move false, Count true, unknown → ArgumentOutOfRangeException

// WarehouseTaskLine
public decimal? QuantityCounted { get; private set; }
/// <summary>La línea de un conteo: una clave sin destino y con lo planeado en cero. Lo esperado no vive en la línea: el servidor lo
/// calcula al contar, y así nada que llegue al operario lo revela (DA-39).</summary>
public static WarehouseTaskLine CreateCount(Guid variantId, LotId? lotId, StockStatus stockStatus, LocationId location);
internal void RecordCount(decimal counted, LotId? lot);        // QuantityCounted = counted, LotId ??= lot, Status = Done

// WarehouseTask
public static WarehouseTask CreateCount(Guid tenantId, string number, WarehouseId warehouse, OwnerId owner,
    IReadOnlyList<WarehouseTaskLine> lines, TaskSourceType sourceType, Guid sourceId, DateTimeOffset now); // Ready, CreatedBy = Guid.Empty
public WarehouseTaskLine RecordCount(Guid memberId, Guid leaseToken, string deviceId, Guid lineId, decimal quantityCounted,
    LotId? lotId, DateTimeOffset now, DateTimeOffset? occurredAt = null, bool memberIsBusy = false);
```

- [ ] **RED.** `TaskTypePolicyTests`: `ACountLastsThirtyMinutesHasPriorityThreeAndNoDestinationOverride`;
  `ACountHasNoMovementReason`; `EveryTypeIsCoveredByEveryPolicy` (theory over `Enum.GetValues<WarehouseTaskType>()`: calls every
  member, `MovementReasonOf` only when `!ConfirmsByCount`). Permissions: `For(Count) == CountPerform`; `AllowedFor` with only
  `CountPerform` → `[Count]`. `WarehouseTaskCountTests`: `ACountIsBornReadyFromTheSystemWithItsSource` (`Type = Count`, `Ready`,
  `ReleasedAt = now`, priority 3, `SourceType = Incident`, `CreatedBy = Guid.Empty`, version 1, `Created` and `Published` with null
  member); `ACountLineHasNoDestinationAndNothingPlanned`; `ACountNeedsBetweenOneAndOneHundredLines`; `ACountLineIsNeverAMoveLine`
  (`CreateMove` with a count line → `TaskLocationInvalid`); `ClaimingACountGivesAThirtyMinuteLease`;
  `RecordingACountClosesTheLineAndTheTask` (`QuantityCounted = 7`, `QuantityConfirmed = 0`, `LineConfirmed` with quantity 7 and
  detail `count`, task `Done`); `CountingZeroIsValid`; `ANegativeCountIs422LineInvalid`; `TheScannedLotFillsALineWithoutLot`;
  `AScannedLotOnALineWithAnotherLotIs422LotMismatch`; `ConfirmingOrShortingACountLineIsAnInvalidTransition` (actions
  `confirm-line`, `short-line`); `CountingAMoveLineIsAnInvalidTransition` (action `count-line`);
  `ACountScanBeforeTheReleaseFloorIs422` (`MovementOccurredAtOutOfRange`); `ALostLeaseThrows`; `AResumableLeaseResumes`.
  `WarehouseTaskPersistenceTests`: `ACountTaskWithACountedLineRoundTrips` (`quantity_planned = 0`, `quantity_counted`, null
  `to_location_id`, source `Incident`); `TheDatabaseRejectsANegativeCount` (`23514`, `CK_task_lines_quantity`);
  `TheDatabaseAcceptsACountOfZero`.
- [ ] Build fails. Copy the output.
- [ ] **GREEN.** `CreateCount` shares `CreateMove`'s line-count and priority checks through a private `NewTask(type, …)`; requires
  `lines.All(line => line.ToLocationId is null && line.QuantityPlanned == 0)` (`TaskLineInvalid`) and calls `Release`.
  `ConfirmLine`/`ShortLine` throw `TaskTransitionException(Status, "confirm-line"/"short-line")` when `ConfirmsByCount(Type)`;
  `RecordCount` requires it (`"count-line"`) and uses `PrepareLineWrite`/`ApplyLease` from A2. `WarehouseTaskLine.Create` (move lines)
  keeps requiring `> 0`. The composer fills `QuantityCounted`; a `Count` line shows `QuantityPlanned = 0`, `QuantityRemaining = 0`.
  `WarehouseTaskModel`: `CK_tasks_type` → `type IN ('Move','Count')`; `CK_task_lines_quantity` → `quantity_planned >= 0 AND
  quantity_confirmed >= 0 AND quantity_confirmed <= quantity_planned AND (quantity_counted IS NULL OR quantity_counted >= 0)`; column
  `quantity_counted numeric(18,6) NULL`. `CK_tasks_status` and `IX_tasks_pool` do not change; a comment on `CountOpenByTypeAsync` and
  on the status list of `CatalogVariantUsageChecker` says T2 adds no task status. Generate `AddCountTasks`; review the `Up` (two
  CHECKs and one column); comment in the `Down` that going down with `Count` rows fails with `23514`, on purpose.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WarehouseTaskPersistenceTests" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `feat(wms): model the blind verification count task`

### Task C2: Queries the count needs: exact balance, open count of a key, reporter kept off their count

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Inventory/IStockReadQueries.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/StockReadQueries.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Control/IIncidentRepository.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/IncidentRepository.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockQueryTests.cs` (or the class that already covers `StockReadQueries`),
  `Tasks/TaskCandidateQueryTests.cs`, `Incidents/IncidentPersistenceTests.cs`

```csharp
// IStockReadQueries
/// <summary>El saldo físico de exactamente esa clave, sin unidad logística: lote nulo es «sin lote», no «cualquiera»
/// (lot_id IS NOT DISTINCT FROM). Cero si no hay fila.</summary>
Task<decimal> FindOnHandAsync(Guid tenantId, StockKey key, CancellationToken cancellationToken);

// IWarehouseTaskRepository
/// <summary>El id del Count abierto (ni Done ni Cancelled) que verifica esa clave; null si no hay (DA-44). Sin bloqueo: quien lo
/// va a enlazar lo bloquea después con LockAsync y vuelve a mirar su estado.</summary>
Task<Guid?> FindOpenCountAsync(Guid tenantId, Guid warehouseId, Guid ownerId, Guid locationId, Guid variantId, Guid? lotId,
    StockStatus stockStatus, CancellationToken cancellationToken);

// IIncidentRepository
Task<IReadOnlyList<Incident>> ListOpenVerifiedByAsync(Guid tenantId, Guid countTaskId, CancellationToken cancellationToken); // tracked
Task<Incident?> FindByAdjustmentAsync(Guid tenantId, Guid adjustmentId, CancellationToken cancellationToken);              // tracked
Task<IReadOnlyList<Guid>> ListReportersOfChildrenAsync(Guid tenantId, Guid parentIncidentId, CancellationToken cancellationToken);
```

- [ ] **RED.** `FindOnHandAsyncReadsTheExactKey` (10 without lot and 4 with lot L1 in one bin → 10, 4, and 0 for another status);
  `TaskCandidateQueryTests.TheReporterIsNotOfferedTheCountThatVerifiesTheirIncident` (DA-45: a `Count` in the pool, an incident with
  `verification_task_id` = it and `reported_by` = the member → `LockNextCandidateAsync` gives `null` to them and the task to
  another member); `TheReporterGetsTheCountWhenItIsAssignedToThem`; `FindOpenCountAsyncIgnoresClosedCountsAndOtherKeys` (lot null ≠
  lot L1); `ListOpenVerifiedByAsyncSkipsClosedIncidents`; `FindByAdjustmentAsync…`; `ListReportersOfChildrenAsync…`.
- [ ] Build fails. Copy the output.
- [ ] **GREEN.** `FindOnHandAsync`: `SELECT COALESCE(SUM(on_hand), 0) FROM wms.stock_balances WHERE tenant_id = … AND owner_id = … AND
  location_id = … AND variant_id = … AND lot_id IS NOT DISTINCT FROM … AND status = … AND handling_unit_id IS NULL`.
  `FindOpenCountAsync`: `tasks t JOIN task_lines l` on the key, `t.type = 'Count' AND t.status NOT IN ('Done','Cancelled')`.
  `LockNextCandidateAsync`: next to the DA-33 filter, `AND (t.assigned_member_id = {memberId} OR NOT EXISTS (SELECT 1 FROM
  wms.incidents i WHERE i.verification_task_id = t.id AND i.reported_by = {memberId}))`, with a comment: DA-45, and the subquery
  locks nothing (the `FOR UPDATE` is on `t`). `wms.md` already warns that this SQL reads other tables by name.
- [ ] Run: `dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~FindOnHandAsyncReadsTheExactKey|FullyQualifiedName~TaskCandidateQueryTests|FullyQualifiedName~IncidentPersistenceTests" -- xUnit.MaxParallelThreads=4`
- [ ] Commit: `feat(wms): read exact balances and keep reporters off their own verification count`

### Task C3: Generate (or link) the verification `Count` from `short` and `POST /incidents`

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Incidents/TaskIncidentRecorder.cs` (public `EnsureVerificationAsync`),
  `ShortTaskLine.cs`, `ReportIncident.cs`; `src/Modules/Wms/Modules.Wms.Domain/Control/Incident.cs` (`LinkVerification(Guid
  countTaskId, DateTimeOffset now)`, event `VerificationLinked`; unit case in `IncidentLifecycleTests`)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskShortApiTests.cs`, `Incidents/IncidentApiTests.cs`, `Tasks/TaskCountApiTests.cs` (first cases)

`EnsureVerificationAsync(Incident incident, Guid warehouseId, Guid ownerId, DateTimeOffset now, ct)` — called right after
`Incident.Report`, before `incidents.Add` and before any ledger call:
1. `!IncidentCatalog.RequestsVerification(type)` or no location/variant → return.
2. Profile `Tracking == Serial` (`IVariantProfileRepository.FindAsync`) → return (DA-44: no `Count`, the supervisor accepts).
3. `unitOfWork.LockAsync("count-key:{tenant}:{owner}:{location}:{variant}:{lot|none}:{status}")`.
4. `tasks.FindOpenCountAsync(…)` → if found: `tasks.LockAsync(countId)` and `GetAsync`; if it is still open, `incident.LinkVerification(countId, now)`
   (event `VerificationLinked`) and return; if it closed meanwhile, fall through.
5. `tasks.NextNumberAsync` → `WarehouseTask.CreateCount(…, [WarehouseTaskLine.CreateCount(variant, lot, status, location)],
   TaskSourceType.Incident, incident.Id, now)` → `tasks.Add` → `incident.LinkVerification(count.Id, now)`.

- [ ] **RED.** `TaskShortApiTests`: `AShortCreatesAReadyCountOfTheSourceKey` (one `Count` `Ready`, `sourceType = "Incident"`,
  `sourceId` = the incident, one line `A-01-02`/`V1`/`Available`, `quantityPlanned = 0`, no destination; the incident has it in
  `verification_task_id`); `ASecondShortOnTheSameKeyLinksTheOpenCount` (another `Move` on the same key → two incidents, one `Count`);
  `AShortOnAnotherLotCreatesAnotherCount`; `AShortOnASerialVariantCreatesNoCount` (profile `Serial`; incident without verification);
  `TheCountAndItsIncidentRollBackWithTheShort` (stock insufficient → no `Count`); `RepeatingTheShortCreatesNoSecondCount`.
  `IncidentApiTests`: `ReportingALocationMismatchWithAVariantCreatesACount`; `ALocationMismatchWithoutAVariantHasNoCount`.
  `TaskCountApiTests` (new class; counter with `DocumentRead` + `CountPerform`): `TheReporterIsNotOfferedTheirCountButAnotherCounterIs`
  (DA-45: the reporter also gets `CountPerform`; their `next` → 204; the counter's `next` → the `Count`);
  `TheSupervisorCanAssignTheCountToTheReporter` (`assign` → 200; then the reporter's `next` → the `Count`);
  `TheCountDtoRevealsNothingExpected` (`quantityPlanned = 0`, `quantityRemaining = 0`, `quantityCounted = null`; the JSON has no
  other quantity field).
- [ ] Build: the recorder method is new → compile RED; copy it. Then the filtered runs fail on the missing `Count`.
- [ ] **GREEN.** Implement; `ShortTaskLineHandler` and `ReportIncidentHandler` call `EnsureVerificationAsync`.
- [ ] Run: `dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskShortApiTests|FullyQualifiedName~IncidentApiTests|FullyQualifiedName~TaskCountApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] Commit: `feat(wms): generate or link the verification count of an incident`

### Task C4: `count` on a line and the `Count` closed hook

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/CountTaskLine.cs`, `src/Modules/Wms/Modules.Wms.Application/Incidents/CountVerificationHook.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskDtos.cs` (`CountTaskLineRequest`), `ConfirmTaskLine.cs` (early
  `ConfirmsByCount` check), `src/Modules/Wms/Modules.Wms.Domain/Control/Incident.cs` (`UnlinkVerification`, `ResolveBySystem`,
  `AttachAdjustment`; the `CountVariance` itself is a plain `Report` with that type)
- Modify: `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (register
  `CountVerificationHook` as `ITaskClosedHook`)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskCountApiTests.cs`, `Incidents/CountVarianceAdjustmentTests.cs` (first part),
  `Incidents/IncidentTenantIsolationApiTests.cs` (add `count`), `Tasks/TaskSyncConflictApiTests.cs` (add `count`),
  `tests/Modules/Wms/Modules.Wms.UnitTests/Control/IncidentLifecycleTests.cs` (system resolution cases)

```csharp
/// <summary>Lo que el operario contó en la ubicación, a ciegas (DA-40). Contar cero es válido.</summary>
public sealed record CountTaskLineRequest(Guid LeaseToken, Guid LocationId, Guid? LotId, decimal QuantityCounted,
    DateTimeOffset? OccurredAt, string? DeviceId) : IIdempotentRequest;
```

`CountTaskLineHandler(OperatorLineRunner runner, IVariantProfileRepository profiles, IValidator<CountTaskLineCommand> validator)`:
validator (`quantityCounted >= 0` and `< 10^12`, `deviceId`, `occurredAt`) → `runner.RunAsync(Action = "count", Quantity =
QuantityCounted, LotId, apply)`; the guard already demands `CountPerform` for a `Count`. `apply`:
1. `!TaskTypePolicy.ConfirmsByCount(task.Type)` → `TaskTransitionException(task.Status, "count-line")` (before any other check).
2. `LocationId != line.FromLocationId` → `wms.task.location_mismatch`.
3. Profile `Serial` → 422 `wms.serial.required` (a serial `Count` is never generated, DA-44; this guards a tracking change after
   generation: nothing is recorded and the supervisor cancels the task).
4. Effective lot `line.LotId ?? command.LotId`; a fixed lot and a different scanned one → `wms.task.lot_mismatch`; null and
   `Tracking == Lot` → 422 `wms.lot.required` (so no adjustment is ever proposed that the ledger would refuse at posting); a scanned
   lot goes through `TaskLotRules.EnsureLotAsync`.
5. `task.RecordCount(member, …, quantityCounted, lot, now, occurredAt, ctx.MemberIsBusy)`. The runner then calls the notifier.

`ConfirmTaskLineHandler` and `ShortTaskLineHandler`: the `ConfirmsByCount` check moves right after the line lookup, so the operator
gets `wms.task.invalid_transition` instead of a confusing `location_mismatch`.

`CountVerificationHook : ITaskClosedHook` (constructor: `IIncidentRepository`, `IStockReadQueries`, `IAdjustmentRepository`,
`IWmsAuditRecorder`, `IExecutionContext`, `IClock`). Ignores any task that is not `Count`. **Never takes the count-key lock** (lock
order). On `Done`, for the counted line:
- `expected = stock.FindOnHandAsync(tenant, key with the counted lot)`; `delta = counted − expected`;
  `verified = incidents.ListOpenVerifiedByAsync(tenant, task.Id)`.
- `delta == 0`: each verified `ResolveBySystem(Accept, $"Verificada por {task.Number}: sin diferencia.", parentIncidentId: null, now)`
  (`ResolvedBy = Guid.Empty`, event `Resolved` with null member).
- `delta != 0`: `adjustments.NextNumberAsync` → `Adjustment.Propose(tenant, number, owner, warehouse, AdjustmentReason.CountVariance,
  note: task.Number, occurredAt: closed.OccurredAt, [AdjustmentLine.Create(location, variant, lot, status, delta, null, null)],
  proposedBy: executionContext.SubjectId, now)` → `adjustments.Add` → audit `AdjustmentProposed` as in `ProposeAdjustmentHandler`
  plus fields `source = "count"`, `taskNumber` (an adjustment is always audited) → `incidents.NextNumberAsync` →
  `Incident.Report(… CountVariance, subject (task.Id, line.Id, location, variant, lot, status, expected, counted), reportedBy:
  closed.ActorMemberId, operationKey: closed.OperationKey, reportedAt: closed.OccurredAt)` → `AttachAdjustment(adjustment.Id)` →
  `incidents.Add` → each verified `ResolveBySystem(Accept, $"Verificada por {task.Number}: ver {cv.Number}.", parentIncidentId:
  cv.Id, now)`. Never calls the ledger.
- On `Cancelled` (supervisor cancels the `Count` or its only line): each verified `UnlinkVerification(task.Number, now)` (clears
  `VerificationTaskId`, event `VerificationCancelled`); they stay `Open` for the supervisor to accept, and a later incident on the
  key creates a new `Count`.

Route: `OperatorAction<CountTaskLineRequest, TaskDto>(group, "/tasks/{id:guid}/lines/{lineId:guid}/count", CountAsync).RequireIdempotencyKey()`.

- [ ] **RED.** `TaskCountApiTests` (the `Count` comes from a `short` by another operator: stock 10, found 2 of 5 → the system has 8
  in `A-01-02`):
  - `CountingWhatTheSystemHasClosesTheIncidentsWithoutAdjustment` (count 8 → `ShortMove` `Resolved`/`Accept`, `resolved_by` empty
    GUID; no adjustment; the `Count` `Done`).
  - `CountingLessProposesACountVarianceAdjustment` (count 5 → one `Adjustment` `Proposed`, reason `CountVariance`, one line delta −3
    on `A-01-02`; a `CountVariance` `Open` with `adjustment_id`, `expected_qty = 8`, `actual_qty = 5`, `reported_by` = the counter,
    `operation_key` = the count key; the `ShortMove` `Resolved` with `parent_incident_id`; **no new movement**; audit
    `wms.adjustment.proposed` once).
  - `CountingMoreProposesAPositiveDelta` (count 9 → +1).
  - `CountingZeroIsValid` (delta −8; `quantity_counted = 0` stored).
  - `CountingALotLineUsesTheScannedLot` (lot-tracked variant, `Count` without lot from a `short` with 0 found → `count` with L1 →
    expected read for L1); `ALotTrackedVariantWithoutLotIs422LotRequired`.
  - `CountingASerialVariantIs422SerialRequired` (a `Count` seeded with `WarehouseTask.CreateCount` + `SaveAsync` on a variant set to
    `Serial` before any stock; nothing recorded, the task stays `InProgress`).
  - `RepeatingTheCountReturnsTheSavedResponse` (one adjustment, one `CountVariance`).
  - `AWrongLocationIs422LocationMismatch`; `ConfirmingACountIs422InvalidTransition`; `ShortingACountIs422InvalidTransition`;
    `CountingAMoveIs422InvalidTransition`.
  - `ACountScanBeforeTheReleaseFloorIs422`.
  - `CountingWithoutCountPerformIs403`.
  - `CancellingTheCountUnlinksItsIncidents` (supervisor `cancel` → incidents `Open`, no `verification_task_id`, event
    `VerificationCancelled`); `CancellingTheOnlyLineOfTheCountDoesTheSame`.
  - `AnotherShortAfterTheCountIsCancelledCreatesANewCount`.
  `TaskSyncConflictApiTests.ACountWithALostLeaseLeavesAConflict` (`action = "count"`, `actual_qty = quantityCounted`; no adjustment);
  `IncidentApiTests.AConflictFromACountCannotBeAcceptedIntoStock` (422 `resolution_not_allowed`; `allowedResolutions = []`).
  `IncidentTenantIsolationApiTests`: add `count` to the route table. Unit: `ASystemResolutionHasNoMemberAndCanPointToAParent`,
  `UnlinkingKeepsTheIncidentOpen`.
- [ ] Build fails. Copy the output.
- [ ] **GREEN.** Implement and register `CountTaskLineHandler`, its validator and the hook.
- [ ] Run:

  ```powershell
  dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Control.Incident|FullyQualifiedName~TaskClosedNotifierTests"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskCountApiTests|FullyQualifiedName~ACountWithALostLeaseLeavesAConflict|FullyQualifiedName~AConflictFromACountCannotBeAcceptedIntoStock|FullyQualifiedName~IncidentTenantIsolationApiTests" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `feat(wms): record a blind count and propose the variance adjustment`

### Task C5: The `CountVariance` adjustment: separation of duties, posting and rejecting

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Control/ApproveAdjustment.cs`, `PostAdjustment.cs`, `RejectAdjustment.cs` (inject
  `IIncidentRepository` and `WmsCaller`)
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Control/Incident.cs` (`ResolveByAdjustment(Guid batchId, Guid by, string adjustmentNumber,
  DateTimeOffset now)`, `RejectByAdjustment(Guid by, string note, DateTimeOffset now)` — both only for `CountVariance`)
- Modify: `tests/Modules/Wms/Modules.Wms.UnitTests/Control/*` if the handlers' constructors are built there (construction only)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Incidents/CountVarianceAdjustmentTests.cs`

- `ApproveAdjustmentHandler`: after loading the adjustment, `incidents.FindByAdjustmentAsync`; if there is a `CountVariance`, `member
  = caller.RequireAsync`; `member == cv.ReportedBy` (the counter) or `member ∈ ListReportersOfChildrenAsync(cv.Id)` → 422
  `wms.incident.self_resolution` (DA-17, DA-47). Memberships are compared, never `SubjectId`. Without an incident, unchanged (no
  membership required).
- `PostAdjustmentHandler`: after `MarkPosted`, the `CountVariance` of the adjustment → `ResolveByAdjustment(posting.BatchId, member,
  adjustment.Number, now)` in the same transaction.
- `RejectAdjustmentHandler`: the `CountVariance` → `RejectByAdjustment(member, command.Note, now)`.

- [ ] **RED.** `CountVarianceAdjustmentTests` (scenario of C4: count 5 against 8):
  - `TheReporterOfTheShortCannotApproveTheAdjustment` and `TheCounterCannotApproveIt` (both given `AdjustmentApprove`; 422
    `wms.incident.self_resolution`; the adjustment stays `Proposed`).
  - `AnotherApproverApprovesAndPostingResolvesTheCountVariance` (after `post`: `A-01-02` on hand = 5; `CountVariance` `Resolved`
    `Accept` with `batch_id`; audit of approve and post as today).
  - `RejectingTheAdjustmentRejectsTheCountVariance`.
  - `ACountVarianceCannotBeResolvedOrRejectedByHand` (422 `wms.incident.resolution_not_allowed` on `resolve` and `reject`).
  - `AnAdjustmentWithoutIncidentIsApprovedAsBefore` (a manual proposal by the same subject without membership checks).
- [ ] Build fails (new domain methods). Copy the output.
- [ ] **GREEN.** Implement.
- [ ] Run: `dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~CountVarianceAdjustmentTests|FullyQualifiedName~AdjustmentApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] Commit: `feat(wms): close count variance incidents through their adjustment`

### Task C6: Catalog usage of open incidents (DA-51) and forward compatibility of the sweeper and the board

**Files:**
- Modify: `src/Bootstrapper/CatalogVariantUsageChecker.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs`;
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockProbeTests.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs` (`LockExpiredAsync`,
  `CountByStatusAsync`, `CountOpenByTypeAsync`, `ListInProgressAsync`, `ListAssignedOpenAsync`), a new
  `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/KnownTaskValues.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskForwardCompatibilityTests.cs`

Catalog usage: one more `UNION`, `SELECT i.variant_id FROM wms.incidents i WHERE i.tenant_id = {tenantId} AND i.status IN ('Open',
'InReview') AND i.variant_id = ANY({ids})`, and a paragraph in the class summary (why: an open `SyncConflict` can write to the ledger
when resolved; which tests pin it). Rule `sql-entre-modulos`: the column names are pinned by the two tests below.

Forward compatibility: `KnownTaskValues` holds `Enum.GetNames` of `WarehouseTaskType`, `WarehouseTaskStatus`, `TaskLineStatus` and
`TaskSourceType`. `LockExpiredAsync` adds `AND t.type = ANY({types}) AND t.source_type = ANY({sources}) AND NOT EXISTS (SELECT 1 FROM
wms.task_lines l WHERE l.task_id = t.id AND l.line_status <> ALL({lineStatuses}))`; the four board queries filter the same way
(LINQ `Contains` over the known values, or raw SQL if EF does not translate the converted enum array — the test decides). A comment
on each says why: a newer pod may write values this build cannot materialize, and one such row must not stop the sweeper (it would
roll back every batch) or break the board.

- [ ] **RED.** `CatalogVariantUsageCheckerTests.AnOpenIncidentWithTheVariantCountsAsUse` (hand replica of `wms.incidents` with only
  the columns the query reads: `tenant_id`, `variant_id`, `status`) and `AResolvedOrRejectedIncidentDoesNot`;
  `StockProbeTests.TheRealCheckerSeesAnOpenIncidentAgainstTheMigratedSchema` and `TheRealCheckerSeesAnOpenCountLineAgainstTheMigratedSchema`.
  `TaskForwardCompatibilityTests` (own database, so altering the schema is allowed):
  - `TheSweeperSkipsATaskOfATypeThisBuildDoesNotKnow`: two claimed `Move`s with expired leases; `ALTER TABLE wms.tasks DROP CONSTRAINT
    "CK_tasks_type"`; `UPDATE wms.tasks SET type = 'FutureType'` on one → `RunOnceAsync` returns 1 without throwing and the known one
    is `Ready`.
  - `TheSweeperSkipsATaskWithALineStatusThisBuildDoesNotKnow` (drop `CK_task_lines_status`, set `line_status = 'FutureStatus'`).
  - `TheBoardIgnoresTypesAndStatusesThisBuildDoesNotKnow` (drop both task CHECKs; one task `FutureType` `InProgress`, one `Move`
    `FutureStatus`, one `Move` `Ready` → `GET /tasks/summary` 200; `byType`/`byStatus` list only known names and count only the known
    rows; `operators` does not list the unknown task).
- [ ] Build: no new signature → the RED is the filtered run failing (unknown rows throw). Copy the output.
- [ ] **GREEN.** Implement.
- [ ] Run:

  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CatalogVariantUsageCheckerTests"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~StockProbeTests|FullyQualifiedName~TaskForwardCompatibilityTests|FullyQualifiedName~TaskLeaseSweeperTests" -- xUnit.MaxParallelThreads=4
  ```

- [ ] Commit: `feat(wms): count open incidents as variant usage and skip unknown task values in background reads`

### Task C7: Documentation, rules and final verification

**Files:**
- Modify: `README.md` (§ Módulo Wms: «Novedades» subsection and the new task routes; «Despliegue de T2» paragraph with the rollout
  above)
- Modify: `.claude/rules/wms.md`, `.claude/rules/sql-entre-modulos.md` (the usage query adds `wms.incidents`; `LockNextCandidateAsync`
  reads `wms.incidents.verification_task_id`/`reported_by` by name)

- [ ] README: routes of §8.5 plus `short` and `count` with permission and idempotency; the T2 catalog and its resolutions; that the
  `Count` is blind and proposes a `CountVariance` adjustment; that `SyncConflict` is recorded even though the answer is 409; the
  `wms.incident.*` codes; forward-only rollout of T2.
- [ ] `.claude/rules/wms.md`, new prescriptive bullets:
  - «Toda acción del operario sobre una línea pasa por `OperatorLineRunner`, y todo cierre de tarea por `TaskClosedNotifier`. Un
    camino nuevo que cierre una tarea sin pasar por ahí deja un `Count` cancelado con novedades enlazadas o un conteo sin evaluar.»
  - «Un `Count` es a ciegas: lo esperado se calcula al contar (`FindOnHandAsync`) y no se guarda en la línea. Ningún campo de un DTO
    del operario puede llevarlo, ni siquiera "para depurar".»
  - «La novedad nace en la transacción del hecho, con su clave. La única excepción es `SyncConflict`, que se confirma sola antes del
    409: no "arregles" el `SaveChangesAsync` antes del `throw`.»
  - «Orden de bloqueos con novedades: operario → su tarea → `count-key:` → el `Count` abierto → numeraciones → saldos. La acción
    `count` no toma `count-key:`: ya tiene la fila del `Count`, y tomarla después invierte el orden de `short`.»
  - «Un tipo de novedad nuevo necesita: valor del enum, fila en `IncidentCatalog` (`EveryTypeHasAnEntry` lo exige), `CK_incidents_type`
    (migración) y, si mueve stock, su resolución en `ResolveIncidentHandler`.»
  - «Un tipo de tarea nuevo necesita además `TaskTypePolicy.ConfirmsByCount`» (extends the T1 bullet), and «el barrido y el tablero
    filtran por los valores que el build conoce (`KnownTaskValues`): no quites ese filtro, es lo que deja desplegar el corte siguiente».
- [ ] **Final verification**, one at a time, foreground, after `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` (ignore PID
  `133940`):

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~CompositionRootTests"
  dotnet test tests/Modules/Wms/Modules.Wms.UnitTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CatalogVariantUsageCheckerTests"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks|FullyQualifiedName~Modules.Wms.IntegrationTests.Incidents|FullyQualifiedName~StockProbeTests|FullyQualifiedName~WmsPersistenceTests|FullyQualifiedName~WmsUserReferenceProbeTests|FullyQualifiedName~AdjustmentApiTests|FullyQualifiedName~StockQueryTests" -- xUnit.MaxParallelThreads=4
  .\scripts\test-feature.ps1
  ```

  **`test-feature.ps1` in PowerShell 5.1:** the script runs with `$ErrorActionPreference = "Stop"`, so the stderr of a failing
  `dotnet test` aborts it before it prints its summary. Whatever it printed, read `TestResults\feature\report.md` and the `.trx` of
  each step (open them with `-LiteralPath`: some names carry brackets), and **read its warnings**: each «Requiere autorización del
  owner» goes into the deviations with file and reason (see «Owner authorization required before merge»).
- [ ] Commit: `docs(wms): document incidents, short lines and verification counts`

**Block C and branch close:** strong review of the whole branch by a read-only subagent without the implementer's context. The
merge to `develop` waits for the owner's authorization of every item in «Owner authorization required before merge», and
`docs/estado.md` is updated on `develop` at merge time, never on this branch.

---

## Self-review

- **Spec coverage:** §6.2 `Count` (permission, 30-min lease, no override, priority 3) → C1; §6.3 `Done` with `Short` → A2; §6.4 row
  «Novedad» (same transaction, same key) → A4, C3; §6.7 `SYNC_CONFLICT` → A5 (+ `count` in C4); §6.9 window/floor of `occurredAt` →
  A2, C1, C4; §6.10 incidents on the board → B3; §6.12 `LineShort` → A2; §7.9 short of a `Move` → A4; §8.1 entities → A3 (with the
  DA-43 columns); §8.2 catalog (T2 types) → A3; §8.3 states → B1; §8.4 rules 1 (A4, A5, C3, C4), 2 (C2, C3, C4), 3 (B1, B4, C5), 4
  (A4, B3: closed types per step), 5 (owner and warehouse on every row, list filter), 6 (approval by permission); §8.5 API → A4, B3,
  B4, plus `count` (DA-40).
- **T1 review item:** one runner for line actions and one close hook (A1), used by `confirm` (A1), `short` (A4), `count` (C4) and by
  the supervisor's `cancel`/`cancel-line` (A1, exercised in C4 by cancelling a `Count`).
- **Against the code:** `Incident` in `Control` does not change `WmsLayerTests`; `ProposedBy`/`ApprovedBy` of adjustments are
  `SubjectId` while incidents store memberships, so separation of duties compares **memberships** (`WmsCaller`). `CountOpenByTypeAsync`
  and the status list of `CatalogVariantUsageChecker` do not change because T2 adds no task status; `Short` lines are no longer
  `Open` and stop counting as usage, which is right (they cannot write any more). No new hosted service (`MultiReplicaInventory`
  intact). No new permission.
- **Contradictions resolved:** §8.2 says `COUNT_VARIANCE` «se resuelve con el ajuste» and §8.5 gives a generic `resolve`: the
  endpoint refuses it and `post`/`reject` of the adjustment close it. §8.5 has no route to record the count: `count` is added (DA-40)
  instead of bending `confirm`, whose body requires a destination and a quantity `> 0`. `short` has no destination: what is found goes
  to the planned one (DA-41). §8.5 `resolve` lists `toLocationId?` and `lotCode?`: T2's resolutions do not use them (they belong to
  `Relocate`/`LOT_MISSING`, later cuts), so the T2 body is `{ version, resolution, note? }`.
- **Known risks:** the `Count` does not lock the location, so a movement between the short and the count changes the expected
  quantity: the count compares against the balance **at count time**, which is right for a delta adjustment. The migrations' `Down`
  fail with T2 data (on purpose, commented). No rollback to pre-T2 code once T2 rows exist.

## Agent decisions (decisión del agente, por confirmar)

1. The incident core (complete vocabularies, `Report`, tables, numbering) lands in **Block A**, not B: `short` and `SyncConflict`
   persist an incident. Block B adds the lifecycle, the evidence lookup and the routes.
2. Two migrations: `AddIncidentsAndShortLines` (A) and `AddCountTasks` (C), so each block ships its own expand step.
3. The hook is application-level (`ITaskClosedHook` + `TaskClosedNotifier`), invoked by `OperatorLineRunner` and
   `SupervisorTaskRunner` inside the transaction; the domain keeps closing tasks as today.
4. Cancelling a `Count` (task or its only line) unlinks its verified incidents (new event `VerificationCancelled`) and leaves them
   `Open` for a manual `Accept`; the next incident on the key creates a new `Count`.
5. Lock order extension with `count-key:` (owner, location, variant, lot or `none`, status) and the `Count` row locked after it when
   linking; the `count` action never takes `count-key:` (avoids a deadlock with `short`).
6. The runner records `IWmsConfirmMetrics.RecordConfirmDuration` for every line action (`confirm`, `short`, `count`).
7. `SyncConflict.reported_at` = `occurredAt` if inside the ledger window, else server time (raw value kept in `raw_payload`);
   `raw_payload` adds `"action"` and drops `"leaseToken"`.
8. `AcceptIntoStock` of a `SyncConflict` only when `actualQty > 0` and the source line has a destination; a conflict from `count` or
   from a `short` with 0 found can only be rejected; `allowedResolutions` is computed per incident.
9. Reusing an `Idempotency-Key` with another body after a `SyncConflict` answers 409 `lease_lost` again (no receipt, so no
   `idempotency_key_reused`) and keeps the first payload.
10. `count` on a serial-tracked variant → 422 `wms.serial.required`, nothing recorded (only reachable if tracking changed after the
    `Count` was generated).
11. Evidence: ids de-duplicated; more than 5 distinct, foreign-tenant, wrong owner type or unknown → 422 `wms.incident.evidence_invalid`
    (domain/recorder rule, no FluentValidation rule).
12. `short` checks a scanned lot with the `CreateMoveTaskHandler` rule (404 `wms.lot.not_found`) because with 0 found there is no
    ledger call; `short` with 0 found on a lot-tracked variant needs no lot (the `Count` line keeps lot null and `count` requires it).
13. `POST /incidents` validates the variant through `IWmsCatalogReader` (`wms.profile.variant_unknown`), the lot like (12), the
    `sourceTaskId` as a task of the tenant and warehouse (404), and `occurredAt` with the ledger window.
14. No CHECK on `incident_events.kind` (like `task_events.kind`).
15. The adjustment proposed by a count has `ProposedBy = executionContext.SubjectId` of the counter and is audited as
    `wms.adjustment.proposed` with `source = "count"`; system resolutions of incidents are not audited (they stay in
    `incident_events`, `ResolvedBy = Guid.Empty`, shown as «Sistema»).
16. `ApproveAdjustmentHandler` requires a membership only when the adjustment has a `CountVariance`.
17. `expectedQty` of a `ShortMove` is the line's remaining quantity (not the bin balance); `GET /incidents` is readable with
    `wms.document.read`, which operators have.
18. Forward compatibility: the sweeper and the board filter by the enum names the build knows; the T2 rollout is forward-only and
    normal `RollingUpdate` is accepted (seconds of possible 500 on the old pod's board).
19. `resolve` body in T2 is `{ version, resolution, note? }` (no `toLocationId`/`lotCode`, unused by T2's resolutions).
20. (Block B) `POST /incidents`: an unparseable `type` is 422 `validation.failed` (field `Type`); a type that parses but is not in
    `IncidentCatalog.DeclarableStandalone` is 422 `wms.incident.type_not_allowed` (handler, after the permission check).
21. (Block B) `POST /incidents` stores `stock_status = NULL` and `expected_qty = NULL`; `quantity` goes to `actual_qty`. A `lotId`
    without `variantId` is 422 `validation.failed` (the FK to `lots` ignores the tenant, so a lot only enters validated against its
    variant and the Self owner with `TaskLotRules.EnsureLotAsync`). `occurredAt` outside the ledger window is 422
    `wms.movement.occurred_at_out_of_range`.
22. (Block B) `resolve` with `AcceptIntoStock` reads the line destination with a narrow query
    (`IWarehouseTaskRepository.FindLineDestinationsAsync`, no tracking, task not loaded) instead of `tasks.GetAsync`; the DTO composer
    uses the same query for `allowedResolutions`, and `FindNumbersAsync` for `sourceTaskNumber`.
23. (Block B) The `AcceptIntoStock` movement uses `occurredAt = incident.ReportedAt` while it is inside the 30-day ledger window and the
    resolve time otherwise (a conflict resolved after 30 days would be rejected by the ledger window otherwise). It also requires the
    incident to carry `stock_status` (every `SyncConflict` made by the runner does).
24. (Block B) `Incident.EnsureCanResolve(resolution)` is the part of `Resolve` that does not mutate, so `resolve` can fail before it
    builds the ledger movement; `Resolve` calls it.
25. (Block B) `resolve` takes the document lock `TryLockDocumentAsync(incident.Id)` like `post` of an adjustment: a second concurrent
    resolve of the same incident answers 409 `wms.document.busy` (or replays the winner's receipt through the SK filter).
26. (Block B) An incident reported by the system (`ReportedBy = Guid.Empty`) never counts as a self-resolution.
27. (Block C) The `count-key` lock and the open `Count` row are taken **before** `incident-seq` (not «right after `Incident.Report`»
    as C3 wrote it): the `count` action already holds the `Count` row and then numbers the adjustment and the variance incident, so
    linking after taking `incident-seq` is a deadlock. `EnsureVerificationAsync` is therefore two public methods of
    `TaskIncidentRecorder`: `LockVerificationAsync` (before numbering) and `LinkVerificationAsync` (after `Report`, before `Add`).
28. (Block C) A `LocationMismatch` reported by `POST /incidents` has no `stock_status` (decision 21); the `Count` it generates counts
    `Available`.
29. (Block C) `Incident.LinkVerification` and `AttachAdjustment` run at creation and do not bump `version`; `UnlinkVerification`,
    `ResolveBySystem`, `ResolveByAdjustment` and `RejectByAdjustment` do. `LinkVerification` takes an optional task number for the
    history detail; `UnlinkVerification` records the number of the cancelled `Count`.
30. (Block C) The hook matches the incidents a `Count` verifies to its counted line by location, variant and status (not lot: the
    scan may fill the lot). A system resolution is `Accept` with a note («Verificada por TSK-…: sin diferencia.» or «…: ver NOV-…»),
    `ResolvedBy = Guid.Empty`, history event without member.
31. (Block C) The `CountVariance` incident: `ReportedBy` is the counter's membership, `reportedAt` the scan time, `operation_key` the
    count's key, no `deviceId`, note «Conteo TSK-…: contó N y el sistema tenía M.». Its adjustment carries the task number as note
    and `source = "count"` plus `taskNumber` in the `wms.adjustment.proposed` audit.
32. (Block C) Posting the adjustment resolves the `CountVariance` with `Accept`, the batch and the posting membership; rejecting it
    rejects the incident with the adjustment's note.
33. (Block C) **Decisión del owner, 2026-11-27** (replaces the earlier «pending the owner»): nobody counts the verification `Count` of an
    incident they reported — not via `next`, not via `POST /tasks/{id}/claim`, not when a supervisor assigns or reassigns it (the
    `assign` fails), and the `count` action refuses it too (defense in depth), always 422 `wms.incident.self_resolution`. In a
    one-person warehouse the `Count` waits and the supervisor resolves the incident by hand.
34. (Block C) Forward compatibility covers the sweeper (`LockExpiredAsync`) and the board reads (`CountByStatusAsync`,
    `CountOpenByTypeAsync`, `ListInProgressAsync`, `ListAssignedOpenAsync`); `ListInProgressAsync` also skips tasks with a line
    status this build does not know. `ListAsync`/`GetAsync` are not filtered (a supervisor reading a row this build cannot read still fails).
35. (Block C) `CountVerificationHook` is registered in `WmsInfrastructureExtensions` next to `TaskClosedNotifier` (where A1 registered
    the notifier), and `CountTaskLineHandler` in `QepServiceCollectionExtensions` with the other task handlers.
36. (Block C) A `SyncConflict` left by a `count` with a lost lease has `allowedResolutions = []` (a `Count` line has no destination)
    and can only be rejected.

37. (Final review) **Decisión del owner, 2026-11-27 (H1):** approving or posting an adjustment with a `CountVariance` whose key balance
    differs from `variance.ExpectedQty` rejects the adjustment and the variance, requests (or links) a new `Count`, re-links the
    incidents and answers 422 `wms.adjustment.count_stale` after saving (same pattern as `SyncConflict`). The rejection is audited
    as `wms.adjustment.rejected` with the **system** as actor (`Guid.Empty`, `WmsActorType.System`) and the caller in `triggeredBy`;
    the incident event has no member (decision 15).
38. (Final review, decisión del agente, por confirmar) H1 details: the re-link moves **every** incident whose `verification_task_id` is
    the old `Count` (any status, not only open ones: in the normal flow the children were already resolved by the system, and
    separation of duties derives reporters from `verification_task_id`); the new `Count`'s source is the rejected `CountVariance`; a
    serial variant gets no new `Count`. `post` reads the balance without locks first (the recount needs `count-key` and numbering,
    which go before balances), then locks the adjustment's balance rows in ledger order (`StockKeyComparer`) and re-reads; if it
    changed in between it answers 412 `concurrency.conflict` (rolled back) and the retry takes the clean 422 path. A key with no
    balance row has nothing to lock (a concurrent first insert is not covered).
39. (Final review, confirmed by the owner 2026-11-27) M1: reporters of a `CountVariance` are `ListReportersVerifiedByAsync(sourceCount)`
    (replaces `ListReportersOfChildrenAsync`).
40. (Final review, decisión del agente, por confirmar) M3/L3: `AcceptIntoStock` is not offered nor accepted when the raw payload's
    `toLocationId` differs from the line destination (an unreadable value counts as different), when the variant is lot-tracked and
    the incident has no lot, or when `actualQty` exceeds the line's **current** remaining (≤ the remaining at incident time).
41. (Final review, security) The cap is cumulative: remaining = planned − confirmed − sum of `actual_qty` of incidents already
    `Resolved` with `AcceptIntoStock` on the same source line (read from `wms.incidents`). Resolutions of one line are serialized
    with the lock `task-line:{lineId}`, taken after the document lock and before the ledger balances (extension of decision 27;
    `resolve` takes no operator, task or `count-key` lock).
42. (Final review, confirmed by the owner 2026-11-27) L5: index `IX_incidents_tenant_reported` on `(tenant_id, reported_at, number)`
    (the order of `GET /incidents` is `reported_at DESC, number DESC`; `number` added so a backward scan serves the whole order),
    migration `AddIncidentsTenantReportedIndex`, expand-only.

**Owner confirmation, 2026-11-27:** the owner reviewed decisions 27, 29, 34, 35, 39, 41 and 42 one by one against the code and
confirmed all seven as implemented. They are no longer pending.

## Deviations

*(Filled by each implementer, per block, as in T1.)*

### Block B

- **B1:** none beyond decision 24 (added in B4).
- **B2:** `ShortTaskLineRequest.PhotoFileIds` is declared with a `null` default so existing callers compile; the recorder exposes
  `ResolveEvidenceAsync` (also used by `POST /incidents` in B3) and validates evidence **before** numbering. B2's recorder constructor
  left the unit-test doubles (`TaskWorld`) uncompiled; they were fixed in B3's commit, so the B2 commit alone does not build
  `Modules.Wms.UnitTests`.
- **B3:** `IncidentDtoComposer` and `IncidentResolutionRules` live in `IncidentDtoComposer.cs`, not in `IncidentDtos.cs`. The DTOs
  `IncidentDto` and `IncidentSummaryDto` are flat records (no inheritance), same JSON as the plan. New repository members:
  `IIncidentRepository.GetAsync/ListAsync/CountOpenAsync`, `IWarehouseTaskRepository.FindNumbersAsync/FindLineDestinationsAsync`.
  `GET /incidents/{id}` uses `GetWithEventsAsync` (no tracking). New unit test `IncidentRoutesTests` (route table with policies).
- **B4:** `resolve` reads the line destination with the narrow query (decision 22) instead of `tasks.GetAsync`. Added the unit test
  `EnsureCanResolveAnswersLikeResolveAndChangesNothing` and the route table cases for `review`, `resolve` and `reject`.

### Final review fixes (2026-11-27)

- **M2/H1/M1/M3/L1/L2/L5:** see decisions 33 and 37 to 42. The existing test `TheSupervisorCanAssignTheCountToTheReporter` was removed
  (it asserted the opposite of decision 33; `TheSupervisorCannotAssignTheCountToTheReporter` replaces it). `FindLineDestinationsAsync`
  became `FindLineFactsAsync` (destination and remaining). `ListReportersOfChildrenAsync` became `ListReportersVerifiedByAsync`.
- **L4** (`ShortAndCountConcurrencyApiTests`) and the sequential/concurrent cap tests characterize behavior that was already
  correct or were written with the fix; L4 was green on the first run (no RED).

### Block C

- **C1:** the board lists the new type `Count` at zero, so two existing assertions changed (`SupervisorTaskActionsTests.
  TheBoardListsEveryStatusAndTypeEvenAtZero`, `TaskSupervisorActionsApiTests`): `ByType` is `[Move, Count]`, not `[Move]`.
- **C2:** none. `FindOpenCountAsync` only counts a `Count` with an `Open` line on the key.
- **C3:** decision 27 (lock order) replaces the single `EnsureVerificationAsync`. Existing test `ShortWithTwoFoundMovesTwoAndClosesTheTask`
  now asserts a verification task instead of `null`. Unit doubles (`TaskWorld`) gained profile, task numbering and the new recorder
  dependencies.
- **C4:** the new `count` cases live in `TaskCountRecordingApiTests` (a second class; the C3 cases are in `TaskCountApiTests`). Fixed a
  Block B defect found here: `resolve` and the incident DTO read a line destination with `GetValueOrDefault`, which gives
  `Guid.Empty` (not `null`) for a line without destination, so a conflict from a `count` answered 404 `wms.location.not_found`
  instead of 422 `resolution_not_allowed`; both now use `TryGetValue` (`AConflictFromACountCannotBeAcceptedIntoStock` pins it).
- **C5:** none. `Incident.EnsureNotInvolvedInApproval` carries the separation-of-duties rule (unit tested).
- **C6:** `KnownTaskValues` holds the names (SQL) and the values (LINQ) of the four enums; EF translated the converted enum arrays,
  so no raw SQL was needed for the board reads.
- **C7:** `AddCountTasks` and `AddIncidentsAndShortLines` were added to the README migrations list together (Block A did not).
  Closed the three extra gaps from Block B: `TwoConcurrentResolvesLeaveOneWinnerAndNoServerError`,
  `ResolvingAndRejectingAConflictAtTheSameTimeNeverDoesBoth` and `AFailureMovingTheStockRollsBackTheResolutionEntirely` (integration:
  the ledger refuses the movement because the source has no stock). They characterize behavior that was already correct, so they
  were green on first run (no RED).
