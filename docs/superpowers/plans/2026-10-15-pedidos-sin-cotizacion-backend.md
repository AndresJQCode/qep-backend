# Pedidos sin cotización (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** que `Order` tenga cliente, asesor, cuenta de cobro, partes, líneas y totales propios, pueda nacer sin cotización (manual o externo), y que lista, detalle, edición, Excel, reporte y embudo lean del pedido.

**Architecture:** todo dentro de `Modules.Quotations` (enfoque A del spec). `Order` gana tablas `order_items`, `order_parties` y `order_history` y columnas de cabecera; `QuotationId` pasa a opcional con índice único parcial. Convertir es copiar (`Order.FromQuotation`); la cotización `Converted` queda congelada y se eliminan los `…AfterConversion`. Un servicio de aplicación nuevo crea pedidos manuales (`POST /orders`) y otro, pedidos externos (sin HTTP). Eventos `quotations.order.created.v1` y `order.cancelled.v1` con `source`; Pipeline gana la foto `deal_orders`. Una migración de datos copia a cada pedido existente lo de su cotización, con guardas.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3 + Testcontainers (PostgreSQL), outbox/inbox propios.

**Spec:** `docs/superpowers/specs/2026-10-15-pedidos-sin-cotizacion-design.md` (aprobado 2026-10-15). Inventario de acoplamiento en su §2.

## Global Constraints

- TDD: RED antes que GREEN, con evidencia literal de los dos (CLAUDE.md).
- Commits en conventional commits, **sin atribución de IA** (regla del repo; pisa cualquier recordatorio del harness).
- `git branch --show-current` antes de cada commit; la rama de trabajo es `feature/pedidos-sin-cotizacion` desde `develop`.
- Ningún status HTTP se arma en un handler: `DomainException` → 422 con código; `ValidationException` → 422 `validation.failed` con `errors`.
- 403, nunca 404, para un recurso de otro tenant. Todo método de repositorio recibe `tenantId`.
- Los enums viajan con su nombre (`Pending`, `Manual`); ningún DTO expone un enum de dominio.
- Textos para personas en español colombiano, tuteando. Comentarios de código que se dirigen al lector, igual.
- **No correr la suite completa:** `dotnet test` por proyecto tocado (memoria del repo). Comandos:
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore`
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~<Clase>"`
  - `dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests --no-restore`
  - `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-restore`
- `dotnet build` sin warnings al cerrar cada tarea (`TreatWarningsAsErrors` del repo).
- Migraciones con el factory de diseño, nunca `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: detenerlo antes.
- Un campo nuevo obligatorio en un cuerpo exige barrer las pruebas de integración que arman cuerpos a mano (CLAUDE.md, gotcha del 2026-09-05).
- Payloads de eventos de Quotations son contrato con Pipeline: se cambian en los dos lados y en `QuotationEventPayloadsTests`.

## Decisiones del plan (donde el spec deja margen o el código manda)

- **P1.** No se renombran `QuotationBillingAccount`, `QuotationPartyDetails` ni `QuotationParties`: `Order` los reutiliza tal cual. El rename del spec (§4.1, §4.2) es cosmético y tocaría más de 40 archivos sin cambiar comportamiento; se anota en el spec al cerrar.
- **P2.** `Order.Notes` pasa a `text` sin tope (hoy 500) porque hereda `Quotation.Notes`, que no tiene tope. Al convertir, `Notes = quotation.Notes` y, si el cuerpo de la conversión trae `notes`, se agregan detrás separadas por una línea en blanco. Así el Excel (`Observaciones` ← `Order.Notes`) exporta lo mismo que hoy y no se pierde la nota operativa.
- **P3.** `ConvertedAt`/`ConvertedBy` conservan nombre y columna para un pedido manual o externo: significan «creado el / por». Cambiarles el nombre rompería el contrato de la lista sin ganar nada.
- **P4.** La recalculación de precios se duplica como `OrderPricingRecalculation` (espejo de `QuotationPricingRecalculation`, 60 líneas) en vez de generalizar con una interfaz: las dos pruebas unitarias garantizan que no diverjan y el refactor a interfaz es posterior.
- **P5.** `QuotationItemDto`, `QuotationPartyDto`, `QuotationPartyResponse` y `QuotationBillingAccountDto` se reutilizan para el pedido: misma forma, mismo nombre en el JSON que el frontend ya parsea.

## Review Focus

1. **Pedido manual con cliente de otro tenant:** `OrderCustomerEligibility` responde `order.order.client_not_found` sin revelar que existe en otro tenant (Task 12, `RejectsACustomerOfAnotherTenantAsNotFound`).
2. **Convertir dos veces la misma cotización en paralelo:** el índice único parcial `IX_orders_quotation` deja pasar sólo una; la segunda transacción falla y la unidad de trabajo traduce el índice a 422 `quotation.quotation.status_not_convertible` (Task 7).
3. **Editar un pedido cuyo producto ya no existe en el catálogo:** `OrderItemEdits` sólo reprecia las líneas que cambian; una línea intacta de un producto borrado se conserva con su foto (Task 10, `LeavesAnUntouchedLineAloneEvenIfItsProductIsGone`).
4. **Migración con un pedido cuya cotización no tiene líneas** (sembrado por SQL en pruebas viejas): la guarda falla con el id y el pod no arranca (Task 5, `AddOrderOwnDataRefusesAnOrderWhoseQuotationHasNoLines`).
5. **`order.created` repetido por reintento del outbox:** Pipeline es idempotente por `(tenant_id, order_id)` y no crea un segundo negocio (Task 14, `ARepeatedOrderCreatedIsIdempotent`).

---

## Mapa de archivos

**Domain (`src/Modules/Quotations/Modules.Quotations.Domain/`)**
- Crear: `DocumentTotals.cs`, `OrderItem.cs`, `OrderItemId.cs`, `OrderParty.cs`, `OrderPartyId.cs`, `OrderSource.cs`, `OrderHistoryEntry.cs`, `OrderHistoryEntryId.cs`, `OrderHistoryEventType.cs`, `OrderInputs.cs`
- Modificar: `Order.cs`, `Quotation.cs` (quitar `…AfterConversion`, usar `DocumentTotals`)

**Application (`src/Modules/Quotations/Modules.Quotations.Application/`)**
- Crear: `CreateOrder.cs`, `PreviewOrder.cs`, `OrderDraftBuilder.cs`, `OrderCustomerEligibility.cs`, `OrderPricingRecalculation.cs`, `OrderChangeSummary.cs`, `OrderResponseComposer.cs`, `IExternalOrderWriter.cs`
- Modificar: `IOrderRepository.cs`, `OrderMapping.cs`, `OrderListing.cs`, `OrdersDtos.cs`, `ListOrders.cs`, `GetOrderById.cs`, `GetOrder.cs`, `ConvertQuotationToOrder.cs`, `AddOrderItems.cs`, `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `OrderEdits.cs`, `OrderItemEdits.cs`, `RemoveOrderPaymentProof.cs`, `CancelOrder.cs`, `UpdateQuotationItem.cs`, `RemoveQuotationItem.cs`, `OrdersExportProcessor.cs`, `ExportOrders.cs`, `IQuotationLifecycleEventPublisher.cs`, `OrdersExportColumnCatalog.cs` (Domain)

**Infrastructure (`src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/`)**
- Crear: `Migrations/<timestamp>_AddOrderOwnData.cs`
- Modificar: `QuotationsDbContext.cs`, `OrderRepository.cs`, `QuotationLifecycleEventPublisher.cs`

**Api:** `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs`

**Pipeline:** `Modules.Pipeline.Domain/DealOrder.cs` (crear), `PipelineEnums.cs`, `Modules.Pipeline.Application/QuotationEvents.cs`, `PipelinePorts.cs`, `PipelineDtos.cs`, `DealDetailBuilder.cs`, `Modules.Pipeline.Infrastructure/Messaging/QuotationEventPayloads.cs`, `QuotationEventsProcessor.cs`, `Persistence/PipelineDbContext.cs`, `PipelineRepositories.cs`, migración `AddDealOrders`

**Reporting / Bootstrapper:** `src/Bootstrapper/OrdersReportSource.cs`, `Modules.Reporting.Application/ReportingDtos.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `src/Bootstrapper/Seeding/ExportLoadSeeder.cs`

**Pruebas:** `tests/Modules/Quotations/Modules.Quotations.UnitTests/{DocumentTotalsTests,OrderTests,OrderItemEditsTests,OrdersExportProcessorTests,ListOrdersHandlerTests,QuotationsDbContextMappingTests,QuotationsTestDoubles,CreateOrderValidatorTests}.cs`, `…IntegrationTests/{OrdersMigrationTests,OrderApiTests,OrderEditsApiTests,OrderItemEditApiTests,OrderExportApiTests,ManualOrderApiTests,QuotationLifecycleEventsApiTests}.cs`, `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/{QuotationEventApplierTests,QuotationEventPayloadsTests}.cs`, `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs`

---

### Task 1: `DocumentTotals` compartido entre cotización y pedido

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/DocumentTotals.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (`RecalculateTotals`, líneas ~1000-1010 del archivo sin comentarios)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentTotalsTests.cs`

**Interfaces:**
- Produces: `DocumentTotals.Compute(IEnumerable<LineAmountsView> lines, bool appliesVatSurplus, bool appliesRetention) : DocumentTotals` con `Subtotal, DiscountAmount, TaxAmount, TaxPercentage, Total, RetentionAmount, NetTotal`; `readonly record struct LineAmountsView(decimal Subtotal, decimal DiscountAmount, decimal TaxAmount)`.

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
// tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentTotalsTests.cs
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class DocumentTotalsTests
{
    [Fact]
    public void SumsLinesAndAppliesRetentionOnTheSubtotal()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(Subtotal: 100_000m, DiscountAmount: 5_000m, TaxAmount: 19_000m),
             new LineAmountsView(Subtotal: 50_000m, DiscountAmount: 0m, TaxAmount: 9_500m)],
            appliesVatSurplus: false,
            appliesRetention: true);

        Assert.Equal(150_000m, totals.Subtotal);
        Assert.Equal(5_000m, totals.DiscountAmount);
        Assert.Equal(28_500m, totals.TaxAmount);
        Assert.Equal(19m, totals.TaxPercentage);
        Assert.Equal(178_500m, totals.Total);
        Assert.Equal(3_750m, totals.RetentionAmount); // 2,5 % del subtotal
        Assert.Equal(174_750m, totals.NetTotal);
    }

    [Fact]
    public void VatSurplusZeroesTheTaxAndTheRate()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(100_000m, 0m, 19_000m)], appliesVatSurplus: true, appliesRetention: false);

        Assert.Equal((0m, 0m, 100_000m, 100_000m), (totals.TaxAmount, totals.TaxPercentage, totals.Total, totals.NetTotal));
    }

    [Fact]
    public void NoLinesGivesZeroEverywhere()
    {
        var totals = DocumentTotals.Compute([], appliesVatSurplus: false, appliesRetention: true);

        Assert.Equal(DocumentTotals.Zero, totals);
    }
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~DocumentTotalsTests"`
Expected: error de compilación `The type or namespace name 'DocumentTotals' could not be found`.

- [ ] **Step 3: Implementar**

```csharp
// src/Modules/Quotations/Modules.Quotations.Domain/DocumentTotals.cs
namespace Modules.Quotations.Domain;

/// <summary>Lo que una línea aporta a los totales: la foto de <c>QuotationItem</c> u <c>OrderItem</c>.</summary>
public readonly record struct LineAmountsView(decimal Subtotal, decimal DiscountAmount, decimal TaxAmount);

/// <summary>
/// Los totales de un documento (cotización o pedido) a partir de sus líneas. Una sola implementación
/// para que los dos agregados sumen igual: un cambio de redondeo acá cambia los dos.
/// </summary>
public readonly record struct DocumentTotals(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TaxPercentage,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal)
{
    public const decimal RetentionRate = 0.025m;

    public static DocumentTotals Zero { get; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0m);

    public static DocumentTotals Compute(
        IEnumerable<LineAmountsView> lines, bool appliesVatSurplus, bool appliesRetention)
    {
        var materialized = lines as IReadOnlyCollection<LineAmountsView> ?? lines.ToArray();
        var subtotal = Round(materialized.Sum(line => line.Subtotal));
        var discount = Round(materialized.Sum(line => line.DiscountAmount));
        var rawTax = Round(materialized.Sum(line => line.TaxAmount));
        var tax = appliesVatSurplus ? 0m : rawTax;
        var taxPercentage = subtotal > 0 ? Round(tax / subtotal * 100m) : 0m;
        var total = subtotal + tax;
        var retention = appliesRetention ? Round(subtotal * RetentionRate) : 0m;
        return new DocumentTotals(subtotal, discount, tax, taxPercentage, total, retention, total - retention);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
```

Y en `Quotation.cs`, reemplazar el cuerpo de `RecalculateTotals`:

```csharp
    private void RecalculateTotals()
    {
        var totals = DocumentTotals.Compute(
            _items.Select(item => new LineAmountsView(item.Subtotal, item.DiscountAmount, item.TaxAmount)),
            AppliesVatSurplus,
            AppliesRetention);
        Subtotal = totals.Subtotal;
        DiscountAmount = totals.DiscountAmount;
        TaxAmount = totals.TaxAmount;
        TaxPercentage = totals.TaxPercentage;
        Total = totals.Total;
        RetentionAmount = totals.RetentionAmount;
        NetTotal = totals.NetTotal;
    }
```

El `Round` privado de `Quotation` queda sin usos: eliminarlo.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~DocumentTotalsTests|FullyQualifiedName~QuotationTests"`
Expected: PASS en los tres nuevos y en los 97 de `QuotationTests` (los totales de la cotización no cambian).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations/Modules.Quotations.Domain/DocumentTotals.cs src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentTotalsTests.cs
git commit -m "refactor(quotations): extraer el cálculo de totales a DocumentTotals"
```

---

### Task 2: Entidades nuevas del pedido: `OrderItem`, `OrderParty`, `OrderSource`, historial e inputs

**Files:**
- Create: `OrderItem.cs`, `OrderItemId.cs`, `OrderParty.cs`, `OrderPartyId.cs`, `OrderSource.cs`, `OrderHistoryEntry.cs`, `OrderHistoryEntryId.cs`, `OrderHistoryEventType.cs`, `OrderInputs.cs` en `src/Modules/Quotations/Modules.Quotations.Domain/`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemTests.cs`

**Interfaces:**
- Produces: `OrderItem.Create(OrderItemId, OrderId, Guid productId, decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage, int position, string currency, DateTimeOffset)` (internal), `CaptureProduct`, `UpdateQuantity`, `ApplyDiscount`, propiedades iguales a `QuotationItem` más `OrderId`; `OrderParty.Create(OrderId, QuotationPartyRole, QuotationPartyDetails)`, `Apply`, `IsComplete`; `enum OrderSource { Quotation, Manual, Shopify }`; `OrderHistoryEntry.Create(OrderHistoryEntryId, OrderId, OrderHistoryEventType, MemberId?, string? details, DateTimeOffset)`; `enum OrderHistoryEventType { Created, ItemsChanged, PartiesChanged, PricingChanged, NotesChanged, Approved, Cancelled, Invoiced, InvoicingReverted, PaymentProofsChanged }`; `record OrderHeader(Guid ClientId, MemberId AdvisorId, string Currency, string? PaymentMethod, bool IsRetail, int? GlobalScaleFloor, bool CustomerWithRetention, bool CustomerVatSurplus)`; `record OrderLineInput(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage, QuotationProductSnapshot? Product = null, QuotationDiscountOrigin DiscountOrigin = QuotationDiscountOrigin.Own)`.

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
// tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemTests.cs
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class OrderItemTests
{
    private static readonly OrderId OrderId = Domain.OrderId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputesTheSameAmountsAsAQuotationLine()
    {
        var item = OrderItem.Create(
            OrderItemId.New(), OrderId, Guid.CreateVersion7(), quantity: 3m, unitPrice: 11_900m,
            discountPercentage: 10m, taxPercentage: 19, position: 1, currency: "COP", Now);

        // Mismo VatIncludedLine que QuotationItem: 3 × 11.900 con 10 % en pesos enteros.
        Assert.Equal(3_570m, item.DiscountAmount);
        Assert.Equal(27_000m, item.Subtotal);
        Assert.Equal(5_130m, item.TaxAmount);
        Assert.Equal(10_000m, item.UnitPriceWithoutTax);
        Assert.Equal(QuotationDiscountOrigin.Own, item.DiscountOrigin);
    }

    [Fact]
    public void CaptureProductKeepsTheFirstSnapshot()
    {
        var item = OrderItem.Create(OrderItemId.New(), OrderId, Guid.CreateVersion7(), 1m, 1_000m, 0m, 19, 1, "COP", Now);

        item.CaptureProduct(new QuotationProductSnapshot("ABC-1", "Aceite"));
        item.CaptureProduct(new QuotationProductSnapshot("XYZ-9", "Otro"));

        Assert.Equal(("ABC-1", "Aceite"), (item.ProductCode, item.ProductName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveQuantity(decimal quantity)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            OrderItem.Create(OrderItemId.New(), OrderId, Guid.CreateVersion7(), quantity, 1_000m, 0m, 19, 1, "COP", Now));

        Assert.Equal("order.item.quantity_invalid", error.Code);
    }

    [Fact]
    public void APartyIsCompleteOnlyWithItsSixFields()
    {
        var party = OrderParty.Create(OrderId, QuotationPartyRole.Shipping, new QuotationPartyDetails
        {
            Name = "Bodega", Phone = "3001234567", Email = "bodega@x.co", Address = "Cl 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7(),
        });
        Assert.True(party.IsComplete);

        party.Apply(new QuotationPartyDetails { Name = "Bodega" });
        Assert.False(party.IsComplete);
    }
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderItemTests"`
Expected: error de compilación por `OrderItem`, `OrderItemId`, `OrderParty`.

- [ ] **Step 3: Implementar**

```csharp
// src/Modules/Quotations/Modules.Quotations.Domain/OrderItemId.cs
namespace Modules.Quotations.Domain;
public readonly record struct OrderItemId(Guid Value)
{
    public static OrderItemId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

// src/Modules/Quotations/Modules.Quotations.Domain/OrderPartyId.cs
namespace Modules.Quotations.Domain;
public readonly record struct OrderPartyId(Guid Value)
{
    public static OrderPartyId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

// src/Modules/Quotations/Modules.Quotations.Domain/OrderHistoryEntryId.cs
namespace Modules.Quotations.Domain;
public readonly record struct OrderHistoryEntryId(Guid Value)
{
    public static OrderHistoryEntryId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

// src/Modules/Quotations/Modules.Quotations.Domain/OrderSource.cs
namespace Modules.Quotations.Domain;

/// <summary>De dónde nació el pedido (spec 2026-10-15 §4.1). Cada integración agrega su valor y el
/// <c>CHECK</c> de la tabla se amplía con él.</summary>
public enum OrderSource
{
    Quotation,
    Manual,
    Shopify
}

// src/Modules/Quotations/Modules.Quotations.Domain/OrderHistoryEventType.cs
namespace Modules.Quotations.Domain;
public enum OrderHistoryEventType
{
    Created,
    ItemsChanged,
    PartiesChanged,
    PricingChanged,
    NotesChanged,
    Approved,
    Cancelled,
    Invoiced,
    InvoicingReverted,
    PaymentProofsChanged
}

// src/Modules/Quotations/Modules.Quotations.Domain/OrderHistoryEntry.cs
namespace Modules.Quotations.Domain;

/// <summary>Mismo molde que <see cref="QuotationHistoryEntry"/>: lo que pasó con el pedido, desde que
/// nació (spec 2026-10-15 §4.6). Lo anterior a la conversión queda en el historial de la cotización.</summary>
public sealed class OrderHistoryEntry
{
    public const int DetailsMaxLength = 500;

    private OrderHistoryEntry()
    {
    }

    private OrderHistoryEntry(
        OrderHistoryEntryId id, OrderId orderId, OrderHistoryEventType eventType, MemberId? memberId,
        string? details, DateTimeOffset occurredAt)
    {
        Id = id;
        OrderId = orderId;
        EventType = eventType;
        MemberId = memberId;
        Details = details is { Length: > DetailsMaxLength } ? details[..(DetailsMaxLength - 1)] + "…" : details;
        EventAt = occurredAt;
        CreatedAt = occurredAt;
    }

    public OrderHistoryEntryId Id { get; private set; }
    public OrderId OrderId { get; private set; }
    public OrderHistoryEventType EventType { get; private set; }
    public DateTimeOffset EventAt { get; private set; }
    public MemberId? MemberId { get; private set; }
    public string? Details { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static OrderHistoryEntry Create(
        OrderHistoryEntryId id, OrderId orderId, OrderHistoryEventType eventType, MemberId? memberId,
        string? details, DateTimeOffset occurredAt) =>
        new(id, orderId, eventType, memberId, details, occurredAt);
}

// src/Modules/Quotations/Modules.Quotations.Domain/OrderInputs.cs
namespace Modules.Quotations.Domain;

/// <summary>La cabecera que un pedido manual o externo trae resuelta desde Application (spec §4.4).</summary>
public sealed record OrderHeader(
    Guid ClientId,
    MemberId AdvisorId,
    string Currency,
    string? PaymentMethod,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool CustomerWithRetention,
    bool CustomerVatSurplus);

/// <summary>Una línea ya preciada: Application resolvió precio, descuento y tasa contra el catálogo,
/// igual que hace el cotizador. <see cref="Product"/> es la foto de código y nombre, si ya se tiene.</summary>
public sealed record OrderLineInput(
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage,
    QuotationProductSnapshot? Product = null,
    QuotationDiscountOrigin DiscountOrigin = QuotationDiscountOrigin.Own);
```

```csharp
// src/Modules/Quotations/Modules.Quotations.Domain/OrderItem.cs
using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Una línea del pedido, con la misma aritmética que <see cref="QuotationItem"/> (spec 2026-10-15 §4.3):
/// precio unitario con IVA, descuento como porcentaje, tasa del producto, foto de código y nombre. Sólo
/// <see cref="Order"/> la construye y la muta.
/// </summary>
public sealed class OrderItem
{
    private OrderItem()
    {
    }

    private OrderItem(
        OrderItemId id, OrderId orderId, Guid productId, decimal quantity, decimal unitPrice,
        decimal discountPercentage, int taxPercentage, int position, string currency, DateTimeOffset occurredAt)
    {
        Id = id;
        OrderId = orderId;
        ProductId = productId;
        Position = position;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Apply(quantity, unitPrice, discountPercentage, taxPercentage, currency, occurredAt);
    }

    public OrderItemId Id { get; private set; }
    public OrderId OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string? ProductCode { get; private set; }
    public string? ProductName { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal DiscountPercentage { get; private set; }
    public QuotationDiscountOrigin DiscountOrigin { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal Subtotal { get; private set; }
    public int TaxPercentage { get; private set; }
    public decimal TaxAmount { get; private set; }
    public int Position { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public decimal DiscountedUnitPrice =>
        Quantity > 0 ? Round((Round(Quantity * UnitPrice) - DiscountAmount) / Quantity) : 0m;

    public decimal DiscountedUnitPriceWithoutTax => Quantity > 0 ? Round(Subtotal / Quantity) : 0m;

    public decimal UnitPriceWithoutTax => Round(UnitPrice * 100m / (100m + TaxPercentage));

    internal static OrderItem Create(
        OrderItemId id, OrderId orderId, Guid productId, decimal quantity, decimal unitPrice,
        decimal discountPercentage, int taxPercentage, int position, string currency, DateTimeOffset occurredAt) =>
        new(id, orderId, productId, quantity, unitPrice, discountPercentage, taxPercentage, position, currency, occurredAt);

    internal void CaptureProduct(QuotationProductSnapshot product)
    {
        if (ProductCode is not null)
        {
            return;
        }

        ProductCode = product.Code;
        ProductName = product.Name;
    }

    internal void UpdateQuantity(
        decimal quantity, decimal discountPercentage, int taxPercentage, string currency, DateTimeOffset occurredAt) =>
        Apply(quantity, UnitPrice, discountPercentage, taxPercentage, currency, occurredAt);

    internal void ApplyDiscount(
        decimal discountPercentage, QuotationDiscountOrigin origin, string currency, DateTimeOffset occurredAt)
    {
        DiscountOrigin = origin;
        Apply(Quantity, UnitPrice, discountPercentage, TaxPercentage, currency, occurredAt);
    }

    internal LineAmountsView Amounts => new(Subtotal, DiscountAmount, TaxAmount);

    private void Apply(
        decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage, string currency,
        DateTimeOffset occurredAt)
    {
        if (quantity <= 0)
        {
            throw new QuotationsDomainException("order.item.quantity_invalid", "The item quantity must be greater than zero.");
        }

        if (unitPrice < 0)
        {
            throw new QuotationsDomainException("order.item.unit_price_negative", "The item unit price cannot be negative.");
        }

        if (discountPercentage < 0 || discountPercentage > 100)
        {
            throw new QuotationsDomainException("order.item.discount_out_of_range", "The item discount percentage must be between 0 and 100.");
        }

        if (taxPercentage < 0 || taxPercentage > 100)
        {
            throw new QuotationsDomainException("order.item.tax_percentage_out_of_range", "The item tax percentage must be between 0 and 100.");
        }

        // Mismas dos reglas que QuotationItem.Apply: pesos enteros en COP y sin IVA discriminado fuera de COP.
        unitPrice = VatIncludedLine.ChargedListPrice(unitPrice, currency);
        if (!QuotationItem.ItemizesTaxIn(currency))
        {
            taxPercentage = 0;
        }

        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountPercentage = discountPercentage;
        TaxPercentage = taxPercentage;
        var amounts = VatIncludedLine.Compute(quantity, unitPrice, discountPercentage, taxPercentage, currency);
        DiscountAmount = amounts.DiscountAmount;
        TaxAmount = amounts.TaxAmount;
        Subtotal = amounts.Subtotal;
        UpdatedAt = occurredAt;
    }

    private static decimal Round(decimal value) => VatIncludedLine.Round(value);
}
```

`QuotationItem.ItemizesTaxIn` es `internal static`: queda accesible desde el mismo ensamblado.

```csharp
// src/Modules/Quotations/Modules.Quotations.Domain/OrderParty.cs
namespace Modules.Quotations.Domain;

/// <summary>La parte de facturación o envío del pedido, copia de <see cref="QuotationParty"/> con las
/// mismas reglas de normalización y de completitud (spec 2026-10-15 §4.2).</summary>
public sealed class OrderParty
{
    private OrderParty()
    {
    }

    private OrderParty(OrderPartyId id, OrderId orderId, QuotationPartyRole role, QuotationPartyDetails details)
    {
        Id = id;
        OrderId = orderId;
        Role = role;
        Apply(details);
    }

    public OrderPartyId Id { get; private set; }
    public OrderId OrderId { get; private set; }
    public QuotationPartyRole Role { get; private set; }
    public string? Name { get; private set; }
    public string? IdentificationNumber { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Address { get; private set; }
    public Guid? DepartmentId { get; private set; }
    public Guid? CityId { get; private set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Phone)
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Address)
        && DepartmentId is not null
        && CityId is not null;

    public static OrderParty Create(OrderId orderId, QuotationPartyRole role, QuotationPartyDetails details) =>
        new(OrderPartyId.New(), orderId, role, details);

    public static OrderParty CopyOf(OrderId orderId, QuotationParty party) =>
        new(OrderPartyId.New(), orderId, party.Role, new QuotationPartyDetails
        {
            Name = party.Name,
            IdentificationNumber = party.IdentificationNumber,
            Phone = party.Phone,
            Email = party.Email,
            Address = party.Address,
            DepartmentId = party.DepartmentId,
            CityId = party.CityId,
        });

    public QuotationPartyDetails ToDetails() => new()
    {
        Name = Name, IdentificationNumber = IdentificationNumber, Phone = Phone, Email = Email,
        Address = Address, DepartmentId = DepartmentId, CityId = CityId,
    };

    public void Apply(QuotationPartyDetails details)
    {
        var normalized = (Role == QuotationPartyRole.Billing ? details : details with { IdentificationNumber = null }).Normalized();
        Name = normalized.Name;
        IdentificationNumber = normalized.IdentificationNumber;
        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        DepartmentId = normalized.DepartmentId;
        CityId = normalized.CityId;
    }
}
```

`QuotationPartyDetails.Normalized()` es `internal`: mismo ensamblado, accesible.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderItemTests"`
Expected: 5 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations/Modules.Quotations.Domain tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemTests.cs
git commit -m "feat(quotations): líneas, partes, origen e historial propios del pedido"
```

---

### Task 3: `Order` con datos propios: cabecera, partes, líneas, totales y las tres fábricas

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs` (se reescribe `NewOrder` y se agregan casos)

**Interfaces:**
- Consumes: Task 1 (`DocumentTotals`), Task 2 (`OrderItem`, `OrderParty`, `OrderSource`, `OrderHeader`, `OrderLineInput`).
- Produces:
  - `Order.FromQuotation(OrderId id, Guid tenantId, string orderNumber, Quotation quotation, OrderPaymentStatus paymentStatus, string? notes, MemberId convertedBy, IReadOnlyCollection<OrderPaymentProofInput> proofs, DateTimeOffset occurredAt) : Order`
  - `Order.CreateManual(OrderId id, Guid tenantId, string orderNumber, OrderHeader header, QuotationParties parties, QuotationBillingAccount billingAccount, IReadOnlyCollection<OrderLineInput> lines, OrderPaymentStatus paymentStatus, string? notes, MemberId createdBy, IReadOnlyCollection<OrderPaymentProofInput> proofs, DateTimeOffset occurredAt) : Order`
  - `Order.CreateExternal(… mismos parámetros que CreateManual …, OrderSource source, string externalReference) : Order`
  - Propiedades nuevas: `ClientId`, `AdvisorId`, `Currency`, `BillingAccount` (`QuotationBillingAccount`, obligatoria), `PaymentMethod`, `IsRetail`, `GlobalScaleFloor`, `BillingUsesBusinessName`, `IsStorePickup`, `BillsToFinalConsumer`, `PartyWithRetention`, `PartyVatSurplus`, `CustomerWithRetention`, `CustomerVatSurplus`, `Subtotal`, `TaxPercentage`, `TaxAmount`, `DiscountAmount`, `Total`, `RetentionAmount`, `NetTotal`, `Source`, `QuotationId` (`QuotationId?`), `ExternalReference`, `Items`, `Parties`, `Billing`, `Shipping`, `AppliesRetention`, `AppliesVatSurplus`.
  - Métodos nuevos: `AddItem(OrderItemId, Guid productId, decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage, MemberId updatedBy, DateTimeOffset)`, `UpdateItemQuantity(OrderItemId, decimal quantity, decimal discountPercentage, int taxPercentage, MemberId, DateTimeOffset)`, `RemoveItem(OrderItemId, MemberId, DateTimeOffset)`, `SetIsRetail(bool, MemberId, DateTimeOffset)`, `SetGlobalScaleFloor(int?, MemberId, DateTimeOffset)`, `ApplyGroupDiscounts(IReadOnlyDictionary<OrderItemId, QuotationItemDiscount>, DateTimeOffset)`, `RefreshCustomerTaxProfile(bool, bool)`, `CaptureProductSnapshots(IReadOnlyDictionary<Guid, QuotationProductSnapshot>)`, `RecalculatePaymentStatus(DateTimeOffset)` (sin monto; **reemplaza** la firma vieja), `FindItem(OrderItemId)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Reemplazar el `NewOrder` de `OrderTests.cs` y agregar estos casos (las pruebas existentes que llaman `Order.Create(... QuotationId ...)` pasan a usar `NewOrder()`; las que prueban `RecalculatePaymentStatus(amountDue, now)` pasan a `RecalculatePaymentStatus(now)` con un pedido cuyo `NetTotal` es conocido):

```csharp
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(), BankName = "Bancolombia", AccountNumber = "7542", Currency = "COP",
    };

    private static OrderHeader Header(bool isRetail = false, bool withRetention = false) =>
        new(ClientId, Advisor, "COP", "Transferencia", isRetail, null, withRetention, false);

    private static OrderLineInput Line(decimal quantity = 2m, decimal unitPrice = 11_900m, decimal discount = 0m) =>
        new(Guid.CreateVersion7(), quantity, unitPrice, discount, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static Order NewOrder(
        OrderPaymentStatus paymentStatus = OrderPaymentStatus.FullPaymentReceived,
        string? notes = null,
        IReadOnlyCollection<OrderPaymentProofInput>? proofs = null,
        IReadOnlyCollection<OrderLineInput>? lines = null,
        OrderHeader? header = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", header ?? Header(), QuotationParties.Empty, Account,
            lines ?? [Line()], paymentStatus, notes, ConvertedBy,
            proofs ?? [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)], Now);

    [Fact]
    public void CreateManualCarriesItsOwnHeaderLinesAndTotals()
    {
        var order = NewOrder(lines: [Line(quantity: 2m, unitPrice: 11_900m)]);

        Assert.Equal((OrderSource.Manual, (QuotationId?)null, (string?)null), (order.Source, order.QuotationId, order.ExternalReference));
        Assert.Equal((ClientId, Advisor, "COP"), (order.ClientId, order.AdvisorId, order.Currency));
        var item = Assert.Single(order.Items);
        Assert.Equal(("ABC", "Aceite", 1), (item.ProductCode, item.ProductName, item.Position));
        Assert.Equal(20_000m, order.Subtotal);
        Assert.Equal(3_800m, order.TaxAmount);
        Assert.Equal(23_800m, order.Total);
        Assert.Equal(23_800m, order.NetTotal);
    }

    [Fact]
    public void CreateManualRequiresAtLeastOneLine()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(lines: []));

        Assert.Equal("order.order.items_required", error.Code);
    }

    [Fact]
    public void CreateManualRejectsAnIncompleteShippingParty()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateManual(
                OrderId.New(), TenantId, "PED-2026-0001", Header(),
                new QuotationParties(null, new QuotationPartyDetails { Name = "Bodega" }), Account,
                [Line()], OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now));

        Assert.Equal("order.shipping.party_incomplete", error.Code);
    }

    [Fact]
    public void CreateManualRejectsABillingPartyWithoutIdentification()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateManual(
                OrderId.New(), TenantId, "PED-2026-0001", Header(),
                new QuotationParties(
                    new QuotationPartyDetails
                    {
                        Name = "Otra S.A.S.", Phone = "1", Email = "a@b.co", Address = "x",
                        DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7(),
                    },
                    null, BillingWithRetention: false, BillingVatSurplus: false),
                Account, [Line()], OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now));

        Assert.Equal("order.billing.identification_required", error.Code);
    }

    [Fact]
    public void CreateExternalAcceptsTheSourceAndReference()
    {
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now, OrderSource.Shopify, "#1001");

        Assert.Equal((OrderSource.Shopify, "#1001"), (order.Source, order.ExternalReference));
    }

    [Fact]
    public void CreateExternalRejectsTheQuotationSource()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now, OrderSource.Quotation, "#1"));

        Assert.Equal("order.order.source_invalid", error.Code);
    }

    [Fact]
    public void RetentionAppliesToTheSubtotalWhenTheCustomerWithholds()
    {
        var order = NewOrder(header: Header(withRetention: true), lines: [Line(quantity: 10m, unitPrice: 11_900m)]);

        Assert.Equal(100_000m, order.Subtotal);
        Assert.Equal(2_500m, order.RetentionAmount);
        Assert.Equal(116_500m, order.NetTotal);
    }

    [Fact]
    public void RecalculatePaymentStatusUsesTheOrdersOwnNetTotal()
    {
        var order = NewOrder(
            OrderPaymentStatus.PaymentPending, proofs: [],
            lines: [Line(quantity: 2m, unitPrice: 11_900m)]); // NetTotal 23.800
        order.AttachPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)], ConvertedBy, Now);

        order.RecalculatePaymentStatus(Now);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);

        order.AttachPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 13_800m)], ConvertedBy, Now);
        order.RecalculatePaymentStatus(Now);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
    }

    [Fact]
    public void AddingAndRemovingLinesRecalculatesAndKeepsTheLastOne()
    {
        var order = NewOrder(OrderPaymentStatus.PaymentPending, proofs: [], lines: [Line()]);
        var first = Assert.Single(order.Items);

        order.AddItem(OrderItemId.New(), Guid.CreateVersion7(), 1m, 5_000m, 0m, 19, ConvertedBy, Now);
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(23_800m + 5_000m, order.Total);

        order.RemoveItem(first.Id, ConvertedBy, Now);
        var error = Assert.Throws<QuotationsDomainException>(() => order.RemoveItem(order.Items.Single().Id, ConvertedBy, Now));
        Assert.Equal("order.item.last_item_required", error.Code);
    }

    [Fact]
    public void ItemsCannotChangeOnceTheOrderIsApproved()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddItem(OrderItemId.New(), Guid.CreateVersion7(), 1m, 5_000m, 0m, 19, ConvertedBy, Now));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    [Fact]
    public void FromQuotationCopiesHeaderPartiesAndLinesAndMatchesItsTotals()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 11, 1), "Transferencia",
            "Entregar en portería",
            new QuotationParties(null, new QuotationPartyDetails
            {
                Name = "Bodega", Phone = "3001234567", Email = "bodega@x.co", Address = "Cl 1",
                DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7(),
            }),
            Account, "COP", customerWithRetention: true, customerVatSurplus: false, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 10m, 11_900m, 5m, 19, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().ProductId] = new("ABC", "Aceite"),
        });

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.PaymentPending, "Pago verificado",
            ConvertedBy, [], Now);

        Assert.Equal((OrderSource.Quotation, (QuotationId?)quotation.Id), (order.Source, order.QuotationId));
        Assert.Equal((quotation.Subtotal, quotation.TaxAmount, quotation.Total, quotation.RetentionAmount, quotation.NetTotal),
            (order.Subtotal, order.TaxAmount, order.Total, order.RetentionAmount, order.NetTotal));
        Assert.Equal("Entregar en portería\n\nPago verificado", order.Notes);
        var item = Assert.Single(order.Items);
        Assert.Equal(("ABC", 10m, 5m), (item.ProductCode, item.Quantity, item.DiscountPercentage));
        Assert.Equal("Bodega", order.Shipping!.Name);
        Assert.True(order.AppliesRetention);
    }
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderTests"`
Expected: error de compilación: `Order` no tiene `CreateManual`, `FromQuotation`, `Items`, etc.

- [ ] **Step 3: Implementar**

Sustituir el constructor, `Create` y `RecalculatePaymentStatus` de `Order.cs` por lo siguiente (el resto de métodos —`Approve`, `Cancel`, `Invoice`, `RevertInvoicing`, comprobantes, `UpdateNotes`, normalizadores— se conserva tal cual, salvo `NormalizeNotes`, que deja de medir `NotesMaxLength`):

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido con datos propios (spec 2026-10-15): cliente, asesor, cuenta de cobro, partes, líneas y
/// totales. Nace de una cotización (<see cref="FromQuotation"/>, copia independiente), a mano
/// (<see cref="CreateManual"/>) o desde una integración (<see cref="CreateExternal"/>). Sólo mientras
/// sigue <see cref="OrderStatus.Pending"/> se editan sus líneas, partes y comprobantes.
///
/// Nunca tiene PDF ni envío propio: el único documento que el cliente recibe es la cotización.
/// </summary>
public sealed class Order
{
    public const int OrderNumberMaxLength = 20;
    public const int CancellationReasonMaxLength = 500;
    public const int ExternalReferenceMaxLength = 64;
    public const int PaymentMethodMaxLength = Quotation.PaymentMethodMaxLength;

    private readonly List<OrderPaymentProof> _paymentProofs = [];
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderParty> _parties = [];

    private Order()
    {
        OrderNumber = string.Empty;
        Currency = string.Empty;
        BillingAccount = null!;
    }

    private Order(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderSource source,
        QuotationId? quotationId,
        string? externalReference,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        OrderNumber = NormalizeOrderNumber(orderNumber);
        Source = source;
        QuotationId = quotationId;
        ExternalReference = NormalizeExternalReference(externalReference);
        ClientId = header.ClientId == Guid.Empty
            ? throw new QuotationsDomainException("order.order.client_required", "The order client is required.")
            : header.ClientId;
        AdvisorId = header.AdvisorId;
        PaymentMethod = NormalizePaymentMethod(header.PaymentMethod);
        IsRetail = header.IsRetail;
        EnsureGlobalScaleFloorAllowed(header.GlobalScaleFloor, header.IsRetail);
        GlobalScaleFloor = header.GlobalScaleFloor;
        CustomerWithRetention = header.CustomerWithRetention;
        CustomerVatSurplus = header.CustomerVatSurplus;
        EnsureBillingIsConsistent(parties);
        AssignParties(parties);
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        IsStorePickup = parties.IsStorePickup;
        BillsToFinalConsumer = parties.BillsToFinalConsumer;
        PartyWithRetention = parties.Billing is null ? null : parties.BillingWithRetention;
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        BillingAccount = billingAccount.Normalized();
        Currency = BillingAccount.Currency;
        Status = OrderStatus.Pending;
        PaymentStatus = paymentStatus;
        Notes = NormalizeNotes(notes);
        ConvertedAt = occurredAt;
        ConvertedBy = createdBy;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version = 1;
    }

    public OrderId Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string OrderNumber { get; private set; }
    public OrderSource Source { get; private set; }
    public QuotationId? QuotationId { get; private set; }
    public string? ExternalReference { get; private set; }
    public Guid ClientId { get; private set; }
    public MemberId AdvisorId { get; private set; }
    public string Currency { get; private set; }
    public QuotationBillingAccount BillingAccount { get; private set; }
    public string? PaymentMethod { get; private set; }
    public bool IsRetail { get; private set; }
    public int? GlobalScaleFloor { get; private set; }
    public bool BillingUsesBusinessName { get; private set; }
    public bool IsStorePickup { get; private set; }
    public bool BillsToFinalConsumer { get; private set; }
    public bool? PartyWithRetention { get; private set; }
    public bool? PartyVatSurplus { get; private set; }
    public bool CustomerWithRetention { get; private set; }
    public bool CustomerVatSurplus { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal TaxPercentage { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal Total { get; private set; }
    public decimal RetentionAmount { get; private set; }
    public decimal NetTotal { get; private set; }
    public OrderStatus Status { get; private set; }
    public OrderPaymentStatus PaymentStatus { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset ConvertedAt { get; private set; }
    public MemberId ConvertedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public MemberId? ApprovedBy { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public MemberId? CancelledBy { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? InvoicedAt { get; private set; }
    public MemberId? InvoicedBy { get; private set; }
    public string? RitualCollectionSyncId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyCollection<OrderPaymentProof> PaymentProofs => _paymentProofs;
    public IReadOnlyCollection<OrderItem> Items => _items;
    public IReadOnlyCollection<OrderParty> Parties => _parties;
    public OrderParty? Billing => _parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Billing);
    public OrderParty? Shipping => _parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Shipping);

    public bool AppliesRetention =>
        (Billing is not null ? PartyWithRetention ?? false : CustomerWithRetention)
        && !BillsToFinalConsumer
        && QuotationItem.ItemizesTaxIn(Currency);

    public bool AppliesVatSurplus =>
        (Billing is not null ? PartyVatSurplus ?? false : CustomerVatSurplus) && !BillsToFinalConsumer;

    /// <summary>Copia independiente de la cotización (spec §4.4): la cotización ya pasó por
    /// <c>EnsureConvertibleToOrder</c> y <c>ConvertToOrder</c>; acá no se vuelve a validar su estado.</summary>
    public static Order FromQuotation(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        Quotation quotation,
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId convertedBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        if (quotation.BillingAccount is null)
        {
            throw new QuotationsDomainException("quotation.billing.account_required", "A quotation must have a billing account before it can be converted to an order.");
        }

        var header = new OrderHeader(
            quotation.ClientId, quotation.AdvisorId, quotation.Currency, quotation.PaymentMethod, quotation.IsRetail,
            quotation.GlobalScaleFloor, quotation.CustomerWithRetention, quotation.CustomerVatSurplus);
        var parties = new QuotationParties(
            quotation.Billing?.ToDetails(), quotation.Shipping?.ToDetails(), quotation.BillingUsesBusinessName,
            quotation.IsStorePickup, quotation.BillsToFinalConsumer, quotation.PartyWithRetention, quotation.PartyVatSurplus);
        var order = new Order(
            id, tenantId, orderNumber, OrderSource.Quotation, quotation.Id, null, header, parties, quotation.BillingAccount,
            paymentStatus, JoinNotes(quotation.Notes, notes), convertedBy, occurredAt);
        foreach (var item in quotation.Items.OrderBy(item => item.Position))
        {
            var copy = OrderItem.Create(
                OrderItemId.New(), order.Id, item.ProductId, item.Quantity, item.UnitPrice, item.DiscountPercentage,
                item.TaxPercentage, order._items.Count + 1, order.Currency, occurredAt);
            copy.ApplyDiscount(item.DiscountPercentage, item.DiscountOrigin, order.Currency, occurredAt);
            if (item.ProductCode is not null)
            {
                copy.CaptureProduct(new QuotationProductSnapshot(item.ProductCode, item.ProductName ?? string.Empty));
            }

            order._items.Add(copy);
        }

        order.EnsureCreatable();
        order.RecalculateTotals();
        order.AddProofs(proofs, convertedBy, occurredAt);
        return order;
    }

    public static Order CreateManual(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        IReadOnlyCollection<OrderLineInput> lines,
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt) =>
        Build(id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, lines, paymentStatus,
            notes, createdBy, proofs, occurredAt);

    /// <summary>Un pedido que trae una integración (Shopify y las que vengan). La elegibilidad del cliente
    /// —completo o incompleto— vive en Application: el dominio sólo exige que haya un id.</summary>
    public static Order CreateExternal(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        IReadOnlyCollection<OrderLineInput> lines,
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt,
        OrderSource source,
        string externalReference)
    {
        if (source is OrderSource.Quotation or OrderSource.Manual)
        {
            throw new QuotationsDomainException("order.order.source_invalid", "An external order needs an external source.");
        }

        if (string.IsNullOrWhiteSpace(externalReference))
        {
            throw new QuotationsDomainException("order.order.external_reference_required", "An external order needs its reference in the source system.");
        }

        return Build(id, tenantId, orderNumber, source, externalReference, header, parties, billingAccount, lines,
            paymentStatus, notes, createdBy, proofs, occurredAt);
    }

    private static Order Build(
        OrderId id, Guid tenantId, string orderNumber, OrderSource source, string? externalReference, OrderHeader header,
        QuotationParties parties, QuotationBillingAccount billingAccount, IReadOnlyCollection<OrderLineInput> lines,
        OrderPaymentStatus paymentStatus, string? notes, MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(billingAccount);
        var order = new Order(
            id, tenantId, orderNumber, source, null, externalReference, header, parties, billingAccount, paymentStatus,
            notes, createdBy, occurredAt);
        foreach (var line in lines)
        {
            order.AddItemCore(OrderItemId.New(), line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage,
                line.TaxPercentage, line.DiscountOrigin, line.Product, occurredAt);
        }

        order.EnsureCreatable();
        order.RecalculateTotals();
        order.AddProofs(proofs, createdBy, occurredAt);
        return order;
    }

    // Lo que EnsureConvertibleToOrder exige a la cotización, trasladado al pedido (spec §4.4).
    private void EnsureCreatable()
    {
        if (_items.Count == 0)
        {
            throw new QuotationsDomainException("order.order.items_required", "An order needs at least one product.");
        }

        if (Billing is { } billing && !billing.IsComplete)
        {
            throw new QuotationsDomainException("order.billing.party_incomplete", "The billing party must have all its fields filled.");
        }

        if (Shipping is { } shipping && !shipping.IsComplete)
        {
            throw new QuotationsDomainException("order.shipping.party_incomplete", "The shipping party must have all its fields filled.");
        }

        if (Billing is { } named && !string.IsNullOrWhiteSpace(named.Name) && string.IsNullOrWhiteSpace(named.IdentificationNumber))
        {
            throw new QuotationsDomainException("order.billing.identification_required", "A billing party with its own name requires an identification number.");
        }

        if (Billing is not null && (PartyWithRetention is null || PartyVatSurplus is null))
        {
            throw new QuotationsDomainException("order.billing.tax_profile_required", "The billing party must state whether it applies withholding tax and VAT surplus.");
        }
    }

    public void AddItem(
        OrderItemId itemId, Guid productId, decimal quantity, decimal unitPrice, decimal discountPercentage,
        int taxPercentage, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("Products can only be added while the order is pending.");
        AddItemCore(itemId, productId, quantity, unitPrice, discountPercentage, taxPercentage, QuotationDiscountOrigin.Own, null, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    private void AddItemCore(
        OrderItemId itemId, Guid productId, decimal quantity, decimal unitPrice, decimal discountPercentage,
        int taxPercentage, QuotationDiscountOrigin origin, QuotationProductSnapshot? product, DateTimeOffset occurredAt)
    {
        if (_items.Any(existing => existing.ProductId == productId))
        {
            throw new QuotationsDomainException("order.item.duplicate_product", "The product is already in the order.");
        }

        var item = OrderItem.Create(itemId, Id, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            _items.Count + 1, Currency, occurredAt);
        if (origin != QuotationDiscountOrigin.Own)
        {
            item.ApplyDiscount(discountPercentage, origin, Currency, occurredAt);
        }

        if (product is not null)
        {
            item.CaptureProduct(product);
        }

        _items.Add(item);
    }

    public void UpdateItemQuantity(
        OrderItemId itemId, decimal quantity, decimal discountPercentage, int taxPercentage, MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsurePending("Products can only be edited while the order is pending.");
        FindItem(itemId).UpdateQuantity(quantity, discountPercentage, taxPercentage, Currency, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    public void RemoveItem(OrderItemId itemId, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("Products can only be removed while the order is pending.");
        if (_items.Count == 1)
        {
            throw new QuotationsDomainException("order.item.last_item_required", "The last remaining item cannot be removed from the order.");
        }

        _items.Remove(FindItem(itemId));
        Touch(updatedBy, occurredAt);
    }

    public void SetIsRetail(bool isRetail, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("The pricing can only change while the order is pending.");
        IsRetail = isRetail;
        if (isRetail)
        {
            GlobalScaleFloor = null;
        }

        Touch(updatedBy, occurredAt);
    }

    public void SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("The pricing can only change while the order is pending.");
        EnsureGlobalScaleFloorAllowed(floor, IsRetail);
        GlobalScaleFloor = floor;
        Touch(updatedBy, occurredAt);
    }

    public void ApplyGroupDiscounts(IReadOnlyDictionary<OrderItemId, QuotationItemDiscount> discounts, DateTimeOffset occurredAt)
    {
        foreach (var item in _items)
        {
            if (discounts.TryGetValue(item.Id, out var discount))
            {
                item.ApplyDiscount(discount.Percentage, discount.Origin, Currency, occurredAt);
            }
        }

        RecalculateTotals();
    }

    public void RefreshCustomerTaxProfile(bool customerWithRetention, bool customerVatSurplus)
    {
        if (Status != OrderStatus.Pending) return;
        if (CustomerWithRetention == customerWithRetention && CustomerVatSurplus == customerVatSurplus) return;
        CustomerWithRetention = customerWithRetention;
        CustomerVatSurplus = customerVatSurplus;
        RecalculateTotals();
    }

    public void CaptureProductSnapshots(IReadOnlyDictionary<Guid, QuotationProductSnapshot> products)
    {
        foreach (var item in _items)
        {
            if (products.TryGetValue(item.ProductId, out var product))
            {
                item.CaptureProduct(product);
            }
        }
    }

    public OrderItem FindItem(OrderItemId itemId) =>
        _items.FirstOrDefault(item => item.Id == itemId)
            ?? throw new QuotationsDomainException("order.item.not_found", "The order item was not found.");

    public void RecalculatePaymentStatus(DateTimeOffset occurredAt)
    {
        EnsurePending("The payment status can only be recalculated on a pending order.");
        var proofsTotal = _paymentProofs.Sum(proof => proof.Amount);
        PaymentStatus = proofsTotal <= 0
            ? OrderPaymentStatus.PaymentPending
            : proofsTotal >= NetTotal
                ? OrderPaymentStatus.FullPaymentReceived
                : OrderPaymentStatus.PartialPaymentReceived;
        UpdatedAt = occurredAt;
        Version++;
    }

    private void Touch(MemberId updatedBy, DateTimeOffset occurredAt)
    {
        _ = updatedBy;
        RecalculateTotals();
        UpdatedAt = occurredAt;
        Version++;
    }

    private void RecalculateTotals()
    {
        var totals = DocumentTotals.Compute(_items.Select(item => item.Amounts), AppliesVatSurplus, AppliesRetention);
        Subtotal = totals.Subtotal;
        DiscountAmount = totals.DiscountAmount;
        TaxAmount = totals.TaxAmount;
        TaxPercentage = totals.TaxPercentage;
        Total = totals.Total;
        RetentionAmount = totals.RetentionAmount;
        NetTotal = totals.NetTotal;
    }

    private void AssignParties(QuotationParties parties)
    {
        if (parties.Billing is { } billing)
        {
            _parties.Add(OrderParty.Create(Id, QuotationPartyRole.Billing, billing));
        }

        if (!parties.IsStorePickup && parties.Shipping is { } shipping)
        {
            _parties.Add(OrderParty.Create(Id, QuotationPartyRole.Shipping, shipping));
        }
    }

    private static void EnsureBillingIsConsistent(QuotationParties parties)
    {
        if (parties.BillsToFinalConsumer && (parties.Billing is not null || parties.BillingUsesBusinessName))
        {
            throw new QuotationsDomainException("order.billing.final_consumer_conflict", "An order billed to the final consumer cannot carry its own billing party or bill to the customer's business name.");
        }
    }

    private static void EnsureGlobalScaleFloorAllowed(int? floor, bool isRetail)
    {
        if (isRetail && floor is not null)
        {
            throw new QuotationsDomainException("order.retail.floor_not_allowed", "A retail order cannot use a global scale floor.");
        }

        if (floor is { } value && value < 1)
        {
            throw new QuotationsDomainException("order.global_scale.floor_invalid", "The global scale floor must be a positive unit count.");
        }
    }

    private static string? JoinNotes(string? quotationNotes, string? orderNotes)
    {
        var first = string.IsNullOrWhiteSpace(quotationNotes) ? null : quotationNotes.Trim();
        var second = string.IsNullOrWhiteSpace(orderNotes) ? null : orderNotes.Trim();
        return (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => first + "\n\n" + second,
        };
    }

    private static string? NormalizeExternalReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var trimmed = reference.Trim();
        return trimmed.Length > ExternalReferenceMaxLength
            ? throw new QuotationsDomainException("order.order.external_reference_too_long", $"The external reference cannot exceed {ExternalReferenceMaxLength} characters.")
            : trimmed;
    }

    private static string? NormalizePaymentMethod(string? paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod)) return null;
        var trimmed = paymentMethod.Trim();
        return trimmed.Length > PaymentMethodMaxLength
            ? throw new QuotationsDomainException("order.order.payment_method_too_long", $"The payment method cannot exceed {PaymentMethodMaxLength} characters.")
            : trimmed;
    }

    // P2: hereda Quotation.Notes, que no tiene tope; sólo se recorta.
    private static string? NormalizeNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
```

`QuotationParty` gana un `ToDetails()` público idéntico al de `OrderParty` (devuelve `QuotationPartyDetails` con sus siete campos). `OrderPaymentProofInput` y `OrderPaymentProofAmountUpdate` quedan como están. El `NotesMaxLength` desaparece; quien lo usaba (`ConvertQuotationToOrderValidator`, `OrderEditsValidator`) deja de limitar las notas (Task 8 y Task 10).

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderTests"`
Expected: PASS en todos (los casos viejos reescritos y los 11 nuevos). El resto del proyecto de pruebas **no compila todavía** (handlers que llaman `Order.Create` y `RecalculatePaymentStatus(decimal, …)`): se arregla en las tareas 7 a 11. Para correr sólo este archivo mientras tanto, excluir temporalmente del `.csproj` los archivos que no compilan **no** es aceptable; en su lugar, dejar que el build falle en los handlers y arreglarlos en orden: este commit se hace con el proyecto Domain compilando (`dotnet build src/Modules/Quotations/Modules.Quotations.Domain`).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations/Modules.Quotations.Domain tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs
git commit -m "feat(quotations): Order con cabecera, partes, líneas y totales propios"
```

---

### Task 4: La cotización `Converted` queda congelada

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (quitar `AddItemAfterConversion`, `SetGlobalScaleFloorAfterConversion`, `SetIsRetailAfterConversion`, `UpdateItemQuantityAfterConversion`, `RemoveItemAfterConversion`, `CaptureProductSnapshotsAfterConversion`; inlinear `SetGlobalScaleFloorCore`/`SetIsRetailCore`/`AddItemCore` donde queden con un solo llamador)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/UpdateQuotationItem.cs:40-60`, `RemoveQuotationItem.cs:30-50` (quitar el bloque `if (quotation.Status == QuotationStatus.Converted) { … }` y la inyección de `IOrderRepository` si queda sin uso)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs`

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
    [Fact]
    public void AConvertedQuotationRejectsEveryEdit()
    {
        var quotation = NewConvertibleQuotation(); // el helper que ya usan las pruebas de conversión
        quotation.ConvertToOrder(Advisor, Now, QuotationProductSnapshot.None);
        var item = quotation.Items.First();

        Assert.Equal("quotation.quotation.not_editable",
            Assert.Throws<QuotationsDomainException>(() => quotation.UpdateItemQuantity(item.Id, 5m, 0m, 19, Advisor, Now)).Code);
        Assert.Equal("quotation.quotation.not_editable",
            Assert.Throws<QuotationsDomainException>(() => quotation.RemoveItem(item.Id, Advisor, Now)).Code);
        Assert.Equal("quotation.quotation.not_editable",
            Assert.Throws<QuotationsDomainException>(() => quotation.SetIsRetail(true, Advisor, Now)).Code);
    }
```

Y borrar de `QuotationTests` los casos que ejercen `…AfterConversion` (buscar `AfterConversion` en el archivo: cada `[Fact]` que lo llame se elimina).

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet build src/Modules/Quotations/Modules.Quotations.Domain` después de quitar los métodos.
Expected: el Domain compila; `Modules.Quotations.Application` falla en `AddOrderItems.cs`, `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `OrderItemEdits.cs` (los arregla Task 10). La prueba nueva pasa cuando el proyecto de pruebas compile (tras Task 11).

- [ ] **Step 3: Implementar**

En `Quotation.cs`:
- Eliminar los seis métodos listados arriba y sus comentarios.
- `SetGlobalScaleFloor` y `SetIsRetail` quedan con el cuerpo de sus `…Core`.
- `AddItem` conserva `AddItemCore` como privado (lo usa sólo él; se puede inlinear).

En `UpdateQuotationItem.cs` y `RemoveQuotationItem.cs`: eliminar el bloque que busca el pedido pendiente y el parámetro `IOrderRepository orderRepository` del constructor primario si no tiene más usos. El `EnsureEditable` del dominio responde `quotation.quotation.not_editable` para una `Converted`.

- [ ] **Step 4: Verificar**

Run: `dotnet build src/Modules/Quotations/Modules.Quotations.Domain --no-restore`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs src/Modules/Quotations/Modules.Quotations.Application/UpdateQuotationItem.cs src/Modules/Quotations/Modules.Quotations.Application/RemoveQuotationItem.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs
git commit -m "refactor(quotations): una cotización convertida ya no se edita a través del pedido"
```

---

### Task 5: Mapeo EF y migración de datos `AddOrderOwnData`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`ConfigureOrder` :399-490, DbSets :9-37, `OnModelCreating`)
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddOrderOwnData.cs` (generada y luego editada)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs`

**Interfaces:**
- Produces: tablas `quotations.order_items`, `quotations.order_parties`, `quotations.order_history`; columnas nuevas en `quotations.orders`; `quotation_id` nullable con índice único parcial `IX_orders_quotation`; `CHECK CK_orders_source`; DbSets `OrderItems`, `OrderParties`, `OrderHistoryEntries` (internal).

- [ ] **Step 1: Escribir la prueba de mapeo que falla**

```csharp
    // QuotationsDbContextMappingTests.cs
    [Fact]
    public void OrdersOwnTheirHeaderLinesPartiesAndHistory()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var order = context.Model.FindEntityType(typeof(Order))!;

        Assert.Equal("client_id", order.FindProperty(nameof(Order.ClientId))!.GetColumnName());
        Assert.Equal("source", order.FindProperty(nameof(Order.Source))!.GetColumnName());
        Assert.True(order.FindProperty(nameof(Order.QuotationId))!.IsNullable);
        Assert.Equal("net_total", order.FindProperty(nameof(Order.NetTotal))!.GetColumnName());
        var quotationIndex = order.GetIndexes().Single(index => index.GetDatabaseName() == "IX_orders_quotation");
        Assert.True(quotationIndex.IsUnique);
        Assert.Equal("quotation_id IS NOT NULL", quotationIndex.GetFilter());

        Assert.Equal("order_items", context.Model.FindEntityType(typeof(OrderItem))!.GetTableName());
        Assert.Equal("order_parties", context.Model.FindEntityType(typeof(OrderParty))!.GetTableName());
        Assert.Equal("order_history", context.Model.FindEntityType(typeof(OrderHistoryEntry))!.GetTableName());
        var billing = order.FindNavigation(nameof(Order.BillingAccount))!;
        Assert.True(billing.ForeignKey.IsOwnership);
        Assert.True(billing.ForeignKey.IsRequired);
    }
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~QuotationsDbContextMappingTests.OrdersOwnTheir"`
Expected: FAIL (`FindProperty("ClientId")` devuelve null, `FindEntityType(typeof(OrderItem))` devuelve null). Si el proyecto aún no compila por los handlers, correr después de Task 11 y anotar la evidencia ahí.

- [ ] **Step 3: Implementar el mapeo**

En `QuotationsDbContext.cs`, DbSets nuevos junto a los de pedidos:

```csharp
    internal DbSet<OrderItem> OrderItems => Set<OrderItem>();
    internal DbSet<OrderParty> OrderParties => Set<OrderParty>();
    public DbSet<OrderHistoryEntry> OrderHistoryEntries => Set<OrderHistoryEntry>();
```

En `OnModelCreating`, después de `ConfigureOrderPaymentProof(modelBuilder);`: `ConfigureOrderItem(modelBuilder); ConfigureOrderParty(modelBuilder); ConfigureOrderHistoryEntry(modelBuilder);`.

`ConfigureOrder` gana, antes de los índices:

```csharp
        order.Property(value => value.QuotationId)
            .HasColumnName("quotation_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new QuotationId(value.Value) : null);
        order.Property(value => value.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(20);
        order.Property(value => value.ExternalReference).HasColumnName("external_reference").HasMaxLength(Order.ExternalReferenceMaxLength);
        order.Property(value => value.ClientId).HasColumnName("client_id");
        order.Property(value => value.AdvisorId).HasColumnName("advisor_id").HasConversion(id => id.Value, value => new MemberId(value));
        order.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        order.Property(value => value.PaymentMethod).HasColumnName("payment_method").HasMaxLength(Order.PaymentMethodMaxLength);
        order.Property(value => value.IsRetail).HasColumnName("is_retail");
        order.Property(value => value.GlobalScaleFloor).HasColumnName("global_scale_floor");
        order.Property(value => value.BillingUsesBusinessName).HasColumnName("billing_uses_business_name");
        order.Property(value => value.IsStorePickup).HasColumnName("is_store_pickup");
        order.Property(value => value.BillsToFinalConsumer).HasColumnName("bills_to_final_consumer");
        order.Property(value => value.PartyWithRetention).HasColumnName("party_with_retention");
        order.Property(value => value.PartyVatSurplus).HasColumnName("party_vat_surplus");
        order.Property(value => value.CustomerWithRetention).HasColumnName("customer_with_retention");
        order.Property(value => value.CustomerVatSurplus).HasColumnName("customer_vat_surplus");
        order.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        order.Property(value => value.TaxPercentage).HasColumnName("tax_percentage").HasPrecision(5, 2);
        order.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        order.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
        order.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        order.Property(value => value.RetentionAmount).HasColumnName("retention_amount").HasPrecision(14, 2);
        order.Property(value => value.NetTotal).HasColumnName("net_total").HasPrecision(14, 2);
        order.Property(value => value.Notes).HasColumnName("notes").HasColumnType("text");
        order.OwnsOne(value => value.BillingAccount, billing =>
        {
            billing.Property(value => value.CompanyId).HasColumnName("billing_company_id");
            billing.Property(value => value.BankName).HasColumnName("billing_bank_name").HasMaxLength(QuotationBillingAccount.BankNameMaxLength);
            billing.Property(value => value.AccountNumber).HasColumnName("billing_account_number").HasMaxLength(QuotationBillingAccount.AccountNumberMaxLength);
            billing.Property(value => value.Currency).HasColumnName("billing_account_currency").HasMaxLength(QuotationBillingAccount.CurrencyLength);
        });
        order.Navigation(value => value.BillingAccount).IsRequired();
        order.Navigation(value => value.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        order.Navigation(value => value.Parties).UsePropertyAccessMode(PropertyAccessMode.Field);
        order.ToTable("orders", "quotations", table =>
            table.HasCheckConstraint("CK_orders_source", "source IN ('Quotation','Manual','Shopify')"));
```

El `Notes` anterior (`HasMaxLength(Order.NotesMaxLength)`) se reemplaza por la línea de `text`. El índice `IX_orders_quotation` pasa a:

```csharp
        order.HasIndex(value => value.QuotationId)
            .IsUnique()
            .HasFilter("quotation_id IS NOT NULL")
            .HasDatabaseName("IX_orders_quotation");
        order.HasIndex(value => new { value.TenantId, value.ClientId }).HasDatabaseName("IX_orders_tenant_client");
        order.HasIndex(value => new { value.TenantId, value.AdvisorId }).HasDatabaseName("IX_orders_tenant_advisor");
        order.HasIndex(value => new { value.TenantId, value.Source }).HasDatabaseName("IX_orders_tenant_source");
```

La FK a `quotations` se conserva (`HasForeignKey(value => value.QuotationId)` acepta nullable con `IsRequired(false)`).

Las tres configuraciones nuevas calcan `ConfigureQuotationItem`, `ConfigureQuotationParty` y `ConfigureQuotationHistoryEntry` cambiando entidad, tabla, id y FK:

```csharp
    private static void ConfigureOrderItem(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<OrderItem>();
        item.ToTable("order_items", "quotations");
        item.HasKey(value => value.Id);
        item.Property(value => value.Id).HasColumnName("id").HasConversion(id => id.Value, value => new OrderItemId(value)).ValueGeneratedNever();
        item.Property(value => value.OrderId).HasColumnName("order_id").HasConversion(id => id.Value, value => new OrderId(value));
        item.Property(value => value.ProductId).HasColumnName("product_id");
        item.Property(value => value.ProductCode).HasColumnName("product_code").HasMaxLength(QuotationProductSnapshot.CodeMaxLength);
        item.Property(value => value.ProductName).HasColumnName("product_name").HasMaxLength(QuotationProductSnapshot.NameMaxLength);
        item.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(10, 2);
        item.Property(value => value.UnitPrice).HasColumnName("unit_price").HasPrecision(14, 2);
        item.Property(value => value.DiscountOrigin).HasColumnName("discount_origin").HasConversion<string>().HasMaxLength(20).IsRequired();
        item.Property(value => value.DiscountPercentage).HasColumnName("discount_percentage").HasPrecision(5, 2);
        item.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
        item.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        item.Property(value => value.TaxPercentage).HasColumnName("tax_percentage");
        item.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        item.Property(value => value.Position).HasColumnName("position");
        item.Property(value => value.CreatedAt).HasColumnName("created_at");
        item.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        item.Ignore(value => value.Amounts);
        item.HasIndex(value => value.OrderId).HasDatabaseName("IX_order_items_order");
        item.HasOne<Order>().WithMany(order => order.Items).HasForeignKey(value => value.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureOrderParty(ModelBuilder modelBuilder)
    {
        var party = modelBuilder.Entity<OrderParty>();
        party.ToTable("order_parties", "quotations");
        party.HasKey(value => value.Id);
        party.Property(value => value.Id).HasColumnName("id").HasConversion(id => id.Value, value => new OrderPartyId(value)).ValueGeneratedNever();
        party.Property(value => value.OrderId).HasColumnName("order_id").HasConversion(id => id.Value, value => new OrderId(value));
        party.Property(value => value.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20);
        party.Property(value => value.Name).HasColumnName("name").HasMaxLength(QuotationPartyDetails.NameMaxLength);
        party.Property(value => value.IdentificationNumber).HasColumnName("identification_number").HasMaxLength(QuotationPartyDetails.IdentificationNumberMaxLength);
        party.Property(value => value.Phone).HasColumnName("phone").HasMaxLength(QuotationPartyDetails.PhoneMaxLength);
        party.Property(value => value.Email).HasColumnName("email").HasMaxLength(QuotationPartyDetails.EmailMaxLength);
        party.Property(value => value.Address).HasColumnName("address").HasMaxLength(QuotationPartyDetails.AddressMaxLength);
        party.Property(value => value.DepartmentId).HasColumnName("department_id");
        party.Property(value => value.CityId).HasColumnName("city_id");
        party.HasIndex(value => new { value.OrderId, value.Role }).IsUnique().HasDatabaseName("IX_order_parties_order_role");
        party.HasOne<Order>().WithMany(order => order.Parties).HasForeignKey(value => value.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureOrderHistoryEntry(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<OrderHistoryEntry>();
        entry.ToTable("order_history", "quotations");
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id).HasColumnName("id").HasConversion(id => id.Value, value => new OrderHistoryEntryId(value)).ValueGeneratedNever();
        entry.Property(value => value.OrderId).HasColumnName("order_id").HasConversion(id => id.Value, value => new OrderId(value));
        entry.Property(value => value.EventType).HasColumnName("event_type").HasConversion<string>().HasMaxLength(50);
        entry.Property(value => value.EventAt).HasColumnName("event_at");
        entry.Property(value => value.MemberId).HasColumnName("member_id").HasConversion(
            id => id.HasValue ? id.Value.Value : (Guid?)null, value => value.HasValue ? new MemberId(value.Value) : null);
        entry.Property(value => value.Details).HasColumnName("details").HasMaxLength(OrderHistoryEntry.DetailsMaxLength);
        entry.Property(value => value.CreatedAt).HasColumnName("created_at");
        entry.HasIndex(value => new { value.OrderId, value.EventAt }).HasDatabaseName("IX_order_history_order_event_at");
        entry.HasOne<Order>().WithMany().HasForeignKey(value => value.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
```

- [ ] **Step 4: Generar la migración y editarla**

Run: `dotnet ef migrations add AddOrderOwnData --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`

EF genera `AddColumn` con `nullable: false` y `defaultValue` para las columnas nuevas. **Editar el `Up`** para que quede en este orden (las columnas nacen nulas, se llenan, se vuelven `NOT NULL` con guardas; las tablas nuevas y el índice parcial al final):

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Columnas nuevas, nulas por ahora.
            migrationBuilder.AddColumn<Guid>(name: "client_id", schema: "quotations", table: "orders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "advisor_id", schema: "quotations", table: "orders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "billing_company_id", schema: "quotations", table: "orders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "billing_bank_name", schema: "quotations", table: "orders", type: "character varying(120)", maxLength: 120, nullable: true);
            migrationBuilder.AddColumn<string>(name: "billing_account_number", schema: "quotations", table: "orders", type: "character varying(32)", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "billing_account_currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: true);
            migrationBuilder.AddColumn<string>(name: "payment_method", schema: "quotations", table: "orders", type: "character varying(50)", maxLength: 50, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "is_retail", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<int>(name: "global_scale_floor", schema: "quotations", table: "orders", type: "integer", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "billing_uses_business_name", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "is_store_pickup", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "bills_to_final_consumer", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "party_with_retention", schema: "quotations", table: "orders", type: "boolean", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "party_vat_surplus", schema: "quotations", table: "orders", type: "boolean", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "customer_with_retention", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "customer_vat_surplus", schema: "quotations", table: "orders", type: "boolean", nullable: false, defaultValue: false);
            foreach (var column in new[] { "subtotal", "tax_amount", "discount_amount", "total", "retention_amount", "net_total" })
            {
                migrationBuilder.AddColumn<decimal>(name: column, schema: "quotations", table: "orders", type: "numeric(14,2)", precision: 14, scale: 2, nullable: false, defaultValue: 0m);
            }

            migrationBuilder.AddColumn<decimal>(name: "tax_percentage", schema: "quotations", table: "orders", type: "numeric(5,2)", precision: 5, scale: 2, nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<string>(name: "source", schema: "quotations", table: "orders", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Quotation");
            migrationBuilder.AddColumn<string>(name: "external_reference", schema: "quotations", table: "orders", type: "character varying(64)", maxLength: 64, nullable: true);
            migrationBuilder.AlterColumn<string>(name: "notes", schema: "quotations", table: "orders", type: "text", nullable: true, oldClrType: typeof(string), oldType: "character varying(500)", oldMaxLength: 500, oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "quotation_id", schema: "quotations", table: "orders", type: "uuid", nullable: true, oldClrType: typeof(Guid), oldType: "uuid");

            // 2. Tablas nuevas: las tres sentencias CreateTable que EF generó para order_items, order_parties y
            //    order_history, con sus índices (IX_order_items_order, IX_order_parties_order_role, IX_order_history_order_event_at)
            //    y sus FKs a orders con ON DELETE CASCADE, se dejan aquí tal cual salieron del scaffold.

            // 3. Guardas: un pedido que hoy no pudo existir no se migra a ciegas.
            migrationBuilder.Sql("""
                DO $$
                DECLARE bad_orders text;
                BEGIN
                    SELECT string_agg(o.id::text, ', ') INTO bad_orders
                    FROM quotations.orders o
                    LEFT JOIN quotations.quotations q ON q.id = o.quotation_id
                    WHERE q.id IS NULL
                       OR q.billing_company_id IS NULL
                       OR NOT EXISTS (SELECT 1 FROM quotations.quotation_items i WHERE i.quotation_id = q.id);
                    IF bad_orders IS NOT NULL THEN
                        RAISE EXCEPTION 'AddOrderOwnData: these orders have no quotation, no billing account or no lines and must be fixed by hand first: %', bad_orders;
                    END IF;
                END $$;
                """);

            // 4. Copia de la cabecera.
            migrationBuilder.Sql("""
                UPDATE quotations.orders o
                SET client_id = q.client_id,
                    advisor_id = q.advisor_id,
                    currency = q.currency,
                    billing_company_id = q.billing_company_id,
                    billing_bank_name = q.billing_bank_name,
                    billing_account_number = q.billing_account_number,
                    billing_account_currency = q.billing_account_currency,
                    payment_method = q.payment_method,
                    is_retail = q.is_retail,
                    global_scale_floor = q.global_scale_floor,
                    billing_uses_business_name = q.billing_uses_business_name,
                    is_store_pickup = q.is_store_pickup,
                    bills_to_final_consumer = q.bills_to_final_consumer,
                    party_with_retention = q.party_with_retention,
                    party_vat_surplus = q.party_vat_surplus,
                    customer_with_retention = q.customer_with_retention,
                    customer_vat_surplus = q.customer_vat_surplus,
                    subtotal = q.subtotal,
                    tax_percentage = q.tax_percentage,
                    tax_amount = q.tax_amount,
                    discount_amount = q.discount_amount,
                    total = q.total,
                    retention_amount = q.retention_amount,
                    net_total = q.net_total,
                    notes = CASE
                        WHEN q.notes IS NULL OR btrim(q.notes) = '' THEN o.notes
                        WHEN o.notes IS NULL OR btrim(o.notes) = '' THEN q.notes
                        ELSE q.notes || E'\n\n' || o.notes END,
                    source = 'Quotation'
                FROM quotations.quotations q
                WHERE q.id = o.quotation_id;
                """);

            // 5. Copia de líneas, partes y el evento inicial del historial.
            migrationBuilder.Sql("""
                INSERT INTO quotations.order_items (
                    id, order_id, product_id, product_code, product_name, quantity, unit_price, discount_origin,
                    discount_percentage, discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
                SELECT gen_random_uuid(), o.id, i.product_id, i.product_code, i.product_name, i.quantity, i.unit_price,
                       i.discount_origin, i.discount_percentage, i.discount_amount, i.subtotal, i.tax_percentage, i.tax_amount,
                       i.position, i.created_at, i.updated_at
                FROM quotations.orders o
                JOIN quotations.quotation_items i ON i.quotation_id = o.quotation_id;

                INSERT INTO quotations.order_parties (
                    id, order_id, role, name, identification_number, phone, email, address, department_id, city_id)
                SELECT gen_random_uuid(), o.id, p.role, p.name, p.identification_number, p.phone, p.email, p.address,
                       p.department_id, p.city_id
                FROM quotations.orders o
                JOIN quotations.quotation_parties p ON p.quotation_id = o.quotation_id;

                INSERT INTO quotations.order_history (id, order_id, event_type, event_at, member_id, details, created_at)
                SELECT gen_random_uuid(), o.id, 'Created', o.converted_at, o.converted_by,
                       'Creado desde la cotización ' || q.quotation_number || '.', o.converted_at
                FROM quotations.orders o
                JOIN quotations.quotations q ON q.id = o.quotation_id;

                UPDATE quotations.quotations q
                SET status = 'Converted'
                WHERE status <> 'Converted' AND EXISTS (SELECT 1 FROM quotations.orders o WHERE o.quotation_id = q.id);
                """);

            // 6. Ahora sí, obligatorias.
            migrationBuilder.AlterColumn<Guid>(name: "client_id", schema: "quotations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "advisor_id", schema: "quotations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: false, oldClrType: typeof(string), oldType: "character varying(3)", oldMaxLength: 3, oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "billing_company_id", schema: "quotations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "billing_bank_name", schema: "quotations", table: "orders", type: "character varying(120)", maxLength: 120, nullable: false, oldClrType: typeof(string), oldType: "character varying(120)", oldMaxLength: 120, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "billing_account_number", schema: "quotations", table: "orders", type: "character varying(32)", maxLength: 32, nullable: false, oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "billing_account_currency", schema: "quotations", table: "orders", type: "character varying(3)", maxLength: 3, nullable: false, oldClrType: typeof(string), oldType: "character varying(3)", oldMaxLength: 3, oldNullable: true);

            // 7. Índices y check.
            migrationBuilder.DropIndex(name: "IX_orders_quotation", schema: "quotations", table: "orders");
            migrationBuilder.CreateIndex(name: "IX_orders_quotation", schema: "quotations", table: "orders", column: "quotation_id", unique: true, filter: "quotation_id IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_orders_tenant_client", schema: "quotations", table: "orders", columns: new[] { "tenant_id", "client_id" });
            migrationBuilder.CreateIndex(name: "IX_orders_tenant_advisor", schema: "quotations", table: "orders", columns: new[] { "tenant_id", "advisor_id" });
            migrationBuilder.CreateIndex(name: "IX_orders_tenant_source", schema: "quotations", table: "orders", columns: new[] { "tenant_id", "source" });
            migrationBuilder.AddCheckConstraint(name: "CK_orders_source", schema: "quotations", table: "orders", sql: "source IN ('Quotation','Manual','Shopify')");
        }
```

El `Down` borra las tres tablas, el check, los tres índices nuevos, vuelve `IX_orders_quotation` a único sin filtro (fallará si hay pedidos sin cotización: es la intención, la migración es irreversible por diseño), devuelve `quotation_id` a `NOT NULL`, `notes` a `varchar(500)` y quita las columnas. Dejar lo que EF genera para el `Down` y agregar sólo el `DropCheckConstraint` y el índice parcial.

Revisar el `Designer.cs` y el `QuotationsDbContextModelSnapshot.cs` generados: deben declarar el filtro `quotation_id IS NOT NULL` en `IX_orders_quotation` y la propiedad `Notes` como `text`.

- [ ] **Step 5: Escribir la prueba de migración que falla**

Agregar a `OrdersMigrationTests.cs` (sembrando con el esquema **anterior** a esta migración, como hace `TheRenameKeepsEveryOrderProofAndCounterUnderTheNewNames`):

```csharp
    private const string LastMigrationBeforeOwnData = "20261009045958_AddQuotationSettings";

    private const string QuotationWithLinesSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_at, version, billing_company_id, billing_bank_name, billing_account_number, billing_account_currency)
        VALUES (
            '01900000-0000-7000-8000-00000000c004', '{TenantId}', 'COT-2026-0007', '01900000-0000-7000-8000-00000000c007',
            '{MemberId}', 'Sent', '2026-09-09T15:00:00Z', '2026-10-09', 'COP', 'Transferencia',
            100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, true, false, 2500.00, 116500.00,
            'Entregar en portería', '{MemberId}', '2026-09-09T15:00:00Z', 2, '01900000-0000-7000-8000-00000000c008',
            'Bancolombia', '7542', 'COP');
        INSERT INTO quotations.quotation_items (
            id, quotation_id, product_id, product_code, product_name, quantity, unit_price, discount_origin, discount_percentage,
            discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        VALUES (
            '01900000-0000-7000-8000-00000000c009', '01900000-0000-7000-8000-00000000c004', '01900000-0000-7000-8000-00000000c00a',
            'ABC', 'Aceite', 10.00, 11900.00, 'Own', 0.00, 0.00, 100000.00, 19, 19000.00, 1, '2026-09-09T15:00:00Z', '2026-09-09T15:00:00Z');
        INSERT INTO quotations.quotation_parties (id, quotation_id, role, name, phone, email, address, department_id, city_id)
        VALUES (
            '01900000-0000-7000-8000-00000000c00b', '01900000-0000-7000-8000-00000000c004', 'Shipping', 'Bodega', '3001234567',
            'bodega@x.co', 'Cl 1', '01900000-0000-7000-8000-00000000c00c', '01900000-0000-7000-8000-00000000c00d');
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        VALUES (
            '{OrderId}', '{TenantId}', 'PED-2026-0001', '01900000-0000-7000-8000-00000000c004', 'Pending', 'PaymentPending',
            'Pago verificado', '2026-09-10T15:00:00Z', '{MemberId}', NULL, NULL, NULL, '2026-09-10T15:00:00Z',
            '2026-09-10T15:00:00Z', 1);
        RESET session_replication_role;
        """;

    [Fact]
    public async Task AddOrderOwnDataCopiesTheQuotationIntoTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationWithLinesSql);

        await migrator.MigrateAsync(MigrationId(context, "_AddOrderOwnData"), TestContext.Current.CancellationToken);

        Assert.Equal(Guid.Parse("01900000-0000-7000-8000-00000000c007"), await ScalarAsync<Guid>(
            connectionString, $"SELECT client_id FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal(116500.00m, await ScalarAsync<decimal>(
            connectionString, $"SELECT net_total FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal("Entregar en portería\n\nPago verificado", await ScalarAsync<string>(
            connectionString, $"SELECT notes FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal("Quotation", await ScalarAsync<string>(
            connectionString, $"SELECT source FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM quotations.order_items WHERE order_id = '{OrderId}' AND product_code = 'ABC'"));
        Assert.Equal("Bodega", await ScalarAsync<string>(
            connectionString, $"SELECT name FROM quotations.order_parties WHERE order_id = '{OrderId}' AND role = 'Shipping'"));
        Assert.Equal("Created", await ScalarAsync<string>(
            connectionString, $"SELECT event_type FROM quotations.order_history WHERE order_id = '{OrderId}'"));
        Assert.Equal("Converted", await ScalarAsync<string>(
            connectionString, "SELECT status FROM quotations.quotations WHERE id = '01900000-0000-7000-8000-00000000c004'"));
    }

    [Fact]
    public async Task AddOrderOwnDataRefusesAnOrderWhoseQuotationHasNoLines()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationWithLinesSql);
        await ExecuteAsync(connectionString, "DELETE FROM quotations.quotation_items");

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync(MigrationId(context, "_AddOrderOwnData"), TestContext.Current.CancellationToken));

        Assert.Contains(OrderId, error.MessageText, StringComparison.Ordinal);
    }
```

- [ ] **Step 6: Correr las pruebas de migración**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrdersMigrationTests"`
Expected: los dos nuevos PASS junto a los dos del rename (requiere que la solución compile: hacer este paso al cerrar Task 11 si los handlers aún no compilan, y anotar la evidencia aquí).

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs
git commit -m "feat(quotations): tablas y columnas propias del pedido con migración de datos"
```

---

### Task 6: Repositorio de pedidos sin `OrderWithQuotation`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubOrderListRepository`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderListApiTests.cs` (se amplía en Task 9)

**Interfaces:**
- Produces (reemplaza las firmas que devolvían `OrderWithQuotation`):
  - `Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status, OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber, OrderSource? source, int page, int pageSize, CancellationToken)`
  - `Task<bool> AnyForExportAsync(… mismos filtros, con OrderSource? source …)`
  - `Task<IReadOnlyList<Order>> ListForExportAsync(… mismos filtros …, OrderExportCursor? after, int limit, CancellationToken)`
  - `Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderItem>>> ListItemsForExportAsync(Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken)`
  - `Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderParty>>> ListPartiesForExportAsync(Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken)`
  - `Task<IReadOnlyList<OrderHistoryEntry>> ListHistoryAsync(Guid tenantId, OrderId orderId, CancellationToken)`; `void AddHistoryEntry(OrderHistoryEntry entry)`
  - `FindByIdAsync`, `FindUntrackedByIdAsync`, `FindByQuotationIdAsync` incluyen `Items`, `Parties` y `PaymentProofs`; `FindByQuotationIdsAsync` se conserva.
- `OrderWithQuotation` **se elimina**.

- [ ] **Step 1: Cambiar el puerto**

```csharp
// IOrderRepository.cs: quitar `OrderWithQuotation`; firmas nuevas
public interface IOrderRepository
{
    Task<Order?> FindByQuotationIdAsync(Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);
    Task<Order?> FindUntrackedByIdAsync(Guid tenantId, OrderId orderId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(Guid tenantId, IReadOnlyCollection<QuotationId> quotationIds, CancellationToken cancellationToken);
    Task<Order?> FindByIdAsync(Guid tenantId, OrderId orderId, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source, int page, int pageSize, CancellationToken cancellationToken);
    Task<bool> AnyForExportAsync(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source, CancellationToken cancellationToken);
    Task<IReadOnlyList<Order>> ListForExportAsync(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source, OrderExportCursor? after, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderItem>>> ListItemsForExportAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderParty>>> ListPartiesForExportAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderHistoryEntry>> ListHistoryAsync(Guid tenantId, OrderId orderId, CancellationToken cancellationToken);
    Task<bool> IsPaymentProofFileInUseAsync(Guid fileId, OrderPaymentProofId? exceptProofId, CancellationToken cancellationToken);
    void Add(Order order);
    void AddHistoryEntry(OrderHistoryEntry entry);
}
```

- [ ] **Step 2: Implementar en `OrderRepository`**

```csharp
    private static IQueryable<Order> WithAggregate(IQueryable<Order> orders) =>
        orders.Include(order => order.PaymentProofs).Include(order => order.Items).Include(order => order.Parties);

    public Task<Order?> FindByQuotationIdAsync(Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders).SingleOrDefaultAsync(
            order => order.TenantId == tenantId && order.QuotationId == quotationId, cancellationToken);

    public Task<Order?> FindUntrackedByIdAsync(Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders.AsNoTracking()).SingleOrDefaultAsync(
            order => order.TenantId == tenantId && order.Id == orderId, cancellationToken);

    public Task<Order?> FindByIdAsync(Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders).SingleOrDefaultAsync(
            order => order.TenantId == tenantId && order.Id == orderId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(
        Guid tenantId, IReadOnlyCollection<QuotationId> quotationIds, CancellationToken cancellationToken)
    {
        if (quotationIds.Count == 0) return new Dictionary<Guid, Order>();
        var ids = quotationIds.Select(id => (QuotationId?)id).ToArray();
        var orders = await dbContext.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.QuotationId != null && ids.Contains(order.QuotationId))
            .ToListAsync(cancellationToken);
        return orders.ToDictionary(order => order.QuotationId!.Value.Value);
    }

    public async Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source, int page, int pageSize, CancellationToken cancellationToken)
    {
        var orders = Filtered(tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber, source);
        var total = await orders.CountAsync(cancellationToken);
        var items = await orders
            .OrderByDescending(order => order.ConvertedAt).ThenByDescending(order => order.OrderNumber)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<bool> AnyForExportAsync(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source, CancellationToken cancellationToken) =>
        Filtered(tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber, source)
            .AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> ListForExportAsync(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source, OrderExportCursor? after, int limit, CancellationToken cancellationToken)
    {
        var orders = Filtered(tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber, source);
        if (after is not null)
        {
            var convertedAt = after.ConvertedAt;
            var number = after.OrderNumber;
            orders = orders.Where(order => EF.Functions.LessThan(
                ValueTuple.Create(order.ConvertedAt, order.OrderNumber), ValueTuple.Create(convertedAt, number)));
        }

        return await orders
            .OrderByDescending(order => order.ConvertedAt).ThenByDescending(order => order.OrderNumber)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Order> Filtered(
        Guid tenantId, Guid? clientId, IReadOnlyCollection<Guid>? clientIds, MemberId? advisorId, OrderStatus? status,
        OrderPaymentStatus? paymentStatus, DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore, string? orderNumber,
        OrderSource? source)
    {
        var orders = dbContext.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);
        if (status is { } orderStatus) orders = orders.Where(order => order.Status == orderStatus);
        if (paymentStatus is { } orderPaymentStatus) orders = orders.Where(order => order.PaymentStatus == orderPaymentStatus);
        if (source is { } orderSource) orders = orders.Where(order => order.Source == orderSource);
        var orderNumberPattern = LikePattern(orderNumber);
        if (orderNumberPattern is not null)
        {
            orders = orders.Where(order => EF.Functions.ILike(order.OrderNumber, orderNumberPattern, LikeEscapeCharacter));
        }

        if (convertedFrom is { } from) orders = orders.Where(order => order.ConvertedAt >= from);
        if (convertedBefore is { } before) orders = orders.Where(order => order.ConvertedAt < before);
        if (clientId is { } client) orders = orders.Where(order => order.ClientId == client);
        if (clientIds is not null) orders = orders.Where(order => clientIds.Contains(order.ClientId));
        if (advisorId is { } advisor) orders = orders.Where(order => order.AdvisorId == advisor);
        return orders;
    }

    public async Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderItem>>> ListItemsForExportAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0) return new Dictionary<OrderId, IReadOnlyList<OrderItem>>();
        var ids = orderIds.ToArray();
        var items = await (
                from item in dbContext.OrderItems.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on item.OrderId equals order.Id
                where order.TenantId == tenantId && ids.Contains(order.Id)
                orderby item.Position
                select item)
            .ToListAsync(cancellationToken);
        return items.GroupBy(item => item.OrderId).ToDictionary(group => group.Key, group => (IReadOnlyList<OrderItem>)group.ToArray());
    }

    public async Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderParty>>> ListPartiesForExportAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0) return new Dictionary<OrderId, IReadOnlyList<OrderParty>>();
        var ids = orderIds.ToArray();
        var parties = await (
                from party in dbContext.OrderParties.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on party.OrderId equals order.Id
                where order.TenantId == tenantId && ids.Contains(order.Id)
                select party)
            .ToListAsync(cancellationToken);
        return parties.GroupBy(party => party.OrderId).ToDictionary(group => group.Key, group => (IReadOnlyList<OrderParty>)group.ToArray());
    }

    public async Task<IReadOnlyList<OrderHistoryEntry>> ListHistoryAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        await (
                from entry in dbContext.OrderHistoryEntries.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on entry.OrderId equals order.Id
                where order.TenantId == tenantId && order.Id == orderId
                orderby entry.EventAt descending, entry.CreatedAt descending
                select entry)
            .ToListAsync(cancellationToken);

    public void AddHistoryEntry(OrderHistoryEntry entry) => dbContext.OrderHistoryEntries.Add(entry);
```

`ListPaymentProofsForExportAsync`, `IsPaymentProofFileInUseAsync`, `Add`, `LikePattern` y `EscapeLikeWildcards` quedan como están.

- [ ] **Step 3: Actualizar el doble de pruebas**

`StubOrderListRepository(params Order[] rows)`: cada método que leía `row.Quotation` lee ahora del pedido (`FindByQuotationIdAsync` filtra `row.QuotationId == quotationId`; `FindByQuotationIdsAsync` usa `row.QuotationId!.Value`; `SearchAsync`/`ListForExportAsync` devuelven `rows` y anotan los filtros incluido `source`; los dos métodos nuevos de ítems y partes devuelven `rows.ToDictionary(order => order.Id, order => (IReadOnlyList<OrderItem>)order.Items.ToArray())` y lo mismo con `Parties`; `ListHistoryAsync` devuelve `[]`; `AddHistoryEntry` guarda en una lista `History` pública). `RecordedOrderExportSearch` gana `OrderSource? Source`.

- [ ] **Step 4: Verificar**

Run: `dotnet build src/Modules/Quotations/Modules.Quotations.Infrastructure --no-restore`
Expected: Infrastructure compila. Application sigue fallando en los handlers que arreglan las tareas 7-11.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs
git commit -m "refactor(quotations): el repositorio de pedidos filtra y carga del pedido"
```

---

### Task 7: Eventos de pedido y conversión como copia

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationLifecycleEventPublisher.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrderChangeSummary.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs`, `CancelOrder.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationLifecycleEventsApiTests.cs`, `OrderApiTests.cs`

**Interfaces:**
- Produces: `IQuotationLifecycleEventPublisher.PublishOrderCreated(Order order, DateTimeOffset occurredAt)`; evento `quotations.order.created.v1` con payload `{ tenantId, orderId, orderNumber, clientId, advisorMemberId, total, currency, source, quotationId (nullable), occurredAt }`, correlación = id del pedido; `quotations.order.cancelled.v1` pasa a `{ tenantId, orderId, quotationId (nullable), source, occurredAt }`, correlación = id del pedido. `OrderChangeSummary.CreatedFromQuotation(string quotationNumber)`, `CreatedManually()`, `CreatedFromExternal(OrderSource source, string reference)`, `ItemAdded/ItemQuantityChanged/ItemRemoved` (reusan los textos de `QuotationChangeSummary`), `Approved()`, `Cancelled(string reason)`, `Invoiced()`, `InvoicingReverted()`, `RetailChanged(bool)`, `GlobalScaleFloorChanged(int?)`, `NotesChanged()`, `PaymentProofsChanged(int added, int corrected, int removed)`.

- [ ] **Step 1: Escribir la prueba que falla**

En `QuotationLifecycleEventsApiTests.cs` (usa `OutboxMessagesAsync` del harness y `ConvertAsync` del propio archivo):

```csharp
    [Fact]
    public async Task ConvertingPublishesConvertedAndOrderCreatedWithTheOrderAsCorrelation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var order = await ConvertAsync(client, tenantId, quotation.Id);

        var messages = await OutboxMessagesAsync(factory, tenantId);
        var created = Assert.Single(messages, message => message.EventName == "quotations.order.created.v1");
        Assert.Equal(order.Id.ToString(), created.CorrelationId);
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal("Quotation", payload.RootElement.GetProperty("source").GetString());
        Assert.Equal(quotation.Id, payload.RootElement.GetProperty("quotationId").GetGuid());
        Assert.Equal(order.OrderNumber, payload.RootElement.GetProperty("orderNumber").GetString());
        Assert.Single(messages, message => message.EventName == "quotations.quotation.converted.v1");
    }
```

Y en `OrderApiTests.cs`:

```csharp
    [Fact]
    public async Task ConvertingCopiesTheQuotationAndFreezesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", "Pago verificado", []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal((quotation.Id, "Quotation", quotation.Total, quotation.NetTotal), (order.QuotationId, order.Source, order.Total, order.NetTotal));
        Assert.Equal(clientId, order.ClientId);
        Assert.Single(order.Items);

        // La cotización queda congelada: editar una línea responde 422 not_editable.
        var edit = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{quotation.Items.Single().Id}",
            new UpdateQuotationItemRequest(5m), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        var problem = await edit.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.quotation.not_editable", problem!.Extensions["code"]!.ToString());
    }
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~ConvertingPublishesConvertedAndOrderCreated|FullyQualifiedName~ConvertingCopiesTheQuotation"`
Expected: no compila (`OrderResponse` no tiene `Source`; se agrega en Task 8) o FAIL por evento ausente. Dejar la evidencia cuando compile (fin de Task 8).

- [ ] **Step 3: Implementar**

Puerto y publicador:

```csharp
// IQuotationLifecycleEventPublisher.cs
public interface IQuotationLifecycleEventPublisher
{
    void PublishSent(Quotation quotation, DateTimeOffset occurredAt);
    void PublishConverted(Quotation quotation, Order order, DateTimeOffset occurredAt);
    void PublishOrderCreated(Order order, DateTimeOffset occurredAt);
    void PublishOrderCancelled(Order order, DateTimeOffset occurredAt);
}

// QuotationLifecycleEventPublisher.cs (Infrastructure)
    internal const string OrderCreatedEventName = "quotations.order.created.v1";

    public void PublishOrderCreated(Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Add(OrderCreatedEventName, order.Id.Value, new OrderCreatedPayload(
            order.TenantId, order.Id.Value, order.OrderNumber, order.ClientId, order.AdvisorId.Value, order.Total, order.Currency,
            order.Source.ToString(), order.QuotationId?.Value, occurredAt), occurredAt);
    }

    public void PublishOrderCancelled(Order order, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        Add(OrderCancelledEventName, order.Id.Value, new OrderCancelledPayload(
            order.TenantId, order.Id.Value, order.QuotationId?.Value, order.Source.ToString(), occurredAt), occurredAt);
    }

    // Add(...) pasa a recibir `Guid correlationId` en vez de `Guid quotationId`; PublishSent/PublishConverted siguen
    // pasando quotation.Id.Value.

    private sealed record OrderCreatedPayload(
        Guid tenantId, Guid orderId, string orderNumber, Guid clientId, Guid advisorMemberId, decimal total, string currency,
        string source, Guid? quotationId, DateTimeOffset occurredAt);

    private sealed record OrderCancelledPayload(Guid tenantId, Guid orderId, Guid? quotationId, string source, DateTimeOffset occurredAt);
```

`OrderChangeSummary.cs` (Application):

```csharp
using System.Globalization;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Los textos del historial del pedido (spec 2026-10-15 §4.6). En español, tuteando donde hable
/// con una persona; acá son hechos, no instrucciones.</summary>
public static class OrderChangeSummary
{
    public static string CreatedFromQuotation(string quotationNumber) => Trim($"Creado desde la cotización {quotationNumber}.");
    public static string CreatedManually() => "Creado a mano desde Pedidos.";
    public static string CreatedFromExternal(OrderSource source, string reference) => Trim($"Creado desde {source} ({reference}).");
    public static string ItemAdded(string productName, decimal quantity) => QuotationChangeSummary.ItemAdded(productName, quantity);
    public static string ItemQuantityChanged(string productName, decimal from, decimal to) => QuotationChangeSummary.ItemQuantityChanged(productName, from, to);
    public static string ItemRemoved(string productName) => QuotationChangeSummary.ItemRemoved(productName);
    public static string RetailChanged(bool isRetail) => isRetail
        ? "Marcó el pedido como detal: ninguna línea recibe descuento."
        : "Quitó el detal: las líneas vuelven a su descuento por escala.";
    public static string GlobalScaleFloorChanged(int? floor) => QuotationChangeSummary.GlobalScaleFloorChanged(floor);
    public static string NotesChanged() => "Editó las notas.";
    public static string Approved() => "Aprobado.";
    public static string Cancelled(string reason) => Trim($"Anulado: {reason}");
    public static string Invoiced() => "Marcado como facturado.";
    public static string InvoicingReverted() => "Se revirtió la facturación.";
    public static string PaymentProofsChanged(int added, int corrected, int removed)
    {
        var parts = new List<string>(3);
        if (added > 0) parts.Add($"{added} comprobante(s) nuevo(s)");
        if (corrected > 0) parts.Add($"{corrected} corregido(s)");
        if (removed > 0) parts.Add($"{removed} retirado(s)");
        return Trim($"Comprobantes: {string.Join(", ", parts)}.");
    }

    private static string Trim(string summary) =>
        summary.Length <= OrderHistoryEntry.DetailsMaxLength ? summary : summary[..(OrderHistoryEntry.DetailsMaxLength - 1)] + "…";
}
```

`ConvertQuotationToOrderHandler.HandleAsync`, dentro de la transacción, reemplaza `Order.Create(...)` por:

```csharp
            quotation.ConvertToOrder(convertedBy, now, products);
            order = Order.FromQuotation(
                OrderId.New(), command.TenantId, orderNumber, quotation, paymentStatus, command.Notes, convertedBy, proofs, now);
            orderRepository.Add(order);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, convertedBy,
                OrderChangeSummary.CreatedFromQuotation(quotation.QuotationNumber), now));
            // …historial de la cotización, auditoría y comprobantes como hoy…
            lifecycleEvents.PublishConverted(quotation, order, now);
            lifecycleEvents.PublishOrderCreated(order, now);
```

El validador quita `.MaximumLength(Order.NotesMaxLength)` (P2). La condición de concurrencia (Review Focus 2): si `SaveChangesAsync` lanza `DbUpdateException` cuyo índice es `IX_orders_quotation`, `QuotationsUnitOfWork` ya traduce índices únicos por nombre (precedente `catalog.product.code_taken`): agregar la entrada `"IX_orders_quotation" => "quotation.quotation.status_not_convertible"` en su mapa.

`CancelOrderHandler`: además de `PublishOrderCancelled`, agrega `orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Cancelled, cancelledBy, OrderChangeSummary.Cancelled(order.CancellationReason!), now))`. `ApproveOrderHandler`, `InvoiceOrderHandler` y `RevertOrderInvoicingHandler` agregan su entrada (`Approved`, `Invoiced`, `InvoicingReverted`) de la misma forma.

- [ ] **Step 4: Commit (tras compilar con Task 8)**

```bash
git add src/Modules/Quotations
git commit -m "feat(quotations): evento order.created, historial del pedido y conversión como copia"
```

---

### Task 8: DTOs, mapeo, detalle y compositor de respuesta del pedido

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs`, `OrderMapping.cs`, `GetOrderById.cs`, `GetOrder.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrderResponseComposer.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (`ToResponse`, `GetOrderByIdAsync`, `GetOrderAsync`, `ConvertQuotationToOrderAsync`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registrar `IOrderResponseComposer`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs`

**Interfaces:**
- Produces:
  - `OrderDto` gana: `string Source, Guid? QuotationId (ya existía, pasa a nullable), string? ExternalReference, Guid ClientId, Guid AdvisorId, string Currency, string? PaymentMethod, bool IsRetail, int? GlobalScaleFloor, bool BillingUsesBusinessName, bool IsStorePickup, bool BillsToFinalConsumer, bool? BillingWithRetention, bool? BillingVatSurplus, bool CustomerVatSurplus, decimal Subtotal, decimal TaxPercentage, decimal TaxAmount, decimal DiscountAmount, decimal Total, decimal RetentionAmount, decimal NetTotal, QuotationBillingAccountDto BillingAccount, IReadOnlyCollection<QuotationPartyDto> Parties, IReadOnlyCollection<QuotationItemDto> Items`.
  - `OrderDetailDto(OrderDto Order, string? QuotationNumber, IReadOnlyCollection<OrderHistoryEntryDto> History)`; `OrderHistoryEntryDto(Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? Details)`.
  - `OrderResponse` espeja `OrderDto`. `OrderDetailResponse(OrderResponse Order, QuotationClientResponse? Client, string? AdvisorName, string? AdvisorEmail, QuotationBillingResponse? BillingAccount, IReadOnlyCollection<QuotationItemResponse> Items, SourceQuotationResponse? SourceQuotation, IReadOnlyCollection<OrderHistoryEntryResponse> History, int[] AvailableFloors)`; `SourceQuotationResponse(Guid Id, string QuotationNumber)`; `OrderHistoryEntryResponse(Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? MemberName, string? Details)`.
  - `IOrderResponseComposer.ComposeAsync(Guid tenantId, OrderDetailDto detail, CancellationToken) : Task<OrderDetailResponse>`.
- Consumes: `IQuotationCustomerLookup`, `IQuotationAdvisorLookup`, `IQuotationProductLookup`, `IQuotationCompanyLookup` (los mismos del compositor de cotizaciones).

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
    // OrderApiTests.cs
    [Fact]
    public async Task TheDetailCarriesTheOrdersOwnDataAndItsSourceQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id), new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        var created = await converted.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{created!.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(clientId, detail.Client!.Id);
        Assert.NotNull(detail.AdvisorName ?? detail.AdvisorEmail);
        Assert.Equal((quotation.Id, quotation.QuotationNumber), (detail.SourceQuotation!.Id, detail.SourceQuotation.QuotationNumber));
        var item = Assert.Single(detail.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.NotEmpty(item.ProductName);
        Assert.Equal("Bancolombia", detail.BillingAccount!.BankName);
        var history = Assert.Single(detail.History);
        Assert.Equal("Created", history.EventType);
        Assert.Contains(quotation.QuotationNumber, history.Details);
    }
```

`OrderDetailResponse`, `SourceQuotationResponse` y `OrderHistoryEntryResponse` se consumen desde el proyecto de pruebas, que ya referencia `Modules.Quotations.Application`.

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~TheDetailCarriesTheOrdersOwnData"`
Expected: no compila (`OrderDetailResponse` no tiene `Client`, `SourceQuotation`, `History`).

- [ ] **Step 3: Implementar**

`OrdersDtos.cs`:

```csharp
public sealed record OrderHistoryEntryDto(Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? Details);

public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    string Source,
    Guid? QuotationId,
    string? ExternalReference,
    Guid ClientId,
    Guid AdvisorId,
    string Status,
    string PaymentStatus,
    string? PaymentMethod,
    string Currency,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool BillingUsesBusinessName,
    bool IsStorePickup,
    bool BillsToFinalConsumer,
    bool? BillingWithRetention,
    bool? BillingVatSurplus,
    // Efectivo (Order.AppliesVatSurplus), igual que QuotationDto.CustomerVatSurplus: la pantalla imprime «exento» con esto.
    bool CustomerVatSurplus,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    QuotationBillingAccountDto BillingAccount,
    IReadOnlyCollection<QuotationPartyDto> Parties,
    IReadOnlyCollection<QuotationItemDto> Items,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
    DateTimeOffset? InvoicedAt,
    Guid? InvoicedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyCollection<OrderPaymentProofDto> PaymentProofs);

public sealed record OrderDetailDto(OrderDto Order, string? QuotationNumber, IReadOnlyCollection<OrderHistoryEntryDto> History);

public sealed record OrderResponse(
    Guid Id,
    string OrderNumber,
    string Source,
    Guid? QuotationId,
    string? ExternalReference,
    Guid ClientId,
    Guid AdvisorId,
    string Status,
    string PaymentStatus,
    string? PaymentMethod,
    string Currency,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool BillingUsesBusinessName,
    bool IsStorePickup,
    bool BillsToFinalConsumer,
    bool? BillingWithRetention,
    bool? BillingVatSurplus,
    bool CustomerVatSurplus,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    QuotationBillingAccountDto BillingAccount,
    IReadOnlyCollection<QuotationPartyResponse> Parties,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
    DateTimeOffset? InvoicedAt,
    Guid? InvoicedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyCollection<OrderPaymentProofResponse> PaymentProofs);
// Las líneas no viajan en OrderResponse: van resueltas (nombre, código, imagen, escalas) en OrderDetailResponse.Items.

public sealed record SourceQuotationResponse(Guid Id, string QuotationNumber);
public sealed record OrderHistoryEntryResponse(Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? MemberName, string? Details);

public sealed record OrderDetailResponse(
    OrderResponse Order,
    QuotationClientResponse? Client,
    string? AdvisorName,
    string? AdvisorEmail,
    QuotationBillingResponse? BillingAccount,
    IReadOnlyCollection<QuotationItemResponse> Items,
    SourceQuotationResponse? SourceQuotation,
    IReadOnlyCollection<OrderHistoryEntryResponse> History,
    int[] AvailableFloors);
```

`OrderListItemDto` y `OrderListItemResponse`: `Guid? QuotationId`, `string? QuotationNumber`, más `string Source` y `string? ExternalReference` al final (Task 9 los llena).

`OrderMapping.ToDto` mapea todos los campos nuevos (`order.Source.ToString()`, `order.QuotationId?.Value`, `order.AppliesVatSurplus`, `order.PartyWithRetention`, `order.PartyVatSurplus`, `ToDto(order.BillingAccount)`, partes e ítems con las mismas funciones privadas que `QuotationMapping` —copiarlas para `OrderParty`/`OrderItem`—). `ToListItemDto(this Order order, string? quotationNumber, string? clientName, string? advisorName)` reemplaza la versión sobre `OrderWithQuotation`.

`GetOrderByIdHandler`:

```csharp
public sealed class GetOrderByIdHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IExecutionContext executionContext) : IQueryHandler<GetOrderByIdQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(GetOrderByIdQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, OrdersPermissions.OrderRead);
        var order = await orderRepository.FindUntrackedByIdAsync(query.TenantId, new OrderId(query.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(query.OrderId);
        var quotationNumber = order.QuotationId is { } quotationId
            ? (await quotationRepository.FindUntrackedAsync(query.TenantId, quotationId, cancellationToken))?.QuotationNumber
            : null;
        var history = await orderRepository.ListHistoryAsync(query.TenantId, order.Id, cancellationToken);
        return new OrderDetailDto(order.ToDto(), quotationNumber, history.Select(OrderMapping.ToDto).ToArray());
    }
}
```

`GetOrderHandler` (por cotización) devuelve `OrderDetailDto` igual (`FindByQuotationIdAsync` + historial). `OrderMapping.ToDto(OrderHistoryEntry)` mapea id, tipo como texto, fecha, miembro y detalle.

`OrderResponseComposer.cs`:

```csharp
public interface IOrderResponseComposer
{
    Task<OrderDetailResponse> ComposeAsync(Guid tenantId, OrderDetailDto detail, CancellationToken cancellationToken);
}

public sealed class OrderResponseComposer(
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup) : IOrderResponseComposer
{
    public async Task<OrderDetailResponse> ComposeAsync(Guid tenantId, OrderDetailDto detail, CancellationToken cancellationToken)
    {
        var order = detail.Order;
        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
        var memberIds = detail.History.Where(entry => entry.MemberId is not null).Select(entry => entry.MemberId!.Value)
            .Append(order.AdvisorId).Distinct().ToArray();
        var advisors = await advisorLookup.FindAsync(tenantId, memberIds, cancellationToken);
        var advisor = advisors.GetValueOrDefault(order.AdvisorId);
        var company = await companyLookup.FindAsync(tenantId, order.BillingAccount.CompanyId, cancellationToken);
        var products = await productLookup.FindManyAsync(tenantId, order.Items.Select(item => item.ProductId).Distinct().ToArray(), cancellationToken);
        return new OrderDetailResponse(
            OrderResponses.ToResponse(order),
            QuotationResponseComposer.ToClientResponse(customer),
            advisor?.DisplayName,
            advisor?.Email,
            QuotationResponseComposer.ToBillingResponse(order.BillingAccount, company),
            order.Items.Select(item => QuotationResponseComposer.ToItemResponse(item, products, order.GlobalScaleFloor)).ToArray(),
            order.QuotationId is { } quotationId && detail.QuotationNumber is { } number ? new SourceQuotationResponse(quotationId, number) : null,
            detail.History.Select(entry => new OrderHistoryEntryResponse(
                entry.Id, entry.EventType, entry.EventAt, entry.MemberId,
                entry.MemberId is { } member ? advisors.GetValueOrDefault(member)?.Label : null, entry.Details)).ToArray(),
            QuotationResponseComposer.AvailableFloors(order.Items, products));
    }
}
```

Para eso, en `QuotationResponseComposer` los tres métodos privados `ToClientResponse`, `ToItemResponse`, `ToBillingResponse` pasan a `internal static`, y `AvailableFloors` gana una sobrecarga `internal static int[] AvailableFloors(IReadOnlyCollection<QuotationItemDto> items, IReadOnlyDictionary<Guid, QuotationProductRef> products)` que la versión actual llama. `OrderResponses.ToResponse(OrderDto)` es el `ToResponse` que hoy vive en `OrderEndpoints` movido a Application (`internal static class OrderResponses`) para que el compositor lo use; `OrderEndpoints` lo llama desde ahí.

`OrderEndpoints`: `GetOrderByIdAsync`, `GetOrderAsync`, `AddOrderItemsAsync`, `SaveOrderEditsAsync` y `PreviewOrderEditsAsync` componen con `IOrderResponseComposer` (`composer.ComposeAsync(tenantId, detail, ct)`); `ConvertQuotationToOrderAsync` sigue devolviendo `OrderResponse`.

Registrar en `QepServiceCollectionExtensions` junto a `IQuotationResponseComposer`: `services.AddScoped<IOrderResponseComposer, OrderResponseComposer>();`.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~TheDetailCarriesTheOrdersOwnData|FullyQualifiedName~ConvertingCopiesTheQuotation|FullyQualifiedName~ConvertingPublishesConvertedAndOrderCreated"`
Expected: 3 PASS (la solución ya compila si Task 9-11 se hicieron primero; si no, esta evidencia se toma al cerrar Task 11).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs
git commit -m "feat(quotations): detalle del pedido con sus propios datos y cotización de origen"
```

---

### Task 9: Lista de pedidos leyendo del pedido, con filtro por origen

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ListOrders.cs`, `OrderListing.cs`, `ExportOrders.cs` (`Source` en comando y filtros)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (`ListOrdersAsync`, `ExportOrdersAsync`, `ToListItemResponse`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ListOrdersHandlerTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderListApiTests.cs`

**Interfaces:**
- Produces: `ListOrdersQuery` gana `string? Source` (antes de `Page`); `ExportOrdersCommand` y `OrdersExportFilters` ganan `string? Source`; `OrderListing.ParseSource(string?) : OrderSource?` lanza `order.order.source_invalid`; `OrderListing.ToListItemsAsync(IQuotationCustomerLookup, IQuotationAdvisorLookup, IQuotationRepository, Guid tenantId, IReadOnlyList<Order> rows, CancellationToken)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Unitaria (reescribir `NewRow` de `ListOrdersHandlerTests` para que construya un `Order` con `Order.CreateManual` —cliente `ClientId`, asesor `AdvisorId`, moneda COP— y el handler reciba `StubOrderListRepository(params Order[])`):

```csharp
    [Fact]
    public async Task ListReadsClientAdvisorAndTotalsFromTheOrder()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0001", ClientId, total: 23_800m));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(page.Items);
        Assert.Equal(("Ferretería El Tornillo", "Asesora Uno", "COP", 23_800m, "Manual", (Guid?)null, (string?)null),
            (row.ClientName, row.AdvisorName, row.Currency, row.Total, row.Source, row.QuotationId, row.QuotationNumber));
    }

    [Fact]
    public async Task ListRejectsAnUnknownSource()
    {
        var handler = NewHandler(NewCustomerLookup());

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewQuery() with { Source = "Telegram" }, TestContext.Current.CancellationToken));

        Assert.Equal("order.order.source_invalid", error.Code);
    }
```

Integración (`OrderListApiTests.cs`):

```csharp
    [Fact]
    public async Task ListFiltersByClientAndSourceOnTheOrderItself()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var otherClientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        await ConvertToOrderAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, factory, tenantId, otherClientId, productId);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{tenantId}/orders?clientId={clientId}&source=Quotation", TestContext.Current.CancellationToken);

        var row = Assert.Single(page!.Items);
        Assert.Equal((clientId, "Quotation"), (row.ClientId, row.Source));
        Assert.NotNull(row.QuotationNumber);

        var none = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{tenantId}/orders?source=Manual", TestContext.Current.CancellationToken);
        Assert.Empty(none!.Items);
    }
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~ListOrdersHandlerTests"`
Expected: no compila (`ListOrdersQuery` sin `Source`; `NewRow` devuelve `OrderWithQuotation`).

- [ ] **Step 3: Implementar**

`OrderListing`:

```csharp
    public static OrderSource? ParseSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        return Enum.TryParse<OrderSource>(source, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException("order.order.source_invalid", $"'{source}' is not a valid order source.");
    }

    public static async Task<IReadOnlyList<OrderListItemDto>> ToListItemsAsync(
        IQuotationCustomerLookup customerLookup,
        IQuotationAdvisorLookup advisorLookup,
        IQuotationRepository quotationRepository,
        Guid tenantId,
        IReadOnlyList<Order> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var clientNames = await customerLookup.FindNamesAsync(tenantId, rows.Select(row => row.ClientId).Distinct().ToArray(), cancellationToken);
        var advisors = await advisorLookup.FindAsync(tenantId, rows.Select(row => row.AdvisorId.Value).Distinct().ToArray(), cancellationToken);
        var quotationNumbers = await quotationRepository.FindNumbersAsync(
            tenantId, rows.Where(row => row.QuotationId is not null).Select(row => row.QuotationId!.Value).Distinct().ToArray(), cancellationToken);
        return rows
            .Select(row => row.ToListItemDto(
                row.QuotationId is { } quotationId ? quotationNumbers.GetValueOrDefault(quotationId) : null,
                clientNames.GetValueOrDefault(row.ClientId),
                advisors.GetValueOrDefault(row.AdvisorId.Value)?.Label))
            .ToArray();
    }
```

`IQuotationRepository` gana `Task<IReadOnlyDictionary<QuotationId, string>> FindNumbersAsync(Guid tenantId, IReadOnlyCollection<QuotationId> quotationIds, CancellationToken)` (consulta `Select(q => new { q.Id, q.QuotationNumber })`, sin tracking). Los dobles de pruebas de `IQuotationRepository` lo implementan devolviendo `COT-…` por id.

`ListOrdersHandler`: inyecta `IQuotationRepository quotationRepository`; parsea `source = OrderListing.ParseSource(query.Source)`; pasa `source` a `SearchAsync`; llama `ToListItemsAsync(customerLookup, advisorLookup, quotationRepository, …)`.

`ExportOrdersHandler` y `OrdersExportFilters`: `Source` viaja en los filtros serializados y se parsea en `OrdersExportProcessor.ParseStatuses` (Task 11).

`OrderEndpoints.ListOrdersAsync` y `ExportOrdersAsync` reciben `string? source = null` y lo pasan; `ToListItemResponse` mapea `Source` y `ExternalReference`.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~ListOrdersHandlerTests"` y luego la de integración.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): la lista de pedidos filtra y lee del pedido, con origen"
```

---

### Task 10: Edición del pedido pendiente sobre el pedido

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderItemEdits.cs`, `AddOrderItems.cs`, `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `OrderEdits.cs`, `RemoveOrderPaymentProof.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrderPricingRecalculation.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemEditsTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`

**Interfaces:**
- Produces: `OrderItemEdits.ApplyAsync(Order order, IReadOnlyCollection<OrderItemAddition> desired, IQuotationProductPricingLookup, Guid tenantId, MemberId updatedBy, DateTimeOffset, bool isRetail, CancellationToken) : Task<IReadOnlyList<OrderItemEdit>>`; `OrderPricingRecalculation.ApplyAsync(IQuotationProductPricingLookup, IQuotationSettingsStore, Guid tenantId, Order order, DateTimeOffset, CancellationToken)`; `AddOrderItemsCommand : ICommand<OrderDetailDto>` (ya no `OrderItemsAddedResult`); `SaveOrderEditsCommand` y `PreviewOrderEditsQuery` devuelven `OrderDetailDto` como hoy pero compuesto desde el pedido.

- [ ] **Step 1: Escribir las pruebas que fallan**

`OrderItemEditsTests.cs` (reescribir el archivo existente para usar `Order` en vez de `Quotation`; el doble `StubQuotationProductPricingLookup` ya existe en `QuotationsTestDoubles`):

```csharp
    [Fact]
    public async Task AppliesAdditionsQuantityChangesAndRemovalsOnTheOrder()
    {
        var keep = Guid.CreateVersion7();
        var drop = Guid.CreateVersion7();
        var add = Guid.CreateVersion7();
        var order = NewPendingOrder(lines: [Line(keep, 1m), Line(drop, 2m)]);
        var pricing = new StubQuotationProductPricingLookup(TenantId, [keep, drop, add], unitPrice: 10_000m, taxPercentage: 19);

        var edits = await OrderItemEdits.ApplyAsync(
            order, [new OrderItemAddition(keep, 3m), new OrderItemAddition(add, 1m)], pricing, TenantId, Member, Now,
            isRetail: false, TestContext.Current.CancellationToken);

        Assert.Equal(
            [OrderItemEditKind.Added, OrderItemEditKind.QuantityChanged, OrderItemEditKind.Removed],
            edits.Select(edit => edit.Kind));
        Assert.Equal([keep, add], order.Items.Select(item => item.ProductId));
        Assert.Equal(3m, order.Items.Single(item => item.ProductId == keep).Quantity);
    }

    // Review Focus 3: una línea intacta de un producto que ya no existe se queda con su foto.
    [Fact]
    public async Task LeavesAnUntouchedLineAloneEvenIfItsProductIsGone()
    {
        var gone = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var order = NewPendingOrder(lines: [Line(gone, 1m, product: new QuotationProductSnapshot("OLD", "Viejo")), Line(other, 1m)]);
        var pricing = new StubQuotationProductPricingLookup(TenantId, [other], unitPrice: 10_000m, taxPercentage: 19);

        await OrderItemEdits.ApplyAsync(
            order, [new OrderItemAddition(gone, 1m), new OrderItemAddition(other, 2m)], pricing, TenantId, Member, Now,
            isRetail: false, TestContext.Current.CancellationToken);

        Assert.Equal("OLD", order.Items.Single(item => item.ProductId == gone).ProductCode);
    }
```

`OrderEditsApiTests.cs`:

```csharp
    [Fact]
    public async Task SavingEditsChangesTheOrderAndLeavesTheQuotationUntouched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var (orderId, quotationId, version) = await CreatePendingOrderAsync(client, factory, tenantId, clientId, productId);

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tenants/{tenantId}/orders/{orderId}")
        {
            Content = JsonContent.Create(new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 7m)], null, "Nota del pedido", null, false)),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(7m, Assert.Single(detail!.Items).Quantity);
        Assert.Equal("Nota del pedido", detail.Order.Notes);
        Assert.Contains(detail.History, entry => entry.EventType == "ItemsChanged");

        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.Equal(1m, Assert.Single(quotation!.Items).Quantity);
    }
```

`CreatePendingOrderAsync` (helper del archivo, línea ~601) pasa a devolver también `quotationId` y la `version` del pedido.

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet build src/Modules/Quotations/Modules.Quotations.Application --no-restore`
Expected: errores en los cuatro handlers y en `OrderItemEdits` por los métodos `…AfterConversion` inexistentes.

- [ ] **Step 3: Implementar**

`OrderItemEdits.ApplyAsync` sobre `Order`: igual que hoy, con `order.Items`, `order.Currency`, `order.AddItem`, `order.UpdateItemQuantity`, `order.RemoveItem` y `OrderItemId.New()`.

`OrderPricingRecalculation.cs`: copia de `QuotationPricingRecalculation` con `Order`, `order.ApplyGroupDiscounts(IReadOnlyDictionary<OrderItemId, QuotationItemDiscount>, now)`, líneas `new QuotationPricingLine(item.Id.Value, item.ProductId, item.Quantity)` y `QuotationMinimumPurchase.IsMet(units, order.Total, order.Currency, settings)` (hacer `IsMet` `internal static`; hoy es privado).

`AddOrderItemsHandler`:

```csharp
public sealed record AddOrderItemsCommand(Guid TenantId, Guid OrderId, IReadOnlyList<OrderItemAddition> ToAdd) : ICommand<OrderDetailDto>;

public sealed class AddOrderItemsHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    IQuotationProductLookup productLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderItemsCommand> validator) : ICommandHandler<AddOrderItemsCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(AddOrderItemsCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var order = await orderRepository.FindByIdAsync(command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException("order.order.not_pending", "Products can only be added while the order is pending.");
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;
        foreach (var addition in command.ToAdd)
        {
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, command.TenantId, addition.ProductId, addition.Quantity, order.Currency, order.IsRetail, cancellationToken);
            order.AddItem(OrderItemId.New(), addition.ProductId, addition.Quantity, pricing.Pricing.UnitPrice,
                pricing.Pricing.DiscountPercentage, pricing.Pricing.TaxPercentage, updatedBy, now);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.ItemsChanged, updatedBy,
                OrderChangeSummary.ItemAdded(pricing.Name, addition.Quantity), now));
            auditPublisher.Publish(command.TenantId, executionContext.SubjectId, "quotation.order.item_added", order.Id.ToString(), "success", now);
        }

        order.CaptureProductSnapshots(await OrderItemProductLabel.ResolveMissingAsync(productLookup, command.TenantId, order, cancellationToken));
        await OrderPricingRecalculation.ApplyAsync(pricingLookup, settingsStore, command.TenantId, order, now, cancellationToken);
        order.RecalculatePaymentStatus(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await OrderDetails.LoadAsync(orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
```

Dos ayudantes nuevos en Application:

```csharp
// OrderItemProductLabel.ResolveMissingAsync: igual que QuotationItemProductLabel.ResolveMissingAsync, sobre order.Items.
internal static class OrderItemProductLabel
{
    public static async Task<IReadOnlyDictionary<Guid, QuotationProductSnapshot>> ResolveMissingAsync(
        IQuotationProductLookup productLookup, Guid tenantId, Order order, CancellationToken cancellationToken)
    {
        var missing = order.Items.Where(item => item.ProductCode is null).Select(item => item.ProductId).Distinct().ToArray();
        if (missing.Length == 0) return QuotationProductSnapshot.None;
        var products = await productLookup.FindManyAsync(tenantId, missing, cancellationToken);
        return products.ToDictionary(entry => entry.Key, entry => new QuotationProductSnapshot(entry.Value.Code, entry.Value.Name));
    }
}

// OrderDetails.LoadAsync: arma el OrderDetailDto después de guardar (número de la cotización de origen + historial).
internal static class OrderDetails
{
    public static async Task<OrderDetailDto> LoadAsync(
        IOrderRepository orders, IQuotationRepository quotations, Guid tenantId, Order order, CancellationToken cancellationToken)
    {
        var quotationNumber = order.QuotationId is { } quotationId
            ? (await quotations.FindUntrackedAsync(tenantId, quotationId, cancellationToken))?.QuotationNumber
            : null;
        var history = await orders.ListHistoryAsync(tenantId, order.Id, cancellationToken);
        return new OrderDetailDto(order.ToDto(), quotationNumber, history.Select(OrderMapping.ToDto).ToArray());
    }
}
```

`SaveOrderEditsHandler`: quita `IQuotationRepository` para la cotización (lo conserva sólo para `OrderDetails.LoadAsync`); `RefreshCustomerTaxProfile` sobre `order`; `OrderItemEdits.ApplyAsync(order, …)`; `order.SetIsRetail` / `order.SetGlobalScaleFloor`; historial con `orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(… OrderHistoryEventType.ItemsChanged | PricingChanged | NotesChanged | PaymentProofsChanged …))` en vez de `quotationRepository.AddHistoryEntry`; `order.CaptureProductSnapshots(await OrderItemProductLabel.ResolveMissingAsync(…))`; `OrderPricingRecalculation.ApplyAsync(…, order, …)`; `order.RecalculatePaymentStatus(now)`; devuelve `OrderDetails.LoadAsync(...)`. El `return` temprano sin cambios devuelve también `OrderDetails.LoadAsync`.

`PreviewOrderEditsHandler`: igual sobre `FindUntrackedByIdAsync`; devuelve `new OrderDetailDto(order.ToDto() with { Version = storedVersion }, quotationNumber, [])` (el preview no necesita historial).

`RemoveOrderPaymentProofHandler`: quita la cotización; `order.RecalculatePaymentStatus(now)`; agrega historial `PaymentProofsChanged(0, 0, 1)`.

`OrderEditsValidator`: quitar `.MaximumLength(Order.NotesMaxLength)` (P2).

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderItemEditsTests"` y `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderEditsApiTests|FullyQualifiedName~OrderItemEditApiTests"`
Expected: PASS. Las pruebas viejas de esos archivos que afirmaban cambios en `detail.Quotation` pasan a afirmar sobre `detail.Items` / `detail.Order`.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): editar un pedido pendiente edita el pedido, no la cotización"
```

---

### Task 11: Excel de pedidos desde el pedido y barrido de compilación

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs` (comentario de `notes`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`AddOrderItemsCommand` → `OrderDetailDto`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs` y los siete archivos que construyen `OrderWithQuotation` (inventario §7 del spec)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs`

- [ ] **Step 1: Escribir la prueba que falla**

En `OrdersExportProcessorTests.cs`, el builder `NewRow` deja de recibir `(order, quotation)` y recibe un `Order` con líneas y partes (`Order.CreateManual` / `FromQuotation`). Caso nuevo:

```csharp
    [Fact]
    public async Task ExportsAnOrderWithoutQuotationReadingEverythingFromTheOrder()
    {
        var order = NewManualOrder(
            lines: [Line(quantity: 2m, unitPrice: 11_900m)],
            shipping: new QuotationPartyDetails { Name = "Bodega", Phone = "300", Email = "b@x.co", Address = "Cl 1", DepartmentId = DepartmentId, CityId = CityId },
            notes: "Entregar en portería");
        var (workbook, _) = await RunAsync(order);

        var row = Assert.Single(workbook.Rows);
        Assert.Equal("Cl 1", Cell(row, "address"));
        Assert.Equal("Entregar en portería", Cell(row, "notes"));
        Assert.Equal(order.Total, Cell(row, "order_total"));
        Assert.Equal("Bancolombia 7542", Cell(row, "bank_account"));
        Assert.Equal(order.OrderNumber, Cell(row, "order_number"));
    }
```

(`RunAsync` y `Cell` son los ayudantes que el archivo ya tiene para correr el procesador con dobles y leer una celda por clave de columna; si se llaman distinto, usar los existentes.)

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore`
Expected: errores por `OrderWithQuotation` en siete archivos y por `RowsFor(OrderWithQuotation …)`.

- [ ] **Step 3: Implementar el procesador**

`BatchContext` pasa a `ItemsByOrder`, `PartiesByOrder` (claves `OrderId`), y los lookups toman del pedido:

```csharp
    private async Task<BatchContext> LoadBatchContextAsync(Guid tenantId, IReadOnlyList<Order> batch, CancellationToken cancellationToken)
    {
        var orderIds = batch.Select(order => order.Id).ToArray();
        var itemsByOrder = await repository.ListItemsForExportAsync(tenantId, orderIds, cancellationToken);
        var partiesByOrder = await repository.ListPartiesForExportAsync(tenantId, orderIds, cancellationToken);
        var proofsByOrder = await repository.ListPaymentProofsForExportAsync(tenantId, orderIds, cancellationToken);
        var productIds = itemsByOrder.Values.SelectMany(items => items).Where(item => item.ProductCode is null).Select(item => item.ProductId).Distinct().ToArray();
        var products = productIds.Length == 0 ? new Dictionary<Guid, QuotationProductRef>() : await productLookup.FindManyAsync(tenantId, productIds, cancellationToken);
        var companyIds = batch.Select(order => order.BillingAccount.CompanyId).Distinct().ToArray();
        // …companies, cityNames, customers (batch.Select(order => order.ClientId)), coordinadora, advisors (order.AdvisorId) igual que hoy…
    }

    private static IEnumerable<ExportCell[]> RowsFor(Order order, BatchContext context, TenantCalendar calendar, IPaymentProofPublisher publisher)
    {
        var items = context.ItemsByOrder.TryGetValue(order.Id, out var foundItems) ? foundItems : [];
        if (items.Count == 0) yield break;
        // …idéntico a hoy, reemplazando `quotation.X` por `order.X`:
        //   quotation.BillingAccount → order.BillingAccount (ya no es nullable)
        //   quotation.ClientId → order.ClientId; quotation.Notes → order.Notes; quotation.IsStorePickup → order.IsStorePickup
        //   quotation.AdvisorId → order.AdvisorId; quotation.Total / RetentionAmount → order.Total / order.RetentionAmount
        //   BilledNameFor(order, billingParty, customer) y BilledIdentificationFor(order, …) usan order.BillsToFinalConsumer y order.BillingUsesBusinessName
        //   QuotationParty → OrderParty en ContactFor / CoordinadoraCityFor; QuotationItem → OrderItem en el foreach
    }
```

`ExportBatchLoop.WriteAllAsync<Order, OrderExportCursor>(…)` con `row => new OrderExportCursor(row.ConvertedAt, row.OrderNumber)`. `ParseStatuses` devuelve también `OrderListing.ParseSource(filters.Source)` y se pasa a `ListForExportAsync`.

En `OrdersExportColumnCatalog.cs`, el comentario de `notes` dice: «Observaciones: `Order.Notes`. Al convertir, el pedido hereda las notas de la cotización (spec 2026-10-15 §6.4), así que un pedido convertido exporta lo mismo que antes; si alguien edita las notas del pedido después, sale lo editado».

- [ ] **Step 4: Barrer los dobles y el registro**

- `QepServiceCollectionExtensions.cs:486-487`: `ICommandHandler<AddOrderItemsCommand, OrderDetailDto>, AddOrderItemsHandler`.
- Los siete archivos de pruebas unitarias con `new OrderWithQuotation(order, quotation)` (`ApproveOrderHandlerTests`, `ListOrdersHandlerTests`, `ExportOrdersHandlerTests`, `OrderInvoicingHandlerTests`, `OrdersExportProcessorTests`, `RemoveOrderPaymentProofHandlerTests`, `ListQuotationsHandlerTests`) pasan un `Order` construido con `Order.CreateManual` (helper compartido `OrderBuilders.Pending(...)` en `QuotationsTestDoubles.cs`) o con `Order.FromQuotation` cuando la prueba necesita la relación.
- `QuotationsDbContextMappingTests.OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints` agrega las columnas nuevas a su lista esperada.

- [ ] **Step 5: Correr y ver que pasa**

Run: `dotnet build --no-restore` (toda la solución) y luego `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore`
Expected: Build succeeded, 0 warnings; unitarias en verde (incluidas las de Task 1-5 pendientes de evidencia). Después: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderExportApiTests|FullyQualifiedName~OrdersMigrationTests|FullyQualifiedName~OrderApiTests"` en verde.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(quotations): el Excel de pedidos lee del pedido"
```

---

### Task 12: Pedido manual: `POST /orders` y `POST /orders/preview`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrderCustomerEligibility.cs`, `OrderDraftBuilder.cs`, `CreateOrder.cs`, `PreviewOrder.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` (requests), `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/CreateOrderValidatorTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ManualOrderApiTests.cs`

**Interfaces:**
- Produces:
  - `CreateOrderRequest(Guid ClientId, string? PaymentMethod, string? Notes, bool IsRetail, int? GlobalScaleFloor, QuotationPartiesRequest? Parties, QuotationBillingAccountRequest BillingAccount, IReadOnlyList<OrderEditItemRequest> Items, string PaymentStatus, IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs)`; el mismo cuerpo sirve para `preview`.
  - `CreateOrderCommand(Guid TenantId, …mismos campos…) : ICommand<OrderDetailDto>`; `PreviewOrderQuery(…) : IQuery<OrderDetailDto>`.
  - `OrderCustomerEligibility.Ensure([NotNull] QuotationCustomerRef? customer, Guid tenantId, Guid clientId, bool allowIncomplete)` con códigos `order.order.client_not_found`, `client_incomplete`, `client_cuc_missing`, `client_inactive`.
  - `OrderDraftBuilder.BuildAsync(OrderDraftInput input, …lookups…, CancellationToken) : Task<Order>` — arma el `Order` preciado sin guardarlo; lo usan crear, preview y el servicio externo (Task 13).
- Consumes: `QuotationBillingAccountResolver`, `QuotationProductPricingResolver`, `OrderPricingRecalculation`, `QuotationAdvisorResolver`, `IOrderNumberGenerator`, `IDocumentNumberingFormatLookup`, `ITenantClock`, `PaymentProofCopies`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`CreateOrderValidatorTests.cs`:

```csharp
public sealed class CreateOrderValidatorTests
{
    private static CreateOrderCommand Valid() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Transferencia", null, false, null, null,
        new QuotationBillingAccountRequest(Guid.CreateVersion7(), "Bancolombia", "7542", "COP"),
        [new OrderItemAddition(Guid.CreateVersion7(), 1m)], "PaymentPending", []);

    [Fact]
    public void AcceptsAMinimalManualOrder() => Assert.True(new CreateOrderValidator().Validate(Valid()).IsValid);

    [Fact]
    public void RequiresAtLeastOneItem()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { Items = [] });
        Assert.Contains(result.Errors, error => error.PropertyName == "Items");
    }

    [Fact]
    public void RequiresTheBillingAccount()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { BillingAccount = null! });
        Assert.Contains(result.Errors, error => error.PropertyName == "BillingAccount");
    }

    [Fact]
    public void RejectsAnUnknownPaymentStatus()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { PaymentStatus = "Paid" });
        Assert.Contains(result.Errors, error => error.PropertyName == "PaymentStatus");
    }
}
```

`ManualOrderApiTests.cs`:

```csharp
public sealed class ManualOrderApiTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static async Task<CreateOrderRequest> ValidRequestAsync(HttpClient client, Guid tenantId, Guid clientId, Guid productId)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        return new CreateOrderRequest(
            clientId, "Transferencia", "Pedido de mostrador", false, null, null,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            [new OrderEditItemRequest(productId, 3m)], "PaymentPending", []);
    }

    [Fact]
    public async Task CreatesAPendingManualOrderWithItsOwnTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(("Pending", "Manual", (Guid?)null, "COP"), (detail.Order.Status, detail.Order.Source, detail.Order.QuotationId, detail.Order.Currency));
        Assert.StartsWith("PED-", detail.Order.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(3m, Assert.Single(detail.Items).Quantity);
        Assert.True(detail.Order.Total > 0);
        Assert.Null(detail.SourceQuotation);
        Assert.Equal("Created", Assert.Single(detail.History).EventType);
        Assert.Equal(detail.Order.Id.ToString(), response.Headers.Location!.ToString().Split('/').Last());
    }

    [Fact]
    public async Task PreviewReturnsTotalsWithoutSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, clientId, productId);

        var preview = await client.PostAsJsonAsync($"{OrdersUrl(tenantId)}/preview", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var detail = await preview.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.True(detail!.Order.Total > 0);
        var page = await client.GetFromJsonAsync<OrdersPageResponse>(OrdersUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task RejectsAnIncompleteCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var incompleteId = await IncompleteCustomerQuotationApiTests.CreateIncompleteCustomerAsync(factory, tenantId); // helper que ya existe en ese archivo; si es privado, hacerlo internal
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, incompleteId, productId);

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.Equal("order.order.client_incomplete", problem!.Extensions["code"]!.ToString());
    }

    // Review Focus 1: un cliente de otro tenant no se distingue de uno inexistente.
    [Fact]
    public async Task RejectsACustomerOfAnotherTenantAsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        using var __ = otherClient;
        var foreignCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var request = await ValidRequestAsync(client, tenantId, foreignCustomerId, productId);

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.Equal("order.order.client_not_found", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task RequiresTheOrderManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ["quotations.order.read"]);
        using var _ = client;

        var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~CreateOrderValidatorTests"`
Expected: no compila (`CreateOrderCommand`, `CreateOrderValidator` no existen).

- [ ] **Step 3: Implementar**

`OrderCustomerEligibility.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Espejo de <see cref="QuotationCustomerEligibility"/> con códigos del pedido (spec 2026-10-15 §5.2).
/// <paramref name="allowIncomplete"/> lo pasa en <c>true</c> sólo <see cref="ExternalOrderService"/>: un pedido manual
/// exige cliente completo con CUC, como la cotización.</summary>
internal static class OrderCustomerEligibility
{
    public static void Ensure([NotNull] QuotationCustomerRef? customer, Guid tenantId, Guid clientId, bool allowIncomplete)
    {
        if (customer is null || customer.TenantId != tenantId)
        {
            throw new QuotationsDomainException("order.order.client_not_found", $"Client '{clientId}' was not found in this tenant.");
        }

        if (!allowIncomplete && !customer.IsComplete)
        {
            throw new QuotationsDomainException("order.order.client_incomplete", "The client record is incomplete; complete it before selling.");
        }

        if (!allowIncomplete && string.IsNullOrWhiteSpace(customer.Cuc))
        {
            throw new QuotationsDomainException("order.order.client_cuc_missing", "The client does not have a CUC assigned.");
        }

        if (!customer.IsActive)
        {
            throw new QuotationsDomainException("order.order.client_inactive", "An inactive client cannot be sold to.");
        }
    }
}
```

`QuotationCustomerRef` necesita `IsComplete`: ya lo tiene (H13 del spec de Shopify lo cita); si no, agregar `bool IsComplete = true` al record y llenarlo en `QuotationCustomerLookup` (Bootstrapper) desde `Customer.IsComplete`.

`OrderDraftBuilder.cs`:

```csharp
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Lo que un pedido nuevo trae del cuerpo (manual) o de la integración (externo), antes de preciar.</summary>
public sealed record OrderDraftInput(
    Guid TenantId,
    Guid ClientId,
    MemberId AdvisorId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest BillingAccount,
    IReadOnlyList<OrderItemAddition> Items,
    OrderPaymentStatus PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofInput> Proofs,
    OrderSource Source,
    string? ExternalReference,
    bool AllowIncompleteCustomer);

/// <summary>Arma un <see cref="Order"/> preciado como lo haría el cotizador: cliente elegible, cuenta de cobro
/// validada contra Companies, precio y descuento por escala de cada línea, descuentos de grupo y mínimo de compra.
/// No guarda nada: crear, previsualizar y el servicio externo lo comparten.</summary>
internal sealed class OrderDraftBuilder(
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IQuotationSettingsStore settingsStore)
{
    public async Task<Order> BuildAsync(OrderDraftInput input, string orderNumber, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var customer = await customerLookup.FindAsync(input.TenantId, input.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, input.TenantId, input.ClientId, input.AllowIncompleteCustomer);
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(companyLookup, input.TenantId, input.BillingAccount, cancellationToken)
            ?? throw new QuotationsDomainException("order.billing.account_required", "An order needs a billing account.");
        var currency = billingAccount.Currency;
        var pricing = await QuotationProductPricingResolver.ResolveManyAsync(
            pricingLookup, input.TenantId, input.Items.Select(item => (item.ProductId, item.Quantity)).ToArray(), currency, input.IsRetail, cancellationToken);
        var products = await productLookup.FindManyAsync(input.TenantId, input.Items.Select(item => item.ProductId).Distinct().ToArray(), cancellationToken);
        var lines = input.Items.Select(item =>
        {
            var priced = pricing[item.ProductId];
            products.TryGetValue(item.ProductId, out var product);
            return new OrderLineInput(
                item.ProductId, item.Quantity, priced.UnitPrice, priced.DiscountPercentage, priced.TaxPercentage,
                product is null ? null : new QuotationProductSnapshot(product.Code, product.Name));
        }).ToArray();
        var header = new OrderHeader(
            input.ClientId, input.AdvisorId, currency, input.PaymentMethod, input.IsRetail, input.GlobalScaleFloor,
            customer.WithRetention, customer.VatSurplus);
        var order = input.Source == OrderSource.Manual
            ? Order.CreateManual(OrderId.New(), input.TenantId, orderNumber, header, input.Parties.ToDomain(), billingAccount, lines,
                input.PaymentStatus, input.Notes, input.AdvisorId, input.Proofs, now)
            : Order.CreateExternal(OrderId.New(), input.TenantId, orderNumber, header, input.Parties.ToDomain(), billingAccount, lines,
                input.PaymentStatus, input.Notes, input.AdvisorId, input.Proofs, now, input.Source, input.ExternalReference!);
        await OrderPricingRecalculation.ApplyAsync(pricingLookup, settingsStore, input.TenantId, order, now, cancellationToken);
        return order;
    }
}
```

`QuotationProductPricingResolver.ResolveManyAsync` ya rechaza producto ausente; agregarle el chequeo `!product.IsActive → quotation.item.product_inactive` que hoy sólo hace `ResolveAsync`. `QuotationMapping.ToDomain(QuotationPartiesRequest?)` es `public static`: se usa tal cual.

`CreateOrder.cs`:

```csharp
public sealed record CreateOrderCommand(
    Guid TenantId, Guid ClientId, string? PaymentMethod, string? Notes, bool IsRetail, int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties, QuotationBillingAccountRequest BillingAccount, IReadOnlyList<OrderItemAddition> Items,
    string PaymentStatus, IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs) : ICommand<OrderDetailDto>, IOrderDraft;

public interface IOrderDraft
{
    Guid ClientId { get; }
    string? PaymentMethod { get; }
    bool IsRetail { get; }
    int? GlobalScaleFloor { get; }
    QuotationPartiesRequest? Parties { get; }
    QuotationBillingAccountRequest BillingAccount { get; }
    IReadOnlyList<OrderItemAddition> Items { get; }
    string PaymentStatus { get; }
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs { get; }
}

public abstract class OrderDraftValidator<TDraft> : AbstractValidator<TDraft> where TDraft : IOrderDraft
{
    private static readonly string[] ValidPaymentStatuses = Enum.GetNames<OrderPaymentStatus>();

    protected OrderDraftValidator(bool requireBillingIdentification)
    {
        RuleFor(draft => draft.ClientId).NotEmpty();
        RuleFor(draft => draft.PaymentMethod).MaximumLength(Order.PaymentMethodMaxLength).When(draft => draft.PaymentMethod is not null);
        RuleFor(draft => draft.GlobalScaleFloor).GreaterThan(0).When(draft => draft.GlobalScaleFloor is not null);
        RuleFor(draft => draft.BillingAccount).NotNull();
        RuleFor(draft => draft.Items).Must(items => items.Count > 0).WithMessage("At least one product is required.");
        RuleForEach(draft => draft.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.ProductId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
        });
        RuleFor(draft => draft.Items)
            .Must(items => items.Select(line => line.ProductId).Distinct().Count() == items.Count)
            .WithMessage("The same product cannot appear more than once.");
        RuleFor(draft => draft.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        RuleForEach(draft => draft.PaymentProofs).SetValidator(new OrderPaymentProofRequestValidator());
        RuleFor(draft => draft.PaymentProofs)
            .Must(proofs => proofs.Select(proof => proof.FileId).Distinct().Count() == proofs.Count)
            .WithMessage("The same file cannot be attached more than once in a request.");
        RuleFor(draft => draft.Parties!)
            .SetValidator(new QuotationPartiesRequestValidator(requireBillingIdentification))
            .When(draft => draft.Parties is not null);
    }
}

public sealed class CreateOrderValidator : OrderDraftValidator<CreateOrderCommand>
{
    public CreateOrderValidator() : base(requireBillingIdentification: true) { }
}

public sealed class CreateOrderHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    OrderDraftBuilder drafts,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<CreateOrderCommand> validator) : ICommandHandler<CreateOrderCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(fileLookup, orderRepository, command.TenantId, proof.FileId, exceptProofId: null, cancellationToken);
        }

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var format = await numberingFormats.GetAsync(command.TenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? calendar.Today.Year : 0;
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);
        var copies = new PaymentProofCopies(paymentProofPublisher);
        Order order;
        try
        {
            var proofs = await copies.PublishAsync(command.TenantId, command.PaymentProofs, cancellationToken);
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            var sequence = await numberGenerator.NextAsync(command.TenantId, counterYear, cancellationToken);
            var orderNumber = DocumentNumberFormatter.Format(format, calendar.Today.Year, sequence);
            order = await drafts.BuildAsync(
                new OrderDraftInput(command.TenantId, command.ClientId, advisorId, command.PaymentMethod, command.Notes, command.IsRetail,
                    command.GlobalScaleFloor, command.Parties, command.BillingAccount, command.Items, paymentStatus, proofs,
                    OrderSource.Manual, null, AllowIncompleteCustomer: false),
                orderNumber, now, cancellationToken);
            orderRepository.Add(order);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, advisorId, OrderChangeSummary.CreatedManually(), now));
            auditPublisher.Publish(command.TenantId, executionContext.SubjectId, "quotation.order.created", order.Id.ToString(), "success", now);
            var attached = PaymentProofCopies.AttachedFrom(proofs);
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            lifecycleEvents.PublishOrderCreated(order, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return await OrderDetails.LoadAsync(orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
```

`PreviewOrder.cs`: `PreviewOrderQuery(…mismos campos…) : IQuery<OrderDetailDto>, IOrderDraft` con `PreviewOrderValidator : OrderDraftValidator<PreviewOrderQuery>` (`requireBillingIdentification: false`, como el preview de cotizaciones); el handler autoriza `OrderManage`, resuelve el asesor, llama `drafts.BuildAsync(input, orderNumber: "PREVIEW", now, …)` con `Proofs` convertidos a `OrderPaymentProofInput(Guid.CreateVersion7(), amount, PaidOn)` y devuelve `new OrderDetailDto(order.ToDto() with { OrderNumber = string.Empty, Id = Guid.Empty }, null, [])`. No toca el consecutivo ni guarda.

Endpoints en `OrderEndpoints`:

```csharp
        collection.MapPost("/", CreateOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderDetailResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        collection.MapPost("/preview", PreviewOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

`CreateOrderAsync` arma el comando (`Items` → `OrderItemAddition`), despacha, compone con `IOrderResponseComposer` y responde `Results.Created($"/api/v1/tenants/{tenantId}/orders/{detail.Order.Id}", response)`. `PreviewOrderAsync` responde `Results.Ok`. La ruta `/preview` se registra **antes** que `/{orderId:guid}/preview` no hace falta: son plantillas distintas.

Registro en `QepServiceCollectionExtensions`: `services.AddScoped<OrderDraftBuilder>();`, `ICommandHandler<CreateOrderCommand, OrderDetailDto>`, `IQueryHandler<PreviewOrderQuery, OrderDetailDto>`, y los dos validadores con `AddScoped<IValidator<…>, …>()` como los demás.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~CreateOrderValidatorTests"` y `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~ManualOrderApiTests"`
Expected: 4 + 5 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations
git commit -m "feat(quotations): pedido manual sin cotización con preview"
```

---

### Task 13: `IExternalOrderWriter`: pedidos desde integraciones, sin HTTP

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExternalOrderServiceTests.cs`

**Interfaces:**
- Produces: `public interface IExternalOrderWriter { Task<ExternalOrderResult> CreateAsync(ExternalOrderDraft draft, CancellationToken); Task SyncPaymentStatusAsync(Guid tenantId, Guid orderId, OrderPaymentStatus status, CancellationToken); Task CancelAsync(Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken); }`; `ExternalOrderDraft(Guid TenantId, OrderSource Source, string ExternalReference, Guid ClientId, Guid AdvisorMembershipId, string? PaymentMethod, string? Notes, QuotationPartiesRequest? Parties, QuotationBillingAccountRequest BillingAccount, IReadOnlyList<ExternalOrderLine> Lines, OrderPaymentStatus PaymentStatus, DateTimeOffset OccurredAt)`; `ExternalOrderLine(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage)`; `ExternalOrderResult(Guid OrderId, string OrderNumber, decimal Total, decimal NetTotal, string Currency)`. Es el puerto que el spec de Shopify consume por su adaptador en Bootstrapper; la implementación `ExternalOrderService` vive en Application y la registra Bootstrapper.
- Diferencia con el manual: las líneas llegan **ya preciadas** (precio con IVA, descuento, tasa) y el cliente puede ser `Incomplete` (`AllowIncompleteCustomer: true`). `SyncPaymentStatusAsync` sólo cambia si el pedido no tiene comprobantes (`Order.SyncExternalPaymentStatus`, método de dominio nuevo que lanza `order.order.payment_has_proofs` si los hay).

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
public sealed class ExternalOrderServiceTests
{
    [Fact]
    public async Task CreatesAnOrderForAnIncompleteCustomerAndPublishesOrderCreated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var incompleteId = await IncompleteCustomerQuotationApiTests.CreateIncompleteCustomerAsync(factory, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId); // helper: la membresía del owner del tenant (ver QuotationAdvisorNameApiTests)

        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var result = await writer.CreateAsync(new ExternalOrderDraft(
            tenantId, OrderSource.Shopify, "#1001", incompleteId, advisorId, "shopify_payments", "Shopify #1001", null,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            [new ExternalOrderLine(productId, 2m, 11_900m, 0m, 19)], OrderPaymentStatus.FullPaymentReceived, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("PED-", result.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(23_800m, result.Total);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal(("Shopify", "#1001", "FullPaymentReceived"), (detail!.Order.Source, detail.Order.ExternalReference, detail.Order.PaymentStatus));
        Assert.Single(await OutboxMessagesAsync(factory, tenantId), message => message.EventName == "quotations.order.created.v1");
    }

    [Fact]
    public async Task SyncPaymentStatusIsRejectedWhenTheOrderHasProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var orderNumber = await OrderApiTests.ConvertOneAsync(client, factory, tenantId, clientId, productId); // con un comprobante
        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{tenantId}/orders?orderNumber={orderNumber}", TestContext.Current.CancellationToken);
        var orderId = Assert.Single(page!.Items).Id;

        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            writer.SyncPaymentStatusAsync(tenantId, orderId, OrderPaymentStatus.PaymentPending, TestContext.Current.CancellationToken));

        Assert.Equal("order.order.payment_has_proofs", error.Code);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{orderId}", TestContext.Current.CancellationToken);
        Assert.Equal("FullPaymentReceived", detail!.Order.PaymentStatus);
    }
}
```

`OwnerMembershipIdAsync(factory, tenantId)` se agrega al harness: abre un scope, lee `TenancyDbContext` (o el puerto `IMembershipDirectory`) y devuelve la membresía activa del owner del tenant; `QuotationAdvisorNameApiTests` ya hace una lectura equivalente, reutilizarla.

```csharp
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~ExternalOrderServiceTests"`
Expected: no compila (`IExternalOrderWriter` no existe).

- [ ] **Step 3: Implementar**

```csharp
// IExternalOrderWriter.cs
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ExternalOrderLine(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);

public sealed record ExternalOrderDraft(
    Guid TenantId, OrderSource Source, string ExternalReference, Guid ClientId, Guid AdvisorMembershipId, string? PaymentMethod,
    string? Notes, QuotationPartiesRequest? Parties, QuotationBillingAccountRequest BillingAccount,
    IReadOnlyList<ExternalOrderLine> Lines, OrderPaymentStatus PaymentStatus, DateTimeOffset OccurredAt);

public sealed record ExternalOrderResult(Guid OrderId, string OrderNumber, decimal Total, decimal NetTotal, string Currency);

/// <summary>Spec 2026-10-15 §4.4 y spec de Shopify §6.2: el único camino para crear un pedido sin sesión. Un módulo
/// consumidor declara su propio puerto y el adaptador en Bootstrapper llama a éste.</summary>
public interface IExternalOrderWriter
{
    Task<ExternalOrderResult> CreateAsync(ExternalOrderDraft draft, CancellationToken cancellationToken);
    Task SyncPaymentStatusAsync(Guid tenantId, Guid orderId, OrderPaymentStatus status, CancellationToken cancellationToken);
    Task CancelAsync(Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken cancellationToken);
}

public sealed class ExternalOrderService(
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductLookup productLookup,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    ITenantClock tenantClock) : IExternalOrderWriter
{
    public async Task<ExternalOrderResult> CreateAsync(ExternalOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var customer = await customerLookup.FindAsync(draft.TenantId, draft.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, draft.TenantId, draft.ClientId, allowIncomplete: true);
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(companyLookup, draft.TenantId, draft.BillingAccount, cancellationToken)
            ?? throw new QuotationsDomainException("order.billing.account_required", "An order needs a billing account.");
        var products = await productLookup.FindManyAsync(draft.TenantId, draft.Lines.Select(line => line.ProductId).Distinct().ToArray(), cancellationToken);
        var lines = draft.Lines.Select(line =>
        {
            products.TryGetValue(line.ProductId, out var product);
            return new OrderLineInput(line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.TaxPercentage,
                product is null ? null : new QuotationProductSnapshot(product.Code, product.Name));
        }).ToArray();
        var advisor = new MemberId(draft.AdvisorMembershipId);
        var header = new OrderHeader(draft.ClientId, advisor, billingAccount.Currency, draft.PaymentMethod, false, null, customer.WithRetention, customer.VatSurplus);
        var calendar = await tenantClock.GetAsync(draft.TenantId, cancellationToken);
        var format = await numberingFormats.GetAsync(draft.TenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? calendar.Today.Year : 0;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var sequence = await numberGenerator.NextAsync(draft.TenantId, counterYear, cancellationToken);
        var orderNumber = DocumentNumberFormatter.Format(format, calendar.Today.Year, sequence);
        var order = Order.CreateExternal(
            OrderId.New(), draft.TenantId, orderNumber, header, draft.Parties.ToDomain(), billingAccount, lines, draft.PaymentStatus,
            draft.Notes, advisor, [], draft.OccurredAt, draft.Source, draft.ExternalReference);
        orderRepository.Add(order);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, null,
            OrderChangeSummary.CreatedFromExternal(draft.Source, draft.ExternalReference), draft.OccurredAt));
        auditPublisher.Publish(draft.TenantId, Guid.Empty, "quotation.order.created", order.Id.ToString(), "success", draft.OccurredAt);
        lifecycleEvents.PublishOrderCreated(order, draft.OccurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ExternalOrderResult(order.Id.Value, order.OrderNumber, order.Total, order.NetTotal, order.Currency);
    }

    public async Task SyncPaymentStatusAsync(Guid tenantId, Guid orderId, OrderPaymentStatus status, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken) ?? throw OrderNotFound.ById(orderId);
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        order.SyncExternalPaymentStatus(status, now);
        auditPublisher.Publish(tenantId, Guid.Empty, "quotation.order.payment_status_synced", order.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken) ?? throw OrderNotFound.ById(orderId);
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        var by = new MemberId(cancelledByMembershipId);
        order.Cancel(by, reason, now);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Cancelled, by, OrderChangeSummary.Cancelled(order.CancellationReason!), now));
        auditPublisher.Publish(tenantId, Guid.Empty, "quotation.order.cancelled", order.Id.ToString(), "success", now);
        lifecycleEvents.PublishOrderCancelled(order, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
```

`IQuotationAuditPublisher.Publish` recibe un `Guid subjectId`: con `Guid.Empty` la auditoría queda «por el sistema», como hace Pipeline (`audit.Record(tenantId, Guid.Empty, true, …)`). Si el publicador de Quotations exige actor, agregarle una sobrecarga `PublishBySystem` que escriba `AuditActorType.Integration`.

Método de dominio en `Order`:

```csharp
    /// <summary>Spec de Shopify §6.3: el estado de pago que avisa la plataforma, sólo si el pedido no tiene comprobantes
    /// propios; con comprobantes, el estado lo gobiernan ellos.</summary>
    public void SyncExternalPaymentStatus(OrderPaymentStatus status, DateTimeOffset occurredAt)
    {
        EnsurePending("The payment status can only be synced on a pending order.");
        if (_paymentProofs.Count > 0)
        {
            throw new QuotationsDomainException("order.order.payment_has_proofs", "The payment status is governed by the attached proofs.");
        }

        if (PaymentStatus == status) return;
        PaymentStatus = status;
        UpdatedAt = occurredAt;
        Version++;
    }
```

Registro: `services.AddScoped<IExternalOrderWriter, ExternalOrderService>();`.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~ExternalOrderServiceTests"`
Expected: 2 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Quotations src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations
git commit -m "feat(quotations): servicio de pedidos externos para integraciones"
```

---

### Task 14: Pipeline: `deal_orders` y los eventos de pedido

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Domain/DealOrder.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Domain/PipelineEnums.cs` (`DealSource.Order`), `Modules.Pipeline.Application/QuotationEvents.cs`, `PipelinePorts.cs` (`IDealOrderRepository`, `PipelineAuditActions.OrderLinked`), `PipelineDtos.cs` (`DealOrderDto`, `DealDetailDto.Orders`), `DealDetailBuilder.cs`, `Modules.Pipeline.Infrastructure/Messaging/QuotationEventPayloads.cs`, `QuotationEventsProcessor.cs`, `Persistence/PipelineDbContext.cs`, `PipelineRepositories.cs`, migración `AddDealOrders`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.UnitTests/QuotationEventPayloadsTests.cs`, `QuotationEventApplierTests.cs`

**Interfaces:**
- Produces: `OrderFact(Guid TenantId, Guid OrderId, string OrderNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, string Source, Guid? QuotationId)`; `OrderCancelledFact` pasa a `(Guid TenantId, Guid OrderId, Guid? QuotationId, string Source)`; `QuotationEventPayloads.TryReadOrderCreated(string json, out OrderFact?)`; `QuotationEventApplier.ApplyOrderCreatedAsync(OrderFact, DateTimeOffset, CancellationToken) : Task<bool>`; `DealOrder` (`TenantId`, `OrderId`, `DealId?`, `CustomerId`, `OrderNumber`, `Total`, `Currency`, `Source`, `CancelledAt?`, `UpdatedAt`) con `Create`, `LinkTo`, `MarkCancelled`; `IDealOrderRepository { FindAsync(tenantId, orderId); ListByDealAsync(tenantId, dealId); Add(DealOrder) }`; `DealOrderDto(Guid OrderId, string OrderNumber, decimal Total, string Currency, string Source, bool Cancelled)`; `DealDetailDto` gana `IReadOnlyList<DealOrderDto> Orders` al final.

- [ ] **Step 1: Escribir las pruebas que fallan**

`QuotationEventPayloadsTests.cs`:

```csharp
    [Fact]
    public void AnOrderCreatedPayloadIsReadByNameWithANullQuotation()
    {
        var json = $$"""{"tenantId":"{{TenantId}}","orderId":"{{OrderId}}","orderNumber":"PED-0003","clientId":"{{ClientId}}","advisorMemberId":"{{AdvisorId}}","total":500.00,"currency":"COP","source":"Shopify","quotationId":null,"occurredAt":"2026-10-15T15:00:00+00:00"}""";

        Assert.True(QuotationEventPayloads.TryReadOrderCreated(json, out var fact));

        Assert.Equal((TenantId, OrderId, "PED-0003", ClientId, AdvisorId, 500.00m, "COP", "Shopify", (Guid?)null),
            (fact!.TenantId, fact.OrderId, fact.OrderNumber, fact.ClientId, fact.AdvisorMemberId, fact.Total, fact.Currency, fact.Source, fact.QuotationId));
    }

    [Fact]
    public void ACancelledPayloadAcceptsANullQuotation()
    {
        var json = $$"""{"tenantId":"{{TenantId}}","orderId":"{{OrderId}}","quotationId":null,"source":"Manual","occurredAt":"2026-10-15T15:00:00+00:00"}""";

        Assert.True(QuotationEventPayloads.TryReadOrderCancelled(json, out var fact));

        Assert.Equal(((Guid?)null, "Manual"), (fact!.QuotationId, fact.Source));
    }
```

`QuotationEventApplierTests.cs` (usa `ApplierBed`; agregarle `OrderFact(Guid clientId, Guid orderId, decimal total, string source = "Manual")` y `Orders` como lista en memoria del `IDealOrderRepository` falso, al lado de `Quotations`):

```csharp
    [Fact]
    public async Task AnOrderWithoutQuotationAndNoOpenDealCreatesAWonDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        Assert.True(await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, orderId, 500m, "Shopify"), Later, Ct));

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((DealSource.Order, bed.AdvisorId, clientId, bed.Pipeline.WonStage.Id, (decimal?)500m), (deal.Source, deal.OwnerMemberId, deal.CustomerId, deal.StageId, deal.Amount));
        Assert.Equal((Guid?)deal.Id, Assert.Single(bed.Orders.Items).DealId);
        Assert.Equal([PipelineAuditActions.DealCreated, PipelineAuditActions.OrderLinked, PipelineAuditActions.DealWon], bed.Audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task AnOrderWithoutQuotationWinsTheOnlyOpenDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);

        await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, Guid.CreateVersion7(), 700m), Later, Ct);

        Assert.Single(bed.Deals.Items);
        Assert.Equal(bed.Pipeline.WonStage.Id, deal.StageId);
    }

    [Fact]
    public async Task AnOrderWithAQuotationDoesNothing()
    {
        var bed = new ApplierBed();
        var fact = bed.OrderFact(Guid.CreateVersion7(), Guid.CreateVersion7(), 700m, "Quotation") with { QuotationId = Guid.CreateVersion7() };

        Assert.True(await bed.Applier.ApplyOrderCreatedAsync(fact, Later, Ct));

        Assert.Empty(bed.Deals.Items);
        Assert.Empty(bed.Orders.Items);
    }

    // Review Focus 5: un reintento del outbox no crea un segundo negocio.
    [Fact]
    public async Task ARepeatedOrderCreatedIsIdempotent()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var fact = bed.OrderFact(clientId, Guid.CreateVersion7(), 500m);
        await bed.Applier.ApplyOrderCreatedAsync(fact, Later, Ct);

        await bed.Applier.ApplyOrderCreatedAsync(fact, Later.AddMinutes(5), Ct);

        Assert.Single(bed.Deals.Items);
        Assert.Single(bed.Orders.Items);
    }

    [Fact]
    public async Task CancellingAnOrderWithoutQuotationReopensItsWonDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, orderId, 500m), Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, orderId, null, "Manual"), Later.AddHours(1), Ct);

        Assert.NotEqual(bed.Pipeline.WonStage.Id, deal.StageId);
        Assert.NotNull(Assert.Single(bed.Orders.Items).CancelledAt);
    }
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests --no-restore --filter "FullyQualifiedName~QuotationEventPayloadsTests|FullyQualifiedName~QuotationEventApplierTests"`
Expected: no compila (`TryReadOrderCreated`, `OrderFact`, `ApplyOrderCreatedAsync`, `OrderCancelledFact` con cuatro campos).

- [ ] **Step 3: Implementar**

```csharp
// Modules.Pipeline.Domain/DealOrder.cs
namespace Modules.Pipeline.Domain;

/// <summary>Spec 2026-10-15 §7: la foto de un pedido sin cotización, paralela a <see cref="DealQuotation"/>.
/// Un pedido que viene de una cotización no deja foto acá: el <c>converted</c> de la cotización ya actuó.</summary>
public sealed class DealOrder
{
    public const int NumberMaxLength = 40;
    public const int SourceMaxLength = 20;

    private DealOrder()
    {
        OrderNumber = string.Empty;
        Currency = string.Empty;
        Source = string.Empty;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid? DealId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string OrderNumber { get; private set; }
    public decimal Total { get; private set; }
    public string Currency { get; private set; }
    public string Source { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static DealOrder Create(Guid tenantId, Guid orderId, string orderNumber, Guid customerId, decimal total, string currency, string source, DateTimeOffset now) =>
        new() { TenantId = tenantId, OrderId = orderId, OrderNumber = orderNumber, CustomerId = customerId, Total = total, Currency = currency, Source = source, UpdatedAt = now };

    public void LinkTo(Guid dealId, DateTimeOffset now) { DealId = dealId; UpdatedAt = now; }

    public bool MarkCancelled(DateTimeOffset now)
    {
        if (CancelledAt is not null) return false;
        CancelledAt = now;
        UpdatedAt = now;
        return true;
    }
}
```

`PipelineEnums.cs`: `DealSource` gana `Order` al final. Migración de Pipeline: ampliar el `CHECK` de `deals.source` si existe (buscar `CK_deals_source` en `PipelineDbContext`) con `'Order'`.

`QuotationEvents.cs`:

```csharp
public sealed record OrderFact(
    Guid TenantId, Guid OrderId, string OrderNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, string Source, Guid? QuotationId);

public sealed record OrderCancelledFact(Guid TenantId, Guid OrderId, Guid? QuotationId, string Source);

// QuotationEventApplier gana `IDealOrderRepository orders` en el constructor y:

    public async Task<bool> ApplyOrderCreatedAsync(OrderFact fact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.QuotationId is not null) return true; // el converted de la cotización ya ganó el negocio
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken)) return false;
        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);
        var snapshot = await orders.FindAsync(fact.TenantId, fact.OrderId, cancellationToken);
        if (snapshot is not null) return true; // idempotente por (tenant, pedido)
        snapshot = DealOrder.Create(fact.TenantId, fact.OrderId, fact.OrderNumber, fact.ClientId, fact.Total, fact.Currency, fact.Source, now);
        orders.Add(snapshot);
        var open = await deals.ListOpenByCustomerAsync(fact.TenantId, fact.ClientId, cancellationToken);
        if (open.Count > 1) return true; // D-O8: queda suelta; el asesor elige desde el negocio
        Deal deal;
        if (open.Count == 1)
        {
            deal = open[0];
        }
        else
        {
            deal = Deal.Create(fact.TenantId, pipeline, fact.ClientId, fact.AdvisorMemberId, fact.OrderNumber, fact.Total, fact.Currency, null,
                DealSource.Order, null, DealActor.ForSystem, now);
            deals.Add(deal);
            Record(fact.TenantId, PipelineAuditActions.DealCreated, deal.Id, now);
        }

        snapshot.LinkTo(deal.Id, now);
        Record(fact.TenantId, PipelineAuditActions.OrderLinked, deal.Id, now);
        if (deal.PrimaryQuotationId is null && deal.Amount is null)
        {
            deal.Update(deal.Title, deal.OwnerMemberId, fact.Total, fact.Currency, deal.ExpectedCloseDate, now);
        }

        if (!deal.IsClosed)
        {
            deal.Win(pipeline, DealActor.ForSystem, now);
            Record(fact.TenantId, PipelineAuditActions.DealWon, deal.Id, now);
        }

        return true;
    }

    // ApplyOrderCancelledAsync: si fact.QuotationId es null → foto en orders; MarkCancelled; si su DealId apunta a un negocio en
    // la etapa ganada, Reopen(pipeline, await deals.FindLastOpenStageIdAsync(...), DealActor.ForSystem, now) y Record(DealReopened).
    // Con QuotationId → el camino de hoy sin cambios (usa fact.QuotationId.Value).
```

`PipelinePorts.cs`: `IDealOrderRepository` y `PipelineAuditActions.OrderLinked = "pipeline.deal.order_linked"`. `PipelineRepositories.cs`: `DealOrderRepository(PipelineDbContext)` calcando `DealQuotationRepository`. `PipelineDbContext`: `DbSet<DealOrder> DealOrders` y tabla `deal_orders` (clave `(tenant_id, order_id)`, columnas `deal_id`, `customer_id`, `order_number` (40), `total` numeric(14,2), `currency` (3), `source` (20), `cancelled_at`, `updated_at`, índices `IX_deal_orders_deal (tenant_id, deal_id)` y `IX_deal_orders_customer (tenant_id, customer_id)`). Registrar el repositorio en `PipelineInfrastructureExtensions` junto al de cotizaciones.

Migración: `dotnet ef migrations add AddDealOrders --project src/Modules/Pipeline/Modules.Pipeline.Infrastructure --context PipelineDbContext -o Persistence/Migrations`.

`QuotationEventPayloads.cs`:

```csharp
    public static bool TryReadOrderCreated(string json, [NotNullWhen(true)] out OrderFact? fact)
    {
        fact = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var number = root.GetProperty("orderNumber").GetString();
            var currency = root.GetProperty("currency").GetString();
            var source = root.GetProperty("source").GetString();
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(currency) || string.IsNullOrWhiteSpace(source)) return false;
            fact = new OrderFact(
                root.GetProperty("tenantId").GetGuid(), root.GetProperty("orderId").GetGuid(), number, root.GetProperty("clientId").GetGuid(),
                root.GetProperty("advisorMemberId").GetGuid(), root.GetProperty("total").GetDecimal(), currency, source, OptionalGuid(root, "quotationId"));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static Guid? OptionalGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetGuid() : null;

    // TryReadOrderCancelled: quotationId con OptionalGuid; source con GetString() (si falta, "Quotation": los mensajes anteriores a este cambio no lo traen).
```

`QuotationEventsProcessor`: `OrderCreatedEvent = "quotations.order.created.v1"` en la lista de `ClaimedOutboxConsumer` y un `case OrderCreatedEvent when QuotationEventPayloads.TryReadOrderCreated(record.PayloadJson, out var created): await applier.ApplyOrderCreatedAsync(created, message.Now, cancellationToken);`.

`DealDetailBuilder`: inyecta `IDealOrderRepository`, lista `ListByDealAsync` y mapea a `DealOrderDto(order.OrderId, order.OrderNumber, order.Total, order.Currency, order.Source, order.CancelledAt is not null)` en `DealDetailDto.Orders`.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests --no-restore`
Expected: PASS, incluidos los 7 nuevos. Luego `dotnet test tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests --no-restore --filter "FullyQualifiedName~QuotationEvents"` en verde (el worker consume `order.created`).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pipeline tests/Modules/Pipeline
git commit -m "feat(pipeline): un pedido sin cotización gana o crea el negocio"
```

---

### Task 15: Reporte de pedidos desde el pedido

**Files:**
- Modify: `src/Bootstrapper/OrdersReportSource.cs`, `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs` (`OrdersReportItemDto`), `Modules.Reporting.Api` (respuesta del listado, campos opcionales)
- Test: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportApiTests.cs`

**Interfaces:**
- Produces: `OrdersReportItemDto` con `Guid? QuotationId`, `string? QuotationNumber` y `string Source` nuevo tras `OrderNumber`.

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
    [Fact]
    public async Task TheReportListsAManualOrderWithoutQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, [.. ReportingPermissions, "quotations.order.manage", "catalog.product.manage", "customers.customer.manage", "companies.company.manage"]);
        using var _ = client;
        var orderNumber = await CreateManualOrderAsync(client, tenantId);

        var page = await client.GetFromJsonAsync<ReportPageResponse<OrdersReportItemResponse>>(
            $"/api/v1/tenants/{tenantId}/reports/orders?from=2026-01-01&to=2026-12-31", TestContext.Current.CancellationToken);

        var row = Assert.Single(page!.Items, item => item.OrderNumber == orderNumber);
        Assert.Equal(("Manual", (Guid?)null, (string?)null), (row.Source, row.QuotationId, row.QuotationNumber));
        Assert.True(row.Total > 0);
    }

    // Helper nuevo de ReportingApiHarness: cliente activo, producto con escalas y empresa con cuenta por los endpoints
    // (los mismos helpers de QuotationsApiHarness que ya usa ConvertToOrderAsync), y luego POST /orders.
    private static async Task<string> CreateManualOrderAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, null, null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 1m)], "PaymentPending", []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        return detail!.Order.OrderNumber;
    }
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-restore --filter "FullyQualifiedName~TheReportListsAManualOrderWithoutQuotation"`
Expected: no compila (`Source` en la respuesta) o el join a `quotations` deja fuera el pedido.

- [ ] **Step 3: Implementar**

`OrdersReportSource`: eliminar `FilterQuotations` y el join; `FilterOrders` aplica `criteria.AdvisorId` (`order.AdvisorId == new MemberId(advisorId)`) y `criteria.ClientId` (`order.ClientId == clientId`); todas las agrupaciones usan `order.Currency`, `order.Subtotal`, `order.TaxAmount`, `order.Total`, `order.AdvisorId`, `order.ClientId`; `BuildQuery` proyecta `OrderRow(order.Id, order.OrderNumber, order.Source, order.QuotationId, order.ConvertedAt, order.AdvisorId, order.ClientId, order.Status, order.PaymentStatus, order.Currency, order.Subtotal, order.TaxAmount, order.Total)` y `ToDtosAsync` resuelve los números de cotización con una consulta `quotations.Quotations.Where(q => ids.Contains(q.Id)).Select(q => new { q.Id, q.QuotationNumber })` para los `QuotationId` no nulos.

`ReportingDtos.cs`:

```csharp
public sealed record OrdersReportItemDto(
    Guid OrderId, string OrderNumber, string Source, Guid? QuotationId, string? QuotationNumber, DateTimeOffset ConvertedAt,
    Guid AdvisorId, string? AdvisorName, Guid ClientId, string? ClientName, string? ClientCuc, string Status, string PaymentStatus,
    string Currency, decimal Subtotal, decimal TaxAmount, decimal Total);
```

La respuesta HTTP del reporte (`Modules.Reporting.Api`) espeja los tres campos. Las pruebas unitarias de Reporting que construyen `OrdersReportItemDto` (`OrdersReportHandlerTests`, `ReportingTestDoubles`) reciben `"Quotation"` y los opcionales.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-restore` y `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-restore --filter "FullyQualifiedName~OrdersReport"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Bootstrapper/OrdersReportSource.cs src/Modules/Reporting tests/Modules/Reporting
git commit -m "feat(reporting): el reporte de pedidos lee del pedido e incluye los que no tienen cotización"
```

---

### Task 16: Siembras, sondas y otros lectores de `quotations.orders`

**Files:**
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:365-401`, `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs` (`SeedOrderAsync` ~:800), `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationUserReferenceProbe.cs:49-58`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs`, `OrphanUserCleanupTests.cs`

- [ ] **Step 1: Ajustar la prueba de la siembra**

En `ExportLoadSeedTests.cs`, el caso que cuenta pedidos sembrados agrega:

```csharp
        Assert.Equal(expectedOrders, await ScalarAsync<long>(connectionString,
            $"SELECT count(*) FROM quotations.order_items i JOIN quotations.orders o ON o.id = i.order_id WHERE o.tenant_id = '{tenantId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            $"SELECT count(*) FROM quotations.orders WHERE tenant_id = '{tenantId}' AND client_id IS NULL"));
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~ExportLoadSeedTests"`
Expected: FAIL (`INSERT INTO quotations.orders` sin `client_id` viola `NOT NULL`).

- [ ] **Step 3: Implementar**

`ExportLoadSeeder.OrdersSql`: el `INSERT` lista además `client_id, advisor_id, currency, billing_company_id, billing_bank_name, billing_account_number, billing_account_currency, payment_method, is_retail, global_scale_floor, billing_uses_business_name, is_store_pickup, bills_to_final_consumer, party_with_retention, party_vat_surplus, customer_with_retention, customer_vat_surplus, subtotal, tax_percentage, tax_amount, discount_amount, total, retention_amount, net_total, source, external_reference` tomados de `quotations.quotations q` (join por `numbered.quotation_id = q.id`), con `source = 'Quotation'` y `external_reference = NULL`. Después del `INSERT` de pedidos, dos sentencias nuevas que copian `quotation_items` → `order_items` y `quotation_parties` → `order_parties` para los pedidos del tenant (mismo SQL que la migración, Task 5 paso 5, acotado por `o.tenant_id = @tenant`).

`OrphanUserCleanupTests.SeedOrderAsync`: reemplaza `Order.Create(...)` por `Order.FromQuotation(OrderId.New(), tenantId, $"PED-{Guid.NewGuid():N}"[..20], quotation, OrderPaymentStatus.PaymentPending, null, convertedBy, [], occurredAt)` después de darle a la cotización una cuenta de cobro y una línea (`quotation.AddItem(...)` con un `productId` cualquiera; `Quotation.Create(... billingAccount: new QuotationBillingAccount { CompanyId = Guid.CreateVersion7(), BankName = "B", AccountNumber = "1", Currency = "COP" } ...)`) y de llamar `quotation.ConvertToOrder(convertedBy, occurredAt, QuotationProductSnapshot.None)`.

`QuotationUserReferenceProbe`: agregar `order.AdvisorId == member` al `AnyAsync` de pedidos (un pedido manual referencia a su asesor sin pasar por una cotización) y `OrderHistoryEntries.AnyAsync(entry => entry.MemberId == member)`.

- [ ] **Step 4: Correr y ver que pasa**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~ExportLoadSeedTests|FullyQualifiedName~PaymentProofReferenceProbeTests"` y `dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --no-restore --filter "FullyQualifiedName~OrphanUserCleanupTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Bootstrapper/Seeding/ExportLoadSeeder.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationUserReferenceProbe.cs tests/Modules/Identity tests/Modules/Quotations
git commit -m "chore(quotations): siembras y sondas con los datos propios del pedido"
```

---

### Task 17: Documentación y cierre

**Files:**
- Modify: `README.md` (§ «API implementada»: rutas `POST /orders`, `POST /orders/preview`, `source` en `GET /orders`; § de cotizaciones y pedidos: el modelo nuevo, el pedido manual, la copia independiente; § «Columnas del Excel de pedidos»: `Observaciones` desde el pedido), `CLAUDE.md` (gotcha nuevo), `docs/integracion-cotizaciones-y-pedidos.md` (forma del pedido, rutas, códigos), `docs/superpowers/specs/2026-10-15-pedidos-sin-cotizacion-design.md` (historial: P1-P5 del plan), `docs/superpowers/specs/2026-10-15-integracion-shopify-pedidos-design.md` (§6.2.3 y §6.5: nombres reales `IExternalOrderWriter`, `ExternalOrderDraft`, `Order.CreateExternal`, `OrderCustomerEligibility`; D-S21 resuelta)

- [ ] **Step 1: Escribir**

CLAUDE.md, en «Gotchas verificados», después del gotcha del payload de eventos de Quotations:

```markdown
- **Un pedido tiene datos propios y puede no tener cotización** (spec 2026-10-15). `Order` lleva cliente,
  asesor, cuenta de cobro, partes, líneas y totales; `QuotationId` es opcional y `Source` dice de dónde nació
  (`Quotation`, `Manual`, `Shopify`). Convertir **copia**: la cotización `Converted` queda congelada y editar un
  pedido pendiente edita `order_items`, nunca `quotation_items`. Un pedido sin sesión se crea sólo por
  `IExternalOrderWriter` (acepta cliente incompleto); el manual exige cliente completo con CUC. Los eventos
  `quotations.order.created.v1` y `order.cancelled.v1` llevan `source` y `quotationId` nullable: Pipeline los lee
  por nombre y guarda la foto en `pipeline.deal_orders`. `Observaciones` del Excel sale de `Order.Notes`, que al
  convertir hereda las de la cotización.
```

README: en la tabla de rutas de pedidos agregar `POST` y `POST /preview` con permiso `quotations.order.manage`, y `source` entre los filtros de `GET /orders` y del export; en la sección del modelo, reemplazar «1:1 con la cotización» por el modelo nuevo; en «Columnas del Excel», la frase de `notes`.

`docs/integracion-cotizaciones-y-pedidos.md`: reemplazar la forma del `Order` (:139) por `OrderResponse` con los campos de Task 8, documentar `OrderDetailResponse` (sin `quotation`, con `sourceQuotation`, `items`, `history`), las dos rutas nuevas con un cuerpo de ejemplo y los códigos `order.order.client_not_found`, `client_incomplete`, `client_cuc_missing`, `client_inactive`, `items_required`, `order.billing.account_required`, `order.shipping.party_incomplete`, `order.billing.party_incomplete`, `order.billing.identification_required`, `order.item.*`.

Spec de pedidos sin cotización, historial: «2026-10-15: plan escrito; decisiones del plan P1-P5 (sin renombrar los records compartidos; `Notes` sin tope; `ConvertedAt/By` como creado el/por; `OrderPricingRecalculation` espejo; DTOs compartidos)». Spec de Shopify: §6.2.3 cita `IExternalOrderWriter.CreateAsync(ExternalOrderDraft)` y `Order.CreateExternal`; §6.5 cita `OrderCustomerEligibility.Ensure(…, allowIncomplete: true)`; D-S21 pasa a «resuelta: `order.created` sin cotización gana o crea el negocio».

- [ ] **Step 2: Verificar**

Run: `dotnet build --no-restore` y `dotnet format --verify-no-changes`.
Expected: sin warnings ni cambios de formato pendientes.

- [ ] **Step 3: Commit**

```bash
git add README.md CLAUDE.md docs
git commit -m "docs: pedidos con datos propios y sin cotización"
```

---

## Orden de ejecución y compilación

Las tareas 3 a 11 dejan la solución sin compilar en el medio: se hacen **seguidas**, en orden, y la evidencia de las pruebas de las tareas 3, 5, 7 y 8 se toma al cerrar la 11 (primer punto en que `dotnet build` vuelve a pasar). Las tareas 12 a 16 son independientes entre sí y cada una compila sola. La 17 cierra.

## Frontend

El frontend (`qep-frontend`, con su propio developer) va en un plan aparte, `docs/superpowers/plans/2026-10-15-pedidos-sin-cotizacion-frontend.md`, que se escribe a partir del spec §8 cuando este backend esté en `develop`: la forma final de `OrderDetailResponse` y `OrderListItemResponse` es su contrato. Despliegue coordinado (spec §10).
