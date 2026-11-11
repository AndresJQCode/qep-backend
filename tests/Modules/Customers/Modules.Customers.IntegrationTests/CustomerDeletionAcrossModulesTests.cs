using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Una base migrada para toda la clase: las pruebas corren en serie y cada una siembra en su propio tenant, así que no se
/// pisan. El contador de comandos va enchufado al <see cref="CustomersDbContext"/> del contenedor y sólo cuenta mientras una
/// prueba lo enciende.
/// </summary>
public sealed class CustomerDeletionDatabase : IAsyncLifetime
{
    private PostgreSqlContainer? _database;

    public CommandCounter Counter { get; } = new();

    private QepApiFactory? _factory;

    public IServiceProvider Services => _factory!.Services;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        ConnectionString = _database.GetConnectionString();
        _factory = new QepApiFactory(
            ConnectionString,
            services => services.ConfigureDbContext<CustomersDbContext>(options => options.AddInterceptors(Counter)));
        // El arranque del host migra todos los esquemas.
        using var client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

/// <summary>Cuenta los comandos que el <see cref="CustomersDbContext"/> manda a la base mientras <see cref="Active"/> está prendido en el flujo que llama.</summary>
public sealed class CommandCounter : DbCommandInterceptor
{
    // AsyncLocal y no un bool: los workers del host (outbox, backfill) usan también el CustomersDbContext, y sus comandos no
    // deben sumar. Sólo cuenta el flujo de la prueba que lo prendió.
    private readonly AsyncLocal<bool> _active = new();
    private int _count;

    public bool Active
    {
        get => _active.Value;
        set => _active.Value = value;
    }

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    private void Hit()
    {
        if (Active)
        {
            Interlocked.Increment(ref _count);
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Hit();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Hit();
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Hit();
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Hit();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Hit();
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Hit();
        return base.ScalarExecuting(command, eventData, result);
    }
}

/// <summary>
/// Spec 2026-11-10 (borrar clientes) §4.2, §4.3 y §5 contra el esquema migrado de todos los módulos: los adaptadores reales del
/// contenedor (<see cref="ICustomerDeletionReferences"/> en Bootstrapper y <see cref="ICustomerDeletionStore"/>), en el mismo
/// orden y la misma transacción que <see cref="BulkDeleteCustomersHandler"/>. Las filas de los otros módulos se siembran por SQL
/// con los nombres de columna de sus migraciones (Customers no tiene API para ellas); la sesión de siembra corre con
/// <c>session_replication_role = replica</c>, que en esa conexión sola apaga las FK hacia filas que la prueba no necesita
/// (etapa, ciudad sin resolver, sesión de caja con su empresa). Estas pruebas son las que se ponen en rojo si alguien renombra
/// una columna que el borrado toca por nombre (<c>42703</c>, <c>42P01</c>).
/// </summary>
public sealed class CustomerDeletionAcrossModulesTests(CustomerDeletionDatabase fixture) : IClassFixture<CustomerDeletionDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Name = "Ana Borrada";
    private const string Phone = "+573001112233";

    private sealed record Footprint(
        Guid Tenant, Guid Customer, Guid Merged, Guid Bystander, Guid Conversation, Guid[] Messages, Guid ScheduledMessage,
        Guid Run, Guid InboxMessage, Guid SentCampaign, Guid SentRecipient, Guid PendingCampaign, Guid PendingRecipient,
        Guid DraftCampaign, Guid Deal, Guid Triage, Guid Sale, Guid Occurrence, Guid ShopifyOrder, Guid Consent,
        Guid BystanderConversation, Guid BystanderSale, Guid BystanderShopifyOrder, string MediaKey, string ScheduledKey);

    private sealed record Deletion(
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> Blockers,
        IReadOnlyDictionary<Guid, int> Consents,
        CustomerDeletionOutcome Outcome,
        IReadOnlyDictionary<Guid, int> Merged);

    // ---------------------------------------------------------------------------------------------------------------------
    // Pruebas
    // ---------------------------------------------------------------------------------------------------------------------

    // §4.3 fila por fila: lo que se borra, lo que se suelta y lo que se conserva. El otro cliente del tenant no se toca.
    [Fact]
    public async Task EveryReferenceEndsAsTheSpecSays()
    {
        var footprint = await SeedFootprintAsync();
        Assert.Equal(StateBefore(footprint), await StateAsync(footprint));

        var deletion = await DeleteAsync(footprint.Tenant, [footprint.Customer]);

        Assert.Empty(deletion.Blockers);
        Assert.Equal(StateAfter(footprint), await StateAsync(footprint));
        Assert.Equal(1, deletion.Consents[footprint.Customer]);
        Assert.Equal(1, deletion.Merged[footprint.Customer]);
        Assert.Equal(new CustomerDeletionCounts(1, 3, 1, 1, 2), deletion.Outcome.Counts[footprint.Customer]);
        Assert.Equal(
            new[] { footprint.MediaKey, footprint.ScheduledKey }.Order(StringComparer.Ordinal),
            deletion.Outcome.StorageKeys[footprint.Customer]);
    }

    // P11: la audiencia nunca queda vacía (CK_campaigns_audience). Si el cliente era lo único, el id se queda.
    [Fact]
    public async Task ADraftCampaignWhoseOnlyAudienceIsTheCustomerKeepsTheId()
    {
        var tenant = Guid.CreateVersion7();
        var customer = await InsertCustomerAsync(tenant, Name, Phone);
        var campaign = Guid.CreateVersion7();
        await SeedAsync(
            """
            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category,
                parameters, audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
            VALUES (@campaign, @tenant, @tenant, 'Solo ella', 'promo', 'es', 'MARKETING', '[]'::jsonb, '{}'::uuid[], ARRAY[@customer],
                'Draft', @tenant, now(), now(), 1);
            """,
            ("campaign", campaign), ("tenant", tenant), ("customer", customer));

        await DeleteAsync(tenant, [customer]);

        Assert.Equal(
            $"{{{customer}}}|1",
            await TextAsync("SELECT audience_customer_ids::text || '|' || version FROM messaging.campaigns WHERE id = @id", ("id", campaign)));
    }

    public static TheoryData<string, string[]> Blockers => new()
    {
        { "quotation", [CustomerDeletionBlockReasons.Quotations] },
        { "order", [CustomerDeletionBlockReasons.Orders] },
        { "loan", [CustomerDeletionBlockReasons.Loans] },
        { "service", [CustomerDeletionBlockReasons.ServiceContracts] },
        { "charge", [CustomerDeletionBlockReasons.ServiceContracts] },
        { "blockedConversation", [CustomerDeletionBlockReasons.BlockedContact] },
        { "quotation+order", [CustomerDeletionBlockReasons.Quotations, CustomerDeletionBlockReasons.Orders] },
    };

    // §4.2: cada motivo, con el orden del contrato. El otro cliente del lote, con una conversación sin bloquear, no aparece.
    [Theory]
    [MemberData(nameof(Blockers))]
    public async Task EachBlockerIsFound(string rows, string[] expected)
    {
        var tenant = Guid.CreateVersion7();
        var customer = await InsertCustomerAsync(tenant, Name, Phone);
        var free = await InsertCustomerAsync(tenant, "Libre", null);
        await InsertConversationAsync(tenant, free, blocked: false);
        foreach (var row in rows.Split('+'))
        {
            await SeedBlockerAsync(tenant, customer, row);
        }

        await using var scope = fixture.Services.CreateAsyncScope();
        var references = scope.ServiceProvider.GetRequiredService<ICustomerDeletionReferences>();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().BeginTransactionAsync(Ct);
        await references.LockAsync(tenant, [customer, free], Ct);

        var blockers = await references.FindBlockersAsync(tenant, [customer, free], Ct);

        var only = Assert.Single(blockers);
        Assert.Equal(customer, only.Key);
        Assert.Equal(expected, only.Value);
    }

    // P5: la lectura de bloqueos toma las conversaciones con FOR UPDATE. Bloquear una en otra conexión espera al commit.
    [Fact]
    public async Task TheBlockedContactCheckLocksTheConversations()
    {
        var tenant = Guid.CreateVersion7();
        var customer = await InsertCustomerAsync(tenant, Name, Phone);
        var conversation = await InsertConversationAsync(tenant, customer, blocked: false);

        await using var scope = fixture.Services.CreateAsyncScope();
        var references = scope.ServiceProvider.GetRequiredService<ICustomerDeletionReferences>();
        var transaction = await scope.ServiceProvider.GetRequiredService<ICustomersUnitOfWork>().BeginTransactionAsync(Ct);
        Task block;
        try
        {
            await references.LockAsync(tenant, [customer], Ct);
            Assert.Empty(await references.FindBlockersAsync(tenant, [customer], Ct));

            block = ExecuteAsync(
                """
                UPDATE messaging.conversations
                   SET blocked_at = now(), blocked_by_member_id = @member, blocked_wa_id = '573001112233'
                 WHERE id = @id
                """,
                ("member", Guid.CreateVersion7()), ("id", conversation));
            var finished = await Task.WhenAny(block, Task.Delay(TimeSpan.FromMilliseconds(300), Ct));
            Assert.NotSame(block, finished);
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        // Sin commit, el Dispose deshace y suelta las filas: el bloqueo de la otra conexión pasa.
        await block;
    }

    // §5 atomicidad: un disparador rompe la última sentencia (shopify.orders, como en la fusión) y todo vuelve, Customers incluido.
    [Fact]
    public async Task AFailureInTheLastStatementLeavesEverythingAsItWas()
    {
        var footprint = await SeedFootprintAsync();
        var before = await StateAsync(footprint);
        var trigger = $"fail_customer_deletion_{footprint.Tenant:N}";
        await ExecuteAsync(
            $"""
            CREATE FUNCTION public.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'borrado roto a propósito';
            END $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE ON shopify.orders
                FOR EACH ROW WHEN (OLD.tenant_id = '{footprint.Tenant}'::uuid) EXECUTE FUNCTION public.{trigger}();
            """);
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeleteAsync(footprint.Tenant, [footprint.Customer]));
            Assert.Contains("borrado roto a propósito", failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER {trigger} ON shopify.orders; DROP FUNCTION public.{trigger}();");
        }

        Assert.Equal(before, await StateAsync(footprint));
        Assert.Equal(StateBefore(footprint), before);
    }

    // El runner del chatbot (RunProcessor.ProcessUnitAsync) toma el candado de la conversación, después su corrida con FOR UPDATE, y
    // mientras los tiene escribe la conversación por Mensajería en OTRA conexión. Si el borrado bloqueara la conversación antes de
    // esperar la corrida, esa escritura esperaría al borrado, el borrado a la corrida y la corrida a la escritura: un ciclo que Postgres
    // no ve (la transacción del runner está ociosa) y que sólo corta el timeout de 30 s. El borrado tiene que esperar el candado del
    // runner sin tener ninguna fila de la conversación, y terminar cuando el runner confirma.
    [Fact]
    public async Task ADeletionWaitsForTheChatbotRunWithoutHoldingItsConversation()
    {
        var footprint = await SeedFootprintAsync();
        await using var runner = new NpgsqlConnection(fixture.ConnectionString);
        await runner.OpenAsync(Ct);
        var runnerTransaction = await runner.BeginTransactionAsync(Ct);
        await using (var hold = new NpgsqlCommand(
            """
            SELECT pg_advisory_xact_lock(20261022, hashtext(@conversation::text));
            SELECT id FROM automations.conversation_runs WHERE id = @run FOR UPDATE;
            """, runner, runnerTransaction))
        {
            hold.Parameters.AddWithValue("conversation", footprint.Conversation);
            hold.Parameters.AddWithValue("run", footprint.Run);
            await hold.ExecuteNonQueryAsync(Ct);
        }

        var deletion = Task.Run(() => DeleteAsync(footprint.Tenant, [footprint.Customer]), Ct);
        Task? reply = null;
        var committed = false;
        try
        {
            await WaitUntilSomeoneWaitsForALockAsync(deletion);

            // La respuesta del bot, por Mensajería y en su propia conexión, mientras el runner tiene su candado y su corrida.
            reply = ExecuteAsync(
                """
                INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, created_at)
                SELECT gen_random_uuid(), id, tenant_id, connection_id, now(), 2, 1, 2, now() FROM messaging.conversations WHERE id = @id
                """,
                ("id", footprint.Conversation));
            Assert.Same(reply, await Task.WhenAny(reply, Task.Delay(TimeSpan.FromSeconds(3), Ct)));
            await reply;
            Assert.False(deletion.IsCompleted, "The deletion did not wait for the chatbot run.");

            await runnerTransaction.CommitAsync(Ct);
            committed = true;
            await deletion;
        }
        finally
        {
            if (!committed)
            {
                await runnerTransaction.CommitAsync(Ct);
                await Task.WhenAll(new[] { deletion, reply ?? Task.CompletedTask }.Select(task => task.ContinueWith(_ => { }, Ct)));
            }

            await runnerTransaction.DisposeAsync();
        }

        Assert.Equal(StateAfter(footprint), await StateAsync(footprint));
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id", ("id", footprint.Conversation)));
    }

    // Final review I-2b (A-5): data_consents ya no tiene FK, así que un consentimiento del chatbot (o una revocación) que entrara entre
    // la foto de los consentimientos y el DELETE quedaría sin foto. El borrado bloquea las filas de los clientes borrables antes de la
    // foto; quien registra un consentimiento bloquea la misma fila (LockCustomerAsync), así que espera y, al despertar, no encuentra
    // al cliente.
    [Fact]
    public async Task AConsentRecordedDuringTheDeletionWaitsAndFindsTheCustomerGone()
    {
        var tenant = Guid.CreateVersion7();
        var customer = await InsertCustomerAsync(tenant, Name, Phone);
        var warmUp = await InsertCustomerAsync(tenant, "Sofía", "+573009998877");
        // Calentamiento del mismo camino con otro cliente: sin él la espera podría ser arranque en frío y no el bloqueo.
        Assert.Equal(DataConsentRecordOutcome.Recorded, await RecordConsentAsync(tenant, warmUp));

        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var references = services.GetRequiredService<ICustomerDeletionReferences>();
        var store = services.GetRequiredService<ICustomerDeletionStore>();
        await using var transaction = await services.GetRequiredService<ICustomersUnitOfWork>().BeginTransactionAsync(Ct);
        await references.LockAsync(tenant, [customer], Ct);
        var found = await store.FindDeletableAsync(tenant, [customer], Ct);
        Assert.Empty(await references.FindBlockersAsync(tenant, [customer], Ct));
        await store.LockRowsAsync(tenant, [customer], Ct);

        var record = Task.Run(() => RecordConsentAsync(tenant, customer), Ct);

        Assert.NotSame(record, await Task.WhenAny(record, Task.Delay(TimeSpan.FromMilliseconds(500), Ct)));
        await store.SnapshotConsentsAsync(tenant, [.. found.Select(row => (row.Id.Value, row.Name, row.PhoneE164))], Ct);
        await references.RemoveAsync(tenant, [customer], Ct);
        await store.DeleteAsync(tenant, [customer], Ct);
        await transaction.CommitAsync(Ct);

        Assert.Equal(DataConsentRecordOutcome.CustomerMissing, await record);
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM customers.data_consents WHERE customer_id = @id", ("id", customer)));
    }

    private async Task<DataConsentRecordOutcome> RecordConsentAsync(Guid tenant, Guid customer)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICustomerDataConsents>().RecordFromChatbotAsync(
            new ChatbotDataConsentRecord(
                tenant,
                customer,
                DataConsentEvent.Accepted,
                DateTimeOffset.UtcNow,
                new DataConsentIdentity(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "CO.123456789", "573001112233"),
                new ChatbotConsentEvidence(
                    "¿Aceptas nuestra política de datos?", Guid.CreateVersion7(), Guid.CreateVersion7(), "wamid.HBgM",
                    Guid.CreateVersion7(), "Bienvenida", Guid.CreateVersion7(), 3, "n4")),
            Ct);
    }

    // §5 rendimiento: sentencias por lote. 100 clientes con 50 mensajes cada uno mandan los mismos comandos que uno solo.
    [Fact]
    public async Task RemovingAHundredCustomersRunsAFixedNumberOfStatements()
    {
        var one = await SeedCustomersWithMessagesAsync(1, 50);
        var hundred = await SeedCustomersWithMessagesAsync(100, 50);

        var forOne = await CountCommandsAsync(one.Tenant, one.Customers);
        var forHundred = await CountCommandsAsync(hundred.Tenant, hundred.Customers);

        Assert.True(forOne > 0);
        Assert.Equal(forOne, forHundred);
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM messaging.conversations WHERE tenant_id = @tenant", ("tenant", hundred.Tenant)));
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM customers.customers WHERE tenant_id = @tenant", ("tenant", hundred.Tenant)));
    }

    // Sin ids no se consulta nada, ni hace falta transacción: el handler llama igual con un lote todo bloqueado o no encontrado.
    [Fact]
    public async Task WithoutIdsNothingIsQueried()
    {
        var tenant = Guid.CreateVersion7();
        await using var scope = fixture.Services.CreateAsyncScope();
        var references = scope.ServiceProvider.GetRequiredService<ICustomerDeletionReferences>();
        var store = scope.ServiceProvider.GetRequiredService<ICustomerDeletionStore>();
        fixture.Counter.Reset();
        fixture.Counter.Active = true;
        try
        {
            await references.LockAsync(tenant, [], Ct);
            Assert.Empty(await references.FindBlockersAsync(tenant, [], Ct));
            var outcome = await references.RemoveAsync(tenant, [], Ct);
            Assert.Empty(outcome.Counts);
            Assert.Empty(outcome.StorageKeys);
            Assert.Empty(await store.FindDeletableAsync(tenant, [], Ct));
            Assert.Empty(await store.SnapshotConsentsAsync(tenant, [], Ct));
            Assert.Empty(await store.DeleteAsync(tenant, [], Ct));
        }
        finally
        {
            fixture.Counter.Active = false;
        }

        Assert.Equal(0, fixture.Counter.Count);
    }

    // Sin la transacción del DbContext, el candado se soltaría al terminar cada sentencia y cada DELETE confirmaría solo.
    [Fact]
    public async Task WithoutATransactionTheReferencesRefuse()
    {
        var tenant = Guid.CreateVersion7();
        var customer = await InsertCustomerAsync(tenant, Name, Phone);
        await using var scope = fixture.Services.CreateAsyncScope();
        var references = scope.ServiceProvider.GetRequiredService<ICustomerDeletionReferences>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => references.LockAsync(tenant, [customer], Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => references.FindBlockersAsync(tenant, [customer], Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => references.RemoveAsync(tenant, [customer], Ct));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // El borrado, como lo corre el handler
    // ---------------------------------------------------------------------------------------------------------------------

    private async Task<Deletion> DeleteAsync(Guid tenant, Guid[] ids)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var references = services.GetRequiredService<ICustomerDeletionReferences>();
        var store = services.GetRequiredService<ICustomerDeletionStore>();
        await using var transaction = await services.GetRequiredService<ICustomersUnitOfWork>().BeginTransactionAsync(Ct);

        await references.LockAsync(tenant, ids, Ct);
        var found = await store.FindDeletableAsync(tenant, ids, Ct);
        var blockers = await references.FindBlockersAsync(tenant, [.. found.Select(customer => customer.Id.Value)], Ct);
        var deletable = found.Where(customer => !blockers.ContainsKey(customer.Id.Value)).ToArray();
        var deletableIds = deletable.Select(customer => customer.Id.Value).ToArray();
        var consents = await store.SnapshotConsentsAsync(
            tenant, [.. deletable.Select(customer => (customer.Id.Value, customer.Name, customer.PhoneE164))], Ct);
        var outcome = await references.RemoveAsync(tenant, deletableIds, Ct);
        var merged = await store.DeleteAsync(tenant, deletableIds, Ct);
        await transaction.CommitAsync(Ct);
        return new Deletion(blockers, consents, outcome, merged);
    }

    // Hasta que alguna sesión de esta base esté esperando un candado (el borrado, detrás del runner). Si el borrado termina o falla antes,
    // no esperó a nadie: la prueba ya no tiene qué mirar.
    private async Task WaitUntilSomeoneWaitsForALockAsync(Task deletion)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Assert.False(deletion.IsCompleted, "The deletion finished without waiting for the chatbot run.");
            if (await ScalarAsync(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The deletion never waited for a lock.");
    }

    // Los candados van uno por cliente a propósito (orden garantizado): se cuenta todo lo demás.
    private async Task<int> CountCommandsAsync(Guid tenant, Guid[] ids)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var references = services.GetRequiredService<ICustomerDeletionReferences>();
        var store = services.GetRequiredService<ICustomerDeletionStore>();
        await using var transaction = await services.GetRequiredService<ICustomersUnitOfWork>().BeginTransactionAsync(Ct);
        await references.LockAsync(tenant, ids, Ct);

        fixture.Counter.Reset();
        fixture.Counter.Active = true;
        try
        {
            var found = await store.FindDeletableAsync(tenant, ids, Ct);
            Assert.Equal(ids.Length, found.Count);
            Assert.Empty(await references.FindBlockersAsync(tenant, ids, Ct));
            await store.SnapshotConsentsAsync(tenant, [.. found.Select(customer => (customer.Id.Value, customer.Name, customer.PhoneE164))], Ct);
            var outcome = await references.RemoveAsync(tenant, ids, Ct);
            Assert.All(ids, id => Assert.Equal(50, outcome.Counts[id].Messages));
            await store.DeleteAsync(tenant, ids, Ct);
        }
        finally
        {
            fixture.Counter.Active = false;
        }

        await transaction.CommitAsync(Ct);
        return fixture.Counter.Count;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Estado observable de la huella
    // ---------------------------------------------------------------------------------------------------------------------

    private static string[] StateBefore(Footprint f) =>
    [
        "customer=1", "merged=1", "addresses=1", "notes=1", "tags=1", "consent=|", "conversation=1", "messages=3", "media=1",
        "scheduled=1", "runs=1", "units=1", "httpCalls=1", "inbox=1",
        $"sentRecipient=Sent|{f.Customer}|{f.Conversation}|{f.Messages[1]}|",
        $"pendingRecipient=Pending|{f.Customer}|||",
        $"draftCampaign={{{f.Customer},{f.Bystander}}}|1",
        "deal=1", "dealHistory=1", "triage=1", $"sale={f.Customer}|{Name}", "profile=1", "occurrence=1", $"shopify={f.Customer}",
        $"bystanderConversation={f.Bystander}", $"bystanderSale={f.Bystander}", $"bystanderShopify={f.Bystander}",
    ];

    private static string[] StateAfter(Footprint f) =>
    [
        "customer=0", "merged=0", "addresses=0", "notes=0", "tags=0", $"consent={Name}|{Phone}", "conversation=0", "messages=0",
        "media=0", "scheduled=0", "runs=0", "units=0", "httpCalls=0", "inbox=0",
        "sentRecipient=Sent||||",
        "pendingRecipient=Skipped||||unreachable",
        $"draftCampaign={{{f.Bystander}}}|2",
        "deal=0", "dealHistory=0", "triage=0", $"sale=|{Name}", "profile=0", "occurrence=0", "shopify=",
        $"bystanderConversation={f.Bystander}", $"bystanderSale={f.Bystander}", $"bystanderShopify={f.Bystander}",
    ];

    private async Task<string[]> StateAsync(Footprint f)
    {
        var state = await TextArrayAsync(
            """
            SELECT ARRAY[
                'customer=' || (SELECT count(*) FROM customers.customers WHERE id = @customer),
                'merged=' || (SELECT count(*) FROM customers.customers WHERE id = @merged),
                'addresses=' || (SELECT count(*) FROM customers.customer_addresses WHERE customer_id = @customer),
                'notes=' || (SELECT count(*) FROM customers.customer_notes WHERE customer_id = @customer),
                'tags=' || (SELECT count(*) FROM customers.customer_tags WHERE customer_id = @customer),
                'consent=' || (SELECT coalesce(customer_name, '') || '|' || coalesce(customer_phone_e164, '')
                               FROM customers.data_consents WHERE id = @consent),
                'conversation=' || (SELECT count(*) FROM messaging.conversations WHERE id = @conversation),
                'messages=' || (SELECT count(*) FROM messaging.messages WHERE id = ANY(@messages)),
                'media=' || (SELECT count(*) FROM messaging.message_media WHERE message_id = ANY(@messages)),
                'scheduled=' || (SELECT count(*) FROM messaging.scheduled_messages WHERE id = @scheduled),
                'runs=' || (SELECT count(*) FROM automations.conversation_runs WHERE id = @run),
                'units=' || (SELECT count(*) FROM automations.run_units WHERE run_id = @run),
                'httpCalls=' || (SELECT count(*) FROM automations.http_calls WHERE run_id = @run),
                'inbox=' || (SELECT count(*) FROM messaging.automation_inbox WHERE message_id = @inbox),
                'sentRecipient=' || (SELECT status || '|' || coalesce(customer_id::text, '') || '|' || coalesce(conversation_id::text, '')
                                     || '|' || coalesce(message_id::text, '') || '|' || coalesce(failure_code, '')
                                     FROM messaging.campaign_recipients WHERE id = @sentRecipient),
                'pendingRecipient=' || (SELECT status || '|' || coalesce(customer_id::text, '') || '|' || coalesce(conversation_id::text, '')
                                        || '|' || coalesce(message_id::text, '') || '|' || coalesce(failure_code, '')
                                        FROM messaging.campaign_recipients WHERE id = @pendingRecipient),
                'draftCampaign=' || (SELECT audience_customer_ids::text || '|' || version FROM messaging.campaigns WHERE id = @draft),
                'deal=' || (SELECT count(*) FROM pipeline.deals WHERE id = @deal),
                'dealHistory=' || (SELECT count(*) FROM pipeline.deal_stage_history WHERE deal_id = @deal),
                'triage=' || (SELECT count(*) FROM pipeline.triage_decisions WHERE id = @triage),
                'sale=' || (SELECT coalesce(customer_id::text, '') || '|' || customer_name FROM pos.sales WHERE id = @sale),
                'profile=' || (SELECT count(*) FROM billing.customer_profiles WHERE tenant_id = @tenant AND customer_id = @customer),
                'occurrence=' || (SELECT count(*) FROM geography.unresolved_city_occurrences WHERE id = @occurrence),
                'shopify=' || (SELECT coalesce(qep_customer_id::text, '') FROM shopify.orders WHERE id = @shopify),
                'bystanderConversation=' || (SELECT customer_id::text FROM messaging.conversations WHERE id = @bystanderConversation),
                'bystanderSale=' || (SELECT customer_id::text FROM pos.sales WHERE id = @bystanderSale),
                'bystanderShopify=' || (SELECT qep_customer_id::text FROM shopify.orders WHERE id = @bystanderShopify)]
            """,
            ("customer", f.Customer), ("merged", f.Merged), ("consent", f.Consent), ("conversation", f.Conversation),
            ("messages", f.Messages), ("scheduled", f.ScheduledMessage), ("run", f.Run), ("inbox", f.InboxMessage),
            ("sentRecipient", f.SentRecipient), ("pendingRecipient", f.PendingRecipient), ("draft", f.DraftCampaign),
            ("deal", f.Deal), ("triage", f.Triage), ("sale", f.Sale), ("tenant", f.Tenant), ("occurrence", f.Occurrence),
            ("shopify", f.ShopifyOrder), ("bystanderConversation", f.BystanderConversation), ("bystanderSale", f.BystanderSale),
            ("bystanderShopify", f.BystanderShopifyOrder));
        return [.. state.Select(line => line ?? "(sin fila)")];
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Siembra
    // ---------------------------------------------------------------------------------------------------------------------

    private async Task<Footprint> SeedFootprintAsync()
    {
        var tenant = Guid.CreateVersion7();
        var customer = await InsertCustomerAsync(tenant, Name, Phone);
        var merged = await InsertCustomerAsync(tenant, "Ana B", null, mergedInto: customer);
        var bystander = await InsertCustomerAsync(tenant, "Sofía", "+573009998877");
        var footprint = new Footprint(
            tenant, customer, merged, bystander,
            Conversation: Guid.CreateVersion7(),
            Messages: [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()],
            ScheduledMessage: Guid.CreateVersion7(),
            Run: Guid.CreateVersion7(),
            InboxMessage: Guid.CreateVersion7(),
            SentCampaign: Guid.CreateVersion7(),
            SentRecipient: Guid.CreateVersion7(),
            PendingCampaign: Guid.CreateVersion7(),
            PendingRecipient: Guid.CreateVersion7(),
            DraftCampaign: Guid.CreateVersion7(),
            Deal: Guid.CreateVersion7(),
            Triage: Guid.CreateVersion7(),
            Sale: Guid.CreateVersion7(),
            Occurrence: Guid.CreateVersion7(),
            ShopifyOrder: Guid.CreateVersion7(),
            Consent: Guid.CreateVersion7(),
            BystanderConversation: Guid.CreateVersion7(),
            BystanderSale: Guid.CreateVersion7(),
            BystanderShopifyOrder: Guid.CreateVersion7(),
            MediaKey: $"tenants/{tenant:N}/messaging/{Guid.NewGuid():N}.jpg",
            ScheduledKey: $"tenants/{tenant:N}/messaging/scheduled/{Guid.NewGuid():N}.jpg");

        await SeedAsync(
            """
            INSERT INTO customers.customer_addresses (id, customer_id, name, address, is_principal, created_at, updated_at, country, city_name)
            VALUES (gen_random_uuid(), @customer, 'Casa', 'Calle 1', true, now(), now(), 'US', 'Miami');
            INSERT INTO customers.customer_notes (id, tenant_id, customer_id, body, author_user_id, version, created_at, updated_at)
            VALUES (gen_random_uuid(), @tenant, @customer, 'Llamar el lunes', @member, 1, now(), now());
            INSERT INTO customers.tags (id, tenant_id, name, color, version, created_at, updated_at)
            VALUES (@tag, @tenant, 'VIP ' || @suffix, 'blue', 1, now(), now());
            INSERT INTO customers.customer_tags (customer_id, tag_id, tenant_id, assigned_by_user_id, assigned_at)
            VALUES (@customer, @tag, @tenant, @member, now());
            INSERT INTO customers.data_consents (id, tenant_id, customer_id, event, occurred_at, channel, channel_user_id, wa_id,
                conversation_id, actor_kind, shown_text, reply_message_id, chatbot_id, chatbot_name, chatbot_version_id,
                chatbot_version_number, node_id)
            VALUES (@consent, @tenant, @customer, 'Accepted', now(), 'WhatsApp', '573001112233', '573001112233', @conversation,
                'Chatbot', '¿Aceptas?', @message0, @member, 'Bot', @member, 1, 'node-1');

            INSERT INTO messaging.conversations (id, tenant_id, connection_id, status, last_activity_at, created_at, updated_at, version,
                customer_id, wa_id)
            VALUES (@conversation, @tenant, @connection, 'Open', now(), now(), now(), 1, @customer, '573001112233'),
                   (@bystanderConversation, @tenant, @connection, 'Open', now(), now(), now(), 1, @bystander, '573009998877');
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, created_at)
            VALUES (@message0, @conversation, @tenant, @connection, now(), 1, 1, 2, now()),
                   (@message1, @conversation, @tenant, @connection, now(), 2, 1, 2, now()),
                   (@message2, @conversation, @tenant, @connection, now(), 1, 2, 2, now());
            INSERT INTO messaging.message_media (message_id, mime_type, meta_media_id, next_attempt_at, storage_key, stored_at)
            VALUES (@message2, 'image/jpeg', 'meta-1', now(), @mediaKey, now());
            INSERT INTO messaging.scheduled_messages (id, tenant_id, conversation_id, client_id, kind, is_voice_note, scheduled_at, status,
                author_member_id, created_by_member_id, created_at, version, media_mime_type, media_storage_key)
            VALUES (@scheduled, @tenant, @conversation, gen_random_uuid(), 'Image', false, now() + interval '1 day', 'Scheduled',
                @member, @member, now(), 1, 'image/jpeg', @scheduledKey);
            INSERT INTO automations.conversation_runs (id, tenant_id, conversation_id, connection_id, chatbot_id, chatbot_version_id,
                status, started_at, updated_at)
            VALUES (@run, @tenant, @conversation, @connection, @member, @member, 'Active', now(), now());
            INSERT INTO automations.run_units (tenant_id, run_id, base_step, trigger, outcome, started_at, ended_at)
            VALUES (@tenant, @run, 0, 'Inbound', 'Waiting', now(), now());
            INSERT INTO automations.http_calls (tenant_id, node_id, connection_id, method, path, duration_ms, outcome, called_at, run_id)
            VALUES (@tenant, 'node-2', @connection, 'GET', '/precio', 12, 'Success', now(), @run);
            INSERT INTO messaging.automation_inbox (message_id, tenant_id, connection_id, conversation_id, opened, created_at, next_attempt_at)
            VALUES (@inbox, @tenant, @connection, @conversation, false, now(), now());

            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category,
                parameters, audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
            VALUES (@sentCampaign, @tenant, @connection, 'Enviada', 'promo', 'es', 'MARKETING', '[]'::jsonb, '{}'::uuid[],
                    ARRAY[@customer], 'Completed', @member, now(), now(), 1),
                   (@pendingCampaign, @tenant, @connection, 'Enviando', 'promo', 'es', 'MARKETING', '[]'::jsonb, '{}'::uuid[],
                    ARRAY[@customer], 'Sending', @member, now(), now(), 1),
                   (@draft, @tenant, @connection, 'Borrador', 'promo', 'es', 'MARKETING', '[]'::jsonb, '{}'::uuid[],
                    ARRAY[@customer, @bystander], 'Draft', @member, now(), now(), 1);
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, status, next_attempt_at,
                conversation_id, message_id, sent_at, wa_id)
            VALUES (@sentRecipient, @sentCampaign, @tenant, @customer, @name, 'Sent', now(), @conversation, @message1, now(), '573001112233'),
                   (@pendingRecipient, @pendingCampaign, @tenant, @customer, @name, 'Pending', now(), NULL, NULL, NULL, '573001112233');

            INSERT INTO pipeline.deals (id, tenant_id, pipeline_id, customer_id, owner_member_id, title, currency, stage_id,
                stage_entered_at, source, last_changed_at, created_at, updated_at, version, history_count, origin_conversation_id)
            VALUES (@deal, @tenant, @member, @customer, @member, 'Negocio de Ana', 'COP', @member, now(), 'Conversation', now(), now(),
                now(), 1, 1, @conversation);
            INSERT INTO pipeline.deal_stage_history (id, tenant_id, deal_id, pipeline_id, ordinal, to_stage_id, occurred_at, actor_kind,
                currency, probability)
            VALUES (gen_random_uuid(), @tenant, @deal, @member, 1, @member, now(), 'System', 'COP', 10);
            INSERT INTO pipeline.triage_decisions (id, tenant_id, conversation_id, customer_id, kind, decided_at,
                decided_against_inbound_at, actor_kind, deal_id)
            VALUES (@triage, @tenant, @conversation, @customer, 'Created', now(), now(), 'System', @deal);

            INSERT INTO pos.cash_sessions (id, tenant_id, cashier_id, cashier_name, company_id, company_name, company_tax_id,
                company_document_type, status, opening_float, opened_at, sales_count, voided_count, sales_total, cash_total, card_total,
                transfer_total, version, created_at, updated_at)
            VALUES (@session, @tenant, @member, 'Caja', @member, 'Empresa', '900', 'NIT', 'Closed', 0, now(), 0, 0, 0, 0, 0, 0, 1, now(), now());
            INSERT INTO pos.sales (id, request_fingerprint, tenant_id, cash_session_id, cashier_id, sale_number, customer_id,
                customer_name, customer_identification_number, subtotal, tax_amount, discount_amount, total, change_amount, status, created_at)
            VALUES (@sale, repeat('a', 64), @tenant, @session, @member, 'POS-1-' || @suffix, @customer, @name, '123', 0, 0, 0, 0, 0,
                    'Completed', now()),
                   (@bystanderSale, repeat('b', 64), @tenant, @session, @member, 'POS-2-' || @suffix, @bystander, 'Sofía', '456', 0, 0, 0,
                    0, 0, 'Completed', now());

            INSERT INTO billing.customer_profiles (tenant_id, customer_id, channel, assignee_member_id, version, created_at, updated_at)
            VALUES (@tenant, @customer, 'WhatsApp', @member, 1, now(), now());
            INSERT INTO geography.unresolved_city_occurrences (id, unresolved_city_id, tenant_id, source, source_ref, order_id,
                party_city_only, customer_city_only, seen_at, customer_id)
            VALUES (@occurrence, gen_random_uuid(), @tenant, 'Shopify', gen_random_uuid(), gen_random_uuid(), false, true, now(), @customer);
            INSERT INTO shopify.orders (id, tenant_id, connection_id, shopify_order_id, order_name, shopify_created_at, shopify_updated_at,
                currency, total_price, taxes_included, payload, import_state, attempts, version, qep_customer_id)
            VALUES (@shopify, @tenant, @connection, @shopifyOrderId, '#1-' || @suffix, now(), now(), 'COP', 0, false, '{}'::jsonb,
                    'Imported', 0, 1, @customer),
                   (@bystanderShopify, @tenant, @connection, @shopifyOrderId + 1, '#2-' || @suffix, now(), now(), 'COP', 0, false,
                    '{}'::jsonb, 'Imported', 0, 1, @bystander);
            """,
            ("tenant", tenant), ("customer", customer), ("bystander", bystander), ("member", Guid.CreateVersion7()),
            ("connection", Guid.CreateVersion7()), ("tag", Guid.CreateVersion7()), ("suffix", Guid.NewGuid().ToString("N")[..10]),
            ("consent", footprint.Consent), ("conversation", footprint.Conversation),
            ("bystanderConversation", footprint.BystanderConversation), ("message0", footprint.Messages[0]),
            ("message1", footprint.Messages[1]), ("message2", footprint.Messages[2]), ("mediaKey", footprint.MediaKey),
            ("scheduled", footprint.ScheduledMessage), ("scheduledKey", footprint.ScheduledKey), ("run", footprint.Run),
            ("inbox", footprint.InboxMessage), ("sentCampaign", footprint.SentCampaign), ("pendingCampaign", footprint.PendingCampaign),
            ("draft", footprint.DraftCampaign), ("sentRecipient", footprint.SentRecipient),
            ("pendingRecipient", footprint.PendingRecipient), ("name", Name), ("deal", footprint.Deal), ("triage", footprint.Triage),
            ("session", Guid.CreateVersion7()), ("sale", footprint.Sale), ("bystanderSale", footprint.BystanderSale),
            ("occurrence", footprint.Occurrence), ("shopify", footprint.ShopifyOrder),
            ("bystanderShopify", footprint.BystanderShopifyOrder), ("shopifyOrderId", Random.Shared.NextInt64(1, long.MaxValue / 2)));
        return footprint;
    }

    private async Task<(Guid Tenant, Guid[] Customers)> SeedCustomersWithMessagesAsync(int customers, int messagesEach)
    {
        var tenant = Guid.CreateVersion7();
        var ids = Enumerable.Range(0, customers).Select(_ => Guid.CreateVersion7()).ToArray();
        var conversations = ids.Select(_ => Guid.CreateVersion7()).ToArray();
        await SeedAsync(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at,
                completeness, phone_e164)
            SELECT seeded.id, @tenant, 'Cliente ' || seeded.n, true, false, false, 1, now(), now(), 'Incomplete',
                   '+5730000' || lpad(seeded.n::text, 5, '0')
            FROM unnest(@ids) WITH ORDINALITY AS seeded (id, n);
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, status, last_activity_at, created_at, updated_at, version,
                customer_id, wa_id)
            SELECT seeded.conversation, @tenant, @connection, 'Open', now(), now(), now(), 1, seeded.customer,
                   '5730000' || lpad(seeded.n::text, 5, '0')
            FROM unnest(@ids, @conversations) WITH ORDINALITY AS seeded (customer, conversation, n);
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, created_at)
            SELECT gen_random_uuid(), conversation.id, @tenant, @connection, now(), 1, 1, 2, now()
            FROM unnest(@conversations) AS conversation (id) CROSS JOIN generate_series(1, @messagesEach);
            """,
            ("tenant", tenant), ("ids", ids), ("conversations", conversations), ("connection", Guid.CreateVersion7()),
            ("messagesEach", messagesEach));
        return (tenant, ids);
    }

    private async Task SeedBlockerAsync(Guid tenant, Guid customer, string row)
    {
        var member = Guid.CreateVersion7();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        switch (row)
        {
            case "quotation":
                await SeedAsync(
                    """
                    INSERT INTO quotations.quotations (id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, subtotal,
                        tax_percentage, tax_amount, discount_amount, total, created_by, updated_at, version)
                    VALUES (gen_random_uuid(), @tenant, 'COT-' || @suffix, @customer, @member, 'Draft', now(), 0, 0, 0, 0, 0, @member, now(), 1);
                    """,
                    ("tenant", tenant), ("customer", customer), ("member", member), ("suffix", suffix));
                break;
            case "order":
                await SeedAsync(
                    """
                    INSERT INTO quotations.orders (id, tenant_id, order_number, status, payment_status, converted_at, converted_by, created_at,
                        updated_at, version, client_id, advisor_id, currency, billing_company_id, billing_bank_name, billing_account_number,
                        billing_account_currency, source)
                    VALUES (gen_random_uuid(), @tenant, 'PED-' || @suffix, 'Cancelled', 'PaymentPending', now(), @member, now(), now(), 1,
                        @customer, @member, 'COP', @member, 'Banco', '123', 'COP', 'Manual');
                    """,
                    ("tenant", tenant), ("customer", customer), ("member", member), ("suffix", suffix));
                break;
            case "loan":
                await SeedAsync(
                    """
                    INSERT INTO loans.loans (id, tenant_id, customer_id, name, type, principal_amount, outstanding_principal, currency,
                        monthly_rate_percent, disbursed_on, cutoff_rule, status, last_generated_period_end, version, created_at, updated_at)
                    VALUES (gen_random_uuid(), @tenant, @customer, 'Novasteel', 'InterestOnly', 1000000, 1000000, 'COP', 1.5,
                        DATE '2026-08-15', 'EndOfMonth', 'Active', DATE '2026-08-31', 1, now(), now());
                    """,
                    ("tenant", tenant), ("customer", customer));
                break;
            case "service":
                await SeedAsync(
                    """
                    INSERT INTO billing.services (id, tenant_id, customer_id, name, price, currency, cycle_months, first_due_date, status,
                        version, created_at, status_changed_at, updated_at)
                    VALUES (gen_random_uuid(), @tenant, @customer, 'Hosting', 100, 'COP', 1, DATE '2026-12-01', 'Cancelled', 1, now(), now(), now());
                    """,
                    ("tenant", tenant), ("customer", customer));
                break;
            case "charge":
                // El cobro sin su servicio: lo que lo bloquea es la fila de billing.charges, no la del servicio.
                await SeedAsync(
                    """
                    INSERT INTO billing.charges (id, tenant_id, service_id, customer_id, amount, currency, due_date, status, version, created_at)
                    VALUES (gen_random_uuid(), @tenant, gen_random_uuid(), @customer, 100, 'COP', DATE '2026-12-01', 'Paid', 1, now());
                    """,
                    ("tenant", tenant), ("customer", customer));
                break;
            case "blockedConversation":
                await InsertConversationAsync(tenant, customer, blocked: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, null);
        }
    }

    private async Task<Guid> InsertConversationAsync(Guid tenant, Guid customer, bool blocked)
    {
        var id = Guid.CreateVersion7();
        await SeedAsync(
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, status, last_activity_at, created_at, updated_at, version,
                customer_id, wa_id, blocked_at, blocked_by_member_id, blocked_wa_id)
            VALUES (@id, @tenant, gen_random_uuid(), 'Open', now(), now(), now(), 1, @customer, @wa,
                CASE WHEN @blocked THEN now() END, CASE WHEN @blocked THEN gen_random_uuid() END, CASE WHEN @blocked THEN @wa END);
            """,
            ("id", id), ("tenant", tenant), ("customer", customer), ("wa", Random.Shared.NextInt64(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture)),
            ("blocked", blocked));
        return id;
    }

    private async Task<Guid> InsertCustomerAsync(Guid tenant, string name, string? phoneE164, Guid? mergedInto = null)
    {
        var id = Guid.CreateVersion7();
        await SeedAsync(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at,
                completeness, phone_e164, merged_into_id, merged_at)
            VALUES (@id, @tenant, @name, @active, false, false, 1, now(), now(), 'Incomplete', @phone, @merged,
                CASE WHEN @merged::uuid IS NULL THEN NULL ELSE now() END);
            """,
            ("id", id), ("tenant", tenant), ("name", name), ("active", mergedInto is null), ("phone", phoneE164), ("merged", mergedInto));
        return id;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // SQL crudo
    // ---------------------------------------------------------------------------------------------------------------------

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
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    private async Task<string?[]> TextArrayAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        return (string?[])(await command.ExecuteScalarAsync(Ct))!;
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
