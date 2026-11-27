# Hr: editar y eliminar registros de «Cargo y lugar de trabajo» — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Un implementador por **bloque**, no por tarea (`CLAUDE.md` del workspace, «Presupuesto de pruebas»).

**Goal:** dos rutas nuevas (`PUT` y `DELETE /assignments/{recordId}`) para corregir o quitar un registro de cargo y lugar mal cargado, bloqueadas cuando el registro ya rigió en una nómina `Approved` o `Closed`, y el PDF del registro eliminado se purga de R2 por un aviso de outbox que Storage consume con reintento.

**Architecture:** las reglas de vecinos viven en `EmploymentContract` (`RewriteAssignment`, `RemoveAssignment` y la consulta pura `CanRemoveAssignment`). El bloqueo por nómina es una regla pura de Application (`AssignmentPayrollGuard`) sobre un puerto nuevo de Hr (`IFrozenPayrollPeriods`), que Bootstrapper adapta desde un puerto de lectura nuevo de Payroll (`IPayrollFrozenPeriods`), como `LegalParameterUsage`. El PDF viaja como `hr.employee.documents-released.v1`; Storage lo consume con reclamo por mensaje (copia de `ClaimedOutboxConsumer` de Messaging) y pregunta a Hr por `IHrDocumentReferences` → `HrDocumentReferences` (Bootstrapper) → `IHrDocumentUsage` (Hr).

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers (Postgres) sólo en integración.

**Spec:** [`docs/superpowers/specs/2026-11-26-hr-editar-eliminar-cargo-lugar-design.md`](../specs/2026-11-26-hr-editar-eliminar-cargo-lugar-design.md) (aprobado por el owner el 2026-11-26, opción (a) para `Liquidated`). Léelo entero antes de tu bloque.

## Global Constraints

- **Worktree** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\hr-assignment-edit`, rama `feature/hr-assignment-edit`. Cada bloque empieza con `Set-Location` a esa ruta y nunca escribe en `...\qep\qep-backend` ni en `qep-frontend`. Sin push, sin merge, nunca sobre `main`.
- **Todos los agentes corren en Opus** (instrucción del owner para este trabajo): implementadores, revisores de bloque y el revisor final.
- **Antes de cada commit:** `git branch --show-current` tiene que decir `feature/hr-assignment-edit`. Se stagean sólo los archivos de la tarea.
- **Commits:** conventional commits, en inglés, que terminan tras una línea en blanco con exactamente las dos líneas que pasa quien lanza el bloque. Hoy son:
  ```
  Session: Tarjetas de Trello
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  ```
- **TDD:** RED antes que GREEN, con la salida literal de los dos en el reporte del bloque. Un RED que es error de compilación se evidencia con `dotnet build <proyecto de pruebas> -nologo -v q`, sin contenedores.
- **Pruebas de integración:** mientras iteras, filtradas por nombre de prueba (`--filter "FullyQualifiedName~Clase.Prueba"`); la clase completa, una vez al final del bloque. Antes de cada corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; un `testhost` o un `dotnet test` vivo es de otra sesión: espera, no lo detengas (el `dotnet.exe` del build host de VS Code no cuenta, README § Verificación). En esta máquina va `$env:Seed__Enabled = "false"` antes (regla `pruebas-y-entorno-local`). Las pruebas corren en primer plano.
- **`Api.exe` corriendo bloquea el build:** si aparece, avisa; no lo detengas.
- **Migraciones** con el factory de diseño (regla `build-y-despliegue`):
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`. Las tres son aditivas (expand/contract, spec § «Migraciones y despliegue»); ninguna exige corte.
- **Nombres del spec, literales:** rutas, cuerpo `{ effectiveDate, positionId, areaId, workplaceId, confirmRewrite }`, campos `canEdit` y `canDelete`, los códigos de error de la tabla del spec, `hr.employee.documents-released.v1` con `{ tenantId, fileIds }`, consumidor `storage.hr-document-release`, esperas 5 s / 30 s / 5 min, `ErrorAfterAttempts = 3`, acciones `AssignmentRewritten` y `AssignmentDeleted`, auditoría `hr.contract.assignment_rewritten` y `hr.contract.assignment_deleted`.
- **Sólo `Approved` y `Closed` bloquean,** y sólo si el período tiene resultado guardado del empleado. `Liquidated`, `Reopened` y `Draft` no bloquean (decisión (a) del owner).
- Comentarios y documentación en español, tuteando. Lo que es contrato (códigos, rutas, enums) no se traduce.
- `docs/estado.md` no se toca en la rama.

## Review Focus

1. Mover hacia atrás la fecha de un registro no primero, hasta días de una nómina aprobada que regía el registro anterior: se rechaza `hr.assignment.used_by_payroll`, porque el rango empieza en la menor de las dos fechas. (Task 2, `MovingADateBackReachesTheDaysThePreviousRecordRuled`)
2. Una nómina aprobada de **otra empresa** del mismo empleado (su contrato anterior) no bloquea el contrato vigente. (Task 2, `AFrozenPeriodOfAnotherCompanyDoesNotBlock`)
3. Un empleado que no tiene resultado en una nómina aprobada (excluido o dado de alta después) no queda bloqueado por ella. (Task 2, `AnEmployeeWithoutAResultIsNotListed`)
4. Eliminar un registro intermedio que deja a sus vecinos iguales: `DELETE` responde `hr.assignment.unchanged` y la ficha ya decía `canDelete: false`, sin haber mutado el agregado. (Task 1, `CanRemoveAssignmentAnswersWithoutMutating`; Task 5, `DeletingAMiddleRecordThatLeavesEqualNeighboursIs422`)
5. El mismo `fileId` en otro registro o en un examen médico: eliminar un registro no purga el PDF. (Task 7, `EachOfTheFiveTablesKeepsItsFile`; Task 9, `AReferencedHrDocumentIsKept`)

## Bloques

| Bloque | Tareas | Depende de | Lo que entrega al siguiente |
| --- | --- | --- | --- |
| 1 — Hr: reglas, nómina y rutas | 1–5 | — | `HrIntegrationEvents.DocumentsReleased`, `IHrDocumentReleases` publicando en el outbox, rutas y DTO listos |
| 2 — Storage: purga del PDF | 6–9 | Bloque 1 (nombre del evento para la paridad; la migración de índices de Hr va después de la de actividad del Bloque 1) | consumidor, puertos y adaptador registrados; `MultiReplicaInventory` al día |
| 3 — Documentos y cierre | 10–12 | Bloques 1 y 2 | README, regla `rrhh`, `.\scripts\test-feature.ps1` en verde, insumos para Trello y el frontend |

Una revisión por bloque al terminarlo (Opus, sólo ese bloque). Al final, una revisión de toda la rama por un subagente de **solo lectura** (Opus) sin el contexto de los implementadores, como exige el `CLAUDE.md` del repo.

## Estructura de archivos

| Archivo (bajo `src/`) | Cambio | Tarea |
| --- | --- | --- |
| `Modules/Hr/Modules.Hr.Domain/HrErrorCodes.cs` | cinco códigos nuevos | 1 |
| `…Domain/AssignmentRecord.cs` | `Rewrite`, `ToEntry` | 1 |
| `…Domain/EmploymentContract.cs` | `RewriteAssignment`, `RemoveAssignment`, `CanRemoveAssignment` | 1 |
| `…Domain/Employee.cs` | `RewriteAssignment`, `RemoveAssignment` | 1 |
| `Modules/Hr/Modules.Hr.Application/IFrozenPayrollPeriods.cs` (nuevo) | puerto + `FrozenPayrollPeriod` | 2 |
| `…Application/AssignmentPayrollGuard.cs` (nuevo) | rangos y bloqueo | 2 |
| `Modules/Payroll/Modules.Payroll.Application/IPayrollFrozenPeriods.cs` (nuevo) | puerto + `PayrollFrozenPeriod` | 2 |
| `Modules/Payroll/Modules.Payroll.Infrastructure/Persistence/PayrollFrozenPeriods.cs` (nuevo) + `PayrollInfrastructureExtensions.cs` | EF y registro | 2 |
| `Bootstrapper/FrozenPayrollPeriods.cs` (nuevo) + `QepServiceCollectionExtensions.cs` | adaptador y registro | 2 |
| `…Application/HrDtos.cs`, `EmployeeDetailComposer.cs` | `CanEdit`, `CanDelete` | 3 |
| `…Domain/HrActivityAction.cs`, `…Application/HrLabels.cs`, `HrAuditActions.cs`, `ActivityChanges.cs` | acciones, etiquetas, auditoría, cambios | 4 |
| `…Infrastructure/Persistence/Migrations/*_AddAssignmentEditActivity.cs` (generada) | `CK_employee_activity_action` | 4 |
| `…Application/HrRequests.cs`, `HrRequestValidators.cs`, `NewContractFactory.cs` (`AssignmentLookups`), `RewriteAssignment.cs` y `RemoveAssignment.cs` (nuevos), `IHrDocumentReleases.cs` (nuevo) | casos de uso | 5 |
| `…Infrastructure/Persistence/HrDocumentReleasePublisher.cs` (nuevo), `HrInfrastructureExtensions.cs` | outbox | 5 |
| `Modules/Hr/Modules.Hr.Api/HrEndpoints.cs` | dos rutas | 5 |
| `Modules/Storage/Modules.Storage.Domain/FileResource.cs` | `PurgeReleasedHrDocument` | 6 |
| `Modules/Storage/Modules.Storage.Application/StorageSubscribedEvents.cs`, `IHrDocumentReferences.cs` (nuevo) | suscripción y puerto | 6 |
| `…Hr.Application/IHrDocumentUsage.cs` (nuevo), `…Hr.Infrastructure/Persistence/HrDocumentUsage.cs` (nuevo), `HrDbContext.cs` | uso de PDF e índices | 7 |
| `…Hr.Infrastructure/Persistence/Migrations/*_AddHrDocumentFileIndexes.cs` (generada) | índices parciales | 7 |
| `Bootstrapper/HrDocumentReferences.cs` (nuevo) | adaptador | 7 |
| `…Storage.Infrastructure/Persistence/StorageInboxMessage.cs`, `StorageDbContext.cs`, `StorageInboxClaims.cs` (nuevo), `Messaging/ClaimedOutboxConsumer.cs` (nuevo), migración `*_AddStorageInboxClaims.cs` | reclamo del inbox | 8 |
| `…Storage.Infrastructure/HrDocuments/` (nuevo): `HrDocumentPurge.cs`, `HrDocumentPurgeStore.cs`, `HrDocumentReleaseProcessor.cs`, `HrDocumentReleaseWorker.cs`; `StorageInfrastructureExtensions.cs` | consumidor | 9 |
| `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` | worker + fábrica (autorizado por el owner) | 9 |
| `README.md`, `CLAUDE.md`, `.claude/rules/rrhh.md` (nueva), `docs/superpowers/specs/2026-11-20-rrhh-fase-1-design.md` | documentos | 10 |

---

# Bloque 1 — Hr: reglas, nómina y rutas (Tasks 1–5)

## Task 1: reglas de vecinos en el agregado

**Files:** `HrErrorCodes.cs`, `AssignmentRecord.cs`, `EmploymentContract.cs`, `Employee.cs`; pruebas en `tests/Modules/Hr/Modules.Hr.UnitTests/AssignmentEditTests.cs` (nuevo, usa `HrTestData`).

**Produces:**
- `HrErrorCodes`: `AssignmentRewriteUnconfirmed = "hr.assignment.rewrite_unconfirmed"`, `AssignmentFirstRecordDateFixed = "hr.assignment.first_record_date_fixed"`, `AssignmentFirstRecordNotDeletable = "hr.assignment.first_record_not_deletable"`, `AssignmentEffectiveDateOutOfOrder = "hr.assignment.effective_date_out_of_order"`, `AssignmentUsedByPayroll = "hr.assignment.used_by_payroll"`. Todos 422 (fuera de `Conflicts`).
- `public AssignmentEntry AssignmentRecord.ToEntry()` y `internal void AssignmentRecord.Rewrite(AssignmentEntry entry)` (fecha, cargo, área, sede y centro).
- `internal void EmploymentContract.RewriteAssignment(AssignmentRecordId id, AssignmentEntry entry, DateTimeOffset now)`.
- `internal AssignmentRecord EmploymentContract.RemoveAssignment(AssignmentRecordId id, DateTimeOffset now)` — devuelve el registro quitado (el handler lee su PDF).
- `public bool EmploymentContract.CanRemoveAssignment(AssignmentRecordId id)` — consulta pura, sin `EnsureActive` (eso lo suma `canEdit`).
- `public void Employee.RewriteAssignment(EmploymentContractId contractId, AssignmentRecordId id, AssignmentEntry entry, bool confirmRewrite, DateTimeOffset now)` — `confirmRewrite` falso lanza `AssignmentRewriteUnconfirmed` antes que todo, como `RewriteSalary`; luego `Touch`.
- `public AssignmentRecord Employee.RemoveAssignment(EmploymentContractId contractId, AssignmentRecordId id, DateTimeOffset now)` — luego `Touch`.

Reglas (spec «Vecinos»), en este orden: contrato activo (`ContractNotActive`); el registro existe (`AssignmentNotFound`); al reescribir el primero, la fecha no cambia (`AssignmentFirstRecordDateFixed`); la fecha nueva queda estrictamente entre la del anterior y la del siguiente (`AssignmentEffectiveDateOutOfOrder`); en cada par tocado el posterior cambia algo (`AssignmentUnchanged`) y, si cambia sede o centro, su fecha es día 1 (`SiteChangeNotFirstOfMonth`). Al quitar: el primero no se quita (`AssignmentFirstRecordNotDeletable`) y se revisa el par anterior–siguiente. Un único helper privado de pares lo usan `RewriteAssignment`, `RemoveAssignment` y `CanRemoveAssignment`; `AddAssignment` no cambia.

- [ ] **Step 1: pruebas que fallan** (`AssignmentEditTests`; `Hire()` arranca el 2026-01-15 en `Warehouse` con `AnalystId`; un helper local `Add(employee, date, position, workplace)` llama `AddAssignment` como `ContractRecordsTests`)

```csharp
[Fact] public void RewritingWithoutConfirmationIsRejected()            // código hr.assignment.rewrite_unconfirmed
[Fact] public void TheFirstRecordKeepsItsDate()                        // primero, fecha 2026-01-16 → hr.assignment.first_record_date_fixed
[Fact] public void TheFirstRecordChangesItsPosition()                  // primero, ManagerId, misma fecha → PositionId == ManagerId
[Fact] public void ANewDateMustStayBetweenItsNeighbours()              // registros 01-15, 03-10, 05-20: 03-10 → 05-20 y → 01-15 dan out_of_order; → 04-01 pasa
[Fact] public void ARewriteEqualToThePreviousIsUnchanged()             // 03-10 Manager → Analyst → hr.assignment.unchanged
[Fact] public void ARewriteEqualToTheNextIsUnchanged()                 // par editado–siguiente
[Fact] public void APlaceChangeOffTheFirstIsRejectedOnEitherPair()     // editado a OtherWarehouse con fecha 03-10 → site_change_not_first_of_month; también si el siguiente (05-20) queda con lugar distinto al editado
[Fact] public void TheFirstRecordIsNotDeletable()                      // hr.assignment.first_record_not_deletable
[Fact] public void DeletingAMiddleRecordChecksThePreviousAgainstTheNext() // 01-15 Analyst, 03-10 Manager, 05-20 Analyst: quitar 03-10 → unchanged
[Fact] public void DeletingTheLastRecordReturnsIt()                    // devuelve el registro, ya no está en Assignments, Version sube
[Fact] public void CanRemoveAssignmentAnswersWithoutMutating()         // false para el primero y para 03-10 del caso anterior, true para el último; Assignments.Count y Version iguales antes y después
[Fact] public void EditsOnANonActiveContractAreRejected()             // tras Terminate: rewrite y remove → hr.contract.not_active
[Fact] public void AnUnknownRecordIsNotFound()                         // hr.assignment.not_found
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests -nologo -v q`; falla porque no existen los métodos.
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests --filter "FullyQualifiedName~AssignmentEditTests|FullyQualifiedName~ContractRecordsTests"`.
- [ ] **Step 5: commit** `feat(hr): rewrite and remove assignment records in the aggregate`

## Task 2: bloqueo por nómina congelada

**Files:** `IFrozenPayrollPeriods.cs`, `AssignmentPayrollGuard.cs` (Hr.Application); `IPayrollFrozenPeriods.cs` (Payroll.Application); `PayrollFrozenPeriods.cs` + registro en `PayrollInfrastructureExtensions.cs` (junto a `IPayrollCalculatedPeriods`, línea 32); `Bootstrapper/FrozenPayrollPeriods.cs` + registro en `QepServiceCollectionExtensions.cs` (junto a `ILegalParameterUsage`, línea 2470). Pruebas: `tests/Modules/Hr/Modules.Hr.UnitTests/AssignmentPayrollGuardTests.cs` y `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/AssignmentPayrollGuardApiTests.cs` (nuevos).

**Consumes:** Task 1.

**Produces:**
- Hr: `public sealed record FrozenPayrollPeriod(Guid CompanyId, DateOnly StartDate, DateOnly EndDate);` y `public interface IFrozenPayrollPeriods { Task<IReadOnlyList<FrozenPayrollPeriod>> ListForEmployeeAsync(Guid tenantId, Guid employeeId, CancellationToken cancellationToken); }`.
- Payroll: `public sealed record PayrollFrozenPeriod(Guid CompanyId, DateOnly StartDate, DateOnly EndDate);` y `public interface IPayrollFrozenPeriods { Task<IReadOnlyList<PayrollFrozenPeriod>> ListWithResultOfAsync(Guid tenantId, Guid employeeId, CancellationToken cancellationToken); }`. EF sobre `Periods` y `EmployeeResults` (`internal`): período del tenant, `Status` en `Approved` o `Closed`, con una fila de `EmployeeResults` de ese `PeriodId` y `EmployeeId`. Sin cambio de esquema.
- Bootstrapper: `internal sealed class FrozenPayrollPeriods(IPayrollFrozenPeriods periods) : IFrozenPayrollPeriods` — sólo traduce.
- `internal static class AssignmentPayrollGuard`:
  - `(DateOnly From, DateOnly? To) RangeOf(EmploymentContract contract, AssignmentRecord record)` — desde su fecha hasta el día anterior al siguiente; si es el último, `contract.EndDate` (nulo = abierto).
  - `(DateOnly From, DateOnly? To) RewriteRange(DateOnly oldDate, EmploymentContract contract, AssignmentRecord rewritten)` — `(min(oldDate, rewritten.EffectiveDate), RangeOf(contract, rewritten).To)`.
  - `bool Reaches(IReadOnlyList<FrozenPayrollPeriod> frozen, Guid companyId, DateOnly from, DateOnly? to)` — algún período de esa empresa con `StartDate <= (to ?? DateOnly.MaxValue)` y `EndDate >= from`.
  - `void EnsureFree(IReadOnlyList<FrozenPayrollPeriod> frozen, Guid companyId, (DateOnly From, DateOnly? To) range)` — lanza `HrDomainException(HrErrorCodes.AssignmentUsedByPayroll, …)`.

- [ ] **Step 1: pruebas que fallan**

```csharp
// AssignmentPayrollGuardTests — contrato del 2026-01-15 al 2026-07-14; registros 01-15 y 04-01 (ManagerId)
[Fact] public void ARecordRulesUntilTheDayBeforeTheNext()          // RangeOf(01-15) == (01-15, 03-31); RangeOf(04-01) == (04-01, 07-14)
[Fact] public void TheLastRecordOfAnOpenContractReachesEveryLaterPeriod() // Terms(duration: Indefinite): RangeOf(último).To == null; Reaches con un período 2030-01-01..15 → true
[Fact] public void MovingADateBackReachesTheDaysThePreviousRecordRuled()  // 04-01 → 03-01 con período congelado 03-01..03-15: RewriteRange(04-01, …) == (03-01, 07-14) y EnsureFree lanza hr.assignment.used_by_payroll
[Fact] public void APeriodThatEndsTheDayBeforeDoesNotReach()      // período 03-16..03-31 contra (04-01, 07-14) → false
[Fact] public void AFrozenPeriodOfAnotherCompanyDoesNotBlock()    // mismo rango, CompanyId = OtherCompanyId → false
```

```csharp
// AssignmentPayrollGuardApiTests (IClassFixture<PayrollApiFixture>; reloj 2026-11-10 → quincena 2026-11-01..15; usa NewSceneAsync y TransitionAsync de PayrollExportApiTests)
[Fact] public async Task OnlyApprovedOrClosedPeriodsWithAResultAreListed()
// liquidate → el puerto IPayrollFrozenPeriods (resuelto del host) devuelve [] para scene.EmployeeId;
// approve → devuelve [new PayrollFrozenPeriod(scene.Setup.Company.Id, 2026-11-01, 2026-11-15)]; close → sigue ahí;
// otro tenant (Guid.NewGuid()) → [].
[Fact] public async Task AnEmployeeWithoutAResultIsNotListed()
// tras approve, un empleado dado de alta después (HireAsync con otro documentNumber) → [].
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests -nologo -v q` y `dotnet build tests/Modules/Payroll/Modules.Payroll.IntegrationTests -nologo -v q`; fallan por compilación.
- [ ] **Step 3: implementar** puertos, EF, adaptador, registros y la regla.
- [ ] **Step 4: GREEN** — unitarias: `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests --filter "FullyQualifiedName~AssignmentPayrollGuardTests"`. Integración (con las precauciones de Global Constraints): `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~AssignmentPayrollGuardApiTests.OnlyApprovedOrClosedPeriodsWithAResultAreListed|FullyQualifiedName~AssignmentPayrollGuardApiTests.AnEmployeeWithoutAResultIsNotListed"`.
- [ ] **Step 5: commit** `feat(hr): read frozen payroll periods of an employee`

## Task 3: `canEdit` y `canDelete` en la ficha

**Files:** `HrDtos.cs` (`AssignmentRecordDto`), `EmployeeDetailComposer.cs`; prueba en `tests/Modules/Hr/Modules.Hr.IntegrationTests/AssignmentEditApiTests.cs` (nuevo, `IClassFixture<HrApiFixture>`, helpers de `HrApiSeeds`).

**Consumes:** `IFrozenPayrollPeriods`, `AssignmentPayrollGuard.RangeOf/Reaches`, `EmploymentContract.CanRemoveAssignment`.

**Produces:**
- `AssignmentRecordDto(…, DateTimeOffset CreatedAt, bool CanEdit, bool CanDelete)` — al final, con `<param>`: `canEdit` = contrato activo y ninguna nómina congelada de la empresa del contrato alcanza `RangeOf`; `canDelete` = `canEdit` y `CanRemoveAssignment`. No miran permisos. Escribe en el DTO el motivo BFF: la pantalla no puede calcularlo porque no ve la nómina.
- `EmployeeDetailComposer` recibe `IFrozenPayrollPeriods` por constructor y consulta **una vez**, sólo si `canReadContract` y hay `ActiveContract`. Los contratos anteriores salen con los dos en `false` sin consultar.

- [ ] **Step 1: prueba que falla**

```csharp
[Fact] public async Task TheDetailSaysWhichRecordsCanBeEditedOrDeleted()
// alta + POST /assignments con un segundo cargo creado en la prueba + otro POST que vuelve al primer cargo:
// assignments (orden descendente): último canEdit && canDelete; el del medio canEdit && !canDelete (vecinos iguales); primero canEdit && !canDelete.
// tras terminar el contrato: en "previous" todos false.
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.IntegrationTests -nologo -v q` no falla (la prueba lee JSON), así que el RED es la corrida: `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~AssignmentEditApiTests.TheDetailSaysWhichRecordsCanBeEditedOrDeleted"` → `KeyNotFoundException` en `canEdit`.
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN** — la misma corrida.
- [ ] **Step 5: commit** `feat(hr): tell the screen which assignment records can be edited or deleted`

## Task 4: rastro de las dos acciones

**Files:** `HrActivityAction.cs` (al final: `AssignmentRewritten`, `AssignmentDeleted`), `HrLabels.cs` (`"Cargo y lugar de trabajo editado"`, `"Cargo y lugar de trabajo eliminado"`, calco de «Examen médico editado/eliminado»), `HrAuditActions.cs` (`ContractAssignmentRewritten = "hr.contract.assignment_rewritten"`, `ContractAssignmentDeleted = "hr.contract.assignment_deleted"`), `ActivityChanges.cs`; migración `AddAssignmentEditActivity` (HrDbContext). Pruebas en `ActivityChangesTests.cs`; `HrLabelsTests` ya exige una etiqueta por acción.

**Produces:**
- `ActivityChanges.AssignmentRewritten(AssignmentEntry before, AssignmentRecord after, IReadOnlyDictionary<Guid, string> names)` — `effectiveDate` con `alwaysList: true` (identifica el registro, como `SalaryRewritten`) y los cuatro campos de lugar, todos `sensitive`.
- `ActivityChanges.AssignmentDeleted(AssignmentRecord removed, IReadOnlyDictionary<Guid, string> names)` — `effectiveDate` y los cuatro campos, del valor a `null`.
- `AssignmentFields` pasa a `(AssignmentEntry? before, AssignmentEntry? after, …)`; quien llama pasa `record.ToEntry()`.

- [ ] **Step 1: pruebas que fallan** (`ActivityChangesTests`)

```csharp
[Fact] public void ARewriteListsTheDateAndWhatChanged()   // fecha igual → effectiveDate listado igual; cargo cambiado → position "Analista"→"Gerente"; area ausente si no cambió; todos Sensitive
[Fact] public void ADeletionListsTheRecordItRemoved()     // effectiveDate "2026-04-01"→null, position, site, workplace → null; area sólo si tenía
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests -nologo -v q`.
- [ ] **Step 3: implementar** y generar la migración: `dotnet ef migrations add AddAssignmentEditActivity --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext -o Persistence/Migrations`. Revisa que el `Up` sólo cambie `CK_employee_activity_action` (drop + add con las dos acciones al final, como `AddMedicalExamActivity`) y el `Down` vuelva a la lista de hoy. Comprueba `dotnet ef migrations has-pending-model-changes --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext` → sin cambios.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests --filter "FullyQualifiedName~ActivityChangesTests|FullyQualifiedName~HrLabelsTests"`.
- [ ] **Step 5: commit** `feat(hr): log assignment rewrites and deletions`

## Task 5: las dos rutas y el aviso del PDF

**Files:** `HrRequests.cs`, `HrRequestValidators.cs`, `NewContractFactory.cs` (`AssignmentLookups`), `RewriteAssignment.cs` y `RemoveAssignment.cs` (nuevos), `IHrDocumentReleases.cs` (nuevo, con `HrIntegrationEvents`), `HrDocumentReleasePublisher.cs` + registro en `HrInfrastructureExtensions.cs` (como `MedicalExamEventPublisher`), `HrEndpoints.cs`, handlers en `QepServiceCollectionExtensions.cs` (junto a `AddAssignmentCommand`, línea 667). Pruebas: `AssignmentEditApiTests.cs` (Hr) y `AssignmentPayrollGuardApiTests.cs` (Payroll).

**Consumes:** Tasks 1–4.

**Produces:**
- `public sealed record AssignmentRewriteRequest(DateOnly? EffectiveDate, Guid? PositionId, Guid? AreaId, Guid? WorkplaceId, bool? ConfirmRewrite);` y `AssignmentRewriteRequestValidator` con las reglas de `AssignmentRequestValidator` (`confirmRewrite` no se valida: es el código de dominio).
- `AssignmentLookups.ResolveAsync(…, AssignmentKeys keys, AssignmentRecord? keeping, CancellationToken)` — nuevo parámetro antes del token; con `keeping`, un cargo, área o centro **igual** al del registro se acepta aunque esté archivado (y la sede de ese centro también); uno distinto archivado sigue siendo 422 de campo (propuesta 6). `AddAssignmentHandler` y `NewContractFactory` pasan `null`.
- `RewriteAssignmentCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, Guid RecordId, AssignmentRewriteRequest Request, string? IfMatch) : ICommand<EmployeeDetailDto>`. Orden del handler: `AuthorizeAsync` → `HrIfMatch.Require` → validador → cargar → `HrSettingsSupport.EnsureVersion` → `FindContract` → `FindAssignment` → `EnsureActive` → `EnsureEffectiveDateWithinContract` → `AssignmentLookups` (con `keeping`) → `EnsureWorkplaceOfCompany` → `before = record.ToEntry()` → `ContractRules.Run(employee.RewriteAssignment(…, request.ConfirmRewrite == true, …))` → `AssignmentPayrollGuard.EnsureFree(frozen, contract.CompanyId, RewriteRange(before.EffectiveDate, contract, record))` → `CommitAsync(AssignmentRewritten, Assignment, …, ContractAssignmentRewritten, Resources.Contract, contract.Id.Value, …)`.
- `RemoveAssignmentCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, Guid RecordId, string? IfMatch) : ICommand<EmployeeDetailDto>`. Orden: igual hasta `EnsureActive` → `range = RangeOf(contract, record)` (antes de quitar) → `removed = ContractRules.Run(employee.RemoveAssignment(…))` → `EnsureFree(frozen, contract.CompanyId, range)` → si `removed.DocumentFileId` tiene valor, `documentReleases.Publish(tenantId, [fileId], calendar.UtcNow)` → `CommitAsync(AssignmentDeleted, …, ContractAssignmentDeleted, …)`. El aviso viaja en el mismo `SaveChanges`.
- `public static class HrIntegrationEvents { public const string DocumentsReleased = "hr.employee.documents-released.v1"; }` y `public interface IHrDocumentReleases { void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt); }` — no guarda (calco de `IChatbotMediaReleases`). `HrDocumentReleasePublisher(HrDbContext)` agrega un `HrOutboxMessage` con payload camelCase `{ tenantId, fileIds }`, `CorrelationId = Guid.NewGuid().ToString()` y `Attempts = 0`.
- Rutas en `MapContracts`: `PUT {Contract}/assignments/{recordId:guid}` (`Accepts<AssignmentRewriteRequest>`) y `DELETE {Contract}/assignments/{recordId:guid}`, las dos `RequireAuthorization(HrPermissions.ContractManage)`, `.ProducesContractWrite()` y `.ProducesProblem(StatusCodes.Status428PreconditionRequired)`, leyendo `IfMatchOf(httpContext)`. Actualiza el comentario de arriba de `MapContracts` (hoy dice que sólo el `PUT` de salario lleva `If-Match`).

- [ ] **Step 1: pruebas que fallan**

```csharp
// AssignmentEditApiTests (Hr). Escena: alta + POST /assignments con otro cargo desde una fecha posterior.
[Fact] public async Task ARewriteChangesTheRecordAndLeavesATrace()        // 200; el registro trae fecha y cargo nuevos; employee.version +1;
                                                                          // CountAuditEntriesAsync("hr.contract.assignment_rewritten", contractId) == 1; GET /activity trae AssignmentRewritten
[Fact] public async Task ARewriteWithoutConfirmationIs422RewriteUnconfirmed()
[Fact] public async Task TheTwoRoutesNeedIfMatch()                        // sin If-Match → 428 precondition.if_match_required; versión vieja → 412 concurrency.conflict
[Fact] public async Task ChangingTheFirstRecordDateIs422FirstRecordDateFixed()
[Fact] public async Task ADateOutsideItsNeighboursIs422OutOfOrder()
[Fact] public async Task ARewriteEqualToANeighbourIs422Unchanged()
[Fact] public async Task AWorkplaceChangeMidMonthIs422()                  // hr.assignment.site_change_not_first_of_month
[Fact] public async Task AWorkplaceOfAnotherCompanyIs422WithItsField()    // hr.assignment.workplace_other_company y HasError(problem, "workplaceId")
[Fact] public async Task ADateAfterTheContractEndIs422OnEffectiveDate()   // validation.failed, HasError "effectiveDate"
[Fact] public async Task KeepingAnArchivedPositionIsAllowedButChoosingAnotherIs422() // archiva el cargo del registro: PUT con ese cargo y otra área → 200; PUT con otro cargo archivado → 422 HasError "positionId"
[Fact] public async Task AnUnknownRecordIs404AndATerminatedContractIs422()  // hr.assignment.not_found; hr.contract.not_active
[Fact] public async Task DeletingRemovesTheRecordAndLeavesATrace()        // 200; ya no está; audit hr.contract.assignment_deleted == 1; GET /activity trae AssignmentDeleted
[Fact] public async Task TheFirstRecordIs422FirstRecordNotDeletable()
[Fact] public async Task DeletingAMiddleRecordThatLeavesEqualNeighboursIs422() // hr.assignment.unchanged
[Fact] public async Task DeletingARecordWithAPdfWritesTheReleaseEvent()   // PDF adjunto con PUT /assignments/{id}/document (como ADocumentCanBeAttachedToARecordLater);
                                                                          // platform.outbox_messages tiene una fila hr.employee.documents-released.v1 con payload {tenantId, fileIds:[fileId]}
                                                                          // (SQL como MedicalExamRemindersApiTests); sin PDF, ninguna fila de ese evento para el tenant
```

```csharp
// AssignmentPayrollGuardApiTests (Payroll, de punta a punta sin dobles). Crea en la escena un segundo cargo y una segunda área por la API de Hr.
[Fact] public async Task ALiquidatedPeriodDoesNotBlockAndAnApprovedOneDoes()
// antes de liquidar: POST /assignments del empleado desde 2026-11-05 con el segundo cargo.
// liquidate → PUT de ese registro cambiando sólo areaId → 200, y approve sigue en 200 (no marca sourceChanged).
// tras approve: GET → ese registro y el primero con canEdit=false; PUT → 422 hr.assignment.used_by_payroll; DELETE → 422 hr.assignment.used_by_payroll.
// POST /assignments desde 2026-12-01 volviendo al primer cargo → canEdit && canDelete; DELETE → 200.
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.IntegrationTests -nologo -v q` compila (las pruebas usan HTTP); el RED es la corrida filtrada de dos de ellas (`…ARewriteChangesTheRecordAndLeavesATrace|…DeletingRemovesTheRecordAndLeavesATrace`) → la ruta no existe (404 o 405, el que dé: va literal en el reporte).
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN** — corridas filtradas por nombre mientras iteras. Al cerrar el bloque, una vez cada clase completa, en fila: `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~AssignmentEditApiTests"` y luego `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~AssignmentPayrollGuardApiTests"`. Más `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests` y `dotnet test tests/ArchitectureTests/ArchitectureTests`.
- [ ] **Step 5: commit** `feat(hr): edit and delete assignment records over HTTP`

**Revisión del Bloque 1** (Opus): contra el spec, «Contrato HTTP», «Errores», «Reglas» y las propuestas 1–6.

---

# Bloque 2 — Storage: purga del PDF (Tasks 6–9)

## Task 6: dominio, suscripción y puerto de Storage

**Files:** `FileResource.cs`, `StorageSubscribedEvents.cs`, `IHrDocumentReferences.cs` (nuevo, Storage.Application); pruebas `tests/Modules/Storage/Modules.Storage.UnitTests/FileResourceTests.cs` y `tests/Bootstrapper/Bootstrapper.UnitTests/HrDocumentsReleasedEventParityTests.cs` (nuevo, calco de `ChatbotMediaReleasedEventParityTests`).

**Consumes:** `HrIntegrationEvents.DocumentsReleased` (Task 5).

**Produces:**
- `public void FileResource.PurgeReleasedHrDocument(DateTimeOffset occurredAt)` — exige `FileOwnerType.HrDocument` (`storage.file.invalid_state`, como `RequireProductImage`) y `Available`; deja `Purged`, `DeletedAt` y `UpdatedAt`.
- `StorageSubscribedEvents.HrDocumentsReleased = "hr.employee.documents-released.v1"` con su `<summary>` del payload.
- `public interface IHrDocumentReferences { Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken); }` — calco de `IChatbotMediaReferences`, con su comentario (subconjunto que el tenant usa en Hr).

- [ ] **Step 1: pruebas que fallan**

```csharp
// FileResourceTests (helper NewHrDocument(bool available = true), calco de NewProductImage con FileOwnerType.HrDocument)
[Fact] public void PurgeReleasedHrDocumentMarksAnAvailableHrDocumentPurged()  // Status Purged, DeletedAt == now, UpdatedAt == now
[Fact] public void PurgeReleasedHrDocumentRequiresAvailable()                 // StorageDomainException "storage.file.invalid_state"
[Fact] public void PurgeReleasedHrDocumentRequiresAnHrDocument()              // NewProductImage() → "storage.file.invalid_state"
// HrDocumentsReleasedEventParityTests
[Fact] public void TheProducerAndTheConsumerNameTheSameEvent()               // literal "hr.employee.documents-released.v1" en las dos copias
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Storage/Modules.Storage.UnitTests -nologo -v q` y `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests -nologo -v q`.
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileResourceTests"` y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~HrDocumentsReleasedEventParityTests"` (Bootstrapper.UnitTests necesita Docker para otras clases, no para ésta).
- [ ] **Step 5: commit** `feat(storage): purge a released HR document and subscribe to its event`

## Task 7: qué PDF usa Hr todavía

**Files:** `IHrDocumentUsage.cs` (Hr.Application), `HrDocumentUsage.cs` (Hr.Infrastructure/Persistence) + registro en `HrInfrastructureExtensions.cs`, `HrDbContext.cs` (índices), migración `AddHrDocumentFileIndexes`, `Bootstrapper/HrDocumentReferences.cs` + registro en `QepServiceCollectionExtensions.cs` (junto a `IChatbotMediaReferences`, línea 2647). Prueba de composición: `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrDocumentReferencesTests.cs` (nuevo, `IClassFixture<HrApiFixture>`).

**Consumes:** `IHrDocumentReferences` (Task 6).

**Produces:**
- `public interface IHrDocumentUsage { Task<IReadOnlySet<Guid>> FindUsedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken); }` — el subconjunto de `fileIds` que nombra un `document_file_id` del tenant en `Contracts`, `SalaryRecords`, `AssignmentRecords` y `ContractRenewals` (acotadas por `Employees.TenantId`: contrato → empleado; registro → contrato → empleado) o en `MedicalExams` (por su `TenantId`). Lista vacía → conjunto vacío sin consulta.
- `internal sealed class HrDocumentReferences(IHrDocumentUsage usage) : IHrDocumentReferences` — sólo traduce.
- En `HrDbContext`, por cada una de las cinco entidades: `HasIndex(value => value.DocumentFileId).HasFilter("document_file_id IS NOT NULL").HasDatabaseName("IX_<tabla>_document_file_id")` con `<tabla>` = `employment_contracts`, `salary_records`, `assignment_records`, `contract_renewals`, `medical_exams`.

- [ ] **Step 1: prueba que falla**

```csharp
// HrDocumentReferencesTests — un PDF distinto en cada tabla, adjuntados por la API (FakeHrDocumentStore del fixture):
// contrato (alta con documentFileId), salario y cargo (PUT …/document), prórroga (POST /renewals con documentFileId), examen médico (como MedicalExamDocumentApiTests).
[Fact] public async Task TheHostResolvesTheBootstrapperAdapter()
// var references = scope.ServiceProvider.GetRequiredService<IHrDocumentReferences>();
// Assert.Equal("Bootstrapper.HrDocumentReferences", references.GetType().FullName);
[Fact] public async Task EachOfTheFiveTablesKeepsItsFile()
// FindReferencedAsync(tenant, [los cinco + un Guid nuevo]) == los cinco; con otro tenant → vacío.
[Fact] public async Task TheFiveIndexesExist()
// SELECT indexname FROM pg_indexes WHERE schemaname = 'hr' AND indexname LIKE 'IX_%_document_file_id' → los cinco nombres.
```

- [ ] **Step 2: RED** — el proyecto compila (`IHrDocumentReferences` es de Task 6), así que el RED es la corrida filtrada de `TheHostResolvesTheBootstrapperAdapter` → `InvalidOperationException: No service for type 'IHrDocumentReferences'`.
- [ ] **Step 3: implementar** y generar `dotnet ef migrations add AddHrDocumentFileIndexes --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext -o Persistence/Migrations`; el `Up` sólo crea los cinco índices y el `Down` los borra. `has-pending-model-changes` sin cambios.
- [ ] **Step 4: GREEN** — corrida filtrada de cada prueba; al cerrar el bloque, la clase completa.
- [ ] **Step 5: commit** `feat(hr): tell Storage which HR documents are still in use`

## Task 8: el inbox de Storage se reclama

**Files:** `StorageInboxMessage.cs` (`ProcessedAt` pasa a `DateTimeOffset?` con `set`; suma `ClaimedUntil` (`DateTimeOffset?`) y `Attempts` (`int`)), `StorageDbContext.ConfigureInbox` (`claimed_until`; `attempts` con `HasDefaultValue(1)`, como `IdentityDbContext`), `Persistence/StorageInboxClaims.cs` (nuevo), `Messaging/ClaimedOutboxConsumer.cs` (nuevo, namespace `Modules.Storage.Infrastructure.Messaging`), migración `AddStorageInboxClaims`. Pruebas: `StorageDbContextMappingTests.cs` (unitaria) y `tests/Modules/Storage/Modules.Storage.IntegrationTests/StorageInboxClaimsMigrationTests.cs` (nueva).

**Produces:**
- `internal static class StorageInboxClaims { public static Task<int?> TryClaimAsync(StorageDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, IReadOnlyList<TimeSpan> leases, CancellationToken cancellationToken); }` — copia literal de `MessagingInboxClaims` sobre `storage.inbox_messages`.
- `internal sealed class ClaimedOutboxConsumer(IServiceScopeFactory scopeFactory, string consumer, IReadOnlyList<string> eventNames, IReadOnlyList<TimeSpan> leases)` con `BatchSize = 20`, `Task<bool> ProcessBatchAsync(Func<ClaimedMessage, CancellationToken, Task> process, Action<Exception, StorageOutboxMessage, int?> onFailure, CancellationToken)` y `Task MarkProcessedAsync(ClaimedMessage message, CancellationToken)`; `internal sealed record ClaimedMessage(IServiceProvider Services, StorageDbContext DbContext, StorageOutboxMessage Record, DateTimeOffset Now)`. Copia de la de Messaging cambiando `MessagingDbContext` → `StorageDbContext` (`Outbox`, `Inbox`).
- Los cinco consumidores de hoy no cambian de lógica: siguen insertando con `ProcessedAt` y su fila toma `attempts = 1` por el default.

- [ ] **Step 1: pruebas que fallan**

```csharp
// StorageDbContextMappingTests.TheInboxMapsToItsTableWithConsumerAndMessageAsKey: suma
// processed_at nullable; claimed_until nullable; attempts no nullable con default 1.
// StorageInboxClaimsMigrationTests (base vacía, IMigrator como IdentityMigrationTests; StorageDbContext del factory de la prueba)
[Fact] public async Task UpKeepsTheRowsAndDownDropsOnlyUnfinishedOnes()
// migra hasta AddFileResourceTenantOwnerCreatedIndex, inserta una fila terminada; migra a la última → attempts = 1, claimed_until null;
// inserta una reclamada sin terminar (processed_at null); Down a la anterior → queda sólo la terminada; MigrateAsync(null) al final.
```

- [ ] **Step 2: RED** — la unitaria falla por aserción: `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~StorageDbContextMappingTests"`; tras cambiar el modelo, `TheModelHasNoChangesPendingAMigration` queda roja hasta generar la migración.
- [ ] **Step 3: implementar** y generar `dotnet ef migrations add AddStorageInboxClaims --project src/Modules/Storage/Modules.Storage.Infrastructure --context StorageDbContext -o Persistence/Migrations`. Edita el `Down` a mano: `migrationBuilder.Sql("DELETE FROM storage.inbox_messages WHERE processed_at IS NULL;")` antes de volver `processed_at` obligatorio, como `20261003010629_AddInboxClaims` de Identity.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~StorageDbContextMappingTests"`; integración filtrada: `--filter "FullyQualifiedName~StorageInboxClaimsMigrationTests"`.
- [ ] **Step 5: commit** `feat(storage): claim inbox messages with retries`

## Task 9: el consumidor, el worker y el inventario

**Files:** `HrDocuments/HrDocumentPurgeStore.cs`, `HrDocumentPurge.cs`, `HrDocumentReleaseProcessor.cs`, `HrDocumentReleaseWorker.cs` (nuevos, namespace `Modules.Storage.Infrastructure.HrDocuments`); `StorageInfrastructureExtensions.cs`; `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`. Pruebas: `tests/Modules/Storage/Modules.Storage.UnitTests/HrDocumentPurgeTests.cs`, `HrDocumentReleaseProcessorTests.cs` (nuevas) y `tests/Modules/Storage/Modules.Storage.IntegrationTests/HrDocumentReleaseTests.cs` (nueva).

**Consumes:** Tasks 6–8.

**Produces:**
- `internal interface IHrDocumentPurgeStore { Task<FileResource?> LockAsync(Guid tenantId, FileResourceId id, CancellationToken ct); Task SaveAsync(CancellationToken ct); Task AbandonAsync(); }` — `HrDocumentPurgeStore(StorageDbContext)`: `LockAsync` abre la transacción si no hay, `SELECT … FOR UPDATE` por id **y** tenant (sin filtrar dueño ni estado, para poder registrar el ajeno) y carga la entidad con variantes; `SaveAsync` guarda y confirma; `AbandonAsync` revierte (patrón de `UnreferencedFileCandidates`).
- `internal sealed partial class HrDocumentPurge(IHrDocumentPurgeStore store, IHrDocumentReferences references, IObjectStorage objectStorage, IStorageAuditPublisher auditPublisher, IClock clock, ILogger<HrDocumentPurge> logger)` (scoped) con `Task PurgeAsync(Guid tenantId, IReadOnlyList<Guid> fileIds, Func<Task> markProcessed, CancellationToken ct)`. Por id (spec «Por cada id»): `LockAsync`; `null`, otro dueño (se registra) o no `Available` → se salta; vuelve a preguntar `references.FindReferencedAsync(tenantId, [id])` bajo el candado y, si lo usan, lo deja; si no, `DeleteAsync` de `StorageKey` y de cada variante, `PurgeReleasedHrDocument(now)` y `PublishSystem(tenantId, "storage.file.purged", "file", id, Reason, now)`. Cada archivo se guarda antes del siguiente; el último, junto con `markProcessed()`, en un solo `SaveAsync`. Una excepción revierte (`AbandonAsync`) y sube: no se marca. `Reason = "hr_document_released"`. `IHrDocumentReferences` es obligatorio en el constructor (no una lista): sin el adaptador el contenedor no se arma y nada se borra (falla cerrado).
- `internal interface IHrDocumentReleaseProcessor { Task<int> ProcessPendingAsync(CancellationToken cancellationToken); }` y `internal sealed partial class HrDocumentReleaseProcessor(IServiceScopeFactory scopeFactory, ILogger<HrDocumentReleaseProcessor> logger)` — calco de `CustomerMediaPurgeProcessor`: `Consumer = "storage.hr-document-release"`, `ErrorAfterAttempts = 3`, esperas `[5 s, 30 s, 5 min]`, un `ClaimedOutboxConsumer` con `[StorageSubscribedEvents.HrDocumentsReleased]`. Por mensaje: payload mal formado (sin `tenantId` Guid no vacío o sin `fileIds` arreglo de Guid) → `Warning` y marca sin efecto (`MarkProcessedAsync` + `SaveChangesAsync`); si no, `GetRequiredService<HrDocumentPurge>()` del scope del mensaje y `PurgeAsync(…, () => claims.MarkProcessedAsync(message, ct))`. `internal void LogFailure(Exception, StorageOutboxMessage, int? attempts)`: sin intento → reclamo fallido (`Warning`); intento < 3 → `Warning` con id e intento; ≥ 3 → `Error`.
- `HrDocumentReleaseWorker(IQueueDoorbell doorbell, IHrDocumentReleaseProcessor processor, ILogger<HrDocumentReleaseWorker> logger)` — `doorbell.Listen(DoorbellQueues.Outbox)`, `DoorbellPolling.RunAsync(listener, PollInterval = 15 s, DoorbellQueues.SafetyNet(DoorbellQueues.Outbox), …)`, repite sin esperar si procesó `>= ClaimedOutboxConsumer.BatchSize`; sin `PeriodicTimer` (como `ChatbotMediaReleaseWorker`).
- Registro: `AddScoped<IHrDocumentPurgeStore, HrDocumentPurgeStore>()`, `AddScoped<HrDocumentPurge>()`, `AddSingleton<HrDocumentReleaseProcessor>()`, `AddSingleton<IHrDocumentReleaseProcessor>(provider => provider.GetRequiredService<HrDocumentReleaseProcessor>())`, `AddHostedService<HrDocumentReleaseWorker>()`.
- `MultiReplicaInventory`: worker `Modules.Storage.Infrastructure.HrDocuments.HrDocumentReleaseWorker` como `WorkerClassification.Claimed`, evidencia `HrDocumentReleaseProcessor.cs:<línea> — ClaimedOutboxConsumer (StorageInboxClaims.cs:<línea>); cada archivo bajo FOR UPDATE`; y en `InterfaceFactories` `IHrDocumentReleaseProcessor` → `HrDocumentReleaseProcessor`, «reenvía al singleton concreto», como `ICustomerMediaPurgeProcessor`. Autorizado por el owner en el spec («Arquitectura», punto 5).

- [ ] **Step 1: pruebas que fallan**

```csharp
// HrDocumentPurgeTests (dobles: store en memoria que filtra por tenant y cuenta Save/Abandon; references con respuestas en cola y modo Failing;
// object storage que anota claves y puede fallar; RecordingStorageAuditPublisher de StorageApplicationTestDoubles)
[Fact] public async Task AnUnreferencedHrDocumentIsDeletedPurgedAndAudited()   // DeleteAsync(StorageKey); Status Purged; audit storage.file.purged / hr_document_released; markProcessed llamado una vez, con el último Save
[Fact] public async Task AReferenceThatAppearsUnderTheLockKeepsTheFile()        // references responde {id}: sin Delete, Available, markProcessed sí
[Fact] public async Task AnotherOwnerAnotherTenantOrNotAvailableIsSkipped()     // Product, otro tenant (store devuelve null) y Purged: sin Delete ni audit; markProcessed sí
[Fact] public async Task AFailingR2OrReferenceQueryMarksNothing()              // cada falla: la excepción sube, markProcessed no se llama, Abandon sí, Status sigue Available
[Fact] public async Task EachFileIsSavedBeforeTheNextOne()                      // dos ids, el segundo falla en R2: el primero quedó Purged y guardado
// HrDocumentReleaseProcessorTests
[Theory, InlineData("{}"), InlineData("{\"tenantId\":\"x\",\"fileIds\":[]}"), InlineData("{\"tenantId\":\"<guid>\",\"fileIds\":\"no\"}")]
public void AMalformedPayloadIsRecognised(string json)                          // el parser interno devuelve false
[Theory, InlineData(1, LogLevel.Warning), InlineData(2, LogLevel.Warning), InlineData(3, LogLevel.Error), InlineData(4, LogLevel.Error)]
public void TheLogLevelFollowsTheAttempt(int attempt, LogLevel level)           // LogFailure con un ILogger que anota niveles (mínimo, en el archivo)
```

```csharp
// HrDocumentReleaseTests (Storage, StorageApiFactory; IHrDocumentReferences reemplazado por un doble con RemoveAll, como ChatbotMediaReleaseTests;
// el archivo HrDocument se crea Available por IStorageSystemUploads, como PublishedChatbotMediaAsync, sin publicarlo)
[Fact] public async Task AReleasedHrDocumentIsPurged()                // objeto fuera de factory.ObjectStorage; Status Purged; audit; inbox processed_at no nulo
[Fact] public async Task AReferencedHrDocumentIsKept()                // Available; inbox terminado
[Fact] public async Task AnR2FailureIsRetriedWithItsClaim()           // factory.ObjectStorage.FailingDeleteKey = la clave: attempts = 1, claimed_until > ahora, processed_at nulo;
                                                                      // vence el reclamo con UPDATE storage.inbox_messages SET claimed_until = now() - interval '1 second'; sin falla → attempts = 2, Purged
[Fact] public async Task TwoReplicasPurgeOnce()                       // dos HrDocumentReleaseProcessor (new con el scopeFactory del host) a la vez: una sola auditoría storage.file.purged
```

Sin prueba de «sin adaptador»: en `Development` el host arma el contenedor con `ValidateOnBuild`, así que un `HrDocumentPurge` sin `IHrDocumentReferences` no deja arrancar el host (y `CompositionRootTests` lo arma igual). Eso es el «falla cerrado» del spec.

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Storage/Modules.Storage.UnitTests -nologo -v q`.
- [ ] **Step 3: implementar** y sumar las dos entradas del inventario.
- [ ] **Step 4: GREEN** — unitarias: `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~HrDocumentPurgeTests|FullyQualifiedName~HrDocumentReleaseProcessorTests"`. Integración filtrada por nombre mientras iteras. Al cerrar el bloque, una clase completa a la vez: `HrDocumentReleaseTests`, `StorageInboxClaimsMigrationTests` (Storage) y `HrDocumentReferencesTests` (Hr); después `dotnet test tests/ArchitectureTests/ArchitectureTests` (incluye `MultiReplicaInventoryTests`) y `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests`.
- [ ] **Step 5: commit** `feat(storage): purge HR documents released by Hr`

**Revisión del Bloque 2** (Opus): contra el spec, «PDF del registro eliminado» y «Migraciones y despliegue»; en particular, que nada purgue sin la segunda pregunta bajo el candado.

---

# Bloque 3 — Documentos y cierre (Tasks 10–12)

## Task 10: documentos

**Files:** `README.md`, `CLAUDE.md`, `.claude/rules/rrhh.md` (nueva), `docs/superpowers/specs/2026-11-20-rrhh-fase-1-design.md`.

- [ ] **Step 1: README.** En «API implementada», una subsección «RRHH: editar y eliminar cargo y lugar de trabajo» con las dos rutas, el cuerpo, `If-Match`, `canEdit`/`canDelete` y la tabla de códigos del spec. En «Biblioteca de archivos (Cloudflare R2)», un párrafo «PDF de RRHH liberados» calcado del de fotos de producto: el evento, `HrDocumentReleaseWorker`, consumidor `storage.hr-document-release`, reclamo con `attempts`/`claimed_until`, la segunda pregunta bajo el candado, `storage.file.purged` / `hr_document_released`, purga inmediata sin gracia y sin métrica de cola (sólo log).
- [ ] **Step 2: regla `rrhh`.** `.claude/rules/rrhh.md` con `paths` `src/Modules/Hr/**`, `src/Bootstrapper/HrDocumentReferences.cs` y `tests/Modules/Hr/**`, y la regla del spec: una tabla de Hr que guarde un `document_file_id` entra en `IHrDocumentUsage` y en `HrDocumentReferencesTests`, o un PDF que todavía usa se purga al soltarlo desde otro registro. Su fila en la tabla «Reglas por módulo» del `CLAUDE.md`.
- [ ] **Step 3: spec de fase 1.** En la línea 201 de `2026-11-20-rrhh-fase-1-design.md`, que también se edita y se elimina un registro de cargo y lugar, con enlace al spec de hoy.
- [ ] **Step 4: commit** `docs(hr): document assignment edits and released HR documents`

## Task 11: verificación de la rama

- [ ] **Step 1:** `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` (espera si hay una corrida ajena), `$env:Seed__Enabled = "false"`, `.\scripts\test-feature.ps1 -DryRun` y luego `.\scripts\test-feature.ps1`, en primer plano.
- [ ] **Step 2:** lee `TestResults\feature\report.md` y **sus avisos**. Se espera «Requiere autorización del owner» por `MultiReplicaInventory.cs` (autorizado en el spec) y ninguna referencia nueva entre módulos; cualquier otro aviso de autorización se reporta y se para.
- [ ] **Step 3:** reporta la tabla del reporte, cada falla con nombre y mensaje, y lo que el nivel rápido no corre (las clases de integración no tocadas, p. ej. `EmployeeRecordsApiTests`, `EmployeeContractApiTests`, las de Payroll que no cambiaron). Si algo está rojo, no se cierra el bloque.

## Task 12: insumos para Trello y el frontend (lo hace el orquestador, no un implementador)

- [ ] **Tarjeta <https://trello.com/c/W4Ijr9DW>:** mover a «Code Review» y comentar rama `feature/hr-assignment-edit`, último commit, qué se probó (las clases y proyectos con su resultado de Task 11 y de cada bloque) y qué no (clases de integración no tocadas, la suite completa, prueba manual, el despliegue).
- [ ] **Tarjeta <https://trello.com/c/0dhhZPZV>:** comentar el contrato final: las dos rutas con `If-Match`, el cuerpo del `PUT`, `canEdit`/`canDelete` en `AssignmentRecordDto`, `422 validation.failed` con `effectiveDate`/`positionId`/`areaId`/`workplaceId`, los códigos de la tabla del spec (con los cinco nuevos), `428 precondition.if_match_required` y `412 concurrency.conflict`.
- [ ] **Prompt para la sesión de `qep-frontend`**, para que el owner lo pegue, completo y entendible sin esta conversación:

```text
Contexto: en qep-backend ya existen (rama feature/hr-assignment-edit, commit <sha>, <mergeado a develop o no>, <desplegado o no>) dos rutas
para corregir los registros de «Cargo y lugar de trabajo» de un contrato. Spec del backend:
qep-backend/docs/superpowers/specs/2026-11-26-hr-editar-eliminar-cargo-lugar-design.md. Tarjeta: https://trello.com/c/0dhhZPZV.

Qué se pide: en la ficha del empleado, «Editar» y «Eliminar» por registro de cargo y lugar.

Contrato (permiso hr.contract.manage; header If-Match con employee.version; ambas responden EmployeeDetailDto entero, la pantalla se repinta de ahí):
- PUT  /api/v1/tenants/{tenantId}/hr/employees/{employeeId}/contracts/{contractId}/assignments/{recordId}
       cuerpo { effectiveDate, positionId, areaId (opcional), workplaceId, confirmRewrite }
- DELETE la misma ruta, sin cuerpo.
- El PDF del registro se sigue cambiando con PUT …/assignments/{recordId}/document.
- AssignmentRecordDto suma canEdit y canDelete. Úsalos para mostrar u ocultar los botones; no miran permisos (eso lo decide hr.contract.manage,
  como el resto de la ficha). canEdit=true no garantiza cualquier cambio: mover la fecha puede alcanzar otra nómina y se rechaza.
Errores:
- 422 validation.failed con errors en effectiveDate, positionId, areaId, workplaceId (como el alta; incluye la fecha posterior a la terminación).
- 404 hr.assignment.not_found; 422 hr.contract.not_active; 422 hr.assignment.workplace_other_company (trae errors.workplaceId);
  422 hr.assignment.rewrite_unconfirmed (PUT sin confirmRewrite=true: abre la confirmación);
  422 hr.assignment.first_record_date_fixed; 422 hr.assignment.first_record_not_deletable;
  422 hr.assignment.effective_date_out_of_order; 422 hr.assignment.unchanged; 422 hr.assignment.site_change_not_first_of_month;
  422 hr.assignment.used_by_payroll (ya rigió en una nómina aprobada o cerrada: hay que reabrirla).
- 428 precondition.if_match_required; 412 concurrency.conflict.
Reglas que la pantalla debe reflejar: la fecha del primer registro no se edita y el primero no se elimina; el historial de actividad trae
AssignmentRewritten («Cargo y lugar de trabajo editado») y AssignmentDeleted («Cargo y lugar de trabajo eliminado»).
Ojo: eliminar un registro con PDF borra el PDF de forma permanente y de inmediato (no hay papelera).
Decisiones pendientes del owner para el frontend: el texto de la confirmación de «Eliminar» cuando el registro tiene PDF.
```

---

## Self-review (hecho al escribir el plan)

- **Cobertura del spec:** propuestas 1–3 y vecinos → Task 1; 4 (contrato terminado) → Tasks 1 y 5; 5 (salario) fuera; 6 (archivados) → Task 5; contrato HTTP y errores → Task 5; `canEdit`/`canDelete` → Task 3; nómina y `Liquidated` (a) → Tasks 2 y 5; rastro → Task 4; evento, referencias, consumidor, worker, sin gracia → Tasks 5–9; las tres migraciones → Tasks 4, 7 y 8; arquitectura 1–6 → Tasks 2, 5, 6, 7, 9; documentos → Task 10; pruebas del spec → repartidas arriba.
- **Nombres nuevos que no están en el spec** (decididos aquí): `FrozenPayrollPeriod`/`IFrozenPayrollPeriods`, `PayrollFrozenPeriod`/`IPayrollFrozenPeriods`, `FrozenPayrollPeriods` (Bootstrapper), `AssignmentPayrollGuard`, `AssignmentRewriteRequest`, `RewriteAssignmentCommand`, `RemoveAssignmentCommand`, `HrIntegrationEvents`, `IHrDocumentReleases`, `HrDocumentReleasePublisher`, `IHrDocumentPurgeStore`, `HrDocumentPurge`, `IHrDocumentReleaseProcessor`, motivo `hr_document_released` y las dos etiquetas del historial.
