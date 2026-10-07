using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class CreatePosSaleHandlerTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate];
    private static readonly string[] Discounter = [PosPermissions.SaleCreate, PosPermissions.SaleDiscount];

    private static (PosTestBed Bed, CashSession Session) Arrange()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();
        return (bed, bed.OpenSessionInStore());
    }

    private static CreatePosSaleCommand Command(
        CashSession session, Guid? id = null, PosSaleLineRequest[]? lines = null, PosPaymentRequest[]? payments = null) =>
        new(
            TenantId,
            id ?? Guid.CreateVersion7(),
            session.Id.Value,
            lines ?? [new(Shampoo, 2m, 10m, 11_900m, 19), new(Avena, 1.5m, 0m, 5_000m, 0), new(Jabon, 3m, 0m, 2_990m, 5)],
            payments ?? [new("Card", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)]);

    private static CreatePosSaleHandler Handler(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Sales, bed.Numbers, bed.UnitOfWork, bed.Audit, bed.Products, bed.Cashiers,
            bed.Memberships, bed.Context(permissions), bed.Clock, bed.TenantClock, new CreatePosSaleValidator());

    // La venta "del otro request" que el choque deja en la base: mismo id, mismo cajero, misma huella.
    private static PosSale WinnerFor(CreatePosSaleCommand command, CashSession session)
    {
        var sale = PosSale.Create(
            new PosSaleId(command.Id), PosSaleFingerprint.Compute(command), session,
            WorkedExampleLines(), [Card(20_000m, "1234"), Cash(20_000m)], Now);
        sale.AssignNumber(1);
        return sale;
    }

    [Fact]
    public async Task CreateRegistersNumbersAndAuditsTheSale()
    {
        var (bed, session) = Arrange();

        var result = await Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Equal("POS-000001", result.Sale.SaleNumber);
        Assert.Equal(37_890m, result.Sale.Total);
        Assert.Equal(2_110m, result.Sale.ChangeAmount);
        Assert.Equal("Laura Gómez", result.Sale.CashierName);
        Assert.Equal("Origen Botánico SAS", result.Sale.Issuer.Name);
        Assert.Equal("Consumidor final", result.Sale.Customer.Name);
        Assert.Equal(3, result.Sale.TaxBreakdown.Count);
        Assert.True(result.Sale.Voidable);
        Assert.Single(bed.Sales.Stored);
        Assert.Equal(1, session.SalesCount);
        Assert.Equal(1, bed.UnitOfWork.Commits);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(("pos.sale.created", "pos_sale"), (audit.Action, audit.ResourceType));
        Assert.Equal(["discount:1:SH-400:10"], audit.ChangedFields.ToArray());
    }

    [Fact]
    public async Task ARepeatWithTheSameBodyReturnsTheSameSaleWithoutSavingOrNumbering()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        var first = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        var repeat = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(repeat.Created);
        Assert.Equal(first.Sale.Id, repeat.Sale.Id);
        Assert.Equal(first.Sale.SaleNumber, repeat.Sale.SaleNumber);
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        Assert.Equal(1, bed.UnitOfWork.Commits);
        Assert.Equal(1, bed.Numbers.Calls);
        Assert.Equal(1, session.SalesCount);
    }

    // El clasificador del frontend toma como definitivo, desde un cobro incierto, todo 422 pos.*:
    // ninguno puede salir antes de buscar la repetición. Si la caja ya se cerró y el precio cambió
    // después de la venta, el reintento igual tiene que recibir su venta, no not_open ni
    // price_changed (spec, «Crear venta», pasos 1 y 2; «Ciclo de vida del id»).
    [Fact]
    public async Task ARepeatIsRecognizedBeforeTheSessionAndPriceChecks()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        var first = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);
        session.Close(100_000m, null, Now);
        var shampoo = bed.Products.Products.Single(entry => entry.Product.Id == Shampoo).Product;
        bed.Products.Replace(shampoo with { PriceCop = 12_500m, IsActive = false });

        var repeat = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(repeat.Created);
        Assert.Equal(first.Sale.Id, repeat.Sale.Id);
        Assert.Equal(1, bed.Numbers.Calls);
        Assert.Equal(1, bed.UnitOfWork.Commits);
    }

    // Lo único que sale antes de la búsqueda con un 422 es validation.failed, que el cliente no
    // toma como definitivo al reintentar. El id vacío nunca llega al pos.sale.id_required de
    // PosSaleId: el validador lo para primero.
    [Fact]
    public async Task AnEmptyIdIsAValidationFailureAndNeverAPosCode()
    {
        var (bed, session) = Arrange();

        var error = await Assert.ThrowsAsync<ValidationException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, Guid.Empty), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == nameof(CreatePosSaleCommand.Id));
        Assert.Equal(0, bed.Numbers.Calls);
    }

    [Fact]
    public async Task TheSameIdWithAnotherBodyIsAConflictAndTheOriginalStays()
    {
        var (bed, session) = Arrange();
        var id = Guid.CreateVersion7();
        await Handler(bed, Discounter).HandleAsync(Command(session, id), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, id, payments: [new("Cash", null, 50_000m, null)]), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.id_conflict", error.Code);
        Assert.Equal(37_890m, Assert.Single(bed.Sales.Stored).Total);
    }

    [Fact]
    public async Task TheSameIdFromAnotherCashierIsAConflict()
    {
        var (bed, _) = Arrange();
        var otherSession = OpenSession(cashier: new MemberId(Guid.CreateVersion7()));
        bed.Sessions.Add(otherSession);
        var foreignCommand = Command(otherSession);
        bed.Sales.Stored.Add(WinnerFor(foreignCommand, otherSession));
        var mySession = bed.Sessions.Sessions[0];

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(mySession, foreignCommand.Id), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.id_conflict", error.Code);
    }

    [Fact]
    public async Task ANewPriceOrOnlyANewTaxRateIsPriceChangedAndSpendsNoNumber()
    {
        var (bed, session) = Arrange();
        var shampoo = bed.Products.Products.Single(entry => entry.Product.Id == Shampoo).Product;

        bed.Products.Replace(shampoo with { PriceCop = 12_500m });
        var price = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));
        bed.Products.Replace(shampoo with { TaxPercentage = 5 });
        var tax = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.price_changed", price.Code);
        Assert.Equal("pos.sale.price_changed", tax.Code);
        Assert.Equal(0, bed.Numbers.Calls);
        Assert.Empty(bed.Sales.Stored);
    }

    [Fact]
    public async Task InactiveUnpricedAndMissingProductsAreRejected()
    {
        var (bed, session) = Arrange();
        var shampoo = bed.Products.Products.Single(entry => entry.Product.Id == Shampoo).Product;

        bed.Products.Replace(shampoo with { IsActive = false });
        var inactive = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));
        bed.Products.Replace(shampoo with { PriceCop = null });
        var unpriced = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, lines: [new(Guid.CreateVersion7(), 1m, 0m, 1_000m, 0)], payments: [new("Cash", null, 1_000m, null)]),
            TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.product_inactive", inactive.Code);
        Assert.Equal("pos.sale.product_price_unavailable", unpriced.Code);
        Assert.Equal("pos.sale.product_not_found", missing.Code);
        Assert.Equal(0, bed.Numbers.Calls);
    }

    [Fact]
    public async Task ALineDiscountWithoutThePermissionIs403BeforeAnyNumber()
    {
        var (bed, session) = Arrange();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(bed, Seller).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.discount_not_allowed", error.Code);
        Assert.Equal(0, bed.Numbers.Calls);
    }

    // Regalar también es un descuento (spec, «Crear venta», paso 6).
    [Fact]
    public async Task AZeroTotalNeedsTheDiscountPermissionAndIsAudited()
    {
        var (bed, session) = Arrange();
        bed.Products.Add("MU-00", "Muestra gratis", 0m, 0, id: Guid.Parse("01900000-0000-7000-8000-0000000000b0"));
        PosSaleLineRequest[] free = [new(Guid.Parse("01900000-0000-7000-8000-0000000000b0"), 1m, 0m, 0m, 0)];
        PosPaymentRequest[] zero = [new("Cash", null, 0m, null)];

        var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(bed, Seller).HandleAsync(Command(session, lines: free, payments: zero), TestContext.Current.CancellationToken));
        var created = await Handler(bed, Discounter).HandleAsync(
            Command(session, lines: free, payments: zero), TestContext.Current.CancellationToken);

        Assert.Equal("pos.sale.discount_not_allowed", denied.Code);
        Assert.Equal(0m, created.Sale.Total);
        Assert.Equal(["total:0"], Assert.Single(bed.Audit.Entries).ChangedFields.ToArray());
    }

    [Fact]
    public async Task WithoutAnOpenSessionOrWithAnotherSessionIdTheSaleIsRejected()
    {
        var (bed, session) = Arrange();

        var mismatch = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session) with { CashSessionId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));
        session.Close(100_000m, null, Now);
        var notOpen = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.session_mismatch", mismatch.Code);
        Assert.Equal("pos.session.not_open", notOpen.Code);
    }

    // Spec, «Crear venta», paso 8, primer orden: lo normal es que el duplicado choque en la Version
    // de la caja (412), no en PK_sales.
    [Fact]
    public async Task AConcurrencyClashWhereTheSaleAppearsReturnsItAs200()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        bed.UnitOfWork.OnSave.Enqueue(() =>
        {
            bed.Sales.Stored.Add(WinnerFor(command, session));
            return new RequestConcurrencyException("concurrency.conflict", "clash");
        });

        var result = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal(command.Id, result.Sale.Id);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
        Assert.Equal(0, bed.UnitOfWork.Commits);
        Assert.Single(bed.Sales.Stored);
    }

    [Fact]
    public async Task AnIdTakenClashWhereTheSaleAppearsReturnsItAs200()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        bed.UnitOfWork.OnSave.Enqueue(() =>
        {
            bed.Sales.Stored.Add(WinnerFor(command, session));
            return new PosDomainException("pos.sale.id_taken", "taken");
        });

        var result = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal(command.Id, result.Sale.Id);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
        Assert.Equal(0, bed.UnitOfWork.Commits);
        Assert.Single(bed.Sales.Stored);
    }

    // Fue un cierre de caja u otra venta del mismo cajero en otra pestaña, no un duplicado.
    [Fact]
    public async Task AConcurrencyClashWithoutTheSaleIsRethrownAs412()
    {
        var (bed, session) = Arrange();
        bed.UnitOfWork.OnSave.Enqueue(() => new RequestConcurrencyException("concurrency.conflict", "clash"));

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
        Assert.Equal(0, bed.UnitOfWork.Commits);
        Assert.Empty(bed.Sales.Stored);
    }

    // El id es la PK de una venta de otro tenant: terminal, el cliente genera otro id.
    [Fact]
    public async Task AnIdTakenClashWithoutTheSaleInTheTenantIsAConflict()
    {
        var (bed, session) = Arrange();
        bed.UnitOfWork.OnSave.Enqueue(() => new PosDomainException("pos.sale.id_taken", "taken"));

        var error = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.id_conflict", error.Code);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
        Assert.Equal(0, bed.UnitOfWork.Commits);
        Assert.Empty(bed.Sales.Stored);
    }

    // Review Focus 2.
    [Fact]
    public async Task CreateWithTheSameProductOnTwoLinesKeepsBothLines()
    {
        var (bed, session) = Arrange();

        var result = await Handler(bed, Discounter).HandleAsync(
            Command(session,
                lines: [new(Shampoo, 1m, 10m, 11_900m, 19), new(Shampoo, 1m, 0m, 11_900m, 19)],
                payments: [new("Cash", null, 30_000m, null)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Sale.Lines.Count);
        Assert.Equal(22_610m, result.Sale.Total);
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task TheValidatorRejectsFieldsOutOfShape(PosSaleLineRequest[] lines, PosPaymentRequest[] payments)
    {
        var (bed, session) = Arrange();

        await Assert.ThrowsAsync<ValidationException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, lines: lines, payments: payments), TestContext.Current.CancellationToken));

        Assert.Equal(0, bed.Numbers.Calls);
    }

    public static TheoryData<PosSaleLineRequest[], PosPaymentRequest[]> InvalidBodies => new()
    {
        { [new(Shampoo, 1.005m, 0m, 11_900m, 19)], [new("Cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 7.005m, 11_900m, 19)], [new("Cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Cash", 11_900m, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Cash", null, 20_000m, "ref")] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Cash", null, 20_000.001m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Card", 11_900m, null, new string('9', 61))] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Card", null, null, null)] },
        { [], [new("Cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [] },
    };
}
