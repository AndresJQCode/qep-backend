using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Los puertos del importador (§6.2) vistos desde Shopify: anotan lo que se les pidió y devuelven lo que la prueba
/// sembró. Ninguno decide nada de lo que se está probando.</summary>
internal sealed class FakeMemberDirectory : IShopifyMemberDirectory
{
    public HashSet<Guid> Active { get; } = [];

    public int Calls { get; private set; }

    public Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Active.Contains(membershipId));
    }

    public Task<string?> NameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

internal sealed class FakeBillingAccounts : IShopifyBillingAccounts
{
    public ShopifyBillingAccountRef? Account { get; set; }

    public int Calls { get; private set; }

    public Task<ShopifyBillingAccountRef?> FindAsync(
        Guid tenantId, Guid companyId, string bank, string number, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Account is { } account && account.CompanyId == companyId ? account : null);
    }
}

/// <summary><see cref="Log"/> es el orden de las búsquedas: <c>id:</c>, <c>document:</c>, <c>phone:</c>, <c>email:</c>.</summary>
internal sealed class FakeCustomerDirectory : IShopifyCustomerDirectory
{
    public List<string> Log { get; } = [];

    public Dictionary<Guid, ShopifyCustomerRef> ById { get; } = [];

    public Dictionary<(string Type, string Number), ShopifyCustomerRef> ByDocument { get; } = [];

    public Dictionary<string, ShopifyCustomerRef> ByPhone { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ShopifyCustomerRef> ByEmail { get; } = new(StringComparer.Ordinal);

    public List<ShopifyCompleteCustomer> Completes { get; } = [];

    public List<(string? Name, string? Phone, string? Email, string? Country)> Incompletes { get; } = [];

    /// <summary>Lo que lanza el alta completa (p. ej. <see cref="ShopifyCustomerNotCompletableException"/>).</summary>
    public Func<Exception>? OnComplete { get; set; }

    public Task<ShopifyCustomerRef?> FindByDocumentAsync(Guid tenantId, string type, string number, CancellationToken cancellationToken)
    {
        Log.Add($"document:{type}:{number}");
        return Task.FromResult(ByDocument.GetValueOrDefault((type, number)));
    }

    public Task<ShopifyCustomerRef?> FindByPhoneAsync(Guid tenantId, string phone, string? country, CancellationToken cancellationToken)
    {
        Log.Add($"phone:{phone}:{country}");
        return Task.FromResult(ByPhone.GetValueOrDefault(phone));
    }

    public Task<ShopifyCustomerRef?> FindByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)
    {
        Log.Add($"email:{email}");
        return Task.FromResult(ByEmail.GetValueOrDefault(email));
    }

    public Task<ShopifyCustomerRef?> FindByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        Log.Add($"id:{id}");
        return Task.FromResult(ById.GetValueOrDefault(id));
    }

    public Task<ShopifyCustomerRef> CreateCompleteAsync(Guid tenantId, ShopifyCompleteCustomer data, CancellationToken cancellationToken)
    {
        Completes.Add(data);
        if (OnComplete is { } fail)
        {
            throw fail();
        }

        // Como Customers: el alta nueva nace en la versión 1 y dice que la creó (SC-05), que es la huella del cliente (S7).
        var created = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true, Created: true, Version: 1);
        ById[created.Id] = created;
        return Task.FromResult(created);
    }

    public Task<ShopifyCustomerRef> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken)
    {
        Incompletes.Add((name, phone, email, country));
        var created = new ShopifyCustomerRef(Guid.CreateVersion7(), false, true, Created: true, Version: 1);
        ById[created.Id] = created;
        return Task.FromResult(created);
    }
}

internal sealed class FakeProductLookup : IShopifyProductLookup
{
    public Dictionary<string, ShopifyProductRef> Products { get; } = new(StringComparer.Ordinal);

    public List<IReadOnlyCollection<string>> Requests { get; } = [];

    public FakeProductLookup With(string sku, int taxPercentage = 19, bool active = true)
    {
        // Dos ids distintos a propósito: un intercambio de VariantId y ProductId en el importador tiene que verse.
        Products[sku] = new ShopifyProductRef(Guid.CreateVersion7(), Guid.CreateVersion7(), sku, active, taxPercentage);
        return this;
    }

    public Task<IReadOnlyDictionary<string, ShopifyProductRef>> FindByCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        Requests.Add([.. skus]);
        return Task.FromResult<IReadOnlyDictionary<string, ShopifyProductRef>>(
            skus.Where(Products.ContainsKey).Distinct(StringComparer.Ordinal).ToDictionary(sku => sku, sku => Products[sku], StringComparer.Ordinal));
    }
}

/// <summary>Ciudades por <c>(departamento, ciudad)</c> exactos: la normalización de tildes y mayúsculas es de Geography y se
/// prueba contra la semilla (<c>ColombiaProvinceCodesGeographyTests</c>). <see cref="Departments"/> son los que resuelven aunque
/// la ciudad no case (D-C11); el de una ciudad sembrada en <see cref="Cities"/> resuelve siempre.</summary>
internal sealed class FakeGeographyLookup : IShopifyGeographyLookup
{
    public Dictionary<(string Department, string City), ShopifyCityRef> Cities { get; } = [];

    public Dictionary<string, Guid> Departments { get; } = new(StringComparer.Ordinal);

    public List<(string Department, string City)> Requests { get; } = [];

    public Task<ShopifyCityLookup> LookupCityAsync(string departmentName, string cityName, CancellationToken cancellationToken)
    {
        Requests.Add((departmentName, cityName));
        var city = Cities.GetValueOrDefault((departmentName, cityName));
        Guid? department = city?.DepartmentId
            ?? (Departments.TryGetValue(departmentName, out var id) ? id : null);
        return Task.FromResult(new ShopifyCityLookup(department, city));
    }
}

/// <summary>La bandeja de Geography vista desde Shopify: anota cada reporte y responde <see cref="Outcome"/>; <see cref="OnReport"/>
/// puede lanzar, como Geography caída.</summary>
internal sealed class FakeUnresolvedCityReporter : IShopifyUnresolvedCityReporter
{
    public List<ShopifyUnresolvedCityReport> Reports { get; } = [];

    public ShopifyUnresolvedCityResult Outcome { get; set; } = new(ShopifyUnresolvedCityOutcome.Recorded, null);

    public Func<Exception>? OnReport { get; set; }

    public Task<ShopifyUnresolvedCityResult> ReportAsync(ShopifyUnresolvedCityReport report, CancellationToken cancellationToken)
    {
        Reports.Add(report);
        if (OnReport is { } fail)
        {
            throw fail();
        }

        return Task.FromResult(Outcome);
    }
}

/// <summary>Las huellas de los sinónimos (§4.6) en la «base»: lo agregado queda pendiente hasta el guardado de
/// <see cref="FakeUnitOfWork"/>, y la búsqueda ve las dos cosas, como el tracker de EF.</summary>
internal sealed class FakeCityCorrectionRepository(FakeShopifyDatabase database) : IShopifyCityCorrectionRepository
{
    public Task<ShopifyCityCorrection?> FindAsync(
        Guid tenantId, Guid synonymId, ShopifyCityCorrectionTarget target, Guid targetId, CancellationToken cancellationToken) =>
        Task.FromResult(database.CityCorrections.Concat(database.PendingCityCorrections).FirstOrDefault(correction =>
            correction.TenantId == tenantId && correction.SynonymId == synonymId && correction.Target == target
            && correction.TargetId == targetId));

    public Task<ShopifyCityCorrection?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(database.CityCorrections.Concat(database.PendingCityCorrections).FirstOrDefault(correction => correction.Id == id));

    public void Add(ShopifyCityCorrection correction) => database.PendingCityCorrections.Add(correction);

    // Como la consulta de EF: sólo lo guardado, de todos los tenants, en orden de id.
    public Task<IReadOnlyList<ShopifyCityCorrection>> ListBySynonymAsync(Guid synonymId, CancellationToken cancellationToken)
    {
        database.Log.Add($"list:{synonymId}");
        return Task.FromResult<IReadOnlyList<ShopifyCityCorrection>>(
            [.. database.CityCorrections.Where(correction => correction.SynonymId == synonymId).OrderBy(correction => correction.Id)]);
    }
}

/// <summary>Los trabajos de corrección (spec 2026-10-26 D-C6) en la «base»: lo agregado queda pendiente hasta el guardado de
/// <see cref="FakeUnitOfWork"/>.</summary>
internal sealed class FakeCityCorrectionJobRepository(FakeShopifyDatabase database) : IShopifyCityCorrectionJobRepository
{
    public Task<ShopifyCityCorrectionJob?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(database.CityCorrectionJobs.Concat(database.PendingCityCorrectionJobs).FirstOrDefault(job => job.Id == id));

    public void Add(ShopifyCityCorrectionJob job) => database.PendingCityCorrectionJobs.Add(job);

    public Task<bool> LockIfPendingAsync(Guid id, CancellationToken cancellationToken)
    {
        database.Log.Add($"job-lock:{id}");
        return Task.FromResult(
            !database.CityCorrectionJobsFinishedElsewhere.Contains(id)
            && database.CityCorrectionJobs.Concat(database.PendingCityCorrectionJobs)
                .Any(job => job.Id == id && job.Status == ShopifyCityCorrectionJobStatus.Pending));
    }
}

/// <summary>Transportadoras por título exacto y la tasa del envío del tenant; anota qué se le preguntó. La normalización del
/// título es del adaptador (<c>ShopifyAdaptersTests</c>).</summary>
internal sealed class FakeShippingLookup : IShopifyShippingLookup
{
    public Dictionary<string, ShopifyCarrierRef> Carriers { get; } = new(StringComparer.Ordinal);

    public int? FreightTaxPercentage { get; set; } = 19;

    public List<string> CarrierRequests { get; } = [];

    public int RateRequests { get; private set; }

    public Task<ShopifyCarrierRef?> MatchCarrierAsync(Guid tenantId, string title, CancellationToken cancellationToken)
    {
        CarrierRequests.Add(title);
        return Task.FromResult(Carriers.GetValueOrDefault(title));
    }

    public Task<int?> GetFreightTaxPercentageAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        RateRequests++;
        return Task.FromResult(FreightTaxPercentage);
    }
}

/// <summary>Anota cada borrador con lo que la fila tenía al llamarlo y cuántos guardados había. <see cref="Result"/> decide la
/// respuesta; <see cref="OnCreate"/> puede lanzar, como un fallo transitorio de Quotations.</summary>
internal sealed class FakeOrderWriter(FakeUnitOfWork unitOfWork) : IShopifyOrderWriter
{
    public List<(ShopifyOrderDraft Draft, int SavesSoFar)> Creates { get; } = [];

    public List<(Guid TenantId, Guid OrderId, string FinancialStatus)> Syncs { get; } = [];

    /// <summary>El orden de las llamadas: <c>create</c> y <c>sync:{estado}</c>.</summary>
    public List<string> Log { get; } = [];

    public ShopifyOrderOutcome SyncOutcome { get; set; } = ShopifyOrderOutcome.Applied;

    public Func<ShopifyOrderDraft, ShopifyOrderWriteResult> Result { get; set; } =
        draft => ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0001", 119_000m, alreadyExisted: false, draft.ClientId);

    public Func<Exception>? OnCreate { get; set; }

    public Task<ShopifyOrderWriteResult> CreateAsync(ShopifyOrderDraft draft, CancellationToken cancellationToken)
    {
        Creates.Add((draft, unitOfWork.Saves));
        Log.Add("create");
        if (OnCreate is { } fail)
        {
            throw fail();
        }

        return Task.FromResult(Result(draft));
    }

    public Task<ShopifyOrderOutcome> SyncPaymentStatusAsync(
        Guid tenantId, Guid orderId, string financialStatus, CancellationToken cancellationToken)
    {
        Syncs.Add((tenantId, orderId, financialStatus));
        Log.Add($"sync:{financialStatus}");
        if (SyncOutcome.Kind == ShopifyOrderOutcomeKind.Applied && PaymentStatus.GetValueOrDefault(orderId) != financialStatus)
        {
            PaymentStatus[orderId] = financialStatus;
            EffectiveSyncs++;
        }

        return Task.FromResult(SyncOutcome);
    }

    /// <summary>Lo que Quotations tiene anulado: anular dos veces el mismo pedido da <c>AlreadyCancelled</c>, como el adaptador
    /// real (<c>order.order.already_cancelled</c>).</summary>
    public HashSet<Guid> CancelledOrders { get; } = [];

    /// <summary>El estado de pago de cada pedido en Quotations: sincronizar con el mismo no cambia nada (D-S26), y
    /// <see cref="EffectiveSyncs"/> cuenta sólo los que sí cambiaron.</summary>
    public Dictionary<Guid, string> PaymentStatus { get; } = [];

    public int EffectiveSyncs { get; private set; }

    public List<(Guid TenantId, Guid OrderId, Guid By, string Reason)> Cancels { get; } = [];

    /// <summary>Fuerza la respuesta de la anulación (<c>AlreadyInvoiced</c>, <c>Ignored</c>).</summary>
    public ShopifyOrderOutcome? CancelOutcome { get; set; }

    /// <summary>Los pedidos de Quotations por clave externa (D-S23).</summary>
    public Dictionary<string, ShopifyOrderWriteResult> ByExternalKey { get; } = new(StringComparer.Ordinal);

    public List<(Guid TenantId, string ExternalKey)> KeyLookups { get; } = [];

    public Task<ShopifyOrderOutcome> CancelAsync(
        Guid tenantId, Guid orderId, Guid byMembershipId, string reason, CancellationToken cancellationToken)
    {
        Cancels.Add((tenantId, orderId, byMembershipId, reason));
        Log.Add("cancel");
        if (CancelOutcome is { } forced)
        {
            return Task.FromResult(forced);
        }

        return Task.FromResult(CancelledOrders.Add(orderId) ? ShopifyOrderOutcome.Applied : ShopifyOrderOutcome.AlreadyCancelled);
    }

    public Task<ShopifyOrderWriteResult?> FindByExternalKeyAsync(Guid tenantId, string externalKey, CancellationToken cancellationToken)
    {
        KeyLookups.Add((tenantId, externalKey));
        return Task.FromResult(ByExternalKey.GetValueOrDefault(externalKey));
    }
}

/// <summary>Una tienda lista para importar y el pedido de Shopify más simple que entra: lo que cada prueba cambia es una sola
/// cosa. Los ids son fijos para que los detalles JSON se puedan comparar como texto.</summary>
internal sealed class ImporterWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 18, 0, 0, TimeSpan.Zero);
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    public static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000b2");
    public static readonly Guid AdvisorId = Guid.Parse("01900000-0000-7000-8000-0000000000b3");
    public static readonly Guid CompanyId = Guid.Parse("01900000-0000-7000-8000-0000000000b4");
    public static readonly Guid ClassificationId = Guid.Parse("01900000-0000-7000-8000-0000000000b5");
    public const string ShopId = "954889";

    public ImporterWorld()
    {
        Database = new FakeShopifyDatabase();
        UnitOfWork = new FakeUnitOfWork(Database);
        Mirrors = new FakeOrderMirrorRepository(Database);
        CityCorrections = new FakeCityCorrectionRepository(Database);
        CityCorrectionJobs = new FakeCityCorrectionJobRepository(Database);
        Writer = new FakeOrderWriter(UnitOfWork);
        Members.Active.Add(AdvisorId);
        Billing.Account = new ShopifyBillingAccountRef(CompanyId, "Bancolombia", "123-456", "COP");
        Products.With("CAM-001");
        Store = ShopifyStore.Create(ConnectionId, TenantId, Now.AddDays(-1));
        Store.Configure(
            AdvisorId,
            new ShopifyBillingAccount(CompanyId, "Bancolombia", "123-456", "COP"),
            null,
            ShopifyStore.DefaultDocumentAttributeKey,
            ShopifyStore.DefaultDocumentTypeAttributeKey,
            ShopifyStore.DefaultIdentificationTypeCode,
            AdvisorId,
            Now.AddDays(-1));
    }

    public FakeShopifyDatabase Database { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public FakeOrderMirrorRepository Mirrors { get; }

    public FakeMemberDirectory Members { get; } = new();

    public FakeBillingAccounts Billing { get; } = new();

    public FakeCustomerDirectory Customers { get; } = new();

    public FakeProductLookup Products { get; } = new();

    public FakeGeographyLookup Geography { get; } = new();

    public FakeShippingLookup Shipping { get; } = new();

    public FakeCityCorrectionRepository CityCorrections { get; }

    public FakeCityCorrectionJobRepository CityCorrectionJobs { get; }

    public FakeUnresolvedCityReporter UnresolvedCities { get; } = new();

    /// <summary>Lo que el importador relee bajo el candado del sinónimo; sin sinónimos, nada que recorregir.</summary>
    public FakeCitySynonymLookup Synonyms { get; } = new();

    public FakeOrderWriter Writer { get; }

    public ShopifyStore Store { get; set; }

    public ShopifyConnectionRef Connection { get; } =
        new(TenantId, ConnectionId, ShopId, "mi-tienda.myshopify.com", true, "shpat_x");

    public ShopifyCustomerResolver Resolver => new(Customers, Mirrors, Geography);

    public ShopifyOrderImporter Importer =>
        new(
            Members, Billing, Resolver, Products, Shipping, Writer, new ShopifyOrderChanges(Writer, Customers, new FakeClock(Now)),
            CityCorrections, CityCorrectionJobs, UnresolvedCities, Synonyms, UnitOfWork, new FakeClock(Now));

    /// <summary>La tienda con clasificación por defecto: con documento y dirección, el comprador puede nacer completo.</summary>
    public ImporterWorld WithClassification()
    {
        Store.Configure(
            AdvisorId,
            new ShopifyBillingAccount(CompanyId, "Bancolombia", "123-456", "COP"),
            ClassificationId,
            ShopifyStore.DefaultDocumentAttributeKey,
            ShopifyStore.DefaultDocumentTypeAttributeKey,
            ShopifyStore.DefaultIdentificationTypeCode,
            AdvisorId,
            Now.AddDays(-1));
        return this;
    }

    /// <summary>La fila en espera, ya guardada, para un snapshot.</summary>
    public ShopifyOrderMirror RowFor(ShopifyOrderSnapshot snapshot)
    {
        var row = ShopifyOrderMirror.Create(TenantId, ConnectionId, snapshot.OrderId, "{}", snapshot.UpdatedAt, snapshot.ToHeader());
        Database.Orders[(ConnectionId, snapshot.OrderId)] = row;
        return row;
    }

    public Task<ShopifyImportOutcome> ImportAsync(ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot) =>
        Importer.ImportAsync(Connection, Store, row, snapshot, TestContext.Current.CancellationToken);

    public static ShopifyOrderSnapshot Order(
        IReadOnlyList<ShopifyLine>? lines = null,
        ShopifyBuyer? buyer = null,
        ShopifyAddress? address = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        string currency = "COP",
        bool test = false,
        DateTimeOffset? cancelledAt = null,
        long? customerId = 7_001L,
        long orderId = 5_550_000_000_001L,
        string financialStatus = "pending",
        bool taxesIncluded = true,
        string? note = "Dejar en portería",
        IReadOnlyList<string>? gateways = null,
        IReadOnlyList<ShopifyShippingLine>? shippingLines = null,
        decimal totalPrice = 119_000m,
        string? cancelReason = "customer") =>
        new(
            orderId,
            "#1001",
            customerId,
            Now.AddMinutes(-30),
            Now.AddMinutes(-20),
            cancelledAt,
            cancelledAt is null ? null : cancelReason,
            financialStatus,
            currency,
            totalPrice,
            taxesIncluded,
            test,
            note,
            gateways ?? ["manual"],
            buyer ?? new ShopifyBuyer("Laura", "Gómez", "laura@verde.co", "+573001234567"),
            address,
            attributes ?? new Dictionary<string, string>(),
            lines ?? [new ShopifyLine("CAM-001", 1m, 119_000m, 0m)],
            shippingLines ?? []);
}
