using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Npgsql;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;
using static Modules.Payroll.IntegrationTests.PayrollExportApiTests;

namespace Modules.Payroll.IntegrationTests;

/// <summary>Una base donde un tenant concreto hace de operador de la plataforma: lo que la consola de valores legales necesita.</summary>
public sealed class PayrollOperatorFixture : PayrollApiFixture
{
    public static readonly Guid OperatorTenantGuid = Guid.Parse("01900000-0000-7000-8000-0000000000f0");

    protected override Guid? OperatorTenant => OperatorTenantGuid;
}

/// <summary>
/// El cruce de punta a punta entre la consola de valores legales (Hr), el puerto <c>ILegalParameterUsage</c>, su adaptador de
/// Bootstrapper y las nóminas calculadas (Payroll), sin dobles: editar un valor legal que ya usó una nómina liquidada es 409 y la foto
/// legal del período no se mueve.
/// </summary>
public sealed class LegalParameterUsageApiTests : IClassFixture<PayrollOperatorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollOperatorFixture _fixture;

    public LegalParameterUsageApiTests(PayrollOperatorFixture fixture) => _fixture = fixture;

    private async Task<string?> LegalValuesOfAsync(Guid periodId)
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT legal_values::text FROM payroll.periods WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", periodId);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    [Fact]
    public async Task EditingTheSmmlvOfALiquidatedPeriodIs409AndThePhotoStays()
    {
        var scene = await NewSceneAsync(_fixture);
        await TransitionAsync(scene, "liquidate", new { confirmNoAbsences = true });
        var photoBefore = await LegalValuesOfAsync(scene.PeriodId);
        Assert.False(string.IsNullOrWhiteSpace(photoBefore));

        var operatorId = PayrollOperatorFixture.OperatorTenantGuid.ToString();
        await SeedTenantAsync(_fixture.Factory, operatorId);
        using var operatorClient = CreateClient(
            _fixture.Factory,
            SubjectId,
            operatorId,
            HrOperatorPermissions.LegalParametersRead,
            HrOperatorPermissions.LegalParametersManage);
        var url = $"/api/v1/tenants/{operatorId}/operator/legal-parameters/Smmlv/2026-01-01";

        var response = await operatorClient.PutAsJsonAsync(url, new { value = 2_000_000 }, Ct);

        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("hr.legal_parameter.in_use", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("periodsAffected").GetInt32() >= 1);
        Assert.Equal(photoBefore, await LegalValuesOfAsync(scene.PeriodId));

        var screen = await JsonOfAsync(await operatorClient.GetAsync(
            $"/api/v1/tenants/{operatorId}/operator/legal-parameters/", Ct));
        var smmlv = screen.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "Smmlv");
        var row = smmlv.GetProperty("rows").EnumerateArray().Single(item => item.GetProperty("effectiveFrom").GetString() == "2026-01-01");
        Assert.NotEqual(2_000_000m, row.GetProperty("value").GetDecimal());
        Assert.True(row.GetProperty("inUse").GetBoolean());

        scene.Tenant.Dispose();
    }
}
