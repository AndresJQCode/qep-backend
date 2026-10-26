using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-5 contra la base: <see cref="ICustomerDataConsents"/> registra la respuesta del bot una sola vez por
/// respuesta (<c>reply_message_id</c>), sobre el cliente que quedó si estaba fusionado, también si está inactivo, y la audita
/// como <c>System</c> en la misma transacción. Los clientes nacen por la API.
/// </summary>
public sealed class CustomerDataConsentsServiceTests
{
    private const string SharedNumber = "92-1396892";
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture(PostgreSqlContainer database, QepApiFactory factory, HttpClient client, Guid cityId, Guid classificationId)
        : IAsyncDisposable
    {
        public string ConnectionString { get; } = database.GetConnectionString();

        public HttpClient Client { get; } = client;

        public static async Task<Fixture> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString());
            await SeedTenantAsync(factory);
            var client = CreateClient(
                factory, SubjectId, TenantId,
                CustomersPermissions.CustomerRead,
                CustomersPermissions.CustomerManage,
                CustomersPermissions.CustomerMerge,
                CustomersPermissions.ClassificationRead,
                CustomersPermissions.ClassificationManage);
            var city = await EnsureCityAsync(client);
            var classification = await CreateClassificationAsync(client);
            return new Fixture(database, factory, client, city.CityId, classification.Id);
        }

        public async Task<Guid> CreateCustomerAsync(string name = "GYSELYS", string type = "NIT", string number = SharedNumber)
        {
            var response = await Client.PostAsJsonAsync(
                CustomersUrl(), NewCustomerBody(cityId, classificationId, name, type, number), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken))!.Id;
        }

        public async Task<T> WithConsentsAsync<T>(Func<ICustomerDataConsents, Task<T>> action)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ICustomerDataConsents>());
        }

        public Task<DataConsentRecordOutcome> RecordAsync(ChatbotDataConsentRecord record) =>
            WithConsentsAsync(consents => consents.RecordFromChatbotAsync(record, TestContext.Current.CancellationToken));

        public Task<bool> HasCurrentAcceptanceAsync(Guid customerId) =>
            WithConsentsAsync(consents => consents.HasCurrentAcceptanceAsync(Tenant, customerId, TestContext.Current.CancellationToken));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static ChatbotDataConsentRecord RecordFor(Guid customerId, DataConsentEvent decision, Guid? replyMessageId = null) =>
        new(
            Tenant,
            customerId,
            decision,
            Now,
            new DataConsentIdentity(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "CO.123456789", "573001234567"),
            new ChatbotConsentEvidence(
                "¿Aceptas nuestra política de datos?", Guid.CreateVersion7(), replyMessageId ?? Guid.CreateVersion7(), "wamid.HBgM",
                Guid.CreateVersion7(), "Bienvenida", Guid.CreateVersion7(), 3, "n4"));

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountAsync(string connectionString, string sql) =>
        long.Parse((await TextAsync(connectionString, sql))!, CultureInfo.InvariantCulture);

    private static Task<long> AuditCountAsync(string connectionString, string action, Guid customerId) =>
        CountAsync(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'platform.audit.recorded.v1' "
            + $"AND payload->>'action' = '{action}' AND payload->>'resourceId' = '{customerId}' "
            + $"AND payload->>'actorType' = 'System' AND payload->>'actorId' = '{Guid.Empty}'");

    private static async Task MergeAsync(HttpClient client, Guid survivor, Guid merged)
    {
        var preview = await client.GetAsync(
            $"{CustomersUrl()}/{survivor}/merge-preview?mergedCustomerIds={merged}", TestContext.Current.CancellationToken);
        var previewText = await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(preview.StatusCode == HttpStatusCode.OK, $"{(int)preview.StatusCode} {previewText}");
        var version = JsonDocument.Parse(previewText).RootElement.GetProperty("survivor").GetProperty("version").GetInt64();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CustomersUrl()}/{survivor}/merge")
        {
            Content = JsonContent.Create(new { mergedCustomerIds = new[] { merged } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
    }

    [Fact]
    public async Task ANewCustomerHasNoCurrentAcceptance()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.CreateCustomerAsync();

        Assert.False(await fixture.HasCurrentAcceptanceAsync(customerId));
    }

    [Fact]
    public async Task AnAcceptedReplyIsCurrentAndAuditedBySystem()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.CreateCustomerAsync();

        var outcome = await fixture.RecordAsync(RecordFor(customerId, DataConsentEvent.Accepted));

        Assert.Equal(DataConsentRecordOutcome.Recorded, outcome);
        Assert.True(await fixture.HasCurrentAcceptanceAsync(customerId));
        Assert.Equal(1L, await CountAsync(
            fixture.ConnectionString,
            $"SELECT count(*) FROM customers.data_consents WHERE customer_id = '{customerId}' AND actor_kind = 'Chatbot' AND event = 'Accepted'"));
        Assert.Equal(1L, await AuditCountAsync(fixture.ConnectionString, CustomerAuditActions.DataConsentAccepted, customerId));
        Assert.Equal("customers.customer.data_consent_accepted", CustomerAuditActions.DataConsentAccepted);
    }

    [Fact]
    public async Task ARejectedReplyIsNotAnAcceptance()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.CreateCustomerAsync();
        Assert.Equal(DataConsentRecordOutcome.Recorded, await fixture.RecordAsync(RecordFor(customerId, DataConsentEvent.Accepted)));

        // Más tarde rechaza: el estado vigente es la última fila, no «alguna vez aceptó».
        var rejection = RecordFor(customerId, DataConsentEvent.Rejected) with { OccurredAt = Now.AddMinutes(1) };
        var outcome = await fixture.RecordAsync(rejection);

        Assert.Equal(DataConsentRecordOutcome.Recorded, outcome);
        Assert.False(await fixture.HasCurrentAcceptanceAsync(customerId));
        Assert.Equal(1L, await AuditCountAsync(fixture.ConnectionString, CustomerAuditActions.DataConsentRejected, customerId));
        Assert.Equal("customers.customer.data_consent_rejected", CustomerAuditActions.DataConsentRejected);
        Assert.Equal("customers.customer.data_consent_revoked", CustomerAuditActions.DataConsentRevoked);
    }

    [Fact]
    public async Task RecordingTheSameReplyTwiceKeepsOneRow()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.CreateCustomerAsync();
        var record = RecordFor(customerId, DataConsentEvent.Accepted);

        Assert.Equal(DataConsentRecordOutcome.Recorded, await fixture.RecordAsync(record));
        Assert.Equal(DataConsentRecordOutcome.AlreadyRecorded, await fixture.RecordAsync(record));

        Assert.Equal(1L, await CountAsync(fixture.ConnectionString, "SELECT count(*) FROM customers.data_consents"));
        Assert.Equal(1L, await AuditCountAsync(fixture.ConnectionString, CustomerAuditActions.DataConsentAccepted, customerId));
    }

    /// <summary>
    /// Review Focus 1: la carrera la cierra el índice, no la consulta previa. Dos registros del mismo cliente nunca llegan a
    /// chocar en el índice —el <c>FOR UPDATE</c> del cliente los pone en fila y el segundo sale por la consulta previa—, así que
    /// los dos son de clientes distintos con la misma respuesta, y la prueba retiene sus <c>INSERT</c> con un
    /// <c>LOCK TABLE … SHARE</c> hasta que los dos pasaron la consulta previa. Al soltarlo, uno entra y el otro recibe la
    /// violación del índice dentro de su transacción, que queda abortada: después, en el mismo scope, la conexión tiene que
    /// seguir sirviendo (la transacción se deshizo, no se siguió usando).
    /// </summary>
    [Fact]
    public async Task TwoConcurrentRecordsOfTheSameReplyKeepOneRow()
    {
        await using var fixture = await Fixture.StartAsync();
        var first = await fixture.CreateCustomerAsync();
        var second = await fixture.CreateCustomerAsync("GISELY", "CE");
        var reply = Guid.CreateVersion7();
        var ct = TestContext.Current.CancellationToken;

        await using var gate = new NpgsqlConnection(fixture.ConnectionString);
        await gate.OpenAsync(ct);
        await using var hold = await gate.BeginTransactionAsync(ct);
        await using (var lockTable = new NpgsqlCommand("LOCK TABLE customers.data_consents IN SHARE MODE", gate, hold))
        {
            await lockTable.ExecuteNonQueryAsync(ct);
        }

        Task<(Guid Customer, DataConsentRecordOutcome Outcome, bool Current)> RecordInItsScopeAsync(Guid customerId) =>
            fixture.WithConsentsAsync(async consents =>
            {
                var outcome = await consents.RecordFromChatbotAsync(RecordFor(customerId, DataConsentEvent.Accepted, reply), ct);
                return (customerId, outcome, await consents.HasCurrentAcceptanceAsync(Tenant, customerId, ct));
            });

        var both = Task.WhenAll(RecordInItsScopeAsync(first), RecordInItsScopeAsync(second));
        await WaitForBlockedInsertsAsync(fixture.ConnectionString, expected: 2, both);
        await hold.RollbackAsync(ct);
        var results = await both;

        Assert.Equal(
            [DataConsentRecordOutcome.Recorded, DataConsentRecordOutcome.AlreadyRecorded],
            results.Select(result => result.Outcome).Order().ToArray());
        var winner = results.Single(result => result.Outcome == DataConsentRecordOutcome.Recorded);
        var loser = results.Single(result => result.Outcome == DataConsentRecordOutcome.AlreadyRecorded);
        Assert.True(winner.Current);
        Assert.False(loser.Current);
        Assert.Equal(1L, await CountAsync(fixture.ConnectionString, "SELECT count(*) FROM customers.data_consents"));
        Assert.Equal(1L, await CountAsync(
            fixture.ConnectionString,
            $"SELECT count(*) FROM customers.data_consents WHERE customer_id = '{winner.Customer}' AND reply_message_id = '{reply}'"));
        Assert.Equal(1L, await AuditCountAsync(fixture.ConnectionString, CustomerAuditActions.DataConsentAccepted, winner.Customer));
        Assert.Equal(0L, await AuditCountAsync(fixture.ConnectionString, CustomerAuditActions.DataConsentAccepted, loser.Customer));
    }

    // Espera a que los dos INSERT estén esperando el lock de la tabla; si uno de los registros termina antes (no llegó al
    // INSERT), la prueba falla acá y no se cuelga.
    private static async Task WaitForBlockedInsertsAsync(string connectionString, int expected, Task work)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (work.IsCompleted)
            {
                await work;
                Assert.Fail("A record finished before reaching its INSERT.");
            }

            var blocked = await CountAsync(
                connectionString,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%INSERT INTO customers.data_consents%'");
            if (blocked == expected)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The {expected} INSERTs never blocked on the table lock.");
    }

    [Fact]
    public async Task ARecordForAMergedCustomerGoesToTheSurvivor()
    {
        await using var fixture = await Fixture.StartAsync();
        var survivor = await fixture.CreateCustomerAsync();
        var merged = await fixture.CreateCustomerAsync("GISELY", "CE");
        await MergeAsync(fixture.Client, survivor, merged);

        var outcome = await fixture.RecordAsync(RecordFor(merged, DataConsentEvent.Accepted));

        Assert.Equal(DataConsentRecordOutcome.Recorded, outcome);
        Assert.Equal(survivor.ToString(), await TextAsync(fixture.ConnectionString, "SELECT customer_id FROM customers.data_consents"));
        Assert.True(await fixture.HasCurrentAcceptanceAsync(survivor));
        Assert.True(await fixture.HasCurrentAcceptanceAsync(merged));
        Assert.Equal(1L, await AuditCountAsync(fixture.ConnectionString, CustomerAuditActions.DataConsentAccepted, survivor));
    }

    [Fact]
    public async Task AnInactiveCustomerStillRecords()
    {
        await using var fixture = await Fixture.StartAsync();
        var customerId = await fixture.CreateCustomerAsync();
        (await fixture.Client.PostAsync($"{CustomersUrl()}/{customerId}/deactivate", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var outcome = await fixture.RecordAsync(RecordFor(customerId, DataConsentEvent.Accepted));

        Assert.Equal(DataConsentRecordOutcome.Recorded, outcome);
        Assert.True(await fixture.HasCurrentAcceptanceAsync(customerId));
    }

    [Fact]
    public async Task AnUnknownCustomerIsMissing()
    {
        await using var fixture = await Fixture.StartAsync();
        _ = await fixture.CreateCustomerAsync();

        var outcome = await fixture.RecordAsync(RecordFor(Guid.CreateVersion7(), DataConsentEvent.Accepted));

        Assert.Equal(DataConsentRecordOutcome.CustomerMissing, outcome);
        Assert.Equal(0L, await CountAsync(fixture.ConnectionString, "SELECT count(*) FROM customers.data_consents"));
        Assert.Equal(0L, await CountAsync(
            fixture.ConnectionString, "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' LIKE 'customers.customer.data_consent_%'"));
    }
}
