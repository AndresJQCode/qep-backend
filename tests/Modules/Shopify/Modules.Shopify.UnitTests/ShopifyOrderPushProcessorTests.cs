using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ShopifyEditTestWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-14 §5 y plan D12, D14: el envío arma <c>orderUpdate</c> sólo con los grupos encolados (Review Focus 4), con la parte
/// de QEP y lo que <c>orderUpdate</c> borraría si no viaja (empresa y país del último snapshot, Review Focus 3), y cierra el envío
/// según lo que respondió Shopify.
/// </summary>
public sealed class ShopifyOrderPushProcessorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class PushWorld
    {
        public PushWorld(string? accessToken = "shpat_x", bool connected = true)
        {
            Row = Edit.ImportedRow();
            Edit.World.Geography.Names[(DepartmentId, MedellinId)] = new ShopifyCityName("MEDELLÍN", "ANTIOQUIA");
            Edit.World.Geography.Names[(DepartmentId, EnvigadoId)] = new ShopifyCityName("ENVIGADO", "ANTIOQUIA");
            if (connected)
            {
                Connections.Connections[Edit.World.Connection.ShopDomain] = Edit.World.Connection with { AccessToken = accessToken };
            }
        }

        public ShopifyEditTestWorld Edit { get; } = new();

        public ShopifyOrderMirror Row { get; }

        public FakeConnectionDirectory Connections { get; } = new();

        public FakeOrderUpdater Updater { get; } = new();

        public ShopifyOrderPushProcessor Processor =>
            new(Connections, Edit.World.Writer, Edit.World.Mirrors, Edit.World.Geography, Updater, Edit.World.UnitOfWork, Edit.Clock);

        public ShopifyOrderPush Push(params ShopifyPushGroup[] groups) => ShopifyOrderPush.Create(Row, groups, ImporterWorld.Now);

        public Task<ShopifyOrderUpdateResult> ProcessAsync(ShopifyOrderPush push) => Processor.ProcessAsync(push, Ct);

        public ShopifyOrderUpdate Sent => Assert.Single(Updater.Updates).Update;
    }

    // Review Focus 4: un envío de dirección no manda la nota (un BuyerNotes vacío pisaría la de Shopify) ni los atributos.
    [Fact]
    public async Task APushOnlySendsTheGroupsItWasEnqueuedFor()
    {
        var world = new PushWorld();

        await world.ProcessAsync(world.Push(ShopifyPushGroup.ShippingAddress));

        Assert.NotNull(world.Sent.ShippingAddress);
        Assert.False(world.Sent.SetNote);
        Assert.Null(world.Sent.Attributes);
        Assert.Equal(ImporterWorld.Order().OrderId, world.Sent.ShopifyOrderId);
    }

    [Fact]
    public async Task NoteAndAttributesGoWhenEnqueued()
    {
        var world = new PushWorld();
        world.Row.SetQepAttributes("""{"Documento":"1036000111"}""");

        await world.ProcessAsync(world.Push(ShopifyPushGroup.Note, ShopifyPushGroup.Attributes));

        Assert.Null(world.Sent.ShippingAddress);
        Assert.Equal((true, "A"), (world.Sent.SetNote, world.Sent.Note));
        Assert.Equal("1036000111", world.Sent.Attributes!["Documento"]);
    }

    // Review Focus 3: orderUpdate reemplaza la dirección entera; la «Empresa» y el país salen del último snapshot.
    [Fact]
    public async Task PushKeepsCompanyAndCountryFromTheLatestSnapshot()
    {
        var world = new PushWorld();
        var latest = Snapshot(address: Address with { Company = "1036000111", CountryCode = "CO" }, minutes: 5);
        world.Row.ApplySnapshot(ShopifyOrderIngestion.WritePayload(latest), latest.UpdatedAt, latest.ToHeader());

        await world.ProcessAsync(world.Push(ShopifyPushGroup.ShippingAddress));

        Assert.Equal(("1036000111", "CO"), (world.Sent.ShippingAddress!.Company, world.Sent.ShippingAddress.CountryCode));
    }

    [Fact]
    public async Task TheCityGoesWithTheQepNameAndTheProvinceCode()
    {
        var world = new PushWorld();

        await world.ProcessAsync(world.Push(ShopifyPushGroup.ShippingAddress));

        Assert.Equal(("MEDELLÍN", "ANT"), (world.Sent.ShippingAddress!.City, world.Sent.ShippingAddress.ProvinceCode));
    }

    // Plan D12: MailingAddressInput no tiene «name»; el eco compara name y address1 + address2 con los de QEP.
    [Fact]
    public async Task TheNameAndAddressGoInFirstNameAndAddress1()
    {
        var world = new PushWorld();

        await world.ProcessAsync(world.Push(ShopifyPushGroup.ShippingAddress));

        var address = world.Sent.ShippingAddress!;
        Assert.Equal(("Laura Gómez", string.Empty), (address.FirstName, address.LastName));
        Assert.Equal(("Cra 5 # 10-20", string.Empty), (address.Address1, address.Address2));
        Assert.Equal("3001112233", address.Phone);
    }

    [Fact]
    public async Task AnUpdatedPushIsSent()
    {
        var world = new PushWorld();
        var push = world.Push(ShopifyPushGroup.Note);

        var result = await world.ProcessAsync(push);

        Assert.Equal(ShopifyOrderUpdateKind.Updated, result.Kind);
        Assert.Equal((ShopifyOrderPushStatus.Sent, (DateTimeOffset?)ImporterWorld.Now), (push.Status, push.SentAt));
        Assert.Equal(1, world.Edit.World.UnitOfWork.Saves);
    }

    // Plan D14: el token vencido lo refresca otro worker; el envío espera sin llamar a Shopify.
    [Fact]
    public async Task AnExpiredTokenRetriesWithoutCallingShopify()
    {
        var world = new PushWorld(accessToken: null);
        var push = world.Push(ShopifyPushGroup.Note);

        var result = await world.ProcessAsync(push);

        Assert.Equal(ShopifyOrderUpdateKind.Retry, result.Kind);
        Assert.Empty(world.Updater.Updates);
        Assert.Equal(ShopifyOrderPushStatus.Pending, push.Status);
    }

    [Fact]
    public async Task AccessDeniedHoldsThePushWithItsCode()
    {
        var world = new PushWorld();
        world.Updater.Result = new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Failed, "access_denied", null);
        var push = world.Push(ShopifyPushGroup.Note);

        await world.ProcessAsync(push);

        // Owner, 2026-11-14: sin write_orders la corrección no se pierde; queda retenida hasta que el comercio conceda el permiso y
        // alguien la vuelva a mandar.
        Assert.Equal((ShopifyOrderPushStatus.Held, "access_denied"), (push.Status, push.FailureCode));
        Assert.Equal(1, world.Edit.World.UnitOfWork.Saves);
    }

    // Owner, 2026-11-14: la conexión cayó después de encolar; el envío queda retenido, no muerto.
    [Fact]
    public async Task AMissingConnectionHoldsThePush()
    {
        var world = new PushWorld(connected: false);
        var push = world.Push(ShopifyPushGroup.Note);

        await world.ProcessAsync(push);

        Assert.Equal((ShopifyOrderPushStatus.Held, "connection_missing"), (push.Status, push.FailureCode));
        Assert.Empty(world.Updater.Updates);
    }

    [Fact]
    public async Task AMissingOrderFails()
    {
        var world = new PushWorld();
        world.Edit.World.Writer.States.Clear();
        var push = world.Push(ShopifyPushGroup.Note);

        await world.ProcessAsync(push);

        Assert.Equal((ShopifyOrderPushStatus.Failed, "order_not_found"), (push.Status, push.FailureCode));
    }
}

/// <summary>Shopify visto desde el envío: anota cada <c>orderUpdate</c> y responde <see cref="Result"/>.</summary>
internal sealed class FakeOrderUpdater : IShopifyOrderUpdater
{
    public List<(ShopifyConnectionRef Connection, ShopifyOrderUpdate Update)> Updates { get; } = [];

    public ShopifyOrderUpdateResult Result { get; set; } = new(ShopifyOrderUpdateKind.Updated, null, null);

    public Task<ShopifyOrderUpdateResult> UpdateAsync(ShopifyConnectionRef connection, ShopifyOrderUpdate update, CancellationToken cancellationToken)
    {
        Updates.Add((connection, update));
        return Task.FromResult(Result);
    }
}
