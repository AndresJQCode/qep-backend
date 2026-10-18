using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §6.1, §6.4 y §7: el <c>PUT</c> de la tienda, el reintento manual y «Marcar como revisado», con
/// los puertos en memoria. Lo que depende de la base (filtros de la lista, conteos del resumen, el token de concurrencia) va en
/// las pruebas de integración.
/// </summary>
public sealed class ShopifyStoreHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PutAdvisorNotMemberThrows()
    {
        var world = new StoreHandlersWorld();
        world.Members.Active.Clear();

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.PutAsync(world.Command()));

        Assert.Equal("shopify.store.advisor_not_member", error.Code);
        Assert.Equal(0, world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PutBillingNotFoundThrows()
    {
        var world = new StoreHandlersWorld();
        world.Billing.Account = null;

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.PutAsync(world.Command()));

        Assert.Equal("shopify.store.billing_account_not_found", error.Code);
        Assert.Equal(0, world.UnitOfWork.Saves);
    }

    // La cuenta existe en la empresa pero en otra moneda: la tienda guardaría una moneda que la cuenta no tiene.
    [Fact]
    public async Task PutBillingInAnotherCurrencyThrowsNotFound()
    {
        var world = new StoreHandlersWorld();

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.PutAsync(world.Command() with { BillingCurrency = "USD" }));

        Assert.Equal("shopify.store.billing_account_not_found", error.Code);
    }

    [Fact]
    public async Task PutClassificationOtherTenantThrows()
    {
        var world = new StoreHandlersWorld();
        var foreign = Guid.CreateVersion7();
        world.References.Classifications[(Guid.CreateVersion7(), foreign)] = "De otro tenant";

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(
            () => world.PutAsync(world.Command() with { DefaultClassificationId = foreign }));

        Assert.Equal("shopify.store.classification_not_found", error.Code);
        Assert.Equal(0, world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PutVersionMismatchThrows412()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => world.PutAsync(world.Command() with { ExpectedVersion = 7 }));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.False(world.Database.Stores[StoreHandlersWorld.ConnectionId].IsConfigured);
        Assert.Equal(0, world.UnitOfWork.Saves);
    }

    [Fact]
    public void PutEmptyAttributeKeyValidationErrors()
    {
        var world = new StoreHandlersWorld();

        var result = new UpdateShopifyStoreValidator().Validate(world.Command() with { DocumentAttributeKey = "   " });

        Assert.Contains(result.Errors, error => error.PropertyName == "documentAttributeKey");
    }

    // Configure lanza ArgumentException con cualquier valor fuera de forma: el validador tiene que atajar todos, o el PUT da 500.
    [Fact]
    public void PutValidatorCoversEveryShapeTheDomainRejects()
    {
        var world = new StoreHandlersWorld();
        var command = world.Command() with
        {
            AdvisorMembershipId = Guid.Empty,
            BillingCompanyId = Guid.Empty,
            BillingBankName = new string('b', ShopifyBillingAccount.BankNameMaxLength + 1),
            BillingAccountNumber = " ",
            BillingCurrency = "CO1",
            DefaultClassificationId = Guid.Empty,
            DocumentAttributeKey = new string('d', ShopifyStore.AttributeKeyMaxLength + 1),
            DocumentTypeAttributeKey = null,
            DefaultIdentificationType = "RUT",
            ExpectedVersion = 0,
        };

        var result = new UpdateShopifyStoreValidator().Validate(command);

        Assert.Equal(
            [
                "advisorMembershipId", "billingAccountNumber", "billingBankName", "billingCompanyId", "billingCurrency",
                "defaultClassificationId", "defaultIdentificationType", "documentAttributeKey", "documentTypeAttributeKey",
                "expectedVersion",
            ],
            result.Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PutConfiguresTheStoreAndBumpsTheVersion()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();

        var dto = await world.PutAsync(world.Command() with { DefaultClassificationId = StoreHandlersWorld.ClassificationId, DefaultIdentificationType = "nit" });

        var store = world.Database.Stores[StoreHandlersWorld.ConnectionId];
        Assert.True(store.IsConfigured);
        Assert.Equal(2L, store.Version);
        Assert.Equal(StoreHandlersWorld.CallerMembershipId, store.UpdatedBy);
        Assert.Equal("NIT", store.DefaultIdentificationType);
        Assert.Equal(1, world.UnitOfWork.Saves);
        Assert.Equal(
            (2L, true, "Asesora Shopify", "Origen Botánico SAS", "Tienda en línea"),
            (dto.Version, dto.IsConfigured, dto.AdvisorName, dto.BillingCompanyName, dto.ClassificationName));
    }

    // §6.1: la tienda nace en el primer contacto; si ese contacto es el PUT, nace con el dominio de la conexión y la
    // ventana de la conciliación en el instante del enlace, no en «ahora».
    [Fact]
    public async Task PutWithoutStoreCreatesItWithShopDomainAndLinkedAt()
    {
        var world = new StoreHandlersWorld();

        var dto = await world.PutAsync(world.Command());

        var store = world.Database.Stores[StoreHandlersWorld.ConnectionId];
        Assert.Equal(
            ("mi-tienda.myshopify.com", (DateTimeOffset?)StoreHandlersWorld.LinkedAt, StoreHandlersWorld.TenantId, true),
            (store.ShopDomain, store.LastReconciledAt, store.TenantId, store.IsConfigured));
        Assert.Equal(2L, dto.Version);
    }

    [Fact]
    public async Task PutConnectionOfAnotherTenantThrows403()
    {
        var world = new StoreHandlersWorld();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(
            () => world.PutAsync(world.Command() with { ConnectionId = Guid.CreateVersion7() }));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task PutWithoutConnectionManageThrows403()
    {
        var world = new StoreHandlersWorld(ShopifyPermissions.ConnectionRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => world.PutAsync(world.Command()));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task GetCreatesTheEmptyStoreOnFirstRead()
    {
        var world = new StoreHandlersWorld();

        var dto = await world.GetAsync();

        var store = world.Database.Stores[StoreHandlersWorld.ConnectionId];
        Assert.Equal(("mi-tienda.myshopify.com", (DateTimeOffset?)StoreHandlersWorld.LinkedAt), (store.ShopDomain, store.LastReconciledAt));
        Assert.Equal((1L, false, "Documento", "Tipo de documento", "CC"), (dto.Version, dto.IsConfigured, dto.DocumentAttributeKey, dto.DocumentTypeAttributeKey, dto.DefaultIdentificationType));
    }

    [Fact]
    public async Task RetryNotWaitingThrows()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedImportedRow(warningsJson: null);

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.RetryAsync(row.Id));

        Assert.Equal("shopify.order.not_waiting", error.Code);
        Assert.Empty(world.Importer.Calls);
    }

    [Fact]
    public async Task RetryUnknownRowThrows404()
    {
        var world = new StoreHandlersWorld();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => world.RetryAsync(Guid.CreateVersion7()));

        Assert.Equal("shopify.order.not_found", error.Code);
    }

    // §6.4: el reintento va por la ingesta (el mismo camino del webhook y la conciliación), con la conexión que da la ruta del
    // dominio de la tienda y el snapshot que guardó el espejo; llega al importador con el tracker vacío.
    [Fact]
    public async Task RetryWaitingRunsTheIngestionWithTheStoredSnapshot()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedWaitingRow();
        world.Database.PendingAudit.Add(("ajeno", Guid.Empty, Guid.Empty, default, "x", "{}"));

        var dto = await world.RetryAsync(row.Id);

        var call = Assert.Single(world.Importer.Calls);
        Assert.Equal(row.ShopifyOrderId, call.Snapshot.OrderId);
        Assert.Equal("Laura", call.Snapshot.Buyer.FirstName);
        Assert.Equal(StoreHandlersWorld.ConnectionId, Assert.Single(world.Importer.Connections).ConnectionId);
        Assert.Empty(world.Database.Audit);
        Assert.Equal((row.Id, "Waiting", "Mi tienda", "Laura Gómez", "laura@verde.co"), (dto.Id, dto.ImportState, dto.StoreName, dto.CustomerName, dto.CustomerEmail));
    }

    [Fact]
    public async Task RetryWithConnectionNotOperationalThrows422()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedWaitingRow();
        world.Connections.Connections["mi-tienda.myshopify.com"] = world.Connection with { IsOperational = false, AccessToken = null };

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.RetryAsync(row.Id));

        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, error.Code);
        Assert.Empty(world.Importer.Calls);
    }

    // La ruta del dominio apunta a otro tenant (la tienda se reenlazó): la conexión de esta fila ya no recibe nada.
    [Fact]
    public async Task RetryWithShopRelinkedElsewhereThrows422()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedWaitingRow();
        world.Connections.Connections["mi-tienda.myshopify.com"] = world.Connection with { TenantId = Guid.CreateVersion7(), ConnectionId = Guid.CreateVersion7() };

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.RetryAsync(row.Id));

        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, error.Code);
        Assert.Empty(world.Importer.Calls);
    }

    [Fact]
    public async Task ReviewClearsFromWithWarnings()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedImportedRow("""[{"code":"total_difference","detail":null}]""");

        var dto = await world.ReviewAsync(row.Id);

        Assert.Equal(StoreHandlersWorld.Now, row.WarningsReviewedAt);
        Assert.Equal((DateTimeOffset?)StoreHandlersWorld.Now, dto.WarningsReviewedAt);
        Assert.Equal(1, world.UnitOfWork.Saves);
        var warning = Assert.Single(dto.Warnings!.Value.EnumerateArray());
        Assert.Equal("total_difference", warning.GetProperty("code").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, warning.GetProperty("detail").ValueKind);
    }

    [Fact]
    public async Task ReviewWithoutWarningsThrows()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedImportedRow(warningsJson: null);

        var error = await Assert.ThrowsAsync<ShopifyDomainException>(() => world.ReviewAsync(row.Id));

        Assert.Equal(ShopifyErrorCodes.OrderNoWarnings, error.Code);
    }

    [Fact]
    public async Task ReviewOtherTenantRouteThrows403()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedImportedRow("""[{"code":"total_difference","detail":null}]""");

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(
            () => world.ReviewHandler.HandleAsync(new ReviewShopifyOrderWarningsCommand(Guid.CreateVersion7(), row.Id), Ct));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Null(row.WarningsReviewedAt);
    }

    // «hoy» es el día del tenant cortado por TenantCalendar, y el instante viaja en UTC (Npgsql no manda a
    // timestamptz un offset distinto de cero). 23:30 del 18 en Bogotá es el 19 en UTC: el día sigue siendo el 18.
    [Fact]
    public async Task SummaryStartsTodayAtTheTenantMidnightInUtc()
    {
        var world = new StoreHandlersWorld();

        await world.SummaryAsync("America/Bogota", new DateTimeOffset(2026, 10, 19, 4, 30, 0, TimeSpan.Zero));

        var since = world.Mirrors.LastImportedSince!.Value;
        Assert.Equal((new DateTimeOffset(2026, 10, 18, 5, 0, 0, TimeSpan.Zero), TimeSpan.Zero), (since, since.Offset));
    }

    // TenantCalendar.StartOfDayUtc: si las 00:00 ocurren dos veces (fin del horario de verano en La Habana, 1 de noviembre de
    // 2026, de 01:00 a 00:00), el día empieza en la primera.
    [Fact]
    public async Task SummaryStartOfDayFollowsTheDaylightSavingRules()
    {
        var world = new StoreHandlersWorld();
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        await world.SummaryAsync("America/Havana", now);

        var calendar = new TenantCalendar(now, TimeZoneInfo.FindSystemTimeZoneById("America/Havana"));
        Assert.Equal(calendar.StartOfDayUtc(calendar.Today), world.Mirrors.LastImportedSince);
    }

    // El PUT audita shopify.store.updated en la misma transacción, con los nombres de lo que cambió; uno
    // rechazado (412) no deja nada.
    [Fact]
    public async Task PutRecordsStoreUpdatedAuditAndARejectedPutDoesNot()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();

        await world.PutAsync(world.Command());
        await Assert.ThrowsAsync<RequestConcurrencyException>(() => world.PutAsync(world.Command() with { ExpectedVersion = 1 }));

        var entry = Assert.Single(world.Database.Audit);
        Assert.Equal(
            ("shopify.store.updated", StoreHandlersWorld.TenantId, StoreHandlersWorld.SubjectId, AuditActorType.Human, StoreHandlersWorld.ConnectionId.ToString()),
            (entry.Action, entry.TenantId, entry.ActorId, entry.ActorType, entry.ResourceId));
        Assert.Empty(world.Database.PendingAudit);
        // Como tenancy.settings.updated, sólo el arreglo con los nombres de lo que cambió; ningún valor (el número de
        // cuenta no entra a la auditoría). Las claves por defecto no cambiaron y no aparecen.
        Assert.Equal(
            """["advisorMembershipId","billingCompanyId","billingBankName","billingAccountNumber","billingCurrency"]""",
            entry.Fields);
        Assert.DoesNotContain("123-456", entry.Fields, StringComparison.Ordinal);
    }

    // Un payload del espejo que no se lee es un fallo permanente, como en la ingesta: queda en last_error
    // y la fila vuelve, sin 500 y sin llamar al importador.
    [Fact]
    public async Task RetryWithUnreadablePayloadRecordsLastErrorWithout500()
    {
        var world = new StoreHandlersWorld();
        world.SeedStore();
        var row = world.SeedWaitingRow(payload: "[]");

        var dto = await world.RetryAsync(row.Id);

        Assert.Empty(world.Importer.Calls);
        Assert.StartsWith("invalid_payload: ", row.LastError, StringComparison.Ordinal);
        Assert.Equal(row.LastError, dto.LastError);
        Assert.Equal("Waiting", dto.ImportState);
        Assert.Equal(1, world.UnitOfWork.Saves);
    }
}

/// <summary>Una tienda enlazada a un tenant con <c>shopify</c> prendido, el asesor activo, la cuenta de cobro y la clasificación
/// del tenant. Cada prueba cambia una sola cosa.</summary>
internal sealed class StoreHandlersWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 18, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset LinkedAt = new(2026, 10, 10, 9, 30, 0, TimeSpan.Zero);
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    public static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    public static readonly Guid AdvisorId = Guid.Parse("01900000-0000-7000-8000-0000000000c3");
    public static readonly Guid CompanyId = Guid.Parse("01900000-0000-7000-8000-0000000000c4");
    public static readonly Guid ClassificationId = Guid.Parse("01900000-0000-7000-8000-0000000000c5");
    public static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000c6");
    public static readonly Guid CallerMembershipId = Guid.Parse("01900000-0000-7000-8000-0000000000c7");

    private readonly string[] permissions;

    public StoreHandlersWorld(params string[] permissions)
    {
        this.permissions = permissions.Length > 0
            ? permissions
            : [ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage, ShopifyPermissions.OrderRead, ShopifyPermissions.OrderManage];
        Database = new FakeShopifyDatabase();
        UnitOfWork = new FakeUnitOfWork(Database);
        Stores = new FakeStoreRepository(Database);
        Mirrors = new FakeOrderMirrorRepository(Database);
        Members.Active.Add(AdvisorId);
        Members.Names[AdvisorId] = "Asesora Shopify";
        Billing.Account = new ShopifyBillingAccountRef(CompanyId, "Bancolombia", "123-456", "COP");
        References.Companies[(TenantId, CompanyId)] = "Origen Botánico SAS";
        References.Classifications[(TenantId, ClassificationId)] = "Tienda en línea";
        Memberships.Members[(SubjectId, TenantId)] = CallerMembershipId;
        Connections.Connections["mi-tienda.myshopify.com"] = Connection;
        TenantConnections.Items.Add(new ShopifyConnectionSummary(ConnectionId, "Mi tienda", "mi-tienda.myshopify.com", "Active"));
    }

    public ShopifyConnectionRef Connection { get; } =
        new(TenantId, ConnectionId, "954889", "mi-tienda.myshopify.com", true, "shpat_x") { LinkedAt = LinkedAt };

    public FakeShopifyDatabase Database { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public FakeStoreRepository Stores { get; }

    public FakeOrderMirrorRepository Mirrors { get; }

    public NamedMemberDirectory Members { get; } = new();

    public FakeBillingAccounts Billing { get; } = new();

    public FakeStoreReferences References { get; } = new();

    public FakeMembershipDirectory Memberships { get; } = new();

    public FakeConnectionDirectory Connections { get; } = new();

    public FakeTenantConnections TenantConnections { get; } = new();

    public FakeOrderNumbers OrderNumbers { get; } = new();

    public FakeOrderImporter Importer { get; } = new();

    public IExecutionContext Context => new ShopifyTestContext(TenantId, SubjectId, permissions);

    public FakeClock Clock { get; } = new(Now);

    public ShopifyOrderRowMapper Mapper => new(TenantConnections, OrderNumbers);

    public UpdateShopifyStoreCommand Command() => new(
        Connection.TenantId,
        Connection.ConnectionId,
        AdvisorId,
        CompanyId,
        "Bancolombia",
        "123-456",
        "COP",
        null,
        "Documento",
        "Tipo de documento",
        "CC",
        1);

    public Task<ShopifyStoreDto> PutAsync(UpdateShopifyStoreCommand command) =>
        new UpdateShopifyStoreHandler(
                Context, new ShopifyEnabledModules(), Connections, Stores, Members, Billing, References, Memberships, UnitOfWork,
                new FakeAuditRecorder(Database), Clock, new UpdateShopifyStoreValidator())
            .HandleAsync(command, TestContext.Current.CancellationToken);

    public Task<ShopifyStoreDto> GetAsync() =>
        new GetShopifyStoreHandler(Context, new ShopifyEnabledModules(), Connections, Stores, Members, References, UnitOfWork, Clock)
            .HandleAsync(new GetShopifyStoreQuery(TenantId, ConnectionId), TestContext.Current.CancellationToken);

    public Task<ShopifyOrderRowDto> RetryAsync(Guid rowId)
    {
        var ingestion = new ShopifyOrderIngestion(
            Stores, Mirrors, Importer, new NoShopifyOrderChangeReaction(), UnitOfWork, Clock);
        return new RetryShopifyOrderHandler(Context, new ShopifyEnabledModules(), Mirrors, Stores, Connections, ingestion, UnitOfWork, Mapper)
            .HandleAsync(new RetryShopifyOrderCommand(TenantId, rowId), TestContext.Current.CancellationToken);
    }

    public ReviewShopifyOrderWarningsHandler ReviewHandler =>
        new(Context, new ShopifyEnabledModules(), Mirrors, UnitOfWork, Clock, Mapper);

    public Task<ShopifyOrderRowDto> ReviewAsync(Guid rowId) =>
        ReviewHandler.HandleAsync(new ReviewShopifyOrderWarningsCommand(TenantId, rowId), TestContext.Current.CancellationToken);

    public Task<ShopifyOrdersSummaryDto> SummaryAsync(string timeZone, DateTimeOffset now) =>
        new GetShopifyOrdersSummaryHandler(Context, new ShopifyEnabledModules(), Mirrors, new FixedTenantClock(timeZone, now))
            .HandleAsync(new GetShopifyOrdersSummaryQuery(TenantId), TestContext.Current.CancellationToken);

    public ShopifyStore SeedStore()
    {
        var store = ShopifyStore.Create(ConnectionId, TenantId, LinkedAt);
        store.RecordShopDomain("mi-tienda.myshopify.com");
        Database.Stores[ConnectionId] = store;
        return store;
    }

    public ShopifyOrderMirror SeedWaitingRow(string? payload = null)
    {
        var snapshot = ImporterWorld.Order();
        var row = ShopifyOrderMirror.Create(
            TenantId, ConnectionId, snapshot.OrderId, payload ?? ShopifyOrderIngestion.WritePayload(snapshot), snapshot.UpdatedAt, snapshot.ToHeader());
        row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, """{"skus":[null]}""", Now.AddHours(-1));
        Database.Orders[(ConnectionId, snapshot.OrderId)] = row;
        return row;
    }

    public ShopifyOrderMirror SeedImportedRow(string? warningsJson)
    {
        var snapshot = ImporterWorld.Order(orderId: 5_550_000_000_009L);
        var row = ShopifyOrderMirror.Create(
            TenantId, ConnectionId, snapshot.OrderId, ShopifyOrderIngestion.WritePayload(snapshot), snapshot.UpdatedAt, snapshot.ToHeader());
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, warningsJson, Now.AddHours(-1));
        Database.Orders[(ConnectionId, snapshot.OrderId)] = row;
        return row;
    }
}

internal sealed class ShopifyTestContext(Guid tenantId, Guid subjectId, string[] permissions) : IExecutionContext
{
    public Guid SubjectId => subjectId;

    public TenantId TenantId => new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission);
}

internal sealed class ShopifyEnabledModules : ITenantModules
{
    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(TenantModuleKeys.All));
}

/// <summary><see cref="FakeMemberDirectory"/> con nombres: el <c>GET</c> de la tienda muestra el del asesor.</summary>
internal sealed class NamedMemberDirectory : IShopifyMemberDirectory
{
    public HashSet<Guid> Active { get; } = [];

    public Dictionary<Guid, string> Names { get; } = [];

    public Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        Task.FromResult(tenantId == StoreHandlersWorld.TenantId && Active.Contains(membershipId));

    public Task<string?> NameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        Task.FromResult(tenantId == StoreHandlersWorld.TenantId ? Names.GetValueOrDefault(membershipId) : null);
}

internal sealed class FakeStoreReferences : IShopifyStoreReferences
{
    public Dictionary<(Guid TenantId, Guid CompanyId), string> Companies { get; } = [];

    public Dictionary<(Guid TenantId, Guid ClassificationId), string> Classifications { get; } = [];

    public Task<string?> CompanyNameAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.GetValueOrDefault((tenantId, companyId)));

    public Task<string?> ClassificationNameAsync(Guid tenantId, Guid classificationId, CancellationToken cancellationToken) =>
        Task.FromResult(Classifications.GetValueOrDefault((tenantId, classificationId)));
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Members { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Members.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeTenantConnections : IShopifyTenantConnections
{
    public List<ShopifyConnectionSummary> Items { get; } = [];

    public Task<IReadOnlyList<ShopifyConnectionSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShopifyConnectionSummary>>(tenantId == StoreHandlersWorld.TenantId ? [.. Items] : []);
}

internal sealed class FakeOrderNumbers : IShopifyOrderNumbers
{
    public Dictionary<Guid, string> Numbers { get; } = [];

    public List<IReadOnlyCollection<Guid>> Requests { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        Requests.Add([.. orderIds]);
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            orderIds.Where(Numbers.ContainsKey).ToDictionary(id => id, id => Numbers[id]));
    }
}

/// <summary>El calendario de Tenancy con un huso y un instante fijos: el resumen corta el día con él.</summary>
internal sealed class FixedTenantClock(string timeZone, DateTimeOffset now) : ITenantClock
{
    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)));
}
