using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Infrastructure;
using Modules.Customers.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Una base migrada y un host para toda la clase. Cada prueba siembra sus propios clientes (ids nuevos) en el tenant de desarrollo.</summary>
public sealed class CustomerBulkDeleteApiDatabase : IAsyncLifetime
{
    private PostgreSqlContainer? _database;

    internal QepApiFactory Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        ConnectionString = _database.GetConnectionString();
        Factory = new QepApiFactory(ConnectionString);
        // El arranque del host migra todos los esquemas.
        using var client = Factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

/// <summary>
/// Spec 2026-11-10 (borrar clientes) §4.1: <c>POST /customers/bulk-delete</c>. El contrato con el frontend: tres listas que
/// siempre viajan (<c>deleted</c>, <c>blocked</c>, <c>notFound</c>) y un 422 con el mapa <c>errors</c> para la lista inválida.
/// Los clientes y las filas que los bloquean se siembran por SQL: la API de Customers no tiene cómo crear cotizaciones ni préstamos.
/// </summary>
public sealed class CustomerBulkDeleteApiTests(CustomerBulkDeleteApiDatabase fixture) : IClassFixture<CustomerBulkDeleteApiDatabase>
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly TimeSpan NoAnswerWindow = TimeSpan.FromMilliseconds(500);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient CreateDeleter() =>
        CreateClient(
            fixture.Factory, SubjectId, TenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage,
            CustomersPermissions.CustomerDelete);

    private static string BulkDeleteUrl(string tenantId = TenantId) => $"{CustomersUrl(tenantId)}/bulk-delete";

    [Fact]
    public async Task WithoutThePermissionItIs403()
    {
        var customer = await InsertCustomerAsync("Sin permiso");
        using var client = CreateClient(
            fixture.Factory, SubjectId, TenantId, CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage);

        var response = await client.PostAsJsonAsync(BulkDeleteUrl(), new { customerIds = new[] { customer } }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM customers.customers WHERE id = @id", ("id", customer)));
    }

    [Fact]
    public async Task TheResponseHasTheContractShape()
    {
        var deletable = await InsertCustomerAsync("Ana Borrable");
        var withQuotation = await InsertCustomerAsync("Bea Con Cotización");
        var invented = Guid.CreateVersion7();
        await SeedAsync(
            """
            INSERT INTO quotations.quotations (id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, subtotal,
                tax_percentage, tax_amount, discount_amount, total, created_by, updated_at, version)
            VALUES (gen_random_uuid(), @tenant, 'COT-' || @suffix, @customer, @member, 'Draft', now(), 0, 0, 0, 0, 0, @member, now(), 1);
            """,
            ("tenant", Tenant), ("customer", withQuotation), ("member", Guid.CreateVersion7()), ("suffix", withQuotation.ToString("N")[..12]));
        using var client = CreateDeleter();

        var response = await client.PostAsJsonAsync(
            BulkDeleteUrl(), new { customerIds = new[] { deletable, withQuotation, invented } }, Ct);

        var body = await BodyAsync(response, HttpStatusCode.OK);
        Assert.Equal([deletable], body.GetProperty("deleted").EnumerateArray().Select(id => id.GetGuid()));
        var blocked = Assert.Single(body.GetProperty("blocked").EnumerateArray());
        Assert.Equal(withQuotation, blocked.GetProperty("customerId").GetGuid());
        Assert.Equal("Bea Con Cotización", blocked.GetProperty("name").GetString());
        Assert.Equal(["quotations"], blocked.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal([invented], body.GetProperty("notFound").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{CustomersUrl()}/{deletable}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CustomersUrl()}/{withQuotation}", Ct)).StatusCode);
    }

    [Fact]
    public async Task AnEmptyListIs422WithTheErrorsMap()
    {
        using var client = CreateDeleter();

        var response = await client.PostAsJsonAsync(BulkDeleteUrl(), new { customerIds = Array.Empty<Guid>() }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await ProblemCodeAsync(response));
        Assert.Contains("CustomerIds", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task OneHundredAndOneIdsIs422()
    {
        using var client = CreateDeleter();
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.CreateVersion7()).ToArray();

        var response = await client.PostAsJsonAsync(BulkDeleteUrl(), new { customerIds = ids }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheDeletionIsAudited()
    {
        var customer = await InsertCustomerAsync("Ana Auditada");
        using var client = CreateDeleter();

        (await client.PostAsJsonAsync(BulkDeleteUrl(), new { customerIds = new[] { customer } }, Ct)).EnsureSuccessStatusCode();

        // La entrada puede llegar por el outbox: se espera hasta que aparezca.
        string? row = null;
        for (var attempt = 0; attempt < 40 && row is null; attempt++)
        {
            row = await TextAsync(
                """
                SELECT outcome || '|' || changed_fields::text FROM audit.entries
                WHERE action = 'customers.customer.deleted' AND resource_id = @id
                """,
                ("id", customer.ToString()));
            if (row is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            }
        }

        Assert.NotNull(row);
        Assert.StartsWith("success|", row);
        Assert.Contains("name=Ana Auditada", row);
    }

    // Quien escribe una referencia toma el candado compartido antes de comprobar al cliente: aquí otra conexión hace de
    // escritor y el borrado tiene que esperar a que confirme, y al despertar ver el préstamo.
    [Fact]
    public async Task ALoanCommittedWhileWaitingBlocksTheDeletion()
    {
        var customer = await InsertCustomerAsync("Cliente Con Carrera");
        using var client = CreateDeleter();
        // Calentamiento: la primera petición de un host nuevo tarda más que la ventana de espera, y sin él la prueba pasaría
        // aunque el candado no existiera (la petición "esperaría" por arranque en frío, no por el candado).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CustomersUrl()}/{customer}", Ct)).StatusCode);
        (await client.PostAsJsonAsync(BulkDeleteUrl(), new { customerIds = new[] { Guid.CreateVersion7() } }, Ct)).EnsureSuccessStatusCode();

        await using var writer = new NpgsqlConnection(fixture.ConnectionString);
        await writer.OpenAsync(Ct);
        await using var transaction = await writer.BeginTransactionAsync(Ct);
        await ExecuteLockAsync(writer, transaction, CustomerReferenceLock.Shared(Tenant, customer));
        await using (var insert = new NpgsqlCommand(
            """
            SET LOCAL session_replication_role = replica;
            INSERT INTO loans.loans (id, tenant_id, customer_id, name, type, principal_amount, outstanding_principal, currency,
                monthly_rate_percent, disbursed_on, cutoff_rule, status, last_generated_period_end, version, created_at, updated_at)
            VALUES (gen_random_uuid(), @tenant, @customer, 'Novasteel', 'InterestOnly', 1000000, 1000000, 'COP', 1.5,
                DATE '2026-08-15', 'EndOfMonth', 'Active', DATE '2026-08-31', 1, now(), now());
            """,
            writer, transaction))
        {
            insert.Parameters.AddWithValue("tenant", Tenant);
            insert.Parameters.AddWithValue("customer", customer);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var post = client.PostAsJsonAsync(BulkDeleteUrl(), new { customerIds = new[] { customer } }, Ct);

        Assert.NotSame(post, await Task.WhenAny(post, Task.Delay(NoAnswerWindow, Ct)));
        await transaction.CommitAsync(Ct);

        var body = await BodyAsync(await post, HttpStatusCode.OK);
        Assert.Empty(body.GetProperty("deleted").EnumerateArray());
        var blocked = Assert.Single(body.GetProperty("blocked").EnumerateArray());
        Assert.Equal(customer, blocked.GetProperty("customerId").GetGuid());
        Assert.Equal(["loans"], blocked.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM customers.customers WHERE id = @id", ("id", customer)));
    }

    // ---------------------------------------------------------------------------------------------------------------------

    private static async Task ExecuteLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, FormattableString lockSql)
    {
        // El helper devuelve un FormattableString: cada argumento pasa a ser un parámetro.
        var arguments = lockSql.GetArguments();
        var names = arguments.Select((_, index) => (object)$"@p{index}").ToArray();
        await using var command = new NpgsqlCommand(string.Format(CultureInfo.InvariantCulture, lockSql.Format, names), connection, transaction);
        for (var index = 0; index < arguments.Length; index++)
        {
            command.Parameters.AddWithValue($"p{index}", arguments[index]!);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> InsertCustomerAsync(string name)
    {
        var id = Guid.CreateVersion7();
        await SeedAsync(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at,
                completeness)
            VALUES (@id, @tenant, @name, true, false, false, 1, now(), now(), 'Incomplete');
            """,
            ("id", id), ("tenant", Tenant), ("name", name));
        return id;
    }

    // replica: la fila de otro módulo se siembra sin las FK hacia filas que la prueba no necesita (miembro, empresa).
    private Task SeedAsync(string sql, params (string Name, object? Value)[] parameters) =>
        ExecuteAsync("SET session_replication_role = replica;\n" + sql, parameters);

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task<string?> TextAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
