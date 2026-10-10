using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §12, «Carga»: N POSTs firmados concurrentes con wamids repetidos → cero 429,
/// un mensaje por wamid, unreadCount exacto, una conversación por waId.</summary>
public sealed class WebhookLoadTests
{
    [Fact]
    public async Task ConcurrentSignedPostsWithRepeatedWamidsNeverGet429AndCountExactly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var client = factory.CreateClient();
        const int Persons = 5;
        const int MessagesPerPerson = 20;
        const int Resends = 3;

        var posts = new List<Task<HttpResponseMessage>>();
        for (var person = 0; person < Persons; person++)
        {
            for (var index = 0; index < MessagesPerPerson; index++)
            {
                for (var resend = 0; resend < Resends; resend++)
                {
                    // Cada reenvío cambia la hora: bytes distintos, mismo wamid.
                    var json = MetaPayloads.InboundText("111", $"57300000000{person}", $"wamid.{person}.{index}", 1760000000 + index * 10 + resend, $"mensaje {index}");
                    posts.Add(PostWebhookAsync(client, json));
                }
            }
        }

        var responses = await Task.WhenAll(posts);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);

        // Varias pasadas del worker en paralelo (dos pods o más): el reclamo deja un solo ganador por entrega.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DrainDeliveriesAsync(factory)));
        while (await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL") > 0)
        {
            await DrainDeliveriesAsync(factory);
        }

        Assert.Equal((long)Persons, await CountAsync(connectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal((long)(Persons * MessagesPerPerson), await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal((long)(Persons * MessagesPerPerson), await CountAsync(connectionString, "SELECT sum(unread_count) FROM messaging.conversations"));
        Assert.Equal((long)MessagesPerPerson, await CountAsync(connectionString, "SELECT min(unread_count)::bigint FROM messaging.conversations"));
    }
}
