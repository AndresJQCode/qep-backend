using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Notifications.Application;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El correo de invitación de un mayorista (plan mayorista fase 2, Task 8): el evento <c>wholesale.distributor-invited.v1</c>
/// escrito en el outbox como lo deja Mayorista, el bucle de entrega prendido y un canal que graba lo enviado. El link sale de
/// <c>Notifications:AppBaseUrl</c> (ruling W1).
/// </summary>
public sealed class WholesaleInvitationNotificationTests
{
    private const string EventName = "wholesale.distributor-invited.v1";

    private const string AppBaseUrl = "https://qep.example";

    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task InvitingADistributorDeliversExactlyOneEmailWithTheLink()
    {
        await using var database = await NotificationsDeliveryHarness.StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        await using var factory = NewFactory(database.GetConnectionString(), channel);
        var (tenantId, _, _) = await NotificationsDeliveryHarness.RegisterOwnerAsync(factory);
        var distributorId = Guid.CreateVersion7();
        var email = $"distri-{Guid.NewGuid():N}@example.com";
        const string token = "tok_en-1";

        await NotificationsDeliveryHarness.InsertOutboxAsync(
            database.GetConnectionString(), EventName, Payload(tenantId, distributorId, email, token));

        var row = await NotificationsDeliveryHarness.WaitForNotificationAsync(
            database.GetConnectionString(), distributorId, WholesaleInvitationEmailTemplate.TemplateRef);
        Assert.NotNull(row);
        Assert.Equal(("Sent", email), (row.Status, row.RecipientAddress));
        Assert.Equal(tenantId, await NotificationsDeliveryHarness.TenantIdOfAsync(
            database.GetConnectionString(), distributorId, WholesaleInvitationEmailTemplate.TemplateRef));

        // El inbox del consumidor impide un segundo correo para el mismo mensaje en los ticks siguientes.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        var message = Assert.Single(channel.Sent, sent => sent.ToAddress == email);
        Assert.Equal("Notifications Delivery Org te invita a comprarle en Lulo CRM", message.Subject);
        Assert.Contains($"{AppBaseUrl}/wholesale-invitations/{token}", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("?ev=", message.TextBody, StringComparison.Ordinal);
        Assert.Single(await NotificationsDeliveryHarness.NotificationsForAsync(
            database.GetConnectionString(), distributorId, WholesaleInvitationEmailTemplate.TemplateRef));
    }

    // Review Focus 4: reenviar publica otro evento con otro token; sale un segundo correo con el link nuevo.
    [Fact]
    public async Task AResendDeliversASecondEmailWithTheNewLink()
    {
        await using var database = await NotificationsDeliveryHarness.StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        await using var factory = NewFactory(database.GetConnectionString(), channel);
        var (tenantId, _, _) = await NotificationsDeliveryHarness.RegisterOwnerAsync(factory);
        var distributorId = Guid.CreateVersion7();
        var email = $"distri-{Guid.NewGuid():N}@example.com";

        await NotificationsDeliveryHarness.InsertOutboxAsync(
            database.GetConnectionString(), EventName, Payload(tenantId, distributorId, email, "first-token"));
        await NotificationsDeliveryHarness.InsertOutboxAsync(
            database.GetConnectionString(), EventName, Payload(tenantId, distributorId, email, "second-token"));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && channel.Sent.Count(sent => sent.ToAddress == email) < 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        var links = channel.Sent.Where(sent => sent.ToAddress == email).Select(sent => sent.TextBody).ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, body => body.Contains($"{AppBaseUrl}/wholesale-invitations/first-token", StringComparison.Ordinal));
        Assert.Contains(links, body => body.Contains($"{AppBaseUrl}/wholesale-invitations/second-token", StringComparison.Ordinal));
    }

    // Spec 2026-11-24: la prueba de buzón del payload llega al correo como ?ev= del link.
    [Fact]
    public async Task AnEmailProofInThePayloadGoesInTheLinkAsEv()
    {
        await using var database = await NotificationsDeliveryHarness.StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        await using var factory = NewFactory(database.GetConnectionString(), channel);
        var (tenantId, _, _) = await NotificationsDeliveryHarness.RegisterOwnerAsync(factory);
        var distributorId = Guid.CreateVersion7();
        var email = $"distri-{Guid.NewGuid():N}@example.com";

        await NotificationsDeliveryHarness.InsertOutboxAsync(
            database.GetConnectionString(), EventName,
            Payload(tenantId, distributorId, email, "tok_en-1", emailProof: "prueba-1"));

        var row = await NotificationsDeliveryHarness.WaitForNotificationAsync(
            database.GetConnectionString(), distributorId, WholesaleInvitationEmailTemplate.TemplateRef);
        Assert.NotNull(row);
        Assert.Equal("Sent", row.Status);
        var message = Assert.Single(channel.Sent, sent => sent.ToAddress == email);
        Assert.Contains(
            $"{AppBaseUrl}/wholesale-invitations/tok_en-1?ev=prueba-1", message.TextBody, StringComparison.Ordinal);
    }

    private static NotificationsApiFactory NewFactory(string connectionString, RecordingEmailChannel channel) =>
        new(
            connectionString,
            channel,
            runDeliveryWorkers: true,
            settings: new Dictionary<string, string> { ["Notifications:AppBaseUrl"] = AppBaseUrl });

    // La forma exacta de WholesaleEventPublisher: nombres en minúscula. Sin prueba, el campo no viaja, como en los eventos que ya
    // estaban en el outbox.
    private static string Payload(
        Guid tenantId, Guid distributorId, string email, string token, string? emailProof = null) =>
        JsonSerializer.Serialize(
            new
            {
                tenantId,
                distributorId,
                email,
                businessName = "Distribuciones Norte SAS",
                token,
                emailProof,
                expiresAt = DateTimeOffset.UtcNow.AddHours(72),
            },
            OmitNulls);
}
