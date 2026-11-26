using System.Text.Json.Nodes;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-14: un pedido de Shopify ya importado y el pedido de QEP que salió de él. <see cref="Applied"/> es la versión de
/// Shopify aplicada; <see cref="Order"/> el pedido de QEP como lo escribió la importación (la misma camiseta, la misma parte de envío
/// en Medellín y la nota «A»). Cada prueba cambia una sola cosa.
/// </summary>
internal sealed class ShopifyEditTestWorld
{
    public static readonly Guid DepartmentId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    public static readonly Guid MedellinId = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    public static readonly Guid EnvigadoId = Guid.Parse("01900000-0000-7000-8000-0000000000c3");
    public static readonly Guid OrderId = Guid.Parse("01900000-0000-7000-8000-0000000000c4");
    public static readonly Guid ClientId = Guid.Parse("01900000-0000-7000-8000-0000000000c5");

    public static readonly ShopifyAddress Address = new(
        "Laura Gómez", "Cra 5 # 10-20", null, "Medellin", "Antioquia", "ANT", "CO", "3001112233");

    public ShopifyEditTestWorld()
    {
        World.Geography.Cities[("ANTIOQUIA", "Medellin")] = new ShopifyCityRef(DepartmentId, MedellinId);
        World.Geography.Cities[("ANTIOQUIA", "Envigado")] = new ShopifyCityRef(DepartmentId, EnvigadoId);
        World.Geography.Departments["ANTIOQUIA"] = DepartmentId;
        Edits = new FakeOrderEditRepository(World.Database);
        Applied = Snapshot();
        Order = new ShopifyOrderState(
            OrderId, 3, "Open", true, false, false, "PaymentPending", ClientId, "COP", false,
            [new ShopifyOrderLineState(Shirt.VariantId, Shirt.ProductId, 2m, 50_000m, 0m, 19)],
            new ShopifyShippingDraft(false, null, null, null, 0m, 0, false),
            Party(MedellinId),
            null,
            "A");
    }

    public ImporterWorld World { get; } = new();

    public FakeOrderEditRepository Edits { get; }

    public ShopifyProductRef Shirt => World.Products.Products["CAM-001"];

    public ShopifyOrderSnapshot Applied { get; set; }

    public ShopifyOrderState Order { get; set; }

    public FakeClock Clock { get; } = new(ImporterWorld.Now);

    public ShopifyEditPlanner Planner =>
        new(
            new ShopifyLineResolver(World.Products, World.CustomLines, World.ProductLinks),
            new ShopifyShippingResolver(World.Shipping),
            World.Resolver,
            World.Customers,
            World.UnresolvedCities,
            World.Geography,
            Clock);

    public ShopifyOrderEdits Detector => new(Planner, World.Writer, Edits, Clock);

    public ShopifyOrderProjection Projection => ShopifyOrderProjection.From(Order, null, Applied.Attributes);

    public static ShopifyLine Line(decimal quantity = 2m, decimal price = 50_000m, decimal discount = 0m) =>
        new("CAM-001", quantity, price, discount, "Camiseta", false, 9001, 2m);

    public static ShopifyShippingFingerprint Party(Guid cityId, string phone = "3001112233") =>
        new("Laura Gómez", phone, "laura@verde.co", "Cra 5 # 10-20", DepartmentId, cityId);

    public static ShopifyOrderSnapshot Snapshot(
        IReadOnlyList<ShopifyLine>? lines = null,
        ShopifyAddress? address = null,
        string? note = "A",
        IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyList<ShopifyShippingLine>? shippingLines = null,
        int minutes = 0,
        bool taxesIncluded = true,
        DateTimeOffset? cancelledAt = null) =>
        ImporterWorld.Order(
            lines: lines ?? [Line()],
            address: address ?? Address,
            note: note,
            attributes: attributes,
            shippingLines: shippingLines,
            taxesIncluded: taxesIncluded,
            cancelledAt: cancelledAt) with
        {
            UpdatedAt = ImporterWorld.Now.AddMinutes(minutes),
        };

    public ShopifyStore Configure(ShopifyEditSync sync, IReadOnlyCollection<string>? paymentStatuses = null)
    {
        World.Store.Configure(
            ImporterWorld.AdvisorId,
            new ShopifyBillingAccount(ImporterWorld.CompanyId, "Bancolombia", "123-456", "COP"),
            null,
            ShopifyStore.DefaultDocumentAttributeKey,
            ShopifyStore.DefaultDocumentTypeAttributeKey,
            ShopifyStore.DefaultIdentificationTypeCode,
            null,
            sync,
            paymentStatuses ?? ShopifyPaymentStatuses.All,
            ImporterWorld.AdvisorId,
            ImporterWorld.Now);
        return World.Store;
    }

    public Task<ShopifyEditPlan> PlanAsync(ShopifyOrderSnapshot incoming, string? qepAttributes = null) =>
        Planner.PlanAsync(World.Store, ImportedRow(legacy: true), Applied, Projection, incoming, Order, qepAttributes, TestContext.Current.CancellationToken);

    /// <summary>La fila del espejo importada, ya guardada. Sin <paramref name="legacy"/> lleva la versión aplicada y la proyección
    /// de <see cref="Applied"/> y <see cref="Order"/>; con él, es una fila importada antes de la sincronización (plan D6).</summary>
    public ShopifyOrderMirror ImportedRow(bool legacy = false)
    {
        var row = World.RowFor(Applied);
        row.ApplySnapshot(ShopifyOrderIngestion.WritePayload(Applied), Applied.UpdatedAt, Applied.ToHeader());
        row.MarkImported(ClientId, OrderId, 0m, null, ImporterWorld.Now);
        if (!legacy)
        {
            row.RecordApplied(ShopifyOrderIngestion.WritePayload(Applied), ShopifyOrderProjection.Write(Projection));
        }

        World.Writer.States[OrderId] = Order;
        return row;
    }

    /// <summary>Lo que hace la ingesta con una fila que ya existía: guarda el payload anterior, aplica el snapshot, llama al
    /// detector, guarda y, con la tienda automática, aplica el cambio pendiente y vuelve a guardar.</summary>
    public async Task DeliverAsync(ShopifyOrderMirror row, ShopifyOrderSnapshot incoming)
    {
        var previous = ShopifyMirrorState.Of(row);
        var previousPayload = row.Payload;
        row.ApplySnapshot(ShopifyOrderIngestion.WritePayload(incoming), incoming.UpdatedAt, incoming.ToHeader());
        await Detector.DetectAsync(
            new ShopifyOrderChange(World.Connection, World.Store, row, previous, incoming), previousPayload, TestContext.Current.CancellationToken);
        await World.UnitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (await Detector.ApplyPendingAsync(World.Store, row, TestContext.Current.CancellationToken))
        {
            await World.UnitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    public IReadOnlyList<ShopifyOrderEdit> AllEdits => [.. World.Database.OrderEdits, .. World.Database.PendingOrderEdits];

    public static IReadOnlyList<(string Kind, string Outcome, string? Reason)> Groups(ShopifyOrderEdit edit) =>
        [.. JsonNode.Parse(edit.Groups)!.AsArray().Select(group =>
            (group!["kind"]!.GetValue<string>(), group["outcome"]!.GetValue<string>(), group["reason"]?.GetValue<string>()))];
}
