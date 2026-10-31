# Préstamos — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El módulo de tenant `loans`: registra préstamos «sólo interés» a un cliente, genera en cada corte una cuota con el interés del período calculado por días, deja marcar cuotas como pagadas y registra abonos a capital como acción aparte, sin imputación automática de pagos.

**Architecture:** Módulo nuevo `src/Modules/Loans/*` con las cuatro capas de `ServiceContracts`. El dominio es puro: `LoanCalendar` (períodos), `LoanInterestCalculator` (tramos), `LoanCapitalLedger` (capital a una fecha), `LoanInstallmentGenerator` y `LoanCapitalOperations` (reglas del abono). La cuota queda fija al generarse. Customers, Tenancy, Identity y Authorization entran por puertos de `Modules.Loans.Application` con adaptadores en `Bootstrapper`. Un worker diario por tenant genera lo cerrado, y los handlers que dependen del período abierto hacen el mismo alcance antes de decidir.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql (`postgres:18-alpine`), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-10-31-prestamos-design.md`](../specs/2026-10-31-prestamos-design.md). Es autoridad: sus decisiones no se reabren. El modelo es **cuotas generadas que se marcan pagadas, completas, más abonos a capital separados**.

**Fuera de alcance:** el frontend (plan propio, escrito sólo desde § Contrato HTTP), el script SQL de Novasteel, `docs/estado.md` (se toca en `develop` al mergear).

---

## Antes de empezar

- **Los comandos de este repo son PowerShell** cuando se entregan al developer: sin `&&`, una línea por comando, `$env:VAR = "valor"` en línea aparte.
- **`Api.exe` tiene que estar detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`, o fallan por archivo bloqueado: `Stop-Process -Name Api -ErrorAction SilentlyContinue`.
- **Las pruebas de integración usan Testcontainers y exigen Docker.** Antes de `dotnet test` de un `*.IntegrationTests`: `$env:Seed__Enabled = "false"` en línea aparte.
- **Por tarea se corren sólo las clases tocadas, con `--filter`.** La suite completa tarda decenas de minutos y no se corre por tarea; en la Task 10 se corren los proyectos afectados.
- Worktree y rama: `qep-backend-worktrees/prestamos`, `feature/prestamos`. `git branch --show-current` justo antes de cada commit; si dice `main`, frena.
- El spec y este plan llegan sin commitear: entran en el primer commit de la Task 1.
- Commits: conventional commits en inglés, **sin** `Co-Authored-By` ni atribución de IA, aunque el harness la sugiera (`CLAUDE.md`).
- `packages.lock.json`: cada proyecto nuevo trae el suyo, y agregar una referencia cambia los de quienes dependen de él. Tras tocar referencias, `dotnet restore Backend.slnx` y se commitean **todos** los locks modificados; el `Dockerfile` restaura con `--locked-mode` y falla con `NU1004`.
- Migraciones: `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- Nunca imprimir secretos.

## Global Constraints

- Clave de módulo `loans`, última en `TenantModuleKeys.All`, `Dependencies[Loans] = [Customers]`, fuera de `DefaultForNewTenants`: «se activa por tenant, como `billing`».
- Permisos `loans.loan.read` y `loans.loan.manage`, con `RequiredModules: [ModuleKeys.Loans]`, en los roles `admin` y `billing`, en ningún otro (el spec no nombra roles: se sigue a `ServiceContracts`).
- Rutas bajo `/api/v1/tenants/{tenantId:guid}/loans`. El handler revalida tenant y permiso; **403, nunca 404**, para otro tenant.
- `Modules.Loans.Application` referencia sólo `BuildingBlocks.*` y `Modules.Tenancy.Application`; nada de EF Core ni Npgsql.
- Esquema `loans`. «Dinero en `numeric(18,2)` más moneda de 3 letras». «Tasa en `numeric(7,4)`, mayor que 0 y hasta 100». Índice único por `(LoanId, PeriodStart)`.
- «Interés de un tramo = capital × tasa mensual × días del tramo ÷ días del período.»
- «Cada tramo se redondea a 2 decimales y la cuota es la suma de sus tramos.» `MidpointRounding.AwayFromZero`, como `DocumentTotals`. Se multiplica antes de dividir: `principal * rate * days / (100m * periodDays)`.
- «El capital baja desde el día del abono inclusive.»
- «No existe el estado "vencida": la cuota está pendiente o pagada.» «Una cuota se paga completa.»
- «Que cambie el monto de una cuota ya generada sin una acción explícita del usuario» no debe pasar: ningún camino escribe `Amount` después de `Generate`.
- «Que el navegador calcule interés o totales» no debe pasar: los agregados y las reglas de la pantalla viajan en el DTO. Los totales nunca se mezclan entre monedas.
- Sin avisos: el worker no envía nada.
- Auditoría en el handler, en la misma transacción, por `ILoansAuditPublisher`, con la fuente `loans`.
- `DateOnly` es `yyyy-MM-dd`; «hoy» es `ITenantClock.Today`, nunca UTC; los opcionales viajan como `null`, nunca ausentes; los enums, con su nombre.
- Comentarios en el código: el **por qué**, en español, tuteando; identificadores en inglés.

## Review Focus

1. **Abono el último día del período.** Un abono del 31 de octubre tiene que caber en el período abierto; por eso octubre se genera recién el 1 de noviembre, aunque el spec diga «en el corte». Task 8 `ACapitalPaymentOnTheLastDayOfThePeriodIsAccepted`, Task 9 `APeriodIsNotGeneratedOnItsLastDay`.
2. **Abono con un período cerrado que el worker todavía no generó** (1 de noviembre, 00:05). Sin alcance, un abono fechado en octubre cambiaría una cuota que aún no existe. Se genera, se guarda y se rechaza por cuota pendiente. Task 8 `ACapitalPaymentOverAClosedUngeneratedPeriodGeneratesItAndIsRejected`.
3. **Abono fechado antes de otro ya registrado.** El spec lo permite (cualquier fecha del período abierto): el saldo se recalcula en orden de fecha y «el más reciente» es el de fecha mayor. Task 2 `ABackdatedPaymentRebalancesTheLaterOnes`.
4. **Saldar el primer día del período abierto.** No hay interés causado: no se genera cuota de 0, el préstamo queda `Settled` y ese abono sí se puede anular. Task 2 `SettlingOnTheFirstDayOfTheOpenPeriodCreatesNoInstallment`, Task 8 `VoidingAZeroInterestSettlementReactivatesTheLoan`.
5. **Quitar el pago de la última cuota de un préstamo inactivo.** Vuelve a `Active` con capital 0 y no genera nada más. Task 7 `RemovingThePaymentReactivatesASettledLoan`, Task 9 `ALoanWithoutCapitalGeneratesNothing`.

---

## Contrato HTTP

Esta sección es completa y autosuficiente: el plan del frontend se escribe sólo desde aquí.

**Base:** `/api/v1/tenants/{tenantId}/loans`. El spec abrevia `/loans`. El tenant va en la ruta **y** en `X-Tenant-Id`, como en Contratos de servicio; si no coinciden, 403. Las escrituras llevan `X-Qep-Client: web`, como todo POST, PUT y DELETE.

**Convenciones:** `date` es `yyyy-MM-dd`; `datetime` es ISO-8601; `money` y `rate` son números JSON con hasta 2 y 4 decimales; `T?` viaja como `null` y nunca ausente. Módulo `loans`, depende de `customers`, apagado para tenants nuevos.

**Enums:** `LoanStatus` = `Active | Settled` (en pantalla «Activo» e «Inactivo»). `LoanType` = `InterestOnly`. `CutoffRule` = `EndOfMonth | DisbursementDay`. `InstallmentStatus` = `Pending | Paid`. `CapitalPaymentBlock` = `PendingInstallments | PeriodClosing | NoCapital`.

### Objetos compartidos

| Objeto | Campos |
| --- | --- |
| `CustomerRef` | `id: uuid`, `name: string`, `phoneE164: string?`, `email: string?` |
| `MemberRef` | `memberId: uuid`, `displayName: string`, `isActive: bool` |
| `CurrencyAmount` | `currency: string`, `amount: money`. Las listas van en orden de catálogo y sin monedas en cero |
| `Segment` | `start: date`, `end: date`, `principal: money`, `days: int`, `interest: money` |
| `Payment` | `paidOn: date`, `note: string?`, `paidBy: MemberRef?`, `paidAt: datetime` |

**`Loan`**

| Campo | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid | |
| `customer` | `CustomerRef` | `name` = «Sin nombre» si el cliente ya no se resuelve |
| `name` | string | |
| `type` | `LoanType` | |
| `principalAmount` | money | Capital desembolsado |
| `outstandingPrincipal` | money | Capital pendiente |
| `currency` | string | |
| `monthlyRatePercent` | rate | `1.5` es 1,5 % |
| `disbursedOn` | date | |
| `cutoffRule` | `CutoffRule` | |
| `assignee` | `MemberRef?` | |
| `note` | string? | |
| `status` | `LoanStatus` | |
| `pendingCount` | int | Cuotas pendientes |
| `pendingAmount` | money | «Por cobrar» |
| `accruedToDate` | money | Interés causado a hoy, período abierto; 0 sin capital. No se debe todavía |
| `lastGeneratedPeriodEnd` | date? | |
| `nextCutoffDate` | date? | Último día del período que contiene hoy, según `cutoffRule`. Lo calcula el backend, nunca el frontend. `null` si `status` es `Settled`, y también en un préstamo `Active` sin capital: ya no tiene más cortes |
| `version` | int | Va en `If-Match` |
| `createdAt`, `updatedAt` | datetime | |

**`Installment`**

| Campo | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid | |
| `periodStart`, `periodEnd` | date | En un primer período parcial, `periodStart` es el desembolso. En la cuota de saldar, `periodEnd` es la fecha del abono y el último tramo termina el día anterior |
| `periodDays` | int? | Divisor del cálculo: días del período completo. `null` si `isImported` |
| `amount` | money | Suma de `segments`, salvo `isImported` |
| `currency` | string | |
| `status` | `InstallmentStatus` | |
| `isImported` | bool | Sin desglose: `segments` viene vacío |
| `payment` | `Payment?` | |
| `segments` | `Segment[]` | En orden de fecha |

**`CapitalPayment`**: `id: uuid`, `amount: money`, `paidOn: date`, `note: string?`, `balanceAfter: money`, `voidedAt: datetime?`, `voidReason: string?`, `createdAt: datetime`.

**`LoanDetail`**

| Campo | Tipo | Nota |
| --- | --- | --- |
| `loan` | `Loan` | |
| `installments` | `Installment[]` | Del período más reciente al más viejo |
| `capitalPayments` | `CapitalPayment[]` | Del más reciente al más viejo, anulados incluidos |
| `currentPeriod` | objeto? | `{ start: date, end: date, days: int, accruedAmount: money, segments: Segment[] }`; `null` sin capital. `accruedAmount` = `loan.accruedToDate` |
| `capitalPaymentBlock` | `CapitalPaymentBlock?` | Por qué no se ofrece abonar; `null` si se puede. `PeriodClosing`: hay un período cerrado sin cuota, se resuelve en minutos |
| `capitalPaymentWindow` | objeto? | `{ from: date, to: date }`, rango válido de la fecha; no nulo sólo si `capitalPaymentBlock` es `null` |
| `voidableCapitalPaymentId` | uuid? | El único abono que hoy se puede anular |
| `canEditTerms` | bool | Sin cuotas ni abonos; si es `false`, sólo nombre, encargado y nota |
| `canDelete` | bool | Sin cuotas pagadas ni abonos |

Un abono anulado cuenta como abono para `canEditTerms` y `canDelete`: su fila no se borra.

### Rutas

| Método y ruta | Permiso | Respuesta OK |
| --- | --- | --- |
| `GET /loans?status&search&page&pageSize` | read | 200 `LoanPage` |
| `POST /loans` | manage | 201 `LoanDetail` |
| `GET /loans/assignees` | read | 200 `{ items: MemberRef[] }` |
| `GET /loans/installments?search&page&pageSize` | read | 200 `InstallmentPage` |
| `GET /loans/{loanId}` | read | 200 `LoanDetail` |
| `PUT /loans/{loanId}` + `If-Match` | manage | 200 `LoanDetail` |
| `DELETE /loans/{loanId}` | manage | 204 |
| `POST /loans/{loanId}/installments/{installmentId}/pay` | manage | 200 `LoanDetail` |
| `PUT /loans/{loanId}/installments/{installmentId}/payment` | manage | 200 `LoanDetail` |
| `DELETE /loans/{loanId}/installments/{installmentId}/payment` | manage | 200 `LoanDetail` |
| `POST /loans/{loanId}/capital-payments/preview` | manage | 200 `CapitalPaymentPreview` |
| `POST /loans/{loanId}/capital-payments` | manage | 201 `LoanDetail` |
| `POST /loans/{loanId}/capital-payments/{paymentId}/void` | manage | 200 `LoanDetail` |

`GET /loans/assignees` no está en la tabla del spec y hace falta: el formulario pide encargado y `/billing/assignees` exige otro módulo y otro permiso. Devuelve las membresías activas con `loans.loan.manage`, por nombre. El selector de cliente sigue siendo `customers`: `admin` y `billing` tienen `customers.customer.read`.

**Cuerpos**

| Ruta | Cuerpo |
| --- | --- |
| `POST /loans` | `customerId: uuid`, `name: string` (1..150), `principalAmount: money` (> 0), `currency: string?` (`null` = moneda del tenant), `monthlyRatePercent: rate` (> 0 y ≤ 100), `disbursedOn: date` (desde 2000-01-01 hasta hoy), `cutoffRule: CutoffRule`, `assigneeMemberId: uuid?`, `note: string?` (≤ 1000). Sin `type`: es `InterestOnly` |
| `PUT /loans/{id}` | Los mismos campos, todos. Con `canEditTerms = false`, `customerId`, `principalAmount`, `currency`, `monthlyRatePercent`, `disbursedOn` y `cutoffRule` deben llegar iguales |
| `pay`, `PUT …/payment` | `paidOn: date`, `note: string?` (≤ 1000) |
| `preview`, `POST capital-payments` | `amount: money` (> 0), `paidOn: date`. `POST` suma `note: string?` (≤ 1000) |
| `void` | `reason: string` (1..500) |

`DELETE …/payment` y `DELETE /loans/{id}` no llevan cuerpo. Sólo `PUT /loans/{id}` lleva `If-Match` (`"<version>"`): sin él 428, viejo 412.

**`GET /loans`:** `status` = `active` (default) | `settled`, sin distinguir mayúsculas. `search` busca en el nombre del cliente y del préstamo. `page` desde 1, `pageSize` 25 por defecto, tope 100. Orden: `disbursedOn` descendente.

**`LoanPage`:** `items: Loan[]`, `total: int`, `page: int`, `pageSize: int`, `summary`. El `summary` es del tenant entero, sin filtros:

| Campo | Tipo |
| --- | --- |
| `activeCount`, `settledCount` | int |
| `pendingInstallmentCount` | int |
| `outstandingPrincipalByCurrency` | `CurrencyAmount[]` |
| `pendingAmountByCurrency` | `CurrencyAmount[]` |
| `accruedToDateByCurrency` | `CurrencyAmount[]` |

`pendingInstallmentCount` cuenta las cuotas pendientes de todo el tenant y no cambia con `status`, `search` ni la página.

**`GET /loans/installments`:** sólo cuotas pendientes. `search` busca en cliente y préstamo. Mismos `page` y `pageSize`. Orden: `periodEnd` ascendente.

**`InstallmentPage`:** `items: InstallmentRow[]`, `total`, `page`, `pageSize`, `summary`.

- `InstallmentRow`: `id: uuid`, `loanId: uuid`, `loanName: string`, `customer: CustomerRef`, `assignee: MemberRef?`, `periodStart: date`, `periodEnd: date`, `amount: money`, `currency: string`, `isImported: bool`.
- `summary`, del tenant entero: `pendingCount: int`, `pendingAmountByCurrency: CurrencyAmount[]`, `oldest: { periodEnd: date, amount: money, currency: string, customerName: string }?`.

**`CapitalPaymentPreview`** (no escribe; con los mismos errores que `POST capital-payments`):

| Campo | Tipo | Nota |
| --- | --- | --- |
| `resultingPrincipal` | money | |
| `settles` | bool | Lleva el capital a cero |
| `finalInstallment` | objeto? | `{ periodStart: date, periodEnd: date, periodDays: int, amount: money, currency: string, segments: Segment[] }`. `null` si no salda o si no hay interés causado (saldar el primer día del período abierto) |

### Errores

ProblemDetails con `code` y `traceId`; el 422 de validación suma `errors` por campo. Se mapean por `code`.

| Status | `code` | Cuándo |
| --- | --- | --- |
| 401 | el de autenticación de la plataforma | Sin sesión |
| 403 | `authorization.denied` | Sin permiso, o tenant de la ruta distinto del activo |
| 403 | sin código o `tenancy.module_not_enabled` | Módulo apagado |
| 404 | `loans.loan.not_found` | |
| 404 | `loans.installment.not_found` | |
| 404 | `loans.capital_payment.not_found` | |
| 412 | `concurrency.conflict` | `If-Match` viejo, o dos escrituras chocaron: relee y reintenta |
| 422 | `validation.failed` | Con `errors`: campos ausentes, formato, topes del cuerpo y `status` inválido |
| 422 | `loans.customer.not_found` | Crear o cambiar cliente |
| 422 | `loans.customer.inactive` | |
| 422 | `loans.currency.unknown` | |
| 422 | `loans.assignee.invalid` | Inactivo o sin `loans.loan.manage` |
| 422 | `loans.loan.invalid` | Defensa del dominio; el validador responde antes |
| 422 | `loans.loan.disbursed_in_future` | |
| 422 | `loans.loan.terms_locked` | `PUT` cambia condiciones con movimiento |
| 422 | `loans.loan.has_movement` | `DELETE` con cuota pagada o abono |
| 422 | `loans.installment.not_pending` | Pagar dos veces |
| 422 | `loans.installment.not_paid` | Corregir o quitar sin pago |
| 422 | `loans.payment.date_in_future` | |
| 422 | `loans.payment.note_too_long` | Defensa del dominio; el validador responde antes |
| 422 | `loans.capital_payment.pending_installments` | Quedan cuotas pendientes |
| 422 | `loans.capital_payment.exceeds_balance` | Monto mayor al capital, o capital en cero |
| 422 | `loans.capital_payment.date_out_of_period` | Fecha fuera de `capitalPaymentWindow` |
| 422 | `loans.capital_payment.already_voided` | |
| 422 | `loans.capital_payment.not_latest` | No es el abono más reciente |
| 422 | `loans.capital_payment.installment_generated` | Ya se generó una cuota después |
| 428 | `precondition.if_match_required` | |

Anular un abono no exige `If-Match`, y pagar dos veces es 422, no 412, como en los cobros.

### Permisos

| Permiso | Permite |
| --- | --- |
| `loans.loan.read` | Listas, detalle y encargados |
| `loans.loan.manage` | Crear, editar, eliminar, pagar y abonar. Sin él, las acciones se ocultan |

---

### Task 1: El cálculo puro: períodos y tramos

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Domain/Modules.Loans.Domain.csproj` (copia del de ServiceContracts), `LoanCutoffRule.cs`, `LoanPeriod.cs`, `LoanCalendar.cs`, `CapitalChange.cs`, `InterestSegment.cs`, `LoanInterestCalculator.cs`
- Create: `tests/Modules/Loans/Modules.Loans.UnitTests/Modules.Loans.UnitTests.csproj` (copia; referencia sólo Domain por ahora), `LoanCalendarTests.cs`, `LoanInterestCalculatorTests.cs`
- Modify: `Backend.slnx:146-154`: carpetas `/src/Modules/Loans/` y `/tests/Modules/Loans/`

**Interfaces:**
- Produces:
  - `public enum LoanCutoffRule { EndOfMonth, DisbursementDay }`
  - `public sealed record LoanPeriod(DateOnly Start, DateOnly End) { public int Days { get; } }`, ambos extremos inclusive.
  - `public static class LoanCalendar { public static LoanPeriod PeriodContaining(LoanCutoffRule rule, DateOnly disbursedOn, DateOnly date); }`
  - `public sealed record CapitalChange(DateOnly EffectiveOn, decimal BalanceAfter);`
  - `public sealed record InterestSegment(DateOnly Start, DateOnly End, decimal Principal, int Days, decimal Interest);`
  - `public static class LoanInterestCalculator { public static IReadOnlyList<InterestSegment> Segments(LoanPeriod period, DateOnly from, DateOnly to, decimal openingPrincipal, IReadOnlyList<CapitalChange> changes, decimal monthlyRatePercent); public static decimal Total(IEnumerable<InterestSegment> segments); }`

- [ ] **Step 1: Write the failing tests**

```csharp
// LoanCalendarTests. Regla 6 del spec: el inicio ajustado es el del ancla, no el del período anterior.
[Theory]
[InlineData("EndOfMonth", "2026-10-10", "2026-10-20", "2026-10-01", "2026-10-31", 31)]
[InlineData("EndOfMonth", "2026-02-10", "2026-02-10", "2026-02-01", "2026-02-28", 28)]
[InlineData("DisbursementDay", "2026-10-12", "2026-10-12", "2026-10-12", "2026-11-11", 31)]
[InlineData("DisbursementDay", "2026-10-12", "2026-11-11", "2026-10-12", "2026-11-11", 31)]
[InlineData("DisbursementDay", "2026-01-31", "2026-02-10", "2026-01-31", "2026-02-27", 28)]
[InlineData("DisbursementDay", "2026-01-31", "2026-02-28", "2026-02-28", "2026-03-30", 31)]
[InlineData("DisbursementDay", "2026-01-31", "2026-03-31", "2026-03-31", "2026-04-29", 30)]
[InlineData("DisbursementDay", "2026-01-31", "2026-04-30", "2026-04-30", "2026-05-30", 31)]
[InlineData("DisbursementDay", "2028-01-31", "2028-02-10", "2028-01-31", "2028-02-28", 29)] // año bisiesto
[InlineData("DisbursementDay", "2028-01-31", "2028-02-29", "2028-02-29", "2028-03-30", 31)]
public void PeriodContainingFollowsTheCutoffRule(string rule, string disbursedOn, string date, string start, string end, int days);

// LoanInterestCalculatorTests. `changes` es "fecha:saldo,fecha:saldo".
[Theory]
// Regla 4 del spec, el ejemplo de octubre.
[InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-31", "100000000", "2026-10-15:60000000", "1.5",
    "2026-10-01..2026-10-14:100000000:14:677419.35|2026-10-15..2026-10-31:60000000:17:493548.39", "1170967.74")]
// Primer período parcial: el día del desembolso cuenta y el divisor es el período completo.
[InlineData("2026-10-01", "2026-10-31", "2026-10-10", "2026-10-31", "100000000", "", "1.5",
    "2026-10-10..2026-10-31:100000000:22:1064516.13", "1064516.13")]
// Febrero: el divisor es 28.
[InlineData("2026-02-01", "2026-02-28", "2026-02-01", "2026-02-28", "100000000", "2026-02-15:60000000", "1.5",
    "2026-02-01..2026-02-14:100000000:14:750000.00|2026-02-15..2026-02-28:60000000:14:450000.00", "1200000.00")]
// Desembolso un 31: 31 ene al 27 feb, 28 días.
[InlineData("2026-01-31", "2026-02-27", "2026-01-31", "2026-02-27", "100000000", "", "1.5",
    "2026-01-31..2026-02-27:100000000:28:1500000.00", "1500000.00")]
// Desembolso un 31, segundo período: 28 feb al 30 mar, 31 días, con abono.
[InlineData("2026-02-28", "2026-03-30", "2026-02-28", "2026-03-30", "100000000", "2026-03-10:60000000", "1.5",
    "2026-02-28..2026-03-09:100000000:10:483870.97|2026-03-10..2026-03-30:60000000:21:609677.42", "1093548.39")]
// Saldar el 15: se calcula hasta el 14; el cambio a cero queda fuera del rango.
[InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-14", "100000000", "2026-10-15:0", "1.5",
    "2026-10-01..2026-10-14:100000000:14:677419.35", "677419.35")]
// Dos abonos el mismo día: gana el último saldo, sin tramo de cero días.
[InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-31", "100000000", "2026-10-15:90000000,2026-10-15:60000000", "1.5",
    "2026-10-01..2026-10-14:100000000:14:677419.35|2026-10-15..2026-10-31:60000000:17:493548.39", "1170967.74")]
// Abono el primer día: un solo tramo con el capital nuevo.
[InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-31", "100000000", "2026-10-01:60000000", "1.5",
    "2026-10-01..2026-10-31:60000000:31:900000.00", "900000.00")]
// Causado a hoy, 20 de octubre.
[InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-20", "100000000", "2026-10-15:60000000", "1.5",
    "2026-10-01..2026-10-14:100000000:14:677419.35|2026-10-15..2026-10-20:60000000:6:174193.55", "851612.90")]
// Punto medio: 0,505 sube a 0,51; el redondeo bancario daría 0,50.
[InlineData("2026-04-01", "2026-04-30", "2026-04-01", "2026-04-30", "1.01", "", "50",
    "2026-04-01..2026-04-30:1.01:30:0.51", "0.51")]
public void SegmentsSplitThePeriodAndAddUp(string start, string end, string from, string to, string opening, string changes, string rate, string expectedSegments, string expectedTotal);

[Fact]
public void AnEmptyRangeHasNoSegments(); // from > to: saldar el primer día. Resultado vacío, Total 0.
```

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter "FullyQualifiedName~LoanCalendarTests|FullyQualifiedName~LoanInterestCalculatorTests"` → FAIL: no compila, los tipos no existen.

- [ ] **Step 3: Implement** — `LoanCalendar.PeriodContaining`:

```csharp
// EndOfMonth: del día 1 al último del mes de `date`.
// DisbursementDay: anchor = disbursedOn.Day; startIn(y, m) = día min(anchor, días del mes).
// start = startIn(mes de date); si date < start, start = startIn(mes anterior).
// end = startIn(mes siguiente a start) - 1 día.
```

`Segments` recorre los cambios en orden dentro de `(from, to]`, aplica antes los de `EffectiveOn <= from` sobre `openingPrincipal`, resume varios cambios del mismo día en el último, ignora los posteriores a `to` y omite tramos con capital 0. `Interest = Math.Round(principal * rate * days / (100m * period.Days), 2, MidpointRounding.AwayFromZero)`. Lleva el `<summary>` con la regla del spec que cumple.

- [ ] **Step 4: Run to verify GREEN** — el filtro anterior → PASS.

- [ ] **Step 5: Commit** — incluye el spec y este plan: `feat(loans): add period calendar and interest segments`

---

### Task 2: Dominio: préstamo, cuota, abono y generación

**Files:**
- Create en `src/Modules/Loans/Modules.Loans.Domain/`: `LoanId.cs`, `LoanStatus.cs`, `LoanType.cs`, `LoanInstallmentStatus.cs`, `LoansDomainException.cs`, `LoansErrorCodes.cs`, `LoanTerms.cs`, `Loan.cs`, `LoanInstallment.cs`, `LoanInstallmentSegment.cs`, `LoanCapitalPayment.cs`, `LoanCapitalLedger.cs`, `LoanInstallmentGenerator.cs`, `LoanCapitalOperations.cs`, `LoanAccrual.cs`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/LoanTests.cs`, `LoanInstallmentTests.cs`, `LoanInstallmentGeneratorTests.cs`, `LoanCapitalOperationsTests.cs`, `LoanAccrualTests.cs`, `LoanTestData.cs`

**Interfaces:**
- Consumes: todo lo de la Task 1.
- Produces:
  - `LoanId` como `ServiceContractId`. `LoansDomainException(string code, string message) : DomainException`.
  - `LoansErrorCodes`: una constante por cada `loans.*` de § Errores.
  - `public sealed record LoanTerms(Guid CustomerId, decimal PrincipalAmount, string Currency, decimal MonthlyRatePercent, DateOnly DisbursedOn, LoanCutoffRule CutoffRule);`
  - `Loan`: `Id`, `TenantId`, `CustomerId`, `Name`, `Type`, `PrincipalAmount`, `OutstandingPrincipal`, `Currency`, `MonthlyRatePercent`, `DisbursedOn`, `CutoffRule`, `AssigneeMemberId`, `Note`, `Status`, `LastGeneratedPeriodEnd`, `Version` (int), `CreatedAt`, `UpdatedAt`; `NameMaxLength = 150`, `NoteMaxLength = 1000`, `LoanTerms Terms { get; }`.
    - `static Loan Create(LoanId id, Guid tenantId, LoanTerms terms, string name, Guid? assigneeMemberId, string? note, DateOnly today, DateTimeOffset now)`
    - `void ChangeTerms(LoanTerms terms, DateOnly today, DateTimeOffset now)`; `void UpdateDetails(string name, Guid? assigneeMemberId, string? note, DateTimeOffset now)`
    - `LoanPeriod? NextPeriodToGenerate()`: `null` si `Settled` o capital 0; si no, `LoanCalendar.PeriodContaining` de `DisbursedOn` o de `LastGeneratedPeriodEnd + 1`.
    - `void MarkGenerated(DateOnly periodEnd, DateTimeOffset now)` y `void SetOutstanding(decimal principal, DateTimeOffset now)`, que suben `Version`: un abono o una edición en paralelo pierden con 412; `void RefreshStatus(int pendingCount, DateTimeOffset now)` (`Settled` si y sólo si capital 0 y `pendingCount == 0`); `void Touch(DateTimeOffset now)`.
    - `DateOnly? NextCutoffDate(DateOnly today)`: `LoanCalendar.PeriodContaining(CutoffRule, DisbursedOn, today).End`; `null` si `Settled` o capital 0.
  - `LoanInstallment`: `Id`, `TenantId`, `LoanId`, `PeriodStart`, `PeriodEnd`, `PeriodDays` (int?), `Amount`, `Currency`, `Status`, `IsImported`, `PaidOn`, `PaymentNote`, `PaidByMemberId`, `PaidAt`, `Version`, `CreatedAt`, `IReadOnlyList<LoanInstallmentSegment> Segments`; `PaymentNoteMaxLength = 1000`.
    - `static LoanInstallment Generate(Loan loan, DateOnly periodStart, DateOnly periodEnd, int periodDays, IReadOnlyList<InterestSegment> segments, DateTimeOffset now)`: `IsImported = false`, `Amount = Total(segments)`.
    - `void MarkPaid(DateOnly paidOn, string? note, Guid paidByMemberId, DateOnly today, DateTimeOffset now)`; `void UpdatePayment(DateOnly paidOn, string? note, DateOnly today, DateTimeOffset now)`; `void RemovePayment()`. Copia las reglas de `Charge`.
  - `LoanInstallmentSegment`: `Id`, `InstallmentId`, `Ordinal`, `StartOn`, `EndOn`, `Principal`, `Days`, `Interest`.
  - `LoanCapitalPayment`: `Id`, `TenantId`, `LoanId`, `Amount`, `PaidOn`, `Note`, `BalanceAfter`, `VoidedAt`, `VoidReason`, `CreatedAt`; `NoteMaxLength = 1000`, `VoidReasonMaxLength = 500`; `static Create(...)`, `void SetBalanceAfter(decimal)`, `void MarkVoided(string reason, DateTimeOffset now)`.
  - `LoanCapitalLedger`: `static IReadOnlyList<LoanCapitalPayment> InOrder(IEnumerable<LoanCapitalPayment> payments)` (sin anulados, por `PaidOn`, `CreatedAt`, `Id`); `static decimal OpeningBalance(Loan loan, IEnumerable<LoanCapitalPayment> payments, DateOnly date)`; `static IReadOnlyList<CapitalChange> ChangesFrom(IEnumerable<LoanCapitalPayment> payments, DateOnly date)`.
  - `LoanInstallmentGenerator`: `static IReadOnlyList<LoanInstallment> GenerateClosedPeriods(Loan loan, IReadOnlyList<LoanCapitalPayment> payments, DateOnly today, DateTimeOffset now)`.
  - `public sealed record CapitalPaymentOutcome(LoanCapitalPayment Payment, LoanInstallment? FinalInstallment);`
  - `LoanCapitalOperations`:
    - `static DateOnly OpenFrom(Loan loan)`: `max(inicio del período siguiente a LastGeneratedPeriodEnd, DisbursedOn)`. Se calcula con `LoanCalendar`, no con `NextPeriodToGenerate()`, que es `null` en un préstamo `Settled` y anular un abono lo necesita ahí.
    - `static CapitalPaymentOutcome Add(Loan loan, IReadOnlyList<LoanCapitalPayment> payments, decimal amount, DateOnly paidOn, string? note, int pendingCount, DateOnly today, DateTimeOffset now)`
    - `static void Void(Loan loan, IReadOnlyList<LoanCapitalPayment> payments, Guid paymentId, string reason, int pendingCount, DateTimeOffset now)`
    - `static LoanCapitalPayment? Voidable(Loan loan, IEnumerable<LoanCapitalPayment> payments)`
  - `public sealed record LoanAccrualResult(LoanPeriod Period, IReadOnlyList<InterestSegment> Segments, decimal Amount);` y `LoanAccrual`: `static LoanAccrualResult? ToDate(Loan loan, IEnumerable<LoanCapitalPayment> payments, DateOnly today)`: el período es `PeriodContaining(today)`, de `max(Start, DisbursedOn)` a `today`; `null` si `Settled` o capital 0.

- [ ] **Step 1: Write the failing tests** — `LoanTestData.Loan(...)` arma un préstamo de 100.000.000 al 1,5 % desembolsado el 2026-10-01, con `EndOfMonth`.

```csharp
// LoanInstallmentGeneratorTests
[Fact] public void AClosedPeriodBecomesOneInstallmentWithItsSegments();
// Octubre con abono de 40M el 15, today = 2026-11-01: 1170967.74, PeriodDays 31, dos tramos, LastGeneratedPeriodEnd 2026-10-31.
[Fact] public void TheOpenPeriodIsNotGenerated();
// today = 2026-10-31: ninguna cuota. El período cierra cuando today > End.
[Fact] public void ACatchUpGeneratesEveryMissedPeriodWithItsRealDates();
// today = 2027-01-15: oct, nov y dic, cada una 1500000.00 con su fecha real.
[Fact] public void AFirstPartialPeriodStartsOnTheDisbursement();
// EndOfMonth, 2026-10-10: PeriodStart 10-10, PeriodEnd 10-31, PeriodDays 31, Amount 1064516.13.
[Fact] public void AGeneratedInstallmentIgnoresLaterPayments();
// Generar octubre, abonar en noviembre: Amount sigue en 1500000.00.

// LoanCapitalOperationsTests
[Fact] public void ACapitalPaymentLowersTheBalanceAndRecordsIt();
[Fact] public void PendingInstallmentsBlockThePayment();     // loans.capital_payment.pending_installments
[Fact] public void AnAmountAboveTheBalanceIsRejected();      // loans.capital_payment.exceeds_balance
[Theory] // today = 2026-10-20: antes del desembolso y después de hoy → date_out_of_period
[InlineData("2026-09-30")] [InlineData("2026-10-21")]
public void ADateOutsideTheOpenPeriodIsRejected(string paidOn);
[Fact] public void ADateBeforeTheOpenPeriodIsRejected();     // octubre generado: paidOn 10-31 con today 11-05
[Fact] public void SettlingGeneratesTheLastInstallmentUpToTheDayBefore();
// Abono de 100M el 2026-10-15: FinalInstallment 677419.35, PeriodEnd 10-15, tramo hasta 10-14, PeriodDays 31; Status sigue Active.
[Fact] public void SettlingAfterAnEarlierPaymentAddsBothSegments();
// 40M el 10, 60M el 20: 435483.87 + 290322.58 = 725806.45.
[Fact] public void SettlingOnTheFirstDayOfTheOpenPeriodCreatesNoInstallment();
// Review Focus 4. FinalInstallment null, Status Settled, capital 0.
[Fact] public void ABackdatedPaymentRebalancesTheLaterOnes();
// Review Focus 3. 40M el 20 y luego 30M el 10: BalanceAfter 70M y 30M; octubre cierra en
// 435483.87 + 338709.68 + 174193.55 = 948387.10. Lo que ya está en un período cerrado no se toca.
[Fact] public void ABackdatedPaymentThatSettlesClosesOnTheLatestDate();
// 40M el 20 y luego 60M el 10: cierra el 20, tramos 100M 9 días y 40M 10 días.
[Fact] public void OnlyTheLatestPaymentCanBeVoided();        // not_latest, por fecha, no por orden de carga
[Fact] public void AVoidedPaymentRestoresTheBalanceAndKeepsItsRow();
[Fact] public void APaymentBeforeAGeneratedInstallmentCannotBeVoided(); // installment_generated
[Fact] public void ThePaymentThatGeneratedTheLastInstallmentCannotBeVoided(); // regla 7 del spec
[Fact] public void VoidingTwiceIsRejected();                 // already_voided

// LoanTests
[Fact] public void ALoanBecomesSettledOnlyWithoutCapitalAndWithoutPendingInstallments();
[Fact] public void AFutureDisbursementIsRejected();          // loans.loan.disbursed_in_future
[Theory]
[InlineData("EndOfMonth", "2026-10-10", "2026-10-20", "2026-10-31")]
[InlineData("DisbursementDay", "2026-10-12", "2026-10-20", "2026-11-11")]
[InlineData("DisbursementDay", "2026-01-31", "2026-02-10", "2026-02-27")]
[InlineData("EndOfMonth", "2026-10-10", "2026-10-31", "2026-10-31")] // hoy es el corte
public void TheNextCutoffFollowsTheCutoffRule(string rule, string disbursedOn, string today, string expected);
[Fact] public void ALoanWithoutCapitalHasNoNextCutoff();     // Active con capital 0 y Settled: null
[Theory] [InlineData("0")] [InlineData("100.0001")]
public void ARateOutsideTheRangeIsRejected(string rate);     // loans.loan.invalid
// LoanInstallmentTests: copia los casos de ChargeTests con los códigos loans.*, más
[Fact] public void RemovingThePaymentReturnsTheInstallmentToPending();
// LoanAccrualTests
[Fact] public void TheAccrualCoversThePeriodUpToToday();     // 2026-10-20 con abono el 15: 851612.90
[Fact] public void ThereIsNoAccrualWithoutCapital();
```

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests` → FAIL: no compila.

- [ ] **Step 3: Implement** — cada método con la firma de Interfaces. Lo que la firma no determina:

```csharp
// GenerateClosedPeriods
while (loan.NextPeriodToGenerate() is { } period && period.End < today)
{
    var from = Max(period.Start, loan.DisbursedOn);
    var segments = LoanInterestCalculator.Segments(period, from, period.End,
        LoanCapitalLedger.OpeningBalance(loan, payments, from),
        LoanCapitalLedger.ChangesFrom(payments, from), loan.MonthlyRatePercent);
    add LoanInstallment.Generate(loan, from, period.End, period.Days, segments, now);
    loan.MarkGenerated(period.End, now);
}

// Add: orden de validación pendingCount > 0, amount > OutstandingPrincipal, paidOn fuera de [OpenFrom, today].
// Después: crea el abono; recorre en orden los abonos con PaidOn >= OpenFrom desde OpeningBalance(OpenFrom),
// asignando BalanceAfter (los de períodos cerrados no se reescriben: pueden ser de la carga inicial);
// SetOutstanding(último saldo).
// Si el saldo es 0: closesOn = mayor PaidOn de la lista; período = PeriodContaining(closesOn);
// from = Max(período.Start, DisbursedOn); to = closesOn - 1 día. Con tramos no vacíos: Generate(loan, from, closesOn,
// período.Days, tramos, now) y MarkGenerated(closesOn). Vacíos: sin cuota.
// RefreshStatus(hay cuota final ? pendingCount + 1 : pendingCount, now).

// Voidable: el último de InOrder, sólo si PaidOn > LastGeneratedPeriodEnd (o éste es null).
// Void: not_found → already_voided → not_latest → installment_generated. Después rebalancea igual que Add,
// y RefreshStatus(pendingCount, now).
```

`LoanCapitalOperations.Add` y `Void` mutan los `payments` recibidos; la lista la pasa el handler. `Add` no agrega a repositorios: eso es del handler, y por eso el preview puede correr el mismo camino sin escribir.

- [ ] **Step 4: Run to verify GREEN** — `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests` → PASS.

- [ ] **Step 5: Commit** — `feat(loans): add loan, installment and capital payment domain`

---

### Task 3: La clave de módulo `loans` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:71-95`, `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:122`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddLoansModuleKey.cs` (+ Designer)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs:13,86-92`, `OperatorReadHandlerTests.cs:68`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs:105-113`, `OperatorConsoleApiTests.cs:347`, `SeedStartupTests.cs:144,177,239,266,346`

**Interfaces:**
- Produces: `TenantModuleKeys.Loans` (`"loans"`), `DependenciesOf(Loans) == [Customers]`.

- [ ] **Step 1: Write the failing tests** — agrega `"loans"` al final de las listas en orden de contrato, y entre `"developer"` y `"messaging"` en las ordenadas de `SeedStartupTests`; súmalo a los `is` de módulos apagados. `DeveloperIsLastHasNoDependenciesAndIsOffForNewTenants` deja de afirmar `All[^1]` y se renombra `DeveloperHasNoDependenciesAndIsOffForNewTenants`.

```csharp
[Fact]
public void LoansIsLastDependsOnCustomersAndIsOffForNewTenants()
{
    Assert.Equal("loans", TenantModuleKeys.All[^1].Value);
    Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Loans));
    Assert.DoesNotContain(TenantModuleKeys.Loans, TenantModuleKeys.DefaultForNewTenants);
}
```

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter FullyQualifiedName~TenantModuleKeysTests` → FAIL: `TenantModuleKeys` no tiene `Loans`.

- [ ] **Step 3: Implement** — la clave con su `<summary>` citando el spec, última en `All`, `[Loans] = [Customers]`. Agrega `'loans'` al CHECK de `TenancyDbContext.cs:122`. Genera `AddLoansModuleKey`, con la forma de `20261031042625_AddDeveloperModuleKey.cs`: su `Down` borra `WHERE module_key = 'loans'` antes de reponer el CHECK. Verifica que la migración sólo toque el CHECK.

- [ ] **Step 4: Run to verify GREEN** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"` → PASS. Luego `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests"` → PASS.

- [ ] **Step 5: Commit** — `feat(tenancy): add loans module key`

---

### Task 4: Persistencia

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/Modules.Loans.Application.csproj` (copia, sin `ClosedXML` en ningún lado), `ILoanRepository.cs`, `ILoanInstallmentRepository.cs`, `ILoanCapitalPaymentRepository.cs`, `ILoansUnitOfWork.cs`, `ILoansAuditPublisher.cs`, `LoansReadModels.cs`
- Create: `src/Modules/Loans/Modules.Loans.Infrastructure/Modules.Loans.Infrastructure.csproj` (copia, sin `ClosedXML`), `LoansDatabaseInitializer.cs`, `LoansInfrastructureExtensions.cs`
- Create en `.../Infrastructure/Persistence/`: `LoansDbContext.cs`, `LoansDbContextFactory.cs`, `LoanRepository.cs`, `LoanInstallmentRepository.cs`, `LoanCapitalPaymentRepository.cs`, `LoansUnitOfWork.cs`, `LoansAuditPublisher.cs`, `LikeSearch.cs`, `UniqueViolation.cs`, `TenantRun.cs`, `Migrations/<timestamp>_InitialLoans.cs`
- Create: `tests/Modules/Loans/Modules.Loans.IntegrationTests/Modules.Loans.IntegrationTests.csproj`, `LoansApiHarness.cs`, `LoansPersistenceTests.cs`
- Modify: `Backend.slnx`, `src/Api/Api.csproj:19-20`, `src/Bootstrapper/Bootstrapper.csproj:18-19`, `src/Api/Program.cs:339-346`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:1017`

**Interfaces:**
- Consumes: el dominio de la Task 2. Todo método recibe `tenantId`.
- Produces:
  - `ILoanRepository`: `void Add(Loan loan)`; `void Remove(Loan loan)`; `Task<Loan?> FindAsync(Guid tenantId, LoanId id, CancellationToken ct)` (con tracking); `Task<(IReadOnlyList<Loan> Items, int Total)> ListAsync(LoanListFilter filter, CancellationToken ct)`; `Task<IReadOnlyList<Loan>> FindManyAsync(Guid tenantId, IReadOnlyCollection<LoanId> ids, CancellationToken ct)`; `Task<IReadOnlyList<Loan>> ListAccruingAsync(Guid tenantId, CancellationToken ct)` (`Active`, capital > 0, sin tracking); `Task<LoanTotals> TotalsAsync(Guid tenantId, CancellationToken ct)`.
  - `ILoanInstallmentRepository`: `void AddRange(IEnumerable<LoanInstallment> installments)`; `Task<LoanInstallment?> FindAsync(Guid tenantId, LoanId loanId, Guid installmentId, CancellationToken ct)`; `Task<IReadOnlyList<LoanInstallment>> ListByLoanAsync(Guid tenantId, LoanId loanId, CancellationToken ct)` (con `Segments`, sin tracking, `PeriodStart` descendente); `Task<int> CountPendingAsync(Guid tenantId, LoanId loanId, CancellationToken ct)`; `Task<IReadOnlyDictionary<LoanId, PendingTotal>> PendingTotalsAsync(Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken ct)`; `Task<(IReadOnlyList<LoanInstallment> Items, int Total)> ListPendingAsync(PendingInstallmentFilter filter, CancellationToken ct)`; `Task<InstallmentTotals> PendingSummaryAsync(Guid tenantId, CancellationToken ct)`.
  - `ILoanCapitalPaymentRepository`: `void Add(LoanCapitalPayment payment)`; `Task<List<LoanCapitalPayment>> ListByLoanAsync(Guid tenantId, LoanId loanId, CancellationToken ct)` (con tracking, anulados incluidos); `Task<IReadOnlyList<LoanCapitalPayment>> ListByLoansAsync(Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken ct)` (sin tracking).
  - `LoansReadModels.cs`: `LoanListFilter(Guid TenantId, LoanStatus Status, string? SearchTerm, IReadOnlyCollection<Guid>? SearchCustomerIds, int Page, int PageSize)`; `PendingInstallmentFilter(Guid TenantId, string? SearchTerm, IReadOnlyCollection<Guid>? SearchCustomerIds, int Page, int PageSize)`; `PendingTotal(int Count, decimal Amount)`; `CurrencyTotal(string Currency, decimal Amount)`; `LoanTotals(int ActiveCount, int SettledCount, int PendingInstallmentCount, IReadOnlyList<CurrencyTotal> OutstandingByCurrency, IReadOnlyList<CurrencyTotal> PendingByCurrency)`; `OldestInstallment(LoanId LoanId, DateOnly PeriodEnd, decimal Amount, string Currency)`; `InstallmentTotals(int PendingCount, IReadOnlyList<CurrencyTotal> PendingByCurrency, OldestInstallment? Oldest)`.
  - `ILoansUnitOfWork.SaveChangesAsync(CancellationToken)`; `ILoansAuditPublisher.Publish(...)` con la firma de `IServiceContractsAuditPublisher`.
  - `services.AddLoansInfrastructure(configuration)`, `InitializeLoansDatabaseAsync(...)`.
  - `LoansApiHarness`: copia de `ServiceContractsApiHarness`, con `LoansUrl(tenantId)`, `SeedCustomerAsync`, `DeactivateCustomerAsync`, `SeedMemberAsync`, `SeedCallerMembershipAsync`, `CountAuditEntriesAsync`, `OpenAsync`, `CreateManager`, `CreateReader`, más un `MutableTenantClock` (copia del de `ServiceContractsReminderProcessorTests`) y `QepApiFactory(connectionString, configureServices)`.
  - `LoansDbContext` público, con `Loans`, `Installments` y `CapitalPayments` públicos.

- [ ] **Step 1: Write the failing tests** — `LoansPersistenceTests`, contra Postgres real:

```csharp
[Fact] public async Task ALoanRoundTripsWithItsInstallmentSegmentsAndPayments();
[Fact] public async Task TheSameLoanAndPeriodCannotHaveTwoInstallments();
// Assert: DbUpdateException con Postgres ConstraintName "IX_installments_loan_period".
[Fact] public async Task DeletingALoanCascadesToItsInstallmentsSegmentsAndPayments();
[Fact] public async Task TheDatabaseRejectsARateOutOfRange();   // CK_loans_monthly_rate
[Fact] public async Task AnImportedInstallmentNeedsNoPeriodDays(); // INSERT crudo is_imported = true, period_days NULL
[Fact] public async Task TheUnitOfWorkTranslatesADuplicatePeriodToAConcurrencyConflict();
// RequestConcurrencyException con Code "concurrency.conflict".
[Fact] public async Task TotalsNeverMixCurrencies();            // COP y USD: una línea por moneda
[Fact] public async Task TotalsCountThePendingInstallmentsOfTheWholeTenant(); // sin pagadas ni las de otro tenant
```

- [ ] **Step 2: Run to verify RED** — `$env:Seed__Enabled = "false"`, luego `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoansPersistenceTests` → FAIL: no compila.

- [ ] **Step 3: Implement `LoansDbContext`** — `Schema = "loans"`, `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`, historial `__ef_migrations_history` en `loans`. Enums como texto `varchar(16)`, `version` `bigint` y `IsConcurrencyToken()`:

| Tabla | Columnas | Restricciones |
| --- | --- | --- |
| `loans.loans` | `id`, `tenant_id`, `customer_id`, `name` (150), `type`, `principal_amount`, `outstanding_principal`, `currency` (3), `monthly_rate_percent` (7,4), `disbursed_on`, `cutoff_rule`, `assignee_member_id`, `note` (1000), `status`, `last_generated_period_end`, `version`, `created_at`, `updated_at` | `CK_loans_status`, `CK_loans_type`, `CK_loans_cutoff_rule`, `CK_loans_monthly_rate` (`> 0 AND <= 100`); `IX_loans_tenant_status`, `IX_loans_tenant_customer` |
| `loans.installments` | `id`, `tenant_id`, `loan_id`, `period_start`, `period_end`, `period_days`, `amount`, `currency`, `status`, `is_imported`, `paid_on`, `payment_note` (1000), `paid_by_member_id`, `paid_at`, `version`, `created_at` | `CK_installments_status`, `CK_installments_period_days` (`is_imported OR period_days IS NOT NULL`); único `IX_installments_loan_period (loan_id, period_start)`; `IX_installments_tenant_status_end`; FK a `loans` con cascada |
| `loans.installment_segments` | `id`, `installment_id`, `ordinal`, `start_on`, `end_on`, `principal`, `days`, `interest` | Único `(installment_id, ordinal)`; FK con cascada |
| `loans.capital_payments` | `id`, `tenant_id`, `loan_id`, `amount`, `paid_on`, `note` (1000), `balance_after`, `voided_at`, `void_reason` (500), `created_at` | `CK_capital_payments_amount` (`> 0`); `IX_capital_payments_loan_paid_on`; FK con cascada |
| `loans.tenant_runs` | `tenant_id`, `run_date`, `claimed_until`, `finished_at` | PK `(tenant_id, run_date)` |

Las FK son reales: viven dentro del esquema, y borrar un préstamo sin movimiento se lleva sus cuotas pendientes. Clientes y membresías son referencias blandas.

- [ ] **Step 4: Implement repositorios y unidad de trabajo** — `LikeSearch` y `UniqueViolation` son copias: los módulos no se ven entre sí. `LoansUnitOfWork` traduce `DbUpdateConcurrencyException` y `IX_installments_loan_period`, por nombre, a `RequestConcurrencyException("concurrency.conflict", ...)`: el worker pudo generar el período en el medio y el usuario sólo tiene que reintentar. La búsqueda es `customerIds.Contains(customer_id) OR ILIKE name`; en `ListPendingAsync`, por `EXISTS` sobre `loans`. Agrupa en SQL sobre el tipo anónimo, como `ChargeRepository.SummaryAsync`.

- [ ] **Step 5: Implement el cableado de datos** — `Modules.Loans.Infrastructure` en `Api.csproj`; `Modules.Loans.Application` e `Infrastructure` en `Bootstrapper.csproj`; `Modules.Loans.Application` en `Modules.Loans.UnitTests.csproj` lo agrega la Task 6; `services.AddLoansInfrastructure(configuration);` junto a `AddServiceContractsInfrastructure`; en `Program.cs`, `await app.Services.InitializeLoansDatabaseAsync(app.Lifetime.ApplicationStopping);` después de ApiAccess y antes de `RunQepSeedAsync`, con su comentario. Genera `InitialLoans`; revisa que cree el esquema `loans` y no toque `audit.entries`. Corre `dotnet restore Backend.slnx`.

- [ ] **Step 6: Run to verify GREEN** — el filtro del Step 2 → PASS.

- [ ] **Step 7: Commit** — `feat(loans): add persistence and initial migration`

---

### Task 5: Composición del módulo: permisos, adaptadores y encargados

**Files:**
- Create en `Modules.Loans.Application/`: `LoansPermissions.cs`, `LoansAuditActions.cs`, `LoansAuthorization.cs`, `LoansNotFound.cs`, `ILoansCustomerLookup.cs`, `ILoansMemberDirectory.cs`, `LoansDtos.cs`, `ListLoanAssignees.cs`
- Create: `src/Modules/Loans/Modules.Loans.Api/Modules.Loans.Api.csproj`, `LoansEndpoints.cs`
- Create: `src/Bootstrapper/LoansCustomerLookup.cs`, `src/Bootstrapper/LoansMemberDirectory.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`: handlers junto a `:330-390`; validadores `:1001`; puertos `:1248`; roles `admin :1489` y `billing :1583`; `PermissionDefinition :2132`; políticas `:2513`
- Modify: `src/Api/Program.cs:266`, `src/Api/Api.csproj`, `Backend.slnx`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj:38-41`
- Test: `tests/ArchitectureTests/ArchitectureTests/LoansLayerTests.cs`, `CompositionRootTests.cs:183-197`, `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansAccessApiTests.cs`

**Interfaces:**
- Consumes: `TenantModuleKeys.Loans`; `LoansApiHarness`.
- Produces:
  - `LoansPermissions.Read = "loans.loan.read"`, `LoansPermissions.Manage = "loans.loan.manage"`.
  - `LoansAuditActions`: `LoanCreated = "loans.loan.created"`, `LoanUpdated = "loans.loan.updated"`, `LoanDeleted = "loans.loan.deleted"`, `InstallmentPaid = "loans.installment.paid"`, `InstallmentPaymentUpdated = "loans.installment.payment_updated"`, `InstallmentPaymentRemoved = "loans.installment.payment_removed"`, `CapitalPaymentCreated = "loans.capital_payment.created"`, `CapitalPaymentVoided = "loans.capital_payment.voided"`; `Resources.Loan = "loan"`, `Resources.Installment = "installment"`, `Resources.CapitalPayment = "capital_payment"`.
  - `internal static class LoansAuthorization { Task EnsureAsync(IExecutionContext context, ITenantModules modules, Guid tenantId, string permission, CancellationToken ct); RequestForbiddenException Denied(); }`, copia de `ServiceContractsAuthorization`.
  - `LoansNotFound.Loan(Guid)`, `.Installment(Guid)`, `.CapitalPayment(Guid)`.
  - `public sealed record LoansCustomerRef(Guid Id, string Name, string? PhoneE164, string? Email, bool IsActive);`
  - `ILoansCustomerLookup`: `FindAsync`, `FindManyAsync`, `SearchIdsAsync`, las firmas de `IServiceContractsCustomerLookup`.
  - `public sealed record LoansMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage);`
  - `ILoansMemberDirectory`: `FindAsync(tenantId, memberIds, ct)`, `ListManagersAsync(tenantId, ct)`.
  - `LoansDtos.cs`: un record por objeto de § Contrato HTTP: `LoanCustomerDto`, `LoanMemberDto`, `LoanCurrencyAmountDto`, `LoanSegmentDto`, `LoanPaymentDto`, `LoanDto`, `LoanInstallmentDto`, `LoanCapitalPaymentDto`, `LoanCurrentPeriodDto`, `LoanPaymentWindowDto`, `LoanDetailDto`, `LoanSummaryDto`, `LoanPageDto`, `LoanInstallmentRowDto`, `LoanOldestInstallmentDto`, `LoanInstallmentSummaryDto`, `LoanInstallmentPageDto`, `LoanFinalInstallmentDto`, `LoanCapitalPaymentPreviewDto`, `LoanAssigneesDto`. Con la nota del BFF sobre `capitalPaymentBlock`, `capitalPaymentWindow` y `voidableCapitalPaymentId`.
  - `MapLoansEndpoints(this IEndpointRouteBuilder)`, grupo `/api/v1/tenants/{tenantId:guid}/loans`, tag `Loans`; `ListLoanAssigneesQuery(Guid TenantId) : IQuery<LoanAssigneesDto>`.

- [ ] **Step 1: Write the failing tests**

`LoansLayerTests.cs`: copia de `ServiceContractsLayerTests.cs` con los tipos `Loan`, `LoansPermissions`, `LoansInfrastructureExtensions`, `LoansEndpoints`.

`CompositionRootTests`: `PermissionDiscoveryFindsTheSeventyConstants` pasa a `PermissionDiscoveryFindsTheSeventyTwoConstants`, con `Assert.Equal(72, ...)` y su línea en el comentario.

```csharp
[Fact]
public void TheLoansPermissionsRequireTheLoansModuleAndGoToAdminAndBilling();
// Categoría "Préstamos", RequiredModules [ModuleKeys.Loans]; en admin y billing, no en advisor ni cashier.

// LoansAccessApiTests
[Fact] public async Task WithoutThePermissionTheAssigneesAre403();
[Fact] public async Task AnotherTenantInTheRouteIs403Never404();
[Fact] public async Task WithTheModuleOffTheRouteIs403();
[Fact] public async Task TheAssigneesAreTheActiveMembersWhoCanManage();
// admin y billing entran; advisor y una membresía removed, no.
```

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~LoansLayerTests|FullyQualifiedName~CompositionRootTests"` → FAIL: no compila.

- [ ] **Step 3: Implement** — adaptadores como `ServiceContractsCustomerLookup` y `ServiceContractsMemberDirectory`, recortados a los métodos del puerto; `LoansMemberDirectory` pregunta por `LoansPermissions.Manage` y no necesita `IUserDirectory`. `PermissionDefinition`:

| Permiso | Etiqueta | Descripción | Categoría | Riesgo |
| --- | --- | --- | --- | --- |
| `Read` | «Ver préstamos» | «Permite ver los préstamos, las cuotas por cobrar y los abonos.» | «Préstamos» | `low` |
| `Manage` | «Gestionar préstamos» | «Permite crear y editar préstamos, marcar cuotas como pagadas y registrar abonos a capital.» | «Préstamos» | `medium` |

Los handlers se registran a mano y con ruta completa, como `:330`: un handler olvidado responde 500. `CompositionRootTests` lo atrapa por reflexión sobre `Modules.*.Application.dll`, y por eso `ArchitectureTests.csproj` tiene que referenciar los cuatro proyectos. Agrega el `using LoansPermissions = ...` junto a `:64` y `app.MapLoansEndpoints();` en `Program.cs:266`. Corre `dotnet restore Backend.slnx`.

- [ ] **Step 4: Run to verify GREEN** — el filtro del Step 2 → PASS. Luego `$env:Seed__Enabled = "false"`, `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoansAccessApiTests` → PASS, y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter FullyQualifiedName~AuthorizationCatalogApiTests` → PASS.

- [ ] **Step 5: Commit** — `feat(loans): wire module permissions, adapters and assignees`

---

### Task 6: Alta, detalle, lista, edición y borrado

**Files:**
- Create en `Modules.Loans.Application/`: `LoanRules.cs`, `LoanDtoComposer.cs`, `CreateLoan.cs`, `GetLoan.cs`, `ListLoans.cs`, `UpdateLoan.cs`, `DeleteLoan.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Api/LoansEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `tests/Modules/Loans/Modules.Loans.UnitTests/Modules.Loans.UnitTests.csproj` (referencia a Application)
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/CreateLoanValidatorTests.cs`, `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoanApiTests.cs`

**Interfaces:**
- Consumes: repositorios de la Task 4; `LoanInstallmentGenerator`, `LoanCapitalOperations`, `LoanAccrual`; `LoansAuthorization`.
- Produces:
  - `LoanDtoComposer`: `Task<LoanDetailDto> ComposeDetailAsync(Loan loan, DateOnly today, CancellationToken ct)`; `Task<IReadOnlyList<LoanDto>> ComposeManyAsync(Guid tenantId, IReadOnlyList<Loan> loans, DateOnly today, CancellationToken ct)`. Un viaje por lote, nunca uno por fila. Lo reutilizan las Tasks 7 y 8.
  - `LoanRules.NormalizeCurrency(string)`, `LoanRules.MustBeAMoneyAmount`, `LoanRules.MustBeAMonthlyRate`, `LoanRules.MustBeACutoffRule`, `LoanRules.TryParseCutoffRule`, `LoanRules.MustBeAReasonableDisbursement()` (sólo el piso `EarliestDisbursement = 2000-01-01`, como `ServiceRules`: cada mes desde esa fecha es una cuota; el techo lo juzga `Loan.Create` con la «hoy» del tenant).
  - Records con todo anulable, para que la ausencia sea `validation.failed`: `CreateLoanRequest`, `UpdateLoanRequest`.
  - `CreateLoanCommand : ICommand<LoanDetailDto>`, `GetLoanQuery : IQuery<LoanDetailDto>`, `ListLoansQuery : IQuery<LoanPageDto>` (`DefaultStatus = "active"`, `DefaultPageSize = 25`, `MaxPageSize = 100`), `UpdateLoanCommand : ICommand<LoanDetailDto>` (con `long Version`), `DeleteLoanCommand : ICommand<bool>`.

`ComposeDetailAsync` deriva así, sin escribir:

```csharp
// capitalPaymentBlock: capital 0 → NoCapital; pendientes > 0 → PendingInstallments;
// NextPeriodToGenerate().End < today → PeriodClosing; si no, null.
// capitalPaymentWindow: { OpenFrom(loan), today } sólo sin bloqueo.
// voidableCapitalPaymentId: LoanCapitalOperations.Voidable(...)?.Id.
// nextCutoffDate: loan.NextCutoffDate(today), en ComposeManyAsync, así que llega a la lista y al detalle.
// currentPeriod: LoanAccrual.ToDate(...). canEditTerms: sin cuotas ni filas de abono.
// canDelete: sin cuotas pagadas ni filas de abono.
```

- [ ] **Step 1: Write the failing tests**

```csharp
// CreateLoanValidatorTests: tabla de campos ausentes o fuera de rango → PropertyName.
[Theory]
[InlineData("Name")] [InlineData("PrincipalAmount")] [InlineData("MonthlyRatePercent")]
[InlineData("DisbursedOn")] [InlineData("CutoffRule")] [InlineData("Note")] [InlineData("CustomerId")]
public void AnInvalidFieldIsReportedByName(string property);

// LoanApiTests, reloj fijo en 2026-10-20 09:00 de Bogotá
[Fact] public async Task CreateRespondsWithTheDetailAndTheOpenPeriod();
// 201; outstanding = principal, status Active, currentPeriod.start 10-01, installments [], canEditTerms true.
[Fact] public async Task ALoanWithoutCurrencyUsesTheTenantCurrency();
[Fact] public async Task CreatingWithAPastDisbursementGeneratesEveryClosedPeriod();
// disbursedOn 2026-07-15, EndOfMonth: julio parcial, agosto y septiembre pendientes; canEditTerms false.
[Fact] public async Task AnInactiveCustomerIsRejected();              // loans.customer.inactive
[Fact] public async Task ACustomerOfAnotherTenantIsNotFound();        // loans.customer.not_found
[Fact] public async Task AnAssigneeWhoCannotManageIsRejected();       // loans.assignee.invalid
[Fact] public async Task AFutureDisbursementIsRejected();
[Fact] public async Task ListSeparatesActiveAndSettledAndSummarizesTheWholeTenantByCurrency();
[Fact] public async Task SearchMatchesTheCustomerOrTheLoanName();
[Fact] public async Task TheListAndTheDetailCarryTheSameNextCutoffDate();
// Reloj 2026-10-20: EndOfMonth 2026-10-31; DisbursementDay del 12, 2026-11-11.
[Fact] public async Task ThePendingInstallmentCountIgnoresTheListFilters();
// Dos préstamos con cuotas: el mismo número con status=settled, con search sin resultados y en otra página.
[Fact] public async Task UpdateWithoutIfMatchIs428();
[Fact] public async Task UpdateWithAStaleVersionIs412();
[Fact] public async Task WithoutMovementEveryTermCanChange();
[Fact] public async Task WithMovementOnlyNameAssigneeAndNoteCanChange(); // 100 y 100.00 son iguales: no es terms_locked
[Fact] public async Task ChangingATermWithMovementIsRejected();       // loans.loan.terms_locked
[Fact] public async Task DeleteRemovesALoanWithOnlyPendingInstallments(); // 204, y auditoría loans.loan.deleted
[Fact] public async Task DeleteIsRejectedWithAPaidInstallment();      // loans.loan.has_movement
[Fact] public async Task DeleteIsRejectedWithACapitalPayment();       // aunque esté anulado
[Fact] public async Task TheDetailAddsUpToWhatTheListShows();         // «Que la pantalla muestre una cifra que no cuadre»
```

- [ ] **Step 2: Run to verify RED** — `$env:Seed__Enabled = "false"`, luego `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoanApiTests` → FAIL: 404, las rutas no existen.

- [ ] **Step 3: Implement** — orden del alta: autorización → validador → cliente → encargado → moneda → `Loan.Create` → `GenerateClosedPeriods` → auditoría → guardar → componer. El `PUT`: autorización → validador → préstamo → `Version == command.Version` o `RequestConcurrencyException` → si hay movimiento y `loan.Terms != nuevos`, `terms_locked`; sin movimiento, `ChangeTerms` y `GenerateClosedPeriods` → `UpdateDetails`. `ListLoans` arma `accruedToDateByCurrency` con `ListAccruingAsync`, `ListByLoansAsync` y `LoanAccrual.ToDate`, en memoria: un tenant tiene pocos préstamos (precedente `ListServices`), y ese interés no se puede sumar en SQL. Rutas en el orden del contrato, `/assignees` y `/installments` antes de `/{loanId:guid}`. `RequireVersion` y `TryParseVersion` se copian de `ServiceContractsEndpoints.cs:382-406`, porque Loans no ve ese módulo.

- [ ] **Step 4: Run to verify GREEN** — `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests` → PASS, y el filtro del Step 2 → PASS.

- [ ] **Step 5: Commit** — `feat(loans): add loan create, read, update and delete`

---

### Task 7: Por cobrar y pago de cuotas

**Files:**
- Create en `Modules.Loans.Application/`: `ListLoanInstallments.cs`, `PayInstallment.cs`, `UpdateInstallmentPayment.cs`, `RemoveInstallmentPayment.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Api/LoansEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoanInstallmentApiTests.cs`

**Interfaces:**
- Consumes: `LoanDtoComposer`; `LoanInstallment.MarkPaid`, `UpdatePayment`, `RemovePayment`; `Loan.RefreshStatus`, `Touch`; `IMembershipDirectory.FindActiveMembershipIdAsync`.
- Produces: `PayInstallmentRequest(DateOnly? PaidOn, string? Note)`; `PayInstallmentCommand`, `UpdateInstallmentPaymentCommand`, `RemoveInstallmentPaymentCommand : ICommand<LoanDetailDto>`; `ListLoanInstallmentsQuery : IQuery<LoanInstallmentPageDto>`.

- [ ] **Step 1: Write the failing tests** — las cuotas se arman con un préstamo de desembolso pasado: el alta genera los períodos cerrados.

```csharp
[Fact] public async Task PayingMarksTheInstallmentAndRecordsWhoPaid();  // payment.paidBy, auditoría loans.installment.paid
[Fact] public async Task PayingTwiceIs422NotPending();                  // loans.installment.not_pending
[Fact] public async Task AFuturePaymentDateIsRejectedInTheTenantZone();
// 23:30 del 31 en Bogotá ya es el 1 en UTC: paidOn del 1 es futuro para el tenant.
[Fact] public async Task AnyPendingInstallmentCanBePaidInAnyOrder();
[Fact] public async Task AnInstallmentOfAnotherLoanIsNotFound();        // loans.installment.not_found
[Fact] public async Task CorrectingChangesDateAndNote();
[Fact] public async Task CorrectingAnUnpaidInstallmentIsRejected();     // loans.installment.not_paid
[Fact] public async Task RemovingThePaymentReturnsItToPending();        // auditoría loans.installment.payment_removed
[Fact] public async Task PayingTheLastInstallmentWithoutCapitalSettlesTheLoan();
[Fact] public async Task RemovingThePaymentReactivatesASettledLoan();   // Review Focus 5
[Fact] public async Task ThePendingListIsOrderedByPeriodAndSummarizesTheTenant();
// summary.oldest es la más antigua aunque search la deje fuera de items.
[Fact] public async Task ThePendingListSearchesCustomerAndLoan();
[Fact] public async Task AReaderCannotPay();                            // 403 authorization.denied
```

- [ ] **Step 2: Run to verify RED** — `$env:Seed__Enabled = "false"`, luego `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoanInstallmentApiTests` → FAIL: 404.

- [ ] **Step 3: Implement** — como `PayCharge.cs`: autorización → validador → préstamo → cuota → membresía activa o `Denied()` → `Today` → dominio → `loan.RefreshStatus(pendientes, now)` y `loan.Touch(now)` → auditoría → guardar → `ComposeDetailAsync`. Pagar y quitar el pago tocan el préstamo a propósito: suben su `Version` y así chocan con un abono en curso o con otro pago de la última cuota. Corregir fecha o nota no cambia nada de eso: no lo toca. `RemovePayment` limpia `PaidOn`, `PaymentNote`, `PaidByMemberId` y `PaidAt`. La lista reutiliza `FindManyAsync`, `ILoansCustomerLookup` y `ILoansMemberDirectory` por lote.

- [ ] **Step 4: Run to verify GREEN** — el filtro del Step 2 → PASS.

- [ ] **Step 5: Commit** — `feat(loans): add pending installments and installment payments`

---

### Task 8: Abonos a capital

**Files:**
- Create en `Modules.Loans.Application/`: `CapitalPaymentSupport.cs`, `PreviewCapitalPayment.cs`, `AddCapitalPayment.cs`, `VoidCapitalPayment.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Api/LoansEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoanCapitalPaymentApiTests.cs`

**Interfaces:**
- Consumes: `LoanCapitalOperations.Add`, `Void`; `LoanInstallmentGenerator.GenerateClosedPeriods`; `LoanDtoComposer`.
- Produces: `CapitalPaymentRequest(decimal? Amount, DateOnly? PaidOn, string? Note)`; `VoidCapitalPaymentRequest(string? Reason)`; `PreviewCapitalPaymentCommand : ICommand<LoanCapitalPaymentPreviewDto>`; `AddCapitalPaymentCommand`, `VoidCapitalPaymentCommand : ICommand<LoanDetailDto>`. Los tres con validador.

- [ ] **Step 1: Write the failing tests** — reloj movido por cada prueba.

```csharp
[Fact] public async Task ThePreviewOfAPartialPaymentWritesNothing();
// resultingPrincipal, settles false, finalInstallment null; luego el detalle sigue igual.
[Fact] public async Task ThePreviewOfASettlementAnnouncesTheLastInstallment();
// 100M el 2026-10-15: amount 677419.35, periodEnd 10-15, periodDays 31, un tramo de 14 días.
[Fact] public async Task ThePreviewMatchesWhatThePaymentCreates();     // misma cuota, mismos tramos
[Fact] public async Task ThePreviewFailsLikeThePayment();              // pending_installments, exceeds_balance, date_out_of_period
[Fact] public async Task APartialPaymentLowersTheCapital();            // 201, balanceAfter, auditoría loans.capital_payment.created
[Fact] public async Task OctoberEndsAt1170967_74();                    // el ejemplo del spec, de punta a punta
[Fact] public async Task PendingInstallmentsBlockThePayment();         // detalle: capitalPaymentBlock PendingInstallments
[Fact] public async Task ACapitalPaymentOnTheLastDayOfThePeriodIsAccepted(); // Review Focus 1
[Fact] public async Task ACapitalPaymentOverAClosedUngeneratedPeriodGeneratesItAndIsRejected();
// Review Focus 2. 1 de noviembre sin worker: 422 pending_installments, y la cuota de octubre queda guardada.
[Fact] public async Task ThePreviewOverAClosedPeriodDoesNotGenerate();
// Mismo escenario: 422, sin filas nuevas. El detalle dice PeriodClosing.
[Fact] public async Task SettlingLeavesTheLoanActiveUntilTheLastInstallmentIsPaid();
[Fact] public async Task VoidingTheLatestPaymentRestoresTheCapitalAndKeepsTheRow();
// voidedAt, voidReason, auditoría loans.capital_payment.voided; el detalle ya no lo ofrece.
[Fact] public async Task VoidingWithoutAReasonIs422();                 // validation.failed, errors.Reason
[Fact] public async Task VoidingAPaymentThatIsNotTheLatestIsRejected();
[Fact] public async Task VoidingAfterAnInstallmentWasGeneratedIsRejected();
[Fact] public async Task ThePaymentThatSettledCannotBeVoided();        // regla 7: ya generó una cuota
[Fact] public async Task VoidingAZeroInterestSettlementReactivatesTheLoan(); // Review Focus 4
[Fact] public async Task ASettledLoanHasNoNextCutoffDate();             // null en la lista y en el detalle
[Fact] public async Task TwoSimultaneousPaymentsCannotOverdrawTheCapital();  // 412 en una de las dos
[Fact] public async Task AnInactiveCustomerCanStillPay();              // sólo se valida al crear
```

- [ ] **Step 2: Run to verify RED** — `$env:Seed__Enabled = "false"`, luego `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoanCapitalPaymentApiTests` → FAIL: 404.

- [ ] **Step 3: Implement `CapitalPaymentSupport`** — `Task<CapitalPaymentContext> LoadAsync(Guid tenantId, Guid loanId, DateOnly today, DateTimeOffset now, CancellationToken ct)`: lee préstamo, abonos y pendientes, corre `GenerateClosedPeriods` y agrega lo generado al repositorio. Devuelve `(Loan, Payments, PendingCount, Generated)`, con `PendingCount` que incluye lo generado.

- [ ] **Step 4: Implement los handlers**

```csharp
// AddCapitalPayment: LoadAsync; si Generated no está vacío, SaveChanges **antes** de decidir:
// esa generación es correcta pase lo que pase con el abono, y sin guardarla el rechazo
// hablaría de una cuota que la pantalla no ve. Después LoanCapitalOperations.Add;
// payments.Add(outcome.Payment); installments.AddRange(cuota final); auditoría; guardar; componer.

// PreviewCapitalPayment: LoadAsync y LoanCapitalOperations.Add, **sin ningún SaveChanges**.
// Comparte el camino con el POST para que el aviso no pueda diferir de lo que el POST crea;
// el DbContext del request se descarta con los cambios en memoria.
// Tiene que ser POST porque lleva cuerpo; sigue sin escribir.

// VoidCapitalPayment: LoadAsync, guarda lo generado, LoanCapitalOperations.Void, auditoría, guardar, componer.
```

- [ ] **Step 5: Run to verify GREEN** — el filtro del Step 2 → PASS.

- [ ] **Step 6: Commit** — `feat(loans): add capital payments, preview and void`

---

### Task 9: Worker de generación

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/LoansOptions.cs`, `ILoansGenerationProcessor.cs`
- Create en `src/Modules/Loans/Modules.Loans.Infrastructure/`: `LoansOptionsValidator.cs`, `Generation/TenantRunStore.cs`, `Generation/LoansGenerationProcessor.cs`, `Generation/LoansGenerationWorker.cs`
- Modify: `LoansInfrastructureExtensions.cs`, `src/Api/appsettings.json:49`, `src/Api/appsettings.example.json`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/LoansOptionsTests.cs`, `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansGenerationProcessorTests.cs`, `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:615`

**Interfaces:**
- Consumes: `LoanInstallmentGenerator`; `ILoanRepository.ListAccruingAsync`; `ITenantClock`, `ITenantDirectory`, `ITenantModules`.
- Produces: `LoansOptions` (`SectionName = "Loans"`, `SweepMinutes = 15`, `Validate()`); `ILoansGenerationProcessor.ProcessTenantAsync(Guid tenantId, CancellationToken ct)`; `TenantRunStore.TryClaimAsync`, `TryFinishAsync`, con las firmas de `ServiceContracts` sobre `loans.tenant_runs`.

- [ ] **Step 1: Write the failing tests**

```csharp
// LoansGenerationProcessorTests, reloj fijo; copia el arranque de ServiceContractsReminderProcessorTests
[Fact] public async Task AClosedPeriodBecomesItsInstallmentOnce();
// 2026-11-01 00:05: una cuota de octubre; correr otra vez no agrega nada.
[Fact] public async Task APeriodIsNotGeneratedOnItsLastDay();          // Review Focus 1, 2026-10-31 23:50
[Fact] public async Task ADownWorkerCatchesUpEveryMissedPeriod();      // ene: oct, nov y dic con sus fechas reales
[Fact] public async Task ALoanGeneratedByAnotherReplicaIsNotDuplicated();
// Decorador de ILoanInstallmentRepository, como InterleavingChargeRepository: mete la fila de «otra réplica»
// entre la lectura y el SaveChanges. Una cuota, sin excepción.
[Fact] public async Task AFailedLoanDoesNotStopTheOthers();          // el mismo decorador lanza con un préstamo
[Fact] public async Task ALoanWithoutCapitalGeneratesNothing();        // Review Focus 5
[Fact] public async Task ASuspendedTenantGeneratesNothing();
[Fact] public async Task WithTheModuleOffNothingIsGeneratedAndTheNextRunCatchesUp();
[Fact] public async Task GenerationSendsNothing();                     // ninguna fila en platform.outbox_messages
```

`LoansOptionsTests`: `SweepMinutes <= 0` falla; el default valida.

`CompositionRootTests`: `TheContainerValidatesAndBuildsTheLoansGenerationWorker`, copia de la de `:615`.

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~LoansOptionsTests` → FAIL: no compila. `$env:Seed__Enabled = "false"`, luego `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoansGenerationProcessorTests` → FAIL: no compila.

- [ ] **Step 3: Implement** — el worker es copia de `ServiceContractsReminderWorker`: tenants con préstamos `Active` y capital > 0, un scope por tenant, un error no frena a los demás. El procesador sigue `ProcessTenantAsync:53-95` sin la hora de envío: no manda nada, y el corte importa desde las 00:00 locales.

```csharp
// Por tenant: calendario → tenant Active → módulo prendido → TryClaimAsync(today) → ids de ListAccruingAsync.
// Por préstamo, dentro de try/catch con ChangeTracker.Clear() en finally, como GenerateServiceAsync:136-166:
//   hasta 2 intentos: releer préstamo y abonos; GenerateClosedPeriods; 0 cuotas → return;
//   AddRange; dbContext.SaveChangesAsync;
//   en DbUpdateConcurrencyException o UniqueViolation "IX_installments_loan_period", con attempt == 0:
//   limpiar el tracker y repetir.
// Al final, TryFinishAsync. Un préstamo que falló se reintenta en la corrida de mañana, con su fecha real.
```

`QepApiFactory` del harness quita `LoansGenerationWorker` salvo con `runGenerationWorker`. Agrega `"Loans": { "SweepMinutes": 15 }` en `appsettings.json` y en `appsettings.example.json`: `ConfigurationExampleTests` falla sin la segunda.

- [ ] **Step 4: Run to verify GREEN** — los dos filtros del Step 2 → PASS. Luego `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~ConfigurationExampleTests"` → PASS.

- [ ] **Step 5: Commit** — `feat(loans): add daily installment generation worker`

---

### Task 10: Fusión de clientes, retención de usuarios y documentación

**Files:**
- Create: `src/Bootstrapper/LoansUserReferenceProbe.cs`
- Modify: `src/Bootstrapper/CustomerMergeReferences.cs:94-101`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:1258`
- Modify: `README.md` (sección nueva después de «Contratos de servicio», y § Módulos por tenant), `CLAUDE.md` (lista de módulos construidos, `:283`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/CustomerMergeReferencesTests.cs:247-262,285-295,315-320`, `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerMergeAcrossModulesApiTests.cs:241`, `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansUserReferenceProbeTests.cs`

**Interfaces:**
- Consumes: `LoansDbContext`, `IMembershipDirectory.ListMembershipIdsByUserAsync`.
- Produces: `internal sealed class LoansUserReferenceProbe : IUserReferenceProbe`, `Source => "loans"`.

- [ ] **Step 1: Write the failing tests**

`CustomerMergeReferencesTests`: agrega `CREATE SCHEMA loans;`, una réplica mínima de `loans.loans (id, tenant_id, customer_id)`, la fila a `AddRowAsync` y `("loans.loans", "customer_id")` a la lista de `:285-295`, y comprueba que otro tenant no se mueve.

`CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`: el arreglo siembra un préstamo por `INSERT` crudo en `loans.loans` y comprueba que `customer_id` pasa al que queda.

```csharp
// LoansUserReferenceProbeTests
[Fact] public async Task AnAssigneeRetainsTheUser();
[Fact] public async Task WhoPaidAnInstallmentRetainsTheUser();
[Fact] public async Task AnUnrelatedUserIsNotRetained();
```

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~CustomerMergeReferencesTests` → FAIL: la fila sigue en el fusionado. `$env:Seed__Enabled = "false"`, luego `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter FullyQualifiedName~LoansUserReferenceProbeTests` → FAIL: no compila.

- [ ] **Step 3: Implement** — en `CustomerMergeReferences.MoveCoreAsync`, antes del bloque de Shopify, que sigue siendo la última sentencia:

```csharp
await database.ExecuteSqlAsync($"""
    UPDATE loans.loans SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
    """, cancellationToken);
```

Sin subir versión (F15). `CountAsync` no cambia: sumar una columna cambiaría la vista previa de la fusión. Actualiza el `<summary>`. `Installments` no guarda cliente: una sola tabla que mover. La sonda sigue a `ServiceContractsUserReferenceProbe`, sobre `Loans.AssigneeMemberId` e `Installments.PaidByMemberId`, y se registra con `services.AddScoped<IUserReferenceProbe, LoansUserReferenceProbe>();`.

- [ ] **Step 4: Run to verify GREEN** — los filtros del Step 2 → PASS. Luego `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests --filter FullyQualifiedName~CustomerMergeAcrossModulesApiTests` → PASS.

- [ ] **Step 5: Documenta** — README: sección «Préstamos» con módulo, esquema, clave, worker y estos tres avisos: el orden de despliegue («primero backend y migración, después frontend»), que un `INSERT` a mano en `loans.loans` debe llenar `outstanding_principal` y `last_generated_period_end`, y que la carga inicial usa `is_imported = true`, `period_days` nulo, sin tramos, con `period_end` en fechas reales del calendario de corte. `CLAUDE.md`: agrega `Loans` a la lista de módulos, cambia «diecinueve» por «veinte» y suma el gotcha de que `CustomerMergeReferences` también escribe en `loans.loans`.

- [ ] **Step 6: Verifica lo afectado** — sin Docker: `dotnet build Backend.slnx` → 0 errores y 0 advertencias; `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS; `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests` → PASS; `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests` → PASS. Con Docker y `$env:Seed__Enabled = "false"`: `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests` → PASS. `dotnet restore src/Api/Api.csproj --locked-mode` → sin `NU1004`.

- [ ] **Step 7: Commit** — `feat(loans): move loans on customer merge and retain referenced users`
