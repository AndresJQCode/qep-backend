using Bootstrapper;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Mayorista fase 3 (Task 7): el envío de una orden vinculada crea el pedido en el mayorista. Un reintento devuelve el pedido que
/// ya existe sin revalidar nada (D-8), el asesor inactivo cae al owner (D-14), un rechazo del motor llega como
/// <c>purchases.order.wholesaler_rejected</c> sin el código interno (D-15) y una moneda distinta a la de la orden se rechaza
/// (revisión del bloque B, M2).
/// </summary>
public sealed class PurchasesWholesaleOrderIntakeTests
{
    private static readonly Guid Wholesaler = Guid.CreateVersion7();
    private static readonly Guid Buyer = Guid.CreateVersion7();
    private static readonly Guid PurchaseOrder = Guid.CreateVersion7();
    private static readonly Guid Advisor = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly Guid Company = Guid.CreateVersion7();
    private static readonly Guid Variant = Guid.CreateVersion7();
    private static readonly Guid Line = Guid.CreateVersion7();

    private readonly FakeGateway _gateway = new();
    private readonly FakeDirectory _directory = new();
    private readonly RecordingLogger _logger = new();

    public PurchasesWholesaleOrderIntakeTests()
    {
        _directory.Offers[Variant] = new WholesaleOffer(Variant, "ACE-1", "Aceite", 12500m, 4.8m, 11900m, 19m);
        _directory.ActiveMembers.Add(Advisor);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WholesaleIntakeRequest Request(string currency = "COP") =>
        new(Buyer, Wholesaler, PurchaseOrder, "OC-0007", "Entregar en bodega", [new WholesaleIntakeLine(Line, Variant, 3m)], currency);

    private PurchasesWholesaleOrderIntake Intake()
    {
        var services = new ServiceCollection()
            .AddScoped<IWholesaleOrderGateway>(_ => _gateway)
            .AddScoped<IWholesaleIntakeDirectory>(_ => _directory)
            .BuildServiceProvider();
        return new PurchasesWholesaleOrderIntake(services.GetRequiredService<IServiceScopeFactory>(), _logger);
    }

    [Fact]
    public async Task AnExistingOrderIsReturnedWithoutRevalidating()
    {
        // La relación ya está bloqueada: revalidar daría 403, pero el pedido ya salió de esta orden (D-8).
        _directory.Blocked = true;
        _gateway.Existing = _gateway.ResultOf(Line, 11900m, alreadyExisted: true);

        var result = await Intake().SubmitAsync(Request(), Ct);

        Assert.True(result.AlreadyExisted);
        Assert.Equal((_gateway.OrderId, "PED-0042"), (result.OrderId, result.OrderNumber));
        Assert.Empty(_gateway.Drafts);
        Assert.Equal(0, _directory.PriceVisibleCalls);
        // La base y el descuento salen de la oferta vigente (sin mirar la relación) cuando su precio es el del pedido.
        var line = Assert.Single(result.Lines);
        Assert.Equal((Line, 12500m, 4.8m, 11900m, 10000m), (line.LineId, line.BasePrice, line.DiscountPercent, line.UnitPrice, line.UnitCost));
    }

    // Spec 2026-11-19: el flag cruza con el resultado; el camino nuevo lo toma de los términos de la relación.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewOrderCarriesTheShowPricesOfTheTerms(bool showPrices)
    {
        _directory.TermsShowPrices = showPrices;

        var result = await Intake().SubmitAsync(Request(), Ct);

        Assert.Equal(showPrices, result.ShowPrices);
        // El pedido del mayorista siempre se crea a precio de lista: el flag sólo dice qué guarda el distribuidor.
        Assert.Equal(11900m, Assert.Single(Assert.Single(_gateway.Drafts).Lines).UnitPrice);
        Assert.Equal(10000m, Assert.Single(result.Lines).UnitCost);
    }

    // D-14: el reintento que encuentra el pedido ya creado usa el flag vivo de la lista del distribuidor.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExistingOrderCarriesTheLiveShowPricesOfTheList(bool showPrices)
    {
        _directory.OrderOffersShowPrices = showPrices;
        _gateway.Existing = _gateway.ResultOf(Line, 11900m, alreadyExisted: true);

        var result = await Intake().SubmitAsync(Request(), Ct);

        Assert.Equal(showPrices, result.ShowPrices);
    }

    // D-14: sin lista o sin relación no hay información, y sin información la orden queda sin precio.
    [Fact]
    public async Task AnExistingOrderWithoutAListHidesThePrices()
    {
        _directory.Offers.Clear();
        _directory.OrderOffersShowPrices = false;
        _gateway.Existing = _gateway.ResultOf(Line, 11900m, alreadyExisted: true);

        var result = await Intake().SubmitAsync(Request(), Ct);

        Assert.False(result.ShowPrices);
        Assert.Equal((11900m, 0m), (Assert.Single(result.Lines).BasePrice, Assert.Single(result.Lines).DiscountPercent));
    }

    [Fact]
    public async Task AnExistingOrderWhosePriceChangedShowsThePriceAsTheBase()
    {
        _gateway.Existing = _gateway.ResultOf(Line, 9520m, alreadyExisted: true);

        var result = await Intake().SubmitAsync(Request(), Ct);

        var line = Assert.Single(result.Lines);
        Assert.Equal((9520m, 0m, 9520m, 8000m), (line.BasePrice, line.DiscountPercent, line.UnitPrice, line.UnitCost));
    }

    [Fact]
    public async Task TheDraftCarriesTheTermsAndTheListPrice()
    {
        await Intake().SubmitAsync(Request(), Ct);

        var draft = Assert.Single(_gateway.Drafts);
        Assert.Equal((Wholesaler, Buyer, PurchaseOrder, "OC-0007"), (draft.TenantId, draft.BuyerTenantId, draft.PurchaseOrderId, draft.PurchaseOrderNumber));
        Assert.Equal((Customer, Advisor, Company, "COP", "Entregar en bodega"), (draft.ClientId, draft.AdvisorMembershipId, draft.BillingCompanyId, draft.Currency, draft.Notes));
        Assert.Equal([new WholesaleOrderLine(Line, Variant, 3m, 11900m)], draft.Lines);
    }

    [Fact]
    public async Task AnInactiveAdvisorFallsBackToTheOwner()
    {
        _directory.ActiveMembers.Clear();

        await Intake().SubmitAsync(Request(), Ct);

        Assert.Equal(Owner, Assert.Single(_gateway.Drafts).AdvisorMembershipId);
    }

    [Fact]
    public async Task AnOrderEngineRejectionIsWholesalerRejected()
    {
        var original = new QuotationsDomainException("order.customer.inactive", "The customer is inactive.");
        _gateway.CreateFailure = original;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Intake().SubmitAsync(Request(), Ct));

        Assert.Equal(PurchasesErrorCodes.WholesalerRejected, error.Code);
        Assert.DoesNotContain("order.customer", error.Message, StringComparison.Ordinal);
        Assert.Same(original, error.InnerException);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("order.customer.inactive", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotLinesUseTheNetUnitCost()
    {
        var result = await Intake().SubmitAsync(Request(), Ct);

        Assert.False(result.AlreadyExisted);
        Assert.Equal("COP", result.Currency);
        var line = Assert.Single(result.Lines);
        Assert.Equal(
            new LinkedLineSnapshot(Line, Variant, "ACE-1", "Aceite", 3m, 10000m, 19m, 12500m, 4.8m, 11900m),
            line);
    }

    // Una línea que el mayorista agregó antes del reintento (D-8) no trae id de la orden: la orden la adopta como línea nueva.
    [Fact]
    public async Task ALineWithoutAPurchaseOrderLineGetsANewId()
    {
        _gateway.Existing = _gateway.ResultOf(null, 11900m, alreadyExisted: true);

        var result = await Intake().SubmitAsync(Request(), Ct);

        var line = Assert.Single(result.Lines);
        Assert.NotEqual(Guid.Empty, line.LineId);
        Assert.NotEqual(Line, line.LineId);
    }

    [Fact]
    public async Task AWholesalerCurrencyChangeIsRejectedBeforeCreatingTheOrder()
    {
        _directory.Currency = "USD";

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Intake().SubmitAsync(Request("COP"), Ct));

        Assert.Equal(PurchasesErrorCodes.WholesalerRejected, error.Code);
        Assert.Empty(_gateway.Drafts);
        Assert.Contains("USD", Assert.Single(_logger.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingOrderInAnotherCurrencyIsRejected()
    {
        _gateway.Existing = _gateway.ResultOf(Line, 11900m, alreadyExisted: true) with { Currency = "USD" };

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Intake().SubmitAsync(Request("COP"), Ct));

        Assert.Equal(PurchasesErrorCodes.WholesalerRejected, error.Code);
    }

    [Fact]
    public async Task CancelWithAnOrderIdCancelsThatOrder()
    {
        await Intake().CancelAsync(Buyer, Wholesaler, PurchaseOrder, "OC-0007", _gateway.OrderId, Ct);

        Assert.Equal([(Wholesaler, _gateway.OrderId, Buyer, PurchaseOrder, "OC-0007")], _gateway.Cancelled);
    }

    // Revisión del bloque B, M1: el borrador no sabe el id del pedido que dejó un envío a medias; se busca por su clave.
    [Fact]
    public async Task CancelWithoutAnOrderIdCancelsTheOrderFoundByItsKey()
    {
        _gateway.Existing = _gateway.ResultOf(Line, 11900m, alreadyExisted: true);

        await Intake().CancelAsync(Buyer, Wholesaler, PurchaseOrder, "OC-0007", null, Ct);

        Assert.Equal([(Wholesaler, _gateway.OrderId, Buyer, PurchaseOrder, "OC-0007")], _gateway.Cancelled);
    }

    [Fact]
    public async Task CancelWithoutAnOrderIdAndNoOrderDoesNothing()
    {
        await Intake().CancelAsync(Buyer, Wholesaler, PurchaseOrder, "OC-0007", null, Ct);

        Assert.Empty(_gateway.Cancelled);
    }

    [Fact]
    public async Task ACancelTheEngineRefusesIsNotCancellable()
    {
        _gateway.CancelFailure = new QuotationsDomainException(BuyerOrderErrors.NotCancellableByBuyer, "Approved.");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Intake().CancelAsync(Buyer, Wholesaler, PurchaseOrder, "OC-0007", _gateway.OrderId, Ct));

        Assert.Equal(PurchasesErrorCodes.NotCancellable, error.Code);
        Assert.DoesNotContain(BuyerOrderErrors.NotCancellableByBuyer, error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeGateway : IWholesaleOrderGateway
    {
        public Guid OrderId { get; } = Guid.CreateVersion7();

        public WholesaleOrderResult? Existing { get; set; }

        public Exception? CreateFailure { get; set; }

        public Exception? CancelFailure { get; set; }

        public List<WholesaleOrderDraft> Drafts { get; } = [];

        public List<(Guid TenantId, Guid OrderId, Guid BuyerTenantId, Guid PurchaseOrderId, string Number)> Cancelled { get; } = [];

        public WholesaleOrderResult ResultOf(Guid? purchaseOrderLineId, decimal unitPrice, bool alreadyExisted) =>
            new(OrderId, "PED-0042", "COP",
                [new WholesaleOrderResultLine(purchaseOrderLineId, Variant, "ACE-1", "Aceite", 3m, unitPrice, 19m)],
                alreadyExisted);

        public Task<WholesaleOrderResult?> FindAsync(
            Guid tenantId, Guid buyerTenantId, Guid purchaseOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == Wholesaler && buyerTenantId == Buyer && purchaseOrderId == PurchaseOrder ? Existing : null);

        public Task<WholesaleOrderResult> CreateAsync(WholesaleOrderDraft draft, CancellationToken cancellationToken)
        {
            if (CreateFailure is { } failure)
            {
                throw failure;
            }

            Drafts.Add(draft);
            var line = Assert.Single(draft.Lines);
            return Task.FromResult(ResultOf(line.PurchaseOrderLineId, line.UnitPrice, alreadyExisted: false));
        }

        public Task<bool> CancelByBuyerAsync(
            Guid tenantId,
            Guid orderId,
            Guid buyerTenantId,
            Guid purchaseOrderId,
            string purchaseOrderNumber,
            CancellationToken cancellationToken)
        {
            if (CancelFailure is { } failure)
            {
                throw failure;
            }

            Cancelled.Add((tenantId, orderId, buyerTenantId, purchaseOrderId, purchaseOrderNumber));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDirectory : IWholesaleIntakeDirectory
    {
        public Dictionary<Guid, WholesaleOffer> Offers { get; } = [];

        public HashSet<Guid> ActiveMembers { get; } = [];

        public bool Blocked { get; set; }

        public string Currency { get; set; } = "COP";

        /// <summary>El flag de la lista en los términos de la relación (camino nuevo).</summary>
        public bool TermsShowPrices { get; set; } = true;

        /// <summary>El flag que devuelve <see cref="OffersForOrderAsync"/> (camino D-8); sin lista, <c>false</c>.</summary>
        public bool OrderOffersShowPrices { get; set; } = true;

        public int PriceVisibleCalls { get; private set; }

        public Task<(WholesaleTerms Terms, IReadOnlyDictionary<Guid, WholesaleOffer> Offers)> PriceVisibleAsync(
            Guid wholesalerTenantId, Guid buyerTenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
        {
            PriceVisibleCalls++;
            if (Blocked)
            {
                throw new RequestForbiddenException(PurchasesErrorCodes.RelationshipInactive, "Inactive.");
            }

            var terms = new WholesaleTerms(
                Guid.CreateVersion7(), Customer, Advisor, Company, Currency, 4.8m, null, "Distribuidor estándar", [], [], TermsShowPrices);
            IReadOnlyDictionary<Guid, WholesaleOffer> found = variantIds.ToDictionary(id => id, id => Offers[id]);
            return Task.FromResult((terms, found));
        }

        public Task<WholesaleOrderOffers> OffersForOrderAsync(
            Guid wholesalerTenantId,
            Guid buyerTenantId,
            string currency,
            IReadOnlyCollection<Guid> variantIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WholesaleOrderOffers(
                variantIds.Where(Offers.ContainsKey).ToDictionary(id => id, id => Offers[id]), OrderOffersShowPrices));

        public Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == Wholesaler && ActiveMembers.Contains(membershipId));

        public Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Owner);
    }

    private sealed class RecordingLogger : ILogger<PurchasesWholesaleOrderIntake>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
