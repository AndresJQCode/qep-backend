# WMS-T1 · Task engine review fixes — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (or subagent-driven-development). **The rules in the
> `qep` workspace `CLAUDE.md` override the skill:** exactly **two blocks, one implementer per block, run one after the other**
> (Block B starts only after Block A is committed). One review per block, and the strong review once at the end. Every task keeps
> its own commit. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Apply the fixes the final reviewer (Fable 5.2, with the owner's delegation of 2026-11-26) decided for the WMS task engine
(cut T1): the sweeper citation and the permission anchor name in the architecture tests, lease renewal on `next`/`claim` (M2) and
the `resumable` extension on `wms.task.lease_lost`, `hold` revoking an expired claim (B1), a lower bound for `occurredAt` (B2), the
orphaned-assignment list on the board (M1), composing DTOs without loading the whole warehouse plus board/list indexes (M4), a
server `recorded_at` for the task history (B3), the missing integration tests (B6), and the spec update. **The decisions are
closed: do not reopen them.** What this plan had to decide on its own is listed at the end under «Agent decisions».

**Architecture:** No new area, no new module reference, no new route, no new error code. Domain changes stay in
`Modules.Wms.Domain/Tasks/WarehouseTask.cs` and `WarehouseTaskEvent.cs`; application changes in `Modules.Wms.Application/Tasks/`;
two read methods are added to existing ports (`IWarehouseTaskRepository.ListAssignedOpenAsync`,
`IWarehouseRepository.FindLocationCodesAsync`); two expand-only migrations (`AddTaskBoardIndexes`, `AddTaskEventRecordedAt`). The
HTTP contract changes only additively: `resumable` in the 409 extensions and `staleAssignments` in `TaskBoardDto`.

**Tech Stack:** .NET 10, minimal APIs, EF Core 10 + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3, Testcontainers.PostgreSql.

**Spec:** [`docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md`](../specs/2026-11-26-wms-tareas-y-operaciones-design.md)
§6.1, §6.7–§6.11, §12, §13. **Previous plan (format and context):**
[`2026-11-26-wms-t1-motor-de-tareas.md`](2026-11-26-wms-t1-motor-de-tareas.md).

**Out of scope:** M3 (who may `claim` what), B4 (maximum claim age), B5 (daily numbering in Colombia time), B7, frontend and
Android, `docs/estado.md` (updated on `develop` at merge time), `GET /tasks/mine` showing an expired `leaseExpiresAt` on its
`current` (not part of M2).

---

## Before starting

- **Worktree (work only here):**
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-t1-fixes`, branch
  `feature/wms-t1-fixes` (from `develop` `8f486325a`). Every script starts with
  `Set-Location "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-t1-fixes"`. Never
  write to the main checkout (`qep-backend`) or another repo.
- **PowerShell only:** no `&&` (`A; if ($?) { B }`), `$env:VAR = "value"` on its own line, `Get-Content` instead of `cat`.
- **Never rewrite a `.cs` with `Set-Content`/`Out-File`** (breaks accents, adds a BOM): use the edit tool.
- **Build lock:** if an `Api.exe` (or a debugger) launched from **this worktree** holds `src/Api/bin`, do not stop it (it is not
  yours: tell the owner). Build only the test project, or add `--artifacts-path .\artifacts` (inside this worktree; `artifacts/`
  is in `.gitignore`) to every `dotnet build`/`dotnet test` of the block.
- **Integration runs:** Docker up, and in the same session:

  ```powershell
  $env:Seed__Enabled = "false"
  Get-Process testhost, dotnet -ErrorAction SilentlyContinue
  ```

  If a `testhost` or a `dotnet test` from another session is running, **wait** and check again; never stop it. Never two
  integration runs at once. Tests run **in the foreground**.
- **RED of a compile failure does not start containers:** the evidence is the build output (`dotnet build <test project>`), copied
  literally into the task notes. Integration runs are filtered by test name while iterating; full classes run once at the end of
  Block B.
- **Migrations** (from the worktree root, with the design-time factory, never `--startup-project`):

  ```powershell
  dotnet ef migrations add <Name> --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
  ```

- **Before every commit:** `git branch --show-current` must print `feature/wms-t1-fixes`; if not, stop. Stage explicit paths only
  (`git add <path> ...`), never `git add -A`. Conventional commits in English. **Every commit ends with this trailer** (blank line
  before it; this repo's rule overrides the global "no attribution" rule):

  ```
  Session: Pendientes del WMS
  Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
  ```

  Commit with a single-quoted here-string so PowerShell does not expand anything:

  ```powershell
  git commit -F - @'
  fix(wms): <subject>

  Session: Pendientes del WMS
  Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
  '@
  ```

  (If `git commit -F -` does not read the here-string in your shell, write the message to a file in `$env:TEMP` with the edit
  tool and use `git commit -F <file>`.) Do not push.
- Code comments and texts for people in Spanish (Colombian, tú); identifiers in English. No `docs/estado.md` changes.

## Existing pieces this plan uses (verified at `8f486325a`)

| Piece | Where | Use |
| --- | --- | --- |
| `WarehouseTask` (`Claim`, `CheckLease`, `Heartbeat`, `Hold`, `ConfirmLine`, `Renew`, `ClearClaim`, `Record`, `CloseIfFinished`, `MarkCancelled`) | `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs` | B1, B2, M2, B3 |
| `WarehouseTaskEvent.Record(tenantId, taskId, kind, memberId, deviceId, lineId, quantity, detail, at)` (only caller: `WarehouseTask.Record`, line 529) | `Modules.Wms.Domain/Tasks/WarehouseTaskEvent.cs` | B3 |
| `LedgerEntryRules.EnsureOccurredAtInWindow`, `WmsErrorCodes.MovementOccurredAtOutOfRange` (`wms.movement.occurred_at_out_of_range`) | `Modules.Wms.Domain/Inventory/LedgerEntryRules.cs`, `Modules.Wms.Domain/WmsErrorCodes.cs:78` | B2 (same code, no new one) |
| `OperatorTaskGuard.EnsureLease` / `EnsureActiveLease` / private `LeaseLost()` — the **only** place `wms.task.lease_lost` is raised | `Modules.Wms.Application/Tasks/OperatorTaskGuard.cs:32-51` | M2 `resumable` |
| `TaskMemberBusyException : ResourceConflictException, IHasProblemExtensions` (`taskId`) | `Modules.Wms.Application/Tasks/TaskMemberBusyException.cs` | Template for `TaskLeaseLostException` |
| `ApiExceptionHandler`: copies `IHasProblemExtensions.ProblemExtensions` with `TryAdd`; `ResourceConflictException` → 409 by base type | `src/Api/ApiExceptionHandler.cs:86-93, 173` | **No change needed** |
| `NextTaskHandler` returns the member's `InProgress` task from `FindInProgressOfMemberAsync` (untracked, **no row lock**) | `Modules.Wms.Application/Tasks/NextTask.cs:51-61` | M2 |
| `ClaimTaskHandler` returns the same task "without changes" when it is already the member's | `Modules.Wms.Application/Tasks/ClaimTask.cs:52-60` | M2 |
| `IWarehouseTaskRepository` (`LockAsync`, `GetAsync`, `FindInProgressOfMemberAsync`, `CountByStatusAsync`, `CountOpenByTypeAsync`, `ListAsync`) | `Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs` | M1, M2, M4 |
| `WarehouseTaskRepository` — `LockExpiredAsync` is **lines 201-216** today (`FOR UPDATE SKIP LOCKED` at 213); `ListAsync` orders `created_at DESC, number DESC` (70-71); board counts filter `tenant_id`, `warehouse_id` and group by `status` / `type` (78-102) | `Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs` | Fix 1, M4 |
| Indexes on `wms.tasks`: `IX_tasks_tenant_number`, `IX_tasks_pool (tenant_id, warehouse_id, status) WHERE status IN ('Ready','Assigned')`, `IX_tasks_member_in_progress`, `IX_tasks_lease`, `IX_tasks_source`, `IX_tasks_owner_id`, `IX_tasks_warehouse_id` (FK convention); `IX_task_events_task (task_id, occurred_at)` | `WarehouseTaskModel.cs:74-85, 180`, migration `20261126051642_AddWarehouseTasks.cs` | M4, B3 |
| `IWmsMemberDirectory.ListWithPermissionAsync(tenant, permission)` (active memberships whose roles grant it), `FindNamesAsync` | `Modules.Wms.Application/Tasks/IWmsMemberDirectory.cs`, adapter `src/Bootstrapper/WmsMemberDirectory.cs` | M1 |
| `TaskTypePermissions.For(type)` | `Modules.Wms.Application/Tasks/TaskTypePermissions.cs:10` | M1 |
| `WarehouseTaskDtoComposer` (`ComposeAsync` loads `warehouses.GetAsync` = whole warehouse **tracked**; `ComposeSummariesAsync` loads `warehouses.ListAsync` = every warehouse of the tenant with all locations) | `Modules.Wms.Application/Tasks/WarehouseTaskDtoComposer.cs:26, 116` | M4 |
| `IWarehouseRepository` | `Modules.Wms.Application/WmsPorts.cs:100-120`, `Modules.Wms.Infrastructure/Persistence/WarehouseRepository.cs` | M4 |
| Typed-id `Contains` precedent (`wanted.Contains(lot.Id)`) | `Modules.Wms.Infrastructure/Persistence/LotRepository.cs:22-25` | M4 |
| Unit doubles: `TaskWorld` (`FakeTasks`, `FakeWarehouses`, `FakeMembers`, `Eligible`, `Names`, `Log`, `Now`) | `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/TaskWorld*.cs` | Every unit test |
| Integration support: `OpenAsync`, `ArrangeAsync`, `CreateClient`, `OtherTenantId`/`OtherSubjectId`, `AllPermissions`, `CodeOfAsync`, `BodyAsync`, `ScalarAsync`, `ExecuteAsync`; `TaskApiSupport` (`TasksUrl`, `ActionRawAsync`, `ActionAsync`, `GetAsync`, `SeedOperatorAsync`, `SecondOperatorSubject`); `TaskTestSupport` (`NewMove`, `SaveAsync`, `ReloadAsync`, `History`) | `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness*.cs`, `Tasks/TaskApiSupport.cs`, `Tasks/TaskTestSupport.cs` | B6 |
| The sweeper timer does **not** run in tests (the harness sets a one-hour startup delay): tests call `TaskLeaseSweeper.RunOnceAsync(now)` | `Tasks/TaskLeaseSweeperTests.cs:12-13` | B3, B6 |
| Idempotency conflict code on a reused key with another body: `wms.idempotency_key_reused` | `Tasks/TaskSupervisorApiTests.cs:62-76` | B6 |
| A foreign tenant calls **our** route (`TasksUrl()` = `TenantId`) with `X-Tenant-Id: OtherTenantId` and every permission → 403 `authorization.denied` | `Tasks/TaskSupervisorApiTests.cs:109-122` | B6 |

---

# Block A — domain, application and contract (fixes 1, 2, 3, 6, 7 and the spec)

One implementer. Order: A1 → A2 → A3 → A4 → A5 → A6. `WarehouseTaskRepository.cs` is **not** touched in this block.

### Task A1: Sweeper citation and permission anchor name (fixes 1 and 2)

**Files:**
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs:133-134`
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:350`

This is a text-only change to tests: there is no meaningful RED. The evidence is the filtered run before (old name found) and after
(new name found, old name gone).

- [ ] Confirm the real lines first (they are the citation):

  ```powershell
  Select-String -LiteralPath src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs -Pattern "LockExpiredAsync\(|FOR UPDATE SKIP LOCKED|NextNumberAsync\("
  ```

  Expected: `LockExpiredAsync(` at 201, the second `FOR UPDATE SKIP LOCKED` at 213, `NextNumberAsync(` at 219 (so the method spans
  201-216). If the numbers differ, use the real ones.
- [ ] In `MultiReplicaInventory.cs`, the `TaskLeaseSweeper` evidence becomes:

  ```csharp
  new("Modules.Wms.Infrastructure.Workers.TaskLeaseSweeper", WorkerClassification.Claimed,
      "Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs:201-216 (LockExpiredAsync) — toma las tareas vencidas con FOR UPDATE SKIP LOCKED en la transacción de la pasada; dos réplicas reparten filas distintas y el dominio (ExpireLease) no hace nada si el lease ya no está vencido"),
  ```

- [ ] In `CompositionRootTests.cs`, rename `PermissionDiscoveryFindsTheHundredAndThirtyConstants` to
  `PermissionDiscoveryFindsEveryPermissionConstant` (no count in the name: the count lives only in `Assert.Equal(131, …)`, so the
  name can no longer drift from the assertion). Keep the `<summary>`, the comment list and `Assert.Equal(131, …)` as they are. Add
  one line at the top of the `<summary>`: `/// La cuenta vive sólo en el Assert: el nombre ya no la lleva, para que no se desfase.`
- [ ] Run:

  ```powershell
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~PermissionDiscoveryFindsEveryPermissionConstant"
  ```

  Expected: green; `PermissionDiscoveryFindsEveryPermissionConstant` is listed as passed.
- [ ] Commit: `test(architecture): cite LockExpiredAsync for the task sweeper and drop the count from the permission anchor`
  (paths: the two files).

### Task A2: `next` and `claim` renew the caller's own lease (M2, part 1)

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs` (new `RenewClaim`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/NextTask.cs`, `ClaimTask.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskLeaseTests.cs`, `OperatorTaskHandlersTests.cs`

Rules (closed by the reviewer): when the caller's own task is already `InProgress` for them, `next` and `claim` **lock the row**
(`LockAsync` inside the transaction, after the member lock and before `GetAsync`) and **renew the lease with the same token**, like
the heartbeat: **no version bump, no event**. This also covers an expired lease the sweeper has not processed yet (status still
`InProgress`): nobody else could have taken it.

- [ ] **RED (domain).** Add to `WarehouseTaskLeaseTests`:

  ```csharp
  // M2: pedir otra vez la tarea en curso renueva el lease con el mismo token, como el latido: sin versión ni evento.
  [Fact]
  public void RenewingMyClaimExtendsTheLeaseWithTheSameTokenWithoutVersionOrEvent()
  {
      var (task, token) = Claimed();
      var events = task.Events.Count;

      // Vencido pero sin barrer: sigue InProgress y nadie más pudo tomarlo.
      task.RenewClaim(Member, Expired);

      Assert.Equal((WarehouseTaskStatus.InProgress, token, 2L), (task.Status, task.LeaseToken!.Value, task.Version));
      Assert.Equal(Expired.AddMinutes(15), task.LeaseExpiresAt);
      Assert.Equal(Expired, task.LastActivityAt);
      Assert.Equal(events, task.Events.Count);
  }

  [Fact]
  public void OnlyTheClaimerRenewsATaskInProgress()
  {
      var (task, _) = Claimed();

      Assert.Throws<InvalidOperationException>(() => task.RenewClaim(Other, Expired));
      Assert.Throws<TaskTransitionException>(() => NewMove().RenewClaim(Member, Now));
  }
  ```

- [ ] **RED (handlers).** In `OperatorTaskHandlersTests`:
  - Replace `NextReturnsTheTaskAlreadyInProgressInThatWarehouse` with:

    ```csharp
    // M2: la que ya tiene en curso vuelve con el lease renovado, bajo el lock de la fila, aunque el barrido no haya pasado.
    [Fact]
    public async Task NextRenewsTheTaskAlreadyInProgressInThatWarehouse()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        world.Candidate = world.AddTask().Id;
        world.Now = TaskWorld.Start.AddMinutes(20);

        var result = await NextAsync(world);

        Assert.Equal((task.Id, task.LeaseToken, 2L), (result.Task!.Id, result.Task.LeaseToken, result.Task.Version));
        Assert.Equal(TaskWorld.Start.AddMinutes(35), result.Task.LeaseExpiresAt);
        Assert.Equal(
            ["begin", $"lock task-member:{TaskWorld.Member:D}", "lock-task", "load-task", "save"], world.Log);
    }
    ```

  - Replace `ClaimingTheTaskAlreadyInProgressReturnsItWithoutChanges` with
    `ClaimingTheTaskAlreadyInProgressRenewsTheLeaseWithTheSameToken`: same arrange, `world.Now = TaskWorld.Start.AddMinutes(20)`,
    assert `(dto.LeaseToken, dto.Version) == (task.LeaseToken, 2L)`, `dto.LeaseExpiresAt == Start + 35 min` and
    `Assert.Contains("save", world.Log)`.
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: fails with `CS1061` (`WarehouseTask` has no `RenewClaim`). Save the
  output (RED). The handler tests fail only after the domain compiles; run them once the method exists and before touching the
  handlers to record their assertion failure (`Assert.Equal() Failure` on `LeaseExpiresAt` / the log).
- [ ] **GREEN (domain).** In `WarehouseTask`, next to `Heartbeat`:

  ```csharp
  /// <summary>Quien ya tiene la tarea en curso la vuelve a pedir («siguiente» o «reclamar»): renueva el lease con el mismo token, sin
  /// evento ni versión, como el latido. Vale también con el lease vencido mientras el barrido no haya pasado: sigue en curso y nadie
  /// más pudo tomarla.</summary>
  public void RenewClaim(Guid memberId, DateTimeOffset now)
  {
      EnsureStatus("claim", WarehouseTaskStatus.InProgress);
      if (ClaimedByMemberId != memberId)
      {
          throw new InvalidOperationException($"Task {Id} is in progress for another member.");
      }

      Renew(now);
  }
  ```

  The device and the token do not change (DA-34 is about reclaiming an *expired, swept* claim; this one is still in progress).
- [ ] **GREEN (`NextTaskHandler`).** Replace the `current is not null` block:

  ```csharp
  var current = await tasks.FindInProgressOfMemberAsync(command.TenantId, member, cancellationToken);
  if (current is not null)
  {
      if (current.WarehouseId.Value != command.WarehouseId)
      {
          throw new TaskMemberBusyException(current.Id);
      }

      // M2: la fila se bloquea y se recarga antes de renovar (orden: operario → tarea). Si el barrido la venció en el medio, ya no
      // está en curso y sigue el camino normal, que se la vuelve a dar con el mismo token (DA-34).
      if (await tasks.LockAsync(command.TenantId, current.Id, cancellationToken)
          && await tasks.GetAsync(command.TenantId, current.Id, cancellationToken) is { Status: WarehouseTaskStatus.InProgress } mine
          && mine.ClaimedByMemberId == member)
      {
          mine.RenewClaim(member, clock.UtcNow);
          var renewed = await composer.ComposeAsync(mine, member, withEvents: false, cancellationToken);
          await unitOfWork.SaveChangesAsync(cancellationToken);
          return new NextTaskResultDto(renewed);
      }
  }
  ```

  Add `using Modules.Wms.Domain.Tasks;` if missing. Update the handler `<summary>`: «Repetirla devuelve la que tiene en curso, con
  el lease renovado.»
- [ ] **GREEN (`ClaimTaskHandler`).** The task is already locked and loaded (tracked). Replace the `current is not null` block:

  ```csharp
  if (current is not null)
  {
      if (current.Id != task.Id)
      {
          throw new TaskMemberBusyException(current.Id);
      }

      // M2: reclamar la que ya tiene en curso renueva el lease con el mismo token, como el latido.
      task.RenewClaim(member, clock.UtcNow);
      var renewed = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
      await unitOfWork.SaveChangesAsync(cancellationToken);
      return renewed;
  }
  ```

  Update the `<summary>`: «Reclamar la que ya tiene en curso la devuelve con el lease renovado.»
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] Commit: `fix(wms): renew the lease when the operator asks again for the task in progress` (paths: the five files).

### Task A3: `wms.task.lease_lost` says whether the lease is resumable (M2, part 2)

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskLeaseLostException.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/OperatorTaskGuard.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/OperatorTaskHandlersTests.cs`

Rule: the 409 carries a problem-details extension `resumable`: `true` when `CheckLease == Resumable` (expired, nobody else took
it), `false` otherwise (someone else has it, reassigned, held, unclaimed, terminal). `ApiExceptionHandler` already copies
`IHasProblemExtensions` (`TryAdd`), exactly as `member_busy` carries `taskId`: **no change in `src/Api`**.

- [ ] **RED.** In `OperatorTaskHandlersTests` (note: `Assert.ThrowsAsync<T>` matches the exact type, so the two existing
  `ThrowsAsync<ResourceConflictException>` for the lease must change):
  - `AStaleTokenIs409LeaseLost`: `var error = await Assert.ThrowsAsync<TaskLeaseLostException>(…)`, keep the code assertion, add
    `Assert.False(error.Resumable);` and `Assert.Equal(false, error.ProblemExtensions["resumable"]);`.
  - `AHeartbeatOnlyAcceptsTheCurrentLease`: `ThrowsAsync<TaskLeaseLostException>`, add `Assert.True(error.Resumable);` (expired
    and nobody else took it: the app can `claim` it again).
  - New test:

    ```csharp
    // La app trata como éxito un unclaim repetido: la tarea ya no es suya y no se puede retomar con ese token.
    [Fact]
    public async Task RepeatingAnAppliedUnclaimIs409NotResumable()
    {
        var world = new TaskWorld();
        var (task, _) = ClaimedByCaller(world);
        var token = task.LeaseToken!.Value;
        var handler = new UnclaimTaskHandler(
            world.Context(), world.Authorization, world.Caller(), world.UnitOfWork(), world.Repository(), Guard(world),
            world.Clock(), world.Composer(), new UnclaimTaskValidator());
        var command = new UnclaimTaskCommand(TaskWorld.Tenant, task.Id, token, "ShiftEnd");
        await handler.HandleAsync(command, Ct);

        var error = await Assert.ThrowsAsync<TaskLeaseLostException>(() => handler.HandleAsync(command, Ct));

        Assert.Equal(WmsErrorCodes.TaskLeaseLost, error.Code);
        Assert.False(error.Resumable);
    }
    ```

- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: fails with `CS0246` (`TaskLeaseLostException` not found). Save
  the output (RED).
- [ ] **GREEN.** `TaskLeaseLostException.cs`, modeled on `TaskMemberBusyException`:

  ```csharp
  using BuildingBlocks.Application;
  using BuildingBlocks.Domain;
  using Modules.Wms.Domain;

  namespace Modules.Wms.Application;

  /// <summary>El token no es el del reclamo vigente (<c>wms.task.lease_lost</c>, 409). <c>resumable</c> viaja en las extensiones para
  /// que la app sepa qué hacer: <c>true</c> si el lease venció y nadie más tomó la tarea (puede pedirla otra vez con <c>claim</c> y
  /// seguir), <c>false</c> si es de otra persona, se la reasignaron, la retuvieron o ya la soltó. Un <c>unclaim</c> repetido llega con
  /// <c>false</c> y la app lo trata como éxito.</summary>
  public sealed class TaskLeaseLostException(bool resumable)
      : ResourceConflictException(
          WmsErrorCodes.TaskLeaseLost,
          resumable ? "Tu reclamo de esta tarea venció. Pídela otra vez para seguir." : "Esta tarea ya no es tuya."),
        IHasProblemExtensions
  {
      public bool Resumable { get; } = resumable;

      public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
          new Dictionary<string, object?> { ["resumable"] = resumable };
  }
  ```

  In `OperatorTaskGuard`: `EnsureLease` throws `new TaskLeaseLostException(resumable: false)` (it only throws on `Lost`);
  `EnsureActiveLease` becomes:

  ```csharp
  var lease = task.CheckLease(memberId, leaseToken);
  if (lease != LeaseCheck.Valid)
  {
      throw new TaskLeaseLostException(resumable: lease == LeaseCheck.Resumable);
  }
  ```

  Delete the private `LeaseLost()` helper. Update the `<summary>` of `EnsureActiveLease`: «… si no, 409 con `resumable`.»
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] Commit: `feat(wms): tell the app whether a lost task lease is resumable` (paths: the three files).

### Task A4: `hold` revokes an expired claim (B1)

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs` (`Hold`)
- Modify: `.claude/rules/wms.md` (the «El barrido conserva…» bullet)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskLeaseTests.cs`

- [ ] **RED.** Add to `WarehouseTaskLeaseTests`, next to `AHeldTaskIsLostForItsLastClaimer`:

  ```csharp
  // B1: retener borra el reclamo que dejó un lease vencido; si no, al volver a publicar la confirmación tardía de antes la reanudaría.
  [Fact]
  public void HoldingAndPublishingAgainRevokesTheExpiredClaim()
  {
      var (task, token) = Claimed();
      task.ExpireLease(Expired);

      task.Hold(Supervisor, null, Expired);
      task.Publish(Supervisor, Expired.AddMinutes(1));

      Assert.Equal(LeaseCheck.Lost, task.CheckLease(Member, token));
      Assert.Null(task.ClaimedByMemberId);
      Assert.Null(task.ClaimedDeviceId);
      Assert.Null(task.LeaseToken);
      task.Claim(Member, Device, Expired.AddMinutes(2));
      Assert.NotEqual(token, task.LeaseToken);
  }
  ```

- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~HoldingAndPublishingAgainRevokesTheExpiredClaim"`:
  fails on `Assert.Equal() Failure` (`Lost` expected, `Resumable` actual). Save the output (RED; it compiles, so this is the
  assertion failure).
- [ ] **GREEN.** In `Hold`, after `OptionalReason` and before changing the status:

  ```csharp
  // B1: un reclamo que quedó de un lease vencido no sobrevive a la retención. Al publicar otra vez, una confirmación tardía de
  // antes ya no la reanuda ni conserva el token (DA-35).
  ClearClaim();
  ```

  Update the `<summary>` of `Hold`: «La saca del pool; conserva a quién está asignada y borra el reclamo que haya quedado de un
  lease vencido.» `Hold` only accepts `Ready`/`Assigned`, so a claim there is always a leftover of an expired lease.
- [ ] In `.claude/rules/wms.md`, append to the «El barrido conserva `claimed_by_member_id` y `lease_token` al vencer» bullet:
  «**Retener (`hold`) sí los limpia** (revisión T1, B1): sin eso, retener y volver a publicar dejaba reanudable una confirmación de
  antes de la retención.»
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] Commit: `fix(wms): revoke an expired claim when a task is held` (paths: the three files).

### Task A5: a scan cannot be earlier than the task release (B2)

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs` (`ConfirmLine`)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskLeaseTests.cs`, `OperatorTaskHandlersTests.cs`

Rule: `occurredAt` (already UTC) must be `>= ReleasedAt ?? CreatedAt`; otherwise 422 with the **existing** code
`wms.movement.occurred_at_out_of_range` (`WmsErrorCodes.MovementOccurredAtOutOfRange`). Equal is allowed. Checked after the ledger
window and **before** resuming anything.

- [ ] **RED.** In `WarehouseTaskLeaseTests`:

  ```csharp
  // B2: nadie escanea una tarea antes de que exista en el pool.
  [Fact]
  public void AScanEarlierThanTheReleaseIsRejectedWithoutResuming()
  {
      var (task, token) = Claimed();
      task.ExpireLease(Expired);

      var error = Assert.Throws<WmsDomainException>(() => task.ConfirmLine(
          Member, token, Device, task.Lines[0].Id, 1, Expired, Now.AddSeconds(-1)));

      Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
      Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
      Assert.Equal(0, task.Lines[0].QuantityConfirmed);
  }

  [Fact]
  public void AScanAtTheReleaseInstantIsAccepted()
  {
      var (task, token) = Claimed();

      task.ConfirmLine(Member, token, Device, task.Lines[0].Id, 1, Now.AddMinutes(1), Now);

      Assert.Equal(1, task.Lines[0].QuantityConfirmed);
  }
  ```

  And in `OperatorTaskHandlersTests`, extend `AScanOutsideTheWindowIs422BeforeTheLedger` with a third case:
  `command with { OccurredAt = TaskWorld.Start.AddMinutes(-1) }` → `MovementOccurredAtOutOfRange` (the task was created and
  released at `Start`).
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~AScanEarlierThanTheRelease|FullyQualifiedName~AScanOutsideTheWindowIs422BeforeTheLedger"`:
  fails (`Assert.Throws() Failure: No exception was thrown`). Save the output (RED).
- [ ] **GREEN.** In `ConfirmLine`, right after `LedgerEntryRules.EnsureOccurredAtInWindow(scannedAt, now);`:

  ```csharp
  // B2: un escaneo anterior a la publicación es imposible (sin publicar, a la creación). Mismo código que la ventana del ledger.
  if (scannedAt < (ReleasedAt ?? CreatedAt))
  {
      throw new WmsDomainException(
          WmsErrorCodes.MovementOccurredAtOutOfRange, "La fecha del escaneo no puede ser anterior a la publicación de la tarea.");
  }
  ```

  Add to the `occurredAt` `<param>` doc: «… y no anterior a la publicación de la tarea.» Update the comment on
  `ConfirmTaskLineValidator` («La ventana de `OccurredAt` … la aplican la tarea y el ledger»): «La ventana y el piso de
  `OccurredAt` no se miran aquí: …».
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] Commit: `fix(wms): reject a task scan earlier than its release` (paths: `WarehouseTask.cs`, `ConfirmTaskLine.cs` if the
  comment changed, and the two test files).

### Task A6: Spec update (fix 10)

**Files:**
- Modify: `docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md`

The spec records the whole approved contract, including what Block B builds (`staleAssignments`, `recorded_at`, indexes), so the
frontend and Android prompts can be written from it. Spanish (Colombian, tú). Insert the texts below; keep the rest unchanged.

- [ ] **§6.1, `wms.tasks` indexes** (paragraph «Índices y restricciones»): after `IX_tasks_source …;` add
  «`IX_tasks_board` `(tenant_id, warehouse_id, status, type)` (los conteos del tablero) e `IX_tasks_list` `(tenant_id,
  warehouse_id, created_at DESC, number DESC)` (el listado del supervisor), de la revisión de T1;».
- [ ] **§6.1, `wms.task_events`**: replace «`detail varchar(500)` null, `occurred_at`. Índice `(task_id, occurred_at)`.» with:
  «`detail varchar(500)` null, `occurred_at` (la hora del hecho: la del escaneo en lo que deja una confirmación, DA-36) y
  `recorded_at` (la hora del servidor al guardarlo). La historia se ordena por `recorded_at`, luego por tipo (orden causal del enum)
  e id: así una confirmación tardía aparece después del vencimiento que la precedió en el servidor, con su hora de escaneo intacta.
  `recorded_at` nace `NOT NULL DEFAULT now()` para que un pod viejo que no la escribe siga insertando, y la migración la llena con
  `occurred_at` en las filas existentes. Índice `(task_id, occurred_at)`.»
- [ ] **§6.7**, add after the «Reclamo» bullet:
  «- **Pedir otra vez la tarea en curso** (`next` o `claim` de quien ya la tiene `InProgress`): el servidor bloquea la fila y le
  renueva el lease con el mismo token, sin evento ni versión, como el latido. Vale también si el lease venció y el barrido todavía
  no pasó: nadie más pudo tomarla. Nunca devuelve un `leaseExpiresAt` vencido.»
- [ ] **§6.7, «Fencing»** bullet: replace the sentence after «409 `wms.task.lease_lost`» with «con la extensión `resumable`:
  `true` si el lease venció y nadie más tomó la tarea (la app la pide otra vez con `claim` y sigue; su cola vale si es el mismo
  celular, DA-34), `false` si la tiene otra persona, se la reasignaron, la retuvieron o ya terminó. Con `false` la app muestra
  «Esta tarea ya no es tuya».»
- [ ] **§6.7, «Confirmación tardía»** bullet, last sentence: «Una tarea retenida (`hold`, vuelve a `Planned`) rechaza las
  confirmaciones tardías (DA-35), y retenerla borra el reclamo que haya quedado del lease vencido: después de retener y publicar,
  una confirmación de antes ya no la reanuda.»
- [ ] **§6.7, «Soltar»** bullet: append «Repetir un `unclaim` que ya se aplicó (la app no recibió la respuesta) da 409
  `wms.task.lease_lost` con `resumable: false`: la app lo trata como éxito, porque la tarea ya no es suya.»
- [ ] **§6.8, step 2**: «Si el operario ya tiene una `InProgress` en esa bodega, se bloquea esa fila (`LockAsync`), se le renueva el
  lease (§6.7) y se devuelve: repetir «siguiente» es idempotente. Si la tiene en otra bodega, 409 `wms.task.member_busy` con `{
  taskId }` en las extensiones.»
- [ ] **§6.9, `occurredAt`** bullet: after «(`[now − 30 días, now + 5 min]`)» add «y no anterior a la publicación de la tarea
  (`released_at`, o `created_at` si nunca se publicó); un escaneo anterior es imposible y llega como 422
  `wms.movement.occurred_at_out_of_range`».
- [ ] **§6.10, «Tablero por bodega»**: append «; arriba también las **asignaciones huérfanas** (`staleAssignments`): tareas
  `Planned` o `Assigned` asignadas a alguien que ya no está activo o perdió el permiso del tipo. Nadie más puede reclamarlas, así
  que el supervisor las reasigna».
- [ ] **§6.11, DTO block**: `TaskBoardDto { warehouseId, byStatus: [{ status, count }], byType: [{ type, open }], operators: [{
  memberId, name, taskId, taskNumber, type, since, lastActivityAt }], staleAssignments: [{ taskId, taskNumber, memberId, name }] }`.
- [ ] **§6.11, bullets** after the `byStatus` one:
  «- `staleAssignments` viaja completa (sin paginar), en orden de número: la pantalla no puede saber si el asignado sigue activo ni
  qué permisos tiene (Tenancy no es suyo), y sin esta lista una tarea asignada a quien ya no puede hacerla espera para siempre.
  Se arma con una sola consulta de asignadas y una consulta de elegibles por permiso, no una por tarea.
  - `events` va en orden de `recorded_at` (hora del servidor), luego tipo e id; cada evento conserva su `occurredAt` (hora del
  escaneo). `recordedAt` no viaja: ninguna pantalla lo dibuja.»
- [ ] **§6.11, «Confirmar exige»**: after «lease válido (o reanudable, §6.7);» add «`occurredAt` dentro de la ventana del ledger y
  no anterior a la publicación (`wms.movement.occurred_at_out_of_range`);».
- [ ] **§6.11, error table**, row `wms.task.lease_lost`: «`leaseToken` que no es el vigente ni reanudable; extensiones `{
  resumable }` (§6.7)».
- [ ] **§12**, add to the integration list: «otro tenant → 403 en toda ruta de tareas (operario, supervisor y lecturas); misma
  clave con otro cuerpo al confirmar → 409; retener contra una confirmación tardía; reasignar en curso → 409 con `resumable:
  false`; lease vencido sin barrer: `next`/`claim` lo renuevan y el latido también; después del barrido, el latido da 409 con
  `resumable: true`; la historia en orden del servidor con la hora del escaneo; `staleAssignments` en el tablero».
- [ ] **New §13.1** (after the DA table, before §14):

  ```markdown
  ### 13.1 Revisión final de T1 y respuestas del owner (2026-11-26)

  **O-7.** El 2026-11-26 el owner delegó en el revisor final (Fable 5.2) las cuatro decisiones de arquitectura y contrato de T1:

  - El área `Tasks` en `WmsLayerTests` (DA-01): **aprobada**.
  - La entrada `Claimed` de `TaskLeaseSweeper` en `MultiReplicaInventory`: **aprobada**, con la cita corregida a `LockExpiredAsync`.
  - El permiso `wms.task.manage` (DA-15): **aprobado**; la prueba ancla de permisos se renombró (ya no lleva la cuenta en el
    nombre). Unas plantillas de rol que traigan este permiso más adelante (O-6) necesitan una autorización nueva.
  - El contrato de §6.11: **aprobado con cambios**: M2 (`next`/`claim` renuevan el lease de quien ya tiene la tarea en curso, y
    `wms.task.lease_lost` lleva `resumable`), B2 (`occurredAt` no anterior a la publicación) y la nota del `unclaim` repetido (409
    con `resumable: false`, que la app trata como éxito). M3 queda pendiente del owner.

  La misma revisión agregó, sin cambiar rutas ni códigos: `staleAssignments` en el tablero (M1), DTOs compuestos sólo con las
  ubicaciones de las líneas e índices del tablero y del listado (M4), `hold` que borra el reclamo vencido (B1) y `recorded_at` en
  `task_events` para ordenar la historia (B3).

  **Respuestas del owner del 2026-11-26** (entradas para los cortes siguientes; ninguna cambia T1):

  | Pregunta | Respuesta | Decisión que cambia | Corte |
  | --- | --- | --- | --- |
  | O-1 | La recepción del WMS suma stock y marca la orden de compra `Received` sola; el «Recibir» manual de Purchases queda sólo para los tenants que no usan recepción | Revierte DA-04 | T3 |
  | O-2 | De dónde salen los pedidos y si la liberación y la asignación son automáticas por reglas o manuales se configura en el motor de tareas | Revierte DA-05 | T5 |
  | O-3 | El pedido pasa a `Prepared` al cerrar el empaque | Confirma DA-06 | T7 |
  | O-4 | Despacho parcial por tenant, apagado por defecto; cross-docking sólo para faltantes, por bodega, apagado por defecto | Confirma DA-24 (con parámetro por tenant) y DA-23 | T7, T8 |
  | O-5 | El POS descuenta stock, a través del inventario básico | Revierte DA-28 | Por definir |
  | O-6 | Plantillas de rol de fábrica «Supervisor de bodega» y «Operario de bodega», también para los tenants existentes cuando se prende el módulo | Revierte DA-31 | Por definir (necesita autorización, ver O-7) |

  **Pendientes del owner** (no se implementan sin respuesta):

  - **M3.** ¿`claim` admite sólo las tareas asignadas a quien reclama y su propio reclamo vencido, o cualquier tarea del pool? Hoy
    admite cualquiera que esté en el pool (DA-09 sólo esconde el pool en la app).
  - **B4.** ¿Un reclamo tiene una edad máxima, aunque siga latiendo? Hoy un operario que deja la app abierta la conserva sin límite.
  - **B5.** ¿La numeración diaria `TSK-yyyyMMdd-NNNN` corta el día en hora de Colombia? Hoy corta en UTC.
  ```

- [ ] **§13 DA table**: append to the «Decisión del agente» cell of DA-04, DA-05, DA-28 and DA-31 « **Revertida por el owner el
  2026-11-26 (§13.1).**»; of DA-06, DA-23 and DA-24 « **Confirmada por el owner el 2026-11-26 (§13.1).**»; of DA-01 and DA-15 «
  **Aprobada (O-7, §13.1).**».
- [ ] **§14**: replace the list O-1 … O-7 with «O-1 a O-7 se contestaron el 2026-11-26: ver §13.1. Siguen abiertas M3, B4 y B5
  (§13.1).»
- [ ] Re-read the edited sections once for voseo (must be tú) and for references that still say «por confirmar» about O-1…O-7.
- [ ] Commit: `docs(wms): record the T1 review decisions and owner answers in the task engine spec` (path: the spec).

### Block A verification (once, at the end of the block)

- [ ] Unit: `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] Architecture: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~CompositionRootTests"`:
  green.
- [ ] Integration (filtered; check `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` first and wait if busy):

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~NextReturnsTheTaskClaimedWithALeaseTokenAndRepeatingItIsTheSame|FullyQualifiedName~TheSameOperatorTappingTwiceGetsOneTask|FullyQualifiedName~ClaimingASecondTaskIs409MemberBusyWithTheCurrentOne|FullyQualifiedName~AConfirmationWithAStaleTokenIs409LeaseLost|FullyQualifiedName~ALateConfirmationResumesTheTask|FullyQualifiedName~UnclaimReturnsTheTaskToThePool|FullyQualifiedName~HoldThenPublishRoundTrips" -- xUnit.MaxParallelThreads=4
  ```

  Expected: green. `NextReturnsTheTaskClaimed…` still asserts the same token **and version** on the second `next` (the renewal
  does not bump `version`).
- [ ] Hand-off note for Block B: the commit SHAs, and confirm `WarehouseTaskRepository.cs` is untouched
  (`git diff --stat 8f486325a -- src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs` prints nothing).

---

# Block B — infrastructure, board, history and tests (fixes 4, 5, 8, 9)

One implementer, after Block A is committed. Order: B1 → B2 → B3 → B4 → B5. **New methods in `WarehouseTaskRepository.cs` go
after `NextNumberAsync` (before the private helpers)** so the `LockExpiredAsync` citation (201-216) does not move.

### Task B1: orphaned assignments on the board (M1)

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs` (record `AssignedTaskRef`, method
  `ListAssignedOpenAsync`)
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskDtos.cs` (`TaskStaleAssignmentDto`, `TaskBoardDto.StaleAssignments`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/GetTaskBoard.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/SupervisorTaskActionsTests.cs`, `TaskWorld.Writes.cs`, `TaskWorld.Reads.cs`;
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskSupervisorActionsApiTests.cs`

Scope (decided here, see «Agent decisions»): tasks in `Planned` **or** `Assigned` with `assigned_member_id` set, in the requested
warehouse, whose assignee is not in the active-with-permission set for the task type's permission. `InProgress` is excluded: the
lease already handles it. One query for the assigned tasks, **one `ListWithPermissionAsync` per distinct permission** (bounded by
the number of task types, never per task), and the names go in the same single `FindNamesAsync` call as `operators`.

- [ ] **RED (unit).** In `TaskWorld.Writes.cs`, add `public int PermissionLookups { get; set; }` to the world; in `FakeMembers`
  (`TaskWorld.Reads.cs`), increment it in `ListWithPermissionAsync`. In `FakeTasks`, implement:

  ```csharp
  public Task<IReadOnlyList<AssignedTaskRef>> ListAssignedOpenAsync(
      Guid tenantId, Guid warehouseId, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<AssignedTaskRef>>(
      [
          .. world.Tasks
              .Where(task => task.Status is WarehouseTaskStatus.Planned or WarehouseTaskStatus.Assigned
                  && task.AssignedMemberId is not null)
              .OrderBy(task => task.Number, StringComparer.Ordinal)
              .Select(task => new AssignedTaskRef(task.Id, task.Number, task.Type, task.AssignedMemberId!.Value)),
      ]);
  ```

  In `SupervisorTaskActionsTests`:

  ```csharp
  // M1: una asignada a quien ya no puede hacerla no la reclama nadie; el tablero la muestra completa y con nombre.
  [Fact]
  public async Task TheBoardListsAssignmentsTheAssigneeCanNoLongerPerform()
  {
      var world = new TaskWorld();
      world.Names[TaskWorld.OtherMember] = "Luis Exoperario";
      var assigned = world.AddTask(assignTo: TaskWorld.OtherMember);
      var planned = world.AddTask(publish: false, assignTo: TaskWorld.OtherMember);
      world.AddTask(assignTo: TaskWorld.Member);
      world.AddTask();
      var handler = new GetTaskBoardHandler(
          world.Context(), world.Authorization, world.Warehouses(), world.Repository(), world.Members());

      var board = await handler.HandleAsync(new GetTaskBoardQuery(TaskWorld.Tenant, world.Warehouse.Id.Value), Ct);

      Assert.Equal(
          [(assigned.Id, assigned.Number, TaskWorld.OtherMember, "Luis Exoperario"),
           (planned.Id, planned.Number, TaskWorld.OtherMember, "Luis Exoperario")],
          board.StaleAssignments.Select(item => (item.TaskId, item.TaskNumber, item.MemberId, item.Name)));
      // Una consulta de elegibles por permiso, no una por tarea.
      Assert.Equal(1, world.PermissionLookups);
  }
  ```

  Also assert in `TheBoardListsEveryStatusAndTypeEvenAtZero` that `board.StaleAssignments` is empty.
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: fails with `CS0246`/`CS1061` (`AssignedTaskRef`,
  `StaleAssignments`, `ListAssignedOpenAsync` missing). Save the output (RED).
- [ ] **GREEN.** In `IWarehouseTaskRepository.cs`:

  ```csharp
  /// <summary>Una tarea abierta con asignado, lo justo para el tablero.</summary>
  public sealed record AssignedTaskRef(Guid TaskId, string Number, WarehouseTaskType Type, Guid AssignedMemberId);

  /// <summary>Las <c>Planned</c> y <c>Assigned</c> de la bodega que tienen asignado, sin rastreo y en orden de número. Todas: el
  /// tablero decide cuáles quedaron huérfanas.</summary>
  Task<IReadOnlyList<AssignedTaskRef>> ListAssignedOpenAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);
  ```

  In `WarehouseTaskRepository` (after `NextNumberAsync`):

  ```csharp
  public async Task<IReadOnlyList<AssignedTaskRef>> ListAssignedOpenAsync(
      Guid tenantId, Guid warehouseId, CancellationToken cancellationToken)
  {
      var id = new WarehouseId(warehouseId);
      return await OfTenant(tenantId)
          .Where(task => task.WarehouseId == id
              && (task.Status == WarehouseTaskStatus.Planned || task.Status == WarehouseTaskStatus.Assigned)
              && task.AssignedMemberId != null)
          .OrderBy(task => task.Number)
          .Select(task => new AssignedTaskRef(task.Id, task.Number, task.Type, task.AssignedMemberId!.Value))
          .ToListAsync(cancellationToken);
  }
  ```

  In `TaskDtos.cs`:

  ```csharp
  /// <summary>Una tarea abierta asignada a alguien que ya no está activo o perdió el permiso del tipo (BFF, revisión T1 M1). La
  /// pantalla no puede saberlo: no conoce Tenancy ni los permisos de nadie, y sin esta lista la tarea espera para siempre a una persona
  /// que no puede reclamarla. Viene completa, sin paginar, en orden de número, para que el supervisor la reasigne.</summary>
  public sealed record TaskStaleAssignmentDto(Guid TaskId, string TaskNumber, Guid MemberId, string Name);
  ```

  and add `IReadOnlyList<TaskStaleAssignmentDto> StaleAssignments` as the **last** parameter of `TaskBoardDto` (extend its
  `<summary>` with «`StaleAssignments` viene completa: ver `TaskStaleAssignmentDto`.»).

  In `GetTaskBoardHandler`, after `working`/`claimedAt`:

  ```csharp
  // M1: una consulta de asignadas y una de elegibles por permiso (tantas como tipos distintos, nunca una por tarea).
  var assigned = await tasks.ListAssignedOpenAsync(query.TenantId, query.WarehouseId, cancellationToken);
  var eligible = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
  foreach (var permission in assigned.Select(task => TaskTypePermissions.For(task.Type)).Distinct(StringComparer.Ordinal))
  {
      eligible[permission] = [.. await members.ListWithPermissionAsync(query.TenantId, permission, cancellationToken)];
  }

  var stale = assigned
      .Where(task => !eligible[TaskTypePermissions.For(task.Type)].Contains(task.AssignedMemberId))
      .ToList();
  var names = await members.FindNamesAsync(
      query.TenantId,
      [.. working.Select(task => task.ClaimedByMemberId!.Value).Concat(stale.Select(task => task.AssignedMemberId)).Distinct()],
      cancellationToken);
  ```

  (this `names` replaces the existing call) and pass as last argument:

  ```csharp
  [
      .. stale.Select(task => new TaskStaleAssignmentDto(
          task.TaskId, task.Number, task.AssignedMemberId, names.GetValueOrDefault(task.AssignedMemberId) ?? "Sin nombre")),
  ]
  ```

  Keep the order of the repository (by number).
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] **Integration** (no RED of its own: it pins the real directory adapter; it must be green once the code above exists). In
  `TaskSupervisorActionsApiTests`:

  ```csharp
  [Fact]
  public async Task TheBoardListsAssignmentsTheAssigneeCanNoLongerPerform()
  {
      var (database, factory) = await OpenAsync();
      await using var _ = database;
      using var __ = factory;
      var s = await ArrangeAsync(database, factory);
      var destination = await CreateDestinationAsync(s);
      var (worker, client) = await SeedOperatorAsync(factory);
      var (advisor, advisorClient) = await SeedOperatorAsync(factory, SecondOperatorSubject, ["advisor"]);
      using var _w = client;
      using var _a = advisorClient;
      // Sembradas por el dominio: la API ya no deja asignar a quien no tiene el permiso, que es justo el caso de perderlo después.
      var orphan = NewMove(s, s.BinA, destination, assignTo: advisor);
      await SaveAsync(factory, orphan, NewMove(s, s.BinA, destination, assignTo: worker));

      var board = await s.Manager.GetFromJsonAsync<TaskBoardDto>($"{TasksUrl()}/summary?warehouseId={s.WarehouseId}", Ct);

      Assert.NotNull(board);
      var stale = Assert.Single(board.StaleAssignments);
      Assert.Equal((orphan.Id, orphan.Number, advisor, "Caller"), (stale.TaskId, stale.TaskNumber, stale.MemberId, stale.Name));
  }
  ```

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TheBoardListsAssignmentsTheAssigneeCanNoLongerPerform|FullyQualifiedName~TheBoardListsEveryStatusEvenAtZeroAndWhoIsWorking" -- xUnit.MaxParallelThreads=4
  ```

  Green. (`SeedCallerMembershipAsync` names every membership «Caller».)
- [ ] Commit: `feat(wms): list orphaned task assignments on the board` (paths: the four source files and the four test files).

### Task B2: compose DTOs from the referenced locations only, and index the board and the list (M4)

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` (`IWarehouseRepository.FindLocationCodesAsync`)
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/WarehouseTaskDtoComposer.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs`
- Create (generated): `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<timestamp>_AddTaskBoardIndexes.cs`,
  `.Designer.cs`; Modify (generated): `WmsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskDtoComposerTests.cs`, `TaskWorld.Reads.cs`, `TaskWorld.cs`;
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/WarehouseTaskPersistenceTests.cs`

Why: `ConfirmTaskLine` composes the `TaskDto` after posting to the ledger, while the task row and the balances are locked; today
that composition loads the whole warehouse **tracked** (`warehouses.GetAsync`), and `ComposeSummariesAsync` loads every warehouse
of the tenant with all its locations. After this task the composer reads only the locations its lines reference, untracked, and
the summaries read only the first source location of each task in the page.

Indexes (checked against the real queries): `CountByStatusAsync` filters `tenant_id`, `warehouse_id` and groups by `status`;
`CountOpenByTypeAsync` filters `tenant_id`, `warehouse_id`, `status NOT IN ('Done','Cancelled')` and groups by `type`;
`ListAsync` filters `tenant_id` (+ `warehouse_id`, optional `status`/`type`/`assigned_member_id`) and orders `created_at DESC,
number DESC`. So: `IX_tasks_board (tenant_id, warehouse_id, status, type)` serves both counts (and B1's list) from the index;
`IX_tasks_list (tenant_id, warehouse_id, created_at DESC, number DESC)` serves the page in order. **Nothing is dropped:**
`IX_tasks_pool` stays (smaller partial index on the hot `next` path; the new one also carries `Done`/`Cancelled` history) and
`IX_tasks_warehouse_id` stays (FK convention index on `warehouse_id`; the new indexes lead with `tenant_id`, so EF keeps it and
the `RESTRICT` FK check on a warehouse delete keeps its index). Adding indexes is expand-only.

- [ ] **RED (unit).** In `TaskWorld.cs` add `public List<IReadOnlyCollection<LocationId>> LocationReads { get; } = [];`. In
  `FakeWarehouses` (`TaskWorld.Reads.cs`): make `GetAsync` and `ListAsync` add `"warehouse-tree"` to `world.Log` before
  returning, and implement:

  ```csharp
  public Task<IReadOnlyDictionary<LocationId, string>> FindLocationCodesAsync(
      Guid tenantId, IReadOnlyCollection<LocationId> ids, CancellationToken ct)
  {
      world.LocationReads.Add(ids);
      return Task.FromResult<IReadOnlyDictionary<LocationId, string>>(
          tenantId == Tenant
              ? world.Warehouse.Locations.Where(location => ids.Contains(location.Id)).ToDictionary(l => l.Id, l => l.Code)
              : new Dictionary<LocationId, string>());
  }
  ```

  In `WarehouseTaskDtoComposerTests`:

  ```csharp
  // M4: la composición corre con la tarea y los saldos bloqueados: sólo lee las ubicaciones de sus líneas, sin la bodega entera.
  [Fact]
  public async Task TheDetailReadsOnlyTheLocationsOfItsLines()
  {
      var world = new TaskWorld();
      var task = world.AddTask();

      var dto = await world.Composer().ComposeAsync(task, null, withEvents: false, Ct);

      Assert.Equal(("A-01-01", "A-01-02"), (dto.Lines[0].FromLocationCode, dto.Lines[0].ToLocationCode));
      // LocationId no es comparable: se compara como conjunto.
      var read = Assert.Single(world.LocationReads);
      Assert.Equal(2, read.Count);
      Assert.True(new HashSet<LocationId> { world.From, world.To }.SetEquals(read));
      Assert.DoesNotContain("warehouse-tree", world.Log);
  }

  [Fact]
  public async Task SummariesReadOnlyTheFirstSourceOfEachTask()
  {
      var world = new TaskWorld();
      var task = world.AddTask(lines: [world.Line(), WarehouseTaskLine.Create(
          TaskWorld.Variant, null, StockStatus.Available, world.Spare, world.To, 1)]);

      var dto = Assert.Single(await world.Composer().ComposeSummariesAsync(TaskWorld.Tenant, [task], Ct));

      Assert.Equal("A-01-01", dto.FirstFromLocationCode);
      Assert.Equal([world.From], Assert.Single(world.LocationReads).ToArray());
      Assert.DoesNotContain("warehouse-tree", world.Log);
  }
  ```

- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: fails with `CS0535`/`CS1061` (`FindLocationCodesAsync` not on
  `IWarehouseRepository`). Save the output (RED).
- [ ] **GREEN (port + adapter).** In `IWarehouseRepository` (`WmsPorts.cs`):

  ```csharp
  /// <summary>El código de cada ubicación pedida del tenant, sin rastreo y sin cargar la bodega: para componer respuestas que corren
  /// con filas bloqueadas. La que no existe en el tenant no viene.</summary>
  Task<IReadOnlyDictionary<LocationId, string>> FindLocationCodesAsync(
      Guid tenantId, IReadOnlyCollection<LocationId> ids, CancellationToken ct);
  ```

  In `WarehouseRepository`:

  ```csharp
  public async Task<IReadOnlyDictionary<LocationId, string>> FindLocationCodesAsync(
      Guid tenantId, IReadOnlyCollection<LocationId> ids, CancellationToken ct)
  {
      if (ids.Count == 0)
      {
          return new Dictionary<LocationId, string>();
      }

      var wanted = ids.Distinct().ToArray();
      return await dbContext.Locations
          .AsNoTracking()
          .Where(location => location.TenantId == tenantId && wanted.Contains(location.Id))
          .ToDictionaryAsync(location => location.Id, location => location.Code, ct);
  }
  ```

- [ ] **GREEN (composer).** In `WarehouseTaskDtoComposer`:
  - `ComposeAsync`: replace the `warehouses.GetAsync` call with
    `var codes = await warehouses.FindLocationCodesAsync(task.TenantId, [.. task.Lines.SelectMany(LocationsOf).Distinct()], cancellationToken);`
    where `private static IEnumerable<LocationId> LocationsOf(WarehouseTaskLine line)` yields `FromLocationId` and, if not null,
    `ToLocationId`. Line codes use `CodeOf(codes, …)`.
  - `ComposeSummariesAsync`: replace `warehouses.ListAsync` with
    `var codes = await warehouses.FindLocationCodesAsync(tenantId, [.. tasks.Select(FirstLine).OfType<WarehouseTaskLine>().Select(line => line.FromLocationId).Distinct()], cancellationToken);`
    with `private static WarehouseTaskLine? FirstLine(WarehouseTask task) => task.Lines.MinBy(line => line.Position);`.
  - `ToSummary(WarehouseTask task, IReadOnlyDictionary<LocationId, string> codes, IReadOnlyDictionary<Guid, string> names,
    DateTimeOffset now)` uses `FirstLine(task)` and `CodeOf(codes, first.FromLocationId)`.
  - `private static string CodeOf(IReadOnlyDictionary<LocationId, string> codes, LocationId id) => codes.GetValueOrDefault(id) ?? string.Empty;`
  - The `WmsNotFound.Warehouse` throws disappear (the FK guarantees the warehouse). Update the class `<summary>`: «los códigos de
    ubicación salen sólo de las ubicaciones que las líneas nombran (sin la bodega entera ni rastreo: la confirmación compone con la
    tarea y los saldos bloqueados) …». Remove the `Warehouse` using if unused.
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] **Indexes.** In `WarehouseTaskModel.ConfigureTask`, after `IX_tasks_source`:

  ```csharp
  // Los conteos del tablero (por estado y abiertas por tipo) y las asignadas, sin tocar la tabla (revisión T1, M4).
  task.HasIndex(value => new { value.TenantId, value.WarehouseId, value.Status, value.Type }, "IX_tasks_board");
  // El listado del supervisor: la página sale del índice en el orden de ListAsync.
  task.HasIndex(value => new { value.TenantId, value.WarehouseId, value.CreatedAt, value.Number }, "IX_tasks_list")
      .IsDescending(false, false, true, true);
  ```

- [ ] Generate the migration:

  ```powershell
  dotnet ef migrations add AddTaskBoardIndexes --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
  ```

  Open the generated `.cs`: `Up` must contain **only** two `CreateIndex` (`IX_tasks_board` on `tenant_id, warehouse_id, status,
  type`; `IX_tasks_list` on `tenant_id, warehouse_id, created_at, number` with `descending: new[] { false, false, true, true }`),
  and `Down` only the two `DropIndex`. If anything else appears (a dropped `IX_tasks_warehouse_id`, a column change), stop and
  find out why before continuing.
- [ ] **Integration** in `WarehouseTaskPersistenceTests` (the adapter and the indexes against the real schema):

  ```csharp
  [Fact]
  public async Task LocationCodesComeOnlyFromTheTenantAndTheIndexesExist()
  {
      var (database, factory) = await OpenAsync();
      await using var _ = database;
      using var __ = factory;
      var s = await ArrangeAsync(database, factory);
      await using var scope = factory.Services.CreateAsyncScope();
      var warehouses = scope.ServiceProvider.GetRequiredService<IWarehouseRepository>();

      var mine = await warehouses.FindLocationCodesAsync(s.TenantId, [new LocationId(s.BinA)], Ct);
      var foreign = await warehouses.FindLocationCodesAsync(Guid.CreateVersion7(), [new LocationId(s.BinA)], Ct);

      Assert.Single(mine);
      Assert.Empty(foreign);
      Assert.Equal(2L, await ScalarAsync<long>(
          database,
          "SELECT count(*) FROM pg_indexes WHERE schemaname = 'wms' AND indexname IN ('IX_tasks_board', 'IX_tasks_list')"));
  }
  ```

  (Use the file's existing `Ct` accessor and usings; add `Modules.Wms.Domain.Warehousing` if missing.)

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~LocationCodesComeOnlyFromTheTenantAndTheIndexesExist|FullyQualifiedName~ConfirmingMovesTheStock|FullyQualifiedName~ListingFiltersByStatusAndPages|FullyQualifiedName~MineShowsTheCurrentAndTheAssigned" -- xUnit.MaxParallelThreads=4
  ```

  Green.
- [ ] Commit: `perf(wms): compose task DTOs from their own locations and index the board and the list` (paths: the four source
  files, `WarehouseTaskModel.cs`, the three generated migration files, the four test files).

### Task B3: server `recorded_at` for the task history (B3)

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTaskEvent.cs`, `WarehouseTask.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs` (`ConfigureEvent`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/WarehouseTaskDtoComposer.cs` (history order),
  `IWarehouseTaskRepository.cs` (doc of `GetWithEventsAsync`)
- Create (generated): `…/Migrations/<timestamp>_AddTaskEventRecordedAt.cs`, `.Designer.cs`; Modify: `WmsDbContextModelSnapshot.cs`
- Modify: `.claude/rules/wms.md` (one bullet)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskLeaseTests.cs`, `WarehouseTaskDtoComposerTests.cs`;
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskTestSupport.cs` (`History`), new
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskEventRecordedAtMigrationTests.cs`,
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskOperatorApiTests.cs`

Rules: every event gets `RecordedAt` = server `now` of the transition; `OccurredAt` keeps its meaning (scan time for the facts of
a confirmation, DA-36; server time otherwise). The history is ordered by **`RecordedAt`, then `Kind` (causal enum order), then
`Id`** — `Kind` stays before `Id` because the facts of one transaction share `RecordedAt` and `Guid.CreateVersion7()` is not
monotonic within a millisecond (see «Agent decisions»). `recordedAt` is **not** added to `TaskEventDto` (no screen needs it).
Migration is expand-only: `recorded_at timestamptz NOT NULL DEFAULT now()` (an old pod that does not write it keeps inserting),
backfilled from `occurred_at`. `IX_task_events_task (task_id, occurred_at)` stays (the history load and the skip sub-query use its
`task_id` prefix; ordering happens in memory).

- [ ] **RED (domain).** In `WarehouseTaskLeaseTests`, extend `ALateSyncKeepsTheScanTimeInTheHistory`: inside the `Assert.All`, add
  `Assert.Equal(synced, e.RecordedAt);`. Add:

  ```csharp
  [Fact]
  public void EveryOtherEventIsRecordedAtItsOwnTime()
  {
      var (task, _) = Claimed();

      Assert.All(task.Events, e => Assert.Equal(e.OccurredAt, e.RecordedAt));
  }
  ```

- [ ] **RED (composer).** In `WarehouseTaskDtoComposerTests`:

  ```csharp
  // B3: la historia sigue el orden del servidor; la confirmación tardía conserva su hora de escaneo.
  [Fact]
  public async Task ALateConfirmationComesAfterTheExpiryKeepingItsScanTime()
  {
      var world = new TaskWorld();
      var task = world.AddTask();
      task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start.AddMinutes(1));
      var token = task.LeaseToken!.Value;
      task.ExpireLease(TaskWorld.Start.AddMinutes(20));
      var scanned = TaskWorld.Start.AddMinutes(5);
      task.ConfirmLine(TaskWorld.Member, token, TaskWorld.Device, task.Lines[0].Id, 5, TaskWorld.Start.AddMinutes(25), scanned);

      var dto = await world.Composer().ComposeAsync(task, null, withEvents: true, Ct);

      Assert.Equal(
          ["Created", "Published", "Claimed", "LeaseExpired", "LeaseResumed", "LineConfirmed", "Completed"],
          dto.Events.Select(value => value.Kind));
      Assert.Equal(scanned, dto.Events[4].OccurredAt);
  }
  ```

- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: fails with `CS1061` (`WarehouseTaskEvent` has no `RecordedAt`). Save
  the output (RED). After adding only the property (next step, first bullet), run the composer test once to record its assertion
  failure (`LeaseResumed` before `LeaseExpired` under the old `OccurredAt` order).
- [ ] **GREEN (domain).**
  - `WarehouseTaskEvent`: add `/// <summary>Cuándo lo guardó el servidor; la historia se ordena por esta fecha (revisión T1, B3).
    <see cref="OccurredAt"/> es la del hecho.</summary> public DateTimeOffset RecordedAt { get; private set; }`; `Record(...)` gains
    a last parameter `DateTimeOffset recordedAt` and sets `RecordedAt = recordedAt.ToUniversalTime()`. Its only caller is
    `WarehouseTask.Record` (check with `Select-String -Path src,tests -Pattern "WarehouseTaskEvent.Record(" -SimpleMatch` over the
    `.cs` files).
  - `WarehouseTask.Record` gains `DateTimeOffset? recordedAt = null` (last optional parameter) and passes `recordedAt ?? at`.
  - Pass the server `now` where `at` is not the server time: in `ConfirmLine`, `Record(TaskEventKind.LeaseResumed, …,
    recordedAt: now)` and `Record(TaskEventKind.LineConfirmed, …, recordedAt: now)`; `CloseIfFinished(Guid by, DateTimeOffset at,
    DateTimeOffset recordedAt, string? cancelReason = null)` and `MarkCancelled(string reason, Guid by, DateTimeOffset at,
    DateTimeOffset recordedAt)` forward it to their `Record` calls. Call sites: `ConfirmLine` → `CloseIfFinished(memberId,
    scannedAt, now)`; `CancelLine` → `CloseIfFinished(by, now, now, detail)`; `Cancel` → `MarkCancelled(detail, by, now, now)`.
    `CompletedAt` stays `scannedAt` (DA-36).
- [ ] **GREEN (composer).** History order: `task.Events.OrderBy(value => value.RecordedAt).ThenBy(value => value.Kind).ThenBy(value
  => value.Id)`, with the comment: «La historia sigue el orden del servidor (`RecordedAt`); cada evento conserva su `OccurredAt`.
  Los hechos de una transacción comparten fecha y un UUID v7 no ordena dentro del mismo milisegundo: dentro de una fecha manda el
  tipo, que el enum declara en orden causal.» Update the `<summary>` of `IWarehouseTaskRepository.GetWithEventsAsync` the same way
  («… quien los muestra ordena por `recorded_at`, tipo e id»). In `TaskTestSupport.History` use the same order and comment.
- [ ] Run `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Modules.Wms.UnitTests.Tasks"`: green.
- [ ] **Model + migration.** In `ConfigureEvent`:

  ```csharp
  // El default sólo existe para el rollout: un pod viejo inserta sin la columna. El código nuevo siempre la escribe.
  history.Property(value => value.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
  ```

  ```powershell
  dotnet ef migrations add AddTaskEventRecordedAt --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
  ```

  The generated `Up` must be a single `AddColumn<DateTimeOffset>(name: "recorded_at", schema: "wms", table: "task_events", type:
  "timestamp with time zone", nullable: false, defaultValueSql: "now()")`. Add right after it, by hand:

  ```csharp
  // Las filas de antes no tienen otra hora que la del hecho: se llenan con ella. Expand-only: el pod viejo no la escribe y cae en el
  // default.
  migrationBuilder.Sql("UPDATE wms.task_events SET recorded_at = occurred_at;");
  ```

  `Down`: only the generated `DropColumn`. Nothing else in `Up` (no index change).
- [ ] **Migration test** (new file `Tasks/TaskEventRecordedAtMigrationTests.cs`; own host, it migrates down and back up, so never
  the shared host):

  ```csharp
  public sealed class TaskEventRecordedAtMigrationTests
  {
      private static CancellationToken Ct => TestContext.Current.CancellationToken;

      [Fact]
      public async Task TheMigrationBackfillsRecordedAtAndAnOldPodCanStillInsert()
      {
          var (database, factory) = await OpenAsync();
          await using var _ = database;
          using var __ = factory;
          var s = await ArrangeAsync(database, factory);
          var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]));
          var options = new DbContextOptionsBuilder<WmsDbContext>()
              .UseNpgsql(database.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WmsDbContext.Schema))
              .Options;
          await using var context = new WmsDbContext(options);
          var migrator = context.GetService<IMigrator>();
          var before = context.Database.GetMigrations().Single(m => m.EndsWith("_AddTaskBoardIndexes", StringComparison.Ordinal));

          await migrator.MigrateAsync(before, Ct);
          await migrator.MigrateAsync(null, Ct);

          Assert.True(await ScalarAsync<long>(database, "SELECT count(*) FROM wms.task_events WHERE task_id = @id", ("id", task.Id)) > 0);
          Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.task_events WHERE recorded_at <> occurred_at"));
          // Un pod viejo no conoce la columna: inserta sin ella y cae en el default.
          await ExecuteAsync(
              database,
              "INSERT INTO wms.task_events (id, tenant_id, task_id, kind, occurred_at) VALUES (@id, @tenant, @task, 'PriorityChanged', now())",
              ("id", Guid.CreateVersion7()), ("tenant", s.TenantId), ("task", task.Id));
          Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.task_events WHERE recorded_at IS NULL"));
      }
  }
  ```

  Usings as in `InventoryLedgerMigrationTests` plus `TaskApiSupport`/`WmsApiHarness` statics. Check the real `NOT NULL` columns
  of `task_events` in the `AddWarehouseTasks` migration before running; add any that the `INSERT` misses.
- [ ] **API test** in `TaskOperatorApiTests` (the order through the real stack; the lease is made to expire by SQL so the sweep
  runs at the real `now`):

  ```csharp
  // B3: la historia va en el orden del servidor y la confirmación tardía conserva su hora de escaneo.
  [Fact]
  public async Task TheHistoryFollowsTheServerOrderAndKeepsTheScanTime()
  {
      var (database, factory) = await OpenAsync();
      await using var _ = database;
      using var __ = factory;
      var floor = await ArrangeFloorAsync(database, factory);
      using var _c = floor.Client;
      var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
      var scanned = DateTimeOffset.UtcNow;
      await ExecuteAsync(database, "UPDATE wms.tasks SET lease_expires_at = now() - interval '1 minute' WHERE id = @id", ("id", task.Id));
      var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
      await sweeper.RunOnceAsync(DateTimeOffset.UtcNow, Ct);

      var response = await ConfirmRawAsync(
          floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 4, occurredAt: scanned));

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      var events = (await GetAsync(floor.Setup.Manager, task.Id)).Events;
      Assert.Equal(
          ["Created", "Published", "Claimed", "LeaseExpired", "LeaseResumed", "LineConfirmed", "Completed"],
          events.Select(value => value.Kind));
      Assert.True(events[4].OccurredAt < events[3].OccurredAt);
  }
  ```

- [ ] In `.claude/rules/wms.md`, add a bullet after the «El barrido conserva…» one: «**La historia de una tarea se ordena por
  `recorded_at` (servidor), luego por tipo e id; `occurred_at` es la hora del escaneo** (DA-36, revisión T1 B3). No ordenes por
  `occurred_at`: una confirmación tardía quedaría antes del vencimiento que la precedió. Y no quites el tipo del desempate: los
  hechos de una transacción comparten fecha y un UUID v7 no ordena dentro del mismo milisegundo.»
- [ ] Run:

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskEventRecordedAtMigrationTests|FullyQualifiedName~TheHistoryFollowsTheServerOrderAndKeepsTheScanTime|FullyQualifiedName~ALateConfirmationResumesTheTask|FullyQualifiedName~ATaskRoundTripsWithItsLinesAndEvents|FullyQualifiedName~GettingATaskIncludesItsEvents" -- xUnit.MaxParallelThreads=4
  ```

  Green.
- [ ] Commit: `feat(wms): order task history by server time keeping the scan time` (paths: domain, model, composer, repository
  interface, the three generated migration files, `wms.md`, and the test files).

### Task B4: missing integration tests (B6)

**Files:**
- Modify: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskApiSupport.cs` (helper `ResumableOfAsync`)
- Modify: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskOperatorApiTests.cs`
- Create: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskTenantIsolationApiTests.cs`

These pin, through the real stack, behavior whose code landed in Block A (M2, `resumable`, B1) or that already existed (tenant
isolation, idempotency). **They are expected to pass on the first run**; the RED evidence for the fixes is the unit RED of Block A.
The only compile RED here is the new helper: `dotnet build tests/Modules/Wms/Modules.Wms.IntegrationTests` with the tests written
and the helper missing fails with `CS0103` (`ResumableOfAsync` does not exist); save that output, then add the helper.

- [ ] Helper in `TaskApiSupport`:

  ```csharp
  /// <summary>La extensión <c>resumable</c> de un 409 <c>wms.task.lease_lost</c> (§6.7).</summary>
  public static async Task<bool> ResumableOfAsync(HttpResponseMessage response)
  {
      using var document = JsonDocument.Parse(await BodyAsync(response));
      return document.RootElement.GetProperty("resumable").GetBoolean();
  }
  ```

- [ ] `TaskOperatorApiTests` — extend and add (all with their own host, `OpenAsync`, like the rest of the class):
  - `AConfirmationWithAStaleTokenIs409LeaseLost` (reassign while `InProgress`): add `Assert.False(await ResumableOfAsync(response));`.
  - `TheSameKeyWithAnotherBodyOnConfirmIs409`: `next`; confirm 2 with key `k` → 200; confirm 3 with the same `k` → 409,
    `wms.idempotency_key_reused`; `TaskMovementsAsync` == 1; the task shows `QuantityConfirmed == 2`.
  - `AHeldAndRepublishedTaskRejectsALateConfirmation`: `next` (token); `sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16),
    Ct)`; `var held = await ActionAsync(floor.Setup.Manager, task.Id, "hold", new { version = (await GetAsync(floor.Setup.Manager,
    task.Id)).Version })`; `var published = await ActionAsync(…, "publish", new { version = held.Version })`; confirm with the old
    token → 409 `wms.task.lease_lost`, `resumable == false`, 0 task movements; the supervisor GET shows `Status == "Ready"` and
    `ClaimedByMemberId == null`.
  - `NextAfterTheLeaseExpiredWithoutASweepRenewsItWithTheSameToken`: `next` → `first`; `ExecuteAsync(database, "UPDATE wms.tasks
    SET lease_expires_at = now() - interval '1 minute' WHERE id = @id", ("id", first.Id))`; `next` again → same `Id`,
    `LeaseToken` and `Version`, and `LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10)`.
  - `ClaimingMyOwnExpiredTaskWithoutASweepRenewsTheLease`: same, but the second call is
    `ActionRawAsync(floor.Client, first.Id, "claim", new { deviceId = "zebra-01" })` → 200, same token and version, renewed
    `LeaseExpiresAt`.
  - `AHeartbeatAfterTheLeaseExpiredWithoutASweepRenewsIt`: same SQL; heartbeat with the token → 200 and `LeaseExpiresAt >
    DateTimeOffset.UtcNow.AddMinutes(10)` (pins today's behavior: `CheckLease` is `Valid` while the task is `InProgress`).
  - `AHeartbeatAfterTheSweepIs409Resumable`: same SQL; `sweeper.RunOnceAsync(DateTimeOffset.UtcNow, Ct)`; heartbeat → 409
    `wms.task.lease_lost` with `resumable == true`; then `claim` from the same device → 200 with the **same** token (DA-34).
- [ ] `TaskTenantIsolationApiTests` — one `[Fact]` (one container) that loops over every task route with a stranger client and
  collects failures, so one run reports all of them:

  ```csharp
  /// <summary>Otro tenant nunca ve ni toca las tareas: 403 en toda ruta, nunca 404 (un 404 confirmaría que el id existe).</summary>
  public sealed class TaskTenantIsolationApiTests
  {
      private static CancellationToken Ct => TestContext.Current.CancellationToken;

      [Fact]
      public async Task AnotherTenantGets403OnEveryTaskRoute()
      {
          var (database, factory) = await OpenAsync();
          await using var _ = database;
          using var __ = factory;
          var s = await ArrangeAsync(database, factory);
          var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]));
          var line = task.Lines[0].Id;
          var token = Guid.NewGuid();
          using var stranger = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);
          var url = TasksUrl();
          var w = s.WarehouseId;
          (HttpMethod Method, string Path, object? Body)[] routes =
          [
              (HttpMethod.Get, $"{url}?warehouseId={w}", null),
              (HttpMethod.Get, $"{url}/summary?warehouseId={w}", null),
              (HttpMethod.Get, $"{url}/assignees?warehouseId={w}&type=Move", null),
              (HttpMethod.Get, $"{url}/{task.Id}", null),
              (HttpMethod.Get, $"{url}/mine?warehouseId={w}", null),
              (HttpMethod.Post, $"{url}/{task.Id}/publish", new { version = task.Version }),
              (HttpMethod.Post, $"{url}/{task.Id}/hold", new { version = task.Version }),
              (HttpMethod.Post, $"{url}/{task.Id}/assign", new { memberId = Guid.NewGuid(), version = task.Version }),
              (HttpMethod.Post, $"{url}/{task.Id}/unassign", new { version = task.Version }),
              (HttpMethod.Post, $"{url}/{task.Id}/priority", new { priority = 5, urgent = false, version = task.Version }),
              (HttpMethod.Post, $"{url}/{task.Id}/cancel", new { reason = "x", version = task.Version }),
              (HttpMethod.Post, $"{url}/{task.Id}/lines/{line}/cancel", new { reason = "x", version = task.Version }),
              (HttpMethod.Post, $"{url}/next", new { warehouseId = w, deviceId = "zebra-01" }),
              (HttpMethod.Post, $"{url}/{task.Id}/claim", new { deviceId = "zebra-01" }),
              (HttpMethod.Post, $"{url}/{task.Id}/heartbeat", new { leaseToken = token }),
              (HttpMethod.Post, $"{url}/{task.Id}/unclaim", new { leaseToken = token, reason = "ShiftEnd" }),
              (HttpMethod.Post, $"{url}/{task.Id}/lines/{line}/confirm", new
              {
                  leaseToken = token, fromLocationId = s.BinA, toLocationId = task.Lines[0].ToLocationId, quantity = 1,
                  occurredAt = DateTimeOffset.UtcNow, deviceId = "zebra-01",
              }),
          ];
          var failures = new List<string>();

          foreach (var (method, path, body) in routes)
          {
              using var request = new HttpRequestMessage(method, path);
              if (body is not null)
              {
                  request.Content = JsonContent.Create(body);
              }

              // confirm es IK: sin la clave respondería 428 antes de mirar el tenant.
              request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
              using var response = await stranger.SendAsync(request, Ct);
              var code = response.StatusCode == HttpStatusCode.Forbidden ? await CodeOfAsync(response) : null;
              if (response.StatusCode != HttpStatusCode.Forbidden || code != "authorization.denied")
              {
                  failures.Add($"{method} {path} → {(int)response.StatusCode} {code}");
              }
          }

          Assert.Empty(failures);
      }
  }
  ```

  Check before running that every body matches its request record (`TaskEndpoints.cs` and the `*Request` records in
  `TaskDtos.cs`); a body that does not bind returns 400 and shows up in `failures`, which is a test bug, not a finding.
  `POST /tasks` is already covered by `CreatingForAnotherTenantIs403`. If a route returns something other than 403, **that is a
  finding: stop and report it** (do not loosen the assertion).
- [ ] Run (filtered by the new and touched tests):

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~TaskTenantIsolationApiTests|FullyQualifiedName~AConfirmationWithAStaleTokenIs409LeaseLost|FullyQualifiedName~TheSameKeyWithAnotherBodyOnConfirmIs409|FullyQualifiedName~AHeldAndRepublishedTaskRejectsALateConfirmation|FullyQualifiedName~WithoutASweep|FullyQualifiedName~AHeartbeatAfterTheSweepIs409Resumable" -- xUnit.MaxParallelThreads=4
  ```

  Green.
- [ ] Commit: `test(wms): cover tenant isolation, confirm idempotency and lease recovery in the task API` (paths: the three files).

### Task B5: Block B verification (once, at the end)

- [ ] Citation still right (Block B must not have moved it):

  ```powershell
  Select-String -LiteralPath src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs -Pattern "LockExpiredAsync\(|NextNumberAsync\("
  ```

  `LockExpiredAsync(` still at 201 and `NextNumberAsync(` at 219. If not, fix `MultiReplicaInventory.cs` and amend nothing: make
  a new commit `test(architecture): update the task sweeper citation`.
- [ ] Unit: `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests`: green (whole project, it is fast).
- [ ] Architecture: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~CompositionRootTests"`:
  green.
- [ ] Integration, **full task classes once** (check `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` first; wait if
  another run is active; foreground):

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks|FullyQualifiedName~WmsPersistenceTests|FullyQualifiedName~InventoryLedgerMigrationTests" -- xUnit.MaxParallelThreads=4
  ```

  This covers every class under `Tasks/` (`TaskOperatorApiTests`, `TaskSupervisorApiTests`, `TaskSupervisorActionsApiTests`,
  `TaskConcurrencyTests`, `TaskLeaseSweeperTests`, `TaskCandidateQueryTests`, `WarehouseTaskPersistenceTests`,
  `TaskEventRecordedAtMigrationTests`, `TaskTenantIsolationApiTests`) plus the schema/migration classes the two new migrations
  could affect. Expected: green. Copy the summary line (`Passed!  - Failed: 0, Passed: N …`) into the hand-off.
- [ ] Merge gate (for whoever merges, not required to finish the block): `.\scripts\test-feature.ps1` reading its warnings. It will
  flag «Requiere autorización del owner» for `MultiReplicaInventory.cs` and `CompositionRootTests.cs` (modified architecture
  tests): both are covered by the O-7 delegation recorded in spec §13.1. Any **other** warning (a new module reference, a contract
  change not in the spec) needs the owner.
- [ ] Hand-off: branch, commit SHAs of both blocks, the test summaries, what was not run, and the «Agent decisions» below for the
  owner. No push.

---

## Agent decisions (por confirmar)

Choices the reviewer did not fix; each one is cheap to revert.

1. **History tie-breaker keeps `Kind` before `Id`** (`recorded_at`, `kind`, `id` instead of `recorded_at`, `id`):
   `Guid.CreateVersion7()` in .NET is not monotonic within one millisecond, and the facts of one transaction share `recorded_at`,
   so `id` alone would break the causal order the existing tests pin (`LeaseResumed` → `LineConfirmed` → `Completed`).
2. **`recordedAt` is not exposed** in `TaskEventDto` (the reviewer's default; no screen needs it).
3. **`recorded_at` is `NOT NULL DEFAULT now()`** (not nullable): an old pod keeps inserting, and new code never reads a null.
4. **`staleAssignments` covers `Planned` and `Assigned`** with an assignee (not `InProgress`, which the lease handles); one
   eligible-members lookup per distinct permission.
5. **Indexes:** `IX_tasks_board` carries `type` as a fourth key column so the "open by type" count is index-only; **no index is
   dropped** (`IX_tasks_pool` stays for the hot `next` path, `IX_tasks_warehouse_id` stays for the FK). No index for `GET /tasks`
   without `warehouseId`.
6. **The permission anchor drops the count from its name** (`PermissionDiscoveryFindsEveryPermissionConstant`) instead of being
   renamed to `…HundredAndThirtyOne…`.
7. **`RenewClaim` keeps the device and the token**; a `next`/`claim` from another device while the task is in progress returns the
   same token, as before. If the sweeper wins the race inside `next`, the handler falls through to the normal claim path.
8. **The 409 message changes when `resumable` is true** («Tu reclamo de esta tarea venció. Pídela otra vez para seguir.»); the
   code and status do not change.
