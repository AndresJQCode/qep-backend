using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// La sonda de retención de Hr: Identity no borra a un usuario huérfano mientras alguna de sus membresías esté vinculada a un
/// empleado (<c>hr.employees.member_id</c>).
/// </summary>
public sealed class HrUserReferenceProbeTests
{
    private static readonly Guid Tenant = Guid.Parse(HrApiHarness.TenantId);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> UserOfAsync(QepApiFactory factory, Guid membershipId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Memberships
            .Where(membership => membership.Id == new MembershipId(membershipId))
            .Select(membership => membership.UserId)
            .SingleAsync(Ct);
    }

    private static async Task<bool> RetainsAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(candidate => candidate.Source == "hr");
        return await probe.HasReferencesAsync(userId, Ct);
    }

    // Un empleado real: la sede lleva FK a una empresa de verdad.
    private static async Task LinkAnEmployeeAsync(QepApiFactory factory, Guid memberId)
    {
        using var client = CreateClient(factory, SubjectId, HrApiHarness.TenantId, "companies.company.read", "companies.company.manage");
        var company = await CreateCompanyAsync(client, Tenant);
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IHrSettingsRepository>();
        var site = Site.Create(SiteId.New(), Tenant, company.Id, "Principal", null, null, null, Now);
        var workplace = Workplace.Create(WorkplaceId.New(), Tenant, site, "Bodega", null, ArlRiskClass.I, false, Now);
        var position = Position.Create(PositionId.New(), Tenant, "Analista", Now);
        settings.Add(site);
        settings.Add(workplace);
        settings.Add(position);
        var employee = Employee.Hire(
            EmployeeId.New(),
            Tenant,
            new EmployeeProfile(
                "Ana", "Pérez", HrDocumentType.CC, "1020304050", "CO", null, null, null, null, null, null, memberId,
                null, null, null, null, ContributorSubtype.Standard, false, [DayOfWeek.Sunday]),
            null,
            new NewContract(
                EmploymentContractId.New(),
                new ContractTerms(company.Id, ContractType.Employee, ContractDuration.Indefinite, SalaryType.Monthly, new DateOnly(2026, 1, 15), null),
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
            new SalaryFloor(new DateOnly(2026, 11, 20), 1_750_905m, 22_761_765m),
            Now);
        scope.ServiceProvider.GetRequiredService<IEmployeeRepository>().Add(employee);
        await scope.ServiceProvider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AnEmployeeLinkedToTheMembershipRetainsTheUser()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var membership = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var user = await UserOfAsync(factory, membership);
        Assert.False(await RetainsAsync(factory, user));

        await LinkAnEmployeeAsync(factory, membership);

        Assert.True(await RetainsAsync(factory, user));
    }

    [Fact]
    public async Task WithoutLinkTheUserIsNotRetained()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var linked = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var bystander = await SeedMemberAsync(factory, "beto@example.com", "Beto Ajeno", "admin");
        var bystanderUser = await UserOfAsync(factory, bystander);

        await LinkAnEmployeeAsync(factory, linked);

        Assert.False(await RetainsAsync(factory, bystanderUser));
    }
}
