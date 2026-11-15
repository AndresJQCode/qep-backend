using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Fix round 1 del bloque C: el seguimiento del hilo no se queda trabado. Las filas sin clasificar que esperan al asesor no ocupan el lote
/// (el feed sólo da las que tienen un único candidato), y un entrante que revienta se salta sin frenar los demás. Host propio por prueba:
/// el feed lee toda la base y la segunda reemplaza un repositorio.
/// </summary>
public sealed class SupportThreadFollowerResilienceTests
{
    private static readonly DateTimeOffset Since = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 201 conversaciones con una fila esperando al asesor (dos candidatos cada una), todas más viejas que la reasignable: antes, el
    // lote de 200 conversaciones se llenaba con ellas en cada tick y la reasignable nunca llegaba.
    [Fact]
    public async Task AReassignableRowIsNotStarvedByRowsWaitingForTheAdvisor()
    {
        var (database, factory) = await OpenOwnHostAsync();
        await using var _ = database;
        await using var __ = factory;
        var tenant = Guid.Parse(NewTenantId());
        await SeedTenantAsync(factory, tenant.ToString());
        var conversation = await SeedConversationAsync(factory, tenant);
        var single = await SaveConversationTicketAsync(factory, tenant, conversation, Since);

        // Dos copias del ticket por cada una de 201 conversaciones sueltas (support.tickets no tiene FK a Mensajería), y una fila sin
        // clasificar por conversación.
        await ExecuteAsync(
            factory,
            """
            CREATE TEMP TABLE copies AS
                SELECT t.*, g AS n, k
                FROM support.tickets t CROSS JOIN generate_series(1, 201) g CROSS JOIN generate_series(0, 1) k
                WHERE t.id = @template;
            UPDATE copies SET id = gen_random_uuid(), number = 100000 + n * 2 + k,
                conversation_id = md5(@template::text || n::text)::uuid;
            ALTER TABLE copies DROP COLUMN n, DROP COLUMN k;
            INSERT INTO support.tickets SELECT * FROM copies;
            INSERT INTO support.unclassified_messages (message_id, tenant_id, conversation_id, occurred_at, created_at)
                SELECT gen_random_uuid(), tenant_id, conversation_id, @waiting, now()
                FROM (SELECT DISTINCT tenant_id, conversation_id FROM copies) waiting;
            """,
            ("template", single.Id.Value), ("waiting", Since.AddMinutes(1)));
        var reassignable = Guid.CreateVersion7();
        await ExecuteAsync(
            factory,
            """
            INSERT INTO support.unclassified_messages (message_id, tenant_id, conversation_id, occurred_at, created_at)
            VALUES (@message, @tenant, @conversation, @occurred, now())
            """,
            ("message", reassignable), ("tenant", tenant), ("conversation", conversation), ("occurred", Since.AddMinutes(2)));
        Assert.Equal(202, await ScalarAsync(
            factory, "SELECT count(*) FROM support.unclassified_messages WHERE tenant_id = @tenant AND resolution IS NULL", ("tenant", tenant)));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
        }

        Assert.Equal(
            "Reassigned",
            await TextAsync(factory, "SELECT resolution FROM support.unclassified_messages WHERE message_id = @id", ("id", reassignable)));
        Assert.Equal(
            "Linked",
            await TextAsync(
                factory,
                "SELECT state FROM support.ticket_messages WHERE ticket_id = @ticket AND message_id = @message",
                ("ticket", single.Id.Value), ("message", reassignable)));
        Assert.Equal(201, await ScalarAsync(
            factory, "SELECT count(*) FROM support.unclassified_messages WHERE tenant_id = @tenant AND resolution IS NULL", ("tenant", tenant)));
    }

    // Revisión del bloque C (CM4): una fila cuyos candidatos se cerraron todos no tiene a quién pasar. Queda pendiente para el asesor
    // («De ninguno»), sin vínculo, y no frena la fila reasignable de otra conversación en el mismo lote.
    [Fact]
    public async Task ARowWhoseCandidatesAllClosedStaysPendingAndDoesNotBlockAReassignableOne()
    {
        var (database, factory) = await OpenOwnHostAsync();
        await using var _ = database;
        await using var __ = factory;
        var tenant = Guid.Parse(NewTenantId());
        await SeedTenantAsync(factory, tenant.ToString());
        var orphaned = await SeedConversationAsync(factory, tenant);
        var live = await SeedConversationAsync(factory, tenant, waId: "573009998877");
        var closedA = await SaveConversationTicketAsync(factory, tenant, orphaned, Since);
        var closedB = await SaveConversationTicketAsync(factory, tenant, orphaned, Since);
        var single = await SaveConversationTicketAsync(factory, tenant, live, Since);
        await ExecuteAsync(
            factory,
            """
            UPDATE support.tickets
            SET status = 'Closed', closed_at = @closed, follow_thread = false, follow_off_at = @closed, version = version + 1
            WHERE id IN (@a, @b)
            """,
            ("closed", Since.AddMinutes(5)), ("a", closedA.Id.Value), ("b", closedB.Id.Value));
        var waiting = Guid.CreateVersion7();
        var reassignable = Guid.CreateVersion7();
        await ExecuteAsync(
            factory,
            """
            INSERT INTO support.unclassified_messages (message_id, tenant_id, conversation_id, occurred_at, created_at)
            VALUES (@waiting, @tenant, @orphaned, @first, now()), (@reassignable, @tenant, @live, @second, now())
            """,
            ("waiting", waiting), ("reassignable", reassignable), ("tenant", tenant), ("orphaned", orphaned), ("live", live),
            ("first", Since.AddMinutes(1)), ("second", Since.AddMinutes(2)));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
        }

        Assert.Null(await TextAsync(factory, "SELECT resolution FROM support.unclassified_messages WHERE message_id = @id", ("id", waiting)));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM support.ticket_messages WHERE message_id = @id", ("id", waiting)));
        Assert.Equal(
            "Reassigned",
            await TextAsync(factory, "SELECT resolution FROM support.unclassified_messages WHERE message_id = @id", ("id", reassignable)));
        Assert.Equal(
            "Linked",
            await TextAsync(
                factory,
                "SELECT state FROM support.ticket_messages WHERE ticket_id = @ticket AND message_id = @message",
                ("ticket", single.Id.Value), ("message", reassignable)));
    }

    // Un entrante que siempre revienta (acá, el repositorio de vínculos) se registra y se salta; el siguiente del lote se vincula igual.
    [Fact]
    public async Task AFailingInboundIsSkippedAndTheRestOfTheBatchIsProcessed()
    {
        var poison = Guid.CreateVersion7();
        var (database, factory) = await OpenOwnHostAsync(services =>
        {
            services.AddScoped<SupportTicketMessageRepository>();
            services.RemoveAll<ISupportTicketMessageRepository>();
            services.AddScoped<ISupportTicketMessageRepository>(provider =>
                new ThrowingTicketMessages(provider.GetRequiredService<SupportTicketMessageRepository>(), poison));
        });
        await using var _ = database;
        await using var __ = factory;
        var tenant = Guid.Parse(NewTenantId());
        await SeedTenantAsync(factory, tenant.ToString());
        var first = await SeedConversationAsync(factory, tenant);
        var second = await SeedConversationAsync(factory, tenant, waId: "573009998877");
        var poisoned = await SaveConversationTicketAsync(factory, tenant, first, Since);
        var healthy = await SaveConversationTicketAsync(factory, tenant, second, Since);
        await SeedMessageAsync(factory, tenant, first, Since.AddMinutes(1), messageId: poison);
        var good = await SeedMessageAsync(factory, tenant, second, Since.AddMinutes(2));

        int processed;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            processed = await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
        }

        Assert.Equal(1, processed);
        Assert.Equal(
            "Linked",
            await TextAsync(
                factory,
                "SELECT state FROM support.ticket_messages WHERE ticket_id = @ticket AND message_id = @message",
                ("ticket", healthy.Id.Value), ("message", good)));
        Assert.Equal(0, await ScalarAsync(
            factory, "SELECT count(*) FROM support.ticket_messages WHERE ticket_id = @ticket", ("ticket", poisoned.Id.Value)));
    }

    private sealed class ThrowingTicketMessages(ISupportTicketMessageRepository inner, Guid poison) : ISupportTicketMessageRepository
    {
        public Task<SupportTicketMessage?> FindAsync(
            Guid tenantId, SupportTicketId ticketId, Guid messageId, CancellationToken cancellationToken) =>
            inner.FindAsync(tenantId, ticketId, messageId, cancellationToken);

        public Task<IReadOnlyList<SupportTicketMessage>> ListVisibleAsync(
            Guid tenantId,
            SupportTicketId ticketId,
            (DateTimeOffset OccurredAt, Guid MessageId)? before,
            int take,
            CancellationToken cancellationToken) =>
            inner.ListVisibleAsync(tenantId, ticketId, before, take, cancellationToken);

        public void Add(SupportTicketMessage link)
        {
            if (link.MessageId == poison)
            {
                throw new InvalidOperationException("Simulated failure for one inbound message.");
            }

            inner.Add(link);
        }
    }
}
