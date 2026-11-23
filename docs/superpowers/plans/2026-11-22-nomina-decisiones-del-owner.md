# Nómina, proyecto 1 — decisiones del owner (2026-11-22) — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or subagent-driven-development) to implement
> this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **El `CLAUDE.md` del workspace manda sobre el proceso del skill** («Presupuesto de pruebas y ejecución de planes»): **dos
> bloques** (A y B), **un implementador por bloque**, una revisión por bloque y la revisión fuerte una sola vez al final. Cada
> tarea conserva su commit.

**Goal:** aplicar las once decisiones del owner sobre nómina: ARL sobre el IBC, consolidación mensual del auxilio y de la
exoneración, auxilio mensual al terminar el contrato en la primera quincena, huella sin nombres, conceptos archivados, control de
año de los tramos del FSP, cita del salario integral y configuración de una empresa inactiva; con el motor en `2026.11.3`.

**Architecture:** el motor sigue siendo puro (`Modules.Payroll.Domain/Engine`). Las decisiones 1–3 y 9 son cambios de reglas y
textos del motor (Bloque A). Las 4, 5, 6 y 10 son de Application y del adaptador de valores legales del Bootstrapper (Bloque B).
Sin migraciones, sin rutas, sin permisos y sin códigos de error nuevos.

**Tech Stack:** .NET 10, xUnit v3, Testcontainers (Postgres 18) sólo en GREEN de integración.

**Spec:** `docs/superpowers/specs/2026-11-22-nomina-decisiones-del-owner-design.md` (léelo entero antes de empezar). Base:
`docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md` y su plan
`docs/superpowers/plans/2026-11-21-nomina-periodo-y-motor-backend.md` («Motor: definiciones exactas»).

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\nomina-decisiones`, rama
  `feature/nomina-decisiones`. Antes de **cada** commit: `git branch --show-current` dice `feature/nomina-decisiones`, en ese
  momento. Commits: conventional commits, **sin atribución de IA** (ni `Co-Authored-By`).
- Comandos en PowerShell (`A; if ($?) { B }`, nunca `&&`). `Api.exe` corriendo bloquea el build: se avisa, no se detiene.
- **TDD:** cada tarea tiene RED y GREEN con salida literal. Un RED que es error de compilación no levanta contenedores: la
  evidencia es la salida de `dotnet build`. La integración (Testcontainers) corre **sólo en GREEN**, filtrada por nombre de
  prueba (`--filter "FullyQualifiedName~X"`), con `-- xUnit.MaxParallelThreads=4`.
- **Antes de cualquier corrida de integración:** `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. Si hay un
  `dotnet test` o `testhost` de otra sesión, se espera; no se detiene. Las pruebas corren en primer plano.
- Una prueba nueva que pasa sin cambiar código es **de caracterización** (fija un comportamiento que ya es correcto): se marca así
  en el commit y no cuenta como RED de la tarea; cada tarea tiene al menos un RED real.
- No se inventan campos, estados, rutas, permisos ni códigos: los códigos que se usan ya existen en `PayrollErrorCodes`.
- `docs/estado.md` no se toca en la rama: se actualiza en `develop`, al mergear.

## Bloques

| Bloque | Tareas | Decisiones | Pruebas |
| ------ | ------ | ---------- | ------- |
| A — Motor puro | A1–A6 | 1, 2, 3, 9 y versión | sólo `Modules.Payroll.UnitTests` |
| B — Aplicación | B1–B6 | 4, 5, 6, 10 y verificación final | unitarias + integración filtrada; al final la suite de los proyectos tocados |

Las decisiones 7, 8 y 11 no tienen tarea (el spec explica por qué no cambian nada).

Comandos que se repiten:

```powershell
# Unitarias de nómina, filtradas por clase o prueba
dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~<Clase>"
# Build de las pruebas (RED de compilación)
dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests
```

---

## Bloque A — Motor puro (Tareas A1–A6)

Un implementador. Al terminar el bloque: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`
una vez (no tiene contenedores) y la revisión del bloque.

### Task A1: Dos bases del mes; la ARL sobre el IBC (decisión 1)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/IbcRule.cs`, `EmployerContributionsRule.cs`,
  `EmployeeDeductionsRule.cs`, `SolidarityFundRule.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/IbcRuleTests.cs`, `EmployerContributionsRuleTests.cs`
- Modify (goldens): `tests/Modules/Payroll/Modules.Payroll.UnitTests/Golden/synthetic/non-salary-over-forty-percent.json`,
  `ibc-cap-and-top-bracket-second.json`

- [ ] **Step 1: Pruebas.**
  - `IbcRuleTests`: reemplazar `outcome.IbcMonth` por `outcome.SocialSecurityIbc` y `outcome.SalaryEarningsMonth` por
    `outcome.ParafiscalBase` en todas sus apariciones (líneas 27, 28, 44, 45, 56, 67, 68, 79, 91 y 103).
  - `EmployerContributionsRuleTests`: renombrar `ArlAndParafiscalsIgnoreTheFortyPercentRule` a
    `ArlFollowsTheIbcWhileParafiscalsIgnoreTheFortyPercentRule` y cambiar su ARL:

    ```csharp
    // ARL is social security: 1 200 000 (IBC with the 40 % excess) x 0.00522 = 6 264 -> 6 300. The fund stays on 1 000 000.
    Assert.Equal(6_300m, Line(lines, "arl").Amount);
    Assert.Contains(Line(lines, "arl").Trace.Inputs, item => item.Name == "IBC del mes" && Equals(item.Value, 1_200_000m));
    Assert.Equal(40_000m, Line(lines, "compensation_fund").Amount);
    ```

  - `EmployerContributionsRuleTests`, nueva:

    ```csharp
    [Fact]
    public void TheArlOfATopEarnerIsCappedLikeTheIbc()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16), 50_000_000m) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15, SalaryRaw = 25_000_000m, Salary = 25_000_000m, Arl = 130_500m, CompensationFund = 1_000_000m,
            },
        };

        var lines = Contributions(input);

        // IBC capped at 25 x 1 750 905 = 43 772 625; x 0.00522 = 228 493.10 -> 228 500 - 130 500.
        Assert.Equal(98_000m, Line(lines, "arl").Amount);
        // The fund has no cap: 50 000 000 x 0.04 = 2 000 000 - 1 000 000.
        Assert.Equal(1_000_000m, Line(lines, "compensation_fund").Amount);
    }
    ```

  - Goldens (ver Step 4): `non-salary-over-forty-percent` `arl` 6300, `employerCost` 2555143;
    `ibc-cap-and-top-bracket-second` `arl` 98000, `employerCost` 36654000.
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS1061: 'IbcOutcome' does not contain a definition for 'SocialSecurityIbc'` (y `ParafiscalBase`).
- [ ] **Step 3: Implementar.**
  - `IbcRule.cs`:

    ```csharp
    /// <summary>
    /// Las dos bases del mes (decisión del owner 1, 2026-11-22). <paramref name="SocialSecurityIbc"/> es el IBC de seguridad social:
    /// devengo salarial más el exceso no salarial sobre el 40 %, con piso por días y tope de 25 SMMLV; base de salud, pensión, FSP y
    /// ARL. <paramref name="ParafiscalBase"/> es <c>SalDev_m</c> (salario, horas e ingresos salariales del mes) sin la regla del 40 %
    /// ni piso ni tope; base de caja, ICBF y SENA.
    /// </summary>
    public sealed record IbcOutcome(decimal SocialSecurityIbc, decimal ParafiscalBase, PayrollLine Line);
    ```

    y `return new IbcOutcome(ibc, salaryEarningsMonth, line);` sin más cambios.
  - `EmployeeDeductionsRule.cs:19,23` y `SolidarityFundRule.cs:16,25,29`: `ibc.IbcMonth` → `ibc.SocialSecurityIbc`.
  - `EmployerContributionsRule.cs`: el `summary` dice «Salud, pensión y ARL van sobre el IBC; caja, ICBF y SENA sobre el devengo
    salarial del mes (sin la regla del 40 %)»; las líneas:

    ```csharp
    Line("health_employer", "Salud (empleador)", "IBC del mes", ibc.SocialSecurityIbc, legal.HealthEmployerRate.Value, [legal.HealthEmployerRate, .. extra], prior.HealthEmployer, exempt),
    Line("pension_employer", "Pensión (empleador)", "IBC del mes", ibc.SocialSecurityIbc, pensionRate, pensionLegal, prior.PensionEmployer, false),
    Line("arl", "ARL", "IBC del mes", ibc.SocialSecurityIbc, arl.Value, [arl], prior.Arl, false),
    Line("compensation_fund", "Caja de compensación", "Devengo salarial del mes", ibc.ParafiscalBase, legal.CompensationFundRate.Value, [legal.CompensationFundRate], prior.CompensationFund, false),
    Line("icbf", "ICBF", "Devengo salarial del mes", ibc.ParafiscalBase, legal.IcbfRate.Value, [legal.IcbfRate, .. extra], prior.Icbf, exempt),
    Line("sena", "SENA", "Devengo salarial del mes", ibc.ParafiscalBase, legal.SenaRate.Value, [legal.SenaRate, .. extra], prior.Sena, exempt),
    ```
- [ ] **Step 4: Goldens.** En `non-salary-over-forty-percent.json`: `"description"` → «Pagos no salariales por encima del 40 %:
  el exceso se suma al IBC y a la ARL, no a la caja ni a los parafiscales (decisión del owner 1).»; `arl` 6300; `employerCost`
  2555143; notas: `"arl = techo100(1200000 × 0.00522 = 6264) − 0 = 6 300"` y en la última `aportes 190300` y `= 2555143`. En
  `ibc-cap-and-top-bracket-second.json`: `arl` 98000; `employerCost` 36654000; nota
  `"arl = techo100(43772625 × 0.00522 = 228493.10) − 130500 = 98 000"`; última nota `aportes 6196500 … = 36654000`.
- [ ] **Step 5: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~IbcRuleTests|FullyQualifiedName~EmployerContributionsRuleTests|FullyQualifiedName~GoldenCaseTests|FullyQualifiedName~EmployeeDeductionsRuleTests|FullyQualifiedName~SolidarityFundRuleTests"`.
  Expected: `Passed!`. (Comprobado al escribir el plan: con la ARL sobre el IBC sólo esos dos casos de oro cambian.)
- [ ] **Step 6: Commit** — `fix(payroll): base the ARL on the social security IBC`

### Task A2: Auxilio consolidado en la segunda quincena, sin descuento (decisión 2, auxilio)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/TransportAllowanceRule.cs`, `MonthAccumulators.cs` (sólo el comentario)
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/TransportAllowanceRuleTests.cs`

- [ ] **Step 1: Pruebas** en `TransportAllowanceRuleTests`:
  - `OverTwiceTheMinimumLosesIt`: `Assert.Equal(0m, transport.Raw)` pasa a `Assert.Equal(124_547.5m, transport.Raw)` (lo causado por
    días se acumula aunque no se pague). **RED.**
  - Nueva **RED**:

    ```csharp
    // Decision 2 + CST art. 149: the month went over the cap in the second half; what the first paid is never deducted.
    [Fact]
    public void TheSecondHalfNeverClawsBackWhatTheFirstPaid()
    {
        var input = WithCommissions(WithCapBase(EngineTestData.Input(SecondHalf, 3_000_000m), TransportCapBase.SalaryAndSalaryIncome), 600_000m) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15, SalaryRaw = 1_500_000m, Salary = 1_500_000m, TransportRaw = 124_547.5m, Transport = 124_548m,
            },
        };

        var transport = EngineTestData.Earnings(input).Transport;

        Assert.Equal(0m, transport.Line.Amount);
        Assert.Equal(
            "El ingreso del mes supera 2 SMMLV: lo pagado en la primera quincena no se descuenta (CST art. 149)",
            transport.Line.Trace.Note);
        Assert.Contains(transport.Line.Trace.Inputs, item => item.Name == "Pagado en la primera quincena" && Equals(item.Value, 124_548m));
    }
    ```

  - Nueva **RED** (hoy la primera guarda `Raw` 0 y la segunda paga 124 548):

    ```csharp
    // First half over the cap (3 600 000), salary down to 3 000 000 on the 16th: the month qualifies and pays all 30 days.
    [Fact]
    public void AMonthThatEndsUnderTheCapPaysTheFirstHalfToo()
    {
        SalarySegmentSource[] salaries =
        [
            new(new DateOnly(2025, 1, 1), 3_600_000m, Teleworker: false),
            new(new DateOnly(2026, 11, 16), 3_000_000m, Teleworker: false),
        ];
        var first = EngineTestData.Earnings(EngineTestData.Input(FirstHalf, 3_600_000m)).Transport;
        var second = EngineTestData.Input(SecondHalf) with
        {
            Salaries = salaries,
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, TransportRaw = first.Raw, Transport = first.Line.Amount },
        };

        Assert.Equal(0m, first.Line.Amount);
        Assert.Equal(249_095m, EngineTestData.Earnings(second).Transport.Line.Amount);
    }
    ```

  - Nueva, **de caracterización** (excepción legal: horas y recargos no cuentan para el tope):

    ```csharp
    [Fact]
    public void OvertimeAndSurchargesNeverCountAgainstTheCap()
    {
        var input = WithCapBase(EngineTestData.Input(FirstHalf, 3_000_000m), TransportCapBase.SalaryAndSalaryIncome) with
        {
            Overtime =
            [
                EngineTestData.Overtime(SystemConceptKey.SundaySurcharge, 40m),
                EngineTestData.Overtime(SystemConceptKey.ExtraDay, 20m),
            ],
        };

        var chain = EngineTestData.Earnings(input);

        Assert.True(chain.Month.MonthSalaryIncome > 3_501_810m);
        Assert.Equal(3_000_000m, chain.Month.TransportCapIncome);
        Assert.Equal(124_548m, chain.Transport.Line.Amount);
    }
    ```
- [ ] **Step 2: RED.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~TransportAllowanceRuleTests"`.
  Expected: fallan `OverTwiceTheMinimumLosesIt` (`Expected: 124547.5 Actual: 0`), `TheSecondHalfNeverClawsBackWhatTheFirstPaid`
  (nota distinta) y `AMonthThatEndsUnderTheCapPaysTheFirstHalfToo` (`Expected: 249095 Actual: 124548`); la de caracterización pasa.
- [ ] **Step 3: Implementar** `TransportAllowanceRule.Compute` (la rama mensual queda como hoy; la Tarea A4 la amplía):

  ```csharp
  private const string Formula =
      "redondeo al peso(auxilio ÷ 30 × días trabajados, acumulado del mes) − lo pagado en la primera quincena; 0 si el ingreso del mes supera el tope, sin descontar lo pagado";

  public static TransportOutcome Compute(EngineInput input, WorkedDays worked, SalaryOutcome salary, MonthIncome month)
  {
      // … code, label, cap y legalValues como hoy …
      var paidBefore = input.Prior.Transport;
      var inputs = new List<TraceInput>
      {
          new("Días trabajados", worked.Days),
          new("Ingreso para el tope", month.TransportCapIncome),
          new("Tope", cap),
          new("Pagado en la primera quincena", paidBefore),
      };

      PayrollLine Line(decimal amount, string? note) =>
          new(code, PayrollLineGroup.Earning, label, amount, null, new LineTrace(Formula, inputs, legalValues, note));

      // The raw accrues by days whether or not the month qualifies (decision 2): the second half settles the whole month, so a
      // first half over the cap still leaves its days for a month that ends under it.
      var raw = legal.TransportAllowance.Value * worked.Days / 30m;

      if (month.TransportCapIncome > cap)
      {
          var over = $"El ingreso del mes supera {legal.TransportAllowanceCapSmmlv.Value:0.##} SMMLV";
          // CST art. 149: what the first half paid is never deducted; the line stays at 0 and says so.
          return new TransportOutcome(
              Line(0m, paidBefore > 0m ? $"{over}: lo pagado en la primera quincena no se descuenta (CST art. 149)" : over), raw);
      }

      if (input.Settings.TransportFrequency == TransportFrequency.Monthly && input.Period.Half == PayrollHalf.First)
      {
          return new TransportOutcome(Line(0m, "Se paga en la segunda quincena"), raw);
      }

      var amount = Math.Max(0m, PayrollRounding.ToPeso(input.Prior.TransportRaw + raw) - paidBefore);
      return new TransportOutcome(Line(amount, null), raw);
  }
  ```

  `MonthAccumulators`: el comentario dice que `TransportRaw` es lo causado por días, se pague o no.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~TransportAllowanceRuleTests|FullyQualifiedName~GoldenCaseTests"`.
  Expected: `Passed!` (los casos de oro no cambian de monto).
- [ ] **Step 5: Commit** — `fix(payroll): settle the transport allowance on the month without clawing back`

### Task A3: Exoneración consolidada en la segunda quincena (decisión 2, exoneración)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/EmployerContributionsRule.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/EmployerContributionsRuleTests.cs`

- [ ] **Step 1: Pruebas.**
  - **RED:**

    ```csharp
    // The first half paid health, ICBF and SENA (company not exonerated then); the month is exempt: the second half refunds them
    // on the employer side. The fund is never exempt.
    [Fact]
    public void AMonthThatTurnsExemptRefundsTheFirstHalfOnTheEmployerSide()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16)) with
        {
            Prior = MonthAccumulators.Empty with
            {
                WorkedDays = 15, SalaryRaw = 875_452.5m, Salary = 875_453m,
                HealthEmployer = 74_500m, PensionEmployer = 105_100m, Arl = 4_600m, CompensationFund = 35_100m,
                Icbf = 26_300m, Sena = 17_600m,
            },
        };

        var lines = Contributions(input);

        Assert.Equal(-74_500m, Line(lines, "health_employer").Amount);
        Assert.Equal(-26_300m, Line(lines, "icbf").Amount);
        Assert.Equal(-17_600m, Line(lines, "sena").Amount);
        Assert.Equal(
            "Exonerado en el mes (ET art. 114-1): se reversa lo aportado en la primera quincena",
            Line(lines, "health_employer").Trace.Note);
        Assert.Equal(35_000m, Line(lines, "compensation_fund").Amount);
    }
    ```

  - **De caracterización:**

    ```csharp
    // First half exempt (9 000 000 < 10 SMMLV); a 9 000 000 commission in the second takes the month to 18 000 000: the second
    // half charges the whole month.
    [Fact]
    public void AMonthThatStopsBeingExemptChargesTheWholeMonthInTheSecondHalf()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16), 9_000_000m) with
        {
            Incomes = [EngineTestData.Income(SystemConceptKey.Commissions, 9_000_000m, true)],
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, SalaryRaw = 4_500_000m, Salary = 4_500_000m },
        };

        var lines = Contributions(input);

        Assert.Equal(1_530_000m, Line(lines, "health_employer").Amount);
        Assert.Equal(540_000m, Line(lines, "icbf").Amount);
        Assert.Equal(360_000m, Line(lines, "sena").Amount);
        Assert.Null(Line(lines, "health_employer").Trace.Note);
    }
    ```
- [ ] **Step 2: RED.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~EmployerContributionsRuleTests"`.
  Expected: falla `AMonthThatTurnsExemptRefundsTheFirstHalfOnTheEmployerSide` (`Expected: -74500 Actual: 0`).
- [ ] **Step 3: Implementar** en `EmployerContributionsRule`:

  ```csharp
  private const string RefundNote = "Exonerado en el mes (ET art. 114-1): se reversa lo aportado en la primera quincena";

  // Decision 2: the second half settles the month. An exempt month owes 0, so what the first half paid comes back here, on the
  // employer side; the worker's lines never depend on the exoneration.
  var month = exempt ? 0m : PayrollRounding.CeilingToHundred(baseAmount * rate);
  var amount = month - prior;
  var note = exempt ? (prior != 0m ? RefundNote : ExemptNote) : null;
  ```

  y `Formula` pasa a `"techo100(base del mes × tasa), 0 si está exonerado; menos lo aportado en la primera quincena"`.
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~EmployerContributionsRuleTests|FullyQualifiedName~GoldenCaseTests|FullyQualifiedName~PayrollEngineTests"`.
  Expected: `Passed!`.
- [ ] **Step 5: Commit** — `fix(payroll): settle the exoneration on the month in the second half`

### Task A4: Auxilio mensual cuando el contrato termina en la primera quincena (decisión 3)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/TransportAllowanceRule.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/TransportAllowanceRuleTests.cs`,
  `tests/Modules/Payroll/Modules.Payroll.UnitTests/Golden/GoldenCaseTests.cs`
- Create: `tests/Modules/Payroll/Modules.Payroll.UnitTests/Golden/synthetic/monthly-transport-ends-in-first-half.json`

- [ ] **Step 1: Pruebas.**
  - `TransportAllowanceRuleTests`, **RED**:

    ```csharp
    // Monthly pays in the last half worked: with the contract ending on the 10th there is no second half for this employee.
    [Fact]
    public void MonthlyPaysWhatItAccruedWhenTheContractEndsInTheFirstHalf()
    {
        var input = EngineTestData.Input(FirstHalf) with { ContractEndedOn = new DateOnly(2026, 11, 10) };
        input = input with { Settings = input.Settings with { TransportFrequency = TransportFrequency.Monthly } };

        var transport = EngineTestData.Earnings(input).Transport;

        // 249 095 / 30 x 10 = 83 031.67 -> 83 032
        Assert.Equal(83_032m, transport.Line.Amount);
        Assert.Equal("El contrato termina en esta quincena: se paga lo causado del mes", transport.Line.Trace.Note);
    }
    ```

  - Caso de oro nuevo `monthly-transport-ends-in-first-half.json`: copia de `monthly-transport-first-half.json` con
    `"name": "monthly-transport-ends-in-first-half"`, `"description": "Auxilio mensual y contrato que termina el 10: se paga lo
    causado en la primera quincena (decisión del owner 3)."`, `"contractEndedOn": "2026-11-10"` en `input`, y `expected`:

    ```json
    "expected": {
      "workedDays": 10,
      "finalSettlementPending": true,
      "negativeNet": false,
      "lines": [
        { "code": "salary", "amount": 583635 },
        { "code": "transport_allowance", "amount": 83032 },
        { "code": "ibc", "amount": 583635 },
        { "code": "health_employee", "amount": 23400 },
        { "code": "pension_employee", "amount": 23400 },
        { "code": "health_employer", "amount": 0 },
        { "code": "pension_employer", "amount": 70100 },
        { "code": "arl", "amount": 3100 },
        { "code": "compensation_fund", "amount": 23400 },
        { "code": "icbf", "amount": 0 },
        { "code": "sena", "amount": 0 },
        { "code": "severance", "amount": 55533 },
        { "code": "severance_interest", "amount": 6667 },
        { "code": "premium", "amount": 55533 },
        { "code": "vacation", "amount": 24338 }
      ],
      "net": 619867,
      "employerCost": 905338
    }
    ```

    `notes`: días 10 (1 al 10); salario 1 750 905 ÷ 30 × 10 = 583 635; auxilio round(83 031,67) = 83 032; IBC = piso 583 635;
    salud y pensión techo100(23 345,4) = 23 400; pensión empleador techo100(70 036,2) = 70 100; ARL techo100(3 046,57) = 3 100;
    caja 23 400; provisiones sobre 666 667 (583 635 + 83 032): cesantías y prima round(55 533,36) = 55 533, intereses
    round(6 666,67) = 6 667, vacaciones round(583 635 × 0,0417 = 24 337,58) = 24 338; neto 666 667 − 46 800 = 619 867; costo
    666 667 + aportes 96 600 + provisiones 142 071 = 905 338. `covers` igual que `monthly-transport-first-half`.
    **Son cuentas a mano: si el motor da otro número, se revisa la cuenta antes que el código** y el valor correcto queda en las
    notas.
  - `GoldenCaseTests.ThereAreTwentyEightSyntheticFilesWithUniqueNames` → `ThereAreTwentyNineSyntheticFilesWithUniqueNames`,
    con `Assert.Equal(29, …)`.
- [ ] **Step 2: RED.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~TransportAllowanceRuleTests|FullyQualifiedName~GoldenCaseTests"`.
  Expected: fallan `MonthlyPaysWhatItAccruedWhenTheContractEndsInTheFirstHalf` (`Expected: 83032 Actual: 0`) y
  `EveryCaseMatches(name: "monthly-transport-ends-in-first-half")` (`transport_allowance = 83032` contra `= 0`, y el neto y el
  costo).
- [ ] **Step 3: Implementar** la rama mensual de `TransportAllowanceRule`:

  ```csharp
  // Monthly pays in the last half worked: the second, or the first when the contract ends inside it (decision 3).
  if (input.Settings.TransportFrequency == TransportFrequency.Monthly && input.Period.Half == PayrollHalf.First)
  {
      if (!worked.FinalSettlementPending)
      {
          return new TransportOutcome(Line(0m, "Se paga en la segunda quincena"), raw);
      }

      var settled = Math.Max(0m, PayrollRounding.ToPeso(input.Prior.TransportRaw + raw) - paidBefore);
      return new TransportOutcome(Line(settled, "El contrato termina en esta quincena: se paga lo causado del mes"), raw);
  }
  ```
- [ ] **Step 4: GREEN.** Mismo comando del Step 2. Expected: `Passed!`.
- [ ] **Step 5: Commit** — `fix(payroll): pay the monthly transport allowance when the contract ends in the first half`

### Task A5: Cita del piso del salario integral (decisión 9)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/LegalBasis.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/LegalBasisTests.cs`

- [ ] **Step 1: Prueba RED:**

  ```csharp
  [Fact]
  public void TheIntegralSalaryFloorCitesTheNumeralAndItsReform() =>
      Assert.Equal("CST art. 132, num. 2 (mod. Ley 50 de 1990, art. 18)", LegalBasis.BasisOf("IntegralSalaryFloor"));
  ```
- [ ] **Step 2: RED.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~LegalBasisTests"`.
  Expected: `Expected: "CST art. 132, num. 2 (mod. Ley 50 de 1990, art. 18)" Actual: "CST art. 132"`.
- [ ] **Step 3: Implementar** `LegalBasis.cs:14`:
  `["IntegralSalaryFloor"] = ("Piso del salario integral", "CST art. 132, num. 2 (mod. Ley 50 de 1990, art. 18)"),`
- [ ] **Step 4: GREEN.** Mismo comando. Expected: `Passed!`.
- [ ] **Step 5: Commit** — `docs(payroll): cite the integral salary floor with its numeral and reform`

### Task A6: Versión del motor 2026.11.3 y spec base corregido

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Domain/Engine/PayrollEngine.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/Engine/PayrollEngineTests.cs`
- Modify (docs): `docs/superpowers/specs/2026-11-21-nomina-periodo-y-motor-design.md`

- [ ] **Step 1: Prueba RED.** `PayrollEngineTests.TheMinimumWageFirstHalfMatchesTheAnchor`, línea 42:
  `Assert.Equal("2026.11.3", result.EngineVersion);`
- [ ] **Step 2: RED.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~PayrollEngineTests"`.
  Expected: `Expected: "2026.11.3" Actual: "2026.11.2"`.
- [ ] **Step 3: Implementar.** `PayrollEngine.cs:9`: `public const string Version = "2026.11.3";`. Las pruebas de
  `PayrollPeriodTests` y `PayrollPersistenceTests` que pasan `"2026.11.1"`/`"2026.11.2"` como dato no se tocan: no leen la
  constante.
- [ ] **Step 4: Spec base.** En `2026-11-21-nomina-periodo-y-motor-design.md`, sección «Cálculo»:
  - «Auxilio de transporte»: agregar «La primera quincena es provisional y la segunda consolida el mes: si el mes supera el tope,
    la segunda paga 0 y no descuenta lo ya pagado (CST art. 149); si el mes queda por debajo, paga el mes completo menos lo
    pagado. Las horas extras y los recargos no cuentan para el tope. Con frecuencia mensual, si el contrato termina en la primera
    quincena, el auxilio se paga en ella. (Decisiones del owner 2 y 3, 2026-11-22.)»
  - «Aportes del empleador»: «y ARL por la clase del centro de trabajo del empleado (no incluye auxilio ni no salariales)» pasa a
    «ARL por la clase del centro de trabajo, sobre el IBC de seguridad social; caja, ICBF y SENA sobre el devengo salarial del mes,
    sin la regla del 40 % (decisión del owner 1, 2026-11-22)», y a la exoneración se le agrega «La segunda quincena la decide sobre
    el mes; si el mes queda exonerado, devuelve lo aportado en la primera del lado del empleador (decisión del owner 2).»
- [ ] **Step 5: GREEN del bloque.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!`.
- [ ] **Step 6: Commit** — `feat(payroll): bump the engine to 2026.11.3`

> **Cierre del Bloque A:** revisión del bloque (una, sobre A1–A6).

---

## Bloque B — Aplicación (Tareas B1–B6)

Un implementador. La integración corre sólo en los GREEN que la piden, filtrada por nombre; las clases completas de integración
tocadas (`PayrollInputsApiTests`, `PayrollSettingsApiTests`) corren una vez al final del bloque, en B6.

### Task B1: La huella sin nombres de conceptos ni de centros de trabajo (decisión 4)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Application/InputFingerprint.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/InputFingerprintTests.cs`

- [ ] **Step 1: Pruebas.**
  - **RED:**

    ```csharp
    // Decision 4: a name is a label, not an input. Renaming a concept or a workplace never makes a period «sourceChanged».
    [Fact]
    public void RenamingAConceptOrAWorkplaceDoesNotChangeIt()
    {
        var conceptId = Guid.Parse("01900000-0000-7000-8000-0000000000f1");
        var recurringId = Guid.Parse("01900000-0000-7000-8000-0000000000f2");
        EngineInput With(string concept, string workplace) => EngineTestData.Input(Day) with
        {
            Workplace = new WorkplaceInfo(workplace, ArlClass.I, HighRiskPension: false),
            Overtime = [new OvertimeInput(conceptId, concept, 2m, null, 1.4m)],
            Incomes = [new IncomeInput(conceptId, concept, 100_000m, true)],
            Deductions = [new DeductionInput(conceptId, concept, 20_000m)],
            Recurring = [new RecurringInput(recurringId, concept, ConceptKind.Income, RecurringMode.Fixed, 50_000m, false, null)],
        };

        Assert.Equal(InputFingerprint.Of(With("Bono", "Sede principal")), InputFingerprint.Of(With("Bono de ventas", "Bodega norte")));
    }
    ```

  - **De caracterización** (la naturaleza sigue contando):

    ```csharp
    [Fact]
    public void TheNatureOfAConceptStillChangesIt()
    {
        var input = EngineTestData.Input(Day) with
        {
            Incomes = [EngineTestData.Income(Modules.Payroll.Domain.SystemConceptKey.OtherIncome, 100_000m, true)],
        };
        var notSalary = input with { Incomes = [input.Incomes[0] with { SalaryConstitutive = false }] };
        var otherConcept = input with { Incomes = [input.Incomes[0] with { ConceptId = Guid.NewGuid() }] };

        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(notSalary));
        Assert.NotEqual(InputFingerprint.Of(input), InputFingerprint.Of(otherConcept));
    }
    ```

    (`using Modules.Payroll.Domain;` para `ConceptKind` y `RecurringMode`; `ArlClass` está en `Modules.Payroll.Domain.Engine`.)
- [ ] **Step 2: RED.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~InputFingerprintTests"`.
  Expected: falla `RenamingAConceptOrAWorkplaceDoesNotChangeIt` (`Assert.Equal() Failure: Strings differ`).
- [ ] **Step 3: Implementar** en `InputFingerprint`:

  ```csharp
  // Decision 4 (2026-11-22): names are labels. Ids, hours, amounts, caps and the nature of each concept (kind, system key,
  // multiplier, mode, salary-constitutive) stay in; the concept and workplace names leave.
  private static readonly string[] ListsWithConceptNames = ["overtime", "incomes", "deductions", "recurring"];

  public static string Of(EngineInput input)
  {
      ArgumentNullException.ThrowIfNull(input);

      var node = JsonSerializer.SerializeToNode(input, PayrollJson.Options)!.AsObject();
      node.Remove("legal");
      foreach (var list in ListsWithConceptNames)
      {
          foreach (var item in node[list]!.AsArray())
          {
              item!.AsObject().Remove("conceptName");
          }
      }

      node["workplace"]!.AsObject().Remove("name");
      return Hash(Canonical(node));
  }
  ```

  Y el `summary` de la clase: «… de lo que el motor lee de un empleado, sin la foto legal ni los nombres de conceptos y centros de
  trabajo …».
- [ ] **Step 4: GREEN.** Mismo comando. Expected: `Passed!`.
- [ ] **Step 5: Commit** — `fix(payroll): keep concept and workplace names out of the input fingerprint`

### Task B2: Las líneas ya capturadas con un concepto archivado se vuelven a guardar (decisión 5, capturas)

**Files:**
- Create: `src/Modules/Payroll/Modules.Payroll.Application/ArchivedLineQuota.cs`
- Modify: `src/Modules/Payroll/Modules.Payroll.Application/UpdateEmployeeInputs.cs`
- Create (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/ArchivedLineQuotaTests.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollInputsApiTests.cs`

- [ ] **Step 1: Pruebas.**
  - `ArchivedLineQuotaTests` (usa el patrón de `EngineInputFactoryTests.Inputs`: `PayrollPeriod.Open` + `ReplaceInputs`):

    ```csharp
    public sealed class ArchivedLineQuotaTests
    {
        private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
        private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
        private static readonly Guid Ana = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
        private static readonly Guid Luis = Guid.Parse("01900000-0000-7000-8000-0000000000e2");
        private static readonly Guid Bonus = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
        private static readonly PayrollActor Actor = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");
        private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

        private static IReadOnlyList<PeriodInput> Saved()
        {
            var period = PayrollPeriod.Open(
                PayrollPeriodId.New(), Tenant, Company, (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), PayrollHalf.First), Actor, Now);
            period.ReplaceInputs(Ana,
            [
                new PeriodInputDraft(Bonus, ConceptKind.Income, null, 100_000m, false),
                new PeriodInputDraft(Bonus, ConceptKind.Income, null, 50_000m, false),
            ], Actor, Now);
            return period.Inputs;
        }

        [Fact]
        public void TheLinesAlreadySavedCanBeTakenAgainButNotOneMore()
        {
            var quota = ArchivedLineQuota.Of(Saved(), Ana);

            Assert.True(quota.TryTake(Bonus, ConceptKind.Income));
            Assert.True(quota.TryTake(Bonus, ConceptKind.Income));
            Assert.False(quota.TryTake(Bonus, ConceptKind.Income));
        }

        [Fact]
        public void TheQuotaIsPerEmployeeAndKind()
        {
            Assert.False(ArchivedLineQuota.Of(Saved(), Luis).TryTake(Bonus, ConceptKind.Income));
            Assert.False(ArchivedLineQuota.Of(Saved(), Ana).TryTake(Bonus, ConceptKind.Deduction));
        }
    }
    ```

  - `PayrollInputsApiTests`, nueva (junto a `AnArchivedConceptIs422`, que no cambia: una línea nueva sigue siendo 422):

    ```csharp
    [Fact]
    public async Task AnArchivedConceptAlreadyCapturedCanBeSavedAgain()
    {
        var scene = await NewSceneAsync(_fixture);
        var concept = await CreatedAsync(
            scene.Tenant.Payroll, $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts",
            new { kind = "Income", name = "Bono viejo", defaultSalaryConstitutive = (bool?)null, multiplier = (decimal?)null });
        var conceptId = concept.GetProperty("id").GetGuid();
        var saved = await PutInputsAsync(
            scene, InputsBody(incomes: [new { conceptId, amount = 1_000m, salaryConstitutive = false }]), $"\"{await VersionOfAsync(scene)}\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var archive = await scene.Tenant.Payroll.PostAsync(
            $"{scene.Tenant.Url}/companies/{scene.Setup.Company.Id}/concepts/{conceptId}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        // The same line, with another amount: it was already there, so it is kept.
        var again = await PutInputsAsync(
            scene, InputsBody(incomes: [new { conceptId, amount = 2_000m, salaryConstitutive = false }]), $"\"{await VersionOfAsync(scene)}\"");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // A second line of it is new: rejected on its own field.
        var extra = await PutInputsAsync(
            scene,
            InputsBody(incomes:
            [
                new { conceptId, amount = 2_000m, salaryConstitutive = false },
                new { conceptId, amount = 500m, salaryConstitutive = false },
            ]),
            $"\"{await VersionOfAsync(scene)}\"");
        Assert.Equal((HttpStatusCode)422, extra.StatusCode);
        var problem = await JsonOfAsync(extra);
        Assert.True(HasError(problem, "incomes[1].conceptId"));
        Assert.False(HasError(problem, "incomes[0].conceptId"));
        scene.Tenant.Dispose();
    }
    ```
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0246: The type or namespace name 'ArchivedLineQuota' could not be found`.
- [ ] **Step 3: Implementar.**
  - `ArchivedLineQuota.cs`:

    ```csharp
    using Modules.Payroll.Domain;

    namespace Modules.Payroll.Application;

    /// <summary>
    /// Decisión del owner 5 (2026-11-22): un concepto archivado no entra en líneas nuevas, pero las que el empleado ya tenía guardadas
    /// en la quincena se vuelven a guardar, con su valor cambiado o no. La captura reemplaza la lista entera y una línea de la
    /// solicitud no trae id, así que se cuenta: por concepto y tipo, a lo sumo tantas como había guardadas.
    /// </summary>
    public sealed class ArchivedLineQuota
    {
        private readonly Dictionary<(Guid ConceptId, ConceptKind Kind), int> _left;

        private ArchivedLineQuota(Dictionary<(Guid ConceptId, ConceptKind Kind), int> left) => _left = left;

        public static ArchivedLineQuota Of(IEnumerable<PeriodInput> saved, Guid employeeId)
        {
            ArgumentNullException.ThrowIfNull(saved);
            return new ArchivedLineQuota(saved
                .Where(input => input.EmployeeId == employeeId)
                .GroupBy(input => (input.ConceptId, input.Kind))
                .ToDictionary(group => group.Key, group => group.Count()));
        }

        /// <summary>Gasta una línea del cupo; falso si no queda: la línea es nueva.</summary>
        public bool TryTake(Guid conceptId, ConceptKind kind)
        {
            var left = _left.GetValueOrDefault((conceptId, kind));
            if (left <= 0)
            {
                return false;
            }

            _left[(conceptId, kind)] = left - 1;
            return true;
        }
    }
    ```

  - `UpdateEmployeeInputs.cs`: antes de los tres `for`,
    `var quota = ArchivedLineQuota.Of(period.Inputs, command.EmployeeId);` (lo guardado **antes** de reemplazar); `CheckConcept`
    recibe `quota` y la rama del archivado pasa a
    `else if (concept.Archived && !quota.TryTake(conceptId, kind))`. El comentario del método: «Un concepto que no existe, es de
    otro tipo o está archivado en una línea nueva es 422 en el campo de su línea; una línea archivada que ya estaba guardada se
    acepta (decisión del owner 5).» El texto «El concepto está archivado.» no cambia.
- [ ] **Step 4: GREEN unitario.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~ArchivedLineQuotaTests"`. Expected: `Passed!`.
- [ ] **Step 5: GREEN de integración** (mirar antes `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`). Run:
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~PayrollInputsApiTests.AnArchivedConcept" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` en `AnArchivedConceptIs422` y `AnArchivedConceptAlreadyCapturedCanBeSavedAgain`.
- [ ] **Step 6: Commit** — `fix(payroll): let saved lines of an archived concept be saved again`

### Task B3: Un recurrente de un concepto archivado deja de aplicar desde la quincena siguiente (decisión 5, recurrentes)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Application/ConceptCatalog.cs`, `EngineInputFactory.cs`, `LiquidationPlanner.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/ConceptCatalogTests.cs`, `EngineInputFactoryTests.cs`

- [ ] **Step 1: Pruebas.**
  - `ConceptCatalogTests`, nueva:

    ```csharp
    // Archived at 03:00 UTC of the 16th = 22:00 of the 15th in Bogotá: archived on the 15th, so it is gone from the 16th on.
    [Fact]
    public async Task ArchivedBeforeCountsTheTenantDay()
    {
        var repository = new FakeConceptRepository();
        var bonus = Custom(Company, ConceptKind.Income, "Bono", constitutive: false);
        bonus.Archive(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero));
        repository.Add(bonus);
        var catalog = await new ConceptCatalog(repository).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken);
        static DateOnly Bogota(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(-5)).DateTime);

        Assert.Contains(bonus.Id.Value, catalog.ArchivedBefore(new DateOnly(2026, 11, 16), Bogota));
        Assert.DoesNotContain(bonus.Id.Value, catalog.ArchivedBefore(new DateOnly(2026, 11, 1), Bogota));
    }
    ```

  - `EngineInputFactoryTests`: el helper `Build` recibe `IReadOnlySet<Guid>? retired = null` y lo pasa como
    `retired ?? new HashSet<Guid>()` después de `applied`. Nueva:

    ```csharp
    [Fact]
    public async Task ARecurringOfARetiredConceptIsLeftOut()
    {
        var bonus = PayrollConcept.Create(PayrollConceptId.New(), Tenant, Company, ConceptKind.Income, "Bono", false, null, Now);
        var catalog = await CatalogAsync(bonus);
        var kept = Recurring(OtherIncome);
        var retired = Recurring(bonus.Id.Value);

        var input = Build(Employee(), catalog, recurring: [kept, retired], retired: new HashSet<Guid> { bonus.Id.Value });

        Assert.Equal([kept.Id.Value], input.Recurring.Select(item => item.RecurringId));
    }
    ```
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS1061: 'ConceptCatalogSnapshot' does not contain a definition for 'ArchivedBefore'` y `error CS1501: No
  overload for method 'Build' takes 11 arguments`.
- [ ] **Step 3: Implementar.**
  - `ConceptCatalogSnapshot`: guardar `concept.ArchivedAt` de los propios (`Dictionary<Guid, DateTimeOffset>`, sólo los
    archivados) y:

    ```csharp
    /// <summary>
    /// Los conceptos propios archivados antes de <paramref name="periodStart"/>, contando el día del tenant (decisión del owner 5):
    /// un recurrente de un concepto archivado deja de aplicar desde la quincena siguiente al día del archivo. La quincena en la que
    /// se archivó lo conserva, así un período ya liquidado no cambia de huella. Los del sistema no se archivan.
    /// </summary>
    public IReadOnlySet<Guid> ArchivedBefore(DateOnly periodStart, Func<DateTimeOffset, DateOnly> tenantDay)
    {
        ArgumentNullException.ThrowIfNull(tenantDay);
        return _archivedAt.Where(entry => tenantDay(entry.Value) < periodStart).Select(entry => entry.Key).ToHashSet();
    }
    ```

  - `EngineInputFactory.Build`: parámetro nuevo `IReadOnlySet<Guid> retiredConcepts` después de `appliedBefore`; se pasa a
    `RecurringOf`, que salta `if (retiredConcepts.Contains(item.ConceptId)) { continue; }` antes de mirar el estado. El
    comentario de `RecurringOf`: «Sólo activos, no agotados y de un concepto no retirado …».
  - `LiquidationPlanner`: inyectar `ITenantClock tenantClock` (`using Modules.Tenancy.Application;`, como los handlers de Payroll);
    en `PlanAsync`, después de cargar el catálogo:

    ```csharp
    // An archived concept's recurring stop the period after the archive day, counted in the tenant's day (decision 5).
    var calendar = await tenantClock.GetAsync(period.TenantId, cancellationToken);
    var retired = concepts.ArchivedBefore(
        period.StartDate, instant => DateOnly.FromDateTime(calendar.ToLocal(instant).DateTime));
    ```

    y pasarlo a `EngineInputFactory.Build`. El registro no cambia: `QepServiceCollectionExtensions.cs:667`
    (`services.AddScoped<Modules.Payroll.Application.LiquidationPlanner>()`) lo resuelve por tipo e `ITenantClock` ya está
    registrado (lo usan los handlers de Payroll).
- [ ] **Step 4: GREEN.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~ConceptCatalogTests|FullyQualifiedName~EngineInputFactoryTests"`.
  Expected: `Passed!`. Integración: la cubren las corridas de clase del final del bloque (B6, `RecurringConceptsApiTests` incluida),
  porque el planner sólo suma una lectura del calendario.
- [ ] **Step 5: Commit** — `fix(payroll): stop recurring concepts of an archived concept from the next period`

### Task B4: Tramos del FSP con control de año (decisión 6)

**Files:**
- Modify: `src/Bootstrapper/PayrollLegalValues.cs`
- Modify (tests): `tests/Bootstrapper/Bootstrapper.UnitTests/PayrollLegalValuesTests.cs`
- Modify (docs): `docs/superpowers/specs/2026-11-22-parametros-legales-consola-operador-design.md` (sólo la frase de hecho)

- [ ] **Step 1: Pruebas.**
  - Nueva **RED**:

    ```csharp
    // Decision 6: the brackets are yearly like the scalar keys. 2027 has every scalar row but only the 2026 bracket set.
    [Fact]
    public async Task ABracketSetFromAnEarlierYearIsLegalParameterMissing()
    {
        var values = SeededValues();
        foreach (var row in values.Where(value => value.EffectiveFrom == Y2026).ToList())
        {
            values.Add(new(row.Key, new DateOnly(2027, 1, 1), row.Value));
        }

        var exception = await Assert.ThrowsAsync<PayrollDomainException>(() =>
            Adapter(values).ResolveAsync(new DateOnly(2027, 1, 14), Ct));

        Assert.Equal("payroll.legal_parameter.missing", exception.Code);
        Assert.Contains("SolidarityFund", exception.Message);
        Assert.DoesNotContain("Smmlv", exception.Message);
    }
    ```

  - `TheMidYearKeysStillResolveAcrossYears`: hoy resuelve 2027 con los tramos de 2026; con la decisión 6 fallaría. Se le agrega
    el conjunto de 2027:

    ```csharp
    var brackets = SeededBrackets();
    brackets.AddRange(SeededBrackets()
        .Where(bracket => bracket.EffectiveFrom == Y2026)
        .Select(bracket => new LegalParameterBracket(
            LegalBracketKey.SolidarityFund, new DateOnly(2027, 1, 1), bracket.LowerBound, bracket.UpperBound, bracket.Rate)));

    var set = await Adapter(values, brackets).ResolveAsync(new DateOnly(2027, 1, 14), Ct);
    ```

    (`LegalParameterBracket` es una clase de Hr con constructor público, no un `record`.)
- [ ] **Step 2: RED.** Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PayrollLegalValuesTests"`.
  Expected: falla `ABracketSetFromAnEarlierYearIsLegalParameterMissing` (`Assert.ThrowsAsync() Failure: No exception was thrown`);
  las demás pasan.
- [ ] **Step 3: Implementar** en `PayrollLegalValues.ResolveAsync`:

  ```csharp
  var solidarity = LegalParameters.ResolveBrackets(brackets, LegalBracketKey.SolidarityFund, asOf);

  // Decisión del owner 6 (2026-11-22): los tramos son anuales para la nómina, como las claves escalares; un conjunto vigente de un
  // año anterior es un año sin cargar.
  if (solidarity.Count == 0 || solidarity[0].EffectiveFrom.Year < asOf.Year)
  {
      missing.Add(LegalBracketKey.SolidarityFund.ToString());
  }
  ```

  y el `summary` de la clase: «… y los tramos con los de la mayor vigencia, que también tiene que ser del año».
- [ ] **Step 4: Doc de parámetros legales.** En `2026-11-22-parametros-legales-consola-operador-design.md`, la frase «**Los tramos
  `SolidarityFund` no son anuales para nómina:** sólo faltan si no hay ningún conjunto vigente (`PayrollLegalValues.cs:38-42`); el
  de 2026 sirve para 2027.» pasa a «**Los tramos `SolidarityFund` también son anuales para nómina** (decisión del owner 6,
  2026-11-22; spec `2026-11-22-nomina-decisiones-del-owner-design.md`): faltan si el conjunto vigente es de un año anterior al del
  periodo. ❓ Si el aviso `nextYearMissing`/`currentYearMissing` los cubre lo decide el owner.» No se toca nada más de ese borrador.
- [ ] **Step 5: GREEN.** Mismo comando del Step 2. Expected: `Passed!`.
- [ ] **Step 6: Commit** — `fix(payroll): require the solidarity fund brackets of the period's year`

### Task B5: La configuración de una empresa inactiva no se edita (decisión 10)

**Files:**
- Modify: `src/Modules/Payroll/Modules.Payroll.Application/PayrollCompanies.cs`, `UpdatePayrollSettings.cs`,
  `EnsureCurrentPeriods.cs`
- Create (tests): `tests/Modules/Payroll/Modules.Payroll.UnitTests/PayrollCompaniesTests.cs`
- Modify (tests): `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollSettingsApiTests.cs`

- [ ] **Step 1: Pruebas.**
  - `PayrollCompaniesTests`:

    ```csharp
    using Modules.Payroll.Application;
    using Modules.Payroll.Domain;

    namespace Modules.Payroll.UnitTests;

    public sealed class PayrollCompaniesTests
    {
        private static PayrollCompanyRef Company(bool active) =>
            new(Guid.Parse("01900000-0000-7000-8000-0000000000c1"), "Comercializadora QEP S.A.S.", "900123456", active);

        [Fact]
        public void AnInactiveCompanyIsCompanyInactive()
        {
            var exception = Assert.Throws<PayrollDomainException>(() => PayrollCompanies.EnsureActive(Company(false), "No."));

            Assert.Equal(PayrollErrorCodes.CompanyInactive, exception.Code);
        }

        [Fact]
        public void AnActiveCompanyPasses() => PayrollCompanies.EnsureActive(Company(true), "No.");
    }
    ```

  - `PayrollSettingsApiTests`, nueva:

    ```csharp
    // Decision 10: an inactive company's settings are read, never changed.
    [Fact]
    public async Task AnInactiveCompanyReadsButCannotSaveItsSettings()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var company = await CreateCompanyAsync(tenant.Admin, tenant.Id, "Comercializadora QEP S.A.S.");
        var first = await PutAsync(tenant.Payroll, SettingsUrl(tenant, company.Id), SettingsBody(), "\"0\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, company.Id);

        var saved = await PutAsync(tenant.Payroll, SettingsUrl(tenant, company.Id), SettingsBody(), "\"1\"");
        var read = await GetJsonAsync(tenant.Payroll, SettingsUrl(tenant, company.Id));

        Assert.Equal((HttpStatusCode)422, saved.StatusCode);
        Assert.Equal("payroll.company.inactive", CodeOf(await JsonOfAsync(saved)));
        Assert.Equal(1, read.GetProperty("version").GetInt64());
        Assert.False(read.GetProperty("company").GetProperty("isActive").GetBoolean());
    }
    ```

    (`SettingsBody()` con sus valores por defecto; si su firma pide argumentos, los del test vecino
    `AnInactiveCompanyWithSavedSettingsIsListed`.)
- [ ] **Step 2: RED de compilación.** Run: `dotnet build tests/Modules/Payroll/Modules.Payroll.UnitTests`.
  Expected: `error CS0117: 'PayrollCompanies' does not contain a definition for 'EnsureActive'`.
- [ ] **Step 3: Implementar.**
  - `PayrollCompanies`:

    ```csharp
    /// <summary>
    /// Una empresa inactiva se lee pero no se cambia: no abre quincenas ni guarda configuración (decisión del owner 10, 2026-11-22).
    /// </summary>
    public static void EnsureActive(PayrollCompanyRef company, string message)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (!company.IsActive)
        {
            throw new PayrollDomainException(PayrollErrorCodes.CompanyInactive, message);
        }
    }
    ```

  - `EnsureCurrentPeriods.cs:67-70` → `PayrollCompanies.EnsureActive(company, "An inactive company cannot open payroll periods.");`
  - `UpdatePayrollSettings.cs`, justo después de `var company = await companies.RequireAsync(…)`:
    `PayrollCompanies.EnsureActive(company, "An inactive company cannot change its payroll settings.");` (antes de leer la versión
    guardada: a una empresa inactiva se le dice eso, no un 412). El `summary` del handler suma la frase «Una empresa inactiva es 422
    `payroll.company.inactive`; el `GET` la sigue respondiendo.»
- [ ] **Step 4: GREEN unitario.** Run: `dotnet test tests/Modules/Payroll/Modules.Payroll.UnitTests --filter "FullyQualifiedName~PayrollCompaniesTests"`. Expected: `Passed!`.
- [ ] **Step 5: GREEN de integración** (mirar antes los procesos). Run:
  `dotnet test tests/Modules/Payroll/Modules.Payroll.IntegrationTests --filter "FullyQualifiedName~AnInactiveCompany" -- xUnit.MaxParallelThreads=4`.
  Expected: `Passed!` en `AnInactiveCompanyReadsButCannotSaveItsSettings`, `AnInactiveCompanyDoesNotOpenPeriods`,
  `AnInactiveCompanyWithSavedSettingsIsListed` y `AnInactiveCompanyWithPeriodsIsListed`.
- [ ] **Step 6: Commit** — `fix(payroll): keep an inactive company's payroll settings read-only`

### Task B5b: El tope del auxilio cuenta los ingresos salariales por defecto (decisión 2, owner 2026-11-22: opción A)

**Files:** `src/Modules/Payroll/Modules.Payroll.Domain/CompanyPayrollSettings.cs` (`DefaultsFor`), su prueba unitaria.

`SalaryOnly` se conserva como opción (pagar el auxilio en más casos favorece al trabajador). Sólo cambia el valor por defecto.

- [ ] **Step 1: RED.** Prueba unitaria `DefaultsCountSalaryIncomeTowardTheTransportCap` (en el archivo de pruebas del dominio de
  settings que exista; si no hay, `tests/Modules/Payroll/Modules.Payroll.UnitTests/CompanyPayrollSettingsTests.cs`):
  `CompanyPayrollSettings.DefaultsFor().TransportCapBase` es `TransportCapBase.SalaryAndSalaryIncome`. Run filtrado. Expected: falla.
- [ ] **Step 2: GREEN.** `DefaultsFor()` → `new(TransportFrequency.Biweekly, TransportCapBase.SalaryAndSalaryIncome, OvertimeBase.SalaryOnly, Exonerated: true)`.
  Run la unitaria de Payroll completa (no integración): las goldens fijan sus propios settings en `EngineTestData.cs:87` y no
  cambian. Si alguna integración de settings lee el valor por defecto (`GetPayrollSettings`), correrla filtrada por nombre.
- [ ] **Step 3: Commit** — `fix(payroll): count salary income toward the transport cap by default`

### Task B6: Verificación final

**Files:** ninguno nuevo; sólo lo que la verificación obligue a corregir.

- [ ] **Step 1: Formato y build.** Run: `dotnet restore Backend.slnx --locked-mode`;
  `dotnet format Backend.slnx --verify-no-changes`; `dotnet build Backend.slnx --no-restore`. Expected: sin errores.
- [ ] **Step 2: Clases de integración tocadas, una vez** (fin del bloque): `--filter "FullyQualifiedName~PayrollInputsApiTests"`,
  luego `--filter "FullyQualifiedName~PayrollSettingsApiTests"`, luego `--filter "FullyQualifiedName~RecurringConceptsApiTests"`,
  cada una sobre `tests/Modules/Payroll/Modules.Payroll.IntegrationTests` con `-- xUnit.MaxParallelThreads=4`, en fila.
- [ ] **Step 3: Suite antes de mergear a `develop`**, una sola vez y en fila (mirar antes
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`), validando cada ruta con `Test-Path` antes (una ruta vacía corre
  la solución entera), cada una con `-- xUnit.MaxParallelThreads=4`:
  `tests/Modules/Payroll/Modules.Payroll.UnitTests`, `tests/Modules/Payroll/Modules.Payroll.IntegrationTests`,
  `tests/Bootstrapper/Bootstrapper.UnitTests` (tocado por B4) y `tests/ArchitectureTests/ArchitectureTests`. Hr no se toca: sus
  proyectos no corren. Expected: `Passed!` en todos; si algo falla, se reporta con su salida literal. No se repite en `develop`.
- [ ] **Step 4: Contrato contra código.** Comprobar que no hay códigos nuevos en `PayrollErrorCodes` (sólo se reusan
  `payroll.company.inactive` y `payroll.legal_parameter.missing`), que `ApiExceptionHandler.cs` no cambió, que no hay migración
  nueva en `src/Modules/Payroll/Modules.Payroll.Infrastructure/Persistence/Migrations/` y que `PayrollEngine.Version` es
  `2026.11.3`.
- [ ] **Step 5: Commit** (sólo si hubo correcciones) — `fix(payroll): <lo corregido>`

> **Cierre:** revisión fuerte de toda la rama (modelo más capaz) y entrega para merge a `develop` cuando el owner lo pida. Al
> mergear, en `develop`: `docs/estado.md` (fila de nómina y Registro). El frontend tiene su propio plan (spec, sección
> «Frontend»): copy de `payroll.company.inactive` y configuración en sólo lectura de una empresa inactiva.

## Fuera de este plan

- Decisiones 7, 8 y 11: sin cambio (ver spec).
- ARL en 0 durante vacaciones e incapacidades y descansos remunerados en la base parafiscal: proyecto 2 (ausencias).
- Indicador por empleado del auxilio pagado de más: el aviso va en la nota de la línea; un indicador en la lista sería otro cambio.
- Bloquear conceptos y recurrentes de una empresa inactiva: la decisión 10 sólo cubre la configuración.
- Lo «Pendiente del owner» del spec.
