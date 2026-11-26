# Fecha del pedido editable (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Workspace rule (`qep/CLAUDE.md`, presupuesto de pruebas):** tasks run in the 2 blocks marked below, one implementer per block, one review per block; each task keeps its own commit.

**Goal:** Add `Order.OrderDate` (`DateOnly`), editable through the order save-all, migrated from `converted_at`, and make list, export, report and PDF read it.

**Architecture:** Domain gets the field, a required `orderDate` on every factory and `ChangeOrderDate`. Application resolves the tenant-local day (`DocumentDates.LocalDate`) at creation and "today" (`TenantCalendar.Today`) on edit, writes the history entry, and exposes the field in the DTOs. Infrastructure maps `order_date`, backfills it in one migration, and every read that meant "the order's date" switches to it.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-16-order-date-design.md`

## Global Constraints

- `OrderDate` is `DateOnly`; JSON `"yyyy-MM-dd"`. `ConvertedAt` and `CreatedAt` never change.
- Every new record parameter / DTO field goes **last** (older clients and positional constructors keep working).
- Request field `OrderDate` absent or `null` = keep the stored one.
- Error code, verbatim: `order.order_date.in_future` (422, `QuotationsDomainException`).
- History text, verbatim: `Cambió la fecha del pedido de {old:yyyy-MM-dd} a {new:yyyy-MM-dd}.` (invariant culture).
- `OrderHistoryEventType.OrderDateChanged` appended at the **end** of the enum.
- Backfill fallback time zone: `America/Bogota`.
- Withholdings / UVT keep reading `CreatedAt` (spec §8 P-1). The Shopify sync window (`CountBySourceSinceAsync`) keeps `ConvertedAt`.
- Query params `convertedFrom` / `convertedTo` keep their names.
- Integration tests: filter by test name while iterating; never two Testcontainers runs at once (`Get-Process testhost, dotnet` first). Full Quotations + Reporting integration projects only if the owner asks (memory: `pruebas-solo-cuando-lo-pida`).

## Review Focus

- Order created at 21:00 Bogotá (02:00 UTC next day) → `OrderDate` is the Bogotá day, not the UTC one. Test in Task 2.
- Save-all without `orderDate` (old frontend) → date untouched, no history entry. Test in Task 3.
- Same date sent again → no history entry, no version bump. Test in Task 1 and Task 3.
- `orderDate` equal to tenant "today" while UTC is already tomorrow → accepted. Test in Task 1 (`today` boundary).
- Report month series: an order whose `OrderDate` moved to the previous month counts there; POS sales unchanged. Test in Task 4.

---

## Block A — domain, persistence, creation

### Task 1: Domain field, factories and `ChangeOrderDate`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`Build` ~613, factories 390/459/487/509/565/598, new method next to `UpdateNotes` ~1661)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderHistoryEventType.cs`
- Modify (creation paths): `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:145`, `OrderDraftBuilder.cs:60,86`, `IExternalOrderWriter.cs:288`, `IStoreOrderGateway.cs:129,205`
- Modify (mechanical, compiler-driven): every factory call under `tests/` (~73).
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`

**Interfaces:**
- Produces: `public DateOnly OrderDate { get; private set; }`; every public factory gains `DateOnly orderDate` **immediately after** its `DateTimeOffset occurredAt` parameter; `Build` gains `DateOnly orderDate` after `occurredAt` and sets `OrderDate = orderDate`.
- Produces: `public bool ChangeOrderDate(DateOnly date, DateOnly today, DateTimeOffset occurredAt)`.
- Produces: `OrderHistoryEventType.OrderDateChanged` (last member).

- [ ] **Step 1: Write the failing tests in `OrderTests.cs`**

```csharp
[Fact] public void A_new_order_keeps_the_order_date_it_was_given()
    // NewOrder(...) built with orderDate: new DateOnly(2026, 11, 13) → order.OrderDate == 2026-11-13
[Fact] public void Changing_the_order_date_moves_it_and_bumps_the_version()
    // ChangeOrderDate(2026-11-10, today: 2026-11-16, now) → true; OrderDate == 2026-11-10; Version == before + 1; UpdatedAt == now
[Fact] public void The_same_order_date_is_not_a_change()
    // ChangeOrderDate(order.OrderDate, today, now) → false; Version unchanged
[Fact] public void Today_is_an_accepted_order_date()
    // ChangeOrderDate(today, today, now) → true
[Fact] public void A_future_order_date_is_rejected()
    // ChangeOrderDate(today.AddDays(1), today, now) → QuotationsDomainException, Code == "order.order_date.in_future"
[Theory] // prepared, invoiced, cancelled
public void A_locked_order_cannot_change_its_date(string state)
    // → same exception EnsureEditable throws for UpdateNotes on that state
```

- [ ] **Step 2: Build the unit test project to see RED**

Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`
Expected: compile errors — `OrderDate` / `ChangeOrderDate` not defined. (Build output is the RED evidence; no containers.)

- [ ] **Step 3: Implement** the field, the factory parameter, `Build` assignment, `ChangeOrderDate` (`EnsureEditable("The order date can only be edited on an editable order.")`, then future check, then equality, then write — same shape as `UpdateNotes`) and the enum member with a `<summary>` citing the spec.
  - Creation paths pass the tenant-local day: `ConvertQuotationToOrder` and `OrderDraftBuilder` already hold a calendar → `DocumentDates.LocalDate(calendar, now)`. `IExternalOrderWriter.cs:271` already computes `DocumentDates.LocalDate(calendar, draft.OccurredAt)` — reuse it. `IStoreOrderGateway`: line 183 has `calendar` → `calendar.Today`; at line 125 keep the calendar instead of `.UtcNow` and pass `calendar.Today` to `QuoteForStore`.
  - Test call sites: pass the date the test means (`new DateOnly(...)`), or `DateOnly.FromDateTime(occurredAt.UtcDateTime)` where the date is irrelevant.

- [ ] **Step 4: Run** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests"` — Expected: PASS.

- [ ] **Step 5: Commit** `feat(orders): add editable order date to the order aggregate`

### Task 2: Persistence, migration with backfill, creation paths

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (~731 order mapping, ~797 indexes)
- Create: migration `AddOrderDate` under `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/` (generate with `dotnet ef migrations add AddOrderDate`, then edit `Up`)
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:~383` (`OrdersSql` insert)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderConvertApiTests.cs`, `ManualOrderApiTests.cs`

**Interfaces:**
- Consumes: Task 1 factory signatures.
- Produces: column `quotations.orders.order_date date not null`; index `(tenant_id, order_date, converted_at, order_number)`.

- [ ] **Step 1: Write failing integration tests**

```csharp
// OrderConvertApiTests
[Fact] Converting_a_quotation_dates_the_order_on_the_tenant_day()
    // tenant time zone America/Bogota, clock at 2026-11-14T02:00:00Z → GET order: orderDate == "2026-11-13"
// ManualOrderApiTests
[Fact] A_manual_order_is_dated_today_in_the_tenant_calendar()
    // orderDate == tenant calendar Today
```
(Use the fake clock the class already uses; if it has none, assert `orderDate == DocumentDates.LocalDate(calendar, convertedAt)` read back from the response.)

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests` fails until the DTO has `OrderDate` (Task 3 adds it). Run this block's integration tests at the end of Task 3 instead; the RED evidence here is the build error.

- [ ] **Step 3: Implement**
  - EF: `order.Property(v => v.OrderDate).HasColumnName("order_date");` and `order.HasIndex(v => new { v.TenantId, v.OrderDate, v.ConvertedAt, v.OrderNumber });` (keep the old index).
  - Migration `Up`, in this order: `AddColumn<DateOnly>("order_date", nullable: true)`; `migrationBuilder.Sql(...)` with the spec §6 `UPDATE … FROM tenancy.tenants …` verbatim; `AlterColumn` to `nullable: false`; `CreateIndex`. `Down` drops index and column.
  - `ExportLoadSeeder.OrdersSql`: add `order_date` to the column list and `(numbered.converted_at AT TIME ZONE 'America/Bogota')::date` to the select (load-test seed only).

- [ ] **Step 4: Verify the migration compiles and the model is in sync**: `dotnet ef migrations has-pending-model-changes` for the Quotations context — Expected: no pending changes.

- [ ] **Step 5: Commit** `feat(orders): persist and backfill the order date`

**End of Block A:** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests` — PASS. Review Block A.

---

## Block B — contract and reads

### Task 3: Save-all, preview, DTOs, history, source quotation date

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` (`SaveOrderEditsRequest` :194, `OrderDto` :23, `OrderResponse` :277, `OrderDetailDto` :110, `SourceQuotationResponse` :380)
- Modify: `OrderEdits.cs` (`IOrderEdits`), `SaveOrderEdits.cs` (command :14, apply ~172, history ~338), `PreviewOrderEdits.cs` (query, apply ~98, detail :140-145), `GetOrderById.cs:43-47`, `OrderMapping.cs` (~47, ~107), `OrderResponseComposer.cs` (~81, ~156), `ListOrders.cs:34` (`OrderListItemDto`), `OrderChangeSummary.cs`, `Modules.Quotations.Api/OrderEndpoints.cs` (~446-462, ~505-521)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`, `OrderApiTests.cs`; unit `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderResponseComposerTests.cs`

**Interfaces:**
- Consumes: `Order.ChangeOrderDate`, `Order.OrderDate`, `OrderHistoryEventType.OrderDateChanged`.
- Produces: `SaveOrderEditsRequest(..., DateOnly? OrderDate = null)`; `SaveOrderEditsCommand` / `PreviewOrderEditsQuery` `DateOnly? OrderDate = null` (last); `IOrderEdits.DateOnly? OrderDate { get; }`; `OrderDto.OrderDate`, `OrderResponse.OrderDate`, `OrderListItemDto.OrderDate` (`DateOnly`, last); `OrderDetailDto(..., DateTimeOffset? QuotationCreatedAt = null)`; `SourceQuotationResponse(Guid Id, string QuotationNumber, DateTimeOffset CreatedAt)`; `OrderChangeSummary.OrderDateChanged(DateOnly from, DateOnly to) : string`.

- [ ] **Step 1: Write failing tests**

```csharp
// OrderEditsApiTests
[Fact] Saving_an_order_date_changes_it_and_records_history()
    // PUT {orderDate:"2026-11-10"} → 200; detail.order.orderDate == "2026-11-10";
    // history has eventType "OrderDateChanged", details == "Cambió la fecha del pedido de {old} a 2026-11-10."
[Fact] Saving_without_order_date_keeps_it()            // no field → unchanged, no OrderDateChanged entry
[Fact] Saving_the_same_order_date_records_nothing()     // same value → no entry, version unchanged by this field
[Fact] A_future_order_date_is_422()                     // tomorrow (tenant) → 422, code "order.order_date.in_future"
[Fact] Previewing_an_order_date_does_not_persist_it()   // POST preview shows new date; GET shows old
[Fact] A_prepared_order_cannot_change_its_date()        // existing not-editable 422 code
// OrderApiTests
[Fact] The_detail_carries_the_source_quotation_creation_instant() // sourceQuotation.createdAt == quotation.createdAt
// OrderResponseComposerTests (unit)
[Fact] A_manual_order_has_no_source_quotation()          // sourceQuotation == null
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests` → compile errors on `OrderDate` / `CreatedAt`.

- [ ] **Step 3: Implement**
  - Endpoint maps `request.OrderDate` into command and query (both handlers).
  - `SaveOrderEditsHandler`: after `UpdateNotes` (~172), `var previousDate = order.OrderDate; var dateChanged = command.OrderDate is { } date && order.ChangeOrderDate(date, calendar.Today, now);` (get `calendar` from the injected `tenantClock`). Next to the `notesChanged` history block (~338) add the `OrderDateChanged` entry with `OrderChangeSummary.OrderDateChanged(previousDate, order.OrderDate)`.
  - `PreviewOrderEditsHandler`: same `ChangeOrderDate` call after `UpdateNotes` (~98); no history.
  - `GetOrderById.LoadAsync` and preview: keep the `FindUntrackedAsync` result, pass `quotation?.CreatedAt` as `QuotationCreatedAt`.
  - Composer: build `SourceQuotationResponse` only when number **and** `QuotationCreatedAt` are present.

- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests.Saving_an_order_date|FullyQualifiedName~OrderEditsApiTests.Saving_without_order_date|FullyQualifiedName~OrderEditsApiTests.Saving_the_same_order_date|FullyQualifiedName~OrderEditsApiTests.A_future_order_date|FullyQualifiedName~OrderEditsApiTests.Previewing_an_order_date|FullyQualifiedName~OrderEditsApiTests.A_prepared_order_cannot|FullyQualifiedName~OrderApiTests.The_detail_carries|FullyQualifiedName~Converting_a_quotation_dates|FullyQualifiedName~A_manual_order_is_dated" -- xUnit.MaxParallelThreads=4` — Expected: PASS (includes Task 2's tests).

- [ ] **Step 5: Commit** `feat(orders): edit the order date in save-all and expose creation instants`

### Task 4: List, export, report and PDF read `OrderDate`

**Files:**
- Modify: `Modules.Quotations.Application/IOrderRepository.cs` (`OrderExportCursor` :7; `SearchAsync` :60, `AnyForExportAsync` :103, `ListForExportAsync` :118 — `DateTimeOffset? convertedFrom/convertedBefore` → `DateOnly? orderDateFrom, DateOnly? orderDateTo`)
- Modify: `Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs` (filters ~290-297, order ~122/~231, cursor ~223-226)
- Modify: `ListOrders.cs:92-98`, `ExportOrders.cs:98`, `OrdersExportProcessor.cs:57, 95, 264`
- Modify: `QuotationPdfDocumentMapper.cs:91`
- Modify: `src/Bootstrapper/OrdersReportSource.cs` (rows ~66/72, months ~205-206, filters ~410-421, branches ~477/~488/~499); `src/Modules/Reporting/Modules.Reporting.Application/ReportingFilters.cs:14-21` (`ReportPeriod`)
- Test: `OrderListApiTests.cs`, `OrderExportApiTests.cs`, `OrderPdfExportApiTests.cs` (or the mapper's unit tests if they exist), `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs`, `OrdersReportPosApiTests.cs`

**Interfaces:**
- Consumes: `Order.OrderDate`, save-all `orderDate` (Task 3) to move a date in tests.
- Produces: `OrderExportCursor(DateOnly OrderDate, DateTimeOffset ConvertedAt, string OrderNumber)`; `ReportPeriod(DateTimeOffset? Start, DateTimeOffset? EndExclusive, TimeZoneInfo TimeZone, DateOnly? From = null, DateOnly? To = null)` with `Of` filling `From`/`To`.

- [ ] **Step 1: Write failing tests**

```csharp
// OrderListApiTests
[Fact] The_date_filter_uses_the_order_date()   // move order A to 2026-10-31 via PUT; ?convertedFrom=2026-10-01&convertedTo=2026-10-31 → A only
[Fact] The_list_is_sorted_by_order_date()       // A moved back sorts after B created later
// OrderExportApiTests
[Fact] The_export_date_column_is_the_order_date() // moved order → its row's date cell == "2026-10-31"; filter by month includes it
// OrderPdfExportApiTests (or mapper unit test)
[Fact] The_order_pdf_prints_the_order_date()    // FromOrder(...).Date == order.OrderDate
// OrdersReportSummaryApiTests
[Fact] A_moved_order_counts_in_its_order_date_month() // order moved Nov→Oct counts in October's month row and in an Oct period
// OrdersReportPosApiTests
[Fact] Pos_sales_keep_their_local_day()          // existing expectations unchanged (run the class's current tests as the guard)
```

- [ ] **Step 2: RED** — run the new tests filtered by name (needs Task 3 compiled): Expected FAIL (still cut by `converted_at`).

- [ ] **Step 3: Implement**
  - Repository: `orders.Where(o => o.OrderDate >= from)` / `<= to`; order `OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.ConvertedAt).ThenBy(o => o.OrderNumber)` (keep the existing secondary order on `OrderNumber`, matching today's direction); cursor compares the triple.
  - `ListOrders` / `ExportOrders` / processor pass `query.ConvertedFrom` / `ConvertedTo` straight through; drop the `TenantDayRange` for orders (keep the calendar where the processor needs it for other cells). `query.TimeZone` stays accepted and unused for the order filter — leave a comment why.
  - Export cell (~264): `order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`.
  - PDF mapper: `header.OrderDate` (`OrderResponse.OrderDate`).
  - Report: add `occurred_on` to the three branches (`o.order_date`, `(s.created_at AT TIME ZONE @time_zone)::date`, `NULL::date`); filters become `occurred_on >= @period_from` / `<= @period_to` (`NpgsqlDbType.Date`) on both branches, built from `ReportPeriod.From/To`; months `extract(year|month FROM r.occurred_on)`; rows `ORDER BY r.occurred_on DESC, r.occurred_at DESC, r.number, r.id`. `time_zone` becomes a parameter of every query that renders the CTE.

- [ ] **Step 4: GREEN** — same filtered command per class (`OrderListApiTests`, `OrderExportApiTests`, `OrderPdfExportApiTests`, then the two Reporting classes in `Modules.Reporting.IntegrationTests`), one run at a time. Then `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests` and `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`. Expected: PASS.

- [ ] **Step 5: Commit** `feat(orders): filter, sort, export and report by order date`

**End of Block B:** run `OrderEditsApiTests`, `OrderListApiTests`, `OrderExportApiTests` classes once each (`-- xUnit.MaxParallelThreads=4`) and `dotnet test tests/ArchitectureTests`. Review Block B. Final whole-branch review on the most capable model before merging to `develop`.
