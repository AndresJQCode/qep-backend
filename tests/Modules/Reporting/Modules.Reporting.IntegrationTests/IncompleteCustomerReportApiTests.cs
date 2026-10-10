using System.Net.Http.Json;
using Npgsql;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>Spec 2026-10-10 §6.4: el reporte de clientes y su resumen no cuentan incompletos (sin clasificación ni
/// ciudad distorsionarían los cortes).</summary>
public sealed class IncompleteCustomerReportApiTests
{
    [Fact]
    public async Task TheCustomerReportAndItsSummaryIgnoreIncompleteRecords()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var counted = await CreateActiveCustomerAsync(client, tenant.TenantId);
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO customers.customers (id, tenant_id, name, is_active, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
                VALUES (gen_random_uuid(), @tenantId, 'Laura', true, 'Incomplete', 'CO.1', false, false, 1, now(), now())
                """,
                connection);
            insert.Parameters.AddWithValue("tenantId", tenant.TenantId);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var page = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);
        // El total del resumen es CustomerCount (CustomerReportSummaryApiTests), no un campo `total`.
        var summary = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(counted.Id, Assert.Single(page.Items).CustomerId);
        Assert.Equal(1, page.Total);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.CustomerCount);
    }
}
