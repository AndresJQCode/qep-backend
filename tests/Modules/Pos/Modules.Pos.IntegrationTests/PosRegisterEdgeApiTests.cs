using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Bordes de los endpoints de B12: lo que el recorrido feliz no toca (precondiciones, 403 del
/// preview, validación) y las consultas de listado de B11, que nunca habían corrido contra Postgres.
/// </summary>
public sealed class PosRegisterEdgeApiTests
{
    // El lector de códigos suele cerrar con un espacio; sin recorte daría un 404 falso.
    [Fact]
    public async Task ByCodeTrimsTheScannedCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);

        var response = await world.Admin.GetAsync($"{world.Url}/products/by-code?code=SH-400%20%20", TestContext.Current.CancellationToken);
        var product = await response.Content.ReadFromJsonAsync<PosProductResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("SH-400", product!.Code);
    }

    [Fact]
    public async Task ClosingWithoutIfMatchIsPreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        var response = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sessions/{session.Id}/close", new { countedCash = 100_000m }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Contains("precondition.if_match_required", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingWithAStaleVersionIsPreconditionFailed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var response = await CloseAsync(world, session.Id, 1, 100_000m);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task PreviewIsForbiddenWithoutSaleCreateAndInAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var other = await PosWorld.ArrangeAsync(factory, database);
        using var readOnly = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, PosPermissions.SaleRead);

        var withoutPermission = await readOnly.PostAsJsonAsync(
            $"{world.Url}/sales/preview", new { lines = world.PlainLine() }, TestContext.Current.CancellationToken);
        var wrongTenant = await world.Admin.PostAsJsonAsync(
            $"{other.Url}/sales/preview", new { lines = world.PlainLine() }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongTenant.StatusCode);
    }

    [Fact]
    public async Task PreviewRejectsAnEmptyCartAndMoreThan200Lines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);

        var empty = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/preview", new { lines = Array.Empty<object>() }, TestContext.Current.CancellationToken);
        var missing = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/preview", new { }, TestContext.Current.CancellationToken);
        var tooMany = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/preview",
            new { lines = Enumerable.Range(0, 201).Select(_ => new { productId = world.Shampoo, quantity = 1m, discountPercentage = 0m }).ToArray() },
            TestContext.Current.CancellationToken);

        Assert.All(new[] { empty, missing, tooMany }, response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.Contains("validation.failed", await tooMany.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // Carry B8: un nombre en blanco no puede dejar el GUID congelado como nombre del cajero.
    [Fact]
    public async Task TheCashierNameFallsBackToTheEmailNeverToTheMembershipId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await ExecuteSqlAsync(
            database,
            "UPDATE tenancy.memberships SET display_name = NULL WHERE tenant_id = @tenantId AND user_id = @userId",
            ("tenantId", world.Tenant.TenantId), ("userId", world.Tenant.OwnerUserId));

        var context = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);

        Assert.Contains("@", context!.Cashier.Name, StringComparison.Ordinal);
        Assert.NotEqual(context.Cashier.MemberId.ToString(), context.Cashier.Name);
    }

    // B11 nunca ejecutó estas consultas: filtros por caja, estado, número y fecha contra Postgres.
    [Fact]
    public async Task SalesAndSessionsListsRunAgainstPostgresWithTheirFilters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        await PostSaleAsync(world, firstId, session.Id);
        await PostSaleAsync(world, secondId, session.Id);
        var voided = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{secondId}/void", new { reason = "Error de digitación" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var all = await GetPageAsync<PosSaleListItemResponse>(world, $"/sales?sessionId={session.Id}&from={today}&to={today}");
        var completed = await GetPageAsync<PosSaleListItemResponse>(world, "/sales?status=Completed");
        var byNumber = await GetPageAsync<PosSaleListItemResponse>(world, "/sales?number=POS-000002");
        var open = await GetPageAsync<PosSessionSummaryResponse>(world, "/sessions?status=Open");
        var dated = await GetPageAsync<PosSessionSummaryResponse>(world, $"/sessions?from={today}&to={today}");
        var closedList = await GetPageAsync<PosSessionSummaryResponse>(world, "/sessions?status=Closed");

        Assert.Equal(2, all.Total);
        Assert.Equal("Voided", Assert.Single(byNumber.Items).Status);
        Assert.Equal(firstId, Assert.Single(completed.Items).Id);
        Assert.Equal(session.Id, Assert.Single(open.Items).Id);
        Assert.Equal(session.Id, Assert.Single(dated.Items).Id);
        Assert.Empty(closedList.Items);
    }

    // Ruling B11: empate de marca de tiempo → desempate por Id, o la paginación repite y salta filas.
    [Fact]
    public async Task ListsBreakTimestampTiesById()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);

        Guid sessionId = Guid.Empty;
        for (var index = 0; index < 3; index++)
        {
            var session = await world.OpenSessionAsync(world.Admin);
            sessionId = session.Id;
            await PostSaleAsync(world, Guid.CreateVersion7(), session.Id);
            Assert.Equal(HttpStatusCode.OK, (await CloseAsync(world, session.Id, 2, 100_000m)).StatusCode);
        }

        await ExecuteSqlAsync(database, "UPDATE pos.sales SET created_at = '2026-10-07T15:00:00Z' WHERE tenant_id = @t", ("t", world.Tenant.TenantId));
        await ExecuteSqlAsync(database, "UPDATE pos.cash_sessions SET opened_at = '2026-10-07T14:00:00Z' WHERE tenant_id = @t", ("t", world.Tenant.TenantId));

        var expectedSales = await IdsAsync(database, "SELECT id FROM pos.sales WHERE tenant_id = @t ORDER BY id DESC", world.Tenant.TenantId);
        var expectedSessions = await IdsAsync(database, "SELECT id FROM pos.cash_sessions WHERE tenant_id = @t ORDER BY id DESC", world.Tenant.TenantId);
        var pagedSales = new List<Guid>();
        var pagedSessions = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            pagedSales.Add((await GetPageAsync<PosSaleListItemResponse>(world, $"/sales?page={page}&pageSize=1")).Items.Single().Id);
            pagedSessions.Add((await GetPageAsync<PosSessionSummaryResponse>(world, $"/sessions?page={page}&pageSize=1")).Items.Single().Id);
        }

        Assert.NotEqual(Guid.Empty, sessionId);
        Assert.Equal(expectedSales, pagedSales);
        Assert.Equal(expectedSessions, pagedSessions);
    }

    // Defensa en profundidad: la consulta por lote de cajas lleva el tenant. Una venta que apunta a
    // la caja de otro tenant (dato corrupto) no puede filtrar su cajero: la lista falla, no la expone.
    [Fact]
    public async Task TheSessionBatchLookupIsScopedToTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var other = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var foreign = await other.OpenSessionAsync(other.Admin);
        await PostSaleAsync(world, Guid.CreateVersion7(), session.Id);
        await ExecuteSqlAsync(
            database,
            "UPDATE pos.sales SET cash_session_id = @foreign WHERE tenant_id = @t",
            ("foreign", foreign.Id), ("t", world.Tenant.TenantId));

        var response = await world.Admin.GetAsync($"{world.Url}/sales", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static Task<HttpResponseMessage> CloseAsync(PosWorld world, Guid sessionId, long version, decimal counted)
    {
        var close = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{sessionId}/close")
        {
            Content = JsonContent.Create(new { countedCash = counted }),
        };
        close.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return world.Admin.SendAsync(close, TestContext.Current.CancellationToken);
    }

    private static async Task PostSaleAsync(PosWorld world, Guid id, Guid sessionId)
    {
        var response = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(id, sessionId, world.PlainLine(), PosWorld.CashPayment(20_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<PosPage<T>> GetPageAsync<T>(PosWorld world, string path)
    {
        var response = await world.Admin.GetAsync($"{world.Url}{path}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PosPage<T>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private static async Task<List<Guid>> IdsAsync(PostgreSqlContainer database, string sql, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenantId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
