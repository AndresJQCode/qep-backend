# Legal parameters operator console — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **The workspace `CLAUDE.md` rules override the skill:** 3 blocks, one implementer per block, one commit per task, one review per block, one strong review at the end.

**Goal:** The platform operator creates, edits and deletes rows of `hr.legal_parameters` and the `SolidarityFund` bracket sets of `hr.legal_parameter_brackets` from the operator console (manual and yearly Excel load), with history, a usage guard against calculated payrolls and a shared Valkey cache.

**Architecture:** Domain rules, labels and the usage rule live in `Modules.Hr.Domain`; one shared `LegalParameterWriter` in `Modules.Hr.Application` applies manual and Excel edits in one unit of work, writes `hr.legal_parameter_changes` and bumps the cache version after commit. Payroll exposes a read port of calculated periods; the Bootstrapper adapts it to Hr's `ILegalParameterUsage` and decorates `ILegalParameterSource` with the cache (Valkey area `LegalParameters`, or a 60 s pod-memory cache when the area is off).

**Tech Stack:** .NET 10, EF Core + Npgsql, StackExchange.Redis (Valkey), ClosedXML, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-22-parametros-legales-consola-operador-design.md`

## Global Constraints

- Worktree `qep-backend-worktrees/legal-parameters-console`, branch `feature/legal-parameters-console` from `develop`. Never touch `main`; never edit `docs/estado.md` on this branch.
- Conventional commits, **no AI attribution** (ignore any `Co-Authored-By` trailer the harness suggests).
- TDD: literal RED and GREEN output per task. A compile-error RED is shown with `dotnet build` output, no containers.
- Integration runs filtered by test name while iterating (`--filter "FullyQualifiedName~<TestName>"`). Before any Testcontainers run: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; if a `testhost` is running, wait (never stop it). Tests run in the foreground.
- `Api.exe` running locks the build: if `dotnet build` fails with a locked file, tell the owner; do not stop it.
- Hr never references Payroll nor `StackExchange.Redis`/EF Core in Application (`HrLayerTests`). Payroll never references Hr (`PayrollLayerTests`).
- Operator handlers authorize first with `OperatorAuthorization.EnsureAuthorized(executionContext, tenantId, permission, operatorTenant)` and **do not** check the `hr` module (`ITenantModules`).
- HTTP status comes from the exception type (`ApiExceptionHandler.MapException`): `in_use` → `ResourceConflictException` (409, with extensions via `IHasProblemExtensions`), `key_unknown` → `ResourceNotFoundException` (404), everything else → `HrDomainException` (422). No per-code entries are added to `ApiExceptionHandler`.
- Error codes (exact): `hr.legal_parameter.in_use`, `.value_invalid`, `.date_invalid`, `.last_row`, `.key_unknown`; import file codes `hr.legal_parameter.import.{file_required, extension_invalid, too_large, unreadable, header_mismatch, empty, too_many_rows, rows_invalid}`; preview warning `hr.legal_parameter.import.next_year_missing`; row codes `hr.legal_parameter.import.row.{key_unknown, date_invalid, value_invalid, brackets_invalid, duplicated, in_use}`.
- Precision: `value numeric(18,6)`; bracket bounds `numeric(9,4)` (max `99999.9999`), rate `numeric(18,6)`. More decimals than the column ⇒ `value_invalid`, never silent rounding.
- Yearly entries = 27 keys (all except `SundaySurchargeRate`, `WeeklyHours`, `ConnectivityAllowanceSince`) + the `SolidarityFund` set = 28. "Missing for year Y" ⇔ no row/set with `effectiveFrom == Y-01-01`. `nextYearMissing` only from December 1 (operator calendar, `ITenantClock`); `currentYearMissing` always.
- Cache: `qep:legalparams:ver` counter, table at `qep:legalparams:{ver}`, TTL 1 h; version read **before** loading; bump **after** commit, without the caller's token. Area off ⇒ pod memory, TTL 60 s. Valkey down ⇒ read the database.
- Excel: `.xlsx`, ≤ 1 MB, ≤ 500 data rows (both sheets together). Endpoints take `IFormFile?` and `.DisableAntiforgery()`; no file ⇒ 422 `file_required`.
- User-facing messages in Colombian Spanish, tuteo. Code comments in Spanish with tuteo, like the rest of Hr.
- Migration only adds the table and nullable columns; it never updates seeded rows and does not touch the `CHECK` of `key`.

## Review Focus

1. A retroactive row created between two existing rows whose range holds a second-fortnight period (`legal_values_date` = the 16th) ⇒ 409 with counts; the same row dated after the last calculated period is accepted. (Task 3, Task 6.)
2. A rate typed as `12` in a cell without percent format ⇒ row `Error` with the exact message «La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de porcentaje?», and nothing is applied. (Task 9.)
3. The same `(key, date)` twice in one file, or the same bracket set split by blank rows ⇒ `duplicated` error on the second occurrence / one set entry with all its rows. (Task 9.)
4. Valkey fails between commit and `INCR` ⇒ the write returns 200 with the new data, the failure is counted in `qep.valkey.fallbacks` and logged; a reader that started before the commit cannot overwrite the new version. (Task 7.)
5. Deleting the only row of a key or the only bracket set ⇒ 422 `last_row`; a route key with the wrong case (`smmlv`) or a number (`0`) ⇒ 404 `key_unknown`. (Task 5, Task 6.)

---

## Block A — Domain, persistence and the writer (Tasks 1–5)

### Task 1: Labels, groups and units

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Domain/LegalParameterLabels.cs` (also holds `LegalParameterGroup`, `LegalParameterUnit`, `LegalParameterLabel`)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParameterLabelsTests.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/LegalParameterLabelsMatchLegalBasisTests.cs`

**Interfaces — Produces:**
```csharp
public enum LegalParameterGroup { SalaryAndCaps, Allowances, Contributions, Provisions, WorkingTime }
public enum LegalParameterUnit { Currency, Fraction, SmmlvMultiple, Hours, Flag }
public sealed record LegalParameterLabel(string Name, LegalParameterGroup Group, LegalParameterUnit Unit, bool Yearly);
public static class LegalParameterLabels
{
    public static LegalParameterLabel Of(LegalParameterKey key);          // throws on a key without entry
    public static string NameOf(LegalBracketKey key);
    public static IReadOnlyList<LegalParameterKey> YearlyKeys { get; }    // 27, in enum order
}
```
Groups exactly as spec «Nombres y grupos». Units: `Smmlv`, `IntegralSalaryFloor`, `TransportAllowance` → `Currency`; every `*Rate` (including `NonSalaryCapRate`, `SundaySurchargeRate`) → `Fraction`; `TransportAllowanceCapSmmlv`, `ExonerationThresholdSmmlv`, `IbcCapSmmlv` → `SmmlvMultiple`; `WeeklyHours` → `Hours`; `ConnectivityAllowanceSince` → `Flag`. Names copied verbatim from `LegalBasis` (`src/Modules/Payroll/Modules.Payroll.Domain/Engine/LegalBasis.cs:13-43`).

- [ ] **Step 1: Write failing tests**
  - `EveryKeyHasALabel`: `Enum.GetValues<LegalParameterKey>()` all resolve; 30 keys.
  - `YearlyKeysAreTheTwentySevenThatPayrollAndHrRequire`: `YearlyKeys.Count == 27`, does not contain the three non-yearly keys, contains `IntegralSalaryFloor`.
  - `GroupsAndUnitsFollowTheSpec`: `Of(Smmlv) == new("Salario mínimo mensual legal vigente", SalaryAndCaps, Currency, true)`; `Of(NonSalaryCapRate).Group == SalaryAndCaps`; `Of(ConnectivityAllowanceSince) == (…, Allowances, Flag, false)`; `Of(WeeklyHours).Unit == Hours`; `Of(IbcCapSmmlv).Unit == SmmlvMultiple`.
  - Bootstrapper: `NamesMatchLegalBasis`: for every `LegalParameterKey`, `LegalParameterLabels.Of(k).Name == LegalBasis` name of `k.ToString()`; `NameOf(SolidarityFund)` equals `LegalBasis` `"SolidarityFund"` name. Use whatever public accessor `LegalBasis` exposes (read the file).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests` fails: `LegalParameterLabels` not found.
- [ ] **Step 3: Implement** `LegalParameterLabels` (a `FrozenDictionary` or switch).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Hr/Modules.Hr.UnitTests --filter "FullyQualifiedName~LegalParameterLabelsTests"` and `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~LegalParameterLabelsMatchLegalBasisTests"` pass.
- [ ] **Step 5: Commit** `feat(hr): legal parameter labels, groups and units`

### Task 2: Value and bracket rules

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Domain/LegalParameterRules.cs` (also `LegalBracketInput`)
- Modify: `src/Modules/Hr/Modules.Hr.Domain/HrErrorCodes.cs` (all codes of Global Constraints; `in_use` is **not** added to `Conflicts` — it is thrown as `ResourceConflictException` directly)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParameterRulesTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record LegalBracketInput(decimal LowerBound, decimal? UpperBound, decimal Rate);
public static class LegalParameterRules
{
    public const string RateOutOfRangeMessage =
        "La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de porcentaje?";
    public static string? Check(LegalParameterKey key, decimal value);                 // null = valid; else Spanish message
    public static void EnsureValid(LegalParameterKey key, decimal value);              // HrDomainException(LegalParameterValueInvalid)
    public static string? CheckBrackets(IReadOnlyList<LegalBracketInput> brackets);    // null = valid
    public static void EnsureValidBrackets(IReadOnlyList<LegalBracketInput> brackets); // HrDomainException(LegalParameterValueInvalid)
}
```
Rules from spec «Validación por clave» and «Tramos»: `Fraction` unit ⇒ `(0, 1]` (out of range ⇒ `RateOutOfRangeMessage`); `Currency` and `SmmlvMultiple` ⇒ `> 0`; `Hours` ⇒ `(0, 168]`; `Flag` ⇒ exactly `1`; > 6 decimals ⇒ invalid. Brackets: ≥ 1, sorted by `LowerBound`, first `≥ 0`, contiguous, `UpperBound > LowerBound`, `null` only in the last, rate `(0, 1]` with ≤ 6 decimals, bounds ≤ 4 decimals and ≤ `99999.9999`.

- [ ] **Step 1: Failing tests** (theories): `RatesMustBeAFractionUpToOne` (`0.04` ok, `1.00` ok, `0` / `1.0000001` / `12` ⇒ `RateOutOfRangeMessage`); `AmountsAndCapsMustBePositive`; `WeeklyHoursUpTo168`; `ConnectivityIsExactlyOne` (`2` invalid); `MoreThanSixDecimalsIsInvalid` (`0.1234567`); `EnsureValidThrowsValueInvalid` (code `hr.legal_parameter.value_invalid`); `TheSeededSolidaritySetIsValid` (4–16 / 16–17 / … / 20–25 as seeded — read `20261122015359_AddPayrollLegalParameters.cs` for the exact rows); brackets invalid cases: empty, gap (`4–16`, `17–18`), overlap, unsorted, `null` upper not last, `upper <= lower`, negative first, rate `0`, bound with 5 decimals.
- [ ] **Step 2: RED** — build fails (`LegalParameterRules` missing).
- [ ] **Step 3: Implement.** Unit comes from `LegalParameterLabels.Of(key).Unit`.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~LegalParameterRulesTests"`.
- [ ] **Step 5: Commit** `feat(hr): validation rules for legal parameter values and brackets`

### Task 3: Usage rule and yearly coverage

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Domain/LegalParameterUsageRule.cs` (also `CalculatedPeriodStamp`, `LegalParameterUsageSummary`)
- Create: `src/Modules/Hr/Modules.Hr.Domain/LegalParameterYears.cs`
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParameterUsageRuleTests.cs`, `LegalParameterYearsTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record CalculatedPeriodStamp(DateOnly LegalValuesDate, Guid CompanyId); // one per calculated period
public sealed record LegalParameterUsageSummary(int PeriodsAffected, int CompaniesAffected, DateOnly From, DateOnly To);
public static class LegalParameterUsageRule
{
    // [effectiveFrom, next date > effectiveFrom among otherEffectiveDates) — otherEffectiveDates excludes effectiveFrom itself.
    public static LegalParameterUsageSummary? Affected(
        DateOnly effectiveFrom, IEnumerable<DateOnly> otherEffectiveDates, IReadOnlyCollection<CalculatedPeriodStamp> stamps);
}
public static class LegalParameterYears
{
    public static bool MissingFor(IEnumerable<DateOnly> effectiveDates, int year);   // no date == year-01-01
    public static bool NextYearWatchOpen(DateOnly today);                            // today.Month == 12
}
```
The same call serves create, edit and delete: the range is always `[E, next other date)`.

- [ ] **Step 1: Failing tests:** `NoStampInRangeIsNull`; `FirstAndSecondFortnightBothCount` (row 2026-01-01, stamps 2026-01-01 and 2026-01-16 of company A ⇒ `(2, 1, 01-01, 01-16)`); `TheNextRowClosesTheRange` (rows 2025-01-01 and 2026-01-01, stamp 2026-01-16 ⇒ row 2025 is `null`); `ARetroactiveRowBetweenRowsCatchesTheSixteenth` (E = 2026-01-10, others {2026-01-01}, stamp 2026-01-16 ⇒ 1 period); `AFutureRowWithoutPeriodsIsFree` (E = 2027-01-01); `CompaniesAreCountedDistinct`. Years: `MissingWhenNoJanuaryFirst` (2027-03-01 only ⇒ missing for 2027), `WatchOpensOnDecemberFirst` (11-30 false, 12-01 true).
- [ ] **Step 2: RED** — build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~LegalParameterUsageRuleTests|FullyQualifiedName~LegalParameterYearsTests"`.
- [ ] **Step 5: Commit** `feat(hr): usage rule against calculated payrolls and yearly coverage`

### Task 4: Entities, history table, migration and repository

**Files:**
- Modify: `src/Modules/Hr/Modules.Hr.Domain/LegalParameterValue.cs`, `LegalParameterBracket.cs` (add `UpdatedAt`, `UpdatedBy`; update the «Sin pantalla» summary)
- Create: `src/Modules/Hr/Modules.Hr.Domain/LegalParameterChange.cs` (also `LegalParameterChangeAction`, `LegalParameterChangeSource`)
- Create: `src/Modules/Hr/Modules.Hr.Application/ILegalParameterRepository.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/LegalParameterRepository.cs`
- Modify: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/HrDbContext.cs` (`ConfigureLegalParameter` + new `DbSet<LegalParameterChange> LegalParameterChanges`)
- Modify: `src/Modules/Hr/Modules.Hr.Infrastructure/HrInfrastructureExtensions.cs` (register repository)
- Create (generated): `Persistence/Migrations/<ts>_AddLegalParameterChanges.cs` + Designer + snapshot
- Test: `tests/Modules/Hr/Modules.Hr.IntegrationTests/LegalParameterPersistenceTests.cs`

**Interfaces — Produces:**
```csharp
// LegalParameterValue / LegalParameterBracket
public DateTimeOffset? UpdatedAt { get; private set; }
public Guid? UpdatedBy { get; private set; }
public static LegalParameterValue Create(LegalParameterKey key, DateOnly effectiveFrom, decimal value, Guid by, DateTimeOffset at);
public void Replace(decimal value, Guid by, DateTimeOffset at);
public static LegalParameterBracket Create(LegalBracketKey key, DateOnly effectiveFrom, LegalBracketInput input, Guid by, DateTimeOffset at);

public enum LegalParameterChangeAction { Created, Updated, Deleted }
public enum LegalParameterChangeSource { Manual, Excel }
public sealed class LegalParameterChange
{
    public static LegalParameterChange Record(string key, DateOnly effectiveFrom, LegalParameterChangeAction action,
        string? oldValueJson, string? newValueJson, Guid changedBy, string changedByName, DateTimeOffset changedAt,
        LegalParameterChangeSource source, Guid? batchId, string? fileName);   // Id = Guid.CreateVersion7()
    // properties: Id, Key, EffectiveFrom, Action, OldValue, NewValue, ChangedBy, ChangedByName, ChangedAt, Source, BatchId, FileName
}

public interface ILegalParameterRepository
{
    Task<IReadOnlyList<LegalParameterValue>> ListValuesAsync(CancellationToken cancellationToken);    // tracked, uncached
    Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken); // tracked, uncached
    void Add(LegalParameterValue value);
    void Remove(LegalParameterValue value);
    void AddBrackets(IEnumerable<LegalParameterBracket> brackets);
    void RemoveBrackets(IEnumerable<LegalParameterBracket> brackets);
    void AddChange(LegalParameterChange change);
    Task<(IReadOnlyList<LegalParameterChange> Items, int Total)> ListChangesAsync(int page, int pageSize, CancellationToken cancellationToken); // newest first (changed_at desc, id desc)
}
```
Table `hr.legal_parameter_changes`: `id uuid pk`, `key varchar(32)`, `effective_from date`, `action varchar(16)`, `old_value jsonb null`, `new_value jsonb null`, `changed_by uuid`, `changed_by_name varchar(200)`, `changed_at timestamptz`, `source varchar(16)`, `batch_id uuid null`, `file_name varchar(255) null`; index on `changed_at`. Columns `updated_at timestamptz null`, `updated_by uuid null` on both existing tables. No `kind` column: `Brackets` ⇔ key is a `LegalBracketKey` name.

- [ ] **Step 1: Failing test** (`LegalParameterPersistenceTests`, Testcontainers): `TheMigrationKeepsSeededRowsWithoutAuthor` (all seeded rows have `UpdatedAt == null`, the 2026 Smmlv value is unchanged — read the seed for the value); `AChangeRoundTripsWithJson` (add a change with `new_value` `"[{\"lowerBound\":4,...}]"`, reload, equal); `ChangesPageNewestFirst`.
- [ ] **Step 2: RED** — build fails (`LegalParameterChange` / repository missing).
- [ ] **Step 3: Implement** entities, mapping, repository; then generate the migration:
  `dotnet ef migrations add AddLegalParameterChanges --project src/Modules/Hr/Modules.Hr.Infrastructure --context HrDbContext -o Persistence/Migrations`. Inspect `Up`: only `CreateTable` + `AddColumn` (nullable) + index; `Down` drops exactly those. No `UpdateData`/`Sql`.
- [ ] **Step 4: GREEN** — check `testhost`, then `dotnet test tests/Modules/Hr/Modules.Hr.IntegrationTests --filter "FullyQualifiedName~LegalParameterPersistenceTests"`.
- [ ] **Step 5: Commit** `feat(hr): legal parameter change history and authorship columns`

### Task 5: Permissions, ports and the shared writer

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/HrOperatorPermissions.cs`
- Create: `src/Modules/Hr/Modules.Hr.Application/ILegalParameterUsage.cs`, `ILegalParameterCacheVersion.cs`
- Create: `src/Modules/Hr/Modules.Hr.Application/LegalParameterWriter.cs` (also `LegalParameterEdit`, `LegalBracketSetEdit`, `LegalParameterChangeOrigin`, `LegalParameterWriteResult`, `LegalParameterInUseException`)
- Create: `src/Modules/Hr/Modules.Hr.Application/LegalParameterChangeJson.cs`
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParameterWriterTests.cs` (in-memory fakes of the ports, like other Hr unit tests' fakes)

**Interfaces — Produces:**
```csharp
public static class HrOperatorPermissions
{
    public const string LegalParametersRead = "operator.legal_parameters.read";
    public const string LegalParametersManage = "operator.legal_parameters.manage";
}
public interface ILegalParameterUsage
{
    /// All tenants: legal values are platform data.
    Task<IReadOnlyList<CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(CancellationToken cancellationToken);
}
public interface ILegalParameterCacheVersion { Task BumpAsync(CancellationToken cancellationToken); } // never throws

public sealed record LegalParameterEdit(LegalParameterKey Key, DateOnly EffectiveFrom, decimal? Value);                  // Value null ⇒ delete
public sealed record LegalBracketSetEdit(LegalBracketKey Key, DateOnly EffectiveFrom, IReadOnlyList<LegalBracketInput>? Brackets); // null ⇒ delete
public sealed record LegalParameterChangeOrigin(LegalParameterChangeSource Source, Guid? BatchId, string? FileName)
{ public static readonly LegalParameterChangeOrigin Manual = new(LegalParameterChangeSource.Manual, null, null); }
public sealed record LegalParameterWriteResult(int Created, int Updated, int Deleted, int Unchanged);

public sealed class LegalParameterInUseException(LegalParameterUsageSummary usage)
    : ResourceConflictException(HrErrorCodes.LegalParameterInUse, "<Spanish message>"), IHasProblemExtensions
{   // extensions: periodsAffected, companiesAffected, from ("yyyy-MM-dd"), to ("yyyy-MM-dd") }

public sealed class LegalParameterWriter(
    ILegalParameterRepository repository, ILegalParameterUsage usage, IHrUnitOfWork unitOfWork,
    ILegalParameterCacheVersion cacheVersion, IExecutionContext executionContext,
    IHrMemberDirectory members, IClock clock)
{
    public Task<LegalParameterWriteResult> ApplyAsync(
        Guid operatorTenantId, IReadOnlyList<LegalParameterEdit> parameters, IReadOnlyList<LegalBracketSetEdit> brackets,
        LegalParameterChangeOrigin origin, CancellationToken cancellationToken);
}
public static class LegalParameterChangeJson
{
    public static string Number(decimal value);                                   // "1750905" invariant
    public static string Brackets(IReadOnlyList<LegalBracketInput> brackets);     // camelCase array
    public static decimal? ReadNumber(string? json);
    public static IReadOnlyList<LegalBracketInput>? ReadBrackets(string? json);
}
```
`ApplyAsync` order: load rows and stamps once; for each edit — validate (`EnsureValid`/`EnsureValidBrackets`); unchanged (same value / same set) ⇒ count, no write, no history, no usage check; delete of the only row/set of its key ⇒ `HrDomainException(LastRow)`; delete of a `(key, date)` that does not exist ⇒ 404 `ResourceNotFoundException(HrErrorCodes.LegalParameterKeyUnknown, "No hay una fila de <name> que rija desde <date>.")` (the spec names no code for it; recorded as an owner question in Task 10). Usage via `LegalParameterUsageRule.Affected(E, other dates of that key **after all edits of the batch**, stamps)` ⇒ throw `LegalParameterInUseException`. Then write rows (`Create`/`Replace`/`Remove`, stamping `executionContext.SubjectId` and `clock.UtcNow`), one `LegalParameterChange` per changed row/set with origin; actor name = `members.FindBySubjectAsync(operatorTenantId, subjectId)` display name or `"Sin nombre"` (as `ActivityWriter`). One `SaveChangesAsync`; then, only if something was written, `cacheVersion.BumpAsync(CancellationToken.None)`.

- [ ] **Step 1: Failing tests:** `CreatingWritesTheRowAndOneManualChange` (action `Created`, `oldValue` null, `newValue` "2000000", source `Manual`, batch null); `UpdatingKeepsOldAndNewValues`; `TheSameValueIsUnchangedWithoutHistoryNorBump`; `DeletingTheLastRowIsLastRow`; `ARowInUseIsRejectedWithCounts` (exception extensions `periodsAffected=2, companiesAffected=1, from="2026-01-01", to="2026-01-16"`); `ACarryInAUsedRangeIsRejected` (same value as in force, new date 2026-01-10, stamp 01-16 ⇒ in_use); `ReplacingASetRemovesTheOldBrackets`; `AnExcelBatchSharesBatchIdAndFileName`; `TheVersionIsBumpedOnceAfterSaving` (fake records order `Save` then `Bump`, one bump for 3 edits); `AnInvalidValueWritesNothing`.
- [ ] **Step 2: RED** — build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~LegalParameterWriterTests"`; then the whole `Modules.Hr.UnitTests` once (end of block) and `tests/ArchitectureTests` once.
- [ ] **Step 5: Commit** `feat(hr): legal parameter writer with usage guard and history`

**End of Block A:** one review of Tasks 1–5.

---

## Block B — API, Payroll usage port and cache (Tasks 6–7)

### Task 6: Operator API, permissions and the usage adapter

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/LegalParameterOperator.cs` (queries, commands, handlers), `LegalParameterDtos.cs`, `LegalParametersComposer.cs`, `LegalParameterRoute.cs`
- Create: `src/Modules/Hr/Modules.Hr.Api/LegalParameterOperatorEndpoints.cs`
- Modify: `src/Api/Program.cs` (`app.MapLegalParameterOperatorEndpoints();` next to `MapTaxUnitOperatorEndpoints`, line ~373)
- Create: `src/Modules/Payroll/Modules.Payroll.Application/IPayrollCalculatedPeriods.cs`
- Create: `src/Modules/Payroll/Modules.Payroll.Infrastructure/Persistence/PayrollCalculatedPeriods.cs` (+ register in `PayrollInfrastructureExtensions`)
- Create: `src/Bootstrapper/LegalParameterUsage.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — handlers + `LegalParametersComposer` + `LegalParameterWriter` next to the Hr block (~line 497–615); `ILegalParameterUsage` next to `IPayrollLegalValues` (~2155); temporary `NullLegalParameterCacheVersion` registration (replaced in Task 7); permissions in the three tax-unit places: admin list (~2841), `PermissionDefinition` (~3660, category `"Operator"`, `RequiredModules: []`, risk `medium`/`high`, `Shape = new("operator.legal_parameters", "Parámetros legales", "read"|"manage")`), policy (~4601)
- Modify: `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrApiHarness.cs` (`QepApiFactory` gains optional `Guid? operatorTenantId` ⇒ `UseSetting("Platform:OperatorTenantId", …)`)
- Test: `tests/Modules/Hr/Modules.Hr.IntegrationTests/LegalParameterOperatorApiTests.cs`, `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollCalculatedPeriodsTests.cs`

**Interfaces — Produces:**
```csharp
// Payroll.Application
public sealed record PayrollCalculatedPeriod(DateOnly LegalValuesDate, Guid CompanyId);
public interface IPayrollCalculatedPeriods
{   /// Every tenant (PayrollDbContext has no tenant filter): periods with legal_values_date not null, one row per period.
    Task<IReadOnlyList<PayrollCalculatedPeriod>> ListOfAllTenantsAsync(CancellationToken cancellationToken); }

// Hr.Application
public static class LegalParameterRoute
{
    public static LegalParameterKey ParseKey(string raw);        // exact enum name, case-sensitive, numbers rejected ⇒ ResourceNotFoundException(key_unknown)
    public static LegalBracketKey ParseBracketKey(string raw);   // same rule
    public static DateOnly ParseDate(string raw);                // "yyyy-MM-dd" invariant ⇒ else HrDomainException(date_invalid)
}
public sealed record ListLegalParametersQuery(TenantId TenantId) : IQuery<LegalParametersDto>;
public sealed record SetLegalParameterCommand(TenantId TenantId, string Key, string EffectiveFrom, decimal Value) : ICommand<LegalParametersDto>;
public sealed record DeleteLegalParameterCommand(TenantId TenantId, string Key, string EffectiveFrom) : ICommand<LegalParametersDto>;
public sealed record SetLegalBracketsCommand(TenantId TenantId, string Key, string EffectiveFrom, IReadOnlyList<LegalBracketInput> Brackets) : ICommand<LegalParametersDto>;
public sealed record DeleteLegalBracketsCommand(TenantId TenantId, string Key, string EffectiveFrom) : ICommand<LegalParametersDto>;
public sealed record ListLegalParameterHistoryQuery(TenantId TenantId, int Page, int PageSize) : IQuery<LegalParameterHistoryPageDto>;
public sealed class LegalParametersComposer(ILegalParameterRepository repository, ILegalParameterUsage usage, ITenantClock tenantClock)
{ public Task<LegalParametersDto> ComposeAsync(TenantId operatorTenantId, CancellationToken cancellationToken); }
```
DTOs (`LegalParameterDtos.cs`), names exactly as the spec's JSON («Forma de las respuestas»); enums travel as names:
`LegalParametersDto(DateOnly Today, int CurrentYear, int CurrentYearMissingCount, int NextYear, int NextYearMissingCount, IReadOnlyList<LegalParameterKeyDto> Parameters, IReadOnlyList<LegalBracketKeyDto> Brackets)`;
`LegalParameterKeyDto(string Key, string Name, string Group, string Unit, bool Yearly, bool NextYearMissing, bool CurrentYearMissing, LegalParameterCurrentDto? Current, IReadOnlyList<LegalParameterRowDto> Rows)`;
`LegalParameterCurrentDto(DateOnly EffectiveFrom, decimal Value)`; `LegalParameterRowDto(DateOnly EffectiveFrom, decimal Value, bool InUse, DateTimeOffset? UpdatedAt)`;
`LegalBracketKeyDto(string Key, string Name, bool NextYearMissing, bool CurrentYearMissing, LegalBracketCurrentDto? Current, IReadOnlyList<LegalBracketSetDto> Sets)`; `LegalBracketCurrentDto(DateOnly EffectiveFrom)`;
`LegalBracketSetDto(DateOnly EffectiveFrom, bool InUse, DateTimeOffset? UpdatedAt, IReadOnlyList<LegalBracketDto> Brackets)`; `LegalBracketDto(decimal LowerBound, decimal? UpperBound, decimal Rate)`;
`LegalParameterHistoryPageDto(IReadOnlyList<LegalParameterChangeDto> Items, int Total, int Page, int PageSize)`;
`LegalParameterChangeDto(Guid Id, string Kind, string Key, DateOnly EffectiveFrom, string Action, decimal? OldValue, decimal? NewValue, IReadOnlyList<LegalBracketDto>? OldBrackets, IReadOnlyList<LegalBracketDto>? NewBrackets, DateTimeOffset ChangedAt, LegalParameterActorDto ChangedBy, string Source, Guid? BatchId, string? FileName)`; `LegalParameterActorDto(Guid SubjectId, string DisplayName)`.
Composer: parameters in enum order, rows newest first, `current` = row in force at `today`; set `updatedAt` = max of its brackets' `UpdatedAt`; `inUse` via `LegalParameterUsageRule.Affected` with stamps read **once**; `nextYearMissing` false for all and count 0 when `!NextYearWatchOpen(today)`; counts over 27 keys + the set. History: `page < 1 ⇒ 1`, `pageSize` clamped to `1..100`, default 50. Every DTO with a BFF reason gets a one-line comment (repo rule).

Routes (group `/api/v1/tenants/{tenantId:guid}/operator/legal-parameters`, tag `Operator`): `GET /` (read), `GET /history` (read), `PUT|DELETE /{key}/{effectiveFrom}` (manage, body `SetLegalParameterRequest(decimal? Value)`, null ⇒ 0 ⇒ `value_invalid`), `PUT|DELETE /brackets/{key}/{effectiveFrom}` (manage, body `SetLegalBracketsRequest(IReadOnlyList<LegalBracketRequest>? Brackets)`, null ⇒ empty ⇒ `value_invalid`). PUT/DELETE return the full `LegalParametersDto`.

- [ ] **Step 1: Failing tests** — `LegalParameterOperatorApiTests` (operator tenant seeded, `ILegalParameterUsage` replaced by a fake with settable stamps in `configureServices`; clock fixed with `MutableTenantClock`):
  `WithoutReadIs403AndNonOperatorTenantIs403`; `GetReturnsThirtyKeysWithNameGroupAndUnit` (`parameters.Count == 30`, `Smmlv` has `name`, `group == "SalaryAndCaps"`, `unit == "Currency"`, `updatedAt` null on seeded rows); `NextYearMissingOnlyFromDecember` (2026-11-30 ⇒ `nextYearMissingCount == 0`; 2026-12-01 ⇒ `28`); `CurrentYearMissingCountsTheYearWithoutJanuaryFirst` (clock in 2027 ⇒ `currentYearMissingCount == 28`); `PutCreatesAndHistoryShowsManual` (PUT `Smmlv/2027-01-01 {value: 2000000}` ⇒ 200 body has the row with `updatedAt` set; `/history` first item `action == "Created"`, `kind == "Parameter"`, `newValue == 2000000`, `source == "Manual"`, `changedBy.subjectId`); `PutInUseIs409WithCounts` (stamp 2026-01-16 ⇒ PUT `Smmlv/2026-01-10` ⇒ 409, `code`, `periodsAffected == 1`, `companiesAffected == 1`, `from == to == "2026-01-16"`); `UnknownKeyIs404` (`smmlv`, `0`, `Foo`); `BadDateIs422` (`2026-13-01`); `RateAboveOneIs422ValueInvalid`; `DeletingTheOnlyRowIsLastRow` (`ConnectivityAllowanceSince` — check how many seeded rows it has; pick a key with one row); `PutBracketsReplacesTheSet` (history `kind == "Brackets"`, `newBrackets` typed); `NonContiguousBracketsAre422`; `TheSalaryRulesStopFailingAfterCreatingTheMissingYear` (spec «Integración»: `GET /hr/salary-rules` for a date before any Smmlv row ⇒ `hr.legal_parameter.missing`; PUT a row at that date ⇒ salary rules 200).
  `PayrollCalculatedPeriodsTests`: `OnlyPeriodsWithLegalValuesDateAcrossTenants` (seed two tenants' periods, one liquidated per tenant, one not — reuse `PayrollApiSeeds`/liquidation helpers).
- [ ] **Step 2: RED** — `dotnet build` of both test projects fails (types missing).
- [ ] **Step 3: Implement** handlers (authorize first with `HrOperatorPermissions.*` and `IOperatorTenant`; parse route; call `LegalParameterWriter.ApplyAsync` with `LegalParameterChangeOrigin.Manual`; return `composer.ComposeAsync`), endpoints, Payroll port, Bootstrapper adapter, registrations and the three permission places.
- [ ] **Step 4: GREEN** — check `testhost`; run each failing test by name, then the class `LegalParameterOperatorApiTests` once and `PayrollCalculatedPeriodsTests` once; `tests/ArchitectureTests` once (`CompositionRootTests` must see every new handler).
- [ ] **Step 5: Commit** `feat(hr): operator console api for legal parameters`

### Task 7: Cache — Valkey area `LegalParameters` and pod-memory fallback

**Files:**
- Modify: `src/Bootstrapper/Valkey/ValkeyArea.cs` (enum value, `TagOf` ⇒ `"legal_parameters"`, `IsOn`), `ValkeyOptions.cs` (`public bool LegalParameters { get; set; }`), `ValkeyKeys.cs`
- Create: `src/Bootstrapper/Valkey/LegalParameters/LegalParameterSnapshot.cs`, `ValkeyLegalParameterSource.cs`, `MemoryLegalParameterCache.cs` (+ `MemoryLegalParameterSource`), `LegalParameterCacheVersions.cs` (`ValkeyLegalParameterCacheVersion`, `MemoryLegalParameterCacheVersion`, `NullLegalParameterCacheVersion`), `LegalParameterCacheRegistration.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (call `services.AddLegalParameterCache(configuration);` on the line right after `services.AddHrInfrastructure(configuration);` ~1861; remove Task 6's temporary registration)
- Modify: `appsettings*.json` / config example files that list `Valkey:Areas:*` (`ConfigurationExampleTests` will tell) with `"LegalParameters": false`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/Valkey/LegalParameterCacheTests.cs`, extend `ValkeyRegistrationTests.cs`

**Interfaces — Produces:**
```csharp
// ValkeyKeys
public static RedisKey LegalParametersVersion => $"{Prefix}legalparams:ver";
public static RedisKey LegalParametersTable(long version) => $"{Prefix}legalparams:{version}";

internal sealed record LegalParameterSnapshot(IReadOnlyList<LegalParameterValue> Values, IReadOnlyList<LegalParameterBracket> Brackets)
{ public string Serialize(); public static LegalParameterSnapshot Deserialize(string json); } // via plain records; rebuild with public ctors

internal sealed class ValkeyLegalParameterSource(ILegalParameterSource inner, ValkeyGateway gateway, TimeProvider time) : ILegalParameterSource; // scoped, memoizes one snapshot per scope
internal sealed class MemoryLegalParameterCache(TimeProvider time)   // singleton
{ public static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(60);
  public long CurrentVersion { get; } public void Bump();
  public bool TryGet(out LegalParameterSnapshot? snapshot); public void Set(long version, LegalParameterSnapshot snapshot); }
internal sealed class MemoryLegalParameterSource(ILegalParameterSource inner, MemoryLegalParameterCache cache) : ILegalParameterSource;
internal sealed partial class ValkeyLegalParameterCacheVersion(ValkeyGateway gateway, TimeProvider time, ILogger<ValkeyLegalParameterCacheVersion> logger) : ILegalParameterCacheVersion;
internal sealed class MemoryLegalParameterCacheVersion(MemoryLegalParameterCache cache) : ILegalParameterCacheVersion;
internal sealed class NullLegalParameterCacheVersion : ILegalParameterCacheVersion;
public static class LegalParameterCacheRegistration
{ public static IServiceCollection AddLegalParameterCache(this IServiceCollection services, IConfiguration configuration); }
```
Valkey read: `GET ver` (missing ⇒ `SET ver seed NX` with `TenantAccessVersionBase.Seed(time)`, then `GET`); `GET table:{ver}`; hit ⇒ deserialize; miss ⇒ `inner` then `SET table:{ver} json EX 3600`. Any failed attempt ⇒ `inner` (the gateway counts the fallback). Bump: `MULTI { SET ver seed NX; INCR ver }`, `CancellationToken.None`, on failure `LogBumpFailed` at `Error` ("… cached legal parameters may be served for up to 1 hour"). Registration: find the current `ILegalParameterSource` descriptor (registered by `AddHrInfrastructure` with an `internal` type), remove it, register the decorator built with `ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)` as `inner`; area on ⇒ Valkey source + Valkey version; off ⇒ memory source + memory version (singleton cache). Area decided from configuration at startup like `AddQepValkey`.

- [ ] **Step 1: Failing tests:** `ResolvingTheSourceGivesTheDecorator` (build the real service collection as `ValkeyRegistrationTests` does: area off ⇒ `MemoryLegalParameterSource`, on ⇒ `ValkeyLegalParameterSource`, version types match); `TagOfLegalParametersIsStable` (`"legal_parameters"`); memory: `ServesFromMemoryWithinSixtySeconds` (inner called once for two scopes), `ExpiresAfterSixtySeconds` (`ManualTimeProvider` +61 s), `ABumpInvalidates`, `AReadThatStartedBeforeTheBumpIsBornStale` (inner blocks; bump during load; next read reloads); Valkey (with `ValkeyTestServer`): `SecondReadHitsValkey`, `BumpChangesTheKey`, `TheTableExpiresInOneHour` (`TTL` ≤ 3600), `ValkeyDownReadsTheDatabase` (disconnected gateway ⇒ inner, fallback metric +1 for `legal_parameters`), `BumpFailureIsLoggedAndDoesNotThrow`.
- [ ] **Step 2: RED** — build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~LegalParameterCacheTests|FullyQualifiedName~ValkeyRegistrationTests"`; `tests/ArchitectureTests` once.
- [ ] **Step 5: Commit** `feat(valkey): legal parameters cache area with versioned key`

**End of Block B:** one review of Tasks 6–7.

---

## Block C — Excel load and closing (Tasks 8–10)

### Task 8: Excel reader and prefilled template

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/LegalParameterImportColumns.cs`, `IExcelLegalParameterImporter.cs` (also the template builder port and row records)
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Excel/ClosedXmlLegalParameterImporter.cs`, `ClosedXmlLegalParameterTemplateBuilder.cs` (+ register both in `HrInfrastructureExtensions`)
- Test: `tests/Modules/Hr/Modules.Hr.IntegrationTests/ClosedXmlLegalParameterWorkbookTests.cs` (no container; same project as other ClosedXML tests if it exists, else Hr.IntegrationTests)

**Interfaces — Produces:**
```csharp
public static class LegalParameterImportColumns
{
    public const string ParametersSheet = "Parámetros"; public const string BracketsSheet = "Tramos";
    public const string Key = "Clave"; public const string Name = "Nombre"; public const string CurrentValue = "Valor vigente";
    public const string CurrentFrom = "Vigente desde"; public const string EffectiveFrom = "Rige desde"; public const string NewValue = "Valor nuevo";
    public const string LowerBound = "Desde"; public const string UpperBound = "Hasta"; public const string Rate = "Tasa";
    public const int MaxRows = 500; public const long MaxSizeInBytes = 1 * 1024 * 1024;
    public static string TemplateFileName(int year) => $"parametros-legales-{year}.xlsx";
}
public sealed record ExcelLegalParameterRow(int RowNumber, string? Key, string? EffectiveFrom, string? NewValue);
public sealed record ExcelLegalBracketRow(int RowNumber, string? Key, string? EffectiveFrom, string? LowerBound, string? UpperBound, string? Rate);
public sealed record LegalParameterWorkbook(IReadOnlyList<ExcelLegalParameterRow> Parameters, IReadOnlyList<ExcelLegalBracketRow> Brackets);
public interface IExcelLegalParameterImporter { Task<LegalParameterWorkbook> ReadAsync(Stream content, CancellationToken cancellationToken); }
public sealed record LegalParameterTemplateRow(LegalParameterKey Key, string Name, LegalParameterUnit Unit, decimal? CurrentValue, DateOnly? CurrentFrom);
public sealed record LegalParameterTemplateData(DateOnly NextYearStart, IReadOnlyList<LegalParameterTemplateRow> Parameters, IReadOnlyList<LegalBracketInput> CurrentBrackets);
public interface ILegalParameterTemplateBuilder { byte[] Build(LegalParameterTemplateData data); }
```
Reader: sheets by name, headers by name ignoring case and accents (reuse the normalization of `ClosedXmlEmployeeImporter`), dates and numbers as invariant text (`yyyy-MM-dd`, dot decimal) like the employee reader; skips blank rows; unreadable ⇒ `hr.legal_parameter.import.unreadable`, missing sheet/header ⇒ `header_mismatch` (both `HrDomainException`). Template: «Parámetros» one row per key (30); only the 27 yearly keys come prefilled with `Rige desde = NextYearStart` and `Valor nuevo = CurrentValue` — the three non-yearly ones keep both empty (a Carry of `ConnectivityAllowanceSince` at January 1 would move the date connectivity starts, and `WeeklyHours` changes on July 15); read-only columns locked/greyed; `Fraction` cells and «Tasa» with `0.00%` format; «Tramos» prefilled with the current set at `NextYearStart`.

- [ ] **Step 1: Failing tests:** `TheTemplateRoundTripsThroughTheReader` (build with 30 rows + 6 brackets ⇒ read ⇒ 30 parameter rows with `EffectiveFrom == "2027-01-01"` and `NewValue == CurrentValue` invariant, 6 bracket rows); `RateCellsArePercentFormatted` (ClosedXML read of the built file: `Valor nuevo` cell of `HealthEmployeeRate` has `NumberFormat.Format == "0.00%"`, value `0.04`); `AMissingHeaderIsHeaderMismatch`; `GarbageIsUnreadable`; `BlankRowsAreSkipped`.
- [ ] **Step 2: RED** — build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~ClosedXmlLegalParameterWorkbookTests"`.
- [ ] **Step 5: Commit** `feat(hr): excel reader and prefilled template for legal parameters`

### Task 9: Import analyzer, preview, apply and template endpoints

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/LegalParameterImportAnalyzer.cs`, `LegalParameterImportDtos.cs`, `LegalParameterImport.cs` (template query, preview and import handlers)
- Modify: `src/Modules/Hr/Modules.Hr.Api/LegalParameterOperatorEndpoints.cs` (3 routes, all `manage`; `/import/template` returns `Results.File(content, ExcelContentType, fileName)` like `HrEndpoints.cs:467-473`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (analyzer + 3 handlers)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/LegalParameterImportAnalyzerTests.cs` (fake reader returning a `LegalParameterWorkbook`), `tests/Modules/Hr/Modules.Hr.IntegrationTests/LegalParameterImportApiTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record GetLegalParameterImportTemplateQuery(TenantId TenantId) : IQuery<LegalParameterImportTemplateFile>;
public sealed record LegalParameterImportTemplateFile(byte[] Content, string FileName);
public sealed record PreviewLegalParameterImportCommand(TenantId TenantId, string FileName, long SizeInBytes, Stream Content) : ICommand<LegalParameterImportPreviewDto>;
public sealed record ImportLegalParametersCommand(TenantId TenantId, string FileName, long SizeInBytes, Stream Content) : ICommand<LegalParameterImportResultDto>;

public sealed record LegalParameterImportPreviewDto(string FileName, LegalParameterImportSummaryDto Summary, bool CanApply,
    IReadOnlyList<LegalParameterImportItemDto> Items, IReadOnlyList<LegalParameterImportWarningDto> Warnings);
public sealed record LegalParameterImportSummaryDto(int Create, int Carry, int Update, int Unchanged, int Error);
public sealed record LegalParameterImportItemDto(string Kind, string Sheet, IReadOnlyList<int> Rows, string? Key, string? Name,
    DateOnly? EffectiveFrom, string Action, decimal? OldValue, decimal? NewValue,
    IReadOnlyList<LegalBracketDto>? OldBrackets, IReadOnlyList<LegalBracketDto>? NewBrackets, string? Code, string? Message);
public sealed record LegalParameterImportWarningDto(string Code, string Key, string Message);
public sealed record LegalParameterImportResultDto(string FileName, DateTimeOffset ReceivedAt, Guid BatchId, int Created, int Carried, int Updated, int Unchanged);

public sealed record LegalParameterImportAnalysis(string FileName, LegalParameterImportPreviewDto Preview,
    IReadOnlyList<LegalParameterEdit> ParameterEdits, IReadOnlyList<LegalBracketSetEdit> BracketEdits, int Carried);
public sealed class LegalParameterImportAnalyzer(IExcelLegalParameterImporter importer, ILegalParameterRepository repository,
    ILegalParameterUsage usage, ITenantClock tenantClock)
{ public Task<LegalParameterImportAnalysis> AnalyzeAsync(TenantId operatorTenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken); }
```
Analyzer: file checks (name, `.xlsx`, ≤ 1 MB, then read, empty ⇒ `empty`, > 500 rows ⇒ `too_many_rows`). Parameter row with empty `Valor nuevo` ⇒ ignored. Per row: key (exact name; else row `key_unknown`), date (`date_invalid`), value (`LegalParameterRules.Check` ⇒ `value_invalid` with its message), same `(key, date)` earlier in the file ⇒ `duplicated`. Brackets grouped by `(Clave, Rige desde)` into **one item per set** with all its sheet rows; invalid set ⇒ `brackets_invalid` with the rules' message. Action: existing `(key, date)` with same value/set ⇒ `Unchanged`; existing different ⇒ `Update`; missing and equal to what is in force at that date ⇒ `Carry`; else `Create`. `oldValue`/`oldBrackets` = in force at that date **before** applying (null if none). Usage per non-unchanged item with other dates = existing ∪ file dates of that key ⇒ `in_use` row error. Warnings: each of the 27 yearly keys and the set still missing `NextYear-01-01` after applying ⇒ `next_year_missing` (always, not only in December). `CanApply = Error == 0`. Preview handler returns `Preview`. Import handler re-analyzes; any error ⇒ `HrDomainException(rows_invalid)`; else `LegalParameterWriter.ApplyAsync(…, new(Excel, Guid.CreateVersion7(), fileName))` — `Carry` is written and recorded as `Created`; result counts `created` (Create only), `carried`, `updated`, `unchanged`. Template handler: data from the repository at operator `today`, `NextYearStart = new(today.Year + 1, 1, 1)`, file name `TemplateFileName(today.Year + 1)`.

- [ ] **Step 1: Failing tests** — analyzer (unit): `AnUntouchedTemplateIsAllCarryAndOneSet` (27 yearly rows + the set prefilled at next Jan 1, non-yearly rows empty ⇒ summary `carry == 28`, no warnings); `OneChangedValueIsCreate` (`oldValue` = in-force value); `AnExistingRowWithANewValueIsUpdate`; `AnExistingRowWithTheSameValueIsUnchanged`; `RateTwelveIsAnErrorWithTheExactMessage`; `UnknownKeyAndBadDateAreRowErrors`; `TheSameKeyAndDateTwiceIsDuplicated`; `ASetIsOneItemWithAllItsRows` (rows `[2,3,4,5,6,7]`); `AGapInTheSetIsBracketsInvalid`; `ARowInAUsedRangeIsInUse`; `MissingNextYearRowsAreWarnings`; `MoreThanFiveHundredRowsIsTooManyRows`; `AnEmptyNewValueIsIgnored`.
  API (integration, filtered): `PreviewWritesNothing` (preview ⇒ `GET` unchanged, `/history` empty); `OneErrorAppliesNothing` (`POST /import` ⇒ 422 `rows_invalid`, history empty); `TheImportSharesOneBatchAndBumpsOnce` (replace `ILegalParameterCacheVersion` with a counting fake: 1 bump; all history items share `batchId`, `source == "Excel"`, `fileName`); `UnchangedRowsLeaveNoHistory`; `WithoutFileIs422FileRequired`; `TheTemplateDownloadsAsXlsx` (content type, file name `parametros-legales-2027.xlsx` with clock in 2026).
- [ ] **Step 2: RED** — build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — unit filter `LegalParameterImportAnalyzerTests`; check `testhost`; integration tests by name, then the class `LegalParameterImportApiTests` once; `tests/ArchitectureTests` once.
- [ ] **Step 5: Commit** `feat(hr): yearly excel load for legal parameters`

### Task 10: Payroll end-to-end guard and spec corrections

**Files:**
- Modify: `tests/Modules/Payroll/Modules.Payroll.IntegrationTests/PayrollLegalPhotoApiTests.cs` (or a new `LegalParameterUsageApiTests.cs` in that project; harness gains the operator setting as in Task 6)
- Modify: `docs/superpowers/specs/2026-11-22-parametros-legales-consola-operador-design.md`

- [ ] **Step 1: Test** `EditingTheSmmlvOfALiquidatedPeriodIs409AndThePhotoStays`: liquidate the first fortnight of a month with the existing helpers; `PUT operator/legal-parameters/Smmlv/<that year>-01-01` with another value ⇒ 409 `in_use`, `periodsAffected >= 1`; the period's `legal_values` JSON is byte-identical before and after. Run by name (it exercises Payroll port + Bootstrapper adapter + API; it should pass on the first run — record its output as GREEN evidence, and show a RED by temporarily registering a fake empty `ILegalParameterUsage` in the test only if the owner asks).
- [ ] **Step 2: Spec corrections** (code wins), each marked *(Corregido en la implementación, 2026-11-23)*:
  - Line numbers: `AddQepValkey` at `QepServiceCollectionExtensions.cs:124`, `AddHrInfrastructure` at `:1861`; permissions at `~2841`, `~3660`, `~4601`.
  - «Códigos de error (mapeo por `code`…)» ⇒ the mapping is by exception type in `ApiExceptionHandler.MapException`; no per-code entries.
  - Bracket validation in the manual `PUT` uses `hr.legal_parameter.value_invalid` (the spec named no code); in the preview it is the row code `brackets_invalid`.
  - Row codes closed: list them (closes ❓ «su lista la cierra el implementador»).
  - History `kind` is derived from `key`, no column.
  - The names test lives in `Bootstrapper.UnitTests` (it needs `LegalBasis`).
  - New ❓ for the owner: deleting a `(key, date)` that does not exist answers 404 `key_unknown`.
  - Template: only the 27 yearly keys come prefilled; the three non-yearly ones come empty (reason in Task 8).
- [ ] **Step 3: Commit** `test(payroll): legal parameter edits cannot touch a liquidated period` and `docs(hr): legal parameters spec matches the implementation`

**End of Block C:** one review of Tasks 8–10, then the strong whole-branch review (most capable model).

## Before merging to `develop`

1. Check `testhost`; run once, one project at a time, `-- xUnit.MaxParallelThreads=4`: `tests/Modules/Hr/Modules.Hr.UnitTests`, `tests/Modules/Hr/Modules.Hr.IntegrationTests`, `tests/Modules/Payroll/Modules.Payroll.UnitTests`, `tests/Modules/Payroll/Modules.Payroll.IntegrationTests`, `tests/ArchitectureTests/ArchitectureTests`; plus `tests/Bootstrapper/Bootstrapper.UnitTests` filtered to `Valkey|LegalParameter` (touched there).
2. `git branch --show-current` in the main checkout; merge `--no-ff` into `develop`; no push. Then a `docs:` commit on `develop` updating `docs/estado.md` (row + Registro).
