using System.Reflection;
using System.Runtime.ExceptionServices;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Automations.Application;
using Modules.Automations.Domain.Engine;
using Modules.Messaging.Application;
using Npgsql;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-26 D-5, D-7 y D-10: el adaptador de Bootstrapper que resuelve el cliente de la conversación para el bloque
/// «Política de datos», contra PostgreSQL. Conversación y entrantes se siembran por SQL, como en
/// <see cref="AutomationConversationsAdapterTests"/>; el aviso en vivo se graba con un notificador falso.</summary>
public sealed class AutomationDataConsentsAdapterTests
{
    private const string UserId = "CO.laura1";
    private const string WaId = "573001234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory,
        WebApplicationFactory<Program> Host,
        string ConnectionString,
        Guid TenantId,
        Guid ConnectionId,
        Guid ConversationId,
        RecordingNotifier Notifier,
        EnsureFailure EnsureFailure);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string? userId = UserId)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var notifier = new RecordingNotifier();
        var failure = new EnsureFailure();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessagingNotifier>();
            services.AddSingleton<IMessagingNotifier>(notifier);
            // El directorio real, salvo cuando la prueba pide que EnsureAsync falle.
            var directory = services.Single(descriptor => descriptor.ServiceType == typeof(IMessagingCustomerDirectory));
            services.Remove(directory);
            services.AddScoped(provider => FailingEnsureDirectory.Wrap(
                (IMessagingCustomerDirectory)ActivatorUtilities.CreateInstance(provider, directory.ImplementationType!), failure));
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(connectionString, tenant.TenantId, connectionId, userId, WaId, "Laura Gómez");
        return new Fixture(factory, host, connectionString, tenant.TenantId, connectionId, conversationId, notifier, failure);
    }

    private static async Task<Guid> SeedConversationAsync(
        string connectionString, Guid tenantId, Guid connectionId, string? userId, string waId, string profileName)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, profile_name, status, unread_count,
                last_inbound_at, last_activity_at, created_at, updated_at, version)
            VALUES (@id, @t, @c, @u, @wa, @name, 'Open', 1, now() - interval '5 minutes', now(), now(), now(), 1)
            """,
            ("id", id), ("t", tenantId), ("c", connectionId), ("u", (object?)userId ?? DBNull.Value), ("wa", waId), ("name", profileName));
        return id;
    }

    private static async Task<Guid> SeedInboundAsync(Fixture f, string wamid, Guid? conversationId = null, Guid? replyTo = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid,
                reply_to_message_id, created_at)
            SELECT @id, c.id, c.tenant_id, c.connection_id, now() - interval '1 minute', 1, 1, 2, 'Acepto', @wamid, @replyTo, now()
              FROM messaging.conversations AS c WHERE c.id = @c
            """,
            ("id", id), ("c", conversationId ?? f.ConversationId), ("wamid", wamid), ("replyTo", (object?)replyTo ?? DBNull.Value));
        return id;
    }

    private static async Task<T> WithAdapterAsync<T>(Fixture f, Func<IAutomationDataConsents, Task<T>> action)
    {
        using var scope = f.Host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAutomationDataConsents>());
    }

    private static Task<DataConsentCheck> CheckAsync(Fixture f, Guid? conversationId = null) =>
        WithAdapterAsync(f, adapter => adapter.CheckAsync(f.TenantId, conversationId ?? f.ConversationId, Ct));

    private static Task<bool> RecordAsync(Fixture f, Guid replyMessageId, DataConsentDecision decision = DataConsentDecision.Accepted) =>
        WithAdapterAsync(f, adapter => adapter.RecordAsync(Record(f, replyMessageId, decision), Ct));

    private static DataConsentRecord Record(Fixture f, Guid replyMessageId, DataConsentDecision decision) => new(
        f.TenantId, f.ConversationId, decision, "¿Aceptas nuestra política de datos?", Guid.CreateVersion7(), replyMessageId,
        Guid.CreateVersion7(), "Asistente de ventas", Guid.CreateVersion7(), 3, "consent");

    private static async Task<Guid?> ConversationCustomerAsync(Fixture f, Guid? conversationId = null)
    {
        var value = await ScalarAsync<object>(f.ConnectionString,
            "SELECT customer_id FROM messaging.conversations WHERE id = @id", ("id", conversationId ?? f.ConversationId));
        return value is Guid id ? id : null;
    }

    private static Task<long> ConsentRowsAsync(Fixture f) => CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.data_consents");

    /// <summary>D-7: sin cliente en la conversación se crea (o encuentra) por el BSUID y se cuelga, como la ingesta; la segunda vez ya
    /// está colgado y no hay otro aviso.</summary>
    [Fact]
    public async Task ACheckWithoutCustomerCreatesAndLinksOne()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        Assert.Equal(DataConsentCheck.NotAccepted, await CheckAsync(f));

        var customerId = await ConversationCustomerAsync(f);
        Assert.NotNull(customerId);
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.customers WHERE tenant_id = @t AND whatsapp_user_id = 'CO.laura1' AND id = @id",
            ("t", f.TenantId), ("id", customerId.Value)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @c AND direction = 3 AND details->>'type' = 'CustomerCreated'",
            ("c", f.ConversationId)));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);

        Assert.Equal(DataConsentCheck.NotAccepted, await CheckAsync(f));

        Assert.Equal(customerId, await ConversationCustomerAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE tenant_id = @t", ("t", f.TenantId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @c AND direction = 3", ("c", f.ConversationId)));
        Assert.Equal([$"changed {f.ConversationId}"], f.Notifier.Events);
    }

    [Fact]
    public async Task ACheckWithAnAcceptedCustomerSaysAccepted()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var reply = await SeedInboundAsync(f, "wamid.A1");

        Assert.Equal(DataConsentCheck.NotAccepted, await CheckAsync(f));
        Assert.True(await RecordAsync(f, reply));

        Assert.Equal(DataConsentCheck.Accepted, await CheckAsync(f));
    }

    /// <summary>D-7: sin BSUID no hay a quién crear ni identidad que guardar; el bot sale por «No acepto» sin preguntar.</summary>
    [Fact]
    public async Task AConversationWithoutChannelIdentityIsUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, userId: null);
        using var _ = f.Factory;
        var reply = await SeedInboundAsync(f, "wamid.U1");

        Assert.Equal(DataConsentCheck.Unavailable, await CheckAsync(f));
        Assert.False(await RecordAsync(f, reply));

        Assert.Null(await ConversationCustomerAsync(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE tenant_id = @t", ("t", f.TenantId)));
        Assert.Equal(0L, await ConsentRowsAsync(f));
        Assert.Empty(f.Notifier.Events);
    }

    [Fact]
    public async Task AConversationOfAnotherTenantIsUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        Assert.Equal(DataConsentCheck.Unavailable,
            await WithAdapterAsync(f, adapter => adapter.CheckAsync(Guid.CreateVersion7(), f.ConversationId, Ct)));

        Assert.Null(await ConversationCustomerAsync(f));
    }

    /// <summary>D-5, D-10: la fila guarda la identidad de canal de la conversación y el wamid del entrante que respondió, con su hora.</summary>
    [Fact]
    public async Task ARecordStoresChannelIdentityAndTheReplyWamid()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var reply = await SeedInboundAsync(f, "wamid.R1");

        Assert.True(await RecordAsync(f, reply));

        var customerId = await ConversationCustomerAsync(f);
        Assert.NotNull(customerId);
        Assert.Equal(
            $"{customerId}|Accepted|WhatsApp|CO.laura1|573001234567|{f.ConversationId}|wamid.R1|true|Asistente de ventas|3|Chatbot|consent",
            await ScalarAsync<string>(f.ConnectionString,
                """
                SELECT concat_ws('|', consent.customer_id, consent.event, consent.channel, consent.channel_user_id, consent.wa_id,
                       consent.conversation_id, consent.reply_wamid, (consent.occurred_at = message.occurred_at)::text,
                       consent.chatbot_name, consent.chatbot_version_number, consent.actor_kind, consent.node_id)
                  FROM customers.data_consents AS consent
                  JOIN messaging.messages AS message ON message.id = consent.reply_message_id
                 WHERE consent.reply_message_id = @r
                """,
                ("r", reply)));
        Assert.Equal(1L, await ConsentRowsAsync(f));
    }

    /// <summary>Final review F1: la fila guarda el mensaje que citó el toque (la pregunta que se respondió), como id de QEP.</summary>
    [Fact]
    public async Task ARecordStoresTheQuotedMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var prompt = await SeedInboundAsync(f, "wamid.P1");
        var reply = await SeedInboundAsync(f, "wamid.P2", replyTo: prompt);
        var unquoted = await SeedInboundAsync(f, "wamid.P3");

        Assert.True(await RecordAsync(f, reply));
        Assert.True(await RecordAsync(f, unquoted));

        Assert.Equal(prompt, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT replied_to_message_id FROM customers.data_consents WHERE reply_message_id = @r", ("r", reply)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.data_consents WHERE reply_message_id = @r AND replied_to_message_id IS NULL", ("r", unquoted)));
    }

    /// <summary>Final review F2: la respuesta tiene que ser un entrante de la conversación del registro; uno de otra conversación (del
    /// mismo tenant) no es prueba de que este titular respondió y no crea a nadie.</summary>
    [Fact]
    public async Task AReplyFromAnotherConversationCannotBeRecorded()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await SeedConversationAsync(f.ConnectionString, f.TenantId, f.ConnectionId, "CO.marta2", "573009876543", "Marta Ruiz");
        var foreign = await SeedInboundAsync(f, "wamid.X1", conversationId: other);

        Assert.False(await RecordAsync(f, foreign));

        Assert.Equal(0L, await ConsentRowsAsync(f));
        Assert.Null(await ConversationCustomerAsync(f));
    }

    [Fact]
    public async Task ARejectionIsRecordedAsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var reply = await SeedInboundAsync(f, "wamid.N1");

        Assert.True(await RecordAsync(f, reply, DataConsentDecision.Rejected));

        Assert.Equal("Rejected", await ScalarAsync<string>(f.ConnectionString, "SELECT event FROM customers.data_consents"));
        Assert.Equal(DataConsentCheck.NotAccepted, await CheckAsync(f));
    }

    /// <summary>Un lote que se reintenta registra la misma respuesta otra vez: ya tiene fila y cuenta como registrada.</summary>
    [Fact]
    public async Task RecordingTheSameReplyTwiceKeepsOneRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var reply = await SeedInboundAsync(f, "wamid.D1");

        Assert.True(await RecordAsync(f, reply));
        Assert.True(await RecordAsync(f, reply));

        Assert.Equal(1L, await ConsentRowsAsync(f));
    }

    /// <summary>Review Focus 5: la respuesta es del cliente que la conversación tiene al responder, no del que tenía al preguntar.</summary>
    [Fact]
    public async Task TheReplyIsRecordedForTheCustomerTheConversationHasWhenReplying()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await SeedConversationAsync(f.ConnectionString, f.TenantId, f.ConnectionId, "CO.marta2", "573009876543", "Marta Ruiz");
        Assert.Equal(DataConsentCheck.NotAccepted, await CheckAsync(f, other));
        var customerB = (await ConversationCustomerAsync(f, other))!.Value;
        Assert.Equal(DataConsentCheck.NotAccepted, await CheckAsync(f));
        var customerA = (await ConversationCustomerAsync(f))!.Value;
        var first = await SeedInboundAsync(f, "wamid.F1");
        Assert.True(await RecordAsync(f, first, DataConsentDecision.Rejected));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @b WHERE id = @id", ("b", customerB), ("id", f.ConversationId));
        var second = await SeedInboundAsync(f, "wamid.F2");
        Assert.True(await RecordAsync(f, second));

        Assert.Equal(customerB, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT customer_id FROM customers.data_consents WHERE reply_message_id = @r", ("r", second)));
        Assert.Equal(customerA, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT customer_id FROM customers.data_consents WHERE reply_message_id = @r", ("r", first)));
    }

    [Fact]
    public async Task AnUnknownReplyCannotBeRecorded()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        Assert.False(await RecordAsync(f, Guid.CreateVersion7()));

        Assert.Equal(0L, await ConsentRowsAsync(f));
        // El entrante se lee antes de resolver el cliente: una respuesta que no existe no crea a nadie.
        Assert.Null(await ConversationCustomerAsync(f));
    }

    /// <summary>D-7: si crear o colgar el cliente falla, la conversación queda sin consentimiento consultable y el bot sale por
    /// «No acepto» sin preguntar.</summary>
    [Fact]
    public async Task AFailedEnsureLeavesTheCheckUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var reply = await SeedInboundAsync(f, "wamid.E1");
        f.EnsureFailure.Next = () => new InvalidOperationException("La base rechazó el cliente.");

        Assert.Equal(DataConsentCheck.Unavailable, await CheckAsync(f));
        Assert.False(await RecordAsync(f, reply));

        Assert.Null(await ConversationCustomerAsync(f));
        Assert.Equal(0L, await ConsentRowsAsync(f));
        Assert.Empty(f.Notifier.Events);
    }

    /// <summary>Un conflicto de concurrencia, un fallo transitorio de la base (aunque venga envuelto) o la cancelación del worker no son
    /// «no se pudo»: suben, y la unidad del motor se reintenta.</summary>
    [Theory]
    [InlineData("cancelled", typeof(OperationCanceledException))]
    [InlineData("concurrency", typeof(RequestConcurrencyException))]
    [InlineData("transient", typeof(InvalidOperationException))]
    public async Task ARetryableFailureWhileEnsuringPropagates(string kind, Type expected)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.EnsureFailure.Next = () => kind switch
        {
            "cancelled" => new OperationCanceledException(),
            "concurrency" => new RequestConcurrencyException("concurrency.conflict", "Deadlock detected."),
            _ => new InvalidOperationException("Retry limit exceeded.", new NpgsqlException("Connection lost.", new TimeoutException())),
        };

        var error = await Assert.ThrowsAnyAsync<Exception>(() => CheckAsync(f));

        Assert.IsType(expected, error);
        Assert.Null(await ConversationCustomerAsync(f));
    }

    /// <summary>Lo que la prueba pide que <c>EnsureAsync</c> lance; <c>null</c> = el directorio real.</summary>
    internal sealed class EnsureFailure
    {
        public Func<Exception>? Next { get; set; }
    }

    /// <summary>Delega todo en el directorio real, salvo <c>EnsureAsync</c> cuando la prueba pide un fallo.</summary>
    public class FailingEnsureDirectory : DispatchProxy
    {
        private IMessagingCustomerDirectory inner = null!;
        private EnsureFailure failure = null!;

        internal static IMessagingCustomerDirectory Wrap(IMessagingCustomerDirectory inner, EnsureFailure failure)
        {
            var proxy = Create<IMessagingCustomerDirectory, FailingEnsureDirectory>();
            var failing = (FailingEnsureDirectory)(object)proxy;
            failing.inner = inner;
            failing.failure = failure;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IMessagingCustomerDirectory.EnsureAsync) && failure.Next is { } next)
            {
                return Task.FromException<MessagingEnsuredCustomer>(next());
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed class RecordingNotifier : IMessagingNotifier
    {
        public List<string> Events { get; } = [];

        public Task ConversationChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add($"changed {conversationId}");
            }

            return Task.CompletedTask;
        }

        public Task InboundMessageAsync(Guid tenantId, Guid conversationId, Guid messageId, Guid? assignedMemberId, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add($"inbound {conversationId} {messageId} {assignedMemberId?.ToString() ?? "-"}");
            }

            return Task.CompletedTask;
        }
    }
}
