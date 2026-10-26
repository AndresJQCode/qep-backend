using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-5 contra la base: <c>customers.data_consents</c> es prueba legal, así que la forma de cada fila la
/// garantizan sus CHECK y no sólo el agregado. Las filas inválidas entran por SQL (el agregado no deja armarlas); las válidas,
/// por <see cref="ICustomerDataConsentRepository"/> y <see cref="ICustomersUnitOfWork"/>.
/// </summary>
public sealed class CustomerDataConsentPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture(QepApiFactory factory, string connectionString) : IDisposable
    {
        public QepApiFactory Factory { get; } = factory;

        public string ConnectionString { get; } = connectionString;

        public async Task<T> WithScopeAsync<T>(Func<ICustomerDataConsentRepository, ICustomersUnitOfWork, Task<T>> action)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await action(
                scope.ServiceProvider.GetRequiredService<ICustomerDataConsentRepository>(),
                scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>());
        }

        public Task<int> SaveAsync(CustomerDataConsent consent) =>
            WithScopeAsync((repository, unitOfWork) =>
            {
                repository.Add(consent);
                return unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
            });

        public void Dispose() => Factory.Dispose();
    }

    // Arrancar el host migra la base: después de esto customers.data_consents existe.
    private static Fixture Start(string connectionString)
    {
        var factory = new QepApiFactory(connectionString);
        _ = factory.Services;
        return new Fixture(factory, connectionString);
    }

    private static DataConsentIdentity IdentityWith(string? waId = "573001234567") =>
        new(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "CO.123456789", waId);

    private static ChatbotConsentEvidence EvidenceWith(Guid replyMessageId) =>
        new("¿Aceptas nuestra política de datos?", Guid.CreateVersion7(), replyMessageId, "wamid.HBgM",
            Guid.CreateVersion7(), "Bienvenida", Guid.CreateVersion7(), 3, "n4");

    private static CustomerDataConsent ChatbotRow(
        Guid customerId, DataConsentEvent decision, DateTimeOffset occurredAt, Guid? replyMessageId = null) =>
        CustomerDataConsent.FromChatbot(
            Tenant, new CustomerId(customerId), decision, occurredAt, IdentityWith(),
            EvidenceWith(replyMessageId ?? Guid.CreateVersion7()));

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

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Incompleto basta para ser dueño de filas a nivel de tabla (mismo atajo que CustomerMergeStoreTests).
    private static async Task<Guid> InsertCustomerAsync(string connectionString, Guid tenant, Guid? mergedInto = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness,
                 merged_into_id, merged_at)
            VALUES (@id, @tenant, 'Cliente', @active, false, false, 1, now(), now(), 'Incomplete', @merged,
                    CASE WHEN @merged::uuid IS NULL THEN NULL ELSE now() END)
            """,
            ("id", id), ("tenant", tenant), ("active", mergedInto is null), ("merged", mergedInto));
        return id;
    }

    // Una fila válida del bot y una revocación válida por otro medio; cada caso pisa lo que hace falta para violar un CHECK.
    private static Dictionary<string, object?> ChatbotColumns(Guid customerId) => new()
    {
        ["id"] = Guid.CreateVersion7(),
        ["tenant_id"] = Tenant,
        ["customer_id"] = customerId,
        ["event"] = "Accepted",
        ["occurred_at"] = Now,
        ["channel"] = "WhatsApp",
        ["channel_user_id"] = "CO.123456789",
        ["wa_id"] = "573001234567",
        ["conversation_id"] = Guid.CreateVersion7(),
        ["actor_kind"] = "Chatbot",
        ["actor_user_id"] = null,
        ["shown_text"] = "¿Aceptas nuestra política de datos?",
        ["prompt_message_id"] = Guid.CreateVersion7(),
        ["reply_message_id"] = Guid.CreateVersion7(),
        ["reply_wamid"] = "wamid.HBgM",
        ["chatbot_id"] = Guid.CreateVersion7(),
        ["chatbot_name"] = "Bienvenida",
        ["chatbot_version_id"] = Guid.CreateVersion7(),
        ["chatbot_version_number"] = 3,
        ["node_id"] = "n4",
        ["medium"] = null,
        ["note"] = null,
    };

    private static Dictionary<string, object?> RevocationColumns(Guid customerId) => new()
    {
        ["id"] = Guid.CreateVersion7(),
        ["tenant_id"] = Tenant,
        ["customer_id"] = customerId,
        ["event"] = "Revoked",
        ["occurred_at"] = Now,
        ["channel"] = null,
        ["channel_user_id"] = null,
        ["wa_id"] = null,
        ["conversation_id"] = null,
        ["actor_kind"] = "Member",
        ["actor_user_id"] = Guid.Parse(SubjectId),
        ["medium"] = "Other",
        ["note"] = "Lo pidió por correo.",
    };

    private static Task InsertConsentAsync(string connectionString, Dictionary<string, object?> columns)
    {
        var names = columns.Keys.ToArray();
        return ExecuteAsync(
            connectionString,
            $"INSERT INTO customers.data_consents ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select(name => "@" + name))})",
            [.. names.Select(name => (name, columns[name]))]);
    }

    // Un evento desconocido viola también CK_data_consents_actor, que sólo admite los tres eventos, y PostgreSQL evalúa los
    // CHECK por orden de nombre: siempre reporta ése primero. Para ver que CK_data_consents_event rechaza solo, se lo quita en
    // una transacción que se revierte (la base es del contenedor de esta prueba).
    private static async Task InsertConsentWithoutTheActorCheckAsync(string connectionString, Dictionary<string, object?> columns)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var drop = new NpgsqlCommand(
            """ALTER TABLE customers.data_consents DROP CONSTRAINT "CK_data_consents_actor" """, connection, transaction))
        {
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var names = columns.Keys.ToArray();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO customers.data_consents ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select(name => "@" + name))})",
            connection,
            transaction);
        foreach (var name in names)
        {
            insert.Parameters.AddWithValue(name, columns[name] ?? DBNull.Value);
        }

        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public enum ViolatedRow
    {
        ChatbotWithoutReply,
        RevocationWithShownText,
        OtherMediumWithConversation,
        ThisConversationWithoutChannelUser,
        ChatbotWithActorUser,
        UnknownEvent,
    }

    private static Dictionary<string, object?> ColumnsFor(ViolatedRow row, Guid customerId)
    {
        var columns = row is ViolatedRow.RevocationWithShownText or ViolatedRow.OtherMediumWithConversation
            or ViolatedRow.ThisConversationWithoutChannelUser
            ? RevocationColumns(customerId)
            : ChatbotColumns(customerId);
        switch (row)
        {
            case ViolatedRow.ChatbotWithoutReply:
                columns["reply_message_id"] = null;
                break;
            case ViolatedRow.RevocationWithShownText:
                columns["shown_text"] = "¿Aceptas nuestra política de datos?";
                break;
            case ViolatedRow.OtherMediumWithConversation:
                columns["conversation_id"] = Guid.CreateVersion7();
                break;
            case ViolatedRow.ThisConversationWithoutChannelUser:
                columns["medium"] = "ThisConversation";
                columns["conversation_id"] = Guid.CreateVersion7();
                columns["channel"] = "WhatsApp";
                break;
            case ViolatedRow.ChatbotWithActorUser:
                columns["actor_user_id"] = Guid.Parse(SubjectId);
                break;
            case ViolatedRow.UnknownEvent:
                columns["event"] = "Maybe";
                break;
        }

        return columns;
    }

    [Theory]
    [InlineData(ViolatedRow.ChatbotWithoutReply, "CK_data_consents_chatbot_fields")]
    [InlineData(ViolatedRow.RevocationWithShownText, "CK_data_consents_chatbot_fields")]
    [InlineData(ViolatedRow.OtherMediumWithConversation, "CK_data_consents_identity")]
    [InlineData(ViolatedRow.ThisConversationWithoutChannelUser, "CK_data_consents_identity")]
    [InlineData(ViolatedRow.ChatbotWithActorUser, "CK_data_consents_actor")]
    [InlineData(ViolatedRow.UnknownEvent, "CK_data_consents_event")]
    public async Task EachCheckRejectsItsRow(ViolatedRow row, string constraint)
    {
        await using var database = await StartDatabaseAsync();
        using var fixture = Start(database.GetConnectionString());
        var customerId = await InsertCustomerAsync(fixture.ConnectionString, Tenant);

        var columns = ColumnsFor(row, customerId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => row == ViolatedRow.UnknownEvent
            ? InsertConsentWithoutTheActorCheckAsync(fixture.ConnectionString, columns)
            : InsertConsentAsync(fixture.ConnectionString, columns));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task AWaIdIsOptionalEvenWithAConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var fixture = Start(database.GetConnectionString());
        var customerId = await InsertCustomerAsync(fixture.ConnectionString, Tenant);
        var columns = ChatbotColumns(customerId);
        columns["wa_id"] = null;

        await InsertConsentAsync(fixture.ConnectionString, columns);
        await fixture.SaveAsync(CustomerDataConsent.FromChatbot(
            Tenant, new CustomerId(customerId), DataConsentEvent.Rejected, Now, IdentityWith(waId: null),
            EvidenceWith(Guid.CreateVersion7())));

        Assert.Equal(
            2L,
            await CountAsync(fixture.ConnectionString, "SELECT count(*) FROM customers.data_consents WHERE wa_id IS NULL AND conversation_id IS NOT NULL"));
    }

    [Fact]
    public async Task ARepeatedReplyIsTranslatedByIndexName()
    {
        await using var database = await StartDatabaseAsync();
        using var fixture = Start(database.GetConnectionString());
        var customerId = await InsertCustomerAsync(fixture.ConnectionString, Tenant);
        var reply = Guid.CreateVersion7();
        await fixture.SaveAsync(ChatbotRow(customerId, DataConsentEvent.Accepted, Now, reply));

        await Assert.ThrowsAsync<DataConsentReplyRecordedException>(() =>
            fixture.SaveAsync(ChatbotRow(customerId, DataConsentEvent.Accepted, Now.AddSeconds(1), reply)));

        Assert.Equal(1L, await CountAsync(fixture.ConnectionString, "SELECT count(*) FROM customers.data_consents"));
        Assert.True(await fixture.WithScopeAsync((repository, _) =>
            repository.ReplyRecordedAsync(Tenant, reply, TestContext.Current.CancellationToken)));
        Assert.False(await fixture.WithScopeAsync((repository, _) =>
            repository.ReplyRecordedAsync(OtherTenant, reply, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task TheLatestIsOrderedByOccurredAtThenSequence()
    {
        await using var database = await StartDatabaseAsync();
        using var fixture = Start(database.GetConnectionString());
        var customerId = await InsertCustomerAsync(fixture.ConnectionString, Tenant);
        var first = ChatbotRow(customerId, DataConsentEvent.Accepted, Now);
        var second = ChatbotRow(customerId, DataConsentEvent.Rejected, Now);
        // Insertada al final pero ocurrida antes: el orden lo manda occurred_at, la secuencia sólo desempata.
        var earlier = ChatbotRow(customerId, DataConsentEvent.Accepted, Now.AddMinutes(-5));
        await fixture.SaveAsync(first);
        await fixture.SaveAsync(second);
        await fixture.SaveAsync(earlier);

        var latest = await fixture.WithScopeAsync((repository, _) =>
            repository.FindLatestAsync(Tenant, new CustomerId(customerId), TestContext.Current.CancellationToken));
        var all = await fixture.WithScopeAsync((repository, _) =>
            repository.ListAsync(Tenant, new CustomerId(customerId), TestContext.Current.CancellationToken));
        var foreign = await fixture.WithScopeAsync((repository, _) =>
            repository.ListAsync(OtherTenant, new CustomerId(customerId), TestContext.Current.CancellationToken));

        Assert.Equal(second.Id, latest!.Id);
        Assert.Equal(DataConsentEvent.Rejected, latest.Event);
        Assert.Equal([second.Id, first.Id, earlier.Id], all.Select(consent => consent.Id).ToArray());
        Assert.True(all[0].Sequence > all[1].Sequence);
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task LockingFollowsOneMergeHopAndNeedsTheTransaction()
    {
        await using var database = await StartDatabaseAsync();
        using var fixture = Start(database.GetConnectionString());
        var survivor = await InsertCustomerAsync(fixture.ConnectionString, Tenant);
        var merged = await InsertCustomerAsync(fixture.ConnectionString, Tenant, mergedInto: survivor);
        var foreign = await InsertCustomerAsync(fixture.ConnectionString, OtherTenant);

        async Task<Guid?> LockAsync(Guid customerId) =>
            await fixture.WithScopeAsync(async (repository, unitOfWork) =>
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(TestContext.Current.CancellationToken);
                return await repository.LockCustomerAsync(Tenant, customerId, TestContext.Current.CancellationToken);
            });

        Assert.Equal(survivor, await LockAsync(survivor));
        Assert.Equal(survivor, await LockAsync(merged));
        Assert.Null(await LockAsync(foreign));
        Assert.Null(await LockAsync(Guid.CreateVersion7()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WithScopeAsync((repository, _) =>
            repository.LockCustomerAsync(Tenant, survivor, TestContext.Current.CancellationToken)));
    }
}
