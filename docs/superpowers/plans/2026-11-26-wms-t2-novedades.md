# WMS-T2 · Novedades, faltante y conteo de verificación — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans. **Las reglas de `CLAUDE.md` de la carpeta `qep` mandan sobre el skill:** tres bloques, un implementador
> por bloque, una revisión por bloque y la revisión fuerte una sola vez al final. Cada tarea conserva su commit. Steps use checkbox
> (`- [ ]`) syntax.

**Goal:** La novedad unificada (`Incident`, spec §8) con su catálogo cerrado, su historia y su evidencia; el faltante declarado en una
línea de tarea (`short`, línea `Short`); el tipo de tarea `Count` (conteo de verificación a ciegas) que se genera en la misma
transacción que la novedad y que, si encuentra diferencia, propone un `Adjustment` con motivo `CountVariance`; y la novedad
`SyncConflict` que queda registrada cuando una confirmación llega con el lease perdido, aunque la confirmación se rechace.

**Architecture:** `Incident` vive en el área `Control` (junto a `Adjustment`): depende sólo de `Inventory` y `Warehousing` y conoce las
tareas por `Guid`. Las tareas no conocen las novedades: la coordinación (crear la novedad, generar el `Count`, proponer el ajuste) vive
en `Application`, siempre en la transacción del hecho que la origina (spec §6.4, §8.4 regla 1). El conteo no escribe al ledger: el
movimiento lo hace el ajuste aprobado y contabilizado por el camino que ya existe (`PostAdjustmentHandler`).

**Tech Stack:** el de T1 (.NET 10, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers `postgres:18-alpine`,
`WebApplicationFactory<Program>`).

**Spec:** [`docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md`](../specs/2026-11-26-wms-tareas-y-operaciones-design.md)
§5 (principio 6), §6.2 (`Count`), §6.3 (`Short` en el cierre), §6.4 (fila «Novedad»), §6.7 (`SYNC_CONFLICT`), §6.10 (novedades en el
tablero), §6.12 (`LineShort`), §7.9 (faltante de un `Move`), §8 completo. Las decisiones nuevas de este plan son DA-38 a DA-51 de §13,
**por confirmar**: si el owner cambia alguna antes de empezar, gana el owner.

**Fuera de alcance:** tipos de novedad de recepción, putaway, picking, empaque y despacho (entran con su corte: el enum crece como
`WarehouseTaskType`); las resoluciones que mueven a cuarentena, devuelven o dan de baja (`ReturnToSupplier`, `WriteOff`, `Relocate`,
`Count`, `Adjust`); `TASK_ABANDONED` y `NeedsRecovery` (T6); conteo cíclico y `Count` manual del supervisor; comentarios sobre una
novedad; frontend (W2) y app (A3); `docs/estado.md` (se toca en `develop` al mergear).

---

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-tasks`, rama
  `feature/wms-tasks` (T1 ya está ahí, desde `e21f40b46`). Si el owner pide rama aparte, `feature/wms-t2` desde la punta de
  `feature/wms-tasks`. `git branch --show-current` justo antes de cada commit; si dice `main` o `develop`, frena.
- **PowerShell:** sin `&&` (`A; if ($?) { B }`), `$env:VAR = "valor"` en línea aparte. `Set-Location` al worktree al empezar cada script.
- **`Api.exe` detenido** antes de `dotnet build`/`test`/`ef` (no detengas uno que no lanzaste: avisa).
- **Una sola corrida de integración a la vez en la máquina:** `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` antes de
  lanzar una; si hay otra, espera. Las pruebas corren **en primer plano**.
- Integración: Docker arriba y `$env:Seed__Enabled = "false"`. Mientras se itera, `--filter "FullyQualifiedName~<Prueba>"`; la clase
  completa una vez al final del bloque. **El RED de un fallo de compilación no levanta contenedores:** la evidencia es la salida del
  build.
- **Deuda de T1 que se paga primero (una vez, antes de la tarea 1):** el bloque B y el C de T1 no corrieron integración (ver
  «Desviaciones» del plan de T1). Corre `--filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks"` e
  `InventoryLedgerPostingTests`. Si algo falla, frena y repórtalo: T2 se apoya en esa confirmación.
- Migración: `dotnet ef migrations add AddIncidents --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations`.
- **Nunca reescribas un `.cs` con `Set-Content`/`Out-File`** (rompe tildes, deja BOM): usa la herramienta de edición.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA**, aunque el harness la sugiera.
- Comentarios y textos para personas en español con tuteo; identificadores en inglés.
- Abre `.claude/rules/wms.md` y `.claude/rules/sql-entre-modulos.md` antes de la tarea 6 (tocas `CatalogVariantUsageChecker`).

## Piezas existentes que este plan usa (verificadas en `feature/wms-tasks` `e21f40b46`)

| Pieza | Dónde | Cómo se usa |
| --- | --- | --- |
| `WarehouseTask` (`CreateMove`, `ConfirmLine(…, occurredAt, memberIsBusy)`, `CheckLease`, `CloseIfFinished`, `Record`, `EnsureStatus`) | `Modules.Wms.Domain/Tasks/WarehouseTask.cs` | `ShortLine`, `CreateCount`, `RecordCount` siguen el molde de `ConfirmLine` |
| `WarehouseTaskLine` (`Create`, `Confirm`, `Cancel`, `Remaining`) | `Modules.Wms.Domain/Tasks/WarehouseTaskLine.cs` | `CreateCount`, `MarkShort`, `RecordCount` |
| `TaskTypePolicy` (`DefaultPriority`, `LeaseDuration`, `AllowsDestinationOverride`, `MovementReasonOf`, `SkipCooldown`) | `Modules.Wms.Domain/Tasks/TaskTypePolicy.cs` | Brazo `Count` en cada método |
| `TaskTypePermissions.For`/`AllowedFor` | `Modules.Wms.Application/Tasks/TaskTypePermissions.cs` | `Count` → `WmsPermissions.CountPerform` |
| `TaskEventKind`, `TaskLineStatus`, `TaskSourceType`, `WarehouseTaskType` (enums por nombre, en orden causal) | `Modules.Wms.Domain/Tasks/` | `LineShort`, `Short`, `Incident`, `Count` |
| `ConfirmTaskLineHandler` (lock del operario → `tasks.LockAsync` → `GetAsync` → `guard.EnsureCanPerformAsync` → `OperatorTaskGuard.EnsureLease` → validar → `ConfirmLine` → una llamada al ledger → recibo → `SaveChangesAsync`) | `Modules.Wms.Application/Tasks/ConfirmTaskLine.cs` | Molde de `ShortTaskLineHandler` y `CountTaskLineHandler`; punto donde se registra `SyncConflict` |
| `OperatorTaskGuard` (`EnsureCanPerformAsync`, `EnsureLease`, `LeaseLost()`), `TaskMemberLocks.For`, `TaskDeviceRules.MustBeADeviceId` | `Modules.Wms.Application/Tasks/OperatorTaskGuard.cs` | Mismo guard; `LeaseLost` pasa a `internal` para el handler que registra el conflicto |
| `IdempotencyKeyFilter` (sin recibo, relanza el error del handler) y `IdempotencyScope.Key` | `Modules.Wms.Api/IdempotencyKeyFilter.cs`, `Modules.Wms.Application/Inventory/IdempotencyScope.cs` | La clave IK de la confirmación es la `operation_key` de la novedad; `resolve` es SK con `UseServerIdempotencyKey("incident", "resolve")` |
| `IWmsUnitOfWork.SaveChangesAsync` (con transacción abierta, además la confirma) | `Modules.Wms.Application/WmsPorts.cs`, `Infrastructure/Persistence/WmsUnitOfWork.cs` | Confirmar la novedad `SyncConflict` antes de lanzar el 409 |
| `Adjustment.Propose`/`Approve`/`Reject`/`MarkPosted`, `AdjustmentLine.Create`, `AdjustmentReason.CountVariance`, `IAdjustmentRepository.NextNumberAsync` | `Modules.Wms.Domain/Control/`, `Modules.Wms.Application/Control/IAdjustmentRepository.cs` | El conteo con diferencia propone el ajuste |
| `ApproveAdjustmentHandler`, `PostAdjustmentHandler` (`MarkPosted`), `RejectAdjustmentHandler` | `Modules.Wms.Application/Control/` | Separación de funciones y cierre de `CountVariance` |
| `InvalidTransitionException` (`wms.document.invalid_transition`, `{ from, action }`) | `Modules.Wms.Domain/Control/InvalidTransitionException.cs` | Transiciones de `Incident` (mismo área, se reutiliza) |
| `IStockReadQueries` (SQL crudo sobre saldos) | `Modules.Wms.Application/Inventory/IStockReadQueries.cs`, `Infrastructure/Persistence/StockReadQueries.cs` | Saldo exacto de una clave al contar |
| `IVariantProfileRepository.FindAsync` (`Tracking`: `None`/`Lot`/`Serial`) | `WmsPorts.cs`, `Domain/Warehousing/VariantLogisticsProfile.cs` | Lote obligatorio al contar; serie sin `Count` |
| `IWarehouseTaskRepository` (`NextNumberAsync`, `LockNextCandidateAsync` con el filtro de DA-33, `CountOpenByTypeAsync`) | `Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs`, `Infrastructure/Persistence/WarehouseTaskRepository.cs` | Numerar el `Count`, excluir a quien reportó, `FindOpenCountAsync` |
| `GetTaskBoardHandler`, `TaskBoardDto` | `Modules.Wms.Application/Tasks/GetTaskBoard.cs`, `TaskDtos.cs` | `openIncidents` |
| `WarehouseTaskDtoComposer`, `TaskLineDto` | `Modules.Wms.Application/Tasks/` | `quantityCounted`; el `Count` nunca muestra lo esperado |
| `CatalogVariantUsageChecker` (ya cuenta líneas `Open` de tareas `Planned/Ready/Assigned/InProgress`) | `src/Bootstrapper/CatalogVariantUsageChecker.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs`, `StockProbeTests` | Suma novedades abiertas con variante |
| `WmsUserReferenceProbe` (cuenta membresías en tareas) | `Modules.Wms.Infrastructure/Persistence/WmsUserReferenceProbe.cs` | Suma `incidents.reported_by`, `resolved_by`, `incident_events.member_id` |
| `QuotationFileLookup.FindManyAsync` (archivos por id con filtro de tenant y tipo de dueño) | `src/Bootstrapper/QuotationFileLookup.cs` | Molde de `WmsEvidenceLookup` sobre `FileOwnerType.WmsEvidence` |
| `WmsPersistenceTests.TheMigrationCreatesTheEightTablesWithTheirUniqueIndexes` (excluye las tablas de tareas) | `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsPersistenceTests.cs` | Excluir también las de novedades |
| `TaskWorld*` (dobles en memoria), `TaskApiSupport` (`SeedOperatorAsync`, `CreateAsync`, `ActionRawAsync`), `TaskTestSupport` (`NewMove`, `SaveAsync`, `ReloadAsync`), `WmsLedgerSeeding.ArrangeAsync`, `StockAsync`, `OnHandAsync` | `tests/Modules/Wms/…/Tasks/`, `WmsApiHarness*.cs` | Arranque de las pruebas |

## Global Constraints

- **La novedad se crea en la misma transacción que el hecho que la origina**, con la misma clave de idempotencia (spec §8.4 regla 1). El
  `Count` que genera, también. Si algo no se guarda, no se guarda nada — **salvo `SyncConflict`**, que existe justamente porque el hecho
  se rechaza (DA-48).
- **El faltante nunca se ajusta en el piso** (DA-18): `short` mueve sólo lo encontrado; la diferencia la propone el conteo como
  `Adjustment` y la aplica quien tenga `wms.adjustment.approve`.
- **Orden de bloqueos** (extiende el de T1): advisory del operario (`task-member:{id}`) → fila de la tarea (`FOR UPDATE`) → advisory de
  la clave contada (`count-key:{tenant}:{location}:{variant}`) → numeraciones (`incident-seq:`, `task-seq:`, la de ajustes) → saldos
  (dentro del ledger). Ningún camino toma una numeración y después una tarea.
- **Un `Count` es a ciegas:** ningún DTO que reciba el operario lleva lo esperado. La línea de un `Count` tiene `quantityPlanned = 0` y
  lo esperado se calcula en el servidor al contar (DA-39).
- Acciones del supervisor sobre una novedad: `version` (412) y auditoría `wms.incident.*`. Las del operario: `incident_events`, sin
  auditoría (DA-50, mismo criterio que DA-27).
- 403 siempre para otro tenant; `wms.incident.not_found` (404) sólo dentro del tenant.
- Enums por nombre en la API y como texto en la base; cantidades `numeric(18,6)` en unidad base.

## Decisiones del plan (donde el spec dejaba margen)

Las que cambian contrato o comportamiento están en spec §13 como DA-38 a DA-51. Las de implementación:

1. **`Incident` en `Control`**, no un área nueva: así `WmsLayerTests` no cambia (ningún área nueva, ninguna referencia nueva entre áreas).
   Las transiciones reutilizan `InvalidTransitionException` (`wms.document.invalid_transition`), como `Adjustment`.
2. **El catálogo es código:** `IncidentCatalog` (dominio, estático) dice por tipo qué resoluciones admite, si genera `Count`, y en qué
   paso se puede declarar (`short` de un `Move`, `POST /incidents`, sólo sistema).
3. **Lo que un conteo verifica se enlaza en la novedad** (`verification_task_id`), no en la tarea: la tarea sólo guarda
   `source_type = Incident` y `source_id` de la primera novedad que la originó.
4. **El 409 de `SyncConflict`:** el handler agrega la novedad, guarda (confirma la transacción, que no tenía otro cambio) y después lanza
  `wms.task.lease_lost`. El filtro IK no encuentra recibo y relanza el 409. El reintento con la misma clave encuentra la novedad por
  `operation_key` bajo el lock del operario y no la duplica.
5. **Numeración** `NOV-yyyyMMdd-NNNN` con `LockAsync("incident-seq:{tenantId}")` y `count(*) … LIKE`, copia de `ADJ-`/`TSK-`.

## Mapa de archivos

| Archivo | Bloque | Qué |
| --- | --- | --- |
| `src/Modules/Wms/Modules.Wms.Domain/Tasks/*.cs` | A | `Count`, `Short`, `LineShort`, `Incident` (fuente), `ShortLine`, `CreateCount`, `RecordCount` |
| `src/Modules/Wms/Modules.Wms.Domain/Control/Incident*.cs` | A | `Incident`, `IncidentEvidence`, `IncidentEvent`, enums, `IncidentCatalog` |
| `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs` | A | `wms.incident.*` |
| `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskTypePermissions.cs` | A | `Count` → `CountPerform` |
| `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/*.cs`, `Control/IncidentTests.cs` | A | Dominio |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/IncidentModel.cs`, `IncidentRepository.cs`, `WarehouseTaskModel.cs`, `WarehouseTaskRepository.cs`, `WmsDbContext.cs`, `WmsUnitOfWork.cs`, `StockReadQueries.cs`, `WmsUserReferenceProbe.cs` | B | Persistencia |
| `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddIncidents*.cs` | B | Migración generada |
| `src/Modules/Wms/Modules.Wms.Application/Control/IIncidentRepository.cs`, `Inventory/IStockReadQueries.cs`, `Tasks/IWarehouseTaskRepository.cs` | B | Puertos |
| `src/Bootstrapper/CatalogVariantUsageChecker.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs` | B | Uso de variante |
| `tests/Modules/Wms/Modules.Wms.IntegrationTests/Control/IncidentPersistenceTests.cs`, `Tasks/TaskCandidateQueryTests.cs`, `WmsPersistenceTests.cs`, `StockProbeTests.cs` | B | Integración |
| `src/Modules/Wms/Modules.Wms.Application/Control/Incident*.cs`, `Tasks/ShortTaskLine.cs`, `Tasks/CountTaskLine.cs`, `Tasks/TaskIncidents.cs`, `Tasks/ConfirmTaskLine.cs`, `Tasks/GetTaskBoard.cs`, `Control/ApproveAdjustment.cs`, `PostAdjustment.cs`, `RejectAdjustment.cs`, `WmsPorts.cs`, `WmsSupport.cs` | C | Casos de uso |
| `src/Modules/Wms/Modules.Wms.Api/IncidentEndpoints.cs`, `TaskEndpoints.cs`, `WmsEndpoints.cs` | C | Rutas |
| `src/Bootstrapper/WmsEvidenceLookup.cs`, `QepServiceCollectionExtensions.cs` | C | Adaptador y registros |
| `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskShortApiTests.cs`, `Tasks/TaskCountApiTests.cs`, `Tasks/TaskSyncConflictApiTests.cs`, `Control/IncidentApiTests.cs`, `Control/CountVarianceAdjustmentTests.cs` | C | Integración |
| `README.md` (§ Módulo Wms), `.claude/rules/wms.md` | C | Contrato y reglas |

---

# Bloque A — Dominio (tareas 1 a 4, un implementador)

### Task 1: Vocabulario de T2 en el motor y política del tipo `Count`

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTaskType.cs` (+ `Count`), `TaskLineStatus.cs` (+ `Short`, entre `Done` y
  `Cancelled`), `TaskEventKind.cs` (+ `LineShort` después de `LineConfirmed`: el enum va en orden causal y la ficha ordena por él),
  `TaskSourceType.cs` (+ `Incident`)
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/TaskTypePolicy.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskTypePermissions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/TaskTypePolicyTests.cs` (nuevo), `TaskRoutesTests.cs` o un `TaskTypePermissionsTests.cs` nuevo

- [ ] **RED.** `TaskTypePolicyTests`, una prueba por regla de §6.2:

  ```csharp
  [Fact]
  public void ACountLastsThirtyMinutesHasPriorityThreeAndNoDestinationOverride()
  {
      Assert.Equal(TimeSpan.FromMinutes(30), TaskTypePolicy.LeaseDuration(WarehouseTaskType.Count));
      Assert.Equal(3, TaskTypePolicy.DefaultPriority(WarehouseTaskType.Count));
      Assert.False(TaskTypePolicy.AllowsDestinationOverride(WarehouseTaskType.Count));
      Assert.True(TaskTypePolicy.ConfirmsByCount(WarehouseTaskType.Count));
      Assert.False(TaskTypePolicy.ConfirmsByCount(WarehouseTaskType.Move));
  }

  // Un conteo nunca mueve stock: el movimiento lo hace el ajuste que propone.
  [Fact]
  public void ACountHasNoMovementReason() =>
      Assert.Throws<InvalidOperationException>(() => TaskTypePolicy.MovementReasonOf(WarehouseTaskType.Count));

  // Un tipo nuevo entra en cada método: esta prueba se pone en rojo si alguno no lo cubre.
  [Theory]
  [MemberData(nameof(AllTypes))]
  public void EveryTypeIsCoveredByEveryPolicy(WarehouseTaskType type) { /* llama los cuatro métodos y ConfirmsByCount; MovementReasonOf sólo si !ConfirmsByCount */ }
  ```

  y en permisos: `TaskTypePermissions.For(WarehouseTaskType.Count) == WmsPermissions.CountPerform`; `AllowedFor` con un contexto que
  sólo tiene `CountPerform` devuelve `[Count]`.
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: falla (`Count`, `ConfirmsByCount` no existen). Guarda la salida.
- [ ] **GREEN.** Agrega los valores de enum (comentario «se persiste por nombre; no se renombra» ya está en cada archivo). En
  `TaskTypePolicy`: `Count => 3`, `Count => TimeSpan.FromMinutes(30)`, `Count => false`, `Count => throw new InvalidOperationException("A count never moves stock.")`,
  y `public static bool ConfirmsByCount(WarehouseTaskType type) => type switch { Move => false, Count => true, _ => throw … }`. En
  `TaskTypePermissions.For`: `WarehouseTaskType.Count => WmsPermissions.CountPerform`.
- [ ] `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~TaskTypePolicyTests|FullyQualifiedName~TaskTypePermissions"`: verde.
- [ ] Commit: `feat(wms): add the count task type and the short line vocabulary`

### Task 2: Faltante en una línea (`ShortLine`) y cierre que cuenta `Short`

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs`, `WarehouseTaskLine.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskShortTests.cs`

API que se agrega:

```csharp
// WarehouseTaskLine
internal void MarkShort();   // la línea queda Short con lo confirmado hasta ahora; ya no admite más confirmaciones

// WarehouseTask
/// <summary>Declara que de lo que falta de la línea sólo había <paramref name="quantityFound"/> (puede ser cero). Lo encontrado se
/// confirma como en ConfirmLine; la línea queda Short y la tarea se cierra si era la última abierta. Mismo lease, misma reanudación y
/// misma regla de DA-32 que ConfirmLine.</summary>
public WarehouseTaskLine ShortLine(
    Guid memberId, Guid leaseToken, string deviceId, Guid lineId, decimal quantityFound,
    DateTimeOffset now, DateTimeOffset? occurredAt = null, bool memberIsBusy = false);
```

- [ ] **RED.** `WarehouseTaskShortTests` (fixtures de `WarehouseTaskLeaseTests`: tarea `Move` de una línea de 5, reclamada):
  - `ShortingWithTwoFoundConfirmsTwoAndClosesTheLineAsShort`: `ShortLine(…, quantityFound: 2)` → línea `Short`, `QuantityConfirmed = 2`,
    eventos `LineConfirmed` (cantidad 2) y `LineShort` (cantidad 3, lo que faltó), tarea `Done` con `CompletedAt` y evento `Completed`.
  - `ShortingWithNothingFoundClosesTheTaskAsDone`: `quantityFound: 0` → sin `LineConfirmed`, `LineShort` con 5, tarea `Done` (spec §6.3:
    «`Done` si alguna línea quedó `Done` o `Short`»).
  - `AShortLineWithAnotherCancelledStillClosesDone`: dos líneas; cancelar una y declarar faltante en la otra → `Done`.
  - `ShortingAfterAPartialConfirmationOnlyCountsTheRest`: confirmar 1, faltante con 1 encontrado → confirmado 2, `LineShort` con 3.
  - `FindingEverythingIsNotAShort`: `quantityFound == Remaining` → `WmsDomainException` `TaskLineInvalid` (el operario confirma).
  - `FindingMoreThanRemainingIs422` → `TaskQuantityExceedsRemaining`; negativo → `TaskLineInvalid`.
  - `ShortingAClosedLineIsAnInvalidTransition` (`TaskTransitionException` con acción `short-line`).
  - `ConfirmingAShortLineIsAnInvalidTransition` (`ConfirmLine` sobre una `Short` → acción `confirm-line`).
  - `AResumableLeaseResumesBeforeShorting` (evento `LeaseResumed` antes de `LineShort`); `ALostLeaseThrowsInvalidOperation`.
  - `ShortingACountLineIsAnInvalidTransition` (preparada con la tarea 3: déjala `[Fact(Skip = "tarea 3")]` hasta entonces o escríbela en la 3).
  - `ShortEventsCarryTheScanTime` (`occurredAt` en `LineConfirmed`, `LineShort` y `Completed`, DA-36).
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.UnitTests`: falla (`ShortLine` no existe). Guarda la salida.
- [ ] **GREEN.** Extrae de `ConfirmLine` un privado `PrepareLineWrite(memberId, token, deviceId, lineId, action, now, occurredAt,
  memberIsBusy)` que hace `CheckLease` (`Lost` → `InvalidOperationException`), `RequiredDevice`, `LineOf`, línea `Open` (si no,
  `TaskTransitionException(line.Status, action)`), ventana de `occurredAt` y devuelve `(line, lease, device, scannedAt)`; la reanudación
  y `Renew` van en otro privado `ApplyLease(lease, memberIsBusy, memberId, device, scannedAt, now)`. `ConfirmLine` queda con el mismo
  comportamiento (las pruebas de T1 siguen verdes). `ShortLine`: valida `0 ≤ quantityFound < Remaining`; si `> 0` hace `line.Confirm`
  y registra `LineConfirmed`; `line.MarkShort()`; `Version++`; `LineShort` con `quantity = Remaining` previo al `MarkShort`; cierre.
  `CloseIfFinished`: la condición de `Done` pasa a `line.Status is TaskLineStatus.Done or TaskLineStatus.Short` y su comentario lo dice.
- [ ] `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Tasks"`: verde (incluidas las de T1).
- [ ] Commit: `feat(wms): let an operator declare a short line`

### Task 3: El `Count` de verificación: creación y registro del conteo

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Tasks/WarehouseTask.cs`, `WarehouseTaskLine.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Tasks/WarehouseTaskCountTests.cs`

API que se agrega:

```csharp
// WarehouseTaskLine
public decimal? QuantityCounted { get; private set; }                 // null hasta contar
/// <summary>La línea de un conteo: una clave (ubicación, variante, lote, estado) sin destino y con lo planeado en cero. Lo esperado
/// no vive en la línea: el servidor lo calcula al contar, y así nada que llegue al operario lo revela (DA-39).</summary>
public static WarehouseTaskLine CreateCount(Guid variantId, LotId? lotId, StockStatus stockStatus, LocationId location);
internal void RecordCount(decimal counted, LotId? lot);              // fija QuantityCounted, LotId ??= lot, Status = Done

// WarehouseTask
public static WarehouseTask CreateCount(
    Guid tenantId, string number, WarehouseId warehouse, OwnerId owner, IReadOnlyList<WarehouseTaskLine> lines,
    TaskSourceType sourceType, Guid sourceId, DateTimeOffset now);  // publicada (Ready), CreatedBy = Guid.Empty (sistema, §6.1)
public WarehouseTaskLine RecordCount(
    Guid memberId, Guid leaseToken, string deviceId, Guid lineId, decimal quantityCounted, LotId? lotId,
    DateTimeOffset now, DateTimeOffset? occurredAt = null, bool memberIsBusy = false);
```

- [ ] **RED.** `WarehouseTaskCountTests`:
  - `ACountIsBornReadyFromTheSystemWithItsSource`: `Type = Count`, `Status = Ready`, `ReleasedAt = now`, `Priority = 3`, `SourceType =
    Incident`, `SourceId`, `CreatedBy = Guid.Empty`, `Version = 1`, eventos `Created`, `Published` con `memberId` nulo.
  - `ACountLineHasNoDestinationAndNothingPlanned`: `ToLocationId` nulo, `QuantityPlanned = 0`, `Remaining = 0`, `Open`.
  - `ACountNeedsBetweenOneAndOneHundredLines` (`TaskLineInvalid`); `ACountLineIsNeverAMoveLine`: `CreateMove` con una línea de
    `CreateCount` → `TaskLocationInvalid` (ya lo da la regla de destino nulo: la prueba lo fija).
  - `ClaimingACountGivesAThirtyMinuteLease`.
  - `RecordingACountClosesTheLineAndTheTask`: `RecordCount(…, 7, null)` → línea `Done`, `QuantityCounted = 7`, `QuantityConfirmed = 0`,
    evento `LineConfirmed` con `quantity = 7` y `detail = "count"`, tarea `Done`.
  - `CountingZeroIsValid` (`QuantityCounted = 0`, línea `Done`).
  - `ANegativeCountIs422LineInvalid`.
  - `TheScannedLotFillsALineWithoutLot` y `AScannedLotOnALineWithAnotherLotIs422LotMismatch` (`WmsErrorCodes.TaskLotMismatch`).
  - `ConfirmingACountLineIsAnInvalidTransition` (`ConfirmLine` → `TaskTransitionException` acción `confirm-line`) y
    `CountingAMoveLineIsAnInvalidTransition` (`RecordCount` sobre `Move` → acción `count-line`). Activa la de la tarea 2
    (`ShortingACountLineIsAnInvalidTransition`, acción `short-line`).
  - `RecordingWithALostLeaseThrowsInvalidOperation`; `AResumableLeaseResumes`.
- [ ] `dotnet build`: falla (RED de compilación). Guarda la salida.
- [ ] **GREEN.** `CreateCount` reutiliza las validaciones de `CreateMove` (líneas 1..`MaxLines`, prioridad) extrayendo un privado
  `NewTask(type, …)`; exige `lines.All(line => line.ToLocationId is null && line.QuantityPlanned == 0)` (`TaskLineInvalid`) y llama
  `Release`. `ConfirmLine` y `ShortLine` lanzan `TaskTransitionException(Status, "confirm-line"/"short-line")` si
  `TaskTypePolicy.ConfirmsByCount(Type)`; `RecordCount` lo exige al revés (`"count-line"`). `RecordCount` usa `PrepareLineWrite` y
  `ApplyLease` de la tarea 2. `WarehouseTaskLine.Create` (líneas de `Move`) no cambia: sigue exigiendo cantidad `> 0`.
- [ ] `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Tasks"`: verde.
- [ ] Commit: `feat(wms): model the blind verification count task`

### Task 4: `Incident` en el área `Control`

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Control/IncidentType.cs`, `IncidentStatus.cs`, `IncidentResolution.cs`,
  `IncidentEventKind.cs`, `IncidentEvidenceKind.cs`, `IncidentCatalog.cs`, `IncidentEvidence.cs`, `IncidentEvent.cs`, `Incident.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Control/IncidentTests.cs`, `IncidentCatalogTests.cs`

Vocabulario (DA-42; todos «se persiste por nombre; no se renombra»):

```csharp
public enum IncidentType { ShortMove, LocationMismatch, UnreadableLabel, CountVariance, SyncConflict }
public enum IncidentStatus { Open, InReview, Resolved, Rejected }
public enum IncidentResolution { Accept, AcceptIntoStock }
public enum IncidentEventKind { Reported, InReview, Resolved, Rejected }
public enum IncidentEvidenceKind { Photo, Scan }

public static class IncidentCatalog
{
    public static IReadOnlySet<IncidentResolution> ResolutionsOf(IncidentType type);   // §8.2: ShortMove, LocationMismatch, UnreadableLabel → Accept; SyncConflict → AcceptIntoStock; CountVariance → ninguna (la cierra el ajuste)
    public static bool CanBeRejected(IncidentType type);                               // SyncConflict (no metió nada al ledger)
    public static bool RequestsVerification(IncidentType type);                         // ShortMove, LocationMismatch → Count
    public static IReadOnlySet<IncidentType> DeclarableOnShort(string taskType);       // "Move" → { ShortMove, LocationMismatch }; otro → vacío
    public static IReadOnlySet<IncidentType> DeclarableStandalone { get; }              // { UnreadableLabel, LocationMismatch }
}
```

`DeclarableOnShort` recibe el tipo de tarea por **nombre** (`string`) y no por `WarehouseTaskType`: `Control` no puede ver `Tasks`
(`WmsLayerTests.DomainAreasOnlyDependDownwards`). El handler pasa `task.Type.ToString()`.

Agregado (propiedades `{ get; private set; }`, ctor privado, `Version++` por transición, como `Adjustment`):

```csharp
public sealed class Incident
{
    public const int NoteMaxLength = 500;
    public const int RawCodeMaxLength = 200;
    public const int MaxEvidence = 5;
    // Id, TenantId, WarehouseId, OwnerId, Number, Type, Status, SourceTaskId?, SourceTaskLineId?, VerificationTaskId?,
    // LocationId?, VariantId?, LotId?, StockStatus?, ExpectedQty?, ActualQty?, RawCode?, RawPayload? (JSON crudo de SyncConflict),
    // Note?, ReportedBy (membresía; Guid.Empty = sistema), DeviceId?, ReportedAt, OperationKey?, Resolution?, ResolvedBy?,
    // ResolvedAt?, ResolutionNote?, BatchId?, AdjustmentId?, ParentIncidentId?, Version; Evidence, Events (sólo se agregan)

    public static Incident Report(Guid tenantId, string number, WarehouseId warehouse, OwnerId owner, IncidentType type,
        IncidentSubject subject, string? note, string? rawCode, string? rawPayload, IReadOnlyList<IncidentEvidence> evidence,
        Guid reportedBy, string? deviceId, string? operationKey, DateTimeOffset reportedAt, DateTimeOffset now);
    public void LinkVerification(Guid countTaskId);
    public void AttachAdjustment(Guid adjustmentId);
    public void Review(Guid by, DateTimeOffset now);                                   // Open → InReview
    public void Resolve(IncidentResolution resolution, Guid by, string? note, Guid? batchId, Guid? parentIncidentId, DateTimeOffset now);
    public void Reject(Guid by, string note, DateTimeOffset now);
    public void EnsureNotSelfResolution(IncidentResolution resolution, Guid by);       // DA-17/DA-47
}

public sealed record IncidentSubject(Guid? SourceTaskId, Guid? SourceTaskLineId, LocationId? Location, Guid? VariantId,
    LotId? LotId, StockStatus? StockStatus, decimal? ExpectedQty, decimal? ActualQty);
```

Códigos nuevos en `WmsErrorCodes` (bloque «Novedades (spec 2026-11-26 §8)»): `IncidentNotFound = "wms.incident.not_found"` (404),
`IncidentSelfResolution = "wms.incident.self_resolution"` (422, spec §8.4), `IncidentResolutionNotAllowed =
"wms.incident.resolution_not_allowed"` (422), `IncidentTypeNotAllowed = "wms.incident.type_not_allowed"` (422),
`IncidentEvidenceInvalid = "wms.incident.evidence_invalid"` (422), `IncidentInvalid = "wms.incident.invalid"` (422: nota, código o
cantidades fuera de rango; el validador da además el mapa `errors`).

- [ ] **RED.** `IncidentCatalogTests`: una prueba por fila de la tabla de arriba, más `EveryTypeHasAnEntry` (recorre
  `Enum.GetValues<IncidentType>()` y llama los cuatro métodos: un tipo nuevo sin fila se pone en rojo).
  `IncidentTests`:
  - `ReportingIsOpenWithVersionOneAndAReportedEvent` (número, sujeto, `ReportedAt` = el del escaneo, `Events = [Reported]`).
  - `TheNoteHasAtMostFiveHundredCharacters` y `MoreThanFiveEvidenceFilesIs422EvidenceInvalid`.
  - `ReviewMovesOpenToInReview`; `ReviewingTwiceIsAnInvalidTransition` (`InvalidTransitionException`, `from = "InReview"`).
  - `ResolvingWithAResolutionTheTypeDoesNotAllowIs422` (`ShortMove` + `AcceptIntoStock`; `CountVariance` + `Accept` desde el endpoint).
  - `ResolvingFromOpenOrInReviewIsTerminal` (un segundo `Resolve` o `Reject` → transición inválida).
  - `OnlyAnIncidentThatMovedNothingCanBeRejected` (`SyncConflict` sí; `ShortMove` → `IncidentResolutionNotAllowed`).
  - `TheReporterCannotAcceptIntoStockTheirOwnIncident` (`EnsureNotSelfResolution` → `IncidentSelfResolution`); `Accept` del mismo
    reportante sí pasa.
  - `TheSystemCanResolveACountVarianceWithItsBatch`: `Resolve(Accept, Guid.Empty, …, batchId, null, now)` es el camino interno que usa
    el posteo del ajuste: la regla «`CountVariance` no tiene resolución» vale sólo para el endpoint (ver el handler en la tarea 10), así
    que el dominio expone `ResolveByAdjustment(Guid batchId, Guid by, string adjustmentNumber, DateTimeOffset now)` y
    `RejectByAdjustment(Guid by, string note, DateTimeOffset now)`, y `Resolve` lanza `IncidentResolutionNotAllowed` para `CountVariance`.
  - `ResolvingAChildRecordsItsParent` (`ParentIncidentId`).
- [ ] `dotnet build`: falla (RED de compilación). Guarda la salida.
- [ ] **GREEN.** Implementa. `Report` recorta `note`/`rawCode`, valida longitudes con `IncidentInvalid`, `MaxEvidence` con
  `IncidentEvidenceInvalid`, fija `Id = Guid.CreateVersion7()`, `Status = Open`, `Version = 1`, evento `Reported` con `reportedAt`. Cada
  transición: `EnsureStatus` → muta → `Version++` → evento. `IncidentEvidence.Create(Guid fileId, IncidentEvidenceKind kind, string?
  rawValue)`, `IncidentEvent.Record(tenantId, incidentId, kind, Guid? memberId, string? detail, DateTimeOffset at)`.
- [ ] `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~Control.Incident"`: verde.
- [ ] Commit: `feat(wms): model the unified incident with its closed catalog`

**Cierre del bloque A:** `Modules.Wms.UnitTests` completo y `WmsLayerTests` (debe seguir verde **sin modificarse**: si pide cambios,
frena; `Incident` no puede referenciar `Tasks`). Revisión del bloque (un revisor, lectura): §6.3 (cierre con `Short`), §8.2 y §8.4
contra las pruebas, y que ningún DTO futuro tenga de dónde sacar lo esperado de un `Count`.

---

# Bloque B — Persistencia, migración y consultas (tareas 5 y 6, un implementador)

### Task 5: Migración `AddIncidents`, modelo EF y repositorio

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Control/IIncidentRepository.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/IncidentModel.cs`, `IncidentRepository.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskModel.cs`, `WmsDbContext.cs`, `WmsUnitOfWork.cs`, `WmsUserReferenceProbe.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/WmsInfrastructureExtensions.cs`
- Create (generada): `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddIncidents.cs` (+ Designer, snapshot)
- Modify: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsPersistenceTests.cs` (excluye `incidents`, `incident_evidence`, `incident_events`)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Control/IncidentPersistenceTests.cs`, `Tasks/WarehouseTaskPersistenceTests.cs` (casos nuevos), `WmsUserReferenceProbeTests` (caso nuevo)

Puerto:

```csharp
public sealed record IncidentPage(IReadOnlyList<Incident> Items, int TotalCount);

public interface IIncidentRepository
{
    void Add(Incident incident);
    /// <summary>Con evidencia, sin eventos, rastreada.</summary>
    Task<Incident?> GetAsync(Guid tenantId, Guid id, CancellationToken ct);
    /// <summary>Con evidencia y eventos, sin rastreo: la ficha.</summary>
    Task<Incident?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<IncidentPage> ListAsync(Guid tenantId, Guid? warehouseId, IncidentStatus? status, IncidentType? type, int page, int pageSize, CancellationToken ct);
    /// <summary>Las abiertas (Open o InReview) de la bodega: el tablero.</summary>
    Task<int> CountOpenAsync(Guid tenantId, Guid warehouseId, CancellationToken ct);
    /// <summary>Rastreadas: las que esperan la verificación de ese conteo, abiertas.</summary>
    Task<IReadOnlyList<Incident>> ListOpenVerifiedByAsync(Guid tenantId, Guid countTaskId, CancellationToken ct);
    /// <summary>Rastreada: la CountVariance que propuso ese ajuste.</summary>
    Task<Incident?> FindByAdjustmentAsync(Guid tenantId, Guid adjustmentId, CancellationToken ct);
    /// <summary>Sin rastreo: las hijas de una CountVariance (separación de funciones).</summary>
    Task<IReadOnlyList<Guid>> ListReportersOfChildrenAsync(Guid tenantId, Guid parentIncidentId, CancellationToken ct);
    /// <summary>La que ya registró esa Idempotency-Key (SyncConflict).</summary>
    Task<bool> ExistsWithOperationKeyAsync(Guid tenantId, string operationKey, CancellationToken ct);
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken ct);
}
```

- [ ] **RED.** `IncidentPersistenceTests` (`OpenAsync` + `ArrangeAsync`):
  - `AnIncidentRoundTripsWithEvidenceAndEvents` (todas las columnas, incluido `raw_payload` JSON).
  - `TheOperationKeyIsUniquePerTenant`: dos novedades con la misma clave en el mismo tenant → la segunda falla con
    `IX_incidents_operation_key` (traducida a `RequestConcurrencyException`); en otro tenant, pasa.
  - `NumbersAreSequentialPerTenantAndDay` (`NOV-yyyyMMdd-0001`, `…-0002`).
  - `TheDatabaseRejectsAnUnknownType` (`UPDATE wms.incidents SET type = 'Nope'` → `23514`, `CK_incidents_type`).
  - `DeletingALocationUsedByAnIncidentIs422HasStock` (`FK_incidents_location` en `LocationReferenceForeignKeys`).
  En `WarehouseTaskPersistenceTests`: `ACountTaskWithAShortAndACountedLineRoundTrips` (tipo `Count`, línea con `quantity_planned = 0`,
  `quantity_counted`, `to_location_id` nulo; otra tarea `Move` con línea `Short`) y `TheDatabaseRejectsANegativeCount` (`23514`,
  `CK_task_lines_quantity`). En `WmsUserReferenceProbeTests`: una membresía que sólo figura como `reported_by` cuenta como referenciada.
- [ ] `dotnet build tests/Modules/Wms/Modules.Wms.IntegrationTests`: falla (RED de compilación).
- [ ] **GREEN.**
  - `WarehouseTaskModel`: `CK_tasks_type` → `type IN ('Move','Count')`; `CK_task_lines_status` → `line_status IN
    ('Open','Done','Short','Cancelled')`; `CK_task_lines_quantity` → `quantity_planned >= 0 AND quantity_confirmed >= 0 AND
    quantity_confirmed <= quantity_planned AND (quantity_counted IS NULL OR quantity_counted >= 0)` (DA-39: sólo se relaja; ningún pod
    viejo escribe `0`); columna `quantity_counted numeric(18,6) null`. `CK_tasks_status` y `IX_tasks_pool` **no cambian**: T2 no agrega
    estados de tarea, así que tampoco cambian `CountOpenByTypeAsync` (excluye `Done`/`Cancelled`) ni la lista de estados de
    `CatalogVariantUsageChecker`; un comentario en cada uno lo dice.
  - `IncidentModel.Configure(ModelBuilder)` (internal static, llamado desde `WmsDbContext.OnModelCreating` después de
    `WarehouseTaskModel.Configure`): tablas `incidents`, `incident_evidence`, `incident_events` con las columnas de la tarea 4 en
    `snake_case`; enums `HasConversion<string>()` `HasMaxLength(32)`; `raw_payload` `jsonb`; `version` `bigint` token; cantidades
    `HasPrecision(18, 6)`; FKs con nombre `FK_incidents_warehouse`, `FK_incidents_owner`, `FK_incidents_location`, `FK_incidents_lot`,
    `FK_incidents_parent` (RESTRICT), `FK_incident_evidence_incident` y `FK_incident_events_incident` (cascade). Sin FK a `tasks` ni a
    `adjustments` (se enlazan por `Guid`, como el `source_id` de la tarea); índices `IX_incidents_tenant_number` único,
    `IX_incidents_operation_key` único `(tenant_id, operation_key) WHERE operation_key IS NOT NULL`, `IX_incidents_open` `(tenant_id,
    warehouse_id, status) WHERE status IN ('Open','InReview')`, `IX_incidents_verification` `(verification_task_id) WHERE
    verification_task_id IS NOT NULL`, `IX_incidents_adjustment` `(adjustment_id) WHERE adjustment_id IS NOT NULL`,
    `IX_incident_events_incident` `(incident_id, occurred_at)`. CHECKs `CK_incidents_type`, `CK_incidents_status`,
    `CK_incidents_resolution` (`resolution IS NULL OR resolution IN ('Accept','AcceptIntoStock')`), `CK_incidents_resolved`
    (`status <> 'Resolved' OR (resolution IS NOT NULL AND resolved_at IS NOT NULL)`), `CK_incidents_quantities` (no negativas).
  - Constantes en `WmsDbContext`: `IncidentNumberIndex`, `IncidentOperationKeyIndex`; `FK_incidents_location` a
    `LocationReferenceForeignKeys`, `FK_incidents_owner` a `OwnerReferenceForeignKeys`.
  - `WmsUnitOfWork.PersistAsync`: `IncidentNumberIndex` e `IncidentOperationKeyIndex` → `RequestConcurrencyException("concurrency.conflict", …)`
    (último recurso: el lock de numeración y el del operario serializan antes).
  - `WmsUserReferenceProbe`: suma `incidents.reported_by`, `incidents.resolved_by` e `incident_events.member_id` (precedente: la
    desviación del bloque B de T1 con las tareas).
  - `IncidentRepository` (molde `AdjustmentRepository`/`WarehouseTaskRepository`): `NextNumberAsync` con `"incident-seq:" + tenantId` y
    prefijo `NOV-`; `ListAsync` más recientes primero con `WmsPaging`.
  - Registra `IIncidentRepository` en `AddWmsInfrastructure`.
  - Genera la migración. Revisa el `Up`: los tres `DropCheckConstraint`/`AddCheckConstraint` de tareas, la columna nueva y las tres
    tablas. El `Down` que genera EF vuelve a los CHECK de T1: **antes de bajar hay que haber borrado las tareas `Count` y las líneas
    `Short`**; deja un comentario en el `Down` que lo diga (bajar con datos falla con `23514`, que es lo correcto).
- [ ] `--filter "FullyQualifiedName~IncidentPersistenceTests|FullyQualifiedName~WarehouseTaskPersistenceTests|FullyQualifiedName~WmsUserReferenceProbeTests|FullyQualifiedName~WmsPersistenceTests"`: verde.
- [ ] Commit: `feat(wms): persist incidents and the count and short task lines`

### Task 6: Consultas que T2 necesita: saldo exacto, conteo abierto, candidata sin el reportante y uso de variante

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Inventory/IStockReadQueries.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/StockReadQueries.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/IWarehouseTaskRepository.cs`, `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseTaskRepository.cs`
- Modify: `src/Bootstrapper/CatalogVariantUsageChecker.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockReadQueriesTests.cs` (o la clase que ya cubre `StockReadQueries`), `Tasks/TaskCandidateQueryTests.cs`, `StockProbeTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs`

```csharp
// IStockReadQueries
/// <summary>El saldo físico de exactamente esa clave, sin unidad logística: el lote nulo es «sin lote», no «cualquiera»
/// (lot_id IS NOT DISTINCT FROM). Cero si no hay fila.</summary>
Task<decimal> FindOnHandAsync(Guid tenantId, StockKey key, CancellationToken cancellationToken);

// IWarehouseTaskRepository
/// <summary>El Count abierto (ni Done ni Cancelled) que verifica esa clave, rastreado; null si no hay (DA-44).</summary>
Task<WarehouseTask?> FindOpenCountAsync(Guid tenantId, Guid warehouseId, Guid ownerId, Guid locationId, Guid variantId,
    Guid? lotId, StockStatus stockStatus, CancellationToken cancellationToken);
```

- [ ] **RED.**
  - `FindOnHandAsyncReadsTheExactKey`: saldo 10 sin lote y 4 con lote L1 en el mismo bin → sin lote 10, con L1 4, con otro estado 0.
  - `TaskCandidateQueryTests.TheReporterIsNotOfferedTheCountThatVerifiesTheirIncident` (DA-45): `Count` en el pool, novedad con
    `verification_task_id` = ese `Count` y `reported_by` = el operario → `LockNextCandidateAsync` devuelve `null` para él y la tarea
    para otro; asignada a él, se la devuelve.
  - `FindOpenCountAsyncIgnoresClosedCountsAndOtherKeys`.
  - `CatalogVariantUsageCheckerTests.AnOpenIncidentWithTheVariantCountsAsUse` (réplica a mano de `wms.incidents` con sólo las columnas
    que la consulta lee: `tenant_id`, `variant_id`, `status`) y `AResolvedIncidentDoesNot`.
  - `StockProbeTests.TheRealCheckerSeesAnOpenIncidentAgainstTheMigratedSchema` (DA-51) y
    `TheRealCheckerSeesAnOpenCountLineAgainstTheMigratedSchema` (la línea `Open` de un `Count` ya la cubre la consulta de T1: la prueba
    lo fija).
- [ ] `dotnet build`: falla (RED de compilación).
- [ ] **GREEN.**
  - `FindOnHandAsync`: `SELECT COALESCE(SUM(on_hand), 0) FROM wms.stock_balances WHERE tenant_id = … AND owner_id = … AND location_id
    = … AND variant_id = … AND lot_id IS NOT DISTINCT FROM … AND status = … AND handling_unit_id IS NULL`.
  - `LockNextCandidateAsync`: en el mismo `WHERE`, junto al filtro de DA-33, `AND (t.assigned_member_id = {memberId} OR NOT EXISTS
    (SELECT 1 FROM wms.incidents i WHERE i.verification_task_id = t.id AND i.reported_by = {memberId}))`. El comentario encima dice que
    es DA-45 y que la subconsulta no bloquea nada (el `FOR UPDATE` es de `t`).
  - `CatalogVariantUsageChecker`: un `UNION` más, `SELECT i.variant_id FROM wms.incidents i WHERE i.tenant_id = {tenantId} AND i.status
    IN ('Open', 'InReview') AND i.variant_id = ANY({ids})`, y un párrafo en el resumen de la clase (por qué: un `SyncConflict` abierto
    puede escribir al ledger al resolverse; qué prueba lo amarra). Regla `sql-entre-modulos`: el nombre de las columnas queda amarrado
    por las dos pruebas.
- [ ] `--filter` por cada prueba nueva, y `CatalogVariantUsageCheckerTests` completa (`Bootstrapper.UnitTests`): verdes.
- [ ] Commit: `feat(wms): read exact balances and keep reporters off their own verification count`

**Cierre del bloque B:** una vez `--filter "FullyQualifiedName~Modules.Wms.IntegrationTests.Tasks|FullyQualifiedName~Modules.Wms.IntegrationTests.Control"`,
`WmsPersistenceTests`, `WmsUnitOfWorkTests`, `StockProbeTests` y `CatalogVariantUsageCheckerTests`. Revisión del bloque: CHECKs contra
el dominio, `Down` de la migración, índices filtrados contra las consultas, orden de bloqueos.

---

# Bloque C — Casos de uso, API y cierre (tareas 7 a 11, un implementador)

### Task 7: `short` en una línea, con su novedad y el `Count` en la misma transacción

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskIncidents.cs` (servicio scoped `TaskIncidentRecorder`), `ShortTaskLine.cs`
- Create: `src/Modules/Wms/Modules.Wms.Application/Control/IWmsEvidenceLookup.cs`; `src/Bootstrapper/WmsEvidenceLookup.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskDtos.cs` (`ShortTaskLineRequest`, `TaskLineDto.QuantityCounted`), `WarehouseTaskDtoComposer.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskShortApiTests.cs`; unitarias en `Modules.Wms.UnitTests/Tasks/` con `TaskWorld`

Piezas:

```csharp
/// <summary>Lo que el operario escaneó al no encontrar todo. <c>QuantityFound</c> va al destino planeado; para otro destino se
/// confirma antes con motivo (DA-41). <c>IncidentType</c> es el nombre del tipo: ShortMove o LocationMismatch en un movimiento.</summary>
public sealed record ShortTaskLineRequest(Guid LeaseToken, decimal QuantityFound, Guid? LotId, string? IncidentType, string? Note,
    IReadOnlyList<Guid>? PhotoFileIds, DateTimeOffset? OccurredAt, string? DeviceId) : IIdempotentRequest;

public interface IWmsEvidenceLookup
{
    /// <summary>Los ids que son archivos <c>WmsEvidence</c> del tenant, en cualquier estado (pueden estar subiendo todavía, DA-49).
    /// Los de otro tenant o de otro tipo de dueño no vienen.</summary>
    Task<IReadOnlySet<Guid>> FindEvidenceAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken ct);
}

/// <summary>Arma la novedad que nace de una tarea y, si su tipo pide verificación, le busca o le crea el Count. No guarda: comparte
/// la transacción del handler.</summary>
public sealed class TaskIncidentRecorder(IIncidentRepository incidents, IWarehouseTaskRepository tasks,
    IVariantProfileRepository profiles, IWmsUnitOfWork unitOfWork, IWmsEvidenceLookup evidence)
{
    public Task<Incident> ReportAsync(WarehouseTask task, WarehouseTaskLine line, IncidentType type, decimal expected, decimal actual,
        LotId? lot, string? note, IReadOnlyList<Guid> photoFileIds, Guid member, string deviceId, string operationKey,
        DateTimeOffset reportedAt, DateTimeOffset now, CancellationToken ct);
}
```

`ReportAsync`: valida la evidencia (`IncidentEvidenceInvalid` si algún id no vuelve) → `incidents.NextNumberAsync` → `Incident.Report`
con sujeto `(task.Id, line.Id, line.FromLocationId, line.VariantId, lot, line.StockStatus, expected, actual)` → si
`IncidentCatalog.RequestsVerification(type)` y el perfil de la variante no es `Serial` (DA-44): `unitOfWork.LockAsync("count-key:{tenant}:{location}:{variant}")`
→ `tasks.FindOpenCountAsync(…)` → si no hay, `tasks.NextNumberAsync` + `WarehouseTask.CreateCount(…, [WarehouseTaskLine.CreateCount(variant,
lot, status, location)], TaskSourceType.Incident, incident.Id, now)` + `tasks.Add` → `incident.LinkVerification(count.Id)` →
`incidents.Add`.

`ShortTaskLineHandler(ShortTaskLineCommand)` — molde `ConfirmTaskLineHandler`, mismo orden:

1. `DocumentRead` → validador (`deviceId`, `quantityFound >= 0`, `incidentType` dentro de `IncidentType`, nota ≤ 500, fotos ≤ 5,
   `occurredAt` no nulo) → clave IK → `member`.
2. Transacción → lock del operario → `tasks.LockAsync` (404) → `GetAsync` → `guard.EnsureCanPerformAsync` → **lease** (la tarea 9 cambia
   este punto para registrar `SyncConflict`) → línea (404).
3. `IncidentCatalog.DeclarableOnShort(task.Type.ToString())` no contiene el tipo → 422 `wms.incident.type_not_allowed`.
4. Lote: `line.LotId` fijo y `LotId` distinto → `wms.task.lot_mismatch`; efectivo = `line.LotId ?? command.LotId`.
5. `busy` como en confirmar (DA-32) → `remaining = line.Remaining` → `task.ShortLine(…, quantityFound, now, occurredAt, busy)`.
6. Si `quantityFound > 0`: **un** `LedgerEntry` `(owner, from, variant, lot, status) → (owner, line.ToLocationId, …)`,
   `TaskTypePolicy.MovementReasonOf(task.Type)`, documento `("Task", task.Id, line.Id)` → `ledger.PostAsync`.
7. `recorder.ReportAsync(task, line, type, expected: remaining, actual: quantityFound, …, operationKey: key, reportedAt: occurredAt)`.
8. `composer.ComposeAsync` → `receipts.Record(dto)` → `SaveChangesAsync`.

Orden real dentro del handler: pasos 5 y 7 (dominio, novedad, `Count`, numeraciones) **antes** del 6 (ledger): así se respeta «numeraciones
antes que saldos». El ledger valida igual el saldo y, si falla (`wms.stock.insufficient`), la transacción entera se deshace: ni línea
`Short`, ni novedad, ni `Count`.

Ruta: `POST /tasks/{id:guid}/lines/{lineId:guid}/short` con `.RequireIdempotencyKey().RequireAuthorization(WmsPermissions.DocumentRead)`.

`WmsEvidenceLookup(IFileResourceRepository repository)`: `ListByIdsAsync` y filtro `resource.TenantId == tenantId && resource.OwnerType ==
FileOwnerType.WmsEvidence` (copia de `QuotationFileLookup.FindManyAsync`, sin mirar el estado). Registro
`services.AddScoped<Modules.Wms.Application.IWmsEvidenceLookup, WmsEvidenceLookup>();` junto a `WmsMemberDirectory`.

`TaskLineDto` suma `decimal? QuantityCounted` (al final; comentario: sólo un `Count` lo llena, después de contar). El composer pone
`QuantityRemaining = 0` y `QuantityPlanned = 0` en las líneas de `Count` tal como vienen: no hay nada que ocultar porque no hay nada
guardado.

- [ ] **RED.** `TaskShortApiTests` (arranque de `TaskOperatorApiTests`: operario con `DocumentRead` + `PutawayPerform`, stock 10 en
  `A-01-02`, `Move` de 5 a `B-01-01`, reclamada con `next`):
  - `ShortWithTwoFoundMovesTwoAndClosesTheTask`: 200, `OnHandAsync(B-01-01) = 2`, `A-01-02 = 8`, línea `Short`, tarea `Done`; una
    novedad `ShortMove` `Open` con `expectedQty = 5`, `actualQty = 2`, `sourceTaskId`, `reportedBy` = el operario; un `Count` `Ready`
    con `sourceType = "Incident"`, una línea de `A-01-02`/`V1`/`Available`, `quantityPlanned = 0`; la novedad lo tiene en
    `verificationTaskId`.
  - `ShortWithNothingFoundMovesNothing` (sin movimiento con `document_id` de la tarea).
  - `RepeatingTheShortReturnsTheSavedResponse` (misma clave → 200 idéntico, una novedad, un `Count`).
  - `ASecondShortOnTheSameKeyReusesTheOpenCount` (DA-44: otra tarea sobre la misma clave → dos novedades, un `Count`).
  - `AShortOnASerialVariantCreatesNoCount` (perfil `Serial`; novedad sin `verificationTaskId`).
  - `ATypeNotDeclarableOnAMoveIs422` (`UnreadableLabel` → `wms.incident.type_not_allowed`).
  - `FindingEverythingIs422LineInvalid`; `AnUnknownPhotoIs422EvidenceInvalid` (un `Guid` cualquiera).
  - `ShortWithoutThePutawayPermissionIs403`; `ShortForAnotherTenantIs403`.
  - `ShortMoreThanTheStockRollsBackEverything` (encontrado 2 con saldo 1 → 422 `wms.stock.insufficient`; ni novedad ni `Count`; la
    línea sigue `Open`).
- [ ] `dotnet build`: rojo (RED de compilación). Guarda la salida.
- [ ] **GREEN.** Implementa y registra `ShortTaskLineHandler`, su validador, `TaskIncidentRecorder` (scoped) y el adaptador.
- [ ] `--filter "FullyQualifiedName~TaskShortApiTests"` y `~CompositionRootTests`: verdes.
- [ ] Commit: `feat(wms): declare a short line with its incident and verification count`

### Task 8: Registrar el conteo y proponer el ajuste

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Tasks/CountTaskLine.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskDtos.cs` (`CountTaskLineRequest`), `ConfirmTaskLine.cs` (rechazo sobre `Count`)
- Modify: `src/Modules/Wms/Modules.Wms.Api/TaskEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskCountApiTests.cs`, `Control/CountVarianceAdjustmentTests.cs` (primera parte)

```csharp
/// <summary>Lo que el operario contó en la ubicación, a ciegas (DA-40). Contar cero es válido.</summary>
public sealed record CountTaskLineRequest(Guid LeaseToken, Guid LocationId, Guid? LotId, decimal QuantityCounted,
    DateTimeOffset? OccurredAt, string? DeviceId) : IIdempotentRequest;
```

`CountTaskLineHandler` — molde `ConfirmTaskLineHandler`, devuelve `TaskDto`:

1. `DocumentRead` → validador (`quantityCounted >= 0` y `< 10^12`, `deviceId`, `occurredAt`) → clave IK → `member`.
2. Transacción → lock del operario → `tasks.LockAsync` → `GetAsync` → guard (el tipo `Count` exige `CountPerform`) → lease → línea.
3. `LocationId != line.FromLocationId` → `wms.task.location_mismatch`. Lote efectivo `line.LotId ?? command.LotId`; si es nulo y el
   perfil tiene `Tracking == Lot` → 422 `wms.lot.required` (así nunca se propone un ajuste que el ledger rechazaría al contabilizar).
4. `busy` (DA-32) → `task.RecordCount(…)`.
5. `unitOfWork.LockAsync("count-key:…")` → `expected = stock.FindOnHandAsync(tenant, key)` con la clave contada →
   `delta = counted − expected`.
6. `verified = incidents.ListOpenVerifiedByAsync(tenant, task.Id)`.
   - `delta == 0`: cada una `Resolve(Accept, Guid.Empty, "Verificada por {task.Number}: sin diferencia.", null, null, now)`.
   - `delta != 0`: `adjustments.NextNumberAsync` → `Adjustment.Propose(tenant, number, owner, warehouse, AdjustmentReason.CountVariance,
     note: "{task.Number}", occurredAt, [AdjustmentLine.Create(location, variant, lot, status, delta, null, null)], proposedBy:
     executionContext.SubjectId, now)` (el mismo actor que `ProposeAdjustmentHandler`) → `adjustments.Add` → auditoría
     `AdjustmentProposed` como en `ProposeAdjustmentHandler` (un ajuste siempre se audita, lo proponga quien lo proponga) →
     `incidents.NextNumberAsync` → `Incident.Report(… CountVariance, sujeto (task.Id, line.Id, location, variant, lot, status, expected,
     counted), reportedBy: member, operationKey: key, reportedAt: occurredAt)` → `AttachAdjustment(adjustment.Id)` → `incidents.Add`
     → cada verificada `Resolve(Accept, Guid.Empty, "Verificada por {task.Number}: ver {cv.Number}.", null, parentIncidentId: cv.Id, now)`.
   Nunca llama al ledger.
7. `composer` → `receipts.Record` → `SaveChangesAsync`.

`ConfirmTaskLineHandler`: el dominio ya rechaza confirmar un `Count` (tarea 3); el handler no cambia salvo que el 422 sale antes de
validar destino: mueve el chequeo `TaskTypePolicy.ConfirmsByCount(task.Type)` justo después de cargar la línea y lanza la
`TaskTransitionException` del dominio, para que el operario no reciba un `location_mismatch` confuso.

Ruta: `POST /tasks/{id:guid}/lines/{lineId:guid}/count`, IK, `DocumentRead`.

- [ ] **RED.** `TaskCountApiTests` (un contador con `DocumentRead` + `CountPerform`; el `Count` sale de un `short` hecho por otro
  operario, como en la tarea 7: saldo 10, encontrado 2 de 5 → quedan 8 en el sistema en `A-01-02`):
  - `TheReporterDoesNotGetTheCountButAnotherCounterDoes` (DA-45: `next` del reportante con `CountPerform` → 204; el contador → el `Count`).
  - `TheCountDtoRevealsNothingExpected` (`quantityPlanned = 0`, `quantityRemaining = 0`, `quantityCounted = null`).
  - `CountingWhatTheSystemHasClosesTheIncidentsWithoutAdjustment` (cuenta 8 → novedad `ShortMove` `Resolved`/`Accept`, ningún ajuste).
  - `CountingLessProposesACountVarianceAdjustment` (cuenta 5 → un `Adjustment` `Proposed`, motivo `CountVariance`, una línea con delta
    −3 en `A-01-02`; novedad `CountVariance` `Open` con `adjustmentId`, `expectedQty = 8`, `actualQty = 5`; la `ShortMove` `Resolved`
    con `parentIncidentId`; **ningún movimiento nuevo** en el ledger).
  - `CountingZeroIsValid` (delta −8).
  - `RepeatingTheCountReturnsTheSavedResponse` (un solo ajuste).
  - `AWrongLocationIs422LocationMismatch`; `ALotTrackedVariantWithoutLotIs422LotRequired`.
  - `ConfirmingACountIs422InvalidTransition` y `CountingAMoveIs422InvalidTransition`.
  - `CountingWithoutCountPerformIs403`.
- [ ] `dotnet build`: rojo (RED de compilación).
- [ ] **GREEN.** Implementa y registra `CountTaskLineHandler` y su validador.
- [ ] `--filter "FullyQualifiedName~TaskCountApiTests"`: verde.
- [ ] Commit: `feat(wms): record a blind count and propose the variance adjustment`

### Task 9: `SyncConflict` cuando una confirmación llega con el lease perdido

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/TaskIncidents.cs` (`RecordSyncConflictAsync`), `OperatorTaskGuard.cs`
  (`LeaseLost()` pasa a `internal static`), `ConfirmTaskLine.cs`, `ShortTaskLine.cs`, `CountTaskLine.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Tasks/TaskSyncConflictApiTests.cs`; unitarias con `TaskWorld`

```csharp
// TaskIncidentRecorder
/// <summary>La confirmación que llegó con un lease perdido, tal cual llegó (DA-48). Si esa Idempotency-Key ya dejó su novedad, no
/// agrega otra. No guarda: el handler confirma la transacción con sólo esto y después responde 409.</summary>
public Task RecordSyncConflictAsync(WarehouseTask task, WarehouseTaskLine? line, Guid member, string deviceId, string operationKey,
    decimal? quantity, string rawPayload, DateTimeOffset reportedAt, DateTimeOffset now, CancellationToken ct);
```

En los tres handlers, el paso de lease pasa a ser:

```csharp
// Spec §6.7 y DA-48: la confirmación rechazada queda como novedad aunque el hecho no se aplique. La transacción sólo tiene la
// novedad (nada se tocó todavía), así que guardarla y lanzar después no deja nada a medias. El filtro IK no encuentra recibo y
// devuelve el 409; un reintento con la misma clave no duplica la novedad (operation_key, bajo el lock del operario).
if (task.CheckLease(member, command.LeaseToken) == LeaseCheck.Lost)
{
    await recorder.RecordSyncConflictAsync(task, task.FindLine(command.LineId), member, command.DeviceId, key, command.Quantity,
        JsonSerializer.Serialize(command), command.OccurredAt ?? now, now, cancellationToken);
    await unitOfWork.SaveChangesAsync(cancellationToken);
    throw OperatorTaskGuard.LeaseLost();
}
```

El sujeto de la novedad sale de la línea si existe (ubicación origen, variante, lote, estado, `actualQty = quantity`); si la línea no es
de la tarea se registra sin sujeto (el 404 no aplica: la tarea existe y el hecho físico es lo que importa). `raw_payload` es el comando
serializado con `JsonSerializerOptions.Web`, **sin** el `leaseToken` (se reemplaza por `null` antes de serializar: el token de otro
reclamo no debe quedar legible en la bandeja).

El guard de permiso (`EnsureCanPerformAsync`) va **antes**: sin permiso es 403 y no queda novedad.

- [ ] **RED.** `TaskSyncConflictApiTests`:
  - `AConfirmationAfterReassignmentLeavesASyncConflict`: el operario A reclama; el supervisor reasigna a B; A confirma → 409
    `wms.task.lease_lost`; existe una novedad `SyncConflict` `Open`, `reportedBy` = A, `sourceTaskLineId`, `actualQty`, `operationKey` =
    la clave, `rawPayload` con `quantity` y sin `leaseToken`; la tarea y el stock no cambiaron.
  - `RetryingTheSameKeyDoesNotDuplicateTheConflict` (dos veces 409, una novedad).
  - `AShortAndACountWithALostLeaseAlsoLeaveAConflict`.
  - `AResumableLateConfirmationLeavesNoConflict` (el caso `ALateConfirmationResumesTheTask` de T1 sigue igual y no crea novedad).
  - `WithoutTheTypePermissionItIs403AndLeavesNothing`.
- [ ] `dotnet build`: rojo si la firma nueva todavía no existe; si compila, el RED es la corrida filtrada de la primera prueba
  (con contenedores: es un fallo de comportamiento, no de compilación). Guarda la salida.
- [ ] **GREEN.** Implementa.
- [ ] `--filter "FullyQualifiedName~TaskSyncConflictApiTests|FullyQualifiedName~TaskOperatorApiTests"`: verde (lo de T1 no cambia).
- [ ] Commit: `feat(wms): keep a sync conflict when a confirmation lost its lease`

### Task 10: API de novedades, resolución, separación de funciones y tablero

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Control/IncidentDtos.cs` (DTOs y `IncidentDtoComposer`), `ReportIncident.cs`,
  `ListIncidents.cs`, `GetIncident.cs`, `ReviewIncident.cs`, `ResolveIncident.cs`, `RejectIncident.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Control/ApproveAdjustment.cs`, `PostAdjustment.cs`, `RejectAdjustment.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Tasks/GetTaskBoard.cs`, `TaskDtos.cs` (`TaskBoardDto.OpenIncidents`)
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` (`Resources.Incident = "wms.incident"`; `IncidentReviewed`,
  `IncidentResolved`, `IncidentRejected` = `"wms.incident.*"`), `WmsSupport.cs` (`WmsNotFound.Incident(Guid)`)
- Create: `src/Modules/Wms/Modules.Wms.Api/IncidentEndpoints.cs`; Modify: `WmsEndpoints.cs` (`IncidentEndpoints.Map(group);`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers, composer)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Control/IncidentApiTests.cs`, `Control/CountVarianceAdjustmentTests.cs` (segunda parte), `Tasks/TaskSupervisorActionsApiTests.cs` (tablero)

Contrato (spec §8.5; los nombres de tipo y resolución van por nombre de enum, DA-42):

```
POST /incidents                   IK  [wms.document.read + permiso de algún tipo de tarea en el handler, DA-49]
     { warehouseId, type, locationId?, variantId?, lotId?, quantity?, rawCode?, note, photoFileIds, sourceTaskId?, occurredAt, deviceId } → 201 IncidentDto
GET  /incidents?warehouseId&status&type&page&pageSize   [wms.document.read] → { items: IncidentSummaryDto[], page, pageSize, totalCount }
GET  /incidents/{id}              [wms.document.read] → IncidentDto (con evidencia y eventos)
POST /incidents/{id}/review       [wms.discrepancy.resolve]  { version } → IncidentDto
POST /incidents/{id}/resolve  SK  [wms.discrepancy.resolve]  { version, resolution, note? } → { batchId?, createdTaskIds }
POST /incidents/{id}/reject       [wms.discrepancy.resolve]  { version, note } → IncidentDto
```

```
IncidentSummaryDto { id, number, type, status, warehouseId, ownerId, locationId, locationCode, variantId, variantCode, productName,
                     expectedQty, actualQty, reportedBy, reportedByName, reportedAt, sourceTaskId, sourceTaskNumber,
                     verificationTaskId, adjustmentId, version }
IncidentDto        { ...IncidentSummaryDto, lotId, lotCode, stockStatus, rawCode, rawPayload, note, deviceId, resolution,
                     resolvedBy, resolvedByName, resolvedAt, resolutionNote, batchId, parentIncidentId, allowedResolutions,
                     canBeRejected, evidence: [{ fileId, kind, rawValue }], events: [{ kind, memberId, memberName, detail, occurredAt }] }
TaskBoardDto       { ...lo de T1, openIncidents }
```

Notas BFF para los comentarios de los DTO: `allowedResolutions` y `canBeRejected` salen de `IncidentCatalog` (la pantalla no conoce el
catálogo y sin esto pintaría botones que dan 422); los nombres de miembro, códigos de ubicación y número de tarea se resuelven en el
servidor (`IWmsMemberDirectory`, la bodega, `IWarehouseTaskRepository`, `IWmsCatalogReader`), como `WarehouseTaskDtoComposer`.
`rawCode`/`rawPayload` viajan sólo en `IncidentDto` (la ficha). `reportedByName` de `Guid.Empty` es «Sistema».

Handlers:
- `ReportIncidentHandler`: `DocumentRead` con bodega → `TaskTypePermissions.AllowedFor(ctx).Count == 0` → 403 → validador (tipo en
  `IncidentCatalog.DeclarableStandalone`, si no 422 `wms.incident.type_not_allowed`; nota obligatoria 1..500; fotos ≤ 5) → dueño Self
  (provisioner antes de la transacción, como `ProposeAdjustmentHandler`) → transacción → bodega (404) y ubicación de la bodega (404) →
  evidencia → número → `Incident.Report(reportedAt: occurredAt)` → si `LocationMismatch` y hay variante, el mismo camino de `Count` que
  `TaskIncidentRecorder` (extrae `EnsureVerificationAsync` a un método público del recorder) → recibo → guardar. Sin variante, la
  `LocationMismatch` queda sin `Count` (no hay clave que contar) y el supervisor la acepta.
- `ReviewIncidentHandler`, `RejectIncidentHandler`: molde `ApproveAdjustmentHandler` (autorizar, cargar 404, autorizar con dueño y
  bodega, `WmsVersions.Ensure`, dominio, auditoría con `executionContext.SubjectId`, componer, guardar). El actor del dominio es la
  membresía (`WmsCaller.RequireAsync`).
- `ResolveIncidentHandler` (SK `incident:{id}:resolve:{version}`): como el anterior, más `incident.EnsureNotSelfResolution(resolution,
  member)` y, para `SyncConflict` + `AcceptIntoStock`, un movimiento con la clave SK: `(owner, location, variant, lot, status) →
  (owner, destino de la línea original, …)` por `actualQty`, motivo `InternalMove`, documento `("Incident", incident.Id,
  sourceTaskLineId)`, actor la membresía del supervisor (DA-48). El destino sale de la línea original (`tasks.GetAsync(sourceTaskId)`);
  si la línea ya no existe o la novedad no tiene sujeto completo → 422 `wms.incident.resolution_not_allowed`. Una llamada al ledger,
  `incident.Resolve(…, batchId)`, recibo, guardar. `CountVariance` → 422 `wms.incident.resolution_not_allowed` («se resuelve con su
  ajuste»). Respuesta `IncidentResolutionDto(Guid? BatchId, IReadOnlyList<Guid> CreatedTaskIds)` (en T2 siempre vacía la lista).
- `ApproveAdjustmentHandler`: después de cargar el ajuste, `incidents.FindByAdjustmentAsync`; si hay `CountVariance`, `member =
  caller.RequireAsync` y si `member == cv.ReportedBy` o está en `ListReportersOfChildrenAsync(cv.Id)` → 422 `wms.incident.self_resolution`
  (DA-17, DA-47). Sin novedad, igual que hoy.
- `PostAdjustmentHandler`: tras `MarkPosted`, si hay `CountVariance` del ajuste, `ResolveByAdjustment(posting.BatchId, member,
  adjustment.Number, now)` en la misma transacción. `RejectAdjustmentHandler`: `RejectByAdjustment(member, command.Note, now)`.
- `GetTaskBoardHandler`: `openIncidents = incidents.CountOpenAsync(tenant, warehouse)`.

Rutas en `IncidentEndpoints` (molde `AdjustmentEndpoints`/`TaskEndpoints`): `POST /incidents` → 201 con `Location`; `resolve` con
`.UseServerIdempotencyKey("incident", "resolve")` y cuerpo `ResolveIncidentRequest(long Version, string? Resolution, string? Note) :
IVersionedRequest`.

- [ ] **RED.** `IncidentApiTests`:
  - `ReportingAnUnreadableLabelReturns201Open` (operario con `PutawayPerform`); `ReportingALocationMismatchWithAVariantCreatesACount`.
  - `ReportingWithoutAnyTaskPermissionIs403`; `ReportingATypeNotDeclarableStandaloneIs422` (`ShortMove`).
  - `ListingFiltersByStatusAndType`; `GettingIncludesEvidenceEventsAndAllowedResolutions`.
  - `ReviewThenAcceptRoundTrips` (versión sube, auditoría `wms.incident.reviewed` y `wms.incident.resolved`); `AStaleVersionIs412`.
  - `ResolvingWithANotAllowedResolutionIs422`; `RejectingAShortMoveIs422`.
  - `AcceptingASyncConflictIntoStockMovesTheStock` (con un supervisor distinto del reportante; movimiento con `document_type =
    'Incident'`); `TheReporterCannotAcceptIntoStockTheirOwnConflict` (422 `wms.incident.self_resolution`);
    `RepeatingTheResolveReturnsTheSavedResponse` (SK: un solo movimiento).
  - `ResolvingWithoutDiscrepancyResolveIs403`; `AnIncidentOfAnotherTenantIs403`; `AnUnknownIncidentIs404`.
  `CountVarianceAdjustmentTests` (segunda parte, sobre el escenario de la tarea 8):
  - `TheReporterOfTheShortCannotApproveTheAdjustment` y `TheCounterCannotApproveIt` (422 `wms.incident.self_resolution`).
  - `ApprovingAndPostingResolvesTheCountVariance` (otro aprobador; tras el `post`, saldo de `A-01-02` = 5 y la `CountVariance`
    `Resolved` con `batchId`).
  - `RejectingTheAdjustmentRejectsTheCountVariance`.
  - `ACountVarianceCannotBeResolvedByHand` (422 `wms.incident.resolution_not_allowed`).
  `TaskSupervisorActionsApiTests.TheBoardCountsOpenIncidents`.
- [ ] `dotnet build`: rojo (RED de compilación). Guarda la salida.
- [ ] **GREEN.** Implementa y registra los seis handlers nuevos, sus validadores y el composer.
- [ ] `--filter "FullyQualifiedName~IncidentApiTests|FullyQualifiedName~CountVarianceAdjustmentTests|FullyQualifiedName~TaskSupervisorActionsApiTests|FullyQualifiedName~Adjustment"` y `~CompositionRootTests`: verdes (las de ajustes existentes no cambian).
- [ ] Commit: `feat(wms): review, resolve and reject incidents behind wms.discrepancy.resolve`

### Task 11: Documentación, reglas y cierre

**Files:**
- Modify: `README.md` (§ Módulo Wms: subsección «Novedades» y lo nuevo de «Tareas de bodega»)
- Modify: `.claude/rules/wms.md`, `.claude/rules/sql-entre-modulos.md` (la consulta de uso suma `wms.incidents`)

- [ ] README: rutas de §8.5 y las dos nuevas de tareas (`short`, `count`) con permiso e idempotencia; catálogo de T2 con sus
  resoluciones; que el `Count` es a ciegas y propone un ajuste `CountVariance`; que `SyncConflict` se registra aunque la respuesta sea
  409; códigos `wms.incident.*`.
- [ ] `.claude/rules/wms.md`, viñetas nuevas (prescriptivas):
  - «Un `Count` es a ciegas: lo esperado se calcula al contar (`FindOnHandAsync`) y no se guarda en la línea. Ningún campo de un DTO
    del operario puede llevarlo, ni siquiera "para depurar".»
  - «La novedad nace en la transacción del hecho, con su clave. La única excepción es `SyncConflict`, que se confirma sola antes del
    409: no "arregles" el `SaveChangesAsync` antes del `throw`.»
  - «Numeraciones antes que saldos: en `short` la novedad, el `Count` y sus números van antes de `ledger.PostAsync`.»
  - «Un tipo de novedad nuevo necesita: valor del enum, fila en `IncidentCatalog` (la prueba `EveryTypeHasAnEntry` lo exige),
    `CK_incidents_type` (migración) y, si mueve stock, su resolución en `ResolveIncidentHandler`.»
  - «Un tipo de tarea nuevo necesita además `TaskTypePolicy.ConfirmsByCount`.» (amplía la viñeta de T1)
- [ ] Corre, uno a la vez y en primer plano: `tests/ArchitectureTests/ArchitectureTests` filtrado por `~WmsLayerTests`,
  `~MultiReplicaInventoryTests`, `~CompositionRootTests`; `Modules.Wms.UnitTests` completo; `Bootstrapper.UnitTests` filtrado por
  `~CatalogVariantUsageCheckerTests`; `Modules.Wms.IntegrationTests` filtrado por `~Tasks|~Control|~StockProbe|~WmsPersistence`.
  Después `.\scripts\test-feature.ps1` y **lee sus avisos**. Esta rama cambia contrato HTTP (rutas `short`, `count`, `/incidents`,
  `TaskBoardDto.openIncidents`, `TaskLineDto.quantityCounted`) y `CompositionRootTests` si sus conteos de handlers lo exigen: todo eso
  pide autorización del owner antes del merge (`CLAUDE.md`, 2026-11-25), junto con las DA-38 a DA-51.
- [ ] Commit: `docs(wms): document incidents, short lines and verification counts`

**Cierre del bloque C y de la rama:** revisión fuerte de toda la rama por un subagente de solo lectura sin el contexto del
implementador. El merge a `develop` espera la autorización del owner y deja `docs/estado.md` para el commit `docs:` en `develop`.

---

## Self-review

- **Cobertura del spec:** §6.2 `Count` (permiso, lease 30 min, sin override, prioridad 3) → T1/T3; §6.3 `Done` con `Short` → T2; §6.4
  fila «Novedad» (misma transacción, misma clave) → T7/T8; §6.7 `SYNC_CONFLICT` → T9; §6.10 novedades en el tablero → T10; §6.12
  `LineShort` → T1/T2; §7.9 faltante de un `Move` → T7; §8.1 entidades → T4/T5 (con las columnas de DA-43); §8.2 catálogo (tipos de
  T2) → T4; §8.3 estados → T4; §8.4 reglas 1 (T7/T8/T9), 2 (T6/T8/T10), 3 (T4/T10), 4 (T7/T10: tipos cerrados por paso), 5 (filtro por
  bodega y el dueño en cada fila), 6 (aprobación por permiso); §8.5 API → T7/T10, más `count` (DA-40).
- **Contra el código:** `Incident` en `Control` no cambia `WmsLayerTests`; `ProposedBy` y `ApprovedBy` del ajuste son `SubjectId`
  (como hoy) mientras que la novedad guarda membresías, así que la separación de funciones compara **membresías** (`WmsCaller` en
  `ApproveAdjustmentHandler`), nunca el `SubjectId` contra `reported_by`. `CountOpenByTypeAsync` y los estados de
  `CatalogVariantUsageChecker` no cambian porque T2 no agrega estados de tarea; las líneas `Short` ya no son `Open` y dejan de contar
  como uso, que es correcto (no pueden escribir más). Ningún hosted service nuevo (`MultiReplicaInventory` intacto). Ningún permiso
  nuevo (`CountPerform` y `DiscrepancyResolve` ya existen con definición, política y rol `admin`).
- **Contradicciones resueltas:** §8.2 dice que `COUNT_VARIANCE` «se resuelve con el ajuste» y §8.5 da un `resolve` genérico: el
  endpoint lo rechaza y lo cierran `post`/`reject` del ajuste. §8.5 no tiene ruta para registrar el conteo: se agrega `count` (DA-40)
  en vez de forzar `confirm`, cuyo cuerpo exige destino y cantidad `> 0`. El cuerpo de `short` no tiene destino: lo encontrado va al
  planeado (DA-41).
- **Riesgos conocidos:** la confirmación de T1 nunca corrió integración (se paga en «Antes de empezar»); `Down` de la migración falla
  con datos de T2 (a propósito, comentado); el `Count` no bloquea la ubicación, así que un movimiento entre el faltante y el conteo
  cambia lo esperado: el conteo compara contra el saldo **al contar**, que es lo correcto para un ajuste por delta.

## Desviaciones

*(La llena el implementador, por bloque, como en T1.)*
