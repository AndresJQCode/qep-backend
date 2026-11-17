# Alerta de UVT al convertir — plan backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **El presupuesto de pruebas del `CLAUDE.md` del workspace manda sobre el skill**: 2 bloques, un implementador por bloque, una revisión por bloque.

**Goal:** El pedido convertido nace con la UVT de su año, `GET …/order/preview` muestra qué cambia antes de convertir, y cada fila del desglose trae `taxUnitLatestYear`.

**Architecture:** `Order.FromQuotation` recibe opcionalmente las UVT vigentes y refresca antes de calcular totales y estado de pago. La conversión y la vista previa leen esas UVT con la misma función (`TaxUnitRefresh.UnitsForAsync`); la vista previa arma dos pedidos en memoria (sin y con UVT) y los compara. Los compositores calculan `taxUnitLatestYear` junto con el año del documento.

**Tech Stack:** .NET 10, xUnit, Testcontainers (integración), minimal APIs.

**Spec:** `docs/superpowers/specs/2026-11-16-alerta-uvt-conversion-design.md` (y el previo `2026-11-16-retenciones-por-uvt-design.md`).

Worktree: `qep-backend-worktrees/alerta-uvt-conversion`, rama `feature/alerta-uvt-conversion`. Nunca `main`; el merge va a `develop`.

## Global Constraints

- TDD: RED antes que GREEN, con la salida literal de los dos. Si el RED es de compilación, la evidencia es el build; no se levantan contenedores.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~…"`) mientras se itera; la clase `OrderWithholdingsApiTests` completa, una vez al final de cada bloque. La suite completa sólo si el owner la pide.
- Antes de una corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra corriendo, esperar. Nunca matar procesos ajenos. Si `Api.exe` bloquea el build, avisar.
- Ningún código de error nuevo. La vista previa reusa 404 `QuotationNotFound` y los 422 de `EnsureConvertibleToOrder`.
- El refresco no escribe historial propio (spec UVT §5.4).
- Comentarios en español explicando por qué, citando el spec (`spec alerta UVT §x`). Conventional commits sin atribución de IA.
- Sin migración.

## Review Focus

1. Pedido con contraentrega convertido con UVT nueva: `EnsureCashOnDeliveryIsPending` y el estado de pago deben seguir igual (pendiente) tras el refresco — Task 1, prueba `FromQuotationRefreshKeepsCashOnDeliveryPending`.
2. Comprobantes que cubrían el neto viejo: el estado de pago del pedido nace calculado contra el neto **nuevo** (parcial si no alcanza) — Task 1, prueba `FromQuotationComputesPaymentStatusAgainstTheRefreshedNet`.
3. Cotización sin filas con base en UVT: ni la conversión ni la vista previa ni los compositores consultan Catalog — Task 2 (`UnitsForAsync` sin filas devuelve null sin llamar al lookup) y Task 3 (sin `MinimumBaseUnit` no se llama `TaxUnitsAsync`).
4. Fila legada con `TaxUnitYear` null y UVT cargada: la vista previa la cuenta como cambio (`FromYear` null) — Task 4, prueba `CompareReportsALegacyRowWithoutYear`.
5. La vista previa no gasta consecutivo — Task 4, integración `ConversionPreviewDoesNotSpendTheOrderNumber`.

---

## Bloque A — el pedido nace con la UVT de su año

### Task 1: `Order.FromQuotation` refresca la UVT

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`FromQuotation`, ~390-456)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/TaxUnitRefreshTests.cs`

**Interfaces:**
- Produces: `Order.FromQuotation(OrderId id, Guid tenantId, string orderNumber, Quotation quotation, string? notes, MemberId convertedBy, IReadOnlyCollection<OrderPaymentProofInput> proofs, DateTimeOffset occurredAt, IReadOnlyDictionary<string, TaxUnitPhoto>? taxUnits = null)`. Con `null` se comporta como hoy.

- [ ] **Step 1: Pruebas que fallan**, en `TaxUnitRefreshTests` (reusar `NewQuotation()`, `RetefuenteServicios` con UVT 2025 a 49.799 y `Uvt2026` a 47.000; elegir los valores para que el veredicto cambie, como en las pruebas vecinas):
  - `FromQuotationWithTaxUnitsRefreshesBeforeTotals`: cotización con foto 2025 → `Order.FromQuotation(..., taxUnits: Uvt2026)` → la fila tiene `TaxUnitYear == 2026`, `TaxUnitValue == 47_000m`, y `RetentionAmount`/`NetTotal` son los recalculados (afirmar los valores literales).
  - `FromQuotationWithoutTaxUnitsKeepsThePhoto`: `taxUnits: null` → `TaxUnitYear == 2025`, totales iguales a los de la cotización.
  - `FromQuotationComputesPaymentStatusAgainstTheRefreshedNet`: un comprobante por exactamente el `NetTotal` de la cotización; con un `Uvt` que hace dejar de aplicar la retención (el neto sube) → `PaymentStatus == PartialPaymentReceived`.
  - `FromQuotationRefreshKeepsCashOnDeliveryPending`: cotización con envío contraentrega, sin comprobantes, `taxUnits: Uvt2026` → `PaymentStatus == PaymentPending`, sin excepción.

- [ ] **Step 2: RED.** `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → falla por el parámetro inexistente. Guardar la salida.

- [ ] **Step 3: Implementar.** En `FromQuotation`, después de copiar las líneas y **antes** de `order.RecalculateTotals()`: si `taxUnits` no es null, `DocumentWithholding.RefreshTaxUnits(order._withholdings, taxUnits)`. No llamar `order.RefreshTaxUnits` (exige `IsEditable` y recalcula de nuevo). Comentario: spec alerta UVT §5.2.

- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~TaxUnitRefreshTests"` → todas pasan.

- [ ] **Step 5: Commit** `feat(orders): refresh the tax unit when building an order from a quotation`.

### Task 2: la conversión lee las UVT del día y las pasa

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/TaxUnitRefresh.cs` (extraer `UnitsAsync` a `internal`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/TaxUnitRefreshTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderWithholdingsApiTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `internal static Task<IReadOnlyDictionary<string, TaxUnitPhoto>?> TaxUnitRefresh.UnitsForAsync(IEnumerable<DocumentWithholding> rows, IQuotationWithholdingLookup lookup, DateOnly documentDate, CancellationToken cancellationToken)` — null si ninguna fila tiene `MinimumBase` y `MinimumBaseUnit`, sin llamar al lookup. `UnitsAsync` privado pasa a delegar en ella.

- [ ] **Step 1: Pruebas que fallan.**
  - Unitaria `UnitsForWithoutUnitRowsDoesNotQueryTheLookup`: filas sin unidad (ReteICA) → `null` y el lookup falso no registra llamadas (usar el falso de lookup que ya usen las pruebas vecinas; si no hay, uno mínimo en el archivo).
  - Integración `ConvertingRefreshesTheTaxUnitToTheOrderYear` en `OrderWithholdingsApiTests`, calcada de `SavingAnEditableOrderRefreshesTheTaxUnit`: `SetUpAsync(factory, taxUnitYear: thisYear - 1)`, retefuente con `minimumBase: 2m`, cotización enviada (aplica: 4.000), `InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m)`, `ConvertAsync` →
    ```csharp
    Assert.Equal(
        ((int?)thisYear, (decimal?)60_000m, false, "BelowMinimumBase", (decimal?)120_000m),
        (entry.TaxUnitYear, entry.TaxUnitValue, entry.TaxUnitOutdated, entry.Reason, entry.MinimumBaseAmount));
    Assert.Equal((0m, order.Total), (order.RetentionAmount, order.NetTotal));
    ```

- [ ] **Step 2: RED.** Unitaria: build falla (`UnitsForAsync` no existe). Integración (sólo después del GREEN de la unitaria, para no levantar contenedores por un error de compilación): `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ConvertingRefreshesTheTaxUnitToTheOrderYear"` → falla con `TaxUnitYear` = `thisYear - 1`.

- [ ] **Step 3: Implementar.** `ConvertQuotationToOrderHandler` recibe `IQuotationWithholdingLookup withholdingLookup`. Después de `EnsureConvertibleToOrder` y del requisito de tipo de persona, **antes** de abrir la transacción: `var taxUnits = await TaxUnitRefresh.UnitsForAsync(quotation.ActiveWithholdings, withholdingLookup, DocumentDates.LocalDate(calendar, now), cancellationToken);` y pasarlo a `Order.FromQuotation(..., taxUnits)`. Comentario: lectura a Catalog fuera del lock del contador, como las demás.

- [ ] **Step 4: GREEN.** Las dos pruebas pasan. Después, una vez, la clase: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderWithholdingsApiTests" -- xUnit.MaxParallelThreads=4` y `--filter "FullyQualifiedName~OrderConvertApiTests"`. Todo verde (en particular `ConvertedOrderCarriesTheBreakdown` y `SavingAnEditableOrderRefreshesTheTaxUnit`).

- [ ] **Step 5: Commit** `feat(orders): convert with the tax unit of the order's year`.

**Fin del Bloque A: revisión del bloque.**

---

## Bloque B — `taxUnitLatestYear` y la vista previa

### Task 3: `taxUnitLatestYear` en el desglose

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (`WithholdingBreakdownResponse`: agregar `int? TaxUnitLatestYear` después de `TaxUnitOutdated`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs` (`DocumentYearAsync` → `TaxUnitContextAsync`, `ToBreakdownResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderResponseComposer.cs` (`ComposeOrderAsync`, `ToResponse(OrderDto, …)` línea ~116, y la llamada de la línea ~103)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderResponseComposerTests.cs`, `OrderWithholdingsApiTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record TaxUnitContext(int? DocumentYear, IReadOnlyDictionary<string, int> LatestYearByCountry) { public static TaxUnitContext None { get; } }` (en `QuotationResponseComposer.cs`).
  - `internal static Task<TaxUnitContext> QuotationResponseComposer.TaxUnitContextAsync(ITenantClock tenantClock, IQuotationWithholdingLookup withholdingLookup, Guid tenantId, DateTimeOffset createdAt, IReadOnlyList<WithholdingVerdictDto>? breakdown, CancellationToken cancellationToken)` — lee el calendario si alguna fila trae `TaxUnitYear` o `MinimumBaseUnit`; llama `TaxUnitsAsync` (una vez, países distintos de las filas con `MinimumBaseUnit`) con la fecha local de `createdAt` sólo si alguna fila trae `MinimumBaseUnit`.
  - `ToBreakdownResponse(IReadOnlyList<WithholdingVerdictDto>? breakdown, TaxUnitContext context)`; `TaxUnitLatestYear` = `context.LatestYearByCountry[verdict.Country]` si la fila tiene `MinimumBaseUnit` y el país está; si no, null.
  - `OrderResponseComposer.ToResponse(OrderDto order, TaxUnitContext context)`.

- [ ] **Step 1: Pruebas que fallan** (unitarias, en `OrderResponseComposerTests` o un archivo nuevo `TaxUnitContextTests.cs` si el falso de lookup no encaja):
  - `LatestYearIsTheLoadedYearForTheCountry`: fila CO con UVT 2025, lookup devuelve `CO → (60_000, 2026)` → `TaxUnitLatestYear == 2026`, `TaxUnitOutdated` según el año del documento.
  - `LatestYearIsNullWithoutAnyLoadedValue`: lookup devuelve diccionario vacío → null.
  - `LatestYearIsNullForARowWithoutUnit` y `NoUnitRowsDoNotQueryTheLookup`.

- [ ] **Step 2: RED.** Build falla (`TaxUnitContext` no existe). Guardar salida.

- [ ] **Step 3: Implementar** con las firmas de arriba. `QuotationResponseComposer` y `OrderResponseComposer` ya reciben `IQuotationWithholdingLookup`. Reemplazar las dos llamadas a `DocumentYearAsync` (borrarla si queda sin uso).

- [ ] **Step 4: GREEN unitario**, luego integración `DetailCarriesTheLatestTaxUnitYear` (nueva, en `OrderWithholdingsApiTests`): `SetUpAsync(factory, taxUnitYear: thisYear - 1)`, convertir, `InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m)`, `GetDetailAsync` → `(TaxUnitYear, TaxUnitOutdated, TaxUnitLatestYear) == (thisYear - 1, true, thisYear)`. RED primero (corre después de que compile: falla por null), luego GREEN.

- [ ] **Step 5: Commit** `feat(quotations): expose the latest loaded tax unit year per withholding`.

### Task 4: `GET …/order/preview`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/PreviewQuotationConversion.cs` (query, handler, DTOs, `Compare`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderResponseComposer.cs` + su interfaz `IOrderResponseComposer` (método de composición de la vista previa)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (grupo `/quotations/{quotationId}/order`, ~184)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationConversionPreviewTests.cs` (nuevo), `OrderWithholdingsApiTests.cs`

**Interfaces:**
- Consumes: Task 1 (`FromQuotation(…, taxUnits)`), Task 2 (`UnitsForAsync`), Task 3 (`TaxUnitContextAsync`, `ToBreakdownResponse`).
- Produces (nombres del contrato que consume el frontend; camelCase en JSON):
  ```csharp
  public sealed record PreviewQuotationConversionQuery(Guid TenantId, Guid QuotationId) : IQuery<QuotationConversionPreviewDto>;
  public sealed record QuotationConversionPreviewDto(
      decimal RetentionAmount, decimal NetTotal, DateTimeOffset CreatedAt,
      IReadOnlyList<WithholdingVerdictDto> WithholdingBreakdown, TaxUnitChangeDto? TaxUnitChange);
  public sealed record TaxUnitChangeDto(
      IReadOnlyList<TaxUnitShiftDto> TaxUnits, IReadOnlyList<WithholdingShiftDto> Withholdings,
      decimal FromNetTotal, decimal ToNetTotal);
  public sealed record TaxUnitShiftDto(string Country, int? FromYear, decimal? FromValue, int ToYear, decimal ToValue);
  public sealed record WithholdingShiftDto(
      Guid TaxRateId, string Name, string Type, decimal? MinimumBase, string? MinimumBaseUnit,
      decimal? FromMinimumBaseAmount, decimal? ToMinimumBaseAmount, decimal Base,
      bool FromApplies, bool ToApplies, decimal FromAmount, decimal ToAmount);
  public sealed record QuotationConversionPreviewResponse(
      decimal RetentionAmount, decimal NetTotal,
      IReadOnlyList<WithholdingBreakdownResponse> WithholdingBreakdown, TaxUnitChangeDto? TaxUnitChange);
  internal static TaxUnitChangeDto? QuotationConversionPreview.Compare(
      IReadOnlyList<WithholdingVerdictDto> before, decimal beforeNet,
      IReadOnlyList<WithholdingVerdictDto> after, decimal afterNet);
  ```
  `Task<QuotationConversionPreviewResponse> IOrderResponseComposer.ComposeConversionPreviewAsync(Guid tenantId, QuotationConversionPreviewDto preview, CancellationToken cancellationToken)`.

- [ ] **Step 1: Pruebas unitarias que fallan** (`QuotationConversionPreviewTests`, sobre `Compare` con listas de `WithholdingVerdictDto` armadas a mano):
  - `CompareIsNullWhenNoTaxUnitChanged`: mismas filas → `null`.
  - `CompareReportsTheRowThatStopsApplying`: CO 2025/50.000 → 2026/60.000, aplica 4.000 → no aplica 0 → `TaxUnits` = `[("CO", 2025, 50_000m, 2026, 60_000m)]`, una `WithholdingShiftDto` con `(FromApplies, ToApplies, FromAmount, ToAmount) == (true, false, 4_000m, 0m)`, netos `beforeNet`/`afterNet`.
  - `CompareOmitsARowWhoseAmountAndVerdictDidNotChange`: la UVT cambia pero la fila sigue aplicando con el mismo monto → `TaxUnits` con un elemento y `Withholdings` vacío.
  - `CompareReportsALegacyRowWithoutYear`: antes `TaxUnitYear` null y `TaxUnitValue` 50.000 → `FromYear` null.
  Regla: hay cambio de UVT si alguna fila con `MinimumBaseUnit` difiere en `(TaxUnitValue, TaxUnitYear)`; las filas se emparejan por posición (mismo orden, mismo `TaxRateId`); `TaxUnits` agrupa por `Country` (uno por país).

- [ ] **Step 2: RED** (build). **Step 3: Implementar** `Compare` y el handler:
  - `QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, OrdersPermissions.OrderManage)`.
  - `FindAsync` → `QuotationNotFound.For` (404); `quotation.EnsureConvertibleToOrder()` (422). Sin elegibilidad del cliente ni tipo de persona (spec §5.3).
  - `now = calendar.UtcNow`; `taxUnits = UnitsForAsync(quotation.ActiveWithholdings, lookup, DocumentDates.LocalDate(calendar, now), ct)`.
  - Dos pedidos: `Order.FromQuotation(OrderId.New(), tenantId, PreviewOrderNumber, quotation, notes: null, quotation.AdvisorId, [], now)` sin y con `taxUnits`. `private const string PreviewOrderNumber = "PREVIEW";` (el número no se valida más que no vacío y nunca sale del contador). Nada se agrega al repositorio ni se guarda.
  - `WithholdingBreakdown` y `RetentionAmount`/`NetTotal` del pedido refrescado; `CreatedAt = now`; `TaxUnitChange = taxUnits is null ? null : Compare(...)`.
  - Composer: `TaxUnitContextAsync(..., preview.CreatedAt, preview.WithholdingBreakdown, ...)` + `ToBreakdownResponse`.
  - Endpoint: `group.MapGet("/preview", PreviewQuotationConversionAsync).RequireAuthorization(OrdersPermissions.OrderManage).Produces<QuotationConversionPreviewResponse>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(422)`.

- [ ] **Step 4: GREEN unitario.** Luego integración, RED → GREEN, en `OrderWithholdingsApiTests` (helper `GetConversionPreviewAsync(setup, quotationId)`):
  - `ConversionPreviewShowsTheTaxUnitChange`: escenario de Task 2 sin convertir → `TaxUnitChange.TaxUnits` = `[("CO", thisYear - 1, 50_000m, thisYear, 60_000m)]`; una fila `(true, false, 4_000m, 0m)` con `(FromMinimumBaseAmount, ToMinimumBaseAmount) == (100_000m, 120_000m)`; `FromNetTotal == quotation.NetTotal`; `ToNetTotal == preview.NetTotal == quotation.Total`.
  - `ConversionPreviewWithoutChangeHasNoTaxUnitChange`: UVT del año ya cargada al cotizar → `TaxUnitChange == null`, `NetTotal == quotation.NetTotal`.
  - `ConversionPreviewDoesNotSpendTheOrderNumber`: pedir la vista previa dos veces y convertir → el número del pedido es el mismo que da una conversión sin vista previa en un tenant nuevo (comparar contra el literal que ya afirme una prueba de conversión de la suite; buscar `OrderNumber` en `OrderApiTests.cs`).
  - `ConversionPreviewOfAConvertedQuotationIs422`: convertir y pedir la vista previa → 422 `quotation.quotation.status_not_convertible` (o el código que hoy da `EnsureConvertibleToOrder` para una convertida; copiarlo de `OrderConversionRulesApiTests`).
  - `ConversionPreviewRequiresOrderManage`: 403 con un miembro sin `quotations.order.manage`, con el mismo patrón de 403 que use `OrderConversionRulesApiTests` o `ManualOrderApiTests`.

- [ ] **Step 5:** una vez, `--filter "FullyQualifiedName~OrderWithholdingsApiTests"` y `--filter "FullyQualifiedName~QuotationWithholdingsApiTests"` (el `WithholdingBreakdownResponse` cambió de forma). Todo verde.

- [ ] **Step 6: Commit** `feat(orders): preview the tax unit change before converting a quotation`.

**Fin del Bloque B: revisión del bloque, y la revisión fuerte de toda la rama.**

## Cierre

- `dotnet build` de la solución sin warnings nuevos; `ArchitectureTests` (rápidas, sin contenedores).
- Merge a `develop` sólo con pedido del owner. Al mergear (en `develop`, no en la rama), anotar en `docs/estado.md`. Despliegue: backend primero.
