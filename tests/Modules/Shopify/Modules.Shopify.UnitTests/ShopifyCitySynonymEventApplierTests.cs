using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-26 D-C6 y desvío S5: el consumidor no corrige, siembra una fila de trabajo por pedido o cliente que el sinónimo
/// puede arreglar. Lo sembrado queda en el tracker (pendiente) para que el procesador lo guarde con el inbox.
/// </summary>
public sealed class ShopifyCitySynonymEventApplierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid MessageId = Guid.Parse("01900000-0000-7000-8000-0000000000a0");
    private static readonly Guid SynonymId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid ValleId = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    private static readonly Guid CaliId = Guid.Parse("01900000-0000-7000-8000-0000000000a3");
    private static readonly Guid CalimaId = Guid.Parse("01900000-0000-7000-8000-0000000000a4");
    private static readonly Guid OperatorId = Guid.Parse("01900000-0000-7000-8000-0000000000a5");
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a6");
    private static readonly Guid TenantA = Guid.Parse("01900000-0000-7000-8000-0000000000a7");
    private static readonly Guid TenantB = Guid.Parse("01900000-0000-7000-8000-0000000000a8");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ShopifyCitySynonymEventApplier Applier, FakeShopifyDatabase Database) NewApplier()
    {
        var database = new FakeShopifyDatabase();
        var applier = new ShopifyCitySynonymEventApplier(
            new FakeCityCorrectionJobRepository(database), new FakeCityCorrectionRepository(database), new FakeUnitOfWork(database),
            new FakeClock(Now));
        return (applier, database);
    }

    private static CitySynonymOccurrenceFact Occurrence(
        Guid tenantId, Guid mirrorOrderId, Guid orderId, Guid? customerId, bool partyCityOnly, bool customerCityOnly, string source = "shopify") =>
        new(Guid.CreateVersion7(), tenantId, source, mirrorOrderId, orderId, customerId, partyCityOnly, customerCityOnly);

    private static CitySynonymResolvedFact Resolved(params CitySynonymOccurrenceFact[] occurrences) =>
        new(SynonymId, ValleId, CaliId, "CALLI", OperatorId, OperatorTenantId, occurrences);

    private static (ShopifyCityCorrectionJobKind, Guid, Guid, ShopifyCityCorrectionTarget, Guid, Guid?, Guid?, Guid, Guid?, ShopifyCityCorrectionJobStatus, int, DateTimeOffset)
        Shape(ShopifyCityCorrectionJob job) =>
        (job.Kind, job.TenantId, job.SynonymId, job.Target, job.TargetId, job.MirrorOrderId, job.CorrectionId, job.OperatorSubjectId,
            job.SourceMessageId, job.Status, job.Attempts, job.CreatedAt);

    // Por ocurrencia de Shopify: un trabajo de pedido si la ciudad era lo único que le faltaba a la parte de envío, y uno de
    // cliente si esta importación lo creó incompleto sólo por la ciudad. Una ocurrencia de otro origen no es de Shopify, y un
    // cliente al que le falta algo más que la ciudad no se puede completar.
    [Fact]
    public async Task AResolvedEventSeedsOneJobPerOrderAndCustomerThatCanBeFixed()
    {
        var (applier, database) = NewApplier();
        var (mirrorA, orderA, customerA) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (mirrorB, orderB, customerB) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await applier.ApplyResolvedAsync(
            MessageId,
            Resolved(
                Occurrence(TenantA, mirrorA, orderA, customerA, partyCityOnly: true, customerCityOnly: true),
                Occurrence(TenantB, mirrorB, orderB, customerB, partyCityOnly: true, customerCityOnly: false),
                Occurrence(TenantB, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), true, true, source: "excel")),
            Ct);

        const ShopifyCityCorrectionJobKind resolve = ShopifyCityCorrectionJobKind.Resolve;
        const ShopifyCityCorrectionJobStatus pending = ShopifyCityCorrectionJobStatus.Pending;
        Assert.Equal(
            [
                (resolve, TenantA, SynonymId, ShopifyCityCorrectionTarget.Order, orderA, (Guid?)mirrorA, (Guid?)null, OperatorId, (Guid?)MessageId, pending, 0, Now),
                (resolve, TenantA, SynonymId, ShopifyCityCorrectionTarget.Customer, customerA, mirrorA, null, OperatorId, MessageId, pending, 0, Now),
                (resolve, TenantB, SynonymId, ShopifyCityCorrectionTarget.Order, orderB, mirrorB, null, OperatorId, MessageId, pending, 0, Now),
            ],
            database.PendingCityCorrectionJobs.Select(Shape));
        Assert.Empty(database.CityCorrectionJobs);
    }

    // Sin correo, teléfono o nombre la parte de envío no nacería con la ciudad (desvío S10): no hay trabajo de pedido, pero el
    // cliente que sólo esperaba la ciudad sí se completa.
    [Fact]
    public async Task AnOccurrenceWithoutPartyCityOnlySeedsNoOrderJob()
    {
        var (applier, database) = NewApplier();
        var customerId = Guid.CreateVersion7();

        await applier.ApplyResolvedAsync(
            MessageId,
            Resolved(Occurrence(TenantA, Guid.CreateVersion7(), Guid.CreateVersion7(), customerId, partyCityOnly: false, customerCityOnly: true)),
            Ct);

        var job = Assert.Single(database.PendingCityCorrectionJobs);
        Assert.Equal((ShopifyCityCorrectionTarget.Customer, customerId), (job.Target, job.TargetId));
    }

    // El mismo pedido puede tener dos ocurrencias (la fila del espejo se borró y el pedido volvió a llegar): un solo trabajo, o el
    // índice único (mensaje, destino, id) rechazaría el guardado y el mensaje no terminaría nunca.
    [Fact]
    public async Task TheSameOrderTwiceInOneEventSeedsOneJob()
    {
        var (applier, database) = NewApplier();
        var orderId = Guid.CreateVersion7();

        await applier.ApplyResolvedAsync(
            MessageId,
            Resolved(
                Occurrence(TenantA, Guid.CreateVersion7(), orderId, null, partyCityOnly: true, customerCityOnly: false),
                Occurrence(TenantA, Guid.CreateVersion7(), orderId, null, partyCityOnly: true, customerCityOnly: false)),
            Ct);

        Assert.Equal(orderId, Assert.Single(database.PendingCityCorrectionJobs).TargetId);
    }

    // O8: corregir el sinónimo recorrige lo que arregló el trabajo (Correction) y lo que casó por él al importarse (Import), en
    // todos los tenants. El trabajo apunta a la huella y lleva su pedido o cliente y su fila del espejo.
    [Fact]
    public async Task ACorrectedEventSeedsARecorrectionPerCorrectionOfBothOrigins()
    {
        var (applier, database) = NewApplier();
        var order = OrderCorrection(TenantA, ShopifyCityCorrectionOrigin.Correction, CaliId);
        var customer = ShopifyCityCorrection.ForCustomer(
            TenantB, SynonymId, ShopifyCityCorrectionOrigin.Import, Guid.CreateVersion7(), null, ValleId, CaliId, 1L, Now.AddDays(-1));
        database.CityCorrections.AddRange([order, customer]);

        await applier.ApplyCorrectedAsync(MessageId, new CitySynonymCorrectedFact(SynonymId, ValleId, CaliId, CalimaId, OperatorId, OperatorTenantId), Ct);

        const ShopifyCityCorrectionJobKind recorrect = ShopifyCityCorrectionJobKind.Recorrect;
        const ShopifyCityCorrectionJobStatus pending = ShopifyCityCorrectionJobStatus.Pending;
        Assert.Equal(
            [
                (recorrect, TenantA, SynonymId, ShopifyCityCorrectionTarget.Order, order.TargetId, order.MirrorOrderId, (Guid?)order.Id, OperatorId, (Guid?)MessageId, pending, 0, Now),
                (recorrect, TenantB, SynonymId, ShopifyCityCorrectionTarget.Customer, customer.TargetId, null, customer.Id, OperatorId, MessageId, pending, 0, Now),
            ],
            // Por destino y no por id: dos GUID v7 del mismo milisegundo no salen ordenados.
            database.PendingCityCorrectionJobs.Select(Shape).OrderBy(shape => shape.Item4));
    }

    // Una huella que ya está en la ciudad nueva no tiene nada que recorregir (una persona ya la había puesto ahí, o una
    // corrección anterior la llevó). Las huellas de otro sinónimo no son de este evento.
    [Fact]
    public async Task ACorrectedEventSkipsCorrectionsAlreadyAtTheNewCity()
    {
        var (applier, database) = NewApplier();
        var atCali = OrderCorrection(TenantA, ShopifyCityCorrectionOrigin.Correction, CaliId);
        var atCalima = OrderCorrection(TenantA, ShopifyCityCorrectionOrigin.Import, CalimaId);
        var otherSynonym = ShopifyCityCorrection.ForOrder(
            TenantA, Guid.CreateVersion7(), ShopifyCityCorrectionOrigin.Correction, Guid.CreateVersion7(), Guid.CreateVersion7(),
            new ShopifyShippingFingerprint("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", ValleId, CaliId), Now);
        database.CityCorrections.AddRange([atCali, atCalima, otherSynonym]);

        await applier.ApplyCorrectedAsync(MessageId, new CitySynonymCorrectedFact(SynonymId, ValleId, CaliId, CalimaId, OperatorId, OperatorTenantId), Ct);

        Assert.Equal(atCali.Id, Assert.Single(database.PendingCityCorrectionJobs).CorrectionId);
    }

    // El trabajo que corrige escribe su huella bajo el candado del sinónimo y, con él tomado, relee la ciudad. Listar las huellas
    // bajo el mismo candado es lo que impide que una huella guardada a la vez quede sin recorregir: o el trabajo guardó antes y
    // la lista la ve, o el trabajo relee después y ve la ciudad nueva.
    [Fact]
    public async Task ACorrectedEventListsTheCorrectionsUnderTheSynonymLock()
    {
        var (applier, database) = NewApplier();

        await applier.ApplyCorrectedAsync(MessageId, new CitySynonymCorrectedFact(SynonymId, ValleId, CaliId, CalimaId, OperatorId, OperatorTenantId), Ct);

        Assert.Equal([$"synonym-lock:{SynonymId}", $"list:{SynonymId}"], database.Log);
    }

    private static ShopifyCityCorrection OrderCorrection(Guid tenantId, ShopifyCityCorrectionOrigin origin, Guid cityId) =>
        ShopifyCityCorrection.ForOrder(
            tenantId, SynonymId, origin, Guid.CreateVersion7(), Guid.CreateVersion7(),
            new ShopifyShippingFingerprint("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", ValleId, cityId),
            Now.AddDays(-1));
}
