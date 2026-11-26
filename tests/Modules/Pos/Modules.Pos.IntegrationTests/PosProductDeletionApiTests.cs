using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// El <c>DELETE</c> de producto visto desde la caja (spec 2026-11-08): una venta cobrada es una foto de lo que se
/// vendió, así que sus líneas no se tocan aunque el producto ya no exista.
/// </summary>
public sealed class PosProductDeletionApiTests
{
    [Fact]
    public async Task ASaleKeepsItsLinesAfterTheProductIsDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var saleId = Guid.CreateVersion7();
        var created = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(saleId, session.Id, world.PlainLine(2m), PosWorld.CashPayment(50_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var linesBefore = await SaleLinesAsync(database, saleId);
        var saleBefore = await world.Admin.GetStringAsync($"{world.Url}/sales/{saleId}", TestContext.Current.CancellationToken);
        var productUrl = $"/api/v1/tenants/{world.Tenant.TenantId}/catalog/products/{world.Shampoo}";
        var product = await world.Tenant.Seeder.GetFromJsonAsync<JsonElement>(productUrl, TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Delete, productUrl);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{product.GetProperty("version").GetInt64()}\"");
        var response = await world.Tenant.Seeder.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var linesAfter = await SaleLinesAsync(database, saleId);
        Assert.Equal(("SH-400", "Shampoo 400 ml", 2m), (linesAfter[0].Code, linesAfter[0].Name, linesAfter[0].Quantity));
        Assert.Equal(linesBefore, linesAfter);
        var saleAfter = await world.Admin.GetStringAsync($"{world.Url}/sales/{saleId}", TestContext.Current.CancellationToken);
        Assert.Equal(saleBefore, saleAfter);
    }

    private sealed record SaleLineRow(
        string Code, string Name, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, decimal TaxAmount, decimal Subtotal);

    private static async Task<IReadOnlyList<SaleLineRow>> SaleLinesAsync(PostgreSqlContainer database, Guid saleId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT product_code, product_name, quantity, unit_price, discount_amount, tax_amount, subtotal
            FROM pos.sale_lines WHERE sale_id = @id ORDER BY position
            """,
            connection);
        command.Parameters.AddWithValue("id", saleId);
        var rows = new List<SaleLineRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new SaleLineRow(
                reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6)));
        }

        Assert.NotEmpty(rows);
        return rows;
    }
}
