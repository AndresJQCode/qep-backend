using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Messaging;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

public sealed class BuyerEmailDeliveryTests
{
    private const string Host = "acme.lulocrm.com";

    [Fact]
    public async Task AVerificationEventSendsOneMailToThePayloadEmail()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.EmailVerificationRequested, BuyerVerificationEmailTemplate.TemplateRef, "tok_1");

        Assert.Equal("Confirma tu cuenta para comprar en Acme", mail.Subject);
        Assert.Contains($"https://{Host}/cuenta/verificar?token=tok_1", mail.TextBody);
    }

    [Fact]
    public async Task AnExistingAccountEventSendsLoginAndForgotLinks()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.ExistingAccountNotice, BuyerExistingAccountEmailTemplate.TemplateRef, null);

        Assert.Equal("Ya tienes una cuenta para comprar en Acme", mail.Subject);
        Assert.Contains($"https://{Host}/cuenta/ingresar", mail.TextBody);
        Assert.Contains($"https://{Host}/cuenta/olvide", mail.TextBody);
    }

    [Fact]
    public async Task AResetEventSendsTheResetLink()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.PasswordResetRequested, BuyerPasswordResetEmailTemplate.TemplateRef, "tok_2");

        Assert.Equal("Restablece tu contraseña", mail.Subject);
        Assert.Contains($"https://{Host}/cuenta/restablecer?token=tok_2", mail.TextBody);
    }

    [Fact]
    public async Task AnAccountDeletedEventSendsTheConfirmation()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        var messageId = await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.AccountDeleted, Payload(accountId, "ana@example.com", null));

        Assert.True(await WaitForProcessedAsync(cs, DeletedConsumer, messageId));
        var mail = Assert.Single(channel.Sent);
        Assert.Equal("ana@example.com", mail.ToAddress);
        Assert.Equal("Eliminamos tu cuenta", mail.Subject);
    }

    // I1: la decisión 9 del spec sólo conserva el registro de la autorización. Una vez enviado el
    // correo de cuenta eliminada, ninguna notificación del comprador guarda su correo ni su cuenta.
    [Fact]
    public async Task AfterTheDeletionMailIsSentNoBuyerNotificationKeepsTheEmailOrTheAccount()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();
        const string email = "ana-deleted@example.com";

        await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.EmailVerificationRequested, Payload(accountId, email, "tok_1"));
        Assert.NotNull(await WaitForNotificationAsync(cs, accountId, BuyerVerificationEmailTemplate.TemplateRef));
        var deletedId = await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.AccountDeleted, Payload(accountId, email, null));

        Assert.True(await WaitForProcessedAsync(cs, DeletedConsumer, deletedId));
        Assert.Equal(2, channel.Sent.Count);
        Assert.Equal(0, await CountRowsMentioningAsync(cs, accountId, email));
        Assert.Equal(2, await CountBuyerRowsAsync(cs));
    }

    // I1 residual A: un envío fallido cierra el mensaje (processed_at, sin reintento) igual que uno
    // exitoso, así que la anonimización tampoco puede depender de que el correo haya salido.
    [Fact]
    public async Task AFailedDeletionMailStillAnonymizesEveryBuyerNotification()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel
        {
            OnSend = call => call == 2
                ? Task.FromException(new InvalidOperationException("provider rejected the message"))
                : Task.CompletedTask,
        };
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();
        const string email = "ana-failed-deletion@example.com";

        await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.EmailVerificationRequested, Payload(accountId, email, "tok_1"));
        Assert.NotNull(await WaitForNotificationAsync(cs, accountId, BuyerVerificationEmailTemplate.TemplateRef));
        var deletedId = await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.AccountDeleted, Payload(accountId, email, null));

        Assert.True(await WaitForProcessedAsync(cs, DeletedConsumer, deletedId));
        Assert.Single(channel.Sent);
        Assert.Equal(0, await CountRowsMentioningAsync(cs, accountId, email));
        Assert.Equal(2, await CountBuyerRowsAsync(cs));
        Assert.Equal(1, await CountFailedRowsAsync(cs, BuyerAccountDeletedEmailTemplate.TemplateRef));
    }

    // I1 residual B2: un correo del comprador que seguía en vuelo cuando se borró la cuenta (el de
    // restablecer, pedido justo antes) no sale, y su fila queda ya anonimizada. Vale si el de cuenta
    // eliminada ya se entregó y también si todavía espera en el outbox.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABuyerEmailOfADeletedAccountIsNotSentAndIsRecordedAnonymized(bool deletionDeliveredFirst)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();
        const string email = "ana-late@example.com";
        var ct = TestContext.Current.CancellationToken;

        var resetId = await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.PasswordResetRequested, Payload(accountId, email, "tok_late"));
        await InsertOutboxAsync(cs, BuyerIntegrationEvents.AccountDeleted, Payload(accountId, email, null));
        if (deletionDeliveredFirst)
        {
            await NewWorker<BuyerAccountDeletedDeliveryWorker>(factory).DrainAsync(ct);
        }

        await NewWorker<BuyerPasswordResetDeliveryWorker>(factory).DrainAsync(ct);

        Assert.NotNull((await FindInboxAsync(cs, ResetConsumer, resetId))?.ProcessedAt);
        Assert.DoesNotContain(channel.Sent, mail => mail.Subject == "Restablece tu contraseña");
        Assert.Equal(0, await CountRowsMentioningAsync(cs, accountId, email));
        var reset = Assert.Single(await NotificationsForAsync(cs, Guid.Empty, BuyerPasswordResetEmailTemplate.TemplateRef));
        Assert.Equal("Failed", reset.Status);
        Assert.Equal("buyer_account_deleted", reset.FailureReason);
    }

    private const string ResetConsumer = "notifications.buyer-password-reset-email";

    private static TWorker NewWorker<TWorker>(NotificationsApiFactory factory)
        where TWorker : OutboxDeliveryWorker =>
        ActivatorUtilities.CreateInstance<TWorker>(factory.Services);

    private static async Task<long> CountFailedRowsAsync(string cs, string templateRef)
    {
        await using var connection = new Npgsql.NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM notifications.notifications WHERE template_ref = @ref AND status = 'Failed'",
            connection);
        command.Parameters.AddWithValue("ref", templateRef);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private const string DeletedConsumer = "notifications.buyer-account-deleted-email";

    private static async Task<bool> WaitForProcessedAsync(string cs, string consumer, Guid messageId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await FindInboxAsync(cs, consumer, messageId))?.ProcessedAt is not null)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<long> CountRowsMentioningAsync(string cs, Guid accountId, string email)
    {
        await using var connection = new Npgsql.NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM notifications.notifications WHERE recipient_id = @id OR recipient_address = @email",
            connection);
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("email", email);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> CountBuyerRowsAsync(string cs)
    {
        await using var connection = new Npgsql.NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM notifications.notifications WHERE template_ref LIKE 'buyers.%'", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task AVerificationWithoutTokenIsRecordedAsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.EmailVerificationRequested, Payload(accountId, "ana@example.com", null));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerVerificationEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("email_token_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task APayloadWithoutEmailIsRecordedAsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        // No el de cuenta eliminada: ése anonimiza su propia fila al cerrar, así que no se encontraría por la cuenta.
        await InsertOutboxAsync(cs, BuyerIntegrationEvents.ExistingAccountNotice, Payload(accountId, string.Empty, null));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerExistingAccountEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("recipient_email_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    [Theory]
    [InlineData("acme\".lulocrm.com")]
    [InlineData("acme lulocrm.com")]
    [InlineData("")]
    public async Task AnInvalidStoreHostIsRecordedAsFailedWithoutSending(string host)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            cs,
            BuyerIntegrationEvents.EmailVerificationRequested,
            Payload(accountId, "ana@example.com", "tok_1", host));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerVerificationEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("store_host_invalid", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task AResetWithoutTokenIsRecordedAsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.PasswordResetRequested, Payload(accountId, "ana@example.com", null));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerPasswordResetEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("email_token_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    private static async Task<EmailMessage> DeliverAsync(string eventName, string templateRef, string? token)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(cs, eventName, Payload(accountId, "ana@example.com", token));

        var row = await WaitForNotificationAsync(cs, accountId, templateRef);
        Assert.Equal("Sent", row!.Status);
        Assert.Equal("ana@example.com", row.RecipientAddress);
        Assert.Null(await TenantIdOfAsync(cs, accountId, templateRef));
        var mail = Assert.Single(channel.Sent);
        Assert.Equal("ana@example.com", mail.ToAddress);
        return mail;
    }

    private static string Payload(Guid accountId, string email, string? token, string host = Host) =>
        JsonSerializer.Serialize(
            new BuyerEmailPayload(accountId, email, host, "Acme", token), JsonSerializerOptions.Web);
}
