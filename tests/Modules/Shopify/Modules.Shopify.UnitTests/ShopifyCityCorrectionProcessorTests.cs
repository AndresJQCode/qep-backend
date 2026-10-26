using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-26 §4.4, §4.6 y D-C6: el procesador del worker que corrige. Lee el sinónimo al correr, escribe por el puerto de
/// Quotations y Customers y, sólo con un éxito, deja la huella y la auditoría en el tenant del pedido o cliente. Todo rechazo termina
/// el trabajo <c>Skipped</c> con su motivo, sin huella ni auditoría.
/// </summary>
public sealed class ShopifyCityCorrectionProcessorTests
{
    private static readonly Guid ValleId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid CaliId = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    private static readonly Guid CalimaId = Guid.Parse("01900000-0000-7000-8000-0000000000c3");
    private static readonly Guid SynonymId = Guid.Parse("01900000-0000-7000-8000-0000000000c4");
    private static readonly Guid OperatorId = Guid.Parse("01900000-0000-7000-8000-0000000000c5");
    private static readonly Guid MessageId = Guid.Parse("01900000-0000-7000-8000-0000000000c6");
    private static readonly Guid OrderId = Guid.Parse("01900000-0000-7000-8000-0000000000c7");
    private static readonly Guid CustomerId = Guid.Parse("01900000-0000-7000-8000-0000000000c8");

    private static readonly ShopifyAddress Calli = new(
        "Laura Gómez", "Cra 5 # 10-20", null, "CALLI", "Valle del Cauca", "VAC", "CO", "+573001112233");

    private static readonly Dictionary<string, string> Document = new(StringComparer.Ordinal)
    {
        ["Documento"] = "1020304050",
        ["Tipo de documento"] = "CC",
    };

    /// <summary>La parte de envío que arma el importador con la dirección de <see cref="Calli"/> y el correo del comprador.</summary>
    private static ShopifyShippingFingerprint PartyIn(Guid cityId) =>
        new("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", ValleId, cityId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AResolveOrderJobAttachesTheShippingWithTheSynonymCityAndAudits()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var job = world.Seed(world.ResolveOrder());

        await world.ProcessAsync(job);

        var attached = Assert.Single(world.Writer.Attached);
        Assert.Equal(
            (TenantId, OrderId, new ShopifyPartyDraft("Laura Gómez", "+573001112233", "laura@verde.co", "Cra 5 # 10-20", ValleId, CaliId),
                ShopifyOrderImporter.RawAddressLabel),
            attached);
        Assert.Equal((ShopifyCityCorrectionJobStatus.Applied, (string?)null, (DateTimeOffset?)Now), (job.Status, job.SkipReason, job.ProcessedAt));
        var correction = Assert.Single(world.Shop.Database.CityCorrections);
        Assert.Equal(
            (TenantId, SynonymId, ShopifyCityCorrectionOrigin.Correction, ShopifyCityCorrectionTarget.Order, OrderId, (Guid?)world.Mirror.Id, (Guid?)null),
            (correction.TenantId, correction.SynonymId, correction.Origin, correction.Target, correction.TargetId, correction.MirrorOrderId, correction.PreviousCityId));
        Assert.Equal(PartyIn(CaliId), correction.Party);
        var entry = Assert.Single(world.Shop.Database.Audit);
        Assert.Equal(
            (ShopifyAuditActions.OrderCityResolved, TenantId, Guid.Empty, AuditActorType.System, OrderId.ToString()),
            (entry.Action, entry.TenantId, entry.ActorId, entry.ActorType, entry.ResourceId));
        Assert.Equal(
            (SynonymId, OperatorId, CaliId, (Guid?)null, "Correction", job.Id),
            AuditFields(entry.Fields));
        Assert.Equal(1, world.Shop.UnitOfWork.Saves);
    }

    // P16: el evento dijo CALIMA, pero antes de que el trabajo corriera el operador corrigió el sinónimo a CALI. Manda la ciudad
    // actual del sinónimo, no la del evento: la parte nace en CALI y la huella también.
    [Fact]
    public async Task AResolveJobUsesTheSynonymsCurrentCity()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CalimaId);
        var job = world.Seed(world.ResolveOrder());
        world.Synonyms.Point(SynonymId, ValleId, CaliId);

        await world.ProcessAsync(job);

        Assert.Equal(CaliId, Assert.Single(world.Writer.Attached).Party.CityId);
        Assert.Equal(CaliId, Assert.Single(world.Shop.Database.CityCorrections).CityId);
        Assert.Equal(CaliId, AuditFields(Assert.Single(world.Shop.Database.Audit).Fields).CityId);
        Assert.DoesNotContain(world.Shop.Database.CityCorrectionJobs, other => other.Kind == ShopifyCityCorrectionJobKind.Recorrect);
    }

    // Review Focus 3: el pedido dejó Pending entre la vista previa y la corrección. El trabajo termina con order_not_pending, y
    // Shopify no deja huella ni auditoría de algo que no pasó.
    [Fact]
    public async Task AnOrderApprovedAfterThePreviewIsSkippedNotTouched()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        world.Writer.OnAttach = _ => new ShopifyCityWriteResult(ShopifyCityWriteKind.NotPending, null, null, "Approved");
        var job = world.Seed(world.ResolveOrder());

        await world.ProcessAsync(job);

        Assert.Equal(
            (ShopifyCityCorrectionJobStatus.Skipped, (string?)ShopifyCityCorrectionSkipReasons.OrderNotPending, (DateTimeOffset?)Now),
            (job.Status, job.SkipReason, job.ProcessedAt));
        Assert.Empty(world.Shop.Database.CityCorrections);
        Assert.Empty(world.Shop.Database.Audit);
    }

    // Review Focus 5 y P16: un sinónimo borrado (o que ya no existe) no corrige nada de lo que tenía pendiente, de ningún tipo; lo
    // ya aplicado no se deshace (O6). El escritor nunca se llama.
    [Fact]
    public async Task JobsOfADeletedSynonymAreSkipped()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId, isDeleted: true);
        var applied = ShopifyCityCorrection.ForOrder(
            TenantId, SynonymId, ShopifyCityCorrectionOrigin.Import, OrderId, world.Mirror.Id, PartyIn(CalimaId), Now.AddDays(-1));
        world.Shop.Database.CityCorrections.Add(applied);
        var resolve = world.Seed(world.ResolveCustomer());
        var recorrect = world.Seed(ShopifyCityCorrectionJob.Recorrect(applied, OperatorId, MessageId, Now));
        var unknown = world.Seed(ShopifyCityCorrectionJob.Resolve(
            TenantId, Guid.CreateVersion7(), ShopifyCityCorrectionTarget.Order, OrderId, world.Mirror.Id, OperatorId, MessageId, Now));

        foreach (var job in new[] { resolve, recorrect, unknown })
        {
            await world.ProcessAsync(job);
        }

        Assert.All(
            new[] { resolve, recorrect, unknown },
            job => Assert.Equal(
                (ShopifyCityCorrectionJobStatus.Skipped, (string?)ShopifyCityCorrectionSkipReasons.SynonymDeleted),
                (job.Status, job.SkipReason)));
        Assert.Equal(0, world.Writer.Calls);
        Assert.Equal((CalimaId, (Guid?)null), (applied.CityId, applied.PreviousCityId));
        Assert.Empty(world.Shop.Database.Audit);
    }

    // Review Focus 4: alguien editó la parte de envío después de que el sinónimo la escribiera. Quotations no la toca
    // (shipping_changed), y la huella queda como estaba: la próxima recorrección compara contra la misma.
    [Fact]
    public async Task ARecorrectionRespectsAShippingPartyEditedAfterTheCorrection()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var correction = ShopifyCityCorrection.ForOrder(
            TenantId, SynonymId, ShopifyCityCorrectionOrigin.Correction, OrderId, world.Mirror.Id, PartyIn(CalimaId), Now.AddDays(-1));
        world.Shop.Database.CityCorrections.Add(correction);
        world.Writer.OnCorrect = _ => new ShopifyCityWriteResult(
            ShopifyCityWriteKind.ShippingChanged, PartyIn(CalimaId) with { Phone = "+573009998877" }, null, null);
        var job = world.Seed(ShopifyCityCorrectionJob.Recorrect(correction, OperatorId, MessageId, Now));

        await world.ProcessAsync(job);

        Assert.Equal(
            (ShopifyCityCorrectionJobStatus.Skipped, (string?)ShopifyCityCorrectionSkipReasons.ShippingChanged),
            (job.Status, job.SkipReason));
        Assert.Equal((PartyIn(CalimaId), (Guid?)null, Now.AddDays(-1)), (correction.Party, correction.PreviousCityId, correction.UpdatedAt));
        Assert.Empty(world.Shop.Database.Audit);
    }

    // §4.6 y O8: lo que casó por el sinónimo al importarse también se recorrige, y sigue siendo de la importación.
    [Fact]
    public async Task ARecorrectionUpdatesTheCorrectionRowAndKeepsItsOrigin()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var correction = ShopifyCityCorrection.ForOrder(
            TenantId, SynonymId, ShopifyCityCorrectionOrigin.Import, OrderId, world.Mirror.Id, PartyIn(CalimaId), Now.AddDays(-1));
        world.Shop.Database.CityCorrections.Add(correction);
        var job = world.Seed(ShopifyCityCorrectionJob.Recorrect(correction, OperatorId, MessageId, Now));

        await world.ProcessAsync(job);

        Assert.Equal((TenantId, OrderId, PartyIn(CalimaId), ValleId, CaliId), Assert.Single(world.Writer.Corrected));
        Assert.Equal(
            (ShopifyCityCorrectionOrigin.Import, PartyIn(CaliId), (Guid?)CalimaId, Now),
            (correction.Origin, correction.Party, correction.PreviousCityId, correction.UpdatedAt));
        Assert.Single(world.Shop.Database.CityCorrections);
        var entry = Assert.Single(world.Shop.Database.Audit);
        Assert.Equal(ShopifyAuditActions.OrderCityCorrected, entry.Action);
        Assert.Equal((SynonymId, OperatorId, CaliId, (Guid?)CalimaId, "Import", job.Id), AuditFields(entry.Fields));
        Assert.Equal(ShopifyCityCorrectionJobStatus.Applied, job.Status);
    }

    // El intento anterior escribió en Quotations y cayó antes de guardar acá: Quotations responde AlreadyApplied y este intento
    // deja la huella y la auditoría que faltaban. Un segundo Resolve del mismo pedido (el importador siembra sin mensaje, así que
    // un pedido reimportado puede sembrar otro) ya encuentra la huella y termina sin volver a escribir.
    [Fact]
    public async Task AlreadyAppliedAfterACrashWritesTheCorrectionAndTheAuditOnce()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        world.Writer.OnAttach = _ => new ShopifyCityWriteResult(ShopifyCityWriteKind.AlreadyApplied, PartyIn(CaliId), null, null);
        var first = world.Seed(world.ResolveOrder());
        var reimported = world.Seed(ShopifyCityCorrectionJob.Resolve(
            TenantId, SynonymId, ShopifyCityCorrectionTarget.Order, OrderId, world.Mirror.Id, Guid.Empty, null, Now));

        await world.ProcessAsync(first);
        await world.ProcessAsync(reimported);

        Assert.Equal(ShopifyCityCorrectionJobStatus.Applied, first.Status);
        Assert.Equal(
            (ShopifyCityCorrectionJobStatus.Skipped, (string?)ShopifyCityCorrectionSkipReasons.ShippingPresent),
            (reimported.Status, reimported.SkipReason));
        Assert.Single(world.Writer.Attached);
        Assert.Equal(PartyIn(CaliId), Assert.Single(world.Shop.Database.CityCorrections).Party);
        Assert.Equal(first.Id, AuditFields(Assert.Single(world.Shop.Database.Audit).Fields).JobId);
    }

    // Desvío S7: el cliente que la importación creó incompleto se completa desde la versión 1, con los datos del comprador
    // releídos del espejo y la clasificación de la tienda, y la huella guarda la versión en que quedó.
    [Fact]
    public async Task ACustomerJobCompletesFromVersionOneAndRecordsTheNewVersion()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        world.Writer.OnComplete = _ => new ShopifyCityWriteResult(ShopifyCityWriteKind.Applied, null, 2, null);
        var job = world.Seed(world.ResolveCustomer());

        await world.ProcessAsync(job);

        var completed = Assert.Single(world.Writer.Completed);
        Assert.Equal((TenantId, CustomerId, (long)ShopifyCityCorrectionProcessor.IncompleteCustomerVersion), (completed.TenantId, completed.CustomerId, completed.ExpectedVersion));
        Assert.Equal(ShopifyCustomerResolver.CompleteDataWithCity(world.Shop.Store, world.Snapshot, CaliId), completed.Data);
        Assert.Equal(CaliId, completed.Data.CityId);
        var correction = Assert.Single(world.Shop.Database.CityCorrections);
        Assert.Equal(
            (ShopifyCityCorrectionTarget.Customer, CustomerId, (Guid?)world.Mirror.Id, ValleId, CaliId, (long?)2, (ShopifyShippingFingerprint?)null),
            (correction.Target, correction.TargetId, correction.MirrorOrderId, correction.DepartmentId, correction.CityId, correction.CustomerVersion, correction.Party));
        var entry = Assert.Single(world.Shop.Database.Audit);
        Assert.Equal((ShopifyAuditActions.CustomerCityResolved, CustomerId.ToString()), (entry.Action, entry.ResourceId));
        Assert.Equal(ShopifyCityCorrectionJobStatus.Applied, job.Status);
    }

    // §4.6 con un cliente: la recorrección manda la versión y la ciudad que guardó la huella, y la huella pasa a la versión nueva.
    [Fact]
    public async Task ACustomerRecorrectionSendsTheVersionAndCityOfItsCorrection()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var correction = ShopifyCityCorrection.ForCustomer(
            TenantId, SynonymId, ShopifyCityCorrectionOrigin.Correction, CustomerId, world.Mirror.Id, ValleId, CalimaId, 2, Now.AddDays(-1));
        world.Shop.Database.CityCorrections.Add(correction);
        var job = world.Seed(ShopifyCityCorrectionJob.Recorrect(correction, OperatorId, MessageId, Now));

        await world.ProcessAsync(job);

        Assert.Equal((TenantId, CustomerId, 2L, CalimaId, CaliId), Assert.Single(world.Writer.CustomerCities));
        Assert.Equal((CaliId, (Guid?)CalimaId, (long?)3), (correction.CityId, correction.PreviousCityId, correction.CustomerVersion));
        Assert.Equal(ShopifyAuditActions.CustomerCityCorrected, Assert.Single(world.Shop.Database.Audit).Action);
    }

    // El evento corrected y un Recorrect sembrado por el procesador pueden alcanzar la misma huella: el segundo la encuentra ya en
    // la ciudad del sinónimo y termina sin escribir. Customers subiría la versión aunque la ciudad no cambie.
    [Fact]
    public async Task ARecorrectionAlreadyAtTheSynonymsCityWritesNothing()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var correction = ShopifyCityCorrection.ForCustomer(
            TenantId, SynonymId, ShopifyCityCorrectionOrigin.Correction, CustomerId, world.Mirror.Id, ValleId, CaliId, 3, Now.AddDays(-1));
        world.Shop.Database.CityCorrections.Add(correction);
        var job = world.Seed(ShopifyCityCorrectionJob.Recorrect(correction, OperatorId, MessageId, Now));

        await world.ProcessAsync(job);

        Assert.Equal(
            (ShopifyCityCorrectionJobStatus.Skipped, (string?)ShopifyCityCorrectionSkipReasons.CustomerNotEligible),
            (job.Status, job.SkipReason));
        Assert.Equal(0, world.Writer.Calls);
        Assert.Equal((long?)3, correction.CustomerVersion);
        Assert.Empty(world.Shop.Database.Audit);
    }

    // El operador corrigió el sinónimo mientras Quotations escribía: el evento corrected pudo listar las huellas antes de que
    // existiera ésta. Después de guardar, el procesador relee el sinónimo y siembra la recorrección de esa huella.
    [Fact]
    public async Task ACityChangedWhileApplyingSeedsARecorrection()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        world.Writer.OnAttach = party =>
        {
            world.Synonyms.Point(SynonymId, ValleId, CalimaId);
            return new ShopifyCityWriteResult(ShopifyCityWriteKind.Applied, PartyIn(CaliId), null, null);
        };
        var job = world.Seed(world.ResolveOrder());

        await world.ProcessAsync(job);

        var correction = Assert.Single(world.Shop.Database.CityCorrections);
        var recorrect = Assert.Single(world.Shop.Database.CityCorrectionJobs, other => other.Kind == ShopifyCityCorrectionJobKind.Recorrect);
        Assert.Equal(
            (TenantId, SynonymId, ShopifyCityCorrectionTarget.Order, OrderId, (Guid?)correction.Id, Guid.Empty, (Guid?)null, ShopifyCityCorrectionJobStatus.Pending),
            (recorrect.TenantId, recorrect.SynonymId, recorrect.Target, recorrect.TargetId, recorrect.CorrectionId, recorrect.OperatorSubjectId, recorrect.SourceMessageId, recorrect.Status));
        Assert.Equal(ShopifyCityCorrectionJobStatus.Applied, job.Status);
        Assert.Equal(2, world.Shop.UnitOfWork.Saves);
    }

    // Cada rechazo de Quotations o Customers termina el trabajo con su motivo, sin huella ni auditoría. Lo que el espejo no trae
    // ni se intenta.
    [Theory]
    [InlineData(ShopifyCityWriteKind.NotFound, ShopifyCityCorrectionTarget.Order, "order_not_found")]
    [InlineData(ShopifyCityWriteKind.StorePickup, ShopifyCityCorrectionTarget.Order, "store_pickup")]
    [InlineData(ShopifyCityWriteKind.ShippingPresent, ShopifyCityCorrectionTarget.Order, "shipping_present")]
    [InlineData(ShopifyCityWriteKind.PartyIncomplete, ShopifyCityCorrectionTarget.Order, "party_incomplete")]
    [InlineData(ShopifyCityWriteKind.NotFound, ShopifyCityCorrectionTarget.Customer, "customer_not_found")]
    [InlineData(ShopifyCityWriteKind.NotEligible, ShopifyCityCorrectionTarget.Customer, "customer_not_eligible")]
    [InlineData(ShopifyCityWriteKind.NotCompletable, ShopifyCityCorrectionTarget.Customer, "customer_not_completable")]
    public async Task EachRefusalEndsSkippedWithItsReason(ShopifyCityWriteKind kind, ShopifyCityCorrectionTarget target, string reason)
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var refusal = new ShopifyCityWriteResult(kind, null, null, null);
        world.Writer.OnAttach = _ => refusal;
        world.Writer.OnComplete = _ => refusal;
        var job = world.Seed(target == ShopifyCityCorrectionTarget.Order ? world.ResolveOrder() : world.ResolveCustomer());

        await world.ProcessAsync(job);

        Assert.Equal((ShopifyCityCorrectionJobStatus.Skipped, (string?)reason), (job.Status, job.SkipReason));
        Assert.Equal(1, world.Writer.Calls);
        Assert.Empty(world.Shop.Database.CityCorrections);
        Assert.Empty(world.Shop.Database.Audit);
    }

    [Fact]
    public async Task WhatTheMirrorDoesNotBringIsSkippedWithoutWriting()
    {
        var world = new CorrectionWorld();
        world.Synonyms.Point(SynonymId, ValleId, CaliId);
        var withoutMirror = world.Seed(ShopifyCityCorrectionJob.Resolve(
            TenantId, SynonymId, ShopifyCityCorrectionTarget.Order, OrderId, Guid.CreateVersion7(), OperatorId, MessageId, Now));
        var noEmail = new CorrectionWorld(Order(address: Calli, attributes: Document, buyer: new ShopifyBuyer("Laura", "Gómez", null, "+573001234567")));
        noEmail.Synonyms.Point(SynonymId, ValleId, CaliId);
        var withoutEmail = noEmail.Seed(noEmail.ResolveOrder());
        var noClassification = new CorrectionWorld(classification: false);
        noClassification.Synonyms.Point(SynonymId, ValleId, CaliId);
        var withoutClassification = noClassification.Seed(noClassification.ResolveCustomer());

        await world.ProcessAsync(withoutMirror);
        await noEmail.ProcessAsync(withoutEmail);
        await noClassification.ProcessAsync(withoutClassification);

        Assert.Equal(
            ((string?)ShopifyCityCorrectionSkipReasons.MirrorMissing, (string?)ShopifyCityCorrectionSkipReasons.PartyIncomplete,
                (string?)ShopifyCityCorrectionSkipReasons.CustomerNotCompletable),
            (withoutMirror.SkipReason, withoutEmail.SkipReason, withoutClassification.SkipReason));
        Assert.Equal((0, 0, 0), (world.Writer.Calls, noEmail.Writer.Calls, noClassification.Writer.Calls));
    }

    private static (Guid SynonymId, Guid OperatorSubjectId, Guid CityId, Guid? PreviousCityId, string Origin, Guid JobId) AuditFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return (
            root.GetProperty("synonymId").GetGuid(),
            root.GetProperty("operatorSubjectId").GetGuid(),
            root.GetProperty("cityId").GetGuid(),
            root.GetProperty("previousCityId").ValueKind == JsonValueKind.Null ? null : root.GetProperty("previousCityId").GetGuid(),
            root.GetProperty("origin").GetString()!,
            root.GetProperty("jobId").GetGuid());
    }

    /// <summary>La tienda del importador (con clasificación por defecto), una fila del espejo con un pedido «CALLI» de un comprador
    /// con documento, y los dobles del sinónimo y del escritor.</summary>
    private sealed class CorrectionWorld
    {
        public CorrectionWorld(ShopifyOrderSnapshot? snapshot = null, bool classification = true)
        {
            Shop = classification ? new ImporterWorld().WithClassification() : new ImporterWorld();
            Shop.Database.Stores[ConnectionId] = Shop.Store;
            Snapshot = snapshot ?? Order(address: Calli, attributes: Document);
            Mirror = ShopifyOrderMirror.Create(
                TenantId, ConnectionId, Snapshot.OrderId, ShopifyOrderIngestion.WritePayload(Snapshot), Snapshot.UpdatedAt, Snapshot.ToHeader());
            Shop.Database.Orders[(ConnectionId, Snapshot.OrderId)] = Mirror;
        }

        public ImporterWorld Shop { get; }

        public ShopifyOrderSnapshot Snapshot { get; }

        public ShopifyOrderMirror Mirror { get; }

        public FakeCitySynonymLookup Synonyms { get; } = new();

        public FakeCityCorrectionWriter Writer { get; } = new();

        public ShopifyCityCorrectionJob ResolveOrder() =>
            ShopifyCityCorrectionJob.Resolve(
                TenantId, SynonymId, ShopifyCityCorrectionTarget.Order, OrderId, Mirror.Id, OperatorId, MessageId, Now);

        public ShopifyCityCorrectionJob ResolveCustomer() =>
            ShopifyCityCorrectionJob.Resolve(
                TenantId, SynonymId, ShopifyCityCorrectionTarget.Customer, CustomerId, Mirror.Id, OperatorId, MessageId, Now);

        /// <summary>El trabajo como lo deja el consumidor: ya guardado.</summary>
        public ShopifyCityCorrectionJob Seed(ShopifyCityCorrectionJob job)
        {
            Shop.Database.CityCorrectionJobs.Add(job);
            return job;
        }

        public Task ProcessAsync(ShopifyCityCorrectionJob job) =>
            new ShopifyCityCorrectionProcessor(
                Synonyms,
                Writer,
                Shop.Mirrors,
                new FakeStoreRepository(Shop.Database),
                Shop.CityCorrections,
                Shop.CityCorrectionJobs,
                new FakeAuditRecorder(Shop.Database),
                Shop.UnitOfWork,
                new FakeClock(Now))
                .ProcessAsync(job, Ct);
    }
}

/// <summary>Los sinónimos de Geography por id; <see cref="Point"/> los crea o los cambia, como un <c>PUT</c> del operador.</summary>
internal sealed class FakeCitySynonymLookup : IShopifyCitySynonymLookup
{
    private readonly Dictionary<Guid, ShopifyCitySynonymRef> synonyms = [];

    public void Point(Guid synonymId, Guid departmentId, Guid cityId, bool isDeleted = false) =>
        synonyms[synonymId] = new ShopifyCitySynonymRef(synonymId, departmentId, cityId, isDeleted);

    public Task<ShopifyCitySynonymRef?> FindAsync(Guid synonymId, CancellationToken cancellationToken) =>
        Task.FromResult(synonyms.GetValueOrDefault(synonymId));
}

/// <summary>El puerto hacia Quotations y Customers: anota cada llamada y, sin otra instrucción, responde como un éxito (la parte
/// como se le mandó, la ciudad nueva, la versión siguiente).</summary>
internal sealed class FakeCityCorrectionWriter : IShopifyCityCorrectionWriter
{
    public Func<ShopifyPartyDraft, ShopifyCityWriteResult>? OnAttach { get; set; }

    public Func<ShopifyShippingFingerprint, ShopifyCityWriteResult>? OnCorrect { get; set; }

    public Func<ShopifyCompleteCustomer, ShopifyCityWriteResult>? OnComplete { get; set; }

    public int Calls { get; private set; }

    public List<(Guid TenantId, Guid OrderId, ShopifyPartyDraft Party, string NotesLinePrefix)> Attached { get; } = [];

    public List<(Guid TenantId, Guid OrderId, ShopifyShippingFingerprint Expected, Guid DepartmentId, Guid CityId)> Corrected { get; } = [];

    public List<(Guid TenantId, Guid CustomerId, long ExpectedVersion, ShopifyCompleteCustomer Data)> Completed { get; } = [];

    public List<(Guid TenantId, Guid CustomerId, long ExpectedVersion, Guid PreviousCityId, Guid CityId)> CustomerCities { get; } = [];

    public Task<ShopifyCityWriteResult> AttachShippingAsync(
        Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken)
    {
        Calls++;
        Attached.Add((tenantId, orderId, party, notesLinePrefix));
        return Task.FromResult(OnAttach?.Invoke(party) ?? new ShopifyCityWriteResult(
            ShopifyCityWriteKind.Applied,
            new ShopifyShippingFingerprint(party.Name, party.Phone, party.Email, party.Address, party.DepartmentId!.Value, party.CityId!.Value),
            null,
            null));
    }

    public Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
        Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken)
    {
        Calls++;
        Corrected.Add((tenantId, orderId, expected, departmentId, cityId));
        return Task.FromResult(OnCorrect?.Invoke(expected) ?? new ShopifyCityWriteResult(
            ShopifyCityWriteKind.Applied, expected with { DepartmentId = departmentId, CityId = cityId }, null, null));
    }

    public Task<ShopifyCityWriteResult> CompleteCustomerAsync(
        Guid tenantId, Guid customerId, long expectedVersion, ShopifyCompleteCustomer data, CancellationToken cancellationToken)
    {
        Calls++;
        Completed.Add((tenantId, customerId, expectedVersion, data));
        return Task.FromResult(OnComplete?.Invoke(data) ?? new ShopifyCityWriteResult(ShopifyCityWriteKind.Applied, null, expectedVersion + 1, null));
    }

    public Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(
        Guid tenantId, Guid customerId, long expectedVersion, Guid previousCityId, Guid cityId, CancellationToken cancellationToken)
    {
        Calls++;
        CustomerCities.Add((tenantId, customerId, expectedVersion, previousCityId, cityId));
        return Task.FromResult(new ShopifyCityWriteResult(ShopifyCityWriteKind.Applied, null, expectedVersion + 1, null));
    }
}
