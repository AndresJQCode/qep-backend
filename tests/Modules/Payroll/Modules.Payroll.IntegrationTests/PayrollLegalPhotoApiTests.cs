using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Payroll.Application;
using Npgsql;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;
using static Modules.Payroll.IntegrationTests.PayrollExportApiTests;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// Review I-1: after the first liquidation the period keeps its own legal photo. A corrective legal row inserted later (a higher
/// SMMLV for the period date) must not change what a Liquidated or Reopened period shows, nor what a capture accepts. Own class and
/// database: the legal table is global to the database and the row is removed in <c>finally</c>.
/// </summary>
public sealed class PayrollLegalPhotoApiTests : IClassFixture<PayrollApiFixture>
{
    private const string Insert = "INSERT INTO hr.legal_parameters (key, effective_from, value) VALUES ('Smmlv', '2026-11-01', 2000000)";
    private const string Delete = "DELETE FROM hr.legal_parameters WHERE key = 'Smmlv' AND effective_from = '2026-11-01'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollLegalPhotoApiTests(PayrollApiFixture fixture) => _fixture = fixture;

    private static async Task ExecuteAsync(PayrollApiFixture fixture, string sql)
    {
        var connectionString = fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
        // La caché del pod (área apagada, 60 s) no ve el SQL a mano: sin esto la fila nueva queda invisible y la prueba pasa en vacío.
        await fixture.Factory.Services.GetRequiredService<ILegalParameterCacheVersion>().BumpAsync(Ct);
    }

    [Fact]
    public async Task ACorrectiveSmmlvAfterLiquidatingDoesNotChangeALiquidatedOrReopenedView()
    {
        var scene = await NewSceneAsync(_fixture);
        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });

        await ExecuteAsync(_fixture, Insert);
        try
        {
            // Liquidated: the employee is still a row (not «salary below minimum») and a capture is still accepted.
            var detail = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct));
            Assert.Equal(1, detail.GetProperty("employees").GetArrayLength());
            Assert.Equal(1, detail.GetProperty("excluded").GetArrayLength());
            Assert.Equal("SalaryTypeNotSupported", detail.GetProperty("excluded")[0].GetProperty("reason").GetString());

            var capture = await PutAsync(
                scene.Tenant.Payroll,
                $"{scene.PeriodUrl}/employees/{scene.EmployeeId}/inputs",
                new { overtime = Array.Empty<object>(), incomes = Array.Empty<object>(), deductions = Array.Empty<object>() },
                await VersionAsync(scene.Tenant.Payroll, scene.PeriodUrl));
            Assert.True(capture.StatusCode == HttpStatusCode.OK, await BodyAsync(capture));

            // Reopened: the same.
            await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
            await TransitionAsync(scene, "approve");
            await TransitionAsync(scene, "reopen", new { reason = "Corregir" });
            var reopened = await JsonOfAsync(await scene.Tenant.Payroll.GetAsync(scene.PeriodUrl, Ct));
            Assert.Equal(1, reopened.GetProperty("employees").GetArrayLength());
            Assert.Equal(1, reopened.GetProperty("excluded").GetArrayLength());
        }
        finally
        {
            await ExecuteAsync(_fixture, Delete);
        }

        scene.Tenant.Dispose();
    }
}
