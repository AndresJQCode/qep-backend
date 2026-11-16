# Retenciones por UVT — plan backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: tareas agrupadas en bloques, una revisión por bloque.

**Goal:** Exponer en cotizaciones y pedidos el veredicto de cada tasa de retención (aplica o no, motivo, umbral en dinero, UVT con su año y si está desactualizada) y refrescar la UVT de los documentos editables.

**Architecture:** El dominio de Quotations gana un veredicto por tasa (`WithholdingVerdict`) que sale de la misma regla que hoy filtra candidatas, y guarda el año de la UVT en la foto. Los handlers de guardar y previsualizar refrescan la UVT antes del recálculo vía el puerto `IQuotationWithholdingLookup`. Los compositores HTTP agregan `withholdingBreakdown` y calculan `taxUnitOutdated` con la fecha local del documento.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-16-retenciones-por-uvt-design.md`

## Global Constraints

- El PDF no se toca.
- `withholdings`, `customerWithholdingTaxRates`, `billingWithholdingTaxRates` y `municipalWithholdings` no cambian.
- Umbral inclusivo, sin redondear; monto a dos decimales (`DocumentTotals.cs:63-71`).
- Documento editable: cotización `Draft` o `Sent` (`Quotation.EnsureEditable`, `Quotation.cs:1240`); pedido `IsEditable` (`Order.cs:294`: `Open`, sin preparar, sin facturar).
- Año del documento = `DocumentDates.LocalDate(calendar, CreatedAt).Year`, el mismo con el que se congela la foto.
- Quotations no referencia Catalog: todo dato de Catalog entra por `IQuotationWithholdingLookup` (adaptador en `src/Bootstrapper/QuotationWithholdingLookup.cs`).
- TDD: RED antes que GREEN, con la salida literal de ambos. RED por compilación = la salida del build.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~X"`), `-- xUnit.MaxParallelThreads=4`. Nunca dos corridas de integración a la vez (mirar `Get-Process testhost, dotnet` antes). La suite completa no se corre salvo pedido del owner.
- Commits convencionales, sin atribución de IA.

## Review Focus

- Documento con dos tasas, una aplica y otra no: el desglose trae las dos en el orden de la fuente y `withholdings` sólo la aplicada → prueba en Task 3.
- Documento creado el 31 de diciembre a las 20:00 hora de Bogotá (1 de enero en UTC): `taxUnitOutdated` es `false` si la UVT congelada es la de ese año local → prueba en Task 4.
- Pedido no editable (facturado) que pasa por un handler de lectura o una acción que no recalcula: su UVT no cambia → prueba en Task 5.
- Tasa sin base mínima: el refresco no le pone valor ni año → prueba en Task 2.
- Cotización con facturación propia: el desglose sale de la fuente `Billing`, no de la del cliente → prueba en Task 3.

---

## Bloque A — dominio (Tasks 1-3, un implementador)

### Task 1: Año de la UVT en Catalog y en la foto

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/TaxUnitValues.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/WithholdingRate.cs`
- Modify: `src/Bootstrapper/QuotationWithholdingLookup.cs:118-135`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxUnitValueTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationWithholdingLookupTests.cs`

**Interfaces:**
- Produces: `public readonly record struct TaxUnitResolution(decimal Value, int Year)` (Catalog.Domain); `TaxUnitValues.Resolve(IEnumerable<TaxUnitValue> values, string country, int documentYear) -> TaxUnitResolution?`; `ValueFor` queda como `Resolve(...)?.Value`.
- Produces: `WithholdingRate.TaxUnitYear { get; init; }` de tipo `int?` (propiedad init, como `CatalogPercentage`, para no tocar los constructores posicionales).

- [ ] **Step 1: Tests que fallan**
  - `TaxUnitValueTests.ResolveReturnsTheYearUsed`: valores CO 2025=100, 2026=200 → `Resolve(…, "CO", 2026) == new(200m, 2026)`; `Resolve(…, "CO", 2028) == new(200m, 2026)`; `Resolve([], "CO", 2026) == null`.
  - `QuotationWithholdingLookupTests.PhotoFreezesTheTaxUnitYear`: tasa CO con base 4 UVT, UVT cargada sólo 2025, documento 2026-01-10 → `TaxUnitValue == 49799m`, `TaxUnitYear == 2025`. Tasa sin base mínima → `TaxUnitYear == null`.
- [ ] **Step 2: Correr y ver el RED** — `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~TaxUnitValueTests"` y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationWithholdingLookupTests"`. Esperado: error de compilación (`Resolve`, `TaxUnitYear` no existen).
- [ ] **Step 3: Implementar** `Resolve` (misma consulta que `ValueFor`, devolviendo año); `Photo` llama `Resolve` una vez y pone `TaxUnitValue` y `TaxUnitYear`.
- [ ] **Step 4: Correr los dos filtros** → PASS, y los tests existentes de `ValueFor` siguen en verde.
- [ ] **Step 5: Commit** `feat(catalog): resolve the tax unit with its year and freeze it in withholdings`

### Task 2: Persistir el año y refrescar la UVT

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/TaxUnitPhoto.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/DocumentWithholding.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs`, `Order.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:244-276`
- Create: migración `AddWithholdingTaxUnitYear`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/TaxUnitRefreshTests.cs` (NUEVO)

**Interfaces:**
- Produces: `public readonly record struct TaxUnitPhoto(decimal Value, int Year)` (Quotations.Domain).
- Produces: `DocumentWithholding.TaxUnitYear` (`int?`), incluido en `Rate` y copiado en `Replace`.
- Produces: `Quotation.RefreshTaxUnits(IReadOnlyDictionary<string, TaxUnitPhoto> byCountry)` y `Order.RefreshTaxUnits(...)` — clave = país canónico. Llaman a su guard de editable, actualizan las filas **de todas las fuentes** con `MinimumBase` y `MinimumBaseUnit` no nulos (país ausente en el diccionario → valor y año null) y recalculan (`RecalculateTotals`). Una fila sin base mínima o sin unidad no cambia.

- [ ] **Step 1: Tests que fallan** (`TaxUnitRefreshTests`, con `TestWithholdings`):
  - `RefreshUpdatesValueAndYearAndRecalculates`: cotización con Retefuente servicios 4 % base 4 UVT congelada con UVT 2025=49.799 y subtotal 190.000 (no aplica: umbral 199.196); refresco con CO → (47.000, 2026) → umbral 188.000, aplica, `RetentionAmount == 7600m`, `TaxUnitYear == 2026`.
  - `RefreshLeavesARateWithoutMinimumBaseAlone`: ReteICA sin base → valor y año siguen null.
  - `RefreshThrowsOnANonEditableQuotation`: cotización `Converted` → `QuotationsDomainException` `quotation.quotation.not_editable`.
  - `RefreshThrowsOnANonEditableOrder`: pedido facturado → excepción de `EnsureEditable` del pedido.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~TaxUnitRefreshTests"` → error de compilación.
- [ ] **Step 3: Implementar** `TaxUnitPhoto`, `DocumentWithholding.RefreshTaxUnit(TaxUnitPhoto?)` (interno; deja la fila sin liquidar como `Replace`) y los dos `RefreshTaxUnits`. En `ConfigureWithholding`: `withholding.Property(value => value.TaxUnitYear).HasColumnName("tax_unit_year").HasColumnType("smallint");`.
- [ ] **Step 4: GREEN** del mismo filtro.
- [ ] **Step 5: Migración** — `dotnet ef migrations add AddWithholdingTaxUnitYear --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`. Agregar al final de `Up` el relleno (spec §8.2), una sentencia por tabla:

```sql
UPDATE quotations.quotation_withholdings w
SET tax_unit_year = (
  SELECT max(t.year) FROM catalog.tax_unit_values t
  WHERE t.country = w.country AND t.value = w.tax_unit_value
    AND t.year <= extract(year FROM d.created_at))
FROM quotations.quotations d
WHERE d.id = w.document_id AND w.tax_unit_value IS NOT NULL;
```

  (y la misma con `order_withholdings` / `quotations.orders`). `Down` sólo borra la columna.
- [ ] **Step 6: Commit** `feat(quotations): persist the tax unit year and refresh it on editable documents`

### Task 3: El veredicto

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/WithholdingVerdict.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/WithholdingCandidates.cs`, `DocumentTotals.cs:64-71`, `Quotation.cs`, `Order.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WithholdingVerdictTests.cs` (NUEVO)

**Interfaces:**
- Produces: `public enum WithholdingReason { Applied, BelowMinimumBase, NoTaxUnitValue, FinalConsumer, NoBillingCompany, CurrencyWithoutTax, OtherMunicipality }`.
- Produces: `public sealed record WithholdingVerdict(WithholdingRate Rate, WithholdingReason Reason, decimal Base, decimal Amount)` con `bool Applies => Reason == WithholdingReason.Applied` y `decimal? MinimumBaseAmount => Rate.MinimumBase is null ? null : Rate.Threshold`.
- Produces: `WithholdingCandidates.Explain(IEnumerable<WithholdingRate> source, bool billsToFinalConsumer, QuotationBillingAccount? billingAccount, string currency, Guid? operationCityId, decimal productsSubtotal) -> IReadOnlyList<WithholdingVerdict>`. Omite las tasas de otro país (spec §5.1). `For` pasa a ser «las de `Explain` cuyo motivo no es de los pasos 1-4», sin repetir condiciones.
- Produces: `DocumentTotals.ThresholdReason(WithholdingRate rate, decimal productsSubtotal) -> WithholdingReason` (`Applied`, `BelowMinimumBase` o `NoTaxUnitValue`), y `Withhold` lo usa.
- Produces: `Quotation.WithholdingVerdicts` y `Order.WithholdingVerdicts` (`IReadOnlyList<WithholdingVerdict>`): `Explain` sobre las filas de la fuente vigente con `Subtotal`; `Amount` = `row.Amount`.

- [ ] **Step 1: Tests que fallan** (`WithholdingVerdictTests`):
  - Un `[Theory]` por motivo: consumidor final → `FinalConsumer`; sin cuenta → `NoBillingCompany`; cuenta CO con moneda USD → `CurrencyWithoutTax`; ReteICA de Medellín con operación en Bogotá → `OtherMunicipality`; base 4 UVT sin valor → `NoTaxUnitValue`; subtotal 150.000 con umbral 199.196 → `BelowMinimumBase`; subtotal 199.196 (igual al umbral) → `Applied`.
  - `FinalConsumerWinsOverThreshold`: consumidor final y subtotal sobre el umbral → `FinalConsumer`.
  - `OtherCountryRatesAreLeftOut`: fuente con Retefuente CO y una tasa MX → un solo veredicto.
  - `ForEqualsExplainFiltered`: los tres casos de `MunicipalWithholdingsTests` (líneas 79-100) dan la misma salida que hoy.
  - `QuotationVerdictsFollowTheActiveSource`: cotización con facturación propia → los veredictos son de `Billing`.
  - `VerdictsKeepSourceOrder`: dos tasas, la primera no aplica y la segunda sí → orden intacto, `Amount` 0 y monto.
- [ ] **Step 2: RED** — `--filter "FullyQualifiedName~WithholdingVerdictTests"` → compilación.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN**, y además `--filter "FullyQualifiedName~MunicipalWithholdingsTests|FullyQualifiedName~DocumentTotalsTests"` siguen en verde.
- [ ] **Step 5: Commit** `feat(quotations): explain why each withholding applies or not`

Fin del Bloque A: correr el proyecto `Modules.Quotations.UnitTests` entero (sin contenedores) y revisión del bloque.

---

## Bloque B — aplicación, HTTP y refresco (Tasks 4-5, un implementador)

### Task 4: `withholdingBreakdown` en las respuestas

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (DTO y response), `OrdersDtos.cs:274, 383`, `QuotationMapping.cs:51-75`, `OrderMapping.cs:65`, `QuotationResponseComposer.cs`, `OrderResponseComposer.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationWithholdingsApiTests.cs`, `OrderWithholdingsApiTests.cs`

**Interfaces:**
- Consumes: `WithholdingVerdict`, `Quotation.WithholdingVerdicts`, `Order.WithholdingVerdicts` (Task 3); `DocumentWithholding.TaxUnitYear` (Task 2).
- Produces: `WithholdingVerdictDto(Guid TaxRateId, string Name, string Type, string Country, decimal Percentage, decimal CatalogPercentage, bool PercentageOverridden, Guid? CityId, bool Applies, string Reason, decimal Base, decimal? MinimumBase, string? MinimumBaseUnit, decimal? MinimumBaseAmount, decimal? TaxUnitValue, int? TaxUnitYear, decimal Amount)`; `WithholdingBreakdownResponse` con esos campos más `bool TaxUnitOutdated`. Campo `WithholdingBreakdown` al final de `QuotationDto`, `OrderDto`, `QuotationResponse`, `OrderResponse` y `OrderDetailResponse` (y todo otro record que hoy lleve `Withholdings`). JSON: `withholdingBreakdown`, `reason` con el nombre del enum.
- `TaxUnitOutdated = TaxUnitYear is { } year && year < documentYear`, con `documentYear` de `DocumentDates.LocalDate(await tenantClock.GetAsync(tenantId, ct), CreatedAt).Year`. Los dos compositores reciben `ITenantClock` por constructor y leen el calendario sólo si alguna fila trae `TaxUnitYear`.

- [ ] **Step 1: Tests que fallan** (integración):
  - `QuotationWithholdingsApiTests.BreakdownExplainsARateBelowTheMinimumBase`: cotización con Retefuente servicios 4 % base 4 UVT y subtotal bajo el umbral → `withholdingBreakdown[0]` con `applies: false`, `reason: "BelowMinimumBase"`, `minimumBaseAmount` = 4 × UVT, `taxUnitYear` y `withholdings` vacío.
  - `QuotationWithholdingsApiTests.BreakdownFlagsAnOutdatedTaxUnit`: UVT cargada sólo del año anterior al de la cotización → `taxUnitOutdated: true`.
  - `QuotationWithholdingsApiTests.OutdatedUsesTheTenantLocalYear`: cotización creada `2026-01-01T01:00Z` (31-dic-2025 en Bogotá) con UVT 2025 → `taxUnitOutdated: false`.
  - `OrderWithholdingsApiTests.ConvertedOrderCarriesTheBreakdown`: pedido convertido de la cotización anterior → mismo veredicto en `GET` del pedido.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests` → error de compilación (sin contenedores).
- [ ] **Step 3: Implementar** mapeo y compositores.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~BreakdownExplainsARateBelowTheMinimumBase|FullyQualifiedName~BreakdownFlagsAnOutdatedTaxUnit|FullyQualifiedName~OutdatedUsesTheTenantLocalYear|FullyQualifiedName~ConvertedOrderCarriesTheBreakdown" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Commit** `feat(quotations): add the withholding breakdown to quotation and order responses`

### Task 5: Refrescar la UVT al guardar y previsualizar

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationWithholdingLookup.cs`, `src/Bootstrapper/QuotationWithholdingLookup.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/TaxUnitRefresh.cs`
- Modify: `QuotationEditsApplication.cs:43` (lo usan `SaveQuotation` y `PreviewQuotation`), `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`
- Test: `QuotationWithholdingsApiTests.cs`, `OrderWithholdingsApiTests.cs`, `QuotationWithholdingLookupTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyDictionary<string, TaxUnitPhoto>> IQuotationWithholdingLookup.TaxUnitsAsync(IReadOnlyCollection<string> countries, DateOnly documentDate, CancellationToken)`; el adaptador lee `ITaxUnitValueRepository.ListAsync(null, …)` una vez y resuelve con `TaxUnitValues.Resolve`. País sin valor → no está en el diccionario.
- Produces: `internal static class TaxUnitRefresh` con `RefreshAsync(Quotation, IQuotationWithholdingLookup, DateOnly documentDate, CancellationToken)` y la sobrecarga para `Order`: junta los países de las filas con base en unidades; sin ninguno no consulta nada; si no, llama `TaxUnitsAsync` y `RefreshTaxUnits`.
- Se llama **al final** de `QuotationEditsApplication.ApplyAsync` (con `withholdings.DocumentDate`) y en `SaveOrderEditsHandler` / `PreviewOrderEditsHandler` después de aplicar las ediciones, sólo si el documento es editable (los handlers ya fallan antes si no lo es).
- El refresco no escribe historial propio (spec §5.4).

- [ ] **Step 1: Tests que fallan**
  - `QuotationWithholdingLookupTests.TaxUnitsAsyncResolvesPerCountry`: CO 2025 y 2026 cargadas, documento 2026 → `{ CO: (v2026, 2026) }`; país sin valores → ausente.
  - `QuotationWithholdingsApiTests.SavingRefreshesTheTaxUnit`: cotización creada con sólo UVT 2025; se carga 2026 por la consola del operador; `PUT` save-all sin cambios → `taxUnitYear: 2026`, `taxUnitOutdated: false`, totales recalculados.
  - `QuotationWithholdingsApiTests.PreviewRefreshesWithoutSaving`: igual con la vista previa; un `GET` posterior sigue con 2025.
  - `OrderWithholdingsApiTests.SavingAnEditableOrderRefreshesTheTaxUnit` y `InvoicedOrderKeepsItsTaxUnit` (facturado: `GET` después de cargar 2026 sigue con 2025).
- [ ] **Step 2: RED** por compilación (`TaxUnitsAsync` no existe).
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** con el filtro de los cinco nombres; después la clase `QuotationWithholdingsApiTests` y `OrderWithholdingsApiTests` enteras, una a la vez.
- [ ] **Step 5: Commit** `feat(quotations): refresh the tax unit of editable documents on save and preview`

Fin del Bloque B: unitarias de Quotations, Catalog y Bootstrapper; `ArchitectureTests` (Quotations sigue sin referenciar Catalog); revisión del bloque. Antes de mergear a `develop`: suite de `Modules.Quotations.IntegrationTests` y `Modules.Catalog.*` **sólo si el owner la pide**.

## Merge

Merge a `develop` (nunca `main`). Actualizar `docs/estado.md` en `develop` al mergear. Desplegar antes que el frontend.
