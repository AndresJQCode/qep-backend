using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// The HR side of the effective member phone against real Postgres. One database for the class; each test works in its own
/// tenant (tenant ids carry no foreign key), so they do not step on each other.
/// </summary>
public sealed class HrMemberPhonesTests : IClassFixture<HrMemberPhonesTests.World>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly SalaryFloor Floor = new(new DateOnly(2026, 11, 20), 1_750_905m, 22_761_765m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public HrMemberPhonesTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal QepApiFactory Factory { get; private set; } = null!;

        public Guid CompanyId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            Factory = new QepApiFactory(_database.GetConnectionString());
            await SeedTenantAsync(Factory);
            using var client = CreateClient(
                Factory, SubjectId, TenantId, "companies.company.read", "companies.company.manage");
            CompanyId = (await CreateCompanyAsync(client, Guid.Parse(TenantId), "Empresa uno")).Id;
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ListsOnlyActiveEmployeesWithPhoneAndMember()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var (position, site, workplace) = await SeedSettingsAsync(tenant);
        var (otherPosition, otherSite, otherWorkplace) = await SeedSettingsAsync(otherTenant);

        var activeMember = Guid.NewGuid();
        var retiredMember = Guid.NewGuid();
        var noPhoneMember = Guid.NewGuid();
        var otherTenantMember = Guid.NewGuid();
        var active = Hire(tenant, "1001", "3001234567", activeMember, position, site, workplace);
        var retired = Hire(tenant, "1002", "3001234568", retiredMember, position, site, workplace);
        retired.Deactivate(new DateOnly(2026, 10, 31), Now);
        var noPhone = Hire(tenant, "1003", null, noPhoneMember, position, site, workplace);
        var noMember = Hire(tenant, "1004", "3001234569", null, position, site, workplace);
        var elsewhere = Hire(otherTenant, "1005", "3001234570", otherTenantMember, otherPosition, otherSite, otherWorkplace);
        await AddAsync(active, retired, noPhone, noMember, elsewhere);

        var found = await InScopeAsync(provider => provider.GetRequiredService<IHrMemberPhones>().ListAsync(
            tenant, [activeMember, retiredMember, noPhoneMember, otherTenantMember], Ct));

        var phone = Assert.Single(found);
        Assert.Equal(activeMember, phone.MemberId);
        Assert.Equal("3001234567", phone.Phone);
    }

    private async Task<(Position Position, Site Site, Workplace Workplace)> SeedSettingsAsync(Guid tenantId)
    {
        var site = Site.Create(SiteId.New(), tenantId, _world.CompanyId, "Principal", null, null, null, Now);
        var workplace = Workplace.Create(WorkplaceId.New(), tenantId, site, "Bodega", null, ArlRiskClass.III, false, Now);
        var position = Position.Create(PositionId.New(), tenantId, "Analista", Now);
        await InScopeAsync(async provider =>
        {
            var settings = provider.GetRequiredService<IHrSettingsRepository>();
            settings.Add(site);
            settings.Add(workplace);
            settings.Add(position);
            await provider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);
            return 0;
        });
        return (position, site, workplace);
    }

    private Employee Hire(
        Guid tenantId,
        string document,
        string? phone,
        Guid? memberId,
        Position position,
        Site site,
        Workplace workplace) => Employee.Hire(
            EmployeeId.New(),
            tenantId,
            new EmployeeProfile(
                "Ana", "Pérez", HrDocumentType.CC, document, "CO", null, phone, null, null, null, null, memberId,
                null, null, null, null, ContributorSubtype.Standard, false, [DayOfWeek.Saturday, DayOfWeek.Sunday]),
            null,
            new NewContract(
                EmploymentContractId.New(),
                new ContractTerms(
                    _world.CompanyId,
                    ContractType.Employee,
                    ContractDuration.Indefinite,
                    SalaryType.Monthly,
                    new DateOnly(2026, 1, 15),
                    null),
                null,
                SalaryRecordId.New(),
                2_500_000m,
                false,
                false,
                AssignmentRecordId.New(),
                position.Id,
                null,
                site.Id,
                workplace.Id),
            Floor,
            Now);

    private async Task AddAsync(params Employee[] employees) =>
        await InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IEmployeeRepository>().AddRange(employees);
            await provider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);
            return 0;
        });

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}
