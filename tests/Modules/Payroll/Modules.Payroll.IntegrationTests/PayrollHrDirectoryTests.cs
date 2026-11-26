using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Domain.Engine;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>El adaptador real de <see cref="IPayrollHrDirectory"/> contra Hr y Companies, con los empleados dados de alta por la API de Hr.</summary>
public sealed class PayrollHrDirectoryTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollHrDirectoryTests(PayrollApiFixture fixture) => _fixture = fixture;

    private async Task<T> WithDirectoryAsync<T>(Func<IPayrollHrDirectory, Task<T>> action)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IPayrollHrDirectory>());
    }

    [Fact]
    public async Task MapsAnEmployeeHiredThroughHr()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup, "1020304050");

        var employees = await WithDirectoryAsync(directory =>
            directory.ListEmployeesAsync(tenant.Id, setup.Company.Id, Start, End, Ct));

        var employee = Assert.Single(employees);
        Assert.Equal(EmployeeIdOf(detail), employee.EmployeeId);
        Assert.Equal("Ana", employee.FirstName);
        Assert.Equal("Pérez", employee.LastName);
        Assert.Equal("CC", employee.DocumentType);
        Assert.Equal("1020304050", employee.DocumentNumber);
        Assert.Equal("Standard", employee.ContributorSubtype);
        Assert.False(employee.ForeignPensionExempt);
        Assert.Equal([DayOfWeek.Sunday], employee.RestDays);
        var contract = Assert.Single(employee.Contracts);
        Assert.Equal("Employee", contract.ContractType);
        Assert.Equal("Monthly", contract.SalaryType);
        Assert.Equal(new DateOnly(2026, 1, 15), contract.StartDate);
        Assert.Null(contract.EndedOn);
        var salary = Assert.Single(contract.Salaries);
        Assert.Equal(new HrSalarySource(new DateOnly(2026, 1, 15), 2_500_000m, false), salary);
        var assignment = Assert.Single(contract.Assignments);
        Assert.Equal("Analista", assignment.PositionName);
        Assert.Equal("Principal", assignment.SiteName);
        Assert.Equal("Bodega", assignment.WorkplaceName);
        Assert.Equal(ArlClass.III, assignment.RiskClass);
        Assert.False(assignment.HighRiskPension);
        Assert.Equal("COMFAMA", assignment.CompensationFund);
    }

    [Fact]
    public async Task CarriesTheRestDays()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await HireAsync(tenant, HireBody(setup, restDays: ["Saturday", "Sunday"]));

        var employees = await WithDirectoryAsync(directory =>
            directory.ListEmployeesAsync(tenant.Id, setup.Company.Id, Start, End, Ct));

        // Order does not matter to the engine: compare as sets.
        Assert.Equal(
            new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }.Order(),
            Assert.Single(employees).RestDays.Order());
    }

    [Fact]
    public async Task OnlyTheRequestedCompanyContractsCome()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        var inFirst = await HireAsync(tenant, HireBody(setup, documentNumber: "1001", firstName: "Ana", lastName: "Alvarez"));
        var inSecond = await HireAsync(tenant, HireBody(
            setup, companyId: second.Company.Id, workplaceId: second.WorkplaceId, documentNumber: "1002", firstName: "Beto", lastName: "Benitez"));

        var first = await WithDirectoryAsync(directory =>
            directory.ListEmployeesAsync(tenant.Id, setup.Company.Id, Start, End, Ct));
        var other = await WithDirectoryAsync(directory =>
            directory.ListEmployeesAsync(tenant.Id, second.Company.Id, Start, End, Ct));

        Assert.Equal([EmployeeIdOf(inFirst)], first.Select(employee => employee.EmployeeId));
        Assert.Equal([EmployeeIdOf(inSecond)], other.Select(employee => employee.EmployeeId));
        Assert.Equal("Norte", Assert.Single(Assert.Single(other).Contracts).Assignments[0].SiteName);
    }

    [Fact]
    public async Task AnotherTenantSeesNothing()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await HireAsync(tenant, setup);

        var employees = await WithDirectoryAsync(directory =>
            directory.ListEmployeesAsync(Guid.NewGuid(), setup.Company.Id, Start, End, Ct));
        var company = await WithDirectoryAsync(directory =>
            directory.FindCompanyAsync(Guid.NewGuid(), setup.Company.Id, Ct));

        Assert.Empty(employees);
        Assert.Null(company);
    }

    [Fact]
    public async Task ListsOnlyActiveCompanies()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var second = await SeedSecondCompanyAsync(tenant);
        await DeactivateCompanyAsync(tenant.Admin, tenant.Id, second.Company.Id);

        var active = await WithDirectoryAsync(directory => directory.ListActiveCompaniesAsync(tenant.Id, Ct));
        var found = await WithDirectoryAsync(directory => directory.FindCompanyAsync(tenant.Id, second.Company.Id, Ct));

        Assert.Equal([setup.Company.Id], active.Select(company => company.Id));
        Assert.False(found!.IsActive);
        Assert.Equal(second.Company.Name, found.Name);
    }
}
