using System.Net.Http.Json;
using Npgsql;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>Spec 2026-10-24 §3.3: el reporte de clientes y su resumen no cuentan a un cliente fusionado, que ya es otra ficha.</summary>
public sealed class MergedCustomerReportApiTests
{
    [Fact]
    public async Task AMergedCustomerIsNotCountedNorListed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var survivor = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var merged = await CreateActiveCustomerAsync(client, tenant.TenantId);
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            // Se deja activo a propósito: el reporte trae los dos estados, y lo que se prueba es el filtro de fusión.
            await using var update = new NpgsqlCommand(
                "UPDATE customers.customers SET merged_into_id = @survivor, merged_at = now(), version = version + 1 WHERE id = @merged",
                connection);
            update.Parameters.AddWithValue("survivor", survivor.Id);
            update.Parameters.AddWithValue("merged", merged.Id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var page = await client.GetFromJsonAsync<ReportPageDto<CustomerReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/customers", TestContext.Current.CancellationToken);
        var summary = await client.GetFromJsonAsync<CustomerReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/customers/summary", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(survivor.Id, Assert.Single(page.Items).CustomerId);
        Assert.Equal(1, page.Total);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.CustomerCount);
    }
}
