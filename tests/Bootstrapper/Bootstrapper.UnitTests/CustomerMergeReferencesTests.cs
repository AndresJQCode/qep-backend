using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Fusión de clientes, FC-03 (spec 2026-10-24 §4): el SQL de <see cref="CustomerMergeReferences"/> contra PostgreSQL.
///
/// **Las tablas de los otros módulos se crean acá, mínimas, y no por las migraciones**, como en
/// <see cref="CatalogVariantUsageCheckerTests"/>: cada réplica lleva sólo las columnas que el adaptador lee o escribe, más
/// la <c>version</c> de las tablas que la tienen (para afirmar que sólo la de campañas sube, rulings C1 y F15) y la foto
/// <c>customer_name</c> de <c>pos.sales</c> (para afirmar que no cambia). Esta prueba no se entera si una migración nombra
/// distinto una columna: eso lo cubre la gemela contra el esquema migrado,
/// <c>CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema</c> (FC-09).
/// </summary>
public sealed class CustomerMergeReferencesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    // Las nueve columnas de la lista, con una fila de B y otra de C (dos fusionados a la vez): todas terminan con A. Las
    // campañas, la décima, tienen sus propias pruebas.
    [Fact]
    public async Task EveryTableOfTheListMovesToTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        foreach (var customer in new[] { merged, other })
        {
            await AddReferencesAsync(database, TenantId, customer);
        }

        await using var customers = CustomersFor(database);
        await new CustomerMergeReferences(customers).MoveAsync(
            TenantId, survivor, [merged, other], TestContext.Current.CancellationToken);

        foreach (var (table, column) in ReferenceColumns)
        {
            Assert.Equal([survivor, survivor], await CustomerIdsAsync(database, table, column, TenantId));
        }
    }

    // Las filas del mismo cliente en otro tenant no se mueven, aunque el id coincida.
    [Fact]
    public async Task AnotherTenantsRowsStayPut()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AddReferencesAsync(database, OtherTenantId, merged);
        var otherCampaign = await AddCampaignAsync(database, OtherTenantId, "Draft", [merged]);

        await using var customers = CustomersFor(database);
        await new CustomerMergeReferences(customers).MoveAsync(
            TenantId, survivor, [merged], TestContext.Current.CancellationToken);

        foreach (var (table, column) in ReferenceColumns)
        {
            Assert.Equal([merged], await CustomerIdsAsync(database, table, column, OtherTenantId));
        }

        await AssertCampaignAsync(database, otherCampaign, [merged], 1);
    }

    // §4: el que queda reemplaza al fusionado y el DISTINCT quita la repetición. El orden de la audiencia se conserva (la
    // primera aparición de cada uno) y la versión sube una vez (ruling C1).
    [Fact]
    public async Task ADraftOrScheduledAudienceSwapsTheMergedAndDropsTheRepeat()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged, before, after) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var both = await AddCampaignAsync(database, TenantId, "Draft", [survivor, merged]);
        var onlyMerged = await AddCampaignAsync(database, TenantId, "Scheduled", [merged]);
        var ordered = await AddCampaignAsync(database, TenantId, "Draft", [before, merged, survivor, after]);
        var untouched = await AddCampaignAsync(database, TenantId, "Draft", [before]);

        await using var customers = CustomersFor(database);
        await new CustomerMergeReferences(customers).MoveAsync(
            TenantId, survivor, [merged], TestContext.Current.CancellationToken);

        await AssertCampaignAsync(database, both, [survivor], 2);
        await AssertCampaignAsync(database, onlyMerged, [survivor], 2);
        await AssertCampaignAsync(database, ordered, [before, survivor, after], 2);
        await AssertCampaignAsync(database, untouched, [before], 1);
    }

    // §4: una campaña que ya congeló su audiencia (o terminó) no se toca, ni su versión.
    [Theory]
    [InlineData("Sending")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public async Task ASendingOrCompletedCampaignKeepsItsAudience(string status)
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var campaign = await AddCampaignAsync(database, TenantId, status, [survivor, merged]);

        await using var customers = CustomersFor(database);
        await new CustomerMergeReferences(customers).MoveAsync(
            TenantId, survivor, [merged], TestContext.Current.CancellationToken);

        await AssertCampaignAsync(database, campaign, [survivor, merged], 1);
    }

    // §4: los destinatarios son la foto de un envío; moverlos chocaría con IX_campaign_recipients_campaign_customer.
    [Fact]
    public async Task CampaignRecipientsAreNeverTouched()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var campaign = await AddCampaignAsync(database, TenantId, "Completed", [survivor, merged]);
        await ExecuteAsync(database, """
            INSERT INTO messaging.campaign_recipients (campaign_id, tenant_id, customer_id)
            VALUES (@campaign, @tenant, @survivor), (@campaign, @tenant, @merged)
            """, ("campaign", campaign), ("tenant", TenantId), ("survivor", survivor), ("merged", merged));

        await using var customers = CustomersFor(database);
        await new CustomerMergeReferences(customers).MoveAsync(
            TenantId, survivor, [merged], TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { survivor, merged }.Order().ToArray(),
            (await CustomerIdsAsync(database, "messaging.campaign_recipients", "customer_id", TenantId)).Order().ToArray());
    }

    // F14: las cinco clases de otros módulos, sólo del tenant y sólo de los ids pedidos.
    [Fact]
    public async Task CountsCountEachKindAndOnlyTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (merged, other, unrelated) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await AddRowAsync(database, "quotations.quotations", "client_id", TenantId, merged);
        await AddRowAsync(database, "quotations.quotations", "client_id", TenantId, other);
        await AddRowAsync(database, "quotations.orders", "client_id", TenantId, merged);
        await AddRowAsync(database, "messaging.conversations", "customer_id", TenantId, other);
        await AddRowAsync(database, "messaging.conversations", "customer_id", TenantId, merged);
        await AddRowAsync(database, "messaging.conversations", "customer_id", TenantId, merged);
        await AddRowAsync(database, "pipeline.deals", "customer_id", TenantId, merged);
        await AddRowAsync(database, "quotations.quotations", "client_id", TenantId, unrelated);
        await AddRowAsync(database, "pos.sales", "customer_id", TenantId, unrelated);
        await AddReferencesAsync(database, OtherTenantId, merged);

        await using var customers = CustomersFor(database);
        var counts = await new CustomerMergeReferences(customers).CountAsync(
            TenantId, [merged, other], TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerMergeReferenceCounts(Quotations: 2, Orders: 1, Conversations: 3, PosSales: 0, Deals: 1), counts);
    }

    // §4: el adaptador corre en la conexión y la transacción del CustomersDbContext; deshacerla deshace también lo movido.
    [Fact]
    public async Task TheMoveRunsInsideTheCustomersTransaction()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AddReferencesAsync(database, TenantId, merged);
        var campaign = await AddCampaignAsync(database, TenantId, "Draft", [merged]);

        await using var customers = CustomersFor(database);
        await using (var transaction = await customers.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await new CustomerMergeReferences(customers).MoveAsync(
                TenantId, survivor, [merged], TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        foreach (var (table, column) in ReferenceColumns)
        {
            Assert.Equal([merged], await CustomerIdsAsync(database, table, column, TenantId));
        }

        await AssertCampaignAsync(database, campaign, [merged], 1);
    }

    // Sin ids no consulta (ruling B3): el contador de comandos queda en cero, las filas siguen con B y los conteos en cero.
    [Fact]
    public async Task NoMergedIdsDoNothingWithoutQuerying()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AddReferencesAsync(database, TenantId, merged);
        var campaign = await AddCampaignAsync(database, TenantId, "Draft", [merged]);
        var commands = new CommandCounter();

        await using var customers = CustomersFor(database, commands);
        var adapter = new CustomerMergeReferences(customers);
        await adapter.MoveAsync(TenantId, survivor, [], TestContext.Current.CancellationToken);
        var counts = await adapter.CountAsync(TenantId, [], TestContext.Current.CancellationToken);

        Assert.Equal(0, commands.Count);
        Assert.Equal(new CustomerMergeReferenceCounts(0, 0, 0, 0, 0), counts);
        foreach (var (table, column) in ReferenceColumns)
        {
            Assert.Equal([merged], await CustomerIdsAsync(database, table, column, TenantId));
        }

        await AssertCampaignAsync(database, campaign, [merged], 1);
    }

    // La versión de las otras tablas con If-Match no sube (F15): una conversación abierta en otra pestaña no da 412.
    [Fact]
    public async Task OnlyTheCampaignsVersionMoves()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (survivor, merged) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AddReferencesAsync(database, TenantId, merged);

        await using var customers = CustomersFor(database);
        await new CustomerMergeReferences(customers).MoveAsync(
            TenantId, survivor, [merged], TestContext.Current.CancellationToken);

        foreach (var table in new[] { "messaging.conversations", "pipeline.deals", "shopify.orders" })
        {
            Assert.Equal(1, Assert.Single(await ScalarsAsync<int>(database, $"SELECT version FROM {table}")));
        }

        Assert.Equal("Foto de la venta", Assert.Single(await ScalarsAsync<string>(database, "SELECT customer_name FROM pos.sales")));
    }

    // Cobros recurrentes (spec 2026-10-29 §3.1): servicios y cobros pasan al que queda; el perfil del fusionado se borra si el que
    // queda ya tiene uno y se le reasigna si no. Dos fusionados con perfil dejan uno solo (la clave es (tenant, cliente)).
    [Fact]
    public async Task MergingMovesServicesAndChargesToTheSurvivorAndDropsTheDuplicateProfile()
    {
        await using var database = await StartDatabaseAsync();
        await CreateReplicaTablesAsync(database);
        var (withProfile, withoutProfile, mergedA, mergedB, mergedC) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (keep, drop, reassign, older, newer) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await AddRowAsync(database, "billing.services", "customer_id", TenantId, mergedA);
        await AddRowAsync(database, "billing.charges", "customer_id", TenantId, mergedA);
        await AddRowAsync(database, "billing.services", "customer_id", OtherTenantId, mergedA);
        await AddProfileAsync(database, TenantId, withProfile, keep, 0);
        await AddProfileAsync(database, TenantId, mergedA, drop, 0);
        await AddProfileAsync(database, OtherTenantId, mergedA, drop, 0);

        await using (var customers = CustomersFor(database))
        {
            await new CustomerMergeReferences(customers).MoveAsync(
                TenantId, withProfile, [mergedA], TestContext.Current.CancellationToken);
        }

        Assert.Equal([withProfile], await CustomerIdsAsync(database, "billing.services", "customer_id", TenantId));
        Assert.Equal([withProfile], await CustomerIdsAsync(database, "billing.charges", "customer_id", TenantId));
        Assert.Equal([mergedA], await CustomerIdsAsync(database, "billing.services", "customer_id", OtherTenantId));
        Assert.Equal([(withProfile, keep)], await ProfilesAsync(database, TenantId));
        Assert.Equal([(mergedA, drop)], await ProfilesAsync(database, OtherTenantId));

        // El que queda no tiene perfil: se reasigna el del fusionado; con dos, gana el escrito más tarde y el otro se borra.
        await AddProfileAsync(database, TenantId, mergedB, older, -10);
        await AddProfileAsync(database, TenantId, mergedC, newer, 0);
        await AddProfileAsync(database, TenantId, mergedA, reassign, -20);
        await using (var customers = CustomersFor(database))
        {
            await new CustomerMergeReferences(customers).MoveAsync(
                TenantId, withoutProfile, [mergedA, mergedB, mergedC], TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            new[] { (withProfile, keep), (withoutProfile, newer) }.Order().ToArray(),
            (await ProfilesAsync(database, TenantId)).Order().ToArray());
    }

    // Las columnas que la fusión mueve (§4), en el orden del adaptador, sin la audiencia de campañas, que va aparte.
    private static readonly (string Table, string Column)[] ReferenceColumns =
    [
        ("quotations.quotations", "client_id"),
        ("quotations.orders", "client_id"),
        ("pos.sales", "customer_id"),
        ("pipeline.deals", "customer_id"),
        ("pipeline.deal_quotations", "customer_id"),
        ("pipeline.deal_orders", "customer_id"),
        ("pipeline.triage_decisions", "customer_id"),
        ("messaging.conversations", "customer_id"),
        ("shopify.orders", "qep_customer_id"),
        ("billing.services", "customer_id"),
        ("billing.charges", "customer_id"),
    ];

    private static CustomersDbContext CustomersFor(PostgreSqlContainer database, CommandCounter? commands = null)
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>().UseNpgsql(database.GetConnectionString());
        if (commands is not null)
        {
            options.AddInterceptors(commands);
        }

        return new CustomersDbContext(options.Options);
    }

    private static Task CreateReplicaTablesAsync(PostgreSqlContainer database) =>
        ExecuteAsync(database, """
            CREATE SCHEMA quotations;
            CREATE SCHEMA pos;
            CREATE SCHEMA pipeline;
            CREATE SCHEMA messaging;
            CREATE SCHEMA shopify;
            CREATE SCHEMA billing;
            CREATE TABLE billing.services (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid NOT NULL,
                version integer NOT NULL DEFAULT 1);
            CREATE TABLE billing.charges (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid NOT NULL);
            CREATE TABLE billing.customer_profiles (tenant_id uuid NOT NULL, customer_id uuid NOT NULL, assignee_member_id uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, customer_id));
            CREATE TABLE quotations.quotations (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, client_id uuid NOT NULL);
            CREATE TABLE quotations.orders (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, client_id uuid);
            CREATE TABLE pos.sales (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid NOT NULL,
                customer_name text NOT NULL DEFAULT 'Foto de la venta');
            CREATE TABLE pipeline.deals (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid NOT NULL,
                version integer NOT NULL DEFAULT 1);
            CREATE TABLE pipeline.deal_quotations (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid NOT NULL);
            CREATE TABLE pipeline.deal_orders (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid);
            CREATE TABLE pipeline.triage_decisions (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid);
            CREATE TABLE messaging.conversations (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid,
                version integer NOT NULL DEFAULT 1);
            CREATE TABLE messaging.campaigns (id uuid PRIMARY KEY, tenant_id uuid NOT NULL, status varchar(16) NOT NULL,
                audience_customer_ids uuid[] NOT NULL, version integer NOT NULL DEFAULT 1);
            CREATE TABLE messaging.campaign_recipients (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), campaign_id uuid NOT NULL,
                tenant_id uuid NOT NULL, customer_id uuid NOT NULL, UNIQUE (campaign_id, customer_id));
            CREATE TABLE shopify.orders (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, qep_customer_id uuid,
                version integer NOT NULL DEFAULT 1);
            """);

    private static async Task AddReferencesAsync(PostgreSqlContainer database, Guid tenantId, Guid customerId)
    {
        foreach (var (table, column) in ReferenceColumns)
        {
            await AddRowAsync(database, table, column, tenantId, customerId);
        }
    }

    // Tabla y columna son constantes de esta clase, nunca entrada: por eso van interpolados.
    private static Task AddRowAsync(PostgreSqlContainer database, string table, string column, Guid tenantId, Guid customerId) =>
        ExecuteAsync(
            database,
            $"INSERT INTO {table} (tenant_id, {column}) VALUES (@tenant, @customer)",
            ("tenant", tenantId),
            ("customer", customerId));

    private static Task AddProfileAsync(
        PostgreSqlContainer database, Guid tenantId, Guid customerId, Guid assigneeMemberId, int minutesFromNow) =>
        ExecuteAsync(
            database,
            """
            INSERT INTO billing.customer_profiles (tenant_id, customer_id, assignee_member_id, updated_at)
            VALUES (@tenant, @customer, @assignee, now() + make_interval(mins => @minutes))
            """,
            ("tenant", tenantId),
            ("customer", customerId),
            ("assignee", assigneeMemberId),
            ("minutes", minutesFromNow));

    private static async Task<(Guid CustomerId, Guid AssigneeMemberId)[]> ProfilesAsync(PostgreSqlContainer database, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT customer_id, assignee_member_id FROM billing.customer_profiles WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(Guid, Guid)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        return [.. rows];
    }

    private static async Task<Guid> AddCampaignAsync(PostgreSqlContainer database, Guid tenantId, string status, Guid[] audience)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            "INSERT INTO messaging.campaigns (id, tenant_id, status, audience_customer_ids) VALUES (@id, @tenant, @status, @audience)",
            ("id", id),
            ("tenant", tenantId),
            ("status", status),
            ("audience", audience));
        return id;
    }

    // La audiencia se compara en orden: el UPDATE conserva la primera aparición de cada uno.
    private static async Task AssertCampaignAsync(
        PostgreSqlContainer database, Guid campaignId, Guid[] expectedAudience, int expectedVersion)
    {
        var (audience, version) = await CampaignAsync(database, campaignId);
        Assert.Equal(expectedAudience, audience);
        Assert.Equal(expectedVersion, version);
    }

    private static async Task<(Guid[] Audience, int Version)> CampaignAsync(PostgreSqlContainer database, Guid campaignId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT audience_customer_ids, version FROM messaging.campaigns WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", campaignId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetFieldValue<Guid[]>(0), reader.GetInt32(1));
    }

    private static async Task<Guid[]> CustomerIdsAsync(PostgreSqlContainer database, string table, string column, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {column} FROM {table} WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    private static async Task<T[]> ScalarsAsync<T>(PostgreSqlContainer database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return [.. values];
    }

    private static async Task ExecuteAsync(PostgreSqlContainer database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    /// <summary>Cuenta cada comando que el contexto manda a la base, de cualquier clase.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
