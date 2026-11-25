# Reajuste de precio de los contratos de servicio (IPC) — Plan de implementación del backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de este repo mandan sobre el proceso del skill** (bloques, una revisión por bloque; ver «Global Constraints»).

**Goal:** Un contrato puede programar su precio desde un período sin generar, avisa al encargado 10 días antes de generar el cobro de la revisión, retiene el cobro de un contrato por IPC hasta que alguien decide, y el IPC del año se carga una vez por tenant y se confirma en lote.

**Architecture:** El cobro separa período (`period_date`, único por contrato) y vencimiento (`due_date`). Una función pura de dominio (`PriceReview.Of`) calcula período de revisión, fechas y estado; la usan los DTO, las acciones, la lista y el digest. `ChargeGenerator` gana el cambio programado pendiente y un modo (`Creation` | `Run`): en `Run` retiene (`Ipc`) o marca «sin revisar» (`Manual`), y corre el vencimiento de lo liberado. Toda acción que resuelve una revisión corre la generación `Run` en su misma transacción. Revisiones del tenant: se calculan en memoria sobre los contratos `Active` y los cambios pendientes (sin columna materializada, §10 lo permite).

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, CQ propio, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-24-reajuste-precio-contratos-design.md` (aprobado, commit `859030876`). §8 es el contrato HTTP; el frontend ya está publicado en `qep-frontend` `develop` (`06141142`) contra él. Léelo junto con este plan.

## Global Constraints

- **Worktree y rama:** `feature/contract-price-adjustment` (cortada de `develop` en `7c580b235`), en `..\qep-backend-worktrees\contract-price-adjustment`. Nada se edita en el checkout `qep-backend` ni en ninguna carpeta del frontend. Antes de cada commit: `git branch --show-current` **en ese momento**. Un subagente que corra scripts hace `Set-Location` al worktree primero. Edita con las herramientas de edición, no con `Set-Content` (rompe las tildes).
- **Commits:** convencionales, uno por tarea, **sin** `Co-Authored-By` ni atribución de IA de ningún tipo.
- **TDD:** RED antes que GREEN, con la salida literal de ambos pegada en el reporte. Un RED que falla por compilación se prueba con `dotnet build` del proyecto de pruebas, **sin** contenedores.
- **Bloques:** tres bloques, un implementador Sonnet por bloque y **una** revisión por bloque. La revisión fuerte (modelo más capaz, toda la rama) corre una sola vez, al final.
- **Integración:** mientras iteras, filtrada por nombre de prueba (`--filter "FullyQualifiedName~NombreDeLaPrueba"`); cada clase tocada completa **una vez**, al final del bloque. Antes de cada corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una de otra sesión, espera y **nunca** la detengas. Nunca dos corridas de integración a la vez. Las pruebas corren en **primer plano**. Cada corrida va precedida, en línea aparte, de `$env:Seed__Enabled = "false"`, y lleva `-- xUnit.MaxParallelThreads=4`. Si `Api.exe` está corriendo el build falla por archivo bloqueado: avisa, no lo detengas.
- **Para mergear a `develop`:** `.\scripts\test-feature.ps1` en verde, leyendo sus avisos. `docs/estado.md` **no** se toca en la rama.
- **Comandos:** Windows PowerShell (`A; if ($?) { B }`). Textos al usuario, comentarios y XML doc en español colombiano tuteando; identificadores, códigos y nombres de enum sin traducir. Los DTO nuevos llevan en su XML doc el porqué BFF (CLAUDE.md).
- **No inventar:** todo campo, ruta y código sale del spec o de este plan. Lo que falte se marca como decisión pendiente del owner.
- **Nombres desplegados que no cambian:** esquema `billing`, clave de módulo `billing`, permisos `billing.service.read|manage` (sin permisos nuevos, P-4), rutas `/api/v1/tenants/{tenantId}/billing`, códigos `billing.*`, columna `last_generated_due_date`.
- **Valores fijos:** `ChargeGenerator.WindowDays` = 5; `PriceReview.NoticeDays` = 15 (aviso = período − 15 = generación − 10); vencimiento de un liberado = `max(period, today + 5)`; `AdvanceReview` suma `12 × k` meses a `NextReviewDate` (menor `k ≥ 1` con resultado `> resuelto`), calculado como `NextReviewDate.AddMonths(12 × k)` en una sola suma, no sumando de a un año; `ipcYear = reviewPeriod.Year − 1` (P-2); puntos adicionales `0…50` con 2 decimales (P-5); tasa IPC `−10.00…100.00` con 2 decimales; año IPC `2000…(hoy del tenant).Year + 1`; `confirm-ipc` 1 a 200 ítems; `GET /price-reviews` `pageSize` 25 por defecto, tope 100; `upcomingPeriodDates` hasta 12.
- **Defaults de las preguntas abiertas (el frontend los implementó):** P-1 ningún cobro generado cambia de monto; P-2 IPC del año calendario anterior a la revisión; P-3 nada se le avisa al cliente; P-4 `billing.service.manage`; P-5 puntos de 0 a 50.
- **Auditoría** por `IServiceContractsAuditPublisher` (atómica, misma unidad de trabajo), no `IAuditRecorder` (ver R-3). Acciones: `billing.service.price_review_updated`, `billing.service.price_scheduled`, `billing.service.price_schedule_cancelled`, `billing.service.price_kept` (recurso `service`), `billing.ipc.saved` (recurso `ipc_rate`, `resourceId` = el año). La aplicación de un programado en la corrida no audita.
- **Despliegue con corte** (R-1): la migración no es aditiva para pods viejos. El handoff lo dice.

## Fallos del spec frente al código y regla que sigue el plan

- **R-1. La migración no es aditiva** (spec, encabezado: «todo lo nuevo es aditivo»). `charges.period_date` y `services.next_review_date` son `NOT NULL` sin default posible, así que un pod viejo que inserta un cobro o crea un contrato falla, y el worker viejo deduplica por `IX_charges_service_due`, que desaparece. **Regla:** se despliega **con corte** (`Recreate` o el backend a cero réplicas), README § «Migraciones con pods viejos y nuevos», regla 2. El frontend tolera el backend viejo y el nuevo, así que no hay despliegue conjunto.
- **R-2. `ApiExceptionHandler` no se toca** (spec §8 «mapeo en `ApiExceptionHandler`»). El mapeo es por tipo de excepción: `ServiceContractsDomainException` → 422, `ResourceNotFoundException` → 404, `ResourceConflictException` → 409, `RequestConcurrencyException` → 412. Los códigos nuevos viajan en esas excepciones.
- **R-3. Auditoría por `IServiceContractsAuditPublisher`** (spec §6.14 dice `IAuditRecorder`). El módulo no puede usar `IAuditRecorder`: acumula en `TenancyDbContext` y la fila se perdería (`ServiceContractsAuditPublisher.cs`, comentario de cabecera).
- **R-4. Dos actualizaciones simultáneas de la misma tasa IPC** (spec §6.11 cubre el insert simultáneo y el `expectedRate` viejo, no dos updates con el mismo `expectedRate`). **Regla:** `ipc_rates.version` es token de concurrencia; el handler convierte el 412 que levanta la unidad de trabajo en `409 billing.ipc.rate_changed` (lo guardado ya no es lo esperado). El frontend trata ese 409 como conflicto de tasa (`use-save-ipc-rate.ts`).
- **R-5. Choque de una acción del usuario con la corrida diaria** (spec §6.11 sólo cubre la corrida). Una acción que libera y genera puede chocar con la corrida en `IX_charges_service_period`, y dos «programar» simultáneos en `IX_service_price_schedules_pending`. **Regla:** `ServiceContractsUnitOfWork` traduce esas dos violaciones (por nombre de índice) a `412 concurrency.conflict`, lo mismo que el token de versión. La corrida no usa la unidad de trabajo y sigue releyendo una vez.
- **R-6. `Charge.Generate` y `ChargeGenerator.Generate` conservan su firma vieja** como sobrecarga (`period = due`, sin «sin revisar»; modo `Creation` sin programado), además de las firmas nuevas del spec. Una docena de pruebas existentes los usan y no ejercen nada de esta feature.
- **R-7. `version` es `int` en el dominio y `bigint` en la base**; los DTO lo exponen como `long` (como hoy). El spec escribe `long`: coincide en el cable.
- **R-8. Formato del motivo automático** (spec: `"IPC {año} ({rate} %)"` y `" + {puntos} puntos"`, sin formato numérico). **Regla:** números con dos decimales y coma decimal (`es-CO`, `"0.00"`): `IPC 2026 (5,20 %) + 2,00 puntos`. Es un texto que lee una persona en el historial.

Sin discrepancias entre el spec y el frontend publicado: rutas, cuerpos, campos, enums y los 15 códigos nuevos coinciden con `service-contracts.api.ts`, `types/service-contracts.ts` y el `kind` `ServicePriceReviewsDue` (`data { dueCount, heldCount, runDate }`, destino `/service-contracts/price-reviews`). Lo único que el frontend no manda es `priceReview.nextReviewDate: null` al crear (siempre manda una fecha); el backend acepta ambos.

## Review Focus

1. **Retención que dura varios períodos:** al liberar, todos los períodos liberados nacen juntos con vencimiento `max(period, today + 5)` (pueden coincidir), los siguientes conservan su ancla y ninguno se duplica. Pruebas en la Tarea 2 (unitaria) y la Tarea 4 (corrida real, dos cobros con el mismo `due_date`).
2. **La corrida diaria y una acción del usuario liberan el mismo contrato a la vez:** uno gana, el otro recibe 412 (acción) o relee (corrida); nunca un cobro duplicado ni un programado `Applied` sin su cobro. Pruebas en las Tareas 3 (traducción) y 4 (relectura por el índice nuevo).
3. **Pausa sobre un contrato retenido:** pausar guarda en `carried_held_periods` los períodos con `p − 5 <=` día de la pausa y borra `held_since`; reanudar los deja retenidos (`held_since` = día de la reanudación) y al liberarlos nacen con `max(period, today + 5)`; lo que cayó en la pausa nunca se cobra; varias pausas acumulan huecos sin duplicar un período; sin retención al pausar, una sola revisión después y nunca se componen IPC de varios años en una revisión. Pruebas en las Tareas 2 y 10.
4. **Dos personas guardan la tasa IPC del mismo año:** nunca gana la última en silencio (insert simultáneo → `concurrent_first_write`; update simultáneo → `rate_changed`, R-4). Prueba en la Tarea 7.
5. **Datos existentes al migrar:** ningún contrato existente queda retenido ni sin cobrar (`Manual`), `next_review_date` cae en un aniversario `>= hoy UTC + 15`, un ancla 29 feb da 28 feb, y `period_date = due_date` en todo cobro. Prueba en la Tarea 3.

---

## Bloque 1 — Dominio, generación, persistencia y corrida (Tareas 1 a 4, un implementador)

### Task 1: Dominio del reajuste

**Files:**
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Domain/ServiceContract.cs`, `Charge.cs`, `ServicePriceChange.cs`, `ServiceContractsErrorCodes.cs`
- Create (Domain): `AdjustmentMode.cs`, `ReviewState.cs`, `PriceChangeOrigin.cs`, `ServicePriceSchedule.cs` (con `PriceScheduleSource` y `PriceScheduleStatus`), `IpcRate.cs`, `PriceReview.cs`
- Modify (renombres): `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/ServiceContractTests.cs`, `ChargeGeneratorTests.cs`; las referencias a `LastGeneratedDueDate` en `Modules.ServiceContracts.IntegrationTests` (`ServiceContractsReminderProcessorTests`, `ServiceContractsPersistenceTests`, `ServiceContractApiTests`)
- Test: create `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/PriceReviewTests.cs`, `ServicePriceScheduleTests.cs`, `IpcRateTests.cs`

**Interfaces:**
- Produces (enums, guardados por nombre): `AdjustmentMode { Manual, Ipc }`, `ReviewState { None, Upcoming, Due, Held, Scheduled }`, `PriceChangeOrigin { Edit, Scheduled, Ipc }`, `PriceScheduleSource { Manual, Ipc }`, `PriceScheduleStatus { Pending, Applied, Cancelled }`.
- Produces (`ServiceContractsErrorCodes`): `ServiceReviewDateInvalid` = `billing.service.review_date_invalid`, `ServiceNotActive` = `billing.service.not_active`, `PriceScheduleAlreadyPending`, `PriceScheduleNotFound`, `PriceSchedulePeriodGenerated`, `PriceScheduleSamePrice`, `PriceScheduleCurrencyLocked`, `PriceScheduleAfterReview` (`billing.price_schedule.*`), `IpcNotSet`, `IpcInUse`, `IpcRateChanged` (`billing.ipc.*`), `PriceReviewNotIpc`, `PriceReviewYearMismatch`, `PriceReviewNotPending`, `PriceReviewPriceChanged` (`billing.price_review.*`), con los valores literales de spec §8.
- Produces (`ServiceContract`):
  - `LastGeneratedDueDate` → `LastGeneratedPeriodDate`; `NextDueDateToGenerate()` → `NextPeriodToGenerate()` (misma aritmética, `null` si no está `Active`); `MarkGenerated(DateOnly periodDate)`.
  - `DateOnly NextUngeneratedPeriod()`: lo mismo sin mirar el estado (la usan el detalle de un pausado y la invariante en `PUT …/price-review`).
  - `AdjustmentMode AdjustmentMode`, `decimal IpcExtraPoints`, `DateOnly NextReviewDate`, `DateOnly? HeldSince`; `public const decimal MaxIpcExtraPoints = 50m`.
  - `DateOnly DefaultNextReviewDate()`: el primer `FirstDueDate.AddMonths(12k)` (`k ≥ 1`) posterior a `LastGeneratedPeriodDate` (o a `FirstDueDate` si es nulo).
  - `void InitializePriceReview(AdjustmentMode mode, decimal ipcExtraPoints, DateOnly? nextReviewDate)`: se llama **después** de la generación de crear o importar; `null` ⇒ `DefaultNextReviewDate()`; una fecha `<= LastGeneratedPeriodDate` ⇒ `ServiceReviewDateInvalid`; `Manual` guarda 0 puntos. No toca `Version`.
  - `void UpdatePriceReview(AdjustmentMode mode, decimal ipcExtraPoints, DateOnly nextReviewDate, DateTimeOffset now)`: `Cancelled` ⇒ `ServiceCancelled`; fecha `<= LastGeneratedPeriodDate` ⇒ `ServiceReviewDateInvalid`; `Manual` ⇒ 0 puntos; `Touch`. Se permite en `Paused`.
  - `void AdvanceReview(DateOnly resolved)`: la regla única de §5.
  - `void KeepPrice(DateTimeOffset now)`: `Cancelled` ⇒ `ServiceCancelled`; no `Active` ⇒ `ServiceNotActive`; `AdvanceReview(PriceReview.ReviewPeriodOf(this))`; `Touch`.
  - `internal void Hold(DateOnly today)`, `internal void ReleaseHold()`, `internal void ApplyScheduledPrice(ServicePriceSchedule schedule, DateOnly period, DateTimeOffset now)` → devuelve `ServicePriceChange` (para `ChargeGenerator`).
  - `Pause` y `Cancel` ponen `HeldSince = null`.
  - `IReadOnlyList<DateOnly> UpcomingPeriods()`: hasta 12 períodos sin generar desde `NextPeriodToGenerate()`; `[]` si no está `Active`; con `Ipc`, sólo hasta el período de revisión inclusive.
- Produces (`ServicePriceSchedule`): `Id`, `TenantId`, `ServiceId`, `EffectiveFrom`, `NewPrice`, `Currency`, `Source`, `int? IpcYear`, `decimal? IpcRate`, `decimal? ExtraPoints`, `Reason`, `Guid? CreatedByMemberId`, `Status`, `DateOnly? AppliedPeriodDate`, `CreatedAt`, `AppliedAt`, `CancelledAt`.
  - `static ServicePriceSchedule Schedule(ServiceContract service, ServicePriceSchedule? pending, decimal newPrice, DateOnly? effectiveFrom, string? reason, Guid? memberId, DateTimeOffset now)` (fuente `Manual`; `effectiveFrom` nulo = período de revisión). Chequeos en este orden: `Cancelled` ⇒ `ServiceCancelled`; no `Active` ⇒ `ServiceNotActive`; `pending` no nulo ⇒ `PriceScheduleAlreadyPending`; `effectiveFrom <= LastGeneratedPeriodDate` ⇒ `PriceSchedulePeriodGenerated`; `Ipc` y `AppliesTo > período de revisión` ⇒ `PriceScheduleAfterReview`; precio (con escala 2) igual al actual ⇒ `PriceScheduleSamePrice`; motivo vacío ⇒ `PriceChangeReasonRequired` (recortado, hasta 500). Hace `Touch` del servicio.
  - `static ServicePriceSchedule ConfirmIpc(ServiceContract service, int ipcYear, decimal ipcRate, decimal newPrice, string reason, Guid? memberId, DateTimeOffset now)`: fuente `Ipc`, `EffectiveFrom` = período de revisión, copia `IpcExtraPoints`; `Touch`.
  - `internal void Apply(DateOnly period, DateTimeOffset now)`; `void Cancel(ServiceContract service, DateTimeOffset now)` (sólo `Pending`; `Touch` del servicio).
- Produces (`IpcRate`): `TenantId`, `int Year`, `decimal Rate`, `Guid? UpdatedByMemberId`, `UpdatedAt`, `int Version`; `const decimal MinRate = -10m, MaxRate = 100m`, `const int MinYear = 2000`; `static IpcRate Create(Guid tenantId, int year, decimal rate, Guid? memberId, DateTimeOffset now)`; `bool Change(decimal rate, Guid? memberId, DateTimeOffset now)` (falso y sin tocar nada si la tasa es la misma; si no, sube `Version`).
- Produces (`ServicePriceChange`): `PriceChangeOrigin Origin`, `Guid? ScheduleId`; `Record(…)` gana `origin` y `scheduleId` (el `Update` de hoy pasa `Edit`, null).
- Produces (`Charge`): `DateOnly PeriodDate`, `bool PriceUnreviewed`; `static Charge Generate(ServiceContract service, DateOnly periodDate, DateOnly dueDate, bool priceUnreviewed, DateTimeOffset now)` y la sobrecarga vieja de 3 argumentos (R-6).
- Produces (`PriceReview`, estático): `public const int NoticeDays = 15`;
  - `PriceReviewSnapshot Of(ServiceContract service, ServicePriceSchedule? pending, DateOnly today)` con `public sealed record PriceReviewSnapshot(DateOnly? ReviewPeriodDate, DateOnly? GenerationDate, DateOnly? NoticeFrom, ReviewState State, int? IpcYear, DateOnly? ScheduleAppliesTo)`. No `Active` ⇒ fechas, `IpcYear` y estado `None`. Estado por la tabla de §6.1 en su orden (`Held` > `Scheduled` > `Due` > `Upcoming`). `IpcYear` sólo con `Ipc`.
  - `DateOnly ReviewPeriodOf(ServiceContract service)`: primer período sin generar `>= NextReviewDate` (desde `NextUngeneratedPeriod()`, que respeta el salto de pausa).
  - `DateOnly AppliesTo(ServiceContract service, DateOnly effectiveFrom)`: primer período sin generar `>= effectiveFrom`.
  - `bool Covers(ServiceContract service, ServicePriceSchedule pending)` y `void EnsureNotAfterReview(ServiceContract service, ServicePriceSchedule? pending)` (`Ipc` y `AppliesTo > ReviewPeriodOf` ⇒ `PriceScheduleAfterReview`).
  - `decimal? IpcPrice(decimal price, decimal ipcRate, decimal extraPoints, PriceRounding rounding)`: `rounding.Apply(price × (1 + (ipcRate + extraPoints) / 100))` con escala 2; `<= 0` ⇒ `null`.
  - `string IpcReason(int year, decimal rate, decimal extraPoints)` (R-8; sin puntos cuando son 0).

- [ ] **Step 1: Pruebas que fallan.**
  - `PriceReviewTests`: `AdvanceReviewMovesWholeYearsPastTheResolvedPeriod` (revisión 2027-01-10, resuelto 2027-01-10 ⇒ 2028-01-10; resuelto 2029-03-10 ⇒ 2030-01-10); `AdvanceReviewClampsTheLeapDay` (2028-02-29 resuelto ese día ⇒ 2029-02-28); `TheReviewPeriodIsTheFirstUngeneratedAnchorOnOrAfterTheReviewDate`; `TheStateFollowsTheTableInOrder` (`Held` aunque haya programado; `Scheduled` sólo si cubre; `Due` desde período − 15 inclusive; `Upcoming` antes; `None` pausado con fechas nulas); `TheNoticeStartsTenDaysBeforeGeneration` (período 2027-01-10 ⇒ generación 2027-01-05, aviso 2026-12-26); `TheIpcYearIsTheYearBeforeTheReview` (`Ipc` 2027-01-10 ⇒ 2026; `Manual` ⇒ `null`); `IpcPriceRoundsWithTheTenantRule` (100000 COP, 5.20 + 2 ⇒ `107200.00` con `(0, Nearest)`; 99.99 USD, 5.2 ⇒ `105.19` con `(2, Nearest)`; −100 ⇒ `null`); `TheIpcReasonCarriesYearRateAndPoints` (`IPC 2026 (5,20 %) + 2,00 puntos`; sin puntos `IPC 2026 (5,20 %)`).
  - `ServicePriceScheduleTests`: una prueba por código del orden de `Schedule` (`ACancelledService…`, `APausedServiceIsNotActive`, `ASecondPendingIsAlreadyPending`, `AnEffectiveDateAlreadyGeneratedIsPeriodGenerated`, `InIpcAChangeAfterTheReviewIsAfterReview`, `InManualAChangeAfterTheReviewIsAllowed`, `TheCurrentPriceIsSamePrice` (100 y 100.00), `AnEmptyReasonIsRequired`); `ANullEffectiveDateMeansTheReviewPeriod`; `SchedulingBumpsTheServiceVersion`; `CancelOnlyCancelsAPending`.
  - `IpcRateTests`: `TheSameRateChangesNothing` (`Change` falso, versión igual); `AnotherRateBumpsTheVersion`.
  - `ServiceContractTests`: `InitializeWithoutADateUsesTheAnniversaryAfterTheLastGenerated` (`first_due` futura ⇒ `+12 meses`; `first_due` hace 26 meses mensual con último generado en la ventana ⇒ el aniversario siguiente a ese); `AnExplicitReviewDateMustFollowTheLastGenerated`; `ManualStoresZeroPoints`; `PauseAndCancelClearTheHold`; `UpcomingPeriodsStopAtTheReviewInIpc` (`Manual` ⇒ 12; `Ipc` con revisión en el 3er período ⇒ 3; pausado ⇒ `[]`); `KeepPriceAdvancesFromTheReviewPeriod`. Renombra aquí las pruebas existentes de `NextDueDateToGenerate…` y los usos de `LastGeneratedDueDate`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests`. Esperado: no compila (`PriceReview`, `ServicePriceSchedule`, `IpcRate`, `NextPeriodToGenerate` no existen). Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces». `ChargeGenerator` sólo se ajusta al renombre (la lógica nueva es la Tarea 2). `ServiceContract.Create` no cambia de firma: arranca `Manual`, 0 puntos, `NextReviewDate = FirstDueDate.AddMonths(12)` hasta que `InitializePriceReview` la fije.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests`. Y `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests` (los renombres compilan).
- [ ] **Step 5: Commit** — `git commit -m "feat(service-contracts): model price reviews, scheduled prices and IPC rates"`.

### Task 2: `ChargeGenerator` con programado, retención y «sin revisar»

**Files:**
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Domain/ChargeGenerator.cs`
- Create: `src/Modules/ServiceContracts/Modules.ServiceContracts.Domain/GenerationMode.cs`
- Test: create `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/ChargeGeneratorReviewTests.cs`

**Interfaces:**
- Consumes: Tarea 1 (`PriceReview`, `ServicePriceSchedule.Apply`, `ServiceContract.Hold|ReleaseHold|ApplyScheduledPrice|AdvanceReview`).
- Produces: `enum GenerationMode { Creation, Run }`; `public sealed record GenerationResult(IReadOnlyList<Charge> Charges, IReadOnlyList<ServicePriceChange> PriceChanges, bool ServiceChanged)`; `static GenerationResult Generate(ServiceContract service, ServicePriceSchedule? pending, GenerationMode mode, DateOnly today, DateTimeOffset now)`. La sobrecarga `Generate(service, today, now)` = `Generate(service, null, Creation, today, now).Charges` (R-6). `ServiceChanged` es verdadero si generó, si cambió `HeldSince`, si avanzó la revisión o si aplicó un programado: con eso la corrida decide si guarda.
- Algoritmo: el de §6.3 pasos 1 a 4 y las tres reglas de `held_since` al terminar la pasada, al pie de la letra. «Retenido al empezar» se lee **antes** del primer período. `Origin` de la fila de historial = `Scheduled` (fuente `Manual`) o `Ipc`; motivo y miembro del programado.

- [ ] **Step 1: Pruebas que fallan** en `ChargeGeneratorReviewTests` (los casos de spec §10), contrato mensual ancla día 10:
  - `AnIpcReviewInTheWindowHoldsAndGeneratesNothingFromThere` (`Run`; `HeldSince == today`; ningún cobro del período de revisión ni posterior; los anteriores sí).
  - `AHeldContractKeepsItsHoldSinceWhileStillHeld`.
  - `ReleasingWithACoveringScheduleShiftsTheDueDate` (ejemplo de §6.5: revisión 2027-01-10, confirmado el 2027-01-14 ⇒ cobro período 2027-01-10, vence 2027-01-19, monto nuevo; el de 2027-02-10, si entra en la ventana, vence 2027-02-10). El programado queda `Applied` con `AppliedPeriodDate`, la revisión pasa a 2028-01-10 y `PriceChanges` trae una fila `Ipc`.
  - `SeveralHeldPeriodsAreReleasedTogetherWithTheSameDueDate` (retenido desde 2027-01-05, liberado el 2027-03-20: los períodos 01-10, 02-10 y 03-10 vencen los tres el 2027-03-25; el 04-10 no se genera).
  - `AReleaseThatReachesAnotherReviewHoldsAgainAsANewHold` (revisión liberada y la siguiente ya en la ventana en la misma pasada ⇒ `HeldSince == today`, lo liberado con `max(p, today + 5)`).
  - `AManualReviewGeneratesUnreviewedAndAdvances` (`PriceUnreviewed`, `NextReviewDate` +12 meses, sin retención).
  - `AScheduleBeforeTheReviewAppliesWithoutResolvingIt` (aplica en su período, la revisión no se mueve, y al llegar a ella retiene).
  - `CreationNeverHoldsNorMarksUnreviewed` (alta con revisión dentro de lo que genera).
  - `PausingAHeldContractClearsTheHold` (`Pause` ⇒ `HeldSince == null`, `Generate` no genera).
  - `AfterAPauseOfSeveralYearsThereIsOneReview` (`Ipc`, revisión 2027-01-10, pausado 2026-12 → reanudado 2030-05-03: la revisión aplica al primer período generado después, se resuelve con `AdvanceReview` a 2031-01-10 tras confirmar un solo programado; `IpcYear` del snapshot = 2029).
  - `ANotHeldPassDueOnTheAnchor` (sin retención previa, `DueDate == PeriodDate`).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests`. Esperado: no compila (`GenerationMode`, `GenerationResult`).
- [ ] **Step 3: Implementar** `Generate` nuevo y la sobrecarga.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests --filter "FullyQualifiedName~ChargeGenerator"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(service-contracts): hold, release and mark unreviewed charges at generation"`.

### Task 3: Persistencia, migración y repositorios

**Files:**
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure/Persistence/ServiceContractsDbContext.cs`, `ServiceContractsUnitOfWork.cs`, `ServicePriceChangeRepository.cs`, `ChargeRepository.cs` (sólo el comentario del índice); `ServiceContractsInfrastructureExtensions.cs` (registro)
- Create (Application): `IServicePriceScheduleRepository.cs`, `IIpcRateRepository.cs`
- Create (Infrastructure/Persistence): `ServicePriceScheduleRepository.cs`, `IpcRateRepository.cs`; migración `<timestamp>_AddPriceAdjustment.cs` (+ `.Designer.cs` y snapshot generados)
- Test: `tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests/ServiceContractsPersistenceTests.cs`; create `ServiceContractsPriceAdjustmentMigrationTests.cs`

**Interfaces:**
- Consumes: entidades de la Tarea 1.
- Produces (esquema, nombres exactos): columnas de spec §7. `services.adjustment_mode varchar(16)` (`CK_services_adjustment_mode`), `ipc_extra_points numeric(5,2) default 0` (`CK_services_ipc_extra_points`, `>= 0 AND <= 50`), `next_review_date date`, `held_since date null`; `LastGeneratedPeriodDate` sigue en `last_generated_due_date`. `charges.period_date date`, `price_unreviewed boolean default false`; único `IX_charges_service_period (service_id, period_date)`; se borra `IX_charges_service_due`. Tabla `service_price_schedules` con `PK_service_price_schedules`, único parcial `IX_service_price_schedules_pending (service_id) WHERE status = 'Pending'`, índice `IX_service_price_schedules_tenant_source_year (tenant_id, source, ipc_year)`, `CK_service_price_schedules_source`, `CK_service_price_schedules_status`, `CK_service_price_schedules_new_price` (`> 0`), `ipc_year smallint`, `ipc_rate numeric(6,2)`, `extra_points numeric(5,2)`. Tabla `ipc_rates` con `PK_ipc_rates (tenant_id, year)`, `year smallint`, `rate numeric(6,2)` (`CK_ipc_rates_rate`), `version bigint` token de concurrencia. `service_price_changes.origin varchar(16)` (`CK_service_price_changes_origin`), `schedule_id uuid null`.
- Produces (migración, en `Up`, en este orden): columnas nuevas con `period_date` y `next_review_date` **nullable**; `internal const string BackfillSql` (idempotente): `UPDATE billing.charges SET period_date = due_date WHERE period_date IS NULL`, `adjustment_mode = 'Manual'`, `origin = 'Edit'`, y `next_review_date` = el menor `first_due_date + make_interval(years => k)` (`k` en `generate_series(1, 200)`) que sea `>= (now() AT TIME ZONE 'utc')::date + 15`, sólo donde es nulo; luego `AlterColumn` a `NOT NULL`; luego el índice nuevo y el `DropIndex` del viejo; luego tablas nuevas. `Down` generado.
- Produces (`IServicePriceScheduleRepository`): `void Add(ServicePriceSchedule schedule)`; `Task<ServicePriceSchedule?> FindPendingAsync(Guid tenantId, ServiceContractId serviceId, CancellationToken)` (con tracking); `Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByServicesAsync(IReadOnlyCollection<ServiceContractId> serviceIds, CancellationToken)` y `ListPendingByTenantAsync(Guid tenantId, CancellationToken)` (sin tracking); `Task<IReadOnlyList<int>> ListPendingIpcYearsAsync(Guid tenantId, CancellationToken)`; `Task RemoveByServiceAsync(ServiceContractId serviceId, CancellationToken)`.
- Produces (`IIpcRateRepository`): `void Add(IpcRate rate)`; `Task<IpcRate?> FindAsync(Guid tenantId, int year, CancellationToken)` (con tracking); `Task<IReadOnlyList<IpcRate>> ListAsync(Guid tenantId, CancellationToken)` (sin tracking, `year` desc).
- Produces (`ServiceContractsUnitOfWork.SaveChangesAsync`): `IX_charges_service_period` e `IX_service_price_schedules_pending` ⇒ `RequestConcurrencyException("concurrency.conflict", …)` (R-5); `PK_ipc_rates` se suma a `ProfilePrimaryKey, SettingsPrimaryKey` ⇒ `billing.concurrent_first_write`. Actualiza el comentario que dice que los índices del worker no se traducen.

- [ ] **Step 1: Pruebas que fallan.**
  - `ServiceContractsPersistenceTests`: `ASecondChargeWithTheSameServiceAndDueDateIsRejected` pasa a `ASecondChargeOfTheSamePeriodIsRejected` (`ConstraintName == "IX_charges_service_period"`); `TwoChargesOfAServiceMayShareTheDueDate` (períodos distintos, mismo vencimiento, se guardan); `OnlyOnePendingScheduleByService` (`IX_service_price_schedules_pending`; un `Cancelled` y un `Pending` conviven); `TheIpcRateRoundTripsAndRejectsOutOfRange` (`CK_ipc_rates_rate` con 100.01); `AServiceRoundTripsItsPriceReview` (modo, puntos, revisión, `held_since`).
  - `ServiceContractsPriceAdjustmentMigrationTests` (base migrada del harness; siembra por SQL y corre `BackfillSql` dos veces): `ExistingContractsGetManualAndAnAnniversaryFifteenDaysAhead` (con `first_due_date` hace 400 días y otra futura: `adjustment_mode = 'Manual'`, puntos 0, `next_review_date` = aniversario `>= hoy UTC + 15` y el anterior a él `<`); `AFebruaryTwentyNinthAnchorReviewsOnTheTwentyEighth` (`first_due_date` 2024-02-29 ⇒ 28 feb del año que toque); `ChargesGetTheirDueDateAsPeriod` (siembra con `period_date` nulo; nota: la prueba hace `ALTER COLUMN period_date DROP NOT NULL` antes de sembrar y lo restaura al final).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests`. Esperado: no compila (`ServicePriceSchedule` sin `DbSet`, `BackfillSql` no existe).
- [ ] **Step 3: Implementar** mapeo, repositorios, traducción y registro DI (scoped, como los demás repositorios).
- [ ] **Step 4: Generar la migración** — `dotnet ef migrations add AddPriceAdjustment --project src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure --context ServiceContractsDbContext -o Persistence/Migrations`. Reordena a mano según «Interfaces» (nullable → `BackfillSql` → `NOT NULL` → índices → tablas). Revisa que el snapshot no traiga cambios ajenos.
- [ ] **Step 5: GREEN** — `$env:Seed__Enabled = "false"` y, en primer plano, `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --filter "FullyQualifiedName~ServiceContractsPriceAdjustmentMigrationTests|FullyQualifiedName~ServiceContractsPersistenceTests" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 6: Commit** — `git commit -m "feat(service-contracts): persist price reviews, schedules and IPC rates"`.

### Task 4: Generación en la corrida y en el ciclo de vida del contrato

**Files:**
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure/Reminders/ServiceContractsReminderProcessor.cs` (sólo el paso de generación)
- Modify (Application): `CreateService.cs`, `ImportServiceContracts.cs`, `PreviewServiceCharges.cs`, `UpdateService.cs`, `ChangeServiceStatus.cs`, `DeleteService.cs`, `ServiceContractsDtos.cs` (sólo `ChargesPreviewDto`)
- Test: `tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests/ServiceContractsReminderProcessorTests.cs`, `ServiceContractApiTests.cs`, `ServiceContractsImportApiTests.cs`; `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/CreateServiceValidatorTests.cs`

**Interfaces:**
- Consumes: Tareas 1 a 3.
- Produces:
  - `GenerateServiceAsync`: carga el programado pendiente (`FindPendingAsync`), corre `Generate(service, pending, Run, today, now)`; si `!ServiceChanged` vuelve; si no, `BumpVersionAfterGeneration()`, agrega cobros e historial y guarda. El reintento único atrapa `DbUpdateConcurrencyException` o `UniqueViolation.Is(exception, "IX_charges_service_period")`. Corrige el XML doc (nombra el índice nuevo y la retención).
  - `CreateServiceRequest` gana `PriceReviewInput? PriceReview` (último parámetro) con `public sealed record PriceReviewInput(string? AdjustmentMode, decimal? IpcExtraPoints, DateOnly? NextReviewDate)`; `CreateServiceCommand` lo lleva. Validador: si viene, `AdjustmentMode` en `Manual|Ipc`, `IpcExtraPoints` requerido, `0…50`, ≤ 2 decimales; errores con prefijo `PriceReview.` (mismo patrón que `Profile.`). El handler genera en `Creation` y luego `InitializePriceReview` (ausente ⇒ `Manual`, 0, `null`). Auditoría de creación suma `"priceReview"` a `CreatedFields` sólo si vino.
  - Importar: tras generar, `InitializePriceReview(Manual, 0, null)`.
  - `ChargesPreviewDto(int ChargesToGenerate, int OverdueCharges, DateOnly DefaultNextReviewDate)`: el transitorio, tras generar, da `DefaultNextReviewDate()`.
  - `UpdateService`: si cambia la moneda y hay pendiente ⇒ `PriceScheduleCurrencyLocked` (antes de `service.Update`). Inyecta `IServicePriceScheduleRepository`.
  - `ChangeServiceStatus`: cancelar también hace `pending?.Cancel(service, now)` (el `Touch` extra no importa: la respuesta lleva la versión final). Pausar ya borra la retención en el dominio.
  - `DeleteService`: `schedules.RemoveByServiceAsync(service.Id)`.

- [ ] **Step 1: Pruebas que fallan.**
  - `ServiceContractsReminderProcessorTests` (clock `MutableTenantClock` de la clase): `AnIpcContractWhoseReviewEntersTheWindowIsHeld` (sin cobro del período, `held_since` = hoy, versión +1, ningún mensaje al cliente); `AManualContractGeneratesUnreviewedAndMovesItsReview`; `AScheduledChangeAppliesInTheRunAndLeavesItsHistory` (cobro con el precio nuevo, programado `Applied`, fila `service_price_changes` con `origin = 'Ipc'` o `'Scheduled'` y su `schedule_id`); `HeldPeriodsReleasedLateShareTheirDueDate` (dos períodos retenidos, liberados por un programado sembrado: dos cobros con el mismo `due_date` y `period_date` distintos); la existente del choque concurrente renombra su comentario y siembra el cobro por `period_date`.
  - `ServiceContractApiTests`: `CreatingWithoutPriceReviewIsManualWithTheDefaultReview`; `CreatingWithAnIpcPriceReviewStoresIt`; `AReviewDateNotAfterTheLastGeneratedIsInvalid` (422 `billing.service.review_date_invalid`); `PriceReviewFieldsAreValidatedWithTheirPrefix` (`errors` con `PriceReview.IpcExtraPoints`); `ChangingTheCurrencyWithAPendingScheduleIsLocked`; `CancellingAServiceCancelsItsPendingSchedule`; `DeletingAServiceDeletesItsSchedules`; la del preview (`new ChargesPreviewDto(4, OverdueOf(3))`) pasa a comparar campos y suma `DefaultNextReviewDate`.
  - `ServiceContractsImportApiTests`: `ImportedContractsAreManualWithTheDefaultReview`.
  - `CreateServiceValidatorTests`: modo desconocido y puntos 50.01 fallan; ausente pasa.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests` y `…UnitTests`. Esperado: no compilan (`PriceReviewInput`, `ChargesPreviewDto` de 3 campos).
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN filtrado** — `$env:Seed__Enabled = "false"`; `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --filter "FullyQualifiedName~Held|FullyQualifiedName~Unreviewed|FullyQualifiedName~ScheduledChange|FullyQualifiedName~PriceReview|FullyQualifiedName~PendingSchedule|FullyQualifiedName~ImportedContractsAreManual|FullyQualifiedName~Preview" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Cierre del bloque (una vez)** — unitarias completas del módulo; `dotnet test tests/ArchitectureTests/ArchitectureTests`; integración, en primer plano y una por una, las clases `ServiceContractsPersistenceTests`, `ServiceContractsPriceAdjustmentMigrationTests`, `ServiceContractsReminderProcessorTests`, `ServiceContractApiTests`, `ServiceContractsImportApiTests`, `OverdueDigestEventTests`.
- [ ] **Step 6: Commit** — `git commit -m "feat(service-contracts): generate with reviews in the daily run and the contract lifecycle"`.

**Revisión del Bloque 1** (una): un revisor sobre las Tareas 1 a 4, con foco en §6.3 y la migración.

---

## Bloque 2 — Contrato HTTP (Tareas 5 a 7, un implementador)

### Task 5: DTOs, lector de revisiones y composición

**Files:**
- Modify (Application): `ServiceContractsDtos.cs`, `ServiceContractsMapping.cs`, `ServiceDetailComposer.cs`, `ListCharges.cs`, `ChargeDtoComposer.cs` si arma el cobro
- Modify (Infrastructure): `Persistence/ChargeRepository.cs` (`SummaryAsync` devuelve `PriceReviews = PriceReviewCountsDto.Empty`)
- Create (Application): `PriceReviewReader.cs`
- Modify (pruebas que construyen DTOs): `tests/Bootstrapper/Bootstrapper.UnitTests/CopilotFinanceToolsTests.cs`, `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/ServiceContractDescriptionTests.cs`
- Test: create `tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests/ServiceContractsPriceReviewApiTests.cs` (la clase de todo el Bloque 2)

**Interfaces:**
- Consumes: `PriceReview.Of`, repositorios de la Tarea 3, `IServiceContractsMemberDirectory`.
- Produces (DTOs, nombres y orden de spec §8; los nuevos campos van **al final** del record existente):
  - `PriceScheduleDto(Guid Id, DateOnly EffectiveFrom, DateOnly AppliesToPeriodDate, decimal NewPrice, string Currency, string Source, int? IpcYear, decimal? IpcRate, decimal? ExtraPoints, string Reason, MemberRefDto? CreatedBy, DateTimeOffset CreatedAt)`.
  - `PriceReviewInfoDto(string AdjustmentMode, decimal IpcExtraPoints, DateOnly NextReviewDate, DateOnly? ReviewPeriodDate, DateOnly? GenerationDate, DateOnly? NoticeFrom, string State, DateOnly? HeldSince, int? IpcYear, PriceScheduleDto? Scheduled)`. Para un pausado con pendiente, `AppliesToPeriodDate` sale de `NextUngeneratedPeriod()`.
  - `PriceReviewCountsDto(int DueCount, int HeldCount)` con `static Empty`.
  - `ChargeDto` + `DateOnly PeriodDate, bool PriceUnreviewed`; `ServiceDto` + `PriceReviewInfoDto PriceReview`; `ServiceDetailDto` + `IReadOnlyList<DateOnly> UpcomingPeriodDates`; `ServicePriceChangeDto` + `string Origin`; `ChargeSummaryDto` + `PriceReviewCountsDto PriceReviews`.
- Produces (`PriceReviewReader`, sealed, registrado scoped): `Task<IReadOnlyList<PriceReviewEntry>> ListOpenAsync(Guid tenantId, DateOnly today, CancellationToken)` con `public sealed record PriceReviewEntry(ServiceContract Service, ServicePriceSchedule? Pending, PriceReviewSnapshot Review)`: los `Active` del tenant (`ListActiveAsync`) con su pendiente (`ListPendingByTenantAsync`) en estado `Due` o `Held`; `static PriceReviewCountsDto Count(IEnumerable<PriceReviewEntry>)`.
- Produces (`ServiceDetailComposer`): `ComposeAsync` y `ComposeSummariesAsync` arman `PriceReview` (pendientes en una consulta, autores de los programados en la misma consulta al directorio que ya existe) y `UpcomingPeriodDates`. `ListChargesHandler` reemplaza `summary.PriceReviews` con `PriceReviewReader.Count(...)`.

- [ ] **Step 1: Pruebas que fallan** en `ServiceContractsPriceReviewApiTests` (siembra con fechas relativas a `TenantToday` del harness): `TheDetailCarriesThePriceReviewAndTheUpcomingPeriods` (`Upcoming` con fechas, `upcomingPeriodDates` de 12; `Ipc` sólo hasta la revisión); `APausedContractHasStateNoneAndNoUpcomingPeriods`; `ChargesCarryPeriodAndUnreviewed`; `PriceChangesCarryTheirOrigin` (un `PUT` de hoy ⇒ `Edit`); `TheChargesSummaryCountsTheTenantPriceReviews` (un `Due` y un `Held` sembrados ⇒ `{ dueCount: 1, heldCount: 1 }` aunque el filtro de la lista no los toque); `TheServiceListCarriesThePriceReview`. Ajusta los constructores posicionales de `CopilotFinanceToolsTests` y `ServiceContractDescriptionTests`.
- [ ] **Step 2: RED** — `dotnet build` de `Modules.ServiceContracts.IntegrationTests` y de `Bootstrapper.UnitTests`. Esperado: no compilan (`PriceReviewInfoDto`, `PriceReviewCountsDto`).
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CopilotFinanceToolsTests"`; `$env:Seed__Enabled = "false"`; `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --filter "FullyQualifiedName~ServiceContractsPriceReviewApiTests" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Commit** — `git commit -m "feat(service-contracts): expose price reviews in services, charges and the summary"`.

### Task 6: Acciones por contrato

**Files:**
- Create (Application): `UpdatePriceReview.cs`, `SchedulePriceChange.cs`, `CancelPriceSchedule.cs`, `KeepPrice.cs`, `PriceReviewGeneration.cs`
- Modify: `ServiceContractsAuditActions.cs`, `ServiceContractsNotFound.cs`, `src/Modules/ServiceContracts/Modules.ServiceContracts.Api/ServiceContractsEndpoints.cs`
- Test: `ServiceContractsPriceReviewApiTests.cs`

**Interfaces:**
- Consumes: Tareas 1 a 5.
- Produces:
  - Auditoría: `ServicePriceReviewUpdated`, `ServicePriceScheduled`, `ServicePriceScheduleCancelled`, `ServicePriceKept`, `IpcSaved` (valores de «Global Constraints»), `Resources.IpcRate = "ipc_rate"`. `ServiceContractsNotFound.PriceSchedule(Guid serviceId)` con `PriceScheduleNotFound`.
  - `static class PriceReviewGeneration { static void Run(ServiceContract service, ServicePriceSchedule? pending, DateOnly today, DateTimeOffset now, IChargeRepository charges, IServicePriceChangeRepository priceChanges) }`: corre `Generate(…, Run, …)` y agrega lo generado. Lo llaman `UpdatePriceReview`, `SchedulePriceChange`, `KeepPrice` y la confirmación de IPC, **siempre**, después de la mutación (no genera nada si no hay qué; el `Touch` de la acción ya subió la versión).
  - `UpdatePriceReviewCommand(Guid TenantId, Guid ServiceId, string AdjustmentMode, decimal IpcExtraPoints, DateOnly NextReviewDate, long ExpectedVersion)` desde `PriceReviewInput` (ausentes ⇒ `validation.failed` del campo); tras `UpdatePriceReview`, `PriceReview.EnsureNotAfterReview(service, pending)`. `changedFields` sólo con lo que cambió (`adjustmentMode`, `ipcExtraPoints`, `nextReviewDate`).
  - `SchedulePriceChangeCommand(Guid TenantId, Guid ServiceId, decimal NewPrice, DateOnly? EffectiveFrom, string? Reason, long ExpectedVersion)`; validador: `NewPrice` con `MustBeAServicePrice`, `Reason` ≤ 500 (vacío lo da el dominio). Firma con `FindActiveMembershipIdAsync`. Auditoría `["newPrice","effectiveFrom","reason"]`.
  - `CancelPriceScheduleCommand(Guid TenantId, Guid ServiceId, long ExpectedVersion)`: sin pendiente ⇒ 404 `billing.price_schedule.not_found`. Auditoría `["scheduleId"]`.
  - `KeepPriceCommand(Guid TenantId, Guid ServiceId, long ExpectedVersion)`: con pendiente ⇒ `PriceScheduleAlreadyPending`. Auditoría `["nextReviewDate"]`.
  - Las cuatro: `ServiceContractsAuthorization.EnsureAsync(… Manage …)`, servicio acotado al tenant (404 `billing.service.not_found`), `ServiceVersion.EnsureCurrent`, respuesta `ServiceDetailDto` por `ServiceDetailComposer`.
  - Endpoints (`RequireVersion` existente, 428 sin `If-Match`, permiso manage): `PUT /services/{serviceId:guid}/price-review`, `POST /services/{serviceId:guid}/price-schedule`, `DELETE /services/{serviceId:guid}/price-schedule`, `POST /services/{serviceId:guid}/keep-price` (sin cuerpo). El `ETag` de la respuesta como en el `PUT /services/{id}`.

- [ ] **Step 1: Pruebas que fallan** (en `ServiceContractsPriceReviewApiTests`):
  - Programar: `SchedulingFromTheReviewMakesItScheduled` (`effectiveFrom: null` ⇒ `state: Scheduled`, `scheduled.appliesToPeriodDate` = revisión, auditoría `billing.service.price_scheduled`); `SchedulingAHeldContractReleasesAndGeneratesNow` (retenido sembrado ⇒ cobro con el precio nuevo y vencimiento `max(period, hoy + 5)`, `heldSince: null`); `SchedulingErrorsFollowTheContract` (pausado ⇒ `not_active`, segundo ⇒ `already_pending`, período generado ⇒ `period_generated`, `Ipc` después de la revisión ⇒ `after_review`, mismo precio ⇒ `same_price`, sin motivo ⇒ `price_reason_required`); `SchedulingWithAStaleVersionIs412` y `WithoutIfMatchIs428`; `AReaderCannotSchedule` (403).
  - Cancelar: `CancellingThePendingScheduleBringsBackTheReview`; `CancellingWithoutPendingIs404`.
  - Mantener: `KeepingThePriceMovesTheReviewAYearAhead`; `KeepingAHeldContractGeneratesAtTheCurrentPrice` (sin `priceUnreviewed`); `KeepingWithAPendingScheduleIsAlreadyPending`.
  - Reajuste: `SwitchingAHeldContractToManualGeneratesUnreviewed`; `MovingTheReviewPastTheHeldPeriodGeneratesWithoutTheUnreviewedMark`; `MovingTheReviewBeforeAPendingChangeIsAfterReview`; `APausedContractCanEditItsPriceReview`; `ACancelledContractCannot`; `PointsAboveFiftyAreValidationFailed` (`errors.IpcExtraPoints`); auditoría `billing.service.price_review_updated` con los campos que cambiaron.
  - Unitarias, create `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/PriceReviewActionValidatorTests.cs`: `SchedulePriceChangeValidator` rechaza `NewPrice` 0 y 10.123 y `Reason` de 501; `UpdatePriceReviewValidator` rechaza modo `Annual`, puntos −0.01 y 50.01, y acepta 50.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests` y `…IntegrationTests`. Esperado: no compilan (`SchedulePriceChangeValidator`, `UpdatePriceReviewValidator` y los comandos no existen); sin contenedores. Pega la salida.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — unitarias filtradas por `PriceReviewActionValidatorTests`; `$env:Seed__Enabled = "false"`; integración `--filter "FullyQualifiedName~Scheduling|FullyQualifiedName~Cancelling|FullyQualifiedName~Keeping|FullyQualifiedName~Switching|FullyQualifiedName~MovingTheReview|FullyQualifiedName~PriceReview" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Commit** — `git commit -m "feat(service-contracts): schedule, cancel and keep prices and edit the price review"`.

### Task 7: IPC del tenant, lista de revisiones y confirmación en lote

**Files:**
- Create (Application): `ListPriceReviews.cs`, `ListIpcRates.cs`, `SaveIpcRate.cs`, `ConfirmIpcReviews.cs`
- Modify: `ServiceContractsDtos.cs`, `ServiceContractsEndpoints.cs`, `IServiceContractRepository.cs` + `ServiceContractRepository.cs` (filtro de la lista)
- Test: `ServiceContractsPriceReviewApiTests.cs`; create `tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests/PriceReviewValidatorTests.cs`

**Interfaces:**
- Consumes: `PriceReviewReader`, `PriceReview.IpcPrice|IpcReason`, `ServicePriceSchedule.ConfirmIpc`, `PriceReviewGeneration.Run`, `ITenantPriceRounding.GetAsync(tenantId, currency)`, `IServiceContractsCustomerLookup.SearchIdsAsync`.
- Produces (DTOs): `PriceReviewServiceRefDto(Guid Id, string Name, CustomerRefDto Customer, decimal Price, string Currency, string Cycle, long Version)`; `PriceReviewRowDto(PriceReviewServiceRefDto Service, MemberRefDto? Assignee, PriceReviewInfoDto Review, decimal? IpcRate, decimal? ComputedPrice)`; `IpcYearNeedDto(int Year, decimal? Rate, int ContractCount)`; `PriceReviewPageDto(IReadOnlyList<PriceReviewRowDto> Items, int Total, int Page, int PageSize, PriceReviewCountsDto Summary, IReadOnlyList<IpcYearNeedDto> IpcYears)`; `IpcRateDto(int Year, decimal Rate, bool InUse, MemberRefDto? UpdatedBy, DateTimeOffset UpdatedAt, long Version)`; `IpcRateListDto(IReadOnlyList<IpcRateDto> Items)`; `ConfirmIpcResultDto(Guid ServiceId, string Outcome, string? Code, ServiceDto? Service)`; `ConfirmIpcResponseDto(IReadOnlyList<ConfirmIpcResultDto> Results)`.
- Produces (`IServiceContractRepository`): `Task<IReadOnlySet<ServiceContractId>> FilterIdsAsync(Guid tenantId, IReadOnlyCollection<ServiceContractId> ids, Guid? assigneeMemberId, string? searchTerm, IReadOnlyCollection<Guid>? searchCustomerIds, CancellationToken)` con la misma regla de encargado (perfil) y de búsqueda (`LikeSearch` sobre el nombre, o cliente) que `ChargeRepository.ListAsync`.
- `GET /price-reviews` (`ListPriceReviewsQuery(Guid TenantId, string State, bool AssigneeMe, string? Search, int Page, int PageSize)`; `State` `all|due|held` sin distinguir mayúsculas, default `all`): `ListOpenAsync` ⇒ `summary` e `ipcYears` (por año de los `Ipc`, con la tasa si está cargada) del tenant entero; luego filtro por estado y `FilterIdsAsync`; orden `GenerationDate` asc y luego `Service.Id`; página en memoria; `assignee=me` sin membresía activa ⇒ página vacía con el `summary` del tenant (como `ListCharges`). `ComputedPrice` sólo `Ipc` con tasa del año, por `PriceReview.IpcPrice` con la regla de redondeo de la moneda.
- `GET /ipc-rates`: `inUse` = año en `ListPendingIpcYearsAsync`.
- `PUT /ipc-rates/{year:int}` (`SaveIpcRateRequest(decimal? Rate, JsonElement ExpectedRate = default)`; `Undefined` ⇒ `validation.failed` en `ExpectedRate`, precedente `PhoneInput`): validador `Year` en `2000…today.Year + 1`, `Rate` requerido en `−10…100` con ≤ 2 decimales, `ExpectedRate` presente y, si es número, ≤ 2 decimales. Orden: misma tasa guardada ⇒ 200 sin escribir ni auditar; lo guardado (o su ausencia) distinto de `expectedRate` ⇒ `ResourceConflictException(IpcRateChanged)`; año en uso ⇒ `IpcInUse`; upsert, auditoría `["year","rate"]`; un `RequestConcurrencyException` de la unidad de trabajo ⇒ `IpcRateChanged` (R-4).
- `POST /price-reviews/confirm-ipc` (`ConfirmIpcRequest(int? Year, IReadOnlyList<ConfirmIpcItemInput>? Items)`, `ConfirmIpcItemInput(Guid? ServiceId, long? Version, decimal? ExpectedPrice, decimal? NewPrice, string? Reason)`): validador (`Items` 1…200 sin repetir `ServiceId`, `Items[i].ExpectedPrice` requerido, `Items[i].NewPrice` > 0 con ≤ 2 decimales, `Items[i].Reason` ≤ 500); sin tasa del año ⇒ 422 `billing.ipc.not_set`. Por ítem, en su transacción (`unitOfWork.Clear()` antes de cada uno), chequeos en este orden: no existe en el tenant ⇒ `billing.service.not_found`; versión ⇒ `concurrency.conflict`; no `Ipc` ⇒ `not_ipc`; estado no `Due` ni `Held` ⇒ `not_pending`; `IpcYear != year` ⇒ `year_mismatch`; calculado nulo o distinto de `expectedPrice` ⇒ `price_changed`; `newPrice` distinto del calculado sin motivo ⇒ `billing.service.price_reason_required`. Motivo: el de la persona si cambió el precio, si no `IpcReason`. Crea el programado, `PriceReviewGeneration.Run`, auditoría `billing.service.price_scheduled` con `["newPrice","effectiveFrom","reason","ipcYear","ipcRate","extraPoints"]`, guarda. Un 412 al guardar es `concurrency.conflict` del ítem; cualquier otra excepción de dominio, su código. `Service` = `ComposeSummariesAsync` del contrato confirmado; `null` si falló.
- Endpoints: `GET /price-reviews` y `GET /ipc-rates` (read), `PUT /ipc-rates/{year:int}` y `POST /price-reviews/confirm-ipc` (manage), sin `If-Match`.

- [ ] **Step 1: Pruebas que fallan.**
  - `PriceReviewValidatorTests`: estado desconocido, `PageSize` 101; año 1999 y `today.Year + 2`; tasa 100.01 y 5.123; ítems vacíos, 201 y repetidos.
  - `ServiceContractsPriceReviewApiTests`: `TheListShowsDueAndHeldByGenerationDate` (summary e `ipcYears` del tenant aunque el filtro `held` deje uno; `computedPrice` con la tasa cargada y `null` sin ella); `AssigneeMeAndSearchFilterLikeTheCharges`; `SavingTheFirstRateNeedsANullExpectedRate`; `AnExpectedRateThatIsNotStoredIs409` (incluido `null` con una ya guardada); `TheSameStoredRateIs200EvenWithAStaleExpectedRate` (sin auditoría nueva); `ARateInUseCannotChange` (`rate_changed` antes que `in_use`); `TwoUpdatesOfTheSameRateNeverOverwriteSilently` (dos `PUT` concurrentes con el mismo `expectedRate`: uno 200 y el otro 409 `billing.ipc.rate_changed`); `AnAbsentExpectedRateIsValidationFailed`; `ConfirmingWithoutTheRateIsNotSet`; `ConfirmingReleasesHeldAndSchedulesDue` (un retenido genera con el precio calculado y el motivo automático; un `Due` queda `Scheduled`); `EachItemFailsOnItsOwn` (versión vieja, `Manual`, `Upcoming`, otro año, `expectedPrice` distinto, precio cambiado sin motivo: cada uno con su `code`, los demás confirmados, resultados en el orden pedido).
- [ ] **Step 2: RED** — `dotnet build` de `Modules.ServiceContracts.UnitTests` y `…IntegrationTests`. Esperado: no compilan (validadores, `PriceReviewPageDto`, `IpcRateDto`, `ConfirmIpcResponseDto`); sin contenedores.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — unitarias filtradas por `PriceReviewValidatorTests`; `$env:Seed__Enabled = "false"`; integración `--filter "FullyQualifiedName~TheListShows|FullyQualifiedName~Rate|FullyQualifiedName~Confirming|FullyQualifiedName~EachItemFails|FullyQualifiedName~AssigneeMeAndSearch" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Cierre del bloque (una vez)** — unitarias del módulo y de `Bootstrapper.UnitTests`; `ArchitectureTests`; integración, una por una, `ServiceContractsPriceReviewApiTests`, `ServiceContractsChargeApiTests`, `ServiceContractApiTests`.
- [ ] **Step 6: Commit** — `git commit -m "feat(service-contracts): load the tenant IPC and confirm price reviews in bulk"`.

**Revisión del Bloque 2** (una): un revisor sobre las Tareas 5 a 7, con el contrato de spec §8 y `service-contracts.api.ts` del frontend a la vista.

---

## Bloque 3 — Avisos al encargado (Tareas 8 y 9, un implementador)

### Task 8: Digest y evento de campana desde la corrida

**Files:**
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure/Reminders/ServiceContractsReminderProcessor.cs` (paso 5), `src/Modules/ServiceContracts/Modules.ServiceContracts.Application/ServiceContractsMessaging.cs`, `ServiceContractsInfrastructureExtensions.cs`
- Create: `src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure/Reminders/PriceReviewDigestEventPublisher.cs`
- Modify (constructores posicionales): `tests/Bootstrapper/Bootstrapper.UnitTests/ServiceContractsEmailSenderTests.cs`
- Test: `tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests/ServiceContractsReminderProcessorTests.cs`, `OverdueDigestEventTests.cs`

**Interfaces:**
- Consumes: `PriceReviewReader.ListOpenAsync`, `IIpcRateRepository.ListAsync`.
- Produces:
  - `public sealed record ServiceContractsPriceReviewRow(string CustomerName, string ServiceName, decimal CurrentPrice, string Currency, DateOnly ReviewPeriodDate, DateOnly GenerationDate, string State, int? IpcYear, bool IpcMissing, DateOnly? HeldSince)`.
  - `ServiceContractsDigestEmail` gana, **al final**, `IReadOnlyList<ServiceContractsPriceReviewRow> PriceReviews` (`Due`) y `IReadOnlyList<ServiceContractsPriceReviewRow> HeldCharges` (`Held`), ordenadas por `GenerationDate`.
  - `PriceReviewDigestEventPublisher` (scoped, copia de `OverdueDigestEventPublisher`): `EventName = "billing.price-reviews.digest.v1"`, payload camelCase `{ tenantId, memberId, runDate (yyyy-MM-dd), dueCount, heldCount, occurredAt }`, `CorrelationId` `"{tenant}:{member}:{date}"`.
  - `SendDigestsAsync`: ya no vuelve temprano sin cobros si hay revisiones; las revisiones se reparten como los cobros (encargado del perfil del cliente activo y que puede gestionar; si no, o sin perfil, a cada gestor de `ListManagersAsync`). Un miembro entra si tiene cobros de interés **o** revisiones. En `SendDigestAsync`, tras ganar el claim: el evento de vencidos como hoy y, si tiene revisiones, `PriceReviewDigestEventPublisher.PublishAsync(…)`, ambos antes de mirar el email.

- [ ] **Step 1: Pruebas que fallan.**
  - `ServiceContractsReminderProcessorTests`: `AMemberWithOnlyPriceReviewsGetsTheDigest` (`RecordingStaffEmail` recibe `PriceReviews` con `IpcYear` e `IpcMissing: true`, sin cobros); `HeldContractsGoToTheHeldSection` (con `HeldSince`); `AContractWithoutProfileGoesToEveryManager`; `ScheduledContractsAreNotInTheDigest`; `TheDigestIsSentOncePerDayWithReviews` (dos corridas el mismo día ⇒ un envío).
  - `OverdueDigestEventTests`: `AMemberWithReviewsGetsThePriceReviewEvent` (una fila de outbox `billing.price-reviews.digest.v1` con `dueCount`/`heldCount`); `NoReviewsNoPriceReviewEvent`.
  - `ServiceContractsEmailSenderTests`: los dos constructores pasan `[]`, `[]` al final.
- [ ] **Step 2: RED** — `dotnet build` de `Modules.ServiceContracts.IntegrationTests` y `Bootstrapper.UnitTests`. Esperado: no compilan (`ServiceContractsPriceReviewRow`, `PriceReviewDigestEventPublisher`).
- [ ] **Step 3: Implementar.** `ServiceContractsEmailSender` (Bootstrapper) todavía ignora las listas nuevas: las mapea la Tarea 9.
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"`; `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --filter "FullyQualifiedName~PriceReview|FullyQualifiedName~HeldContractsGoTo|FullyQualifiedName~WithoutProfileGoesToEveryManager|FullyQualifiedName~ScheduledContractsAreNot|FullyQualifiedName~OncePerDayWithReviews" -- xUnit.MaxParallelThreads=4`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ServiceContractsEmailSenderTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(service-contracts): send price reviews in the daily digest and the bell event"`.

### Task 9: Campana `ServicePriceReviewsDue` y secciones del email

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Domain/InAppNotificationKind.cs`, `src/Modules/Notifications/Modules.Notifications.Infrastructure/InApp/BillingInAppConsumer.cs`, `src/Modules/Notifications/Modules.Notifications.Application/BillingEmailParts.cs`, `BillingDigestEmailTemplate.cs`, `src/Bootstrapper/ServiceContractsEmailSender.cs`
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InAppBillingTests.cs`, `tests/Modules/Notifications/Modules.Notifications.UnitTests/BillingEmailTemplatesTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ServiceContractsEmailSenderTests.cs`

**Interfaces:**
- Consumes: el evento y las filas de la Tarea 8.
- Produces:
  - `InAppNotificationKind.ServicePriceReviewsDue` **al final** del enum (se guarda por nombre; `kind varchar(64)` sin CHECK, verificado en `NotificationsDbContext.cs:70`: no hay migración).
  - `BillingInAppConsumer.EventNames` = `["billing.charges.overdue-digest.v1", "billing.price-reviews.digest.v1"]`; por `record.EventName`, el nuevo arma `data = { dueCount, heldCount, runDate }` y `dedupe_key = "billing-price-review:" + runDate`, `resourceId: null`. Actualiza el XML doc de la clase.
  - `public sealed record BillingPriceReviewEmailRow(string CustomerName, string ServiceName, string CurrentPrice, DateOnly ReviewPeriodDate, DateOnly GenerationDate, int? IpcYear, bool IpcMissing, DateOnly? HeldSince)`; `BillingDigestEmailInput` gana, al final, `IReadOnlyList<BillingPriceReviewEmailRow> PriceReviews` y `IReadOnlyList<BillingPriceReviewEmailRow> HeldCharges`.
  - `BillingDigestEmailTemplate`: secciones «Precios por revisar» (columnas Cliente, Servicio, Precio actual, Revisión, Se genera, IPC) y «Cobros retenidos» (las mismas más «Retenido desde»), después de «Avisos fallidos»; vacía no se dibuja. IPC: `"{año}"`, o `"{año} (falta cargarlo)"` con `IpcMissing`, o `"—"` en `Manual`. Asunto: el de hoy, más `", {n} precios por revisar"` y `", {m} cobros retenidos"` sólo si son mayores que cero. Texto en español colombiano, tuteando.
  - `ServiceContractsEmailSender.SendDigestAsync` mapea las dos listas (precio con `Money`).

- [ ] **Step 1: Pruebas que fallan.**
  - `InAppBillingTests`: `APriceReviewEventCreatesAnEntryForTheMember` (`kind == "ServicePriceReviewsDue"`, `data` con los tres campos); `ARepeatedPriceReviewEventTheSameDayDoesNotDuplicate`; `OverdueAndPriceReviewEntriesOfTheSameDayCoexist`.
  - `BillingEmailTemplatesTests`: `TheDigestDrawsPriceReviewsAndHeldCharges` (títulos, «falta cargarlo», «Retenido desde», asunto con los dos conteos); `EmptyReviewSectionsAreNotDrawn` (asunto igual al de hoy).
  - `ServiceContractsEmailSenderTests`: `TheDigestCarriesThePriceReviewRows`.
- [ ] **Step 2: RED** — `dotnet build` de `Modules.Notifications.UnitTests`, `Modules.Notifications.IntegrationTests` y `Bootstrapper.UnitTests`. Esperado: no compilan (`ServicePriceReviewsDue`, `BillingPriceReviewEmailRow`).
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — unitarias de Notifications y `Bootstrapper.UnitTests --filter "FullyQualifiedName~ServiceContractsEmailSenderTests"`; `$env:Seed__Enabled = "false"`; `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InAppBillingTests" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Cierre del bloque (una vez)** — integración completa, una por una, `ServiceContractsReminderProcessorTests`, `OverdueDigestEventTests`, `InAppBillingTests`; `ArchitectureTests`; luego `.\scripts\test-feature.ps1` en verde, leyendo sus avisos.
- [ ] **Step 6: Commit** — `git commit -m "feat(notifications): add the price reviews bell and digest sections"`.

**Revisión del Bloque 3** (una), y luego la revisión fuerte de toda la rama.

---

### Task 10: Periods held before a pause survive the resume

Runs after Task 7 is committed (it touches `ServiceContractsPriceReviewApiTests.cs`). Independent of Tasks 8 and 9.
Owner decision (2026-11-25, binding): resuming keeps the periods already held before the pause; they stay held until a price is decided, then bill as released held charges. Periods that fell during the pause are never billed.

**Files:**
- Modify (Domain): `src/Modules/ServiceContracts/Modules.ServiceContracts.Domain/ServiceContract.cs`, `PriceReview.cs`
- Modify (Application): `.../Modules.ServiceContracts.Application/ChangeServiceStatus.cs`
- Modify (Infrastructure): `Persistence/ServiceContractsDbContext.cs`; **edit** (do not add) `Persistence/Migrations/20261125131151_AddPriceAdjustment.cs`, its `.Designer.cs` and `ServiceContractsDbContextModelSnapshot.cs`
- Modify (callers of `Pause`, mechanical): unit `ServiceContractTests`, `ChargeGeneratorTests`, `ChargeGeneratorReviewTests`, `PriceReviewTests`, `ServicePriceScheduleTests`; integration `ServiceContractsPersistenceTests`, `ServiceContractsRepositoryTests`, `ServiceContractsReminderProcessorTests`, `ServiceContractsPriceReviewApiTests`
- Test: `ChargeGeneratorReviewTests`, `ServiceContractTests`, `ServiceContractsPersistenceTests`, `ServiceContractsPriceAdjustmentMigrationTests`, `ServiceContractsPriceReviewApiTests`
- Docs: the spec (text below) and the plan (append this task; replace Review Focus 3)

**Interfaces** (consumes Tasks 1-6: `ChargeGenerator.Generate`, `PriceReviewGeneration.Run`, `IServicePriceScheduleRepository.FindPendingAsync`)

`ServiceContract`:
- `private readonly List<DateOnly> _carriedHeldPeriods = [];` and `public IReadOnlyList<DateOnly> CarriedHeldPeriods => _carriedHeldPeriods;` (ascending, every item `> LastGeneratedPeriodDate`).
- `public void Pause(DateOnly today, DateTimeOffset now)` (**signature change**). If `HeldSince is not null`: walk `p = NextUngeneratedPeriod()` while `p <= today.AddDays(ChargeGenerator.WindowDays)`, add `p` if missing, `p = PeriodAfter(p)`. Then `HeldSince = null`, as today.
- `Resume(DateOnly today, DateTimeOffset now)` (same signature): add `if (_carriedHeldPeriods.Count > 0) HeldSince = today;`.
- `Cancel`: also `_carriedHeldPeriods.Clear()`.
- `public DateOnly NextUngeneratedPeriod()`: `LastGeneratedPeriodDate is null ? FirstDueDate : PeriodAfter(last)`.
- `private DateOnly PeriodAfter(DateOnly period)`: the first carried period `> period` if any; otherwise `ReactivatedOn > period ? BillingCycles.FirstDueDateOnOrAfter(FirstDueDate, Cycle, ReactivatedOn) : BillingCycles.NextDueDate(FirstDueDate, Cycle, period)`. With an empty list this is the current arithmetic.
- `public DateOnly FirstUngeneratedPeriodOnOrAfter(DateOnly date)`: the first carried period `>= date`; otherwise `start = carried.Count > 0 ? PeriodAfter(carried[^1]) : NextUngeneratedPeriod()` and `start >= date ? start : BillingCycles.FirstDueDateOnOrAfter(FirstDueDate, Cycle, date)`.
- `MarkGenerated(DateOnly periodDate)`: `_carriedHeldPeriods.Remove(periodDate); LastGeneratedPeriodDate = periodDate;`.
- `UpcomingPeriods()`: steps with `PeriodAfter`, not `BillingCycles.NextDueDate`.

`PriceReview.AppliesTo` => `service.FirstUngeneratedPeriodOnOrAfter(effectiveFrom)`. `Covers`, `EnsureNotAfterReview` and `UpcomingPeriods` must walk the same cursor, never raw anchors.

`ChangeServiceStatusHandler`: inject `IServicePriceChangeRepository priceChanges`; read tenant `today` for Pause and Resume; on Resume call `service.Resume(today, now)` and, if `service.CarriedHeldPeriods.Count > 0`, `PriceReviewGeneration.Run(service, await schedules.FindPendingAsync(...), today, now, charges, priceChanges)` in the same transaction. Update the class XML doc.

EF: `service.PrimitiveCollection<List<DateOnly>>("_carriedHeldPeriods").HasColumnName("carried_held_periods").HasColumnType("date[]").HasDefaultValueSql("'{}'")` (precedent: `AuthorizationDbContext` `_permissions`).

Migration (edit `AddPriceAdjustment`; it is not in develop and never deployed): `Up` `AddColumn<List<DateOnly>>("carried_held_periods", schema "billing", table "services", type "date[]", nullable false, defaultValueSql "'{}'")` next to the other `services` columns, no backfill; `Down` `DropColumn`. Designer and snapshot: copy the property from a probe migration (`dotnet ef migrations add Probe ...`, copy, `dotnet ef migrations remove`).

`ChargeGenerator` and `ServiceContractsReminderProcessor` **do not change**.

- [ ] **Step 1: Failing tests.** Monthly contract anchored on the 10th, `Ipc`, review 2027-01-10, last generated 2026-12-10, price 100000, IPC price 107200. Use the existing `NewService`, `Run`, `ConfirmIpc` helpers of `ChargeGeneratorReviewTests`.
  - `PausingAHeldContractKeepsThePeriodsWhoseGenerationDayHadCome` (Theory): `Run("2027-01-05")` (held). `Pause(2027-02-05)` => carried `[2027-01-10, 2027-02-10]`, `HeldSince == null`. `Pause(2027-02-04)` => `[2027-01-10]`.
  - `PausingAContractThatWasNotHeldCarriesNothing`: `Run("2027-01-04")` (nothing, no hold), `Pause(2027-01-05)` => `[]`; `Resume(2027-03-01)`, `Run("2027-03-05")` => no charges, `HeldSince == 2027-03-05`, review period 2027-03-10; 01-10 and 02-10 never billed.
  - `ResumingKeepsTheCarriedPeriodsHeld`: held on 01-05, `Pause(2027-02-05)`, `Resume(2027-04-20)` => `HeldSince == 2027-04-20`; `PriceReview.Of(..., 2027-04-20)` => `(Held, ReviewPeriodDate 2027-01-10, IpcYear 2026)`; `UpcomingPeriods() == [2027-01-10]`; `Run("2027-04-21")` => no charges, `ServiceChanged == false`, `HeldSince` unchanged.
  - `ReleasingTheCarriedPeriodsBillsThemAndSkipsThePause`: previous case plus `Run("2027-04-21", ConfirmIpc(service))` => `[(2027-01-10, due 2027-04-26), (2027-02-10, due 2027-04-26)]`, both 107200; list empty, `HeldSince == null`, `NextReviewDate == 2028-01-10`, `NextPeriodToGenerate() == 2027-05-10` (03-10, 04-10 never billed). `Run("2027-05-05")` => `[(2027-05-10, 2027-05-10)]`.
  - `SeveralPausesKeepEveryHeldGap`: held on 01-05, `Pause(2027-01-20)` => `[01-10]`; `Resume(2027-03-01)`, `Run("2027-03-06")` (still held), `Pause(2027-03-07)` => `[01-10, 03-10]`; `Resume(2027-06-01)`, `Run("2027-06-02", ConfirmIpc)` => `[(01-10, 06-07), (03-10, 06-07)]`; `NextPeriodToGenerate() == 2027-06-10` (02-10, 04-10, 05-10 never billed).
  - `SwitchingToManualWhilePausedBillsTheCarriedPeriodsAtResume`: held, `Pause(2027-02-05)`, `UpdatePriceReview(Manual, 0, 2027-01-10)`, `Resume(2027-04-20)`, `Run("2027-04-20")` => `[(01-10, 04-25, unreviewed: true), (02-10, 04-25, unreviewed: false)]`, both 100000; `NextReviewDate == 2028-01-10`, `HeldSince == null`.
  - `AfterALongPauseTheCarriedReviewAndTheNewYearAreTwoReviews`: held, `Pause(2027-01-20)`, `Resume(2030-05-03)` => `IpcYear` 2026; `Run("2030-05-06", ConfirmIpc(2026))` => `[(2027-01-10, 2030-05-11)]`, `HeldSince == 2030-05-06` (new hold), review period 2030-05-10, `IpcYear` 2029; `ServicePriceSchedule.ConfirmIpc(service, 2029, ...)` and `Run("2030-05-06", it)` => `[(2030-05-10, 2030-05-11)]`, `NextReviewDate == 2031-01-10`.
  - `CancellingDropsTheCarriedPeriods`: held, `Pause(2027-02-05)`, `Cancel` => `[]`, `HeldSince == null`.
  - `ServiceContractTests`: `UpcomingPeriodsSkipThePauseButNotTheCarriedPeriods` (held, `Pause(2027-02-05)`, switch to `Manual`, `Resume(2027-04-20)` => `[01-10, 02-10, 05-10, 06-10, ...]`, 12 items); `AScheduleAppliesAlongTheCarriedPeriods` (`AppliesTo(2027-02-01) == 2027-02-10`; `AppliesTo(2027-03-01) == 2027-05-10`). Update every `Pause(now)` to `Pause(<date>, now)`. `PausingAHeldContractClearsTheHold` also asserts carried `[2027-01-10]` with `Pause(2027-01-08)`. `AfterAPauseOfSeveralYearsThereIsOneReview` unchanged (not held).
  - `ServiceContractsPersistenceTests.AServiceRoundTripsItsCarriedHeldPeriods`: save with two carried periods, reload => same list; `MarkGenerated(first)`, save, reload => one left.
  - `ServiceContractsPriceAdjustmentMigrationTests`: `ExistingContractsGetManual...` also asserts `carried_held_periods = '{}'`.
  - `ServiceContractsPriceReviewApiTests`: `ResumingAHeldContractKeepsItHeld` (`SeedHeldAsync` period P, `POST pause`, `POST resume` => `state: Held`, `heldSince: TenantToday`, `reviewPeriodDate: P`, no charge with `periodDate >= P`); `KeepingAResumedHeldContractBillsTheCarriedPeriods` (then `POST keep-price` => charge `(P, Later(P, TenantToday + 5), Price, priceUnreviewed: false)`, `heldSince: null`); `ResumingAPausedHeldContractSwitchedToManualBillsAtOnce` (pause, `PUT price-review` Manual, resume => charge P due `max(P, today + 5)`, `priceUnreviewed: true`, `heldSince: null`).
- [ ] **Step 2: RED.** `dotnet build` of the unit and integration test projects. Expected: compile failure (`CarriedHeldPeriods`, `Pause(DateOnly, DateTimeOffset)`). Paste output.
- [ ] **Step 3: Implement** the interfaces above.
- [ ] **Step 4: Migration.** Edit `AddPriceAdjustment` (Up, Down, Designer, snapshot). `dotnet ef migrations has-pending-model-changes --project src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure --context ServiceContractsDbContext` => no changes. Snapshot brings nothing unrelated.
- [ ] **Step 5: GREEN.** Unit project. Then `$env:Seed__Enabled = "false"` and integration with `--filter "FullyQualifiedName~Carried|FullyQualifiedName~Resum|FullyQualifiedName~ServiceContractsPriceAdjustmentMigrationTests" -- xUnit.MaxParallelThreads=4`. Then once, one at a time: `ServiceContractsPriceReviewApiTests`, `ServiceContractsPersistenceTests`, `ServiceContractApiTests`.
- [ ] **Step 6: Commit** `fix(service-contracts): keep the periods held before a pause when resuming`.

---

## Handoff

- **Despliegue con corte** (R-1): `Recreate` o el backend a cero réplicas mientras corre `AddPriceAdjustment`. La migración rellena datos existentes (`Manual`, `period_date = due_date`, revisión `>= hoy UTC + 15`): ningún contrato queda retenido. El frontend publicado tolera el backend viejo y el nuevo.
- **`docs/estado.md`** se actualiza en `develop`, al mergear.
- **Pendiente del owner:** las preguntas P-1 a P-5 siguen abiertas con los defaults de «Global Constraints»; R-8 (formato del motivo automático) es una decisión del plan revisable.
