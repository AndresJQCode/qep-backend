using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El panel del listado de ventas: las cuatro cifras del encabezado y la comparación con el
/// período anterior.
/// </summary>
public sealed class GetSalesSummaryHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());

    private static readonly DateOnly MonthFrom = new(2026, 9, 1);
    private static readonly DateOnly MonthTo = new(2026, 9, 30);

    // Lo pendiente y lo aprobado parten el total del período: son los dos únicos estados que una
    // venta puede tener, así que sumados tienen que dar exactamente lo vendido.
    [Fact]
    public async Task SummarySplitsThePeriodBetweenPendingAndApproved()
    {
        var handler = NewHandler(
            NewSale("VEN-2026-0001", new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
                total: 300_000m),
            NewSale("VEN-2026-0002", new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
                total: 700_000m, approved: true));

        var summary = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.SaleCount);
        Assert.Equal(1_000_000m, summary.Total);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(300_000m, summary.PendingTotal);
        Assert.Equal(1, summary.ApprovedCount);
        Assert.Equal(700_000m, summary.ApprovedTotal);
        Assert.Equal(summary.PendingTotal + summary.ApprovedTotal, summary.Total);
    }

    // Lo recaudado son los comprobantes cargados sobre esas ventas, no otra lectura de caja.
    [Fact]
    public async Task SummaryAddsUpThePaymentProofsOfThePeriod()
    {
        var handler = NewHandler(
            NewSale("VEN-2026-0001", new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
                total: 1_000_000m, proofs: [400_000m, 250_000m]));

        var summary = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(650_000m, summary.CollectedTotal);
    }

    // Fuera de la ventana no cuenta para nada: ni al total, ni a los estados, ni a lo cobrado.
    [Fact]
    public async Task SummaryIgnoresSalesOutsideTheWindow()
    {
        var handler = NewHandler(
            NewSale("VEN-2026-0001", new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
                total: 500_000m, proofs: [100_000m]),
            NewSale("VEN-2026-0008", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
                total: 900_000m, proofs: [900_000m]));

        var summary = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.SaleCount);
        Assert.Equal(500_000m, summary.Total);
        Assert.Equal(100_000m, summary.CollectedTotal);
    }

    // El período anterior es otra ventana de la misma longitud pegada antes, y no una resta sobre
    // lo ya traído: sin eso la variación del encabezado compara contra cero siempre.
    [Fact]
    public async Task SummaryTotalsTheEquallyLongWindowRightBefore()
    {
        var handler = NewHandler(
            NewSale("VEN-2026-0001", new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
                total: 1_000_000m),
            // 30 días antes del 1 de septiembre: cae dentro de la ventana anterior.
            NewSale("VEN-2026-0000", new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
                total: 800_000m),
            // Más vieja que esa ventana: no entra en ninguna de las dos.
            NewSale("VEN-2026-9999", new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
                total: 5_000_000m));

        var summary = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(1_000_000m, summary.Total);
        Assert.Equal(800_000m, summary.PreviousTotal);
    }

    // Sin ventas el panel sigue siendo un panel: ceros, no una excepción ni un hueco.
    [Fact]
    public async Task SummaryReportsZeroesForAnEmptyPeriod()
    {
        var handler = NewHandler();

        var summary = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(0, summary.SaleCount);
        Assert.Equal(0m, summary.Total);
        Assert.Equal(0m, summary.CollectedTotal);
        Assert.Equal(0m, summary.PreviousTotal);
    }

    // Llega por query string, así que un rango dado vuelta sólo se escribe a mano — pero en
    // silencio devolvería un período vacío en vez de decir que la dirección está mal.
    [Fact]
    public async Task SummaryRefusesARangeThatEndsBeforeItStarts()
    {
        var handler = NewHandler();

        var failure = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                new GetSalesSummaryQuery(TenantId, MonthTo, MonthFrom),
                TestContext.Current.CancellationToken));

        Assert.Equal("sale.summary.range_invalid", failure.Code);
    }

    // Ver el listado y ver su panel son el mismo permiso: son la misma pantalla.
    [Fact]
    public async Task SummaryRefusesSomeoneWhoCannotReadSales()
    {
        var handler = new GetSalesSummaryHandler(
            new StubSaleListRepository(),
            new StubExecutionContext(SubjectId, TenantId, SalesPermissions.SaleRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken));
    }

    private static GetSalesSummaryQuery NewQuery() => new(TenantId, MonthFrom, MonthTo);

    private static SaleWithQuotation NewSale(
        string saleNumber,
        DateTimeOffset convertedAt,
        decimal total,
        bool approved = false,
        decimal[]? proofs = null)
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            $"QUO-{saleNumber[4..]}",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 12, 31),
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            convertedAt);

        // El total de la venta es el de su cotización, y el de una cotización es el de sus
        // líneas: se siembra con una línea que valga lo que la prueba necesita en vez de
        // asignarlo a mano.
        quotation.AddItem(
            QuotationItemId.New(),
            Guid.CreateVersion7(),
            quantity: 1,
            unitPrice: total,
            discountPercentage: 0m,
            taxPercentage: 0,
            AdvisorId,
            convertedAt);

        var sale = Sale.Create(
            SaleId.New(),
            TenantId,
            saleNumber,
            quotation.Id,
            SalePaymentStatus.PaymentPending,
            notes: null,
            AdvisorId,
            (proofs ?? []).Select(amount =>
                new SalePaymentProofInput(Guid.CreateVersion7(), amount)).ToArray(),
            convertedAt);

        if (approved)
        {
            sale.Approve(AdvisorId, convertedAt);
        }

        return new SaleWithQuotation(sale, quotation);
    }

    private static GetSalesSummaryHandler NewHandler(params SaleWithQuotation[] sales) =>
        new(new StubSaleListRepository(sales), new StubExecutionContext(SubjectId, TenantId));
}
