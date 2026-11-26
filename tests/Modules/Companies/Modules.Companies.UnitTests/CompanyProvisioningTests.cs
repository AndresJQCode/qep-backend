using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.UnitTests;

/// <summary>
/// El alta de empresa que hace el sistema al aceptar una invitación de mayorista: sin <c>IExecutionContext</c>, idempotente por
/// tipo y número de documento exactos, bajo el candado por tenant (no hay índice único que lo arbitre, EMP-08).
/// </summary>
public sealed class CompanyProvisioningTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid ActorId = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid KnownCity = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public async Task CreatesTheCompanyWithTheGivenDataAndAuditsWithTheActor()
    {
        var world = new World();

        var id = await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        var company = Assert.Single(world.Repository.Companies);
        Assert.Equal(company.Id.Value, id);
        Assert.Equal("Distribuciones del Norte", company.Name);
        Assert.Equal("900123456", company.TaxId);
        Assert.Equal("CO", company.Country);
        Assert.Equal(CompanyDocumentType.Nit, company.DocumentType);
        Assert.Equal(KnownCity, company.CityId);
        Assert.Equal("Calle 10 # 40-20", company.Address);
        Assert.Equal("ana@distri.co", company.Email);
        Assert.Equal("+573001234567", company.Phone);
        Assert.True(company.ChargesVat);
        var audit = Assert.Single(world.Audit.Entries);
        Assert.Equal("companies.company.created", audit.Action);
        Assert.Equal(ActorId, audit.ActorId);
        Assert.Equal(TenantId, audit.TenantId);
    }

    [Fact]
    public async Task TakesTheLockInsideTheTransactionBeforeLookingAndCommitsAfterSaving()
    {
        var world = new World();

        await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        // Lo que sigue a «commit» es la siembra de tasas (el doble también anota en este log).
        Assert.Equal(["begin", $"lock:{TenantId}", "save", "commit"], world.UnitOfWork.Log.Take(4));
    }

    [Fact]
    public async Task ReusesAnExistingCompanyWithTheSameTypeAndNumberWithoutUpdatingIt()
    {
        var world = new World();
        var existing = world.Seed(CompanyDocumentType.Nit, "900123456", "Otro nombre");

        var id = await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        Assert.Equal(existing.Id.Value, id);
        Assert.Single(world.Repository.Companies);
        Assert.Equal("Otro nombre", existing.Name);
        Assert.DoesNotContain("save", world.UnitOfWork.Log);
        Assert.Empty(world.Audit.Entries);
    }

    [Fact]
    public async Task TheFirstCompanyIsCreatedWhenTheTenantHasNone()
    {
        var world = new World();

        var id = await world.Sut.EnsureFirstCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        var company = Assert.Single(world.Repository.Companies);
        Assert.Equal(company.Id.Value, id);
        Assert.Equal(["begin", $"lock:{TenantId}", "save", "commit"], world.UnitOfWork.Log.Take(4));
    }

    [Fact]
    public async Task TheFirstCompanyIsSkippedWhenTheTenantAlreadyHasOneEvenInactiveOrOfAnotherDocument()
    {
        var world = new World();
        var existing = world.Seed(CompanyDocumentType.Nit, "800111222", "La que ya tenia");
        existing.Deactivate(DateTimeOffset.UnixEpoch);

        var id = await world.Sut.EnsureFirstCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        Assert.Null(id);
        Assert.Single(world.Repository.Companies);
        Assert.DoesNotContain("save", world.UnitOfWork.Log);
        Assert.Empty(world.Audit.Entries);
    }

    [Fact]
    public async Task ReusesAnInactiveCompanyToo()
    {
        var world = new World();
        var existing = world.Seed(CompanyDocumentType.Nit, "900123456", "Otro nombre");
        existing.Deactivate(DateTimeOffset.UnixEpoch);

        var id = await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        Assert.Equal(existing.Id.Value, id);
        Assert.Single(world.Repository.Companies);
    }

    [Fact]
    public async Task DoesNotReuseACompanyWhoseNumberOnlyContainsTheRequestedOne()
    {
        var world = new World();
        world.Seed(CompanyDocumentType.Nit, "9001234567", "Otra");

        await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        Assert.Equal(2, world.Repository.Companies.Count);
    }

    [Fact]
    public async Task DoesNotReuseTheSameNumberWithAnotherDocumentType()
    {
        var world = new World();
        world.Seed(CompanyDocumentType.Cc, "900123456", "Persona");

        await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        Assert.Equal(2, world.Repository.Companies.Count);
    }

    [Fact]
    public async Task AColombianCompanyWithoutCityIsRejectedWithTheCityIdField()
    {
        var world = new World();

        var failure = await Assert.ThrowsAsync<ValidationException>(() => world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456"), TestContext.Current.CancellationToken));

        Assert.Contains(failure.Errors, error => error.PropertyName == "CityId");
        Assert.Empty(world.Repository.Companies);
        Assert.DoesNotContain("commit", world.UnitOfWork.Log);
    }

    [Fact]
    public async Task AnUnknownCityIsCompaniesCityNotFound()
    {
        var world = new World();

        var failure = await Assert.ThrowsAsync<CompaniesDomainException>(() => world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Equal("companies.company.city_not_found", failure.Code);
        Assert.Empty(world.Repository.Companies);
    }

    [Fact]
    public async Task SeedsTheDefaultRatesAfterCommitForAColombianCompany()
    {
        var world = new World();

        await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("CO", "NIT", "900123456", cityId: KnownCity), TestContext.Current.CancellationToken);

        var call = Assert.Single(world.Rates.Calls);
        Assert.Equal((TenantId, (Guid?)ActorId), call);
        Assert.Equal("commit", world.UnitOfWork.Log[^2]);
        Assert.Equal($"rates:{TenantId}", world.UnitOfWork.Log[^1]);
    }

    [Fact]
    public async Task DoesNotSeedRatesForACompanyOutsideColombia()
    {
        var world = new World();

        await world.Sut.EnsureCompanyAsync(
            TenantId, ActorId, Data("ES", "NIF", "B12345678", cityName: "Madrid"), TestContext.Current.CancellationToken);

        Assert.Empty(world.Rates.Calls);
        Assert.Single(world.Repository.Companies);
    }

    private static ProvisionedCompanyData Data(
        string country, string documentType, string taxId, Guid? cityId = null, string? cityName = null) =>
        new(
            "Distribuciones del Norte", country, documentType, taxId, cityId, cityName,
            "Calle 10 # 40-20", "ana@distri.co", "+573001234567");

    private sealed class World
    {
        public World()
        {
            Rates = new RecordingRates(UnitOfWork.Log);
            Sut = new CompanyProvisioning(
                Repository,
                UnitOfWork,
                Audit,
                new KnownCityLookup(),
                new FixedClock(),
                new CreateCompanyValidator(),
                new EmptyTaxRateLookup(),
                Rates,
                NullLogger<CompanyProvisioning>.Instance);
        }

        public InMemoryCompanyRepository Repository { get; } = new();

        public NoOpUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public RecordingRates Rates { get; }

        public CompanyProvisioning Sut { get; }

        public Company Seed(CompanyDocumentType type, string taxId, string name)
        {
            var company = Company.Create(
                CompanyId.New(), TenantId, name, [], taxId, "CO", type, KnownCity, null,
                new CompanyContactInfo(), DateTimeOffset.UnixEpoch);
            Repository.Add(company);
            return company;
        }
    }

    private sealed class RecordingAudit : ICompaniesAuditPublisher
    {
        public List<(Guid TenantId, Guid ActorId, string Action)> Entries { get; } = [];

        public void Publish(
            Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Entries.Add((tenantId, actorId, action));
    }

    private sealed class RecordingRates(List<string> log) : ICompanyDefaultTaxRates
    {
        public List<(Guid TenantId, Guid? ActorId)> Calls { get; } = [];

        public Task EnsureColombianVatAsync(Guid tenantId, Guid? actorId, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, actorId));
            log.Add($"rates:{tenantId}");
            return Task.CompletedTask;
        }
    }

    private sealed class KnownCityLookup : ICompanyGeographyLookup
    {
        private static readonly CompanyCityRef City =
            new(KnownCity, "05001", "Medellin", Guid.Parse("01900000-0000-7000-8000-0000000000d1"), "05", "Antioquia");

        public Task<CompanyCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            Task.FromResult<CompanyCityRef?>(cityId == KnownCity ? City : null);

        public Task<IReadOnlyDictionary<Guid, CompanyCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompanyCityRef>>(
                cityIds.Where(id => id == KnownCity).ToDictionary(id => id, _ => City));

        public Task<IReadOnlyDictionary<Guid, CompanyRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompanyRegionRef>>(new Dictionary<Guid, CompanyRegionRef>());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class EmptyTaxRateLookup : ICompanyTaxRateLookup
    {
        public Task<IReadOnlyDictionary<Guid, CompanyTaxRateRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompanyTaxRateRef>>(new Dictionary<Guid, CompanyTaxRateRef>());
    }
}
