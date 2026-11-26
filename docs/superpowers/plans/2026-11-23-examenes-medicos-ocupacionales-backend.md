# Exámenes médicos ocupacionales — plan backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Registrar exámenes médicos ocupacionales por empleado en el módulo `Hr`, con periodicidad por cargo, egreso pendiente a 5 días hábiles, pantalla de control, importación de historial y recordatorios en la campana.

**Architecture:** Todo dentro del módulo `Hr` (enfoque A del spec). `MedicalExam` es un agregado con versión propia; `ExitExamRequirement` nace al terminar un contrato y se recalcula tras cada escritura de exámenes. Hr gana outbox y worker propios copiando ServiceContracts; Notifications suma dos kinds y un consumidor.

**Tech Stack:** .NET 10, EF Core + Npgsql, ClosedXML 0.105.1, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-23-examenes-medicos-ocupacionales-design.md` (este repo). Los números de sección (§) son de ese spec.

## Global Constraints

- Rama de trabajo: worktree `qep-backend-worktrees/medical-exams`, rama `feature/medical-exams` desde `develop`. Nunca commitear sobre `main`. El merge termina en `develop`, sin push.
- TDD: RED antes que GREEN, con la salida literal de ambos. El RED de un fallo de compilación no levanta contenedores.
- Mientras se itera, la integración se filtra por nombre (`--filter "FullyQualifiedName~Nombre"`). La clase completa corre una vez por bloque. La suite del módulo corre una sola vez antes de mergear. Nunca dos corridas de integración a la vez: antes de lanzar una, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una, esperar, sin detenerla.
- Los comandos de prueba corren en primer plano. Si `Api.exe` está corriendo, no se puede compilar: avisar, no detenerlo.
- Capas verificadas por `ArchitectureTests`: Application no referencia EF Core ni Npgsql; las violaciones de unicidad se traducen en Infrastructure por **nombre de índice**.
- Permisos nuevos: `hr.medical.read`, `hr.medical.manage`, con `RequiredModules: [ModuleKeys.Hr]`, sólo para `admin`. El conteo de `CompositionRootTests` pasa de 112 a 114.
- Códigos de error exactamente como el §7.3 del spec. Restricciones, recomendaciones y proveedor **nunca** van a la actividad, a la auditoría ni a un evento.
- Conventional commits, sin atribución de IA.

## Review Focus

- Un examen `Exit` editado a otro tipo o con fecha anterior a `TerminatedOn` reabre el requerimiento (`Pending`, o `NotAttended` si tenía aviso).
- Dos exámenes `Exit` y dos requerimientos: cada requerimiento toma el más viejo que califique y no se comparte.
- Empleado `Deactivated`, o `Retired` con egreso cerrado: `status` es `null`, pero el `GET` responde con el historial.
- Un usuario con `hr.employee.read` y sin `hr.medical.read` recibe 403 en todo endpoint de exámenes y no ve la categoría `Medical` en la actividad.
- Reimportar el mismo archivo no duplica nada (el duplicado es error de fila y no escribe nada).

## File Structure

Crear en `src/Modules/Hr/`:

- `Modules.Hr.Domain/MedicalExam.cs`, `MedicalExamType.cs`, `ExitExamRequirement.cs`, `ExitExamStatus.cs`, `MedicalExamStatus.cs`, `MedicalExamRules.cs` (puro: estados, próximo periódico, recálculo del egreso).
- `Modules.Hr.Application/MedicalExams/`: un archivo por caso de uso (`RecordMedicalExam`, `UpdateMedicalExam`, `DeleteMedicalExam`, `SetMedicalExamDocument`, `MarkExitNotAttended`, `GetEmployeeMedicalExams`, `GetMedicalExamOverview`, `OpenMedicalExamUploadSession`, `SetPositionExamFrequency`, importación), más `IHrBusinessCalendar.cs` y los DTOs.
- `Modules.Hr.Infrastructure/Excel/ClosedXmlMedicalExamImporter.cs` y `ClosedXmlMedicalExamImportTemplateBuilder.cs`; `Reminders/` (worker, processor, stores, publisher); migración EF.
- Adaptadores en `src/Bootstrapper/`: `HrBusinessCalendarSource.cs`, `HrMemberDirectory` (ampliar).
- Notifications: `InAppNotificationKind.cs`, `InAppNotificationMapper.cs`, `HrInAppConsumer.cs`.

---

## Bloque A — dominio, permisos, CRUD, cargo, egreso

### Task 1: Permisos y calendario hábil

**Files:**
- Modify: `src/Modules/Hr/Modules.Hr.Application/HrPermissions.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (definiciones ≈4146-4196, lista de `admin` ≈2971-2977, políticas ≈4822-4835), `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (conteo `:377` y `TheHrPermissionsRequireTheHrModuleAndGoOnlyToAdmin` `:938-951`), `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrApiHarness.cs`, `HrApiSeeds.cs`.
- Create: `Modules.Hr.Application/MedicalExams/IHrBusinessCalendar.cs`, `src/Bootstrapper/HrBusinessCalendarSource.cs`.

**Interfaces:**
- Produces: `HrPermissions.MedicalRead = "hr.medical.read"`, `HrPermissions.MedicalManage = "hr.medical.manage"`; `IHrBusinessCalendar { Task<DateOnly> AddBusinessDaysAsync(Guid tenantId, DateOnly from, int days, CancellationToken ct); }` (el n-ésimo día hábil estrictamente después de `from`; lunes a viernes, y festivos de `ColombianHolidays` si el país regional del tenant es `CO`).

- [ ] **Step 1: Escribir las pruebas que fallan.** El conteo de permisos esperado pasa a 114 y la lista de `TheHrPermissionsRequireTheHrModuleAndGoOnlyToAdmin` suma los dos. Prueba de unidad de `HrBusinessCalendarSource` con nombre `AddBusinessDays_SkipsWeekendsAndColombianHolidays`: desde el viernes anterior a un lunes festivo, 1 día hábil cae el martes; con país `US` el mismo caso cae el lunes.
- [ ] **Step 2: Correr y ver el RED.** `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"` falla por 112≠114. Evidencia: salida literal.
- [ ] **Step 3: Implementar** las constantes, las dos `PermissionDefinition` (etiquetas «Ver exámenes médicos» y «Gestionar exámenes médicos», categoría «Recursos humanos», `RequiredModules: [ModuleKeys.Hr]`, `Shape = new("hr.medical", "Exámenes médicos", "read"|"manage")`), la lista de `admin`, las políticas, los seeds de las pruebas de integración de Hr y el adaptador, que reutiliza `SupportBusinessCalendarSource` o extrae su helper.
- [ ] **Step 4: Correr y ver el GREEN** (mismos comandos, más las pruebas del calendario).
- [ ] **Step 5: Commit** `feat(hr): medical exam permissions and business-day calendar`.

### Task 2: Dominio de exámenes y reglas de estado

**Files:**
- Create: los archivos de dominio listados arriba; `tests/Modules/Hr/Modules.Hr.UnitTests/MedicalExamRulesTests.cs` (usar el proyecto de pruebas unitarias de Hr existente; si no hay, ubicar donde estén las del dominio de Hr).

**Interfaces:**
- Produces:
  - `enum MedicalExamType { PreEmployment, Periodic, OccupationChange, PostIncapacity, ReturnToWork, FollowUp, Exit }`.
  - `MedicalExam.Create(MedicalExamId id, Guid tenantId, EmployeeId employeeId, MedicalExamType type, DateOnly examDate, DateOnly today, string? provider, string? restrictions, string? recommendations, DateOnly? restrictionsUntil, DateTimeOffset now)` valida el §5.1 y lanza el error de dominio del §7.3 (`future_date`, `restrictions_until_invalid`); además `Update(...)` con los mismos parámetros, `SetDocument(Guid? fileId, now)`, y `Version`/`Touch()` al estilo de `OrganizationSetting`.
  - `ExitExamRequirement` con `EmployeeId`, `ContractId`, `TerminatedOn`, `DueDate`, `Status` (`Pending|Done|NotAttended`), `ExamId?`, `NotifiedOn?`, `Notes?`.
  - `MedicalExamRules.Recalculate(IReadOnlyList<ExitExamRequirement> requirements, IReadOnlyList<MedicalExam> exams)`: reglas del §5.3, determinista; cada requerimiento toma el examen `Exit` más viejo con `ExamDate ≥ TerminatedOn` que no tomó otro; sin examen vuelve a `NotAttended` si hay `NotifiedOn`, y a `Pending` si no.
  - `MedicalExamRules.StatusOf(...)` y `NextPeriodicDue(DateOnly lastExam, int frequencyMonths)`, según el §5.4: el orden de los seis estados, `DueSoonDays = 30`, universo fuera = `null`.

- [ ] **Step 1: Escribir las pruebas que fallan** (nombres exactos): `Recalculate_ClosesTheOldestRequirementWithTheOldestQualifyingExam`, `Recalculate_TwoExitsTwoRequirements_NeitherSharesAnExam`, `Recalculate_ReopensToNotAttendedWhenThereWasANotice`, `Recalculate_ReopensToPendingWhenThereWasNoNotice`, `Recalculate_ExamBeforeTerminatedOn_DoesNotClose`, `StatusOf_FollowsTheFirstMatchingRule` (una fila por cada uno de los seis estados, más `null` para desactivado y para retirado con egreso cerrado), `NextPeriodicDue_AddsMonths`, `Create_RejectsFutureDate`, `Create_RejectsRestrictionsUntilWithoutRestrictions`.
- [ ] **Step 2: Correr y ver el RED** por compilación (los tipos no existen). `dotnet build` y `dotnet test <proyecto> --filter "FullyQualifiedName~MedicalExamRulesTests"`.
- [ ] **Step 3: Implementar** el dominio con las firmas de arriba. Sin EF ni dependencias de infraestructura.
- [ ] **Step 4: GREEN** con el mismo filtro.
- [ ] **Step 5: Commit** `feat(hr): medical exam domain rules`.

### Task 3: Persistencia, cargo y egreso al terminar

**Files:**
- Modify: `Modules.Hr.Infrastructure/Persistence/HrDbContext.cs`, `Modules.Hr.Domain/Position.cs` (`ExamFrequencyMonths : int?`, `SetExamFrequency(int? months, now)` valida 1..36), `Modules.Hr.Application/HrSettingsDtos.cs` (`PositionDto` suma `int? ExamFrequencyMonths`), `TerminateContract.cs`, `HrEndpoints.cs`.
- Create: configuración EF de `medical_exams` y `exit_exam_requirements` (tablas `hr.medical_exams` con índice único `(TenantId, EmployeeId, Type, ExamDate)`, nombre del índice fijo, y `hr.exit_exam_requirements`); migración con el **backfill del §5.3** (contratos `Terminated` cuyo `DueDate` calculado cae hoy o después); `SetPositionExamFrequency.cs`.
- Test: `tests/Modules/Hr/Modules.Hr.IntegrationTests/MedicalExamPersistenceApiTests.cs`.

**Interfaces:**
- Consumes: Task 1 (`IHrBusinessCalendar`) y Task 2 (dominio).
- Produces: `PUT /api/v1/tenants/{tenantId}/hr/positions/{id}/exam-frequency`, body `{ "months": int? }`, `If-Match` del cargo, política `hr.settings.manage`, responde `PositionDto`; error `hr.settings.exam_frequency_invalid` (422). `TerminateContract` crea el `ExitExamRequirement` en la misma unidad de trabajo, con `DueDate = AddBusinessDaysAsync(tenant, date, 5)`. Desactivar **no** lo crea.

- [ ] **Step 1: Escribir las pruebas de integración que fallan:** `PutExamFrequency_StoresMonthsAndBumpsVersion`, `PutExamFrequency_OutOfRange_Returns422WithCode`, `PutPosition_WithOnlyName_KeepsExamFrequency`, `Terminate_CreatesPendingExitRequirementDueInFiveBusinessDays`, `Deactivate_DoesNotCreateExitRequirement`, `Migration_BackfillsOnlyContractsStillWithinTheDeadline`.
- [ ] **Step 2: Correr y ver el RED** filtrando por `MedicalExamPersistenceApiTests`. Primero compilación; el RED de contenedores sólo si la compilación pasa.
- [ ] **Step 3: Implementar** el mapeo, la migración, el endpoint y el cambio en `TerminateContract`. La traducción de la violación del índice único a `hr.medical_exam.duplicate` va en Infrastructure.
- [ ] **Step 4: GREEN** con el mismo filtro.
- [ ] **Step 5: Commit** `feat(hr): exam frequency per position and exit requirement on termination`.

### Task 4: CRUD de exámenes, egreso y lectura del empleado

**Files:**
- Create: los casos de uso `RecordMedicalExam`, `UpdateMedicalExam`, `DeleteMedicalExam`, `MarkExitNotAttended`, `GetEmployeeMedicalExams`, los DTOs del §7.1 y `MedicalExamEndpoints.cs` (agregado al grupo de `HrEndpoints.cs`).
- Test: `MedicalExamsApiTests.cs`, `MedicalExamAccessApiTests.cs`.

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: los endpoints `GET|POST /employees/{id}/medical-exams`, `PUT|DELETE …/{examId}` con `If-Match` (428/412), `POST /employees/{id}/exit-exam/not-attended`; cada escritura responde `EmployeeMedicalExamsDto` (§7.1). Todo exige `hr.employee.read` y además `hr.medical.read` (lecturas) o `hr.medical.manage` (escrituras, que también requieren read). Después de **cada** escritura se llama `MedicalExamRules.Recalculate` y se guarda en la misma unidad de trabajo.

- [ ] **Step 1: Escribir las pruebas que fallan:** `Post_RecordsExamAndReturnsWholeDto`, `Post_Duplicate_Returns422Duplicate`, `Put_WithoutIfMatch_Returns428`, `Put_StaleVersion_Returns412`, `Put_ChangingExitToPeriodic_ReopensTheRequirement`, `Delete_TheExitExam_ReopensTheRequirement`, `NotAttended_SetsNoticeAndKeepsItAcrossRecalculation`, `NotAttended_WithoutPending_Returns422NoPendingExit`, `Get_Deactivated_ReturnsNullStatusWithHistory`, `Get_WithEmployeeReadButNoMedicalRead_Returns403`, `Post_FutureDate_Returns422FutureDate`.
- [ ] **Step 2: RED** por filtro de nombre (`FullyQualifiedName~MedicalExamsApiTests`).
- [ ] **Step 3: Implementar.** El `EmployeeMedicalExamsDto` calcula `status`, `nextPeriodicDue`, `examFrequencyMonths` (del cargo vigente de la asignación del contrato activo), `exitRequirement` (el más reciente) con `businessDaysLeft` y `overdue` usando `IHrBusinessCalendar` y el día del tenant (`ITenantClock`), y `exams` de la fecha más reciente a la más vieja.
- [ ] **Step 4: GREEN** por filtro; luego las dos clases completas (`MedicalExamsApiTests`, `MedicalExamAccessApiTests`) **una vez** al cerrar el bloque.
- [ ] **Step 5: Commit** `feat(hr): medical exam CRUD and exit requirement lifecycle`.

---

## Bloque B — control, PDF, actividad, importación

### Task 5: Resumen de control

**Files:** Create `GetMedicalExamOverview.cs`; Test `MedicalExamOverviewApiTests.cs`.

**Interfaces:** `GET /medical-exams/overview?status=&search=&page=&pageSize=` con `hr.medical.read` + `hr.employee.read`; responde el §7.2. `status` acepta varios valores separados por coma; uno desconocido es 422 `hr.medical_exam.status_invalid`. El orden es urgencia primero y luego `nextDue` ascendente. Los `counts` ignoran `status` y `search`. **Pagina en la base**: no se traen todos los empleados a memoria.

- [ ] **Step 1: Pruebas que fallan:** `Overview_CountsIgnoreTheFilters`, `Overview_StatusList_FiltersWithComma`, `Overview_OrdersByUrgencyThenNextDue`, `Overview_UniverseIsActivePlusRetiredWithPendingExit`, `Overview_Search_MatchesNameOrDocument`, `Overview_UnknownStatus_Returns422`, `Overview_WithoutMedicalRead_Returns403`, `Overview_Paginates_20Employees_PageSize5`.
- [ ] **Step 2: RED**, por filtro `MedicalExamOverviewApiTests`.
- [ ] **Step 3: Implementar** con una consulta paginada (EF) que arma el último examen y el próximo; el estado se resuelve en el servidor con las reglas de Task 2.
- [ ] **Step 4: GREEN**, y la clase completa una vez.
- [ ] **Step 5: Commit** `feat(hr): medical exam overview`.

### Task 6: PDF del certificado

**Files:** Create `OpenMedicalExamUploadSession.cs`, `SetMedicalExamDocument.cs`; reutiliza `IHrDocumentStore` y `HrDocumentPolicy` (PDF, 10 MB); Test `MedicalExamDocumentApiTests.cs`.

**Interfaces:** `POST /medical-exams/upload-session` (`hr.medical.manage`; misma forma que `HrUploadSessionDto`), `PUT /employees/{id}/medical-exams/{examId}/document` con `{ fileId: Guid? }` (`null` quita), `GET /employees/{id}/medical-exams/{examId}/document` (URL firmada de 5 minutos, `hr.medical.read`). Un archivo ajeno, faltante o rechazado es 422, como el resto de documentos de Hr.

- [ ] **Step 1: Pruebas que fallan:** `UploadSession_RequiresMedicalManage`, `SetDocument_AttachesAndExamShowsHasDocument`, `SetDocument_Null_RemovesIt`, `SetDocument_ForeignFile_Returns422`, `GetDocument_WithoutMedicalRead_Returns403`.
- [ ] **Step 2: RED** por filtro; **Step 3** implementar copiando el patrón de `SetHrDocument.cs`; **Step 4** GREEN.
- [ ] **Step 5: Commit** `feat(hr): medical exam certificate pdf`.

### Task 7: Actividad y auditoría

**Files:** Modify `HrActivityAction.cs` (`MedicalExamRecorded`, `MedicalExamUpdated`, `MedicalExamDeleted`, `ExitExamNotAttended`), `HrActivityCategory.cs` (`Medical`), `ActivityChanges.cs`, `HrAuditActions.cs` (`hr.medical_exam.created|updated|deleted|document_updated|imported|exit_not_attended`), `ListEmployeeActivity.cs`, los casos de uso de las Tasks 4 y 6; Test `MedicalExamActivityApiTests.cs`.

**Interfaces:** los cambios de actividad registran **sólo tipo, fecha y presencia de PDF**. `GET …/activity` omite la categoría `Medical` a quien no tiene `hr.medical.read`. Eliminar un empleado borra sus exámenes y requerimientos.

- [ ] **Step 1: Pruebas que fallan:** `Activity_NeverContainsRestrictionsRecommendationsOrProvider`, `Activity_MedicalCategoryHiddenWithoutMedicalRead`, `Audit_RowsExistForEachWrite`, `DeleteEmployee_RemovesExamsAndRequirements`.
- [ ] **Step 2-4:** RED por filtro, implementar, GREEN.
- [ ] **Step 5: Commit** `feat(hr): medical exam activity and audit`.

### Task 8: Importación de historial

**Files:** Create `MedicalExamImportColumns.cs` (columnas exactas del §8), `IExcelMedicalExamImporter`, `IMedicalExamImportTemplateBuilder`, los dos adaptadores ClosedXML, `PreviewMedicalExamImport.cs`, `ImportMedicalExams.cs`, `GetMedicalExamImportTemplate.cs`; Test `MedicalExamImportApiTests.cs` + pruebas del analizador.

**Interfaces:** `GET /medical-exams/import/template`, `POST /medical-exams/import/preview` y `POST /medical-exams/import` (multipart `file`, `.DisableAntiforgery()`, `hr.medical.manage`). Hojas «Exámenes» y «Valores válidos», 1000 filas, 5 MB. La vista previa devuelve filas válidas y errores por fila con código; un solo error y no se escribe nada. Un duplicado, contra la base o contra otra fila del archivo, es error de fila. Un `Egreso` importado no exige contrato terminado y, si existe un egreso `Pending` que cumple la regla, lo cierra (se llama `Recalculate`). Una sola entrada de auditoría con el conteo.

- [ ] **Step 1: Pruebas que fallan:** `Preview_WritesNothing`, `Import_AllOrNothing_OneBadRowWritesNone`, `Import_Twice_SecondRunIsAllDuplicates`, `Import_DuplicateInsideTheFile_IsRowError`, `Import_UnknownDocument_IsRowError`, `Import_FutureDate_IsRowError`, `Import_ExitRow_ClosesPendingRequirement`, `Import_RestrictionsUntilWithoutRestrictions_IsRowError`, `Template_HasTheEightColumnsAndTheValidValuesSheet`.
- [ ] **Step 2-4:** RED por filtro, implementar copiando el patrón de la importación de empleados, GREEN; clases completas del bloque una vez.
- [ ] **Step 5: Commit** `feat(hr): medical exam history import`.

---

## Bloque C — recordatorios en la campana

### Task 9: Outbox, worker y eventos de Hr

**Files:**
- Create: `HrOutboxMessage` (proyección con `ExcludeFromMigrations`, como `ServiceContractsOutboxMessage`), `HrMedicalExamsReminderWorker`, `HrMedicalExamsReminderProcessor`, stores de corrida por tenant y de claim por miembro y día (`TenantRunStore`, `DailyDigestStore` propios de Hr; sus tablas van en la migración), `MedicalExamEventPublisher`.
- Modify: `IHrMemberDirectory.cs` (`ListWithPermissionAsync(Guid tenantId, string permission, CancellationToken ct)`), su adaptador en `src/Bootstrapper/` con `IRolePermissionChecker.AnyGrantsAsync`, `TerminateContract.cs`, `HrInfrastructureExtensions.cs`, `appsettings.json` (`Hr:SweepMinutes` = 15, `Hr:SendHourLocal` = 8).
- Test: `MedicalExamRemindersApiTests.cs`.

**Interfaces:** eventos `hr.medical-exams.due-digest.v1` (payload camelCase `{ tenantId, memberId, runDate, pendingCount, occurredAt }`, correlación `{tenant}:{member}:{runDate}`) y `hr.medical-exams.exit-required.v1` (`{ tenantId, memberId, employeeId, employeeName, dueDate, occurredAt }`, correlación `{tenant}:{member}:{contractId}`). `pendingCount = ExitPending + NoExams + Overdue + DueSoon` y no se publica con 0. Destinatarios: `hr.medical.manage`. El `exit-required` se escribe en `TerminateContract`, **antes del commit y en la misma transacción**; sin destinatarios la terminación sigue sin mensajes; si la consulta falla, falla la terminación. Ningún payload lleva restricciones ni tipo de examen. El worker corre desde la hora local del tenant, sólo si está activo y con el módulo `hr`, una corrida por tenant y día y un claim por miembro y día.

- [ ] **Step 1: Pruebas que fallan** (las del procesador se prueban con un reloj y un calendario controlados, sin esperar el temporizador): `Processor_PublishesOneDigestPerManagerWithTheCount`, `Processor_DoesNotRunBeforeSendHourLocal`, `Processor_RunsOncePerTenantPerDay`, `Processor_SkipsMembersWithoutManagePermission`, `Processor_ZeroPending_PublishesNothing`, `Terminate_PublishesExitRequiredToEachManager`, `Terminate_WithNoManagers_StillSucceedsWithoutMessages`, `Payloads_NeverContainClinicalText`.
- [ ] **Step 2-4:** RED por filtro, implementar copiando `ServiceContractsReminderWorker`/`Processor` y `OverdueDigestEventPublisher`, GREEN.
- [ ] **Step 5: Commit** `feat(hr): medical exam reminder worker and events`.

### Task 10: Notifications y cierre de la documentación

**Files:**
- Modify: `Modules.Notifications.Domain/InAppNotificationKind.cs` (`HrMedicalExamsDue`, `HrExitExamRequired`), `Modules.Notifications.Application/InApp/InAppNotificationMapper.cs` (`KeysOf`: `{ pendingCount, runDate }` y `{ employeeId, employeeName, dueDate }`), `NotificationsInfrastructureExtensions.cs` (registro del consumidor), `docs/superpowers/specs/2026-11-22-in-app-notifications-backend-design.md` (§6, §8.5, §11, §12), `docs/estado.md` (en `develop`, al mergear, nunca en la rama).
- Create: `Modules.Notifications.Infrastructure/InApp/HrInAppConsumer.cs` (consumidor `notifications.in-app.hr`, derivado de `InAppOutboxConsumer`; dedupe keys `hr-medical-exams:{runDate}` y `hr-exit-exam:{employeeId}:{dueDate}`).
- Test: pruebas del consumidor y del mapeo en el proyecto de pruebas de Notifications.

- [ ] **Step 1: Pruebas que fallan:** `Consumer_MapsDueDigestToHrMedicalExamsDue`, `Consumer_MapsExitRequiredToHrExitExamRequired`, `Consumer_DedupesTheSameRunDate`, `Mapper_KeysAreAlwaysPresent_NullWhenMissing`, `Consumer_DropsEventsOlderThan24h`.
- [ ] **Step 2-4:** RED por filtro, implementar, GREEN.
- [ ] **Step 5: Verificación final del bloque y de la rama:** la suite de los proyectos de prueba de Hr, Notifications y `ArchitectureTests`, **una sola vez** y con `-- xUnit.MaxParallelThreads=4`. Reportar la salida literal.
- [ ] **Step 6: Commit** `feat(notifications): hr medical exam kinds and consumer`.

## Cierre

- Revisión fuerte única, de toda la rama, con el modelo más capaz.
- Merge a `develop` (sin push), con `git branch --show-current` antes de tocar y otra vez antes de commitear. Después se actualiza `docs/estado.md` en `develop`.
- El deploy es **backend primero**: el frontend tolera `examFrequencyMonths` ausente y el 404 de los endpoints nuevos.
