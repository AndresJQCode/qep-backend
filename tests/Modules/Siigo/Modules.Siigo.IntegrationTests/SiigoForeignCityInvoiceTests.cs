using System.Text.Json;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-26: facturar en Siigo a un cliente de otro país, de punta a punta con el host entero y el doble <see cref="FakeSiigoApi"/>.
/// Una ciudad que está en la lista de Siigo sale con sus tres códigos; una que no, queda en <c>customer_city_unlinked</c> y en la
/// bandeja del operador, y sólo la vista previa y la emisión la anotan (la disponibilidad no). Ninguna prueba llama a Siigo.
/// </summary>
public sealed class SiigoForeignCityInvoiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string? Status, string? ErrorCode) Outcome(JsonElement item) =>
        (item.GetProperty("status").GetString(), item.GetProperty("errorCode").GetString());

    private static string[] MissingCodes(JsonElement missing) =>
        [.. missing.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : value.GetProperty("code").GetString()!)];

    private static Task<long> CountAsync(InvoicingWorld world, string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<long>(world.ConnectionString, sql, parameters);

    private static Task<long> OccurrencesOfTheOrderAsync(InvoicingWorld world) =>
        CountAsync(
            world,
            "SELECT count(*) FROM geography.unresolved_foreign_city_occurrences WHERE order_id = @order AND tenant_id = @tenant",
            ("order", world.OrderId),
            ("tenant", world.TenantId));

    private static IReadOnlyList<FakeSiigoCall> CustomerPosts(InvoicingWorld world) =>
        [.. world.Siigo.Calls.Where(call => call.Path == "/v1/customers" && call.Method == HttpMethod.Post)];

    // El operador vincula «Miamy» a Miami (Us / 09 / 1): la fila de sinónimo que deja la consola, escrita directo.
    private static Task<long> LinkMiamyAsync(InvoicingWorld world) =>
        CountAsync(
            world,
            """
            WITH inserted AS (
                INSERT INTO geography.foreign_city_synonyms
                    (id, country_code, normalized_text, display_text, billing_city_id, created_by, created_at, updated_by, updated_at, version)
                SELECT gen_random_uuid(), 'US', 'MIAMY', 'Miamy', id, @by, now(), @by, now(), 1
                FROM geography.billing_cities
                WHERE country_code = 'US' AND state_code = '09' AND city_code = '1'
                RETURNING 1)
            SELECT count(*) FROM inserted
            """,
            ("by", world.Tenant.OwnerUserId));

    [Fact]
    public async Task AForeignCustomerWhoseCityIsInTheListIsInvoicedWithTheSiigoCodes()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miami"));

        var rows = await IssueRowsThroughBatchAsync(world);

        Assert.Equal("Created", rows[0].GetProperty("status").GetString());
        using var customer = JsonDocument.Parse(CustomerPosts(world).Single().Body!);
        var city = customer.RootElement.GetProperty("address").GetProperty("city");
        Assert.Equal(
            ("Us", "09", "1"),
            (city.GetProperty("country_code").GetString(), city.GetProperty("state_code").GetString(), city.GetProperty("city_code").GetString()));
        Assert.Equal(
            ("50", "123456789"),
            (customer.RootElement.GetProperty("id_type").GetString(), customer.RootElement.GetProperty("identification").GetString()));
    }

    [Fact]
    public async Task AnUnlinkedCityShowsInThePreviewAndIsAnnotatedOncePerOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miamy"));

        var first = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/preview", Ct));
        var second = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/preview", Ct));

        foreach (var preview in new[] { first, second })
        {
            var codes = MissingCodes(preview.GetProperty("missing"));
            Assert.Contains("customer_city_unlinked", codes);
            Assert.DoesNotContain("customer_city_missing", codes);
        }

        Assert.Equal(
            1,
            await CountAsync(
                world, "SELECT count(*) FROM geography.unresolved_foreign_cities WHERE country_code = 'US' AND normalized_text = 'MIAMY'"));
        Assert.Equal(1, await OccurrencesOfTheOrderAsync(world));
    }

    [Fact]
    public async Task TheAvailabilityCheckNeverAnnotates()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miamy"));

        var response = await world.Client.GetAsync(world.BaseRoute + "/availability", Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CountAsync(world, "SELECT count(*) FROM geography.unresolved_foreign_cities"));
    }

    [Fact]
    public async Task AnUnlinkedCityIsSkippedByTheBatchAndAnnotated()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miamy"));

        var item = await IssueThroughBatchAsync(world);

        Assert.Equal(("Skipped", "siigo.invoice.not_ready"), Outcome(item));
        Assert.Contains("customer_city_unlinked", MissingCodes(item.GetProperty("missing")));
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/customers" or "/v1/invoices");
        Assert.Equal(1, await OccurrencesOfTheOrderAsync(world));
    }

    // Review Focus 3: la misma orden vista muchas veces (vista previa y luego tanda) deja una sola ocurrencia, y la fila no se mueve.
    [Fact]
    public async Task APreviewAndThenTheBatchOfTheSameOrderLeaveOneOccurrenceAndTheRowStill()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miamy"));
        const string rowSql =
            "SELECT version::text || '|' || last_seen_at::text || '|' || updated_at::text FROM geography.unresolved_foreign_cities";

        await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/preview", Ct));
        var afterPreview = await ScalarAsync<string>(world.ConnectionString, rowSql);
        var item = await IssueThroughBatchAsync(world);

        Assert.Equal(("Skipped", "siigo.invoice.not_ready"), Outcome(item));
        Assert.Equal(1, await CountAsync(world, "SELECT count(*) FROM geography.unresolved_foreign_cities"));
        Assert.Equal(1, await OccurrencesOfTheOrderAsync(world));
        Assert.Equal(afterPreview, await ScalarAsync<string>(world.ConnectionString, rowSql));
    }

    [Fact]
    public async Task OnceTheOperatorLinksTheTextTheNextIssueInvoicesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miamy"));
        Assert.Equal(1, await LinkMiamyAsync(world));

        var rows = await IssueRowsThroughBatchAsync(world);

        Assert.Equal("Created", rows[0].GetProperty("status").GetString());
        using var customer = JsonDocument.Parse(CustomerPosts(world).Single().Body!);
        var city = customer.RootElement.GetProperty("address").GetProperty("city");
        Assert.Equal(
            ("Us", "09", "1"),
            (city.GetProperty("country_code").GetString(), city.GetProperty("state_code").GetString(), city.GetProperty("city_code").GetString()));
        Assert.Equal(0, await CountAsync(world, "SELECT count(*) FROM geography.unresolved_foreign_cities"));
    }
}
