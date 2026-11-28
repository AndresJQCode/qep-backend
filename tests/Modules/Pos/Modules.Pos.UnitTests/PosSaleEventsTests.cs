using Modules.Pos.Application;
using Modules.Pos.Domain;
using Modules.Pos.Infrastructure.Persistence;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

/// <summary>
/// <c>pos.sale.created.v1</c> y <c>pos.sale.voided.v1</c>: salen del caso de uso, con las líneas de la venta y la versión fija
/// (1 y 2) que ordena la anulación después de la venta. Un caso de uso que falla no publica nada.
/// </summary>
public sealed class PosSaleEventsTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate, PosPermissions.SaleDiscount];
    private static readonly string[] VoidPermissions = [PosPermissions.SaleVoid];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreatePosSaleHandler Creator(PosTestBed bed) =>
        new(bed.Sessions, bed.Sales, bed.Numbers, bed.UnitOfWork, bed.SaleIdLock, bed.Audit, bed.Events, bed.Products, bed.Costs,
            bed.Cashiers, bed.Memberships, bed.Customers, bed.TenantDirectory, bed.PriceRounding, bed.Context(Seller), bed.Clock,
            bed.TenantClock, new CreatePosSaleValidator());

    private static VoidPosSaleHandler Voider(PosTestBed bed) =>
        new(bed.Sales, bed.Sessions, bed.UnitOfWork, bed.Audit, bed.Events, bed.Cashiers, bed.Memberships,
            bed.Context(VoidPermissions), bed.Clock, bed.TenantClock, new VoidPosSaleValidator());

    private static CreatePosSaleCommand CommandFor(CashSession session) =>
        new(
            TenantId,
            Guid.CreateVersion7(),
            session.Id.Value,
            [new(Shampoo, 2m, 10m, 11_900m, 19), new(Avena, 1.5m, 0m, 5_000m, 0), new(Jabon, 3m, 0m, 2_990m, 5)],
            [new("Card", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)],
            null);

    [Fact]
    public async Task CreatingASalePublishesItsLinesWithVersionOne()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();
        var session = bed.OpenSessionInStore();

        var creation = await Creator(bed).HandleAsync(CommandFor(session), Ct);

        var sale = Assert.Single(bed.Sales.Stored);
        var published = Assert.Single(bed.Events.Created);
        Assert.Equal((TenantId, sale.Id.Value, 1), (published.tenantId, published.saleId, published.version));
        Assert.Equal(sale.CreatedAt, published.occurredAt);
        Assert.Equal(
            sale.Lines.Select(line => (line.Id.Value, line.VariantId, line.Quantity)).ToArray(),
            published.lines.Select(line => (line.lineId, line.variantId, line.quantity)).ToArray());
        Assert.Equal([Shampoo, Avena, Jabon], published.lines.Select(line => line.variantId).ToArray());
        Assert.True(creation.Created);
        Assert.Empty(bed.Events.Voided);
    }

    // El inventario del WMS lee estos eventos por nombre de campo: se parsea el payload REAL del publicador con el lector de Wms.
    [Fact]
    public void BothPosEventsParseWithTheWmsReader()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);
        using var context = new PosDbContextFactory().CreateDbContext([]);
        var publisher = new PosSaleEventPublisher(context);

        publisher.PublishCreated(sale, Now);
        publisher.PublishVoided(sale, Now);

        var created = Assert.Single(context.Outbox.Local, message => message.EventName == PosSaleEvents.CreatedEventName);
        Assert.True(InventoryEventPayloads.TryParse(created.EventName, created.PayloadJson, out var parsedCreated), created.PayloadJson);
        Assert.Equal(InventoryEventKind.PosCreated, parsedCreated.Kind);
        Assert.Equal(InventoryDocumentTypes.PosSale, parsedCreated.DocumentType);
        Assert.Equal((TenantId, sale.Id.Value, 1L), (parsedCreated.TenantId, parsedCreated.DocumentId, parsedCreated.Version));
        Assert.Equal(
            sale.Lines.Select(line => (line.Id.Value, (Guid?)line.VariantId, line.Quantity)).ToArray(),
            parsedCreated.Lines.Select(line => (line.LineId, line.VariantId, line.Quantity)).ToArray());

        var voided = Assert.Single(context.Outbox.Local, message => message.EventName == PosSaleEvents.VoidedEventName);
        Assert.True(InventoryEventPayloads.TryParse(voided.EventName, voided.PayloadJson, out var parsedVoided), voided.PayloadJson);
        Assert.Equal(InventoryEventKind.PosVoided, parsedVoided.Kind);
        Assert.Equal((TenantId, sale.Id.Value, 2L), (parsedVoided.TenantId, parsedVoided.DocumentId, parsedVoided.Version));
    }

    [Fact]
    public async Task AReplayDoesNotPublishASecondEvent()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();
        var command = CommandFor(bed.OpenSessionInStore());
        var handler = Creator(bed);
        await handler.HandleAsync(command, Ct);

        var replay = await handler.HandleAsync(command, Ct);

        Assert.False(replay.Created);
        Assert.Single(bed.Events.Created);
    }

    [Fact]
    public async Task VoidingPublishesVersionTwo()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);

        await Voider(bed).HandleAsync(new VoidPosSaleCommand(TenantId, sale.Id.Value, "Cliente se arrepintió"), Ct);

        var published = Assert.Single(bed.Events.Voided);
        Assert.Equal((TenantId, sale.Id.Value, 2), (published.tenantId, published.saleId, published.version));
        Assert.Equal(Now, published.occurredAt);
    }

    [Fact]
    public async Task AVoidThatFailsPublishesNothing()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);
        await Voider(bed).HandleAsync(new VoidPosSaleCommand(TenantId, sale.Id.Value, "Motivo"), Ct);
        bed.Events.Voided.Clear();

        var again = await Assert.ThrowsAsync<PosDomainException>(() =>
            Voider(bed).HandleAsync(new VoidPosSaleCommand(TenantId, sale.Id.Value, "Otra vez"), Ct));

        Assert.Equal("pos.sale.already_voided", again.Code);
        Assert.Empty(bed.Events.Voided);
    }

    private static PosSale StoreSale(PosTestBed bed, CashSession session)
    {
        var sale = PosSale.Create(
            PosSaleId.New(), Fingerprint, session, WorkedExampleLines(), [Card(20_000m, "1234"), Cash(20_000m)], Now);
        sale.AssignNumber(bed.Sales.Stored.Count + 1);
        session.RegisterSale(sale, Now);
        bed.Sales.Stored.Add(sale);
        return sale;
    }
}
