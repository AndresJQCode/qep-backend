# Nómina: vacaciones disfrutadas (pago anticipado, salario fijo) — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **El `CLAUDE.md` del workspace manda sobre el proceso del skill** («Presupuesto de pruebas y ejecución de planes»): **tres
> bloques** (A, B y C), **un implementador por bloque**, una revisión por bloque y la revisión fuerte (modelo más capaz, toda la
> rama) una sola vez al final. Cada tarea conserva su commit.

**Goal:** registrar vacaciones disfrutadas por empleado y empresa (origen `Qep` o `External`), pagarlas completas en la quincena
que contiene el inicio, descontar sus días de las quincenas que tocan y cotizarlas por mes, con el motor en `2026.11.5`.

**Architecture:** el motor sigue puro (`Modules.Payroll.Domain/Engine`): recibe las vacaciones del mes en el `EngineInput`
(`VacationInput`) y cuenta días de nómina sólo desde fechas (`VacationCalendar`). Application calcula días hábiles con un puerto
de festivos (`IPayrollHrDirectory` gana `RestDays`; `IPayrollHolidayCalendar` nuevo, adaptador en Bootstrapper sobre
`ColombianHolidays`), resuelve el IBC anterior y lee la foto de la quincena que paga (`VacationInputBuilder`), y bloquea por
huella (`VacationLocks`). Un agregado `Vacation` (tabla `payroll.vacations`) con su CRUD bajo
`/companies/{companyId}/employees/{employeeId}/vacations`.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers (Postgres 18) sólo en GREEN de integración,
ClosedXML (Excel).

**Spec:** `docs/superpowers/specs/2026-11-23-nomina-vacaciones-disfrutadas-design.md` (léelo entero antes de empezar; manda sobre
este plan). Base: `docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md`, su plan
`docs/superpowers/plans/2026-11-21-nomina-periodo-y-motor-backend.md` («Motor: definiciones exactas», reglas R-n) y
`docs/superpowers/plans/2026-11-22-nomina-decisiones-del-owner.md` (D-n). Pantallas: spec del frontend con el mismo nombre en
`qep-frontend`, que consume la sección «Contrato HTTP» de este plan.

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\nomina-vacaciones`, rama
  `feature/nomina-vacaciones`. Antes de **cada** commit, `git branch --show-current` en ese momento dice
  `feature/nomina-vacaciones`. Conventional commits en inglés, **sin atribución de IA** (ni `Co-Authored-By`).
- Comandos al developer en PowerShell (`A; if ($?) { B }`, nunca `&&`; `curl.exe`; cuerpos JSON a archivo con `-d "@archivo.json"`).
  `Api.exe` corriendo bloquea el build: se avisa, no se detiene.
- **TDD estricto:** cada tarea tiene RED y GREEN con salida literal. Un RED que es error de compilación no levanta contenedores: la
  evidencia es la salida de `dotnet build`. La integración (Testcontainers) corre **sólo en GREEN**, filtrada por nombre de prueba
  (`--filter "FullyQualifiedName~X"`) con `-- xUnit.MaxParallelThreads=4`; la clase entera, una vez al final del bloque.
- **Antes de cualquier corrida de integración:** `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. Si hay un
  `dotnet test` o `testhost` de otra sesión, se espera; **nunca se detiene**. Nunca dos corridas de integración a la vez. Las
  pruebas corren en primer plano, también las de un subagente.
- Una prueba nueva que pasa sin tocar código es **de caracterización**: se dice en el commit y no cuenta como el RED de la tarea.
- El motor es puro: nada de base, reloj ni calendario en `Modules.Payroll.Domain`. Los días hábiles se calculan en Application.
- Payroll no referencia Messaging ni Hr (`PayrollLayerTests`): los festivos y los días de descanso llegan por puertos con adaptador
  en Bootstrapper. Los festivos son **siempre** los de Colombia (no se mira el país del tenant).
- Nada se inventa: los códigos nuevos son los ocho `payroll.vacation.*` del spec (tabla «Códigos de error»), que se agregan
  primero a la sección «Contrato HTTP» de este plan (regla de `PayrollErrorCodes`). Lo que falte es decisión pendiente del owner.
- Copy al usuario en español neutro, tuteando. Identificadores, comentarios y commits en inglés; los comentarios dicen por qué.
- Los 29 casos de oro de hoy **no cambian**: sin vacaciones el motor da exactamente lo de hoy (spec, «Interacción…»).
- Valores marcados en el spec como «a verificar contra la exportación de Siigo (Tarea 15)» (días pagados por calendario, llamada
  2; provisiones, llamada 5) se implementan como dice el spec y quedan como **nota de verificación**, no como bloqueo.
- `docs/estado.md` **no se toca en la rama**: se actualiza en `develop`, al mergear (paso de merge al final, no es tarea).
- Proyectos de prueba de este cambio: `Modules.Payroll.UnitTests`, `Modules.Payroll.IntegrationTests`, `Bootstrapper.UnitTests`
  y `ArchitectureTests`. El código de Hr **no cambia** (sólo el puerto de Payroll y el adaptador de Bootstrapper leen
  `Employee.RestDays`, que ya existe), así que las pruebas de Hr no se corren. No es transversal en el sentido del `CLAUDE.md`
  del workspace: Bootstrapper sólo gana dos adaptadores de Payroll y su registro, y la migración es de un solo módulo. Antes de
  mergear: esos cuatro proyectos completos y un `dotnet build` de la solución entera.

Comandos que se repiten:

```powershell
# RED de compilación
dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests
# Unitarias filtradas
dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~<Clase>"
# Integración filtrada (sólo en GREEN, después de mirar Get-Process)
Get-Process testhost, dotnet -ErrorAction SilentlyContinue
dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~<Prueba>" -- xUnit.MaxParallelThreads=4
```

## Review Focus

1. **Huella de los períodos ya liquidados al desplegar.** `EngineInput` gana `Vacations`; serializada vacía cambiaría la huella de
   todo período Liquidado y prendería un `sourceChanged` falso en todos. `InputFingerprint.Of` quita `vacations` cuando está vacía
   (prueba en A2: la huella de una entrada sin vacaciones es igual a la de hoy, fijada antes del cambio).
2. **Una edición inocua con una quincena Aprobada que Hr ya movió.** Comparar sólo contra la huella guardada bloquearía toda
   edición si Hr cambió algo desde la aprobación. `VacationLocks` rechaza si la huella nueva difiere de la guardada **y** de la de
   hoy sin el cambio («cambiaría la huella», spec regla 1). Prueba en C2.
3. **Un anticipo mayor que lo que debe una quincena posterior** (vacaciones que cubren la primera de un mes entero, pagadas el mes
   anterior). Sin tope la línea sale negativa y le «devuelve» al trabajador plata que la segunda no puede retener. La línea con
   (b) nunca baja de 0 por (b): `línea = d − mín(b, máx(0, d))`, `d = tasa × IBC − (a)`; el mes sigue cerrando en la segunda.
   Prueba en A5 y en el caso de oro `vacation-new-year-floor`.
4. **Una edición que no toca el cálculo de una quincena Aprobada** (spec: «mover el fin dentro de una quincena no aprobada pasa»).
   La huella serializa toda la entrada: si una quincena que no paga recibiera las fechas completas, mover el fin cambiaría su huella
   sin cambiar su cálculo. `EngineInput.Vacations` lleva sólo las que pagan en esta quincena o tienen días entre el día 1 del mes y
   el fin de esta (spec: «del mes hasta ésta inclusive»), y a una que no paga aquí le llegan las fechas **recortadas** a esa
   ventana y `BusinessDays = 0` (los días de nómina no cambian al recortar). Pruebas en B4 y C2.
5. **Dos registros simultáneos que se solapan.** La validación del handler no ve el insert concurrente: la restricción `EXCLUDE`
   `EX_vacations_no_overlap` (con `btree_gist`) lo frena y `PayrollUnitOfWork` la traduce a 409 `payroll.vacation.overlap`.
   Prueba en B3.

---

## Contrato HTTP

Grupo `/api/v1/tenants/{tenantId:guid}/payroll`. Tenant en la ruta; política por permiso y revalidación en el handler
(`PayrollAuthorization`, 403 nunca 404). Ruta base `R = /companies/{companyId:guid}/employees/{employeeId:guid}/vacations`.

| Método y ruta | Política | Handler exige | Responde |
| --- | --- | --- | --- |
| `GET R` | `PeriodRead` | read | 200 `IReadOnlyList<VacationDto>`, inicio más reciente primero (desempate por `id`) |
| `POST R/preview` | `PeriodManage` | read + manage | 200 `VacationPreviewDto`; no escribe |
| `POST R` | `PeriodManage` | read + manage | 201 la lista entera; sin `If-Match` |
| `PUT R/{vacationId:guid}` | `PeriodManage` | read + manage | 200 la lista entera; `If-Match` (428/412) |
| `DELETE R/{vacationId:guid}` | `PeriodManage` | read + manage | 200 la lista entera; `If-Match` (428/412) |

Requests (todo opcional en el tipo para que el validador dé el campo):

```csharp
public sealed record VacationRequest(
    string? Origin, DateOnly? StartDate, DateOnly? EndDate, decimal? PreviousMonthIbc, int? BusinessDays,
    IReadOnlyList<ExternalVacationMonthRequest>? ExternalMonths);
public sealed record ExternalVacationMonthRequest(
    string? Month, decimal? VacationPay, decimal? HealthAdvance, decimal? PensionAdvance, decimal? SolidarityFundAdvance);
public sealed record VacationPreviewRequest(string? Origin, DateOnly? StartDate, DateOnly? EndDate);
```

Validador `VacationRequestValidator` (`validation.failed`, mapa `errors`): `origin` ∈ {`Qep`, `External`}; `startDate`,
`endDate` obligatorias; `endDate ≥ startDate`; `previousMonthIbc > 0` si viene. `External`: `previousMonthIbc` y
`businessDays ≥ 1` obligatorios; cada `externalMonths[i].month` con forma `yyyy-MM`, dentro de [mes de `startDate`, mes de
`endDate`], sin repetir; cada monto `≥ 0` (clave `externalMonths[i].<campo>`). `Qep`: `businessDays` y `externalMonths` ausentes
o vacíos (si no, error en esa clave). `VacationPreviewRequestValidator`: `origin`, `startDate`, `endDate` obligatorias,
`endDate ≥ startDate`.

DTOs (los enums por nombre; los opcionales presentes como `null`):

```csharp
public sealed record VacationQuincenaDto(DateOnly StartDate, DateOnly EndDate, int Days);
public sealed record QuincenaRefDto(DateOnly StartDate, DateOnly EndDate);
public sealed record VacationPaidInDto(DateOnly StartDate, DateOnly EndDate, Guid? PeriodId, string? Status);
public sealed record ExternalVacationMonthDto(
    string Month, decimal? VacationPay, decimal HealthAdvance, decimal PensionAdvance, decimal SolidarityFundAdvance);
public sealed record VacationCanDto(bool Update, bool Delete);
public sealed record VacationDto(
    Guid Id, Guid EmployeeId, string Origin, DateOnly StartDate, DateOnly EndDate, int BusinessDays, DateOnly ReturnDate,
    decimal? PreviousMonthIbc, IReadOnlyList<VacationQuincenaDto> Quincenas, VacationPaidInDto? PaidIn,
    IReadOnlyList<ExternalVacationMonthDto> ExternalMonths, long Version, VacationCanDto Can);
public sealed record VacationPreviewDto(
    int BusinessDays, DateOnly ReturnDate, IReadOnlyList<VacationQuincenaDto> Quincenas, IReadOnlyList<string> Months,
    QuincenaRefDto? PaidIn);
public sealed record PeriodVacationDto(Guid EmployeeId, string FullName, DateOnly StartDate, DateOnly EndDate, int Days);
```

- `VacationDto.Quincenas`: los días de nómina por quincena (`VacationCalendar`), la pantalla no los cuenta. `PaidIn`: la quincena
  que contiene `StartDate` con el id y el estado del período si existe; `null` con `External`. `ExternalMonths`: vacío con `Qep`.
  `Can`: ambos falsos si alguna quincena que alcanzan (`VacationReach`) está Aprobada o Cerrada.
- `VacationPreviewDto.BusinessDays`: el calculado por QEP (con `External`, de referencia). `Months`: los `yyyy-MM` que tocan, en
  orden. `PaidIn`: `null` con `External`. Con `Qep` responde los 422 de fechas (`starts_on_rest_day`, `no_business_days`,
  `outside_contract`, `before_first_period`, `employee.not_liquidable`); nunca `overlap` ni bloqueos (no sabe qué se edita).
- Cambios en DTOs existentes: `PayrollTotalsDto` gana `VacationPay` (después de `Salary`); `PayrollEmployeeRowDto` gana
  `int? VacationDays` (al final; nulo hasta liquidar, como `WorkedDays`); `PayrollEmployeeDetailDto` gana
  `IReadOnlyList<VacationDto> Vacations` (las que tocan el período: lo pagan o tienen días en él); `PayrollPeriodDetailDto` gana
  `IReadOnlyList<PeriodVacationDto> Vacations` (mismas, `Days` = días de nómina en esta quincena, para el diálogo de liquidar).

Códigos nuevos (`PayrollErrorCodes`):

| Constante | Código | HTTP |
| --- | --- | --- |
| `VacationNotFound` | `payroll.vacation.not_found` | 404 (`PayrollNotFound.Vacation(id)` → `ResourceNotFoundException`) |
| `VacationOverlap` | `payroll.vacation.overlap` | 409 (entra en `Conflicts`; `PayrollConflicts.Run` y `PayrollUnitOfWork`) |
| `VacationStartsOnRestDay` | `payroll.vacation.starts_on_rest_day` | 422, sólo `Qep` |
| `VacationNoBusinessDays` | `payroll.vacation.no_business_days` | 422, sólo `Qep` |
| `VacationOutsideContract` | `payroll.vacation.outside_contract` | 422 |
| `VacationBeforeFirstPeriod` | `payroll.vacation.before_first_period` | 422 |
| `VacationNotPaidYet` | `payroll.vacation.not_paid_yet` | 422, al liquidar |
| `VacationPaidPeriodLocked` | `payroll.vacation.paid_period_locked` | 422, al liquidar la que paga |

Se reutilizan `payroll.company.not_found`, `payroll.employee.not_found`, `payroll.company.inactive`,
`payroll.employee.not_liquidable` y `payroll.period.not_editable` (con el detalle «Reabre la quincena para cambiar estas
vacaciones»). `BlockerReason` gana `VacationOutsideContract` (etiqueta «Tiene vacaciones fuera de su contrato: corrígelas»).
Auditoría: `payroll.vacation.created|updated|deleted`, recurso `vacation`, campos `origin`, `startDate`, `endDate`,
`previousMonthIbc`, `businessDays`, `externalMonths` (nombres, nunca valores).

---

## Bloques

| Bloque | Tareas | Qué deja funcionando | Pruebas |
| --- | --- | --- | --- |
| A — Motor puro | A1–A6 | El motor liquida vacaciones con los ejemplos del spec como casos de oro; versión `2026.11.5` | sólo `Modules.Payroll.UnitTests` |
| B — Datos y lecturas | B1–B4 | Descansos y festivos por puerto, agregado y tabla, y el planificador que arma `VacationInput` y guarda la foto | unitarias + `Bootstrapper.UnitTests` + integración filtrada; al final `PayrollHrDirectoryTests`, `PayrollPersistenceTests` y `PayrollVacationPlannerTests` enteras |
| C — Casos de uso y HTTP | C1–C5 | Las rutas, los bloqueos, la liquidación entre meses, los DTOs y el Excel | unitarias + integración filtrada; al final los cuatro proyectos completos |

---

## Bloque A — Motor puro (Tareas A1–A6)

Un implementador. Todo en `src/Modules/Payroll/Modules.Payroll.Domain` y `tests/Modules/Payroll/Modules.Payroll.UnitTests`. Al
terminar: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` una vez (no tiene
contenedores) y la revisión del bloque.

### Task A1: Días de nómina por quincena (`VacationCalendar`)

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Domain/VacationCalendar.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/VacationCalendarTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record VacationQuincenaDays(DateOnly Start, DateOnly End, int Days);
  public static class VacationCalendar
  {
      /// Every quincena the range touches, in order, with its payroll days (spec «Días»).
      public static IReadOnlyList<VacationQuincenaDays> PayrollDays(DateOnly start, DateOnly end);
      /// The «yyyy-MM» months the range touches, in order.
      public static IReadOnlyList<string> Months(DateOnly start, DateOnly end);
  }
  ```

Regla (spec «Días»): por cada quincena `q` que el rango toca (`QuincenaCalendar.Containing`), si el rango cubre `q.End` los días son
`15 − (días de q antes del inicio)`; si no, son los días reales del rango dentro de `q`.

- [ ] **Step 1: Pruebas** (`VacationCalendarTests`):
  - `CrossesMonthFromTheFirstHalf`: `PayrollDays(2026-12-14, 2027-01-05)` = `[(2026-12-01, 2026-12-15, 2), (2026-12-16, 2026-12-31, 15), (2027-01-01, 2027-01-15, 5)]`.
  - `TheThirtyFirstDoesNotCount`: `PayrollDays(2026-12-20, 2026-12-31)` = `[(2026-12-16, 2026-12-31, 11)]`.
  - `FebruaryAbsorbsTheDifference`: `PayrollDays(2027-02-17, 2027-03-05)` = `[(2027-02-16, 2027-02-28, 14), (2027-03-01, 2027-03-15, 5)]`.
  - `SecondExampleOfTheSpec`: `PayrollDays(2027-01-12, 2027-02-01)` = `[(…-01-01, …-01-15, 4), (…-01-16, …-01-31, 15), (…-02-01, …-02-15, 1)]`.
  - `MonthsInOrder`: `Months(2026-11-23, 2026-12-04)` = `["2026-11", "2026-12"]`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests` → `error CS0103: The name 'VacationCalendar' does not exist`.
- [ ] **Step 3: Implementar** `VacationCalendar` en `Modules.Payroll.Domain` (namespace `Modules.Payroll.Domain`), sobre `QuincenaCalendar`.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~VacationCalendarTests"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): count payroll days of a vacation per quincena`

### Task A2: La entrada del motor y los días trabajados sin vacaciones

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/EngineInput.cs`, `EmployeeLiquidation.cs`, `PayrollEngine.cs`,
  `SalaryRule.cs`, `TransportAllowanceRule.cs`
- Create: `src/Modules/Payroll/Modules.Payroll.Domain/VacationOrigin.cs`, `Engine/VacationInput.cs`, `Engine/VacationDaysRule.cs`
- Modify: `src/Modules/Payroll/Modules.Payroll.Application/InputFingerprint.cs`
- Modify (tests): `Engine/SalaryRuleTests.cs`, `Engine/TransportAllowanceRuleTests.cs`, `Engine/PayrollEngineTests.cs`,
  `InputFingerprintTests.cs`, `Golden/GoldenCase.cs`, `Golden/GoldenCaseTests.cs`, `Engine/EngineTestData.cs`
- Create (tests): `Engine/VacationDaysRuleTests.cs`

**Interfaces:**
- Consumes: `VacationCalendar.PayrollDays` (A1).
- Produces:
  ```csharp
  public enum VacationOrigin { Qep, External }                          // Modules.Payroll.Domain
  public enum VacationIbcSource { Qep, Typed, Salary }                  // Modules.Payroll.Domain.Engine
  public sealed record VacationAdvance(decimal Health, decimal Pension, decimal SolidarityFund)
  { public static VacationAdvance Zero { get; } = new(0m, 0m, 0m); }
  /// <param name="PaidHere">This quincena contains StartDate and the origin is Qep: it pays the whole vacation.</param>
  /// <param name="MonthVacationPay">External only: what Siigo paid for the days of this period's month; null if not typed.</param>
  /// <param name="MonthAdvance">Term (b) for this period's month; Zero when PaidHere.</param>
  public sealed record VacationInput(
      Guid VacationId, VacationOrigin Origin, DateOnly StartDate, DateOnly EndDate, int BusinessDays, bool PaidHere,
      decimal SalaryAtStart, DateOnly SalaryRecordDate, decimal PreviousMonthIbc, VacationIbcSource PreviousIbcSource,
      decimal? MonthVacationPay, VacationAdvance MonthAdvance);
  // EngineInput gains, last: IReadOnlyList<VacationInput> Vacations
  public sealed record DateRange(DateOnly From, DateOnly To);
  public sealed record VacationDaysOutcome(
      int Days, int MonthDays, decimal IbcPart, decimal ParafiscalValue, decimal ServiceValue,
      VacationAdvance MonthAdvance, IReadOnlyList<DateRange> Ranges)
  { public static VacationDaysOutcome None { get; } }
  public static class VacationDaysRule { public static VacationDaysOutcome Compute(EngineInput input, WorkedDays worked); }
  // EmployeeLiquidation gains, last: int VacationDays, IReadOnlyList<VacationPhoto> VacationPhotos   (VacationPhoto: A3)
  // SalaryRule.Compute(EngineInput, WorkedDays, VacationDaysOutcome); the 2-arg overload passes VacationDaysOutcome.None
  // TransportAllowanceRule.Compute(EngineInput, WorkedDays, SalaryOutcome, MonthIncome, VacationDaysOutcome); idem
  ```

`VacationDaysRule` (puro): para cada `VacationInput`, sus días en esta quincena son los de `VacationCalendar.PayrollDays` cuyo
`Start == Period.Start`; los del mes hasta aquí suman los de las quincenas del mismo mes con `Start ≤ Period.Start`. Valor por día
`v`: `Qep` → `SalaryAtStart ÷ 30`; `External` → `MonthVacationPay ÷ (días de nómina del mes del período)` si viene, si no
`SalaryAtStart ÷ 30`. `IbcPart = Σ PreviousMonthIbc × díasMes ÷ 30`, `ParafiscalValue = Σ v × díasMes`, `ServiceValue = Σ v × días`
(multiplica antes de dividir, como `SalaryRule`); `MonthAdvance = Σ MonthAdvance`; `Ranges` = los días de vacaciones dentro de la
quincena (para cortar los tramos de salario).

- [ ] **Step 1: Pruebas.**
  - Todo `new EngineInput(...)` pasa `Vacations: []` (`EngineTestData.Input`, `GoldenInput.ToEngineInput`, y
    `EngineInputFactory.Build` hasta B4). `EngineTestData` gana
    `Vacation(DateOnly start, DateOnly end, bool paidHere, decimal salary = 3_000_000m, decimal previousIbc = 3_000_000m, VacationOrigin origin = Qep, decimal? monthPay = null, VacationAdvance? advance = null)`
    (`BusinessDays` 15, `SalaryRecordDate` 2025-01-01, fuente `Typed`).
  - `VacationDaysRuleTests.CountsTheDaysOfThisQuincenaAndOfTheMonth`: período 2026-12-16..31, vacación 2026-12-14..2027-01-05 no
    pagada aquí → `Days = 15`, `MonthDays = 17`, `IbcPart = 1_700_000`, `ParafiscalValue = 1_700_000`, `ServiceValue = 1_500_000`.
  - `VacationDaysRuleTests.ExternalUsesWhatSiigoPaid`: período 2026-12-01..15, `External` 2026-11-23..2026-12-04, `monthPay = 480_000`
    → `Days = 4`, valor por día 120 000: `ParafiscalValue = 480_000`, `ServiceValue = 480_000`.
  - `SalaryRuleTests.PaysOnlyTheWorkedDays`: período 2026-12-01..15, salario 3 000 000, vacación desde 2026-12-14 → línea `salary`
    1 300 000; `SalaryRuleTests.ZeroWorkedDaysPaysZero`: 2026-12-16..31 cubierta entera → línea 0, sin excepción, y
    `SalaryInForce = 3_000_000`.
  - `SalaryRuleTests.SegmentsAreCutOnWorkedDaysOnly`: aumento a 3 300 000 el 2026-12-10, vacación 2026-12-14..: tramo 1 de 9 días
    a 3 000 000 y tramo 2 de 4 días a 3 300 000 (Σ = 13); línea 1 340 000.
  - `TransportAllowanceRuleTests.NoVacationDayAccruesTheAllowance`: 2026-12-01..15 con 2 días de vacaciones → 107 941
    (`round(249 095 × 13 ÷ 30)`); con 15 → 0.
  - `PayrollEngineTests.WorkedDaysExcludeVacationDays`: 2026-12-16..31 con 15 días de vacaciones → `WorkedDays = 0`,
    `VacationDays = 15`, `Month.WorkedDays = prior.WorkedDays + 0`.
  - `InputFingerprintTests.NoVacationsKeepsTodaysFingerprint`: **antes de tocar el motor**, copia en la prueba el hash que da hoy
    `InputFingerprint.Of(EngineTestData.Input(new DateOnly(2026, 11, 1), 2_500_000m))` (sácalo con el código de la rama antes del
    primer cambio de esta tarea y pega el literal);
    después del cambio con `Vacations = []` sigue igual. Y `AVacationChangesIt`: agregar una vacación cambia el hash.
  - `GoldenCase.GoldenInput` gana `IReadOnlyList<VacationInput>? Vacations` (ausente = `[]`) y `GoldenExpected` gana
    `int? VacationDays` (si viene, `GoldenCaseTests` lo compara con `liquidation.VacationDays`).
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests` → `error CS0246: The type or namespace name 'VacationInput' could not be found` (y `VacationDaysRule`).
- [ ] **Step 3: Implementar.**
  - Los tipos de «Interfaces». `PayrollEngine.Liquidate`: `vac = VacationDaysRule.Compute(input, worked)` justo después de
    `WorkedDaysRule`; `SalaryRule` y `TransportAllowanceRule` reciben `vac`; `MonthAccumulators.WorkedDays = prior.WorkedDays +
    (worked.Days − vac.Days)`; `EmployeeLiquidation(…, WorkedDays: worked.Days − vac.Days, …, VacationDays: vac.Days, VacationPhotos: [])`.
  - `SalaryRule`: los tramos se arman sobre `[CoveredStart, CoveredEnd]` menos `vac.Ranges`; cada tramo cuenta sus días reales y el
    último absorbe la diferencia hasta `worked.Days − vac.Days`; con 0 días la línea es 0 (`Raw = 0`) y `SalaryInForce`,
    `Teleworker` y `SalaryRecordDate` siguen siendo los del último día cubierto. El rastro dice «Días trabajados» con los netos y
    suma «Días de vacaciones» sólo si hay.
  - `TransportAllowanceRule`: `raw = auxilio × (worked.Days − vac.Days) ÷ 30`; «Días trabajados» del rastro, los netos.
  - `InputFingerprint.Of`: después de serializar, `if (node["vacations"] is JsonArray { Count: 0 }) node.Remove("vacations");`
    con el comentario del porqué (Review Focus 1).
  - `RecurringConceptRule` **no cambia** (sigue con los días cubiertos; ver «Fuera de este plan»).
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests` (toda la unitaria: confirma que los 29
  casos de oro no cambian) → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): take vacation days out of the worked days`

### Task A3: La línea `vacation_pay`, el resumen y la foto

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/VacationPayRule.cs`, `Engine/VacationPhoto.cs`
- Modify: `Engine/PayrollSummary.cs`, `Engine/PayrollEngine.cs`
- Test: `Engine/VacationPayRuleTests.cs`, `Engine/PayrollEngineTests.cs`; ajustar los `new PayrollSummary(...)` de las pruebas
  (`PayrollColumnLayoutTests.Numbered` pasa a 24 posiciones en C4, aquí sólo compila con el campo nuevo).

**Interfaces:**
- Consumes: `VacationInput`, `VacationCalendar.PayrollDays`.
- Produces:
  ```csharp
  public sealed record VacationMonthAdvance(DateOnly Month, decimal IbcPart, decimal Health, decimal Pension, decimal SolidarityFund);
  /// What the paying quincena froze for the following ones (spec «Foto de las vacaciones»); Months = retained, not computed.
  public sealed record VacationPhoto(
      Guid VacationId, decimal SalaryAtStart, DateOnly SalaryRecordDate, decimal PreviousMonthIbc, VacationIbcSource PreviousIbcSource,
      IReadOnlyList<VacationQuincenaDays> Quincenas, IReadOnlyList<VacationMonthAdvance> Months);
  public sealed record VacationPayOutcome(IReadOnlyList<PayrollLine> Lines, decimal Total);
  public static class VacationPayRule { public static VacationPayOutcome Compute(EngineInput input); }
  // PayrollSummary gains decimal VacationPay right after Salary; TotalEarned includes it
  ```

- [ ] **Step 1: Pruebas.**
  - `VacationPayRuleTests.PaysEveryPayrollDayOfTheRange`: período 2026-12-01..15, vacación 2026-12-14..2027-01-05 pagada aquí,
    salario 3 000 000 → una línea `vacation_pay:{id}`, `Earning`, etiqueta «Vacaciones disfrutadas», monto 2 200 000,
    `SalaryConstitutive == null`; el rastro tiene «Inicio», «Fin», «Días hábiles» (15), un `TraceInput` por quincena con su fecha de
    inicio y días (2, 15, 5), «Salario del inicio» 3 000 000 con su fecha de registro, y la nota
    «CST arts. 186 y 192; pago anticipado de todo el disfrute».
  - `VacationPayRuleTests.OnlyTheQuincenaThatPays`: la misma vacación con `PaidHere = false` → sin líneas; una `External` → sin líneas.
  - `VacationPayRuleTests.TwoVacationsTwoLines`: dos vacaciones que empiezan en la quincena → dos líneas y `Total` = suma.
  - `PayrollEngineTests.VacationPayIsEarnedButNotSalary`: con la vacación pagada aquí, `Summary.VacationPay = 2_200_000`,
    `Summary.TotalEarned` la incluye, y ni `MonthIncome` ni el tope del auxilio la ven (el auxilio sigue en 107 941).
- [ ] **Step 2: RED.** build → `error CS0103: The name 'VacationPayRule' does not exist` / `'PayrollSummary' does not contain a definition for 'VacationPay'`.
- [ ] **Step 3: Implementar.** `amount = ToPeso(SalaryAtStart × Σ días de nómina de todas las quincenas ÷ 30)` (multiplica primero).
  En `PayrollEngine`, las líneas van `salary`, `vacation_pay:*`, auxilio, horas… (I-11: «Vacaciones disfrutadas» va después de
  «Salario», como en el Excel). `PayrollSummary.From`: `VacationPay = Σ líneas con Code que empieza por "vacation_pay:"`; `Sum`
  la suma. Las fotos se arman en A5 (aquí `VacationPhotos` sigue `[]`).
- [ ] **Step 4: GREEN.** `--filter "FullyQualifiedName~VacationPayRuleTests|FullyQualifiedName~PayrollEngineTests|FullyQualifiedName~GoldenCaseTests"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): pay the whole vacation in the quincena where it starts`

### Task A4: Tres bases del mes, aportes del empleador y provisiones

**Files:**
- Modify: `Engine/IbcRule.cs`, `Engine/EmployerContributionsRule.cs`, `Engine/ProvisionsRule.cs`, `Engine/PayrollEngine.cs`
- Test: `Engine/IbcRuleTests.cs`, `Engine/EmployerContributionsRuleTests.cs`, `Engine/ProvisionsRuleTests.cs`

**Interfaces:**
- Consumes: `VacationDaysOutcome` (A2).
- Produces:
  ```csharp
  public sealed record IbcOutcome(decimal SocialSecurityIbc, decimal ArlBase, decimal ParafiscalBase, PayrollLine Line);
  // IbcRule.Compute(EngineInput, WorkedDays, VacationDaysOutcome, decimal salaryLine, MonthIncome); 4-arg overload = None
  // ProvisionsRule.Compute(EngineInput, decimal salary, decimal transport, decimal overtime, decimal salaryIncome, decimal serviceValue); 5-arg overload = 0
  ```

Fórmulas (spec «IBC: tres bases por mes»): `rawWorked` = la base de hoy (devengo salarial con la regla del 40 %); `wm` =
`Prior.WorkedDays + worked.Days − vac.Days`; `vm` = `vac.MonthDays`.
`SocialSecurityIbc = CeilingToPeso(mín(máx(rawWorked + vac.IbcPart; SMMLV × (wm + vm) ÷ 30); 25 SMMLV))`;
`ArlBase = CeilingToPeso(mín(máx(rawWorked; SMMLV × wm ÷ 30); 25 SMMLV))`; `ParafiscalBase = SalDev_m + vac.ParafiscalValue`. La
línea `ibc` sigue siendo `SocialSecurityIbc`; con vacaciones su rastro suma «IBC anterior a las vacaciones», «Días de vacaciones
del mes» y «Parte de vacaciones», y la nota «Sin IBC del mes anterior en QEP: se usa el salario» si alguna llega con
`PreviousIbcSource = Salary`.

- [ ] **Step 1: Pruebas.**
  - `IbcRuleTests.TheVacationDaysUseThePreviousIbc`: 2026-12-01..15, 2 días de vacaciones (IBC anterior 3 000 000), salario
    1 300 000 → `SocialSecurityIbc = 1_500_000`, `ArlBase = 1_300_000`, `ParafiscalBase = 1_500_000`.
  - `IbcRuleTests.TheSecondHalfCountsTheFirstHalfsVacationDays`: 2026-12-16..31, 15 días, `Prior` con 13 trabajados y salario
    1 300 000 → `SocialSecurityIbc = 3_000_000`, `ArlBase = 1_300_000`, `ParafiscalBase = 3_000_000`.
  - `IbcRuleTests.FallbackSalaryNote`: con `PreviousIbcSource = Salary` la línea lleva la nota exacta de arriba.
  - `EmployerContributionsRuleTests.ArlIgnoresVacationDays`: con el segundo caso y `Prior.Arl = 6_800` → `arl` 0; `pension_employer`
    180 000 con `Prior.PensionEmployer = 180_000`; `compensation_fund` 60 000 con `Prior.CompensationFund = 60_000`.
  - `ProvisionsRuleTests.VacationDaysAreServiceTimeButThePayIsNot`: salario 0, auxilio 0, `serviceValue = 1_500_000` → cesantías y
    prima 124 950, intereses 15 000, vacaciones 62 550 (spec, ejemplo de la segunda de enero).
- [ ] **Step 2: RED.** build → `error CS7036: There is no argument given that corresponds to the required parameter 'ArlBase'` (o `'IbcOutcome' does not contain a definition for 'ArlBase'`).
- [ ] **Step 3: Implementar.** `EmployerContributionsRule`: ARL sobre `ibc.ArlBase` con base del rastro «IBC del mes sin
  vacaciones»; salud y pensión siguen en `SocialSecurityIbc`; caja, ICBF y SENA en `ParafiscalBase`. La exoneración no cambia.
  `ProvisionsRule`: `fullBase = salary + transport + overtime + salaryIncome + serviceValue`, `vacationBase = salary + salaryIncome +
  serviceValue`; el rastro suma «Días de vacaciones como tiempo de servicio» sólo si `serviceValue > 0`. `vacation_pay` no entra
  en ninguna base. Nota de verificación (llamada 5, Tarea 15): esta lectura se confirma contra la exportación de Siigo.
- [ ] **Step 4: GREEN.** `--filter "FullyQualifiedName~IbcRuleTests|FullyQualifiedName~EmployerContributionsRuleTests|FullyQualifiedName~ProvisionsRuleTests|FullyQualifiedName~GoldenCaseTests"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): split the month bases for vacation days`

### Task A5: Deducciones del trabajador: término (b), anticipo en la que paga y tope por neto

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/VacationAdvanceRule.cs`
- Modify: `Engine/EmployeeDeductionsRule.cs`, `Engine/SolidarityFundRule.cs`, `Engine/PayrollSummary.cs`, `Engine/PayrollEngine.cs`
- Test: `Engine/EmployeeDeductionsRuleTests.cs`, `Engine/SolidarityFundRuleTests.cs`, `Engine/VacationAdvanceRuleTests.cs`,
  `Engine/PayrollEngineTests.cs`

**Interfaces:**
- Consumes: `VacationDaysOutcome`, `VacationPhoto`, `VacationMonthAdvance` (A2, A3).
- Produces:
  ```csharp
  // EmployeeDeductionsRule.Compute(EngineInput, IbcOutcome, VacationDaysOutcome); 2-arg overload = None
  // SolidarityFundRule.Compute(EngineInput, decimal salaryInForce, IbcOutcome, VacationDaysOutcome); 3-arg overload = None
  public sealed record VacationAdvanceOutcome(IReadOnlyList<PayrollLine> Lines, IReadOnlyList<VacationPhoto> Photos);
  public static class VacationAdvanceRule
  {
      /// <param name="available">The net of the quincena before the advance lines; what exceeds it is not withheld.</param>
      public static VacationAdvanceOutcome Compute(EngineInput input, decimal available);
  }
  ```

Reglas (spec «Deducciones del trabajador» y Review Focus 3):
- Posterior, por línea de salud y pensión: `d = ToPeso(tasa × SocialSecurityIbc) − (a)`, `línea = d − mín(b, máx(0, d))` con `b` =
  `vac.MonthAdvance.Health` (o `.Pension`). FSP igual con `d = CeilingToHundred(IBC × tasa del tramo) − (a)` y `b =
  MonthAdvance.SolidarityFund`; la línea existe si hay tasa, `(a) ≠ 0` o `b ≠ 0`. El rastro suma «Anticipo de vacaciones del
  mes» cuando `b > 0`.
- Anticipo (sólo vacaciones `PaidHere`): por cada mes `m` con días de nómina fuera de esta quincena (la otra del mes y los
  siguientes), `parte_m = CeilingToPeso(PreviousMonthIbc × d_m ÷ 30)`; salud y pensión `ToPeso(tasa × parte_m)`; FSP
  `CeilingToHundred(parte_m × tasa del tramo de PreviousMonthIbc ÷ SMMLV)` sólo si esa tasa existe (≥ 4 SMMLV). Se retiene hasta
  `available` en orden de mes y, dentro del mes, salud, pensión, FSP; lo retenido va a `VacationMonthAdvance` y a la foto. Líneas
  `health_employee_vacation_advance` y `pension_employee_vacation_advance` (etiquetas «Anticipo de salud por vacaciones» y
  «Anticipo de pensión por vacaciones») si hay algún mes; `solidarity_fund_vacation_advance` («Anticipo del FSP por vacaciones»)
  si su total > 0. Rastro: un `TraceInput` por mes (`yyyy-MM`, parte, calculado, retenido) y la nota «Se retiene hasta dejar el
  neto en 0» cuando retenido < calculado.
- Tope por neto en una quincena sin devengo (todas las `Earning` en 0) y con días de vacaciones: las líneas positivas de salud,
  pensión y FSP del trabajador pasan a 0. Nota en primera quincena: «Sin devengo en esta quincena: lo que falta se retiene en la
  segunda quincena del mes»; en segunda: «Sin devengo en esta quincena: {x} pesos no se retienen (decisión del owner)» con `x` el
  monto quitado (`F0`, invariante).
- `PayrollSummary`: `HealthEmployee = health_employee + health_employee_vacation_advance`, igual pensión y FSP (es lo que retiene
  la colilla). `MonthAccumulators.HealthEmployee|PensionEmployee|SolidarityFund` suman **sólo** `health_employee`,
  `pension_employee`, `solidarity_fund` de las líneas, no del resumen.
- Orden de líneas: …`health_employee`, `pension_employee`, `solidarity_fund`, los tres anticipos, luego las deducciones capturadas.
- `EmployeeLiquidation.VacationPhotos` = `VacationAdvanceOutcome.Photos` (una por vacación `PaidHere`, con `Quincenas` de
  `VacationCalendar` y `Months` retenidos).

- [ ] **Step 1: Pruebas.**
  - `EmployeeDeductionsRuleTests.SecondHalfSubtractsTheMonthAdvance`: IBC 3 000 000, `Prior.HealthEmployee = 60_000`,
    `MonthAdvance.Health = 60_000` → `health_employee` 0; igual pensión.
  - `EmployeeDeductionsRuleTests.TheAdvanceNeverTurnsTheLineIntoARefund`: d = 38 000, b = 70 036 → línea 0.
  - `VacationAdvanceRuleTests.AdvancesEveryOtherMonth` (ejemplo 2 del spec): período 2027-01-01..15, vacación 2027-01-12..2027-02-01
    pagada aquí, IBC anterior 3 000 000, `available` grande → `health_employee_vacation_advance` 64 000, `pension_…` 64 000, sin
    FSP; foto con `Months = [(2027-01-01, 1_500_000, 60_000, 60_000, 0), (2027-02-01, 100_000, 4_000, 4_000, 0)]` y
    `Quincenas` (4, 15, 1).
  - `VacationAdvanceRuleTests.RetainsUpToTheNet`: lo mismo con `available = 100_000` → salud enero 60 000, pensión enero 40 000,
    resto 0; la foto guarda lo retenido; la nota aparece.
  - `PayrollEngineTests.NoEarningsCapsTheWorkerLines`: segunda quincena con 15 días de vacaciones, `d − b = 5 964` → 0 y nota
    «Sin devengo en esta quincena: 5964 pesos no se retienen (decisión del owner)»; primera quincena análoga → nota de la segunda.
  - `PayrollEngineTests.AdvanceLinesStayOutOfTheMonthAccumulators`: en la que paga, `Month.HealthEmployee = 60_000` aunque
    `Summary.HealthEmployee = 124_000`.
- [ ] **Step 2: RED.** build → `error CS0103: The name 'VacationAdvanceRule' does not exist`.
- [ ] **Step 3: Implementar** según las reglas de arriba. `available` = `TotalEarned − TotalDeductions` antes de los anticipos (si
  es negativo, 0).
- [ ] **Step 4: GREEN.** `--filter "FullyQualifiedName~EmployeeDeductionsRuleTests|FullyQualifiedName~SolidarityFundRuleTests|FullyQualifiedName~VacationAdvanceRuleTests|FullyQualifiedName~PayrollEngineTests|FullyQualifiedName~GoldenCaseTests"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): withhold the worker's social security of the vacation in advance`

### Task A6: Casos de oro de vacaciones y motor `2026.11.5`

**Files:**
- Create: `tests/Modules/Payroll/Modules.Payroll.UnitTests/Golden/legal/2027-01-01.json` (sintético: los valores de
  `2026-11-01.json` con `resolvedAt` 2027-01-01, `description` en el caso que lo usa), `Golden/legal/2027-01-01-smmlv-1900000.json`
  (igual, con `smmlv.value` 1 900 000 y `effectiveFrom` 2027-01-01).
- Create (goldens, `Golden/synthetic/`): ver la tabla.
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/PayrollEngine.cs` (`Version = "2026.11.5"`), las pruebas que fijan la
  versión (`rg -l "2026.11.4" tests src`).

Todos con `settings` `Biweekly` / `SalaryOnly` / `SalaryOnly` / `exonerated: true`, ARL clase I, salario 3 000 000 desde
2025-01-01 salvo que se diga otra cosa. Cada `notes` lleva la cuenta a mano **antes** de correr (convención de los casos de hoy).

| Archivo (`name`) | Período | Vacación (entrada) | Esperado (además de `notes`) |
| --- | --- | --- | --- |
| `vacation-crosses-month-paid-first-half-1` | 2026-12-01..15 | Qep 2026-12-14..2027-01-05, 15 hábiles, pagada aquí, IBC ant. 3 000 000 `Typed` | `salary` 1 300 000; `vacation_pay:{id}` 2 200 000; auxilio 107 941; `ibc` 1 500 000; salud y pensión 60 000; anticipos 80 000 c/u; `pension_employer` 180 000; `arl` 6 800; `compensation_fund` 60 000; cesantías y prima 133 941; intereses 16 079; vacaciones 62 550; neto 3 327 941; costo 4 201 252; `vacationDays` 2 |
| `vacation-crosses-month-paid-first-half-2` | 2026-12-16..31 | la misma, no pagada aquí; `MonthAdvance` 60 000/60 000/0; `prior` = `Month` del caso 1 | `salary` 0; auxilio 0; `ibc` 3 000 000; salud y pensión 0; `pension_employer` 180 000; `arl` 0; caja 60 000; cesantías y prima 124 950; intereses 15 000; vacaciones 62 550; neto 0; costo 567 450; `workedDays` 0; `vacationDays` 15 |
| `vacation-crosses-month-paid-first-half-3` | 2027-01-01..15, legal `2027-01-01.json` | la misma; `MonthAdvance` 20 000/20 000/0 | `salary` 1 000 000; auxilio 83 032; `ibc` 1 500 000; salud y pensión 40 000; `pension_employer` 180 000; `arl` 5 300; caja 60 000; cesantías y prima 131 867; intereses 15 830; vacaciones 62 550; neto 1 003 032; costo 1 670 446; `vacationDays` 5 |
| `vacation-advance-january-paid-first-half` | 2027-01-01..15, legal 2027 | Qep 2027-01-12..2027-02-01, 15 hábiles, pagada aquí, IBC ant. 3 000 000 `Qep` | `salary` 1 100 000; `vacation_pay` 2 000 000; auxilio 91 335; `ibc` 1 500 000; salud y pensión 60 000; anticipos 64 000 c/u; `pension_employer` 180 000; `arl` 5 800; caja 60 000; cesantías y prima 132 558; intereses 15 913; vacaciones 62 550; neto 2 943 335; costo 3 780 714 |
| `vacation-whole-second-half` | 2027-01-16..31, legal 2027 | la anterior, no pagada aquí; `MonthAdvance` 60 000/60 000/0; `prior` = `Month` del caso anterior | `salary` 0; auxilio 0; `ibc` 3 000 000; salud y pensión 0; `arl` 0; `pension_employer` 180 000; caja 60 000; cesantías y prima 124 950; intereses 15 000; vacaciones 62 550; neto 0; costo 567 450 |
| `vacation-starts-second-half-of-february` | 2027-02-16..28, legal 2027; `prior` de una primera de febrero trabajada entera | Qep 2027-02-17..2027-03-05, 13 hábiles, pagada aquí | `workedDays` 1; `vacationDays` 14; `salary` 100 000; `vacation_pay` 1 900 000 (19 días); anticipos de marzo: parte 500 000 → 20 000 c/u; el resto, calculado a mano en `notes` |
| `vacation-ibc-previous-month-from-qep` | 2026-12-01..15 | como el caso 1 con IBC ant. 2 800 000 `Qep` | `ibc` 1 486 667; salud y pensión 59 467; anticipos 74 667 c/u (56 000 + 18 667); el resto a mano |
| `vacation-ibc-fallback-salary` | 2026-12-01..15 | como el caso 1 con IBC ant. 3 000 000 `Salary` | los mismos montos del caso 1 (la nota la prueba `IbcRuleTests.FallbackSalaryNote`) |
| `vacation-new-year-floor` | 2027-01-16..31, legal `2027-01-01-smmlv-1900000.json` | salario 1 750 905 desde 2025-01-01 y 1 900 000 desde 2027-01-01; Qep 2026-12-28..2027-01-31 pagada en diciembre, IBC ant. 1 750 905; `MonthAdvance` 70 036/70 036/0; `prior` de la primera de enero (0 trabajados, salud y pensión 0) | `ibc` 1 900 000; salud y pensión 0 con la nota de 5964 pesos; neto 0 |
| `vacation-external-november-into-december` | 2026-12-01..15 | External 2026-11-23..2026-12-04, 10 hábiles, IBC ant. 3 000 000 `Typed`, sin montos | `salary` 1 100 000; sin `vacation_pay`; auxilio 91 335; `ibc` 1 500 000; salud y pensión 60 000; `arl` 5 800; caja 60 000; cesantías y prima 132 558; intereses 15 913; vacaciones 62 550; neto 1 071 335; costo 1 780 714 |
| `vacation-external-november-into-december-advance` | igual | igual con `MonthAdvance` 16 000/16 000/0 (lo retenido por Siigo para 2026-12) | salud y pensión 44 000; neto 1 103 335; lo demás igual |

- [ ] **Step 1: Pruebas.** Escribir los once archivos con la entrada completa y los `expected.lines` en el orden del motor (la tabla
  da los montos que el spec fija o que se derivan sin ambigüedad; lo marcado «a mano» se cuenta en `notes` antes de correr). Más:
  `PayrollEngineTests.EngineVersionIs2026_11_5` (`Assert.Equal("2026.11.5", PayrollEngine.Version)`).
- [ ] **Step 2: RED.** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~GoldenCaseTests|FullyQualifiedName~EngineVersionIs2026_11_5"` →
  falla `EngineVersionIs2026_11_5` (`Expected: 2026.11.5 Actual: 2026.11.4`). Si un caso de oro falla, se rehace la cuenta a mano
  antes de tocar el motor: una diferencia entre la cuenta y el motor se discute contra el spec, no se acomoda.
- [ ] **Step 3: Implementar.** `PayrollEngine.Version = "2026.11.5"` (R-26) y las pruebas que fijaban la versión vieja.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4` → `Passed!`
  (los 29 de hoy + los 11 nuevos).
- [ ] **Step 5: Commit** — `test(payroll): golden cases for enjoyed vacations and engine 2026.11.5`

**Fin del Bloque A:** la unitaria completa una vez, revisión del bloque. Nota de verificación para la Tarea 15 del plan base: los
casos `vacation-crosses-month-*` y `vacation-advance-*` son los que se comparan contra la exportación real de Siigo (llamadas 2 y
5); una diferencia es un RED, no un redondeo aceptado.

---

## Bloque B — Datos y lecturas (Tareas B1–B4)

Un implementador. Al terminar: `PayrollHrDirectoryTests`, `PayrollPersistenceTests` y `PayrollVacationPlannerTests` enteras (una
a la vez), `Bootstrapper.UnitTests --filter PayrollHolidayCalendarTests`, `ArchitectureTests --filter PayrollLayerTests`, y la
revisión del bloque.

### Task B1: Días de descanso por el puerto de Hr y festivos por puerto propio

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Application/IPayrollHrDirectory.cs` (`HrEmployeeSource` gana, al final,
  `IReadOnlyList<DayOfWeek> RestDays`), `src/Bootstrapper/PayrollHrDirectory.cs` (`employee.RestDays`)
- Create: `src/Modules/Payroll/Modules.Payroll.Application/IPayrollHolidayCalendar.cs`,
  `src/Modules/Payroll/Modules.Payroll.Application/VacationBusinessDays.cs`, `src/Bootstrapper/PayrollHolidayCalendar.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro junto a `IPayrollHrDirectory`)
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/VacationBusinessDaysTests.cs`,
  `tests/Bootstrapper/Bootstrapper.UnitTests/PayrollHolidayCalendarTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollHrDirectoryTests.cs`, `PayrollApiSeeds.cs` (`HireBody` gana
  `string[]? restDays = null` → `["restDays"]`), y los tres helpers que construyen `HrEmployeeSource`
  (`EngineInputFactoryTests`, `InputsImportRowParserTests`, `PayrollEligibilityTests`) pasan `[DayOfWeek.Sunday]`.

**Interfaces:**
- Produces:
  ```csharp
  /// Colombian national holidays, always (payroll is Colombian law): adapter in Bootstrapper over ColombianHolidays.
  public interface IPayrollHolidayCalendar
  {
      Task<IReadOnlySet<DateOnly>> HolidaysAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
  }
  public static class VacationBusinessDays
  {
      public static bool IsBusinessDay(DateOnly day, IReadOnlyCollection<DayOfWeek> restDays, IReadOnlySet<DateOnly> holidays);
      public static int Count(DateOnly start, DateOnly end, IReadOnlyCollection<DayOfWeek> restDays, IReadOnlySet<DateOnly> holidays);
      /// First business day after end; holidays must cover at least end+1 .. end+31.
      public static DateOnly ReturnDate(DateOnly end, IReadOnlyCollection<DayOfWeek> restDays, IReadOnlySet<DateOnly> holidays);
  }
  ```

- [ ] **Step 1: Pruebas.**
  - `VacationBusinessDaysTests.FirstExample`: 2026-12-14..2027-01-05, descanso sábado y domingo, festivos {2026-12-25, 2027-01-01}
    → 15; regreso 2027-01-06.
  - `VacationBusinessDaysTests.SecondExample`: 2027-01-12..2027-02-01, sábado y domingo, festivos {2027-01-11} → 15; regreso
    2027-02-02.
  - `VacationBusinessDaysTests.SaturdayIsBusinessWithHrsDefault`: 2026-12-14..2026-12-19 con sólo domingo → 6; con sábado y domingo → 5.
  - `PayrollHolidayCalendarTests.ColombianHolidaysAcrossTheYear`: 2026-12-01..2027-01-31 contiene 2026-12-08, 2026-12-25,
    2027-01-01 y 2027-01-11, y no 2027-01-06.
  - `PayrollHrDirectoryTests.MapsAnEmployeeHiredThroughHr` suma `Assert.Equal([DayOfWeek.Sunday], employee.RestDays)`; nueva
    `CarriesTheRestDays` con `HireBody(setup, restDays: ["Saturday", "Sunday"])` → `[Saturday, Sunday]`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests` → `error CS0103: The name 'VacationBusinessDays' does not exist`;
  `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → `error CS0246: … 'PayrollHolidayCalendar' could not be found`.
- [ ] **Step 3: Implementar.** El adaptador une `ColombianHolidays.For(year)` de cada año del rango (calco de `SchedulingHolidays`,
  sin el país). Registro `services.AddScoped<IPayrollHolidayCalendar, PayrollHolidayCalendar>()`.
- [ ] **Step 4: GREEN.** Unitarias filtradas (`VacationBusinessDaysTests`, `EngineInputFactoryTests`, `PayrollEligibilityTests`,
  `InputsImportRowParserTests`) y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PayrollHolidayCalendarTests"`;
  luego `Get-Process…` y la integración `--filter "FullyQualifiedName~PayrollHrDirectoryTests.CarriesTheRestDays|FullyQualifiedName~PayrollHrDirectoryTests.MapsAnEmployeeHiredThroughHr"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): read rest days and Colombian holidays through ports`

### Task B2: El agregado `Vacation`, sus códigos y el bloqueo nuevo

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Domain/Vacation.cs`, `VacationId.cs`, `ExternalVacationMonth.cs`
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/PayrollErrorCodes.cs` (ocho constantes; `VacationOverlap` en `Conflicts`),
  `BlockerReason.cs` (`VacationOutsideContract`), `src/Modules/Payroll/Modules.Payroll.Application/PayrollLabels.cs` (su
  etiqueta), `PayrollNotFound.cs` (`Vacation(Guid id)`), `PayrollAuditActions.cs` (tres acciones y `Resources.Vacation`)
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/VacationTests.cs`, `PayrollLabelsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct VacationId(Guid Value) { public static VacationId New(); public override string ToString(); }
  /// Month is the first day of the month; absent advances are 0, an absent VacationPay stays null (spec «Origen externo»).
  public sealed record ExternalVacationMonth(DateOnly Month, decimal? VacationPay, decimal HealthAdvance, decimal PensionAdvance, decimal SolidarityFundAdvance);
  public sealed class Vacation
  {
      public VacationId Id; public Guid TenantId, CompanyId, EmployeeId; public VacationOrigin Origin;
      public DateOnly StartDate, EndDate, ReturnDate; public int BusinessDays; public decimal? PreviousMonthIbc;
      public IReadOnlyList<ExternalVacationMonth> ExternalMonths; public long Version; public DateTimeOffset CreatedAt, UpdatedAt;
      public static Vacation Create(VacationId id, Guid tenantId, Guid companyId, Guid employeeId, VacationOrigin origin,
          DateOnly startDate, DateOnly endDate, int businessDays, DateOnly returnDate, decimal? previousMonthIbc,
          IReadOnlyList<ExternalVacationMonth> externalMonths, DateTimeOffset now);
      public void Update(VacationOrigin origin, DateOnly startDate, DateOnly endDate, int businessDays, DateOnly returnDate,
          decimal? previousMonthIbc, IReadOnlyList<ExternalVacationMonth> externalMonths, DateTimeOffset now);   // Version++
      public bool Overlaps(DateOnly start, DateOnly end);
  }
  // PayrollAuditActions: VacationCreated = "payroll.vacation.created", VacationUpdated, VacationDeleted; Resources.Vacation = "vacation"
  ```
  Las invariantes del agregado son `ArgumentException` (el validador ya dio el campo): `EndDate ≥ StartDate`; `External` exige
  `PreviousMonthIbc` y `BusinessDays ≥ 1`; `Qep` no lleva `ExternalMonths`.

- [ ] **Step 1: Pruebas** (`VacationTests`): `CreateKeepsWhatItWasGiven` (versión 1), `UpdateBumpsTheVersion`, `QepCannotCarryExternalMonths`,
  `ExternalNeedsThePreviousIbc`, `OverlapsIsInclusive` (2026-12-14..2027-01-05 solapa 2027-01-05..01-10 y no 2027-01-06..); y
  `PayrollLabelsTests` toma `VacationOutsideContract` solo (la teoría recorre los miembros).
- [ ] **Step 2: RED.** build → `error CS0246: … 'Vacation' could not be found`.
- [ ] **Step 3: Implementar** (calco de `RecurringConcept` para versión y fechas).
- [ ] **Step 4: GREEN.** `--filter "FullyQualifiedName~VacationTests|FullyQualifiedName~PayrollLabelsTests"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): add the vacation aggregate and its error codes`

### Task B3: Persistencia: tabla, restricción, foto en el resultado

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Application/IVacationRepository.cs`,
  `src/Modules/Payroll/Modules.Payroll.Infrastructure/Persistence/VacationRepository.cs`, la migración `AddVacations`
- Modify: `Persistence/PayrollDbContext.cs` (`DbSet<Vacation> Vacations`, `ConfigureVacation`), `Persistence/PayrollUnitOfWork.cs`,
  `Persistence/EmployeeResultRow.cs`, `PayrollInfrastructureExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollPersistenceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IVacationRepository
  {
      Task<Vacation?> FindAsync(Guid tenantId, VacationId id, CancellationToken cancellationToken);           // tracked
      Task<IReadOnlyList<Vacation>> ListByEmployeeAsync(Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken);
      /// Company-wide, no tracking: start ≤ to and end ≥ from.
      Task<IReadOnlyList<Vacation>> ListOverlappingAsync(Guid tenantId, Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
      void Add(Vacation vacation);
      void Remove(Vacation vacation);
  }
  ```
  Tabla `payroll.vacations`: `id uuid pk`, `tenant_id`, `company_id`, `employee_id`, `origin varchar(16)` (nombre del enum),
  `start_date date`, `end_date date`, `business_days int`, `return_date date`, `previous_month_ibc numeric(18,2) null`,
  `external_months jsonb not null default '[]'`, `version bigint` (token de concurrencia), `created_at`, `updated_at`; índice
  `IX_vacations_tenant_company_employee_start` `(tenant_id, company_id, employee_id, start_date)`; FK a `companies.companies` como
  `payroll.periods`. La migración hace `CREATE EXTENSION IF NOT EXISTS btree_gist;` (Payroll migra **antes** que Scheduling en
  `Program.cs`) y agrega a mano `CONSTRAINT "EX_vacations_no_overlap" EXCLUDE USING gist (tenant_id WITH =, company_id WITH =,
  employee_id WITH =, daterange(start_date, end_date, '[]') WITH &&)`. `payroll.employee_results` gana
  `vacation_days int not null default 0` y `vacation_photos jsonb not null default '[]'`. Todo aditivo (expand/contract):
  pods viejos siguen funcionando.
  `EmployeeResultRow` gana `VacationDays` y `VacationPhotosJson` (serializa `liquidation.VacationPhotos` con `PayrollJson.Options`).

- [ ] **Step 1: Pruebas** (`PayrollPersistenceTests`): `AVacationRoundTripsWithItsExternalMonths`;
  `TwoOverlappingVacationsOfTheSameEmployeeConflict` (dos `Add` en dos unidades de trabajo → `ResourceConflictException` con
  `Code == "payroll.vacation.overlap"`); `ADifferentEmployeeDoesNotConflict`; `AResultKeepsItsVacationDaysAndPhotos`; y
  `AResultSavedBeforeVacationsReadsZeroAndNoPhotos` (fila con `vacation_photos` `'[]'` y resumen sin `vacationPay` → 0).
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Payroll/Modules.Payroll.IntegrationTests` → `error CS0246: … 'IVacationRepository' could not be found`.
- [ ] **Step 3: Implementar.** Generar la migración con
  `dotnet ef migrations add AddVacations --project src/Modules/Payroll/Modules.Payroll.Infrastructure --context PayrollDbContext -o Persistence/Migrations`
  y agregar a mano la extensión y la restricción (como `20261121210113_InitialScheduling.cs`). `PayrollUnitOfWork`:
  `catch (DbUpdateException e) when (IsExclusion(e, "EX_vacations_no_overlap"))` → `ResourceConflictException(PayrollErrorCodes.VacationOverlap, "Se cruzan con otras vacaciones de este empleado.", e)`
  (agregar `IsExclusion` a `UniqueViolation.cs` de Payroll, calco de Scheduling).
- [ ] **Step 4: GREEN.** `Get-Process…`; `--filter "FullyQualifiedName~PayrollPersistenceTests.AVacation|FullyQualifiedName~PayrollPersistenceTests.TwoOverlapping|FullyQualifiedName~PayrollPersistenceTests.ADifferentEmployee|FullyQualifiedName~PayrollPersistenceTests.AResult"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): persist vacations and the vacation photo of each result`

### Task B4: El planificador arma `VacationInput` (y el bloqueo por contrato)

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Application/VacationReach.cs`, `VacationInputBuilder.cs`
- Modify: `LiquidationPlanner.cs`, `EngineInputFactory.cs`, `PayrollEligibility.cs`, `PeriodComposer.cs`, `LiquidatePeriod.cs`
  (`ToResult` no cambia: la foto viaja en `Liquidation`), `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/VacationInputBuilderTests.cs`, `VacationReachTests.cs`,
  `EngineInputFactoryTests.cs`, `PayrollEligibilityTests.cs`; `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollVacationPlannerTests.cs`

**Interfaces:**
- Consumes: `IVacationRepository` (B3), `VacationCalendar` (A1), `VacationInput`/`VacationPhoto` (A2/A3).
- Produces:
  ```csharp
  public static class VacationReach
  {
      /// Quincena that pays a Qep vacation: the one containing StartDate (exists or not). Null for External.
      public static (DateOnly Start, DateOnly End)? PayingQuincena(Vacation vacation);
      /// Is this vacation part of the period's EngineInput: pays here, or has days between the 1st of the month and period.End.
      public static bool IsInputOf(Vacation vacation, DateOnly periodStart, DateOnly periodEnd);
      /// Start dates of every quincena whose input includes it (paying one, those with days, and the second half of a month whose first half has days).
      public static IReadOnlyList<DateOnly> QuincenaStarts(Vacation vacation);
  }
  public sealed record UnpaidVacation(Guid EmployeeId, Guid VacationId, DateOnly PaidInStart, DateOnly PaidInEnd);
  public sealed record VacationPlan(IReadOnlyList<VacationInput> Inputs, IReadOnlyList<UnpaidVacation> Unpaid);
  public sealed class VacationInputBuilder(IPayrollPeriodRepository periods, IPayrollResultStore results)
  {
      public Task<VacationPlan> BuildAsync(PayrollPeriod period, HrEmployeeSource employee, HrContractSource contract,
          IReadOnlyList<Vacation> vacations, IReadOnlyDictionary<Guid, VacationPhoto> photoOverrides, CancellationToken cancellationToken);
  }
  public sealed record VacationListOverride(Guid EmployeeId, IReadOnlyList<Vacation> Vacations);
  public sealed record PlanOverrides(VacationListOverride? Vacations, IReadOnlyDictionary<Guid, VacationPhoto> Photos)
  { public static PlanOverrides None { get; } }
  // LiquidationPlanner.PlanAsync(PayrollPeriod, CancellationToken) => PlanAsync(period, PlanOverrides.None, ct)
  // LiquidationPlanner.PlanAsync(PayrollPeriod, PlanOverrides, CancellationToken)
  // LiquidationPlan gains, last: IReadOnlyList<UnpaidVacation> Unpaid
  // EngineInputFactory.Build(..., MonthAccumulators prior, IReadOnlyList<VacationInput> vacations)
  // PayrollEligibility.WithVacations(EligibilityResult result, IEnumerable<Vacation> employeeVacations): Blocked + VacationOutsideContract
  //   when a vacation starts before contract.StartDate or ends after contract.EndedOn; an Excluded result is returned as is.
  ```

Reglas de `VacationInputBuilder` por vacación (sólo las `IsInputOf`, ordenadas por `StartDate` y luego `Id`):
- Fechas y días hábiles (Review Focus 4): `PaidHere` lleva `StartDate`, `EndDate` y `BusinessDays` completos. Las demás llevan
  `StartDate = máx(StartDate, día 1 del mes)`, `EndDate = mín(EndDate, period.EndDate)` y `BusinessDays = 0`; una `External` con
  `vacationPay` tecleado para el mes recorta el fin al último día del mes (el valor por día divide por los días del mes entero).
  `VacationCalendar` da los mismos días de nómina con las fechas recortadas.
- `SalaryAtStart` y `SalaryRecordDate`: el registro de salario del contrato vigente el `StartDate` real.
- `PaidHere` (`Qep` y `PayingQuincena.Start == period.StartDate`): IBC anterior, en orden (spec «IBC anterior al inicio»):
  1. las dos quincenas del mes anterior al mes del inicio existen, no están en `Draft` y tienen resultado del empleado → el
     `Amount` de la línea `ibc` del resultado de la segunda, `VacationIbcSource.Qep`;
  2. si no, `Vacation.PreviousMonthIbc` → `Typed`;
  3. si no, el salario vigente el día anterior al inicio → `Salary`.
  `MonthAdvance = Zero`, `MonthVacationPay = null`.
- `Qep` pagada en otra quincena: la foto es `photoOverrides[id]` si viene, si no la `VacationPhotos` del resultado del empleado en
  el período de `PayingQuincena`. `SalaryAtStart`, `SalaryRecordDate`, IBC anterior y su fuente salen de la foto; `MonthAdvance` =
  el `VacationMonthAdvance` del mes del período (o `Zero`). Sin período, sin resultado o sin foto → entrada con la foto en cero y
  un `UnpaidVacation`. «Pagada» además exige período `Liquidated|Approved|Closed` y `!Outdated`: si no, también `UnpaidVacation`
  (el `SourceChanged` se mira en C3, no aquí: así el planificador nunca se llama a sí mismo).
- `External`: IBC anterior = `PreviousMonthIbc` (`Typed`); `MonthVacationPay` y `MonthAdvance` = lo tecleado para el mes del
  período (`Zero`/`null` si no hay fila).

- [ ] **Step 1: Pruebas.**
  - `VacationReachTests`: `IsInputOf` de 2026-12-20..2027-01-05 es falso para 2026-12-01..15 y verdadero para 2026-12-16..31 y
    2027-01-01..15; de 2026-12-01..10 es verdadero para 2026-12-16..31 (la segunda cuenta los días de la primera);
    `QuincenaStarts(2026-12-14..2027-01-05)` = `[2026-12-01, 2026-12-16, 2027-01-01, 2027-01-16]`.
  - `VacationInputBuilderTests` (repositorios falsos en memoria dentro del archivo): `PreviousIbcFromQepWhenBothHalvesHaveResults`
    (ibc de la segunda de noviembre 2 800 000 → `Qep`); `PreviousIbcTypedWhenAHalfIsMissing`; `PreviousIbcFallsBackToTheSalary`
    (aumento el día del inicio: usa el del día anterior); `PaidElsewhereReadsThePhotoOfItsMonth` (enero lee 20 000/20 000);
    `PaidElsewhereWithoutAPhotoIsUnpaid`; `AnOutdatedPayingPeriodIsUnpaid`; `ExternalUsesTheTypedMonth`; `PhotoOverrideWins`;
    `ANonPayingQuincenaGetsClippedDates` (2026-12-14..2027-01-05 en 2027-01-01..15 llega como 2027-01-01..05 con
    `BusinessDays` 0, y mover el fin a 2027-01-08 da otra entrada para enero pero la misma para 2026-12-16..31).
  - `PayrollEligibilityTests.AVacationAfterTheContractEndBlocks` → `Blocked` con `[VacationOutsideContract]`.
  - `EngineInputFactoryTests.VacationsTravelInTheInput`.
  - `PayrollVacationPlannerTests` (integración, clase nueva con `PayrollApiFixture`; las pruebas de integración de B y C2 se
    quedan en 2026 porque liquidar 2027 exige cargar sus valores legales anuales, ver C3): `PlansTheVacationOfTheMonth` (alta por
    Hr con sábado y domingo, vacación `Qep` 2026-12-14..2026-12-24 guardada por el repositorio, período 2026-12-01..15 abierto por
    `ensure-current` con el reloj en 2026-12-02 → la entrada del empleado lleva la vacación con `PaidHere`) y
    `LiquidatingSavesThePhoto` (liquidar por HTTP → el resultado guardado tiene `VacationDays` 2 y una foto con `Quincenas`
    (2, 9) y `Months` = `[(2026-12-01, 900 000, 36 000, 36 000, 0)]` para un salario de 3 000 000).
- [ ] **Step 2: RED.** build → `error CS0103: The name 'VacationReach' does not exist`.
- [ ] **Step 3: Implementar.** `LiquidationPlanner` lee `vacations.ListOverlappingAsync(tenant, company, primer día del mes del
  período, period.EndDate)` una vez, agrupa por empleado (aplicando `PlanOverrides.Vacations` al suyo), aplica
  `PayrollEligibility.WithVacations` antes de armar la entrada, y junta los `Unpaid`. `PeriodComposer.BuildViewAsync` también
  aplica `WithVacations` (así el bloqueado aparece en `blocked`). Registro en DI de `VacationInputBuilder`.
- [ ] **Step 4: GREEN.** Unitarias filtradas de las clases de arriba; `Get-Process…`;
  `--filter "FullyQualifiedName~PayrollVacationPlannerTests"` → `Passed!`; luego `PayrollLiquidationApiTests` entera (la
  liquidación de hoy no cambia).
- [ ] **Step 5: Commit** — `feat(payroll): plan each period with the vacations of its month`

**Fin del Bloque B:** las clases `PayrollHrDirectoryTests`, `PayrollPersistenceTests` y `PayrollVacationPlannerTests` enteras,
una por vez; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~PayrollLayerTests"`; revisión del
bloque.

---

## Bloque C — Casos de uso y HTTP (Tareas C1–C5)

Un implementador. Al terminar: los cuatro proyectos de prueba completos (uno a la vez) y la revisión fuerte de toda la rama.

### Task C1: Leer y previsualizar (`GET` y `POST …/preview`)

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Application/VacationDtos.cs` (los DTOs y requests de «Contrato HTTP»),
  `VacationValidators.cs`, `VacationRules.cs`, `VacationComposer.cs`, `ListVacations.cs`, `PreviewVacation.cs`
- Modify: `src/Modules/Payroll/Modules.Payroll.Api/PayrollEndpoints.cs` (`MapVacations`), `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/VacationValidatorsTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollVacationsApiTests.cs`

**Interfaces:**
- Consumes: B1–B4.
- Produces:
  ```csharp
  /// Everything a write or a preview checks about the dates, shared by preview, create and update.
  public sealed record VacationFacts(HrEmployeeSource Employee, HrContractSource Contract, int ComputedBusinessDays, DateOnly ReturnDate,
      IReadOnlyList<VacationQuincenaDays> Quincenas, IReadOnlyList<string> Months);
  public sealed class VacationRules(IPayrollHrDirectory hr, IPayrollHolidayCalendar holidays, IPayrollPeriodRepository periods,
      IPayrollLegalValues legalValues, PayrollCompanies companies, PayrollEmployees employees)
  {
      /// Order: company exists and is active, employee in the company, contract in force on start covers end, first period,
      /// Qep only: start is a business day, ≥ 1 business day, employee liquidable in the paying quincena.
      public Task<VacationFacts> CheckAsync(Guid tenantId, Guid companyId, Guid employeeId, VacationOrigin origin,
          DateOnly start, DateOnly end, CancellationToken cancellationToken);
  }
  public sealed class VacationComposer(IVacationRepository vacations, IPayrollPeriodRepository periods)
  {
      public Task<IReadOnlyList<VacationDto>> ListAsync(Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken);
      /// Those that touch the period: pay in it or have days in it.
      public Task<IReadOnlyList<VacationDto>> TouchingAsync(PayrollPeriod period, Guid employeeId, CancellationToken cancellationToken);
  }
  public sealed record ListVacationsQuery(Guid TenantId, Guid CompanyId, Guid EmployeeId) : IQuery<IReadOnlyList<VacationDto>>;
  public sealed record PreviewVacationQuery(Guid TenantId, Guid CompanyId, Guid EmployeeId, VacationPreviewRequest Request) : IQuery<VacationPreviewDto>;
  ```
  `CheckAsync`: empresa inactiva → `payroll.company.inactive` («An inactive company cannot change its vacations.»); el empleado no
  tiene contrato con la empresa → `payroll.employee.not_found`; sin contrato vigente el día del inicio o que no cubre el fin →
  `outside_contract`; primera quincena de la empresa = el menor `StartDate` de `periods.ListAsync` (sin períodos, la regla no
  aplica); `Qep` con inicio antes de ella o `External` con fin antes de ella → `before_first_period`; festivos de `start` a
  `end + 31`; `Qep`: inicio no hábil → `starts_on_rest_day`, 0 hábiles → `no_business_days`, y `PayrollEligibility.Evaluate` del
  empleado en la quincena que paga (SMMLV a su inicio) `Excluded` → `payroll.employee.not_liquidable`.

- [ ] **Step 1: Pruebas.**
  - `VacationValidatorsTests`: cada regla del validador de «Contrato HTTP» con su clave (`origin`, `startDate`, `endDate`,
    `previousMonthIbc`, `businessDays`, `externalMonths[0].month`, `externalMonths[1].healthAdvance`), y `QepRejectsBusinessDays`.
  - `PayrollVacationsApiTests` (integración): `PreviewCountsTheBusinessDays` (sábado y domingo, 2026-12-14..2027-01-05 →
    `businessDays` 15, `returnDate` 2027-01-06, `quincenas` (2, 15, 5), `months` `["2026-12","2027-01"]`, `paidIn`
    2026-12-01..15); `PreviewOfExternalHasNoPaidIn`; `PreviewRejectsARestDayStart` (inicio sábado → 422 `starts_on_rest_day`);
    `PreviewRejectsOutsideTheContract` (contrato con fin 2026-12-31 → 422 `outside_contract`); `PreviewRejectsBeforeTheFirstPeriod`;
    `ListIsEmptyAtFirst`; `ReadingNeedsPeriodRead` (sin permiso → 403); `AnotherTenantsEmployeeIs404`.
- [ ] **Step 2: RED.** build de las dos → `error CS0246: … 'VacationRequest' could not be found`.
- [ ] **Step 3: Implementar.** `MapVacations` calca `MapRecurring` (políticas de la tabla; `ProducesProblem` 403/404/409/412/422/428
  según la ruta). `VacationComposer`: `Quincenas` con `VacationCalendar`, `PaidIn` con `VacationReach.PayingQuincena` +
  `periods.FindByStartAsync`, `Can` = ninguna de `VacationReach.QuincenaStarts` está `Approved|Closed`. Orden de la lista:
  `StartDate` descendente y luego `Id`.
- [ ] **Step 4: GREEN.** Unitaria filtrada; `Get-Process…`; integración `--filter "FullyQualifiedName~PayrollVacationsApiTests.Preview|FullyQualifiedName~PayrollVacationsApiTests.List|FullyQualifiedName~PayrollVacationsApiTests.Reading|FullyQualifiedName~PayrollVacationsApiTests.AnotherTenants"` → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): list and preview an employee's vacations`

### Task C2: Registrar, editar y borrar, con bloqueo por huella y desactualizado

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Application/SaveVacation.cs` (crear y editar), `DeleteVacation.cs`, `VacationLocks.cs`
- Modify: `PayrollEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollVacationsApiTests.cs`

**Interfaces:**
- Consumes: `VacationRules`, `VacationComposer` (C1), `LiquidationPlanner.PlanAsync(period, PlanOverrides, ct)` (B4).
- Produces:
  ```csharp
  public sealed record CreateVacationCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, VacationRequest Request) : ICommand<IReadOnlyList<VacationDto>>;
  public sealed record UpdateVacationCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, Guid VacationId, VacationRequest Request, string? IfMatch) : ICommand<IReadOnlyList<VacationDto>>;
  public sealed record DeleteVacationCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, Guid VacationId, string? IfMatch) : ICommand<IReadOnlyList<VacationDto>>;
  public sealed class VacationLocks(IPayrollPeriodRepository periods, IPayrollResultStore results, LiquidationPlanner planner)
  {
      /// Rule 1: for every Approved or Closed quincena the old or the new range reaches, the employee's fingerprint with the
      /// vacations as they would be must equal the stored one or today's without the change; else 422 payroll.period.not_editable.
      public Task EnsureUnlockedAsync(Guid tenantId, Guid companyId, Guid employeeId, IReadOnlyList<Vacation> before,
          IReadOnlyList<Vacation> after, CancellationToken cancellationToken);
      /// Rule 2: MarkOutdated on every reached quincena that is Liquidated or Reopened (tracked); Draft is a no-op, Approved and Closed are skipped.
      public Task MarkReachedOutdatedAsync(Guid tenantId, Guid companyId, IEnumerable<Vacation> touched, PayrollActor actor, DateTimeOffset now, CancellationToken cancellationToken);
  }
  ```

Orden del handler (crear): autorizar `PeriodRead` + `PeriodManage`; validar; `VacationRules.CheckAsync`; `businessDays` = el
calculado (`Qep`) o el tecleado (`External`); solapamiento con las del empleado (`PayrollConflicts.Run` → 409); armar el
`Vacation` nuevo sin guardar; `VacationLocks.EnsureUnlockedAsync(before: las de hoy, after: las de hoy + la nueva)`; `Add`;
`MarkReachedOutdatedAsync`; auditoría (`created`, todos los campos); un `SaveChanges`; `VacationComposer.ListAsync`. Editar:
igual con `If-Match` (`PayrollIfMatch.Require`, luego `PayrollSupport.EnsureVersion`), el id debe ser de esa empresa y empleado
(si no, `PayrollNotFound.Vacation`), solapamiento ignorando la propia, `before`/`after` con la vieja y la nueva, desactualiza lo
que alcanzan las dos, auditoría `updated` con `PayrollSupport.Diff` de los seis campos. Borrar: `If-Match`, bloqueo con `after`
sin ella, desactualiza, auditoría `deleted` (`[]` de campos). El detalle de `not_editable` es «Reabre la quincena para cambiar estas
vacaciones».

- [ ] **Step 1: Pruebas** (`PayrollVacationsApiTests`):
  - `CreateAnswersTheWholeList` (201, una `VacationDto` con `businessDays` 15, `paidIn.status` `Draft`, `can` verdaderos, auditoría
    `payroll.vacation.created` contada con `CountAuditEntriesAsync`).
  - `ExternalKeepsWhatWasTyped` (`businessDays` 9 tecleado aunque QEP cuente 10, `externalMonths` devuelto con `vacationPay` nulo y
    anticipos 0 donde no se tecleó).
  - `OverlapIs409`; `UpdateNeedsIfMatch` (428) y `UpdateWithAStaleVersionIs412`; `UpdateOfAnotherEmployeesVacationIs404`
    (`payroll.vacation.not_found`); `DeleteAnswersTheRemainingList`.
  - `ALiquidatedQuincenaItReachesGetsOutdated` (liquidar 2026-12-01..15, crear vacaciones que la tocan → el período queda
    `outdated` y sube su versión) y `ADraftIsNotTouched` (versión igual).
  - Todo en diciembre de 2026 (ver B4). `AnApprovedPayingQuincenaBlocksMovingTheEnd` (`Qep` 2026-12-14..24, liquidar y aprobar la
    2026-12-01..15; mover el fin al 26 cambia `vacation_pay` → 422 `payroll.period.not_editable` con ese detalle).
  - `AChangeThatDoesNotReachTheApprovedOnePasses` (el ejemplo del spec, con `External` 2026-12-10..20: aprobada la 2026-12-01..15,
    mover el fin al 22, dentro de la segunda no aprobada → 200; mover el inicio al 11 → 422).
  - `AnApprovedOneThatHrMovedDoesNotBlockAHarmlessEdit` (Review Focus 2: con la de arriba aprobada, un registro de salario nuevo de
    Hr con fecha 2026-12-05 mueve su huella; mover el fin al 22 → 200).
  - `AnInactiveCompanyCannotRegister` (422 `payroll.company.inactive`).
- [ ] **Step 2: RED.** build de integración → `error CS0246: … 'CreateVacationCommand' could not be found`.
- [ ] **Step 3: Implementar** según el orden de arriba.
- [ ] **Step 4: GREEN.** `Get-Process…`; integración filtrada por cada prueba nueva (de a grupos con `|`) → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): register, edit and delete vacations behind the fingerprint lock`

### Task C3: Liquidar con vacaciones de otra quincena y volver a liquidar la que paga

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Application/VacationPayments.cs`
- Modify: `LiquidatePeriod.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollVacationLiquidationApiTests.cs`

**Interfaces:**
- Consumes: `LiquidationPlan.Unpaid` (B4), `VacationLocks` (C2), `VacationReach`.
- Produces:
  ```csharp
  public sealed class VacationPayments(IPayrollPeriodRepository periods, LiquidationPlanner planner)
  {
      /// Rule 5: every vacation of the plan paid elsewhere is paid (Liquidated, Approved or Closed, not Outdated, and a Liquidated
      /// one without SourceChanged). Else 422 payroll.vacation.not_paid_yet naming the paying quincena.
      public Task EnsurePaidAsync(LiquidationPlan plan, Guid tenantId, Guid companyId, CancellationToken cancellationToken);
      /// Rule 4: with the new photos, no Approved or Closed later quincena changes (same comparison as VacationLocks); else 422
      /// payroll.vacation.paid_period_locked naming it. Then MarkOutdated on the later Liquidated or Reopened ones.
      public Task ApplyNewPhotosAsync(PayrollPeriod paying, IReadOnlyList<EmployeeResult> results, PayrollActor actor, DateTimeOffset now, CancellationToken cancellationToken);
  }
  ```
  En `LiquidatePeriodHandler`, `EnsurePaidAsync` va después del plan y de los bloqueados; `ApplyNewPhotosAsync` después de
  `ReplaceAllAsync` y antes del `SaveChanges` (todo o nada). El detalle de los dos 422 nombra la quincena
  («La quincena del 1 al 15 de diciembre de 2026…»). La regla R-14 de hoy (la segunda del mes) no cambia.

- [ ] **Step 1: Pruebas** (`PayrollVacationLiquidationApiTests`, empleado de 3 000 000 con sábado y domingo, vacaciones
  2026-12-14..2027-01-05, reloj movido por `Fixture.Clock`). La clase usa una fixture con `OperatorTenant` (calco de
  `LegalParameterUsageApiTests`) y carga por la ruta del operador los valores anuales de 2027 que exige `PayrollLegalValues`
  (`Smmlv` 1 750 905, `TransportAllowance` 249 095 y los tramos del FSP, `effectiveFrom` 2027-01-01, sintéticos como en A6):
  sin ellos enero responde `payroll.legal_parameter.missing`.
  - `TheSecondHalfWaitsForThePayingOne` (liquidar 2026-12-16..31 sin la primera liquidada → 422 `first_half_not_liquidated`, la de
    hoy; con la primera liquidada y luego desactualizada por una edición → 422 `payroll.vacation.not_paid_yet`).
  - `JanuaryNeedsDecemberPaid` (2027-01-01..15 con diciembre en `Draft` → `not_paid_yet`).
  - `TheWholeExampleClosesTheMonth`: liquidar las tres quincenas en orden y comprobar los montos de los casos de oro
    `vacation-crosses-month-paid-first-half-1..2` en el detalle del empleado (`salary`, `vacation_pay:{id}`, `health_employee`,
    anticipos) — la integración confirma que el planificador arma lo que el caso de oro supone.
  - `ReliquidatingThePayingOneOutdatesTheFollowing` (las siguientes Liquidadas quedan `outdated`).
  - `ReliquidatingThePayingOneIsLockedByAnApprovedFollowing` (aprobar la segunda de diciembre, reabrir la primera, cambiar el
    salario de Hr para que la foto cambie, volver a liquidar → 422 `paid_period_locked`); `…ButPassesWhenThePhotoIsTheSame`.
  - `ExternalDoesNotWaitForAnyPayment` (External 2026-11-23..2026-12-04 → la primera de diciembre liquida sin `vacation_pay` y
    resta el anticipo tecleado).
- [ ] **Step 2: RED.** build → `error CS0246: … 'VacationPayments' could not be found`.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN.** `Get-Process…`; `--filter "FullyQualifiedName~PayrollVacationLiquidationApiTests.<Prueba>"` una a una → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): liquidate quincenas whose vacations are paid in another one`

### Task C4: DTOs de período y empleado, Excel

**Files:**
- Modify: `PayrollDtos.cs` (`PayrollTotalsDto.VacationPay`, `PayrollEmployeeRowDto.VacationDays`, `PayrollEmployeeDetailDto.Vacations`,
  `PayrollPeriodDetailDto.Vacations`), `PeriodComposer.cs` (inyecta `VacationComposer` e `IVacationRepository`),
  `Excel/PayrollColumnLayouts.cs`, `Excel/PayrollWorkbook.cs` (`PayrollWorkbookRow` gana `int VacationDays` después de
  `WorkedDays`), `Excel/ExportPeriod.cs`
- Test: `tests/Modules/Payroll/Modules.Payroll.UnitTests/PayrollColumnLayoutTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollVacationsApiTests.cs`, `PayrollExportApiTests.cs`

**Interfaces:**
- Consumes: `VacationComposer.TouchingAsync` (C1), `EmployeeLiquidation.VacationDays` (A2).
- Produces: los cuatro cambios de DTO de «Contrato HTTP». Excel «Resumen»: «Días de vacaciones» (`Integer`) después de «Días» y
  «Vacaciones disfrutadas» (`Money`, `summary.VacationPay`) después de «Salario»: 31 columnas, 24 de dinero. «Total devengado»,
  «Salud», «Pensión» y «FSP» ya la/los incluyen por el resumen. La provisión «Vacaciones» no cambia de nombre.

- [ ] **Step 1: Pruebas.**
  - `PayrollColumnLayoutTests`: `Titles` con las dos columnas nuevas en su lugar; `TheSummaryHasTheThirtyOneColumnsInOrder`;
    `Numbered` con 24 posiciones (`VacationPay` = 2); 24 columnas de dinero; «Días de vacaciones» `Integer`.
  - `PayrollVacationsApiTests.ThePeriodDetailListsTheVacations` (`Qep` 2026-12-14..24: `vacations[0]` con `fullName`, fechas y
    `days` 2 en la 2026-12-01..15) y `TheEmployeeDetailCarriesThemAndTheRowItsDays` (tras liquidar: `employee.vacationDays` 2,
    `employee.workedDays` 13, `summary.vacationPay` 1 100 000 = 3 000 000 ÷ 30 × 11, `vacations[0].paidIn.status` `Liquidated`).
  - `PayrollExportApiTests.TheExportShowsVacationDaysAndPay`.
- [ ] **Step 2: RED.** `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~PayrollColumnLayoutTests"` → falla `Assert.Equal() Failure: Expected 31, Actual 29` (o build `CS1729` por el constructor de `PayrollWorkbookRow`).
- [ ] **Step 3: Implementar.** `PayrollTotalsDto.From` pasa `summary.VacationPay`; las filas toman `VacationDays` del resultado
  (nulo sin resultado); `PeriodVacationDto.FullName` sale de la fila (o del resultado en Aprobado/Cerrado), `Days` de
  `VacationCalendar` en esta quincena.
- [ ] **Step 4: GREEN.** Unitaria filtrada; `Get-Process…`; integración filtrada por las tres pruebas → `Passed!`.
- [ ] **Step 5: Commit** — `feat(payroll): show vacation days and pay in the period, the employee and the Excel`

### Task C5: Verificación final de la rama

**Files:** ninguno nuevo (sólo arreglos que salgan de las corridas, cada uno con su prueba).

- [ ] **Step 1:** `dotnet build` de la solución entera → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- [ ] **Step 2:** `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; luego, **uno a la vez y en primer plano**:
  ```powershell
  dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests -- xUnit.MaxParallelThreads=4
  dotnet test tests/ArchitectureTests/ArchitectureTests -- xUnit.MaxParallelThreads=4
  dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests -- xUnit.MaxParallelThreads=4
  ```
  Cada uno → `Passed!` con su conteo literal en la evidencia.
- [ ] **Step 3:** `dotnet format --verify-no-changes` (sin cambios; si los hay, se corrigen **antes** de la revisión fuerte).
- [ ] **Step 4:** revisión fuerte de toda la rama (modelo más capaz) contra el spec y la sección «Review Focus».

---

## Al mergear a `develop` (no es tarea de la rama)

1. `git branch --show-current` en `develop`; merge de `feature/nomina-vacaciones` (nunca a `main`).
2. Las cuatro corridas de C5 sobre `develop` si entró algo entre medio (una sola vez, en fila).
3. En `develop`, `docs/estado.md`: la fila de nómina (vacaciones disfrutadas, motor `2026.11.5`, migración `AddVacations` aditiva)
   y el Registro, en el mismo merge o en un `docs:` inmediato.
4. Orden de despliegue: backend primero (el frontend tolera un backend viejo, pero no al revés: sin las rutas, «Registrar
   vacaciones» da 404).

## Fuera de este plan

- Recurrentes en modo proporcional: siguen sobre los días cubiertos, no sobre los trabajados (el spec no lo cambia). Si un
  recurrente proporcional no debe pagar días de vacaciones, es decisión pendiente del owner.
- Saldo de días causados, salario variable, compensadas en dinero, «Solo vacaciones», incapacidades y licencias (proyecto 2).
- La comparación contra la exportación real de Siigo es la Tarea 15 del plan base.
