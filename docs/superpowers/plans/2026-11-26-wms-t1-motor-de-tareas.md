# WMS-T1 · Motor de tareas — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans. **Las reglas de `CLAUDE.md` de la carpeta `qep` mandan sobre el skill:** tres bloques, un implementador
> por bloque, una revisión por bloque y la revisión fuerte una sola vez al final. Cada tarea conserva su commit. Steps use checkbox
> (`- [ ]`) syntax.

**Goal:** El núcleo del motor de tareas del WMS: el área de dominio `Tasks` con `WarehouseTask` (líneas y eventos), su persistencia,
«siguiente tarea» con `FOR UPDATE SKIP LOCKED`, lease con token de exclusión, latido y barrido de vencidos, la API del supervisor y del
operario, el permiso `wms.task.manage`, y un primer tipo real, `Move`, cuya confirmación escribe al ledger.

**Architecture:** Área nueva `Modules.Wms.Domain.Tasks` (sólo depende de `Inventory` y `Warehousing`; ningún otro área la conoce). El
agregado aplica todas las transiciones; `Application` orquesta autorización, lease, ledger y recibos; `Infrastructure` trae el modelo
EF, el SQL de selección de candidata y el barrido. La confirmación de una línea es una ruta IK que bloquea la tarea (`FOR UPDATE`) y
después llama **una vez** a `IInventoryLedger.PostAsync`. Las acciones del supervisor se protegen con `version` (412).

**Tech Stack:** .NET 10, minimal APIs, EF Core 10 + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3, Testcontainers.PostgreSql
(`postgres:18-alpine`), `WebApplicationFactory<Program>` (los mismos de WMS-D).

**Spec:** [`docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md`](../specs/2026-11-26-wms-tareas-y-operaciones-design.md),
§3, §5, §6 completo y §7.9. Las decisiones `DA-xx` están **por confirmar**: si el owner cambia alguna antes de empezar, gana el owner.

**Fuera de alcance:** novedades y `short` (T2), recepción y unidades logísticas (T3), tipos distintos de `Move`, dependencias y
`Blocked` (T5), tareas de dos pasos, `HANDLING` y `NeedsRecovery` (T6), zonas/equipos, frontend y app, `docs/estado.md` (se toca en
`develop` al mergear).

---

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-tasks`, rama
  `feature/wms-tasks` (nace de `feature/wms-d`). Todo comando corre ahí (`Set-Location` a esa ruta al empezar cada script).
  `git branch --show-current` justo antes de cada commit; si dice `main` o `develop`, frena.
- **PowerShell:** sin `&&` (`A; if ($?) { B }`), `$env:VAR = "valor"` en línea aparte.
- **`Api.exe` detenido** antes de `dotnet build`/`test`/`ef` (no detengas uno que no lanzaste: avisa).
- **Una sola corrida de integración a la vez en la máquina:** antes de lanzar una, `Get-Process testhost, dotnet -ErrorAction
  SilentlyContinue`; si hay otra, espera. Las pruebas corren **en primer plano**.
- Integración: Docker arriba y `$env:Seed__Enabled = "false"`. Mientras se itera, `--filter "FullyQualifiedName~<Prueba>"`; la clase
  completa una vez al final de cada bloque.
- **El RED de un fallo de compilación no levanta contenedores:** la evidencia es la salida del build.
- **Verificación de la base (una vez, antes de la tarea 1):** la dimensión de unidad logística de WMS-D (T18) nunca se probó contra la
  base. Corre `dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~InventoryLedgerPostingTests"`.
  Si falla, frena y repórtalo: T1 confirma movimientos por ese ledger.
- Migración: `dotnet ef migrations add AddWarehouseTasks --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations`.
- **Nunca reescribas un `.cs` con `Set-Content`/`Out-File`** (rompe tildes, deja BOM): usa la herramienta de edición.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA**, aunque el harness la sugiera.
- Comentarios y textos para personas en español con tuteo; identificadores en inglés.

## Piezas existentes que este plan usa (verificadas en `feature/wms-tasks`)

| Pieza | Dónde | Cómo se usa |
| --- | --- | --- |
| `Adjustment` (patrón de agregado: `private` ctor, `static Create`, `Version++` por transición, `EnsureStatus`) | `src/Modules/Wms/Modules.Wms.Domain/Control/Adjustment.cs` | Molde de `WarehouseTask` |
| `InvalidTransitionException : DomainException, IHasProblemExtensions` | `Modules.Wms.Domain/Control/InvalidTransitionException.cs` | Molde de `TaskTransitionException` (no se reutiliza: `Tasks` no puede ver `Control`) |
| `WmsDomainException(code, message)`, `WmsErrorCodes` | `Modules.Wms.Domain/WmsDomainException.cs`, `WmsErrorCodes.cs` | Códigos `wms.task.*` |
| `WarehouseId`, `LocationId`, `OwnerId` (`readonly record struct (Guid Value)`), `Location` (`WarehouseId`, `Code`, `IsVirtual`, `AllowsStorage`, `Status`) | `Modules.Wms.Domain/Warehousing/` | Referencias de la tarea |
| `LotId`, `StockKey`, `StockStatus`, `MovementReason.InternalMove`, `LedgerEntry`, `LedgerBatch`, `LedgerActor`, `LedgerActorType.Human`, `LedgerDocument` | `Modules.Wms.Domain/Inventory/` | Confirmación al ledger |
| `IInventoryLedger.PostAsync` (una llamada por transacción) | `Modules.Wms.Application/Inventory/IInventoryLedger.cs` | |
| `IWmsUnitOfWork` (`BeginTransactionAsync`, `LockAsync`, `SaveChangesAsync`) | `Modules.Wms.Application/WmsPorts.cs` | Transacción y lock por operario |
| `WmsAuthorization.EnsureAsync`, `WmsCaller.RequireAsync`, `WmsNotFound`, `WmsVersions.Ensure` | `Modules.Wms.Application/WmsSupport.cs` | Autorización, actor (membresía), 404, 412 |
| `IdempotencyScope`, `IOperationReceipts.Record`, `IIdempotentRequest`, `RequireIdempotencyKey()` | `Modules.Wms.Application/Inventory/`, `Modules.Wms.Api/IdempotencyKeyFilter.cs` | Rutas IK (crear tarea, confirmar línea) |
| `PostAdjustmentHandler` (orden: autorizar → clave → `await using` transacción → lock → cargar → validar → ledger → recibo → `SaveChangesAsync`) | `Modules.Wms.Application/Control/PostAdjustment.cs` | Molde de `ConfirmTaskLineHandler` |
| `ApproveAdjustmentHandler` (autorizar dos veces, `WmsVersions.Ensure`, auditar, componer, guardar) | `Modules.Wms.Application/Control/ApproveAdjustment.cs` | Molde de las acciones del supervisor |
| `AdjustmentDtoComposer` (variantes por `IWmsCatalogReader`, lotes por `ILotRepository.GetManyAsync`) | `Modules.Wms.Application/Control/AdjustmentDtos.cs` | Molde de `WarehouseTaskDtoComposer` |
| `AdjustmentRepository.NextNumberAsync` (lock bloqueante + `count(*) LIKE`) | `Modules.Wms.Infrastructure/Persistence/AdjustmentRepository.cs` | Numeración `TSK-` |
| `InventoryLedgerModel.ConfigureAdjustment` (enums `HasConversion<string>()`, `version` `bigint` token, `HasCheckConstraint`, `HasMany(...).UsePropertyAccessMode(Field)`) | `Modules.Wms.Infrastructure/Persistence/InventoryLedgerModel.cs` | Molde de `WarehouseTaskModel` |
| `WmsUnitOfWork.PersistAsync` (traducción por nombre de índice y de FK) y `WmsDbContext.LocationReferenceForeignKeys` / `OwnerReferenceForeignKeys` | `Modules.Wms.Infrastructure/Persistence/WmsUnitOfWork.cs`, `WmsDbContext.cs` | Índice de «una tarea en curso» y FKs nuevas |
| `WorkerLoop.RunAsync`, `WmsWorkerOptions` (`[Range]` + `WmsWorkerOptionsValidator`) | `Modules.Wms.Infrastructure/Workers/WorkerLoop.cs`, `Modules.Wms.Application/Inventory/WmsWorkerOptions.cs` | Barrido de leases |
| `MultiReplicaInventory` (`WorkerClassification.Claimed`) | `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` | Entrada del barrido |
| `PurchasesMemberDirectory` (nombres por `TenancyDbContext`) y `SupportMemberPermissions` (permiso por `IRolePermissionChecker`) | `src/Bootstrapper/` | Molde de `WmsMemberDirectory` |
| Registros a mano: handlers (bloque Wms ~`ExportStockHandler`), `PermissionDefinition` (tras `WmsPermissions.CountApprove`), políticas (`AddAuthorization`, tras `CountApprove`), rol `admin` (lista que termina en `WmsPermissions.CountApprove`) | `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Permiso nuevo y handlers |
| `WmsApiHarness` (`OpenAsync`, `CreateClient`, `CreateManager`, `AllPermissions`, `CodeOfAsync`, `CountAuditEntriesAsync`, `ScalarAsync`), `WmsLedgerSeeding.ArrangeAsync` → `LedgerSetup` (bodega `BOG`, bin `A-01-02`, variante `V1`, `Self`), `StockAsync`, `OnHandAsync`, `SeedCallerMembershipAsync(factory, tenantId, subjectId)`, `CreateBinAsync` | `tests/Modules/Wms/Modules.Wms.IntegrationTests/` | Arranque de las pruebas |

## Global Constraints

- **La tarea nunca mueve stock por sí misma**; sólo `ConfirmTaskLineHandler` llama al ledger, una vez por transacción, con
  `LedgerDocument("Task", taskId, lineId)` y motivo `MovementReason.InternalMove`.
- **Orden de bloqueos:** advisory del operario (`task-member:{memberId}`) → fila de la tarea (`FOR UPDATE`) → saldos (dentro del
  ledger, `StockKeyComparer`). Ningún camino toma la tarea después de los saldos.
- Toda transición sube `Version` y agrega un `WarehouseTaskEvent`, **salvo** `Heartbeat` (sólo extiende el lease, sin evento ni
  versión).
- `leaseToken` sólo viaja en el DTO a quien tiene la tarea reclamada.
- Las acciones del supervisor llevan `version` (412 `concurrency.conflict`); crear y confirmar son IK (recibo antes de
  `SaveChangesAsync`, `await using` de la transacción).
- 403 `authorization.denied` siempre, nunca 404 para otro tenant; el permiso del tipo (`Move` → `wms.putaway.perform`) se exige en el
  handler del operario.
- Enums por nombre en la API y como texto en la base; cantidades `numeric(18,6)` en unidad base.

## Decisiones del plan (donde el spec dejaba margen)

1. **Lease perdido es 409 pero lo decide el dominio sin lanzar 409:** el dominio no ve `BuildingBlocks.Application`. `WarehouseTask.CheckLease(memberId, token)` devuelve `LeaseCheck { Valid, Resumable, Lost }`; el handler traduce `Lost` a `ResourceConflictException(wms.task.lease_lost)`. Las operaciones del dominio que exigen lease lanzan `InvalidOperationException` si se las llama con un lease perdido (error de cableado, no del usuario).
2. **`member_busy` con extensión:** `TaskMemberBusyException : ResourceConflictException, IHasProblemExtensions` en Application (el `ApiExceptionHandler` copia las extensiones de cualquier excepción que implemente la interfaz).
3. **«Siguiente tarea» sin resultado:** el comando devuelve `NextTaskResultDto(TaskDto? Task)` y el endpoint responde 204 si es `null` (el dispatcher no admite respuestas nulas sueltas).
4. **Perfil logístico:** crear una tarea llama `IVariantProfileRepository.GetOrCreateDefaultAsync` por variante: valida que exista (`wms.profile.variant_unknown`) y da `baseUom` y precisión (`wms.uom.precision_exceeded` si la cantidad tiene más decimales). Va **antes** de abrir la transacción de numeración, como el provisioner en `ProposeAdjustmentHandler`.
5. **Ubicaciones válidas para un `Move`:** las dos de la bodega de la tarea y no virtuales; el **destino** además admite almacenamiento (el origen puede ser un muelle, como `REC-01`); `from ≠ to`. `Blocked`/`Counting` no se validan al crear: el ledger las rechaza al confirmar.
6. **Un `Move` sin asignado se crea `Ready` si `publish = true` (default) y `Planned` si no;** con `assignToMemberId` y `publish = true` nace `Assigned`.
7. **Número:** `TSK-yyyyMMdd-NNNN` con `LockAsync("task-seq:{tenantId}")` y `count(*) … LIKE`, igual que `ADJ-`.

## Mapa de archivos

| Archivo | Bloque | Qué |
| --- | --- | --- |
| `tests/ArchitectureTests/ArchitectureTests/WmsLayerTests.cs` | A | Área `Tasks` |
| `src/Modules/Wms/Modules.Wms.Domain/Tasks/*.cs` | A | Enums, `TaskTransitionException`, `WarehouseTask`, `WarehouseTaskLine`, `WarehouseTaskEvent`, `TaskTypePolicy`, `TaskPriority` |
| `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs` | A | Códigos `wms.task.*` |
| `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/*.cs` | A | Pruebas del dominio |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs`, `WarehouseTaskRepository.cs` | B | Modelo EF y repositorio |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WmsDbContext.cs`, `WmsUnitOfWork.cs` | B | Constantes, configuración, traducción |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/*_AddWarehouseTasks*.cs` | B | Migración generada |
| `src/Modules/Wms/Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs` | B | Puerto |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Workers/TaskLeaseSweeper.cs`, `WmsInfrastructureExtensions.cs` | B | Barrido y registros |
| `src/Modules/Wms/Modules.Wms.Application/Inventory/WmsWorkerOptions.cs`, `src/Api/appsettings.json` | B | `TaskLeaseSweepSeconds` |
| `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` | B | Clasificación del barrido |
| `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/*.cs` | B, C | Pruebas de integración |
| `src/Modules/Wms/Modules.Wms.Application/WmsPermissions.cs`, `WmsPorts.cs` (auditoría), `WmsSupport.cs` (`WmsNotFound.Task`) | C | Permiso, auditoría, 404 |
| `src/Modules/Wms/Modules.Wms.Application/Tasks/*.cs` | C | DTOs, composer, puerto de membresías, comandos y consultas |
| `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`, `WmsEndpoints.cs` | C | Rutas |
| `src/Bootstrapper/WmsMemberDirectory.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` | C | Adaptador, handlers, permiso, política, rol |
| `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness.cs` | C | `AllPermissions` suma `TaskManage` |
| `README.md` (§ Módulo Wms), `.claude/rules/wms.md` | C | Contrato y gotchas |

---

# Bloque A — Dominio (tareas 1 a 3, un implementador)

### Task 1: Área `Tasks` vigilada por la arquitectura, vocabulario y códigos

**Files:**
- Modify: `tests/ArchitectureTests/ArchitectureTests/WmsLayerTests.cs`
- Create: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTaskType.cs`, `WarehouseTaskStatus.cs`, `TaskLineStatus.cs`,
  `TaskEventKind.cs`, `TaskSourceType.cs`, `TaskUnclaimReason.cs`, `TaskTransitionException.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs`

- [ ] **RED.** En `WmsLayerTests`: agrega `[InlineData("Tasks", new[] { "Inventory", "Warehousing" })]` a
  `DomainAreasOnlyDependDownwards`, suma `"Tasks"` al arreglo `areas` (así ninguna otra área puede referenciarla), y una prueba que
  impide que la fila nueva pase por vacía:

  ```csharp
  // Spec 2026-11-26 §4: el motor de tareas es un área al nivel de las de proceso. Sin tipos, la fila de arriba pasaría sin probar nada.
  [Fact]
  public void TheTasksAreaHasTypes() =>
      Assert.Contains(Domain.GetTypes(), type => type.Namespace == "Modules.Wms.Domain.Tasks");
  ```

  Corre `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"`: falla
  `TheTasksAreaHasTypes`. Guarda la salida.
- [ ] **GREEN.** Crea en `namespace Modules.Wms.Domain.Tasks` (comentario de cada enum: «se persiste por nombre; no se renombra»):
  - `WarehouseTaskType { Move }` (los demás tipos de §6.2 entran con su corte).
  - `WarehouseTaskStatus { Planned, Ready, Assigned, InProgress, Done, Cancelled }`.
  - `TaskLineStatus { Open, Done, Cancelled }`.
  - `TaskEventKind { Created, Published, Held, Assigned, Unassigned, Claimed, Unclaimed, LeaseExpired, LeaseResumed, PriorityChanged, LineConfirmed, LineCancelled, Completed, Cancelled }`.
  - `TaskSourceType { Manual }`.
  - `TaskUnclaimReason { ShiftEnd, CannotReach, Skipped, Other }`.
  - `TaskTransitionException(string from, string action) : DomainException(WmsErrorCodes.TaskInvalidTransition, …), IHasProblemExtensions` con `{ from, action }` (copia de `InvalidTransitionException`).
- [ ] En `WmsErrorCodes` agrega, en un bloque «Tareas (spec 2026-11-26 §6.11)»: `TaskNotFound = "wms.task.not_found"`,
  `TaskInvalidTransition = "wms.task.invalid_transition"`, `TaskLeaseLost = "wms.task.lease_lost"`, `TaskMemberBusy =
  "wms.task.member_busy"`, `TaskAssignedToOther = "wms.task.assigned_to_other"`, `TaskAssigneeNotEligible =
  "wms.task.assignee_not_eligible"`, `TaskLocationInvalid = "wms.task.location_invalid"`, `TaskLocationMismatch =
  "wms.task.location_mismatch"`, `TaskLotMismatch = "wms.task.lot_mismatch"`, `TaskQuantityExceedsRemaining =
  "wms.task.quantity_exceeds_remaining"`, `TaskLineInvalid = "wms.task.line_invalid"`.
- [ ] Corre el mismo filtro: verde (incluida la fila `Tasks` de `DomainAreasOnlyDependDownwards`).
- [ ] Commit: `feat(wms): add the tasks domain area and its vocabulary`

### Task 2: `WarehouseTask` — creación y acciones del supervisor

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTaskLine.cs`, `WarehouseTaskEvent.cs`, `WarehouseTask.cs`, `TaskTypePolicy.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskTests.cs`

Forma del agregado (las propiedades son `{ get; private set; }`, ctor privado para EF, como `Adjustment`):

```csharp
public sealed class WarehouseTask
{
    public const int MaxLines = 100;
    public const int NoteMaxLength = 500;
    public const int ReasonMaxLength = 500;
    public const int DeviceIdMaxLength = 64;

    // Id, TenantId, WarehouseId (WarehouseId), OwnerId (OwnerId), Number, Type, Status, Priority (int), Urgent, DueAt?, ReleasedAt?,
    // SourceType, SourceId?, Note?, AssignedMemberId?, ClaimedByMemberId?, ClaimedDeviceId?, LeaseToken?, LeaseExpiresAt?,
    // LastActivityAt?, CreatedBy, CreatedAt, CompletedAt?, CancelledAt?, CancelReason?, Version (long)
    // Lines (IReadOnlyList<WarehouseTaskLine>), Events (IReadOnlyList<WarehouseTaskEvent>): listas privadas, sólo se agrega.

    public static WarehouseTask CreateMove(
        Guid tenantId, string number, WarehouseId warehouse, OwnerId owner, int? priority, DateTimeOffset? dueAt, string? note,
        IReadOnlyList<WarehouseTaskLine> lines, Guid? assignTo, bool publish, Guid createdBy, DateTimeOffset now);

    public void Publish(Guid by, DateTimeOffset now);                       // Planned → Ready | Assigned; ReleasedAt = now
    public void Hold(Guid by, string? reason, DateTimeOffset now);          // Ready | Assigned → Planned
    public void Assign(Guid memberId, Guid by, string? reason, DateTimeOffset now);
    public void Unassign(Guid by, DateTimeOffset now);                      // Assigned → Ready; Planned queda Planned sin asignado
    public void ChangePriority(int priority, bool urgent, Guid by, DateTimeOffset now);
    public void Cancel(string reason, Guid by, DateTimeOffset now);
    public void CancelLine(Guid lineId, string reason, Guid by, DateTimeOffset now);
}
```

`WarehouseTaskLine.Create(Guid variantId, LotId? lotId, StockStatus stockStatus, LocationId from, LocationId? to, decimal quantity)`
con `Id = Guid.CreateVersion7()`, `Position` (lo fija `Place(int)` como `AdjustmentLine`), `QuantityPlanned`, `QuantityConfirmed = 0`,
`Status = Open`, `CancelReason`, `Remaining => QuantityPlanned - QuantityConfirmed`. `WarehouseTaskEvent.Record(Guid tenantId, Guid
taskId, TaskEventKind kind, Guid? memberId, string? deviceId, Guid? lineId, decimal? quantity, string? detail, DateTimeOffset at)`.

`TaskTypePolicy` (estática): `DefaultPriority(WarehouseTaskType) → 3` para `Move`; `LeaseDuration(type) → TimeSpan.FromMinutes(15)`;
`AllowsDestinationOverride(type) → true` para `Move`; `MovementReasonOf(type) → MovementReason.InternalMove`.

- [ ] **RED.** Escribe `WarehouseTaskTests` (fixtures como `AdjustmentTests`: `Tenant`, `Member`, `Now`, `Owner`, `Warehouse`,
  `Variant`, helpers `Line(decimal qty = 5)` y `NewMove(bool publish = true, Guid? assignTo = null, params WarehouseTaskLine[] lines)`).
  Una prueba por regla:

  ```csharp
  [Fact]
  public void CreatingAPublishedMoveIsReadyWithReleasedAtAndACreatedEvent()
  {
      var task = NewMove();

      Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
      Assert.Equal(Now, task.ReleasedAt);
      Assert.Equal(3, task.Priority);
      Assert.Equal(1, task.Version);
      Assert.Equal([TaskEventKind.Created, TaskEventKind.Published], task.Events.Select(e => e.Kind));
  }

  [Fact]
  public void CreatingWithAnAssigneeAndPublishIsAssigned() =>
      Assert.Equal(WarehouseTaskStatus.Assigned, NewMove(assignTo: Other).Status);

  [Fact]
  public void CreatingUnpublishedIsPlannedWithoutReleasedAt()
  {
      var task = NewMove(publish: false);
      Assert.Equal(WarehouseTaskStatus.Planned, task.Status);
      Assert.Null(task.ReleasedAt);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(101)]
  public void AMoveHasBetweenOneAndOneHundredLines(int count)
  {
      var error = Assert.Throws<WmsDomainException>(() => NewMove(lines: Enumerable.Range(0, count).Select(_ => Line()).ToArray()));
      Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
  }
  ```

  y además: cantidad `≤ 0` → `TaskLineInvalid`; `from == to` → `TaskLocationInvalid`; prioridad fuera de 1..9 → `TaskLineInvalid`;
  `Publish` desde `Ready` → `TaskTransitionException` con `ProblemExtensions["from"] == "Ready"`; `Hold` desde `Assigned` deja
  `Planned` conservando `AssignedMemberId`, y `Publish` después vuelve a `Assigned`; `Assign` desde `Ready` → `Assigned` y sube
  `Version`; `Assign` desde `InProgress` sin motivo → `TaskLineInvalid`, con motivo → `Assigned`, `LeaseToken` nuevo nulo y
  `ClaimedByMemberId` nulo; `Unassign` desde `Assigned` → `Ready`; `ChangePriority` en `Done` → transición inválida; `Cancel` cancela
  las líneas abiertas y es terminal (un segundo `Cancel` → transición inválida); `CancelLine` de la única línea abierta sin ninguna
  `Done` → la tarea queda `Cancelled`; `CancelLine` de la última abierta con otra `Done` → `Done` con `CompletedAt`; `CancelLine` de una
  línea ya cancelada → `TaskTransitionException` con acción `cancel-line`.
- [ ] Corre `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: falla por tipos inexistentes. Guarda la salida (RED de compilación).
- [ ] **GREEN.** Implementa `WarehouseTaskLine`, `WarehouseTaskEvent`, `TaskTypePolicy` y `WarehouseTask` con estas reglas:
  - `CreateMove`: valida líneas (1..`MaxLines`), `priority ?? TaskTypePolicy.DefaultPriority(Move)` en 1..9, nota ≤ 500 (recortada),
    coloca posiciones, `Status = Planned`, evento `Created`; si `assignTo` → `AssignedMemberId`; si `publish` → `Publish` (sin
    subir la versión dos veces: la creación termina con `Version = 1`).
  - Cada transición: `EnsureStatus(permitidos, "action")` → muta → `Version++` → evento con `by` y `detail` (motivo).
  - `Assign` desde `InProgress` exige motivo, limpia `ClaimedByMemberId`, `ClaimedDeviceId`, `LeaseToken`, `LeaseExpiresAt` y deja
    `Assigned`. Desde `Planned` queda `Planned` con el asignado.
  - `CloseIfFinished(now)` (privado): si no quedan líneas `Open`, `Done` (con `CompletedAt`, evento `Completed`) si alguna línea está
    `Done`, si no `Cancelled`; limpia el lease.
  - `Cancel`/`CancelLine` exigen motivo de 1..500.
- [ ] Corre `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~WarehouseTaskTests"`: verde.
- [ ] Commit: `feat(wms): model the warehouse task with its supervisor transitions`

### Task 3: Reclamo, lease, confirmación y prioridad

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs`
- Create: `src/Modules/Wms/Modules.Wms.Domain/Tasks/LeaseCheck.cs`, `TaskPriority.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskLeaseTests.cs`, `TaskPriorityTests.cs`

API que se agrega:

```csharp
public enum LeaseCheck { Valid, Resumable, Lost }

public void Claim(Guid memberId, string deviceId, DateTimeOffset now);           // Ready (sin asignado) | Assigned a memberId → InProgress
public LeaseCheck CheckLease(Guid memberId, Guid leaseToken);
public void Heartbeat(Guid memberId, Guid leaseToken, DateTimeOffset now);       // extiende; sin Version++ ni evento
public void Unclaim(Guid memberId, Guid leaseToken, TaskUnclaimReason reason, DateTimeOffset now);
public bool ExpireLease(DateTimeOffset now);                                     // true si cambió
public WarehouseTaskLine ConfirmLine(Guid memberId, Guid leaseToken, string deviceId, Guid lineId, decimal quantity, DateTimeOffset now);

public static class TaskPriority
{
    public const int UrgentBonus = 10;
    public const int AgingStepMinutes = 20;
    public const int AgingCap = 3;
    public static int Score(int priority, bool urgent, DateTimeOffset? releasedAt, DateTimeOffset? dueAt, DateTimeOffset now);
    public static int DueTier(DateTimeOffset? dueAt, DateTimeOffset now);   // 0, 1 (≤2 h), 2 (≤1 h), 3 (≤30 min), 4 (vencida)
}
```

- [ ] **RED.** `WarehouseTaskLeaseTests`:
  - `Claim` desde `Ready` → `InProgress`, `LeaseToken` no vacío, `LeaseExpiresAt = now + 15 min`, `ClaimedDeviceId`, evento `Claimed`.
  - `Claim` de una `Assigned` a otro → `WmsDomainException` `TaskAssignedToOther`; desde `Planned` → transición inválida.
  - `CheckLease`: token vigente del mismo miembro → `Valid`; otro token o otro miembro con la tarea `InProgress` → `Lost`; tarea
    `Ready` tras `ExpireLease` con el token y el miembro del último reclamo → `Resumable`; tarea reclamada después por otro (token nuevo)
    → `Lost`; tarea `Cancelled` o `Done` → `Lost`.
  - `Heartbeat` extiende `LeaseExpiresAt` a `now + 15 min` sin cambiar `Version` ni agregar eventos.
  - `Unclaim` → `Ready`, sin asignado aunque estuviera asignada a ese miembro, evento `Unclaimed` con el motivo en `detail`.
  - `ExpireLease` con lease vencido → `Ready` (o `Assigned` si tenía asignado), conserva `ClaimedByMemberId` y `LeaseToken`, evento
    `LeaseExpired`, devuelve `true`; con lease vigente devuelve `false` y no toca nada.
  - `ConfirmLine` parcial (2 de 5) deja la línea `Open` con `Remaining = 3`, renueva el lease, `LastActivityAt = now`, evento
    `LineConfirmed` con `lineId` y `quantity`; la segunda (3) cierra la línea y, siendo la única, la tarea queda `Done`.
  - `ConfirmLine` por más de lo pendiente → `TaskQuantityExceedsRemaining`; sobre una línea cerrada → transición inválida
    (`confirm-line`); cantidad ≤ 0 → `TaskLineInvalid`.
  - `ConfirmLine` con `Resumable` reanuda: `InProgress`, mismo token, evento `LeaseResumed` antes de `LineConfirmed`.
  - `ConfirmLine` con `Lost` → `InvalidOperationException` (el handler debió frenar antes).
- [ ] `TaskPriorityTests`: `Score(5, false, now - 70 min, null, now) == 8` (aging 3); `Score(5, false, now - 200 min, …) == 8` (tope);
  `Score(5, true, now, null, now) == 15`; `DueTier`: sin fecha 0, a 3 h 0, a 2 h 1, a 45 min 2, a 20 min 3, vencida 4.
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: falla (RED de compilación). Guarda la salida.
- [ ] **GREEN.** Implementa. `Claim` genera `LeaseToken = Guid.NewGuid()`, valida `deviceId` 1..64. `ConfirmLine`: `CheckLease` →
  `Lost` lanza `InvalidOperationException`; `Resumable` reanuda; valida línea y cantidad; suma; cierra la línea si `Remaining == 0`;
  `CloseIfFinished`; `Version++`.
- [ ] `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Tasks"`: verde.
- [ ] Commit: `feat(wms): add task claim, lease fencing and line confirmation`

**Cierre del bloque A:** corre `Modules.Wms.UnitTests` completo y `WmsLayerTests`. Revisión del bloque (un revisor, lectura): reglas de
§6.3 y §6.7 del spec contra las pruebas.

---

# Bloque B — Persistencia, candidata y barrido (tareas 4 a 6, un implementador)

### Task 4: Modelo EF, migración `AddWarehouseTasks` y repositorio

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs`, `WarehouseTaskRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WmsDbContext.cs`, `WmsUnitOfWork.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/WmsInfrastructureExtensions.cs`
- Create (generada): `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddWarehouseTasks.cs` (+ Designer, snapshot)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/WarehouseTaskPersistenceTests.cs`, `Tasks/TaskTestSupport.cs`

Puerto:

```csharp
public sealed record WarehouseTaskPage(IReadOnlyList<WarehouseTask> Items, int TotalCount);
public sealed record TaskStatusCount(WarehouseTaskStatus Status, int Count);

public interface IWarehouseTaskRepository
{
    void Add(WarehouseTask task);
    /// <summary>Con líneas, sin eventos (se agregan sin cargarlos), rastreada.</summary>
    Task<WarehouseTask?> GetAsync(Guid tenantId, Guid id, CancellationToken ct);
    /// <summary><c>SELECT … FOR UPDATE</c> de la fila, bloqueante; exige la transacción abierta. Devuelve false si no existe en el tenant.</summary>
    Task<bool> LockAsync(Guid tenantId, Guid id, CancellationToken ct);
    /// <summary>Con líneas y eventos, sin rastreo: la ficha del supervisor.</summary>
    Task<WarehouseTask?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<WarehouseTaskPage> ListAsync(Guid tenantId, Guid? warehouseId, WarehouseTaskStatus? status, WarehouseTaskType? type, Guid? assignedMemberId, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<TaskStatusCount>> CountByStatusAsync(Guid tenantId, Guid warehouseId, CancellationToken ct);
    Task<IReadOnlyList<WarehouseTask>> ListInProgressAsync(Guid tenantId, Guid warehouseId, CancellationToken ct);
    Task<IReadOnlyList<WarehouseTask>> ListAssignedToAsync(Guid tenantId, Guid warehouseId, Guid memberId, CancellationToken ct);
    Task<WarehouseTask?> FindInProgressOfMemberAsync(Guid tenantId, Guid memberId, CancellationToken ct);
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken ct);
}
```

- [ ] **RED.** `TaskTestSupport` (internal static): `NewMove(LedgerSetup s, Guid from, Guid to, decimal qty = 5, bool publish = true, Guid? assignTo = null, int? priority = null, DateTimeOffset? dueAt = null)` que arma el agregado con el dominio, y `SaveAsync(QepApiFactory factory, WarehouseTask task)` que lo guarda por `IWarehouseTaskRepository` + `IWmsUnitOfWork` en un scope. `WarehouseTaskPersistenceTests` (cada prueba abre su base con `OpenAsync` y `ArrangeAsync`, y crea un segundo bin con `CreateBinAsync`):
  - `ATaskRoundTripsWithItsLinesAndEvents`: guarda, relee con `GetWithEventsAsync`, compara estado, líneas (cantidades, ubicaciones,
    `StockStatus`) y eventos en orden.
  - `TheDatabaseRejectsAnInProgressTaskWithoutLease`: `ExecuteAsync` con `UPDATE wms.tasks SET status = 'InProgress' WHERE id = @id`
    falla con `23514` y nombre `CK_tasks_claim` (atrapa `PostgresException`).
  - `TwoInProgressTasksOfTheSameMemberCollideOnTheIndex`: dos tareas reclamadas por el mismo miembro, guardadas en dos scopes →
    la segunda lanza `TaskMemberBusyException` traducida por `WmsUnitOfWork` (409 `wms.task.member_busy`). *(La excepción se crea en
    esta tarea en `Modules.Wms.Application/Tasks/TaskMemberBusyException.cs`, con `taskId` opcional en las extensiones.)*
  - `DeletingALocationUsedByATaskIs422HasStock`: `DELETE` por la API de un bin usado como destino responde 422
    `wms.location.has_stock` (FK `FK_task_lines_to_location` en `LocationReferenceForeignKeys`).
  - `NumbersAreSequentialPerTenantAndDay`: dos `NextNumberAsync` en transacciones sucesivas → `TSK-yyyyMMdd-0001`, `…-0002`.
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.IntegrationTests`: falla por tipos inexistentes (RED de compilación).
- [ ] **GREEN.**
  - `WarehouseTaskModel.Configure(ModelBuilder)` (internal static, como `InventoryLedgerModel`), llamado desde
    `WmsDbContext.OnModelCreating` después de `InventoryLedgerModel.Configure`. Tablas `tasks`, `task_lines`, `task_events` con las
    columnas de §6.1 del spec (T1: sin `quantity_taken`, `reservation_id` ni `handling_unit_id`); enums `HasConversion<string>()`
    `HasMaxLength(16)` (kind de evento 32); `version` `bigint` `IsConcurrencyToken`; cantidades `HasPrecision(18, 6)`; FKs con
    nombre `FK_tasks_warehouse`, `FK_tasks_owner`, `FK_task_lines_task` (cascade), `FK_task_lines_from_location`,
    `FK_task_lines_to_location`, `FK_task_lines_lot`, `FK_task_events_task`, todas RESTRICT salvo la de líneas; `HasMany(Lines)` y
    `HasMany(Events)` con `UsePropertyAccessMode(Field)`.
  - Constantes en `WmsDbContext`: `TaskNumberIndex = "IX_tasks_tenant_number"`, `TaskMemberInProgressIndex =
    "IX_tasks_member_in_progress"`; `HasCheckConstraint` de `CK_tasks_type` (`'Move'`), `CK_tasks_status` (los seis de T1),
    `CK_tasks_priority`, `CK_tasks_claim`, `CK_tasks_assigned`, `CK_tasks_released`, `CK_task_lines_status`,
    `CK_task_lines_quantity` (`quantity_planned > 0 AND quantity_confirmed >= 0 AND quantity_confirmed <= quantity_planned`).
    Índices de §6.1 con `HasFilter`. Suma `FK_task_lines_from_location`, `FK_task_lines_to_location` a `LocationReferenceForeignKeys`
    y `FK_tasks_owner` a `OwnerReferenceForeignKeys`.
  - `WmsUnitOfWork.PersistAsync`: `UniqueViolation.Is(exception, WmsDbContext.TaskMemberInProgressIndex)` →
    `throw new TaskMemberBusyException(null, exception)`; `TaskNumberIndex` → `RequestConcurrencyException("concurrency.conflict", …)`
    (mismo criterio que `AdjustmentNumberIndex`).
  - `WarehouseTaskRepository`: `LockAsync` con `SELECT id FROM wms.tasks WHERE tenant_id = {t} AND id = {id} FOR UPDATE` (exige
    transacción, como `NextNumberAsync`); `NextNumberAsync` copia de `AdjustmentRepository.NextNumberAsync` con clave
    `"task-seq:" + tenantId` y prefijo `TSK-`; `ListAsync` ordena por `CreatedAt` desc y pagina con `WmsPaging`.
  - Registra `IWarehouseTaskRepository` en `AddWmsInfrastructure`.
  - Genera la migración con el comando de «Antes de empezar». Revisa el `Up` (tres tablas, índices filtrados, CHECKs, FKs) y deja el
    `Down` que genera EF (tablas nuevas sin datos de nadie: bajarlas es seguro, a diferencia del ledger).
- [ ] `dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WarehouseTaskPersistenceTests"`: verde.
- [ ] Commit: `feat(wms): persist warehouse tasks with their lines and history`

### Task 5: Candidata con `FOR UPDATE SKIP LOCKED` y prioridad

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskCandidateQueryTests.cs`

```csharp
/// <summary>La mejor tarea elegible no bloqueada, y la deja bloqueada por esta transacción (exige la de BeginTransactionAsync).
/// El orden es el de TaskPriority (spec §6.6) calculado en SQL con @now: asignada al miembro primero, luego score, due_at, released_at, id.</summary>
Task<Guid?> LockNextCandidateAsync(Guid tenantId, Guid warehouseId, Guid memberId, IReadOnlyCollection<WarehouseTaskType> types, DateTimeOffset now, CancellationToken ct);
```

SQL (en `WarehouseTaskRepository`, con `SqlQuery<Guid>` interpolado; `types` como `string[]`):

```sql
SELECT t.id AS "Value" FROM wms.tasks t
WHERE t.tenant_id = {tenantId} AND t.warehouse_id = {warehouseId}
  AND t.status IN ('Ready','Assigned')
  AND (t.assigned_member_id IS NULL OR t.assigned_member_id = {memberId})
  AND t.type = ANY({types})
ORDER BY (t.assigned_member_id IS NOT NULL) DESC,
  (t.priority
   + CASE WHEN t.urgent THEN 10 ELSE 0 END
   + LEAST(3, FLOOR(EXTRACT(EPOCH FROM ({now} - t.released_at)) / 1200)::int)
   + CASE WHEN t.due_at IS NULL THEN 0
          WHEN t.due_at <= {now} THEN 4
          WHEN t.due_at <= {now} + interval '30 minutes' THEN 3
          WHEN t.due_at <= {now} + interval '1 hour' THEN 2
          WHEN t.due_at <= {now} + interval '2 hours' THEN 1
          ELSE 0 END) DESC,
  t.due_at NULLS LAST, t.released_at, t.id
LIMIT 1
FOR UPDATE SKIP LOCKED
```

Un comentario encima dice que los números repiten `TaskPriority` y que `TaskCandidateQueryTests` amarra las dos versiones.

- [ ] **RED.** `TaskCandidateQueryTests` (sembrando tareas con `TaskTestSupport`):
  - `AnAssignedTaskWinsOverAHigherPriorityPoolTask` (asignada prioridad 1 vs pool prioridad 9).
  - `ATaskAssignedToSomeoneElseIsNeverACandidate`.
  - `UrgentBeatsAgedAndAgedIsCapped`: urgente prioridad 1 vs vieja de 300 min prioridad 5 → la urgente; vieja 300 min prioridad 5 vs
    nueva prioridad 8 → empate por score (8 y 8) resuelto por `due_at` y luego `released_at` (la vieja).
  - `AnOverdueTaskBeatsAFresherOneOfEqualPriority`.
  - `PlannedAndDoneTasksAreNotCandidates`.
  - `TwoTransactionsGetDifferentTasks`: abre dos scopes, cada uno con su transacción; el primero llama `LockNextCandidateAsync` y no
    confirma; el segundo recibe la otra tarea (no espera, no la misma). Con una sola tarea, el segundo recibe `null`.
  - `TheScoreMatchesTaskPriority`: para tres tareas sembradas, el orden de candidatas sucesivas (bloqueando cada una) coincide con
    ordenar en memoria por `TaskPriority.Score`.
- [ ] `dotnet build`: falla por el método inexistente (RED de compilación).
- [ ] **GREEN.** Implementa el método. `released_at` nunca es nulo en `Ready`/`Assigned` (`CK_tasks_released`).
- [ ] `--filter "FullyQualifiedName~TaskCandidateQueryTests"`: verde.
- [ ] Commit: `feat(wms): pick the next task with skip locked and the priority score`

### Task 6: `TaskLeaseSweeper`

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Inventory/WmsWorkerOptions.cs` (`[Range(10, 600)] public int TaskLeaseSweepSeconds { get; init; } = 60;`), `src/Api/appsettings.json` (`"TaskLeaseSweepSeconds": 60` en `Wms:Workers`)
- Modify: `IWarehouseTaskRepository` (+ `Task<IReadOnlyList<Guid>> LockExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct)`), `WarehouseTaskRepository`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Workers/TaskLeaseSweeper.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/WmsInfrastructureExtensions.cs`
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskLeaseSweeperTests.cs`

- [ ] **RED (arquitectura).** Agrega a `MultiReplicaInventory`, junto a las de WMS:

  ```csharp
  new("Modules.Wms.Infrastructure.Workers.TaskLeaseSweeper", WorkerClassification.Claimed,
      "Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs (LockExpiredAsync) — toma las tareas vencidas con FOR UPDATE SKIP LOCKED en la transacción de la pasada; dos réplicas reparten filas distintas y el dominio (ExpireLease) no hace nada si el lease ya no está vencido"),
  ```

  Corre `--filter "FullyQualifiedName~MultiReplicaInventoryTests"`: rojo (la entrada no está registrada en el contenedor).
- [ ] **RED (integración).** `TaskLeaseSweeperTests` (el worker se obtiene como `WmsHousekeepingTests.WorkerOf`, desde
  `GetServices<IHostedService>().OfType<TaskLeaseSweeper>()`, y se llama `RunOnceAsync(now)` con un `now` explícito):
  - `AnExpiredClaimGoesBackToThePool`: tarea reclamada con `now0`; `RunOnceAsync(now0 + 16 min)` → `Ready`, evento `LeaseExpired`,
    devuelve 1.
  - `AnExpiredAssignedClaimGoesBackToItsAssignee` → `Assigned`.
  - `ALiveLeaseIsUntouched` (`now0 + 5 min` → sigue `InProgress`, devuelve 0).
  - `TwoSweepsAtOnceNeverExpireTheSameTaskTwice`: dos `RunOnceAsync` en paralelo sobre 3 tareas vencidas → suma 3 y cada tarea con un
    solo evento `LeaseExpired`.
- [ ] **GREEN.**
  - `LockExpiredAsync`: `SELECT id … WHERE status = 'InProgress' AND lease_expires_at < {now} ORDER BY lease_expires_at LIMIT {limit}
    FOR UPDATE SKIP LOCKED`.
  - `TaskLeaseSweeper(IServiceScopeFactory, IOptions<WmsWorkerOptions>, ILogger<TaskLeaseSweeper>) : BackgroundService`, internal sealed
    partial, con `WorkerLoop.RunAsync(WorkerStartupSeconds, TaskLeaseSweepSeconds, …)`; `public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct)`:
    por lotes de 100, en un scope y una transacción por lote: `LockExpiredAsync` → `GetAsync` → `ExpireLease(now)` → `SaveChangesAsync`;
    termina cuando un lote vuelve vacío. El tick usa `IClock.UtcNow` resuelto por pasada (como `BalanceReconciliationWorker`). Log de
    error con `[LoggerMessage]`.
  - Registro: `services.AddSingleton<TaskLeaseSweeper>(); services.AddHostedService(provider => provider.GetRequiredService<TaskLeaseSweeper>());`.
- [ ] `--filter "FullyQualifiedName~TaskLeaseSweeperTests"` y `MultiReplicaInventoryTests`: verdes.
- [ ] Commit: `feat(wms): return expired task leases to the pool`

**Cierre del bloque B:** corre una vez `--filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks"` completo y
`WmsPersistenceTests`, `WmsUnitOfWorkTests` (tocaste la traducción). Revisión del bloque: orden de bloqueos, índices filtrados contra
las consultas, `Down` de la migración.

---

# Bloque C — API, permiso y Bootstrapper (tareas 7 a 10, un implementador)

### Task 7: Permiso `wms.task.manage`, puerto de membresías, DTOs y crear/leer tareas

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsPermissions.cs` (+ `TaskManage = "wms.task.manage"`, comentario «Supervisar tareas: crear, asignar, priorizar, cancelar»; el resumen de la clase pasa de dieciséis a diecisiete)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (definición, política, rol `admin`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` (`Resources.Task = "wms.task"`; `TaskCreated`, `TaskPublished`, `TaskHeld`, `TaskAssigned`, `TaskUnassigned`, `TaskPriorityChanged`, `TaskCancelled`, `TaskLineCancelled` = `"wms.task.*"`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsSupport.cs` (`WmsNotFound.Task(Guid)`, `WmsNotFound.TaskLine(Guid)` → `wms.task.not_found`)
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/IWmsMemberDirectory.cs`, `TaskDtos.cs`, `WarehouseTaskDtoComposer.cs`, `TaskTypePermissions.cs`, `CreateMoveTask.cs`, `GetTask.cs`, `ListTasks.cs`
- Create: `src/Bootstrapper/WmsMemberDirectory.cs`
- Create: `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`; Modify: `WmsEndpoints.cs` (`TaskEndpoints.Map(group);`)
- Modify: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness.cs` (`AllPermissions` suma `WmsPermissions.TaskManage`; el comentario dice diecisiete)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskSupervisorApiTests.cs`

Piezas:

```csharp
public interface IWmsMemberDirectory
{
    /// <summary>Nombre visible de membresías del tenant en cualquier estado (como PurchasesMemberDirectory); la que no tiene nombre no viene.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
    /// <summary>True sólo si la membresía está Active en el tenant y alguno de sus roles concede el permiso (como SupportMemberPermissions).</summary>
    Task<bool> CanPerformAsync(Guid tenantId, Guid memberId, string permission, CancellationToken ct);
    /// <summary>Las membresías Active del tenant cuyo rol concede el permiso.</summary>
    Task<IReadOnlyList<Guid>> ListWithPermissionAsync(Guid tenantId, string permission, CancellationToken ct);
}

public static class TaskTypePermissions
{
    // Spec §6.2: ejecutar una tarea exige el permiso de su proceso; Move usa el del operario que ya mueve mercancía entre bins.
    public static string For(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => WmsPermissions.PutawayPerform,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    public static IReadOnlyList<WarehouseTaskType> AllowedFor(IExecutionContext ctx) =>
        [.. Enum.GetValues<WarehouseTaskType>().Where(type => ctx.HasPermission(For(type)))];
}

public sealed record CreateMoveTaskRequest(Guid WarehouseId, Guid? OwnerId, int? Priority, DateTimeOffset? DueAt, string? Note,
    bool? Publish, Guid? AssignToMemberId, IReadOnlyList<MoveTaskLineRequest>? Lines) : IIdempotentRequest;
public sealed record MoveTaskLineRequest(Guid VariantId, Guid? LotId, string? StockStatus, Guid FromLocationId, Guid ToLocationId, decimal Quantity);
```

DTOs exactamente como §6.11 del spec (`TaskSummaryDto`, `TaskDto`, `TaskLineDto`, `TaskEventDto`, `TaskPageDto`), con el comentario
BFF en `TaskDto` sobre `leaseToken` (sólo al que la tiene) y `events` (sólo en la ficha). `WarehouseTaskDtoComposer(IWarehouseRepository,
IWmsCatalogReader, ILotRepository, IVariantProfileRepository, IWmsMemberDirectory, IClock)`: `ComposeAsync(task, Guid? viewerMemberId,
bool withEvents, ct)` y `ComposeSummariesAsync(tenantId, tasks, ct)`; códigos de ubicación desde la bodega (`GetAsync`/`ListAsync(includeInactive: true)`
como `AdjustmentDtoComposer`), `effectivePriority = TaskPriority.Score(…, clock.UtcNow)`, `baseUom` del perfil.

`CreateMoveTaskHandler` (molde: `ProposeAdjustmentHandler`): autoriza `TaskManage` → `validator.ValidateAndThrowAsync` (líneas 1..100,
cantidad > 0, `StockStatus` válido o nulo = `Available`, prioridad 1..9, nota ≤ 500) → provisioner si `OwnerId` es nulo → perfiles
`GetOrCreateDefaultAsync` por variante y precisión → `await using` transacción → dueño (`owners.FindAsync` / `GetSelfAsync`) y
autorización con dueño y bodega → bodega (`warehouses.GetAsync`, 404) → ubicaciones (`warehouse.FindLocation`; de otra bodega o virtual,
destino sin almacenamiento, o `from == to` → `wms.task.location_invalid`) → asignado (`members.CanPerformAsync(…, TaskTypePermissions.For(Move))`
o 422 `wms.task.assignee_not_eligible`) → `NextNumberAsync` → `WarehouseTask.CreateMove` con el actor `WmsCaller.RequireAsync` →
`tasks.Add` → auditoría `TaskCreated` con `ownerId`, `number`, `lineCount` → componer → `receipts.Record(dto)` → `SaveChangesAsync`.

Endpoints de esta tarea (molde: `AdjustmentEndpoints`): `POST /tasks` `.RequireIdempotencyKey().RequireAuthorization(WmsPermissions.TaskManage)` → 201 con
`Location: /api/v1/tenants/{tenantId}/wms/tasks/{id}`; `GET /tasks` y `GET /tasks/{id:guid}` con `WmsPermissions.DocumentRead`.

Bootstrapper: `PermissionDefinition(WmsPermissions.TaskManage, "Supervisar tareas de bodega", "Permite crear, asignar, priorizar y cancelar las tareas de los operarios de bodega.", "Bodegas", "medium", RequiredModules: [ModuleKeys.Wms]) { Shape = new("wms.task", "Tareas de bodega", "manage") }`
después de la de `CountApprove`; `.AddPolicy(WmsPermissions.TaskManage, policy => AddPermissionRequirement(policy, WmsPermissions.TaskManage))`;
`WmsPermissions.TaskManage` al final de la lista del rol `admin`; `services.AddScoped<Modules.Wms.Application.IWmsMemberDirectory, WmsMemberDirectory>();`
junto a los otros adaptadores Wms; handlers `CreateMoveTaskHandler`, `GetTaskHandler`, `ListTasksHandler` a mano en el bloque Wms; el
composer en `AddWmsInfrastructure` (como `AdjustmentDtoComposer`). `WmsMemberDirectory(TenancyDbContext tenancy, IRolePermissionChecker permissions)`:
nombres como `PurchasesMemberDirectory`; permiso como `SupportMemberPermissions.HasAnyAsync` con un solo permiso; la lista recorre las
membresías `Active` del tenant y filtra con `AnyGrantsAsync`.

- [ ] **RED.** `TaskSupervisorApiTests` (arranque: `OpenAsync`, `ArrangeAsync`, un segundo bin `B-01-01`; el manager tiene
  `AllPermissions`):
  - `CreatingAMoveReturns201ReadyWithItsLines`: `POST /tasks` con `Idempotency-Key` → 201, `status = "Ready"`, `number` con
    `^TSK-\d{8}-0001$`, línea con `fromLocationCode = "A-01-02"`, `toLocationCode = "B-01-01"`, `quantityRemaining = 5`,
    `baseUom = "UND"`, `leaseToken = null`; una entrada de auditoría `wms.task.created`.
  - `RepeatingTheKeyReturnsTheSavedTaskWith200` y `TheSameKeyWithAnotherBodyIs409` (`wms.idempotency_key_reused`); sin header → 428
    `wms.idempotency_key_required`.
  - `CreatingWithoutTaskManageIs403` (cliente con `DocumentRead` y `PutawayPerform`); `CreatingForAnotherTenantIs403`.
  - `AVirtualOrForeignLocationIs422LocationInvalid` (destino `ADJUSTMENT`; origen de otra bodega `MDE`).
  - `AssigningToAMemberWithoutThePutawayPermissionIs422`: siembra una segunda membresía con rol `advisor` (sin filas de rol en el
    tenant de prueba, así que no concede nada de WMS) → 422 `wms.task.assignee_not_eligible`; con rol `admin` → 200.
    *`SeedCallerMembershipAsync` (en `WmsApiHarness.Inventory.cs`) hoy fija `["admin"]` y un correo `caller-{tenantId[^2..]}@example.com`
    que choca si se siembran dos membresías del mismo tenant. Primer paso de esta tarea: agrégale `IReadOnlyCollection<string>? roles = null`
    (default `["admin"]`) y deriva el correo del `subjectId`. Las llamadas existentes no cambian.*
  - `ListingFiltersByStatusAndPages` y `GettingATaskIncludesItsEvents` (`events[0].kind = "Created"`).
  - `AnUnknownTaskIs404TaskNotFound`.
  - Arquitectura: `CompositionRootTests` (filtro `~CompositionRootTests`) queda verde con el permiso nuevo (forma, política, módulos,
    handlers registrados). Córrelo después del GREEN.
- [ ] `dotnet build`: rojo por tipos inexistentes (RED de compilación). Guarda la salida.
- [ ] **GREEN.** Implementa lo descrito.
- [ ] `--filter "FullyQualifiedName~TaskSupervisorApiTests"` y `--filter "FullyQualifiedName~CompositionRootTests"`: verdes.
- [ ] Commit: `feat(wms): create and read warehouse tasks behind wms.task.manage`

### Task 8: Acciones del supervisor, tablero y asignables

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/SupervisorTaskActions.cs` (comandos `PublishTaskCommand`, `HoldTaskCommand`, `AssignTaskCommand`, `UnassignTaskCommand`, `ChangeTaskPriorityCommand`, `CancelTaskCommand`, `CancelTaskLineCommand` y sus handlers), `GetTaskBoard.cs`, `ListTaskAssignees.cs`
- Modify: `TaskEndpoints.cs`, `QepServiceCollectionExtensions.cs` (handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskSupervisorActionsApiTests.cs`

Los siete handlers comparten un servicio scoped para no repetir el molde de `ApproveAdjustmentHandler`:

```csharp
public sealed class SupervisorTaskRunner(IExecutionContext ctx, WmsAuthorization authorization, IWarehouseTaskRepository tasks,
    IWmsUnitOfWork unitOfWork, IWmsAuditRecorder audit, IClock clock, WmsCaller caller, WarehouseTaskDtoComposer composer)
{
    /// <summary>Autoriza TaskManage → carga (404) → autoriza con dueño y bodega → versión (412) → muta → audita → compone → guarda.</summary>
    public async Task<TaskDto> RunAsync(Guid tenantId, Guid taskId, long version, string auditAction,
        Action<WarehouseTask, Guid, DateTimeOffset> mutate, IReadOnlyDictionary<string, string?> fields, CancellationToken ct);
}
```

`AssignTaskHandler` valida antes el asignado con `IWmsMemberDirectory.CanPerformAsync` (422 `wms.task.assignee_not_eligible`). El
tablero (`GetTaskBoardQuery(tenantId, warehouseId)` → `TaskBoardDto`) completa `byStatus` con los seis estados aunque cuenten cero, y
`operators` desde `ListInProgressAsync` con nombres. `ListTaskAssigneesQuery(tenantId, warehouseId, type)` → `{ items: [{ memberId, name }] }`
ordenado por nombre.

Rutas: las de §6.11 del spec (`publish`, `hold`, `assign`, `unassign`, `priority`, `cancel`, `lines/{lineId:guid}/cancel` con
`TaskManage`; `summary` con `DocumentRead`; `assignees` con `TaskManage`). Cuerpos `record … (long Version, …)`.

- [ ] **RED.** `TaskSupervisorActionsApiTests`:
  - `HoldThenPublishRoundTrips` (`Ready → Planned → Ready`, versión sube en cada paso, auditoría `wms.task.held` y `wms.task.published`).
  - `AStaleVersionIs412`.
  - `AssignAndUnassign` (con un miembro que sí puede: rol con `wms.putaway.perform`).
  - `ChangingPriorityShowsInEffectivePriority` (`urgent: true` → `effectivePriority = priority + 10`).
  - `CancellingClosesOpenLines` y `CancellingTwiceIs422InvalidTransition` con `extensions.from = "Cancelled"`.
  - `CancellingTheOnlyLineCancelsTheTask`.
  - `TheBoardListsEveryStatusEvenAtZero` (seis entradas en `byStatus`).
  - `AssigneesOnlyListMembersWithThePermission`.
  - `ActionsWithoutTaskManageAre403`.
- [ ] `dotnet build`: rojo (RED de compilación).
- [ ] **GREEN.** Implementa y registra los nueve handlers.
- [ ] `--filter "FullyQualifiedName~TaskSupervisorActionsApiTests"`: verde.
- [ ] Commit: `feat(wms): let supervisors publish, hold, assign, prioritize and cancel tasks`

### Task 9: API del operario y confirmación al ledger

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/GetMyTasks.cs`, `NextTask.cs`, `ClaimTask.cs`, `HeartbeatTask.cs`, `UnclaimTask.cs`, `ConfirmTaskLine.cs`, `OperatorTaskGuard.cs`
- Modify: `TaskEndpoints.cs`, `QepServiceCollectionExtensions.cs` (handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskOperatorApiTests.cs`, `Tasks/TaskConcurrencyTests.cs`

`OperatorTaskGuard` (scoped): `EnsureCanPerformAsync(tenantId, WarehouseTask task, ct)` (autoriza `DocumentRead` con dueño y bodega y
exige `ctx.HasPermission(TaskTypePermissions.For(task.Type))`, si no `WmsAuthorization.Denied()`), y `EnsureLease(task, memberId,
token)` que traduce `LeaseCheck.Lost` a `ResourceConflictException(WmsErrorCodes.TaskLeaseLost, "Esta tarea ya no es tuya.")`.

`NextTaskHandler(NextTaskCommand(TenantId, WarehouseId, DeviceId))` → `NextTaskResultDto`:

```csharp
await authorization.EnsureAsync(ctx, command.TenantId, WmsPermissions.DocumentRead, warehouseId: command.WarehouseId, ct: ct);
var types = TaskTypePermissions.AllowedFor(ctx);
if (types.Count == 0) throw WmsAuthorization.Denied();
var member = await caller.RequireAsync(command.TenantId, ct);
await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
await unitOfWork.LockAsync($"task-member:{member:D}", ct);
var current = await tasks.FindInProgressOfMemberAsync(command.TenantId, member, ct);
if (current is not null)
{
    if (current.WarehouseId.Value != command.WarehouseId) throw new TaskMemberBusyException(current.Id);
    return new NextTaskResultDto(await composer.ComposeAsync(current, member, withEvents: false, ct));
}
var now = clock.UtcNow;
var candidateId = await tasks.LockNextCandidateAsync(command.TenantId, command.WarehouseId, member, types, now, ct);
if (candidateId is null) return new NextTaskResultDto(null);
var task = await tasks.GetAsync(command.TenantId, candidateId.Value, ct) ?? throw WmsNotFound.Task(candidateId.Value);
task.Claim(member, command.DeviceId, now);
var dto = await composer.ComposeAsync(task, member, withEvents: false, ct);
await unitOfWork.SaveChangesAsync(ct);
return new NextTaskResultDto(dto);
```

`ClaimTaskHandler`: igual, pero `tasks.LockAsync(id)` (404 si no existe) en vez de la candidata, y `OperatorTaskGuard.EnsureCanPerformAsync`
antes de `Claim`. `HeartbeatTaskHandler`: transacción, `LockAsync`, y el latido sólo vale con `LeaseCheck.Valid`: `Resumable` y
`Lost` responden 409 `wms.task.lease_lost` y la app vuelve a pedir la tarea (sólo una confirmación reanuda, porque trae un hecho
físico que no se puede descartar); devuelve `TaskLeaseDto(leaseExpiresAt)`. `UnclaimTaskHandler`: `EnsureLease` con `Valid`, `Unclaim`. `GetMyTasksHandler`:
`FindInProgressOfMemberAsync` (si es de esa bodega) + `ListAssignedToAsync`.

`ConfirmTaskLineHandler(ConfirmTaskLineCommand(TenantId, TaskId, LineId, LeaseToken, FromLocationId, ToLocationId, LotId?, Quantity, OverrideReason?, OccurredAt, DeviceId))`
— molde `PostAdjustmentHandler`:

1. `authorization.EnsureAsync(… DocumentRead …)`; `var key = scope.Key ?? throw new InvalidOperationException(…)`.
2. `await using var transaction = await unitOfWork.BeginTransactionAsync(ct);` → `tasks.LockAsync` (404) → `tasks.GetAsync` →
   `guard.EnsureCanPerformAsync` → `member = caller.RequireAsync` → `guard.EnsureLease` (acepta `Valid` y `Resumable`).
3. Línea (404 `TaskLine` si no es de la tarea). `FromLocationId != line.FromLocationId` → 422 `wms.task.location_mismatch`.
   `ToLocationId != line.ToLocationId`: sin `OverrideReason` (o tipo que no lo admite) → `wms.task.location_mismatch`; con motivo,
   el destino tiene que ser de la bodega, no virtual y con almacenamiento (`wms.task.location_invalid`). `line.LotId` no nulo y
   `LotId` distinto → `wms.task.lot_mismatch`; lote efectivo = `line.LotId ?? command.LotId`.
4. `task.ConfirmLine(member, token, deviceId, lineId, quantity, now)` (valida cantidad y lease; reanuda si hacía falta).
5. Ledger: `new LedgerEntry(new StockKey(owner, from, variant, lot, line.StockStatus), new StockKey(owner, to, variant, lot,
   line.StockStatus), quantity, null, TaskTypePolicy.MovementReasonOf(task.Type), new LedgerDocument("Task", task.Id, line.Id),
   command.OccurredAt, overrideReason: command.OverrideReason)` en `new LedgerBatch(tenantId, Guid.CreateVersion7(), key, new
   LedgerActor(member, LedgerActorType.Human, command.DeviceId), [entry])` → `ledger.PostAsync`.
6. `dto = composer.ComposeAsync(task, member, withEvents: false)` → `receipts.Record(dto)` → `unitOfWork.SaveChangesAsync`.

Validadores: `deviceId` 1..64, `quantity > 0`, `overrideReason` ≤ 200, `reason` de `unclaim` dentro de `TaskUnclaimReason`.

Rutas (todas `RequireAuthorization(WmsPermissions.DocumentRead)`): `GET /tasks/mine`, `POST /tasks/next` (204 si `Task` es `null`),
`POST /tasks/{id:guid}/claim`, `/heartbeat`, `/unclaim`, `POST /tasks/{id:guid}/lines/{lineId:guid}/confirm` con `.RequireIdempotencyKey()`
y cuerpo `ConfirmTaskLineRequest : IIdempotentRequest`.

- [ ] **RED.** `TaskOperatorApiTests` (operario: `CreateClient(factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform)`
  con su membresía sembrada; stock de 10 en `A-01-02` con `StockAsync`; tarea `Move` de 4 a `B-01-01` creada por el manager):
  - `NextReturnsTheTaskClaimedWithALeaseToken`; repetir `next` devuelve la misma tarea y el mismo token.
  - `NextWithoutEligibleTasksIs204`.
  - `NextWithoutAnyTypePermissionIs403`.
  - `ConfirmingMovesTheStock`: confirmar 4 → `OnHandAsync(A-01-02) = 6`, `OnHandAsync(B-01-01) = 4`, tarea `Done`; el movimiento lleva
    `document_type = 'Task'`, `reason = 'InternalMove'` y `device_id`.
  - `RepeatingAConfirmationReturnsTheSavedResponse` (misma clave → 200 idéntico, un solo movimiento en `wms.stock_movements`).
  - `AConfirmationWithAStaleTokenIs409LeaseLost` (el supervisor reasignó a otro).
  - `ALateConfirmationResumesTheTask`: `TaskLeaseSweeper.RunOnceAsync(now + 16 min)` y luego confirmar con el token viejo → 200, la tarea
    `Done`, eventos `LeaseExpired`, `LeaseResumed`, `LineConfirmed`, `Completed`.
  - `AWrongSourceIs422LocationMismatch`; `AnotherDestinationWithReasonIsAccepted` (un tercer bin, `overrideReason` en el movimiento).
  - `ConfirmingMoreThanTheStockIs422StockInsufficient` (tarea de 12 con 10 en el bin) y la tarea sigue `InProgress` con la línea
    intacta (la transacción se deshizo).
  - `UnclaimReturnsTheTaskToThePool`; `HeartbeatExtendsTheLease`.
  - `MineShowsTheCurrentAndTheAssigned`.
- [ ] `TaskConcurrencyTests`:
  - `TwoOperatorsAskingAtOnceGetDifferentTasks` (dos tareas, dos operarios, `Task.WhenAll` de dos `next`).
  - `TheSameOperatorTappingTwiceGetsOneTask` (`Task.WhenAll` de dos `next` del mismo operario con dos tareas en el pool → mismo id, y
    una sola tarea `InProgress`).
  - `TwoConfirmationsOfTheSameTaskSerialize` (dos líneas, dos confirmaciones en paralelo con claves distintas → ambas 200, versión
    final 3, stock correcto).
- [ ] `dotnet build`: rojo (RED de compilación).
- [ ] **GREEN.** Implementa y registra los seis handlers.
- [ ] `--filter "FullyQualifiedName~TaskOperatorApiTests"` y `~TaskConcurrencyTests`: verdes.
- [ ] Commit: `feat(wms): let operators take, confirm and release tasks`

### Task 10: Documentación, reglas y cierre

**Files:**
- Modify: `README.md` (subsección «Tareas de bodega (motor de tareas)» dentro de § Módulo Wms, y el conteo de permisos de diecisiete)
- Modify: `.claude/rules/wms.md`

- [ ] README: rutas de §6.11 con permiso e idempotencia; estados; que el `leaseToken` sólo viaja a quien la tiene; que «siguiente» es
  idempotente y responde 204; el barrido y `Wms:Workers:TaskLeaseSweepSeconds`; que `Move` exige `wms.putaway.perform`.
- [ ] `.claude/rules/wms.md`, viñetas nuevas (prescriptivas, no descriptivas):
  - «La tarea se bloquea **antes** que los saldos: `LockAsync` de la tarea y después el ledger. Un camino nuevo que confirme sin
    bloquear la tarea, o que la bloquee después de postear, reabre carreras de la cola offline y deadlocks.»
  - «`Heartbeat` no sube `version` a propósito: si la subiera, cada latido (2 min) dejaría en 412 la pantalla del supervisor.»
  - «El barrido conserva `claimed_by_member_id` y `lease_token` al vencer: es lo que deja reanudar una confirmación tardía. No los
    "limpies".»
  - «Los números de `TaskPriority` están repetidos en el SQL de `LockNextCandidateAsync`; `TaskCandidateQueryTests` los amarra.
    Cambiar uno sin el otro lo pone en rojo.»
  - «Un tipo de tarea nuevo necesita: valor del enum, `CK_tasks_type` (migración), `TaskTypePermissions.For` y `TaskTypePolicy`.»
- [ ] Corre, uno a la vez y en primer plano: `tests/ArchitectureTests/ArchitectureTests` filtrado por `~WmsLayerTests`,
  `~MultiReplicaInventoryTests`, `~CompositionRootTests`; `Modules.Wms.UnitTests` completo; `Modules.Wms.IntegrationTests` filtrado
  por `~Tasks`. Después `.\scripts\test-feature.ps1` y **lee sus avisos**: va a pedir autorización del owner por `WmsLayerTests` y
  `MultiReplicaInventory` modificadas. Esa autorización (spec §14, O-7) se pide antes de mergear; una prueba en verde no la reemplaza.
- [ ] Commit: `docs(wms): document the task engine contract and its rules`

**Cierre del bloque C y de la rama:** revisión fuerte de toda la rama por un subagente de solo lectura sin el contexto del
implementador (`CLAUDE.md`). El merge a `develop` espera la autorización del owner (O-7) y deja `docs/estado.md` para el commit `docs:`
en `develop`.

---

## Self-review

- **Cobertura del spec:** §6.1 (tablas, índices, CHECKs) → T4; §6.2 (`Move`, permiso, lease, override, motivo) → T2/T3/T7/T9; §6.3
  (estados de T1 y reglas) → T2/T3; §6.5 (pull + push, elegibilidad) → T5/T8/T9; §6.6 → T3/T5; §6.7 (reclamo, latido, fencing,
  barrido, tardía, soltar) → T3/T6/T9; §6.8 (lock del operario, `SKIP LOCKED`, índice, `FOR UPDATE` al confirmar) → T5/T9; §6.9
  (IK al confirmar, `occurredAt` validado por el ledger) → T9; §6.10 → T8; §6.11 (rutas, DTOs, códigos, permiso) → T7–T9; §6.12
  (eventos, auditoría sólo del supervisor) → T2/T7/T8; §7.9 → T9.
- **Contra el código:** las tareas usan `WmsCaller` (membresía) como actor; la auditoría del supervisor pasa `executionContext.SubjectId`
  como los handlers de ajustes (`ApproveAdjustmentHandler`). Los dos `CHECK` y la FK nueva sobre `locations` caen en las traducciones
  existentes por nombre. `WmsWorkerOptions` vive en Application: `ConfigurationExampleTests` sólo mira las options de los ensamblados
  `Infrastructure` y `Bootstrapper`, así que la clave nueva no exige tocar `appsettings.example.json`.
- **Contradicciones resueltas:** el latido no acepta `Resumable` (sólo la confirmación reanuda, porque trae el hecho físico); la
  cancelación de la última línea decide `Done`/`Cancelled` por si quedó alguna `Done`; `Assign` desde `InProgress` exige motivo.
- **Riesgo conocido:** si `SeedCallerMembershipAsync` sólo siembra `admin`, las pruebas de «miembro sin permiso» necesitan el parámetro
  `roles` (anotado en T7). La base del ledger (WMS-D T18) se verifica antes de empezar.

## Desviaciones

### Bloque A

- **Orden de las tareas 2 y 3.** `Assign` desde `InProgress`, `ChangePriority` en `Done` y el cierre en `Done` necesitan `Claim` y
  `ConfirmLine`: se implementaron y probaron en la tarea 3.
- **Asignar revoca el reclamo vencido.** `Assign` desde `InProgress`, o a una membresía distinta de la que reclamó, limpia el reclamo:
  su confirmación tardía recibe `wms.task.lease_lost` (§6.5). Sin esto quedaría una tarea en curso de una persona y asignada a otra.
- **Campos del reclamo.** `Done`, `Cancelled` y `Unclaim` limpian los cuatro. `ExpireLease` conserva membresía, dispositivo y token, y
  limpia `LeaseExpiresAt`: quien componga los DTO sólo muestra reclamo y `leaseToken` con la tarea `InProgress`.
- **`ReleasedAt`.** `Hold` lo borra (`Publish` lo fija de nuevo); `Unclaim` y `ExpireLease` lo conservan.
- **`Unassign` de una `Planned` sin asignado** es `wms.task.invalid_transition`.
- **Línea inexistente.** El dominio expone `FindLine`; `CancelLine` y `ConfirmLine` con otra línea lanzan `InvalidOperationException`:
  el handler responde antes el 404.
- **`deviceId` fuera de 1..64** y nota de más de 500 lanzan `ArgumentException`: los validadores de la tarea 9 y la 7 dan el 422.
- **El envejecimiento nunca es negativo** (relojes corridos entre réplicas). El SQL de la tarea 5 debe usar
  `LEAST(3, GREATEST(0, FLOOR(…)))`.

### Bloque B

- **`LockExpiredAsync` devuelve `ExpiredTaskLease(TenantId, TaskId)`**, no sólo ids: `GetAsync` pide el tenant. Es la única lectura del
  puerto que cruza tenants.
- **El barrido termina cuando un lote no vence ninguna** (incluye el lote vacío): no puede quedar girando sobre una fila que el dominio
  no cambie.
- **`WmsUserReferenceProbe` cuenta las tareas** (no estaba en el plan): `tasks.created_by`, `assigned_member_id`, `claimed_by_member_id`
  y `task_events.member_id` guardan membresías; sin esto la limpieza de huérfanos borraría a quien sólo figura en la historia de una
  tarea.
- **`WmsPersistenceTests.TheMigrationCreatesTheEightTablesWithTheirUniqueIndexes`** excluye las tres tablas nuevas: sin eso se pone en
  rojo.
- **Puerto:** `ListInProgressAsync`, `ListAssignedToAsync` y `FindInProgressOfMemberAsync` leen sin rastreo (una instancia rastreada
  antes de `LockAsync` quedaría vieja); `ListAssignedToAsync` trae sólo las `Assigned`, ordenadas por corte, antigüedad e id;
  `ListAsync` normaliza la página con `WmsPaging`.
- **Modelo:** `priority` es `smallint` por conversión a `short`; el índice único de posición se llama `IX_task_lines_position`; EF
  agrega por convención `IX_tasks_owner_id`, `IX_tasks_warehouse_id` e `IX_task_lines_{from_location_id,to_location_id,lot_id}`.
- **Sin ejecutar:** ninguna prueba de integración de este bloque ni la verificación previa (`InventoryLedgerPostingTests`): había otra
  suite con contenedores en la máquina. Se verificó con build, `ArchitectureTests`, `Modules.Wms.UnitTests`,
  `has-pending-model-changes` y la traducción de las consultas LINQ sin conexión. Hay que correr, antes de mergear,
  `--filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks"`, `WmsPersistenceTests`, `WmsUnitOfWorkTests`,
  `WmsUserReferenceProbeTests` e `InventoryLedgerPostingTests`.

### Bloque C

- **Las acciones del supervisor bloquean la fila** (`LockAsync` y después `GetAsync`) dentro de una transacción, además de comparar
  `version`: así una acción nunca cruza sus escrituras con una confirmación a medias.
- **Confirmar toma primero el lock del operario** (`task-member:{id}`), como `next` y `claim`: es el orden global y lo que hace segura
  la lectura de «ya tiene otra en curso» (DA-32).
- **Decisiones nuevas** DA-32 a DA-37 (spec §13), con un commit cada una (DA-35 sólo documenta lo que el dominio ya hacía).
- **`ConfirmLine`** recibe además `occurredAt` y `memberIsBusy`, opcionales. La ventana de `occurredAt` la valida también el dominio,
  antes de reanudar, con el código del ledger.
- **DA-33:** el filtro de lo saltado no aplica a lo que el supervisor le asignó a esa persona.
- **Puerto:** se agregaron `CountOpenByTypeAsync` (`byType`) y `FindClaimedAtAsync` (`operators.since`, el último evento `Claimed`;
  el DTO lo admite nulo).
- **Reclamar la tarea que ya tiene en curso** la devuelve sin cambios, como repetir `next`. «Mis tareas» exige el permiso de algún tipo
  y filtra por los permitidos.
- **Crear** valida además el lote (404 `wms.lot.not_found` si no es de la variante y el dueño: la FK no lo mira), la precisión
  (`wms.uom.precision_exceeded`), una cantidad menor que 10^12 y líneas nulas; envía a la base (`FlushAsync`) antes de componer.
- **`GET /tasks/assignees`** no comprueba que la bodega exista: sólo acota la autorización.
- **Confirmar** una línea registra `qep.wms.confirm.duration`.
- **Pruebas:** como la integración no se podía correr, los casos de uso también tienen pruebas unitarias con dobles en memoria
  (`Modules.Wms.UnitTests/Tasks/TaskWorld*.cs`) y `TaskRoutesTests` fija rutas y políticas.
  `TwoConfirmationsOfTheSameTaskSerialize` termina en versión 4.
- **Arquitectura:** `CompositionRootTests` cambió (131 permisos y la fila `wms.task.manage`). Necesita autorización del owner junto
  con `WmsLayerTests` y `MultiReplicaInventory` (O-7).
- **Sin ejecutar:** ninguna prueba de integración, `Bootstrapper.UnitTests` ni `.\scripts\test-feature.ps1` (había otra suite con
  contenedores en la máquina). Antes de mergear hay que correr, además de las listadas en el bloque B,
  `--filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks"`, `StockProbeTests`, la clase
  `CatalogVariantUsageCheckerTests` y `.\scripts\test-feature.ps1`.

### Pendiente para el bloque C (resuelto en el bloque C)

- `GetWithEventsAsync` trae los eventos sin orden: el composer los ordena (viñeta siguiente).
- `TaskBoardDto.byType` no tiene consulta en el puerto (`CountByStatusAsync` es por estado).
- Reclamar con otra tarea en curso: el handler consulta `FindInProgressOfMemberAsync` antes para dar `taskId`; el índice lanza
  `TaskMemberBusyException` sin él.
- `CatalogVariantUsageChecker` no cuenta las líneas de tareas abiertas, que todavía pueden escribir en el ledger (como las de un
  ajuste abierto): decidir antes de mergear.
- La tabla de workers del README y la frase de `WorkerStartupSeconds` («los tres workers del ledger») necesitan el barrido (tarea 10).

- Los eventos de una misma transacción comparten `occurred_at`, y `Guid.CreateVersion7` no ordena dentro del mismo milisegundo: la ficha
  y las pruebas ordenan por `(occurred_at, kind en el orden del enum, id)`. El enum está declarado en orden causal.
- `TwoConfirmationsOfTheSameTaskSerialize` termina en versión 4, no 3 (crear 1, reclamar 2, dos confirmaciones).
- `TaskBoardDto.operators.since` no tiene columna: sale del evento `Claimed` o hay que decidirlo.
- Decisiones del owner: (1) tras soltar con `Skipped`, `next` puede devolver la misma tarea; (2) una confirmación tardía de quien ya
  tiene otra tarea en curso choca con `IX_tasks_member_in_progress`, y si vuelve a reclamar la misma, el token cambia y su cola se
  rechaza; (3) cancelar la única línea, confirmada en parte, deja la tarea `Cancelled` (§6.3 al pie de la letra).
