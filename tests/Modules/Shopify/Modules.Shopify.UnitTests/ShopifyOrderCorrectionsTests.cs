using BuildingBlocks.Application;
using FluentValidation;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ShopifyEditTestWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-14 §5 y plan D3, D20-D22: el asesor corrige la parte de envío, la nota del cliente y los atributos de un pedido de
/// Shopify. Sólo se escriben y se encolan los grupos que cambiaron, en una transacción; la proyección no se mueve (lo corrigió el
/// asesor: una edición posterior de Shopify sobre ese grupo es conflicto).
/// </summary>
public sealed class ShopifyOrderCorrectionsTests
{
    private static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid MembershipId = Guid.Parse("01900000-0000-7000-8000-0000000000e2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyPartyInput PartyInput(Guid cityId, string phone = "3001112233") =>
        new("Laura Gómez", phone, "laura@verde.co", "Cra 5 # 10-20", DepartmentId, cityId);

    private sealed class CorrectionWorld
    {
        public CorrectionWorld(Func<ShopifyOrderState, ShopifyOrderState>? order = null, bool legacy = false)
        {
            Edit.Configure(ShopifyEditSync.Manual);
            Edit.World.Database.Stores[ImporterWorld.ConnectionId] = Edit.World.Store;
            if (order is not null)
            {
                Edit.Order = order(Edit.Order);
            }

            Memberships.Members[(SubjectId, ImporterWorld.TenantId)] = MembershipId;
            Row = Edit.ImportedRow(legacy);
            Corrections = new FakeOrderCorrectionWriter(Edit.World.Writer);
            Pushes = new FakeOrderPushRepository(Edit.World.Database);
        }

        public ShopifyEditTestWorld Edit { get; } = new();

        public ShopifyOrderMirror Row { get; }

        public FakeMembershipDirectory Memberships { get; } = new();

        public FakeOrderCorrectionWriter Corrections { get; }

        public FakeOrderPushRepository Pushes { get; }

        public ShopifyOrderState Order => Edit.World.Writer.States[OrderId];

        public IReadOnlyList<ShopifyOrderPush> AllPushes => [.. Edit.World.Database.OrderPushes, .. Edit.World.Database.PendingOrderPushes];

        private static ShopifyTestContext Context =>
            new(ImporterWorld.TenantId, SubjectId, [ShopifyPermissions.OrderRead, ShopifyPermissions.OrderManage]);

        private ShopifyOrderSyncReader Reader =>
            new(
                Pushes,
                Edit.Edits,
                new ShopifyOrderChangeMapper(new FakeTenantConnections(), new FakeOrderNumbers(), new FakeMemberDirectory(), Edit.World.Mirrors));

        public CorrectShopifyOrderHandler Handler =>
            new(
                Context,
                new ShopifyEnabledModules(),
                Memberships,
                Edit.World.Mirrors,
                Edit.World.Writer,
                Corrections,
                new ShopifyOrderPushes(Pushes),
                new FakeAuditRecorder(Edit.World.Database),
                Edit.World.UnitOfWork,
                Edit.Clock,
                Reader,
                Connections,
                Edit.World.Geography,
                new CorrectShopifyOrderValidator());

        /// <summary>La conexión del pedido, operativa: la corrección se encola (owner 2026-11-14).</summary>
        public FakeConnectionDirectory Connections
        {
            get
            {
                var directory = new FakeConnectionDirectory();
                directory.Connections[Edit.World.Connection.ShopDomain] = Edit.World.Connection;
                return directory;
            }
        }

        public GetShopifyOrderSyncHandler SyncHandler => new(Context, new ShopifyEnabledModules(), Edit.World.Mirrors, Edit.World.Writer, Reader);

        public CorrectShopifyOrderCommand Command(
            ShopifyPartyInput? party = null, string? buyerNotes = "A", IReadOnlyList<ShopifyAttributeInput>? attributes = null) =>
            new(ImporterWorld.TenantId, Row.Id, Order.Version, party, buyerNotes, attributes ?? []);

        public Task<ShopifyOrderSyncDto> CorrectAsync(CorrectShopifyOrderCommand command) => Handler.HandleAsync(command, Ct);
    }

    [Fact]
    public async Task OnlyTheChangedGroupsAreEnqueued()
    {
        var world = new CorrectionWorld();

        await world.CorrectAsync(world.Command(PartyInput(MedellinId), buyerNotes: "B"));

        var push = Assert.Single(world.AllPushes);
        Assert.Equal([ShopifyPushGroup.Note], push.Groups);
        var correction = Assert.Single(world.Corrections.Calls);
        Assert.Null(correction.ShippingParty);
        Assert.Equal((true, "B"), (correction.ReplaceBuyerNotes, correction.BuyerNotes));
        Assert.Equal(MembershipId, correction.MemberId);
        Assert.Contains(world.Edit.World.Database.Audit, entry => entry.Action == ShopifyAuditActions.OrderCorrected);
        Assert.Equal(["begin", "save", "commit"], world.Edit.World.Database.Log.TakeLast(3));
    }

    // El formulario de la corrección no tiene código postal: reenviar la misma parte de un pedido que lo guarda no es un cambio.
    [Fact]
    public async Task TheSamePartyOnAnOrderWithPostalCodeSendsNothing()
    {
        var world = new CorrectionWorld(
            order => order with { ShippingParty = order.ShippingParty! with { PostalCode = "050021" } });

        var result = await world.CorrectAsync(world.Command(PartyInput(MedellinId)));

        Assert.Equal(CorrectShopifyOrderHandler.SyncNothingToSend, result.ShopifySync);
        Assert.Empty(world.AllPushes);
    }

    [Fact]
    public async Task ASecondCorrectionMergesIntoThePendingPush()
    {
        var world = new CorrectionWorld();
        await world.CorrectAsync(world.Command(buyerNotes: "B"));

        await world.CorrectAsync(world.Command(buyerNotes: "B", attributes: [new("Documento", "1036000111")]));

        var push = Assert.Single(world.AllPushes);
        Assert.Equal([ShopifyPushGroup.Note, ShopifyPushGroup.Attributes], push.Groups);
        Assert.Equal("1036000111", ShopifyEditPlanner.ReadAttributes(world.Row.QepAttributes!)["Documento"]);
    }

    [Fact]
    public async Task NothingChangedWritesNothing()
    {
        var world = new CorrectionWorld();
        var saves = world.Edit.World.UnitOfWork.Saves;

        var dto = await world.CorrectAsync(world.Command(PartyInput(MedellinId), buyerNotes: " A "));

        Assert.Empty(world.AllPushes);
        Assert.Empty(world.Corrections.Calls);
        Assert.Equal(saves, world.Edit.World.UnitOfWork.Saves);
        Assert.Null(world.Row.QepAttributes);
        Assert.Equal((true, (string?)null, (ShopifyOrderPushDto?)null), (dto.CanEdit, dto.EditBlockedReason, dto.Push));
    }

    // Review Focus 5 y plan D3: lo que corrige el asesor no mueve la proyección.
    [Fact]
    public async Task ACorrectionDoesNotMoveTheProjection()
    {
        var world = new CorrectionWorld();
        var projection = world.Row.AppliedProjection;

        await world.CorrectAsync(world.Command(PartyInput(EnvigadoId), buyerNotes: "B", attributes: [new("Documento", "1")]));

        Assert.Equal(projection, world.Row.AppliedProjection);
        Assert.Equal(EnvigadoId, world.Order.ShippingParty!.CityId);
        Assert.Equal(
            [ShopifyPushGroup.ShippingAddress, ShopifyPushGroup.Note, ShopifyPushGroup.Attributes],
            Assert.Single(world.AllPushes).Groups);
    }

    [Fact]
    public async Task ANotImportedRowIsNotImported()
    {
        var world = new CorrectionWorld();
        world.Row.MarkCancelled(null);

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.CorrectAsync(world.Command(buyerNotes: "B")));

        Assert.Equal("shopify.order.not_imported", error.Code);
        Assert.Empty(world.AllPushes);
    }

    [Fact]
    public async Task AStaleOrderVersionIsAConcurrencyConflict()
    {
        var world = new CorrectionWorld();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            world.CorrectAsync(world.Command(buyerNotes: "B") with { ExpectedOrderVersion = world.Order.Version - 1 }));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(world.Corrections.Calls);
    }

    [Fact]
    public async Task ANotEditableOrderIsNotEditable()
    {
        var world = new CorrectionWorld(order => order with { IsEditable = false, IsPrepared = true });

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() =>
            world.CorrectAsync(world.Command(attributes: [new("Documento", "1")])));

        Assert.Equal("order.order.not_editable", error.Code);
        Assert.Empty(world.AllPushes);
        Assert.Null(world.Row.QepAttributes);
    }

    [Fact]
    public void CorrectionValidationErrorKeysAreTheContractOnes()
    {
        var validator = new CorrectShopifyOrderValidator();
        var invalid = new CorrectShopifyOrderCommand(
            ImporterWorld.TenantId,
            Guid.CreateVersion7(),
            1,
            new ShopifyPartyInput("", new string('3', 33), "x", "", Guid.Empty, null),
            new string('n', 2001),
            [new("", null), new("A", new string('v', 1001)), new("A", null)]);

        var keys = validator.Validate(invalid).Errors.Select(error => error.PropertyName).ToHashSet(StringComparer.Ordinal);
        var nullAttributes = validator.Validate(invalid with { ShippingParty = null, BuyerNotes = null, Attributes = null })
            .Errors.Select(error => error.PropertyName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "shippingParty.name", "shippingParty.phone", "shippingParty.email", "shippingParty.address", "shippingParty.departmentId",
                "shippingParty.cityId", "attributes[0].key", "attributes[1].value", "attributes", "buyerNotes",
            },
            keys);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "attributes" }, nullAttributes);
    }

    // Review Minor 4: un elemento null en la lista era un 500 (ChildRules salta los null y el handler los desreferenciaba).
    [Fact]
    public void ANullAttributeIsAValidationErrorOnItsIndex()
    {
        var command = new CorrectShopifyOrderCommand(
            ImporterWorld.TenantId, Guid.CreateVersion7(), 1, null, null, [new("Documento", "1"), null!]);

        var keys = new CorrectShopifyOrderValidator().Validate(command).Errors.Select(error => error.PropertyName).ToArray();

        Assert.Equal(["attributes[1]"], keys);
    }

    [Fact]
    public async Task AnInvalidCorrectionIsAValidationErrorBeforeAnyWrite()
    {
        var world = new CorrectionWorld();

        await Assert.ThrowsAsync<ValidationException>(() => world.CorrectAsync(world.Command(attributes: null) with { Attributes = null }));

        Assert.Empty(world.Corrections.Calls);
    }

    // Owner 2026-11-15: el departamento y la ciudad existen, y la ciudad es de ese departamento. Si no, el envío a Shopify se
    // quedaba reintentando (ciudad inexistente) o mandaba una dirección incoherente (ciudad de otro departamento).
    [Theory]
    [InlineData("otherDepartmentCity", "shippingParty.cityId")]
    [InlineData("unknownCity", "shippingParty.cityId")]
    [InlineData("unknownDepartment", "shippingParty.departmentId")]
    public async Task AShippingCityOutsideItsDepartmentIsAValidationErrorBeforeAnyWrite(string scenario, string expectedKey)
    {
        var world = new CorrectionWorld();
        var cundinamarca = Guid.Parse("01900000-0000-7000-8000-0000000000c4");
        var soacha = Guid.Parse("01900000-0000-7000-8000-0000000000c5");
        world.Edit.World.Geography.Cities[("CUNDINAMARCA", "Soacha")] = new ShopifyCityRef(cundinamarca, soacha);
        var party = scenario switch
        {
            "otherDepartmentCity" => PartyInput(soacha),
            "unknownCity" => PartyInput(Guid.CreateVersion7()),
            _ => PartyInput(MedellinId) with { DepartmentId = Guid.CreateVersion7() },
        };
        var saves = world.Edit.World.UnitOfWork.Saves;

        var error = await Assert.ThrowsAsync<ValidationException>(() => world.CorrectAsync(world.Command(party, buyerNotes: "B")));

        Assert.Equal([expectedKey], error.Errors.Select(failure => failure.PropertyName).ToArray());
        Assert.Empty(world.Corrections.Calls);
        Assert.Empty(world.AllPushes);
        Assert.Equal(saves, world.Edit.World.UnitOfWork.Saves);
    }

    // D22: vacío, espacios y null son lo mismo, y se guardan null.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ABlankBuyerNoteIsNull(string? notes)
    {
        var withNote = new CorrectionWorld();
        var withoutNote = new CorrectionWorld(order => order with { BuyerNotes = null });

        await withNote.CorrectAsync(withNote.Command(buyerNotes: notes));
        await withoutNote.CorrectAsync(withoutNote.Command(buyerNotes: notes));

        var correction = Assert.Single(withNote.Corrections.Calls);
        Assert.Equal((true, (string?)null), (correction.ReplaceBuyerNotes, correction.BuyerNotes));
        Assert.Empty(withoutNote.AllPushes);
        Assert.Empty(withoutNote.Corrections.Calls);
    }

    // D21: la parte de envío sólo se corrige en Colombia.
    [Fact]
    public async Task ANonColombianAddressIsNotSupported()
    {
        var world = new CorrectionWorld(order => order with { ShippingCountry = "VE" });

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() =>
            world.CorrectAsync(world.Command(PartyInput(EnvigadoId), buyerNotes: "B")));

        Assert.Equal("shopify.order.address_not_supported", error.Code);
        Assert.Empty(world.Corrections.Calls);
        Assert.Empty(world.AllPushes);
    }

    [Fact]
    public async Task AStorePickupOrderTakesNoShippingParty()
    {
        var world = new CorrectionWorld(order => order with { IsStorePickup = true, ShippingParty = null });

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.CorrectAsync(world.Command(PartyInput(EnvigadoId))));

        Assert.Equal("order.order.store_pickup", error.Code);
    }

    // D20: el motivo por el que no se edita, en orden de precedencia.
    [Theory]
    [InlineData("NotImported")]
    [InlineData("Cancelled")]
    [InlineData("Archived")]
    [InlineData("Invoiced")]
    [InlineData("Prepared")]
    public async Task EditBlockedReasonFollowsTheOrderState(string expected)
    {
        var world = new CorrectionWorld(order => expected switch
        {
            "Cancelled" => order with { Status = "Cancelled", IsEditable = false, IsPrepared = true },
            "Archived" => order with { Status = "Archived", IsEditable = false, IsInvoiced = true },
            "Invoiced" => order with { IsEditable = false, IsInvoiced = true, IsPrepared = true },
            "Prepared" => order with { IsEditable = false, IsPrepared = true },
            _ => order,
        });
        if (expected == "NotImported")
        {
            world.Row.MarkCancelled(null);
        }

        var dto = await world.SyncHandler.HandleAsync(new GetShopifyOrderSyncQuery(ImporterWorld.TenantId, OrderId), Ct);
        var editable = await new CorrectionWorld().SyncHandler.HandleAsync(new GetShopifyOrderSyncQuery(ImporterWorld.TenantId, OrderId), Ct);

        Assert.Equal((false, expected), (dto.CanEdit, dto.EditBlockedReason));
        Assert.Equal((true, (string?)null), (editable.CanEdit, editable.EditBlockedReason));
    }

    [Fact]
    public async Task AStorePickupOrderHasNoShippingAddressSupport()
    {
        var world = new CorrectionWorld(order => order with { IsStorePickup = true, ShippingParty = null });

        var dto = await world.SyncHandler.HandleAsync(new GetShopifyOrderSyncQuery(ImporterWorld.TenantId, OrderId), Ct);

        Assert.Equal((true, false), (dto.CanEdit, dto.ShippingAddressSupported));
    }

    [Fact]
    public async Task GetSyncShowsThePendingPushAndTheEditedAttributes()
    {
        var world = new CorrectionWorld();
        await world.CorrectAsync(world.Command(attributes: [new(" Documento ", "1036000111")]));

        var dto = await world.SyncHandler.HandleAsync(new GetShopifyOrderSyncQuery(ImporterWorld.TenantId, OrderId), Ct);

        Assert.True(dto.AttributesEditedInQep);
        Assert.Equal([new ShopifyAttributeDto("Documento", "1036000111")], dto.Attributes);
        Assert.Equal("Pending", dto.Push!.Status);
        Assert.Equal(["Attributes"], dto.Push.Groups);
        Assert.Equal(world.Order.Version, dto.OrderVersion);
    }

    [Fact]
    public async Task AnUnknownQepOrderIsNotFound()
    {
        var world = new CorrectionWorld();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            world.SyncHandler.HandleAsync(new GetShopifyOrderSyncQuery(ImporterWorld.TenantId, Guid.CreateVersion7()), Ct));

        Assert.Equal("shopify.order.not_found", error.Code);
    }
}

/// <summary>Quotations visto desde la corrección: cambia el pedido en memoria como el adaptador real (la versión vieja es
/// <c>concurrency.conflict</c>) y anota cada llamada.</summary>
internal sealed class FakeOrderCorrectionWriter(FakeOrderWriter orders) : IShopifyOrderCorrectionWriter
{
    public List<ShopifyOrderCorrection> Calls { get; } = [];

    public Task<ShopifyOrderState> CorrectAsync(ShopifyOrderCorrection correction, CancellationToken cancellationToken)
    {
        Calls.Add(correction);
        var state = orders.States[correction.OrderId];
        if (state.Version != correction.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The order changed.");
        }

        var updated = state with
        {
            ShippingParty = correction.ShippingParty is { DepartmentId: { } department, CityId: { } city } party
                // Como Quotations: la corrección no recibe código postal y conserva el del pedido mientras no cambie el
                // departamento.
                ? new ShopifyShippingFingerprint(
                    party.Name, party.Phone, party.Email, party.Address, department, city,
                    state.ShippingParty?.DepartmentId == department ? state.ShippingParty.PostalCode : null)
                : state.ShippingParty,
            BuyerNotes = correction.ReplaceBuyerNotes ? correction.BuyerNotes : state.BuyerNotes,
            Version = state.Version + 1,
        };
        orders.States[correction.OrderId] = updated;
        return Task.FromResult(updated);
    }
}
