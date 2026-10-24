using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec 2026-10-24 §3.2–§3.4 contra la base: lo que la fusión lee y escribe en <c>customers</c> sin pasar por el agregado
/// (<see cref="ICustomerMergeStore"/>) y la transacción explícita de <see cref="ICustomersUnitOfWork"/>. Los clientes del tenant
/// de las pruebas, sus notas y sus direcciones entran por la API; lo del otro tenant y lo que necesita fechas exactas, por SQL.
/// El alta por la API copia el domicilio a la libreta como principal, con el nombre del cliente: esa es la principal de cada
/// cliente en estas pruebas.
/// </summary>
public sealed class CustomerMergeStoreTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);

    private sealed class Fixture(QepApiFactory factory, HttpClient client, Guid cityId, Guid classificationId, string connectionString)
    {
        private int _sequence;

        public QepApiFactory Factory { get; } = factory;

        public HttpClient Client { get; } = client;

        public Guid CityId { get; } = cityId;

        public string ConnectionString { get; } = connectionString;

        public async Task<Guid> CreateCustomerAsync(string name)
        {
            _sequence++;
            var customer = await CustomersApiHarness.CreateCustomerAsync(
                Client, CityId, classificationId, name, $"900.{_sequence:000}.000-1");
            return customer.Id;
        }

        public async Task AddNoteAsync(Guid customerId, string body)
        {
            var response = await Client.PostAsJsonAsync(
                $"{CustomersUrl()}/{customerId}/notes", new { body }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        public Task<CustomerResponse> AddAddressAsync(Guid customerId, bool isPrincipal, string name) =>
            CustomersApiHarness.AddAddressAsync(Client, customerId, CityId, isPrincipal, name);

        public async Task<T> WithStoreAsync<T>(Func<ICustomerMergeStore, Task<T>> action)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ICustomerMergeStore>());
        }

        public async Task WithStoreAsync(Func<ICustomerMergeStore, Task> action)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<ICustomerMergeStore>());
        }
    }

    private static async Task<Fixture> StartAsync(QepApiFactory factory, string connectionString)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        return new Fixture(factory, client, city.CityId, classification.Id, connectionString);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<object?[]>> RowsAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    // Incompleto basta para llevar un documento y ser dueño de notas y direcciones a nivel de tabla.
    private static async Task<Guid> InsertCustomerAsync(
        string connectionString, Guid tenant, string name, string? type = null, string? number = null,
        DateTimeOffset? createdAt = null, Guid? mergedInto = null)
    {
        var id = Guid.CreateVersion7();
        var at = createdAt ?? DateTimeOffset.UtcNow;
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness,
                 identification_type, identification_number, merged_into_id, merged_at)
            VALUES (@id, @tenant, @name, @active, false, false, 1, @at, @at, 'Incomplete', @type, @number, @merged,
                    CASE WHEN @merged::uuid IS NULL THEN NULL ELSE now() END)
            """,
            ("id", id), ("tenant", tenant), ("name", name), ("active", mergedInto is null), ("at", at),
            ("type", type), ("number", number), ("merged", mergedInto));
        return id;
    }

    private static Task InsertNoteAsync(string connectionString, Guid tenant, Guid customerId, string body) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO customers.customer_notes (id, tenant_id, customer_id, body, author_user_id, version, created_at, updated_at)
            VALUES (@id, @tenant, @customer, @body, @author, 1, now(), now())
            """,
            ("id", Guid.CreateVersion7()), ("tenant", tenant), ("customer", customerId), ("body", body),
            ("author", Guid.Parse(OtherSubjectId)));

    private static Task InsertAddressAsync(string connectionString, Guid customerId, Guid cityId, bool isPrincipal) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO customers.customer_addresses (id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
            VALUES (@id, @customer, 'Ajena', 'Calle 1', NULL, @city, @principal, now(), now())
            """,
            ("id", Guid.CreateVersion7()), ("customer", customerId), ("city", cityId), ("principal", isPrincipal));

    private static async Task<List<(string Body, Guid CustomerId, long Version)>> NotesAsync(string connectionString, string bodyPrefix) =>
        (await RowsAsync(
            connectionString,
            "SELECT body, customer_id, version FROM customers.customer_notes WHERE body LIKE @prefix ORDER BY body",
            ("prefix", bodyPrefix + "%")))
        .Select(row => ((string)row[0]!, (Guid)row[1]!, (long)row[2]!))
        .ToList();

    private static async Task<List<(string Name, Guid CustomerId, bool IsPrincipal)>> AddressesAsync(
        string connectionString, IReadOnlyCollection<Guid> customerIds) =>
        (await RowsAsync(
            connectionString,
            "SELECT name, customer_id, is_principal FROM customers.customer_addresses WHERE customer_id = ANY(@ids) ORDER BY name",
            ("ids", customerIds.ToArray())))
        .Select(row => ((string)row[0]!, (Guid)row[1]!, (bool)row[2]!))
        .ToList();

    private static async Task<Guid?> MergedIntoAsync(string connectionString, Guid customerId) =>
        (Guid?)(await RowsAsync(
            connectionString, "SELECT merged_into_id FROM customers.customers WHERE id = @id", ("id", customerId)))[0][0];

    [Fact]
    public async Task MoveOwnedMovesNotesAndAddressesAndKeepsTheSurvivorsPrincipal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var survivor = await fixture.CreateCustomerAsync("Quien Queda");
        var merged = await fixture.CreateCustomerAsync("El Fusionado");
        var bystander = await fixture.CreateCustomerAsync("Otro del Tenant");
        await fixture.AddAddressAsync(merged, isPrincipal: false, "B secundaria del fusionado");
        await fixture.AddNoteAsync(merged, "nota B1");
        await fixture.AddNoteAsync(merged, "nota B2");
        await fixture.AddNoteAsync(bystander, "nota C1");
        var versionsBefore = (await NotesAsync(fixture.ConnectionString, "nota ")).ToDictionary(note => note.Body, note => note.Version);

        await fixture.WithStoreAsync(store =>
            store.MoveOwnedAsync(Tenant, survivor, [merged], TestContext.Current.CancellationToken));

        var notes = await NotesAsync(fixture.ConnectionString, "nota ");
        Assert.Equal(
            [("nota B1", survivor), ("nota B2", survivor), ("nota C1", bystander)],
            notes.Select(note => (note.Body, note.CustomerId)).ToArray());
        Assert.All(notes, note => Assert.Equal(versionsBefore[note.Body], note.Version));

        var addresses = await AddressesAsync(fixture.ConnectionString, [survivor, merged, bystander]);
        Assert.Equal(
            [
                ("B secundaria del fusionado", survivor, false),
                ("El Fusionado", survivor, false),
                ("Otro del Tenant", bystander, true),
                ("Quien Queda", survivor, true)
            ],
            addresses.ToArray());
    }

    [Fact]
    public async Task MoveOwnedNeverTouchesAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var survivor = await fixture.CreateCustomerAsync("Quien Queda");
        var foreign = await InsertCustomerAsync(fixture.ConnectionString, OtherTenant, "Ajeno");
        await InsertNoteAsync(fixture.ConnectionString, OtherTenant, foreign, "nota ajena");
        await InsertAddressAsync(fixture.ConnectionString, foreign, fixture.CityId, isPrincipal: true);

        await fixture.WithStoreAsync(store =>
            store.MoveOwnedAsync(Tenant, survivor, [foreign], TestContext.Current.CancellationToken));

        Assert.Equal([foreign], (await NotesAsync(fixture.ConnectionString, "nota ajena")).Select(note => note.CustomerId).ToArray());
        Assert.Equal(
            [("Ajena", foreign, true), ("Quien Queda", survivor, true)],
            (await AddressesAsync(fixture.ConnectionString, [survivor, foreign])).ToArray());
    }

    [Fact]
    public async Task RepointRewritesTheChainOnlyInTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var survivor = await fixture.CreateCustomerAsync("A Quien Queda");
        var merged = await fixture.CreateCustomerAsync("B El Fusionado");
        var chained = await fixture.CreateCustomerAsync("C Ya Fusionado en B");
        var unrelated = await fixture.CreateCustomerAsync("D Fusionado en otro");
        var unrelatedSurvivor = await fixture.CreateCustomerAsync("E Quedó de D");
        await MarkMergedAsync(fixture.ConnectionString, chained, merged);
        await MarkMergedAsync(fixture.ConnectionString, unrelated, unrelatedSurvivor);
        var foreignTarget = await InsertCustomerAsync(fixture.ConnectionString, OtherTenant, "Ajeno que quedó");
        var foreignChained = await InsertCustomerAsync(fixture.ConnectionString, OtherTenant, "Ajeno fusionado", mergedInto: foreignTarget);

        var chain = await fixture.WithStoreAsync(store =>
            store.FindIdsMergedIntoAsync(Tenant, [merged, foreignTarget], TestContext.Current.CancellationToken));
        Assert.Equal([chained], chain.ToArray());

        await fixture.WithStoreAsync(store =>
            store.RepointMergedIntoAsync(Tenant, [chained, foreignChained], survivor, TestContext.Current.CancellationToken));

        Assert.Equal(survivor, await MergedIntoAsync(fixture.ConnectionString, chained));
        Assert.Equal(foreignTarget, await MergedIntoAsync(fixture.ConnectionString, foreignChained));
        Assert.Equal(unrelatedSurvivor, await MergedIntoAsync(fixture.ConnectionString, unrelated));
        Assert.Null(await MergedIntoAsync(fixture.ConnectionString, merged));
    }

    [Fact]
    public async Task CountOwnedCountsNotesAndAddresses()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var first = await fixture.CreateCustomerAsync("Primero");
        var second = await fixture.CreateCustomerAsync("Segundo");
        var bystander = await fixture.CreateCustomerAsync("Fuera del conteo");
        await fixture.AddNoteAsync(first, "nota 1");
        await fixture.AddNoteAsync(first, "nota 2");
        await fixture.AddNoteAsync(second, "nota 3");
        await fixture.AddNoteAsync(bystander, "nota 4");
        await fixture.AddAddressAsync(first, isPrincipal: true, "Dirección 1");
        await fixture.AddAddressAsync(bystander, isPrincipal: true, "Dirección 2");
        var foreign = await InsertCustomerAsync(fixture.ConnectionString, OtherTenant, "Ajeno");
        await InsertNoteAsync(fixture.ConnectionString, OtherTenant, foreign, "nota ajena");
        await InsertAddressAsync(fixture.ConnectionString, foreign, fixture.CityId, isPrincipal: true);

        var counts = await fixture.WithStoreAsync(store =>
            store.CountOwnedAsync(Tenant, [first, second, foreign], TestContext.Current.CancellationToken));
        var none = await fixture.WithStoreAsync(store =>
            store.CountOwnedAsync(Tenant, [], TestContext.Current.CancellationToken));

        // Direcciones: la principal del alta y «Dirección 1» del primero, y la principal del alta del segundo.
        Assert.Equal(new CustomerMergeOwnedCounts(Notes: 3, Addresses: 3), counts);
        Assert.Equal(new CustomerMergeOwnedCounts(Notes: 0, Addresses: 0), none);
    }

    [Fact]
    public async Task FindSurvivorIdFollowsTheMergeAndIsNullOtherwise()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var survivor = await fixture.CreateCustomerAsync("Quien Queda");
        var merged = await fixture.CreateCustomerAsync("El Fusionado");
        await MarkMergedAsync(fixture.ConnectionString, merged, survivor);
        var ct = TestContext.Current.CancellationToken;

        var (ofMerged, ofSurvivor, fromOtherTenant, unknown) = await fixture.WithStoreAsync(async store => (
            await store.FindSurvivorIdAsync(Tenant, merged, ct),
            await store.FindSurvivorIdAsync(Tenant, survivor, ct),
            await store.FindSurvivorIdAsync(OtherTenant, merged, ct),
            await store.FindSurvivorIdAsync(Tenant, Guid.CreateVersion7(), ct)));

        Assert.Equal(survivor, ofMerged);
        Assert.Null(ofSurvivor);
        Assert.Null(fromOtherTenant);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task FindSurvivorIdByIdentificationUsesTheOldestMerged()
    {
        const string Number = "830111111";
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Arranca el host, que migra la base.
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var connectionString = database.GetConnectionString();
        var now = DateTimeOffset.UtcNow;
        var oldestSurvivor = await InsertCustomerAsync(connectionString, Tenant, "Quedó del más viejo");
        var newestSurvivor = await InsertCustomerAsync(connectionString, Tenant, "Quedó del más nuevo");
        var foreignSurvivor = await InsertCustomerAsync(connectionString, OtherTenant, "Quedó en otro tenant");
        // El dueño vivo del documento es todavía más viejo: no cuenta, la búsqueda es sólo entre fusionados.
        await InsertCustomerAsync(connectionString, Tenant, "Dueño vivo", "Nit", Number, now.AddDays(-30));
        await InsertCustomerAsync(connectionString, OtherTenant, "Ajeno más viejo", "Nit", Number, now.AddDays(-20), foreignSurvivor);
        await InsertCustomerAsync(connectionString, Tenant, "Fusionado nuevo", "Nit", Number, now.AddDays(-5), newestSurvivor);
        await InsertCustomerAsync(connectionString, Tenant, "Fusionado viejo", "Nit", Number, now.AddDays(-10), oldestSurvivor);
        await InsertCustomerAsync(connectionString, Tenant, "Otro tipo", "Cc", Number, now.AddDays(-15), newestSurvivor);
        var ct = TestContext.Current.CancellationToken;

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICustomerMergeStore>();

        Assert.Equal(oldestSurvivor, await store.FindSurvivorIdByIdentificationAsync(Tenant, IdentificationType.Nit, Number, ct));
        Assert.Equal(foreignSurvivor, await store.FindSurvivorIdByIdentificationAsync(OtherTenant, IdentificationType.Nit, Number, ct));
        Assert.Null(await store.FindSurvivorIdByIdentificationAsync(Tenant, IdentificationType.Nit, "830111112", ct));
        Assert.Null(await store.FindSurvivorIdByIdentificationAsync(Tenant, IdentificationType.Ce, Number, ct));
    }

    [Fact]
    public async Task ARolledBackTransactionLeavesEverythingAsItWas()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var fixture = await StartAsync(factory, database.GetConnectionString());
        using var _client = fixture.Client;
        var survivor = await fixture.CreateCustomerAsync("Quien Queda");
        var merged = await fixture.CreateCustomerAsync("El Fusionado");
        await fixture.AddNoteAsync(merged, "nota B1");
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<ICustomerMergeStore>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            await store.MoveOwnedAsync(Tenant, survivor, [merged], ct);
            // Dentro de la transacción ya se ve movido; sin Commit, el Dispose lo deshace.
            Assert.Equal(new CustomerMergeOwnedCounts(0, 0), await store.CountOwnedAsync(Tenant, [merged], ct));
        }

        Assert.Equal([merged], (await NotesAsync(fixture.ConnectionString, "nota B1")).Select(note => note.CustomerId).ToArray());
        Assert.Equal(
            [("El Fusionado", merged, true), ("Quien Queda", survivor, true)],
            (await AddressesAsync(fixture.ConnectionString, [survivor, merged])).ToArray());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<ICustomerMergeStore>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            await store.MoveOwnedAsync(Tenant, survivor, [merged], ct);
            await transaction.CommitAsync(ct);
        }

        Assert.Equal([survivor], (await NotesAsync(fixture.ConnectionString, "nota B1")).Select(note => note.CustomerId).ToArray());
        Assert.Equal(
            [("El Fusionado", survivor, false), ("Quien Queda", survivor, true)],
            (await AddressesAsync(fixture.ConnectionString, [survivor, merged])).ToArray());
    }
}
