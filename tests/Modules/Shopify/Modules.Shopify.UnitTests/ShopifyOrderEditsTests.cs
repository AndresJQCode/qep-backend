using BuildingBlocks.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ShopifyEditTestWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §3 y §4: el detector que la ingesta llama después del espejo. Crea el cambio con el estado de la tabla
/// del §4, reemplaza el pendiente, no repite la misma entrega y, con la tienda automática, aplica en el mismo procesamiento.</summary>
public sealed class ShopifyOrderEditsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, true, ShopifyEditSync.Automatic, ShopifyOrderEditStatus.Blocked, "order_not_editable")]
    [InlineData(true, false, ShopifyEditSync.Automatic, ShopifyOrderEditStatus.Blocked, "payment_status_not_allowed")]
    [InlineData(true, true, ShopifyEditSync.Automatic, ShopifyOrderEditStatus.Applied, null)]
    [InlineData(true, true, ShopifyEditSync.Manual, ShopifyOrderEditStatus.Pending, null)]
    public async Task TheStateTableOfTheSpec(
        bool editable, bool paymentAllowed, ShopifyEditSync sync, ShopifyOrderEditStatus expected, string? reason)
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(sync, paymentAllowed ? null : ["FullPaymentReceived"]);
        world.Order = world.Order with { IsEditable = editable, IsPrepared = !editable };
        var row = world.ImportedRow();

        await world.DeliverAsync(row, Snapshot(lines: [Line(quantity: 1m)], minutes: 10));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal((expected, reason), (edit.Status, edit.StatusReason));
        Assert.Equal((OrderId, row.Id, (DateTimeOffset?)world.Applied.UpdatedAt), (edit.QepOrderId, edit.MirrorOrderId, edit.BaseUpdatedAt));
        Assert.Equal(expected == ShopifyOrderEditStatus.Applied, world.World.Writer.States[OrderId].Lines[0].Quantity == 1m);
    }

    [Fact]
    public async Task ASecondSnapshotSupersedesThePendingChange()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Manual);
        var row = world.ImportedRow();

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));
        await world.DeliverAsync(row, Snapshot(note: "C", minutes: 20));

        Assert.Equal(
            [ShopifyOrderEditStatus.Superseded, ShopifyOrderEditStatus.Pending],
            world.AllEdits.OrderBy(edit => edit.SnapshotUpdatedAt).Select(edit => edit.Status).ToArray());
    }

    [Fact]
    public async Task TheSameSnapshotTwiceCreatesOneChange()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Manual);
        var row = world.ImportedRow();

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));
        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));

        Assert.Equal(ShopifyOrderEditStatus.Pending, Assert.Single(world.AllEdits).Status);
    }

    [Fact]
    public async Task AutomaticWithAStaleOrderVersionStaysPending()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        world.World.Writer.OnApplyEdit = _ => throw new RequestConcurrencyException("concurrency.conflict", "The order changed.");
        var row = world.ImportedRow();

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));

        Assert.Equal(ShopifyOrderEditStatus.Pending, Assert.Single(world.AllEdits).Status);
        Assert.Equal("A", world.World.Writer.States[OrderId].BuyerNotes);
    }

    // Spec §3: el conflicto se marca para que se vea en la bandeja. Con la tienda automática no se aplica solo: queda pendiente.
    [Fact]
    public async Task AnAutomaticChangeWithAConflictStaysPending()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        var row = world.ImportedRow();
        world.World.Writer.States[OrderId] = world.Order with { BuyerNotes = "Q" };

        await world.DeliverAsync(row, Snapshot(note: "C", minutes: 10));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal((ShopifyOrderEditStatus.Pending, (string?)null), (edit.Status, edit.StatusReason));
        Assert.Equal([("Note", "Conflict", "qep_changed")], Groups(edit));
        Assert.Empty(world.World.Writer.Edits);
        Assert.Equal(ShopifyOrderIngestion.WritePayload(world.Applied), row.AppliedPayload);
    }

    // Un conflicto deja todo el cambio pendiente, también los grupos que sí se aplicarían: lo decide una persona.
    [Fact]
    public async Task AnAutomaticChangeWithAConflictAndAnApplyGroupAppliesNothing()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        var row = world.ImportedRow();
        world.World.Writer.States[OrderId] = world.Order with { BuyerNotes = "Q" };

        await world.DeliverAsync(row, Snapshot(lines: [Line(quantity: 1m)], note: "C", minutes: 10));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);
        Assert.Equal([("Lines", "Apply", (string?)null), ("Note", "Conflict", "qep_changed")], Groups(edit));
        Assert.Equal(2m, world.World.Writer.States[OrderId].Lines[0].Quantity);
    }

    // Review M-2: otra regla del pedido rechazó la aplicación automática; el cambio sigue pendiente y dice por qué.
    [Fact]
    public async Task AnAutomaticApplyRejectedByTheOrderStaysPendingWithTheRuleCode()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        world.World.Writer.OnApplyEdit = _ => new ShopifyOrderEditOutcome(null, "order.order.store_pickup");
        var row = world.ImportedRow();

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal((ShopifyOrderEditStatus.Pending, "order.order.store_pickup"), (edit.Status, edit.StatusReason));
        Assert.Equal("A", world.World.Writer.States[OrderId].BuyerNotes);
    }

    [Fact]
    public async Task ALegacyRowUsesThePreviousPayloadAsBase()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Manual);
        // El pedido viejo no tiene la nota en BuyerNotes (sin backfill): no es una diferencia de Shopify y no sale.
        world.Order = world.Order with { BuyerNotes = null };
        var row = world.ImportedRow(legacy: true);

        await world.DeliverAsync(row, Snapshot(lines: [Line(quantity: 1m)], minutes: 10));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal([("Lines", "Apply", (string?)null)], Groups(edit));
    }

    [Fact]
    public async Task ALegacyRowTakesTheProjectionFromTheCurrentOrder()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Manual);
        // Alguien corrigió el teléfono antes del despliegue: para un pedido viejo nada cuenta como «lo tocó QEP» (plan D6).
        world.Order = world.Order with { ShippingParty = Party(MedellinId, phone: "3009998877") };
        var row = world.ImportedRow(legacy: true);

        await world.DeliverAsync(row, Snapshot(address: Address with { City = "Envigado" }, minutes: 10));

        Assert.Equal([("ShippingAddress", "Apply", (string?)null)], Groups(Assert.Single(world.AllEdits)));
    }

    [Fact]
    public async Task AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        world.Order = world.Order with { BuyerNotes = null };
        var row = world.ImportedRow(legacy: true);

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal(ShopifyOrderEditStatus.Applied, edit.Status);
        Assert.Equal([("Note", "Apply", (string?)null)], Groups(edit));
        Assert.Equal("B", world.World.Writer.States[OrderId].BuyerNotes);
    }

    [Fact]
    public async Task ACancelledSnapshotComputesNothing()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        var row = world.ImportedRow();

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10, cancelledAt: ImporterWorld.Now));

        Assert.Empty(world.AllEdits);
        Assert.Empty(world.World.Writer.Edits);
    }

    // Review Focus 5: el eco adopta el snapshot pero no mueve la proyección, así que lo que corrigió el asesor sigue protegido.
    [Fact]
    public async Task AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        var row = world.ImportedRow();
        world.World.Writer.States[OrderId] = world.Order with { BuyerNotes = "B" };

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));
        Assert.Empty(world.AllEdits);
        Assert.Equal("B", ShopifyOrderIngestion.ReadPayload(row.AppliedPayload!).Note);

        await world.DeliverAsync(row, Snapshot(note: "C", minutes: 20));

        var edit = Assert.Single(world.AllEdits);
        Assert.Equal([("Note", "Conflict", "qep_changed")], Groups(edit));
        Assert.Equal("B", world.World.Writer.States[OrderId].BuyerNotes);
    }

    [Fact]
    public async Task AnAppliedChangeMovesTheAppliedPayloadAndTheProjection()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        var row = world.ImportedRow();
        var incoming = Snapshot(lines: [Line(quantity: 1m)], note: "B", minutes: 10);

        await world.DeliverAsync(row, incoming);

        Assert.Equal(ShopifyOrderEditStatus.Applied, Assert.Single(world.AllEdits).Status);
        Assert.Equal(ShopifyOrderIngestion.WritePayload(incoming), row.AppliedPayload);
        var projection = ShopifyOrderProjection.Read(row.AppliedProjection!);
        Assert.Equal((1m, "B"), (Assert.Single(projection.Lines).Quantity, projection.BuyerNotes));
    }

    [Fact]
    public async Task ARowThatWasNotImportedComputesNothing()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Automatic);
        var row = world.World.RowFor(world.Applied);

        await world.DeliverAsync(row, Snapshot(note: "B", minutes: 10));

        Assert.Empty(world.AllEdits);
    }
}
