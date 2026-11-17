using BuildingBlocks.Application;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class TaxUnitOperatorHandlerTests
{
    private static readonly Guid OperatorTenantGuid = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid Subject = Guid.Parse("01900000-0000-7000-8000-0000000000aa");
    private static readonly TenantId OperatorTenant = new(OperatorTenantGuid);

    // 23:00 del 23-dic en Bogotá (UTC-5): todavía no es el 24.
    private static readonly DateTimeOffset BeforeTheWarning = new(2026, 12, 24, 3, 0, 0, TimeSpan.Zero);

    // 01:00 del 24-dic en Bogotá.
    private static readonly DateTimeOffset AfterTheWarning = new(2026, 12, 24, 6, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private sealed class Fixture
    {
        public Fixture(DateTimeOffset utcNow)
        {
            Clock = new FixedClock(utcNow);
            List = new ListTaxUnitsHandler(Repository, Context, new FixedOperator(), Clock);
            Set = new SetTaxUnitValueHandler(Repository, UnitOfWork, Audit, Context, new FixedOperator(), Clock);
        }

        public InMemoryRepository Repository { get; } = new();

        public RecordingUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public Context Context { get; } = new();

        public FixedClock Clock { get; }

        public ListTaxUnitsHandler List { get; }

        public SetTaxUnitValueHandler Set { get; }
    }

    [Fact]
    public async Task ListShowsOnlyCountriesWithUnit()
    {
        var fixture = new Fixture(BeforeTheWarning);
        fixture.Repository.Items.Add(TaxUnitValue.Create("CO", 2025, 47065m, Subject, BeforeTheWarning, 2026));
        fixture.Repository.Items.Add(TaxUnitValue.Create("CO", 2026, 49799m, Subject, BeforeTheWarning, 2026));

        var result = await fixture.List.HandleAsync(new ListTaxUnitsQuery(OperatorTenant), TestContext.Current.CancellationToken);

        var colombia = Assert.Single(result.Countries);
        Assert.Equal(("CO", "UVT", "COP"), (colombia.Country, colombia.TaxUnit, colombia.Currency));
        Assert.Equal([2026, 2025], colombia.Values.Select(value => value.Year));
        Assert.Equal(49799m, colombia.Values[0].Value);
    }

    [Fact]
    public async Task NextYearMissingUsesTheOperatorTenantClock()
    {
        var early = new Fixture(BeforeTheWarning);
        var late = new Fixture(AfterTheWarning);

        var before = await early.List.HandleAsync(new ListTaxUnitsQuery(OperatorTenant), TestContext.Current.CancellationToken);
        var after = await late.List.HandleAsync(new ListTaxUnitsQuery(OperatorTenant), TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(before.Countries).NextYearMissing);
        Assert.True(Assert.Single(after.Countries).NextYearMissing);
        Assert.Equal(OperatorTenantGuid, late.Clock.AskedFor);
    }

    [Fact]
    public async Task SetAuditsValueSet()
    {
        var fixture = new Fixture(BeforeTheWarning);

        var result = await fixture.Set.HandleAsync(
            new SetTaxUnitValueCommand(OperatorTenant, "co", 2026, 49799m), TestContext.Current.CancellationToken);

        Assert.Equal((2026, 49799m), (result.Year, result.Value));
        var item = Assert.Single(fixture.Repository.Items);
        Assert.Equal("CO", item.Country);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("catalog.tax_unit.value_set", "CO-2026"), (audit.Action, audit.ResourceId));
        Assert.Equal((OperatorTenantGuid, Subject), (audit.TenantId, audit.ActorId));
        Assert.Equal(1, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task SetTwiceReplaces()
    {
        var fixture = new Fixture(BeforeTheWarning);
        var token = TestContext.Current.CancellationToken;

        await fixture.Set.HandleAsync(new SetTaxUnitValueCommand(OperatorTenant, "CO", 2026, 49799m), token);
        var second = await fixture.Set.HandleAsync(new SetTaxUnitValueCommand(OperatorTenant, "CO", 2026, 50000m), token);

        Assert.Equal(50000m, second.Value);
        Assert.Equal(50000m, Assert.Single(fixture.Repository.Items).Value);
        Assert.Equal(2, fixture.Audit.Entries.Count);
    }

    [Fact]
    public async Task ACountryWithoutUnitIsRejectedAndNothingIsWritten()
    {
        var fixture = new Fixture(BeforeTheWarning);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(() => fixture.Set.HandleAsync(
            new SetTaxUnitValueCommand(OperatorTenant, "ES", 2026, 1m), TestContext.Current.CancellationToken));

        Assert.Equal("catalog.tax_unit.country_without_unit", error.Code);
        Assert.Empty(fixture.Repository.Items);
        Assert.Equal(0, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnotherTenantCannotOperate()
    {
        var fixture = new Fixture(BeforeTheWarning);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => fixture.Set.HandleAsync(
            new SetTaxUnitValueCommand(new TenantId(Guid.CreateVersion7()), "CO", 2026, 1m), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => fixture.List.HandleAsync(
            new ListTaxUnitsQuery(new TenantId(Guid.CreateVersion7())), TestContext.Current.CancellationToken));
    }

    private sealed class Context : IExecutionContext
    {
        public Guid SubjectId => Subject;

        public TenantId TenantId => OperatorTenant;

        public bool HasPermission(string permission) => true;
    }

    private sealed class FixedOperator : IOperatorTenant
    {
        public bool IsOperator(Guid tenantId) => tenantId == OperatorTenantGuid;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ITenantClock
    {
        public Guid? AskedFor { get; private set; }

        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            AskedFor = tenantId;
            return Task.FromResult(new TenantCalendar(utcNow, Bogota));
        }
    }

    private sealed class RecordingUnitOfWork : ICatalogUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<ICatalogTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record AuditEntry(Guid TenantId, Guid ActorId, string Action, string ResourceId);

    private sealed class RecordingAudit : ICatalogAuditPublisher
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Publish(
            Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Entries.Add(new AuditEntry(tenantId, actorId, action, resourceId));
    }

    // Compara el país tal cual, como el repositorio real.
    private sealed class InMemoryRepository : ITaxUnitValueRepository
    {
        public List<TaxUnitValue> Items { get; } = [];

        public Task<IReadOnlyList<TaxUnitValue>> ListAsync(string? country, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaxUnitValue>>(
                [.. Items.Where(item => country is null || item.Country == country).OrderByDescending(item => item.Year)]);

        public Task<TaxUnitValue?> FindAsync(string country, int year, CancellationToken cancellationToken) =>
            Task.FromResult(Items.FirstOrDefault(item => item.Country == country && item.Year == year));

        public Task<bool> AnyAsync(string country, CancellationToken cancellationToken) =>
            Task.FromResult(Items.Any(item => item.Country == country));

        public void Add(TaxUnitValue value) => Items.Add(value);
    }
}
