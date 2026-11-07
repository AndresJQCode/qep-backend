# Importar préstamos desde Excel — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tres endpoints bajo `/loans` (plantilla, vista previa e importación) que convierten cada fila válida de un Excel en lo mismo que un `POST /loans`, creando el cliente si no existe.

**Architecture:** Copia por módulo del patrón de `ServiceContracts`: análisis puro compartido por vista previa e importación, sin staging, una transacción por fila. Clientes, teléfonos, documentos y correo del encargado entran por puertos `ILoans*` con adaptadores en `Bootstrapper`; ClosedXML vive en Infrastructure. `LoanOpening` extrae de `CreateLoanHandler` la apertura del préstamo, y la usan el `POST`, el conteo en seco del planner y la escritura de la importación.

**Tech Stack:** .NET 10, minimal APIs, EF Core 10 + Npgsql, FluentValidation 12.1.1, ClosedXML 0.105.1, xUnit v3, Testcontainers.PostgreSql.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-11-01-importar-prestamos-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-11-01-importar-prestamos-design.md). Vive en el repo del frontend. Referencia del patrón: `src/Modules/ServiceContracts/`.

**Plan hermano:** `qep-frontend/docs/superpowers/plans/2026-11-01-importar-prestamos-frontend.md`. Se escribe sólo desde § Contrato HTTP de este plan.

## Antes de empezar

- Rama `feature/importar-prestamos` desde `develop`, nunca sobre `main`: `git worktree add ../qep-backend-worktrees/importar-prestamos -b feature/importar-prestamos develop`. El worktree necesita su propio índice de CodeGraph. `git branch --show-current` justo antes de cada commit.
- `Stop-Process -Name Api -ErrorAction SilentlyContinue` antes de `dotnet build`, `dotnet test` o `dotnet ef`.
- Unitarias por tarea. Las de integración necesitan Docker y `$env:Seed__Enabled = "false"`, y corren una sola vez, en la Task 8. Hasta entonces, `dotnet build` de `Modules.Loans.IntegrationTests` es la señal RED/GREEN de lo escrito ahí.
- Este plan llega sin commitear: cópialo al worktree y entra en el commit de la Task 1.
- Commits: conventional commits en inglés, sin `Co-Authored-By` ni atribución de IA, siempre `git commit ... -- <paths>`: pueden correr sesiones concurrentes.
- Tras tocar referencias, `dotnet restore Backend.slnx` y se commitean **todos** los `packages.lock.json` modificados: el `Dockerfile` restaura con `--locked-mode`.

## Global Constraints

- Rutas bajo `/api/v1/tenants/{tenantId}/loans`: `GET /import/template`, `POST /import/preview`, `POST /import`, los tres con `LoansPermissions.Manage`.
- «Mismos topes que contratos: `.xlsx` o `.xls`, hasta 10 MB y 500 filas.»
- «Los errores del archivo son `loans.import.*` y los de fila `loans.import.row.*`.» Los del archivo son `LoansDomainException` (422); los de fila viajan en el DTO.
- «No hay staging: confirmar reenvía el mismo archivo y el análisis corre otra vez entero.»
- «Emparejar: documento, si no teléfono en E.164, si no correo», el primero que aplica decide, igual que contratos.
- «La clave es cliente resuelto + nombre del préstamo, sin distinguir mayúsculas y recortada.» `loan_exists` mira todos los préstamos del cliente, `Settled` incluidos. Los eliminados no cuentan: `loans` se borra físicamente.
- «La importación nunca modifica un cliente emparejado.»
- «Las reglas de `LoanRules.cs` se reutilizan, no se reescriben.»
- El número de cuotas sale de `LoanInstallmentGenerator`, nunca de una reimplementación.
- Sin índice único ni migración en `loans`.
- `Modules.Loans.Application` no referencia ClosedXML, EF Core, Npgsql ni otro módulo de negocio salvo Tenancy: `LoansLayerTests`.
- Comentarios en español, identificadores en inglés. El log de una fila fallida lleva sólo ids y número de fila, nunca datos personales.

## Review Focus

1. **Capital «150.000» como texto.** `MustBeAMoneyAmount` usa `ignoreTrailingZeros: true`, así que sola lo aceptaría como 150: mil veces menos. Task 2 `APrincipalWithThousandsDotsIsRejected`.
2. **Desembolso futuro.** `LoanRules` no lo limita, pero `Loan.Create` lanza `loans.loan.disbursed_in_future`: sin guarda, una fila así tumba la vista previa entera con un 422 de archivo. Task 3 `AFutureDisbursementIsARowErrorAndTheOthersStillPlan`.
3. **Tasa con formato de porcentaje integrado.** Excel guarda `0%` y `0.00%` por id de formato (9 y 10), con `Format` vacío al leer: mirar sólo el texto deja pasar 0,015 como 0,015 %. Task 6 `APercentCellIsNormalized` y `APlainDecimalCellStaysAsWritten`.
4. **Interés que redondea a 0,00.** Un período cerrado que no produce cuota: contar períodos diría 2 y la verdad es 0. Task 3 `ARateThatRoundsToZeroCountsNoInstallments`.
5. **Fila cuyo préstamo falla con cliente nuevo y luego se reintenta.** El cliente queda y el reintento lo empareja, sin duplicarlo ni dejar en el contexto lo de la fila anterior. Task 7 `AFailedWriteClearsTheContextAndTheNextRowStillSaves`, Task 8 `AFailedRowKeepsItsCustomerAndARetryDoesNotDuplicateIt`.

---

## Contrato HTTP

Completo: el plan del frontend se escribe sólo desde aquí.

**Base:** `/api/v1/tenants/{tenantId}/loans`. Tenant en la ruta y en `X-Tenant-Id`; `POST` con `X-Qep-Client: web`. `T?` viaja como `null`, nunca ausente. `date` es `yyyy-MM-dd`. `multipart/form-data` con el campo `file`.

| Método y ruta | Permiso | OK |
| --- | --- | --- |
| `GET /import/template` | manage | 200 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`, archivo `plantilla-prestamos.xlsx` |
| `POST /import/preview` | manage | 200 `LoansImportPreview`. No escribe |
| `POST /import` | manage | 200 `LoansImportResult` |

Un backend sin estas rutas responde 404. Sin permiso o con el módulo apagado, 403.

```ts
interface LoansImportRowError {
  rowNumber: number // la fila real del Excel; la 1 es el encabezado
  code: string // loans.import.row.*
  message: string // inglés, para soporte; nunca se pinta
  field: string | null // ver «Campos»
}

interface LoansImportRowPreview {
  rowNumber: number
  customer: { name: string; matchedId: string | null; action: 'create' | 'match' }
  loan: {
    name: string
    principalAmount: number
    currency: string
    monthlyRatePercent: number
    disbursedOn: string
    cutoffRule: 'EndOfMonth' | 'DisbursementDay'
  }
  installmentsToGenerate: number
}

interface LoansImportPreview {
  fileName: string
  totalRows: number
  validRows: LoansImportRowPreview[]
  errors: LoansImportRowError[]
  summary: {
    customersToCreate: number
    customersMatched: number
    loansToCreate: number
    installmentsToGenerate: number
  }
}

interface LoansImportedRow {
  rowNumber: number
  loanId: string
  customerId: string
  customerCreated: boolean
  installmentsGenerated: number
}

interface LoansImportResult {
  fileName: string
  receivedAt: string
  totalRows: number
  importedCount: number
  errorCount: number // filas con error, no errores
  customersCreated: number
  imported: LoansImportedRow[]
  errors: LoansImportRowError[]
}
```

**Errores de archivo** (422, ProblemDetails con `code`): `loans.import.file_required`, `loans.import.extension_invalid`, `loans.import.too_large`, `loans.import.unreadable`, `loans.import.too_many_rows`, `loans.import.header_mismatch`.

**Errores de fila:** `loans.import.row.customer_name_required`, `.identification_incomplete`, `.identification_type_invalid`, `.phone_required`, `.phone_invalid`, `.email_invalid`, `.country_invalid`, `.loan_name_required`, `.principal_invalid`, `.currency_unknown`, `.rate_invalid`, `.disbursed_on_invalid`, `.disbursed_in_future`, `.cutoff_rule_invalid`, `.assignee_invalid`, `.note_too_long`, `.customer_ambiguous`, `.customer_inactive`, `.duplicate_loan`, `.loan_exists`, `.failed`.

**Campos** (`field`, nombres de `ExcelLoanRow` en PascalCase, o `null`): `CustomerName`, `IdentificationType`, `IdentificationNumber`, `Phone`, `Email`, `Country`, `LoanName`, `PrincipalAmount`, `Currency`, `MonthlyRatePercent`, `DisbursedOn`, `CutoffRule`, `AssigneeEmail`, `Note`.

**Columnas de la plantilla**, en orden: `Cliente`, `Tipo de documento`, `Número de documento`, `Teléfono`, `Correo`, `País`, `Nombre del préstamo`, `Capital`, `Moneda`, `Tasa mensual %`, `Fecha de desembolso`, `Regla de corte`, `Correo del encargado`, `Nota`. `Regla de corte` ofrece `Último día del mes` y `Día del desembolso`.

---

## Mapa de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `Application/LoanOpening.cs` (nuevo) | Abre un préstamo con sus cuotas, sin persistir |
| `Application/LoansImportColumns.cs`, `IExcelLoansImporter.cs`, `ExcelLoanRowRules.cs` | Columnas, fila cruda y puertos Excel, reglas de fila, interpretación |
| `Application/LoansImportDtos.cs`, `LoansImportPlanner.cs` | Contrato y plan puro |
| `Application/ILoansCustomerProvisioning.cs`, `ILoansPhoneNormalizer.cs`, `ILoansImportFailureLog.cs` | Puertos nuevos |
| `Application/ExcelLoanRowDeduplicator.cs`, `LoansImportAnalyzer.cs` | Duplicados del archivo y análisis compartido |
| `Application/PreviewLoansImport.cs`, `ImportLoans.cs`, `GetLoansImportTemplate.cs` | Handlers |
| `Infrastructure/Excel/ClosedXmlLoansImporter.cs`, `ClosedXmlLoansImportTemplateBuilder.cs`, `LoansImportFailureLog.cs` | Adaptadores |
| `Bootstrapper/LoansCustomerProvisioning.cs`, `LoansPhoneNormalizer.cs` | Adaptadores cruzados |
| `Domain/LoansErrorCodes.cs`, `Api/LoansEndpoints.cs`, `Bootstrapper/QepServiceCollectionExtensions.cs`, `Bootstrapper/LoansCustomerLookup.cs`, `Bootstrapper/LoansMemberDirectory.cs`, `Infrastructure/Persistence/LoanRepository.cs`, `LoansUnitOfWork.cs` | Se modifican |

Rutas abreviadas: `Application` es `src/Modules/Loans/Modules.Loans.Application`, y así las demás capas.

---

### Task 1: `LoanOpening`, una sola apertura de préstamo

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/LoanOpening.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Application/CreateLoan.cs:83-84,101-107,116`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/LoanOpeningTests.cs`

**Interfaces:**
- Produces: `internal static class LoanOpening` con `public static readonly string[] AuditedFields` y `public static (Loan Loan, IReadOnlyList<LoanInstallment> Installments) Open(Guid tenantId, LoanTerms terms, string name, Guid? assigneeMemberId, string? note, DateOnly today, DateTimeOffset now)`

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class LoanOpeningTests
{
    private static (Loan Loan, IReadOnlyList<LoanInstallment> Installments) Open(
        string disbursedOn, decimal principal = 100_000_000m, decimal rate = 1.5m, string today = "2026-03-10") =>
        LoanOpening.Open(
            Guid.NewGuid(),
            new LoanTerms(Guid.NewGuid(), principal, "COP", rate, LoanTestData.Date(disbursedOn), LoanCutoffRule.EndOfMonth),
            "Capital de trabajo", null, null, LoanTestData.Date(today), LoanTestData.Now);

    [Fact]
    public void ADisbursementInThePastOpensWithTheInstallmentsOfItsClosedPeriods()
    {
        var (loan, installments) = Open("2026-01-15");

        Assert.Equal(2, installments.Count);
        Assert.Equal(LoanTestData.Date("2026-02-28"), loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void AnInterestThatRoundsToZeroOpensWithoutInstallmentsAndStillMarksThePeriods()
    {
        var (loan, installments) = Open("2026-01-01", principal: 1m, rate: 0.01m);

        Assert.Empty(installments);
        Assert.Equal(LoanTestData.Date("2026-02-28"), loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void AFutureDisbursementFailsWithTheDomainCode()
    {
        var error = Assert.Throws<LoansDomainException>(() => Open("2026-03-11"));

        Assert.Equal(LoansErrorCodes.LoanDisbursedInFuture, error.Code);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~LoanOpeningTests`
Expected: FAIL, no compila: `LoanOpening` no existe.

- [ ] **Step 3: Implement `LoanOpening` y úsalo en `CreateLoanHandler`**

`Open` es `Loan.Create` con `LoanId.New()` seguido de `LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], today, now)`. `AuditedFields` es el `CreatedFields` actual, que sale de `CreateLoanHandler`. El handler llama a `Open` y hace `loans.Add`, `installments.AddRange` y `Publish(..., LoanOpening.AuditedFields, now)`; no cambia nada más.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add docs/superpowers/plans/2026-11-01-importar-prestamos-backend.md src/Modules/Loans/Modules.Loans.Application/LoanOpening.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoanOpeningTests.cs
git commit -m "refactor(loans): extract the loan opening so the import shares it" -- docs/superpowers/plans/2026-11-01-importar-prestamos-backend.md src/Modules/Loans/Modules.Loans.Application/LoanOpening.cs src/Modules/Loans/Modules.Loans.Application/CreateLoan.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoanOpeningTests.cs
```

### Task 2: Reglas de fila sobre el texto crudo

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/LoansImportColumns.cs`, `IExcelLoansImporter.cs`, `ILoansPhoneNormalizer.cs`, `ExcelLoanRowRules.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Domain/LoansErrorCodes.cs`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportTestDoubles.cs`, `ExcelLoanRowRulesTests.cs`

**Interfaces:**
- Produces, en `LoansErrorCodes`: `ImportFileRequired`, `ImportExtensionInvalid`, `ImportTooLarge`, `ImportUnreadable`, `ImportTooManyRows`, `ImportHeaderMismatch` y `ImportRow*`, uno por código de § Contrato HTTP (`ImportRowCustomerNameRequired` … `ImportRowFailed`).
- Produces: `LoansImportColumns` con las constantes `CustomerName`, `IdentificationType`, `IdentificationNumber`, `Phone`, `Email`, `Country`, `LoanName`, `PrincipalAmount`, `Currency`, `MonthlyRate`, `DisbursedOn`, `CutoffRule`, `Assignee`, `Note` (los encabezados de § Contrato HTTP), `Ordered` (14, en ese orden) y `CutoffRules = ["Último día del mes", "Día del desembolso"]`.
- Produces: `sealed record ExcelLoanRow(int RowNumber, string? CustomerName, string? IdentificationType, string? IdentificationNumber, string? Phone, string? Email, string? Country, string? LoanName, string? PrincipalAmount, string? Currency, string? MonthlyRatePercent, string? DisbursedOn, string? CutoffRule, string? AssigneeEmail, string? Note)`
- Produces: `interface IExcelLoansImporter { Task<IReadOnlyList<ExcelLoanRow>> ReadAsync(Stream content, CancellationToken cancellationToken); }`, `interface ILoansImportTemplateBuilder { byte[] Build(IReadOnlyCollection<string> identificationTypes, IReadOnlyCollection<string> currencies, CancellationToken cancellationToken); }`
- Produces: `interface ILoansPhoneNormalizer { string? ToE164(string? phone, string? country); bool IsKnownCountry(string country); }`, `interface ILoansIdentificationTypes { IReadOnlyCollection<string> SupportedWireValues { get; } }`
- Produces: `sealed class ExcelLoanRowRules(ILoansPhoneNormalizer phones, ILoansIdentificationTypes identificationTypes) : AbstractValidator<ExcelLoanRow>`
- Produces: `sealed record LoansImportRow(int RowNumber, string CustomerName, string? IdentificationType, string? IdentificationNumber, string? Phone, string? PhoneE164, string? Email, string Country, string LoanName, decimal PrincipalAmount, string? Currency, decimal MonthlyRatePercent, DateOnly DisbursedOn, LoanCutoffRule CutoffRule, string? AssigneeEmail, string? Note)` con `string? CustomerKey`, `string LoanKey` y `static LoansImportRow From(ExcelLoanRow row, ILoansPhoneNormalizer phones)`
- Produces: `internal static class LoansImportValues` con `NameMaxLength = 150`, `EmailMaxLength = 254`, `DefaultCountry = "CO"`, `Blank`, `CountryOf`, `Fold`, `TryParseAmount`, `TryParseRate`, `TryParseDate`, `TryParseCutoffRule`, `NormalizeLoanName`

- [ ] **Step 1: Write the failing test**

`LoansImportTestDoubles.cs` copia `ServiceContractsImportTestDoubles`: `Now = new(2026, 3, 10, 13, 0, 0, TimeSpan.Zero)`, `Today = new(2026, 3, 10)`, `FakePhones`, `FakeIdentificationTypes` y estos dos:

```csharp
public static ExcelLoanRow ValidRow(int rowNumber = 2) => new(
    rowNumber, "Panadería La Esquina", "CC", "1020304050", "300 123 4567", "pan@example.com", "CO",
    "Capital de trabajo", "1500000", "COP", "1.5", "2026-01-15", "Último día del mes", "ana@example.com", "Sin garantía");

public static LoansImportRow ImportRow(
    int rowNumber = 2, string loanName = "Capital de trabajo", string? identificationType = "CC",
    string? identificationNumber = "1020304050", string? phoneE164 = "+573001234567", string? email = "pan@example.com",
    decimal principal = 1_500_000m, decimal rate = 1.5m, string? currency = null, string disbursedOn = "2026-01-15",
    LoanCutoffRule cutoffRule = LoanCutoffRule.EndOfMonth, string? assigneeEmail = "ana@example.com") => new(
    rowNumber, "Panadería La Esquina", identificationType, identificationNumber, phoneE164 is null ? null : "300 123 4567",
    phoneE164, email, "CO", loanName, principal, currency, rate, DateOnly.ParseExact(disbursedOn, "yyyy-MM-dd"), cutoffRule,
    assigneeEmail, null);
```

```csharp
public sealed class ExcelLoanRowRulesTests
{
    private static string[] Codes(ExcelLoanRow row) =>
        new ExcelLoanRowRules(new FakePhones(), new FakeIdentificationTypes()).Validate(row).Errors.Select(e => e.ErrorCode).ToArray();

    [Fact]
    public void AValidRowHasNoErrors() => Assert.Empty(Codes(ValidRow()));

    [Theory]
    [InlineData("150.000")]   // el punto separa miles en Colombia: nunca se lee como 150
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1500000.555")]
    [InlineData("12345678901234567")]
    [InlineData(null)]
    public void APrincipalWithThousandsDotsIsRejected(string? value) =>
        Assert.Equal([LoansErrorCodes.ImportRowPrincipalInvalid], Codes(ValidRow() with { PrincipalAmount = value }));

    [Theory]
    [InlineData("1500000.50")]
    [InlineData("1234567890123456")]
    public void APrincipalInsideNumeric182Passes(string value) =>
        Assert.Empty(Codes(ValidRow() with { PrincipalAmount = value }));

    [Theory]
    [InlineData("0")]
    [InlineData("100.5")]
    [InlineData("1.23456")]
    [InlineData("1,5")]
    [InlineData(null)]
    public void ARateOutOfMustBeAMonthlyRateIsRejected(string? value) =>
        Assert.Equal([LoansErrorCodes.ImportRowRateInvalid], Codes(ValidRow() with { MonthlyRatePercent = value }));

    [Theory]
    [InlineData("100")]
    [InlineData("1.5000")]
    public void ARateInsideTheRulePasses(string value) => Assert.Empty(Codes(ValidRow() with { MonthlyRatePercent = value }));

    [Theory]
    [InlineData("1999-12-31")]
    [InlineData("31/13/2026")]
    [InlineData(null)]
    public void ADisbursementBeforeTheFloorOrUnreadableIsRejected(string? value) =>
        Assert.Equal([LoansErrorCodes.ImportRowDisbursedOnInvalid], Codes(ValidRow() with { DisbursedOn = value }));

    [Theory]
    [InlineData("2000-01-01")]
    [InlineData("15/01/2026")]
    [InlineData("2099-01-01")] // el techo lo juzga el plan con la «hoy» del tenant
    public void ADisbursementInsideTheRulePasses(string value) => Assert.Empty(Codes(ValidRow() with { DisbursedOn = value }));

    [Theory]
    [InlineData("Último día del mes")]
    [InlineData("ULTIMO DIA DEL MES")]
    [InlineData("Día del desembolso")]
    [InlineData("EndOfMonth")]
    [InlineData("DisbursementDay")]
    public void TheFormLabelsAndTheWireNamesAreCutoffRules(string value) => Assert.Empty(Codes(ValidRow() with { CutoffRule = value }));

    [Theory]
    [InlineData("0")]
    [InlineData("Mensual")]
    [InlineData(null)]
    public void AnythingElseIsNotACutoffRule(string? value) =>
        Assert.Equal([LoansErrorCodes.ImportRowCutoffRuleInvalid], Codes(ValidRow() with { CutoffRule = value }));

    [Fact]
    public void TheLoanNameAndTheNoteFollowLoanRules()
    {
        Assert.Equal([LoansErrorCodes.ImportRowLoanNameRequired], Codes(ValidRow() with { LoanName = "   " }));
        Assert.Equal([LoansErrorCodes.ImportRowLoanNameRequired], Codes(ValidRow() with { LoanName = new string('x', 151) }));
        Assert.Equal([LoansErrorCodes.ImportRowNoteTooLong], Codes(ValidRow() with { Note = new string('x', 1001) }));
        Assert.Empty(Codes(ValidRow() with { Note = "   " }));
    }

    [Fact]
    public void AnEmptyCurrencyPassesAndAnUnknownOneIsARowError()
    {
        Assert.Empty(Codes(ValidRow() with { Currency = null }));
        Assert.Equal([LoansErrorCodes.ImportRowCurrencyUnknown], Codes(ValidRow() with { Currency = "ZZZ" }));
    }

    [Fact]
    public void AnEmptyAssigneePasses() => Assert.Empty(Codes(ValidRow() with { AssigneeEmail = null }));

    [Fact]
    public void TheCustomerColumnsKeepTheContractsRules()
    {
        Assert.Equal([LoansErrorCodes.ImportRowCustomerNameRequired], Codes(ValidRow() with { CustomerName = " " }));
        Assert.Equal([LoansErrorCodes.ImportRowIdentificationIncomplete], Codes(ValidRow() with { IdentificationNumber = null }));
        Assert.Equal([LoansErrorCodes.ImportRowIdentificationTypeInvalid], Codes(ValidRow() with { IdentificationType = "RUT" }));
        Assert.Equal([LoansErrorCodes.ImportRowPhoneInvalid], Codes(ValidRow() with { Phone = "123" }));
        Assert.Equal([LoansErrorCodes.ImportRowEmailInvalid], Codes(ValidRow() with { Email = "no-es-correo" }));
        Assert.Equal([LoansErrorCodes.ImportRowCountryInvalid], Codes(ValidRow() with { Country = "ZZ" }));
    }

    [Fact]
    public void FromInterpretsTheCells()
    {
        var row = LoansImportRow.From(
            ValidRow() with { LoanName = "  Crédito   CASA ", Currency = " ", AssigneeEmail = " ANA@example.com ", Note = "  " },
            new FakePhones());

        Assert.Equal("document:CC:1020304050", row.CustomerKey);
        Assert.Equal("crédito casa", row.LoanKey);
        Assert.Equal(("Crédito   CASA", 1_500_000m, 1.5m), (row.LoanName, row.PrincipalAmount, row.MonthlyRatePercent));
        Assert.Equal(LoanCutoffRule.EndOfMonth, row.CutoffRule);
        Assert.Null(row.Currency);
        Assert.Equal("ana@example.com", row.AssigneeEmail);
        Assert.Null(row.Note);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~ExcelLoanRowRulesTests`
Expected: FAIL, no compila.

- [ ] **Step 3: Implement**

`LoansImportValues` y las reglas de cliente son copia de `ExcelServiceContractsRowRules.cs` sin la parte de canal. Una decisión que el código de contratos no trae: **las extensiones de `LoanRules` piden valores tipados y encadenan varios validadores**, y `WithErrorCode` sólo alcanza al último. Por eso cada regla se envuelve en un validador privado y la fila expone un solo `Must`, con el código de la columna:

```csharp
private static readonly InlineValidator<decimal> Principal = new() { v => v.RuleFor(x => x).MustBeAMoneyAmount() };
private static readonly InlineValidator<decimal> Rate = new() { v => v.RuleFor(x => x).MustBeAMonthlyRate() };
private static readonly InlineValidator<DateOnly> Disbursement = new() { v => v.RuleFor(x => x).MustBeAReasonableDisbursement() };
private static readonly InlineValidator<string> Name = new() { v => v.RuleFor(x => x).MustBeALoanName() };
private static readonly InlineValidator<string?> Note = new() { v => v.RuleFor(x => x).MustBeALoanNote() };

RuleFor(row => row.PrincipalAmount)
    .Must(text => LoansImportValues.TryParseAmount(text, out var amount) && Principal.Validate(amount).IsValid)
    .WithErrorCode(LoansErrorCodes.ImportRowPrincipalInvalid)
    .WithMessage("The principal must be a number greater than zero with at most two decimals.");
```

`Validate` no acepta un modelo nulo: `Name.Validate(text ?? string.Empty)`, y lo mismo con `Note`. `TryParseAmount` es `decimal.TryParse` con `NumberStyles.AllowDecimalPoint` e invariante, más `amount.Scale <= LoanRules.MoneyScale`: esa escala es la que rechaza `150.000`, porque `MustBeAMoneyAmount` ignora ceros finales. `TryParseRate` es el mismo parseo sin mirar la escala: `1.5000` vale. `TryParseDate` usa `["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"]`. `TryParseCutoffRule` compara `Fold(text)` contra `ultimo dia del mes` y `endofmonth`, `dia del desembolso` y `disbursementday`. `NormalizeLoanName` es `NormalizeServiceName` de contratos. `LoanKey => NormalizeLoanName(LoanName)`. `LoanName` se guarda recortado, sin colapsar espacios internos: es lo que guarda el dominio.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~ExcelLoanRowRulesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Loans/Modules.Loans.Application/LoansImportColumns.cs src/Modules/Loans/Modules.Loans.Application/IExcelLoansImporter.cs src/Modules/Loans/Modules.Loans.Application/ILoansPhoneNormalizer.cs src/Modules/Loans/Modules.Loans.Application/ExcelLoanRowRules.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportTestDoubles.cs tests/Modules/Loans/Modules.Loans.UnitTests/ExcelLoanRowRulesTests.cs
git commit -m "feat(loans): validate an import row with the loan rules" -- src/Modules/Loans/Modules.Loans.Application src/Modules/Loans/Modules.Loans.Domain/LoansErrorCodes.cs tests/Modules/Loans/Modules.Loans.UnitTests
```

### Task 3: Contrato y plan de la importación

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/LoansImportDtos.cs`, `LoansImportPlanner.cs`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportPlannerTests.cs`

**Interfaces:**
- Consumes: `LoansImportRow` (Task 2), `LoanOpening.Open` (Task 1), `LoansCustomerRef`, `LoansMemberRef`.
- Produces los DTOs de § Contrato HTTP: `LoansImportRowErrorDto(int RowNumber, string Code, string Message, string? Field)`, `LoansImportCustomerPreviewDto(string Name, Guid? MatchedId, string Action)`, `LoansImportLoanPreviewDto(string Name, decimal PrincipalAmount, string Currency, decimal MonthlyRatePercent, DateOnly DisbursedOn, string CutoffRule)`, `LoansImportRowPreviewDto(int RowNumber, LoansImportCustomerPreviewDto Customer, LoansImportLoanPreviewDto Loan, int InstallmentsToGenerate)`, `LoansImportPreviewSummaryDto(int CustomersToCreate, int CustomersMatched, int LoansToCreate, int InstallmentsToGenerate)`, `LoansImportPreviewDto(string FileName, int TotalRows, IReadOnlyList<LoansImportRowPreviewDto> ValidRows, IReadOnlyList<LoansImportRowErrorDto> Errors, LoansImportPreviewSummaryDto Summary)`, `LoansImportedRowDto(int RowNumber, Guid LoanId, Guid CustomerId, bool CustomerCreated, int InstallmentsGenerated)`, `LoansImportResultDto(string FileName, DateTimeOffset ReceivedAt, int TotalRows, int ImportedCount, int ErrorCount, int CustomersCreated, IReadOnlyList<LoansImportedRowDto> Imported, IReadOnlyList<LoansImportRowErrorDto> Errors)`, `LoansImportTemplateFile(byte[] Content, string FileName)`
- Produces: `LoansImportContext(DateOnly Today, DateTimeOffset Now, string DefaultCurrency, IReadOnlyDictionary<int, IReadOnlyList<LoansCustomerRef>> MatchesByRow, IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> LoanNamesByCustomer, IReadOnlyDictionary<string, LoansMemberRef> ManagersByEmail, IReadOnlyDictionary<int, string>? NewCustomerKeys = null)`
- Produces: `PlannedLoanRow(LoansImportRow Row, Guid? MatchedCustomerId, string CustomerName, string? NewCustomerKey, Guid? AssigneeMemberId, string Currency, int InstallmentsToGenerate)` con `LoansImportRowPreviewDto ToPreview()`; `LoansImportPlan(IReadOnlyList<PlannedLoanRow> Rows, IReadOnlyList<LoansImportRowErrorDto> Errors, LoansImportPreviewSummaryDto Summary)`; `static class LoansImportPlanner { static LoansImportPlan Plan(IReadOnlyList<LoansImportRow> rows, LoansImportContext context); }`

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class LoansImportPlannerTests
{
    private static readonly LoansCustomerRef Panaderia = new(Guid.NewGuid(), "Panadería", "+573001234567", "pan@example.com", true);
    private static readonly LoansMemberRef Ana = new(Guid.NewGuid(), "Ana", true, true);

    private static LoansImportContext Context(
        IReadOnlyDictionary<int, IReadOnlyList<LoansCustomerRef>>? matches = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? loanNames = null) => new(
        Today, Now, "COP", matches ?? new Dictionary<int, IReadOnlyList<LoansCustomerRef>>(),
        loanNames ?? new Dictionary<Guid, IReadOnlyCollection<string>>(),
        new Dictionary<string, LoansMemberRef> { ["ana@example.com"] = Ana });

    private static Dictionary<int, IReadOnlyList<LoansCustomerRef>> Matched(params LoansCustomerRef[] customers) => new() { [2] = customers };

    [Fact]
    public void ANewCustomerRowPlansItsCreationAndItsRealInstallments()
    {
        var plan = LoansImportPlanner.Plan([ImportRow()], Context());

        var row = Assert.Single(plan.Rows);
        Assert.Null(row.MatchedCustomerId);
        Assert.Equal("document:CC:1020304050", row.NewCustomerKey);
        Assert.Equal(("COP", 2, Ana.MemberId), (row.Currency, row.InstallmentsToGenerate, row.AssigneeMemberId));
        Assert.Equal(new LoansImportPreviewSummaryDto(1, 0, 1, 2), plan.Summary);
    }

    [Fact]
    public void TheCutoffRuleChangesTheCount()
    {
        var plan = LoansImportPlanner.Plan([ImportRow(cutoffRule: LoanCutoffRule.DisbursementDay)], Context());

        Assert.Equal(1, Assert.Single(plan.Rows).InstallmentsToGenerate);
    }

    [Fact]
    public void ARateThatRoundsToZeroCountsNoInstallments()
    {
        var plan = LoansImportPlanner.Plan([ImportRow(principal: 1m, rate: 0.01m, disbursedOn: "2026-01-01")], Context());

        Assert.Equal(0, Assert.Single(plan.Rows).InstallmentsToGenerate);
    }

    [Fact]
    public void AFutureDisbursementIsARowErrorAndTheOthersStillPlan()
    {
        var plan = LoansImportPlanner.Plan([ImportRow(disbursedOn: "2026-03-11"), ImportRow(3, identificationNumber: "99")], Context());

        Assert.Equal([3], plan.Rows.Select(row => row.Row.RowNumber));
        var error = Assert.Single(plan.Errors);
        Assert.Equal((2, LoansErrorCodes.ImportRowDisbursedInFuture, "DisbursedOn"), (error.RowNumber, error.Code, error.Field));
    }

    [Fact]
    public void ADisbursementTodayIsAccepted() =>
        Assert.Empty(LoansImportPlanner.Plan([ImportRow(disbursedOn: "2026-03-10")], Context()).Errors);

    [Fact]
    public void AMatchedCustomerIsUsedWithItsOwnName()
    {
        var plan = LoansImportPlanner.Plan([ImportRow()], Context(Matched(Panaderia)));

        var row = Assert.Single(plan.Rows);
        Assert.Equal((Panaderia.Id, "Panadería", (string?)null), (row.MatchedCustomerId, row.CustomerName, row.NewCustomerKey));
        Assert.Equal("match", row.ToPreview().Customer.Action);
    }

    [Fact]
    public void AnInactiveCustomerAndAnAmbiguousOneAreRowErrors()
    {
        var inactive = LoansImportPlanner.Plan([ImportRow()], Context(Matched(Panaderia with { IsActive = false })));
        var ambiguous = LoansImportPlanner.Plan([ImportRow()], Context(Matched(Panaderia, Panaderia with { Id = Guid.NewGuid() })));

        Assert.Equal(LoansErrorCodes.ImportRowCustomerInactive, Assert.Single(inactive.Errors).Code);
        Assert.Equal(LoansErrorCodes.ImportRowCustomerAmbiguous, Assert.Single(ambiguous.Errors).Code);
    }

    [Fact]
    public void ANewCustomerNeedsAPhone()
    {
        var plan = LoansImportPlanner.Plan([ImportRow(phoneE164: null)], Context());

        var error = Assert.Single(plan.Errors);
        Assert.Equal((LoansErrorCodes.ImportRowPhoneRequired, "Phone"), (error.Code, error.Field));
    }

    [Theory]
    [InlineData("Capital de trabajo")]
    [InlineData("  capital   DE trabajo ")]
    public void ALoanTheCustomerAlreadyHasIsARowError(string existing)
    {
        var plan = LoansImportPlanner.Plan(
            [ImportRow()],
            Context(Matched(Panaderia), new Dictionary<Guid, IReadOnlyCollection<string>> { [Panaderia.Id] = [existing] }));

        var error = Assert.Single(plan.Errors);
        Assert.Equal((LoansErrorCodes.ImportRowLoanExists, "LoanName"), (error.Code, error.Field));
    }

    [Fact]
    public void AnAssigneeWhoDoesNotResolveIsARowErrorAndAnEmptyOneIsNot()
    {
        var unresolved = LoansImportPlanner.Plan([ImportRow(assigneeEmail: "nadie@example.com")], Context());
        var empty = LoansImportPlanner.Plan([ImportRow(assigneeEmail: null)], Context());

        var error = Assert.Single(unresolved.Errors);
        Assert.Equal((LoansErrorCodes.ImportRowAssigneeInvalid, "AssigneeEmail"), (error.Code, error.Field));
        Assert.Null(Assert.Single(empty.Rows).AssigneeMemberId);
    }

    [Fact]
    public void TheRowCurrencyWinsOverTheTenants() =>
        Assert.Equal("USD", Assert.Single(LoansImportPlanner.Plan([ImportRow(currency: "USD")], Context()).Rows).Currency);

    [Fact]
    public void ToPreviewNamesTheCutoffRuleByItsWireName() =>
        Assert.Equal("EndOfMonth", Assert.Single(LoansImportPlanner.Plan([ImportRow()], Context()).Rows).ToPreview().Loan.CutoffRule);
}
```

Con la «hoy» 2026-03-10: fin de mes cierra enero y febrero (2); día de desembolso desde el 15 cierra sólo 15-ene a 14-feb (1).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~LoansImportPlannerTests`
Expected: FAIL, no compila.

- [ ] **Step 3: Implement**

`Plan` y `PlanRow` siguen `ServiceContractsImportPlanner` sin perfiles ni canal. El orden de `PlanRow`, primera falla gana: ambiguo → inactivo → clave de cliente → `phone_required` (cliente nuevo sin teléfono que ninguna fila anterior planeó) → `loan_exists` → `assignee_invalid` → `disbursed_in_future` → conteo en seco.

El futuro se mira contra `context.Today` **antes** del conteo porque `Loan.Create` lanza. El conteo es `LoanOpening.Open(Guid.Empty, new LoanTerms(Guid.Empty, row.PrincipalAmount, currency, row.MonthlyRatePercent, row.DisbursedOn, row.CutoffRule), row.LoanName, null, null, context.Today, context.Now).Installments.Count`, con el capital y la tasa reales de la fila: de ellos depende que el interés redondee a cero, a diferencia del conteo de contratos, que usa precio 1. `ToPreview` pone `CutoffRule = Row.CutoffRule.ToString()` y `Action = "create"` o `"match"`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~LoansImportPlannerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Loans/Modules.Loans.Application/LoansImportDtos.cs src/Modules/Loans/Modules.Loans.Application/LoansImportPlanner.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportPlannerTests.cs
git commit -m "feat(loans): plan an import with the real installment generation" -- src/Modules/Loans/Modules.Loans.Application/LoansImportDtos.cs src/Modules/Loans/Modules.Loans.Application/LoansImportPlanner.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportPlannerTests.cs
```

### Task 4: Puertos y adaptadores cruzados

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/ILoansCustomerProvisioning.cs`, `src/Bootstrapper/LoansCustomerProvisioning.cs`, `src/Bootstrapper/LoansPhoneNormalizer.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Application/ILoansCustomerLookup.cs`, `ILoansMemberDirectory.cs`, `ILoanRepository.cs`, `src/Modules/Loans/Modules.Loans.Infrastructure/Persistence/LoanRepository.cs`, `src/Bootstrapper/LoansCustomerLookup.cs`, `src/Bootstrapper/LoansMemberDirectory.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:1326-1327`
- Test: `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansImportDirectoriesTests.cs` (se escribe acá, corre en la Task 8)

**Interfaces:**
- Produces, en `ILoansCustomerLookup`: `Task<IReadOnlyList<LoansCustomerRef>> FindByIdentificationAsync(Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken)`, `FindByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken)`, `FindByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)`, del mismo tipo.
- Produces, en `ILoansMemberDirectory`: `Task<LoansMemberRef?> FindManagerByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)`
- Produces, en `ILoanRepository`: `Task<IReadOnlyList<LoanNameRef>> ListNamesByCustomersAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)` y `sealed record LoanNameRef(Guid CustomerId, string Name)`
- Produces: `LoansNewCustomer(string Name, string? IdentificationType, string? IdentificationNumber, string? Phone, string? Email, string Country)`, `LoansProvisionedCustomer(Guid Id, bool Created)`, `interface ILoansCustomerProvisioning { Task<LoansProvisionedCustomer> CreateAsync(Guid tenantId, Guid actorUserId, LoansNewCustomer data, CancellationToken cancellationToken); }`

- [ ] **Step 1: Write the failing test**

`LoansImportDirectoriesTests` con `OpenAsync()` del harness, `scope.ServiceProvider.GetRequiredService<...>()`, tenant `Guid.Parse(LoansApiHarness.TenantId)`:

```csharp
[Fact]
public async Task FindManagerByEmailIgnoresCaseAndOnlyReturnsWhoCanManage()
{
    var (database, factory) = await OpenAsync();
    await using var _ = database;
    using var __ = factory;
    var ana = await SeedMemberAsync(factory, "ana@example.com", "Ana", "admin");
    await SeedMemberAsync(factory, "beto@example.com", "Beto", "advisor");
    await SeedMemberAsync(factory, "caro@example.com", "Caro", "admin", removed: true);
    await using var scope = factory.Services.CreateAsyncScope();
    var directory = scope.ServiceProvider.GetRequiredService<ILoansMemberDirectory>();

    Assert.Equal(ana, (await directory.FindManagerByEmailAsync(Tenant, "ANA@example.com", Ct))!.MemberId);
    Assert.Null(await directory.FindManagerByEmailAsync(Tenant, "beto@example.com", Ct));
    Assert.Null(await directory.FindManagerByEmailAsync(Tenant, "caro@example.com", Ct));
}

[Fact]
public async Task TheLookupMatchesByPhoneAndEmail()
{
    // SeedCustomerAsync con "+573001234567" y "pan@example.com".
    // FindByPhoneE164Async("+573001234567") y FindByEmailAsync("PAN@example.com") devuelven ese id.
}

[Fact]
public async Task ProvisioningWithADocumentCreatesItOnceAndADuplicateRaceReturnsTheWinner()
{
    // CreateAsync(Tenant, SubjectId, new LoansNewCustomer("Pan", "CC", "1020304050", "+573001234567", null, "CO"), Ct)
    // → Created true. La misma llamada otra vez → mismo Id, Created false.
}

[Fact]
public async Task ProvisioningWithoutADocumentCreatesAnIncompleteCustomer()
{
    // CreateAsync(..., new LoansNewCustomer("Pan", null, null, "+573001234567", null, "CO"), Ct) → Created true;
    // FindByPhoneE164Async lo encuentra.
}

[Fact]
public async Task ListNamesByCustomersIncludesSettledLoansAndNoOtherTenants()
{
    // Dos POST /loans del cliente; UPDATE loans.loans SET status = 'Settled' a uno; ListNamesByCustomersAsync devuelve los dos.
    // Con la lista vacía de ids devuelve [].
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build tests/Modules/Loans/Modules.Loans.IntegrationTests`
Expected: FAIL, no compila: los métodos no existen.

- [ ] **Step 3: Implement**

- `LoansCustomerLookup`: los tres métodos son copia de `ServiceContractsCustomerLookup:69-111`, con `MergedIntoId == null` y `IdentificationTypeParser.Parse`.
- `LoansMemberDirectory`: suma `IUserDirectory users` al constructor y `FindManagerByEmailAsync`, y corrige su comentario, que dice «sin el correo». `LoansMemberRef` **no cambia**: `GET /assignees` y las listas no deben pagar una consulta de correo por miembro. El método recorre las membresías activas con `ToRefAsync`, y sólo por las que tienen `CanManage` pide `users.GetEmailAsync(membership.UserId, ...)` y compara con `StringComparison.OrdinalIgnoreCase`. Devuelve la primera o `null`.
- `LoanRepository.ListNamesByCustomersAsync`: `AsNoTracking`, filtro por tenant y `customerIds.Contains(loan.CustomerId)`, sin filtrar por estado, con `.Select(loan => new LoanNameRef(loan.CustomerId, loan.Name))`. Lista vacía devuelve `[]` sin consultar.
- `LoansCustomerProvisioning(ICustomerProvisioning customers, ILoansCustomerLookup lookup, CustomersDbContext customersContext)` es copia de `ServiceContractsCustomerProvisioning`, con `Source = "loans"`. Es decir: `ChangeTracker.Clear()` en el `catch`, y relectura ante `CustomersErrorCodes.IdentificationTaken`.
- En el mismo archivo, `LoansIdentificationTypes : ILoansIdentificationTypes` sobre `IdentificationTypeParser.SupportedWireValues`. `LoansPhoneNormalizer` es copia de `ServiceContractsPhoneNormalizer`.
- DI junto a las líneas 1326-1327, con ruta completa: `ILoansCustomerProvisioning` Scoped, `ILoansPhoneNormalizer` y `ILoansIdentificationTypes` Singleton.

No hace falta tocar `Bootstrapper.csproj`: ya referencia Customers, Identity, Tenancy y Loans. `ICustomerProvisioning` no exige permiso: la importación no suma ninguno, como contratos.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet build tests/Modules/Loans/Modules.Loans.IntegrationTests`
Expected: compila. Las pruebas corren en la Task 8.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Loans/Modules.Loans.Application/ILoansCustomerProvisioning.cs src/Bootstrapper/LoansCustomerProvisioning.cs src/Bootstrapper/LoansPhoneNormalizer.cs tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansImportDirectoriesTests.cs
git commit -m "feat(loans): reach customers and members for the import" -- src/Modules/Loans/Modules.Loans.Application/ILoansCustomerProvisioning.cs src/Modules/Loans/Modules.Loans.Application/ILoansCustomerLookup.cs src/Modules/Loans/Modules.Loans.Application/ILoansMemberDirectory.cs src/Modules/Loans/Modules.Loans.Application/ILoanRepository.cs src/Modules/Loans/Modules.Loans.Infrastructure/Persistence/LoanRepository.cs src/Bootstrapper/LoansCustomerProvisioning.cs src/Bootstrapper/LoansPhoneNormalizer.cs src/Bootstrapper/LoansCustomerLookup.cs src/Bootstrapper/LoansMemberDirectory.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansImportDirectoriesTests.cs
```

### Task 5: El análisis compartido

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/ExcelLoanRowDeduplicator.cs`, `LoansImportAnalyzer.cs`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportAnalyzerTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 3 y 4.
- Produces: `static class ExcelLoanRowDeduplicator { static (IReadOnlyList<LoansImportRow> FirstOccurrences, IReadOnlyList<LoansImportRow> Duplicates) Partition(IReadOnlyList<LoansImportRow> rows, Func<LoansImportRow, string?> customerKeyOf); }`
- Produces: `LoansImportAnalysis(string FileName, int TotalRows, LoansImportPlan Plan, IReadOnlyList<LoansImportRowErrorDto> Errors, DateOnly Today, DateTimeOffset Now)`
- Produces: `sealed class LoansImportAnalyzer(IExcelLoansImporter importer, IValidator<ExcelLoanRow> rowRules, ILoansPhoneNormalizer phones, ILoansCustomerLookup customers, ILoanRepository loans, ILoansMemberDirectory members, ITenantClock tenantClock, ITenantDefaultCurrency tenantDefaultCurrency)` con `const int MaxRows = 500`, `const long MaxSizeInBytes = 10 * 1024 * 1024` y `Task<LoansImportAnalysis> AnalyzeAsync(Guid tenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)`

- [ ] **Step 1: Write the failing test**

Fakes como en `ServiceContractsImportAnalyzerTests`: `FakeImporter`, `FakeCustomers.With(name, phone, email, document)`, `FakeLoans` (implementa `ListNamesByCustomersAsync` desde una lista; el resto `NotSupportedException`), `AnaManages` (`FindManagerByEmailAsync` compara con `ana@example.com`), `FixedTenantClock` (`new TenantCalendar(Now, TimeZoneInfo.Utc)`), `CopCurrency`. `Row(...)` arma una `ExcelLoanRow` con `with`.

`Phone = "+573001234567"`. `Row(n)` trae documento `CC 1020304050`, teléfono `300 123 4567`, correo `pan@example.com` y préstamo «Capital de trabajo»; `AnalyzeAsync(customers, [loans,] rows...)`.

| Prueba | Clientes en base | Filas | Espera |
| --- | --- | --- | --- |
| `ANewDocumentWhosePhoneBelongsToAnExistingCustomerCreatesANewCustomer` | `Ferretería` con `Phone` | `Row(2)` | Una fila con `MatchedCustomerId` nulo, sin errores |
| `WithoutADocumentThePhoneDecides` | `Ferretería` con `Phone` y el correo | `Row(2, type: null, number: null)` | `MatchedCustomerId == customers.Ids[0]` |
| `WithoutDocumentAndPhoneTheEmailDecides` | La misma | `Row(2, type: null, number: null, phone: null)` | `MatchedCustomerId == customers.Ids[0]` |
| `TwoCustomersWithThePhoneAreAmbiguous` | `Uno` y `Dos` con `Phone` | `Row(2, type: null, number: null)` | Sin filas; un error `ImportRowCustomerAmbiguous` |
| `TheSameCustomerReachedByDocumentAndByPhoneWithTheSameLoanIsADuplicate` | `Pan` con `Phone` y documento | `Row(2)`, `Row(3, type: null, number: null, loan: "  CAPITAL de trabajo ")` | Filas `[2]`; error `(3, ImportRowDuplicateLoan, "LoanName")` |
| `ANewCustomerRepeatedInTheFileIsCreatedOnceWithItsTwoLoans` | Ninguno | `Row(2)`, `Row(3, type: null, number: null, loan: "Vehículo")` | 2 filas, una sola `NewCustomerKey`, `Summary.CustomersToCreate == 1` |
| `ALoanInTheDatabaseIsLoanExistsOnTheCustomerResolved` | `Pan` con documento; `FakeLoans(Ids[0], "capital de TRABAJO")` | `Row(2)` | Un error `ImportRowLoanExists` |
| `AnAssigneeResolvesWhateverItsCase` | Ninguno | `Row(2) with { AssigneeEmail = "ANA@example.com" }` | `AssigneeMemberId` no nulo |
| `MoreThan500RowsIsAFileError` | Ninguno | 501 filas | `LoansDomainException` con `ImportTooManyRows` |
| `ExactlyTheTopRowCountPasses` | Ninguno | 500 filas de clientes distintos | No lanza |

```csharp
[Theory]
[InlineData(null, 100L, LoansErrorCodes.ImportFileRequired)]
[InlineData("", 100L, LoansErrorCodes.ImportFileRequired)]
[InlineData("prestamos.xlsx", 0L, LoansErrorCodes.ImportFileRequired)]
[InlineData("prestamos.csv", 100L, LoansErrorCodes.ImportExtensionInvalid)]
[InlineData("prestamos.xlsx", 10_485_761L, LoansErrorCodes.ImportTooLarge)]
public async Task TheFileIsRefusedBeforeReadingIt(string? name, long size, string code)
```

La teoría afirma el código y que `FakeImporter` no se leyó.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~LoansImportAnalyzerTests`
Expected: FAIL, no compila.

- [ ] **Step 3: Implement**

Copia de `ServiceContractsImportAnalyzer`, incluidos `MatchAsync` y `LinkNewCustomers`, con estas diferencias:

- `BuildContextAsync` no lee perfiles. `LoanNamesByCustomer` agrupa `loans.ListNamesByCustomersAsync(tenantId, matchedIds, ...)`.
- `managers` recorre sólo los correos no nulos.
- `today` y `now` salen de un solo `tenantClock.GetAsync`: `Today` y `UtcNow`, como `CreateLoanHandler`. No se inyecta `IClock`.

El deduplicador es `ExcelServiceContractsRowDeduplicator.Partition(rows, keyOf)` con `row.LoanKey`, sin la sobrecarga por celda. El mensaje de duplicado usa `nameof(ExcelLoanRow.LoanName)` como campo.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~LoansImportAnalyzerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Loans/Modules.Loans.Application/ExcelLoanRowDeduplicator.cs src/Modules/Loans/Modules.Loans.Application/LoansImportAnalyzer.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportAnalyzerTests.cs
git commit -m "feat(loans): analyze an import file without writing" -- src/Modules/Loans/Modules.Loans.Application/ExcelLoanRowDeduplicator.cs src/Modules/Loans/Modules.Loans.Application/LoansImportAnalyzer.cs tests/Modules/Loans/Modules.Loans.UnitTests/LoansImportAnalyzerTests.cs
```

### Task 6: Lector, plantilla y log con ClosedXML

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/ILoansImportFailureLog.cs`, `src/Modules/Loans/Modules.Loans.Infrastructure/Excel/ClosedXmlLoansImporter.cs`, `Excel/ClosedXmlLoansImportTemplateBuilder.cs`, `LoansImportFailureLog.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Infrastructure/Modules.Loans.Infrastructure.csproj`, `LoansInfrastructureExtensions.cs`, `tests/Modules/Loans/Modules.Loans.UnitTests/Modules.Loans.UnitTests.csproj`, `tests/ArchitectureTests/ArchitectureTests/LoansLayerTests.cs`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/Excel/ClosedXmlLoansImporterTests.cs`, `Excel/ClosedXmlLoansImportTemplateBuilderTests.cs`

**Interfaces:**
- Consumes: `IExcelLoansImporter`, `ILoansImportTemplateBuilder`, `LoansImportColumns` (Task 2).
- Produces: `interface ILoansImportFailureLog { void RowFailed(Guid tenantId, int rowNumber, string stage, Exception exception); }`

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class ClosedXmlLoansImporterTests
{
    private readonly ClosedXmlLoansImporter importer = new();

    private static Stream Workbook(Action<IXLWorksheet> fill, IEnumerable<string>? headers = null)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Préstamos");
        var titles = (headers ?? LoansImportColumns.Ordered).ToArray();
        for (var column = 0; column < titles.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = titles[column];
        }

        fill(sheet);
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static int ColumnOf(string header) => LoansImportColumns.Ordered.ToList().IndexOf(header) + 1;

    private Task<IReadOnlyList<ExcelLoanRow>> ReadAsync(Stream stream) => importer.ReadAsync(stream, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(9)]        // 0%
    [InlineData(10)]       // 0.00%
    public async Task APercentCellIsNormalized(int builtInFormat)
    {
        var rows = await ReadAsync(Workbook(sheet =>
        {
            var cell = sheet.Cell(2, ColumnOf(LoansImportColumns.MonthlyRate));
            cell.Value = 0.015;
            cell.Style.NumberFormat.NumberFormatId = builtInFormat;
        }));

        Assert.Equal("1.5", Assert.Single(rows).MonthlyRatePercent);
    }

    [Fact]
    public async Task ACustomPercentFormatIsNormalized()
    {
        var rows = await ReadAsync(Workbook(sheet =>
        {
            var cell = sheet.Cell(2, ColumnOf(LoansImportColumns.MonthlyRate));
            cell.Value = 0.0175;
            cell.Style.NumberFormat.Format = "0.0000%";
        }));

        Assert.Equal("1.75", Assert.Single(rows).MonthlyRatePercent);
    }

    [Fact]
    public async Task APlainDecimalCellStaysAsWritten()
    {
        var rows = await ReadAsync(Workbook(sheet => sheet.Cell(2, ColumnOf(LoansImportColumns.MonthlyRate)).Value = 1.5));

        Assert.Equal("1.5", Assert.Single(rows).MonthlyRatePercent);
    }

    [Fact]
    public async Task APercentFormatOnTheCapitalIsNotTouched()
    {
        var rows = await ReadAsync(Workbook(sheet =>
        {
            var cell = sheet.Cell(2, ColumnOf(LoansImportColumns.PrincipalAmount));
            cell.Value = 2;
            cell.Style.NumberFormat.NumberFormatId = 9;
        }));

        Assert.Equal("2", Assert.Single(rows).PrincipalAmount);
    }

    [Fact]
    public async Task ADateCellArrivesAsIsoAndBlankRowsAreSkipped()
    {
        var rows = await ReadAsync(Workbook(sheet =>
        {
            sheet.Cell(3, ColumnOf(LoansImportColumns.DisbursedOn)).Value = new DateTime(2026, 1, 15);
            sheet.Cell(2, 1).Value = "   ";
        }));

        var row = Assert.Single(rows);
        Assert.Equal((3, "2026-01-15"), (row.RowNumber, row.DisbursedOn));
    }

    [Fact]
    public async Task HeadersAreFoundWithoutAccentsOrCaseAndInAnyOrder()
    {
        var headers = LoansImportColumns.Ordered.Reverse().Select(h => h.ToUpperInvariant().Replace('Ó', 'O').Replace('É', 'E').Replace('Í', 'I'));

        var rows = await ReadAsync(Workbook(sheet => sheet.Cell(2, 1).Value = "Sin garantía", headers));

        Assert.Equal("Sin garantía", Assert.Single(rows).Note);
    }

    [Fact]
    public async Task AWorkbookWithoutTheColumnsIsHeaderMismatch()
    {
        var error = await Assert.ThrowsAsync<LoansDomainException>(() => ReadAsync(Workbook(_ => { }, ["Cliente"])));

        Assert.Equal(LoansErrorCodes.ImportHeaderMismatch, error.Code);
    }

    [Fact]
    public async Task AFileThatIsNotAWorkbookIsUnreadable()
    {
        var error = await Assert.ThrowsAsync<LoansDomainException>(() => ReadAsync(new MemoryStream("a,b"u8.ToArray())));

        Assert.Equal(LoansErrorCodes.ImportUnreadable, error.Code);
    }
}

public sealed class ClosedXmlLoansImportTemplateBuilderTests
{
    private static XLWorkbook Build() => new(new MemoryStream(new ClosedXmlLoansImportTemplateBuilder().Build(
        ["CC", "NIT"], ["COP", "USD"], TestContext.Current.CancellationToken)));

    [Fact]
    public void TheFirstSheetHasTheFourteenHeadersInOrder()
    {
        using var workbook = Build();
        var data = workbook.Worksheets.First();

        Assert.Equal(LoansImportColumns.Ordered, Enumerable.Range(1, 14).Select(column => data.Cell(1, column).GetString()));
    }

    [Fact]
    public void TheCutoffRuleOffersTheFormLabels()
    {
        using var workbook = Build();
        var reference = workbook.Worksheet("Referencia");

        Assert.Equal(["Último día del mes", "Día del desembolso"], reference.Column(3).CellsUsed().Skip(1).Select(cell => cell.GetString()));
        Assert.Equal(3, workbook.Worksheets.First().DataValidations.Count());
    }

    [Fact]
    public void TheRateColumnStaysGeneralSoExcelMarksAPercent()
    {
        using var workbook = Build();
        var column = LoansImportColumns.Ordered.ToList().IndexOf(LoansImportColumns.MonthlyRate) + 1;

        Assert.Equal(0, workbook.Worksheets.First().Cell(2, column).Style.NumberFormat.NumberFormatId);
    }
}
```

En `LoansLayerTests`:

```csharp
[Fact]
public void ApplicationDoesNotReferenceClosedXml() =>
    Assert.DoesNotContain(ReferenceNamesOf(typeof(LoansPermissions).Assembly), name => name == "ClosedXML");
```

- [ ] **Step 2: Run to verify it fails**

Agrega `<ProjectReference>` a `Modules.Loans.Infrastructure` en `Modules.Loans.UnitTests.csproj` y `<PackageReference Include="ClosedXML" />` en `Modules.Loans.Infrastructure.csproj`. Luego `dotnet restore Backend.slnx`.

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~ClosedXmlLoans`
Expected: FAIL, no compila: las clases no existen.

- [ ] **Step 3: Implement**

`ClosedXmlLoansImporter` es copia de `ClosedXmlServiceContractsImporter` sobre `LoansImportColumns.Ordered`. La columna de tasa pasa por un lector propio:

```csharp
// Una celda con formato de porcentaje guarda la fracción: 1,5 % llega como 0,015. Los formatos integrados 9 (0%) y 10 (0.00%)
// se leen por id, con Format vacío, así que hay que mirar los dos.
private static string? RateCell(IXLRow row, int column)
{
    var cell = row.Cell(column);
    var format = cell.Style.NumberFormat;
    var isPercent = cell.DataType == XLDataType.Number
        && (format.NumberFormatId is 9 or 10 || format.Format.Contains('%', StringComparison.Ordinal));
    return isPercent
        ? ((decimal)cell.GetDouble() * 100m).ToString("0.##########", CultureInfo.InvariantCulture)
        : Cell(row, column);
}
```

La conversión va en `decimal`: en `double`, 0,0175 × 100 da 1,7500000000000002.

`ClosedXmlLoansImportTemplateBuilder` es copia de la de contratos: hoja de datos `Préstamos`, hoja `Referencia`, tres listas (Tipos de documento, Monedas, Reglas de corte, en ese orden y en esas columnas), formato `yyyy-mm-dd` en `Fecha de desembolso`, `ValidationRows = LoansImportAnalyzer.MaxRows`. **La columna de tasa queda sin formato:** sobre una celda General Excel marca porcentaje al teclear `1,5%`, y sobre una con formato numérico guarda 0,015 sin marcarlo.

`LoansImportFailureLog` es copia de `ServiceContractsImportFailureLog`, con mensaje `Loans import row {RowNumber} of tenant {TenantId} failed at {Stage}; it was reported as row.failed.`

En `LoansInfrastructureExtensions`: `IExcelLoansImporter` y `ILoansImportTemplateBuilder` Scoped, `ILoansImportFailureLog` Singleton.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~ClosedXmlLoans`
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter FullyQualifiedName~LoansLayerTests`
Expected: PASS los dos.

- [ ] **Step 5: Commit**

Lista los locks con `git status --short` y agrégalos todos al commit.

```powershell
git add src/Modules/Loans/Modules.Loans.Application/ILoansImportFailureLog.cs src/Modules/Loans/Modules.Loans.Infrastructure/Excel src/Modules/Loans/Modules.Loans.Infrastructure/LoansImportFailureLog.cs tests/Modules/Loans/Modules.Loans.UnitTests/Excel
git commit -m "feat(loans): read and build the import workbook" -- src/Modules/Loans/Modules.Loans.Application/ILoansImportFailureLog.cs src/Modules/Loans/Modules.Loans.Infrastructure tests/Modules/Loans/Modules.Loans.UnitTests tests/ArchitectureTests/ArchitectureTests/LoansLayerTests.cs <los packages.lock.json modificados>
```

### Task 7: Handlers de plantilla, vista previa e importación

**Files:**
- Create: `src/Modules/Loans/Modules.Loans.Application/GetLoansImportTemplate.cs`, `PreviewLoansImport.cs`, `ImportLoans.cs`
- Modify: `src/Modules/Loans/Modules.Loans.Application/ILoansUnitOfWork.cs`, `LoansAuditActions.cs`, `src/Modules/Loans/Modules.Loans.Infrastructure/Persistence/LoansUnitOfWork.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:398-441`
- Test: `tests/Modules/Loans/Modules.Loans.UnitTests/ImportLoansHandlerTests.cs`

**Interfaces:**
- Consumes: `LoansImportAnalyzer` (Task 5), `LoanOpening` (Task 1), `ILoansCustomerProvisioning` (Task 4), `ILoansImportFailureLog` (Task 6).
- Produces: `void Clear()` en `ILoansUnitOfWork`; `LoansAuditActions.ImportCompleted = "loans.import.completed"` y `Resources.Import = "import"`
- Produces: `GetLoansImportTemplateQuery(Guid TenantId) : IQuery<LoansImportTemplateFile>`, `GetLoansImportTemplateHandler` con `const string FileName = "plantilla-prestamos.xlsx"`
- Produces: `PreviewLoansImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content) : ICommand<LoansImportPreviewDto>`, `ImportLoansCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content) : ICommand<LoansImportResultDto>`
- Produces: `ImportLoansHandler(IExecutionContext executionContext, ITenantModules tenantModules, LoansImportAnalyzer analyzer, ILoansCustomerProvisioning provisioning, ILoanRepository loans, ILoanInstallmentRepository installments, ILoansUnitOfWork unitOfWork, ILoansAuditPublisher auditPublisher, ILoansImportFailureLog failureLog)`

- [ ] **Step 1: Write the failing test**

Fakes: `FakeContext` (`SubjectId`, `TenantId = new TenantId(Tenant)`, `HasPermission` configurable), `NoModuleRow` (`ITenantModules.FindAsync` devuelve `null`: el guard pasa), `FakeLoans` y `FakeInstallments` que guardan lo agregado y lo mueven a `Saved` en `SaveChangesAsync`, `FakeUnitOfWork` (falla por nombre de préstamo, cuenta `Clear`), `FakeAudit`, `FakeProvisioning` (cuenta llamadas y devuelve `Guid.NewGuid()`, `Created: true`; lanza si el nombre contiene el texto de `FailOn`), `FakeFailureLog`. El analizador real, armado como en la Task 5.

```csharp
[Fact]
public async Task EachValidRowOpensALoanWithItsInstallmentsAndItsAudit()
{
    var result = await HandleAsync(Row(2), Row(3, number: "999", loan: "Vehículo"));

    Assert.Equal((2, 0, 2), (result.ImportedCount, result.ErrorCount, result.CustomersCreated));
    Assert.Equal(2, loans.Saved.Count);
    Assert.Equal(4, installments.Saved.Count);
    Assert.Equal(2, audit.Count(LoansAuditActions.LoanCreated));
    Assert.Equal(1, audit.Count(LoansAuditActions.ImportCompleted));
}

[Fact]
public async Task TwoLoansOfTheSameNewCustomerCreateItOnce()
{
    var result = await HandleAsync(Row(2), Row(3, type: null, number: null, loan: "Vehículo"));

    Assert.Equal(1, provisioning.Calls);
    Assert.Equal(1, result.CustomersCreated);
    Assert.Single(result.Imported.Select(row => row.CustomerId).Distinct());
}

[Fact]
public async Task AMatchedCustomerIsNeverProvisioned()
{
    customers.With("Pan", document: ("CC", "1020304050"));

    var result = await HandleAsync(Row(2));

    Assert.Equal(0, provisioning.Calls);
    Assert.False(Assert.Single(result.Imported).CustomerCreated);
}

[Fact]
public async Task AFailedWriteClearsTheContextAndTheNextRowStillSaves()
{
    unitOfWork.FailOn("Falla");

    var result = await HandleAsync(Row(2, loan: "Falla"), Row(3, number: "999"));

    Assert.Equal((1, 1), (result.ImportedCount, result.ErrorCount));
    var error = Assert.Single(result.Errors);
    Assert.Equal((2, LoansErrorCodes.ImportRowFailed), (error.RowNumber, error.Code));
    Assert.DoesNotContain("forced", error.Message);
    Assert.Equal([(2, "loan")], failureLog.Rows);
    Assert.DoesNotContain(loans.Saved, loan => loan.Name == "Falla");
    Assert.True(unitOfWork.Clears >= 2);
}

[Fact]
public async Task ACustomerThatCannotBeCreatedFailsItsRowOnly()
{
    provisioning.FailOn("Panadería");

    var result = await HandleAsync(Row(2), Row(3, customer: "Otro", number: "999", phone: "3009999999"));

    Assert.Equal((1, 1, 1), (result.ImportedCount, result.ErrorCount, result.CustomersCreated));
    Assert.Equal([(2, "customer")], failureLog.Rows);
}

[Fact]
public async Task ADomainRefusalDoesNotReachTheFailureLog()
{
    unitOfWork.FailOn("Falla", new LoansDomainException(LoansErrorCodes.LoanInvalid, "x"));

    var result = await HandleAsync(Row(2, loan: "Falla"));

    Assert.Empty(failureLog.Rows);
    Assert.Equal(LoansErrorCodes.ImportRowFailed, Assert.Single(result.Errors).Code);
}

[Fact]
public async Task ErrorCountCountsRowsNotErrors()
{
    var result = await HandleAsync(Row(2) with { PrincipalAmount = "abc", MonthlyRatePercent = "abc" });

    Assert.Equal((1, 2), (result.ErrorCount, result.Errors.Count));
}

[Fact]
public async Task WithoutManageItIsForbiddenBeforeReadingTheFile() =>
    await Assert.ThrowsAsync<RequestForbiddenException>(() => HandleAsync(permissions: [LoansPermissions.Read], Row(2)));
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests --filter FullyQualifiedName~ImportLoansHandlerTests`
Expected: FAIL, no compila.

- [ ] **Step 3: Implement**

`GetLoansImportTemplateHandler` y `PreviewLoansImportHandler` son copia de los de contratos, con `LoansAuthorization.EnsureAsync(..., LoansPermissions.Manage, ...)`.

`ImportLoansHandler` es copia de `ImportServiceContractsHandler`, sin perfiles, con `ResolveCustomerAsync` y `Failed` iguales, y etapas `"customer"` y `"loan"`. `receivedAt`, `today` y `now` salen de `analysis`, sin `IClock`. La escritura de una fila, dentro de `try/catch/finally` con `unitOfWork.Clear()` en el `finally`:

```csharp
var terms = new LoanTerms(customerId, row.PrincipalAmount, planned.Currency, row.MonthlyRatePercent, row.DisbursedOn, row.CutoffRule);
var (loan, generated) = LoanOpening.Open(tenantId, terms, row.LoanName, planned.AssigneeMemberId, row.Note, today, now);
loans.Add(loan);
installments.AddRange(generated);
auditPublisher.Publish(
    tenantId, executionContext.SubjectId, LoansAuditActions.LoanCreated, LoansAuditActions.Resources.Loan,
    loan.Id.ToString(), "success", LoanOpening.AuditedFields, now);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

Al final, `ImportCompleted` con recurso `rows=…;imported=…;customersCreated=…;errors=…` y campos `["rows", "imported", "customersCreated", "errors"]`, guardado con `SaveChangesAsync`.

`LoansUnitOfWork.Clear() => dbContext.ChangeTracker.Clear();`. En `QepServiceCollectionExtensions`, con la ruta completa de la sección Loans, los tres handlers y `AddScoped<LoansImportAnalyzer>()`. `ExcelLoanRowRules` lo registra `AddValidatorsFromAssemblyContaining<CreateLoanValidator>` (línea 1056): no hace falta otra línea.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Modules/Loans/Modules.Loans.UnitTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Loans/Modules.Loans.Application/GetLoansImportTemplate.cs src/Modules/Loans/Modules.Loans.Application/PreviewLoansImport.cs src/Modules/Loans/Modules.Loans.Application/ImportLoans.cs tests/Modules/Loans/Modules.Loans.UnitTests/ImportLoansHandlerTests.cs
git commit -m "feat(loans): import loans one transaction per row" -- src/Modules/Loans/Modules.Loans.Application src/Modules/Loans/Modules.Loans.Infrastructure/Persistence/LoansUnitOfWork.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Loans/Modules.Loans.UnitTests/ImportLoansHandlerTests.cs
```

### Task 8: Endpoints e integración de punta a punta

**Files:**
- Modify: `src/Modules/Loans/Modules.Loans.Api/LoansEndpoints.cs:33`
- Test: `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansImportApiTests.cs`

**Interfaces:**
- Consumes: los comandos y la query de la Task 7.
- Produces: las tres rutas de § Contrato HTTP.

- [ ] **Step 1: Write the failing test**

Arranque con `MutableTenantClock(new DateTimeOffset(2026, 3, 10, 13, 0, 0, TimeSpan.Zero))` (hoy 2026-03-10 en Bogotá), registrado como en `LoanApiTests:30-34`. `Workbook`, `Upload`, `CountAsync` y el trigger `FailInsertsNamedFallaAsync` son copia de `ServiceContractsImportApiTests`. Filas de texto con los 14 encabezados. `SeedAnaAsync` siembra `ana@example.com` con rol `admin`.

```csharp
[Fact]
public async Task TheTemplateDownloadsWithTheFourteenHeaders()  // 200, content type de Excel, y encabezados == LoansImportColumns.Ordered

[Fact]
public async Task PreviewAnswersRowsErrorsAndInstallmentsWithoutWriting()
{
    // Fila 2 válida, desembolso 2026-01-15; fila 3 con Capital "abc".
    // validRows.Count == 1, validRows[0].InstallmentsToGenerate == 2, summary.InstallmentsToGenerate == 2,
    // errors[0] == (3, "loans.import.row.principal_invalid", "PrincipalAmount").
    // SELECT count(*) FROM loans.loans == 0 y FROM customers.customers == 0.
}

[Fact]
public async Task ImportCreatesCustomersLoansInstallmentsAndAudit()
{
    // Dos préstamos del mismo cliente nuevo.
    // customersCreated == 1, importedCount == 2; customers.customers == 1, loans.loans == 2, loans.installments == 4;
    // audit.entries con 'loans.loan.created' == 2 y con 'loans.import.completed' == 1.
}

[Fact]
public async Task AnImportedLoanEqualsOnePostedWithTheSameData()
{
    // POST /loans y una fila del Excel con los mismos datos y otro nombre, mismo cliente.
    // GET /loans/{id} de cada uno: PrincipalAmount, OutstandingPrincipal, Currency, MonthlyRatePercent, DisbursedOn,
    // CutoffRule, LastGeneratedPeriodEnd y (PeriodStart, PeriodEnd, Amount) de cada cuota son iguales.
}

[Fact]
public async Task ASecondUploadOfTheSameFileCreatesNothing()
{
    // Importa dos filas; reenvía los mismos bytes.
    // importedCount == 0, errors.Select(Code) == ["loans.import.row.loan_exists" x2]; loans.loans == 2, customers.customers == 1.
}

[Fact]
public async Task ADeletedLoanCanBeImportedAgain()  // Importa, DELETE /loans/{id}, reimporta: importedCount == 1, customersCreated == 0

[Fact]
public async Task ASettledLoanStillCountsAsExisting()  // UPDATE loans.loans SET status = 'Settled'; reimporta: loan_exists

[Fact]
public async Task AFailedRowKeepsItsCustomerAndARetryDoesNotDuplicateIt()
{
    // Trigger sobre loans.loans para name = 'Falla'. Fila cliente nuevo + 'Falla' y otra fila válida de otro cliente.
    // importedCount == 1, errors[0].Code == "loans.import.row.failed", customers.customers == 2.
    // DROP TRIGGER, reenvía: importedCount == 1, customersCreated == 0, customers.customers == 2.
}

[Fact]
public async Task ALoanCreatedBetweenPreviewAndConfirmBecomesARowError()  // Preview OK, POST /loans con ese nombre, import: loan_exists

[Fact]
public async Task AMatchedCustomerIsNeverModified()
{
    // SeedCustomerAsync("Pan", "+573001234567", "viejo@example.com"); fila sin documento, mismo teléfono, otro nombre y correo.
    // customers.customers.name == 'Pan', email == 'viejo@example.com', version == 1.
}

[Fact]
public async Task AnAssigneeResolvesAndOneWithoutManageIsARowError()  // SeedMemberAsync con "admin" y con "advisor"

[Fact]
public async Task MoreThan500RowsAnswers422()  // code == loans.import.too_many_rows
[Fact]
public async Task AFileWithOtherHeadersAnswers422()  // code == loans.import.header_mismatch
[Fact]
public async Task WithoutAFileAnswers422()  // code == loans.import.file_required

[Fact]
public async Task WithoutManageAllThreeAnswer403()  // CreateReader
[Fact]
public async Task WithTheModuleOffAllThreeAnswer403()  // OpenAsync(modules: TenantModuleKeys.All sin Loans)
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build tests/Modules/Loans/Modules.Loans.IntegrationTests`
Expected: compila: el archivo sólo habla HTTP. No se corre: las rutas responderían 404 y la integración corre una sola vez, en el Step 4.

- [ ] **Step 3: Implement**

Antes de `/{loanId:guid}`, las tres rutas son copia de `ServiceContractsEndpoints.cs:123-142`: `RequireAuthorization(LoansPermissions.Manage)`, `DisableAntiforgery()` en los dos `POST`, `ExcelContentType` y `IFormFile? file` anulable, con `file?.OpenReadStream() ?? Stream.Null`, para que sin archivo responda 422 y no 400.

- [ ] **Step 4: Run to verify it passes**

```powershell
Stop-Process -Name Api -ErrorAction SilentlyContinue
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests
dotnet test tests/Modules/Loans/Modules.Loans.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet build Backend.slnx
```

Expected: PASS y build limpio. Corre la clase `LoansImportDirectoriesTests` de la Task 4 y las `LoanApiTests` que cubren el refactor de la Task 1.

- [ ] **Step 5: Commit**

```powershell
git add tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansImportApiTests.cs
git commit -m "feat(loans): expose the template, preview and import endpoints" -- src/Modules/Loans/Modules.Loans.Api/LoansEndpoints.cs tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansImportApiTests.cs
```

---

## Despliegue

Backend primero. Sin migración. Un frontend viejo no ve nada nuevo.
