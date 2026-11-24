using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Companies.UnitTests;

/// <summary>
/// Spec 2026-11-17 §5.3 (R6): crear una empresa colombiana que cobra IVA, o pasar una a «cobra IVA» en Colombia, pide las
/// tasas por defecto después de guardar. Un fallo de la siembra no tumba la respuesta: la empresa es lo que el usuario pidió.
/// </summary>
public sealed class CreateCompanyDefaultTaxRatesTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid BogotaId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public async Task AColombianCompanyWithVatAsksForTheDefaultRates()
    {
        var seeder = new RecordingDefaultTaxRates();
        var handler = CreateHandler(new InMemoryCompanyRepository(), seeder);

        await handler.HandleAsync(CreateCommand(chargesVat: null), TestContext.Current.CancellationToken);

        var call = Assert.Single(seeder.Calls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal(SubjectId, call.ActorId);
    }

    [Fact]
    public async Task ACompanyWithoutVatDoesNotAskForThem()
    {
        var seeder = new RecordingDefaultTaxRates();
        var handler = CreateHandler(new InMemoryCompanyRepository(), seeder);

        await handler.HandleAsync(CreateCommand(chargesVat: false), TestContext.Current.CancellationToken);

        Assert.Empty(seeder.Calls);
    }

    [Fact]
    public async Task AForeignCompanyDoesNotAskForThem()
    {
        var seeder = new RecordingDefaultTaxRates();
        var handler = CreateHandler(new InMemoryCompanyRepository(), seeder);

        await handler.HandleAsync(
            CreateCommand(chargesVat: true) with
            {
                Country = "ES",
                DocumentType = "NIF",
                CityId = null,
                CityName = "Madrid",
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(seeder.Calls);
    }

    [Fact]
    public async Task AFailingSeedStillReturnsTheCompany()
    {
        var seeder = new RecordingDefaultTaxRates { Failure = new InvalidOperationException("catalog down") };
        var repository = new InMemoryCompanyRepository();
        var handler = CreateHandler(repository, seeder);

        var company = await handler.HandleAsync(CreateCommand(chargesVat: true), TestContext.Current.CancellationToken);

        Assert.Equal("Andes Logistica S.A.S.", company.Name);
        Assert.Single(seeder.Calls);
        Assert.Single(repository.Companies);
    }

    [Fact]
    public async Task TurningVatOnAsksForTheRates()
    {
        var repository = new InMemoryCompanyRepository();
        var stored = Company.Create(
            CompanyId.New(),
            TenantId,
            "Andes Logistica S.A.S.",
            [],
            "900.111.222-3",
            "CO",
            CompanyDocumentTypes.Parse("NIT"),
            BogotaId,
            null,
            new CompanyContactInfo(),
            DateTimeOffset.UnixEpoch,
            chargesVat: false);
        repository.Add(stored);
        var seeder = new RecordingDefaultTaxRates();
        var handler = UpdateHandler(repository, seeder);

        await handler.HandleAsync(
            new UpdateCompanyCommand(
                TenantId,
                stored.Id.Value,
                "Andes Logistica S.A.S.",
                [],
                "900.111.222-3",
                "CO",
                "NIT",
                BogotaId,
                null,
                null,
                null,
                null,
                ChargesVat: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantId, Assert.Single(seeder.Calls).TenantId);
    }

    [Fact]
    public async Task AnUpdateThatAlreadyChargedVatDoesNotAskAgain()
    {
        var repository = new InMemoryCompanyRepository();
        var stored = Company.Create(
            CompanyId.New(),
            TenantId,
            "Andes Logistica S.A.S.",
            [],
            "900.111.222-3",
            "CO",
            CompanyDocumentTypes.Parse("NIT"),
            BogotaId,
            null,
            new CompanyContactInfo(),
            DateTimeOffset.UnixEpoch);
        repository.Add(stored);
        var seeder = new RecordingDefaultTaxRates();
        var handler = UpdateHandler(repository, seeder);

        await handler.HandleAsync(
            new UpdateCompanyCommand(
                TenantId,
                stored.Id.Value,
                "Andes Logistica S.A.S. renombrada",
                [],
                "900.111.222-3",
                "CO",
                "NIT",
                BogotaId,
                null,
                null,
                null,
                null),
            TestContext.Current.CancellationToken);

        Assert.Empty(seeder.Calls);
    }

    private static CreateCompanyCommand CreateCommand(bool? chargesVat) =>
        new(
            TenantId,
            "Andes Logistica S.A.S.",
            [],
            "900.111.222-3",
            "CO",
            "NIT",
            BogotaId,
            null,
            null,
            null,
            null,
            ChargesVat: chargesVat);

    private static CreateCompanyHandler CreateHandler(InMemoryCompanyRepository repository, ICompanyDefaultTaxRates seeder) =>
        new(
            repository,
            new NoOpUnitOfWork(),
            new NoOpAuditPublisher(),
            new FixedGeographyLookup(),
            new ManagerContext(),
            new FixedClock(),
            new CreateCompanyValidator(),
            new EmptyTaxRateLookup(),
            seeder,
            NullLogger<CreateCompanyHandler>.Instance);

    private static UpdateCompanyHandler UpdateHandler(InMemoryCompanyRepository repository, ICompanyDefaultTaxRates seeder) =>
        new(
            repository,
            new NoOpUnitOfWork(),
            new NoOpAuditPublisher(),
            new FixedGeographyLookup(),
            new ManagerContext(),
            new FixedClock(),
            new UpdateCompanyValidator(),
            new EmptyTaxRateLookup(),
            seeder,
            NullLogger<UpdateCompanyHandler>.Instance);

    private sealed class RecordingDefaultTaxRates : ICompanyDefaultTaxRates
    {
        public List<(Guid TenantId, Guid? ActorId)> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task EnsureColombianVatAsync(Guid tenantId, Guid? actorId, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, actorId));
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class InMemoryCompanyRepository : ICompanyRepository
    {
        public List<Company> Companies { get; } = [];

        public Task<IReadOnlyList<Company>> SearchAsync(
            Guid tenantId,
            string? search,
            string? name,
            string? taxId,
            CompanyStatusFilter? status,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Company>>(Companies.Where(company => company.TenantId == tenantId).ToList());

        public Task<Company?> FindAsync(Guid tenantId, CompanyId companyId, CancellationToken cancellationToken) =>
            Task.FromResult(Companies.SingleOrDefault(company => company.TenantId == tenantId && company.Id == companyId));

        public Task<IReadOnlySet<string>> ListCountriesAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<bool> AnyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public void Add(Company company) => Companies.Add(company);

        public void Remove(Company company) => Companies.Remove(company);
    }

    private sealed class NoOpUnitOfWork : ICompaniesUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);
    }

    private sealed class NoOpAuditPublisher : ICompaniesAuditPublisher
    {
        public void Publish(
            Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)
        {
        }
    }

    private sealed class FixedGeographyLookup : ICompanyGeographyLookup
    {
        private static readonly CompanyCityRef Bogota =
            new(BogotaId, "11001", "Bogota", Guid.Parse("01900000-0000-7000-8000-0000000000d1"), "11", "Bogota D.C.");

        public Task<CompanyCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            Task.FromResult<CompanyCityRef?>(cityId == BogotaId ? Bogota : null);

        public Task<IReadOnlyDictionary<Guid, CompanyRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompanyRegionRef>>(new Dictionary<Guid, CompanyRegionRef>());

        public Task<IReadOnlyDictionary<Guid, CompanyCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompanyCityRef>>(
                cityIds.Where(id => id == BogotaId).ToDictionary(id => id, _ => Bogota));
    }

    private sealed class ManagerContext : IExecutionContext
    {
        public Guid SubjectId => CreateCompanyDefaultTaxRatesTests.SubjectId;

        public TenantId TenantId => new(CreateCompanyDefaultTaxRatesTests.TenantId);

        public bool HasPermission(string permission) => permission == CompaniesPermissions.CompanyManage;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class EmptyTaxRateLookup : ICompanyTaxRateLookup
    {
        public Task<IReadOnlyDictionary<Guid, CompanyTaxRateRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompanyTaxRateRef>>(new Dictionary<Guid, CompanyTaxRateRef>());
    }
}
