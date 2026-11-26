using Modules.Tenancy.Application;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>El tenant de un mayorista con todos los puertos de distribuidores falsos y los handlers armados sobre ellos.</summary>
internal sealed class DistributorWorld
{
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    public DistributorWorld(params string[] permissions)
    {
        Context = new FakeExecutionContext(
            Tenant,
            permissions.Length == 0
                ? [WholesalePermissions.DistributorRead, WholesalePermissions.DistributorManage]
                : permissions);
        PriceList = PriceLists.Seed(Tenant, "Mayoristas", -15m);
        Advisor = Members.AddActive("Ana Mayorista");
        Company = Companies.Add("Mayorista SAS");
        MedellinId = Cities.Add("Medellín");
    }

    /// <summary>La ciudad colombiana que existe en la geografía falsa.</summary>
    public Guid MedellinId { get; }

    public FakeCityLookup Cities { get; } = new();

    public Guid Tenant { get; } = Guid.CreateVersion7();

    public FakeExecutionContext Context { get; set; }

    public FakeClock Clock { get; } = new(Now);

    public FakeWholesalePriceLists PriceLists { get; } = new();

    public FakeDistributorRepository Distributors { get; } = new();

    public FakeMemberDirectory Members { get; } = new();

    public FakeCompanyLookup Companies { get; } = new();

    public FakeTenantNames TenantNames { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakeAuditPublisher Audit { get; } = new();

    public FakeEventPublisher Events { get; } = new();

    /// <summary>Tenancy para el país del tenant (spec 2026-11-18: el teléfono se normaliza con él).</summary>
    public FakeTenancy Tenancy { get; } = new();

    public FakePhoneNormalizer Phones { get; } = new();

    public FakeWhatsAppConnections WhatsApp { get; } = new();

    public FakeMembershipDirectory Memberships { get; } = new();

    public FakeInvitationLinks Links { get; } = new();

    public WholesalePriceList PriceList { get; }

    public Guid Advisor { get; }

    public WholesaleCompanyRef Company { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DistributorComposer Composer() => new(Context, Clock, PriceLists, Members, Companies, Cities, TenantNames, WhatsApp);

    public InviteDistributorHandler InviteHandler() => new(
        Context, Clock, PriceLists, Distributors, Members, Companies, Cities, Tenancy, Phones, WhatsApp, Memberships, UnitOfWork,
        Audit, Events, Links, Composer(), new InviteDistributorValidator());

    public RotateInvitationLinkHandler RotateHandler() => new(Context, Clock, Distributors, UnitOfWork, Audit, Links, Composer());

    public UpdateDistributorHandler UpdateHandler() => new(
        Context, Clock, PriceLists, Distributors, Members, Companies, Cities, Tenancy, Phones, WhatsApp, UnitOfWork, Audit,
        Composer(), new UpdateDistributorValidator());

    public TransitionDistributorHandler TransitionHandler() =>
        new(Context, Clock, Distributors, WhatsApp, Memberships, UnitOfWork, Audit, Events, Composer(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TransitionDistributorHandler>.Instance);

    public DeleteDistributorHandler DeleteHandler() => new(Context, Clock, Distributors, UnitOfWork, Audit);

    public GetDistributorHandler GetHandler() => new(Context, Distributors, Composer());

    public ListDistributorsHandler ListHandler() => new(Context, Distributors, Composer(), new ListDistributorsValidator());

    public GetDistributorOptionsHandler OptionsHandler() => new(Context, PriceLists, Members, Companies, WhatsApp, Tenancy);

    public InviteDistributorCommand InviteCommand(
        string identification = "900123456",
        string email = "ana@distri.co",
        string country = "CO",
        string identificationType = "NIT") =>
        new(Tenant, email, "Distribuciones Norte SAS", country, identificationType, identification, "Ana Pérez", PriceList.Id.Value, Advisor, Company.Id, "trace-1")
        {
            // La ciudad que el país exige, para que los casos que no hablan de ella sigan pasando.
            CityId = country == "CO" ? MedellinId : null,
            CityName = country == "CO" ? null : "Madrid",
            Address = "Calle 10 # 40-20",
        };

    /// <summary>Un distribuidor sembrado sin pasar por el handler, con un hash de token conocido.</summary>
    public Distributor SeedInvited(
        string identification = "900123456",
        string? tokenHash = null,
        DateTimeOffset? at = null,
        string? phone = null,
        Guid? whatsAppConnectionId = null,
        string identificationType = "NIT",
        string country = "CO",
        Guid? cityId = null,
        string? cityName = null,
        string? address = null,
        string? emailProofHash = null)
    {
        var distributor = Distributor.Invite(
            Tenant,
            "ana@distri.co",
            "Distribuciones " + identification,
            country,
            identificationType,
            identification,
            "Ana Pérez",
            PriceList.Id,
            Advisor,
            Company.Id,
            Context.SubjectId,
            tokenHash ?? InvitationTokens.HashOf(InvitationTokens.Generate()),
            Guid.CreateVersion7(),
            phone,
            whatsAppConnectionId,
            at ?? Now,
            cityId,
            cityName,
            address,
            emailProofHash);
        return Distributors.Seed(distributor);
    }

    public Distributor SeedActive(string identification = "900123456", Guid? distributorTenant = null)
    {
        var distributor = SeedInvited(identification);
        distributor.Accept(Guid.CreateVersion7(), distributorTenant ?? Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));
        return distributor;
    }
}
