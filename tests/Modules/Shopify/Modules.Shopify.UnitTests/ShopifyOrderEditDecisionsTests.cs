using BuildingBlocks.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ShopifyEditTestWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §4 y plan D9/D16: aceptar vuelve a planear contra el pedido de ahora; rechazar y revisar adoptan el
/// snapshot sin tocar el pedido. Sólo un cambio abierto se decide (409), y «revisado» es sólo para uno bloqueado (422).</summary>
public sealed class ShopifyOrderEditDecisionsTests
{
    private static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid MembershipId = Guid.Parse("01900000-0000-7000-8000-0000000000d2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class DecisionWorld
    {
        public DecisionWorld(bool editable = true)
        {
            Edit.Configure(ShopifyEditSync.Manual);
            Edit.World.Database.Stores[ImporterWorld.ConnectionId] = Edit.World.Store;
            Edit.Order = Edit.Order with { IsEditable = editable, IsPrepared = !editable };
            Memberships.Members[(SubjectId, ImporterWorld.TenantId)] = MembershipId;
            Row = Edit.ImportedRow();
        }

        public ShopifyEditTestWorld Edit { get; } = new();

        public ShopifyOrderMirror Row { get; }

        public FakeMembershipDirectory Memberships { get; } = new();

        public ShopifyChangeDecider Decider =>
            new(
                new ShopifyTestContext(
                    ImporterWorld.TenantId, SubjectId, [ShopifyPermissions.OrderRead, ShopifyPermissions.OrderManage]),
                new ShopifyEnabledModules(),
                Memberships,
                Edit.Edits,
                Edit.World.Mirrors,
                new FakeStoreRepository(Edit.World.Database),
                Edit.World.Writer,
                Edit.Planner,
                Edit.Detector,
                new FakeAuditRecorder(Edit.World.Database),
                Edit.World.UnitOfWork,
                Edit.Clock,
                new ShopifyOrderChangeMapper(new FakeTenantConnections(), new FakeOrderNumbers(), new FakeMemberDirectory(), Edit.World.Mirrors));

        public async Task<ShopifyOrderEdit> DeliverAsync(ShopifyOrderSnapshot incoming)
        {
            await Edit.DeliverAsync(Row, incoming);
            return Edit.AllEdits.OrderByDescending(edit => edit.CreatedAt).ThenByDescending(edit => edit.SnapshotUpdatedAt).First();
        }
    }

    [Fact]
    public async Task AcceptABlockedChangeOnceTheOrderIsEditableAgainApplies()
    {
        var world = new DecisionWorld(editable: false);
        var edit = await world.DeliverAsync(Snapshot(lines: [Line(quantity: 1m)], minutes: 10));
        Assert.Equal(ShopifyOrderEditStatus.Blocked, edit.Status);
        world.Edit.World.Writer.States[OrderId] = world.Edit.World.Writer.States[OrderId] with { IsEditable = true, IsPrepared = false };

        var dto = await world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct);

        Assert.Equal(("Applied", (string?)null), (dto.Status, dto.StatusReason));
        Assert.Equal((ShopifyOrderEditStatus.Applied, (Guid?)MembershipId), (edit.Status, edit.DecidedBy));
        Assert.Equal(1m, world.Edit.World.Writer.States[OrderId].Lines[0].Quantity);
        Assert.Contains(world.Edit.World.Database.Audit, entry => entry.Action == ShopifyAuditActions.OrderEditAccepted);
    }

    [Fact]
    public async Task AcceptWhileStillNotEditableIs422AndStaysBlocked()
    {
        var world = new DecisionWorld(editable: false);
        var edit = await world.DeliverAsync(Snapshot(lines: [Line(quantity: 1m)], minutes: 10));

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("shopify.edit.order_not_editable", error.Code);
        Assert.Equal((ShopifyOrderEditStatus.Blocked, "order_not_editable"), (edit.Status, edit.StatusReason));
        Assert.Empty(world.Edit.World.Writer.Edits);
    }

    // D16: el asesor preparó el pedido entre el aviso y el clic.
    [Fact]
    public async Task AcceptAPendingThatCannotApplyAnymoreBlocksIt()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);
        world.Edit.World.Writer.States[OrderId] = world.Edit.World.Writer.States[OrderId] with { IsEditable = false, IsPrepared = true };
        var saves = world.Edit.World.UnitOfWork.Saves;

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("shopify.edit.order_not_editable", error.Code);
        Assert.Equal((ShopifyOrderEditStatus.Blocked, "order_not_editable"), (edit.Status, edit.StatusReason));
        Assert.Equal(saves + 1, world.Edit.World.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AcceptWithAStaleOrderIs412AndLeavesTheChangePending()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));
        world.Edit.World.Writer.OnApplyEdit = _ => throw new RequestConcurrencyException("concurrency.conflict", "The order changed.");

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);
    }

    [Fact]
    public async Task RejectAdoptsTheSnapshotSoTheNextDiffDoesNotProposeIt()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));

        var dto = await world.Decider.RejectAsync(ImporterWorld.TenantId, edit.Id, Ct);
        await world.Edit.DeliverAsync(world.Row, Snapshot(note: "B", minutes: 20));

        Assert.Equal("Rejected", dto.Status);
        Assert.Equal(ShopifyOrderEditStatus.Rejected, Assert.Single(world.Edit.AllEdits).Status);
        Assert.Equal("A", world.Edit.World.Writer.States[OrderId].BuyerNotes);
        Assert.Contains(world.Edit.World.Database.Audit, entry => entry.Action == ShopifyAuditActions.OrderEditRejected);
    }

    [Fact]
    public async Task ReviewAPendingIsNotBlocked()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Decider.ReviewAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("shopify.edit.not_blocked", error.Code);
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);
    }

    [Fact]
    public async Task ReviewABlockedChangeAdoptsTheSnapshot()
    {
        var world = new DecisionWorld(editable: false);
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));

        var dto = await world.Decider.ReviewAsync(ImporterWorld.TenantId, edit.Id, Ct);

        Assert.Equal("Reviewed", dto.Status);
        Assert.Equal("B", ShopifyOrderIngestion.ReadPayload(world.Row.AppliedPayload!).Note);
    }

    [Fact]
    public async Task DecidingAClosedChangeIsNotOpen()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));
        await world.Decider.RejectAsync(ImporterWorld.TenantId, edit.Id, Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("shopify.edit.not_open", error.Code);
    }

    [Fact]
    public async Task AnUnknownChangeIsNotFound()
    {
        var world = new DecisionWorld();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => world.Decider.RejectAsync(ImporterWorld.TenantId, Guid.CreateVersion7(), Ct));

        Assert.Equal("shopify.edit.not_found", error.Code);
    }

    [Fact]
    public async Task WithoutAnActiveMembershipTheDecisionIsForbidden()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));
        world.Memberships.Members.Clear();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => world.Decider.RejectAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal(ShopifyAuthorization.DeniedCode, error.Code);
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);
    }

    // Review N-6: el plan de ahora no trae grupos (todo es eco) y el pedido ya no se edita; el bloqueo conserva los grupos que el
    // cambio mostraba en vez de guardar una lista vacía.
    [Fact]
    public async Task AcceptBlockedWithAnEmptyPlanKeepsTheGroupsItShowed()
    {
        var world = new DecisionWorld();
        world.Edit.World.Writer.States[OrderId] = world.Edit.World.Writer.States[OrderId] with { BuyerNotes = "Q" };
        var edit = await world.DeliverAsync(Snapshot(note: "C", minutes: 10));
        var groups = edit.Groups;
        Assert.Equal([("Note", "Conflict", "qep_changed")], Groups(edit));
        world.Edit.World.Writer.States[OrderId] = world.Edit.World.Writer.States[OrderId] with
        {
            BuyerNotes = "C",
            IsEditable = false,
            IsPrepared = true,
        };

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("shopify.edit.order_not_editable", error.Code);
        Assert.Equal((ShopifyOrderEditStatus.Blocked, groups), (edit.Status, edit.Groups));
    }

    // Review M-2: otra regla del pedido al aceptar es un 422 con el código de Quotations, y el cambio sigue abierto.
    [Fact]
    public async Task AcceptRejectedByAnotherOrderRuleIs422WithItsCode()
    {
        var world = new DecisionWorld();
        var edit = await world.DeliverAsync(Snapshot(note: "B", minutes: 10));
        world.Edit.World.Writer.OnApplyEdit = _ => new ShopifyOrderEditOutcome(null, "order.order.items_required");

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.Decider.AcceptAsync(ImporterWorld.TenantId, edit.Id, Ct));

        Assert.Equal("order.order.items_required", error.Code);
        Assert.IsAssignableFrom<BuildingBlocks.Domain.DomainException>(error);
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);
    }
}
