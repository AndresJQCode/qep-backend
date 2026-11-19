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
        PriceList = PriceLists.Seed(Domain.PriceList.Create(Tenant, "Mayoristas", 15m, PriceListVisibility.AllActive, [], Now));
        Advisor = Members.AddActive("Ana Mayorista");
        Company = Companies.Add("Mayorista SAS");
    }

    public Guid Tenant { get; } = Guid.CreateVersion7();

    public FakeExecutionContext Context { get; set; }

    public FakeClock Clock { get; } = new(Now);

    public FakePriceListRepository PriceLists { get; } = new();

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

    public PriceList PriceList { get; }

    public Guid Advisor { get; }

    public WholesaleCompanyRef Company { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DistributorComposer Composer() => new(Context, Clock, PriceLists, Members, Companies, TenantNames, WhatsApp);

    public InviteDistributorHandler InviteHandler() => new(
        Context, Clock, PriceLists, Distributors, Members, Companies, Tenancy, Phones, WhatsApp, Memberships, UnitOfWork, Audit,
        Events, Composer(), new InviteDistributorValidator());

    public UpdateDistributorHandler UpdateHandler() => new(
        Context, Clock, PriceLists, Distributors, Members, Companies, Tenancy, Phones, WhatsApp, UnitOfWork, Audit, Composer(),
        new UpdateDistributorValidator());

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
        new(Tenant, email, "Distribuciones Norte SAS", country, identificationType, identification, "Ana Pérez", PriceList.Id.Value, Advisor, Company.Id, "trace-1");

    /// <summary>Un distribuidor sembrado sin pasar por el handler, con un hash de token conocido.</summary>
    public Distributor SeedInvited(
        string identification = "900123456",
        string? tokenHash = null,
        DateTimeOffset? at = null,
        string? phone = null,
        Guid? whatsAppConnectionId = null,
        string identificationType = "NIT",
        string country = "CO")
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
            at ?? Now);
        return Distributors.Seed(distributor);
    }

    public Distributor SeedActive(string identification = "900123456", Guid? distributorTenant = null)
    {
        var distributor = SeedInvited(identification);
        distributor.Accept(Guid.CreateVersion7(), distributorTenant ?? Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));
        return distributor;
    }
}
