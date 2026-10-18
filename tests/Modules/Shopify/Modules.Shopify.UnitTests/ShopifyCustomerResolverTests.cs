using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §6.2.1: quién es el comprador en QEP. Se busca en orden (documento, comprador ya visto en el espejo de la
/// tienda, teléfono, correo) y gana el primero; si no aparece, nace completo sólo con documento, clasificación por defecto y
/// ciudad, y si no, incompleto. El cliente nunca detiene la importación, y se anota en la fila antes de escribir el pedido
/// (D-S24).
/// </summary>
public sealed class ShopifyCustomerResolverTests
{
    private static readonly Dictionary<string, string> Document = new(StringComparer.Ordinal)
    {
        ["Documento"] = " 1020304050 ",
        ["Tipo de documento"] = "cc",
    };

    private static readonly ShopifyAddress Medellin =
        new("Laura Gómez", "Cra 43A # 1-50", "Apto 301", "Medellín", "Antioquia", "ANT", "CO", "+573001234567");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchOrderDocumentThenSeenShopifyIdThenPhoneThenEmail()
    {
        var world = new ImporterWorld();
        var seenId = Guid.CreateVersion7();
        SeenBefore(world, shopifyCustomerId: 7_001L, qepCustomerId: seenId);
        var byEmail = new ShopifyCustomerRef(Guid.CreateVersion7(), false, true);
        world.Customers.ByEmail["laura@verde.co"] = byEmail;
        var snapshot = Order(attributes: Document, address: Medellin);
        var row = world.RowFor(snapshot);

        var found = await world.Resolver.FindAsync(world.Store, row, snapshot, Ct);

        Assert.Equal(byEmail, found);
        Assert.Equal(
            ["document:CC:1020304050", $"id:{seenId}", "phone:+573001234567:CO", "email:laura@verde.co"],
            world.Customers.Log);
    }

    [Fact]
    public async Task SearchStopsAtTheFirstMatch()
    {
        var world = new ImporterWorld();
        var byDocument = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        world.Customers.ByDocument[("CC", "1020304050")] = byDocument;
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        var snapshot = Order(attributes: Document);

        var found = await world.Resolver.FindAsync(world.Store, world.RowFor(snapshot), snapshot, Ct);

        Assert.Equal(byDocument, found);
        Assert.Equal(["document:CC:1020304050"], world.Customers.Log);
    }

    [Fact]
    public async Task SeenShopifyIdWinsOverPhoneAndEmail()
    {
        var world = new ImporterWorld();
        var seen = new ShopifyCustomerRef(Guid.CreateVersion7(), false, true);
        world.Customers.ById[seen.Id] = seen;
        SeenBefore(world, shopifyCustomerId: 7_001L, qepCustomerId: seen.Id);
        var snapshot = Order();

        var found = await world.Resolver.FindAsync(world.Store, world.RowFor(snapshot), snapshot, Ct);

        Assert.Equal(seen, found);
        Assert.Equal([$"id:{seen.Id}"], world.Customers.Log);
    }

    // D-S24: el reintento usa el cliente que el intento anterior anotó, antes que cualquier búsqueda.
    [Fact]
    public async Task RetryUsesTheAttachedCustomerFirst()
    {
        var world = new ImporterWorld();
        var attached = new ShopifyCustomerRef(Guid.CreateVersion7(), false, true);
        world.Customers.ById[attached.Id] = attached;
        var snapshot = Order(attributes: Document);
        var row = world.RowFor(snapshot);
        row.AttachCustomer(attached.Id);

        var found = await world.Resolver.FindAsync(world.Store, row, snapshot, Ct);

        Assert.Equal(attached, found);
        Assert.Equal([$"id:{attached.Id}"], world.Customers.Log);
    }

    [Fact]
    public async Task DocumentClassificationCityCreatesComplete()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(classificationId: ClassificationId);
        var city = new ShopifyCityRef(Guid.CreateVersion7(), Guid.CreateVersion7());
        world.Geography.Cities[("ANTIOQUIA", "Medellín")] = city;
        var snapshot = Order(attributes: Document, address: Medellin);

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.True(created.IsComplete);
        Assert.Equal(
            new ShopifyCompleteCustomer(
                "Laura Gómez", "CC", "1020304050", "+573001234567", "laura@verde.co", "Cra 43A # 1-50 Apto 301", "CO",
                city.CityId, null, ClassificationId),
            Assert.Single(world.Customers.Completes));
        Assert.Empty(world.Customers.Incompletes);
    }

    [Fact]
    public async Task UnknownProvinceCodeTriesTheProvinceName()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(classificationId: ClassificationId);
        var city = new ShopifyCityRef(Guid.CreateVersion7(), Guid.CreateVersion7());
        world.Geography.Cities[("Antioquia", "Medellín")] = city;
        var snapshot = Order(attributes: Document, address: Medellin with { ProvinceCode = "XX" });

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.True(created.IsComplete);
        Assert.Equal(city.CityId, Assert.Single(world.Customers.Completes).CityId);
        Assert.Equal([("Antioquia", "Medellín")], world.Geography.Requests);
    }

    [Fact]
    public async Task ColombianAddressWithoutCityCreatesIncomplete()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(classificationId: ClassificationId);
        var snapshot = Order(attributes: Document, address: Medellin);

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.False(created.IsComplete);
        Assert.Empty(world.Customers.Completes);
        Assert.Equal(("Laura Gómez", "+573001234567", "laura@verde.co", "CO"), Assert.Single(world.Customers.Incompletes));
    }

    // §6.2.1 paso 3: fuera de Colombia no hay ciudad DIVIPOLA que resolver; la ciudad va como texto.
    [Fact]
    public async Task ForeignAddressCreatesCompleteWithTheCityName()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(classificationId: ClassificationId);
        var snapshot = Order(attributes: Document, address: new ShopifyAddress(null, "Av. Arequipa 123", null, "Lima", "Lima", "LIM", "pe", null));

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.True(created.IsComplete);
        var sent = Assert.Single(world.Customers.Completes);
        Assert.Equal(("PE", (Guid?)null, "Lima", "Av. Arequipa 123"), (sent.Country, sent.CityId, sent.CityName, sent.Address));
        Assert.Empty(world.Geography.Requests);
    }

    // Un comprador con documento pero sin correo no pasa las reglas del POST de clientes; el alta cae a
    // incompleta y el pedido sigue.
    [Fact]
    public async Task DocumentWithoutEmailFallsBackToIncomplete()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(classificationId: ClassificationId);
        world.Geography.Cities[("ANTIOQUIA", "Medellín")] = new ShopifyCityRef(Guid.CreateVersion7(), Guid.CreateVersion7());
        world.Customers.OnComplete = () => new ShopifyCustomerNotCompletableException("validation.failed", new InvalidOperationException());
        var snapshot = Order(attributes: Document, address: Medellin, buyer: new ShopifyBuyer("Laura", "Gómez", null, "+573001234567"));

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.False(created.IsComplete);
        Assert.Single(world.Customers.Completes);
        Assert.Equal(("Laura Gómez", "+573001234567", (string?)null, "CO"), Assert.Single(world.Customers.Incompletes));
    }

    [Fact]
    public async Task DocumentWithoutClassificationCreatesIncompleteWithDocumentWarning()
    {
        var world = new ImporterWorld();
        world.Geography.Cities[("ANTIOQUIA", "Medellín")] = new ShopifyCityRef(Guid.CreateVersion7(), Guid.CreateVersion7());
        var snapshot = Order(attributes: Document, address: Medellin);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        Assert.Empty(world.Customers.Completes);
        Assert.Single(world.Customers.Incompletes);
        Assert.Equal(
            """[{"code":"customer_incomplete","detail":{"document":{"type":"CC","number":"1020304050"}}}]""",
            row.Warnings);
    }

    [Fact]
    public async Task FoundIncompleteCustomerWarnsWithoutDocument()
    {
        var world = new ImporterWorld();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), false, true);
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal("""[{"code":"customer_incomplete","detail":null}]""", row.Warnings);
        Assert.Empty(world.Customers.Incompletes);
    }

    [Fact]
    public async Task FoundCompleteCustomerHasNoWarnings()
    {
        var world = new ImporterWorld();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Null(row.Warnings);
    }

    // Shopify acepta 255 + 255 en el nombre y Customers guarda 160 (Customer.NameMaxLength). Sin recortar
    // acá, el POST de clientes lo rechaza por validación y un comprador con todo para ficha completa nace incompleto.
    [Fact]
    public async Task LongNameIsCutToTheCustomerColumnAndStillCreatesComplete()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(classificationId: ClassificationId);
        world.Geography.Cities[("ANTIOQUIA", "Medellín")] = new ShopifyCityRef(Guid.CreateVersion7(), Guid.CreateVersion7());
        var buyer = new ShopifyBuyer(new string('A', 150), new string('B', 150), "laura@verde.co", "+573001234567");
        var snapshot = Order(attributes: Document, address: Medellin, buyer: buyer);

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.True(created.IsComplete);
        Assert.Equal(new string('A', 150) + " " + new string('B', 9), Assert.Single(world.Customers.Completes).Name);
    }

    [Fact]
    public async Task LongNameIsCutForAnIncompleteToo()
    {
        var world = new ImporterWorld();
        var snapshot = Order(buyer: new ShopifyBuyer(new string('A', 160), "Gómez", null, null));

        await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.Equal(new string('A', 160), Assert.Single(world.Customers.Incompletes).Name);
    }

    [Fact]
    public async Task NoNameIncompleteNamedClienteDeShopify()
    {
        var world = new ImporterWorld();
        var snapshot = Order(buyer: new ShopifyBuyer(null, "  ", null, null), address: null);

        var created = await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.False(created.IsComplete);
        Assert.Equal(("Cliente de Shopify", (string?)null, (string?)null, (string?)null), Assert.Single(world.Customers.Incompletes));
    }

    // Sin nombre del comprador, el de la dirección de envío (§6.2.1 paso 3).
    [Fact]
    public async Task BuyerWithoutNameTakesTheAddressName()
    {
        var world = new ImporterWorld();
        var snapshot = Order(buyer: new ShopifyBuyer(null, null, null, null), address: Medellin);

        await world.Resolver.CreateAsync(world.Store, snapshot, Ct);

        Assert.Equal(("Laura Gómez", "+573001234567", (string?)null, "CO"), Assert.Single(world.Customers.Incompletes));
    }

    [Fact]
    public async Task UnknownDocumentTypeUsesStoreDefault()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(defaultIdentificationType: "NIT");
        var snapshot = Order(attributes: new Dictionary<string, string> { ["Documento"] = "900123456", ["Tipo de documento"] = "RUT" });

        await world.Resolver.FindAsync(world.Store, world.RowFor(snapshot), snapshot, Ct);

        Assert.Equal("document:NIT:900123456", world.Customers.Log[0]);
    }

    [Fact]
    public async Task DocumentReadsTheStoreAttributeKeys()
    {
        var world = new ImporterWorld();
        world.Store = ConfiguredStore(documentKey: "Cédula", documentTypeKey: "Tipo");
        var snapshot = Order(attributes: new Dictionary<string, string> { ["Cédula"] = "79123456", ["Tipo"] = "Pasaporte", ["Documento"] = "1" });

        await world.Resolver.FindAsync(world.Store, world.RowFor(snapshot), snapshot, Ct);

        Assert.Equal("document:PASAPORTE:79123456", world.Customers.Log[0]);
    }

    [Fact]
    public async Task DocumentLongerThanItsColumnIsNotADocument()
    {
        var world = new ImporterWorld();
        var snapshot = Order(attributes: new Dictionary<string, string> { ["Documento"] = new string('9', 33) });

        await world.Resolver.FindAsync(world.Store, world.RowFor(snapshot), snapshot, Ct);

        Assert.DoesNotContain(world.Customers.Log, entry => entry.StartsWith("document:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomerAttachedBeforeOrderWrite()
    {
        var world = new ImporterWorld();
        world.Writer.OnCreate = () => new InvalidOperationException("Quotations is down.");
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.ImportAsync(row, snapshot));

        var created = Assert.Single(world.Customers.ById.Keys);
        Assert.Equal(created, row.QepCustomerId);
        var (draft, savesSoFar) = Assert.Single(world.Writer.Creates);
        Assert.Equal(created, draft.ClientId);
        // El anotado se guardó en su propia transacción antes de llamar al escritor: aunque el pedido falle, el reintento lo ve.
        Assert.Equal(1, savesSoFar);
        Assert.Equal(1, world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task FoundCustomerAlreadyAttachedIsNotSavedAgain()
    {
        var world = new ImporterWorld();
        var attached = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        world.Customers.ById[attached.Id] = attached;
        var snapshot = Order();
        var row = world.RowFor(snapshot);
        row.AttachCustomer(attached.Id);

        await world.ImportAsync(row, snapshot);

        Assert.Equal(0, Assert.Single(world.Writer.Creates).SavesSoFar);
    }

    [Theory]
    [InlineData("nit", "NIT")]
    [InlineData(" CC ", "CC")]
    [InlineData("ce", "CE")]
    [InlineData("Pasaporte", "PASAPORTE")]
    [InlineData("RUT", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DocumentTypesNormalize(string? value, string? expected) =>
        Assert.Equal(expected, ShopifyDocumentTypes.Normalize(value));

    private static void SeenBefore(ImporterWorld world, long shopifyCustomerId, Guid qepCustomerId)
    {
        var previous = Order(orderId: 5_550_000_000_000L, customerId: shopifyCustomerId);
        var row = world.RowFor(previous);
        row.AttachCustomer(qepCustomerId);
    }

    private static ShopifyStore ConfiguredStore(
        Guid? classificationId = null,
        string documentKey = ShopifyStore.DefaultDocumentAttributeKey,
        string documentTypeKey = ShopifyStore.DefaultDocumentTypeAttributeKey,
        string defaultIdentificationType = ShopifyStore.DefaultIdentificationTypeCode)
    {
        var store = ShopifyStore.Create(ConnectionId, TenantId, Now.AddDays(-1));
        store.Configure(
            AdvisorId,
            new ShopifyBillingAccount(CompanyId, "Bancolombia", "123-456", "COP"),
            classificationId,
            documentKey,
            documentTypeKey,
            defaultIdentificationType,
            AdvisorId,
            Now.AddDays(-1));
        return store;
    }
}
