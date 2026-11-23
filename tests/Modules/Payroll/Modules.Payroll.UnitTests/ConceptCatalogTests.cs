using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class ConceptCatalogTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid OtherCompany = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeConceptRepository : IPayrollConceptRepository
    {
        public List<PayrollConcept> Stored { get; } = [];

        public Task<PayrollConcept?> FindAsync(Guid tenantId, Guid companyId, PayrollConceptId id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.FirstOrDefault(concept =>
                concept.TenantId == tenantId && concept.CompanyId == companyId && concept.Id == id));

        public Task<IReadOnlyList<PayrollConcept>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollConcept>>(
                Stored.Where(concept => concept.TenantId == tenantId && concept.CompanyId == companyId).ToList());

        public void Add(PayrollConcept concept) => Stored.Add(concept);
    }

    private static PayrollConcept Custom(Guid companyId, ConceptKind kind, string name, bool? constitutive = null, decimal? multiplier = null) =>
        PayrollConcept.Create(PayrollConceptId.New(), Tenant, companyId, kind, name, constitutive, multiplier, Now);

    // Archived at 03:00 UTC of the 16th = 22:00 of the 15th in Bogotá: archived on the 15th, so it is gone from the 16th on.
    [Fact]
    public async Task ArchivedBeforeCountsTheTenantDay()
    {
        var repository = new FakeConceptRepository();
        var bonus = Custom(Company, ConceptKind.Income, "Bono", constitutive: false);
        bonus.Archive(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero));
        repository.Add(bonus);
        var catalog = await new ConceptCatalog(repository).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken);
        static DateOnly Bogota(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(-5)).DateTime);

        Assert.Contains(bonus.Id.Value, catalog.ArchivedBefore(new DateOnly(2026, 11, 16), Bogota));
        Assert.DoesNotContain(bonus.Id.Value, catalog.ArchivedBefore(new DateOnly(2026, 11, 1), Bogota));
    }

    [Fact]
    public async Task SystemConceptsAreAlwaysThere()
    {
        var catalog = await new ConceptCatalog(new FakeConceptRepository()).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken);

        var commissions = catalog.Find(Guid.Parse("00000000-0000-0000-0002-000000000001"));
        Assert.NotNull(commissions);
        Assert.True(commissions.System);
        Assert.Equal(SystemConceptKey.Commissions, commissions.SystemKey);
        Assert.Equal(ConceptKind.Income, commissions.Kind);
        Assert.True(commissions.DefaultSalaryConstitutive);
        Assert.Null(commissions.Multiplier);
        Assert.False(commissions.Archived);
        Assert.Equal(12, catalog.ToDtos().Count);
    }

    [Fact]
    public async Task ACustomConceptOfAnotherCompanyIsNotFound()
    {
        var repository = new FakeConceptRepository();
        var foreign = Custom(OtherCompany, ConceptKind.Income, "Bono de otra empresa", constitutive: false);
        var own = Custom(Company, ConceptKind.Income, "Auxilio de alimentación", constitutive: false);
        repository.Add(foreign);
        repository.Add(own);

        var catalog = await new ConceptCatalog(repository).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken);

        Assert.Null(catalog.Find(foreign.Id.Value));
        var resolved = catalog.Find(own.Id.Value);
        Assert.NotNull(resolved);
        Assert.False(resolved.System);
        Assert.Null(resolved.SystemKey);
        Assert.Equal("Auxilio de alimentación", resolved.Name);
    }

    [Fact]
    public async Task TheSystemConceptsComeFirstInTheContractOrder()
    {
        var repository = new FakeConceptRepository();
        repository.Add(Custom(Company, ConceptKind.Deduction, "Libranza"));
        repository.Add(Custom(Company, ConceptKind.Overtime, "Hora extra especial", multiplier: 1.4m));
        var archived = Custom(Company, ConceptKind.Overtime, "Antigua", multiplier: 2m);
        archived.Archive(Now);
        repository.Add(archived);

        var dtos = (await new ConceptCatalog(repository).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken)).ToDtos();

        Assert.Equal(SystemConcepts.All.Select(concept => concept.Id), dtos.Take(12).Select(dto => dto.Id));
        Assert.All(dtos.Take(12), dto => Assert.True(dto.System));
        Assert.Equal(["Antigua", "Hora extra especial", "Libranza"], dtos.Skip(12).Select(dto => dto.Name));
        Assert.True(dtos[12].Archived);
        Assert.Equal(1.4m, dtos[13].Multiplier);
        // El del sistema no se edita: sin versión; el propio sí la trae.
        Assert.Null(dtos[0].Version);
        Assert.NotNull(dtos[13].Version);
    }
}
