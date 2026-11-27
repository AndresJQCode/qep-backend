using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;
using static Modules.Payroll.IntegrationTests.PayrollExportApiTests;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Editar y eliminar registros de cargo y lugar contra la nómina (spec 2026-11-26, «Nómina»): sólo un período <c>Approved</c> o
/// <c>Closed</c> con resultado guardado del empleado bloquea. Reloj del harness: 2026-11-10, quincena 2026-11-01..15.
/// </summary>
public sealed class AssignmentPayrollGuardApiTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateOnly FirstHalfStart = new(2026, 11, 1);
    private static readonly DateOnly FirstHalfEnd = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public AssignmentPayrollGuardApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private async Task<IReadOnlyList<PayrollFrozenPeriod>> FrozenAsync(Guid tenantId, Guid employeeId)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPayrollFrozenPeriods>()
            .ListWithResultOfAsync(tenantId, employeeId, Ct);
    }

    [Fact]
    public async Task OnlyApprovedOrClosedPeriodsWithAResultAreListed()
    {
        var scene = await NewSceneAsync(_fixture);
        var expected = new PayrollFrozenPeriod(scene.Setup.Company.Id, FirstHalfStart, FirstHalfEnd);

        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
        Assert.Empty(await FrozenAsync(scene.Tenant.Id, scene.EmployeeId));

        await TransitionAsync(scene, "approve");
        Assert.Equal([expected], await FrozenAsync(scene.Tenant.Id, scene.EmployeeId));

        await TransitionAsync(scene, "close");
        Assert.Equal([expected], await FrozenAsync(scene.Tenant.Id, scene.EmployeeId));

        Assert.Empty(await FrozenAsync(Guid.NewGuid(), scene.EmployeeId));
    }

    [Fact]
    public async Task AnEmployeeWithoutAResultIsNotListed()
    {
        var scene = await NewSceneAsync(_fixture);
        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
        await TransitionAsync(scene, "approve");

        var later = EmployeeIdOf(await HireAsync(scene.Tenant, scene.Setup, "3003"));

        Assert.Empty(await FrozenAsync(scene.Tenant.Id, later));
    }
}
