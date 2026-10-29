# Cobros recurrentes — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El módulo de tenant Facturación (`billing`), cuya primera feature son los cobros recurrentes: guarda servicios recurrentes por cliente, genera **cada período** su cobro aunque el anterior siga sin pagar, deja marcar pagado con fecha real y nota, le manda al cliente **un** mensaje por día por un solo canal (WhatsApp o Email) con el calendario fijo (−5, 0, +3, +7k) agrupando sus vencidos, y al encargado un email diario con lo que vence, venció o falló, sin que ningún aviso salga dos veces.

**Architecture:** Módulo nuevo `src/Modules/Billing/*` con las cuatro capas de siempre (copia de `Suppliers`). El dominio es puro: `BillingService` (agregado con ciclo, estado y hasta dónde generó), `Charge` y `CustomerBillingProfile`, más `BillingCycles` (aritmética del ancla), `ChargeGenerator` (qué cobros entran en ventana) y `ReminderCalendar` (qué paso toca hoy). Customers, Identity, Authorization, Notifications y Mensajería entran por puertos de `Modules.Billing.Application` con adaptadores en `Bootstrapper`. El worker `BillingReminderWorker` corre cada 15 min, corta el día con `ITenantClock`, hace tres trabajos por tenant (generar, mensaje por cliente, email por encargado) y deduplica por filas únicas (`billing.charges(service_id, due_date)`, `billing.customer_messages(tenant, customer, date)`, `billing.reminder_steps(charge_id, step)`, `billing.daily_digests(tenant, member, date)`, `billing.tenant_runs(tenant, date)`). Envía él mismo por los adaptadores (`IEmailChannel`, Mensajería), porque el camino del outbox de Notifications no devuelve el resultado ni admite destinatarios que no son usuarios. `Overdue` se calcula al responder; la base guarda `Pending`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql (`postgres:18-alpine`), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-10-29-cobros-recurrentes.md`](../specs/2026-10-29-cobros-recurrentes.md) (contrato) y
`qep-frontend/docs/superpowers/specs/2026-10-29-cobros-recurrentes-design.md` (diseño de producto). Las secciones citadas (§) son del contrato salvo que diga «design».

**Fuera de alcance:** el frontend (plan propio en `qep-frontend`). El canal de WhatsApp **sí** entra (Task 12): el owner aprobó las plantillas el 2026-10-29.

---

## Antes de empezar

- Rama: `git branch --show-current` en `qep-backend` **en ese momento**. Tiene que decir `feature/cobros-recurrentes` (desde `develop`); si dice `main`, frenar. Se repite antes de cada commit.
- `Stop-Process -Name Api -ErrorAction SilentlyContinue` antes de `dotnet build`, `dotnet test` o `dotnet ef`.
- Integración: `$env:Seed__Enabled = "false"` en línea aparte antes de cualquier `dotnet test` de `*.IntegrationTests`. Necesitan Docker.
- Todo comando es PowerShell: sin `&&`, una línea por comando.
- Por tarea se corren **sólo** las clases tocadas con `--filter`; la suite completa corre una vez, en la Task 15.
- Migraciones: `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`. Billing necesita su `BillingDbContextFactory` (copia de `SuppliersDbContextFactory.cs`).
- Commits: conventional commits en inglés, **sin** `Co-Authored-By` ni atribución de IA, aunque el harness la sugiera.
- Nunca imprimir secretos.

## Global Constraints

- Clave `billing`, última en `TenantModuleKeys.All`, `Dependencies[Billing] = [Customers]`, fuera de `DefaultForNewTenants` (§1).
- Permisos `billing.service.read` y `billing.service.manage`; `PermissionDefinition` con categoría `"Billing"`, riesgos `"low"`/`"medium"`, `RequiredModules: [ModuleKeys.Billing]`; en los roles `admin` **y `billing`** («Facturación», `QepServiceCollectionExtensions.cs:1406-1435`), en ningún otro (§1, owner 2026-10-29). El módulo es Facturación y los cobros recurrentes su primera feature: nombres acotados por recurso (`billing.service.*`, `/billing/services`, `/billing/charges`).
- Rutas bajo `/api/v1/tenants/{tenantId:guid}/billing`; handler revalida tenant + permiso (403 `authorization.denied`) y módulo (403 `tenancy.module_not_enabled`), nunca 404 por otro tenant (§1).
- `Modules.Billing.Application` referencia sólo `BuildingBlocks.*` y `Modules.Tenancy.Application`; nada de EF Core ni Npgsql (§1).
- Esquema `billing`; tablas y columnas de §2 con esos nombres exactos; `(service_id, due_date)` único; `(tenant_id, customer_id, run_date)` único en `customer_messages`; PK `(charge_id, step)` en `reminder_steps`.
- Ciclos por el cable `Monthly|Semiannual|Annual` ↔ `cycle_months` `1|6|12`. Estados de cobro guardados `Pending|Paid|Cancelled`; en el cable `Pending|Overdue|Paid|Cancelled` con `Overdue = Pending && dueDate < today` (§2, §4).
- Anclas: `firstDueDate + k×cycleMonths`, día del ancla o último del mes. Generación (§3.2): cada período su cobro; un cobro nace cuando su `dueDate <= today + 5`; `next = lastGeneratedDueDate is null ? firstDueDate : primera ancla > lastGeneratedDueDate`, y si `reactivatedOn > lastGeneratedDueDate`, `next = primera ancla >= reactivatedOn`. Marcar pagado **no** genera. Al crear, con `firstDueDate` futura nace un cobro; con pasada, todos hasta `today + 5`.
- Pasos del calendario: `before_5 = due−5`, `due`, `after_3 = due+3`, `after_{3+7k} = due+3+7k`; de cada cobro toca el **último** paso con fecha `<= today` sin fila en `reminder_steps`. Por cliente **un** mensaje por día: `Overdue` si algún cobro con paso hoy está vencido (lista todos sus vencidos, total por moneda), si no `Reminder` (los cobros con paso hoy, total por moneda, vencimiento más próximo) (§3.3). Un servicio `Paused` no genera, pero sus `Pending` siguen avisándose; `Cancel` cancela todos sus `Pending`.
- Códigos de fallo exactos de §3.4; nunca cambiar de canal ante un fallo.
- `Billing:SweepMinutes = 15`, `Billing:SendHourLocal = 8`, validadas al arrancar (§6).
- DTO: opcionales como `null`; fechas `date` en `yyyy-MM-dd`; `PaidOn <= today del tenant` (§3.2, §4).
- Auditoría con `IAuditRecorder` en la misma transacción, acciones de §4.
- Comentarios en el código explican el **por qué** y citan archivo:línea cuando copian un contrato.

## Review Focus

1. **Ancla 31 → mes corto → mes largo:** `firstDueDate = 2026-01-31`, mensual: las anclas son `01-31`, `02-28`, `03-31` (no `03-28`). Task 2, `NextDueDateKeepsTheAnchorDayAcrossShortMonths`.
2. **Dos réplicas en el mismo minuto:** dos `ProcessTenantAsync` concurrentes sobre el mismo tenant generan cada período **una** vez y mandan a cada cliente **un** mensaje. Task 10, `TwoConcurrentRunsGenerateOnceAndMessageOnce`.
3. **Worker apagado 10 días:** cobro vencido hace 12 días sin filas: hoy se cubre sólo `after_10`, no `before_5`+`due`+`after_3`; y se generan los períodos que entraron en ventana mientras tanto. Task 10, `ACatchUpCoversOnlyTheLatestDueStep` y `ACatchUpGeneratesEveryMissedPeriod`.
4. **Migrar un servicio con tres meses de atraso:** `firstDueDate = today − 3 meses`, mensual: el `POST` responde con **cuatro** cobros (tres vencidos y el del mes en curso si entra en ventana), no uno. Task 7, `CreateWithAPastFirstDueDateGeneratesEveryPeriodUpToTheWindow`.
5. **Cliente con dos cobros vencidos en monedas distintas:** un solo mensaje `Overdue` con «2 cobro(s)» y el total «$ 450.000 y USD 20», nunca sumado. Task 10, `AnOverdueMessageGroupsEveryOverdueChargeWithOneTotalPerCurrency`.
6. **Dos clientes existentes con el mismo teléfono y una fila sin documento:** la fila es `customer_ambiguous`, no se crea un tercero ni se elige uno. Task 14, `ARowMatchingTwoCustomersByPhoneIsAmbiguous`.
7. **Vista previa y confirmación con el mismo archivo, pero alguien creó el servicio en el medio:** la confirmación devuelve esa fila como `service_exists` y las demás se importan. Task 14, `AServiceCreatedBetweenPreviewAndConfirmBecomesARowError`.

---

### Task 1: La clave de módulo `billing` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:61-83`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:120-122`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddBillingModuleKey.cs` (+ Designer, generados)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs:13,71-76`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs:105-113`

**Interfaces:**
- Produces: `TenantModuleKeys.Billing` (`"billing"`), `TenantModuleKeys.DependenciesOf(Billing) == [Customers]`.

- [ ] **Step 1: Write the failing tests** — en `TenantModuleKeysTests`, agrega `"billing"` al final de la lista de `AllKeepsTheContractOrder` y:

```csharp
[Fact]
public void BillingIsLastDependsOnCustomersAndIsOutsideTheDefaults()
{
    Assert.Equal(TenantModuleKeys.Billing, TenantModuleKeys.All[^1]);
    Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Billing));
    Assert.DoesNotContain(TenantModuleKeys.Billing, TenantModuleKeys.DefaultForNewTenants);
}
```

`SuppliersIsLastWithoutDependenciesAndOutsideTheDefaults` deja de afirmar `All[^1]` (ya no es el último). En `TenantModulesApiTests.cs:105-113`, agrega `"billing"` a la lista y a los dos `is` de módulos apagados.

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter FullyQualifiedName~TenantModuleKeysTests` → FAIL: `TenantModuleKeys` no tiene `Billing`.

- [ ] **Step 3: Implement** — `public static readonly TenantModuleKey Billing = TenantModuleKey.Define("billing");` con su `<summary>` citando el design; último en `All`; `[Billing] = [Customers]` en `Dependencies`. En `TenancyDbContext.cs:122` agrega `'billing'` al CHECK. Genera la migración `AddBillingModuleKey`; su `Down` borra `WHERE module_key = 'billing'` antes de reponer el CHECK (copia `20261024172027_AddSuppliersModuleKey.cs`). Verifica que la migración generada sólo toque el CHECK.

- [ ] **Step 4: Run to verify GREEN** — el filtro anterior → PASS. Luego `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter FullyQualifiedName~TenantModulesApiTests` → PASS.

- [ ] **Step 5: Commit** — `feat(tenancy): add billing module key`

---

### Task 2: Dominio de Billing: ciclo, servicio, cobro y perfil

**Files:**
- Create: `src/Modules/Billing/Modules.Billing.Domain/Modules.Billing.Domain.csproj` (copia del de Suppliers; referencia `BuildingBlocks.Domain`)
- Create: `BillingCycle.cs`, `BillingService.cs`, `Charge.cs`, `ChargeGenerator.cs`, `CustomerBillingProfile.cs`, `BillingChannel.cs`, `BillingServiceStatus.cs`, `ChargeStatus.cs`, `BillingDomainException.cs`, `BillingErrorCodes.cs` en esa carpeta
- Create: `tests/Modules/Billing/Modules.Billing.UnitTests/Modules.Billing.UnitTests.csproj`, `BillingCycleTests.cs`, `BillingServiceTests.cs`, `ChargeTests.cs`, `ChargeGeneratorTests.cs`, `CustomerBillingProfileTests.cs`
- Modify: `QEP.slnx` (o el `.sln` que exista): agrega los dos proyectos

**Interfaces:**
- Produces:
  - `public enum BillingCycle { Monthly = 1, Semiannual = 6, Annual = 12 }` y `public static class BillingCycles { public static DateOnly NextDueDate(DateOnly firstDueDate, BillingCycle cycle, DateOnly after); public static DateOnly FirstDueDateOnOrAfter(DateOnly firstDueDate, BillingCycle cycle, DateOnly date); public static BillingCycle Parse(string wire); }` — `NextDueDate` devuelve la primera ancla **estrictamente posterior** a `after`; `FirstDueDateOnOrAfter`, la primera ancla `>= date`.
  - `public sealed class BillingService` con `Id (BillingServiceId)`, `TenantId`, `CustomerId (Guid)`, `Name`, `Price`, `Currency`, `Cycle`, `FirstDueDate`, `Status`, `ReactivatedOn (DateOnly?)`, `LastGeneratedDueDate (DateOnly?)`, `Version`, timestamps; `static Create(BillingServiceId id, Guid tenantId, Guid customerId, string name, decimal price, string currency, BillingCycle cycle, DateOnly firstDueDate, DateTimeOffset now) : BillingService` (valida nombre 1..150 trim, precio > 0 → `BillingDomainException(BillingErrorCodes.ServiceInvalid)`), `Update(string name, decimal price, string currency, DateTimeOffset now)` (lanza `billing.service.cancelled` si `Cancelled`), `Pause(now)`, `Resume(DateOnly today, now)`, `Cancel(now)` (transiciones inválidas → `billing.service.invalid_transition`), `DateOnly? NextDueDateToGenerate()` (§3.2: `null` si no está `Active`; si no, `LastGeneratedDueDate is null ? FirstDueDate : NextDueDate(..., LastGeneratedDueDate)`, y si `ReactivatedOn > LastGeneratedDueDate`, `FirstDueDateOnOrAfter(..., ReactivatedOn)`), `MarkGenerated(DateOnly dueDate)`.
  - `public static class ChargeGenerator { public static IReadOnlyList<Charge> Generate(BillingService service, DateOnly today, DateTimeOffset now); }` — repite `NextDueDateToGenerate` + `Charge.Generate` + `MarkGenerated` mientras `next <= today.AddDays(5)`; con `LastGeneratedDueDate == null` genera al menos el primero aunque esté fuera de la ventana (§3.2, «la pantalla necesita el próximo vencimiento»). `WindowDays = 5` como constante pública.
  - `public sealed class Charge` con `Id (Guid v7)`, `TenantId`, `ServiceId`, `CustomerId`, `Amount`, `Currency`, `DueDate`, `Status (ChargeStatus { Pending, Paid, Cancelled })`, `PaidOn`, `PaymentNote`, `PaidByMemberId`, `PaidAt`, `Version`, `CreatedAt`; `static Charge Generate(BillingService service, DateOnly dueDate, DateTimeOffset now)`; `MarkPaid(DateOnly paidOn, string? note, Guid paidByMemberId, DateOnly today, DateTimeOffset now)` (`not_pending`, `date_in_future`, nota > 1000 → `billing.payment.note_too_long`); `UpdatePayment(paidOn, note, today, now)` (`not_paid`); `Cancel(now)` (sólo `Pending`); `bool IsOverdue(DateOnly today)`.
  - `public sealed class CustomerBillingProfile` con `TenantId`, `CustomerId`, `Channel (BillingChannel { WhatsApp, Email })`, `AssigneeMemberId`, `Version`; `static Create(...)`, `Change(BillingChannel channel, Guid assigneeMemberId, DateTimeOffset now)`.
  - `BillingErrorCodes`: constantes con los códigos de §3 y §4 (`ServiceNotFound = "billing.service.not_found"`, `ChargeNotFound`, `ChargeNotPending`, `ChargeNotPaid`, `PaymentDateInFuture`, `PaymentNoteTooLong`, `ServiceInvalidTransition`, `ServiceCancelled`, `ServiceInvalid`, `ProfileMissing`, `ProfileNotFound`, `ProfileAssigneeInvalid`, `ProfileWhatsAppRequiresPhone`, `ProfileEmailRequiresAddress`, `CustomerNotFound`, `CustomerInactive`, `CurrencyUnknown`, `SettingsConnectionUnknown`).

- [ ] **Step 1: Write the failing tests**

`BillingCycleTests`:
```csharp
[Theory]
[InlineData("2026-01-31", 1, "2026-01-31", "2026-02-28")]
[InlineData("2026-01-31", 1, "2026-02-28", "2026-03-31")]  // Review Focus 1: el ancla no se pierde
[InlineData("2026-01-15", 6, "2026-01-15", "2026-07-15")]
[InlineData("2024-02-29", 12, "2024-02-29", "2025-02-28")]
[InlineData("2026-01-15", 1, "2026-03-20", "2026-04-15")]  // «después» entre dos anclas: la siguiente
[InlineData("2026-01-15", 1, "2026-04-15", "2026-05-15")]  // «después» igual a un ancla: estrictamente posterior
public void NextDueDateKeepsTheAnchorDayAcrossShortMonths(string first, int months, string after, string expected);

[Theory] [InlineData("Monthly", 1)] [InlineData("Semiannual", 6)] [InlineData("Annual", 12)]
public void ParseReadsTheWireNames(string wire, int months);
[Fact] public void ParseRejectsAnythingElse(); // Assert.Throws<BillingDomainException> con BillingErrorCodes.ServiceInvalid
```

`BillingServiceTests`:
```csharp
[Fact] public void CreateRequiresANameAndAPositivePrice();           // "", "   ", 151 chars, 0, -1 → ServiceInvalid
[Fact] public void PauseOnlyFromActiveAndResumeOnlyFromPaused();      // Paused.Pause y Active.Resume → ServiceInvalidTransition
[Fact] public void CancelFromActiveOrPausedAndNeverBack();           // Cancelled.Resume/Pause/Cancel → ServiceInvalidTransition
[Fact] public void UpdateIsRefusedOnACancelledService();             // ServiceCancelled
[Fact] public void ResumeRecordsTheReactivationDate();               // ReactivatedOn == today
[Fact] public void NextDueDateToGenerateStartsAtTheFirstDueDate();   // LastGeneratedDueDate null → FirstDueDate
[Fact] public void NextDueDateToGenerateIsTheAnchorAfterTheLastOne(); // last 2026-02-10, mensual → 2026-03-10
[Fact] public void NextDueDateToGenerateAfterAResumeSkipsThePausedPeriods()
{
    // mensual, ancla 2026-01-10; último generado 2026-02-10; pausado; reactivado el 2026-05-03 → 2026-05-10 (no 03-10)
    // reactivado exactamente el 2026-05-10 → 2026-05-10 (igual o posterior)
}
[Fact] public void NextDueDateToGenerateIsNullUnlessActive();        // Paused y Cancelled → null
```

`ChargeGeneratorTests` (today = 2026-03-10):
```csharp
[Fact] public void AFutureFirstDueDateGeneratesExactlyOneCharge();              // first 2026-06-01 → 1 cobro, fuera de ventana a propósito
[Fact] public void APastFirstDueDateGeneratesEveryPeriodUpToTheWindow();        // first 2025-12-10 mensual → 12-10, 01-10, 02-10, 03-10 (y 03-15 no: > today+5)
[Fact] public void ADueDateInsideTheWindowIsGenerated();                        // last 2026-02-14, mensual → 03-14 (== today+4) sí; 04-14 no
[Fact] public void NothingIsGeneratedForAPausedOrCancelledService();
[Fact] public void GeneratedChargesCopyTheCurrentPriceAndCurrency();
```

`ChargeTests`:
```csharp
[Fact] public void GenerateCopiesAmountAndCurrencyFromTheService();
[Fact] public void MarkPaidRequiresPendingAndANonFutureDate();        // Paid.MarkPaid → ChargeNotPending; paidOn = today+1 → PaymentDateInFuture; paidOn = today OK
[Fact] public void MarkPaidTrimsTheNoteAndKeepsNullForEmpty();        // "  por Nequi " → "por Nequi"; "   " → null; 1001 chars → PaymentNoteTooLong
[Fact] public void UpdatePaymentRequiresPaid();                       // Pending.UpdatePayment → ChargeNotPaid
[Fact] public void IsOverdueOnlyWhilePendingAndPastDue();             // due 10, today 11 → true; today 10 → false; Paid → false
[Fact] public void CancelOnlyAPendingCharge();
```

`CustomerBillingProfileTests`: `ChangeBumpsVersionAndUpdatedAt`.

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/Modules/Billing/Modules.Billing.UnitTests` → FAIL por compilación (tipos inexistentes).

- [ ] **Step 3: Implement** los tipos de **Interfaces**. `NextDueDate`: `k = 0; loop candidate = AddMonthsClamped(first, k × months) while candidate <= after`; `AddMonthsClamped` = `first.AddMonths(n)` ya recorta al último día del mes (`DateOnly.AddMonths` conserva el día cuando existe) — el ancla se guarda aparte, por eso siempre se suma desde `first` y no desde el vencimiento anterior. Constructores privados para EF, como `Supplier.cs`.

- [ ] **Step 4: Run to verify GREEN** — mismo comando → PASS.

- [ ] **Step 5: Commit** — `feat(billing): add domain model for recurring services and charges`

---

### Task 3: Application: permisos, autorización, puertos, DTO y el proyecto Api vacío; capas y composición

**Files:**
- Create: `src/Modules/Billing/Modules.Billing.Application/Modules.Billing.Application.csproj` (refs `BuildingBlocks.Application`, `Modules.Billing.Domain`, `Modules.Tenancy.Application`), `BillingPermissions.cs`, `BillingAuthorization.cs`, `BillingDtos.cs`, `BillingMapping.cs`, `IBillingServiceRepository.cs`, `IChargeRepository.cs`, `ICustomerBillingProfileRepository.cs`, `IBillingSettingsRepository.cs`, `IBillingUnitOfWork.cs`, `IBillingCustomerLookup.cs`, `IBillingMemberDirectory.cs`, `IBillingCustomerEmail.cs`, `IBillingStaffEmail.cs`, `IBillingWhatsAppOutbound.cs`, `IBillingConsentGate.cs`, `IBillingAuditPublisher.cs`, `BillingAuditActions.cs`, `BillingNotFound.cs`
- Create: `src/Modules/Billing/Modules.Billing.Infrastructure/Modules.Billing.Infrastructure.csproj`, `BillingInfrastructureExtensions.cs` (vacío por ahora: `AddBillingInfrastructure(this IServiceCollection, IConfiguration)`), `src/Modules/Billing/Modules.Billing.Api/Modules.Billing.Api.csproj`, `BillingEndpoints.cs` (`MapBillingEndpoints` que sólo crea el grupo)
- Modify: `src/Bootstrapper/Bootstrapper.csproj` (refs Application e Infrastructure), `src/Api/Api.csproj` (ref Api), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`PermissionDefinition` ×2 junto a las de Suppliers `:1944-1957`; políticas junto a `:2317-2321`; `BillingPermissions.Read/Manage` en el rol `admin` tras `:1345` **y en el rol `billing` tras `:1434`**, con un comentario al estilo de ese bloque: «Facturación cobra los servicios recurrentes: ve «Por cobrar» y marca los pagos (owner, 2026-10-29)»; `services.AddBillingInfrastructure(configuration)` tras `:898`), `src/Api/Program.cs` (`app.MapBillingEndpoints()` junto a `MapSupplierEndpoints`)
- Create: `tests/ArchitectureTests/ArchitectureTests/BillingLayerTests.cs` (copia de `SuppliersLayerTests.cs` con los tipos de Billing)
- Modify: `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` (refs a los 4 proyectos), `CompositionRootTests.cs` (junto a `:520-530`)

**Interfaces:**
- Produces:
  - `BillingPermissions.Read = "billing.service.read"`, `BillingPermissions.Manage = "billing.service.manage"`.
  - `BillingAuthorization.EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, string permission, CancellationToken)` — copia de `PipelineAuthorization.EnsureAsync` con `TenantModuleKeys.Billing`.
  - DTO de §4 como `sealed record` con esos nombres: `MemberRefDto(Guid MemberId, string DisplayName, bool IsActive)`, `CustomerRefDto(Guid Id, string Name, string? PhoneE164, string? Email)`, `CurrencyAmountDto(string Currency, decimal Amount)`, `ProfileDto`, `ReminderDto` (con `Kind`), `PaymentDto`, `ChargeDto`, `ServiceDto` (con `NextCharge`, `OpenCount`, `OpenAmountByCurrency`), `ServiceDetailDto`, `ChargePageDto(IReadOnlyList<ChargeDto> Items, int Total, int Page, int PageSize, ChargeSummaryDto Summary)`, `ChargeSummaryDto(int OpenCount, int OverdueCount, int FailedReminderCount, IReadOnlyList<CurrencyAmountDto> OpenAmountByCurrency, IReadOnlyList<CurrencyAmountDto> OverdueAmountByCurrency)`, `ServicePageDto`, `AssigneesDto(IReadOnlyList<MemberRefDto> Items)`, `BillingSettingsDto(Guid? WhatsAppConnectionId, IReadOnlyList<BillingConnectionDto> Connections)`, `BillingConnectionDto(Guid Id, string Name, string? Phone, bool Ready, string? NotReadyReason)`.
  - `BillingMapping.ToChargeDto(Charge, BillingService, CustomerRefDto, MemberRefDto? assignee, MemberRefDto? paidBy, ReminderDto? last, DateOnly today)` — `Status` = `"Overdue"` cuando `charge.IsOverdue(today)`; `DaysOverdue = max(0, today − dueDate)`. `CurrencyTotals.Of(IEnumerable<(string Currency, decimal Amount)>) : IReadOnlyList<CurrencyAmountDto>` (un total por moneda, orden del catálogo `Currencies`), compartido por lista, servicio y mensajes.
  - Puertos con las firmas de §3.3: `IBillingCustomerLookup` (`BillingCustomerRef(Guid Id, string Name, string? PhoneE164, string? Email, bool IsActive)`), `IBillingMemberDirectory` (`BillingMemberRef(Guid MemberId, string DisplayName, string? Email, bool IsActive, bool CanManage)`; `FindAsync(tenantId, IReadOnlyCollection<Guid>)`, `ListManagersAsync(tenantId)` = activas cuyos roles conceden `billing.service.manage`), `IBillingCustomerEmail.SendAsync(BillingCustomerMessage, ct) : Task<BillingSendResult>`, `IBillingStaffEmail.SendDigestAsync(BillingDigestEmail, ct)`, `IBillingWhatsAppOutbound` (`SendAsync(BillingCustomerMessage, Guid connectionId, ct)`, `ListConnectionsAsync(tenantId, ct) : Task<IReadOnlyList<BillingConnectionDto>>`), `IBillingConsentGate.MayNotifyAsync(tenantId, customerId, ct) : Task<bool>`.
  - `public abstract record BillingSendResult { sealed record Sent; sealed record Failed(string Code); }`.
  - `BillingCustomerMessage(BillingMessageKind Kind /* Reminder | Overdue */, string ToAddress /* email o wa_id */, string CustomerName, string TenantName, IReadOnlyList<BillingMessageLine> Lines, IReadOnlyList<CurrencyAmountDto> TotalByCurrency, DateOnly NearestDueDate, BillingEmailFormat Format)`; `BillingMessageLine(string ServiceName, decimal Amount, string Currency, DateOnly DueDate, int DaysOverdue)`; `BillingDigestEmail(string ToAddress, string MemberName, string TenantName, IReadOnlyList<BillingDigestRow> DueSoon, IReadOnlyList<BillingDigestRow> Overdue, IReadOnlyList<BillingDigestRow> FailedReminders, IReadOnlyList<CurrencyAmountDto> OverdueTotalByCurrency, BillingEmailFormat Format)`; `BillingDigestRow(string CustomerName, string ServiceName, decimal Amount, string Currency, DateOnly DueDate, string? FailureCode)`. `EmailRegionalFormat` ya existe en `Modules.Notifications.Application`; **Billing no la puede referenciar**: `BillingEmailFormat(string TimeZoneId, string DateFormat, string NumberFormat)` vive en Billing.Application y el adaptador la traduce.
  - Repositorios: `IBillingServiceRepository { Add, FindAsync(tenantId, id), ListAsync(BillingServiceQuery) → (items, total), ListActiveAsync(tenantId) }`, `IChargeRepository { AddRange, FindAsync, ListPendingByServiceAsync(serviceId), ListPendingByTenantAsync(tenantId), ListAsync(ChargeQuery), ListByServiceAsync, SummaryAsync(tenantId, today) }`, `ICustomerBillingProfileRepository { FindAsync(tenantId, customerId), ListAsync(tenantId), Add }`, `IBillingSettingsRepository { GetOrDefaultAsync, Upsert }`, `IBillingUnitOfWork.SaveChangesAsync(ct)`.

- [ ] **Step 1: Write the failing tests** — `BillingLayerTests` (los cinco de `SuppliersLayerTests` con `BillingPermissions`, `BillingInfrastructureExtensions`, `BillingEndpoints`, `BillingService`) y en `CompositionRootTests`:

```csharp
[Fact]
public void TheBillingPermissionsRequireTheBillingModuleAndGoToAdminAndBilling()
{
    // definitions[permission].Category == "Billing"; RequiredModules == [ModuleKeys.Billing];
    // los roles "admin" y "billing" conceden Read y Manage; "advisor" y "cashier" no (owner, 2026-10-29)
}
```

- [ ] **Step 2: Run to verify RED** — `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~BillingLayerTests|FullyQualifiedName~TheBillingPermissions"` → FAIL por compilación.

- [ ] **Step 3: Implement** los proyectos, tipos y registros de **Files**. Sin handlers todavía. `BillingAuthorization` copia `PipelineSupport.cs:10-25` citándolo.

- [ ] **Step 4: Run to verify GREEN** — mismo filtro → PASS; `dotnet build` limpio.

- [ ] **Step 5: Commit** — `feat(billing): scaffold module with permissions, ports and layer tests`

---

### Task 4: Persistencia: `BillingDbContext`, migración inicial, repositorios

**Files:**
- Create: `src/Modules/Billing/Modules.Billing.Infrastructure/Persistence/BillingDbContext.cs`, `BillingDbContextFactory.cs`, `BillingServiceRepository.cs`, `ChargeRepository.cs`, `CustomerBillingProfileRepository.cs`, `BillingSettingsRepository.cs`, `BillingUnitOfWork.cs`, `BillingSettings.cs` (entidad `TenantId`, `WhatsAppConnectionId`, `Version`), `CustomerMessage.cs`, `ReminderStep.cs`, `DailyDigest.cs`, `TenantRun.cs` (entidades de infraestructura: nadie fuera del worker las escribe; `lastReminder` las lee), `BillingDatabaseInitializer.cs` (copia de `SuppliersDatabaseInitializer.cs`), `Migrations/<timestamp>_InitialBilling.cs`
- Modify: `BillingInfrastructureExtensions.cs` (DbContext, repositorios, UoW, initializer)
- Test: `tests/Modules/Billing/Modules.Billing.IntegrationTests/Modules.Billing.IntegrationTests.csproj`, `BillingApiHarness.cs` (copia de `SuppliersApiHarness.cs` con `BillingPermissions`, `TenantModuleKeys.Billing` y una `SeedCustomerAsync(factory, tenantId, name, phoneE164, email)` que inserta en `customers.customers` lo mínimo, como lo hacen los harness de Quotations), `BillingPersistenceTests.cs`

**Interfaces:**
- Produces: tablas de §2 con esos nombres; `BillingDbContext` con `DbSet<BillingService> Services`, `DbSet<Charge> Charges`, `DbSet<CustomerBillingProfile> Profiles`, `DbSet<BillingSettings> Settings`, `internal DbSet<CustomerMessage> CustomerMessages`, `DbSet<ReminderStep> ReminderSteps`, `DbSet<DailyDigest> DailyDigests`, `DbSet<TenantRun> TenantRuns`.
- Consumes: entidades de Task 2.

- [ ] **Step 1: Write the failing tests** (integración, Testcontainers):

```csharp
[Fact] public async Task ASecondChargeWithTheSameServiceAndDueDateIsRejected();     // DbUpdateException por IX_charges_service_due
[Fact] public async Task ASecondMessageForTheSameCustomerAndDayIsRejected();        // IX_customer_messages_tenant_customer_day
[Fact] public async Task ASecondStepRowForTheSameChargeAndStepIsRejected();         // PK_reminder_steps
[Fact] public async Task AServiceRoundTripsCycleStatusReactivationAndLastGenerated(); // cycle_months 6, Paused, reactivated_on, last_generated_due_date
[Fact] public async Task TheCycleMonthsCheckRejectsOtherValues();                  // UPDATE a 3 → PostgresException 23514
```

- [ ] **Step 2: Run to verify RED** — `$env:Seed__Enabled = "false"`; `dotnet test tests/Modules/Billing/Modules.Billing.IntegrationTests --filter FullyQualifiedName~BillingPersistenceTests` → FAIL (no hay DbContext).

- [ ] **Step 3: Implement** — mapeo con nombres snake_case de §2, `HasConversion<string>()` para enums, `Version` como `IsConcurrencyToken`, CHECKs de §2 con `HasCheckConstraint`, índices nombrados `IX_charges_tenant_status_due`, `IX_charges_tenant_customer_status`, `IX_charges_service_due` (único), `IX_customer_messages_tenant_customer_day` (único), `IX_services_tenant_customer`; `reminder_steps` con PK `(charge_id, step)`. Genera `InitialBilling` con el factory. Repositorios como `SupplierRepository.cs`; `ChargeRepository.SummaryAsync` agrupa en SQL (`GroupBy` sobre el tipo anónimo del `Where`, no sobre un record: memoria del proyecto «EF no agrega sobre una proyección a record»).

- [ ] **Step 4: Run to verify GREEN** — mismo filtro → PASS.

- [ ] **Step 5: Commit** — `feat(billing): add persistence and initial migration`

---

### Task 5: Adaptadores en Bootstrapper: clientes, miembros y consentimiento

**Files:**
- Create: `src/Bootstrapper/BillingCustomerLookup.cs` (sobre `CustomersDbContext`, como `QuotationCustomerLookup.cs`), `src/Bootstrapper/BillingMemberDirectory.cs` (sobre `TenancyDbContext.Memberships` + `IUserDirectory` + `IRolePermissionChecker` de Tenancy.Application, como `MessagingAssignees`), `src/Bootstrapper/BillingConsentGate.cs` (`MayNotifyAsync` → `true`; el comentario dice que el owner decidió el 2026-10-29 que el aviso de cobro sale sin importar el consentimiento de Ley 1581 y que éste es el único punto que cambiaría, con la cita a `CustomerDataConsent.cs:52-56`)
- Modify: `QepServiceCollectionExtensions.cs` (registros scoped, junto a `:1123`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/BillingConsentGateTests.cs`; `tests/Modules/Billing/Modules.Billing.IntegrationTests/BillingDirectoriesTests.cs`

**Interfaces:**
- Consumes: puertos de Task 3.
- Produces: implementaciones registradas de `IBillingCustomerLookup`, `IBillingMemberDirectory`, `IBillingConsentGate`.

- [ ] **Step 1: Write the failing tests**

```csharp
// BillingConsentGateTests
[Fact] public async Task Phase1AlwaysAllows();
// BillingDirectoriesTests (integración; siembra tenant, dos membresías: una Active con rol admin, una Removed)
[Fact] public async Task FindAsyncReturnsNameEmailActivityAndCanManageOfEachMember();   // Removed → IsActive false; rol advisor → CanManage false
[Fact] public async Task ListManagersReturnsOnlyActiveMembersWhoseRolesGrantManage();   // admin y billing sí; advisor no
[Fact] public async Task CustomerLookupReturnsPhoneE164AndEmail();
[Fact] public async Task SearchIdsMatchesTheCustomerNameCaseInsensitively();
```

- [ ] **Step 2: Run to verify RED** → FAIL (tipos inexistentes).

- [ ] **Step 3: Implement** — `DisplayName` de la membresía, o «Sin nombre» si es null (`MembershipEndpoints.cs:320-323`); `Email` por `IUserDirectory.GetEmailAsync(membership.UserId)`. `ListManagersAsync`: membresías `Active` filtradas con `IRolePermissionChecker` para `BillingPermissions.Manage`.

- [ ] **Step 4: Run to verify GREEN**.

- [ ] **Step 5: Commit** — `feat(billing): add customer, member and consent adapters`

---

### Task 6: Perfil de cobro y encargados (endpoints)

**Files:**
- Create: `Modules.Billing.Application/GetCustomerProfile.cs`, `UpsertCustomerProfile.cs` (comando + validador + handler), `ListAssignees.cs`, `ProfileRules.cs` (`EnsureChannelIsReachable(BillingChannel, BillingCustomerRef)` → códigos §3.1; `EnsureAssigneeCanManage(BillingMemberRef?)` → `assignee_invalid` si es null, no activo o sin `CanManage`)
- Modify: `Modules.Billing.Api/BillingEndpoints.cs` (`GET/PUT /customers/{customerId:guid}/profile`, `GET /assignees`), `QepServiceCollectionExtensions.cs` (handlers a mano, como Suppliers `:264-289`)
- Test: `Modules.Billing.UnitTests/ProfileRulesTests.cs`, `Modules.Billing.IntegrationTests/BillingProfileApiTests.cs`

**Interfaces:**
- Produces: `UpsertCustomerProfileCommand(Guid TenantId, Guid CustomerId, string Channel, Guid AssigneeMemberId) : ICommand<ProfileDto>`; `GetCustomerProfileQuery(Guid TenantId, Guid CustomerId) : IQuery<ProfileDto>` (lanza `ResourceNotFoundException(billing.profile.not_found)`); `ListAssigneesQuery(Guid TenantId) : IQuery<AssigneesDto>`; `ProfileRules` reutilizado por Task 7.

- [ ] **Step 1: Write the failing tests**

```csharp
// ProfileRulesTests
[Fact] public void WhatsAppNeedsAPhoneAndEmailNeedsAnAddress();  // los dos códigos de §3.1
// BillingProfileApiTests
[Fact] public async Task PutCreatesTheProfileAndGetReadsItBack();                 // 200, channel, assignee.displayName, version 1
[Fact] public async Task PutAgainOverwritesWithoutIfMatch();                      // version 2
[Fact] public async Task PutWithARemovedMemberAnswers422AssigneeInvalid();
[Fact] public async Task PutWithAnActiveMemberWithoutManageAnswers422AssigneeInvalid();   // owner 2026-10-29: el encargado tiene que poder marcar pagos
[Fact] public async Task PutWhatsAppForACustomerWithoutPhoneAnswers422();         // billing.profile.whatsapp_requires_phone
[Fact] public async Task GetWithoutProfileAnswers404WithCode();                   // billing.profile.not_found
[Fact] public async Task AnotherTenantAnswers403NotFound404();                    // authorization.denied
[Fact] public async Task WithTheModuleOffAnswers403ModuleNotEnabled();            // SeedTenantAsync sin Billing
[Fact] public async Task AssigneesListsOnlyActiveMembersWhoCanManageSortedByName();
```

- [ ] **Step 2: Run to verify RED** → FAIL.

- [ ] **Step 3: Implement** handlers con `BillingAuthorization.EnsureAsync` primero; `UpsertCustomerProfileHandler` audita `billing.profile.updated`.

- [ ] **Step 4: Run to verify GREEN** — `--filter "FullyQualifiedName~ProfileRulesTests|FullyQualifiedName~BillingProfileApiTests"`.

- [ ] **Step 5: Commit** — `feat(billing): add customer billing profile and assignees endpoints`

---

### Task 7: Servicios recurrentes (crear, leer, editar, pausar, reactivar, cancelar)

**Files:**
- Create: `Modules.Billing.Application/CreateService.cs`, `UpdateService.cs`, `ChangeServiceStatus.cs` (un comando con `BillingServiceTransition { Pause, Resume, Cancel }`), `GetService.cs`, `ListServices.cs`, `ServiceDetailComposer.cs` (arma `ServiceDetailDto`: cliente por `IBillingCustomerLookup`, miembros por `IBillingMemberDirectory.FindAsync` en un batch, último aviso por cobro)
- Modify: `BillingEndpoints.cs` (rutas de §4 «Servicios»; `If-Match` leído como en `MessagingEndpoints` → 428 sin header, 412 con versión vieja), `QepServiceCollectionExtensions.cs`
- Test: `Modules.Billing.IntegrationTests/BillingServiceApiTests.cs`

**Interfaces:**
- Produces: `CreateServiceCommand(Guid TenantId, Guid CustomerId, string Name, decimal Price, string? Currency, string Cycle, DateOnly FirstDueDate, ProfileInput? Profile) : ICommand<ServiceDetailDto>`; `UpdateServiceCommand(..., long ExpectedVersion)`; `ChangeServiceStatusCommand(Guid TenantId, Guid ServiceId, BillingServiceTransition Transition, long ExpectedVersion)`; `GetServiceQuery`; `ListServicesQuery(Guid TenantId, Guid? CustomerId, string Status, string? Search, int Page, int PageSize)`; `ServiceDetailComposer.ComposeAsync(BillingService, ct) : Task<ServiceDetailDto>` (lo reutiliza Task 8).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task CreateWithProfileCreatesServiceProfileAndFirstCharge();      // first en 2 meses: 201; charges.Count == 1; nextCharge.dueDate == firstDueDate; status Pending
[Fact] public async Task CreateWithAPastFirstDueDateGeneratesEveryPeriodUpToTheWindow(); // Review Focus 4: first = today − 3 meses, mensual → 4 cobros, 3 "Overdue" con daysOverdue > 0; openCount 4; openAmountByCurrency == 4 × price
[Fact] public async Task CreateWithoutProfileForACustomerWithoutOneAnswers422();     // billing.profile.missing
[Fact] public async Task CreateIgnoresTheProfileWhenTheCustomerAlreadyHasOne();      // el perfil no cambia
[Fact] public async Task CreateWithNullCurrencyUsesTheTenantDefault();              // "COP" del tenant sembrado
[Fact] public async Task CreateWithAnUnknownCurrencyAnswers422();                   // billing.currency.unknown
[Fact] public async Task CreateForAnInactiveCustomerAnswers422();                    // billing.customer.inactive
[Fact] public async Task CreateValidatesNamePriceCycleAndDate();                     // validation.failed con errors.Name, errors.Price, errors.Cycle, errors.FirstDueDate
[Fact] public async Task UpdateChangesNamePriceCurrencyAndKeepsTheGeneratedCharge(); // el cobro conserva el monto viejo
[Fact] public async Task UpdateWithoutIfMatchAnswers428AndWithAnOldVersion412();
[Fact] public async Task PauseResumeCancelFollowTheTransitionTable();               // Resume sobre Active → 422 invalid_transition
[Fact] public async Task CancelCancelsEveryPendingCharge();                         // servicio con 3 Pending → los 3 "Cancelled"; nextCharge null; openCount 0
[Fact] public async Task PauseKeepsThePendingChargesPending();                      // nada cambia en charges
[Fact] public async Task ListFiltersByCustomerAndStatusAndDefaultsToActive();
[Fact] public async Task GetOfAnotherTenantsServiceAnswers403();
[Fact] public async Task PreviewChargesCountsWhatCreateWouldGenerateWithoutWriting();   // GET /services/preview-charges?cycle=Monthly&firstDueDate=today−3m → { chargesToGenerate 4, overdueCharges 3 }; 0 filas en billing.services
[Fact] public async Task PreviewChargesWithABadCycleAnswers422();
```

Agrega a **Files**: `Modules.Billing.Application/PreviewServiceCharges.cs` (`PreviewServiceChargesQuery(Guid TenantId, string Cycle, DateOnly FirstDueDate) : IQuery<ChargesPreviewDto>`; `ChargesPreviewDto(int ChargesToGenerate, int OverdueCharges)`; corre `ChargeGenerator.Generate` sobre un `BillingService` transitorio y cuenta) y la ruta `GET /services/preview-charges` **antes** de `/{serviceId:guid}` en `BillingEndpoints.cs`.

- [ ] **Step 2: Run to verify RED** → FAIL.

- [ ] **Step 3: Implement** — `CreateServiceHandler`: autorización → cliente (`IBillingCustomerLookup.FindAsync`, `not_found`/`inactive`) → perfil (existe o se crea con `ProfileRules`) → moneda (`ITenantDefaultCurrency.GetAsync` si null; `Currencies` para validar) → `BillingService.Create` → `ChargeGenerator.Generate(service, calendar.Today, now)` → auditoría → `SaveChangesAsync` una vez. `Cancel` carga los `Pending` con `ListPendingByServiceAsync` y los cancela en la misma transacción. `ServiceDetailComposer` arma `NextCharge`, `OpenCount` y `OpenAmountByCurrency` con `CurrencyTotals.Of`.

- [ ] **Step 4: Run to verify GREEN** — `--filter FullyQualifiedName~BillingServiceApiTests`.

- [ ] **Step 5: Commit** — `feat(billing): add recurring service endpoints`

---

### Task 8: Cobros: lista «Por cobrar», detalle, marcar pagado, corregir pago

**Files:**
- Create: `Modules.Billing.Application/ListCharges.cs`, `GetCharge.cs`, `PayCharge.cs`, `UpdateChargePayment.cs`, `ChargeListComposer.cs`
- Modify: `BillingEndpoints.cs` (rutas de §4 «Cobros»), `QepServiceCollectionExtensions.cs`
- Test: `Modules.Billing.IntegrationTests/BillingChargeApiTests.cs`

**Interfaces:**
- Produces: `ListChargesQuery(Guid TenantId, string Status, bool AssigneeMe, Guid? CustomerId, string? Search, int Page, int PageSize) : IQuery<ChargePageDto>`; `PayChargeCommand(Guid TenantId, Guid ChargeId, DateOnly PaidOn, string? Note) : ICommand<ChargeDto>`; `UpdateChargePaymentCommand(...) : ICommand<ChargeDto>`.
- Consumes: `ChargeListComposer` (mapeo con cliente, encargado, quién marcó y último paso), `IMembershipDirectory.FindActiveMembershipIdAsync` (quién marca y `assignee=me`), `ITenantClock` (hoy).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task PayMarksPaidRecordsWhoAndGeneratesNothing();                      // 200 ChargeDto Paid con payment.paidBy.displayName; el servicio sigue con los mismos cobros (owner 2026-10-29)
[Fact] public async Task PayingAnAlreadyPaidChargeAnswers422NotPending();
[Fact] public async Task PayWithAFutureDateAnswers422DateInFuture();                       // paidOn = tenant today + 1
[Fact] public async Task PayWithA1001CharNoteAnswers422();                                 // validation.failed errors.Note
[Fact] public async Task PayingAChargeOfAPausedOrCancelledServiceIsAllowed();              // la deuda existe
[Fact] public async Task UpdatePaymentChangesDateAndNoteOnlyOnAPaidCharge();              // Pending → 422 not_paid
[Fact] public async Task OpenListIsSortedByDueDateAndMarksOverdueWithTodayOfTheTenant();   // status "Overdue", daysOverdue
[Fact] public async Task SummaryIsTheWholeTenantsDebtRegardlessOfFilters();                // openCount, overdueCount, openAmountByCurrency y overdueAmountByCurrency: un total por moneda (COP y USD separados)
[Fact] public async Task AssigneeMeReturnsOnlyTheCallersCustomers();
[Fact] public async Task AssigneeMeWithoutMembershipAnswersEmpty();                        // Review Focus 5
[Fact] public async Task SearchMatchesCustomerNameAndServiceName();
[Fact] public async Task PageSizeAbove100Answers422();
```

- [ ] **Step 2: Run to verify RED** → FAIL.

- [ ] **Step 3: Implement** — `PayChargeHandler`: autorización → cobro → `calendar.Today` → `charge.MarkPaid(...)` → auditoría `billing.charge.paid` → `SaveChangesAsync` → `ChargeListComposer` para el DTO. **No genera**: eso es del worker (§3.2). `ListChargesHandler` resuelve `today` una vez y lo pasa al mapeo y al `SummaryAsync`.

- [ ] **Step 4: Run to verify GREEN** — `--filter FullyQualifiedName~BillingChargeApiTests`.

- [ ] **Step 5: Commit** — `feat(billing): add charge list and payment endpoints`

---

### Task 9: Plantillas de correo en Notifications y adaptadores de envío

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/BillingReminderEmailTemplate.cs` (`TemplateRef = "billing.reminder.v1"`, `Render(BillingCustomerEmailInput, EmailRegionalFormat) : EmailMessage`; asunto con un servicio «Tu servicio {servicio} vence el {fecha}», con varios «Tus servicios vencen el {fecha}»; cuerpo: tabla servicio / monto / vence, total por moneda, «Si ya realizaste el pago, ignora este mensaje.»), `BillingOverdueEmailTemplate.cs` (`"billing.overdue.v1"`; asunto «Tienes {n} cobro(s) vencido(s)»; tabla con días de atraso, total por moneda, tono firme: «Escríbenos para ponerte al día.»), `BillingDigestEmailTemplate.cs` (`"billing.digest.v1"`; asunto «Cobros de hoy: {n} por vencer, {m} vencidos, {k} avisos fallidos»; tres secciones, las vacías no se dibujan; total vencido por moneda). Nombres de tenant, cliente y servicio escapados en HTML como `InvitationEmailTemplate.cs:25`.
- Create: `src/Bootstrapper/BillingEmailSender.cs` (implementa `IBillingCustomerEmail` e `IBillingStaffEmail` sobre `IEmailChannel` y los templates; elige el template por `Kind`; una excepción del canal → `Failed("send_failed")`), `src/Bootstrapper/BillingWhatsAppOutboundUnavailable.cs` (`IBillingWhatsAppOutbound` provisional: `SendAsync` → `Failed("channel_not_configured")`, `ListConnectionsAsync` → `[]`; la Task 12 lo reemplaza)
- Modify: `QepServiceCollectionExtensions.cs` (registros)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/BillingEmailTemplatesTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/BillingEmailSenderTests.cs`

**Interfaces:**
- Consumes: `IEmailChannel`, `EmailMessage`, `EmailRegionalFormats` (Notifications.Application); `BillingCustomerMessage`, `BillingDigestEmail`, `BillingSendResult` (Task 3).

- [ ] **Step 1: Write the failing tests**

```csharp
// BillingEmailTemplatesTests
[Fact] public void ReminderUsesTheSoftSubjectAndTheTenantFormat();            // "vence el 10/03/2026" con DateFormat dd/MM/yyyy; "$ 150.000" con NumberFormat del tenant
[Fact] public void ReminderWithTwoServicesListsBothAndOneTotalPerCurrency();   // "$ 150.000 y USD 20" (Review Focus 5 por email)
[Fact] public void OverdueUsesTheFirmSubjectWithTheCountAndTheDaysOverdue();  // "Tienes 2 cobro(s) vencido(s)"
[Fact] public void TemplatesEscapeTenantCustomerAndServiceNamesInHtml();      // "<b>" → "&lt;b&gt;"
[Fact] public void DigestOmitsEmptySectionsAndCountsInTheSubject();
// BillingEmailSenderTests (IEmailChannel grabador, como RecordingEmailChannel)
[Fact] public async Task AChannelExceptionBecomesFailedSendFailed();
[Fact] public async Task ASuccessfulSendIsSentAndCarriesTheRecipient();
```

- [ ] **Step 2: Run to verify RED** → FAIL.

- [ ] **Step 3: Implement** según **Files**; el adaptador traduce `BillingEmailFormat` → `EmailRegionalFormats.For(TimeZoneInfo.FindSystemTimeZoneById(...), new TenantRegionalSettings(...))` o la sobrecarga que exista en `EmailRegionalFormats` (leerla antes; no inventar).

- [ ] **Step 4: Run to verify GREEN**.

- [ ] **Step 5: Commit** — `feat(billing): add reminder and digest email templates with senders`

---

### Task 10: Calendario, procesador diario y worker

**Files:**
- Create: `Modules.Billing.Domain/ReminderCalendar.cs`
- Create: `Modules.Billing.Application/IBillingReminderProcessor.cs` (`ProcessTenantAsync(Guid tenantId, CancellationToken)`), `BillingOptions.cs` (`SweepMinutes`, `SendHourLocal`, `Validate`)
- Create: `Modules.Billing.Application/CustomerMessagePlanner.cs` (puro: `Plan(IReadOnlyList<Charge> pendingOfCustomer, IReadOnlySet<(Guid ChargeId, string Step)> covered, DateOnly today) : CustomerMessagePlan?` con `Kind`, `Charges` a listar y `Steps` a cubrir, según §3.3 paso 4)
- Create: `Modules.Billing.Infrastructure/Reminders/BillingReminderProcessor.cs`, `CustomerMessageStore.cs` (claim por inserción del mensaje + filas de `reminder_steps`, lease 5 min, `MarkSent/MarkFailed`), `DailyDigestStore.cs`, `TenantRunStore.cs`, `BillingReminderWorker.cs`
- Modify: `BillingInfrastructureExtensions.cs` (`AddHostedService<BillingReminderWorker>()`, opciones validadas), `src/Api/appsettings.json` (`"Billing": { "SweepMinutes": 15, "SendHourLocal": 8 }`)
- Test: `Modules.Billing.UnitTests/ReminderCalendarTests.cs`, `CustomerMessagePlannerTests.cs`, `Modules.Billing.IntegrationTests/BillingReminderProcessorTests.cs` (con `IBillingCustomerEmail`/`IBillingStaffEmail` grabadores inyectados por `ConfigureServices` del harness, y el worker apagado como `runDeliveryWorkers` en `NotificationsApiFactory`)

**Interfaces:**
- Produces: `ReminderCalendar.StepDueOn(DateOnly dueDate, DateOnly today) : string?` — el último paso con fecha `<= today`, o `null`; `ReminderCalendar.DateOf(string step, DateOnly dueDate) : DateOnly`; `CustomerMessagePlanner.Plan(...)` (arriba); `IBillingReminderProcessor.ProcessTenantAsync` corre §3.3 pasos 2-6 en orden: generación (`ChargeGenerator` por servicio `Active`, `AddRange` con la violación del único tratada como «ya existe»), mensajes por cliente, digests.
- Consumes: puertos de Task 3 y 5, adaptadores de Task 9, `ITenantClock`, `ITenantDirectory.GetRegionalSettingsAsync` y `GetDisplayNameAsync`, `IActiveTenantsQuery`, `ITenantModules`.

- [ ] **Step 1: Write the failing tests**

```csharp
// ReminderCalendarTests (due = 2026-03-10)
[Theory]
[InlineData("2026-03-04", null)]          // faltan 6 días
[InlineData("2026-03-05", "before_5")]
[InlineData("2026-03-09", "before_5")]    // nada nuevo tocó; sigue siendo el último
[InlineData("2026-03-10", "due")]
[InlineData("2026-03-12", "due")]
[InlineData("2026-03-13", "after_3")]
[InlineData("2026-03-19", "after_3")]
[InlineData("2026-03-20", "after_10")]
[InlineData("2026-03-27", "after_17")]
public void StepDueOnIsTheLatestStepNotAfterToday(string today, string? expected);
[Fact] public void DateOfInvertsStepDueOn();

// CustomerMessagePlannerTests (today = 2026-03-10)
[Fact] public void NoStepDueMeansNoMessage();                                        // único cobro due 03-20 → null
[Fact] public void OneUpcomingChargeIsAReminderCoveringItsStep();                    // due 03-10 sin cubrir → Reminder, Steps [(id,"due")]
[Fact] public void TwoUpcomingChargesTheSameDayAreOneReminder();                     // due 03-10 y due 03-15 (before_5) → Reminder con los dos, NearestDueDate 03-10
[Fact] public void AnOverdueStepMakesAnOverdueMessageListingEveryOverdueCharge();    // due 02-25 (after_10 hoy) + due 02-20 (sin paso hoy, ya cubierto) + due 03-15 → Overdue lista 02-25 y 02-20, cubre after_10 y before_5
[Fact] public void AlreadyCoveredStepsAreIgnored();                                  // (id,"due") en covered → null

// BillingReminderProcessorTests
[Fact] public async Task ADueTodayChargeGetsOneEmailOneMessageRowAndOneStepRow();    // customer_messages 1 Sent kind Reminder; reminder_steps (charge,"due")
[Fact] public async Task RunningTwiceTheSameDaySendsNothingMore();                   // tenant_runs finished → 0 envíos nuevos
[Fact] public async Task TwoConcurrentRunsGenerateOnceAndMessageOnce();              // Review Focus 2: Task.WhenAll de dos ProcessTenantAsync → 1 cobro nuevo por período, 1 email
[Fact] public async Task ACatchUpCoversOnlyTheLatestDueStep();                       // Review Focus 3: due = today−12, sin filas → sólo after_10
[Fact] public async Task ACatchUpGeneratesEveryMissedPeriod();                       // Review Focus 3: last_generated hace 3 meses, mensual → 3 cobros nuevos (hasta today+5)
[Fact] public async Task TheRunGeneratesTheChargeFiveDaysBeforeItsDueDateAndNotEarlier(); // ancla today+5 → se crea; ancla today+6 → no
[Fact] public async Task AnOverdueMessageGroupsEveryOverdueChargeWithOneTotalPerCurrency(); // Review Focus 5: 2 vencidos COP+USD → 1 mensaje Overdue, "$ 450.000 y USD 20"
[Fact] public async Task TwoCustomersGetTwoMessagesAndOneCustomerNeverGetsTwo();
[Fact] public async Task PaidAndCancelledChargesGetNoReminderButAPausedServicesPendingChargesDo(); // owner 2026-10-29
[Fact] public async Task APausedServiceGeneratesNoNewCharge();
[Fact] public async Task BeforeTheSendHourNothingHappens();                          // reloj fijo 07:59 del tenant → 0 filas
[Fact] public async Task AWhatsAppProfileWithoutConnectionFailsWithChannelNotConfiguredAndNeverFallsBackToEmail();
[Fact] public async Task AnEmailProfileWhoseCustomerLostTheAddressFailsWithNoEmail();
[Fact] public async Task TheDigestGoesOncePerAssigneeWithDueSoonOverdueAndFailedRows(); // daily_digests 1 fila; secciones correctas; cobro a 6 días no entra
[Fact] public async Task AnAssigneeNoLongerActiveOrWithoutManageSendsTheRowsToEveryManager();
[Fact] public async Task NoRowsOfInterestSendsNoDigest();
[Fact] public async Task AFailingCustomerSendDoesNotStopTheOthers();                 // dos clientes, el primero lanza → el segundo Sent, el primero Failed send_failed con sus pasos cubiertos igual
```

El reloj del tenant se fija inyectando un `ITenantClock` de prueba (precedente `FixedTenantClock` en Reporting tests).

- [ ] **Step 2: Run to verify RED** → FAIL.

- [ ] **Step 3: Implement** — `BillingReminderProcessor.ProcessTenantAsync` sigue §3.3 paso a paso. Reclamo del run: `INSERT ... ON CONFLICT (tenant_id, run_date) DO UPDATE SET claimed_until = EXCLUDED.claimed_until WHERE billing.tenant_runs.finished_at IS NULL AND billing.tenant_runs.claimed_until < now() RETURNING tenant_id` → sin fila devuelta, otra réplica lo tiene. Reclamo del mensaje: `INSERT` de la fila `Sending` más sus `reminder_steps` en un `SaveChanges`; `DbUpdateException` por `IX_customer_messages_tenant_customer_day` o `PK_reminder_steps` → skip (otra réplica). Generación: `AddRange` por servicio y `SaveChanges`; la violación de `IX_charges_service_due` se trata como «ya existe» y se relee. Un `try` por servicio, por cliente y por email; `ChangeTracker.Clear()` en el `finally` (precedente `OutboxDeliveryWorker.cs:138-143`). `BillingReminderWorker` copia `QuotationExpirationWorker.cs` con el intervalo de `BillingOptions` y, por tenant, `try/catch` que registra y sigue.

- [ ] **Step 4: Run to verify GREEN** — `--filter "FullyQualifiedName~ReminderCalendarTests|FullyQualifiedName~BillingReminderProcessorTests"`.

- [ ] **Step 5: Commit** — `feat(billing): add charge generation, grouped customer messages and the daily worker`

---

### Task 11: Configuración del módulo, fusión de clientes y retención de usuarios

**Files:**
- Create: `Modules.Billing.Application/GetBillingSettings.cs`, `UpdateBillingSettings.cs`
- Modify: `BillingEndpoints.cs` (`GET/PUT /settings`), `QepServiceCollectionExtensions.cs`
- Modify: el adaptador de `ICustomerMergeReferences` en Bootstrapper (`MoveAsync`: `UPDATE billing.services/charges SET customer_id = survivor`; perfil del fusionado: `DELETE` si el que queda tiene, `UPDATE customer_id` si no; `CountAsync` **sin cambios**, con el comentario de seguimiento de §5)
- Create: `src/Bootstrapper/BillingUserReferenceProbe.cs` (`Source = "billing"`), registrado como las demás sondas
- Test: `Modules.Billing.IntegrationTests/BillingSettingsApiTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/<la clase que prueba la fusión>` (+ un caso), `tests/Bootstrapper/Bootstrapper.UnitTests/BillingUserReferenceProbeTests.cs` o integración si la sonda consulta la base

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task GetSettingsAnswersTheStoredConnectionAndWhatListConnectionsReturns(); // con el IBillingWhatsAppOutbound provisional: connections []
[Fact] public async Task PutWithAnUnknownConnectionAnswers422();                  // billing.settings.connection_unknown: no está en ListConnectionsAsync
[Fact] public async Task PutNullClearsTheConnection();
[Fact] public async Task MergingMovesServicesAndChargesToTheSurvivorAndDropsTheDuplicateProfile();
[Fact] public async Task TheProbeRetainsAUserWhoIsAnAssigneeOrMarkedAPayment();
```

- [ ] **Step 2: RED** → FAIL. **Step 3: Implement.** **Step 4: GREEN.**

- [ ] **Step 5: Commit** — `feat(billing): add module settings, customer merge and user retention`

---

### Task 12: Canal de WhatsApp (plantillas aprobadas por el owner, 2026-10-29)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs` (o donde viva `MessageTemplateKind`): `BillingReminder`, `BillingOverdue`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/BillingTemplateProvisioning.cs` (copia de `QuotationTemplateProvisioning.cs`: dos definiciones con los nombres `qep_cobro_recordatorio` / `qep_cobro_vencido`, categoría `UTILITY`, idioma `es`, **parámetros posicionales** y el cuerpo **literal** de §5 del contrato), `BillingTemplateOutbound.cs` (`IBillingTemplateOutbound.SendAsync(BillingTemplateSend) : Task<BillingTemplateSendResult>`; copia de `QuotationTemplateOutbound` sin `HeaderDocument` ni R2; mismo `IsAcceptable`, misma apertura por teléfono con `IConversationOpener.OpenByPhoneAsync`, misma `OutboundSettlement`), `IBillingTemplateReadiness.FindApprovedAsync(tenantId, connectionId, kind)` junto a `TemplateManagementPorts.cs:51`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuotationTemplateReadiness.cs` (o una clase hermana) para el nuevo puerto; el aprovisionamiento corre donde corre el de cotizaciones (leer `QuotationTemplateProvisioning` y su llamador antes; no inventar el disparador)
- Create: `src/Bootstrapper/BillingMetaWhatsAppSender.cs` (`IBillingWhatsAppOutbound` real: `ListConnectionsAsync` sobre las conexiones `whatsapp-cloud` activas de Integrations como `QuotationsWhatsAppChannelDirectory.cs:26-32`, `Ready` = ambas plantillas `APPROVED`, `NotReadyReason` `template_not_approved`; `SendAsync` elige la plantilla por `Kind`, arma `{{1}}..{{4}}` según §5 —nombres unidos con « y », totales por moneda unidos con « y », fecha con el formato del tenant— y traduce `ConnectionUnavailable(reason)` → `Failed("connection_unavailable:<reason>")`, `Failed(code)` → `Failed("send_failed:<code>")`); borra `BillingWhatsAppOutboundUnavailable.cs`
- Modify: `QepServiceCollectionExtensions.cs` (registro), `Reminders/BillingReminderProcessor.cs` (canal `WhatsApp`: sin `settings.WhatsAppConnectionId` → `channel_not_configured`; sin `PhoneE164` → `no_phone`; si no, `IBillingWhatsAppOutbound.SendAsync`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/BillingTemplateProvisioningTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/BillingMetaWhatsAppSenderTests.cs`, `Modules.Billing.IntegrationTests/BillingReminderProcessorTests.cs` (+ casos WhatsApp), `BillingSettingsApiTests.cs` (+ casos)

**Interfaces:**
- Consumes: `IBillingWhatsAppOutbound`, `BillingCustomerMessage`, `BillingConnectionDto` (Task 3); `IWhatsAppCloudClient.SendTemplateAsync`, `TemplateSend`, `OutboundSettlement`, `IMessagingConnectionDirectory` (Messaging).
- Produces: `BillingTemplateSend(Guid TenantId, Guid ConnectionId, string WaId, Guid CustomerId, string RecipientName, string TemplateName, string TemplateLanguage, IReadOnlyList<string> PositionalParameters)`; `BillingTemplateSendResult` con las mismas tres variantes que `QuotationTemplateSendResult`.

- [ ] **Step 1: Write the failing tests**

```csharp
// BillingTemplateProvisioningTests
[Fact] public void TheTwoDefinitionsCarryTheOwnersTextVerbatim();        // Body de §5, UTILITY, "es", 4 y 3 parámetros posicionales
// BillingMetaWhatsAppSenderTests (IBillingTemplateOutbound grabador)
[Fact] public async Task AReminderFillsCustomerServiceAmountAndDate();   // {{1}}="Ana Pérez", {{2}}="Hosting plan pro", {{3}}="$ 150.000", {{4}}="10/03/2026"
[Fact] public async Task AReminderWithTwoServicesJoinsNamesAndTotalsWithY(); // {{2}}="Hosting plan pro y Dominio", {{3}}="$ 150.000 y USD 20"
[Fact] public async Task AnOverdueMessageFillsCountAndTotal();           // {{2}}="2", {{3}}="$ 450.000"
[Fact] public async Task ConnectionUnavailableAndFailedMapToTheFailureCodes(); // "connection_unavailable:paused", "send_failed:132018"
[Fact] public async Task ListConnectionsMarksReadyOnlyWithBothTemplatesApproved();
// BillingReminderProcessorTests (+)
[Fact] public async Task AWhatsAppProfileWithAReadyConnectionSendsTheTemplateAndNeverAnEmail();
[Fact] public async Task AWhatsAppProfileWithoutAConfiguredConnectionFailsChannelNotConfigured();
[Fact] public async Task AWhatsAppProfileWhoseConnectionLacksTheTemplateFailsTemplateNotApproved();
// BillingSettingsApiTests (+)
[Fact] public async Task GetSettingsListsTheConnectionsWithReadiness();
[Fact] public async Task PutAcceptsAListedConnectionEvenIfNotReadyYet();   // elegirla antes de la aprobación es válido; el envío fallará con template_not_approved hasta entonces
```

- [ ] **Step 2: RED.** **Step 3: Implement** según **Files**. **Step 4: GREEN** — los cuatro filtros.
- [ ] **Step 5: Commit** — `feat(billing): send customer reminders through WhatsApp templates`

---

### Task 13: Clasificación opcional en la importación de Clientes (owner, 2026-10-29)

Coherente con la decisión del 2026-10-19 (`Customer.cs:1135-1138`, migración `20261019130935_AllowCustomerWithoutClassification`,
`customer-form.schema.ts:136-138`): el único lugar que todavía la exige es la importación.

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs:139-142` (borra la regla `classification_required`), `:342-353` (resuelve el nombre sólo si no está vacío; vacío → `classification = null`; nombre que no existe sigue siendo `classification_not_found`), `CandidateRow.Classification` pasa a `ClientClassification?`, `:607-611` y `:651-656` pasan `candidate.Classification?.Id` y `candidate.Classification?.Prefix` (`CucFormatter.Build(null, …)` ya lo admite, `CucFormatter.cs:45-50`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerImportTemplateBuilder.cs` sólo si tiene texto de «obligatorio» para la columna (leerlo; el desplegable se queda)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/ExcelCustomerRowRulesTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// ExcelCustomerRowRulesTests
[Fact] public void AnEmptyClassificationIsValid();                        // ya no hay classification_required
// CustomerStatusAndImportApiTests
[Fact] public async Task ImportingARowWithoutClassificationCreatesTheCustomerWithAnUnprefixedCuc(); // CUC = departamento + consecutivo (8 caracteres); classification null en el GET
[Fact] public async Task ImportingARowWithAnUnknownClassificationStillFails();                     // classification_not_found
```

- [ ] **Step 2: RED.** **Step 3: Implement.** **Step 4: GREEN** — `--filter "FullyQualifiedName~ExcelCustomerRowRulesTests|FullyQualifiedName~CustomerStatusAndImportApiTests"`.
- [ ] **Step 5: Commit** — `feat(customers): make classification optional in the Excel import`

---

### Task 14: Importación de servicios desde Excel (owner, 2026-10-29)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs:29` (`RegisterCustomerData(..., string? Email = null)`) y su implementación (`CreateFromRegisterAsync` pasa el correo al agregado); `tests/Modules/Customers/Modules.Customers.UnitTests/<CustomerProvisioningTests>` (+1 caso: `RegisterWithAnEmailStoresIt`)
- Create: `Modules.Billing.Application/IBillingCustomerProvisioning.cs` (`CreateAsync(Guid tenantId, Guid actorUserId, BillingNewCustomer data, ct) : Task<Guid>`; `BillingNewCustomer(string Name, string? IdentificationType, string? IdentificationNumber, string? Phone, string? Email, string Country)`), `IExcelBillingImporter.cs` (`ReadAsync(Stream, ct) : Task<IReadOnlyList<ExcelBillingRow>>`; `ExcelBillingRow(int RowNumber, string? CustomerName, string? IdentificationType, string? IdentificationNumber, string? Phone, string? Email, string? Country, string? ServiceName, string? Price, string? Currency, string? Cycle, string? DueDate, string? Channel, string? AssigneeEmail)`), `IBillingImportTemplateBuilder.cs`, `BillingImportColumns.cs` (encabezados de §3.2b), `ExcelBillingRowRules.cs` (FluentValidation, códigos de §3.2b), `ExcelBillingRowDeduplicator.cs` (puro: clave = documento normalizado, si no E.164, si no correo en minúsculas; + nombre de servicio normalizado → `duplicate_service`), `BillingImportPlanner.cs` (puro: de filas válidas + clientes emparejados + perfiles + servicios existentes + `today` → `ImportRowPreviewDto`/errores; usa `ChargeGenerator` en seco), `PreviewBillingImport.cs`, `ImportBillingServices.cs`, `GetBillingImportTemplate.cs`, `BillingImportDtos.cs` (§4 «Importación»)
- Create: `Modules.Billing.Infrastructure/Excel/ClosedXmlBillingImporter.cs`, `ClosedXmlBillingImportTemplateBuilder.cs` (copias de los de Customers; desplegables de §3.2b)
- Create: `src/Bootstrapper/BillingCustomerProvisioning.cs` (sobre `ICustomerProvisioning`: con documento `CreateFromRegisterAsync(..., source: "billing")` y, ante `identification_taken`, relee por documento; sin documento `CreateIncompleteAsync`)
- Modify: `IBillingCustomerLookup` + `BillingCustomerLookup.cs` (`FindByIdentificationAsync`, `FindByPhoneE164Async`, `FindByEmailAsync` → `IReadOnlyList<BillingCustomerRef>`), `IBillingMemberDirectory` + `BillingMemberDirectory.cs` (`FindManagerByEmailAsync(tenantId, email) → BillingMemberRef?`), `BillingEndpoints.cs` (`GET /import/template`, `POST /import/preview`, `POST /import` con `DisableAntiforgery()` como `CustomerEndpoints.cs:150`), `QepServiceCollectionExtensions.cs`, `BillingInfrastructureExtensions.cs`
- Test: `Modules.Billing.UnitTests/ExcelBillingRowRulesTests.cs`, `ExcelBillingRowDeduplicatorTests.cs`, `BillingImportPlannerTests.cs`, `Modules.Billing.IntegrationTests/BillingImportApiTests.cs` (archivos armados con ClosedXML en la prueba, como las de Customers)

**Interfaces:**
- Consumes: `ChargeGenerator`, `BillingService.Create`, `ProfileRules`, `IBillingCustomerLookup`, `IBillingMemberDirectory`, `ITenantDefaultCurrency`, `ITenantClock`, `IPhoneNumberNormalizer` (BuildingBlocks o el que use Customers; leerlo).
- Produces: `PreviewBillingImportCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content) : ICommand<ImportPreviewDto>`; `ImportBillingServicesCommand(...) : ICommand<ImportResultDto>`; `GetBillingImportTemplateQuery(Guid TenantId)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// ExcelBillingRowRulesTests: un [Theory] por código de §3.2b con la celda que lo dispara; y
[Fact] public void AValidRowHasNoErrors();
[Fact] public void CycleAcceptsAccentsAndCaseInsensitively();                      // "mensual", "SEMESTRAL", "Anual" válidos
// ExcelBillingRowDeduplicatorTests
[Fact] public void TheSameCustomerAndServiceTwiceFlagsTheSecondRow();              // misma cédula, "Hosting" y " hosting " → duplicate_service en la 2ª
[Fact] public void TheSameCustomerWithTwoServicesIsNotADuplicate();
// BillingImportPlannerTests (today = 2026-03-10)
[Fact] public void ARowWithoutAMatchCreatesTheCustomerAndTheProfile();             // action "create", profileAction "create"
[Fact] public void ARowMatchingACustomerWithAProfileKeepsIt();                     // profileAction "kept" aunque la fila traiga otro encargado
[Fact] public void ARowMatchingTwoCustomersByPhoneIsAmbiguous();                   // Review Focus 6
[Fact] public void APastDueDateCountsTheOverdueCharges();                          // due 2025-12-10 mensual → chargesToGenerate 4, overdueCharges 3
[Fact] public void AnExistingActiveServiceWithTheSameNameIsARowError();            // service_exists
[Fact] public void AnEmptyChannelDefaultsToWhatsAppWithPhoneAndEmailWithout();
// BillingImportApiTests
[Fact] public async Task TheTemplateDownloadsWithTheThirteenHeaders();
[Fact] public async Task PreviewAnswersValidRowsErrorsAndSummaryWithoutWriting();  // 0 filas en billing.services y customers.customers después
[Fact] public async Task ImportCreatesMissingCustomersAsIncompleteAndMatchesExistingOnes(); // customerCreated true/false; el nuevo sin CUC
[Fact] public async Task ImportCreatesTheProfileTheServiceAndItsCharges();          // charges == chargesGenerated, overdue según today
[Fact] public async Task ImportKeepsValidRowsWhenOthersFail();                      // 3 filas, 1 inválida → importedCount 2, errorCount 1
[Fact] public async Task AServiceCreatedBetweenPreviewAndConfirmBecomesARowError(); // Review Focus 7
[Fact] public async Task AnAssigneeWithoutManageIsARowError();                      // assignee_invalid
[Fact] public async Task MoreThan500RowsAnswers422();
[Fact] public async Task WithoutManageAnswers403();
```

- [ ] **Step 2: RED.** **Step 3: Implement** según **Files**. Orden del handler de importación: leer → reglas de fila → deduplicar → emparejar (lookup) → planear → por fila válida: cliente por el puerto (si `action == create`) → perfil + servicio + cobros en una `SaveChangesAsync` de Billing → fila importada; excepción en el segundo paso → `billing.import.row.failed` y sigue. `ChangeTracker.Clear()` por fila. Auditoría `billing.import.completed` al final.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~ExcelBillingRow|FullyQualifiedName~BillingImportPlannerTests|FullyQualifiedName~BillingImportApiTests"`.
- [ ] **Step 5: Commit** — `feat(billing): import recurring services from Excel creating missing customers`

---

### Task 15: Suite completa, README y despliegue

**Files:**
- Modify: `README.md` (sección «Facturación: cobros recurrentes»: módulo, permisos y roles, rutas, generación por período, calendario y agrupación, worker, configuración, plantillas de WhatsApp y su aprobación, importación desde Excel y la clasificación ahora opcional en la importación de Clientes, cómo habilitarlo desde la consola del operador)
- Modify: `docs/superpowers/specs/2026-10-29-cobros-recurrentes.md` si algún nombre cambió al implementar (gana el código)

- [ ] **Step 1:** `Stop-Process -Name Api -ErrorAction SilentlyContinue`; `dotnet build` → 0 warnings nuevos.
- [ ] **Step 2:** `dotnet test tests/ArchitectureTests` → PASS. `dotnet test --filter "FullyQualifiedName~UnitTests"` → PASS.
- [ ] **Step 3:** `$env:Seed__Enabled = "false"`; `dotnet test` (completa, decenas de minutos con Docker) → PASS; anota en el ledger las tres fallas preexistentes conocidas si aparecen (memoria «Seed de user-secrets se cuela en tests»).
- [ ] **Step 4:** Levanta `dotnet run --project src/Api` contra la base local y prueba a mano con `curl.exe` (cuerpos JSON desde archivo, `-d "@body.json"`): crear perfil + servicio con fecha pasada (varios cobros), pagar uno, ver la lista con el resumen. Dejar la evidencia literal en el ledger.
- [ ] **Step 5: Commit** — `docs(billing): document the recurring billing feature`

---

## Self-review (hecho al escribir; revisado tras las respuestas del owner del 2026-10-29)

- **Cobertura del spec:** §1 → Tasks 1, 3; §2 → Task 4; §3.1 → Tasks 5, 6; §3.2 → Tasks 2, 7, 10; §3.2b → Task 14; §3.3-3.4 → Tasks 9, 10, 12; §4 → Tasks 6-8, 11, 12, 14; §5 → Tasks 11, 12, 13, 14; §6 → Task 10; §7 → Task 15.
- **Review Focus 6 y 7** (importación) tienen prueba en Task 14.
- **Tipos consistentes:** `BillingSendResult`, `BillingCustomerMessage`, `CurrencyTotals.Of`, `ChargeGenerator.Generate`, `CustomerMessagePlanner.Plan`, `ServiceDetailComposer`, `ChargeListComposer`, `ProfileRules`, `ReminderCalendar.StepDueOn`, `BillingPermissions.Read/Manage` se nombran igual en todas las tareas. Ya no existe `NextDueDateAfter` ni `FindCurrentAsync`: la generación es del worker.
- **Review Focus:** los cinco tienen prueba nombrada en su tarea (2, 10, 10, 7, 10).
